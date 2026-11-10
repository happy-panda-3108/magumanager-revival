using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Ma9M port (engine session, 2026-11-01; user: "3D모델링이랑 게임 시뮬레이션을 조금 더 부드럽고 자연스럽게 … 송구동작, 공의
// fielder and runner motions (humanoid muscle clips in resourcesassetbundle_character_animator, decoded outside Unity by
// Ma9MBattingForms does the batting stances: a humanoid avatar for our skeleton (fielder, runner and batter share it),
// every frame posed with HumanPoseHandler and baked. Each clip is turned so +z is the way he faces at one of its moments
// (align=: its first frame by default; "release", the way the throwing hand goes as the ball leaves it; or a time in
// seconds), stood on the ground at its first frame (or set as another clip it goes on from: ref=), and its travel over
// the ground taken out one of three ways:
//   loop    a cycle (a run, a stance): drift and sway about its middle taken out, so it repeats in place;
//   place   the script moves him (a dive, a slide): the hips kept over the spot, up and down left as they are;
//   track   his own steps and turns (a crow hop and the turn to the target in a throw): taken out of the clip, which
//           plays in place facing +z, and written to the info file per frame (x z yaw) for the player to move and turn
//           him along (scaling the turn to the one he has to make).
// As the game's own clips do, Bip01 carries him and the pelvis keeps the skeleton's place under it, so the two kinds
// blend. It also measures what the player needs to time the clips: a cycle's ground speed at speed 1 (from the planted
// foot), the travel, and the moments of a catch (the glove at its lowest or furthest out) and a throw (the throwing hand
// at its fastest) -> one line per clip in the info file.
//
// Unity.exe -batchmode -quit -projectPath <p> -executeMethod Ma9MFieldMotions.Run
//   -ma9mfmsrc <dir of <clip>.txt>  -ma9mfmlist <file: "<magu clip> <our clip> <loop|place|track> [align=...] [ref=<our clip>]">
//   [-ma9mfmout <Assets folder>] [-ma9mfminfo <file>] [-ma9mfmshots <dir>]
public static class Ma9MFieldMotions
{
	private static readonly string[] Bones =
	{
		"Bip01", "Bip01 Pelvis", "Bip01 Spine", "Bip01 Spine1", "Bip01 Neck", "Bip01 Head",
		"Bip01 L Clavicle", "Bip01 L UpperArm", "Bip01 L Forearm", "Bip01 L Hand",
		"Bip01 R Clavicle", "Bip01 R UpperArm", "Bip01 R Forearm", "Bip01 R Hand",
		"Bip01 L Thigh", "Bip01 L Calf", "Bip01 L Foot", "Bip01 L Toe0",
		"Bip01 R Thigh", "Bip01 R Calf", "Bip01 R Foot", "Bip01 R Toe0"
	};

	private static readonly Dictionary<string, string> Human = new Dictionary<string, string>
	{
		{ "Hips", "Bip01 Pelvis" }, { "Spine", "Bip01 Spine" }, { "Chest", "Bip01 Spine1" }, { "Neck", "Bip01 Neck" }, { "Head", "Bip01 Head" },
		{ "LeftShoulder", "Bip01 L Clavicle" }, { "LeftUpperArm", "Bip01 L UpperArm" }, { "LeftLowerArm", "Bip01 L Forearm" }, { "LeftHand", "Bip01 L Hand" },
		{ "RightShoulder", "Bip01 R Clavicle" }, { "RightUpperArm", "Bip01 R UpperArm" }, { "RightLowerArm", "Bip01 R Forearm" }, { "RightHand", "Bip01 R Hand" },
		{ "LeftUpperLeg", "Bip01 L Thigh" }, { "LeftLowerLeg", "Bip01 L Calf" }, { "LeftFoot", "Bip01 L Foot" }, { "LeftToes", "Bip01 L Toe0" },
		{ "RightUpperLeg", "Bip01 R Thigh" }, { "RightLowerLeg", "Bip01 R Calf" }, { "RightFoot", "Bip01 R Foot" }, { "RightToes", "Bip01 R Toe0" }
	};

	private const string Glove = "Bip01 L Hand", Throwing = "Bip01 R Hand";

	private static GameObject s_man;
	private static Dictionary<string, Transform> s_bone;
	private static HumanPoseHandler s_handler;
	private static float s_refFloor, s_tall;
	private static Vector3 s_pelvisPos;        // the pelvis under Bip01 in the game's clips
	private static Quaternion s_pelvisRot;

	private static string Arg(string name, string def = null)
	{
		string[] a = System.Environment.GetCommandLineArgs();
		int i = System.Array.IndexOf(a, name);
		return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
	}

	private static float[][] Load(string file, out float rate)
	{
		string[] lines = File.ReadAllLines(file);
		rate = float.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture);
		return lines.Skip(1).Where(l => l.Length > 0).Select(l => l.Split(' ').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray()).ToArray();
	}

	private static float V(float[] row, int a) => row[a - 7];

	private sealed class Pose
	{
		public Vector3[] Pos = new Vector3[Bones.Length];
		public Quaternion[] Rot = new Quaternion[Bones.Length];
		public Vector3[] World = new Vector3[Bones.Length];
		public Vector3 HipsPos;
		public Quaternion HipsRot;
	}

	// A clip's frame: turned by Turn about the origin, then moved by Move.
	private struct Frame
	{
		public Quaternion Turn;
		public Vector3 Move;
	}

	public static void Run()
	{
		string src = Arg("-ma9mfmsrc");
		string list = Arg("-ma9mfmlist");
		string outDir = Arg("-ma9mfmout", "Assets/Resources/Animation/Ma9MField");
		string info = Arg("-ma9mfminfo");
		string shots = Arg("-ma9mfmshots");
		EditorSceneManager.NewScene(NewSceneSetup.EmptyScene);
		s_man = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameObject/Fielder.prefab"));
		s_man.name = "Fielder";
		s_man.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
		foreach (MonoBehaviour mb in s_man.GetComponentsInChildren<MonoBehaviour>()) mb.enabled = false;
		foreach (Renderer r in s_man.GetComponentsInChildren<Renderer>()) if (!(r is SkinnedMeshRenderer)) r.enabled = false;
		SkinnedMeshRenderer smr = s_man.GetComponentInChildren<SkinnedMeshRenderer>();
		smr.sharedMesh = FielderMesh();
		smr.sharedMaterials = new[] { new Material(Shader.Find("Standard")) { color = new Color(0.85f, 0.85f, 0.85f) }, new Material(Shader.Find("Standard")) { color = new Color(0.15f, 0.25f, 0.7f) } };
		smr.updateWhenOffscreen = true;
		smr.forceMatrixRecalculationPerRender = true;
		s_bone = s_man.GetComponentsInChildren<Transform>(true).Where(t => Bones.Contains(t.name)).ToDictionary(t => t.name);
		// The ground under the game's own fielder: his feet in the original ready stance.
		AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/Def_InfielderStd.anim").SampleAnimation(s_man, 0f);
		s_refFloor = Floor();
		s_pelvisPos = s_bone["Bip01 Pelvis"].localPosition;
		s_pelvisRot = s_bone["Bip01 Pelvis"].localRotation;
		SpineLog("original stance");
		s_tall = 1.3f * (s_bone["Bip01 Head"].position.y - s_refFloor);
		Avatar avatar = BuildAvatar(smr);
		float humanScale;
		{
			Animator an = s_man.AddComponent<Animator>();
			an.avatar = avatar;
			an.Rebind();
			humanScale = an.humanScale;
			Object.DestroyImmediate(an);
		}
		Debug.Log($"[Ma9MField] avatar valid {avatar.isValid} human {avatar.isHuman} scale {humanScale:F3} floor {s_refFloor:F3}");
		s_handler = new HumanPoseHandler(avatar, s_man.transform);
		Directory.CreateDirectory(outDir);
		List<string> infoLines = new List<string>
		{
			"# clip key=value: len (s), mode, speed (m/s over the ground at 1x), travel (x,z m) and turn (deg) over the clip, start (his facing at",
			"#   frame 0, deg), then where the glove (L hand) and the throwing hand (R hand) are from his feet, as the player stands him",
			"#   (t,x,y,z): gstart, glow (lowest), ghigh (highest), gout (furthest from the hips), release (the throwing hand at its fastest),",
			"#   hipslow (t,y), air (s with both feet off the ground)",
			"# track clip frames rate (x z yaw)*frames"
		};
		Dictionary<string, Frame> frames = new Dictionary<string, Frame>();
		foreach (string line in File.ReadAllLines(list))
		{
			string[] f = line.Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
			if (f.Length < 3 || f[0].StartsWith("#")) continue;
			Dictionary<string, string> opt = f.Skip(3).Where(x => x.Contains('=')).ToDictionary(x => x.Split('=')[0], x => x.Split('=')[1]);
			string file = Path.Combine(src, f[0] + ".txt");
			if (!File.Exists(file)) { Debug.LogWarning("[Ma9MField] missing " + file); continue; }
			float[][] rows = Load(file, out float rate);
			string mode = f[2];
			Frame fr = opt.TryGetValue("ref", out string refClip) && frames.TryGetValue(refClip, out Frame before) ? before : Align(rows, rate, opt.TryGetValue("align", out string how) ? how : "0");
			frames[f[1]] = fr;
			List<Pose> poses = rows.Select(r => { SetPose(r, fr); return Take(); }).ToList();
			int pelvis = System.Array.IndexOf(Bones, "Bip01 Pelvis");
			Vector3 start = poses[0].World[pelvis], end = poses[poses.Count - 1].World[pelvis];
			float speed = GroundSpeed(poses, rate);
			int release = Release(poses, rate);
			Vector3 travel = end - start;
			float turn = Mathf.DeltaAngle(0f, Yaw(poses[poses.Count - 1]) - Yaw(poses[0])), startYaw = Yaw(poses[0]);
			string track = null;
			List<Vector3> moves = null;
			if (mode == "loop")
			{
				// About its middle, the drift from the first frame to the last taken out.
				Vector3 mean = Vector3.zero;
				foreach (Pose p in poses) mean += p.World[pelvis];
				mean /= poses.Count;
				for (int i = 0; i < poses.Count; i++)
				{
					float u = poses.Count > 1 ? i / (float)(poses.Count - 1) : 0f;
					Vector3 d = mean + (end - start) * (u - 0.5f);
					Shift(poses[i], new Vector3(-d.x, 0f, -d.z));
				}
			}
			else if (mode == "place")
			{
				for (int i = 0; i < poses.Count; i++)
				{
					Vector3 d = poses[i].World[pelvis] - start;
					Shift(poses[i], new Vector3(-d.x, 0f, -d.z));
				}
			}
			else if (mode == "track")
			{
				string F(float v, string fmt) => v.ToString(fmt, CultureInfo.InvariantCulture);
				moves = Extract(poses);
				track = $"track {f[1]} {poses.Count} {F(rate, "F0")} " + string.Join(" ", moves.Select(v => $"{F(v.x, "F3")} {F(v.y, "F3")} {F(v.z, "F1")}"));
			}
			string summary = Info(f[1], poses, rate, mode, speed, travel, turn, startYaw, release);
			foreach (Pose p in poses) Rebase(p);
			AnimationClip clip = Write(poses, rate, mode == "loop");
			clip.name = f[1];
			AssetDatabase.CreateAsset(clip, outDir + "/" + f[1] + ".anim");
			infoLines.Add(summary);
			if (track != null) infoLines.Add(track);
			Debug.Log("[Ma9MField] " + summary);
			if (shots != null) Sheet(shots, f[1], poses, moves);
		}
		AssetDatabase.SaveAssets();
		if (info != null) File.WriteAllLines(info, infoLines);
		// Dev: the game's own clips in the same pictures, to compare (-ma9mfmorig Def_InfielderThrow,Run_Run,…).
		string orig = Arg("-ma9mfmorig");
		if (orig != null && shots != null)
		{
			foreach (string name in orig.Split(','))
			{
				AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/" + name + ".anim");
				if (clip == null) { Debug.LogWarning("[Ma9MField] no clip " + name); continue; }
				List<Pose> poses = new List<Pose>();
				int n = int.Parse(Arg("-ma9mfmcols", "8"));
				for (int i = 0; i < n; i++)
				{
					clip.SampleAnimation(s_man, clip.length * i / (n - 1f));
					poses.Add(Take());
				}
				Sheet(shots, "orig_" + name, poses);
			}
		}
		Object.DestroyImmediate(s_man);
	}

	// Pose him from one frame of humanoid values, then turn and move him into the clip's frame.
	private static void SetPose(float[] r, Frame fr)
	{
		HumanPose pose = new HumanPose { muscles = new float[HumanTrait.MuscleCount] };
		pose.bodyPosition = new Vector3(V(r, 7), V(r, 8), V(r, 9));
		pose.bodyRotation = new Quaternion(V(r, 10), V(r, 11), V(r, 12), V(r, 13));
		for (int m = 0; m < HumanTrait.MuscleCount; m++) pose.muscles[m] = V(r, 42 + m);
		s_handler.SetHumanPose(ref pose);
		Transform pelvis = s_bone["Bip01 Pelvis"];
		pelvis.SetPositionAndRotation(fr.Turn * pelvis.position + fr.Move, fr.Turn * pelvis.rotation);
	}

	// The clip's frame: +z the way he faces at a moment of it (align: a time in seconds, or "release", the way the
	// throwing hand goes as the ball leaves it, at its fastest), his hips over the origin and his feet on the ground at
	// its first frame.
	private static Frame Align(float[][] rows, float rate, string how)
	{
		Frame id = new Frame { Turn = Quaternion.identity, Move = Vector3.zero };
		float yaw;
		if (how == "release")
		{
			List<Vector3> hand = rows.Select(r => { SetPose(r, id); return s_bone[Throwing].position; }).ToList();
			int best = 1;
			float fast = 0f;
			for (int i = 1; i < hand.Count; i++)
			{
				float v = (hand[i] - hand[i - 1]).magnitude;
				if (v > fast) { fast = v; best = i; }
			}
			Vector3 d = hand[Mathf.Min(hand.Count - 1, best + 1)] - hand[Mathf.Max(0, best - 2)];
			yaw = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
		}
		else
		{
			int k = Mathf.Clamp(Mathf.RoundToInt(float.Parse(how, CultureInfo.InvariantCulture) * rate), 0, rows.Length - 1);
			SetPose(rows[k], id);
			yaw = Stance().Item2;
		}
		Quaternion turn = Quaternion.Euler(0f, -yaw, 0f);
		SetPose(rows[0], id);
		Vector3 at = turn * s_bone["Bip01 Pelvis"].position;
		Frame fr = new Frame { Turn = turn, Move = new Vector3(-at.x, 0f, -at.z) };
		SetPose(rows[0], fr);
		fr.Move.y = s_refFloor - Floor();
		return fr;
	}

	// Moves him by d over the ground.
	private static void Shift(Pose p, Vector3 d)
	{
		p.HipsPos += d;
		for (int b = 0; b < Bones.Length; b++) if (b != 0) p.World[b] += d;
	}

	// His travel and turn over the ground (the hips' spot and the way they face, the turn smoothed over five frames),
	// taken out of the clip, which then plays over the origin facing as he does at the moment it is aligned on: per frame
	// (x, z, yaw in degrees) from his first frame's spot and that facing.
	private static List<Vector3> Extract(List<Pose> poses)
	{
		int pelvis = System.Array.IndexOf(Bones, "Bip01 Pelvis");
		float[] yaw = new float[poses.Count];
		yaw[0] = Yaw(poses[0]);
		for (int i = 1; i < poses.Count; i++) yaw[i] = yaw[i - 1] + Mathf.DeltaAngle(yaw[i - 1], Yaw(poses[i]));
		float[] smooth = new float[poses.Count];
		for (int i = 0; i < poses.Count; i++)
		{
			int a = Mathf.Max(0, i - 2), b = Mathf.Min(poses.Count - 1, i + 2);
			float sum = 0f;
			for (int j = a; j <= b; j++) sum += yaw[j];
			smooth[i] = sum / (b - a + 1);
		}
		Vector3 origin = poses[0].World[pelvis];
		List<Vector3> track = new List<Vector3>();
		for (int i = 0; i < poses.Count; i++)
		{
			Vector3 at = new Vector3(poses[i].World[pelvis].x - origin.x, 0f, poses[i].World[pelvis].z - origin.z);
			track.Add(new Vector3(at.x, at.z, smooth[i]));
			Quaternion back = Quaternion.Euler(0f, -smooth[i], 0f);
			Vector3 lift = new Vector3(origin.x, 0f, origin.z) + at;
			poses[i].HipsPos = back * (poses[i].HipsPos - lift);
			poses[i].HipsRot = back * poses[i].HipsRot;
			for (int b = 0; b < Bones.Length; b++) poses[i].World[b] = back * (poses[i].World[b] - lift);
		}
		return track;
	}

	// As the game's clips have it: Bip01 carries him, the pelvis keeps its place under it.
	private static void Rebase(Pose p)
	{
		Quaternion bip = p.HipsRot * Quaternion.Inverse(s_pelvisRot);
		p.Rot[0] = bip;
		p.Pos[0] = p.HipsPos - bip * s_pelvisPos;
		p.Rot[1] = s_pelvisRot;
		p.Pos[1] = s_pelvisPos;
	}

	// Where he stands and faces: the hips, and the yaw of the way they face.
	private static (Vector3, float) Stance()
	{
		Vector3 lateral = Vector3.ProjectOnPlane(s_bone["Bip01 L Thigh"].position - s_bone["Bip01 R Thigh"].position, Vector3.up);
		Vector3 fwd = Vector3.Cross(Vector3.up, lateral);
		return (s_bone["Bip01 Pelvis"].position, Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg);
	}

	private static float Floor() => Mathf.Min(s_bone["Bip01 L Toe0"].position.y, s_bone["Bip01 R Toe0"].position.y, s_bone["Bip01 L Foot"].position.y, s_bone["Bip01 R Foot"].position.y);

	private static Pose Take()
	{
		Pose p = new Pose();
		for (int b = 0; b < Bones.Length; b++)
		{
			Transform t = s_bone[Bones[b]];
			p.Pos[b] = t.localPosition;
			p.Rot[b] = t.localRotation;
			p.World[b] = t.position;
		}
		p.HipsPos = s_bone["Bip01 Pelvis"].position;
		p.HipsRot = s_bone["Bip01 Pelvis"].rotation;
		return p;
	}

	// Where a foot meets the ground: the lower of its ankle and its toe (a runner is on his toes).
	private static int Sole(Pose p, string side)
	{
		int foot = System.Array.IndexOf(Bones, "Bip01 " + side + " Foot"), toe = System.Array.IndexOf(Bones, "Bip01 " + side + " Toe0");
		return p.World[foot].y < p.World[toe].y ? foot : toe;
	}

	private static float SoleY(Pose p) => Mathf.Min(p.World[Sole(p, "L")].y, p.World[Sole(p, "R")].y);

	// A cycle's speed over the ground at playback speed 1: how fast the planted foot (the lower ankle, within 4 cm of the
	// clip's lowest, the same foot in two frames running) goes back under his hips. By the ankle, not the lower of ankle
	// and toe: with the feet at their true pitch (2026-11-02) the toe is lowest only as he pushes off, and the runs read
	// 0 (4 cm) or 7.0 m/s (9 cm). The ankle reads them 8% low (5.44): the shipped Ma9MFieldMotions.txt keeps the speeds
	// of the 11-01 bake (flat feet, checked on screen; the legs did not change), copied over after the 11-02 re-bake.
	private static float GroundSpeed(List<Pose> poses, float rate)
	{
		int pel = System.Array.IndexOf(Bones, "Bip01 Pelvis");
		int Ankle(string side) => System.Array.IndexOf(Bones, "Bip01 " + side + " Foot");
		float low = poses.Min(p => Mathf.Min(p.World[Ankle("L")].y, p.World[Ankle("R")].y));
		float sum = 0f;
		int n = 0;
		for (int i = 0; i + 1 < poses.Count; i++)
		{
			string side = poses[i].World[Ankle("L")].y < poses[i].World[Ankle("R")].y ? "L" : "R";
			int a = Ankle(side), b = a;
			if (poses[i].World[a].y > low + 0.04f || poses[i + 1].World[b].y > low + 0.04f) continue;
			string other = side == "L" ? "R" : "L";
			if (poses[i + 1].World[Ankle(other)].y < poses[i + 1].World[b].y) continue;
			float foot = poses[i + 1].World[b].z - poses[i].World[a].z, hips = poses[i + 1].World[pel].z - poses[i].World[pel].z;
			sum += (hips - foot) * rate;
			n++;
		}
		return n > 0 ? sum / n : 0f;
	}

	// The frame the throwing hand is at its fastest (the ball leaves it).
	private static int Release(List<Pose> poses, float rate)
	{
		int h = System.Array.IndexOf(Bones, Throwing), rel = 0;
		float fast = 0f;
		for (int i = 1; i < poses.Count; i++)
		{
			float v = (poses[i].World[h] - poses[i - 1].World[h]).magnitude * rate;
			if (v > fast) { fast = v; rel = i; }
		}
		return rel;
	}

	// One line of key=value: what the player needs of the clip (positions from his feet, as the player stands him: the
	// poses here are after the clip's travel is taken out).
	private static string Info(string name, List<Pose> poses, float rate, string mode, float speed, Vector3 travel, float turn, float startYaw, int release)
	{
		int g = System.Array.IndexOf(Bones, Glove), h = System.Array.IndexOf(Bones, Throwing), pel = System.Array.IndexOf(Bones, "Bip01 Pelvis");
		int low = 0, high = 0, outIdx = 0, hipsLow = 0;
		float best = float.MinValue;
		for (int i = 0; i < poses.Count; i++)
		{
			if (poses[i].World[g].y < poses[low].World[g].y) low = i;
			if (poses[i].World[g].y > poses[high].World[g].y) high = i;
			float reach = (poses[i].World[g] - poses[i].World[pel]).magnitude;
			if (reach > best) { best = reach; outIdx = i; }
			if (poses[i].World[pel].y < poses[hipsLow].World[pel].y) hipsLow = i;
		}
		int air = poses.Count(p => SoleY(p) > s_refFloor + 0.05f);
		string F(float v, string fmt = "F3") => v.ToString(fmt, CultureInfo.InvariantCulture);
		string P(int i, int b) => $"{F(i / rate)},{F(poses[i].World[b].x)},{F(poses[i].World[b].y)},{F(poses[i].World[b].z)}";
		return string.Join(" ", name, "len=" + F((poses.Count - 1) / rate), "mode=" + mode, "speed=" + F(speed, "F2"), $"travel={F(travel.x, "F2")},{F(travel.z, "F2")}",
			"turn=" + F(turn, "F0"), "start=" + F(startYaw, "F0"), "gstart=" + P(0, g), "glow=" + P(low, g), "ghigh=" + P(high, g), "gout=" + P(outIdx, g),
			"release=" + P(release, h), $"hipslow={F(hipsLow / rate)},{F(poses[hipsLow].World[pel].y)}", "air=" + F(air / rate, "F2"));
	}

	private static float Yaw(Pose p)
	{
		Vector3 lateral = Vector3.ProjectOnPlane(p.World[System.Array.IndexOf(Bones, "Bip01 L Thigh")] - p.World[System.Array.IndexOf(Bones, "Bip01 R Thigh")], Vector3.up);
		Vector3 fwd = Vector3.Cross(Vector3.up, lateral);
		return Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;
	}

	// A humanoid avatar for the Biped, built in a T-pose (as Ma9MBattingForms.BuildAvatar).
	private static Avatar BuildAvatar(SkinnedMeshRenderer smr)
	{
		Transform[] bs = smr.bones;
		Dictionary<Transform, Vector3> rest = bs.ToDictionary(t => t, t => t.localPosition);
		// How steep his feet stand in the game's own stance (the bind pose has them 8° steeper).
		AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/AnimationClip/Run_BasicStd.anim").SampleAnimation(s_man, 0f);
		Dictionary<string, float> sole = new[] { "L", "R" }.ToDictionary(side => side, SolePitch);
		Debug.Log($"[Ma9MField] the feet stand {sole["L"]:F1}° / {sole["R"]:F1}° ankle to toe in Run_BasicStd (kept in the T-pose)");
		BindPose(smr);
		float drift = 0f;
		foreach (Transform t in bs)
		{
			if (t == s_bone["Bip01"]) continue;
			drift = Mathf.Max(drift, (t.localPosition - rest[t]).magnitude);
			t.localPosition = rest[t];
		}
		Debug.Log($"[Ma9MField] bind pose offsets differ from the skeleton's by up to {drift:F4}");
		SpineLog("bind pose");
		Transform root = s_bone["Bip01"];
		Vector3 headUp = (s_bone["Bip01 Head"].position - s_bone["Bip01 Pelvis"].position).normalized;
		root.rotation = Quaternion.FromToRotation(headUp, Vector3.up) * root.rotation;
		Vector3 hips = Vector3.ProjectOnPlane(s_bone["Bip01 L Thigh"].position - s_bone["Bip01 R Thigh"].position, Vector3.up);
		root.rotation = Quaternion.FromToRotation(Vector3.Cross(Vector3.up, hips), Vector3.forward) * root.rotation;
		void Point(string a, string b, Vector3 dir)
		{
			Transform ta = s_bone[a];
			ta.rotation = Quaternion.FromToRotation(s_bone[b].position - ta.position, dir) * ta.rotation;
		}
		Point("Bip01 L UpperArm", "Bip01 L Forearm", Vector3.left);
		Point("Bip01 L Forearm", "Bip01 L Hand", Vector3.left);
		Point("Bip01 R UpperArm", "Bip01 R Forearm", Vector3.right);
		Point("Bip01 R Forearm", "Bip01 R Hand", Vector3.right);
		Point("Bip01 L Thigh", "Bip01 L Calf", Vector3.down);
		Point("Bip01 L Calf", "Bip01 L Foot", Vector3.down);
		Point("Bip01 R Thigh", "Bip01 R Calf", Vector3.down);
		Point("Bip01 R Calf", "Bip01 R Foot", Vector3.down);
		foreach (string side in new[] { "L", "R" }) SoleFlat(side, sole[side]);
		SpineLog("T-pose");
		HumanDescription d = new HumanDescription
		{
			human = Human.Select(kv => new HumanBone { humanName = kv.Key, boneName = kv.Value, limit = new HumanLimit { useDefaultValues = true } }).ToArray(),
			skeleton = s_man.GetComponentsInChildren<Transform>(true).Select(t => new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale }).ToArray(),
			upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f, armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false
		};
		return AvatarBuilder.BuildHumanAvatar(s_man, d);
	}

	// How steep the foot stands, ankle to toe (degrees, toe below): with the sole flat on the ground, 59.7° on this
	// high-ankled figure (the game's own stances keep it). The T-pose's foot must stand so: a humanoid foot's muscles are
	// 새로 만들거나 새로운 폼을 만든 선수는 발이 45도로 세워져있음"). Ma9MBattingForms does the same.
	private static float SolePitch(string side)
	{
		Vector3 a = s_bone["Bip01 " + side + " Toe0"].position - s_bone["Bip01 " + side + " Foot"].position;
		return Mathf.Atan2(-a.y, new Vector2(a.x, a.z).magnitude) * Mathf.Rad2Deg;
	}

	// The foot turned (about the side-to-side axis) so ankle to toe points forward and down at that pitch.
	private static void SoleFlat(string side, float pitch)
	{
		Transform foot = s_bone["Bip01 " + side + " Foot"];
		Vector3 along = Vector3.ProjectOnPlane(s_bone["Bip01 " + side + " Toe0"].position - foot.position, Vector3.right);
		float r = pitch * Mathf.Deg2Rad;
		if (along.sqrMagnitude > 1e-6f) foot.rotation = Quaternion.FromToRotation(along, new Vector3(0f, -Mathf.Sin(r), Mathf.Cos(r)) * along.magnitude) * foot.rotation;
	}

	private static T Part<T>(string name) where T : Component => AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/Character/Mesh/" + name + ".prefab").GetComponentInChildren<T>();

	// The right-handed fielder's mesh as the game makes it (Ma9DoriMaker: the low body, cap and glove combined; the prefab's bones
	// are in the low body's order, the full one's differ).
	private static Mesh FielderMesh()
	{
		GameObject tmp = new GameObject("Ma9MFieldMaker");
		Ma9DoriMaker maker = tmp.AddComponent<Ma9DoriMaker>();
		Mesh dst = new Mesh();
		maker.Combine(Part<SkinnedMeshRenderer>("Ma9_Body_R_Lo").sharedMesh, Part<MeshFilter>("Ma9_Cap_R_Lo").sharedMesh, Part<MeshFilter>("Ma9_Glo_R").sharedMesh, null, dst, false);
		Object.DestroyImmediate(tmp);
		dst.RecalculateBounds();
		return dst;
	}

	// Dev: how the spine runs (each segment's lean forward and to the side from vertical).
	private static void SpineLog(string label)
	{
		string spine = "";
		string[] chain = { "Bip01 Pelvis", "Bip01 Spine", "Bip01 Spine1", "Bip01 Neck", "Bip01 Head" };
		for (int i = 0; i + 1 < chain.Length; i++)
		{
			Vector3 seg = s_bone[chain[i + 1]].position - s_bone[chain[i]].position;
			spine += $" {chain[i + 1].Replace("Bip01 ", "")} fwd {Mathf.Atan2(seg.z, seg.y) * Mathf.Rad2Deg:F0} side {Mathf.Atan2(seg.x, seg.y) * Mathf.Rad2Deg:F0}";
		}
		Debug.Log($"[Ma9MField] {label} spine from vertical:{spine}");
	}

	// The bones as the mesh was bound, parents first.
	private static void BindPose(SkinnedMeshRenderer smr)
	{
		Transform[] bs = smr.bones;
		int Depth(Transform t)
		{
			int n = 0;
			for (Transform p = t.parent; p != null; p = p.parent) n++;
			return n;
		}
		foreach (int i in Enumerable.Range(0, bs.Length).OrderBy(i => Depth(bs[i])))
		{
			Matrix4x4 w = smr.transform.localToWorldMatrix * smr.sharedMesh.bindposes[i].inverse;
			bs[i].SetPositionAndRotation(w.GetColumn(3), w.rotation);
		}
	}

	private static AnimationClip Write(List<Pose> poses, float rate, bool loop)
	{
		AnimationClip clip = new AnimationClip { legacy = true, frameRate = rate, wrapMode = loop ? WrapMode.Loop : WrapMode.Once };
		for (int b = 0; b < Bones.Length; b++)
		{
			string path = AnimationUtility.CalculateTransformPath(s_bone[Bones[b]], s_man.transform);
			Quaternion[] rot = poses.Select(p => p.Rot[b]).ToArray();
			for (int i = 1; i < rot.Length; i++) if (Quaternion.Dot(rot[i - 1], rot[i]) < 0) rot[i] = new Quaternion(-rot[i].x, -rot[i].y, -rot[i].z, -rot[i].w);
			string[] q = { "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
			for (int k = 0; k < 4; k++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), q[k]), Ma9MBattingFormsCurve(rate, rot.Select(x => x[k]).ToArray()));
			Vector3[] pos = poses.Select(p => p.Pos[b]).ToArray();
			string[] pn = { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z" };
			for (int k = 0; k < 3; k++) AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Transform), pn[k]), Ma9MBattingFormsCurve(rate, pos.Select(x => x[k]).ToArray()));
		}
		return clip;
	}

	// Keys every frame with central-difference slopes, less those the curve passes within 0.0005 of anyway (as
	// Ma9MBattingForms.Curve).
	private static AnimationCurve Ma9MBattingFormsCurve(float rate, float[] v)
	{
		const float tolerance = 0.0005f;
		if (v.Max() - v.Min() < 1e-6f) return new AnimationCurve(new Keyframe(0f, v[0]), new Keyframe((v.Length - 1) / rate, v[0]));
		Keyframe[] all = new Keyframe[v.Length];
		for (int i = 0; i < v.Length; i++)
		{
			int i0 = System.Math.Max(0, i - 1), i1 = System.Math.Min(v.Length - 1, i + 1);
			float slope = i1 > i0 ? (v[i1] - v[i0]) * rate / (i1 - i0) : 0f;
			all[i] = new Keyframe(i / rate, v[i], slope, slope);
		}
		List<Keyframe> keep = new List<Keyframe> { all[0] };
		int last = 0;
		for (int i = 1; i < v.Length - 1; i++)
		{
			AnimationCurve trial = new AnimationCurve(all[last], all[i + 1]);
			bool fits = true;
			for (int j = last + 1; j <= i && fits; j++) fits = Mathf.Abs(trial.Evaluate(j / rate) - v[j]) <= tolerance;
			if (!fits)
			{
				keep.Add(all[i]);
				last = i;
			}
		}
		keep.Add(all[v.Length - 1]);
		return new AnimationCurve(keep.ToArray());
	}

	// Dev: a contact sheet of the clip, eight moments from its start to its end, from his right side and from in front of
	// him to his left; the white disc and the bar are where he started and the way he faced (+z).
	private static void Sheet(string dir, string name, List<Pose> poses, List<Vector3> moves = null)
	{
		float tall = s_tall;
		Debug.Log($"[Ma9MField] sheet {name}: head {poses[0].World[System.Array.IndexOf(Bones, "Bip01 Head")]} hips {poses[0].World[1]} Bip01 {poses[0].World[0]} local {poses[0].Pos[0]} scale {s_bone["Bip01"].lossyScale} tall {tall:F2}");
		string lengths = "";
		for (int b = 1; b < Bones.Length; b++)
		{
			int parent = System.Array.IndexOf(Bones, s_bone[Bones[b]].parent.name);
			if (parent >= 0) lengths += $" {Bones[b].Replace("Bip01 ", "")}={(poses[0].World[b] - poses[0].World[parent]).magnitude:F3}/{poses[0].Pos[b].magnitude:F3}";
		}
		Debug.Log("[Ma9MField] bone lengths world/local:" + lengths);
		Directory.CreateDirectory(dir);
		GameObject camGo = GameObject.Find("Ma9MFieldCam");
		if (camGo == null) camGo = new GameObject("Ma9MFieldCam");
		Camera cam = camGo.GetComponent<Camera>();
		if (cam == null) cam = camGo.AddComponent<Camera>();
		GameObject sunGo = GameObject.Find("Ma9MFieldSun");
		if (sunGo == null)
		{
			sunGo = new GameObject("Ma9MFieldSun");
			Light l = sunGo.AddComponent<Light>();
			l.type = LightType.Directional;
			l.intensity = 1.2f;
			sunGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
			GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
			ground.name = "Ma9MFieldGround";
			ground.transform.localScale = new Vector3(2f, 1f, 2f);
			ground.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.3f, 0.45f, 0.3f) };
			GameObject spot = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
			spot.name = "Ma9MFieldSpot";
			spot.transform.localScale = new Vector3(0.3f, 0.005f, 0.3f);
			spot.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = Color.white };
			GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
			bar.name = "Ma9MFieldFacing";
			bar.transform.SetPositionAndRotation(new Vector3(0f, 0.005f, 0.6f), Quaternion.identity);
			bar.transform.localScale = new Vector3(0.05f, 0.01f, 1.2f);
			bar.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Standard")) { color = new Color(0.9f, 0.3f, 0.2f) };
		}
		cam.backgroundColor = new Color(0.55f, 0.65f, 0.8f);
		cam.clearFlags = CameraClearFlags.SolidColor;
		cam.fieldOfView = 30f;
		int tile = int.Parse(Arg("-ma9mfmtile", "260")), cols = int.Parse(Arg("-ma9mfmcols", "8"));
		RenderTexture rt = new RenderTexture(tile, tile, 24) { antiAliasing = 4 };
		cam.targetTexture = rt;
		Texture2D sheet = new Texture2D(tile * cols, tile * 2, TextureFormat.RGB24, false);
		for (int k = 0; k < cols; k++)
		{
			int i = Mathf.RoundToInt(k * (poses.Count - 1) / (float)(cols - 1));
			for (int b = 0; b < Bones.Length; b++)
			{
				s_bone[Bones[b]].localPosition = poses[i].Pos[b];
				s_bone[Bones[b]].localRotation = poses[i].Rot[b];
			}
			// Along its track, as the player moves and turns him.
			if (moves != null) s_man.transform.SetPositionAndRotation(new Vector3(moves[i].x, 0f, moves[i].y), Quaternion.Euler(0f, moves[i].z, 0f));
			// On his hips (a clip that travels far would leave the picture); the white disc is where he started.
			Vector3 hp = s_man.transform.TransformPoint(poses[i].World[System.Array.IndexOf(Bones, "Bip01 Pelvis")]);
			Vector3 c = new Vector3(hp.x, 0.5f * tall, hp.z);
			for (int v = 0; v < 2; v++)
			{
				cam.transform.position = c + 3.4f * tall * (v == 0 ? new Vector3(1f, 0.15f, 0f) : new Vector3(-0.6f, 0.3f, 0.75f)).normalized;
				cam.transform.LookAt(c);
				cam.Render();
				RenderTexture.active = rt;
				sheet.ReadPixels(new Rect(0, 0, tile, tile), k * tile, (1 - v) * tile);
				RenderTexture.active = null;
			}
		}
		s_man.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
		sheet.Apply();
		File.WriteAllBytes(Path.Combine(dir, name + ".png"), sheet.EncodeToPNG());
		Object.DestroyImmediate(sheet);
		cam.targetTexture = null;
		rt.Release();
	}
}
