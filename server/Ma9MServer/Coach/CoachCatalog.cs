using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ma9MServer
{
	// 코칭스태프 cards and rules (systems session, 2026-11-04; the user's design, relayed by the engine session at 00:19).
	// Test servers only until the user has looked at it: everything is off unless MA9M_COACH=1.
	//
	// The five slots are the original's (eCoachType): 수석코치 HD, 투수코치 PC, 타격코치 HC, 트레이닝코치 in the old 스카우터
	// slot SC, 치어리더 CL. A coach belongs to one club (the card team codes 0-19 that 팀컬러 uses, 12 none), and every coach
	// but the 치어리더 works only for the cards of its club. Cards repeat (a team may hold the same card twice).
	// A club's coaches (user, 2026-11-04 00:4x: "투수, 타자코치만 올려주는 스탯에 따라 5종류 … 수석, 타자5, 투수5, 트레이닝코치,
	// 치어리더까지 총 13명", then "노말이랑 스페셜은 그냥 1종만 있는걸로 하자"): 노말 and 스페셜 one of each role (5); 레어 and 엘리트 one
	// 수석·트레이닝코치·치어리더 and a 타격·투수코치 for each of five stats (a batter's all but 수비, a pitcher's all but 체력), 13,
	// each its own picture; that stat +1 (레어) / +2 (엘리트) for the club (user: "1장은 컨택+1, 1장은 선구+1 …"). 36 a club.
	//
	// idx = 9,000,000 + club × 10,000 + role × 1,000 + grade × 100 + picture × 10 + stat: picture 1 and stat 0, except a
	// 레어·엘리트 타격·투수코치: picture 1-5 in BatterStats / PitcherStats order and stat that stat's training slot + 1. The client draws the picture of idx − idx % 10 (CoachCardDrawer, engine session).
	public static class CoachCatalog
	{
		public static readonly bool Enabled = Environment.GetEnvironmentVariable("MA9M_COACH") == "1";

		// Test conveniences (packs and SP topped up on the 코칭스태프 screen): MA9M_COACH_TEST=1.
		public static readonly bool TestMode = Enabled && Environment.GetEnvironmentVariable("MA9M_COACH_TEST") == "1";

		// Test servers only (MA9M_COACH_TEST=1 as well): MA9M_COACH_ALWAYS=1 makes the 타격·투수코치 come to every training he may
		// (still not without 50 SP). Test mode also seats a new 면담 group once all
		// are done, and anyone of the club (then anyone of the 1군) when nobody is 나쁨 or worse (CoachConsult.Seat).
		public static readonly bool AlwaysTraining = TestMode && Environment.GetEnvironmentVariable("MA9M_COACH_ALWAYS") == "1";

		// 타격·투수코치 on a training's top result, user 2026-11-04 07:40: "상한을 넘겨서 한 칸 더 올리기 보다는 +1 올라가는 연출이
		// 나왔으면 함 … 가장 좋은 결과가 나왔을 때 +4 이렇게 보일텐데, 코치 이벤트가 뜨면서 SP소모하고 나면 +5" — he comes on every
		// result and adds +1 (III +4 → +5, IV +5 → +6), shown by the client's coach effect (Material_Coachs 0…+6).

		public const int Base = 9_000_000;

		public const int HD = 0, PC = 1, HC = 2, SC = 3, CL = 4, Roles = 5;

		public const int Normal = 1, Special = 2, Rare = 3, Elite = 4;

		public const int Pictures = 5;

		public static readonly int[] Clubs = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 16, 17, 18, 19 };

		// Clubs with no player cards (the cards start in 1993): MBC, 청보, 삼미. Their cards stay valid, but no pack or 조합 makes
		// one (user, 2026-11-05 20:40: "93년 이전의 팀 코치 카드는 안 나오게 하는걸로", "태평양이랑 현대만 나오는게 좋지
		// 않을까?? LG쪽은 LG만 나오고").
		public static readonly int[] OldClubs = { 16, 17, 18 };

		public static readonly int[] PackClubs = Clubs.Except(OldClubs).ToArray();

		public static readonly string[] RoleNames = { "수석코치", "투수코치", "타격코치", "트레이닝코치", "치어리더" };

		private static readonly string[] RoleTypes = { "HD", "PC", "HC", "SC", "CL" };

		private static readonly string[] GradeTypes = { null, "Normal", "Special", "Rare", "Elite" };

		public static readonly string[] GradeNames = { null, "노말", "스페셜", "레어", "엘리트" };

		// The client's namestring_KR.team (the table's team column).
		private static readonly string[] ClientTeams =
		{
			"두산", "해태", "한화", "히어로즈", "현대", "KIA", "LG", "롯데", "삼성", "SK",
			"OB", "쌍방울", "국가대표", "NC", "빙그레", "태평양", "MBC", "청보", "삼미", "kt"
		};

		// Bonus stats as training slot + 1: a batter's all but 수비 (slot 4), a pitcher's all but 체력 (slot 0).
		public static readonly int[] BatterStats = { 1, 2, 3, 4, 6 }, PitcherStats = { 2, 3, 4, 5, 6 };

		// By grade (index 1-4), the user's numbers unless marked as a default of mine.
		// 타격·투수코치 in a training, %: 20/30/40/50 until 2026-11-12 (user: "투수, 타자코치가 트레이닝에 개입할 확률을 전체적으로 10% 증가").
		public static readonly int[] TrainingChance = { 0, 30, 40, 50, 60 };

		public static readonly int[] Visitors = { 0, 2, 3, 4, 5 };  // 수석코치: visitors a sitting (default: the original's 2-5, which the client's room shows)

		public static readonly int[] CheerPercent = { 0, 3, 6, 10, 15 };  // 치어리더: league-game MP

		public static readonly int[] TopMixBooks = { 0, 5, 4, 3, 2 };  // 트레이닝코치: III books for one IV

		// 트레이닝코치: the four result squares, %, for its club's cards. 2026-11-12 (user: "트레이닝 코치가 있을 때 지금 확률의 각각
		// -2%/-1%/+1%/+2%로 수정해서 반영합시다"): each grade's odds of 11-04 (25/25/25/25, 24/24/24/28, 23/23/23/31, 22/22/22/34)
		// moved −2/−1/+1/+2 points, so the 노말 one counts too.
		public static readonly int[][] ResultOdds =
		{
			null, new[] { 23, 24, 26, 27 }, new[] { 22, 23, 25, 30 }, new[] { 21, 22, 24, 33 }, new[] { 20, 21, 23, 36 }
		};

		public static readonly int[] SetBonus = { 0, 0, 1, 2, 3 };  // all five of one club: by the lowest grade, every 육성스탯 of its cards

		// Staff Point (the original's numbers, used where this game has the activity; the 카페 and 스카우터 rows are gone).
		public const int MaxSp = 2000, LoginSp = 50, LeagueWinSp = 30, TrainingSp = 50, ConsultSp = 150;

		// Coach cards a team may hold, copies counted (user 11-04: "보유 한도 100명으로 합시다"). The same card may be held more
		// than once (user 11-05: "선수카드랑 다르게 중복해서 획득이 되어야"); the client keys its lists by copy (CoachCardInfo.ma9mKey).
		public const int MaxCoaches = 100;

		// 코칭스태프 조합 (전력강화 slot 5, the original's Scene_CoachCardCombine), user 11-05 ("A 규칙을 적용하되, 같은 카드 4장을
		// 넣어야 승급되는걸로"): cards of one grade, none on the staff. Four copies of one card (club, role, grade; a 타격·투수
		// 코치's stat may differ) → that card one grade up; any other four → a random card one grade up; three 엘리트 → a random
		// 엘리트 (user: "남는 엘리트 3장으로 합시다. 나름 최상위 등급 카드인데, 조금은 효율적으로 쓰게"). Since 11-10 four of one
		// lineage give a card of that line, and three 엘리트 of one line a 엘리트 of that line (CoachHandlers.TryCoachMix).
		public const int MixCards = 4, EliteMixCards = 3;

		public static int MixCardsOf(int grade) => grade == Elite ? EliteMixCards : MixCards;

		// 코칭스태프 카드팩: one card, 30만 MP (user 11-05: "가격은 한 팩에 30만 MP정도"). Odds % for a club's five 엘리트 at 3억 on
		// average (user: "평균 3억이 맞는 것 같음, 40, 32, 18, 10은 너무 확률이 높은 것 같네"): with the 조합 above, 1,007 packs
		// (3.02억) on average, 981 median, 20% of teams within 737 and 80% within 1,261 (scratch packsim4.py, 3,000 runs).
		// Lineages from 11-05 19:35 (user: "확률 낮춰 3억 유지"): 12 lines instead of 19 clubs, 50/33/14/3 → 70/25/4/1 for a line's
		// five 엘리트 at 998 packs (2.99억), 20% within 778, 80% within 1,212 (packsim5.py, NC=12).
		public const int Pack = 400011, SetPack = 400012;

		public const int PackPrice = 300_000;

		// Per mille since 2026-11-10 (user 01:5x: "1 추천안으로", ① 83/14.5/2/0.5 %), with the 조합 of one lineage (4 of a line →
		// that line one grade up; 3 엘리트 of a line → a 엘리트 of that line): a line's five 엘리트 at about 3억 (packsim7.py).
		public static readonly int[] PackGradeOdds = { 0, 830, 145, 20, 5 };

		// A grade's pack odds in %, as the shop writes them (83, 14.5, 2, 0.5).
		public static double PackPercent(int grade) => PackGradeOdds[grade] * 100.0 / PackGradeOdds.Sum();

		public static string PackPercentText(int grade) => PackPercent(grade).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

		// Coaches work by club lineage (user, 2026-11-05 19:20: "코치·팀컬러 모두 계보로", TeamColors.Lineage): a staff may mix
		// MBC and LG cards and still be one LG set, and a KIA coach works for 해태 cards as well as KIA's.
		public static int Line(int club) => TeamColors.Lineage(club);

		public static int LineOf(int idx) => Line(Club(idx));

		// Whether the coach works for a card of team code `teamCode`.
		public static bool Serves(int coachIdx, int teamCode) => LineOf(coachIdx) == Line(teamCode);

		// A player card's lineage for the staff (2026-11-15; user 18:3x via the engine session: "FA 선수들은 자팀 효과를 받으니까, 팀컬러,
		// 코칭스태프 등의 효과를 받게끔"): an FA card is of every line, as in the 팀컬러 (TeamColors.IsMember), so any staff works for it.
		public static bool ServesCard(int coachIdx, CardInfo card) => card != null && (card.IsFA || Serves(coachIdx, card.TeamCode));

		public static bool InLine(CardInfo card, int line) => card != null && line >= 0 && (card.IsFA || Line(card.TeamCode) == line);

		// The lineages that have coaches, each with its pack clubs (a pack draws a line first, then one of its clubs).
		private static readonly int[][] Lines = PackClubs.GroupBy(Line).Select(g => g.ToArray()).ToArray();

		// The pack clubs of a club's lineage (청보 → 현대, 태평양).
		public static int[] LineClubs(int club) => Lines.First(l => Line(l[0]) == Line(club));

		public static int Idx(int club, int role, int grade, int picture, int stat) => Base + club * 10_000 + role * 1_000 + grade * 100 + picture * 10 + stat;

		public static int Club(int idx) => (idx - Base) / 10_000;

		public static int Role(int idx) => idx / 1_000 % 10;

		public static int Grade(int idx) => idx / 100 % 10;

		public static int Picture(int idx) => idx / 10 % 10;

		public static int Stat(int idx) => idx % 10;

		// A 레어·엘리트 타격·투수코치 (one of five stats).
		public static bool HasStat(int role, int grade) => (role == PC || role == HC) && grade >= Rare;

		public static int[] StatsOf(int role) => role == HC ? BatterStats : PitcherStats;

		// The card of a club, role and grade (a 타격·투수코치 for that stat, training slot + 1).
		public static int Card(int club, int role, int grade, int stat = 0) =>
			HasStat(role, grade) ? Idx(club, role, grade, Array.IndexOf(StatsOf(role), stat) + 1, stat) : Idx(club, role, grade, 1, 0);

		// What a card's bonus stat gives (레어 +1, 엘리트 +2).
		public static int StatBonus(int grade) => grade == Elite ? 2 : grade == Rare ? 1 : 0;

		public static bool IsCoach(int idx)
		{
			if (idx < Base || idx >= Base + 200_000) return false;
			int role = Role(idx), grade = Grade(idx), picture = Picture(idx), stat = Stat(idx);
			if (Array.IndexOf(Clubs, Club(idx)) < 0 || role >= Roles || grade < Normal || grade > Elite) return false;
			return HasStat(role, grade) ? picture >= 1 && picture <= Pictures && StatsOf(role)[picture - 1] == stat : picture == 1 && stat == 0;
		}

		public static string Name(int idx) => TeamColors.FranchiseName(Club(idx)) + " " + RoleNames[Role(idx)];

		// The card window's description, one "\n" part (one dot) per job, naming the clubs it works for (user, 2026-11-05: "같은
		// 구단 … 그 팀 이름으로 하면 되는것 아닌가?", "동그라미는 3개 쓰지 않아도 됨", and no 깡스탯·육성스탯 words: a bonus just shows
		// as the orange number under the stat). It goes in ma9_Coach_Index.txt's last column, which the client shows after the
		// string table's description; those rows are left out for these roles, so this is the whole text (null for 치어리더).
		public static string Description(int idx)
		{
			int role = Role(idx), grade = Grade(idx);
			string line = TeamColors.LineageName(Club(idx));
			switch (role)
			{
				case HD:
					return $"1군의 {line} 선수 중 컨디션 나쁜 선수가 면담실로 찾아옵니다.\\n{ConsultSp} SP 면담: 컨디션 1~3단계 회복(12시간)";
				case PC:
				case HC:
					string who = role == PC ? "투수" : "타자";
					string text = $"{line} {who}의 트레이닝에 등장\\n{TrainingSp} SP를 사용하면 결과 +1";
					return HasStat(role, grade) ? text + $"\\n{line} {who} {StatName(role, Stat(idx))} +{StatBonus(grade)}" : text;
				case SC:
					return $"북 조합: III {TopMixBooks[grade]}권 → IV(+2~+5) 1권\\n{line} 선수의 트레이닝 결과 확률 {string.Join("/", ResultOdds[grade])}%";
				default:
					return null;
			}
		}

		public static string StatName(int role, int stat) => TrainingBooks.StatNames[(role == HC ? 0 : 6) + stat];

		// Every card: 19 clubs × 36.
		public static IEnumerable<int> All()
		{
			foreach (int club in Clubs)
			{
				for (int role = 0; role < Roles; role++)
				{
					for (int grade = Normal; grade <= Elite; grade++)
					{
						foreach (int stat in HasStat(role, grade) ? StatsOf(role) : new[] { 0 })
						{
							yield return Card(club, role, grade, stat);
						}
					}
				}
			}
		}

		// 자팀 코치 (user, 2026-11-13 00:5x: "타자코치랑 투수코치까지 원하는 스탯으로 뽑으려니까 빡세네? 자팀 코치가 뜰 확률을 조금
		// 높일까?" → 20%, 자팀 = 1군에 가장 많은 계보): a pack's card is of the team's own lineage this often, else of one of the
		// other 11 alike (until then all 12 alike, 8.3% each). A line's five 엘리트 with the wanted 타격·투수 stats: 7.8억 → 5.1억
		// MP on average, any stats 2.9억 → 2.0억 (scratch coachpack_sim.py, 400 runs each). 조합 keep every line alike.
		public const int OwnLinePercent = 20;

		// A pack's card for a team whose own lineage is `ownLine` (CoachHandlers.OwnLine; -1 or a line without coaches: all alike).
		public static int RollPack(Random rng, int ownLine)
		{
			int[] own = ownLine < 0 ? null : Lines.FirstOrDefault(l => Line(l[0]) == ownLine);
			if (own == null) return Roll(rng);
			int[] line;
			if (rng.Next(100) < OwnLinePercent)
			{
				line = own;
			}
			else
			{
				int[][] others = Lines.Where(l => l != own).ToArray();
				line = others[rng.Next(others.Length)];
			}
			return Roll(rng, 0, line[rng.Next(line.Length)]);
		}

		// One card: grade by the pack's odds (or the given one), then any of the grade's coaches (5, or 13 from 레어) of any
		// pack club (or of the given club and role; an old club gives way to one of its line's).
		public static int Roll(Random rng, int grade = 0, int club = -1, int role = -1)
		{
			if (grade == 0)
			{
				int u = rng.Next(PackGradeOdds.Sum());
				grade = Normal;
				while (grade < Elite && u >= PackGradeOdds[grade])
				{
					u -= PackGradeOdds[grade];
					grade++;
				}
			}
			// A lineage first, then one of its clubs, so a line of two clubs (현대·태평양) comes no oftener than 롯데.
			if (club < 0)
			{
				int[] line = Lines[rng.Next(Lines.Length)];
				club = line[rng.Next(line.Length)];
			}
			else if (Array.IndexOf(OldClubs, club) >= 0)
			{
				int[] line = LineClubs(club);
				club = line[rng.Next(line.Length)];
			}
			// The five roles alike at every grade, then a 레어·엘리트 타격·투수코치's stat (until 11-05 레어 and up drew from the 13
			// cards, so a 타격·투수코치 came five times as often as the others).
			role = role >= 0 ? role : rng.Next(Roles);
			return Card(club, role, grade, HasStat(role, grade) ? StatsOf(role)[rng.Next(Pictures)] : 0);
		}

		// The 능력치 다시 뽑기 of three 엘리트 타격·투수코치 of one line (user, 2026-11-14: "롯데 타격코치 3명 넣었는데, 각각 정신, 정신,
		// 스피드였음. 그럼 조합결과가 타격코치이고 컨택, 파워, 선구 중에서 나왔어야하는데 왜 다시 스피드가 나온 것인지"): the same
		// role at that grade, its stat drawn from the ones none of the cards put in had (three cards leave at least two of five).
		public static int RollOtherStat(Random rng, int grade, int club, int role, IEnumerable<int> had)
		{
			int[] left = StatsOf(role).Except(had).ToArray();
			if (left.Length == 0) return Roll(rng, grade, club, role);
			if (Array.IndexOf(OldClubs, club) >= 0)
			{
				int[] line = LineClubs(club);
				club = line[rng.Next(line.Length)];
			}
			return Card(club, role, grade, left[rng.Next(left.Length)]);
		}

		// Comment codes in ma9_Coach_String.txt (the room's line, the training coach's two lines).
		public const int RoomVisitors = 2001, RoomNobody = 2002, RoomNoCoach = 2003, RoomDone = 2004;

		public const int HitterMeet = 2101, HitterMeetLines = 5, HitterDone = 2151, HitterDoneLines = 3;

		public const int PitcherMeet = 2201, PitcherMeetLines = 5, PitcherDone = 2251, PitcherDoneLines = 3;

		// The tables the client reads (ResourceManager: ma9_Coach_Index.txt, ma9_Coach_String.txt, ConsultingRoom.txt), into
		// data/tables when coaches are on; otherwise they are left alone, so the live server's bundle keeps none of them.
		public static void WriteTables(string tablesDir)
		{
			if (!Enabled) return;
			StringBuilder index = new StringBuilder("Code\tYear\tName\tTeam\tType\tGrade\tBonus\n");
			foreach (int idx in All())
			{
				index.Append($"{idx}\t0\t{Name(idx)}\t{ClientTeams[Club(idx)]}\t{RoleTypes[Role(idx)]}\t{GradeTypes[Grade(idx)]}\t{Description(idx) ?? string.Empty}\n");
			}
			Write(tablesDir, "ma9_Coach_Index.txt", index.ToString());
			Write(tablesDir, "ma9_Coach_String.txt", StringTable());
			Write(tablesDir, "ConsultingRoom.txt", ConsultTable());
		}

		private static void Write(string dir, string name, string text)
		{
			string path = Path.Combine(dir, name);
			if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
			{
				File.WriteAllText(path, text, new UTF8Encoding(false));
			}
		}

		// Code, coach type, state, text. State grade × 10 is the card window's 능력치 line, grade × 10 + 1 its description
		// (lines split at a written "\n", each with a dot); the comments have state 0.
		private static string StringTable()
		{
			StringBuilder sb = new StringBuilder("Code\tUser\tState\tString\n");
			void Row(int code, int role, int state, string text) => sb.Append($"{code}\t{RoleTypes[role]}\t{state}\t{text}\n");
			for (int grade = Normal; grade <= Elite; grade++)
			{
				int code = 1000 + grade * 10;
				Row(100 + code, HD, grade * 10, $"면담 선수 최대 {Visitors[grade]}명 (0시·12시)");
				// The descriptions of 수석·투수·타격·트레이닝코치 name the card's clubs, so they are per card (Description, the index's
				// last column) and their rows are left out here (user 11-05).
				foreach (int role in new[] { PC, HC })
				{
					// User 11-04: "트레이닝 중 40% 확률로 등장".
					Row(100 * role + 100 + code, role, grade * 10, $"트레이닝 중 {TrainingChance[grade]}% 확률로 등장");
				}
				Row(400 + code, SC, grade * 10, $"III 트레이닝북 {TopMixBooks[grade]}권 → IV 1권");
				Row(500 + code, CL, grade * 10, $"리그 경기 MP +{CheerPercent[grade]}%");
				Row(501 + code, CL, grade * 10 + 1, $"리그 경기 MP +{CheerPercent[grade]}%(모든 구단)"
					+ "\\n매일 고급 회복제·MP주머니 선물");
			}
			Row(RoomVisitors, HD, 0, "감독님, 컨디션이 떨어진 선수들이 면담을 기다리고 있습니다.");
			// User 11-10 (박석민 report: 컨디션 나쁨인데 "찾아온 선수가 없습니다"; he was under a 면담 effect): the reason either way. Two lines of
			// the 380 px label at size 14, as the old text (354 + 290 px by PopupText.Px).
			Row(RoomNobody, HD, 0, "지금은 찾아온 선수가 없습니다. 1군 선수의 컨디션이 나빠지거나 면담 효과가 끝나면 찾아옵니다.");
			Row(RoomNoCoach, HD, 0, "수석코치를 등록하면 선수들이 면담실로 찾아옵니다.");
			Row(RoomDone, HD, 0, "이번 면담은 모두 마쳤습니다. 다음 면담은 0시와 12시입니다.");
			string[] hitterMeet =
			{
				"감독님, 이 친구 스윙을 조금만 손보면 훨씬 좋아지겠습니다.", "제가 한번 붙어서 가르쳐 볼까요?",
				"방금 타격, 타이밍만 잡으면 더 나아질 겁니다.", "이 선수는 제가 잘 압니다. 잠깐만 맡겨 주시죠.",
				"좋은 훈련이었습니다. 제가 마무리를 도와도 될까요?"
			};
			string[] pitcherMeet =
			{
				"감독님, 투구 폼을 조금만 잡아 주면 공이 더 살아나겠습니다.", "제가 불펜에서 한번 봐줄까요?",
				"릴리스 포인트만 다듬으면 한 단계 올라갈 수 있습니다.", "이 투수는 제가 잘 압니다. 잠깐만 맡겨 주시죠.",
				"좋은 훈련이었습니다. 제가 마무리를 도와도 될까요?"
			};
			string[] done = { "역시 감이 좋은 선수입니다. 한 단계 올라갔습니다!", "보셨습니까? 금방 알아듣네요.", "이 정도면 다음 경기가 기대됩니다." };
			for (int k = 0; k < HitterMeetLines; k++) Row(HitterMeet + k, HC, 0, hitterMeet[k]);
			for (int k = 0; k < HitterDoneLines; k++) Row(HitterDone + k, HC, 0, done[k]);
			for (int k = 0; k < PitcherMeetLines; k++) Row(PitcherMeet + k, PC, 0, pitcherMeet[k]);
			for (int k = 0; k < PitcherDoneLines; k++) Row(PitcherDone + k, PC, 0, done[k]);
			return sb.ToString();
		}

		// 면담 dialogues (user: "랜덤한 선택지 3개가 등장하고 각각 1, 2, 3단계 컨디션 상승의 멘트 중 하나 … 멘트는 사실 거기서 거기인데, 컨디션
		// 자체는 랜덤하게 상승하는 느낌으로"). A row: the 감독's opening, the player's answer, three choices (button text and the
		// 감독's line), the player's last line. The client does not say which choice was taken, and the choices mean nothing
		// apart, so the server rolls the 1-3 steps at the end, each a third (the same as three shuffled +1/+2/+3 lines).
		// Rows are numbered type × 1000 + reason × 100 + opening × 10 + variant: type 1 batter (H), 2 pitcher (P); reason
		// 0 condition, 1 recent form.
		public const int ConsultVariants = 6;

		public static int ConsultRow(bool batter, bool slump, int opening, int variant) => (batter ? 1000 : 2000) + (slump ? 100 : 0) + opening * 10 + variant;

		private static readonly (string q, string a)[][] Openings =
		{
			new[]  // batter, condition
			{
				("요즘 몸이 무거워 보이던데, 무슨 일 있나?", "감독님… 요즘 컨디션이 영 안 올라옵니다.\\n배트가 무겁게 느껴져요."),
				("표정이 안 좋군. 잠은 잘 자고 있나?", "솔직히 요즘 잠을 설칩니다.\\n몸이 생각대로 안 움직여요.")
			},
			new[]  // batter, form
			{
				("최근 타석에서 많이 답답해 보이더군.", "네… 공이 잘 안 보입니다.\\n뭘 해도 안 맞는 것 같아요."),
				("요즘 성적 때문에 마음고생이 많지?", "팀에 보탬이 못 돼서 죄송합니다.\\n타석에 서면 자꾸 조급해져요.")
			},
			new[]  // pitcher, condition
			{
				("공이 무거워 보이던데, 어디 불편한가?", "어깨가 좀 뻐근합니다.\\n공에 힘이 안 실리는 것 같아요."),
				("요즘 지쳐 보이는군. 괜찮나?", "몸이 계속 무겁습니다.\\n회복이 잘 안 되는 느낌이에요.")
			},
			new[]  // pitcher, form
			{
				("최근 등판에서 많이 맞았지.", "네… 제구가 마음대로 안 됩니다.\\n자신감이 많이 떨어졌어요."),
				("마운드에서 많이 쫓기는 것 같더군.", "주자만 나가면 머리가 하얘집니다.\\n제 공을 못 던지고 있어요.")
			}
		};

		private static readonly (string button, string line)[] Choices =
		{
			("푹 쉬고 오게", "오늘은 생각 말고 푹 쉬게.\\n몸이 먼저일세."),
			("자네를 믿네", "자네 실력은 내가 제일 잘 아네.\\n난 자네를 믿고 있어."),
			("기본으로 돌아가게", "잘될 때 하던 대로만 하게.\\n기본이 제일 중요하네."),
			("부담 갖지 말게", "결과는 내가 책임지겠네.\\n자네는 마음 편히 하게."),
			("같이 이겨내 보세", "혼자 끙끙 앓지 말게.\\n코치진이 다 도와줄 걸세."),
			("작은 것부터 하세", "오늘은 하나만 생각하세.\\n그거면 충분하네."),
			("팀이 자네 편이야", "동료들도 자네를 기다리고 있네.\\n힘내게."),
			("몸부터 챙기게", "트레이너와 같이 몸부터 챙기게.\\n그다음은 금방일세."),
			("웃으면서 하게", "야구는 즐거워야 잘되는 법이네.\\n어깨 펴게.")
		};

		private static readonly string[] LastLines =
		{
			"감사합니다, 감독님!\\n다시 해 보겠습니다.", "말씀 듣고 나니 한결 가볍습니다.\\n꼭 보답하겠습니다.", "네, 마음 단단히 먹겠습니다!"
		};

		// index, type, opening, answer, (choice, line) × 4 (the fourth empty: the patched client hides it), last line,
		// effects (CR_Effect name and two numbers, for the record; the result comes in CONSULTING_END).
		private static string ConsultTable()
		{
			StringBuilder sb = new StringBuilder("index\ttype\tintro_q\tintro_a\tselect1\toutput1\tselect2\toutput2\tselect3\toutput3\tselect4\toutput4\tanswer\teffect\tmin\tmax\n");
			Random rng = new Random(20261104);
			for (int kind = 0; kind < Openings.Length; kind++)
			{
				bool batter = kind < 2, slump = kind % 2 == 1;
				for (int opening = 0; opening < Openings[kind].Length; opening++)
				{
					for (int variant = 0; variant < ConsultVariants; variant++)
					{
						int[] picks = Enumerable.Range(0, Choices.Length).OrderBy(_ => rng.Next()).Take(3).ToArray();
						(string q, string a) = Openings[kind][opening];
						sb.Append($"{ConsultRow(batter, slump, opening, variant)}\t{(batter ? "H" : "P")}\t{q}\t{a}");
						foreach (int p in picks) sb.Append($"\t{Choices[p].button}\t{Choices[p].line}");
						sb.Append($"\t\t\t{LastLines[rng.Next(LastLines.Length)]}\tCONDITION\t1\t3\n");
					}
				}
			}
			return sb.ToString();
		}
	}
}
