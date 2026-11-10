using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ma9MSim;

namespace TraitSim
{
	// What a team gets from its traits, as levers the current engine already has.
	// Stat gains are shown points, played at the development weight. Rate multipliers act on the
	// team's own batters or pitchers; "against" effects of the pitching side act on the opposing
	// batters, which in a two-team game is the same thing.
	public sealed class Effect
	{
		public double[] Bat = new double[6];   // 컨택 파워 선구 주력 정신 수비
		public double[] Pit = new double[6];   // 체력 구속 컨트롤 테크닉 구위 정신 (all pitchers)
		public double[] Late = new double[6];  // the same, set-up man and closer only
		public double[] Start = new double[6]; // the same, the five starters only
		public double HR = 1, K = 1, BB = 1, XBH = 1, StealTry = 1, StealCaught = 1;    // own batting
		public double PK = 1, PBabip = 1, PHR = 1, PXBH = 1, PStealTry = 1, PCaught = 1; // own pitching
		public double StartOuts;               // outs added to every starter's target (old deck rules)
		public double StartLimit;              // pitches added to every starter's limit (fatigue rules)

		public Effect Clone()
		{
			Effect e = (Effect)MemberwiseClone();
			e.Bat = (double[])Bat.Clone();
			e.Pit = (double[])Pit.Clone();
			e.Late = (double[])Late.Clone();
			e.Start = (double[])Start.Clone();
			return e;
		}

		// Combine two effects: gains add, multipliers multiply.
		public static Effect operator +(Effect a, Effect b)
		{
			Effect e = a.Clone();
			for (int j = 0; j < 6; j++)
			{
				e.Bat[j] += b.Bat[j];
				e.Pit[j] += b.Pit[j];
				e.Late[j] += b.Late[j];
				e.Start[j] += b.Start[j];
			}
			e.HR *= b.HR; e.K *= b.K; e.BB *= b.BB; e.XBH *= b.XBH; e.StealTry *= b.StealTry; e.StealCaught *= b.StealCaught;
			e.PK *= b.PK; e.PBabip *= b.PBabip; e.PHR *= b.PHR; e.PXBH *= b.PXBH; e.PStealTry *= b.PStealTry; e.PCaught *= b.PCaught;
			e.StartOuts += b.StartOuts;
			e.StartLimit += b.StartLimit;
			return e;
		}

		// The same effect scaled: gains times s, multipliers to the power s.
		public Effect Scale(double s)
		{
			Effect e = Clone();
			for (int j = 0; j < 6; j++)
			{
				e.Bat[j] *= s;
				e.Pit[j] *= s;
				e.Late[j] *= s;
				e.Start[j] *= s;
			}
			double P(double m) => Math.Pow(m, s);
			e.HR = P(HR); e.K = P(K); e.BB = P(BB); e.XBH = P(XBH); e.StealTry = P(StealTry); e.StealCaught = P(StealCaught);
			e.PK = P(PK); e.PBabip = P(PBabip); e.PHR = P(PHR); e.PXBH = P(PXBH); e.PStealTry = P(PStealTry); e.PCaught = P(PCaught);
			e.StartOuts *= s;
			e.StartLimit *= s;
			return e;
		}

		public bool IsEmpty => Bat.All(x => x == 0) && Pit.All(x => x == 0) && Late.All(x => x == 0) && Start.All(x => x == 0) && StartOuts == 0 && StartLimit == 0
			&& new[] { HR, K, BB, XBH, StealTry, StealCaught, PK, PBabip, PHR, PXBH, PStealTry, PCaught }.All(x => x == 1);
	}

	public sealed class Roster
	{
		public CardRow[] Order = new CardRow[9];
		public int[] Position = new int[9];
		public CardRow[] Pitchers = new CardRow[11];
	}

	public sealed class MatchResult
	{
		public long Games, Ties;
		public double WinsA;            // ties count half
		public long RunsA, RunsB;
		public long HitsA, AbA, HitsB, AbB, HrA, HrB, SoA, SoB, BbA, BbB, SbA, CsA, SbB, CsB;
		public long OutsSpA, StartsA;   // team A starters: outs recorded and games started
		public long PitchesSpA;         // team A starters: pitches
		public double SpLimitShareA;    // team A starters: pitches / limit, summed
		public long PitchesA, PaB;      // all team A pitches, plate appearances of team B
		public long PitchersUsedA;      // team A pitchers who appeared
		public long ReliefAppsA, TiredAppsA, CloserAppsA; // season mode: team A relief outings, those over the rest share, closer outings

		public double WinPct => WinsA / Games;
		public double WinSE => 0.5 / Math.Sqrt(Games);
		public double RsPerG => RunsA / (double)Games;
		public double RaPerG => RunsB / (double)Games;
		public double RunDiff => (RunsA - RunsB) / (double)Games;
		public double RPerG => (RunsA + RunsB) / (2.0 * Games);

		public void Add(MatchResult o)
		{
			Games += o.Games; Ties += o.Ties; WinsA += o.WinsA; RunsA += o.RunsA; RunsB += o.RunsB;
			HitsA += o.HitsA; AbA += o.AbA; HitsB += o.HitsB; AbB += o.AbB; HrA += o.HrA; HrB += o.HrB;
			SoA += o.SoA; SoB += o.SoB; BbA += o.BbA; BbB += o.BbB; SbA += o.SbA; CsA += o.CsA; SbB += o.SbB; CsB += o.CsB;
			OutsSpA += o.OutsSpA; StartsA += o.StartsA; PitchesSpA += o.PitchesSpA; SpLimitShareA += o.SpLimitShareA;
			PitchesA += o.PitchesA; PaB += o.PaB; PitchersUsedA += o.PitchersUsedA;
			ReliefAppsA += o.ReliefAppsA; TiredAppsA += o.TiredAppsA; CloserAppsA += o.CloserAppsA;
		}
	}

	public sealed class Arena
	{
		public const double DevWeight = 0.34;
		public readonly GameEnv Env;
		public readonly List<Roster> Rosters = new List<Roster>();
		// Batting and throwing hands from the server card tables (CardRow has none).
		public static readonly Dictionary<CardRow, (Hand bats, Hand throws, bool low)> Hands = new Dictionary<CardRow, (Hand, Hand, bool)>();
		// League reference (TeamStrength on the server): off = absolute stats.
		public static bool LeagueReference;
		public static bool Debug;
		public static double[] RefBat, RefPit;

		public Arena(string dataDir, int rosters, int seed, double minShare)
		{
			Env = CardGame.LoadEnv(dataDir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dataDir);
			LoadHands(Path.Combine(dataDir, "..", "tables"), cards);
			double[] Mean(char kind)
			{
				double[] m = new double[6];
				double w = 0;
				foreach (CardRow r in cards.Values.Where(c => c.Kind == kind))
				{
					double s = Math.Clamp(r.Share, 0, 1);
					for (int j = 0; j < 6; j++) m[j] += s * r.R[j];
					w += s;
				}
				return m.Select(x => x / w).ToArray();
			}
			double[] Avg(IEnumerable<CardRow> rs) { List<CardRow> l = rs.ToList(); return Enumerable.Range(0, 6).Select(j => l.Average(r => r.R[j])).ToArray(); }
			List<CardRow> reg = cards.Values.Where(c => c.Share >= 0.9).ToList();
			List<CardRow> regB = reg.Where(c => c.Kind == 'B').ToList();
			double[][] byPos = Enumerable.Range(1, 8).Select(p => Avg(regB.Where(c => c.MainPos == p))).Append(Avg(regB)).ToArray();
			RefBat = Enumerable.Range(0, 6).Select(j => byPos.Average(m => m[j])).ToArray();
			double[] sp = Avg(reg.Where(c => c.Kind == 'P' && c.Role == "SP")), rp = Avg(reg.Where(c => c.Kind == 'P' && c.Role != "SP"));
			RefPit = sp.Zip(rp, (a, b) => 0.59 * a + 0.41 * b).ToArray();
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= minShare).ToList();
			Random rnd = new Random(seed);
			for (int r = 0; r < rosters; r++)
			{
				Rosters.Add(MakeRoster(regulars, rnd));
			}
		}

		private static double Bat3(CardRow c) => c.R[0] + c.R[1] + c.R[2];

		private static void LoadHands(string tables, Dictionary<(char, int, int), CardRow> cards)
		{
			foreach ((string file, char kind) in new[] { ("P_BatterTable.txt", 'B'), ("P_PItcherTable.txt", 'P') })
			{
				string path = Path.Combine(tables, file);
				if (!File.Exists(path)) continue;
				foreach (string line in File.ReadLines(path).Skip(1))
				{
					string[] c = line.Split('	');
					if (c.Length < 30) continue;
					if (!cards.TryGetValue((kind, int.Parse(c[12]), int.Parse(c[1])), out CardRow row)) continue;
					int hit = int.Parse(c[5]), thr = int.Parse(c[4]);
					Hands[row] = (hit == 2 ? Hand.L : hit == 3 ? Hand.S : Hand.R, thr == 2 ? Hand.L : Hand.R, kind == 'P' && int.Parse(c[16]) >= 2);
				}
			}
		}

		private static GameCard Card(CardRow shown, CardRow played)
		{
			GameCard g = new GameCard { Row = played };
			if (Hands.TryGetValue(shown, out var h))
			{
				g.Bats = h.bats;
				g.Throws = h.throws;
				g.LowSlot = h.low;
			}
			return g;
		}

		// TeamStrength.ApplyLeagueReference: the two clubs' playing-time-weighted means moved back to the reference.
		private static readonly double[] SlotWeight = { 0.118, 0.118, 0.118, 0.118, 0.118, 0.06, 0.06, 0.06, 0.06, 0.085, 0.085 };

		private static void ApplyReference(GameTeam[] league)
		{
			double[] bat = new double[6], pit = new double[6];
			double wb = 0, wp = 0, def = 0, wd = 0;
			foreach (GameTeam t in league)
			{
				for (int k = 0; k < 9; k++)
				{
					CardRow c = t.Order[k].Row;
					for (int j = 0; j < 6; j++) bat[j] += c.R[j];
					wb++;
					if (t.Position[k] < 8) { def += c.Defs[t.Position[k]]; wd++; }
				}
				for (int s = 0; s < 11; s++)
				{
					CardRow c = t.Pitchers[s].Row;
					for (int j = 0; j < 6; j++) pit[j] += SlotWeight[s] * c.R[j];
					wp += SlotWeight[s];
				}
			}
			for (int j = 0; j < 6; j++) { bat[j] = bat[j] / wb - RefBat[j]; pit[j] = pit[j] / wp - RefPit[j]; }
			pit[0] = 0;
			double defShift = def / wd - RefBat[5];
			foreach (GameTeam t in league)
			{
				foreach (GameCard g in t.Order.Concat(t.Pitchers))
				{
					CardRow c = g.Row.Clone();
					double[] sh = c.Kind == 'B' ? bat : pit;
					for (int j = 0; j < 6; j++) c.R[j] -= sh[j];
					if (c.Kind == 'B') for (int p = 0; p < 8; p++) if (c.Defs[p] > 20) c.Defs[p] -= defShift;
					g.Row = c;
				}
			}
		}

		private static Roster MakeRoster(List<CardRow> regulars, Random rnd)
		{
			Roster t = new Roster();
			List<CardRow> pool = regulars.Where(c => c.Kind == 'B').OrderBy(_ => rnd.Next()).ToList();
			HashSet<CardRow> used = new HashSet<CardRow>();
			CardRow[] at = new CardRow[9];
			for (int pos = 0; pos < 8; pos++)
			{
				CardRow c = pool.First(b => !used.Contains(b) && b.MainPos == pos + 1);
				used.Add(c);
				at[pos] = c;
			}
			at[8] = pool.First(b => !used.Contains(b));
			// A conventional order: on-base and speed leading off, the most power 4th, then 3rd and
			// 5th, the rest by overall bat.
			List<int> left = Enumerable.Range(0, 9).ToList();
			int[] order = new int[9];
			int Take(Func<CardRow, double> score)
			{
				int best = left.OrderByDescending(i => score(at[i])).First();
				left.Remove(best);
				return best;
			}
			order[0] = Take(c => c.R[0] + c.R[2] + c.R[3] - 0.5 * c.R[1]);
			order[3] = Take(c => c.R[1] + 0.3 * c.R[0]);
			order[2] = Take(c => c.R[1] + 0.6 * c.R[0]);
			order[4] = Take(c => c.R[1] + 0.3 * c.R[0]);
			order[1] = Take(c => c.R[0] + c.R[2]);
			for (int k = 5; k < 9; k++) order[k] = Take(Bat3);
			for (int k = 0; k < 9; k++)
			{
				t.Order[k] = at[order[k]];
				t.Position[k] = order[k];
			}
			List<CardRow> sp = regulars.Where(c => c.Kind == 'P' && c.Role == "SP").OrderBy(_ => rnd.Next()).Take(5).ToList();
			List<CardRow> rel = regulars.Where(c => c.Kind == 'P' && c.Role != "SP").OrderBy(_ => rnd.Next()).ToList();
			CardRow cp = rel.First(c => c.Role == "CP");
			CardRow su = rel.First(c => c.Role == "SU" && c != cp);
			List<CardRow> rp = rel.Where(c => c != cp && c != su).Take(4).ToList();
			for (int s = 0; s < 5; s++) t.Pitchers[s] = sp[s];
			for (int s = 0; s < 4; s++) t.Pitchers[5 + s] = rp[s];
			t.Pitchers[9] = su;
			t.Pitchers[10] = cp;
			return t;
		}

		// A card with shown gains, played at the development weight (gain capped at 140 shown).
		private static CardRow Raised(CardRow c, double[] gains, double w)
		{
			if (gains.All(g => g == 0))
			{
				return c;
			}
			CardRow r = c.Clone();
			double Gain(double v, double d) => Math.Max(0, Math.Min(140, v + d) - v);
			for (int j = 0; j < 6; j++)
			{
				r.R[j] += w * Gain(c.R[j], gains[j]);
			}
			if (c.Kind == 'B' && gains[5] != 0)
			{
				for (int p = 0; p < 8; p++)
				{
					if (c.Defs[p] > 20) r.Defs[p] += w * Gain(c.Defs[p], gains[5]);  // 20 = cannot play there
				}
			}
			if (c.Kind == 'P' && gains[4] != 0)
			{
				r.Pitches = c.Pitches.Select(x => (x.code, x.rating + w * Gain(x.rating, gains[4]))).ToList();
			}
			return r;
		}

		private static GameTeam Team(Roster r, Effect e, int starterSlot, double w, GameTactics tac = null)
		{
			GameTeam t = new GameTeam { Name = "t", StarterSlot = starterSlot, Tactics = tac?.Clone() ?? new GameTactics() };
			for (int k = 0; k < 9; k++)
			{
				t.Order[k] = Card(r.Order[k], e == null ? r.Order[k] : Raised(r.Order[k], e.Bat, w));
				t.Position[k] = r.Position[k];
			}
			for (int s = 0; s < 11; s++)
			{
				CardRow c = r.Pitchers[s];
				if (e != null)
				{
					double[] extra = s >= 9 ? e.Late : s < 5 ? e.Start : new double[6];
					c = Raised(c, e.Pit.Zip(extra, (a, b) => a + b).ToArray(), w);
				}
				t.Pitchers[s] = Card(r.Pitchers[s], c);
			}
			return t;
		}

		// Catcher defence (effective) that multiplies the caught-stealing share by f; the shown
		// value stays, so the catcher's boost to his pitchers does not move.
		private static void ScaleCaught(SeasonModel m, int team, double f, FieldRules fr)
		{
			if (f == 1) return;
			int i = m.Decks[team].At[0];
			double orig = m.Defs[i][0];
			double now = fr.CaughtMult((orig - 65) / 10.0);
			double y = (f * now - 1) / fr.CaughtPer10;      // X(x) wanted
			double x = y <= 3 ? y : 3 + 2 * (y - 3);
			m.CatcherShown ??= Enumerable.Repeat(double.NaN, m.Batters.Length).ToArray();
			if (double.IsNaN(m.CatcherShown[i])) m.CatcherShown[i] = orig;
			m.Defs[i] = (double[])m.Defs[i].Clone();
			m.Defs[i][0] = 65 + 10 * x;
		}

		private static void Apply(SeasonModel m, int a, Effect e)
		{
			if (e == null) return;
			int b = 1 - a;
			foreach (int i in m.TeamBatters[a])
			{
				BatterTalent t = m.BT[i];
				t.K = Math.Clamp(t.K * e.K, 0.01, 0.6);
				t.BB = Math.Clamp(t.BB * e.BB, 0.005, 0.35);
				t.HR = Math.Clamp(t.HR * e.HR, 0.0003, 0.2);
				t.Double = Math.Clamp(t.Double * e.XBH, 0.03, 0.5);
				t.Triple = Math.Clamp(t.Triple * e.XBH, 0.0005, 0.15);
				t.Steal = Math.Clamp(t.Steal * e.StealTry, 0.001, 0.6);
			}
			foreach (int i in m.TeamBatters[b])
			{
				BatterTalent t = m.BT[i];
				t.Double = Math.Clamp(t.Double * e.PXBH, 0.03, 0.5);
				t.Triple = Math.Clamp(t.Triple * e.PXBH, 0.0005, 0.15);
				t.Steal = Math.Clamp(t.Steal * e.PStealTry, 0.001, 0.6);
			}
			GameDeck d = m.Decks[a];
			for (int s = 0; s < 11; s++)
			{
				int p = d.Pitchers[s];
				if (p < 0) continue;
				PitcherTalent t = m.PT[p];
				t.K = Math.Clamp(t.K * e.PK, 0.02, 0.6);
				t.Babip = Math.Clamp(t.Babip * e.PBabip, 0.12, 0.5);
				t.HR = Math.Clamp(t.HR * e.PHR, 0.001, 0.15);
				if (s < 5) m.StartOuts[p] += e.StartOuts;
				if (s < 5 && m.Tac != null) m.Tac.Limit[p] += e.StartLimit;
			}
			ScaleCaught(m, b, e.StealCaught, m.Field);  // our runners against their catcher
			ScaleCaught(m, a, e.PCaught, m.Field);      // their runners against our catcher
		}

		// Team A (with effect ea) against team B (with eb) on every roster: A uses roster r, B uses
		// roster (r + shift) % n; shift 0 = mirror match. Half the games at each park.
		public MatchResult Play(Effect ea, Effect eb, int gamesPerRoster, ulong seed, int shift = 0, double w = DevWeight, int rosters = 0, GameTactics tacA = null, GameTactics tacB = null, Func<Roster, bool> filter = null)
		{
			MatchResult total = new MatchResult();
			object gate = new object();
			EngineParams prm = new EngineParams { RunEnvironment = Env.RunEnvironment, ReachOnError = Env.ReachOnError };
			int n = rosters > 0 ? Math.Min(rosters, Rosters.Count) : Rosters.Count;
			Parallel.For(0, n, r =>
			{
				if (filter != null && !filter(Rosters[r])) return;
				MatchResult res = new MatchResult();
				Roster ra = Rosters[r], rb = Rosters[(r + shift) % Rosters.Count];
				for (int venue = 0; venue < 2; venue++)
				{
					int a = venue;  // model team index of A: 0 = home
					GameTeam ta = Team(ra, ea, 0, w, tacA), tb = Team(rb, eb, 0, w, tacB);
					if (LeagueReference) ApplyReference(new[] { ta, tb });
					SeasonModel m = a == 0 ? CardGame.Build(ta, tb, Env) : CardGame.Build(tb, ta, Env);
					Apply(m, a, ea);
					Apply(m, 1 - a, eb);
					SeasonSimulator sim = new SeasonSimulator(m, prm, seed * 1_000_003UL + (ulong)(r * 2 + venue + 1) * 7919UL);
					int games = gamesPerRoster / 2;
					for (int g = 0; g < games; g++)
					{
						m.Decks[0].StarterSlot = m.Decks[1].StarterSlot = g % 5;
						SimStats st = sim.PlayOne(0, 1, null);
						Tally(m, st, a, res);
					}
				}
				lock (gate) total.Add(res);
			});
			return total;
		}

		// A season: A and B play every day for `days` days, home and road in turn, the rotation
		// going round; with carry, each pitcher's fatigue units stay and recover a share per day.
		// ownA: 개인작전 per staff slot for team A (null = team).
		public MatchResult PlaySeason(Effect ea, GameTactics tacA, GameTactics[] ownA, GameTactics tacB, int days, bool carry, ulong seed, int rosters = 0, int shift = 0)
		{
			MatchResult total = new MatchResult();
			object gate = new object();
			EngineParams prm = new EngineParams { RunEnvironment = Env.RunEnvironment, ReachOnError = Env.ReachOnError };
			int n = rosters > 0 ? Math.Min(rosters, Rosters.Count) : Rosters.Count;
			Parallel.For(0, n, r =>
			{
				MatchResult res = new MatchResult();
				Roster ra = Rosters[r], rb = Rosters[(r + shift) % Rosters.Count];
				double[][] state = { new double[11], new double[11] };   // by team (0 = A), staff slot
				for (int day = 0; day < days; day++)
				{
					int a = day % 2;  // A at home on even days
					GameTeam ta = Team(ra, ea, day % 5, DevWeight, tacA), tb = Team(rb, null, day % 5, DevWeight, tacB);
					if (ownA != null) for (int s = 0; s < 11; s++) ta.Pitchers[s].Own = ownA[s]?.Clone();
					if (LeagueReference) ApplyReference(new[] { ta, tb });
					SeasonModel m = a == 0 ? CardGame.Build(ta, tb, Env) : CardGame.Build(tb, ta, Env);
					Apply(m, a, ea);
					for (int k = 0; k < 2; k++)
					{
						int mt = k == 0 ? a : 1 - a;
						for (int s = 0; s < 11; s++) m.Tac.CarryIn[m.Decks[mt].Pitchers[s]] = carry ? state[k][s] : 0;
					}
					SeasonSimulator sim = new SeasonSimulator(m, prm, seed * 1_000_003UL + (ulong)(r * 1000 + day + 1) * 7919UL);
					SimStats st = sim.PlayOne(0, 1, null);
					Tally(m, st, a, res);
					for (int s = 5; s < 11; s++)
					{
						int p = m.Decks[a].Pitchers[s];
						if (st.Pit(p, PS.G) > 0)
						{
							res.ReliefAppsA++;
							if (!m.Tac.Rested(p)) res.TiredAppsA++;
							if (s == 10) res.CloserAppsA++;
						}
					}
					if (Debug && r == 0 && day < 12)
					{
						int mtA = a;
						Console.WriteLine($"day {day} sp{day % 5} A: " + string.Join(" ", Enumerable.Range(0, 11).Select(s2 => { int p2 = m.Decks[mtA].Pitchers[s2]; return $"{state[0][s2]:F0}/{m.Tac.Limit[p2]:F0}:{st.Pit(p2, PS.NP)}"; })) + $"  score A {Enumerable.Range(0, 1).Sum(_ => m.TeamBatters[mtA].Sum(i => st.Bat(i, BS.R)))}");
					}
					for (int k = 0; k < 2; k++)
					{
						int mt = k == 0 ? a : 1 - a;
						for (int s = 0; s < 11; s++)
						{
							int p = m.Decks[mt].Pitchers[s];
							double left = state[k][s] + m.Tac.Units(p, st.Pit(p, PS.NP));
							state[k][s] = Math.Max(0, left - CardGame.Rules.RecoverShare * m.Tac.Limit[p]);
						}
					}
				}
				lock (gate) total.Add(res);
			});
			return total;
		}

		private static void Tally(SeasonModel m, SimStats st, int a, MatchResult res)
		{
			int b = 1 - a;
			long Sum(int team, int stat) => m.TeamBatters[team].Sum(i => st.Bat(i, stat));
			long ra = Sum(a, BS.R), rb = Sum(b, BS.R);
			res.Games++;
			res.RunsA += ra;
			res.RunsB += rb;
			if (ra > rb) res.WinsA += 1;
			else if (ra == rb) { res.WinsA += 0.5; res.Ties++; }
			res.HitsA += Sum(a, BS.H); res.AbA += Sum(a, BS.AB); res.HitsB += Sum(b, BS.H); res.AbB += Sum(b, BS.AB);
			res.HrA += Sum(a, BS.HR); res.HrB += Sum(b, BS.HR); res.SoA += Sum(a, BS.SO); res.SoB += Sum(b, BS.SO);
			res.BbA += Sum(a, BS.BB); res.BbB += Sum(b, BS.BB);
			res.SbA += Sum(a, BS.SB); res.CsA += Sum(a, BS.CS); res.SbB += Sum(b, BS.SB); res.CsB += Sum(b, BS.CS);
			GameDeck d = m.Decks[a];
			for (int s = 0; s < 11; s++)
			{
				int p = d.Pitchers[s];
				if (p < 0) continue;
				res.PitchesA += st.Pit(p, PS.NP);
				if (st.Pit(p, PS.G) > 0) res.PitchersUsedA++;
				if (s < 5 && st.Pit(p, PS.GS) > 0)
				{
					res.StartsA++;
					res.OutsSpA += st.Pit(p, PS.Outs);
					res.PitchesSpA += st.Pit(p, PS.NP);
					if (m.Tac != null) res.SpLimitShareA += st.Pit(p, PS.NP) / m.Tac.Limit[p];
				}
			}
			res.PaB += Sum(b, BS.PA);
		}
	}
}
