using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Ma9MSim
{
	// One player-season's final card (from tools/scripts/make_cards.py card_ratings.csv).
	public sealed class CardRow
	{
		public char Kind;
		public int Year, Sidx, MainPos;
		public string Name, Role;
		public bool Made;
		public double Share;
		public double Weight;                // plate appearances / batters faced (from the records)
		public double[] R = new double[6];  // batter: 컨택 파워 선구 주력 정신 수비 / pitcher: 체력 구속 컨트롤 테크닉 구위 정신
		public double[] Raw = new double[6]; // the same before the short-season cut, unrounded
		public double[] Profile;             // hybrid mode: hidden per-card multipliers per component
		public List<(string code, double rating)> Pitches = new List<(string, double)>();
		// Batters: defence at 포수 1루 2루 3루 유격 좌익 중견 우익 (final and before the cut).
		public double[] Defs = new double[8], RawDefs = new double[8];
		public double ShownCatcher = double.NaN;  // development test: catcher defence as shown (cards plus bonus points)

		public CardRow Clone()
		{
			CardRow c = (CardRow)MemberwiseClone();
			c.R = (double[])R.Clone();
			c.Raw = (double[])Raw.Clone();
			c.Profile = Profile != null ? (double[])Profile.Clone() : null;
			c.Defs = (double[])Defs.Clone();
			c.RawDefs = (double[])RawDefs.Clone();
			return c;
		}
	}

	// How a card plays: each outcome rate is the reference league rate times exp(Σ β·x), with
	// x = (stat − 65) / 10, then moved into the season's environment by undoing the card's era
	// adjustment. Coefficients start from a regression of fitted talents on the cards and are
	// then tuned by replaying the seasons with cards only.
	public sealed class CardRules
	{
		public sealed class Comp
		{
			public string Name, Label;
			public int[] Stats;      // indices into CardRow.R
			public string[] StatNames;
			public double Base;      // reference-world league rate (stats at 65)
			public double[] Beta;    // linear term per stat
			public double[] Gamma;   // curvature per stat (inside the card range; straight beyond it)
			// Rate form: most stats were scored linearly from a rate, so the rate is linear in the
			// stat (base x (1 + g)); 주력 was scored from log rates, so its results are exponential.
			public bool Linear = true;
		}

		// Above 95 (training only) each stat keeps half its slope at 95: diminishing returns.
		public const double TrainedSlope = 0.5;

		// Contribution of one stat: b x + g q(x) inside the card range, straight lines outside.
		public static double G(double beta, double gamma, double x)
		{
			if (x > XHigh) return beta * XHigh + gamma * XHigh * XHigh + TrainedSlope * (beta + 2 * gamma * XHigh) * (x - XHigh);
			if (x < XLow) return beta * XLow + gamma * XLow * XLow + (beta + 2 * gamma * XLow) * (x - XLow);
			return beta * x + gamma * x * x;
		}

		public static double GPrime(double beta, double gamma, double x)
		{
			if (x > XHigh) return TrainedSlope * (beta + 2 * gamma * XHigh);
			if (x < XLow) return beta + 2 * gamma * XLow;
			return beta + 2 * gamma * x;
		}

		// The card range is 40..95 (x -2.5..3); trained stats beyond 95 continue on the tangent.
		public const double XLow = -2.5, XHigh = 3.0;

		public static double Q(double x)
		{
			if (x > XHigh) return XHigh * XHigh + 2 * XHigh * (x - XHigh);
			if (x < XLow) return XLow * XLow + 2 * XLow * (x - XLow);
			return x * x;
		}

		public static double QPrime(double x) => 2 * Math.Clamp(x, XLow, XHigh);

		public static readonly string[] BatterStats = { "컨택", "파워", "선구", "주력", "정신", "수비" };
		public static readonly string[] PitcherStats = { "체력", "구속", "컨트롤", "테크닉", "구위", "정신" };

		// Batter components (0 컨택, 1 파워, 2 선구, 3 주력): the stat the result was derived from
		// comes first; the others carry the correlations a card's four numbers must stand in for
		// (sluggers strike out more, patient hitters walk more and put fewer balls in play...).
		public Comp BK = new Comp { Name = "K", Label = "삼진", Stats = new[] { 2, 0, 1 } };
		public Comp BBB = new Comp { Name = "BB", Label = "볼넷", Stats = new[] { 2, 1, 0 } };
		public Comp BHR = new Comp { Name = "HR", Label = "홈런", Stats = new[] { 1, 0, 2 } };
		public Comp BBabip = new Comp { Name = "BABIP", Label = "인플레이 안타", Stats = new[] { 0, 3, 1, 2 } };
		public Comp BDouble = new Comp { Name = "2B", Label = "2루타 비중", Stats = new[] { 1, 3, 0 } };
		public Comp BTriple = new Comp { Name = "3B", Label = "3루타 비중", Stats = new[] { 3, 1 } };
		public Comp BSteal = new Comp { Name = "SB", Label = "도루 시도", Stats = new[] { 3 } };
		public double SpeedPer10 = 0.08;   // baserunning multiplier exp(SpeedPer10 · x주력)
		// Pitcher components.
		// Pitcher components (1 구속, 2 컨트롤, 3 테크닉, 4 구위).
		public Comp PK = new Comp { Name = "K", Label = "탈삼진", Stats = new[] { 1, 4, 3 } };
		public Comp PBB = new Comp { Name = "BB", Label = "볼넷", Stats = new[] { 2, 4 } };
		public Comp PHR = new Comp { Name = "HR", Label = "피홈런", Stats = new[] { 3, 4, 1 } };
		public Comp PBabip = new Comp { Name = "BABIP", Label = "인플레이 피안타", Stats = new[] { 3, 4, 1 } };
		public double ErrorPer10 = -0.25;  // team fielding: errors × exp(ErrorPer10 · x수비)
		// Pure cards: a pitcher's 위기관리 (hits and homers allowed with runners in scoring
		// position) = exp(RispBase + RispPer10 · x정신).
		public double RispBase, RispPer10;

		public IEnumerable<Comp> Batter => new[] { BK, BBB, BHR, BBabip, BDouble, BTriple, BSteal };
		public IEnumerable<Comp> Pitcher => new[] { PK, PBB, PHR, PBabip };

		public static double X(double rating) => (rating - 65) / 10.0;

		public static double Mult(Comp c, double[] r)
		{
			double s = 0;
			for (int j = 0; j < c.Stats.Length; j++)
			{
				s += G(c.Beta[j], c.Gamma[j], X(r[c.Stats[j]]));
			}
			return c.Linear ? Math.Max(0.15, 1 + s) : Math.Exp(s);
		}

		// d log(rate) / d(boost) when every stat is multiplied by (1 + boost): what 정신 does.
		public static double PerPercent(Comp c, double[] r, Func<int, bool> only = null)
		{
			double s = 0;
			for (int j = 0; j < c.Stats.Length; j++)
			{
				if (only != null && !only(c.Stats[j])) continue;
				double rating = r[c.Stats[j]];
				s += GPrime(c.Beta[j], c.Gamma[j], X(rating)) * rating / 10.0;
			}
			return c.Linear ? s / Mult(c, r) : s;
		}

		// Hybrid: the card's own rate in the reference world, rule + hidden offset, kept in bounds.
		// A +10 therefore changes every card's rate by the same amount (until a bound).
		public static double HybridRate(Comp c, double[] r, double offset, double lo, double hi)
		{
			double s = 0;
			for (int j = 0; j < c.Stats.Length; j++)
			{
				s += G(c.Beta[j], c.Gamma[j], X(r[c.Stats[j]]));
			}
			return Math.Clamp(c.Linear ? c.Base * (1 + s) + offset : c.Base * Math.Exp(s) * offset, lo, hi);
		}

		public static double RuleRate(Comp c, double[] r)
		{
			double s = 0;
			for (int j = 0; j < c.Stats.Length; j++)
			{
				s += G(c.Beta[j], c.Gamma[j], X(r[c.Stats[j]]));
			}
			return c.Linear ? c.Base * (1 + s) : c.Base * Math.Exp(s);
		}

		// d log(rate) / d(boost) for the hybrid rate (every stat multiplied by 1 + boost).
		public static double HybridPerPercent(Comp c, double[] r, double rate, Func<int, bool> only = null)
		{
			double s = 0;
			for (int j = 0; j < c.Stats.Length; j++)
			{
				if (only != null && !only(c.Stats[j])) continue;
				double rating = r[c.Stats[j]];
				s += GPrime(c.Beta[j], c.Gamma[j], X(rating)) * rating / 10.0;
			}
			return c.Linear ? c.Base * s / Math.Max(1e-6, rate) : s;
		}

		public CardRules()
		{
			BTriple.Linear = false;
			BSteal.Linear = false;
			foreach (Comp c in Batter)
			{
				c.StatNames = c.Stats.Select(i => BatterStats[i]).ToArray();
			}
			foreach (Comp c in Pitcher)
			{
				c.StatNames = c.Stats.Select(i => PitcherStats[i]).ToArray();
			}
		}
	}

	// Season ↔ reference-world factors, the inverse of the card maker's era adjustments.
	public sealed class EraWorld
	{
		public sealed class Factors
		{
			public double BK, BBB, BHR, BBabip, BSteal, PK, PBB, PHR, PBabip;
		}

		public readonly Dictionary<int, Factors> Seasons = new Dictionary<int, Factors>();

		public static EraWorld Build(IEnumerable<Season> seasons)
		{
			var lines = new Dictionary<int, (double avg, double slg, double era, double whip, League lg)>();
			foreach (Season s in seasons)
			{
				List<BatterLine> bs = s.Batters.Where(b => !b.Replacement).ToList();
				double ab = bs.Sum(b => b.AB), h = bs.Sum(b => b.H), d = bs.Sum(b => b.D), t = bs.Sum(b => b.T), hr = bs.Sum(b => b.HR);
				List<PitcherLine> ps = s.Pitchers.ToList();
				double outs = ps.Sum(p => p.Outs);
				lines[s.Year] = (h / ab, (h + d + 2 * t + 3 * hr) / ab, ps.Sum(p => p.ER) * 27 / outs, (ps.Sum(p => p.H) + ps.Sum(p => p.BB)) * 3 / outs, League.From(s));
			}
			double rAvg = lines.Values.Average(x => x.avg), rSlg = lines.Values.Average(x => x.slg), rEra = lines.Values.Average(x => x.era), rWhip = lines.Values.Average(x => x.whip);
			double rK = lines.Values.Average(x => x.lg.K), rBB = lines.Values.Average(x => x.lg.BB), rSt = lines.Values.Average(x => x.lg.Steal);
			EraWorld w = new EraWorld();
			foreach (var kv in lines)
			{
				var x = kv.Value;
				w.Seasons[kv.Key] = new Factors
				{
					BK = x.lg.K > rK ? rK / x.lg.K : 1, BBB = x.lg.BB < rBB ? rBB / x.lg.BB : 1, BHR = rSlg / x.slg, BBabip = rAvg / x.avg,
					BSteal = x.lg.Steal < rSt ? rSt / x.lg.Steal : 1,
					PK = x.lg.K < rK ? rK / x.lg.K : 1, PBB = x.lg.BB > rBB ? rBB / x.lg.BB : 1, PHR = rEra / x.era, PBabip = rWhip / x.whip
				};
			}
			return w;
		}
	}

	public static class CardTest
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Switches for isolating effects in the card replay.
		public static bool UseClutch = true, UseCardPitches = true, UseCardDefence = true;
		// Replay with the uncut ratings: measures the rules alone, without the short-season cut.
		public static bool UseRaw;

		private static double[] Ratings(CardRow c) => UseRaw ? c.Raw : c.R;
		public static readonly ConcurrentBag<string> Debug = new ConcurrentBag<string>();

		public static Dictionary<(char, int, int), CardRow> LoadRatings(string path)
		{
			Dictionary<(char, int, int), CardRow> rows = new Dictionary<(char, int, int), CardRow>();
			string[] lines = File.ReadAllLines(path, Encoding.UTF8);
			for (int i = 1; i < lines.Length; i++)
			{
				string[] c = lines[i].Split(',');
				if (c.Length < 16)
				{
					continue;
				}
				CardRow r = new CardRow
				{
					Kind = c[0][0], Year = int.Parse(c[1]), Sidx = int.Parse(c[2]), Name = c[3], Made = c[4] == "1",
					Share = double.Parse(c[5], Inv), Role = c[6], MainPos = c[7].Length > 0 ? int.Parse(c[7]) : 0
				};
				for (int j = 0; j < 6; j++)
				{
					r.R[j] = double.Parse(c[8 + j], Inv);
				}
				for (int j = 0; j < 6; j++)
				{
					r.Raw[j] = c.Length > 16 + j ? double.Parse(c[16 + j], Inv) : r.R[j];
				}
				foreach (string part in c[15].Split(' ', StringSplitOptions.RemoveEmptyEntries))
				{
					string[] kv = part.Split(':');
					r.Pitches.Add((kv[0], double.Parse(kv[1], Inv)));
				}
				if (r.Kind == 'B')
				{
					string[] d = c[14].Split(' ', StringSplitOptions.RemoveEmptyEntries);
					for (int p = 0; p < 8; p++)
					{
						r.Defs[p] = d.Length == 8 ? double.Parse(d[p], Inv) : 40;
						// Before the cut: the main position carries the card's defence, other
						// positions it played 10 less, positions it never played 20, DH 40 anywhere.
						r.RawDefs[p] = r.MainPos == 10 || r.MainPos == 0 ? 40
							: p + 1 == r.MainPos ? r.Raw[5]
							: r.Defs[p] > 20 ? Math.Max(40, r.Raw[5] - 10) : 20;
					}
				}
				rows[(r.Kind, r.Sidx, r.Year)] = r;
			}
			if (CutPower.HasValue)
			{
				foreach (CardRow r in rows.Values)
				{
					Recut(r, CutPower.Value);
				}
			}
			return rows;
		}

		// Short-season cut with another strength: 40 + (stat − 40) × share^power.
		public static double? CutPower;

		public static void Recut(CardRow r, double power)
		{
			double f = Math.Pow(Math.Min(1.0, r.Share), power);
			for (int j = 0; j < 6; j++)
			{
				r.R[j] = Math.Round(40 + (r.Raw[j] - 40) * f);
			}
			for (int p = 0; p < 8; p++)
			{
				r.Defs[p] = r.RawDefs[p] > 40 ? Math.Round(40 + (r.RawDefs[p] - 40) * f) : r.RawDefs[p];
			}
		}

		// ---- talents from cards ----

		internal static BatterTalent BatterFrom(CardRules k, CardRow c, EraWorld.Factors f, League lg, bool hybrid = false)
		{
			double[] r = Ratings(c);
			if (hybrid && c.Profile != null)
			{
				CardRules.Comp[] comps = BatterComps(k);
				double R(int j) => CardRules.HybridRate(comps[j], r, c.Profile[j], BatterBounds[j].lo, BatterBounds[j].hi);
				return new BatterTalent
				{
					K = R(0) / f.BK, BB = R(1) / f.BBB, HBP = lg.HBP, HR = R(2) / f.BHR, Babip = R(3) / f.BBabip,
					Double = R(4), Triple = R(5), Steal = R(6) / f.BSteal, Sac = lg.Sac * 0.7, Speed = 1.0
				};
			}
			double[] h = new double[] { 1, 1, 1, 1, 1, 1, 1 };
			BatterTalent t = new BatterTalent
			{
				K = Clamp(k.BK.Base * CardRules.Mult(k.BK, r) * h[0] / f.BK),
				BB = Clamp(k.BBB.Base * CardRules.Mult(k.BBB, r) * h[1] / f.BBB),
				HBP = lg.HBP,
				HR = Clamp(k.BHR.Base * CardRules.Mult(k.BHR, r) * h[2] / f.BHR),
				Babip = Math.Clamp(k.BBabip.Base * CardRules.Mult(k.BBabip, r) * h[3] / f.BBabip, 0.12, 0.5),
				Double = Math.Clamp(k.BDouble.Base * CardRules.Mult(k.BDouble, r) * h[4], 0.05, 0.45),
				Triple = Math.Clamp(k.BTriple.Base * CardRules.Mult(k.BTriple, r) * h[5], 0.001, 0.15),
				Steal = Clamp(k.BSteal.Base * CardRules.Mult(k.BSteal, r) * h[6] / f.BSteal),
				Sac = lg.Sac * 0.7
			};
			t.Speed = Math.Clamp(Math.Exp(k.SpeedPer10 * CardRules.X(r[3])), 0.7, 1.35);
			return t;
		}

		internal static PitcherTalent PitcherFrom(CardRules k, CardRow c, EraWorld.Factors f, League lg, bool hybrid = false)
		{
			double[] r = Ratings(c);
			if (hybrid && c.Profile != null)
			{
				CardRules.Comp[] comps = PitcherComps(k);
				double R(int j) => CardRules.HybridRate(comps[j], r, c.Profile[j], PitcherBounds[j].lo, PitcherBounds[j].hi);
				return new PitcherTalent { K = R(0) / f.PK, BB = R(1) / f.PBB, HBP = lg.HBP, HR = R(2) / f.PHR, Babip = R(3) / f.PBabip };
			}
			double[] h = new double[] { 1, 1, 1, 1 };
			return new PitcherTalent
			{
				K = Clamp(k.PK.Base * CardRules.Mult(k.PK, r) * h[0] / f.PK),
				BB = Clamp(k.PBB.Base * CardRules.Mult(k.PBB, r) * h[1] / f.PBB),
				HBP = lg.HBP,
				HR = Clamp(k.PHR.Base * CardRules.Mult(k.PHR, r) * h[2] / f.PHR),
				Babip = Math.Clamp(k.PBabip.Base * CardRules.Mult(k.PBabip, r) * h[3] / f.PBabip, 0.12, 0.5)
			};
		}

		private static double Clamp(double v) => Math.Clamp(v, 0.0005, 0.6);

		// Reference-world bounds of each result (batter K, BB, HR, BABIP, 2B, 3B, SB; pitcher K, BB, HR, BABIP).
		private static readonly (double lo, double hi)[] BatterBounds = { (0.01, 0.6), (0.005, 0.35), (0.0003, 0.2), (0.12, 0.5), (0.03, 0.5), (0.0005, 0.15), (0.001, 0.6) };
		private static readonly (double lo, double hi)[] PitcherBounds = { (0.02, 0.6), (0.005, 0.3), (0.001, 0.15), (0.12, 0.5) };

		internal static CardRules.Comp[] BatterComps(CardRules k) => new[] { k.BK, k.BBB, k.BHR, k.BBabip, k.BDouble, k.BTriple, k.BSteal };

		internal static CardRules.Comp[] PitcherComps(CardRules k) => new[] { k.PK, k.PBB, k.PHR, k.PBabip };

		internal static PitchSlot[] SlotsFrom(CardRow c)
		{
			return c.Pitches.Select(p => new PitchSlot
			{
				Name = p.code, Rating = p.rating, TwoSeam = p.code == "TSEAM",
				Family = p.code switch
				{
					"FAST" => PitchFamily.Fastball, "CURT" => PitchFamily.Cutter, "KNUKL" => PitchFamily.Knuckle,
					"HSLID" or "JSLID" or "SLID" => PitchFamily.Slider, "CURV" or "SCURV" => PitchFamily.Curve,
					"FORK" or "SFF" => PitchFamily.Split, "SINK" or "TSEAM" => PitchFamily.Sinker, _ => PitchFamily.Change
				}
			}).ToArray();
		}

		// Clean rules for the hybrid mode: each result moves only with the stats it was derived
		// from, always in the good direction.
		public static CardRules CleanRules()
		{
			CardRules k = new CardRules();
			k.BK.Stats = new[] { 0, 2 }; k.BBB.Stats = new[] { 2 }; k.BHR.Stats = new[] { 1 }; k.BBabip.Stats = new[] { 0 };
			k.BDouble.Stats = new[] { 1 }; k.BTriple.Stats = new[] { 3 }; k.BSteal.Stats = new[] { 3 };
			k.PK.Stats = new[] { 1, 4 }; k.PBB.Stats = new[] { 2 }; k.PHR.Stats = new[] { 3, 4 }; k.PBabip.Stats = new[] { 3, 4 };
			foreach (CardRules.Comp c in k.Batter) c.StatNames = c.Stats.Select(i => CardRules.BatterStats[i]).ToArray();
			foreach (CardRules.Comp c in k.Pitcher) c.StatNames = c.Stats.Select(i => CardRules.PitcherStats[i]).ToArray();
			return k;
		}

		// Rules read off how each stat was scored (tools/scripts/make_cards.py card_rule_slopes.json):
		// every stat moves exactly the results it was built from, by the amount its scale implies.
		public static string SlopesPath;

		public static CardRules DefinitionalRules(string path, List<SeasonFit> fits, Dictionary<(char, int, int), CardRow> cards, EraWorld world)
		{
			using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
			System.Text.Json.JsonElement bat = doc.RootElement.GetProperty("batter"), pit = doc.RootElement.GetProperty("pitcher");
			double D(System.Text.Json.JsonElement side, string stat, string comp) => side.GetProperty(stat).GetProperty(comp).GetDouble();
			CardRules k = new CardRules();
			void Set(CardRules.Comp c, int[] stats, double[] delta, string[] names)
			{
				c.Stats = stats;
				c.StatNames = stats.Select(i => names[i]).ToArray();
				c.Beta = delta;      // absolute change per +10 for now; divided by the base below
				c.Gamma = new double[stats.Length];
			}
			Set(k.BK, new[] { 2, 0 }, new[] { D(bat, "eye", "K"), D(bat, "contact", "K") }, CardRules.BatterStats);
			Set(k.BBB, new[] { 2 }, new[] { D(bat, "eye", "BB") }, CardRules.BatterStats);
			Set(k.BHR, new[] { 1, 0 }, new[] { D(bat, "power", "HR"), D(bat, "contact", "HR") }, CardRules.BatterStats);
			Set(k.BBabip, new[] { 0 }, new[] { D(bat, "contact", "BABIP") }, CardRules.BatterStats);
			Set(k.BDouble, new[] { 1 }, new[] { D(bat, "power", "2B") }, CardRules.BatterStats);
			Set(k.BTriple, new[] { 3 }, new[] { D(bat, "speed", "3B") }, CardRules.BatterStats);
			Set(k.BSteal, new[] { 3 }, new[] { D(bat, "speed", "SB") }, CardRules.BatterStats);
			// 구속 keeps part of its strikeout slope; 컨트롤 adds strikeouts too (ahead in the count).
			Set(k.PK, new[] { 1, 4, 2 }, new[] { D(pit, "velocity", "K") * VeloKScale, D(pit, "stuff", "K"), ControlK }, CardRules.PitcherStats);
			Set(k.PBB, new[] { 2 }, new[] { D(pit, "control", "BB") }, CardRules.PitcherStats);
			Set(k.PHR, new[] { 3 }, new[] { D(pit, "technic", "HR") }, CardRules.PitcherStats);
			Set(k.PBabip, new[] { 3, 4 }, new[] { D(pit, "technic", "BABIP"), D(pit, "stuff", "BABIP") }, CardRules.PitcherStats);

			// Bases: the reference-world league rate (arithmetic mean for linear results, geometric
			// mean for the log-scored 주력 results), weighted by playing time.
			var bat_ = new List<(BatterTalent t, EraWorld.Factors f, double w)>();
			var pit_ = new List<(PitcherTalent t, EraWorld.Factors f, double w)>();
			foreach (SeasonFit fit in fits)
			{
				EraWorld.Factors f = world.Seasons[fit.Model.Season.Year];
				for (int i = 0; i < fit.Model.Batters.Length; i++)
				{
					if (!fit.Model.Batters[i].Replacement) bat_.Add((fit.Model.BT[i], f, fit.Model.Batters[i].PA));
				}
				for (int i = 0; i < fit.Model.Pitchers.Length; i++)
				{
					if (!fit.Model.Pitchers[i].Replacement) pit_.Add((fit.Model.PT[i], f, fit.Model.Pitchers[i].BF));
				}
			}
			double Mean<T>(List<(T t, EraWorld.Factors f, double w)> rows, Func<T, EraWorld.Factors, double> rate, bool log)
				=> log ? Math.Exp(rows.Sum(x => Math.Log(Math.Max(1e-5, rate(x.t, x.f))) * x.w) / rows.Sum(x => x.w))
					: rows.Sum(x => rate(x.t, x.f) * x.w) / rows.Sum(x => x.w);
			k.BK.Base = Mean(bat_, (t, f) => t.K * f.BK, false);
			k.BBB.Base = Mean(bat_, (t, f) => t.BB * f.BBB, false);
			k.BHR.Base = Mean(bat_, (t, f) => t.HR * f.BHR, false);
			k.BBabip.Base = Mean(bat_, (t, f) => t.Babip * f.BBabip, false);
			k.BDouble.Base = Mean(bat_, (t, f) => t.Double, false);
			k.BTriple.Linear = true;
			k.BSteal.Linear = true;
			k.BTriple.Base = Mean(bat_, (t, f) => t.Triple, false);
			k.BSteal.Base = Mean(bat_, (t, f) => t.Steal * f.BSteal, false);
			k.PK.Base = Mean(pit_, (t, f) => t.K * f.PK, false);
			k.PBB.Base = Mean(pit_, (t, f) => t.BB * f.PBB, false);
			k.PHR.Base = Mean(pit_, (t, f) => t.HR * f.PHR, false);
			k.PBabip.Base = Mean(pit_, (t, f) => t.Babip * f.PBabip, false);
			foreach (CardRules.Comp c in k.Batter.Concat(k.Pitcher))
			{
				if (c.Linear)
				{
					c.Beta = c.Beta.Select(d => d / c.Base).ToArray();
				}
			}
			return k;
		}

		// Sign each stat must take: +1 the rate rises with the stat, -1 it falls.
		private static readonly Dictionary<string, int> GoodSign = new Dictionary<string, int>
		{
			{ "B:K", -1 }, { "B:BB", 1 }, { "B:HR", 1 }, { "B:BABIP", 1 }, { "B:2B", 1 }, { "B:3B", 1 }, { "B:SB", 1 },
			{ "P:K", 1 }, { "P:BB", -1 }, { "P:HR", -1 }, { "P:BABIP", -1 }
		};

		// ---- initial coefficients: regress fitted talents (reference world) on the cards ----

		private static void FitInitial(CardRules k, List<SeasonFit> fits, Dictionary<(char, int, int), CardRow> cards, EraWorld world, bool clean = false)
		{
			var bat = new List<(CardRow c, BatterTalent t, EraWorld.Factors f, double w, bool q)>();
			var pit = new List<(CardRow c, PitcherTalent t, EraWorld.Factors f, double w, bool q)>();
			foreach (SeasonFit fit in fits)
			{
				EraWorld.Factors f = world.Seasons[fit.Model.Season.Year];
				for (int i = 0; i < fit.Model.Batters.Length; i++)
				{
					BatterLine b = fit.Model.Batters[i];
					if (!b.Replacement && cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c))
					{
						bat.Add((c, fit.Model.BT[i], f, b.PA, c.Share >= 0.999));
					}
				}
				for (int i = 0; i < fit.Model.Pitchers.Length; i++)
				{
					PitcherLine p = fit.Model.Pitchers[i];
					if (!p.Replacement && cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c))
					{
						pit.Add((c, fit.Model.PT[i], f, p.BF, c.Share >= 0.999));
					}
				}
			}
			void Fit<T>(CardRules.Comp comp, List<(CardRow c, T t, EraWorld.Factors f, double w, bool q)> rows, Func<T, EraWorld.Factors, double> refRate)
			{
				comp.Base = rows.Sum(x => refRate(x.t, x.f) * x.w) / rows.Sum(x => x.w);
				var q = rows.Where(x => x.q).ToList();
				int n = comp.Stats.Length;
				double[] y = q.Select(x => comp.Linear ? refRate(x.t, x.f) / comp.Base : Math.Log(Math.Max(1e-4, refRate(x.t, x.f)) / comp.Base)).ToArray();
				double[] w = q.Select(x => x.w).ToArray();
				bool[] useQ = Enumerable.Repeat(true, n).ToArray();
				bool[] use = Enumerable.Repeat(true, n).ToArray();
				string key = (typeof(T) == typeof(BatterTalent) ? "B:" : "P:") + comp.Name;
				double[] beta = new double[n], gamma = new double[n];
				double intercept = 0;
				for (int attempt = 0; attempt < 2 * n + 1; attempt++)
				{
					// Columns: linear terms of used stats, curvature of stats allowed to curve, intercept.
					List<Func<CardRow, double>> cols = new List<Func<CardRow, double>>();
					List<(int j, bool quad)> map = new List<(int, bool)>();
					for (int j = 0; j < n; j++)
					{
						int st = comp.Stats[j];
						if (!use[j]) continue;
						cols.Add(c => CardRules.X(c.Raw[st])); map.Add((j, false));
						if (useQ[j]) { cols.Add(c => CardRules.Q(CardRules.X(c.Raw[st]))); map.Add((j, true)); }
					}
					cols.Add(c => 1.0);
					double[] b = Wls(q.Select(x => cols.Select(f => f(x.c)).ToArray()).ToArray(), y, w);
					Array.Clear(beta); Array.Clear(gamma);
					for (int i = 0; i < map.Count; i++)
					{
						if (map[i].quad) gamma[map[i].j] = b[i]; else beta[map[i].j] = b[i];
					}
					intercept = b[b.Length - 1];
					if (!clean)
					{
						break;
					}
					// Clean rules: every stat monotone in its good direction over the card range.
					int sign = GoodSign[key];
					bool changed = false;
					for (int j = 0; j < n && !changed; j++)
					{
						if (!use[j]) continue;
						double lo = beta[j] + 2 * gamma[j] * CardRules.XLow, hi = beta[j] + 2 * gamma[j] * CardRules.XHigh;
						if (useQ[j] && (lo * sign < 0 || hi * sign < 0)) { useQ[j] = false; changed = true; }
						else if (!useQ[j] && beta[j] * sign <= 0) { use[j] = false; changed = true; }
					}
					if (!changed) break;
				}
				if (comp.Linear)
				{
					// rate/base = intercept + Σ g  ->  base' = base·intercept, g' = g / intercept.
					double b0 = Math.Max(0.05, intercept);
					comp.Base *= b0;
					comp.Beta = beta.Select(v => v / b0).ToArray();
					comp.Gamma = gamma.Select(v => v / b0).ToArray();
				}
				else
				{
					comp.Beta = beta;
					comp.Gamma = gamma;
					comp.Base *= Math.Exp(intercept);
				}
			}
			Fit(k.BK, bat, (t, f) => t.K * f.BK);
			Fit(k.BBB, bat, (t, f) => t.BB * f.BBB);
			Fit(k.BHR, bat, (t, f) => t.HR * f.BHR);
			Fit(k.BBabip, bat, (t, f) => t.Babip * f.BBabip);
			Fit(k.BDouble, bat, (t, f) => t.Double);
			Fit(k.BTriple, bat, (t, f) => t.Triple);
			Fit(k.BSteal, bat, (t, f) => t.Steal * f.BSteal);
			Fit(k.PK, pit, (t, f) => t.K * f.PK);
			Fit(k.PBB, pit, (t, f) => t.BB * f.PBB);
			Fit(k.PHR, pit, (t, f) => t.HR * f.PHR);
			Fit(k.PBabip, pit, (t, f) => t.Babip * f.PBabip);
		}

		// Weighted least squares without intercept (y is already relative to the base rate).
		private static double[] Wls(double[][] X, double[] y, double[] w)
		{
			int k = X[0].Length;
			double[,] a = new double[k, k + 1];
			for (int n = 0; n < y.Length; n++)
			{
				for (int i = 0; i < k; i++)
				{
					for (int j = 0; j < k; j++)
					{
						a[i, j] += w[n] * X[n][i] * X[n][j];
					}
					a[i, k] += w[n] * X[n][i] * y[n];
				}
			}
			for (int i = 0; i < k; i++)
			{
				int pivot = i;
				for (int r = i + 1; r < k; r++)
				{
					if (Math.Abs(a[r, i]) > Math.Abs(a[pivot, i])) pivot = r;
				}
				for (int c = 0; c <= k; c++)
				{
					(a[i, c], a[pivot, c]) = (a[pivot, c], a[i, c]);
				}
				for (int r = 0; r < k; r++)
				{
					if (r == i) continue;
					double m = a[r, i] / a[i, i];
					for (int c = 0; c <= k; c++)
					{
						a[r, c] -= m * a[i, c];
					}
				}
			}
			return Enumerable.Range(0, k).Select(i => a[i, k] / a[i, i]).ToArray();
		}

		// Hidden profile: what the clean rules miss for this card, per component, so that the uncut
		// card replays its own season.
		private static void SetProfiles(CardRules k, List<SeasonFit> fits, Dictionary<(char, int, int), CardRow> cards, EraWorld world)
		{
			foreach (SeasonFit fit in fits)
			{
				EraWorld.Factors f = world.Seasons[fit.Model.Season.Year];
				for (int i = 0; i < fit.Model.Batters.Length; i++)
				{
					BatterLine b = fit.Model.Batters[i];
					if (b.Replacement || !cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c)) continue;
					c.Weight = b.PA;
					BatterTalent t = fit.Model.BT[i];
					double P(CardRules.Comp comp, double refRate) => comp.Linear ? refRate - CardRules.RuleRate(comp, c.Raw)
						: Math.Clamp(refRate / CardRules.RuleRate(comp, c.Raw), 0.25, 4.0);
					c.Profile = new[] { P(k.BK, t.K * f.BK), P(k.BBB, t.BB * f.BBB), P(k.BHR, t.HR * f.BHR), P(k.BBabip, t.Babip * f.BBabip),
						P(k.BDouble, t.Double), P(k.BTriple, t.Triple), P(k.BSteal, t.Steal * f.BSteal) };
				}
				for (int i = 0; i < fit.Model.Pitchers.Length; i++)
				{
					PitcherLine p = fit.Model.Pitchers[i];
					if (p.Replacement || !cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c)) continue;
					c.Weight = p.BF;
					PitcherTalent t = fit.Model.PT[i];
					double P(CardRules.Comp comp, double refRate) => comp.Linear ? refRate - CardRules.RuleRate(comp, c.Raw)
						: Math.Clamp(refRate / CardRules.RuleRate(comp, c.Raw), 0.25, 4.0);
					c.Profile = new[] { P(k.PK, t.K * f.PK), P(k.PBB, t.BB * f.PBB), P(k.PHR, t.HR * f.PHR), P(k.PBabip, t.Babip * f.PBabip), 1.0 };
				}
			}
		}

		public static int ProfileIterations = 12;

		// Fit each card's hidden profile inside the full game engine (정신, pitch mix, fielding on),
		// with the uncut ratings, so the card itself replays its season with every in-game effect
		// working. Same scheme as the season fitter: simulate, nudge toward the record, anneal,
		// average the last third.
		private static void FitProfiles(CardRules k, List<SeasonFit> fits, Dictionary<(char, int, int), CardRow> cards, EraWorld world,
			ParkModel parks, CardData cardData, int iterations, int reps, Action<string> log)
		{
			bool savedRaw = UseRaw;
			UseRaw = true;
			Dictionary<CardRow, double[]> sum = new Dictionary<CardRow, double[]>();
			int averageFrom = iterations * 2 / 3, averaged = 0;
			for (int it = 0; it < iterations; it++)
			{
				double eta = 0.8 - 0.5 * it / Math.Max(1, iterations - 1);
				ConcurrentBag<Replay> bag = new ConcurrentBag<Replay>();
				Parallel.ForEach(fits, f => bag.Add(Run(f, k, cards, world, parks, cardData, reps, (ulong)f.Model.Season.Year * 31337 + (ulong)it, hybrid: true)));
				foreach (Replay r in bag)
				{
					League lg = r.Model.League;
					double n = r.Reps;
					for (int i = 0; i < r.Model.Batters.Length; i++)
					{
						BatterLine b = r.Model.Batters[i];
						if (b.Replacement || !cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c) || c.Profile == null) continue;
						double S(int stat) => r.Stats.Bat(i, stat) / n;
						double pa = S(BS.PA);
						if (pa < 1) continue;
						double sH = S(BS.H), sHr = S(BS.HR), sAb = S(BS.AB), sSo = S(BS.SO);
						CardRules.Comp[] bc = BatterComps(k);
						double Nb(int j, double rc, double rn, double sc, double sn, double lgRate, double kk) =>
							StepRate(bc[j], c.Raw, c.Profile[j], BatterBounds[j], rc, rn, sc, sn, lgRate, kk, eta);
						c.Profile[0] = Nb(0, b.SO, b.PA, sSo, pa, lg.K, 8);
						c.Profile[1] = Nb(1, b.BB, b.PA, S(BS.BB), pa, lg.BB, 8);
						c.Profile[2] = Nb(2, b.HR, b.PA, sHr, pa, lg.HR, 8);
						c.Profile[3] = Nb(3, b.H - b.HR, b.AB - b.SO - b.HR, sH - sHr, sAb - sSo - sHr, lg.Babip, 15);
						c.Profile[4] = Nb(4, b.D, b.H - b.HR, S(BS.D), sH - sHr, lg.Double, 5);
						c.Profile[5] = Nb(5, b.T, b.H - b.HR, S(BS.T), sH - sHr, lg.Triple, 5);
						double realOn = b.Singles + b.BB + b.HBP, simOn = sH - S(BS.D) - S(BS.T) - sHr + S(BS.BB) + S(BS.HBP);
						c.Profile[6] = Nb(6, b.SB, realOn, S(BS.SB), simOn, lg.Steal, 5);
					}
					for (int i = 0; i < r.Model.Pitchers.Length; i++)
					{
						PitcherLine p = r.Model.Pitchers[i];
						if (p.Replacement || !cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c) || c.Profile == null) continue;
						double S(int stat) => r.Stats.Pit(i, stat) / n;
						double bf = S(PS.BF);
						if (bf < 1) continue;
						CardRules.Comp[] pc = PitcherComps(k);
						double Np(int j, double rc, double rn, double sc, double sn, double lgRate, double kk) =>
							StepRate(pc[j], c.Raw, c.Profile[j], PitcherBounds[j], rc, rn, sc, sn, lgRate, kk, eta);
						c.Profile[0] = Np(0, p.SO, p.BF, S(PS.SO), bf, lg.K, 10);
						c.Profile[1] = Np(1, p.BB, p.BF, S(PS.BB), bf, lg.BB, 10);
						c.Profile[2] = Np(2, p.HR, p.BF, S(PS.HR), bf, lg.HR, 10);
						double realBip = p.BF - p.SO - p.BB - p.HBP - p.HR, simBip = bf - S(PS.SO) - S(PS.BB) - S(PS.HBP) - S(PS.HR);
						c.Profile[3] = Np(3, p.H - p.HR, realBip, S(PS.H) - S(PS.HR), simBip, lg.Babip, 20);
						// 위기관리: earned runs per out; only runners-in-scoring-position at-bats move, so step harder.
						double lgEr = 0.11;
						c.Profile[4] = Math.Clamp(Step(c.Profile[4], p.ER, p.Outs, S(PS.ER), S(PS.Outs), lgEr, 15, Math.Min(1.0, eta * 2)), 0.5, 1.6);
					}
				}
				if (it >= averageFrom)
				{
					averaged++;
					foreach (CardRow c in cards.Values)
					{
						if (c.Profile == null) continue;
						if (!sum.TryGetValue(c, out double[] acc))
						{
							acc = new double[c.Profile.Length];
							sum[c] = acc;
						}
						for (int j = 0; j < acc.Length; j++) acc[j] += c.Profile[j];
					}
				}
				log($"profile fit {it + 1}/{iterations} (step {eta:F2})");
			}
			foreach (var kv in sum)
			{
				for (int j = 0; j < kv.Value.Length; j++) kv.Key.Profile[j] = kv.Value[j] / averaged;
			}
			UseRaw = savedRaw;
		}

		// Move the card's own rate by (real / simulated)^eta and keep the result as its offset.
		private static double StepRate(CardRules.Comp comp, double[] raw, double profile, (double lo, double hi) bounds,
			double realCount, double realN, double simCount, double simN, double league, double k, double eta)
		{
			double real = (realCount + k * league) / (Math.Max(0, realN) + k);
			double sim = (simCount + k * league) / (Math.Max(0, simN) + k);
			double rate = CardRules.HybridRate(comp, raw, profile, bounds.lo, bounds.hi);
			double target = Math.Clamp(rate * Math.Pow(real / sim, eta), bounds.lo, bounds.hi);
			return comp.Linear ? target - CardRules.RuleRate(comp, raw) : Math.Clamp(target / CardRules.RuleRate(comp, raw), 0.1, 10);
		}

		private static double Step(double profile, double realCount, double realN, double simCount, double simN, double league, double k, double eta)
		{
			double real = (realCount + k * league) / (Math.Max(0, realN) + k);
			double sim = (simCount + k * league) / (Math.Max(0, simN) + k);
			return Math.Clamp(profile * Math.Pow(real / sim, eta), 0.1, 8.0);
		}

		// ---- pure cards: visible stats only ----

		// Design values the data cannot decide (per +10, reference world): 구위 and 컨트롤 add
		// strikeouts on top of 구속, 테크닉 also takes a little off balls in play.
		public static double PureStuffK = 0.02, PureControlK = 0.015, PureTechBabip = -0.005;
		// Hybrid rules: 구속 keeps this share of its strikeout slope; 컨트롤 adds ControlK.
		public static double VeloKScale = 0.65, ControlK = 0.015;
		public static bool Pure;

		// One side's scoring problem. Each card's stats are chosen so the rules reproduce its
		// in-engine rates (the hybrid fit) as closely as its few numbers allow; the rules are then
		// refit to the new stats, and both are iterated. Every stat keeps its scale: weighted mean
		// 65, best qualified season 95, floor 40.
		private sealed class Inverse
		{
			public string Side;
			public string[] CompNames;
			public int[] Stats;            // card stat index of each scored stat
			public int[,] Sign;            // [comp, stat] required sign, 0 = no effect
			public double[,] Fixed;        // design coefficients (NaN = fitted)
			public double[,] A;            // rate change per +10 (absolute)
			public double[] Base;
			public double[] Imp;
			public bool[] LogComp;         // 위기관리 is fitted in log terms
			public List<CardRow> Cards = new List<CardRow>();
			public List<double[]> Y = new List<double[]>();
			public List<double> W = new List<double>();
			public List<bool> Q = new List<bool>();
			public List<double[]> X = new List<double[]>();
			public double[] R2;
			public double[] Corr;          // new stat vs the card's own
		}

		private static Inverse BatterInverse()
		{
			Inverse v = new Inverse
			{
				Side = "B",
				CompNames = new[] { "K", "BB", "HR", "BABIP", "2B", "3B", "SB" },
				Stats = new[] { 0, 1, 2, 3 },
				Imp = new[] { 1.0, 1.0, 1.5, 1.5, 0.6, 0.4, 0.8 },
				LogComp = new bool[7]
			};
			v.Sign = new int[7, 4];
			v.Sign[0, 2] = -1; v.Sign[0, 0] = -1;   // 삼진: 선구, 컨택
			v.Sign[1, 2] = 1;                       // 볼넷: 선구
			v.Sign[2, 1] = 1; v.Sign[2, 0] = 1;     // 홈런: 파워, 컨택
			v.Sign[3, 0] = 1;                       // 인플레이 안타: 컨택
			v.Sign[4, 1] = 1;                       // 2루타 비중: 파워
			v.Sign[5, 3] = 1;                       // 3루타 비중: 주력
			v.Sign[6, 3] = 1;                       // 도루 시도: 주력
			v.Fixed = new double[7, 4];
			for (int r = 0; r < 7; r++) for (int s = 0; s < 4; s++) v.Fixed[r, s] = double.NaN;
			return v;
		}

		private static Inverse PitcherInverse()
		{
			Inverse v = new Inverse
			{
				Side = "P",
				CompNames = new[] { "K", "BB", "HR", "BABIP", "위기관리" },
				Stats = new[] { 1, 2, 3, 4, 5 },
				Imp = new[] { 1.0, 1.0, 1.0, 1.0, 1.0 },
				LogComp = new[] { false, false, false, false, true }
			};
			v.Sign = new int[5, 5];
			v.Sign[0, 0] = 1; v.Sign[0, 3] = 1; v.Sign[0, 1] = 1;   // 탈삼진: 구속, 구위, 컨트롤
			v.Sign[1, 1] = -1;                                      // 볼넷: 컨트롤
			v.Sign[2, 2] = -1;                                      // 피홈런: 테크닉
			v.Sign[3, 3] = -1; v.Sign[3, 2] = -1;                   // 인플레이 피안타: 구위, 테크닉
			v.Sign[4, 4] = -1;                                      // 위기관리 (득점권 피안타 배수): 정신
			v.Fixed = new double[5, 5];
			for (int r = 0; r < 5; r++) for (int s = 0; s < 5; s++) v.Fixed[r, s] = double.NaN;
			v.Fixed[0, 3] = PureStuffK;
			v.Fixed[0, 1] = PureControlK;
			v.Fixed[3, 2] = PureTechBabip;
			return v;
		}

		private static void Collect(Inverse v, CardRules k, Dictionary<(char, int, int), CardRow> cards)
		{
			foreach (CardRow c in cards.Values.Where(c => c.Kind == v.Side[0] && c.Profile != null).OrderBy(c => c.Year).ThenBy(c => c.Sidx))
			{
				CardRules.Comp[] comps = v.Side == "B" ? BatterComps(k) : PitcherComps(k);
				(double lo, double hi)[] bounds = v.Side == "B" ? BatterBounds : PitcherBounds;
				double[] y = new double[v.CompNames.Length];
				for (int j = 0; j < comps.Length; j++)
				{
					y[j] = CardRules.HybridRate(comps[j], c.Raw, c.Profile[j], bounds[j].lo, bounds[j].hi);
				}
				if (v.Side == "P")
				{
					y[4] = Math.Log(c.Profile[4]);
				}
				v.Cards.Add(c);
				v.Y.Add(y);
				v.W.Add(c.Weight);
				v.Q.Add(c.Share >= 0.999);
				v.X.Add(v.Stats.Select(s => CardRules.X(c.Raw[s])).ToArray());
			}
		}

		private static void Refit(Inverse v)
		{
			int nc = v.CompNames.Length, ns = v.Stats.Length, n = v.Cards.Count;
			v.A ??= new double[nc, ns];
			v.Base ??= new double[nc];
			for (int r = 0; r < nc; r++)
			{
				for (int s = 0; s < ns; s++)
				{
					if (!double.IsNaN(v.Fixed[r, s])) v.A[r, s] = v.Fixed[r, s];
				}
				List<int> free = Enumerable.Range(0, ns).Where(s => v.Sign[r, s] != 0 && double.IsNaN(v.Fixed[r, s])).ToList();
				while (true)
				{
					double[][] cols = new double[n][];
					double[] yy = new double[n];
					for (int i = 0; i < n; i++)
					{
						cols[i] = free.Select(s => v.X[i][s]).Append(1.0).ToArray();
						double y = v.Y[i][r];
						for (int s = 0; s < ns; s++)
						{
							if (!double.IsNaN(v.Fixed[r, s])) y -= v.Fixed[r, s] * v.X[i][s];
						}
						yy[i] = y;
					}
					double[] b = Wls(cols, yy, v.W.ToArray());
					int wrong = free.FindIndex(s => b[free.IndexOf(s)] * v.Sign[r, s] <= 0);
					if (wrong >= 0 && free.Count > 1)
					{
						v.A[r, free[wrong]] = 0;
						free.RemoveAt(wrong);
						continue;
					}
					for (int j = 0; j < free.Count; j++)
					{
						v.A[r, free[j]] = b[j] * v.Sign[r, free[j]] > 0 ? b[j] : 1e-6 * v.Sign[r, free[j]];
					}
					v.Base[r] = b[b.Length - 1];
					break;
				}
			}
		}

		// Each card's stats: weighted least squares on its rates, floor 40, no ceiling yet.
		private static void SolveCards(Inverse v)
		{
			int nc = v.CompNames.Length, ns = v.Stats.Length;
			double[] wc = new double[nc];
			for (int r = 0; r < nc; r++)
			{
				double sw = v.W.Sum(), m = Enumerable.Range(0, v.Cards.Count).Sum(i => v.Y[i][r] * v.W[i]) / sw;
				double var = Enumerable.Range(0, v.Cards.Count).Sum(i => v.W[i] * (v.Y[i][r] - m) * (v.Y[i][r] - m)) / sw;
				wc[r] = v.Imp[r] / Math.Max(1e-12, var);
			}
			for (int i = 0; i < v.Cards.Count; i++)
			{
				double[] x = v.X[i], y = v.Y[i];
				for (int sweep = 0; sweep < 40; sweep++)
				{
					for (int s = 0; s < ns; s++)
					{
						double num = 0, den = 0;
						for (int r = 0; r < nc; r++)
						{
							double a = v.A[r, s];
							if (a == 0) continue;
							double pred = v.Base[r];
							for (int t = 0; t < ns; t++)
							{
								if (t != s) pred += v.A[r, t] * x[t];
							}
							num += wc[r] * a * (y[r] - pred);
							den += wc[r] * a * a;
						}
						if (den > 0) x[s] = Math.Clamp(num / den, CardRules.XLow, 8);
					}
				}
			}
		}

		// Keep each stat's scale: weighted mean 65 (x 0), best qualified season 95 (x 3).
		private static void Normalise(Inverse v)
		{
			int nc = v.CompNames.Length, ns = v.Stats.Length, n = v.Cards.Count;
			double sw = v.W.Sum();
			for (int s = 0; s < ns; s++)
			{
				double m = Enumerable.Range(0, n).Sum(i => v.X[i][s] * v.W[i]) / sw;
				double top = Enumerable.Range(0, n).Where(i => v.Q[i]).Max(i => v.X[i][s]);
				if (top - m < 0.3) continue;
				double k = 3 / (top - m);
				for (int i = 0; i < n; i++)
				{
					v.X[i][s] = Math.Max(CardRules.XLow, (v.X[i][s] - m) * k);
				}
				for (int r = 0; r < nc; r++)
				{
					v.Base[r] += v.A[r, s] * m;
					v.A[r, s] /= k;
				}
			}
		}

		private static void ScoreFit(Inverse v)
		{
			int nc = v.CompNames.Length, ns = v.Stats.Length, n = v.Cards.Count;
			double sw = v.W.Sum();
			v.R2 = new double[nc];
			for (int r = 0; r < nc; r++)
			{
				double m = Enumerable.Range(0, n).Sum(i => v.Y[i][r] * v.W[i]) / sw, ss = 0, se = 0;
				for (int i = 0; i < n; i++)
				{
					double pred = v.Base[r];
					for (int s = 0; s < ns; s++) pred += v.A[r, s] * Math.Min(3, v.X[i][s]);
					ss += v.W[i] * (v.Y[i][r] - m) * (v.Y[i][r] - m);
					se += v.W[i] * (v.Y[i][r] - pred) * (v.Y[i][r] - pred);
				}
				v.R2[r] = 1 - se / Math.Max(1e-12, ss);
			}
			v.Corr = new double[ns];
			for (int s = 0; s < ns; s++)
			{
				double[] a = v.X.Select(x => Math.Min(3, x[s])).ToArray(), b = v.Cards.Select(c => CardRules.X(c.Raw[v.Stats[s]])).ToArray();
				double ma = Enumerable.Range(0, n).Sum(i => a[i] * v.W[i]) / sw, mb = Enumerable.Range(0, n).Sum(i => b[i] * v.W[i]) / sw;
				double sab = 0, saa = 0, sbb = 0;
				for (int i = 0; i < n; i++)
				{
					sab += v.W[i] * (a[i] - ma) * (b[i] - mb); saa += v.W[i] * (a[i] - ma) * (a[i] - ma); sbb += v.W[i] * (b[i] - mb) * (b[i] - mb);
				}
				v.Corr[s] = sab / Math.Sqrt(Math.Max(1e-12, saa * sbb));
			}
		}

		private static void Iterate(Inverse v, int rounds)
		{
			Refit(v);
			for (int it = 0; it < rounds; it++)
			{
				SolveCards(v);
				Normalise(v);
				Refit(v);
			}
			ScoreFit(v);
		}

		// Pure cards from the inverse scoring: new ratings, no hidden profile (위기관리 from 정신).
		private static (CardRules rules, Dictionary<(char, int, int), CardRow> cards) PureInverse(CardRules hybrid, Dictionary<(char, int, int), CardRow> cards,
			int rounds, Action<string> log, out Inverse bat, out Inverse pit)
		{
			bat = BatterInverse();
			pit = PitcherInverse();
			Collect(bat, hybrid, cards);
			Collect(pit, hybrid, cards);
			Iterate(bat, rounds);
			Iterate(pit, rounds);
			CardRules k = new CardRules { SpeedPer10 = hybrid.SpeedPer10, ErrorPer10 = hybrid.ErrorPer10 };
			void Rules(Inverse v, CardRules.Comp[] comps, string[] names)
			{
				for (int r = 0; r < comps.Length; r++)
				{
					List<int> used = Enumerable.Range(0, v.Stats.Length).Where(s => v.A[r, s] != 0).ToList();
					comps[r].Linear = true;
					comps[r].Base = v.Base[r];
					comps[r].Stats = used.Select(s => v.Stats[s]).ToArray();
					comps[r].StatNames = comps[r].Stats.Select(i => names[i]).ToArray();
					comps[r].Beta = used.Select(s => v.A[r, s] / v.Base[r]).ToArray();
					comps[r].Gamma = new double[used.Count];
				}
			}
			Rules(bat, BatterComps(k), CardRules.BatterStats);
			Rules(pit, PitcherComps(k), CardRules.PitcherStats);
			k.RispBase = pit.Base[4];
			k.RispPer10 = pit.A[4, 4];
			Dictionary<(char, int, int), CardRow> result = cards.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
			void Apply(Inverse v)
			{
				for (int i = 0; i < v.Cards.Count; i++)
				{
					CardRow c = result[(v.Cards[i].Kind, v.Cards[i].Sidx, v.Cards[i].Year)];
					for (int s = 0; s < v.Stats.Length; s++)
					{
						c.Raw[v.Stats[s]] = Math.Clamp(65 + 10 * v.X[i][s], 40, 95);
					}
				}
			}
			Apply(bat);
			Apply(pit);
			foreach (CardRow c in result.Values)
			{
				Recut(c, CutPower ?? 1.0);
			}
			ZeroProfiles(k, result);
			Inverse b = bat, p = pit;
			log($"pure scoring: batter R² " + string.Join(" ", b.CompNames.Select((nm, r) => $"{nm} {b.R2[r]:F2}"))
				+ " | pitcher R² " + string.Join(" ", p.CompNames.Select((nm, r) => $"{nm} {p.R2[r]:F2}")));
			return (k, result);
		}

		// No hidden profile: offsets 0; a pitcher's 위기관리 comes from his 정신 through the rule.
		private static void ZeroProfiles(CardRules k, Dictionary<(char, int, int), CardRow> cards)
		{
			foreach (CardRow c in cards.Values)
			{
				if (c.Kind == 'B')
				{
					c.Profile = new double[7];
				}
				else
				{
					double[] r = c.Raw;
					c.Profile = new double[] { 0, 0, 0, 0, Math.Clamp(Math.Exp(k.RispBase + k.RispPer10 * FieldRules.X(CardRules.X(r[5]))), 0.5, 1.6) };
				}
			}
		}

		// 정신 → 위기관리 for the current cards: regress the hybrid 위기관리 on 정신 (log terms).
		private static (double a, double b, double r2) RispFromMental(Dictionary<(char, int, int), CardRow> cards)
		{
			var rows = cards.Values.Where(c => c.Kind == 'P' && c.Profile != null && c.Profile.Length > 4).ToList();
			double[][] X = rows.Select(c => new[] { CardRules.X(c.Raw[5]), 1.0 }).ToArray();
			double[] y = rows.Select(c => Math.Log(c.Profile[4])).ToArray(), w = rows.Select(c => c.Weight).ToArray();
			double[] b = Wls(X, y, w);
			double sw = w.Sum(), my = y.Select((v, i) => v * w[i]).Sum() / sw;
			double ss = y.Select((v, i) => w[i] * (v - my) * (v - my)).Sum(), se = y.Select((v, i) => w[i] * Math.Pow(v - b[1] - b[0] * X[i][0], 2)).Sum();
			return (b[1], b[0], 1 - se / ss);
		}

		// ---- replay ----

		private sealed class Replay
		{
			public SeasonFit Fit;
			public SeasonModel Model;
			public SimStats Stats;
			public int Reps;
		}

		// Game-league environment: multipliers on every strikeout, walk, homer and in-play hit rate
		// (null = the seasons as they were).
		public sealed class EnvMults
		{
			public double K = 1, BB = 1, HR = 1, Babip = 1;
		}

		public static EnvMults Env;
		// Development test: the game-league centre (batting average, ERA) to tune to (null = best fit to the bands).
		public static (double avg, double era)? EnvTarget;

		// Fielding and running rules of card mode (null = off).
		public static FieldRules Field = new FieldRules();

		private static Replay Run(SeasonFit fit, CardRules k, Dictionary<(char, int, int), CardRow> cards, EraWorld world, ParkModel parks, CardData cardData, int reps, ulong seed, bool hybrid = false, bool pure = false)
		{
			Season s = fit.Model.Season;
			SeasonModel m = SeasonModel.Build(s, parks, cardData, new MatchupParams());
			EraWorld.Factors f = world.Seasons[s.Year];
			int nb = m.Batters.Length, np = m.Pitchers.Length;
			m.Clutch = UseClutch;
			m.BatMental = new double[nb];
			m.PitMental = new double[np];
			m.BatT = Enumerable.Range(0, 4).Select(_ => new double[nb]).ToArray();
			m.PitT = Enumerable.Range(0, 4).Select(_ => new double[np]).ToArray();
			m.PitRisp = hybrid ? Enumerable.Repeat(1.0, np).ToArray() : null;
			if (Field != null)
			{
				m.Field = Field;
				m.Defs = new double[nb][];
				m.IsCatcher = new bool[nb];
				m.CatcherShown = Enumerable.Repeat(double.NaN, nb).ToArray();
				m.RunX = new double[nb];
				m.VeloX = new double[np];
				m.PitCT = Enumerable.Range(0, 4).Select(_ => new double[np]).ToArray();
			}
			double[] defSum = new double[s.Teams.Count], defW = new double[s.Teams.Count];
			for (int i = 0; i < nb; i++)
			{
				BatterLine b = m.Batters[i];
				if (b.Replacement || !cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c))
				{
					// Unrecorded players (the replacement lines) keep what the season fit gave them.
					m.BT[i] = fit.Model.BT[i];
					m.BatMental[i] = 65;
					if (m.Defs != null)
					{
						m.Defs[i] = new double[] { 30, 50, 50, 50, 50, 50, 50, 50 };
						m.RunX[i] = Math.Clamp(Math.Log(Math.Max(0.5, fit.Model.BT[i].Speed)) / k.SpeedPer10, -2.5, 3);
					}
					continue;
				}
				m.BT[i] = BatterFrom(k, c, f, m.League, hybrid);
				if (m.Defs != null)
				{
					m.Defs[i] = UseRaw ? c.RawDefs : c.Defs;
					m.CatcherShown[i] = UseRaw ? c.ShownCatcher : double.NaN;
					m.IsCatcher[i] = c.MainPos == 1;
					m.RunX[i] = CardRules.X(Ratings(c)[3]);
				}
				if (hybrid && b.PA >= 400)
				{
					BatterTalent a = fit.Model.BT[i], z = m.BT[i];
					Debug.Add(string.Join(",", "B", b.Year, b.Name, b.PA, c.Share.ToString("F2", Inv), a.K.ToString("F4", Inv), z.K.ToString("F4", Inv), a.BB.ToString("F4", Inv), z.BB.ToString("F4", Inv),
						a.HR.ToString("F4", Inv), z.HR.ToString("F4", Inv), a.Babip.ToString("F4", Inv), z.Babip.ToString("F4", Inv), string.Join(" ", c.R.Select(v => v.ToString(Inv))), string.Join(" ", c.Raw.Select(v => v.ToString("F1", Inv))),
						string.Join(" ", (c.Profile ?? new double[0]).Select(v => v.ToString("F3", Inv)))));
				}
				if (hybrid && !pure)
				{
					// Bunting habit and the part of baserunning 주력 does not explain stay with the card.
					m.BT[i].Sac = fit.Model.BT[i].Sac;
					m.BT[i].Speed = Math.Clamp(fit.Model.BT[i].Speed, 0.7, 1.35);
				}
				else if (pure)
				{
					// Visible stats only: baserunning from 주력, bunting at the league habit.
					m.BT[i].Sac = m.League.Sac * 0.7;
					m.BT[i].Speed = Math.Clamp(Math.Exp(k.SpeedPer10 * CardRules.X(Ratings(c)[3])), 0.7, 1.35);
				}
				m.BatMental[i] = Ratings(c)[4];
				if (hybrid && c.Profile != null)
				{
					f = world.Seasons[s.Year];
					m.BatT[0][i] = CardRules.HybridPerPercent(k.BK, Ratings(c), m.BT[i].K * f.BK);
					m.BatT[1][i] = CardRules.HybridPerPercent(k.BBB, Ratings(c), m.BT[i].BB * f.BBB);
					m.BatT[2][i] = CardRules.HybridPerPercent(k.BHR, Ratings(c), m.BT[i].HR * f.BHR);
					m.BatT[3][i] = CardRules.HybridPerPercent(k.BBabip, Ratings(c), m.BT[i].Babip * f.BBabip);
				}
				else
				{
					m.BatT[0][i] = CardRules.PerPercent(k.BK, Ratings(c));
					m.BatT[1][i] = CardRules.PerPercent(k.BBB, Ratings(c));
					m.BatT[2][i] = CardRules.PerPercent(k.BHR, Ratings(c));
					m.BatT[3][i] = CardRules.PerPercent(k.BBabip, Ratings(c));
				}
				if (c.MainPos != 10)
				{
					defSum[m.BatterTeam[i]] += Ratings(c)[5] * b.PA;
					defW[m.BatterTeam[i]] += b.PA;
				}
			}
			for (int t = 0; t < s.Teams.Count && UseCardDefence; t++)
			{
				m.ErrorMult[t] = defW[t] > 0 ? Math.Exp(k.ErrorPer10 * CardRules.X(defSum[t] / defW[t])) : 1.0;
			}
			for (int i = 0; i < np; i++)
			{
				PitcherLine p = m.Pitchers[i];
				if (p.Replacement || !cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c))
				{
					m.PT[i] = fit.Model.PT[i];
					m.PitMental[i] = 65;
					continue;
				}
				m.PT[i] = PitcherFrom(k, c, f, m.League, hybrid);
				if (hybrid && c.Profile != null && c.Profile.Length > 4)
				{
					m.PitRisp[i] = pure ? Math.Clamp(Math.Exp(k.RispBase + k.RispPer10 * FieldRules.X(CardRules.X(Ratings(c)[5]))), 0.5, 1.6) : c.Profile[4];
				}
				if (m.Defs != null)
				{
					double[] rr = Ratings(c);
					m.VeloX[i] = CardRules.X(rr[1]);
					// The catcher's boost: how one percent on 컨트롤·테크닉·구위 moves each rate.
					Func<int, bool> boosted = st => st >= 2 && st <= 4;
					CardRules.Comp[] pc = PitcherComps(k);
					for (int j = 0; j < 4; j++)
					{
						m.PitCT[j][i] = hybrid && c.Profile != null
							? CardRules.HybridPerPercent(pc[j], rr, j switch { 0 => m.PT[i].K * f.PK, 1 => m.PT[i].BB * f.PBB, 2 => m.PT[i].HR * f.PHR, _ => m.PT[i].Babip * f.PBabip }, boosted)
							: CardRules.PerPercent(pc[j], rr, boosted);
					}
				}
				if (hybrid && p.Outs >= 300)
				{
					PitcherTalent a = fit.Model.PT[i], z = m.PT[i];
					Debug.Add(string.Join(",", "P", p.Year, p.Name, p.Outs / 3, c.Share.ToString("F2", Inv), a.K.ToString("F4", Inv), z.K.ToString("F4", Inv), a.BB.ToString("F4", Inv), z.BB.ToString("F4", Inv),
						a.HR.ToString("F4", Inv), z.HR.ToString("F4", Inv), a.Babip.ToString("F4", Inv), z.Babip.ToString("F4", Inv), string.Join(" ", c.R.Select(v => v.ToString(Inv))), string.Join(" ", c.Raw.Select(v => v.ToString("F1", Inv))),
						string.Join(" ", (c.Profile ?? new double[0]).Select(v => v.ToString("F3", Inv)))));
				}
				m.PitMental[i] = Ratings(c)[5];
				// The boost works on the pitcher's own rates (more K, fewer walks, homers and hits).
				if (hybrid && c.Profile != null)
				{
					m.PitT[0][i] = CardRules.HybridPerPercent(k.PK, Ratings(c), m.PT[i].K * f.PK);
					m.PitT[1][i] = CardRules.HybridPerPercent(k.PBB, Ratings(c), m.PT[i].BB * f.PBB);
					m.PitT[2][i] = CardRules.HybridPerPercent(k.PHR, Ratings(c), m.PT[i].HR * f.PHR);
					m.PitT[3][i] = CardRules.HybridPerPercent(k.PBabip, Ratings(c), m.PT[i].Babip * f.PBabip);
				}
				else
				{
					m.PitT[0][i] = CardRules.PerPercent(k.PK, Ratings(c));
					m.PitT[1][i] = CardRules.PerPercent(k.PBB, Ratings(c));
					m.PitT[2][i] = CardRules.PerPercent(k.PHR, Ratings(c));
					m.PitT[3][i] = CardRules.PerPercent(k.PBabip, Ratings(c));
				}
				if (UseCardPitches)
				{
					m.Mix[i] = c.Pitches.Count > 0 ? PitchMix.Build(SlotsFrom(c), m.MP) : null;
				}
			}
			if (Env != null)
			{
				// Game-league environment: every rate (both sides and the league) scaled alike. The
				// talents are copied first: replacement players share the season fit's objects.
				for (int i = 0; i < nb; i++)
				{
					BatterTalent t = m.BT[i];
					m.BT[i] = new BatterTalent { K = t.K * Env.K, BB = t.BB * Env.BB, HBP = t.HBP, HR = t.HR * Env.HR, Babip = t.Babip * Env.Babip,
						Double = t.Double, Triple = t.Triple, Steal = t.Steal, Sac = t.Sac, Speed = t.Speed };
				}
				for (int i = 0; i < np; i++)
				{
					PitcherTalent t = m.PT[i];
					m.PT[i] = new PitcherTalent { K = t.K * Env.K, BB = t.BB * Env.BB, HBP = t.HBP, HR = t.HR * Env.HR, Babip = t.Babip * Env.Babip };
				}
				m.League.K *= Env.K; m.League.BB *= Env.BB; m.League.HR *= Env.HR; m.League.Babip *= Env.Babip;
			}
			SimStats st = Fitter.Run(m, fit.Params, reps, seed);
			return new Replay { Fit = fit, Model = m, Stats = st, Reps = reps };
		}

		// ---- calibration ----

		private static void Tune(CardRules k, List<Replay> replays, double eta, Func<char, int, int, bool> full)
		{
			// (real count, real denominator, sim count, sim denominator) per qualified player, per component.
			void Adjust(CardRules.Comp comp, IEnumerable<(double rc, double rn, double sc, double sn, double lg, bool q)> rows)
			{
				// Level from full (uncut) cards only: cut bench cards are weaker than their records by
				// design and must not pull the regulars up.
				var all = rows.Where(x => x.rn > 0 && x.sn > 0 && x.q).ToList();
				double level = all.Sum(x => x.rc) / Math.Max(1e-9, all.Sum(x => x.sc * x.rn / x.sn));
				var q = all.Where(x => x.q && x.rc > 0 && x.sc > 0).ToList();
				double mx = 0, my = 0, w = 0;
				foreach (var x in q)
				{
					double xs = Math.Log(x.sc / x.sn / x.lg), ys = Math.Log(x.rc / x.rn / x.lg);
					mx += xs * x.rn; my += ys * x.rn; w += x.rn;
				}
				mx /= w; my /= w;
				double sxy = 0, sxx = 0;
				foreach (var x in q)
				{
					double xs = Math.Log(x.sc / x.sn / x.lg) - mx, ys = Math.Log(x.rc / x.rn / x.lg) - my;
					sxy += x.rn * xs * ys; sxx += x.rn * xs * xs;
				}
				// Only the level is tuned: the spread between players comes from the regression on
				// fitted talents, which already reproduce each player's season.
				comp.Base *= Math.Pow(level, eta);
			}
			var b = new List<(BatterLine line, double[] s, League lg, bool q)>();
			var p = new List<(PitcherLine line, double[] s, League lg, bool q)>();
			foreach (Replay r in replays)
			{
				int games = r.Model.Season.Games;
				for (int i = 0; i < r.Model.Batters.Length; i++)
				{
					BatterLine x = r.Model.Batters[i];
					if (x.Replacement) continue;
					double[] s = Enumerable.Range(0, BS.Count).Select(j => r.Stats.Bat(i, j) / (double)r.Reps).ToArray();
					b.Add((x, s, r.Model.League, full('B', x.Sidx, x.Year)));
				}
				for (int i = 0; i < r.Model.Pitchers.Length; i++)
				{
					PitcherLine x = r.Model.Pitchers[i];
					if (x.Replacement) continue;
					double[] s = Enumerable.Range(0, PS.Count).Select(j => r.Stats.Pit(i, j) / (double)r.Reps).ToArray();
					p.Add((x, s, r.Model.League, full('P', x.Sidx, x.Year)));
				}
			}
			Adjust(k.BK, b.Select(x => ((double)x.line.SO, (double)x.line.PA, x.s[BS.SO], x.s[BS.PA], x.lg.K, x.q)));
			Adjust(k.BBB, b.Select(x => ((double)x.line.BB, (double)x.line.PA, x.s[BS.BB], x.s[BS.PA], x.lg.BB, x.q)));
			Adjust(k.BHR, b.Select(x => ((double)x.line.HR, (double)x.line.PA, x.s[BS.HR], x.s[BS.PA], x.lg.HR, x.q)));
			Adjust(k.BBabip, b.Select(x => ((double)(x.line.H - x.line.HR), (double)(x.line.AB - x.line.SO - x.line.HR), x.s[BS.H] - x.s[BS.HR], x.s[BS.AB] - x.s[BS.SO] - x.s[BS.HR], x.lg.Babip, x.q)));
			Adjust(k.BDouble, b.Select(x => ((double)x.line.D, (double)(x.line.H - x.line.HR), x.s[BS.D], x.s[BS.H] - x.s[BS.HR], x.lg.Double, x.q)));
			Adjust(k.BTriple, b.Select(x => ((double)x.line.T, (double)(x.line.H - x.line.HR), x.s[BS.T], x.s[BS.H] - x.s[BS.HR], x.lg.Triple, x.q)));
			Adjust(k.BSteal, b.Select(x => ((double)x.line.SB, (double)(x.line.Singles + x.line.BB + x.line.HBP), x.s[BS.SB], x.s[BS.H] - x.s[BS.D] - x.s[BS.T] - x.s[BS.HR] + x.s[BS.BB] + x.s[BS.HBP], x.lg.Steal, x.q)));
			Adjust(k.PK, p.Select(x => ((double)x.line.SO, (double)x.line.BF, x.s[PS.SO], x.s[PS.BF], x.lg.K, x.q)));
			Adjust(k.PBB, p.Select(x => ((double)x.line.BB, (double)x.line.BF, x.s[PS.BB], x.s[PS.BF], x.lg.BB, x.q)));
			Adjust(k.PHR, p.Select(x => ((double)x.line.HR, (double)x.line.BF, x.s[PS.HR], x.s[PS.BF], x.lg.HR, x.q)));
			Adjust(k.PBabip, p.Select(x => ((double)(x.line.H - x.line.HR), (double)(x.line.BF - x.line.SO - x.line.BB - x.line.HBP - x.line.HR),
				x.s[PS.H] - x.s[PS.HR], x.s[PS.BF] - x.s[PS.SO] - x.s[PS.BB] - x.s[PS.HBP] - x.s[PS.HR], x.lg.Babip, x.q)));
		}

		// ---- scores ----

		public sealed class Score
		{
			public double Avg, Obp, Slg, Hr, KPct, BbPct, Sb, Era, K9, Bb9, Hr9, LeagueAvg, LeagueEra, LeagueRpg;
			public int Batters, Pitchers;
		}

		private static Score Evaluate(IEnumerable<(SeasonModel m, SimStats st, int reps)> runs, Func<PitcherLine, bool> keepPitcher = null)
		{
			Score sc = new Score();
			double lAvg = 0, lEra = 0, lRpg = 0;
			int seasons = 0;
			foreach (var (m, st, reps) in runs)
			{
				seasons++;
				int games = m.Season.Games;
				double rAb = 0, rH = 0, sAb = 0, sH = 0;
				for (int i = 0; i < m.Batters.Length; i++)
				{
					BatterLine b = m.Batters[i];
					if (b.Replacement) continue;
					double pa = st.Bat(i, BS.PA) / (double)reps, ab = st.Bat(i, BS.AB) / (double)reps, h = st.Bat(i, BS.H) / (double)reps;
					rAb += b.AB; rH += b.H; sAb += ab; sH += h;
					if (b.PA < 3.1 * games || pa < 1) continue;
					double d = st.Bat(i, BS.D) / (double)reps, t = st.Bat(i, BS.T) / (double)reps, hr = st.Bat(i, BS.HR) / (double)reps;
					double bb = st.Bat(i, BS.BB) / (double)reps, hbp = st.Bat(i, BS.HBP) / (double)reps, so = st.Bat(i, BS.SO) / (double)reps, sb = st.Bat(i, BS.SB) / (double)reps;
					sc.Batters++;
					sc.Avg += Math.Abs(h / ab - b.H / (double)b.AB);
					sc.Obp += Math.Abs((h + bb + hbp) / pa - (b.H + b.BB + b.HBP) / (double)b.PA);
					sc.Slg += Math.Abs((h + d + 2 * t + 3 * hr) / ab - (b.H + b.D + 2 * b.T + 3 * b.HR) / (double)b.AB);
					sc.Hr += Math.Abs(hr / pa * b.PA - b.HR);
					sc.KPct += Math.Abs(so / pa - b.SO / (double)b.PA);
					sc.BbPct += Math.Abs(bb / pa - b.BB / (double)b.PA);
					sc.Sb += Math.Abs(sb / pa * b.PA - b.SB);
				}
				double rEr = 0, rOuts = 0, sEr = 0, sOuts = 0;
				for (int i = 0; i < m.Pitchers.Length; i++)
				{
					PitcherLine p = m.Pitchers[i];
					if (p.Replacement) continue;
					double outs = st.Pit(i, PS.Outs) / (double)reps, er = st.Pit(i, PS.ER) / (double)reps;
					rEr += p.ER; rOuts += p.Outs; sEr += er; sOuts += outs;
					if (p.Outs < 240 || outs < 3 || (keepPitcher != null && !keepPitcher(p))) continue;
					double so = st.Pit(i, PS.SO) / (double)reps, bb = st.Pit(i, PS.BB) / (double)reps, hr = st.Pit(i, PS.HR) / (double)reps;
					sc.Pitchers++;
					sc.Era += Math.Abs(er * 27 / outs - p.ER * 27.0 / p.Outs);
					sc.K9 += Math.Abs(so * 27 / outs - p.SO * 27.0 / p.Outs);
					sc.Bb9 += Math.Abs(bb * 27 / outs - p.BB * 27.0 / p.Outs);
					sc.Hr9 += Math.Abs(hr * 27 / outs - p.HR * 27.0 / p.Outs);
				}
				lAvg += Math.Abs(sH / sAb - rH / rAb);
				lEra += Math.Abs(sEr * 27 / sOuts - rEr * 27 / rOuts);
				double realRpg = Fitter.RealRunsPerPa(m) * Fitter.TotalPa(m, st) / (st.Games * 2.0);
				lRpg += Math.Abs(st.Runs / (double)(st.Games * 2) - realRpg);
			}
			int nb = Math.Max(1, sc.Batters), np = Math.Max(1, sc.Pitchers);
			sc.Avg /= nb; sc.Obp /= nb; sc.Slg /= nb; sc.Hr /= nb; sc.KPct /= nb; sc.BbPct /= nb; sc.Sb /= nb;
			sc.Era /= np; sc.K9 /= np; sc.Bb9 /= np; sc.Hr9 /= np;
			sc.LeagueAvg = lAvg / seasons; sc.LeagueEra = lEra / seasons; sc.LeagueRpg = lRpg / seasons;
			return sc;
		}

		// ---- driver ----

		public static void RunAll(List<Season> seasons, ParkModel parks, CardData cardData, string ratingsPath, string outDir, int rounds, int reps, Action<string> log)
		{
			Dictionary<(char, int, int), CardRow> cards = LoadRatings(ratingsPath);
			log($"card ratings: {cards.Count} player-seasons");
			EraWorld world = EraWorld.Build(seasons);

			// Fitted talents and run environment per season (the reference the cards came from).
			ConcurrentBag<SeasonFit> bag = new ConcurrentBag<SeasonFit>();
			Parallel.ForEach(seasons, s => bag.Add(Fitter.Fit(s, new EngineParams(), 30, 8, reps, (ulong)s.Year * 7919, parks, cardData, new MatchupParams())));
			List<SeasonFit> fits = bag.OrderBy(f => f.Model.Season.Year).ToList();
			// Pitchers whose cards were cut for short seasons are judged separately.
			bool Full(PitcherLine p) => cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c) && c.Share >= 0.999;
			Score baseline = Evaluate(fits.Select(f => (f.Model, f.Validation, f.ValidationReps)), Full);
			log($"talent baseline: AVG {baseline.Avg:F4} HR {baseline.Hr:F2} K% {baseline.KPct:F4} | ERA {baseline.Era:F3} K9 {baseline.K9:F2}");

			CardRules rules = new CardRules();
			FitInitial(rules, fits, cards, world);
			List<(int round, Score score)> history = new List<(int, Score)>();
			List<Replay> last = null;
			for (int round = 0; round <= rounds; round++)
			{
				ConcurrentBag<Replay> runs = new ConcurrentBag<Replay>();
				Parallel.ForEach(fits, f => runs.Add(Run(f, rules, cards, world, parks, cardData, reps, (ulong)f.Model.Season.Year * 104729 + (ulong)round)));
				last = runs.OrderBy(r => r.Model.Season.Year).ToList();
				Score sc = Evaluate(last.Select(r => (r.Model, r.Stats, r.Reps)), Full);
				history.Add((round, sc));
				log($"round {round}: AVG {sc.Avg:F4} OBP {sc.Obp:F4} SLG {sc.Slg:F4} HR {sc.Hr:F2} K% {sc.KPct:F4} BB% {sc.BbPct:F4} SB {sc.Sb:F2} | ERA {sc.Era:F3} K9 {sc.K9:F2} BB9 {sc.Bb9:F2} HR9 {sc.Hr9:F2} | league AVG {sc.LeagueAvg:F4} ERA {sc.LeagueEra:F3} R/G {sc.LeagueRpg:F3}");
				if (round < rounds)
				{
					Tune(rules, last, 0.5, (kind, sidx, year) => cards.TryGetValue((kind, sidx, year), out CardRow c) && c.Share >= 0.999); // both sides move: half each
				}
			}
			// Hybrid: clean, monotone rules plus each card's hidden profile.
			CardRules clean;
			if (SlopesPath != null && File.Exists(SlopesPath))
			{
				clean = DefinitionalRules(SlopesPath, fits, cards, world);
				log($"rules: read off the card scales ({SlopesPath})");
			}
			else
			{
				clean = CleanRules();
				FitInitial(clean, fits, cards, world, clean: true);
			}
			SetProfiles(clean, fits, cards, world);
			if (ProfileIterations > 0)
			{
				FitProfiles(clean, fits, cards, world, parks, cardData, ProfileIterations, Math.Max(6, reps / 3), log);
			}
			// What the game server plays with: the rules, every card's hidden profile, and the
			// reference world (league rates and base-running constants) the cards were scaled to.
			Directory.CreateDirectory(outDir);
			WriteRules(Path.Combine(outDir, "card_rules_clean.json"), clean);
			WriteProfiles(Path.Combine(outDir, "card_profiles.csv"), cards);
			CardGame.WriteEnv(Path.Combine(outDir, "game_env.json"), clean, fits, world);
			if (DevTest)
			{
				DevelopmentTest(fits, clean, cards, world, parks, cardData, reps, outDir, log);
				return;
			}
			List<Replay> Replays(CardRules rules, Dictionary<(char, int, int), CardRow> deck, bool raw, bool pure, ulong salt)
			{
				bool saved = UseRaw;
				UseRaw = raw;
				ConcurrentBag<Replay> b = new ConcurrentBag<Replay>();
				Parallel.ForEach(fits, f => b.Add(Run(f, rules, deck, world, parks, cardData, reps, (ulong)f.Model.Season.Year * salt, hybrid: true, pure: pure)));
				UseRaw = saved;
				return b.OrderBy(r => r.Model.Season.Year).ToList();
			}
			Score Log(string label, List<Replay> runs)
			{
				Score x = Evaluate(runs.Select(r => (r.Model, r.Stats, r.Reps)), Full);
				log($"{label}: AVG {x.Avg:F4} OBP {x.Obp:F4} SLG {x.Slg:F4} HR {x.Hr:F2} K% {x.KPct:F4} BB% {x.BbPct:F4} SB {x.Sb:F2} | ERA {x.Era:F3} K9 {x.K9:F2} BB9 {x.Bb9:F2} HR9 {x.Hr9:F2} | league AVG {x.LeagueAvg:F4} ERA {x.LeagueEra:F3} R/G {x.LeagueRpg:F3}");
				return x;
			}
			// Hybrid, uncut ratings (the rules and profiles themselves), then the real cut cards.
			List<Replay> hruns = Replays(clean, cards, true, false, 7777);
			Score hsc = Log("hybrid", hruns);
			List<Replay> hcut = Replays(clean, cards, false, false, 7778);
			Score hcutScore = Log("hybrid, cut cards", hcut);
			List<string[]> hBands = Bands(hcut, cards);
			foreach (string[] row in hBands) log("  cut " + string.Join(" ", row));
			var variants = new List<(string label, Score score)> { ("숨은 성향 (하이브리드)", hsc) };
			List<Replay> pinv = null, pinvCut = null;
			List<string[]> pBands = null;
			Inverse ib = null, ip = null;
			CardRules pk = null;
			(double a, double b, double r2) risp = (0, 0, 0);
			if (Pure)
			{
				// Current cards, rules only (no hidden profile); 위기관리 from 정신.
				risp = RispFromMental(cards);
				log($"정신 → 위기관리: exp({risp.a:F3} {risp.b:+0.000;-0.000}·x), R² {risp.r2:F2}");
				clean.RispBase = risp.a;
				clean.RispPer10 = risp.b;
				Dictionary<(char, int, int), CardRow> plain = cards.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
				ZeroProfiles(clean, plain);
				variants.Add(("지금 카드 + 규칙만", Log("pure, current cards", Replays(clean, plain, true, true, 7779))));
				// Cards re-scored by inverting the rules.
				(pk, Dictionary<(char, int, int), CardRow> deck) = PureInverse(clean, cards, 40, log, out ib, out ip);
				pinv = Replays(pk, deck, true, true, 7780);
				variants.Add(("역산 카드 + 규칙만", Log("pure, inverse cards", pinv)));
				pinvCut = Replays(pk, deck, false, true, 7781);
				Log("pure, inverse cards, cut", pinvCut);
				pBands = Bands(pinvCut, deck);
				foreach (string[] row in pBands) log("  cut " + string.Join(" ", row));
				WriteInverse(Path.Combine(outDir, "card_inverse.csv"), ib, ip);
				WriteRules(Path.Combine(outDir, "card_rules_pure.json"), pk);
			}

			Directory.CreateDirectory(outDir);
			WriteRules(Path.Combine(outDir, "card_rules.json"), rules);
			WriteRules(Path.Combine(outDir, "card_rules_clean.json"), clean);
			WriteProfiles(Path.Combine(outDir, "card_profiles.csv"), cards);
			WriteReport(Path.Combine(outDir, "cardtest.html"), rules, history, baseline, last, cards);
			WriteReport(Path.Combine(outDir, "cardtest_hybrid.html"), clean, new List<(int, Score)> { (0, hsc) }, baseline, hruns, cards);
			variants.Insert(0, ("지금 카드 + 자유 회귀 규칙", history[0].score));
			WritePureReport(Path.Combine(outDir, "cardtest_pure.html"), baseline, variants, hBands, pBands, ib, ip, pk, risp, hruns, pinv, cards);
			WritePlayers(Path.Combine(outDir, "cardtest_players.csv"), last, cards);
			WritePlayers(Path.Combine(outDir, "cardtest_hybrid_players.csv"), hruns, cards);
			File.WriteAllLines(Path.Combine(outDir, "hybrid_debug.csv"), new[] { "kind,year,name,pa,share,fitK,cardK,fitBB,cardBB,fitHR,cardHR,fitBabip,cardBabip,ratings,raw,profile" }.Concat(Debug.OrderBy(x => x)), new UTF8Encoding(true));
		}

		// Card replay against the record by playing-time share (the short-season cut's effect).
		private static List<string[]> Bands(List<Replay> runs, Dictionary<(char, int, int), CardRow> cards)
		{
			(string label, double lo, double hi)[] bb = { ("타자 규정 이상", 0.999, 9), ("타자 75~99%", 0.75, 0.999), ("타자 50~75%", 0.5, 0.75), ("타자 25~50%", 0.25, 0.5), ("타자 25% 미만", 0, 0.25) };
			(string label, double lo, double hi)[] pb = { ("투수 규정 이상", 0.999, 9), ("투수 50~99%", 0.5, 0.999), ("투수 50% 미만", 0, 0.5) };
			List<string[]> rows = new List<string[]>();
			foreach (var (label, lo, hi) in bb)
			{
				double n = 0, w = 0, dAvg = 0, dOps = 0;
				foreach (Replay r in runs)
				{
					for (int i = 0; i < r.Model.Batters.Length; i++)
					{
						BatterLine b = r.Model.Batters[i];
						if (b.Replacement || b.AB < 1 || !cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c) || c.Share < lo || c.Share >= hi) continue;
						double pa = r.Stats.Bat(i, BS.PA), ab = r.Stats.Bat(i, BS.AB), h = r.Stats.Bat(i, BS.H);
						if (ab < 1) continue;
						double ops = (h + r.Stats.Bat(i, BS.BB) + r.Stats.Bat(i, BS.HBP)) / pa + (h + r.Stats.Bat(i, BS.D) + 2 * r.Stats.Bat(i, BS.T) + 3 * r.Stats.Bat(i, BS.HR)) / ab;
						double rops = (b.H + b.BB + b.HBP) / (double)b.PA + (b.H + b.D + 2 * b.T + 3 * b.HR) / (double)b.AB;
						n++;
						w += b.PA;
						dAvg += b.PA * (h / ab - b.H / (double)b.AB);
						dOps += b.PA * (ops - rops);
					}
				}
				rows.Add(new[] { label, n.ToString("F0"), (dAvg / Math.Max(1, w)).ToString("+0.000;-0.000", Inv), "타율", (dOps / Math.Max(1, w)).ToString("+0.000;-0.000", Inv), "OPS" });
			}
			foreach (var (label, lo, hi) in pb)
			{
				double n = 0, w = 0, dEra = 0;
				foreach (Replay r in runs)
				{
					for (int i = 0; i < r.Model.Pitchers.Length; i++)
					{
						PitcherLine p = r.Model.Pitchers[i];
						if (p.Replacement || p.Outs < 3 || !cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c) || c.Share < lo || c.Share >= hi) continue;
						double outs = r.Stats.Pit(i, PS.Outs);
						if (outs < 1) continue;
						n++;
						w += p.Outs;
						dEra += p.Outs * (r.Stats.Pit(i, PS.ER) * 27.0 / outs - p.ER * 27.0 / p.Outs);
					}
				}
				rows.Add(new[] { label, n.ToString("F0"), (dEra / Math.Max(1, w)).ToString("+0.00;-0.00", Inv), "평균자책", "", "" });
			}
			return rows;
		}

		private static void WriteInverse(string path, Inverse b, Inverse p)
		{
			StringBuilder sb = new StringBuilder("kind,year,sidx,name,share,old1,old2,old3,old4,old5,new1,new2,new3,new4,new5\n");
			foreach (Inverse v in new[] { b, p })
			{
				for (int i = 0; i < v.Cards.Count; i++)
				{
					CardRow c = v.Cards[i];
					IEnumerable<string> olds = v.Stats.Select(st => c.Raw[st].ToString("F1", Inv)).Concat(Enumerable.Repeat("", 5 - v.Stats.Length));
					IEnumerable<string> news = v.X[i].Select(x => Math.Clamp(65 + 10 * x, 40, 95).ToString("F1", Inv)).Concat(Enumerable.Repeat("", 5 - v.Stats.Length));
					sb.Append(string.Join(",", c.Kind, c.Year, c.Sidx, c.Name, c.Share.ToString("F2", Inv), string.Join(",", olds), string.Join(",", news))).Append('\n');
				}
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WritePureReport(string path, Score baseline, List<(string label, Score score)> variants, List<string[]> hBands, List<string[]> pBands,
			Inverse ib, Inverse ip, CardRules pk, (double a, double b, double r2) risp, List<Replay> hruns, List<Replay> pinv, Dictionary<(char, int, int), CardRow> cards)
		{
			StringBuilder h = new StringBuilder();
			h.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>숨은 성향 없는 카드</title><style>");
			h.Append(":root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e4e1d8;--head:#f1eee6}@media (prefers-color-scheme:dark){:root{--bg:#18181a;--fg:#ecebe6;--muted:#a19f98;--line:#33322f;--head:#232325}}");
			h.Append("body{background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,'Malgun Gothic',sans-serif;margin:0 auto;padding:24px 16px;max-width:1100px}h2{font-size:17px;margin:26px 0 8px}.note{color:var(--muted)}");
			h.Append(".wrap{overflow-x:auto}table{border-collapse:collapse;font-variant-numeric:tabular-nums;white-space:nowrap}th,td{border-bottom:1px solid var(--line);padding:3px 8px;text-align:right}th{background:var(--head)}td.l,th.l{text-align:left}</style></head><body>");
			h.Append("<h1 style=\"font-size:22px\">숨은 성향 없이 카드만으로 재현되는가 (1999~2025)</h1>");
			h.Append("<h2>검증 (규정타석 타자 / 80이닝 이상이면서 삭감 없는 투수의 평균 오차, 삭감 전 능력치)</h2><div class=\"wrap\"><table><tr><th class=\"l\">방식</th><th>타율</th><th>출루율</th><th>장타율</th><th>홈런(개)</th><th>삼진%</th><th>볼넷%</th><th>도루(개)</th><th>평균자책</th><th>K/9</th><th>BB/9</th><th>HR/9</th><th>리그 타율</th><th>리그 ERA</th><th>리그 경기당 득점</th></tr>");
			void Row(string label, Score x) => h.Append($"<tr><td class=\"l\">{label}</td><td>{x.Avg:.000}</td><td>{x.Obp:.000}</td><td>{x.Slg:.000}</td><td>{x.Hr:F1}</td><td>{x.KPct * 100:F1}</td><td>{x.BbPct * 100:F1}</td><td>{x.Sb:F1}</td>"
				+ $"<td>{x.Era:F2}</td><td>{x.K9:F2}</td><td>{x.Bb9:F2}</td><td>{x.Hr9:F2}</td><td>{x.LeagueAvg:.000}</td><td>{x.LeagueEra:F2}</td><td>{x.LeagueRpg:F2}</td></tr>");
			Row("참고: 실력값(카드 이전)", baseline);
			foreach (var (label, sc) in variants) Row(label, sc);
			h.Append("</table></div>");
			if (ib != null)
			{
				h.Append("<h2>역산 카드: 보이는 능력치로 각 결과를 얼마나 설명하는가 (R²)</h2><div class=\"wrap\"><table><tr><th class=\"l\">대상</th>");
				foreach (string n in ib.CompNames) h.Append($"<th>{n}</th>");
				h.Append("</tr><tr><td class=\"l\">타자</td>");
				foreach (double v in ib.R2) h.Append($"<td>{v:F2}</td>");
				h.Append("</tr></table><table style=\"margin-top:8px\"><tr><th class=\"l\">대상</th>");
				foreach (string n in ip.CompNames) h.Append($"<th>{n}</th>");
				h.Append("</tr><tr><td class=\"l\">투수</td>");
				foreach (double v in ip.R2) h.Append($"<td>{v:F2}</td>");
				h.Append("</tr></table></div><p class=\"note\">1이면 능력치만으로 완전히 재현, 0이면 리그 평균 이상의 정보가 없음.</p>");
				h.Append("<h2>역산 능력치와 지금 카드 능력치의 상관</h2><div class=\"wrap\"><table><tr>");
				foreach (int st in ib.Stats) h.Append($"<th>{CardRules.BatterStats[st]}</th>");
				foreach (int st in ip.Stats) h.Append($"<th>{CardRules.PitcherStats[st]}</th>");
				h.Append("</tr><tr>");
				foreach (double v in ib.Corr.Concat(ip.Corr)) h.Append($"<td>{v:F2}</td>");
				h.Append("</tr></table></div>");
				h.Append("<h2>역산 카드의 규칙: 능력치 +10의 효과 (기준 세계)</h2><div class=\"wrap\"><table><tr><th class=\"l\">대상</th><th class=\"l\">결과</th><th>65일 때</th><th class=\"l\">+10의 효과</th></tr>");
				void Rule(string who, CardRules.Comp c)
				{
					bool babip = c.Name == "BABIP";
					string eff = string.Join(", ", c.StatNames.Select((n, j) => $"{n} {c.Beta[j] * c.Base * (babip ? 1000 : 100):+0.0;-0.0}{(babip ? "리" : "%p")}"));
					h.Append($"<tr><td class=\"l\">{who}</td><td class=\"l\">{c.Label}</td><td>{(babip ? c.Base.ToString(".000") : (c.Base * 100).ToString("F1") + "%")}</td><td class=\"l\">{eff}</td></tr>");
				}
				foreach (CardRules.Comp c in BatterComps(pk)) Rule("타자", c);
				foreach (CardRules.Comp c in PitcherComps(pk)) Rule("투수", c);
				h.Append($"<tr><td class=\"l\">투수</td><td class=\"l\">위기관리 (득점권 피안타 배수)</td><td>{Math.Exp(pk.RispBase):F2}</td><td class=\"l\">정신 ×{Math.Exp(pk.RispPer10):F3}</td></tr></table></div>");
				h.Append("<p class=\"note\">인플레이 타율은 1000분의 1(리) 단위. 구위·컨트롤의 탈삼진과 테크닉의 인플레이 피안타는 설계값으로 고정, 나머지는 데이터로 맞춤.</p>");
			}
			if (risp.r2 != 0)
			{
				h.Append($"<p class=\"note\">지금 카드: 투수 정신 → 위기관리 회귀 R² {risp.r2:F2} (정신 +10마다 득점권 피안타 ×{Math.Exp(risp.b):F3}).</p>");
			}
			// Famous seasons: real / hybrid / pure inverse.
			(int y, string n)[] famous = { (1999, "이승엽"), (2003, "이승엽"), (2010, "이대호"), (2014, "서건창"), (2015, "테임즈"), (2016, "최형우"), (2020, "로하스"), (2024, "김도영"), (2021, "이정후") };
			h.Append("<h2>기억에 남는 타자 (실제 / 숨은 성향 / 역산 카드)</h2><div class=\"wrap\"><table><tr><th class=\"l\">선수</th><th>타율</th><th>홈런</th><th>삼진</th><th>볼넷</th><th>도루</th></tr>");
			string Bat(List<Replay> runs, int y, string n, Func<BatterLine, SimStats, int, double, string> f)
			{
				Replay r = runs?.FirstOrDefault(x => x.Model.Season.Year == y);
				if (r == null) return "-";
				int i = Array.FindIndex(r.Model.Batters, b => b.Name == n);
				if (i < 0) return "-";
				BatterLine b = r.Model.Batters[i];
				return f(b, r.Stats, i, b.PA / Math.Max(1.0, r.Stats.Bat(i, BS.PA)));
			}
			foreach (var (y, n) in famous)
			{
				if (Bat(hruns, y, n, (b, st, i, sc) => "x") == "-") continue;
				string Cell(Func<BatterLine, string> real, Func<BatterLine, SimStats, int, double, string> sim) =>
					$"<td>{Bat(hruns, y, n, (b, st, i, sc) => real(b))} / {Bat(hruns, y, n, sim)} / {Bat(pinv, y, n, sim)}</td>";
				h.Append($"<tr><td class=\"l\">{y} {WebUtility.HtmlEncode(n)}</td>");
				h.Append(Cell(b => (b.H / (double)b.AB).ToString(".000"), (b, st, i, sc) => (st.Bat(i, BS.H) / (double)st.Bat(i, BS.AB)).ToString(".000")));
				h.Append(Cell(b => b.HR.ToString(), (b, st, i, sc) => (st.Bat(i, BS.HR) * sc).ToString("F1")));
				h.Append(Cell(b => b.SO.ToString(), (b, st, i, sc) => (st.Bat(i, BS.SO) * sc).ToString("F0")));
				h.Append(Cell(b => b.BB.ToString(), (b, st, i, sc) => (st.Bat(i, BS.BB) * sc).ToString("F0")));
				h.Append(Cell(b => b.SB.ToString(), (b, st, i, sc) => (st.Bat(i, BS.SB) * sc).ToString("F1")));
				h.Append("</tr>");
			}
			h.Append("</table></div>");
			(int y, string n)[] pf = { (2006, "류현진"), (2010, "류현진"), (2011, "오승환"), (2011, "윤석민"), (2019, "린드블럼"), (2023, "페디"), (2025, "폰세"), (2017, "양현종") };
			h.Append("<h2>기억에 남는 투수 (실제 / 숨은 성향 / 역산 카드)</h2><div class=\"wrap\"><table><tr><th class=\"l\">선수</th><th>평균자책</th><th>탈삼진</th><th>볼넷</th><th>피홈런</th></tr>");
			string Pit(List<Replay> runs, int y, string n, Func<PitcherLine, SimStats, int, double, string> f)
			{
				Replay r = runs?.FirstOrDefault(x => x.Model.Season.Year == y);
				if (r == null) return "-";
				int i = Array.FindIndex(r.Model.Pitchers, p => p.Name == n);
				if (i < 0) return "-";
				PitcherLine p = r.Model.Pitchers[i];
				return f(p, r.Stats, i, p.Outs / Math.Max(1.0, r.Stats.Pit(i, PS.Outs)));
			}
			foreach (var (y, n) in pf)
			{
				if (Pit(hruns, y, n, (p, st, i, sc) => "x") == "-") continue;
				string Cell(Func<PitcherLine, string> real, Func<PitcherLine, SimStats, int, double, string> sim) =>
					$"<td>{Pit(hruns, y, n, (p, st, i, sc) => real(p))} / {Pit(hruns, y, n, sim)} / {Pit(pinv, y, n, sim)}</td>";
				h.Append($"<tr><td class=\"l\">{y} {WebUtility.HtmlEncode(n)}</td>");
				h.Append(Cell(p => (p.ER * 27.0 / p.Outs).ToString("F2"), (p, st, i, sc) => (st.Pit(i, PS.ER) * 27.0 / Math.Max(1.0, st.Pit(i, PS.Outs))).ToString("F2")));
				h.Append(Cell(p => p.SO.ToString(), (p, st, i, sc) => (st.Pit(i, PS.SO) * sc).ToString("F0")));
				h.Append(Cell(p => p.BB.ToString(), (p, st, i, sc) => (st.Pit(i, PS.BB) * sc).ToString("F0")));
				h.Append(Cell(p => p.HR.ToString(), (p, st, i, sc) => (st.Pit(i, PS.HR) * sc).ToString("F1")));
				h.Append("</tr>");
			}
			h.Append("</table></div>");
			// Fielding and running (hybrid replay).
			long[] fld = new long[SimStats.FieldBins * 2 + 4];
			foreach (Replay r in hruns)
			{
				for (int j = 0; j < fld.Length; j++) fld[j] += r.Stats.Field[j];
			}
			double teamSeasons = hruns.Sum(r => r.Model.Season.Teams.Count * (double)r.Reps);
			h.Append("<h2>포수 수비별 도루 저지율 (숨은 성향 카드 재현)</h2><div class=\"wrap\"><table><tr><th class=\"l\">포수 수비</th><th>도루 시도</th><th>저지율</th></tr>");
			for (int bin = 0; bin < SimStats.FieldBins; bin++)
			{
				if (fld[bin * 2] < 200) continue;
				h.Append($"<tr><td class=\"l\">{40 + 5 * bin}~{44 + 5 * bin}</td><td>{fld[bin * 2]}</td><td>{fld[bin * 2 + 1] * 100.0 / fld[bin * 2]:F1}%</td></tr>");
			}
			h.Append($"</table></div><p class=\"note\">팀당 한 시즌 평균: 좌중간 단타에서 2루 시도 {fld[SimStats.FieldTry] / teamSeasons:F1}번 (세이프 {fld[SimStats.FieldSafe] / teamSeasons:F1}, 아웃 {fld[SimStats.FieldOut] / teamSeasons:F1}), 우중간 3루타 저지 {fld[SimStats.FieldHeld] / teamSeasons:F1}번.</p>");
			void BandTable(string title, List<string[]> rows)
			{
				if (rows == null) return;
				h.Append($"<h2>{title}</h2><div class=\"wrap\"><table><tr><th class=\"l\">규정 대비 출전</th><th>인원</th><th class=\"l\">실제 대비 카드 결과</th></tr>");
				foreach (string[] r in rows)
				{
					h.Append($"<tr><td class=\"l\">{r[0]}</td><td>{r[1]}</td><td class=\"l\">{r[3]} {r[2]}{(r[4].Length > 0 ? $", {r[5]} {r[4]}" : "")}</td></tr>");
				}
				h.Append("</table></div>");
			}
			BandTable($"출전량 삭감의 효과: 숨은 성향 카드 (출전 비율 {(CutPower ?? 1.0):0.##}제곱)", hBands);
			BandTable($"출전량 삭감의 효과: 역산 카드 (출전 비율 {(CutPower ?? 1.0):0.##}제곱)", pBands);
			h.Append("</body></html>");
			File.WriteAllText(path, h.ToString(), Encoding.UTF8);
		}

		// ---- development: training, contract bonuses and team colour raise stats ----

		public static bool DevTest;
		// Development weight: the share of a bonus point (training, contract, team colour) the game plays (null = calibrate).
		public static double? DevWeight;
		// Calibration target instead: a club this many bonus points above the league wins this often.
		public static (int d, double win)? DevTarget;

		private sealed class DevLine
		{
			public string Section, Label;
			public double Avg, Obp, Slg, HrPct, KPct, BbPct, Rpg, Era, TriplesPer100H, StealTriesPerG, CsPct, StretchPerTeam, StretchSafePct, HeldPct;
			public double TeamWinPct = double.NaN, TeamBasePct = double.NaN, TeamAvg = double.NaN, TeamEra = double.NaN;
		}

		// Raise stats on a copy of the deck: batter/pitcher stats in the masks, every fielding
		// position (when 수비 is raised) and the pitch ratings (when 구위 is raised). The shown stat
		// gains d (cap 140); the game plays it at the development weight w: base + w × gain.
		private static Dictionary<(char, int, int), CardRow> Raised(Dictionary<(char, int, int), CardRow> cards, double d, int[] batStats, int[] pitStats,
			HashSet<(char, int, int)> only = null, double w = 1.0)
		{
			Dictionary<(char, int, int), CardRow> deck = cards.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
			double Gain(double v) => Math.Max(0, Math.Min(140, v + d) - v);
			foreach (var kv in deck)
			{
				if (only != null && !only.Contains(kv.Key)) continue;
				CardRow c = kv.Value;
				int[] mask = c.Kind == 'B' ? batStats : pitStats;
				foreach (int j in mask) c.Raw[j] += w * Gain(c.Raw[j]);
				if (c.Kind == 'B' && mask.Contains(5))
				{
					c.ShownCatcher = c.RawDefs[0] + Gain(c.RawDefs[0]);
					for (int p = 0; p < 8; p++) c.RawDefs[p] += w * Gain(c.RawDefs[p]);
				}
				if (c.Kind == 'P' && mask.Contains(4))
				{
					c.Pitches = c.Pitches.Select(x => (x.code, x.rating + w * Gain(x.rating))).ToList();
				}
			}
			return deck;
		}

		// League reference: every stat counts from the league's own average (playing-time weighted),
		// so a league whose players all gained the same plays like the original one. Here the
		// reference is each real season's card average, so the unraised deck replays unchanged.
		private static void Normalise(Dictionary<(char, int, int), CardRow> deck, Dictionary<(char, int, int), CardRow> reference)
		{
			foreach (var group in deck.Values.Where(c => c.Weight > 0).GroupBy(c => (c.Year, c.Kind)))
			{
				double w = group.Sum(c => c.Weight);
				double[] shift = new double[6];
				for (int j = 0; j < 6; j++)
				{
					double now = group.Sum(c => c.Raw[j] * c.Weight) / w;
					double was = group.Sum(c => reference[(c.Kind, c.Sidx, c.Year)].Raw[j] * c.Weight) / w;
					shift[j] = now - was;
				}
				foreach (CardRow c in deck.Values.Where(c => c.Year == group.Key.Year && c.Kind == group.Key.Kind))
				{
					for (int j = 0; j < 6; j++) c.Raw[j] -= shift[j];
					if (c.Kind == 'B')
					{
						for (int p = 0; p < 8; p++) c.RawDefs[p] -= shift[5];
					}
					else
					{
						c.Pitches = c.Pitches.Select(x => (x.code, x.rating - shift[4])).ToList();
					}
				}
			}
		}

		// League totals of a replay set; with `team`, also that club's (index 0 each season) record.
		private static DevLine Summarise(string label, List<Replay> runs, HashSet<(char, int, int)> team = null)
		{
			double pa = 0, ab = 0, h = 0, d = 0, t = 0, hr = 0, bb = 0, so = 0, hbp = 0, sb = 0, cs = 0, sf = 0, outs = 0, er = 0, runsScored = 0, games = 0, w = 0, l = 0;
			double tAb = 0, tH = 0, tOuts = 0, tEr = 0, teamSeasons = 0;
			long tries = 0, safe = 0, held = 0;
			foreach (Replay r in runs)
			{
				SeasonModel m = r.Model;
				for (int i = 0; i < m.Batters.Length; i++)
				{
					pa += r.Stats.Bat(i, BS.PA); ab += r.Stats.Bat(i, BS.AB); h += r.Stats.Bat(i, BS.H); d += r.Stats.Bat(i, BS.D); t += r.Stats.Bat(i, BS.T);
					hr += r.Stats.Bat(i, BS.HR); bb += r.Stats.Bat(i, BS.BB); so += r.Stats.Bat(i, BS.SO); hbp += r.Stats.Bat(i, BS.HBP);
					sb += r.Stats.Bat(i, BS.SB); cs += r.Stats.Bat(i, BS.CS); sf += r.Stats.Bat(i, BS.SF);
				}
				for (int i = 0; i < m.Pitchers.Length; i++)
				{
					outs += r.Stats.Pit(i, PS.Outs); er += r.Stats.Pit(i, PS.ER);
				}
				if (team != null)
				{
					foreach (int i in m.TeamBatters[0]) { tAb += r.Stats.Bat(i, BS.AB); tH += r.Stats.Bat(i, BS.H); }
					foreach (int i in m.TeamPitchers[0])
					{
						tOuts += r.Stats.Pit(i, PS.Outs); tEr += r.Stats.Pit(i, PS.ER);
						if (!m.Pitchers[i].Replacement) { w += r.Stats.Pit(i, PS.W); l += r.Stats.Pit(i, PS.L); }
					}
				}
				runsScored += r.Stats.Runs;
				games += r.Stats.Games;
				teamSeasons += m.Season.Teams.Count * (double)r.Reps;
				tries += r.Stats.Field[SimStats.FieldTry];
				safe += r.Stats.Field[SimStats.FieldSafe];
				held += r.Stats.Field[SimStats.FieldHeld];
			}
			return new DevLine
			{
				Label = label, Avg = h / ab, Obp = (h + bb + hbp) / (ab + bb + hbp + sf), Slg = (h + d + 2 * t + 3 * hr) / ab,
				HrPct = hr / pa, KPct = so / pa, BbPct = bb / pa, Rpg = runsScored / (games * 2), Era = er * 27 / outs,
				TriplesPer100H = t * 100 / h, StealTriesPerG = (sb + cs) / (games * 2), CsPct = cs / Math.Max(1, sb + cs),
				StretchPerTeam = tries / teamSeasons, StretchSafePct = safe / (double)Math.Max(1, tries), HeldPct = held / Math.Max(1, held + t),
				TeamWinPct = team != null ? w / Math.Max(1, w + l) : double.NaN,
				TeamAvg = team != null ? tH / Math.Max(1, tAb) : double.NaN, TeamEra = team != null ? tEr * 27 / Math.Max(1, tOuts) : double.NaN
			};
		}

		private static void DevelopmentTest(List<SeasonFit> fits, CardRules k, Dictionary<(char, int, int), CardRow> cards, EraWorld world,
			ParkModel parks, CardData cardData, int reps, string outDir, Action<string> log)
		{
			List<Replay> Play(Dictionary<(char, int, int), CardRow> deck)
			{
				bool savedRaw = UseRaw;
				UseRaw = true;
				ConcurrentBag<Replay> b = new ConcurrentBag<Replay>();
				// The same random stream for every scenario, so the differences are the rules'.
				Parallel.ForEach(fits, f => b.Add(Run(f, k, deck, world, parks, cardData, reps, (ulong)f.Model.Season.Year * 7777, hybrid: true)));
				UseRaw = savedRaw;
				return b.OrderBy(r => r.Model.Season.Year).ToList();
			}
			Dictionary<(char, int, int), CardRow> Ref(Dictionary<(char, int, int), CardRow> deck)
			{
				Normalise(deck, cards);
				return deck;
			}
			int[] allB = { 0, 1, 2, 3, 4, 5 }, allP = { 0, 1, 2, 3, 4, 5 }, none = new int[0];
			List<DevLine> rows = new List<DevLine>();
			string section = "";
			DevLine Add(DevLine x)
			{
				x.Section = section;
				rows.Add(x);
				log($"{x.Label}: AVG {x.Avg:F3} SLG {x.Slg:F3} HR% {x.HrPct * 100:F2} K% {x.KPct * 100:F1} BB% {x.BbPct * 100:F1} R/G {x.Rpg:F2} ERA {x.Era:F2} | 3B/100H {x.TriplesPer100H:F2} SBtry/G {x.StealTriesPerG:F2} CS% {x.CsPct * 100:F1} stretch/team {x.StretchPerTeam:F1} safe% {x.StretchSafePct * 100:F1} held% {x.HeldPct * 100:F1}"
					+ (double.IsNaN(x.TeamWinPct) ? "" : $" | team W% {x.TeamWinPct:F3} AVG {x.TeamAvg:F3} ERA {x.TeamEra:F2}"));
				return x;
			}
			// One club per season (the first in the table) gains on every stat; the rest of the league stays.
			HashSet<(char, int, int)> team = new HashSet<(char, int, int)>();
			foreach (SeasonFit f in fits)
			{
				SeasonModel m = f.Model;
				for (int i = 0; i < m.Batters.Length; i++) if (m.BatterTeam[i] == 0) team.Add(('B', m.Batters[i].Sidx, m.Batters[i].Year));
				foreach (int i in m.TeamPitchers[0]) team.Add(('P', m.Pitchers[i].Sidx, m.Pitchers[i].Year));
			}

			List<Replay> baseRuns = Play(cards);
			DevLine teamBase = Summarise("한 팀 기준", baseRuns, team);
			section = "상대와 육성 차이: 능력치 효과 100%";
			List<(double e, double win)> curve = new List<(double, double)> { (0, teamBase.TeamWinPct) };
			foreach (int e in new[] { 4, 8, 10, 12, 15 })
			{
				DevLine x = Summarise($"육성 차이 +{e}", Play(Ref(Raised(cards, e, allB, allP, team))), team);
				x.TeamBasePct = teamBase.TeamWinPct;
				Add(x);
				curve.Add((e, x.TeamWinPct));
			}
			double EFor(double target)
			{
				for (int i = 1; i < curve.Count; i++)
				{
					if (curve[i].win >= target)
					{
						var (e0, w0) = curve[i - 1];
						var (e1, w1) = curve[i];
						return e0 + (e1 - e0) * (target - w0) / Math.Max(1e-9, w1 - w0);
					}
				}
				return double.NaN;
			}
			List<(int d, double target, double e)> calib = new List<(int, double, double)>();
			double sxy = 0, sxx = 0;
			foreach (var (d, target) in DevTarget.HasValue ? new[] { DevTarget.Value } : new[] { 10, 20, 30, 40 }.Select(d => (d, teamBase.TeamWinPct + d / 100.0)).ToArray())
			{
				double e = EFor(target);
				calib.Add((d, target, e));
				if (!double.IsNaN(e))
				{
					sxy += e * d;
					sxx += d * d;
				}
			}
			double w = DevWeight ?? Math.Round(sxy / sxx, 3);
			log("development weight " + w.ToString("F2", Inv) + ": " + string.Join(", ", calib.Select(c => $"+{c.d} -> {c.e:F1}")));

			section = $"상대와 육성 차이: 육성 반영률 {w * 100:F1}%";
			foreach (int d in new[] { 10, 20, 30, 40, 75 })
			{
				DevLine x = Summarise($"육성 차이 +{d} (실제 효과 +{d * w:F1})", Play(Ref(Raised(cards, d, allB, allP, team, w))), team);
				x.TeamBasePct = teamBase.TeamWinPct;
				Add(x);
			}

			section = "리그 전체가 함께 육성 (리그 평균 기준)";
			Add(Summarise("육성 없음", baseRuns));
			Add(Summarise($"전원 +40, 반영률 {w * 100:F0}%, 리그 평균 기준 없이", Play(Raised(cards, 40, allB, allP, null, w))));
			Add(Summarise($"전원 +40, 반영률 {w * 100:F0}% + 리그 평균 기준", Play(Ref(Raised(cards, 40, allB, allP, null, w)))));
			Add(Summarise("주력·수비만 +30, 리그 평균 기준 없이", Play(Raised(cards, 30, new[] { 3, 5 }, none))));
			Add(Summarise("주력·수비만 +30 + 리그 평균 기준", Play(Ref(Raised(cards, 30, new[] { 3, 5 }, none)))));

			// The catcher's lift on 컨트롤·테크닉·구위 (shown defence) against none at all.
			section = $"포수 효과 ({Field.CatcherBoostFrom + 1:F0}부터 1점당 {Field.CatcherBoostPerPoint:0.##}%)와 효과 없음";
			double savedFrom = Field.CatcherBoostFrom;
			foreach (bool on in new[] { false, true })
			{
				Field.CatcherBoostFrom = on ? savedFrom : 1e9;
				string at = on ? "포수 효과" : "효과 없음";
				Add(Summarise($"육성 없음, {at}", on ? baseRuns : Play(cards)));
				Add(Summarise($"전원 +40, 반영률 + 리그 평균 기준, {at}", Play(Ref(Raised(cards, 40, allB, allP, null, w)))));
				DevLine x = Summarise($"육성 차이 +30, {at}", Play(Ref(Raised(cards, 30, allB, allP, team, w))), team);
				x.TeamBasePct = teamBase.TeamWinPct;
				Add(x);
			}
			Field.CatcherBoostFrom = savedFrom;

			// Gap singles by the runner's 주력, and every club's batting average and ERA.
			long[] stretch = new long[SimStats.FieldBins * 3];
			foreach (Replay r in baseRuns)
			{
				for (int j = 0; j < stretch.Length; j++) stretch[j] += r.Stats.Stretch[j];
			}
			double teamSeasons = baseRuns.Sum(r => r.Model.Season.Teams.Count * (double)r.Reps);
			List<ClubRow> clubs = RealClubs(baseRuns, cards);

			// Game-league environment: the league centre that keeps the most clubs inside the
			// batting-average and ERA bands, reached by scaling in-play hits (average) and homers
			// with walks (runs). Elasticities are rough; a few rounds converge.
			var (targetA, targetE, _) = BestCentre(clubs);
			if (EnvTarget.HasValue) (targetA, targetE) = EnvTarget.Value;
			double la = 0, lb = 0;
			List<Replay> envRuns = baseRuns;
			for (int it = 0; it < 8; it++)
			{
				var (ma, me) = LeagueMeans(envRuns);
				log($"environment round {it}: league AVG {ma:F4} ERA {me:F3} (target {targetA:F3} {targetE:F2})");
				double dA = Math.Log(targetA / ma), dE = Math.Log(targetE / me);
				if (Math.Abs(dA) < 0.0015 && Math.Abs(dE) < 0.004) break;
				double da = dA / 0.85, db = (dE - 1.6 * da) / 0.6;
				la += da;
				lb += db;
				Env = new EnvMults { Babip = Math.Exp(la), HR = Math.Exp(lb), BB = Math.Exp(lb) };
				envRuns = Play(cards);
			}
			EnvMults tuned = Env ?? new EnvMults();
			Env = null;
			section = "게임 리그 환경 (리그 평균 기준점)";
			Add(Summarise("지금 기준점 (1999~2025 평균 환경)", baseRuns));
			Add(Summarise($"범위에 맞춘 환경: 인플레이 안타 ×{tuned.Babip:F3}, 홈런·볼넷 ×{tuned.HR:F3}", envRuns));
			List<ClubRow> envClubs = RealClubs(envRuns, cards);
			StringBuilder envHtml = new StringBuilder();
			envHtml.Append($"<h2>범위에 맞춘 환경에서 기억에 남는 타자 (실제 / 지금 기준점 / 맞춘 환경)</h2><div class=\"wrap\"><table><tr><th class=\"l\">선수</th><th>타율</th><th>홈런</th><th>볼넷</th></tr>");
			(int y, string n)[] famous = { (1999, "이승엽"), (2003, "이승엽"), (2014, "박병호"), (2015, "테임즈"), (2016, "최형우"), (2020, "로하스"), (2024, "김도영"), (2021, "이정후") };
			foreach (var (y, nm) in famous)
			{
				Replay rb = baseRuns.FirstOrDefault(x => x.Model.Season.Year == y), re = envRuns.FirstOrDefault(x => x.Model.Season.Year == y);
				int i = rb == null ? -1 : Array.FindIndex(rb.Model.Batters, b => b.Name == nm);
				if (i < 0) continue;
				BatterLine b = rb.Model.Batters[i];
				string Line(Replay r, Func<SimStats, int, double, string> f) => f(r.Stats, i, b.PA / Math.Max(1.0, r.Stats.Bat(i, BS.PA)));
				envHtml.Append($"<tr><td class=\"l\">{y} {WebUtility.HtmlEncode(nm)}</td>"
					+ $"<td>{b.H / (double)b.AB:.000} / {Line(rb, (st, j, sc) => (st.Bat(j, BS.H) / (double)st.Bat(j, BS.AB)).ToString(".000"))} / {Line(re, (st, j, sc) => (st.Bat(j, BS.H) / (double)st.Bat(j, BS.AB)).ToString(".000"))}</td>"
					+ $"<td>{b.HR} / {Line(rb, (st, j, sc) => (st.Bat(j, BS.HR) * sc).ToString("F1"))} / {Line(re, (st, j, sc) => (st.Bat(j, BS.HR) * sc).ToString("F1"))}</td>"
					+ $"<td>{b.BB} / {Line(rb, (st, j, sc) => (st.Bat(j, BS.BB) * sc).ToString("F0"))} / {Line(re, (st, j, sc) => (st.Bat(j, BS.BB) * sc).ToString("F0"))}</td></tr>");
			}
			envHtml.Append("</table></div>");
			bool InBands(double a, double e) => a >= AvgLow && a <= AvgHigh && e >= EraLow && e <= EraHigh;
			var (ea, ee) = LeagueMeans(envRuns);
			double envShare = envClubs.Count(c => InBands(ea + c.SimAvg - c.LgSimAvg, ee * c.SimEra / c.LgSimEra)) * 100.0 / envClubs.Count;
			envHtml.Append($"<p class=\"note\">맞춘 환경의 리그 타율 {ea:.000}, 평균자책 {ee:F2}: 실제 구단들의 리그 대비 차이를 얹으면 {envShare:F0}%가 두 범위 안. 표는 각 시즌 환경에서 돌린 값이라 홈런·볼넷의 줄어든 비율을 보면 됨.</p>");
			Directory.CreateDirectory(outDir);
			WriteDevReport(Path.Combine(outDir, "cardtest_dev.html"), rows, calib, w, clubs, stretch, teamSeasons, envHtml.ToString());
			File.WriteAllLines(Path.Combine(outDir, "team_lines.csv"), new[] { "year,club,real_avg,sim_avg,real_era,sim_era,league_real_avg,league_sim_avg,league_real_era,league_sim_era" }
				.Concat(clubs.Select(c => string.Join(",", c.Year, c.Club, c.RealAvg.ToString("F4", Inv), c.SimAvg.ToString("F4", Inv), c.RealEra.ToString("F3", Inv), c.SimEra.ToString("F3", Inv),
					c.LgRealAvg.ToString("F4", Inv), c.LgSimAvg.ToString("F4", Inv), c.LgRealEra.ToString("F3", Inv), c.LgSimEra.ToString("F3", Inv)))), new UTF8Encoding(true));
		}

		private static (double avg, double era) LeagueMeans(List<Replay> runs)
		{
			double a = 0, e = 0;
			foreach (Replay r in runs)
			{
				double ab = 0, h = 0, outs = 0, er = 0;
				for (int i = 0; i < r.Model.Batters.Length; i++) { ab += r.Stats.Bat(i, BS.AB); h += r.Stats.Bat(i, BS.H); }
				for (int i = 0; i < r.Model.Pitchers.Length; i++) { outs += r.Stats.Pit(i, PS.Outs); er += r.Stats.Pit(i, PS.ER); }
				a += h / ab;
				e += er * 27 / outs;
			}
			return (a / runs.Count, e / runs.Count);
		}

		// League centre that keeps the most clubs inside both bands, with each club's gap to its
		// league taken from the card replay (average: difference, ERA: ratio).
		private static (double avg, double era, double share) BestCentre(List<ClubRow> clubs)
		{
			bool In(double a, double e) => a >= AvgLow && a <= AvgHigh && e >= EraLow && e <= EraHigh;
			double bestA = 0, bestE = 0, best = -1;
			for (double a = 0.250; a <= 0.3001; a += 0.001)
			{
				for (double e = 3.5; e <= 5.001; e += 0.05)
				{
					double s = clubs.Count(c => In(a + c.SimAvg - c.LgSimAvg, e * c.SimEra / c.LgSimEra)) * 100.0 / clubs.Count;
					if (s > best) { best = s; bestA = a; bestE = e; }
				}
			}
			return (bestA, bestE, best);
		}

		private sealed class ClubRow
		{
			public int Year;
			public string Club;
			public double Bat, Pit, Adv, RealWin, SimWin;
			public double RealAvg, SimAvg, RealEra, SimEra, LgRealAvg, LgSimAvg, LgRealEra, LgSimEra;
		}

		// Each real club against its league: the playing-time-weighted gap of its cards (batters
		// 컨택 파워 선구 주력 수비, pitchers 구속 컨트롤 테크닉 구위), how it finished, and its
		// batting average and ERA (record and card replay).
		private static List<ClubRow> RealClubs(List<Replay> runs, Dictionary<(char, int, int), CardRow> cards)
		{
			int[] bs = { 0, 1, 2, 3, 5 }, ps = { 1, 2, 3, 4 };
			List<ClubRow> list = new List<ClubRow>();
			foreach (Replay r in runs)
			{
				SeasonModel m = r.Model;
				double Mean(IEnumerable<int> idx, char kind, int[] stats, Func<int, double> weight, Func<int, (int sidx, int year)> key, double[] centre)
				{
					double s = 0, w = 0;
					foreach (int i in idx)
					{
						var (sidx, year) = key(i);
						if (!cards.TryGetValue((kind, sidx, year), out CardRow c)) continue;
						s += weight(i) * stats.Average(j => c.Raw[j] - (centre != null ? centre[j] : 0));
						w += weight(i);
					}
					return w > 0 ? s / w : 0;
				}
				double[] lb = new double[6], lp = new double[6];
				foreach (int j in bs) lb[j] = Mean(Enumerable.Range(0, m.Batters.Length).Where(i => !m.Batters[i].Replacement), 'B', new[] { j }, i => m.Batters[i].PA, i => (m.Batters[i].Sidx, m.Batters[i].Year), null);
				foreach (int j in ps) lp[j] = Mean(Enumerable.Range(0, m.Pitchers.Length).Where(i => !m.Pitchers[i].Replacement), 'P', new[] { j }, i => m.Pitchers[i].BF, i => (m.Pitchers[i].Sidx, m.Pitchers[i].Year), null);
				(double avg, double era) Real(IEnumerable<int> bats, IEnumerable<int> pits)
				{
					List<BatterLine> b = bats.Select(i => m.Batters[i]).Where(x => !x.Replacement).ToList();
					List<PitcherLine> p = pits.Select(i => m.Pitchers[i]).Where(x => !x.Replacement).ToList();
					return (b.Sum(x => x.H) / (double)Math.Max(1, b.Sum(x => x.AB)), p.Sum(x => x.ER) * 27.0 / Math.Max(1, p.Sum(x => x.Outs)));
				}
				(double avg, double era) Sim(IEnumerable<int> bats, IEnumerable<int> pits) =>
					(bats.Sum(i => (double)r.Stats.Bat(i, BS.H)) / Math.Max(1, bats.Sum(i => (double)r.Stats.Bat(i, BS.AB))),
					 pits.Sum(i => (double)r.Stats.Pit(i, PS.ER)) * 27 / Math.Max(1, pits.Sum(i => (double)r.Stats.Pit(i, PS.Outs))));
				var lgReal = Real(Enumerable.Range(0, m.Batters.Length), Enumerable.Range(0, m.Pitchers.Length));
				var lgSim = Sim(Enumerable.Range(0, m.Batters.Length), Enumerable.Range(0, m.Pitchers.Length));
				for (int t = 0; t < m.Season.Teams.Count; t++)
				{
					double bat = Mean(m.TeamBatters[t].Where(i => !m.Batters[i].Replacement), 'B', bs, i => m.Batters[i].PA, i => (m.Batters[i].Sidx, m.Batters[i].Year), lb);
					double pit = Mean(m.TeamPitchers[t].Where(i => !m.Pitchers[i].Replacement), 'P', ps, i => m.Pitchers[i].BF, i => (m.Pitchers[i].Sidx, m.Pitchers[i].Year), lp);
					double rw = 0, rl = 0, sw = 0, sl = 0;
					foreach (int i in m.TeamPitchers[t])
					{
						rw += m.Pitchers[i].W; rl += m.Pitchers[i].L;
						sw += r.Stats.Pit(i, PS.W); sl += r.Stats.Pit(i, PS.L);
					}
					var real = Real(m.TeamBatters[t], m.TeamPitchers[t]);
					var sim = Sim(m.TeamBatters[t], m.TeamPitchers[t]);
					int code = m.Season.Teams[t].Code;
					list.Add(new ClubRow
					{
						Year = m.Season.Year, Club = Records.TeamNames.TryGetValue(code, out string n) ? n : code.ToString(),
						Bat = bat, Pit = pit, Adv = (bat + pit) / 2, RealWin = rw / Math.Max(1, rw + rl), SimWin = sw / Math.Max(1, sw + sl),
						RealAvg = real.avg, RealEra = real.era, SimAvg = sim.avg, SimEra = sim.era,
						LgRealAvg = lgReal.avg, LgRealEra = lgReal.era, LgSimAvg = lgSim.avg, LgSimEra = lgSim.era
					});
				}
			}
			return list;
		}

		// Team batting average and ERA bands the game league should keep its clubs in.
		public static double AvgLow = 0.260, AvgHigh = 0.300, EraLow = 3.5, EraHigh = 4.8;

		private static void WriteDevReport(string path, List<DevLine> rows, List<(int d, double target, double e)> calib, double w, List<ClubRow> clubs, long[] stretch, double teamSeasons, string envHtml)
		{
			StringBuilder h = new StringBuilder();
			h.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>육성 시나리오</title><style>");
			h.Append(":root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e4e1d8;--head:#f1eee6}@media (prefers-color-scheme:dark){:root{--bg:#18181a;--fg:#ecebe6;--muted:#a19f98;--line:#33322f;--head:#232325}}");
			h.Append("body{background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,'Malgun Gothic',sans-serif;margin:0 auto;padding:24px 16px;max-width:1200px}h2{font-size:17px;margin:26px 0 8px}.note{color:var(--muted)}");
			h.Append(".wrap{overflow-x:auto}table{border-collapse:collapse;font-variant-numeric:tabular-nums;white-space:nowrap}th,td{border-bottom:1px solid var(--line);padding:3px 8px;text-align:right}th{background:var(--head)}td.l,th.l{text-align:left}</style></head><body>");
			h.Append("<h1 style=\"font-size:22px\">육성 시나리오: 능력치가 오른 리그와 팀 (1999~2025 카드 재현)</h1>");
			foreach (var group in rows.GroupBy(r => r.Section))
			{
				bool team = group.Any(x => !double.IsNaN(x.TeamWinPct));
				h.Append($"<h2>{WebUtility.HtmlEncode(group.Key)}</h2><div class=\"wrap\"><table><tr><th class=\"l\">시나리오</th>");
				if (team) h.Append("<th>팀 승률</th><th>팀 타율</th><th>팀 평균자책</th>");
				h.Append("<th>리그 타율</th><th>출루율</th><th>장타율</th><th>홈런%</th><th>삼진%</th><th>볼넷%</th><th>경기당 득점</th><th>평균자책</th>"
					+ "<th>3루타/안타100</th><th>도루 시도/경기</th><th>도루 저지율</th><th>2루 시도/팀 시즌</th><th>2루 시도 성공%</th><th>3루타 저지%</th></tr>");
				foreach (DevLine x in group)
				{
					h.Append($"<tr><td class=\"l\">{WebUtility.HtmlEncode(x.Label)}</td>");
					if (team) h.Append(double.IsNaN(x.TeamWinPct) ? "<td></td><td></td><td></td>" : $"<td>{x.TeamBasePct:.000} → {x.TeamWinPct:.000}</td><td>{x.TeamAvg:.000}</td><td>{x.TeamEra:F2}</td>");
					h.Append($"<td>{x.Avg:.000}</td><td>{x.Obp:.000}</td><td>{x.Slg:.000}</td><td>{x.HrPct * 100:F2}</td><td>{x.KPct * 100:F1}</td><td>{x.BbPct * 100:F1}</td>"
						+ $"<td>{x.Rpg:F2}</td><td>{x.Era:F2}</td><td>{x.TriplesPer100H:F2}</td><td>{x.StealTriesPerG:F2}</td><td>{x.CsPct * 100:F1}</td><td>{x.StretchPerTeam:F1}</td><td>{x.StretchSafePct * 100:F1}</td><td>{x.HeldPct * 100:F1}</td></tr>");
				}
				h.Append("</table></div>");
			}
			h.Append("<h2>육성 반영률 맞추기: 목표 승률에 필요한 실제 효과</h2><div class=\"wrap\"><table><tr><th class=\"l\">육성 차이</th><th>목표 승률</th><th>필요한 실제 효과</th><th>비율</th></tr>");
			foreach (var (d, target, e) in calib)
			{
				h.Append($"<tr><td class=\"l\">+{d}</td><td>{target:.000}</td><td>{(double.IsNaN(e) ? "-" : "+" + e.ToString("F1"))}</td><td>{(double.IsNaN(e) ? "-" : (e / d * 100).ToString("F0") + "%")}</td></tr>");
			}
			h.Append($"</table></div><p class=\"note\">육성 반영률 {w * 100:F0}%: 카드 원래 능력치는 그대로 100% 작동하고, 육성·계약·팀컬러로 오른 부분만 이 비율로 작동.</p>");

			// Gap singles by the runner's 주력.
			h.Append("<h2>좌중간 단타에서 2루 시도 (주자 주력별, 육성 없음)</h2><div class=\"wrap\"><table><tr><th class=\"l\">주자 주력</th><th>팀 시즌당 시도</th><th>성공</th><th>아웃</th><th>성공률</th></tr>");
			long tt = 0, ts = 0;
			for (int bin = 0; bin < SimStats.FieldBins; bin++)
			{
				long tries = stretch[bin * 3], safe = stretch[bin * 3 + 1];
				tt += tries; ts += safe;
				if (tries < 50) continue;
				h.Append($"<tr><td class=\"l\">{40 + 5 * bin}~{44 + 5 * bin}</td><td>{tries / teamSeasons:F2}</td><td>{safe / teamSeasons:F2}</td><td>{(tries - safe) / teamSeasons:F2}</td><td>{safe * 100.0 / tries:F0}%</td></tr>");
			}
			h.Append($"<tr><td class=\"l\">전체</td><td>{tt / teamSeasons:F1}</td><td>{ts / teamSeasons:F1}</td><td>{(tt - ts) / teamSeasons:F1}</td><td>{ts * 100.0 / Math.Max(1, tt):F0}%</td></tr></table></div>");
			h.Append("<p class=\"note\">시도 조건: 1루가 비어 있고, 주자 주력이 80 이상(90에서 최대)이면서 좌익수·중견수 평균 수비보다 10 이상 높을 때(30 이상이면 최대). 세이프 확률은 수비보다 20 높을 때 75%, 10점마다 ±4%p, 최대 88%.</p>");

			// Team batting average and ERA bands.
			int n = clubs.Count;
			bool In(double avg, double era) => avg >= AvgLow && avg <= AvgHigh && era >= EraLow && era <= EraHigh;
			double Share(Func<ClubRow, bool> f) => clubs.Count(f) * 100.0 / n;
			double sdAvgReal = Math.Sqrt(clubs.Average(c => Math.Pow(c.RealAvg - c.LgRealAvg, 2))), sdAvgSim = Math.Sqrt(clubs.Average(c => Math.Pow(c.SimAvg - c.LgSimAvg, 2)));
			double sdEraReal = Math.Sqrt(clubs.Average(c => Math.Pow(c.RealEra / c.LgRealEra - 1, 2))), sdEraSim = Math.Sqrt(clubs.Average(c => Math.Pow(c.SimEra / c.LgSimEra - 1, 2)));
			var (bestA, bestE, bestShare) = BestCentre(clubs);
			double meanA = clubs.Average(c => c.LgSimAvg), meanE = clubs.Average(c => c.LgSimEra);
			double atMean = clubs.Count(c => In(meanA + c.SimAvg - c.LgSimAvg, meanE * c.SimEra / c.LgSimEra)) * 100.0 / n;
			h.Append($"<h2>팀 타율 {AvgLow:.000}~{AvgHigh:.000}, 팀 평균자책 {EraLow:F1}~{EraHigh:F1} 범위 ({n}개 팀-시즌)</h2><div class=\"wrap\"><table><tr><th class=\"l\"></th><th>타율 범위 안</th><th>평균자책 범위 안</th><th>둘 다</th><th>리그 대비 팀 타율 편차</th><th>리그 대비 팀 평균자책 편차</th></tr>");
			h.Append($"<tr><td class=\"l\">실제 기록 (그 시즌 환경)</td><td>{Share(c => c.RealAvg >= AvgLow && c.RealAvg <= AvgHigh):F0}%</td><td>{Share(c => c.RealEra >= EraLow && c.RealEra <= EraHigh):F0}%</td><td>{Share(c => In(c.RealAvg, c.RealEra)):F0}%</td><td>±{sdAvgReal:.000}</td><td>±{sdEraReal * 100:F0}%</td></tr>");
			h.Append($"<tr><td class=\"l\">카드 재현 (그 시즌 환경)</td><td>{Share(c => c.SimAvg >= AvgLow && c.SimAvg <= AvgHigh):F0}%</td><td>{Share(c => c.SimEra >= EraLow && c.SimEra <= EraHigh):F0}%</td><td>{Share(c => In(c.SimAvg, c.SimEra)):F0}%</td><td>±{sdAvgSim:.000}</td><td>±{sdEraSim * 100:F0}%</td></tr>");
			h.Append($"<tr><td class=\"l\">게임 리그, 지금 기준점 (리그 타율 {meanA:.000}, 평균자책 {meanE:F2})</td><td></td><td></td><td>{atMean:F0}%</td><td></td><td></td></tr>");
			h.Append($"<tr><td class=\"l\">게임 리그, 가장 잘 맞는 기준점 (리그 타율 {bestA:.000}, 평균자책 {bestE:F2})</td><td></td><td></td><td>{bestShare:F0}%</td><td></td><td></td></tr></table></div>");
			h.Append("<p class=\"note\">게임 리그 줄: 리그 평균 기준을 쓰면 리그 평균이 고정되므로, 실제 팀들의 리그 대비 차이(카드 재현)를 그 기준점에 얹어서 계산.</p>");
			h.Append(envHtml);
			h.Append("<h2>시즌별 리그 타율·평균자책 (실제 / 카드 재현)과 범위 안 팀 수</h2><div class=\"wrap\"><table><tr><th class=\"l\">시즌</th><th>리그 타율</th><th>리그 평균자책</th><th>팀 타율 범위</th><th>팀 평균자책 범위</th><th>둘 다 범위 안 (실제)</th></tr>");
			foreach (var g in clubs.GroupBy(c => c.Year))
			{
				ClubRow f = g.First();
				h.Append($"<tr><td class=\"l\">{g.Key}</td><td>{f.LgRealAvg:.000} / {f.LgSimAvg:.000}</td><td>{f.LgRealEra:F2} / {f.LgSimEra:F2}</td><td>{g.Min(c => c.RealAvg):.000}~{g.Max(c => c.RealAvg):.000}</td><td>{g.Min(c => c.RealEra):F2}~{g.Max(c => c.RealEra):F2}</td><td>{g.Count(c => In(c.RealAvg, c.RealEra))}/{g.Count()}</td></tr>");
			}
			h.Append("</table></div>");

			// Real clubs: slope of the real win% on the card gap.
			double mx = clubs.Average(c => c.Adv), my = clubs.Average(c => c.RealWin);
			double slope = clubs.Sum(c => (c.Adv - mx) * (c.RealWin - my)) / clubs.Sum(c => (c.Adv - mx) * (c.Adv - mx));
			double sy = Math.Sqrt(clubs.Average(c => (c.RealWin - my) * (c.RealWin - my))), sx = Math.Sqrt(clubs.Average(c => (c.Adv - mx) * (c.Adv - mx)));
			h.Append($"<h2>실제 구단: 리그 평균 대비 카드 능력치와 승률 ({clubs.Count}개 팀-시즌)</h2>");
			h.Append($"<p class=\"note\">팀 능력치 차이 = (타자 컨택·파워·선구·주력·수비 평균 차이 + 투수 구속·컨트롤·테크닉·구위 평균 차이) ÷ 2, 출전량 가중. 실제 승률과의 상관 {slope * sx / sy:F2}, 능력치 +1당 승률 {slope * 100:+0.0;-0.0}%p. 팀 능력치 차이의 표준편차 {sx:F1}.</p>");
			h.Append("<div class=\"wrap\"><table><tr><th class=\"l\">팀</th><th>타자 차이</th><th>투수 차이</th><th>팀 차이</th><th>실제 승률</th><th>시뮬 승률</th><th>실제 타율</th><th>실제 평균자책</th></tr>");
			foreach (ClubRow c in clubs.OrderByDescending(c => c.Adv).Take(8).Concat(clubs.OrderBy(c => c.Adv).Take(5)))
			{
				h.Append($"<tr><td class=\"l\">{c.Year} {WebUtility.HtmlEncode(c.Club)}</td><td>{c.Bat:+0.0;-0.0}</td><td>{c.Pit:+0.0;-0.0}</td><td>{c.Adv:+0.0;-0.0}</td><td>{c.RealWin:.000}</td><td>{c.SimWin:.000}</td><td>{c.RealAvg:.000}</td><td>{c.RealEra:F2}</td></tr>");
			}
			h.Append("</table></div><p class=\"note\">위 8줄은 능력치 차이가 가장 큰 팀, 아래 5줄은 가장 작은 팀.</p>");
			h.Append("<p class=\"note\">모든 시나리오는 같은 난수로 돌려서 차이는 규칙에서만 나옵니다. 리그 평균 기준: 각 능력치를 리그 평균(출전량 가중)과의 차이로 계산. 육성 차이 시나리오: 시즌마다 첫 번째 구단만 올려 나머지 구단과 육성 차이를 만들고 리그 평균 기준 적용. 팀 승률은 그 팀 투수들의 승패 합계.</p></body></html>");
			File.WriteAllText(path, h.ToString(), Encoding.UTF8);
		}

		internal static void WriteRules(string path, CardRules k)
		{
			StringBuilder sb = new StringBuilder("{\n  \"scale\": \"x = (stat - 65) / 10; q(x) = x^2 inside -2.5..3, tangent outside; rate = base * profile * exp(sum(beta*x + gamma*q(x)))\",\n");
			void Comp(string side, CardRules.Comp c, bool last)
			{
				sb.Append($"  \"{side}_{c.Name}\": {{ \"base\": {c.Base.ToString("F5", Inv)}, ");
				sb.Append(string.Join(", ", c.StatNames.Select((n, j) => $"\"{n}\": [{c.Beta[j].ToString("F4", Inv)}, {(c.Gamma != null ? c.Gamma[j] : 0).ToString("F4", Inv)}]")));
				sb.Append(last ? " }\n" : " },\n");
			}
			foreach (CardRules.Comp c in k.Batter) Comp("batter", c, false);
			foreach (CardRules.Comp c in k.Pitcher) Comp("pitcher", c, false);
			sb.Append($"  \"speed_per10\": {k.SpeedPer10.ToString("F3", Inv)},\n  \"error_per10\": {k.ErrorPer10.ToString("F3", Inv)}\n}}\n");
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
		}

		// Hidden per-card profile for the game server: batters K BB HR BABIP 2B 3B SB, pitchers K BB HR BABIP 위기관리.
		internal static void WriteProfiles(string path, Dictionary<(char, int, int), CardRow> cards)
		{
			StringBuilder sb = new StringBuilder("kind,year,sidx,name,p1,p2,p3,p4,p5,p6,p7\n");
			foreach (CardRow c in cards.Values.OrderBy(c => c.Kind).ThenBy(c => c.Year).ThenBy(c => c.Sidx))
			{
				if (c.Profile == null) continue;
				sb.Append(string.Join(",", c.Kind, c.Year, c.Sidx, c.Name, string.Join(",", c.Profile.Select(v => v.ToString("F4", Inv))))).Append('\n');
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WritePlayers(string path, List<Replay> runs, Dictionary<(char, int, int), CardRow> cards)
		{
			StringBuilder sb = new StringBuilder("kind,year,name,share,real_pa_ip,real_avg,sim_avg,real_hr,sim_hr,real_so,sim_so,real_bb,sim_bb,real_era,sim_era\n");
			foreach (Replay r in runs)
			{
				double n = r.Reps;
				for (int i = 0; i < r.Model.Batters.Length; i++)
				{
					BatterLine b = r.Model.Batters[i];
					if (b.Replacement || !cards.TryGetValue(('B', b.Sidx, b.Year), out CardRow c)) continue;
					double pa = r.Stats.Bat(i, BS.PA) / n, ab = r.Stats.Bat(i, BS.AB) / n, h = r.Stats.Bat(i, BS.H) / n;
					double scale = pa > 0 ? b.PA / pa : 0;
					sb.Append(string.Join(",", "B", b.Year, b.Name, c.Share.ToString("F2", Inv), b.PA, (b.AB > 0 ? b.H / (double)b.AB : 0).ToString("F3", Inv), (ab > 0 ? h / ab : 0).ToString("F3", Inv),
						b.HR, (r.Stats.Bat(i, BS.HR) / n * scale).ToString("F1", Inv), b.SO, (r.Stats.Bat(i, BS.SO) / n * scale).ToString("F1", Inv), b.BB, (r.Stats.Bat(i, BS.BB) / n * scale).ToString("F1", Inv), "", "")).Append('\n');
				}
				for (int i = 0; i < r.Model.Pitchers.Length; i++)
				{
					PitcherLine p = r.Model.Pitchers[i];
					if (p.Replacement || !cards.TryGetValue(('P', p.Sidx, p.Year), out CardRow c)) continue;
					double outs = r.Stats.Pit(i, PS.Outs) / n;
					double scale = outs > 0 ? p.Outs / outs : 0;
					sb.Append(string.Join(",", "P", p.Year, p.Name, c.Share.ToString("F2", Inv), (p.Outs / 3.0).ToString("F1", Inv), "", "", p.HR, (r.Stats.Pit(i, PS.HR) / n * scale).ToString("F1", Inv),
						p.SO, (r.Stats.Pit(i, PS.SO) / n * scale).ToString("F1", Inv), p.BB, (r.Stats.Pit(i, PS.BB) / n * scale).ToString("F1", Inv),
						(p.ER * 27.0 / p.Outs).ToString("F2", Inv), (outs > 0 ? r.Stats.Pit(i, PS.ER) / n * 27 / outs : 0).ToString("F2", Inv))).Append('\n');
				}
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WriteReport(string path, CardRules k, List<(int round, Score score)> history, Score baseline, List<Replay> last, Dictionary<(char, int, int), CardRow> cards)
		{
			StringBuilder h = new StringBuilder();
			h.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>카드 규칙 검증</title><style>");
			h.Append(":root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e4e1d8;--head:#f1eee6}@media (prefers-color-scheme:dark){:root{--bg:#18181a;--fg:#ecebe6;--muted:#a19f98;--line:#33322f;--head:#232325}}");
			h.Append("body{background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,'Malgun Gothic',sans-serif;margin:0 auto;padding:24px 16px;max-width:1100px}h2{font-size:17px;margin:26px 0 8px}.note{color:var(--muted)}");
			h.Append(".wrap{overflow-x:auto}table{border-collapse:collapse;font-variant-numeric:tabular-nums;white-space:nowrap}th,td{border-bottom:1px solid var(--line);padding:3px 8px;text-align:right}th{background:var(--head)}td.l,th.l{text-align:left}</style></head><body>");
			h.Append("<h1 style=\"font-size:22px\">카드 규칙 검증 (1999~2025, 카드만으로 시즌 재현)</h1>");
			h.Append("<h2>규칙: 능력치 10점이 바꾸는 확률</h2><p class=\"note\">x = (능력치 − 65) ÷ 10. 확률 = 리그 평균 × e^(Σ 계수·x). 표는 능력치 +10일 때의 변화. 투수와 타자가 만나면 두 확률을 Log5로 합칩니다.</p>");
			h.Append("<div class=\"wrap\"><table><tr><th class=\"l\">대상</th><th class=\"l\">결과</th><th>리그 기준값</th><th class=\"l\">능력치 +10의 효과</th></tr>");
			void Row(string who, CardRules.Comp c)
			{
				string eff = string.Join(", ", c.StatNames.Select((n, j) => $"{n} {(Math.Exp(c.Beta[j]) - 1) * 100:+0.0;-0.0}%"));
				h.Append($"<tr><td class=\"l\">{who}</td><td class=\"l\">{c.Label}</td><td>{c.Base:F4}</td><td class=\"l\">{eff}</td></tr>");
			}
			foreach (CardRules.Comp c in k.Batter) Row("타자", c);
			foreach (CardRules.Comp c in k.Pitcher) Row("투수", c);
			h.Append($"<tr><td class=\"l\">타자</td><td class=\"l\">주루(진루 확률)</td><td>1</td><td class=\"l\">주력 {(Math.Exp(k.SpeedPer10) - 1) * 100:+0.0}%</td></tr>");
			h.Append($"<tr><td class=\"l\">팀 수비</td><td class=\"l\">실책</td><td>3%</td><td class=\"l\">수비 평균 {(Math.Exp(k.ErrorPer10) - 1) * 100:+0.0}%</td></tr></table></div>");
			// What a card plays like at each level (the other stats at 65, profile 1).
			int[] levels = { 40, 50, 65, 80, 95, 110, 140 };
			h.Append("<h2>능력치 단계별 확률 (나머지 능력치 65, 숨은 성향 보통)</h2><div class=\"wrap\"><table><tr><th class=\"l\">결과</th><th class=\"l\">능력치</th>"
				+ string.Concat(levels.Select(l => $"<th>{l}</th>")) + "</tr>");
			void Levels(CardRules.Comp c, string[] names)
			{
				for (int j = 0; j < c.Stats.Length; j++)
				{
					if (c.Beta[j] == 0 && (c.Gamma == null || c.Gamma[j] == 0)) continue;
					h.Append($"<tr><td class=\"l\">{c.Label}</td><td class=\"l\">{names[c.Stats[j]]}</td>");
					foreach (int l in levels)
					{
						double[] r = Enumerable.Repeat(65.0, 6).ToArray();
						r[c.Stats[j]] = l;
						h.Append($"<td>{c.Base * CardRules.Mult(c, r) * 100:F1}%</td>");
					}
					h.Append("</tr>");
				}
			}
			foreach (CardRules.Comp c in k.Batter) Levels(c, CardRules.BatterStats);
			foreach (CardRules.Comp c in k.Pitcher) Levels(c, CardRules.PitcherStats);
			h.Append("</table></div><p class=\"note\">95 초과는 육성으로만 도달하는 구간이라 실제 기록이 없어, 95 지점의 기울기로 곧게 이어 붙였습니다.</p>");
			h.Append("<p class=\"note\">정신: 득점권이거나 5회 이후 3점 차 이내면, 정신이 높은 쪽이 (차이÷10)%만큼 모든 능력치가 오른 것처럼 계산. 구종: 직구와 최고 구종 1:1, 결정구 능력치가 투수 평균보다 높으면 삼진↑ 피홈런↓.</p>");
			h.Append("<h2>검증 (규정타석 타자 / 80이닝 이상이면서 삭감 없는 투수의 평균 오차)</h2><div class=\"wrap\"><table><tr><th class=\"l\">회차</th><th>타율</th><th>출루율</th><th>장타율</th><th>홈런(개)</th><th>삼진%</th><th>볼넷%</th><th>도루(개)</th><th>평균자책</th><th>K/9</th><th>BB/9</th><th>HR/9</th><th>리그 타율</th><th>리그 ERA</th><th>리그 경기당 득점</th></tr>");
			void ScoreRow(string label, Score s)
			{
				h.Append($"<tr><td class=\"l\">{label}</td><td>{s.Avg:.000}</td><td>{s.Obp:.000}</td><td>{s.Slg:.000}</td><td>{s.Hr:F1}</td><td>{s.KPct * 100:F1}</td><td>{s.BbPct * 100:F1}</td><td>{s.Sb:F1}</td>"
					+ $"<td>{s.Era:F2}</td><td>{s.K9:F2}</td><td>{s.Bb9:F2}</td><td>{s.Hr9:F2}</td><td>{s.LeagueAvg:.000}</td><td>{s.LeagueEra:F2}</td><td>{s.LeagueRpg:F2}</td></tr>");
			}
			ScoreRow("참고: 실력값(카드 이전)", baseline);
			foreach (var (round, s) in history)
			{
				ScoreRow(round == 0 ? "카드 0회차(초기 계수)" : $"카드 {round}회차", s);
			}
			h.Append("</table></div><p class=\"note\">참고 줄은 카드로 바꾸기 전 실력값으로 돌린 결과(같은 엔진). 카드는 반올림, 능력치 묶음, 규정 미만 삭감 때문에 정보가 일부 줄어듭니다.</p>");

			// Famous seasons, card replay vs real.
			(int y, string n)[] famous = { (1999, "이승엽"), (2003, "이승엽"), (2010, "이대호"), (2014, "서건창"), (2015, "테임즈"), (2016, "최형우"), (2020, "로하스"), (2024, "김도영"), (2021, "이정후") };
			h.Append("<h2>기억에 남는 타자 (실제 / 카드 시뮬)</h2><div class=\"wrap\"><table><tr><th class=\"l\">선수</th><th>타율</th><th>홈런</th><th>삼진</th><th>볼넷</th><th>도루</th></tr>");
			foreach (var (y, n) in famous)
			{
				Replay r = last.FirstOrDefault(x => x.Model.Season.Year == y);
				if (r == null) continue;
				int i = Array.FindIndex(r.Model.Batters, b => b.Name == n);
				if (i < 0) continue;
				BatterLine b = r.Model.Batters[i];
				double reps = r.Reps, pa = r.Stats.Bat(i, BS.PA) / reps, sc = b.PA / pa;
				h.Append($"<tr><td class=\"l\">{y} {WebUtility.HtmlEncode(n)}</td><td>{b.H / (double)b.AB:.000} / {r.Stats.Bat(i, BS.H) / (double)r.Stats.Bat(i, BS.AB):.000}</td>"
					+ $"<td>{b.HR} / {r.Stats.Bat(i, BS.HR) / reps * sc:F1}</td><td>{b.SO} / {r.Stats.Bat(i, BS.SO) / reps * sc:F1}</td><td>{b.BB} / {r.Stats.Bat(i, BS.BB) / reps * sc:F1}</td><td>{b.SB} / {r.Stats.Bat(i, BS.SB) / reps * sc:F1}</td></tr>");
			}
			h.Append("</table></div>");
			(int y, string n)[] pf = { (2006, "류현진"), (2010, "류현진"), (2011, "오승환"), (2011, "윤석민"), (2019, "린드블럼"), (2023, "페디"), (2025, "폰세"), (2017, "양현종") };
			h.Append("<h2>기억에 남는 투수 (실제 / 카드 시뮬)</h2><div class=\"wrap\"><table><tr><th class=\"l\">선수</th><th>평균자책</th><th>탈삼진</th><th>볼넷</th><th>피홈런</th></tr>");
			foreach (var (y, n) in pf)
			{
				Replay r = last.FirstOrDefault(x => x.Model.Season.Year == y);
				if (r == null) continue;
				int i = Array.FindIndex(r.Model.Pitchers, p => p.Name == n);
				if (i < 0) continue;
				PitcherLine p = r.Model.Pitchers[i];
				double reps = r.Reps, outs = r.Stats.Pit(i, PS.Outs) / reps, sc = p.Outs / outs;
				h.Append($"<tr><td class=\"l\">{y} {WebUtility.HtmlEncode(n)}</td><td>{p.ER * 27.0 / p.Outs:F2} / {r.Stats.Pit(i, PS.ER) / reps * 27 / outs:F2}</td>"
					+ $"<td>{p.SO} / {r.Stats.Pit(i, PS.SO) / reps * sc:F1}</td><td>{p.BB} / {r.Stats.Pit(i, PS.BB) / reps * sc:F1}</td><td>{p.HR} / {r.Stats.Pit(i, PS.HR) / reps * sc:F1}</td></tr>");
			}
			h.Append("</table></div></body></html>");
			File.WriteAllText(path, h.ToString(), Encoding.UTF8);
		}
	}
}
