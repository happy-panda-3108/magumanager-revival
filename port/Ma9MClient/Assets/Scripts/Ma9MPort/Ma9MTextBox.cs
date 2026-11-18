using System.Collections.Generic;
using UnityEngine;

// 글상자 (systems session, 2026-11-17; user 18:1x: "공지사항을 눌렀을 때 글이 글상자를 삐져 나가면, 글씨 크기를 조절하기 보다는 아래로 내리는
// 스크롤 같은 기능이 있으면 좋겠음" / "이 글상자의 스크롤 기능은 도움말이든, 공지사항이든, 어떤 것들도 다 해당하는 것임" / "앞으로 모든
// 글상자들에 글씨가 삐져 나가는지 아닌지 체크해서 스크롤을 생성할지, 글씨 양을 줄일지 고민해서 만들도록").
// One way for every box of text: the text keeps the readable body size (BodySize; the app font's own line height is 1.5) and wraps
// at the box's width, never shrinks. A box that can grow (the common popups, FitPopup) grows first, up to MaxPopupHeight; what is
// still taller scrolls inside the box: a clipped panel, a finger drag or the mouse wheel, and a thin bar on the right that shows
// how much there is and where you are. Every box is listed (Live) so Ma9MTextCheck ("textcheck") can tell a box that scrolls on
// purpose from text that runs out of its frame.
public class Ma9MTextBox : MonoBehaviour
{
	public const float BodySize = 14f;

	// Panel_Popup1/Popup2: box 443 × 182, title at y 65, buttons' top at y −44; the message between them, centred at y 10.
	private const float PopupTextWidth = 400f, PopupTextHeight = 84f, PopupHeight = 182f;

	// The tallest a common popup grows: the UI is 480 units tall and the top bar (55, drawn over the popups) stays clear.
	public const float MaxPopupHeight = 350f;

	// A message longer than this many lines reads from the left (a centred page of help is hard to follow).
	private const int LeftAfterLines = 6;

	private const float BarWidth = 3f, BarGap = 6f, ScrollPad = 4f;

	public static readonly List<Ma9MTextBox> Live = new List<Ma9MTextBox>();

	public UILabel Label;

	// The box in the label's parent's units: centre and size.
	public Vector2 Centre;

	public Vector2 Size;

	public float ContentHeight;

	public bool Scrolls;

	private float m_x;

	private float m_top;

	private float m_scroll;

	private float m_max;

	private UITexture m_bar;

	// Readable size, wrapped at `width`, never shrunk. Returns the text's height.
	public static float Prepare(UILabel label, float width, float size = BodySize)
	{
		label.shrinkToFit = false;
		label.maxLineCount = 0;
		label.multiLine = true;
		label.transform.localScale = new Vector3(size, size, 1f);
		label.lineWidth = Mathf.Max(1, Mathf.RoundToInt(width));
		return HeightOf(label);
	}

	public static float HeightOf(UILabel label)
	{
		label.MarkAsChanged();
		return label.relativeSize.y * label.transform.localScale.y;
	}

	// The label (text already set) in a box centred at `centre`, `width` × `height` in its parent's units: wrapped (a little
	// inside the box, room for the bar) and, when taller than the box, scrolling. left: lines from the box's left edge
	// (a list), else centred as the label's pivot has it.
	public static Ma9MTextBox Fit(UILabel label, Vector2 centre, float width, float height, bool left = false, float size = BodySize)
	{
		float textWidth = width - 2f * (BarWidth + BarGap);
		float h = Prepare(label, textWidth, size);
		return Attach(label, centre, width, height, textWidth, h, left);
	}

	// Panel_Popup1/Popup2 (Popup1/Popup2.Initialize, Ma9MBookPopup.FitContent): the message at the body size, wrapped at the box;
	// a longer one grows the popup (the title and the buttons move apart, the box stretches) up to MaxPopupHeight, and scrolls
	// past that. Returns how much the popup grew.
	public static float FitPopup(UILabel content, bool left = false)
	{
		return FitPopup(content, PopupTextHeight, PopupHeight, left);
	}

	// Panel_Popup3/Popup4 (the broadcast's): box 443 × 244, title at y 90, buttons at y −79: textHeight 130, popupHeight 244.
	public static float FitPopup(UILabel content, float textHeight, float popupHeight, bool left = false)
	{
		if (content == null)
		{
			return 0f;
		}
		Vector3 p = content.transform.localPosition;
		float h = Prepare(content, PopupTextWidth);
		if (h > LeftAfterLines * BodySize * 1.5f + 1f)
		{
			left = true;
		}
		Vector2 centre = new Vector2(left ? 0f : p.x, p.y);
		float extra = 0f;
		if (h > textHeight)
		{
			// A little room over and under the text (tester_1117l: grown to the exact height, the glyphs stood 2 units out).
			extra = Mathf.Max(0f, Mathf.Min(h - textHeight + 8f, MaxPopupHeight - popupHeight));
			Grow(content, extra);
		}
		Attach(content, centre, PopupTextWidth + 2f * (BarWidth + BarGap), textHeight + extra, PopupTextWidth, h, left);
		return extra;
	}

	// A popup that turns pages (Ma9MAdvice 자동 편성·특성 추천 [자세히]; 2026-11-18 남은일목록 A7, NC 버그 26: grown by a guessed 17 per
	// line with the text at 13, the box's title band stretched and the title stood on its top edge): sized for the tallest page
	// as FitPopup does, then page 0 shown. Turn pages with SetText. Returns how much the popup grew.
	public static float FitPopupPages(UILabel content, string[] pages)
	{
		if (content == null || pages == null || pages.Length == 0)
		{
			return 0f;
		}
		int tallest = 0;
		float best = -1f;
		for (int i = 0; i < pages.Length; i++)
		{
			content.text = pages[i] ?? string.Empty;
			float h = Prepare(content, PopupTextWidth);
			if (h > best)
			{
				best = h;
				tallest = i;
			}
		}
		content.text = pages[tallest] ?? string.Empty;
		float extra = FitPopup(content);
		SetText(content, pages[0] ?? string.Empty);
		return extra;
	}

	// New text in a label already fitted (FitPopup / FitPopupPages): a scrolling box starts again at the top with its bar and
	// travel for this text, so textcheck sees the real height.
	public static void SetText(UILabel label, string text)
	{
		if (label == null)
		{
			return;
		}
		label.text = text;
		Ma9MTextBox t = label.GetComponent<Ma9MTextBox>();
		if (t == null && label.transform.parent != null)
		{
			t = label.transform.parent.GetComponent<Ma9MTextBox>();
		}
		if (t == null)
		{
			return;
		}
		float h = HeightOf(label);
		t.ContentHeight = h;
		if (t.Scrolls)
		{
			t.m_scroll = 0f;
			t.m_max = Mathf.Max(0f, h - t.Size.y + 2f * ScrollPad);
			if (t.m_bar != null)
			{
				t.m_bar.transform.localScale = new Vector3(BarWidth, Mathf.Max(16f, t.Size.y * Mathf.Min(1f, t.Size.y / Mathf.Max(1f, h))), 1f);
			}
			t.Place();
		}
	}

	// The popup's other parts above the message go up, those below go down, the box (PopupBack) gets taller.
	private static void Grow(UILabel content, float extra)
	{
		Transform root = content.transform.parent;
		if (root == null)
		{
			return;
		}
		float y = content.transform.localPosition.y;
		List<Transform> children = new List<Transform>();
		foreach (Transform child in root)
		{
			children.Add(child);
		}
		foreach (Transform child in children)
		{
			if (child == content.transform || child.name == "Cloud")
			{
				continue;
			}
			if (child.name == "PopupBack")
			{
				if (!SliceBack(child, extra))
				{
					Vector3 s = child.localScale;
					child.localScale = new Vector3(s.x, s.y + extra, s.z);
				}
				continue;
			}
			Vector3 c = child.localPosition;
			if (c.y > y + 1f)
			{
				child.localPosition = new Vector3(c.x, c.y + extra * 0.5f, c.z);
			}
			else if (c.y < y - 1f)
			{
				child.localPosition = new Vector3(c.x, c.y - extra * 0.5f, c.z);
			}
		}
	}

	// The popups' box (atlas sprite popup_box2, 443 × 244 px: shadow 0–10, title band 11–41, body 42–171, the line over the buttons
	// at 172, the buttons' part 173–232, shadow 233–243) is a simple sprite, so stretching it moved the title band and the line
	// over the buttons along (tester_1117l: the line ran through the text). Grown, it is drawn in three pieces of the same atlas
	// (one draw call, depth order kept): the top (to the body) and the bottom (from the line) at the popup's own scale, only the
	// body stretched. False when the box is some other sprite (then it stretches as before).
	private const string BoxSprite = "popup_box2";

	private const float BoxTopRows = 42f, BoxBottomRows = 72f;

	private static bool SliceBack(Transform back, float extra)
	{
		UISprite s = back.GetComponent<UISprite>();
		if (s == null || s.atlas == null || s.spriteName != BoxSprite || s.atlas.coordinates != UIAtlas.Coordinates.Pixels)
		{
			return false;
		}
		UIAtlas.Sprite src = s.atlas.GetSprite(BoxSprite);
		if (src == null || src.outer.height < BoxTopRows + BoxBottomRows + 2f)
		{
			return false;
		}
		float rows = src.outer.height;
		float k = back.localScale.y / rows;
		float w = back.localScale.x;
		float h = back.localScale.y + extra;
		float top = BoxTopRows * k, bottom = BoxBottomRows * k;
		Vector3 c = back.localPosition;
		Piece(back, s, Part(s.atlas, src, "_top", 0f, BoxTopRows), new Vector3(c.x, c.y + h * 0.5f - top * 0.5f, c.z), w, top);
		Piece(back, s, Part(s.atlas, src, "_body", BoxTopRows, rows - BoxBottomRows), new Vector3(c.x, c.y + (bottom - top) * 0.5f, c.z), w, h - top - bottom);
		Piece(back, s, Part(s.atlas, src, "_bottom", rows - BoxBottomRows, rows), new Vector3(c.x, c.y - h * 0.5f + bottom * 0.5f, c.z), w, bottom);
		s.enabled = false;
		return true;
	}

	// Rows y0–y1 of the sprite as a sprite of its own in the same atlas (added once; NGUI's pixel rects count rows from the top).
	private static string Part(UIAtlas atlas, UIAtlas.Sprite src, string suffix, float y0, float y1)
	{
		string name = "ma9m_" + src.name + suffix;
		if (atlas.GetSprite(name) == null)
		{
			UIAtlas.Sprite part = new UIAtlas.Sprite();
			part.name = name;
			part.outer = new Rect(src.outer.x, src.outer.y + y0, src.outer.width, y1 - y0);
			part.inner = part.outer;
			atlas.spriteList.Add(part);
		}
		return name;
	}

	private static void Piece(Transform back, UISprite like, string spriteName, Vector3 pos, float width, float height)
	{
		UISprite piece = NGUITools.AddSprite(back.parent.gameObject, like.atlas, spriteName);
		piece.gameObject.name = back.name + spriteName.Substring(spriteName.LastIndexOf('_'));
		piece.type = UISprite.Type.Simple;
		piece.pivot = UIWidget.Pivot.Center;
		piece.depth = like.depth;
		piece.color = like.color;
		piece.transform.localPosition = pos;
		piece.transform.localScale = new Vector3(width, Mathf.Max(1f, height), 1f);
	}

	private static Ma9MTextBox Attach(UILabel label, Vector2 centre, float width, float height, float textWidth, float contentHeight, bool left)
	{
		float z = label.transform.localPosition.z;
		float x = left ? (centre.x - textWidth * 0.5f) : label.transform.localPosition.x;
		if (contentHeight <= height + 0.5f)
		{
			if (left)
			{
				label.pivot = UIWidget.Pivot.Left;
				label.transform.localPosition = new Vector3(x, centre.y, z);
			}
			Ma9MTextBox fits = label.gameObject.GetComponent<Ma9MTextBox>();
			if (fits == null)
			{
				fits = label.gameObject.AddComponent<Ma9MTextBox>();
			}
			fits.Set(label, centre, width, height, contentHeight, false);
			return fits;
		}
		// Too tall: the label goes under a clipped panel over the box, top-aligned, and the box takes the drags.
		Transform parent = label.transform.parent;
		GameObject go = new GameObject("Ma9MTextScroll");
		go.layer = label.gameObject.layer;
		go.transform.parent = parent;
		go.transform.localRotation = Quaternion.identity;
		go.transform.localScale = Vector3.one;
		go.transform.localPosition = new Vector3(0f, 0f, z);
		UIPanel clip = go.AddComponent<UIPanel>();
		clip.clipping = UIDrawCall.Clipping.HardClip;
		clip.clipRange = new Vector4(centre.x, centre.y, width, height);
		BoxCollider box = go.AddComponent<BoxCollider>();
		box.center = new Vector3(centre.x, centre.y, 0.5f);
		box.size = new Vector3(width, height, 1f);
		Ma9MTextBox t = go.AddComponent<Ma9MTextBox>();
		label.transform.parent = go.transform;
		label.pivot = left ? UIWidget.Pivot.TopLeft : UIWidget.Pivot.Top;
		t.m_x = left ? x : centre.x;
		// A little room inside the window (tester_1117m: text 3 units taller than the box began with its first line cut).
		t.m_top = centre.y + height * 0.5f - ScrollPad;
		NGUITools.MarkParentAsChanged(label.gameObject);
		t.Set(label, centre, width, height, contentHeight, true);
		t.m_max = contentHeight - height + 2f * ScrollPad;
		// The bar's track and the bar, at the right edge of the box (they stay; only the label moves).
		Bar(go, new Vector3(centre.x + width * 0.5f - BarWidth, centre.y, -0.5f), BarWidth, height, new Color(1f, 1f, 1f, 0.12f));
		t.m_bar = Bar(go, Vector3.zero, BarWidth, Mathf.Max(16f, height * height / contentHeight), new Color(1f, 0.84f, 0.38f, 0.85f));
		t.Place();
		UIEventListener listener = UIEventListener.Get(go);
		listener.onDrag = delegate(GameObject g, Vector2 delta)
		{
			t.Move(delta.y * t.UnitsPerPixel());
		};
		listener.onScroll = delegate(GameObject g, float delta)
		{
			t.Move(-delta * 200f);  // a wheel notch (0.1) is about one line
		};
		return t;
	}

	private void Set(UILabel label, Vector2 centre, float width, float height, float contentHeight, bool scrolls)
	{
		Label = label;
		Centre = centre;
		Size = new Vector2(width, height);
		ContentHeight = contentHeight;
		Scrolls = scrolls;
	}

	private static UITexture Bar(GameObject parent, Vector3 pos, float width, float height, Color colour)
	{
		GameObject go = NGUITools.AddChild(parent);
		go.name = "Ma9MTextBar";
		UITexture t = go.AddComponent<UITexture>();
		t.shader = Shader.Find("Unlit/Transparent Colored");
		t.mainTexture = Texture2D.whiteTexture;
		t.color = colour;
		go.transform.localPosition = pos;
		go.transform.localScale = new Vector3(width, height, 1f);
		return t;
	}

	private float UnitsPerPixel()
	{
		UIRoot root = NGUITools.FindInParents<UIRoot>(base.gameObject);
		float height = (root != null) ? root.activeHeight : 480f;
		return height / Mathf.Max(1f, Screen.height);
	}

	// Positive: the text goes up (further down the text comes into view).
	public void Move(float units)
	{
		if (!Scrolls)
		{
			return;
		}
		m_scroll = Mathf.Clamp(m_scroll + units, 0f, m_max);
		Place();
	}

	// 0 the top … 1 the end (the remote "textscroll").
	public void ScrollTo(float fraction)
	{
		if (Scrolls)
		{
			m_scroll = Mathf.Clamp01(fraction) * m_max;
			Place();
		}
	}

	private void Place()
	{
		if (Label != null)
		{
			Label.transform.localPosition = new Vector3(m_x, m_top + m_scroll, 0f);
		}
		if (m_bar != null)
		{
			float barH = m_bar.transform.localScale.y;
			float travel = Size.y - barH;
			float f = (m_max > 0f) ? (m_scroll / m_max) : 0f;
			m_bar.transform.localPosition = new Vector3(Centre.x + Size.x * 0.5f - BarWidth, Centre.y + Size.y * 0.5f - barH * 0.5f - travel * f, -1f);
		}
	}

	private void OnEnable()
	{
		if (!Live.Contains(this))
		{
			Live.Add(this);
		}
	}

	private void OnDisable()
	{
		Live.Remove(this);
	}

	private void OnDestroy()
	{
		Live.Remove(this);
	}
}
