using System;
using UnityEngine;

// Ma9M port (systems session, 2026-11-20 01:0x; user: "조합 관련된건 한 번 눌러서 바로 올라가는게 편하고, 팀 로스터, 앨범에서는 한 번
// 눌렀을 때 선수 정보가 나오는게 편하거든"): a tap and a long press (UICamera's OnLongPress, 0.7 s) on a list item that is not a card
// slot (PlayerCardSlot has its own tap mode) — the 팀컬러 list: a tap puts the team colour in its slot, a long press shows its
// description. The release after a long press, and a swipe of the list, are not taps.
public class Ma9MTapHold : MonoBehaviour
{
	public Action<GameObject> Tap;

	public Action<GameObject> Hold;

	private bool m_held;

	public static Ma9MTapHold On(GameObject go, Action<GameObject> tap, Action<GameObject> hold)
	{
		Ma9MTapHold t = go.GetComponent<Ma9MTapHold>() ?? go.AddComponent<Ma9MTapHold>();
		t.Tap = tap;
		t.Hold = hold;
		return t;
	}

	private void OnPress(bool pressed)
	{
		if (pressed)
		{
			m_held = false;
		}
	}

	private void OnLongPress()
	{
		m_held = true;
		if (Hold != null)
		{
			Hold(base.gameObject);
		}
	}

	private void OnClick()
	{
		if (m_held)
		{
			m_held = false;
			return;
		}
		if (PlayerCardSlot.Ma9MWasDrag())
		{
			return;
		}
		if (Tap != null)
		{
			Tap(base.gameObject);
		}
	}
}
