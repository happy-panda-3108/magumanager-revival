using System;

namespace Ma9MSim.Fielding
{
	// 정신 rework (user, 2026-10-30): the higher-정신 side still plays (difference / 10)% above its
	// card, now in every plate appearance but scaled by how much the moment matters (a leverage
	// index: late, close, men on), and by K. Big in a one-run ninth or a tie with runners on, close
	// to nothing in a blowout, so a cleanup hitter who comes up with men on uses it most. Base stats
	// still decide most of a matchup: a few percent on every rate only tips it when the two are close.
	public static class Clutch
	{
		public static bool New = FieldEngine.On;
		public static double K = 1.7;   // --statvalue 2026-10-30: lineup 정신 +10 ≈ +3.6%p win (컨택 +10.2), staff ≈ +3.8%p (구위 +6.7)
		// Normalises Leverage so its plate-appearance mean is about 1 (--clutchtest).
		public static double Norm = 0.575;   // mean raw leverage over card games, 2026-10-30
		public static bool Track;          // tests: accumulate the mean leverage
		public static double SumL;
		public static long CountL;

		public static double Leverage(int inning, int lead, bool on1, bool on2, bool on3, int outs)
		{
			// Base-out: a man in scoring position makes the at-bat count more, two outs a little more yet.
			double bo = 0.80 + (on1 ? 0.25 : 0) + (on2 ? 0.40 : 0) + (on3 ? 0.40 : 0);
			if (outs == 2 && (on2 || on3)) bo *= 1.15;
			// Score and inning: the closer and later, the more it weighs (a tie in the 9th or later most).
			int inn = Math.Min(inning, 10);
			double d = Math.Abs(lead);
			double late = 0.55 + 0.13 * inn;                // 1st 0.68 … 9th 1.72
			double close = Math.Exp(-d * (0.18 + 0.06 * inn)); // 9th: 1 run 0.48 of a tie, 3 runs 0.11
			// The batting side behind by one or two has the tying run at the plate: a bit more.
			if (lead < 0 && d <= 2 && inn >= 7) close *= 1.15;
			return bo * late * close / Norm;
		}
	}
}
