using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary;
using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "허슬 플레이 모음을 만들어서 클립을 재생해줄 수 있는 메뉴 … 유튜브 쇼츠
// 영상처럼"): the season's best defensive plays (server HustlePlays: 삼중살, 홈 보살, 펜스·다이빙 캐치 …), listed on
// 일정 및 결과's third page (Ma9MHustlePage) and played back one after another in 3D. A clip is no video: the server
// sends its game's broadcast and the clip's scene plays as a one-scene highlight, its slow-motion replay included
// (Ma9MScriptPlayer). Clips of other games load the broadcast scene again for their park and uniforms. On the
// broadcast screen this component draws the clip's title, the count and 다음.
public class Ma9MHustle : MonoBehaviour
{
	public static readonly string[] KindNames = { "", "삼중살", "홈 보살", "펜스 캐치", "다이빙 캐치", "외야 보살", "라이너 병살", "홈 승부", "다이빙 스톱", "병살" };

	// One line of the server's list: id|season|game|scene|kind|grade|game label|inning|emblem|fielding club|position|
	// fielder|batter|fielding club's runs|batting club's runs|batting club.
	public sealed class Clip
	{
		public int Id, Season, Game, Scene, Kind, Grade, Emblem, FieldRuns, BatRuns;

		public string GameLabel, Inning, Club, Pos, Fielder, Batter, BatClub;

		public string KindName => Kind > 0 && Kind < KindNames.Length ? KindNames[Kind] : string.Empty;

		public static Clip Parse(string line)
		{
			string[] f = line.Split('|');
			if (f.Length < 16) return null;
			int I(int k) => int.TryParse(f[k], out int v) ? v : 0;
			return new Clip
			{
				Id = I(0), Season = I(1), Game = I(2), Scene = I(3), Kind = I(4), Grade = I(5), GameLabel = f[6], Inning = f[7], Emblem = I(8),
				Club = f[9], Pos = f[10], Fielder = f[11], Batter = f[12], FieldRuns = I(13), BatRuns = I(14), BatClub = f[15]
			};
		}
	}

	public static List<Clip> Queue = new List<Clip>();

	public static int Index;

	// 연속 재생: on to the next clip when one ends.
	public static bool All;

	// Clips are playing (from the request until the page is back).
	public static bool Active;

	// A clip's game asked for and not here yet.
	public static bool Waiting;

	// The schedule screen opens on this page (back from the clips).
	public static bool BackToPage;

	// DataCenter.OnEndScene_UpdateData keeps the state before this scene while the game is read through.
	public static int TargetScene = -1;

	public static HighlightScene Captured;

	// Dev ("-ma9mhighlight <game>:<scene>"): a game's highlights are that one scene.
	public static int DevScene = -1;

	private static Packet.PKS_PACKET s_handled;

	private float m_shown;

	public static Clip Current => Active && Index >= 0 && Index < Queue.Count ? Queue[Index] : null;

	// Common_TopMenu leaves the broadcast reply alone when it answers a clip.
	public static bool Claims(Packet.PKS_PACKET packet) => Waiting || ReferenceEquals(packet, s_handled);

	public static void Play(List<Clip> clips, int index, bool all)
	{
		if (clips == null || index < 0 || index >= clips.Count) return;
		Queue = clips;
		Index = index;
		All = all;
		Active = true;
		BackToPage = true;
		Request();
	}

	private static void Request()
	{
		Clip c = Queue[Index];
		Waiting = true;
		NetworkConnector.RemoveProcessCallBack(OnPacket);
		NetworkConnector.AddProcessCallBack(OnPacket);
		SceneChanger.ShowLoadingTransparent();
		DataCenter.instance.SetTargetGame(c.Game, 0, eBroadcastType.HIGHLIGHT, eMatchType.REGULAR_MATCH);
		Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT p = new Ma9MPacket.PKS_CS_OTHER_GAME_HIGHLIGHT();
		p.clip.n = c.Id;
		NetworkConnector.SendPacket(p);
		Debug.Log("[Ma9MHustle] clip " + c.Id + " (" + (Index + 1) + "/" + Queue.Count + "): game " + c.Game + " scene " + c.Scene + " " + c.KindName);
	}

	private static void OnPacket(Packet.PKS_PACKET packet)
	{
		if (!Waiting || (Ma9MPacket.SUB)packet.GetSUB() != Ma9MPacket.SUB.PK_SC_ENTER_BROADCAST) return;
		Waiting = false;
		s_handled = packet;
		NetworkConnector.RemoveProcessCallBack(OnPacket);
		Ma9MPacket.PKS_SC_ENTER_BROADCAST p = (Ma9MPacket.PKS_SC_ENTER_BROADCAST)packet;
		Clip c = Current;
		if (c != null && p.game_num.n != 0)
		{
			DataCenter dc = DataCenter.instance;
			dc.SetTargetGame(c.Game, p.result_num.n, eBroadcastType.HIGHLIGHT, eMatchType.REGULAR_MATCH);
			dc.SetHighlightPlay(true);
			dc.Initialize(p);
			TargetScene = c.Scene;
			Captured = null;
			dc.FastForwardingScene();
			TargetScene = -1;
			dc.m_HighlightResult.Clear();
			if (Captured != null)
			{
				dc.m_HighlightResult.Add(Captured);
				BroadcastNet.pastScene = "Scene_ScheduleAndResult";
				TopMenu_DataCenter.currentRosterType = eRosterType.ROSTER_COMMON;
				Time.timeScale = 1f;
				// Forced: the next clip comes from the broadcast screen itself (SceneChanger ignores the scene it is in).
				SceneChanger.LoadLevel("Scene_BroadCast", true);
				return;
			}
		}
		// The game is gone (or the scene is not in it): on to the next, else back to the list.
		Debug.LogWarning("[Ma9MHustle] clip " + (c != null ? c.Id : 0) + " has no game");
		if (All && Index + 1 < Queue.Count)
		{
			Index++;
			Request();
			return;
		}
		SceneChanger.HideLoadingTransparent();
		if (SceneChanger.GetLoadedLevelName() == "Scene_BroadCast")
		{
			Stop();
			return;
		}
		Active = false;
		PopupManager.ViewPopup1(Localization.instance.Get("Message"), Localization.instance.Get("Ma9M_Hustle_Gone"), Localization.instance.Get("OK"), null);
	}

	// HighlightManager: the clip's scene (and its replay) is over.
	public static void OnClipEnd()
	{
		if (Waiting) return;
		if (All && Index + 1 < Queue.Count)
		{
			Next();
			return;
		}
		Stop();
	}

	private static void Next()
	{
		Ma9MScriptPlayer.Finish();
		Time.timeScale = 1f;
		Index++;
		Request();
	}

	// Where a game's highlights go back to (BroadcastNet.pastScene): the screen they were opened from, else the lobby
	// (the unconfirmed-games screen would start over).
	public static string HighlightReturn()
	{
		string s = BroadcastNet.pastScene;
		return s == "Scene_ScheduleAndResult" || s == "Scene_PostSeason" ? s : "Scene_MainLobby";
	}

	// The back button during highlights or clips (the original handled only the live and text broadcasts).
	public static void Back()
	{
		if (Active)
		{
			Stop();
			return;
		}
		Ma9MScriptPlayer.Finish();
		Time.timeScale = 1f;
		DataCenter.instance.SetHighlightPlay(false);
		DataCenter.instance.BroadcastType = eBroadcastType.MAX;
		SceneChanger.LoadLevel(HighlightReturn());
	}

	// Back to the list (the last clip over, or the back button on the broadcast screen).
	public static void Stop()
	{
		Ma9MScriptPlayer.Finish();
		Time.timeScale = 1f;
		Active = false;
		Waiting = false;
		NetworkConnector.RemoveProcessCallBack(OnPacket);
		DataCenter.instance.SetHighlightPlay(false);
		DataCenter.instance.BroadcastType = eBroadcastType.MAX;
		SceneChanger.LoadLevel("Scene_ScheduleAndResult");
	}

	private void Start()
	{
		m_shown = Time.realtimeSinceStartup;
	}

	private void OnGUI()
	{
		Clip c = Current;
		if (c == null || Waiting) return;
		float h = Screen.height, w = Screen.width;
		// The title for the first seconds of the clip, from the windup.
		if (Time.realtimeSinceStartup - m_shown < 6f)
		{
			GUIStyle big = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(h * 0.045f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
			big.normal.textColor = new Color(1f, 0.85f, 0.2f);
			GUIStyle small = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(h * 0.03f), alignment = TextAnchor.MiddleCenter };
			small.normal.textColor = Color.white;
			GUI.Box(new Rect(w * 0.22f, h * 0.2f, w * 0.56f, h * 0.08f), c.KindName + "  ·  " + c.Pos + " " + c.Fielder, big);
			GUI.Box(new Rect(w * 0.22f, h * 0.28f, w * 0.56f, h * 0.06f),
				c.Season + "시즌 " + c.GameLabel + " " + c.Inning + "  ·  " + c.Club + " " + c.FieldRuns + " : " + c.BatRuns + " " + c.BatClub + "  ·  타자 " + c.Batter, small);
		}
		if (All)
		{
			GUIStyle st = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(h * 0.035f), fontStyle = FontStyle.Bold };
			string count = (Index + 1) + " / " + Queue.Count;
			if (Index + 1 < Queue.Count)
			{
				if (GUI.Button(new Rect(w * 0.8f, h * 0.2f, w * 0.17f, h * 0.08f), count + "   다음 >", st)) Next();
			}
			else
			{
				GUI.Box(new Rect(w * 0.8f, h * 0.2f, w * 0.17f, h * 0.08f), count, st);
			}
		}
	}
}
