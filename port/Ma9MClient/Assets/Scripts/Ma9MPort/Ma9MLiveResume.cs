using Ma9MPackets;
using NetworkLibrary;
using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "홈화면에서 나간 시점부터 다시 들어온 시점까지는 날리고 그 시점부터 중계를
// 이어서"): the league's live broadcast, back from the home screen (any stall over 5 s), joins the game again where the
// server has it now instead of the old "중계 수신 상태가 원활하지 않습니다" exit. The broadcast is entered again (the lobby's
// 중계 보기 request), read through to the present and reloaded, without the lineup board. Highlights only pause.
public static class Ma9MLiveResume
{
	public static bool Waiting;

	// The reloaded broadcast skips the lineup board (BroadCast_MainWindow START).
	public static bool Rejoined;

	// The game the lobby's 중계 is showing when it is a finished one from its start, not live (Ma9MReplayBanner); 0 live.
	public static int ReplayOf;

	private static Packet.PKS_PACKET s_handled;

	// Common_TopMenu leaves the broadcast reply alone when it answers a rejoin.
	public static bool Claims(Packet.PKS_PACKET packet) => Waiting || ReferenceEquals(packet, s_handled);

	public static void Rejoin()
	{
		if (Waiting) return;
		Waiting = true;
		NetworkConnector.RemoveProcessCallBack(OnPacket);
		NetworkConnector.AddProcessCallBack(OnPacket);
		SceneChanger.ShowLoadingTransparent();
		DataCenter.instance.SetTargetGame(0, 0, eBroadcastType.VIDEO, eMatchType.REGULAR_MATCH);
		Ma9MPacket.PKS_CS_ENTER_BROADCAST p = new Ma9MPacket.PKS_CS_ENTER_BROADCAST();
		p.game_num.n = 0;
		p.result_num.n = 0;
		NetworkConnector.SendPacket(p);
		Debug.Log("[Ma9MLiveResume] back to the live broadcast");
	}

	private static void OnPacket(Packet.PKS_PACKET packet)
	{
		if (!Waiting || (Ma9MPacket.SUB)packet.GetSUB() != Ma9MPacket.SUB.PK_SC_ENTER_BROADCAST) return;
		Waiting = false;
		s_handled = packet;
		NetworkConnector.RemoveProcessCallBack(OnPacket);
		Ma9MPacket.PKS_SC_ENTER_BROADCAST p = (Ma9MPacket.PKS_SC_ENTER_BROADCAST)packet;
		if (p.game_num.n == 0)
		{
			SceneChanger.HideLoadingTransparent();
			SceneChanger.LoadLevel("Scene_MainLobby");
			return;
		}
		DataCenter dc = DataCenter.instance;
		dc.SetHighlightPlay(false);
		ReplayOf = p.scene_time.n == 0 ? p.game_num.n : 0;
		dc.Initialize(p);
		dc.FastForwardingScene();
		TopMenu_DataCenter.m_IsRealTimeBroadcast = true;
		TopMenu_DataCenter.currentRosterType = eRosterType.ROSTER_COMMON;
		Time.timeScale = 1f;
		Rejoined = true;
		// Forced: the broadcast screen loads itself again (SceneChanger ignores the scene it is in).
		SceneChanger.LoadLevel("Scene_BroadCast", true);
	}
}
