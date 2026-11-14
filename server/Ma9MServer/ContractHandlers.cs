using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 팀 관리 > 계약관리 (ContractManage_*) and the player window's 계약 tab (systems session, 2026-10-19).
	//
	// User decisions: contracts run in real days, 7 for a new card and 7 more per renewal (user, 2026-10-24: "테스트
	// 및 빠른 게임을 지향 … 7일로 변경, 재계약기간도 7일"; 30 and 30 before, the original's), at most 99 left, for
	// ball × 40,000 MP and, for the first six, +3 to a chosen stat (played
	// at the development weight). Renewing with more than 10 days left (2 with MA9M_CONTRACT_FROM2, SurchargeAfter) costs
	// more, by the client's own formula (ContractCost.GetCost: × (1 + 0.2 × (days − 10))). An expired card keeps its roster
	// place but plays at 최하 (Condition.For; the client's roster warning says so); renewing it
	// restarts its condition at 보통. Release refunds ball × 5,000 MP.
	public static class ContractHandlers
	{
		public const byte StartDays = 7;

		public const int ExtendDays = 7;

		public const int MaxDays = 99;

		public const int BonusRenewals = 6;

		public const int BonusPerRenewal = 3;

		public const int CostPerBall = 40_000;

		// Renewing with more days left than this costs more, × (1 + 0.2 × (days − this)). 10 as the original; 2 with
		// MA9M_CONTRACT_FROM2=1 (user, 2026-11-10 02:2x: "그럼 우리는 2일 초과로 남아있을 시 비싸지게 만듭시다"). The switch
		// goes on in run_server.bat once the phone has APK 1.0.41, which reads this number from the cost table's 12th column
		// (written only while the switch is on, so 1.0.40 gets the same table as before); 1.0.40 and older count from 10.
		public static readonly int SurchargeAfter = Environment.GetEnvironmentVariable("MA9M_CONTRACT_FROM2") == "1" ? 2 : 10;

		public const int RefundPerBall = 5_000;

		// Releases stop at this many cards (the client's own check, patched from the original 50).
		public const int MinCards = 25;

		// 스탯재분배권 (ContractManage_Main shows the button while the team has one).
		public const int RedistItem = 90611;

		private static string s_cs;

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static readonly Dictionary<uint, (DateTime at, Dictionary<int, (DateTime until, DateTime? renewed)> cards)> s_state =
			new Dictionary<uint, (DateTime, Dictionary<int, (DateTime, DateTime?)>)>();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			WriteCostTable(Path.Combine(dataDir, "tables", "ma9_contract-cost.txt"));
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_CONTRACT>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_CARD_DETAIL_INFO_MANAGE_CONTRACT>(Detail);
			loop.Register<Ma9MPacket.PKS_CS_EXTEND_CONTRACT>(Extend);
			loop.Register<Ma9MPacket.PKS_CS_BREAK_CONTRACT>(Break);
			loop.Register<Ma9MPacket.PKS_CS_USE_ITEM_REDIST_CONTRACT_STAT>(Redistribute);
		}

		// The client's cost table (TableReader.ContractCost): a row per renewal count 0-5 (6+ uses the
		// last), a column per ball 1-10. Every row is ball × 40,000 (user's economy draft). A 12th column, SurchargeAfter,
		// only when it is not the original 10 (APK 1.0.41 reads it; older apps read the first 11 columns of a row).
		private static void WriteCostTable(string path)
		{
			string after = SurchargeAfter != 10 ? "\t" + SurchargeAfter.ToString(CultureInfo.InvariantCulture) : string.Empty;
			List<string> lines = new List<string> { "count\t" + string.Join("\t", Enumerable.Range(1, 10).Select(b => $"ball{b}")) + (after.Length > 0 ? "\tsurcharge_after" : string.Empty) };
			for (int count = 0; count < BonusRenewals; count++)
			{
				lines.Add(count + "\t" + string.Join("\t", Enumerable.Range(1, 10).Select(b => (b * CostPerBall).ToString(CultureInfo.InvariantCulture))) + after);
			}
			string text = string.Join("\r\n", lines) + "\r\n";
			if (!File.Exists(path) || File.ReadAllText(path) != text)
			{
				File.WriteAllText(path, text);
			}
		}

		// Same as the client's ContractCost.GetCost (whose 10 comes from the table since 1.0.41: SurchargeAfter).
		public static int Cost(int count, int ball, int daysLeft)
		{
			float cost = Math.Clamp(ball, 1, 10) * CostPerBall;
			if (daysLeft > SurchargeAfter)
			{
				cost *= 1f + (daysLeft - (float)SurchargeAfter) * 0.2f;
			}
			return (int)(cost / 100f) * 100;
		}

		// The first day a contract is expired: its stored end, or the acquisition day + StartDays.
		public static DateTime Until(string acquiredUtc, string until)
		{
			if (until != null && DateTime.TryParse(until, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime u))
			{
				return u.Date;
			}
			DateTime acquired = DateTime.Parse(acquiredUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
			return acquired.ToLocalTime().Date.AddDays(StartDays);
		}

		public static byte DaysLeft(DateTime until, DateTime today) => (byte)Math.Clamp((until - today.Date).Days, 0, MaxDays);

		// A team card's contract for Condition.For (null = not a team card, e.g. AI clubs). Cached per
		// team; renewals and releases drop the cache.
		public static (DateTime until, DateTime? renewed)? State(uint user, int idx)
		{
			if (s_cs == null)
			{
				return null;
			}
			lock (s_state)
			{
				if (!s_state.TryGetValue(user, out var entry) || (DateTime.Now - entry.at).TotalMinutes > 1)
				{
					entry = (DateTime.Now, Load(user));
					s_state[user] = entry;
				}
				return entry.cards.TryGetValue(idx, out var st) ? st : null;
			}
		}

		private static Dictionary<int, (DateTime until, DateTime? renewed)> Load(uint user)
		{
			Dictionary<int, (DateTime, DateTime?)> cards = new Dictionary<int, (DateTime, DateTime?)>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, acquired, contract_until, contract_restart FROM team_card WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				DateTime? renewed = r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture).Date;
				cards[r.GetInt32(0)] = (Until(r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)), renewed);
			}
			return cards;
		}

		public static void Invalidate(uint user)
		{
			lock (s_state)
			{
				s_state.Remove(user);
			}
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static HashSet<int> Roster(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			return l == null ? new HashSet<int>() : l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_CONTRACT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			List<TeamCard> cards = s_db.GetCards(user);
			Ma9MPacket.PKS_SC_ENTER_CONTRACT reply = new Ma9MPacket.PKS_SC_ENTER_CONTRACT();
			reply.item_index.array = new[] { new PInteger { n = RedistItem } };
			reply.item_amount.array = new[] { new PShort { n = (short)Math.Min(short.MaxValue, InventoryHandlers.ItemAmount(user, RedistItem)) } };
			reply.possess_count.n = (short)cards.Count;
			reply.max_possess_count.n = RosterHandlers.MaxPossessOf(user);
			// In the roster list's order (GetCards); the client then asks for ROSTER_PLAYER.
			reply.contract_count.array = cards.Select(c => new PByte { n = (byte)Math.Min(255, c.ContractCount) }).ToArray();
			reply.major.array = Roster(user).Select(i => new PInteger { n = i }).ToArray();
			session.Send(reply);
		}

		private static void Detail(GameSession session, Ma9MPacket.PKS_CS_CARD_DETAIL_INFO_MANAGE_CONTRACT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			TeamCard card = s_db.GetCards(session.Team.UserCode).FirstOrDefault(c => c.Idx == packet.index.n);
			Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_MANAGE_CONTRACT reply = new Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_MANAGE_CONTRACT();
			reply.remain_contract.n = card?.RemainContract ?? StartDays;
			reply.contract_count.n = (byte)Math.Min(255, card?.ContractCount ?? 0);
			session.Send(reply);
		}

		private static void Extend(GameSession session, Ma9MPacket.PKS_CS_EXTEND_CONTRACT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_EXTEND_CONTRACT reply = new Ma9MPacket.PKS_SC_EXTEND_CONTRACT();
			string why = TryExtend(user, packet.target_card.n, packet.bonus_stat.n, DateTime.Now.Date, out int mp);
			reply.result.n = (byte)(why == null ? 0 : 1);
			reply.cur_point.n = mp;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 계약 연장 card {packet.target_card.n}: {why ?? "ok"} (MP {mp:N0})");
		}

		// Null = renewed; otherwise why not. mp = the balance afterwards.
		public static string TryExtend(uint user, int idx, int bonusStat, DateTime today, out int mp)
		{
			mp = s_db.AddMp(user, 0);
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			CardInfo info = s_catalog.Get(idx);
			if (card == null || info == null)
			{
				return "not a card of the team";
			}
			if (card.RemainContract >= MaxDays)
			{
				return "already 99 days";
			}
			bool bonus = card.ContractCount < BonusRenewals;
			int[] points = BonusPoints(bonusStat);
			if (bonus && points == null)
			{
				return "no bonus stat chosen";
			}
			int cost = Cost(card.ContractCount, info.Ball, card.RemainContract);
			if (mp < cost)
			{
				return $"MP short ({cost:N0})";
			}
			bool expired = card.ContractUntil <= today;
			DateTime until = (expired ? today : card.ContractUntil).AddDays(ExtendDays);
			if (until > today.AddDays(MaxDays))
			{
				until = today.AddDays(MaxDays);
			}
			sbyte[] contract = (sbyte[])card.Contract.Clone();
			if (bonus)
			{
				for (int k = 0; k < 6; k++)
				{
					contract[k] = (sbyte)Math.Min(sbyte.MaxValue, contract[k] + points[k]);
				}
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "UPDATE team_card SET contract_until = $until, contract_count = contract_count + 1, contract = $contract"
					+ (expired ? ", contract_restart = $today" : string.Empty) + " WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.AddWithValue("$until", until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$today", today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				cmd.Parameters.Add("$contract", SqliteType.Blob).Value = contract.Select(v => (byte)v).ToArray();
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.ExecuteNonQuery();
			}
			mp = s_db.AddMp(user, -cost, MpLedger.Contract);
			Invalidate(user);
			Missions.Add(user, Missions.Contracts);  // 업적
			return null;
		}

		// A renewal's bonus by stat (null = none chosen). 0–5: +3 to that stat (the 1.0.5 client); 32 + a·36 +
		// b·6 + c: +1 each to a, b and c (user, 2026-10-20: "보너스 스탯 3을 주고 원하는대로 찍을 수 있게 … 컨택1,
		// 파워2"; the client's Ma9MContractPoints).
		public static int[] BonusPoints(int code)
		{
			int[] points = new int[6];
			if (code >= 0 && code <= 5)
			{
				points[code] = BonusPerRenewal;
				return points;
			}
			if (code < 32 || code >= 32 + 216)
			{
				return null;
			}
			int v = code - 32;
			points[v / 36]++;
			points[v / 6 % 6]++;
			points[v % 6]++;
			return points;
		}

		private static void Break(GameSession session, Ma9MPacket.PKS_CS_BREAK_CONTRACT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_BREAK_CONTRACT reply = new Ma9MPacket.PKS_SC_BREAK_CONTRACT();
			string why = TryRelease(user, packet.target_card.n, out int mp);
			reply.result.n = (byte)(why == null ? 0 : 1);
			reply.cur_point.n = mp;
			if (why == null)
			{
				reply.error.SetNoError();
			}
			else
			{
				// The app reads this through Ma9MClientUtil.ErrorPopup, which parses JSON: plain text threw there.
				reply.error.SetErrorMessage(why);
			}
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 선수 방출 card {packet.target_card.n}: {why ?? "ok"} (MP {mp:N0})");
		}

		// Null = released; otherwise the message for the player.
		public static string TryRelease(uint user, int idx, out int mp)
		{
			mp = s_db.AddMp(user, 0);
			List<TeamCard> cards = s_db.GetCards(user);
			TeamCard card = cards.FirstOrDefault(c => c.Idx == idx);
			CardInfo info = s_catalog.Get(idx);
			if (card == null || info == null)
			{
				return "보유한 선수가 아닙니다.";
			}
			if (card.IsLock)
			{
				return "보호 중인 선수는 방출할 수 없습니다.";
			}
			if (Roster(user).Contains(idx))
			{
				return "1군 로스터에 있는 선수는 방출할 수 없습니다.";
			}
			if (LessonHandlers.InLesson(user, idx))
			{
				return "특별수강 중인 선수는 방출할 수 없습니다.";
			}
			if (cards.Count <= MinCards)
			{
				return $"보유한 선수가 {MinCards}명 이하일 때는 방출할 수 없습니다.";
			}
			s_db.RemoveCards(user, new[] { idx });
			mp = s_db.AddMp(user, info.Ball * RefundPerBall, MpLedger.Release);
			Invalidate(user);
			return null;
		}

		private static void Redistribute(GameSession session, Ma9MPacket.PKS_CS_USE_ITEM_REDIST_CONTRACT_STAT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_USE_ITEM_REDIST_CONTRACT_STAT reply = new Ma9MPacket.PKS_SC_USE_ITEM_REDIST_CONTRACT_STAT();
			string why = TryRedistribute(user, packet.target_card.n, packet.stats.sbytes);
			reply.result.n = (byte)(why == null ? 0 : 1);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 스탯 재분배 card {packet.target_card.n}: {why ?? "ok"}");
		}

		// 스탯재분배권: the same contract points laid out again; uses one item.
		public static string TryRedistribute(uint user, int idx, sbyte[] stats)
		{
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			if (card == null || stats == null || stats.Length != 6 || stats.Any(v => v < 0))
			{
				return "bad request";
			}
			if (stats.Sum(v => (int)v) != card.Contract.Sum(v => (int)v))
			{
				return "point total differs";
			}
			if (InventoryHandlers.ItemAmount(user, RedistItem) < 1)
			{
				return "no 스탯재분배권";
			}
			InventoryHandlers.AddItem(user, RedistItem, -1);
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "UPDATE team_card SET contract = $contract WHERE user_code = $u AND card_idx = $i";
			cmd.Parameters.Add("$contract", SqliteType.Blob).Value = stats.Select(v => (byte)v).ToArray();
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			cmd.ExecuteNonQuery();
			return null;
		}

		// Round trips on a data copy (--leaguetest); 0 = ok.
		// Dev (systems session, 2026-11-10): `--contracttest` on a copy of the data directory — the cost table (12 columns
		// with MA9M_CONTRACT_FROM2, else the 11 the old apps had) and SelfTest on the test account's team.
		public static int SelfTestOnCopy(string dataDir)
		{
			uint user = 0;
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				object found = cmd.ExecuteScalar();
				if (found != null) user = Convert.ToUInt32(found);
			}
			string[] rows = File.ReadAllLines(Path.Combine(dataDir, "tables", "ma9_contract-cost.txt"));
			int columns = SurchargeAfter != 10 ? 12 : 11;
			bool tableOk = rows.Length == 1 + BonusRenewals && rows.All(r => r.Split('\t').Length == columns)
				&& rows.Skip(1).All(r => columns == 11 || r.Split('\t')[11] == SurchargeAfter.ToString(CultureInfo.InvariantCulture));
			ServerLog.Info($"contract self-test: {(tableOk ? "ok" : "FAIL")} cost table {rows.Length} rows of {columns} columns, surcharge after {SurchargeAfter} days; row 1: {rows.ElementAtOrDefault(1)}");
			if (user == 0) ServerLog.Info("contract self-test: FAIL no autotest team");
			int problems = (tableOk ? 0 : 1) + (user == 0 ? 1 : SelfTest(user));
			ServerLog.Info($"contract self-test: {(problems == 0 ? "all ok" : problems + " problem(s)")}");
			return problems == 0 ? 0 : 1;
		}

		public static int SelfTest(uint user)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"contract self-test: {(ok ? "ok" : "FAIL")} {what}");
			}
			DateTime today = DateTime.Now.Date;
			List<TeamCard> cards = s_db.GetCards(user);
			HashSet<int> roster = Roster(user);
			// A card just acquired on the data copy (the cheapest one not owned). The free card with the most days left stood in
			// before, but 7-day contracts run out within the week and an aged copy had none left (2026-10-31: "new contract 0 days").
			HashSet<int> ownedIdx = cards.Select(c => c.Idx).ToHashSet();
			CardInfo fresh = s_catalog.All.Where(ci => !ownedIdx.Contains(ci.Idx)).OrderBy(ci => ci.Ball).ThenBy(ci => ci.Idx).FirstOrDefault();
			if (fresh != null)
			{
				s_db.AddCards(user, new[] { fresh.Idx }, RosterHandlers.StartContract);
				cards = s_db.GetCards(user);
			}
			TeamCard pick = fresh != null ? cards.FirstOrDefault(c => c.Idx == fresh.Idx)
				: cards.Where(c => !roster.Contains(c.Idx) && !c.IsLock && c.ContractCount == 0).OrderByDescending(c => c.RemainContract).FirstOrDefault();
			if (pick == null)
			{
				Check(false, "no free card to test with");
				return problems;
			}
			CardInfo info = s_catalog.Get(pick.Idx);
			Check(pick.RemainContract > 0 && pick.RemainContract <= StartDays, $"new contract {pick.RemainContract} days (card {pick.Idx}, {info.Ball}볼)");
			Check(Cost(0, info.Ball, SurchargeAfter) == info.Ball * CostPerBall && Cost(0, info.Ball, SurchargeAfter + 20) == (int)(info.Ball * CostPerBall * 5f / 100f) * 100,
				$"cost = ball × 40,000 up to {SurchargeAfter} days left, × 5 at {SurchargeAfter + 20}");
			// The user's numbers for a 9볼 (11-10 02:2x, after 2 days): 3 days 43.2만, 7 days 72만, 14 days 122.4만.
			Check(SurchargeAfter != 2 || Cost(0, 9, 2) == 360_000 && Cost(0, 9, 3) == 432_000 && Cost(0, 9, 7) == 720_000 && Cost(0, 9, 14) == 1_224_000,
				$"9볼 at 2/3/7/14 days left: {Cost(0, 9, 2):N0} / {Cost(0, 9, 3):N0} / {Cost(0, 9, 7):N0} / {Cost(0, 9, 14):N0} (surcharge after {SurchargeAfter})");
			s_db.AddMp(user, 50_000_000, MpLedger.Test);
			int mp0 = s_db.AddMp(user, 0);
			string why = TryExtend(user, pick.Idx, 1, today, out int mp1);
			TeamCard after = s_db.GetCards(user).First(c => c.Idx == pick.Idx);
			Check(why == null && after.RemainContract == Math.Min(pick.RemainContract + ExtendDays, MaxDays) && after.ContractCount == 1
				&& after.Contract[1] == pick.Contract[1] + BonusPerRenewal && mp0 - mp1 == Cost(0, info.Ball, pick.RemainContract),
				$"renewal: {pick.RemainContract} → {after.RemainContract} days, stat 2 +{after.Contract[1] - pick.Contract[1]}, {mp0 - mp1:N0} MP ({why ?? "ok"})");
			// Expiry: push the end into the past; the card plays at 최하, a renewal restarts it at 보통.
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "UPDATE team_card SET contract_until = $d WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.AddWithValue("$d", today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", pick.Idx);
				cmd.ExecuteNonQuery();
			}
			Invalidate(user);
			byte expiredCondition = Condition.For(user, pick.Idx, today);
			TeamCard expired = s_db.GetCards(user).First(c => c.Idx == pick.Idx);
			why = TryExtend(user, pick.Idx, 0, today, out _);
			Check(expired.RemainContract == 0 && expiredCondition == 4 && why == null && Condition.For(user, pick.Idx, today) == 2
				&& s_db.GetCards(user).First(c => c.Idx == pick.Idx).RemainContract == ExtendDays, $"expired: 0 days, condition {expiredCondition} (최하); renewed → {ExtendDays} days, 보통");
			// Three single points (Ma9MContractPoints): 컨택, 파워, 선구 +1 each = code 32 + 0·36 + 1·6 + 3.
			TeamCard beforePoints = s_db.GetCards(user).First(c => c.Idx == pick.Idx);
			why = TryExtend(user, pick.Idx, 32 + 0 * 36 + 1 * 6 + 3, today, out _);
			TeamCard afterPoints = s_db.GetCards(user).First(c => c.Idx == pick.Idx);
			Check(why == null && afterPoints.Contract[0] == beforePoints.Contract[0] + 1 && afterPoints.Contract[1] == beforePoints.Contract[1] + 1
				&& afterPoints.Contract[3] == beforePoints.Contract[3] + 1 && afterPoints.Contract.Sum(v => (int)v) == beforePoints.Contract.Sum(v => (int)v) + 3,
				$"three points 컨1 파1 선1: {string.Join("/", beforePoints.Contract)} → {string.Join("/", afterPoints.Contract)} ({why ?? "ok"})");
			Check(TryExtend(user, pick.Idx, 20, today, out _) != null && BonusPoints(32 + 215)?.Sum() == 3 && BonusPoints(248) == null, "bad bonus codes refused");
			int refund = info.Ball * RefundPerBall;
			int before = s_db.AddMp(user, 0);
			bool rosterRefused = roster.Count > 0 && TryRelease(user, roster.First(), out _) != null;
			why = TryRelease(user, pick.Idx, out int mp2);
			Check(rosterRefused && (cards.Count <= MinCards ? why != null : why == null && mp2 - before == refund && s_db.GetCards(user).All(c => c.Idx != pick.Idx)),
				$"release: roster card refused, free card {(why ?? $"+{refund:N0} MP")}");
			return problems;
		}
	}
}
