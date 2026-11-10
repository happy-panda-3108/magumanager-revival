using System.Collections.Generic;
using UnityEngine;

// Ma9M (engine session, 2026-11-10; user: "로비에 있는 동안 경기가 끝나면 자동으로 경기결과확인 화면으로 넘어갑시다. 가령
// 1시간동안 로비에 가만히 있는거면, 첫 20분 뒤 결과확인 화면으로 넘어가고, 이 상태에서 가만히 있으면 20분마다 경기가 하나씩
// 쌓이는 느낌으로"). The original went to the results screen only when the lobby was entered (the server's
// PKS_SC_EXIST_UNCONFIRMED_GAME_RESULT after ENTER_MAIN); a lobby left open kept its old record, MP and NEXT VS (AI testers
// 11-09/11-10), and games ending while the results screen was open never joined its list.
//
// The top bar knows when my game's 경기중 (its first five minutes, the server's result lock) ends: the bar's
// in_broadcast goes from 1 to 0 (Common_TopMenu.ProcessPK_SC_GLOBAL_INFO → GameOver). From then on:
//  - in the lobby, once nothing else is open and nobody is touching the screen: on to the results screen, as the
//    server's packet does;
//  - on the results screen, once no scoreboard or popup is open: the list is asked for again (the new game joins it);
//  - anywhere else: nothing (the lobby's next ENTER_MAIN brings the results screen as before).
public static class Ma9MResultsAuto
{
	// How long the screen must have been left alone before the lobby gives way to the results screen.
	private const float QuietSeconds = 4f;

	// The lobby's own panels (Loader_MainLobby, Loader); any other panel showing is a popup or a menu in the way.
	private static readonly HashSet<string> s_lobbyPanels = new HashSet<string> { "MainMenu", "LockerRoom_Main", "TopMenu", "Background", "UIBackground" };

	private static bool s_pending;

	private static float s_lastTouch;

	private static float s_nextLog;

	private static bool s_onAir;

	// Every top bar that comes (PKS_SC_GLOBAL_INFO: Common_TopMenu and TopMenu_DataCenter.UpdateInfo both take it, whichever
	// is first — the first try watched TopMenu_DataCenter.m_inBroadcast in one of them only, after the other had already
	// set it, and never saw the change): 경기중 that is no longer 경기중 = my game's result can be opened.
	public static void OnBar(bool inBroadcast)
	{
		if (s_onAir && !inBroadcast)
		{
			s_pending = true;
		}
		s_onAir = inBroadcast;
	}

	// The results screen just asked for its list itself.
	public static void Clear()
	{
		s_pending = false;
	}

	// Every frame, from the top bar.
	public static void Tick()
	{
		if (Ma9MInput.GetMouseButton(0) || Ma9MInput.touchCount > 0 || Ma9MInput.anyKeyDown)
		{
			s_lastTouch = Time.realtimeSinceStartup;
		}
		if (!s_pending)
		{
			return;
		}
		string scene = SceneChanger.GetLoadedLevelName();
		if (scene == "Scene_Unconfirmed")
		{
			Unconfirmed_Main results = (Unconfirmed_Main)Object.FindObjectOfType(typeof(Unconfirmed_Main));
			if (results != null && results.Ma9MRefresh())
			{
				s_pending = false;
			}
			return;
		}
		if (scene != "Scene_MainLobby")
		{
			s_pending = false;
			return;
		}
		if (Time.realtimeSinceStartup - s_lastTouch < QuietSeconds || UICamera.selectedObject != null && UICamera.selectedObject.GetComponent<UIInput>() != null)
		{
			return;
		}
		string inTheWay = PanelManager.ActiveOtherThan(s_lobbyPanels);
		if (inTheWay != null || !PopupManager.GetIfAllTutorialBoxHide() || Ma9MAwards.Pending || Ma9MSeasonResult.Showing)
		{
			if (Time.realtimeSinceStartup >= s_nextLog)
			{
				s_nextLog = Time.realtimeSinceStartup + 30f;
				Debug.Log("[Ma9MResultsAuto] a game ended; waiting for the lobby to be free (" + (inTheWay ?? "tutorial or ceremony") + ")");
			}
			return;
		}
		s_pending = false;
		Debug.Log("[Ma9MResultsAuto] a game ended while the lobby was open: on to the results screen");
		TopMenu_DataCenter.m_SceneManage.Push(scene);
		SceneChanger.LoadLevel("Scene_Unconfirmed");
	}
}
