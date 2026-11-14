using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 새벽 자동 시범경기 (user, 2026-11-03: "사용자가 나밖에 없으니까 매일 새벽 시간대에 자동으로 … 내가 자고 있는 동안
	// 계정들 시범경기 10판씩 다 돌리고 싶어"; systems session). From AutoFrom every club of a real account (not the
	// autotest ones) plays the day's tries it has left, at the difficulty it chose last (어려움 when it never played),
	// with the same opponents, MP, missions and prize cards as by hand: one game every AutoEvery on the game loop. A
	// match tried or played by hand and left without its result is settled first (its try was spent already), so it is
	// never overwritten. A club in use (a packet within AutoIdle) waits until it has been quiet that long; after
	// AutoUntil the day is left as it is. The day's games add up in exhibition_auto, and the next lobby visit shows
	// them in a popup (LobbyHandlers → DeliverAuto). Live server only (game port 18000), unless MA9M_EXHIBITION_AUTO
	// is 1 (0 turns it off).
	public static partial class ExhibitionHandlers
	{
		public static readonly TimeSpan AutoFrom = TimeSpan.FromHours(5), AutoUntil = TimeSpan.FromHours(9);

		private static readonly TimeSpan AutoIdle = TimeSpan.FromMinutes(20);

		private const int AutoEvery = 5_000;  // ms between two games

		private const int AutoDefaultGrade = 2;  // 어려움: every club of the user's played it (2026-11-03)

		private static readonly string[] GradeNames = { "쉬움", "보통", "어려움" };

		// The latest packet of each club and its session (GameLoop.AfterPacket), for "in use" and a connected club's MP.
		private static readonly Dictionary<uint, (DateTime at, GameSession session)> s_seen = new Dictionary<uint, (DateTime, GameSession)>();

		private static System.Threading.Timer s_autoTimer;

		private static DateTime s_autoDone;  // the day every club was done

		// Clubs whose game failed (logged once): left alone for the rest of that day, not retried every few seconds.
		private static readonly Dictionary<uint, DateTime> s_autoFailed = new Dictionary<uint, DateTime>();

		public static void StartAuto(GameLoop loop)
		{
			string flag = Environment.GetEnvironmentVariable("MA9M_EXHIBITION_AUTO");
			if (s_cs == null || (flag != null ? flag == "0" : Program.GamePort != 18000))
			{
				ServerLog.Info("exhibition auto: off");
				return;
			}
			AutoTable();
			GameLoop.AfterPacket += session =>
			{
				if (session.Team != null) s_seen[session.Team.UserCode] = (DateTime.Now, session);
			};
			s_autoTimer = new System.Threading.Timer(_ => loop.Post(() => AutoTick(DateTime.Now)), null, AutoEvery, AutoEvery);
			ServerLog.Info($"exhibition auto: daily {AutoFrom:hh\\:mm}-{AutoUntil:hh\\:mm}, clubs quiet for {AutoIdle.TotalMinutes:F0} min");
		}

		private static void AutoTable() => Execute(@"CREATE TABLE IF NOT EXISTS exhibition_auto (
			user_code INTEGER NOT NULL,
			day TEXT NOT NULL,
			grade INTEGER NOT NULL,
			games INTEGER NOT NULL DEFAULT 0,
			wins INTEGER NOT NULL DEFAULT 0,
			ties INTEGER NOT NULL DEFAULT 0,
			losses INTEGER NOT NULL DEFAULT 0,
			mp INTEGER NOT NULL DEFAULT 0,
			scores TEXT NOT NULL DEFAULT '',
			cards TEXT NOT NULL DEFAULT '',
			carried INTEGER NOT NULL DEFAULT 0,
			shown INTEGER NOT NULL DEFAULT 0,
			PRIMARY KEY (user_code, day))");

		private static string DayText(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

		// Clubs of real accounts that have a 1군, and the 자급자족 tester (user, 2026-11-13 16:5x 결정 1: autotest7 plays as an ordinary
		// user on START2026 alone, to measure how long the game lasts without coupons; the other testers stay out).
		public const string SelfSufficientTester = "autotest7@ma9m.local";

		private static List<(uint code, string name)> AutoClubs()
		{
			List<(uint code, string name)> clubs = new List<(uint, string)>();
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = @"SELECT t.user_code, t.team_name FROM team t JOIN account a ON a.id = t.account_id
					WHERE (a.email NOT LIKE 'autotest%' OR a.email = $self) AND t.team_name <> '' ORDER BY t.user_code";
				cmd.Parameters.AddWithValue("$self", SelfSufficientTester);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) clubs.Add(((uint)r.GetInt64(0), r.GetString(1)));
			}
			return clubs.Where(t => s_db.GetLineup(t.code) != null).ToList();
		}

		// The difficulty of the club's latest exhibition (the last five are in exhibition_log).
		private static int LastGrade(uint code)
		{
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT info FROM exhibition_log WHERE user_code = $u ORDER BY rowid DESC LIMIT 1";
			cmd.Parameters.AddWithValue("$u", (long)code);
			return cmd.ExecuteScalar() is string info && JsonSerializer.Deserialize<Saved>(info) is Saved s ? Math.Clamp(s.Grade, 0, 2) : AutoDefaultGrade;
		}

		// A match tried (or played) and left without its result.
		private static bool Unfinished(uint code, out Match match) => TryGet(code, out match) && !match.Played;

		// One game of the first club with something to play that is not in use; the day is done when none is left.
		private static void AutoTick(DateTime now)
		{
			if (now.TimeOfDay < AutoFrom || now.TimeOfDay >= AutoUntil || s_autoDone == now.Date)
			{
				return;
			}
			bool waiting = false;
			foreach ((uint code, string name) in AutoClubs())
			{
				if ((TriesLeft(code) <= 0 && !Unfinished(code, out _)) || (s_autoFailed.TryGetValue(code, out DateTime failed) && failed == now.Date))
				{
					continue;
				}
				if (s_seen.TryGetValue(code, out var seen) && now - seen.at < AutoIdle)
				{
					waiting = true;
					continue;
				}
				try
				{
					AutoGame(code, name, now);
				}
				catch (Exception ex)
				{
					s_autoFailed[code] = now.Date;
					ServerLog.Warn($"[auto] exhibition {name} failed, left for today: {ex}");
				}
				return;
			}
			if (!waiting)
			{
				s_autoDone = now.Date;
				ServerLog.Info("exhibition auto: all clubs done for today");
			}
		}

		private static void AutoGame(uint code, string name, DateTime now)
		{
			Lineup mine = s_db.GetLineup(code);
			bool carried = Unfinished(code, out Match match);
			if (!carried)
			{
				match = NewMatch(code, mine, LastGrade(code));
			}
			if (match.Buff == null)
			{
				Play(code, name, mine, match, null, "[auto]");
			}
			int earned = Settle(code, match, out int prize, out _);
			// Off the memory too, so a client still holding this match (tried, not started) cannot start it again.
			s_matches.Remove(code);
			int balance = s_db.AddMp(code, earned, MpLedger.Exhibition);
			if (s_seen.TryGetValue(code, out var seen) && seen.session?.Team?.UserCode == code)
			{
				seen.session.Team.Mp = balance;
			}
			int result = match.HomeRuns > match.AwayRuns ? 0 : match.HomeRuns == match.AwayRuns ? 1 : 2;
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = @"INSERT INTO exhibition_auto (user_code, day, grade, games, wins, ties, losses, mp, scores, cards, carried)
					VALUES ($u, $d, $g, 1, $w, $t, $l, $mp, $s, $c, $car)
					ON CONFLICT (user_code, day) DO UPDATE SET games = games + 1, wins = wins + $w, ties = ties + $t, losses = losses + $l,
						mp = mp + $mp, scores = CASE WHEN scores = '' THEN $s ELSE scores || ' ' || $s END,
						cards = CASE WHEN $c = '' THEN cards WHEN cards = '' THEN $c ELSE cards || ',' || $c END,
						carried = carried + $car, shown = 0";
				cmd.Parameters.AddWithValue("$u", (long)code);
				cmd.Parameters.AddWithValue("$d", DayText(now));
				cmd.Parameters.AddWithValue("$g", match.Grade);
				cmd.Parameters.AddWithValue("$w", result == 0 ? 1 : 0);
				cmd.Parameters.AddWithValue("$t", result == 1 ? 1 : 0);
				cmd.Parameters.AddWithValue("$l", result == 2 ? 1 : 0);
				cmd.Parameters.AddWithValue("$mp", earned);
				cmd.Parameters.AddWithValue("$s", $"{match.HomeRuns}:{match.AwayRuns}");
				cmd.Parameters.AddWithValue("$c", prize != 0 ? prize.ToString(CultureInfo.InvariantCulture) : string.Empty);
				cmd.Parameters.AddWithValue("$car", carried ? 1 : 0);
				cmd.ExecuteNonQuery();
			}
			ServerLog.Info($"[auto] exhibition {name} ({GradeNames[match.Grade]}{(carried ? ", left from before" : string.Empty)}): {match.HomeRuns} : {match.AwayRuns} {match.AiName}, +{earned:N0} MP{(prize != 0 ? $", card {prize}" : string.Empty)}, {TriesLeft(code)} tries left");
		}

		private static string CardName(int idx) => Catalog.Get(idx) is CardInfo c ? $"{c.Year % 100:00} {c.Name}" : idx.ToString(CultureInfo.InvariantCulture);

		// The popups for the club's auto days not shown yet (the latest three; older ones are only marked), marked shown.
		// Each fits the type-1 popup (Panel_Popup1: 443 px wide, no wrapping, room for five lines between the title and
		// the button): short lines, at most four card names.
		private static List<(string title, string text)> AutoPopups(uint code)
		{
			List<(string day, int grade, int games, int w, int t, int l, int mp, string scores, string cards, int carried)> rows = new List<(string, int, int, int, int, int, int, string, string, int)>();
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT day, grade, games, wins, ties, losses, mp, scores, cards, carried FROM exhibition_auto WHERE user_code = $u AND shown = 0 AND games > 0 ORDER BY day";
				cmd.Parameters.AddWithValue("$u", (long)code);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) rows.Add((r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetInt32(9)));
			}
			if (rows.Count == 0) return new List<(string, string)>();
			Execute($"UPDATE exhibition_auto SET shown = 1 WHERE user_code = {(long)code} AND shown = 0");
			List<(string title, string text)> popups = new List<(string, string)>();
			foreach (var row in rows.Skip(Math.Max(0, rows.Count - 3)))
			{
				DateTime day = DateTime.ParseExact(row.day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
				List<string> lines = new List<string>
				{
					$"{day.Month}월 {day.Day}일 새벽 시범경기 {row.games}경기 ({GradeNames[Math.Clamp(row.grade, 0, 2)]})",
					$"{row.w}승 {row.t}무 {row.l}패, MP +{row.mp:N0}"
				};
				if (row.carried > 0) lines.Add($"(결과를 받지 않았던 {row.carried}경기 포함)");
				lines.Add(row.scores);
				List<string> cards = row.cards.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => CardName(int.Parse(s, CultureInfo.InvariantCulture))).ToList();
				if (cards.Count > 0)
				{
					lines.Add($"얻은 선수: {string.Join(", ", cards.Take(4))}{(cards.Count > 4 ? $" 외 {cards.Count - 4}명" : string.Empty)}");
				}
				popups.Add(("새벽 시범경기", string.Join("\n", lines)));
			}
			return popups;
		}

		// At the lobby (no league games waiting): the auto days not shown yet.
		public static void DeliverAuto(GameSession session)
		{
			if (session.Team == null || s_cs == null || s_autoTimer == null) return;
			bool cards = false;
			foreach ((string title, string text) in AutoPopups(session.Team.UserCode))
			{
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[] { new PLongString { str = title }, new PLongString { str = text } };
				session.Send(popup);
				cards |= text.Contains("얻은 선수:");
				ServerLog.Info($"[{session.Id}] exhibition auto popup: {text.Replace("\n", " / ")}");
			}
			// Prize cards of the night into a full storage (user 2026-11-13 결정 3): the same note as after a game by hand.
			if (cards && StorageFullPopup(session.Team.UserCode) is Ma9MPacket.PKS_SC_POPUP full)
			{
				session.Send(full);
			}
		}

		// Dev (--autoexhtest, a copy of the data only): the day's tries cleared, one club left with a match tried by hand
		// and one in use; then the clock at 04:59, 05:00 … as AutoTick sees it. Checks: nothing before 05:00 or from 09:00,
		// ten games a club, the left match settled first, the club in use untouched until it has been quiet 20 min, MP and
		// missions as by hand, no second round, and the popups once.
		public static int AutoSelfTest(string dbPath)
		{
			if (string.Equals(Path.GetFullPath(dbPath), Path.GetFullPath(@"C:\Ma9MProject\server\data\ma9m.db"), StringComparison.OrdinalIgnoreCase))
			{
				ServerLog.Warn("--autoexhtest writes to the DB: run it on a copy of the data");
				return 2;
			}
			AutoTable();
			Execute("DELETE FROM exhibition_tries");
			Execute("DELETE FROM exhibition_match");
			Execute("DELETE FROM exhibition_auto");
			s_tries.Clear();
			s_matches.Clear();
			List<(uint code, string name)> clubs = AutoClubs();
			if (clubs.Count < 3)
			{
				ServerLog.Warn($"--autoexhtest needs three clubs of real accounts ({clubs.Count})");
				return 2;
			}
			int fails = 0;
			void Check(bool ok, string what)
			{
				if (!ok) fails++;
				ServerLog.Info($"{(ok ? "ok  " : "FAIL")} {what}");
			}
			DateTime today = DateTime.Today;
			Dictionary<uint, int> mp0 = clubs.ToDictionary(t => t.code, t => s_db.AddMp(t.code, 0));
			Dictionary<uint, int> missions0 = clubs.ToDictionary(t => t.code, t => Missions.TodayCount(t.code, Missions.Exhibitions));
			(uint left, string leftName) = clubs[0];
			(uint busy, string busyName) = clubs[1];
			// A hand-tried match left without starting it (one of today's tries spent), and a club in use.
			NewMatch(left, s_db.GetLineup(left), 1);
			s_seen[busy] = (today + AutoFrom - TimeSpan.FromMinutes(1), null);
			AutoTick(today + AutoFrom - TimeSpan.FromMinutes(1));
			Check(clubs.All(t => TriesLeft(t.code) == (t.code == left ? TriesPerDay - 1 : TriesPerDay)), "04:59: nothing played");
			AutoTick(today + AutoUntil);
			Check(clubs.All(t => TriesLeft(t.code) == (t.code == left ? TriesPerDay - 1 : TriesPerDay)), "09:00: nothing played");
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			int ticks = 0;
			bool busyTouched = false;
			DateTime clock = today + AutoFrom, quiet = s_seen[busy].at + AutoIdle;
			for (; ticks < 2000 && s_autoDone != today; ticks++)
			{
				AutoTick(clock);
				if (clock < quiet && TriesLeft(busy) != TriesPerDay) busyTouched = true;
				clock += TimeSpan.FromSeconds(5);
			}
			int played = clubs.Sum(t => TriesPerDay - TriesLeft(t.code));
			Check(!busyTouched && s_autoDone == today && clubs.All(t => TriesLeft(t.code) == 0) && !Unfinished(left, out _),
				$"{ticks} ticks to {clock:HH:mm:ss}: {played} tries over {clubs.Count} clubs, {busyName} (in use) waited until {quiet:HH:mm}, {watch.ElapsedMilliseconds / Math.Max(1, played)} ms a game");
			Dictionary<uint, (int games, int w, int t, int l, int mp, int carried, string cards)> rows = new Dictionary<uint, (int, int, int, int, int, int, string)>();
			using (SqliteConnection c = new SqliteConnection(s_cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code, games, wins, ties, losses, mp, carried, cards FROM exhibition_auto WHERE day = $d";
				cmd.Parameters.AddWithValue("$d", DayText(today));
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) rows[(uint)r.GetInt64(0)] = (r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6), r.GetString(7));
			}
			foreach ((uint code, string name) in clubs)
			{
				rows.TryGetValue(code, out var row);
				int mp = s_db.AddMp(code, 0) - mp0[code];
				int missions = Missions.TodayCount(code, Missions.Exhibitions) - missions0[code];
				// The left match was one of today's ten.
				Check(row.games == TriesPerDay && row.w + row.t + row.l == row.games && row.mp == mp && missions == row.games && row.carried == (code == left ? 1 : 0),
					$"{name}: {row.games} games {row.w}-{row.t}-{row.l}, +{row.mp:N0} MP (balance +{mp:N0}), missions +{missions}, carried {row.carried}, cards [{row.cards}]");
			}
			s_autoDone = default;
			AutoTick(clock);
			Check(clubs.All(t => TriesLeft(t.code) == 0) && rows.Values.Sum(r => r.games) == clubs.Sum(t => TriesPerDay - TriesLeft(t.code)), "a second round plays nothing");
			// The popup: five lines at most, each narrower than the window (about 14 px a Hangul syllable, 7.5 px else, 420 px).
			static double Px(string line) => line.Sum(ch => ch >= '가' && ch <= '힣' ? 14 : 7.5);
			foreach ((uint code, string name) in new[] { (left, leftName), (busy, busyName) })
			{
				List<(string title, string text)> popups = AutoPopups(code);
				string[] lines = popups.FirstOrDefault().text?.Split('\n') ?? Array.Empty<string>();
				Check(popups.Count == 1 && lines.Length <= 5 && lines.All(l => Px(l) <= 420),
					$"popup for {name} ({lines.Length} lines, widest {(lines.Length > 0 ? lines.Max(Px) : 0):F0} px): {string.Join(" / ", lines)}");
				Check(AutoPopups(code).Count == 0, "  and only once");
			}
			ServerLog.Info($"auto exhibition self-test: {(fails == 0 ? "all ok" : $"{fails} FAILED")}");
			return fails == 0 ? 0 : 1;
		}
	}
}
