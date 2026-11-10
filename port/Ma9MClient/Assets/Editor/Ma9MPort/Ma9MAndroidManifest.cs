#if UNITY_ANDROID
using System.IO;
using UnityEditor.Android;

// The private server's CDN is plain HTTP (http://<PC>:18080/ma9m/...). Android 9+ blocks cleartext
// HTTP for apps targeting API 28 and up unless the manifest allows it, and "Allow downloads over
// HTTP" does not add that here (the first APK's manifest had no usesCleartextTraffic), so the
// generated Gradle project gets it on <application>.
public class Ma9MAndroidManifest : IPostGenerateGradleAndroidProject
{
	public int callbackOrder => 0;

	public void OnPostGenerateGradleAndroidProject(string path)
	{
		string[] manifests =
		{
			Path.Combine(path, "src", "main", "AndroidManifest.xml"),
			Path.Combine(path, "..", "launcher", "src", "main", "AndroidManifest.xml")
		};
		foreach (string manifest in manifests)
		{
			if (!File.Exists(manifest))
			{
				continue;
			}
			string text = File.ReadAllText(manifest);
			int application = text.IndexOf("<application");
			if (application < 0 || text.Contains("usesCleartextTraffic"))
			{
				continue;
			}
			text = text.Insert(application + "<application".Length, " android:usesCleartextTraffic=\"true\"");
			File.WriteAllText(manifest, text);
			UnityEngine.Debug.Log("[Ma9MBuild] cleartext HTTP allowed in " + manifest);
		}
	}
}
#endif
