using System;
using UnityEngine;
using UnityEngine.SceneManagement;

// Ma9M port: frame-time watch. Any frame over 50 ms is logged with what was going on (scene,
// broadcast state, log lines and garbage collections during that frame), and a summary goes
// out every 30 s, so stutter can be traced to its cause from the player log.
public class Ma9MPerf : MonoBehaviour
{
	private const float Hitch = 0.05f;

	private int m_logs;

	private int m_gc;

	private int m_frames;

	private int m_hitches;

	private float m_worst;

	private float m_since;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Init()
	{
		GameObject go = new GameObject("Ma9MPerf");
		DontDestroyOnLoad(go);
		go.AddComponent<Ma9MPerf>();
	}

	private void OnEnable()
	{
		Application.logMessageReceived += Count;
		m_gc = GC.CollectionCount(0);
		m_since = Time.realtimeSinceStartup;
	}

	private void OnDisable()
	{
		Application.logMessageReceived -= Count;
	}

	private void Count(string condition, string stackTrace, LogType type)
	{
		if (!condition.StartsWith("[Ma9MPerf]"))
		{
			m_logs++;
		}
	}

	private void Update()
	{
		// Measures the frame that just ended: its log lines and collections were counted during it.
		float dt = Time.unscaledDeltaTime;
		int gc = GC.CollectionCount(0);
		m_frames++;
		if (dt > Hitch && m_frames > 1)
		{
			m_hitches++;
			m_worst = Mathf.Max(m_worst, dt);
			string play = PlayManager.instance != null ? PlayManager.instance.GetState().ToString() : "-";
			Debug.Log($"[Ma9MPerf] hitch {dt * 1000f:F0} ms scene={SceneManager.GetActiveScene().name} play={play} logs={m_logs} gc={gc - m_gc}");
		}
		m_logs = 0;
		m_gc = gc;
		float now = Time.realtimeSinceStartup;
		if (now - m_since >= 30f)
		{
			Debug.Log($"[Ma9MPerf] {m_frames / (now - m_since):F1} fps over {now - m_since:F0} s, {m_hitches} hitches, worst {m_worst * 1000f:F0} ms");
			m_since = now;
			m_frames = 0;
			m_hitches = 0;
			m_worst = 0f;
		}
	}
}
