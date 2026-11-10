using UnityEngine;
using UnityEngine.Rendering;

// broadcast scene's sun and ambient light to suit it when the stadium is spawned. The 마구돌이 use
// their own fixed key light (Ma9/MA9Shader), so this only changes how the stadium itself is lit.
public class Ma9MStadiumLighting : MonoBehaviour
{
	public Vector3 sunEuler = new Vector3(50f, 75f, 0f);

	public Color sunColor = new Color(1f, 0.97f, 0.92f);

	public float sunIntensity = 1.05f;

	public Color ambientSky = new Color(0.56f, 0.6f, 0.66f);

	public Color ambientEquator = new Color(0.5f, 0.49f, 0.47f);

	public Color ambientGround = new Color(0.32f, 0.3f, 0.27f);

	private void Start()
	{
		Apply();
	}

	public void Apply()
	{
		RenderSettings.ambientMode = AmbientMode.Trilight;
		RenderSettings.ambientSkyColor = ambientSky;
		RenderSettings.ambientEquatorColor = ambientEquator;
		RenderSettings.ambientGroundColor = ambientGround;
		Light sun = RenderSettings.sun;
		if (sun == null)
		{
			foreach (Light l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
			{
				if (l.type == LightType.Directional)
				{
					sun = l;
					break;
				}
			}
		}
		if (sun != null)
		{
			sun.transform.rotation = Quaternion.Euler(sunEuler);
			sun.color = sunColor;
			sun.intensity = sunIntensity;
		}
	}
}
