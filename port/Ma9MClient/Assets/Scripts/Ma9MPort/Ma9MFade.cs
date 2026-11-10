using UnityEngine;
using UnityEngine.SceneManagement;

// Ma9M port (engine session, 2026-10-31; user: "하이라이트를 보고 있는데 여전히 시나리오 이후에 잠깐정도 저런 화면이
// 나오면서 멈추긴 하는데 … 더 자연스럽게 다음 장면으로 넘어갈 수 있도록"): between a game's highlight scenes the
// screen goes black for a moment instead of showing the next scene being set up (the field cleared, the ball left
// hanging, the next batter's camera waiting). PlayManager fades out at the end of a scene and in at the next batter.
public sealed class Ma9MFade : MonoBehaviour
{
	private static Ma9MFade s_fade;
	private static Texture2D s_black;

	private float m_alpha, m_target, m_speed = 1f, m_blackSince = -1f;

	// Fully black now.
	public static bool Black => s_fade != null && s_fade.m_alpha >= 0.999f;

	public static void Out(float seconds)
	{
		if (s_fade == null)
		{
			GameObject go = new GameObject("Ma9MFade");
			DontDestroyOnLoad(go);
			s_fade = go.AddComponent<Ma9MFade>();
			SceneManager.activeSceneChanged += (a, b) => Clear();
		}
		s_fade.Go(1f, seconds);
	}

	public static void In(float seconds)
	{
		if (s_fade != null) s_fade.Go(0f, seconds);
	}

	public static void Clear()
	{
		if (s_fade == null) return;
		s_fade.m_alpha = 0f;
		s_fade.m_target = 0f;
	}

	private void Go(float to, float seconds)
	{
		m_target = to;
		m_speed = 1f / Mathf.Max(0.01f, seconds);
	}

	private void Update()
	{
		m_alpha = Mathf.MoveTowards(m_alpha, m_target, m_speed * Time.unscaledDeltaTime);
		// Never left black: whatever was to bring the picture back, it comes back on its own after a while.
		if (m_alpha >= 0.999f)
		{
			if (m_blackSince < 0f) m_blackSince = Time.realtimeSinceStartup;
			if (Time.realtimeSinceStartup - m_blackSince > 2.5f) Go(0f, 0.4f);
		}
		else
		{
			m_blackSince = -1f;
		}
	}

	private void OnGUI()
	{
		if (m_alpha <= 0.001f) return;
		if (s_black == null)
		{
			s_black = new Texture2D(1, 1);
			s_black.SetPixel(0, 0, Color.black);
			s_black.Apply();
		}
		GUI.depth = -1000;
		Color c = GUI.color;
		GUI.color = new Color(0f, 0f, 0f, m_alpha);
		GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), s_black);
		GUI.color = c;
	}
}
