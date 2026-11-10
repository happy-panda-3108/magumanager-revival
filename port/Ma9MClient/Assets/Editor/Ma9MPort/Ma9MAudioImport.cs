using UnityEditor;
using UnityEngine;

// BGM streams from disk, short effects are decoded once at load.
public class Ma9MAudioImport : AssetPostprocessor
{
	private void OnPreprocessAudio()
	{
		if (!assetPath.StartsWith("Assets/Resources/Ma9MSound/"))
		{
			return;
		}
		AudioImporter importer = (AudioImporter)assetImporter;
		// Music (lobby BGM and the stadiums' 응원가 loops) streams in stereo; short effects decompress.
		bool isBgm = assetPath.Contains("/bgm/") || assetPath.Contains("/cheer/");
		AudioImporterSampleSettings settings = importer.defaultSampleSettings;
		settings.compressionFormat = AudioCompressionFormat.Vorbis;
		settings.quality = isBgm ? 0.6f : 0.7f;
		settings.loadType = isBgm ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
		importer.defaultSampleSettings = settings;
		importer.forceToMono = !isBgm;
	}
}
