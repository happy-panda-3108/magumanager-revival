using System;
using System.Collections.Generic;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// Turns an engine play (BallPlan + Result + FieldOutcome) into a PlayScript the 3D broadcast can
	// play back: the ball's path, every fielder's runs, catches, throws, covers and tags, and every
	// runner's legs, slides and calls. Only the staging is decided here (who backs up, where the
	// cutoff man stands, when a runner slides); the results are the engine's.
	public sealed class Choreo
	{
		public const int RunnerActor = 20;
		public const double Reach = 0.9, Tag = Resolver.Tag;
		public static double DiveFly = 2.2, DiveGlove = 0.6;   // a dive's body flight and the glove past where he lands (m)
		public static double OutMargin = 0.12, OutMarginSpread = 0.08;   // a runner called out gets there this long after the ball (s)

		private PlayScript s;
		private Defence D;
		private readonly P[] m_pos = new P[Geometry.FielderCount];
		private readonly double[] m_free = new double[Geometry.FielderCount];   // when each man can start his next move
		private readonly bool[] m_busy = new bool[Geometry.FielderCount];
		private readonly int[] m_tagSeg = new int[4];   // per runner slot: his Tag segment (-1 none)
		private readonly double[] m_lateBall = new double[4];   // per runner slot: a throw drawn late for him (NaN none)
		private readonly RunnerSkill[] m_runSk = new RunnerSkill[4];

		public PlayScript Build(BallPlan plan, in BallPlan.Result r, FieldOutcome o, Defence d, BatterRun batter, RunnerSkill[] run, bool[] on)
		{
			s = new PlayScript { Ball = plan.Ball };
			D = d;
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				m_pos[f] = d.Pos[f];
				m_free[f] = d.F[f].React;
				m_busy[f] = false;
			}
			for (int i = 0; i < 4; i++) { m_tagSeg[i] = -1; m_lateBall[i] = double.NaN; m_runSk[i] = i == 0 ? batter.R : run[i]; }
			double held = BattedBall(plan, r, o);          // ball in a glove from this time
			int holder = HolderOf(plan, r);
			double last = Throws(o, holder, held, plan, r);
			Helpers(plan, r, o, holder);
			Runners(plan, r, o, batter, run, on, last);
			s.End = Math.Max(s.End, (float)(last + 1.2));
			return s;
		}

		private static int HolderOf(BallPlan plan, in BallPlan.Result r)
		{
			if (r.Class == 0 || (plan.Ground && !r.Through && r.Fielder >= 0)) return r.Fielder;
			return r.Ret.F;
		}

		// ------------------------------------------------------------------ fielders' moves

		// Run fielder f to point p, arriving no later than `by` if he can (else as soon as he can);
		// returns when he gets there. He leaves when he is free (his reaction on the first move).
		private double MoveTo(int f, P p, double by, bool hurry = true)
		{
			FielderSkill sk = D.F[f];
			double dist = m_pos[f].Dist(p);
			if (dist < 0.3)
			{
				return m_free[f];
			}
			double need = FMath.RunTime(dist / sk.Route, sk.Speed, sk.Accel);
			double start = m_free[f];
			// No need to sprint early: leave so as to arrive a beat before he is needed.
			if (!hurry && by - need - 0.3 > start) start = by - need - 0.3;
			double arrive = start + need;
			s.Add(SegKind.Move, f, start, arrive, m_pos[f], p);
			m_pos[f] = p;
			m_free[f] = arrive;
			return arrive;
		}

		private void Hold(int f, double until)
		{
			if (until > m_free[f] + 0.05)
			{
				s.Add(SegKind.Wait, f, m_free[f], until, m_pos[f], m_pos[f]);
				m_free[f] = until;
			}
		}

		// ------------------------------------------------------------------ steals

		// A steal of second (fromBase 1) or third (2), as the engine called it (safe or not). Time 0 is
		// the pitch in the catcher's mitt (the client's MESSAGE_STEALPLAY_START). The runner broke on
		// the pitcher's first move (BreakAhead before it) from his lead, so he is well down the line
		// at full speed; the catcher's transfer and his arm (Defence) set the throw; the cover man
		// takes it a stride off the bag and puts the tag on as he slides in. The engine's call stands:
		// a safe runner beats the ball (if his legs alone would not, the throw is a touch late and
		// wide of the bag), an out one gets there just after it (a poor jump if he is quick).
		public const double BreakAhead = 1.75, CatcherTransfer = 0.75;

		public PlayScript Steal(Defence d, RunnerSkill run, int fromBase, bool safe)
		{
			s = new PlayScript { Ball = new BattedBall { Ev = 0, La = 0, H = 45 } };
			D = d;
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				m_pos[f] = d.Pos[f];
				m_free[f] = 0.2;
				m_busy[f] = false;
			}
			for (int i = 0; i < 4; i++) { m_tagSeg[i] = -1; m_lateBall[i] = double.NaN; }
			int toBase = fromBase + 1;
			int actor = RunnerActor + fromBase;
			double lead = Resolver.Lead[fromBase];
			double ran = FMath.RunDist(BreakAhead, run.Speed, run.Accel);
			double at0 = Math.Min(Geometry.BaseLine - 4, lead + ran);
			P start = PathPoint(fromBase, at0);
			P bag = Geometry.Base[toBase];
			double natural = (Geometry.BaseLine - at0) / run.Speed;
			int cover = toBase == 3 ? Geometry.B3 : Geometry.SS;
			P spot = BagSpot(toBase, true);
			// The throw: out of the catcher's crouch, over the mound to the bag.
			P plate = new P(0.6, 0.6);
			double release = CatcherTransfer;
			double fly = plate.Dist(spot) / Math.Max(25, d.F[Geometry.C].Arm);
			double ballAt = release + fly;
			double arrive = natural;
			if (safe)
			{
				if (arrive > ballAt - 0.1) { ballAt = arrive + 0.12; spot = Toward(spot, plate, -1.2); }   // late and wide: he is in
				else arrive = Math.Min(arrive, ballAt - 0.12);
			}
			else if (arrive < ballAt + 0.15)
			{
				arrive = ballAt + 0.2;   // a poor jump
			}
			double tag = Math.Max(ballAt, arrive);
			// The pitch in the mitt, the transfer, the throw.
			s.Add(SegKind.Catch, Geometry.C, 0, 0, plate, plate, style: CatchStyle.Receive);
			s.Add(SegKind.BallHeld, 30, 0, release, plate, plate, arg: Geometry.C);
			s.Add(SegKind.Throw, Geometry.C, release, release, plate, spot, arg: cover);
			s.Add(SegKind.BallThrow, 30, release, ballAt, plate, spot, arg: cover);
			// The cover man broke for the bag as the pitch went by: he is there for the ball (the
			// ground he made up before 0 comes off his start, the client blending it in).
			FielderSkill ck = d.F[cover];
			double can = FMath.RunDist(Math.Max(0.3, ballAt - 0.15), ck.Speed, ck.Accel) * ck.Route;
			m_free[cover] = 0;
			if (m_pos[cover].Dist(spot) > can) m_pos[cover] = Toward(spot, m_pos[cover], can);
			MoveTo(cover, spot, ballAt - 0.15, hurry: false);
			Hold(cover, ballAt);
			s.Add(SegKind.Catch, cover, ballAt, ballAt, spot, spot, style: CatchStyle.Receive);
			m_tagSeg[fromBase] = s.Segs.Count;
			s.Add(SegKind.Tag, cover, tag, tag, spot, spot, arg: fromBase, style: 1);
			s.Add(SegKind.BallHeld, 30, ballAt, tag + 1.0, spot, spot, arg: cover);
			// Backing up: the centre fielder behind second (the left fielder behind third), the other
			// middle infielder toward the bag.
			// Both on the throw's line beyond the bag (the infielder a step to his own side of it).
			int back = toBase == 3 ? Geometry.LF : Geometry.CF;
			MoveTo(back, Toward(bag, plate, -14), ballAt, hurry: true);
			int mid = toBase == 3 ? Geometry.SS : Geometry.B2;
			MoveTo(mid, Toward(Toward(bag, plate, -5), d.Pos[mid], 2), ballAt, hurry: true);
			// The runner: already going at full speed, a slide (head first now and then), the call.
			bool head = ((int)(run.Speed * 1000) + fromBase) % 4 == 0;
			s.Add(SegKind.Run, actor, 0, Math.Max(0.1, arrive - 0.5), start, Toward(bag, start, 3.5), arg: (byte)((fromBase << 4) | toBase));
			s.Add(SegKind.Slide, actor, Math.Max(0.1, arrive - 0.5), arrive, Toward(bag, start, 3.5), bag, arg: toBase, style: head ? 1 : 0);
			s.Add(safe ? SegKind.Safe : SegKind.Out, actor, tag, tag + 0.8, bag, bag, arg: toBase);
			s.End = (float)(tag + 1.6);
			return s;
		}

		// ------------------------------------------------------------------ the batted ball

		// Run f toward p to within reach of it, by time "by" if he can. If he can, he arrives (from his
		// free time on) and the point is returned with made = true; if not, he runs until "by" and the
		// point he has reached is returned (the rest is a dive or a miss).
		private (P at, bool made) Approach(int f, P p, double by, double reach)
		{
			FielderSkill sk = D.F[f];
			P from = m_pos[f];
			double full = from.Dist(p);
			double dist = Math.Max(0, full - reach);
			P target = full > 0.01 ? Toward(p, from, Math.Min(reach, full)) : p;
			double start = m_free[f];
			if (dist < 0.2)
			{
				return (from, start <= by + 0.05);
			}
			double need = FMath.RunTime(dist / sk.Route, sk.Speed, sk.Accel);
			if (start + need <= by)
			{
				s.Add(SegKind.Move, f, start, start + need, from, target);
				m_pos[f] = target;
				m_free[f] = start + need;
				return (target, true);
			}
			double ran = by > start ? FMath.RunDist(by - start, sk.Speed, sk.Accel) * sk.Route : 0;
			P got = Toward(target, from, Math.Max(0, dist - ran));
			if (by > start + 0.02)
			{
				s.Add(SegKind.Move, f, start, by, from, got);
				m_free[f] = by;
			}
			m_pos[f] = got;
			return (got, false);
		}

		// Fielder f catches (or fields) the ball at point p at time t: he gets there and waits, or runs
		// as far as he can and dives the rest. Returns true if it was a dive.
		private bool Glove(int f, P p, double t, bool showDive, double reach, byte style, byte diveStyle, double after, bool noDive = false)
		{
			const double DiveTime = 0.35;
			int mark = s.Segs.Count;
			P from0 = m_pos[f];
			double free0 = m_free[f];
			(P at, bool made) = Approach(f, p, showDive ? t - DiveTime : t, reach);
			if (!made && !showDive && noDive)
			{
				// A slow roller he charges (or the pitcher or catcher off his spot): no dive on a ball
				// that has all but stopped; he gets there a touch quicker than his pace and scoops it.
				s.Segs.RemoveRange(mark, s.Segs.Count - mark);
				P target = from0.Dist(p) > reach ? Toward(p, from0, reach * 0.5) : from0;
				double t0 = Math.Min(free0, t - 0.3);
				if (target.Dist(from0) > 0.3) s.Add(SegKind.Move, f, t0, t, from0, target);
				m_pos[f] = target;
				m_free[f] = t + 0.12;
				s.Add(SegKind.Catch, f, t, t, target, target, style: style);
				return false;
			}
			bool dive = showDive || !made;
			if (dive)
			{
				// He leaves his feet DiveTime before the ball gets there, at most DiveFly from where his
				// body lands (a full-length layout at a sprint carries the body about 2 m in its
				// 0.3 s in the air), and lands DiveGlove short of the ball with the glove out (a 2-3
				// head figure's arm). Where he could not get that close at his own pace he runs it a
				// touch quicker: the old way ran him until the catch and then put him on the ball,
				// so the dive never showed and he jumped up to 3 m (2026-10-31).
				s.Segs.RemoveRange(mark, s.Segs.Count - mark);
				m_pos[f] = from0;
				m_free[f] = free0;
				double t0 = Math.Max(free0, t - DiveTime);
				P land = from0.Dist(p) > DiveGlove ? Toward(p, from0, DiveGlove) : from0;
				P leave = from0.Dist(land) > DiveFly ? Toward(land, from0, DiveFly) : from0;
				// A ball on him at once (a liner at an infielder): no time to run, he dives from where he is.
				if (t0 - free0 < 0.25) leave = from0;
				if (leave.Dist(from0) > 0.3)
				{
					FielderSkill sk = D.F[f];
					double need = FMath.RunTime(from0.Dist(leave) / sk.Route, sk.Speed, sk.Accel);
					double start = Math.Min(free0, t0 - 0.1);
					// Leave late enough to arrive just in time, as Approach does, or as soon as he can.
					if (start + need < t0) start = Math.Max(free0, t0 - need);
					s.Add(SegKind.Move, f, start, Math.Max(start + 0.1, t0), from0, leave);
				}
				s.Add(SegKind.Dive, f, t0, Math.Max(t0 + 0.05, t), leave, land, style: diveStyle);
				m_pos[f] = land;
				m_free[f] = t + after;
				s.Add(SegKind.Catch, f, t, t, land, land, style: diveStyle);
				return true;
			}
			Hold(f, t);
			m_pos[f] = at;
			m_free[f] = t + 0.12;
			s.Add(SegKind.Catch, f, t, t, at, at, style: style);
			return false;
		}

		// Ball pieces from contact until a fielder has it, with the primary fielder's run and catch.
		// Returns the time the ball is in his glove (m_free says when he can throw).
		private double BattedBall(BallPlan plan, in BallPlan.Result r, FieldOutcome o)
		{
			BattedBall b = plan.Ball;
			double h = b.H;
			if (plan.Ground)
			{
				double d0 = Math.Max(0.5, Flight.Carry(b.Ev, b.La)), t0 = Flight.Hang(b.Ev, b.La);
				P first = P.Polar(d0, h);
				s.Add(SegKind.BallFly, 30, 0, t0, new P(0, 0), first);
				if (!r.Through && r.Fielder >= 0 && r.Class <= 1)
				{
					// Fielded on the infield.
					s.Add(SegKind.BallRoll, 30, t0, r.T, first, r.At, style: RollStyle(Flight.GroundDecel));
					int f = r.Fielder;
					bool realDive = r.How == Catch.DiveStop;
					bool scoop = !realDive && (f == Geometry.Pit || f == Geometry.C || r.How == Catch.Charging);
					// Booted (the resolver's ground error, the batter on): no clean pick. It squirts out in front of him and
					// to a side, he picks it up, and throws on all the same if anyone is still going (user, 2026-11-01:
					// "2루수가 슬라이딩해서 공 잡았는데 또 2루나 1루로 던지지도 않고"; it was drawn as a clean stop with
					// no play).
					bool booted = o.Error && o.BatterOnError && !o.ErrorThrow;
					bool dove = Glove(f, r.At, r.T, r.ShowDive || realDive, 1.0, booted ? CatchStyle.Bobble : r.How == Catch.Charging ? CatchStyle.Charging : CatchStyle.Grounder,
						booted ? CatchStyle.DiveMiss : CatchStyle.DiveStop, realDive ? BallPlan.GetUp : 0.5, scoop);
					m_busy[f] = true;
					if (!booted) return r.T;
					P glove = m_pos[f];
					double hd = h * Math.PI / 180, side = (Hash(plan.Ball) & 1) == 0 ? 1 : -1;
					P loose = new P(glove.X - 0.9 * Math.Cos(hd) - side * 0.9 * Math.Sin(hd), glove.Z - 0.9 * Math.Sin(hd) + side * 0.9 * Math.Cos(hd));
					s.Add(SegKind.BallLoose, 30, r.T, r.T + 0.45, glove, loose);
					m_free[f] = r.T + (dove ? BallPlan.GetUp : 0.35);
					double got = Math.Max(MoveTo(f, loose, m_free[f]), r.T + 0.5);
					s.Add(SegKind.BallLoose, 30, r.T + 0.45, got, loose, loose);
					s.Add(SegKind.Catch, f, got, got, loose, loose, style: CatchStyle.Pickup);
					m_free[f] = got + 0.15;
					return got;
				}
				// Through the infield: the man who had a shot dives and misses; it rolls on to the outfield.
				if (r.DoveMissed && plan.Fielder >= 0)
				{
					int f = plan.Fielder;
					Glove(f, plan.FieldPoint, plan.FieldTime, true, 1.0, CatchStyle.Grounder, CatchStyle.DiveMiss, 1.0);
				}
				Roll roll = Roll.Make(d0, t0, Flight.LandSpeed(b.Ev, b.La) * Flight.HopKeep, h, Geometry.FenceAt(h), Flight.GroundDecel);
				return Retrieve(roll, r, Fumbled(o, r));
			}
			// In the air.
			if (r.Class == 0)
			{
				int f = r.Fielder;
				// Where the ball is at the catch (a step short of where it would land).
				P at = r.How == Catch.Liner || r.How == Catch.Leaping ? r.At : P.Polar(Flight.DistAtTime(b.Ev, b.La, r.T), h);
				s.Add(SegKind.BallFly, 30, 0, r.T, new P(0, 0), at);
				byte style = r.How == Catch.Leaping ? CatchStyle.WallLeap : r.How == Catch.Liner ? CatchStyle.Liner : r.How == Catch.Running ? CatchStyle.Running : CatchStyle.Fly;
				// Dropped (the resolver's fly error, the batter on): no clean catch. A dive comes up empty, a
				// standing catch bobbles it; the ball pops out a stride on and he picks it up (user, 2026-10-31:
				// a shortstop's diving catch looked clean, then nobody was out and a run came in).
				bool dropped = o.Error && o.BatterOnError;
				bool dove = Glove(f, at, r.T, r.ShowDive && r.How != Catch.Leaping, 0.8, dropped ? CatchStyle.Bobble : style, dropped ? CatchStyle.DiveMiss : CatchStyle.Diving, 1.0);
				m_busy[f] = true;
				if (!dropped) return r.T;
				// Out of the glove in front of him and to a side (where the broadcast sees it, not behind his body).
				P glove = m_pos[f];
				double hd = h * Math.PI / 180, side = (Hash(plan.Ball) & 1) == 0 ? 1 : -1;
				P loose = new P(glove.X - 0.9 * Math.Cos(hd) - side * 0.9 * Math.Sin(hd), glove.Z - 0.9 * Math.Sin(hd) + side * 0.9 * Math.Cos(hd));
				s.Add(SegKind.BallLoose, 30, r.T, r.T + 0.45, glove, loose);
				m_free[f] = r.T + (dove ? (Geometry.IsOutfielder(f) ? BallPlan.OfGetUp : BallPlan.GetUp) : 0.35);
				double got = MoveTo(f, loose, m_free[f]);
				got = Math.Max(got, r.T + 0.5);
				s.Add(SegKind.BallLoose, 30, r.T + 0.45, got, loose, loose);
				s.Add(SegKind.Catch, f, got, got, loose, loose, style: CatchStyle.Pickup);
				m_free[f] = got + 0.15;
				return got;
			}
			// A hit in the air: it comes down (maybe off the wall); the man who tried may have dived.
			P land = P.Polar(plan.LandDist, h);
			s.Add(SegKind.BallFly, 30, 0, plan.LandTime, new P(0, 0), land);
			if (r.DoveMissed && plan.Catcher >= 0)
			{
				int f = plan.Catcher;
				P where = P.Polar(Flight.DistAtTime(b.Ev, b.La, plan.CatchTime), h);
				Glove(f, where, plan.CatchTime, true, 0.8, CatchStyle.Fly, CatchStyle.DiveMiss, BallPlan.OfGetUp);
			}
			double vl = Flight.LandSpeed(b.Ev, b.La);
			double fence = Geometry.FenceAt(h);
			Roll roll2 = plan.OffWall
				? Roll.Make(fence, plan.LandTime, vl * 0.5, h, fence, BallPlan.AirDecel)
				: Roll.Make(plan.LandDist, plan.LandTime, vl * BallPlan.AirKeep, h, fence, BallPlan.AirDecel);
			return Retrieve(roll2, r, Fumbled(o, r));
		}

		// The resolver's outfield fumble (everybody a base more, no throw): the man running the ball down boots it.
		private static bool Fumbled(FieldOutcome o, in BallPlan.Result r) => o.Error && !o.ErrorThrow && !o.BatterOnError && r.Class > 0 && o.ErrorFielder == r.Ret.F;

		// The loose ball rolls (off the wall maybe) until the retriever meets it; he runs to that point
		// and picks it up. Returns when he reaches it.
		private double Retrieve(Roll roll, in BallPlan.Result r, bool fumble = false)
		{
			Retrieval ret = r.Ret;
			double meet = Math.Max(roll.T0, ret.Meet);
			P end = roll.At(meet);
			int st = RollStyle(roll.Decel);
			if (roll.HitsWall && meet > roll.TWall)
			{
				s.Add(SegKind.BallRoll, 30, roll.T0, roll.TWall, roll.At(roll.T0), roll.At(roll.TWall), style: st);
				s.Add(SegKind.BallRoll, 30, roll.TWall, meet, roll.At(roll.TWall), end, arg: 1, style: st);
			}
			else
			{
				s.Add(SegKind.BallRoll, 30, roll.T0, meet, roll.At(roll.T0), end, style: st);
			}
			int f = ret.F;
			(P at, bool _) = Approach(f, end, meet, 0.7);
			Hold(f, meet);
			// The last step onto the ball as he scoops it up.
			if (at.Dist(end) > 0.2)
			{
				s.Add(SegKind.Move, f, meet, Math.Max(meet + 0.05, Math.Min(ret.T, meet + 0.2)), at, end);
			}
			m_busy[f] = true;
			if (fumble)
			{
				// Booted (user, 2026-10-31: three runs came in and "외야 장타가 펜스를 친건지 아님 누가 실책을 한건지 알 수가
				// 없음"): the ball gets away from him a couple of metres, on away from the plate and to a side, and he
				// chases it down; the runners' extra base is the resolver's.
				double side = (Hash(r.Ret.F * 31 + (int)(meet * 100)) & 1) == 0 ? 1.0 : -1.0;
				P on = Toward(end, Geometry.Base[0], -1.6);
				P loose = new P(on.X - side * (on.Z - end.Z), on.Z + side * (on.X - end.X));
				s.Add(SegKind.Catch, f, meet, meet, end, end, style: CatchStyle.Bobble);
				s.Add(SegKind.BallLoose, 30, meet, meet + 0.5, end, loose);
				m_pos[f] = end;
				m_free[f] = meet + 0.35;
				double got = Math.Max(MoveTo(f, loose, meet + 0.9), meet + 0.9);
				s.Add(SegKind.BallLoose, 30, meet + 0.5, got, loose, loose);
				double up = got + Math.Max(0.15, ret.T - meet);
				s.Add(SegKind.Catch, f, got, up, loose, loose, style: CatchStyle.Pickup);
				m_pos[f] = loose;
				m_free[f] = up;
				return got;
			}
			s.Add(SegKind.Catch, f, meet, ret.T, end, end, style: CatchStyle.Pickup);
			m_pos[f] = end;
			m_free[f] = ret.T;
			return meet;
		}

		public static int RollStyle(double decel) => (int)Math.Round(decel * 2);

		// Where a rolling ball is, u seconds into a roll piece of duration T over D metres at the given
		// deceleration: it slows evenly, and if it would stop short of the end it stops exactly there
		// (the same rule the broadcast client uses).
		public static double RollDist(double D, double T, double decel, double u)
		{
			if (T <= 1e-3) return D;
			double v0 = D / T + decel * T / 2;
			if (v0 - decel * T >= 0) return Math.Min(D, v0 * u - 0.5 * decel * u * u);
			double vs = Math.Sqrt(2 * decel * D);
			double ts = vs / decel;
			return u >= ts ? D : vs * u - 0.5 * decel * u * u;
		}

		private static P Toward(P target, P from, double back)
		{
			double d = target.Dist(from);
			if (d < back + 0.1) return from;
			double k = back / d;
			return new P(target.X + (from.X - target.X) * k, target.Z + (from.Z - target.Z) * k);
		}

		// ------------------------------------------------------------------ throws

		// Where a fielder takes a throw at a base: at first a stride off the bag on the fair side of the line, so the
		// batter-runner has the bag and the two never stand in one another. At second and third on the bag's far edge
		// (past it from the runner's way in) and a little to the infield side: the runner slides straight in at the bag
		// (user, 2026-11-02: "주자가 슬라이딩 하는 부분이 루 바깥쪽을 지나가는 듯? 조금 안쪽으로 … 수비수는 루 가장 바깥쪽을
		// 밟고 있으면 겹칠 일이 없을 것 같음"; a stride short of the bag on the infield side, the broadcast had to push the
		// runner out wide of it, Ma9MParkWarp.MakeRoom). At home the catcher on the first-base side of the plate and a
		// little behind it (he stood up the third-base line in front of it, where the runner slid into him). On a tag play
		// at first he is closer (0.6 m).
		public static P BagSpot(int baseNo, bool tag = false)
		{
			const double L = Geometry.BaseLine;
			double o = tag ? 0.6 : 0.8;   // a foot on the bag (2-3 head figures: as near as they can be without standing in the runner)
			switch (baseNo)
			{
				case 1: return new P(L - 0.4, o);
				case 2: return new P(L - 0.3, L + 0.45);
				case 3: return new P(-0.45, L - 0.3);
				default: return new P(0.5, -0.4);
			}
		}

		// Who takes a throw at a base: the same rules as the engine (Resolver.CoverOf) for infield
		// plays; on throws from the outfield the base's own man, the shortstop or second baseman at
		// second (whoever is not the cutoff), the catcher at home.
		private static int Receiver(int baseNo, int thrower, P from, int cut)
		{
			bool outfield = Geometry.IsOutfielder(thrower) || cut >= 0;
			switch (baseNo)
			{
				case 1: return thrower == Geometry.B1 ? Geometry.Pit : Geometry.B1;
				case 2:
					if (outfield) return cut == Geometry.SS ? Geometry.B2 : Geometry.SS;
					return thrower == Geometry.SS || thrower == Geometry.B3 || ((thrower == Geometry.Pit || thrower == Geometry.C) && from.H > 45) ? Geometry.B2 : Geometry.SS;
				case 3: return thrower == Geometry.B3 ? Geometry.SS : Geometry.B3;
				default: return Geometry.C;
			}
		}

		private double Throws(FieldOutcome o, int holder, double held, BallPlan plan, in BallPlan.Result r)
		{
			double t = held;
			int has = holder;
			if (has < 0) return t;
			double ready = m_free[has];
			if (o.Throws.Count == 0)
			{
				// No play: the ball comes back in to the infield (outfield: to the cutoff man at second).
				P at = m_pos[has];
				// A runner still going: the man with the ball throws there himself, a beat late — the outfielder home or to
				// third over the cutoff man, an infielder who booted it to first for the batter (user, 2026-11-01: "홈 던지라고
				// 시도를 하라고 했는데 왜 안 던지고 가만히 있냐고"; "2루수가 슬라이딩해서 공 잡았는데 또 2루나 1루로 던지지도
				// 않고"; the outfielder had only thrown back in to the cutoff man, after the run was in).
				double attempt = LateThrow(o, has, at, t, infield: !Geometry.IsOutfielder(has));
				if (attempt > 0) return attempt;
				if (Geometry.IsOutfielder(has))
				{
					int cut = at.H > 45 ? Geometry.SS : Geometry.B2;
					P c = Toward(Geometry.Base[2], at, Math.Min(25, at.Dist(Geometry.Base[2]) * 0.4));
					if (m_free[cut] > ready + 1.0) cut = cut == Geometry.SS ? Geometry.B2 : Geometry.SS;
					double rel = Math.Max(ready, t) + 0.6 + D.Extra * 0.5;
					double fly = at.Dist(c) / Math.Max(22, D.F[has].Arm * 0.75);
					// He throws when the cutoff man will be there to take it.
					double there = MoveTo(cut, c, rel + fly, hurry: false);
					rel = Math.Max(rel, there - fly);
					double arrive = rel + fly;
					Hold(cut, arrive);
					s.Add(SegKind.BallHeld, 30, t, rel, at, at, arg: has);
					s.Add(SegKind.Throw, has, rel, rel, at, c, arg: cut);
					s.Add(SegKind.BallThrow, 30, rel, arrive, at, c, arg: cut, style: at.Dist(c) > 45 ? 1 : 0);
					s.Add(SegKind.Catch, cut, arrive, arrive, c, c, style: CatchStyle.Receive);
					m_busy[cut] = true;
					double late = LateThrow(o, cut, c, arrive);
					if (late > 0) return late;
					s.Add(SegKind.BallHeld, 30, arrive, arrive + 1.0, c, c, arg: cut);
					return arrive;
				}
				s.Add(SegKind.BallHeld, 30, t, t + 1.0, at, at, arg: has);
				return t;
			}
			for (int ti = 0; ti < o.Throws.Count; ti++)
			{
				Throw th = o.Throws[ti];
				int from = th.From;
				P p0 = from == has ? m_pos[from] : th.FromPoint;
				bool tagPlay = Geometry.IsOutfielder(th.From) || th.Relay >= 0 || (th.Base == 0 && !(th.Runner == 3 && o.Track[1].Set && o.Track[2].Set));
				double ballAt = th.Arrive - (Geometry.IsOutfielder(th.From) ? Tag : 0);
				double late = 0;
				if (!th.Out && !th.Wild && th.Runner >= 0 && th.Runner < 4)
				{
					// The engine called him safe with the ball there first (its close calls carry a spread
					// for a bobbled transfer, a slow throw, a late tag): the throw goes a beat late, so it
					// gets there just after he does.
					RunnerTrack rt = o.Track[th.Runner];
					double there = rt.Set && !rt.Caught && rt.To % 4 == th.Base ? ArriveAt(rt, th.Runner) : double.NaN;
					if (!double.IsNaN(there) && ballAt < there + 0.1)
					{
						late = there + 0.12 - ballAt;
						ballAt += late;
						m_lateBall[th.Runner] = ballAt;
					}
				}
				P bag = th.Base >= 0 ? BagSpot(th.Base, tagPlay && th.Runner >= 0) : p0;
				// The thrower may be the pivot man at a base: he got there through the previous throw.
				m_pos[from] = p0;
				if (th.Step)
				{
					// He takes the ball to the bag himself: onto its near edge, not beside it; at second and third onto its far
					// edge (BagSpot), out of the way of the runner sliding in (2026-11-02: the second baseman's own step on
					// the bag stood on the runner's side of it).
					bag = th.Base == 2 || th.Base == 3 ? BagSpot(th.Base) : th.Base >= 0 ? Toward(Geometry.Base[th.Base], p0, 0.35) : p0;
					s.Add(SegKind.BallHeld, 30, t, ballAt, p0, bag, arg: from);
					s.Add(SegKind.Move, from, Math.Max(t, th.Release), ballAt, p0, bag);
					m_pos[from] = bag;
					m_free[from] = ballAt;
					if (th.Runner >= 0 && th.Runner < 4) m_tagSeg[th.Runner] = s.Segs.Count;
					s.Add(SegKind.Tag, from, ballAt, ballAt, bag, bag, arg: th.Runner, style: 0);
					if (th.Base == 1 && ti == o.Throws.Count - 1)
					{
						// Off the bag again before the batter gets there.
						P off = new P(bag.X - 1.2, bag.Z + 1.4);
						s.Add(SegKind.Move, from, ballAt + 0.25, ballAt + 0.85, bag, off);
						m_pos[from] = off;
						m_free[from] = ballAt + 0.85;
						s.Add(SegKind.BallHeld, 30, ballAt, ballAt + 0.85, bag, off, arg: from);
						t = ballAt + 0.85;
						has = from;
						continue;
					}
					t = ballAt;
					has = from;
					continue;
				}
				double rel = Math.Max(t, th.Release + late);
				int recv = Receiver(th.Base, from, p0, th.Relay);
				// The throw leaves when he is ready (the engine's release) and takes until the ball is
				// due there; only one that would float (under 60% of his arm) waits a beat, so it is
				// thrown at a believable speed. (Holding it and then firing it at full arm looked like
				// an outfielder standing still with the ball.)
				if (th.Relay < 0 && !th.Wild)
				{
					double slow = p0.Dist(bag) / Math.Max(14, D.F[from].Arm * 0.6);
					rel = Math.Max(rel, ballAt - slow);
				}
				s.Add(SegKind.BallHeld, 30, t, rel, p0, p0, arg: from);
				if (th.Relay >= 0)
				{
					int cut = th.Relay;
					P c = th.CutPoint;
					MoveTo(cut, c, th.CutArrive, hurry: false);
					Hold(cut, th.CutArrive);
					s.Add(SegKind.Throw, from, rel, rel, p0, c, arg: cut);
					s.Add(SegKind.BallThrow, 30, rel, th.CutArrive, p0, c, arg: cut, style: p0.Dist(c) > 45 ? 1 : 0);
					s.Add(SegKind.Catch, cut, th.CutArrive, th.CutArrive, c, c, style: CatchStyle.Receive);
					s.Add(SegKind.BallHeld, 30, th.CutArrive, th.CutRelease + late, c, c, arg: cut);
					m_busy[cut] = true;
					from = cut;
					p0 = c;
					rel = th.CutRelease + late;
				}
				m_busy[recv] = true;
				MoveTo(recv, bag, ballAt, hurry: false);
				Hold(recv, ballAt);
				s.Add(SegKind.Throw, from, rel, rel, p0, bag, arg: recv);
				if (th.Wild)
				{
					// Past the bag: it gets away and rolls on.
					P past = Toward(bag, p0, -12);
					s.Add(SegKind.BallThrow, 30, rel, ballAt, p0, bag, arg: recv, style: 0);
					s.Add(SegKind.BallLoose, 30, ballAt, ballAt + 1.6, bag, past, style: RollStyle(Flight.GroundDecel));
					s.Add(SegKind.Catch, recv, ballAt, ballAt, bag, bag, style: CatchStyle.Bobble);
					int chase = recv;
					MoveTo(chase, past, ballAt + 1.6);
					s.Add(SegKind.BallHeld, 30, ballAt + 1.6, ballAt + 2.6, past, past, arg: chase);
					t = ballAt + 2.6;
					has = chase;
					continue;
				}
				s.Add(SegKind.BallThrow, 30, rel, ballAt, p0, bag, arg: recv, style: p0.Dist(bag) > 45 ? 1 : 0);
				s.Add(SegKind.Catch, recv, ballAt, ballAt, bag, bag, style: CatchStyle.Receive);
				if (th.Runner >= 0)
				{
					if (th.Runner < 4) m_tagSeg[th.Runner] = s.Segs.Count;
					s.Add(SegKind.Tag, recv, tagPlay ? ballAt + Tag : ballAt, tagPlay ? ballAt + Tag : ballAt, bag, bag, arg: th.Runner, style: tagPlay ? 1 : 0);
				}
				m_free[recv] = ballAt + 0.3;
				t = ballAt;
				has = recv;
			}
			s.Add(SegKind.BallHeld, 30, t, t + 1.0, m_pos[has], m_pos[has], arg: has);
			return t;
		}

		// The cutoff man with the ball and a runner still going to second, third or home (no play called on him): he throws
		// there all the same, a beat late, rather than standing with it while they run (user, 2026-10-31: "이미 시나리오는
		// 홈에서 세잎이고 3루에서 세잎이더라도 그냥 던졌으면 함"). At the lead runner; the ball gets there just after him, so
		// the call is a close safe. Returns when it is caught there, or 0 (nobody still running).
		// The same for the man who fielded it with no play on (from when he is ready, ReadyTransfer later): an infielder
		// (infield) throws to first for the batter while he is still going, else at the lead runner.
		public const double CutTransfer = 0.45, ReadyTransfer = 0.3, LateBy = 0.15, LateHoldMax = 1.0;

		private double LateThrow(FieldOutcome o, int cut, P c, double caught, bool? infield = null)
		{
			double release = infield == null ? caught + CutTransfer : Math.Max(caught, m_free[cut]) + ReadyTransfer;
			bool batter = infield == true;
			int slot = -1, toBase = 0;
			double there = 0;
			for (int i = 0; i <= 3; i++)
			{
				RunnerTrack tr = o.Track[i];
				if (!tr.Set || tr.Out || tr.Hold || tr.Caught || tr.To <= i || tr.To < (batter ? 1 : 2)) continue;
				double arr = ArriveAt(tr, i);
				if (arr <= release + 0.1) continue;
				if (slot >= 0 && (batter ? slot == 0 || (i != 0 && tr.To < toBase) : tr.To < toBase)) continue;
				slot = i; toBase = tr.To; there = arr;
			}
			if (slot < 0) return 0;
			int baseNo = toBase % 4;
			int recv = Receiver(baseNo, cut, c, cut);
			if (recv == cut || m_busy[recv]) return 0;
			P bag = BagSpot(baseNo, true);
			double fly = c.Dist(bag) / Math.Max(22, D.F[cut].Arm);
			// Not ahead of him: he holds it a moment (looking the runner in) when a throw at once would beat him.
			double rel = Math.Max(release, there + LateBy - fly);
			if (rel - release > LateHoldMax) return 0;
			double ballAt = rel + fly;
			m_busy[recv] = true;
			MoveTo(recv, bag, ballAt, hurry: false);
			Hold(recv, ballAt);
			s.Add(SegKind.BallHeld, 30, caught, rel, c, c, arg: cut);
			s.Add(SegKind.Throw, cut, rel, rel, c, bag, arg: recv);
			s.Add(SegKind.BallThrow, 30, rel, ballAt, c, bag, arg: recv, style: c.Dist(bag) > 45 ? 1 : 0);
			s.Add(SegKind.Catch, recv, ballAt, ballAt, bag, bag, style: CatchStyle.Receive);
			s.Add(SegKind.BallHeld, 30, ballAt, ballAt + 1.0, bag, bag, arg: recv);
			m_free[cut] = rel + 0.3;
			m_free[recv] = ballAt + 0.3;
			m_lateBall[slot] = ballAt;
			return ballAt;
		}

		// ------------------------------------------------------------------ everybody else

		// Covers and backups nobody asked for: infielders to their bags, the cutoff man on a ball to
		// the outfield, outfielders backing up, the pitcher behind the base the play is going to.
		private void Helpers(BallPlan plan, in BallPlan.Result r, FieldOutcome o, int holder)
		{
			P ballAt = holder >= 0 ? m_pos[holder] : P.Polar(30, plan.Ball.H);
			bool outfieldBall = ballAt.R > 40;
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				if (m_busy[f] || f == Geometry.C) continue;
				P target = m_pos[f];
				switch (f)
				{
					case Geometry.B1: target = Geometry.Base[1]; break;
					case Geometry.B3: target = outfieldBall ? Geometry.Base[3] : m_pos[f]; break;
					case Geometry.B2:
					case Geometry.SS:
						if (outfieldBall)
						{
							bool cutSide = (f == Geometry.SS) == (ballAt.H > 45);
							target = cutSide ? Toward(Geometry.Base[2], ballAt, Math.Min(28, ballAt.Dist(Geometry.Base[2]) * 0.45)) : Geometry.Base[2];
						}
						break;
					case Geometry.Pit:
						target = outfieldBall ? new P(-4, 6) : m_pos[f];
						break;
					default:
						// Outfielders back up the play near them.
						if (Geometry.IsOutfielder(f) && holder >= 0 && Geometry.IsOutfielder(holder) && m_pos[f].Dist(ballAt) < 45)
						{
							target = Toward(ballAt, Geometry.Base[0], -8);
							target = Toward(target, m_pos[f], 4);
						}
						break;
				}
				if (target.Dist(m_pos[f]) > 0.5)
				{
					MoveTo(f, target, m_free[f] + 2, hurry: true);
				}
			}
		}

		// ------------------------------------------------------------------ runners

		private static P PathPoint(int fromBase, double along)
		{
			// Along the base path from base fromBase (0 home … 3 third) by `along` metres (may run past).
			double d = along;
			int b = fromBase;
			while (d > Geometry.BaseLine && b < 4)
			{
				d -= Geometry.BaseLine;
				b++;
			}
			P a = Geometry.Base[b % 4], c = Geometry.Base[(b + 1) % 4];
			double k = Math.Clamp(d / Geometry.BaseLine, -0.3, 1.3);
			return new P(a.X + (c.X - a.X) * k, a.Z + (c.Z - a.Z) * k);
		}

		private void Runners(BallPlan plan, in BallPlan.Result r, FieldOutcome o, BatterRun batter, RunnerSkill[] run, bool[] on, double last)
		{
			for (int slot = 0; slot <= 3; slot++)
			{
				RunnerTrack tr = o.Track[slot];
				if (!tr.Set) continue;
				int actor = RunnerActor + slot;
				RunnerSkill rs = slot == 0 ? batter.R : run[slot];
				int fromBase = slot;
				if (slot == 0 && tr.Caught)
				{
					// Ran toward first until the catch, then eased up and peeled off.
					double t1 = Math.Max(tr.Start + 0.5, tr.End + 0.6);
					double dist = Math.Min(Geometry.BaseLine * 0.8, rs.Run(Math.Max(0, t1 - tr.Start)) > 0 ? FMath.RunDist(t1 - tr.Start, rs.Speed, rs.Accel) * 0.85 : 5);
					s.Add(SegKind.Run, actor, tr.Start, t1, PathPoint(0, BatterRun.BoxOffset), PathPoint(0, dist), arg: 0x10);
					s.Add(SegKind.Peel, actor, t1, t1 + 1.5, PathPoint(0, dist), PathPoint(0, dist + 3));
					continue;
				}
				double off = slot == 0 ? BatterRun.BoxOffset : tr.Off;
				P lead = PathPoint(fromBase, off);
				if (tr.Hold && o.TwoOuts && slot > 0)
				{
					// Two out: he went on contact (user, 2026-10-31: "2아웃에는 무조건 주자들이 뛰어야하는데 가만히 있음").
					// The play that holds him ends the inning (the catch, the out at first), so he eases up short of
					// the next base when it is made; no run is drawn.
					double stop = Math.Max(1.0, (r.Class == 0 && !plan.Ground ? r.T : last) + 0.3);
					double room = Geometry.BaseLine - off - 3.0;
					double ran = Math.Min(room, FMath.RunDist(Math.Max(0, stop - 0.05), rs.Speed, rs.Accel));
					s.Add(SegKind.Run, actor, 0.05, stop, lead, PathPoint(fromBase, off + ran), arg: (byte)((fromBase << 4) | (fromBase + 1)));
					s.Add(SegKind.Stand, actor, stop, stop + 1.5, PathPoint(fromBase, off + ran), PathPoint(fromBase, off + ran), arg: fromBase);
					continue;
				}
				if (tr.Hold)
				{
					// Off the bag by his lead, back once the ball is dead.
					double back = Math.Max(1.0, double.IsNaN(tr.End) ? last - 0.5 : tr.End);
					s.Add(SegKind.Stand, actor, 0, back - 0.8, lead, lead, arg: fromBase);
					s.Add(SegKind.Back, actor, back - 0.8, back, lead, Geometry.Base[fromBase], arg: fromBase);
					continue;
				}
				if (tr.TagUp)
				{
					// Back to the bag while it hangs, then off at the catch.
					s.Add(SegKind.Back, actor, 0, Math.Min(1.0, tr.Start - 0.1), lead, Geometry.Base[fromBase], arg: fromBase);
					s.Add(SegKind.Stand, actor, Math.Min(1.0, tr.Start - 0.1), tr.Start, Geometry.Base[fromBase], Geometry.Base[fromBase], arg: fromBase);
					off = 0;
					lead = Geometry.Base[fromBase];
				}
				else
				{
					s.Add(SegKind.Stand, actor, 0, tr.Start, lead, lead, arg: fromBase);
				}
				if (tr.To == fromBase && slot > 0)
				{
					// Back to his own bag after a caught liner: he dives back, and is doubled off if
					// the throw beats him (the out is called when the ball gets there).
					P own = Geometry.Base[fromBase];
					double back = tr.Start + Math.Max(0.4, lead.Dist(own) / 5.5);
					// Doubled off: the ball beat him (the engine's call can be a close one decided by
					// the spread), so his hand reaches the bag just after it.
					if (tr.Out && m_tagSeg[slot] >= 0) back = Math.Max(back, s.Segs[m_tagSeg[slot]].T0 + 0.2);
					s.Add(SegKind.Back, actor, tr.Start, back - 0.45, lead, Toward(own, lead, 2.2), arg: fromBase);
					s.Add(SegKind.Slide, actor, back - 0.45, back, Toward(own, lead, 2.2), own, arg: fromBase, style: 1);
					if (tr.Out)
					{
						int ts = m_tagSeg[slot];
						double outAt = ts >= 0 ? Math.Min(back, s.Segs[ts].T0) + 0.1 : back;
						s.Add(SegKind.Out, actor, outAt, outAt + 0.8, own, own, arg: fromBase);
					}
					continue;
				}
				// Legs base to base, times from his speed, stretched to the engine's arrival if it gave one.
				int to = tr.To;
				double total = (to - fromBase) * Geometry.BaseLine - off;
				double natural = rs.RunFrom(Math.Max(0.5, total), tr.V0) + rs.Turn * Math.Max(0, to - fromBase - 1);
				double end = ArriveAt(tr, slot);
				if (tr.Out && end < tr.Start + natural * 0.7) end = tr.Start + natural * 0.7;
				if (tr.Out)
				{
					// Out by a step, not a tie (user, 2026-10-31: "주자나 타자가 0.1~2초정도 더 늦게 도착하는걸로 … 진짜 간발의
					// 차이로 아웃됐다 그런 느낌"): the ball is there 0.12-0.2 s before him (the engine's call can be closer).
					double ballThere = BallAtBase(o, slot);
					double margin = OutMargin + OutMarginSpread * ((Hash(plan.Ball) + slot * 37) % 100) / 100.0;
					if (!double.IsNaN(ballThere) && end < ballThere + margin) end = ballThere + margin;
				}
				double scale = (end - tr.Start) / Math.Max(0.1, natural);
				double t = tr.Start;
				double pos = off;
				for (int b = fromBase; b < to; b++)
				{
					double legEnd = (b + 1 - fromBase) * Geometry.BaseLine;
					double legLen = legEnd - (pos - 0);
					double done = rs.RunFrom(Math.Max(0.5, legEnd - off), tr.V0) + rs.Turn * Math.Max(0, b - fromBase);
					double tLeg = tr.Start + done * scale;
					P a = PathPoint(fromBase, pos), c = Geometry.Base[(b + 1) % 4];
					bool lastLeg = b + 1 == to;
					// The batter runs to first in the runner's lane, on the foul side of the line.
					if (slot == 0 && b == 0 && lastLeg) c = new P(Geometry.BaseLine, -0.5);
					s.Add(SegKind.Run, actor, t, tLeg, a, c, arg: (byte)((b << 4) | (b + 1)));
					t = tLeg;
					pos = legEnd;
					if (lastLeg)
					{
						int baseNo = (b + 1) % 4;
						// Close plays: he slides (head-first now and then at first, when it is that close).
						double ballAt = BallAtBase(o, slot);
						bool close = !double.IsNaN(ballAt) && Math.Abs(ballAt - t) < 0.6;
						if (tr.Out)
						{
							if (baseNo != 1 && close) s.Add(SegKind.Slide, actor, t - 0.5, t, Toward(c, a, 3.5), c, arg: baseNo, style: 0);
							double outAt = Math.Max(t, double.IsNaN(ballAt) ? t : ballAt);
							int ts = slot < 4 ? m_tagSeg[slot] : -1;
							if (ts >= 0)
							{
								Seg tag = s.Segs[ts];
								if (tag.Style == 1)
								{
									// A tag: it lands when he gets there.
									tag.T0 = tag.T1 = (float)Math.Max(tag.T0, t);
									s.Segs[ts] = tag;
									outAt = tag.T0;
								}
								else
								{
									// A force: called as his foot comes down a step behind the ball, or soon after the ball
									// when he is far behind (it was 0.1 s after the ball, before he got there).
									outAt = Math.Min(t, tag.T0 + 0.35);
								}
							}
							s.Add(SegKind.Out, actor, outAt, outAt + 0.8, c, c, arg: baseNo);
							if (slot == 0 && baseNo == 1)
							{
								// Through the bag all the same.
								s.Add(SegKind.Run, actor, t, t + 0.9, c, new P(Geometry.BaseLine + 4.5, -0.9), arg: 0x11);
							}
						}
						else
						{
							bool slid = false;
							if (close)
							{
								// At first only a head-first dive on a bang-bang play; elsewhere he slides.
								bool head = baseNo == 1 ? Math.Abs(ballAt - t) < 0.15 && (Hash(plan.Ball) % 3 == 0) : Hash(plan.Ball) % 4 == 0;
								if (baseNo != 1 || head)
								{
									s.Add(SegKind.Slide, actor, t - 0.5, t, Toward(c, a, 3.5), c, arg: baseNo, style: head ? 1 : 0);
									slid = true;
								}
								s.Add(SegKind.Safe, actor, t, t + 0.8, c, c, arg: baseNo);
							}
							if (baseNo == 0) s.Add(SegKind.Score, actor, t, t + 0.6, c, c);
							if (slot == 0 && baseNo == 1 && !slid)
							{
								P bag1 = Geometry.Base[1];
								if (Geometry.IsOutfielder(HolderOf(plan, r)))
								{
									// A hit to the outfield: he rounds first, looks, and goes back.
									P turn = new P(Geometry.BaseLine + 1.5, 3.5);
									s.Add(SegKind.Run, actor, t, t + 0.8, c, turn, arg: 0x12);
									s.Add(SegKind.Stand, actor, t + 0.8, t + 1.3, turn, turn, arg: 1);
									s.Add(SegKind.Back, actor, t + 1.3, t + 2.6, turn, bag1, arg: 1);
								}
								else
								{
									// Beat it out: through the bag, then back.
									P past = new P(Geometry.BaseLine + 4.5, -0.9);
									s.Add(SegKind.Run, actor, t, t + 0.9, c, past, arg: 0x11);
									s.Add(SegKind.Stand, actor, t + 0.9, t + 1.3, past, past, arg: 1);
									s.Add(SegKind.Back, actor, t + 1.3, t + 3.0, past, bag1, arg: 1);
								}
							}
						}
					}
				}
			}
		}

		// When a throw at this runner reached his base (NaN if none).
		// When a runner gets to the end of his run: the engine's time, or his legs' from his start.
		private double ArriveAt(in RunnerTrack tr, int slot)
		{
			if (!double.IsNaN(tr.End) && tr.End > tr.Start) return tr.End;
			RunnerSkill rs = m_runSk[slot];
			double off = tr.TagUp ? 0 : slot == 0 ? BatterRun.BoxOffset : tr.Off;
			double total = (tr.To - slot) * Geometry.BaseLine - off;
			return tr.Start + rs.RunFrom(Math.Max(0.5, total), tr.V0) + rs.Turn * Math.Max(0, tr.To - slot - 1);
		}

		private double BallAtBase(FieldOutcome o, int slot)
		{
			if (slot < 4 && !double.IsNaN(m_lateBall[slot])) return m_lateBall[slot];
			foreach (Throw t in o.Throws)
			{
				if (t.Runner == slot) return t.Arrive - (Geometry.IsOutfielder(t.From) ? Tag : 0);
			}
			return double.NaN;
		}

		private static int Hash(in BattedBall b) => (int)(Math.Abs(b.Ev * 7919 + b.La * 104729 + b.H * 1299709) % 1000);

		private static int Hash(int k) => (int)((uint)(k * 2654435761u) >> 7) % 1000;
	}
}
