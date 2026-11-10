using System.Collections.Generic;
using System.IO;
using UnityEngine;

// Ma9M port (engine session, 2026-10-30): plays a ball in play back from the server's script
// (Ma9MScripts) instead of the original AI. At contact (PlayManager HIT) the fielders', runners' and
// ball's own logic is parked (safe states, components off) and every frame this sets their
// positions and animations from the script; catches, dives, throws, tags, slides, calls and runs
// fire at their times. The scene ends when the script does (PlayManager INPLAY), and everything is
// handed back at ENDSCENE. "-ma9mnoscript" keeps the old AI for comparison.
public class Ma9MScriptPlayer : MonoBehaviour
{
	public static Ma9MScriptPlayer Instance;

	public static bool Active => Instance != null && Instance.m_active;

	public static bool Done => Active && Instance.m_done;

	// The ball is in a glove (or out of sight as everybody walks off): Ball.UpdateShadowPos takes its shadow away, as the
	// original does for a ball an infielder holds (engine session, 2026-11-05: the shadow stayed on the grass under the
	// glove of a diving catch and then under the man).
	public static bool BallInGlove => Active && (Instance.m_holder >= 0 || Instance.m_walkingOff);

	// Highlights: the play once more, slower, from the replay angles (calls and the score not again).
	public bool Replayed { get; private set; }
	private float m_boardShown = -1f;   // Time.time the score board went up in this play (-1 not)

	// In highlights: the replay waits until the score board has been up long enough to read.
	public bool ReplayReady()
	{
		BroadCastScore b = m_pm != null && m_pm.parentWindow != null ? m_pm.parentWindow.BScore : null;
		return m_boardShown < 0 || b == null || b.GetState() == BroadCastScoreState.IDLE || Time.time >= m_boardShown + 1.8f;
	}

	public const float ReplaySpeed = 0.6f;

	// Live and in a game's highlights the play runs this much faster than the engine's real time (user, 2026-10-31:
	// "타구랑 송구 속도가 전체적으로 너무 느림 새 엔진 적용하기 전이랑 너무 달라서 이질감"): the ball, the men and the
	// camera alike, so every call stays as the engine made it. 허슬 플레이 clips play at 1 (their replay at 0.6).
	public const float PlayRate = 1.3f;

	// After the play, until the next scene takes over (ENDSCENE and WAITPACKET, a second or so), it plays on under its
	// camera instead of stopping where it was cut (user, 2026-10-31: "화면이 0.5초정도 멈춤, 자연스럽지 않음" — the ball
	// hung in the air and men ran on the spot under a camera held still); no calls or runs then. EndCoast ends it.
	private bool m_coast;
	// The inning over (its third out in this play, or a scene without a script ended it): from this time everybody walks
	// off to his dugout (user, 2026-10-31: "이닝이 끝나는 아웃카운트가 잡히면 공격진영이든 수비진영이든 둘 다 각자
	// 더그아웃으로 걸어가는 모션"). -1 not.
	private float m_walkOffAt = -1f;
	private bool m_walkingOff;
	public const float WalkSpeed = 1.5f;
	private static AnimationClip s_walkClip;
	private readonly Transform[] m_glove = new Transform[9];

	private bool m_replay;
	private float m_replayEnd;

	private const int Actors = 24, RunnerBase = 20;

	private Ma9MScriptTimeline m_tl;
	private bool m_active, m_done;
	private float m_t, m_end;
	private PlayManager m_pm;
	private readonly Transform[] m_tr = new Transform[Actors];
	private readonly Animation[] m_anim = new Animation[Actors];
	private readonly Vector2[] m_start = new Vector2[Actors];
	private readonly float[] m_actionUntil = new float[Actors];
	private readonly string[] m_clip = new string[Actors];
	private readonly float[] m_outFade = new float[Actors];   // > 0: leave the action this slowly (it plays on as it fades)
	// A turn under way (the throw: toward the target in a fraction of a second rather than at once).
	private readonly Vector2[] m_turnTo = new Vector2[Actors];
	private readonly float[] m_turnUntil = new float[Actors];
	private readonly float[] m_leaveAt = new float[Actors];
	private readonly Vector2[] m_leaveFrom = new Vector2[Actors];
	private readonly bool[] m_leaving = new bool[Actors];
	private readonly Vector2[] m_leaveTo = new Vector2[Actors];
	private readonly Vector2[] m_off = new Vector2[Actors];
	private readonly float[] m_offT0 = new float[Actors];
	private readonly float[] m_offDur = new float[Actors];
	private readonly float[] m_flyCatchAt = new float[Actors];
	// A ground ball scooped (user, 2026-11-01: "글러브를 갖다대서 공을 줍는거지, 공이 자석마냥 끌려가서 공을 줍는게 아님"):
	// the script stands him a reach short of the ball, and the ball was drawn into his glove. Now he steps in to where
	// his glove is on the ball's spot when it gets there, facing the way it comes, the scoop held at its glove-down
	// first frame until then; he stays there (whatever little steps the script has him take) until he next really
	// moves (a throw leaves from where he is and comes in where the script has it).
	private readonly Vector2[] m_stand = new Vector2[Actors];
	private readonly float[] m_lungeFrom = new float[Actors], m_lungeAt = new float[Actors], m_lungeUntil = new float[Actors], m_scoopAt = new float[Actors];
	// A slide's move so his front foot (hand) ends on the bag (ReachBag): from when, by when, until when.
	private readonly Vector2[] m_slideShift = new Vector2[Actors];
	private readonly float[] m_slideFrom = new float[Actors], m_slideAt = new float[Actors], m_slideUntil = new float[Actors];
	// The glove in the scoop's first frame (Def_GrounderCatchSuc and Def_InfldGrounderCatchSuc alike), from his feet:
	// ahead of him and out to his glove side (m).
	private const float ScoopForward = 0.25f, ScoopSide = 0.09f, LungeMax = 1.5f;
	// 1.8 m from where it puts his glove on the ball (the glove was 0.3 m off at 1.5).
	private const float LungeLong = 2.4f;
	// The clip's speed once the ball is in (the scoop's rise, a dive's landing and getting up).
	private readonly float[] m_upSpeed = new float[Actors];
	// A diving catch's clip kept on the script's clock until the catch (catch time, the clip's catch frame, the clip;
	// -1 none): a slow frame started it late and it fell behind (engine session, 2026-11-05; user: "다이빙 캐치할 때 공이
	// 살짝 땅에 닿고, 글러브가 공을 덮는데" — at 15-20 fps the glove was still 1.7-2.3 m short of the ball and 0.3 m
	// lower at the catch, and the ball was pulled down and back into it).
	private readonly float[] m_diveSyncT1 = new float[Actors], m_diveSyncAt = new float[Actors];
	private readonly string[] m_diveSyncClip = new string[Actors];
	// A diving catch's frame in Def_DivingCatchUpIn (s) and its glove from his feet (ahead, out to his glove side, m): in
	// the air, at the top of his flight, the glove out at 1.05 m and nothing of him on the ground (0.18; user, 2026-11-02:
	// "아직도 슬라이딩해서 플라이볼 잡을 때 땅에 떨어진 공을 슬라이딩해서 잡는 현상이 남아있는데" — at 0.24 the glove was
	// coming down through 0.7 m and he was on his belly 0.03 s later, so it looked caught off the grass); a ground ball,
	// the glove down on the grass as he lands (0.30). The ball comes down to that glove (Ma9MScriptTimeline.DiveGlove).
	private const float DiveAirAt = 0.18f, DiveAirAhead = 1.15f, DiveAirSide = 0.10f;
	// The flight before the catch frame is not played slower than this (a lazy, floating leap); he leaves his feet a
	// little later instead.
	private const float DiveAirMinSpeed = 0.7f;
	private const float DiveGroundAt = 0.30f, DiveGroundAhead = 1.11f, DiveGroundSide = 0.30f;
	// How fast he really goes over the ground (smoothed), from where he was put last frame: what his run is matched to.
	private readonly Vector2[] m_lastPos = new Vector2[Actors], m_vel = new Vector2[Actors];
	private readonly bool[] m_hasLast = new bool[Actors];
	private const float RunStride = 2.37f;
	// script's spot and facing (StartTrack, Tracking).
	private readonly Ma9MMotions.Info[] m_trk = new Ma9MMotions.Info[Actors];
	private readonly float[] m_trkT0 = new float[Actors], m_trkSpeed = new float[Actors], m_trkTarget = new float[Actors];
	private readonly float[] m_trkLeft = new float[Actors], m_trkUntil = new float[Actors];
	private readonly Vector2[] m_trkFrom = new Vector2[Actors];
	private readonly bool[] m_trkMirror = new bool[Actors];
	private readonly Transform[] m_hand = new Transform[9];
	// Clips to start at set times (a fly ball: the glove up once he stands under it, then the catch).
	private struct Planned
	{
		public float At, Hold, Skip, Speed, Fade;
		public string Clip;
	}

	private readonly List<Planned>[] m_plan = NewPlans();
	// A running catch coming (its time, and from which side: F, BL, BR): his last strides with the glove up.
	private readonly float[] m_runCatchAt = new float[Actors];
	private readonly string[] m_runCatchDir = new string[Actors];
	// A catch in the air (ReachAir): his step in uses the lunge above (not a scoop: he turns with his run); squared to the
	// way the ball comes in from a second before it (standing), and the running catch's side his glove was set for.
	private readonly bool[] m_airReach = new bool[Actors];
	private readonly Vector2[] m_airFace = new Vector2[Actors];
	private readonly float[] m_airFaceFrom = new float[Actors], m_airFaceUntil = new float[Actors];
	private readonly string[] m_airSide = new string[Actors];

	private static List<Planned>[] NewPlans()
	{
		List<Planned>[] p = new List<Planned>[Actors];
		for (int i = 0; i < Actors; i++) p[i] = new List<Planned>();
		return p;
	}
	// The throw in the air: from his hand as it left it, onto the script's line (the difference fading over the flight).
	private float m_throwT0 = -1f;
	private Vector3 m_throwOff;
	private static readonly string[] ReadyClips = { "M_Fielder_Idle_InfielderA_01", "M_Fielder_Idle_InfielderB_01", "M_Fielder_Idle_InfielderC_01", "M_Fielder_Idle_Infielder01", "M_Fielder_Idle_Infielder02" };

	// Tag plays: the fielder's glove goes to the runner's lead foot (Ma9MTagReach).
	private struct TagPlay
	{
		public int F, R;
		public float T;
		public Vector2 Bag;
		public bool Head;
	}

	private readonly List<TagPlay> m_tags = new List<TagPlay>();
	private readonly Ma9MTagReach.Arm[] m_arm = new Ma9MTagReach.Arm[9];
	private readonly Ma9MTagReach.Limbs[] m_limbs = new Ma9MTagReach.Limbs[4];
	private TableReader.BallPreset m_preset;
	private float m_presetV, m_presetH;
	private Vector3 m_presetCatch;
	private readonly Fielder[] m_fielders = new Fielder[9];
	private readonly Runner[] m_runners = new Runner[4];
	private FielderManager m_fm;
	private Ball m_ball;
	private Renderer m_ballRenderer;
	private int m_scored, m_scene, m_holder = -1;
	private float m_nextShot = float.MaxValue;

	// Dev: "-ma9mscriptshots <dir>" saves a screenshot every half second of each scripted play and the
	// script as text beside them, so plays can be checked without watching.
	private static readonly string s_shotDir = ShotDir();
	private static string s_overrideName;
	private static readonly bool s_oldCam = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9moldcam") >= 0;
	private Ma9MCameraDirector m_director;
	private static readonly Dictionary<int, int> s_overridePlays = new Dictionary<int, int>();

	// Dev: "-ma9mshotstep <s>" shoots that often (default 0.5); "-ma9mtagcam" puts the camera close
	// to a tag play from a second before the tag (to see where the glove goes).
	private static readonly float s_shotStep = DevFloat("-ma9mshotstep", 0.5f);
	private static readonly bool s_tagCam = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9mtagcam") >= 0;
	// Dev: "-ma9mclosecam" keeps the camera five and a half metres off the man with the ball (or the one it is going to,
	// or the batter-runner), to see the motions.
	private static readonly bool s_closeCam = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9mclosecam") >= 0;
	// "-ma9mclosecam runner": on the batter-runner all the way.
	private static readonly bool s_closeRunner = s_closeCam && System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "runner") > System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9mclosecam");
	// Dev: "-ma9mtopcam" looks straight down on that man from 9 m up (which way he faces, his arm and the ball's way).
	private static readonly bool s_topCam = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9mtopcam") >= 0;
	private Vector2 m_topFrom, m_topTo;
	private float m_topUntil = -1f;
	private int m_camOn = -1;

	private static float DevFloat(string name, float def)
	{
		string[] args = System.Environment.GetCommandLineArgs();
		int i = System.Array.IndexOf(args, name);
		return i >= 0 && i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? Mathf.Max(0.05f, v) : def;
	}

	private static string ShotDir()
	{
		string[] args = System.Environment.GetCommandLineArgs();
		int i = System.Array.IndexOf(args, "-ma9mscriptshots");
		if (i < 0 || i + 1 >= args.Length) return null;
		Directory.CreateDirectory(args[i + 1]);
		return args[i + 1];
	}

	private enum EvKind { Catch, Dive, Throw, Tag, Slide, Out, Safe, Score, ScoreBoard, CatchOut, Camera, Sound }

	private struct Ev
	{
		public float At;
		public EvKind Kind;
		public Ma9MPiece P;
		public string Clip;
		public float Vol;
	}

	private readonly List<Ev> m_events = new List<Ev>();
	private int m_next;
	private float m_pullLogged = -1f;

	public static int EPosOf(int deck) => deck == 8 ? 0 : deck + 1;

	// Start the script of this scene if there is one (and scripts are on); false = play it the old way.
	public static bool TryBegin(PlayManager pm, int scene)
	{
		if (Ma9MScripts.Disabled) return false;
		Ma9MPlayScript s = Ma9MScripts.For(scene);
		if (s == null || s.Pieces.Count == 0) return false;
		s_overrideName = null;
		if (Ma9MScripts.Overrides != null)
		{
			// Dev: the least played test script whose runners are all on base in this scene.
			BatterManager bm = pm.GetBatterManager();
			int best = -1;
			for (int k = 0; k < Ma9MScripts.Overrides.Count; k++)
			{
				bool fits = true;
				foreach (Ma9MPiece p in Ma9MScripts.Overrides[k].script.Pieces)
				{
					if (p.Actor > RunnerBase && p.Actor < RunnerBase + 4 && (bm == null || !bm.IsInGameRunner(p.Actor - RunnerBase))) fits = false;
				}
				int played = s_overridePlays.TryGetValue(k, out int n) ? n : 0;
				if (fits && (best < 0 || played < s_overridePlays.GetValueOrDefault(best))) best = k;
			}
			if (best >= 0)
			{
				s = Ma9MScripts.Overrides[best].script;
				s_overrideName = Ma9MScripts.Overrides[best].name;
				s_overridePlays[best] = s_overridePlays.GetValueOrDefault(best) + 1;
				Debug.Log("[Ma9MScript] test script " + s_overrideName + " in scene " + scene);
			}
		}
		if (Instance == null)
		{
			Instance = pm.gameObject.GetComponent<Ma9MScriptPlayer>() ?? pm.gameObject.AddComponent<Ma9MScriptPlayer>();
		}
		try
		{
			Instance.Begin(pm, s, scene);
			return true;
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MScript] could not start: " + ex);
			Instance.Release();
			return false;
		}
	}

	private void Begin(PlayManager pm, Ma9MPlayScript s, int scene)
	{
		// Fitted to this park's fence (the engine's reference park is up to 4 m deeper).
		s = Ma9MParkWarp.MakeRoom(Ma9MParkWarp.Apply(s, pm.GetFenceData()));
		if (!DataCenter.instance.GetHighlightPlay()) s = LiveCut(s);
		Time.timeScale = Ma9MHustle.Active ? 1f : PlayRate;
		m_coast = false;
		m_walkingOff = false;
		for (int g = 0; g < 9; g++) m_glove[g] = null;
		m_pm = pm;
		Replayed = false;
		m_boardShown = -1f;
		m_replay = false;
		m_scene = scene;
		m_tl = new Ma9MScriptTimeline(s);
		m_t = 0;
		m_topUntil = -1f;
		m_end = s.End + 0.6f;
		m_done = false;
		m_next = 0;
		m_scored = 0;
		for (int a = 0; a < Actors; a++)
		{
			m_tr[a] = null; m_anim[a] = null; m_actionUntil[a] = 0; m_clip[a] = null; m_outFade[a] = 0; m_turnUntil[a] = 0; m_leaveAt[a] = -1; m_leaving[a] = false;
			m_hasLast[a] = false; m_vel[a] = Vector2.zero; m_trk[a] = null; m_plan[a].Clear(); m_runCatchAt[a] = -1f; m_diveSyncT1[a] = -1f;
		}
		for (int d = 0; d < 9; d++) { m_arm[d] = null; m_hand[d] = null; }
		m_throwT0 = -1f;
		m_camOn = -1;
		for (int i = 0; i < 4; i++) m_limbs[i] = null;
		// Fielders: parked in AI_NONE, their Update off; we drive transform and animation.
		m_fm = pm.GetFielderManager();
		for (int d = 0; d < 9; d++)
		{
			Fielder f = m_fm != null ? m_fm.GetFielder((ePos)EPosOf(d)) : null;
			m_fielders[d] = f;
			if (f == null) continue;
			f.ForceState(eFielderState.AI_NONE);
			f.enabled = false;
			m_tr[d] = f.transform;
			m_anim[d] = f.GetComponent<Animation>();
			m_start[d] = new Vector2(f.transform.position.x, f.transform.position.z);
		}
		if (m_fm != null) m_fm.enabled = false;
		// Runners: the batter-runner (0) and the men on first, second, third.
		BatterManager bm = pm.GetBatterManager();
		for (int i = 0; i < 4; i++)
		{
			Runner r = bm != null ? bm.GetRunner(i) : null;
			bool inScript = false;
			foreach (Ma9MPiece q in s.Pieces) if (q.Actor == RunnerBase + i) inScript = true;
			if (!inScript) r = null;
			m_runners[i] = r;
			if (r == null) continue;
			if (r == m_breaking) m_breaking = null;
			r.ForceState(eRunnerState.IDLE);
			r.enabled = false;
			m_tr[RunnerBase + i] = r.transform;
			m_anim[RunnerBase + i] = r.Ma9MAnimation;
			m_start[RunnerBase + i] = new Vector2(r.transform.position.x, r.transform.position.z);
		}
		// The ball: stopped, driven by us.
		m_ball = pm.GetBall();
		if (m_ball != null)
		{
			m_ball.ForceState(eBallState.STOP);
			m_ball.enabled = false;
			m_ballRenderer = m_ball.BallObject != null ? m_ball.BallObject.GetComponentInChildren<Renderer>() : null;
		}
		// Where the client put everybody and where the script starts them differ a little (a runner's
		// lead most): the difference fades out over 0.6 s for a fielder, and for a runner over his first
		// leg (or two seconds of standing), so he jogs to it instead of sliding.
		for (int a = 0; a < Actors; a++)
		{
			m_offT0[a] = -1;
			m_lungeAt[a] = -1;
			m_slideAt[a] = -1;
			m_scoopAt[a] = -1;
			m_airReach[a] = false;
			m_airFaceUntil[a] = -1;
			m_airSide[a] = null;
			if (m_tr[a] == null) continue;
			foreach (Ma9MPiece p in m_tl.PiecesOf(a))
			{
				if (!Ma9MScriptTimeline.Moves(p.Kind)) continue;
				m_off[a] = m_start[a] - p.P0;
				m_offT0[a] = p.T0;
				m_offDur[a] = 0.6f;
				if (a >= RunnerBase)
				{
					// A runner's lead in the client (8 m) is longer than the engine's: the difference
					// melts away over his first leg (he never steps back before running), or over his
					// wait when he stays (2-3 s of lead shuffling).
					float firstLeg = -1f;
					foreach (Ma9MPiece q in m_tl.PiecesOf(a))
					{
						if (q.Kind == Ma9MSeg.Run || q.Kind == Ma9MSeg.Back)
						{
							firstLeg = q.T1;
							break;
						}
					}
					m_offDur[a] = firstLeg > 0 ? Mathf.Max(0.6f, firstLeg - p.T0) : Mathf.Clamp(p.T1 - p.T0, 0.6f, 3f);
				}
				break;
			}
		}
		BuildEvents(s);
		float third = ThirdOutAt(OutTimes(s, out _, out _));
		m_walkOffAt = third >= 0 ? third + 0.7f : -1f;
		foreach (TagPlay tp in m_tags)
		{
			if (m_arm[tp.F] == null && m_tr[tp.F] != null) m_arm[tp.F] = Ma9MTagReach.Arm.Of(m_tr[tp.F]);
			int slot = tp.R - RunnerBase;
			if (m_limbs[slot] == null && m_tr[tp.R] != null) m_limbs[slot] = Ma9MTagReach.Limbs.Of(m_tr[tp.R]);
		}
		if (s_shotDir != null)
		{
			m_nextShot = 0.2f;
			DumpScript(s);
		}
		else
		{
			m_nextShot = float.MaxValue;
		}
		// The broadcast camera frames a ball in play from the ball preset's numbers (launch angles, the
		// catch point it zooms toward, the chasing fielder): give it the script's for this play (the
		// preset is shared, so its values are put back at the end).
		Ma9MPiece first = default;
		bool found = false;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Catch && p.Style != 9 && p.Style != 10 && p.Style != 11 && (!found || p.T0 < first.T0))
			{
				first = p;
				found = true;
			}
		}
		if (found)
		{
			pm.catchPosition = Ma9MScriptTimeline.World(first.P1, 1f);
			pm.catchTime = first.T0;
			pm.chasePos = (ePos)EPosOf(first.Actor);
		}
		m_preset = pm.GetBallData();
		if (m_preset != null)
		{
			m_presetV = m_preset.m_Vangle;
			m_presetH = m_preset.m_Hangle;
			m_presetCatch = m_preset.m_catchpos;
			// The original's pop-up camera (looking down on the infield, above 70°) for any high fly
			// caught on the infield or just past it: real pop-ups go up at 50-70°, and tracking them
			// from the plate showed only sky.
			m_preset.m_Vangle = s.La >= 50f && found && first.P1.magnitude < 70f ? 75f : s.La;
			m_preset.m_Hangle = s.H;
			if (found) m_preset.m_catchpos = pm.catchPosition;
		}
		// "-ma9moldcam": the original broadcast camera (to compare).
		m_director = s_oldCam ? null : new Ma9MCameraDirector(m_tl, s, false);
		// The inning over: the wide look at everybody walking off, once the last call has been seen.
		if (m_director != null && m_walkOffAt >= 0) m_director.WideFrom(m_walkOffAt + 0.3f);
		m_active = true;
		Debug.Log("[Ma9MScript] scene play " + s.Pieces.Count + " pieces, " + s.End.ToString("F1") + " s");
	}

	// At INPLAY: the batter steps out of the box as the batter-runner (the original does this in
	// BatterManager INPLAY, which a scripted play skips).
	public void SwapBatter(BatterManager bm)
	{
		if (bm == null) return;
		bm.ClearBatter();
		Runner r0 = m_runners[0];
		if (r0 != null)
		{
			SkinnedMeshRenderer smr = r0.GetComponentInChildren<SkinnedMeshRenderer>();
			if (smr != null) smr.enabled = true;
		}
	}

	private void BuildEvents(Ma9MPlayScript s)
	{
		m_events.Clear();
		m_tags.Clear();
		bool batterCaught = false;
		for (int a = 0; a < Actors; a++) m_flyCatchAt[a] = -1;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Peel) batterCaught = true;
			// Caught: once he has peeled off he walks to his dugout.
			if (p.Kind == Ma9MSeg.Peel && p.Actor == RunnerBase) Leave(p.Actor, p.T1);
			if (p.Kind == Ma9MSeg.Catch && (p.Style == 0 || p.Style == 1) && p.Actor < Actors) m_flyCatchAt[p.Actor] = p.T0;
		}
		bool caughtCalled = false;
		foreach (Ma9MPiece p in s.Pieces)
		{
			switch (p.Kind)
			{
				case Ma9MSeg.Catch:
					m_events.Add(new Ev { At = p.T0 - CatchLead(p.Style), Kind = EvKind.Catch, P = p });
					if (batterCaught && !caughtCalled && IsAirCatch(p.Style))
					{
						caughtCalled = true;
						m_events.Add(new Ev { At = p.T0 + 0.1f, Kind = EvKind.CatchOut, P = p });
					}
					break;
				case Ma9MSeg.Dive:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Dive, P = p });
					break;
				case Ma9MSeg.Throw:
				{
					// Not into the throw before the ball is in his glove (the windup then plays quicker: Fire).
					float caught = m_tl.HeldSince(p.Actor, p.T0 - 0.02f);
					m_events.Add(new Ev { At = Mathf.Max(p.T0 - ThrowLead(p), caught >= 0 ? caught + 0.08f : 0f), Kind = EvKind.Throw, P = p });
					break;
				}
				case Ma9MSeg.Tag:
					m_events.Add(new Ev { At = p.T0 - (p.Style == 1 ? 0.35f : 0.1f), Kind = EvKind.Tag, P = p });
					if (p.Style == 1 && p.Actor < 9 && p.Arg < 4)
					{
						bool head = false;
						foreach (Ma9MPiece q in s.Pieces)
						{
							if (q.Kind == Ma9MSeg.Slide && q.Actor == RunnerBase + p.Arg && q.Style == 1) head = true;
						}
						m_tags.Add(new TagPlay { F = p.Actor, R = RunnerBase + p.Arg, T = p.T0, Bag = p.P1, Head = head });
					}
					break;
				case Ma9MSeg.Slide:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Slide, P = p });
					break;
				case Ma9MSeg.Out:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Out, P = p });
					break;
				case Ma9MSeg.Safe:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Safe, P = p });
					break;
				case Ma9MSeg.Score:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Score, P = p });
					m_events.Add(new Ev { At = Mathf.Min(Mathf.Max(p.T0 + 1.0f, LastCall(s) + 0.5f), s.End + 0.4f), Kind = EvKind.ScoreBoard, P = p });
					break;
				case Ma9MSeg.BallThrow:
					m_events.Add(new Ev { At = p.T0, Kind = EvKind.Camera, P = p });
					break;
			}
		}
		AddSounds(s);
		m_events.Sort((a, b) => a.At.CompareTo(b.At));
		ReachAirAll();
	}

	// Every catch in the air by a fielder: where he goes for it (ReachAir). Set before the play and again for its replay
	// (a throw after the catch takes over the lunge it uses).
	private void ReachAirAll()
	{
		foreach (Ma9MPiece p in m_tl.Script.Pieces)
		{
			if (p.Kind == Ma9MSeg.Catch && p.Actor < 9 && m_tr[p.Actor] != null && (p.Style == 0 || p.Style == 1 || p.Style == 4)) ReachAir(p.Actor, p);
		}
	}

	// A ball in the air comes down into his glove, not where the script stands him (user, 2026-11-03: "아직도 가끔 외야수가
	// 살짝 옆에 서서 플라이볼 잡으면서 공이 빨려들어가는"): the engine stops him a reach short of the ball, on the side he
	// came from (Choreo.Glove, 0.8 m), and the last 0.2 s pulled the ball that far across into his glove (StepBall). He goes
	// on to where the catch clip's glove meets the ball, facing the way it comes in (on a running catch, the way he runs):
	// over the end of his last run, which bends there, or a step when he did not run; there until he next moves.
	private void ReachAir(int a, Ma9MPiece p)
	{
		Vector3 b = m_tl.BallAt(p.T0 - 0.01f, out _, out _);
		Vector2 ball = new Vector2(b.x, b.z);
		Ma9MPiece run = default;
		bool ran = false;
		foreach (Ma9MPiece q in m_tl.PiecesOf(a))
		{
			bool goes = q.Kind == Ma9MSeg.Move || q.Kind == Ma9MSeg.Run || q.Kind == Ma9MSeg.Back;
			if (!goes || q.T0 >= p.T0 || (q.P1 - q.P0).sqrMagnitude < 0.01f) continue;
			run = q;
			ran = true;
		}
		Vector2 me = ScriptPos(a, p.T0), facing;
		string clip;
		if (p.Style == 1)
		{
			facing = ran ? run.P1 - run.P0 : ball - me;
			m_airSide[a] = RunCatchSide(facing.normalized * 5f, ScriptPos(a, p.T0 - CatchLead(p.Style)), ball);
			clip = "M_Fielder_RunningCatch_" + m_airSide[a];
		}
		else
		{
			facing = Incoming(p.T0);
			clip = p.Style == 4 && b.y < 1f ? "M_Fielder_Catch" : "M_Fielder_FlyBallCatch";
		}
		Ma9MMotions.Info ci = Ma9MMotions.Get(clip);
		if (ci == null || facing.sqrMagnitude < 1e-4f) return;
		// His glove as the ball gets there, from his feet: the catch clip's first frame; under a fly ball he waits for,
		// mostly still the glove-up wait's last (FlyBallCatch_Before, as AirCatch plays it: it ends at the catch, and the
		// catch fades in from 0.03 s before over 0.08), the glove over his head (measured 0.19 m behind the first frame's).
		Vector3 glove = ci.GloveStart;
		Ma9MMotions.Info bi = p.Style == 0 ? Ma9MMotions.Get("M_Fielder_FlyBallCatch_Before") : null;
		if (bi != null && p.T0 - Mathf.Max(p.T0 - CatchLead(p.Style), StandsFrom(a, p.T0)) >= 0.2f) glove = Vector3.Lerp(bi.GloveHigh, ci.GloveStart, 0.03f / 0.08f);
		// The ball comes down through the glove's height as it gets there (not for a liner, too flat to time so).
		if (p.Style != 4)
		{
			m_tl.LiftTo(p.T0, glove.y);
			b = m_tl.BallAt(p.T0 - 0.01f, out _, out _);
			ball = new Vector2(b.x, b.z);
			if (p.Style == 0) facing = Incoming(p.T0);
		}
		facing.Normalize();
		Vector2 right = new Vector2(facing.y, -facing.x);
		// The glove hand is his left; a mirrored figure (a left-hander) wears it on the right (as Scoop).
		float side = m_tr[a].lossyScale.x < 0 ? glove.x : -glove.x;
		m_stand[a] = me + Vector2.ClampMagnitude(ball - facing * glove.z + right * side - me, LungeMax);
		float at = ran ? Mathf.Min(run.T1, p.T0 - 0.02f) : p.T0 - 0.1f;
		float from = ran ? Mathf.Max(run.T0, at - 1.2f) : p.T0 - 0.6f;
		m_lungeFrom[a] = Mathf.Max(0f, Mathf.Min(from, at - 0.25f));
		m_lungeAt[a] = Mathf.Max(m_lungeFrom[a] + 0.1f, at);
		m_lungeUntil[a] = m_tl.NextMove(a, p.T0);
		m_airReach[a] = true;
		m_airFace[a] = facing;
		m_airFaceFrom[a] = p.T0 - 1f;
		m_airFaceUntil[a] = p.Style == 1 ? -1f : p.T0 + 0.05f;
		if (s_shotDir != null) Debug.Log($"[Ma9MScript] air catch st{p.Style} by {a} at {p.T0:F2}: script {me} ball {ball} y {b.y:F2} stand {m_stand[a]} ({(m_stand[a] - me).magnitude:F2} m, from {m_lungeFrom[a]:F2} by {m_lungeAt[a]:F2}) facing {facing} clip {clip}");
	}

	// Which way a ball in the air comes in at t, along the ground (from where it was a quarter second before); from the
	// plate for one dropping straight down.
	private Vector2 Incoming(float t)
	{
		Vector3 b0 = m_tl.BallAt(t - 0.25f, out _, out _), b1 = m_tl.BallAt(t - 0.01f, out _, out _);
		Vector2 d = new Vector2(b0.x - b1.x, b0.z - b1.z);
		return d.sqrMagnitude >= 0.04f ? d : -new Vector2(b1.x, b1.z);
	}

	// A running catch's clip (RunningCatch_F, or over the shoulder _BL / _BR when he runs away from the plate) from how he
	// runs (v, m/s), where he is and where the ball comes down.
	private static string RunCatchSide(Vector2 v, Vector2 pos, Vector2 ball)
	{
		if (v.sqrMagnitude <= 1f || Vector2.Dot(v, -pos) >= 0f) return "F";
		Vector2 toBall = ball - pos;
		return v.x * toBall.y - v.y * toBall.x > 0f ? "BL" : "BR";
	}

	// The sounds of the play (user, 2026-11-03: "공을 잡을 때, 슬라이딩 할때, 기타등등 효과음이 다 사라졌어"): the original's
	// fielders and runners made them as they caught and slid (Fielder/Runner: BroadCast_Catch, BroadCast_Sliding), which a
	private void AddSounds(Ma9MPlayScript s)
	{
		List<float> caught = new List<float>();
		List<Ma9MPiece> ball = new List<Ma9MPiece>();
		foreach (Ma9MPiece p in s.Pieces)
		{
			switch (p.Kind)
			{
				case Ma9MSeg.Catch:
					// Not the pitch in the catcher's mitt that opens a steal (the pitch made that one), nor a dive it got past.
					if (p.T0 > 0.05f && p.Style != 10) AddSound(p.T0, Ma9MSoundLibrary.CatchSound, p.Style == 8 || p.Style == 11 ? 0.7f : 1f);
					caught.Add(p.T0);
					break;
				case Ma9MSeg.Dive:
					// Down on the grass: his catch frame is 0.12 s before he lands (LayOut).
					AddSound(p.T1 + 0.12f, Ma9MSoundLibrary.SlidingSound, 0.8f);
					break;
				case Ma9MSeg.Slide:
					AddSound(p.T0, Ma9MSoundLibrary.SlidingSound, 1f);
					break;
				case Ma9MSeg.BallThrow:
					AddSound(p.T0, Ma9MSoundLibrary.ThrowFlyingSound, 0.7f);
					if (p.Style == 1) AddSound(p.T0 + 0.75f * (p.T1 - p.T0), Ma9MSoundLibrary.BoundSound, 0.8f);
					break;
			}
			if (p.Actor == Ma9MPiece.Ball) ball.Add(p);
		}
		// A batted ball's first bounce, where its flight ends on the grass (not off a glove; a chopper off the plate has the
		// bat's sound).
		ball.Sort((x, y) => x.T0.CompareTo(y.T0));
		for (int i = 0; i + 1 < ball.Count; i++)
		{
			Ma9MPiece f = ball[i];
			bool down = ball[i + 1].Kind == Ma9MSeg.BallRoll || ball[i + 1].Kind == Ma9MSeg.BallLoose;
			if (f.Kind != Ma9MSeg.BallFly || !down || f.T1 - f.T0 < 0.5f || caught.Exists(c => Mathf.Abs(c - f.T1) < 0.05f)) continue;
			AddSound(f.T1, Ma9MSoundLibrary.BoundSound, 0.8f);
		}
	}

	private void AddSound(float at, string clip, float vol) => m_events.Add(new Ev { At = at, Kind = EvKind.Sound, Clip = clip, Vol = vol });

	// When the play's last call is made (the score board waits for it, so it does not cover the close-up).
	private static float LastCall(Ma9MPlayScript s)
	{
		float last = 0;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Out || p.Kind == Ma9MSeg.Safe || p.Kind == Ma9MSeg.Tag) last = Mathf.Max(last, p.T0);
		}
		return last;
	}

	private static bool IsAirCatch(byte style) => style == 0 || style == 1 || style == 2 || style == 3 || style == 4;

	// a fly ball for up to 0.83 s, the last strides of a running catch, the glove out at the bag).
	private static float CatchLead(byte style)
	{
		bool magu = Ma9MMotions.Get("M_Fielder_FlyBallCatch") != null;
		switch (style)
		{
			case 0: return magu ? 0.85f : 0.30f;    // fly ball
			case 1: return magu ? 0.50f : 0.30f;    // running catch
			case 3: return 0.55f;           // leap at the wall
			case 4: return 0.12f;           // liner
			case 5: case 7: case 8: return 0.20f;   // grounder, charging, pick-up
			case 9: return magu ? 0.70f : 0.35f;    // taking a throw
			default: return 0.0f;
		}
	}

	// quicker one for a throw in of under 40 m), infielders, the catcher and the pitcher the infield throw, a flip of a
	// few metres the toss; the game's own (0.5 / 0.3 / 0.2 s) where they are missing.
	private static string ThrowClip(Ma9MPiece p, out float release)
	{
		float dist = (p.P1 - p.P0).magnitude;
		string clip, old;
		if (p.Actor >= 5 && p.Actor <= 7)
		{
			clip = dist < 40f ? "M_Fielder_Throw_Outfielder_Fast" : "M_Fielder_Throw_Outfielder";
			old = "Def_OutfielderThrow";
			release = 0.5f;
		}
		else if (dist < 8f)
		{
			clip = "M_Fielder_Toss";
			old = "Def_Toss";
			release = 0.2f;
		}
		else
		{
			clip = "M_Fielder_Throw_Infielder";
			old = "Def_InfielderThrow";
			release = 0.3f;
		}
		Ma9MMotions.Info info = Ma9MMotions.Get(clip);
		if (info == null || info.ReleaseT <= 0f) return old;
		release = info.ReleaseT;
		return clip;
	}

	private float ThrowLead(Ma9MPiece p)
	{
		ThrowClip(p, out float release);
		return release;
	}

	private void Update()
	{
		BreakStep();
		if (!m_active || (m_done && !m_coast)) return;
		m_t += Time.deltaTime;
		try
		{
			if (m_walkOffAt >= 0 && m_t >= m_walkOffAt && !m_walkingOff) WalkOff();
			StepBall();
			for (int a = 0; a < Actors; a++)
			{
				if (m_tr[a] != null) StepActor(a);
				if (m_tr[a] != null && m_t < m_turnUntil[a] && m_turnTo[a].sqrMagnitude > 1e-4f)
				{
					Quaternion want = Quaternion.LookRotation(new Vector3(m_turnTo[a].x, 0, m_turnTo[a].y));
					m_tr[a].rotation = Quaternion.RotateTowards(m_tr[a].rotation, want, 900f * Time.deltaTime);
				}
			}
			while (m_next < m_events.Count && m_events[m_next].At <= m_t)
			{
				// After the scene: the men's own moves only, no more calls or runs (it was cut after the last).
				EvKind k = m_events[m_next].Kind;
				if (!m_coast || k == EvKind.Catch || k == EvKind.Dive || k == EvKind.Throw || k == EvKind.Slide) Fire(m_events[m_next]);
				m_next++;
			}
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MScript] " + ex);
			m_done = true;
			if (m_coast) Release();
			return;
		}
		if (m_t >= m_nextShot)
		{
			ScreenCapture.CaptureScreenshot(Path.Combine(s_shotDir, $"s{m_scene:000}_{Mathf.RoundToInt(m_nextShot * 100):0000}.png"));
			m_nextShot += s_shotStep;
		}
		if (m_coast) return;
		// A highlight plays on (runners walking off) while its score board is still up before the replay.
		bool waitBoard = !m_replay && DataCenter.instance.GetHighlightPlay() && !ReplayReady();
		if ((m_t >= m_end && !waitBoard) || (m_replay && m_t >= m_replayEnd)) m_done = true;
	}

	private void DumpScript(Ma9MPlayScript s)
	{
		try
		{
			System.Text.StringBuilder b = new System.Text.StringBuilder();
			b.AppendLine($"scene {m_scene} ev {s.Ev:F1} la {s.La:F1} h {s.H:F1} end {s.End:F2}" + (s_overrideName != null ? " test " + s_overrideName : ""));
			for (int a = 0; a < Actors; a++)
			{
				if (m_tr[a] != null) b.AppendLine($"start {a} ({m_start[a].x:F1},{m_start[a].y:F1})");
			}
			foreach (Ma9MPiece p in s.Pieces)
			{
				b.AppendLine($"{p.Kind,-9} a{p.Actor,-2} arg{p.Arg,-3} st{p.Style,-2} {p.T0,6:F2}-{p.T1,6:F2} ({p.P0.x:F1},{p.P0.y:F1})->({p.P1.x:F1},{p.P1.y:F1})");
			}
			File.WriteAllText(Path.Combine(s_shotDir, $"s{m_scene:000}.txt"), b.ToString());
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MScript] dump: " + ex.Message);
		}
	}

	// Leaving the broadcast mid-play: the game's clock back to normal for the screens after.
	private void OnDestroy()
	{
		Time.timeScale = 1f;
	}

	private void LateUpdate()
	{
		if (!m_active) return;
		// The camera: this play's director (Ma9MCameraDirector) until the scene hands back.
		if (m_director != null && Camera.main != null && m_ball != null)
		{
			try
			{
				m_director.Apply(Camera.main, m_t, m_ball.transform.position, a => a >= 0 && a < Actors && m_tr[a] != null ? m_tr[a].position : Vector3.zero);
			}
			catch (System.Exception ex)
			{
				Debug.LogWarning("[Ma9MScript] camera: " + ex.Message);
				m_director = null;
			}
		}
		if ((s_closeCam || s_topCam) && Camera.main != null && !m_replay) CloseCam(Camera.main);
		if (m_done || m_coast) return;
		try
		{
			foreach (TagPlay tp in m_tags)
			{
				float w = Ma9MTagReach.Weight(m_t, tp.T);
				int slot = tp.R - RunnerBase;
				if (w <= 0 || m_tr[tp.F] == null || m_tr[tp.R] == null || m_arm[tp.F] == null || m_limbs[slot] == null) continue;
				Vector3 target = m_limbs[slot].Lead(Ma9MScriptTimeline.World(tp.Bag), tp.Head, m_tr[tp.R]);
				Ma9MTagReach.Apply(m_tr[tp.F], m_arm[tp.F], target, w);
			}
			if (s_tagCam && Camera.main != null)
			{
				foreach (TagPlay tp in m_tags)
				{
					if (m_t < tp.T - 1.0f || m_t > tp.T + 1.0f) continue;
					// From behind the bag (the outfield side), three metres off and a little above it,
					// facing the fielder.
					Vector3 bag = Ma9MScriptTimeline.World(tp.Bag);
					Vector3 inward = (new Vector3(13.7f, 0, 13.7f) - bag).normalized;
					Vector3 side = Vector3.Cross(Vector3.up, inward);
					Camera.main.transform.position = bag - inward * 2.8f + side * 1.2f + Vector3.up * 1.3f;
					Camera.main.transform.LookAt(bag + Vector3.up * 0.3f);
					Camera.main.fieldOfView = 45f;
					break;
				}
			}
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MScript] tag reach: " + ex.Message);
			m_tags.Clear();
		}
	}

	// Dev (-ma9mclosecam): on the man with the ball, the one it goes to next, or the batter-runner; from his right side
	// a little ahead, 5.5 m off and 2 m up. Changes man only once the last one has had him for half a second.
	private float m_camSince;

	private void CloseCam(Camera cam)
	{
		int who = s_closeRunner ? RunnerBase : m_holder;
		if (who < 0 && m_tl.GloveEnd(m_t, out int catcher, out _)) who = catcher;
		if (who < 0 || m_tr[who] == null) who = m_tr[RunnerBase] != null ? RunnerBase : m_camOn;
		if (who != m_camOn && (m_camOn < 0 || m_t - m_camSince > 0.5f))
		{
			m_camOn = who;
			m_camSince = m_t;
		}
		if (m_camOn < 0 || m_tr[m_camOn] == null) return;
		Transform tr = m_tr[m_camOn];
		if (s_topCam)
		{
			// Over the man with the ball and the one his next throw goes to (or him alone), high enough for both; the
			// first-base line across the picture (screen up = toward the third-base line).
			Transform th = m_holder >= 0 && m_holder < 9 ? m_tr[m_holder] : null;
			if (th != null && NextThrowAny(m_holder, out Vector2 goes) - m_t < 1.5f)
			{
				m_topFrom = new Vector2(th.position.x, th.position.z);
				m_topTo = goes;
				m_topUntil = NextThrowAny(m_holder, out _) + 0.6f;
			}
			Vector3 mid = tr.position;
			float span = 0f;
			if (m_t < m_topUntil)
			{
				Vector2 c = (m_topFrom + m_topTo) * 0.5f;
				mid = new Vector3(c.x, 0, c.y);
				span = Vector2.Distance(m_topFrom, m_topTo);
			}
			cam.transform.position = mid + Vector3.up * Mathf.Max(9f, span * 1.3f);
			cam.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
			cam.fieldOfView = 40f;
			return;
		}
		Vector3 side = tr.right * Mathf.Sign(tr.lossyScale.x), at = tr.position + Vector3.up * 0.7f;
		cam.transform.position = at + (side * 0.85f + tr.forward * 0.5f).normalized * 5.5f + Vector3.up * 1.3f;
		cam.transform.LookAt(at);
		cam.fieldOfView = 34f;
	}

	private void StepBall()
	{
		if (m_ball == null) return;
		if (m_walkingOff)
		{
			// In a glove (the man who made the last out has it) on the way off.
			if (m_ballRenderer != null) m_ballRenderer.enabled = false;
			m_holder = -1;
			return;
		}
		Vector3 p = m_tl.BallAt(m_t, out int holder, out Ma9MSeg kind);
		m_holder = kind == Ma9MSeg.BallHeld ? holder : -1;
		bool held = kind == Ma9MSeg.BallHeld;
		if (m_ballRenderer != null && m_ballRenderer.enabled == held) m_ballRenderer.enabled = !held;
		if (!held)
		{
			// The last 0.2 s into the glove of the man catching it (a fly ball came down beside him, or into his
			// neck: user, 2026-10-31, "플라이 아웃의 경우 수비수 글러브 위에서 공이 떨어져서 잡히게").
			// A ground ball goes on along the ground into the glove he has put down for it, the last few centimetres only.
			if (m_tl.GloveEnd(m_t, out int catcher, out float caughtAt))
			{
				int style = m_tl.CatchStyleAt(catcher, caughtAt);
				bool ground = style == 5 || style == 6 || style == 7 || style == 8;
				if (s_shotDir != null && style == 2 && caughtAt - m_t < 0.5f && Glove(catcher) != null)
				{
					// Dev (2026-11-05; user: "다이빙 캐치할 때 공이 살짝 땅에 닿고, 글러브가 공을 덮는데"): a diving catch's last
					// half second, frame by frame: the ball's height on its way and his glove's.
					Transform gl = Glove(catcher);
					Debug.Log($"[Ma9MScript] dive frame by {catcher} t {m_t:F3} to catch {caughtAt - m_t:F3}: ball y {p.y:F2} glove y {gl.position.y:F2} apart {Vector3.Distance(p, gl.position):F2} m");
				}
				float into = ground ? 0.06f : 0.2f;
				Transform glove = caughtAt - m_t < into ? Glove(catcher) : null;
				if (glove != null)
				{
					Vector3 g = ground ? new Vector3(glove.position.x, p.y, glove.position.z) : glove.position;
					if (s_shotDir != null && caughtAt - m_t < 1.5f * Time.deltaTime && m_pullLogged != caughtAt)
					{
						// Dev: how far off its own way the glove takes the ball in its last frame.
						m_pullLogged = caughtAt;
						Debug.Log($"[Ma9MScript] into the glove st{style} by {catcher} t {m_t:F2}: ball {p} glove {g} pulled {new Vector2(g.x - p.x, g.z - p.z).magnitude:F2} m across, {g.y - p.y:F2} m up");
					}
					p = Vector3.Lerp(p, g, Mathf.SmoothStep(0f, 1f, 1f - (caughtAt - m_t) / into));
				}
			}
			// A throw leaves from his hand (wherever his steps, his scoop and the clip put it) and comes in where the script
			// has it, the difference fading over the flight.
			if (kind == Ma9MSeg.BallThrow && m_tl.ThrowNow(m_t, out int thrower, out float th0, out float th1))
			{
				if (th0 != m_throwT0)
				{
					m_throwT0 = th0;
					Transform hand = Hand(thrower);
					m_throwOff = hand != null ? hand.position - m_tl.BallAt(th0 + 1e-4f, out _, out _) : Vector3.zero;
					if (m_throwOff.magnitude > 3f) m_throwOff = Vector3.zero;
				}
				p += m_throwOff * (1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((m_t - th0) / Mathf.Max(0.05f, th1 - th0))));
			}
			m_ball.transform.position = p;
			// Its spin, as Ball.Update gave it (it is off while the script plays; user, 2026-10-31: "송구할 때 공에 회전이
			// 없어짐. 그냥 너클볼이 날아다니는 것 같음"): 1,720°/s off the bat, half that thrown, a sixth rolling.
			float spin = kind == Ma9MSeg.BallFly ? 1720f : kind == Ma9MSeg.BallThrow ? 860f : kind == Ma9MSeg.BallRoll || kind == Ma9MSeg.BallLoose ? 290f : 0f;
			if (spin > 0 && m_ball.BallObject != null) m_ball.BallObject.transform.Rotate(new Vector3(spin * Time.deltaTime, 0f, spin * Time.deltaTime));
		}
		else if (holder >= 0 && holder < 9 && m_tr[holder] != null)
		{
			// Keep it with the man holding it (hidden), so the camera keeps looking at him.
			Vector3 h = m_tr[holder].position;
			m_ball.transform.position = new Vector3(h.x, Ma9MScriptTimeline.HandHeight, h.z);
		}
	}

	private void StepActor(int a)
	{
		Transform tr = m_tr[a];
		if (tr == null) return;
		bool runner = a >= RunnerBase;
		Vector2 pos;
		Ma9MPiece active;
		bool moving;
		{
			// How fast he went since last frame (smoothed; a jump to a new spot is not a speed).
			Vector2 here = new Vector2(tr.position.x, tr.position.z);
			float dt = Time.deltaTime;
			if (m_hasLast[a] && dt > 1e-4f)
			{
				Vector2 v = (here - m_lastPos[a]) / dt;
				if (v.magnitude < 20f) m_vel[a] = Vector2.Lerp(m_vel[a], v, 1f - Mathf.Exp(-12f * dt));
			}
			m_lastPos[a] = here;
			m_hasLast[a] = true;
		}
		if (m_leaveAt[a] >= 0 && m_t >= m_leaveAt[a])
		{
			if (!m_leaving[a])
			{
				m_leaving[a] = true;
				m_leaveFrom[a] = new Vector2(tr.position.x, tr.position.z);
			}
			// Out, home, or the inning over: he walks off to his dugout.
			Vector2 dir = m_leaveTo[a] - m_leaveFrom[a];
			float k = Mathf.Min(1f, (m_t - m_leaveAt[a]) * WalkSpeed / Mathf.Max(1f, dir.magnitude));
			pos = m_leaveFrom[a] + dir * k;
			moving = k < 1f;
			active = default;
			Face(tr, dir);
			Ma9MMotions.Info walk = Ma9MMotions.Get("M_Runner_Walk");
			if (moving) Cycle(a, "M_Runner_Walk", walk != null && walk.Speed > 0.5f ? WalkSpeed / walk.Speed : 1f, "Run_Walk", 1f);
			else Cycle(a, a < 9 ? "M_Fielder_Idle_Outfielder" : "M_Runner_Idle", 1f, "Run_BasicStd", 1f);
			Place(tr, pos, a);
			return;
		}
		if (m_trk[a] != null && Tracking(a, tr)) return;
		while (m_plan[a].Count > 0 && m_plan[a][0].At <= m_t)
		{
			Planned q = m_plan[a][0];
			m_plan[a].RemoveAt(0);
			Action(a, q.Clip, q.Hold, q.Speed, q.Fade);
			// From where it would be now had this frame come on time.
			float late = Mathf.Max(0f, m_t - q.At) * q.Speed;
			if ((q.Skip > 0f || late > 0f) && m_anim[a] != null && m_anim[a][q.Clip] != null) m_anim[a][q.Clip].time = q.Skip + late;
		}
		if (m_diveSyncT1[a] >= 0)
		{
			AnimationState ds = m_anim[a] != null && m_clip[a] == m_diveSyncClip[a] ? m_anim[a][m_diveSyncClip[a]] : null;
			if (m_t >= m_diveSyncT1[a]) m_diveSyncT1[a] = -1f;
			else if (ds != null)
			{
				float want = m_diveSyncAt[a] - (m_diveSyncT1[a] - m_t) * ds.speed;
				if (want > ds.time + 1e-3f) ds.time = want;
			}
		}
		pos = m_tl.PosAt(a, m_t, m_start[a], out active, out moving);
		if (runner && moving && active.Kind == Ma9MSeg.Run) pos += Rounding(a, active, pos);
		if (m_scoopAt[a] >= 0 && m_t >= m_scoopAt[a])
		{
			// The ball is in: up with it.
			float caught = m_scoopAt[a];
			m_scoopAt[a] = -1;
			if (m_anim[a] != null && m_clip[a] != null && m_anim[a][m_clip[a]] != null) m_anim[a][m_clip[a]].speed = m_upSpeed[a];
			if (s_shotDir != null && a < 9 && Glove(a) != null && m_ball != null)
			{
				Vector3 g = Glove(a).position, bp = m_tl.BallAt(caught - 0.01f, out _, out _);
				Vector3 local = tr.InverseTransformPoint(new Vector3(bp.x, g.y, bp.z));
				Debug.Log($"[Ma9MScript] glove at the catch by {a} t {m_t:F2}: glove {g} ball {bp} apart {new Vector2(g.x - bp.x, g.z - bp.z).magnitude:F2} m; ball in his frame {local}; scale {tr.lossyScale.x:F0}");
			}
		}
		if (m_offT0[a] >= 0 && m_t >= m_offT0[a]) pos += m_off[a] * (1f - Mathf.Clamp01((m_t - m_offT0[a]) / m_offDur[a]));
		pos += Lunge(a, pos);
		pos += SlideShift(a);
		if (m_offT0[a] >= 0 && m_t >= m_offT0[a])
		{
			float k = Mathf.Clamp01((m_t - m_offT0[a]) / m_offDur[a]);
			if (!moving && k < 1f && m_off[a].magnitude > 1.5f && m_t >= m_actionUntil[a])
			{
				// Still making up a real difference while the script has him standing: a runner
				// shuffles it in his lead stance, a fielder jogs it.
				Place(tr, pos, a);
				if (runner && HasClip(a, "Run_LeadLeftWalk"))
				{
					Loco(a, "Run_LeadLeftWalk", 1f);
				}
				else
				{
					FaceMoving(a, pos, -m_off[a]);
					Run(a, m_vel[a].magnitude, "Run_Run", 0.7f);
				}
				return;
			}
		}
		Place(tr, pos, a);
		if (moving)
		{
			// Where he really goes (a curve, a step in to the ball) once he is going, else the leg's way.
			Vector2 dir = m_vel[a].sqrMagnitude > 1f ? m_vel[a] : active.P1 - active.P0;
			// Down for a ground ball he keeps facing it.
			if (!Scooping(a)) FaceMoving(a, pos, dir);
			if (m_t >= m_actionUntil[a])
			{
				float dur = Mathf.Max(0.05f, active.T1 - active.T0);
				float speed = (active.P1 - active.P0).magnitude / dur;
				Run(a, m_vel[a].magnitude, runner && speed > 8.6f && HasClip(a, "Run_FastRun") ? "Run_FastRun" : "Run_Run", Mathf.Clamp(speed / 6.5f, 0.6f, 1.6f));
			}
			return;
		}
		// Taking a throw: squared up to the man throwing it, from a moment before it leaves his hand until it is in the
		// glove, whatever he is doing (user, 2026-11-02: "받는 수비수도 던지는 수비수 쪽으로 몸을 돌려서 받고"; at the bag in
		// the glove-out clip he had stayed the way he came, as that clip owns him until the catch).
		// (Not once he is into a tag: that turned him to the runner, as before.)
		Vector2 thrower = Vector2.zero;
		bool taking = a < 9 && m_clip[a] != "Def_SliderTag" && m_tl.ThrowTo(a, m_t, out thrower);
		if (taking) Face(tr, thrower - pos);
		// Under a ball in the air he catches standing: squared to the way it comes in, which his glove was set for (ReachAir).
		bool squared = !taking && a < 9 && m_t >= m_airFaceFrom[a] && m_t < m_airFaceUntil[a];
		if (squared) Face(tr, m_airFace[a]);
		if (m_t < m_actionUntil[a]) return;
		if (!runner)
		{
			// With the ball and a throw to make: set to throw, looking at where it goes.
			if (m_holder == a && NextThrow(a, out Vector2 to) - m_t < 2.5f)
			{
				Face(tr, to - pos);
				Cycle(a, "M_Fielder_ThrowIdle", 1f, HasClip(a, "Def_ThrowStd") ? "Def_ThrowStd" : "Def_InfielderStd", 1f);
				return;
			}
			// Watching the ball (under a fly ball he is about to catch: calling for it).
			Vector3 b = m_ball != null ? m_ball.transform.position : Vector3.zero;
			if (!taking && !squared) Face(tr, new Vector2(b.x - tr.position.x, b.z - tr.position.z));
			bool calling = m_flyCatchAt[a] > m_t && m_flyCatchAt[a] - m_t < 2.5f;
			if (calling && HasClip(a, "Def_Callplay")) Loco(a, "Def_Callplay", 1f);
			else Cycle(a, a >= 5 && a <= 7 ? "M_Fielder_Idle_Outfielder" : ReadyClips[a % ReadyClips.Length], 1f, "Def_InfielderStd", 1f);
		}
		else
		{
			bool lead = active.Kind == Ma9MSeg.Stand && active.P0 != BaseOf(active.Arg);
			Cycle(a, lead ? "M_Runner_LeadIdle01" : "M_Runner_Idle", 1f, lead ? "Run_LeadStd" : "Run_BasicStd", 1f);
		}
	}

	// A clip that turns and steps on its own (a throw: Ma9MMotions track), started from where he stands and the way he
	// faces now: its track turned so that he faces `toward` square as the ball leaves his hand (user, 2026-11-02: "송구할
	// 때 그 받는 수비수 방향으로 몸이 돌아야하는데 … 몸은 앞을 보는데 옆으로 송구"; laid by the hand's way, the infield
	// throw left him 48° short of the target at the release and never nearer than 38° after it: a shortstop throwing to
	// first faced the plate, a third baseman nearly so; the outfield throws 26-39°). How far his facing is from the clip's
	// first frame is turned off over the windup, gone 0.12 s before the ball leaves: the turn through the release is the
	// clip's own, as fast as it is (scaling the whole turn instead slowed or sped the body through the release). A
	// left-hander's figure is the right-hander's mirrored, and so is his track.
	private void StartTrack(int a, Ma9MMotions.Info info, Vector2 toward, float speed)
	{
		Transform tr = m_tr[a];
		if (info.Track == null || tr == null || toward.sqrMagnitude < 1e-4f) return;
		bool mirror = tr.lossyScale.x < 0;
		info.TrackAt(0f, out _, out float y0);
		info.TrackAt(info.ReleaseT, out _, out float yr);
		if (mirror)
		{
			y0 = -y0;
			yr = -yr;
		}
		float target = Mathf.Atan2(toward.x, toward.y) * Mathf.Rad2Deg - yr;
		m_trk[a] = info;
		m_trkT0[a] = m_t;
		m_trkSpeed[a] = speed;
		m_trkMirror[a] = mirror;
		m_trkTarget[a] = target;
		m_trkFrom[a] = new Vector2(tr.position.x, tr.position.z);
		m_trkLeft[a] = Mathf.DeltaAngle(target + y0, tr.eulerAngles.y);
		m_trkUntil[a] = m_tl.NextMove(a, m_t);
	}

	// While a tracked clip plays: his spot and facing from its track. When it is over (or he has to move), he stays where
	// its steps took him until he next moves (then back onto the script over 0.4 s, as after a scoop).
	private bool Tracking(int a, Transform tr)
	{
		Ma9MMotions.Info info = m_trk[a];
		float ct = (m_t - m_trkT0[a]) * m_trkSpeed[a];
		if (ct >= info.Length || m_t >= m_trkUntil[a])
		{
			m_trk[a] = null;
			m_stand[a] = new Vector2(tr.position.x, tr.position.z);
			m_airReach[a] = false;
			m_lungeFrom[a] = m_t;
			m_lungeAt[a] = m_t;
			m_lungeUntil[a] = m_tl.NextMove(a, m_t);
			return false;
		}
		info.TrackAt(ct, out Vector2 at, out float y);
		if (m_trkMirror[a])
		{
			at.x = -at.x;
			y = -y;
		}
		float r = m_trkTarget[a] * Mathf.Deg2Rad;
		Vector2 off = new Vector2(at.x * Mathf.Cos(r) + at.y * Mathf.Sin(r), -at.x * Mathf.Sin(r) + at.y * Mathf.Cos(r));
		float settle = Mathf.Max(0.1f, info.ReleaseT - 0.12f);
		float yaw = m_trkTarget[a] + y + m_trkLeft[a] * (1f - Mathf.SmoothStep(0f, 1f, ct / settle));
		Place(tr, m_trkFrom[a] + off, a);
		tr.rotation = Quaternion.Euler(0f, yaw, 0f);
		return true;
	}

	// Fielder a's throwing hand (the right hand's bone; mirrored for a left-hander like the glove).
	private Transform Hand(int a)
	{
		if (a < 0 || a >= 9 || m_tr[a] == null) return null;
		if (m_hand[a] == null)
		{
			foreach (Transform t in m_tr[a].GetComponentsInChildren<Transform>(true))
			{
				if (t.name == "Bip01 R Hand") { m_hand[a] = t; break; }
			}
		}
		return m_hand[a];
	}

	private void Cycle(int a, string clip, float speed, string old, float oldSpeed)
	{
		if (Ma9MMotions.Ensure(m_anim[a], clip)) Loco(a, clip, speed);
		else Loco(a, old, oldSpeed);
	}

	// game's ran at a rate per leg; user, 2026-11-01: "주자 및 수비수의 움직임이 엄청 자연스럽다고 느껴지지는 않음"), the fast
	// one at a sprint, from one to the other in step. The game's own run (old, at oldSpeed) where the clips are missing.
	private void Run(int a, float v, string old, float oldSpeed)
	{
		// The last strides into a running catch: the glove up (its cycle 0.47 s for the run's stride).
		if (a < 9 && m_runCatchAt[a] > m_t && m_runCatchAt[a] - m_t < 0.5f)
		{
			string glove = "M_Fielder_RunningCatch_Before_" + m_runCatchDir[a];
			Ma9MMotions.Info gi = Ma9MMotions.Get(glove);
			if (gi != null && Ma9MMotions.Ensure(m_anim[a], glove))
			{
				Loco(a, glove, Mathf.Clamp(v / (RunStride / Mathf.Max(0.1f, gi.Length)), 0.45f, 1.8f));
				return;
			}
		}
		// figure he seemed to crawl along the grass with his head at his knees.
		string clip = a >= RunnerBase ? "M_Runner_Run" : "M_Fielder_Run";
		Ma9MMotions.Info info = Ma9MMotions.Get(clip);
		if (info == null || !Ma9MMotions.Ensure(m_anim[a], clip))
		{
			Loco(a, old, oldSpeed);
			return;
		}
		float natural = info.Speed > 1f ? info.Speed : RunStride / Mathf.Max(0.1f, info.Length);
		Loco(a, clip, Mathf.Clamp(v / natural, 0.45f, 1.8f));
	}

	// Dev (script shots): how far off the target he faces as the ball leaves his hand.
	private System.Collections.IEnumerator LogAim(int a, float release, Vector2 to)
	{
		yield return new WaitForSeconds(Mathf.Max(0f, release - 0.03f));
		if (m_tr[a] == null) yield break;
		// And where the throwing hand goes then (over the ground, at its fastest): the way the ball leaves.
		Transform hand = Hand(a);
		Vector3 h0 = hand != null ? hand.position : Vector3.zero;
		yield return new WaitForSeconds(0.06f);
		if (m_tr[a] == null) yield break;
		float off = Vector3.SignedAngle(new Vector3(to.x, 0, to.y), m_tr[a].forward, Vector3.up);
		Vector3 swing = hand != null ? hand.position - h0 : Vector3.zero;
		float arm = Vector3.SignedAngle(new Vector3(to.x, 0, to.y), new Vector3(swing.x, 0, swing.z), Vector3.up);
		Debug.Log($"[Ma9MScript] throw by {a} released facing {off:F0} deg off the target, the hand going {arm:F0} deg off it");
	}

	// Dev (script shots): how far off the man who threw it he faces as a throw goes into his glove.
	private System.Collections.IEnumerator LogTake(int a, float wait, Vector2 from)
	{
		yield return new WaitForSeconds(Mathf.Max(0f, wait));
		if (m_tr[a] == null) yield break;
		Vector3 to = new Vector3(from.x - m_tr[a].position.x, 0, from.y - m_tr[a].position.z);
		Debug.Log($"[Ma9MScript] throw taken by {a} facing {Vector3.SignedAngle(to, m_tr[a].forward, Vector3.up):F0} deg off the thrower");
	}

	// Live, a play ends a moment after its last runner stops and its last call (and a second after the batted ball is
	// fielded), not after the ball has gone back in to the infield (user, 2026-10-31: "전체적으로 중계 속도가 너무
	// 느려졌음 … 불필요한 장면"): on the user's games that is 0.7 s a play, 40 s a game. Highlights play it all.
	private static Ma9MPlayScript LiveCut(Ma9MPlayScript s)
	{
		float[] outAt = OutTimes(s, out bool batterCaught, out float firstCatch);
		float settle = 0f;
		foreach (Ma9MPiece p in s.Pieces)
		{
			int slot = p.Actor - RunnerBase;
			if (slot >= 0 && slot < 4 && (p.Kind == Ma9MSeg.Run || p.Kind == Ma9MSeg.Slide || p.Kind == Ma9MSeg.Out || p.Kind == Ma9MSeg.Safe || p.Kind == Ma9MSeg.Score || p.Kind == Ma9MSeg.Back))
			{
				// Once he is out, where he jogs off to does not hold the play up.
				if (outAt[slot] >= 0 && p.Kind != Ma9MSeg.Out && p.T0 >= outAt[slot] - 0.01f) continue;
				if (slot == 0 && batterCaught) continue;
				settle = Mathf.Max(settle, p.T1);
			}
			if (p.Kind == Ma9MSeg.Tag) settle = Mathf.Max(settle, p.T0 + 0.6f);
		}
		if (firstCatch < float.MaxValue) settle = Mathf.Max(settle, firstCatch + 1.0f);
		// The third out ends it at once (user, 2026-10-31: "2아웃에 플라이나 땅볼을 잡고 바로 이닝 교체 되는게 아니라
		// 한참동안 카메라가 비추다가 넘어가는데").
		float third = ThirdOutAt(outAt);
		if (third >= 0) settle = Mathf.Min(settle, third + 0.8f);
		float end = Mathf.Min(s.End, settle + 0.3f);
		if (settle <= 0f || end >= s.End - 0.05f) return s;
		Ma9MPlayScript w = new Ma9MPlayScript { Ev = s.Ev, La = s.La, H = s.H, End = end };
		w.Pieces.AddRange(s.Pieces);
		return w;
	}

	// Who is out when (slot 0 the batter, 1-3 the runners; -1 not): a runner at his call, the batter at the catch when he
	// is caught (he peels off).
	private static float[] OutTimes(Ma9MPlayScript s, out bool batterCaught, out float firstCatch)
	{
		firstCatch = float.MaxValue;
		float[] outAt = { -1f, -1f, -1f, -1f };
		batterCaught = false;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Catch && p.Style != 9 && p.Actor < 9) firstCatch = Mathf.Min(firstCatch, p.T0);
			if (p.Kind == Ma9MSeg.Peel && p.Actor == RunnerBase) batterCaught = true;
			int slot = p.Actor - RunnerBase;
			if (p.Kind == Ma9MSeg.Out && slot >= 0 && slot < 4 && (outAt[slot] < 0 || p.T0 < outAt[slot])) outAt[slot] = p.T0;
		}
		if (batterCaught && outAt[0] < 0 && firstCatch < float.MaxValue) outAt[0] = firstCatch;
		return outAt;
	}

	// The out that ends the inning in this play (the outs before it from DataCenter), or -1.
	private static float ThirdOutAt(float[] outAt)
	{
		List<float> outs = new List<float>();
		foreach (float t in outAt) if (t >= 0) outs.Add(t);
		outs.Sort();
		int need = 3 - DataCenter.instance.GetOutcount();
		return need >= 1 && outs.Count >= need ? outs[need - 1] : -1f;
	}

	// Dugouts in the field's frame (x up the first-base line, z up the third), off the field on each side: the home
	// team's on the first-base side, the visitors' on the third.
	private static readonly Vector2 FirstDugout = new Vector2(13f, -20f), ThirdDugout = new Vector2(-20f, 13f);

	private static Vector2 DugoutOf(bool batting) => (DataCenter.instance.GetAtkTeam() == eTeam.T_HOME) == batting ? FirstDugout : ThirdDugout;

	// Actor a walks off to his dugout from time at (a fielder to the fielding side's, a runner to the batting side's).
	private void Leave(int a, float at)
	{
		if (m_leaveAt[a] >= 0 && m_leaveAt[a] <= at) return;
		m_leaveAt[a] = at;
		m_leaveTo[a] = DugoutOf(a >= RunnerBase);
		if (a < 9) EnsureWalk(a);
	}

	// The inning is over: everybody still on the field walks off, the fielders a beat apart, a runner still going once he
	// has got where he was going (a second at most).
	private void WalkOff()
	{
		m_walkingOff = true;
		for (int a = 0; a < Actors; a++)
		{
			if (m_tr[a] == null) continue;
			Leave(a, a < 9 ? m_t + 0.08f * (a % 4) : Mathf.Min(m_t + 1f, Mathf.Max(m_t, LastMove(a) + 0.1f)));
		}
	}

	// The fielders' clips have no walk: the runners' (the same figure and bones) is lent to them.
	private void EnsureWalk(int a)
	{
		Animation an = m_anim[a];
		if (an == null || an["Run_Walk"] != null) return;
		if (s_walkClip == null && m_pm != null && m_pm.GetBatterManager() != null)
		{
			for (int i = 0; i < 4 && s_walkClip == null; i++)
			{
				Runner r = m_pm.GetBatterManager().GetRunner(i);
				Animation ra = r != null ? r.Ma9MAnimation : null;
				if (ra != null && ra["Run_Walk"] != null) s_walkClip = ra["Run_Walk"].clip;
			}
		}
		if (s_walkClip != null) an.AddClip(s_walkClip, "Run_Walk");
	}

	// A man on the move looks where he goes, unless he is about to throw or throwing: then at the target (user,
	// 2026-10-31: the 2루수 of a 병살 threw to first facing the way he had come to the bag, the ball going to first).
	private void FaceMoving(int a, Vector2 pos, Vector2 dir)
	{
		if (m_t < m_turnUntil[a]) return;
		if (a < 9 && m_holder == a && NextThrow(a, out Vector2 to) - m_t < 0.6f)
		{
			Face(m_tr[a], to - pos);
			return;
		}
		// Taking a throw: squared up to the thrower as he gets to the bag (user, 2026-10-31: the first baseman took
		// a throw from third looking at the batter-runner).
		if (a < 9 && m_tl.ThrowTo(a, m_t, out Vector2 from))
		{
			Face(m_tr[a], from - pos);
			return;
		}
		Face(m_tr[a], dir);
	}

	// Fielder a's glove hand (the left hand's bone; a left-handed thrower's figure is mirrored).
	private Transform Glove(int a)
	{
		if (a < 0 || a >= 9 || m_tr[a] == null) return null;
		if (m_glove[a] == null)
		{
			foreach (Transform t in m_tr[a].GetComponentsInChildren<Transform>(true))
			{
				if (t.name == "Bip01 L Hand") { m_glove[a] = t; break; }
			}
		}
		return m_glove[a];
	}

	// When fielder a next throws (and where to), or +inf.
	private float NextThrow(int a, out Vector2 to)
	{
		to = Vector2.zero;
		foreach (Ma9MPiece p in m_tl.PiecesOf(a))
		{
			if (p.Kind == Ma9MSeg.Throw && p.T0 >= m_t)
			{
				to = p.P1;
				return p.T0;
			}
		}
		return float.MaxValue;
	}

	// Dev (top cam): when fielder a throws next, from a moment ago (the throw just made) on, and where to.
	private float NextThrowAny(int a, out Vector2 to)
	{
		to = Vector2.zero;
		foreach (Ma9MPiece p in m_tl.PiecesOf(a))
		{
			if (p.Kind == Ma9MSeg.Throw && p.T0 >= m_t - 0.6f)
			{
				to = p.P1;
				return p.T0;
			}
		}
		return float.MaxValue;
	}

	// When fielder a next moves (a run, a cover) after now, or +inf.
	private float NextMove(int a)
	{
		float next = float.MaxValue;
		foreach (Ma9MPiece p in m_tl.PiecesOf(a))
		{
			if (Ma9MScriptTimeline.Moves(p.Kind) && p.Kind != Ma9MSeg.Stand && p.Kind != Ma9MSeg.Wait && p.T0 > m_t + 0.05f) next = Mathf.Min(next, p.T0);
		}
		return next;
	}

	private float LastMove(int a)
	{
		float end = 0;
		foreach (Ma9MPiece p in m_tl.PiecesOf(a))
		{
			if (Ma9MScriptTimeline.Moves(p.Kind) && p.Kind != Ma9MSeg.Stand && p.Kind != Ma9MSeg.Wait) end = Mathf.Max(end, p.T1);
		}
		return end;
	}

	private static Vector2 BaseOf(int b)
	{
		switch (b % 4)
		{
			case 1: return new Vector2(27.43f, 0);
			case 2: return new Vector2(27.43f, 27.43f);
			case 3: return new Vector2(0, 27.43f);
			default: return Vector2.zero;
		}
	}

	private void Place(Transform tr, Vector2 p, int a)
	{
		float y = 0;
		if (a == 8)
		{
			// The pitcher stands on the mound's slope (Pitcher.Update does this when it runs).
			float d = Vector2.Distance(p, new Vector2(13.07f, 13.07f));
			y = 0.415f * (1f - Mathf.Min(1f, d / 5f));
		}
		tr.position = new Vector3(p.x, y, p.y);
	}

	// Turning to face dir at his pace, whatever the frame rate (it was a third of the way each frame: quick turns on a
	// fast screen, lazy ones on a slow phone).
	private static void Face(Transform tr, Vector2 dir)
	{
		if (dir.sqrMagnitude < 1e-4f) return;
		Quaternion q = Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y));
		tr.rotation = Quaternion.Slerp(tr.rotation, q, 1f - Mathf.Exp(-TurnRate * Time.deltaTime));
	}

	private const float TurnRate = 20f;

	private static void FaceNow(Transform tr, Vector2 dir)
	{
		if (dir.sqrMagnitude < 1e-4f) return;
		tr.rotation = Quaternion.LookRotation(new Vector3(dir.x, 0, dir.y));
	}

	private bool HasClip(int a, string clip) => m_anim[a] != null && m_anim[a][clip] != null;

	// Looping movement or stance clip (only switched when it changes).
	private void Loco(int a, string clip, float speed)
	{
		Animation an = m_anim[a];
		if (an == null) return;
		if (an[clip] == null)
		{
			clip = a >= RunnerBase ? "Run_BasicStd" : "Def_InfielderStd";
			if (an[clip] == null) return;
		}
		an[clip].speed = speed;
		if (m_clip[a] == clip) return;
		an[clip].wrapMode = WrapMode.Loop;
		float fade = 0.15f;
		if (m_outFade[a] > 0)
		{
			fade = m_outFade[a];
			m_outFade[a] = 0;
		}
		an.CrossFade(clip, fade);
		m_clip[a] = clip;
	}

	// A one-shot clip that owns the actor for its length (or `hold` seconds).
	// Down to a ground ball: he steps in to where his glove takes it, the scoop's glove-down frame held until it is
	// there, then up with it (the rest of the clip, for hold after the catch).
	private void Scoop(int a, Ma9MPiece p, string clip, float hold, float ahead = ScoopForward, float glove = ScoopSide)
	{
		float lead = Mathf.Max(0.05f, p.T0 - m_t);
		Vector3 b = m_tl.BallAt(p.T0 - 0.01f, out _, out _), before = m_tl.BallAt(p.T0 - 0.25f, out _, out _);
		Vector2 ball = new Vector2(b.x, b.z);
		// Square to a ball still rolling in (it comes into the glove from in front); a ball all but stopped, from
		// where he comes.
		Vector2 dir = new Vector2(before.x - b.x, before.z - b.z);
		if (dir.magnitude < 0.25f) dir = ball - new Vector2(m_tr[a].position.x, m_tr[a].position.z);
		if (dir.sqrMagnitude < 0.0025f) dir = new Vector2(m_tr[a].forward.x, m_tr[a].forward.z);
		dir.Normalize();
		Vector2 right = new Vector2(dir.y, -dir.x);
		// The glove hand is his left; a mirrored figure (a left-hander) wears it on the right.
		float side = m_tr[a].lossyScale.x < 0 ? -glove : glove;
		Vector2 me = ScriptPos(a, p.T0);
		m_stand[a] = me + Vector2.ClampMagnitude(ball - dir * ahead + right * side - me, ahead > 0.5f ? LungeLong : LungeMax);
		m_airReach[a] = false;
		m_lungeFrom[a] = m_t;
		m_lungeAt[a] = p.T0;
		m_lungeUntil[a] = m_tl.NextMove(a, p.T0);
		FaceNow(m_tr[a], dir);
		Action(a, clip, lead + hold, 0f, lead);
		m_scoopAt[a] = p.T0;
		m_upSpeed[a] = 1f;
		if (s_shotDir != null) Debug.Log($"[Ma9MScript] scoop set by {a} t {m_t:F2} for {p.T0:F2}: script pos {me} ball {ball} stand {m_stand[a]} facing {dir} side {side} clip {clip}");
	}

	// A diving catch: the ball goes into the glove he has out in front, then he lands with it (user, 2026-11-01: "다이빙
	// 캐치해서 플라이볼 잡을 때 공이 땅에 떨어지고 슬라이딩해서 잡아낸단 말이야 … 사람이 더 일찍 다이빙 한 다음 글러브 안으로
	// 공이 들어가게끔"). The clip was timed to land as the ball came, sooner still with a throw to follow, so the glove met
	// the ball on the grass. Now the clip is at its catch frame when the ball gets there (only then does it speed up for
	// a throw), and he flies to where that frame's glove is on the ball, there until he next moves, like a scoop.
	private void LayOut(int a, Ma9MPiece p, string clip, float len, float throwAt)
	{
		bool air = p.Style == 2;
		float at = air ? DiveAirAt : DiveGroundAt, ahead = air ? DiveAirAhead : DiveGroundAhead;
		Vector3 b = m_tl.BallAt(p.T1 - 0.01f, out _, out _);
		Vector2 ball = new Vector2(b.x, b.z);
		Vector2 dir = ball - p.P0;
		if (dir.magnitude < 0.25f) dir = p.P1 - p.P0;
		if (dir.sqrMagnitude < 0.0025f) dir = new Vector2(m_tr[a].forward.x, m_tr[a].forward.z);
		dir.Normalize();
		Vector2 right = new Vector2(dir.y, -dir.x);
		float side = (air ? DiveAirSide : DiveGroundSide) * (m_tr[a].lossyScale.x < 0 ? -1f : 1f);
		Vector2 body = ball - dir * ahead + right * side;
		// Not back from where he leaves his feet (a ball on him at once): the glove reaches past it and takes it in.
		float back = Vector2.Dot(body - p.P0, dir);
		if (back < 0) body -= dir * back;
		Vector2 me = ScriptPos(a, p.T1);
		m_stand[a] = me + Vector2.ClampMagnitude(body - me, LungeMax);
		m_airReach[a] = false;
		m_lungeFrom[a] = m_t;
		m_lungeAt[a] = p.T1;
		m_lungeUntil[a] = m_tl.NextMove(a, p.T1);
		FaceNow(m_tr[a], dir);
		float fly = Mathf.Max(0.05f, p.T1 - m_t);
		float speed = Mathf.Clamp(at / fly, air ? DiveAirMinSpeed : 0.5f, 2f), up = speed;
		if (throwAt < float.MaxValue) up = Mathf.Clamp((len - at) / Mathf.Max(0.3f, throwAt - 0.3f - p.T1), speed, 1.8f);
		// Into the dive so its catch frame comes as the ball does: now, or (a slow clip) a beat later, running till then.
		float leave = p.T1 - at / speed;
		if (leave > m_t + 0.02f) Plan(a, leave, clip, at / speed + (len - at) / up, 0f, speed, 0.08f);
		else Action(a, clip, fly + (len - at) / up, speed);
		m_scoopAt[a] = p.T1;
		m_upSpeed[a] = up;
		m_diveSyncT1[a] = p.T1;
		m_diveSyncAt[a] = at;
		m_diveSyncClip[a] = clip;
		if (s_shotDir != null) Debug.Log($"[Ma9MScript] dive set by {a} t {m_t:F2} for {p.T1:F2}: script land {me} ball {b} body {m_stand[a]} speed {speed:F2} up {up:F2}");
	}

	// his glove side sets where he stands for it), else the game's own.
	private void Grounder(int a, Ma9MPiece p, string old, float hold)
	{
		string clip = a >= 5 && a <= 7 ? "M_Fielder_GroundBallCatch_Outfielder_F" : "M_Fielder_GroundBallCatch_Infielder_F";
		Ma9MMotions.Info info = Ma9MMotions.Get(clip);
		if (info != null && info.GloveStart.z > 0.1f && Ma9MMotions.Ensure(m_anim[a], clip)) Scoop(a, p, clip, Mathf.Max(hold, info.Length), info.GloveStart.z, -info.GloveStart.x);
		else Scoop(a, p, old, hold);
	}

	// stands at his spot, at most its 0.83 s, ending at the catch), then the catch with the glove brought down to his
	// chest (FlyBallCatch). A liner (4): the catch alone, high, or at his chest (Fielder_Catch) for a ball under a metre.
	private bool AirCatch(int a, Ma9MPiece p)
	{
		float y = m_tl.BallAt(p.T0 - 0.01f, out _, out _).y;
		string clip = p.Style == 4 && y < 1f ? "M_Fielder_Catch" : "M_Fielder_FlyBallCatch";
		Ma9MMotions.Info ci = Ma9MMotions.Get(clip);
		if (ci == null || !Ma9MMotions.Ensure(m_anim[a], clip)) return false;
		m_plan[a].Clear();
		if (p.Style == 0) Before(a, "M_Fielder_FlyBallCatch_Before", p.T0, 0.2f);
		Plan(a, p.T0 - 0.03f, clip, Mathf.Max(0.5f, ci.Length), 0f, 1f, 0.08f);
		return true;
	}

	// A running catch (style 1): his last strides with the glove up (Run), the catch on the run (RunningCatch_F; running
	// away from the plate, over the shoulder on the side the ball comes down: _BL / _BR).
	private bool RunCatch(int a, Ma9MPiece p)
	{
		// The side his glove was set for (ReachAir), else from how he runs now.
		Vector3 b = m_tl.BallAt(p.T0 - 0.01f, out _, out _);
		string side = m_airSide[a] ?? RunCatchSide(m_vel[a], new Vector2(m_tr[a].position.x, m_tr[a].position.z), new Vector2(b.x, b.z));
		string clip = "M_Fielder_RunningCatch_" + side;
		Ma9MMotions.Info ci = Ma9MMotions.Get(clip);
		if (ci == null || !Ma9MMotions.Ensure(m_anim[a], clip)) return false;
		m_plan[a].Clear();
		m_runCatchAt[a] = p.T0;
		m_runCatchDir[a] = side;
		Plan(a, p.T0 - 0.03f, clip, Mathf.Max(0.4f, ci.Length), 0f, 1f, 0.08f);
		return true;
	}

	// Taking a throw (style 9): the glove out for it while he stands at the bag (Fielder_Catch_Before), then the catch
	// (Fielder_Catch).
	private bool TakeThrow(int a, Ma9MPiece p)
	{
		Ma9MMotions.Info ci = Ma9MMotions.Get("M_Fielder_Catch");
		if (ci == null || !Ma9MMotions.Ensure(m_anim[a], ci.Name)) return false;
		m_plan[a].Clear();
		Before(a, "M_Fielder_Catch_Before", p.T0, 0.15f);
		Plan(a, p.T0 - 0.03f, ci.Name, Mathf.Max(0.35f, ci.Length), 0f, 1f, 0.06f);
		return true;
	}

	// bag over the script's, and up on the bag a third of a second after he gets there (if he is still there then: an
	// out walks off).
	private bool Slide(int a, Ma9MPiece p)
	{
		string kind = p.Style == 1 ? "M_Runner_HeadSliding" : "M_Runner_Sliding";
		Ma9MMotions.Info drop = Ma9MMotions.Get(kind + "_Before"), slide = Ma9MMotions.Get(kind), up = Ma9MMotions.Get(kind + "_After");
		if (drop == null || slide == null || !Ma9MMotions.Ensure(m_anim[a], drop.Name) || !Ma9MMotions.Ensure(m_anim[a], slide.Name)) return false;
		const float into = 0.22f;
		m_plan[a].Clear();
		Action(a, drop.Name, into, drop.Length / into, 0.08f);
		float rest = Mathf.Max(0.3f, p.T1 - m_t - into) + 0.35f;
		float speed = Mathf.Clamp(slide.Length / rest, 0.6f, 1.5f);
		Plan(a, m_t + into, slide.Name, rest, 0f, speed, 0.06f);
		if (up != null && Ma9MMotions.Ensure(m_anim[a], up.Name)) Plan(a, m_t + into + rest, up.Name, up.Length, 0f, 1f, 0.15f);
		ReachBag(a, p, slide.Name, Mathf.Min(slide.Length, (p.T1 - m_t - into) * speed), p.Style == 1);
		return true;
	}

	// The slide ends with what leads (his front foot; head first, his front hand) on the bag, not his hips on it (user,
	// 2026-11-02: "벤트레그 슬라이딩 할 때 발바닥이 루를 밟는게 아니라 살짝 옆 쪽을 밟으면서 슬라이딩"; "주자가 슬라이딩 하는
	// 부분이 루 바깥쪽을 지나가는 듯"): the clip at the moment he gets there says where that foot is from him, and he is
	// moved over the slide by what it takes, there until he next moves (as a scoop). The clip is sampled here, in Update;
	// the animation overwrites the pose before it is drawn.
	private void ReachBag(int a, Ma9MPiece p, string clip, float clipTime, bool head)
	{
		Transform tr = m_tr[a];
		Animation an = m_anim[a];
		Vector2 dir = p.P1 - p.P0;
		if (tr == null || an == null || an[clip] == null || dir.sqrMagnitude < 0.25f) return;
		dir.Normalize();
		an[clip].clip.SampleAnimation(tr.gameObject, Mathf.Max(0f, clipTime));
		Vector3 lead = Vector3.zero;
		float ahead = float.MinValue;
		foreach (string limb in head ? new[] { "Bip01 L Hand", "Bip01 R Hand" } : new[] { "Bip01 L Toe0", "Bip01 R Toe0" })
		{
			Transform b = Bone(a, limb);
			if (b == null) continue;
			Vector3 off = b.position - tr.position;
			float f = Vector3.Dot(off, tr.forward);
			if (f > ahead) { ahead = f; lead = off; }
		}
		if (ahead == float.MinValue) return;
		// That offset as he will face at the end (along the slide), and the bag he goes for (its near half).
		float side = Vector3.Dot(lead, tr.right);
		Vector2 right = new Vector2(dir.y, -dir.x);
		Vector2 reach = dir * ahead + right * side;
		Vector2 bag = BaseOf(p.Arg) - dir * (head ? 0.12f : 0.05f);
		m_slideShift[a] = Vector2.ClampMagnitude(bag - (p.P1 + reach), 2.5f);
		m_slideFrom[a] = m_t;
		m_slideAt[a] = p.T1;
		m_slideUntil[a] = m_tl.NextMove(a, p.T1);
		if (s_shotDir != null) Debug.Log($"[Ma9MScript] slide by {a} to base {p.Arg}: {(head ? "hand" : "foot")} {ahead:F2} ahead {side:F2} aside at clip {clipTime:F2}; moved {m_slideShift[a]}");
	}

	// The slide's move at this moment (ReachBag): in by the time he gets there, there until he next moves, then gone over 0.4 s.
	private Vector2 SlideShift(int a)
	{
		if (m_slideAt[a] < 0) return Vector2.zero;
		float k;
		if (m_t < m_slideAt[a]) k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(m_slideFrom[a], m_slideAt[a], m_t));
		else if (m_t < m_slideUntil[a]) k = 1f;
		else k = 1f - Mathf.SmoothStep(0f, 1f, (m_t - m_slideUntil[a]) / 0.4f);
		return m_slideShift[a] * k;
	}

	private Transform Bone(int a, string name)
	{
		foreach (Transform t in m_tr[a].GetComponentsInChildren<Transform>(true))
		{
			if (t.name == name) return t;
		}
		return null;
	}

	// Rounding a base on the way to the next (user, 2026-11-01: "주자 … 움직임이 … 자연스럽다고 느껴지지는 않음"): out wide
	// before the bag and in across it, not a corner turned on the spot — the last 8 m of the leg bowed out (away from the
	// way he turns) by up to 1.2 m for a right-angle turn, back on the line at the bag.
	private Vector2 Rounding(int a, Ma9MPiece leg, Vector2 pos)
	{
		float rem = (leg.P1 - pos).magnitude;
		if (rem > 8f || rem < 0.05f) return Vector2.zero;
		foreach (Ma9MPiece q in m_tl.PiecesOf(a))
		{
			if (q.Kind != Ma9MSeg.Run || Mathf.Abs(q.T0 - leg.T1) > 0.05f || (q.P0 - leg.P1).sqrMagnitude > 0.25f) continue;
			Vector2 d1 = (leg.P1 - leg.P0).normalized, d2 = (q.P1 - q.P0).normalized;
			float turn = Vector2.SignedAngle(d1, d2);
			if (Mathf.Abs(turn) < 20f) return Vector2.zero;
			// Out: to his right on a turn to the left (and the other way).
			Vector2 outward = turn > 0f ? new Vector2(d1.y, -d1.x) : new Vector2(-d1.y, d1.x);
			return outward * (1.2f * Mathf.Min(1f, Mathf.Abs(turn) / 90f) * Mathf.Sin(Mathf.PI * (8f - rem) / 8f));
		}
		return Vector2.zero;
	}

	// A lead-in clip from when he stands (no more running before the catch at `at`), its end at the catch: only its last
	// part when there is less time than it lasts, none under 0.2 s.
	private void Before(int a, string clip, float at, float fade)
	{
		Ma9MMotions.Info bi = Ma9MMotions.Get(clip);
		float stands = Mathf.Max(m_t, StandsFrom(a, at)), left = at - stands;
		if (bi == null || left < 0.2f || !Ma9MMotions.Ensure(m_anim[a], clip)) return;
		Plan(a, stands, clip, left, Mathf.Max(0f, bi.Length - left), 1f, fade);
	}

	private void Plan(int a, float at, string clip, float hold, float skip, float speed, float fade)
	{
		m_plan[a].Add(new Planned { At = at, Clip = clip, Hold = hold, Skip = skip, Speed = speed, Fade = fade });
		m_plan[a].Sort((x, y) => x.At.CompareTo(y.At));
	}

	// When actor a stops running before time t (his last run, move or dive up to then ends); t if he is on the move then.
	private float StandsFrom(int a, float t)
	{
		float stands = 0f;
		foreach (Ma9MPiece q in m_tl.PiecesOf(a))
		{
			bool goes = q.Kind == Ma9MSeg.Move || q.Kind == Ma9MSeg.Run || q.Kind == Ma9MSeg.Back || q.Kind == Ma9MSeg.Dive || q.Kind == Ma9MSeg.Slide;
			if (!goes || q.T0 >= t || (q.P1 - q.P0).sqrMagnitude < 0.01f) continue;
			if (q.T1 >= t - 0.01f) return t;
			stands = Mathf.Max(stands, q.T1);
		}
		return stands;
	}

	// Where the script has actor a at t, with what is left then of the start's difference.
	private Vector2 ScriptPos(int a, float t)
	{
		Vector2 pos = m_tl.PosAt(a, t, m_start[a], out _, out _);
		if (m_offT0[a] >= 0 && t >= m_offT0[a]) pos += m_off[a] * (1f - Mathf.Clamp01((t - m_offT0[a]) / m_offDur[a]));
		return pos;
	}

	// The scoop's step at this moment, from where the script has him (scripted): in by the catch, there until he next
	// moves, then gone over 0.4 s of that.
	private Vector2 Lunge(int a, Vector2 scripted)
	{
		if (m_lungeAt[a] < 0) return Vector2.zero;
		float k;
		if (m_t < m_lungeAt[a]) k = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(m_lungeFrom[a], m_lungeAt[a], m_t));
		else if (m_t < m_lungeUntil[a]) k = 1f;
		else k = 1f - Mathf.SmoothStep(0f, 1f, (m_t - m_lungeUntil[a]) / 0.4f);
		return (m_stand[a] - scripted) * k;
	}

	private bool Scooping(int a) => m_lungeAt[a] >= 0 && !m_airReach[a] && m_t >= m_lungeFrom[a] && m_t < m_lungeAt[a] + 0.3f;

	private void Action(int a, string clip, float hold = -1f, float speed = 1f, float fade = 0.08f)
	{
		Animation an = m_anim[a];
		if (an == null || an[clip] == null) return;
		an[clip].wrapMode = WrapMode.ClampForever;
		an[clip].speed = speed;
		an[clip].time = 0;
		an.CrossFade(clip, fade);
		m_clip[a] = clip;
		m_outFade[a] = 0;
		m_actionUntil[a] = m_t + (hold > 0 ? hold : an[clip].length / Mathf.Max(0.1f, speed));
	}

	private void Fire(Ev e)
	{
		Ma9MPiece p = e.P;
		int a = p.Actor;
		// Walking off: what the script still had for him (a throw back in, a slide) is past.
		bool move = e.Kind == EvKind.Catch || e.Kind == EvKind.Dive || e.Kind == EvKind.Throw || e.Kind == EvKind.Tag || e.Kind == EvKind.Slide;
		if (move && a >= 0 && a < Actors && m_leaving[a]) return;
		switch (e.Kind)
		{
			case EvKind.Catch:
				if (a >= 9 || m_tr[a] == null) return;
				if (s_shotDir != null && Camera.main != null && m_pm.GetCameraManager() != null)
				{
					Debug.Log($"[Ma9MScript] catch st{p.Style} by {a} at {p.P1} t {p.T0:F2}: cam {m_pm.GetCameraManager().GetState()} pos {Camera.main.transform.position} fov {Camera.main.fieldOfView:F1}");
				}
				// he faces the ball as he goes and watches it, not turned at once here.
				bool planned = p.Style == 0 || p.Style == 4 ? AirCatch(a, p) : p.Style == 1 ? RunCatch(a, p) : p.Style == 9 && TakeThrow(a, p);
				if (s_shotDir != null && p.Style == 9 && m_tl.ThrowTo(a, p.T0 - 0.01f, out Vector2 thrownFrom)) StartCoroutine(LogTake(a, p.T0 - m_t, thrownFrom));
				if (planned)
				{
					// A liner is on him at once: squared to it as it comes (the way his glove was set for: ReachAir).
					if (p.Style == 4) FaceNow(m_tr[a], m_airFaceUntil[a] > m_t ? m_airFace[a] : -p.P1);
				}
				else if (p.Style == 2 || p.Style == 6 || p.Style == 10)
				{
					// A dive: he lies the way he dove and gets up from there (user, 2026-10-31: "다이빙 캐치 하는 방향이랑
					// 잡고나서 누워있는 방향이 다르더라"; he was turned to face the plate as the ball went in).
				}
				else if (p.Style == 5 || p.Style == 7 || p.Style == 8 || p.Style == 9)
				{
					// Grounders and throws: square up to the ball coming in.
					Vector3 b = m_ball != null ? m_ball.transform.position : Vector3.zero;
					FaceNow(m_tr[a], new Vector2(b.x - m_tr[a].position.x, b.z - m_tr[a].position.z));
				}
				else
				{
					FaceNow(m_tr[a], -p.P1);
				}
				switch (p.Style)
				{
					case 0: case 1: case 4: if (!planned) Action(a, "Def_FlyBallCatchSuc", 0.75f); break;
					case 3: Action(a, "Def_JumpCatchSuc"); break;
					case 5: case 7: Grounder(a, p, p.P1.magnitude < 50 ? "Def_InfldGrounderCatchSuc" : "Def_GrounderCatchSuc", 0.3f); break;
					case 8: Grounder(a, p, "Def_GrounderCatchSuc", Mathf.Max(0.25f, p.T1 - p.T0)); break;
					case 9: if (!planned) Action(a, "Def_ThrowCatch", 0.6f); break;
					case 11:
					{
						// A bobble: a ball dropped out of the air (a fly error) or a grounder.
						m_tl.BallAt(p.T0 - 0.01f, out _, out Ma9MSeg was);
						Action(a, was == Ma9MSeg.BallFly && HasClip(a, "Def_FlyBallCatchErr") ? "Def_FlyBallCatchErr" : "Def_GrounderCatchErr");
						break;
					}
				}
				break;
			case EvKind.Dive:
			{
				if (a >= 9 || m_tr[a] == null) return;
				// The clip flies (to 0.3 s), lies (to 0.8) and gets up (1.0); timed so its flight is the script's,
				// and so he is up before a throw that follows (the throw then turns him to it).
				string clip = p.Style == 10 ? "Def_DivingCatchUpErr" : "Def_DivingCatchUpIn";
				float len = m_anim[a] != null && m_anim[a][clip] != null ? m_anim[a][clip].length : 1f;
				float throwAt = NextThrow(a, out _);
				if (p.Style == 2 || p.Style == 6)
				{
					LayOut(a, p, clip, len, throwAt);
					break;
				}
				FaceNow(m_tr[a], p.P1 - p.P0);
				float speed = Mathf.Clamp(0.27f / Mathf.Max(0.05f, p.T1 - p.T0), 0.6f, 1.4f);
				if (throwAt < float.MaxValue)
				{
					float room = throwAt - 0.3f - m_t;
					speed = Mathf.Clamp(len / Mathf.Max(0.4f, room), speed, 1.8f);
				}
				Action(a, clip, len / speed, speed);
				break;
			}
			case EvKind.Throw:
			{
				if (a >= 9 || m_tr[a] == null) return;
				// Into the throw turning to the target, not at once (user, 2026-10-31: "송구 동작이 너무 구림. 매끄럽지
				// 않음"). The infield throw and the flip play out their follow-through unless he has to move on;
				// the outfield throw's tail is a lunge flat to the ground, so it eases into the stance from a
				// moment after the ball goes, still moving (it was held still and faded, which looked stiff).
				string clip = ThrowClip(p, out float release);
				Ma9MMotions.Info info = clip.StartsWith("M_") ? Ma9MMotions.Get(clip) : null;
				if (info != null && Ma9MMotions.Ensure(m_anim[a], clip))
				{
					// steps along its track, so the ball goes at the target as it leaves his hand (StartTrack).
					float speed = Mathf.Clamp(release / Mathf.Max(0.12f, p.T0 - m_t), 1f, 2f);
					m_turnUntil[a] = 0f;
					StartTrack(a, info, p.P1 - p.P0, speed);
					Action(a, clip, Mathf.Min(info.Length / speed, Mathf.Max(release / speed + 0.35f, NextMove(a) - m_t)), speed, 0.12f);
					m_outFade[a] = 0.25f;
					if (s_shotDir != null) StartCoroutine(LogAim(a, release / speed, p.P1 - p.P0));
					break;
				}
				if (clip.StartsWith("M_"))
				{
					clip = p.Actor >= 5 && p.Actor <= 7 ? "Def_OutfielderThrow" : (p.P1 - p.P0).magnitude < 8f ? "Def_Toss" : "Def_InfielderThrow";
					release = clip == "Def_OutfielderThrow" ? 0.5f : clip == "Def_Toss" ? 0.2f : 0.3f;
				}
				// The target owns his facing until the ball is gone (a move under him does not turn him away).
				m_turnTo[a] = p.P1 - p.P0;
				m_turnUntil[a] = m_t + release + 0.25f;
				if (s_shotDir != null) StartCoroutine(LogAim(a, release, p.P1 - p.P0));
				if (!HasClip(a, clip)) clip = "Def_OutfielderThrow";
				bool lunge = clip == "Def_OutfielderThrow";
				float len = m_anim[a][clip].length;
				float hold = lunge ? release + 0.15f : Mathf.Min(len - 0.12f, Mathf.Max(release + 0.3f, NextMove(a) - m_t));
				Action(a, clip, hold, 1f, 0.2f);
				if (lunge) m_outFade[a] = 0.4f;
				break;
			}
			case EvKind.Tag:
				if (a >= 9 || m_tr[a] == null) return;
				if (p.Style != 1)
				{
					// A force: the base is his with the ball in his glove, no tag (user, 2026-10-31: "1루수는 태그 하지
					// 않아도 됨. 루만 밟으면 타자가 죽으니까"). He stays as he caught it.
					break;
				}
				if (p.Style == 1 && p.Arg < 4 && m_tr[RunnerBase + p.Arg] != null)
				{
					Vector3 r = m_tr[RunnerBase + p.Arg].position;
					FaceNow(m_tr[a], new Vector2(r.x - m_tr[a].position.x, r.z - m_tr[a].position.z));
				}
				Action(a, p.Style == 1 ? "Def_SliderTag" : "Def_StdTag", p.Style == 1 ? 0.95f : 0.6f, p.Style == 1 ? 1f : 1.5f);
				break;
			case EvKind.Slide:
				if (!Slide(a, p)) Action(a, p.Style == 1 && HasClip(a, "Run_HFSliding") ? "Run_HFSliding" : "Run_Sliding", 1.8f);
				break;
			case EvKind.Out:
			{
				if (!m_replay) Call(true, p.Arg);
				int ra = a;
				if (ra >= RunnerBase && m_tr[ra] != null)
				{
					// Off the field once his own moves are done (a batter runs through first first).
					Leave(ra, Mathf.Max(m_t + 0.9f, LastMove(ra) + 0.2f));
				}
				break;
			}
			case EvKind.CatchOut:
				if (!m_replay) Call(true, 255);
				break;
			case EvKind.Safe:
				if (!m_replay) Call(false, p.Arg);
				break;
			case EvKind.Score:
			{
				if (a >= RunnerBase && m_tr[a] == null) break;
				if (a >= RunnerBase && m_tr[a] != null)
				{
					Leave(a, Mathf.Max(m_t + 0.5f, LastMove(a)));
				}
				break;
			}
			case EvKind.ScoreBoard:
			{
				// A second after he crosses (the board would cover the close-up of the play at the plate).
				if (m_replay || (a >= RunnerBase && m_tr[a] == null)) break;
				m_scored++;
				BroadcastTeamInfo home = DataCenter.instance.GetHomeInfo(), away = DataCenter.instance.GetAwayInfo();
				if (m_pm.parentWindow != null && m_pm.parentWindow.BScore != null)
				{
					m_pm.parentWindow.BScore.SetScoreNum(home.R, away.R, DataCenter.instance.GetAtkTeam() != eTeam.T_HOME ? away.R : home.R);
					m_boardShown = Time.time;
				}
				break;
			}
			case EvKind.Sound:
				Ma9MSoundLibrary.PlayEffect(e.Clip, e.Vol);
				if (s_shotDir != null) Debug.Log($"[Ma9MScript] sound {e.Clip} x{e.Vol:F1} at {m_t:F2}");
				break;
			case EvKind.Camera:
			{
				if (m_director != null) break;
				CameraManager cm = m_pm.GetCameraManager();
				if (cm != null) cm.TraceBall(Ma9MScriptTimeline.World(p.P1, Ma9MScriptTimeline.HandHeight), Mathf.Max(0.2f, p.T1 - p.T0));
				break;
			}
		}
	}

	// OUT / SAFE: the text, the sound, the base umpire's call (255 = no base: a caught ball).
	private void Call(bool isOut, int baseNo)
	{
		if (isOut)
		{
			m_pm.IncCurrentOutCount();
			SoundManager.PlayEffectSound(eBroadCastSound.BroadCast_Out);
			if (m_pm.parentWindow != null) m_pm.parentWindow.ShowTextUIOut();
		}
		else
		{
			SoundManager.PlayEffectSound(eBroadCastSound.BroadCast_Safe);
			if (m_pm.parentWindow != null) m_pm.parentWindow.ShowTextUISafe();
		}
		// The call goes up the screen (the original's spare upper spot): the director frames the play
		// in the middle, where the word would cover the man making it.
		BroadCast_TextImage ti = m_pm.parentWindow != null ? m_pm.parentWindow.textImage : null;
		if (ti != null && ti.BallState != null && ti.TextImagePosition != null && ti.TextImagePosition.Length > 1 && m_pm.parentWindow.BScore != null && m_pm.parentWindow.BScore.GetState() == BroadCastScoreState.IDLE)
		{
			ti.BallState.transform.localPosition = ti.TextImagePosition[1];
		}
		if (baseNo == 255 || m_pm.umpiremanager == null) return;
		eUmpireManagerState st;
		switch (baseNo % 4)
		{
			case 1: st = isOut ? eUmpireManagerState.RUNNER1BOUT : eUmpireManagerState.RUNNER1BSAFE; break;
			case 2: st = isOut ? eUmpireManagerState.RUNNER2BOUT : eUmpireManagerState.RUNNER2BSAFE; break;
			case 3: st = isOut ? eUmpireManagerState.RUNNER3BOUT : eUmpireManagerState.RUNNER3BSAFE; break;
			default: st = isOut ? eUmpireManagerState.RUNNERHOMEOUT : eUmpireManagerState.RUNNERHOMESAFE; break;
		}
		m_pm.umpiremanager.ForceState(st);
	}

	public void StartReplay()
	{
		Replayed = true;
		m_replay = true;
		// The score board has had its moment: off the replay.
		BroadCastScore board = m_pm.parentWindow != null ? m_pm.parentWindow.BScore : null;
		if (board != null && board.GetState() != BroadCastScoreState.IDLE) board.ForceState(BroadCastScoreState.IDLE);
		m_done = false;
		m_director = new Ma9MCameraDirector(m_tl, m_tl.Script, true);
		// Only the moment that made it a highlight, in slow motion.
		m_director.KeyMoment(m_end, out float from, out float to);
		m_t = from;
		m_replayEnd = to;
		m_next = 0;
		while (m_next < m_events.Count && m_events[m_next].At < from) m_next++;
		for (int a = 0; a < Actors; a++)
		{
			m_actionUntil[a] = 0; m_clip[a] = null; m_outFade[a] = 0; m_turnUntil[a] = 0; m_leaveAt[a] = -1; m_leaving[a] = false; m_trk[a] = null; m_hasLast[a] = false;
			m_plan[a].Clear();
		}
		ReachAirAll();
		m_throwT0 = -1f;
		m_walkingOff = false;
		if (s_shotDir != null)
		{
			m_scene += 500;   // dev shots of the replay beside the live ones
			m_nextShot = from + 0.2f;
		}
		Time.timeScale = ReplaySpeed;
		Debug.Log("[Ma9MScript] replay");
	}

	private void OnGUI()
	{
		if (!m_active || !m_replay) return;
		GUIStyle st = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(Screen.height * 0.04f), fontStyle = FontStyle.BoldAndItalic };
		st.normal.textColor = new Color(1f, 0.85f, 0.2f);
		GUI.Box(new Rect(Screen.width * 0.03f, Screen.height * 0.12f, Screen.height * 0.3f, Screen.height * 0.07f), "REPLAY", st);
	}

	// A steal: the runner breaks on the pitcher's first move (PlayManager INPITCH), from his lead
	// toward the next base, picking up speed; the script takes him over when the pitch is in the mitt.
	private Runner m_breaking;
	private float m_breakSpeed;
	private Vector3 m_breakDir;

	public static void BreakForSteal(PlayManager pm, int scene)
	{
		if (Ma9MScripts.Disabled) return;
		Ma9MPlayScript s = Ma9MScripts.For(scene);
		if (s == null) return;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind != Ma9MSeg.Run || p.Actor <= RunnerBase || p.Actor >= RunnerBase + 4) continue;
			Runner r = pm.GetBatterManager() != null ? pm.GetBatterManager().GetRunner(p.Actor - RunnerBase) : null;
			if (r == null) return;
			if (Instance == null) Instance = pm.gameObject.GetComponent<Ma9MScriptPlayer>() ?? pm.gameObject.AddComponent<Ma9MScriptPlayer>();
			Instance.m_breaking = r;
			Instance.m_breakSpeed = 0;
			Vector3 to = Ma9MScriptTimeline.World(p.P1);
			Instance.m_breakDir = new Vector3(to.x - r.transform.position.x, 0, to.z - r.transform.position.z).normalized;
			r.ForceState(eRunnerState.IDLE);
			r.enabled = false;
			r.transform.rotation = Quaternion.LookRotation(Instance.m_breakDir);
			Animation an = r.Ma9MAnimation;
			string run = Ma9MMotions.Ensure(an, "M_Runner_Run") ? "M_Runner_Run" : "Run_Run";
			if (an != null && an[run] != null)
			{
				an[run].wrapMode = WrapMode.Loop;
				an.CrossFade(run, 0.1f);
			}
			Instance.m_breakClip = run;
			return;
		}
	}

	private void BreakStep()
	{
		if (m_breaking == null || m_active) return;
		m_breakSpeed = Mathf.Min(8.2f, m_breakSpeed + 5f * Time.deltaTime);
		m_breaking.transform.position += m_breakDir * m_breakSpeed * Time.deltaTime;
		Animation an = m_breaking.Ma9MAnimation;
		Ma9MMotions.Info run = Ma9MMotions.Get("M_Runner_Run");
		if (m_breakClip == "M_Runner_Run" && run != null && run.Speed > 1f && an != null && an[m_breakClip] != null) an[m_breakClip].speed = Mathf.Clamp(m_breakSpeed / run.Speed, 0.45f, 1.8f);
	}

	private string m_breakClip;

	// ENDSCENE: the play goes on under its camera until the next scene takes over (EndCoast). The scene's end sets the
	// backup fielders idle: their clips are put back on the next frame.
	public static void Finish()
	{
		if (Instance == null || !Instance.m_active) return;
		Instance.m_coast = true;
		Instance.m_done = false;
		for (int a = 0; a < 9; a++) Instance.m_clip[a] = null;
	}

	// The next scene, the inning change or the next highlight: everything back to its own logic (PlayManager, as it
	// leaves ENDSCENE / WAITPACKET, before it places anybody).
	public static void EndCoast()
	{
		if (Instance == null || !Instance.m_coast) return;
		Instance.Release();
	}

	// ENDSCENE of a scene that ended the inning. A scripted play is already walking off (or does now); a scene with no
	// script (a strikeout, a bunt) is taken over here, the fielders and the runners left on base walking off under the
	// wide camera until the inning change.
	public static void InningOver(PlayManager pm)
	{
		if (Instance != null && Instance.m_coast)
		{
			if (!Instance.m_walkingOff) Instance.WalkOff();
			return;
		}
		if (Ma9MScripts.Disabled || pm == null) return;
		if (Instance == null) Instance = pm.gameObject.GetComponent<Ma9MScriptPlayer>() ?? pm.gameObject.AddComponent<Ma9MScriptPlayer>();
		try
		{
			Instance.BeginWalkOff(pm);
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MScript] walk-off: " + ex);
			Instance.Release();
		}
	}

	private void BeginWalkOff(PlayManager pm)
	{
		Ma9MPlayScript s = new Ma9MPlayScript { Ev = 0, La = 0, H = 45, End = 0 };
		m_pm = pm;
		m_tl = new Ma9MScriptTimeline(s);
		m_t = 0;
		m_end = 0;
		m_done = false;
		m_coast = true;
		m_replay = false;
		m_next = 0;
		m_events.Clear();
		m_tags.Clear();
		m_preset = null;
		for (int a = 0; a < Actors; a++)
		{
			m_tr[a] = null; m_anim[a] = null; m_actionUntil[a] = 0; m_clip[a] = null; m_outFade[a] = 0; m_turnUntil[a] = 0; m_leaveAt[a] = -1; m_leaving[a] = false; m_offT0[a] = -1;
			m_lungeAt[a] = -1; m_slideAt[a] = -1; m_scoopAt[a] = -1; m_hasLast[a] = false; m_vel[a] = Vector2.zero; m_trk[a] = null; m_plan[a].Clear(); m_runCatchAt[a] = -1f; m_diveSyncT1[a] = -1f;
			m_airReach[a] = false; m_airFaceUntil[a] = -1; m_airSide[a] = null;
		}
		for (int d = 0; d < 9; d++) m_hand[d] = null;
		m_fm = pm.GetFielderManager();
		for (int d = 0; d < 9; d++)
		{
			Fielder f = m_fm != null ? m_fm.GetFielder((ePos)EPosOf(d)) : null;
			m_fielders[d] = f;
			if (f == null) continue;
			f.ForceState(eFielderState.AI_NONE);
			f.enabled = false;
			m_tr[d] = f.transform;
			m_anim[d] = f.GetComponent<Animation>();
			m_start[d] = new Vector2(f.transform.position.x, f.transform.position.z);
		}
		if (m_fm != null) m_fm.enabled = false;
		BatterManager bm = pm.GetBatterManager();
		for (int i = 0; i < 4; i++)
		{
			// The men left on base (the batter of a strikeout is not a runner).
			Runner r = i > 0 && bm != null && bm.IsInGameRunner(i) ? bm.GetRunner(i) : null;
			m_runners[i] = r;
			if (r == null) continue;
			r.ForceState(eRunnerState.IDLE);
			r.enabled = false;
			m_tr[RunnerBase + i] = r.transform;
			m_anim[RunnerBase + i] = r.Ma9MAnimation;
			m_start[RunnerBase + i] = new Vector2(r.transform.position.x, r.transform.position.z);
		}
		m_ball = pm.GetBall();
		m_ballRenderer = m_ball != null && m_ball.BallObject != null ? m_ball.BallObject.GetComponentInChildren<Renderer>() : null;
		if (m_ball != null)
		{
			m_ball.ForceState(eBallState.STOP);
			m_ball.enabled = false;
		}
		m_director = s_oldCam ? null : new Ma9MCameraDirector(m_tl, s, false);
		if (m_director != null) m_director.WideFrom(0f);
		m_scene = DataCenter.instance.GetCurrentSceneNum();
		m_nextShot = s_shotDir != null ? 0.2f : float.MaxValue;
		m_walkOffAt = 0f;
		m_walkingOff = false;
		m_active = true;
		Debug.Log("[Ma9MScript] inning over: walking off");
	}

	private void Release()
	{
		if (m_breaking != null)
		{
			m_breaking.enabled = true;
			m_breaking = null;
		}
		m_active = false;
		m_done = false;
		m_coast = false;
		m_walkingOff = false;
		m_walkOffAt = -1f;
		Time.timeScale = 1f;
		m_director = null;
		m_replay = false;
		if (m_preset != null)
		{
			m_preset.m_Vangle = m_presetV;
			m_preset.m_Hangle = m_presetH;
			m_preset.m_catchpos = m_presetCatch;
			m_preset = null;
		}
		for (int d = 0; d < 9; d++)
		{
			if (m_fielders[d] != null) m_fielders[d].enabled = true;
			m_fielders[d] = null;
		}
		if (m_fm != null) m_fm.enabled = true;
		for (int i = 0; i < 4; i++)
		{
			if (m_runners[i] != null) m_runners[i].enabled = true;
			m_runners[i] = null;
		}
		if (m_ball != null)
		{
			m_ball.enabled = true;
			if (m_ballRenderer != null) m_ballRenderer.enabled = true;
		}
		for (int a = 0; a < Actors; a++) m_tr[a] = null;
	}
}
