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

		// Win% a side gains when every batter (or pitcher) of the home team gets +plus on one stat:
		// the same games with and without the boost (common seeds), home win% difference.
		public static void StatValue(string dir, int games, string side, int stat, double plus, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			LoadHands(dir, log);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
			Random rnd = new Random(11);
			double baseWins = 0, boostWins = 0;
			for (int g = 0; g < games; g++)
			{
				GameTeam home = MakeTeam(regulars, rnd), away = MakeTeam(regulars, rnd);
				for (int pass = 0; pass < 2; pass++)
				{
					GameTeam h = home;
					if (pass == 1)
					{
						h = new GameTeam { Name = home.Name, StarterSlot = home.StarterSlot };
						Array.Copy(home.Position, h.Position, home.Position.Length);
						for (int i = 0; i < 9; i++)
						{
							CardRow c = home.Order[i].Row;
							// bat:5 = 수비: every position he can field (the game reads Defs, not the card's shown defence).
							if (side == "bat") { c = c.Clone(); if (stat == 5) { for (int p = 0; p < 8; p++) if (c.Defs[p] > 20) c.Defs[p] += plus; } else c.R[stat] += plus; }
							h.Order[i] = Card(c);
						}
						for (int i = 0; i < 11; i++)
						{
							if (home.Pitchers[i] == null) continue;
							CardRow c = home.Pitchers[i].Row;
							if (side == "pit") { c = c.Clone(); c.R[stat] += plus; }
							h.Pitchers[i] = Card(c);
						}
					}
					SeasonModel model = CardGame.Build(h, away, env);
					SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(g + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					long hr = 0, ar = 0;
					foreach (int i in model.TeamBatters[0]) hr += st.Bat(i, BS.R);
					foreach (int i in model.TeamBatters[1]) ar += st.Bat(i, BS.R);
					double w = hr > ar ? 1 : hr == ar ? 0.5 : 0;
					if (pass == 0) baseWins += w; else boostWins += w;
				}
			}
			if (Fielding.Clutch.Track && Fielding.Clutch.CountL > 0) log($"  mean leverage {Fielding.Clutch.SumL / Fielding.Clutch.CountL:F3} over {Fielding.Clutch.CountL} PA (norm {Fielding.Clutch.Norm})");
			log($"{side} stat {stat} +{plus}: win% {baseWins / games:F3} -> {boostWins / games:F3} ({100 * (boostWins - baseWins) / games:+0.00;-0.00}%p, {games} games)");
		}

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
			Checker total = new Checker(_ => "");
			Checker shown = null;
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
