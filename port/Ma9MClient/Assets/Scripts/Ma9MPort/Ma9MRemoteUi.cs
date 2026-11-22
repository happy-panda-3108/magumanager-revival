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
		public bool Top, Off;
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

	// files): the 3D renderers drawn at a point of the 960-wide view, nearest first — object path, its materials and their main /
	// mask textures, and the distance. A renderer counts when its bounds, seen by an enabled non-UI camera, cover the point.
	public static string Pick(float vx, float vy)
	{
		float k = Mathf.Max(1, Screen.width) / Ma9MRemote.ViewWidth;
		Vector2 p = new Vector2(vx * k, Screen.height - vy * k);
		List<KeyValuePair<float, string>> hits = new List<KeyValuePair<float, string>>();
		foreach (Camera cam in Camera.allCameras)
		{
			if (cam == null || !cam.isActiveAndEnabled || cam.GetComponent<UICamera>() != null)
			{
				continue;
			}
			Ray ray = cam.ScreenPointToRay(p);
			foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
			{
				if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || (cam.cullingMask & (1 << r.gameObject.layer)) == 0)
				{
					continue;
				}
				// Bounds the camera stands inside (stands, sky, the whole ground) say d < 0: last, after the ones in front.
				if (!r.bounds.IntersectRay(ray, out float d))
				{
					continue;
				}
				if (d < 0f)
				{
					d = 10000f - d;
				}
				StringBuilder sb = new StringBuilder();
				sb.Append(d >= 10000f ? "inside " : d.ToString("0.0") + "m ").Append(cam.name).Append(" | ").Append(PathOf(r.transform)).Append(" |");
				foreach (Material m in r.sharedMaterials)
				{
					if (m == null) continue;
					sb.Append(' ').Append(m.name).Append('[').Append(m.shader != null ? m.shader.name : "?");
					foreach (string prop in new[] { "_MainTex", "_BlendTex", "_Tex1" })
					{
						if (m.HasProperty(prop) && m.GetTexture(prop) != null) sb.Append(' ').Append(prop).Append('=').Append(m.GetTexture(prop).name);
					}
					sb.Append(']');
				}
				hits.Add(new KeyValuePair<float, string>(d, sb.ToString()));
			}
		}
		hits.Sort((a, b) => a.Key.CompareTo(b.Key));
		StringBuilder o = new StringBuilder("ok pick " + vx.ToString("0") + "," + vy.ToString("0") + " hits=" + hits.Count);
		for (int i = 0; i < hits.Count && i < 20; i++)
		{
			o.Append(LineSep).Append(hits[i].Value);
		}
		return o.ToString();
	}

	private static string PathOf(Transform t)
	{
		string s = t.name;
		for (int i = 0; i < 3 && t.parent != null; i++)
		{
			t = t.parent;
			s = t.name + "/" + s;
		}
		return s;
	}

	public static List<Button> Buttons(bool all)
	{
		List<Button> list = new List<Button>();
		foreach (Collider c in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
		{
			if (c == null || !c.gameObject.activeInHierarchy) continue;
			// (engine 11-20, 남은일 "계약관리 [계약연장]·[선수방출]이 ui에 없음"): a greyed button (UIButton/UIImageButton.isEnabled = false)
			// has its collider switched off, so it vanished from the list; it is listed as "(꺼짐)" and tap does not press it.
			bool off = !c.enabled;
			if (off && c.GetComponent<UIImageButton>() == null && c.GetComponent<UIButton>() == null) continue;
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
			bool any = UICamera.Raycast(new Vector3(s.x, s.y, 0f), ref hit) && hit.collider != null;
			// An off collider is never hit: it counts as on top when nothing is there or what is there sits in its own panel (the
			// screen's backdrop), and as covered when another panel (a popup) is in front.
			bool top = off ? !any || NGUITools.FindInParents<UIPanel>(hit.collider.gameObject) == NGUITools.FindInParents<UIPanel>(c.gameObject)
				: any && Related(hit.collider.transform, c.transform);
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
			// (engine 11-20, 남은일 "카드 목록의 선수 이름이 text에 안 나옴", 자급자족·NC) A card's name is drawn in its art, not a label:
			// the slot's card from the table — "'22 이정후 9볼" — ahead of any label it has.
			PlayerCardSlot slot = c.GetComponentInParent<PlayerCardSlot>() ?? c.GetComponentInChildren<PlayerCardSlot>();
			TableReader.TRRow_PlayerCard card = slot != null && slot.GetCardIndex() > 0 ? slot.GetCardInfo() : null;
			if (card != null) label = $"'{card.year % 100:00} {card.name} {card.ball}볼" + (label.Length > 0 ? " · " + label : string.Empty);
			list.Add(new Button { View = View(s), Label = label, Name = c.gameObject.name, Top = top, Off = off });
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
			string line = Mathf.RoundToInt(b.View.x) + "," + Mathf.RoundToInt(b.View.y) + " \"" + Cut(b.Label, 24) + "\" " + b.Name + (b.Off ? " (꺼짐)" : string.Empty) + (b.Top ? string.Empty : " (가림)");
			if (seen.Add(line)) sb.Append(LineSep).Append(line);
		}
		return sb.ToString();
	}

	// sns [from] (engine 11-20, 06:30 회차: 생중계를 네 번 보며 도루·번트를 한 번도 못 봄 — 리그에서는 팀 경기당 도루 시도 1.6번):
	// the game's play-by-play log, the lines the LIVE-SNS screen shows ("1루주자 김실 : 도루로 2루까지 진루", "투수 교체 A → B"), in
	// play order, read from the data while the 3D broadcast keeps running. Each line is "n text"; from = the first n wanted (default:
	// the last 12). The reply's first line says total=N, so the next call asks from N.
	public static string Sns(int from)
	{
		SNSInfo s = DataCenter.instance != null ? DataCenter.instance.Ma9MSnsInfo : null;
		if (s == null || s.m_SNSList == null) return "ok sns scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " total=0 (no game log)";
		List<string> lines = new List<string>();
		for (int h = 0; h < s.m_SNSList.Count; h++)
		{
			List<string> half = s.m_SNSList[h];
			if (half == null || half.Count == 0) continue;
			lines.Add("== " + (h / 2 + 1) + "회" + (h % 2 == 0 ? "초" : "말") + " ==");
			foreach (string block in half)
			{
				// A block is written for the screen, newest line first; its "n번타자 이름" header (a new batter) stays on top.
				List<string> b = new List<string>();
				foreach (string l in (block ?? string.Empty).Split('\n'))
				{
					string c = Clean(l);
					if (c.Length > 0) b.Add(c);
				}
				int head = b.Count > 0 && Regex.IsMatch(b[0], @"^\d+번타자 ") ? 1 : 0;
				if (head == 1) lines.Add(b[0]);
				for (int i = b.Count - 1; i >= head; i--) lines.Add(b[i]);
			}
		}
		if (from < 0) from = Mathf.Max(0, lines.Count - 12);
		// The log line of a play is written when its scene ends; "now=scene:SITUATION:pitches" is the scene playing now, so a
		// watcher can photograph a steal or a bunt while it happens (the play comes on the scene's last pitch).
		DataCenter dc = DataCenter.instance;
		string now = dc.m_simulResults != null && dc.m_curSceneNum >= 0 && dc.m_curSceneNum < dc.m_simulResults.Length && dc.m_simulResults[dc.m_curSceneNum] != null
			? " now=" + dc.m_curSceneNum + ":" + dc.m_simulResults[dc.m_curSceneNum].m_sit.ToString().Replace("ST_", string.Empty) + ":" + dc.m_simulResults[dc.m_curSceneNum].m_NP
			: string.Empty;
		// play=… (INPITCH, HIT, INPLAY, STEALPLAY, …): the moment the play itself starts; the pitches before it take 1-16 s.
		if (PlayManager.instance != null) now += " play=" + PlayManager.instance.GetState();
		StringBuilder sb = new StringBuilder("ok sns scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " total=" + lines.Count + now);
		for (int i = Mathf.Max(0, from); i < lines.Count; i++) sb.Append(LineSep).Append(i).Append(' ').Append(lines[i]);
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
		List<Button> shown = Buttons(false);
		List<Button> list = shown.FindAll(b => !b.Off);
		List<Button> hits = new List<Button>();
		foreach (Button b in list) if (Norm(b.Label) == q) hits.Add(b);
		if (hits.Count == 0) foreach (Button b in list) if (Norm(b.Label).Contains(q)) hits.Add(b);
		if (hits.Count == 0) foreach (Button b in list) if (b.Name.ToLowerInvariant().Contains(q)) hits.Add(b);
		if (hits.Count == 0)
		{
			int g = shown.FindIndex(b => b.Off && (Norm(b.Label).Contains(q) || b.Name.ToLowerInvariant().Contains(q)));
			if (g >= 0)
			{
				what = "\"" + Cut(shown[g].Label.Length > 0 ? shown[g].Label : shown[g].Name, 24) + "\" is greyed out (꺼짐) — 지금은 누를 수 없습니다(카드를 먼저 고르는 화면이면 카드부터)";
				return false;
			}
		}
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
