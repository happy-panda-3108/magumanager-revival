// Ma9M port: settings of this version. 룰렛연출 (user, 2026-10-19: the fusion and MP주머니 roulettes take
// too long when seen often) lives in the settings screen's 친선초대 toggle, unused in the solo game
// (Korean.txt Setting_Right_Bottom_SubTitle3 renamed); on by default like that toggle.
// 2026-10-24 the toggle became 카드연출 (user: "이런 애니메이션 모션을 끌 수 있는 기능도 옵션에 … 트레이드 할 때 가끔 빠르게
// 끝내고 싶은데 이펙트 계속 나오면 오래걸림"): off also drops the slow result effects of a combine and of high cards
// in a pack (CardEffects).
// 2026-11-12 (user 11:5x 12번: "MP주머니는 그냥 연출을 스킵하는게 디폴트이고, 조합 연출이랑 카드 연출을 별도로 두고 싶음. 조합 룰렛은
// 가끔 보면 재밌는데(레전드 보존 조합), 그 외는 너무 피곤하고 기다리기 오래걸림. 카드 연출은 이제 대량 팩을 깔 때 빠르게 깔 수 있기
// 때문에 생략 안 하고 보는게 더 좋을 것 같음"): two settings.
// - 카드연출 (the 친선초대 toggle, Setting_FriendlyMatchInvite, on by default): a pack's high cards (ShopInventory_CardOpen,
//   Ma9MCardSpread).
// - 조합연출 (a row of its own in the 설정 column's empty fifth place, Setting_Main.Ma9MAddCombineToggle; key Ma9M_CombineEffects,
//   off by default): the combine roulette and result effects (Combine_MixResult) and the MP주머니 roulette (Panel_Luckypocket). A
//   레전드 combine's roulette shows either way. (The first try took 중계옵션's 진동 row, which is the home-run vibration's; undone.)
// The first run of this version turns 카드연출 on once (it also meant the combines until now).
public static class Ma9MPortSettings
{
	public const string CombineKey = "Ma9M_CombineEffects";

	private const string SplitKey = "Ma9M_EffectsSplit";

	private static bool s_migrated;

	private static void Migrate()
	{
		if (s_migrated)
		{
			return;
		}
		s_migrated = true;
		if (DataManager.GetData_String(SplitKey) != null)
		{
			return;
		}
		DataManager.PutData_String("Setting_FriendlyMatchInvite", "True");
		DataManager.PutData_String(CombineKey, "False");
		DataManager.PutData_String(SplitKey, "1");
		DataManager.Save();
	}

	// 카드연출: a pack's high-card effects.
	public static bool CardEffects
	{
		get
		{
			Migrate();
			string value = DataManager.GetData_String("Setting_FriendlyMatchInvite");
			return value == null || value.CompareTo("True") == 0;
		}
	}

	// 조합연출: the combine roulette and result effects, the MP주머니 roulette.
	public static bool CombineEffects
	{
		get
		{
			Migrate();
			return DataManager.GetData_String(CombineKey) == "True";
		}
	}

	// The MP주머니 roulette (and other roulettes that follow 조합연출).
	public static bool ShowRoulette
	{
		get
		{
			return CombineEffects;
		}
	}

	// A combine's roulette: with 조합연출 on, or when a 레전드 (card 800000-809999) is among the candidates.
	public static bool ShowCombineRoulette(System.Collections.Generic.IList<int> candidates)
	{
		if (CombineEffects)
		{
			return true;
		}
		if (candidates == null)
		{
			return false;
		}
		for (int i = 0; i < candidates.Count; i++)
		{
			if (candidates[i] >= 800000 && candidates[i] < 810000)
			{
				return true;
			}
		}
		return false;
	}
}
