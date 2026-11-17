using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// 글 넘침 검사 (systems session, 2026-11-17; user 18:1x: "앞으로 모든 글상자들에 글씨가 삐져 나가는지 아닌지 체크해서 스크롤을 생성할지, 글씨
// 양을 줄일지 고민해서 만들도록, 누가 지적해서 수정하는 것이 아니라 스스로 확인할 수 있어야 함"). The remote "textcheck <file>" looks at
// every label on the screen as it is drawn (UIWidget.Ma9MDrawnBounds: the glyphs' real place) and writes what is wrong:
//   넘침     the text runs out of the frame behind it (the smallest sprite or texture behind it holding its middle, at least as tall
//            as one line) or out of its 글상자 (Ma9MTextBox)
//   화면밖   past the screen's edge
//   잘림     a clipped panel cuts it and the panel is not a list or box that scrolls
//   겹침     two texts lie over each other (30 % of the smaller one)
//   줄임     a line limit dropped part of the text
//   줄어듦   shrink-to-fit made it below 3/4 of its size
//   작음     under 10 units tall (19 px on the user's phone)
// then the 글상자 on the screen and whether they scroll. Numbers are screen pixels; "u" are design units (480 tall).
public static class Ma9MTextCheck
{
	private sealed class Item
	{
		public UIWidget Widget;

		public Rect Rect;

		public Camera Cam;

		// Distance from the camera (what is nearer is drawn over).
		public float Depth;

		// The outermost panel above it: one screen, popup or overlay.
		public UIPanel Root;

		// Set when a clipped panel cuts the text (reported if nothing covers it).
		public string Cut;
	}

	private static UIPanel RootPanel(UIWidget w)
	{
		UIPanel root = null;
		for (Transform t = w.cachedTransform; t != null; t = t.parent)
		{
			UIPanel p = t.GetComponent<UIPanel>();
			if (p != null)
			{
				root = p;
			}
		}
		return root;
	}

	// A whole-screen cover nearer the camera (PanelManager puts each later panel 10 nearer, popups last).
	private static bool Covered(Item it, List<Item> covers)
	{
		foreach (Item c in covers)
		{
			if (c.Cam == it.Cam && c.Root != it.Root && c.Depth < it.Depth - 0.01f)
			{
				return true;
			}
		}
		return false;
	}

	// Panels that cut their text on purpose (the lobby's 소식 band, which flows through its window).
	public static readonly HashSet<UIPanel> ClipOk = new HashSet<UIPanel>();

	private static readonly string[] Order = { "넘침", "화면밖", "잘림", "겹침", "줄임", "줄어듦", "작음" };

	public static string Run(string path)
	{
		s_found.Clear();
		float ppu = Mathf.Max(0.01f, Ma9MFontRes.PixelsPerUnit);
		List<Item> labels = new List<Item>();
		List<Item> frames = new List<Item>();
		List<Item> covers = new List<Item>();
		Rect screen = new Rect(0f, 0f, Screen.width, Screen.height);
		foreach (UIWidget w in UnityEngine.Object.FindObjectsByType<UIWidget>(FindObjectsSortMode.None))
		{
			if (w == null || !w.enabled || !w.gameObject.activeInHierarchy || w.color.a < 0.05f)
			{
				continue;
			}
			UIPanel panel = w.panel;
			if (panel != null && panel.alpha < 0.05f)
			{
				continue;
			}
			UILabel label = w as UILabel;
			if (label != null && string.IsNullOrEmpty(label.text?.Trim()))
			{
				continue;
			}
			Camera cam = CameraFor(w.gameObject.layer);
			Rect r;
			if (cam == null || !ScreenRect(w, cam, out r))
			{
				continue;
			}
			// Wholly off the screen: a panel parked outside until it slides in, not shown.
			Rect onScreen = Intersect(r, screen);
			if (onScreen.width <= 0.5f || onScreen.height <= 0.5f)
			{
				continue;
			}
			Item item = new Item { Widget = w, Rect = r, Cam = cam, Depth = cam.WorldToScreenPoint(w.cachedTransform.position).z, Root = RootPanel(w) };
			// A clipped panel: only the part inside counts; nothing inside, not on screen.
			if (panel != null && panel.clipping != UIDrawCall.Clipping.None)
			{
				Rect clip = ClipRect(panel, cam);
				Rect inside = Intersect(r, clip);
				if (inside.width <= 0.5f || inside.height <= 0.5f)
				{
					continue;
				}
				if (label != null && Area(inside) < Area(r) * 0.98f && !Scrolls(panel) && !ClipOk.Contains(panel))
				{
					item.Cut = "판 밖 " + Out(r, clip).ToString("0") + "px";
				}
				item.Rect = inside;
			}
			if (label != null)
			{
				labels.Add(item);
			}
			else if (r.width < Screen.width * 0.9f || r.height < Screen.height * 0.9f)
			{
				frames.Add(item);
			}
			else if (w.color.a * ((panel != null) ? panel.alpha : 1f) >= 0.3f)
			{
				covers.Add(item);  // a whole-screen dim or picture: what lies behind it is not what the user reads
			}
		}
		// What a whole-screen cover in front hides is left out (the screen under a popup or an overlay).
		labels.RemoveAll(it => Covered(it, covers));
		frames.RemoveAll(it => Covered(it, covers));
		// A text under an opaque sprite nearer the camera is hidden too (tester_1117o: the roster list's positions under the
		// filter panel's buttons were counted as 겹침).
		labels.RemoveAll(it => frames.Any(f => f.Cam == it.Cam && f.Root != it.Root && f.Depth < it.Depth - 0.01f && f.Widget.color.a >= 0.9f
			&& Area(Intersect(f.Rect, it.Rect)) >= Area(it.Rect) * 0.8f));
		foreach (Item it in labels)
		{
			UILabel label = (UILabel)it.Widget;
			Rect r = it.Rect;
			if (it.Cut != null)
			{
				Add("잘림", label, r, it.Cut);
			}
			float over = -Mathf.Min(Mathf.Min(r.xMin, r.yMin), Mathf.Min(Screen.width - r.xMax, Screen.height - r.yMax));
			if (over > 1f)
			{
				Add("화면밖", label, r, over.ToString("0") + "px");
			}
			Ma9MTextBox box = BoxOf(label);
			if (box != null)
			{
				if (!box.Scrolls)
				{
					Rect b = BoxRect(box, it.Cam);
					float o = Out(r, b);
					if (o > 2f)
					{
						Add("넘침", label, r, "글상자 밖 " + o.ToString("0") + "px");
					}
				}
			}
			else
			{
				Item frame = FrameOf(it, frames);
				if (frame != null)
				{
					float o = Out(r, frame.Rect);
					if (o > 2f && o > r.height * 0.05f)
					{
						Add("넘침", label, r, "틀(" + frame.Widget.name + ") 밖 " + o.ToString("0") + "px");
					}
				}
			}
			if (label.maxLineCount > 0 && Plain(label.processedText).Length < Plain(label.supportEncoding ? NGUITools.StripSymbols(label.text) : label.text).Length)
			{
				Add("줄임", label, r, label.maxLineCount + "줄 제한");
			}
			float units = label.transform.localScale.y;
			float drawnUnits = LineUnits(label, it.Cam) / ppu;
			if (label.shrinkToFit && label.font != null && units < label.font.Ma9MDesignSize * label.font.pixelSize * 0.75f)
			{
				Add("줄어듦", label, r, units.ToString("0.#") + "u / " + label.font.Ma9MDesignSize + "u");
			}
			else if (drawnUnits > 0f && drawnUnits < 10f)
			{
				Add("작음", label, r, drawnUnits.ToString("0.#") + "u");
			}
		}
		// Texts over each other.
		for (int i = 0; i < labels.Count; i++)
		{
			for (int j = i + 1; j < labels.Count; j++)
			{
				Rect a = labels[i].Rect, b = labels[j].Rect;
				Rect x = Intersect(a, b);
				if (x.width <= 1f || x.height <= 1f)
				{
					continue;
				}
				float share = Area(x) / Mathf.Max(1f, Mathf.Min(Area(a), Area(b)));
				if (share < 0.3f)
				{
					continue;
				}
				UILabel la = (UILabel)labels[i].Widget, lb = (UILabel)labels[j].Widget;
				if (la.text == lb.text && Mathf.Abs(a.center.x - b.center.x) < 3f && Mathf.Abs(a.center.y - b.center.y) < 3f)
				{
					continue;  // a copy laid on itself (a hand-made shadow)
				}
				Add("겹침", la, a, Mathf.RoundToInt(share * 100f) + "% \"" + Snip(lb.text) + "\" " + Path(lb.transform));
			}
		}
		StringBuilder sb = new StringBuilder();
		sb.Append("scene=").Append(SceneManager.GetActiveScene().name).Append(" screen=").Append(Screen.width).Append('x').Append(Screen.height)
			.Append(" labels=").Append(labels.Count).Append(" ppu=").Append(ppu.ToString("0.00")).Append('\n');
		Dictionary<string, int> counts = new Dictionary<string, int>();
		foreach (string kind in Order)
		{
			foreach (string line in s_found)
			{
				if (line.StartsWith("[" + kind + "]", StringComparison.Ordinal))
				{
					sb.Append(line).Append('\n');
					counts[kind] = (counts.TryGetValue(kind, out int n) ? n : 0) + 1;
				}
			}
		}
		foreach (Ma9MTextBox t in Ma9MTextBox.Live)
		{
			if (t != null && t.Label != null && t.isActiveAndEnabled)
			{
				sb.Append("[글상자] ").Append(Path(t.Label.transform)).Append(" | \"").Append(Snip(t.Label.text)).Append("\" | ")
					.Append(t.Scrolls ? "스크롤" : "맞음").Append(" 글 ").Append(t.ContentHeight.ToString("0")).Append("u / 칸 ").Append(t.Size.y.ToString("0")).Append("u\n");
			}
		}
		s_found.Clear();
		string summary = "ok";
		foreach (string kind in Order)
		{
			if (counts.TryGetValue(kind, out int n))
			{
				summary += " " + kind + "=" + n;
			}
		}
		if (summary == "ok")
		{
			summary = "ok 문제 없음";
		}
		if (!string.IsNullOrEmpty(path))
		{
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
			summary += " → " + path;
		}
		return summary;
	}

	private static readonly List<string> s_found = new List<string>();

	private static void Add(string kind, UILabel label, Rect r, string detail)
	{
		s_found.Add("[" + kind + "] " + Path(label.transform) + " | \"" + Snip(label.text) + "\" | " + detail + " | (" + r.xMin.ToString("0") + "," + (Screen.height - r.yMax).ToString("0") + " " + r.width.ToString("0") + "×" + r.height.ToString("0") + ")");
	}

	private static Camera CameraFor(int layer)
	{
		UICamera ui = UICamera.FindCameraForLayer(layer);
		return (ui != null) ? ui.cachedCamera : NGUITools.FindCameraForLayer(layer);
	}

	private static bool ScreenRect(UIWidget w, Camera cam, out Rect r)
	{
		r = default(Rect);
		Vector3 min, max;
		if (!w.Ma9MDrawnBounds(out min, out max))
		{
			return false;
		}
		Transform t = w.cachedTransform;
		Vector3 a = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(min.x, min.y, 0f)));
		Vector3 b = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(max.x, max.y, 0f)));
		Vector3 c = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(min.x, max.y, 0f)));
		Vector3 d = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(max.x, min.y, 0f)));
		float x0 = Mathf.Min(Mathf.Min(a.x, b.x), Mathf.Min(c.x, d.x)), x1 = Mathf.Max(Mathf.Max(a.x, b.x), Mathf.Max(c.x, d.x));
		float y0 = Mathf.Min(Mathf.Min(a.y, b.y), Mathf.Min(c.y, d.y)), y1 = Mathf.Max(Mathf.Max(a.y, b.y), Mathf.Max(c.y, d.y));
		r = Rect.MinMaxRect(x0, y0, x1, y1);
		return r.width > 0.5f && r.height > 0.5f;
	}

	private static Rect ClipRect(UIPanel panel, Camera cam)
	{
		Vector4 c = panel.clipRange;
		Transform t = panel.cachedTransform;
		Vector3 a = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(c.x - c.z * 0.5f, c.y - c.w * 0.5f, 0f)));
		Vector3 b = cam.WorldToScreenPoint(t.TransformPoint(new Vector3(c.x + c.z * 0.5f, c.y + c.w * 0.5f, 0f)));
		return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
	}

	private static Rect BoxRect(Ma9MTextBox box, Camera cam)
	{
		Transform parent = box.Label.transform.parent;
		if (box.GetComponent<UIPanel>() == null && parent == null)
		{
			return new Rect(0f, 0f, Screen.width, Screen.height);
		}
		Vector3 a = cam.WorldToScreenPoint(parent.TransformPoint(new Vector3(box.Centre.x - box.Size.x * 0.5f, box.Centre.y - box.Size.y * 0.5f, 0f)));
		Vector3 b = cam.WorldToScreenPoint(parent.TransformPoint(new Vector3(box.Centre.x + box.Size.x * 0.5f, box.Centre.y + box.Size.y * 0.5f, 0f)));
		return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
	}

	private static Ma9MTextBox BoxOf(UILabel label)
	{
		Ma9MTextBox own = label.GetComponent<Ma9MTextBox>();
		if (own != null)
		{
			return own;
		}
		Transform p = label.transform.parent;
		return (p != null) ? p.GetComponent<Ma9MTextBox>() : null;
	}

	// A list (UIDraggablePanel) or a 글상자 that scrolls.
	private static bool Scrolls(UIPanel panel)
	{
		if (panel.GetComponent<UIDraggablePanel>() != null)
		{
			return true;
		}
		Ma9MTextBox box = panel.GetComponent<Ma9MTextBox>();
		return box != null && box.Scrolls;
	}

	// The smallest sprite or texture behind the label that holds its middle and is at least as tall as one line of it.
	private static Item FrameOf(Item label, List<Item> frames)
	{
		UIWidget lw = label.Widget;
		Vector2 mid = label.Rect.center;
		float line = LineUnits((UILabel)lw, label.Cam) * 0.8f;
		float labelZ = lw.cachedTransform.position.z;
		Item best = null;
		foreach (Item f in frames)
		{
			// Only a frame of the same screen or popup (not a sprite of whatever lies under it), holding most of the text (11-17
			// tester_1117o: a card slot or icon that only touched a stat name's middle was taken for its frame).
			if (f.Cam != label.Cam || f.Root != label.Root || !f.Rect.Contains(mid) || f.Rect.height < line
				|| Area(Intersect(f.Rect, label.Rect)) < Area(label.Rect) * 0.6f)
			{
				continue;
			}
			bool behind = (f.Widget.panel == lw.panel) ? (f.Widget.depth < lw.depth) : (f.Widget.cachedTransform.position.z >= labelZ - 0.001f);
			if (!behind)
			{
				continue;
			}
			// The one right behind the text (nearest the camera among those behind it), then the smaller (tester_1117p: the
			// filter buttons' labels took a card's small sprite far behind the popup instead of their own button).
			if (best == null || f.Depth < best.Depth - 0.001f || (Math.Abs(f.Depth - best.Depth) <= 0.001f && Area(f.Rect) < Area(best.Rect)))
			{
				best = f;
			}
		}
		return best;
	}

	// One line of the label on screen, in pixels.
	private static float LineUnits(UILabel label, Camera cam)
	{
		Transform t = label.cachedTransform;
		Vector3 a = cam.WorldToScreenPoint(t.TransformPoint(Vector3.zero));
		Vector3 b = cam.WorldToScreenPoint(t.TransformPoint(Vector3.up));
		return Mathf.Abs(b.y - a.y);
	}

	// How far `r` runs past `frame` on its worst side (0 when inside).
	private static float Out(Rect r, Rect frame)
	{
		return Mathf.Max(0f, Mathf.Max(Mathf.Max(frame.xMin - r.xMin, r.xMax - frame.xMax), Mathf.Max(frame.yMin - r.yMin, r.yMax - frame.yMax)));
	}

	private static Rect Intersect(Rect a, Rect b)
	{
		float x0 = Mathf.Max(a.xMin, b.xMin), x1 = Mathf.Min(a.xMax, b.xMax);
		float y0 = Mathf.Max(a.yMin, b.yMin), y1 = Mathf.Min(a.yMax, b.yMax);
		return (x1 > x0 && y1 > y0) ? Rect.MinMaxRect(x0, y0, x1, y1) : new Rect(0f, 0f, 0f, 0f);
	}

	private static float Area(Rect r)
	{
		return Mathf.Max(0f, r.width) * Mathf.Max(0f, r.height);
	}

	private static string Plain(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return string.Empty;
		}
		StringBuilder sb = new StringBuilder(s.Length);
		foreach (char c in s)
		{
			if (!char.IsWhiteSpace(c))
			{
				sb.Append(c);
			}
		}
		return sb.ToString();
	}

	private static string Snip(string s)
	{
		if (string.IsNullOrEmpty(s))
		{
			return string.Empty;
		}
		s = s.Replace("\r", string.Empty).Replace("\n", "⏎");
		return (s.Length > 28) ? (s.Substring(0, 28) + "…") : s;
	}

	private static string Path(Transform t)
	{
		string path = t.name;
		int n = 0;
		for (Transform p = t.parent; p != null && n < 3; p = p.parent, n++)
		{
			path = p.name + "/" + path;
		}
		return path;
	}
}
