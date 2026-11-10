using UnityEngine;

// Ma9M port: card pictures are made for today's screens. The server sends them at four times the
// 2014 phone size (face: 512x512 texture, detail: 440x512), and this class makes them fit any
// screen on its own:
// - the card keeps the same size in the UI whatever the picture's resolution, so layouts made for
//   the original 128x128 pictures stay as they were;
// - mipmaps and trilinear filtering keep text and outlines clean when a card is drawn smaller;
// - on small screens the picture is halved on the GPU (no read-back) while it is still more than
//   twice what the screen can show, which saves memory on phones.
public static class Ma9MCardArt
{
	// The layout's own sizes, in UI units (the original 800x480 screen).
	public const float FaceWidth = 79f;

	public const float FaceHeight = 127f;

	public const float FaceTexture = 128f;

	public const float DetailHeight = 128f;

	private static bool s_logged;

	// The picture's texture, sized for this screen. logicalTexHeight is the texture's height in
	// UI units (128 for both the face texture and the detail picture).
	public static Texture2D Load(byte[] data, GameObject owner, float logicalTexHeight)
	{
		Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
		if (!tex.LoadImage(data))
		{
			return tex;
		}
		tex.wrapMode = TextureWrapMode.Clamp;
		if (tex.width <= 8 && tex.height <= 8)
		{
			// The "no art" placeholder.
			tex.filterMode = FilterMode.Point;
			return tex;
		}
		if (tex.mipmapCount <= 1)
		{
			// LoadImage kept no mip chain: rebuild the texture with one.
			Texture2D mipped = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, true);
			mipped.SetPixels32(tex.GetPixels32());
			mipped.Apply(true, true);
			Object.Destroy(tex);
			tex = mipped;
		}
		else
		{
			// Drop the CPU-side copy; only the GPU needs the pixels.
			tex.Apply(false, true);
		}
		tex.wrapMode = TextureWrapMode.Clamp;
		tex.filterMode = FilterMode.Trilinear;
		tex.anisoLevel = 1;
		float pxPerUnit = PixelsPerUnit(owner);
		Texture2D fitted = tex;
		// Halve while the half is still at least as sharp as the screen can show.
		while (fitted.mipmapCount > 1 && fitted.height / 2f >= logicalTexHeight * pxPerUnit && fitted.height / 2 >= 128 && SystemInfo.copyTextureSupport != UnityEngine.Rendering.CopyTextureSupport.None)
		{
			Texture2D half = Half(fitted);
			if (half == null)
			{
				break;
			}
			if (fitted != tex)
			{
				Object.Destroy(fitted);
			}
			fitted = half;
		}
		if (fitted != tex)
		{
			Object.Destroy(tex);
		}
		if (!s_logged)
		{
			s_logged = true;
			Debug.Log($"[Ma9MCardArt] picture {fitted.width}x{fitted.height} mips {fitted.mipmapCount} at {pxPerUnit:F2} px/unit");
		}
		return fitted;
	}

	// Sets a face card's size to the layout's 79x127 after MakePixelPerfect sized it to the texture.
	public static void FitFace(UITexture widget)
	{
		widget.transform.localScale = new Vector3(FaceWidth, FaceHeight, 1f);
	}

	// Sets a detail picture's size: its texture is drawn at DetailHeight units tall.
	public static void FitDetail(UITexture widget)
	{
		Texture tex = widget.mainTexture;
		if (tex == null || tex.height <= DetailHeight * 1.5f)
		{
			return;
		}
		float s = DetailHeight / tex.height;
		widget.transform.localScale = new Vector3(tex.width * s, DetailHeight, 1f);
	}

	private static float PixelsPerUnit(GameObject owner)
	{
		UIRoot root = owner != null ? NGUITools.FindInParents<UIRoot>(owner) : null;
		if (root == null && UIRoot.list.Count > 0)
		{
			root = UIRoot.list[0];
		}
		float adjust = root != null ? root.pixelSizeAdjustment : 480f / Mathf.Max(2, Screen.height);
		return adjust > 0f ? 1f / adjust : 1f;
	}

	// The texture one mip level down, copied on the GPU (the copy has no CPU-side pixels).
	private static Texture2D Half(Texture2D src)
	{
		try
		{
			Texture2D dst = new Texture2D(src.width / 2, src.height / 2, src.format, src.mipmapCount - 1, false);
			dst.filterMode = src.filterMode;
			dst.wrapMode = src.wrapMode;
			dst.anisoLevel = src.anisoLevel;
			for (int mip = 0; mip < dst.mipmapCount; mip++)
			{
				Graphics.CopyTexture(src, 0, mip + 1, dst, 0, mip);
			}
			return dst;
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MCardArt] halving failed: " + ex.Message);
			return null;
		}
	}
}
