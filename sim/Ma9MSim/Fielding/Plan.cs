using System;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// A ball on the grass: from distance S0 at time T0 it runs outward along spray H at speed V,
	// slowing by Decel; if it reaches the fence it comes back off it at WallKeep of its speed.
	public struct Roll
	{
		public const double WallKeep = 0.3;
		public double S0, T0, V, H, Fence, Decel;
		public bool HitsWall;
		public double TWall, VBack, TStop, SStop;
		private double m_cos, m_sin;   // of the spray angle, for At (the same numbers P.Polar gives)

		public static Roll Make(double s0, double t0, double v, double h, double fence, double decel)
		{
			double Decel = decel;
			Roll r = new Roll { S0 = s0, T0 = t0, V = Math.Max(0, v), H = h, Fence = fence, Decel = decel };
			double a = h * Math.PI / 180;
			r.m_cos = Math.Cos(a);
			r.m_sin = Math.Sin(a);
			double free = r.S0 + r.V * r.V / (2 * Decel);
			if (free >= fence && s0 < fence)
			{
				double disc = r.V * r.V - 2 * Decel * (fence - s0);
				double vw = Math.Sqrt(Math.Max(0, disc));
				r.HitsWall = true;
				r.TWall = t0 + (r.V - vw) / Decel;
				r.VBack = vw * WallKeep;
				r.TStop = r.TWall + r.VBack / Decel;
				r.SStop = fence - r.VBack * r.VBack / (2 * Decel);
			}
			else if (s0 >= fence)
			{
				// Already at the wall (a ball off the wall in the air): it drops and comes back slowly.
				r.HitsWall = true;
				r.TWall = t0;
				r.VBack = r.V * WallKeep;
				r.TStop = t0 + r.VBack / Decel;
				r.SStop = fence - r.VBack * r.VBack / (2 * Decel);
			}
			else
			{
				r.TStop = t0 + r.V / Decel;
				r.SStop = free;
			}
			return r;
		}

		public double DistAt(double t)
		{
			if (t <= T0) return S0;
			if (HitsWall && t >= TWall)
			{
				double u = Math.Min(t, TStop) - TWall;
				return Fence - VBack * u + 0.5 * Decel * u * u;
			}
			double w = Math.Min(t, TStop) - T0;
			return S0 + V * w - 0.5 * Decel * w * w;
		}

		public double SpeedAt(double t)
		{
			if (t >= TStop) return 0;
			if (HitsWall && t >= TWall) return VBack - Decel * (t - TWall);
			return V - Decel * Math.Max(0, t - T0);
		}

		public P At(double t)
		{
			double s = DistAt(t);
			return new P(s * m_cos, s * m_sin);
		}
	}

	// Who gets to a loose ball first, where and when, and how long his throws take.
	public struct Retrieval
	{
		public int F;            // fielder
		public P At;             // where he picks it up
		public double T;         // when the ball is in his hand (pick-up included)
		public double Meet;      // when he reaches the ball (before the pick-up)
		public bool OffWall;     // played off the wall
		public double Throw2, Throw3, ThrowH;   // arrival at second, third, home (tag included)
		public int Relay2, Relay3, RelayH;      // cutoff man used, -1 none

		public const double PickUp = 0.25, MovingBall = 0.15, Tag = 0.15;

		// Speed (engine session, 2026-10-31: the fielding engine made league games ~4× slower, most
		// of it here and in grounders): the ball's path on the 0.05 s grid is worked out once for all
		// the fielders, the outfielders look first, and a man stops looking once he cannot have the
		// ball in hand before the best so far. The pick is the same as before to the last bit: the
		// soonest in hand, a tie going to the lower position number.
		// (2026-11-20) The pitcher and the catcher last: a loose ball dying near the plate is theirs (a missed dribbler).
		private static readonly int[] s_order = { Geometry.LF, Geometry.CF, Geometry.RF, Geometry.B1, Geometry.B2, Geometry.B3, Geometry.SS, Geometry.Pit, Geometry.C };

		[ThreadStatic] private static double[] t_gridT;
		[ThreadStatic] private static P[] t_gridAt;

		public static Retrieval Find(Defence d, Roll roll, int skip, int late, double lateBy, bool outfieldOnly)
		{
			double[] gridT = t_gridT ??= new double[256];
			P[] gridAt = t_gridAt ??= new P[256];
			int n = 0;
			for (double t = roll.T0; t <= roll.TStop + 0.001; t += 0.05)
			{
				if (n == gridT.Length)
				{
					Array.Resize(ref t_gridT, n * 2);
					Array.Resize(ref t_gridAt, n * 2);
					gridT = t_gridT;
					gridAt = t_gridAt;
				}
				gridT[n] = t;
				gridAt[n] = roll.At(t);
				n++;
			}
			Retrieval best = new Retrieval { F = -1, T = double.MaxValue };
			foreach (int f in s_order)
			{
				if (f == skip || (outfieldOnly && !Geometry.IsOutfielder(f)))
				{
					continue;
				}
				FielderSkill s = d.F[f];
				P pos = d.Pos[f];
				double delay = f == late ? lateBy : 0;
				double tau = -1;
				bool beaten = false;
				for (int k = 0; k < n; k++)
				{
					double t = gridT[k];
					// He meets the ball at t or later, and has it PickUp after that at the soonest.
					double soonest = t + PickUp;
					if (soonest > best.T || (soonest == best.T && f > best.F))
					{
						beaten = true;
						break;
					}
					double need = delay + s.RunTime(Math.Max(0, pos.Dist(gridAt[k]) - s.Reach));
					if (need <= t)
					{
						tau = t;
						break;
					}
				}
				if (beaten)
				{
					continue;
				}
				P at;
				if (tau < 0)
				{
					at = roll.At(roll.TStop);
					tau = Math.Max(roll.TStop, delay + s.RunTime(Math.Max(0, d.Pos[f].Dist(at) - s.Reach)));
				}
				else
				{
					at = roll.At(tau);
				}
				bool wall = roll.HitsWall && tau >= roll.TWall;
				double t1 = tau + PickUp + (wall ? s.Wall : 0) + (roll.SpeedAt(tau) > 12 ? MovingBall : 0);
				if (t1 < best.T || (t1 == best.T && f < best.F))
				{
					best = new Retrieval { F = f, At = at, T = t1, OffWall = wall, Meet = tau };
				}
			}
			(double a2, int r2) = d.ThrowTime(best.F, best.At, 2);
			(double a3, int r3) = d.ThrowTime(best.F, best.At, 3);
			(double ah, int rh) = d.ThrowTime(best.F, best.At, 0);
			best.Throw2 = best.T + a2 + Tag; best.Relay2 = r2;
			best.Throw3 = best.T + a3 + Tag; best.Relay3 = r3;
			best.ThrowH = best.T + ah + Tag; best.RelayH = rh;
			return best;
		}
	}

	public enum Catch : byte { None, Routine, Running, Diving, Leaping, Liner, Grounder, DiveStop, Charging }

	// Everything about a batted ball that does not depend on luck: where it lands or rolls, who can
	// get to it and with how much time to spare, and how long the throws take. Realize() then adds
	// the luck (read, dive, close plays) from a Noise draw.
	public sealed class BallPlan
	{
		public BattedBall Ball;
		public bool Ground;
		public bool HomeRun;        // clears the wall: not a ball in play
		public double BT1, BT2, BT3;   // batter to first, second, third

		// Air balls.
		public double LandDist, LandTime;
		public bool OffWall;            // hits the wall on the fly (below its top)
		public double WallY;
		public int LinerF = -1;         // an infielder who can snag a liner as it passes
		public double LinerMargin, LinerDiveT, LinerDiveSkill, LinerTime, LinerY;
		public P LinerPoint;
		public int Catcher = -1;        // best man to catch it in the air
		public double Margin, DiveT, DiveSkill, CatchTime;
		public P CatchPoint;
		public bool WallCatch;
		public Retrieval Clean, AfterDive;

		// Ground balls.
		public int Fielder = -1;
		public double FieldMargin, FieldTime, FieldDiveT, FieldDiveSkill, Arrive1;
		public P FieldPoint;
		public Retrieval Through;

		public const double ReadSd = 0.24, LinerSd = 0.10, FieldSd = 0.12, Safe1Sd = 0.12, SafeSd = 0.20, DecideSd = 0.08;

		// MakeGround's path grid, one per thread (league games run in parallel).
		[ThreadStatic] private static double[] t_gridS, t_gridT, t_gridD1;
		[ThreadStatic] private static P[] t_gridQ;
		// A ball landing in the outfield keeps AirKeep of its speed through the first bounces, then
		// slows by AirDecel on the grass (tuned so a league of regulars gives about 2% triples).
		public static double AirKeep = 0.35, AirDecel = 8.0;   // --fieldlab plays grid, 2026-10-30
		public static double InfieldChaseMax = 58;   // shallow-outfield balls infielders chase too (was 45)
		// Plays shown as dives (user, 2026-10-30: a good defence makes several a game): every real dive,
		// and this share of catches with less than ShowDiveAir s to spare (grounders: ShowDiveGround).
		public static double ShowDiveAir = 0.30, ShowDiveGround = 0.10, ShowDiveShare = 0.7;
		public const double CatchEarly = 0.04, WallLeap = 3.1, LinerReachY = 2.6, GetUp = 0.85, OfGetUp = 1.1, DiveTry = 0.30;
		// The glove's reach without leaving the ground (a 2-3 head figure's raised arm), and laid out in a dive.
		public const double StandReachY = 2.3, DiveReachY = 1.6;
		// Infielders leap for liners (the old rule, up to LinerReachY + 0.6): off until 잠재력 unlocks it.
		public static bool LeapingInfield = false;

		public static BallPlan Make(BattedBall b, Defence d, BatterRun br)
		{
			BallPlan p = new BallPlan { Ball = b };
			p.BT1 = br.ToBase(1);
			p.BT2 = br.ToBase(2);
			p.BT3 = br.ToBase(3);
			if (b.La < 10)
			{
				p.MakeGround(d);
			}
			else
			{
				p.MakeAir(d);
			}
			return p;
		}

		private void MakeAir(Defence d)
		{
			double ev = Ball.Ev, la = Ball.La, h = Ball.H;
			double carry = Flight.Carry(ev, la), hang = Flight.Hang(ev, la);
			double fence = Geometry.FenceAt(h);
			LandDist = carry;
			LandTime = hang;
			if (carry >= fence)
			{
				(double y, double t) = Flight.At(ev, la, fence);
				if (double.IsNaN(y))
				{
					y = 0; t = hang;
				}
				if (y > Geometry.WallHeight)
				{
					HomeRun = true;
					WallY = y;
					LandDist = fence;
					LandTime = t;
					return;
				}
				OffWall = true;
				WallY = y;
				LandDist = fence;
				LandTime = t;
			}
			P land = P.Polar(LandDist, h);

			// Infielders: a liner or a low fly passing them within reach of the glove, raised (StandReachY) or laid out to
			// the side in a dive (DiveReachY). No leaping snag: an infielder's high jump catch is not in the game yet (user,
			// 2026-10-31: "내야수 점프캐치도 나중에 잠재력을 통해 해금"; it took 5 balls in 1,000, up to 3.2 m high). The
			// outfielder's leap at the wall stays ("수비 잘하는 외야수의 특권").
			LinerF = -1;
			LinerMargin = double.NegativeInfinity;
			for (int f = Geometry.B1; f <= Geometry.Pit; f++)
			{
				if (!Geometry.IsInfielder(f) && f != Geometry.Pit)
				{
					continue;
				}
				FielderSkill s = d.F[f];
				P pos = d.Pos[f];
				double r = pos.R;
				if (r >= LandDist - 1)
				{
					continue;
				}
				(double y, double t) = Flight.At(ev, la, r);
				if (double.IsNaN(y) || y > (LeapingInfield ? LinerReachY + 0.6 : StandReachY))
				{
					continue;
				}
				double side = Math.Abs(r * Math.Sin((h - pos.H) * Math.PI / 180));
				double cover = FMath.RunDist(t - s.React, s.Speed, s.Accel) + s.Reach - (y > LinerReachY ? (y - LinerReachY) * 2 : 0);
				double margin = (cover - side) / s.Speed;   // seconds to spare (negative: short)
				if (margin > LinerMargin)
				{
					LinerMargin = margin;
					LinerF = f;
					LinerDiveT = LeapingInfield || y <= DiveReachY ? s.DiveReach / s.Speed : 0;
					LinerDiveSkill = s.DiveSkill * 0.8;
					LinerTime = t;
					LinerY = y;
					LinerPoint = P.Polar(r, h);
				}
			}

			// Anyone (catcher and pitcher too on pop-ups) racing the ball to where it comes down.
			double catchT = LandTime - CatchEarly;
			Catcher = -1;
			Margin = double.NegativeInfinity;
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				if (OffWall && !Geometry.IsOutfielder(f))
				{
					continue;
				}
				if (OffWall && WallY > WallLeap)
				{
					continue;
				}
				if (f == Geometry.C && Ball.La < 50)
				{
					continue;
				}
				FielderSkill s = d.F[f];
				P target = OffWall ? P.Polar(LandDist - 0.6, h) : land;
				double need = s.RunTime(Math.Max(0, d.Pos[f].Dist(target) - s.Reach));
				double m = catchT - need;
				// The outfielder takes what both can reach; the centre fielder has the call.
				if (Geometry.IsOutfielder(f) && m > 0.3) m += 0.25;
				if (f == Geometry.CF && m > 0.3) m += 0.1;
				if (m > Margin)
				{
					Margin = m;
					Catcher = f;
					DiveT = s.DiveReach / s.Speed;
					DiveSkill = s.DiveSkill;
					CatchTime = catchT;
					CatchPoint = target;
				}
			}
			WallCatch = OffWall;
			if (Catcher >= 0 && (Geometry.IsOutfielder(Catcher) && Margin > 0.55 || !Geometry.IsOutfielder(Catcher) && Margin > 0.3))
			{
				// Undo the priority bonus so Margin stays seconds to spare.
				Margin -= Geometry.IsOutfielder(Catcher) ? 0.25 + (Catcher == Geometry.CF ? 0.1 : 0) : 0;
			}

			// Not caught: it bounces on (or comes back off the wall) and someone runs it down.
			double vl = Flight.LandSpeed(ev, la);
			Roll roll = OffWall
				? Roll.Make(fence, LandTime, vl * 0.5, h, fence, AirDecel)
				: Roll.Make(LandDist, LandTime, vl * AirKeep, h, fence, AirDecel);
			// A blooper into the shallow outfield is run down by whoever gets there first, the
			// shortstop or second baseman going out as often as an outfielder coming in; deeper
			// than InfieldChaseMax only the outfielders chase.
			Clean = Retrieval.Find(d, roll, -1, -1, 0, LandDist > InfieldChaseMax);
			AfterDive = Catcher >= 0 ? Retrieval.Find(d, roll, -1, Catcher, CatchTime + OfGetUp, LandDist > InfieldChaseMax) : Clean;
		}

		private void MakeGround(Defence d)
		{
			Ground = true;
			double ev = Ball.Ev, la = Ball.La, h = Ball.H;
			double d0 = Math.Max(0.5, Flight.Carry(ev, la)), t0 = Flight.Hang(ev, la);
			double v = Flight.LandSpeed(ev, la) * Flight.HopKeep;
			double stop = Flight.GroundStop(v, d0);
			Fielder = -1;
			FieldMargin = double.NegativeInfinity;
			Arrive1 = double.MaxValue;
			double bestScore = double.MaxValue;
			// Speed (engine session, 2026-10-31): the ball every 0.5 m along its path — when it gets there,
			// the spot, its distance to first — worked out once for all six men (the same numbers each
			// worked out for himself before; P.Polar's cosine and sine once).
			double ha = h * Math.PI / 180, hc = Math.Cos(ha), hs = Math.Sin(ha);
			double maxR = 0;
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				if (Geometry.IsInfielder(f) || f == Geometry.Pit || f == Geometry.C) maxR = Math.Max(maxR, d.Pos[f].R);
			}
			double hiAll = Math.Min(stop, maxR + 9);
			double[] gridS = t_gridS ??= new double[256], gridT = t_gridT ??= new double[256], gridD1 = t_gridD1 ??= new double[256];
			P[] gridQ = t_gridQ ??= new P[256];
			int n = 0;
			for (double sd = Math.Max(d0, 1.5); sd <= hiAll; sd += 0.5)
			{
				if (n == gridS.Length)
				{
					Array.Resize(ref t_gridS, n * 2);
					Array.Resize(ref t_gridT, n * 2);
					Array.Resize(ref t_gridD1, n * 2);
					Array.Resize(ref t_gridQ, n * 2);
					gridS = t_gridS; gridT = t_gridT; gridD1 = t_gridD1; gridQ = t_gridQ;
				}
				double tb = Flight.GroundTime(v, d0, t0, sd);
				P q = new P(sd * hc, sd * hs);
				gridS[n] = sd;
				gridT[n] = tb;
				gridQ[n] = q;
				gridD1[n] = q.Dist(Geometry.Base[1]);
				n++;
				if (double.IsInfinity(tb)) break;
			}
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				if (!(Geometry.IsInfielder(f) || f == Geometry.Pit || f == Geometry.C))
				{
					continue;
				}
				FielderSkill s = d.F[f];
				P pos = d.Pos[f];
				double hi = Math.Min(stop, pos.R + 9);
				if (f == Geometry.C) hi = Math.Min(hi, 9);
				double bestM = double.NegativeInfinity, bestMs = 0, bestMt = 0;
				double pickS = -1, pickT = 0, pickArrive = double.MaxValue;
				for (int k = 0; k < n; k++)
				{
					double sd = gridS[k];
					if (!(sd <= hi)) break;
					double tb = gridT[k];
					if (double.IsInfinity(tb)) break;
					P q = gridQ[k];
					double tf = s.RunTime(Math.Max(0, pos.Dist(q) - s.Reach));
					double m = tb - tf;
					if (m > bestM)
					{
						bestM = m; bestMs = sd; bestMt = tb;
					}
					if (m >= 0.12)
					{
						// Fielded here: when would the throw reach first?
						double toFirst = gridD1[k];
						double arrive = tb + 0.12 + s.Release + toFirst / s.Arm;
						if (f == Geometry.B1 && toFirst < 9)
						{
							arrive = tb + 0.1 + s.RunTime(toFirst) - s.React;
						}
						if (arrive < pickArrive)
						{
							pickArrive = arrive; pickS = sd; pickT = tb;
						}
					}
				}
				// (2026-11-20, user: "1루수 앞 빗맞은 땅볼이 나왔는데 이걸 왜 우익수가 달려나오면서 처리하려고 하는지 모르겠음 … 이게 3루타가 되던데 …
				// 내야 빗맞은 땅볼 행운의 안타가 장타가 되는 일이 있어서는 안됨") A ball that dies within his reach is his whatever the clock:
				// he walks up and picks it up at rest. Before, a man who could not meet the moving ball "missed" a dribbler that
				// stopped in front of him, and the outfielders alone went for it (Through).
				bool rest = false;
				if (stop >= 1.5 && stop <= hi + 0.25)
				{
					double tStop = Flight.GroundTime(v, d0, t0, Math.Max(d0, stop - 0.25));
					if (!double.IsInfinity(tStop) && !double.IsNaN(tStop))
					{
						P q = new P(stop * hc, stop * hs);
						double tf = s.RunTime(Math.Max(0, pos.Dist(q) - s.Reach));
						double tb = Math.Max(tStop, tf);
						double toFirst = q.Dist(Geometry.Base[1]);
						double arrive = tb + 0.12 + s.Release + toFirst / s.Arm;
						if (f == Geometry.B1 && toFirst < 9)
						{
							arrive = tb + 0.1 + s.RunTime(toFirst) - s.React;
						}
						if (arrive < pickArrive)
						{
							pickArrive = arrive; pickS = stop; pickT = tb; rest = true;
						}
					}
				}
				double margin = pickS >= 0 ? Math.Max(rest ? 1.0 : 0.12, bestM) : bestM;
				double sAt = pickS >= 0 ? pickS : bestMs;
				double tAt = pickS >= 0 ? pickT : bestMt;
				if (double.IsNegativeInfinity(margin)) continue;
				// Prefer whoever can make the play soonest; a first baseman lets the second baseman take
				// what both can reach, and the catcher only takes dribblers.
				double score = pickS >= 0 ? pickArrive : 100 - margin;
				if (f == Geometry.B1 && pickS >= 0 && P.Polar(sAt, h).Dist(Geometry.Base[1]) > 9) score += 0.4;
				if (score < bestScore)
				{
					bestScore = score;
					Fielder = f;
					FieldMargin = margin;
					FieldPoint = P.Polar(sAt, h);
					FieldTime = tAt;
					FieldDiveT = s.DiveReach / s.Speed;
					FieldDiveSkill = s.DiveSkill;
					Arrive1 = pickS >= 0 ? pickArrive : tAt + 0.12 + GetUp + s.Release + FieldPoint.Dist(Geometry.Base[1]) / s.Arm;
				}
			}
			Roll roll = Roll.Make(d0, t0, v, h, Geometry.FenceAt(h), Flight.GroundDecel);
			// (2026-11-20) Whoever has it in hand first, infielders, the pitcher and the catcher too: a ball the fielder missed
			// that dies on the infield is not an outfielder's to run in for (the man who missed it starts after he got up).
			Through = Retrieval.Find(d, roll, -1, Fielder, FieldTime + GetUp, false);
		}

		// The luck in a play, drawn once and reused for the league-average and the real fielders.
		public struct Noise
		{
			public double Read, Dive, Liner, LinerDive, Field, FieldDive, Safe1, Dec2, Safe2, Dec3, Safe3;

			public static Noise Draw(ref Rng rng)
			{
				return new Noise
				{
					Read = BallGenerator.Normal(ref rng), Dive = rng.Next(), Liner = BallGenerator.Normal(ref rng), LinerDive = rng.Next(),
					Field = BallGenerator.Normal(ref rng), FieldDive = rng.Next(), Safe1 = BallGenerator.Normal(ref rng),
					Dec2 = BallGenerator.Normal(ref rng), Safe2 = BallGenerator.Normal(ref rng), Dec3 = BallGenerator.Normal(ref rng), Safe3 = BallGenerator.Normal(ref rng)
				};
			}
		}

		// What happened to the batter: out (0) or a single, double, triple; how it was caught or
		// fielded, by whom, where and when; and whether he was thrown out stretching.
		public struct Result
		{
			public int Class;
			public Catch How;
			public int Fielder;
			public P At;
			public double T;
			public bool DoveMissed, InfieldHit, OffWall, Through;
			public bool ShowDive;       // a close catch or stop shown as a dive (picture only, the result is the same)
			public int OutStretching;   // 0, or the base he was thrown out at (2 or 3)
			public Retrieval Ret;
		}

		// Thresholds to go for an extra base: safe chance needed (decisions are a little noisy).
		public static double Go2Default = 0.84, Go3Default = 0.995;
		public double Go2 = Go2Default, Go3 = Go3Default;

		public Result Realize(in Noise n)
		{
			Result r = new Result { Fielder = -1 };
			if (Ground)
			{
				double m = FieldMargin + FieldSd * n.Field;
				bool fielded = false, dove = false;
				if (Fielder >= 0 && m >= 0)
				{
					fielded = true;
				}
				else if (Fielder >= 0 && m >= -FieldDiveT)
				{
					dove = true;
					fielded = n.FieldDive < FieldDiveSkill * Math.Sqrt(1 + m / FieldDiveT);
					r.DoveMissed = !fielded;
				}
				if (fielded)
				{
					double arrive = Arrive1 + (dove ? GetUp : 0);
					r.Fielder = Fielder;
					r.At = FieldPoint;
					r.T = FieldTime;
					r.How = dove ? Catch.DiveStop : m < 0.25 ? Catch.Charging : Catch.Grounder;
					r.ShowDive = dove || (m < ShowDiveGround && Fielder != Geometry.C && Fielder != Geometry.Pit && n.FieldDive < ShowDiveShare);
					bool out1 = BT1 - arrive + Safe1Sd * n.Safe1 >= 0;
					r.Class = out1 ? 0 : 1;
					r.InfieldHit = !out1;
					return r;
				}
				r.Through = true;
				return Extra(r, Through, n);
			}
			if (HomeRun)
			{
				r.Class = 4;
				return r;
			}
			// A liner or low fly snagged by an infielder.
			if (LinerF >= 0)
			{
				double m = LinerMargin + LinerSd * n.Liner;
				if (m >= 0 || (m >= -LinerDiveT && n.LinerDive < LinerDiveSkill))
				{
					r.Fielder = LinerF;
					r.At = LinerPoint;
					r.T = LinerTime;
					r.How = m >= 0 ? Catch.Liner : Catch.Diving;
					// A close one shown as a dive only when it is low enough to dive at (above that he takes it standing).
					r.ShowDive = m < ShowDiveAir && (LeapingInfield || LinerY <= DiveReachY);
					return r;
				}
			}
			if (Catcher >= 0)
			{
				double m = Margin + ReadSd * n.Read;
				// He lays out only when he thinks he has a real chance; otherwise he pulls up and plays
				// the hop so the ball does not get by him.
				double diveP = m < 0 && m >= -DiveT ? DiveSkill * Math.Sqrt(1 + m / DiveT) : 0;
				bool tries = m < 0 && diveP >= DiveTry && !WallCatch;
				if (m >= 0 || (tries && n.Dive < diveP))
				{
					r.Fielder = Catcher;
					r.At = CatchPoint;
					r.T = CatchTime;
					r.How = WallCatch ? Catch.Leaping : m < 0 ? Catch.Diving : m < 0.35 ? Catch.Running : Catch.Routine;
					r.ShowDive = r.How == Catch.Diving || (!WallCatch && m < ShowDiveAir && n.Dive < ShowDiveShare);
					return r;
				}
				r.DoveMissed = tries;
			}
			r.OffWall = OffWall;
			return Extra(r, r.DoveMissed ? AfterDive : Clean, n);
		}

		private Result Extra(Result r, Retrieval ret, in Noise n)
		{
			r.Ret = ret;
			r.Fielder = ret.F;
			r.At = ret.At;
			r.T = ret.T;
			r.Class = 1;
			double p2 = FMath.Phi((ret.Throw2 - BT2) / SafeSd);
			if (p2 + DecideSd * n.Dec2 >= Go2)
			{
				if (ret.Throw2 - BT2 + SafeSd * n.Safe2 < 0)
				{
					r.OutStretching = 2;
					return r;
				}
				r.Class = 2;
				double p3 = FMath.Phi((ret.Throw3 - BT3) / SafeSd);
				if (p3 + DecideSd * n.Dec3 >= Go3)
				{
					if (ret.Throw3 - BT3 + SafeSd * n.Safe3 < 0)
					{
						r.OutStretching = 3;
						return r;
					}
					r.Class = 3;
				}
			}
			return r;
		}
	}
}
