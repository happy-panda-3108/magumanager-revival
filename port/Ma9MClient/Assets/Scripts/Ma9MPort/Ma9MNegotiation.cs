using System;
using System.Collections;
using System.Collections.Generic;
using Ma9MPackets;
using UnityEngine;

// FA 에이전트 협상 (systems session, 2026-11-17; user 09:3x: "에이전트 협상으로 게이지 채우는 것 마음에 듭니다. 정가의 +-20~30%도 괜찮은 것 같고,
// 선수 만족 시 컨디션 최상 및 계약일수가 달라질 수 있는 것도 마음에 듭니다. … 다시 FA가 되어서 재계약 협상하는 시스템이 더 재밌을 것 같으니
// 구현합시다"). Design: docs\FA협상_설계.md. Server: FaNegotiation (REQ_SCENE_TUTORIAL "ma9m:v2:fan:<idx>:<action>", POPUP types 30 turn /
// 31 deal / 33 done; the app says it has this screen with %ma9m-fan1, ClientBuildSetting).
//
// A full-screen overlay drawn from code on the screen's 2D UI root, like Ma9MFaCeremony (this NGUI sizes widgets by their transform's
// scale; z in front is more negative): the agent on the left (a portrait drawn here, name, bent), the FA card on the right, the agent's
// words typed in a bubble, the 선수 만족도 gauge (0-120: 100 = deal, 110 = very satisfied → 컨디션 최상), the terms, four answers, and
// [정가로 바로 계약] / [그만두기].
//   영입 (FA 시장, OpenSigning): deal → [서명하기] → "sign" → the server signs → the screen closes → Ma9MFaCeremony (계약서 → 입단식) →
//   the server's result popup and the screen's refresh. Signing first, the scenes after: nothing is shown as signed that failed.
//   재계약 (계약관리, OpenRenewal): deal → [재계약하기] → the screen closes → the caller sends its EXTEND (the server takes the deal's
//   price and days) and plays the 재계약 scene when it succeeds.
public class Ma9MNegotiation : MonoBehaviour
{
	public const byte TurnType = 30, DealType = 31, DoneType = 33;

	private static Ma9MNegotiation s_live;

	// The start's answer builds the screen (a refusal is a plain popup).
	private static int s_startIdx;

	private static bool s_startRenewal;

	private static Action s_startRefresh;

	private static Action<Ma9MFaCeremony.Info> s_startOnDeal;

	private static float s_startUntil;

	private static Watch s_watch;

	public static bool Showing => s_live != null;

	// 전력강화 > FA 시장 [FA 영입]: refresh = the screen's reload after a signing.
	public static void OpenSigning(int cardIdx, Action refresh)
	{
		Open(cardIdx, false, refresh, null);
	}

	// 계약관리 FA 카드 [연장]: onDeal gets the terms when the agent agreed; the caller renews (EXTEND) and plays the scene.
	public static void OpenRenewal(int cardIdx, Action<Ma9MFaCeremony.Info> onDeal)
	{
		Open(cardIdx, true, null, onDeal);
	}

	private static void Open(int cardIdx, bool renewal, Action refresh, Action<Ma9MFaCeremony.Info> onDeal)
	{
		if (s_live != null)
		{
			s_live.Close();
		}
		if (s_startIdx != 0 && Time.realtimeSinceStartup < s_startUntil)
		{
			return;
		}
		s_startIdx = cardIdx;
		s_startRenewal = renewal;
		s_startRefresh = refresh;
		s_startOnDeal = onDeal;
		s_startUntil = Time.realtimeSinceStartup + 20f;
		SceneChanger.ShowLoadingTransparent();
		Request(cardIdx, "start");
		if (s_watch == null)
		{
			GameObject go = new GameObject("Ma9MNegotiationWatch");
			UnityEngine.Object.DontDestroyOnLoad(go);
			s_watch = go.AddComponent<Watch>();
		}
	}

	private static void Request(int cardIdx, string action)
	{
		Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL spacket = new Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL();
		spacket.scene_name.str = "ma9m:v2:fan:" + cardIdx + ":" + action;
		NetworkConnector.SendPacket(spacket);
	}

	// A server without the negotiation answers the start with nothing of ours: the loading goes after 20 s.
	private class Watch : MonoBehaviour
	{
		private void Update()
		{
			if (s_startIdx != 0 && Time.realtimeSinceStartup >= s_startUntil)
			{
				s_startIdx = 0;
				SceneChanger.HideLoadingTransparent();
				PopupManager.ViewPopup1(Localization.instance.Get("Message"), "서버가 답하지 않았습니다.\n잠시 뒤 다시 해 주세요.", Localization.instance.Get("OK"), null);
			}
		}
	}

	// NetworkConnector.ProcessPKS_SC_POPUP: true when the popup was one of ours.
	public static bool Receive(Ma9MPacket.PKS_SC_POPUP packet)
	{
		byte type = packet.Type.n;
		if (type != TurnType && type != DealType && type != DoneType)
		{
			// A server without the negotiation answers "알 수 없는 요청" (type 1): the loading goes, the popup shows as usual.
			if (s_startIdx != 0 && (type == 1 || type == 2))
			{
				s_startIdx = 0;
				SceneChanger.HideLoadingTransparent();
			}
			return false;
		}
		string[] m = new string[16];
		for (int i = 0; i < m.Length; i++)
		{
			m[i] = (packet.Messages.array != null && i < packet.Messages.array.Length && packet.Messages.array[i] != null) ? (packet.Messages.array[i].str ?? string.Empty) : string.Empty;
		}
		if (s_startIdx != 0)
		{
			int idx = s_startIdx;
			s_startIdx = 0;
			SceneChanger.HideLoadingTransparent();
			if (type == DoneType)
			{
				PopupManager.ViewPopup1(m[0], m[1], Localization.instance.Get("OK"), null);
				return true;
			}
			Ma9MNegotiation screen = Build(idx, s_startRenewal, s_startRefresh, s_startOnDeal);
			if (screen == null)
			{
				Request(idx, "quit");
				PopupManager.ViewPopup1(m[0], "이 화면에서는 협상 창을 열 수 없습니다.", Localization.instance.Get("OK"), null);
				return true;
			}
			screen.Apply(type, m);
			return true;
		}
		if (s_live != null)
		{
			s_live.Apply(type, m);
		}
		else if (type == DoneType)
		{
			PopupManager.ViewPopup1(m[0], m[1], Localization.instance.Get("OK"), null);
		}
		return true;
	}

	private static Ma9MNegotiation Build(int idx, bool renewal, Action refresh, Action<Ma9MFaCeremony.Info> onDeal)
	{
		try
		{
			// The screen's 2D UI root and its camera, and z in front of its UI within the camera's clip range, as the 계약서·입단식 finds
			// them (Ma9MFaCeremony.OverlayRoot / OverlayZ; tester_1117g, 11-17: in the lobby NGUITools.FindCameraForLayer answered with the
			// stadium's perspective camera, so the overlay went on the "(3D)" root and was not seen). 55 % of the way to the near plane, a
			// little behind the scenes' 60 %.
			GameObject parent = Ma9MFaCeremony.OverlayRoot(out UIFont font, out Camera camera);
			if (parent == null || font == null)
			{
				return null;
			}
			GameObject go = NGUITools.AddChild(parent);
			go.name = "Ma9MNegotiation";
			float z = Ma9MFaCeremony.OverlayZ(parent, camera, 0.55f);
			Debug.Log("[Ma9MNegotiation] on " + parent.name + ", z " + z.ToString("F0"));
			go.transform.localPosition = new Vector3(0f, 0f, z);
			go.AddComponent<UIPanel>();
			Ma9MNegotiation screen = go.AddComponent<Ma9MNegotiation>();
			screen.m_idx = idx;
			screen.m_renewal = renewal;
			screen.m_refresh = refresh;
			screen.m_onDeal = onDeal;
			screen.m_font = font;
			s_live = screen;
			screen.Lay();
			return screen;
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MNegotiation] could not build: " + e);
			if (s_live != null)
			{
				s_live.Close();
			}
			return null;
		}
	}

	// ---- state ------------------------------------------------------------------------------------------------------

	private int m_idx;
	private bool m_renewal;
	private Action m_refresh;
	private Action<Ma9MFaCeremony.Info> m_onDeal;
	private UIFont m_font;
	private bool m_escape;
	private bool m_closed;
	private bool m_busy;
	private float m_busyUntil;
	private bool m_signing;
	private bool m_ended;
	private Ma9MFaCeremony.Info m_deal;
	private Vector2 m_min = new Vector2(-480f, -270f), m_max = new Vector2(480f, 270f);
	private readonly List<Texture2D> m_made = new List<Texture2D>();

	private GameObject m_board;
	private UILabel m_title, m_turns, m_agentName, m_bentLabel, m_cardName, m_words, m_gaugeValue, m_terms, m_warn, m_hint, m_stamp;
	private UITexture m_bentPill, m_fill;
	private readonly Btn[] m_choices = new Btn[4];
	private readonly string[] m_choiceAction = new string[4];
	private Btn m_quick, m_quit, m_sign;
	private float m_gauge = -1f;
	private Coroutine m_typing, m_gaugeRun;

	private const float BoardW = 760f, BoardH = 440f, GaugeW = 380f, GaugeMax = 120f;

	private static readonly Color Gold = new Color(1f, 0.84f, 0.38f);
	private static readonly Color Ink = new Color(0.1f, 0.1f, 0.14f);
	private static readonly Color Soft = new Color(0.78f, 0.82f, 0.9f);

	private sealed class Btn
	{
		public GameObject Root;
		public UITexture Back;
		public UILabel Label;
		public Color Colour;
		public float Width;
		public float Size;
		public Action Tap;
	}

	// ---- layout -----------------------------------------------------------------------------------------------------

	private void Lay()
	{
		ComputeScreen();
		Vector3 centre = new Vector3((m_min.x + m_max.x) * 0.5f, (m_min.y + m_max.y) * 0.5f, 0f);
		// The whole screen: dark, takes every tap.
		Box(base.gameObject, centre, (m_max.x - m_min.x) + 40f, (m_max.y - m_min.y) + 40f, new Color(0.01f, 0.02f, 0.05f, 0.9f), true);
		m_board = NGUITools.AddChild(base.gameObject);
		m_board.name = "Board";
		m_board.transform.localPosition = new Vector3(centre.x, centre.y, -4f);
		float fit = Mathf.Min(1f, ((m_max.x - m_min.x) - 16f) / (BoardW + 8f), ((m_max.y - m_min.y) - 16f) / (BoardH + 8f));
		m_board.transform.localScale = new Vector3(fit, fit, 1f);
		GameObject b = m_board;
		// Frame, board, header.
		Box(b, new Vector3(0f, 0f, 2f), BoardW + 8f, BoardH + 8f, new Color(Gold.r, Gold.g, Gold.b, 0.55f));
		Box(b, new Vector3(0f, 0f, 1f), BoardW, BoardH, new Color(0.06f, 0.08f, 0.15f, 0.98f));
		Box(b, new Vector3(0f, BoardH * 0.5f - 22f, 0f), BoardW, 44f, new Color(0.11f, 0.15f, 0.29f, 1f));
		Box(b, new Vector3(0f, BoardH * 0.5f - 45f, -0.5f), BoardW, 2f, new Color(Gold.r, Gold.g, Gold.b, 0.8f));
		m_title = Text(b, m_renewal ? "FA 재계약 협상" : "FA 협상", new Vector3(-BoardW * 0.5f + 20f, BoardH * 0.5f - 22f, -1f), 21f, Gold, UIWidget.Pivot.Left, 300f);
		m_turns = Text(b, string.Empty, new Vector3(BoardW * 0.5f - 20f, BoardH * 0.5f - 22f, -1f), 15f, Soft, UIWidget.Pivot.Right, 260f);

		// Our club (11-17 12:2x, user: "에이전트 자리에 차라리 구단 엠블렘이 차지하고 조건을 선수가 제시하는게 좋아보임"): its emblem on a
		// panel and its name; the club's lines are the four answers below.
		Vector3 clubAt = new Vector3(-285f, 92f, -1f);
		Box(b, clubAt + new Vector3(0f, 0f, 0.5f), 150f, 150f, new Color(Gold.r, Gold.g, Gold.b, 0.45f));
		Box(b, clubAt + new Vector3(0f, 0f, 0.3f), 144f, 144f, new Color(0.13f, 0.17f, 0.3f, 1f));
		if (!ClubEmblem(b, TopMenu_DataCenter.m_teamEmblem, clubAt + new Vector3(0f, 0f, -0.5f), 1.25f))
		{
			Text(b, "우리 구단", clubAt, 18f, Soft, UIWidget.Pivot.Center, 130f);
		}
		m_agentName = Text(b, string.IsNullOrEmpty(TopMenu_DataCenter.m_teamName) ? "우리 구단" : TopMenu_DataCenter.m_teamName, new Vector3(-285f, -2f, -1f), 16f, Color.white, UIWidget.Pivot.Center, 170f);
		Text(b, "구단", new Vector3(-285f, -26f, -1f), 13f, Gold, UIWidget.Pivot.Center);

		// The FA card.
		GameObject card = NGUITools.AddChild(b);
		card.name = "Card";
		card.transform.localPosition = new Vector3(285f, 96f, -1f);
		card.transform.localScale = new Vector3(1.05f, 1.05f, 1f);
		if (PlayerCardDrawer.instance != null)
		{
			try
			{
				PlayerCardDrawer.instance.MakeCardDetailImageObject(m_idx, card);
			}
			catch (Exception e)
			{
				Debug.Log("[Ma9MNegotiation] picture " + m_idx + ": " + e.Message);
			}
		}
		TableReader.TRRow_PlayerCard row = ResourceManager.g_playerTable != null ? ResourceManager.g_playerTable.FindPlayer(m_idx) : null;
		string who = row != null ? ("'" + (row.year % 100).ToString("00") + " " + row.name) : string.Empty;
		m_cardName = Text(b, who, new Vector3(285f, -2f, -1f), 16f, Color.white, UIWidget.Pivot.Center, 180f);
		// The player's bent under his name (he states his own terms).
		m_bentPill = Box(b, new Vector3(285f, -28f, -0.5f), 132f, 24f, new Color(0.3f, 0.3f, 0.35f, 1f));
		m_bentLabel = Text(b, "FA", new Vector3(285f, -28f, -1f), 13f, Color.white, UIWidget.Pivot.Center, 124f);

		// The bubble: the player's words, pointing at him, the text in its middle (user: "글상자 안에 위아래좌우 가운데 정렬이 되어
		// 있는게 아니라 글이 살짝 붕 떠있는 느낌").
		// 168 … 56: under the header's gold line (173-177) with a gap, above the gauge's label row (tester_1117i + the user's phone, 11-17
		// 14:2x: "텍스트박스가 미묘하게 선을 살짝 넘어가 있는데 살짝 내려줄 수 있어??" — it ran 180 … 56, over the line).
		Vector3 bubbleAt = new Vector3(0f, 112f, -1f);
		Box(b, bubbleAt + new Vector3(0f, 0f, 0.5f), 384f, 112f, new Color(0.95f, 0.94f, 0.9f, 1f));
		UITexture tip = Box(b, new Vector3(192f, 132f, 0.4f), 18f, 18f, new Color(0.95f, 0.94f, 0.9f, 1f));
		tip.transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
		m_words = Text(b, string.Empty, bubbleAt, 15f, Ink, UIWidget.Pivot.Center, 356f, 5);

		// The gauge: 0-120, the deal at 100, very satisfied at 110.
		float left = -GaugeW * 0.5f;
		Text(b, "선수 만족도", new Vector3(left, 42f, -1f), 13f, Soft, UIWidget.Pivot.Left);
		m_gaugeValue = Text(b, string.Empty, new Vector3(-left, 42f, -1f), 15f, Color.white, UIWidget.Pivot.Right);
		Box(b, new Vector3(0f, 20f, 0.5f), GaugeW + 6f, 26f, new Color(0f, 0f, 0f, 0.6f));
		m_fill = Box(b, new Vector3(left, 20f, 0f), 1f, 20f, new Color(0.9f, 0.3f, 0.25f, 1f));
		m_fill.pivot = UIWidget.Pivot.Left;
		m_fill.transform.localPosition = new Vector3(left, 20f, 0f);
		float at100 = left + GaugeW * 100f / GaugeMax, at110 = left + GaugeW * 110f / GaugeMax;
		Box(b, new Vector3(at100, 20f, -0.5f), 2f, 30f, Color.white);
		Box(b, new Vector3(at110, 20f, -0.5f), 2f, 30f, Gold);
		// Above the bar, beside the value (tester_1117d: under it, it ran into the terms line).
		Text(b, "계약", new Vector3(at100, 42f, -1f), 11f, Color.white, UIWidget.Pivot.Center);
		Box(b, new Vector3(at110, 38f, -1f), 8f, 8f, Gold).transform.localRotation = Quaternion.Euler(0f, 0f, 45f);
		m_terms = Text(b, string.Empty, new Vector3(0f, -14f, -1f), 14f, Soft, UIWidget.Pivot.Center, 560f);
		m_warn = Text(b, string.Empty, new Vector3(0f, -34f, -1f), 13f, new Color(1f, 0.45f, 0.4f), UIWidget.Pivot.Center, 560f);

		// The answers.
		Color[] tones = { new Color(0.18f, 0.36f, 0.66f), new Color(0.12f, 0.45f, 0.42f), new Color(0.12f, 0.45f, 0.42f), new Color(0.55f, 0.2f, 0.2f) };
		for (int i = 0; i < 4; i++)
		{
			int k = i;
			m_choices[i] = MakeButton(b, new Vector3(-279f + 186f * i, -80f, -1f), 178f, 52f, tones[i], string.Empty, 14f, Color.white);
			m_choices[i].Tap = delegate
			{
				Choose(k);
			};
		}
		m_sign = MakeButton(b, new Vector3(0f, -80f, -1f), 300f, 56f, new Color(0.85f, 0.66f, 0.2f), m_renewal ? "재계약하기" : "서명하기", 21f, Ink);
		m_sign.Tap = OnSign;
		m_sign.Root.SetActive(false);
		m_quick = MakeButton(b, new Vector3(-112f, -168f, -1f), 210f, 38f, new Color(0.24f, 0.27f, 0.36f), "정가로 바로 계약", 15f, Color.white);
		m_quick.Tap = delegate
		{
			Busy();
			Request(m_idx, "quick");
		};
		m_quit = MakeButton(b, new Vector3(112f, -168f, -1f), 210f, 38f, new Color(0.24f, 0.27f, 0.36f), "그만두기", 15f, Color.white);
		m_quit.Tap = Quit;
		m_hint = Text(b, "선수의 성향에 맞는 제안일수록 만족도가 크게 오릅니다. 금색 선(110)을 넘겨 합의하면 24시간 컨디션 최상으로 뜁니다.",
			new Vector3(0f, -BoardH * 0.5f + 14f, -1f), 11f, new Color(0.6f, 0.65f, 0.75f), UIWidget.Pivot.Center, BoardW - 30f);
		m_stamp = Text(b, string.Empty, new Vector3(0f, 20f, -6f), 34f, Gold, UIWidget.Pivot.Center);
		m_stamp.gameObject.SetActive(false);

		AndroidManager.Instance.PushEscapeAction(OnEscape);
		m_escape = true;
		SoundManager.PlayEffectSound(eEffectSound.ScreenSlide);
		StartCoroutine(Pop(m_board.transform, fit));
	}

	private void ComputeScreen()
	{
		// The whole screen in the root's units: its UIRoot's height (480 on screens wider than 5:3, UIRoot.Start) times the screen's
		// aspect (tester_1117b, 11-17: the camera reading below fell back to its 960 × 540 guess in the lobby).
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

	// ---- the server's answers ---------------------------------------------------------------------------------------

	private void Apply(byte type, string[] m)
	{
		m_busy = false;
		if (type == TurnType)
		{
			ShowTurn(m);
		}
		else if (type == DealType)
		{
			ShowDeal(m);
		}
		else if (m_signing && m[2] == "1")
		{
			Signed(m[0], m[1]);
		}
		else
		{
			ShowEnd(m[0], m[1]);
		}
	}

	// [title, line, gauge, terms, c0, c1, c2, c3, agent · bent, turns, idx, state, delta, renewal]
	private void ShowTurn(string[] m)
	{
		m_title.text = m[0];
		m_turns.text = m[9];
		SetAgent(m[8]);
		int gauge = ParseInt(m[2]);
		int delta = ParseInt(m[12]);
		GaugeTo(gauge);
		if (delta != 0)
		{
			StartCoroutine(Float(delta));
		}
		Say(m[1]);
		m_terms.text = m[3];
		m_warn.text = string.Empty;
		bool last = m[11] == "last";
		for (int i = 0; i < 4; i++)
		{
			string label = m[4 + i];
			m_choiceAction[i] = string.IsNullOrEmpty(label) ? null : (last ? (i == 0 ? "accept" : (i == 3 ? "quitlast" : null)) : ("c" + i));
			SetButton(m_choices[i], label, m_choiceAction[i] != null);
			if (last)
			{
				Recolour(m_choices[i], i == 0 ? new Color(0.75f, 0.58f, 0.18f) : new Color(0.32f, 0.32f, 0.38f));
			}
		}
		m_sign.Root.SetActive(false);
		m_quick.Root.SetActive(!last);
		SetButton(m_quit, "그만두기", true);
		m_quit.Root.transform.localPosition = new Vector3(last ? 0f : 112f, -168f, -1f);
	}

	// [title, line, idx, name, fromClub, seasons, days, price, list, gauge, top, renewal, toClub, toEmblem, agent · bent]
	// (the agent since 11-17 12:xx: a 미리 연장 opens the screen with the deal, before any turn named him).
	private void ShowDeal(string[] m)
	{
		m_title.text = m[0];
		m_turns.text = "합의";
		if (!string.IsNullOrEmpty(m[14]))
		{
			SetAgent(m[14]);
		}
		int gauge = ParseInt(m[9]);
		bool top = m[10] == "1";
		m_deal = new Ma9MFaCeremony.Info
		{
			cardIdx = ParseInt(m[2]),
			playerName = m[3],
			fromClub = m[4],
			seasons = ParseInt(m[5]),
			days = ParseInt(m[6]),
			priceMp = ParseLong(m[7]),
			listPriceMp = ParseLong(m[8]),
			satisfaction = gauge,
			conditionTop = top,
			isRenewal = m[11] == "1",
			toClub = string.IsNullOrEmpty(m[12]) ? TopMenu_DataCenter.m_teamName : m[12],
			toEmblem = ParseInt(m[13]) > 0 ? ParseInt(m[13]) : TopMenu_DataCenter.m_teamEmblem
		};
		GaugeTo(gauge);
		Say(m[1]);
		m_terms.text = "합의한 조건: " + m_deal.seasons + "시즌(" + m_deal.days + "일) · " + m_deal.priceMp.ToString("N0") + " MP" + (top ? " · 컨디션 최상" : string.Empty);
		m_warn.text = TopMenu_DataCenter.MyMP < m_deal.priceMp ? ("MP가 부족합니다. 가진 MP " + TopMenu_DataCenter.MyMP.ToString("N0")) : string.Empty;
		for (int i = 0; i < 4; i++)
		{
			m_choices[i].Root.SetActive(false);
		}
		m_quick.Root.SetActive(false);
		SetButton(m_quit, "다음에 하기", true);
		m_quit.Root.transform.localPosition = new Vector3(0f, -168f, -1f);
		m_sign.Root.SetActive(true);
		StartCoroutine(Stamp(top ? "크게 만족!" : "합의!", top));
		SoundManager.PlayEffectSound(top ? eEffectSound.Combine_Over7 : eEffectSound.MissionPopup);
	}

	// A refusal, a break-off or an error: the words and [닫기].
	private void ShowEnd(string title, string text)
	{
		m_ended = true;
		m_signing = false;
		if (!string.IsNullOrEmpty(title))
		{
			m_title.text = title;
		}
		m_turns.text = string.Empty;
		Say(text);
		if (text.Contains("결렬"))
		{
			GaugeTo(0);
			StartCoroutine(Stamp("결렬", false));
			SoundManager.PlayEffectSound(eEffectSound.Scoreboard_Lose);
		}
		for (int i = 0; i < 4; i++)
		{
			m_choices[i].Root.SetActive(false);
		}
		m_sign.Root.SetActive(false);
		m_quick.Root.SetActive(false);
		SetButton(m_quit, "닫기", true);
		m_quit.Root.transform.localPosition = new Vector3(0f, -168f, -1f);
	}

	// 영입 done on the server: the scenes, then its result and the screen's reload.
	private void Signed(string title, string text)
	{
		m_ended = true;
		Ma9MFaCeremony.Info info = m_deal;
		Action refresh = m_refresh;
		Close();
		Ma9MFaCeremony.Play(info, delegate
		{
			if (refresh != null)
			{
				refresh();
			}
			PopupManager.ViewPopup1(title, text, Localization.instance.Get("OK"), null);
		});
	}

	// ---- the player's moves -----------------------------------------------------------------------------------------

	private void Choose(int i)
	{
		string action = m_choiceAction[i];
		if (action == null)
		{
			return;
		}
		if (action == "quitlast")
		{
			Quit();
			return;
		}
		Busy();
		Request(m_idx, action);
	}

	private void OnSign()
	{
		if (m_deal == null)
		{
			return;
		}
		if (m_renewal || m_deal.isRenewal)
		{
			Ma9MFaCeremony.Info info = m_deal;
			Action<Ma9MFaCeremony.Info> onDeal = m_onDeal;
			m_ended = true;
			Close();
			if (onDeal != null)
			{
				onDeal(info);
			}
			return;
		}
		m_signing = true;
		Busy();
		Request(m_idx, "sign");
	}

	private void Busy()
	{
		m_busy = true;
		m_busyUntil = Time.realtimeSinceStartup + 20f;
	}

	private void Update()
	{
		if (m_busy && Time.realtimeSinceStartup >= m_busyUntil)
		{
			m_busy = false;
			ShowEnd(null, "서버가 답하지 않았습니다.\n잠시 뒤 다시 해 주세요.");
		}
	}

	private void OnEscape()
	{
		Quit();
	}

	// [그만두기] / 뒤로: the talk ends on the server too (a struck deal just lapses in ten minutes).
	private void Quit()
	{
		if (!m_ended && m_deal == null)
		{
			Request(m_idx, "quit");
		}
		Close();
	}

	private void Close()
	{
		if (m_closed)
		{
			return;
		}
		m_closed = true;
		if (m_escape)
		{
			m_escape = false;
			// 11-17 18:1x: only this screen's own back action (the 재계약 scene may already sit on top of it).
			AndroidManager.Instance.Ma9MRemoveEscapeAction(OnEscape);
			AndroidManager.Instance.EscapeEnable = true;
		}
		if (s_live == this)
		{
			s_live = null;
		}
		UnityEngine.Object.Destroy(base.gameObject);
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
		if (!m_closed)
		{
			m_closed = true;
			if (m_escape)
			{
				AndroidManager.Instance.Ma9MRemoveEscapeAction(OnEscape);
				AndroidManager.Instance.EscapeEnable = true;
			}
		}
		if (s_live == this)
		{
			s_live = null;
		}
	}

	// ---- parts ------------------------------------------------------------------------------------------------------

	// "<who> · <bent>": the player's bent under his card (the left side is our club since 11-17 12:2x; who is not shown).
	private void SetAgent(string agentAndBent)
	{
		string[] p = (agentAndBent ?? string.Empty).Split(new[] { " · " }, StringSplitOptions.None);
		string bent = p.Length > 1 ? p[1] : (p.Length > 0 ? p[0] : string.Empty);
		if (string.IsNullOrEmpty(bent))
		{
			return;
		}
		m_bentLabel.text = bent;
		Fit(m_bentLabel, 13f, 124f);
		Color c = bent.Contains("돈") ? new Color(0.75f, 0.55f, 0.1f) : bent.Contains("우승") ? new Color(0.65f, 0.15f, 0.2f) : bent.Contains("주전") ? new Color(0.15f, 0.42f, 0.7f) : new Color(0.2f, 0.5f, 0.3f);
		m_bentPill.color = c;
	}

	private void Say(string text)
	{
		if (m_typing != null)
		{
			StopCoroutine(m_typing);
		}
		m_typing = StartCoroutine(Type(m_words, text ?? string.Empty, 0.018f));
	}

	private void GaugeTo(int value)
	{
		if (m_gaugeRun != null)
		{
			StopCoroutine(m_gaugeRun);
		}
		m_gaugeRun = StartCoroutine(GaugeRun(Mathf.Clamp(value, 0, (int)GaugeMax)));
	}

	private IEnumerator GaugeRun(float to)
	{
		float from = m_gauge < 0f ? 0f : m_gauge;
		if (Mathf.Abs(to - from) > 0.5f)
		{
			SoundManager.PlayEffectSound(eEffectSound.BarIncrease);
		}
		for (float s = 0f; s < 1f; s += Time.deltaTime / 0.55f)
		{
			SetGauge(Mathf.Lerp(from, to, 1f - (1f - s) * (1f - s)));
			yield return null;
		}
		SetGauge(to);
	}

	private void SetGauge(float v)
	{
		m_gauge = v;
		m_fill.transform.localScale = new Vector3(Mathf.Max(1f, GaugeW * v / GaugeMax), 20f, 1f);
		m_fill.color = v >= 110f ? Gold : v >= 100f ? new Color(0.35f, 0.85f, 0.4f) : v >= 70f ? new Color(0.55f, 0.8f, 0.3f) : v >= 40f ? new Color(0.95f, 0.65f, 0.2f) : new Color(0.9f, 0.3f, 0.25f);
		m_gaugeValue.text = Mathf.RoundToInt(v).ToString();
	}

	// "+20" / "−15" rising from the gauge's end.
	private IEnumerator Float(int delta)
	{
		float x = -GaugeW * 0.5f + GaugeW * Mathf.Clamp(m_gauge < 0f ? 0f : m_gauge, 0f, GaugeMax) / GaugeMax;
		// From the bar's end, a short rise (tester_1117d: rising from the label row it drifted into the bubble).
		UILabel label = Text(m_board, (delta > 0 ? "+" : "−") + Mathf.Abs(delta), new Vector3(x + 18f, 20f, -5f), 18f, delta > 0 ? new Color(0.45f, 1f, 0.5f) : new Color(1f, 0.4f, 0.35f), UIWidget.Pivot.Left);
		Vector3 from = label.transform.localPosition;
		for (float s = 0f; s < 1f && label != null; s += Time.deltaTime / 0.9f)
		{
			label.transform.localPosition = from + new Vector3(0f, 14f * s, 0f);
			label.alpha = 1f - s * s;
			yield return null;
		}
		if (label != null)
		{
			UnityEngine.Object.Destroy(label.gameObject);
		}
	}

	// A word slammed over the gauge (합의! / 크게 만족! / 결렬).
	private IEnumerator Stamp(string word, bool gold)
	{
		m_stamp.text = word;
		m_stamp.color = word == "결렬" ? new Color(1f, 0.35f, 0.3f) : gold ? Gold : new Color(0.55f, 1f, 0.6f);
		m_stamp.gameObject.SetActive(true);
		m_stamp.transform.localRotation = Quaternion.Euler(0f, 0f, -8f);
		for (float s = 0f; s < 1f; s += Time.deltaTime / 0.2f)
		{
			float k = Mathf.Lerp(80f, 34f, s * s);
			m_stamp.transform.localScale = new Vector3(k, k, 1f);
			m_stamp.alpha = Mathf.Clamp01(s * 2f);
			yield return null;
		}
		m_stamp.transform.localScale = new Vector3(34f, 34f, 1f);
		yield return new WaitForSeconds(1.1f);
		for (float s = 0f; s < 1f; s += Time.deltaTime / 0.4f)
		{
			m_stamp.alpha = 1f - s;
			yield return null;
		}
		m_stamp.gameObject.SetActive(false);
	}

	private IEnumerator Pop(Transform t, float fit)
	{
		for (float s = 0f; s < 1f; s += Time.deltaTime / 0.22f)
		{
			float k = fit * Mathf.Lerp(0.85f, 1f, 1f - (1f - s) * (1f - s));
			t.localScale = new Vector3(k, k, 1f);
			yield return null;
		}
		t.localScale = new Vector3(fit, fit, 1f);
	}

	private IEnumerator Type(UILabel label, string text, float perLetter)
	{
		for (int i = 1; i <= text.Length; i++)
		{
			label.text = text.Substring(0, i);
			label.MarkAsChanged();
			yield return new WaitForSeconds(perLetter);
		}
		label.text = text;
		label.MarkAsChanged();
		m_typing = null;
	}

	private Btn MakeButton(GameObject parent, Vector3 pos, float w, float h, Color colour, string text, float size, Color ink)
	{
		GameObject root = NGUITools.AddChild(parent);
		root.name = "Button";
		root.transform.localPosition = pos;
		UITexture back = Box(root, Vector3.zero, w, h, colour, true);
		Box(root, new Vector3(0f, h * 0.5f - 2f, -0.5f), w, 3f, new Color(1f, 1f, 1f, 0.18f));
		UILabel label = Text(root, text, new Vector3(0f, 0f, -1f), size, ink, UIWidget.Pivot.Center, w - 14f);
		Btn btn = new Btn { Root = root, Back = back, Label = label, Colour = colour, Width = w - 14f, Size = size };
		UIEventListener.Get(back.gameObject).onClick = delegate
		{
			if (!m_busy && btn.Tap != null && btn.Back.color.a > 0.5f)
			{
				SoundManager.PlayEffectSound(eEffectSound.Button);
				btn.Tap();
			}
		};
		UIEventListener.Get(back.gameObject).onPress = delegate(GameObject g, bool down)
		{
			Color c = down ? btn.Colour * 0.72f : btn.Colour;
			c.a = btn.Back.color.a;
			btn.Back.color = c;
		};
		return btn;
	}

	private void SetButton(Btn btn, string text, bool on)
	{
		btn.Root.SetActive(!string.IsNullOrEmpty(text));
		text = text ?? string.Empty;
		btn.Label.maxLineCount = 2;
		btn.Label.text = text;
		btn.Label.transform.localScale = new Vector3(btn.Size, btn.Size, 1f);
		// Too wide on one line (tester_1117i: "계약금을 올려 드리겠습니다 (+10%)" shrank to a small font): the part in brackets goes on a
		// second line.
		int cut = text.LastIndexOf(" (", StringComparison.Ordinal);
		if (cut > 0 && btn.Label.relativeSize.x * btn.Size > btn.Width)
		{
			btn.Label.text = text.Substring(0, cut) + "\n" + text.Substring(cut + 1);
		}
		Fit(btn.Label, btn.Size, btn.Width);
		Color c = btn.Colour;
		c.a = on ? 1f : 0.4f;
		btn.Back.color = c;
	}

	private void Recolour(Btn btn, Color colour)
	{
		btn.Colour = colour;
		Color c = colour;
		c.a = btn.Back.color.a;
		btn.Back.color = c;
	}

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

	// A label made here is drawn about half its size above a middle pivot (the borrowed dynamic font's vertical offset under Unity 6:
	// 0.49 × size, measured by the engine session on the user's phone and the PC, 11-17). Labels with a Left / Center / Right pivot are
	// put that much lower so they sit in the middle of their boxes and buttons (1.0.55 showed them high, the 소식 band's cut in half).
	private const float TextDrop = 0.49f;

	// A label in the screen's own font. width > 0: one line shrinks to it, more lines wrap at it (as Ma9MFaCeremony.Text).
	private UILabel Text(GameObject parent, string text, Vector3 pos, float size, Color colour, UIWidget.Pivot pivot, float width = 0f, int lines = 1)
	{
		if (pivot == UIWidget.Pivot.Left || pivot == UIWidget.Pivot.Center || pivot == UIWidget.Pivot.Right)
		{
			pos.y -= TextDrop * size;
		}
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Text";
		UILabel label = go.AddComponent<UILabel>();
		label.font = m_font;
		label.supportEncoding = false;
		label.color = colour;
		label.maxLineCount = lines;
		label.lineWidth = (lines > 1 && width > 0f) ? Mathf.RoundToInt(width) : 0;
		label.text = text ?? string.Empty;
		go.transform.localScale = new Vector3(size, size, 1f);
		label.pivot = pivot;
		go.transform.localPosition = pos;
		if (lines <= 1)
		{
			Fit(label, size, width);
		}
		label.MarkAsChanged();
		return label;
	}

	private static void Fit(UILabel label, float size, float width)
	{
		label.transform.localScale = new Vector3(size, size, 1f);
		if (width <= 0f || string.IsNullOrEmpty(label.text))
		{
			return;
		}
		float wide = label.relativeSize.x * size;
		if (wide > width)
		{
			float fit = Mathf.Max(size * 0.6f, size * width / wide);
			label.transform.localScale = new Vector3(fit, fit, 1f);
		}
	}

	private static int ParseInt(string s)
	{
		int.TryParse(s, out int v);
		return v;
	}

	private static long ParseLong(string s)
	{
		long.TryParse(s, out long v);
		return v;
	}

	// Our club's emblem (as the 입단식 draws it: the Emblem component on a UITexture; a season logo too). False when none can be drawn.
	private static bool ClubEmblem(GameObject parent, int index, Vector3 pos, float scale)
	{
		if (!Ma9MEmblemYears.IsDrawable(index))
		{
			return false;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "ClubEmblem";
		holder.transform.localPosition = pos;
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		GameObject go = NGUITools.AddChild(holder);
		UITexture picture = go.AddComponent<UITexture>();
		picture.shader = Shader.Find("Unlit/Transparent Colored");
		try
		{
			go.AddComponent<global::Emblem>().SetEmblem("100x102", index);
			return true;
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MNegotiation] emblem " + index + ": " + e.Message);
			return false;
		}
	}

	// ---- the agent's portrait (until 11-17 12:2x; kept unused) -------------------------------------------------------

	private Texture2D Made(int w, int h)
	{
		Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
		t.wrapMode = TextureWrapMode.Clamp;
		m_made.Add(t);
		return t;
	}

	// A man in a suit on an office-blue ground; the hair, skin, tie and glasses by the player (so one player's agent always looks the same).
	private Texture2D AgentTexture(int seed)
	{
		int n = 96;
		Texture2D t = Made(n, n);
		System.Random r = new System.Random(seed * 7919 + 17);
		Color[] skins = { new Color(0.96f, 0.82f, 0.68f), new Color(0.9f, 0.74f, 0.6f), new Color(0.84f, 0.67f, 0.52f) };
		Color[] hairs = { new Color(0.08f, 0.07f, 0.07f), new Color(0.2f, 0.15f, 0.12f), new Color(0.45f, 0.45f, 0.47f) };
		Color[] ties = { new Color(0.75f, 0.1f, 0.15f), new Color(0.12f, 0.25f, 0.6f), new Color(0.8f, 0.6f, 0.15f), new Color(0.2f, 0.5f, 0.3f) };
		Color skin = skins[r.Next(skins.Length)], hair = hairs[r.Next(hairs.Length)], tie = ties[r.Next(ties.Length)];
		Color suit = Color.Lerp(new Color(0.12f, 0.13f, 0.17f), new Color(0.22f, 0.24f, 0.3f), (float)r.NextDouble());
		bool glasses = r.Next(3) == 0;
		int part = r.Next(2) == 0 ? -1 : 1;
		Color clear = new Color(0f, 0f, 0f, 0f);
		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				float u = x - 47.5f, v = y;
				Color c = Color.Lerp(new Color(0.16f, 0.24f, 0.42f), new Color(0.3f, 0.42f, 0.62f), y / (float)n);
				// Shoulders and suit.
				float sh = (u * u) / (44f * 44f) + ((v + 6f) * (v + 6f)) / (36f * 36f);
				if (sh < 1f && v < 34f)
				{
					c = suit;
					// The shirt's V and the tie.
					if (v > 10f && v < 32f && Mathf.Abs(u) < (32f - v) * 0.55f)
					{
						c = new Color(0.95f, 0.95f, 0.97f);
						if (Mathf.Abs(u) < 2.6f + (32f - v) * 0.05f && v < 30f)
						{
							c = tie;
						}
					}
					// Lapels.
					if (v > 6f && v < 32f && Mathf.Abs(Mathf.Abs(u) - (32f - v) * 0.55f) < 1.2f)
					{
						c = suit * 0.7f;
						c.a = 1f;
					}
				}
				// Neck.
				if (Mathf.Abs(u) < 7.5f && v >= 28f && v < 40f)
				{
					c = skin * 0.93f;
					c.a = 1f;
				}
				// Head.
				float hx = u / 16.5f, hy = (v - 55f) / 20f;
				if (hx * hx + hy * hy < 1f)
				{
					c = skin;
					// Eyes, brows, mouth.
					if (((u + 6.5f) * (u + 6.5f) + (v - 56f) * (v - 56f) < 2.6f) || ((u - 6.5f) * (u - 6.5f) + (v - 56f) * (v - 56f) < 2.6f))
					{
						c = new Color(0.12f, 0.1f, 0.1f);
					}
					if (Mathf.Abs(v - 61f) < 0.8f && Mathf.Abs(Mathf.Abs(u) - 6.5f) < 3.2f)
					{
						c = hair;
					}
					if (Mathf.Abs(v - 45.5f) < 0.7f && Mathf.Abs(u) < 4.5f)
					{
						c = skin * 0.7f;
						c.a = 1f;
					}
					if (glasses && Mathf.Abs(Mathf.Sqrt((Mathf.Abs(u) - 6.5f) * (Mathf.Abs(u) - 6.5f) + (v - 56f) * (v - 56f)) - 4.2f) < 0.6f)
					{
						c = new Color(0.1f, 0.1f, 0.12f);
					}
				}
				// Hair: the top of the head, a side part.
				float gx = u / 17.5f, gy = (v - 57f) / 21f;
				if (gx * gx + gy * gy < 1f && v > 63f - part * u * 0.12f && !(Mathf.Abs(u - part * 5f) < 0.8f && v > 70f))
				{
					c = hair;
				}
				// Ears.
				if ((Mathf.Abs(Mathf.Abs(u) - 16.5f) < 2.6f) && Mathf.Abs(v - 54f) < 4.5f)
				{
					c = skin * 0.95f;
					c.a = 1f;
				}
				// A round frame.
				float d = Mathf.Sqrt(u * u + (y - 47.5f) * (y - 47.5f));
				if (d > 47f)
				{
					c = clear;
				}
				c.a = d > 47f ? 0f : 1f;
				t.SetPixel(x, y, c);
			}
		}
		t.Apply();
		return t;
	}
}
