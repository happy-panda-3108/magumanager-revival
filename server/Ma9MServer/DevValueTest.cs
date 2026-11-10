using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ma9MSim;

namespace Ma9MServer
{
	// "--devvalue": what development points are worth, stat by stat, under the current rules or a variant
	// (engine session, 2026-10-27; the user asked why 테크닉 all-in pays best and how development could spread).
	// Mirror matches of the fixed AI clubs (DV_TIERS, default 마이너 and 메이저 = 2,3): one side's whole staff or
	// lineup gets DV_AMOUNT more shown points in one stat, on top of a level both sides share (DV_LEVEL on every
	// stat, as team colour). Same seeds for every treatment and the untreated mirror, which is subtracted.
	// The live rules unless varied: DV_CURVE "100:0.75,120:0.5" (TeamStrength.DevCurve; "none" = flat), DV_KNEE the
	// in-play knee (stat value or "off"), DV_PITW / DV_BATW six weights in TeamCard order (TeamStrength.PitDevStat /
	// BatDevStat), DV_SCALE (TeamStrength.DevScale). The rules before 2026-10-27: DV_CURVE=none DV_KNEE=95
	// DV_PITW=1,1,1,1,1,1 DV_SCALE=1.
	// DV_TREAT: "p4,p3,b0,…" (p = staff, b = lineup, TeamCard stat order; "all" = every stat of every player).
	public static class DevValueTest
	{
		private sealed class Club
		{
			public string Name;
			public Lineup Lineup;
			public Dictionary<int, TeamCard> Development;
			public int Flat;
		}

		private static readonly string[] PitNames = { "체력", "구속", "컨트롤", "테크닉", "구위", "정신" };
		private static readonly string[] BatNames = { "컨택", "파워", "주력", "선구", "수비", "정신" };
		// TeamCard batter order → CardRow.R (TeamStrength.BatterSlot).
		private static readonly int[] BatterSlot = { 0, 1, 3, 2, 5, 4 };

		private sealed class Acc
		{
			public double Win, RunsFor, RunsAgainst, Games;
			public double[] Bat = new double[BS.Count], Opp = new double[BS.Count];

			public void Add(Acc o)
			{
				Win += o.Win; RunsFor += o.RunsFor; RunsAgainst += o.RunsAgainst; Games += o.Games;
				for (int i = 0; i < BS.Count; i++) { Bat[i] += o.Bat[i]; Opp[i] += o.Opp[i]; }
			}
		}

		public static int Run(GameData data, string dbPath)
		{
			int Env(string key, int dflt) => int.TryParse(Environment.GetEnvironmentVariable(key), out int v) ? v : dflt;
			string Str(string key, string dflt) => Environment.GetEnvironmentVariable(key) is string s && s.Length > 0 ? s : dflt;
			int games = Env("DV_GAMES", 20000), amount = Env("DV_AMOUNT", 10), level = Env("DV_LEVEL", 0), salt = Env("DV_SALT", 0);
			ApplyVariant(Str("DV_CURVE", ""), Str("DV_KNEE", ""), Str("DV_PITW", ""), Str("DV_BATW", ""));
			if (Str("DV_SCALE", "") is string scale && scale.Length > 0) TeamStrength.DevScale = double.Parse(scale);
			AiTeams.Init(dbPath, data.Catalog);
			List<Club> clubs = new List<Club>();
			foreach (int tier in Str("DV_TIERS", "2,3").Split(',').Select(int.Parse))
			{
				foreach (AiTeam a in AiTeams.OfTier(tier))
				{
					Lineup stored = Lineup.Parse(a.Lineup);
					Lineup arranged = LineupBuilder.Build(stored.Batters.Concat(stored.Pitchers).Where(i => i != 0), data.Catalog);
					clubs.Add(new Club { Name = a.Name, Lineup = arranged, Development = a.Development, Flat = a.Development.Count > 0 ? 0 : LeagueRules.AiBonus[tier] });
				}
			}
			string[] treats = Str("DV_TREAT", "p1,p2,p3,p4,p0,p5,b0,b1,b3,b2,b4,b5").Split(',');
			ServerLog.Info($"devvalue: {clubs.Count} clubs, {games} games a treatment, +{amount} on top of level +{level}; curve [{string.Join(" ", TeamStrength.DevCurve.Select(c => $"{c.from:F0}→{c.mult:F2}"))}], "
				+ $"knee x {CardRules.KneeX:F1}, scale {TeamStrength.DevScale:F2}, pitcher weights [{string.Join(" ", TeamStrength.PitDevStat.Select(w => w.ToString("F2")))}], batter [{string.Join(" ", TeamStrength.BatDevStat.Select(w => w.ToString("F2")))}]");
			Acc baseline = Play(data, clubs, games, salt, level, null, 0);
			Report("기준(대칭)", baseline, baseline, false);
			{
				double[] s = baseline.Bat.Zip(baseline.Opp, (x, y) => x + y).ToArray();
				ServerLog.Info($"devvalue: 기준 환경 득점 {(baseline.RunsFor + baseline.RunsAgainst) / (2 * baseline.Games):F2}/경기, 타율 {s[BS.H] / s[BS.AB]:F3}, 홈런 {s[BS.HR] / (2 * baseline.Games):F2}/경기");
			}
			foreach (string t in treats)
			{
				Acc r = Play(data, clubs, games, salt, level, t, amount);
				bool pitch = t.StartsWith("p");
				string Part(string s)
				{
					string[] kv = s[1..].Split(':');
					return $"{(s[0] == 'p' ? PitNames : BatNames)[int.Parse(kv[0])]} +{(kv.Length > 1 ? kv[1] : amount.ToString())}";
				}
				string label = t == "all" ? $"전원 전 능력치 +{amount}" : (pitch ? "투수진 " : "타선 ") + string.Join(" ", t.Split('+').Select(Part));
				Report(label, r, baseline, pitch);
			}
			return 0;
		}

		private static void ApplyVariant(string curve, string knee, string pitw, string batw)
		{
			if (curve.Length > 0)
			{
				TeamStrength.DevCurve = curve == "none" ? Array.Empty<(double, double)>()
					: curve.Split(',').Select(s => s.Split(':')).Select(p => (double.Parse(p[0]), double.Parse(p[1]))).OrderBy(p => p.Item1).ToArray();
			}
			if (knee.Length > 0) CardRules.KneeX = knee == "off" ? 1e9 : (double.Parse(knee) - 65) / 10;
			if (pitw.Length > 0) TeamStrength.PitDevStat = pitw.Split(',').Select(double.Parse).ToArray();
			if (batw.Length > 0)
			{
				// Given in TeamCard order; the weights are kept in CardRow.R order.
				double[] w = batw.Split(',').Select(double.Parse).ToArray();
				double[] r = new double[6];
				for (int k = 0; k < 6; k++) r[BatterSlot[k]] = w[k];
				TeamStrength.BatDevStat = r;
			}
		}

		private static void Report(string label, Acc r, Acc b, bool pitch)
		{
			double n = r.Games;
			// The treated side's batting (lineup treatments) or what its staff allowed (staff treatments), against the
			// untreated side's in the same games (the league reference moves both sides, so the baseline run is no guide).
			double[] x = pitch ? r.Opp : r.Bat, y = pitch ? r.Bat : r.Opp;
			string Rates(double[] s)
			{
				double pa = s[BS.PA], bip = s[BS.AB] - s[BS.SO] - s[BS.HR] + s[BS.SF];
				return $"K {100 * s[BS.SO] / pa:F2} BB {100 * s[BS.BB] / pa:F2} HR {100 * s[BS.HR] / pa:F2} BABIP {(s[BS.H] - s[BS.HR]) / bip:F4} SB {s[BS.SB] / n:F2}";
			}
			double win = 100 * (r.Win - b.Win) / n, diff = (r.RunsFor - r.RunsAgainst - b.RunsFor + b.RunsAgainst) / n;
			ServerLog.Info($"devvalue: {label,-16} 승률 {win:+0.00;-0.00}%p, 득실 {diff:+0.000;-0.000}/경기 (득 {(r.RunsFor - b.RunsFor) / n:+0.000;-0.000}, 실 {(r.RunsAgainst - b.RunsAgainst) / n:+0.000;-0.000}) | {Rates(x)} | 상대편 {Rates(y)}");
		}

		private static TeamCard DevOf(Club c, int idx, int level, string treat, int amount, bool pitcher)
		{
			TeamCard d = new TeamCard { Idx = idx };
			if (c.Development.TryGetValue(idx, out TeamCard own))
			{
				for (int k = 0; k < 6; k++) { d.Contract[k] = own.Contract[k]; d.Training[k] = own.Training[k]; d.Educated[k] = own.Educated[k]; }
			}
			for (int k = 0; k < 6; k++)
			{
				d.Training[k] += (sbyte)c.Flat;
				d.TeamColor[k] = (sbyte)level;
			}
			if (treat == "all")
			{
				for (int k = 0; k < 6; k++) d.TeamColor[k] += (sbyte)amount;
			}
			else if (treat != null)
			{
				// "p4" (+amount) or combinations "p4:15+p3:15".
				foreach (string part in treat.Split('+'))
				{
					if ((part[0] == 'p') != pitcher) continue;
					string[] kv = part[1..].Split(':');
					d.TeamColor[int.Parse(kv[0])] += (sbyte)(kv.Length > 1 ? int.Parse(kv[1]) : amount);
				}
			}
			return d;
		}

		private static GameTeam Team(GameData data, Club c, int game, int level, string treat, int amount)
		{
			Lineup l = c.Lineup;
			GameTeam t = new GameTeam { Name = c.Name };
			for (int pos = 0; pos < Lineup.FielderSlots; pos++) t.Position[l.Fielders[pos] - 1] = pos;
			for (int slot = 0; slot < 9; slot++) t.Order[slot] = data.Card(l.Batters[slot], 2);
			for (int s = 0; s < Lineup.PitcherSlots; s++) t.Pitchers[s] = l.Pitchers[s] != 0 ? data.Card(l.Pitchers[s], 2) : null;
			t.StarterSlot = LeagueService.StarterSlot(l, game);
			LineupEffects.Apply(t, l, data.Catalog, null);
			for (int slot = 0; slot < 9; slot++) TeamStrength.AddBonuses(t.Order[slot], DevOf(c, l.Batters[slot], level, treat, amount, false));
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				if (t.Pitchers[s] != null) TeamStrength.AddBonuses(t.Pitchers[s], DevOf(c, l.Pitchers[s], level, treat, amount, true));
			}
			return t;
		}

		private static Acc Play(GameData data, List<Club> clubs, int games, int salt, int level, string treat, int amount)
		{
			Acc total = new Acc();
			object gate = new object();
			Parallel.For(0, games, () => new Acc(), (g, _, acc) =>
			{
				Club c = clubs[g % clubs.Count];
				int game = g / clubs.Count + 1;
				GameTeam ta = Team(data, c, game, level, treat, amount), tb = Team(data, c, game, level, null, 0);
				TeamStrength.ApplyLeagueReference(new[] { ta, tb });
				bool aHome = (g / clubs.Count) % 2 == 0;
				SeasonModel m = aHome ? CardGame.Build(ta, tb, data.Env) : CardGame.Build(tb, ta, data.Env);
				SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = data.Env.RunEnvironment, ReachOnError = data.Env.ReachOnError, MaxInnings = LeagueRules.MaxInnings },
					(ulong)(g * 7919 + 13 + salt)).PlayOne(0, 1, null);
				int a = aHome ? 0 : 1, b = 1 - a;
				long x = m.TeamBatters[a].Sum(i => st.Bat(i, BS.R)), y = m.TeamBatters[b].Sum(i => st.Bat(i, BS.R));
				acc.Games++;
				acc.Win += x > y ? 1 : x == y ? 0.5 : 0;
				acc.RunsFor += x;
				acc.RunsAgainst += y;
				for (int k = 0; k < BS.Count; k++)
				{
					acc.Bat[k] += m.TeamBatters[a].Sum(i => st.Bat(i, k));
					acc.Opp[k] += m.TeamBatters[b].Sum(i => st.Bat(i, k));
				}
				return acc;
			}, acc =>
			{
				lock (gate) total.Add(acc);
			});
			return total;
		}
	}
}
