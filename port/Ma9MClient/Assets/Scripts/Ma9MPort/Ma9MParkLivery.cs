using System.Collections.Generic;
using UnityEngine;

// Ma9M (engine session, 2026-11-18; AI tester 쌍방울 15 / 남은일 A12, user "A 버그들 다 고칩시다"): 쌍방울 plays its home games at
// 중계인데 구장 광고판이 TIGERS다". When the home club is not the Tigers (KIA or 해태), the park's materials that use those
// textures get copies with the sign-free ones (tools/scripts/stadium/kwangju_neutral.py → Resources/Ma9MGround/KwangjuNeutral).
public static class Ma9MParkLivery
{
	private static readonly Dictionary<string, string> s_kwangju = new Dictionary<string, string>
	{
		{ "074_Champions_G_01", "Ma9MGround/KwangjuNeutral/074_Champions_G_01_neutral" },
		{ "074_Champions_logo", "Ma9MGround/KwangjuNeutral/074_Champions_logo_neutral" },
	};

	public static void Apply(GameObject ground, string groundName, int homeTeam)
	{
		if (ground == null || groundName != "Kwangju" || ResourceManager.ServerType != eServerType.KBO)
		{
			return;
		}
		int club = Ma9MEmblemYears.BaseOf(homeTeam);
		if (club == (int)eTeamName.KIA_TIGERS || club == (int)eTeamName.HAITAI_TIGERS)
		{
			return;
		}
		Dictionary<Material, Material> swapped = new Dictionary<Material, Material>();
		int count = 0;
		foreach (Renderer r in ground.GetComponentsInChildren<Renderer>(true))
		{
			Material[] mats = r.sharedMaterials;
			bool changed = false;
			for (int i = 0; i < mats.Length; i++)
			{
				Material m = mats[i];
				if (m == null)
				{
					continue;
				}
				if (!swapped.TryGetValue(m, out Material copy))
				{
					copy = null;
					Texture tex = m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
					if (tex != null && s_kwangju.TryGetValue(tex.name, out string path))
					{
						Texture2D neutral = Resources.Load<Texture2D>(path);
						if (neutral != null)
						{
							copy = new Material(m) { name = m.name + " (Ma9M neutral)" };
							copy.SetTexture("_MainTex", neutral);
						}
					}
					swapped[m] = copy;
				}
				if (copy != null)
				{
					mats[i] = copy;
					changed = true;
				}
			}
			if (changed)
			{
				r.sharedMaterials = mats;
				count++;
			}
		}
		Debug.Log("[Ma9MParkLivery] 광주 park for home club " + homeTeam + ": " + count + " renderer(s) without the Tigers signs");
	}
}
