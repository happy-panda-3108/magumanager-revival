using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary.PacketType;
using UnityEngine;

// Ma9M port (user, 2026-10-21: "리그 종료 때 어떤 팀의 어떤 선수가 수상했는지 보여 주는 화면 추가"): the season's
// 시상식 from the server (PKS_SC_POPUP type 9, LeagueAwards): pages of title, subtitle and lines, shown in the
// lobby's LeagueChangePopup with 이전 / 다음 (and 확인 on the last page). Pages wait here until the lobby is
// up and no common popup (the 시즌 완주 보너스 comes at the same lobby visit) is on screen.
public static class Ma9MAwards
{
	public const byte PopupType = 9;

	private static readonly List<string[]> s_pages = new List<string[]>();

	private static bool s_open;

	// The team the pages belong to (TopMenu_DataCenter.m_teamName when they came).
	private static string s_owner;

	// 2026-11-05 (user: a ceremony seen on one account of the phone, the next account's never came): the state is static, so
	// leaving the lobby or switching accounts with the popup open left s_open set and every later ceremony waiting for good.
	// A popup that is no longer on screen counts as closed, and another team's pages are dropped when new ones come.
	public static bool Pending
	{
		get
		{
			if (s_open && !OnScreen())
			{
				s_open = false;
			}
			// A season result screen waiting (Ma9MSeasonResult, 2026-11-10) counts too: the lobby opens it the same way.
			return Ma9MSeasonResult.Pending || (!s_open && s_pages.Count > 0);
		}
	}

	private static bool OnScreen()
	{
		UIPanel panel = PanelManager.GetPanel("LeagueChagePopupPanel");
		return panel != null && panel.gameObject.activeSelf;
	}

	public static int Count
	{
		get
		{
			return s_pages.Count;
		}
	}

	public static void Receive(PArray<PLongString> messages)
	{
		if (messages == null || messages.array == null)
		{
			return;
		}
		if (s_owner != TopMenu_DataCenter.m_teamName)
		{
			s_pages.Clear();
			s_open = false;
			s_owner = TopMenu_DataCenter.m_teamName;
		}
		// 2026-11-10: one more string after the pages of three = the season result screen's data (server
		// LeagueAwards.Result.cs). The screen takes the ceremony's place; the pages are for apps before this one.
		if (messages.array.Length % 3 == 1 && Ma9MSeasonResult.Enqueue(messages.array[messages.array.Length - 1].str))
		{
			TryShow();
			return;
		}
		for (int i = 0; i + 2 < messages.array.Length; i += 3)
		{
			s_pages.Add(new string[3] { messages.array[i].str, messages.array[i + 1].str, messages.array[i + 2].str });
		}
		TryShow();
	}

	// Opens the popup on the first page; false while the lobby or another popup is in the way.
	public static bool TryShow()
	{
		if (!Pending || SceneChanger.GetLoadedLevelName() != "Scene_MainLobby")
		{
			return false;
		}
		if (Ma9MSeasonResult.Pending)
		{
			return Ma9MSeasonResult.TryShow();
		}
		if (Ma9MSeasonResult.Showing)
		{
			return false;
		}
		if (Object.FindObjectOfType(typeof(Popup1)) != null)
		{
			return false;
		}
		UIPanel panel = PanelManager.GetPanel("LeagueChagePopupPanel");
		if (panel == null)
		{
			return false;
		}
		LeagueChangePopup popup = panel.GetComponent<LeagueChangePopup>();
		if (popup == null || panel.gameObject.activeSelf)
		{
			return false;
		}
		s_open = true;
		panel.gameObject.SetActive(true);
		popup.ShowAwardPage(0);
		return true;
	}

	public static string[] Page(int index)
	{
		return (index >= 0 && index < s_pages.Count) ? s_pages[index] : null;
	}

	// The popup closed after the last page: these pages are done.
	public static void Done()
	{
		s_pages.Clear();
		s_open = false;
	}
}
