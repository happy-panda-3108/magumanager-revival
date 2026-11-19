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

	// Dev (remote "seasonresult <file>", systems session 2026-11-19): the screen with a result from a file, in the lobby.
	public static string Test(string file)
	{
		string json;
		try
		{
			json = System.IO.File.ReadAllText(file, System.Text.Encoding.UTF8).Trim();
		}
		catch (Exception e)
		{
			return "error " + e.Message;
		}
		if (!Enqueue(json))
		{
			return "error not a result (JSON object)";
		}
		return TryShow() ? "ok shown" : "ok queued (the lobby shows it when it can)";
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
			// 2026-11-19 23:0x (user: "시상식용 계단 같은걸 쓰잖아? 거기에 카드를 올려놓고, 1~3등이 어떤 성적으로 1~3등인지, 소속팀은 어디인지"):
			// four titles to a page, each on a podium (PageTitles).
			AddTitlePages("타자 타이틀", titles.FindAll(t => !B(t, "p")));
			AddTitlePages("투수 타이틀", titles.FindAll(t => B(t, "p")));
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

	private const int PodiumsPerPage = 4;

	private void AddTitlePages(string name, List<JsonData> titles)
	{
		int pages = (titles.Count + PodiumsPerPage - 1) / PodiumsPerPage;
		for (int p = 0; p < pages; p++)
		{
			List<JsonData> part = titles.GetRange(p * PodiumsPerPage, Mathf.Min(PodiumsPerPage, titles.Count - p * PodiumsPerPage));
			Add(pages > 1 ? name + " " + (p + 1) + "/" + pages : name, page => PageTitles(page, part));
		}
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
		if (m_pops.Count > 0)
		{
			PopStep();
		}
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
		// The line and my club's band reach x 100 (from 85): "포스트시즌" ends near 95 (textcheck, tester_1119g: 15 px past the band).
		Box(page, new Vector3(-143f, top - 14f, ZBox), 486f, 1f, new Color(1f, 1f, 1f, 0.25f));
		JsonData mine = null;
		for (int i = 0; i < clubs.Count; i++)
		{
			JsonData c = clubs[i];
			float y = top - 30f - i * row;
			bool me = B(c, "me");
			if (me)
			{
				mine = c;
				Box(page, new Vector3(-143f, y, ZMark), 486f, row - 3f, new Color(1f, 0.8f, 0.2f, 0.22f));
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
		string stakes = S(m_data, "verdict3");
		// With the stakes line the lower lines move up a little to make room (tester_1119g: four lines from -102 down filled the box).
		float lift = stakes.Length > 0 ? 8f : 0f;
		Text(page, verdict, new Vector3(250f, -62f + lift, ZText), verdict.Length > 4 ? 26f : 36f, verdict == "강등" ? Down : Gold, UIWidget.Pivot.Center, 236f);
		Text(page, S(m_data, "verdict2"), new Vector3(250f, -102f + lift, ZText), 13f, Color.white, UIWidget.Pivot.Center, 236f);
		// 2026-11-19 23:0x (user: "포스트시즌 최종 4위 안이면 트리플A 승격" 한 줄 … "그리고 11월을 아래로 내려서 좀 쉽게 읽히도록"): what the
		// post-season decides (server LeagueAwards.PostStakes), and the date on a line of its own ("포스트시즌: 준플레이오프부터 · 11월 /
		// 18일 01:00" broke inside the date).
		if (stakes.Length > 0)
		{
			// Two lines on purpose, broken before the tier (tester_1119g: one line did not fit the 236 and wrapped as "…트리플A / 승격").
			Text(page, stakes.Replace(" 안이면 ", " 안이면\n"), new Vector3(250f, -120f, ZText), 13f, Up, UIWidget.Pivot.Center, 236f, 2);
		}
		Text(page, Ma9MDateLine(S(m_data, "next")), new Vector3(250f, -157f, ZText), 13f, Sky, UIWidget.Pivot.Center, 236f, 2);
	}

	// "포스트시즌: 준플레이오프부터 · 11월 18일 01:00" → the date on the second line.
	private static string Ma9MDateLine(string next)
	{
		int cut = next.LastIndexOf(" · ", StringComparison.Ordinal);
		return cut > 0 ? next.Substring(0, cut) + "\n" + next.Substring(cut + 3) : next;
	}

	// 2026-11-19 23:0x (user: "보통 시상식 같은 경우 올림픽이나 다른 인게임 시상식에서도 그렇지만 시상식용 계단 같은걸 쓰잖아? 거기에 카드를
	// 올려놓고, 1~3등이 어떤 성적으로 1~3등인지, 소속팀은 어디인지 이런식으로 나왔으면 함. 지금 결산 이미지는 정보를 한 눈에 보기는 편하지만,
	// 시상식이라는 이미지는 없음"): up to four titles, two to a row, each a podium — 2위 on the left, 1위 in the middle on the highest
	// step, 3위 on the right; on each step the card, the player, his record and his club. What my club got from the title is on the
	// title's line as "우리 구단 +62만 MP" (11-18 question 7, AI tester LG: the old "+62만" under the winner's card read as his own prize).
	// The cards come up onto the steps one by one, 3위 first (PopIn).
	private const float CellW = 376f, CellH = 160f;

	private static readonly Color[] StepColour =
	{
		new Color(0.78f, 0.6f, 0.16f, 0.92f),   // 1위 gold
		new Color(0.56f, 0.6f, 0.68f, 0.92f),   // 2위 silver
		new Color(0.6f, 0.38f, 0.2f, 0.92f)     // 3위 bronze
	};

	// 23:5x (user: "등수 1, 2, 3을 시상대 박스 가운데 정렬로 … 한 가운데에 등수가 박혀있는 느낌으로, 그리고 17 만 있는게 아니라 17승, 24홀드,
	// 197탈삼진 이런식으로 … 공동 1위가 있는 경우 둘 다 1등 시상대를 쓰게하고 시상대에 빈칸이 없게끔 해줘. 공동 2위의 경우 둘 다 2등 시상대를
	// 쓰게 하고"): the three steps hold the first three players in rank order — the middle, then the left, then the right — and each
	// step is as high and coloured as its player's rank (two co-winners: two gold steps, the next player on the third). A step only
	// stands where a player does. The rank is set in the middle of its step, embossed; the record carries its unit (17승, 197탈삼진,
	// 타율 .374).
	private static readonly float[] StepHigh = { 36f, 27f, 20f };

	private static readonly float[] StepDigit = { 24f, 19f, 15f };

	// The unit after a counting record; the rate titles (타율, 평균자책, WHIP …) put their name before the number instead.
	private static readonly Dictionary<string, string> RecordUnit = new Dictionary<string, string>
	{
		{ "홈런", "홈런" }, { "타점", "타점" }, { "득점", "득점" }, { "안타", "안타" }, { "도루", "도루" },
		{ "승리", "승" }, { "탈삼진", "탈삼진" }, { "홀드", "홀드" }, { "세이브", "세이브" }
	};

	private void PageTitles(GameObject page, List<JsonData> titles)
	{
		for (int i = 0; i < titles.Count && i < PodiumsPerPage; i++)
		{
			JsonData t = titles[i];
			string title = S(t, "n");
			float cx = -190f + (i % 2) * (CellW + 4f), cy = 60f - (i / 2) * (CellH + 6f);
			List<JsonData> first = A(t, "a"), second = A(t, "b"), third = A(t, "c");
			int got = I(t, "mp") * first.FindAll(w => B(w, "me")).Count + I(t, "mp2") * second.FindAll(w => B(w, "me")).Count + I(t, "mp3") * third.FindAll(w => B(w, "me")).Count;
			bool mine = got > 0;
			Box(page, new Vector3(cx, cy, ZBox), CellW, CellH, mine ? new Color(0.42f, 0.32f, 0.05f, 0.5f) : new Color(0f, 0f, 0f, 0.38f));
			float headY = cy + CellH / 2f - 14f, left = cx - CellW / 2f + 10f;
			UILabel head = Text(page, title, new Vector3(left, headY, ZText), 16f, Sky, UIWidget.Pivot.Left);
			if (mine)
			{
				Text(page, "우리 구단 +" + Man(got) + " MP", new Vector3(cx + CellW / 2f - 10f, headY, ZText), 13f, Gold, UIWidget.Pivot.Right);
			}
			if (first.Count == 0)
			{
				Text(page, "수상자 없음", new Vector3(cx, cy + 6f, ZText), 15f, Grey, UIWidget.Pivot.Center);
				Text(page, S(t, "v"), new Vector3(cx, cy - 18f, ZText), 12f, Grey, UIWidget.Pivot.Center, CellW - 40f);
				continue;
			}
			// The players in rank order with their rank and record: 1위 (all listed winners), 2위, 3위.
			List<KeyValuePair<JsonData, int>> ranked = new List<KeyValuePair<JsonData, int>>();
			first.ForEach(w => ranked.Add(new KeyValuePair<JsonData, int>(w, 1)));
			second.ForEach(w => ranked.Add(new KeyValuePair<JsonData, int>(w, 2)));
			third.ForEach(w => ranked.Add(new KeyValuePair<JsonData, int>(w, 3)));
			int winners = first.Count + I(t, "xa");
			// A tie the steps cannot all show: how many share it, by the title's name.
			string shared = winners > 1 ? "공동 1위 " + winners + "명" : second.Count > 1 ? "공동 2위 " + second.Count + "명" : string.Empty;
			if (shared.Length > 0)
			{
				Text(page, shared, new Vector3(left + head.relativeSize.x * 16f + 8f, headY, ZText), 12f, Grey, UIWidget.Pivot.Left);
			}
			string[] value = { S(t, "v"), S(t, "v2"), S(t, "v3") };
			float floor = cy - CellH / 2f + 4f;
			// Middle, left, right.
			float[] slotX = { cx, cx - 122f, cx + 122f };
			for (int slot = 0; slot < 3 && slot < ranked.Count; slot++)
			{
				JsonData p = ranked[slot].Key;
				int rank = ranked[slot].Value, r = rank - 1;
				float x = slotX[slot], high = StepHigh[r], top = floor + high;
				Box(page, new Vector3(x, floor + high / 2f, ZBox), 112f, high, StepColour[r]);
				Box(page, new Vector3(x, top - 1f, ZMark), 112f, 2f, new Color(1f, 1f, 1f, 0.35f));
				UILabel digit = Text(page, rank.ToString(), new Vector3(x, floor + high / 2f, ZText), StepDigit[r], Color.white, UIWidget.Pivot.Center);
				digit.effectStyle = UILabel.Effect.Shadow;
				digit.effectColor = new Color(0f, 0f, 0f, 0.55f);
				// The card and its lines rise together from the step (PopIn): the card on the left of the step, its lines on the right,
				// from the card's top down — the player, the record, the club (two lines if long).
				GameObject stand = NGUITools.AddChild(page);
				stand.name = "Place" + rank;
				stand.transform.localPosition = new Vector3(x, top, 0f);
				const float cardScale = 0.46f, cardH = 127f * cardScale;
				Card(stand, I(p, "c"), new Vector3(-36f, cardH / 2f + 1f, ZText), cardScale);
				const float textX = -13f, textW = 77f;
				float cardTop = cardH + 1f;
				Text(stand, S(p, "n"), new Vector3(textX, cardTop - 7f, ZText), 13f, B(p, "me") ? Gold : Color.white, UIWidget.Pivot.Left, textW);
				Record(stand, title, value[r], new Vector3(textX, cardTop - 24f, ZText), textW);
				Text(stand, S(p, "t"), new Vector3(textX, cardTop - 42f, ZText), 11f, B(p, "me") ? Gold : Grey, UIWidget.Pivot.Left, textW, 2);
				// The right step first, then the left, then the middle.
				PopIn(stand.transform, i * 0.25f + (slot == 2 ? 0f : slot == 1 ? 0.12f : 0.24f));
			}
		}
	}

	// "17승", "197탈삼진"; a rate title's name in small grey before its number ("타율 .374", "평균자책 1.34").
	private void Record(GameObject parent, string title, string value, Vector3 pos, float width)
	{
		if (string.IsNullOrEmpty(value))
		{
			return;
		}
		string unit;
		if (RecordUnit.TryGetValue(title, out unit))
		{
			Text(parent, value + unit, pos, 14f, Sky, UIWidget.Pivot.Left, width);
			return;
		}
		UILabel name = Text(parent, title, pos, 11f, Grey, UIWidget.Pivot.Left);
		float w = name.relativeSize.x * 11f + 3f;
		Text(parent, value, pos + new Vector3(w, 0f, 0f), 14f, Sky, UIWidget.Pivot.Left, Mathf.Max(20f, width - w));
	}

	// ---- the cards rising onto the steps ---------------------------------------------------------------------------

	private readonly List<KeyValuePair<Transform, float>> m_pops = new List<KeyValuePair<Transform, float>>();

	private const float PopTime = 0.3f;

	private void PopIn(Transform stand, float delay)
	{
		stand.localScale = new Vector3(1f, 0.001f, 1f);
		m_pops.Add(new KeyValuePair<Transform, float>(stand, Time.realtimeSinceStartup + delay));
	}

	private void PopStep()
	{
		float now = Time.realtimeSinceStartup;
		for (int k = m_pops.Count - 1; k >= 0; k--)
		{
			Transform stand = m_pops[k].Key;
			if (stand == null)
			{
				m_pops.RemoveAt(k);
				continue;
			}
			float t = Mathf.Clamp01((now - m_pops[k].Value) / PopTime);
			// Up from the step with a little overshoot (ease-out-back).
			float u = t - 1f;
			float s = 1f + 2.70158f * u * u * u + 1.70158f * u * u;
			stand.localScale = new Vector3(1f, Mathf.Max(0.001f, s), 1f);
			if (t >= 1f)
			{
				stand.localScale = Vector3.one;
				m_pops.RemoveAt(k);
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

	private const float LetterRise = 0.4f;

	// A label in the lobby popup's font, its letters centred on pos (LetterRise). width > 0: longer text is shrunk to it (one line) or
	// wrapped (lines > 1).
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
		// 2026-11-19 23:5x (user: "순위, 엠블렘, 기타 등등의 글들이 높이가 안 맞는 것 같은데, 그리고 띠상자 바깥으로 글이 삐져 나가는 것 같음 …
		// 매번 지적 대상인데, 변하는게 없어"): this font's letters stand 0.4 × the size above a centre-pivot label's place (measured on
		// tester_1119h: 15 pt names 9 px over their row and its emblem, the 20 pt "1" 12 px over its step's middle). Placed that much
		// lower, a label's letters are centred where it is put — on its row, its band, its step.
		go.transform.localPosition = pos - new Vector3(0f, LetterRise * size, 0f);
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
