using UnityEngine;

// Unity 4 ParticleEmitter was removed in Unity 2018.3; the dust effects now use ParticleSystem.
public static class LegacyParticleCompat
{
	public static void SetEmit(ParticleSystem ps, bool emit)
	{
		if (ps == null)
		{
			return;
		}
		ParticleSystem.EmissionModule emission = ps.emission;
		emission.enabled = emit;
		if (emit && !ps.isPlaying)
		{
			ps.Play();
		}
	}
}
