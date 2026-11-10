using System.Collections.Generic;
using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "카메라 연출도 자연스럽게", close-ups and replays): the
// broadcast camera for a scripted ball in play, cut like a TV broadcast from the script read ahead:
//   - the batted ball from high behind the plate, framed with the man going to get it;
//   - a close-up of a diving, leaping or running catch from just before it;
//   - throws followed the same way, and a close-up of the base when the play there is close (a tag
//     or a force with the runner within a breath of the ball), until the call;
//   - a wide look at the diamond to finish.
// Shots ease toward their framing (no snapping) and cut only between shots. The replay mode uses
// other angles (from the outfield side, low at the bases) for highlights.
public sealed class Ma9MCameraDirector
{
	private enum ShotKind { Track, CatchClose, BaseClose, Settle }

	private struct Shot
	{
		public ShotKind Kind;
		public float From, To;
		public Vector3 Point;     // catch point or base
		public int Actor;         // the fielder of a catch close-up
		public int Base;          // the base of a base close-up
	}

	public delegate Vector3 ActorPos(int actor);

	private readonly List<Shot> m_shots = new List<Shot>();
	private readonly Ma9MScriptTimeline m_tl;
	private readonly bool m_replay;
	private readonly int m_firstCatcher = -1;
	private readonly float m_firstCatchAt = -1;
	private readonly List<Ma9MPiece> m_throws = new List<Ma9MPiece>();
	private readonly int m_stealer = -1;     // a steal: the runner, and the bag he is going for
	private readonly Vector3 m_stealBag;
	private readonly Vector3 m_replayPos;    // the replay's tracking camera: the far dugout from the ball
	private int m_cur = -1;
	private Vector3 m_pos, m_look;
	private float m_fov;
	private bool m_started;
	private float m_wideFrom = float.MaxValue;

	private static readonly Vector3 HomeHigh = new Vector3(-13f, 10.5f, -13f);
	private static readonly Vector3 Mound = new Vector3(13.7f, 0f, 13.7f);

	// The tracking shot's narrowest view in the first 0.8 s after contact (degrees, vertical): the plate, the batter
	// leaving it and the infield around them.
	private const float OffBatFov = 24f;

	public Ma9MCameraDirector(Ma9MScriptTimeline tl, Ma9MPlayScript s, bool replay)
	{
		m_tl = tl;
		m_replay = replay;
		// The batted ball's first real catch or pick-up (not a throw taken, not a miss).
		Ma9MPiece first = default;
		bool found = false;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Catch && p.Style != 9 && p.Style != 10 && (!found || p.T0 < first.T0))
			{
				first = p;
				found = true;
			}
		}
		if (found)
		{
			m_firstCatcher = first.Actor;
			m_firstCatchAt = first.T0;
		}
		// Replays follow the ball from high up the line across from where it went (a ball to the right
		// side from the third-base side, and the other way round), not from the live camera behind the
		// plate; back and high enough that the infielders and umpires near it stay below the picture.
		m_replayPos = new Vector3(-16f, 9f, 14f);
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.BallFly)
			{
				if (p.P1.y > p.P1.x) m_replayPos = new Vector3(14f, 9f, -16f);
				break;
			}
		}
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.BallThrow) m_throws.Add(p);
			// A steal (no batted ball, the pitch in the mitt at 0): framed with the runner and his bag.
			if (s.Ev <= 0 && p.Kind == Ma9MSeg.Run && p.Actor > Ma9MPiece.RunnerBase && m_stealer < 0)
			{
				m_stealer = p.Actor;
				m_stealBag = Ma9MScriptTimeline.World(BaseAt(p.Arg & 15));
			}
		}
		float end = s.End + 0.6f;
		// Base close-ups: a throw's arrival at a base where a runner's call comes within 0.7 s of it.
		List<Shot> closes = new List<Shot>();
		foreach (Ma9MPiece call in s.Pieces)
		{
			if (call.Kind != Ma9MSeg.Out && call.Kind != Ma9MSeg.Safe) continue;
			if (call.Actor < Ma9MPiece.RunnerBase) continue;
			float arrive = -1;
			foreach (Ma9MPiece th in s.Pieces)
			{
				if (th.Kind == Ma9MSeg.BallThrow && Vector2.Distance(th.P1, BaseAt(call.Arg)) < 2.5f && Mathf.Abs(th.T1 - call.T0) < 0.7f) arrive = th.T1;
			}
			if (arrive < 0) continue;
			closes.Add(new Shot { Kind = ShotKind.BaseClose, From = Mathf.Max(0.5f, Mathf.Min(arrive, call.T0) - 0.55f), To = Mathf.Max(arrive, call.T0) + 0.9f, Base = call.Arg % 4, Point = Ma9MScriptTimeline.World(BaseAt(call.Arg)) });
		}
		// A run scoring with no play at the plate gets no shot of its own: the camera stays on the ball and the men
		// handling it (user, 2026-10-31: "득점해서 홈을 밟는걸 카메라로 비춰주는 것 보다 타구가 어떻게 처리되고 있는지를
		// 보여주는게 더 좋을 것 같은데? … 펜스를 친건지 아님 누가 실책을 한건지 알 수가 없음").
		closes.Sort((a, b) => a.From.CompareTo(b.From));
		for (int k = closes.Count - 1; k > 0; k--)
		{
			if (closes[k].Base == closes[k - 1].Base && closes[k].From <= closes[k - 1].To + 0.3f)
			{
				Shot m = closes[k - 1];
				m.To = Mathf.Max(m.To, closes[k].To);
				closes[k - 1] = m;
				closes.RemoveAt(k);
			}
		}
		// A catch worth a close-up: a dive, a leap at the wall, a diving stop (or any catch in a replay). Not a routine
		// running catch in the outfield live (user, 2026-10-31: the outfielder alone on the screen was "굳이 불필요한 장면").
		Shot? catchClose = null;
		if (found && (first.Style == 2 || first.Style == 3 || first.Style == 6 || (replay && first.Style != 5 && first.Style != 8)))
		{
			catchClose = new Shot { Kind = ShotKind.CatchClose, From = Mathf.Max(0.3f, first.T0 - 0.6f), To = first.T0 + 1.1f, Actor = first.Actor, Point = Ma9MScriptTimeline.World(first.P1) };
		}
		// Lay the shots out: tracking between the close-ups, a settle shot at the end.
		float t = 0;
		void Add(Shot sh)
		{
			// A close-up straight after another: cut to it at once rather than to a moment of the wide
			// shot in between.
			if (sh.Kind != ShotKind.Track && m_shots.Count > 0 && sh.From > t && sh.From - t < 0.8f) sh.From = t;
			if (sh.From > t + 0.05f) m_shots.Add(new Shot { Kind = ShotKind.Track, From = t, To = sh.From });
			sh.From = Mathf.Max(sh.From, t);
			if (sh.To > sh.From + 0.3f)
			{
				m_shots.Add(sh);
				t = sh.To;
			}
		}
		if (catchClose.HasValue) Add(catchClose.Value);
		foreach (Shot c in closes)
		{
			// One that starts during the last shot picks up where it ends, if enough of it is left.
			if (c.To > t + 0.6f) Add(c);
		}
		float lastAction = Mathf.Max(t, LastEvent(s));
		// A moment of tracking between a close-up and the settle shot is a jump cut: settle at once.
		if (lastAction - t < 0.8f) lastAction = t;
		if (lastAction + 0.6f < end)
		{
			Add(new Shot { Kind = ShotKind.Track, From = lastAction, To = lastAction });
			m_shots.Add(new Shot { Kind = ShotKind.Settle, From = Mathf.Max(t, lastAction), To = end + 5f });
		}
		else
		{
			m_shots.Add(new Shot { Kind = ShotKind.Track, From = t, To = end + 5f });
		}
	}

	// The moment a replay is about: its first close-up (catch or base), or else the first catch, or
	// the end. From and to bound the slow-motion replay.
	public void KeyMoment(float end, out float from, out float to)
	{
		foreach (Shot sh in m_shots)
		{
			if (sh.Kind == ShotKind.CatchClose || sh.Kind == ShotKind.BaseClose)
			{
				from = Mathf.Max(0f, sh.From - 1.5f);
				to = Mathf.Min(end, sh.To + 0.4f);
				return;
			}
		}
		if (m_firstCatchAt > 0)
		{
			// The ball off the bat until it is fielded (a hit's runs come in after).
			from = 0f;
			to = Mathf.Min(end, Mathf.Min(m_firstCatchAt + 1.0f, 6f));
			return;
		}
		from = 0f;
		to = end;
	}

	private static float LastEvent(Ma9MPlayScript s)
	{
		float last = 0;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.BallThrow || p.Kind == Ma9MSeg.Catch || p.Kind == Ma9MSeg.Out || p.Kind == Ma9MSeg.Safe || p.Kind == Ma9MSeg.Score || p.Kind == Ma9MSeg.Tag) last = Mathf.Max(last, p.T1 + 0.4f);
		}
		return last;
	}

	private static Vector2 BaseAt(int b)
	{
		switch (b % 4)
		{
			case 1: return new Vector2(27.43f, 0);
			case 2: return new Vector2(27.43f, 27.43f);
			case 3: return new Vector2(0, 27.43f);
			default: return Vector2.zero;
		}
	}

	// From time t on, the wide look at the diamond whatever the play had next (the inning over: everybody walking off).
	public void WideFrom(float t) => m_wideFrom = Mathf.Min(m_wideFrom, t);

	// Sets the camera for time t. ball: where the ball is; actor: where an actor stands.
	public void Apply(Camera cam, float t, Vector3 ball, ActorPos actor)
	{
		if (cam == null || m_shots.Count == 0) return;
		int k = 0;
		while (k < m_shots.Count - 1 && t >= m_shots[k].To) k++;
		Shot sh = m_shots[k];
		if (t >= m_wideFrom && sh.Kind != ShotKind.Settle)
		{
			k = -2;
			sh = new Shot { Kind = ShotKind.Settle };
		}
		bool cut = k != m_cur;
		m_cur = k;
		Vector3 pos, look;
		float fov;
		switch (sh.Kind)
		{
			case ShotKind.CatchClose:
			{
				// Between the fielder and the plate, off to one side, a little above him.
				Vector3 f = actor(sh.Actor);
				Vector3 toHome = (-new Vector3(sh.Point.x, 0, sh.Point.z)).normalized;
				Vector3 side = Vector3.Cross(Vector3.up, toHome);
				float far = m_replay ? 7f : 9f;
				pos = sh.Point + toHome * far + side * (m_replay ? -4f : 3f) + Vector3.up * (m_replay ? 1.4f : 2.4f);
				// The man and the ball coming down to him: a ball still high is let out of the top.
				Vector3 b = Framed(ball, 2.5f, 0.2f);
				look = Vector3.Lerp(f, b, 0.35f) + Vector3.up * 0.6f;
				fov = FitFov(pos, look, new[] { f + Vector3.up * 1.2f, f, b }, cam.aspect, 22f, 40f);
				break;
			}
			case ShotKind.BaseClose:
				BaseCamera(sh.Base, out pos);
				look = sh.Point + Vector3.up * 0.5f;
				fov = m_replay ? 20f : 26f;
				break;
			case ShotKind.Settle:
				pos = HomeHigh;
				look = Mound + new Vector3(4f, 0, 4f);
				fov = 42f;
				break;
			default:
			{
				// High behind the plate, the ball and the man going after it in the picture.
				pos = m_replay ? m_replayPos : HomeHigh;
				// A fly ball's height is played down, so the field and the man under it stay in the
				// picture rather than the sky (the ball comes back into it as it drops).
				List<Vector3> pts = new List<Vector3> { Framed(ball, 4f, 0.3f) };
				if (m_stealer >= 0)
				{
					// A steal: the catcher's throw, the runner and the bag he is racing it to.
					if (t < 0.6f) pts.Add(new Vector3(0f, 1.2f, 0f));
					pts.Add(actor(m_stealer) + Vector3.up);
					pts.Add(m_stealBag + Vector3.up * 0.5f);
				}
				// Off the bat: the plate and the ball leaving it, before closing in on the ball.
				else if (t < 0.8f) pts.Add(new Vector3(0f, 1.2f, 0f));
				if (m_firstCatcher >= 0 && m_firstCatcher < 9 && t < m_firstCatchAt + 0.5f)
				{
					Vector3 f = actor(m_firstCatcher);
					Vector2 bg = new Vector2(ball.x - f.x, ball.z - f.z);
					// Once the ball is off the bat (or near him), he is in the frame with it.
					if (t > 0.8f || bg.magnitude < 35f || m_firstCatchAt - t < 1.6f) pts.Add(f + Vector3.up);
				}
				// A throw: the thrower as he lets it go, and where it is going all the way there.
				foreach (Ma9MPiece th in m_throws)
				{
					if (t < th.T0 - 0.3f || t > th.T1 + 0.3f) continue;
					pts.Add(Ma9MScriptTimeline.World(th.P1) + Vector3.up);
					if (t < th.T0 + 0.4f) pts.Add(Ma9MScriptTimeline.World(th.P0) + Vector3.up);
				}
				look = Centre(pts);
				// The ball alone: not so tight that it is only grass around it. Off the bat not tighter than OffBatFov: the
				// plate and the ball leaving it alone (an infield ball whose fielder is not in the frame yet) were drawn in
				// at 7°, and the batter's and the umpire's helmets filled the screen for half a second (user, 2026-11-05:
				// "순간적으로 시야가 확 가려졌다가 경기장이 다시 보일때가 있는데").
				fov = FitFov(pos, look, pts.ToArray(), cam.aspect, t < 0.8f ? OffBatFov : pts.Count > 1 ? 7f : 12f, 45f);
				break;
			}
		}
		pos = Unblock(pos, look, actor);
		if (cut || !m_started)
		{
			m_pos = pos;
			m_look = look;
			m_fov = fov;
			m_started = true;
		}
		else
		{
			// Ease toward the framing: the look a little behind the ball, the zoom slower still.
			float dt = Time.deltaTime;
			m_pos = Vector3.Lerp(m_pos, pos, 1 - Mathf.Exp(-dt / 0.25f));
			m_look = Vector3.Lerp(m_look, look, 1 - Mathf.Exp(-dt / 0.12f));
			m_fov = Mathf.Lerp(m_fov, fov, 1 - Mathf.Exp(-dt / 0.35f));
		}
		cam.transform.position = m_pos;
		cam.transform.rotation = Quaternion.LookRotation(m_look - m_pos);
		cam.fieldOfView = m_fov;
	}

	// Nobody in front of the lens (user, 2026-10-31: now and then the camera moved oddly, once a replay close-up from
	// inside a fielder's head — the right fielder backing up first stood where the low first-base camera was): a man
	// within 1.6 m of the camera moves it off him and over the heads, and one in the first 4 m of its sight line lifts it
	// over him. Fielders 0-8 and the runners (Ma9MPiece.RunnerBase on).
	private static Vector3 Unblock(Vector3 pos, Vector3 look, ActorPos actor)
	{
		for (int i = 0; i < 13; i++)
		{
			int a = i < 9 ? i : Ma9MPiece.RunnerBase + i - 9;
			Vector3 p = actor(a);
			if (p == Vector3.zero) continue;
			Vector3 flat = new Vector3(pos.x - p.x, 0f, pos.z - p.z);
			if (flat.magnitude < 1.6f && pos.y < 2.8f)
			{
				Vector3 away = flat.sqrMagnitude > 0.01f ? flat.normalized : new Vector3(-p.x, 0f, -p.z).normalized;
				pos = new Vector3(p.x, 0f, p.z) + away * 1.6f + Vector3.up * Mathf.Max(pos.y, 2.8f);
				continue;
			}
			Vector3 head = p + Vector3.up * 1.3f;
			Vector3 dir = (look - pos).normalized;
			float along = Vector3.Dot(head - pos, dir);
			if (along > 0f && along < 4f && (head - (pos + dir * along)).magnitude < 0.7f)
			{
				pos.y = Mathf.Max(pos.y, head.y + 1.2f);
			}
		}
		return pos;
	}

	private void BaseCamera(int b, out Vector3 pos)
	{
		// Live: from the dugout side of first and third, from behind second, from the first-base side
		// of the plate. Replay: low and from the other side.
		switch (b)
		{
			case 1: pos = m_replay ? new Vector3(36f, 1.2f, 6f) : new Vector3(23f, 3f, -10f); break;
			case 2: pos = m_replay ? new Vector3(20f, 1.2f, 37f) : new Vector3(39f, 3.5f, 39f); break;
			case 3: pos = m_replay ? new Vector3(8f, 1.2f, 36f) : new Vector3(-10f, 3f, 24f); break;
			default: pos = m_replay ? new Vector3(-6f, 1.2f, 7f) : new Vector3(9f, 3f, -8f); break;
		}
	}

	private static Vector3 Framed(Vector3 b, float knee, float k) => b.y <= knee ? b : new Vector3(b.x, knee + (b.y - knee) * k, b.z);

	private static Vector3 Centre(List<Vector3> pts)
	{
		Vector3 c = Vector3.zero;
		foreach (Vector3 p in pts) c += p;
		return c / pts.Count;
	}

	// The vertical field of view that holds every point (with a margin) looking from pos at look.
	private static float FitFov(Vector3 pos, Vector3 look, Vector3[] pts, float aspect, float min, float max)
	{
		Vector3 fwd = (look - pos).normalized;
		Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
		Vector3 up = Vector3.Cross(fwd, right);
		float need = 0;
		foreach (Vector3 p in pts)
		{
			Vector3 d = p - pos;
			float z = Vector3.Dot(d, fwd);
			if (z < 0.5f) continue;
			float v = Mathf.Abs(Vector3.Dot(d, up)) / z;
			float h = Mathf.Abs(Vector3.Dot(d, right)) / z / Mathf.Max(0.5f, aspect);
			need = Mathf.Max(need, Mathf.Max(v, h));
		}
		float fov = 2f * Mathf.Atan(need * 1.35f + 0.02f) * Mathf.Rad2Deg;
		return Mathf.Clamp(fov, min, max);
	}
}
