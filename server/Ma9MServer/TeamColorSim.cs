using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ma9MSim;

namespace Ma9MServer
{
	// "--tcsim [cap] [games]" (engine session, 2026-10-19): how strong a team-colour deck can be.
	// For each kind of team colour (one team-year, one team's all-stars, ...) the best 25-card deck
	// inside the ball cap is built, then played against the best deck with no restriction (the "free"
	// deck) at several team-colour bonuses, to find the bonus that evens it out. The user set one
	// team-year decks at +19 for batters and pitchers; the others are to be priced from this.
	public static partial class TeamColorSim
	{
		// Deck slots: 0-7 fielders C..RF, 8 DH, 9-13 bench, 14-18 SP1-5, 19-22 RP1-4, 23 SU, 24 CP.
		private const int Slots = 25, Bench = 9, FirstPitcher = 14;

		// Card values are runs per game against a league-average player, from the rates the engine
		// plays the card at (linear weights per plate appearance).
		private const double WBB = 0.69, WHBP = 0.72, W1B = 0.88, W2B = 1.25, W3B = 1.58, WHR = 2.03, WOut = -0.28;
		private const double PaPerGame = 4.2, BfPerInning = 4.3, ReliefBf = 2.0, LateBf = 2.6;

		// Runs per game per point of fielding at C..RF (the catcher also helps his pitchers).
		private static readonly double[] DefenceWeight = { 0.005, 0.001, 0.003, 0.0025, 0.004, 0.0015, 0.003, 0.002 };

		// Runs a game per point of the catcher's shown defence past FieldRules.CatcherBoostFrom.
		private const double CatcherBoostRuns = 0.017;

		private static double s_lgPerPa;

		// Fill order: scarce fielding spots first, the DH and bench after them, then the staff.
		private static readonly int[] FillOrder = { 0, 4, 2, 6, 3, 7, 5, 1, 8, 14, 15, 16, 17, 18, 24, 23, 19, 20, 21, 22, 9, 10, 11, 12, 13 };

		private static readonly Dictionary<int, string> TeamNames = new Dictionary<int, string>
		{
			[0] = "두산", [1] = "해태", [2] = "한화", [3] = "히어로즈", [4] = "현대", [5] = "KIA", [6] = "LG",
			[7] = "롯데", [8] = "삼성", [9] = "SK", [11] = "쌍방울", [13] = "NC", [19] = "kt",
			// 1993-1998 cards since 2026-11-02 (user: "26년, 98, 97, 96, 95, 94, 93까지 추가"); 챔피언스 AI decks stay 1999 on
			// (TeamYearDecks keeps its own year filter).
			[10] = "OB", [14] = "빙그레", [15] = "태평양"
		};

		private sealed class Cand
		{
			public CardInfo Info;
			public CardRow Row;
			public double Off, Stuff, Starter;
			public double[] Def = new double[8];
		}

		private sealed class Deck
		{
			public string Label;
			public Cand[] At = new Cand[Slots];
			public double Value;
			public int Balls;
		}

		private static GameData s_data;

		public static int Run(GameData data, string[] args, int flag)
		{
			s_data = data;
			League league = data.Env.League;
			s_lgPerPa = PerPa(league.K, league.BB, league.HBP, league.HR, league.Babip, league.Double, league.Triple);
			int cap = flag + 1 < args.Length && int.TryParse(args[flag + 1], out int c) ? c : 150;
			int games = flag + 2 < args.Length && int.TryParse(args[flag + 2], out int g) ? g : 2000;
			// Every season the card tables hold (1993-2026 since 2026-11-02; 1999-2025 before).
			List<Cand> all = data.Catalog.Drawable
				.Where(x => x.Year >= 1982 && TeamNames.ContainsKey(x.TeamCode) && x.Ball >= 1)
				.Select(x => Make(x)).Where(x => x != null).ToList();
			ServerLog.Info($"tcsim: {all.Count} cards, cap {cap}, {games} games per point");

			Deck free = Build("자유 덱", all, cap);
			ServerLog.Info($"tcsim: free deck {free.Balls} balls, value {free.Value:F2}: {Describe(free)}");

			if (Environment.GetEnvironmentVariable("TCSIM_DIAG") == "2")
			{
				// Per-percent sensitivities (BatT/PitT) over every card: how far do the outliers go?
				List<Cand> bats = all.Where(x => x.Info.IsBatter).ToList(), pits = all.Where(x => !x.Info.IsBatter).ToList();
				List<double>[] bt = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray(), pt = Enumerable.Range(0, 4).Select(_ => new List<double>()).ToArray();
				List<(double v, string who)> worst = new List<(double, string)>();
				for (int k = 0; k * 9 < bats.Count; k++)
				{
					GameTeam a = new GameTeam(), o = Team(free, 0, 0);
					for (int i = 0; i < 9; i++) { Cand cb = bats[Math.Min(bats.Count - 1, k * 9 + i)]; a.Order[i] = s_data.Card(cb.Info.Idx, 2); a.Position[i] = i; }
					for (int s = 0; s < 11; s++) { Cand cp = pits[Math.Min(pits.Count - 1, (k * 11 + s) % pits.Count)]; a.Pitchers[s] = s_data.Card(cp.Info.Idx, 2); }
					SeasonModel m = CardGame.Build(a, o, s_data.Env);
					for (int i = 0; i < 9; i++) for (int j = 0; j < 4; j++) { bt[j].Add(m.BatT[j][i]); worst.Add((Math.Abs(m.BatT[j][i]), $"B{j} {a.Order[i].Row.Year} {a.Order[i].Row.Name}")); }
					foreach (int p in m.TeamPitchers[0]) for (int j = 0; j < 4; j++) { pt[j].Add(m.PitT[j][p]); worst.Add((Math.Abs(m.PitT[j][p]), $"P{j} {m.Pitchers[p].Year} {m.Pitchers[p].Name}")); }
				}
				string Q(List<double> l) { var x = l.OrderBy(v => v).ToList(); return $"min {x[0]:F2} p1 {x[x.Count / 100]:F2} med {x[x.Count / 2]:F2} p99 {x[x.Count * 99 / 100]:F2} max {x[^1]:F2}"; }
				for (int j = 0; j < 4; j++) ServerLog.Info($"tcsim diag BatT[{j}] {Q(bt[j])} | PitT[{j}] {Q(pt[j])}");
				ServerLog.Info("tcsim diag largest: " + string.Join(", ", worst.OrderByDescending(w => w.v).Take(15).Select(w => $"{w.who} {w.v:F0}")));
				return 0;
			}
			if (Environment.GetEnvironmentVariable("TCSIM_DIAG") == "1")
			{
				// Win share against small bonuses, to check it rises smoothly.
				Deck doosan = Build("두산 올스타", all.Where(x => x.Info.TeamCode == 0).ToList(), cap);
				foreach (int b in new[] { 3, 5 })
				{
					// Batting totals of the deck over a few hundred games.
					long[] tot = new long[BS.Count];
					long oppPitchers = 0;
					long[] hrBy = new long[9], paBy = new long[9];
					for (int gi = 0; gi < 400; gi++)
					{
						GameTeam x = Team(doosan, b, gi), y = Team(free, 0, gi);
						TeamStrength.ApplyLeagueReference(new[] { x, y });
						SeasonModel mm = CardGame.Build(x, y, s_data.Env);
						SimStats st = new SeasonSimulator(mm, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(gi * 7919 + 13)).PlayOne(0, 1, null);
						foreach (int i in mm.TeamBatters[0]) { for (int k = 0; k < BS.Count; k++) tot[k] += st.Bat(i, k); hrBy[i % 9] += st.Bat(i, BS.HR); paBy[i % 9] += st.Bat(i, BS.PA); }
						oppPitchers += mm.TeamPitchers[1].Count(p => st.Pit(p, PS.G) > 0);
					}
					ServerLog.Info($"tcsim diag +{b} totals/400: PA {tot[BS.PA]} H {tot[BS.H]} HR {tot[BS.HR]} BB {tot[BS.BB]} SO {tot[BS.SO]} SB {tot[BS.SB]} R {tot[BS.R]} opp pitchers/game {oppPitchers / 400.0:F2} | HR/PA by slot " + string.Join(" ", Enumerable.Range(0, 9).Select(k => $"{Team(doosan, b, 0).Order[k].Row.Name}:{(double)hrBy[k] / Math.Max(1, paBy[k]):F3}")));
					GameTeam a = Team(doosan, b, 0), o = Team(free, 0, 0);
					TeamStrength.ApplyLeagueReference(new[] { a, o });
					SeasonModel m = CardGame.Build(a, o, s_data.Env);
					for (int i = 0; i < 9; i++)
					{
						BatterTalent t = m.BT[i];
						ServerLog.Info($"tcsim diag +{b} {a.Order[i].Row.Name} R {string.Join("/", a.Order[i].Row.R.Select(x => x.ToString("F0")))} K {t.K:F3} BB {t.BB:F3} HR {t.HR:F3} BABIP {t.Babip:F3} 2B {t.Double:F2}");
					}
				}
				foreach (Deck d in new[] { free, doosan })
				{
					ServerLog.Info($"tcsim diag {d.Label}: " + string.Join(" | ", Enumerable.Range(0, 7).Select(b => { var r = Play(d, free, b, games, 0); return $"+{b} {100 * r.win:F1}% {r.rf:F2}-{r.ra:F2}"; })));
				}
				return 0;
			}
			int[] teams = all.Select(x => x.Info.TeamCode).Distinct().OrderBy(x => x).ToArray();
			int[] years = all.Select(x => x.Info.Year).Distinct().OrderBy(x => x).ToArray();
			var families = new List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)>
			{
				("단일 년도 팀", (from t in teams from y in years select ($"{y} {TeamNames[t]}", (Func<CardInfo, bool>)(x => x.TeamCode == t && x.Year == y))).ToList()),
				("듀얼 년도 단일 팀", (from t in teams from y1 in years from y2 in years where y1 < y2
					select ($"{y1}+{y2} {TeamNames[t]}", (Func<CardInfo, bool>)(x => x.TeamCode == t && (x.Year == y1 || x.Year == y2)))).ToList()),
				("단일 년도 듀얼 팀", (from y in years from t1 in teams from t2 in teams where t1 < t2
					select ($"{y} {TeamNames[t1]}+{TeamNames[t2]}", (Func<CardInfo, bool>)(x => x.Year == y && (x.TeamCode == t1 || x.TeamCode == t2)))).ToList()),
				("연대 단일 팀", (from t in teams from d in new[] { 1990, 2000, 2010, 2020 }
					select ($"{d}년대 {TeamNames[t]}", (Func<CardInfo, bool>)(x => x.TeamCode == t && x.Year >= d && x.Year <= d + 9))).ToList()),
				("단일 년도 올스타", years.Select(y => ($"{y}", (Func<CardInfo, bool>)(x => x.Year == y))).ToList()),
				("단일 팀 올스타", teams.Select(t => (TeamNames[t], (Func<CardInfo, bool>)(x => x.TeamCode == t))).ToList()),
				("듀얼 팀 올스타", (from t1 in teams from t2 in teams where t1 < t2
					select ($"{TeamNames[t1]}+{TeamNames[t2]}", (Func<CardInfo, bool>)(x => x.TeamCode == t1 || x.TeamCode == t2))).ToList()),
			};

			// Strongest deck per franchise and the value of each stat (systems session, TeamColorSim.Report.cs).
			if (Array.IndexOf(args, "teams") >= 0) return RunTeams(families, all, free, cap, games);
			if (Array.IndexOf(args, "stats") >= 0) return RunStats(families, all, free, cap, games);
			if (Array.IndexOf(args, "dev") >= 0) return RunDev(families, all, free, cap, games);
			if (Array.IndexOf(args, "devstats") >= 0) return RunDevStats(families, all, free, cap, games);
			if (Array.IndexOf(args, "plan") >= 0) return RunPlan(families, all, free, cap, games);  // 선수별 육성 추천 (TeamColorSim.Plan.cs)
			if (Array.IndexOf(args, "entry") >= 0) return RunEntry(families, all, free, cap, games);  // 투수진 안을 리그 상대로 (TeamColorSim.Entry.cs)
			if (Array.IndexOf(args, "grade") >= 0)
			{
				return RunGrades(families, all, free, cap, games);
			}
			int[] bonuses = { 0, 5, 10, 15, 20, 25, 30 };
			var summary = new List<string>();
			// The free deck against itself: the noise floor (should be 50%).
			double self = Play(free, free, 0, games, 1).win;
			ServerLog.Info($"tcsim: free vs free {100 * self:F1}%");
			foreach ((string name, List<(string label, Func<CardInfo, bool> ok)> instances) in families)
			{
				List<Deck> decks = new List<Deck>();
				object gate = new object();
				Parallel.ForEach(instances, inst =>
				{
					List<Cand> pool = all.Where(x => inst.ok(x.Info)).ToList();
					Deck d = Build(inst.label, pool, cap);
					if (d != null)
					{
						lock (gate) decks.Add(d);
					}
				});
				decks = decks.OrderByDescending(d => d.Value).ToList();
				if (decks.Count == 0)
				{
					ServerLog.Info($"tcsim: {name}: no complete deck");
					continue;
				}
				ServerLog.Info($"tcsim: {name} top: " + string.Join(", ", decks.Take(8).Select(d => $"{d.Label} {d.Balls}볼 {d.Value:F2}")));
				ServerLog.Info($"tcsim: {name}: {decks.Count} of {instances.Count} complete; value best {decks[0].Value:F2}, median {decks[decks.Count / 2].Value:F2}, free {free.Value:F2}");
				// Simulated: the best deck, the 90th and 75th percentile and the median.
				var picks = new[] { ("최고", 0), ("상위10%", decks.Count / 10), ("상위25%", decks.Count / 4), ("중앙", decks.Count / 2) }
					.GroupBy(p => p.Item2).Select(p => p.First()).ToList();
				foreach ((string tag, int k) in picks)
				{
					Deck d = decks[k];
					double[] win = bonuses.Select(b => Play(d, free, b, games, 1000 + b).win).ToArray();
					double even = BreakEven(bonuses, win);
					double at19 = Play(d, free, 19, games, 2019).win;
					string line = $"{name} [{tag}] {d.Label}: {d.Balls}볼, 가치 {d.Value:F2} | 승률 {string.Join(" ", bonuses.Select((b, i) => $"+{b} {100 * win[i]:F1}%"))} | +19 {100 * at19:F1}% | 본전 보너스 {even:F1}";
					ServerLog.Info("tcsim: " + line);
					ServerLog.Info($"tcsim:    {Describe(d)}");
					summary.Add(line);
				}
			}
			ServerLog.Info("tcsim: ===== summary (cap " + cap + ") =====");
			foreach (string s in summary)
			{
				ServerLog.Info("tcsim: " + s);
			}
			return 0;
		}

		private static Cand Make(CardInfo info)
		{
			GameCard g = s_data.Card(info.Idx, 2);
			if (g?.Row == null)
			{
				return null;
			}
			CardRow r = g.Row;
			Cand c = new Cand { Info = info, Row = r };
			League lg = s_data.Env.League;
			if (info.IsBatter)
			{
				BatterTalent t = CardGame.BatterTalentOf(r, s_data.Env);
				c.Off = (PerPa(t.K, t.BB, t.HBP, t.HR, t.Babip, t.Double, t.Triple) - s_lgPerPa) * PaPerGame;
				for (int p = 0; p < 8; p++)
				{
					c.Def[p] = info.CanField(p) ? DefenceWeight[p] * (r.Defs[p] - 65) : double.NaN;
				}
				// The catcher's boost to his pitchers (FieldRules.CatcherBoost: 0.15% of their 컨·테·구위 a
				// point of shown defence past 90) is worth ~0.017 runs a game a point (measured: 양의지
				// +18 defence → +2.2%p wins; systems session, 2026-10-20). Without it the builder chose
				// hitting catchers (최형우 C 40).
				if (info.CanField(0))
				{
					c.Def[0] += CatcherBoostRuns * Math.Max(0, r.Defs[0] - s_data.Env.Field.CatcherBoostFrom);
				}
			}
			else
			{
				// Runs saved per batter faced, times the batters he faces per team game: a starter one
				// start in five, as long as his pitch limit lets him go.
				PitcherTalent t = CardGame.PitcherTalentOf(r, s_data.Env);
				c.Stuff = s_lgPerPa - PerPa(t.K, t.BB, t.HBP, t.HR, t.Babip, lg.Double, lg.Triple);
				double innings = Math.Clamp(CardGame.Rules.Limit(r.R[0]) / 16.5, 3, 8);
				c.Starter = c.Stuff * innings * BfPerInning / 5;
			}
			return c;
		}

		// Runs per plate appearance by linear weights (outs negative).
		private static double PerPa(double k, double bb, double hbp, double hr, double babip, double dbl, double tpl)
		{
			double inPlay = Math.Max(0, 1 - k - bb - hbp - hr);
			double hits = inPlay * babip;
			double outs = k + inPlay - hits;
			return WBB * bb + WHBP * hbp + WHR * hr + hits * ((1 - dbl - tpl) * W1B + dbl * W2B + tpl * W3B) + WOut * outs;
		}

		private static bool Fits(Cand c, int slot) =>
			slot < 8 ? c.Info.IsBatter && !double.IsNaN(c.Def[slot]) : slot < FirstPitcher ? c.Info.IsBatter : !c.Info.IsBatter;

		private static double Value(Cand c, int slot)
		{
			if (c == null) return 0;
			if (slot < 8) return c.Off + (double.IsNaN(c.Def[slot]) ? -15 : c.Def[slot]);  // out of position
			if (slot == 8) return c.Off;
			if (slot < FirstPitcher) return -0.001 * c.Info.Ball;  // bench: as cheap as possible
			int s = slot - FirstPitcher;
			return s < 5 ? c.Starter : c.Stuff * (s < 9 ? ReliefBf : LateBf);
		}

		// Best deck under the cap: maximise value − λ·balls slot by slot (λ by bisection), then spend
		// what is left on the best value per ball. One card per player (no one twice from two years).
		private static Deck Build(string label, List<Cand> pool, int cap)
		{
			if (pool.Count(c => c.Info.IsBatter) < 14 || pool.Count(c => !c.Info.IsBatter) < 11)
			{
				return null;
			}
			List<Cand>[] eligible = Enumerable.Range(0, Slots).Select(s => pool.Where(c => Fits(c, s)).ToList()).ToArray();
			// A spot nobody in the pool can field is played by the best spare batter.
			for (int s = 0; s < 8; s++)
			{
				if (eligible[s].Count == 0) eligible[s] = pool.Where(c => c.Info.IsBatter).ToList();
			}
			Deck Assign(double lambda)
			{
				Deck d = new Deck { Label = label };
				HashSet<int> people = new HashSet<int>();
				foreach (int s in FillOrder)
				{
					Cand best = null;
					double bestScore = double.NegativeInfinity;
					foreach (Cand c in eligible[s])
					{
						if (people.Contains(Person(c))) continue;
						double score = Value(c, s) - lambda * c.Info.Ball;
						if (score > bestScore)
						{
							bestScore = score;
							best = c;
						}
					}
					if (best == null) return null;
					d.At[s] = best;
					people.Add(Person(best));
				}
				return d;
			}
			double lo = 0, hi = 60;
			Deck fit = Assign(hi);
			if (fit == null || fit.At.Sum(c => c.Info.Ball) > cap)
			{
				return null;  // even the cheapest cards are over the cap
			}
			for (int it = 0; it < 30; it++)
			{
				double mid = (lo + hi) / 2;
				Deck d = Assign(mid);
				if (d != null && d.At.Sum(c => c.Info.Ball) <= cap)
				{
					fit = d;
					hi = mid;
				}
				else
				{
					lo = mid;
				}
			}
			// Spend the rest: the swap with the best value per ball that still fits, until none does.
			int balls = fit.At.Sum(c => c.Info.Ball);
			while (true)
			{
				HashSet<int> used = fit.At.Select(Person).ToHashSet();
				int bestSlot = -1;
				Cand bestCand = null;
				double bestScore = 0;
				for (int s = 0; s < Slots; s++)
				{
					Cand cur = fit.At[s];
					double now = Value(cur, s);
					foreach (Cand c in eligible[s])
					{
						if (used.Contains(Person(c)) || !Fits(c, s)) continue;
						double dv = Value(c, s) - now;
						int db = c.Info.Ball - cur.Info.Ball;
						if (dv <= 1e-9 || balls + db > cap) continue;
						double score = db <= 0 ? 1e6 + dv : dv / db;
						if (score > bestScore)
						{
							bestScore = score;
							bestSlot = s;
							bestCand = c;
						}
					}
				}
				if (bestSlot < 0) break;
				balls += bestCand.Info.Ball - fit.At[bestSlot].Info.Ball;
				fit.At[bestSlot] = bestCand;
			}
			fit.Balls = balls;
			fit.Value = Enumerable.Range(0, Slots).Sum(s => Value(fit.At[s], s));
			return fit;
		}

		private static int Person(Cand c) => c.Info.IsBatter ? c.Info.Sidx : -1 - c.Info.Sidx;

		private static string Describe(Deck d)
		{
			string Short(Cand c) => $"{c.Info.Year % 100:00}{c.Info.Name}{c.Info.Ball}";
			return "타 " + string.Join(" ", Enumerable.Range(0, 9).Select(s => Short(d.At[s])))
				+ " | 벤치 " + string.Join(" ", Enumerable.Range(Bench, 5).Select(s => Short(d.At[s])))
				+ " | 투 " + string.Join(" ", Enumerable.Range(FirstPitcher, 11).Select(s => Short(d.At[s])));
		}

		// The deck (with an all-stats team-colour bonus) against the opponent, home and road in turn,
		// the rotation going round; the same seeds for every bonus.
		private static (double win, double rf, double ra) Play(Deck deck, Deck opp, int bonus, int games, int salt, Func<CardInfo, bool> member = null)
		{
			double wins = 0, rf = 0, ra = 0;
			object gate = new object();
			Parallel.For(0, games, () => (0.0, 0.0, 0.0), (g, _, acc) =>
			{
				GameTeam a = Team(deck, bonus, g, member), b = Team(opp, 0, g);
				TeamStrength.ApplyLeagueReference(new[] { a, b });
				bool aHome = g % 2 == 0;
				SeasonModel m = aHome ? CardGame.Build(a, b, s_data.Env) : CardGame.Build(b, a, s_data.Env);
				SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(g * 7919 + 13)).PlayOne(0, 1, null);
				int x = m.TeamBatters[aHome ? 0 : 1].Sum(i => (int)st.Bat(i, BS.R));
				int y = m.TeamBatters[aHome ? 1 : 0].Sum(i => (int)st.Bat(i, BS.R));
				return (acc.Item1 + (x > y ? 1 : x == y ? 0.5 : 0), acc.Item2 + x, acc.Item3 + y);
			}, acc =>
			{
				lock (gate)
				{
					wins += acc.Item1;
					rf += acc.Item2;
					ra += acc.Item3;
				}
			});
			return (wins / games, rf / games, ra / games);
		}

		private static GameTeam Team(Deck d, int bonus, int game, Func<CardInfo, bool> member = null)
		{
			GameTeam t = new GameTeam { Name = d.Label };
			TeamCard tc = new TeamCard();
			for (int j = 0; j < 6; j++) tc.TeamColor[j] = (sbyte)bonus;
			// Batting order: the nine by offence; Position = fielding spot per batting slot.
			int[] order = Enumerable.Range(0, 9).OrderByDescending(s => d.At[s].Off).ToArray();
			for (int i = 0; i < 9; i++)
			{
				t.Order[i] = s_data.Card(d.At[order[i]].Info.Idx, 2);
				t.Position[i] = order[i];
				if (bonus != 0 && (member == null || member(d.At[order[i]].Info))) TeamStrength.AddBonuses(t.Order[i], tc);
			}
			for (int s = 0; s < 11; s++)
			{
				t.Pitchers[s] = s_data.Card(d.At[FirstPitcher + s].Info.Idx, 2);
				if (bonus != 0 && (member == null || member(d.At[FirstPitcher + s].Info))) TeamStrength.AddBonuses(t.Pitchers[s], tc);
			}
			t.StarterSlot = game % 5;
			return t;
		}

		// "--tcsim [cap] [games] grade" (systems session, 2026-10-19): weaker team colours for partly
		// matching decks (user: 25 of 25 members = S, 19 = A, 14 = B, 11 = C). For each kind's best
		// instance, the best deck with at least k members (the rest free picks) is built and the bonus
		// for its members that evens it with the free deck is measured. The S bonuses the user set are
		// then scaled by break-even(k) / break-even(25).
		private static int RunGrades(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int[] grades = { 25, 19, 14, 11 };
			string[] tags = { "S", "A", "B", "C" };
			int[] bonuses = { 0, 4, 8, 12, 16, 20, 24, 28 };
			var summary = new List<string>();
			foreach ((string name, List<(string label, Func<CardInfo, bool> ok)> instances) in families)
			{
				// The kind's three best complete instances (as the full study ranks them).
				List<(Deck deck, Func<CardInfo, bool> ok)> top = new List<(Deck, Func<CardInfo, bool>)>();
				object gate = new object();
				Parallel.ForEach(instances, inst =>
				{
					Deck d = Build(inst.label, all.Where(x => inst.ok(x.Info)).ToList(), cap);
					if (d == null) return;
					lock (gate) top.Add((d, inst.ok));
				});
				top = top.OrderByDescending(t => t.deck.Value).Take(3).ToList();
				if (top.Count == 0)
				{
					ServerLog.Info($"tcsim grade: {name}: no complete deck");
					continue;
				}
				// A grade is a team-wide buff: the bonus goes to every card of the deck (C), as the
				// study for the full decks does; members-only made fewer members need more each.
				double[] even = new double[grades.Length];
				string[] parts = new string[grades.Length];
				for (int g = 0; g < grades.Length; g++)
				{
					List<double> evens = new List<double>();
					List<int> counts = new List<int>();
					double zero = 0;
					foreach ((Deck full, Func<CardInfo, bool> ok) in top)
					{
						Deck d = grades[g] == Slots ? full : BuildWithMembers(full.Label + $" {grades[g]}명", all, ok, grades[g], cap);
						if (d == null) continue;
						double[] win = bonuses.Select(b => Play(d, free, b, games, 3000 + 100 * g + b).win).ToArray();
						evens.Add(BreakEven(bonuses, win));
						counts.Add(d.At.Count(c => ok(c.Info)));
						zero += win[0];
						ServerLog.Info($"tcsim grade: {name} {full.Label} {tags[g]}: {counts[^1]}명, {d.Balls}볼, 가치 {d.Value:F2} | " + string.Join(" ", bonuses.Select((b, i) => $"+{b} {100 * win[i]:F1}%")) + $" | 본전 {evens[^1]:F1}");
					}
					even[g] = evens.Count > 0 ? evens.Average() : double.NaN;
					parts[g] = evens.Count > 0 ? $"{tags[g]}({string.Join("/", counts)}명) 0보너스 {100 * zero / evens.Count:F1}% 본전 {even[g]:F1}" : $"{tags[g]} 없음";
				}
				string ratios = string.Join(" ", Enumerable.Range(0, grades.Length).Select(g => $"{tags[g]} {(even[0] > 0 ? even[g] / even[0] : double.NaN):F2}"));
				string line = $"{name} [{string.Join(", ", top.Select(t => t.deck.Label))}] " + string.Join(" | ", parts) + $" | 비율 {ratios}";
				ServerLog.Info("tcsim grade: " + line);
				summary.Add(line);
			}
			ServerLog.Info($"tcsim grade: ===== summary (cap {cap}, free value {free.Value:F2}) =====");
			foreach (string line in summary)
			{
				ServerLog.Info("tcsim grade: " + line);
			}
			return 0;
		}

		// Best deck under the cap with at least k cards for which member() holds: value − λ·balls + μ·member,
		// μ raised by bisection until the deck has k members; the final top-ups keep k.
		private static Deck BuildWithMembers(string label, List<Cand> pool, Func<CardInfo, bool> member, int k, int cap)
		{
			List<Cand>[] eligible = Enumerable.Range(0, Slots).Select(s => pool.Where(c => Fits(c, s)).ToList()).ToArray();
			Deck Assign(double lambda, double mu)
			{
				Deck d = new Deck { Label = label };
				HashSet<int> people = new HashSet<int>();
				foreach (int s in FillOrder)
				{
					Cand bestCand = null;
					double bestScore = double.NegativeInfinity;
					foreach (Cand c in eligible[s])
					{
						if (people.Contains(Person(c))) continue;
						double score = Value(c, s) - lambda * c.Info.Ball + (member(c.Info) ? mu : 0);
						if (score > bestScore)
						{
							bestScore = score;
							bestCand = c;
						}
					}
					if (bestCand == null) return null;
					d.At[s] = bestCand;
					people.Add(Person(bestCand));
				}
				return d;
			}
			Deck FitCap(double mu)
			{
				double lo = 0, hi = 60;
				Deck fit = Assign(hi, mu);
				if (fit == null || fit.At.Sum(c => c.Info.Ball) > cap) return null;
				for (int it = 0; it < 24; it++)
				{
					double mid = (lo + hi) / 2;
					Deck d = Assign(mid, mu);
					if (d != null && d.At.Sum(c => c.Info.Ball) <= cap)
					{
						fit = d;
						hi = mid;
					}
					else
					{
						lo = mid;
					}
				}
				return fit;
			}
			int Members(Deck d) => d.At.Count(c => member(c.Info));
			double muLo = 0, muHi = 20;
			Deck chosen = FitCap(muHi);
			if (chosen == null || Members(chosen) < k) return null;
			for (int it = 0; it < 20; it++)
			{
				double mid = (muLo + muHi) / 2;
				Deck d = FitCap(mid);
				if (d != null && Members(d) >= k)
				{
					chosen = d;
					muHi = mid;
				}
				else
				{
					muLo = mid;
				}
			}
			// Spend what is left on the best value per ball, never below k members.
			int balls = chosen.At.Sum(c => c.Info.Ball);
			while (true)
			{
				HashSet<int> used = chosen.At.Select(Person).ToHashSet();
				int members = Members(chosen);
				int bestSlot = -1;
				Cand bestCand = null;
				double bestScore = 0;
				for (int s = 0; s < Slots; s++)
				{
					Cand cur = chosen.At[s];
					double now = Value(cur, s);
					foreach (Cand c in eligible[s])
					{
						if (used.Contains(Person(c))) continue;
						if (members - (member(cur.Info) ? 1 : 0) + (member(c.Info) ? 1 : 0) < k) continue;
						double dv = Value(c, s) - now;
						int db = c.Info.Ball - cur.Info.Ball;
						if (dv <= 1e-9 || balls + db > cap) continue;
						double score = db <= 0 ? 1e6 + dv : dv / db;
						if (score > bestScore)
						{
							bestScore = score;
							bestSlot = s;
							bestCand = c;
						}
					}
				}
				if (bestSlot < 0) break;
				balls += bestCand.Info.Ball - chosen.At[bestSlot].Info.Ball;
				chosen.At[bestSlot] = bestCand;
			}
			chosen.Balls = balls;
			chosen.Value = Enumerable.Range(0, Slots).Sum(s => Value(chosen.At[s], s));
			return chosen;
		}

		// The bonus where the win share crosses 50% (linear between the measured points).
		private static double BreakEven(int[] b, double[] w)
		{
			if (w[0] >= 0.5) return 0;
			for (int i = 1; i < b.Length; i++)
			{
				if (w[i] >= 0.5) return b[i - 1] + (b[i] - b[i - 1]) * (0.5 - w[i - 1]) / (w[i] - w[i - 1]);
			}
			return double.PositiveInfinity;
		}
	}
}
