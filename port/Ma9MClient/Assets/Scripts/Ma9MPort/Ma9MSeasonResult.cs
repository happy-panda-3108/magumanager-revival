using System;
using System.Collections;
using System.Collections.Generic;
using LitJson;
using UnityEngine;

// Ma9M (engine session, 2026-11-10; user: "시즌 끝나고 단순 팝업으로 안내하기 보다는 새로운 화면이 나오면서 순위, 타이틀홀더
// 시상식을 진행했으면 함", and "(가) 추천안": once when the regular season ends, once at the close after the post-season).
//
// The server's ceremony (PKS_SC_POPUP type 9) still carries its pages of three strings for the old popup; from this
// build a JSON string rides after them (server LeagueAwards.Result.cs) and Ma9MAwards hands it here instead of showing
// the pages. A full-screen overlay in the lobby, page by page:
//   kind 0 (regular season): 최종 순위 → 타자 타이틀 → 투수 타이틀 → 리그 MVP → 우리 구단 결산
//   kind 1 (the close):      우승 구단과 한국시리즈 MVP → 최종 순위와 다음 시즌
// 다음 / 이전 / 건너뛰기 (the last page), the back key = 다음. A first draft made of plain boxes, the lobby popup's
// font and the game's own card pictures and emblems (user, 2026-10-25: new screens ship as first drafts).
public class Ma9MSeasonResult : MonoBehaviour
{
	private static readonly Queue<string> s_queue = new Queue<string>();

	private static string s_owner;

	private static Ma9MSeasonResult s_live;

	public static bool Pending => s_queue.Count > 0 && s_live == null;

	public static bool Showing => s_live != null;

	// False when the string is not the screen's data (the pages are shown instead).
	public static bool Enqueue(string json)
	{
		if (string.IsNullOrEmpty(json) || json[0] != '{')
		{
			return false;
		}
		// Another team's results (an account switch) are dropped, as the old popup's pages are.
		if (s_owner != TopMenu_DataCenter.m_teamName)
		{
			s_queue.Clear();
			s_owner = TopMenu_DataCenter.m_teamName;
		}
		s_queue.Enqueue(json);
		return true;
	}

	// Opens the next result; false while the lobby or another popup is in the way (the lobby tries again).
	public static bool TryShow()
	{
		if (!Pending || SceneChanger.GetLoadedLevelName() != "Scene_MainLobby")
		{
			return false;
		}
		if (UnityEngine.Object.FindObjectOfType(typeof(Popup1)) != null)
		{
			return false;
		}
		UIPanel panel = PanelManager.GetPanel("LeagueChagePopupPanel");
		if (panel == null || panel.gameObject.activeSelf)
		{
			return false;
		}
		LeagueChangePopup popup = panel.GetComponent<LeagueChangePopup>();
		if (popup == null || popup.label_SubDescription == null || popup.btn_Close == null)
		{
			return false;
		}
		JsonData data;
		string json = s_queue.Dequeue();
		try
		{
			data = JsonMapper.ToObject(json);
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MSeasonResult] unreadable result: " + e.Message);
			return false;
		}
		// A panel of its own next to the popup's (the popup only lends its font and button): inside the popup's panel,
		// the popup's frame stayed on screen in a corner and the backdrop never showed (first two drafts).
		GameObject go = NGUITools.AddChild(panel.transform.parent.gameObject);
		go.name = "Ma9MSeasonResult";
		go.transform.localPosition = panel.transform.localPosition;
		go.AddComponent<UIPanel>();
		s_live = go.AddComponent<Ma9MSeasonResult>();
		s_live.Build(panel, popup, data);
		return true;
	}

	// ---- JSON ----------------------------------------------------------------------------------------------------

	private static JsonData Get(JsonData d, string key)
	{
		if (d == null || !d.IsObject)
		{
			return null;
		}
		return ((IDictionary)d).Contains(key) ? d[key] : null;
	}

	private static int I(JsonData d, string key, int none = 0)
	{
		JsonData v = Get(d, key);
		if (v == null)
		{
			return none;
		}
		return v.IsInt ? (int)v : v.IsLong ? (int)(long)v : v.IsDouble ? (int)(double)v : none;
	}

	private static string S(JsonData d, string key, string none = "")
	{
		JsonData v = Get(d, key);
		return (v != null && v.IsString) ? (string)v : none;
	}

	private static bool B(JsonData d, string key)
	{
		JsonData v = Get(d, key);
		return v != null && v.IsBoolean && (bool)v;
	}

	private static List<JsonData> A(JsonData d, string key)
	{
		List<JsonData> list = new List<JsonData>();
		JsonData v = Get(d, key);
		if (v != null && v.IsArray)
		{
			for (int i = 0; i < v.Count; i++)
			{
				list.Add(v[i]);
			}
		}
		return list;
	}

	private static string Man(int mp)
	{
		return (mp % 10000 == 0) ? (mp / 10000).ToString("N0") + "만" : mp.ToString("N0");
	}

	// ---- the screen ----------------------------------------------------------------------------------------------

	private static readonly Color Gold = new Color(1f, 0.82f, 0.2f);

	private static readonly Color Sky = new Color(0.5f, 0.85f, 1f);

	private static readonly Color Grey = new Color(0.72f, 0.76f, 0.82f);

	private static readonly Color Up = new Color(0.45f, 0.95f, 0.5f);

	private static readonly Color Down = new Color(1f, 0.45f, 0.4f);

	// Well in front of the lobby: its panels sit ten apart down to about -130 (PanelManager.Rearrange) and their widgets
	// reach further (at -2 and at -40 the backdrop was drawn behind the lobby); the UI camera sees to about -480.
	// The buttons sit behind the text: every label of the panel is one mesh, sorted by its middle, so a button nearer
	// than that middle covered its own caption.
	private const float ZBack = -300f, ZBox = -302f, ZMark = -304f, ZButton = -306f, ZText = -310f;

	private UIPanel m_panel;

	private LeagueChangePopup m_popup;

	private JsonData m_data;

	private readonly List<string> m_names = new List<string>();

	private readonly List<Action<GameObject>> m_builders = new List<Action<GameObject>>();

	private GameObject[] m_pages;

	private int m_page = -1;

	private UILabel m_title, m_sub, m_count;

	private UIImageButton m_prev, m_next, m_skip;

	private UILabel m_money;

	private int m_moneyTo;

	private float m_moneyAt;

	private bool m_escape;

	private void Build(UIPanel panel, LeagueChangePopup popup, JsonData data)
	{
		m_panel = panel;
		m_popup = popup;
		m_data = data;
		// The backdrop: larger than any screen (the lobby's camera for this layer is the stadium's perspective one, whose
		// viewport corners at z 0 are all one point: the first drafts got a backdrop of size 0 and the lobby showed through).
		Debug.Log("[Ma9MSeasonResult] shown, kind " + I(data, "kind"));
		Box(base.gameObject, new Vector3(0f, 0f, ZBack), 4000f, 3000f, new Color(0.03f, 0.05f, 0.1f, 0.97f), true);
		Box(base.gameObject, new Vector3(0f, 146f, ZBox), 760f, 2f, new Color(0.5f, 0.85f, 1f, 0.5f));
		int kind = I(data, "kind");
		string head = I(data, "season") + "시즌 " + S(data, "tier") + " 리그 · " + (kind == 0 ? "정규시즌 결산" : "시즌 마감");
		m_title = Text(base.gameObject, head, new Vector3(-370f, 164f, ZText), 22f, Color.white, UIWidget.Pivot.Left);
		m_sub = Text(base.gameObject, string.Empty, new Vector3(60f, 164f, ZText), 18f, Sky, UIWidget.Pivot.Center);
		m_count = Text(base.gameObject, string.Empty, new Vector3(370f, 164f, ZText), 14f, Grey, UIWidget.Pivot.Right);
		if (kind == 0)
		{
			Add("최종 순위", PageStandings);
			List<JsonData> titles = A(data, "titles");
			if (titles.Exists(t => !B(t, "p")))
			{
				Add("타자 타이틀", page => PageTitles(page, titles.FindAll(t => !B(t, "p"))));
			}
			if (titles.Exists(t => B(t, "p")))
			{
				Add("투수 타이틀", page => PageTitles(page, titles.FindAll(t => B(t, "p"))));
			}
			if (Get(data, "mvp") != null && A(Get(data, "mvp"), "a").Count > 0)
			{
				Add("리그 MVP", PageMvp);
			}
			Add("우리 구단 결산", PageSummary);
		}
		else
		{
			Add("한국시리즈", PageChampion);
			Add("최종 순위", PageStandings);
		}
		m_pages = new GameObject[m_builders.Count];
		m_prev = Button("Prev", new Vector3(-150f, -214f, ZButton), "OnBtn_Ma9MPrev", "이전");
		m_next = Button("Next", new Vector3(0f, -214f, ZButton), "OnBtn_Ma9MNext", "다음");
		m_skip = Button("Skip", new Vector3(150f, -214f, ZButton), "OnBtn_Ma9MSkip", "건너뛰기");
		AndroidManager.Instance.PushEscapeAction(OnEscape);
		m_escape = true;
		SoundManager.PlayEffectSound(eEffectSound.MissionPopup);
		Show(0);
	}

	private void Add(string name, Action<GameObject> builder)
	{
		m_names.Add(name);
		m_builders.Add(builder);
	}

	private void Show(int page)
	{
		page = Mathf.Clamp(page, 0, m_builders.Count - 1);
		if (m_page >= 0 && m_pages[m_page] != null)
		{
			m_pages[m_page].SetActive(false);
		}
		m_page = page;
		if (m_pages[page] == null)
		{
			// Made when it is first shown, never switched off in the frame it is made (this NGUI keeps such a
			// widget's picture on the panel).
			m_pages[page] = NGUITools.AddChild(base.gameObject);
			m_pages[page].name = "Page" + page;
			m_builders[page](m_pages[page]);
		}
		else
		{
			m_pages[page].SetActive(true);
		}
		m_sub.text = m_names[page];
		m_count.text = (page + 1) + " / " + m_builders.Count;
		bool last = page >= m_builders.Count - 1;
		// A button not to be used goes off screen: the popup button's disabled picture is the popup's whole blue frame
		// (it showed round 이전 on the first page), and switching it off in the frame it was made leaves its picture.
		Place(m_prev, -150f, page > 0);
		Place(m_skip, 150f, !last);
		SetButtonText(m_next, last ? "확인" : "다음");
		if (last && m_money != null)
		{
			m_moneyAt = Time.realtimeSinceStartup;
		}
	}

	private void OnBtn_Ma9MPrev()
	{
		if (m_page > 0)
		{
			Show(m_page - 1);
		}
	}

	private void OnBtn_Ma9MNext()
	{
		if (m_page < m_builders.Count - 1)
		{
			Show(m_page + 1);
			return;
		}
		Close();
	}

	private void OnBtn_Ma9MSkip()
	{
		Show(m_builders.Count - 1);
	}

	private void OnEscape()
	{
		// The back key: on to the next page (AndroidManager switches the key off until the action says it is done).
		OnBtn_Ma9MNext();
		if (s_live == this)
		{
			AndroidManager.Instance.EscapeEnable = true;
		}
	}

	private void Close()
	{
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
		UnityEngine.Object.Destroy(base.gameObject);
	}

	private void OnDestroy()
	{
		// The lobby left with the screen up (a scene change): the next visit can show what is left.
		if (s_live == this)
		{
			s_live = null;
		}
	}

	private void Update()
	{
		if (m_money != null && m_moneyAt > 0f)
		{
			float t = Mathf.Clamp01((Time.realtimeSinceStartup - m_moneyAt) / 1.2f);
			m_money.text = "+" + Mathf.RoundToInt(m_moneyTo * (1f - (1f - t) * (1f - t))).ToString("N0") + " MP";
			if (t >= 1f)
			{
				m_moneyAt = 0f;
			}
		}
	}

	// ---- pages ---------------------------------------------------------------------------------------------------

	// The standings on the left (my club's row marked, what the place means on the right of each row), my club's
	// verdict on the right.
	private void PageStandings(GameObject page)
	{
		List<JsonData> clubs = A(m_data, "clubs");
		bool close = I(m_data, "kind") == 1;
		float top = 126f, row = 27f;
		string[] heads = { "순위", "구단", "승", "패", "무", "승률", close ? "다음 시즌" : "결과" };
		// 2026-11-18 (AI testers kt 12회차 버그 16, 두산 13회차): "포스트시즌" at 62 ran into the right-hand box (x 125); the column
		// starts at 30 now, clear of 승률 (ends near 10), and the five letters end near 95.
		float[] xs = { -362f, -300f, -128f, -92f, -56f, -8f, 30f };
		for (int i = 0; i < heads.Length; i++)
		{
			Text(page, heads[i], new Vector3(xs[i], top, ZText), 13f, Grey, i == 1 || i == 6 ? UIWidget.Pivot.Left : UIWidget.Pivot.Center);
		}
		Box(page, new Vector3(-150f, top - 14f, ZBox), 470f, 1f, new Color(1f, 1f, 1f, 0.25f));
		JsonData mine = null;
		for (int i = 0; i < clubs.Count; i++)
		{
			JsonData c = clubs[i];
			float y = top - 30f - i * row;
			bool me = B(c, "me");
			if (me)
			{
				mine = c;
				Box(page, new Vector3(-150f, y, ZMark), 470f, row - 3f, new Color(1f, 0.8f, 0.2f, 0.22f));
			}
			Color colour = me ? Gold : Color.white;
			int w = I(c, "w"), l = I(c, "l");
			Text(page, I(c, "r").ToString(), new Vector3(xs[0], y, ZText), 16f, colour, UIWidget.Pivot.Center);
			Emblem(page, I(c, "e"), "38x29", new Vector3(-328f, y, ZText), 0.8f);
			Text(page, S(c, "n"), new Vector3(xs[1], y, ZText), 15f, colour, UIWidget.Pivot.Left, 160f);
			Text(page, w.ToString(), new Vector3(xs[2], y, ZText), 14f, colour, UIWidget.Pivot.Center);
			Text(page, l.ToString(), new Vector3(xs[3], y, ZText), 14f, colour, UIWidget.Pivot.Center);
			Text(page, I(c, "d").ToString(), new Vector3(xs[4], y, ZText), 14f, colour, UIWidget.Pivot.Center);
			Text(page, (w + l > 0 ? (w / (float)(w + l)) : 0f).ToString("0.000"), new Vector3(xs[5], y, ZText), 14f, colour, UIWidget.Pivot.Center);
			int move = I(c, "mv");
			string what = move == 2 ? "포스트시즌" : move > 0 ? "승격" : move < 0 ? "강등" : "잔류";
			Text(page, what, new Vector3(xs[6], y, ZText), 13f, move == 2 ? Sky : move > 0 ? Up : move < 0 ? Down : Grey, UIWidget.Pivot.Left);
		}
		// My club.
		Box(page, new Vector3(250f, -22f, ZBox), 250f, 312f, new Color(0f, 0f, 0f, 0.38f));
		if (mine != null)
		{
			Emblem(page, I(mine, "e"), "100x102", new Vector3(250f, 78f, ZText), 1f);
			Text(page, S(mine, "n"), new Vector3(250f, 8f, ZText), 20f, Color.white, UIWidget.Pivot.Center, 230f);
			Text(page, I(mine, "r") + "위 · " + I(mine, "w") + "승 " + I(mine, "l") + "패 " + I(mine, "d") + "무", new Vector3(250f, -16f, ZText), 14f, Grey, UIWidget.Pivot.Center);
		}
		string verdict = S(m_data, "verdict");
		Text(page, verdict, new Vector3(250f, -62f, ZText), verdict.Length > 4 ? 26f : 36f, verdict == "강등" ? Down : Gold, UIWidget.Pivot.Center, 236f);
		Text(page, S(m_data, "verdict2"), new Vector3(250f, -102f, ZText), 13f, Color.white, UIWidget.Pivot.Center, 236f);
		Text(page, S(m_data, "next"), new Vector3(250f, -142f, ZText), 13f, Sky, UIWidget.Pivot.Center, 236f, 2);
	}

	// Up to eight titles, four to a row: the winner's card, name and club, the value, 2위 and 3위, and what my club got.
	private void PageTitles(GameObject page, List<JsonData> titles)
	{
		const float cellW = 186f, cellH = 154f;
		for (int i = 0; i < titles.Count && i < 8; i++)
		{
			JsonData t = titles[i];
			float cx = -279f + (i % 4) * cellW, cy = 56f - (i / 4) * (cellH + 4f);
			List<JsonData> first = A(t, "a"), second = A(t, "b"), third = A(t, "c");
			int got = I(t, "mp") * first.FindAll(w => B(w, "me")).Count + I(t, "mp2") * second.FindAll(w => B(w, "me")).Count + I(t, "mp3") * third.FindAll(w => B(w, "me")).Count;
			bool mineFirst = first.Exists(w => B(w, "me"));
			Box(page, new Vector3(cx, cy, ZBox), cellW - 6f, cellH - 4f, mineFirst ? new Color(0.5f, 0.38f, 0.05f, 0.55f) : new Color(0f, 0f, 0f, 0.38f));
			Text(page, S(t, "n"), new Vector3(cx - 84f, cy + 62f, ZText), 16f, Sky, UIWidget.Pivot.Left);
			Text(page, S(t, "v"), new Vector3(cx + 84f, cy + 62f, ZText), 18f, Color.white, UIWidget.Pivot.Right);
			if (first.Count == 0)
			{
				Text(page, "수상자 없음", new Vector3(cx, cy, ZText), 13f, Grey, UIWidget.Pivot.Center);
				continue;
			}
			JsonData winner = first[0];
			Card(page, I(winner, "c"), new Vector3(cx - 52f, cy - 10f, ZText), 0.66f);
			float x = cx - 10f;
			Text(page, S(winner, "n"), new Vector3(x, cy + 36f, ZText), 15f, B(winner, "me") ? Gold : Color.white, UIWidget.Pivot.Left, 92f);
			Text(page, "'" + I(winner, "y").ToString("00") + " " + S(winner, "t"), new Vector3(x, cy + 20f, ZText), 11f, Grey, UIWidget.Pivot.Left, 92f);
			int more = first.Count - 1 + I(t, "xa");
			if (more > 0)
			{
				Text(page, "공동 " + (first.Count > 1 ? S(first[1], "n") : string.Empty) + (more > 1 ? " 외 " + (more - 1) + "명" : string.Empty), new Vector3(x, cy + 6f, ZText), 11f, Grey, UIWidget.Pivot.Left, 92f);
			}
			float y = cy - 10f;
			foreach (KeyValuePair<string, List<JsonData>> place in new[] { new KeyValuePair<string, List<JsonData>>("2위 ", second), new KeyValuePair<string, List<JsonData>>("3위 ", third) })
			{
				if (place.Value.Count > 0)
				{
					JsonData who = place.Value[0];
					Text(page, place.Key + S(who, "n") + (place.Value.Count > 1 ? " 외" : string.Empty), new Vector3(x, y, ZText), 11f, B(who, "me") ? Gold : Grey, UIWidget.Pivot.Left, 92f);
					y -= 15f;
				}
			}
			if (got > 0)
			{
				Text(page, "+" + Man(got) + " MP", new Vector3(x, cy - 58f, ZText), 14f, Gold, UIWidget.Pivot.Left);
			}
		}
	}

	private void PageMvp(GameObject page)
	{
		JsonData mvp = Get(m_data, "mvp");
		JsonData who = A(mvp, "a")[0];
		bool me = B(who, "me");
		Box(page, new Vector3(0f, -22f, ZBox), 620f, 312f, me ? new Color(0.5f, 0.38f, 0.05f, 0.45f) : new Color(0f, 0f, 0f, 0.38f));
		Detail(page, I(who, "c"), new Vector3(-170f, -22f, ZText), 1.7f);
		Text(page, S(mvp, "n"), new Vector3(110f, 96f, ZText), 34f, Gold, UIWidget.Pivot.Center);
		Text(page, S(who, "n"), new Vector3(110f, 46f, ZText), 28f, Color.white, UIWidget.Pivot.Center, 300f);
		Emblem(page, I(who, "e"), "38x29", new Vector3(110f - 80f, 12f, ZText), 0.9f);
		Text(page, "'" + I(who, "y").ToString("00") + " " + S(who, "t"), new Vector3(110f - 58f, 12f, ZText), 15f, Grey, UIWidget.Pivot.Left, 200f);
		Text(page, S(mvp, "v"), new Vector3(110f, -34f, ZText), 24f, Sky, UIWidget.Pivot.Center);
		int more = A(mvp, "a").Count - 1 + I(mvp, "xa");
		if (more > 0)
		{
			Text(page, "공동 수상 외 " + more + "명", new Vector3(110f, -62f, ZText), 13f, Grey, UIWidget.Pivot.Center);
		}
		Text(page, me ? "우리 구단 수상 · +" + Man(I(mvp, "mp")) + " MP" : "보상 " + Man(I(mvp, "mp")) + " MP", new Vector3(110f, -104f, ZText), 18f, me ? Gold : Grey, UIWidget.Pivot.Center);
	}

	// What my club won and the MP it came to (counted up), then what comes next.
	private void PageSummary(GameObject page)
	{
		List<string> lines = new List<string>();
		List<JsonData> all = A(m_data, "titles");
		if (Get(m_data, "mvp") != null)
		{
			all.Add(Get(m_data, "mvp"));
		}
		foreach (JsonData t in all)
		{
			foreach (KeyValuePair<string, string> place in new[] { new KeyValuePair<string, string>("a", "mp"), new KeyValuePair<string, string>("b", "mp2"), new KeyValuePair<string, string>("c", "mp3") })
			{
				foreach (JsonData w in A(t, place.Key))
				{
					if (B(w, "me"))
					{
						string rank = place.Key == "a" ? (S(t, "n").EndsWith("MVP") ? string.Empty : " 1위") : place.Key == "b" ? " 2위" : " 3위";
						lines.Add(S(t, "n") + rank + "  " + S(w, "n") + "  +" + Man(I(t, place.Value)));
					}
				}
			}
		}
		Box(page, new Vector3(0f, 6f, ZBox), 700f, 236f, new Color(0f, 0f, 0f, 0.38f));
		int titles = I(m_data, "mine"), placed = I(m_data, "placed");
		string head = titles + placed > 0 ? "우리 구단 수상 " + titles + "개" + (placed > 0 ? " · 2·3위 " + placed + "명" : string.Empty) : "이번 시즌 우리 구단 수상은 없습니다";
		Text(page, head, new Vector3(0f, 104f, ZText), 18f, titles + placed > 0 ? Gold : Grey, UIWidget.Pivot.Center);
		// Three columns of up to nine lines; more than that ends with how many are left out.
		int room = 27;
		for (int i = 0; i < lines.Count && i < room; i++)
		{
			string line = (i == room - 1 && lines.Count > room) ? "… 외 " + (lines.Count - room + 1) + "건" : lines[i];
			Text(page, line, new Vector3(-335f + (i / 9) * 228f, 78f - (i % 9) * 20f, ZText), 12f, Color.white, UIWidget.Pivot.Left, 220f);
		}
		Text(page, "보상 합계", new Vector3(-120f, -140f, ZText), 16f, Grey, UIWidget.Pivot.Right);
		m_moneyTo = I(m_data, "mp");
		m_money = Text(page, "+0 MP", new Vector3(-100f, -140f, ZText), 32f, Gold, UIWidget.Pivot.Left);
		Text(page, S(m_data, "next"), new Vector3(0f, -180f, ZText), 15f, Sky, UIWidget.Pivot.Center, 700f);
	}

	// The close: the champion on the left, the 한국시리즈 MVP on the right.
	private void PageChampion(GameObject page)
	{
		JsonData champ = Get(m_data, "champ");
		Box(page, new Vector3(-190f, -22f, ZBox), 340f, 312f, new Color(0.5f, 0.38f, 0.05f, 0.45f));
		Text(page, "우승", new Vector3(-190f, 100f, ZText), 40f, Gold, UIWidget.Pivot.Center);
		if (champ != null)
		{
			Emblem(page, I(champ, "e"), "100x102", new Vector3(-190f, 22f, ZText), 1.1f);
			Text(page, S(champ, "n"), new Vector3(-190f, -58f, ZText), 24f, Color.white, UIWidget.Pivot.Center, 320f);
		}
		Text(page, S(m_data, "series"), new Vector3(-190f, -92f, ZText), 16f, Sky, UIWidget.Pivot.Center);
		JsonData mvp = Get(m_data, "mvp");
		Box(page, new Vector3(190f, -22f, ZBox), 340f, 312f, new Color(0f, 0f, 0f, 0.38f));
		if (mvp == null || A(mvp, "a").Count == 0)
		{
			Text(page, "한국시리즈 MVP 없음", new Vector3(190f, -22f, ZText), 15f, Grey, UIWidget.Pivot.Center);
			return;
		}
		JsonData who = A(mvp, "a")[0];
		bool me = B(who, "me");
		Text(page, S(mvp, "n"), new Vector3(190f, 104f, ZText), 24f, Gold, UIWidget.Pivot.Center);
		Detail(page, I(who, "c"), new Vector3(190f, 22f, ZText), 1.1f);
		Text(page, S(who, "n"), new Vector3(190f, -72f, ZText), 22f, Color.white, UIWidget.Pivot.Center, 320f);
		Text(page, "'" + I(who, "y").ToString("00") + " " + S(who, "t") + " · " + S(mvp, "v"), new Vector3(190f, -96f, ZText), 13f, Grey, UIWidget.Pivot.Center, 320f);
		Text(page, me ? "우리 구단 수상 · +" + Man(I(mvp, "mp")) + " MP" : "보상 " + Man(I(mvp, "mp")) + " MP", new Vector3(190f, -128f, ZText), 16f, me ? Gold : Grey, UIWidget.Pivot.Center);
	}

	// ---- parts ---------------------------------------------------------------------------------------------------

	private UITexture Box(GameObject parent, Vector3 pos, float width, float height, Color colour, bool blocks = false)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Box";
		UITexture box = go.AddComponent<UITexture>();
		box.shader = Shader.Find("Unlit/Transparent Colored");
		box.mainTexture = Texture2D.whiteTexture;
		box.color = colour;
		go.transform.localPosition = pos;
		go.transform.localScale = new Vector3(width, height, 1f);
		if (blocks)
		{
			// Nothing under the screen takes a tap.
			go.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 0f);
		}
		return box;
	}

	// A label in the lobby popup's font. width > 0: longer text is shrunk to it (one line) or wrapped (lines > 1).
	private UILabel Text(GameObject parent, string text, Vector3 pos, float size, Color colour, UIWidget.Pivot pivot, float width = 0f, int lines = 1)
	{
		GameObject go = UnityEngine.Object.Instantiate(m_popup.label_SubDescription.gameObject) as GameObject;
		go.name = "Text";
		go.transform.parent = parent.transform;
		go.transform.localScale = new Vector3(size, size, 1f);
		Ma9MCardSpread.DropLocalize(go);
		go.SetActive(true);
		UILabel label = go.GetComponent<UILabel>();
		label.supportEncoding = false;
		label.color = colour;
		label.maxLineCount = lines;
		label.lineWidth = (lines > 1) ? Mathf.RoundToInt(width) : 0;
		label.shrinkToFit = false;
		label.text = text ?? string.Empty;
		// The pivot before the place: this NGUI's pivot setter moves the widget so its picture stays where it was
		// (set after the place, every column of the first draft was off by half its own width).
		label.pivot = pivot;
		go.transform.localPosition = pos;
		if (width > 0f && lines <= 1)
		{
			// One line that is too long: smaller letters, down to 60 %.
			float wide = label.relativeSize.x * size;
			if (wide > width)
			{
				float fit = Mathf.Max(size * 0.6f, size * width / wide);
				go.transform.localScale = new Vector3(fit, fit, 1f);
			}
		}
		label.MarkAsChanged();
		return label;
	}

	private void Emblem(GameObject parent, int index, string size, Vector3 pos, float scale)
	{
		if (!Ma9MEmblemYears.IsDrawable(index))   // a club's emblem or a season's logo (2026-11-16)
		{
			return;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "Emblem";
		holder.transform.localPosition = pos;
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		GameObject go = NGUITools.AddChild(holder);
		UITexture picture = go.AddComponent<UITexture>();
		picture.shader = Shader.Find("Unlit/Transparent Colored");
		go.AddComponent<global::Emblem>().SetEmblem(size, index);
	}

	// The roster card (79 x 127), not to be tapped.
	private void Card(GameObject parent, int card, Vector3 pos, float scale)
	{
		if (card <= 0 || PlayerCardDrawer.instance == null)
		{
			return;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "Card";
		holder.transform.localPosition = pos;
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		try
		{
			PlayerCardDrawer.instance.MakeCardObjectNew(card, holder, false);
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MSeasonResult] card " + card + ": " + e.Message);
		}
	}

	// The player's large picture (the card detail's).
	private void Detail(GameObject parent, int card, Vector3 pos, float scale)
	{
		if (card <= 0 || PlayerCardDrawer.instance == null)
		{
			return;
		}
		GameObject holder = NGUITools.AddChild(parent);
		holder.name = "Detail";
		holder.transform.localPosition = pos;
		holder.transform.localScale = new Vector3(scale, scale, 1f);
		try
		{
			PlayerCardDrawer.instance.MakeCardDetailImageObject(card, holder);
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MSeasonResult] picture " + card + ": " + e.Message);
		}
	}

	private UIImageButton Button(string name, Vector3 pos, string function, string text)
	{
		GameObject go = UnityEngine.Object.Instantiate(m_popup.btn_Close.gameObject) as GameObject;
		go.name = "Btn_" + name;
		go.transform.parent = base.transform;
		go.transform.localScale = Vector3.one;
		go.transform.localPosition = pos;
		Ma9MCardSpread.DropLocalize(go);
		go.SetActive(true);
		UIButtonMessage message = go.GetComponent<UIButtonMessage>();
		if (message == null)
		{
			message = go.AddComponent<UIButtonMessage>();
		}
		message.target = base.gameObject;
		message.functionName = function;
		UIImageButton button = go.GetComponent<UIImageButton>();
		SetButtonText(button, text);
		Ma9MClientUtil.ButtonSetActive(button, true);
		return button;
	}

	private static void Place(UIImageButton button, float x, bool shown)
	{
		if (button != null)
		{
			button.transform.localPosition = shown ? new Vector3(x, -214f, ZButton) : new Vector3(x, -5000f, ZButton);
		}
	}

	private static void SetButtonText(UIImageButton button, string text)
	{
		UILabel label = (button != null) ? button.GetComponentInChildren<UILabel>(true) : null;
		if (label != null)
		{
			label.text = text;
		}
	}
}
