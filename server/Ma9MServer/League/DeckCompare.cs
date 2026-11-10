using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Ma9MSim;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// "--deckcompare <team name> [seasons]" with DECKCMP_VARIANTS (systems session, 2026-11-10; user 02:3x through the engine
	// session: "어떤 구조가 더 승률이 높은지 계산해주길 바람", "선발로 쓰는게 승률이 높은지 마무리로 쓰고 유영찬을 중계로 쓰는게
	// 승률이 높은지"): a real team as it is — its saved 1군, its cards with their development, 작전실, 팀 특성 — against the
	// clubs of its current league, built and played as league games are (Teams, PlayGame), with lineup variants.
	// - Each variant plays `seasons` runs of the 144-game schedule; only the team's own games are played (the other clubs
	//   rest in between), pitcher fatigue carried. Every variant gets the same seeds, so differences are paired.
	// - DECKCMP_VARIANTS = "name|op|op;name|op…", ops:
	//   B<slot>=<idx> batter slot 1-14 (1-9 the order, 10-14 the bench); P<slot>=<idx> pitcher slot 1-11 (SP1-5, RP1-4, SU, CP);
	//   F=<9 numbers with "."> the fielders C 1B 2B 3B SS LF CF RF DH as batting slots 1-9; BUILD = LineupBuilder arranges the
	//   variant's 25 cards; PT<idx>=<position_training> a card's 포지션 변경 (0 = none, (role − 11)·10 + 1 대성공 / 2 성공);
	//   DEF<idx>=<pos 0-7>:<delta> adds to the card's defence there (a what-if for a card fix), R<idx>=<k 0-5>:<delta> to a rating;
	//   DEV<idx>=<slot 0-5>:<points> development points in that training slot (batter 컨택 파워 스피드 선구 수비 H정신, pitcher
	//   체력 구속 컨트롤 테크닉 구위 P정신), added to the card's 특강 column, so they play through the development curve.
	// - Writes each variant's lineup and position changes into the DB (and puts the saved ones back at the end): a copy only.
	public static partial class LeagueService
	{
		private static readonly object s_deckBuildLock = new object();

		private sealed class DeckVariant
		{
			public string Name;
			public Lineup Lineup;
			public Dictionary<int, byte> Pt = new Dictionary<int, byte>();
			public List<(int idx, int pos, double delta)> Defs = new List<(int, int, double)>();
			public List<(int idx, int k, double delta)> Ratings = new List<(int, int, double)>();
			public List<(int idx, int slot, int points)> Dev = new List<(int, int, int)>();
			// 작전 (2026-11-11, user: "작전 방침이랑 작전실 방침도 같이 승률이 높은 쪽으로 설정하는 방법"): the team's 작전실 sliders
			// (null = as saved), every card following the team (NoOwn), and cards' own 개인작전 (4 values of their side).
			public byte[] OpBat, OpPit;
			public bool NoOwn;
			public Dictionary<int, byte[]> Own = new Dictionary<int, byte[]>();
			public bool TouchesOps => OpBat != null || OpPit != null || NoOwn || Own.Count > 0;
		}

		// A variant's 작전: the team sliders on GameTeam.Tactics, and each card's 개인작전 laid over them as OperationHandlers.ApplyTo
		// does (a card keeping its saved 개인작전 gets the variant's team values on the sliders it does not set).
		private static void DeckOps(GameTeam t, Lineup l, uint user, DeckVariant v)
		{
			(byte[] offense, byte[] defense) = OperationHandlers.TeamOps(user);
			if (v.OpBat != null) offense = (byte[])v.OpBat.Clone();
			if (v.OpPit != null) defense = (byte[])v.OpPit.Clone();
			t.Tactics = GameTactics.From(offense, defense);
			void Lay(GameCard card, int idx, bool pitcher)
			{
				if (card == null) return;
				byte[] mine = null;
				if (v.Own.TryGetValue(idx, out byte[] set)) mine = set;
				else if (!v.NoOwn && card.Own != null)
				{
					GameTactics o = card.Own;
					mine = pitcher ? new[] { (byte)o.Repertoire, (byte)o.Approach, (byte)o.Location, (byte)o.Effort }
						: new[] { (byte)o.Hitting, (byte)o.Patience, (byte)o.Running, (byte)o.Bunting };
				}
				if (mine == null)
				{
					card.Own = null;
					return;
				}
				byte[] off = (byte[])offense.Clone(), def = (byte[])defense.Clone();
				Array.Copy(mine, pitcher ? def : off, OperationHandlers.PersonalSliders);
				card.Own = GameTactics.From(off, def);
			}
			for (int slot = 0; slot < t.Order.Length && slot < l.Batters.Length; slot++) Lay(t.Order[slot], l.Batters[slot], false);
			for (int slot = 0; slot < t.Pitchers.Length && slot < l.Pitchers.Length; slot++) Lay(t.Pitchers[slot], l.Pitchers[slot], true);
		}

		private static byte[] DeckSliders(string text, int n)
		{
			byte[] b = text.Split('.').Select(x => (byte)Math.Clamp(int.Parse(x), 0, 100)).ToArray();
			if (b.Length != n) throw new ArgumentException($"{n} values expected: {text}");
			return b;
		}

		public static int DeckCompare(string teamName, int seasons, string spec, string dbPath)
		{
			string cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			uint user = 0;
			using (SqliteConnection c = new SqliteConnection(cs))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code FROM team WHERE team_name = $n";
				cmd.Parameters.AddWithValue("$n", teamName);
				object o = cmd.ExecuteScalar();
				if (o != null) user = Convert.ToUInt32(o);
			}
			LeagueState s = user != 0 ? s_store.Load(user) : null;
			Lineup saved = user != 0 ? s_db.GetLineup(user) : null;
			if (s == null || saved == null)
			{
				ServerLog.Warn($"[deckcmp] no league or lineup for '{teamName}'");
				return 2;
			}
			List<TeamCard> cards = s_db.GetCards(user);
			HashSet<int> have = cards.Select(c => c.Idx).ToHashSet();
			Dictionary<int, byte> savedPt = cards.GroupBy(c => c.Idx).ToDictionary(g => g.Key, g => g.First().PositionTrainingInfo);
			ServerLog.Info($"[deckcmp] {teamName} (user {user}): {LeagueRules.TierNames[s.Tier]} season {s.Season}, game {s.NextGame}, club {s.MyIndex} of {s.Clubs.Count}: "
				+ string.Join(", ", s.Clubs.Select(c => c.Name + (c.IsHuman ? "(사람)" : string.Empty))));

			List<DeckVariant> variants = new List<DeckVariant>();
			foreach (string text in (string.IsNullOrWhiteSpace(spec) ? "지금" : spec).Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = text.Split('|');
				DeckVariant v = new DeckVariant { Name = parts[0].Trim(), Lineup = Lineup.Parse(saved.Serialize()) };
				foreach (string raw in parts.Skip(1))
				{
					string op = raw.Trim();
					string[] kv = op.Split('=', 2);
					if (op == "BUILD")
					{
						v.Lineup = LineupBuilder.Build(v.Lineup.Batters.Concat(v.Lineup.Pitchers).Where(i => i != 0), s_data.Catalog);
					}
					else if (op == "NOOWN")
					{
						v.NoOwn = true;
					}
					else if (kv[0] == "OPB" || kv[0] == "OPP")
					{
						// 작전실: 타자 타격·공격·도루·번트·수비, 투수 투수성향·승부·제구·투구·교체, 0-100 with "." (e.g. OPB=50.50.75.25.50).
						byte[] b = DeckSliders(kv[1], OperationHandlers.TeamSliders);
						if (kv[0] == "OPB") v.OpBat = b; else v.OpPit = b;
					}
					else if (kv[0].StartsWith("OWN"))
					{
						v.Own[int.Parse(kv[0].Substring(3))] = DeckSliders(kv[1], OperationHandlers.PersonalSliders);
					}
					else if (kv[0] == "F")
					{
						v.Lineup.Fielders = kv[1].Split('.').Select(byte.Parse).ToArray();
					}
					else if (kv[0].StartsWith("PT"))
					{
						v.Pt[int.Parse(kv[0].Substring(2))] = byte.Parse(kv[1]);
					}
					else if (kv[0].StartsWith("DEV"))
					{
						string[] pd = kv[1].Split(':');
						v.Dev.Add((int.Parse(kv[0].Substring(3)), int.Parse(pd[0]), int.Parse(pd[1])));
					}
					else if (kv[0].StartsWith("DEF"))
					{
						string[] pd = kv[1].Split(':');
						v.Defs.Add((int.Parse(kv[0].Substring(3)), int.Parse(pd[0]), double.Parse(pd[1], System.Globalization.CultureInfo.InvariantCulture)));
					}
					else if (kv[0].StartsWith("R"))
					{
						string[] pd = kv[1].Split(':');
						v.Ratings.Add((int.Parse(kv[0].Substring(1)), int.Parse(pd[0]), double.Parse(pd[1], System.Globalization.CultureInfo.InvariantCulture)));
					}
					else if (kv[0].StartsWith("B"))
					{
						v.Lineup.Batters[int.Parse(kv[0].Substring(1)) - 1] = int.Parse(kv[1]);
					}
					else if (kv[0].StartsWith("P"))
					{
						v.Lineup.Pitchers[int.Parse(kv[0].Substring(1)) - 1] = int.Parse(kv[1]);
					}
					else
					{
						ServerLog.Warn($"[deckcmp] unknown op '{op}' in {v.Name}");
						return 2;
					}
				}
				variants.Add(v);
			}
			HashSet<int> touched = variants.SelectMany(v => v.Pt.Keys).ToHashSet();
			HashSet<int> devTouched = variants.SelectMany(v => v.Dev.Select(x => x.idx)).ToHashSet();
			Dictionary<int, byte[]> savedEdu = new Dictionary<int, byte[]>();
			using (SqliteConnection c = new SqliteConnection(cs))
			{
				c.Open();
				foreach (int idx in devTouched)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "SELECT educated FROM team_card WHERE user_code = $u AND card_idx = $i";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$i", idx);
					if (cmd.ExecuteScalar() is byte[] blob) savedEdu[idx] = blob;
				}
			}
			void SetEdu(int idx, byte[] blob)
			{
				using SqliteConnection c = new SqliteConnection(cs);
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "UPDATE team_card SET educated = $b WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.AddWithValue("$b", blob);
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.ExecuteNonQuery();
			}

			void SetPt(int idx, byte value)
			{
				using SqliteConnection c = new SqliteConnection(cs);
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "UPDATE team_card SET position_training = $v WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.AddWithValue("$v", (int)value);
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.ExecuteNonQuery();
			}
			string Name(int idx) => s_data.Catalog.Get(idx)?.Name ?? idx.ToString();
			string[] posNames = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" };
			string Describe(Lineup l)
			{
				string[] at = new string[9];
				for (int p = 0; p < Lineup.FielderSlots; p++) if (l.Fielders[p] >= 1 && l.Fielders[p] <= 9) at[l.Fielders[p] - 1] = posNames[p];
				string order = string.Join(" ", Enumerable.Range(0, 9).Select(i => $"{i + 1}.{Name(l.Batters[i])}({at[i]})"));
				string bench = string.Join(" ", l.Batters.Skip(9).Where(i => i != 0).Select(Name));
				string[] slot = { "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };
				string pitch = string.Join(" ", Enumerable.Range(0, Lineup.PitcherSlots).Select(i => $"{slot[i]} {Name(l.Pitchers[i])}"));
				return $"{order} | 벤치 {bench} | {pitch}";
			}

			int[] baseWins = null;
			List<string> report = new List<string>();
			foreach (DeckVariant v in variants)
			{
				string problem = !v.Lineup.IsValid(have) ? "lineup not valid for the owned cards" : RosterHandlers.LegendProblem(v.Lineup);
				int balls = RosterHandlers.LineupBalls(v.Lineup), cap = LeagueRules.BallCap[s.Tier];
				ServerLog.Info($"[deckcmp] {v.Name}: {Describe(v.Lineup)}; {balls}/{cap}볼"
					+ (v.Pt.Count > 0 ? "; 포지션 변경 " + string.Join(", ", v.Pt.Select(kv => $"{Name(kv.Key)} {kv.Value}")) : string.Empty)
					+ (v.Dev.Count > 0 ? "; 육성 " + string.Join(", ", v.Dev.Select(x => $"{Name(x.idx)} 칸{x.slot} {x.points:+0;-0}")) : string.Empty)
					+ (v.OpBat != null ? "; 타자 작전 " + string.Join(".", v.OpBat) : string.Empty) + (v.OpPit != null ? "; 투수 작전 " + string.Join(".", v.OpPit) : string.Empty)
					+ (v.NoOwn ? "; 개인작전 끔" : string.Empty) + (v.Own.Count > 0 ? "; 개인작전 " + string.Join(", ", v.Own.Select(kv => $"{Name(kv.Key)} {string.Join(".", kv.Value)}")) : string.Empty)
					+ (problem != null ? $"; PROBLEM {problem}" : string.Empty) + (balls > cap ? "; OVER THE CAP" : string.Empty));
				s_db.SaveLineup(user, v.Lineup);
				foreach (int idx in touched) SetPt(idx, v.Pt.TryGetValue(idx, out byte pt) ? pt : savedPt.GetValueOrDefault(idx));
				foreach (int idx in savedEdu.Keys)
				{
					byte[] blob = (byte[])savedEdu[idx].Clone();
					foreach (var x in v.Dev.Where(x => x.idx == idx)) blob[x.slot] = (byte)Math.Clamp((sbyte)blob[x.slot] + x.points, sbyte.MinValue, sbyte.MaxValue);
					SetEdu(idx, blob);
				}
				Stopwatch sw = Stopwatch.StartNew();
				int[] wins = new int[seasons], losses = new int[seasons], draws = new int[seasons];
				long[] runsFor = new long[seasons], runsAgainst = new long[seasons];
				// A card's what-if (DEF / R): its game card gets a changed copy of its row.
				void Tweak(GameCard card, int idx)
				{
					if (card == null || !v.Defs.Any(x => x.idx == idx) && !v.Ratings.Any(x => x.idx == idx)) return;
					CardRow row = card.Row.Clone();
					foreach (var x in v.Defs.Where(x => x.idx == idx))
					{
						row.Defs[x.pos] += x.delta;
						row.RawDefs[x.pos] += x.delta;
					}
					foreach (var x in v.Ratings.Where(x => x.idx == idx)) row.R[x.k] += x.delta;
					card.Row = row;
				}
				// Seasons in parallel (DECKCMP_THREADS, default 4), each on its own copy of the league state; the card games are
				// thread-safe (--tcsim plays them in parallel) and the teams are built from the DB by reads only.
				int threads = int.TryParse(Environment.GetEnvironmentVariable("DECKCMP_THREADS"), out int th) && th > 0 ? th : 4;
				System.Threading.Tasks.Parallel.For(0, seasons, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
				{
					LeagueState run = new LeagueState
					{
						UserCode = s.UserCode, Tier = s.Tier, Season = 9000 + k, Start = s.Start, NextGame = 1, MyIndex = s.MyIndex, Clubs = s.Clubs,
						FirstGame = 1, PostSlot = 1, NextTier = s.NextTier, Share = s.Share
					};
					Dictionary<(int club, int card), PitcherLoad> fatigue = new Dictionary<(int club, int card), PitcherLoad>();
					for (int game = 1; game <= LeagueRules.Games; game++)
					{
						(int away, int home)[] pairs = LeagueRules.Pairs(game, run.Start);
						int pi = Array.FindIndex(pairs, p => p.away == run.MyIndex || p.home == run.MyIndex);
						if (pi < 0) continue;
						(int away, int home) = pairs[pi];
						// The teams one at a time: building them reads server caches that are not thread-safe (CoachHandlers.ConsultSteps
						// through Condition.For broke a run on 2026-11-10); only the games themselves run in parallel.
						GameTeam[] teams;
						Lineup[] lineups;
						TraitEffects.Effect[] traits;
						lock (s_deckBuildLock)
						{
							(teams, lineups, traits) = Teams(run, game, _ => game);
							GameTeam mine = teams[run.MyIndex];
							for (int slot = 0; slot < 9; slot++) Tweak(mine.Order[slot], lineups[run.MyIndex].Batters[slot]);
							for (int slot = 0; slot < Lineup.PitcherSlots; slot++) Tweak(mine.Pitchers[slot], lineups[run.MyIndex].Pitchers[slot]);
							if (v.TouchesOps) DeckOps(mine, lineups[run.MyIndex], user, v);
						}
						Dictionary<(int club, int card), PitcherLoad> after = new Dictionary<(int club, int card), PitcherLoad>();
						LeagueGame g = PlayGame(run, game, home, away, teams, lineups, new List<PlayerSeason>(), traits, fatigue, after, post: false);
						fatigue = after;
						bool atHome = home == run.MyIndex;
						int my = atHome ? g.HomeScore : g.AwayScore, their = atHome ? g.AwayScore : g.HomeScore;
						runsFor[k] += my;
						runsAgainst[k] += their;
						if (my > their) wins[k]++; else if (my < their) losses[k]++; else draws[k]++;
					}
				});
				int w = wins.Sum(), l = losses.Sum(), d = draws.Sum();
				long rf = runsFor.Sum(), ra = runsAgainst.Sum();
				double pct = w + l == 0 ? 0 : 100.0 * w / (w + l);
				string diff = string.Empty;
				if (baseWins == null) baseWins = wins;
				else
				{
					// Paired by season (same seeds): the mean difference in wins per season and its standard error, as win%.
					double[] dd = Enumerable.Range(0, seasons).Select(k => (double)(wins[k] - baseWins[k])).ToArray();
					double mean = dd.Average(), sd = seasons > 1 ? Math.Sqrt(dd.Sum(x => (x - mean) * (x - mean)) / (seasons - 1)) : 0;
					double per = 100.0 / Math.Max(1.0, (w + l + d) / (double)seasons);
					diff = $", vs {variants[0].Name} {mean * per:+0.00;-0.00}%p ± {sd / Math.Sqrt(seasons) * per:F2}";
				}
				int n = w + l + d;
				string line = $"[deckcmp] {v.Name}: {w}-{l}-{d}, {pct:F2}% ({n / Math.Max(1, seasons)} games × {seasons}), runs {rf / (double)n:F2} - {ra / (double)n:F2}{diff} ({sw.Elapsed.TotalSeconds:F0} s)";
				ServerLog.Info(line);
				report.Add(line);
			}
			s_db.SaveLineup(user, saved);
			foreach (int idx in touched) SetPt(idx, savedPt.GetValueOrDefault(idx));
			foreach (KeyValuePair<int, byte[]> kv in savedEdu) SetEdu(kv.Key, kv.Value);
			foreach (string line in report) ServerLog.Info(line);
			return 0;
		}
	}
}
