using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

// Ma9M port (engine session, 2026-11-01; user: "송구동작, 공의 움직임, 주자 및 수비수의 움직임이 엄청 자연스럽다고 느껴지지는
// Editor/Ma9MPort/Ma9MFieldMotions (Resources/Animation/Ma9MField/M_*.anim), and what the script player needs to time
// them (Resources/Ma9MFieldMotions.txt): a cycle's speed over the ground at speed 1, where the glove and the throwing
// hand are at a clip's moments (from his feet, +z the way he faces), and for the clips that step and turn on their own
// their track. The clips are lent to a man's Animation the first time he needs one. "-ma9moldmotions" keeps the game's
// own clips (to compare).
public static class Ma9MMotions
{
	public sealed class Info
	{
		public string Name, Mode;
		public float Length, Speed, Turn, StartYaw, Air;
		// The glove at the first frame; its lowest, highest and furthest out (time, position).
		public Vector3 GloveStart, GloveLow, GloveHigh, GloveOut, Release;
		public float GloveLowT, GloveHighT, GloveOutT, ReleaseT;
		// track: per frame his spot (x, z) and the way he faces (yaw, deg) from his first frame's spot, in the clip's frame.
		public float Rate;
		public Vector3[] Track;

		public void TrackAt(float t, out Vector2 at, out float yaw)
		{
			at = Vector2.zero;
			yaw = 0f;
			if (Track == null || Track.Length == 0) return;
			float f = Mathf.Clamp(t * Rate, 0f, Track.Length - 1);
			int i0 = Mathf.FloorToInt(f), i1 = Mathf.Min(Track.Length - 1, i0 + 1);
			Vector3 v = Vector3.Lerp(Track[i0], Track[i1], f - i0);
			at = new Vector2(v.x, v.y);
			yaw = v.z;
		}
	}

	public static readonly bool Off = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-ma9moldmotions") >= 0;

	private static Dictionary<string, Info> s_info;
	private static readonly Dictionary<string, AnimationClip> s_clips = new Dictionary<string, AnimationClip>();

	public static Info Get(string name)
	{
		if (Off) return null;
		if (s_info == null) Load();
		return s_info.TryGetValue(name, out Info i) ? i : null;
	}

	// Lends the clip to this man's Animation (once); false when it is not there.
	public static bool Ensure(Animation an, string name)
	{
		if (an == null || Off) return false;
		if (an[name] != null) return true;
		if (!s_clips.TryGetValue(name, out AnimationClip clip))
		{
			clip = Resources.Load<AnimationClip>("Animation/Ma9MField/" + name);
			s_clips[name] = clip;
		}
		if (clip == null) return false;
		an.AddClip(clip, name);
		return true;
	}

	private static void Load()
	{
		s_info = new Dictionary<string, Info>();
		TextAsset ta = Resources.Load<TextAsset>("Ma9MFieldMotions");
		if (ta == null) return;
		foreach (string raw in ta.text.Split('\n'))
		{
			string line = raw.Trim();
			if (line.Length == 0 || line[0] == '#') continue;
			string[] f = line.Split(' ');
			if (f[0] == "track" && f.Length > 4 && s_info.TryGetValue(f[1], out Info ti))
			{
				int n = int.Parse(f[2], CultureInfo.InvariantCulture);
				ti.Rate = float.Parse(f[3], CultureInfo.InvariantCulture);
				ti.Track = new Vector3[n];
				for (int k = 0; k < n && 4 + 3 * k + 2 < f.Length; k++)
				{
					ti.Track[k] = new Vector3(F(f[4 + 3 * k]), F(f[5 + 3 * k]), F(f[6 + 3 * k]));
				}
				continue;
			}
			Info info = new Info { Name = f[0] };
			for (int k = 1; k < f.Length; k++)
			{
				int eq = f[k].IndexOf('=');
				if (eq < 0) continue;
				string key = f[k].Substring(0, eq), val = f[k].Substring(eq + 1);
				switch (key)
				{
					case "len": info.Length = F(val); break;
					case "mode": info.Mode = val; break;
					case "speed": info.Speed = F(val); break;
					case "turn": info.Turn = F(val); break;
					case "start": info.StartYaw = F(val); break;
					case "air": info.Air = F(val); break;
					case "gstart": Point(val, out _, out info.GloveStart); break;
					case "glow": Point(val, out info.GloveLowT, out info.GloveLow); break;
					case "ghigh": Point(val, out info.GloveHighT, out info.GloveHigh); break;
					case "gout": Point(val, out info.GloveOutT, out info.GloveOut); break;
					case "release": Point(val, out info.ReleaseT, out info.Release); break;
				}
			}
			s_info[info.Name] = info;
		}
	}

	private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

	private static void Point(string s, out float t, out Vector3 p)
	{
		string[] v = s.Split(',');
		t = F(v[0]);
		p = new Vector3(F(v[1]), F(v[2]), F(v[3]));
	}
}
