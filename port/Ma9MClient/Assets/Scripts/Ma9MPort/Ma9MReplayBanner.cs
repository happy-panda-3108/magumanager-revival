using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "경기 중계랑 스코어보드가 다름 … 무슨 일이 일어난거지?"): the lobby's 중계
// pressed between two slots plays the game just finished from its first pitch (the next one is not on yet), and it
// looked live: the user watched PO 2차전 through while PO 3차전 was played, then got 3차전's board. Such a broadcast now
// says so for its first seconds. The server sends scene_time 0 only for a finished game (live is 1 s on).
public sealed class Ma9MReplayBanner : MonoBehaviour
{
	private float m_from = -1f;

	private void OnGUI()
	{
		int game = Ma9MLiveResume.ReplayOf;
		if (game <= 0 || DataCenter.instance.GetHighlightPlay()) return;
		if (m_from < 0) m_from = Time.realtimeSinceStartup;
		if (Time.realtimeSinceStartup - m_from > 7f) return;
		float h = Screen.height, w = Screen.width;
		GUIStyle big = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(h * 0.045f), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
		big.normal.textColor = new Color(1f, 0.85f, 0.2f);
		GUIStyle small = new GUIStyle(GUI.skin.box) { fontSize = Mathf.RoundToInt(h * 0.03f), alignment = TextAnchor.MiddleCenter };
		small.normal.textColor = Color.white;
		string which = game <= 144 ? "정규시즌 " + game + "경기" : "포스트시즌 경기";
		GUI.Box(new Rect(w * 0.22f, h * 0.2f, w * 0.56f, h * 0.08f), "지난 경기 다시보기  ·  " + which, big);
		GUI.Box(new Rect(w * 0.1f, h * 0.28f, w * 0.8f, h * 0.06f), "이미 끝난 경기입니다. 다음 경기는 시작 후 중계 버튼으로 생중계를 볼 수 있습니다.", small);
	}
}
