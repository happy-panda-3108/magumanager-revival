using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ma9MSim
{
	// One card as the game server fields it.
	public sealed class GameCard
	{
		public CardRow Row;           // ratings (as shown on the card), pitches, defence, hidden profile
		public Hand Bats = Hand.R;
		public Hand Throws = Hand.R;
		public bool LowSlot;          // sidearm / submarine
		public double Condition;      // today's condition: -0.10, -0.05, 0, +0.05, +0.10
		public GameTactics Own;       // 개인작전 (null = 팀 작전에 따름)
	}

	public sealed class GameTeam
	{
		public string Name = string.Empty;
		public GameCard[] Order = new GameCard[9];     // batting order
		public int[] Position = new int[9];            // per batting slot: 0-7 = C..RF, 8 = DH
		public GameCard[] Pitchers = new GameCard[11]; // SP1-5, RP1-4, SU, CP (null = empty)
		public int StarterSlot;
		public GameTactics Tactics = new GameTactics();  // 작전실
	}

	// The reference world the cards were scaled to, and how the engine plays in it.
	public sealed class GameEnv
	{
		public CardRules Rules;
		public League League;
		public double RunEnvironment = 1.0, ReachOnError = 0.03;
		public MatchupParams MP = new MatchupParams();
		public FieldRules Field = new FieldRules();
	}

	// Server games with cards: builds a two-team SeasonModel from the managers' decks and
	// plays one game through the season engine, every play reported to a recorder.
	public static class CardGame
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		private static readonly EraWorld.Factors Reference = new EraWorld.Factors
		{
			BK = 1, BBB = 1, BHR = 1, BBabip = 1, BSteal = 1, PK = 1, PBB = 1, PHR = 1, PBabip = 1
		};

		// ---- files written by the card test, read by the server ----

		public static void WriteEnv(string path, CardRules k, List<SeasonFit> fits, EraWorld world)
		{
			double Geo(double a, double b) => Math.Sqrt(a * b);
			Dictionary<string, double> v = new Dictionary<string, double>
			{
				["K"] = Geo(k.BK.Base, k.PK.Base),
				["BB"] = Geo(k.BBB.Base, k.PBB.Base),
				["HBP"] = fits.Average(f => f.Model.League.HBP),
				["HR"] = Geo(k.BHR.Base, k.PHR.Base),
				["Babip"] = Geo(k.BBabip.Base, k.PBabip.Base),
				["Double"] = k.BDouble.Base,
				["Triple"] = k.BTriple.Base,
				["Steal"] = fits.Average(f => f.Model.League.Steal * world.Seasons[f.Model.Season.Year].BSteal),
				["Sac"] = fits.Average(f => f.Model.League.Sac),
				["RunEnvironment"] = fits.Average(f => f.Params.RunEnvironment),
				["ReachOnError"] = fits.Average(f => f.Params.ReachOnError)
			};
			File.WriteAllText(path, "{\n" + string.Join(",\n", v.Select(kv => $"  \"{kv.Key}\": {kv.Value.ToString("R", Inv)}")) + "\n}\n", new UTF8Encoding(false));
		}

		public static GameEnv LoadEnv(string dir)
		{
			GameEnv env = new GameEnv { Rules = LoadRules(Path.Combine(dir, "card_rules_clean.json")) };
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "game_env.json")));
			double D(string key) => doc.RootElement.GetProperty(key).GetDouble();
			env.League = new League
			{
				K = D("K"), BB = D("BB"), HBP = D("HBP"), HR = D("HR"), Babip = D("Babip"), Double = D("Double"), Triple = D("Triple"), Steal = D("Steal"), Sac = D("Sac")
			};
			env.RunEnvironment = D("RunEnvironment");
			env.ReachOnError = D("ReachOnError");
			return env;
		}

		// card_rules_clean.json (CardTest.WriteRules): every component linear in its stats.
		public static CardRules LoadRules(string path)
		{
			CardRules k = new CardRules();
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
			void Read(CardRules.Comp c, string key, string[] names)
			{
				JsonElement e = doc.RootElement.GetProperty(key);
				List<int> stats = new List<int>();
				List<double> beta = new List<double>(), gamma = new List<double>();
				foreach (JsonProperty p in e.EnumerateObject())
				{
					if (p.Name == "base")
					{
						c.Base = p.Value.GetDouble();
						continue;
					}
					stats.Add(Array.IndexOf(names, p.Name));
					beta.Add(p.Value[0].GetDouble());
					gamma.Add(p.Value[1].GetDouble());
				}
				c.Stats = stats.ToArray();
				c.StatNames = stats.Select(i => names[i]).ToArray();
				c.Beta = beta.ToArray();
				c.Gamma = gamma.ToArray();
				c.Linear = true;
			}
			Read(k.BK, "batter_K", CardRules.BatterStats);
			Read(k.BBB, "batter_BB", CardRules.BatterStats);
			Read(k.BHR, "batter_HR", CardRules.BatterStats);
			Read(k.BBabip, "batter_BABIP", CardRules.BatterStats);
			Read(k.BDouble, "batter_2B", CardRules.BatterStats);
			Read(k.BTriple, "batter_3B", CardRules.BatterStats);
			Read(k.BSteal, "batter_SB", CardRules.BatterStats);
			Read(k.PK, "pitcher_K", CardRules.PitcherStats);
			Read(k.PBB, "pitcher_BB", CardRules.PitcherStats);
			Read(k.PHR, "pitcher_HR", CardRules.PitcherStats);
			Read(k.PBabip, "pitcher_BABIP", CardRules.PitcherStats);
			k.SpeedPer10 = doc.RootElement.GetProperty("speed_per10").GetDouble();
			k.ErrorPer10 = doc.RootElement.GetProperty("error_per10").GetDouble();
			return k;
		}

		// Cards keyed (kind, sidx, year) with their hidden profiles (card_profiles.csv).
		public static Dictionary<(char, int, int), CardRow> LoadCards(string dir)
		{
			Dictionary<(char, int, int), CardRow> cards = CardTest.LoadRatings(Path.Combine(dir, "card_ratings.csv"));
			string profiles = Path.Combine(dir, "card_profiles.csv");
			if (File.Exists(profiles))
			{
				foreach (string line in File.ReadLines(profiles, Encoding.UTF8).Skip(1))
				{
					string[] c = line.Split(',');
					if (c.Length < 9) continue;
					char kind = c[0][0];
					int n = kind == 'B' ? 7 : 5;
					if (!cards.TryGetValue((kind, int.Parse(c[2], Inv), int.Parse(c[1], Inv)), out CardRow row)) continue;
					row.Profile = c.Skip(c.Length - n).Select(x => double.Parse(x, Inv)).ToArray();
				}
			}
			return cards;
		}

		// ---- one game ----

		// Outs a starter is good for, from 체력 (a regular starter's card sits near 80 = 5⅓ innings).
		public static double StartOuts(double stamina) => Math.Clamp(5.5 + 0.1 * (stamina - 80), 3, 8.5) * 3;

		// Outs per relief outing, from 체력 (relievers sit near 45 = one inning).
		public static double ReliefOuts(double stamina) => Math.Clamp(3 + (stamina - 45) / 5, 2, 9);

		public static (SimStats stats, SeasonModel model) Play(GameTeam home, GameTeam away, GameEnv env, ulong seed, IGameRecorder recorder)
		{
			SeasonModel m = Build(home, away, env);
			SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, seed);
			SimStats st = sim.PlayOne(0, 1, recorder);
			return (st, m);
		}

		// Today's condition scales every stat of the card (최상 +10% ... 최하 -10%); the rules are
		// linear in the stats, so good and bad days cancel out on average.
		private static CardRow WithCondition(GameCard g)
		{
			if (g.Condition == 0)
			{
				return g.Row;
			}
			CardRow c = g.Row.Clone();
			double f = 1 + g.Condition;
			for (int j = 0; j < 6; j++)
			{
				c.R[j] = g.Row.R[j] * f;
			}
			for (int p = 0; p < 8; p++)
			{
				c.Defs[p] = g.Row.Defs[p] * f;
			}
			c.Pitches = g.Row.Pitches.Select(x => (x.code, x.rating * f)).ToList();
			return c;
		}

		// Prototype switch: 작전실, pitch counts and fatigue (off = the old deck rules).
		public static bool UseTactics = true;
		public static TacticRules Rules = new TacticRules();

		private static TacticModel BuildTactics(GameTeam[] teams, List<GameCard> batters, List<GameCard> pitchers, List<int> batterTeam, List<int> pitcherTeam, List<CardRow> pitcherRows, SeasonModel m)
		{
			int nb = batters.Count, np = pitchers.Count;
			TacticModel t = new TacticModel { R = Rules };
			for (int k = 0; k < 2; k++) t.Team[k] = teams[k].Tactics ?? new GameTactics();
			t.BatHit = new double[nb];
			t.BatPat = new double[nb];
			t.BatRun = new double[nb];
			t.BatBunt = new double[nb];
			t.BuntWeak = new double[nb];
			double OnBase(BatterTalent b) => b.BB + b.HBP + b.HR + (1 - b.K - b.BB - b.HBP - b.HR) * b.Babip;
			League lg = m.League;
			double lgOb = lg.BB + lg.HBP + lg.HR + (1 - lg.K - lg.BB - lg.HBP - lg.HR) * lg.Babip;
			for (int i = 0; i < nb; i++)
			{
				GameTactics g = batters[i].Own ?? t.Team[batterTeam[i]];
				t.BatHit[i] = GameTactics.S(g.Hitting);
				t.BatPat[i] = GameTactics.S(g.Patience);
				t.BatRun[i] = GameTactics.S(g.Running);
				t.BatBunt[i] = GameTactics.S(g.Bunting);
				t.BuntWeak[i] = Math.Clamp(Math.Pow(lgOb / Math.Max(0.05, OnBase(m.BT[i])), 3), 0.3, 3);
			}
			t.PitRep = new double[np];
			t.PitApp = new double[np];
			t.PitLoc = new double[np];
			t.PitEff = new double[np];
			t.Limit = new double[np];
			t.CarryIn = new double[np];
			t.CtrlX = new double[np];
			t.TechX = new double[np];
			for (int i = 0; i < np; i++)
			{
				t.CtrlX[i] = CardRules.X(pitcherRows[i].R[2]);
				t.TechX[i] = CardRules.X(pitcherRows[i].R[3]);
				GameTactics g = pitchers[i].Own ?? t.Team[pitcherTeam[i]];
				t.PitRep[i] = GameTactics.S(g.Repertoire);
				t.PitApp[i] = GameTactics.S(g.Approach);
				t.PitLoc[i] = GameTactics.S(g.Location);
				t.PitEff[i] = -GameTactics.S(g.Effort);
				t.Limit[i] = t.R.Limit(pitcherRows[i].R[0]);
				if (t.PitRep[i] != 0 && m.Mix[i] != null)
				{
					m.Mix[i] = m.Mix[i].Lean(Math.Max(0.1, 1 + t.R.FastLean * t.PitRep[i]));
				}
			}
			return t;
		}

		public static SeasonModel Build(GameTeam home, GameTeam away, GameEnv env)
		{
			CardRules k = env.Rules;
			GameTeam[] teams = { home, away };
			Season season = new Season { Year = 2025, Games = 144 };
			List<GameCard> batters = new List<GameCard>(), pitchers = new List<GameCard>();
			List<int> batterTeam = new List<int>(), pitcherTeam = new List<int>();
			for (int t = 0; t < 2; t++)
			{
				TeamSeason ts = new TeamSeason { Code = t + 1 };
				foreach (GameCard c in teams[t].Order)
				{
					batters.Add(c);
					batterTeam.Add(t);
					ts.Batters.Add(new BatterLine { Sidx = c.Row.Sidx, Name = c.Row.Name, Year = c.Row.Year, Team = t + 1 });
				}
				foreach (GameCard c in teams[t].Pitchers.Where(p => p != null))
				{
					pitchers.Add(c);
					pitcherTeam.Add(t);
					ts.Pitchers.Add(new PitcherLine { Sidx = c.Row.Sidx, Name = c.Row.Name, Year = c.Row.Year, Team = t + 1, G = 1 });
				}
				season.Teams.Add(ts);
			}
			int nb = batters.Count, np = pitchers.Count;
			SeasonModel m = new SeasonModel { Season = season, League = env.League, MP = env.MP, Field = env.Field };
			m.Batters = season.Batters.ToArray();
			m.Pitchers = season.Pitchers.ToArray();
			m.BatterTeam = batterTeam.ToArray();
			m.TeamBatters = Enumerable.Range(0, 2).Select(t => Enumerable.Range(0, nb).Where(i => batterTeam[i] == t).ToArray()).ToArray();
			m.TeamPitchers = Enumerable.Range(0, 2).Select(t => Enumerable.Range(0, np).Where(i => pitcherTeam[i] == t).ToArray()).ToArray();
			m.TeamStarters = m.TeamPitchers;
			m.TeamCloser = new[] { -1, -1 };
			m.TargetPA = new double[nb];
			m.TargetGS = new double[np];
			m.StartOuts = pitchers.Select(c => StartOuts(c.Row.R[0])).ToArray();
			m.ReliefOuts = pitchers.Select(c => ReliefOuts(c.Row.R[0])).ToArray();
			List<CardRow> batterRows = batters.Select(WithCondition).ToList(), pitcherRows = pitchers.Select(WithCondition).ToList();
			m.Schedule = new[] { (0, 1) };
			m.ParkF = m.NeutralParkF = Enumerable.Range(0, 2).Select(_ => Enumerable.Repeat(1.0, PC.Count).ToArray()).ToArray();
			m.NeutralParks = true;

			// Talents in the reference world: the clean rules plus each card's hidden profile.
			League lg = env.League;
			m.BT = new BatterTalent[nb];
			m.BatMental = new double[nb];
			m.BatT = Enumerable.Range(0, 4).Select(_ => new double[nb]).ToArray();
			m.Defs = new double[nb][];
			m.IsCatcher = new bool[nb];
			m.RunX = new double[nb];
			for (int i = 0; i < nb; i++)
			{
				CardRow c = batterRows[i];
				BatterTalent t = CardTest.BatterFrom(k, c, Reference, lg, hybrid: true);
				t.Sac = lg.Sac * 0.7;
				t.Speed = Math.Clamp(Math.Exp(k.SpeedPer10 * CardRules.X(c.R[3])), 0.7, 1.35);
				m.BT[i] = t;
				m.BatMental[i] = c.R[4];
				bool hybrid = c.Profile != null;
				m.BatT[0][i] = hybrid ? CardRules.HybridPerPercent(k.BK, c.R, t.K) : CardRules.PerPercent(k.BK, c.R);
				m.BatT[1][i] = hybrid ? CardRules.HybridPerPercent(k.BBB, c.R, t.BB) : CardRules.PerPercent(k.BBB, c.R);
				m.BatT[2][i] = hybrid ? CardRules.HybridPerPercent(k.BHR, c.R, t.HR) : CardRules.PerPercent(k.BHR, c.R);
				m.BatT[3][i] = hybrid ? CardRules.HybridPerPercent(k.BBabip, c.R, t.Babip) : CardRules.PerPercent(k.BBabip, c.R);
				m.Defs[i] = c.Defs;
				m.IsCatcher[i] = c.MainPos == 1;
				m.RunX[i] = CardRules.X(c.R[3]);
			}
			m.PT = new PitcherTalent[np];
			m.PitMental = new double[np];
			m.PitT = Enumerable.Range(0, 4).Select(_ => new double[np]).ToArray();
			m.PitRisp = new double[np];
			m.VeloX = new double[np];
			m.PitCT = Enumerable.Range(0, 4).Select(_ => new double[np]).ToArray();
			m.Mix = new PitchMix[np];
			Func<int, bool> boosted = st => st >= 2 && st <= 4;
			for (int i = 0; i < np; i++)
			{
				CardRow c = pitcherRows[i];
				PitcherTalent t = CardTest.PitcherFrom(k, c, Reference, lg, hybrid: true);
				m.PT[i] = t;
				m.PitMental[i] = c.R[5];
				bool hybrid = c.Profile != null;
				CardRules.Comp[] pc = CardTest.PitcherComps(k);
				double[] rates = { t.K, t.BB, t.HR, t.Babip };
				for (int j = 0; j < 4; j++)
				{
					m.PitT[j][i] = hybrid ? CardRules.HybridPerPercent(pc[j], c.R, rates[j]) : CardRules.PerPercent(pc[j], c.R);
					m.PitCT[j][i] = hybrid ? CardRules.HybridPerPercent(pc[j], c.R, rates[j], boosted) : CardRules.PerPercent(pc[j], c.R, boosted);
				}
				m.PitRisp[i] = hybrid && c.Profile.Length > 4 ? c.Profile[4] : 1.0;
				m.VeloX[i] = CardRules.X(c.R[1]);
				m.Mix[i] = c.Pitches.Count > 0 ? PitchMix.Build(CardTest.SlotsFrom(c), m.MP) : null;
			}
			m.Clutch = true;

			// Handedness and the generic platoon splits.
			m.BatSide = batters.Select(c => (int)c.Bats).ToArray();
			m.PitcherType = pitchers.Select(c => Platoon.TypeOf(new Thrower { Throws = c.Throws, LowSlot = c.LowSlot })).ToArray();
			m.PlatoonF = Platoon.Build(new[] { 0.62, 0.33, 0.05 }, new[] { 0.62, 0.12, 0.24, 0.02 }, m.MP);

			// Decks: batting order, who stands where, the pitching staff by role.
			m.Decks = new GameDeck[2];
			m.ErrorMult = new double[2];
			int bOffset = 0, pOffset = 0;
			for (int t = 0; t < 2; t++)
			{
				GameTeam team = teams[t];
				GameDeck d = new GameDeck { StarterSlot = team.StarterSlot };
				double defSum = 0;
				int fielders = 0;
				for (int slot = 0; slot < 9; slot++)
				{
					d.Order[slot] = bOffset + slot;
					int pos = team.Position[slot];
					if (pos < 8)
					{
						d.At[pos] = bOffset + slot;
						defSum += batterRows[bOffset + slot].Defs[pos];
						fielders++;
					}
				}
				m.ErrorMult[t] = Math.Exp(k.ErrorPer10 * CardRules.X(defSum / Math.Max(1, fielders)));
				int next = pOffset;
				for (int s = 0; s < 11; s++)
				{
					d.Pitchers[s] = team.Pitchers[s] != null ? next++ : -1;
				}
				m.Decks[t] = d;
				bOffset += 9;
				pOffset = next;
			}
			if (UseTactics)
			{
				m.Tac = BuildTactics(teams, batters, pitchers, batterTeam, pitcherTeam, pitcherRows, m);
				for (int t = 0; t < 2; t++)
				{
					double sum = 0;
					for (int pos = 0; pos < 8; pos++) sum += m.Defs[m.Decks[t].At[pos]][pos];
					m.Tac.TeamDefX[t] = CardRules.X(sum / 8);
				}
			}
			return m;
		}
	}
}
