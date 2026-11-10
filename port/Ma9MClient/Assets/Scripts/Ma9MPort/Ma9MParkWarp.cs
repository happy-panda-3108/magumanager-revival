using System.Collections.Generic;
using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "외야 장타코스인데, 공이랑 수비수가 펜스 너머까지 걸어감. 그래서 파묻혀서
// 안 보임"): the fielding engine plays every ball in its reference park (98 m down the lines, 113 m in the alleys,
// 122 m to centre; sim Fielding.Geometry.FenceAt), and the parks here stand up to 4.4 m shorter (사직 117.7 m to
// centre): a ball off the reference wall rolled on behind this park's fence, its fielder after it. A script is
// stretched or squeezed along each spray line from 60 m out so the reference wall lands on this park's fence; a
// rolling ball and the fielders stay on the field side of it. The outcome is the engine's either way.
public static class Ma9MParkWarp
{
	private const float From = 60f;

	// How far in from the fence a man (his body) and a ball on the grass stay.
	private const float ManGap = 0.7f, BallGap = 0.15f;

	private static FenceData s_for;

	private static float[] s_angle, s_radius;

	public static Ma9MPlayScript Apply(Ma9MPlayScript s, FenceData fd)
	{
		if (s == null || fd == null || fd.fences == null || fd.fences.Length < 2) return s;
		if (s_for != fd) Profile(fd);
		if (s_angle == null || s_angle.Length < 2) return s;
		Ma9MPlayScript w = new Ma9MPlayScript { Ev = s.Ev, La = s.La, H = s.H, End = s.End };
		foreach (Ma9MPiece p in s.Pieces)
		{
			Ma9MPiece q = p;
			// Balls in the air (flights, throws) go where they go; a ball on the grass and the men stop short of the wall.
			float gap = p.Actor == Ma9MPiece.Ball ? (p.Kind == Ma9MSeg.BallRoll || p.Kind == Ma9MSeg.BallLoose || p.Kind == Ma9MSeg.BallHeld ? BallGap : -1f) : ManGap;
			q.P0 = Warp(p.P0, gap);
			q.P1 = Warp(p.P1, gap);
			w.Pieces.Add(q);
		}
		return w;
	}

	// A runner sliding into a force (the fielder on the bag, not tagging him) goes in at the far edge of the bag, at
	// least 1.4 m from the fielder: the engine had the fielder 0.8 m off the runner's line, and the big-headed figures
	// lay one over the other (user, 2026-10-31: the 2루수 of a 병살 sat on the bag inside the runner). Plays stored before
	// the server put the fielder past the bag (2026-11-02) still need it.
	public static Ma9MPlayScript MakeRoom(Ma9MPlayScript s)
	{
		const float Room = 1.4f, MaxShift = 0.7f;
		if (s == null) return s;
		Ma9MScriptTimeline tl = null;
		Ma9MPlayScript w = null;
		for (int i = 0; i < s.Pieces.Count; i++)
		{
			Ma9MPiece sl = s.Pieces[i];
			if (sl.Kind != Ma9MSeg.Slide || sl.Actor < Ma9MPiece.RunnerBase || sl.Actor >= Ma9MPiece.RunnerBase + 4) continue;
			int runner = sl.Actor - Ma9MPiece.RunnerBase;
			bool tagged = false;
			foreach (Ma9MPiece q in s.Pieces) if (q.Kind == Ma9MSeg.Tag && q.Style == 1 && q.Arg == runner) tagged = true;
			if (tagged) continue;
			if (tl == null) tl = new Ma9MScriptTimeline(s);
			Vector2 end = sl.P1, path = (sl.P1 - sl.P0).sqrMagnitude > 1e-4f ? (sl.P1 - sl.P0).normalized : Vector2.up;
			Vector2 near = Vector2.zero;
			float best = Room;
			for (int a = 0; a < 9; a++)
			{
				Vector2 f = tl.PosAt(a, sl.T1, new Vector2(999f, 999f), out _, out _);
				float d = Vector2.Distance(f, end);
				if (d < best) { best = d; near = f; }
			}
			if (best >= Room) continue;
			// A fielder past the bag, on its far edge (the server puts him there since 2026-11-02: user, "수비수는 루 가장
			// 바깥쪽을 밟고 있으면 겹칠 일이 없을 것 같음"), leaves the runner his line into the bag.
			if (Vector2.Dot(near - end, path) > 0.2f) continue;
			Vector2 side = near - end;
			side -= Vector2.Dot(side, path) * path;
			if (side.sqrMagnitude < 1e-4f) side = new Vector2(path.y, -path.x);
			Vector2 shift = -side.normalized * Mathf.Min(MaxShift, Room - best);
			if (w == null)
			{
				w = new Ma9MPlayScript { Ev = s.Ev, La = s.La, H = s.H, End = s.End };
				w.Pieces.AddRange(s.Pieces);
			}
			for (int k = 0; k < w.Pieces.Count; k++)
			{
				Ma9MPiece q = w.Pieces[k];
				if (q.Actor != sl.Actor || q.T1 < sl.T1 - 1e-3f) continue;
				if ((q.P0 - end).sqrMagnitude < 0.01f && k != i) q.P0 += shift;
				if ((q.P1 - end).sqrMagnitude < 0.01f) q.P1 += shift;
				w.Pieces[k] = q;
			}
		}
		return w ?? s;
	}

	private static Vector2 Warp(Vector2 p, float gap)
	{
		float r = p.magnitude;
		if (r <= From) return p;
		float h = Mathf.Atan2(p.y, p.x) * Mathf.Rad2Deg;
		float fence = FenceAt(h);
		float r2 = From + (r - From) * (fence - From) / (RefFence(h) - From);
		if (gap >= 0 && r2 > fence - gap) r2 = Mathf.Max(From, fence - gap);
		return p * (r2 / r);
	}

	// sim Fielding.Geometry.FenceAt.
	private static float RefFence(float h)
	{
		float d = Mathf.Abs(Mathf.Clamp(h, 0, 90) - 45);
		if (d >= 22.5f) return 113f + (98f - 113f) * (d - 22.5f) / 22.5f;
		float g = d / 22.5f;
		return 122f + (113f - 122f) * g * g;
	}

	// This park's fence along a spray line (degrees from the first-base line), between its posts.
	private static float FenceAt(float h)
	{
		int n = s_angle.Length;
		if (h <= s_angle[0]) return s_radius[0];
		if (h >= s_angle[n - 1]) return s_radius[n - 1];
		int lo = 0, hi = n - 1;
		while (hi - lo > 1)
		{
			int mid = (lo + hi) / 2;
			if (s_angle[mid] <= h) lo = mid; else hi = mid;
		}
		float u = (h - s_angle[lo]) / Mathf.Max(1e-4f, s_angle[hi] - s_angle[lo]);
		return Mathf.Lerp(s_radius[lo], s_radius[hi], u);
	}

	private static void Profile(FenceData fd)
	{
		s_for = fd;
		List<Vector2> pts = new List<Vector2>(fd.fences.Length);
		foreach (Vector3 v in fd.fences)
		{
			float r = new Vector2(v.x, v.z).magnitude;
			if (r > From) pts.Add(new Vector2(Mathf.Atan2(v.z, v.x) * Mathf.Rad2Deg, r));
		}
		pts.Sort((a, b) => a.x.CompareTo(b.x));
		s_angle = new float[pts.Count];
		s_radius = new float[pts.Count];
		for (int i = 0; i < pts.Count; i++)
		{
			s_angle[i] = pts[i].x;
			s_radius[i] = pts[i].y;
		}
		Debug.Log($"[Ma9MParkWarp] {fd.name}: fence {FenceAt(0):F1} / {FenceAt(22.5f):F1} / {FenceAt(45):F1} / {FenceAt(67.5f):F1} / {FenceAt(90):F1} m");
	}
}
