using System;
using System.Collections;
using System.IO;
using UnityEngine;

// Dev-only: when the player is started with "-ma9mcapture <dir>", save screenshots at fixed
// times and quit, so the port can be checked without anyone watching the window.
// "-ma9mcapturetimes 3,10,20" overrides the default schedule (seconds since start).
public class AutoCapture : MonoBehaviour
{
	private float[] m_times = { 2f, 5f, 10f, 15f, 20f };

	private string m_Dir;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mcapture");
		if (i < 0 || i + 1 >= args.Length)
		{
			return;
		}
		GameObject go = new GameObject("AutoCapture");
		DontDestroyOnLoad(go);
		AutoCapture capture = go.AddComponent<AutoCapture>();
		capture.m_Dir = args[i + 1];
		int t = Array.IndexOf(args, "-ma9mcapturetimes");
		if (t >= 0 && t + 1 < args.Length)
		{
			capture.m_times = Array.ConvertAll(args[t + 1].Split(','), float.Parse);
		}
	}

	private IEnumerator Start()
	{
		Directory.CreateDirectory(m_Dir);
		float start = Time.realtimeSinceStartup;
		foreach (float t in m_times)
		{
			while (Time.realtimeSinceStartup - start < t)
			{
				yield return null;
			}
			string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
			ScreenCapture.CaptureScreenshot(Path.Combine(m_Dir, $"t{t:00}_{scene}.png"));
			Debug.Log($"[AutoCapture] t={t} scene={scene} frame={Time.frameCount}");
		}
		// A DevAutoLogin scene tour decides when to quit.
		if (Array.IndexOf(Environment.GetCommandLineArgs(), "-ma9mtour") < 0)
		{
			yield return new WaitForSecondsRealtime(1f);
			Application.Quit();
		}
	}
}
