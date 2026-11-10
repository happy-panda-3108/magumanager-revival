using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

// Builds a plain ballpark prefab (Resources/Ma9MGround/Default) for the 3D broadcast while the
// real stadium bundles are missing: grass, infield dirt, bases, mound, foul lines and an outfield
// fence with the FenceData the ball physics needs. The game's field frame: home plate at the
// origin, the first-base line along +x, the third-base line along +z, metres.
//   Unity.exe -batchmode -quit -projectPath <p> -executeMethod Ma9MGroundBuilder.Build
public static class Ma9MGroundBuilder
{
	private const string Dir = "Assets/Resources/Ma9MGround";

	private const float Line = 98f, Centre = 120f, FenceHeight = 2.5f;

	public static float FenceRadius(float angleDeg) => Line + (Centre - Line) * Mathf.Sin(2f * angleDeg * Mathf.Deg2Rad);

	[MenuItem("Ma9M/Build Default Ground")]
	public static void Build()
	{
		Directory.CreateDirectory(Dir);
		GameObject root = new GameObject("Default");
		Material grass = Mat("Grass", new Color(0.23f, 0.52f, 0.20f));
		Material grassLight = Mat("GrassInfield", new Color(0.27f, 0.58f, 0.23f));
		Material dirt = Mat("Dirt", new Color(0.62f, 0.43f, 0.27f));
		Material white = Mat("Chalk", new Color(0.95f, 0.95f, 0.92f));
		Material wall = Mat("Wall", new Color(0.10f, 0.25f, 0.18f));
		Material track = Mat("Track", new Color(0.50f, 0.34f, 0.22f));

		Vector3 mound = new Vector3(13.04f, 0f, 13.04f);
		Part(root, "Grass", Quad(new Vector3(-150f, 0f, -150f), new Vector3(300f, 0f, 0f), new Vector3(0f, 0f, 300f)), grass, 0f);
		// Warning track just inside the fence.
		Part(root, "Track", FenceBand(-4f, 0f), track, 0.004f);
		Part(root, "InfieldDirt", Disc(mound, 28.9f, 64), dirt, 0.008f);
		Part(root, "HomeDirt", Disc(Vector3.zero, 4f, 32), dirt, 0.010f);
		Part(root, "InfieldGrass", Quad(new Vector3(2.7f, 0f, 2.7f), new Vector3(22f, 0f, 0f), new Vector3(0f, 0f, 22f)), grassLight, 0.012f);
		Part(root, "Mound", Disc(mound, 2.74f, 32), dirt, 0.016f);
		foreach (Vector3 b in new[] { new Vector3(27.43f, 0f, 0f), new Vector3(27.43f, 0f, 27.43f), new Vector3(0f, 0f, 27.43f) })
		{
			Part(root, "BaseDirt", Disc(b, 2.8f, 24), dirt, 0.014f);
			Part(root, "Base", Quad(b - new Vector3(0.19f, 0f, 0.19f), new Vector3(0.38f, 0f, 0f), new Vector3(0f, 0f, 0.38f)), white, 0.02f);
		}
		Part(root, "HomePlate", Quad(new Vector3(-0.215f, 0f, -0.215f), new Vector3(0.43f, 0f, 0f), new Vector3(0f, 0f, 0.43f)), white, 0.02f);
		Part(root, "Rubber", Quad(mound - new Vector3(0.3f, 0f, 0.08f), new Vector3(0.6f, 0f, 0f), new Vector3(0f, 0f, 0.16f)), white, 0.03f);
		// Foul lines to the poles.
		Part(root, "FoulLine1B", Quad(new Vector3(0f, 0f, -0.05f), new Vector3(Line, 0f, 0f), new Vector3(0f, 0f, 0.1f)), white, 0.018f);
		Part(root, "FoulLine3B", Quad(new Vector3(-0.05f, 0f, 0f), new Vector3(0.1f, 0f, 0f), new Vector3(0f, 0f, Line)), white, 0.018f);

		// Fence from the left-field pole round to the right-field pole (normals face home plate).
		List<Vector3> fence = new List<Vector3>();
		for (int i = 0; i <= 36; i++)
		{
			float a = 90f - 2.5f * i;
			float r = FenceRadius(a);
			fence.Add(new Vector3(r * Mathf.Cos(a * Mathf.Deg2Rad), FenceHeight, r * Mathf.Sin(a * Mathf.Deg2Rad)));
		}
		Part(root, "Fence", Wall(fence, FenceHeight), wall, 0f);
		// Foul poles.
		Part(root, "PoleLF", Wall(new List<Vector3> { new Vector3(-0.2f, 0f, Line), new Vector3(0.2f, 0f, Line) }, 14f), Mat("Pole", new Color(0.95f, 0.85f, 0.15f)), 0f);
		Part(root, "PoleRF", Wall(new List<Vector3> { new Vector3(Line, 0f, 0.2f), new Vector3(Line, 0f, -0.2f) }, 14f), Mat("Pole", new Color(0.95f, 0.85f, 0.15f)), 0f);
		// Stands: a sloped band behind the fence and around the infield.
		Part(root, "Stands", Stands(), Mat("Stands", new Color(0.32f, 0.33f, 0.38f)), 0f);

		FenceData data = root.AddComponent<FenceData>();
		data.fences = fence.ToArray();
		PrefabUtility.SaveAsPrefabAsset(root, Dir + "/Default.prefab");
		Object.DestroyImmediate(root);
		AssetDatabase.SaveAssets();
		Debug.Log("[Ma9MGroundBuilder] built " + Dir + "/Default.prefab");
	}

	private static Material Mat(string name, Color color)
	{
		string path = Dir + "/" + name + ".mat";
		Material m = AssetDatabase.LoadAssetAtPath<Material>(path);
		if (m == null)
		{
			m = new Material(Shader.Find("Unlit/Color"));
			AssetDatabase.CreateAsset(m, path);
		}
		m.color = color;
		EditorUtility.SetDirty(m);
		return m;
	}

	private static void Part(GameObject root, string name, Mesh mesh, Material mat, float lift)
	{
		mesh.name = name;
		string path = Dir + "/" + name + "_" + root.transform.childCount + ".asset";
		AssetDatabase.CreateAsset(mesh, path);
		GameObject go = new GameObject(name);
		go.transform.SetParent(root.transform, false);
		go.transform.localPosition = new Vector3(0f, lift, 0f);
		go.AddComponent<MeshFilter>().sharedMesh = mesh;
		go.AddComponent<MeshRenderer>().sharedMaterial = mat;
	}

	private static Mesh Quad(Vector3 o, Vector3 u, Vector3 v)
	{
		Mesh m = new Mesh();
		m.vertices = new[] { o, o + u, o + u + v, o + v };
		m.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
		m.RecalculateBounds();
		return m;
	}

	private static Mesh Disc(Vector3 c, float r, int n)
	{
		List<Vector3> v = new List<Vector3> { c };
		List<int> t = new List<int>();
		for (int i = 0; i <= n; i++)
		{
			float a = 2f * Mathf.PI * i / n;
			v.Add(c + new Vector3(r * Mathf.Cos(a), 0f, r * Mathf.Sin(a)));
			if (i > 0)
			{
				t.AddRange(new[] { 0, i + 1, i, 0, i, i + 1 });
			}
		}
		Mesh m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
		m.RecalculateBounds();
		return m;
	}

	// A flat band following the fence, from (fence + inner) to (fence + outer) metres.
	private static Mesh FenceBand(float inner, float outer)
	{
		List<Vector3> v = new List<Vector3>();
		List<int> t = new List<int>();
		for (int i = 0; i <= 36; i++)
		{
			float a = (90f - 2.5f * i) * Mathf.Deg2Rad;
			float r = FenceRadius(90f - 2.5f * i);
			v.Add(new Vector3((r + inner) * Mathf.Cos(a), 0f, (r + inner) * Mathf.Sin(a)));
			v.Add(new Vector3((r + outer) * Mathf.Cos(a), 0f, (r + outer) * Mathf.Sin(a)));
			if (i > 0)
			{
				int k = 2 * i;
				t.AddRange(new[] { k - 2, k - 1, k, k - 1, k + 1, k, k - 2, k, k - 1, k - 1, k, k + 1 });
			}
		}
		Mesh m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
		m.RecalculateBounds();
		return m;
	}

	private static Mesh Wall(List<Vector3> line, float height)
	{
		List<Vector3> v = new List<Vector3>();
		List<int> t = new List<int>();
		for (int i = 0; i < line.Count; i++)
		{
			Vector3 p = new Vector3(line[i].x, 0f, line[i].z);
			v.Add(p);
			v.Add(p + Vector3.up * height);
			if (i > 0)
			{
				int k = 2 * i;
				t.AddRange(new[] { k - 2, k - 1, k, k - 1, k + 1, k, k - 2, k, k - 1, k - 1, k, k + 1 });
			}
		}
		Mesh m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
		m.RecalculateBounds();
		return m;
	}

	// Seating: rises from just behind the fence (outfield) and from 18 m behind the plate round the infield.
	private static Mesh Stands()
	{
		List<Vector3> v = new List<Vector3>();
		List<int> t = new List<int>();
		int n = 72;
		for (int i = 0; i <= n; i++)
		{
			float deg = 135f - 360f * i / n;   // all the way round, starting behind third base
			float a = deg * Mathf.Deg2Rad;
			float norm = ((deg % 360f) + 360f) % 360f;
			bool outfield = norm <= 90f;
			float r0 = outfield ? FenceRadius(norm) + 1f : 26f + 8f * Mathf.Abs(Mathf.Cos(a - Mathf.PI / 4f));
			Vector3 dir = new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
			// Behind the plate the stands are centred on the infield, not on home plate.
			Vector3 centre = outfield ? Vector3.zero : new Vector3(13f, 0f, 13f);
			v.Add(centre + dir * r0 + Vector3.up * 3f);
			v.Add(centre + dir * (r0 + 30f) + Vector3.up * 22f);
			if (i > 0)
			{
				int k = 2 * i;
				t.AddRange(new[] { k - 2, k - 1, k, k - 1, k + 1, k, k - 2, k, k - 1, k - 1, k, k + 1 });
			}
		}
		Mesh m = new Mesh { vertices = v.ToArray(), triangles = t.ToArray() };
		m.RecalculateBounds();
		return m;
	}
}
