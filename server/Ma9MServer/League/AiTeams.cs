using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// One fixed league opponent (see AiTeams).
	public sealed class AiTeam
	{
		public int Tier, Slot;
		public string Name = string.Empty;
		public byte Emblem;
		public string Lineup = string.Empty;   // Lineup.Serialize
		public int Ball;
		public Dictionary<byte, byte> Traits = new Dictionary<byte, byte>();
		public Dictionary<int, TeamCard> Development = new Dictionary<int, TeamCard>();   // card idx → 재계약/트레이닝 bonus
	}

	// 리그 고정 AI 구단 (user, 2026-10-19). Each tier has nine fixed opponents, made once and kept across
	// seasons; every user league of that tier meets the same nine (the user's club is the tenth).
	// - Rosters: 25 real cards inside the tier's AI ball range (LeagueRules.AiBalls), no player on two
	//   clubs of the same tier.
	// - 팀 특성: 90% of the tier's points (9 / 12 / 15 / 18), in turn batting all-in, pitching all-in and
	//   split: LeagueRules.AiTraitBuilds cut from their deepest traits.
	// - 메이저 clubs are developed like a user's club: every card re-signed 4–6 times (재계약, +3 on every
	//   stat each) and three training slots on its main stats (books II/III). Lower tiers keep the flat AI
	//   development (LeagueRules.AiBonus).
	// The user's later goal is to replace these clubs one by one with their own single-year teams
	// (source_user, unused for now); league clubs follow their fixed team at the next league tick.
	// 챔피언스 (user, 2026-10-28: "팀덱을 갖춘 ai팀") has no fixed clubs: its free places go to team-year decks drawn each
	// season (Decks, tier 4 slots 0-29 of the same table): the 30 strongest single team-year decks
	// (TeamColorSim.TeamYearDecks), named "2018 두산" with their club's emblem, 단일 년도 팀 S on every card and one
	// 특별수강 each (the coach that suits the card best, +4/+5 and +2 +2), but no 재계약 and no 트레이닝 (user: "트레이닝이랑
	// 재계약 육성만 빠진 상태 … 100% 육성이 아닌 50%정도만"), and 90% of the tier's trait points like every AI club. From the
	// 11-03 12:00 season one or two of each deck's starters sit out for 후보 (ChampionsDecks, WithBackups), and the clubs and
	// decks are made again at a season's 편성 when the cards changed under them (RenewForSeason).
	// 메이저 (user, 2026-10-29: "메이저리그도 이제 단순 볼 수 박치기 팀이 아니라 팀덱으로 갖춰서 ai팀이 있었으면 함 … 팀덱 A 정도로 …
	// 볼 수 높은 팀보다는 약간 강하면서, 챔피언스리그의 AI보다는 확실히 약한 팀"): from the season after, its leagues draw nine
	// decks a season too (tier 3 slots DeckSlot0…, after the fixed clubs, which stay for the leagues made before). Which
	// decks and how developed: MajorDecks (set by --aitest, AiStrengthTest).
	public static class AiTeams
	{
		public const int PerTier = LeagueRules.Clubs - 1;
		public const double TraitShare = 0.9;
		public const int DeckPool = 30;
		private const int Renewal = 3;   // stat points per 재계약

		// 메이저's decks sit after its fixed clubs in league_ai_team (slot DeckSlot0 + key); 챔피언스's are slots 0-29.
		private const int DeckSlot0 = 100;

		// A tier's deck pool: the team-year decks ranked Skip+1… by value, 단일 년도 팀 at Grade (0 S, 1 A) on every card, one
		// 특별수강 each when Lecture, and with Backups MinBackups to that many of the nine starters given up for 후보 (WithBackups).
		// Six tiers (user, 2026-11-08) add per-card development: LectureMain fixes the 특별수강's main stat (0 = rolled +4/+5 as
		// before; the two sub stats +2 each either way), TrainSlots stats get +TrainEach 트레이닝 and Renewals stats +3 재계약 (the
		// card's most valuable ones, Valuable), Position changes a pitcher standing in another role's slot to it when his card
		// may (1 대성공, 2 성공), and Coach gives the coaching-staff set of the deck's own club by its grade (1 노말, 2 스페셜:
		// CoachCatalog.SetBonus on every stat; a 타격·투수코치's own stat adds nothing below 레어).
		// BelowNext (더블A, 2026-11-14): the decks of the tier above's pool are left out first, so the pool is the next ones down.
		public sealed record DeckRule(int Skip, int Grade, bool Lecture, int Backups = 0, int MinBackups = 1, int LectureMain = 0,
			int TrainSlots = 0, int TrainEach = 0, int Renewals = 0, int Position = 0, int Coach = 0, bool BelowNext = false);

		// 챔피언스 (user, 2026-11-02: "팀들마다 주전 선수 1~2명을 후보로 교체한 덱 … 아직 단일덱이 완성 되지 않은 팀들에게는 빡센
		// 난이도"): one or two starters of every deck out.
		public static readonly DeckRule ChampionsDecks = new DeckRule(0, 0, true, 2);

		// 메이저: the same 30 team-years as 챔피언스 at A with the 특별수강. --aitest 12 seasons (2026-10-29), win% against 메이저's
		// fixed clubs / 챔피언스's decks: A 50.4 / 23.4, A+특강 54.6 / 26.5, S 71.9 / 43.3; ranks 31-60 A+특강 46.7 / 22.5, S+특강
		// 69.9 / 41.9 (the fixed clubs against 챔피언스 24.2). A+특강 is "slightly stronger" and "clearly weaker".
		public static DeckRule MajorDecks = new DeckRule(0, 1, true);

		// Six tiers (user, 2026-11-08: "챔피언스리그 : 전원 주전, 필요할 경우 포지션 변경도 완전 성공인 상태, 특강5, 트레이닝 15, 재계약 9,
		// 스페셜 코치진 세트덱 / 레전드리그 : 전원 주전, … 포지션 변경 일반 성공 … 특강5, 트레이닝 12, 재계약 6, 노말 코치진 세트덱 /
		// 메이저리그 : 전원 주전, … 일반 성공 … 특강4, 트레이닝 8, 재계약 3 / 트리플A리그 : 2~3명 후보, 특강4, 트레이닝 4, 재계약 3";
		// 팀컬러 "챔피언스 S, 레전드 S, 메이저 A, 트리플A A"; read as asked: 트레이닝 15 = +5 on three stats, 12 = +4 on three, 8 = +4
		// on two, 4 = +4 on one; 재계약 9 = three +3s). 트리플A, 메이저, 레전드, 챔피언스 by tier 2-5.
		private static readonly DeckRule[] Rules6 =
		{
			new DeckRule(0, 1, true, Backups: 3, MinBackups: 2, LectureMain: 4, TrainSlots: 1, TrainEach: 4, Renewals: 1),
			new DeckRule(0, 1, true, LectureMain: 4, TrainSlots: 2, TrainEach: 4, Renewals: 1, Position: 2),
			new DeckRule(0, 0, true, LectureMain: 5, TrainSlots: 3, TrainEach: 4, Renewals: 2, Position: 2, Coach: 1),
			new DeckRule(0, 0, true, LectureMain: 5, TrainSlots: 3, TrainEach: 5, Renewals: 3, Position: 1, Coach: 2),
		};

		// Seven tiers (user, 2026-11-14 20:5x: "더블 A는 팀-연도 덱에 트리플A에서 사용되는 덱보다 순위가 낮은 조금 약한 덱들이 나오고,
		// 팀컬러는 B, 특강은 없음, 트레이닝은 1과목 +4, 재계약 3, 코치 없음, 주전 2~3명 후보로"; asked: "트리플A 30덱을 빼고 다음 30덱"):
		// 더블A at tier 2, then the six-tier rules for 트리플A … 챔피언스 at 3-6. 재계약 3 = one stat +3, as 트리플A's (11-08 reading).
		public static readonly DeckRule DoubleADecks = new DeckRule(0, 2, false, Backups: 3, MinBackups: 2, TrainSlots: 1, TrainEach: 4, Renewals: 1,
			BelowNext: true);

		private static readonly DeckRule[] Rules7 = { DoubleADecks, Rules6[0], Rules6[1], Rules6[2], Rules6[3] };

		public static DeckRule RuleOf(int tier) => LeagueRules.Seven ? Rules7[Math.Clamp(tier - 2, 0, Rules7.Length - 1)]
			: LeagueRules.NewStructure ? Rules6[Math.Clamp(tier - 2, 0, Rules6.Length - 1)]
			: tier == LeagueRules.ChampionsTier ? ChampionsDecks : MajorDecks;

		public static bool HasDecks(int tier) => LeagueRules.NewStructure ? tier >= 2 : tier == LeagueRules.MajorTier || tier == LeagueRules.ChampionsTier;

		// Tiers with nine fixed clubs (all but 챔피언스 before; 비기너 and 루키 with six tiers).
		public static bool HasClubs(int tier) => LeagueRules.NewStructure ? tier <= 1 : tier != LeagueRules.ChampionsTier;

		private static int TopClubTier => LeagueRules.NewStructure ? 1 : LeagueRules.MajorTier;

		private static int DeckSlot(int tier, int key) => tier == LeagueRules.ChampionsTier ? key : DeckSlot0 + key;

		private static bool IsDeck(AiTeam t) => HasDecks(t.Tier) && t.Slot >= DeckSlot(t.Tier, 0);

		// A deck's key in its tier's pool (LeagueClub.AiKey).
		public static int KeyOf(AiTeam deck) => deck.Slot - DeckSlot(deck.Tier, 0);

		private static readonly string[][] Names =
		{
			new[] { "퓨처스 연합", "나눔 올스타", "새싹 야구단", "더그아웃 형제", "번트 장인", "무쇠 팔", "외야 사냥꾼", "마무리 명가", "스위치 히터스" },
			new[] { "루키 연합", "번개 발야구", "근성 야구", "신바람 야구", "도루 군단", "필승조", "타격 기계", "슬러거 연합", "언더핸드 클럽" },
			new[] { "베테랑 연합", "불펜 명가", "한방 타선", "수비 장인", "강철 어깨", "외인 군단", "클러치 히터스", "파이어볼러즈", "그랜드슬램" },
			new[] { "레전드 올스타", "드림 올스타", "왕조의 후예", "황금 세대", "에이스 클럽", "홈런 군단", "철벽 마운드", "역전의 명수", "다이아몬드 클럽" }
		};

		private static string s_cs;
		private static CardCatalog s_catalog;
		private static readonly Dictionary<(int tier, int slot), AiTeam> s_teams = new Dictionary<(int, int), AiTeam>();

		public static int TraitPoints(int tier) => (int)Math.Floor(LeagueRules.TraitPoints[Math.Clamp(tier, 0, LeagueRules.Tiers - 1)] * TraitShare);

		// data: needed to build 챔피언스's decks the first time (LeagueService.Init); without it the stored decks are used.
		public static void Init(string dbPath, CardCatalog catalog, GameData data = null)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			s_catalog = catalog;
			s_teams.Clear();
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS league_ai_team (
					tier INTEGER NOT NULL,
					slot INTEGER NOT NULL,
					name TEXT NOT NULL,
					emblem INTEGER NOT NULL,
					lineup TEXT NOT NULL,
					ball INTEGER NOT NULL,
					traits TEXT NOT NULL,
					development TEXT NOT NULL,
					source_user INTEGER,
					created TEXT NOT NULL,
					PRIMARY KEY (tier, slot))";
				cmd.ExecuteNonQuery();
				// The DeckRule a tier's deck pool was built with (RenewForSeason makes it again when the rule changes).
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_ai_rule (tier INTEGER PRIMARY KEY, rule TEXT NOT NULL)";
				cmd.ExecuteNonQuery();
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT tier, slot, name, emblem, lineup, ball, traits, development FROM league_ai_team";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					AiTeam t = new AiTeam
					{
						Tier = (int)r.GetInt64(0), Slot = (int)r.GetInt64(1), Name = r.GetString(2), Emblem = (byte)r.GetInt64(3),
						Lineup = r.GetString(4), Ball = (int)r.GetInt64(5), Traits = ParseTraits(r.GetString(6)), Development = ParseDevelopment(r.GetString(7))
					};
					s_teams[(t.Tier, t.Slot)] = t;
				}
			}
			// The deck ranking now, at start: a 편성 that makes the decks again then runs in milliseconds on the game loop.
			if (data != null) Ranked(data);
			for (int tier = 0; tier < LeagueRules.Tiers; tier++)
			{
				if (HasClubs(tier))
				{
					// Ball range changed (LeagueRules.AiBalls): the whole tier is drawn again (no shared players).
					(int lo0, int hi0) = LeagueRules.AiBalls[tier];
					List<AiTeam> stale = Clubs(tier);
					if (stale.Any(t => t.Ball < lo0 || t.Ball > hi0))
					{
						DropClubs(c, tier, $"were {string.Join("/", stale.Select(t => t.Ball))}");
					}
					FillClubs(c, tier);
				}
				if (HasDecks(tier))
				{
					InitDecks(c, data, tier);
				}
			}
			// Something stale (the 11-02 card prices): the next season's clubs and decks are built now, off the game loop (Fresh).
			if (data != null)
			{
				List<int> clubTiers = Enumerable.Range(0, LeagueRules.Tiers).Where(t => HasClubs(t) && ClubsStale(t)).ToList();
				List<int> deckTiers = Enumerable.Range(0, LeagueRules.Tiers).Where(t => HasDecks(t) && DecksStale(c, t)).ToList();
				s_fresh = clubTiers.Count + deckTiers.Count == 0 ? null : System.Threading.Tasks.Task.Run(() => BuildFresh(data, clubTiers, deckTiers));
			}
		}

		// A tier's fixed club; a tier without them (decks only) falls back to the highest tier that has them.
		public static AiTeam Get(int tier, int slot) => s_teams[(Math.Clamp(tier, 0, TopClubTier), Math.Clamp(slot, 0, PerTier - 1))];

		// The six-tier switch (LeagueStore.MigrateTiers6 has moved the stored tiers): the teams read again, and clubs or decks
		// built at server start for the five tiers dropped, so the 편성 right after builds the six tiers' (RenewForSeason).
		public static void Reload()
		{
			s_fresh = null;
			s_teams.Clear();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT tier, slot, name, emblem, lineup, ball, traits, development FROM league_ai_team";
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				AiTeam t = new AiTeam
				{
					Tier = (int)r.GetInt64(0), Slot = (int)r.GetInt64(1), Name = r.GetString(2), Emblem = (byte)r.GetInt64(3),
					Lineup = r.GetString(4), Ball = (int)r.GetInt64(5), Traits = ParseTraits(r.GetString(6)), Development = ParseDevelopment(r.GetString(7))
				};
				s_teams[(t.Tier, t.Slot)] = t;
			}
		}

		public static IEnumerable<AiTeam> OfTier(int tier) => Enumerable.Range(0, PerTier).Select(k => Get(tier, k));

		// A tier's team-year decks (메이저, 챔피언스) by key.
		public static AiTeam Deck(int tier, int key) => s_teams.TryGetValue((tier, DeckSlot(tier, key)), out AiTeam t) ? t : null;

		public static List<AiTeam> Decks(int tier) => s_teams.Values.Where(t => t.Tier == tier && IsDeck(t)).OrderBy(t => t.Slot).ToList();

		// The team-year a deck or a team's 1군 is built from: the most common (year, club) of its cards (FA cards, club 250, left out
		// since 2026-11-15: they belong to no team-year).
		public static (int year, int team) TeamYearOf(IEnumerable<int> cards) => cards.Select(s_catalog.Get).Where(x => x != null && !x.IsFA)
			.GroupBy(x => (x.Year, x.TeamCode)).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

		public static (int year, int team) TeamYearOf(AiTeam deck) => TeamYearOf(Cards(deck).Select(x => x.Idx));

		// Every team-year a team's 1군 leans on (at least min of its cards; a '12/'15 삼성 mix has two).
		public static HashSet<(int year, int team)> TeamYearsOf(IEnumerable<int> cards, int min = 5) => cards.Select(s_catalog.Get).Where(x => x != null && !x.IsFA)
			.GroupBy(x => (x.Year, x.TeamCode)).Where(g => g.Count() >= min).Select(g => g.Key).ToHashSet();

		// Builds a tier's deck pool once (kept in league_ai_team): RuleOf(tier)'s ranks of the team-year decks.
		private static void InitDecks(SqliteConnection c, GameData data, int tier)
		{
			int have = Decks(tier).Count;
			if (have >= DeckPool) return;
			if (data == null)
			{
				if (have == 0) ServerLog.Warn($"league AI clubs: no {LeagueRules.TierNames[tier]} decks yet (made at the next server start)");
				return;
			}
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			List<AiTeam> pool = MakePool(data, tier, RuleOf(tier));
			StorePool(c, tier, pool, $"made in {sw.ElapsedMilliseconds} ms");
		}

		// A tier's deck pool into league_ai_team in place of the old one, with the DeckRule it was built by (league_ai_rule).
		private static void StorePool(SqliteConnection c, int tier, List<AiTeam> pool, string how)
		{
			using (SqliteCommand del = c.CreateCommand())
			{
				del.CommandText = "DELETE FROM league_ai_team WHERE tier = $t AND slot >= $s";
				del.Parameters.AddWithValue("$t", tier);
				del.Parameters.AddWithValue("$s", DeckSlot(tier, 0));
				del.ExecuteNonQuery();
			}
			foreach (AiTeam stale in Decks(tier)) s_teams.Remove((stale.Tier, stale.Slot));
			foreach (AiTeam t in pool)
			{
				Save(c, t);
				s_teams[(t.Tier, t.Slot)] = t;
			}
			ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} decks {how} ({RuleOf(tier)}): {string.Join(", ", pool.Select(t => $"{t.Name} {t.Ball}볼"))}");
			using SqliteCommand rule = c.CreateCommand();
			rule.CommandText = "INSERT OR REPLACE INTO league_ai_rule (tier, rule) VALUES ($t, $r)";
			rule.Parameters.AddWithValue("$t", tier);
			rule.Parameters.AddWithValue("$r", RuleOf(tier).ToString());
			rule.ExecuteNonQuery();
		}

		private static List<AiTeam> Clubs(int tier) => s_teams.Values.Where(t => t.Tier == tier && !IsDeck(t)).ToList();

		// A tier's fixed clubs out, to be drawn again (FillClubs).
		private static void DropClubs(SqliteConnection c, int tier, string why)
		{
			(int lo, int hi) = LeagueRules.AiBalls[tier];
			using SqliteCommand del = c.CreateCommand();
			del.CommandText = "DELETE FROM league_ai_team WHERE tier = $t AND slot < $s";
			del.Parameters.AddWithValue("$t", tier);
			del.Parameters.AddWithValue("$s", PerTier);
			del.ExecuteNonQuery();
			foreach (AiTeam t in Clubs(tier)) s_teams.Remove((t.Tier, t.Slot));
			ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} drawn again for {lo}~{hi}볼 ({why})");
		}

		// The tier's missing fixed clubs, no player on two of them.
		private static void FillClubs(SqliteConnection c, int tier)
		{
			HashSet<int> taken = new HashSet<int>(Clubs(tier).SelectMany(t => Cards(t)).Select(x => x.Sidx));
			List<AiTeam> made = new List<AiTeam>();
			for (int slot = 0; slot < PerTier; slot++)
			{
				if (s_teams.ContainsKey((tier, slot))) continue;
				made.Add(Generate(tier, slot, taken));
			}
			StoreClubs(c, tier, made);
		}

		// Clubs drawn for a tier's empty places into league_ai_team.
		private static void StoreClubs(SqliteConnection c, int tier, List<AiTeam> made)
		{
			foreach (AiTeam t in made)
			{
				Save(c, t);
				s_teams[(t.Tier, t.Slot)] = t;
			}
			if (made.Count > 0)
			{
				ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} made {made.Count} ({string.Join(", ", made.Select(t => $"{t.Name} {t.Ball}볼 특성{t.Traits.Values.Sum(v => v)}"))})"
					+ (made.Any(t => t.Development.Count > 0) ? $", 육성 평균 +{made.Average(MeanDevelopment):F1}" : ""));
			}
		}

		// A tier's fixed clubs by their cards' balls; stale when one falls outside LeagueRules.AiBalls.
		private static List<int> ClubBalls(int tier) => Clubs(tier).Select(t => Cards(t).Sum(x => x.Ball)).ToList();

		private static bool ClubsStale(int tier)
		{
			(int lo, int hi) = LeagueRules.AiBalls[tier];
			return ClubBalls(tier).Any(b => b < lo || b > hi);
		}

		private static string StoredRule(SqliteConnection c, int tier)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT rule FROM league_ai_rule WHERE tier = $t";
			cmd.Parameters.AddWithValue("$t", tier);
			return cmd.ExecuteScalar() as string;
		}

		// A deck pool is stale when a deck's cards add up to other balls than stored or its rule is not the one it was built by.
		private static bool DecksStale(SqliteConnection c, int tier) =>
			Decks(tier).Any(d => Cards(d).Sum(x => x.Ball) != d.Ball) || StoredRule(c, tier) != RuleOf(tier).ToString();

		// The next season's clubs and decks, built on a worker thread when Init finds something stale, so that the 편성
		// (RenewForSeason) only puts them in: built on the game loop they held every client for seconds (6.5 s on a Release test
		// copy, about three times that on the Debug live server). The same cards and seeds as building them there, so the same
		// teams. The worker touches neither s_teams nor the database; the catalog and the ranking are read-only by then.
		private sealed class Fresh
		{
			public readonly Dictionary<int, List<AiTeam>> Clubs = new Dictionary<int, List<AiTeam>>();
			public readonly Dictionary<int, List<AiTeam>> Decks = new Dictionary<int, List<AiTeam>>();
		}

		private static System.Threading.Tasks.Task<Fresh> s_fresh;

		private static Fresh BuildFresh(GameData data, List<int> clubTiers, List<int> deckTiers)
		{
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			Fresh f = new Fresh();
			foreach (int tier in clubTiers)
			{
				HashSet<int> taken = new HashSet<int>();
				f.Clubs[tier] = Enumerable.Range(0, PerTier).Select(slot => Generate(tier, slot, taken)).ToList();
			}
			foreach (int tier in deckTiers)
			{
				f.Decks[tier] = MakePool(data, tier, RuleOf(tier));
			}
			ServerLog.Info($"league AI clubs: the next season's clubs ({string.Join(", ", clubTiers.Select(t => LeagueRules.TierNames[t]))}) and decks "
				+ $"({string.Join(", ", deckTiers.Select(t => LeagueRules.TierNames[t]))}) built in {sw.ElapsedMilliseconds} ms, in at the 편성");
			return f;
		}

		// The worker's teams (waiting for it if it is still busy), or null to build them on the game loop.
		private static Fresh TakeFresh()
		{
			System.Threading.Tasks.Task<Fresh> task = s_fresh;
			s_fresh = null;
			if (task == null) return null;
			try
			{
				return task.Result;
			}
			catch (Exception e)
			{
				ServerLog.Warn($"league AI clubs: building the next season's teams at start failed ({e.GetBaseException().Message}); made now on the game loop");
				return null;
			}
		}

		// 다음 시즌 AI (user, 2026-11-02: "다음시즌부터 새 볼값으로 짜줘"): the 11-02 card set priced the cards anew, so the fixed
		// clubs drawn for the old balls add up to 124–153 by their cards and the decks were built for the old balls. When a season's
		// 편성 is due and its leagues have closed (LeagueService.RenewAi), what is stale is made again from the current cards before
		// the next season's first league: a tier's fixed clubs when one falls outside LeagueRules.AiBalls by its cards' balls (when
		// they all fit, only the stored totals are put right), a deck pool when a deck's cards add up to other balls than stored or
		// its DeckRule is not the one it was built with (league_ai_rule). Leagues in play are left alone: their games read these
		// teams (LeagueService.AiTeamOf). Once per season start in this process; a second call finds nothing stale.
		private static readonly HashSet<DateTime> s_renewed = new HashSet<DateTime>();

		public static bool RenewedFor(DateTime seasonStart) => s_renewed.Contains(seasonStart);

		public static void RenewForSeason(GameData data, DateTime seasonStart)
		{
			if (!s_renewed.Add(seasonStart)) return;
			using SqliteConnection c = Open();
			// Every row below in one transaction (a 편성 that makes everything again writes about a hundred).
			Sql(c, "BEGIN");
			try
			{
				Renew(c, data, seasonStart);
				Sql(c, "COMMIT");
			}
			catch
			{
				Sql(c, "ROLLBACK");
				throw;
			}
		}

		// Tests on a data copy (MA9M_TIERS6): what a 편성 would make again, now, without marking a season renewed.
		public static void RenewNow(GameData data)
		{
			using SqliteConnection c = Open();
			Sql(c, "BEGIN");
			try
			{
				Renew(c, data, DateTime.Now);
				Sql(c, "COMMIT");
			}
			catch
			{
				Sql(c, "ROLLBACK");
				throw;
			}
		}

		private static void Sql(SqliteConnection c, string text)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = text;
			cmd.ExecuteNonQuery();
		}

		private static void Renew(SqliteConnection c, GameData data, DateTime seasonStart)
		{
			Fresh f = TakeFresh();
			for (int tier = 0; tier < LeagueRules.Tiers; tier++)
			{
				if (HasClubs(tier))
				{
					List<AiTeam> clubs = Clubs(tier);
					List<int> balls = ClubBalls(tier);
					if (ClubsStale(tier))
					{
						DropClubs(c, tier, $"for the season from {seasonStart:MM-dd HH:mm}: their cards add up to {string.Join("/", balls)}볼 now");
						if (f != null && f.Clubs.TryGetValue(tier, out List<AiTeam> made)) StoreClubs(c, tier, made);
						else FillClubs(c, tier);
					}
					else
					{
						for (int i = 0; i < clubs.Count; i++)
						{
							// The tier's trait points changed (six tiers, 2026-11-08: 비기너 12, 루키 15): the build cut again.
							bool traits = clubs[i].Traits.Values.Sum(v => v) != TraitPoints(tier);
							if (clubs[i].Ball == balls[i] && !traits) continue;
							clubs[i].Ball = balls[i];
							if (traits) clubs[i].Traits = TraitBuild(tier, clubs[i].Slot);
							Save(c, clubs[i]);
						}
					}
				}
				if (!HasDecks(tier)) continue;
				string rule = RuleOf(tier).ToString();
				string was = StoredRule(c, tier);
				List<AiTeam> decks = Decks(tier);
				int off = decks.Count(d => Cards(d).Sum(x => x.Ball) != d.Ball);
				if (off == 0 && was == rule) continue;
				ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} decks made again for the season from {seasonStart:MM-dd HH:mm} "
					+ $"({off} of {decks.Count} off their cards' balls; rule {was ?? "not recorded"} → {rule})");
				if (f != null && f.Decks.TryGetValue(tier, out List<AiTeam> pool))
				{
					StorePool(c, tier, pool, "built at server start");
				}
				else
				{
					foreach (AiTeam d in decks) s_teams.Remove((d.Tier, d.Slot));
					InitDecks(c, data, tier);
				}
			}
		}

		private static List<TeamColorSim.TeamYearDeck> s_ranked;

		// The team-year decks by value. Built once a process: the card tables only change with a restart, so Init builds it at
		// server start, before the game loop runs, and a season's 편성 (RenewForSeason) only picks from it.
		private static List<TeamColorSim.TeamYearDeck> Ranked(GameData data) =>
			s_ranked ??= TeamColorSim.TeamYearDecks(data, LeagueRules.BallCap[LeagueRules.ChampionsTier]).OrderByDescending(d => d.Value).ToList();

		// The team-year decks of a tier's pool: within its ball cap by value, rule.Skip left out, the next DeckPool; with
		// rule.BelowNext the tier above's pool left out before that (더블A under 트리플A, 2026-11-14).
		public static List<TeamColorSim.TeamYearDeck> PoolDecks(GameData data, int tier, DeckRule rule)
		{
			IEnumerable<TeamColorSim.TeamYearDeck> fit = Ranked(data).Where(d => d.Balls <= LeagueRules.BallCap[tier]);
			if (rule.BelowNext && tier + 1 < LeagueRules.Tiers && HasDecks(tier + 1))
			{
				HashSet<(int, int)> above = PoolDecks(data, tier + 1, RuleOf(tier + 1)).Select(d => (d.Year, d.TeamCode)).ToHashSet();
				fit = fit.Where(d => !above.Contains((d.Year, d.TeamCode)));
			}
			return fit.Skip(rule.Skip).Take(DeckPool).ToList();
		}

		// A deck pool as AI clubs (not saved): the team-year decks ranked by value (built once), rule.Skip of them left
		// out, the next DeckPool with 단일 년도 팀 at rule.Grade on every card and, with rule.Lecture, one 특별수강 each.
		public static List<AiTeam> MakePool(GameData data, int tier, DeckRule rule)
		{
			List<TeamColorSim.TeamYearDeck> decks = PoolDecks(data, tier, rule);
			(int bat, int pit) colour = TeamColors.SingleYearTeam(rule.Grade);
			List<AiTeam> pool = new List<AiTeam>();
			List<string> swapped = new List<string>(), changed = new List<string>();
			for (int k = 0; k < decks.Count; k++)
			{
				TeamColorSim.TeamYearDeck d = decks[k];
				Random rnd = new Random(20261028 + 100 * d.Year + d.TeamCode);
				List<CardInfo> cards = rule.Backups > 0 ? WithBackups(d, rule.Backups, swapped, rule.MinBackups) : d.Cards;
				Lineup arranged = LineupBuilder.Build(cards.Select(x => x.Idx), s_catalog);
				AiTeam t = new AiTeam
				{
					Tier = tier, Slot = DeckSlot(tier, k), Name = $"{d.Year} {TeamColors.TeamName(d.TeamCode, d.Year)}", Emblem = (byte)d.TeamCode,
					Lineup = arranged.Serialize(), Ball = cards.Sum(x => x.Ball),
					Traits = TraitBuild(tier, k)
				};
				int setBonus = rule.Coach > 0 ? CoachCatalog.SetBonus[Math.Clamp(rule.Coach, 0, CoachCatalog.SetBonus.Length - 1)] : 0;
				foreach (CardInfo card in cards)
				{
					TeamCard dev = new TeamCard { Idx = card.Idx };
					sbyte tc = (sbyte)(card.IsBatter ? colour.bat : colour.pit);
					for (int j = 0; j < 6; j++) dev.TeamColor[j] = tc;
					if (rule.Lecture) dev.Educated = rule.LectureMain > 0 ? Lecture(card, rule.LectureMain) : Lecture(card, rnd);
					foreach (int j in Valuable(card, Trainable(card)).Take(rule.TrainSlots)) dev.Training[j] = (sbyte)rule.TrainEach;
					foreach (int j in Valuable(card, Enumerable.Range(0, 6)).Take(rule.Renewals)) dev.Contract[j] = (sbyte)Renewal;
					for (int j = 0; j < 6; j++) dev.Coach[j] = (sbyte)setBonus;
					t.Development[card.Idx] = dev;
				}
				if (rule.Position > 0) ChangeRoles(t, arranged, rule.Position, changed);
				pool.Add(t);
			}
			if (swapped.Count > 0)
			{
				ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} decks' 후보 in for starters: {string.Join(", ", swapped)}");
			}
			if (changed.Count > 0)
			{
				ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} decks' 포지션 변경: {string.Join(", ", changed)}");
			}
			return pool;
		}

		// Pitching slots by role (Lineup.Pitchers: SP1-5, RP1-4, SU, CP) as ePos 12-15.
		private static int SlotRole(int slot) => slot < 5 ? (int)CardPos.SP : slot < 9 ? (int)CardPos.SP + 1 : slot == 9 ? (int)CardPos.SP + 2 : (int)CardPos.SP + 3;

		// 포지션 변경 where needed: a pitcher LineupBuilder put in another role's slot takes that role when his card may change to
		// it (PositionChangeHandlers.AllowedRoles), as a user's card would (result 1 대성공, 2 성공); the rest play out of role.
		private static void ChangeRoles(AiTeam t, Lineup arranged, int result, List<string> log)
		{
			List<string> mine = new List<string>();
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				int idx = arranged.Pitchers[s];
				CardInfo card = idx == 0 ? null : s_catalog.Get(idx);
				if (card == null || card.IsBatter || !t.Development.TryGetValue(idx, out TeamCard dev)) continue;
				int role = SlotRole(s);
				if ((int)card.MainPosition == role || !PositionChangeHandlers.AllowedRoles(idx).Contains((byte)role)) continue;
				dev.PositionTrainingInfo = (byte)((role - 11) * 10 + result);
				mine.Add($"{card.Name} {RoleName(card.MainPosition)}→{RoleName((CardPos)role)}");
			}
			if (mine.Count > 0) log.Add($"{t.Name} {string.Join("·", mine)}");
		}

		private static string RoleName(CardPos p) => p == CardPos.SP ? "선발" : (int)p == (int)CardPos.SP + 1 ? "중계" : (int)p == (int)CardPos.SP + 2 ? "셋업" : "마무리";

		// Stats a card can train (TeamCard order): batters 컨택·파워·스피드·선구, pitchers 구속·컨트롤·테크닉·구위.
		private static IEnumerable<int> Trainable(CardInfo card) => card.IsBatter ? new[] { 0, 1, 2, 3 } : new[] { 1, 2, 3, 4 };

		// A card's stats from the most valuable on: the measured worth of +10 (11-05 rework: 구위 2.95, 테크닉 2.91, 구속 2.53,
		// 컨트롤 2.01; 선구 4.33, 주력 1.34, 컨택·파워 by the 특별수강 weights, a catcher's 수비 first), halved where the card is past the
		// 105 knee already (the development curve flattens there).
		private static readonly double[] BatWorth = { 3.6, 3.1, 1.34, 4.33, 0.4, 0.4 };
		private static readonly double[] PitWorth = { 0.2, 2.53, 2.01, 2.91, 2.95, 0.3 };

		private static IEnumerable<int> Valuable(CardInfo card, IEnumerable<int> stats)
		{
			double[] w = (double[])(card.IsBatter ? BatWorth : PitWorth).Clone();
			if (card.IsBatter && card.MainPosition == CardPos.C) w[4] = 5.0;
			return stats.OrderByDescending(j => w[j] * (StatOf(card, j) >= 105 ? 0.5 : 1.0)).ThenBy(j => j);
		}

		// A card's own value of a TeamCard-order stat: pitchers' six are in that order; a batter card has five (con, pow, spd, eye,
		// mental: CardInfo.Stats) and its 수비 by position elsewhere (0 here: never past the knee).
		private static readonly int[] BatterCardStat = { 0, 1, 2, 3, -1, 4 };

		private static int StatOf(CardInfo card, int j)
		{
			int k = card.IsBatter ? BatterCardStat[j] : j;
			return k >= 0 && k < card.Stats.Length ? card.Stats[k] : 0;
		}

		// One to `most` of the deck's nine starters (how many and who at random, seeded by team-year) leave for the team-year's
		// best other batter who can play that spot and costs fewer balls (or as many with a lower Overall), so the deck gets
		// weaker and stays under its cap; LineupBuilder then arranges the 25, so a bench player may take the spot instead. A
		// starter with no such 후보 stays. The swaps go into `swapped` for the log.
		private static List<CardInfo> WithBackups(TeamColorSim.TeamYearDeck d, int most, List<string> swapped, int fewest = 1)
		{
			Random rnd = new Random(20261102 + 100 * d.Year + d.TeamCode);
			List<CardInfo> cards = new List<CardInfo>(d.Cards);
			Lineup l = LineupBuilder.Build(cards.Select(x => x.Idx), s_catalog);
			List<(int spot, CardInfo card)> starters = Enumerable.Range(0, Lineup.FielderSlots)
				.Select(p => (spot: p, card: s_catalog.Get(l.Batters[l.Fielders[p] - 1]))).Where(x => x.card != null).ToList();
			int left = rnd.Next(Math.Min(fewest, most), most + 1);
			List<string> mine = new List<string>();
			foreach ((int spot, CardInfo starter) in starters.OrderBy(_ => rnd.Next()).ToList())
			{
				if (left == 0) break;
				HashSet<int> players = cards.Select(x => x.Sidx).ToHashSet();
				CardInfo backup = s_catalog.Drawable
					.Where(x => x.IsBatter && x.Year == d.Year && x.TeamCode == d.TeamCode && !players.Contains(x.Sidx)
						&& (spot == Lineup.FielderSlots - 1 || x.CanField(spot))
						&& (x.Ball < starter.Ball || x.Ball == starter.Ball && x.Overall < starter.Overall))
					.OrderByDescending(x => (int)x.MainPosition == spot + 1).ThenByDescending(x => x.Ball).ThenByDescending(x => x.Overall).ThenBy(x => x.Idx)
					.FirstOrDefault();
				if (backup == null) continue;
				cards[cards.FindIndex(x => x.Idx == starter.Idx)] = backup;
				mine.Add($"{starter.Name}{starter.Ball}→{backup.Name}{backup.Ball}");
				left--;
			}
			if (mine.Count > 0) swapped.Add($"{d.Year} {TeamColors.TeamName(d.TeamCode, d.Year)} {string.Join("·", mine)}");
			return cards;
		}

		// Stat weights for picking a deck card's 특별수강 coach (TeamCard order; the measured worth of +9 in each, roughly:
		// batters 컨택 > 파워 > 선구, pitchers 구위 ≈ 테크닉 > 컨트롤 ≈ 구속; a catcher's 수비 is worth the most).
		private static readonly double[] BatWeight = { 0.52, 0.45, 0.05, 0.40, 0.05, 0.05 };
		private static readonly double[] PitWeight = { 0.0, 0.15, 0.15, 0.25, 0.28, 0.03 };

		// One 특별수강 result: the eligible coach whose main and sub stats suit the card best, rolled as a lesson is.
		private static sbyte[] Lecture(CardInfo card, Random rnd)
		{
			double[] w = card.IsBatter ? (double[])BatWeight.Clone() : PitWeight;
			if (card.IsBatter && card.MainPosition == CardPos.C) w[4] = 1.2;
			LessonHandlers.Coach best = (card.IsBatter ? LessonHandlers.Batting : LessonHandlers.Pitching)
				.Where(co => LessonHandlers.Eligible(co, card))
				.OrderByDescending(co => 4.3 * w[co.Main] + 2 * (w[co.Sub1] + w[co.Sub2])).FirstOrDefault();
			return best == null ? new sbyte[6] : LessonHandlers.Roll(best, false, rnd);
		}

		// The same coach with the main stat fixed at `main` (six tiers: 특강4 / 특강5) and the sub stats +2 each.
		private static sbyte[] Lecture(CardInfo card, int main)
		{
			double[] w = card.IsBatter ? (double[])BatWeight.Clone() : PitWeight;
			if (card.IsBatter && card.MainPosition == CardPos.C) w[4] = 1.2;
			LessonHandlers.Coach best = (card.IsBatter ? LessonHandlers.Batting : LessonHandlers.Pitching)
				.Where(co => LessonHandlers.Eligible(co, card))
				.OrderByDescending(co => 4.3 * w[co.Main] + 2 * (w[co.Sub1] + w[co.Sub2])).FirstOrDefault();
			sbyte[] stat = new sbyte[6];
			if (best == null) return stat;
			stat[best.Main] = (sbyte)main;
			stat[best.Sub1] += 2;
			stat[best.Sub2] += 2;
			return stat;
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static IEnumerable<CardInfo> Cards(AiTeam t)
		{
			Lineup l = Lineup.Parse(t.Lineup);
			return l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(s_catalog.Get).Where(x => x != null);
		}

		private static AiTeam Generate(int tier, int slot, HashSet<int> taken)
		{
			Random rnd = new Random(20261019 + 100 * tier + slot);
			(int lo, int hi) = LeagueRules.AiBalls[tier];
			List<CardInfo> cards = StarterTeam.Draw(rnd, s_catalog, lo, hi, takenPlayers: taken);
			taken.UnionWith(cards.Select(x => x.Sidx));
			AiTeam t = new AiTeam
			{
				Tier = tier, Slot = slot, Name = Names[tier][slot], Emblem = (byte)rnd.Next(1, 11),
				Lineup = LineupBuilder.Build(cards.Select(x => x.Idx), s_catalog).Serialize(), Ball = cards.Sum(x => x.Ball),
				Traits = TraitBuild(tier, slot)
			};
			if (!LeagueRules.NewStructure && tier == LeagueRules.MajorTier)
			{
				foreach (CardInfo card in cards)
				{
					t.Development[card.Idx] = Develop(card, rnd);
				}
			}
			return t;
		}

		// 메이저 development of one card: 재계약 4–6 times, three training slots on its main stats.
		private static TeamCard Develop(CardInfo card, Random rnd)
		{
			TeamCard d = new TeamCard { Idx = card.Idx };
			sbyte contract = (sbyte)(Renewal * rnd.Next(4, 7));
			for (int k = 0; k < 6; k++) d.Contract[k] = contract;
			// Batters train 컨택·파워·스피드·선구, pitchers 구속·컨트롤·테크닉·구위 (TeamCard stat order),
			// the card's better stats first.
			int[] candidates = card.IsBatter ? new[] { 0, 1, 2, 3 } : new[] { 1, 2, 3, 4 };
			foreach (int k in candidates.OrderByDescending(k => card.Stats[k] + rnd.Next(0, 12)).Take(3))
			{
				bool third = rnd.NextDouble() < 0.4;
				d.Training[k] = (sbyte)(third ? rnd.Next(1, 5) : rnd.Next(0, 4));   // 북 III +1…+4, 북 II 0…+3
			}
			return d;
		}

		private static double MeanDevelopment(AiTeam t) =>
			t.Development.Count == 0 ? 0 : t.Development.Values.Average(d => Enumerable.Range(0, 6).Average(k => d.Contract[k] + d.Training[k]));

		// The tier's AI build for this style (slot % 3), cut to 90% of the tier's points from its deepest
		// traits so every trait still has its tier gate and prerequisites.
		public static Dictionary<byte, byte> TraitBuild(int tier, int slot)
		{
			string[] builds = LeagueRules.AiTraitBuilds[Math.Clamp(tier, 0, LeagueRules.Tiers - 1)];
			List<(byte idx, byte pts)> list = builds[slot % builds.Length].Split(',').Select(kv => kv.Split(':'))
				.Select(kv => (byte.Parse(kv[0]), byte.Parse(kv[1]))).ToList();
			int target = TraitPoints(tier);
			while (list.Sum(x => x.pts) > target)
			{
				int last = list.FindLastIndex(x => x.pts > 0);
				list[last] = (list[last].idx, (byte)(list[last].pts - 1));
			}
			Dictionary<byte, byte> points = list.Where(x => x.pts > 0).ToDictionary(x => x.idx, x => x.pts);
			string error = TeamPropertyHandlers.Validate(points, target);
			if (error != null)
			{
				ServerLog.Info($"league AI clubs: {LeagueRules.TierNames[tier]} build {slot % builds.Length} after the 90% cut is invalid ({error})");
			}
			return points;
		}

		private static void Save(SqliteConnection c, AiTeam t)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"INSERT OR REPLACE INTO league_ai_team (tier, slot, name, emblem, lineup, ball, traits, development, source_user, created)
				VALUES ($t, $s, $n, $e, $l, $b, $tr, $d, NULL, $now)";
			cmd.Parameters.AddWithValue("$t", t.Tier);
			cmd.Parameters.AddWithValue("$s", t.Slot);
			cmd.Parameters.AddWithValue("$n", t.Name);
			cmd.Parameters.AddWithValue("$e", t.Emblem);
			cmd.Parameters.AddWithValue("$l", t.Lineup);
			cmd.Parameters.AddWithValue("$b", t.Ball);
			cmd.Parameters.AddWithValue("$tr", string.Join(",", t.Traits.Select(kv => $"{kv.Key}:{kv.Value}")));
			// idx:재계약/트레이닝, then 특별수강/팀컬러 when there are any (챔피언스 decks), then 포지션 변경/코치 when there are any
			// (six tiers, 2026-11-08).
			cmd.Parameters.AddWithValue("$d", string.Join(";", t.Development.Values.Select(d =>
			{
				bool more = d.PositionTrainingInfo != 0 || d.Coach.Any(x => x != 0);
				return $"{d.Idx}:{string.Join(",", d.Contract)}/{string.Join(",", d.Training)}"
					+ (more || d.Educated.Any(x => x != 0) || d.TeamColor.Any(x => x != 0) ? $"/{string.Join(",", d.Educated)}/{string.Join(",", d.TeamColor)}" : string.Empty)
					+ (more ? $"/{d.PositionTrainingInfo}/{string.Join(",", d.Coach)}" : string.Empty);
			})));
			cmd.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
			cmd.ExecuteNonQuery();
		}

		private static Dictionary<byte, byte> ParseTraits(string text) =>
			text.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1]));

		private static Dictionary<int, TeamCard> ParseDevelopment(string text)
		{
			Dictionary<int, TeamCard> dev = new Dictionary<int, TeamCard>();
			foreach (string entry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] head = entry.Split(':');
				string[] parts = head[1].Split('/');
				TeamCard d = new TeamCard { Idx = int.Parse(head[0]) };
				d.Contract = parts[0].Split(',').Select(sbyte.Parse).ToArray();
				d.Training = parts[1].Split(',').Select(sbyte.Parse).ToArray();
				if (parts.Length >= 4)
				{
					d.Educated = parts[2].Split(',').Select(sbyte.Parse).ToArray();
					d.TeamColor = parts[3].Split(',').Select(sbyte.Parse).ToArray();
				}
				if (parts.Length >= 6)
				{
					d.PositionTrainingInfo = byte.Parse(parts[4]);
					d.Coach = parts[5].Split(',').Select(sbyte.Parse).ToArray();
				}
				dev[d.Idx] = d;
			}
			return dev;
		}
	}
}
