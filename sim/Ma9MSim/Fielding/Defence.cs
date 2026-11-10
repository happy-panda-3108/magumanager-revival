using System;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// The fielding side for one play: each man's tools and where he stands.
	public sealed class Defence
	{
		public readonly FielderSkill[] F = new FielderSkill[Geometry.FielderCount];
		public readonly P[] Pos = (P[])Geometry.Start.Clone();

		// The reference fielders the batted-ball library is played against: the regulars' mean card
		// at each main position (defence there, 주력), from the card tables (--gametest; 2026-11-01:
		// the cards' defence from the real fielding runs, Statiz). The game's league reference holds
		// a league of regulars at this level, so cards replay their records against it; better or
		// worse fielders move results from here (FielderSkill: a regular plays with the tools tuned).
		// 2026-11-02 04시 (cards_live, user: "주전 수비수들은 최저값이 40이 아니라 50으로 … 후보들 … 40~평균 아래"): regular fielders
		// on 50/73/95 and the rest on 40/56/72, each season by its whole contribution; the values below are the 1999-2025
		// batting regulars' means at their main position (주력 before the decade stretch).
		// 2026-11-02 (cards_v6, user: "보정을 다 고려했을 때 수비 잘하는 선수를 가리자는거야"): every season's fielders in one
		// line by their whole contribution (Statiz fielding runs + positional adjustment; the last 40, the regulars' mean
		// 63, the first 95), so one card point is the same runs at every position; a shortstop's regulars stand near 80,
		// a first baseman's near 50. Statiz batting lines for 주력.
		public static readonly double[] RefDefence = { 83.6, 57.2, 77.4, 71.3, 84.5, 61.6, 71.3, 63.5 };
		public static readonly double[] RefSpeed = { 54.9, 58.6, 72.9, 64.4, 71.4, 69.1, 78.7, 69.6 };
		// Batters' mean 주력 (regulars): the reference batter-runner. (cards_v6: 1999-2025 regulars, on the one scale of
		// 1982-2026.)
		public const double RefBatterSpeed = 67.3;
		public static double RefRunX => (RefBatterSpeed - 65) / 10;

		// Points of the cards the tools were tuned with (2026-11-01) that one card point stands for at each position: runs a
		// point is worth now over then (--fieldvalue). The tools (FielderSkill) and the plays outside the fielding engine
		// (steals; errors and the gaps when it is off; the tactics' team defence) all read a card through TunedX.
		// cards_live (50/73/95): C 8.2, 1B 7.3, 2B 8.0, 3B 7.9, SS 8.0, LF 7.3, CF 7.6, RF 7.3 runs per 10 points, set play by play
		// (--fieldvalue, Ref ± 10, two passes; within 0.25 of these).
		// cards_v6: C 6.1, 1B 7.1, 2B 6.3, 3B 6.6, SS 5.9, LF 7.1, CF 6.4, RF 6.9 runs per 10 points (each regular's season
		// scale slope × 144 / his fielding games), C 1.7 … RF 2.6 then; set play by play (--fieldvalue, Ref ± 10: C 6.1,
		// 1B 7.1, 2B 6.3, 3B 6.6, SS 6.0, LF 7.0, CF 6.2, RF 7.0).
		public static readonly double[] PointK = { 3.90, 2.88, 2.09, 2.08, 2.47, 1.79, 1.90, 2.76 };

		// x = (stat − 65) / 10 of the tuned card with the same standing at the position: a regular here reads as the
		// regular the tools were tuned at, and each point away from him as PointK of theirs.
		public static double TunedX(int pos, double x) => (FielderSkill.TunedDefence[pos] - 65) / 10 + PointK[pos] * (x - (RefDefence[pos] - 65) / 10);

		public static Defence Average()
		{
			Defence d = new Defence();
			for (int p = 0; p < Geometry.FielderCount; p++)
			{
				d.F[p] = p < 8 ? FielderSkill.For(p, (RefDefence[p] - 65) / 10, (RefSpeed[p] - 65) / 10) : FielderSkill.For(p, 0, 0);
			}
			return d;
		}

		public void Set(int pos, double xd, double xs) => F[pos] = FielderSkill.For(pos, xd, xs);

		// xsBase: his card's own 주력 (development on top runs faster with the same start, so it adds range).
		public void Set(int pos, double xd, double xs, double xsBase) => F[pos] = FielderSkill.For(pos, xd, xd, xd, xd, xs, xsBase);

		// Throw from point q to a base: straight, or through a cutoff man when that is quicker on a
		// long throw (he stands RelayFromBase m out from the base on the line, catches, turns, fires).
		public const double RelayFromBase = 36, RelayTransfer = 0.55, RelayMin = 58;

		// Outfield throws lose speed with distance (an arc, often a hop): at OfDecay 0.0022 a 60 m
		// throw averages 87% of the arm, an 85 m one 81%. OfExtra is the gather and crow hop.
		public static double OfDecay = 0.0022, OfExtra = 0.65;   // --fieldlab plays grid, then --gametest fit with NeedHome/NeedThird, 2026-10-30

		// This defence's gather and crow hop (OfExtra, moved by the league's run environment: see
		// SeasonSimulator.BuildDefence).
		public double Extra = OfExtra;

		public double ThrowSecs(int thrower, double d)
		{
			FielderSkill f = F[thrower];
			if (!Geometry.IsOutfielder(thrower)) return d / f.Arm;
			return Extra + d / (f.Arm * Math.Max(0.6, 1 - OfDecay * d));
		}

		public (double t, int relay) ThrowTime(int thrower, P q, int toBase)
		{
			ThrowLeg leg = ThrowPlan(thrower, q, toBase);
			return (leg.Arrive, leg.Cut);
		}

		// A throw in its parts, in seconds from when the thrower has the ball: it leaves his hand at
		// Release; through a cutoff man it reaches him at CutArrive at CutPoint and leaves him at
		// CutRelease; it reaches the base at Arrive.
		public struct ThrowLeg
		{
			public double Release, Arrive, CutArrive, CutRelease;
			public int Cut;
			public P CutPoint;
		}

		public ThrowLeg ThrowPlan(int thrower, P q, int toBase)
		{
			P b = Geometry.Base[toBase];
			double d = q.Dist(b);
			FielderSkill f = F[thrower];
			double release = f.Release + (Geometry.IsOutfielder(thrower) ? Extra : 0);
			double direct = f.Release + ThrowSecs(thrower, d);
			ThrowLeg leg = new ThrowLeg { Release = release, Arrive = direct, Cut = -1 };
			if (d < RelayMin || !Geometry.IsOutfielder(thrower))
			{
				return leg;
			}
			int cut = CutoffFor(thrower, q, toBase);
			double k = RelayFromBase / d;
			P c = new P(b.X + (q.X - b.X) * k, b.Z + (q.Z - b.Z) * k);
			FielderSkill cf = F[cut];
			double cutArrive = f.Release + ThrowSecs(thrower, q.Dist(c));
			double relay = cutArrive + RelayTransfer + c.Dist(b) / cf.Arm;
			if (relay < direct)
			{
				leg.Arrive = relay;
				leg.Cut = cut;
				leg.CutPoint = c;
				leg.CutArrive = cutArrive;
				leg.CutRelease = cutArrive + RelayTransfer;
			}
			return leg;
		}

		// The shortstop takes relays from left and centre, the second baseman from right; a throw to
		// second from left field is cut by the shortstop as well.
		public static int CutoffFor(int thrower, P q, int toBase)
		{
			if (toBase == 1)
			{
				return Geometry.B2;
			}
			return q.H > 40 ? Geometry.SS : Geometry.B2;
		}
	}

	// The batter as a runner. A left-handed batter is a step closer to first and already moving
	// that way as he finishes his swing.
	public struct BatterRun
	{
		public RunnerSkill R;
		public bool Lefty;
		public const double StartRight = 0.30, StartLeft = 0.22, BoxOffset = 0.9;

		public double Start => Lefty ? StartLeft : StartRight;

		// Seconds from contact to first (4.27 for an average right-handed batter), second, third, home.
		public double ToBase(int b)
		{
			double run = Geometry.BaseLine * b - BoxOffset;
			return Start + R.Run(run) + R.Turn * Math.Max(0, b - 1);
		}
	}
}
