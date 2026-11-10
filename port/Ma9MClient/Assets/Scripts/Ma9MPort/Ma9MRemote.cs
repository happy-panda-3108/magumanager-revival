using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

// Ma9M port (engine session, 2026-11-10): remote control for AI screen testers. "-ma9mremote <port>" makes the player listen
// on 127.0.0.1:<port>, one command per connection (a line of text, answered with one line), and drive itself through
// Ma9MInput instead of the real mouse, so four testers can play at once (user, 11-10 01:5x: "ai 테스터 4명 … 각각 다른 구단의
// 덱"). Coordinates are in a 960-wide view of the game picture with y from the top, as tools\ai_tester\ma9m_eye used.
//   shot <png>                      picture of the game, 960 wide
//   zoom <x> <y> <w> <h> <png>      that part of the view at the game's own resolution
//   click <x> <y> | dbl <x> <y> | hold <x> <y> <ms> | drag <x1> <y1> <x2> <y2> [ms] | wheel <x> <y> <notches>
//   type <text> | key <esc|enter|backspace|tab> | wait <ms> | state
//   any action + " --shot <png> [--wait <ms>]" waits (1500 ms) and takes the picture.
// The sound is off unless "-ma9mremotesound". Dev tool only: without the argument nothing here runs.
[DefaultExecutionOrder(-32000)]
public class Ma9MRemote : MonoBehaviour
{
	private sealed class Job
	{
		public string Line;

		public string Reply;

		public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
	}

	public const float ViewWidth = 960f;

	private static readonly ConcurrentQueue<Job> s_jobs = new ConcurrentQueue<Job>();

	private static bool s_sound;

	private TcpListener m_listener;

	private Job m_current;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void Init()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mremote");
		if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int port))
		{
			return;
		}
		s_sound = Array.IndexOf(args, "-ma9mremotesound") >= 0;
		Ma9MInput.Remote = true;
		GameObject go = new GameObject("Ma9MRemote");
		DontDestroyOnLoad(go);
		go.AddComponent<Ma9MRemote>().Listen(port);
	}

	private void Listen(int port)
	{
		m_listener = new TcpListener(IPAddress.Loopback, port);
		m_listener.Start();
		Thread thread = new Thread(AcceptLoop) { IsBackground = true, Name = "Ma9MRemote" };
		thread.Start();
		Debug.Log($"[Ma9MRemote] listening on 127.0.0.1:{port}, batch={Application.isBatchMode}, screen={Screen.width}x{Screen.height}");
	}

	private void AcceptLoop()
	{
		while (true)
		{
			TcpClient client;
			try
			{
				client = m_listener.AcceptTcpClient();
			}
			catch (Exception)
			{
				return;
			}
			ThreadPool.QueueUserWorkItem(_ => Serve(client));
		}
	}

	private static void Serve(TcpClient client)
	{
		using (client)
		{
			try
			{
				NetworkStream stream = client.GetStream();
				StreamReader reader = new StreamReader(stream, new UTF8Encoding(false));
				string line = reader.ReadLine();
				if (string.IsNullOrWhiteSpace(line))
				{
					return;
				}
				Job job = new Job { Line = line.Trim() };
				s_jobs.Enqueue(job);
				string reply = job.Done.Wait(TimeSpan.FromSeconds(120)) ? job.Reply : "error timeout";
				byte[] bytes = new UTF8Encoding(false).GetBytes((reply ?? "ok") + "\n");
				stream.Write(bytes, 0, bytes.Length);
			}
			catch (Exception e)
			{
				Debug.Log("[Ma9MRemote] " + e.Message);
			}
		}
	}

	private void Update()
	{
		Ma9MInput.Apply();
		if (!s_sound)
		{
			AudioListener.volume = 0f;
		}
		if (m_current == null && s_jobs.TryDequeue(out Job job))
		{
			m_current = job;
			StartCoroutine(Run(job));
		}
	}

	private void OnDestroy()
	{
		m_listener?.Stop();
	}

	private IEnumerator Run(Job job)
	{
		string reply = "ok";
		string shot = null;
		int wait = 1500;
		bool waitGiven = false;
		string line = job.Line;
		// " --shot <png>" and " --wait <ms>" at the end, in either order (found 11-10: "type … --wait 800 --shot x" typed the
		// "--wait 800" too).
		while (true)
		{
			int s = line.LastIndexOf(" --shot ", StringComparison.Ordinal);
			int w = line.LastIndexOf(" --wait ", StringComparison.Ordinal);
			if (s < 0 && w < 0)
			{
				break;
			}
			if (s > w)
			{
				shot = Unquote(line.Substring(s + 8));
				line = line.Substring(0, s).TrimEnd();
			}
			else
			{
				int.TryParse(line.Substring(w + 8).Trim(), out wait);
				waitGiven = true;
				line = line.Substring(0, w).TrimEnd();
			}
		}
		string verb = line.Split(' ')[0].ToLowerInvariant();
		string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
		IEnumerator action = null;
		try
		{
			switch (verb)
			{
			case "click":
				action = Click(P(a, 1), false);
				break;
			case "dbl":
				action = Click(P(a, 1), true);
				break;
			case "hold":
				action = Hold(P(a, 1), N(a, 3, 800));
				break;
			case "drag":
				action = Drag(P(a, 1), P(a, 3), N(a, 5, 400), N(a, 6, 0));
				break;
			case "wheel":
				action = Wheel(P(a, 1), N(a, 3, 1));
				break;
			case "type":
				action = Type(line.Length > 5 ? Unescape(line.Substring(5)) : "");
				break;
			case "key":
				action = Key(a.Length > 1 ? a[1] : "esc");
				break;
			case "wait":
				action = WaitMs(N(a, 1, 1000));
				break;
			case "shot":
				shot = Unquote(line.Length > 5 ? line.Substring(5).Trim() : "shot.png");
				wait = 0;
				break;
			case "zoom":
				break;
			case "state":
				reply = $"ok scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} screen={Screen.width}x{Screen.height} frame={Time.frameCount} batch={Application.isBatchMode}";
				break;
			default:
				reply = "error unknown command: " + verb;
				break;
			}
		}
		catch (Exception e)
		{
			reply = "error " + e.Message;
		}
		if (action != null)
		{
			yield return action;
		}
		if (verb == "zoom" && reply == "ok")
		{
			string path = null;
			Rect r = default(Rect);
			try
			{
				r = new Rect(F(a, 1), F(a, 2), F(a, 3), F(a, 4));
				path = Unquote(string.Join(" ", a, 5, a.Length - 5));
			}
			catch (Exception e)
			{
				reply = "error " + e.Message;
			}
			if (path != null)
			{
				if (waitGiven && wait > 0)
				{
					yield return new WaitForSecondsRealtime(wait / 1000f);
				}
				yield return Capture(path, r, result => reply = result);
			}
		}
		// "--wait" alone waits too (11-10: testers clicked again mid scene change after "click … --wait 2000" answered at once).
		if (shot == null && waitGiven && wait > 0 && verb != "zoom" && reply.StartsWith("ok", StringComparison.Ordinal))
		{
			yield return new WaitForSecondsRealtime(wait / 1000f);
		}
		if (shot != null && reply.StartsWith("ok", StringComparison.Ordinal))
		{
			if (wait > 0)
			{
				yield return new WaitForSecondsRealtime(wait / 1000f);
			}
			yield return Capture(shot, null, result => reply = result);
		}
		job.Reply = reply;
		job.Done.Set();
		m_current = null;
	}

	// --- actions ---

	private static Vector2 Screen2(Vector2 view)
	{
		float scale = Screen.width / ViewWidth;
		return new Vector2(view.x * scale, Screen.height - view.y * scale);
	}

	private IEnumerator Click(Vector2 view, bool twice)
	{
		Vector2 p = Screen2(view);
		Ma9MInput.SetPos(p);
		yield return null;
		for (int i = 0; i < (twice ? 2 : 1); i++)
		{
			Ma9MInput.SetButton(0, true);
			yield return null;
			yield return null;
			Ma9MInput.SetButton(0, false);
			yield return null;
			if (twice && i == 0)
			{
				yield return new WaitForSecondsRealtime(0.08f);
			}
		}
	}

	private IEnumerator Hold(Vector2 view, int ms)
	{
		Ma9MInput.SetPos(Screen2(view));
		yield return null;
		Ma9MInput.SetButton(0, true);
		yield return new WaitForSecondsRealtime(ms / 1000f);
		Ma9MInput.SetButton(0, false);
		yield return null;
	}

	// hold > 0: press and keep still that long first (a long press that then drags, e.g. the roster's batting order).
	private IEnumerator Drag(Vector2 from, Vector2 to, int ms, int hold)
	{
		Vector2 a = Screen2(from), b = Screen2(to);
		Ma9MInput.SetPos(a);
		yield return null;
		Ma9MInput.SetButton(0, true);
		yield return null;
		if (hold > 0)
		{
			yield return new WaitForSecondsRealtime(hold / 1000f);
		}
		float start = Time.realtimeSinceStartup, length = Mathf.Max(0.05f, ms / 1000f);
		while (Time.realtimeSinceStartup - start < length)
		{
			Ma9MInput.SetPos(Vector2.Lerp(a, b, (Time.realtimeSinceStartup - start) / length));
			yield return null;
		}
		Ma9MInput.SetPos(b);
		yield return null;
		yield return null;
		Ma9MInput.SetButton(0, false);
		yield return null;
	}

	private IEnumerator Wheel(Vector2 view, int notches)
	{
		Ma9MInput.SetPos(Screen2(view));
		yield return null;
		int n = Mathf.Abs(notches);
		for (int i = 0; i < n; i++)
		{
			Ma9MInput.SetScroll(notches > 0 ? 0.1f : -0.1f);
			yield return null;
			yield return null;
		}
	}

	private IEnumerator Type(string text)
	{
		foreach (char c in text)
		{
			Ma9MInput.SetText(c.ToString());
			yield return null;
			yield return null;
		}
	}

	private IEnumerator Key(string name)
	{
		KeyCode key;
		switch (name.ToLowerInvariant())
		{
		case "esc":
		case "escape":
		case "back":
			key = KeyCode.Escape;
			break;
		case "enter":
		case "return":
			key = KeyCode.Return;
			break;
		case "backspace":
			key = KeyCode.Backspace;
			break;
		case "tab":
			key = KeyCode.Tab;
			break;
		default:
			key = (KeyCode)Enum.Parse(typeof(KeyCode), name, true);
			break;
		}
		Ma9MInput.SetKey(key, true);
		if (key == KeyCode.Return)
		{
			Ma9MInput.SetText("\n");
		}
		else if (key == KeyCode.Backspace)
		{
			Ma9MInput.SetText("\b");
		}
		yield return null;
		yield return null;
		Ma9MInput.SetKey(key, false);
		yield return null;
	}

	private static IEnumerator WaitMs(int ms)
	{
		yield return new WaitForSecondsRealtime(ms / 1000f);
	}

	// --- pictures ---

	// The whole view at 960 wide, or (zoom) a view rectangle at the game's own pixels. Batch mode has no back buffer, so the
	// cameras are rendered into a texture there.
	private IEnumerator Capture(string path, Rect? zoom, Action<string> done)
	{
		yield return new WaitForEndOfFrame();
		Texture2D full = null;
		string result;
		try
		{
			full = Application.isBatchMode ? RenderCameras() : ScreenCapture.CaptureScreenshotAsTexture();
			Texture2D picture;
			if (zoom.HasValue)
			{
				float scale = full.width / ViewWidth;
				Rect z = zoom.Value;
				int x = Mathf.Clamp(Mathf.RoundToInt(z.x * scale), 0, full.width - 1);
				int w = Mathf.Clamp(Mathf.RoundToInt(z.width * scale), 1, full.width - x);
				int h = Mathf.Clamp(Mathf.RoundToInt(z.height * scale), 1, full.height);
				int yTop = Mathf.RoundToInt(z.y * scale);
				int y = Mathf.Clamp(full.height - yTop - h, 0, full.height - h);
				picture = new Texture2D(w, h, TextureFormat.RGB24, false);
				picture.SetPixels(full.GetPixels(x, y, w, h));
				picture.Apply();
			}
			else
			{
				picture = Scale(full, Mathf.RoundToInt(ViewWidth), Mathf.RoundToInt(full.height * ViewWidth / full.width));
			}
			string dir = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(dir))
			{
				Directory.CreateDirectory(dir);
			}
			File.WriteAllBytes(path, picture.EncodeToPNG());
			result = $"ok {picture.width}x{picture.height} scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} {path}";
			if (picture != full)
			{
				Destroy(picture);
			}
		}
		catch (Exception e)
		{
			result = "error capture " + e.Message;
		}
		if (full != null)
		{
			Destroy(full);
		}
		done(result);
	}

	private static Texture2D RenderCameras()
	{
		int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
		RenderTexture rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
		RenderTexture old = RenderTexture.active;
		RenderTexture.active = rt;
		GL.Clear(true, true, Color.black);
		Camera[] cameras = Camera.allCameras;
		Array.Sort(cameras, (p, q) => p.depth.CompareTo(q.depth));
		foreach (Camera cam in cameras)
		{
			RenderTexture before = cam.targetTexture;
			cam.targetTexture = rt;
			cam.Render();
			cam.targetTexture = before;
		}
		RenderTexture.active = rt;
		Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
		tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
		tex.Apply();
		RenderTexture.active = old;
		RenderTexture.ReleaseTemporary(rt);
		return tex;
	}

	private static Texture2D Scale(Texture2D src, int w, int h)
	{
		RenderTexture old = RenderTexture.active;
		RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
		Graphics.Blit(src, rt);
		RenderTexture.active = rt;
		Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
		tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
		tex.Apply();
		RenderTexture.active = old;
		RenderTexture.ReleaseTemporary(rt);
		return tex;
	}

	// --- parsing ---

	private static Vector2 P(string[] a, int i) => new Vector2(F(a, i), F(a, i + 1));

	private static float F(string[] a, int i) => float.Parse(a[i], CultureInfo.InvariantCulture);

	private static int N(string[] a, int i, int fallback) => a.Length > i && int.TryParse(a[i], out int v) ? v : fallback;

	private static string Unquote(string s)
	{
		s = s.Trim();
		return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
	}

	// "type" text: \n, \b, \t and \\ escapes (literal escapes are written by the tool, see feedback on escapes).
	private static string Unescape(string s)
	{
		StringBuilder b = new StringBuilder();
		for (int i = 0; i < s.Length; i++)
		{
			if (s[i] == '\\' && i + 1 < s.Length)
			{
				char n = s[++i];
				b.Append(n == 'n' ? '\n' : n == 'b' ? '\b' : n == 't' ? '\t' : n);
			}
			else
			{
				b.Append(s[i]);
			}
		}
		return b.ToString();
	}
}
