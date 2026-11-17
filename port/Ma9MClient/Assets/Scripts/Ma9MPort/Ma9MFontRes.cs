using System;
using UnityEngine;
using UnityEngine.SceneManagement;

// Ma9M port (systems session, 2026-11-17; user 18:1x: "도움말이랑 ui마다 글씨 폰트나 크기, 전부 다 다르기 때문에 살짝 불편함. 해상도도 높은
// 글씨 폰트에 가독성 높은 글씨 크기, 줄간격 등을 유지해서 읽기 편하게 만들어줬으면 함"). The app's dynamic fonts (DroidSansFallback18/24)
// drew their glyphs 14 and 18 px tall and NGUI stretched them to the label's size: on the user's phone (2000 × 923, 480 design
// units tall, so 1.92 px a unit) a 14-unit line came out of 14-px glyphs stretched to 27 px, soft at the edges. Each dynamic font
// now draws at its design size × the screen's pixels per unit (UIFont.Ma9MRenderAt); layouts keep the design size
// (UIFont.Ma9MDesignSize), so only the sharpness changes. "-ma9mnofontres" keeps the old glyphs (for before/after pictures).
// Not in the editor: the fonts are prefab assets there and the change would be saved into them.
public static class Ma9MFontRes
{
	private static bool s_off;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		if (Application.isEditor || Array.IndexOf(Environment.GetCommandLineArgs(), "-ma9mnofontres") >= 0)
		{
			s_off = true;
			return;
		}
		SceneManager.sceneLoaded += delegate
		{
			Apply();
		};
		Apply();
	}

	// Screen pixels per design unit (UIRoot: 480 units tall on screens wider than 5:3, 800 wide on narrower ones).
	public static float PixelsPerUnit
	{
		get
		{
			return Mathf.Min(Screen.height / 480f, Screen.width / 800f);
		}
	}

	public static void Apply()
	{
		if (s_off)
		{
			return;
		}
		float ppu = PixelsPerUnit;
		if (ppu <= 1.05f)
		{
			return;
		}
		try
		{
			foreach (UIFont font in Resources.FindObjectsOfTypeAll<UIFont>())
			{
				if (font == null || !font.isDynamic || font.replacement != null)
				{
					continue;
				}
				int pixels = Mathf.RoundToInt(font.Ma9MDesignSize * ppu);
				if (font.Ma9MRenderAt(pixels))
				{
					Debug.Log("[Ma9MFontRes] " + font.name + ": " + font.Ma9MDesignSize + " → " + font.dynamicFontSize + " px (" + ppu.ToString("0.00") + " px a unit)");
				}
			}
		}
		catch (Exception e)
		{
			Debug.Log("[Ma9MFontRes] " + e.Message);
		}
	}
}
