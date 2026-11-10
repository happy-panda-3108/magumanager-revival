using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 전력강화 > 포지션 변경 (the original 포지션 훈련 screen: Scene_PositionChange, PositionChange_Main), systems session
	// 2026-10-24. User's rules:
	// - Only pitchers who really pitched in another role may change, to those roles (tools/scripts/make_position_roles.py
	//   writes data/tables/ma9m_pitcher_roles.txt); the list shows only them.
	// - 40만 MP a try; 대성공 40% (result 1, purple on the card), else 성공 (result 2, sky blue). No failure.
	// - Once per card: another change needs the 포지션 변경 초기화권 (the client's own card-targeting item 90841, 80만 MP
	//   in the shop), which puts the card back to its own role and stats.
	// - Stats (health, 구속, 컨트롤, 테크닉, 구위, P정신 change by Delta), and the new role is the card's role in games
	//   (a reliever made a starter is out of role in the bullpen).
	// The result is team_card.position_training = (role - 11) * 10 + result, which every card info already carries
	// (PUICardInfo.position_training_info): the client shows the new role and stats from it.
	public static class PositionChangeHandlers
	{
		public const int Fee = 400_000;

		public const int ResetItem = 90841;

		public const byte Great = 1, Success = 2;

		public const double GreatChance = 0.40;

		// 체력 change between the rotation and the bullpen (user, 2026-10-24). To the bullpen: 25, the user's pick after
		// the average starter card's 체력 less the average reliever's came to 22.5 (69.5 − 47.0 over all 3,201 pitcher
		// cards; first value 30). To the rotation: 20, kept.
		public const int ToBullpenHealth = 25, ToStarterHealth = 20;

		// The lobby menu (POWERUP_POSITIONCHANGE), shown only to builds that add this mark to their login's bundle_version:
		// an older app computes a changed card's stats from the original factor table, which it lacks, and fails.
		public const byte Menu = 15;

		public const string ClientMark = "ma9m-pos1";

		public static bool ClientSupports(GameSession session) => session?.ClientVersion?.Contains(ClientMark) == true;

		private static readonly Dictionary<int, byte[]> s_roles = new Dictionary<int, byte[]>();

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static string s_cs;

		private static readonly Random s_rng = new Random();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			s_roles.Clear();
			// ma9m_pitcher_roles.txt from the season cards (make_position_roles.py), then ma9m_legend_roles.txt for the 레전드
			// (user, 2026-11-10 07:5x: "김용수 선발, 선동열, 최동원의 마무리 가능 등 포지션 변경이 가능하게끔 … 실제로 뛰었던 포지션들에";
			// the engine session's legend pipeline writes it from their career records): the same columns, idx, own role, roles
			// allowed; the change itself follows the same rules (Delta).
			int legends = 0;
			foreach (string name in new[] { "ma9m_pitcher_roles.txt", "ma9m_legend_roles.txt" })
			{
				string path = Path.Combine(dataDir, "tables", name);
				if (!File.Exists(path)) continue;
				foreach (string line in File.ReadLines(path).Skip(1))
				{
					string[] c = line.Split('\t');
					if (c.Length >= 3 && int.TryParse(c[0], out int idx) && c[2].Trim().Length > 0)
					{
						s_roles[idx] = c[2].Split(',').Select(r => byte.Parse(r.Trim())).ToArray();
						if (name.StartsWith("ma9m_legend")) legends++;
					}
				}
			}
			ServerLog.Info($"포지션 변경: {s_roles.Count} pitcher cards may change role ({legends} 레전드)");
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_PITCHER_POSITION_TRAINING>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_TRY_PITCHER_POSITION_TRAINING>(Try);
			loop.Register<Ma9MPacket.PKS_CS_USE_ITEM_GET_TARGET_CARD_LIST>(Targets);
			loop.Register<Ma9MPacket.PKS_CS_USE_CARD_TARGETING_ITEM>(UseTargeting);
		}

		public static IEnumerable<ItemDef> Items()
		{
			yield return new ItemDef
			{
				Index = ResetItem, Name = "포지션 변경 초기화권", Type = ItemType.Common, TypeText = "포지션 변경 아이템", File = "pos_reset",
				Desc = "포지션을 바꾼 투수 한 명을 원래 보직과 능력치로 되돌립니다. 그 뒤 다시 포지션 변경을 할 수 있습니다."
			};
		}

		// The roles a card may change to (12 SP … 15 CP), none when it may not.
		public static byte[] AllowedRoles(int idx) => s_roles.TryGetValue(idx, out byte[] roles) ? roles : Array.Empty<byte>();

		// For the games: the role a team card plays (ePos 12-15) after a change, its own role when none.
		public static int RoleOf(CardInfo card, byte positionTraining) => positionTraining % 10 != 0 ? positionTraining / 10 + 11 : (int)card.MainPosition;

		// The stat change (health, 구속, 컨트롤, 테크닉, 구위, P정신) of a change from the card's own role (user, 2026-10-24):
		// starter to bullpen 성공 -25/+1/-1/-1/-1/-1, 대성공 -25/+2/+1/+1/+1/+1; within the bullpen 성공 0/-1/-1/-1/-1/-1,
		// 대성공 0/0/+1/+1/+1/0; bullpen to starter 성공 +20/-2/-1/-1/-1/-1, 대성공 +20/-1/+1/+1/+1/+1 (체력 from
		// ToBullpenHealth / ToStarterHealth). The client's TRRow_PitcherCard.GetPositionTrainedInfo shows the same.
		public static sbyte[] Delta(int ownRole, byte positionTraining)
		{
			if (positionTraining % 10 == 0)
			{
				return new sbyte[6];
			}
			int to = positionTraining / 10 + 11;
			bool great = positionTraining % 10 == Great;
			bool fromStarter = ownRole == (int)CardPos.SP, toStarter = to == (int)CardPos.SP;
			if (fromStarter && !toStarter)
			{
				sbyte health = -ToBullpenHealth;
				return great ? new sbyte[] { health, 2, 1, 1, 1, 1 } : new sbyte[] { health, 1, -1, -1, -1, -1 };
			}
			if (!fromStarter && toStarter)
			{
				sbyte health = ToStarterHealth;
				return great ? new sbyte[] { health, -1, 1, 1, 1, 1 } : new sbyte[] { health, -2, -1, -1, -1, -1 };
			}
			return great ? new sbyte[] { 0, 0, 1, 1, 1, 0 } : new sbyte[] { 0, -1, -1, -1, -1, -1 };
		}

		// For the games (engine session, 2026-10-24): a team card's change by card number (TeamStrength.AddBonuses), and
		// its role as CardInfo.PreferredOrder counts pitchers (0 선발, 1 중계, 2 셋업, 3 마무리; LineupEffects).
		public static sbyte[] DeltaOf(int idx, byte positionTraining) =>
			positionTraining % 10 == 0 || s_catalog?.Get(idx) is not CardInfo card || card.IsBatter ? new sbyte[6] : Delta((int)card.MainPosition, positionTraining);

		public static int OrderOf(CardInfo card, byte positionTraining) =>
			positionTraining % 10 == 0 || card.IsBatter ? card.PreferredOrder : RoleOf(card, positionTraining) - (int)CardPos.SP;

		private static PUICardInfo Info(uint user, TeamCard card, DateTime now) =>
			new PUICardInfo(card.Idx, Condition.For(user, card.Idx, now), card.RemainContract, card.PositionTrainingInfo, (byte)(card.IsLock ? 2 : 1));

		private static HashSet<int> Lineup(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			return l == null ? new HashSet<int>() : l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_PITCHER_POSITION_TRAINING packet)
		{
			if (session.Team == null)
			{
				return;
			}
			session.Send(EnterReply(session.Team.UserCode, DateTime.Now));
		}

		// Only the pitchers who may change (user: "포지션 변경이 가능한 선수만 보이게"); one already changed stays listed
		// with its new role (the screen says it cannot change again). The slot counter reads changed / listed.
		internal static Ma9MPacket.PKS_SC_ENTER_PITCHER_POSITION_TRAINING EnterReply(uint user, DateTime now)
		{
			List<TeamCard> cards = s_db.GetCards(user).Where(c => s_roles.ContainsKey(c.Idx)).ToList();
			Ma9MPacket.PKS_SC_ENTER_PITCHER_POSITION_TRAINING reply = new Ma9MPacket.PKS_SC_ENTER_PITCHER_POSITION_TRAINING();
			reply.players.array = cards.Select(c => new PUICardInfoPositionTraining
			{
				ui_info = Info(user, c, now),
				possible_positions = new PByteArray { bytes = AllowedRoles(c.Idx) },
				training_fee = new PInteger { n = Fee }
			}).ToArray();
			HashSet<int> lineup = Lineup(user);
			reply.players_in_roster.array = cards.Where(c => lineup.Contains(c.Idx)).Select(c => new PInteger { n = c.Idx }).ToArray();
			HashSet<int> lessons = LessonHandlers.InLesson(user);
			reply.players_in_lesson.array = cards.Where(c => lessons.Contains(c.Idx)).Select(c => new PInteger { n = c.Idx }).ToArray();
			reply.max_slot.n = (byte)Math.Min(255, cards.Count);
			reply.used_slot.n = (byte)Math.Min(255, cards.Count(c => c.PositionTrainingInfo % 10 != 0));
			reply.free_item_amount.n = 0;
			reply.item_amount.n = InventoryHandlers.ItemAmount(user, ResetItem);
			return reply;
		}

		private static void Try(GameSession session, Ma9MPacket.PKS_CS_TRY_PITCHER_POSITION_TRAINING packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_TRY_PITCHER_POSITION_TRAINING reply = new Ma9MPacket.PKS_SC_TRY_PITCHER_POSITION_TRAINING();
			reply.error_info.SetNoError();
			string refuse = TryChange(user, packet.target_index.n, packet.target_position.n, out byte result);
			if (refuse != null)
			{
				reply.error_info.SetError(refuse);
			}
			reply.result.n = result;
			reply.my_point.n = session.Team.Mp = s_db.AddMp(user, 0);
			reply.free_item_amount.n = 0;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 포지션 변경 card {packet.target_index.n} → {packet.target_position.n}: {refuse ?? (result == Great ? "대성공" : "성공")}");
			if (refuse == null)
			{
				TopBar.Send(session);
			}
		}

		// The refusal's Korean.txt key (PosChange_…), or null with the result stored and the fee paid.
		internal static string TryChange(uint user, int idx, int target, out byte result)
		{
			result = 0;
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			string refuse =
				card == null || !AllowedRoles(idx).Contains((byte)target) ? "PosChange_InvalidPos" :
				card.PositionTrainingInfo % 10 != 0 ? "PosChange_AlreadyTrained" :
				LessonHandlers.InLesson(user, idx) ? "PosChange_InLessonFail" :
				s_db.AddMp(user, 0) < Fee ? "PosChange_LessMP" : null;
			if (refuse != null)
			{
				return refuse;
			}
			result = s_rng.NextDouble() < GreatChance ? Great : Success;
			s_db.AddMp(user, -Fee);
			Save(user, idx, (byte)((target - 11) * 10 + result));
			return null;
		}

		private static void Save(uint user, int idx, byte value)
		{
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "UPDATE team_card SET position_training = $v WHERE user_code = $u AND card_idx = $i";
			cmd.Parameters.AddWithValue("$v", (int)value);
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			cmd.ExecuteNonQuery();
		}

		// The card-targeting items' card lists (the inventory and this screen's 초기화 button): for the 초기화권 the
		// changed pitchers; nothing for the other items of that kind.
		private static void Targets(GameSession session, Ma9MPacket.PKS_CS_USE_ITEM_GET_TARGET_CARD_LIST packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			DateTime now = DateTime.Now;
			Ma9MPacket.PKS_SC_USE_ITEM_GET_TARGET_CARD_LIST reply = new Ma9MPacket.PKS_SC_USE_ITEM_GET_TARGET_CARD_LIST();
			reply.target_item.n = packet.target_item.n;
			reply.card_list.array = packet.target_item.n == ResetItem
				? s_db.GetCards(user).Where(c => c.PositionTrainingInfo % 10 != 0).Select(c => Info(user, c, now)).ToArray()
				: Array.Empty<PUICardInfo>();
			session.Send(reply);
		}

		private static void UseTargeting(GameSession session, Ma9MPacket.PKS_CS_USE_CARD_TARGETING_ITEM packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			bool ok = packet.item_index.n == ResetItem && TryReset(user, packet.target_card.n);
			Ma9MPacket.PKS_SC_USE_CARD_TARGETING_ITEM reply = new Ma9MPacket.PKS_SC_USE_CARD_TARGETING_ITEM();
			reply.result.n = (byte)(ok ? 0 : 1);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 포지션 변경 초기화권 card {packet.target_card.n}: {(ok ? "ok" : "refused")}");
		}

		internal static bool TryReset(uint user, int idx)
		{
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			if (card == null || card.PositionTrainingInfo % 10 == 0 || InventoryHandlers.ItemAmount(user, ResetItem) < 1)
			{
				return false;
			}
			InventoryHandlers.AddItem(user, ResetItem, -1);
			Save(user, idx, 0);
			return true;
		}

		// Dev check: `Ma9MServer.dll <copy of data dir> --positiontest` (autotest's team; writes to that DB).
		public static int SelfTest(Database db)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok)
				{
					problems++;
				}
				ServerLog.Info($"[positiontest] {(ok ? "ok  " : "FAIL")} {what}");
			}
			Check(s_roles.Count > 1000, $"role table: {s_roles.Count} cards");
			Check(Delta(12, 22).SequenceEqual(new sbyte[] { -25, 1, -1, -1, -1, -1 }) && Delta(12, 21).SequenceEqual(new sbyte[] { -25, 2, 1, 1, 1, 1 })
				&& Delta(13, 32).SequenceEqual(new sbyte[] { 0, -1, -1, -1, -1, -1 }) && Delta(13, 31).SequenceEqual(new sbyte[] { 0, 0, 1, 1, 1, 0 })
				&& Delta(15, 12).SequenceEqual(new sbyte[] { 20, -2, -1, -1, -1, -1 }) && Delta(15, 11).SequenceEqual(new sbyte[] { 20, -1, 1, 1, 1, 1 })
				&& Delta(12, 0).All(v => v == 0), "stat changes: SP→RP, RP→SU, CP→SP (성공, 대성공)");
			int greats = Enumerable.Range(0, 100_000).Count(_ => s_rng.NextDouble() < GreatChance);
			Check(Math.Abs(greats / 1000.0 - 40) < 1, $"대성공 {greats / 1000.0:F1}%");
			uint user;
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
			}
			if (user == 0)
			{
				Check(false, "no test team");
				return problems;
			}
			// A reliever who may become a set-up man, not owned yet: added to the test team.
			HashSet<int> owned = db.GetCards(user).Select(c => c.Idx).ToHashSet();
			int pick = s_roles.Where(kv => kv.Value.Contains((byte)CardPos.SU) && s_catalog.Get(kv.Key)?.MainPosition == CardPos.RP && !owned.Contains(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
			int notAllowed = s_catalog.All.Where(ci => !ci.IsBatter && !s_roles.ContainsKey(ci.Idx) && !owned.Contains(ci.Idx)).Select(ci => ci.Idx).FirstOrDefault();
			db.AddCards(user, new[] { pick, notAllowed }, RosterHandlers.StartContract);
			db.AddMp(user, 2_000_000);
			Ma9MPacket.PKS_SC_ENTER_PITCHER_POSITION_TRAINING list = EnterReply(user, DateTime.Now);
			bool listed = list.players.array.Any(p => p.ui_info.index.n == pick) && list.players.array.All(p => s_roles.ContainsKey(p.ui_info.index.n))
				&& !list.players.array.Any(p => p.ui_info.index.n == notAllowed);
			Check(listed, $"list: {list.players.array.Length} pitchers, all allowed; {s_catalog.Get(pick)?.Name} in, {s_catalog.Get(notAllowed)?.Name} out");
			int mp0 = db.AddMp(user, 0);
			string wrong = TryChange(user, pick, (int)CardPos.CP, out _);
			string ok = TryChange(user, pick, (int)CardPos.SU, out byte result);
			TeamCard after = db.GetCards(user).First(c => c.Idx == pick);
			string again = TryChange(user, pick, (int)CardPos.SU, out _);
			Check(wrong == "PosChange_InvalidPos" && ok == null && (result == Great || result == Success) && after.PositionTrainingInfo == 30 + result
				&& db.AddMp(user, 0) == mp0 - Fee && again == "PosChange_AlreadyTrained" && RoleOf(s_catalog.Get(pick), after.PositionTrainingInfo) == (int)CardPos.SU,
				$"change to SU: {(result == Great ? "대성공" : "성공")} ({after.PositionTrainingInfo}), -{Fee:N0} MP; CP refused ({wrong}); again refused ({again})");
			InventoryHandlers.AddItem(user, ResetItem, 1 - InventoryHandlers.ItemAmount(user, ResetItem));
			bool reset = TryReset(user, pick);
			bool resetAgain = TryReset(user, pick);
			Check(reset && !resetAgain && db.GetCards(user).First(c => c.Idx == pick).PositionTrainingInfo == 0 && InventoryHandlers.ItemAmount(user, ResetItem) == 0
				&& TryChange(user, pick, (int)CardPos.SU, out _) == null, "초기화권: back to its own role, ticket used, may change again");
			// 레전드 (ma9m_legend_roles.txt, 2026-11-10): one that may change is listed and changes as any card does (CP → SP:
			// 체력 +20). Skipped while the file is not there yet.
			int legend = s_roles.Keys.Where(i => s_catalog.Get(i)?.IsLegend == true && !owned.Contains(i)).OrderBy(i => i).FirstOrDefault();
			if (legend != 0)
			{
				CardInfo info = s_catalog.Get(legend);
				byte to = s_roles[legend][0];
				db.AddCards(user, new[] { legend }, RosterHandlers.StartContract);
				db.AddMp(user, Fee);
				bool shown = EnterReply(user, DateTime.Now).players.array.Any(p => p.ui_info.index.n == legend);
				string changed = TryChange(user, legend, to, out byte legendResult);
				TeamCard legendCard = db.GetCards(user).First(c => c.Idx == legend);
				Check(shown && changed == null && RoleOf(info, legendCard.PositionTrainingInfo) == to
					&& DeltaOf(legend, legendCard.PositionTrainingInfo).SequenceEqual(Delta((int)info.MainPosition, legendCard.PositionTrainingInfo)),
					$"레전드 {info.Name} ({info.MainPosition} → {to}, {s_roles.Keys.Count(i => s_catalog.Get(i)?.IsLegend == true)} 레전드 in the table): listed, "
					+ $"{(legendResult == Great ? "대성공" : "성공")}, 체력 {DeltaOf(legend, legendCard.PositionTrainingInfo)[0]:+0;-0}");
			}
			else
			{
				ServerLog.Info("[positiontest] (no 레전드 in the role tables yet)");
			}
			return problems;
		}
	}
}
