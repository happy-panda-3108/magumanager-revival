using System;
using Ma9MPackets;
using UnityEngine;

// Ma9M port (user 2026-11-11 18:4x via the engine session, 10번 "볼 상한 안에서 가장 강한 라인업을 짜는 기능 … 일단 진행해봅시다",
// 11번 "찍었을 때 가장 승률이 높게 나오는 쪽으로 자동으로 찍어주는 기능"; 21:2x "특성을 가장 승률 높은 조합으로 자동 배분해주는 기능이
// 생길 때 같이 반영합시다"): 특성관리 "추천 배분" and 로스터 "자동 편성". The app asks with REQ_SCENE_TUTORIAL "ma9m:…"; the
// server (AdviceHandlers) works it out and answers with POPUP type 10 [title, text, ok label], shown here with 적용 / 취소
// (POPUP_ACK 10 / 11; types 1 and 2 send 0 / 1), then type 11 [title, text] whose OK refreshes the screen (Refresh, set by
// the screen that asked). A plain message (type 1) comes when there is nothing to advise.
public static class Ma9MAdvice
{
	public const byte AskType = 10;

	public const byte DoneType = 11;

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
		spacket.scene_name.str = "ma9m:" + what;
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
		if (s_waiting)
		{
			s_waiting = false;
			SceneChanger.HideLoadingTransparent();
		}
		string[] m = new string[3];
		for (int i = 0; i < m.Length; i++)
		{
			m[i] = (packet.Messages.array != null && i < packet.Messages.array.Length && packet.Messages.array[i] != null) ? packet.Messages.array[i].str : null;
		}
		if (packet.Type.n == AskType)
		{
			Ask(m[0] ?? string.Empty, m[1] ?? string.Empty, m[2] ?? "적용");
			return true;
		}
		if (packet.Type.n == DoneType)
		{
			PopupManager.ViewPopup1(m[0] ?? string.Empty, m[1] ?? string.Empty, Localization.instance.Get("OK"), delegate
			{
				if (Refresh != null)
				{
					Refresh();
				}
			});
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

	// The common popup grown for the lines (the message does not wrap; four lines fit the box as it is).
	private static void Ask(string title, string text, string ok)
	{
		int lines = text.Split('\n').Length;
		Ma9MBookPopup p = Ma9MBookPopup.Create(Mathf.Max(0, lines - 4) * 17f);
		p.Title.text = title;
		p.Content.text = text;
		p.Content.transform.localScale = new Vector3(13f, 13f, 1f);
		bool answered = false;
		p.OnClose = delegate
		{
			if (!answered)
			{
				Ack(AckCancel);
			}
		};
		p.AddButton(ok, -80f, p.BottomRow, 1f, delegate
		{
			answered = true;
			p.Close();
			Ack(AckApply);
		});
		p.AddButton(Localization.instance.Get("Cancel"), 80f, p.BottomRow, 1f, p.Close);
	}

	// 로스터 "자동 편성": keep the team colours registered now, or any.
	public static void AskLineup(Action refresh)
	{
		Ma9MBookPopup p = Ma9MBookPopup.Create(0f);
		p.Title.text = "자동 편성";
		p.Content.text = "가진 카드로 볼 상한 안에서 가장 강한 1군을 계산합니다.\n팀컬러 유지: 지금 작동하는 팀컬러가 그대로 남는 편성만\n자유롭게: 팀컬러가 바뀌어도 가장 강한 편성\n(적용 전에 바뀌는 선수를 보여 드립니다)";
		p.Content.transform.localScale = new Vector3(12f, 12f, 1f);
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
