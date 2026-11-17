using UnityEngine;

// Ma9M port (AI tester 1, 2026-11-10, F13: "코치 조합 안내 팝업 첫 줄이 제목 띠에 붙음"; the 팀컬러 업적 popup did the same): the
// common popups (Panel_Popup1/Popup2: box 443 × 182, title at y 65, buttons' top at y −44, message centred at y 10) hold four
// lines of the 14-unit message; a longer message ran up into the title band.
// 11-10 to 11-17 the message was made smaller until it fit (down to 10). 11-17 (user 18:1x: "글씨 크기를 조절하기 보다는 아래로 내리는
// 스크롤 같은 기능이 있으면 좋겠음"): it keeps the body size and wraps at the box; a longer one grows the popup, and scrolls past the
// tallest popup (Ma9MTextBox.FitPopup).
public static class Ma9MPopupFit
{
	public static void Fit(UILabel content)
	{
		Ma9MTextBox.FitPopup(content);
	}
}
