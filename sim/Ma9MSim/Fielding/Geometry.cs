using System;

namespace Ma9MSim.Fielding
{
	// The field in the 3D broadcast's frame (client Ball.cs / FielderManager): home plate at the
	// origin, x along the first-base line, z along the third-base line, metres. A spray angle h is
	// measured from the first-base line (0) to the third-base line (90); 45 is straight away.
	public static class Geometry
	{
		public const double BaseLine = 27.43;
		public const double MoundDist = 18.44;

		public struct P
		{
			public double X, Z;
			public P(double x, double z) { X = x; Z = z; }
			public double Dist(P o) => Math.Sqrt((X - o.X) * (X - o.X) + (Z - o.Z) * (Z - o.Z));
			public double R => Math.Sqrt(X * X + Z * Z);
			public double H => Math.Atan2(Z, X) * 180 / Math.PI;
			public static P Polar(double r, double hDeg)
			{
				double a = hDeg * Math.PI / 180;
				return new P(r * Math.Cos(a), r * Math.Sin(a));
			}
			public override string ToString() => $"({X:F1},{Z:F1})";
		}

		// Bases by number: 0 home, 1 first, 2 second, 3 third.
		public static readonly P[] Base =
		{
			new P(0, 0), new P(BaseLine, 0), new P(BaseLine, BaseLine), new P(0, BaseLine)
		};

		// Deck positions as eDeckFielder / ePos minus one: 0 C, 1 1B, 2 2B, 3 3B, 4 SS, 5 LF, 6 CF, 7 RF;
		// the pitcher is 8 here (the deck keeps him apart).
		public const int C = 0, B1 = 1, B2 = 2, B3 = 3, SS = 4, LF = 5, CF = 6, RF = 7, Pit = 8, FielderCount = 9;
		public static readonly string[] PosName = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "P" };

		// Where each man stands before the pitch (FielderManager's start positions; the pitcher
		// finishes his delivery a stride toward the plate).
		public static readonly P[] Start =
		{
			new P(-1.5, -1.5), new P(30.41, 6.36), new P(34.65, 21.92), new P(6.36, 30.41), new P(21.92, 34.65),
			new P(24.04, 79.2), new P(60.1, 60.1), new P(79.2, 24.04), new P(12.2, 12.2)
		};

		public static bool IsInfielder(int pos) => pos >= B1 && pos <= SS;

		public static bool IsOutfielder(int pos) => pos >= LF && pos <= RF;

		// The reference park the outcomes are decided in (league-neutral, like the card games'
		// neutral park factors): 98 m down the lines, 113 m in the power alleys, 122 m to centre,
		// a 3 m wall. The broadcast maps a ball to the real park's fence at the same angle.
		public const double LineFence = 98, AlleyFence = 113, CentreFence = 122, WallHeight = 3.0;

		public static double FenceAt(double hDeg)
		{
			double d = Math.Abs(Math.Clamp(hDeg, 0, 90) - 45);   // 0 centre … 45 line
			if (d >= 22.5)
			{
				double f = (d - 22.5) / 22.5;
				return AlleyFence + (LineFence - AlleyFence) * f;
			}
			double g = d / 22.5;
			return CentreFence + (AlleyFence - CentreFence) * g * g;
		}

		// Fair territory (spray 0-90), with a sliver of tolerance for the chalk.
		public static bool Fair(double hDeg) => hDeg >= -0.5 && hDeg <= 90.5;
	}
}
