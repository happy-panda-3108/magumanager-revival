using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 도전과제 > 오늘의 미션 (user, 2026-10-20): six missions a day on the original Daily Mission screen,
	// one per card: 출석, 리그 안타, 리그 탈삼진, 시범경기 2회, 카드팩 구매, 트레이닝팩 구매. Each pays MP by
	// its stars when its 보상받기 is pressed; clearing all six pays as much again (the screen's notice).
	// The day turns at local midnight. The counters (mission_counter: all time, mission_daily: per
	// day) also feed the 업적 (Achievements).
	public static class Missions
	{
		// Counter keys. League numbers count when a result is confirmed (the same moment its MP is paid),
		// so the missions never show a result before the unconfirmed-games screen does.
		public const string Login = "login", LoginDays = "login_days", LeagueHits = "league_hits", LeagueStrikeouts = "league_k",
			LeagueWins = "league_wins", LeagueHomeRuns = "league_hr", Exhibitions = "exhibitions", ExhibitionWins = "exhibition_wins",
			CardPacksBought = "card_packs_bought", CardPacksOpened = "card_packs_opened", TrainingPacksBought = "training_packs_bought",
			TrainingPacksOpened = "training_packs_opened", Trainings = "trainings", Contracts = "contracts", Fusions = "fusions",
			TeamColour = "team_color", SeasonsDone = "seasons_done", SeasonsWon = "seasons_won", AllClears = "daily_all_clear",
			TeamColourFull = "team_color_full", TeamColourS = "team_color_s", TeamYearS = "team_year_s";

		// 경기 순간 업적 (user, 2026-10-22): the engine marks them per league game and side in GameDetail.Moments;
		// OnLeagueGames counts the user's side when the result is confirmed. One key per occurrence (two grand
		// slams in a game = twice).
		public const string GameCycle = "game_cycle",            // 사이클링 히트: one batter 1B, 2B, 3B and HR in the game
			GameThreeHomers = "game_3hr",                         // one batter 3+ home runs
			GameGrandSlam = "game_grand_slam",                    // a grand slam
			GameWalkOff = "game_walkoff",                         // a walk-off win
			GameNoHitter = "game_no_hitter",                      // 노히트노런: the opponent 0 hits and 0 runs in 9+ innings (combined counts)
			GamePerfect = "game_perfect",                         // 퍼펙트: no opponent on base at all in 9+ innings (also a no-hitter)
			GameShutout = "game_shutout",                         // 완봉승: one pitcher the whole game, 0 runs
			GameNoHitterSolo = "game_no_hitter_solo",             // 노히트노런 by the starter alone (2026-11-05; the above is the 팀 one)
			GamePerfectSolo = "game_perfect_solo",                // 퍼펙트게임 by the starter alone
			GameComplete = "game_complete",                       // 완투승: one pitcher the whole game, a win
			GameTwelveK = "game_12k",                             // one pitcher 12+ strikeouts
			GameComeback5 = "game_comeback5";                     // a win after trailing by 5+ runs

		// 업적 다양화 (user, 2026-10-22). Season records, the post-season, promotion and awards are counted once per
		// season by Achievements.OnSeasonClosed; ExhibitionHardWins by ExhibitionHandlers (어려움 = Grade 2);
		// ExhibitionDayBest is today's wins at their best (Add); TierReached is the highest league reached (0-4, 챔피언스 4).
		public const string ExhibitionHardWins = "exhibition_hard_wins", ExhibitionDayBest = "exhibition_day_best",
			SeasonTeamHomers200 = "season_team_hr200", SeasonTeamAvg300 = "season_team_avg300", SeasonTeamEra350 = "season_team_era350",
			SeasonWins100 = "season_wins100", Season40Homers = "season_40hr", Season50Homers = "season_50hr", Season400 = "season_400",
			Season200Hits = "season_200h", Season3030 = "season_3030", Season20Wins = "season_20w", Season40Saves = "season_40sv",
			Season200K = "season_200k", PostseasonIn = "postseason_in", KoreanSeriesIn = "ks_in", KoreanSeriesWon = "ks_won",
			MajorKoreanSeriesWon = "major_ks_won", ChampionsKoreanSeriesWon = "champions_ks_won", Promotions = "promotions", TierReached = "tier_reached",
			LegendKoreanSeriesWon = "legend_ks_won",
			AwardTitles = "award_titles", AwardTitlesSeason = "award_titles_season", AwardMvp = "award_mvp", AwardKsMvp = "award_ks_mvp",
			TripleCrownBatter = "triple_crown_bat", TripleCrownPitcher = "triple_crown_pit",
			TeamColourNames = "team_color_names", Lessons = "lessons", LessonsTop = "lessons_top", ConditionItems = "condition_items";

		private sealed class Daily
		{
			public int Category, Index, Level, Goal;
			public string Key, Title, GoalText, RecordText;
		}

		// Category = the card on the screen (0 구단 … 5 육성 in the original; titles in Korean.txt).
		private static readonly Daily[] Today =
		{
			new Daily { Category = 0, Index = 1, Level = 1, Goal = 1, Key = Login, Title = "출석", GoalText = "오늘 게임에 접속하세요.", RecordText = "오늘 접속 {0}회" },
			new Daily { Category = 1, Index = 2, Level = 4, Goal = 250, Key = LeagueHits, Title = "타격", GoalText = "리그 경기에서 안타 250개를 치세요. (결과를 확인한 경기)", RecordText = "리그 안타 {0}개" },
			new Daily { Category = 2, Index = 3, Level = 4, Goal = 200, Key = LeagueStrikeouts, Title = "투구", GoalText = "리그 경기에서 탈삼진 200개를 잡으세요. (결과를 확인한 경기)", RecordText = "리그 탈삼진 {0}개" },
			new Daily { Category = 3, Index = 4, Level = 3, Goal = 2, Key = Exhibitions, Title = "시범경기", GoalText = "시범경기를 2번 치르세요.", RecordText = "시범경기 {0}회" },
			new Daily { Category = 4, Index = 5, Level = 2, Goal = 1, Key = CardPacksBought, Title = "카드팩", GoalText = "상점에서 카드팩(선수·코칭스태프)을 1개 이상 사세요.", RecordText = "카드팩 구매 {0}개" },
			new Daily { Category = 5, Index = 6, Level = 2, Goal = 1, Key = TrainingPacksBought, Title = "트레이닝", GoalText = "상점에서 트레이닝 카드팩을 1개 이상 사세요.", RecordText = "트레이닝팩 구매 {0}개" },
		};

		// MP by stars (user, 2026-10-20: "하루 38만은 너무 적소" → 370,000 + the same for all six). The 난이도
		// table on the screen reads them from the table's level rows (category 99). Goals stay at 255 or
		// below: the reply's record is a byte.
		// ×1.5 since 2026-11-14 (user 09:3x 결정 4: "미션 보상을 1.5배 늘립시다"; was 30,000 40,000 60,000 100,000 150,000): the six of a
		// day pay 555,000 (was 370,000) and the all-clear the same again.
		public static readonly int[] LevelMp = { 0, 45_000, 60_000, 90_000, 150_000, 225_000 };

		private const int LevelRowCategory = 99, LevelRowIndex = 90;

		// mission_claim row for the all-clear bonus.
		private const int AllClearCategory = 6;

		private static string s_cs;

		private static Database s_db;

		public static void Register(GameLoop loop, Database db, string dataDir)
		{
			s_db = db;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			Execute(@"CREATE TABLE IF NOT EXISTS mission_counter (user_code INTEGER NOT NULL, key TEXT NOT NULL, value INTEGER NOT NULL, PRIMARY KEY (user_code, key));
				CREATE TABLE IF NOT EXISTS mission_daily (user_code INTEGER NOT NULL, day TEXT NOT NULL, key TEXT NOT NULL, value INTEGER NOT NULL, PRIMARY KEY (user_code, day, key));
				CREATE TABLE IF NOT EXISTS mission_claim (user_code INTEGER NOT NULL, day TEXT NOT NULL, category INTEGER NOT NULL, mp INTEGER NOT NULL, PRIMARY KEY (user_code, day, category))");
			// Old days are only kept for a week.
			string cut = Day(DateTime.Now.AddDays(-7));
			Execute($"DELETE FROM mission_daily WHERE day < '{cut}'; DELETE FROM mission_claim WHERE day < '{cut}'");
			// 레전드 리그 (2026-11-08): TierReached counts in the six-tier numbering from now on (레전드 4, 챔피언스 5; Reached), so the
			// highest league of every team moves up one from 메이저 once (league_fix 'tier_reached6'): today's 메이저 counts as 레전드.
			Execute(@"CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL);
				UPDATE mission_counter SET value = value + 1 WHERE key = 'tier_reached' AND value >= 3 AND NOT EXISTS (SELECT 1 FROM league_fix WHERE name = 'tier_reached6');
				INSERT OR IGNORE INTO league_fix (name, done) VALUES ('tier_reached6', datetime('now', 'localtime'))");
			WriteTable(Path.Combine(dataDir, "tables", "dailymission.txt"));
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_DAILY_MISSION>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_GET_DAILY_MISSION_REWARD>(Reward);
			// 0시 자동 지급: every minute on the game loop (the first a minute after the start, once 업적 and the rest are registered),
			// which also pays the days the server was down for.
			StartAutoPay();
			s_dayTimer = new System.Threading.Timer(_ => loop.Post(() => Sweep(DateTime.Now)), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
		}

		// 0시 자동 지급 (user, 2026-11-14 09:3x 결정 4: "0시에 못 받은 보상을 자동으로 주게 합시다"; AI tester autotest7 lost a cleared
		// 6만 at midnight): once a day is over, every mission cleared that day and not taken is paid as [보상받기] pays it, and the
		// all-clear bonus when all six are then taken; the lobby says so once (DeliverAutoPay). From the day this started
		// (league_fix 'missions_autopay_from') — the days before stay as they were. Each day is swept once ('missions_autopay_<day>').
		private static System.Threading.Timer s_dayTimer;

		private static string s_autoPayFrom;

		private static void StartAutoPay()
		{
			Execute(@"CREATE TABLE IF NOT EXISTS mission_autopay (user_code INTEGER NOT NULL, day TEXT NOT NULL, mp INTEGER NOT NULL, shown INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (user_code, day));
				CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)");
			Execute($"INSERT OR IGNORE INTO league_fix (name, done) VALUES ('missions_autopay_from', '{Day(DateTime.Now)}')");
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT done FROM league_fix WHERE name = 'missions_autopay_from'";
			s_autoPayFrom = cmd.ExecuteScalar() as string ?? Day(DateTime.Now);
		}

		// Pays the days before `now`'s that are not swept yet; returns the MP paid (tests).
		internal static int Sweep(DateTime now)
		{
			if (s_cs == null || s_autoPayFrom == null) return 0;
			string today = Day(now);
			List<string> days = new List<string>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"SELECT DISTINCT day FROM mission_daily WHERE day < $t AND day >= $f
					AND NOT EXISTS (SELECT 1 FROM league_fix WHERE name = 'missions_autopay_' || mission_daily.day) ORDER BY day";
				cmd.Parameters.AddWithValue("$t", today);
				cmd.Parameters.AddWithValue("$f", s_autoPayFrom);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) days.Add(r.GetString(0));
			}
			int total = 0;
			foreach (string day in days)
			{
				List<uint> users = new List<uint>();
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT DISTINCT user_code FROM mission_daily WHERE day = $d";
					cmd.Parameters.AddWithValue("$d", day);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) users.Add((uint)r.GetInt64(0));
				}
				int paidDay = 0, teams = 0;
				foreach (uint user in users)
				{
					int paid = PayDay(user, day);
					if (paid > 0)
					{
						paidDay += paid;
						teams++;
					}
				}
				Execute($"INSERT OR IGNORE INTO league_fix (name, done) VALUES ('missions_autopay_{day}', datetime('now', 'localtime'))");
				ServerLog.Info($"오늘의 미션 자동 지급 {day}: {teams}개 구단 {paidDay:N0} MP");
				total += paidDay;
			}
			return total;
		}

		// One team's unclaimed rewards of a day that is over: the MP paid (0 if none).
		private static int PayDay(uint user, string day)
		{
			Dictionary<int, int> claims = new Dictionary<int, int>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT category, mp FROM mission_claim WHERE user_code = $u AND day = $d";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$d", day);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) claims[r.GetInt32(0)] = r.GetInt32(1);
			}
			int paid = 0;
			foreach (Daily d in Today)
			{
				if (claims.ContainsKey(d.Category) || Read("SELECT value FROM mission_daily WHERE user_code = $u AND day = $d AND key = $k", user, d.Key, day) < d.Goal)
				{
					continue;
				}
				int mp = LevelMp[d.Level];
				if (ClaimOn(user, day, d.Category, mp))
				{
					claims[d.Category] = mp;
					paid += mp;
				}
			}
			if (paid > 0 && !claims.ContainsKey(AllClearCategory) && Today.All(x => claims.ContainsKey(x.Category)))
			{
				int bonus = claims.Where(kv => kv.Key != AllClearCategory).Sum(kv => kv.Value);
				if (ClaimOn(user, day, AllClearCategory, bonus))
				{
					paid += bonus;
					// 업적 미션 올클리어 counts it (the total only: the day is over).
					using SqliteConnection c = Open();
					using SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "INSERT INTO mission_counter (user_code, key, value) VALUES ($u, $k, 1) ON CONFLICT (user_code, key) DO UPDATE SET value = value + 1";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$k", AllClears);
					cmd.ExecuteNonQuery();
				}
			}
			if (paid <= 0) return 0;
			s_db.AddMp(user, paid, MpLedger.Mission);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "INSERT OR IGNORE INTO mission_autopay (user_code, day, mp, shown) VALUES ($u, $d, $m, 0)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$d", day);
				cmd.Parameters.AddWithValue("$m", paid);
				cmd.ExecuteNonQuery();
			}
			Achievements.Evaluate(user);
			ServerLog.Info($"오늘의 미션 자동 지급 user {user} {day}: +{paid:N0} MP");
			return paid;
		}

		// True when this day's row was new (a [보상받기] at the same moment cannot pay twice).
		private static bool ClaimOn(uint user, string day, int category, int mp)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR IGNORE INTO mission_claim (user_code, day, category, mp) VALUES ($u, $d, $c, $m)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$d", day);
			cmd.Parameters.AddWithValue("$c", category);
			cmd.Parameters.AddWithValue("$m", mp);
			return cmd.ExecuteNonQuery() == 1;
		}

		// At the lobby (LobbyHandlers): the days paid by themselves since the last visit, once.
		public static void DeliverAutoPay(GameSession session)
		{
			if (session.Team == null || s_cs == null || s_autoPayFrom == null) return;
			List<(string day, int mp)> rows = new List<(string, int)>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT day, mp FROM mission_autopay WHERE user_code = $u AND shown = 0 ORDER BY day";
				cmd.Parameters.AddWithValue("$u", (long)session.Team.UserCode);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1)));
			}
			if (rows.Count == 0) return;
			Execute($"UPDATE mission_autopay SET shown = 1 WHERE user_code = {(long)session.Team.UserCode} AND shown = 0");
			List<string> lines = new List<string> { "받지 않은 오늘의 미션 보상을" };
			foreach ((string day, int mp) in rows.Skip(Math.Max(0, rows.Count - 3)))
			{
				DateTime d = DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
				lines.Add($"{d.Month}월 {d.Day}일 +{mp:N0} MP");
			}
			lines.Add("자정이 지나 자동으로 넣었습니다.");
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[] { new PLongString { str = "오늘의 미션" }, new PLongString { str = string.Join("\n", lines) } };
			session.Send(popup);
			session.Team.Mp = s_db.AddMp(session.Team.UserCode, 0);
			TopBar.Send(session);
			ServerLog.Info($"[{session.Id}] mission autopay popup: {string.Join(" / ", lines)}");
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static void Execute(string sql)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = sql;
			cmd.ExecuteNonQuery();
		}

		private static string Day(DateTime t) => t.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		// The client's DailyMissionTableReader: index, category, title, level, goal, (reward), condition, record text.
		private static void WriteTable(string path)
		{
			StringBuilder sb = new StringBuilder("index\tcategory\ttitle\tlevel\tgoal\treward\tcondition_num\trecord\n");
			foreach (Daily d in Today)
			{
				sb.Append($"{d.Index}\t{d.Category}\t{d.Title}\t{d.Level}\t{d.GoalText}\t{LevelMp[d.Level]}\t{d.Goal}\t{d.RecordText}\n");
			}
			// The reward per star for the screen's 난이도 table (rows 91–95; no mission uses them).
			for (int level = 1; level < LevelMp.Length; level++)
			{
				sb.Append($"{LevelRowIndex + level}\t{LevelRowCategory}\t보상\t{level}\t-\t{LevelMp[level]}\t0\t-\n");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
		}

		// Adds to a counter (all time and today) and checks the 업적.
		public static void Add(uint user, string key, int amount = 1)
		{
			if (s_cs == null || user == 0 || amount <= 0)
			{
				return;
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"INSERT INTO mission_counter (user_code, key, value) VALUES ($u, $k, $n) ON CONFLICT (user_code, key) DO UPDATE SET value = value + $n;
					INSERT INTO mission_daily (user_code, day, key, value) VALUES ($u, $d, $k, $n) ON CONFLICT (user_code, day, key) DO UPDATE SET value = value + $n";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$k", key);
				cmd.Parameters.AddWithValue("$n", amount);
				cmd.Parameters.AddWithValue("$d", Day(DateTime.Now));
				cmd.ExecuteNonQuery();
			}
			if (key == ExhibitionWins)
			{
				// 업적 하루 7승 / 10전 10승: the day's wins at their best.
				SetAtLeast(user, ExhibitionDayBest, TodayCount(user, ExhibitionWins));
			}
			Achievements.Evaluate(user);
		}

		public static int Total(uint user, string key) => Read("SELECT value FROM mission_counter WHERE user_code = $u AND key = $k", user, key, null);

		public static int TodayCount(uint user, string key) => Read("SELECT value FROM mission_daily WHERE user_code = $u AND day = $d AND key = $k", user, key, Day(DateTime.Now));

		private static int Read(string sql, uint user, string key, string day)
		{
			if (s_cs == null) return 0;
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$k", key);
			if (day != null) cmd.Parameters.AddWithValue("$d", day);
			object v = cmd.ExecuteScalar();
			return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
		}

		private const string Backfilled = "backfill_v1";

		// A league tier as TierReached counts it: the six-tier numbering (before the switch 메이저 3 → 4, 챔피언스 4 → 5).
		// Seven tiers (더블A, 2026-11-14): still the six-tier numbering, 더블A counting as 루키 (no achievement of its own yet).
		public static int Reached(int tier) => LeagueRules.Seven ? LeagueRules.ToSix(tier) : LeagueRules.NewStructure ? tier : LeagueRules.ToNew(tier);

		public static void SetAtLeast(uint user, string key, int value)
		{
			if (s_cs == null || user == 0) return;
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT INTO mission_counter (user_code, key, value) VALUES ($u, $k, $n) ON CONFLICT (user_code, key) DO UPDATE SET value = MAX(value, $n)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$k", key);
			cmd.Parameters.AddWithValue("$n", value);
			cmd.ExecuteNonQuery();
		}

		// The 업적 started counting on 2026-10-20 19:41 (user: what was done before must pay too). Once per
		// team, what the records still hold is read back: league wins and home runs from the confirmed
		// games of every season, finished seasons from the season number, cards from the shop's count.
		// The rest (exhibitions, trainings, contracts, fusions, logins) has no history and starts then.
		internal static void Backfill(uint user)
		{
			if (Total(user, Backfilled) > 0 || LeagueService.Store == null)
			{
				return;
			}
			int wins = 0, homeRuns = 0;
			LeagueState s = LeagueService.Store.Load(user);
			if (s != null)
			{
				for (int season = 1; season <= s.Season; season++)
				{
					foreach (LeagueGame g in LeagueService.Store.Games(user, season).Where(g => g.Confirmed))
					{
						int side = g.Detail?.Owner == null ? -1 : g.Detail.Owner[0] == user ? 0 : g.Detail.Owner[1] == user ? 1 : -1;
						if (side < 0 || g.Detail.Stats == null) continue;
						homeRuns += g.Detail.Stats[side][3];
						if ((side == 0 ? g.HomeScore : g.AwayScore) > (side == 0 ? g.AwayScore : g.HomeScore)) wins++;
					}
				}
				SetAtLeast(user, SeasonsDone, s.Season - 1);
			}
			SetAtLeast(user, LeagueWins, wins);
			SetAtLeast(user, LeagueHomeRuns, homeRuns);
			int cards = 0;
			try
			{
				cards = ShopHandlers.CardsBought(user);
			}
			catch (SqliteException)
			{
				// The shop's table is not there yet (a self-test before the shop's).
			}
			SetAtLeast(user, CardPacksOpened, cards);
			SetAtLeast(user, Backfilled, 1);
			ServerLog.Info($"업적 지난 기록 user {user}: 리그 {wins}승, 홈런 {homeRuns}, 완주 시즌 {(s == null ? 0 : s.Season - 1)}, 상점 카드 {cards}장");
			Achievements.Evaluate(user);
		}

		// The first lobby visit of the day: 출석 (and one more day for the 출석 업적).
		public static void OnLogin(uint user)
		{
			Backfill(user);
			// 업적 승격: the league the team plays in now counts as reached (teams promoted before the 업적 existed).
			LeagueState league = LeagueService.Store?.Load(user);
			if (league != null && Total(user, TierReached) < Reached(league.Tier))
			{
				SetAtLeast(user, TierReached, Reached(league.Tier));
				Achievements.Evaluate(user);
			}
			// 팀컬러 업적 for a registration made before they existed.
			TeamColors.RecordForAchievements(user);
			if (TodayCount(user, Login) > 0)
			{
				return;
			}
			Add(user, Login);
			Add(user, LoginDays);
		}

		// The user's confirmed league games (LeagueService.ConfirmAndPay): hits, strikeouts by the
		// user's pitchers, wins and home runs.
		public static void OnLeagueGames(uint user, IEnumerable<LeagueGame> games)
		{
			int hits = 0, strikeouts = 0, wins = 0, homeRuns = 0;
			Dictionary<string, int> moments = new Dictionary<string, int>();
			foreach (LeagueGame g in games)
			{
				int side = g.Detail?.Owner == null ? -1 : g.Detail.Owner[0] == user ? 0 : g.Detail.Owner[1] == user ? 1 : -1;
				if (side < 0 || g.Detail.Stats == null) continue;
				hits += g.Detail.Stats[side][0];
				homeRuns += g.Detail.Stats[side][3];
				strikeouts += g.Detail.Stats[1 - side][5];
				int mine = side == 0 ? g.HomeScore : g.AwayScore, theirs = side == 0 ? g.AwayScore : g.HomeScore;
				if (mine > theirs) wins++;
				// 경기 순간 업적 (the engine's GameDetail.Moments; games stored before them have none).
				string[] marks = g.Detail.Moments != null && g.Detail.Moments.Length > side ? g.Detail.Moments[side] : null;
				foreach (string key in marks ?? Array.Empty<string>())
				{
					moments[key] = moments.GetValueOrDefault(key) + 1;
				}
			}
			Add(user, LeagueHits, hits);
			Add(user, LeagueStrikeouts, strikeouts);
			Add(user, LeagueWins, wins);
			Add(user, LeagueHomeRuns, homeRuns);
			foreach (KeyValuePair<string, int> m in moments)
			{
				Add(user, m.Key, m.Value);
			}
		}

		private static Dictionary<int, int> Claims(uint user)
		{
			Dictionary<int, int> claims = new Dictionary<int, int>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT category, mp FROM mission_claim WHERE user_code = $u AND day = $d";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$d", Day(DateTime.Now));
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) claims[r.GetInt32(0)] = r.GetInt32(1);
			return claims;
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_DAILY_MISSION packet)
		{
			Ma9MPacket.PKS_SC_ENTER_DAILY_MISSION reply = new Ma9MPacket.PKS_SC_ENTER_DAILY_MISSION();
			if (session.Team == null)
			{
				session.Send(reply);
				return;
			}
			uint user = session.Team.UserCode;
			Dictionary<int, int> claims = Claims(user);
			(PInteger mission, PByte clear, PByte record)[] slots =
			{
				(reply.mission_club, reply.clear_club, reply.record_club),
				(reply.mission_batter, reply.clear_batter, reply.record_batter),
				(reply.mission_pitcher, reply.clear_pitcher, reply.record_pitcher),
				(reply.mission_competition, reply.clear_competition, reply.record_competition),
				(reply.mission_social, reply.clear_social, reply.record_social),
				(reply.mission_foster, reply.clear_foster, reply.record_foster),
			};
			foreach (Daily d in Today)
			{
				int count = TodayCount(user, d.Key);
				slots[d.Category].mission.n = d.Index;
				slots[d.Category].record.n = (byte)Math.Min(255, count);
				// 0 not yet, 1 cleared (보상받기), 2 reward taken.
				slots[d.Category].clear.n = (byte)(claims.ContainsKey(d.Category) ? 2 : count >= d.Goal ? 1 : 0);
			}
			reply.today_earned_point.n = claims.Values.Sum();
			session.Send(reply);
		}

		// 빨간 점 (user, 2026-11-10 20:0x: "일일 미션 중에서 깬 미션이 있을 경우, 특성 관리 붉은 점처럼 추가해주길 바람"): a mission of today
		// cleared and its reward not taken yet (보상받기). The all-clear bonus comes with the last reward, so it needs no check.
		public static bool HasClaimable(uint user)
		{
			Dictionary<int, int> claims = Claims(user);
			return Today.Any(d => !claims.ContainsKey(d.Category) && TodayCount(user, d.Key) >= d.Goal);
		}

		private static void Reward(GameSession session, Ma9MPacket.PKS_CS_GET_DAILY_MISSION_REWARD packet)
		{
			Ma9MPacket.PKS_SC_GET_DAILY_MISSION_REWARD reply = new Ma9MPacket.PKS_SC_GET_DAILY_MISSION_REWARD();
			int category = packet.mission_category.n;
			reply.mission_category.n = packet.mission_category.n;
			Daily d = Today.FirstOrDefault(x => x.Category == category);
			if (session.Team == null || d == null)
			{
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			uint user = session.Team.UserCode;
			Dictionary<int, int> claims = Claims(user);
			if (claims.ContainsKey(category) || TodayCount(user, d.Key) < d.Goal)
			{
				// Already taken (a double tap): the balance as it is, no error screen.
				reply.result.n = (byte)(claims.ContainsKey(category) ? 0 : 1);
				reply.cur_point.n = s_db.AddMp(user, 0);
				session.Send(reply);
				return;
			}
			int mp = LevelMp[d.Level];
			Claim(user, category, mp);
			int balance = s_db.AddMp(user, mp, MpLedger.Mission);
			string bonusText = string.Empty;
			int allClear = 0;
			if (Today.All(x => x.Category == category || claims.ContainsKey(x.Category)))
			{
				// All six: what they paid, once more.
				int bonus = claims.Where(kv => kv.Key != AllClearCategory).Sum(kv => kv.Value) + mp;
				Claim(user, AllClearCategory, bonus);
				balance = s_db.AddMp(user, bonus, MpLedger.Mission);
				bonusText = $", all clear +{bonus:N0}";
				Add(user, AllClears);
				allClear = bonus;
			}
			session.Team.Mp = balance;
			reply.result.n = 0;
			reply.cur_point.n = balance;
			session.Send(reply);
			if (allClear > 0)
			{
				// The bonus said in words (AI tester kt 47, 11-12: "올클리어 보너스 +○○ MP 한 줄이 있으면"); the app only plays its effect.
				// A type-1 popup: its OK (POPUP_ACK 0) is ignored by AdviceHandlers.Ack.
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[]
				{
					new PLongString { str = "오늘의 미션 모두 달성" },
					new PLongString { str = $"미션 {Today.Length}개를 모두 달성해\n오늘 받은 미션 MP만큼 한 번 더 드렸습니다.\n보너스 +{allClear:N0} MP" }
				};
				session.Send(popup);
			}
			ServerLog.Info($"[{session.Id}] 오늘의 미션 {d.Title}: +{mp:N0} MP{bonusText}");
		}

		private static void Claim(uint user, int category, int mp)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR IGNORE INTO mission_claim (user_code, day, category, mp) VALUES ($u, $d, $c, $m)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$d", Day(DateTime.Now));
			cmd.Parameters.AddWithValue("$c", category);
			cmd.Parameters.AddWithValue("$m", mp);
			cmd.ExecuteNonQuery();
		}

		// Dev (--missionpaytest, a copy of the data only, 2026-11-14): 0시 자동 지급 on the test team — yesterday all six cleared and
		// none taken (six + the all-clear), the day before three cleared (those three only), and a second sweep paying nothing.
		public static int AutoPaySelfTest(string email)
		{
			uint user;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = $e AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				cmd.Parameters.AddWithValue("$e", email);
				object found = cmd.ExecuteScalar();
				if (found == null || found is DBNull)
				{
					ServerLog.Info("[missionpaytest] FAIL no test team");
					return 1;
				}
				user = (uint)(long)found;
			}
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[missionpaytest] {(ok ? "ok  " : "FAIL")} {what}");
			}
			string d1 = Day(DateTime.Now.AddDays(-1)), d2 = Day(DateTime.Now.AddDays(-2));
			Execute($@"DELETE FROM mission_daily WHERE user_code = {(long)user} AND day IN ('{d1}', '{d2}');
				DELETE FROM mission_claim WHERE user_code = {(long)user} AND day IN ('{d1}', '{d2}');
				DELETE FROM mission_autopay WHERE user_code = {(long)user};
				DELETE FROM league_fix WHERE name IN ('missions_autopay_{d1}', 'missions_autopay_{d2}');
				UPDATE league_fix SET done = '{d2}' WHERE name = 'missions_autopay_from'");
			s_autoPayFrom = d2;
			foreach (Daily d in Today)
			{
				Execute($"INSERT INTO mission_daily (user_code, day, key, value) VALUES ({(long)user}, '{d1}', '{d.Key}', {d.Goal})");
				if (d.Category <= 2)
				{
					Execute($"INSERT INTO mission_daily (user_code, day, key, value) VALUES ({(long)user}, '{d2}', '{d.Key}', {d.Goal})");
				}
			}
			int six = Today.Sum(d => LevelMp[d.Level]), three = Today.Where(d => d.Category <= 2).Sum(d => LevelMp[d.Level]);
			int mp0 = s_db.AddMp(user, 0), clears0 = Total(user, AllClears);
			Sweep(DateTime.Now);
			int mp1 = s_db.AddMp(user, 0);
			Check(mp1 - mp0 == three + 2 * six, $"MP +{mp1 - mp0:N0} (그제 3칸 {three:N0} + 어제 6칸 {six:N0} + 올클리어 {six:N0})");
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = $"SELECT day, COUNT(*), SUM(mp) FROM mission_claim WHERE user_code = {(long)user} AND day IN ('{d1}', '{d2}') GROUP BY day ORDER BY day";
				using SqliteDataReader r = cmd.ExecuteReader();
				List<string> got = new List<string>();
				while (r.Read()) got.Add($"{r.GetString(0)} {r.GetInt32(1)}칸 {r.GetInt64(2):N0}");
				Check(got.Count == 2 && got[0].Contains(" 3칸 ") && got[1].Contains(" 7칸 "), "받은 칸: " + string.Join(" / ", got));
			}
			Check(Total(user, AllClears) == clears0 + 1, $"올클리어 누계 {clears0} → {Total(user, AllClears)}");
			int again = Sweep(DateTime.Now);
			Check(again == 0 && s_db.AddMp(user, 0) == mp1, $"두 번째 지급 {again:N0} MP (0이어야 함)");
			return problems;
		}

		// Self-test on a data copy: every mission's counter reaches its goal.
		public static int SelfTest(uint user, out string line)
		{
			foreach (Daily d in Today)
			{
				Add(user, d.Key, d.Goal);
			}
			bool ok = Today.All(d => TodayCount(user, d.Key) >= d.Goal);
			line = $"오늘의 미션: 카운터 {(ok ? "ok" : "FAIL")}, 보상 합계 {Today.Sum(d => LevelMp[d.Level]):N0} MP (+ 올클리어 같은 만큼)";
			return ok ? 0 : 1;
		}
	}
}
