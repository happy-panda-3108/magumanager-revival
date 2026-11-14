using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 전력강화 > 코칭스태프 (the original's Scene_CoachingStaff, CoachingStaff_Main), systems session 2026-11-04. Cards and
	// numbers in CoachCatalog; the 면담실 in CoachConsult.cs, the training coaches in CoachTraining.cs.
	// - Five slots, one coach of the slot's role each (APPLY_COACH_CARD). A team may hold a card more than once (user 11-05),
	//   100 cards in all (CoachCatalog.MaxCoaches).
	// - Staff Point: +50 at the day's first lobby, +30 a league win (paid with the game's MP), at most 2,000.
	// - 치어리더: league-game MP +3/6/10/15% (shown as the result's "(+n)"), and every day a 고급 컨디션 회복제 and an MP주머니
	//   (user: "매일 출석과 동시에 치어리더의 선물"): at the day's first lobby, or from the screen's 선물받기 when the 치어리더 came later.
	// - 육성스탯 (the cards' bonus points, played like 트레이닝 and 특강): a 레어 / 엘리트 타격·투수코치's own stat for its club's
	//   batters / pitchers, and all five of one club (user: "코칭스태프 구단이 일치되는 순간부터, 스페셜 이상은 해당 구단에게 육성스탯
	//   + 1") +1/+2/+3 on every stat of that club's cards by the lowest of the five grades. The client shows them in the
	//   cards' bingogame_stat (counted into every stat it draws); TeamStrength.Bonus adds TeamCard.Coach.
	// Off unless MA9M_COACH=1, and the menu only for builds with the mark below.
	public static partial class CoachHandlers
	{
		public const byte Menu = 23;  // POWERUP_COACH_MAIN

		public const string ClientMark = "ma9m-coach1";

		public static bool MenuOn(GameSession session) => CoachCatalog.Enabled && session?.ClientVersion?.Contains(ClientMark) == true;

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static string s_cs;

		private static readonly Random s_rng = new Random();

		// The five slots (coach idx, 0 = empty) by team.
		private static readonly Dictionary<uint, int[]> s_staff = new Dictionary<uint, int[]>();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			if (!CoachCatalog.Enabled)
			{
				return;
			}
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS coach_card (id INTEGER PRIMARY KEY AUTOINCREMENT, user_code INTEGER NOT NULL, coach_idx INTEGER NOT NULL, acquired TEXT NOT NULL);
					CREATE INDEX IF NOT EXISTS coach_card_user ON coach_card (user_code);
					CREATE TABLE IF NOT EXISTS coach_state (user_code INTEGER PRIMARY KEY, staff TEXT NOT NULL DEFAULT '0,0,0,0,0', sp INTEGER NOT NULL DEFAULT 0, sp_day TEXT, gift_day TEXT);
					CREATE TABLE IF NOT EXISTS coach_consult (user_code INTEGER NOT NULL, sitting TEXT NOT NULL, card_idx INTEGER NOT NULL, status INTEGER NOT NULL,
						slump INTEGER NOT NULL, row_index INTEGER NOT NULL DEFAULT 0, steps INTEGER NOT NULL DEFAULT 0, until TEXT, PRIMARY KEY (user_code, sitting, card_idx));
					CREATE TABLE IF NOT EXISTS coach_cheer (user_code INTEGER NOT NULL, season INTEGER NOT NULL, game INTEGER NOT NULL, mp INTEGER NOT NULL,
						paid INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (user_code, season, game));
					CREATE TABLE IF NOT EXISTS coach_pity (user_code INTEGER NOT NULL, role INTEGER NOT NULL, owed INTEGER NOT NULL, PRIMARY KEY (user_code, role));";
				cmd.ExecuteNonQuery();
			}
			if (CoachCatalog.TestMode)
			{
				// Test servers: 면담실 visitors made 나쁨/매우 나쁨 (CoachConsult TestLow).
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS coach_testlow (user_code INTEGER NOT NULL, card_idx INTEGER NOT NULL, level INTEGER NOT NULL, from_at TEXT NOT NULL, until TEXT NOT NULL, PRIMARY KEY (user_code, card_idx))";
				cmd.ExecuteNonQuery();
			}
			ResetOnce();
			OldClubsOnce();
			RerollFix1114();
			CoachCatalog.WriteTables(Path.Combine(dataDir, "tables"));
			ServerLog.Info($"코칭스태프: on{(CoachCatalog.TestMode ? " (test mode: packs and SP topped up)" : string.Empty)}, 타격·투수코치 +1 even on the book's best, {CoachCatalog.All().Count()} cards");
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_COACH_MAIN>(EnterMain);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_COACH_CARD>(Apply);
			loop.Register<Ma9MPacket.PKS_CS_GET_TODAY_CLITEM>(Gift);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_CONSULTING_ROOM>(EnterRoom);
			loop.Register<Ma9MPacket.PKS_CS_CONSULTING_START>(StartConsult);
			loop.Register<Ma9MPacket.PKS_CS_CONSULTING_END>(EndConsult);
			loop.Register<Ma9MPacket.PKS_CS_TRY_COACH_TRAINING>(TryCoachTraining);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_COACH_MIX>(EnterMix);
			loop.Register<Ma9MPacket.PKS_CS_PROC_COACH_MIX>(ProcMix);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// 2026-11-14 22:25 (user, SKY마토 user 7: "롯데 타격코치 3명 넣었는데, 각각 정신, 정신, 스피드였음 … 왜 다시 스피드가 나온 것인지??
		// … 컨택 타격코치로 변경해줄 것"): the 능력치 다시 뽑기 then drew from all five stats (fixed in RollOtherStat). Once, at the next
		// start (the staff is cached while the server runs), that card — 롯데 엘리트 타격코치 스피드 9072433, coach_card id 10052 — becomes
		// the 컨택 one (9072411), on the staff too, with a notice.
		private static void RerollFix1114()
		{
			const uint User = 7;
			const long Row = 10052;
			const int From = 9072433, To = 9072411;
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM coach_fix WHERE name = 'reroll_fix_1114'";
			if ((long)cmd.ExecuteScalar() > 0) return;
			using SqliteTransaction t = c.BeginTransaction();
			cmd.Transaction = t;
			cmd.CommandText = "UPDATE coach_card SET coach_idx = $to WHERE id = $row AND user_code = $u AND coach_idx = $from";
			cmd.Parameters.AddWithValue("$to", To);
			cmd.Parameters.AddWithValue("$row", Row);
			cmd.Parameters.AddWithValue("$u", (long)User);
			cmd.Parameters.AddWithValue("$from", From);
			int cards = cmd.ExecuteNonQuery();
			int staff = 0;
			if (cards == 1)
			{
				cmd.Parameters.Clear();
				cmd.CommandText = "SELECT staff FROM coach_state WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)User);
				string s = cmd.ExecuteScalar() as string;
				if (s != null)
				{
					string[] slots = s.Split(',');
					int k = Array.IndexOf(slots, From.ToString());
					if (k >= 0)
					{
						slots[k] = To.ToString();
						cmd.CommandText = "UPDATE coach_state SET staff = $s WHERE user_code = $u";
						cmd.Parameters.AddWithValue("$s", string.Join(",", slots));
						staff = cmd.ExecuteNonQuery();
					}
				}
				cmd.Parameters.Clear();
				// A notice already seen (the 11-05 reset's) is replaced, not shown again; one still waiting gets this added.
				cmd.CommandText = @"INSERT INTO coach_notice (user_code, title, text, shown) VALUES ($u, $t, $x, 0)
					ON CONFLICT(user_code) DO UPDATE SET title = excluded.title,
					text = CASE WHEN shown = 0 THEN text || char(10) || char(10) || excluded.text ELSE excluded.text END, shown = 0";
				cmd.Parameters.AddWithValue("$u", (long)User);
				cmd.Parameters.AddWithValue("$t", "코치 카드 정정");
				cmd.Parameters.AddWithValue("$x", "코치 조합(능력치 다시 뽑기)에서 넣은 카드의\n능력치가 다시 나오던 문제를 고쳤습니다.\n롯데 엘리트 타격코치: 스피드 → 컨택");
				cmd.ExecuteNonQuery();
			}
			cmd.Parameters.Clear();
			cmd.CommandText = "INSERT INTO coach_fix (name, done) VALUES ('reroll_fix_1114', $d)";
			cmd.Parameters.AddWithValue("$d", DateTime.Now.ToString("s"));
			cmd.ExecuteNonQuery();
			t.Commit();
			s_staff.Clear();
			ServerLog.Info($"코치 카드 정정(11-14): user {User} {From} → {To} ({cards} card, {staff} staff slot)");
		}

		// The lineage change (user, 2026-11-05 19:25: "코치카드를 전부 회수해서 그 비용을 다시 돌려주고 처음부터 다시 까도록
		// 만듭시다. LG 계정에서 MBC 삭제하거나 현대에서 태평양을 삭제한 이력이 있었음"): once, every coach card is taken back and
		// the staff emptied, and the packs bought are paid back at the pack price. The packs per team are the live server's
		// shop log from 01:40 (the shop's first coach pack) to 19:27; every one had been opened. SP and books stay.
		private static readonly (uint user, int packs)[] ResetPacks = { (7, 105), (9, 130), (10, 60), (11, 150), (14, 80), (16, 20) };

		private static void ResetOnce()
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS coach_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL);
				CREATE TABLE IF NOT EXISTS coach_notice (user_code INTEGER PRIMARY KEY, title TEXT NOT NULL, text TEXT NOT NULL, shown INTEGER NOT NULL DEFAULT 0)";
			cmd.ExecuteNonQuery();
			cmd.CommandText = "SELECT COUNT(*) FROM coach_fix WHERE name = 'lineage_reset_1105'";
			if ((long)cmd.ExecuteScalar() > 0) return;
			Dictionary<uint, int> packs = ResetPacks.ToDictionary(p => p.user, p => p.packs);
			Dictionary<uint, int> cards = new Dictionary<uint, int>();
			cmd.CommandText = "SELECT user_code, COUNT(*) FROM coach_card GROUP BY user_code";
			using (SqliteDataReader r = cmd.ExecuteReader())
			{
				while (r.Read()) cards[(uint)r.GetInt64(0)] = r.GetInt32(1);
			}
			cmd.CommandText = "DELETE FROM coach_card; UPDATE coach_state SET staff = '0,0,0,0,0'";
			cmd.ExecuteNonQuery();
			foreach (uint user in cards.Keys.Union(packs.Keys))
			{
				int n = cards.GetValueOrDefault(user), bought = packs.GetValueOrDefault(user);
				long mp = (long)bought * CoachCatalog.PackPrice;
				if (mp > 0) s_db.AddMp(user, (int)mp, MpLedger.Coach);
				cmd.CommandText = "INSERT OR REPLACE INTO coach_notice (user_code, title, text, shown) VALUES ($u, $t, $x, 0)";
				cmd.Parameters.Clear();
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$t", "코칭스태프 개편");
				cmd.Parameters.AddWithValue("$x", "코치 카드가 구단 계보(KIA·해태, LG·MBC, 현대·태평양 등)로 바뀌어\n가지고 있던 코치 카드를 모두 회수했습니다."
					+ $"\n\n코치 카드 {n}장 회수, 코칭스태프 등록 해제\n코칭스태프 카드팩 {bought}개 값 {mp:N0} MP를 돌려드렸습니다.\n\n처음부터 다시 뽑아 주세요.");
				cmd.ExecuteNonQuery();
				ServerLog.Info($"코칭스태프 개편: user {user} 코치 카드 {n}장 회수, 카드팩 {bought}개 → {mp:N0} MP");
			}
			cmd.Parameters.Clear();
			cmd.CommandText = "INSERT INTO coach_fix (name, done) VALUES ('lineage_reset_1105', $t)";
			cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("s"));
			cmd.ExecuteNonQuery();
			s_staff.Clear();
		}

		// MBC·청보·삼미 out of the packs (CoachCatalog.OldClubs): once, each card of those clubs becomes the same role, grade and
		// stat of one of its line's pack clubs, drawn as a pack would (user, 2026-11-05 20:45: "지금은 현대 또는 태평양으로
		// 바꿔주면 될 것 같음"), on the staff too. Only 행복한현대 held any (5, from packs at 20:21-20:24).
		private static void OldClubsOnce()
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM coach_fix WHERE name = 'old_clubs_1105'";
			if ((long)cmd.ExecuteScalar() > 0) return;
			List<(long id, uint user, int from, int to)> moves = new List<(long, uint, int, int)>();
			cmd.CommandText = "SELECT id, user_code, coach_idx FROM coach_card ORDER BY id";
			using (SqliteDataReader r = cmd.ExecuteReader())
			{
				while (r.Read())
				{
					int idx = r.GetInt32(2), club = CoachCatalog.Club(idx);
					if (Array.IndexOf(CoachCatalog.OldClubs, club) < 0) continue;
					int[] line = CoachCatalog.LineClubs(club);
					moves.Add((r.GetInt64(0), (uint)r.GetInt64(1), idx,
						CoachCatalog.Card(line[s_rng.Next(line.Length)], CoachCatalog.Role(idx), CoachCatalog.Grade(idx), CoachCatalog.Stat(idx))));
				}
			}
			foreach (var g in moves.GroupBy(m => m.user))
			{
				uint user = g.Key;
				foreach (var m in g)
				{
					cmd.CommandText = "UPDATE coach_card SET coach_idx = $i WHERE id = $id";
					cmd.Parameters.Clear();
					cmd.Parameters.AddWithValue("$i", m.to);
					cmd.Parameters.AddWithValue("$id", m.id);
					cmd.ExecuteNonQuery();
				}
				cmd.Parameters.Clear();
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.CommandText = "SELECT staff FROM coach_state WHERE user_code = $u";
				if (cmd.ExecuteScalar() is string staff)
				{
					List<(long id, uint user, int from, int to)> left = g.ToList();
					string moved = string.Join(",", staff.Split(',').Select(int.Parse).Select(idx =>
					{
						int k = left.FindIndex(m => m.from == idx);
						if (k < 0) return idx;
						int to = left[k].to;
						left.RemoveAt(k);
						return to;
					}));
					cmd.CommandText = "UPDATE coach_state SET staff = $s WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$s", moved);
					cmd.ExecuteNonQuery();
				}
				// The popup, after one not yet shown if there is one.
				cmd.CommandText = "SELECT text FROM coach_notice WHERE user_code = $u AND shown = 0";
				string before = cmd.ExecuteScalar() as string;
				string text = $"MBC·청보·삼미 코치 카드는 이제 카드팩에서 나오지 않습니다.\n가지고 있던 {g.Count()}장을 같은 계보, 같은 역할·등급의 카드로 바꿨습니다.\n효과는 그대로입니다.\n\n"
					+ string.Join("\n", g.Select(m => $"{Label(m.from)} → {Label(m.to)}"));
				cmd.CommandText = "INSERT OR REPLACE INTO coach_notice (user_code, title, text, shown) VALUES ($u, $t, $x, 0)";
				cmd.Parameters.AddWithValue("$t", before == null ? "코치 카드 정리" : "코칭스태프 개편");
				cmd.Parameters.AddWithValue("$x", before == null ? text : before + "\n\n" + text);
				cmd.ExecuteNonQuery();
				ServerLog.Info($"코치 카드 정리: user {user} {string.Join(", ", g.Select(m => $"{Label(m.from)} → {Label(m.to)}"))}");
			}
			cmd.Parameters.Clear();
			cmd.CommandText = "INSERT INTO coach_fix (name, done) VALUES ('old_clubs_1105', $t)";
			cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("s"));
			cmd.ExecuteNonQuery();
			s_staff.Clear();
		}

		private static string Day(DateTime t) => t.ToString("yyyy-MM-dd");

		// ---- cards and state

		public static List<int> Coaches(uint user)
		{
			List<int> list = new List<int>();
			if (s_cs == null) return list;
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT coach_idx FROM coach_card WHERE user_code = $u ORDER BY id";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add(r.GetInt32(0));
			return list;
		}

		internal static void AddCoaches(uint user, IEnumerable<int> coaches)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction t = c.BeginTransaction();
			foreach (int idx in coaches)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = t;
				cmd.CommandText = "INSERT INTO coach_card (user_code, coach_idx, acquired) VALUES ($u, $i, $a)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.Parameters.AddWithValue("$a", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
				cmd.ExecuteNonQuery();
			}
			t.Commit();
		}

		// Read only (engine session, 2026-11-10 22:0x): it used to INSERT OR IGNORE the row on every read, so a read inside another
		// connection's write transaction on the same thread (HustlePlays.Archive → GetCards → FillBonus → Staff) waited 30 s
		// for that lock and failed ("database is locked"). A team with no row reads the table's defaults; SetState makes the row.
		private static (string staff, int sp, string spDay, string giftDay) State(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT staff, sp, sp_day, gift_day FROM coach_state WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			if (!r.Read())
			{
				return ("0,0,0,0,0", CoachCatalog.TestMode ? CoachCatalog.MaxSp : 0, null, null);
			}
			return (r.GetString(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
		}

		private static void SetState(uint user, string column, object value)
		{
			using SqliteConnection c = Open();
			using (SqliteCommand ins = c.CreateCommand())
			{
				ins.CommandText = "INSERT OR IGNORE INTO coach_state (user_code, sp) VALUES ($u, $sp)";
				ins.Parameters.AddWithValue("$u", (long)user);
				ins.Parameters.AddWithValue("$sp", CoachCatalog.TestMode ? CoachCatalog.MaxSp : 0);
				ins.ExecuteNonQuery();
			}
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = $"UPDATE coach_state SET {column} = $v WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$v", value ?? DBNull.Value);
			cmd.ExecuteNonQuery();
		}

		// The five slots; a coach the team no longer holds drops out.
		public static int[] Staff(uint user)
		{
			if (!CoachCatalog.Enabled || s_cs == null) return new int[CoachCatalog.Roles];
			if (s_staff.TryGetValue(user, out int[] cached)) return cached;
			int[] staff = State(user).staff.Split(',').Select(int.Parse).ToArray();
			List<int> owned = Coaches(user);
			for (int slot = 0; slot < staff.Length; slot++)
			{
				if (staff[slot] != 0 && !owned.Remove(staff[slot])) staff[slot] = 0;
			}
			s_staff[user] = staff;
			return staff;
		}

		public static int Sp(uint user) => s_cs == null ? 0 : State(user).sp;

		// Adds (or with a negative amount takes) SP within 0 … MaxSp; the new balance.
		public static int AddSp(uint user, int amount)
		{
			int sp = Math.Clamp(State(user).sp + amount, 0, CoachCatalog.MaxSp);
			SetState(user, "sp", sp);
			return sp;
		}

		// The one lineage of all five slots (its successor's team code, TeamColors.Lineage; MBC and LG cards mix since 11-05) and
		// its bonus (0 when the five are not one line or the lowest is 노말).
		public static (int club, int bonus) SetBonusOf(int[] staff)
		{
			if (staff.Any(i => i == 0)) return (-1, 0);
			int club = CoachCatalog.LineOf(staff[0]);
			if (staff.Any(i => CoachCatalog.LineOf(i) != club)) return (-1, 0);
			return (club, CoachCatalog.SetBonus[staff.Min(CoachCatalog.Grade)]);
		}

		// TeamCard.Coach for the team's cards (Database.GetCards): the set bonus and the 타격·투수코치's own stats.
		public static void FillBonus(uint user, List<TeamCard> cards)
		{
			if (!CoachCatalog.Enabled || s_cs == null || cards.Count == 0) return;
			int[] staff = Staff(user);
			if (staff.All(i => i == 0)) return;
			(int setClub, int setBonus) = SetBonusOf(staff);
			int hitter = staff[CoachCatalog.HC], pitcher = staff[CoachCatalog.PC];
			foreach (TeamCard card in cards)
			{
				CardInfo info = s_catalog.Get(card.Idx);
				if (info == null) continue;
				sbyte[] bonus = new sbyte[6];
				if (CoachCatalog.Line(info.TeamCode) == setClub)
				{
					for (int j = 0; j < 6; j++) bonus[j] += (sbyte)setBonus;
				}
				int own = info.IsBatter ? hitter : pitcher;
				if (own != 0 && CoachCatalog.Serves(own, info.TeamCode) && CoachCatalog.Stat(own) > 0)
				{
					bonus[CoachCatalog.Stat(own) - 1] += (sbyte)CoachCatalog.StatBonus(CoachCatalog.Grade(own));
				}
				card.Coach = bonus;
			}
		}

		// ---- the screen

		private static void EnterMain(GameSession session, Ma9MPacket.PKS_CS_ENTER_COACH_MAIN packet)
		{
			uint user = session.Team.UserCode;
			if (CoachCatalog.TestMode)
			{
				TopUp(user);
			}
			int[] staff = Staff(user);
			List<int> rest = Coaches(user);
			foreach (int idx in staff.Where(i => i != 0)) rest.Remove(idx);
			(int club, int bonus) = SetBonusOf(staff);
			var state = State(user);
			Ma9MPacket.PKS_SC_ENTER_COACH_MAIN reply = new Ma9MPacket.PKS_SC_ENTER_COACH_MAIN();
			reply.error_info.SetNoError();
			reply.SP.n = (short)state.sp;
			reply.MaxSP.n = CoachCatalog.MaxSp;
			reply.ApplyCoachList.array = staff.Select(i => new PInteger { n = i }).ToArray();
			reply.PossessCoachList.array = rest.Select(i => new PInteger { n = i }).ToArray();
			reply.BonusTeam.n = (byte)(bonus > 0 ? club : 20);
			reply.BonusStat.n = (byte)bonus;
			reply.todayGiftAble.n = (byte)(staff[CoachCatalog.CL] != 0 && state.giftDay != Day(DateTime.Now) ? 1 : 0);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 코칭스태프: SP {state.sp}, staff [{string.Join(", ", staff.Select(i => i == 0 ? "-" : Label(i)))}], {rest.Count} more cards{(bonus > 0 ? $", set bonus +{bonus}" : string.Empty)}");
		}

		internal static string Label(int idx) => $"{CoachCatalog.GradeNames[CoachCatalog.Grade(idx)]} {CoachCatalog.Name(idx)}{(CoachCatalog.Stat(idx) > 0 ? $"({CoachCatalog.StatName(CoachCatalog.Role(idx), CoachCatalog.Stat(idx))})" : string.Empty)}";

		// Test servers (MA9M_COACH_TEST=1): enough packs and SP to try everything.
		private static void TopUp(uint user)
		{
			if (InventoryHandlers.ItemAmount(user, CoachCatalog.Pack) < 10) InventoryHandlers.AddItem(user, CoachCatalog.Pack, 30 - InventoryHandlers.ItemAmount(user, CoachCatalog.Pack));
			if (InventoryHandlers.ItemAmount(user, CoachCatalog.SetPack) < 3) InventoryHandlers.AddItem(user, CoachCatalog.SetPack, 10 - InventoryHandlers.ItemAmount(user, CoachCatalog.SetPack));
			if (Sp(user) < 500) AddSp(user, CoachCatalog.MaxSp);
		}

		private static void Apply(GameSession session, Ma9MPacket.PKS_CS_APPLY_COACH_CARD packet)
		{
			uint user = session.Team.UserCode;
			int[] want = (packet.ApplyCoachList.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			Ma9MPacket.PKS_SC_APPLY_COACH_CARD reply = new Ma9MPacket.PKS_SC_APPLY_COACH_CARD();
			string refuse = TryApply(user, want);
			if (refuse != null)
			{
				reply.error_info.SetErrorMessage(refuse);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] 코칭스태프 등록 refused: {refuse}");
				return;
			}
			(int club, int bonus) = SetBonusOf(Staff(user));
			reply.error_info.SetNoError();
			reply.BonusTeam.n = (byte)(bonus > 0 ? club : 20);
			reply.BonusStat.n = (byte)bonus;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 코칭스태프 등록: [{string.Join(", ", want.Select(i => i == 0 ? "-" : Label(i)))}]{(bonus > 0 ? $", set bonus +{bonus}" : string.Empty)}");
		}

		// Null = the five slots are now these.
		internal static string TryApply(uint user, int[] want)
		{
			if (want.Length != CoachCatalog.Roles) return "코치 명단이 잘못되었습니다.";
			List<int> owned = Coaches(user);
			for (int slot = 0; slot < want.Length; slot++)
			{
				if (want[slot] == 0) continue;
				if (!CoachCatalog.IsCoach(want[slot]) || CoachCatalog.Role(want[slot]) != slot) return "그 자리에 맞지 않는 코치입니다.";
				if (!owned.Remove(want[slot])) return "보유하지 않은 코치입니다.";
			}
			int[] before = Staff(user);
			SetState(user, "staff", string.Join(",", want));
			s_staff[user] = want.ToArray();
			if (before[CoachCatalog.HD] != want[CoachCatalog.HD])
			{
				// Another 수석코치: this sitting's visitors who were not seen yet go (the new one brings his own club's).
				ForgetWaiting(user);
			}
			return null;
		}

		// ---- 치어리더

		private static void Gift(GameSession session, Ma9MPacket.PKS_CS_GET_TODAY_CLITEM packet)
		{
			Ma9MPacket.PKS_SC_GET_TODAY_CLITEM reply = new Ma9MPacket.PKS_SC_GET_TODAY_CLITEM();
			string refuse = TryGift(session.Team.UserCode);
			if (refuse == null) reply.error.SetNoError();
			else reply.error.SetErrorMessage(refuse);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 치어리더 선물: {refuse ?? "ok"}");
		}

		public static readonly (int item, int count)[] GiftItems = { (PlayerManageHandlers.Advanced, 1), (ShopHandlers.LuckyPouch, 1) };

		internal static string TryGift(uint user)
		{
			int cheer = Staff(user)[CoachCatalog.CL];
			if (cheer == 0) return "치어리더를 등록해야 선물을 받을 수 있습니다.";
			if (State(user).giftDay == Day(DateTime.Now)) return "오늘의 치어리더 선물은 이미 받았습니다.";
			SetState(user, "gift_day", Day(DateTime.Now));
			foreach ((int item, int count) in GiftItems) InventoryHandlers.AddItem(user, item, count);
			return null;
		}

		// The lobby (LobbyHandlers.EnterMain): the day's first SP and the 치어리더's gift with a popup.
		public static void OnLobby(GameSession session)
		{
			if (!CoachCatalog.Enabled || s_cs == null || session.Team == null) return;
			uint user = session.Team.UserCode;
			// The 11-05 take-back and refund (ResetOnce), once.
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT title, text FROM coach_notice WHERE user_code = $u AND shown = 0";
				cmd.Parameters.AddWithValue("$u", (long)user);
				string title = null, text = null;
				using (SqliteDataReader r = cmd.ExecuteReader())
				{
					if (r.Read())
					{
						title = r.GetString(0);
						text = r.GetString(1);
					}
				}
				if (title != null)
				{
					cmd.CommandText = "UPDATE coach_notice SET shown = 1 WHERE user_code = $u";
					cmd.ExecuteNonQuery();
					Ma9MPacket.PKS_SC_POPUP notice = new Ma9MPacket.PKS_SC_POPUP();
					notice.Type.n = 1;
					notice.Messages.array = new[] { new PLongString { str = title }, new PLongString { str = text } };
					session.Send(notice);
					TopBar.Send(session);
					ServerLog.Info($"[{session.Id}] 코칭스태프 개편 안내");
				}
			}
			string today = Day(DateTime.Now);
			if (State(user).spDay != today)
			{
				SetState(user, "sp_day", today);
				int sp = AddSp(user, CoachCatalog.LoginSp);
				ServerLog.Info($"[{session.Id}] 코칭스태프: 접속 SP +{CoachCatalog.LoginSp} → {sp}");
			}
			int cheer = Staff(user)[CoachCatalog.CL];
			if (cheer != 0 && TryGift(user) == null)
			{
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[]
				{
					new PLongString { str = "치어리더의 선물" },
					new PLongString { str = $"{CoachCatalog.Name(cheer)}의 오늘 선물입니다.\n고급 컨디션 회복제 1개, MP주머니 1개\n인벤토리에서 확인해 주세요." }
				};
				session.Send(popup);
				ServerLog.Info($"[{session.Id}] 치어리더 선물 (lobby)");
			}
		}

		// League-game MP of the 치어리더 (LeagueHandlers shows it as the game's "(+n)", ConfirmAndPay pays it): fixed the
		// first time the user's unconfirmed game is looked at or confirmed, from the 치어리더 registered then; none for a
		// game confirmed without one.
		public static short Cheer(uint user, LeagueGame g)
		{
			if (!CoachCatalog.Enabled || s_cs == null || g == null || g.Mp <= 0) return 0;
			using SqliteConnection c = Open();
			using (SqliteCommand get = c.CreateCommand())
			{
				get.CommandText = "SELECT mp FROM coach_cheer WHERE user_code = $u AND season = $s AND game = $g";
				get.Parameters.AddWithValue("$u", (long)user);
				get.Parameters.AddWithValue("$s", g.Season);
				get.Parameters.AddWithValue("$g", g.Game);
				object mp = get.ExecuteScalar();
				if (mp != null) return (short)Math.Min(short.MaxValue, Convert.ToInt32(mp));
			}
			if (g.Confirmed) return 0;
			int cheer = Staff(user)[CoachCatalog.CL];
			int extra = cheer == 0 ? 0 : (int)Math.Round(g.Mp * CoachCatalog.CheerPercent[CoachCatalog.Grade(cheer)] / 100.0);
			using SqliteCommand put = c.CreateCommand();
			put.CommandText = "INSERT OR IGNORE INTO coach_cheer (user_code, season, game, mp) VALUES ($u, $s, $g, $mp)";
			put.Parameters.AddWithValue("$u", (long)user);
			put.Parameters.AddWithValue("$s", g.Season);
			put.Parameters.AddWithValue("$g", g.Game);
			put.Parameters.AddWithValue("$mp", extra);
			put.ExecuteNonQuery();
			return (short)Math.Min(short.MaxValue, extra);
		}

		// Games being confirmed (LeagueService.ConfirmAndPay, before they are marked confirmed): their 치어리더 MP and SP for the wins.
		public static void OnLeagueConfirmed(uint user, IEnumerable<LeagueGame> games)
		{
			if (!CoachCatalog.Enabled || s_cs == null) return;
			int mp = 0, wins = 0;
			foreach (LeagueGame g in games)
			{
				int side = g.Detail?.Owner == null ? -1 : g.Detail.Owner[0] == user ? 0 : g.Detail.Owner[1] == user ? 1 : -1;
				if (side >= 0 && (side == 0 ? g.HomeScore > g.AwayScore : g.AwayScore > g.HomeScore)) wins++;
				if (Cheer(user, g) <= 0) continue;
				using SqliteConnection c = Open();
				using SqliteCommand pay = c.CreateCommand();
				pay.CommandText = "UPDATE coach_cheer SET paid = 1 WHERE user_code = $u AND season = $s AND game = $g AND paid = 0 RETURNING mp";
				pay.Parameters.AddWithValue("$u", (long)user);
				pay.Parameters.AddWithValue("$s", g.Season);
				pay.Parameters.AddWithValue("$g", g.Game);
				object paid = pay.ExecuteScalar();
				if (paid != null) mp += Convert.ToInt32(paid);
			}
			if (mp > 0) s_db.AddMp(user, mp, MpLedger.Coach);
			if (wins > 0) AddSp(user, wins * CoachCatalog.LeagueWinSp);
			if (mp > 0 || wins > 0) ServerLog.Info($"코칭스태프: user {user} league games confirmed, 치어리더 +{mp:N0} MP, {wins} wins SP +{wins * CoachCatalog.LeagueWinSp}");
		}

		// ---- 코칭스태프 카드팩 (shop product 203, 30만 MP; the 세트팩 only on test servers)

		public static bool IsPack(int item) => CoachCatalog.Enabled && (item == CoachCatalog.Pack || CoachCatalog.TestMode && item == CoachCatalog.SetPack);

		// 자팀 (user, 2026-11-13 00:5x: "1군 최다 계보"): the lineage most of the 1군's 25 cards belong to, more balls first on a
		// tie, then the lower line; -1 without a lineup. CoachCatalog.RollPack gives it OwnLinePercent of the packs.
		public static int OwnLine(uint user)
		{
			Lineup lineup = s_db?.GetLineup(user);
			if (lineup == null || s_catalog == null) return -1;
			var lines = lineup.Batters.Concat(lineup.Pitchers).Where(i => i != 0).Distinct()
				.Select(i => s_catalog.Get(i)).Where(c => c != null)
				.GroupBy(c => CoachCatalog.Line(c.TeamCode))
				.Select(g => (line: g.Key, cards: g.Count(), balls: g.Sum(c => c.Ball)))
				.OrderByDescending(x => x.cards).ThenByDescending(x => x.balls).ThenBy(x => x.line).ToList();
			return lines.Count == 0 ? -1 : lines[0].line;
		}

		// A pack's card for this team (자팀 계보 at OwnLinePercent).
		public static int RollPack(uint user, Random rng) => CoachCatalog.RollPack(rng, OwnLine(user));

		public static IEnumerable<ItemDef> Items()
		{
			if (!CoachCatalog.Enabled) yield break;
			Func<int, string> odds = CoachCatalog.PackPercentText;
			yield return new ItemDef
			{
				Index = CoachCatalog.Pack, Name = "코칭스태프 카드팩", Type = ItemType.CoachCardPack, TypeText = "코치 카드팩", File = "pack_coach",
				Desc = $"코치 카드 1장을 얻습니다(노말 {odds(1)}%, 스페셜 {odds(2)}%, 레어 {odds(3)}%, 엘리트 {odds(4)}%). 1군에 가장 많은 구단(계보)의 코치가 {CoachCatalog.OwnLinePercent}%. 같은 카드가 또 나올 수 있습니다."
			};
			if (!CoachCatalog.TestMode) yield break;
			yield return new ItemDef
			{
				Index = CoachCatalog.SetPack, Name = "코치 세트팩 (테스트)", Type = ItemType.CoachCardPack, TypeText = "코치 카드팩", File = "pack_all",
				Desc = "한 구단의 같은 등급 코치 5장(수석·투수·타격·트레이닝·치어리더)을 얻습니다. 테스트 서버 전용."
			};
		}

		public static void OpenPacks(GameSession session, int item, int amount, Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM reply)
		{
			uint user = session.Team.UserCode;
			int each = item == CoachCatalog.SetPack ? CoachCatalog.Roles : 1;
			if (InventoryHandlers.ItemAmount(user, item) < amount)
			{
				reply.result.n = 1;
			}
			else if (Coaches(user).Count + amount * each > CoachCatalog.MaxCoaches)
			{
				reply.result.n = 2;
				reply.error_info.SetErrorMessage($"코치는 {CoachCatalog.MaxCoaches}장까지 가질 수 있습니다.");
			}
			else
			{
				List<int> cards = new List<int>();
				int ownLine = OwnLine(user);
				for (int n = 0; n < amount; n++)
				{
					if (item == CoachCatalog.Pack)
					{
						cards.Add(CoachCatalog.RollPack(s_rng, ownLine));
						continue;
					}
					// 세트팩 (test): the five roles of one club and grade.
					int grade = CoachCatalog.Grade(CoachCatalog.Roll(s_rng)), club = CoachCatalog.PackClubs[s_rng.Next(CoachCatalog.PackClubs.Length)];
					cards.AddRange(Enumerable.Range(0, CoachCatalog.Roles).Select(role => CoachCatalog.Roll(s_rng, grade, club, role)));
				}
				AddCoaches(user, cards);
				InventoryHandlers.AddItem(user, item, -amount);
				reply.result.n = 0;
				reply.card_index.array = cards.Select(c => new PInteger { n = c }).ToArray();
				reply.card_condition.array = cards.Select(_ => new PByte { n = 0 }).ToArray();
				ServerLog.Info($"[{session.Id}] coach pack {item} x{amount}: {string.Join(", ", cards.Select(Label))}");
			}
			session.Send(reply);
		}
	}
}
