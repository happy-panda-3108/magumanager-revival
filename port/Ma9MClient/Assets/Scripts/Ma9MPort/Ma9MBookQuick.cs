using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

// Ma9M port (user 2026-11-11 21:3x via the engine session: "트레이닝북 조합 이거 좀 더 간편한 방법은 없을까? 빠른선택 - 확인 - 확인
// 이걸 거의 150번 정도 눌러야 4레벨 트레이닝북이 다 완성되고, 이 마저도 교환을 해야하는데 빠른 조합 이런 기능이 생겨서 한 방에 싹 다
// 정리해주면 좋겠음"; 21:4x picks "단계를 골라서" and "목표 권수대로 한 번에"):
// - 빠른조합 (트레이닝북 조합 screen): a popup with the cascade for each stop (II / III / IV까지) and one button per stop; the
//   server combines every grade below the stop as far as the books go (server TrainingHandlers.TryQuickMix).
// - 목표교환 (교환소 screen, and from the 빠른조합 result): the wanted count of each stat's III or IV book, kept per team on this
//   device; the server swaps the books above a target into the stats below theirs (TrainingHandlers.TryTargetExchange).
// Both go as PROC_TCARD_MIX with a tag first; the screen then reloads the list and shows the difference.
public static class Ma9MBookQuick
{
	public const int QuickMixTag = -1;

	public const int TargetTag = -2;

	public const int Stats = 12;

	public static readonly string[] StatNames = new string[13] { null, "컨택", "파워", "스피드", "선구", "수비", "H정신", "체력", "구속", "컨트롤", "테크닉", "구위", "P정신" };

	private static readonly Color Dim = new Color(0.55f, 0.55f, 0.55f);

	private static readonly Color Picked = new Color(1f, 0.82f, 0.29f);

	public static int Book(int stat, int grade)
	{
		return 31000 + stat * 10 + grade;
	}

	private static int StatOf(int item)
	{
		return (item - 31000) / 10;
	}

	private static int GradeOf(int item)
	{
		return (item - 31000) % 10;
	}

	private static string G(int grade)
	{
		return CombineTraining_DataCenter.Ma9MGradeName(grade);
	}

	// IV books exist when the item table has them (코칭스태프 on).
	public static int MaxGrade
	{
		get
		{
			return (CombineTraining_DataCenter.Ma9MBookOf(1, 4) != 0) ? 4 : 3;
		}
	}

	// Every book the screen holds, item → amount (call after the row's books were put back).
	public static Dictionary<int, int> Snapshot()
	{
		Dictionary<int, int> books = new Dictionary<int, int>();
		foreach (TrainingCardInfo info in CombineTraining_DataCenter.GetTrainingInfos().Values)
		{
			if (info.m_Amount > 0)
			{
				books[info.m_CardIndex] = info.m_Amount;
			}
		}
		return books;
	}

	private static int Amount(Dictionary<int, int> books, int item)
	{
		int n;
		return books.TryGetValue(item, out n) ? n : 0;
	}

	private static int GradeTotal(Dictionary<int, int> books, int grade)
	{
		int total = 0;
		for (int s = 1; s <= Stats; s++)
		{
			total += Amount(books, Book(s, grade));
		}
		return total;
	}

	// The targets of one grade, stats 1-12 (index 0 unused).
	public static int[] Targets(int grade)
	{
		int[] targets = new int[Stats + 1];
		string text = null;
		try
		{
			text = PlayerPrefs.GetString(Ma9MSortMemory.PrefsKey("BookTargets" + grade), string.Empty);
		}
		catch (Exception)
		{
		}
		string[] parts = (text ?? string.Empty).Split(',');
		for (int i = 0; i < Stats && i < parts.Length; i++)
		{
			int n;
			if (int.TryParse(parts[i], out n))
			{
				targets[i + 1] = Mathf.Clamp(n, 0, 999);
			}
		}
		return targets;
	}

	public static void SaveTargets(int grade, int[] targets)
	{
		string[] parts = new string[Stats];
		for (int i = 0; i < Stats; i++)
		{
			parts[i] = targets[i + 1].ToString();
		}
		try
		{
			PlayerPrefs.SetString(Ma9MSortMemory.PrefsKey("BookTargets" + grade), string.Join(",", parts));
			PlayerPrefs.Save();
		}
		catch (Exception)
		{
		}
	}

	// ---- 빠른조합 ----

	public static void ShowMix(CombineTraining_Main main)
	{
		Dictionary<int, int> books = Snapshot();
		int maxGrade = MaxGrade;
		int topMix = CombineTraining_DataCenter.Ma9MTopMix;
		// used[g]: books of grade g combined; made[g]: books of grade g made, going all the way up (each stage only depends
		// on the ones below it, so the numbers of a lower stop are the same).
		int[] used = new int[6];
		int[] made = new int[6];
		for (int g = 1; g < maxGrade; g++)
		{
			int need = (g == CombineTraining_DataCenter.Ma9MTopGrade) ? topMix : CombineTraining_DataCenter.Ma9MMixCount;
			int total = GradeTotal(books, g) + made[g];
			int count = (need > 0) ? (total / need) : 0;
			used[g] = count * need;
			made[g + 1] = count;
		}
		List<string> lines = new List<string>();
		StringBuilder have = new StringBuilder("가진 책: ");
		for (int g = 1; g <= maxGrade; g++)
		{
			have.Append((g > 1) ? " · " : string.Empty).Append(G(g)).Append(' ').Append(GradeTotal(books, g));
		}
		lines.Add(have.ToString());
		bool[] can = new bool[6];
		bool any = false;
		for (int stop = 2; stop <= maxGrade; stop++)
		{
			int g = stop - 1;
			any |= made[stop] > 0;
			can[stop] = any && !(stop == 4 && topMix == 0);
			string head = G(stop) + "까지: " + ((stop > 2) ? "이어서 " : string.Empty);
			if (stop == 4 && topMix == 0)
			{
				lines.Add(head + "트레이닝코치를 등록해야 합니다");
			}
			else if (made[stop] == 0)
			{
				lines.Add(head + G(g) + " 책이 모자람 (" + CombineTraining_DataCenter.Ma9MNeed(g) + "권부터)");
			}
			else
			{
				lines.Add(head + G(g) + " " + used[g] + "권 → " + G(stop) + " " + made[stop] + "권" + ((stop == 4) ? (" (III " + topMix + "권당 1권)") : string.Empty));
			}
		}
		lines.Add("능력치는 무작위 · 남는 책은 그대로");
		// 2026-11-12 (AI tester LG 66: "IV권의 효과(+2~+5)가 미리 나오지 않는다"): each grade's result on one training square.
		lines.Add("한 칸 효과: I -1~+2 · II 0~+3 · III +1~+4" + ((maxGrade >= 4) ? " · IV +2~+5" : string.Empty));
		Ma9MBookPopup p = Ma9MBookPopup.Create(63f);
		p.Title.text = "빠른 조합";
		p.Content.text = string.Join("\n", lines.ToArray());
		p.Content.transform.localScale = new Vector3(13f, 13f, 1f);
		float y = p.BottomRow;
		float[] xs = new float[4] { -150f, -50f, 50f, 150f };
		for (int stop = 2; stop <= 4; stop++)
		{
			int s = stop;
			bool on = s <= maxGrade && can[s];
			UILabel label = p.AddButton(G(s) + "까지", xs[s - 2], y, 0.78f, delegate
			{
				if (!(s <= maxGrade && can[s]))
				{
					PopupManager.ViewPopup1(Localization.instance.Get("Message"), (s == 4 && topMix == 0) ? "IV 등급은 코칭스태프에 트레이닝코치를\n등록해야 만들 수 있습니다." : "조합할 책이 모자랍니다.\n(같은 등급 4권부터, III은 트레이닝코치 권수)", Localization.instance.Get("OK"), null);
					return;
				}
				p.Close();
				main.Ma9MSendQuick(new int[2] { QuickMixTag, s }, true, s);
			});
			label.color = on ? label.color : Dim;
		}
		p.AddButton("닫기", xs[3], y, 0.78f, p.Close);
	}

	// ---- 결과 ----

	public static void ShowResult(CombineTraining_Main main, bool mix, int grade, Dictionary<int, int> before, Dictionary<int, int> after)
	{
		if (mix)
		{
			StringBuilder line = new StringBuilder("가진 책: ");
			for (int g = 1; g <= MaxGrade; g++)
			{
				line.Append((g > 1) ? " · " : string.Empty).Append(G(g)).Append(' ').Append(GradeTotal(before, g)).Append("→").Append(GradeTotal(after, g));
			}
			List<string> news = new List<string>();
			int made = 0;
			for (int s = 1; s <= Stats; s++)
			{
				int d = Amount(after, Book(s, grade)) - Amount(before, Book(s, grade));
				if (d > 0)
				{
					news.Add(StatNames[s] + " " + d);
					made += d;
				}
			}
			string text = line + "\n새 " + G(grade) + " " + made + "권" + ((news.Count > 0) ? (":\n" + Rows(news, 6)) : string.Empty);
			if (grade >= CombineTraining_DataCenter.Ma9MTopGrade)
			{
				PopupManager.ViewPopup2("빠른 조합 결과", text, "목표교환", Localization.instance.Get("OK"), delegate
				{
					ShowTargets(main, grade);
				}, null);
			}
			else
			{
				PopupManager.ViewPopup1("빠른 조합 결과", text, Localization.instance.Get("OK"), null);
			}
			return;
		}
		int[] targets = Targets(grade);
		List<string> changes = new List<string>();
		List<string> short_ = new List<string>();
		int moved = 0;
		for (int s = 1; s <= Stats; s++)
		{
			int a = Amount(before, Book(s, grade));
			int b = Amount(after, Book(s, grade));
			if (a != b)
			{
				changes.Add(StatNames[s] + " " + a + "→" + b);
			}
			moved += Mathf.Max(0, b - a);
			if (targets[s] > b)
			{
				short_.Add(StatNames[s] + " " + (targets[s] - b));
			}
		}
		string result = G(grade) + " 책 " + moved + "권을 받았습니다. (" + (moved * CombineTraining_DataCenter.Ma9MExchangeCost) + "권 사용)\n" + Rows(changes, 4) + ((short_.Count > 0) ? ("\n모자람: " + string.Join(" · ", short_.ToArray())) : string.Empty);
		PopupManager.ViewPopup1("목표 교환 결과", result, Localization.instance.Get("OK"), null);
	}

	// Entries joined with " · ", a new line every `per` (the popup's message does not wrap by itself).
	private static string Rows(List<string> items, int per)
	{
		StringBuilder sb = new StringBuilder();
		for (int i = 0; i < items.Count; i++)
		{
			sb.Append((i == 0) ? string.Empty : ((i % per == 0) ? "\n" : " · ")).Append(items[i]);
		}
		return sb.ToString();
	}

	// ---- 목표교환 ----

	// The grade the editor opens on: IV when the table has IV books and the team has some or can make them, else III.
	public static int DefaultTargetGrade()
	{
		if (MaxGrade < 4)
		{
			return 3;
		}
		return (CombineTraining_DataCenter.Ma9MTopMix > 0 || GradeTotal(Snapshot(), 4) > 0) ? 4 : 3;
	}

	public static void ShowTargets(CombineTraining_Main main, int grade)
	{
		new TargetEditor(main, grade);
	}

	private class TargetEditor
	{
		private const float Extra = 190f;

		private readonly CombineTraining_Main m_main;

		private readonly Ma9MBookPopup m_p;

		private int m_grade;

		private int[] m_targets;

		private int[] m_held = new int[Stats + 1];

		private int m_pick = 1;

		private readonly UILabel[] m_names = new UILabel[Stats + 1];

		private readonly UILabel[] m_counts = new UILabel[Stats + 1];

		private UILabel m_gradeButton;

		private Color m_white = Color.white;

		public TargetEditor(CombineTraining_Main main, int grade)
		{
			m_main = main;
			m_p = Ma9MBookPopup.Create(Extra);
			m_p.OnClose = delegate
			{
				SaveTargets(m_grade, m_targets);
			};
			m_white = m_p.Content.color;
			float top = m_p.TitleY - 30f;
			for (int col = 0; col < 2; col++)
			{
				float x0 = (col == 0) ? (-195f) : 15f;
				UILabel head = m_p.AddLabel(x0, top, UIWidget.Pivot.Left, 12f);
				head.text = (col == 0) ? "타자  (보유 / 목표)" : "투수  (보유 / 목표)";
				head.color = Dim;
				for (int r = 0; r < 6; r++)
				{
					int stat = col * 6 + r + 1;
					float y = top - 24f * (r + 1);
					m_names[stat] = m_p.AddLabel(x0, y, UIWidget.Pivot.Left, 14f);
					m_counts[stat] = m_p.AddLabel(x0 + 172f, y, UIWidget.Pivot.Right, 14f);
					m_p.AddTapZone(x0 + 88f, y, 182f, 23f, delegate
					{
						m_pick = stat;
						Refresh();
					});
				}
			}
			m_p.Content.transform.localPosition = new Vector3(0f, top - 24f * 7f - 18f, m_p.Content.transform.localPosition.z);
			m_p.Content.transform.localScale = new Vector3(12f, 12f, 1f);
			float row1 = m_p.BottomRow + 40f;
			m_p.AddButton("-1", -135f, row1, 0.85f, null, delegate(bool down)
			{
				m_p.Hold(delegate
				{
					Step(-1);
				}, down);
			});
			m_p.AddButton("+1", 0f, row1, 0.85f, null, delegate(bool down)
			{
				m_p.Hold(delegate
				{
					Step(1);
				}, down);
			});
			// "이 줄 0" (2026-11-12, AI tester LG: "0으로" read as all the targets; it clears the picked row only).
			m_p.AddButton("이 줄 0", 135f, row1, 0.85f, delegate
			{
				m_targets[m_pick] = 0;
				Refresh();
			});
			m_gradeButton = m_p.AddButton(string.Empty, -135f, m_p.BottomRow, 0.85f, delegate
			{
				if (MaxGrade < 4)
				{
					return;
				}
				SaveTargets(m_grade, m_targets);
				Load((m_grade == 4) ? 3 : 4);
			});
			m_p.AddButton("닫기", 0f, m_p.BottomRow, 0.85f, m_p.Close);
			m_p.AddButton("교환하기", 135f, m_p.BottomRow, 0.85f, Exchange);
			Load(grade);
		}

		private void Load(int grade)
		{
			m_grade = grade;
			m_targets = Targets(grade);
			Dictionary<int, int> books = Snapshot();
			for (int s = 1; s <= Stats; s++)
			{
				m_held[s] = Amount(books, Book(s, grade));
			}
			m_pick = 1;
			for (int s = Stats; s >= 1; s--)
			{
				if (m_targets[s] > 0)
				{
					m_pick = s;
				}
			}
			m_p.Title.text = "목표 교환 - " + G(grade) + " 트레이닝북";
			m_gradeButton.text = (MaxGrade < 4) ? "III만" : ((grade == 4) ? "III 목표" : "IV 목표");
			m_gradeButton.color = (MaxGrade < 4) ? Dim : m_white;
			Refresh();
		}

		private void Step(int delta)
		{
			m_targets[m_pick] = Mathf.Clamp(m_targets[m_pick] + delta, 0, 999);
			Refresh();
		}

		// The server's rule (TryTargetExchange): books above a stat's target go to the stats below theirs, two for one since
		// 2026-11-12 (CombineTraining_DataCenter.Ma9MExchangeCost). moves = books received.
		private void Plan(out int moves, out int lacking, out int extra)
		{
			int surplus = 0;
			int deficit = 0;
			for (int s = 1; s <= Stats; s++)
			{
				surplus += Mathf.Max(0, m_held[s] - m_targets[s]);
				deficit += Mathf.Max(0, m_targets[s] - m_held[s]);
			}
			int cost = CombineTraining_DataCenter.Ma9MExchangeCost;
			moves = Mathf.Min(surplus / cost, deficit);
			lacking = deficit - moves;
			extra = surplus - moves * cost;
		}

		private void Refresh()
		{
			for (int s = 1; s <= Stats; s++)
			{
				bool picked = s == m_pick;
				m_names[s].text = (picked ? "> " : string.Empty) + StatNames[s];
				m_counts[s].text = m_held[s] + " / " + m_targets[s];
				Color c = picked ? Picked : ((m_targets[s] > 0 || m_held[s] > 0) ? m_white : Dim);
				m_names[s].color = c;
				m_counts[s].color = c;
			}
			int moves;
			int lacking;
			int extra;
			Plan(out moves, out lacking, out extra);
			string first = (moves > 0) ? ("교환하면 책 " + (moves * CombineTraining_DataCenter.Ma9MExchangeCost) + "권 → " + moves + "권") : "바꿀 책이 없습니다 (2권 → 1권)";
			first += ((lacking > 0) ? (" · 모자람 " + lacking) : string.Empty) + ((extra > 0) ? (" · 그대로 " + extra) : string.Empty);
			m_p.Content.text = first + "\n줄을 눌러 고르고 -1 / +1 (꾹 누르면 빠르게)";
		}

		private void Exchange()
		{
			int moves;
			int lacking;
			int extra;
			Plan(out moves, out lacking, out extra);
			bool none = true;
			for (int s = 1; s <= Stats; s++)
			{
				none &= m_targets[s] == 0;
			}
			if (none || moves == 0)
			{
				PopupManager.ViewPopup1(Localization.instance.Get("Message"), none ? "능력치별 목표 권수를 먼저 정해 주세요." : ((lacking == 0) ? (G(m_grade) + " 책이 이미 목표 권수만큼 있습니다.") : ((extra > 0) ? ("목표보다 많은 " + G(m_grade) + " 책이 " + extra + "권뿐입니다.\n(2권을 내야 1권을 받습니다)") : ("목표보다 많은 " + G(m_grade) + " 책이 없어\n바꿀 책이 없습니다."))), Localization.instance.Get("OK"), null);
				return;
			}
			int[] entries = new int[2 + Stats];
			entries[0] = TargetTag;
			entries[1] = m_grade;
			for (int s = 1; s <= Stats; s++)
			{
				entries[1 + s] = m_targets[s];
			}
			int grade = m_grade;
			m_p.Close();
			m_main.Ma9MSendQuick(entries, false, grade);
		}
	}
}

// Panel_Popup2 grown by `extra` with buttons of our own (clones of its 확인, any number and rows), extra labels (clones of its
// message) and tap zones. Its two buttons stay hidden as the clone source. The back button closes it.
public class Ma9MBookPopup : MonoBehaviour
{
	public UILabel Title;

	public UILabel Content;

	public Action OnClose;

	public float BottomRow;

	public float TitleY;

	private string m_panelName;

	private bool m_closed;

	private GameObject m_template;

	private Action m_holdStep;

	private float m_holdNext;

	public static Ma9MBookPopup Create(float extra)
	{
		string name = "Ma9MBook_" + Guid.NewGuid();
		UIPanel panel = PanelManager.CreatePanel(PopupManager.m_instance.pfPopup2, name, true);
		Popup2 popup = panel.GetComponent<Popup2>();
		Ma9MBookPopup p = panel.gameObject.AddComponent<Ma9MBookPopup>();
		p.m_panelName = name;
		p.Title = popup.titleLabel;
		p.Content = popup.contentLabel;
		Transform back = panel.transform.Find("PopupBack");
		if (back != null)
		{
			back.localScale = new Vector3(back.localScale.x, back.localScale.y + extra, back.localScale.z);
		}
		Vector3 t = p.Title.transform.localPosition;
		p.TitleY = t.y + extra / 2f;
		p.Title.transform.localPosition = new Vector3(t.x, p.TitleY, t.z);
		GameObject left = popup.confirmLabel.transform.parent.gameObject;
		GameObject right = popup.cancelLabel.transform.parent.gameObject;
		p.BottomRow = left.transform.localPosition.y - extra / 2f;
		p.m_template = left;
		left.SetActive(false);
		right.SetActive(false);
		AndroidManager.Instance.PushEscapeAction(p.Close);
		return p;
	}

	// click: a tap; press: down / up (for holding). Returns the button's label.
	public UILabel AddButton(string text, float x, float y, float scale, Action click, Action<bool> press = null)
	{
		// Cloned under the popup at once (Ma9MRecipeBrowser: a copy made at the scene root got a UIPanel of its own).
		GameObject go = UnityEngine.Object.Instantiate(m_template, m_template.transform.parent);
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
		go.transform.localPosition = new Vector3(x, y, m_template.transform.localPosition.z);
		go.transform.localScale = m_template.transform.localScale * scale;
		UILabel label = go.GetComponentInChildren<UILabel>(true);
		if (label != null)
		{
			UILocalize localize = label.GetComponent<UILocalize>();
			if (localize != null)
			{
				UnityEngine.Object.DestroyImmediate(localize);
			}
			label.text = text;
		}
		go.SetActive(true);
		UIEventListener listener = UIEventListener.Get(go);
		if (click != null)
		{
			listener.onClick = delegate
			{
				if (!m_closed)
				{
					click();
				}
			};
		}
		if (press != null)
		{
			listener.onPress = delegate(GameObject g, bool down)
			{
				if (!m_closed || !down)
				{
					press(down);
				}
			};
		}
		return label;
	}

	public UILabel AddLabel(float x, float y, UIWidget.Pivot pivot, float size)
	{
		GameObject go = UnityEngine.Object.Instantiate(Content.gameObject, Content.transform.parent);
		UIPanel stray = go.GetComponent<UIPanel>();
		if (stray != null)
		{
			UnityEngine.Object.DestroyImmediate(stray);
		}
		go.name = "Ma9MLabel";
		UILabel label = go.GetComponent<UILabel>();
		label.text = string.Empty;
		label.pivot = pivot;
		go.transform.localScale = new Vector3(size, size, 1f);
		go.transform.localPosition = new Vector3(x, y, Content.transform.localPosition.z);
		return label;
	}

	// An invisible tap area in front of the popup (NGUI takes the nearest collider).
	public void AddTapZone(float cx, float cy, float w, float h, Action tap)
	{
		GameObject go = new GameObject("Ma9MTap");
		go.layer = Content.gameObject.layer;
		go.transform.parent = base.transform;
		go.transform.localRotation = Quaternion.identity;
		go.transform.localScale = Vector3.one;
		go.transform.localPosition = new Vector3(cx, cy, -2f);
		BoxCollider box = go.AddComponent<BoxCollider>();
		box.size = new Vector3(w, h, 1f);
		UIEventListener.Get(go).onClick = delegate
		{
			if (!m_closed)
			{
				tap();
			}
		};
	}

	// A press runs the step once, then again and again while held (after 0.45 s, every 0.07 s).
	public void Hold(Action step, bool down)
	{
		if (!down)
		{
			m_holdStep = null;
			return;
		}
		step();
		m_holdStep = step;
		m_holdNext = Time.realtimeSinceStartup + 0.45f;
	}

	private void Update()
	{
		if (m_holdStep != null && !m_closed && Time.realtimeSinceStartup >= m_holdNext)
		{
			m_holdNext = Time.realtimeSinceStartup + 0.07f;
			m_holdStep();
		}
	}

	// Takes its back-button entry off first (as Popup1-4 do), then the panel.
	public void Close()
	{
		if (m_closed)
		{
			return;
		}
		m_closed = true;
		m_holdStep = null;
		AndroidManager.Instance.PopEscapeAction();
		AndroidManager.Instance.EscapeEnable = true;
		if (OnClose != null)
		{
			OnClose();
		}
		PanelManager.RemovePanel(m_panelName);
	}
}
