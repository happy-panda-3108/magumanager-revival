using System;
using Ma9MPackets;
using UnityEngine;

// Ma9M port (user 2026-11-11 18:4x via the engine session, 10번 "볼 상한 안에서 가장 강한 라인업을 짜는 기능 … 일단 진행해봅시다",
// 11번 "찍었을 때 가장 승률이 높게 나오는 쪽으로 자동으로 찍어주는 기능"; 21:2x "특성을 가장 승률 높은 조합으로 자동 배분해주는 기능이
// 생길 때 같이 반영합시다"): 특성관리 "추천 배분" and 로스터 "자동 편성". The app asks with REQ_SCENE_TUTORIAL "ma9m:…"; the
// server (AdviceHandlers) works it out and answers with POPUP type 10 [title, text, ok label], shown here with 적용 / 취소
// (POPUP_ACK 10 / 11; types 1 and 2 send 0 / 1), then type 11 [title, text] whose OK refreshes the screen (Refresh, set by
// the screen that asked). A plain message (type 1) comes when there is nothing to advise.
// 2026-11-12 (user 01:0x, phone 1.0.45: "특강화면에서 무슨 버그가 나고 있는 것 같음 팝업이 생겼음"): type 10 is also the 특강 info
// popup (SpecialLecture_DataCenter.Ma9MInfoPopupType), and 1.0.45 took every type 10 for this window. Now the app asks
// "ma9m:v2:…" and the server answers with types 20 / 21, which nothing else uses (server AdviceHandlers.AskType2 / DoneType2).
public static class Ma9MAdvice
{
	public const byte AskType = 20;

	public const byte DoneType = 21;

	public const byte AckApply = 10;

	public const byte AckCancel = 11;

	// The screen's own reload (TeamProperty_Main: ENTER_TEAM_PROPERTY again; Roster_Main: the roster again).
	public static Action Refresh;

	private static float s_waitUntil;

	private static bool s_waiting;

	public static void Request(string what, Action refresh)
	{
		if (s_waiting && Time.realtimeSinceStartup < s_waitUntil)
		{
			return;
		}
		Refresh = refresh;
		s_waiting = true;
		s_waitUntil = Time.realtimeSinceStartup + 20f;
		SceneChanger.ShowLoadingTransparent();
		Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL spacket = new Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL();
		spacket.scene_name.str = "ma9m:v2:" + what;
		NetworkConnector.SendPacket(spacket);
		// A server without this (before its update) answers with nothing of ours: the loading goes after 20 s.
		if (s_timer == null)
		{
			GameObject go = new GameObject("Ma9MAdviceTimer");
			UnityEngine.Object.DontDestroyOnLoad(go);
			s_timer = go.AddComponent<Timer>();
		}
	}

	private static Timer s_timer;

	private class Timer : MonoBehaviour
	{
		private void Update()
		{
			if (s_waiting && Time.realtimeSinceStartup >= s_waitUntil)
			{
				s_waiting = false;
				SceneChanger.HideLoadingTransparent();
				PopupManager.ViewPopup1(Localization.instance.Get("Message"), "서버가 답하지 않았습니다.\n잠시 뒤 다시 해 주세요.", Localization.instance.Get("OK"), null);
			}
		}
	}

	// NetworkConnector.ProcessPKS_SC_POPUP, before its own types: true when the popup was one of ours.
	public static bool Receive(Ma9MPacket.PKS_SC_POPUP packet)
	{
		// The answer is ours or a plain message (1 / 2: nothing to advise, a refusal); any other popup leaves the loading.
		byte type = packet.Type.n;
		if (s_waiting && (type == AskType || type == 1 || type == 2))
		{
			s_waiting = false;
			SceneChanger.HideLoadingTransparent();
		}
		int count = (packet.Messages.array != null) ? Mathf.Max(3, packet.Messages.array.Length) : 3;
		string[] m = new string[count];
		for (int i = 0; i < m.Length; i++)
		{
			m[i] = (packet.Messages.array != null && i < packet.Messages.array.Length && packet.Messages.array[i] != null) ? packet.Messages.array[i].str : null;
		}
		if (type == AskType)
		{
			// [title, text, ok, page…] (2026-11-12: the 자동 편성 window's 자세히 pages).
			string[] pages = new string[count - 2];
			pages[0] = m[1] ?? string.Empty;
			for (int i = 3; i < count; i++)
			{
				pages[i - 2] = m[i] ?? string.Empty;
			}
			Ask(m[0] ?? string.Empty, pages, m[2] ?? "적용");
			return true;
		}
		if (type == DoneType)
		{
			// 2026-11-12 (AI tester 두산 버그 10: after 자동 편성 [적용] the 로스터's 등록하기 sent the old 1군 back — "로스터 등록에 실패하였습니다"
			// and the 선발 등록 제한 of the starter it took out): the screen reloads as soon as the server says it is saved, not only on the
			// popup's OK (a back key or a tap elsewhere left the old 1군 on screen).
			Action refresh = Refresh;
			Refresh = null;
			if (refresh != null)
			{
				refresh();
			}
			PopupManager.ViewPopup1(m[0] ?? string.Empty, m[1] ?? string.Empty, Localization.instance.Get("OK"), null);
			return true;
		}
		return false;
	}

	private static void Ack(byte n)
	{
		Ma9MPacket.PKS_CS_POPUP_ACK spacket = new Ma9MPacket.PKS_CS_POPUP_ACK();
		spacket.Ack.n = n;
		NetworkConnector.SendPacket(spacket);
	}

	// The common popup grown for the lines of its longest page (the message does not wrap; four lines fit the box as it is).
	// More than one page (2026-11-12, AI testers' night run: the 자동 편성 window cut the list to "외 N명"): a 자세히 button between
	// 적용 and 취소 goes through the pages and back to the first.
	private static void Ask(string title, string[] pages, string ok)
	{
		// 2026-11-18 (남은일목록 A7, NC 버그 26 "자동 편성 (팀컬러 유지) 제목이 창 위 테두리에 붙음"): it grew by a guessed 17 per line
		// with the text at 13, stretching the whole box (its title band too). Now as every popup (CLAUDE.md 11): the body size,
		// wrapped at the box, grown for the tallest page with the box drawn in three pieces, scrolling past the tallest.
		Ma9MBookPopup p = Ma9MBookPopup.Create(0f);
		p.Title.text = title;
		p.FitPages(pages);
		bool answered = false;
		p.OnClose = delegate
		{
			if (!answered)
			{
				Ack(AckCancel);
			}
		};
		bool paged = pages.Length > 1;
		p.AddButton(ok, paged ? -135f : -80f, p.BottomRow, paged ? 0.85f : 1f, delegate
		{
			answered = true;
			p.Close();
			Ack(AckApply);
		});
		if (paged)
		{
			int shown = 0;
			UILabel more = null;
			more = p.AddButton("자세히", 0f, p.BottomRow, 0.85f, delegate
			{
				shown = (shown + 1) % pages.Length;
				p.ShowPage(pages[shown]);
				if (more != null)
				{
					// The page shown now, then where the button goes (2026-11-13, AI testers LG 78·kt 51: "자세히 2/2" named the next
					// page, so the one before the last read as the last and the "참고" page after it was never found).
					int last = pages.Length - 1;
					more.text = (shown == 0) ? "자세히" : (shown + "/" + last + ((shown == last) ? " · 요약" : " · 다음"));
				}
			});
		}
		p.AddButton(Localization.instance.Get("Cancel"), paged ? 135f : 80f, p.BottomRow, paged ? 0.85f : 1f, p.Close);
	}

	// 로스터 "자동 편성": keep the team colours registered now, or any.
	public static void AskLineup(Action refresh)
	{
		// One line more since 2026-11-12 (user 10:4x: 선수보호 cards are fixed, server AutoLineup Locked).
		// 2026-11-18 (A7): the body size and a fitted box as the result window (it was text at 12 in a box grown by a guess).
		Ma9MBookPopup p = Ma9MBookPopup.Create(0f);
		p.Title.text = "자동 편성";
		p.Content.text = "가진 카드로 볼 상한 안에서 가장 강한 1군을 계산합니다.\n팀컬러 유지: 지금 작동하는 팀컬러가 그대로 남는 편성만\n자유롭게: 팀컬러가 바뀌어도 가장 강한 편성\n선수보호를 건 1군 선수는 그 자리 그대로 둡니다.\n(적용 전에 바뀌는 선수를 보여 드립니다)";
		p.FitContent();
		p.AddButton("팀컬러 유지", -135f, p.BottomRow, 0.85f, delegate
		{
			p.Close();
			Request("autolineup:keep", refresh);
		});
		p.AddButton("자유롭게", 0f, p.BottomRow, 0.85f, delegate
		{
			p.Close();
			Request("autolineup:free", refresh);
		});
		p.AddButton("닫기", 135f, p.BottomRow, 0.85f, p.Close);
	}

	// A copy of a screen button with our label and click (the original's messages removed).
	public static GameObject CloneButton(GameObject source, string text, Vector3 localPosition, Action click)
	{
		if (source == null)
		{
			return null;
		}
		GameObject go = UnityEngine.Object.Instantiate(source, source.transform.parent);
		UIPanel stray = go.GetComponent<UIPanel>();
		if (stray != null)
		{
			UnityEngine.Object.DestroyImmediate(stray);
		}
		foreach (UIButtonMessage message in go.GetComponents<UIButtonMessage>())
		{
			UnityEngine.Object.DestroyImmediate(message);
		}
		go.name = "Ma9MButton_" + text;
		go.transform.localPosition = localPosition;
		foreach (UILabel label in go.GetComponentsInChildren<UILabel>(true))
		{
			UILocalize localize = label.GetComponent<UILocalize>();
			if (localize != null)
			{
				UnityEngine.Object.DestroyImmediate(localize);
			}
			label.text = text;
		}
		go.SetActive(true);
		UIImageButton button = go.GetComponent<UIImageButton>();
		if (button != null)
		{
			Ma9MClientUtil.ButtonSetActive(button, true);
		}
		UIEventListener.Get(go).onClick = delegate
		{
			click();
		};
		return go;
	}
}
