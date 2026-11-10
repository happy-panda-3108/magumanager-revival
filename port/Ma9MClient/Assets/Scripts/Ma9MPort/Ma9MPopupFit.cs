using UnityEngine;

// Ma9M port (AI tester 1, 2026-11-10, F13: "코치 조합 안내 팝업 첫 줄이 제목 띠에 붙음"; the 팀컬러 업적 popup did the same): the
// common popups (Panel_Popup1/Popup2: box 443 × 182, title at y 65, buttons' top at y −44, message centred at y 10, 368 wide)
// hold four lines of the 14-unit message; a longer message ran up into the title band. The message is now made smaller until
// it fits between the title and the buttons (it wraps again at the smaller size), never below MinScale.
public static class Ma9MPopupFit
{
	private const float MaxHeight = 84f;

	private const float BaseScale = 14f;

	private const float MinScale = 10f;

	public static void Fit(UILabel content)
	{
		if (content == null)
		{
			return;
		}
		Transform t = content.transform;
		float scale = BaseScale;
		t.localScale = new Vector3(scale, scale, 1f);
		for (int i = 0; i < 4; i++)
		{
			content.MarkAsChanged();
			float height = content.relativeSize.y * t.localScale.y;
			if (height <= MaxHeight || scale <= MinScale)
			{
				break;
			}
			scale = Mathf.Max(MinScale, scale * MaxHeight / height);
			t.localScale = new Vector3(scale, scale, 1f);
		}
	}
}
