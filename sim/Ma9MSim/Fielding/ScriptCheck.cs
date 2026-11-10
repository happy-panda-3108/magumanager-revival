using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// Plausibility checks on PlayScripts, run over thousands of plays without the client:
	// the ball meets the glove at every catch, nobody runs or throws faster than a man can, the ball's
	// path has no jumps, a fielder never has two moves at once, outs are called after the ball gets there.
	public static class ScriptCheck
	{
		public sealed class Report
		{
			public readonly Dictionary<string, int> Count = new Dictionary<string, int>();
			public readonly Dictionary<string, List<string>> Examples = new Dictionary<string, List<string>>();
			public int Plays;

			public void Flag(string what, string example)
			{
				Count[what] = Count.TryGetValue(what, out int c) ? c + 1 : 1;
				if (!Examples.TryGetValue(what, out List<string> l)) Examples[what] = l = new List<string>();
				if (l.Count < 3) l.Add(example);
			}
		}

		// Where the ball is at time t (ground point; the flight's height is not checked here).
		public static P BallAt(PlayScript s, double t)
		{
			Seg last = default;
			bool any = false;
			foreach (Seg g in s.Segs)
			{
				if (g.Actor != 30) continue;
				any = true;
				if (t >= g.T0 - 1e-4 && t <= g.T1 + 1e-4)
				{
					double u = g.T1 > g.T0 ? (t - g.T0) / (g.T1 - g.T0) : 1;
					switch (g.Kind)
					{
						case SegKind.BallFly:
						{
							// Along the flight, scaled so it ends where the script says it ends.
							double x = Flight.DistAtTime(s.Ball.Ev, s.Ball.La, t);
							double xEnd = Flight.DistAtTime(s.Ball.Ev, s.Ball.La, g.T1);
							double k = xEnd > 0.5 ? g.P1.R / xEnd : 1;
							return P.Polar(x * k, s.Ball.H);
						}
						case SegKind.BallRoll:
						case SegKind.BallLoose:
						{
							double len = g.P0.Dist(g.P1);
							double dd = Choreo.RollDist(len, g.T1 - g.T0, Math.Max(0.5, g.Style / 2.0), t - g.T0);
							double k = len > 1e-3 ? dd / len : 1;
							return new P(g.P0.X + (g.P1.X - g.P0.X) * k, g.P0.Z + (g.P1.Z - g.P0.Z) * k);
						}
						default:
							return new P(g.P0.X + (g.P1.X - g.P0.X) * u, g.P0.Z + (g.P1.Z - g.P0.Z) * u);
					}
				}
				last = g;
			}
			return any ? last.P1 : new P(0, 0);
		}

		public static double FlightDistAt(BattedBall b, double t)
		{
			double lo = 0, hi = Flight.Carry(b.Ev, b.La);
			for (int i = 0; i < 30; i++)
			{
				double mid = (lo + hi) / 2;
				(double _, double tm) = Flight.At(b.Ev, b.La, mid);
				if (double.IsNaN(tm) || tm > t) hi = mid; else lo = mid;
			}
			return lo;
		}

		// Where an actor (fielder 0-8, runner 20-23) is at time t from his Move/Dive/Run/... pieces.
		public static P ActorAt(PlayScript s, int actor, double t, P start)
		{
			P at = start;
			foreach (Seg g in s.Segs)
			{
				if (g.Actor != actor) continue;
				bool moves = g.Kind is SegKind.Move or SegKind.Dive or SegKind.Run or SegKind.Slide or SegKind.Back or SegKind.Peel or SegKind.Stand or SegKind.Wait;
				if (!moves) continue;
				if (t < g.T0) break;
				if (t <= g.T1)
				{
					double u = g.T1 > g.T0 ? (t - g.T0) / (g.T1 - g.T0) : 1;
					return new P(g.P0.X + (g.P1.X - g.P0.X) * u, g.P0.Z + (g.P1.Z - g.P0.Z) * u);
				}
				at = g.P1;
			}
			return at;
		}

		public static void Check(PlayScript s, Defence d, Report rep, string tag)
		{
			rep.Plays++;
			// 1. Catches: glove meets ball.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.Catch && g.Style != CatchStyle.Bobble))
			{
				P ball = BallAt(s, g.T0);
				P man = ActorAt(s, g.Actor, g.T0, d.Pos[g.Actor]);
				double miss = ball.Dist(man);
				double tol = g.Style is CatchStyle.Diving or CatchStyle.DiveStop ? 3.0 : 1.8;
				if (miss > tol) rep.Flag($"catch: glove {tol} m+ from ball", $"{tag} {Geometry.PosName[g.Actor]} st{g.Style} t{g.T0:F2} miss {miss:F1} m");
			}
			// 2. Moves: speed.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.Move && g.Actor < Geometry.FielderCount))
			{
				double dt = g.T1 - g.T0, dist = g.P0.Dist(g.P1);
				if (dt < -1e-3) { rep.Flag("move: ends before it starts", $"{tag} {Geometry.PosName[g.Actor]}"); continue; }
				double v = dt > 0.05 ? dist / dt : (dist > 0.5 ? 99 : 0);
				if (v > d.F[g.Actor].Speed * 1.05 + 0.3) rep.Flag("move: faster than the man", $"{tag} {Geometry.PosName[g.Actor]} {dist:F1} m in {dt:F2} s = {v:F1} m/s");
			}
			// 3. Throws: speed.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.BallThrow))
			{
				double dt = g.T1 - g.T0, dist = g.P0.Dist(g.P1);
				double v = dt > 0.02 ? dist / dt : 99;
				if (v > 48 || (v < 12 && dist > 8)) rep.Flag("throw: speed out of range", $"{tag} {dist:F1} m in {dt:F2} s = {v:F1} m/s");
			}
			// 4. Ball path continuity.
			List<Seg> balls = s.Segs.Where(g => g.Actor == 30).ToList();
			for (int i = 1; i < balls.Count; i++)
			{
				Seg a = balls[i - 1], b = balls[i];
				P aEnd = a.P1;
				if (Math.Abs(a.T1 - b.T0) > 0.06) rep.Flag("ball: time gap", $"{tag} {a.Kind}->{b.Kind} {a.T1:F2}/{b.T0:F2}");
				if (aEnd.Dist(b.P0) > 1.2) rep.Flag("ball: jumps", $"{tag} {a.Kind}->{b.Kind} {aEnd}->{b.P0} at {b.T0:F2}");
			}
			// 5. A fielder's moves never overlap.
			for (int f = 0; f < Geometry.FielderCount; f++)
			{
				List<Seg> m = s.Segs.Where(g => g.Actor == f && (g.Kind is SegKind.Move or SegKind.Dive or SegKind.Wait)).OrderBy(g => g.T0).ToList();
				for (int i = 1; i < m.Count; i++)
				{
					if (m[i].T0 < m[i - 1].T1 - 0.05) rep.Flag("fielder: moves overlap", $"{tag} {Geometry.PosName[f]} {m[i - 1].Kind} {m[i - 1].T0:F2}-{m[i - 1].T1:F2} / {m[i].Kind} {m[i].T0:F2}");
				}
			}
			// 6. Runners: speed, and no two on a base at the end.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.Run))
			{
				double dt = g.T1 - g.T0, dist = g.P0.Dist(g.P1);
				double v = dt > 0.05 ? dist / dt : 99;
				if (v > 10.8) rep.Flag("runner: too fast", $"{tag} r{g.Actor - Choreo.RunnerActor} {dist:F1} m in {dt:F2} s");
				if (dt < -1e-3) rep.Flag("runner: leg ends before it starts", $"{tag} r{g.Actor - Choreo.RunnerActor}");
			}
			// 7. Calls: an out is not called before the ball is at the base.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.Out))
			{
				P bag = g.P1;
				double ballThere = double.NaN;
				foreach (Seg b in balls)
				{
					if ((b.Kind == SegKind.BallThrow || b.Kind == SegKind.BallHeld) && b.P1.Dist(bag) < 2.0) { ballThere = b.Kind == SegKind.BallThrow ? b.T1 : b.T1; break; }
				}
				if (!double.IsNaN(ballThere) && g.T0 < ballThere - 0.05) rep.Flag("call: out before the ball", $"{tag} r{g.Actor - Choreo.RunnerActor} out {g.T0:F2} ball {ballThere:F2}");
			}
			// 8. Calls: a runner left safe at a base a throw reached well before him.
			foreach (Seg g in s.Segs.Where(g => g.Kind == SegKind.Run))
			{
				if (s.Segs.Any(o => o.Kind == SegKind.Out && o.Actor == g.Actor)) continue;
				int bag = Enumerable.Range(0, 4).FirstOrDefault(b => Geometry.Base[b].Dist(g.P1) < 1.5, -1);
				if (bag < 0) continue;
				foreach (Seg b in balls)
				{
					if (b.Kind == SegKind.BallThrow && b.P1.Dist(Geometry.Base[bag]) < 2.5 && b.T1 < g.T1 - 0.15 && b.T1 > g.T1 - 3)
					{
						// A throw that got away (it rolls on from the bag): safe all the same.
						if (balls.Any(l => l.Kind == SegKind.BallLoose && Math.Abs(l.T0 - b.T1) < 0.05)) continue;
						rep.Flag("call: safe after the ball", $"{tag} r{g.Actor - Choreo.RunnerActor} at {bag} {g.T1:F2} ball {b.T1:F2}");
						break;
					}
				}
			}
		}
	}
}
