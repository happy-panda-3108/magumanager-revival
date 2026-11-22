using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

// Ma9M (engine session, 2026-11-18, AI tester NC 30 / 남은일 A13, user "A 버그들 다 고칩시다"): the broadcast's fixed cameras
// right in front of them: the line-up shot (-13, 10.5, -13) and the high in-play shots look through the net from a metre
// or two, and its cords fill the screen as a thick dark grid. Each park has one net/wire renderer (one material named
// …_Wire / …_net, a mesh of ≤ 120 triangles around the stands); it is hidden while the camera is within NearMetres of
// it, on either side, and shown again further away, where it reads as a fine net. Measured from the fixed spots
// (CameraManager): line-up shot to the net 0.0 m 광주, 3.7 대전, 4.7 창원 (the tester's thick grid), 5.9 수원, 15 대구,
// 27 잠실·문학·사직; the foul-pole shots 3-9 m; the in-play end spot (-4, 10.5, -4) 8.0 창원, 9.0 대전, 12.2 광주.
public class Ma9MBackNet : MonoBehaviour
{
	public const float NearMetres = 13f;

	private static readonly Regex s_netMaterial = new Regex(@"(?i)(_wire|_net)");

	private readonly List<Renderer> m_nets = new List<Renderer>();

	// 11-20 (AI 테스터 NC, 남은일 0-3: 창원 홈 경기의 포수 뒤 카메라에서는 그물이 아직 화면을 덮음): the triangles are kept in the
	// mesh's own space and moved to the world with the renderer's current matrix whenever it changes (the 11-18 version took the
	// world positions once at Attach, before the park was placed), and every enabled camera that draws the net's layer is checked,
	// not only Camera.main (the pitch view behind the catcher is drawn by another camera).
	private readonly List<Vector3[]> m_local = new List<Vector3[]>();

	private readonly List<Vector3[]> m_tris = new List<Vector3[]>();

	private readonly List<Matrix4x4> m_at = new List<Matrix4x4>();

	private Camera[] m_cams = new Camera[8];

	public static void Attach(GameObject ground)
	{
		if (ground == null || ground.GetComponent<Ma9MBackNet>() != null)
		{
			return;
		}
		Ma9MBackNet n = ground.AddComponent<Ma9MBackNet>();
		n.Collect();
		if (n.m_nets.Count == 0)
		{
			Object.Destroy(n);
		}
	}

	private void Collect()
	{
		foreach (MeshRenderer r in GetComponentsInChildren<MeshRenderer>(true))
		{
			Material[] mats = r.sharedMaterials;
			if (mats.Length != 1 || mats[0] == null || !s_netMaterial.IsMatch(mats[0].name))
			{
				continue;
			}
			MeshFilter f = r.GetComponent<MeshFilter>();
			Mesh mesh = f != null ? f.sharedMesh : null;
			if (mesh == null || !mesh.isReadable)
			{
				continue;
			}
			Vector3[] v = mesh.vertices;
			int[] idx = mesh.triangles;
			Vector3[] local = new Vector3[idx.Length];
			for (int i = 0; i < idx.Length; i++)
			{
				local[i] = v[idx[i]];
			}
			m_nets.Add(r);
			m_local.Add(local);
			m_tris.Add(new Vector3[local.Length]);
			m_at.Add(new Matrix4x4());
		}
		Debug.Log("[Ma9MBackNet] " + m_nets.Count + " net renderer(s) in " + name);
	}

	private void LateUpdate()
	{
		int camCount = Camera.allCamerasCount;
		if (camCount == 0)
		{
			return;
		}
		if (m_cams.Length < camCount)
		{
			m_cams = new Camera[camCount];
		}
		camCount = Camera.GetAllCameras(m_cams);
		float near2 = NearMetres * NearMetres;
		for (int k = 0; k < m_nets.Count; k++)
		{
			Renderer r = m_nets[k];
			if (r == null)
			{
				continue;
			}
			Matrix4x4 m = r.transform.localToWorldMatrix;
			Vector3[] t = m_tris[k];
			if (m != m_at[k])
			{
				Vector3[] l = m_local[k];
				for (int i = 0; i < l.Length; i++)
				{
					t[i] = m.MultiplyPoint3x4(l[i]);
				}
				m_at[k] = m;
			}
			int layer = 1 << r.gameObject.layer;
			bool close = false;
			for (int ci = 0; ci < camCount && !close; ci++)
			{
				Camera cam = m_cams[ci];
				if (cam == null || !cam.isActiveAndEnabled || (cam.cullingMask & layer) == 0)
				{
					continue;
				}
				Vector3 c = cam.transform.position;
				for (int i = 0; i + 2 < t.Length && !close; i += 3)
				{
					close = (ClosestOnTriangle(c, t[i], t[i + 1], t[i + 2]) - c).sqrMagnitude < near2;
				}
			}
			if (r.enabled == close)
			{
				r.enabled = !close;
			}
		}
	}

	// Closest point on triangle abc to p (Ericson, Real-Time Collision Detection 5.1.5).
	private static Vector3 ClosestOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
	{
		Vector3 ab = b - a, ac = c - a, ap = p - a;
		float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
		if (d1 <= 0f && d2 <= 0f) return a;
		Vector3 bp = p - b;
		float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
		if (d3 >= 0f && d4 <= d3) return b;
		float vc = d1 * d4 - d3 * d2;
		if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));
		Vector3 cp = p - c;
		float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
		if (d6 >= 0f && d5 <= d6) return c;
		float vb = d5 * d2 - d1 * d6;
		if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));
		float va = d3 * d6 - d5 * d4;
		if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
		float denom = 1f / (va + vb + vc);
		return a + ab * (vb * denom) + ac * (vc * denom);
	}
}
