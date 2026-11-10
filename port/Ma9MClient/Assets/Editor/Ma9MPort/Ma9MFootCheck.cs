using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Dev check (batch: -executeMethod Ma9MFootCheck.Run): how steep the foot is (ankle to toe against the ground) in the bind
// 들린 채로 … 기존 고정 타격폼 있는 선수들은 발이 땅에 붙어 있는데, 새로 만들거나 새로운 폼을 만든 선수는 발이 45도로").
public static class Ma9MFootCheck
{
	private static readonly string[] Clips =
	{
		"Assets/AnimationClip/Run_Run.anim", "Assets/AnimationClip/Run_BasicStd.anim", "Assets/AnimationClip/Bat_SwingStd.anim",
		"Assets/Resources/animation/Ma9MField/M_Runner_Run.anim", "Assets/Resources/animation/Ma9MField/M_Fielder_Run.anim",
		"Assets/Resources/animation/Ma9MField/M_Runner_Idle.anim", "Assets/Resources/animation/Ma9MField/M_Fielder_Idle_Outfielder.anim"
	};

	public static void Run()
	{
		GameObject man = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameObject/Fielder.prefab"));
		Dictionary<string, Transform> bone = man.GetComponentsInChildren<Transform>(true).GroupBy(t => t.name).ToDictionary(g => g.Key, g => g.First());
		Debug.Log("[Ma9MFoot] bind pose: " + Pitches(bone));
		List<string> clips = Clips.ToList();
		clips.AddRange(AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets/Resources/animation/unique_batting_stance" }).Select(AssetDatabase.GUIDToAssetPath).Where(p => p.Contains("Bat_SwingStd_M")).Take(3));
		foreach (string path in clips)
		{
			AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
			if (clip == null)
			{
				Debug.Log("[Ma9MFoot] missing " + path);
				continue;
			}
			// The planted foot of each frame (the lower ankle): its pitch, then the median and the spread.
			List<float> planted = new List<float>();
			int frames = Mathf.Max(2, Mathf.RoundToInt(clip.length * 30f));
			for (int i = 0; i < frames; i++)
			{
				clip.SampleAnimation(man, clip.length * i / (frames - 1));
				string side = bone["Bip01 L Foot"].position.y < bone["Bip01 R Foot"].position.y ? "L" : "R";
				planted.Add(Pitch(bone, side));
			}
			planted.Sort();
			Debug.Log($"[Ma9MFoot] {System.IO.Path.GetFileNameWithoutExtension(path)}: planted foot pitch median {F(planted[planted.Count / 2])}, 10% {F(planted[planted.Count / 10])}, 90% {F(planted[planted.Count * 9 / 10])} (n {planted.Count})");
		}
		// The diving catch (the game's own clip): where the glove and the hips are, frame by frame, from his feet at the start.
		AnimationClip dive = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/Def_DivingCatchUpIn.anim");
		if (dive != null)
		{
			Transform root = man.transform;
			for (float t = 0f; t <= Mathf.Min(0.6f, dive.length) + 1e-3f; t += 1f / 30f)
			{
				dive.SampleAnimation(man, t);
				Vector3 g = root.InverseTransformPoint(bone["Bip01 L Hand"].position), h = root.InverseTransformPoint(bone["Bip01 Pelvis"].position);
				float low = Mathf.Min(bone["Bip01 L Toe0"].position.y, bone["Bip01 R Toe0"].position.y, bone["Bip01 Head"].position.y, h.y);
				Debug.Log($"[Ma9MFoot] dive t {t:F3}: glove ({F(g.x)}, {F(g.y)}, {F(g.z)}) hips ({F(h.x)}, {F(h.y)}, {F(h.z)}) lowest {F(low)}");
			}
		}
		Object.DestroyImmediate(man);
	}

	private static string Pitches(Dictionary<string, Transform> bone) => $"L {F(Pitch(bone, "L"))}, R {F(Pitch(bone, "R"))}";

	// Degrees the toe is below the ankle (positive: pointing down, as a foot standing flat on a raised heel).
	private static float Pitch(Dictionary<string, Transform> bone, string side)
	{
		Vector3 v = bone["Bip01 " + side + " Toe0"].position - bone["Bip01 " + side + " Foot"].position;
		return Mathf.Atan2(-v.y, new Vector2(v.x, v.z).magnitude) * Mathf.Rad2Deg;
	}

	private static string F(float v) => v.ToString("F1", CultureInfo.InvariantCulture);
}
