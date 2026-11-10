using System.IO;
using UnityEditor;
using UnityEngine;

// Renders a stadium prefab from a few broadcast-like viewpoints to PNGs, without running the game.
//   Unity.exe -batchmode -quit -projectPath <p> -executeMethod Ma9MStadiumPreview.Render -ma9mstadium Jamsil -ma9mout <dir>
public static class Ma9MStadiumPreview
{
	public static void Render()
	{
		string[] args = System.Environment.GetCommandLineArgs();
		string name = Arg(args, "-ma9mstadium") ?? "Jamsil";
		string outDir = Arg(args, "-ma9mout") ?? "Temp/StadiumPreview";
		Directory.CreateDirectory(outDir);

		GameObject prefab = Resources.Load<GameObject>("Ma9MGround/" + name);
		GameObject stadium = Object.Instantiate(prefab);
		GameObject sunGo = new GameObject("Sun");
		Light sun = sunGo.AddComponent<Light>();
		sun.type = LightType.Directional;
		RenderSettings.sun = sun;
		stadium.GetComponent<Ma9MStadiumLighting>()?.Apply();
		Material sky = AssetDatabase.LoadAssetAtPath<Material>("Assets/Material/sky_Sunny_M.mat");
		if (sky != null)
		{
			RenderSettings.skybox = sky;
		}

		// A stand-in 마구돌이 height marker at the plate and on the mound, to judge scale.
		Marker(new Vector3(0.6f, 0.9f, -0.3f));
		Marker(new Vector3(13.07f, 1.3f, 13.07f));

		Camera cam = new GameObject("Cam").AddComponent<Camera>();
		cam.clearFlags = CameraClearFlags.Skybox;
		cam.farClipPlane = 3000f;
		cam.allowMSAA = true;
		RenderTexture rt = new RenderTexture(1600, 900, 24) { antiAliasing = 4 };
		cam.targetTexture = rt;

		Shot(cam, rt, outDir, "1_behind_plate", new Vector3(-5.5f, 3.2f, -5.5f), new Vector3(20f, 0f, 20f), 40f);
		Shot(cam, rt, outDir, "2_centerfield_tv", new Vector3(52f, 9f, 52f), new Vector3(0f, 1f, 0f), 18f);
		Shot(cam, rt, outDir, "3_high_home", new Vector3(-30f, 35f, -30f), new Vector3(45f, 0f, 45f), 50f);
		Shot(cam, rt, outDir, "4_top", new Vector3(45f, 220f, 45f), new Vector3(45.01f, 0f, 45f), 60f);
		Shot(cam, rt, outDir, "5_first_base_side", new Vector3(45f, 6f, -12f), new Vector3(10f, 1f, 15f), 45f);
		Shot(cam, rt, outDir, "6_outfield_from_home", new Vector3(2f, 2.5f, 2f), new Vector3(80f, 8f, 80f), 60f);
		Debug.Log("[Ma9MStadiumPreview] wrote " + outDir);
	}

	private static void Marker(Vector3 at)
	{
		GameObject g = GameObject.CreatePrimitive(PrimitiveType.Capsule);
		g.transform.position = at;
		g.transform.localScale = new Vector3(0.6f, 0.9f, 0.6f);
		g.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.red };
	}

	private static void Shot(Camera cam, RenderTexture rt, string dir, string name, Vector3 from, Vector3 at, float fov)
	{
		cam.transform.position = from;
		cam.transform.LookAt(at);
		cam.fieldOfView = fov;
		cam.Render();
		RenderTexture.active = rt;
		Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
		tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
		tex.Apply();
		File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
		RenderTexture.active = null;
	}

	private static string Arg(string[] args, string key)
	{
		int i = System.Array.IndexOf(args, key);
		return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
	}
}
