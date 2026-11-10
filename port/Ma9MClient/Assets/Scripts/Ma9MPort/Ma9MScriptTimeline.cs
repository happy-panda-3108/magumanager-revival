using System.Collections.Generic;
using UnityEngine;

// Ma9M port (engine session, 2026-10-30): where everything in a PlayScript is at a given time, as
// pure functions of the script (no scene objects), so the player and tests share one answer.
// World frame: x along the first-base line, z along the third-base line, y up, metres (the
// broadcast's own frame: FielderManager start positions, Ball.cs).
public sealed class Ma9MScriptTimeline
{
	public readonly Ma9MPlayScript Script;
	private readonly Ma9MFlight m_flight;
	private readonly List<Ma9MPiece> m_ball = new List<Ma9MPiece>();
	private readonly List<Ma9MPiece>[] m_actor = new List<Ma9MPiece>[24];
	// Per actor and piece: how much of a leg he slows down over at its end (0: he goes on, or something happens there).
	private readonly List<float>[] m_brake = new List<float>[24];

	public const float BallRadius = 0.037f, HandHeight = 1.15f, GloveHeightLow = 0.25f;

	// A dive at a ball in the air: the ball comes down into the glove he has out low just before he lands (user, 2026-10-31:
	// a liner went over the shortstop's head and he dove under it; the glove's height in Ma9MScriptPlayer.LayOut's frame,
	// 2026-11-01). No leap instead: an infielder's high jump catch is not in the game yet (user, 2026-10-31: "내야수
	// 점프캐치도 나중에 잠재력을 통해 해금"); the server no longer has one snag a liner above his raised glove, so only plays
	// stored before that dip from higher. 1.05 m: the glove at the top of his flight (Ma9MScriptPlayer.DiveAirAt; it was
	// 0.67, the glove on its way down to the grass, 2026-11-02).
	public const float DiveGlove = 1.05f;

	private float m_dipAt = -1f;

	// A fly ball caught standing or on the run (Ma9MScriptPlayer.ReachAir): its flight played a little slower (by m_lift
	// over the whole of it, 1-3%), so that at the catch it comes down through the glove's height, not below it (the engine
	// catches it 0.04 s before it would land, 0.7-1.1 m up, and the glove is up at 1.5), and the last frames do not lift it
	// up into the glove.
	private float m_liftAt = -1f, m_lift;

	public void LiftTo(float t1, float y)
	{
		foreach (Ma9MPiece f in m_ball)
		{
			if (f.Kind != Ma9MSeg.BallFly || f.T0 > 1e-3f || Mathf.Abs(f.T1 - t1) > 1e-3f || f.T1 < 0.5f) continue;
			float d = 0f;
			while (d < 0.15f && m_flight.At(t1 - d).y < y) d += 0.005f;
			m_liftAt = f.T1;
			m_lift = d;
		}
	}

	public Ma9MScriptTimeline(Ma9MPlayScript s)
	{
		Script = s;
		m_flight = new Ma9MFlight(s.Ev, s.La);
		for (int i = 0; i < m_actor.Length; i++) m_actor[i] = new List<Ma9MPiece>();
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Actor == Ma9MPiece.Ball) m_ball.Add(p);
			else if (p.Actor < m_actor.Length) m_actor[p.Actor].Add(p);
		}
		m_ball.Sort((a, b) => a.T0.CompareTo(b.T0));
		foreach (List<Ma9MPiece> l in m_actor) l.Sort((a, b) => a.T0.CompareTo(b.T0));
		for (int a = 0; a < m_actor.Length; a++) m_brake[a] = Brakes(m_actor[a], a >= 20);
		foreach (Ma9MPiece d in s.Pieces)
		{
			if (d.Kind != Ma9MSeg.Dive) continue;
			foreach (Ma9MPiece f in m_ball)
			{
				if (f.Kind == Ma9MSeg.BallFly && Mathf.Abs(f.T1 - d.T1) < 0.03f) m_dipAt = f.T1;
			}
		}
	}

	// The ball piece under way at t ends in a fielder's glove (the next piece is his holding it): who, and when.
	public bool GloveEnd(float t, out int fielder, out float at)
	{
		fielder = -1;
		at = 0f;
		int cur = -1;
		for (int i = 0; i < m_ball.Count; i++)
		{
			if (t < m_ball[i].T0) break;
			cur = i;
		}
		if (cur < 0 || m_ball[cur].Kind == Ma9MSeg.BallHeld || t > m_ball[cur].T1) return false;
		for (int i = cur + 1; i < m_ball.Count; i++)
		{
			if (m_ball[i].Kind == Ma9MSeg.BallHeld && Mathf.Abs(m_ball[i].T0 - m_ball[cur].T1) < 0.02f && m_ball[i].Arg < 9)
			{
				fielder = m_ball[i].Arg;
				at = m_ball[cur].T1;
				return true;
			}
		}
		return false;
	}

	// The style of fielder f's catch at time at, -1 none.
	public int CatchStyleAt(int f, float at)
	{
		List<Ma9MPiece> ps = PiecesOf(f);
		if (ps == null) return -1;
		foreach (Ma9MPiece p in ps)
		{
			if (p.Kind == Ma9MSeg.Catch && Mathf.Abs(p.T0 - at) < 0.03f) return p.Style;
		}
		return -1;
	}

	// The throw in the air at t: who threw it (the man holding the ball before it) and when it left and lands.
	public bool ThrowNow(float t, out int thrower, out float t0, out float t1)
	{
		thrower = -1;
		t0 = t1 = 0f;
		int cur = -1;
		for (int i = 0; i < m_ball.Count; i++)
		{
			if (t < m_ball[i].T0) break;
			cur = i;
		}
		if (cur < 1 || m_ball[cur].Kind != Ma9MSeg.BallThrow || t > m_ball[cur].T1) return false;
		Ma9MPiece held = m_ball[cur - 1];
		if (held.Kind != Ma9MSeg.BallHeld || held.Arg >= 9) return false;
		thrower = held.Arg;
		t0 = m_ball[cur].T0;
		t1 = m_ball[cur].T1;
		return true;
	}

	// Since when fielder f has had the ball he holds at t (the catch before a throw), -1 if he has not got it then.
	public float HeldSince(int f, float t)
	{
		foreach (Ma9MPiece p in m_ball)
		{
			if (p.Kind == Ma9MSeg.BallHeld && p.Arg == f && t >= p.T0 - 0.01f && t <= p.T1 + 0.01f) return p.T0;
		}
		return -1f;
	}

	// When actor a next goes anywhere after t (a run, a dive, back to his spot), float.MaxValue if he does not; not
	// the step or two in to the ball some catches have just after (an outfielder's pick-up).
	public float NextMove(int a, float t)
	{
		List<Ma9MPiece> ps = PiecesOf(a);
		float next = float.MaxValue;
		if (ps == null) return next;
		foreach (Ma9MPiece p in ps)
		{
			bool goes = p.Kind == Ma9MSeg.Move || p.Kind == Ma9MSeg.Dive || p.Kind == Ma9MSeg.Back || p.Kind == Ma9MSeg.Run;
			float len = (p.P1 - p.P0).magnitude;
			if (goes && p.T0 > t + 0.01f && len > 0.1f && (p.T0 > t + 0.3f || len > 1.5f)) next = Mathf.Min(next, p.T0);
		}
		return next;
	}

	// A throw to fielder a in flight at t, or about to be (0.4 s): where it comes from.
	public bool ThrowTo(int a, float t, out Vector2 from)
	{
		from = Vector2.zero;
		foreach (Ma9MPiece p in m_ball)
		{
			if (p.Kind == Ma9MSeg.BallThrow && p.Arg == a && t >= p.T0 - 0.4f && t <= p.T1)
			{
				from = p.P0;
				return true;
			}
		}
		return false;
	}

	public List<Ma9MPiece> PiecesOf(int actor) => actor >= 0 && actor < m_actor.Length ? m_actor[actor] : null;

	public static Vector3 World(Vector2 p, float y = 0) => new Vector3(p.x, y, p.y);

	// The rolling-ball law the server uses (Choreo.RollDist): even deceleration, stopping exactly at
	// the end point when it would stop short of it.
	public static float RollDist(float D, float T, float decel, float u)
	{
		if (T <= 1e-3f) return D;
		float v0 = D / T + decel * T / 2;
		if (v0 - decel * T >= 0) return Mathf.Min(D, v0 * u - 0.5f * decel * u * u);
		float vs = Mathf.Sqrt(2 * decel * D);
		float ts = vs / decel;
		return u >= ts ? D : vs * u - 0.5f * decel * u * u;
	}

	// The ball at time t, and who holds it (fielder deck position, -1 when loose or in flight).
	public Vector3 BallAt(float t, out int holder, out Ma9MSeg kind)
	{
		holder = -1;
		kind = Ma9MSeg.BallFly;
		Ma9MPiece cur = default;
		bool found = false;
		for (int i = 0; i < m_ball.Count; i++)
		{
			if (t < m_ball[i].T0) break;
			cur = m_ball[i];
			found = true;
		}
		if (!found)
		{
			return new Vector3(0, 1.0f, 0);
		}
		kind = cur.Kind;
		float dur = Mathf.Max(1e-3f, cur.T1 - cur.T0);
		float u = Mathf.Clamp01((t - cur.T0) / dur);
		switch (cur.Kind)
		{
			case Ma9MSeg.BallFly:
			{
				// The flight, scaled along the spray line so it ends where the script says.
				float tt = Mathf.Min(t, cur.T1);
				float ft = tt, fend = cur.T1;
				if (m_liftAt > 0 && Mathf.Abs(cur.T1 - m_liftAt) < 1e-3f)
				{
					fend = cur.T1 - m_lift;
					ft = tt * fend / cur.T1;
				}
				Vector2 at = m_flight.At(ft);
				Vector2 end = m_flight.At(fend);
				float k = end.x > 0.5f ? cur.P1.magnitude / end.x : 1f;
				Vector2 dir = cur.P1.sqrMagnitude > 1e-4f ? cur.P1.normalized : new Vector2(Mathf.Cos(Script.H * Mathf.Deg2Rad), Mathf.Sin(Script.H * Mathf.Deg2Rad));
				Vector2 g = dir * at.x * k;
				float y = Mathf.Max(BallRadius, at.y);
				if (m_dipAt > 0 && Mathf.Abs(cur.T1 - m_dipAt) < 1e-3f && tt > m_dipAt - 0.3f)
				{
					y = Mathf.Lerp(y, DiveGlove, Mathf.SmoothStep(0f, 1f, (tt - (m_dipAt - 0.3f)) / 0.3f));
				}
				return new Vector3(g.x, y, g.y);
			}
			case Ma9MSeg.BallRoll:
			case Ma9MSeg.BallLoose:
			{
				float len = (cur.P1 - cur.P0).magnitude;
				float d = RollDist(len, dur, Mathf.Max(0.5f, cur.Style / 2f), Mathf.Min(t, cur.T1) - cur.T0);
				Vector2 g = len > 1e-3f ? Vector2.Lerp(cur.P0, cur.P1, d / len) : cur.P1;
				// A little hop off the grass now and then.
				float hop = cur.Kind == Ma9MSeg.BallLoose ? 0 : Mathf.Abs(Mathf.Sin(d * 0.35f)) * Mathf.Clamp01(1 - u) * 0.15f;
				return new Vector3(g.x, BallRadius + hop, g.y);
			}
			case Ma9MSeg.BallThrow:
			{
				Vector2 g = Vector2.Lerp(cur.P0, cur.P1, u);
				float dist = (cur.P1 - cur.P0).magnitude;
				// A thrown ball: from the hand to the receiver's chest, falling as a thrown ball does in the time it is in the
				// air (g T² / 8 over the straight line; it was a guess from the distance), a long one bouncing once.
				float h0 = HandHeight + 0.4f, h1 = HandHeight;
				float arc = cur.Style == 1 ? 0 : Mathf.Min(7f, 9.81f * dur * dur / 8f);
				float y = Mathf.Lerp(h0, h1, u) + arc * 4 * u * (1 - u);
				if (cur.Style == 1)
				{
					// One hop: down to the grass three quarters of the way, up into the glove.
					y = u < 0.75f ? Mathf.Lerp(h0, BallRadius, u / 0.75f) + 2.5f * Mathf.Sin(Mathf.PI * u / 0.75f) : Mathf.Lerp(BallRadius, h1, (u - 0.75f) / 0.25f) + 0.6f * Mathf.Sin(Mathf.PI * (u - 0.75f) / 0.25f);
				}
				return new Vector3(g.x, y, g.y);
			}
			default:
				// Held: the holder's glove (the player puts it in the hand bone when it can).
				holder = cur.Arg;
				Vector2 h = Vector2.Lerp(cur.P0, cur.P1, u);
				return new Vector3(h.x, HandHeight, h.y);
		}
	}

	// An actor's ground position at time t from his movement pieces (start: where he stands at contact).
	public Vector2 PosAt(int actor, float t, Vector2 start, out Ma9MPiece active, out bool moving)
	{
		active = default;
		moving = false;
		Vector2 at = start;
		List<Ma9MPiece> l = PiecesOf(actor);
		if (l == null) return at;
		for (int i = 0; i < l.Count; i++)
		{
			Ma9MPiece p = l[i];
			if (!Moves(p.Kind)) continue;
			if (t < p.T0) break;
			if (t <= p.T1)
			{
				float u = p.T1 > p.T0 ? (t - p.T0) / (p.T1 - p.T0) : 1;
				active = p;
				moving = (p.P1 - p.P0).sqrMagnitude > 0.01f && p.Kind != Ma9MSeg.Wait && p.Kind != Ma9MSeg.Stand;
				if (p.Kind == Ma9MSeg.Move || p.Kind == Ma9MSeg.Run || p.Kind == Ma9MSeg.Back)
				{
					// Speeding up from a standstill over the first third, steady, and slowing down to a stop where he
					// stops there (the same leg in the same time, only no longer at full tilt into a dead stop).
					u = Ease(u, 0.3f, m_brake[actor][i]);
				}
				return Vector2.Lerp(p.P0, p.P1, u);
			}
			at = p.P1;
			active = p;
		}
		return at;
	}

	// Speeding up over the first a of a leg and slowing down over the last b (a + b ≤ 1), steady between, the speed never
	// jumping.
	private static float Ease(float u, float a, float b)
	{
		float c = 1f / (1f - a / 2f - b / 2f);
		if (u < a) return c * u * u / (2f * a);
		if (u <= 1f - b || b <= 0f) return c * (a / 2f + (u - a));
		float r = 1f - u;
		return c * (a / 2f + (1f - a - b) + b / 2f - r * r / (2f * b));
	}

	// Which legs end in a stop (no move of his follows at once, and no catch, throw, tag, dive or slide of his is made at
	// its end): he slows down over the last two metres or so (a fielder) or a metre and a half (a runner onto his base).
	private static List<float> Brakes(List<Ma9MPiece> l, bool runner)
	{
		List<float> brake = new List<float>(l.Count);
		for (int i = 0; i < l.Count; i++)
		{
			Ma9MPiece p = l[i];
			float b = 0f;
			if (p.Kind == Ma9MSeg.Move || p.Kind == Ma9MSeg.Run || p.Kind == Ma9MSeg.Back)
			{
				bool goesOn = false;
				for (int j = 0; j < l.Count; j++)
				{
					Ma9MPiece q = l[j];
					if (j == i || q.T0 < p.T1 - 0.05f || q.T0 > p.T1 + 0.25f) continue;
					bool move = (q.Kind == Ma9MSeg.Move || q.Kind == Ma9MSeg.Run || q.Kind == Ma9MSeg.Back || q.Kind == Ma9MSeg.Dive || q.Kind == Ma9MSeg.Slide || q.Kind == Ma9MSeg.Peel) && q.T0 > p.T0 + 0.01f;
					bool act = q.Kind == Ma9MSeg.Catch || q.Kind == Ma9MSeg.Throw || q.Kind == Ma9MSeg.Tag;
					if (move || act) goesOn = true;
				}
				float len = (p.P1 - p.P0).magnitude;
				if (!goesOn && len > 0.3f) b = Mathf.Clamp((runner ? 1.5f : 2f) / len, 0.15f, 0.6f);
			}
			brake.Add(b);
		}
		return brake;
	}

	public static bool Moves(Ma9MSeg k) => k == Ma9MSeg.Move || k == Ma9MSeg.Dive || k == Ma9MSeg.Run || k == Ma9MSeg.Slide || k == Ma9MSeg.Back || k == Ma9MSeg.Peel || k == Ma9MSeg.Stand || k == Ma9MSeg.Wait;
}
