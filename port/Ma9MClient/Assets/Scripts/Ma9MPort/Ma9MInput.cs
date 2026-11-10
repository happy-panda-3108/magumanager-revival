using System.Collections.Generic;
using UnityEngine;

// Ma9M port (engine session, 2026-11-10; user: "ai 테스터 4명 … 그렇게 진행하고 싶어. 각각 다른 구단의 덱을 만들어봤으면 좋겠어"):
// every script reads mouse, key and text input through this class instead of UnityEngine.Input. Normally it passes straight
// through. When the player runs with "-ma9mremote <port>" (Ma9MRemote, AI testers), it answers from the injected state only,
// so several clients can be driven at once without the real mouse, and the real mouse and keyboard do not reach them.
// Down/Up hold for exactly the frame Ma9MRemote set them in (it runs first, DefaultExecutionOrder -32000).
// Never call Ma9MInput from inside this class: each member must reach UnityEngine.Input directly.
public static class Ma9MInput
{
	public static bool Remote;

	private static Vector3 s_pos;

	private static readonly bool[] s_held = new bool[3];

	private static readonly int[] s_downFrame = { -1, -1, -1 };

	private static readonly int[] s_upFrame = { -1, -1, -1 };

	private static float s_scroll;

	private static int s_scrollFrame = -1;

	private static string s_text = "";

	private static int s_textFrame = -1;

	private static readonly Dictionary<KeyCode, int> s_keyDown = new Dictionary<KeyCode, int>();

	private static readonly Dictionary<KeyCode, int> s_keyUp = new Dictionary<KeyCode, int>();

	private static readonly HashSet<KeyCode> s_keyHeld = new HashSet<KeyCode>();

	// --- injection (Ma9MRemote, main thread) ---
	// The Set* calls come from coroutines, which run after every Update of the frame, so they only queue the change;
	// Ma9MRemote.Update (first of all scripts) applies the queue at the start of the next frame, and every reader of that
	// frame sees the same Down/Up (found 11-10: applied straight from the coroutine, NGUI never saw a press).

	private static readonly List<System.Action> s_pending = new List<System.Action>();

	public static void Apply()
	{
		if (s_pending.Count == 0)
		{
			return;
		}
		System.Action[] now = s_pending.ToArray();
		s_pending.Clear();
		foreach (System.Action a in now)
		{
			a();
		}
	}

	public static void SetPos(Vector2 screen)
	{
		s_pending.Add(() => s_pos = new Vector3(screen.x, screen.y, 0f));
	}

	public static void SetButton(int button, bool down)
	{
		s_pending.Add(() => ApplyButton(button, down));
	}

	public static void SetScroll(float delta)
	{
		s_pending.Add(() =>
		{
			s_scroll = delta;
			s_scrollFrame = Time.frameCount;
		});
	}

	public static void SetText(string text)
	{
		s_pending.Add(() =>
		{
			s_text = text ?? "";
			s_textFrame = Time.frameCount;
		});
	}

	public static void SetKey(KeyCode key, bool down)
	{
		s_pending.Add(() => ApplyKey(key, down));
	}

	private static void ApplyButton(int button, bool down)
	{
		if (s_held[button] == down)
		{
			return;
		}
		s_held[button] = down;
		if (down)
		{
			s_downFrame[button] = Time.frameCount;
		}
		else
		{
			s_upFrame[button] = Time.frameCount;
		}
	}

	private static void ApplyKey(KeyCode key, bool down)
	{
		if (down)
		{
			if (s_keyHeld.Add(key))
			{
				s_keyDown[key] = Time.frameCount;
			}
		}
		else if (s_keyHeld.Remove(key))
		{
			s_keyUp[key] = Time.frameCount;
		}
	}

	// --- the UnityEngine.Input members the client uses ---

	public static Vector3 mousePosition => Remote ? s_pos : Input.mousePosition;

	public static bool GetMouseButton(int button) => Remote ? button >= 0 && button < 3 && s_held[button] : Input.GetMouseButton(button);

	public static bool GetMouseButtonDown(int button) => Remote ? button >= 0 && button < 3 && s_downFrame[button] == Time.frameCount : Input.GetMouseButtonDown(button);

	public static bool GetMouseButtonUp(int button) => Remote ? button >= 0 && button < 3 && s_upFrame[button] == Time.frameCount : Input.GetMouseButtonUp(button);

	public static bool GetKey(KeyCode key) => Remote ? s_keyHeld.Contains(key) : Input.GetKey(key);

	public static bool GetKeyDown(KeyCode key) => Remote ? s_keyDown.TryGetValue(key, out int f) && f == Time.frameCount : Input.GetKeyDown(key);

	public static bool GetKeyUp(KeyCode key) => Remote ? s_keyUp.TryGetValue(key, out int f) && f == Time.frameCount : Input.GetKeyUp(key);

	public static bool anyKeyDown
	{
		get
		{
			if (!Remote)
			{
				return Input.anyKeyDown;
			}
			foreach (int f in s_keyDown.Values)
			{
				if (f == Time.frameCount)
				{
					return true;
				}
			}
			for (int i = 0; i < 3; i++)
			{
				if (s_downFrame[i] == Time.frameCount)
				{
					return true;
				}
			}
			return false;
		}
	}

	public static float GetAxis(string axis)
	{
		if (!Remote)
		{
			return Input.GetAxis(axis);
		}
		return axis == "Mouse ScrollWheel" && s_scrollFrame == Time.frameCount ? s_scroll : 0f;
	}

	public static string inputString => Remote ? (s_textFrame == Time.frameCount ? s_text : "") : Input.inputString;

	public static int touchCount => Remote ? 0 : Input.touchCount;

	public static Touch GetTouch(int index) => Input.GetTouch(index);

	public static IMECompositionMode imeCompositionMode
	{
		get => Input.imeCompositionMode;
		set => Input.imeCompositionMode = value;
	}

	public static string compositionString => Remote ? "" : Input.compositionString;

	public static Vector2 compositionCursorPos
	{
		get => Input.compositionCursorPos;
		set => Input.compositionCursorPos = value;
	}

	public static bool backButtonLeavesApp
	{
		get => Input.backButtonLeavesApp;
		set => Input.backButtonLeavesApp = value;
	}
}
