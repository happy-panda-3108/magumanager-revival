using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 도전과제 > 업적 (user, 2026-10-20): the original 구단미션 page (swipe left from 오늘의 미션) holds
	// one-time achievements in its four tabs: 성적, 수집, 육성, 특별. A cleared achievement pays its MP
	// and shows the original '도전과제완료' box (PKS_SC_GET_TEAM_MISSION_REWARD), which closes on a tap.
	// The box waits while league results are unconfirmed, so it never gives a result away. The list
	// is ma9_challenge.txt (TR_TeamMission), written here.
	public static class Achievements
	{
		private sealed class Def
		{
			public short Index;
			public char Grade;
			public int Type;      // 1 성적, 2 수집, 3 육성 (원작 연습), 4 특별
			public string Name, Detail, Key;
			public int Goal;
		}

		// Collection keys counted from the roster.
		private const string Elite = "elite", Rare = "rare", Roster = "roster", TenBall = "ten_ball", Years = "years";

		// 레전드 카드 (user, 2026-11-09: "레전드 획득 관련 도전과제도 추가해줘"): 레전드 owned, and the pairs of a 레전드 batter and pitcher
		// (the 1군 takes one of each, RosterHandlers.MaxLegend*).
		private const string Legend = "legend", LegendPair = "legend_pair";

		private static Def D(int index, char grade, int type, string name, string detail, string key, int goal) =>
			new Def { Index = (short)index, Grade = grade, Type = type, Name = name, Detail = detail, Key = key, Goal = goal };

		private static readonly Def[] All =
		{
			D(101, 'E', 1, "첫 승리", "리그 경기에서 첫 승리를 거두세요.", Missions.LeagueWins, 1),
			D(102, 'D', 1, "10승", "리그 경기 통산 10승을 거두세요.", Missions.LeagueWins, 10),
			D(103, 'C', 1, "50승", "리그 경기 통산 50승을 거두세요.", Missions.LeagueWins, 50),
			D(104, 'B', 1, "100승", "리그 경기 통산 100승을 거두세요.", Missions.LeagueWins, 100),
			D(105, 'A', 1, "300승", "리그 경기 통산 300승을 거두세요.", Missions.LeagueWins, 300),
			D(106, 'S', 1, "1000승", "리그 경기 통산 1,000승을 거두세요.", Missions.LeagueWins, 1000),
			D(111, 'D', 1, "홈런 100개", "리그 경기에서 팀 통산 홈런 100개를 치세요.", Missions.LeagueHomeRuns, 100),
			D(112, 'C', 1, "홈런 500개", "리그 경기에서 팀 통산 홈런 500개를 치세요.", Missions.LeagueHomeRuns, 500),
			D(113, 'B', 1, "홈런 1000개", "리그 경기에서 팀 통산 홈런 1,000개를 치세요.", Missions.LeagueHomeRuns, 1000),
			D(121, 'E', 1, "시범경기 10승", "시범경기에서 10승을 거두세요.", Missions.ExhibitionWins, 10),
			D(122, 'C', 1, "시범경기 50승", "시범경기에서 50승을 거두세요.", Missions.ExhibitionWins, 50),
			D(123, 'B', 1, "시범경기 200승", "시범경기에서 200승을 거두세요.", Missions.ExhibitionWins, 200),
			D(131, 'D', 1, "시즌 완주", "리그 시즌 144경기를 끝까지 치르세요.", Missions.SeasonsDone, 1),
			D(132, 'B', 1, "첫 우승", "리그 시즌을 1위로 마치세요.", Missions.SeasonsWon, 1),
			D(133, 'A', 1, "우승 5회", "리그 시즌을 1위로 5번 마치세요.", Missions.SeasonsWon, 5),

			// 업적 다양화 (user, 2026-10-22). 시범경기 (어려움 = the hard level; 하루 = one day's 10 tries).
			D(124, 'D', 1, "어려움 첫 승", "시범경기 '어려움'에서 처음 이기세요.", Missions.ExhibitionHardWins, 1),
			D(125, 'C', 1, "어려움 20승", "시범경기 '어려움'에서 20승을 거두세요.", Missions.ExhibitionHardWins, 20),
			D(126, 'B', 1, "어려움 100승", "시범경기 '어려움'에서 100승을 거두세요.", Missions.ExhibitionHardWins, 100),
			D(127, 'C', 1, "하루 7승", "하루에 시범경기 7승을 거두세요.", Missions.ExhibitionDayBest, 7),
			D(128, 'B', 1, "하루 10전 10승", "하루 시범경기 10번을 모두 이기세요.", Missions.ExhibitionDayBest, 10),
			D(129, 'E', 1, "시범경기 100경기", "시범경기를 100번 치르세요.", Missions.Exhibitions, 100),
			// 경기 순간 (league games, counted when the result is confirmed). Grades from the engine's frequencies per
			// 1,000 team games (--leaguetest 30): walk-off 39, grand slam 35, 5-run comeback 10.7, 12 K 6.8, shutout 5.3,
			// three homers 4.4, no-hitter 0.73, cycle 0.48, perfect game 0.
			D(141, 'A', 1, "사이클링 히트", "한 타자가 한 경기에서 1루타·2루타·3루타·홈런을 모두 치세요.", Missions.GameCycle, 1),
			D(142, 'C', 1, "한 경기 3홈런", "한 타자가 한 경기에서 홈런 3개를 치세요.", Missions.GameThreeHomers, 1),
			D(143, 'E', 1, "만루홈런", "리그 경기에서 만루홈런을 치세요.", Missions.GameGrandSlam, 1),
			D(144, 'D', 1, "만루홈런 10개", "리그 경기에서 만루홈런 10개를 치세요.", Missions.GameGrandSlam, 10),
			D(145, 'E', 1, "끝내기 승리", "리그 경기에서 끝내기로 이기세요.", Missions.GameWalkOff, 1),
			D(146, 'D', 1, "끝내기 10번", "리그 경기에서 끝내기로 10번 이기세요.", Missions.GameWalkOff, 10),
			D(147, 'A', 1, "팀 노히트노런", "상대에게 안타와 점수를 하나도 주지 않고 이기세요. (계투 인정)", Missions.GameNoHitter, 1),
			D(148, 'S', 1, "팀 퍼펙트게임", "상대 타자를 한 명도 내보내지 않고 이기세요. (계투 인정)", Missions.GamePerfect, 1),
			D(149, 'D', 1, "완봉승", "투수 한 명이 끝까지 던져 무실점으로 이기세요.", Missions.GameShutout, 1),
			D(150, 'B', 1, "완봉승 10번", "완봉승을 10번 거두세요.", Missions.GameShutout, 10),
			D(151, 'D', 1, "12탈삼진", "투수 한 명이 한 경기에서 삼진 12개를 잡으세요.", Missions.GameTwelveK, 1),
			D(152, 'D', 1, "5점 차 역전승", "5점 이상 뒤지던 경기를 뒤집어 이기세요.", Missions.GameComeback5, 1),
			D(153, 'C', 1, "역전승 10번", "5점 이상 뒤지던 경기를 10번 뒤집어 이기세요.", Missions.GameComeback5, 10),
			// 선발 혼자 (user, 2026-11-05: "노히트노런, 퍼펙트게임, 완봉승, 완투승은 모두 선발투수 혼자 결과가 나왔을 때 깨지는 업적";
			// 147-148 became the 팀 ones, chose "노히트노런 S, 퍼펙트 S, 완투승 D").
			D(154, 'S', 1, "노히트노런", "선발 투수 혼자 끝까지 던져 안타와 점수를 하나도 주지 않고 이기세요.", Missions.GameNoHitterSolo, 1),
			D(155, 'S', 1, "퍼펙트게임", "선발 투수 혼자 끝까지 던져 상대 타자를 한 명도 내보내지 않고 이기세요.", Missions.GamePerfectSolo, 1),
			D(156, 'D', 1, "완투승", "선발 투수 혼자 끝까지 던져 이기세요.", Missions.GameComplete, 1),
			// 시즌 기록 (the regular season of the user's club, checked when the season closes).
			D(161, 'C', 1, "팀 200홈런", "한 시즌 팀 홈런 200개를 치세요.", Missions.SeasonTeamHomers200, 1),
			D(162, 'B', 1, "팀 타율 3할", "한 시즌 팀 타율 3할 이상으로 마치세요.", Missions.SeasonTeamAvg300, 1),
			D(163, 'B', 1, "팀 평균자책 3.50", "한 시즌 팀 평균자책점 3.50 이하로 마치세요.", Missions.SeasonTeamEra350, 1),
			D(164, 'A', 1, "시즌 100승", "한 시즌 100승을 거두세요.", Missions.SeasonWins100, 1),
			D(165, 'C', 1, "40홈런 타자", "한 시즌 홈런 40개를 친 타자를 만드세요.", Missions.Season40Homers, 1),
			D(166, 'B', 1, "50홈런 타자", "한 시즌 홈런 50개를 친 타자를 만드세요.", Missions.Season50Homers, 1),
			D(167, 'A', 1, "4할 타자", "규정타석을 채우고 타율 4할로 시즌을 마친 타자를 만드세요.", Missions.Season400, 1),
			D(168, 'B', 1, "200안타", "한 시즌 안타 200개를 친 타자를 만드세요.", Missions.Season200Hits, 1),
			D(169, 'B', 1, "30-30 클럽", "한 시즌 홈런 30개·도루 30개를 기록한 타자를 만드세요.", Missions.Season3030, 1),
			D(170, 'C', 1, "20승 투수", "한 시즌 20승을 거둔 투수를 만드세요.", Missions.Season20Wins, 1),
			D(171, 'C', 1, "40세이브", "한 시즌 40세이브를 거둔 투수를 만드세요.", Missions.Season40Saves, 1),
			D(172, 'B', 1, "200탈삼진", "한 시즌 탈삼진 200개를 잡은 투수를 만드세요.", Missions.Season200K, 1),
			// 포스트시즌·승격 (리그 등급: 0 비기너, 1 루키, 2 마이너, 3 메이저, 4 챔피언스 — 189·190 from 2026-10-28).
			D(181, 'D', 1, "포스트시즌 진출", "포스트시즌에 나가세요.", Missions.PostseasonIn, 1),
			D(182, 'C', 1, "한국시리즈 진출", "한국시리즈에 나가세요.", Missions.KoreanSeriesIn, 1),
			D(183, 'B', 1, "한국시리즈 우승", "한국시리즈에서 우승하세요.", Missions.KoreanSeriesWon, 1),
			D(184, 'A', 1, "한국시리즈 5회 우승", "한국시리즈에서 5번 우승하세요.", Missions.KoreanSeriesWon, 5),
			D(185, 'A', 1, "메이저 한국시리즈 우승", "메이저 리그에서 한국시리즈 우승을 차지하세요.", Missions.MajorKoreanSeriesWon, 1),
			D(186, 'D', 1, "첫 승격", "시즌을 마치고 윗 리그로 올라가세요.", Missions.Promotions, 1),
			// TierReached in the six-tier numbering since 2026-11-08 (2 트리플A, 3 메이저, 4 레전드, 5 챔피언스; Missions.Reached): 187 was
			// "마이너 리그", 189 needed 4; 191 and 192 are new with the 레전드 리그.
			D(187, 'C', 1, "트리플A 리그", "트리플A 리그에 오르세요.", Missions.TierReached, 2),
			D(188, 'B', 1, "메이저 리그", "메이저 리그에 오르세요.", Missions.TierReached, 3),
			D(189, 'A', 1, "챔피언스 리그", "챔피언스 리그에 오르세요.", Missions.TierReached, 5),
			D(190, 'S', 1, "챔피언스 한국시리즈 우승", "챔피언스 리그에서 한국시리즈 우승을 차지하세요.", Missions.ChampionsKoreanSeriesWon, 1),
			D(191, 'A', 1, "레전드 리그", "레전드 리그에 오르세요.", Missions.TierReached, 4),
			D(192, 'A', 1, "레전드 한국시리즈 우승", "레전드 리그에서 한국시리즈 우승을 차지하세요.", Missions.LegendKoreanSeriesWon, 1),

			D(201, 'D', 2, "첫 엘리트", "엘리트 카드를 1장 보유하세요.", Elite, 1),
			D(202, 'C', 2, "엘리트 5장", "엘리트 카드를 5장 보유하세요.", Elite, 5),
			D(203, 'B', 2, "엘리트 10장", "엘리트 카드를 10장 보유하세요.", Elite, 10),
			D(204, 'A', 2, "엘리트 25장", "엘리트 카드를 25장 보유하세요.", Elite, 25),
			D(205, 'S', 2, "엘리트 50장", "엘리트 카드를 50장 보유하세요.", Elite, 50),
			D(211, 'E', 2, "레어 10장", "레어 카드를 10장 보유하세요.", Rare, 10),
			D(212, 'D', 2, "레어 30장", "레어 카드를 30장 보유하세요.", Rare, 30),
			D(213, 'C', 2, "레어 60장", "레어 카드를 60장 보유하세요.", Rare, 60),
			D(214, 'B', 2, "레어 100장", "레어 카드를 100장 보유하세요.", Rare, 100),
			D(221, 'E', 2, "선수 100명", "선수를 100명 보유하세요.", Roster, 100),
			D(222, 'D', 2, "선수 200명", "선수를 200명 보유하세요.", Roster, 200),
			D(223, 'C', 2, "선수 300명", "선수를 300명 보유하세요.", Roster, 300),
			D(224, 'B', 2, "선수 500명", "선수를 500명 보유하세요.", Roster, 500),
			// 10볼 (86 cards in all), every season 1999–2025, and named team colours (팀컬러 이름별 업적 below).
			D(231, 'D', 2, "첫 10볼", "10볼 카드를 1장 보유하세요.", TenBall, 1),
			D(232, 'C', 2, "10볼 5장", "10볼 카드를 5장 보유하세요.", TenBall, 5),
			D(233, 'B', 2, "10볼 10장", "10볼 카드를 10장 보유하세요.", TenBall, 10),
			D(234, 'A', 2, "10볼 20장", "10볼 카드를 20장 보유하세요.", TenBall, 20),
			D(235, 'C', 2, "27개 시즌", "서로 다른 27개 시즌(연도)의 선수를 보유하세요.", Years, 27),  // text since 2026-11-10: cards run 1993-2026 now (34 seasons), the rule was always 27 different seasons
			D(241, 'D', 2, "팀컬러 5종", "이름이 다른 팀컬러 5종을 적용하세요.", Missions.TeamColourNames, 5),
			D(242, 'C', 2, "팀컬러 10종", "이름이 다른 팀컬러 10종을 적용하세요.", Missions.TeamColourNames, 10),
			D(243, 'B', 2, "팀컬러 20종", "이름이 다른 팀컬러 20종을 적용하세요.", Missions.TeamColourNames, 20),
			D(244, 'A', 2, "팀컬러 50종", "이름이 다른 팀컬러 50종을 적용하세요.", Missions.TeamColourNames, 50),
			// 레전드 카드 (2026-11-09; 1차 21장: 레전드 선수팩, 레전드 조합).
			D(251, 'C', 2, "첫 레전드", "레전드 카드를 1장 보유하세요.", Legend, 1),
			D(252, 'B', 2, "레전드 투·타", "레전드 투수와 레전드 타자를 1장씩 보유하세요.", LegendPair, 1),
			D(253, 'B', 2, "레전드 5장", "레전드 카드를 5장 보유하세요.", Legend, 5),
			D(254, 'A', 2, "레전드 10장", "레전드 카드를 10장 보유하세요.", Legend, 10),
			D(255, 'S', 2, "1차 레전드 완성", "1차 레전드 카드 21장을 모두 보유하세요.", Legend, 21),

			D(301, 'E', 3, "카드팩 10개", "선수 카드팩을 10개 여세요.", Missions.CardPacksOpened, 10),
			D(302, 'D', 3, "카드팩 50개", "선수 카드팩을 50개 여세요.", Missions.CardPacksOpened, 50),
			D(303, 'C', 3, "카드팩 200개", "선수 카드팩을 200개 여세요.", Missions.CardPacksOpened, 200),
			D(304, 'B', 3, "카드팩 500개", "선수 카드팩을 500개 여세요.", Missions.CardPacksOpened, 500),
			D(305, 'A', 3, "카드팩 1000개", "선수 카드팩을 1,000개 여세요.", Missions.CardPacksOpened, 1000),
			D(311, 'E', 3, "트레이닝팩 10개", "트레이닝 카드팩을 10개 여세요.", Missions.TrainingPacksOpened, 10),
			D(312, 'D', 3, "트레이닝팩 50개", "트레이닝 카드팩을 50개 여세요.", Missions.TrainingPacksOpened, 50),
			D(313, 'C', 3, "트레이닝팩 200개", "트레이닝 카드팩을 200개 여세요.", Missions.TrainingPacksOpened, 200),
			D(314, 'B', 3, "트레이닝팩 500개", "트레이닝 카드팩을 500개 여세요.", Missions.TrainingPacksOpened, 500),
			D(321, 'E', 3, "트레이닝 20회", "선수 트레이닝을 20번 하세요.", Missions.Trainings, 20),
			D(322, 'D', 3, "트레이닝 100회", "선수 트레이닝을 100번 하세요.", Missions.Trainings, 100),
			D(323, 'C', 3, "트레이닝 500회", "선수 트레이닝을 500번 하세요.", Missions.Trainings, 500),
			D(331, 'E', 3, "계약 연장 10회", "선수 계약을 10번 연장하세요.", Missions.Contracts, 10),
			D(332, 'D', 3, "계약 연장 50회", "선수 계약을 50번 연장하세요.", Missions.Contracts, 50),
			D(333, 'C', 3, "계약 연장 200회", "선수 계약을 200번 연장하세요.", Missions.Contracts, 200),
			D(341, 'E', 3, "선수 조합 10회", "선수 조합을 10번 하세요.", Missions.Fusions, 10),
			D(342, 'D', 3, "선수 조합 50회", "선수 조합을 50번 하세요.", Missions.Fusions, 50),
			D(343, 'C', 3, "선수 조합 200회", "선수 조합을 200번 하세요.", Missions.Fusions, 200),
			// 특별수강 (+5 = the main stat's best result) and 컨디션 회복제.
			D(351, 'E', 3, "첫 특별수강", "특별수강을 처음 마치세요.", Missions.Lessons, 1),
			D(352, 'D', 3, "특별수강 10회", "특별수강을 10번 마치세요.", Missions.Lessons, 10),
			D(353, 'C', 3, "특별수강 50회", "특별수강을 50번 마치세요.", Missions.Lessons, 50),
			D(354, 'B', 3, "특별수강 100회", "특별수강을 100번 마치세요.", Missions.Lessons, 100),
			D(355, 'D', 3, "특강 +5", "특별수강에서 주 능력치 +5를 받으세요.", Missions.LessonsTop, 1),
			D(356, 'C', 3, "특강 +5 10회", "특별수강에서 주 능력치 +5를 10번 받으세요.", Missions.LessonsTop, 10),
			D(361, 'E', 3, "컨디션 회복제 10개", "컨디션 회복제를 10번 쓰세요.", Missions.ConditionItems, 10),
			D(362, 'D', 3, "컨디션 회복제 50개", "컨디션 회복제를 50번 쓰세요.", Missions.ConditionItems, 50),

			D(401, 'E', 4, "첫 출석", "처음으로 출석하세요.", Missions.LoginDays, 1),
			D(402, 'D', 4, "출석 7일", "7일 출석하세요.", Missions.LoginDays, 7),
			D(403, 'C', 4, "출석 30일", "30일 출석하세요.", Missions.LoginDays, 30),
			D(404, 'B', 4, "출석 100일", "100일 출석하세요.", Missions.LoginDays, 100),
			D(411, 'D', 4, "첫 팀컬러", "팀컬러를 처음으로 등록하세요.", Missions.TeamColour, 1),
			// User, 2026-10-20: more for team colours.
			D(415, 'D', 4, "팀컬러 3칸", "팀컬러를 C·H·P 세 칸 모두 등록하세요.", Missions.TeamColourFull, 1),
			D(416, 'C', 4, "S 팀컬러", "S 등급 팀컬러를 등록하세요.", Missions.TeamColourS, 1),
			D(417, 'A', 4, "단일 년도 팀", "단일 년도 팀 S 팀컬러(1군 25명 전원 같은 년도·같은 팀)를 등록하세요.", Missions.TeamYearS, 1),
			D(412, 'D', 4, "미션 올클리어", "오늘의 미션 6개를 모두 클리어하세요.", Missions.AllClears, 1),
			D(413, 'C', 4, "올클리어 10회", "오늘의 미션 올클리어를 10번 하세요.", Missions.AllClears, 10),
			D(414, 'B', 4, "올클리어 50회", "오늘의 미션 올클리어를 50번 하세요.", Missions.AllClears, 50),
			// 리그 시상 (LeagueAwards; a title = one of the 14 categories won by a card of the user's club).
			D(421, 'D', 4, "첫 타이틀", "리그 시상에서 우리 선수가 타이틀을 받게 하세요.", Missions.AwardTitles, 1),
			D(422, 'C', 4, "타이틀 10개", "리그 시상 타이틀을 10개 받으세요.", Missions.AwardTitles, 10),
			D(423, 'B', 4, "타이틀 50개", "리그 시상 타이틀을 50개 받으세요.", Missions.AwardTitles, 50),
			D(424, 'B', 4, "리그 MVP", "우리 선수가 리그 MVP를 받게 하세요.", Missions.AwardMvp, 1),
			D(425, 'C', 4, "한국시리즈 MVP", "우리 선수가 한국시리즈 MVP를 받게 하세요.", Missions.AwardKsMvp, 1),
			D(426, 'B', 4, "한 시즌 타이틀 7개", "한 시즌 리그 시상 14개 부문 중 7개 이상을 받으세요.", Missions.AwardTitlesSeason, 7),
			D(427, 'A', 4, "타자 3관왕", "한 타자가 타율·홈런·타점 타이틀을 함께 받게 하세요.", Missions.TripleCrownBatter, 1),
			D(428, 'A', 4, "투수 3관왕", "한 투수가 승리·평균자책·탈삼진 타이틀을 함께 받게 하세요.", Missions.TripleCrownPitcher, 1),
		};

		private static readonly Dictionary<int, Def> s_static = All.ToDictionary(d => (int)d.Index);

		// 팀컬러 이름별 업적 (user, 2026-10-22; TeamColors.Achievement gives each named entry's name and grade): a row
		// is made the first time any team applies that named entry (table team_color_achievement: idx 5001…, the
		// entry) and written into ma9_challenge.txt, so the 업적 list grows with the team colours found. A client
		// reads the table at login, so a row new in this session is unknown to its table: those are announced with
		// a popup instead of the 도전과제완료 box.
		private const int DynamicFirst = 5001, DynamicLast = 32000;

		private static readonly Dictionary<int, Def> s_dynamic = new Dictionary<int, Def>();

		private static readonly Dictionary<int, int> s_dynamicByEntry = new Dictionary<int, int>();

		private static readonly Dictionary<int, int> s_nameKey = new Dictionary<int, int>();

		private static string s_tablePath;

		private static Def Find(int idx) => s_static.TryGetValue(idx, out Def d) ? d : s_dynamic.TryGetValue(idx, out d) ? d : null;

		private static Def DynamicDef(int idx, int entry)
		{
			var a = TeamColors.Achievement(entry);
			return a == null ? null : new Def { Index = (short)idx, Grade = a.Value.grade, Type = 4, Name = a.Value.name, Detail = a.Value.detail, Key = "team_color_entry", Goal = 1 };
		}

		// User, 2026-10-20: S and A paid too little (were 1,000,000 and 500,000). S ≈ three 9볼+ cards from
		// 고급팩, A ≈ one or two; what was already paid at the old rate is topped up (TopUp).
		public static int Mp(char grade) => grade switch { 'S' => 5_000_000, 'A' => 2_000_000, 'B' => 200_000, 'C' => 100_000, 'D' => 50_000, _ => 30_000 };

		private static int OldMp(char grade) => grade switch { 'S' => 1_000_000, 'A' => 500_000, _ => Mp(grade) };

		// Users whose old-rate S/A achievements have been checked this run.
		private static readonly HashSet<uint> s_toppedUp = new HashSet<uint>();

		private static string s_cs;

		private static Database s_db;

		private static CardCatalog s_catalog;

		// Users with a cleared achievement whose box has not been shown yet.
		private static readonly HashSet<uint> s_pending = new HashSet<uint>();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS achievement (user_code INTEGER NOT NULL, idx INTEGER NOT NULL, cleared TEXT NOT NULL,
					delivered INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (user_code, idx));
					SELECT DISTINCT user_code FROM achievement WHERE delivered = 0";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) s_pending.Add((uint)r.GetInt64(0));
			}
			// paid_mp: what an achievement paid (NULL = paid before 2026-10-20 at the old S/A rate).
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('achievement') WHERE name = 'paid_mp'";
				if (Convert.ToInt32(cmd.ExecuteScalar()) == 0)
				{
					cmd.CommandText = "ALTER TABLE achievement ADD COLUMN paid_mp INTEGER";
					cmd.ExecuteNonQuery();
				}
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS team_color_achievement (idx INTEGER PRIMARY KEY, entry INTEGER NOT NULL UNIQUE, created TEXT NOT NULL);
					SELECT idx, entry FROM team_color_achievement ORDER BY idx";
				using SqliteDataReader r = cmd.ExecuteReader();
				lock (s_dynamic)
				{
					s_dynamic.Clear();
					s_dynamicByEntry.Clear();
					s_nameKey.Clear();
					while (r.Read())
					{
						int idx = r.GetInt32(0), entry = r.GetInt32(1);
						Def d = DynamicDef(idx, entry);
						if (d == null) continue;
						s_dynamic[idx] = d;
						s_dynamicByEntry[entry] = idx;
						s_nameKey[idx] = TeamColors.Achievement(entry).Value.nameKey;
					}
				}
			}
			s_tablePath = Path.Combine(dataDir, "tables", "ma9_challenge.txt");
			WriteTable(s_tablePath);
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TEAM_MISSION>(Enter);
			// The client's tutorial asks for a reward it was never sent; nothing to do.
			loop.Register<Ma9MPacket.PKS_CS_GET_TEAM_MISSION_REWARD>((session, packet) => { });
			GameLoop.AfterPacket += Deliver;
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// TR_TeamMission: index, grade, type, name, detail, action, kind, 3 × (condition, value, range), MP.
		private static void WriteTable(string path)
		{
			StringBuilder sb = new StringBuilder("index\tgrade\ttype\tname\tdetail\taction\tkind\tc1\tv1\tr1\tc2\tv2\tr2\tc3\tv3\tr3\tmp\n");
			List<Def> rows = All.ToList();
			lock (s_dynamic)
			{
				rows.AddRange(s_dynamic.Values.OrderBy(d => d.Index));
			}
			foreach (Def d in rows)
			{
				sb.Append($"{d.Index}\t{d.Grade}\t{d.Type}\t{d.Name}\t{d.Detail}\t{d.Key}\tNONE\t0\t0\t0\t0\t0\t0\t0\t0\t0\t{Mp(d.Grade)}\n");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
		}

		// The 업적 a team has cleared (also for self-tests).
		internal static HashSet<int> ClearedOf(uint user) => Cleared(user);

		private static HashSet<int> Cleared(uint user)
		{
			HashSet<int> set = new HashSet<int>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT idx FROM achievement WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) set.Add(r.GetInt32(0));
			return set;
		}

		// Records every achievement the user now meets (paid and shown by Deliver).
		public static void Evaluate(uint user)
		{
			if (s_cs == null || user == 0)
			{
				return;
			}
			HashSet<int> cleared = Cleared(user);
			List<Def> open = All.Where(d => !cleared.Contains(d.Index)).ToList();
			if (open.Count == 0)
			{
				return;
			}
			Dictionary<string, int> values = new Dictionary<string, int>();
			int Value(string key)
			{
				if (values.TryGetValue(key, out int v)) return v;
				if (key == Elite || key == Rare || key == Roster || key == TenBall || key == Years || key == Legend || key == LegendPair)
				{
					List<TeamCard> cards = s_db.GetCards(user);
					List<CardInfo> infos = cards.Select(c => s_catalog.Get(c.Idx)).Where(x => x != null).ToList();
					values[Roster] = cards.Count;
					// 레전드 (grade 5, the season '81) count neither as 엘리트 nor as a season (2026-11-09).
					values[Elite] = infos.Count(c => c.Grade >= 4 && !c.IsLegend);
					values[Rare] = infos.Count(c => c.Grade == 3);
					values[TenBall] = infos.Count(c => c.Ball >= 10);
					values[Years] = infos.Where(c => !c.IsLegend && c.Year > 0).Select(c => c.Year).Distinct().Count();
					values[Legend] = infos.Count(c => c.IsLegend);
					values[LegendPair] = Math.Min(infos.Count(c => c.IsLegend && c.IsBatter), infos.Count(c => c.IsLegend && !c.IsBatter));
					return values[key];
				}
				return values[key] = Missions.Total(user, key);
			}
			List<Def> now = open.Where(d => Value(d.Key) >= d.Goal).ToList();
			if (now.Count == 0)
			{
				return;
			}
			using (SqliteConnection c = Open())
			using (SqliteTransaction tx = c.BeginTransaction())
			{
				foreach (Def d in now)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.Transaction = tx;
					cmd.CommandText = "INSERT OR IGNORE INTO achievement (user_code, idx, cleared) VALUES ($u, $i, $t)";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$i", d.Index);
					cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
					cmd.ExecuteNonQuery();
				}
				tx.Commit();
			}
			s_pending.Add(user);
			ServerLog.Info($"업적 user {user}: {string.Join(", ", now.Select(d => d.Name))}");
		}

		// After any packet of the user's session: pay and show what was cleared (not while league
		// results wait for confirmation).
		public static void Deliver(GameSession session)
		{
			// Not right after a results-screen CONFIRM: the app is still counting up that game and adds its MP at the stamp, so
			// a top bar now showed it twice (2026-11-09, GameSession.ConfirmedLast). The app sends nothing until the stamp; the
			// next packet delivers, and the popup no longer gives the result away during the count-up.
			if (session != null && session.ConfirmedLast)
			{
				session.ConfirmedLast = false;
				return;
			}
			if (session?.Team == null || (!s_pending.Contains(session.Team.UserCode) && s_toppedUp.Contains(session.Team.UserCode)))
			{
				return;
			}
			uint user = session.Team.UserCode;
			if (LeagueHandlers.HasUnconfirmed(user))
			{
				return;
			}
			if (!s_toppedUp.Contains(user))
			{
				TopUp(session);
			}
			if (!s_pending.Contains(user))
			{
				return;
			}
			List<int> due = new List<int>();
			using (SqliteConnection c = Open())
			{
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT idx FROM achievement WHERE user_code = $u AND delivered = 0 ORDER BY idx";
					cmd.Parameters.AddWithValue("$u", (long)user);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) due.Add(r.GetInt32(0));
				}
				foreach (int idx in due)
				{
					using SqliteCommand mark = c.CreateCommand();
					mark.CommandText = "UPDATE achievement SET delivered = 1, paid_mp = $m WHERE user_code = $u AND idx = $i";
					mark.Parameters.AddWithValue("$u", (long)user);
					mark.Parameters.AddWithValue("$i", idx);
					Def d = Find(idx);
					mark.Parameters.AddWithValue("$m", d == null ? 0 : Mp(d.Grade));
					mark.ExecuteNonQuery();
				}
			}
			s_pending.Remove(user);
			int mp = 0;
			List<Def> named = new List<Def>();
			foreach (int idx in due)
			{
				Def d = Find(idx);
				if (d == null) continue;
				mp += Mp(d.Grade);
				if (s_dynamic.ContainsKey(idx))
				{
					named.Add(d);
					continue;
				}
				Ma9MPacket.PKS_SC_GET_TEAM_MISSION_REWARD box = new Ma9MPacket.PKS_SC_GET_TEAM_MISSION_REWARD();
				box.mission_index.n = d.Index;
				session.Send(box);
			}
			if (named.Count > 0)
			{
				// 팀컬러 이름별 업적: one popup (the client's table may not have the rows yet).
				int namedMp = named.Sum(d => Mp(d.Grade));
				// Five lines at most: six (five 업적 and the total) put the title over the first line (AI tester, 2026-11-10 shot 024).
				int shown = named.Count <= 4 ? named.Count : 3;
				List<string> lines = named.OrderByDescending(d => AchievementOrder(d.Grade)).Take(shown).Select(d => $"{d.Name} ({d.Grade}) +{Mp(d.Grade):N0} MP").ToList();
				if (named.Count > shown) lines.Add($"외 {named.Count - shown}개");
				lines.Add($"합계 {namedMp:N0} MP · 업적 목록에는 다음 접속부터 보입니다.");
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[] { new PLongString { str = "팀컬러 업적 달성" }, new PLongString { str = string.Join("\n", lines) } };
				session.Send(popup);
			}
			if (mp > 0)
			{
				session.Team.Mp = s_db.AddMp(user, mp);
				TopBar.Send(session);
				ServerLog.Info($"[{session.Id}] 업적 {due.Count}개: +{mp:N0} MP{(named.Count > 0 ? $" (팀컬러 {string.Join(", ", named.Select(d => d.Name))})" : string.Empty)}");
			}
		}

		private static int AchievementOrder(char grade) => "EDCBAS".IndexOf(grade);

		// 팀컬러 이름별 업적 (TeamColors.RecordForAchievements): the applied named entries and their lower grades. Rows
		// new to every team are made and the table rewritten; the user's new ones wait for Deliver. Then the 팀컬러 n종
		// count (different names, any grade).
		public static void TeamColoursApplied(uint user, IEnumerable<int> entries)
		{
			if (s_cs == null || user == 0)
			{
				return;
			}
			List<int> rows = new List<int>();
			bool added = false;
			lock (s_dynamic)
			{
				foreach (int entry in entries.SelectMany(TeamColors.WithLowerGrades).Distinct())
				{
					if (!s_dynamicByEntry.TryGetValue(entry, out int idx))
					{
						idx = s_dynamic.Count == 0 ? DynamicFirst : s_dynamic.Keys.Max() + 1;
						Def d = DynamicDef(idx, entry);
						if (d == null || idx > DynamicLast)
						{
							continue;
						}
						using (SqliteConnection c = Open())
						using (SqliteCommand cmd = c.CreateCommand())
						{
							cmd.CommandText = "INSERT INTO team_color_achievement (idx, entry, created) VALUES ($i, $e, $t)";
							cmd.Parameters.AddWithValue("$i", idx);
							cmd.Parameters.AddWithValue("$e", entry);
							cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
							cmd.ExecuteNonQuery();
						}
						s_dynamic[idx] = d;
						s_dynamicByEntry[entry] = idx;
						s_nameKey[idx] = TeamColors.Achievement(entry).Value.nameKey;
						added = true;
					}
					rows.Add(idx);
				}
			}
			if (added && s_tablePath != null)
			{
				WriteTable(s_tablePath);
			}
			HashSet<int> cleared = Cleared(user);
			List<int> fresh = rows.Where(i => !cleared.Contains(i)).ToList();
			if (fresh.Count > 0)
			{
				using (SqliteConnection c = Open())
				using (SqliteTransaction tx = c.BeginTransaction())
				{
					foreach (int idx in fresh)
					{
						using SqliteCommand cmd = c.CreateCommand();
						cmd.Transaction = tx;
						cmd.CommandText = "INSERT OR IGNORE INTO achievement (user_code, idx, cleared) VALUES ($u, $i, $t)";
						cmd.Parameters.AddWithValue("$u", (long)user);
						cmd.Parameters.AddWithValue("$i", idx);
						cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
						cmd.ExecuteNonQuery();
						cleared.Add(idx);
					}
					tx.Commit();
				}
				s_pending.Add(user);
				ServerLog.Info($"업적 user {user}: 팀컬러 {string.Join(", ", fresh.Select(i => s_dynamic[i].Name))}");
			}
			int names;
			lock (s_dynamic)
			{
				names = cleared.Where(s_nameKey.ContainsKey).Select(i => s_nameKey[i]).Distinct().Count();
			}
			Missions.SetAtLeast(user, Missions.TeamColourNames, names);
		}

		// The season's records, post-season, promotion and awards for the user's club (LeagueAwards.Settle, once per
		// season, while the season's lines are there).
		public static void OnSeasonClosed(LeagueState s, List<LeagueAwards.Award> awards)
		{
			if (s_cs == null || s == null)
			{
				return;
			}
			uint user = s.UserCode;
			int me = s.MyIndex;
			List<PlayerSeason> lines = LeagueService.Store.PlayerLines(user, s.Season).Where(p => p.Club == me).ToList();
			List<PlayerSeason> bat = lines.Where(p => !p.Pitcher).ToList(), pit = lines.Where(p => p.Pitcher).ToList();
			var club = s.Clubs[me];
			int games = Math.Max(1, club.W + club.L + club.D);
			List<string> got = new List<string>();
			void Mark(bool ok, string key)
			{
				if (!ok) return;
				Missions.Add(user, key);
				got.Add(key);
			}
			int ab = bat.Sum(p => p.AB), hits = bat.Sum(p => p.H), outs = pit.Sum(p => p.Outs), er = pit.Sum(p => p.ER);
			Mark(bat.Sum(p => p.HR) >= 200, Missions.SeasonTeamHomers200);
			Mark(ab > 0 && hits >= 0.300 * ab, Missions.SeasonTeamAvg300);
			Mark(outs > 0 && er * 27.0 / outs <= 3.50, Missions.SeasonTeamEra350);
			Mark(club.W >= 100, Missions.SeasonWins100);
			Mark(bat.Any(p => p.HR >= 40), Missions.Season40Homers);
			Mark(bat.Any(p => p.HR >= 50), Missions.Season50Homers);
			Mark(bat.Any(p => p.PA >= Math.Floor(3.1 * games) && p.AB > 0 && p.H >= 0.400 * p.AB), Missions.Season400);
			Mark(bat.Any(p => p.H >= 200), Missions.Season200Hits);
			Mark(bat.Any(p => p.HR >= 30 && p.SB >= 30), Missions.Season3030);
			Mark(pit.Any(p => p.W >= 20), Missions.Season20Wins);
			Mark(pit.Any(p => p.SV >= 40), Missions.Season40Saves);
			Mark(pit.Any(p => p.PSO >= 200), Missions.Season200K);
			// Post-season place and the next league as LeagueService.Close works them out.
			int rank = s.RankOf(me), place = 0;
			if (LeagueRules.HasPostseason(s.Tier))
			{
				try
				{
					place = Postseason.Of(s).PlaceOf(me);
				}
				catch (Exception e)
				{
					ServerLog.Warn($"업적: no bracket for user {user} season {s.Season}: {e.Message}");
				}
			}
			Mark(place > 0, Missions.PostseasonIn);
			Mark(place == 1 || place == 2, Missions.KoreanSeriesIn);
			Mark(place == 1, Missions.KoreanSeriesWon);
			// By the six-tier numbering (before the switch this season's 메이저 counts as 레전드, 2026-11-08).
			int tier6 = Missions.Reached(s.Tier);
			Mark(place == 1 && tier6 == LeagueRules.MajorTier, Missions.MajorKoreanSeriesWon);
			Mark(place == 1 && tier6 == 4, Missions.LegendKoreanSeriesWon);
			Mark(place == 1 && tier6 == 5, Missions.ChampionsKoreanSeriesWon);
			int next = LeagueRules.NextTier(s.Tier, rank, place);
			Mark(next > s.Tier, Missions.Promotions);
			Missions.SetAtLeast(user, Missions.TierReached, Missions.Reached(next));
			// Awards: the 14 categories (a tie counts), the MVPs, a card holding three titles.
			List<LeagueAwards.Award> mine = awards.Where(a => a.Page < 2 && a.Winners.Any(w => w.Club == me)).ToList();
			if (mine.Count > 0)
			{
				Missions.Add(user, Missions.AwardTitles, mine.Count);
				got.Add($"타이틀 {mine.Count}");
			}
			Missions.SetAtLeast(user, Missions.AwardTitlesSeason, mine.Count);
			Mark(awards.Any(a => a.Page == 2 && a.Category == "리그 MVP" && a.Winners.Any(w => w.Club == me)), Missions.AwardMvp);
			Mark(awards.Any(a => a.Page == 2 && a.Category == "한국시리즈 MVP" && a.Winners.Any(w => w.Club == me)), Missions.AwardKsMvp);
			bool Crown(params string[] categories) => categories
				.Select(name => awards.FirstOrDefault(a => a.Category == name)?.Winners.Where(w => w.Club == me).Select(w => w.CardIdx) ?? Enumerable.Empty<int>())
				.Aggregate((x, y) => x.Intersect(y)).Any();
			Mark(Crown("타율", "홈런", "타점"), Missions.TripleCrownBatter);
			Mark(Crown("승리", "평균자책", "탈삼진"), Missions.TripleCrownPitcher);
			Evaluate(user);
			ServerLog.Info($"업적 시즌 기록 user {user} season {s.Season}: {(got.Count == 0 ? "없음" : string.Join(", ", got))}");
		}

		// Achievements paid before the S/A raise get the difference once, with a notice.
		private static void TopUp(GameSession session)
		{
			uint user = session.Team.UserCode;
			s_toppedUp.Add(user);
			List<int> old = new List<int>();
			using (SqliteConnection c = Open())
			{
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT idx FROM achievement WHERE user_code = $u AND delivered = 1 AND paid_mp IS NULL";
					cmd.Parameters.AddWithValue("$u", (long)user);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) old.Add(r.GetInt32(0));
				}
				foreach (int idx in old)
				{
					Def d = All.FirstOrDefault(x => x.Index == idx);
					using SqliteCommand mark = c.CreateCommand();
					mark.CommandText = "UPDATE achievement SET paid_mp = $m WHERE user_code = $u AND idx = $i";
					mark.Parameters.AddWithValue("$u", (long)user);
					mark.Parameters.AddWithValue("$i", idx);
					mark.Parameters.AddWithValue("$m", d == null ? 0 : Mp(d.Grade));
					mark.ExecuteNonQuery();
				}
			}
			List<Def> raised = old.Select(i => All.FirstOrDefault(x => x.Index == i)).Where(d => d != null && Mp(d.Grade) > OldMp(d.Grade)).ToList();
			int mp = raised.Sum(d => Mp(d.Grade) - OldMp(d.Grade));
			if (mp <= 0)
			{
				return;
			}
			session.Team.Mp = s_db.AddMp(user, mp);
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[]
			{
				new PLongString { str = "업적 보상 상향" },
				new PLongString { str = $"S·A 등급 업적 보상이 올랐습니다 (S 500만, A 200만 MP).\n이미 받은 {string.Join(", ", raised.Select(d => d.Name))}의 차액 {mp:N0} MP를 드립니다." }
			};
			session.Send(popup);
			TopBar.Send(session);
			ServerLog.Info($"[{session.Id}] 업적 차액 +{mp:N0} MP ({string.Join(", ", raised.Select(d => d.Name))})");
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_TEAM_MISSION packet)
		{
			Ma9MPacket.PKS_SC_ENTER_TEAM_MISSION reply = new Ma9MPacket.PKS_SC_ENTER_TEAM_MISSION();
			HashSet<int> cleared = session.Team == null ? new HashSet<int>() : Cleared(session.Team.UserCode);
			reply.cleared_mission.array = cleared.Where(i => Find(i) != null).OrderBy(i => i).Select(i => new PShort { n = (short)i }).ToArray();
			session.Send(reply);
		}

		// Self-test on a data copy: the counters' achievements are recorded once and the list is complete.
		public static int SelfTest(uint user, out string line)
		{
			Evaluate(user);
			HashSet<int> cleared = Cleared(user);
			bool unique = All.Select(d => d.Index).Distinct().Count() == All.Length;
			line = $"업적: {All.Length}개 (중복 번호 {(unique ? "없음" : "FAIL")}), 이 팀 달성 {cleared.Count}개: {string.Join(", ", All.Where(d => cleared.Contains(d.Index)).Select(d => d.Name))}";
			return unique ? 0 : 1;
		}
	}
}
