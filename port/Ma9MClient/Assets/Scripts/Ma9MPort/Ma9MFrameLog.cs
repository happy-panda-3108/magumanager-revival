using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// Dev-only (engine session, 2026-11-05; user: "순간적으로 시야가 확 가려졌다가 경기장이 다시 보일때가 있는데 … 너무 빨리
// 지나가서 실제로 캡쳐하지는 못했는데"): "-ma9mframelog <dir>" keeps, for every frame of the 3D broadcast, a 320x180
// picture and a line of what made it (play and camera states, the camera, the BGM), so a moment that lasts a few frames
// can be found afterwards by comparing each picture with the ones around it.
public class Ma9MFrameLog : MonoBehaviour
{
	private const int Width = 320, Height = 180;

	private string m_dir;

	private StreamWriter m_csv;

	private RenderTexture m_full, m_small;

	private Texture2D m_read;

	private int m_frame;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mframelog");
		if (i < 0 || i + 1 >= args.Length) return;
		GameObject go = new GameObject("Ma9MFrameLog");
		DontDestroyOnLoad(go);
		Ma9MFrameLog log = go.AddComponent<Ma9MFrameLog>();
		log.m_dir = args[i + 1];
		Directory.CreateDirectory(log.m_dir);
		log.m_csv = new StreamWriter(Path.Combine(log.m_dir, "frames.csv"), false, new UTF8Encoding(false));
		log.m_csv.WriteLine("frame,time,scene,play,camera,scripted,x,y,z,pitch,yaw,fov,bgm,lobby");
		log.StartCoroutine(log.Loop());
	}

	private IEnumerator Loop()
	{
		WaitForEndOfFrame end = new WaitForEndOfFrame();
		while (true)
		{
			yield return end;
			if (SceneManager.GetActiveScene().name != "Scene_BroadCast" || PlayManager.instance == null || Camera.main == null) continue;
			try
			{
				Shoot();
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Ma9MFrameLog] " + ex.Message);
			}
		}
	}

	private void Shoot()
	{
		if (m_full == null || m_full.width != Screen.width || m_full.height != Screen.height)
		{
			if (m_full != null) m_full.Release();
			m_full = new RenderTexture(Screen.width, Screen.height, 0);
			m_small = new RenderTexture(Width, Height, 0);
			m_read = new Texture2D(Width, Height, TextureFormat.RGB24, false);
		}
		ScreenCapture.CaptureScreenshotIntoRenderTexture(m_full);
		Graphics.Blit(m_full, m_small);
		RenderTexture was = RenderTexture.active;
		RenderTexture.active = m_small;
		m_read.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
		RenderTexture.active = was;
		m_frame++;
		File.WriteAllBytes(Path.Combine(m_dir, $"f{m_frame:000000}.jpg"), m_read.EncodeToJPG(80));
		PlayManager pm = PlayManager.instance;
		Transform cam = Camera.main.transform;
		CameraManager cm = pm.GetCameraManager();
		AudioSource bgm = SoundManager.instance.bgmBroadcastAudio, lobby = SoundManager.instance.bgmLobbyAudio;
		string Clip(AudioSource s) => s == null || s.clip == null ? "-" : s.clip.name + (s.isPlaying ? "" : "(stopped)");
		CultureInfo inv = CultureInfo.InvariantCulture;
		m_csv.WriteLine(string.Join(",", m_frame.ToString(inv), Time.realtimeSinceStartup.ToString("F3", inv), DataCenter.instance.GetCurrentSceneNum().ToString(inv),
			pm.GetState().ToString(), cm != null ? cm.GetState().ToString() : "-", Ma9MScriptPlayer.Active ? "1" : "0",
			cam.position.x.ToString("F2", inv), cam.position.y.ToString("F2", inv), cam.position.z.ToString("F2", inv),
			cam.eulerAngles.x.ToString("F1", inv), cam.eulerAngles.y.ToString("F1", inv), Camera.main.fieldOfView.ToString("F1", inv), Clip(bgm), Clip(lobby)));
		if (m_frame % 60 == 0) m_csv.Flush();
	}

	private void OnApplicationQuit()
	{
		if (m_csv != null) m_csv.Flush();
	}
}
