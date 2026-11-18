using UnityEngine;

// Ma9M port (2026-11-17, AI testers 두산 8 · kt 4: "창을 닫는 누름이 뒤 카드까지 눌러 상세가 열림"): a message window (Popup1-4) or
// an 업적 box (MissionCompleteBox) closed by a tap leaves an invisible full-screen collider in its place for a moment, so the
// next quick tap (the second half of a double tap, an impatient repeat) lands on nothing instead of on the card or button
// that was under the window. Seen in the 11-17 game.logs of both testers: "[Ma9MMission] box closed by a tap" and, from
// the same double tap, PK_CS_CARD_DETAIL_INFO 0.25 s later (계약관리: a card opens its info on a single tap). A window opened
// by the closing tap's own callback sits behind the shield too, so a double tap on 확인 no longer closes the next window
// unread. Only taps (UICamera is delivering a touch); the back key and the 5-second close of a box raise nothing.
public class Ma9MTapShield : MonoBehaviour
{
	public const float Seconds = 0.3f;

	private float m_until;

	// From PanelManager.RemovePanel, before the panel goes.
	public static void AfterPopupTap(UIPanel panel)
	{
		if (panel == null || UICamera.currentTouch == null)
		{
			return;
		}
		GameObject go = panel.gameObject;
		if (go.GetComponent<Popup1>() == null && go.GetComponent<Popup2>() == null && go.GetComponent<Popup3>() == null && go.GetComponent<Popup4>() == null && go.GetComponent<MissionCompleteBox>() == null)
		{
			return;
		}
		Transform parent = panel.transform.parent;
		if (parent == null)
		{
			return;
		}
		GameObject shield = new GameObject("Ma9MTapShield");
		shield.layer = go.layer;
		Transform t = shield.transform;
		t.parent = parent;
		t.localRotation = Quaternion.identity;
		t.localScale = Vector3.one;
		// In front of the closed window's own buttons (panel z -1 … -2) and of a window that takes its place.
		t.localPosition = new Vector3(0f, 0f, panel.transform.localPosition.z - 3f);
		BoxCollider box = shield.AddComponent<BoxCollider>();
		box.isTrigger = true;
		box.size = new Vector3(4000f, 4000f, 0f);
		shield.AddComponent<Ma9MTapShield>().m_until = Time.realtimeSinceStartup + Seconds;
	}

	// Ma9M (engine session, 2026-11-18, AI tester 쌍방울 23 / 남은일 A4): a screen that comes up by itself (the 확인하지 않은
	// 결과 screen, sent there on the way back to the lobby) is covered the same way for its first moment, so the taps meant
	// for the lobby's menu don't press its buttons and rows. Taps only meet the shield; the back key still works.
	public static Ma9MTapShield Cover(UIPanel panel, float seconds)
	{
		if (panel == null || panel.transform.parent == null)
		{
			return null;
		}
		GameObject shield = new GameObject("Ma9MTapShield");
		shield.layer = panel.gameObject.layer;
		Transform t = shield.transform;
		t.parent = panel.transform.parent;
		t.localRotation = Quaternion.identity;
		t.localScale = Vector3.one;
		t.localPosition = new Vector3(0f, 0f, panel.transform.localPosition.z - 5f);
		BoxCollider box = shield.AddComponent<BoxCollider>();
		box.isTrigger = true;
		box.size = new Vector3(4000f, 4000f, 0f);
		Ma9MTapShield s = shield.AddComponent<Ma9MTapShield>();
		s.m_until = Time.realtimeSinceStartup + seconds;
		return s;
	}

	// Keeps the shield up for at least this long from now.
	public void HoldAtLeast(float seconds)
	{
		m_until = Mathf.Max(m_until, Time.realtimeSinceStartup + seconds);
	}

	private void Update()
	{
		if (Time.realtimeSinceStartup >= m_until)
		{
			Destroy(base.gameObject);
		}
	}
}
