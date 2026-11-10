using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// 선발 5경기 제한 (user, 2026-11-09: "5경기 제한 규칙 만들기"; the original's rule behind the client's notice
	// Roster_PlayerMap_Change_SpBlock "1군에서 내려간 선발투수는 5경기 동안 선발투수 등록이 제한됩니다", which the server did not
	// keep until then): a pitcher the user takes out of the five starter slots (to the bullpen or out of the 1군) may not be put
	// back into a starter slot until his team has played 5 more league games. Counted on the league's latest game
	// (LeagueState.LastGame, postseason games too) within the season. No block while starters are free (between a club's
	// last game and the next season, LeagueService.StartersFree) or without a league. Only the user's own saves start a
	// block or are refused; lineups the server builds neither start nor obey it.
	public static class SpBlock
	{
		public const int Games = 5;

		private const int StarterSlots = 5;

		private static string s_cs;

		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS sp_block (
				user_code INTEGER NOT NULL,
				card_idx INTEGER NOT NULL,
				season INTEGER NOT NULL,
				from_game INTEGER NOT NULL,
				PRIMARY KEY (user_code, card_idx))";
			cmd.ExecuteNonQuery();
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static IEnumerable<int> Starters(Lineup l) => l.Pitchers.Take(StarterSlots).Where(i => i != 0);

		// The time the rule reads (SelfTest moves it into a season when the data copy's leagues are between seasons).
		private static Func<DateTime> s_clock = () => DateTime.Now;

		// Games left of a block set at game from of season, for the league as it is now (0 = over).
		internal static int Left(int season, int from, LeagueState s, DateTime now)
		{
			if (s == null || s.Season != season || LeagueService.StartersFree(s, now)) return 0;
			return Math.Max(0, Games - (s.LastGame - from));
		}

		// The team's blocked pitchers and the games each has left.
		public static Dictionary<int, int> Blocked(uint user)
		{
			Dictionary<int, int> blocked = new Dictionary<int, int>();
			LeagueState s = LeagueService.Store?.Load(user);
			if (s_cs == null || s == null) return blocked;
			DateTime now = s_clock();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, season, from_game FROM sp_block WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				int left = Left(r.GetInt32(1), r.GetInt32(2), s, now);
				if (left > 0) blocked[r.GetInt32(0)] = left;
			}
			return blocked;
		}

		// Why a user's save puts a blocked pitcher into a starter slot he was not in (null when it does not).
		public static string Problem(uint user, Lineup before, Lineup after, CardCatalog catalog)
		{
			HashSet<int> had = Starters(before).ToHashSet();
			List<int> back = Starters(after).Where(i => !had.Contains(i)).Distinct().ToList();
			if (back.Count == 0) return null;
			Dictionary<int, int> blocked = Blocked(user);
			List<int> refused = back.Where(blocked.ContainsKey).ToList();
			if (refused.Count == 0) return null;
			return $"1군 선발에서 내려간 투수는 {Games}경기 동안\n다시 선발로 넣을 수 없습니다.\n"
				+ string.Join(", ", refused.Select(i => $"{catalog?.Get(i)?.Name ?? i.ToString()} ({blocked[i]}경기 남음)"));
		}

		// After a user's save: the starters taken out are blocked from the league's latest game on.
		public static void Record(uint user, Lineup before, Lineup after, out List<int> started)
		{
			started = new List<int>();
			LeagueState s = LeagueService.Store?.Load(user);
			if (s_cs == null || s == null || LeagueService.StartersFree(s, s_clock())) return;
			HashSet<int> still = Starters(after).ToHashSet();
			started = Starters(before).Where(i => !still.Contains(i)).Distinct().ToList();
			if (started.Count == 0) return;
			using SqliteConnection c = Open();
			foreach (int idx in started)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "INSERT OR REPLACE INTO sp_block (user_code, card_idx, season, from_game) VALUES ($u, $i, $s, $g)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$g", s.LastGame);
				cmd.ExecuteNonQuery();
			}
		}

		// The block lifted (the pitcher went to a 특강, LessonHandlers.TryEnroll); whether there was one.
		public static bool Clear(uint user, int idx)
		{
			if (s_cs == null) return false;
			bool had = Blocked(user).ContainsKey(idx);
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "DELETE FROM sp_block WHERE user_code = $u AND card_idx = $i";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			cmd.ExecuteNonQuery();
			return had;
		}

		// For the roster screen (PKS_SC_ENTER_ROSTER_INFO sp_blocked / sp_blocked_count): blocked pitchers the team owns and
		// that are not starters now. The client looks each one up in its pitcher list (a missing one would throw) and keeps
		// it out of the starter slots.
		public static List<(int idx, int left)> ForRoster(uint user, IEnumerable<int> ownedPitchers, Lineup lineup)
		{
			HashSet<int> owned = ownedPitchers.ToHashSet();
			HashSet<int> starters = lineup == null ? new HashSet<int>() : Starters(lineup).ToHashSet();
			return Blocked(user).Where(kv => owned.Contains(kv.Key) && !starters.Contains(kv.Key)).Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key).ToList();
		}

		// Dev (in --leaguetest, a data copy): the count, a block from a save, the refusal and the roster list, on the first
		// team whose league is mid-season. Removes its rows afterwards.
		public static int SelfTest(Database db, CardCatalog catalog)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[spblock] {(ok ? "ok  " : "FAIL")} {what}");
			}
			DateTime now = DateTime.Now;
			LeagueState mid = new LeagueState { Season = 7, Start = now.AddHours(-10), NextGame = 74, FirstGame = 1, LastGame = 73 };
			Check(Left(7, 73, mid, now) == 5 && Left(7, 70, mid, now) == 2 && Left(7, 68, mid, now) == 0 && Left(6, 73, mid, now) == 0,
				$"games left: removed after game 73 → 5, after 70 → 2, after 68 → 0, last season → 0");
			uint user = 0;
			Lineup lineup = null;
			DateTime at = now;
			foreach (uint code in db.AllTeamCodes())
			{
				LeagueState s = LeagueService.Store?.Load(code);
				Lineup l = db.GetLineup(code);
				// A season with games to come; earlier --leaguetest steps may have moved the copy's leagues to the next season,
				// which starts later today, so the rule's clock is set inside it (2026-11-09 17:02: no team was mid-season "now").
				if (s == null || LeagueService.NextGameOf(s) == null || l == null || l.Pitchers.Take(StarterSlots).Count(i => i != 0) < StarterSlots) continue;
				int bullpen = l.Pitchers.Skip(StarterSlots).FirstOrDefault(i => i != 0);
				if (bullpen == 0) continue;
				user = code;
				lineup = l;
				at = now < s.Start ? s.Start.AddMinutes(1) : now;
				break;
			}
			if (user == 0)
			{
				Check(false, "no team with a season to play, five starters and a reliever");
				return problems;
			}
			s_clock = () => at;
			try
			{
				problems += SelfTestOn(db, catalog, user, lineup, at);
			}
			finally
			{
				s_clock = () => DateTime.Now;
			}
			ServerLog.Info($"[spblock] {problems} problem(s)");
			return problems;
		}

		private static int SelfTestOn(Database db, CardCatalog catalog, uint user, Lineup lineup, DateTime at)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[spblock] {(ok ? "ok  " : "FAIL")} {what}");
			}
			if (at > DateTime.Now) ServerLog.Info($"[spblock] team {user}'s season starts later; the rule's clock is set to {at:MM-dd HH:mm}");
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "DELETE FROM sp_block WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.ExecuteNonQuery();
			}
			// SP1 and the first reliever swap places: SP1 is blocked, putting him back is refused, the swap back the other way
			// (the reliever out, who is not blocked) passes; the roster list holds SP1 while he is in the bullpen.
			int sp1 = lineup.Pitchers[0];
			int rpSlot = Array.FindIndex(lineup.Pitchers, StarterSlots, i => i != 0);
			int rp = lineup.Pitchers[rpSlot];
			Lineup swapped = new Lineup { Batters = lineup.Batters, Fielders = lineup.Fielders, Pitchers = (int[])lineup.Pitchers.Clone(), NextSp = lineup.NextSp };
			swapped.Pitchers[0] = rp;
			swapped.Pitchers[rpSlot] = sp1;
			Record(user, lineup, swapped, out List<int> started);
			Dictionary<int, int> blocked = Blocked(user);
			string problem = Problem(user, swapped, lineup, catalog);
			List<(int idx, int left)> roster = ForRoster(user, new[] { sp1, rp }, swapped);
			Check(started.SequenceEqual(new[] { sp1 }) && blocked.TryGetValue(sp1, out int left) && left == Games && !blocked.ContainsKey(rp)
					&& problem != null && problem.Contains($"{Games}경기 남음") && roster.Count == 1 && roster[0].idx == sp1 && ForRoster(user, new[] { sp1 }, lineup).Count == 0,
				$"team {user}: SP1 {catalog.Get(sp1)?.Name} to the bullpen → blocked {(blocked.TryGetValue(sp1, out int l2) ? l2 : 0)} games; back refused: {problem?.Replace('\n', ' ')}; roster list {string.Join(",", roster)}");
			// A save that leaves the starters alone passes; the server-built lineup with him in it is not refused either.
			Check(Problem(user, swapped, swapped, catalog) == null && Problem(user, lineup, lineup, catalog) == null, "unchanged starters pass");
			// Sent to a 특강 (LessonHandlers.TryEnroll → Clear): the block is gone and putting him back passes.
			bool cleared = Clear(user, sp1);
			Check(cleared && !Blocked(user).ContainsKey(sp1) && Problem(user, swapped, lineup, catalog) == null && !Clear(user, sp1),
				"특강 신청으로 제한 풀림 → 다시 선발 가능");
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "DELETE FROM sp_block WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.ExecuteNonQuery();
			}
			return problems;
		}
	}
}
