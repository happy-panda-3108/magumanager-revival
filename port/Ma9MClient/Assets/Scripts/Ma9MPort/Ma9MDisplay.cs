using UnityEngine;

// Ma9M port: PC display defaults applied once at startup. The game was tuned for 2014 phones
// (30 fps menus, no anti-aliasing); on a PC it can afford sharp edges and a steady 60 fps.
// The development build's error console stays hidden, since the remaining log errors come from
// Android/Facebook stubs that do nothing on Windows.
public static class Ma9MDisplay
{
	public const int FrameRate = 60;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void Apply()
	{
		Debug.developerConsoleEnabled = false;
		Debug.developerConsoleVisible = false;
		QualitySettings.antiAliasing = 8;
		QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
		QualitySettings.globalTextureMipmapLimit = 0;
		QualitySettings.vSyncCount = 0;
		Application.targetFrameRate = FrameRate;
	}
}
