using System;
using System.IO;
using UnityEngine;

// Dev (engine session, 2026-11-11): screenshots and animation in a windowless (-batchmode) client. There nothing is drawn
// to a screen, so ScreenCapture saves black frames and every Animation set to cull by its renderers stops (the glove of a
// diving catch stayed at 0.69 m all play long). With "-ma9mscriptshots" in -batchmode this keeps every figure animating
// and Save draws the cameras into a texture (as Ma9MRemote's shot does); in a window Save is ScreenCapture as before.
public class Ma9MBatchShot : MonoBehaviour
{
	private float m_next;

	public static bool On { get; private set; }

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		if (!Application.isBatchMode || Array.IndexOf(Environment.GetCommandLineArgs(), "-ma9mscriptshots") < 0) return;
		On = true;
		GameObject go = new GameObject("Ma9MBatchShot");
		DontDestroyOnLoad(go);
		go.AddComponent<Ma9MBatchShot>();
		Debug.Log("[Ma9MBatchShot] windowless shots on: figures always animate, shots drawn from the cameras");
	}

	private void Update()
	{
		if (Time.realtimeSinceStartup < m_next) return;
		m_next = Time.realtimeSinceStartup + 0.5f;
		foreach (Animation a in FindObjectsByType<Animation>(FindObjectsSortMode.None))
		{
			if (a.cullingType != AnimationCullingType.AlwaysAnimate) a.cullingType = AnimationCullingType.AlwaysAnimate;
		}
		foreach (SkinnedMeshRenderer r in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
		{
			if (!r.updateWhenOffscreen) r.updateWhenOffscreen = true;
		}
	}

	public static void Save(string path)
	{
		if (!On)
		{
			ScreenCapture.CaptureScreenshot(path);
			return;
		}
		try
		{
			Texture2D tex = Ma9MRemote.RenderCameras();
			File.WriteAllBytes(path, tex.EncodeToPNG());
			Destroy(tex);
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Ma9MBatchShot] " + ex.Message);
		}
	}
}
