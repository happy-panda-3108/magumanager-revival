using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ma9MServer
{
	// League rules for solo play (user, 2026-10-18): ten clubs, a game every 20 minutes, 144 games
	// in two days, then the postseason (from 2026-10-21, LeagueCalendar and Postseason). Five tiers:
	// 비기너, 루키, 마이너, 메이저 and, from 2026-10-28, 챔피언스 (user: "메이저 다음으로 챌린저스 리그"; renamed 챔피언스
	// 2026-10-29: "챌린저스가 아니라 챔피언스가 맞았음"), where the real teams that reach it play in one league together
	// and team-year AI decks fill the other places (LeagueService's shared leagues, AiTeams.Decks).
	public static class LeagueRules
	{
		public const int Clubs = 10;
		public const int Games = 144;
		public const int SlotMinutes = 20;
		public const int Series = 3;    // the client shows every schedule row as a three-game series
		// 레전드 리그 (user, 2026-11-08: "메이져 위에 레전드 리그를 만들고 그 위에 챔피언스 … 기존 메이져리그의 역할이 레전드리그가 되는거고,
		// 챔피언스 리그 전 관문 역할 … 메이져리그는 기존 리그랑 같은 역할 … 마이너리그 이름을 트리플A로"; asked the same evening: from the
		// next season, today's 메이저 counting as 레전드). Six tiers from the season starting NewStructureFrom: 비기너 0, 루키 1,
		// 트리플A 2, 메이저 3, 레전드 4, 챔피언스 5. Until then the five (챔피언스 4). The switch happens once, at that 편성, after
		// every league of the old season has closed (LeagueService.RenewAi → LeagueStore.MigrateTiers6): the stored tiers of
		// 메이저 and 챔피언스 move up one, so this season's 메이저 clubs go on as 레전드 by their finish (1-3 챔피언스, 4-6 레전드,
		// 7-10 메이저) and 마이너's top four go up to the new 메이저. Everything below reads the arrays and tiers of the
		// structure in force (NewStructure, kept in league_fix as 'tiers6').
		public static bool NewStructure { get; private set; }

		public static readonly DateTime NewStructureFrom = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Local);

		public static void UseNewStructure(bool on = true)
		{
			NewStructure = on;
			if (!on) Seven = false;
		}

		// 더블A (user, 2026-11-14 20:5x: "더블 A는 팀-연도 덱에 트리플A에서 사용되는 덱보다 순위가 낮은 조금 약한 덱들이 나오고, 팀컬러는 B,
		// 특강은 없음, 트레이닝은 1과목 +4, 재계약 3, 코치 없음, 주전 2~3명 후보로, 특성은 다른 리그처럼 더블A 특성의 90% 사용 / 더블 A의 볼
		// 한도를 140으로 두고, 트리플 A부터 볼 한도를 5씩 올리겠음 / 리그 자체의 특성 포인트 제한은 12, 15, 18, 20, 22, 24, 26"; MP "1, 1.3,
		// 1.6, 1.8, 2.0, 2.3, 2.6"; from the season starting 11-16 00:00): seven tiers, 비기너 0, 루키 1, 더블A 2, 트리플A 3, 메이저 4,
		// 레전드 5, 챔피언스 6. Like the six-tier switch, once, at the first 편성 for a season from SevenFrom (LeagueStore.MigrateTiers7,
		// league_fix 'tiers7'): every stored tier from 트리플A up moves up one. Seven implies NewStructure (the six-tier code paths:
		// deck tiers from 2 up, fixed clubs for 비기너 and 루키, 레전드 as the gate).
		public static bool Seven { get; private set; }

		public static readonly DateTime SevenFrom = new DateTime(2026, 11, 16, 0, 0, 0, DateTimeKind.Local);

		public static void UseSeven(bool on = true)
		{
			Seven = on;
			if (on) NewStructure = true;
		}

		// Shared leagues in every tier (user 2026-11-18 01:0x: "이제부터는 같은 리그가 되면 최대한 유저 계정들끼리 매칭되게끔해줘 기존에는
		// 챔피언스 리그만 그렇게 하고 있었는데 이제 슬슬 만나게 할거야"): at 편성 the real teams going to the same tier play one shared
		// league (LeagueService.FormLeagues); a team alone in its tier gets a league of its own as before. MA9M_SHARED_ALL=0 in
		// run_server.bat goes back to 챔피언스 only.
		public static readonly bool SharedAll = Environment.GetEnvironmentVariable("MA9M_SHARED_ALL") != "0";

		public static int Tiers => Seven ? 7 : NewStructure ? 6 : 5;
		// 메이저 (3 with five or six tiers, 4 with seven).
		public static int MajorTier => Seven ? 4 : 3;
		public static int DoubleATier => Seven ? 2 : -1;
		public static int LegendTier => Seven ? 5 : NewStructure ? 4 : -1;
		public static int ChampionsTier => Seven ? 6 : NewStructure ? 5 : 4;
		// The tier whose top three go up to 챔피언스 (메이저 before, 레전드 after).
		public static int GateTier => Seven ? 5 : NewStructure ? 4 : 3;
		// An old-structure tier in the six-tier numbering.
		public static int ToNew(int oldTier) => oldTier >= 3 ? oldTier + 1 : oldTier;
		// A seven-tier tier in the six-tier numbering (achievements count in it, Missions.Reached): 더블A counts as 루키.
		public static int ToSix(int tier) => tier <= 1 ? tier : tier == 2 ? 1 : tier - 1;
		// Innings (user, 2026-10-26): league and exhibition games go to 11 and then end level, as the KBO
		// since 2025; the postseason before the 한국시리즈 plays on until there is a winner (끝장승부), and
		// the 한국시리즈 goes to 11 with extra games for ties (Postseason). EndlessInnings caps the 끝장승부
		// for the broadcast's one-byte scene count (an 18-inning game is ~205 scenes of the 255; a game
		// still level then is played again). The scoreboards show innings 1-11 and an "X" column holding
		// the runs from the 12th on (LeagueHandlers.Innings; the client's board art says X there).
		public const int MaxInnings = 11;
		public const int EndlessInnings = 18;

		public static string[] TierNames => Seven ? TierNames7 : NewStructure ? TierNames6 : TierNames5;
		private static readonly string[] TierNames5 = { "비기너", "루키", "마이너", "메이저", "챔피언스" };
		private static readonly string[] TierNames6 = { "비기너", "루키", "트리플A", "메이저", "레전드", "챔피언스" };
		private static readonly string[] TierNames7 = { "비기너", "루키", "더블A", "트리플A", "메이저", "레전드", "챔피언스" };
		// eLeagueGrade the client shows: 마이너 takes the SINGLE_A slot (badges redrawn), 챔피언스 the CHAMPIONS one with its
		// original blue badges (user, 2026-10-28: "원래 파란색 배지로 … 그걸 썼으면"). Six tiers: 트리플A and 레전드 take the original
		// TRIPLE_A (purple) and LEGEND (orange) badges, which every atlas still has.
		public static byte[] TierGrade => Seven ? TierGrade7 : NewStructure ? TierGrade6 : TierGrade5;
		private static readonly byte[] TierGrade5 = { 0, 1, 2, 5, 9 };
		private static readonly byte[] TierGrade6 = { 0, 1, 4, 5, 7, 9 };
		// Seven tiers: 더블A takes the original DOUBLE_A (3; the app still has its "doubleA" badges and the name 더블A).
		private static readonly byte[] TierGrade7 = { 0, 1, 3, 4, 5, 7, 9 };
		// Team-trait points per tier (user, 2026-10-18; 챔피언스 +3, 2026-10-28; six tiers, user 2026-11-08: "챔피언스를 24로 상향,
		// 레전드가 23, 메이저가 21, 트리플A가 18, 루키가 15, 비기너가 12").
		public static int[] TraitPoints => Seven ? TraitPoints7 : NewStructure ? TraitPoints6 : TraitPoints5;
		private static readonly int[] TraitPoints5 = { 11, 14, 17, 20, 23 };
		private static readonly int[] TraitPoints6 = { 12, 15, 18, 21, 23, 24 };
		private static readonly int[] TraitPoints7 = { 12, 15, 18, 20, 22, 24, 26 };
		// Roster ball cap per tier, and the next day's (챔피언스 155, user 2026-10-28; a team-year deck is 147 at most). Six
		// tiers (user, 2026-11-08: "새 메이저만 사이값 145"): 레전드 keeps 메이저's 150.
		public static int[] BallCap => Seven ? BallCap7 : NewStructure ? BallCap6 : BallCap5;
		private static readonly int[] BallCap5 = { 120, 130, 140, 150, 155 };
		private static readonly int[] BallCap6 = { 120, 130, 140, 145, 150, 155 };
		private static readonly int[] BallCap7 = { 120, 130, 140, 145, 150, 155, 160 };
		// AI clubs' ball total per tier: 95% of the tier's cap, ±1 (user, 2026-10-19, after pitcher fatigue
		// left 85-ball 비기너 clubs with 43-56 stamina starters: 120 → 114, 130 → 123.5, 140 → 133, 150 → 142.5).
		// AiTeams regenerates a tier's fixed clubs when they fall outside it. 챔피언스 has no fixed clubs: its AI places
		// go to team-year decks with their own ball totals (AiTeams.Decks).
		// Six tiers: only 비기너 and 루키 keep fixed clubs; 트리플A up draw team-year decks (AiTeams.RuleOf).
		public static (int lo, int hi)[] AiBalls => Seven ? AiBalls7 : NewStructure ? AiBalls6 : AiBalls5;
		private static readonly (int lo, int hi)[] AiBalls5 = { (113, 115), (123, 124), (132, 134), (142, 143), (0, 0) };
		private static readonly (int lo, int hi)[] AiBalls6 = { (113, 115), (123, 124), (0, 0), (0, 0), (0, 0), (0, 0) };
		private static readonly (int lo, int hi)[] AiBalls7 = { (113, 115), (123, 124), (0, 0), (0, 0), (0, 0), (0, 0), (0, 0) };
		// League-game MP (user-approved draft): win / tie / loss, times the tier's multiplier. Raised ×1.2
		// (user, 2026-10-19: 리그 승패 보너스 1.2배) from 20,000 / 14,000 / 10,000. The 시즌 완주 보너스 and the 리그 시상 use the
		// same multipliers (user, 2026-10-28: 1.0 / 1.15 / 1.3 / 1.5 / 1.8, then the same day "비기너 1.0 / 루키 1.25 / 마이너 1.5
		// / 메이저 2.0 / 챔피언스 2.5").
		public const int WinMp = 24000, TieMp = 16800, LossMp = 12000;
		// Six tiers (user, 2026-11-08 22:3x: "레전드는 2.3 챔피언스는 2.6으로 진행합시다"), from the 11-11 season; this season ends on
		// the five tiers' 2.5. A 1.0.35+ client shows a game's MP in full (LeagueHandlers GetPoints); older ones show 65,535 at
		// most in the lists (2.6 × 24,000 = 62,400 fits anyway).
		public static double[] RewardMult => Seven ? RewardMult7 : NewStructure ? RewardMult6 : RewardMult5;
		private static readonly double[] RewardMult5 = { 1.0, 1.25, 1.5, 2.0, 2.5 };
		private static readonly double[] RewardMult6 = { 1.0, 1.25, 1.5, 2.0, 2.3, 2.6 };
		// Seven tiers (user, 2026-11-14 20:5x: "1, 1.3, 1.6, 1.8, 2.0, 2.3, 2.6 이렇게 합시다"; 루키 1.25 → 1.3, 트리플A 1.5 → 1.8).
		private static readonly double[] RewardMult7 = { 1.0, 1.3, 1.6, 1.8, 2.0, 2.3, 2.6 };
		// AI clubs' development: bonus points on every stat (played at the development weight). 메이저's fixed clubs
		// and 챔피언스's decks carry per-card development instead (AiTeams); with six tiers every deck tier does.
		public static int[] AiBonus => Seven ? AiBonus7 : NewStructure ? AiBonus6 : AiBonus5;
		private static readonly int[] AiBonus5 = { 0, 6, 12, 18, 0 };
		private static readonly int[] AiBonus6 = { 0, 6, 0, 0, 0, 0 };
		private static readonly int[] AiBonus7 = { 0, 6, 0, 0, 0, 0, 0 };
		// Promotion / relegation (user, 2026-10-21; until then by final rank, 3/5/3 up and 3/5 down):
		// 루키 and 마이너 play a postseason, and its top four go up (the champion, the runner-up, the PO and the
		// 준PO losers); 비기너 has none, and its regular-season top four go up. The bottom three of the
		// regular season go down (not from 비기너).
		// 메이저 (user, 2026-10-28: "메이저리그 3위까지 승급, 4~6등은 잔류, 7~10등은 강등"), by the final standing (the
		// postseason place for its five clubs, the regular-season rank for the rest): 1-3 go up, 4-6 stay, 7-10 go down.
		// 챔피언스, the top tier (user, 2026-10-28: "1~3위만 잔류, 나머지는 전부 강등"; that morning it had 메이저's old rule, the
		// top four staying): the top three stay and every other club goes down.
		public static bool HasPostseason(int tier) => tier >= 1;
		public const int PromotedPlaces = 4;
		public const int RelegatedPlaces = 3;
		public const int PostseasonClubs = 5;
		public const int MajorPromoted = 3, MajorKept = 6;
		public const int TopTierKept = 3;

		// The next season's tier from the regular-season rank and the postseason place (1 champion .. 5
		// wild-card loser; 0 = none).
		public static int NextTier(int tier, int rank, int postPlace)
		{
			int place = HasPostseason(tier) ? postPlace : rank;
			// The final standing: the postseason place, else the regular-season rank (6-10 play no postseason).
			int final = place >= 1 ? place : rank;
			// The gate to 챔피언스 (메이저, with six tiers 레전드) has the 1-3 up / 4-6 stay / 7-10 down rule; the new 메이저 plays as
			// 루키 and 트리플A do (user, 2026-11-08: "메이져리그는 기존 리그랑 같은 역할").
			if (tier >= ChampionsTier) return final <= TopTierKept ? ChampionsTier : GateTier;
			if (tier == GateTier) return final <= MajorPromoted ? ChampionsTier : final <= MajorKept ? GateTier : GateTier - 1;
			bool up = place >= 1 && place <= PromotedPlaces;
			if (rank > Clubs - RelegatedPlaces) return Math.Max(0, tier - 1);
			return up ? tier + 1 : tier;
		}

		// AI clubs' team traits: three balanced test builds per tier (tools/traitsim/traits.json, all
		// within the tier's points): batting all-in, pitching all-in and a split. The nine AI clubs
		// take them in turn (3/3/3), so the league's run level stays put; traits are outside the
		// league reference. A first mix by club number (6 batting to 2 pitching) lifted 비기너 R/G ~0.9.
		// Six tiers: 비기너 / 루키 / 트리플A / 메이저 the old 비기너 / 루키 / 마이너 / 메이저 rows, 레전드 and 챔피언스 the old 챔피언스 row
		// (every build is cut to 90% of its tier's points: 10 / 13 / 16 / 18 / 20 / 21).
		public static string[][] AiTraitBuilds => Seven ? AiTraitBuilds7 : NewStructure ? AiTraitBuilds6 : AiTraitBuilds5;
		private static readonly string[][] AiTraitBuilds5 =
		{
			new[] { "101:2,102:2,104:2,105:2,106:2,112:1", "121:2,122:2,124:2,125:2,126:2,131:1", "101:2,102:2,104:2,121:2,122:2,124:1" },
			new[] { "101:2,102:2,104:2,105:2,106:2,112:2,110:2", "121:2,122:2,124:2,125:2,126:2,131:2,132:2", "101:2,102:2,103:2,104:2,121:2,122:2,124:2" },
			new[] { "101:2,102:2,104:2,105:2,106:2,112:2,110:2,114:2,113:1", "121:2,122:2,124:2,125:2,126:2,131:2,132:2,133:1,134:2", "101:2,102:2,103:2,104:2,121:2,122:2,124:2,125:2,126:1" },
			new[]
			{
				"101:2,102:2,104:2,103:1,105:2,106:2,112:2,110:2,114:2,113:1,118:1,120:1",
				"121:2,122:2,124:2,123:1,125:2,126:2,131:2,132:2,133:1,134:2,138:1,140:1",
				"101:2,102:2,104:2,105:2,106:2,121:2,122:2,124:2,125:2,126:2"
			},
			// 챔피언스 (23): 메이저's builds and three more points, so the 90% cut (20) leaves 메이저's full builds.
			new[]
			{
				"101:2,102:2,104:2,103:1,105:2,106:2,112:2,110:2,114:2,113:1,118:1,120:1,107:2,111:1",
				"121:2,122:2,124:2,123:1,125:2,126:2,131:2,132:2,133:1,134:2,138:1,140:1,127:2,130:1",
				"101:2,102:2,104:2,105:2,106:2,121:2,122:2,124:2,125:2,126:2,103:2,123:1"
			}
		};

		// (After AiTraitBuilds5: static fields are set in the order written.)
		private static readonly string[][] AiTraitBuilds6 = { AiTraitBuilds5[0], AiTraitBuilds5[1], AiTraitBuilds5[2], AiTraitBuilds5[3], AiTraitBuilds5[4], AiTraitBuilds5[4] };
		// Seven tiers (cut to 90% of 12 / 15 / 18 / 20 / 22 / 24 / 26 = 10 / 13 / 16 / 18 / 19 / 21 / 23): 더블A the old 마이너 row (17),
		// 트리플A the old 메이저 row (20), 메이저 up the old 챔피언스 row (23).
		private static readonly string[][] AiTraitBuilds7 = { AiTraitBuilds5[0], AiTraitBuilds5[1], AiTraitBuilds5[2], AiTraitBuilds5[3], AiTraitBuilds5[4], AiTraitBuilds5[4], AiTraitBuilds5[4] };

		// The traits of the k-th AI club of a league (k = 0..8, the user's club skipped).
		public static Dictionary<byte, byte> AiTraits(int tier, int k)
		{
			string[] builds = AiTraitBuilds[Math.Clamp(tier, 0, Tiers - 1)];
			return builds[k % builds.Length].Split(',').Select(kv => kv.Split(':'))
				.ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1]));
		}

		public static int TierOfGrade(byte grade) => Math.Max(0, Array.IndexOf(TierGrade, grade));

		public static int Reward(int tier, int runsFor, int runsAgainst) =>
			(int)Math.Round((runsFor > runsAgainst ? WinMp : runsFor == runsAgainst ? TieMp : LossMp) * RewardMult[tier]);

		private static readonly string[] Sponsors = { "마구매니저", "홈런볼", "MP주머니", "드림", "레전드", "퓨처스" };

		public static string Sponsor(int season) => Sponsors[(season - 1) % Sponsors.Length];

		// Game slots: every 20 minutes on the clock (:00, :20, :40).
		public static DateTime NextSlot(DateTime t)
		{
			DateTime floor = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute / SlotMinutes * SlotMinutes, 0, t.Kind);
			return floor < t ? floor.AddMinutes(SlotMinutes) : floor;
		}

		public static DateTime SlotTime(DateTime seasonStart, int game) => seasonStart.AddMinutes((game - 1) * SlotMinutes);

		// The schedule: 48 rows of five pairings (away, home), each row a three-game series.
		// Round robin by the circle method; every third cycle swaps home and away.
		public static readonly (int away, int home)[][] Rows = BuildRows(Games / Series);

		// From the 2026-11-01 cycle (user, 2026-10-31: "팀당 16경기를 했을텐데 상대전적이 10승 0무 5패 15게임밖에"): 16 games
		// against every club, as the KBO plays them. The old 48 three-game rows went round five times and three rows more, so
		// a club met three opponents 18 times and the rest 15. Now the round robin goes round six times, four as three-game
		// series and the last two (games 109-144) as two-game series: 4 × 3 + 2 × 2 = 16 against each, 8 at home. A league
		// that began before keeps the old rows (Pairs by its start).
		public static readonly DateTime SixteenFrom = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Local);

		public static readonly (int away, int home)[][] Rows16 = BuildRows(54);

		private const int ThreeGameRows16 = 36;

		public static bool Sixteen(DateTime seasonStart) => seasonStart >= SixteenFrom;

		public static int RowGames16(int row) => row < ThreeGameRows16 ? 3 : 2;

		private static int Row16(int game) => game <= ThreeGameRows16 * 3 ? (game - 1) / 3 : ThreeGameRows16 + (game - 1 - ThreeGameRows16 * 3) / 2;

		// A game's five pairings in a league that began at seasonStart.
		public static (int away, int home)[] Pairs(int game, DateTime seasonStart) => Sixteen(seasonStart) ? Rows16[Row16(game)] : Pairs(game);

		private static (int away, int home)[][] BuildRows(int count)
		{
			int rounds = Clubs - 1;
			List<(int, int)[]> rows = new List<(int, int)[]>();
			for (int r = 0; rows.Count < count; r++)
			{
				int round = r % rounds, cycle = r / rounds;
				int[] ring = new int[Clubs];
				ring[0] = 0;
				for (int i = 1; i < Clubs; i++) ring[i] = 1 + (i - 1 + round) % rounds;
				(int, int)[] pairs = new (int, int)[Clubs / 2];
				for (int i = 0; i < Clubs / 2; i++)
				{
					int a = ring[i], b = ring[Clubs - 1 - i];
					bool swap = (round + i + cycle) % 2 == 1;
					pairs[i] = swap ? (b, a) : (a, b);
				}
				rows.Add(pairs);
			}
			return rows.ToArray();
		}

		public static (int away, int home)[] Pairs(int game) => Rows[(game - 1) / Series];

		// ManagementLeagueSchedule.txt for the client (TableReader.LeagueSchedule): a header, then
		// "row\tAH\tAH\tAH\tAH\tAH" with each pairing as two digits (away, home).
		public static string ScheduleTable() => ScheduleTable(DateTime.Now);

		// From SixteenFrom the rows of the 16-game schedule, each with its games after the pairings (a client from 1.0.22
		// reads them; older ones take every row as three games, right up to game 110). The CDN builds it for the time asked.
		public static string ScheduleTable(DateTime now)
		{
			bool sixteen = now >= SixteenFrom;
			(int away, int home)[][] rows = sixteen ? Rows16 : Rows;
			StringBuilder sb = new StringBuilder("idx\tmatch1\tmatch2\tmatch3\tmatch4\tmatch5" + (sixteen ? "\tgames" : string.Empty) + "\n");
			for (int r = 0; r < rows.Length; r++)
			{
				sb.Append(r + 1);
				foreach ((int away, int home) in rows[r]) sb.Append('\t').Append(away * 10 + home);
				if (sixteen) sb.Append('\t').Append(RowGames16(r));
				sb.Append('\n');
			}
			return sb.ToString();
		}
	}
}
