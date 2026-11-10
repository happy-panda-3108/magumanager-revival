using System;
using System.Collections.Generic;
using UnityEngine;

// Ma9M port (user, 2026-10-24: "저장된 조합을 사용하는 ui가 좀 구린 것 같음 … 이전, 선택, 다음 이렇게 3개의 버튼 … '닫기'와
// '모두 지우기'가 서로 반대의 위치"): 선수조합's saved recipes in one popup that pages in place. It is Panel_Popup2 with a
// third button and room for five lines (the back taller, the title and the buttons moved with it). Item pages: 이전 ·
// 선택 (고정 해제 on the pinned one) · 다음. After the last one: 모두 지우기 (asks first) · 이전 · 닫기, so 닫기 sits where
// 다음 was and paging on never lands on 모두 지우기. The back button closes it.
public class Ma9MRecipeBrowser : MonoBehaviour
{
	private const int Lines = 5;

	private const float Extra = 54f;

	private const float ButtonX = 135f;

	private static readonly Color Dim = new Color(0.55f, 0.55f, 0.55f);

	private string m_panelName;

	private UILabel m_title;

	private UILabel m_content;

	private readonly UILabel[] m_labels = new UILabel[3];

	private readonly bool[] m_enabled = new bool[3];

	private Color m_labelColor = Color.white;

	private int m_cursor;

	private bool m_closed;

	private Action<Ma9MCombineRecipes.Recipe> m_pick;

	private Action m_unpin;

	private Action m_clear;

	// pick: the recipe chosen (the browser closes first); unpin: the pinned one let go (the browser stays); clear: after
	// 모두 지우기 was confirmed (the browser closes first).
	public static void Show(int cursor, Action<Ma9MCombineRecipes.Recipe> pick, Action unpin, Action clear)
	{
		string name = "Ma9MRecipes_" + Guid.NewGuid();
		UIPanel panel = PanelManager.CreatePanel(PopupManager.m_instance.pfPopup2, name, true);
		Popup2 popup = panel.GetComponent<Popup2>();
		Ma9MRecipeBrowser browser = panel.gameObject.AddComponent<Ma9MRecipeBrowser>();
		browser.m_panelName = name;
		browser.m_pick = pick;
		browser.m_unpin = unpin;
		browser.m_clear = clear;
		browser.m_cursor = cursor;
		browser.Build(popup);
		AndroidManager.Instance.PushEscapeAction(browser.Close);
		browser.Refresh();
	}

	private void Build(Popup2 popup)
	{
		m_title = popup.titleLabel;
		m_content = popup.contentLabel;
		GameObject left = popup.confirmLabel.transform.parent.gameObject;
		GameObject right = popup.cancelLabel.transform.parent.gameObject;
		// Cloned under the popup at once: a copy made at the scene root got a UIPanel of its own on its first alpha read
		// (UIWidget.finalAlpha → CreatePanel), drawn apart from the popup's, so its back fell behind the popup's and only
		// the label (z -1) showed (user, 2026-10-24: "선택 칸이 왜 버튼이 아니고 그냥 글만").
		GameObject middle = UnityEngine.Object.Instantiate(left, left.transform.parent);
		UIPanel stray = middle.GetComponent<UIPanel>();
		if (stray != null)
		{
			UnityEngine.Object.DestroyImmediate(stray);
		}
		middle.name = "Ma9MMiddleButton";
		middle.transform.localScale = left.transform.localScale;
		Rewire(left, "Ma9MOnLeft");
		Rewire(middle, "Ma9MOnMiddle");
		Rewire(right, "Ma9MOnRight");
		m_labels[0] = popup.confirmLabel;
		m_labels[1] = middle.GetComponentInChildren<UILabel>(true);
		m_labels[2] = popup.cancelLabel;
		m_labelColor = popup.confirmLabel.color;
		Transform back = transform.Find("PopupBack");
		if (back != null)
		{
			back.localScale = new Vector3(back.localScale.x, back.localScale.y + Extra, back.localScale.z);
		}
		Vector3 t = m_title.transform.localPosition;
		m_title.transform.localPosition = new Vector3(t.x, t.y + Extra / 2f, t.z);
		float y = left.transform.localPosition.y - Extra / 2f;
		left.transform.localPosition = new Vector3(-ButtonX, y, left.transform.localPosition.z);
		middle.transform.localPosition = new Vector3(0f, y, left.transform.localPosition.z);
		right.transform.localPosition = new Vector3(ButtonX, y, right.transform.localPosition.z);
	}

	private static void Rewire(GameObject button, string function)
	{
		foreach (UIButtonMessage message in button.GetComponents<UIButtonMessage>())
		{
			message.functionName = function;
		}
	}

	private void SetButtons(string a, string b, string c, bool aOn, bool bOn, bool cOn)
	{
		string[] texts = new string[3] { a, b, c };
		bool[] on = new bool[3] { aOn, bOn, cOn };
		for (int i = 0; i < 3; i++)
		{
			m_enabled[i] = on[i];
			if (m_labels[i] != null)
			{
				m_labels[i].text = texts[i];
				m_labels[i].color = on[i] ? m_labelColor : Dim;
			}
		}
	}

	private bool AtEnd(List<Ma9MCombineRecipes.Recipe> saved)
	{
		return m_cursor >= saved.Count;
	}

	private void Refresh()
	{
		List<Ma9MCombineRecipes.Recipe> saved = Ma9MCombineRecipes.Saved();
		if (saved.Count == 0)
		{
			Close();
			return;
		}
		m_cursor = Mathf.Clamp(m_cursor, 0, saved.Count);
		if (AtEnd(saved))
		{
			Ma9MCombineRecipes.Recipe pinned = Ma9MCombineRecipes.Pinned;
			m_title.text = "저장된 조합";
			m_content.text = "목록 끝입니다. 저장된 조합 " + saved.Count + "개\n" + ((pinned != null) ? ("고정: " + pinned.Text) : "고정된 조합 없음");
			SetButtons("모두 지우기", "이전", "닫기", true, true, true);
			return;
		}
		int from = Mathf.Clamp(m_cursor - Lines / 2, 0, Mathf.Max(0, saved.Count - Lines));
		List<string> lines = new List<string>();
		for (int i = from; i < saved.Count && i < from + Lines; i++)
		{
			lines.Add(((i == m_cursor) ? "> " : "   ") + (i + 1) + ". " + saved[i].Text + (Ma9MCombineRecipes.IsPinned(saved[i]) ? " (고정)" : string.Empty));
		}
		m_title.text = "저장된 조합 (" + (m_cursor + 1) + "/" + saved.Count + ")";
		m_content.text = string.Join("\n", lines.ToArray());
		SetButtons("이전", Ma9MCombineRecipes.IsPinned(saved[m_cursor]) ? "고정 해제" : "선택", "다음", m_cursor > 0, true, true);
	}

	private void Ma9MOnLeft()
	{
		if (m_closed || !m_enabled[0])
		{
			return;
		}
		List<Ma9MCombineRecipes.Recipe> saved = Ma9MCombineRecipes.Saved();
		if (AtEnd(saved))
		{
			PopupManager.ViewPopup2("모두 지우기", "저장된 조합 " + saved.Count + "개와 고정을 모두 지울까요?", "지우기", "취소", delegate
			{
				Close();
				if (m_clear != null)
				{
					m_clear();
				}
			}, null);
			return;
		}
		m_cursor--;
		Refresh();
	}

	private void Ma9MOnMiddle()
	{
		if (m_closed || !m_enabled[1])
		{
			return;
		}
		List<Ma9MCombineRecipes.Recipe> saved = Ma9MCombineRecipes.Saved();
		if (AtEnd(saved))
		{
			m_cursor--;
			Refresh();
			return;
		}
		Ma9MCombineRecipes.Recipe recipe = saved[m_cursor];
		if (Ma9MCombineRecipes.IsPinned(recipe))
		{
			if (m_unpin != null)
			{
				m_unpin();
			}
			Refresh();
			return;
		}
		Close();
		if (m_pick != null)
		{
			m_pick(recipe);
		}
	}

	private void Ma9MOnRight()
	{
		if (m_closed || !m_enabled[2])
		{
			return;
		}
		if (AtEnd(Ma9MCombineRecipes.Saved()))
		{
			Close();
			return;
		}
		m_cursor++;
		Refresh();
	}

	// Takes its back-button entry off first (as Popup1-4 do), then the panel.
	private void Close()
	{
		if (m_closed)
		{
			return;
		}
		m_closed = true;
		AndroidManager.Instance.PopEscapeAction();
		AndroidManager.Instance.EscapeEnable = true;
		PanelManager.RemovePanel(m_panelName);
	}
}
