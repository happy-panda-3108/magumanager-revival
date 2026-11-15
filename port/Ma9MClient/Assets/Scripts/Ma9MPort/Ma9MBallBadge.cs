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
		return Put(title, band, new Vector3(b.min.x - 6f - d / 2f, b.center.y, title.transform.localPosition.z), d);
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
		ball.mainTexture = BallTexture();
		ball.depth = font.depth + 1;
		ballGo.transform.localScale = new Vector3(d, d, 1f);
		GameObject letterGo = UnityEngine.Object.Instantiate(font.gameObject) as GameObject;
		letterGo.name = "Letter";
		letterGo.transform.parent = root.transform;
		letterGo.transform.localRotation = Quaternion.identity;
		Ma9MCardSpread.DropLocalize(letterGo);
		foreach (Collider c in letterGo.GetComponents<Collider>())
		{
			UnityEngine.Object.Destroy(c);
		}
		for (int i = letterGo.transform.childCount - 1; i >= 0; i--)
		{
			UnityEngine.Object.Destroy(letterGo.transform.GetChild(i).gameObject);
		}
		UILabel letter = letterGo.GetComponent<UILabel>();
		letter.text = Letter(band);
		letter.color = LetterColour(band);
		letter.effectStyle = UILabel.Effect.None;
		letter.pivot = UIWidget.Pivot.Center;
		letter.depth = font.depth + 2;
		float s = d * 0.66f;
		letterGo.transform.localScale = new Vector3(s, s, 1f);
		letterGo.transform.localPosition = new Vector3(0f, d * 0.02f, 0f);
		letterGo.SetActive(true);
		return root;
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

	// A white baseball, 64 px: soft edge, grey rim, slight shade toward the bottom right, two red seams curving in from the sides
	// (kept clear of the middle where the letter goes).
	private static Texture2D BallTexture()
	{
		if (s_ball != null)
		{
			return s_ball;
		}
		const int n = 64;
		s_ball = new Texture2D(n, n, TextureFormat.ARGB32, false);
		s_ball.wrapMode = TextureWrapMode.Clamp;
		float c = n / 2f, r = n / 2f - 1f;
		Color white = new Color(0.99f, 0.98f, 0.95f), rim = new Color(0.5f, 0.5f, 0.52f), seam = new Color(0.86f, 0.16f, 0.16f);
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
				col.a = a;
				s_ball.SetPixel(x, y, col);
			}
		}
		s_ball.Apply();
		return s_ball;
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
