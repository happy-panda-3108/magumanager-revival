using System;
using System.Collections.Generic;
using Ma9MPackets;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// 코칭스태프 in 트레이닝 (TrainingHandlers), user 2026-11-04:
	// - 타격코치 (batters) / 투수코치 (pitchers) of the card's club: "원작처럼 트레이닝 중 확률적으로 등장해서 SP 50을 사용하면 결과를 한
	//   단계 올려주는 느낌", 노말/스페셜/레어/엘리트 20/30/40/50%. The client's training result shows him (encounter_coach), asks
	//   to spend the SP and sends TRY_COACH_TRAINING; the slot's result goes one square up. He does not come when the team
	//   has under 50 SP. On the book's best result he takes it one past it (user 11-04: "+4 … SP소모하고 나면 +5").
	//   Not on the lowest square (2026-11-12).
	// - 트레이닝코치: the result odds of his club's cards (23/24/26/27 … 20/21/23/36 since 2026-11-12), and in 트레이닝북 조합 III books into a
	//   IV (노말 5 … 엘리트 2 books, any club: books have none).
	public static partial class CoachHandlers
	{
		// Coach trainings offered and not taken yet: transaction id → the training.
		private static readonly Dictionary<int, (uint user, int card, int slot, int item, int result, DateTime at)> s_offers = new Dictionary<int, (uint, int, int, int, int, DateTime)>();

		private static int s_nextOffer;

		// The four result squares' odds for this card, or null for the usual 25% each.
		public static int[] ResultOdds(uint user, CardInfo info)
		{
			if (!CoachCatalog.Enabled || s_cs == null || info == null) return null;
			int coach = Staff(user)[CoachCatalog.SC];
			return coach != 0 && CoachCatalog.Serves(coach, info.TeamCode) ? CoachCatalog.ResultOdds[CoachCatalog.Grade(coach)] : null;
		}

		public static int RollResult(uint user, CardInfo info, Random rng)
		{
			int[] odds = ResultOdds(user, info);
			if (odds == null) return TrainingBooks.RollResult(rng);
			int u = rng.Next(100);
			for (int r = 0; r < odds.Length - 1; r++)
			{
				if (u < odds[r]) return r;
				u -= odds[r];
			}
			return odds.Length - 1;
		}

		// III books one IV takes (0 = no 트레이닝코치).
		public static int TopMixBooks(uint user)
		{
			if (!CoachCatalog.Enabled || s_cs == null) return 0;
			int coach = Staff(user)[CoachCatalog.SC];
			return coach == 0 ? 0 : CoachCatalog.TopMixBooks[CoachCatalog.Grade(coach)];
		}

		// A training just made (TrainingHandlers.Try): maybe the coach comes (encounter_coach, transaction_id, his line).
		public static void OfferTraining(uint user, CardInfo info, int cardIdx, int slot, int item, int result, Ma9MPacket.PKS_SC_TRY_CARD_TRAINING reply, Random rng = null)
		{
			if (!CoachCatalog.Enabled || s_cs == null || info == null) return;
			rng ??= s_rng;
			int coach = Staff(user)[info.IsBatter ? CoachCatalog.HC : CoachCatalog.PC];
			if (coach == 0 || !CoachCatalog.Serves(coach, info.TeamCode)) return;
			// Not on the lowest square (user 2026-11-12: "트레이닝하다가 젤 낮은 단계에서 코치가 등장하는거 살짝 역겨운데 그 때는 코치가 등장하지
			// 않는걸로 합시다").
			if (result == 0 || result > TopResult) return;
			if (Sp(user) < CoachCatalog.TrainingSp) return;
			// 최고 칸 보장 (user 2026-11-13 01:5x: "코치 … 5뜰때는 안 나오고 4뜰때만 쳐 나와서 열받게 해" → "3, 4 에서도 코치는 나오는데 5에서
			// 코치가 안 나오면 다음 5때 반드시 코치가 나오게"): a top square he missed by the roll makes the next top square of the same
			// role (타격코치 / 투수코치) certain; coach_pity keeps it across restarts. Squares 1-2 roll as before and leave it alone.
			int role = info.IsBatter ? CoachCatalog.HC : CoachCatalog.PC;
			bool owed = result == TopResult && Owed(user, role);
			if (!CoachCatalog.AlwaysTraining && !owed && rng.Next(100) >= CoachCatalog.TrainingChance[CoachCatalog.Grade(coach)])
			{
				if (result == TopResult) SetOwed(user, role, true);
				return;
			}
			if (owed) SetOwed(user, role, false);
			int id = ++s_nextOffer;
			s_offers[id] = (user, cardIdx, slot, item, result, DateTime.Now);
			reply.encounter_coach.n = 1;
			reply.transaction_id.n = id;
			reply.CoachMent_Encounter.n = (short)(info.IsBatter ? CoachCatalog.HitterMeet + rng.Next(CoachCatalog.HitterMeetLines) : CoachCatalog.PitcherMeet + rng.Next(CoachCatalog.PitcherMeetLines));
			ServerLog.Info($"코칭스태프: {Label(coach)} came to {info.Year} {info.Name}'s training (offer {id}{(owed ? ", 최고 칸 보장" : string.Empty)})");
		}

		// The book's top square (TrainingStore results 0-3).
		internal const int TopResult = 3;

		// 최고 칸 보장: whether the next top square of this role is owed a coach.
		internal static bool Owed(uint user, int role)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT owed FROM coach_pity WHERE user_code = $u AND role = $r";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$r", role);
			return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) != 0;
		}

		internal static void SetOwed(uint user, int role, bool owed)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT INTO coach_pity (user_code, role, owed) VALUES ($u, $r, $o) ON CONFLICT (user_code, role) DO UPDATE SET owed = $o";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$r", role);
			cmd.Parameters.AddWithValue("$o", owed ? 1 : 0);
			cmd.ExecuteNonQuery();
		}

		private static void TryCoachTraining(GameSession session, Ma9MPacket.PKS_CS_TRY_COACH_TRAINING packet)
		{
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_TRY_COACH_TRAINING reply = new Ma9MPacket.PKS_SC_TRY_COACH_TRAINING();
			string refuse = TryTake(user, packet.transaction_id.n, out int slot, out bool batter, out string what);
			reply.training_slot.n = slot;
			if (refuse != null)
			{
				reply.result.n = 1;
				reply.error_info.SetErrorMessage(refuse);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] 코치 트레이닝 refused: {refuse}");
				return;
			}
			reply.result.n = 2;
			reply.CoachMent_Result.n = (short)(batter ? CoachCatalog.HitterDone + s_rng.Next(CoachCatalog.HitterDoneLines) : CoachCatalog.PitcherDone + s_rng.Next(CoachCatalog.PitcherDoneLines));
			reply.error_info.SetNoError();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 코치 트레이닝: {what}, SP {Sp(user)}");
		}

		// Null = the slot's result is one square up and the SP paid.
		internal static string TryTake(uint user, int id, out int slot, out bool batter, out string what)
		{
			slot = 0;
			batter = true;
			what = null;
			if (!s_offers.TryGetValue(id, out var offer) || offer.user != user) return "코치가 이미 돌아갔습니다.";
			s_offers.Remove(id);
			slot = offer.slot;
			CardInfo info = s_catalog.Get(offer.card);
			batter = info?.IsBatter != false;
			(int item, int result)[] slots = TrainingHandlers.SlotsOf(user, offer.card);
			if (slots == null || offer.slot >= slots.Length || slots[offer.slot] != (offer.item, offer.result)) return "그 사이 트레이닝 결과가 바뀌었습니다.";
			if (Sp(user) < CoachCatalog.TrainingSp) return "보유한 SP가 부족합니다.";
			AddSp(user, -CoachCatalog.TrainingSp);
			sbyte[] training = TrainingHandlers.SetSlot(user, offer.card, offer.slot, offer.item, offer.result + 1);
			int value = TrainingBooks.Value(offer.item, offer.result + 1);
			what = $"{info?.Year} {info?.Name} slot {offer.slot + 1}: {TrainingBooks.Name(offer.item)} {value - 1:+0;-0;0} → {value:+0;-0;0}, training [{string.Join(" ", training ?? Array.Empty<sbyte>())}]";
			return null;
		}
	}
}
