using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// "--selftest": plays games between random clubs and replays every broadcast buffer the way
	// the client's DataCenter does (outs, half-innings, score), checking that it adds up.
	public static class BroadcastSelfTest
	{
		// The condition chain over a year: its mix and how often it holds or moves.
		public static void ConditionStats()
		{
			int[] count = new int[5];
			int same = 0, one = 0, more = 0, days = 0;
			for (int card = 1; card <= 300; card++)
			{
				int prev = -1;
				for (DateTime d = new DateTime(2026, 1, 2); d < new DateTime(2027, 1, 2); d = d.AddDays(1))
				{
					int c = Condition.For(7, card, d);
					count[c]++;
					if (prev >= 0)
					{
						days++;
						int gap = Math.Abs(c - prev);
						if (gap == 0) same++; else if (gap == 1) one++; else more++;
					}
					prev = c;
				}
			}
			int total = count.Sum();
			ServerLog.Info("condition mix " + string.Join(" / ", count.Select(c => $"{c * 100.0 / total:F1}%")) + $" | next day: same {same * 100.0 / days:F0}%, one step {one * 100.0 / days:F0}%, further {more * 100.0 / days:F0}%");
		}

		public static int Run(GameData data, int games)
		{
			ConditionStats();
			// MA9M_SELFTEST_GAMES: more games for a longer run (the 끝장승부's long games are rare).
			if (int.TryParse(Environment.GetEnvironmentVariable("MA9M_SELFTEST_GAMES"), out int more) && more > 0) games = more;
			Random rnd = new Random(1);
			int failures = 0, scenesMax = 0, inningsMax = 0, longScenes = 0, buffMax = 0;
			long buffSum = 0;
			Ma9MSim.Fielding.ScriptCheck.Report check = new Ma9MSim.Fielding.ScriptCheck.Report();
			Ma9MSim.Fielding.Defence refDefence = Ma9MSim.Fielding.Defence.Average();
			double runs = 0, hits = 0, ab = 0, hr = 0, starterOuts = 0, er = 0, outsAll = 0;
			Dictionary<short, BallPreset> presets = data.Presets.ForGround(1).ToDictionary(p => p.Code);
			for (int g = 0; g < games; g++)
			{
				GameTeam home = RandomTeam(data, rnd), away = RandomTeam(data, rnd);
				SeasonModel model = CardGame.Build(home, away, data.Env);
				BroadcastRecorder rec = new BroadcastRecorder(data.Presets, g);
				rec.Attach(model, home, away);
				// Played as the postseason's 끝장승부 (LeagueRules.EndlessInnings), so long games' broadcasts get replayed too.
				SimStats st = new SeasonSimulator(model, new EngineParams { RunEnvironment = data.Env.RunEnvironment, ReachOnError = data.Env.ReachOnError, MaxInnings = LeagueRules.EndlessInnings }, (ulong)g + 1).PlayOne(0, 1, rec);
				byte[] buff = rec.Finish();
				int innings = (int)(model.TeamPitchers[0].Sum(p => st.Pit(p, PS.Outs)) + 2) / 3;
				if (innings > inningsMax) { inningsMax = innings; longScenes = buff[0]; }
				int homeRuns = model.TeamBatters[0].Sum(i => (int)st.Bat(i, BS.R)), awayRuns = model.TeamBatters[1].Sum(i => (int)st.Bat(i, BS.R));
				for (int i = 0; i < model.Batters.Length; i++) { hits += st.Bat(i, BS.H); ab += st.Bat(i, BS.AB); hr += st.Bat(i, BS.HR); }
				for (int t = 0; t < 2; t++) starterOuts += st.Pit(model.Decks[t].Pitchers[model.Decks[t].StarterSlot], PS.Outs);
				for (int i = 0; i < model.Pitchers.Length; i++) { er += st.Pit(i, PS.ER); outsAll += st.Pit(i, PS.Outs); }
				string problem = Replay(buff, presets, out int h, out int a, out int scenes);
				if (Ma9MSim.Fielding.FieldEngine.On)
				{
					// The scripts behind the records: unpack, decode and check each one as the client would.
					List<byte[]> scripts = BroadcastRecorder.ReadScripts(buff);
					buffMax = Math.Max(buffMax, buff.Length);
					buffSum += buff.Length;
					if (scripts == null) { problem ??= "no scripts block"; }
					else
					{
						foreach (byte[] sc in scripts)
						{
							if (sc == null) continue;
							Ma9MSim.Fielding.PlayScript ps = Ma9MSim.Fielding.PlayScript.Decode(sc);
							if (ps == null) { problem ??= "script did not decode"; continue; }
							Ma9MSim.Fielding.ScriptCheck.Check(ps, refDefence, check, $"g{g}");
						}
					}
				}
				scenesMax = Math.Max(scenesMax, scenes);
				runs += h + a;
				if (problem == null && (h != homeRuns || a != awayRuns))
				{
					problem = $"score {a}:{h} replayed, engine {awayRuns}:{homeRuns}";
				}
				if (problem != null)
				{
					failures++;
					if (failures <= 5) ServerLog.Warn($"selftest game {g}: {problem}");
				}
			}
			ServerLog.Info($"selftest: {games} games, {failures} failures, R/G {runs / (2.0 * games):F2}, AVG {hits / ab:F3}, HR/G {hr / (2.0 * games):F2}, ERA {er * 27 / outsAll:F2}, starter IP {starterOuts / (6.0 * games):F1}, most scenes {scenesMax}, longest game {inningsMax} innings ({longScenes} scenes)");
			if (Ma9MSim.Fielding.FieldEngine.On)
			{
				ServerLog.Info($"selftest scripts: buff {buffSum / Math.Max(1, games)} B a game (max {buffMax}), {check.Plays} plays checked");
				foreach (var kv in check.Count.OrderByDescending(x => x.Value))
				{
					ServerLog.Info($"  {kv.Key}: {kv.Value} ({100.0 * kv.Value / Math.Max(1, check.Plays):F2}%) e.g. {check.Examples[kv.Key][0]}");
				}
			}
			return failures == 0 ? 0 : 1;
		}

		// "--traittest": mirror matches (the same random club on both sides, home and away swapped
		// every other game) with a 팀 특성 build on one side only, to check that the effects reach the
		// engine and move results as the systems session's balance runs measured (tools/traitsim).
		public static int TraitTest(GameData data, int games)
		{
			(string name, Dictionary<byte, byte> pts)[] builds =
			{
				("없음 (기준)", new Dictionary<byte, byte>()),
				("메이저 투타 10+10", P(101, 2, 102, 2, 104, 2, 105, 2, 106, 2, 121, 2, 122, 2, 124, 2, 125, 2, 126, 2)),
				("메이저 타자 올인", P(101, 2, 102, 2, 104, 2, 103, 1, 105, 2, 106, 2, 112, 2, 110, 2, 114, 2, 113, 1, 118, 1, 120, 1)),
				("메이저 투수 올인", P(121, 2, 122, 2, 124, 2, 123, 1, 125, 2, 126, 2, 131, 2, 132, 2, 133, 1, 134, 2, 138, 1, 140, 1)),
				("비기너 타자 올인", P(101, 2, 102, 2, 104, 2, 105, 2, 106, 2, 112, 1)),
			};
			foreach ((string name, Dictionary<byte, byte> pts) in builds)
			{
				TraitEffects.Effect e = TraitEffects.FromPoints(pts);
				int wins = 0, ties = 0;
				double runsFor = 0, runsAgainst = 0;
				for (int g = 0; g < games; g++)
				{
					GameTeam a = RandomTeam(data, new Random(g)), b = RandomTeam(data, new Random(g));
					TraitEffects.Raise(a, e);
					bool aHome = g % 2 == 0;
					SeasonModel model = aHome ? CardGame.Build(a, b, data.Env) : CardGame.Build(b, a, data.Env);
					TraitEffects.Apply(model, aHome ? 0 : 1, e);
					SimStats st = new SeasonSimulator(model, new EngineParams { RunEnvironment = data.Env.RunEnvironment, ReachOnError = data.Env.ReachOnError }, (ulong)g + 7).PlayOne(0, 1, null);
					int ra = model.TeamBatters[aHome ? 0 : 1].Sum(i => (int)st.Bat(i, BS.R));
					int rb = model.TeamBatters[aHome ? 1 : 0].Sum(i => (int)st.Bat(i, BS.R));
					runsFor += ra;
					runsAgainst += rb;
					if (ra > rb) wins++; else if (ra == rb) ties++;
				}
				ServerLog.Info($"traittest {name}: win {100.0 * (wins + 0.5 * ties) / games:F1}% over {games} games, R/G {runsFor / games:F2} vs {runsAgainst / games:F2}");
			}
			return 0;
		}

		private static Dictionary<byte, byte> P(params int[] pairs)
		{
			Dictionary<byte, byte> d = new Dictionary<byte, byte>();
			for (int i = 0; i + 1 < pairs.Length; i += 2)
			{
				d[(byte)pairs[i]] = (byte)pairs[i + 1];
			}
			return d;
		}

		private static GameTeam RandomTeam(GameData data, Random rnd)
		{
			// SELFTEST_MODE=regulars: full-season cards only (the simulator's own check uses these).
			bool regulars = Environment.GetEnvironmentVariable("SELFTEST_MODE") == "regulars";
			bool normal = Environment.GetEnvironmentVariable("SELFTEST_CONDITION") == "normal";
			List<CardInfo> cards = data.Catalog.All.Where(c => !regulars || data.Card(c.Idx, 2).Row.Share >= 0.9).ToList();
			List<int> picks = new List<int>();
			foreach (CardPos pos in new[] { CardPos.C, CardPos.B1, CardPos.B2, CardPos.B3, CardPos.SS, CardPos.LF, CardPos.CF, CardPos.RF })
			{
				List<CardInfo> pool = cards.Where(c => c.IsBatter && c.MainPosition == pos && (regulars || c.Ball >= 4)).ToList();
				picks.Add(pool[rnd.Next(pool.Count)].Idx);
			}
			picks.AddRange(cards.Where(c => c.IsBatter && (regulars || c.Ball >= 4)).OrderBy(_ => rnd.Next()).Take(6).Select(c => c.Idx));
			foreach ((CardPos pos, int n) in new[] { (CardPos.SP, 5), (CardPos.RP, 4), (CardPos.SU, 1), (CardPos.CP, 1) })
			{
				picks.AddRange(cards.Where(c => !c.IsBatter && c.MainPosition == pos && (regulars || c.Ball >= 4)).OrderBy(_ => rnd.Next()).Take(n).Select(c => c.Idx));
			}
			Lineup l = LineupBuilder.Build(picks.Distinct(), data.Catalog);
			GameTeam t = new GameTeam { Name = "test" };
			for (int pos = 0; pos < 9; pos++) t.Position[l.Fielders[pos] - 1] = pos;
			for (int s = 0; s < 9; s++) t.Order[s] = data.Card(l.Batters[s], normal ? (byte)2 : (byte)rnd.Next(5));
			for (int s = 0; s < 11; s++) t.Pitchers[s] = l.Pitchers[s] != 0 ? data.Card(l.Pitchers[s], normal ? (byte)2 : (byte)rnd.Next(5)) : null;
			t.StarterSlot = rnd.Next(5);
			return t;
		}

		// DataCenter.OnEndScene_UpdateData, reduced to outs, half-innings and runs.
		private static string Replay(byte[] buff, Dictionary<short, BallPreset> presets, out int homeRuns, out int awayRuns, out int count)
		{
			homeRuns = awayRuns = 0;
			int ptr = 0;
			count = buff[ptr++];
			bool homeBats = false;
			int outs = 0;
			for (int n = 0; n < count; n++)
			{
				byte sp = buff[ptr++];
				if (sp != 0)
				{
					byte arg = buff[ptr++];
					if (sp == Sit.SpChange)
					{
						if (outs != 3) return $"scene {n}: change with {outs} outs";
						outs = 0;
						homeBats = !homeBats;
					}
					else if (sp == Sit.SpChangePitcher && arg > 10)
					{
						return $"scene {n}: pitcher slot {arg}";
					}
					else if (sp == Sit.SpGameEnd && n != count - 1)
					{
						return $"scene {n}: game end before the last scene";
					}
					continue;
				}
				byte sit = buff[ptr];
				byte np = buff[ptr + 2];
				short code = BitConverter.ToInt16(buff, ptr + 5);
				byte[] order = buff.Skip(ptr + 7).Take(4).ToArray();
				byte[] next = buff.Skip(ptr + 11).Take(4).ToArray();
				ptr += 16;
				if (sit is Sit.GroundOut or Sit.FlyOut or Sit.LineDrive or Sit.Single or Sit.Double or Sit.Triple or Sit.HomeRun or Sit.DoublePlay or Sit.Bunt or Sit.Error)
				{
					if (!presets.TryGetValue(code, out BallPreset p)) return $"scene {n}: unknown ball code {code}";
					if ((int)p.Chase > 8) return $"scene {n}: chase position {p.Chase}";
				}
				if ((sit is Sit.LookingSO or Sit.SwingSO) && np < 3) return $"scene {n}: strikeout in {np} pitches";
				if (sit == Sit.FourBall && np < 4) return $"scene {n}: walk in {np} pitches";
				if (order[0] < 1 || order[0] > 9) return $"scene {n}: batter order {order[0]}";
				switch (sit)
				{
					case Sit.LookingSO:
					case Sit.SwingSO:
						outs++;
						break;
					case Sit.FlyOut:
					case Sit.LineDrive:
					case Sit.DoublePlay:
					case Sit.Bunt:
						outs++;
						for (int k = 1; k < 4; k++) if (next[k] >= 5 && next[k] <= 8 && order[k] != 0) outs++;
						break;
					case Sit.Steal:
						for (int k = 1; k < 4; k++) if (next[k] >= 5 && next[k] <= 8 && order[k] != 0) outs++;
						break;
					case Sit.FourBall:
					case Sit.DeadBall:
						break;
					default:
						for (int k = 0; k < 4; k++) if (next[k] >= 5 && next[k] <= 8 && order[k] != 0) outs++;
						break;
				}
				int scored = Enumerable.Range(0, 4).Count(k => next[k] == 4 && order[k] != 0);
				if (homeBats) homeRuns += scored; else awayRuns += scored;
				if (outs > 3) return $"scene {n}: {outs} outs";
			}
			return null;
		}
	}
}
