using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// Dev (engine session, 2026-11-15; user to the systems session 04:0x: "지금 챔피언스 리그도 리그 자체가 너무 타고투저라서 투수들이
	// 다 박살나고 있어"): why the higher tiers score more than the reference world (this season's team runs a game: 비기너 4.8-5.1 …
	// 챔피언스 5.76, the reference 4.99). Every running league (a shared one once) plays chains of its own games as the league
	// plays them (Teams: card + bonus → league reference → 팀 특성; today's condition; pitchers' fatigue carried from game to game)
	// and again with one part changed, the same seeds for every variant; runs a team-game and the league's line by tier.
	// Read-only: nothing is saved. Run on a data copy: --runenv [rounds] (RUNENV_THREADS, default 2).
	// Variants:
	//   live     Teams as the league plays them
	//   neutral  every card at 보통 condition (Teams raw), reference and traits as live
	//   nodev    neutral, and no card bonus (교육·계약·트레이닝·팀컬러·코치; AI decks' development) — the cards as printed
	//   notraits neutral, no 팀 특성
	//   noref    neutral, no league reference
	//   full     neutral, the reference both ways (RefMode.Full: no stat left out for being under the reference)
	//   nodef    neutral, the reference without its fielding shift
	//   batref   neutral, the reference on batters only
	//   pitref   neutral, the reference on pitchers only
	public static partial class LeagueService
	{
		private sealed class RunEnvLine
		{
			public long Games, Runs, PA, AB, H, D, T, HR, BB, HBP, SO, SF, Outs, SpOuts;

			public void Add(RunEnvLine o)
			{
				Games += o.Games; Runs += o.Runs; PA += o.PA; AB += o.AB; H += o.H; D += o.D; T += o.T; HR += o.HR;
				BB += o.BB; HBP += o.HBP; SO += o.SO; SF += o.SF; Outs += o.Outs; SpOuts += o.SpOuts;
			}

			public string Row(string label)
			{
				double g = Math.Max(1, Games), pa = Math.Max(1, PA), ab = Math.Max(1, AB);
				double avg = H / ab, obp = (H + BB + HBP) / (double)Math.Max(1, AB + BB + HBP + SF), slg = (H + D + 2 * T + 3 * HR) / ab;
				return $"| {label} | {Games / 2} | {Runs / g:F2} | {avg:F3} | {obp:F3} | {slg:F3} | {100.0 * HR / pa:F2} | {100.0 * SO / pa:F1} | {100.0 * BB / pa:F1} | {100.0 * SpOuts / Math.Max(1, Outs):F0} |";
			}
		}

		private static readonly string[] RunEnvVariants = { "live", "neutral", "nodev", "notraits", "noref", "full", "nodef", "batref", "pitref" };

		public static int RunEnvTest(int rounds)
		{
			int threads = int.TryParse(Environment.GetEnvironmentVariable("RUNENV_THREADS"), out int th) ? Math.Max(1, th) : 2;
			// (11-15) RUNENV_VARIANTS=live,neutral plays only those (fitting the reference: several runs in a row).
			string pick = Environment.GetEnvironmentVariable("RUNENV_VARIANTS");
			string[] variants = string.IsNullOrWhiteSpace(pick) ? RunEnvVariants : RunEnvVariants.Where(v => pick.Split(',').Select(x => x.Trim()).Contains(v)).ToArray();
			// (11-15) RUNENV_COR = a 공인구 반발계수 for every game (BallEffect; empty = each league's season ball, SeasonBall),
			// BALL_K = "down,up" for BallEffect.KDown/KUp in this run.
			System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
			double forcedCor = double.TryParse(Environment.GetEnvironmentVariable("RUNENV_COR"), System.Globalization.NumberStyles.Float, inv, out double fc) ? fc : double.NaN;
			string[] bk = (Environment.GetEnvironmentVariable("BALL_K") ?? "").Split(',');
			if (bk.Length == 2 && double.TryParse(bk[0], System.Globalization.NumberStyles.Float, inv, out double kd) && double.TryParse(bk[1], System.Globalization.NumberStyles.Float, inv, out double ku))
			{
				BallEffect.KDown = kd;
				BallEffect.KUp = ku;
			}
			if (!double.IsNaN(forcedCor))
			{
				(double fh, double fb, double fx) = BallEffect.Factors(forcedCor);
				Console.WriteLine($"runenv: ball {forcedCor:F4} ({5 + BallEffect.RunsOff(forcedCor):F2} runs expected), x {BallEffect.EngineX(forcedCor):F3} (K {BallEffect.KDown:F3}/{BallEffect.KUp:F3}): HR ×{fh:F3}, BABIP ×{fb:F3}, 2B/3B share ×{fx:F3}");
			}
			const int warm = 5;   // the first rounds of a chain build its fatigue up (every pitcher starts rested); not counted
			// One state per league: the user's own, and a shared league once (its first member's copy).
			List<LeagueState> leagues = new List<LeagueState>();
			HashSet<string> shares = new HashSet<string>();
			foreach ((uint code, string name, byte emblem, byte grade) in s_store.ActiveTeams())
			{
				LeagueState s = s_store.Load(code);
				if (s == null || s.Clubs.Count == 0) continue;
				if (s.Share != null && !shares.Add(s.Share)) continue;
				leagues.Add(s);
			}
			Console.WriteLine($"runenv: {leagues.Count} league(s), {rounds} rounds a chain ({warm} warm-up), {threads} thread(s)");

			// Season so far, from the standings (the real games).
			Dictionary<int, (long runs, long games)> season = new Dictionary<int, (long, long)>();
			foreach (LeagueState s in leagues)
			{
				(long r, long g) = season.GetValueOrDefault(s.Tier);
				season[s.Tier] = (r + s.Clubs.Sum(c => (long)c.RunsFor), g + s.Clubs.Sum(c => (long)(c.W + c.L + c.D)));
			}

			// Teams for each league and variant (one thread: the reference mode is a static switch).
			var jobs = new List<(LeagueState s, string variant, GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits)>();
			var shifts = new Dictionary<int, List<(double[] bat, double[] pit, double def)>>();
			var spreads = new Dictionary<int, List<double[]>>();
			foreach (LeagueState s in leagues)
			{
				int game = Math.Clamp(s.NextGame, 1, LeagueRules.Games);
				foreach (string v in variants)
				{
					GameTeam[] teams;
					Lineup[] lineups;
					TraitEffects.Effect[] traits;
					if (v == "live")
					{
						(teams, lineups, traits) = Teams(s, game, _ => game);
					}
					else
					{
						(teams, lineups, traits) = v == "nodev" ? BareTeams(s, game) : Teams(s, game, _ => game, raw: true);
						TeamStrength.RefMode mode = TeamStrength.Mode;
						if (v == "full") TeamStrength.Mode = TeamStrength.RefMode.Full;
						var shift = TeamStrength.LeagueShift(teams);
						TeamStrength.Mode = mode;
						if (v == "neutral")
						{
							shifts.TryAdd(s.Tier, new List<(double[], double[], double)>());
							shifts[s.Tier].Add(shift);
						}
						if (v == "nodef") shift.def = 0;
						if (v == "batref") shift.pit = new double[6];
						if (v == "pitref") { shift.bat = new double[6]; shift.def = 0; }
						if (v != "noref")
						{
							foreach (GameTeam t in teams) TeamStrength.ApplyShift(t, shift);
						}
						if (v == "neutral")
						{
							spreads.TryAdd(s.Tier, new List<double[]>());
							spreads[s.Tier].Add(Spread(teams));
						}
						if (v == "notraits") traits = traits.Select(_ => TraitEffects.FromPoints(new Dictionary<byte, byte>())).ToArray();
						for (int i = 0; i < teams.Length; i++) TraitEffects.Raise(teams[i], traits[i]);
					}
					jobs.Add((s, v, teams, lineups, traits));
				}
			}

			// Play: a chain of `rounds` league games for each league and variant.
			var results = new Dictionary<(int tier, string variant), RunEnvLine>();
			object gate = new object();
			GameEnv env = s_data.Env;
			System.Threading.Tasks.Parallel.ForEach(jobs, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, job =>
			{
				RunEnvLine line = new RunEnvLine();
				int clubs = job.teams.Length;
				double[][] units = new double[clubs][];
				int[][] days = new int[clubs][], last1 = new int[clubs][], last2 = new int[clubs][];
				for (int i = 0; i < clubs; i++) { units[i] = new double[11]; days[i] = new int[11]; last1[i] = new int[11]; last2[i] = new int[11]; }
				int first = Math.Clamp(job.s.NextGame, 1, LeagueRules.Games);
				for (int r = 0; r < rounds; r++)
				{
					int g = 1 + (first - 1 + r) % LeagueRules.Games;
					foreach ((int away, int home) in LeagueRules.Pairs(g, job.s.Start))
					{
						int[] club = { home, away };
						GameTeam h = CopyTeam(job.teams[home]), a = CopyTeam(job.teams[away]);
						h.StarterSlot = StarterSlot(job.lineups[home], g);
						a.StarterSlot = StarterSlot(job.lineups[away], g);
						SeasonModel m = CardGame.Build(h, a, env);
						TraitEffects.Apply(m, 0, job.traits[home]);
						TraitEffects.Apply(m, 1, job.traits[away]);
						BallEffect.Apply(m, double.IsNaN(forcedCor) ? BallEffect.CorOf(job.s) : forcedCor);
						for (int t = 0; t < 2 && m.Tac != null; t++)
						{
							for (int slot = 0; slot < 11 && slot < m.Decks[t].Pitchers.Length; slot++)
							{
								int p = m.Decks[t].Pitchers[slot];
								if (p < 0) continue;
								m.Tac.CarryIn[p] = units[club[t]][slot];
								m.Tac.DaysInRow[p] = days[club[t]][slot];
								m.Tac.Last1[p] = last1[club[t]][slot];
								m.Tac.Last2[p] = last2[club[t]][slot];
							}
						}
						// The same seed for every variant (paired); not HashCode, which is seeded per process.
						ulong seed = job.s.UserCode * 1000003UL + (ulong)job.s.Season * 7919UL + (ulong)r * 104729UL + (ulong)home * 13UL + 1;
						SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, seed).PlayOne(0, 1, null);
						for (int t = 0; t < 2 && m.Tac != null; t++)
						{
							for (int slot = 0; slot < 11 && slot < m.Decks[t].Pitchers.Length; slot++)
							{
								int p = m.Decks[t].Pitchers[slot];
								if (p < 0) continue;
								int np = (int)st.Pit(p, PS.NP);
								units[club[t]][slot] = m.Tac.R.CarryOut(m.Tac.CarryIn[p], m.Tac.Units(p, np), m.Tac.Limit[p]);
								days[club[t]][slot] = st.Pit(p, PS.G) > 0 ? days[club[t]][slot] + 1 : 0;
								last2[club[t]][slot] = last1[club[t]][slot];
								last1[club[t]][slot] = np;
							}
						}
						if (r < warm) continue;
						for (int t = 0; t < 2; t++)
						{
							long B(int stat) => m.TeamBatters[t].Sum(i => st.Bat(i, stat));
							line.Games++;
							line.Runs += B(BS.R); line.PA += B(BS.PA); line.AB += B(BS.AB); line.H += B(BS.H); line.D += B(BS.D); line.T += B(BS.T);
							line.HR += B(BS.HR); line.BB += B(BS.BB); line.HBP += B(BS.HBP); line.SO += B(BS.SO); line.SF += B(BS.SF);
							for (int slot = 0; slot < m.Decks[t].Pitchers.Length; slot++)
							{
								int p = m.Decks[t].Pitchers[slot];
								if (p < 0) continue;
								long o = st.Pit(p, PS.Outs);
								line.Outs += o;
								if (slot < 5) line.SpOuts += o;
							}
						}
					}
				}
				lock (gate)
				{
					if (!results.TryGetValue((job.s.Tier, job.variant), out RunEnvLine sum)) results[(job.s.Tier, job.variant)] = sum = new RunEnvLine();
					sum.Add(line);
				}
			});

			// Report by tier.
			string F(double[] a) => string.Join(" ", a.Select(x => x.ToString("+0.0;-0.0;0.0")));
			foreach (int tier in results.Keys.Select(k => k.tier).Distinct().OrderBy(t => t))
			{
				(long sr, long sg) = season.GetValueOrDefault(tier);
				Console.WriteLine();
				Console.WriteLine($"## {LeagueRules.TierNames[tier]} ({leagues.Count(s => s.Tier == tier)} league(s)) · this season so far {(sg > 0 ? sr / (double)sg : double.NaN):F2} runs a team-game ({sg / 2} games)");
				if (shifts.TryGetValue(tier, out var sh))
				{
					double[] bat = Enumerable.Range(0, 6).Select(j => sh.Average(x => x.bat[j])).ToArray(), pit = Enumerable.Range(0, 6).Select(j => sh.Average(x => x.pit[j])).ToArray();
					Console.WriteLine($"reference shift (neutral, mean of leagues): batters 컨택 파워 선구 주력 정신 수비 {F(bat)} · pitchers 체력 구속 컨트롤 테크닉 구위 정신 {F(pit)} · fielding {sh.Average(x => x.def):+0.0;-0.0}");
				}
				if (spreads.TryGetValue(tier, out var sp))
				{
					double[] m = Enumerable.Range(0, sp[0].Length).Select(j => sp.Average(x => x[j])).ToArray();
					Console.WriteLine($"after the reference (neutral): batters' SD 컨택 {m[0]:F1} 파워 {m[1]:F1} 선구 {m[2]:F1} · pitchers' SD 구속 {m[3]:F1} 컨트롤 {m[4]:F1} 테크닉 {m[5]:F1} 구위 {m[6]:F1} · mean 파워 {m[7]:F1} 구위 {m[8]:F1}");
				}
				Console.WriteLine("| variant | games | R/G | AVG | OBP | SLG | HR% | K% | BB% | SP outs% |");
				Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
				foreach (string v in variants)
				{
					if (results.TryGetValue((tier, v), out RunEnvLine l)) Console.WriteLine(l.Row(v));
				}
			}
			return 0;
		}

		// The league's spread after the reference: SD of the 9 batters' 컨택 파워 선구, of the staff's 구속 컨트롤 테크닉 구위
		// (innings-weighted as LeagueShift), and the means of 파워 and 구위.
		private static double[] Spread(GameTeam[] teams)
		{
			List<double[]> bats = teams.SelectMany(t => t.Order).Where(g => g?.Row != null).Select(g => g.Row.R).ToList();
			List<double[]> pits = teams.SelectMany(t => t.Pitchers).Where(g => g?.Row != null).Select(g => g.Row.R).ToList();
			double Sd(List<double[]> rs, int j)
			{
				if (rs.Count < 2) return 0;
				double mean = rs.Average(r => r[j]);
				return Math.Sqrt(rs.Sum(r => (r[j] - mean) * (r[j] - mean)) / (rs.Count - 1));
			}
			return new[] { Sd(bats, 0), Sd(bats, 1), Sd(bats, 2), Sd(pits, 1), Sd(pits, 2), Sd(pits, 3), Sd(pits, 4), bats.Average(r => r[1]), pits.Average(r => r[4]) };
		}

		// The league's teams as Teams(raw) builds them but without any card bonus (AddBonuses): the cards as printed, with their
		// lineup effects and the real teams' tactics, every card at 보통 condition. The over-cap penalty stays as in Teams.
		private static (GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) BareTeams(LeagueState s, int game)
		{
			DateTime day = s.SlotTime(game);
			GameTeam[] teams = new GameTeam[LeagueRules.Clubs];
			Lineup[] lineups = new Lineup[LeagueRules.Clubs];
			for (int i = 0; i < LeagueRules.Clubs; i++)
			{
				LeagueClub c = s.Clubs[i];
				lineups[i] = LineupOf(s, c, out List<TeamCard> owned);
				AiTeam ai = owned == null ? AiTeamOf(s, c) : null;
				teams[i] = Team(c, lineups[i], day, game, owned, ai?.Development.Values, 0, neutral: true);
				if (OverCapPenalty && owned != null && RosterHandlers.LineupBalls(lineups[i]) > LeagueRules.BallCap[s.Tier])
				{
					foreach (GameCard g in teams[i].Order.Concat(teams[i].Pitchers).Where(g => g != null)) g.Condition = (1 + g.Condition) * OverCapStrength - 1;
				}
			}
			TraitEffects.Effect[] traits = s.Clubs.Select(c => c.IsHuman ? TraitEffects.Of(c.Owner)
				: TraitEffects.FromPoints(AiTeamOf(s, c)?.Traits ?? new Dictionary<byte, byte>())).ToArray();
			return (teams, lineups, traits);
		}
	}
}
