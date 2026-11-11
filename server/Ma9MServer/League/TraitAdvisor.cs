using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 특성 자동 배분 (engine session, 2026-11-11; user 18:4x 11번: "특성 추천 배분도 진행합시다. 찍었을 때 가장 승률이 높게 나오는 쪽으로
	// 자동으로 찍어주는 기능을 만듭시다"): the 팀 특성 points that make the club's 1군 strongest against its own league, as
	// Ma9MSim.TeamValue counts it (no games played: quick, and with no luck in it).
	// - The club as league games build it (Teams: development, team colour, today's condition, lineup effects, the league
	//   reference) with its own traits left off; the other clubs with theirs, as one typical batter and pitcher.
	// - Each trait at each point count is valued alone; every allocation the trees allow (TeamPropertyHandlers.Validate: tiers
	//   open by 6·4·4·3 points in the tier below, a trait under an arrow needs the one above mastered, CP of the tier) is
	//   scored by those values, the best few of each split of the points between the batter and the pitcher tree are valued
	//   again as a whole (stat gains overlap at the 140 cap), and the best of them is the advice.
	// Packets and the screen are the systems session's; AdviseTraits is what they call. Dev: --traitadvice <team name> (a copy).
	public static partial class LeagueService
	{
		public sealed class TraitAdvice
		{
			public int Cp;
			public Dictionary<byte, byte> Points = new Dictionary<byte, byte>();   // the advice
			public Dictionary<byte, byte> Saved = new Dictionary<byte, byte>();    // the club's points now
			public double Runs, SavedRuns;                                         // runs a game over no traits
			public double Win => Runs * TeamValue.WinPerRun;                       // win share over no traits
			public double SavedWin => SavedRuns * TeamValue.WinPerRun;
			public List<(byte index, byte points, double runs)> Singles = new List<(byte, byte, double)>();
			public TeamValue.Parts AdviceParts, SavedParts;
		}

		public static TraitAdvice AdviseTraits(uint userCode)
		{
			Func<Dictionary<byte, byte>, TeamValue.Parts> Value = TraitValuer(userCode);
			if (Value == null) return null;
			TraitAdvice advice = new TraitAdvice { Cp = TeamPropertyHandlers.AvailableCp(userCode), Saved = TeamPropertyHandlers.Points(userCode) };
			return Advise(advice, Value);
		}

		// TeamValue of the club with a set of trait points, runs a game over none (the clubs as they are now).
		public static double TraitRuns(uint userCode, Dictionary<byte, byte> points)
		{
			Func<Dictionary<byte, byte>, TeamValue.Parts> Value = TraitValuer(userCode);
			return Value == null ? double.NaN : Value(points).Runs - Value(new Dictionary<byte, byte>()).Runs;
		}

		// The club as league games build it now, its traits off, against the league's typical batter and pitcher.
		private static Func<Dictionary<byte, byte>, TeamValue.Parts> TraitValuer(uint userCode)
		{
			LeagueState s = s_store?.Load(userCode);
			if (s == null || s.Clubs.Count == 0) return null;
			int me = s.MyIndex;
			int game = Math.Clamp(s.NextGame, 1, 144);
			var (teams, _, traits) = Teams(s, game, _ => game, bareClub: me);
			GameEnv env = s_data.Env;
			int other = Enumerable.Range(0, teams.Length).First(i => i != me);

			// The league's typical batter and pitcher as our club meets them (their traits on).
			List<BatterTalent> bats = new List<BatterTalent>();
			List<PitcherTalent> pits = new List<PitcherTalent>();
			for (int j = 0; j < teams.Length; j++)
			{
				if (j == me) continue;
				SeasonModel mj = CardGame.Build(teams[me], teams[j], env);
				TraitEffects.Apply(mj, 1, traits[j]);
				bats.Add(TeamValue.TypicalBatter(mj, 1));
				PitcherTalent p = TeamValue.TypicalPitcher(mj, 1);
				if (traits[j] != null) p.Xbh *= traits[j].PXBH;
				pits.Add(p);
			}
			BatterTalent oppBat = new BatterTalent
			{
				K = bats.Average(b => b.K), BB = bats.Average(b => b.BB), HBP = bats.Average(b => b.HBP), HR = bats.Average(b => b.HR), Babip = bats.Average(b => b.Babip),
				Double = bats.Average(b => b.Double), Triple = bats.Average(b => b.Triple), Steal = bats.Average(b => b.Steal), Speed = bats.Average(b => b.Speed)
			};
			PitcherTalent oppPit = new PitcherTalent
			{
				K = pits.Average(p => p.K), BB = pits.Average(p => p.BB), HBP = pits.Average(p => p.HBP), HR = pits.Average(p => p.HR), Babip = pits.Average(p => p.Babip), Xbh = pits.Average(p => p.Xbh)
			};

			TeamValue.Parts Value(Dictionary<byte, byte> points)
			{
				TraitEffects.Effect e = TraitEffects.FromPoints(points) ?? new TraitEffects.Effect();
				GameTeam t = CopyTeam(teams[me]);
				TraitEffects.Raise(t, e);
				SeasonModel m = CardGame.Build(t, teams[other], env);
				// The rates on the model; steals and the other side's extra-base hits go to TeamValue directly (Apply would
				// move the catchers' defence for the caught share, which TeamValue would count as fielding).
				TraitEffects.Effect rates = new TraitEffects.Effect { HR = e.HR, K = e.K, BB = e.BB, XBH = e.XBH, PK = e.PK, PBabip = e.PBabip, PHR = e.PHR, StartLimit = e.StartLimit };
				TraitEffects.Apply(m, 0, rates);
				BatterTalent ob = new BatterTalent
				{
					K = oppBat.K, BB = oppBat.BB, HBP = oppBat.HBP, HR = oppBat.HR, Babip = oppBat.Babip, Steal = oppBat.Steal, Speed = oppBat.Speed,
					Double = Math.Clamp(oppBat.Double * e.PXBH, 0.03, 0.5), Triple = Math.Clamp(oppBat.Triple * e.PXBH, 0.0005, 0.15)
				};
				return TeamValue.Of(m, 0, e.StealTry, e.StealCaught, e.PStealTry, e.PCaught, oppBat: ob, oppPit: oppPit);
			}
			return Value;
		}

		private static TraitAdvice Advise(TraitAdvice advice, Func<Dictionary<byte, byte>, TeamValue.Parts> Value)
		{
			double bare = Value(new Dictionary<byte, byte>()).Runs;
			// Each trait alone, at each point count.
			Dictionary<(byte, int), double> single = new Dictionary<(byte, int), double>();
			foreach (TeamPropertyHandlers.Trait t in TeamPropertyHandlers.Traits)
			{
				for (int p = 1; p <= t.Max; p++)
				{
					double v = Value(new Dictionary<byte, byte> { [t.Index] = (byte)p }).Runs - bare;
					single[(t.Index, p)] = v;
					advice.Singles.Add((t.Index, (byte)p, v));
				}
			}
			// Every allocation of each tree by the singles' sum, the best few for each point total.
			const int Keep = 6;
			List<(byte[] pts, double v)>[] Best(char type)
			{
				TeamPropertyHandlers.Trait[] tree = TeamPropertyHandlers.Traits.Where(t => t.Type == type).OrderBy(t => t.Level).ThenBy(t => t.Index).ToArray();
				List<(byte[] pts, double v)>[] best = Enumerable.Range(0, tree.Sum(t => t.Max) + 1).Select(_ => new List<(byte[], double)>()).ToArray();
				byte[] at = new byte[tree.Length];
				int[] tierSum = new int[7];
				void Walk(int k, int used, double v)
				{
					if (k == tree.Length)
					{
						List<(byte[] pts, double v)> b = best[used];
						if (b.Count < Keep || v > b[^1].v)
						{
							b.Add(((byte[])at.Clone(), v));
							b.Sort((x, y) => y.v.CompareTo(x.v));
							if (b.Count > Keep) b.RemoveAt(b.Count - 1);
						}
						return;
					}
					TeamPropertyHandlers.Trait t = tree[k];
					bool open = tierSum[t.Level - 1] >= TeamPropertyHandlers.Gate[t.Level] || t.Level == 1;
					int ess = t.Essential == 0 ? -1 : Array.FindIndex(tree, x => x.Index == t.Essential);
					bool allowed = open && (ess < 0 || at[ess] == tree[ess].Max);
					for (int p = 0; p <= (allowed ? t.Max : 0); p++)
					{
						at[k] = (byte)p;
						tierSum[t.Level] += p;
						Walk(k + 1, used + p, v + (p > 0 ? single[(t.Index, p)] : 0));
						tierSum[t.Level] -= p;
					}
					at[k] = 0;
				}
				Walk(0, 0, 0);
				return best;
			}
			TeamPropertyHandlers.Trait[] hTree = TeamPropertyHandlers.Traits.Where(t => t.Type == 'H').OrderBy(t => t.Level).ThenBy(t => t.Index).ToArray();
			TeamPropertyHandlers.Trait[] pTree = TeamPropertyHandlers.Traits.Where(t => t.Type == 'P').OrderBy(t => t.Level).ThenBy(t => t.Index).ToArray();
			List<(byte[] pts, double v)>[] hBest = Best('H'), pBest = Best('P');
			// Splits of the CP, the best combinations by the singles' sum, then each valued as a whole.
			List<(Dictionary<byte, byte> pts, double v)> combos = new List<(Dictionary<byte, byte>, double)>();
			for (int kh = 0; kh < hBest.Length && kh <= advice.Cp; kh++)
			{
				for (int kp = 0; kp < pBest.Length && kh + kp <= advice.Cp; kp++)
				{
					foreach ((byte[] h, double hv) in hBest[kh])
					{
						foreach ((byte[] p, double pv) in pBest[kp])
						{
							Dictionary<byte, byte> d = new Dictionary<byte, byte>();
							for (int i = 0; i < h.Length; i++) if (h[i] > 0) d[hTree[i].Index] = h[i];
							for (int i = 0; i < p.Length; i++) if (p[i] > 0) d[pTree[i].Index] = p[i];
							combos.Add((d, hv + pv));
						}
					}
				}
			}
			double bestRuns = double.MinValue;
			foreach (var (pts, _) in combos.OrderByDescending(c => c.v).Take(40))
			{
				if (TeamPropertyHandlers.Validate(pts, advice.Cp) != null) continue;
				TeamValue.Parts parts = Value(pts);
				if (parts.Runs - bare > bestRuns)
				{
					bestRuns = parts.Runs - bare;
					advice.Points = pts;
					advice.AdviceParts = parts;
				}
			}
			advice.Runs = bestRuns;
			advice.SavedParts = Value(advice.Saved);
			advice.SavedRuns = advice.SavedParts.Runs - bare;
			return advice;
		}

		// Dev: --traitbalance [pairs] (engine session, 2026-11-11; user 20:4x "특성 세기는 가 안으로 해줘"): each trait at its max on
		// average rosters (random regulars' cards, no development or team colour; the other side another such team), TeamValue's
		// win %p a point against its tier's target (the 10-18 design: batter .36 .42 .48 .55 1.10, pitcher ×1.1), and the share
		// of its per-point amounts that would meet it.
		public static int TraitBalanceTest(int pairs)
		{
			List<CardInfo> cards = s_data.Catalog.All.Where(c => !c.IsLegend && s_data.Card(c.Idx, 2)?.Row is { } r && r.Share >= 0.9).ToList();
			Random rnd = new Random(7);
			GameTeam Make()
			{
				GameTeam t = new GameTeam { Name = "avg" };
				List<CardInfo> bats = cards.Where(c => c.IsBatter).OrderBy(_ => rnd.Next()).ToList();
				HashSet<int> used = new HashSet<int>();
				for (int pos = 0; pos < 8; pos++)
				{
					CardInfo c = bats.First(b => !used.Contains(b.Sidx) && (int)b.MainPosition == pos + 1);
					used.Add(c.Sidx);
					t.Order[pos] = s_data.Card(c.Idx, 2);
					t.Position[pos] = pos;
				}
				CardInfo dh = bats.First(b => !used.Contains(b.Sidx));
				t.Order[8] = s_data.Card(dh.Idx, 2);
				t.Position[8] = 8;
				List<CardInfo> pits = cards.Where(c => !c.IsBatter).OrderBy(_ => rnd.Next()).ToList();
				List<CardInfo> sp = pits.Where(c => c.MainPosition == CardPos.SP).Take(5).ToList();
				List<CardInfo> rp = pits.Where(c => c.MainPosition == CardPos.RP).Take(4).ToList();
				CardInfo su = pits.First(c => c.MainPosition == CardPos.SU), cp = pits.First(c => c.MainPosition == CardPos.CP);
				for (int s = 0; s < 5; s++) t.Pitchers[s] = s_data.Card(sp[s].Idx, 2);
				for (int s = 0; s < 4; s++) t.Pitchers[5 + s] = s_data.Card(rp[s].Idx, 2);
				t.Pitchers[9] = s_data.Card(su.Idx, 2);
				t.Pitchers[10] = s_data.Card(cp.Idx, 2);
				return t;
			}
			GameEnv env = s_data.Env;
			double Runs(GameTeam home, GameTeam away, Dictionary<byte, byte> points)
			{
				TraitEffects.Effect e = TraitEffects.FromPoints(points) ?? new TraitEffects.Effect();
				GameTeam t = CopyTeam(home);
				TraitEffects.Raise(t, e);
				SeasonModel m = CardGame.Build(t, away, env);
				TraitEffects.Apply(m, 0, new TraitEffects.Effect { HR = e.HR, K = e.K, BB = e.BB, XBH = e.XBH, PK = e.PK, PBabip = e.PBabip, PHR = e.PHR, StartLimit = e.StartLimit });
				BatterTalent ob = TeamValue.TypicalBatter(m, 1);
				ob.Double = Math.Clamp(ob.Double * e.PXBH, 0.03, 0.5);
				ob.Triple = Math.Clamp(ob.Triple * e.PXBH, 0.0005, 0.15);
				return TeamValue.Of(m, 0, e.StealTry, e.StealCaught, e.PStealTry, e.PCaught, oppBat: ob).Runs;
			}
			double[] batTarget = { 0.36, 0.42, 0.48, 0.55, 1.10 };
			Dictionary<byte, double> sum = new Dictionary<byte, double>();
			for (int n = 0; n < pairs; n++)
			{
				GameTeam home = Make(), away = Make();
				double bare = Runs(home, away, new Dictionary<byte, byte>());
				foreach (TeamPropertyHandlers.Trait t in TeamPropertyHandlers.Traits)
				{
					sum[t.Index] = sum.GetValueOrDefault(t.Index) + Runs(home, away, new Dictionary<byte, byte> { [t.Index] = t.Max }) - bare;
				}
			}
			ServerLog.Info($"[traitbalance] {pairs} pairs of average rosters ({cards.Count} regular cards); win %p a point (TeamValue) against the tier target:");
			foreach (TeamPropertyHandlers.Trait t in TeamPropertyHandlers.Traits)
			{
				double perPoint = 100 * sum[t.Index] / pairs * TeamValue.WinPerRun / t.Max;
				double target = batTarget[t.Level - 1] * (t.Type == 'P' ? 1.1 : 1);
				ServerLog.Info($"[traitbalance]   {t.Index} {t.Level}단계 {t.Name,-12} {perPoint:F2}%p/pt  목표 {target:F2}  ×{target / Math.Max(1e-6, perPoint):F2}");
			}
			return 0;
		}

		// Dev: --traitbalancesim [pairs] [games]: the same in full games — each trait at its max on the home side of average
		// rosters against another, `games` home games a pair with the same seeds with and without it (starters rotating).
		public static int TraitBalanceSim(int pairs, int games, int threads = 4)
		{
			List<CardInfo> cards = s_data.Catalog.All.Where(c => !c.IsLegend && s_data.Card(c.Idx, 2)?.Row is { } r && r.Share >= 0.9).ToList();
			Random rnd = new Random(7);
			GameTeam Make()
			{
				GameTeam t = new GameTeam { Name = "avg" };
				List<CardInfo> bats = cards.Where(c => c.IsBatter).OrderBy(_ => rnd.Next()).ToList();
				HashSet<int> used = new HashSet<int>();
				for (int pos = 0; pos < 8; pos++)
				{
					CardInfo c = bats.First(b => !used.Contains(b.Sidx) && (int)b.MainPosition == pos + 1);
					used.Add(c.Sidx);
					t.Order[pos] = s_data.Card(c.Idx, 2);
					t.Position[pos] = pos;
				}
				t.Order[8] = s_data.Card(bats.First(b => !used.Contains(b.Sidx)).Idx, 2);
				t.Position[8] = 8;
				List<CardInfo> pits = cards.Where(c => !c.IsBatter).OrderBy(_ => rnd.Next()).ToList();
				List<CardInfo> sp = pits.Where(c => c.MainPosition == CardPos.SP).Take(5).ToList(), rp = pits.Where(c => c.MainPosition == CardPos.RP).Take(4).ToList();
				for (int s = 0; s < 5; s++) t.Pitchers[s] = s_data.Card(sp[s].Idx, 2);
				for (int s = 0; s < 4; s++) t.Pitchers[5 + s] = s_data.Card(rp[s].Idx, 2);
				t.Pitchers[9] = s_data.Card(pits.First(c => c.MainPosition == CardPos.SU).Idx, 2);
				t.Pitchers[10] = s_data.Card(pits.First(c => c.MainPosition == CardPos.CP).Idx, 2);
				return t;
			}
			(GameTeam home, GameTeam away)[] teams = Enumerable.Range(0, pairs).Select(_ => (Make(), Make())).ToArray();
			TeamPropertyHandlers.Trait[] traits = TeamPropertyHandlers.Traits;
			TraitEffects.Effect[] effects = traits.Select(t => TraitEffects.FromPoints(new Dictionary<byte, byte> { [t.Index] = t.Max })).ToArray();
			GameTeam[][] raised = teams.Select(p => effects.Select(e => { GameTeam c = CopyTeam(p.home); TraitEffects.Raise(c, e); return c; }).ToArray()).ToArray();
			GameEnv env = s_data.Env;
			double[] d = new double[traits.Length], d2 = new double[traits.Length];
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, pairs * games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, n =>
			{
				int p = n / games, g = n % games;
				double Play(GameTeam homeTeam, TraitEffects.Effect e)
				{
					GameTeam h = CopyTeam(homeTeam), a = CopyTeam(teams[p].away);
					h.StarterSlot = g % 5;
					a.StarterSlot = (g / 5) % 5;
					SeasonModel m = CardGame.Build(h, a, env);
					TraitEffects.Apply(m, 0, e);
					SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(n + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					long r0 = m.TeamBatters[0].Sum(i => st.Bat(i, BS.R)), r1 = m.TeamBatters[1].Sum(i => st.Bat(i, BS.R));
					return r0 > r1 ? 1 : r0 == r1 ? 0.5 : 0;
				}
				double b = Play(teams[p].home, null);
				double[] mine = new double[traits.Length];
				for (int k = 0; k < traits.Length; k++) mine[k] = Play(raised[p][k], effects[k]) - b;
				lock (gate) for (int k = 0; k < traits.Length; k++) { d[k] += mine[k]; d2[k] += mine[k] * mine[k]; }
			});
			double[] batTarget = { 0.36, 0.42, 0.48, 0.55, 1.10 };
			int total = pairs * games;
			ServerLog.Info($"[traitbalancesim] {pairs} pairs × {games} games of average rosters; win %p a point in full games against the tier target (MA9M_TRAIT_SCALE {Environment.GetEnvironmentVariable("MA9M_TRAIT_SCALE") ?? "-"}):");
			for (int k = 0; k < traits.Length; k++)
			{
				TeamPropertyHandlers.Trait t = traits[k];
				double mean = d[k] / total, se = Math.Sqrt(Math.Max(0, d2[k] / total - mean * mean) / total);
				double perPoint = 100 * mean / t.Max, target = batTarget[t.Level - 1] * (t.Type == 'P' ? 1.1 : 1);
				ServerLog.Info($"[traitbalancesim]   {t.Index} {t.Level}단계 {t.Name,-12} {perPoint:F2} ± {100 * se / t.Max:F2}%p/pt  목표 {target:F2}  ×{target / Math.Max(1e-6, perPoint):F2}");
			}
			return 0;
		}

		// A team to change without touching the one it came from (cards copied; rows are replaced, not changed, by Raise).
		private static GameTeam CopyTeam(GameTeam t)
		{
			GameCard Copy(GameCard c) => c == null ? null : new GameCard { Row = c.Row, Bats = c.Bats, Throws = c.Throws, LowSlot = c.LowSlot, Condition = c.Condition, Own = c.Own };
			GameTeam r = new GameTeam { Name = t.Name, StarterSlot = t.StarterSlot, Tactics = t.Tactics };
			Array.Copy(t.Position, r.Position, t.Position.Length);
			for (int i = 0; i < t.Order.Length; i++) r.Order[i] = Copy(t.Order[i]);
			for (int i = 0; i < t.Pitchers.Length; i++) r.Pitchers[i] = Copy(t.Pitchers[i]);
			return r;
		}

		// Dev check in full games: the club with each set of points against every other club of its league, `games` games each
		// at home with the same seeds for every set (so the differences are paired); win share per set.
		public static double[] TraitSimCheck(uint userCode, List<Dictionary<byte, byte>> sets, int games, int threads = 4)
		{
			LeagueState s = s_store.Load(userCode);
			int me = s.MyIndex;
			int game = Math.Clamp(s.NextGame, 1, 144);
			var (teams, _, traits) = Teams(s, game, _ => game, bareClub: me);
			GameEnv env = s_data.Env;
			int[] opps = Enumerable.Range(0, teams.Length).Where(j => j != me).ToArray();
			double[] wins = new double[sets.Count];
			for (int k = 0; k < sets.Count; k++)
			{
				TraitEffects.Effect e = TraitEffects.FromPoints(sets[k]);
				GameTeam mine = CopyTeam(teams[me]);
				TraitEffects.Raise(mine, e);
				double w = 0;
				object gate = new object();
				System.Threading.Tasks.Parallel.For(0, opps.Length * games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, n =>
				{
					int j = opps[n / games];
					// Both rotations turn over (the teams of Teams are one game's: the same two starters every game).
					GameTeam home = CopyTeam(mine), away = CopyTeam(teams[j]);
					home.StarterSlot = n % 5;
					away.StarterSlot = (n / 5) % 5;
					SeasonModel m = CardGame.Build(home, away, env);
					TraitEffects.Apply(m, 0, e);
					TraitEffects.Apply(m, 1, traits[j]);
					SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(n + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					long r0 = m.TeamBatters[0].Sum(i => st.Bat(i, BS.R)), r1 = m.TeamBatters[1].Sum(i => st.Bat(i, BS.R));
					double x = r0 > r1 ? 1 : r0 == r1 ? 0.5 : 0;
					lock (gate) w += x;
				});
				wins[k] = w / (opps.Length * games);
			}
			return wins;
		}

		// Dev: --traitadvice <team name> on a copy of the data (read-only, but the server starts as usual around it).
		// TRAITADVICE_SIM=<games a club>: the advice, the saved points and none checked in full games (TraitSimCheck);
		// TRAITADVICE_SETS="name=101:2,102:2;name=…" more sets to check.
		public static int TraitAdviceTest(string teamName, string dbPath)
		{
			uint user = 0;
			string cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
			{
				c.Open();
				using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code FROM team WHERE team_name = $n";
				cmd.Parameters.AddWithValue("$n", teamName);
				object o = cmd.ExecuteScalar();
				if (o != null) user = Convert.ToUInt32(o);
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			TraitAdvice a = user != 0 ? AdviseTraits(user) : null;
			if (a == null)
			{
				ServerLog.Warn($"[traitadvice] no league for '{teamName}'");
				return 2;
			}
			string Show(Dictionary<byte, byte> p) => string.Join(" ", p.Where(x => x.Value > 0).OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
			ServerLog.Info($"[traitadvice] {teamName} (user {user}), CP {a.Cp}, {watch.ElapsedMilliseconds} ms");
			foreach (var g in a.Singles.GroupBy(x => x.index))
			{
				TeamPropertyHandlers.Trait t = TeamPropertyHandlers.Traits.First(x => x.Index == g.Key);
				ServerLog.Info($"[traitadvice]   {t.Index} {t.Level}단계 {t.Name}: " + string.Join("  ", g.Select(x => $"{x.points}pt {x.runs:+0.000;-0.000}점 ({100 * x.runs * TeamValue.WinPerRun / x.points:+0.00;-0.00}%p/pt)")));
			}
			ServerLog.Info($"[traitadvice] saved  {Show(a.Saved)}: {a.SavedRuns:+0.000;-0.000} runs a game, {100 * a.SavedWin:+0.00;-0.00}%p | {a.SavedParts}");
			ServerLog.Info($"[traitadvice] advice {Show(a.Points)}: {a.Runs:+0.000;-0.000} runs a game, {100 * a.Win:+0.00;-0.00}%p | {a.AdviceParts}");
			if (int.TryParse(Environment.GetEnvironmentVariable("TRAITADVICE_SIM"), out int simGames) && simGames > 0)
			{
				List<(string name, Dictionary<byte, byte> pts)> sets = new List<(string, Dictionary<byte, byte>)>
				{
					("none", new Dictionary<byte, byte>()), ("saved", a.Saved), ("advice", a.Points)
				};
				foreach (string spec in (Environment.GetEnvironmentVariable("TRAITADVICE_SETS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
				{
					string[] kv = spec.Split('=', 2);
					sets.Add((kv[0], kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':')).ToDictionary(x => byte.Parse(x[0]), x => byte.Parse(x[1]))));
				}
				watch.Restart();
				double[] w = TraitSimCheck(user, sets.Select(x => x.pts).ToList(), simGames);
				for (int k = 0; k < sets.Count; k++)
				{
					string est = "";
					est = $", TeamValue {100 * TraitRuns(user, sets[k].pts) * TeamValue.WinPerRun:+0.00;-0.00}%p over none";
					ServerLog.Info($"[traitadvice] sim {sets[k].name,-8} {Show(sets[k].pts)}: win {100 * w[k]:F2}% ({100 * (w[k] - w[0]):+0.00;-0.00}%p over none){est}");
				}
				ServerLog.Info($"[traitadvice] sim: {simGames} games against each of the other clubs a set, {watch.Elapsed.TotalSeconds:F0} s");
			}
			return 0;
		}
	}
}
