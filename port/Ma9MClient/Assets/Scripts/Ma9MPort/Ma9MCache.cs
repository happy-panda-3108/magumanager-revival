using UnityEngine;

// Ma9M port: the client keeps downloaded emblems and 3D uniforms forever (KBO/Emblem/..., KBO/Uniform/...),
// with no version to tell it they changed. When the server's art changes, bump EmblemArt or UniformArt and
// the cached copies are dropped once at startup, so the new ones are downloaded.
public static class Ma9MCache
{
	private const string EmblemArt = "2";

	// 2: spikes too (the flat <TEAM>_SPIKE placeholders stayed cached under the same names).
	private const string UniformArt = "2";

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void DropStale()
	{
		Drop("Ma9M.EmblemArt", EmblemArt, "Emblem");
		Drop("Ma9M.UniformArt", UniformArt, "Uniform");
	}

	private static void Drop(string key, string art, string folder)
	{
		try
		{
			if (PlayerPrefs.GetString(key, string.Empty) == art)
			{
				return;
			}
			string dir = System.IO.Path.Combine(Application.persistentDataPath, "KBO", folder);
			if (System.IO.Directory.Exists(dir))
			{
				System.IO.Directory.Delete(dir, true);
			}
			PlayerPrefs.SetString(key, art);
			PlayerPrefs.Save();
			Debug.Log("[Ma9MCache] " + folder + " cache cleared for art " + art);
		}
		catch (System.Exception ex)
		{
			Debug.LogWarning("[Ma9MCache] " + ex.Message);
		}
	}
}
