using UnityEngine;

// Ma9M port (engine session, 2026-10-30): on a tag play the fielder's glove goes to the runner. The
// tag clip (Def_SliderTag) only kneels and sweeps the glove down in front of him; around the tag this
// steps him toward the runner if his arm cannot reach, turns him square to him, and bends his glove
// arm (two-bone IK on the left arm: the glove hand) so the glove lands on the runner's lead foot (his
// lead hand on a head-first slide). Runs after the animation each frame (Ma9MScriptPlayer.LateUpdate).
public static class Ma9MTagReach
{
	public sealed class Arm
	{
		public Transform Upper, Fore, Hand;
		public float Reach;

		public static Arm Of(Transform root)
		{
			Transform hand = Bone(root, "Bip01 L Hand");
			if (hand == null || hand.parent == null || hand.parent.parent == null) return null;
			Arm a = new Arm { Hand = hand, Fore = hand.parent, Upper = hand.parent.parent };
			a.Reach = ((a.Fore.position - a.Upper.position).magnitude + (a.Hand.position - a.Fore.position).magnitude) * 0.97f;
			return a;
		}
	}

	public sealed class Limbs
	{
		public Transform FootL, FootR, HandL, HandR;

		public static Limbs Of(Transform root)
		{
			return new Limbs
			{
				FootL = Bone(root, "Bip01 L Foot"),
				FootR = Bone(root, "Bip01 R Foot"),
				HandL = Bone(root, "Bip01 L Hand"),
				HandR = Bone(root, "Bip01 R Hand")
			};
		}

		// The part of him that gets to the bag first: a foot, or a hand when he dives in head first.
		public Vector3 Lead(Vector3 bag, bool headFirst, Transform fallback)
		{
			Transform a = headFirst ? HandL : FootL, b = headFirst ? HandR : FootR;
			if (a == null && b == null) return fallback.position + fallback.forward * 0.4f + Vector3.up * 0.15f;
			if (a == null) return b.position;
			if (b == null) return a.position;
			return (a.position - bag).sqrMagnitude < (b.position - bag).sqrMagnitude ? a.position : b.position;
		}
	}

	public static Transform Bone(Transform root, string name)
	{
		if (root.name == name) return root;
		for (int i = 0; i < root.childCount; i++)
		{
			Transform t = Bone(root.GetChild(i), name);
			if (t != null) return t;
		}
		return null;
	}

	// How much of the reach is on at time t for a tag at time T: in over the quarter second before,
	// held, out again after.
	public static float Weight(float t, float T)
	{
		if (t < T - 0.25f || t > T + 0.65f) return 0;
		if (t < T) return Smooth((t - (T - 0.25f)) / 0.25f);
		if (t < T + 0.35f) return 1;
		return 1 - Smooth((t - (T + 0.35f)) / 0.3f);
	}

	private static float Smooth(float u)
	{
		u = Mathf.Clamp01(u);
		return u * u * (3 - 2 * u);
	}

	public static void Apply(Transform body, Arm arm, Vector3 target, float w)
	{
		if (w <= 0 || arm == null) return;
		// Square up to the runner and, if the glove would fall short, a step toward him.
		Vector3 flat = target - body.position;
		flat.y = 0;
		if (flat.sqrMagnitude > 1e-4f)
		{
			body.rotation = Quaternion.Slerp(body.rotation, Quaternion.LookRotation(flat), w);
		}
		Vector3 toTarget = target - arm.Upper.position;
		float over = toTarget.magnitude - arm.Reach;
		if (over > 0)
		{
			Vector3 step = toTarget;
			step.y = 0;
			if (step.sqrMagnitude > 1e-4f) body.position += step.normalized * Mathf.Min(over, 0.45f) * w;
		}
		TwoBone(arm.Upper, arm.Fore, arm.Hand, target, w);
	}

	// Bend upper arm and forearm so the hand reaches the target, keeping the elbow on the side it is.
	private static void TwoBone(Transform upper, Transform fore, Transform hand, Vector3 target, float w)
	{
		Vector3 a = upper.position, b = fore.position, c = hand.position;
		float l1 = (b - a).magnitude, l2 = (c - b).magnitude;
		if (l1 < 1e-4f || l2 < 1e-4f) return;
		Vector3 at = target - a;
		if (at.sqrMagnitude < 1e-6f) return;
		Vector3 dir = at.normalized;
		float d = Mathf.Clamp(at.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, l1 + l2 - 1e-3f);
		Vector3 n = Vector3.Cross(b - a, c - b);
		if (n.sqrMagnitude < 1e-8f) n = Vector3.Cross(dir, Vector3.up);
		n = (n - Vector3.Dot(n, dir) * dir);
		if (n.sqrMagnitude < 1e-8f) return;
		n.Normalize();
		float side = Mathf.Sign(Vector3.Dot(Vector3.Cross(dir, b - a), n));
		if (side == 0) side = 1;
		float angA = Mathf.Acos(Mathf.Clamp((l1 * l1 + d * d - l2 * l2) / (2 * l1 * d), -1f, 1f)) * Mathf.Rad2Deg;
		Vector3 elbow = a + (Quaternion.AngleAxis(side * angA, n) * dir) * l1;
		Quaternion q1 = Quaternion.FromToRotation(b - a, elbow - a);
		upper.rotation = Quaternion.Slerp(upper.rotation, q1 * upper.rotation, w);
		Vector3 b2 = fore.position, c2 = hand.position;
		Vector3 goal = a + dir * d;
		Quaternion q2 = Quaternion.FromToRotation(c2 - b2, goal - b2);
		fore.rotation = Quaternion.Slerp(fore.rotation, q2 * fore.rotation, w);
	}
}
