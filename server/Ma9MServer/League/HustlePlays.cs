using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ma9MPackets;
using Ma9MSim.Fielding;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 허슬 플레이 모음 (engine session, 2026-10-31; user: "허슬 플레이 모음을 만들어서 클립을 재생해줄 수 있는 메뉴"):
	// the season's best defensive plays, found in the fielding engine's 3D scripts and replayed like a
	// highlight (일정 및 결과's third page, client Ma9MHustle). A game is looked through once the user has seen
	// its result (LeagueHandlers.Seen), so the list never tells a result early. While its season runs a clip
	// plays from the game in league_game; when the season closes (LeagueService.Close) the best of the user's
	// club and of the league stay, each with its game's broadcast as the season had it (hustle_packet), since
	// the next season brings other clubs and drops the games.
	public static class HustlePlays
	{
		public const byte TriplePlay = 1, HomeAssist = 2, WallCatch = 3, DivingCatch = 4, OfAssist = 5, LinerDp = 6, HomeTag = 7, DiveStop = 8, DoublePlay = 9;

		public static readonly string[] KindNames = { "", "삼중살", "홈 보살", "펜스 캐치", "다이빙 캐치", "외야 보살", "라이너 병살", "홈 승부", "다이빙 스톱", "병살" };

		// A kind's worth before the moment (Clutch.Leverage) and the dive's length weigh in.
		private static readonly int[] Worth = { 0, 300, 220, 200, 160, 160, 150, 120, 110, 60 };

		// Clips a closed season keeps (the user's club's and the league's best) and a list shows.
		public const int Keep = 30;

		// At most this many of a kind in a list while others wait (a test season: 0.8 diving catches a game
		// against 0.04 home assists, so the best 30 were nearly all dives).
		private static readonly int[] Cap = { 0, Keep, Keep, Keep, 10, Keep, Keep, 6, 5, 3 };

		// The best Keep of rows sorted best first, at most Cap of a kind unless too few others.
		private static List<T> Pick<T>(List<T> sorted, Func<T, int> kind)
		{
			List<T> list = new List<T>();
			int[] n = new int[Cap.Length];
			foreach (T x in sorted)
			{
				if (list.Count >= Keep) break;
				if (n[kind(x)]++ < Cap[kind(x)]) list.Add(x);
			}
			foreach (T x in sorted)
			{
				if (list.Count >= Keep) break;
				if (!list.Contains(x)) list.Add(x);
			}
			return list;
		}

		private static readonly string[] PosNames = { "포수", "1루수", "2루수", "3루수", "유격수", "좌익수", "중견수", "우익수", "투수" };

		// Short: the list's 경기 column is made for "121차전".
		private static readonly string[] SeriesNames = { "WC", "준PO", "PO", "KS" };

		private static string s_cs;

		public static void Register(GameLoop loop, string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS hustle_clip (
						id INTEGER PRIMARY KEY AUTOINCREMENT,
						user_code INTEGER NOT NULL,
						season INTEGER NOT NULL,
						game INTEGER NOT NULL,
						home INTEGER NOT NULL,
						scene INTEGER NOT NULL,
						kind INTEGER NOT NULL,
						grade INTEGER NOT NULL,
						mine INTEGER NOT NULL,
						line TEXT NOT NULL);
					CREATE INDEX IF NOT EXISTS hustle_clip_season ON hustle_clip (user_code, season, grade);
					CREATE TABLE IF NOT EXISTS hustle_scan (
						user_code INTEGER NOT NULL,
						season INTEGER NOT NULL,
						game INTEGER NOT NULL,
						home INTEGER NOT NULL,
						PRIMARY KEY (user_code, season, game, home));
					CREATE TABLE IF NOT EXISTS hustle_packet (
						user_code INTEGER NOT NULL,
						season INTEGER NOT NULL,
						game INTEGER NOT NULL,
						home INTEGER NOT NULL,
						packet BLOB NOT NULL,
						PRIMARY KEY (user_code, season, game, home));";
				cmd.ExecuteNonQuery();
			}
			loop.Register<Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT>(OnRequest);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// A play worth keeping: its scene in the buffer, kind, grade, the fielder (script actor: 0-7 deck
		// positions, 8 the pitcher) and the moment.
		public sealed class Found
		{
			public int Scene, Kind, Grade, Actor, Inning, BatterOrder, FieldRuns, BatRuns, PitcherSlot;
			public bool Bottom;
		}

		// The plays worth keeping in a stored game's buffer (none without the engine's scripts).
		public static List<Found> Find(byte[] buff)
		{
			List<Found> list = new List<Found>();
			List<byte[]> scripts = BroadcastRecorder.ReadScripts(buff);
			if (scripts == null) return list;
			int ptr = 0, count = buff[ptr++];
			bool bottom = false;
			int inning = 1, outs = 0;
			int[] runs = new int[2];           // away, home
			int[] pitcher = { -1, -1 };        // a side's reliever slot, -1 its starter
			for (int n = 0; n < count && ptr < buff.Length; n++)
			{
				byte sp = buff[ptr++];
				if (sp != 0)
				{
					byte arg = buff[ptr++];
					if (sp == Sit.SpChange)
					{
						if (bottom) inning++;
						bottom = !bottom;
						outs = 0;
					}
					else if (sp == Sit.SpChangePitcher)
					{
						pitcher[bottom ? 0 : 1] = arg;   // the fielding side's
					}
					continue;
				}
				byte sit = buff[ptr];
				byte[] order = { buff[ptr + 7], buff[ptr + 8], buff[ptr + 9], buff[ptr + 10] };
				byte[] next = { buff[ptr + 11], buff[ptr + 12], buff[ptr + 13], buff[ptr + 14] };
				ptr += 16 + buff[ptr + 15];
				// Outs as the client counts them (DataCenter.OnEndScene_UpdateData): a strikeout, fly, liner, double
				// play or bunt puts the batter out by its situation, his base slot only on the other plays.
				int bat = bottom ? 1 : 0, outsOnPlay = 0, scored = 0;
				bool batterOutBySit = sit is Sit.LookingSO or Sit.SwingSO or Sit.FlyOut or Sit.LineDrive or Sit.DoublePlay or Sit.Bunt;
				if (batterOutBySit) outsOnPlay++;
				for (int k = batterOutBySit ? 1 : 0; k < 4; k++)
				{
					if (order[k] == 0) continue;
					if (next[k] >= 5 && next[k] <= 8) outsOnPlay++;
					else if (next[k] == 4) scored++;
				}
				PlayScript script = n < scripts.Count && scripts[n] != null ? PlayScript.Decode(scripts[n]) : null;
				if (script != null)
				{
					(int kind, int actor, double dive) = Classify(script, sit, outsOnPlay, batterOutBySit || (next[0] >= 5 && next[0] <= 8));
					if (kind != 0)
					{
						double lev = Clutch.Leverage(inning, runs[bat] - runs[1 - bat], order[1] != 0, order[2] != 0, order[3] != 0, outs);
						int grade = (int)Math.Round(Worth[kind] * (0.7 + 0.3 * Math.Min(lev, 3.5)) + 25 * dive);
						list.Add(new Found
						{
							Scene = n, Kind = kind, Grade = grade, Actor = actor, Inning = inning, Bottom = bottom, BatterOrder = order[0],
							FieldRuns = runs[1 - bat], BatRuns = runs[bat], PitcherSlot = pitcher[1 - bat]
						});
					}
				}
				outs += outsOnPlay;
				runs[bat] += scored;
			}
			return list;
		}

		// What a scripted ball in play shows, the fielder it is about and the dive's length (m).
		private static (int kind, int actor, double dive) Classify(PlayScript s, byte sit, int outsOnPlay, bool batterOut)
		{
			if (sit == Sit.Steal || sit == Sit.Bunt) return (0, 0, 0);
			// The first fielder to the ball (relay and cover men only receive) and how he got it.
			Seg? first = null;
			foreach (Seg g in s.Segs)
			{
				if (g.Kind == SegKind.Catch && g.Actor <= Geometry.Pit && g.Style != CatchStyle.Receive && (first == null || g.T0 < first.Value.T0)) first = g;
			}
			if (first == null) return (0, 0, 0);
			int fielder = first.Value.Actor, style = first.Value.Style;
			double dive = 0;
			foreach (Seg g in s.Segs)
			{
				if (g.Kind == SegKind.Dive && g.Actor == fielder) dive = Math.Max(dive, g.P0.Dist(g.P1));
			}
			// An outfielder's throw that beat a runner (or the batter), and outs at the plate.
			double ofThrow = double.MaxValue;
			int ofThrower = -1, homeThrower = -1;
			foreach (Seg g in s.Segs)
			{
				if (g.Kind != SegKind.Throw) continue;
				if (g.Actor >= Geometry.LF && g.Actor <= Geometry.RF && g.T0 < ofThrow) { ofThrow = g.T0; ofThrower = g.Actor; }
				if (Near(g.P1) && homeThrower < 0) homeThrower = g.Actor;
			}
			bool assist = false, assistHome = false, outAtHome = false;
			foreach (Seg g in s.Segs)
			{
				if (g.Kind != SegKind.Out || g.Actor < 20) continue;
				bool home = Near(g.P0);
				outAtHome |= home;
				if (ofThrower >= 0 && g.T0 >= ofThrow - 0.05)
				{
					assist = true;
					assistHome |= home;
				}
			}
			if (outsOnPlay >= 3) return (TriplePlay, fielder, dive);
			if (assist) return (assistHome ? HomeAssist : OfAssist, ofThrower, dive);
			if (style == CatchStyle.WallLeap && batterOut) return (WallCatch, fielder, dive);
			if (style == CatchStyle.Liner && outsOnPlay >= 2) return (LinerDp, fielder, dive);
			if (style == CatchStyle.Diving && batterOut) return (DivingCatch, fielder, dive);
			if (outAtHome) return (HomeTag, homeThrower >= 0 ? homeThrower : fielder, dive);
			if (style == CatchStyle.DiveStop && batterOut) return (DiveStop, fielder, dive);
			if (outsOnPlay == 2) return (DoublePlay, fielder, dive);
			return (0, 0, 0);
		}

		private static bool Near(Geometry.P p) => p.X * p.X + p.Z * p.Z < 16;

		// Looks through the season's stored games not seen yet by this (all: every one, when the season closes).
		private static void Scan(LeagueState s, bool all)
		{
			int seen = all ? int.MaxValue : LeagueHandlers.Seen(s);
			// The latest slot's games stay out while their broadcast window runs (the postseason's other series).
			int onAir = all ? -1 : LeagueHandlers.BroadcastWindow(s, DateTime.Now)?.game ?? -1;
			List<(int game, int home, int away, string detail, byte[] buff)> todo = new List<(int, int, int, string, byte[])>();
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"SELECT g.game, g.home, g.away, g.detail, g.buff FROM league_game g
					WHERE g.user_code = $u AND g.season = $s AND g.game <= $seen AND g.game <> $air AND g.buff IS NOT NULL
					AND NOT EXISTS (SELECT 1 FROM hustle_scan h WHERE h.user_code = g.user_code AND h.season = g.season AND h.game = g.game AND h.home = g.home)";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$seen", seen);
				cmd.Parameters.AddWithValue("$air", onAir);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) todo.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetString(3), (byte[])r[4]));
			}
			if (todo.Count == 0) return;
			Bracket bracket = todo.Any(t => Postseason.IsPost(t.game)) ? Postseason.Of(s) : null;
			CardCatalog catalog = LeagueService.Data.Catalog;
			string Name(int idx) => idx == 0 ? string.Empty : (catalog.Get(idx)?.Name ?? string.Empty).Replace('|', '/');
			int clips = 0;
			using SqliteTransaction tx = c.BeginTransaction();
			foreach ((int game, int home, int away, string detail, byte[] buff) in todo)
			{
				List<Found> found;
				GameDetail d;
				try
				{
					found = Find(buff);
					d = found.Count > 0 ? JsonSerializer.Deserialize<GameDetail>(detail) : null;
				}
				catch (Exception e)
				{
					ServerLog.Warn($"hustle: game {s.Season}/{game}/{home} unreadable ({e.Message})");
					found = new List<Found>();
					d = null;
				}
				string label = GameLabel(bracket, game);
				foreach (Found f in found)
				{
					int fieldClub = f.Bottom ? away : home, batClub = f.Bottom ? home : away;
					Lineup fl = Lineup.Parse(f.Bottom ? d.AwayLineup : d.HomeLineup), bl = Lineup.Parse(f.Bottom ? d.HomeLineup : d.AwayLineup);
					int hero;
					if (f.Actor == Geometry.Pit)
					{
						int slot = f.PitcherSlot >= 0 ? f.PitcherSlot : LeagueService.StarterSlot(fl, LeagueService.RotationOf(s, fieldClub, game));
						hero = slot >= 0 && slot < fl.Pitchers.Length ? fl.Pitchers[slot] : 0;
					}
					else
					{
						int order = fl.Fielders[f.Actor];
						hero = order >= 1 && order <= Lineup.BatterSlots ? fl.Batters[order - 1] : 0;
					}
					int batter = f.BatterOrder >= 1 && f.BatterOrder <= 9 ? bl.Batters[f.BatterOrder - 1] : 0;
					LeagueClub fc = s.Clubs[fieldClub], bc = s.Clubs[batClub];
					string line = string.Join("|", s.Season, game, f.Scene, f.Kind, f.Grade, label, f.Inning + (f.Bottom ? "회말" : "회초"), fc.Emblem,
						Short(fc.Name), PosNames[f.Actor], Name(hero), Name(batter), f.FieldRuns, f.BatRuns, Short(bc.Name));
					using SqliteCommand ins = c.CreateCommand();
					ins.Transaction = tx;
					ins.CommandText = "INSERT INTO hustle_clip (user_code, season, game, home, scene, kind, grade, mine, line) VALUES ($u, $s, $g, $h, $sc, $k, $gr, $m, $l)";
					ins.Parameters.AddWithValue("$u", (long)s.UserCode);
					ins.Parameters.AddWithValue("$s", s.Season);
					ins.Parameters.AddWithValue("$g", game);
					ins.Parameters.AddWithValue("$h", home);
					ins.Parameters.AddWithValue("$sc", f.Scene);
					ins.Parameters.AddWithValue("$k", f.Kind);
					ins.Parameters.AddWithValue("$gr", f.Grade);
					ins.Parameters.AddWithValue("$m", fieldClub == s.MyIndex ? 1 : 0);
					ins.Parameters.AddWithValue("$l", line);
					ins.ExecuteNonQuery();
					clips++;
				}
				using SqliteCommand mark = c.CreateCommand();
				mark.Transaction = tx;
				mark.CommandText = "INSERT OR IGNORE INTO hustle_scan (user_code, season, game, home) VALUES ($u, $s, $g, $h)";
				mark.Parameters.AddWithValue("$u", (long)s.UserCode);
				mark.Parameters.AddWithValue("$s", s.Season);
				mark.Parameters.AddWithValue("$g", game);
				mark.Parameters.AddWithValue("$h", home);
				mark.ExecuteNonQuery();
			}
			tx.Commit();
			if (all || todo.Count > 50) ServerLog.Info($"hustle: user {s.UserCode} season {s.Season}: {todo.Count} games looked through, {clips} plays");
		}

		// A packet string holds 127 characters; a line has 15 fields.
		private static string Short(string name) => (name.Length > 10 ? name.Substring(0, 10) : name).Replace('|', '/');

		private static string GameLabel(Bracket bracket, int game)
		{
			if (!Postseason.IsPost(game)) return game + "차전";
			SeriesState x = bracket?.AtSlot(Postseason.SlotOf(game));
			return x == null ? "포스트시즌" : $"{SeriesNames[(int)x.Kind]} {Postseason.SlotOf(game) - x.FirstSlot + 1}차전";
		}

		// The season closes (LeagueService.Close, before the next league is formed): every game looked
		// through, the best of the user's club and of the league kept with their games' broadcasts.
		public static void Archive(LeagueState s)
		{
			if (s_cs == null) return;
			try
			{
				Scan(s, true);
				List<(long id, int game, int home, bool mine, int kind)> rows = new List<(long, int, int, bool, int)>();
				using SqliteConnection c = Open();
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT id, game, home, mine, kind FROM hustle_clip WHERE user_code = $u AND season = $s ORDER BY grade DESC, id";
					cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
					cmd.Parameters.AddWithValue("$s", s.Season);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3) != 0, r.GetInt32(4)));
				}
				HashSet<long> keep = new HashSet<long>(Pick(rows, x => x.kind).Concat(Pick(rows.Where(x => x.mine).ToList(), x => x.kind)).Select(x => x.id));
				// The kept games' broadcasts are made BEFORE the write transaction (2026-11-10 22:00 팀 정비: made inside it,
				// BroadcastReply → Database.GetCards → CoachHandlers.State wrote coach_state on its own connection for an owner
				// not cached yet, and waited for this very transaction's lock: 30 s on the game loop for every league, every
				// archive failed and nobody could log in for 5 minutes).
				List<(int game, int home, byte[] bytes)> made = new List<(int, int, byte[])>();
				foreach ((int game, int home) in rows.Where(x => keep.Contains(x.id)).Select(x => (x.game, x.home)).Distinct())
				{
					LeagueGame g = LeagueService.Store.Games(s.UserCode, s.Season, game: game, withBuff: true).FirstOrDefault(x => x.Home == home);
					if (g?.Buff == null) continue;
					Ma9MPacket.PKS_SC_ENTER_BROADCAST reply = LeagueHandlers.BroadcastReply(s, g);
					byte[] bytes = new byte[reply.GetSize()];
					reply.ToBytes(bytes, 0);
					made.Add((game, home, bytes));
				}
				using SqliteTransaction tx = c.BeginTransaction();
				foreach ((long id, int game, int home, bool mine, int kind) in rows.Where(x => !keep.Contains(x.id)))
				{
					using SqliteCommand del = c.CreateCommand();
					del.Transaction = tx;
					del.CommandText = "DELETE FROM hustle_clip WHERE id = $id";
					del.Parameters.AddWithValue("$id", id);
					del.ExecuteNonQuery();
				}
				int packets = 0;
				foreach ((int game, int home, byte[] bytes) in made)
				{
					using SqliteCommand ins = c.CreateCommand();
					ins.Transaction = tx;
					ins.CommandText = "INSERT OR REPLACE INTO hustle_packet (user_code, season, game, home, packet) VALUES ($u, $s, $g, $h, $p)";
					ins.Parameters.AddWithValue("$u", (long)s.UserCode);
					ins.Parameters.AddWithValue("$s", s.Season);
					ins.Parameters.AddWithValue("$g", game);
					ins.Parameters.AddWithValue("$h", home);
					ins.Parameters.Add("$p", SqliteType.Blob).Value = bytes;
					ins.ExecuteNonQuery();
					packets++;
				}
				using (SqliteCommand drop = c.CreateCommand())
				{
					// The closed season's marks stay until the next close: until the next league is formed (59 h)
					// the list still looks at this season, and must not find its games again.
					drop.Transaction = tx;
					drop.CommandText = "DELETE FROM hustle_scan WHERE user_code = $u AND season < $s";
					drop.Parameters.AddWithValue("$u", (long)s.UserCode);
					drop.Parameters.AddWithValue("$s", s.Season);
					drop.ExecuteNonQuery();
				}
				tx.Commit();
				ServerLog.Info($"hustle: user {s.UserCode} season {s.Season} closed: {keep.Count} of {rows.Count} plays kept, {packets} games");
			}
			catch (Exception e)
			{
				ServerLog.Warn($"hustle: archiving user {s.UserCode} season {s.Season} failed: {e}");
			}
		}

		private static void OnRequest(GameSession session, Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT packet)
		{
			if (session.Team == null) return;
			LeagueState s = LeagueService.Store.Load(session.Team.UserCode);
			if (s == null) return;
			if (packet.clip.n != 0)
			{
				session.Send(Play(s, packet.clip.n));
				return;
			}
			Scan(s, false);
			uint user = s.UserCode;
			List<int> seasons = new List<int>();
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT DISTINCT season FROM hustle_clip WHERE user_code = $u ORDER BY season DESC";
				cmd.Parameters.AddWithValue("$u", (long)user);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) seasons.Add(r.GetInt32(0));
			}
			if (!seasons.Contains(s.Season)) seasons.Insert(0, s.Season);
			int season = packet.season.n != 0 && seasons.Contains(packet.season.n) ? packet.season.n : s.Season;
			List<(int rank, int kind, string line)> rows = new List<(int, int, string)>();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT id, kind, line FROM hustle_clip WHERE user_code = $u AND season = $s" + (packet.scope.n == 0 ? " AND mine = 1" : string.Empty)
					+ " ORDER BY grade DESC, id";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", season);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) rows.Add((rows.Count, r.GetInt32(1), r.GetInt64(0) + "|" + r.GetString(2)));
			}
			Ma9MPacket.PKS_SC_OTHER_GAME_HIGHLIGHT reply = new Ma9MPacket.PKS_SC_OTHER_GAME_HIGHLIGHT();
			reply.season.n = season;
			reply.seasons.array = seasons.Select(x => new PInteger { n = x }).ToArray();
			reply.clips.array = Pick(rows, x => x.kind).OrderBy(x => x.rank).Select(x => new PString { str = x.line }).ToArray();
			session.Send(reply);
		}

		// Dev (--hustletest <user> on a data copy): the plays found in the user's season so far, each clip's game
		// played back (its scene a scripted play), then the season-close archive and a kept clip from it.
		public static int SelfTest(uint user)
		{
			Console.OutputEncoding = System.Text.Encoding.UTF8;
			LeagueState s = LeagueService.Store.Load(user);
			if (s == null) { Console.WriteLine($"no league for {user}"); return 1; }
			int problems = 0;
			System.Diagnostics.Stopwatch w = System.Diagnostics.Stopwatch.StartNew();
			Scan(s, false);
			Console.WriteLine($"season {s.Season}, seen up to game {LeagueHandlers.Seen(s)}: looked through in {w.ElapsedMilliseconds} ms");
			if (Environment.GetEnvironmentVariable("MA9M_HUSTLE_COLD") == "1")
			{
				// As the 팀 정비 does it on a freshly started server (2026-11-10 22:01): the archive first, nothing cached yet
				// (the play-back check below fills the coach cache and hid the 22:00 lock).
				System.Diagnostics.Stopwatch a = System.Diagnostics.Stopwatch.StartNew();
				Archive(s);
				Console.WriteLine($"cold archive in {a.ElapsedMilliseconds} ms");
				return 0;
			}
			List<(long id, int game, int home, int scene, int kind, int grade, bool mine, string line)> rows = new List<(long, int, int, int, int, int, bool, string)>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT id, game, home, scene, kind, grade, mine, line FROM hustle_clip WHERE user_code = $u AND season = $s ORDER BY grade DESC";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", s.Season);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6) != 0, r.GetString(7)));
			}
			int games;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM hustle_scan WHERE user_code = $u AND season = $s";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", s.Season);
				games = (int)(long)cmd.ExecuteScalar();
			}
			Console.WriteLine($"{games} games, {rows.Count} plays ({rows.Count(x => x.mine)} by the user's club), {rows.Count / (double)Math.Max(1, games):F2} a game");
			for (int k = 1; k < KindNames.Length; k++)
			{
				List<int> g = rows.Where(x => x.kind == k).Select(x => x.grade).ToList();
				Console.WriteLine($"  {KindNames[k],-8} {g.Count,5}  ({g.Count / (double)Math.Max(1, games):F3} a game)  grade {(g.Count > 0 ? $"{g.Min()}-{g.Max()} mean {g.Average():F0}" : "-")}");
			}
			Console.WriteLine("best:");
			foreach (var x in rows.Take(12)) Console.WriteLine($"  {x.id}|{x.line}");
			// Every clip's game plays: the packet carries the buffer whose scene is that scripted play.
			foreach (var x in rows)
			{
				Ma9MPacket.PKS_SC_ENTER_BROADCAST p = Play(s, (int)x.id);
				List<byte[]> scripts = p.game_num.n == 0 ? null : BroadcastRecorder.ReadScripts(p.buff.bytes);
				if (scripts == null || x.scene >= scripts.Count || scripts[x.scene] == null)
				{
					if (problems++ < 5) Console.WriteLine($"  FAIL clip {x.id} (game {x.game}, scene {x.scene}): no scripted play there");
				}
			}
			Console.WriteLine($"play-back check: {rows.Count - problems}/{rows.Count} clips ok");
			// The season closes.
			Archive(s);
			Scan(s, false);
			List<long> kept = new List<long>();
			int again;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT id FROM hustle_clip WHERE user_code = $u AND season = $s ORDER BY grade DESC";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", s.Season);
				using (SqliteDataReader r = cmd.ExecuteReader())
				{
					while (r.Read()) kept.Add(r.GetInt64(0));
				}
				again = kept.Count;
			}
			int expect = Pick(rows, x => x.kind).Concat(Pick(rows.Where(x => x.mine).ToList(), x => x.kind)).Select(x => x.id).Distinct().Count();
			Console.WriteLine("league list:");
			foreach (var x in Pick(rows, y => y.kind).OrderByDescending(y => y.grade)) Console.WriteLine($"  {x.grade,4} {KindNames[x.kind]}");
			if (again != expect) { problems++; Console.WriteLine($"  FAIL archive kept {again}, expected {expect} (found again after the close?)"); }
			foreach (long id in kept)
			{
				var x = rows.First(y => y.id == id);
				Ma9MPacket.PKS_SC_ENTER_BROADCAST p = Play(s, (int)id);
				LeagueGame g = LeagueService.Store.Games(user, s.Season, game: x.game, withBuff: true).First(y => y.Home == x.home);
				bool same = p.game_num.n == g.Game && p.buff.bytes.SequenceEqual(g.Buff) && p.home_team_name.str == s.Clubs[g.Home].Name
					&& p.away_batters.array.Length == LeagueHandlers.BroadcastReply(s, g).away_batters.array.Length;
				if (!same) { problems++; Console.WriteLine($"  FAIL kept clip {id}: archived broadcast differs from the game"); }
			}
			Console.WriteLine($"archive: {kept.Count} kept (expected {expect}), archived broadcasts checked; {problems} problems");
			return problems == 0 ? 0 : 1;
		}

		// A clip's game: from league_game while its season runs, else as the season closed with it.
		private static Ma9MPacket.PKS_SC_ENTER_BROADCAST Play(LeagueState s, int clip)
		{
			Ma9MPacket.PKS_SC_ENTER_BROADCAST none = new Ma9MPacket.PKS_SC_ENTER_BROADCAST();
			using SqliteConnection c = Open();
			int season, game, home;
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT season, game, home FROM hustle_clip WHERE id = $id AND user_code = $u";
				cmd.Parameters.AddWithValue("$id", clip);
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				using SqliteDataReader r = cmd.ExecuteReader();
				if (!r.Read()) return none;
				(season, game, home) = (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT packet FROM hustle_packet WHERE user_code = $u AND season = $s AND game = $g AND home = $h";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", season);
				cmd.Parameters.AddWithValue("$g", game);
				cmd.Parameters.AddWithValue("$h", home);
				if (cmd.ExecuteScalar() is byte[] bytes)
				{
					Ma9MPacket.PKS_SC_ENTER_BROADCAST kept = new Ma9MPacket.PKS_SC_ENTER_BROADCAST();
					kept.ToType(bytes, 0);
					return kept;
				}
			}
			if (season != s.Season) return none;
			LeagueGame g = LeagueService.Store.Games(s.UserCode, season, game: game, withBuff: true).FirstOrDefault(x => x.Home == home);
			return g?.Buff == null ? none : LeagueHandlers.BroadcastReply(s, g);
		}
	}
}
