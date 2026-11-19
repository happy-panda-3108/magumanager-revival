using System;
using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary.PacketType;
using UnityEngine;

// 로비 소식 띠 (systems session, 2026-11-17; user 09:3x: "로비 소식에 띠를 추가해서 약간 기존 게임들 공지사항 띠처럼 나와서 오른쪽에서
// 왼쪽으로 글자가 흘러가며 볼 수 있듯이 약간 그런 느낌의 소식 띠도 괜찮은 것 같음"). The server (NewsFeed) sends the latest league news
// at each lobby visit as POPUP type 32 [line, line, …] to an app with %ma9m-news1 (ClientBuildSetting). A thin dark band right under the
// lobby's top bar: a gold "소식" tag on the left, the lines one after another flowing right to left in a clipped panel, again from the
// first when all have passed. A tap opens them all in a window.
public class Ma9MNewsTicker : MonoBehaviour
{
	public const byte Type = 32;

	private static readonly List<string> s_lines = new List<string>();

	private static Ma9MNewsTicker s_live;

	// The design width and the lobby's top bar (Common_TopMenu, 55 of the 480 design units), in the root's units. The band runs from
	// 57 to 81 (the lobby's NEXT VS box starts at about 82).
	private const float DesignW = 800f, TopBarH = 57f;

	private const float BandH = 24f, Speed = 60f, Gap = 90f, TextSize = 15f;

	// A label made here is drawn about half its size above its pivot (the borrowed dynamic font's vertical offset under Unity 6; the
	// engine session measured 0.49 × size on the user's phone, 2000 × 923, and on the PC). 1.0.55 (user 11-17 11:5x: "띠랑 글자 위치가
	// 조금 안 맞는 것 같네, 글자가 반쯤 잘려서 보이잖아 이거 수정해야함"): the top half of "소식" went under the top bar and the flowing
	// text was cut by the clip. So the labels sit this much lower than the band's middle.
	private const float TextDrop = 0.49f;

	private float TextY => m_y - TextDrop * TextSize;

	// NetworkConnector.ProcessPKS_SC_POPUP.
	public static bool Receive(Ma9MPacket.PKS_SC_POPUP packet)
	{
		if (packet.Type.n != Type)
		{
			return false;
		}
		s_lines.Clear();
		if (packet.Messages.array != null)
		{
			foreach (PLongString s in packet.Messages.array)
			{
				if (s != null && !string.IsNullOrEmpty(s.str))
				{
					s_lines.Add(s.str);
				}
			}
		}
		try
		{
			Show();
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MNewsTicker] " + e);
		}
		return true;
	}

	// On the lobby (MainLobby_MainMenu up): the band, made once per lobby, its lines replaced.
	private static void Show()
	{
		if (s_lines.Count == 0)
		{
			return;
		}
		MainLobby_MainMenu lobby = UnityEngine.Object.FindObjectOfType<MainLobby_MainMenu>();
		if (lobby == null)
		{
			return;
		}
		if (s_live != null)
		{
			s_live.Restart();
			return;
		}
		UIRoot root = NGUITools.FindInParents<UIRoot>(lobby.gameObject);
		if (root == null)
		{
			return;
		}
		UIFont font = null;
		foreach (UILabel label in root.GetComponentsInChildren<UILabel>(true))
		{
			if (label.font != null)
			{
				font = label.font;
				break;
			}
		}
		if (font == null)
		{
			return;
		}
		GameObject go = NGUITools.AddChild(root.gameObject);
		go.name = "Ma9MNewsTicker";
		go.transform.localPosition = new Vector3(0f, 0f, -2f);
		go.AddComponent<UIPanel>();
		s_live = go.AddComponent<Ma9MNewsTicker>();
		s_live.m_font = font;
		s_live.Lay();
	}

	private UIFont m_font;
	private UIPanel m_clip;
	private UILabel m_text;
	private float m_left, m_right, m_y;
	private int m_next;
	private float m_x;
	private float m_width;

	private void Lay()
	{
		// The app's UI is laid out on an 800 × 480 design (UIRoot.Start: 480 tall on screens wider than 5:3, the extra width black at
		// the sides; 800 wide on narrower ones, taller), in the root's units around its centre. The band: 800 wide, right under the
		// lobby's top bar (55 of 480). tester_1117b (11-17): taking the screen from the camera fell back to a 960 × 540 guess, so the band
		// sat over the top bar and across the black sides.
		UIRoot root = NGUITools.FindInParents<UIRoot>(base.gameObject);
		float screenH = (root != null) ? root.activeHeight : 480f;
		float aspect = Screen.width / (float)Mathf.Max(1, Screen.height);
		float w = Mathf.Min(DesignW, screenH * aspect);
		Vector2 min = new Vector2(-w * 0.5f, -screenH * 0.5f), max = new Vector2(w * 0.5f, screenH * 0.5f);
		m_y = max.y - TopBarH - BandH * 0.5f;
		float cx = 0f;
		// The band and a thin gold line under it.
		UITexture band = Box(base.gameObject, new Vector3(cx, m_y, 0f), w, BandH, new Color(0f, 0f, 0f, 0.55f));
		band.gameObject.AddComponent<BoxCollider>().size = new Vector3(1f, 1f, 0f);
		UIEventListener.Get(band.gameObject).onClick = delegate
		{
			ShowAll();
		};
		Box(base.gameObject, new Vector3(cx, m_y - BandH * 0.5f, -0.5f), w, 1.5f, new Color(1f, 0.84f, 0.38f, 0.6f));
		// The tag.
		float tagW = 58f;
		Box(base.gameObject, new Vector3(min.x + tagW * 0.5f, m_y, -1f), tagW, BandH, new Color(0.85f, 0.66f, 0.2f, 1f));
		Text(base.gameObject, "소식", new Vector3(min.x + tagW * 0.5f, TextY, -2f), TextSize, new Color(0.1f, 0.1f, 0.14f), UIWidget.Pivot.Center);
		// The flowing text in a clipped panel from the tag to the right edge.
		m_left = min.x + tagW + 6f;
		m_right = max.x;
		GameObject clip = NGUITools.AddChild(base.gameObject);
		clip.name = "Clip";
		clip.transform.localPosition = new Vector3(0f, 0f, -1f);
		m_clip = clip.AddComponent<UIPanel>();
		m_clip.clipping = UIDrawCall.Clipping.HardClip;
		m_clip.clipRange = new Vector4((m_left + m_right) * 0.5f, m_y, m_right - m_left, BandH);
		Ma9MTextCheck.ClipOk.Add(m_clip);  // the line flows through this window on purpose
		m_text = Text(clip, string.Empty, new Vector3(m_right, TextY, 0f), TextSize, Color.white, UIWidget.Pivot.Left);
		Restart();
	}

	// Ma9M (11-17 18:1x, user: "로비의 소식 공지사항 띠가 매번 로비 올때마다 초기화되는 것 같음, 인내심이 부족한 사람은 뒷 내용을 띠로 확인하지
	// 못할 것임"): the band is made again with each lobby, and it began at the first line every time. Where it was (the line on show
	// and how far it had gone) is kept across lobbies, and the band goes on from there; when new news moved that line, it is found
	// again by its text.
	private static string s_atText;

	private static float s_atOffset;

	private void Restart()
	{
		int at = (s_atText != null) ? s_lines.IndexOf(s_atText) : -1;
		if (at < 0)
		{
			m_next = 0;
			Next();
			return;
		}
		m_next = at;
		Next();
		m_x = m_right + 8f + Mathf.Min(0f, s_atOffset);
		// A line already past the left edge: the next one.
		if (m_x + m_width + Gap < m_left)
		{
			Next();
		}
		m_text.transform.localPosition = new Vector3(m_x, TextY, 0f);
	}

	private void Next()
	{
		if (s_lines.Count == 0 || m_text == null)
		{
			return;
		}
		if (m_next >= s_lines.Count)
		{
			m_next = 0;
		}
		m_text.text = s_lines[m_next++];
		m_text.MarkAsChanged();
		m_width = m_text.relativeSize.x * m_text.transform.localScale.x;
		m_x = m_right + 8f;
		m_text.transform.localPosition = new Vector3(m_x, TextY, 0f);
	}

	// 11-17 (tester_1117l): the band is drawn over the popups' dim, and a grown popup (Ma9MTextBox, up to 350 tall) reaches under
	// it, so it steps aside while a message window is open (checked every 0.3 s).
	private float m_coverCheck;

	private bool m_hidden;

	private static bool MessageOpen()
	{
		return UnityEngine.Object.FindFirstObjectByType<Popup1>() != null || UnityEngine.Object.FindFirstObjectByType<Popup2>() != null
			|| UnityEngine.Object.FindFirstObjectByType<Popup3>() != null || UnityEngine.Object.FindFirstObjectByType<Popup4>() != null
			|| UnityEngine.Object.FindFirstObjectByType<Ma9MBookPopup>() != null;
	}

	private void Update()
	{
		m_coverCheck -= Time.unscaledDeltaTime;
		if (m_coverCheck <= 0f)
		{
			m_coverCheck = 0.3f;
			bool hide = MessageOpen();
			if (hide != m_hidden)
			{
				m_hidden = hide;
				foreach (UIPanel p in GetComponentsInChildren<UIPanel>(true))
				{
					p.alpha = hide ? 0f : 1f;
				}
			}
		}
		if (m_text == null || s_lines.Count == 0)
		{
			return;
		}
		m_x -= Speed * Time.deltaTime;
		m_text.transform.localPosition = new Vector3(m_x, TextY, 0f);
		// The next line once this one has gone past the left edge (a gap after it).
		if (m_x + m_width + Gap < m_left)
		{
			Next();
		}
		s_atText = m_text.text;
		s_atOffset = m_x - (m_right + 8f);
	}

	// All of them in a window ([공지] first, then the newest news). 11-17 18:1x (user: "공지사항을 눌렀을 때 글이 글상자를 삐져 나가면, 글씨
	// 크기를 조절하기 보다는 아래로 내리는 스크롤 같은 기능이 있으면 좋겠음"): every line (it showed the first 8 at 12 units), at the body
	// size, wrapped at the box, each item after a "·" (a wrapped item's next line has none); the window grows and then scrolls
	// (Ma9MBookPopup.FitContent).
	public static void ShowAll()
	{
		if (s_lines.Count == 0)
		{
			return;
		}
		Ma9MBookPopup p = Ma9MBookPopup.Create(0f);
		// 2026-11-19 (user 21:3x, 11-18 question 5 — AI tester 두산 53: only the notices and FA news, not the league's): "알아서 선정".
		p.Title.text = "소식";
		p.Content.text = "· " + string.Join("\n· ", s_lines.ToArray());
		p.FitContent(true);
		p.AddButton(Localization.instance.Get("OK"), 0f, p.BottomRow, 1f, p.Close);
	}

	private void OnDestroy()
	{
		if (s_live == this)
		{
			s_live = null;
		}
		if (m_clip != null)
		{
			Ma9MTextCheck.ClipOk.Remove(m_clip);
		}
	}

	private UITexture Box(GameObject parent, Vector3 pos, float width, float height, Color colour)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Box";
		UITexture t = go.AddComponent<UITexture>();
		t.shader = Shader.Find("Unlit/Transparent Colored");
		t.mainTexture = Texture2D.whiteTexture;
		t.color = colour;
		go.transform.localPosition = pos;
		go.transform.localScale = new Vector3(width, height, 1f);
		return t;
	}

	private UILabel Text(GameObject parent, string text, Vector3 pos, float size, Color colour, UIWidget.Pivot pivot)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Text";
		UILabel label = go.AddComponent<UILabel>();
		label.font = m_font;
		label.supportEncoding = false;
		label.color = colour;
		label.maxLineCount = 1;
		label.lineWidth = 0;
		label.text = text ?? string.Empty;
		go.transform.localScale = new Vector3(size, size, 1f);
		label.pivot = pivot;
		go.transform.localPosition = pos;
		label.MarkAsChanged();
		return label;
	}
}
