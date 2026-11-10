using System;
using System.Collections;
using UnityEngine;

// Ma9M port (decision 1 (가), user 2026-11-10 19:0x: "예전 버전 표를 썼다고? 추천안인 (가) + (나) 적용해서 수정합시다"): after a
// server restart an app comes back by its access token (NetworkConnector.SendPKS_CS_REQUEST_ACCESS_TOKEN_LOGIN) without
// passing Scene_ServerSelect, the only place the table bundle is checked (ResourceManager.ServerVersionCheck). It kept the
// tables it had, so a renewal showed the old surcharge rule's 320,000 and the server took 640,000 (AI testers, 11-10).
// Now a token login in a game scene asks the version info again; when the server's table version is not the one this app
// loaded, a popup says so and its 확인 goes through Scene_ServerSelect, which picks the same club again (RechooseTeam) and
// downloads the new tables there as a normal login does.
public class Ma9MTableRecheck : MonoBehaviour
{
	// Scenes where no club is in yet or the normal path checks anyway.
	private static readonly string[] Skip = { "Scene_Start", "Scene_Login", "Scene_WebLogin", "Scene_ServerSelect", "Scene_IntroTutorial", "Scene_Tutorial_CreateTeam", "Scene_Tutorial_PlayerView" };

	// A game on screen: the popup waits until it is over.
	private static readonly string[] Wait = { "Scene_BroadCast", "Scene_SNSBroadcast" };

	private static Ma9MTableRecheck s_runner;

	private static bool s_busy;

	// The club last entered (ServerSelect.SendPKS_CS_CHOOSE_TEAM) and the one ServerSelect should enter by itself.
	public static uint LastTeam;

	public static uint RechooseTeam;

	public static void AfterTokenLogin()
	{
		string scene = SceneChanger.GetLoadedLevelName();
		if (s_busy || LastTeam == 0 || Array.IndexOf(Skip, scene) >= 0)
		{
			return;
		}
		if (s_runner == null)
		{
			GameObject go = new GameObject("Ma9MTableRecheck");
			UnityEngine.Object.DontDestroyOnLoad(go);
			s_runner = go.AddComponent<Ma9MTableRecheck>();
		}
		s_busy = true;
		WebManager.EnqueueWebRequestCallback(ResourceManager.Ma9MVersionInfoUrl(), new Action<string>(OnVersionInfo), new Action<string>(OnError), 1);
	}

	private static void OnError(string error)
	{
		Debug.Log("Ma9MTableRecheck: version info failed: " + error);
		s_busy = false;
	}

	private static void OnVersionInfo(string xml)
	{
		string server = ResourceManager.Ma9MServerTableVersion(xml);
		string loaded = ResourceManager.Ma9MLoadedTableVersion();
		if (string.IsNullOrEmpty(server) || server == loaded || s_runner == null)
		{
			s_busy = false;
			return;
		}
		Debug.Log("Ma9MTableRecheck: server tables " + server + ", loaded " + loaded);
		s_runner.StartCoroutine(s_runner.Ask());
	}

	private IEnumerator Ask()
	{
		while (Array.IndexOf(Wait, SceneChanger.GetLoadedLevelName()) >= 0)
		{
			yield return new WaitForSeconds(1f);
		}
		PopupManager.ViewPopup1(Localization.instance.Get("Message"), "서버의 게임 데이터가 새로 바뀌었습니다.\n확인을 누르면 새 데이터를 받고\n같은 구단으로 다시 들어갑니다.", Localization.instance.Get("OK"), () =>
		{
			RechooseTeam = LastTeam;
			s_busy = false;
			SceneChanger.LoadLevel("Scene_ServerSelect");
		});
	}
}
