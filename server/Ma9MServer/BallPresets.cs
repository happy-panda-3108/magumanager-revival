using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ma9MServer
{
	// ePos values used by ball presets and the text broadcast.
	public enum FieldPos
	{
		P = 0, C = 1, B1 = 2, B2 = 3, B3 = 4, SS = 5, LF = 6, CF = 7, RF = 8,
		None = 11   // the client's ePos.POS_NONE: no fielder in that slot
	}

	// eBatterRecord: the text-broadcast line for a batted ball.
	public enum BatterRecord
	{
		None = 0,
		InfieldHit1B = 1, InfieldHit2B = 2, InfieldHit3B = 3, InfieldHitSS = 4,
		SingleLF = 5, SingleCF = 6, SingleRF = 7,
		DoubleBackLF = 8, DoubleLeftLF = 9, DoubleBetweenLC = 10, DoubleBackCF = 11, DoubleBetweenCR = 12, DoubleBackRF = 13, DoubleRightRF = 14,
		TripleLeftLF = 15, TripleBetweenLC = 16, TripleBackCF = 17, TripleBetweenCR = 18, TripleBackRF = 19, TripleRightRF = 20,
		HrLF = 21, HrCF = 22, HrRF = 23,
		FlyP = 28, SacFlyP = 37, LineDriveP = 46, Bunt = 55
	}

	public enum PresetKind
	{
		GroundOut, DoublePlay, FlyOut, SacFly, LineDrive, InfieldHit, Single, Double, Triple, HomeRun, Error, Bunt
	}

	public sealed class BallPreset
	{
		public short Code;
		public PresetKind Kind;
		public float Speed, VAngle, HAngle;
		public FieldPos Chase, Backup1, Backup2;
		public bool Hit;
		public int Fly;
		public float CatchTime, CatchX, CatchZ;
		public int HitType;     // 1 single, 2 double, 3 triple, 4 inside-the-park homer, 5 homer
		public bool Fence, DoublePlay, Bunt;
		public int CatchType;   // 0 normal, 1 error, 2 diving, 3 jump
		public BatterRecord Sns;
		public int Sub;         // the double/triple/single/homer direction it stands for
		public int Ground;      // 0 every park, else eGround + 1 (the client merges the two sets)
	}

	// An outfield wall: distance and height by spray angle (0 = first-base line, 90 = third-base
	// line), from server/data/fences/<park>.json (written by the client's Ma9MStadiumImporter).
	public sealed class FenceProfile
	{
		public string Name = "Default";
		private (double angle, double r, double h)[] m_points = Enumerable.Range(0, 37)
			.Select(i => (90.0 - 2.5 * i, 98.0 + 22.0 * Math.Sin(2 * (90.0 - 2.5 * i) * Math.PI / 180), 2.5)).ToArray();

		public static FenceProfile Load(string path)
		{
			using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
			List<(double, double, double)> pts = new List<(double, double, double)>();
			foreach (System.Text.Json.JsonElement e in doc.RootElement.GetProperty("points").EnumerateArray())
			{
				double x = e.GetProperty("x").GetDouble(), y = e.GetProperty("y").GetDouble(), z = e.GetProperty("z").GetDouble();
				pts.Add((Math.Atan2(z, x) * 180 / Math.PI, Math.Sqrt(x * x + z * z), y));
			}
			return new FenceProfile { Name = Path.GetFileNameWithoutExtension(path), m_points = pts.OrderByDescending(p => p.Item1).ToArray() };
		}

		// Distance and height of the wall along this spray angle.
		public (double r, double h) At(double angle)
		{
			for (int i = 0; i + 1 < m_points.Length; i++)
			{
				(double a0, double r0, double h0) = m_points[i];
				(double a1, double r1, double h1) = m_points[i + 1];
				if (angle <= a0 && angle >= a1)
				{
					double t = a0 == a1 ? 0 : (a0 - angle) / (a0 - a1);
					return (r0 + (r1 - r0) * t, h0 + (h1 - h0) * t);
				}
			}
			(double _, double r, double h) = angle > m_points[0].angle ? m_points[0] : m_points[^1];
			return (r, h);
		}
	}

	// The client's BallPreset table (lost with the original CDN), rebuilt from the client's own
	// ball physics (Ball.cs): the ball leaves (0, 1, 0) at speed·(cos v cos h, sin v, cos v sin h),
	// gravity 16.562, no drag; the first bounce keeps 0.6 of the run and 0.2 of the drop, later
	// bounces half the drop, then it rolls to a stop at 10.14. h = 0 is the first-base line, 90
	// the third-base line; units are metres. Fielders run to the catch point by the catch time
	// on their own, so the presets only have to be plausible.
	public sealed class BallPresetCatalog
	{
		private const double G = 16.561998;

		// Fielder start positions (FielderManager), indexed by FieldPos.
		private static readonly (double x, double z)[] Home =
		{
			(13.04, 13.04), (-1.5, -1.5), (30.41, 6.36), (34.65, 21.92), (6.36, 30.41), (21.92, 34.65), (24.04, 79.2), (60.1, 60.1), (79.2, 24.04)
		};

		public readonly List<BallPreset> All = new List<BallPreset>();

		private readonly Dictionary<(PresetKind kind, int key), List<BallPreset>> m_index = new Dictionary<(PresetKind, int), List<BallPreset>>();

		private short m_next;

		public static readonly string[] GroundParks =
		{
			"Jamsil", "Sajik", "Daejeon", "Daegu", "Munhak", "Kwangju", "MaguDome", "Suwon", "", "Masan", "Munhak", "Munhak", "MaguDome"
		};

		private FenceProfile[] m_fences;

		public static BallPresetCatalog Build(string fenceDir = null)
		{
			BallPresetCatalog c = new BallPresetCatalog();
			c.m_fences = GroundParks.Select(name =>
			{
				string path = fenceDir != null && name.Length > 0 ? Path.Combine(fenceDir, name + ".json") : null;
				return path != null && File.Exists(path) ? FenceProfile.Load(path) : new FenceProfile();
			}).ToArray();
			c.Generate();
			foreach (BallPreset p in c.ForGround(1))
			{
				int key = p.Kind is PresetKind.Single or PresetKind.Double or PresetKind.Triple or PresetKind.HomeRun or PresetKind.InfieldHit ? p.Sub : (int)p.Chase;
				if (!c.m_index.TryGetValue((p.Kind, key), out List<BallPreset> list))
				{
					list = new List<BallPreset>();
					c.m_index[(p.Kind, key)] = list;
				}
				list.Add(p);
			}
			return c;
		}

		// A preset of this kind for this fielder (outs, infield hits, errors, bunts) or this
		// direction (BatterRecord for singles, doubles, triples, homers); any of the kind if none.
		public BallPreset Pick(PresetKind kind, int key, double u)
		{
			if (!m_index.TryGetValue((kind, key), out List<BallPreset> list))
			{
				list = All.Where(p => p.Kind == kind).ToList();
			}
			return list[Math.Min(list.Count - 1, (int)(u * list.Count))];
		}

		public bool Has(PresetKind kind, int key) => m_index.ContainsKey((kind, key));

		// What the client holds for a park (eGround + 1): the shared presets and that park's own.
		public IEnumerable<BallPreset> ForGround(int ground) => All.Where(p => p.Ground == 0 || p.Ground == ground);

		public IEnumerable<string> FenceNames => m_fences.Select(f => f.Name);

		public void WriteTable(string path)
		{
			CultureInfo inv = CultureInfo.InvariantCulture;
			StringBuilder sb = new StringBuilder();
			sb.Append("Code\t구역\t공의스피드\tV각\tH각\t체이스포지션\t백업포지션1\t백업포지션2\tHit/Out\tflyOut\t공잡는시간\t공잡는위치_X\t공잡는위치_Z\tHit_type\t1루주자추가진루\t2루주자추가진루\t3루주자추가진루\t펜스닿는여부\tdouble_play\t번트가능여부\t에러/호수비\t구장정보\t문자중계\n");
			foreach (BallPreset p in All)
			{
				sb.Append(string.Join("\t", new[]
				{
					p.Code.ToString(inv), ((int)p.Kind).ToString(inv), p.Speed.ToString("0.###", inv), p.VAngle.ToString("0.###", inv), p.HAngle.ToString("0.###", inv),
					((int)p.Chase).ToString(inv), ((int)p.Backup1).ToString(inv), ((int)p.Backup2).ToString(inv), p.Hit ? "1" : "0", p.Fly.ToString(inv),
					p.CatchTime.ToString("0.###", inv), p.CatchX.ToString("0.###", inv), p.CatchZ.ToString("0.###", inv), p.HitType.ToString(inv),
					"0", "0", "0", p.Fence ? "1" : "0", p.DoublePlay ? "1" : "0", p.Bunt ? "1" : "0", p.CatchType.ToString(inv), p.Ground.ToString(inv), ((int)p.Sns).ToString(inv)
				}));
				sb.Append('\n');
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
		}

		// ---- physics (Ball.cs) ----

		private sealed class Flight
		{
			public double Vh, Vy, Dir;
			public double[] T = new double[6];    // phase durations: flight, 4 bounces, roll
			public double[] Vhs = new double[6];  // horizontal speed during each phase
			public double LandTime, LandDist, StopDist;

			public double DistanceAt(double t)
			{
				double d = 0;
				for (int i = 0; i < 5; i++)
				{
					if (t <= T[i])
					{
						return d + Vhs[i] * t;
					}
					d += Vhs[i] * T[i];
					t -= T[i];
				}
				double roll = Math.Min(t, T[5]);
				return d + Vhs[5] * roll - 0.5 * 10.14 * roll * roll;
			}

			// First time the ball is this far from home plate (∞ if it stops short).
			public double TimeTo(double dist)
			{
				double total = 0;
				double d = 0;
				for (int i = 0; i < 5; i++)
				{
					double next = d + Vhs[i] * T[i];
					if (dist <= next)
					{
						return total + (dist - d) / Math.Max(1e-6, Vhs[i]);
					}
					d = next;
					total += T[i];
				}
				double rest = dist - d;
				double v = Vhs[5];
				double disc = v * v - 2 * 10.14 * rest;
				if (disc < 0)
				{
					return double.PositiveInfinity;
				}
				return total + (v - Math.Sqrt(disc)) / 10.14;
			}

			public double HeightAt(double t) => t <= T[0] ? 1 + Vy * t - 0.5 * G * t * t : 0.1;

			public double TotalTime => T.Sum();
		}

		private static Flight Fly(double speed, double v, double h)
		{
			Flight f = new Flight { Dir = h };
			double rv = v * Math.PI / 180;
			f.Vh = speed * Math.Cos(rv);
			f.Vy = speed * Math.Sin(rv);
			f.T[0] = (f.Vy + Math.Sqrt(f.Vy * f.Vy + 2 * G * 0.9)) / G;
			f.Vhs[0] = f.Vh;
			double vy = (G * f.T[0] - f.Vy) * 0.2;
			double vh = f.Vh * 0.6;
			for (int i = 1; i < 5; i++)
			{
				f.T[i] = 2 * vy / G;
				f.Vhs[i] = vh;
				vy *= 0.5;
			}
			f.Vhs[5] = vh;
			f.T[5] = vh / 10.14;
			f.LandTime = f.T[0];
			f.LandDist = f.Vh * f.T[0];
			f.StopDist = f.DistanceAt(f.TotalTime);
			return f;
		}

		// Speed that lands a ball launched at v on the ground this far out.
		private static double SpeedForCarry(double v, double dist)
		{
			double lo = 5, hi = 90;
			for (int i = 0; i < 60; i++)
			{
				double mid = (lo + hi) / 2;
				if (Fly(mid, v, 45).LandDist < dist) lo = mid; else hi = mid;
			}
			return (lo + hi) / 2;
		}

		// Speed that makes a ball launched at v come to rest this far out (grounders lose 40% at the
		// first bounce and then roll against 10.14 m/s², so they need far more than their distance).
		private static double SpeedForStop(double v, double dist)
		{
			double lo = 5, hi = 120;
			for (int i = 0; i < 60; i++)
			{
				double mid = (lo + hi) / 2;
				if (Fly(mid, v, 45).StopDist < dist) lo = mid; else hi = mid;
			}
			return (lo + hi) / 2;
		}

		// How far an infielder can get to a ball t seconds after contact: a reaction (the pitcher is
		// still finishing his delivery), then about 7 m/s, plus a 1.5 m dive.
		private static double Reach(FieldPos f, double t) => Math.Max(0, (t - (f == FieldPos.P ? 0.45 : 0.3)) * 7.0) + 1.5;

		// The smallest margin by which a grounder stays out of every infielder's reach along its
		// path (negative: someone could field it). Low balls only; the client's ball physics.
		private static double Clearance(double speed, double v, double h)
		{
			Flight fl = Fly(speed, v, h);
			double r = h * Math.PI / 180;
			double worst = double.MaxValue;
			foreach (FieldPos f in new[] { FieldPos.P, FieldPos.B1, FieldPos.B2, FieldPos.SS, FieldPos.B3 })
			{
				(double fx, double fz) = Home[(int)f];
				for (double t = 0.05; t < fl.TotalTime; t += 0.05)
				{
					if (fl.HeightAt(t) > 2.2)
					{
						continue;
					}
					double dist = fl.DistanceAt(t);
					if (dist > Math.Sqrt(fx * fx + fz * fz) + 12)
					{
						break;
					}
					double bx = dist * Math.Cos(r), bz = dist * Math.Sin(r);
					worst = Math.Min(worst, Math.Sqrt((bx - fx) * (bx - fx) + (bz - fz) * (bz - fz)) - Reach(f, t));
				}
			}
			return worst;
		}

		// Three grounder singles for a zone (spray angles lo..hi): the least hard-hit ball that
		// gets through at each candidate angle, spread out across the zone's holes.
		private static List<(double h, double speed, double v)> HoleGrounders(double lo, double hi)
		{
			List<(double h, double speed, double v, double clear)> found = new List<(double, double, double, double)>();
			for (double h = lo; h <= hi; h += 0.5)
			{
				(double h, double speed, double v, double clear) best = (h, 0, 0, double.MinValue);
				foreach (double v in new[] { -2.0, 1.0, 3.0 })
				{
					for (double speed = 36; speed <= 62; speed += 2)
					{
						double c = Clearance(speed, v, h);
						if (c >= 0.5 && (best.clear < 0.5 || speed < best.speed))
						{
							best = (h, speed, v, c);
						}
						else if (best.clear < 0.5 && c > best.clear)
						{
							best = (h, speed, v, c);
						}
					}
				}
				found.Add(best);
			}
			List<(double h, double speed, double v, double clear)> ok = found.Where(f => f.clear >= 0.5).ToList();
			if (ok.Count == 0)
			{
				ok = found.OrderByDescending(f => f.clear).Take(6).ToList();
			}
			// Spread: the clearest angle, then the clearest ones at least 4° from those already taken.
			List<(double h, double speed, double v)> picked = new List<(double, double, double)>();
			foreach ((double h, double speed, double v, double clear) c in ok.OrderByDescending(f => Math.Min(f.clear, 3.0)).ThenBy(f => f.speed))
			{
				if (picked.All(p => Math.Abs(p.h - c.h) >= 4))
				{
					picked.Add((c.h, c.speed, c.v));
				}
				if (picked.Count == 3)
				{
					break;
				}
			}
			while (picked.Count < 3)
			{
				picked.Add(picked[picked.Count - 1]);
			}
			return picked;
		}

		private static double Angle(FieldPos p) => Math.Atan2(Home[(int)p].z, Home[(int)p].x) * 180 / Math.PI;

		private static double Dist(FieldPos p) => Math.Sqrt(Home[(int)p].x * Home[(int)p].x + Home[(int)p].z * Home[(int)p].z);

		private static (double x, double z) Point(double h, double d) => (d * Math.Cos(h * Math.PI / 180), d * Math.Sin(h * Math.PI / 180));

		// How far along the ray at h a fielder at p gets to the ball (his closest point on it).
		private static double Projection(FieldPos p, double h)
		{
			(double x, double z) = Home[(int)p];
			double r = h * Math.PI / 180;
			return x * Math.Cos(r) + z * Math.Sin(r);
		}

		// The ball starts flying when PlayManager enters HIT, but fielders, the catch and the camera
		// count catch time from INPLAY, which follows HIT after 0.2 s (one frame later at 60 fps;
		// the port shortened the original 0.5 s). A flight time used as is made every catch late:
		// the ball sailed past the fielder and was then snapped into his glove.
		public const double HitToInPlay = 0.217;

		private BallPreset Add(PresetKind kind, double speed, double v, double h, FieldPos chase, FieldPos b1, FieldPos b2, bool hit, int fly,
			double flightTime, (double x, double z) at, int hitType, BatterRecord sns, int sub = 0, bool fence = false, bool dp = false, bool bunt = false, int catchType = 0)
		{
			BallPreset p = new BallPreset
			{
				Code = m_next++, Kind = kind, Speed = (float)speed, VAngle = (float)v, HAngle = (float)h, Chase = chase, Backup1 = b1, Backup2 = b2,
				Hit = hit, Fly = fly, CatchTime = (float)Math.Max(0.05, flightTime - HitToInPlay), CatchX = (float)at.x, CatchZ = (float)at.z, HitType = hitType,
				Fence = fence, DoublePlay = dp, Bunt = bunt, CatchType = catchType, Sns = sns, Sub = sub
			};
			All.Add(p);
			return p;
		}

		private static FieldPos Backup(FieldPos p) => p switch
		{
			FieldPos.B1 => FieldPos.RF, FieldPos.B2 => FieldPos.CF, FieldPos.SS => FieldPos.CF, FieldPos.B3 => FieldPos.LF,
			FieldPos.P => FieldPos.CF, FieldPos.C => FieldPos.P, FieldPos.LF => FieldPos.CF, FieldPos.CF => FieldPos.LF, _ => FieldPos.CF
		};

		// The second backup chases the ball as well, so it must never be a man with a job of his
		// own: the shortstop (or second baseman) going out as the relay on a ball to the outfield,
		// or an infielder on a ground ball another infielder is fielding (the second baseman used
		// to charge the mound on comebackers). Only the gaps get a second outfielder.
		private static FieldPos Backup2(FieldPos p) => p switch
		{
			FieldPos.CF => FieldPos.RF, FieldPos.B2 => FieldPos.RF, FieldPos.SS => FieldPos.LF, _ => FieldPos.None
		};

		// Outfielder covering a direction.
		private static FieldPos Outfielder(double h) => h >= 60 ? FieldPos.LF : h >= 30 ? FieldPos.CF : FieldPos.RF;

		private static BatterRecord Shift(BatterRecord baseRecord, FieldPos p) => (BatterRecord)((int)baseRecord + (int)p);

		private void Generate()
		{
			FieldPos[] infield = { FieldPos.P, FieldPos.B1, FieldPos.B2, FieldPos.SS, FieldPos.B3 };
			FieldPos[] outfield = { FieldPos.LF, FieldPos.CF, FieldPos.RF };
			double[] offsets = { -5, -2, 2, 5 };

			// Grounders the infielder turns into an out (code 1000+) and double plays (1100+).
			foreach (bool dp in new[] { false, true })
			{
				m_next = dp ? (short)1100 : (short)1000;
				foreach (FieldPos f in infield)
				{
					foreach (double off in offsets)
					{
						foreach (double extra in new[] { 4.0, 9.0 })
						{
							double h = Angle(f) + (f == FieldPos.P ? off * 0.6 : off);
							double v = -6;
							double d = Math.Max(4, Projection(f, h) - (f == FieldPos.P ? 0 : 1.5));
							// Hard enough to still be rolling when it reaches the fielder (a softer and a
							// sharper one); the old fixed 24–30 m/s died well in front of him.
							double speed = SpeedForStop(v, d + extra);
							Flight fl = Fly(speed, v, h);
							double t = fl.TimeTo(d);
							if (double.IsInfinity(t)) { d = fl.StopDist; t = fl.TotalTime; }
							Add(dp ? PresetKind.DoublePlay : PresetKind.GroundOut, speed, v, h, f, Backup(f), Backup2(f), false, 0, t, Point(h, d), 0, BatterRecord.None, dp: dp);
						}
					}
				}
			}

			// Pop-ups and fly balls caught (1200+), and deep flies that score a runner from third (1400+).
			m_next = 1200;
			foreach (FieldPos f in new[] { FieldPos.C, FieldPos.P, FieldPos.B1, FieldPos.B2, FieldPos.SS, FieldPos.B3 })
			{
				foreach (double off in new[] { -6.0, 0.0, 6.0 })
				{
					// Catcher pop-ups go up behind the plate (foul ground).
					double h = f == FieldPos.C ? 225 + off * 3 : Angle(f) + off;
					double carry = f == FieldPos.C ? 6 : Math.Max(8, Dist(f) + (f == FieldPos.P ? 0 : 3));
					double v = 76;
					double speed = SpeedForCarry(v, carry);
					Flight fl = Fly(speed, v, h);
					double t = fl.LandTime - 0.08;
					Add(PresetKind.FlyOut, speed, v, h, f, Backup(f), Backup2(f), false, 1, t, Point(h, fl.Vh * t), 0, Shift(BatterRecord.FlyP, f));
				}
			}
			foreach (bool sac in new[] { false, true })
			{
				if (sac) m_next = 1400;
				foreach (FieldPos f in outfield)
				{
					foreach (double off in offsets)
					{
						double h = Angle(f) + off * 1.6;
						double carry = Projection(f, h) + (sac ? 9 : 0) + off;
						double v = sac ? 38 : 42;
						double speed = SpeedForCarry(v, carry);
						Flight fl = Fly(speed, v, h);
						double t = fl.LandTime - 0.06;
						Add(sac ? PresetKind.SacFly : PresetKind.FlyOut, speed, v, h, f, Backup(f), Backup2(f), false, 1, t, Point(h, fl.Vh * t), 0,
							Shift(sac ? BatterRecord.SacFlyP : BatterRecord.FlyP, f));
					}
				}
			}

			// Line drives caught (1500+).
			m_next = 1500;
			foreach (FieldPos f in infield.Concat(outfield))
			{
				foreach (double off in new[] { -3.0, 0.0, 3.0 })
				{
					bool deep = f >= FieldPos.LF;
					double h = Angle(f) + off;
					double d = Projection(f, h);
					double v = deep ? 14 : 9;
					double speed = deep ? SpeedForCarry(v, d + 1) : 36;
					Flight fl = Fly(speed, v, h);
					double t = Math.Min(fl.LandTime, d / fl.Vh);
					Add(PresetKind.LineDrive, speed, v, h, f, Backup(f), Backup2(f), false, 1, t, Point(h, fl.Vh * t), 0, Shift(BatterRecord.LineDriveP, f));
				}
			}

			// Infield hits (1600+): slow rollers deep in the hole.
			m_next = 1600;
			foreach (FieldPos f in new[] { FieldPos.B1, FieldPos.B2, FieldPos.SS, FieldPos.B3 })
			{
				BatterRecord sns = f switch { FieldPos.B1 => BatterRecord.InfieldHit1B, FieldPos.B2 => BatterRecord.InfieldHit2B, FieldPos.B3 => BatterRecord.InfieldHit3B, _ => BatterRecord.InfieldHitSS };
				foreach (double off in new[] { -8.0, -4.0, 4.0, 8.0 })
				{
					double h = Angle(f) + off;
					double speed = 19;
					double v = -4;
					Flight fl = Fly(speed, v, h);
					double d = Math.Min(fl.StopDist, Projection(f, h) + 2);
					double t = Math.Min(fl.TimeTo(d), fl.TotalTime);
					Add(PresetKind.InfieldHit, speed, v, h, f, Backup(f), Backup2(f), true, 0, t, Point(h, d), 1, sns, (int)sns);
				}
			}

			// Singles to the outfield (1700+): grounders through the infield and liners that drop in.
			// A grounder single has to go where no infielder can get to it, or the broadcast shows an
			// infielder letting a ball he could field roll past while he covers a base: each zone's
			// grounders are searched for direction and pace that stay out of everyone's reach.
			m_next = 1700;
			(BatterRecord sns, double lo, double hi, double[] liners)[] singles =
			{
				(BatterRecord.SingleLF, 56, 89, new[] { 66.0, 70.0, 84.0 }), (BatterRecord.SingleCF, 33, 57, new[] { 41.0, 45.0, 49.0 }), (BatterRecord.SingleRF, 1, 34, new[] { 6.0, 20.0, 24.0 })
			};
			foreach ((BatterRecord sns, double lo, double hi, double[] liners) in singles)
			{
				List<(double h, double speed, double v)> grounders = HoleGrounders(lo, hi);
				for (int i = 0; i < 3; i++)
				{
					foreach (bool liner in new[] { false, true })
					{
						double h = liner ? liners[i] : grounders[i].h;
						FieldPos of = Outfielder(h);
						// The outfielder charges in and meets the ball about 12 m in front of where he stood.
						double meet = Projection(of, h) - 12;
						double v = liner ? 16 : grounders[i].v;
						double speed = liner ? SpeedForCarry(16, Projection(of, h) - 14) : grounders[i].speed;
						Flight fl = Fly(speed, v, h);
						double d = liner ? Math.Min(fl.StopDist, Math.Max(fl.LandDist + 2, meet)) : Math.Min(fl.StopDist, meet);
						double t = Math.Min(fl.TimeTo(d), fl.TotalTime);
						Add(PresetKind.Single, speed, v, h, of, Backup(of), Backup2(of), true, 0, t, Point(h, d), 1, sns, (int)sns);
					}
				}
			}

			// Doubles (1800+), triples (1900+) and home runs (2000+) depend on the park's wall, so each
			// park (eGround + 1) gets its own set under the same codes, as the original table had.
			for (int g = 0; g < m_fences.Length; g++)
			{
				int first = All.Count;
				GenerateLongHits(m_fences[g]);
				for (int i = first; i < All.Count; i++)
				{
					All[i].Ground = g + 1;
				}
			}
			// Errors (2100+): routine grounders the infielder boots.
			m_next = 2100;
			foreach (FieldPos f in infield.Where(f => f != FieldPos.P))
			{
				foreach (double off in new[] { -3.0, 3.0 })
				{
					double h = Angle(f) + off;
					double d = Projection(f, h) - 1.5;
					double speed = SpeedForStop(-6, d + 6);
					Flight fl = Fly(speed, -6, h);
					Add(PresetKind.Error, speed, -6, h, f, Backup(f), Backup2(f), false, 0, fl.TimeTo(d), Point(h, d), 0, BatterRecord.None, catchType: 1);
				}
			}

			// Bunts (2200+): dead along the lines or back to the pitcher.
			m_next = 2200;
			(FieldPos f, double h)[] bunts = { (FieldPos.B3, 76), (FieldPos.P, 45), (FieldPos.B1, 14), (FieldPos.C, 60) };
			foreach ((FieldPos f, double h) in bunts)
			{
				double speed = f == FieldPos.C ? 5 : 9;
				Flight fl = Fly(speed, -20, h);
				double d = Math.Min(fl.StopDist, f == FieldPos.C ? 2 : 9);
				Add(PresetKind.Bunt, speed, -20, h, f, FieldPos.P, FieldPos.C, false, 0, Math.Min(fl.TimeTo(d), fl.TotalTime), Point(h, d), 0, BatterRecord.Bunt, bunt: true);
			}
		}

		// Gap and wall hits stay in the park: they land a few metres short of the wall and roll to it
		// (a ball that cleared the wall would bounce back off thin air, since only homers fly out),
		// and the fielder picks them up in front of it. Homers clear the wall by at least 3 m.
		private void GenerateLongHits(FenceProfile fence)
		{
			(BatterRecord sns, double h, double carry, bool fence)[] doubles =
			{
				(BatterRecord.DoubleBackLF, 73, 96, true), (BatterRecord.DoubleLeftLF, 86, 92, false), (BatterRecord.DoubleBetweenLC, 58, 88, false),
				(BatterRecord.DoubleBackCF, 45, 104, true), (BatterRecord.DoubleBetweenCR, 32, 88, false), (BatterRecord.DoubleBackRF, 17, 96, true), (BatterRecord.DoubleRightRF, 4, 92, false)
			};
			m_next = 1800;
			foreach ((BatterRecord sns, double h0, double carry0, bool wall) in doubles)
			{
				// Down the line (DoubleLeftLF / DoubleRightRF) the liner lands past the corner outfielder's
				// depth (~83 m) and runs into the corner. It used to carry 82 m, level with him and 20 m to
				// his side, so he ran back after a ball that had dropped in front of him (user, 2026-10-20:
				// "좌익수 앞 타구를 … 대각선방향 뒤로 가면서", "좌익수 앞 안타가 2루타가 되는 것 같은데").
				bool line = sns is BatterRecord.DoubleLeftLF or BatterRecord.DoubleRightRF;
				foreach (double off in new[] { -2.0, 2.0 })
				{
					double h = h0 + off;
					(double r, double _) = fence.At(h);
					double carry = Math.Min(carry0, r - (wall ? 4 : line ? 6 : 12));
					FieldPos of = Outfielder(h);
					double v = 24;
					double speed = SpeedForCarry(v, carry);
					Flight fl = Fly(speed, v, h);
					double d = Math.Min(Math.Min(fl.StopDist, carry + 9), r - 2.5);
					double t = Math.Min(fl.TimeTo(d), fl.TotalTime);
					Add(PresetKind.Double, speed, v, h, of, Backup(of), Backup2(of), true, 0, t, Point(h, d), 2, sns, (int)sns, fl.StopDist >= r - 1);
				}
			}
			(BatterRecord sns, double h, double carry)[] triples =
			{
				(BatterRecord.TripleLeftLF, 87, 92), (BatterRecord.TripleBetweenLC, 57, 100), (BatterRecord.TripleBackCF, 45, 110),
				(BatterRecord.TripleBetweenCR, 33, 100), (BatterRecord.TripleBackRF, 18, 102), (BatterRecord.TripleRightRF, 3, 92)
			};
			m_next = 1900;
			foreach ((BatterRecord sns, double h0, double carry0) in triples)
			{
				foreach (double off in new[] { -1.5, 1.5 })
				{
					double h = h0 + off;
					(double r, double _) = fence.At(h);
					double carry = Math.Min(carry0, r - 4);
					FieldPos of = Outfielder(h);
					double v = 22;
					double speed = SpeedForCarry(v, carry);
					Flight fl = Fly(speed, v, h);
					double d = Math.Min(Math.Min(fl.StopDist, carry + 14), r - 2.5);
					double t = Math.Min(fl.TimeTo(d), fl.TotalTime) + 0.6;
					Add(PresetKind.Triple, speed, v, h, of, Backup(of), Backup2(of), true, 0, t, Point(h, d), 3, sns, (int)sns, true);
				}
			}
			m_next = 2000;
			// in play — the fielder goes back to the wall, it just clears it, and the batter rounds the
			// bases — while the no-doubter (hit type 5) glows off the bat and is a homer at once.
			(BatterRecord sns, double[] dirs)[] homers = { (BatterRecord.HrLF, new[] { 68.0, 74.0, 80.0 }), (BatterRecord.HrCF, new[] { 40.0, 45.0, 50.0 }), (BatterRecord.HrRF, new[] { 10.0, 16.0, 22.0 }) };
			(double v, double over, int type)[] kinds = { (26, 1.2, 4), (31, 1.8, 4), (27, 9, 5), (32, 13, 5), (37, 17, 5) };
			foreach ((BatterRecord sns, double[] dirs) in homers)
			{
				foreach (double h in dirs)
				{
					foreach ((double v, double over, int type) in kinds)
					{
						(double r, double wallH) = fence.At(h);
						// Height at the wall = 1 + R tan v − g R² / (2 s² cos² v): solve for the speed
						// that clears it by the given margin.
						double clear = wallH + over;
						double rv = v * Math.PI / 180;
						double speed = Math.Sqrt(G * r * r / (2 * Math.Cos(rv) * Math.Cos(rv) * (1 + r * Math.Tan(rv) - clear)));
						FieldPos of = Outfielder(h);
						Flight fl = Fly(speed, v, h);
						// The fielder goes back to the wall for the scraper; he only turns to watch the other.
						double d = type == 4 ? r - 1.5 : Math.Min(fl.LandDist, r - 6);
						Add(PresetKind.HomeRun, speed, v, h, of, Backup(of), Backup2(of), true, 0, type == 4 ? fl.LandTime : fl.LandTime * 0.6, Point(h, d), type, sns, (int)sns);
					}
				}
			}
		}
	}
}
