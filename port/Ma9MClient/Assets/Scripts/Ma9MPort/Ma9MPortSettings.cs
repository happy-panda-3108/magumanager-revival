// Ma9M port: settings of this version. 룰렛연출 (user, 2026-10-19: the fusion and MP주머니 roulettes take
// too long when seen often) lives in the settings screen's 친선초대 toggle, unused in the solo game
// (Korean.txt Setting_Right_Bottom_SubTitle3 renamed); on by default like that toggle.
// 2026-10-24 the toggle became 카드연출 (user: "이런 애니메이션 모션을 끌 수 있는 기능도 옵션에 … 트레이드 할 때 가끔 빠르게
// 끝내고 싶은데 이펙트 계속 나오면 오래걸림"): off also drops the slow result effects of a combine and of high cards
// in a pack (CardEffects).
public static class Ma9MPortSettings
{
	public static bool ShowRoulette
	{
		get
		{
			string value = DataManager.GetData_String("Setting_FriendlyMatchInvite");
			return value == null || value.CompareTo("True") == 0;
		}
	}

	public static bool CardEffects
	{
		get
		{
			return ShowRoulette;
		}
	}
}
