using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Ma9MSim
{
	// Self-check for server games: random teams of regulars, many games, league totals, and the
	// play-by-play of one game with a consistency check of every recorded play.
	public static class CardGameTest
	{
		private sealed class Checker : IGameRecorder
		{
			public readonly List<string> Lines = new List<string>();
			public int Outs, Runs, Plays, Problems;
			// League detail (fielding engine and the old rules alike, where the old rules have it).
			public long Bip, Dp, Tp, Errors, Assists, AssistHome, InfieldHits, Hits, Dives, DiveStops, WallCatches, DoubledOff, Fc, SacFly;
			public long S1n, S1to3, S2n, S2home, D1n, D1home, Thrown;
			// Half-innings by the runs scored in them (the bullpen measure, 2026-11-10).
			public long Big4, Big5, Big8;
			private int m_halfRuns;
			public bool Print;
			public Checker Into;
			private readonly Func<int, string> m_name;

			public Checker(Func<int, string> name)
			{
				m_name = name;
			}

			public void HalfInningStart(int inning, bool bottom)
			{
				Outs = 0;
				m_halfRuns = Runs;
				if (Print) Lines.Add($"-- {inning}회{(bottom ? "말" : "초")}");
			}

			public void HalfInningEnd(int inning, bool bottom)
			{
				if (Outs != 3 && Print) Lines.Add($"   (half ends with {Outs} outs)");
				int got = Runs - m_halfRuns;
				Checker sum = Into ?? this;
				if (got >= 4) sum.Big4++;
				if (got >= 5) sum.Big5++;
				if (got >= 8) sum.Big8++;
			}

			public void PitcherChange(int team, int pitcher)
			{
				if (Print) Lines.Add($"   투수 교체: {pitcher}");
			}

			private void Tally(PlayInfo p)
			{
				bool bip = p.Kind is PlayKind.Single or PlayKind.Double or PlayKind.Triple or PlayKind.GroundOut or PlayKind.DoublePlay
					or PlayKind.FlyOut or PlayKind.SacFly or PlayKind.LineOut or PlayKind.Error;
				if (!bip) return;
				Bip++;
				if (p.Kind == PlayKind.DoublePlay) Dp++;
				if (p.Kind == PlayKind.SacFly) SacFly++;
				if (p.Kind is PlayKind.Single or PlayKind.Double or PlayKind.Triple) Hits++;
				if (p.Kind == PlayKind.Single && p.Before[1] >= 0) { S1n++; if (p.Dest[1] == 3 || p.Dest[1] == 4) S1to3++; }
				if (p.Kind == PlayKind.Single && p.Before[2] >= 0) { S2n++; if (p.Dest[2] == 4) S2home++; }
				if (p.Kind == PlayKind.Double && p.Before[1] >= 0) { D1n++; if (p.Dest[1] == 4) D1home++; }
				for (int i = 1; i < 4; i++) if (p.Before[i] >= 0 && p.Dest[i] >= 5 && p.Kind is PlayKind.Single or PlayKind.Double or PlayKind.Triple) Thrown++;
				Fielding.FieldOutcome f = p.Field;
				if (f == null) { if (p.Kind == PlayKind.Error) Errors++; return; }
				if (f.Error) Errors++;
				if (f.TriplePlay) Tp++;
				if (f.DoubledOff) DoubledOff++;
				if (f.Fc) Fc++;
				if (f.Assist >= 5 && f.Assist <= 7) { Assists++; if (f.Throws.Exists(t => t.Out && t.Base == 0)) AssistHome++; }
				if (f.Ball.InfieldHit && f.Hit > 0) InfieldHits++;
				if (f.Hit == 0 && f.Ball.ShowDive && !f.Ball.Through && f.Ball.How != Fielding.Catch.Grounder && f.Ball.How != Fielding.Catch.Charging && f.Ball.How != Fielding.Catch.DiveStop) Dives++;
				if (f.Hit == 0 && f.Ball.ShowDive && (f.Ball.How == Fielding.Catch.Grounder || f.Ball.How == Fielding.Catch.Charging || f.Ball.How == Fielding.Catch.DiveStop)) DiveStops++;
				if (f.Hit == 0 && f.Ball.How == Fielding.Catch.Leaping) WallCatches++;
			}

			public void Play(PlayInfo p)
			{
				Plays++;
				int outs = 0;
				bool steal = p.Kind is PlayKind.StealSafe or PlayKind.StealOut;
				if (!steal && p.Kind is PlayKind.Strikeout or PlayKind.FlyOut or PlayKind.SacFly or PlayKind.LineOut or PlayKind.DoublePlay or PlayKind.SacBunt)
				{
					outs++; // the client counts these batters out on its own
				}
				for (int i = steal ? 1 : 0; i < 4; i++)
				{
					if (p.Before[i] < 0) continue;
					if (p.Dest[i] >= 5 && !(i == 0 && p.Kind is PlayKind.DoublePlay or PlayKind.SacBunt)) outs++;
					if (p.Dest[i] == 4) Runs++;
				}
				Outs += outs;
				(Into ?? this).Tally(p);
				// Bases after the play must hold at most one runner each.
				int[] taken = new int[4];
				for (int i = 0; i < 4; i++)
				{
					if (p.Before[i] >= 0 && p.Dest[i] >= 1 && p.Dest[i] <= 3) taken[p.Dest[i]]++;
					if (p.Before[i] >= 0 && p.Dest[i] == 0 && i > 0) taken[i]++;
				}
				if (taken.Any(t => t > 1)) Problems++;
				if (Print)
				{
					Lines.Add($"   {p.Slot + 1}번 {m_name(p.Batter)}: {p.Kind} outs {p.OutsBefore}->{p.OutsBefore + outs} "
						+ $"[{string.Join(" ", Enumerable.Range(0, 4).Select(i => p.Before[i] < 0 ? "-" : $"{i}>{p.Dest[i]}"))}] runs {p.Runs}");
				}
			}
		}

		// What one fielder is worth in full games (throws, double plays, runners all in): the home
		// side's man at `pos` set to each level of defence (and 주력 moved half as much for outfielders,
		// 0.3 for infielders), same games and seeds otherwise; runs allowed a 144-game season vs the
		// regulars' own level.
		public static void Value(string dir, int games, int pos, double[] levels, Action<string> log, bool hustle = false)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			string[] posName = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
			List<string> row = new List<string>();
			double baseRuns = double.NaN;
			foreach (double lv in levels)
			{
				Random rnd = new Random(7);
				long allowed = 0;
				HustleCount count = hustle ? new HustleCount { Pos = pos } : null;
				for (int g = 0; g < games; g++)
				{
					GameTeam home = MakeTeam(regulars, rnd), away = MakeTeam(regulars, rnd);
					SeasonModel model = CardGame.Build(home, away, env);
					if (!double.IsNaN(lv))
					{
						double[] defs = (double[])model.Defs[pos].Clone();
						double moved = lv - defs[pos];
						defs[pos] = lv;
						model.Defs[pos] = defs;
						if (model.RunX != null)
						{
							model.RunX[pos] += moved / 10 * (pos >= 5 ? 0.5 : 0.3);
						}
					}
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, count);
					foreach (int i in model.TeamBatters[1]) allowed += st.Bat(i, BS.R);
				}
				double perSeason = 144.0 * allowed / games;
				if (double.IsNaN(lv)) baseRuns = perSeason;
				row.Add(double.IsNaN(lv) ? $"own {perSeason:F1}" : $"{lv:F0}: {perSeason:F1} ({baseRuns - perSeason:+0.0;-0.0})");
				if (count != null)
				{
					double k = 144.0 / games;
					log($"  {posName[pos]} {(double.IsNaN(lv) ? "own" : lv.ToString("F0"))}: a season {count.Chances * k:F0} balls first to him, outs on them {100.0 * count.Outs / Math.Max(1, count.Chances):F1}%,"
						+ $" diving catches {count.Dive * k:F1}, wall catches {count.Wall * k:F1}, diving stops {count.Stop * k:F1}, outfield assists {count.Assist * k:F1}");
				}
			}
			log($"{posName[pos]} runs allowed a season ({games} games each): " + string.Join("  ", row));
		}

		// --hustle (engine session, 2026-10-31; user: "수비 값이 오르면 더 자주 발생하니?"): the measured home fielder's
		// plays, from the fielding engine's scripts (the server's 허슬 플레이 counts them the same way, HustlePlays).
		private sealed class HustleCount : IGameRecorder
		{
			public int Pos;
			public long Chances, Outs, Dive, Wall, Stop, Assist;

			public void HalfInningStart(int inning, bool bottom) { }

			public void HalfInningEnd(int inning, bool bottom) { }

			public void PitcherChange(int team, int pitcher) { }

			public void Play(PlayInfo p)
			{
				if (p.Team != 1 || p.Script == null) return;
				Fielding.PlayScript s = Fielding.PlayScript.Decode(p.Script);
				if (s == null || p.Kind == PlayKind.StealSafe || p.Kind == PlayKind.StealOut) return;
				bool batterOut = p.Kind is PlayKind.FlyOut or PlayKind.LineOut or PlayKind.SacFly or PlayKind.DoublePlay || (p.Dest[0] >= 5 && p.Dest[0] <= 8);
				Fielding.Seg? first = null;
				float throwAt = float.MaxValue;
				foreach (Fielding.Seg g in s.Segs)
				{
					if (g.Kind == Fielding.SegKind.Catch && g.Actor <= Fielding.Geometry.Pit && g.Style != Fielding.CatchStyle.Receive && (first == null || g.T0 < first.Value.T0)) first = g;
					if (g.Kind == Fielding.SegKind.Throw && g.Actor == Pos && Pos >= Fielding.Geometry.LF) throwAt = Math.Min(throwAt, g.T0);
				}
				if (first != null && first.Value.Actor == Pos)
				{
					Chances++;
					if (batterOut) Outs++;
					if (batterOut && first.Value.Style == Fielding.CatchStyle.Diving) Dive++;
					if (batterOut && first.Value.Style == Fielding.CatchStyle.WallLeap) Wall++;
					if (batterOut && first.Value.Style == Fielding.CatchStyle.DiveStop) Stop++;
				}
				if (throwAt < float.MaxValue && s.Segs.Any(g => g.Kind == Fielding.SegKind.Out && g.Actor >= 20 && g.T0 >= throwAt - 0.05)) Assist++;
			}
		}

		// --fieldvalue: what one fielder is worth, play by play (Fielding.FieldShadow: no game-to-game noise). The home
		// side's man at `pos` is played at each level, every part of his job at once (all) or one part at a time (range,
		// arm, hands, pivot; cs: a catcher's throwing on steals; speed: his 주력 −10 / +10 with his own defence), on the
		// same balls with the same luck; runs saved a 144-game season against his own level, from the run expectancy of
		// the states each version leaves (RE24 from the same games). CaughtPer10: a what-if for the catcher's throwing.
		public static double? CaughtPer10;

		public static void FieldValue(string dir, int games, int pos, double[] levels, string[] parts, int threads, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			if (CaughtPer10 != null) env.Field.CaughtPer10 = CaughtPer10.Value;
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			string[] posName = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
			Random rnd = new Random(7);
			(GameTeam home, GameTeam away)[] teams = new (GameTeam, GameTeam)[games];
			for (int g = 0; g < games; g++) teams[g] = (MakeTeam(regulars, rnd), MakeTeam(regulars, rnd));
			double own = teams.Average(t => t.home.Order[pos].Row.Defs[pos]);
			List<(string part, double level)> vs = new List<(string, double)>();
			foreach (string p in parts)
			{
				if (p == "none") continue;   // just the counts as played
				if (p == "speed")
				{
					vs.Add((p, -10));
					vs.Add((p, 10));
					continue;
				}
				foreach (double lv in levels) vs.Add((p, lv));
			}
			Fielding.FieldShadow total = new Fielding.FieldShadow { Team = 0, Pos = pos };
			total.Init(vs.Count);
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
				() =>
				{
					Fielding.FieldShadow s = new Fielding.FieldShadow { Team = 0, Pos = pos };
					s.Init(vs.Count);
					return s;
				},
				(g, _, sh) =>
				{
					SeasonModel model = CardGame.Build(teams[g].home, teams[g].away, env);
					double xo = CardRules.X(model.Defs[pos][pos]), xs = model.RunX[pos];
					sh.Variants = new Fielding.FielderSkill[vs.Count];
					sh.CaughtMult = pos == 0 ? new double[vs.Count] : null;
					for (int k = 0; k < vs.Count; k++)
					{
						(string part, double lv) = vs[k];
						double x = part == "speed" ? xo : CardRules.X(lv);
						double Part(string name) => part == "all" || part == name ? x : xo;
						sh.Variants[k] = Fielding.FielderSkill.For(pos, Part("range"), Part("arm"), Part("hands"), Part("pivot"), part == "speed" ? xs + lv / 10 : xs);
						if (sh.CaughtMult != null) sh.CaughtMult[k] = env.Field.CaughtMult(Part("cs"));
					}
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919) { Shadow = sh };
					sim.PlayOne(0, 1, null);
					return sh;
				},
				sh =>
				{
					lock (gate) total.Merge(sh);
				});
			double[] re = new double[24];
			for (int s = 0; s < 24; s++) re[s] = total.ReN[s] > 0 ? total.ReSum[s] / total.ReN[s] : 0;
			string[] baseName = { "---", "1--", "-2-", "12-", "--3", "1-3", "-23", "123" };
			log("RE24: " + string.Join("  ", Enumerable.Range(0, 3).Select(o => $"{o} out " + string.Join(" ", Enumerable.Range(0, 8).Select(b => $"{baseName[b]} {re[o * 8 + b]:F2}")))));
			double k144 = 144.0 / games;
			string[] who = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "P" };
			log("  a season, as played: grounders fielded " + string.Join(" ", Enumerable.Range(0, 9).Select(f => $"{who[f]} {total.Fielded[f] * k144:F0}"))
				+ " | caught " + string.Join(" ", Enumerable.Range(0, 9).Select(f => $"{who[f]} {total.Caught[f] * k144:F0}"))
				+ " | errors " + string.Join(" ", Enumerable.Range(0, 9).Select(f => $"{who[f]} {total.Errors[f] * k144:F1}")));
			string[] zn = { "L", "LC", "C", "RC", "R" };
			long zb = total.ZoneBalls.Sum();
			log("  directions (balls in play, share / batting average): " + string.Join("  ", Enumerable.Range(0, 5).Select(z => $"{zn[z]} {100.0 * total.ZoneBalls[z] / Math.Max(1, zb):F1}% / {(double)total.ZoneHits[z] / Math.Max(1, total.ZoneBalls[z]):F3}"))
				+ $"  | batters L/S {100.0 * teams.Sum(t => t.away.Order.Count(c => c.Bats != Hand.R)) / (9.0 * games):F0}%, pitchers L {100.0 * teams.Sum(t => t.home.Pitchers.Count(c => c != null && c.Throws == Hand.L)) / (11.0 * games):F0}%");
			log($"{posName[pos]}: {games} games, own level {own:F1}, a season {total.Plays * k144:F0} balls in play{(pos == 0 ? $" and {total.Steals * k144:F0} steal chances" : "")}; runs saved a season against his own level:");
			int i = 0;
			foreach (string p in parts.Where(p => p != "none"))
			{
				List<string> row = new List<string>();
				Dictionary<double, double> saved = new Dictionary<double, double>();
				foreach ((string part, double lv) in vs.Where(v => v.part == p))
				{
					saved[lv] = -total.Delta(++i, re) * k144;
					row.Add(p == "speed" ? $"speed {lv:+0;-0}: {saved[lv]:+0.00;-0.00}" : $"{lv:F0}: {saved[lv]:+0.00;-0.00}");
				}
				string per10 = saved.ContainsKey(40) && saved.ContainsKey(95) ? $"  | per 10 points {(saved[95] - saved[40]) / 5.5:F2} (65->95 {saved[95] - (saved.TryGetValue(65, out double s65) ? s65 : 0):F2})" : p == "speed" ? $"  | per 10 points {(saved[10] - saved[-10]) / 2:F2}" : "";
				log($"  {p,-6} " + string.Join("  ", row) + per10);
			}
		}

		// --stealvalue (engine session, 2026-11-11; user 18:4x: "도루의 팀적가치 손익분기점이 70%인가 그런데, 그 미만인 선수는
		// 손해일거고, 그 이상인 선수는 이득이지 않을까"): the run expectancy of every base-out state from the games themselves
		// (RE24 at each plate appearance, innings 1-8), the break-even odds of each steal state from it, and every attempt
		// valued by the state it leaves (runs on the play + RE after − RE before), by the runner's odds as the engine drew
		// them, his 주력 and the catcher's defence. The home side plays 도루 성향 `running` (the away side 50); the same
		// teams and seeds every run, so runs differ only by the attempts the setting adds or takes away.
		private struct StealTry
		{
			public byte Team, Before, After;   // after 24 = side retired
			public bool Safe;
			public float Odds, Speed, Catcher;
		}

		private sealed class StealCount : IGameRecorder
		{
			public readonly double[] ReSum = new double[24];
			public readonly long[] ReN = new long[24];
			public readonly List<StealTry> Tries = new List<StealTry>();
			public SeasonSimulator Sim;
			public double[] RunX;
			private readonly List<(int state, int runs)> m_half = new List<(int, int)>();
			private int m_runs;

			public void HalfInningStart(int inning, bool bottom)
			{
				m_half.Clear();
				m_runs = 0;
			}

			public void HalfInningEnd(int inning, bool bottom)
			{
				if (inning > 8) return;   // walk-offs and unplayed halves cut the ninth short
				foreach ((int s, int r) in m_half)
				{
					ReSum[s] += m_runs - r;
					ReN[s]++;
				}
			}

			public void PitcherChange(int team, int pitcher) { }

			public void Play(PlayInfo p)
			{
				int before = p.OutsBefore * 8 + (p.Before[1] >= 0 ? 1 : 0) + (p.Before[2] >= 0 ? 2 : 0) + (p.Before[3] >= 0 ? 4 : 0);
				if (p.Kind is not (PlayKind.StealSafe or PlayKind.StealOut))
				{
					m_half.Add((before, m_runs));
					m_runs += p.Runs;
					return;
				}
				int outs = p.OutsBefore, bases = 0, runner = -1;
				for (int i = 1; i < 4; i++)
				{
					if (p.Before[i] < 0) continue;
					int d = p.Dest[i] == 0 ? i : p.Dest[i];
					if (p.Dest[i] != 0) runner = p.Before[i];
					if (d >= 5) outs++;
					else if (d <= 3) bases |= 1 << (d - 1);
				}
				(double odds, double catcher) = Sim.StealOdds(runner, 1 - p.Team);
				Tries.Add(new StealTry
				{
					Team = (byte)p.Team, Before = (byte)before, After = (byte)(outs >= 3 ? 24 : outs * 8 + bases), Safe = p.Kind == PlayKind.StealSafe,
					Odds = (float)odds, Speed = (float)(65 + 10 * RunX[runner]), Catcher = (float)catcher
				});
				m_runs += p.Runs;
			}

			public void Merge(StealCount o)
			{
				for (int s = 0; s < 24; s++)
				{
					ReSum[s] += o.ReSum[s];
					ReN[s] += o.ReN[s];
				}
				Tries.AddRange(o.Tries);
			}
		}

		public static void StealValue(string dir, int games, int running, int threads, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(7);
			(GameTeam home, GameTeam away)[] teams = new (GameTeam, GameTeam)[games];
			for (int g = 0; g < games; g++)
			{
				teams[g] = (MakeTeam(regulars, rnd), MakeTeam(regulars, rnd));
				teams[g].home.Tactics = new GameTactics { Running = running };
			}
			StealCount total = new StealCount();
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
				() => new StealCount(),
				(g, _, sc) =>
				{
					SeasonModel model = CardGame.Build(teams[g].home, teams[g].away, env);
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
					sc.Sim = sim;
					sc.RunX = model.RunX;
					sim.PlayOne(0, 1, sc);
					return sc;
				},
				sc =>
				{
					lock (gate) total.Merge(sc);
				});
			double[] re = new double[25];
			for (int s = 0; s < 24; s++) re[s] = total.ReN[s] > 0 ? total.ReSum[s] / total.ReN[s] : 0;
			string[] baseName = { "---", "1--", "-2-", "12-", "--3", "1-3", "-23", "123" };
			log($"steal setting: home {running}, away 50; rule {(CardGame.Rules.StealByOdds ? "by odds (before 11-11)" : "every runner alike (11-11)")}; {games} games");
			for (int o = 0; o < 3; o++)
			{
				log($"  RE24 {o} out: " + string.Join("  ", Enumerable.Range(0, 8).Select(b => $"{baseName[b]} {re[o * 8 + b]:F3}")));
			}
			double Value(StealTry t) => re[t.After] - re[t.Before];
			// Break-even odds of each steal state: p·RE(safe) + (1 − p)·RE(out) = RE(before).
			log("  break-even odds (attempts in this run):");
			double beSum = 0, beN = 0;
			foreach ((int b, int safeB, int outB, string what) in new[] { (1, 2, 0, "2nd from 1--"), (5, 6, 4, "2nd from 1-3"), (2, 4, 0, "3rd from -2-") })
			{
				for (int o = 0; o < 2; o++)
				{
					int s = o * 8 + b, ok = o * 8 + safeB, bad = (o + 1) * 8 + outB;
					double be = (re[s] - re[bad]) / (re[ok] - re[bad]);
					int n = total.Tries.Count(t => t.Before == s);
					beSum += be * n;
					beN += n;
					log($"    {what} {o} out: {100 * be:F1}%  (safe {re[ok] - re[s]:+0.000;-0.000} runs, out {re[bad] - re[s]:+0.000;-0.000}; {n} attempts)");
				}
			}
			log($"    attempt-weighted {100 * beSum / Math.Max(1, beN):F1}%");
			double k144 = 144.0 / games;
			void Table(string title, Func<StealTry, double> key, double[] cuts, string fmt)
			{
				log($"  {title}:");
				for (int team = 0; team < 2; team++)
				{
					List<StealTry> mine = total.Tries.Where(t => t.Team == team).ToList();
					log($"    {(team == 0 ? $"home ({running})" : "away (50)")}: attempts {mine.Count * k144:F1} a season, safe {100.0 * mine.Count(t => t.Safe) / Math.Max(1, mine.Count):F1}%, runs {mine.Sum(Value) * k144:+0.00;-0.00} a season");
					for (int i = 0; i <= cuts.Length; i++)
					{
						double lo = i == 0 ? double.MinValue : cuts[i - 1], hi = i == cuts.Length ? double.MaxValue : cuts[i];
						List<StealTry> bin = mine.Where(t => key(t) >= lo && key(t) < hi).ToList();
						if (bin.Count == 0) continue;
						string label = i == 0 ? $"< {cuts[0].ToString(fmt)}" : i == cuts.Length ? $">= {cuts[^1].ToString(fmt)}" : $"{lo.ToString(fmt)}-{hi.ToString(fmt)}";
						log($"      {label,-11} attempts {bin.Count * k144,6:F2} a season, odds {100 * bin.Average(t => t.Odds):F1}%, safe {100.0 * bin.Count(t => t.Safe) / bin.Count:F1}%, runs per attempt {bin.Average(Value):+0.000;-0.000}, a season {bin.Sum(Value) * k144:+0.00;-0.00}");
					}
				}
			}
			Table("by the runner's odds", t => t.Odds, new[] { 0.55, 0.60, 0.65, 0.70, 0.75, 0.80, 0.85 }, "0.00");
			Table("by the runner's speed (card, as played)", t => t.Speed, new[] { 50.0, 60, 70, 80, 90, 100, 110 }, "F0");
			Table("by the catcher's defence", t => t.Catcher, new[] { 50.0, 60, 70, 80, 90 }, "F0");
		}

		// --runningtest (engine session, 2026-11-11): what the 도루 성향 slider is worth to a team, the same games with the
		// home side at 50 and at `running` (common seeds): runs scored and allowed a game both ways, win%, its steals and
		// its runners on hits. --aggr 0 leaves the runners on hits as at 50 (steals only), --runtry 0 the steals (runners on
		// hits only); 97쌍방울·25LG's tcsim had 도루 100 at +2.7 / +2.3%p.
		private sealed class RunCount : IGameRecorder
		{
			public long Sb, Cs, S1n, S1to3, S2n, S2home, D1n, D1home, Thrown;

			public void HalfInningStart(int inning, bool bottom) { }

			public void HalfInningEnd(int inning, bool bottom) { }

			public void PitcherChange(int team, int pitcher) { }

			public void Play(PlayInfo p)
			{
				if (p.Team != 0) return;
				if (p.Kind == PlayKind.StealSafe) { Sb++; return; }
				if (p.Kind == PlayKind.StealOut) { Cs++; return; }
				if (p.Kind == PlayKind.Single && p.Before[1] >= 0) { S1n++; if (p.Dest[1] == 3 || p.Dest[1] == 4) S1to3++; }
				if (p.Kind == PlayKind.Single && p.Before[2] >= 0) { S2n++; if (p.Dest[2] == 4) S2home++; }
				if (p.Kind == PlayKind.Double && p.Before[1] >= 0) { D1n++; if (p.Dest[1] == 4) D1home++; }
				for (int i = 1; i < 4; i++)
				{
					if (p.Before[i] >= 0 && p.Dest[i] >= 5 && p.Kind is PlayKind.Single or PlayKind.Double or PlayKind.Triple) Thrown++;
				}
			}

			public void Merge(RunCount o)
			{
				Sb += o.Sb; Cs += o.Cs; S1n += o.S1n; S1to3 += o.S1to3; S2n += o.S2n; S2home += o.S2home; D1n += o.D1n; D1home += o.D1home; Thrown += o.Thrown;
			}
		}

		// Band: only home batters whose card 주력 is in [lo, hi) get `running` as their 개인작전 (the team stays at 50).
		public static (double lo, double hi) RunningBand = (double.MinValue, double.MaxValue);

		public static void RunningTest(string dir, int games, int running, int threads, Action<string> log)
		{
			bool banded = RunningBand.lo > double.MinValue || RunningBand.hi < double.MaxValue;
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(7);
			(GameTeam home, GameTeam away)[] teams = new (GameTeam, GameTeam)[games];
			for (int g = 0; g < games; g++) teams[g] = (MakeTeam(regulars, rnd), MakeTeam(regulars, rnd));
			int[,] scored = new int[2, games], allowed = new int[2, games];
			RunCount[] total = { new RunCount(), new RunCount() };
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
				() => new[] { new RunCount(), new RunCount() },
				(g, _, rc) =>
				{
					for (int arm = 0; arm < 2; arm++)
					{
						teams[g].home.Tactics = new GameTactics { Running = arm == 0 || banded ? 50 : running };
						foreach (GameCard c in teams[g].home.Order)
						{
							c.Own = arm == 1 && banded && c.Row.R[3] >= RunningBand.lo && c.Row.R[3] < RunningBand.hi ? new GameTactics { Running = running } : null;
						}
						SeasonModel model = CardGame.Build(teams[g].home, teams[g].away, env);
						SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
						SimStats st = sim.PlayOne(0, 1, rc[arm]);
						foreach (int i in model.TeamBatters[0]) scored[arm, g] += (int)st.Bat(i, BS.R);
						foreach (int i in model.TeamBatters[1]) allowed[arm, g] += (int)st.Bat(i, BS.R);
					}
					return rc;
				},
				rc =>
				{
					lock (gate)
					{
						total[0].Merge(rc[0]);
						total[1].Merge(rc[1]);
					}
				});
			TacticRules R = CardGame.Rules;
			if (banded)
			{
				double per = teams.Average(t => t.home.Order.Count(c => c.Row.R[3] >= RunningBand.lo && c.Row.R[3] < RunningBand.hi));
				log($"  only batters with 주력 {RunningBand.lo}-{RunningBand.hi} ({per:F2} a lineup) set to {running}, the team at 50");
			}
			log($"running test: home 50 vs {running} (away 50), {games} games, steals {(R.RunTry == 0 ? "as at 50" : R.StealByOdds ? "by odds (before 11-11)" : "every runner alike")}, runners on hits x{R.RunAggression}");
			(double mean, double se) Diff(Func<int, double> d)
			{
				double s = 0, s2 = 0;
				for (int g = 0; g < games; g++) { double x = d(g); s += x; s2 += x * x; }
				double mean = s / games;
				return (mean, Math.Sqrt(Math.Max(0, s2 / games - mean * mean) / games));
			}
			double Win(int arm, int g) => scored[arm, g] > allowed[arm, g] ? 1 : scored[arm, g] == allowed[arm, g] ? 0.5 : 0;
			for (int arm = 0; arm < 2; arm++)
			{
				RunCount c = total[arm];
				double tg = games;
				log($"  home {(arm == 0 ? 50 : running)}: R/G {Enumerable.Range(0, games).Average(g => scored[arm, g]):F3}, allowed {Enumerable.Range(0, games).Average(g => allowed[arm, g]):F3}, win {100 * Enumerable.Range(0, games).Average(g => Win(arm, g)):F2}%"
					+ $" | SB {c.Sb / tg:F3} CS {c.Cs / tg:F3} a game ({100.0 * c.Sb / Math.Max(1, c.Sb + c.Cs):F1}%) | 1st->3rd on 1B {(double)c.S1to3 / Math.Max(1, c.S1n):F3}, 2nd->home on 1B {(double)c.S2home / Math.Max(1, c.S2n):F3}, 1st->home on 2B {(double)c.D1home / Math.Max(1, c.D1n):F3}, out on hits {c.Thrown / tg:F3} a game");
			}
			(double ds, double dsSe) = Diff(g => scored[1, g] - scored[0, g]);
			(double da, double daSe) = Diff(g => allowed[1, g] - allowed[0, g]);
			(double dw, double dwSe) = Diff(g => Win(1, g) - Win(0, g));
			log($"  difference: runs {ds:+0.000;-0.000} ± {dsSe:F3} a game ({144 * ds:+0.0;-0.0} a season), allowed {da:+0.000;-0.000} ± {daSe:F3}, win {100 * dw:+0.00;-0.00} ± {100 * dwSe:F2}%p");
		}

		// --teamvalue (engine session, 2026-11-11, for TeamValue: the 특성 자동 배분 and the 자동 편성, user 18:4x 10·11번): the
		// constants of a quick, deterministic strength count — each plate-appearance event's run value in this engine (runs
		// from it to the end of the half-inning less the run expectancy of its base-out state, innings 1-8), plate
		// appearances a game by batting slot, and batters faced a team-game by deck slot (today's starter, RP1-4, SU, CP).
		public static readonly string[] EventNames = { "K", "BB", "HBP", "HR", "1B", "2B", "3B", "out", "ROE", "SH" };

		public static int EventOf(PlayKind k) => k switch
		{
			PlayKind.Strikeout => 0,
			PlayKind.Walk => 1,
			PlayKind.HitByPitch => 2,
			PlayKind.HomeRun => 3,
			PlayKind.Single or PlayKind.BuntHit => 4,
			PlayKind.Double => 5,
			PlayKind.Triple => 6,
			PlayKind.Error => 8,
			PlayKind.SacBunt => 9,
			_ => 7
		};

		private sealed class ValueCount : IGameRecorder
		{
			public readonly double[,] RestSum = new double[24, 10];
			public readonly long[,] N = new long[24, 10];
			public readonly long[] PaBySlot = new long[9];
			public readonly long[] BfBySlot = new long[11];   // 0 = the game's starter, 5-10 = RP1-4, SU, CP, 1-4 = other starters in relief
			public long Starts, StarterOuts;
			// Starters by pitch limit (10-pitch bins from 30): starts, batters faced (TeamValue's starter by limit).
			public readonly long[] LimitStarts = new long[16], LimitBf = new long[16];
			public SeasonModel Model;
			private readonly List<(int state, int ev, int runs)> m_half = new List<(int, int, int)>();
			private int m_runs;

			public void HalfInningStart(int inning, bool bottom)
			{
				m_half.Clear();
				m_runs = 0;
			}

			public void HalfInningEnd(int inning, bool bottom)
			{
				if (inning > 8) return;
				foreach ((int s, int ev, int r) in m_half)
				{
					RestSum[s, ev] += m_runs - r;
					N[s, ev]++;
				}
			}

			public void PitcherChange(int team, int pitcher) { }

			public void Play(PlayInfo p)
			{
				if (p.Kind is PlayKind.StealSafe or PlayKind.StealOut)
				{
					m_runs += p.Runs;
					return;
				}
				int state = p.OutsBefore * 8 + (p.Before[1] >= 0 ? 1 : 0) + (p.Before[2] >= 0 ? 2 : 0) + (p.Before[3] >= 0 ? 4 : 0);
				m_half.Add((state, EventOf(p.Kind), m_runs));
				m_runs += p.Runs;
				PaBySlot[p.Slot]++;
				GameDeck d = Model.Decks[1 - p.Team];
				int slot = d.SlotOf(p.Pitcher);
				BfBySlot[slot == d.StarterSlot ? 0 : slot >= 5 ? slot : Math.Max(1, slot)]++;
			}

			public void Merge(ValueCount o)
			{
				for (int s = 0; s < 24; s++) for (int e = 0; e < 10; e++) { RestSum[s, e] += o.RestSum[s, e]; N[s, e] += o.N[s, e]; }
				for (int i = 0; i < 9; i++) PaBySlot[i] += o.PaBySlot[i];
				for (int i = 0; i < 11; i++) BfBySlot[i] += o.BfBySlot[i];
				for (int i = 0; i < 16; i++) { LimitStarts[i] += o.LimitStarts[i]; LimitBf[i] += o.LimitBf[i]; }
				Starts += o.Starts;
				StarterOuts += o.StarterOuts;
			}
		}

		public static void TeamValueConstants(string dir, int games, int threads, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(7);
			(GameTeam home, GameTeam away)[] teams = new (GameTeam, GameTeam)[games];
			for (int g = 0; g < games; g++) teams[g] = (MakeTeam(regulars, rnd), MakeTeam(regulars, rnd));
			// Half the games: the home side's starter of the day with 체력 drawn from 40-100 (relievers starting, as user decks do).
			for (int g = 0; g < games; g += 2)
			{
				GameTeam h = teams[g].home;
				CardRow c = h.Pitchers[h.StarterSlot].Row.Clone();
				c.R[0] = 40 + rnd.Next(61);
				h.Pitchers[h.StarterSlot] = new GameCard { Row = c, Bats = h.Pitchers[h.StarterSlot].Bats, Throws = h.Pitchers[h.StarterSlot].Throws, LowSlot = h.Pitchers[h.StarterSlot].LowSlot };
			}
			ValueCount total = new ValueCount();
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads },
				() => new ValueCount(),
				(g, _, vc) =>
				{
					SeasonModel model = CardGame.Build(teams[g].home, teams[g].away, env);
					vc.Model = model;
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, vc);
					for (int t = 0; t < 2; t++)
					{
						int sp = model.Decks[t].Pitchers[model.Decks[t].StarterSlot];
						vc.Starts++;
						vc.StarterOuts += st.Pit(sp, PS.Outs);
						if (model.Tac != null)
						{
							int bin = Math.Clamp((int)((model.Tac.Limit[sp] - 30) / 10), 0, 15);
							vc.LimitStarts[bin]++;
							vc.LimitBf[bin] += (long)st.Pit(sp, PS.BF);
						}
					}
					return vc;
				},
				vc =>
				{
					lock (gate) total.Merge(vc);
				});
			double[] re = new double[24];
			long all = 0;
			double allSum = 0;
			for (int s = 0; s < 24; s++)
			{
				double sum = 0;
				long n = 0;
				for (int e = 0; e < 10; e++) { sum += total.RestSum[s, e]; n += total.N[s, e]; }
				re[s] = n > 0 ? sum / n : 0;
				all += n;
			}
			double[] w = new double[10];
			long[] count = new long[10];
			for (int e = 0; e < 10; e++)
			{
				double v = 0;
				for (int s = 0; s < 24; s++)
				{
					v += total.RestSum[s, e] - total.N[s, e] * re[s];
					count[e] += total.N[s, e];
				}
				w[e] = count[e] > 0 ? v / count[e] : 0;
				allSum += v;
			}
			double tg = 2.0 * games;
			log($"team value constants, {games} games (innings 1-8 for the run values):");
			log("  run value of an event (runs to the end of the half-inning less RE24 of its state): " + string.Join("  ", Enumerable.Range(0, 10).Select(e => $"{EventNames[e]} {w[e]:+0.000;-0.000} ({100.0 * count[e] / all:F1}%)")) + $"  | mean {allSum / all:+0.0000;-0.0000}");
			log("  plate appearances a team-game by batting slot: " + string.Join(" ", total.PaBySlot.Select(n => (n / tg).ToString("F3"))));
			string[] slotName = { "SP(today)", "SP(relief)", "", "", "", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };
			log("  batters faced a team-game by deck slot: " + string.Join("  ", Enumerable.Range(0, 11).Where(i => i == 0 || i == 1 || i >= 5).Select(i => $"{slotName[i]} {total.BfBySlot[i] / tg:F2}"))
				+ $"  | starters {total.StarterOuts / 3.0 / total.Starts:F2} IP a start");
			log("  starter's batters faced by his pitch limit: " + string.Join("  ", Enumerable.Range(0, 16).Where(b => total.LimitStarts[b] > 50)
				.Select(b => $"{30 + 10 * b}-{39 + 10 * b} {(double)total.LimitBf[b] / total.LimitStarts[b]:F1} ({total.LimitStarts[b]})")));
		}

		// Win% a side gains when every batter (or pitcher) of the home team gets +plus on one stat:
		// the same games with and without the boost (common seeds), home win% difference.
		// The home team as StatValue boosts it: every batter (or pitcher in StatValueSlots) +plus on one stat (bat:5 = defence
		// at every position he can field; pit:4 with his pitch ratings; stat -1 = all six).
		private static GameTeam Boosted(GameTeam home, string side, int stat, double plus)
		{
			GameTeam h = new GameTeam { Name = home.Name, StarterSlot = home.StarterSlot };
			Array.Copy(home.Position, h.Position, home.Position.Length);
			for (int i = 0; i < 9; i++)
			{
				CardRow c = home.Order[i].Row;
				if (side == "bat") { c = c.Clone(); if (stat == 5) { for (int p = 0; p < 8; p++) if (c.Defs[p] > 20) c.Defs[p] += plus; } else c.R[stat] += plus; }
				h.Order[i] = Card(c);
			}
			for (int i = 0; i < 11; i++)
			{
				if (home.Pitchers[i] == null) continue;
				CardRow c = home.Pitchers[i].Row;
				if (side == "pit" && i >= StatValueSlots.lo && i <= StatValueSlots.hi)
				{
					c = c.Clone();
					for (int k = 0; k < 6; k++) if (stat == k || stat < 0) c.R[k] += plus;
					if (stat < 0 || stat == 4) c.Pitches = c.Pitches?.Select(x => (x.code, x.rating + plus)).ToList();
				}
				h.Pitchers[i] = Card(c);
			}
			return h;
		}

		// --teamvaluecheck: TeamValue's change for the same boosts --statvalue plays (the same teams: seed 11), to set and check
		// its constants against the full games.
		public static void TeamValueCheck(string dir, int teams, string[] specs, double plus, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(11);
			double[] off = new double[specs.Length], def = new double[specs.Length];
			TeamValue.Parts[] sum = specs.Select(_ => new TeamValue.Parts()).ToArray();
			double[,] evBase = new double[specs.Length, 7], evBoost = new double[specs.Length, 7];
			// The measured side's events a PA from the talents: our batters by their plate appearances, or batters against our
			// pitchers by the batters each slot faces.
			void Events(SeasonModel m, string side, double[,] into, int k)
			{
				double w = 0;
				double[] acc = new double[7];
				PitcherTalent oppP = TeamValue.TypicalPitcher(m, 1);
				BatterTalent oppB = TeamValue.TypicalBatter(m, 1);
				if (side == "bat")
				{
					for (int s = 0; s < 9; s++)
					{
						var x = TeamValue.Matchup(m.BT[m.TeamBatters[0][s]], oppP, m.League);
						double[] e = TeamValue.Events(x.k, x.bb, x.hbp, x.hr, x.babip, x.dbl, x.tpl);
						for (int j = 0; j < 7; j++) acc[j] += TeamValue.PaBySlot[s] * e[j];
						w += TeamValue.PaBySlot[s];
					}
				}
				else
				{
					GameDeck d = m.Decks[0];
					for (int s = 0; s < 11; s++)
					{
						if (d.Pitchers[s] < 0) continue;
						double bf = s < 5 ? TeamValue.BfStart / 5 : TeamValue.BfRelief[s - 5];
						var x = TeamValue.Matchup(oppB, m.PT[d.Pitchers[s]], m.League);
						double[] e = TeamValue.Events(x.k, x.bb, x.hbp, x.hr, x.babip, x.dbl, x.tpl);
						for (int j = 0; j < 7; j++) acc[j] += bf * e[j];
						w += bf;
					}
				}
				for (int j = 0; j < 7; j++) into[k, j] += acc[j] / w;
			}
			for (int g = 0; g < teams; g++)
			{
				GameTeam home = MakeTeam(regulars, rnd), away = MakeTeam(regulars, rnd);
				SeasonModel bm = CardGame.Build(home, away, env);
				TeamValue.Parts b = TeamValue.Of(bm, 0);
				for (int k = 0; k < specs.Length; k++)
				{
					string[] sp = specs[k].Split(':');
					SeasonModel vm = CardGame.Build(Boosted(home, sp[0], int.Parse(sp[1]), plus), away, env);
					TeamValue.Parts v = TeamValue.Of(vm, 0);
					Events(bm, sp[0], evBase, k);
					Events(vm, sp[0], evBoost, k);
					off[k] += (v.Bat - b.Bat) + (v.Speed - b.Speed) + (v.Mental - b.Mental) + (v.Steal - b.Steal);
					def[k] += (v.Field - b.Field) + (v.Pitch - b.Pitch) + (v.Stamina - b.Stamina) + (v.PitMental - b.PitMental) + (v.OppSteal - b.OppSteal);
					sum[k].Bat += v.Bat - b.Bat; sum[k].Speed += v.Speed - b.Speed; sum[k].Mental += v.Mental - b.Mental; sum[k].Steal += v.Steal - b.Steal;
					sum[k].Field += v.Field - b.Field; sum[k].Pitch += v.Pitch - b.Pitch; sum[k].Stamina += v.Stamina - b.Stamina; sum[k].PitMental += v.PitMental - b.PitMental;
				}
			}
			for (int k = 0; k < specs.Length; k++)
			{
				log($"teamvalue {specs[k]} +{plus} (slots {StatValueSlots.lo}-{StatValueSlots.hi}): runs scored {off[k] / teams:+0.0000;-0.0000}, allowed {-def[k] / teams:+0.0000;-0.0000} a game"
					+ $" | bat {sum[k].Bat / teams:+0.000;-0.000} speed {sum[k].Speed / teams:+0.000;-0.000} mental {sum[k].Mental / teams:+0.000;-0.000} steal {sum[k].Steal / teams:+0.000;-0.000}"
					+ $" field {sum[k].Field / teams:+0.000;-0.000} pitch {sum[k].Pitch / teams:+0.000;-0.000} stamina {sum[k].Stamina / teams:+0.000;-0.000} pmental {sum[k].PitMental / teams:+0.000;-0.000}");
				string[] en = { "SO", "BB", "HBP", "HR", "1B", "2B", "3B" };
				log("  events a PA from the talents: " + string.Join("  ", Enumerable.Range(0, 7).Select(j => $"{en[j]} {evBase[k, j] / teams:F4}->{evBoost[k, j] / teams:F4} ({(evBoost[k, j] - evBase[k, j]) / teams:+0.0000;-0.0000})")));
			}
		}

		// --slots lo-hi: only the home pitchers in those deck slots get the boost (0-4 SP, 5-8 RP1-4, 9 SU, 10 CP; TeamValue's
		// wins per run by role), stat -1 = all six. 구위 (4) raises the pitch ratings too, as a trait's 구위 does (since
		// 2026-11-11; before, pit:4 moved the card's 구위 column only).
		public static (int lo, int hi) StatValueSlots = (0, 10);

		public static void StatValue(string dir, int games, string side, int stat, double plus, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(11);
			(GameTeam home, GameTeam away)[] teams = new (GameTeam, GameTeam)[games];
			for (int g = 0; g < games; g++) teams[g] = (MakeTeam(regulars, rnd), MakeTeam(regulars, rnd));
			// Per game and pass: runs scored and allowed by the home side, and the measured side's events (our batters', or the
			// other side's batters against our pitchers): PA, SO, BB, HBP, HR, H, 2B, 3B.
			double[,] scored = new double[2, games], allowed = new double[2, games];
			long[,] ev = new long[2, 8];
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = StatValueThreads }, g =>
			{
				long[,] mine = new long[2, 8];
				for (int pass = 0; pass < 2; pass++)
				{
					GameTeam h = pass == 0 ? teams[g].home : Boosted(teams[g].home, side, stat, plus);
					SeasonModel model = CardGame.Build(h, teams[g].away, env);
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					foreach (int i in model.TeamBatters[0]) scored[pass, g] += st.Bat(i, BS.R);
					foreach (int i in model.TeamBatters[1]) allowed[pass, g] += st.Bat(i, BS.R);
					foreach (int i in model.TeamBatters[side == "pit" ? 1 : 0])
					{
						int[] stats = { BS.PA, BS.SO, BS.BB, BS.HBP, BS.HR, BS.H, BS.D, BS.T };
						for (int k = 0; k < 8; k++) mine[pass, k] += (long)st.Bat(i, stats[k]);
					}
				}
				lock (gate) for (int p = 0; p < 2; p++) for (int k = 0; k < 8; k++) ev[p, k] += mine[p, k];
			});
			double Win(int pass, int g) => scored[pass, g] > allowed[pass, g] ? 1 : scored[pass, g] == allowed[pass, g] ? 0.5 : 0;
			double baseWins = Enumerable.Range(0, games).Sum(g => Win(0, g)), boostWins = Enumerable.Range(0, games).Sum(g => Win(1, g));
			(double mean, double se) D(Func<int, double> f)
			{
				double s = 0, s2 = 0;
				for (int g = 0; g < games; g++) { double x = f(g); s += x; s2 += x * x; }
				return (s / games, Math.Sqrt(Math.Max(0, s2 / games - (s / games) * (s / games)) / games));
			}
			(double ds, double dsSe) = D(g => scored[1, g] - scored[0, g]);
			(double da, double daSe) = D(g => allowed[1, g] - allowed[0, g]);
			if (Fielding.Clutch.Track && Fielding.Clutch.CountL > 0) log($"  mean leverage {Fielding.Clutch.SumL / Fielding.Clutch.CountL:F3} over {Fielding.Clutch.CountL} PA (norm {Fielding.Clutch.Norm})");
			log($"{side} stat {stat} +{plus}{(side == "pit" ? $" (slots {StatValueSlots.lo}-{StatValueSlots.hi})" : "")}: win% {baseWins / games:F3} -> {boostWins / games:F3} ({100 * (boostWins - baseWins) / games:+0.00;-0.00}%p, {games} games)"
				+ $"; runs scored {ds:+0.0000;-0.0000} ± {dsSe:F4}, allowed {da:+0.0000;-0.0000} ± {daSe:F4} a game");
			string[] en = { "SO", "BB", "HBP", "HR", "1B", "2B", "3B" };
			double Rate(int p, int k)
			{
				double pa = ev[p, 0];
				return k switch { 4 => (ev[p, 5] - ev[p, 6] - ev[p, 7] - ev[p, 4]) / pa, 5 => ev[p, 6] / pa, 6 => ev[p, 7] / pa, _ => ev[p, k + 1] / pa };
			}
			log($"  {(side == "pit" ? "their" : "our")} events a PA, as played: " + string.Join("  ", Enumerable.Range(0, 7).Select(k => $"{en[k]} {Rate(0, k):F4}->{Rate(1, k):F4} ({Rate(1, k) - Rate(0, k):+0.0000;-0.0000})")));
		}

		public static int StatValueThreads = 4;

		// The cards' batting and throwing hands (P_BatterTable / P_PItcherTable beside the rules, or the server's tables),
		// so test games have left-handed batters and pitchers as the server's do (GameData). Before 2026-11-01 every test
		// card batted and threw right-handed, and nearly every ground ball went to the left side.
		private static Dictionary<(char, int, int), (Hand bats, Hand throws, bool low)> s_hands;

		private static void LoadHands(string dir, Action<string> log)
		{
			s_hands = new Dictionary<(char, int, int), (Hand, Hand, bool)>();
			foreach ((char kind, string file) in new[] { ('B', "P_BatterTable.txt"), ('P', "P_PItcherTable.txt") })
			{
				string path = new[] { dir, Path.Combine(dir, "..", "tables") }.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);
				if (path == null)
				{
					log($"  ({file} not found: those cards play right-handed)");
					continue;
				}
				foreach (string line in File.ReadLines(path).Skip(1))
				{
					string[] c = line.Split('	');
					if (c.Length < 30 || !int.TryParse(c[1], out int year) || !int.TryParse(c[12], out int sidx)) continue;
					Hand bats = c[5] == "2" ? Hand.L : c[5] == "3" ? Hand.S : Hand.R;
					Hand throws = c[4] == "2" ? Hand.L : Hand.R;
					bool low = kind == 'P' && int.TryParse(c[16], out int type) && type >= 2;
					s_hands[(kind, sidx, year)] = (bats, throws, low);
				}
			}
		}

		private static GameCard Card(CardRow c)
		{
			GameCard g = new GameCard { Row = c };
			if (s_hands != null && s_hands.TryGetValue((c.Kind, c.Sidx, c.Year), out (Hand bats, Hand throws, bool low) h))
			{
				g.Bats = h.bats;
				g.Throws = h.throws;
				g.LowSlot = h.low;
			}
			return g;
		}

		private static GameTeam MakeTeam(List<CardRow> regulars, Random rnd)
		{
			GameTeam t = new GameTeam { Name = "test" };
			List<CardRow> pool = regulars.Where(c => c.Kind == 'B').OrderBy(_ => rnd.Next()).ToList();
			HashSet<CardRow> used = new HashSet<CardRow>();
			for (int p = 0; p < 8; p++)
			{
				CardRow c = pool.First(b => !used.Contains(b) && b.MainPos == p + 1);
				used.Add(c);
				t.Order[p] = Card(c);
				t.Position[p] = p;
			}
			CardRow dh = pool.First(b => !used.Contains(b));
			t.Order[8] = Card(dh);
			t.Position[8] = 8;
			List<CardRow> sp = regulars.Where(c => c.Kind == 'P' && c.Role == "SP").OrderBy(_ => rnd.Next()).Take(5).ToList();
			List<CardRow> rp = regulars.Where(c => c.Kind == 'P' && c.Role != "SP").OrderBy(_ => rnd.Next()).Take(6).ToList();
			for (int s = 0; s < 5; s++) t.Pitchers[s] = Card(sp[s]);
			for (int s = 0; s < 6; s++) t.Pitchers[5 + s] = Card(rp[s]);
			t.StarterSlot = rnd.Next(5);
			return t;
		}

		public static void Run(string dir, int games, Action<string> log)
		{
			Fielding.FieldEngine.CountKeys = true;
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			// Reference fielders: regulars' defence at their main position and their 주력.
			string[] posName = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
			log("regulars by main position (defence / 주력, n): " + string.Join("  ", Enumerable.Range(0, 8).Select(p =>
			{
				List<CardRow> at = regulars.Where(c => c.Kind == 'B' && c.MainPos == p + 1).ToList();
				return at.Count == 0 ? $"{posName[p]} -" : $"{posName[p]} {at.Average(c => c.Defs[p]):F1}/{at.Average(c => c.R[3]):F1} ({at.Count})";
			})) + $"  all batters' 주력 {regulars.Where(c => c.Kind == 'B').Average(c => c.R[3]):F1}");
			Random rnd = new Random(7);
			GameTeam Make()
			{
				GameTeam t = new GameTeam { Name = "test" };
				List<CardRow> pool = regulars.Where(c => c.Kind == 'B').OrderBy(_ => rnd.Next()).ToList();
				HashSet<CardRow> used = new HashSet<CardRow>();
				for (int pos = 0; pos < 8; pos++)
				{
					CardRow c = pool.First(b => !used.Contains(b) && b.MainPos == pos + 1);
					used.Add(c);
					t.Order[pos] = Card(c);
					t.Position[pos] = pos;
				}
				CardRow dh = pool.First(b => !used.Contains(b));
				t.Order[8] = Card(dh);
				t.Position[8] = 8;
				List<CardRow> sp = regulars.Where(c => c.Kind == 'P' && c.Role == "SP").OrderBy(_ => rnd.Next()).Take(5).ToList();
				List<CardRow> rp = regulars.Where(c => c.Kind == 'P' && c.Role != "SP").OrderBy(_ => rnd.Next()).Take(6).ToList();
				for (int s = 0; s < 5; s++) t.Pitchers[s] = Card(sp[s]);
				for (int s = 0; s < 6; s++) t.Pitchers[5 + s] = Card(rp[s]);
				t.StarterSlot = rnd.Next(5);
				return t;
			}
			long runs = 0, plays = 0, problems = 0, halfBad = 0;
			double hits = 0, ab = 0, dbl = 0, trp = 0, hr = 0, bb = 0, so = 0, sb = 0, cs = 0, gidp = 0, er = 0, rAll = 0, sfAll = 0, pa = 0;
			// Starters: outs, pitches, runs; how many went 110+ pitches, and how many were left in for 7 innings or more after
			// 5 runs or more.
			double spN = 0, spOuts = 0, spNP = 0, spR = 0, sp110 = 0, sp120 = 0, spLeft = 0, spR5 = 0;
			// Relievers (2026-11-10, the hit-reliever rule): outings, outs, pitches, runs, and outings by runs allowed.
			double rpN = 0, rpOuts = 0, rpNP = 0, rpR = 0, rp3 = 0, rp4 = 0, rp5 = 0, rpShort = 0;
			double svAll = 0, hldAll = 0, bsAll = 0, rpWins = 0;
			Checker total = new Checker(_ => "");
			Checker shown = null;
			SeasonSimulator.ScoringLog = log;
			for (int g = 0; g < games; g++)
			{
				GameTeam home = Make(), away = Make();
				SeasonModel model = CardGame.Build(home, away, env);
				Checker chk = new Checker(i => model.Batters[i].Name) { Print = g == 0 };
				chk.Into = total;
				SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
				SimStats st = sim.PlayOne(0, 1, chk);
				runs += st.Runs;
				if (chk.Runs != st.Runs) problems++;
				plays += chk.Plays;
				problems += chk.Problems;
				for (int i = 0; i < model.Batters.Length; i++)
				{
					hits += st.Bat(i, BS.H);
					ab += st.Bat(i, BS.AB);
					dbl += st.Bat(i, BS.D); trp += st.Bat(i, BS.T); hr += st.Bat(i, BS.HR); bb += st.Bat(i, BS.BB); so += st.Bat(i, BS.SO);
					sb += st.Bat(i, BS.SB); cs += st.Bat(i, BS.CS); gidp += st.Bat(i, BS.GIDP); sfAll += st.Bat(i, BS.SF); pa += st.Bat(i, BS.PA);
				}
				for (int i = 0; i < model.Pitchers.Length; i++)
				{
					er += st.Pit(i, PS.ER); rAll += st.Pit(i, PS.R);
					svAll += st.Pit(i, PS.SV); hldAll += st.Pit(i, PS.HLD); bsAll += st.Pit(i, PS.BS);
					if (st.Pit(i, PS.W) > 0 && st.Pit(i, PS.GS) == 0) rpWins++;
					if (st.Pit(i, PS.GS) > 0)
					{
						double np = st.Pit(i, PS.NP), outs = st.Pit(i, PS.Outs), r = st.Pit(i, PS.R);
						spN++; spOuts += outs; spNP += np; spR += r;
						if (np >= 110) sp110++;
						if (np >= 120) sp120++;
						if (r >= 5) spR5++;
						if (r >= 5 && outs >= 21) spLeft++;
					}
					else if (st.Pit(i, PS.G) > 0)
					{
						double outs = st.Pit(i, PS.Outs), r = st.Pit(i, PS.R);
						rpN++; rpOuts += outs; rpNP += st.Pit(i, PS.NP); rpR += r;
						if (r >= 3) rp3++;
						if (r >= 4) rp4++;
						if (r >= 5) rp5++;
						if (r >= 5 && outs <= 3) rpShort++;
					}
				}
				if (g == 0) shown = chk;
			}
			foreach (string line in shown.Lines.Take(60)) log(line);
			log($"{games} games: R/G {runs / (2.0 * games):F2}, AVG {hits / ab:F3}, plays/game {plays / (double)games:F1}, inconsistent plays {problems}");
			double tg = 2.0 * games;   // team-games
			Checker t = total;
			log($"  starters: {spOuts / 3 / Math.Max(1, spN):F2} IP, {spNP / Math.Max(1, spN):F1} pitches, {spR / Math.Max(1, spN):F2} R a start; 110+ pitches {100 * sp110 / Math.Max(1, spN):F1}%, 120+ {100 * sp120 / Math.Max(1, spN):F1}%; 5+ R {100 * spR5 / Math.Max(1, spN):F1}%, of those 7+ IP {100 * spLeft / Math.Max(1, spR5):F1}%");
			log($"  relievers: {rpN / tg:F2} a team-game, {rpOuts / 3 / Math.Max(1, rpN):F2} IP, {rpNP / Math.Max(1, rpN):F1} pitches an outing, {27 * rpR / Math.Max(1, rpOuts):F2} R/9; 3+ R {100 * rp3 / Math.Max(1, rpN):F2}%, 4+ R {100 * rp4 / Math.Max(1, rpN):F2}%, 5+ R {100 * rp5 / Math.Max(1, rpN):F2}%, 5+ R within an inning {100 * rpShort / Math.Max(1, rpN):F2}%");
			log($"  half-innings a team-game with 4+ runs {t.Big4 / tg:F3}, 5+ {t.Big5 / tg:F3}, 8+ {t.Big8 / tg:F4}");
			log($"  scoring (official {SeasonSimulator.OfficialScoring}): a 1000 games SV {1000 * svAll / games:F1}, HLD {1000 * hldAll / games:F1}, BS {1000 * bsAll / games:F1}, relievers' W {1000 * rpWins / games:F1}; wins given to a better reliever {1000.0 * SeasonSimulator.ScoringWinBest / games:F1} (starter short) + {1000.0 * SeasonSimulator.ScoringWinLater / games:F1} (brief and ineffective)");
			log($"  save spots from the 9th (TacticRules.SaveSpot {TacticRules.SaveSpot}): closer in {SeasonSimulator.SaveSpotCloser}, set-up man in {SeasonSimulator.SaveSpotSetup}, neither left {SeasonSimulator.SaveSpotNone} ({1000.0 * (SeasonSimulator.SaveSpotCloser + SeasonSimulator.SaveSpotSetup) / games:F1} changes a 1000 games)");
			log($"  BABIP {(hits - hr) / (ab - so - hr + sfAll):F3}  AB/team-game {ab / tg:F2}  PA {pa / tg:F2}");
			log($"  per team-game: 2B {dbl / tg:F2} 3B {trp / tg:F3} HR {hr / tg:F2} BB {bb / tg:F2} SO {so / tg:F2} SB {sb / tg:F2} CS {cs / tg:F2} GIDP {gidp / tg:F2} SF {t.SacFly / tg:F2} E {t.Errors / tg:F2}  earned {er / Math.Max(1, rAll):F3}");
			log($"  advancement: 1st->3rd on 1B {(double)t.S1to3 / Math.Max(1, t.S1n):F3} ({t.S1n}), 2nd->home on 1B {(double)t.S2home / Math.Max(1, t.S2n):F3} ({t.S2n}), 1st->home on 2B {(double)t.D1home / Math.Max(1, t.D1n):F3} ({t.D1n}), runners out on hits {t.Thrown / tg:F3}");
			log($"  fielding: DP {t.Dp / tg:F2} TP {t.Tp} (a season of 720 games: {t.Tp * 720.0 / games:F1}), OF assists {t.Assists / tg * 144:F1}/team-season (home {t.AssistHome / tg * 144:F1}), IF hits {100.0 * t.InfieldHits / Math.Max(1, t.Hits):F1}% of BIP hits, FC {t.Fc / tg:F2}, doubled off {t.DoubledOff / tg:F3}");
			if (Fielding.FieldEngine.On)
			{
				for (int k = 0; k < Fielding.BipLibrary.Keys - 1; k++)
				{
					long[] row = Enumerable.Range(0, 5).Select(c => Fielding.FieldEngine.KeyClass[k, c]).ToArray();
					log($"  intent {Fielding.BipLibrary.KeyName[k]} {row.Sum() / tg:F2}/team-game -> out/1B/2B/3B/HR {string.Join(" ", row.Select(x => (100.0 * x / Math.Max(1, row.Sum())).ToString("F1")))}%");
				}
			}
			log($"  plays: diving catches {t.Dives / tg:F2}, diving stops {t.DiveStops / tg:F2}, wall catches {t.WallCatches / tg:F3} per team-game");
		}
	}
}
