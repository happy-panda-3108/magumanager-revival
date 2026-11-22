using System;
using System.Collections;
using System.Collections.Generic;
using TableReader;
using UnityEngine;

// FA 계약서 서명 → 입단식 (engine session, 2026-11-17; user 09:3x: "협상이 종료되서 영입되는 순간에 계약서에 서명하는 연출을
// 추가해주고 … 계약서 서명 뒤 입단식도 너무 기대되는데 높은 퀄리티의 입단식과 재밌게 만들어줬으면 함").
// The systems session's 협상 screen (Ma9MNegotiation) sends the signing first and, once the server took it, closes itself
// and calls Play; its `done` shows the server's result popup and redraws the FA 시장 (a 재계약: the 계약관리 list). So a
// failed signing never shows "계약 완료" (docs\FA협상_설계.md, section 6).
//
// A full-screen overlay of its own panel on the screen's 2D UI root, drawn from code (this NGUI sizes widgets by their
// transform's scale; z in front is more negative):
//   1. 계약서 — the paper slides up, the terms are typed line by line, the player signs (his name written letter by letter
//      with a pen), the red "계약 완료" seal slams down (shake, dust).
//   2. 입단식 (not for a 재계약) — a stage in the club's colours under moving spotlights and the club's emblem, the player's
//      picture, the jersey presented in front of him with his name, camera flashes, the reporter's question and his answer
//      (by how happy the deal left him), the terms on a ribbon, confetti; "탭하여 계속".
// [건너뛰기] or the back key ends it; a tap moves on a scene. done is called exactly once (also when the screen goes away).
public class Ma9MFaCeremony : MonoBehaviour
{
	public sealed class Info
	{
		public int cardIdx;            // the FA card (7,000,000 + source)
		public string playerName;
		public string fromClub;        // the source club's name for the card's season
		public string toClub;          // our club's name
		public int toEmblem;           // our club's emblem number (Ma9MEmblemYears)
		public int seasons;            // contract seasons (1 = 2.5 days)
		public int days;
		public long priceMp;
		public long listPriceMp;
		public int satisfaction;       // the 협상 gauge at the signing (100-120s; 110+ = 컨디션 최상)
		public bool conditionTop;
		public bool isRenewal;         // 재계약 협상: a short re-signing, no 입단식
	}

	private static Ma9MFaCeremony s_live;

	public static bool Showing => s_live != null;

	// done is called exactly once: when the scenes end or the player skips them.
	public static void Play(Info info, Action done)
	{
		if (info == null)
		{
			done?.Invoke();
			return;
		}
		try
		{
			if (s_live != null)
			{
				s_live.Finish();
			}
			GameObject parent = OverlayRoot(out UIFont font, out Camera cam);
			if (parent == null || font == null)
			{
				Debug.Log("[Ma9MFaCeremony] no 2D UI root or font on this screen: done without the scenes");
				done?.Invoke();
				return;
			}
			GameObject go = NGUITools.AddChild(parent);
			go.name = "Ma9MFaCeremony";
			go.transform.localPosition = new Vector3(0f, 0f, OverlayZ(parent, cam, 0.6f));
			go.AddComponent<UIPanel>();
			s_live = go.AddComponent<Ma9MFaCeremony>();
			s_live.m_info = info;
			s_live.m_done = done;
			s_live.m_font = font;
			Debug.Log("[Ma9MFaCeremony] " + info.playerName + " → " + info.toClub + (info.isRenewal ? " (재계약)" : string.Empty) + ", " + info.seasons + "시즌 " + info.days + "일 " + info.priceMp.ToString("N0") + " MP | root " + parent.name + " z " + go.transform.localPosition.z.ToString("F0"));
			s_live.StartCoroutine(s_live.Run());
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MFaCeremony] could not start: " + e);
			if (s_live != null)
			{
				s_live.Finish();
			}
			else
			{
				done?.Invoke();
			}
		}
	}

	// Dev (Ma9MRemote "ceremony <idx> [renewal] [top]"): sample terms, our club, nothing sent to the server.
	public static string PlayTest(int cardIdx, bool renewal, bool top)
	{
		TRRow_PlayerCard card = cardIdx > 0 && ResourceManager.g_playerTable != null ? (ResourceManager.g_playerTable.FindBatter(cardIdx) ?? ResourceManager.g_playerTable.FindPitcher(cardIdx)) : null;
		Info info = new Info
		{
			cardIdx = cardIdx,
			playerName = card != null && !string.IsNullOrEmpty(card.name) ? card.name : "시험 선수",
			fromClub = "원래 구단",
			toClub = string.IsNullOrEmpty(TopMenu_DataCenter.m_teamName) ? "우리 구단" : TopMenu_DataCenter.m_teamName,
			toEmblem = TopMenu_DataCenter.m_teamEmblem,
			seasons = 4,
			days = 10,
			priceMp = 16500000,
			listPriceMp = 15000000,
			satisfaction = top ? 112 : 88,
			conditionTop = top,
			isRenewal = renewal
		};
		Play(info, () => Debug.Log("[Ma9MFaCeremony] test done"));
		return "ok ceremony " + info.playerName + (renewal ? " (재계약)" : string.Empty);
	}

	// In front of the screen's UI but inside its camera's clip range (11-17, tester_1117b: at z -600 under the root the whole
	// overlay was clipped — the scenes' UI cameras draw world z -2 … 2 around themselves and the root scales by 2 / screen
	// height, so -600 is past -2). `share` of the way to the near plane, in the parent's units; -300 when it cannot be told.
	// Public for the other full-screen overlays (Ma9MNegotiation).
	public static float OverlayZ(GameObject parent, Camera cam, float share)
	{
		float scale = parent.transform.lossyScale.z;
		if (cam == null || Mathf.Abs(scale) < 1e-6f || cam.nearClipPlane >= 0f)
		{
			return -300f;
		}
		float world = cam.transform.position.z + cam.nearClipPlane * share;
		float local = (world - parent.transform.position.z) / scale;
		return float.IsNaN(local) || local > -20f || local < -2000f ? -300f : local;
	}

	// The camera that draws a UI root: one of its own children seeing its layer, else any active orthographic camera seeing it.
	// Not NGUITools.FindCameraForLayer: that takes the first active camera whose mask has the layer, and in the lobby that is
	// the ballpark's perspective camera (tester_1117g, 11-17: the overlay went under "UI Root (3D)" at z -300 and was not seen).
	private static Camera CameraOf(UIRoot root)
	{
		int bit = 1 << root.gameObject.layer;
		foreach (Camera c in root.GetComponentsInChildren<Camera>())
		{
			if (c.enabled && (c.cullingMask & bit) != 0)
			{
				return c;
			}
		}
		foreach (Camera c in Camera.allCameras)
		{
			if (c.orthographic && (c.cullingMask & bit) != 0)
			{
				return c;
			}
		}
		return null;
	}

	// The screen's 2D UI root, its camera and a font its labels use. Ranked: active, drawn by an orthographic camera, not the
	// "(3D)" root, then the camera drawn last (highest depth).
	public static GameObject OverlayRoot(out UIFont font, out Camera camera)
	{
		font = null;
		camera = null;
		UIRoot pick = null;
		float best = float.MinValue;
		foreach (UIRoot root in UnityEngine.Object.FindObjectsOfType<UIRoot>())
		{
			Camera cam = CameraOf(root);
			float score = (root.gameObject.activeInHierarchy ? 1000f : 0f)
				+ (cam != null && cam.orthographic ? 500f : 0f)
				+ (root.name.Contains("3D") ? -200f : 0f)
				+ (root.name.Contains("2D") ? 100f : 0f)
				+ (cam != null ? Mathf.Clamp(cam.depth, -50f, 50f) : -60f);
			if (score > best)
			{
				best = score;
				pick = root;
				camera = cam;
			}
		}
		if (pick == null)
		{
			return null;
		}
		foreach (UILabel label in pick.GetComponentsInChildren<UILabel>(true))
		{
			if (label.font != null)
			{
				font = label.font;
				break;
			}
		}
		if (font == null)
		{
			foreach (UILabel label in UnityEngine.Object.FindObjectsOfType<UILabel>())
			{
				if (label.font != null)
				{
					font = label.font;
					break;
				}
			}
		}
		return pick.gameObject;
	}

	// ---- state ------------------------------------------------------------------------------------------------------

	private Info m_info;
	private Action m_done;
	private UIFont m_font;
	private bool m_finished;
	private bool m_escape;
	private bool m_tapped;
	private float m_shake;
	private Vector2 m_min = new Vector2(-480f, -270f), m_max = new Vector2(480f, 270f);
	private Color m_club = new Color(0.1f, 0.2f, 0.45f), m_club2 = new Color(0.85f, 0.1f, 0.15f);
	private GameObject m_scene;
	private readonly List<Texture2D> m_made = new List<Texture2D>();

	private const float ZBack = 0f, ZDeco = -4f, ZPaper = -8f, ZPicture = -12f, ZFront = -16f, ZText = -20f, ZFlash = -26f, ZTop = -30f;

	// ---- the run ----------------------------------------------------------------------------------------------------

	private IEnumerator Run()
	{
		ComputeScreen();
		ClubColours();
		// The whole screen: dark, takes every tap (a tap = on to the next part).
		UITexture back = Box(base.gameObject, Centre(ZBack), Width + 40f, Height + 40f, new Color(0.02f, 0.02f, 0.05f, 0f), true);
		UIEventListener.Get(back.gameObject).onClick = _ => m_tapped = true;
		yield return Fade(back, 0.92f, 0.25f);
		Button(new Vector3(GameRight - 70f, m_max.y - 28f, ZTop), "건너뛰기", Finish);
		AndroidManager.Instance.PushEscapeAction(OnEscape);
		m_escape = true;
		yield return Contract();
		if (!m_info.isRenewal)
		{
			yield return Ceremony();
		}
		Finish();
	}

	private void Update()
	{
		if (m_shake > 0f && m_scene != null)
		{
			m_shake = Mathf.Max(0f, m_shake - Time.deltaTime * 3f);
			float a = m_shake * 14f;
			m_scene.transform.localPosition = new Vector3(UnityEngine.Random.Range(-a, a), UnityEngine.Random.Range(-a, a), 0f);
			if (m_shake <= 0f)
			{
				m_scene.transform.localPosition = Vector3.zero;
			}
		}
	}

	// A wait that a tap cuts short.
	private IEnumerator Wait(float seconds)
	{
		m_tapped = false;
		float end = Time.time + seconds;
		while (Time.time < end && !m_tapped && !m_finished)
		{
			yield return null;
		}
	}

	// ---- 1. 계약서 ---------------------------------------------------------------------------------------------------

	private IEnumerator Contract()
	{
		m_scene = NGUITools.AddChild(base.gameObject);
		m_scene.name = "Contract";
		GameObject paper = NGUITools.AddChild(m_scene);
		paper.name = "Paper";
		float pw = 430f, ph = 400f;
		Vector3 home = new Vector3(0f, -6f, ZPaper);
		paper.transform.localPosition = home + new Vector3(0f, -Height, 0f);
		paper.transform.localRotation = Quaternion.Euler(0f, 0f, 6f);
		Tex(paper, PaperTexture((int)pw, (int)ph), new Vector3(0f, 0f, 0f), pw, ph, Color.white);
		// A clipboard (결재판) under the paper and its metal clip over the top edge, so the tilt reads as a paper laid on a desk
		// (user 11-17 14:2x: "종이가 책상에 놓인 것처럼 의도된 연출이면 상관없음. 그러면 뒤쪽에 결재판이나 갖다대는 것도 추가해둬서").
		Box(paper, new Vector3(10f, -14f, 1.2f), pw + 40f, ph + 64f, new Color(0f, 0f, 0f, 0.4f));
		Box(paper, new Vector3(0f, -6f, 0.8f), pw + 40f, ph + 64f, new Color(0.33f, 0.2f, 0.11f));
		Box(paper, new Vector3(0f, -6f, 0.7f), pw + 30f, ph + 54f, new Color(0.42f, 0.27f, 0.15f));
		Box(paper, new Vector3(0f, ph * 0.5f + 14f, -0.6f), 150f, 38f, new Color(0.55f, 0.57f, 0.6f));
		Box(paper, new Vector3(0f, ph * 0.5f + 18f, -0.7f), 134f, 22f, new Color(0.8f, 0.82f, 0.85f));
		Box(paper, new Vector3(0f, ph * 0.5f + 2f, -0.7f), 104f, 6f, new Color(0.45f, 0.47f, 0.5f));
		Color ink = new Color(0.12f, 0.12f, 0.16f);
		Color soft = new Color(0.35f, 0.33f, 0.3f);
		string head = m_info.isRenewal ? "재 계 약 서" : "선 수 계 약 서";
		// Down by half its size like the other centred words (user 11-17 15:xx: "선수계약서 << 이 단어가 글상자를 삐져나가는 것 같으니
		// 몇 픽셀정도 아래로 내려주고"): drawn half a letter high, its top crossed the frame lines.
		Settle(Text(paper, head, new Vector3(0f, ph * 0.5f - 46f, -1f), 30f, ink, UIWidget.Pivot.Center));
		Box(paper, new Vector3(0f, ph * 0.5f - 74f, -1f), pw - 80f, 2f, m_club);
		// Fewer words rather than smaller letters when long names would need a third line (user 11-17 18:1x: "글씨 크기를 조절하기
		// 보다는" … "스크롤을 생성할지, 글씨 양을 줄일지"): the "(이하 …)" parts go first.
		string club = m_info.toClub ?? "우리 구단", who = m_info.playerName ?? "선수";
		// The particles follow the names before the brackets (11-18, tester 두산 불편 52: "잠실허슬두(이하 "구단")과" → "와").
		string intro = club + "(이하 \"구단\")" + (Batchim(club) ? "과 " : "와 ") + who + "(이하 \"선수\")" + (Batchim(who) ? "은" : "는") + " 다음과 같이 계약한다.";
		UILabel introLabel = Text(paper, intro, new Vector3(-pw * 0.5f + 34f, ph * 0.5f - 104f, -1f), 13f, soft, UIWidget.Pivot.Left, pw - 68f, 2);
		if (introLabel.transform.localScale.x < 13f - 0.01f)
		{
			UnityEngine.Object.Destroy(introLabel.gameObject);
			Text(paper, club + (Batchim(club) ? "과 " : "와 ") + who + (Batchim(who) ? "은" : "는") + " 다음과 같이 계약한다.",
				new Vector3(-pw * 0.5f + 34f, ph * 0.5f - 104f, -1f), 13f, soft, UIWidget.Pivot.Left, pw - 68f, 2);
		}
		SoundManager.PlayEffectSound(eEffectSound.ScreenSlide);
		yield return Move(paper.transform, home, Quaternion.Euler(0f, 0f, -1.5f), 0.45f);
		List<string> terms = new List<string>
		{
			"제1조  계약 기간: " + m_info.seasons + "시즌 (" + m_info.days + "일)",
			// No list price on the paper (user 11-17: "계약서에 정가라는 단어는 붙이지 말아줘 실제 계약서에 저런 단어가 있으면 기분 상할 것 같아").
			"제2조  계약금: " + m_info.priceMp.ToString("N0") + " MP",
			"제3조  " + (m_info.isRenewal ? "선수는 구단에 계속 소속되어 경기에 나선다." : HomeComing() ? "선수는 친정 구단에 돌아와 1군 선수로 뛴다." : "선수는 " + (m_info.fromClub ?? "원래 구단") + (Batchim(m_info.fromClub ?? "원래 구단") ? "을" : "를") + " 떠나 구단의 1군 선수로 뛴다."),
		};
		if (m_info.conditionTop)
		{
			terms.Add("특약  선수는 최상의 컨디션으로 첫 경기를 맞는다.");
		}
		float y = ph * 0.5f - 146f;
		foreach (string line in terms)
		{
			// Made with the whole line first so it is shrunk to the paper, then typed (11-17: 제3조 ran off the paper).
			UILabel label = Text(paper, line, new Vector3(-pw * 0.5f + 34f, y, -1f), 16f, ink, UIWidget.Pivot.Left, pw - 68f);
			label.text = string.Empty;
			label.MarkAsChanged();
			yield return Type(label, line, 0.018f);
			y -= 34f;
		}
		// Signatures: the club's (printed), then the player's, written.
		float sy = -ph * 0.5f + 70f;
		Text(paper, "구단", new Vector3(-pw * 0.5f + 40f, sy + 26f, -1f), 13f, soft, UIWidget.Pivot.Left);
		Text(paper, m_info.toClub ?? string.Empty, new Vector3(-pw * 0.5f + 40f, sy, -1f), 17f, ink, UIWidget.Pivot.Left, 150f);
		Box(paper, new Vector3(-pw * 0.5f + 40f + 75f, sy - 14f, -1f), 150f, 1.5f, soft);
		Text(paper, "선수 서명", new Vector3(30f, sy + 26f, -1f), 13f, soft, UIWidget.Pivot.Left);
		Box(paper, new Vector3(30f + 85f, sy - 14f, -1f), 170f, 1.5f, soft);
		yield return Wait(0.25f);
		Color pen = new Color(0.08f, 0.16f, 0.5f);
		UILabel sign = Text(paper, string.Empty, new Vector3(36f, sy - 2f, -2f), 24f, pen, UIWidget.Pivot.Left, 160f);
		sign.transform.localRotation = Quaternion.Euler(0f, 0f, 4f);
		GameObject nib = NGUITools.AddChild(paper);
		Tex(nib, PenTexture(), new Vector3(10f, 26f, -3f), 18f, 64f, Color.white).transform.localRotation = Quaternion.Euler(0f, 0f, -35f);
		string name = m_info.playerName ?? string.Empty;
		for (int i = 1; i <= name.Length && !m_finished; i++)
		{
			sign.text = name.Substring(0, i);
			sign.MarkAsChanged();
			nib.transform.localPosition = new Vector3(36f + sign.relativeSize.x * 24f, sy - 2f, 0f);
			SoundManager.PlayEffectSound(eEffectSound.Card_Drag);
			yield return new WaitForSeconds(0.16f);
		}
		yield return new WaitForSeconds(0.25f);
		UnityEngine.Object.Destroy(nib);
		// The seal.
		GameObject seal = NGUITools.AddChild(paper);
		seal.name = "Seal";
		Vector3 sealAt = new Vector3(pw * 0.5f - 96f, sy + 64f, -4f);
		seal.transform.localPosition = sealAt;
		seal.transform.localRotation = Quaternion.Euler(0f, 0f, -14f);
		Color red = new Color(0.82f, 0.08f, 0.1f);
		UITexture ring = Tex(seal, SealTexture(), Vector3.zero, 130f, 130f, red);
		UILabel done = Settle(Text(seal, m_info.isRenewal ? "재계약\n완료" : "계약\n완료", new Vector3(0f, 0f, -1f), 26f, red, UIWidget.Pivot.Center, 0f, 2));
		seal.transform.localScale = new Vector3(3f, 3f, 1f);
		ring.alpha = 0f;
		done.alpha = 0f;
		for (float t = 0f; t < 1f && !m_finished; t += Time.deltaTime / 0.16f)
		{
			float s = Mathf.Lerp(3f, 1f, t * t);
			seal.transform.localScale = new Vector3(s, s, 1f);
			ring.alpha = done.alpha = Mathf.Clamp01(t * 2f);
			yield return null;
		}
		seal.transform.localScale = Vector3.one;
		ring.alpha = done.alpha = 0.92f;
		SoundManager.PlayEffectSound(eEffectSound.Scoreboard_Oneshot);
		m_shake = 1f;
		Dust(paper, sealAt, red);
		// On only at a tap (user 11-17 14:2x: "계약서 사인하는 장면 자동으로 안 넘어가고 터치해야 넘어가게 해줘"); [건너뛰기] and
		// the back key still end it.
		yield return new WaitForSeconds(0.35f);
		UILabel hint = Settle(Text(base.gameObject, "탭하여 계속", new Vector3(GameRight - 60f, m_min.y + 20f, ZTop), 15f, new Color(1f, 1f, 1f, 0.9f), UIWidget.Pivot.Center));
		StartCoroutine(Blink(hint));
		m_tapped = false;
		while (!m_tapped && !m_finished)
		{
			yield return null;
		}
		if (hint != null)
		{
			UnityEngine.Object.Destroy(hint.gameObject);
		}
		// Away it goes (the 재계약 ends here).
		yield return Move(paper.transform, home + new Vector3(Width, 60f, 0f), Quaternion.Euler(0f, 0f, -18f), 0.4f);
		UnityEngine.Object.Destroy(m_scene);
		m_scene = null;
	}

	// ---- 2. 입단식 ---------------------------------------------------------------------------------------------------

	private IEnumerator Ceremony()
	{
		m_scene = NGUITools.AddChild(base.gameObject);
		m_scene.name = "Ceremony";
		// The stage: the club's colours top to bottom, a floor, spotlights.
		UITexture stage = Tex(m_scene, Gradient(m_club * 0.55f, m_club * 0.12f), Centre(ZBack - 1f), Width + 40f, Height + 40f, Color.white);
		stage.alpha = 0f;
		Box(m_scene, new Vector3(0f, m_min.y + 40f, ZDeco), Width + 40f, 80f, new Color(0f, 0f, 0f, 0.45f));
		Box(m_scene, new Vector3(0f, m_min.y + 80f, ZDeco), Width + 40f, 3f, new Color(m_club2.r, m_club2.g, m_club2.b, 0.8f));
		// The club's emblem large and faint behind.
		GameObject emblem = Emblem(m_scene, m_info.toEmblem, "100x102", new Vector3(0f, 40f, ZDeco - 1f), 3.2f);
		List<Transform> beams = new List<Transform>();
		Texture2D beamTex = BeamTexture();
		for (int i = 0; i < 4; i++)
		{
			GameObject b = NGUITools.AddChild(m_scene);
			b.name = "Beam";
			float x = -330f + i * 220f;
			b.transform.localPosition = new Vector3(x, m_max.y + 10f, ZDeco - 2f);
			Tex(b, beamTex, new Vector3(0f, -260f, 0f), 170f, 540f, new Color(1f, 0.97f, 0.85f, 0.16f));
			beams.Add(b.transform);
		}
		StartCoroutine(Sway(beams));
		yield return Fade(stage, 1f, 0.35f);
		// Title.
		UILabel title = Text(m_scene, (m_info.toClub ?? string.Empty) + " 입단식", new Vector3(0f, m_max.y - 44f, ZText), 30f, Color.white, UIWidget.Pivot.Center, GameWidth - 200f);
		title.effectStyle = UILabel.Effect.Shadow;
		title.effectColor = new Color(0f, 0f, 0f, 0.8f);
		UILabel sub = Text(m_scene, "FA " + (m_info.playerName ?? string.Empty) + " 영입", new Vector3(0f, m_max.y - 76f, ZText), 18f, Gold, UIWidget.Pivot.Center);
		SoundManager.PlayEffectSound(eEffectSound.Combine_Over7);
		// The player, in from below.
		GameObject who = NGUITools.AddChild(m_scene);
		who.name = "Player";
		Vector3 whoAt = new Vector3(-150f, 4f, ZPicture);
		who.transform.localPosition = whoAt + new Vector3(0f, -90f, 0f);
		Detail(who, m_info.cardIdx, 1.75f);
		StartCoroutine(FramePicture(who));
		StartCoroutine(Move(who.transform, whoAt, Quaternion.identity, 0.5f));
		yield return Wait(0.6f);
		// The jersey, presented: it rises and turns to show the back with his name.
		GameObject jersey = NGUITools.AddChild(m_scene);
		jersey.name = "Jersey";
		Vector3 jerseyAt = new Vector3(130f, 22f, ZFront);
		jersey.transform.localPosition = jerseyAt + new Vector3(0f, -260f, 0f);
		Tex(jersey, JerseyTexture(), Vector3.zero, 230f, 190f, Color.white);
		UILabel back = Text(jersey, (m_info.playerName ?? string.Empty), new Vector3(0f, 34f, -1f), 26f, Color.white, UIWidget.Pivot.Center, 110f);
		back.effectStyle = UILabel.Effect.Outline;
		back.effectColor = m_club2 * 0.6f;
		Emblem(jersey, m_info.toEmblem, "38x29", new Vector3(0f, -10f, -1f), 1.3f);
		Text(jersey, m_info.toClub ?? string.Empty, new Vector3(0f, -52f, -1f), 14f, new Color(1f, 1f, 1f, 0.9f), UIWidget.Pivot.Center, 110f);
		yield return Move(jersey.transform, jerseyAt, Quaternion.Euler(0f, 0f, -4f), 0.55f);
		SoundManager.PlayEffectSound(eEffectSound.AcecardRegister);
		// Flashes.
		for (int i = 0; i < 7 && !m_finished; i++)
		{
			Flash(new Vector3(UnityEngine.Random.Range(-GameRight + 60f, GameRight - 60f), UnityEngine.Random.Range(-60f, m_max.y - 100f), ZFlash), i % 3 == 0);
			SoundManager.PlayEffectSound(eEffectSound.CardOpen);
			yield return new WaitForSeconds(UnityEngine.Random.Range(0.12f, 0.3f));
		}
		StartCoroutine(Confetti());
		// The terms on a ribbon.
		UITexture ribbon = Box(m_scene, new Vector3(0f, m_min.y + 40f, ZFront), Width + 40f, 46f, new Color(m_club2.r, m_club2.g, m_club2.b, 0.92f));
		string terms = m_info.seasons + "시즌 (" + m_info.days + "일) · 계약금 " + m_info.priceMp.ToString("N0") + " MP" + (m_info.conditionTop ? " · 컨디션 최상" : string.Empty);
		Settle(Text(m_scene, terms, new Vector3(0f, m_min.y + 40f, ZText), 18f, Color.white, UIWidget.Pivot.Center, GameWidth - 200f));
		ribbon.alpha = 0.92f;
		// The reporter and the player.
		string q = Question();
		string a = Answer();
		UILabel ql = Bubble(new Vector3(-30f, -98f, ZText), "기자", q, new Color(0.95f, 0.95f, 0.98f), new Color(0.15f, 0.15f, 0.2f));
		yield return Type(ql, q, 0.03f);
		yield return Wait(0.5f);
		UILabel al = Bubble(new Vector3(-30f, -137f, ZText), m_info.playerName ?? "선수", a, Gold, new Color(0.12f, 0.08f, 0f));
		yield return Type(al, a, 0.035f);
		// More flashes on the answer, then wait for a tap.
		for (int i = 0; i < 3 && !m_finished; i++)
		{
			Flash(new Vector3(UnityEngine.Random.Range(-GameRight + 60f, GameRight - 60f), UnityEngine.Random.Range(-40f, m_max.y - 100f), ZFlash), false);
			yield return new WaitForSeconds(0.2f);
		}
		// On the terms ribbon's right end (11-17: above it the hint sat on the answer).
		UILabel hint = Settle(Text(m_scene, "탭하여 계속", new Vector3(GameRight - 60f, m_min.y + 40f, ZText - 1f), 15f, new Color(1f, 1f, 1f, 0.85f), UIWidget.Pivot.Center));
		StartCoroutine(Blink(hint));
		// On only at a tap, like the contract (11-18, tester 두산 불편 50: "탭하여 계속" closed by itself after 12 s).
		m_tapped = false;
		while (!m_tapped && !m_finished)
		{
			yield return null;
		}
		if (emblem == null || sub == null)
		{
			yield break;
		}
	}

	private string Question()
	{
		string[] q =
		{
			(m_info.toClub ?? "구단") + (Batchim(m_info.toClub ?? "구단") ? "을" : "를") + " 고른 이유는 무엇인가요?",
			"새 유니폼을 입은 소감 한마디 부탁드립니다.",
			(m_info.toClub ?? "구단") + " 팬들에게 한마디 해 주세요.",
		};
		return q[Mathf.Abs((m_info.cardIdx + m_info.days) % q.Length)];
	}

	// The answer by how the deal went.
	private string Answer()
	{
		string club = m_info.toClub ?? "구단";
		// The gauge is at least 100 at any signing (11-17, systems session): only 110+ (컨디션 최상) counts as delighted.
		if (m_info.conditionTop || m_info.satisfaction >= 110)
		{
			return "꼭 오고 싶던 팀입니다. 몸 상태도 최고예요. 첫 경기부터 보여 드리겠습니다!";
		}
		if (m_info.listPriceMp > 0 && m_info.priceMp < m_info.listPriceMp * 9 / 10)
		{
			return "조건보다 " + club + "에서 뛰고 싶은 마음이 컸습니다. 실력으로 증명하겠습니다.";
		}
		if (m_info.listPriceMp > 0 && m_info.priceMp > m_info.listPriceMp * 11 / 10)
		{
			return "좋은 대우를 해 주신 만큼 그 이상으로 갚겠습니다. 우승이 목표입니다.";
		}
		// The extended contract: 6시즌 since 11-17 16:5x (base 4시즌, "+2시즌" card; was 8 over a base of 6).
		if (m_info.seasons >= 6)
		{
			return "오래 함께하자는 믿음을 주셨습니다. " + club + "에서 끝까지 뛰겠습니다.";
		}
		return "기대에 꼭 보답하겠습니다. " + club + "의 우승을 위해 모든 걸 걸겠습니다.";
	}

	// ---- parts ------------------------------------------------------------------------------------------------------

	private float Width => m_max.x - m_min.x;

	private float Height => m_max.y - m_min.y;

	private Vector3 Centre(float z) => new Vector3((m_min.x + m_max.x) * 0.5f, (m_min.y + m_max.y) * 0.5f, z);

	private static readonly Color Gold = new Color(1f, 0.84f, 0.38f);

	// The game's own 800-wide middle (the design is 800 × 480; wider screens get side margins): buttons and words stay
	// inside it, backdrops cover the whole screen (11-17, systems session: [건너뛰기] sat in the right margin).
	private float GameRight => Mathf.Min(m_max.x, 400f);

	private float GameWidth => Mathf.Min(Width, 800f);

	private void ComputeScreen()
	{
		// The whole screen in the root's units: the UIRoot's height (480 on screens wider than 5:3) times the screen's
		// aspect, as Ma9MNegotiation does; the camera reading is the fallback.
		UIRoot root = NGUITools.FindInParents<UIRoot>(base.gameObject);
		if (root != null && root.activeHeight > 100)
		{
			float h = root.activeHeight, w = h * Screen.width / Mathf.Max(1f, Screen.height);
			Vector3 at = base.transform.localPosition;
			m_min = new Vector2(-w * 0.5f - at.x, -h * 0.5f - at.y);
			m_max = new Vector2(w * 0.5f - at.x, h * 0.5f - at.y);
			return;
		}
		Camera cam = NGUITools.FindCameraForLayer(base.gameObject.layer);
		if (cam == null || !cam.orthographic)
		{
			return;
		}
		Vector3 a = base.transform.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(0f, 0f, 0f)));
		Vector3 b = base.transform.InverseTransformPoint(cam.ViewportToWorldPoint(new Vector3(1f, 1f, 0f)));
		if (Mathf.Abs(b.x - a.x) < 100f || Mathf.Abs(b.y - a.y) < 100f)
		{
			return;
		}
		m_min = new Vector2(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y));
		m_max = new Vector2(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
	}

	// The club's two colours (by the club its emblem is of; a season's logo counts as its club).
	private void ClubColours()
	{
		int b = Ma9MEmblemYears.BaseOf(m_info.toEmblem);
		Color[][] p =
		{
			new[] { C(19, 30, 70), C(237, 28, 36) },    // 0 두산
			new[] { C(200, 16, 46), C(30, 30, 30) },    // 1 해태
			new[] { C(255, 102, 0), C(40, 40, 40) },    // 2 한화
			new[] { C(128, 0, 32), C(210, 210, 210) },  // 3 히어로즈
			new[] { C(0, 94, 170), C(240, 190, 30) },   // 4 현대
			new[] { C(234, 0, 41), C(20, 20, 20) },     // 5 KIA
			new[] { C(195, 0, 47), C(30, 30, 30) },     // 6 LG
			new[] { C(4, 30, 66), C(209, 16, 52) },     // 7 롯데
			new[] { C(7, 76, 161), C(230, 230, 240) },  // 8 삼성
			new[] { C(206, 14, 45), C(255, 180, 0) },   // 9 SK·SSG
			new[] { C(0, 60, 140), C(200, 0, 0) },      // 10 OB
			new[] { C(190, 30, 45), C(20, 40, 90) },    // 11 쌍방울
			new[] { C(0, 56, 168), C(210, 20, 40) },    // 12 국가대표
			new[] { C(7, 30, 61), C(174, 143, 77) },    // 13 NC
			new[] { C(255, 110, 0), C(30, 60, 140) },   // 14 빙그레
			new[] { C(0, 120, 70), C(240, 200, 40) },   // 15 태평양
			new[] { C(0, 70, 160), C(220, 30, 40) },    // 16 MBC
			new[] { C(0, 100, 180), C(240, 240, 240) }, // 17 청보
			new[] { C(0, 80, 170), C(230, 40, 40) },    // 18 삼미
			new[] { C(20, 20, 20), C(237, 28, 36) },    // 19 kt
		};
		if (b >= 0 && b < p.Length)
		{
			m_club = p[b][0];
			m_club2 = p[b][1];
		}
	}

	private static Color C(int r, int g, int b) => new Color(r / 255f, g / 255f, b / 255f);

	private UITexture Box(GameObject parent, Vector3 pos, float width, float height, Color colour, bool blocks = false)
	{
		UITexture box = Tex(parent, Texture2D.whiteTexture, pos, width, height, colour);
		if (blocks)
		{
			box.gameObject.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 0f);
		}
		return box;
	}

	private UITexture Tex(GameObject parent, Texture texture, Vector3 pos, float width, float height, Color colour)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Tex";
		UITexture t = go.AddComponent<UITexture>();
		t.shader = Shader.Find("Unlit/Transparent Colored");
		t.mainTexture = texture;
		t.color = colour;
		go.transform.localPosition = pos;
		go.transform.localScale = new Vector3(width, height, 1f);
		return t;
	}

	// A label in the screen's own font. width > 0: one line shrinks to it, more lines wrap at it.
	private UILabel Text(GameObject parent, string text, Vector3 pos, float size, Color colour, UIWidget.Pivot pivot, float width = 0f, int lines = 1)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Text";
		UILabel label = go.AddComponent<UILabel>();
		label.font = m_font;
		label.supportEncoding = false;
		label.color = colour;
		label.maxLineCount = lines;
		// In the parent's units: UILabel divides by its scale when it wraps.
		label.lineWidth = (lines > 1 && width > 0f) ? Mathf.RoundToInt(width) : 0;
		label.text = text ?? string.Empty;
		go.transform.localScale = new Vector3(size, size, 1f);
		// The pivot before the place (this NGUI's pivot setter moves the widget).
		label.pivot = pivot;
		go.transform.localPosition = pos;
		if (width > 0f && lines <= 1 && !string.IsNullOrEmpty(text))
		{
			float wide = label.relativeSize.x * size;
			if (wide > width)
			{
				float fit = Mathf.Max(size * 0.6f, size * width / wide);
				go.transform.localScale = new Vector3(fit, fit, 1f);
			}
		}
		else if (width > 0f && lines > 1 && !string.IsNullOrEmpty(text))
		{
			// Wrapped text cut at maxLineCount (a long club or player name) shrinks until it all fits (11-17, CLAUDE.md rule 11:
			// every text box checked for overflow before anyone points it out).
			string want = text.Replace("\n", string.Empty).Replace(" ", string.Empty);
			float s = size;
			for (int i = 0; i < 6 && label.processedText.Replace("\n", string.Empty).Replace(" ", string.Empty).Length < want.Length; i++)
			{
				s = Mathf.Max(size * 0.6f, s * 0.92f);
				go.transform.localScale = new Vector3(s, s, 1f);
				label.MarkAsChanged();
			}
		}
		label.MarkAsChanged();
		return label;
	}

	// The borrowed font draws its letters about half a letter above the label's own middle (11-17: the user's phone showed the
	// lobby news band's words half cut; here [건너뛰기], the ribbon and the seal sat high in their boxes by 0.48-0.57 of the size).
	// For words centred in a box: down by half the label's (final) size.
	private const float GlyphLift = 0.5f;

	// Whether a word ends in a final consonant (과/와, 은/는); a name ending in a letter or digit (윤석민A) counts as none.
	private static bool Batchim(string word)
	{
		if (string.IsNullOrEmpty(word))
		{
			return false;
		}
		char c = word[word.Length - 1];
		return c >= '가' && c <= '힣' && (c - '가') % 28 != 0;
	}

	private static UILabel Settle(UILabel label)
	{
		if (label != null)
		{
			Transform t = label.transform;
			t.localPosition -= new Vector3(0f, t.localScale.y * GlyphLift, 0f);
		}
		return label;
	}

	// A speech line: who, then the words (typed by the caller).
	private UILabel Bubble(Vector3 pos, string who, string words, Color back, Color ink)
	{
		float w = 470f;
		Box(m_scene, pos + new Vector3(w * 0.5f - 60f, 0f, 2f), w + 20f, 38f, new Color(back.r, back.g, back.b, 0.93f));
		Settle(Text(m_scene, who, pos + new Vector3(-56f, 0f, 0f), 13f, ink * 0.8f, UIWidget.Pivot.Left, 54f));
		// Made with the whole line first so it is shrunk to the bubble (typed letter by letter after; 11-17: the answer ran out).
		UILabel line = Settle(Text(m_scene, words, pos + new Vector3(2f, 0f, 0f), 15f, ink, UIWidget.Pivot.Left, w - 76f));
		line.text = string.Empty;
		line.MarkAsChanged();
		return line;
	}

	private GameObject Emblem(GameObject parent, int index, string size, Vector3 pos, float scale)
	{
		if (!Ma9MEmblemYears.IsDrawable(index))
		{
			return null;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "Emblem";
		holder.transform.localPosition = pos;
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		GameObject go = NGUITools.AddChild(holder);
		UITexture picture = go.AddComponent<UITexture>();
		picture.shader = Shader.Find("Unlit/Transparent Colored");
		try
		{
			go.AddComponent<global::Emblem>().SetEmblem(size, index);
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MFaCeremony] emblem " + index + ": " + e.Message);
		}
		return holder;
	}

	// The player's large picture (the card detail's, 128 units at scale 1).
	private void Detail(GameObject parent, int card, float scale)
	{
		if (card <= 0 || PlayerCardDrawer.instance == null)
		{
			return;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "Detail";
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		try
		{
			PlayerCardDrawer.instance.MakeCardDetailImageObject(card, holder);
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MFaCeremony] picture " + card + ": " + e.Message);
		}
	}

	// 11-20 (AI 테스터 kt 18·두산 56, 남은일 0-2: kt 출신 FA를 kt가 영입하면 계약서가 "kt를 떠나"): the FA card's source club
	// (Ma9MSortMemory.FaSourceTeam) and ours (the club whose uniform signed FAs wear, Ma9MFaArt.Club, else our emblem's club) in
	// the same franchise (OB → 두산 counts as one, Ma9MEmblemYears.Franchise) make him come home instead.
	private bool HomeComing()
	{
		int from = Ma9MSortMemory.FaSourceTeam(m_info.cardIdx);
		int ours = Ma9MFaArt.Club >= 0 ? Ma9MFaArt.Club : Ma9MEmblemYears.BaseOf(m_info.toEmblem);
		int f = Ma9MEmblemYears.Franchise(from);
		return f >= 0 && f == Ma9MEmblemYears.Franchise(ours);
	}

	// 11-20 (AI 테스터 두산 50, 14회차 "FA 카드 그림은 여전히 오른쪽 · 아래가 곧게 잘려 방망이 끝과 몸 아래가 네모로 끊긴다"): the
	// card's detail art is itself cut square at the right and the bottom (it is made for the player screen's picture box), so it
	// gets a portrait frame on exactly its own rectangle: a gold edge and the club colour inside, and the cuts become the frame's
	// edges. The art may arrive a moment later (lazy card textures), so the bounds are polled for up to 2 s.
	private IEnumerator FramePicture(GameObject who)
	{
		Bounds b = new Bounds();
		for (float t = 0f; t < 2f && !m_finished && who != null; t += Time.deltaTime)
		{
			b = NGUIMath.CalculateRelativeWidgetBounds(who.transform, who.transform);
			if (b.size.x > 20f && b.size.y > 20f)
			{
				break;
			}
			yield return null;
		}
		if (who == null || m_finished || b.size.x <= 20f || b.size.y <= 20f)
		{
			yield break;
		}
		Vector3 c = new Vector3(b.center.x, b.center.y, 2f);
		Box(who, c + new Vector3(6f, -8f, 0.6f), b.size.x + 16f, b.size.y + 16f, new Color(0f, 0f, 0f, 0.45f));
		Box(who, c + new Vector3(0f, 0f, 0.4f), b.size.x + 12f, b.size.y + 12f, Gold);
		Tex(who, Gradient(m_club * 0.75f, m_club * 0.3f), c, b.size.x, b.size.y, Color.white);
	}

	private void Button(Vector3 pos, string text, Action onTap)
	{
		UITexture b = Box(base.gameObject, pos, 110f, 34f, new Color(0f, 0f, 0f, 0.55f), true);
		Settle(Text(base.gameObject, text, pos + new Vector3(0f, 0f, -1f), 15f, new Color(1f, 1f, 1f, 0.9f), UIWidget.Pivot.Center));
		UIEventListener.Get(b.gameObject).onClick = _ => onTap();
	}

	// ---- motion -----------------------------------------------------------------------------------------------------

	private IEnumerator Fade(UITexture t, float to, float seconds)
	{
		float from = t.alpha;
		for (float s = 0f; s < 1f && !m_finished; s += Time.deltaTime / seconds)
		{
			t.alpha = Mathf.Lerp(from, to, s);
			yield return null;
		}
		t.alpha = to;
	}

	private IEnumerator Move(Transform t, Vector3 to, Quaternion turn, float seconds)
	{
		Vector3 from = t.localPosition;
		Quaternion fromTurn = t.localRotation;
		for (float s = 0f; s < 1f && !m_finished && t != null; s += Time.deltaTime / seconds)
		{
			float e = 1f - (1f - s) * (1f - s) * (1f - s);   // ease out
			t.localPosition = Vector3.LerpUnclamped(from, to, e);
			t.localRotation = Quaternion.Slerp(fromTurn, turn, e);
			yield return null;
		}
		if (t != null)
		{
			t.localPosition = to;
			t.localRotation = turn;
		}
	}

	private IEnumerator Type(UILabel label, string text, float perLetter)
	{
		for (int i = 1; i <= text.Length && !m_finished; i++)
		{
			if (m_tapped)
			{
				i = text.Length;
				m_tapped = false;
			}
			label.text = text.Substring(0, i);
			label.MarkAsChanged();
			yield return new WaitForSeconds(perLetter);
		}
		label.text = text;
		label.MarkAsChanged();
	}

	private IEnumerator Sway(List<Transform> beams)
	{
		float t0 = Time.time;
		while (!m_finished && m_scene != null)
		{
			for (int i = 0; i < beams.Count; i++)
			{
				if (beams[i] != null)
				{
					beams[i].localRotation = Quaternion.Euler(0f, 0f, Mathf.Sin((Time.time - t0) * 0.9f + i * 1.7f) * 18f);
				}
			}
			yield return null;
		}
	}

	// 11-20 (AI 테스터 NC 76·두산 50, 남은일 0-2: "탭하여 계속" 글씨가 어두워 거의 안 보임): the old blink went 0.05-0.85, dark on the
	// red ribbon half the time. Now it breathes 0.7-1.0 with a dark outline, so it reads on the ribbon and on the paper alike.
	private IEnumerator Blink(UILabel label)
	{
		if (label != null)
		{
			label.color = Color.white;
			label.effectStyle = UILabel.Effect.Outline;
			label.effectColor = new Color(0f, 0f, 0f, 0.85f);
		}
		while (label != null && !m_finished)
		{
			label.alpha = 0.85f + 0.15f * Mathf.Sin(Time.time * 4f);
			yield return null;
		}
	}

	// A camera flash: a star burst where it pops, and a faint white over the screen for the big ones.
	private void Flash(Vector3 at, bool big)
	{
		StartCoroutine(FlashRun(at, big));
	}

	private IEnumerator FlashRun(Vector3 at, bool big)
	{
		UITexture star = Tex(m_scene, GlowTexture(), at, 90f, 90f, Color.white);
		UITexture screen = big ? Box(m_scene, Centre(ZFlash - 1f), Width + 40f, Height + 40f, new Color(1f, 1f, 1f, 0.55f)) : null;
		for (float s = 0f; s < 1f && !m_finished; s += Time.deltaTime / 0.28f)
		{
			float k = 1f - s;
			if (star != null)
			{
				star.alpha = k;
				star.transform.localScale = new Vector3(90f + 80f * s, 90f + 80f * s, 1f);
			}
			if (screen != null)
			{
				screen.alpha = 0.55f * k * k;
			}
			yield return null;
		}
		if (star != null)
		{
			UnityEngine.Object.Destroy(star.gameObject);
		}
		if (screen != null)
		{
			UnityEngine.Object.Destroy(screen.gameObject);
		}
	}

	private void Dust(GameObject parent, Vector3 at, Color colour)
	{
		for (int i = 0; i < 10; i++)
		{
			float a = i * Mathf.PI * 2f / 10f;
			StartCoroutine(Bit(parent, at, new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * UnityEngine.Random.Range(60f, 110f), colour, 0.5f, 6f));
		}
	}

	private IEnumerator Confetti()
	{
		Color[] colours = { m_club2, Color.white, Gold, Color.Lerp(m_club, Color.white, 0.35f) };
		for (int i = 0; i < 70 && !m_finished && m_scene != null; i++)
		{
			Vector3 start = new Vector3(UnityEngine.Random.Range(m_min.x, m_max.x), m_max.y + 20f, ZFlash + 2f);
			Vector3 fall = new Vector3(UnityEngine.Random.Range(-60f, 60f), -Height - 60f, 0f);
			StartCoroutine(Bit(m_scene, start, fall, colours[i % colours.Length], UnityEngine.Random.Range(2.2f, 3.6f), UnityEngine.Random.Range(6f, 11f)));
			yield return new WaitForSeconds(0.035f);
		}
	}

	// One flying bit (dust, confetti): from `at` along `by` over `seconds`, turning and fading at the end.
	private IEnumerator Bit(GameObject parent, Vector3 at, Vector3 by, Color colour, float seconds, float size)
	{
		UITexture b = Box(parent, at, size, size * 0.6f, colour);
		float spin = UnityEngine.Random.Range(-360f, 360f);
		for (float s = 0f; s < 1f && !m_finished && b != null; s += Time.deltaTime / seconds)
		{
			b.transform.localPosition = at + by * s + new Vector3(Mathf.Sin(s * 9f) * 8f, 0f, 0f);
			b.transform.localRotation = Quaternion.Euler(0f, 0f, spin * s);
			b.alpha = s < 0.8f ? 1f : (1f - s) * 5f;
			yield return null;
		}
		if (b != null)
		{
			UnityEngine.Object.Destroy(b.gameObject);
		}
	}

	// ---- pictures (made here, freed at the end) ---------------------------------------------------------------------

	private Texture2D Made(int w, int h)
	{
		Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
		t.wrapMode = TextureWrapMode.Clamp;
		m_made.Add(t);
		return t;
	}

	// Cream paper, a faint grain, a ruled border in the club's colour.
	// The paper at its size on screen (11-17: a 128 × 120 picture stretched 3.4 times blurred its one-pixel frame lines unevenly
	// once tilted, and the user saw the paper as bent): a 3-unit club-colour frame 14 in, a 1.5-unit second-colour line inside it,
	// lines that stop at the corners.
	private Texture2D PaperTexture(int w, int h)
	{
		Texture2D t = Made(w, h);
		System.Random r = new System.Random(m_info.cardIdx);
		Color paper = new Color(0.97f, 0.94f, 0.86f);
		Color32[] px = new Color32[w * h];
		bool Line(int x, int y, int from, int width)
		{
			int dx = Mathf.Min(x, w - 1 - x), dy = Mathf.Min(y, h - 1 - y);
			bool onX = dx >= from && dx < from + width && dy >= from;
			bool onY = dy >= from && dy < from + width && dx >= from;
			return onX || onY;
		}
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				Color c = paper * (1f - (float)r.NextDouble() * 0.035f);
				if (Line(x, y, 14, 3))
				{
					c = Color.Lerp(c, m_club, 0.85f);
				}
				else if (Line(x, y, 20, 2))
				{
					c = Color.Lerp(c, m_club2, 0.55f);
				}
				c.a = 1f;
				px[y * w + x] = c;
			}
		}
		t.SetPixels32(px);
		t.Apply();
		return t;
	}

	// A ring (the seal), white to be coloured: two circles.
	private Texture2D SealTexture()
	{
		int n = 96;
		Texture2D t = Made(n, n);
		float c = (n - 1) * 0.5f;
		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
				float a = Band(d, 0.9f, 0.98f) + Band(d, 0.78f, 0.83f);
				// A worn stamp: some holes.
				if (((x * 7 + y * 13) % 17) == 0)
				{
					a *= 0.4f;
				}
				t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
			}
		}
		t.Apply();
		return t;
	}

	private static float Band(float d, float a, float b)
	{
		float soft = 0.015f;
		return Mathf.Clamp01((d - a) / soft) * Mathf.Clamp01((b - d) / soft);
	}

	// A pen: a dark barrel and a gold nib, upright.
	private Texture2D PenTexture()
	{
		int w = 8, h = 32;
		Texture2D t = Made(w, h);
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				bool nib = y < 6 && Mathf.Abs(x - 3.5f) < (y + 1) * 0.6f;
				bool body = y >= 6;
				Color c = nib ? new Color(0.9f, 0.75f, 0.3f, 1f) : body ? new Color(0.08f, 0.08f, 0.12f, 1f) : new Color(0f, 0f, 0f, 0f);
				if (body && (x == 0 || x == w - 1))
				{
					c.a = 0.6f;
				}
				t.SetPixel(x, y, c);
			}
		}
		t.Apply();
		return t;
	}

	// Top to bottom.
	private Texture2D Gradient(Color top, Color bottom)
	{
		Texture2D t = Made(2, 64);
		for (int y = 0; y < 64; y++)
		{
			Color c = Color.Lerp(bottom, top, y / 63f);
			c.a = 1f;
			t.SetPixel(0, y, c);
			t.SetPixel(1, y, c);
		}
		t.Apply();
		return t;
	}

	// A spotlight's cone: narrow at the top, soft edges, fading down.
	private Texture2D BeamTexture()
	{
		int w = 64, h = 128;
		Texture2D t = Made(w, h);
		// Row 0 is the bottom: wide and faint there, narrow and bright at the top (where the lamp is).
		for (int y = 0; y < h; y++)
		{
			float v = y / (float)(h - 1);
			float half = Mathf.Lerp(0.5f, 0.06f, v);
			for (int x = 0; x < w; x++)
			{
				float u = Mathf.Abs(x - (w - 1) * 0.5f) / ((w - 1) * 0.5f);
				float a = Mathf.Clamp01((half - u * 0.5f) / Mathf.Max(0.02f, half * 0.4f));
				a *= Mathf.Lerp(0.15f, 1f, v);
				t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
			}
		}
		t.Apply();
		return t;
	}

	// A soft round glow with four rays (the flash).
	private Texture2D GlowTexture()
	{
		int n = 64;
		Texture2D t = Made(n, n);
		float c = (n - 1) * 0.5f;
		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				float dx = (x - c) / c, dy = (y - c) / c;
				float d = Mathf.Sqrt(dx * dx + dy * dy);
				float a = Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d);
				a += Mathf.Clamp01(1f - Mathf.Abs(dx) * 14f) * Mathf.Clamp01(1f - Mathf.Abs(dy)) * 0.7f;
				a += Mathf.Clamp01(1f - Mathf.Abs(dy) * 14f) * Mathf.Clamp01(1f - Mathf.Abs(dx)) * 0.7f;
				t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(a)));
			}
		}
		t.Apply();
		return t;
	}

	// A jersey seen from the back in the club's colour, white trim (name and emblem are labels on top).
	private Texture2D JerseyTexture()
	{
		int w = 116, h = 106;
		Texture2D t = Made(w, h);
		Color body = m_club, trim = Color.Lerp(m_club2, Color.white, 0.25f);
		// A baseball jersey laid flat, seen from the back (v 0 = the hem): the shoulders slope from the collar, short sleeves go
		// out and down from them (11-17 first draft: flat shoulders and sleeves to the top corners read as a cape).
		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float u = x / (float)(w - 1), v = y / (float)(h - 1);
				float side = Mathf.Abs(u - 0.5f);                 // 0 in the middle, 0.5 at the edges
				float shoulder = 0.9f - Mathf.Max(0f, side - 0.08f) * 0.55f;   // the top line: flat at the collar, sloping out
				bool torso = side < 0.24f && v < shoulder;
				// A sleeve: a band of even depth from the torso's side (side 0.24) out to side 0.42, sloping down at about 40°; the
				// last bit is the cuff.
				float ext = side - 0.24f;
				bool sleeve = ext >= 0f && ext < 0.18f && v < 0.812f - ext * 0.9f && v > 0.6f - ext * 0.9f;
				bool cuff = sleeve && ext > 0.15f;
				bool collar = side < 0.1f && v > shoulder - 0.045f && v < shoulder + 0.01f && v < 0.9f;
				Color c = new Color(0f, 0f, 0f, 0f);
				if (torso || sleeve)
				{
					c = body;
					// Trim: the hem, the cuffs and piping down the sides.
					if ((torso && v < 0.035f) || cuff || (torso && Mathf.Abs(side - 0.235f) < 0.008f))
					{
						c = trim;
					}
					// A soft fold shade down the middle.
					c *= 1f - 0.08f * Mathf.Clamp01(1f - side * 6f);
					c.a = 1f;
				}
				if (collar)
				{
					c = trim;
					c.a = 1f;
				}
				t.SetPixel(x, y, c);
			}
		}
		t.Apply();
		return t;
	}

	// ---- the end ----------------------------------------------------------------------------------------------------

	private void OnEscape()
	{
		Finish();
	}

	private void Finish()
	{
		if (m_finished)
		{
			return;
		}
		m_finished = true;
		if (m_escape)
		{
			m_escape = false;
			AndroidManager.Instance.PopEscapeAction();
			AndroidManager.Instance.EscapeEnable = true;
		}
		if (s_live == this)
		{
			s_live = null;
		}
		Action done = m_done;
		m_done = null;
		UnityEngine.Object.Destroy(base.gameObject);
		try
		{
			done?.Invoke();
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MFaCeremony] done: " + e);
		}
	}

	private void OnDestroy()
	{
		foreach (Texture2D t in m_made)
		{
			if (t != null)
			{
				UnityEngine.Object.Destroy(t);
			}
		}
		m_made.Clear();
		// The screen went away under it (a scene change): the signing must still be sent.
		if (!m_finished)
		{
			m_finished = true;
			if (m_escape)
			{
				AndroidManager.Instance.PopEscapeAction();
				AndroidManager.Instance.EscapeEnable = true;
			}
			if (s_live == this)
			{
				s_live = null;
			}
			Action done = m_done;
			m_done = null;
			try
			{
				done?.Invoke();
			}
			catch (Exception e)
			{
				Debug.Log("[Ma9MFaCeremony] done on destroy: " + e);
			}
		}
	}
}
