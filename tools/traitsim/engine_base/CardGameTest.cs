using System;
using System.Collections.Generic;
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
			public bool Print;
			private readonly Func<int, string> m_name;

			public Checker(Func<int, string> name)
			{
				m_name = name;
			}

			public void HalfInningStart(int inning, bool bottom)
			{
				Outs = 0;
				if (Print) Lines.Add($"-- {inning}회{(bottom ? "말" : "초")}");
			}

			public void HalfInningEnd(int inning, bool bottom)
			{
				if (Outs != 3 && Print) Lines.Add($"   (half ends with {Outs} outs)");
			}

			public void PitcherChange(int team, int pitcher)
			{
				if (Print) Lines.Add($"   투수 교체: {pitcher}");
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

		public static void Run(string dir, int games, Action<string> log)
		{
			GameEnv env = CardGame.LoadEnv(dir);
			Dictionary<(char, int, int), CardRow> cards = CardGame.LoadCards(dir);
			List<CardRow> regulars = cards.Values.Where(c => c.Share >= 0.9).ToList();
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
					t.Order[pos] = new GameCard { Row = c };
					t.Position[pos] = pos;
				}
				CardRow dh = pool.First(b => !used.Contains(b));
				t.Order[8] = new GameCard { Row = dh };
				t.Position[8] = 8;
				List<CardRow> sp = regulars.Where(c => c.Kind == 'P' && c.Role == "SP").OrderBy(_ => rnd.Next()).Take(5).ToList();
				List<CardRow> rp = regulars.Where(c => c.Kind == 'P' && c.Role != "SP").OrderBy(_ => rnd.Next()).Take(6).ToList();
				for (int s = 0; s < 5; s++) t.Pitchers[s] = new GameCard { Row = sp[s] };
				for (int s = 0; s < 6; s++) t.Pitchers[5 + s] = new GameCard { Row = rp[s] };
				t.StarterSlot = rnd.Next(5);
				return t;
			}
			long runs = 0, plays = 0, problems = 0, halfBad = 0;
			double hits = 0, ab = 0;
			Checker shown = null;
			for (int g = 0; g < games; g++)
			{
				GameTeam home = Make(), away = Make();
				SeasonModel model = CardGame.Build(home, away, env);
				Checker chk = new Checker(i => model.Batters[i].Name) { Print = g == 0 };
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
				}
				if (g == 0) shown = chk;
			}
			foreach (string line in shown.Lines.Take(60)) log(line);
			log($"{games} games: R/G {runs / (2.0 * games):F2}, AVG {hits / ab:F3}, plays/game {plays / (double)games:F1}, inconsistent plays {problems}");
		}
	}
}
