using System;
using Ma9MPackets;
using NetworkLibrary;
using UnityEngine;

// 공인구 배지 (systems session, 2026-11-15). User 11:54: "로비의 경기 preview, 리그 순위표에서도 B, N, S 이런 식으로 공인구 표시가
// 되어 있으면 유저들이 공인구가 어떤 리그다 라는걸 바로 확인할 수 있을 것 같고"; 12:0x: "B, S, N 이렇게 하자. 빅볼 스몰볼 노말볼
// 줄임말로 쓰고 야구공 안에 글씨가 들어가있는 느낌으로"; 12:1x: 트리플A부터 챔피언스까지만, 아래 리그는 노말볼 고정.
// The server puts the season's ball in front of the standings' sponsor ("[빅볼] 마구매니저", server SeasonBall.Tag: the drawn ball
// from 트리플A up, "[노말볼] " below once the draws have started, nothing before). This takes the tag off the title and draws a
// white baseball with the letter instead: B 빅볼 (red), N 노말볼 (dark), S 스몰볼 (blue). The ball is drawn in code, so it needs
// no atlas; it is white with red seams, unlike the app's "n볼" cost icons and the red "N" new-item dots.
public static class Ma9MBallBadge
{
	public const string ObjectName = "Ma9MBallBadge";

	private static Texture2D s_ball;

	// The band last read from the server (빅볼 / 노말볼 / 스몰볼), null when it said none.
	public static string Last;

	// "[빅볼] 마구매니저" → "마구매니저" with band "빅볼"; anything else is returned as it is, band null.
	public static string Strip(string sponsor, out string band)
	{
		band = null;
		if (string.IsNullOrEmpty(sponsor) || sponsor[0] != '[')
		{
			return sponsor;
		}
		int end = sponsor.IndexOf("] ", StringComparison.Ordinal);
		if (end < 0)
		{
			return sponsor;
		}
		string name = sponsor.Substring(1, end - 1);
		if (name != "빅볼" && name != "노말볼" && name != "스몰볼")
		{
			return sponsor;
		}
		band = name;
		return sponsor.Substring(end + 2);
	}

	public static string Letter(string band)
	{
		return band == "빅볼" ? "B" : band == "스몰볼" ? "S" : "N";
	}

	public static Color LetterColour(string band)
	{
		if (band == "빅볼")
		{
			return new Color(0.85f, 0.1f, 0.1f);
		}
		if (band == "스몰볼")
		{
			return new Color(0.1f, 0.35f, 0.85f);
		}
		return new Color(0.18f, 0.18f, 0.18f);
	}

	// A badge left of a title label, as tall as about 1.3 lines of it. band null removes any.
	public static GameObject PutBefore(UILabel title, string band)
	{
		if (title == null)
		{
			return null;
		}
		Transform parent = title.transform.parent;
		Bounds b = NGUIMath.CalculateRelativeWidgetBounds(parent, title.transform);
		float d = Mathf.Max(18f, b.size.y * 1.3f);
		// At the letters' middle, not the label box's (2026-11-18, user's capture of the standings: the ball sat about 12 px below
		// "홈런볼배 프로야구 순위표"; the box runs a line height, 1.5 letters, below the top, and the letters fill its upper part).
		return Put(title, band, new Vector3(b.min.x - 6f - d / 2f, b.center.y + b.size.y * 0.3f, title.transform.localPosition.z), d);
	}

	// A badge of diameter d at pos in the font label's parent; the label gives the font and depth. Any badge put there before goes first.
	public static GameObject Put(UILabel font, string band, Vector3 pos, float d)
	{
		if (font == null)
		{
			return null;
		}
		Transform parent = font.transform.parent;
		Remove(parent);
		if (band == null)
		{
			return null;
		}
		GameObject root = NGUITools.AddChild(parent.gameObject);
		root.name = ObjectName;
		root.transform.localPosition = pos;
		GameObject ballGo = NGUITools.AddChild(root);
		ballGo.name = "Ball";
		UITexture ball = ballGo.AddComponent<UITexture>();
		ball.shader = Shader.Find("Unlit/Transparent Colored");
		// The letter is drawn into the ball's own texture (AI testers 11-16, all six: a white ball without its letter in the lobby,
		// the N's top cut on the standings). A font label over a UITexture is a second draw call, and this NGUI orders draw calls
		// of different materials by distance, not by depth: the ball could cover the letter.
		ball.mainTexture = BallTexture(band);
		ball.depth = font.depth + 1;
		ballGo.transform.localScale = new Vector3(d, d, 1f);
		// A tap tells what the ball means (AI testers 11-16: "눌러도 반응이 없다").
		BoxCollider box = ballGo.AddComponent<BoxCollider>();
		box.size = new Vector3(1f, 1f, 1f);
		string tapped = band;
		UIEventListener.Get(ballGo).onClick = go => Explain(tapped);
		return root;
	}

	// What each ball means, as the help's 공인구 pages say it (Ma9MHelp).
	public static void Explain(string band)
	{
		string body = band == "빅볼"
			? "이번 시즌은 빅볼(B, 고반발)입니다.\n홈런과 장타가 늘어 점수가 많이 나는 시즌입니다."
			: band == "스몰볼"
				? "이번 시즌은 스몰볼(S, 저반발)입니다.\n홈런과 장타가 줄어 점수가 적게 나는 시즌입니다."
				: "이번 시즌은 노말볼(N, 표준)입니다.\n득점이 평소와 같은 시즌입니다.";
		body += "\n트리플A부터 챔피언스까지는 시즌마다 공인구가 바뀌고,\n그 아래 리그는 노말볼로 고정입니다.";
		PopupManager.ViewPopup1("공인구", body, Localization.instance.Get("OK"), null);
	}

	public static void Remove(Transform parent)
	{
		if (parent == null)
		{
			return;
		}
		for (int i = parent.childCount - 1; i >= 0; i--)
		{
			Transform t = parent.GetChild(i);
			if (t.name == ObjectName)
			{
				t.gameObject.SetActive(false);
				UnityEngine.Object.Destroy(t.gameObject);
			}
		}
	}

	private static readonly System.Collections.Generic.Dictionary<string, Texture2D> s_balls = new System.Collections.Generic.Dictionary<string, Texture2D>();

	// A white baseball, 64 px: soft edge, grey rim, slight shade toward the bottom right, two red seams curving in from the sides
	// (kept clear of the middle), and the band's letter in its colour in the middle.
	private static Texture2D BallTexture(string band)
	{
		string letter = Letter(band);
		Texture2D tex;
		if (s_balls.TryGetValue(letter, out tex) && tex != null)
		{
			return tex;
		}
		const int n = 64;
		s_ball = new Texture2D(n, n, TextureFormat.ARGB32, false);
		s_ball.wrapMode = TextureWrapMode.Clamp;
		float c = n / 2f, r = n / 2f - 1f;
		Color white = new Color(0.99f, 0.98f, 0.95f), rim = new Color(0.5f, 0.5f, 0.52f), seam = new Color(0.86f, 0.16f, 0.16f);
		Color ink = LetterColour(band);
		// The letter box: 30 px tall (a little under half the ball), centred; LetterDistance works in units of that height.
		const float letterH = 30f;
		for (int y = 0; y < n; y++)
		{
			for (int x = 0; x < n; x++)
			{
				float dx = x + 0.5f - c, dy = y + 0.5f - c;
				float d = Mathf.Sqrt(dx * dx + dy * dy);
				float a = Mathf.Clamp01(r - d + 0.5f);
				if (a <= 0f)
				{
					s_ball.SetPixel(x, y, new Color(0f, 0f, 0f, 0f));
					continue;
				}
				// Light from the top left (texture y grows upward).
				float shade = 1f - 0.14f * Mathf.Clamp01((dx - dy) / (2f * r) + 0.5f);
				Color col = new Color(white.r * shade, white.g * shade, white.b * shade, 1f);
				float s1 = Mathf.Abs(Mathf.Sqrt((dx - 1.45f * r) * (dx - 1.45f * r) + dy * dy) - 0.95f * r);
				float s2 = Mathf.Abs(Mathf.Sqrt((dx + 1.45f * r) * (dx + 1.45f * r) + dy * dy) - 0.95f * r);
				float sd = Mathf.Min(s1, s2);
				if (sd < 1.7f && d < r - 2f)
				{
					col = Color.Lerp(seam, col, sd / 1.7f);
				}
				float edge = r - 2.4f;
				if (d > edge)
				{
					col = Color.Lerp(col, rim, Mathf.Clamp01((d - edge) / 1.6f));
				}
				// The letter: its stroke distance in pixels, antialiased over one pixel.
				float ld = LetterDistance(letter, dx / letterH, dy / letterH) * letterH;
				float cover = Mathf.Clamp01(StrokeHalf * letterH - ld + 0.5f);
				if (cover > 0f)
				{
					col = Color.Lerp(col, ink, cover);
				}
				col.a = a;
				s_ball.SetPixel(x, y, col);
			}
		}
		s_ball.Apply();
		s_balls[letter] = s_ball;
		return s_ball;
	}

	// Half the stroke of the letter, in letter heights (a bold sans letter).
	private const float StrokeHalf = 0.085f;

	// Distance from (x, y) to the centre line of B, N or S, in letter heights; (0, 0) is the letter's middle, the letter spans
	// y −0.5..0.5 (the stroke half outside that) and about x −0.32..0.32.
	private static float LetterDistance(string letter, float x, float y)
	{
		float top = 0.5f - StrokeHalf, bottom = -0.5f + StrokeHalf;
		if (letter == "B")
		{
			float left = -0.26f, mid = 0.02f;
			float d = Seg(x, y, left, bottom, left, top);
			// The upper bowl (smaller) and the lower bowl: straight runs out from the stem, then a half circle on the right.
			float ru = (top - mid) / 2f, cu = (top + mid) / 2f, xu = 0.03f;
			d = Mathf.Min(d, Seg(x, y, left, top, xu, top));
			d = Mathf.Min(d, Seg(x, y, left, mid, xu, mid));
			d = Mathf.Min(d, HalfRight(x, y, xu, cu, ru));
			float rl = (mid - bottom) / 2f, cl = (mid + bottom) / 2f, xl = 0.05f;
			d = Mathf.Min(d, Seg(x, y, left, mid, xl, mid));
			d = Mathf.Min(d, Seg(x, y, left, bottom, xl, bottom));
			d = Mathf.Min(d, HalfRight(x, y, xl, cl, rl));
			return d;
		}
		if (letter == "S")
		{
			// Two arcs meeting in the middle: the upper one open on the lower right, the lower one open on the upper left.
			float rr = (top - bottom) / 4f, cx = 0f;
			float d = Arc(x, y, cx, top - rr, rr, 25f, 270f);
			d = Mathf.Min(d, Arc(x, y, cx, bottom + rr, rr, -155f, 90f));
			return d;
		}
		// N: two stems and the diagonal from the top left to the bottom right.
		float l = -0.25f, rgt = 0.25f;
		float dn = Seg(x, y, l, bottom, l, top);
		dn = Mathf.Min(dn, Seg(x, y, rgt, bottom, rgt, top));
		dn = Mathf.Min(dn, Seg(x, y, l, top, rgt, bottom));
		return dn;
	}

	private static float Seg(float px, float py, float ax, float ay, float bx, float by)
	{
		float vx = bx - ax, vy = by - ay;
		float t = Mathf.Clamp01(((px - ax) * vx + (py - ay) * vy) / Mathf.Max(1e-6f, vx * vx + vy * vy));
		float qx = ax + t * vx - px, qy = ay + t * vy - py;
		return Mathf.Sqrt(qx * qx + qy * qy);
	}

	// The right half of a circle (x ≥ cx).
	private static float HalfRight(float px, float py, float cx, float cy, float r)
	{
		if (px >= cx)
		{
			float dx = px - cx, dy = py - cy;
			return Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - r);
		}
		float d1 = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy - r) * (py - cy - r));
		float d2 = Mathf.Sqrt((px - cx) * (px - cx) + (py - cy + r) * (py - cy + r));
		return Mathf.Min(d1, d2);
	}

	// An arc of a circle from angle a0 counter-clockwise to a1 (degrees, 0 = right, a1 > a0).
	private static float Arc(float px, float py, float cx, float cy, float r, float a0, float a1)
	{
		float dx = px - cx, dy = py - cy;
		float ang = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
		while (ang < a0)
		{
			ang += 360f;
		}
		if (ang <= a1)
		{
			return Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - r);
		}
		float e0x = cx + r * Mathf.Cos(a0 * Mathf.Deg2Rad), e0y = cy + r * Mathf.Sin(a0 * Mathf.Deg2Rad);
		float e1x = cx + r * Mathf.Cos(a1 * Mathf.Deg2Rad), e1y = cy + r * Mathf.Sin(a1 * Mathf.Deg2Rad);
		return Mathf.Min(Mathf.Sqrt((px - e0x) * (px - e0x) + (py - e0y) * (py - e0y)), Mathf.Sqrt((px - e1x) * (px - e1x) + (py - e1y) * (py - e1y)));
	}
}

// The lobby's next-match box (MainLobby_LeftWindow.Initialize): asks the standings once for the season's ball tag and puts the badge
// under "VS", between the two starters. Nothing when the server sends no tag.
public class Ma9MBallLobby : MonoBehaviour
{
	public MainLobby_LeftWindow window;

	private bool m_asked;

	public void Ask()
	{
		if (!m_asked)
		{
			NetworkConnector.AddProcessCallBack(OnPacket);
			m_asked = true;
		}
		NetworkConnector.SendPacket(new Ma9MPacket.PKS_CS_ENTER_RANK_INFO());
	}

	private void OnPacket(Packet.PKS_PACKET packet)
	{
		if ((Ma9MPacket.SUP)packet.GetSUP() != Ma9MPacket.SUP.SUP_LOBBY || (Ma9MPacket.SUB)packet.GetSUB() != Ma9MPacket.SUB.PK_SC_ENTER_RANK_INFO)
		{
			return;
		}
		Stop();
		if (window == null || window.NextMatchInfo == null || !window.NextMatchInfo.activeInHierarchy || window.SPSlot0 == null || window.SPSlot1 == null)
		{
			return;
		}
		string band;
		Ma9MBallBadge.Strip(((Ma9MPacket.PKS_SC_ENTER_RANK_INFO)packet).season_sponsor.str, out band);
		Ma9MBallBadge.Last = band;
		Transform box = window.Text_OpponentTeamName.transform.parent;
		Vector3 p0 = box.InverseTransformPoint(window.SPSlot0.transform.position);
		Vector3 p1 = box.InverseTransformPoint(window.SPSlot1.transform.position);
		Vector3 mid = (p0 + p1) / 2f;
		// The starters' cards are about 160 units tall around their slots; "VS" sits at their middle, the badge below it.
		Ma9MBallBadge.Put(window.Text_OpponentTeamName, band, new Vector3(mid.x, mid.y - 46f, window.Text_OpponentTeamName.transform.localPosition.z), 34f);
	}

	private void Stop()
	{
		if (m_asked)
		{
			NetworkConnector.RemoveProcessCallBack(OnPacket);
			m_asked = false;
		}
	}

	private void OnDestroy()
	{
		Stop();
	}
}
