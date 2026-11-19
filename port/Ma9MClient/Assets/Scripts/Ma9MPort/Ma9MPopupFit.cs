using UnityEngine;

// Ma9M port (AI tester 1, 2026-11-10, F13: "코치 조합 안내 팝업 첫 줄이 제목 띠에 붙음"; the 팀컬러 업적 popup did the same): the
// common popups (Panel_Popup1/Popup2: box 443 × 182, title at y 65, buttons' top at y −44, message centred at y 10) hold four
// lines of the 14-unit message; a longer message ran up into the title band.
// 11-10 to 11-17 the message was made smaller until it fit (down to 10). 11-17 (user 18:1x: "글씨 크기를 조절하기 보다는 아래로 내리는
// 스크롤 같은 기능이 있으면 좋겠음"): it keeps the body size and wraps at the box; a longer one grows the popup, and scrolls past the
// tallest popup (Ma9MTextBox.FitPopup).
public static class Ma9MPopupFit
{
	// 2026-11-19 (user 21:3x, 11-18 question 8 — AI tester LG 108: the FA 시장 help's pages were boxes of different heights, so [다음]
	// stood at y 288 on pages 1-4 and 306 on page 5 and a tap in the same place missed): "가장 긴 쪽의 높이를 맞춥시다. 항상 같은 칸에
	// 버튼이 있게끔". A help (Ma9MHelp.ShowPage) names its pages before it opens a page; a popup showing one of them is sized for the
	// tallest, so every page's box and buttons stand in the same place.
	public static string[] Pages;

	public static void Fit(UILabel content)
	{
		string[] pages = Pages;
		if (pages != null && pages.Length > 1 && content != null && System.Array.IndexOf(pages, content.text) >= 0)
		{
			Ma9MTextBox.FitPopupTallest(content, pages, content.text);
			return;
		}
		Ma9MTextBox.FitPopup(content);
	}
}
