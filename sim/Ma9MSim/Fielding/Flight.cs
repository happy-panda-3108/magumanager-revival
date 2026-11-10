using System;

namespace Ma9MSim.Fielding
{
	// Batted-ball flight in still air: drag plus Magnus lift from backspin, integrated once at
	// start-up into tables over exit velocity × launch angle. Distances in metres from home
	// plate, times in seconds, the ball leaves the bat 1 m above the ground. The constants were
	// fitted to Statcast-like carries and hang times (scratchpad field/tune.py, 2026-10-30):
	// 100 mph at 28° carries ~376 ft in 4.7 s, 95 mph at 12° ~232 ft in 2.2 s.
	public static class Flight
	{
		public const double Mass = 0.145, Radius = 0.0366, Rho = 1.20, Gravity = 9.81, StartHeight = 1.0;
		public const double Cd0 = 0.36, CdSpin = 0.15, LiftK = 1.2, SpinBase = 200, SpinPerDeg = 55, SpinMax = 3000, TopSpin = -800;

		// Table grid: exit velocity 10-58 m/s (22-130 mph) by 0.5, launch angle -20..85° by 1.
		public const double EvMin = 10, EvStep = 0.5, LaMin = -20, LaStep = 1;
		public const int EvN = 97, LaN = 106;
		// Height and time along the path every 2 m of horizontal distance, to 160 m.
		public const double PathStep = 2.0;
		public const int PathN = 81;

		private static readonly float[] s_carry = new float[EvN * LaN];
		private static readonly float[] s_hang = new float[EvN * LaN];
		private static readonly float[] s_apex = new float[EvN * LaN];
		private static readonly float[] s_landSpeed = new float[EvN * LaN];   // horizontal speed at landing
		private static readonly float[] s_height = new float[EvN * LaN * PathN];
		private static readonly float[] s_time = new float[EvN * LaN * PathN];

		static Flight()
		{
			for (int i = 0; i < EvN; i++)
			{
				for (int j = 0; j < LaN; j++)
				{
					Integrate(EvMin + i * EvStep, LaMin + j * LaStep, i * LaN + j);
				}
			}
		}

		public static double SpinRpm(double la) => la <= 0 ? TopSpin : Math.Min(SpinMax, SpinBase + SpinPerDeg * la);

		private static void Integrate(double ev, double laDeg, int cell)
		{
			double la = laDeg * Math.PI / 180;
			double x = 0, y = StartHeight, vx = ev * Math.Cos(la), vy = ev * Math.Sin(la), t = 0, apex = y;
			double rpm = SpinRpm(laDeg);
			double w = Math.Abs(rpm) * 2 * Math.PI / 60;
			double k0 = 0.5 * Rho * Math.PI * Radius * Radius / Mass;
			const double dt = 0.004;
			int next = 0;
			float[] h = s_height, tt = s_time;
			int baseIdx = cell * PathN;
			for (int k = 0; k < PathN; k++)
			{
				h[baseIdx + k] = float.NaN;
				tt[baseIdx + k] = float.NaN;
			}
			(double ax, double ay) Acc(double ux, double uy)
			{
				double sp = Math.Sqrt(ux * ux + uy * uy);
				double s = Radius * w / Math.Max(sp, 1e-3);
				double cd = Cd0 + CdSpin * s;
				double cl = LiftK * s / (1 + 2.6 * s) * (rpm >= 0 ? 1 : -1);
				double kk = k0 * sp;
				return (-kk * cd * ux - kk * cl * uy, -kk * cd * uy + kk * cl * ux - Gravity);
			}
			while (t < 12)
			{
				while (next < PathN && next * PathStep <= x)
				{
					h[baseIdx + next] = (float)y;
					tt[baseIdx + next] = (float)t;
					next++;
				}
				(double ax1, double ay1) = Acc(vx, vy);
				(double ax2, double ay2) = Acc(vx + ax1 * dt / 2, vy + ay1 * dt / 2);
				(double ax3, double ay3) = Acc(vx + ax2 * dt / 2, vy + ay2 * dt / 2);
				(double ax4, double ay4) = Acc(vx + ax3 * dt, vy + ay3 * dt);
				double nx = x + vx * dt + (ax1 + ax2 + ax3) * dt * dt / 6;
				double ny = y + vy * dt + (ay1 + ay2 + ay3) * dt * dt / 6;
				double nvx = vx + (ax1 + 2 * ax2 + 2 * ax3 + ax4) * dt / 6;
				double nvy = vy + (ay1 + 2 * ay2 + 2 * ay3 + ay4) * dt / 6;
				if (ny < 0)
				{
					double f = y / (y - ny);
					x += (nx - x) * f;
					t += dt * f;
					vx += (nvx - vx) * f;
					break;
				}
				x = nx; y = ny; vx = nvx; vy = nvy; t += dt;
				if (y > apex) apex = y;
			}
			s_carry[cell] = (float)x;
			s_hang[cell] = (float)t;
			s_apex[cell] = (float)apex;
			s_landSpeed[cell] = (float)Math.Max(0, vx);
		}

		private static void Cell(double ev, double la, out int i0, out int j0, out double fi, out double fj)
		{
			double a = Math.Clamp((ev - EvMin) / EvStep, 0, EvN - 1.0001);
			double b = Math.Clamp((la - LaMin) / LaStep, 0, LaN - 1.0001);
			i0 = (int)a; j0 = (int)b;
			fi = a - i0; fj = b - j0;
		}

		private static double Lerp2(float[] tab, double ev, double la)
		{
			Cell(ev, la, out int i, out int j, out double fi, out double fj);
			int c = i * LaN + j;
			double v00 = tab[c], v01 = tab[c + 1], v10 = tab[c + LaN], v11 = tab[c + LaN + 1];
			return (v00 * (1 - fj) + v01 * fj) * (1 - fi) + (v10 * (1 - fj) + v11 * fj) * fi;
		}

		// Horizontal distance where the ball first touches the ground.
		public static double Carry(double ev, double la) => Lerp2(s_carry, ev, la);

		// Seconds in the air.
		public static double Hang(double ev, double la) => Lerp2(s_hang, ev, la);

		public static double Apex(double ev, double la) => Lerp2(s_apex, ev, la);

		// Horizontal speed when it lands (what is left for the bounce and roll).
		public static double LandSpeed(double ev, double la) => Lerp2(s_landSpeed, ev, la);

		// Height and time when the ball passes horizontal distance x (NaN beyond the carry).
		public static (double height, double time) At(double ev, double la, double x)
		{
			Cell(ev, la, out int i, out int j, out double fi, out double fj);
			double kx = x / PathStep;
			int k = (int)kx;
			if (k < 0 || k >= PathN - 1)
			{
				return (double.NaN, double.NaN);
			}
			double fk = kx - k;
			double hs = 0, ts = 0, wsum = 0;
			for (int a = 0; a < 2; a++)
			{
				for (int b = 0; b < 2; b++)
				{
					double wgt = (a == 0 ? 1 - fi : fi) * (b == 0 ? 1 - fj : fj);
					if (wgt <= 0) continue;
					int c = ((i + a) * LaN + j + b) * PathN + k;
					float h0 = s_height[c], h1 = s_height[c + 1], t0 = s_time[c], t1 = s_time[c + 1];
					if (float.IsNaN(h0) || float.IsNaN(h1))
					{
						continue;
					}
					hs += wgt * (h0 + (h1 - h0) * fk);
					ts += wgt * (t0 + (t1 - t0) * fk);
					wsum += wgt;
				}
			}
			return wsum < 0.5 ? (double.NaN, double.NaN) : (hs / wsum, ts / wsum);
		}

		// Horizontal distance of the ball t seconds after contact (in the air; the carry once landed).
		public static double DistAtTime(double ev, double la, double t)
		{
			double hang = Hang(ev, la), carry = Carry(ev, la);
			if (t >= hang) return carry;
			if (t <= 0) return 0;
			double lo = 0, hi = carry;
			for (int i = 0; i < 28; i++)
			{
				double mid = (lo + hi) / 2;
				(double _, double tm) = At(ev, la, mid);
				if (double.IsNaN(tm) || tm > t) hi = mid; else lo = mid;
			}
			return (lo + hi) / 2;
		}

		// Ground balls: after the first hop a grounder keeps HopKeep of its horizontal speed and then
		// slows by GroundDecel m/s² (bounces and grass). A 90 mph grounder reaches 40 m in ~1.4 s.
		public const double HopKeep = 0.72, GroundDecel = 4.6;

		// Time for a ground ball whose speed on the grass starts at v (m/s, from distance d0 and
		// time t0 where it first hit the ground) to reach distance d; +∞ if it stops short.
		public static double GroundTime(double v, double d0, double t0, double d)
		{
			if (d <= d0)
			{
				return t0 * d / Math.Max(d0, 0.01);
			}
			double s = d - d0;
			double disc = v * v - 2 * GroundDecel * s;
			if (disc <= 0)
			{
				return double.PositiveInfinity;
			}
			return t0 + (v - Math.Sqrt(disc)) / GroundDecel;
		}

		// Speed of that ground ball at distance d (0 once it has stopped).
		public static double GroundSpeed(double v, double d0, double d)
		{
			if (d <= d0) return v;
			double disc = v * v - 2 * GroundDecel * (d - d0);
			return disc <= 0 ? 0 : Math.Sqrt(disc);
		}

		// Where a ground ball stops rolling.
		public static double GroundStop(double v, double d0) => d0 + v * v / (2 * GroundDecel);
	}
}
