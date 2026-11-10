using System.Collections;
using System.Collections.Generic;
using TableReader;
using UnityEngine;

//  - The no-doubter homer (ball preset hit type 5): it glows off the bat, the camera follows it
//    into the stands, the batter admires it and struts, the pitcher kicks the dirt or turns away.
//  - The wall-scraper (hit type 4): the outfielder goes back to the wall and leaps, the ball
//    just clears his glove, and the batter's trot is followed round the bases, ending with a
//    shot down the third-base line as he comes home to the teammates waiting at the plate.
//  - The walk-off: a banner, the winning side running out to mob the hero, the losers walking off.
// While a shot runs this owns Camera.main (placed in LateUpdate, after CameraManager), and
// PlayManager waits for Busy to clear before it closes the scene.
public class Ma9MShow : MonoBehaviour
{
	public static Ma9MShow Instance { get; private set; }

	// A sequence is playing: the scene must not close yet.
	public static bool Busy => Instance != null && (Instance.m_show != null || Instance.m_walkOff != null);

	// The wall-scraper trot is running HOMERUNINPLAY in place of PlayManager's own timeline.
	public static bool RunsTrot => Instance != null && Instance.m_trot;

	private const float TrotSpeed = 8f;

	private static readonly Vector3 BattingDugout = new Vector3(-20f, 0f, 13f);

	private static readonly Vector3 FieldingDugout = new Vector3(13f, 0f, -20f);

	private Coroutine m_show;

	private Coroutine m_walkOff;

	private bool m_trot;

	private bool m_walkOffDone;

	private int m_lastInningSeen = -1;

	// Camera override.
	private bool m_camOn;

	private bool m_camSnap;

	private Vector3 m_camPos;

	private System.Func<Vector3> m_camLook;

	private float m_camFov;

	private float m_camFovRate;

	private float m_camFovMin = 8f;

	private readonly List<GameObject> m_spawned = new List<GameObject>();

	private UILabel m_banner;

	private float m_bannerTime;

	private static PlayManager PM => PlayManager.instance;

	private static void Log(string what)
	{
		Debug.Log("[Ma9MShow] " + what + " (t=" + Time.time.ToString("F2") + ")");
	}

	private void Awake()
	{
		Instance = this;
	}

	private void OnDestroy()
	{
		if (Instance == this)
		{
			Instance = null;
		}
		Cleanup();
	}

	// ---------------------------------------------------------------- camera

	private void Cam(Vector3 pos, System.Func<Vector3> look, float fov, float fovRate = 0f, float fovMin = 8f)
	{
		// CameraManager's tracking states (TRACE_FLY, TRACE_BALL...) turn the camera every frame;
		// left running they fought this shot and the camera drifted between the two subjects.
		CameraManager cm = PM.GetCameraManager();
		if (cm.GetState() != eCameraManagerState.OFFDEF_CHANGE)
		{
			cm.ForceState(eCameraManagerState.OFFDEF_CHANGE);
		}
		m_camFovMin = fovMin;
		m_camOn = true;
		m_camSnap = true;
		m_camPos = pos;
		m_camLook = look;
		m_camFov = fov;
		m_camFovRate = fovRate;
		if (Camera.main != null)
		{
			Camera.main.fieldOfView = fov;
		}
	}

	private void CamOff()
	{
		m_camOn = false;
	}

	private void LateUpdate()
	{
		UpdateBanner();
		if (!m_camOn || Camera.main == null || m_camLook == null)
		{
			return;
		}
		Transform c = Camera.main.transform;
		c.position = m_camPos;
		Vector3 dir = m_camLook() - m_camPos;
		if (dir.sqrMagnitude > 0.0001f)
		{
			Quaternion want = Quaternion.LookRotation(dir);
			c.rotation = m_camSnap ? want : Quaternion.Slerp(c.rotation, want, Time.deltaTime * 7f);
		}
		m_camSnap = false;
		m_camFov += m_camFovRate * Time.deltaTime;
		Camera.main.fieldOfView = Mathf.Clamp(m_camFov, m_camFovMin, 70f);
	}

	// ---------------------------------------------------------------- helpers

	private static void Play(Component who, string clip, float fade = 0.15f, float speed = 1f, WrapMode wrap = WrapMode.Once)
	{
		if (who == null)
		{
			return;
		}
		Animation a = who.GetComponent<Animation>();
		if (a == null || a[clip] == null)
		{
			return;
		}
		a[clip].wrapMode = wrap;
		a[clip].speed = speed;
		a.CrossFade(clip, fade);
	}

	// Lends a clip another kind of figure has (all share the Bip01 rig): fielders get the
	// runners' walk, runners the batters' celebrations.
	private static void Lend(Component who, GameObject prefab, string clip)
	{
		Animation a = who != null ? who.GetComponent<Animation>() : null;
		Animation src = prefab != null ? prefab.GetComponent<Animation>() : null;
		if (a == null || src == null || a[clip] != null)
		{
			return;
		}
		AnimationClip c = src.GetClip(clip);
		if (c != null)
		{
			a.AddClip(c, clip);
		}
	}

	private static void Face(Transform t, Vector3 at)
	{
		at.y = t.position.y;
		if ((at - t.position).sqrMagnitude > 0.0001f)
		{
			t.rotation = Quaternion.LookRotation(at - t.position);
		}
	}

	private static bool HomeBats => DataCenter.instance.GetAtkTeam() == eTeam.T_HOME;

	// Shows the run(s) on the score popup (which also moves the top bar, see BroadCastScore).
	private static void ScorePopup(int runs)
	{
		BroadcastTeamInfo home = DataCenter.instance.GetHomeInfo();
		BroadcastTeamInfo away = DataCenter.instance.GetAwayInfo();
		int bat = HomeBats ? home.R : away.R;
		PM.parentWindow.BScore.SetScoreNum(home.R, away.R, bat + runs - 1);
	}

	private static int RunsOnHomer()
	{
		int n = 0;
		byte[] order = PM.m_sr.m_base_batterOrder;
		for (int i = 0; i < order.Length; i++)
		{
			if (order[i] != 0)
			{
				n++;
			}
		}
		return Mathf.Max(1, n);
	}

	private static Pitcher ThePitcher => PM.GetFielderManager().GetFielder(ePos.POS_P) as Pitcher;

	private void PitcherReaction(bool angry)
	{
		Pitcher p = ThePitcher;
		if (p == null)
		{
			return;
		}
		p.ForceState(eFielderState.PITCHER_CEREMONY);
		Play(p, angry ? "Pit_FourBallKick" : "Pit_HomerunWatchingBall", 0.2f);
	}

	private static void ShowHomeUmpire()
	{
		GameObject u = PM.umpiremanager.GetUmpire(eUmpirePos.POS_HOMEUMPIRE);
		if (u != null)
		{
			u.SetActive(true);
		}
	}

	// ---------------------------------------------------------------- no-doubter (hit type 5)

	public void StartNoDoubter()
	{
		StopShow();
		Log("no-doubter");
		m_show = StartCoroutine(NoDoubter());
	}

	private IEnumerator NoDoubter()
	{
		Ball ball = PM.GetBall();
		Batter batter = PM.GetBatterManager().GetBatter();
		bool angry = Random.Range(0, 2) == 0;
		PM.parentWindow.ShowStrikeZone(false);
		BallPreset data = PM.GetBallData();
		float h = (data != null ? data.m_Hangle : 45f) * Mathf.Deg2Rad;
		Vector3 toBall = new Vector3(Mathf.Cos(h), 0f, Mathf.Sin(h));
		// The swing and the glow off the bat, from the pitch camera.
		yield return new WaitForSeconds(0.6f);
		// The batter admiring it: he watches it go, so the camera stands out along its flight.
		ShowHomeUmpire();
		if (batter != null)
		{
			Vector3 bp = batter.transform.position;
			Vector3 side = Vector3.Cross(Vector3.up, toBall);
			Cam(bp + toBall * 4.3f + side * 0.8f + Vector3.up * 1.3f, () => batter.transform.position + Vector3.up * 0.9f, 34f, -2.5f);
		}
		yield return new WaitForSeconds(1.7f);
		// Into the stands: from behind the plate, closing in as it carries.
		Cam(new Vector3(-7f, 2.6f, -7f), () => ball.transform.position, 40f, -9f, 16f);
		PitcherReaction(angry);
		Play(batter, "Bat_FourBallproud", 0.3f);
		yield return new WaitForSeconds(1.6f);
		// The pitcher, from in front of him: turned to watch it go, or kicking the dirt at the plate.
		Pitcher p = ThePitcher;
		if (p != null)
		{
			Vector3 pp = p.transform.position;
			Vector3 face = angry ? new Vector3(-pp.x, 0f, -pp.z).normalized : toBall;
			Vector3 side = Vector3.Cross(Vector3.up, face);
			Cam(pp + face * 4.6f + side * 1.1f + Vector3.up * 1.25f, () => p.transform.position + Vector3.up * 0.95f, 30f, -1.5f);
		}
		ScorePopup(RunsOnHomer());
		yield return new WaitForSeconds(2.2f);
		CamOff();
		m_show = null;
	}

	// ---------------------------------------------------------------- wall-scraper (hit type 4)

	public void StartWallScraper()
	{
		StopShow();
		Log("wall-scraper");
		m_trot = true;
		m_show = StartCoroutine(WallScraper());
	}

	private IEnumerator WallScraper()
	{
		PlayManager pm = PM;
		Ball ball = pm.GetBall();
		BallPreset data = pm.GetBallData();
		float vh = data != null ? data.m_speed * Mathf.Cos(data.m_Vangle * Mathf.Deg2Rad) : 40f;
		Vector3 wallAt = pm.catchPosition;
		wallAt.y = 0f;
		float wall = wallAt.magnitude + 1.5f;
		Vector3 wallDir = wallAt.sqrMagnitude > 1f ? wallAt.normalized : new Vector3(0.7071f, 0f, 0.7071f);
		Fielder chaser = pm.GetFielderManager().GetCatchFlyFielder();
		pm.parentWindow.ShowStrikeZone(false);
		float t0 = Time.time;
		bool started = false, atWall = false, leapt = false, cleared = false;
		float clearAt = 0f;
		// The flight: from high behind the plate, closing in as it carries; then at the wall, the
		// outfielder leaping as it goes over him. The batter breaks for first, the pitcher turns.
		while (!cleared || Time.time - clearAt < 1.5f)
		{
			float t = Time.time - t0;
			if (!started && t > 0.35f)
			{
				started = true;
				pm.GetBatterManager().ForceState(eBatterManagerState.INPLAY);
				ShowHomeUmpire();
				Cam(new Vector3(-6.5f, 3.4f, -6.5f), () => ball.transform.position, 44f, -10f, 20f);
				PitcherReaction(false);
			}
			Vector3 b = ball.transform.position;
			float bd = new Vector2(b.x, b.z).magnitude;
			if (started && !atWall && (wall - bd) / Mathf.Max(5f, vh) < 0.95f)
			{
				atWall = true;
				// Behind and beside the outfielder, framing him and the top of the wall the ball clears.
				Vector3 side = Vector3.Cross(Vector3.up, wallDir);
				Vector3 fpos = chaser != null ? chaser.transform.position : wallDir * (wall - 1.5f);
				fpos.y = 0f;
				Vector3 from = fpos - wallDir * 13f + side * 3.5f + Vector3.up * 1.9f;
				Vector3 look = Vector3.Lerp(fpos, wallDir * (wall - 1f), 0.5f) + Vector3.up * 2.3f;
				Cam(from, () => look, 42f);
			}
			if (!leapt && chaser != null && ball.GetState() == eBallState.FLY)
			{
				float fd = new Vector2(chaser.transform.position.x, chaser.transform.position.z).magnitude;
				if ((fd - bd) / Mathf.Max(5f, vh) < 0.42f)
				{
					leapt = true;
					Log("leap " + chaser.m_pos);
					chaser.ForceState(eFielderState.AI_NONE);
					Face(chaser.transform, Vector3.zero);
					Play(chaser, "Def_JumpCatchErr", 0.05f);
				}
			}
			if (!cleared && (bd > wall || t > 7f))
			{
				cleared = true;
				clearAt = Time.time;
				Log("cleared the wall at " + bd.ToString("F1") + " m");
				pm.parentWindow.ShowTextUIHomerun();
				Runner hero0 = pm.GetBatterManager().GetRunner(0);
				Lend(hero0, pm.ma9DoriMaker.runnerPrefab, "Run_Homerun45");
				// He ran hard out of the box; once it is gone he eases into his trot.
				KeepTrot(true);
			}
			if (cleared)
			{
				KeepTrot();
			}
			yield return null;
		}
		Runner hero = pm.GetBatterManager().GetRunner(0);
		// Round second: from shallow centre, the runner coming through the bag towards the camera
		// (a cut, so he can be a few strides short of it).
		if (hero != null && pm.GetBatterManager().IsInGameRunner(0) && hero.Ma9MDestBase <= eBaseIndex.BI_2B && (hero.Ma9MDestBase < eBaseIndex.BI_2B || (hero.transform.position - POSITION_INFO.BasePosition[2]).magnitude > 6f))
		{
			hero.Ma9MPlaceBefore(eBaseIndex.BI_2B, 6f, TrotSpeed);
			Play(hero, "Run_Homerun45", 0.1f, 1.1f, WrapMode.Loop);
		}
		Cam(new Vector3(41f, 2.3f, 39f), () => hero != null ? hero.transform.position + Vector3.up * 0.8f : POSITION_INFO.BasePosition[2], 30f, 1.5f);
		for (float w = 0f; w < 2.3f; w += Time.deltaTime)
		{
			KeepTrot();
			yield return null;
		}
		// Down the third-base line to the plate, where the runners who scored wait for him.
		if (hero != null && pm.GetBatterManager().IsInGameRunner(0) && (hero.Ma9MDestBase < eBaseIndex.BI_3B || (hero.Ma9MDestBase == eBaseIndex.BI_3B && (hero.transform.position - POSITION_INFO.BasePosition[3]).magnitude > 4f)))
		{
			hero.Ma9MPlaceBefore(eBaseIndex.BI_3B, 4f, TrotSpeed);
		}
		GreetAtHome(hero);
		Log("third to home");
		Cam(new Vector3(3.3f, 1.35f, -2.7f), () => hero != null ? hero.transform.position + Vector3.up * 0.85f : POSITION_INFO.BasePosition[3], 27f, 2.6f);
		float wait = 0f;
		while (hero != null && pm.GetBatterManager().IsInGameRunner(0) && wait < 7f)
		{
			KeepTrot();
			wait += Time.deltaTime;
			yield return null;
		}
		// Home: the popup, the runners who scored greet him at the plate, and they all walk off to the dugout
		// (user, 2026-10-31: he stopped on the plate in mid-stride and stayed there while the next scene was awaited).
		Log("home");
		ScorePopup(RunsOnHomer());
		if (hero != null)
		{
			hero.ForceState(eRunnerState.IDLE);
			hero.transform.position = new Vector3(-0.2f, 0f, 1.2f);
			Face(hero.transform, new Vector3(-1.0f, 0f, 2.4f));
			Play(hero, "Run_BasicStd", 0.15f, 1f, WrapMode.Loop);
		}
		Cam(new Vector3(7.2f, 1.8f, 7.6f), () => new Vector3(-0.6f, 0.85f, 1.4f), 30f, 1f);
		List<Runner> greeters = new List<Runner>();
		for (int i = 1; i < 4; i++)
		{
			Runner r = pm.GetBatterManager().GetRunner(i);
			if (r != null && r != hero)
			{
				greeters.Add(r);
			}
		}
		for (float w = 0f; w < 1.3f; w += Time.deltaTime)
		{
			Hop(hero, w, 0.25f);
			for (int i = 0; i < greeters.Count; i++)
			{
				Hop(greeters[i], w + 0.3f * (i + 1), 0.2f);
			}
			yield return null;
		}
		foreach (Runner r in greeters)
		{
			if (r != null)
			{
				r.transform.position = new Vector3(r.transform.position.x, 0f, r.transform.position.z);
			}
		}
		if (hero != null)
		{
			hero.transform.position = new Vector3(hero.transform.position.x, 0f, hero.transform.position.z);
		}
		List<Runner> group = new List<Runner>(greeters);
		if (hero != null)
		{
			group.Insert(0, hero);
		}
		StartCoroutine(WalkOff(group));
		yield return new WaitForSeconds(1.1f);
		CamOff();
		m_trot = false;
		m_show = null;
		if (m_walkOff == null)
		{
			pm.SendMessage(ePlayManagerMessage.MESSAGE_SCENE_END);
		}
	}

	// The batter-runner jogs (the runner resets him to a sprint at every base).
	private void KeepTrot(bool now = false)
	{
		Runner hero = PM.GetBatterManager().GetRunner(0);
		if (hero == null || hero.GetState() != eRunnerState.RUNNER_ADVANCE || !PM.GetBatterManager().IsInGameRunner(0))
		{
			return;
		}
		if (Mathf.Abs(hero.Ma9MSpeed - TrotSpeed) > 0.01f && (now || hero.GetElapsedTimeInState() < 0.25f))
		{
			hero.SetRunnerAdvanceTime(TrotSpeed);
			if (hero.Ma9MAnimation != null && hero.Ma9MAnimation["Run_Homerun45"] != null && hero.Ma9MDestBase != eBaseIndex.BI_1B)
			{
				Play(hero, "Run_Homerun45", 0.2f, 1.1f, WrapMode.Loop);
			}
		}
	}

	// Men who scored walk off toward the dugout behind third (where the original sends scoring runners), on
	// until the next scene clears them.
	private IEnumerator WalkOff(List<Runner> men)
	{
		Vector3 dugout = new Vector3(-20f, 0f, 13f);
		foreach (Runner r in men)
		{
			if (r == null) continue;
			r.ForceState(eRunnerState.IDLE);
			Face(r.transform, dugout);
			Play(r, "Run_Walk", 0.25f, 1f, WrapMode.Loop);
		}
		while (true)
		{
			bool any = false;
			foreach (Runner r in men)
			{
				if (r == null) continue;
				any = true;
				Vector3 to = dugout - r.transform.position;
				to.y = 0f;
				if (to.magnitude > 0.3f)
				{
					r.transform.position += to.normalized * Mathf.Min(1.6f * Time.deltaTime, to.magnitude);
				}
			}
			if (!any) yield break;
			yield return null;
		}
	}

	// The runners who scored ahead of him wait behind the plate.
	private void GreetAtHome(Runner hero)
	{
		Vector3[] spots = { new Vector3(-1.0f, 0f, 2.4f), new Vector3(0.6f, 0f, 2.9f), new Vector3(-2.3f, 0f, 1.5f) };
		int k = 0;
		for (int i = 1; i < 4; i++)
		{
			Runner r = PM.GetBatterManager().GetRunner(i);
			if (r == null || r == hero || k >= spots.Length)
			{
				continue;
			}
			r.ForceState(eRunnerState.IDLE);
			r.transform.position = spots[k++];
			Face(r.transform, POSITION_INFO.BasePosition[3]);
			Play(r, "Run_BasicStd", 0.1f, 1f, WrapMode.Loop);
			r.GetComponentInChildren<SkinnedMeshRenderer>().enabled = true;
		}
	}

	private static void Hop(Component who, float t, float height)
	{
		if (who == null)
		{
			return;
		}
		Vector3 p = who.transform.position;
		p.y = Mathf.Abs(Mathf.Sin(t * Mathf.PI * 2.4f)) * height;
		who.transform.position = p;
	}

	// ---------------------------------------------------------------- walk-off

	// Every run as it scores (BroadCastScore): in the home half of the ninth or later, the run
	// that puts the home side ahead ends the game.
	public static void OnRunScored(int awayScore, int homeScore)
	{
		if (Instance == null || PM == null || Instance.m_walkOffDone)
		{
			return;
		}
		if (DataCenter.instance.GetInning() < 8 || !HomeBats || homeScore <= awayScore)
		{
			return;
		}
		Instance.m_walkOffDone = true;
		Log("walk-off " + awayScore + ":" + homeScore);
		Instance.m_walkOff = Instance.StartCoroutine(Instance.WalkOff());
	}

	private IEnumerator WalkOff()
	{
		PlayManager pm = PM;
		eSituation sit = pm.GetCurrentSituation();
		string what = sit == eSituation.ST_HR ? "끝내기 홈런!" : (sit == eSituation.ST_1B || sit == eSituation.ST_2B || sit == eSituation.ST_3B) ? "끝내기 안타!" : sit == eSituation.ST_FOURBALL ? "끝내기 볼넷!" : "끝내기!";
		// A home-run show in progress finishes first (the trot ends at the plate); the walk-off follows on.
		while (m_show != null)
		{
			yield return null;
		}
		pm.parentWindow.ShowStrikeZone(false);
		Banner(what);
		// The hero: the batter-runner if he is out on the bases, else the batter at the plate.
		BatterManager bm = pm.GetBatterManager();
		Component hero = null;
		Runner r0 = bm.GetRunner(0);
		if (r0 != null && r0.GetComponentInChildren<SkinnedMeshRenderer>().enabled)
		{
			hero = r0;
			r0.ForceState(eRunnerState.IDLE);
		}
		else if (bm.GetBatter() != null)
		{
			hero = bm.GetBatter();
		}
		Vector3 spot = hero != null ? hero.transform.position : Vector3.zero;
		spot.y = 0f;
		if (hero is Runner)
		{
			Play(hero, "Run_Homerun45", 0.1f, 1f, WrapMode.Loop);
		}
		// The runners still on the field join in.
		List<Component> mob = new List<Component>();
		for (int i = 1; i < 4; i++)
		{
			Runner r = bm.GetRunner(i);
			if (r != null && r.gameObject.activeInHierarchy && r != hero)
			{
				r.ForceState(eRunnerState.IDLE);
				mob.Add(r);
			}
		}
		// The bench empties.
		bool home = HomeBats;
		for (int i = 0; i < 7; i++)
		{
			GameObject g = pm.ma9DoriMaker.InstantiateRunner(home, Random.Range(0, 3) > 0);
			g.transform.position = BattingDugout + new Vector3(Random.Range(-1.5f, 1.5f), 0f, Random.Range(-3f, 3f));
			m_spawned.Add(g);
			mob.Add(g.GetComponent<Runner>());
		}
		// The camera looks at the hero from the infield; the mob gathers on his far side and round
		// the flanks, so their faces and his are in shot.
		Vector3 camDir = POSITION_INFO.MoundPosition - spot;
		camDir.y = 0f;
		camDir = camDir.sqrMagnitude > 4f ? camDir.normalized : new Vector3(0.7071f, 0f, 0.7071f);
		float baseAngle = Mathf.Atan2(-camDir.z, -camDir.x);
		List<Vector3> dest = new List<Vector3>();
		List<float> delay = new List<float>();
		for (int i = 0; i < mob.Count; i++)
		{
			float a = baseAngle + Mathf.Lerp(-1.25f, 1.25f, mob.Count > 1 ? i / (float)(mob.Count - 1) : 0.5f);
			dest.Add(spot + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * Random.Range(1.3f, 2.2f));
			delay.Add(mob[i] is Runner && m_spawned.Contains(mob[i].gameObject) ? Random.Range(0f, 0.6f) : 0f);
		}
		if (hero != null)
		{
			Face(hero.transform, spot + camDir * 5f);
		}
		// The losers walk off.
		FielderManager fm = pm.GetFielderManager();
		List<Fielder> losers = new List<Fielder>();
		for (int i = 0; i < 9; i++)
		{
			Fielder f = fm.GetFielder((ePos)i);
			if (f == null)
			{
				continue;
			}
			f.ForceState(eFielderState.AI_NONE);
			Lend(f, pm.ma9DoriMaker.runnerPrefab, "Run_Walk");
			losers.Add(f);
		}
		// Wide on the celebration from the infield, easing in.
		Vector3 from = spot + camDir * 10f;
		from.y = 4.2f;
		Cam(from, () => spot + Vector3.up * 0.9f, 40f, -1.6f);
		float t = 0f;
		bool[] arrived = new bool[mob.Count];
		while (t < 6.5f)
		{
			t += Time.deltaTime;
			for (int i = 0; i < mob.Count; i++)
			{
				Component m = mob[i];
				if (m == null)
				{
					continue;
				}
				if (t < delay[i])
				{
					continue;
				}
				Vector3 p = m.transform.position;
				p.y = 0f;
				Vector3 to = dest[i] - p;
				if (!arrived[i] && to.magnitude > 0.2f)
				{
					Face(m.transform, dest[i]);
					if (!m.GetComponent<Animation>().IsPlaying("Run_FastRun"))
					{
						Play(m, "Run_FastRun", 0.1f, 1f, WrapMode.Loop);
					}
					m.transform.position = p + to.normalized * Mathf.Min(to.magnitude, 8.5f * Time.deltaTime);
				}
				else
				{
					if (!arrived[i])
					{
						arrived[i] = true;
						Face(m.transform, spot);
						Play(m, "Run_LeadStd", 0.15f, 1f, WrapMode.Loop);
					}
					Hop(m, t + i * 0.37f, 0.3f);
				}
			}
			Hop(hero, t, 0.35f);
			for (int i = 0; i < losers.Count; i++)
			{
				Fielder f = losers[i];
				if (f == null || t < 0.6f + i * 0.15f)
				{
					continue;
				}
				Vector3 p = f.transform.position;
				Vector3 to = FieldingDugout - p;
				to.y = 0f;
				if (to.magnitude > 0.5f)
				{
					Face(f.transform, FieldingDugout);
					if (f.GetComponent<Animation>()["Run_Walk"] != null && !f.GetComponent<Animation>().IsPlaying("Run_Walk"))
					{
						Play(f, "Run_Walk", 0.2f, 0.85f, WrapMode.Loop);
					}
					f.transform.position = p + to.normalized * 2.1f * Time.deltaTime;
				}
			}
			yield return null;
		}
		CamOff();
		m_walkOff = null;
	}

	// ---------------------------------------------------------------- banner

	private void Banner(string text)
	{
		BroadCast_MainWindow w = PM.parentWindow;
		UILabel src = w != null && w.topInfo != null ? w.topInfo.label_Inning : null;
		if (src == null)
		{
			return;
		}
		if (m_banner == null)
		{
			Transform parent = w.textImage != null ? w.textImage.transform.parent : w.transform;
			GameObject g = Instantiate(src.gameObject, parent);
			g.name = "Ma9MBanner";
			m_banner = g.GetComponent<UILabel>();
		}
		m_banner.gameObject.SetActive(true);
		m_banner.text = text;
		m_banner.color = new Color(1f, 0.86f, 0.22f);
		m_banner.effectStyle = UILabel.Effect.Outline;
		m_banner.effectColor = new Color(0.35f, 0.08f, 0.02f);
		m_banner.depth = 100;
		m_banner.transform.localPosition = new Vector3(0f, 95f, 0f);
		m_bannerTime = Time.time;
	}

	private void UpdateBanner()
	{
		if (m_banner == null || !m_banner.gameObject.activeSelf)
		{
			return;
		}
		float t = Time.time - m_bannerTime;
		float s = t < 0.3f ? Mathf.Lerp(200f, 70f, t / 0.3f) : 70f + Mathf.Sin(t * 5f) * 2f;
		m_banner.transform.localScale = new Vector3(s, s, 1f);
		m_banner.alpha = t < 5.6f ? 1f : Mathf.Clamp01(1f - (t - 5.6f) / 0.6f);
		if (t > 6.3f)
		{
			m_banner.gameObject.SetActive(false);
		}
	}

	// ---------------------------------------------------------------- lifecycle

	private void StopShow()
	{
		if (m_show != null)
		{
			StopCoroutine(m_show);
			m_show = null;
		}
		m_trot = false;
		CamOff();
	}

	private void Update()
	{
		if (PM == null)
		{
			return;
		}
		ePlayManagerState s = PM.GetState();
		// A new game (or the end board) clears the stage.
		if (s == ePlayManagerState.ENDGAME || s == ePlayManagerState.IDLE || s == ePlayManagerState.CHANGE || s == ePlayManagerState.STARTGAME)
		{
			if (m_spawned.Count > 0 || m_camOn)
			{
				Cleanup();
			}
			if (s == ePlayManagerState.STARTGAME)
			{
				m_walkOffDone = false;
			}
		}
		int inning = DataCenter.instance.GetInning();
		if (inning < m_lastInningSeen)
		{
			m_walkOffDone = false;
		}
		m_lastInningSeen = inning;
	}

	private void Cleanup()
	{
		foreach (GameObject g in m_spawned)
		{
			if (g != null)
			{
				Destroy(g);
			}
		}
		m_spawned.Clear();
		if (m_show != null)
		{
			StopCoroutine(m_show);
			m_show = null;
		}
		if (m_walkOff != null)
		{
			StopCoroutine(m_walkOff);
			m_walkOff = null;
		}
		m_trot = false;
		m_camOn = false;
	}
}
