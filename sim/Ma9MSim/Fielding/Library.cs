using System;
using System.Collections.Generic;
using System.Threading;

namespace Ma9MSim.Fielding
{
	// Batted balls already played once by a league-average defence against a league-average batter,
	// sorted by what happened. The engine keeps deciding WHAT happens from the cards (hit or out,
	// single or double, ground or fly), as before; a ball (with the luck of its play) is then drawn
	// from the matching bucket and played again by the real fielders and the real batter with the
	// same luck. Average fielders give back the same result, so every card's record still replays;
	// better or worse ones turn hits into outs and outs into hits on the balls where it matters.
	//
	// The batter-runner (2026-11-01): a card's own singles, doubles and triples already hold his legs (his
	// real record), so the same balls are sorted once per runner speed (SpeedX, 주력 as x, 10 points apart)
	// and a batter draws from the runner nearest his own. Sorted for the league's average runner only, a
	// fast man stretched hits past his record and a slow one fell short: league triples 0.19 a team-game
	// against the cards' 0.15 (--gametest).
	public sealed class BipLibrary
	{
		public const int GroundOut = 0, FlyOut = 1, LineOut = 2, Single = 3, Double = 4, Triple = 5, HomeRun = 6, Keys = 7;
		public static readonly string[] KeyName = { "땅볼아웃", "뜬공아웃", "라이너아웃", "단타", "2루타", "3루타", "홈런" };

		public struct Entry
		{
			public BattedBall Ball;
			public ulong Seed;
		}

		// Runner speeds the balls are sorted for: the reference runner (Defence.RefRunX, bin RefBin) and 10, 20, 30
		// points either side.
		public static readonly double[] SpeedX = { -3, -2, -1, 0, 1, 2, 3 };
		public const int RefBin = 3;

		private readonly Entry[][][][] m_bucket = new Entry[2][][][];   // [hand 0 right, 1 left][speed bin][key]
		public readonly long[,] Count = new long[2, Keys];             // the reference runner's
		public int PerHand;

		public static int DefaultPerHand = 120000;
		private static BipLibrary s_shared;
		private static readonly object s_lock = new object();

		public static BipLibrary Shared
		{
			get
			{
				if (s_shared == null)
				{
					lock (s_lock)
					{
						s_shared ??= Build(DefaultPerHand, 0x6D61676Bul);
					}
				}
				return s_shared;
			}
		}

		public static BipLibrary Build(int perHand, ulong seed, BallGenerator gen = null)
		{
			gen ??= BallGenerator.Template ?? new BallGenerator();
			BipLibrary lib = new BipLibrary { PerHand = perHand };
			Defence avg = Defence.Average();
			int bins = SpeedX.Length;
			for (int hand = 0; hand < 2; hand++)
			{
				List<Entry>[][] lists = new List<Entry>[bins][];
				BatterRun[] runners = new BatterRun[bins];
				for (int s = 0; s < bins; s++)
				{
					lists[s] = new List<Entry>[Keys];
					for (int k = 0; k < Keys; k++) lists[s][k] = new List<Entry>(perHand / 4);
					runners[s] = new BatterRun { R = RunnerSkill.For(Defence.RefRunX + SpeedX[s]), Lefty = hand == 1 };
				}
				Rng rng = new Rng(seed + (ulong)hand * 7919);
				for (int i = 0; i < perHand; i++)
				{
					BattedBall b = gen.Next(ref rng);
					if (hand == 1) b = b.Mirror();
					ulong seedOf = (ulong)(rng.Next() * ulong.MaxValue);
					// Where the ball goes and who gets to it does not depend on the batter: one plan, then each
					// runner's times to the bases.
					BallPlan plan = BallPlan.Make(b, avg, runners[RefBin]);
					Entry entry = new Entry { Ball = b, Seed = seedOf };
					for (int s = 0; s < bins; s++)
					{
						int key;
						if (plan.HomeRun)
						{
							key = HomeRun;
						}
						else
						{
							plan.BT1 = runners[s].ToBase(1);
							plan.BT2 = runners[s].ToBase(2);
							plan.BT3 = runners[s].ToBase(3);
							Rng nr = new Rng(seedOf);
							BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref nr));
							key = KeyOf(r.Class, b.Type);
						}
						lists[s][key].Add(entry);
					}
				}
				lib.m_bucket[hand] = new Entry[bins][][];
				for (int s = 0; s < bins; s++)
				{
					lib.m_bucket[hand][s] = new Entry[Keys][];
					for (int k = 0; k < Keys; k++)
					{
						lib.m_bucket[hand][s][k] = lists[s][k].ToArray();
						if (s == RefBin) lib.Count[hand, k] = lists[s][k].Count;
					}
				}
			}
			return lib;
		}

		public static int KeyOf(int cls, BallType type)
		{
			if (cls == 0) return type == BallType.Ground ? GroundOut : type == BallType.Line ? LineOut : FlyOut;
			return cls >= 4 ? HomeRun : Single + cls - 1;
		}

		// For the league's average runner.
		public Entry Draw(int hand, int key, double u) => DrawBin(hand, RefBin, key, u);

		// For a batter-runner of 주력 x (RunnerSkill.Xs): the bin nearest him.
		public Entry Draw(int hand, double runX, int key, double u)
		{
			int s = (int)Math.Round(runX - Defence.RefRunX) + RefBin;
			return DrawBin(hand, Math.Clamp(s, 0, SpeedX.Length - 1), key, u);
		}

		private Entry DrawBin(int hand, int bin, int key, double u)
		{
			Entry[] b = m_bucket[hand][bin][key];
			if (b.Length == 0)
			{
				b = m_bucket[hand][bin][key == Triple ? Double : Single];
			}
			return b[Math.Min(b.Length - 1, (int)(u * b.Length))];
		}
	}
}
