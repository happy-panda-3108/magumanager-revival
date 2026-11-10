using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	// Per-plate-appearance talent against a league-average opponent.
	public sealed class BatterTalent
	{
		public double K, BB, HBP, HR;   // per PA
		public double Babip;            // hits / balls in play (non-HR)
		public double Double, Triple;   // share of non-HR hits
		public double Steal;            // steal attempts per time on first
		public double Sac;              // sacrifices (bunts + flies) per PA
		public double Speed = 1.0;      // baserunning multiplier around 1
	}

	public sealed class PitcherTalent
	{
		public double K, BB, HBP, HR;   // per batter faced
		public double Babip;
	}

	// League environment of one season (from the real batting totals).
	public sealed class League
	{
		public double K, BB, HBP, HR, Babip, Double, Triple, Steal, Sac;

		public static League From(Season s)
		{
			double pa = 0, ab = 0, h = 0, d = 0, t = 0, hr = 0, bb = 0, so = 0, hbp = 0, sb = 0;
			foreach (BatterLine b in s.Batters.Where(b => !b.Replacement))
			{
				pa += b.PA; ab += b.AB; h += b.H; d += b.D; t += b.T; hr += b.HR; bb += b.BB; so += b.SO; hbp += b.HBP; sb += b.SB;
			}
			double hitsInPlay = h - hr;
			return new League
			{
				K = so / pa, BB = bb / pa, HBP = Math.Max(hbp, 1) / pa, HR = hr / pa,
				Babip = hitsInPlay / Math.Max(1, ab - so - hr),
				Double = d / Math.Max(1, hitsInPlay), Triple = Math.Max(t, 1) / Math.Max(1, hitsInPlay),
				Steal = sb / Math.Max(1, h - d - t - hr + bb + hbp),
				Sac = Math.Max(1, pa - ab - bb - hbp) / pa
			};
		}
	}

	// Baserunning and batted-ball constants. RunEnvironment scales how aggressively runners
	// advance; it is fitted per season to the real runs per game.
	public sealed class EngineParams
	{
		public double GroundShare = 0.47;
		public double FlyShare = 0.36;
		public double DoublePlay = 0.42;
		public double GroundAdvance = 0.50;
		public double SacFly = 0.60;
		public double Fly2To3 = 0.25;
		public double Single2ToHome = 0.60;
		public double Single1To3 = 0.27;
		public double Double1ToHome = 0.42;
		public double ReachOnError = 0.030;
		public double StealSuccess = 0.70;
		public double RunEnvironment = 1.0;
		// Share of runs that are earned. The records carry total runs next to earned runs for
		// 2014, 2015, 2018, 2024 and 2025: 0.89-0.92. Errors are tuned to it.
		public double EarnedShare = 0.91;

		public EngineParams Clone()
		{
			return (EngineParams)MemberwiseClone();
		}
	}

	public static class Log5
	{
		// Odds-ratio combination of a batter rate and a pitcher rate around the league rate.
		public static double Combine(double batter, double pitcher, double league)
		{
			double odds = batter / (1 - batter) * (pitcher / (1 - pitcher)) / (league / (1 - league));
			return odds / (1 + odds);
		}
	}

	public static class TalentInit
	{
		// Observed rates shrunk toward the league by a few dozen PA, so a 3-PA cameo does not
		// start as a .667 hitter.
		public static BatterTalent FromRecord(BatterLine b, League lg)
		{
			if (b.Replacement)
			{
				// Unrecorded batters are mostly bench players: a little below league average.
				return new BatterTalent
				{
					K = lg.K * 1.08, BB = lg.BB * 0.92, HBP = lg.HBP, HR = lg.HR * 0.75, Babip = lg.Babip * 0.97,
					Double = lg.Double, Triple = lg.Triple, Steal = lg.Steal * 0.8, Sac = lg.Sac
				};
			}
			const double m = 60;
			double pa = b.PA;
			double bip = Math.Max(0, b.AB - b.SO - b.HR);
			double hip = Math.Max(0, b.H - b.HR);
			double onFirst = Math.Max(0, b.Singles + b.BB + b.HBP);
			BatterTalent t = new BatterTalent
			{
				K = Shrink(b.SO, pa, lg.K, m),
				BB = Shrink(b.BB, pa, lg.BB, m),
				HBP = Shrink(b.HBP, pa, lg.HBP, m),
				HR = Shrink(b.HR, pa, lg.HR, m),
				Babip = Shrink(hip, bip, lg.Babip, 120),
				Double = Shrink(b.D, hip, lg.Double, 40),
				Triple = Shrink(b.T, hip, lg.Triple, 40),
				Steal = Shrink(b.SB, onFirst, lg.Steal, 30),
				Sac = Shrink(Math.Max(0, b.PA - b.AB - b.BB - b.HBP), pa, lg.Sac, m) * 0.7
			};
			t.Speed = Math.Clamp(Math.Pow(t.Triple / lg.Triple, 0.25) * Math.Pow(t.Steal / lg.Steal, 0.2), 0.7, 1.35);
			return t;
		}

		public static PitcherTalent FromRecord(PitcherLine p, League lg)
		{
			if (p.Replacement && p.H + p.SO == 0)
			{
				return new PitcherTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip };
			}
			const double m = 80;
			double bf = p.BF;
			double bip = Math.Max(0, bf - p.SO - p.BB - p.HBP - p.HR);
			return new PitcherTalent
			{
				K = Shrink(p.SO, bf, lg.K, m),
				BB = Shrink(p.BB, bf, lg.BB, m),
				HBP = Shrink(p.HBP, bf, lg.HBP, m),
				HR = Shrink(p.HR, bf, lg.HR, m),
				Babip = Shrink(Math.Max(0, p.H - p.HR), bip, lg.Babip, 150)
			};
		}

		private static double Shrink(double count, double n, double league, double m)
		{
			return Math.Clamp((count + m * league) / (n + m), 0.0005, 0.95);
		}
	}
}
