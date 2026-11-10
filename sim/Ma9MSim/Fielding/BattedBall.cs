using System;

namespace Ma9MSim.Fielding
{
	// Statcast's batted-ball types by launch angle: ground ball under 10°, line drive 10-25°, fly
	// ball 25-50°, pop-up above 50°.
	public enum BallType : byte { Ground, Line, Fly, Popup }

	public struct BattedBall
	{
		public float Ev;   // exit velocity, m/s
		public float La;   // launch angle, degrees
		public float H;    // spray angle, degrees (0 = first-base line, 90 = third-base line)

		public BallType Type => La < 10 ? BallType.Ground : La < 25 ? BallType.Line : La < 50 ? BallType.Fly : BallType.Popup;

		public double Mph => Ev / 0.44704;

		public override string ToString() => $"{Mph:F0}mph {La:F0}° h{H:F0} {Type}";

		// The same ball from the other side of the plate (a left-handed batter's mirror image).
		public BattedBall Mirror() => new BattedBall { Ev = Ev, La = La, H = 90 - H };
	}

	// League batted balls (balls in play and home runs together) for a right-handed batter. A
	// left-handed batter's are the mirror image. Shapes follow public Statcast distributions:
	// launch angle ~ N(11°, 27°); exit velocity peaks at 15° (~92 mph) and falls off both ways; a
	// right-handed batter pulls ground balls toward third (spray > 45) more than fly balls.
	// The spread (2026-11-01): wide enough that the real KBO's fielders get their real chances, with the cards'
	// own batting and throwing hands (--fieldvalue, a season: grounders 1B 207 / 2B 388 / 3B 286 / SS 429 against
	// Statiz 2013+ regulars' ~200 / 384 / 283 / 407 from their assists, errors and DP relays; catches LF 373 /
	// CF 481 / RF 361, the centre fielder 1.3 times a corner man as Statiz's putouts have him, 1.24). It was
	// 21° / 19°, which piled the balls up the middle (centre fielder 2.0 times a corner man).
	public sealed class BallGenerator
	{
		public double LaMean = 11, LaSd = 27, EvPeak = 41.0, EvCurve = 0.0035, EvSd = 5.6, EvMin = 13, EvMax = 52.5;
		public double PullGround = 14, PullLine = 7, PullFly = 3, SprayGround = 28, SprayAir = 40;

		// Tests: the league library's generator (BipLibrary.Shared), its settings moved by --pullground and the like.
		public static BallGenerator Template;

		public BattedBall Next(ref Rng rng)
		{
			while (true)
			{
				double la = LaMean + LaSd * Normal(ref rng);
				if (la < -45 || la > 85)
				{
					continue;
				}
				double ev = EvPeak - EvCurve * (la - 15) * (la - 15) + EvSd * Normal(ref rng);
				if (ev < EvMin || ev > EvMax)
				{
					continue;
				}
				bool ground = la < 10;
				double pull = ground ? PullGround : la < 25 ? PullLine : la < 50 ? PullFly : 0;
				double h = 45 + pull + (ground ? SprayGround : SprayAir) * Normal(ref rng);
				if (!Geometry.Fair(h))
				{
					continue;   // foul: not a ball in play
				}
				return new BattedBall { Ev = (float)ev, La = (float)la, H = (float)Math.Clamp(h, 0, 90) };
			}
		}

		public static double Normal(ref Rng rng)
		{
			double u1 = Math.Max(1e-12, rng.Next()), u2 = rng.Next();
			return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
		}
	}
}
