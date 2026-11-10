using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Ma9M port (user, 2026-11-07: "게임 전체적인 가이드를 특정 메뉴마다 (?) 이런 표시의 도움말 팁들을 추가할까 생각 중임"; 2026-11-10 19:0x:
// "12. 포지션 변경에도 (?) 도움말 버튼이 있어서 누르면 팝업 형식으로 포지션 변경 시 적용되는 스탯 변화등의 정보를 제공", "14. 도움말
// 초안의 경우 일단 적용해보고", 20:0x "8. 컨디션에 대한 설명도 추가"): a yellow "(?)" right of a screen's path title ("팀 관리 >
// 계약관리"), and its pages in the common popup, five short lines a page, "다음" to the next. The texts follow the server's
// rules as of 11-10 (ContractHandlers, PositionChangeHandlers, CoachConsult, TeamPropertyHandlers, TeamColors, FusionHandlers,
// GameData.ConditionShare); keep them in step when a rule changes.
public class Ma9MHelp : MonoBehaviour
{
	private static Ma9MHelp s_runner;

	private static readonly string Condition =
		"컨디션은 선수마다 날마다 정해지고 하루 종일 같습니다.\n" +
		"화살표가 위를 볼수록 좋습니다.\n최상 · 상 · 보통 · 나쁨 · 매우 나쁨\n" +
		"경기에서 모든 능력치가 최상 +10%, 상 +5%,\n나쁨 −5%, 매우 나쁨 −10%가 됩니다.";

	private static readonly string ConditionUp =
		"컨디션을 올리는 법\n" +
		"· 컨디션 회복제(상점 기타 탭)\n" +
		"· 면담실 면담: 1~3단계 올라 12시간 갑니다.\n" +
		"계약이 끝난 선수는 1군에 있어도\n매우 나쁨으로 뜁니다(연장하면 보통부터).";

	private static readonly Dictionary<string, Func<string[]>> Pages = new Dictionary<string, Func<string[]>>
	{
		["Scene_Roster"] = () => new[]
		{
			"1군은 타자 14명(주전 9 + 후보 5), 투수 11명입니다.\n" +
			"1군 볼 합계는 리그 상한(Today Ball) 안이어야 하고,\n넘으면 리그 경기에서 전력이 크게 떨어집니다.\n" +
			"상한: 비기너 120 · 루키 130 · 트리플A 140\n메이저 145 · 레전드 150 · 챔피언스 155",
			"선발에서 뺀 투수는 리그 5경기 동안\n다시 선발로 넣을 수 없습니다.\n" +
			"레전드 카드는 1군에 타자 1장, 투수 1장까지입니다.\n" +
			"같은 선수의 카드(레전드 포함)는 1군에 한 장만 됩니다.\n" +
			"수비할 수 없는 자리의 타자는 능력치가 10%,\n보직이 아닌 자리의 투수는 15% 낮아집니다.",
			Condition,
			ConditionUp
		},
		["Scene_ContractManage"] = () => new[]
		{
			"새 카드의 계약은 7일입니다.\n연장하면 7일이 늘어납니다(최대 99일).\n" +
			"계약금은 볼 × 4만 MP입니다.\n" +
			"남은 계약이 " + TableReader.ContractCost.Ma9MSurchargeAfter + "일을 넘을 때 연장하면\n하루마다 계약금이 20%씩 더 붙습니다.",
			"처음 6번 연장할 때마다 보너스 3점을\n원하는 능력치에 나눠 찍습니다(최대 +18).\n" +
			"7번째부터는 능력치는 오르지 않고\n계약일만 늘어납니다.\n" +
			"스탯재분배권으로 보너스 점수를 다시 나눌 수 있습니다.",
			"계약이 끝난 선수는 1군에 있어도\n컨디션 매우 나쁨으로 뜁니다.\n연장하면 보통부터 다시 시작합니다.\n" +
			"방출하면 볼 × 5천 MP를 돌려받습니다.\n(보관함에 25장은 남아야 합니다.)"
		},
		["Scene_PositionChange"] = () => new[]
		{
			"다른 보직으로 실제로 뛴 적이 있는 투수만\n그 보직으로 바꿀 수 있습니다(목록에 나온 선수).\n" +
			"한 번에 40만 MP, 결과는 대성공 40% 또는 성공\n(실패는 없습니다).\n" +
			"바뀐 보직이 경기에서 그 투수의 보직이 됩니다.",
			"카드마다 한 번만 바꿀 수 있습니다.\n" +
			"다시 하려면 포지션 변경 초기화권(상점 80만 MP)으로\n원래 보직·능력치로 되돌린 뒤 합니다.\n" +
			"레전드도 목록에 나오면 바꿀 수 있습니다.\n" +
			"바뀐 능력치는 다음 쪽에 있습니다.",
			"능력치 변화 (중계·셋업·마무리 → 선발)\n" +
			"성공: 체력 +20, 구속 −2, 나머지 −1\n" +
			"대성공: 체력 +20, 구속 −1, 나머지 +1\n" +
			"나머지 = 컨트롤 · 테크닉 · 구위 · P정신",
			"능력치 변화 (선발 → 중계·셋업·마무리)\n" +
			"성공: 체력 −25, 구속 +1, 나머지 −1\n" +
			"대성공: 체력 −25, 구속 +2, 나머지 +1\n" +
			"불펜끼리(예: 마무리 → 중계)\n성공: 체력 빼고 모두 −1 / 대성공: 컨트롤·테크닉·구위 +1"
		},
		["Scene_CoachingStaff"] = () => new[]
		{
			"면담실: 면담 시간은 0시~12시, 12시~24시입니다.\n" +
			"수석코치 구단(계보)의 1군 선수 중 컨디션이\n나쁨·매우 나쁨인 선수가 찾아옵니다(후보 제외).\n" +
			"한 시간대에 수석코치 등급만큼 옵니다.\n(노말 2 · 스페셜 3 · 레어 4 · 엘리트 5명)",
			"면담은 SP 150이 듭니다.\n끝나면 컨디션이 1~3단계 올라 12시간 갑니다.\n" +
			"면담 효과가 남은 선수와 계약이 끝난 선수는\n오지 않습니다.\n" +
			"면담실은 열 때마다 빈자리를 채웁니다.",
			Condition
		},
		["Scene_TeamProperty"] = () => new[]
		{
			"리그 등급이 오를수록 쓸 수 있는 CP가 늘어납니다.\n" +
			"타자·투수 특성의 줄은 바로 아래 줄에 점수가\n쌓여야 열립니다.\n" +
			"둘째 줄: 첫째 줄에 6점, 셋째 줄: 둘째 줄에 4점\n넷째 줄: 셋째 줄에 4점, 다섯째 줄: 넷째 줄에 3점",
			"\"(OO 마스터 필요)\" 특성은 그 특성을\n최대까지 올려야 찍을 수 있습니다.\n" +
			"강등되어 CP가 줄면 마지막에 넣은 특성부터 빠집니다.\n" +
			"쓰지 않은 CP가 있으면 메뉴에 붉은 점이 뜹니다."
		},
		["Scene_TeamColor"] = () => new[]
		{
			"1군 구성에 따라 팀컬러가 생기고,\n해당하는 선수의 능력치가 오릅니다.\n" +
			"칸은 셋입니다: 전체(1군 25명), 타자진(14명), 투수진(11명).\n" +
			"같은 연도·같은 구단이 많을수록 강합니다.",
			"강한 순서\n1. 단일 년도 팀  2. 단일 년도 듀얼 팀\n3. 듀얼 년도 단일 팀  4. 연대 단일 팀\n" +
			"5. 단일 년도 올스타  6. 단일 팀 올스타\n7. 듀얼 팀 올스타",
			"등급(S·A·B·C)은 조건에 맞는 인원으로 정해집니다.\n" +
			"전체: S 25명 · A 19 · B 14 · C 11\n타자진: S 14 · A 11 · B 8 · C 6\n투수진: S 11 · A 8 · B 6 · C 5\n" +
			"레전드는 연도와 상관없이 자기 구단(계보)에 들어갑니다."
		},
		["Scene_Combine"] = () => new[]
		{
			"넣은 카드 중 볼이 가장 높은 카드가 기준 카드이고,\n나머지는 재료입니다(2~8장).\n" +
			"재료 볼 합계가 기준 볼 × 7이면 가득입니다.\n(예: 8볼 기준에 재료 56볼)\n" +
			"결과는 기준 카드와 같은 쪽(타자/투수) 무작위입니다.",
			"재료가 적으면 한 단계 내려갈 수 있고,\n많을수록 오를 확률이 큽니다(한 번에 최대 +2볼).\n" +
			"엘리트는 레어·엘리트 기준 카드에서만 나옵니다.\n" +
			"보호 아이템을 쓰면 결과가 낮을 때\n기준 카드가 남습니다.",
			"레전드 조합: 레전드 1장 + 다른 카드 7장까지.\n" +
			"다른 카드 볼 합계 60볼이면 100% 보존\n(1볼에 1.67%). 보존되면 없는 레전드 중 하나.\n" +
			"실패하면 같은 쪽 10볼 엘리트가 나옵니다.\n레전드 보호 아이템을 쓰면 실패해도 레전드가 남습니다."
		},
		["Scene_Unconfirmed"] = () => new[]
		{
			"리그 경기는 20분마다 한 경기씩 열립니다.\n" +
			"경기 MP와 홈런볼은 결과를 확인할 때 들어옵니다.\n" +
			"\"모두 확인\"은 이 화면에 나온 경기를\n한 번에 확인합니다(그 뒤 끝난 경기는 다음에).",
		}
	};

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
	private static void Boot()
	{
		if (s_runner != null)
		{
			return;
		}
		GameObject go = new GameObject("Ma9MHelp");
		DontDestroyOnLoad(go);
		s_runner = go.AddComponent<Ma9MHelp>();
		SceneManager.sceneLoaded += (scene, mode) => s_runner.OnScene(scene.name);
	}

	private void OnScene(string scene)
	{
		if (Pages.ContainsKey(scene))
		{
			StartCoroutine(Attach(scene));
		}
	}

	// The path title appears with the screen; look for it for a few seconds.
	private IEnumerator Attach(string scene)
	{
		for (int tries = 0; tries < 20; tries++)
		{
			yield return new WaitForSeconds(0.25f);
			if (SceneChanger.GetLoadedLevelName() != scene)
			{
				yield break;
			}
			UILabel title = FindTitle();
			if (title == null)
			{
				continue;
			}
			if (title.transform.parent != null && title.transform.parent.Find("Ma9MHelpButton") != null)
			{
				yield break;
			}
			MakeButton(title, scene);
			yield break;
		}
	}

	// The highest visible label whose text is a path ("팀 관리 > 계약관리").
	private static UILabel FindTitle()
	{
		UILabel best = null;
		foreach (UILabel label in UnityEngine.Object.FindObjectsByType<UILabel>(FindObjectsSortMode.None))
		{
			if (!label.gameObject.activeInHierarchy || string.IsNullOrEmpty(label.text) || !label.text.Contains(" > ") || label.text.Contains("\n"))
			{
				continue;
			}
			if (best == null || label.transform.position.y > best.transform.position.y)
			{
				best = label;
			}
		}
		return best;
	}

	private static void MakeButton(UILabel title, string scene)
	{
		GameObject go = UnityEngine.Object.Instantiate(title.gameObject) as GameObject;
		go.name = "Ma9MHelpButton";
		go.transform.parent = title.transform.parent;
		go.transform.localScale = title.transform.localScale;
		go.transform.localRotation = title.transform.localRotation;
		UILocalize localize = go.GetComponent<UILocalize>();
		if (localize != null)
		{
			UnityEngine.Object.Destroy(localize);
		}
		UILabel q = go.GetComponent<UILabel>();
		q.text = "(?)";
		q.color = new Color(1f, 0.82f, 0f);
		// Right of the title: its bounds in the parent, the button's left edge 10 units past them.
		Bounds titleBounds = NGUIMath.CalculateRelativeWidgetBounds(title.transform.parent, title.transform);
		go.transform.localPosition = title.transform.localPosition;
		Bounds own = NGUIMath.CalculateRelativeWidgetBounds(title.transform.parent, go.transform);
		go.transform.localPosition += new Vector3(titleBounds.max.x + 10f - own.min.x, 0f, 0f);
		NGUITools.AddWidgetCollider(go);
		go.AddComponent<Ma9MHelpButton>().Scene = scene;
		go.SetActive(true);
	}

	public static void Show(string scene)
	{
		Func<string[]> pages;
		if (Pages.TryGetValue(scene, out pages))
		{
			ShowPage(pages(), 0);
		}
	}

	private static void ShowPage(string[] pages, int i)
	{
		string title = pages.Length > 1 ? "도움말 (" + (i + 1) + "/" + pages.Length + ")" : "도움말";
		if (i < pages.Length - 1)
		{
			PopupManager.ViewPopup2(title, pages[i], "다음", "닫기", () => ShowPage(pages, i + 1), null);
		}
		else
		{
			PopupManager.ViewPopup1(title, pages[i], "닫기", null);
		}
	}
}

// The "(?)" label's click (NGUI sends OnClick to the collider's object).
public class Ma9MHelpButton : MonoBehaviour
{
	public string Scene;

	private void OnClick()
	{
		Ma9MHelp.Show(Scene);
	}
}
