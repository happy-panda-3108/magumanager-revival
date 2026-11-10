using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary;
using UnityEngine;

// Ma9M port (engine session, 2026-10-31): 일정 및 결과's third page, 허슬 플레이 (Ma9MHustle). It is a copy of the 우리팀
// page beside the 리그 page (the drag pages are 1200 apart), with its rows, frames and buttons: the day buttons pick
// 우리팀 / 리그 전체 and the season, the columns show 경기 · 이닝 · 장면 · 수비 선수 · 타자 · 평점, and the bottom
// buttons play the chosen clip or all of them from it. Every page gets a third dot.
public class Ma9MHustlePage : MonoBehaviour
{
	private const int Page = 2;

	private const int BtnMine = 0, BtnLeague = 1, BtnOlder = 4, BtnNewer = 5;

	// What the page showed, kept for the way back from the clips.
	private static int s_scope, s_season, s_selected;

	private ScheduleAndResult_Main m_main;

	private UIGrid m_grid;

	private UIDraggablePanel m_scroll;

	private GameObject m_itemPrefab;

	private ScheduleAndResult_DayButton[] m_buttons;

	private UIImageButton m_play, m_playAll;

	private UILabel m_season, m_empty;

	private readonly List<ScheduleAndResult_MyTeamItem> m_rows = new List<ScheduleAndResult_MyTeamItem>();

	private List<Ma9MHustle.Clip> m_clips = new List<Ma9MHustle.Clip>();

	private int[] m_seasons = Array.Empty<int>();

	private bool m_listening, m_asked;

	// Builds the page from the 우리팀 page (ScheduleAndResult_Main.Start); back from the clips it opens on it.
	public static Ma9MHustlePage Attach(ScheduleAndResult_Main main)
	{
		if (main == null || main.panel_MyTeamResult == null || main.panel_AllTeamResult == null) return null;
		ScheduleAndResult_MyTeamResult src = main.panel_MyTeamResult;
		Transform all = main.panel_AllTeamResult.transform;
		float spacing = all.localPosition.x - src.transform.localPosition.x;
		if (Mathf.Abs(spacing) < 1f) spacing = 1200f;
		GameObject go = Instantiate(src.gameObject, src.transform.parent);
		go.name = "Panel_HustlePlays";
		go.transform.localPosition = all.localPosition + new Vector3(spacing, 0f, 0f);
		go.transform.localScale = src.transform.localScale;
		ScheduleAndResult_MyTeamResult copy = go.GetComponent<ScheduleAndResult_MyTeamResult>();
		Ma9MHustlePage page = go.AddComponent<Ma9MHustlePage>();
		page.m_main = main;
		page.m_grid = copy.grid_ResultItem;
		page.m_scroll = copy.panel_ResultItemList;
		page.m_itemPrefab = copy.myTeamResultItemPrefab;
		page.m_buttons = copy.btn_Days;
		page.m_play = copy.btn_Highlight;
		page.m_playAll = copy.btn_Broadcast;
		page.m_season = copy.label_Season;
		UIImageButton third = copy.btn_Scoreboard;
		DestroyImmediate(copy);
		if (page.m_grid != null)
		{
			for (int i = page.m_grid.transform.childCount - 1; i >= 0; i--) Destroy(page.m_grid.transform.GetChild(i).gameObject);
		}
		if (third != null) third.gameObject.SetActive(false);
		page.Build();
		// Three dots on every page.
		Dots(src.transform, 0);
		Dots(all, 1);
		Dots(go.transform, 2);
		DragPage drag = src.GetComponentInParent<DragPage>();
		if (drag != null)
		{
			drag.maxPage = Mathf.Max(drag.maxPage, Page);
			if (Ma9MHustle.BackToPage)
			{
				drag.curPage = Page;
				Vector3 p = drag.transform.localPosition;
				drag.transform.localPosition = new Vector3(-Page * 1200f, p.y, p.z);
			}
		}
		if (Ma9MHustle.BackToPage)
		{
			Ma9MHustle.BackToPage = false;
			page.Show();
		}
		return page;
	}

	private static void Dots(Transform panel, int selected)
	{
		Transform boxes = Find(panel, "SlideBoxes");
		if (boxes == null) return;
		List<UISprite> dots = new List<UISprite>();
		foreach (Transform t in boxes) { UISprite s = t.GetComponent<UISprite>(); if (s != null) dots.Add(s); }
		if (dots.Count < 2) return;
		dots.Sort((a, b) => a.transform.localPosition.x.CompareTo(b.transform.localPosition.x));
		if (dots.Count == 2)
		{
			float d = dots[1].transform.localPosition.x - dots[0].transform.localPosition.x;
			GameObject extra = Instantiate(dots[1].gameObject, boxes);
			extra.name = "SlideBox5";
			extra.transform.localPosition = dots[1].transform.localPosition;
			extra.transform.localScale = dots[1].transform.localScale;
			foreach (UISprite s in dots) s.transform.localPosition -= new Vector3(d, 0f, 0f);
			dots.Add(extra.GetComponent<UISprite>());
		}
		for (int i = 0; i < dots.Count; i++) dots[i].spriteName = i == selected ? "slide_box_full" : "slide_box_empty";
	}

	private static Transform Find(Transform root, string name)
	{
		foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
		{
			if (t.name == name) return t;
		}
		return null;
	}

	private static void SetText(Transform root, string name, string text)
	{
		Transform t = Find(root, name);
		UILabel label = t != null ? t.GetComponent<UILabel>() : null;
		if (label == null) return;
		UILocalize loc = label.GetComponent<UILocalize>();
		if (loc != null) Destroy(loc);
		label.text = text;
	}

	private static string L(string key) => Localization.instance.Get(key);

	private void Build()
	{
		Transform root = transform;
		SetText(root, "Label_Title", L("Ma9M_Hustle_Title"));
		SetText(root, "Label_Gamenum", L("Ma9M_Hustle_ColGame"));
		SetText(root, "Label_StartTime", L("Ma9M_Hustle_ColInning"));
		SetText(root, "Label_Result", L("Ma9M_Hustle_ColPlay"));
		SetText(root, "Label_Teamname", L("Ma9M_Hustle_ColFielder"));
		SetText(root, "Label_Score", L("Ma9M_Hustle_ColBatter"));
		SetText(root, "Label_AcqruiedMP", L("Ma9M_Hustle_ColGrade"));
		string[] titles = { L("Ma9M_Hustle_Mine"), L("Ma9M_Hustle_League"), null, null, L("Ma9M_Hustle_Older"), L("Ma9M_Hustle_Newer"), null };
		for (int i = 0; m_buttons != null && i < m_buttons.Length; i++)
		{
			ScheduleAndResult_DayButton b = m_buttons[i];
			if (b == null) continue;
			string title = i < titles.Length ? titles[i] : null;
			if (title == null)
			{
				b.gameObject.SetActive(false);
				continue;
			}
			b.Initialize(title, i);
			b.SetCallback(gameObject, "OnButton");
		}
		Hook(m_play, "Ma9M_Hustle_Play", "OnPlay");
		Hook(m_playAll, "Ma9M_Hustle_PlayAll", "OnPlayAll");
		// The list's empty note, in the header's style, in the middle of the list.
		Transform head = Find(root, "Label_Gamenum");
		if (head != null && m_scroll != null)
		{
			GameObject note = Instantiate(head.gameObject, m_scroll.transform.parent);
			note.name = "Label_HustleEmpty";
			note.transform.localPosition = new Vector3(m_scroll.transform.localPosition.x, m_scroll.transform.localPosition.y - 120f, head.localPosition.z);
			m_empty = note.GetComponent<UILabel>();
			m_empty.pivot = UIWidget.Pivot.Center;
			m_empty.lineWidth = 900;
			m_empty.text = string.Empty;
		}
		Refresh();
	}

	private void Hook(UIImageButton button, string key, string function)
	{
		if (button == null) return;
		// 전체중계 and 하이라이트 have no UIButtonMessage in the prefab (their page sets a listener in code).
		UIButtonMessage m = button.GetComponent<UIButtonMessage>();
		if (m != null)
		{
			m.target = gameObject;
			m.functionName = function;
		}
		else
		{
			UIEventListener.Get(button.gameObject).onClick = _ => SendMessage(function, SendMessageOptions.DontRequireReceiver);
		}
		UILocalize loc = button.GetComponentInChildren<UILocalize>();
		if (loc != null)
		{
			loc.key = key;
			loc.Localize();
		}
	}

	// The page came into view (ScheduleAndResult_Main.ChangePage).
	public void Show()
	{
		Listen();
		if (!m_asked) Ask(s_scope, s_season);
	}

	private void Listen()
	{
		if (m_listening) return;
		NetworkConnector.AddProcessCallBack(ProcessPacket);
		m_listening = true;
	}

	public void Hide()
	{
		if (!m_listening) return;
		NetworkConnector.RemoveProcessCallBack(ProcessPacket);
		m_listening = false;
	}

	private void OnDestroy()
	{
		Hide();
	}

	private void Ask(int scope, int season)
	{
		Listen();
		m_asked = true;
		s_scope = scope;
		Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT p = new Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT();
		p.scope.n = (byte)scope;
		p.season.n = season;
		p.clip.n = 0;
		SceneChanger.ShowLoadingTransparent();
		NetworkConnector.SendPacket(p);
	}

	private void ProcessPacket(Packet.PKS_PACKET packet)
	{
		if ((Ma9MPacket.SUB)packet.GetSUB() != Ma9MPacket.SUB.PK_SC_OTHER_GAME_HIGHLIGHT) return;
		SceneChanger.HideLoadingTransparent();
		Ma9MPacket.PKS_SC_OTHER_GAME_HIGHLIGHT p = (Ma9MPacket.PKS_SC_OTHER_GAME_HIGHLIGHT)packet;
		bool same = p.season.n == s_season;
		s_season = p.season.n;
		m_seasons = p.seasons.array.Select(x => x.n).ToArray();
		m_clips = p.clips.array.Select(x => Ma9MHustle.Clip.Parse(x.str)).Where(c => c != null).ToList();
		if (!same || s_selected >= m_clips.Count) s_selected = 0;
		UpdateList();
		Refresh();
	}

	private void UpdateList()
	{
		foreach (ScheduleAndResult_MyTeamItem r in m_rows)
		{
			if (r != null) Destroy(r.gameObject);
		}
		m_rows.Clear();
		if (m_grid == null || m_itemPrefab == null) return;
		m_grid.transform.DetachChildren();
		for (int i = 0; i < m_clips.Count; i++)
		{
			Ma9MHustle.Clip c = m_clips[i];
			GameObject go = Instantiate(m_itemPrefab);
			go.transform.parent = m_grid.transform;
			go.transform.localPosition = new Vector3(1200f, 0f, 0f);
			go.transform.localScale = Vector3.one;
			ScheduleAndResult_MyTeamItem item = go.GetComponentInChildren<ScheduleAndResult_MyTeamItem>();
			item.label_Gamenum.text = c.GameLabel;
			item.label_StartTime.text = c.Inning;
			item.label_Teamname.text = c.Pos + " " + c.Fielder;
			item.emblem.SetEmblem("38x29", c.Emblem);
			item.label_Score.text = c.Batter;
			item.label_MP.text = c.Grade.ToString();
			// The play's name where the result picture was.
			if (item.sprite_WLDV != null)
			{
				GameObject kind = Instantiate(item.label_StartTime.gameObject, item.label_StartTime.transform.parent);
				kind.name = "Label_Kind";
				Vector3 at = item.sprite_WLDV.transform.localPosition, row = item.label_StartTime.transform.localPosition;
				kind.transform.localPosition = new Vector3(at.x, row.y, row.z);
				UILabel k = kind.GetComponent<UILabel>();
				k.pivot = UIWidget.Pivot.Center;
				k.text = c.KindName;
				k.color = new Color(1f, 0.85f, 0.2f);
				item.sprite_WLDV.gameObject.SetActive(false);
			}
			item.Ma9MSetIndex(i);
			item.SetCallback(gameObject, "OnRow");
			m_rows.Add(item);
		}
		BroadcastMessage("CheckParent", SendMessageOptions.DontRequireReceiver);
		m_grid.Reposition();
		if (m_scroll != null) m_scroll.ResetPosition();
		Select(s_selected);
	}

	private void Select(int i)
	{
		s_selected = i;
		for (int k = 0; k < m_rows.Count; k++)
		{
			if (m_rows[k] == null) continue;
			if (k == i) m_rows[k].SetSelected();
			else m_rows[k].SetUnSelected();
		}
	}

	private void Refresh()
	{
		if (m_season != null) m_season.text = s_season > 0 ? DateTime.Now.Year + L("Year") + " " + s_season + L("Season") : string.Empty;
		int at = Array.IndexOf(m_seasons, s_season);
		for (int i = 0; m_buttons != null && i < m_buttons.Length; i++)
		{
			ScheduleAndResult_DayButton b = m_buttons[i];
			if (b == null) continue;
			UILabel label = b.GetComponentInChildren<UILabel>(true);
			if (i == BtnMine || i == BtnLeague)
			{
				bool on = (i == BtnMine) == (s_scope == 0);
				if (on) b.SetSelected(); else b.SetUnselected();
				if (label != null) label.color = on ? Ma9MColorInfo.commonBlue : Color.white;
			}
			else if (i == BtnOlder || i == BtnNewer)
			{
				// A day button's off look is the chosen day's blue: a season button that has nowhere to go is not shown.
				bool can = i == BtnOlder ? at >= 0 && at + 1 < m_seasons.Length : at > 0;
				b.gameObject.SetActive(can);
				if (label != null) label.color = Color.white;
			}
		}
		bool any = m_clips.Count > 0;
		Ma9MClientUtil.ButtonSetActive(m_play, any);
		Ma9MClientUtil.ButtonSetActive(m_playAll, any);
		if (m_empty != null) m_empty.text = any || !m_asked ? string.Empty : L("Ma9M_Hustle_Empty");
	}

	public void OnButton(int idx)
	{
		switch (idx)
		{
			case BtnMine: if (s_scope != 0) Ask(0, s_season); break;
			case BtnLeague: if (s_scope != 1) Ask(1, s_season); break;
			case BtnOlder:
			{
				int at = Array.IndexOf(m_seasons, s_season);
				if (at >= 0 && at + 1 < m_seasons.Length) Ask(s_scope, m_seasons[at + 1]);
				break;
			}
			case BtnNewer:
			{
				int at = Array.IndexOf(m_seasons, s_season);
				if (at > 0) Ask(s_scope, m_seasons[at - 1]);
				break;
			}
		}
		Refresh();
	}

	public void OnRow(int idx)
	{
		Select(idx);
	}

	public void OnPlay()
	{
		if (m_clips.Count == 0) return;
		Hide();
		Ma9MHustle.Play(m_clips, Mathf.Clamp(s_selected, 0, m_clips.Count - 1), false);
	}

	public void OnPlayAll()
	{
		if (m_clips.Count == 0) return;
		Hide();
		Ma9MHustle.Play(m_clips, Mathf.Clamp(s_selected, 0, m_clips.Count - 1), true);
	}
}
