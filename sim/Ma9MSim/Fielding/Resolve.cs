using System;
using System.Collections.Generic;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// One throw in a play, for the broadcast and the fielding record.
	public struct Throw
	{
		public int From;     // fielder (deck position, 8 = pitcher)
		public int Base;     // 0 home, 1-3; -1 to the cutoff only
		public int Relay;    // cutoff man who relayed it, -1 none
		public int Runner;   // slot thrown at (0 batter, 1-3 runner on that base), -1 none
		public bool Out;
		public bool Step;    // the fielder took the base himself (no throw)
		public bool Wild;    // throwing error
		public double Arrive;
		// For the broadcast: when it left the hand and from where; through a cutoff man, when he caught
		// it and threw it on (CutRelease) and where he stood.
		public double Release, CutArrive, CutRelease;
		public P FromPoint, CutPoint;
	}

	// One runner (slot 0 the batter, 1-3 the man on that base) through the play: he leaves From at
	// Start, Off metres along the path, already at V0 m/s; he ends on To (1-3, 4 home) at End, or is
	// put out there. TagUp: he waited on the bag for the catch. Caught: the batter's ball was caught.
	// Hold: he stays on From (after drifting off by Off). End NaN: work it out from his speed.
	public struct RunnerTrack
	{
		public bool Set, Out, TagUp, Caught, Hold;
		public int From, To;
		public double Start, Off, V0, End;
	}

	// What a ball in play did to everybody, in the engine's terms. Dest per slot (0 batter, 1-3 the
	// runner who stood on that base) follows PlayInfo: 0 stays (the batter: caught), 1-3 base,
	// 4 scores, 5-8 out at first, second, third, home.
	public sealed class FieldOutcome
	{
		public int Hit;                        // bases credited to the batter as a hit (0 none)
		public readonly int[] Dest = new int[4];
		public readonly bool[] Rbi = new bool[4];
		public int Outs;
		public bool Gidp, SacFly, Fc, Error, ErrorThrow, ErrorExtends, TriplePlay, DoubledOff, BatterOnError;
		public int ErrorFielder = -1;
		public int Assist = -1;                // outfielder who threw a runner out
		public Ma9MSim.PlayKind Kind;
		public BallPlan.Result Ball;
		public readonly List<Throw> Throws = new List<Throw>(4);
		public readonly RunnerTrack[] Track = new RunnerTrack[4];
		public double TagTime;          // tag-ups: when the ball was caught
		public bool TwoOuts;            // two out before the play (the broadcast's runners go on contact)

		public void Clear()
		{
			Hit = 0; Outs = 0;
			Array.Clear(Dest); Array.Clear(Rbi); Array.Clear(Track);
			TagTime = 0;
			TwoOuts = false;
			Gidp = SacFly = Fc = Error = ErrorThrow = ErrorExtends = TriplePlay = DoubledOff = BatterOnError = false;
			ErrorFielder = -1; Assist = -1;
			Throws.Clear();
		}
	}

	// Plays out a batted ball with runners on: tag-ups, force plays and double plays, outfield
	// throws, errors. Everything is decided from times (who gets where when) with a little noise on
	// close plays; the batter's own result (out, single, double, triple) comes in from BallPlan.
	public sealed class Resolver
	{
		public const double L = Geometry.BaseLine;
		public static readonly double[] Lead = { 0, 3.5, 4.5, 2.5 };       // off the bag at contact
		public static double[] Partway = { 0, 6.0, 5.0, 0.0 };    // extra way off on a fly with fewer than two outs (halfway-ish from first)
		public const double ForceSd = 0.12, DecideSd = 0.07, Tag = 0.15;
		// Tuned against the old engine's rates (--fieldlab plays): safe/out spread on throws from the
		// outfield (accuracy, hops), when runners break on a line-drive hit, the tag-up jump.
		// A fielder takes the ball to a bag himself only when he is on it (StepAlways), or a step or two away when that beats
		// a throw (StepMax); otherwise he tosses it to the man covering (user, 2026-10-31: "1루 주자 상황에서 유격수가 공을 잡고
		// 2루에 공을 안 던짐" — at 3 m always, 7 m when quicker, the shortstop walked to second with it).
		public static double StepAlways = 1.5, StepMax = 3.0;
		// The first baseman takes a grounder to first himself, running, when the bag is within StepFirst and he gets
		// there no more than StepFirstSlack after a toss to the pitcher covering would (user, 2026-11-01: "1루수가 땅볼을
		// 잡았는데 왜 투수가 베이스 커버 들어올때까지 기다려?? 본인이 1루로 뛰어가서 밟으면 되잖아"; the 3 m rule above
		// had him toss it from 4 m off the bag and wait for the pitcher). 16 m, was 10: from 10-16 m off the bag he still
		// stood with the ball 1-2.6 s for the pitcher (2026-11-02: half a percent of plays, about one a game in two).
		public static double StepFirst = 16.0, StepFirstSlack = 0.25;
		public static double LinerLead = 2.0, LinerReact = 0.5;    // secondary lead and freeze on a liner (doubled off), 2026-10-30
		public static double TagSd = 0.24, LineRead = 0.30, FlyLead = 2.6, TagJump = 0.0, Turn2 = 0.30, SureOut = 0.95, NeedShift = 0;
		// Safe chance a runner (his third-base coach) wants before going, by outs: scoring, going to
		// third; and on a tag-up.
		public static readonly double[] NeedHome = { 0.66, 0.45, 0.75 }, NeedThird = { 0.70, 0.68, 0.95 };   // full-game fit (--gametest) for 10-15 OF assists a team-season, 2026-10-30
		public static double TagHome0 = 0.74, TagHome1 = 0.62, TagThird0 = 0.88, TagThird1 = 0.92;

		// Error rates per chance for a league-average fielder (Hands/ThrowErr scale them).
		// Rates for a 65 fielder; a league of regulars (~73) makes about 0.55 errors a team-game.
		// This play's scale on all of them: the league's reach-on-error rate against the one the rates
		// were set under (the server's game_env ReachOnError, ErrRefRoe), the 작전실 field setting and,
		// in season replays, the team's own error rate (set per play by SeasonSimulator.PhysicalBip).
		public double ErrScale = 1;
		public static double ErrRefRoe = 0.0437;
		// The runner on third sent on contact on a grounder deep to the middle infield (ThirdOnThrowToFirst): 80%, was 65% on
		// every grounder played at first and 45% before that. Deep: 2B/SS fielding it this far from the plate or more (they
		// stand at 41 m). The first-base end's throw home after one, catch to release.
		public static double GoHomeOnThrow = 0.80, MiddleBack = 33, HomeTransfer = 0.5;
		public static double GroundErr = 0.027, ThrowErr = 0.0095, FlyErr = 0.0055, FumbleErr = 0.007, OfThrowErr = 0.014;

		public Defence D;
		public BatterRun Batter;
		public readonly RunnerSkill[] Run = new RunnerSkill[4];
		public readonly bool[] On = new bool[4];
		public int OutsBefore;
		public double Aggression;   // -1 (station to station) .. +1 (send everybody), 작전실 주루: the batter's setting (before 11-11)
		// Each runner's own (by his base, 1-3): his 도루 성향 sends him, not the batter's (2026-11-11, with the steal rule: a
		// 개인작전 적극 on a fast runner made the others run whenever he batted). Set by the engine; AggrByBatter = all Aggression.
		public readonly double[] Aggr = new double[4];

		private readonly FieldOutcome o = new FieldOutcome();

		// Safe chance a runner needs to try for that base: [base 2/3/home][outs before].
		private double Need(int toBase, int outs, int runner)
		{
			double n = toBase == 4 ? NeedHome[Math.Min(2, outs)] : toBase == 3 ? NeedThird[Math.Min(2, outs)] : 0.88;
			return n - 0.10 * Aggr[runner] - NeedShift;
		}

		private static double N(ref Rng rng) => BallGenerator.Normal(ref rng);

		private void Trk(int slot, int from, double start, double off, double v0, int to, bool outFlag, double end, bool tagUp = false, bool hold = false)
		{
			o.Track[slot] = new RunnerTrack { Set = true, From = from, Start = start, Off = off, V0 = v0, To = to, Out = outFlag, End = end, TagUp = tagUp, Hold = hold };
		}

		// After the play: every runner without a track yet gets a plain one from his Dest (errors,
		// runners who stayed), times worked out from his speed by the broadcast.
		// The runners not tracked yet go at start (NaN: they stay). On a ground ball (ground) the forced ones, and with two
		// out everybody, ran on contact whatever came of it — a boot, a wild throw (user, 2026-11-01: "2아웃인데 1루주자 출발
		// 안 하다가 … 그제서야 주자가 출발해서 2루에 도착하는 모습"; they all left with the error).
		private void FillTracks(double start, bool ground = false)
		{
			for (int b = 0; b <= 3; b++)
			{
				if (o.Track[b].Set || (b > 0 && !On[b])) continue;
				int d = o.Dest[b];
				if (b == 0)
				{
					Trk(0, 0, Batter.Start, 0, 0, d >= 5 ? d - 4 : d == 0 ? 1 : d, d >= 5, double.NaN);
				}
				else if (d == 0)
				{
					Trk(b, b, double.NaN, Lead[b], 0, b, false, double.NaN, hold: true);
				}
				else
				{
					bool forced = b == 1 || (b == 2 && On[1]) || (b == 3 && On[1] && On[2]);
					bool contact = ground && (forced || OutsBefore == 2);
					Trk(b, b, contact ? 0.05 : start, Lead[b], contact ? 1.5 : 0, d >= 5 ? d - 4 : d, d >= 5, double.NaN);
				}
			}
		}

		public FieldOutcome Resolve(BallPlan plan, in BallPlan.Result r, ref Rng rng)
		{
			o.Clear();
			o.Ball = r;
			o.TwoOuts = OutsBefore == 2;
			if (!plan.Ground && r.Class == 0)
			{
				AirOut(plan, r, ref rng);
			}
			else if (plan.Ground && r.Class <= 1 && !r.Through && r.Fielder >= 0)
			{
				GroundFielded(plan, r, ref rng);
			}
			else
			{
				OutfieldHit(plan, r, ref rng);
			}
			return o;
		}

		// ---------------------------------------------------------------- caught in the air

		private void AirOut(BallPlan plan, in BallPlan.Result r, ref Rng rng)
		{
			int f = r.Fielder;
			FielderSkill fs = D.F[f];
			bool liner = r.How == Catch.Liner || (plan.Ball.Type == BallType.Line && !Geometry.IsOutfielder(f));
			o.Kind = plan.Ball.Type == BallType.Line ? Ma9MSim.PlayKind.LineOut : Ma9MSim.PlayKind.FlyOut;
			// Dropped: everybody moves up, the batter is on (second on a deep drop now and then).
			double pE = ErrScale * FlyErr * fs.Hands * (r.How == Catch.Diving || r.How == Catch.Running || r.How == Catch.Leaping ? 2.5 : 1);
			if (rng.Next() < pE)
			{
				o.Error = true; o.ErrorFielder = f; o.BatterOnError = true;
				o.Kind = Ma9MSim.PlayKind.Error;
				int bb = Geometry.IsOutfielder(f) && rng.Next() < 0.3 ? 2 : 1;
				o.Dest[0] = bb;
				o.ErrorExtends = OutsBefore == 2;
				if (OutsBefore == 2)
				{
					DroppedTwoOut(plan, r, bb);
					return;
				}
				AdvanceAll(bb, 1, ref rng);
				FillTracks(r.T + 0.3);
				return;
			}
			o.Dest[0] = 0;
			o.Outs = 1;
			o.Track[0] = new RunnerTrack { Set = true, From = 0, To = 1, Start = Batter.Start, Caught = true, End = r.T };
			int outs = OutsBefore + 1;
			if (outs >= 3)
			{
				FillTracks(double.NaN);
				return;
			}
			double tc = r.T;
			P q = r.At;
			o.TagTime = tc;
			if (liner)
			{
				// Runners froze at their leads; one far off the bag the catcher can reach is doubled off.
				for (int b = 1; b <= 3 && outs < 3; b++)
				{
					if (!On[b]) continue;
					int cover = b == 1 ? Geometry.B1 : b == 2 ? (f == Geometry.SS ? Geometry.B2 : Geometry.SS) : Geometry.B3;
					P bag = Geometry.Base[b];
					double coverAt = cover == f ? 0 : D.F[cover].RunTime(D.Pos[cover].Dist(bag));
					double tf = cover == f
						? tc + Math.Max(0.05, fs.RunTime(q.Dist(bag)) - fs.React)
						: Math.Max(tc + 0.35 + q.Dist(bag) / fs.Arm, coverAt + 0.05);
					// He was at his secondary lead when it was hit, froze on the liner and drifted back
					// while it was in the air; then dives back.
					double left = Math.Max(0, Lead[b] + LinerLead - 4.0 * Math.Max(0, tc - LinerReact));
					double tb = tc + 0.25 + left / 6.0;
					if (tb - tf + ForceSd * N(ref rng) > 0)
					{
						o.Dest[b] = 4 + b;
						o.Outs++;
						outs++;
						o.DoubledOff = true;
						o.Throws.Add(new Throw { From = f, Base = b, Relay = -1, Runner = b, Out = true, Step = cover == f, Arrive = tf, Release = cover == f ? tc : tc + 0.35, FromPoint = q });
						Trk(b, b, tc + 0.25, Lead[b] + LinerLead, 0, b, true, tf);
					}
					else
					{
						Trk(b, b, tc + 0.25, left, 0, b, false, tb, hold: true);
					}
				}
				FillTracks(double.NaN);
				return;
			}
			if (!Geometry.IsOutfielder(f) || (plan.Ball.La < 20 && q.R < 75))
			{
				FillTracks(double.NaN);
				return;   // pop-up, infield fly or a shallow liner: nobody tags
			}
			// Tag-ups: who goes, then where the throw goes.
			bool[] go = new bool[4];
			double[] arr = new double[4], thr = new double[4];
			int[] relay = new int[4];
			double[] pSafe = new double[4];
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b]) continue;
				int to = b + 1;
				if (to <= 3 && On[to] && !go[to]) continue;   // the base ahead stays occupied
				arr[b] = tc + 0.15 - TagJump + Run[b].Run(L);
				(double t, int rl) = D.ThrowTime(f, q, to % 4);
				thr[b] = tc + t + Tag;
				relay[b] = rl;
				pSafe[b] = FMath.Phi((thr[b] - arr[b]) / TagSd);
				double need = to == 4 ? (OutsBefore == 0 ? TagHome0 : TagHome1) : to == 3 ? (OutsBefore == 0 ? TagThird0 : TagThird1) : 0.97;
				go[b] = pSafe[b] + DecideSd * N(ref rng) >= need - 0.08 * Aggr[b];
			}
			// The throw goes after the lead runner who can be had; the others are safe.
			int target = -1;
			for (int b = 3; b >= 1; b--)
			{
				if (go[b] && 1 - pSafe[b] >= 0.12) { target = b; break; }
			}
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b]) continue;
				if (!go[b]) { o.Dest[b] = 0; continue; }
				int to = b + 1;
				if (b == target)
				{
					bool safe = thr[b] - arr[b] + TagSd * N(ref rng) >= 0;
					Defence.ThrowLeg leg = D.ThrowPlan(f, q, to % 4);
					o.Throws.Add(new Throw { From = f, Base = to % 4, Relay = relay[b], Runner = b, Out = !safe, Arrive = thr[b], Release = tc + leg.Release, CutArrive = tc + leg.CutArrive, CutRelease = tc + leg.CutRelease, CutPoint = leg.CutPoint, FromPoint = q });
					if (!safe)
					{
						o.Dest[b] = to == 4 ? 8 : 4 + to;
						o.Outs++;
						o.Assist = f;
						Trk(b, b, tc + 0.15 - TagJump, 0, 0, to, true, thr[b], tagUp: true);
						continue;
					}
				}
				Trk(b, b, tc + 0.15 - TagJump, 0, 0, to, false, arr[b], tagUp: true);
				o.Dest[b] = to;
				if (to == 4)
				{
					o.Rbi[b] = true;
					o.SacFly = true;
				}
			}
			if (o.SacFly)
			{
				o.Kind = Ma9MSim.PlayKind.SacFly;
			}
			// A run on a tag-up counts unless the third out came first (the runner thrown out was ahead of him).
			if (OutsBefore + o.Outs >= 3)
			{
				for (int b = 1; b <= 3; b++)
				{
					if (o.Dest[b] == 4 && target >= 0 && arr[b] > thr[target]) { o.Dest[b] = 0; o.Rbi[b] = false; o.SacFly = false; }
				}
				if (!o.SacFly && o.Kind == Ma9MSim.PlayKind.SacFly) o.Kind = Ma9MSim.PlayKind.FlyOut;
			}
			for (int b = 1; b <= 3; b++)
			{
				if (On[b] && o.Track[b].Set && !o.Track[b].Out && o.Dest[b] == 0) { o.Track[b].To = b; o.Track[b].Hold = true; }
			}
			FillTracks(double.NaN);
		}

		// A ball dropped in the air with two out: everybody ran on contact, so the drop finds the runners well on their way
		// and they keep going (user, 2026-10-31: "떨군걸 보고 뛰는게 아니라 그냥 뛰고 있다가 떨구고 다시 상황재개될때 루에
		// 붙어있는식으로"). Each takes the farthest base he makes well ahead of a throw from where the ball is picked up
		// (no play is made on anyone); a runner never passes the one ahead.
		private void DroppedTwoOut(BallPlan plan, in BallPlan.Result r, int bb)
		{
			int f = r.Fielder;
			const double Start = 0.10, V0 = 2.0, Spare = 0.4;
			double pickup = r.T + (r.ShowDive ? (Geometry.IsOutfielder(f) ? BallPlan.OfGetUp : BallPlan.GetUp) + 0.2 : 0.7);
			double Arr(int b, int to) => Start + Run[b].RunFrom((to - b) * L - Lead[b], V0) + Run[b].Turn * Math.Max(0, to - b - 1);
			int[] minTo = new int[4];
			int push = bb;
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				minTo[b] = Math.Min(4, Math.Max(b + 1, b <= push ? push + 1 : b + 1));
				push = Math.Max(push, minTo[b]);
			}
			int cap = 4;
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b]) continue;
				int to = Math.Min(minTo[b], cap);
				for (int k = cap; k > minTo[b]; k--)
				{
					(double t, int _) = D.ThrowTime(f, r.At, k % 4);
					if (pickup + t + Tag - Arr(b, k) >= Spare) { to = k; break; }
				}
				o.Dest[b] = to;
				cap = to == 4 ? 4 : to - 1;
			}
			Normalize();
			for (int b = 1; b <= 3; b++)
			{
				if (On[b]) Trk(b, b, Start, Lead[b], V0, o.Dest[b] == 0 ? b : o.Dest[b], false, double.NaN);
			}
			FillTracks(pickup);
		}

		// ---------------------------------------------------------------- ground ball fielded

		// A pivot man already set on the bag when the ball comes turns it quicker.
		public static double EarlyPivot = 0.7, RelaySd = 0.22;

		private static double PivotTime(in FielderSkill p, double ballAt, double coverAt) => p.Pivot * (ballAt - coverAt >= 0.5 ? EarlyPivot : 1.0);

		private int CoverOf(int baseNo, int f, P q)
		{
			switch (baseNo)
			{
				case 1: return f == Geometry.B1 ? Geometry.Pit : Geometry.B1;
				case 2: return f == Geometry.SS || f == Geometry.B3 || (f == Geometry.Pit && q.H > 45) || (f == Geometry.C && q.H > 45) ? Geometry.B2 : Geometry.SS;
				case 3: return f == Geometry.B3 ? Geometry.SS : Geometry.B3;
				default: return Geometry.C;
			}
		}

		private double CoverTime(int baseNo, int f, P q)
		{
			int c = CoverOf(baseNo, f, q);
			if (c == Geometry.C) return 0;
			return D.F[c].RunTime(D.Pos[c].Dist(Geometry.Base[baseNo]));
		}

		// Ball arrival at a base from a fielder holding it at point q at time t0 (or him stepping on it).
		private double Arrival(int thrower, P q, double t0, int baseNo, int fielder, P fq, out bool step) => Arrival(thrower, q, t0, baseNo, fielder, fq, out step, out _);

		private double Arrival(int thrower, P q, double t0, int baseNo, int fielder, P fq, out bool step, out double release)
		{
			P bag = Geometry.Base[baseNo];
			double d = q.Dist(bag);
			FielderSkill s = D.F[thrower];
			// A few strides to the bag himself, when that beats throwing to the man covering it.
			double walk = t0 + Math.Max(0.1, d / Math.Max(4, s.Speed * 0.7));
			if (d < StepAlways)
			{
				step = true;
				release = t0;
				return walk;
			}
			// Turning to throw to second costs the first baseman, the pitcher and the catcher a beat.
			double turn = baseNo == 2 && (thrower == Geometry.B1 || thrower == Geometry.Pit || thrower == Geometry.C) ? Turn2 : 0;
			release = t0 + s.Release + turn;
			double thrown = Math.Max(release + d / s.Arm, CoverTime(baseNo, fielder, fq) + 0.05);
			if (baseNo == 1 && thrower == Geometry.B1 && d < StepFirst)
			{
				double run = t0 + Math.Max(0.1, FMath.RunTime(d, s.Speed, s.Accel));
				if (run <= thrown + StepFirstSlack)
				{
					step = true;
					release = t0;
					return run;
				}
			}
			step = d < StepMax && walk < thrown;
			if (step) release = t0;
			return step ? walk : thrown;
		}

		private void GroundFielded(BallPlan plan, in BallPlan.Result r, ref Rng rng)
		{
			int f = r.Fielder;
			FielderSkill fs = D.F[f];
			P q = r.At;
			double tg = r.T + 0.12 + (r.How == Catch.DiveStop ? BallPlan.GetUp : 0);
			bool f1 = On[1], f2 = On[1] && On[2], f3 = On[1] && On[2] && On[3];
			// Booted.
			double pE = ErrScale * GroundErr * fs.Hands * (r.How == Catch.DiveStop ? 1.7 : r.How == Catch.Charging ? 1.3 : 1) * (plan.Ball.Ev > 42 ? 1.3 : 1);
			if (rng.Next() < pE)
			{
				o.Error = true; o.ErrorFielder = f; o.BatterOnError = true;
				o.Kind = Ma9MSim.PlayKind.Error;
				o.Dest[0] = 1;
				AdvanceAll(1, 0, ref rng);
				o.ErrorExtends = OutsBefore == 2 || (OutsBefore == 1 && f1);
				FillTracks(r.T + 0.4, ground: true);
				return;
			}
			bool twoOut = OutsBefore == 2;
			// Runners' times to the next base (forced, or everybody with two out, run on contact).
			double R(int b, double start, double v0) => start + Run[b].RunFrom(L - Lead[b], v0);
			double r1 = f1 ? R(1, 0.05, 1.5) : 0;
			double r2 = On[2] ? R(2, f2 || twoOut ? 0.05 : 0.15, f2 || twoOut ? 1.5 : 0.5) : 0;
			double r3 = On[3] ? R(3, f3 || twoOut ? 0.05 : 0.10, 1.0) : 0;
			// Unforced runners: the one on second goes when the ball is to his right; the one on third
			// reads whether he can beat a throw home.
			bool go2 = On[2] && (f2 || twoOut || q.H < 45);
			bool go3 = false;
			double aHome = Arrival(f, q, tg, 0, f, q, out _) + Tag;
			if (On[3])
			{
				go3 = f3 || twoOut || FMath.Phi((aHome - r3) / TagSd) + DecideSd * N(ref rng) >= 0.62 - 0.08 * Aggr[3];
			}
			double bt1 = plan.BT1;
			// Options for the first throw.
			double a1 = Arrival(f, q, tg, 1, f, q, out bool s1, out double rel1);
			double p1 = FMath.Phi((bt1 - a1) / ForceSd);
			double best = p1 * 1.0;
			int first = 1;
			double a2 = 0, a3 = 0, ah = 0, p2 = 0, p3 = 0, ph = 0;
			bool s2 = false, s3 = false, sh = false;
			int piv2 = -1;
			double relay2 = 0, relay3 = 0, relayH = 0;
			double rel2 = 0, rel3 = 0, relH = 0;
			if (f1)
			{
				a2 = Arrival(f, q, tg, 2, f, q, out s2, out rel2);
				p2 = FMath.Phi((r1 - a2) / ForceSd);
				piv2 = s2 ? f : CoverOf(2, f, q);
				FielderSkill ps = D.F[piv2];
				double a1r = Math.Max(a2 + (s2 ? 0.25 : PivotTime(ps, a2, CoverTime(2, f, q))) + L / ps.Arm, CoverTime(1, f, q) + 0.05);
				relay2 = FMath.Phi((bt1 - a1r) / RelaySd);
				double v = twoOut ? p2 : p2 * 1.15 + p2 * relay2;
				if (p1 >= SureOut && p2 < 0.9 && relay2 < 0.3) v = 0;
				if (v > best) { best = v; first = 2; }
			}
			if (f2)
			{
				a3 = Arrival(f, q, tg, 3, f, q, out s3, out rel3);
				p3 = FMath.Phi((r2 - a3) / ForceSd);
				int c3 = s3 ? f : CoverOf(3, f, q);
				FielderSkill cs = D.F[c3];
				double a1r = Math.Max(a3 + (s3 ? 0.30 : 0.45) + Geometry.Base[3].Dist(Geometry.Base[1]) / cs.Arm, CoverTime(1, f, q) + 0.05);
				relay3 = FMath.Phi((bt1 - a1r) / RelaySd);
				double v = twoOut ? p3 : p3 * 1.25 + p3 * relay3;
				if (v > best) { best = v; first = 3; }
			}
			if (On[3] && (f3 || go3))
			{
				ah = Arrival(f, q, tg, 0, f, q, out sh, out relH);
				ph = f3 ? FMath.Phi((r3 - ah) / ForceSd) : FMath.Phi((r3 - ah - Tag) / TagSd);
				FielderSkill cs = D.F[Geometry.C];
				double a1r = Math.Max(ah + 0.45 + L / cs.Arm, CoverTime(1, f, q) + 0.05);
				relayH = f3 ? FMath.Phi((bt1 - a1r) / RelaySd) : 0;
				double v = twoOut ? ph : ph * 1.6 + ph * relayH;
				if (v > best) { best = v; first = 4; }
			}

			int outs = OutsBefore;
			bool batterOut = false, batterDone = false;
			int[] dest = o.Dest;
			for (int b = 1; b <= 3; b++) dest[b] = 0;

			// Throws, in order; a wild one gives everybody a base.
			bool Wild(int thrower, double margin, ref Rng g)
			{
				double pw = ErrScale * ThrowErr * D.F[thrower].ThrowErr * (Math.Abs(margin) < 0.25 ? 1.7 : 1);
				return g.Next() < pw;
			}

			if (first == 1)
			{
				// To first: the batter's fate is the coupled one (plan class).
				bool wild = !s1 && Wild(f, bt1 - a1, ref rng);
				batterOut = r.Class == 0 && !wild;
				o.Throws.Add(new Throw { From = f, Base = 1, Relay = -1, Runner = 0, Out = batterOut, Step = s1, Wild = wild, Arrive = a1, Release = rel1, FromPoint = q });
				if (wild)
				{
					o.Error = o.ErrorThrow = true; o.ErrorFielder = f; o.BatterOnError = true;
					o.Kind = Ma9MSim.PlayKind.Error;
					int bb = rng.Next() < 0.6 ? 2 : 1;
					dest[0] = bb;
					AdvanceAll(bb, 1, ref rng);
					o.ErrorExtends = OutsBefore == 2 || (OutsBefore == 1 && f1);
					FillTracks(a1 + 0.2, ground: true);
					return;
				}
				batterDone = true;
				if (batterOut) { dest[0] = 5; outs++; }
				else { dest[0] = 1; o.Hit = 1; }
			}
			else
			{
				int lead = first == 4 ? 3 : first - 1;   // runner thrown at
				int baseNo = first == 4 ? 0 : first;
				double a = first == 2 ? a2 : first == 3 ? a3 : ah;
				double rt = first == 2 ? r1 : first == 3 ? r2 : r3;
				bool step = first == 2 ? s2 : first == 3 ? s3 : sh;
				bool force = first != 4 || f3;
				bool wild = !step && Wild(f, rt - a, ref rng);
				bool outLead = !wild && (force ? rt - a + ForceSd * N(ref rng) > 0 : rt - a - Tag + TagSd * N(ref rng) > 0);
				double relLead = first == 2 ? rel2 : first == 3 ? rel3 : relH;
				o.Throws.Add(new Throw { From = f, Base = baseNo, Relay = -1, Runner = lead, Out = outLead, Step = step, Wild = wild, Arrive = a, Release = relLead, FromPoint = q });
				if (wild)
				{
					o.Error = o.ErrorThrow = true; o.ErrorFielder = f;
					o.Kind = Ma9MSim.PlayKind.Error;
					dest[0] = 1;
					o.BatterOnError = r.Class == 0;
					o.Hit = r.Class == 0 ? 0 : 1;
					AdvanceAll(1, 1, ref rng);
					o.ErrorExtends = OutsBefore == 2;
					FillTracks(a + 0.2, ground: true);
					return;
				}
				if (outLead)
				{
					dest[lead] = first == 4 ? 8 : 4 + first;
					outs++;
					o.Fc = true;
					// Relay for two (or three) if the inning is still going.
					if (outs < 3)
					{
						int pivot = first == 2 ? piv2 : first == 3 ? (s3 ? f : CoverOf(3, f, q)) : Geometry.C;
						FielderSkill ps = D.F[pivot];
						P from = Geometry.Base[baseNo];
						double t0 = a + (first == 2 ? (s2 ? 0.25 : PivotTime(ps, a, CoverTime(2, f, q))) : first == 3 ? (s3 ? 0.30 : 0.45) : 0.45);
						// Third-to-second-to-first with nobody out and runners on first and second: the triple play.
						if (first == 3 && OutsBefore == 0 && f1)
						{
							int c2 = Geometry.B2;
							double a2b = Math.Max(t0 + L / ps.Arm, CoverTime(2, f, q) + 0.05);
							if (r1 - a2b + ForceSd * N(ref rng) > 0)
							{
								dest[1] = 6;
								outs++;
								o.Throws.Add(new Throw { From = pivot, Base = 2, Relay = -1, Runner = 1, Out = true, Arrive = a2b, Release = t0, FromPoint = from });
								FielderSkill p2s = D.F[c2];
								double a1c = Math.Max(a2b + PivotTime(p2s, a2b, CoverTime(2, f, q)) + L / p2s.Arm, CoverTime(1, f, q) + 0.05);
								bool outB = bt1 - a1c + RelaySd * N(ref rng) > 0;
								o.Throws.Add(new Throw { From = c2, Base = 1, Relay = -1, Runner = 0, Out = outB, Arrive = a1c, Release = a2b + PivotTime(p2s, a2b, CoverTime(2, f, q)), FromPoint = Geometry.Base[2] });
								batterDone = true;
								if (outB) { dest[0] = 5; outs++; batterOut = true; o.TriplePlay = true; }
								else dest[0] = 1;
								goto Rest;
							}
						}
						double a1r = Math.Max(t0 + from.Dist(Geometry.Base[1]) / ps.Arm, CoverTime(1, f, q) + 0.05);
						bool tryTwo = force && FMath.Phi((bt1 - a1r) / RelaySd) > 0.15;
						if (tryTwo)
						{
							bool wild2 = Wild(pivot, bt1 - a1r, ref rng);
							bool outB = !wild2 && bt1 - a1r + RelaySd * N(ref rng) > 0;
							o.Throws.Add(new Throw { From = pivot, Base = 1, Relay = -1, Runner = 0, Out = outB, Wild = wild2, Arrive = a1r, Release = t0, FromPoint = from });
							batterDone = true;
							if (wild2)
							{
								o.Error = o.ErrorThrow = true; o.ErrorFielder = pivot;
								dest[0] = 2;
							}
							else if (outB) { dest[0] = 5; outs++; batterOut = true; }
							else dest[0] = 1;
						}
					}
				}
				if (!batterDone)
				{
					dest[0] = 1;
					batterDone = true;
					// All safe after trying for the lead runner: a hit if he beat it out anyway.
					if (!outLead) { o.Hit = r.Class >= 1 ? 1 : 0; o.Fc = r.Class == 0; }
				}
			}

		Rest:
			o.Outs = outs - OutsBefore;
			if (o.Outs >= 2 && batterOut) o.Gidp = true;
			// The client counts the batter out on its own for a double play: only when he was.
			o.Kind = o.Outs >= 2 && batterOut ? Ma9MSim.PlayKind.DoublePlay : o.Hit > 0 ? Ma9MSim.PlayKind.Single : Ma9MSim.PlayKind.GroundOut;
			bool inningOver = outs >= 3;
			bool sent3 = false, sent2 = false;
			// A batter safe at first beat the throw there: the broadcast shows it arriving just after him (Choreo, a safe call's
			// throw), so a throw on from first, and a runner going as it is seen to leave, come that much later (2026-11-11: on
			// infield hits the throw to third left the first baseman's hand as he caught it and flew at 50 m/s).
			double shift1 = first == 1 && o.Throws.Count > 0 ? FirstShift(o.Throws[0], bt1) : 0;
			// Everybody not thrown at: forced runners take the next base, the others as they went.
			int occupied = dest[0] >= 1 && dest[0] <= 3 ? dest[0] : 0;
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b] || dest[b] >= 5) continue;
				bool forced = b == 1 || (b == 2 && f1) || (b == 3 && f2);
				int to;
				if (forced) to = b + 1;
				else if (b == 2 && SecondThrowRule && !go2 && first == 1 && !inningOver && !On[3]) to = SecondOnThrowToFirst(f, q, shift1, ref rng, ref outs, out sent2);
				else if (b == 2) to = go2 || (first == 1 && rng.Next() < (q.H < 45 ? 0.7 : 0.25)) ? 3 : 2;
				else if (go3 || first != 1 || inningOver) to = go3 ? 4 : 3;
				else to = ThirdOnThrowToFirst(f, q, ShiftRule ? shift1 : 0, ref rng, ref outs, out sent3);
				dest[b] = to == b ? 0 : to;
			}
			if ((dest[3] == 8 || dest[2] == 7) && first == 1)
			{
				// Out at the plate after the out (or the hit) at first: one more out (a double play with the batter out;
				// not a GIDP, nobody was forced), and maybe the inning.
				o.Outs = outs - OutsBefore;
				o.Kind = o.Outs >= 2 && batterOut ? Ma9MSim.PlayKind.DoublePlay : o.Hit > 0 ? Ma9MSim.PlayKind.Single : Ma9MSim.PlayKind.GroundOut;
				inningOver = outs >= 3;
			}
			// Make sure nobody shares a base: a runner whose base ahead stays taken holds.
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b] || dest[b] == 0 || dest[b] >= 4) continue;
				for (int a = b + 1; a <= 3; a++)
				{
					if (On[a] && (dest[a] == 0 ? a : dest[a]) == dest[b]) { dest[b] = dest[b] - 1 == b ? 0 : dest[b] - 1; }
				}
			}
			Normalize();
			if (inningOver)
			{
				// No run scores when the third out is a force (or the batter at first). For the picture,
				// runners still alive move up when the base ahead is free, else stay.
				bool[] taken = new bool[4];
				for (int b = 3; b >= 1; b--)
				{
					if (!On[b] || dest[b] >= 5) continue;
					int want = dest[b] == 0 ? b : Math.Min(3, dest[b]);
					if (want != b && taken[want]) want = b;
					dest[b] = want == b ? 0 : want;
					taken[want] = true;
				}
			}
			for (int b = 1; b <= 3; b++)
			{
				if (dest[b] == 4) o.Rbi[b] = !o.Gidp && !o.Error;
			}
			// Tracks: who ran from contact and who went only on the throw.
			double firstRelease = o.Throws.Count > 0 ? SeenRelease(o.Throws[0]) + (ShiftRule || sent2 ? shift1 : 0) : tg;
			double OutAt(int slot)
			{
				foreach (Throw t in o.Throws) if (t.Runner == slot && t.Out) return t.Arrive;
				return double.NaN;
			}
			int bd = dest[0];
			Trk(0, 0, Batter.Start, 0, 0, bd >= 5 ? bd - 4 : Math.Max(1, bd), bd >= 5, bd >= 5 ? OutAt(0) : double.NaN);
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				int d = dest[b];
				bool forced = b == 1 || (b == 2 && f1) || (b == 3 && f2);
				bool contact = forced || twoOut || (b == 2 && go2) || (b == 3 && (go3 || sent3));
				double st = b == 1 ? 0.05 : b == 2 ? (f2 || twoOut ? 0.05 : 0.15) : (f3 || twoOut ? 0.05 : 0.10);
				double v = b == 1 ? 1.5 : b == 2 ? (f2 || twoOut ? 1.5 : 0.5) : 1.0;
				if (d == 0)
				{
					Trk(b, b, double.NaN, Lead[b], 0, b, false, double.NaN, hold: true);
				}
				else if (contact)
				{
					Trk(b, b, st, Lead[b], v, d >= 5 ? d - 4 : d, d >= 5, d >= 5 ? OutAt(b) : double.NaN);
				}
				else
				{
					Trk(b, b, firstRelease + 0.1, Lead[b], 0, d >= 5 ? d - 4 : d, d >= 5, d >= 5 ? OutAt(b) : double.NaN);
				}
			}
		}

		// The runner on third, not forced, fewer than two out, who did not go on contact, on a ground ball the defence plays
		// at first (the infield is back: there is no infield in). Deep to the middle infield he is sent all the same and the
		// run is given up for the out (user, 2026-11-02: "1사 3루에서 유격이나 2루 깊은 땅볼 때 주자는 홈으로 들어오게 보내주고
		// 1루에 바로 승부"); anywhere else he is held, and he breaks on the throw to first only when he can beat the throw
		// home that follows, which is then made, out or safe (user: "결과가 세이프더라도 시도는 해야"; under the 10-31 rule he
		// went 65% of the time and scored with nobody throwing). Where he ends: 3 held, 4 scores, 8 out at home.
		private int ThirdOnThrowToFirst(int f, P q, double shift, ref Rng rng, ref int outs, out bool sent)
		{
			sent = false;
			if ((f == Geometry.B2 || f == Geometry.SS) && q.R >= MiddleBack)
			{
				sent = rng.Next() < GoHomeOnThrow + 0.1 * Aggr[3];
				return sent ? 4 : 3;
			}
			// The man with the ball at first (the fielder who took it there himself, or the one covering) throws home.
			Throw t1 = o.Throws[0];
			int recv = t1.Step ? t1.From : CoverOf(1, f, q);
			double release = t1.Arrive + shift + HomeTransfer;
			double ball = release + L / D.F[recv].Arm;
			// He goes as the ball is seen to leave for first (the track's start), from his lead, standing.
			double there = SeenRelease(t1) + shift + 0.1 + Run[3].RunFrom(L - Lead[3], 0);
			if (FMath.Phi((ball + Tag - there) / TagSd) + DecideSd * N(ref rng) < 0.62 - 0.08 * Aggr[3]) return 3;
			bool outHome = there - ball - Tag + TagSd * N(ref rng) > 0;
			o.Throws.Add(new Throw { From = recv, Base = 0, Relay = -1, Runner = 3, Out = outHome, Arrive = ball, Release = release, FromPoint = Geometry.Base[1] });
			if (outHome) outs++;
			return outHome ? 8 : 4;
		}

		// The runner on second, not forced, who did not go on contact (the ball was not to the right side), third base empty, on
		// a ground ball the defence plays at first (user, 2026-11-11: "1사 2루에서 투수 땅볼때 1루로 던졌는데 2루주자가 3루로 뛰었음.
		// 이때 1루수가 아웃 잡고 3루로 던져야하는데 안 던짐 … 이런거 세잎이더라도 3루에 던져서 태그까지"): he breaks for third on the throw
		// to first only when he can beat the throw to third that follows (as ThirdOnThrowToFirst for home), which is then made,
		// out or safe. Before, he went a quarter of the time whatever the throw would do, and nobody threw. Where he ends: 2 held,
		// 3 safe, 7 out at third. MA9M_SECOND_ON_THROW=0 = the rule before.
		public static readonly bool SecondThrowRule = Environment.GetEnvironmentVariable("MA9M_SECOND_ON_THROW") != "0";
		public static double ThirdTransfer = 0.5;
		// How sure he must be of beating the throw to go (as the runner on third's 0.62). --fieldlab script --secondbar -9
		// makes every such runner go, to look at the throw to third and the tag (none went in 300k plays at 0.62).
		public static double SecondGoBar = 0.62;

		// How much later than the engine's arrival the broadcast shows the first throw at first: a safe call's throw is made
		// to get there just after the batter (Choreo, the same 0.1/0.12 s). MA9M_FIRST_SHIFT=0 = the runner from third as
		// before (the runner from second always uses it: his rule is new).
		public static readonly bool ShiftRule = Environment.GetEnvironmentVariable("MA9M_FIRST_SHIFT") != "0";

		private static double FirstShift(in Throw t1, double bt1) => !t1.Out && !t1.Wild && t1.Arrive < bt1 + 0.1 ? bt1 + 0.12 - t1.Arrive : 0;

		private int SecondOnThrowToFirst(int f, P q, double shift, ref Rng rng, ref int outs, out bool sent)
		{
			sent = false;
			Throw t1 = o.Throws[0];
			int recv = t1.Step ? t1.From : CoverOf(1, f, q);
			double release = t1.Arrive + shift + ThirdTransfer;
			double ball = release + Geometry.Base[1].Dist(Geometry.Base[3]) / D.F[recv].Arm;
			// He goes as the ball is seen to leave for first, from his lead, standing.
			double there = SeenRelease(t1) + shift + 0.1 + Run[2].RunFrom(L - Lead[2], 0);
			if (FMath.Phi((ball + Tag - there) / TagSd) + DecideSd * N(ref rng) < SecondGoBar - 0.08 * Aggr[2]) return 2;
			sent = true;
			bool out3 = there - ball - Tag + TagSd * N(ref rng) > 0;
			o.Throws.Add(new Throw { From = recv, Base = 3, Relay = -1, Runner = 2, Out = out3, Arrive = ball, Release = release, FromPoint = Geometry.Base[1] });
			if (out3) outs++;
			return out3 ? 7 : 3;
		}

		// When a throw is seen to leave his hand: the broadcast holds one that would float to a man still on his way to the
		// bag until it can go at 60% of his arm (Choreo.Throws), so the runners who go on the throw go then, not while he
		// stands there with it (2026-11-02: a first baseman waited 2.6 s for the pitcher to cover, and the man on third ran
		// home under his nose from the engine's release).
		private double SeenRelease(in Throw t)
		{
			if (t.Step || t.Base < 0 || t.Relay >= 0) return t.Release;
			return Math.Max(t.Release, t.Arrive - t.FromPoint.Dist(Choreo.BagSpot(t.Base)) / Math.Max(14, D.F[t.From].Arm * 0.6));
		}

		// ---------------------------------------------------------------- base hits

		private void OutfieldHit(BallPlan plan, in BallPlan.Result r, ref Rng rng)
		{
			Retrieval ret = r.Ret;
			int f = ret.F;
			P q = ret.At;
			double tr = ret.T;
			int cls = Math.Clamp(r.Class, 1, 3);
			o.Hit = cls;
			o.Kind = cls == 1 ? Ma9MSim.PlayKind.Single : cls == 2 ? Ma9MSim.PlayKind.Double : Ma9MSim.PlayKind.Triple;
			int[] dest = o.Dest;
			int outs = OutsBefore;
			bool stretchOut = r.OutStretching > 0;
			int bb = stretchOut ? r.OutStretching : cls;   // where the batter ends (or is out)
			int bbSafe = stretchOut ? r.OutStretching - 1 : cls;
			// When each runner starts and from where.
			bool twoOut = OutsBefore == 2;
			double[] start = new double[4], off = new double[4], v0 = new double[4];
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				off[b] = Lead[b];
				if (plan.Ground)
				{
					bool forced = b == 1 || (b == 2 && On[1]) || (b == 3 && On[1] && On[2]);
					start[b] = forced || twoOut ? 0.05 : 0.45;
					v0[b] = forced || twoOut ? 1.5 : 0;
				}
				else if (twoOut)
				{
					start[b] = 0.10; v0[b] = 2.0;
				}
				else if (plan.Ball.La < 22)
				{
					// A liner: he holds until it is past the infielders, then runs.
					start[b] = Math.Min(plan.LandTime, LineRead) + 0.05; v0[b] = 1.0;
				}
				else
				{
					// A fly: partway off the bag, he runs once he can tell it will drop.
					off[b] += Partway[b];
					start[b] = Math.Max(0.6, plan.LandTime - FlyLead);
				}
			}
			double Arr(int b, int to) => start[b] + Run[b].RunFrom((to - b) * L - off[b], v0[b]) + Run[b].Turn * Math.Max(0, to - b - 1);
			// Least each runner must reach (pushed by the batter and the runners behind).
			int[] minTo = new int[4];
			int push = Math.Max(1, bbSafe);
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				int m = Math.Max(b + 1, b <= push ? push + 1 : b + 1);
				minTo[b] = Math.Min(4, m);
				push = Math.Max(push, minTo[b]);
			}
			// Lead runner first: how far does each go?
			int[] to = new int[4];
			double[] pSafe = new double[4], aT = new double[4];
			int[] rl = new int[4];
			int cap = 4;
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b]) continue;
				int choice = Math.Min(minTo[b], cap);
				for (int k = cap; k > minTo[b]; k--)
				{
					(double t, int rly) = D.ThrowTime(f, q, k % 4);
					double a = tr + t + Tag;
					double ps = FMath.Phi((a - Arr(b, k)) / TagSd);
					if (ps + DecideSd * N(ref rng) >= Need(k, OutsBefore, b))
					{
						choice = k;
						break;
					}
				}
				to[b] = choice;
				(double tt, int rr) = D.ThrowTime(f, q, choice % 4);
				aT[b] = tr + tt + Tag;
				rl[b] = rr;
				pSafe[b] = FMath.Phi((aT[b] - Arr(b, choice)) / TagSd);
				cap = choice == 4 ? 4 : choice - 1;
			}
			// The throw: after the lead runner worth throwing at (home first), else just back in.
			int target = -1;
			double bestV = 0;
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b] || to[b] < 3) continue;
				double v = (1 - pSafe[b]) * (to[b] == 4 ? 1.5 : 1.2);
				if (1 - pSafe[b] >= 0.12 && v > bestV) { bestV = v; target = b; }
			}
			// Bobbled in the outfield: one more base for everybody.
			if (rng.Next() < ErrScale * FumbleErr * D.F[f].Hands * (ret.OffWall ? 1.5 : 1))
			{
				o.Error = true; o.ErrorFielder = f;
				for (int b = 1; b <= 3; b++) if (On[b]) dest[b] = 4;
				dest[0] = stretchOut ? Math.Min(3, bb) : Math.Min(3, cls + 1);
				for (int b = 1; b <= 3; b++) if (dest[b] == 4) o.Rbi[b] = true;
				for (int b = 1; b <= 3; b++) if (On[b]) Trk(b, b, start[b], off[b], v0[b], 4, false, double.NaN);
				FillTracks(tr);
				return;
			}
			double thirdOutAt = double.MaxValue;
			if (stretchOut)
			{
				dest[0] = 4 + bb;
				outs++;
				o.Assist = (bb == 2 ? ret.Relay2 : ret.Relay3) >= 0 ? (bb == 2 ? ret.Relay2 : ret.Relay3) : f;
				thirdOutAt = outs >= 3 ? (bb == 2 ? ret.Throw2 : ret.Throw3) : double.MaxValue;
				Defence.ThrowLeg sl = D.ThrowPlan(f, q, bb);
				o.Throws.Add(new Throw { From = f, Base = bb, Relay = bb == 2 ? ret.Relay2 : ret.Relay3, Runner = 0, Out = true, Arrive = bb == 2 ? ret.Throw2 : ret.Throw3, Release = tr + sl.Release, CutArrive = tr + sl.CutArrive, CutRelease = tr + sl.CutRelease, CutPoint = sl.CutPoint, FromPoint = q });
			}
			else
			{
				dest[0] = cls;
			}
			for (int b = 3; b >= 1; b--)
			{
				if (!On[b]) continue;
				dest[b] = to[b];
				if (b == target && outs < 3)
				{
					bool wild = rng.Next() < ErrScale * OfThrowErr * D.F[f].ThrowErr;
					bool safe = wild || aT[b] - Arr(b, to[b]) + TagSd * N(ref rng) >= 0;
					Defence.ThrowLeg tl = D.ThrowPlan(f, q, to[b] % 4);
					o.Throws.Add(new Throw { From = f, Base = to[b] % 4, Relay = rl[b], Runner = b, Out = !safe, Wild = wild, Arrive = aT[b], Release = tr + tl.Release, CutArrive = tr + tl.CutArrive, CutRelease = tr + tl.CutRelease, CutPoint = tl.CutPoint, FromPoint = q });
					if (wild)
					{
						o.Error = o.ErrorThrow = true; o.ErrorFielder = f;
					}
					if (!safe)
					{
						dest[b] = to[b] == 4 ? 8 : 4 + to[b];
						outs++;
						o.Assist = rl[b] >= 0 ? rl[b] : f;
						if (outs >= 3) thirdOutAt = aT[b];
					}
				}
			}
			// The batter takes an extra base on a throw home or to third that he can beat.
			if (!stretchOut && target >= 0 && outs < 3 && dest[0] < 3)
			{
				int next = dest[0] + 1;
				bool free = true;
				for (int b = 1; b <= 3; b++) if (On[b] && dest[b] == next) free = false;
				double tb = plan.BT1 + (next == 2 ? plan.BT2 - plan.BT1 : plan.BT3 - plan.BT2);
				double tb2 = next == 2 ? plan.BT2 : plan.BT3;
				if (free && FMath.Phi((aT[target] + 0.9 - tb2) / TagSd) + DecideSd * N(ref rng) >= 0.75)
				{
					dest[0] = next;
				}
			}
			// Runs score unless the third out was made before the runner touched the plate.
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b] || dest[b] != 4) continue;
				if (outs >= 3 && Arr(b, 4) >= thirdOutAt)
				{
					dest[b] = 3;
					continue;
				}
				o.Rbi[b] = true;
			}
			o.Outs = outs - OutsBefore;
			Normalize();
			// Tracks.
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				int d = dest[b];
				bool isOut = d >= 5;
				int k = isOut ? d - 4 : d == 0 ? b : d;
				Trk(b, b, start[b], off[b], v0[b], k, isOut, isOut ? aT[b] : (k > b ? Arr(b, k) : double.NaN));
			}
			int bd = dest[0];
			if (bd >= 5)
			{
				Trk(0, 0, Batter.Start, 0, 0, bd - 4, true, bd - 4 == 2 ? ret.Throw2 : ret.Throw3);
			}
			else
			{
				Trk(0, 0, Batter.Start, 0, 0, bd, false, bd == 1 ? plan.BT1 : bd == 2 ? plan.BT2 : plan.BT3);
			}
		}

		// Runners never pass each other or share a base: walking up from the batter, anyone who would
		// end on or behind the man behind him moves one base past him (a wild throw's extra base).
		private void Normalize()
		{
			int[] dest = o.Dest;
			int prev = dest[0] >= 1 && dest[0] <= 3 ? dest[0] : 0;
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b] || dest[b] >= 5 || dest[b] == 4) continue;
				int at = dest[b] == 0 ? b : dest[b];
				if (at <= prev) at = prev + 1;
				dest[b] = at >= 4 ? 4 : at == b ? 0 : at;
				prev = Math.Min(at, 4);
			}
		}

		// Everybody one base, the batter to `batterTo`; runners not forced go `extra` more with
		// some chance (an error on a throw lets the ball get away).
		private void AdvanceAll(int batterTo, int extra, ref Rng rng)
		{
			int push = batterTo;
			for (int b = 1; b <= 3; b++)
			{
				if (!On[b]) continue;
				int to = b <= push ? push + 1 : b + (rng.Next() < 0.5 ? extra : 0);
				to = Math.Max(to, b + (extra > 0 ? 1 : 0));
				to = Math.Min(4, to);
				o.Dest[b] = to == b ? 0 : to;
				push = Math.Max(push, to);
			}
		}
	}
}
