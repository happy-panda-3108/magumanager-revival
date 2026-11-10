using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 코칭스태프 조합 (user 2026-11-04: 전력강화 slot 5, left of the 트레이닝북 교환소): the original's Scene_CoachCardCombine
	// (CoachCardCombineMain) with ENTER_COACH_MIX / PROC_COACH_MIX. The rule (user 11-05, CoachCatalog.MixCards): four cards of
	// one grade; four copies of one card → that card one grade up, four of one club lineage → a card of that line one grade up
	// (11-09), else a random card one grade up; three 엘리트 → a 엘리트 of their line when they share one (11-10), else a random 엘리트.
	public static partial class CoachHandlers
	{
		private static void EnterMix(GameSession session, Ma9MPacket.PKS_CS_ENTER_COACH_MIX packet)
		{
			uint user = session.Team.UserCode;
			int[] staff = Staff(user);
			List<int> rest = Coaches(user);
			foreach (int idx in staff.Where(i => i != 0)) rest.Remove(idx);
			Ma9MPacket.PKS_SC_ENTER_COACH_MIX reply = new Ma9MPacket.PKS_SC_ENTER_COACH_MIX();
			reply.error_info.SetNoError();
			reply.CoachCardList.array = rest.Select(i => new PInteger { n = i }).ToArray();
			reply.DeckCoachCount.n = (short)staff.Count(i => i != 0);
			reply.CoachMax.n = CoachCatalog.MaxCoaches;
			reply.CanNotMix_CoachCardList.array = Array.Empty<PInteger>();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 코칭스태프 조합: {rest.Count} cards, {reply.DeckCoachCount.n} on the staff");
		}

		private static void ProcMix(GameSession session, Ma9MPacket.PKS_CS_PROC_COACH_MIX packet)
		{
			uint user = session.Team.UserCode;
			int[] cards = (packet.CoachCardList.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			Ma9MPacket.PKS_SC_PROC_COACH_MIX reply = new Ma9MPacket.PKS_SC_PROC_COACH_MIX();
			string refuse = TryCoachMix(user, cards, s_rng, out int made, out string how);
			if (refuse != null)
			{
				reply.error_info.SetErrorMessage(refuse);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] 코칭스태프 조합 refused: {refuse}");
				return;
			}
			reply.error_info.SetNoError();
			reply.NewCoachCard.n = made;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 코칭스태프 조합 ({how}): {string.Join(", ", cards.Select(Label))} → {Label(made)}");
		}

		// Null = the cards are gone and `made` is held. `how`: "승급" (four copies), "같은 구단" (four of one line), "무작위",
		// "같은 구단 엘리트" or "엘리트 다시 뽑기".
		internal static string TryCoachMix(uint user, int[] cards, Random rng, out int made, out string how)
		{
			made = 0;
			how = null;
			if (cards.Length == 0 || cards.Any(c => !CoachCatalog.IsCoach(c)))
			{
				return $"코치 카드 {CoachCatalog.MixCards}장(엘리트는 {CoachCatalog.EliteMixCards}장)을 넣어 주세요.";
			}
			int grade = CoachCatalog.Grade(cards[0]);
			if (cards.Any(c => CoachCatalog.Grade(c) != grade)) return "같은 등급의 코치 카드만 함께 조합할 수 있습니다.";
			if (cards.Length != CoachCatalog.MixCardsOf(grade))
			{
				return grade == CoachCatalog.Elite ? $"엘리트 코치 카드는 {CoachCatalog.EliteMixCards}장을 넣어 주세요." : $"코치 카드 {CoachCatalog.MixCards}장을 넣어 주세요.";
			}
			// Copies: each card as often as it is held, less the ones on the staff.
			List<int> free = Coaches(user);
			foreach (int idx in Staff(user).Where(i => i != 0)) free.Remove(idx);
			List<int> left = free.ToList();
			foreach (int c in cards)
			{
				if (left.Remove(c)) continue;
				return free.Contains(c) || !Coaches(user).Contains(c) ? "가지고 있지 않은 코치 카드가 들어 있습니다." : "코칭스태프에 등록된 코치는 조합할 수 없습니다.";
			}
			// One lineage and role (user 11-05 19:35: LG 2장 + MBC 2장 count as the same card; the new card's club "넣은 비율대로").
			// One lineage, roles mixed (user 11-09 20:3x D-3: "같은구단 코치 4장으로 상위 등급 같은 구단 랜덤 코치"): that line one
			// grade up, any role. Three 엘리트 of one line (user 11-10 01:5x: "같은 구단 엘리트 코치 3장을 넣으면 같은 구단 랜덤 엘리트
			// 코치 1장"): a 엘리트 of that line. Cards of more than one line: a card of any line, as before.
			int line = CoachCatalog.LineOf(cards[0]), role = CoachCatalog.Role(cards[0]);
			bool oneLine = cards.All(c => CoachCatalog.LineOf(c) == line);
			bool same = oneLine && cards.All(c => CoachCatalog.Role(c) == role);
			int club = CoachCatalog.Club(cards[rng.Next(cards.Length)]);
			if (grade == CoachCatalog.Elite)
			{
				made = oneLine ? CoachCatalog.Roll(rng, CoachCatalog.Elite, club) : CoachCatalog.Roll(rng, CoachCatalog.Elite);
				how = oneLine ? "같은 구단 엘리트" : "엘리트 다시 뽑기";
			}
			else if (same)
			{
				made = CoachCatalog.Roll(rng, grade + 1, club, role);
				how = "승급";
			}
			else if (oneLine)
			{
				made = CoachCatalog.Roll(rng, grade + 1, club);
				how = "같은 구단";
			}
			else
			{
				made = CoachCatalog.Roll(rng, grade + 1);
				how = "무작위";
			}
			RemoveCoaches(user, cards);
			AddCoaches(user, new[] { made });
			return null;
		}

		private static void RemoveCoaches(uint user, IEnumerable<int> coaches)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction t = c.BeginTransaction();
			foreach (int idx in coaches)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = t;
				cmd.CommandText = "DELETE FROM coach_card WHERE id = (SELECT id FROM coach_card WHERE user_code = $u AND coach_idx = $i LIMIT 1)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.ExecuteNonQuery();
			}
			t.Commit();
		}
	}
}
