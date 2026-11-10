using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Ma9MBuild
{
	// Unity.exe -batchmode -quit -projectPath <p> -executeMethod Ma9MBuild.BuildWindows [-ma9mcleanbuild]
	// -ma9mcleanbuild drops the incremental build cache, needed after scenes/prefabs were repaired
	// on disk (the cache otherwise keeps the old, broken serialized scenes).
	// -ma9mrelease makes a release player: no development overhead, and plain log lines without
	// stack traces (collecting one per Debug.Log stalled frames in the chatty broadcast code).
	public static void BuildWindows()
	{
		string[] args = System.Environment.GetCommandLineArgs();
		bool clean = System.Array.IndexOf(args, "-ma9mcleanbuild") >= 0;
		bool release = System.Array.IndexOf(args, "-ma9mrelease") >= 0;
		PlayerSettings.SetStackTraceLogType(LogType.Log, release ? StackTraceLogType.None : StackTraceLogType.ScriptOnly);
		PlayerSettings.SetStackTraceLogType(LogType.Warning, release ? StackTraceLogType.None : StackTraceLogType.ScriptOnly);
		PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
		PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
		PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
		string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
		// -ma9mout <exe path>: a test build elsewhere, so the PC build the user plays stays as it is.
		int o = System.Array.IndexOf(args, "-ma9mout");
		string outPath = o >= 0 && o + 1 < args.Length ? args[o + 1] : "../builds/win/Ma9M.exe";
		BuildPlayerOptions options = new BuildPlayerOptions
		{
			scenes = scenes,
			locationPathName = outPath,
			target = BuildTarget.StandaloneWindows64,
			options = (release ? BuildOptions.None : BuildOptions.Development) | (clean ? BuildOptions.CleanBuildCache : BuildOptions.None)
		};
		// -ma9mproduct <name>: the test build gets its own product name, so its PlayerPrefs and
		// persistent data (saved login, caches) never mix with the PC build the user plays.
		int pn = System.Array.IndexOf(args, "-ma9mproduct");
		string product = PlayerSettings.productName;
		if (pn >= 0 && pn + 1 < args.Length)
		{
			PlayerSettings.productName = args[pn + 1];
		}
		BuildReport report;
		try
		{
			report = BuildPipeline.BuildPlayer(options);
		}
		finally
		{
			PlayerSettings.productName = product;
		}
		Debug.Log($"[Ma9MBuild] result={report.summary.result} errors={report.summary.totalErrors} size={report.summary.totalSize}");
		if (report.summary.result != BuildResult.Succeeded)
		{
			EditorApplication.Exit(1);
		}
	}

	// Unity.exe -batchmode -quit -buildTarget Android -projectPath <p> -executeMethod Ma9MBuild.BuildAndroid -ma9mhost <PC LAN IP>
	// A release APK for phones on the same network as the server PC (personal use):
	// - the server host is baked into Resources/Ma9MServerHost.txt for this build only (PC builds
	//   keep 127.0.0.1); a phone can still override it with <persistentDataPath>/ma9m_server.txt;
	// - fixed package name and the keystore in port/android, and a version code one higher each
	//   build (port/android/version_code.txt), so a new APK installs over the old app;
	// - ARM64, IL2CPP, minimal stripping (the game uses reflection and SendMessage a lot), landscape.
	public static void BuildAndroid()
	{
		string[] args = System.Environment.GetCommandLineArgs();
		int h = System.Array.IndexOf(args, "-ma9mhost");
		string host = h >= 0 && h + 1 < args.Length ? args[h + 1] : null;
		if (string.IsNullOrEmpty(host))
		{
			Debug.LogError("[Ma9MBuild] -ma9mhost <PC LAN IP> is required for the Android build");
			EditorApplication.Exit(1);
			return;
		}
		string androidDir = System.IO.Path.GetFullPath("../android");
		System.Collections.Generic.Dictionary<string, string> keys = new System.Collections.Generic.Dictionary<string, string>();
		foreach (string line in System.IO.File.ReadAllLines(System.IO.Path.Combine(androidDir, "keystore.properties")))
		{
			int eq = line.IndexOf('=');
			if (eq > 0)
			{
				keys[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
			}
		}
		string codeFile = System.IO.Path.Combine(androidDir, "version_code.txt");
		int code = int.Parse(System.IO.File.ReadAllText(codeFile).Trim()) + 1;
		UnityEditor.Build.NamedBuildTarget android = UnityEditor.Build.NamedBuildTarget.Android;
		PlayerSettings.SetApplicationIdentifier(android, "com.ma9m.magumanager");
		PlayerSettings.bundleVersion = "1.0." + code;
		PlayerSettings.Android.bundleVersionCode = code;
		PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
		PlayerSettings.SetManagedStrippingLevel(android, ManagedStrippingLevel.Minimal);
		// Keep every engine module: some are only reached by name (SendMessage, AddComponent strings).
		PlayerSettings.stripEngineCode = false;
		PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
		PlayerSettings.Android.forceInternetPermission = true;
		PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
		PlayerSettings.allowedAutorotateToLandscapeLeft = true;
		PlayerSettings.allowedAutorotateToLandscapeRight = true;
		PlayerSettings.allowedAutorotateToPortrait = false;
		PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
		PlayerSettings.Android.useCustomKeystore = true;
		PlayerSettings.Android.keystoreName = System.IO.Path.Combine(androidDir, keys["keystore"]);
		PlayerSettings.Android.keystorePass = keys["storepass"];
		PlayerSettings.Android.keyaliasName = keys["alias"];
		PlayerSettings.Android.keyaliasPass = keys["keypass"];
		EditorUserBuildSettings.buildAppBundle = false;
		PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
		PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
		string hostFile = "Assets/Resources/Ma9MServerHost.txt";
		string previous = System.IO.File.Exists(hostFile) ? System.IO.File.ReadAllText(hostFile) : null;
		System.IO.File.WriteAllText(hostFile, host);
		AssetDatabase.ImportAsset(hostFile);
		BuildReport report;
		try
		{
			string[] scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
			report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
			{
				scenes = scenes,
				locationPathName = "../builds/android/Ma9M.apk",
				target = BuildTarget.Android,
				options = BuildOptions.None
			});
		}
		finally
		{
			if (previous != null)
			{
				System.IO.File.WriteAllText(hostFile, previous);
			}
			else
			{
				AssetDatabase.DeleteAsset(hostFile);
			}
			AssetDatabase.Refresh();
		}
		Debug.Log($"[Ma9MBuild] android result={report.summary.result} errors={report.summary.totalErrors} size={report.summary.totalSize} versionCode={code} host={host}");
		if (report.summary.result != BuildResult.Succeeded)
		{
			EditorApplication.Exit(1);
			return;
		}
		System.IO.File.WriteAllText(codeFile, code.ToString());
	}

	// Unity.exe -batchmode -quit -projectPath <p> -executeMethod Ma9MBuild.ReimportScenesAndPrefabs
	// Re-imports scenes and prefabs after a script edited them on disk (e.g. port_fix_dup_ids.py),
	// then checks that the intro tutorial scene resolves its panel prefab.
	public static void ReimportScenesAndPrefabs()
	{
		foreach (string guid in AssetDatabase.FindAssets("t:Prefab t:Scene", new[] { "Assets" }))
		{
			AssetDatabase.ImportAsset(AssetDatabase.GUIDToAssetPath(guid), ImportAssetOptions.ForceUpdate);
		}
		AssetDatabase.Refresh();
		GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/GameObject/Panel_IntroTutorial.prefab");
		Debug.Log($"[Ma9MBuild] Panel_IntroTutorial prefab loaded: {prefab != null}");
		EditorSceneManager.OpenScene("Assets/UI/Scene/Scene_IntroTutorial.unity");
		Loader_IntroTutorial loader = Object.FindAnyObjectByType<Loader_IntroTutorial>();
		Debug.Log($"[Ma9MBuild] Loader_IntroTutorial found: {loader != null}, pfTutorialMain: {(loader != null && loader.pfTutorialMain != null ? loader.pfTutorialMain.name : "null")}");
	}
}
