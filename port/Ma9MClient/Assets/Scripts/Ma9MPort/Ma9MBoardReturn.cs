// Ma9M (engine session, 2026-11-18; AI tester kt 40, user "A 버그들 다 고칩시다"): 스코어보드 → 문자중계 (or 하이라이트) →
// 돌아가기 went to the main lobby when the board was opened on the 확인하지 않은 결과 screen, and to the bare list on 일정·결과;
// looking at the next game's board meant starting over. Now the board's owner leaves a note when one of those two
// buttons is pressed (ScoreBoard_Main.Ma9MBeforeLeave), the broadcast's back arms it (BroadcastNet.OnBtn_Back,
// Ma9MHustle.Back), and the owner's screen, loaded again, opens that game's board once more.
//  - "unconfirmed": the list as it was before (the server's list no longer holds the games already opened), with how
//    far it had been opened (m_confirmTargetNum), so nothing is confirmed or stamped twice.
//  - "myteam": 일정·결과's 우리 팀 page; the board of that game again.
// Any other way out of the broadcast leaves the note unarmed, and the next screen that looks drops it.
public static class Ma9MBoardReturn
{
	public static string Owner { get; private set; }

	public static string Scene { get; private set; }

	public static int Game { get; private set; }

	// The 확인하지 않은 결과 list and its confirm mark.
	public static object Snapshot { get; private set; }

	public static byte ConfirmTarget { get; private set; }

	private static bool s_armed;

	public static void Note(string owner, int game, object snapshot = null, byte confirmTarget = 0)
	{
		Owner = owner;
		Scene = SceneChanger.GetLoadedLevelName();
		Game = game;
		Snapshot = snapshot;
		ConfirmTarget = confirmTarget;
		s_armed = false;
	}

	// The scene the broadcast's back should load for the note, or null when there is none for pastScene.
	public static string ReturnScene(string pastScene)
	{
		return (Owner != null && Scene == pastScene) ? Scene : null;
	}

	public static void Arm()
	{
		s_armed = Owner != null;
	}

	// The owner's screen asks once: true (and the note) only right after the broadcast's back armed it.
	public static bool Take(string owner)
	{
		bool ok = s_armed && Owner == owner && Scene == SceneChanger.GetLoadedLevelName();
		if (!ok)
		{
			Clear();
		}
		s_armed = false;
		return ok;
	}

	public static void Clear()
	{
		Owner = null;
		Scene = null;
		Game = 0;
		Snapshot = null;
		ConfirmTarget = 0;
		s_armed = false;
	}
}
