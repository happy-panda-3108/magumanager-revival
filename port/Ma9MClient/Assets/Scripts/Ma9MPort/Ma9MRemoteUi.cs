using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

// Ma9M (engine session, 2026-11-19, user: "전부 다 합시다. 어떻게든, 테스터가 실행될 때 사용되는 토큰 양만 줄이면 돼"): the AI testers
// read the screen as text instead of pictures. Ma9MRemote's commands:
//   ui [all]            the buttons a tap would reach now: "x,y "label" name", in the 960-wide view (all: covered ones too)
//   text [filter]       the labels on the screen, nearest panel first: "x,y "text""; with a filter only those holding it
//   tap <text|name>[#n] taps the n-th (default first) reachable button whose label or name holds the text
// Lines are joined with LineSep on the wire (one reply line); tools\ai_tester\ma9m_remote prints them as lines.
public static class Ma9MRemoteUi
{
	public const char LineSep = '\u001F';

	private static readonly Regex s_codes = new Regex(@"\[[0-9a-fA-F]{6}\]|\[[0-9a-fA-F]{2}\]|\[-\]|\[[bisu]\]|\[/[bisu]\]");

	public struct Button
	{
		public Vector2 View;
		public string Label, Name;
		public bool Top;
	}

	public static string Clean(string s)
	{
		if (string.IsNullOrEmpty(s)) return string.Empty;
		s = s_codes.Replace(s, string.Empty).Replace("\r", string.Empty).Replace('\n', ' ').Replace('\t', ' ').Trim();
		while (s.Contains("  ")) s = s.Replace("  ", " ");
		return s;
	}

	private static string Cut(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

	private static Vector2 View(Vector3 screen)
	{
		float k = Ma9MRemote.ViewWidth / Mathf.Max(1, Screen.width);
		return new Vector2(screen.x * k, (Screen.height - screen.y) * k);
	}

	private static bool Related(Transform a, Transform b) => a == b || a.IsChildOf(b) || b.IsChildOf(a);

	public static List<Button> Buttons(bool all)
	{
		List<Button> list = new List<Button>();
		foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
		{
			if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
			UICamera uc = UICamera.FindCameraForLayer(c.gameObject.layer);
			if (uc == null) continue;
			Camera cam = uc.cachedCamera;
			Bounds b = c.bounds;
			Vector3 s = cam.WorldToScreenPoint(b.center);
			if (s.x < 0 || s.y < 0 || s.x > Screen.width || s.y > Screen.height) continue;
			Vector3 lo = cam.WorldToScreenPoint(b.min), hi = cam.WorldToScreenPoint(b.max);
			bool huge = Mathf.Abs(hi.x - lo.x) > Screen.width * 0.9f && Mathf.Abs(hi.y - lo.y) > Screen.height * 0.9f;
			if (huge && !all) continue;
			RaycastHit hit = default(RaycastHit);
			bool top = UICamera.Raycast(new Vector3(s.x, s.y, 0f), ref hit) && hit.collider != null && Related(hit.collider.transform, c.transform);
			if (!top && !all) continue;
			string label = string.Empty;
			foreach (UILabel l in c.GetComponentsInChildren<UILabel>(false))
			{
				if (l.enabled && l.color.a > 0.05f && !string.IsNullOrEmpty(l.text?.Trim()))
				{
					label = Clean(l.text);
					break;
				}
			}
			list.Add(new Button { View = View(s), Label = label, Name = c.gameObject.name, Top = top });
		}
		list.Sort((p, q) => Mathf.RoundToInt(p.View.y / 8f) != Mathf.RoundToInt(q.View.y / 8f) ? p.View.y.CompareTo(q.View.y) : p.View.x.CompareTo(q.View.x));
		return list;
	}

	public static string Ui(bool all)
	{
		List<Button> list = Buttons(all);
		StringBuilder sb = new StringBuilder();
		sb.Append("ok ui scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " buttons=" + list.Count);
		HashSet<string> seen = new HashSet<string>();
		foreach (Button b in list)
		{
			string line = Mathf.RoundToInt(b.View.x) + "," + Mathf.RoundToInt(b.View.y) + " \"" + Cut(b.Label, 24) + "\" " + b.Name + (b.Top ? string.Empty : " (가림)");
			if (seen.Add(line)) sb.Append(LineSep).Append(line);
		}
		return sb.ToString();
	}

	public static string Text(string filter)
	{
		// Labels on screen, grouped by their top panel, the nearest panel (a popup) first.
		List<(float z, string root, Vector2 v, string text)> items = new List<(float, string, Vector2, string)>();
		Rect screen = new Rect(0, 0, Screen.width, Screen.height);
		string f = string.IsNullOrEmpty(filter) ? null : Norm(filter);
		foreach (UILabel l in UnityEngine.Object.FindObjectsByType<UILabel>(FindObjectsSortMode.None))
		{
			if (l == null || !l.enabled || !l.gameObject.activeInHierarchy || l.color.a < 0.05f) continue;
			string t = Clean(l.text);
			if (t.Length == 0) continue;
			if (f != null && !Norm(t).Contains(f)) continue;
			UIPanel p = l.panel;
			if (p != null && p.alpha < 0.05f) continue;
			UICamera uc = UICamera.FindCameraForLayer(l.gameObject.layer);
			Camera cam = uc != null ? uc.cachedCamera : NGUITools.FindCameraForLayer(l.gameObject.layer);
			if (cam == null) continue;
			Vector3 s = cam.WorldToScreenPoint(l.cachedTransform.position);
			if (!screen.Contains(new Vector2(s.x, s.y))) continue;
			if (p != null && p.clipping != UIDrawCall.Clipping.None)
			{
				Vector4 c = p.clipRange;
				Transform pt = p.cachedTransform;
				Vector3 a = cam.WorldToScreenPoint(pt.TransformPoint(new Vector3(c.x - c.z * 0.5f, c.y - c.w * 0.5f, 0f)));
				Vector3 b = cam.WorldToScreenPoint(pt.TransformPoint(new Vector3(c.x + c.z * 0.5f, c.y + c.w * 0.5f, 0f)));
				if (!Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y)).Contains(new Vector2(s.x, s.y))) continue;
			}
			UIPanel top = p;
			for (Transform t2 = l.transform; t2 != null; t2 = t2.parent)
			{
				UIPanel up = t2.GetComponent<UIPanel>();
				if (up != null) top = up;
			}
			items.Add((s.z, top != null ? top.name : "-", View(s), t));
		}
		Dictionary<string, float> near = new Dictionary<string, float>();
		foreach (var it in items) near[it.root] = near.TryGetValue(it.root, out float z) ? Mathf.Min(z, it.z) : it.z;
		items.Sort((p, q) =>
		{
			int g = near[p.root].CompareTo(near[q.root]);
			if (g != 0) return g;
			int r = string.CompareOrdinal(p.root, q.root);
			if (r != 0) return r;
			return Mathf.RoundToInt(p.v.y / 8f) != Mathf.RoundToInt(q.v.y / 8f) ? p.v.y.CompareTo(q.v.y) : p.v.x.CompareTo(q.v.x);
		});
		StringBuilder sb = new StringBuilder("ok text scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " labels=" + items.Count);
		string group = null;
		int n = 0;
		foreach (var it in items)
		{
			if (it.root != group)
			{
				group = it.root;
				sb.Append(LineSep).Append("[" + group + "]");
			}
			sb.Append(LineSep).Append(Mathf.RoundToInt(it.v.x) + "," + Mathf.RoundToInt(it.v.y) + " \"" + Cut(it.text, 60) + "\"");
			if (++n >= 220)
			{
				sb.Append(LineSep).Append("… (more; give a filter)");
				break;
			}
		}
		return sb.ToString();
	}

	private static string Norm(string s) => Clean(s).Replace(" ", string.Empty).ToLowerInvariant();

	// The button to tap for "query" or "query#n": exact label first, then a label holding it, then a name holding it.
	public static bool Find(string query, out Vector2 view, out string what)
	{
		view = default(Vector2);
		what = null;
		int nth = 1;
		int hash = query.LastIndexOf('#');
		if (hash > 0 && int.TryParse(query.Substring(hash + 1), out int k) && k >= 1)
		{
			nth = k;
			query = query.Substring(0, hash);
		}
		string q = Norm(query);
		if (q.Length == 0) return false;
		List<Button> list = Buttons(false);
		List<Button> hits = new List<Button>();
		foreach (Button b in list) if (Norm(b.Label) == q) hits.Add(b);
		if (hits.Count == 0) foreach (Button b in list) if (Norm(b.Label).Contains(q)) hits.Add(b);
		if (hits.Count == 0) foreach (Button b in list) if (b.Name.ToLowerInvariant().Contains(q)) hits.Add(b);
		if (hits.Count < nth)
		{
			StringBuilder sb = new StringBuilder();
			foreach (Button b in list) sb.Append(" | ").Append(Cut(b.Label.Length > 0 ? b.Label : b.Name, 16));
			what = "no button for \"" + query + "\"" + (hits.Count > 0 ? " #" + nth + " (only " + hits.Count + ")" : string.Empty) + "; on screen:" + sb;
			return false;
		}
		Button hit = hits[nth - 1];
		view = hit.View;
		what = "tapped \"" + Cut(hit.Label, 24) + "\" " + hit.Name + " at " + Mathf.RoundToInt(view.x) + "," + Mathf.RoundToInt(view.y) + (hits.Count > 1 ? " (" + hits.Count + " matches)" : string.Empty);
		return true;
	}
}
