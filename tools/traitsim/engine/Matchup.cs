using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	// Per-PA multipliers on the Log5 rates beyond batter-vs-pitcher talent: park, platoon,
	// the pitch that decides the plate appearance, and the fielders behind the pitcher.
	public sealed class MatchupParams
	{
		public bool Platoon = true;
		public bool PitchMix = true;
		public bool Defence = true;

		// Platoon split: log(rate vs same-side pitcher / rate vs opposite-side pitcher).
		// Generic values by batter side (MLB-scale splits; the records hold no per-player splits).
		public double[] RightySplit = { 0.12, -0.10, -0.15, -0.02 };   // K, BB, HR, BABIP
		public double[] LeftySplit = { 0.14, -0.12, -0.16, -0.02 };
		public double LowSlotSplit = 1.4;   // sidearm / submarine arms have wider splits

		// Pitch usage (user's rule): the main fastball (better of four-seam / two-seam) and the best
		// off-speed pitch about 1:1; every other pitch weighs OtherPitchWeight x (rating / partner)^4.
		public double OtherPitchWeight = 0.35;
		public double OtherPitchPower = 4.0;

		// Effect of the deciding pitch's rating relative to the pitcher's usage-weighted mean,
		// per 10 rating points (log scale).
		public double PitchK = 0.12, PitchHR = -0.10, PitchBabip = -0.03;

		public MatchupParams Clone()
		{
			return (MatchupParams)MemberwiseClone();
		}
	}

	public sealed class PitchMix
	{
		public PitchSlot[] Slots;
		public double[] Usage;      // shares, sum 1
		public double[] Cumulative;
		public double[] K, HR, Babip, Ground;

		// Batted-ball and whiff tendencies by family (log multipliers), seeded from MLB pitch-type
		// norms: sinkers and splitters keep the ball down, sliders and splitters miss bats,
		// four-seamers get fly balls. Normalised per pitcher so the mix only redistributes his
		// strikeouts and homers; the ground-ball lean is kept (it drives double plays).
		private static readonly Dictionary<PitchFamily, (double k, double hr, double gb)> Traits = new Dictionary<PitchFamily, (double, double, double)>
		{
			{ PitchFamily.Fastball, (-0.05, 0.05, -0.20) },
			{ PitchFamily.Sinker, (-0.20, -0.25, 0.30) },
			{ PitchFamily.Cutter, (0.00, -0.05, 0.05) },
			{ PitchFamily.Slider, (0.12, 0.00, 0.00) },
			{ PitchFamily.Curve, (0.08, -0.05, 0.10) },
			{ PitchFamily.Split, (0.20, -0.10, 0.25) },
			{ PitchFamily.Change, (0.08, 0.00, 0.12) },
			{ PitchFamily.Knuckle, (0.00, 0.05, 0.00) },
		};

		public static PitchMix Build(PitchSlot[] slots, MatchupParams mp)
		{
			if (slots == null || slots.Length == 0)
			{
				return null;
			}
			int n = slots.Length;
			double[] w = UsageWeights(slots, mp);
			double sum = w.Sum();
			PitchMix mix = new PitchMix { Slots = slots, Usage = w.Select(x => x / sum).ToArray(), Cumulative = new double[n], K = new double[n], HR = new double[n], Babip = new double[n], Ground = new double[n] };
			double acc = 0;
			for (int i = 0; i < n; i++)
			{
				acc += mix.Usage[i];
				mix.Cumulative[i] = acc;
			}
			mix.Cumulative[n - 1] = 1.0;
			double mean = Enumerable.Range(0, n).Sum(i => mix.Usage[i] * slots[i].Rating);
			double zk = 0, zhr = 0, zb = 0;
			for (int i = 0; i < n; i++)
			{
				double d = (slots[i].Rating - mean) / 10;
				(double tk, double thr, double tgb) = Traits[slots[i].Family];
				mix.K[i] = Math.Exp(mp.PitchK * d + tk);
				mix.HR[i] = Math.Exp(mp.PitchHR * d + thr);
				mix.Babip[i] = Math.Exp(mp.PitchBabip * d);
				mix.Ground[i] = Math.Exp(tgb);
				zk += mix.Usage[i] * mix.K[i];
				zhr += mix.Usage[i] * mix.HR[i];
				zb += mix.Usage[i] * mix.Babip[i];
			}
			for (int i = 0; i < n; i++)
			{
				mix.K[i] /= zk;
				mix.HR[i] /= zhr;
				mix.Babip[i] /= zb;
			}
			return mix;
		}

		private static double[] UsageWeights(PitchSlot[] slots, MatchupParams mp)
		{
			int n = slots.Length;
			double[] w = new double[n];
			bool IsFastball(PitchSlot s) => s.Family == PitchFamily.Fastball || s.TwoSeam;
			int fastball = -1, partner = -1;
			for (int i = 0; i < n; i++)
			{
				if (IsFastball(slots[i]) && (fastball < 0 || slots[i].Rating > slots[fastball].Rating || (slots[i].Rating == slots[fastball].Rating && !slots[i].TwoSeam)))
				{
					fastball = i;
				}
			}
			if (fastball < 0)
			{
				fastball = Enumerable.Range(0, n).OrderByDescending(i => slots[i].Rating).First();
			}
			// The partner is the best off-speed pitch; another fastball or a cutter only if nothing else.
			for (int pass = 0; pass < 2 && partner < 0; pass++)
			{
				for (int i = 0; i < n; i++)
				{
					bool offSpeed = !IsFastball(slots[i]) && slots[i].Family != PitchFamily.Cutter;
					if (i != fastball && (pass == 1 || offSpeed) && (partner < 0 || slots[i].Rating > slots[partner].Rating))
					{
						partner = i;
					}
				}
			}
			for (int i = 0; i < n; i++)
			{
				w[i] = i == fastball || i == partner ? 1.0 : partner >= 0 ? mp.OtherPitchWeight * Math.Pow(slots[i].Rating / slots[partner].Rating, mp.OtherPitchPower) : 0;
			}
			return w;
		}

		// 투수 성향: the fastballs' usage times w, renormalised; each pitch's effect stays, so leaning
		// on the better pitches pays and leaning on the worse ones costs.
		public PitchMix Lean(double w)
		{
			if (w == 1) return this;
			PitchMix x = (PitchMix)MemberwiseClone();
			int n = Slots.Length;
			double[] u = new double[n];
			for (int i = 0; i < n; i++) u[i] = Usage[i] * (Slots[i].Family == PitchFamily.Fastball || Slots[i].TwoSeam ? w : 1);
			double sum = u.Sum();
			x.Usage = u.Select(v => v / sum).ToArray();
			x.Cumulative = new double[n];
			double acc = 0;
			for (int i = 0; i < n; i++)
			{
				acc += x.Usage[i];
				x.Cumulative[i] = acc;
			}
			x.Cumulative[n - 1] = 1.0;
			return x;
		}

		public int Pick(double u)
		{
			for (int i = 0; i < Cumulative.Length; i++)
			{
				if (u < Cumulative[i])
				{
					return i;
				}
			}
			return Cumulative.Length - 1;
		}
	}

	public static class Platoon
	{
		// Pitcher type: 0 R, 1 R low slot, 2 L, 3 L low slot.
		public static int TypeOf(Thrower t)
		{
			return (t.Throws == Hand.L ? 2 : 0) + (t.LowSlot ? 1 : 0);
		}

		// [batter side, pitcher type, component] multipliers. Double-centred on the season's real
		// mix of batters and pitchers: averaged over the pitchers he faces a hitter's multiplier is
		// one, and so is a pitcher's over the hitters he faces, so both fitted talents stay their
		// overall level and only the split itself is added.
		public static double[,,] Build(double[] sideShare, double[] typeShare, MatchupParams mp)
		{
			double[,,] f = new double[3, 4, 4];
			for (int c = 0; c < 4; c++)
			{
				double[,] l = new double[3, 4];
				for (int side = 0; side < 3; side++)
				{
					double[] split = side == (int)Hand.L ? mp.LeftySplit : mp.RightySplit;
					for (int t = 0; t < 4; t++)
					{
						bool lefty = t >= 2, low = (t & 1) == 1;
						// Switch hitters always take the platoon edge; their small residual split is ignored.
						double sign = side == (int)Hand.S ? 0 : ((side == (int)Hand.L) == lefty ? 0.5 : -0.5);
						l[side, t] = sign * split[c] * (low ? mp.LowSlotSplit : 1.0);
					}
				}
				for (int it = 0; it < 30; it++)
				{
					for (int side = 0; side < 3; side++)
					{
						double mean = 0;
						for (int t = 0; t < 4; t++) mean += typeShare[t] * l[side, t];
						for (int t = 0; t < 4; t++) l[side, t] -= mean;
					}
					for (int t = 0; t < 4; t++)
					{
						double mean = 0;
						for (int side = 0; side < 3; side++) mean += sideShare[side] * l[side, t];
						for (int side = 0; side < 3; side++) l[side, t] -= mean;
					}
				}
				for (int side = 0; side < 3; side++)
				{
					for (int t = 0; t < 4; t++)
					{
						f[side, t, c] = Math.Exp(l[side, t]);
					}
				}
			}
			return f;
		}
	}

	public static class Defence
	{
		// Reach-on-error multiplier per club from its fielders' fielding percentage (games-
		// weighted). Only seasons whose records carry fielding percentage get one.
		public static double[] ErrorMultipliers(Season s)
		{
			double[] mult = Enumerable.Repeat(1.0, s.Teams.Count).ToArray();
			List<(double fpct, double g)>[] teams = s.Teams.Select(t => t.Batters.Where(b => !double.IsNaN(b.Fpct) && b.G > 0).Select(b => (b.Fpct, (double)b.G)).ToList()).ToArray();
			if (teams.Any(t => t.Sum(x => x.g) < 300))
			{
				return mult;
			}
			double[] err = teams.Select(t => t.Sum(x => (1 - x.fpct) * x.g) / t.Sum(x => x.g)).ToArray();
			double league = err.Average();
			for (int i = 0; i < mult.Length; i++)
			{
				mult[i] = Math.Clamp(err[i] / league, 0.6, 1.5);
			}
			return mult;
		}
	}

	// Card mode: the fielders and runners by position. Stats enter as x = (stat − 65) / 10; above
	// 95 (training) a stat keeps half its slope, like every other card rule.
	public sealed class FieldRules
	{
		// Catcher: caught-stealing share × (1 + 0.10 x). A league-average 30% becomes 39% at 95
		// and 22% at 40 (a third or better is a top catcher).
		public double CaughtPer10 = 0.10;
		// Pitcher 구속: steal attempts × (1 − 0.06 x).
		public double VeloAttemptPer10 = -0.06;
		// Singles into the left-centre gap with first base open: only a genuinely fast runner who is
		// also clearly faster than the fielders' arms goes for second. 주력 from 80 (full at 90),
		// and margin = (주력 − LF/CF 수비) / 10 from +1 (full at +3). Safe 75% at a margin of +2,
		// 4%p more or less per 10 points, at most 88% (about 80% overall).
		public double GapSingle = 0.12;
		public double StretchRunFrom = 1.5, StretchRunFull = 2.5;
		public double StretchFrom = 1.0, StretchFull = 3.0, StretchTryMax = 0.6;
		public double StretchSafeAt2 = 0.75, StretchSafePer10 = 0.04, StretchSafeMax = 0.88;
		// League share of singles stretched into doubles: the card's double share already holds
		// them, so every batter's clean doubles are reduced by it.
		public double AverageStretch = 0.004;
		// Right-centre: triples × (1 − 0.08 x) by the RF/CF defence; the rest are held to doubles.
		public double TriplePer10 = -0.08;
		// A catcher above 90 (shown defence) lifts the pitcher's 컨트롤·테크닉·구위 by 0.15% per
		// point over 90 (91 → 0.15%, 100 → 1.5%, 140 → 7.5%).
		public double CatcherBoostFrom = 90, CatcherBoostPerPoint = 0.15;
		// What-if: every catcher at this defence (null = the cards).
		public double? CatcherOverride;

		public static double X(double x) => x > 3 ? 3 + 0.5 * (x - 3) : x;

		public static double Lin(double per10, double x) => 1 + per10 * X(x);

		public double CaughtMult(double catcherX) => Math.Max(0.3, Lin(CaughtPer10, catcherX));

		public double AttemptMult(double veloX) => Math.Max(0.3, Lin(VeloAttemptPer10, veloX));

		public double TryP(double runX, double defX) =>
			StretchTryMax * Math.Clamp((X(runX) - StretchRunFrom) / (StretchRunFull - StretchRunFrom), 0, 1)
				* Math.Clamp((X(runX) - X(defX) - StretchFrom) / (StretchFull - StretchFrom), 0, 1);

		public double SafeP(double runX, double defX) =>
			Math.Clamp(StretchSafeAt2 + StretchSafePer10 * (X(runX) - X(defX) - 2), 0.5, StretchSafeMax);

		public double TripleKeep(double defX) => Math.Max(0.2, Lin(TriplePer10, defX));

		public double CatcherBoost(double defence) => defence > CatcherBoostFrom ? (defence - CatcherBoostFrom) * CatcherBoostPerPoint : 0;
	}
}
