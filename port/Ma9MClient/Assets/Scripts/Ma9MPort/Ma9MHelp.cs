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
		"컨디션은 선수마다 날마다 정해지고,\n저절로는 하루 동안 바뀌지 않습니다.\n" +
		"화살표가 위를 볼수록 좋습니다.\n최상 · 상 · 보통 · 나쁨 · 매우 나쁨\n" +
		"경기에서 모든 능력치가 최상 +10%, 상 +5%,\n나쁨 −5%, 매우 나쁨 −10%가 됩니다.";

	private static readonly string ConditionUp =
		"컨디션을 올리는 법\n" +
		"· 컨디션 회복제(상점 기타 탭)\n" +
		"· 면담실 면담: 1~3단계 올라 12시간 갑니다.\n" +
		"계약이 끝난 선수는 1군에 있어도\n매우 나쁨으로 뜁니다(연장하면 보통부터).";

	// 2026-11-13 (user 16:5x 결정 5: "TP는 최초 6개 있고, TP 충전권을 상점에서 10만 MP로 사야한다는 것도 도움말로 적어야겠네"; server
	// TrainingStore.TpCharges 6, ShopHandlers 202 트레이닝 횟수 충전권 100,000 MP). The training tab is a panel of the player window,
	// which has no path title of its own, so the page goes on the screens that lead there.
	private static readonly string TrainingPoint =
		"트레이닝(선수 정보 > 트레이닝)은 카드마다\nTRAINING POINT 6칸으로 시작해 한 번에 1칸씩 씁니다.\n" +
		"다 쓰면 저절로 다시 차지 않습니다.\n" +
		"상점 트레이닝 탭의 트레이닝 횟수 충전권(10만 MP)을\n트레이닝 화면의 아이템 버튼으로 쓰면 6칸이 다시 찹니다.";

	private static readonly Dictionary<string, Func<string[]>> Pages = new Dictionary<string, Func<string[]>>
	{
		["Scene_Roster"] = () => new[]
		{
			"1군은 타자 14명(주전 9 + 후보 5), 투수 11명입니다.\n" +
			"1군 볼 합계는 리그 상한(Today Ball) 안이어야 하고,\n넘으면 리그 경기에서 전력이 크게 떨어집니다.\n" +
			// 2026-11-14 (user: 더블A 140, 트리플A부터 +5; server LeagueRules.BallCap7 from the 11-16 season).
			"상한: 비기너 120 · 루키 130 · 더블A 140\n트리플A 145 · 메이저 150 · 레전드 155 · 챔피언스 160",
			"선발에서 뺀 투수는 리그 5경기 동안\n다시 선발로 넣을 수 없습니다.\n" +
			// 2026-11-15 FA (server RosterHandlers.FaProblem: one FA card, one card of a player).
			"레전드는 1군에 타자·투수 1장씩, FA는 1장까지입니다.\n" +
			"같은 선수의 카드(레전드·FA)는 1군에 한 장만 됩니다.\n" +
			"수비할 수 없는 자리의 타자는 능력치가 10%,\n보직이 아닌 자리의 투수는 15% 낮아집니다.",
			// AI 테스터 불편 6·61 (타순 바꾸는 법을 1~5회차에 못 찾음).
			"타순 바꾸기: 타순 줄을 1초쯤 꾹 누른 채\n다른 줄로 끌면 두 줄의 타순이 맞바뀝니다\n(수비 자리는 그대로).\n" +
			// 2026-11-13 (AI tester NC 8회차: 선발 순서를 끌어 바꿀 수 있다는 안내가 없음; Roster_Common_Processor.Ma9MNoNextStarter).
			"선발 순서: 다음 경기가 없을 때만 선발끼리 끌어 바꿈.\n" +
			"다 바꾼 뒤 \"등록하기\"를 눌러야 저장됩니다.",
			// 2026-11-12 (user 10:4x, server AutoLineup Locked).
			"자동 편성: 가진 카드로 볼 상한 안에서\n가장 강한 1군을 계산해 바뀌는 선수를 보여 줍니다.\n" +
			"선수 정보 창에서 선수보호를 건 1군 선수는\n자동 편성이 빼거나 자리를 옮기지 않습니다\n(타순은 바뀔 수 있음).",
			// 2026-11-13 (user 16:5x 결정 11 (가): 로스터 거르기를 카드 목록 방식으로, "길게 누르면 거르기" 안내; Ma9MRosterPicks).
			"보유선수·선수교체 목록 위 버튼\n짧게 누르면 정렬(한 번 더 누르면 반대로),\n" +
			"길게 누르면 거르기: 여러 값 고르기·제외·모두 해제.\n" +
			"거르기는 버튼끼리 함께 걸리고(예: LG + 1~2볼),\n바로 전 정렬이 같은 묶음 안의 2순위가 됩니다.",
			Condition,
			ConditionUp,
			TrainingPoint
		},
		["Scene_ContractManage"] = () => new[]
		{
			// AI 테스터 두산 불편 (11-11): 카드 왼쪽 아래 숫자가 무엇인지, 7일이 실제 날인지.
			"새 카드의 계약은 7일입니다(0시마다 하루씩 줄어듦).\n카드 왼쪽 아래 숫자가 남은 계약일입니다.\n연장하면 7일이 늘어납니다(최대 99일).\n" +
			"계약금은 볼 × 4만 MP입니다.\n" +
			"남은 계약이 " + TableReader.ContractCost.Ma9MSurchargeAfter + "일을 넘을 때 연장하면\n하루마다 계약금이 20%씩 더 붙습니다.",
			"처음 6번 연장할 때마다 보너스 3점을\n원하는 능력치에 나눠 찍습니다(최대 +18).\n" +
			"7번째부터는 능력치는 오르지 않고\n계약일만 늘어납니다.\n" +
			"스탯재분배권으로 보너스 점수를 다시 나눌 수 있습니다.",
			"계약이 끝난 선수는 1군에 있어도\n컨디션 매우 나쁨으로 뜁니다.\n연장하면 보통부터 다시 시작합니다.\n" +
			"방출하면 볼 × 5천 MP를 돌려받습니다.\n(보관함에 25장은 남아야 합니다.)",
			// 2026-11-15 FA (user 18:3x·18:46 via the engine session; server ContractHandlers.FaCost / FaBonusPerRenewal).
			"FA 선수: 영입하면 계약 14일, 연장도 14일씩.\n연장마다 보너스 5점(처음 6번).\n" +
			"계약금: 1·2·3번째 연장은 영입가의 110·120·130%,\n4번째부터 영입가의 52%로 고정.\n" +
			"(8볼 1,650만 · 1,800만 · 1,950만 → 780만 MP)"
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
			// AI 테스터 두산·NC (11-11): 코치를 어디서 얻는지, 세트 보너스, "북 조합" 버튼이 없음. CoachCatalog (PackGradeOdds,
			// TrainingChance, TopMixBooks, CheerPercent, SetBonus, SP), CoachingStaff_Main.OnBtn_TodayPlayers.
			"코치 카드는 상점 > 트레이닝 탭의\n코칭스태프 카드팩(30만 MP)에서 나옵니다.\n" +
			// 2026-11-13 (user): 자팀(1군에 가장 많은 계보) 코치 20% (CoachCatalog.OwnLinePercent).
			"(노말 83% · 스페셜 14.5% · 레어 2% · 엘리트 0.5%,\n1군에 가장 많은 구단(계보)의 코치가 20%)\n" +
			"칸: 수석 · 타격 · 투수 · 트레이닝코치 · 치어리더",
			"코치는 자기 구단(계보) 선수에게만 일합니다.\n" +
			"수석코치: 면담실에 선수를 보냅니다(뒤쪽).\n" +
			// 2026-11-12 (user): not on the lowest square. 2026-11-13: a missed top square makes the next top square certain.
			"타격·투수코치: 트레이닝에 나와(노말 30% ~ 엘리트 60%,\n가장 낮은 칸엔 안 나오고, 가장 높은 칸을 놓치면\n다음 가장 높은 칸엔 꼭) SP 50으로 결과 한 칸 위로.\n" +
			"치어리더: 리그 경기 MP +3% · 6% · 10% · 15%",
			"트레이닝코치: \"북 조합\" 버튼(트레이닝북 조합)에서\nIII 트레이닝북 5·4·3·2권으로 IV 1권을 만들고,\n그 구단 선수의 트레이닝 결과를 좋게 합니다.\n" +
			"다섯 칸이 모두 한 구단(계보)이면 세트 보너스로\n그 구단 선수 능력치가 오릅니다(가장 낮은 등급 기준\n스페셜 +1 · 레어 +2 · 엘리트 +3, 노말은 없음).",
			"SP: 접속 +50, 리그 승리 +30 (최대 2,000)\n" +
			"코치 카드 4장(엘리트 3장)으로 한 등급 위 카드를\n만들 수 있습니다(전력강화 > 코칭스태프 조합).\n" +
			// 2026-11-14 (user: "코치는 중복해서 등장할 수 있다고 도움말에 적혀있지 않나?"; CoachCatalog: copies allowed since 11-05).
			"코치는 100장까지 가질 수 있고, 선수 카드와 달리\n같은 코치가 또 나올 수 있습니다(팩·조합 모두).",
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
			"타자·투수 특성의 줄은 바로 위 줄에 점수가\n쌓여야 열립니다(첫째 줄이 맨 위).\n" +
			"둘째 줄: 첫째 줄에 6점, 셋째 줄: 둘째 줄에 4점\n넷째 줄: 셋째 줄에 4점, 다섯째 줄: 넷째 줄에 3점",
			"\"(OO 마스터 필요)\" 특성은 그 특성을\n최대까지 올려야 찍을 수 있습니다.\n" +
			"강등되어 CP가 줄면 마지막에 넣은 특성부터 빠집니다.\n" +
			"쓰지 않은 CP가 있으면 팀 관리 메뉴의 특성관리에\n빨간 N 표시가 뜹니다."
		},
		["Scene_TeamColor"] = () => new[]
		{
			"1군 구성에 따라 팀컬러가 생기고,\n해당하는 선수의 능력치가 오릅니다.\n" +
			"칸은 셋입니다: 전체(1군 25명), 타자진(14명), 투수진(11명).\n" +
			"같은 연도·같은 구단이 많을수록 강합니다.",
			"강한 순서\n1. 단일 년도 팀  2. 단일 년도 듀얼 팀\n3. 듀얼 년도 단일 팀  4. 연대 단일 팀\n" +
			"5. 단일 년도 올스타  6. 단일 팀 올스타\n7. 듀얼 팀 올스타",
			// 2026-11-12 (user 11:5x 9·11): 연대 steps of its own, each dual team 40% (TeamColors.DecadeNeeded, DualEach).
			"듀얼 팀: 같은 해의 두 구단, 두 구단이 각각\n조건 인원의 40% 이상(25명이면 10명씩)\n" +
			"듀얼 년도: 한 구단의 두 해\n연대: 한 구단의 같은 연대(예: 2000~2009)",
			"등급(S·A·B·C)은 조건에 맞는 인원으로 정해집니다.\n" +
			"전체: S 25명 · A 19 · B 14 · C 11\n타자진: S 14 · A 11 · B 8 · C 6\n투수진: S 11 · A 8 · B 6 · C 5\n" +
			// 2026-11-15 19:4x (user: "년도 조건을 타는 조커 카드"): an FA card joins any club's colour of its own season or decade
			// (server TeamColors.IsMember), and gets its bonus.
			"레전드는 자기 구단 모든 해, FA는 자기 해 모든 구단.",
			"연대 단일 팀만 단계가 따로입니다.\n" +
			"전체: S 25명 · A 22 · B 19 · C 14\n타자진: S 14 · A 12 · B 11 · C 8\n투수진: S 11 · A 10 · B 8 · C 6\n" +
			"효과는 A가 S의 70%, B 50%, C 30%입니다.",
			// AI 테스터 불편 3·61 (넣는 법과 효과 크기가 없음), TeamColors.Kinds / GradeShare.
			// 2026-11-11 (AI 테스터 kt 4회차): "저절로 걸리지 않는다"는 틀림. 11-09부터 자동 등록(TeamColors.AutoRegister).
			// Five short lines (2026-11-12, engine screen check: two long lines shrank the whole page's text).
			"1군을 등록하면 칸마다 가장 강한 팀컬러가\n저절로 등록됩니다. 다른 것을 쓰려면\n" +
			"목록의 칸을 두 번 눌러 고른 뒤 등록하세요\n(직접 고른 것은 조건을 채우는 동안 그대로).\n" +
			// 2026-11-13 (user 16:5x 결정 4): hints from a third of the members (server TeamColors.Hints).
			"보라색 = 채움 · 회색 = 3분의 1 이상 모임",
			// Five lines (2026-11-12, engine screen check: six made it the smallest page).
			"S 등급 효과(해당 선수의 모든 능력치 +)\n" +
			"전체 칸: 단일 년도 팀 15 · 듀얼 팀 15 · 듀얼 년도 14\n연대 7 · 년도 올스타 3 · 팀 올스타 2 · 듀얼 팀 올스타 1\n" +
			"타자진·투수진 칸: 6·4·4·3·2·1·1 (같은 순서)\n" +
			// 2026-11-13 (AI tester LG 79: page 5's 70/50/30% and these differ with no word why; about as wide as before).
			"A는 S의 45%·B 25%·C 15%(연대 단일 팀만 5쪽), 칸끼리 더함."
		},
		["Scene_Combine"] = () => new[]
		{
			"넣은 카드 중 볼이 가장 높은 카드가 기준 카드이고,\n나머지는 재료입니다(2~8장).\n" +
			"재료 볼 합계가 기준 볼 × 7이면 가득(오를 확률이 가장 큼)입니다.\n(예: 8볼 기준에 재료 56볼)\n" +
			"결과는 기준 카드와 같은 쪽(타자/투수) 무작위입니다.",
			"재료가 적으면 한 단계 내려갈 수 있고,\n많을수록 오를 확률이 큽니다(한 번에 최대 +2볼).\n" +
			"엘리트(확률 줄의 9엘·10엘)는 레어·엘리트 기준에서만.\n" +
			"보호 아이템을 쓰면 결과가 낮을 때\n기준 카드가 남습니다.",
			// 2026-11-11 레어 기준 조합 강화 (server FusionRules.Table 7·8; user "이대로 적용합시다").
			"기준 카드가 안 내려가는 재료 볼 합계:\n" +
			"노멀·스페셜·엘리트는 가득의 35%\n(예: 7볼 18볼, 10볼 엘리트 25볼)\n" +
			"레어는 가득의 65%(8볼 37볼, 9볼 41볼).\n" +
			"레어에서 엘리트는 재료가 많을수록 잘 나옵니다.",
			"레전드 조합: 레전드 1장 + 다른 카드 7장까지.\n" +
			"다른 카드 볼 합계 60볼이면 100% 성공(1볼에 1.67%).\n성공하면 아직 없는 레전드 한 장으로 바뀝니다.\n" +
			"실패하면 같은 쪽 10볼 엘리트가 나옵니다.\n레전드 보호 아이템을 쓰면 실패해도 레전드가 남습니다.",
			// 2026-11-15 (server FusionHandlers: lesson_players = 특별수강 + FA; the app leaves out the 1군 itself).
			"1군 선수, 특별수강 중인 선수,\nFA 선수는 조합 목록에 나오지 않습니다.\n(FA 카드는 조합할 수 없습니다.)"
		},
		// 2026-11-15 FA 시장 (user 15:4x via the engine session; server FaMarket, AlbumHandlers, RosterHandlers.FaProblem,
		// TeamColors, coach lines by HomeTeam). The album is a page of 구단정보 ("기록실>구단정보", TitleHints).
		["Scene_TeamInfo"] = () => new[]
		{
			"앨범: 가져 본 적 있는 카드는 색으로,\n아직 없는 카드는 흑백으로 보입니다.\n" +
			"연도 버튼 > 등급별: 10볼 엘리트 · 9볼 엘리트 ·\n9볼 레어 · 8볼 레어 · 레전드 · FA를\n시즌·구단 없이 한 번에 봅니다(정렬·거르기).",
			"FA 선수: 8·9볼 레어 카드의 FA 판입니다.\n능력치는 원래 카드와 같고, 팀컬러는\n구단 상관없이 자기 해·연대 묶음에 듭니다.\n" +
			"1군에는 FA 1장까지, 원래 카드와 함께는 안 됩니다.\n카드팩·조합·시범경기 보상에는 나오지 않습니다.",
			"FA 영입: 등급별 > FA나 시즌별 FA 구단에서\n카드를 누르면 계약금과 남는 MP가 나오고,\n" +
			"[영입]이면 계약 14일로 들어옵니다(같은 FA는 한 장).\n꾹 누르면 선수 정보를 봅니다.\n" +
			"코치 계보는 원래 카드의 구단으로 칩니다."
		},
		// AI 테스터 불편 59 (2026-11-11): 특별강좌·코치카드조합에 도움말이 없음. LessonHandlers, CoachCatalog (MixCards), ShopHandlers 가격.
		["Scene_SpecialLecture"] = () => new[]
		{
			"타격 코치 1명(첫 쪽)과 투수 코치 1명(옆으로 넘김)이\n매일 0시에 바뀝니다(KBO 감독 22명이 차례로).\n" +
			"코치마다 나이 조건(25세 이하 · 26~32세 · 33세 이상,\n또는 용병만)에 맞는 그 쪽 선수만 받습니다.",
			// 2026-11-12 (user 10:4x): a 1군 card turns the 즉시 완료권 on; a coach's +5 takes the player off his list.
			"한 번에 6명까지. 1군 선수는 즉시 완료권과 함께만\n(1군 카드를 고르면 즉시 완료권이 저절로 켜짐).\n" +
			"수강료: 1~4볼 8만 · 5~7볼 20만 · 8~9볼 40만 · 10볼 80만\n" +
			"18시간 뒤 주 능력치 +4(70%) 또는 +5(30%),\n보조 능력치 둘 +2씩. 수강 중에는 1군·조합·방출 불가.",
			"결과는 카드마다 하나입니다.\n다시 들으려면 특별강좌 초기화권(100만 MP).\n" +
			"그 코치에게 +5를 받은 선수는\n그 코치 목록에 다시 나오지 않습니다.\n" +
			"취소해도 수강료는 돌려받지 않습니다.",
			"행운권(40만): +5 확률 30% → 60%.\n" +
			"즉시 완료권(300만): 신청할 때 쓰거나,\n수강 중인 선수 카드를 눌러 바로 끝냅니다."
		},
		["Scene_CoachCardCombine"] = () => new[]
		{
			"같은 등급 코치 카드 4장을 넣으면\n한 등급 위 카드 1장이 나옵니다(스태프에 있는 카드는 못 넣음).\n" +
			"같은 카드 4장: 그 카드의 한 등급 위\n같은 계보 4장: 그 계보의 한 등급 위 카드\n그 밖의 4장: 무작위 한 등급 위 카드(가진 코치도 나옴)",
			// 2026-11-13 (user): three 엘리트 타격코치 (투수코치) of one line → the same coach, its stat drawn again (CoachMix).
			// 2026-11-14 (user: 정신·정신·스피드 gave 스피드 again): the new stat is one the three did not have (CoachCatalog.RollOtherStat).
			"엘리트는 3장으로 새 엘리트 1장을 받습니다\n(같은 계보 3장이면 그 계보의 엘리트,\n같은 계보 타격·투수코치 3장은 넣은 카드에 없던 능력치로).\n" +
			"계보는 이름이 바뀐 구단을 한 구단으로 봅니다\n(예: 해태·KIA, OB·두산, 빙그레·한화)."
		},
		// AI 테스터 NC (11-11): 작전실·순위표에 (?)가 없음. sim TacticRules (Tactics.cs), LeagueState.Ranked, LeagueRules.NextTier.
		["Scene_OperationRoom"] = () => new[]
		{
			"막대는 0~100, 가운데 50이 보통이고\n끝으로 갈수록 효과가 커집니다.\n" +
			"타자 작전 5개 · 투수 작전 5개가 팀 전체에 걸립니다.\n" +
			"선수 정보 창 > 개인작전에서 한 선수만 따로 정하면\n그 선수는 작전실을 바꿔도 자기 값대로 뜁니다(수비·교체는 팀만).",
			"타격: 빅볼은 홈런과 삼진이 함께 늘어\n장타력 높은 타자에게 맞습니다.\n스몰볼은 공을 맞히는 쪽입니다(번트도 늘어남).\n" +
			"공격: 끈질긴 승부는 공을 많이 봐 볼넷·삼진이 늘고\n상대 투수가 빨리 지칩니다.",
			"도루: 적극적일수록 더 자주 뜁니다.\n" +
			"번트: 권장일수록 경기 후반 접전에서\n약한 타자부터 희생번트를 댑니다.\n" +
			"수비: 적극적은 안타를 더 막고(수비 좋을수록)\n실책도 늘어납니다.",
			"투수 성향: 직구 위주는 투구 수와 볼넷이 줄어듭니다.\n" +
			"승부: 유인구는 삼진·볼넷·투구 수가 늘고 타구가 약해집니다\n(컨트롤 좋은 투수일수록 볼넷 손해가 적음).\n" +
			"제구: 바깥쪽은 홈런을 줄이고 볼넷·단타가 늘어납니다\n(홈런을 잘 맞는 투수일수록 효과가 큼).",
			"투구: 전력투구는 능력치가 조금 오르고 빨리 지쳐\n짧게 던지는 불펜에 맞습니다.\n" +
			"교체: 권장은 투수를 일찍 바꾸고, 자제는 오래 던지게 합니다.\n" +
			"투수가 던질 수 있는 투구 수는 체력으로 정해집니다."
		},
		["Scene_LeagueRankingChart"] = () => new[]
		{
			"순위는 승률(승 ÷ (승+패), 무승부 제외) 순입니다.\n" +
			"승률이 같으면 그 팀끼리의 맞대결 성적,\n그다음 득실차로 정합니다.\n" +
			"시즌은 144경기, 20분마다 한 경기씩 열립니다.",
			// 2026-11-14 (engine tester_y: six lines made this page's text smaller than page 1's): five lines, no heading line.
			"비기너: 1~4위 승격(포스트시즌 없음)\n" +
			"루키·더블A·트리플A·메이저: 포스트시즌 최종 1~4위\n승격, 정규 8~10위 강등\n" +
			"레전드: 최종 1~3위 챔피언스, 4~6위 잔류, 7~10위 강등\n" +
			"챔피언스: 최종 1~3위 잔류, 나머지는 레전드로",
			// 2026-11-15 공인구 (user 11:29~12:1x via the engine session; server SeasonBall / BallEffect, badge Ma9MBallBadge).
			"공인구: 트리플A·메이저·레전드·챔피언스는\n시즌마다 새 공을 씁니다(한 시즌은 모든 리그가 같은 공).\n" +
			"비기너·루키·더블A는 늘 노말볼(N)입니다.\n" +
			"B 빅볼(고반발) · N 노말볼 · S 스몰볼(저반발)이\n3분의 1씩 나오고, 순위표 제목과 로비 VS 아래에 보입니다.",
			"공이 바꾸는 것: 홈런, 안타, 2루타·3루타\n(삼진과 볼넷은 그대로).\n" +
			"빅볼(팀당 약 6점): 파워 타자와 홈런을 덜 맞는 투수가 유리.\n" +
			"스몰볼(팀당 약 4점): 출루·주루·수비, 투수진의 한 점이 귀함.\n" +
			"자동 편성과 특성 추천은 이번 시즌 공으로 계산합니다."
		},
		// 2026-11-11 빠른조합·목표교환 (Ma9MBookQuick; server TrainingHandlers TryMix/TryExchange/TryQuickMix/TryTargetExchange).
		["Scene_CombineTraining"] = () => CombineTraining_Main.Ma9MExchange ? new[]
		{
			// 2 → 1 since 2026-11-12 (user 10:4x).
			"교환소: 안 쓰는 III·IV 트레이닝북 2권을\n같은 등급 원하는 능력치 책 1권으로 바꿉니다.\n" +
			"(2권은 능력치가 달라도 되고, 등급은 같아야 합니다)\n책 2권을 칸에 올리고 교환을 눌러 능력치를 고릅니다.",
			"목표교환: 능력치별로 갖고 싶은 권수를 정하면\n목표보다 많은 책(목표 0인 능력치부터) 2권마다\n" +
			"모자란 능력치 책 1권으로 한 번에 바꿉니다.\n목표는 이 기기에 구단별로 기억됩니다(III·IV 따로).\n" +
			"줄을 눌러 고르고 -1/+1(꾹 누르면 빠르게)."
		} : new[]
		{
			"같은 등급 트레이닝북 4권 → 한 등급 위 1권\n(능력치는 무작위). III은 트레이닝코치가 있으면\n" +
			"코치 등급에 따라 5·4·3·2권으로 IV 1권.\n빠른선택: 같은 등급 4권을 칸에 올립니다.",
			"빠른조합: 고른 등급(II·III·IV)까지 가진 책을\n아래 등급부터 한 번에 모두 조합합니다.\n" +
			// 2026-11-12 (AI tester 쌍방울 B12): 목표교환 is for III·IV books, so only those results offer it.
			"모자라 남는 책은 그대로입니다.\nIII·IV까지 조합하면 결과 창에서\n목표교환을 바로 열 수 있습니다.",
			TrainingPoint
		},
		// 2026-11-12 (AI testers LG 38 · kt 48: "시범경기에 다음 리그 선발이 나오는데 체력 영향 안내가 없다"; 야구팬 24: 카드를 받았는지
		// 모름). Server ExhibitionHandlers (TriesPerDay, RewardMp, PrizeChance; no fatigue) and ExhibitionAuto (05-09시).
		["Scene_ExhibitionGame"] = () => new[]
		{
			"시범경기는 하루 10번입니다(0시에 다시 채워짐).\n" +
			"리그 순위·체력에는 영향이 없고 홈런볼도 없습니다.\n" +
			"다음 리그 경기 선발이 나와도\n피로가 쌓이지 않습니다.",
			"보상 MP (승 / 무 / 패)\n" +
			"EASY 4만 / 2.5만 / 1.5만\nNORMAL 6.5만 / 4만 / 2만\nHARD 10만 / 6.5만 / 3만\n" +
			"이기면 20% 확률로 상대 구단 선수 카드 1장.",
			// 2026-11-12 (AI tester kt: "남은 시범경기는 새벽에" read as tonight's leftovers): ExhibitionAuto plays the day's own tries.
			"매일 새벽 5시~9시에 그날의 시범경기 10번을\n서버가 마지막으로 고른 난이도로 대신 치르고,\n" +
			"결과는 로비 알림으로 보여 드립니다.\n낮·밤에 남은 경기는 0시에 없어집니다."
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
			List<UILabel> titles = FindTitles(scene);
			if (titles.Count == 0)
			{
				continue;
			}
			int made = 0;
			foreach (UILabel title in titles)
			{
				if (title.transform.parent != null && title.transform.parent.Find("Ma9MHelpButton") != null)
				{
					continue;
				}
				MakeButton(title, scene);
				made++;
			}
			Debug.Log("[Ma9MHelp] " + scene + ": (?) on " + made + " of " + titles.Count + " title(s) \"" + titles[0].text + "\" (" + PathOf(titles[0].transform) + ")");
			yield break;
		}
		Debug.Log("[Ma9MHelp] " + scene + ": no path title found");
	}

	// 2026-11-14 (engine tester_x: no "(?)" on 기록실 > 리그순위표): the ranking chart is five pages side by side (DragPage, x 0 …
	// 4800), each with its own "기록실 > 리그순위표" label, so the one highest label could be another page's or some other " > "
	// text. A scene listed here takes only titles holding its words, and every page's copy of the title gets a button.
	// 2026-11-15: 구단정보's title is "기록실>구단정보" (Korean.txt TeamInfo_LeftTitle, no spaces), so a scene with a hint takes ">" with
	// or without them.
	private static readonly Dictionary<string, string> TitleHints = new Dictionary<string, string>
	{
		["Scene_LeagueRankingChart"] = "순위표",
		["Scene_TeamInfo"] = "구단정보"
	};

	private static List<UILabel> FindTitles(string scene)
	{
		string hint;
		TitleHints.TryGetValue(scene, out hint);
		UILabel best = FindTitle(hint);
		List<UILabel> all = new List<UILabel>();
		if (best == null)
		{
			return all;
		}
		foreach (UILabel label in UnityEngine.Object.FindObjectsByType<UILabel>(FindObjectsSortMode.None))
		{
			if (label.gameObject.activeInHierarchy && label.gameObject.name != "Ma9MHelpButton" && label.text == best.text)
			{
				all.Add(label);
			}
		}
		if (!all.Contains(best))
		{
			all.Insert(0, best);
		}
		return all;
	}

	private static string PathOf(Transform t)
	{
		string path = t.name;
		for (Transform p = t.parent; p != null; p = p.parent)
		{
			path = p.name + "/" + path;
		}
		return path;
	}

	// The highest visible label whose text is a path ("팀 관리 > 계약관리"), holding hint when given.
	private static UILabel FindTitle(string hint = null)
	{
		UILabel best = null;
		foreach (UILabel label in UnityEngine.Object.FindObjectsByType<UILabel>(FindObjectsSortMode.None))
		{
			if (!label.gameObject.activeInHierarchy || string.IsNullOrEmpty(label.text) || !label.text.Contains(string.IsNullOrEmpty(hint) ? " > " : ">") || label.text.Contains("\n"))
			{
				continue;
			}
			if (!string.IsNullOrEmpty(hint) && !label.text.Contains(hint))
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
		// AI 테스터 8회차 불편 59 (2026-11-11: "작고 어두워 확대해야 보인다", 손가락으로 누르기 작다): brighter, outlined, a third
		// larger, a wider touch box, and a pulse on each visit until that screen's help has been opened once.
		q.color = new Color(1f, 0.88f, 0.15f);
		q.effectStyle = UILabel.Effect.Outline;
		q.effectColor = new Color(0.3f, 0.15f, 0f);
		go.transform.localScale = title.transform.localScale * 1.35f;
		// Right of the title: its bounds in the parent, the button's left edge 10 units past them, centred on the title's line.
		Bounds titleBounds = NGUIMath.CalculateRelativeWidgetBounds(title.transform.parent, title.transform);
		go.transform.localPosition = title.transform.localPosition;
		Bounds own = NGUIMath.CalculateRelativeWidgetBounds(title.transform.parent, go.transform);
		go.transform.localPosition += new Vector3(titleBounds.max.x + 10f - own.min.x, titleBounds.center.y - own.center.y, 0f);
		BoxCollider box = NGUITools.AddWidgetCollider(go);
		if (box != null)
		{
			box.size = new Vector3(box.size.x * 1.8f, box.size.y * 2.2f, box.size.z);
		}
		go.AddComponent<Ma9MHelpButton>().Scene = scene;
		go.SetActive(true);
	}

	public static string SeenKey(string scene)
	{
		return "Ma9M_HelpSeen_" + scene;
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
		else if (pages.Length > 1)
		{
			// The last page keeps 닫기 where the pages before had it (2026-11-12, AI tester NC: the one centred 닫기 of Popup1 took
			// a second tap), with 처음 in the place of 다음.
			PopupManager.ViewPopup2(title, pages[i], "처음", "닫기", () => ShowPage(pages, 0), null);
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

	private IEnumerator Start()
	{
		bool seen = false;
		try
		{
			seen = PlayerPrefs.GetInt(Ma9MHelp.SeenKey(Scene), 0) == 1;
		}
		catch (Exception)
		{
		}
		if (seen)
		{
			yield break;
		}
		// Three pulses (up to 1.3× and back) when the screen opens.
		Vector3 baseScale = transform.localScale;
		for (int n = 0; n < 3; n++)
		{
			for (float t = 0f; t < 0.5f; t += Time.unscaledDeltaTime)
			{
				if (this == null)
				{
					yield break;
				}
				float k = 1f + 0.3f * Mathf.Sin(Mathf.PI * t / 0.5f);
				transform.localScale = baseScale * k;
				yield return null;
			}
			transform.localScale = baseScale;
			yield return new WaitForSecondsRealtime(0.25f);
		}
	}

	private void OnClick()
	{
		try
		{
			PlayerPrefs.SetInt(Ma9MHelp.SeenKey(Scene), 1);
			PlayerPrefs.Save();
		}
		catch (Exception)
		{
		}
		Ma9MHelp.Show(Scene);
	}
}
