using System;

namespace Ma9MSim.Fielding
{
	// One fielder's physical tools for a play, from his card defence at the position (xd) and his
	// 주력 (xs), both as x = (stat − 65) / 10 (CardRules.X: half slope above the knee). Outfield
	// range leans on speed as well as defence (user, 2026-10-30: "수비 잘하고 주력이 높은 외야수는
	// 빨리 쫓아가서"); an infielder's first step and hands are mostly his defence.
	public struct FielderSkill
	{
		public double React;      // s before the first step (reading the ball)
		public double Speed;      // top running speed, m/s
		public double Accel;      // m/s²
		public double Route;      // route efficiency (path length × 1/Route)
		public double Reach;      // m covered by the glove without a dive
		public double DiveReach;  // extra m a full dive covers
		public double DiveSkill;  // chance a dive that gets there holds the ball
		public double Arm;        // throw speed, m/s
		public double Release;    // s from fielding to the throw leaving the hand
		public double Hands;      // fielding-error multiplier (1 = league)
		public double ThrowErr;   // throwing-error multiplier
		public double Wall;       // s to play a ball off the wall (reads the carom)
		public double Pivot;      // s to take a throw at second and fire on (double plays)
		public double Xd, Xs;     // the inputs, kept for reports

		// Time to run d metres from a standstill (accelerate to top speed, then cruise), after the
		// reaction; route efficiency stretches the path.
		public double RunTime(double d) => React + FMath.RunTime(d / Route, Speed, Accel);

		// How strongly defence and speed move each tool (reaction, speed, route, dive; arm), as tuned with --fieldlab
		// (2026-10-30).
		public static double OfRange = 0.25, IfRange = 0.55, OfArm = 0.3;
		public static double PivotBase = 1.07, PivotPer10 = 0.03, WallBase = 0.9, ErrPer10 = 0.2;   // --fieldlab plays: double plays near the old engine's 42%

		// The regulars' level those tools were tuned at (the old cards' regulars by main position, 2026-10-30): a
		// regular at today's Defence.RefDefence / RefSpeed plays with exactly the tools a regular had then, so the
		// league's plays (double plays, assists, infield hits, dives) stay as tuned whatever the card scale.
		internal static readonly double[] TunedDefence = { 72.2, 73.0, 76.0, 72.7, 73.0, 75.0, 75.0, 72.4 };
		private static readonly double[] TunedSpeed = { 55.7, 59.5, 73.5, 65.3, 72.4, 70.3, 79.6, 69.9 };

		// How far one card point moves each part of the job from there, by position (C..RF): range (first step, the
		// speed his defence adds, route, dive), arm (throw speed and release; an outfielder's play off the wall),
		// hands (fielding and throwing errors) and the pivot on a double play; SpeedK moves his top speed by 주력.
		// Fitted play by play (--fieldvalue, 2026-11-01) so that 10 points of card defence are worth what 10 points
		// are in the real fielding runs the cards come from (Statiz, a 144-game season: C 1.7, 1B 2.8, 2B 3.8, 3B 3.5,
		// SS 3.1, LF 3.5, CF 3.7, RF 2.6 runs), split as Statiz's 2013+ parts split real fielders: infield range and
		// arm together ~90-95% (the arm decides infield hits), errors ~5%, turning two ~5%; outfield range ~92%, arm
		// ~6%, errors ~2%. (The old rule moved every part alike, infield 0.4, outfield 0.5: a shortstop's 30 points
		// were worth 20 runs, a first baseman's 1.)
		public static readonly double[] RangeK = { 1, 0.672, 0.354, 0.466, 0.234, 0.735, 0.591, 0.556 };
		public static readonly double[] ArmK = { 1, 0.672, 0.354, 0.466, 0.234, 0.111, 0.057, 0.067 };
		public static readonly double[] HandsK = { 1, 0.159, 0.108, 0.075, 0.067, 0.216, 0.185, 0.154 };
		public static readonly double[] PivotK = { 1, 0.4, 0.121, 0.4, 0.089, 0.5, 0.5, 0.5 };
		public static readonly double[] SpeedK = { 1, 0.4, 0.4, 0.4, 0.4, 0.5, 0.5, 0.5 };

		// 주력 changes how a fielder gets there, not how much ground he covers: the real fielding runs a card's defence
		// comes from already hold his legs (2026-11-01). A man faster than his position's regulars runs faster but
		// starts a beat later, a slower one sooner, so both reach a ball LegDist m away at the time his defence alone
		// gives; past that the faster man gains, short of it he loses (--fieldvalue: within 0.1 run a season per 10
		// points of 주력 at every position).
		public static bool LegsNeutral = true;
		public static readonly double[] LegDist = { 0, 3, 6.2, 3, 5.7, 19, 19, 17 };

		// Error rate of each position against the others (fielding and throwing alike, 1 = the base rates): the split
		// of a season's errors among the positions as real regulars make them (Statiz 2013+: 3B 16.3, SS 20.6, 2B 14.5,
		// 1B 9.9, LF 4.0, CF 3.5, RF 4.3 a 144-game season; --fieldvalue counts), the league's total kept.
		public static readonly double[] PosErr = { 1, 1.25, 0.85, 1.34, 1.10, 0.71, 0.58, 0.80, 1 };

		public static FielderSkill For(int pos, double xd, double xs) => For(pos, xd, xd, xd, xd, xs);

		// Each part from its own x (--fieldvalue moves one at a time).
		// xsBase (NaN = xs): the 주력 LegsNeutral evens out. The card's own 주력 since 2026-11-05 (user: "육성한 스피드만 반영"):
		// the start is set for the legs he came with, so 주력 from development runs faster on the same start and adds range.
		public static FielderSkill For(int pos, double xRange, double xArm, double xHands, double xPivot, double xs, double xsBase = double.NaN)
		{
			if (double.IsNaN(xsBase)) xsBase = xs;
			double xd = xRange, xa = xArm, xh = xHands, xp = xPivot, xsRef = xs;
			if (pos <= Geometry.RF)
			{
				double rs = (Defence.RefSpeed[pos] - 65) / 10;
				double td = (TunedDefence[pos] - 65) / 10, ts = (TunedSpeed[pos] - 65) / 10;
				xd = td + RangeK[pos] * (Defence.TunedX(pos, xRange) - td);
				xa = td + ArmK[pos] * (Defence.TunedX(pos, xArm) - td);
				xh = td + HandsK[pos] * (Defence.TunedX(pos, xHands) - td);
				xp = td + PivotK[pos] * (Defence.TunedX(pos, xPivot) - td);
				xs = ts + SpeedK[pos] * (xs - rs);
				xsBase = ts + SpeedK[pos] * (xsBase - rs);
				xsRef = ts;
			}
			else
			{
				xsBase = xs;
			}
			xd = Math.Clamp(xd, -4, 7.5);
			xa = Math.Clamp(xa, -4, 7.5);
			xh = Math.Clamp(xh, -4, 7.5);
			xp = Math.Clamp(xp, -4, 7.5);
			xs = Math.Clamp(xs, -4, 7.5);
			xsBase = Math.Clamp(xsBase, -4, 7.5);
			FielderSkill f = new FielderSkill { Xd = xd, Xs = xs };
			if (Geometry.IsOutfielder(pos))
			{
				double k = OfRange;
				f.React = Math.Clamp(0.45 - 0.04 * k * xd, 0.22, 0.70);
				f.Speed = Math.Clamp(8.3 + (0.28 * xs + 0.10 * xd) * k, 6.8, 9.8);
				f.Accel = 5.0;
				f.Route = Math.Clamp(0.94 + 0.018 * k * xd, 0.86, 0.995);
				if (LegsNeutral)
				{
					double vRef = Math.Clamp(8.3 + (0.28 * xsRef + 0.10 * xd) * k, 6.8, 9.8);
					double vOwn = Math.Clamp(8.3 + (0.28 * xsBase + 0.10 * xd) * k, 6.8, 9.8);
					f.React = Math.Clamp(f.React + FMath.RunTime(LegDist[pos] / f.Route, vRef, f.Accel) - FMath.RunTime(LegDist[pos] / f.Route, vOwn, f.Accel), 0.12, 0.85);
				}
				f.Reach = 1.0;
				f.DiveReach = Math.Clamp(1.6 + 0.15 * k * xd, 0.9, 2.6);
				f.DiveSkill = Math.Clamp(0.62 + 0.06 * k * xd, 0.30, 0.92);
				f.Arm = Math.Clamp(35.0 + 2.0 * OfArm * xa, 28, 44);
				f.Release = Math.Clamp(0.85 - 0.06 * OfArm * xa, 0.55, 1.15);
				f.Wall = Math.Clamp(WallBase - 0.15 * OfArm * xa, 0.2, 1.5);
				f.Pivot = 0.8;
			}
			else if (pos == Geometry.C)
			{
				f.React = 0.35; f.Speed = Math.Clamp(6.8 + 0.2 * xs, 5.8, 7.8); f.Accel = 4.5; f.Route = 0.95;
				f.Reach = 1.0; f.DiveReach = 1.0; f.DiveSkill = 0.5;
				f.Arm = Math.Clamp(34.0 + 1.5 * xa, 28, 40); f.Release = Math.Clamp(0.75 - 0.05 * xa, 0.5, 1.0);
				f.Wall = 1.0; f.Pivot = 0.8;
			}
			else if (pos == Geometry.Pit)
			{
				f.React = 0.30; f.Speed = 6.8; f.Accel = 5.0; f.Route = 0.95;
				f.Reach = 0.9; f.DiveReach = 0.8; f.DiveSkill = 0.35;
				f.Arm = 30; f.Release = 0.65; f.Wall = 1.0; f.Pivot = 0.8;
			}
			else
			{
				// Getting to a ball in the hole is legs and defence (user, 2026-10-30: "유간 타구 … 뛰어가서
				// 잡을 수 있는가 << 스피드랑 수비"); dive, hands and arm are defence; a strong arm matters
				// mostly at third and short, where the throws are long.
				double k = IfRange;
				f.React = Math.Clamp(0.20 - 0.025 * k * xd, 0.08, 0.32);
				f.Speed = Math.Clamp(7.0 + (0.25 * xs + 0.20 * xd) * k, 5.8, 8.6);
				f.Accel = 6.0;
				f.Route = 0.97;
				if (LegsNeutral)
				{
					double vRef = Math.Clamp(7.0 + (0.25 * xsRef + 0.20 * xd) * k, 5.8, 8.6);
					double vOwn = Math.Clamp(7.0 + (0.25 * xsBase + 0.20 * xd) * k, 5.8, 8.6);
					f.React = Math.Clamp(f.React + FMath.RunTime(LegDist[pos] / f.Route, vRef, f.Accel) - FMath.RunTime(LegDist[pos] / f.Route, vOwn, f.Accel), 0.03, 0.45);
				}
				f.Reach = 1.2;
				f.DiveReach = Math.Clamp(1.5 + 0.15 * k * xd, 0.8, 2.4);
				f.DiveSkill = Math.Clamp(0.55 + 0.07 * k * xd, 0.25, 0.90);
				double arm0 = pos == Geometry.B1 ? 30 : pos == Geometry.B2 ? 31 : 34;
				double armPer = pos == Geometry.B1 ? 0.6 : pos == Geometry.B2 ? 1.0 : 1.8;
				f.Arm = Math.Clamp(arm0 + armPer * xa, 25, 42);
				f.Release = Math.Clamp(0.55 - 0.05 * xa, 0.32, 0.80);
				f.Wall = 1.0;
				f.Pivot = Math.Clamp(PivotBase - PivotPer10 * xp, 0.6, 1.5);
			}
			// Error rates per chance (fielding and throwing), around the regulars' level.
			f.Hands = PosErr[pos] * Math.Exp(-ErrPer10 * xh);
			f.ThrowErr = PosErr[pos] * Math.Exp(-ErrPer10 * xh);
			return f;
		}
	}

	// A runner's legs from 주력 (xs). Average home to first is 4.30 s from contact for a right-handed
	// batter (a left-handed one starts a step closer, −0.08 s).
	public struct RunnerSkill
	{
		public double Speed;   // top speed, m/s
		public double Accel;
		public double Turn;    // s lost rounding a base
		public double Xs;

		public static RunnerSkill For(double xs)
		{
			xs = Math.Clamp(xs, -4, 7.5);
			return new RunnerSkill
			{
				Xs = xs,
				Speed = Math.Clamp(8.5 + 0.33 * xs, 7.0, 10.2),
				Accel = 5.0,
				Turn = Math.Clamp(0.30 - 0.03 * xs, 0.15, 0.45)
			};
		}

		public double Run(double d) => FMath.RunTime(d, Speed, Accel);

		// Run d metres with a flying start at speed v0 (a runner already moving).
		public double RunFrom(double d, double v0) => FMath.RunTime(d, Speed, Accel, v0);
	}

	public static partial class FMath
	{
		// Time to cover d metres starting at speed v0, accelerating at a to top speed v.
		public static double RunTime(double d, double v, double a, double v0 = 0)
		{
			if (d <= 0) return 0;
			v0 = Math.Min(v0, v);
			double ta = (v - v0) / a;
			double da = (v * v - v0 * v0) / (2 * a);
			if (d <= da)
			{
				return (-v0 + Math.Sqrt(v0 * v0 + 2 * a * d)) / a;
			}
			return ta + (d - da) / v;
		}

		// How far a man gets in t seconds (inverse of RunTime).
		public static double RunDist(double t, double v, double a, double v0 = 0)
		{
			if (t <= 0) return 0;
			v0 = Math.Min(v0, v);
			double ta = (v - v0) / a;
			if (t <= ta) return v0 * t + 0.5 * a * t * t;
			return (v * v - v0 * v0) / (2 * a) + v * (t - ta);
		}

		// Standard normal CDF (Abramowitz-Stegun 7.1.26, |err| < 1.5e-7).
		public static double Phi(double z)
		{
			double t = 1 / (1 + 0.3275911 * Math.Abs(z) / Math.Sqrt(2));
			double y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z / 2);
			return z >= 0 ? 0.5 * (1 + y) : 0.5 * (1 - y);
		}
	}
}
