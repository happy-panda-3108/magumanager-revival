using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 팀 관리 > 팀컬러 (TeamColor_Main), systems session, 2026-10-19.
	//
	// Seven kinds, each in three slots and four grades:
	// - Slots: C (the whole 1군, 25 cards), H (the 14 batters), P (the 11 pitchers). One entry per slot
	//   (APPLY_TEAM_COLOR); a slot's bonus goes to the cards of its scope that belong to the entry's group
	//   (user, 2026-10-24: "팀컬러의 조건에 맞는 선수들만 능력치가 오를 수 있도록"; every card of the scope before), on
	//   all six stats, and plays at the development weight like the other bonuses (team_card.team_color →
	//   TeamStrength.AddBonuses).
	// - Grades by how many cards of the scope belong to the kind's best group (user: 25 of 25 = S, 19 = A,
	//   14 = B, 11 = C, C the lowest; H and P scaled to 14/11/8/6 and 11/8/6/5).
	// - S bonuses (H, P, C), 2026-10-24 (user: "팀 올스타 팀컬러는 난이도가 높지 않으므로 낮아야 하고, 오히려 20년대 팀
	//   올스타, 단일년도 팀 타자진 등등 조건이 걸리는 팀컬러의 경우 보상이 더 높아야"): the kinds that tie team and season
	//   up, the 타자진/투수진 slots most, the all-stars of one dimension down. Batters / pitchers with all three slots:
	//   단일 년도 팀 21 (was 19), 단일 년도 듀얼 팀 19 (18/16), 듀얼 년도 단일 팀 18 (15/17), 연대 단일 팀 13 (9; 11 since 10-25, below),
	//   단일 년도 올스타 5 (8/6), 단일 팀 올스타 3 (5/7), 듀얼 팀 올스타 2 (3). The first values came from the engine
	//   session's --tcsim break-even study (top 10% decks) and the user's 단일 년도 팀 4/4/15.
	//   2026-10-25 (user: "연대 팀 올스타 팀컬러의 능력치가 너무 강한 것 같음 … 현대의 경우 너무 쉽게 맞출 수 있는데, 다른
	//   팀이랑 보너스를 다르게 하기는 형평성이 안 맞으니, 전체적으로 연대 팀 올스타의 소폭 하향"): 연대 단일 팀 C 10 → 8,
	//   13 → 11 (현대 1999–2007 fits one decade, so its decade entry is nearly its all-star). H/P stay 3, the slots the
	//   user wanted higher.
	// - A/B/C = S × 0.45 / 0.25 / 0.15, from "--tcsim 150 6000 grade": the bonus that evens the best
	//   k-member decks (the rest free picks) with the free deck, as a share of the 25-member one
	//   (medians over the kinds A 0.42 B 0.22 C 0.09; 단일 년도 팀 A 0.46 B 0.28 C 0.19), rounded.
	public static class TeamColors
	{
		private enum Kind { SingleYearTeam = 1, SingleYearDualTeam, DualYearTeam, DecadeTeam, YearAllStar, TeamAllStar, DualTeamAllStar }

		private static readonly (Kind kind, string name, string group, int h, int p, int c)[] Kinds =
		{
			(Kind.SingleYearTeam, "단일 년도 팀", "같은 년도·같은 팀", 6, 6, 15),
			(Kind.SingleYearDualTeam, "단일 년도 듀얼 팀", "같은 년도의 두 팀", 4, 4, 15),
			(Kind.DualYearTeam, "듀얼 년도 단일 팀", "같은 팀의 두 년도", 4, 4, 14),
			(Kind.DecadeTeam, "연대 단일 팀", "같은 팀·같은 연대", 3, 3, 7),   // C 8 → 7 on 2026-11-12 (DecadeNeeded below)
			(Kind.YearAllStar, "단일 년도 올스타", "같은 년도", 2, 2, 3),
			(Kind.TeamAllStar, "단일 팀 올스타", "같은 팀", 1, 1, 2),
			(Kind.DualTeamAllStar, "듀얼 팀 올스타", "두 팀", 1, 1, 1),
		};

		private static readonly string[] GradeNames = { "S", "A", "B", "C" };

		private static readonly double[] GradeShare = { 1.0, 0.45, 0.25, 0.15 };

		// Members needed per slot (C, H, P) and grade (S, A, B, C).
		private static readonly int[][] Needed = { new[] { 25, 19, 14, 11 }, new[] { 14, 11, 8, 6 }, new[] { 11, 8, 6, 5 } };

		// 연대 단일 팀's own steps (user, 2026-11-12 11:5x: "연대 팀컬러 중간 단계를 두고 효과를 조금 낮춰봅시다. 난이도를 살짝 완화시켜
		// 봅시다"; 11-10 결정 4; AI tester 쌍방울 11-11 B2: a 1990s 쌍방울 25 lost S → A, +8 → +4 on 24 cards, for one card changed):
		// A one or two short of all (22 / 12 / 10) at 0.7, B at the old A count at 0.5, C at the old B at 0.3; the S of the C slot
		// 8 → 7. The 11-13 of 25 that made the old C (+1) make none now.
		private static readonly int[][] DecadeNeeded = { new[] { 25, 22, 19, 14 }, new[] { 14, 12, 11, 8 }, new[] { 11, 10, 8, 6 } };

		private static readonly double[] DecadeShare = { 1.0, 0.7, 0.5, 0.3 };

		// 듀얼 팀 kinds (user, 2026-11-10 결정 8: "듀얼 팀 팀컬러의 경우 각 팀별로 최소 10명씩 있게끔"): each of the two teams has at
		// least 40% of the grade's members (10 of 25, 8 of 19 …; 6 of 14 batters, 5 of 11 pitchers), so a near single team is no
		// dual one. 단일 년도 듀얼 팀 and 듀얼 팀 올스타; 듀얼 년도 단일 팀 (two seasons of one team) as before.
		private const double DualEachShare = 0.4;

		private static bool IsDual(Kind kind) => kind == Kind.SingleYearDualTeam || kind == Kind.DualTeamAllStar;

		private static int Need(int k, int slot, int grade) => Kinds[k].kind == Kind.DecadeTeam ? DecadeNeeded[slot][grade] : Needed[slot][grade];

		private static double Share(int k, int grade) => Kinds[k].kind == Kind.DecadeTeam ? DecadeShare[grade] : GradeShare[grade];

		// Each of a dual kind's two teams at a count (10 of 25).
		private static int DualEach(int need) => (int)Math.Ceiling(need * DualEachShare - 1e-9);

		private static readonly string[] SlotNames = { "C", "H", "P" };

		private static readonly string[] ScopeNames = { "1군 25명", "타자 14명", "투수 11명" };

		private static string s_cs;

		private static Database s_db;

		private static CardCatalog s_catalog;

		// Template index (the table's rows, one per kind, slot and grade): 1000 + kind·100 + slot·10 + grade
		// (slot 0 C, 1 H, 2 P; grade 0 S … 3 C).
		private static int Index(Kind kind, int slot, int grade) => 1000 + (int)kind * 100 + slot * 10 + grade;

		// Named entry (user, 2026-10-20: "'25 삼성 / '25 삼성 타자진 / '25 삼성 투수진"): kind·10^8 + slot·10^7 +
		// grade·10^6 + the group (BestGroup). The client (TeamColorTable) builds the name and condition from
		// the number and takes the effect from the template row.
		private const int InstanceBase = 100_000_000;

		private static int Instance(Kind kind, int slot, int grade, int group) => (int)kind * InstanceBase + slot * 10_000_000 + grade * 1_000_000 + group;

		private static (Kind kind, int slot, int grade)? Parse(int index)
		{
			int k, slot, grade;
			if (index >= InstanceBase)
			{
				k = index / InstanceBase;
				slot = index / 10_000_000 % 10;
				grade = index / 1_000_000 % 10;
			}
			else
			{
				k = (index - 1000) / 100;
				slot = index / 10 % 10;
				grade = index % 10;
				if (index < 1100) return null;
			}
			if (k < 1 || k > Kinds.Length || slot > 2 || grade > 3) return null;
			return ((Kind)k, slot, grade);
		}

		// Club names by season (the client's TeamColorTable has the same list). 10 and 14-18 are the 1982-1998 clubs
		// (cards from 2026-11-02, the client's namestring_KR and emblems have them).
		public static string TeamName(int code, int year) => code switch
		{
			0 => "두산", 1 => "해태", 2 => "한화", 3 => year <= 2008 ? "우리" : year == 2009 ? "히어로즈" : year <= 2018 ? "넥센" : "키움",
			4 => "현대", 5 => "KIA", 6 => "LG", 7 => "롯데", 8 => "삼성", 9 => year <= 2020 ? "SK" : "SSG", 11 => "쌍방울", 13 => "NC", 19 => "kt",
			10 => "OB", 14 => "빙그레", 15 => "태평양", 16 => "MBC", 17 => "청보", 18 => "삼미",
			_ => "팀" + code
		};

		// A club across its seasons (all-star kinds).
		public static string FranchiseName(int code) => code == 3 ? "히어로즈" : code == 9 ? "SK·SSG" : TeamName(code, 2025);

		// lets a 해태 card into a KIA set deck): a club bought whole or renamed counts as its successor — 해태→KIA, OB→두산,
		// 빙그레→한화, MBC→LG, 삼미→청보→태평양→현대. SK is not 쌍방울's nor 히어로즈 현대's (KBO too: new clubs); SK·SSG and
		// the 히어로즈 names already share a card code. The value is the successor's code.
		public static int Lineage(int code) => code switch { 1 => 5, 10 => 0, 14 => 2, 16 => 6, 15 or 17 or 18 => 4, _ => code };

		// The club a lineage was in a season: 해태 to 2000, OB to 1998, 빙그레 to 1993, MBC to 1989, 삼미 to 1984, 청보 to 1987,
		// 태평양 to 1995.
		public static int ClubOf(int line, int year) => line switch
		{
			5 => year <= 2000 ? 1 : 5, 0 => year <= 1998 ? 10 : 0, 2 => year <= 1993 ? 14 : 2, 6 => year <= 1989 ? 16 : 6,
			4 => year <= 1984 ? 18 : year <= 1987 ? 17 : year <= 1995 ? 15 : 4, _ => line
		};

		// A lineage's name from its clubs that have player cards (MBC, 청보 and 삼미 have none from 1993 on).
		public static string LineageName(int code) => Lineage(code) switch
		{
			5 => "KIA·해태", 0 => "두산·OB", 2 => "한화·빙그레", 4 => "현대·태평양", int line => FranchiseName(line)
		};

		// What 단일 년도 팀 S gives each batter and each pitcher (H or P, and C) on every stat: 챔피언스's AI decks (AiTeams).
		public static (int bat, int pit) SingleYearTeamS => (Kinds[0].h + Kinds[0].c, Kinds[0].p + Kinds[0].c);

		// The same at a grade (0 S, 1 A, 2 B, 3 C): 메이저's AI decks are A (user, 2026-10-29: "팀덱 A 정도로").
		public static (int bat, int pit) SingleYearTeam(int grade) => (Value(0, 1, grade) + Value(0, 0, grade), Value(0, 2, grade) + Value(0, 0, grade));

		private static int Year(int yy) => yy >= 80 ? 1900 + yy : 2000 + yy;

		private static readonly string[] SlotSuffix = { "", " 타자진", " 투수진" };

		// "'25 삼성 타자진" and "2025년 삼성" (the condition's group) for a named entry.
		public static (string name, string group) Label(int index)
		{
			var e = Parse(index);
			if (e == null || index < InstanceBase) return (e == null ? "-" : Kinds[(int)e.Value.kind - 1].name, Kinds[(int)(e?.kind ?? Kind.SingleYearTeam) - 1].group);
			int g = index % 1_000_000;
			string name, group;
			switch (e.Value.kind)
			{
				case Kind.SingleYearTeam:
				{
					int t = g / 100, y = Year(g % 100);
					name = $"'{y % 100:00} {TeamName(t, y)}";
					group = $"{y}년 {TeamName(t, y)}";
					break;
				}
				case Kind.SingleYearDualTeam:
				{
					int y = Year(g / 10000), t1 = g / 100 % 100, t2 = g % 100;
					name = $"'{y % 100:00} {TeamName(t1, y)}·{TeamName(t2, y)}";
					group = $"{y}년 {TeamName(t1, y)}·{TeamName(t2, y)}";
					break;
				}
				case Kind.DualYearTeam:
				{
					// A lineage (11-05): '95·'99 OB·두산.
					int t = g / 10000, y1 = Year(g / 100 % 100), y2 = Year(g % 100);
					string c1 = TeamName(ClubOf(t, y1), y1), c2 = TeamName(ClubOf(t, y2), y2), clubs = c1 == c2 ? c2 : $"{c1}·{c2}";
					name = $"'{y1 % 100:00}·'{y2 % 100:00} {clubs}";
					group = $"{y1}·{y2}년 {clubs}";
					break;
				}
				case Kind.DecadeTeam:
				{
					int t = g / 100, dc = g % 100, d = (dc >= 50 ? 100 + dc : 200 + dc) * 10;
					// A lineage's clubs of that decade with cards (11-05: "1990년대 OB·두산"), else the club's name as before.
					List<int> codes = Enumerable.Range(Math.Max(d, 1993), Math.Max(1, Math.Min(d + 9, 2026) - Math.Max(d, 1993) + 1)).Select(y => ClubOf(t, y)).Distinct().ToList();
					string club = codes.Count > 1 ? string.Join("·", codes.Select(code => TeamName(code, d + 5)))
						: TeamName(codes.Count == 1 ? codes[0] : t, Math.Clamp(d + 5, t == 3 ? 2008 : 1999, 2025));
					name = $"{d}년대 {club}";
					group = name;
					break;
				}
				case Kind.YearAllStar:
				{
					int y = Year(g % 100);
					name = $"'{y % 100:00} 올스타";
					group = $"{y}년";
					break;
				}
				case Kind.TeamAllStar:
					name = $"{LineageName(g)} 올스타";
					group = LineageName(g);
					break;
				default:
					name = $"{LineageName(g / 100)} & {LineageName(g % 100)} 올스타";
					group = $"{LineageName(g / 100)} & {LineageName(g % 100)}";
					break;
			}
			return (name + SlotSuffix[e.Value.slot], group);
		}

		public static int Value(int kindIndex, int slot, int grade)
		{
			var k = Kinds[kindIndex];
			int s = slot == 0 ? k.c : slot == 1 ? k.h : k.p;
			// Rounded; an entry worth 0 is left out of the table (small kinds have no low grades in a slot).
			return s <= 0 ? 0 : (int)Math.Floor(s * Share(kindIndex, grade) + 0.5);
		}

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS team_color_choice (user_code INTEGER PRIMARY KEY, c INTEGER NOT NULL, h INTEGER NOT NULL, p INTEGER NOT NULL)";
				cmd.ExecuteNonQuery();
				// 자동 등록 (2026-11-09): manual = the slots the user chose (bits C 1, H 2, P 4), fresh = new entries since the screen was
				// last opened (the 빨간 점). Teams registered before start with every slot automatic (user: the strongest).
				cmd.CommandText = "SELECT name FROM pragma_table_info('team_color_choice')";
				List<string> columns = new List<string>();
				using (SqliteDataReader r = cmd.ExecuteReader())
				{
					while (r.Read()) columns.Add(r.GetString(0));
				}
				foreach (string column in new[] { "manual", "fresh" }.Where(n => !columns.Contains(n)))
				{
					cmd.CommandText = $"ALTER TABLE team_color_choice ADD COLUMN {column} INTEGER NOT NULL DEFAULT 0";
					cmd.ExecuteNonQuery();
				}
			}
			WriteTable(Path.Combine(dataDir, "tables", "ma9_team_color_131130.txt"));
			// The cards keep what was written last; the values or rules may have changed since (2026-10-24: group
			// members only, new values), so every registration is written again at start.
			List<uint> registered = new List<uint>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT user_code FROM team_color_choice";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) registered.Add((uint)r.GetInt64(0));
			}
			foreach (uint user in registered)
			{
				WriteBonuses(user);
			}
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TEAM_COLOR>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_TEAM_COLOR>(Apply);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// The client's table (TableReader.TeamColorTable): index, C/H/P, (unused), S/A/B/C, name, condition,
		// effect on all / batters / pitchers ("NONE" where it does not apply).
		private static void WriteTable(string path)
		{
			List<string> lines = new List<string> { "index\ttype\tkind\tgrade\tname\tdesc_cond\tdesc_apply_c\tdesc_apply_h\tdesc_apply_p" };
			for (int k = 0; k < Kinds.Length; k++)
			{
				for (int slot = 0; slot < 3; slot++)
				{
					for (int g = 0; g < 4; g++)
					{
						int v = Value(k, slot, g);
						if (v <= 0) continue;
						string effect = $"해당 선수 전 능력치 +{v}";
						string cond = (g == 0 ? $"{ScopeNames[slot]} 전원이 {Kinds[k].group} 선수" : $"{Kinds[k].group} 선수가 {ScopeNames[slot]} 중 {Need(k, slot, g)}명 이상")
							+ (IsDual(Kinds[k].kind) ? $"(팀마다 {DualEach(Need(k, slot, g))}명 이상)" : string.Empty);
						lines.Add(string.Join("\t", Index(Kinds[k].kind, slot, g), SlotNames[slot], (int)Kinds[k].kind, GradeNames[g], Kinds[k].name, cond,
							slot == 0 ? effect : "NONE", slot == 1 ? effect : "NONE", slot == 2 ? effect : "NONE"));
					}
				}
			}
			string text = string.Join("\r\n", lines) + "\r\n";
			if (!File.Exists(path) || File.ReadAllText(path) != text)
			{
				File.WriteAllText(path, text);
			}
		}

		// The kind's best group in a scope and its card count. Group numbers: team·100 + yy (단일 년도 팀),
		// yy·10000 + team·100 + team (단일 년도 듀얼 팀), team·10000 + yy·100 + yy (듀얼 년도 단일 팀),
		// team·100 + decade (연대), yy (단일 년도 올스타), team (단일 팀 올스타), team·100 + team (듀얼 팀 올스타).
		// A dual kind needs cards from both of its two groups (user's naming: "'25 삼성·LG").
		private static (int group, int count) BestGroup(Kind kind, List<CardInfo> cards)
		{
			if (cards.Count == 0) return (0, 0);
			// 레전드 카드 (user, 2026-11-08: "레전드 카드는 같은 팀 구단 팀컬러에서 년도 영향을 안 받으니, 01롯데 세트덱에 선발 1명을 빼고
			// 최동원을 넣는다던가"; lineages asked the same evening: "계보로 묶기"): a 레전드 joins any season's group of its club's
			// lineage, so the groups come from the other cards and each takes the 레전드 of its line (LegendGroup).
			if (kind != Kind.TeamAllStar && kind != Kind.DualTeamAllStar && cards.Any(c => c.IsLegend) && cards.Any(c => !c.IsLegend))
			{
				return LegendGroup(kind, cards.Where(c => !c.IsLegend).ToList(), cards.Where(c => c.IsLegend).ToList());
			}
			int Yy(int year) => year % 100;
			// Two biggest subgroups (ties: the later season / the lower club code first), or none.
			List<(int key, int n)> Two(IEnumerable<IGrouping<int, CardInfo>> groups, bool byYear) => groups
				.Select(x => (key: x.Key, n: x.Count())).OrderByDescending(x => x.n).ThenBy(x => byYear ? -x.key : x.key).Take(2).ToList();
			switch (kind)
			{
				case Kind.SingleYearTeam:
				{
					var best = cards.GroupBy(c => (c.TeamCode, c.Year)).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key.Year).First();
					return (best.Key.TeamCode * 100 + Yy(best.Key.Year), best.Count());
				}
				case Kind.SingleYearDualTeam:
				{
					var best = cards.GroupBy(c => c.Year).Select(y => (year: y.Key, two: Two(y.GroupBy(c => c.TeamCode), false)))
						.Where(x => x.two.Count == 2).OrderByDescending(x => x.two.Sum(t => t.n)).ThenByDescending(x => x.year).FirstOrDefault();
					if (best.two == null) return (0, 0);
					int a = Math.Min(best.two[0].key, best.two[1].key), b = Math.Max(best.two[0].key, best.two[1].key);
					return (Yy(best.year) * 10000 + a * 100 + b, best.two.Sum(t => t.n));
				}
				case Kind.DualYearTeam:
				{
					var best = cards.GroupBy(c => Lineage(c.TeamCode)).Select(t => (team: t.Key, two: Two(t.GroupBy(c => c.Year), true)))
						.Where(x => x.two.Count == 2).OrderByDescending(x => x.two.Sum(t => t.n)).ThenBy(x => x.team).FirstOrDefault();
					if (best.two == null) return (0, 0);
					int y1 = Math.Min(best.two[0].key, best.two[1].key), y2 = Math.Max(best.two[0].key, best.two[1].key);
					return (best.team * 10000 + Yy(y1) * 100 + Yy(y2), best.two.Sum(t => t.n));
				}
				case Kind.DecadeTeam:
				{
					var best = cards.GroupBy(c => (TeamCode: Lineage(c.TeamCode), Decade: c.Year / 10)).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key.Decade).First();
					return (best.Key.TeamCode * 100 + best.Key.Decade % 100, best.Count());
				}
				case Kind.YearAllStar:
				{
					var best = cards.GroupBy(c => c.Year).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key).First();
					return (Yy(best.Key), best.Count());
				}
				case Kind.TeamAllStar:
				{
					var best = cards.GroupBy(c => Lineage(c.TeamCode)).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).First();
					return (best.Key, best.Count());
				}
				default:
				{
					var two = Two(cards.GroupBy(c => Lineage(c.TeamCode)), false);
					if (two.Count < 2) return (0, 0);
					return (Math.Min(two[0].key, two[1].key) * 100 + Math.Max(two[0].key, two[1].key), two.Sum(t => t.n));
				}
			}
		}

		// BestGroup with 레전드 cards: the groups of the other cards (as BestGroup makes them, same tie rules), each counting the
		// 레전드 of its club's lineage too (단일 년도 올스타: every 레전드, a season of its own not counting).
		private static (int group, int count) LegendGroup(Kind kind, List<CardInfo> normal, List<CardInfo> legends)
		{
			int Yy(int year) => year % 100;
			int Of(int line) => legends.Count(c => Lineage(c.TeamCode) == line);
			switch (kind)
			{
				case Kind.SingleYearTeam:
				{
					var best = normal.GroupBy(c => (c.TeamCode, c.Year)).Select(g => (g.Key, n: g.Count() + Of(Lineage(g.Key.TeamCode))))
						.OrderByDescending(x => x.n).ThenByDescending(x => x.Key.Year).First();
					return (best.Key.TeamCode * 100 + Yy(best.Key.Year), best.n);
				}
				case Kind.SingleYearDualTeam:
				{
					var best = normal.GroupBy(c => c.Year).Select(y => (year: y.Key, two: y.GroupBy(c => c.TeamCode)
							.Select(t => (key: t.Key, n: t.Count() + Of(Lineage(t.Key)))).OrderByDescending(t => t.n).ThenBy(t => t.key).Take(2).ToList()))
						.Where(x => x.two.Count == 2).OrderByDescending(x => x.two.Sum(t => t.n)).ThenByDescending(x => x.year).FirstOrDefault();
					if (best.two == null) return (0, 0);
					int a = Math.Min(best.two[0].key, best.two[1].key), b = Math.Max(best.two[0].key, best.two[1].key);
					int both = Lineage(a) == Lineage(b) ? Of(Lineage(a)) : 0;   // one lineage's 레전드 counted once
					return (Yy(best.year) * 10000 + a * 100 + b, best.two.Sum(t => t.n) - both);
				}
				case Kind.DualYearTeam:
				{
					var best = normal.GroupBy(c => Lineage(c.TeamCode)).Select(t => (team: t.Key, two: t.GroupBy(c => c.Year)
							.Select(y => (key: y.Key, n: y.Count())).OrderByDescending(y => y.n).ThenBy(y => -y.key).Take(2).ToList()))
						.Where(x => x.two.Count == 2).OrderByDescending(x => x.two.Sum(t => t.n) + Of(x.team)).ThenBy(x => x.team).FirstOrDefault();
					if (best.two == null) return (0, 0);
					int y1 = Math.Min(best.two[0].key, best.two[1].key), y2 = Math.Max(best.two[0].key, best.two[1].key);
					return (best.team * 10000 + Yy(y1) * 100 + Yy(y2), best.two.Sum(t => t.n) + Of(best.team));
				}
				case Kind.DecadeTeam:
				{
					var best = normal.GroupBy(c => (TeamCode: Lineage(c.TeamCode), Decade: c.Year / 10)).Select(g => (g.Key, n: g.Count() + Of(g.Key.TeamCode)))
						.OrderByDescending(x => x.n).ThenByDescending(x => x.Key.Decade).First();
					return (best.Key.TeamCode * 100 + best.Key.Decade % 100, best.n);
				}
				default:   // 단일 년도 올스타
				{
					var best = normal.GroupBy(c => c.Year).OrderByDescending(x => x.Count()).ThenByDescending(x => x.Key).First();
					return (Yy(best.Key), best.Count() + legends.Count);
				}
			}
		}

		// Whether a card belongs to an entry's group (the numbers BestGroup gives).
		private static bool IsMember(Kind kind, int group, CardInfo c)
		{
			if (c == null) return false;
			// A 레전드 card: any season of its club's lineage (LegendGroup).
			if (c.IsLegend)
			{
				int legendLine = Lineage(c.TeamCode);
				return kind switch
				{
					Kind.SingleYearTeam => legendLine == Lineage(group / 100),
					Kind.SingleYearDualTeam => legendLine == Lineage(group / 100 % 100) || legendLine == Lineage(group % 100),
					Kind.DualYearTeam => legendLine == group / 10000,
					Kind.DecadeTeam => legendLine == group / 100,
					Kind.YearAllStar => true,
					Kind.TeamAllStar => legendLine == group,
					_ => legendLine == group / 100 || legendLine == group % 100
				};
			}
			// The team kinds over seasons count a club's lineage (user, 2026-11-05: "코치·팀컬러 모두 계보로"); one season has
			// one club of a line, so the single-year kinds keep the card's own club.
			int yy = c.Year % 100, team = c.TeamCode, line = Lineage(team);
			return kind switch
			{
				Kind.SingleYearTeam => team == group / 100 && yy == group % 100,
				Kind.SingleYearDualTeam => yy == group / 10000 && (team == group / 100 % 100 || team == group % 100),
				Kind.DualYearTeam => line == group / 10000 && (yy == group / 100 % 100 || yy == group % 100),
				Kind.DecadeTeam => line == group / 100 && c.Year / 10 % 100 == group % 100,
				Kind.YearAllStar => yy == group,
				Kind.TeamAllStar => line == group,
				_ => line == group / 100 || line == group % 100
			};
		}

		// The 1군 split into the three scopes (empty slots left out).
		private static List<CardInfo>[] Scopes(Lineup l)
		{
			List<CardInfo> bats = (l?.Batters ?? Array.Empty<int>()).Where(i => i != 0).Select(s_catalog.Get).Where(c => c != null).ToList();
			List<CardInfo> pits = (l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0).Select(s_catalog.Get).Where(c => c != null).ToList();
			return new[] { bats.Concat(pits).ToList(), bats, pits };
		}

		// The best grade (0 S … 3 C, -1 none) a scope reaches in a kind and slot, with the group and its members (2026-11-12: the
		// kind's own steps, Need; a dual kind's pair with each team at DualEach, DualReach).
		private static (int group, int grade, int members) Reach(int k, int slot, List<CardInfo> cards)
		{
			if (IsDual(Kinds[k].kind))
			{
				return DualReach(k, slot, cards);
			}
			(int group, int members) = BestGroup(Kinds[k].kind, cards);
			int grade = members == 0 ? -1 : Enumerable.Range(0, 4).Where(g => members >= Need(k, slot, g) && Value(k, slot, g) > 0).DefaultIfEmpty(-1).First();
			return (group, grade, members);
		}

		// A dual kind's best pair: in each season the four clubs with most cards (단일 년도 듀얼 팀), or the five lineages with most
		// (듀얼 팀 올스타); the best grade where both together have the members and each has DualEach of them, then the most members.
		// Members of each team as the single kinds count them (a 레전드 with its lineage). Group numbers as BestGroup's.
		private static (int group, int grade, int members) DualReach(int k, int slot, List<CardInfo> cards)
		{
			Kind kind = Kinds[k].kind;
			List<CardInfo> normal = cards.Where(c => !c.IsLegend).ToList();
			if (normal.Count == 0) return (0, -1, 0);
			List<(int group, int a, int b)> pairs = new List<(int, int, int)>();
			// Seasons too small even for a hint (a third of the lowest grade since 2026-11-13, Hints) are skipped: 자동 편성 asks
			// this thousands of times.
			int floor = (Need(k, slot, 3) + 2) / 3 - (cards.Count - normal.Count);
			if (kind == Kind.SingleYearDualTeam)
			{
				foreach (IGrouping<int, CardInfo> season in normal.GroupBy(c => c.Year).Where(s => s.Count() >= floor))
				{
					int yy = season.Key % 100;
					List<int> teams = season.GroupBy(c => c.TeamCode).OrderByDescending(t => t.Count()).ThenBy(t => t.Key).Take(4).Select(t => t.Key).ToList();
					Dictionary<int, int> n = teams.ToDictionary(t => t, t => cards.Count(c => IsMember(Kind.SingleYearTeam, t * 100 + yy, c)));
					for (int i = 0; i < teams.Count; i++)
					{
						for (int j = i + 1; j < teams.Count; j++)
						{
							int lo = Math.Min(teams[i], teams[j]), hi = Math.Max(teams[i], teams[j]);
							pairs.Add((yy * 10000 + lo * 100 + hi, n[lo], n[hi]));
						}
					}
				}
			}
			else
			{
				List<int> lines = cards.GroupBy(c => Lineage(c.TeamCode)).OrderByDescending(t => t.Count()).ThenBy(t => t.Key).Take(5).Select(t => t.Key).ToList();
				Dictionary<int, int> n = lines.ToDictionary(t => t, t => cards.Count(c => IsMember(Kind.TeamAllStar, t, c)));
				for (int i = 0; i < lines.Count; i++)
				{
					for (int j = i + 1; j < lines.Count; j++)
					{
						int lo = Math.Min(lines[i], lines[j]), hi = Math.Max(lines[i], lines[j]);
						pairs.Add((lo * 100 + hi, n[lo], n[hi]));
					}
				}
			}
			if (pairs.Count == 0) return (0, -1, 0);
			int GradeOf((int group, int a, int b) p) => Enumerable.Range(0, 4)
				.Where(g => p.a + p.b >= Need(k, slot, g) && Math.Min(p.a, p.b) >= DualEach(Need(k, slot, g)) && Value(k, slot, g) > 0).DefaultIfEmpty(-1).First();
			var best = pairs.Select(p => (p.group, grade: GradeOf(p), members: p.a + p.b))
				.OrderBy(p => p.grade < 0 ? 9 : p.grade).ThenByDescending(p => p.members).ThenByDescending(p => p.group).First();
			return best;
		}

		// Whether batters and pitchers (a 1군's 14 + 11) reach any team colour; no catalog needed (StarterTeam.DrawClub, 2026-11-12).
		public static bool AnyAvailable(List<CardInfo> batters, List<CardInfo> pitchers)
		{
			List<CardInfo>[] scopes = { batters.Concat(pitchers).ToList(), batters, pitchers };
			for (int k = 0; k < Kinds.Length; k++)
			{
				for (int slot = 0; slot < 3; slot++)
				{
					if (Reach(k, slot, scopes[slot]).grade >= 0) return true;
				}
			}
			return false;
		}

		// Entries the lineup qualifies for: per kind and slot, its best group at the best grade it reaches.
		public static HashSet<int> Available(Lineup l)
		{
			List<CardInfo>[] scopes = Scopes(l);
			HashSet<int> ok = new HashSet<int>();
			for (int k = 0; k < Kinds.Length; k++)
			{
				for (int slot = 0; slot < 3; slot++)
				{
					(int group, int grade, int members) = Reach(k, slot, scopes[slot]);
					if (members > 0 && grade >= 0)
					{
						ok.Add(Instance(Kinds[k].kind, slot, grade, group));
					}
				}
			}
			return ok;
		}

		// Greyed hints for the list: per kind and slot, the best group's next grade up (or its lowest grade
		// when it reaches none), so the screen shows what a few more players would give. Since 2026-11-12 (user 11-10 결정 8:
		// "팀컬러의 경우 조건의 반이상 인원이 갖춰지면 보이게끔") only once the group has half that grade's members; a third since
		// 2026-11-13 (user 16:5x 결정 4: "팀컬러 힌트의 기준을 1/3로 낮춥시다"; AI testers: half hid the targets worth chasing).
		private static List<int> Hints(Lineup l)
		{
			List<CardInfo>[] scopes = Scopes(l);
			List<int> hints = new List<int>();
			for (int k = 0; k < Kinds.Length; k++)
			{
				for (int slot = 0; slot < 3; slot++)
				{
					(int group, int grade, int members) = Reach(k, slot, scopes[slot]);
					if (members == 0) continue;
					int reached = grade < 0 ? 4 : grade;
					int target = Enumerable.Range(0, reached).Where(g => Value(k, slot, g) > 0).DefaultIfEmpty(-1).Last();
					if (target >= 0 && members * 3 >= Need(k, slot, target)) hints.Add(Instance(Kinds[k].kind, slot, target, group));
				}
			}
			return hints;
		}

		// A registration follows the lineup: each slot keeps its kind, and the entry is whatever group and
		// grade the 1군 reaches in it now (user: a partly changed deck plays a weaker team colour), 0 when
		// it reaches none.
		private static int[] Resolve(int[] choice, HashSet<int> available)
		{
			int[] r = new int[3];
			for (int slot = 0; slot < 3; slot++)
			{
				var e = Parse(choice[slot]);
				if (e == null || e.Value.slot != slot) continue;
				r[slot] = available.FirstOrDefault(i => Parse(i) is { } p && p.kind == e.Value.kind && p.slot == slot);
			}
			return r;
		}

		public static int[] Choice(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT c, h, p FROM team_color_choice WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			return r.Read() ? new[] { r.GetInt32(0), r.GetInt32(1), r.GetInt32(2) } : new int[3];
		}

		private static void SaveChoice(uint user, int[] choice)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			// Keeps manual and fresh (INSERT OR REPLACE reset them).
			cmd.CommandText = "INSERT INTO team_color_choice (user_code, c, h, p) VALUES ($u, $c, $h, $p) ON CONFLICT(user_code) DO UPDATE SET c = excluded.c, h = excluded.h, p = excluded.p";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$c", choice[0]);
			cmd.Parameters.AddWithValue("$h", choice[1]);
			cmd.Parameters.AddWithValue("$p", choice[2]);
			cmd.ExecuteNonQuery();
		}

		// Per-card bonus of the chosen entries the lineup still qualifies for, on the cards of each entry's group.
		public static Dictionary<int, int> Bonuses(Lineup l, int[] choice)
		{
			HashSet<int> available = Available(l);
			int[][] scopes =
			{
				(l?.Batters ?? Array.Empty<int>()).Concat(l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0).ToArray(),
				(l?.Batters ?? Array.Empty<int>()).Where(i => i != 0).ToArray(),
				(l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0).ToArray()
			};
			Dictionary<int, int> bonus = new Dictionary<int, int>();
			choice = Resolve(choice, available);
			for (int slot = 0; slot < 3; slot++)
			{
				var e = Parse(choice[slot]);
				if (e == null || e.Value.slot != slot || !available.Contains(choice[slot])) continue;
				int v = Value((int)e.Value.kind - 1, slot, e.Value.grade);
				int group = choice[slot] % 1_000_000;
				foreach (int idx in scopes[slot].Where(i => IsMember(e.Value.kind, group, s_catalog.Get(i))))
				{
					bonus[idx] = bonus.GetValueOrDefault(idx) + v;
				}
			}
			return bonus;
		}

		// A candidate 1군's bonuses as the 자동 등록 would give them (each slot its strongest entry) and those entries (engine
		// session, 2026-11-11: 자동 편성 counts the team colour a 1군 would get).
		public static (Dictionary<int, int> bonus, int[] entries) AutoBonuses(Lineup l)
		{
			HashSet<int> available = Available(l);
			int[] choice = Enumerable.Range(0, 3).Select(slot => Strongest(l, slot, available)).ToArray();
			return (Bonuses(l, choice), choice);
		}

		// For 자동 편성's team colour plans (engine session, 2026-11-12): kinds, members needed, entry numbers and membership.
		// kindIndex 0-6 in the Kinds order; slot 0 C, 1 H, 2 P; grade 0 S … 3 C; group as BestGroup numbers it.
		public static int KindCount => Kinds.Length;

		public static int NeededFor(int slot, int grade) => Needed[slot][grade];

		// By kind (systems session 2026-11-12: 연대 단일 팀's own steps). A dual kind also wants DualEachFor of each team.
		public static int NeededFor(int kindIndex, int slot, int grade) => Need(kindIndex, slot, grade);

		public static int DualEachFor(int kindIndex, int slot, int grade) => IsDual(Kinds[kindIndex].kind) ? DualEach(Need(kindIndex, slot, grade)) : 0;

		public static int InstanceOf(int kindIndex, int slot, int grade, int group) => Instance(Kinds[kindIndex].kind, slot, grade, group);

		public static bool IsMemberOf(int instance, CardInfo c) => instance >= InstanceBase && Parse(instance) is { } e && IsMember(e.kind, instance % 1_000_000, c);

		// Writes the bonuses into team_card.team_color (every card; 0 off the 1군). Call after the lineup or
		// the choice changes.
		public static void Refresh(uint user)
		{
			if (s_cs == null) return;
			AutoRegister(user);
			WriteBonuses(user);
			// 팀컬러 업적: the registered entries follow the lineup (grade and group), so a better 1군 counts as applied.
			RecordForAchievements(user);
		}

		private static void WriteBonuses(uint user)
		{
			Dictionary<int, int> bonus = Bonuses(s_db.GetLineup(user), Choice(user));
			List<TeamCard> cards = s_db.GetCards(user);
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (TeamCard card in cards)
			{
				sbyte v = (sbyte)Math.Clamp(bonus.GetValueOrDefault(card.Idx), 0, 100);
				if (card.TeamColor.All(x => x == v)) continue;
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = "UPDATE team_card SET team_color = $t WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.Add("$t", SqliteType.Blob).Value = Enumerable.Repeat((byte)v, 6).ToArray();
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", card.Idx);
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// 팀컬러 이름별 업적 (user, 2026-10-22: every named team colour applied for the first time is its own
		// achievement, "이름이 다른 팀컬러를 적용하면 서로 다른 팀컬러로 쳐서 개수가 늘어나는 방식"). Its grade (E…S, the
		// reward) by how hard it is to collect: the kind's S in the C slot — 단일 년도 팀 S, the dual kinds A, 연대 단일 팀
		// and the two single all-stars B ("단일 팀 올스타 S는 너무 쉬우니 B"), 듀얼 팀 올스타 C — one step lower per grade
		// below S and for the H/P slots (14 batters / 11 pitchers), never below E.
		private static readonly int[] AchievementTop = { 5, 4, 4, 3, 3, 3, 2 };

		private const string AchievementGrades = "EDCBAS";

		public static (string name, string detail, char grade, int nameKey)? Achievement(int instance)
		{
			var e = Parse(instance);
			if (e == null || instance < InstanceBase)
			{
				return null;
			}
			int step = Math.Max(0, AchievementTop[(int)e.Value.kind - 1] - e.Value.grade - (e.Value.slot == 0 ? 0 : 1));
			string label = Label(instance).name, grade = GradeNames[e.Value.grade];
			return ($"{label} {grade}", $"팀컬러 '{label}'({Kinds[(int)e.Value.kind - 1].name}) {grade} 등급을 처음 적용하세요.",
				AchievementGrades[step], instance - e.Value.grade * 1_000_000);
		}

		// The same named entry at its lower grades that exist (a kind with few points has no low grades in a slot):
		// applying S also clears A, B and C, so going straight to S pays as much as climbing.
		public static IEnumerable<int> WithLowerGrades(int instance)
		{
			var e = Parse(instance);
			if (e == null || instance < InstanceBase)
			{
				yield break;
			}
			int baseIndex = instance - e.Value.grade * 1_000_000;
			for (int g = e.Value.grade; g < GradeNames.Length; g++)
			{
				if (g == e.Value.grade || Value((int)e.Value.kind - 1, e.Value.slot, g) > 0)
				{
					yield return baseIndex + g * 1_000_000;
				}
			}
		}

		// For APPLY_ROSTER_PITCHER: chosen entries the new lineup lost, and entries it newly qualifies for.
		public static (int[] lost, int[] gained) Diff(uint user, Lineup before, Lineup after)
		{
			HashSet<int> was = Available(before), now = Available(after);
			int[] choice = Choice(user), wasChosen = Resolve(choice, was), nowChosen = Resolve(choice, now);
			int[] lost = Enumerable.Range(0, 3).Where(slot => wasChosen[slot] != 0 && nowChosen[slot] == 0).Select(slot => wasChosen[slot]).ToArray();
			int[] gained = now.Where(i => !was.Contains(i)).ToArray();
			return (lost, gained);
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_TEAM_COLOR packet)
		{
			if (session.Team == null) return;
			uint user = session.Team.UserCode;
			Refresh(user);
			// The screen seen: the 빨간 점 for new entries goes (Notice).
			SetState(user, "fresh", 0);
			Lineup lineup = s_db.GetLineup(user);
			HashSet<int> available = Available(lineup);
			int[] choice = Resolve(Choice(user), available);
			Ma9MPacket.PKS_SC_ENTER_TEAM_COLOR reply = new Ma9MPacket.PKS_SC_ENTER_TEAM_COLOR();
			reply.team_color_c.n = choice[0];
			reply.team_color_h.n = choice[1];
			reply.team_color_p.n = choice[2];
			reply.team_colors.array = NonEmpty(available.OrderBy(i => i));
			// Greyed entries after the available ones: the next grade each kind could reach.
			reply.show_team_colors.array = NonEmpty(Hints(lineup).Where(i => !available.Contains(i)));
			session.Send(reply);
		}

		// The client reads an empty PArray as null, and TeamColor_DataCenter then drops the whole reply: a 1군 at S in
		// every slot has no greyed hints left, and the screen showed nothing at all (user, 2026-10-25, a full '25 LG
		// deck: "팀컬러가 하나도 안 나오네?"). Index 0 is no entry, which the client skips (TeamColorTable.GetTeamColor).
		private static PInteger[] NonEmpty(IEnumerable<int> ids)
		{
			PInteger[] array = ids.Select(i => new PInteger { n = i }).ToArray();
			return array.Length > 0 ? array : new[] { new PInteger { n = 0 } };
		}

		private static void Apply(GameSession session, Ma9MPacket.PKS_CS_APPLY_TEAM_COLOR packet)
		{
			Ma9MPacket.PKS_SC_APPLY_TEAM_COLOR reply = new Ma9MPacket.PKS_SC_APPLY_TEAM_COLOR();
			if (session.Team == null)
			{
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			uint user = session.Team.UserCode;
			int[] choice = { packet.team_color_c.n, packet.team_color_h.n, packet.team_color_p.n };
			bool ok = TryApply(user, choice);
			reply.result.n = (byte)(ok ? 0 : 1);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 팀컬러 C {EntryName(choice[0])}, H {EntryName(choice[1])}, P {EntryName(choice[2])}: {(ok ? "ok" : "refused")}");
		}

		// The best totals a 1군 of 14 batters and 11 pitchers can register: batters C + H, pitchers C + P
		// (TeamColorSim deck reports).
		public static (int bat, int pit) BestTotals(List<CardInfo> batters, List<CardInfo> pitchers)
		{
			List<CardInfo>[] scopes = { batters.Concat(pitchers).ToList(), batters, pitchers };
			int[] best = new int[3];
			for (int k = 0; k < Kinds.Length; k++)
			{
				for (int slot = 0; slot < 3; slot++)
				{
					(_, int grade, _) = Reach(k, slot, scopes[slot]);
					if (grade >= 0) best[slot] = Math.Max(best[slot], Value(k, slot, grade));
				}
			}
			return (best[0] + best[1], best[0] + best[2]);
		}

		// 등록: every chosen entry (0 = empty slot) must be one the 1군 qualifies for, in its own slot.
		public static bool TryApply(uint user, int[] choice)
		{
			HashSet<int> available = Available(s_db.GetLineup(user));
			bool ok = Enumerable.Range(0, 3).All(slot => choice[slot] == 0 || (available.Contains(choice[slot]) && Parse(choice[slot])?.slot == slot));
			if (ok)
			{
				// The user's own choice where it is not the strongest (자동 등록 keeps it while it holds); an empty slot stays automatic.
				Lineup l = s_db.GetLineup(user);
				int manual = Enumerable.Range(0, 3).Where(slot => choice[slot] != 0 && choice[slot] != Strongest(l, slot, available)).Aggregate(0, (m, slot) => m | 1 << slot);
				SaveChoice(user, choice);
				SetState(user, "manual", manual);
				Refresh(user);
				RecordForAchievements(user);
			}
			return ok;
		}

		// 자동 등록 (user, 2026-11-09: "가장 강한 팀컬러로 자동 등록되는걸로도 합시다. 그래서 본인이 도전과제를 깨기 위해 다른 팀컬러도
		// 적용하겠다 하면, 직접 다시 고를 수도 있는걸로"): at every 1군 save, the team colour screen and 등록 (Refresh), each slot takes
		// the strongest entry the 1군 reaches, unless the user chose that slot (manual, TryApply) and the choice still holds (its
		// kind in that slot, Resolve); a choice that broke goes back to automatic. Returns whether the registration changed.
		private static bool AutoRegister(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			HashSet<int> available = Available(l);
			int[] stored = Choice(user), resolved = Resolve(stored, available);
			int manual = State(user, "manual"), mask = 0;
			int[] next = new int[3];
			for (int slot = 0; slot < 3; slot++)
			{
				if ((manual & 1 << slot) != 0 && resolved[slot] != 0)
				{
					next[slot] = stored[slot];
					mask |= 1 << slot;
				}
				else
				{
					next[slot] = Strongest(l, slot, available);
				}
			}
			if (next.SequenceEqual(stored) && mask == manual) return false;
			SaveChoice(user, next);
			SetState(user, "manual", mask);
			return true;
		}

		// The strongest entry of a slot: the most stat points it gives (its value on each member of its group in the slot's
		// scope), then the higher value per card. 0 when the 1군 reaches none.
		private static int Strongest(Lineup l, int slot, HashSet<int> available)
		{
			List<CardInfo> scope = Scopes(l)[slot];
			return available.Where(i => Parse(i)?.slot == slot)
				.Select(i => (i, kind: Parse(i).Value.kind, v: Value((int)Parse(i).Value.kind - 1, slot, Parse(i).Value.grade)))
				.OrderByDescending(x => x.v * scope.Count(c => IsMember(x.kind, x.i % 1_000_000, c)))
				.ThenByDescending(x => x.v)
				.ThenBy(x => x.i)
				.Select(x => x.i)
				.FirstOrDefault();
		}

		private static int State(uint user, string column)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = $"SELECT {column} FROM team_color_choice WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			object v = cmd.ExecuteScalar();
			return v == null || v is DBNull ? 0 : Convert.ToInt32(v);
		}

		private static void SetState(uint user, string column, int value)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = $"INSERT INTO team_color_choice (user_code, c, h, p, {column}) VALUES ($u, 0, 0, 0, $v) ON CONFLICT(user_code) DO UPDATE SET {column} = $v";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$v", value);
			cmd.ExecuteNonQuery();
		}

		// What is registered and working now (each slot's entry, 0 when none), for the popup after a 1군 save.
		public static int[] Registered(uint user)
		{
			if (s_cs == null) return new int[3];
			return Resolve(Choice(user), Available(s_db.GetLineup(user)));
		}

		// After a 1군 save (RosterHandlers.ApplyPitchers; Refresh has registered again): a popup naming each slot that changed and
		// the user's choices kept although a stronger entry came, or null when the registration is as before. gained = entries
		// new to the 1군 and not registered, for the app's own "새로운 팀컬러가 활성화되었습니다" popup when nothing changed; they
		// also light the 빨간 점 until the team colour screen is opened.
		public static (Ma9MPacket.PKS_SC_POPUP popup, int[] gained) AfterSave(uint user, Lineup before, int[] wasRegistered)
		{
			if (s_cs == null) return (null, Array.Empty<int>());
			Lineup after = s_db.GetLineup(user);
			HashSet<int> was = Available(before), now = Available(after);
			int[] reg = Resolve(Choice(user), now);
			int[] gained = now.Where(i => !was.Contains(i) && !reg.Contains(i)).ToArray();
			if (gained.Length > 0) SetState(user, "fresh", 1);
			if (reg.SequenceEqual(wasRegistered)) return (null, gained);
			int manual = State(user, "manual");
			// Sentences (user, 2026-11-11 18:4x: "\"@@ S 팀컬러가 해제되었습니다.\" << 이 멘트가 나오게 합시다"): a slot that lost its entry says so, and
			// one that changed to another entry says both (it used to say only "… 등록", AI tester kt 4회차). Five lines fit the box
			// (PopupText); with more, one short line per slot.
			List<string> lines = new List<string>(), compact = new List<string>();
			for (int slot = 0; slot < 3; slot++)
			{
				int old = wasRegistered[slot], got = reg[slot];
				if (got != old)
				{
					if (old != 0) lines.Add($"{Short(old)} 팀컬러가 해제되었습니다.");
					if (got != 0) lines.Add($"{Short(got)} 팀컬러가 등록되었습니다.");
					compact.Add(old != 0 && got != 0 ? $"{Short(old)} 해제 → {Short(got)} 등록" : old != 0 ? $"{Short(old)} 해제" : $"{Short(got)} 등록");
				}
				else if ((manual & 1 << slot) != 0 && got != 0 && Strongest(after, slot, now) != got)
				{
					lines.Add($"직접 고른 {Short(got)} 그대로");
					compact.Add($"직접 고른 {Short(got)} 그대로");
				}
			}
			if (lines.Count > 4 || lines.Any(line => PopupText.Px(line) > PopupText.Width)) lines = compact;
			lines.Add("팀 관리 > 팀컬러에서 바꿀 수 있습니다.");
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			string title = Enumerable.Range(0, 3).Any(slot => reg[slot] != 0 && reg[slot] != wasRegistered[slot]) ? "팀컬러 자동 등록" : "팀컬러 해제";
			popup.Messages.array = new[] { new PLongString { str = title }, new PLongString { str = string.Join("\n", lines) } };
			return (popup, Array.Empty<int>());
		}

		// "LG 올스타 타자진 A"
		private static string Short(int i) => Parse(i) is { } e ? $"{Label(i).name} {GradeNames[e.grade]}" : "-";

		// The same for AdviceHandlers' 자동 편성 window (systems session, 2026-11-12).
		public static string ShortName(int i) => Short(i);

		// 빨간 점 (user, 2026-11-09 D-2: "팀컬러 역시 사용가능한데 등록을 안 한 상태면 빨간 점을 표시"): a slot left empty although
		// the 1군 reaches an entry for it, or entries new since the team colour screen was last opened (fresh).
		public static bool Notice(uint user)
		{
			if (s_cs == null) return false;
			if (State(user, "fresh") != 0) return true;
			HashSet<int> available = Available(s_db.GetLineup(user));
			int[] reg = Resolve(Choice(user), available);
			return Enumerable.Range(0, 3).Any(slot => reg[slot] == 0 && available.Any(i => Parse(i)?.slot == slot));
		}

		// 팀컬러 업적 (Achievements 411, 415–417): what the registration gives with the 1군 as it is now —
		// any entry, all three slots, an S entry, 단일 년도 팀 S in the C slot.
		public static void RecordForAchievements(uint user)
		{
			if (s_cs == null) return;
			int[] r = Resolve(Choice(user), Available(s_db.GetLineup(user)));
			var parsed = r.Select(i => i == 0 ? null : Parse(i)).ToArray();
			bool any = r.Any(i => i != 0), full = r.All(i => i != 0);
			bool s = parsed.Any(p => p != null && p.Value.grade == 0);
			bool teamYear = parsed[0] != null && parsed[0].Value.kind == Kind.SingleYearTeam && parsed[0].Value.grade == 0;
			if (any) Missions.SetAtLeast(user, Missions.TeamColour, 1);
			if (full) Missions.SetAtLeast(user, Missions.TeamColourFull, 1);
			if (s) Missions.SetAtLeast(user, Missions.TeamColourS, 1);
			if (teamYear) Missions.SetAtLeast(user, Missions.TeamYearS, 1);
			// 이름별 업적 and the 팀컬러 n종 count (Achievements).
			if (any) Achievements.TeamColoursApplied(user, r.Where(i => i >= InstanceBase));
			if (any) Achievements.Evaluate(user);
		}

		private static string EntryName(int i) => i == 0 || Parse(i) == null ? "-" : $"{Label(i).name} {GradeNames[Parse(i).Value.grade]} ({Kinds[(int)Parse(i).Value.kind - 1].name})";

		// Self-test (--leaguetest, on the data copy): registering the best entries through the 등록 path puts
		// the bonus on the 1군 cards of each entry's group and nothing on the others, and it reaches the card window's 팀컬러 stat
		// and the sim's bonus points (TeamStrength.Bonus); an entry in the wrong slot is refused; the team's
		// own registration is put back afterwards. Returns the number of problems.
		public static int SelfTest(uint user, out string line)
		{
			int[] original = Choice(user);
			Lineup l = s_db.GetLineup(user);
			HashSet<int> available = Available(l);
			int[] best = new int[3];
			for (int slot = 0; slot < 3; slot++)
			{
				best[slot] = available.Where(i => Parse(i)?.slot == slot).OrderByDescending(i => Value((int)Parse(i).Value.kind - 1, slot, Parse(i).Value.grade)).FirstOrDefault();
			}
			int problems = 0;
			bool applied = TryApply(user, best);
			if (!applied) problems++;
			Dictionary<int, int> expected = Bonuses(l, best);
			List<TeamCard> cards = s_db.GetCards(user);
			int wrong = cards.Count(c => c.TeamColor.Any(x => x != expected.GetValueOrDefault(c.Idx)));
			problems += wrong;
			TeamCard sample = cards.Where(c => expected.GetValueOrDefault(c.Idx) > 0).OrderByDescending(c => expected[c.Idx]).FirstOrDefault();
			string shown = "-";
			if (sample != null)
			{
				sbyte[] stat = RosterHandlers.ToCardInfoStat(user, sample, DateTime.Today).team_color_stat.sbytes;
				double[] sim = TeamStrength.Bonus(sample);
				int v = expected[sample.Idx];
				bool statOk = stat.All(x => x == v);
				bool simOk = Enumerable.Range(0, 6).All(j => sim[j] == sample.Educated[j] + sample.Contract[j] + sample.Training[j] + v);
				if (!statOk || !simOk) problems++;
				shown = $"{s_catalog.Get(sample.Idx)?.Name ?? sample.Idx.ToString()} 카드창 +{string.Join("/", stat)}{(simOk ? "" : " sim FAIL")}";
			}
			// A batter entry in the C slot must be refused and leave the registration as it was.
			bool refusedOk = true;
			if (best[1] != 0)
			{
				refusedOk = !TryApply(user, new[] { best[1], 0, 0 }) && Choice(user).SequenceEqual(best);
				if (!refusedOk) problems++;
			}
			SaveChoice(user, original);
			Refresh(user);
			int batMax = (l?.Batters ?? Array.Empty<int>()).Where(i => i != 0).Select(i => expected.GetValueOrDefault(i)).DefaultIfEmpty(0).Max();
			int pitMax = (l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0).Select(i => expected.GetValueOrDefault(i)).DefaultIfEmpty(0).Max();
			line = best.All(x => x == 0)
				? $"등록 점검: 받을 수 있는 팀컬러 없음 (빈 등록 {(applied ? "ok" : "FAIL")})"
				: $"등록 점검 {(problems == 0 ? "ok" : "FAIL")}: C {EntryName(best[0])}, H {EntryName(best[1])}, P {EntryName(best[2])} → 타자 +{batMax}, 투수 +{pitMax}; 어긋난 카드 {wrong}장; {shown}; 슬롯 틀린 등록 {(refusedOk ? "거절됨" : "FAIL")}";
			// 이름별 업적 grades: 단일 팀 올스타 S = B (user), 단일 년도 팀 S = S, its 타자진 A = B, 듀얼 팀 올스타 S = C; S also clears
			// the lower grades that exist (듀얼 팀 올스타 C slot has only S).
			int teamYear = Instance(Kind.SingleYearTeam, 0, 0, 825), teamAllStar = Instance(Kind.TeamAllStar, 0, 0, 8), dualAllStar = Instance(Kind.DualTeamAllStar, 0, 0, 708);
			bool gradesOk = Achievement(teamAllStar)?.grade == 'B' && Achievement(teamYear)?.grade == 'S' && Achievement(Instance(Kind.SingleYearTeam, 1, 1, 825))?.grade == 'B'
				&& Achievement(dualAllStar)?.grade == 'C' && WithLowerGrades(teamYear).Count() == 4 && WithLowerGrades(dualAllStar).Count() == 1
				&& Achievement(teamYear)?.name == "'25 삼성 S";
			if (!gradesOk) problems++;
			line += $"; 이름별 업적 등급 {(gradesOk ? "ok" : "FAIL")} ({Achievement(teamYear)?.name} {Achievement(teamYear)?.grade}, {Achievement(teamAllStar)?.name} {Achievement(teamAllStar)?.grade}, {Achievement(dualAllStar)?.name} {Achievement(dualAllStar)?.grade})";
			// Only the group's cards get an entry's bonus: 11 '25 롯데 batters and 3 others reach '25 롯데 타자진 A, which
			// lifts the 11 alone (user, 2026-10-24).
			List<CardInfo> lotte = s_catalog.All.Where(c => c.IsBatter && c.TeamCode == 7 && c.Year == 2025).Take(11).ToList();
			List<CardInfo> others = s_catalog.All.Where(c => c.IsBatter && c.TeamCode != 7).Take(3).ToList();
			bool memberOk = false;
			if (lotte.Count == 11 && others.Count == 3)
			{
				Lineup test = new Lineup();
				for (int i = 0; i < 14; i++) test.Batters[i] = (i < 11 ? lotte[i] : others[i - 11]).Idx;
				Dictionary<int, int> b = Bonuses(test, new[] { 0, Instance(Kind.SingleYearTeam, 1, 1, 725), 0 });
				memberOk = lotte.All(c => b.GetValueOrDefault(c.Idx) == Value(0, 1, 1)) && others.All(c => !b.ContainsKey(c.Idx));
			}
			if (!memberOk) problems++;
			line += $"; 조건 선수만 적용 {(memberOk ? "ok" : "FAIL")} ('25 롯데 타자진 A: 롯데 11명 +{Value(0, 1, 1)}, 다른 팀 3명 0)";
			// Lineages (user, 2026-11-05): 7 해태 and 7 KIA batters make one KIA·해태 team all-star of 14; the single-year kind
			// keeps the club.
			List<CardInfo> haitai = s_catalog.All.Where(c => c.IsBatter && c.TeamCode == 1).Take(7).ToList();
			List<CardInfo> kia = s_catalog.All.Where(c => c.IsBatter && c.TeamCode == 5).Take(7).ToList();
			bool lineOk = false;
			if (haitai.Count == 7 && kia.Count == 7)
			{
				List<CardInfo> both = haitai.Concat(kia).ToList();
				var teamAll = BestGroup(Kind.TeamAllStar, both);
				Lineup test = new Lineup();
				for (int i = 0; i < 14; i++) test.Batters[i] = both[i].Idx;
				Dictionary<int, int> b = Bonuses(test, new[] { 0, Instance(Kind.TeamAllStar, 1, 0, 5), 0 });
				lineOk = teamAll == (5, 14) && both.All(c => b.GetValueOrDefault(c.Idx) == Value(5, 1, 0)) && Lineage(16) == 6 && Lineage(9) == 9
					&& EntryName(Instance(Kind.TeamAllStar, 0, 0, 5)).StartsWith("KIA·해태 올스타") && BestGroup(Kind.SingleYearTeam, both).count <= 7;
			}
			if (!lineOk) problems++;
			line += $"; 계보 {(lineOk ? "ok" : "FAIL")} (해태 7 + KIA 7 → {EntryName(Instance(Kind.TeamAllStar, 1, 0, 5))} 14명)";
			// 레전드 (2026-11-08; made-up cards, the real ones are not in the tables yet): a 롯데 레전드 ('81, grade 5) joins the '25 롯데
			// group, a 해태 레전드 joins KIA's seasons by lineage but not 롯데's, and both count in a 연대 group of their line.
			bool legendOk = false;
			if (lotte.Count == 11 && kia.Count == 7)
			{
				CardInfo Legend(CardInfo from, int idx, int team) => new CardInfo { Idx = idx, IsBatter = true, Year = 1981, TeamCode = team, Grade = 5, Ball = 10, Name = "레전드", Stats = from.Stats, Defence = from.Defence };
				CardInfo lotteLegend = Legend(lotte[0], 800201, 7), haitaiLegend = Legend(kia[0], 800202, 1);
				var withLotte = BestGroup(Kind.SingleYearTeam, lotte.Append(lotteLegend).ToList());
				var notLotte = BestGroup(Kind.SingleYearTeam, lotte.Append(haitaiLegend).ToList());
				int kiaYear = kia.GroupBy(c => c.Year).OrderByDescending(g => g.Count()).First().Key;
				List<CardInfo> kiaYearCards = kia.Where(c => c.Year == kiaYear).ToList();
				var withHaitai = BestGroup(Kind.SingleYearTeam, kiaYearCards.Append(haitaiLegend).ToList());
				legendOk = withLotte == (725, 12) && notLotte == (725, 11)
					&& IsMember(Kind.SingleYearTeam, 725, lotteLegend) && !IsMember(Kind.SingleYearTeam, 725, haitaiLegend)
					&& withHaitai == (5 * 100 + kiaYear % 100, kiaYearCards.Count + 1) && IsMember(Kind.SingleYearTeam, 5 * 100 + kiaYear % 100, haitaiLegend)
					&& IsMember(Kind.DecadeTeam, 7 * 100 + 2, lotteLegend) && IsMember(Kind.DualYearTeam, 5 * 10000 + 909, haitaiLegend)
					&& BestGroup(Kind.TeamAllStar, lotte.Append(lotteLegend).ToList()) == (7, 12);
			}
			if (!legendOk) problems++;
			line += $"; 레전드 {(legendOk ? "ok" : "FAIL")} ('25 롯데 11 + 롯데 레전드 → 12, + 해태 레전드 → 11, 해태 레전드는 KIA 시즌에)";
			return problems;
		}

		// Dev (2026-11-09, `--tctest` on a copy): 자동 등록, the user's choice kept while it holds, the popup after a save and the
		// 빨간 점, on the test team (autotest@) with made-up 1군s: A = one club-year (14 batters, 11 pitchers), B = the same club's
		// batters from many years, C = batters of 14 clubs. Puts the team's lineup and registration back. Returns the problems.
		public static int AutoSelfTest(string dbPath)
		{
			uint user = 0;
			using (SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				object v = cmd.ExecuteScalar();
				if (v != null) user = (uint)Convert.ToInt64(v);
			}
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"팀컬러 자동 등록 {(ok ? "ok  " : "FAIL")} {what}");
			}
			if (user == 0)
			{
				Check(false, "no autotest team");
				return problems;
			}
			Lineup original = s_db.GetLineup(user);
			int[] choice0 = Choice(user);
			int manual0 = State(user, "manual"), fresh0 = State(user, "fresh");
			string Names(int[] r) => string.Join(" / ", r.Select(Short));
			try
			{
				List<CardInfo> real = s_catalog.All.Where(c => c.Year >= 1993 && c.Grade < 5).ToList();
				var clubYear = real.GroupBy(c => (c.TeamCode, c.Year)).Where(g => g.Count(c => c.IsBatter) >= 14 && g.Count(c => !c.IsBatter) >= 11)
					.OrderByDescending(g => g.Key.Year).First();
				List<CardInfo> bats = clubYear.Where(c => c.IsBatter).Take(14).ToList(), pits = clubYear.Where(c => !c.IsBatter).Take(11).ToList();
				Lineup Make(IList<CardInfo> b, IList<CardInfo> p)
				{
					Lineup l = new Lineup { Fielders = original.Fielders, NextSp = original.NextSp };
					for (int i = 0; i < 14; i++) l.Batters[i] = b[i].Idx;
					for (int i = 0; i < 11; i++) l.Pitchers[i] = p[i].Idx;
					return l;
				}
				int club = clubYear.Key.TeamCode;
				List<CardInfo> clubBats = real.Where(c => c.IsBatter && Lineage(c.TeamCode) == Lineage(club)).GroupBy(c => c.Year).Select(g => g.First()).Take(14).ToList();
				// At most two batters of a line and two of a year: no 타자진 entry reaches (the smallest needs six).
				List<CardInfo> mixedBats = new List<CardInfo>();
				foreach (CardInfo c in real.Where(c => c.IsBatter))
				{
					if (mixedBats.Count == 14) break;
					if (mixedBats.Count(m => Lineage(m.TeamCode) == Lineage(c.TeamCode)) < 2 && mixedBats.Count(m => m.Year == c.Year) < 2) mixedBats.Add(c);
				}
				Lineup a = Make(bats, pits), b = Make(clubBats, pits), mixed = Make(mixedBats, pits);
				ServerLog.Info($"팀컬러 자동 등록 1군 A = '{clubYear.Key.Year % 100:00} {TeamName(club, clubYear.Key.Year)} 14+11, B = 같은 계보 타자 {clubBats.Count}개 연도, C = 타자 {mixedBats.Count}명 여러 구단");

				// 1. Nothing registered: every slot takes the strongest; A reaches 단일 년도 팀 in all three.
				s_db.SaveLineup(user, a);
				SaveChoice(user, new int[3]);
				SetState(user, "manual", 0);
				SetState(user, "fresh", 0);
				bool noticeEmpty = Notice(user);
				Refresh(user);
				HashSet<int> availA = Available(a);
				int[] regA = Registered(user);
				Check(regA.All(i => i != 0) && Enumerable.Range(0, 3).All(slot => regA[slot] == Strongest(a, slot, availA)) && regA.All(i => Parse(i).Value.kind == Kind.SingleYearTeam),
					$"빈 등록 → 가장 강한 것: {Names(regA)}");
				Check(noticeEmpty && !Notice(user), $"빨간 점: 빈 칸이고 쓸 수 있는 것 있음 {noticeEmpty}, 자동 등록 뒤 {Notice(user)}");

				// 2. The user picks a weaker 타자진 entry (단일 팀 올스타): kept as theirs, also through another Refresh.
				int weaker = availA.Where(i => Parse(i)?.slot == 1 && Parse(i).Value.kind == Kind.TeamAllStar).FirstOrDefault();
				bool applied = weaker != 0 && TryApply(user, new[] { regA[0], weaker, regA[2] });
				Refresh(user);
				int[] regManual = Registered(user);
				Check(applied && State(user, "manual") == 2 && regManual[1] == weaker && regManual[0] == regA[0],
					$"직접 고른 타자진 {Short(weaker)}: manual {State(user, "manual")}, 다시 계산해도 {Short(regManual[1])}");

				// 3. B: the same line's batters of many years. The 단일 팀 올스타 타자진 still holds (kept); C changes by itself.
				int[] before = Registered(user);
				s_db.SaveLineup(user, b);
				Refresh(user);
				(Ma9MPacket.PKS_SC_POPUP popupB, int[] gainedB) = AfterSave(user, a, before);
				int[] regB = Registered(user);
				string textB = popupB?.Messages.array[1].str ?? "-";
				Check(regB[1] != 0 && Parse(regB[1]).Value.kind == Kind.TeamAllStar && (State(user, "manual") & 2) != 0 && popupB != null,
					$"B: 타자진 직접 고른 것 유지 {Short(regB[1])}, 전체 {Short(regB[0])}; 팝업 \"{popupB?.Messages.array[0].str}\" {textB.Replace("\n", " | ")}");

				// 4. C: batters of many clubs. The kept choice breaks and the slot goes back to automatic.
				before = regB;
				s_db.SaveLineup(user, mixed);
				Refresh(user);
				(Ma9MPacket.PKS_SC_POPUP popupC, int[] gainedC) = AfterSave(user, b, before);
				int[] regC = Registered(user);
				HashSet<int> availC = Available(mixed);
				List<string> linesC = (popupC?.Messages.array[1].str ?? string.Empty).Split('\n').ToList();
				Check((State(user, "manual") & 2) == 0 && regC[1] == Strongest(mixed, 1, availC) && popupC != null,
					$"C: 깨진 타자진 → 자동 {Short(regC[1])}, manual {State(user, "manual")}; 팝업 \"{popupC?.Messages.array[0].str}\" {string.Join(" | ", linesC)}");
				List<string> allLines = linesC.Concat(textB.Split('\n')).ToList();
				Check(linesC.Count <= 5 && allLines.All(line => PopupText.Px(line) <= PopupText.Width),
					$"팝업 줄 {linesC.Count}줄, 가장 넓은 줄 {allLines.Max(PopupText.Px):F0} px (한도 {PopupText.Width})");

				// 5. Back to A: entries new to the 1군 that are not registered light the dot until the screen is opened.
				before = regC;
				s_db.SaveLineup(user, a);
				Refresh(user);
				(Ma9MPacket.PKS_SC_POPUP popupA, int[] gainedA) = AfterSave(user, mixed, before);
				bool lit = Notice(user);
				SetState(user, "fresh", 0);
				Check(popupA != null && gainedA.Length == 0 && lit && !Notice(user),
					$"C → A: 등록이 바뀌어 서버 팝업 \"{popupA?.Messages.array[0].str}\"(앱의 새 팀컬러 팝업 없음), 등록 안 된 새 팀컬러로 빨간 점 {lit}, 화면을 열면 {Notice(user)}");
				// The same save with the registration as it was: no server popup, the app's own popup gets the new entries.
				(Ma9MPacket.PKS_SC_POPUP same, int[] gainedSame) = AfterSave(user, mixed, Registered(user));
				SetState(user, "fresh", 0);
				Check(same == null && gainedSame.Length > 0 && !gainedSame.Intersect(Registered(user)).Any(),
					$"등록 그대로인 저장: 서버 팝업 없음, 앱의 새 팀컬러 팝업에 {gainedSame.Length}개(예: {Short(gainedSame.FirstOrDefault())})");

				// 7. (2026-11-12) 연대 단일 팀's own steps (25 S +7, 22 A +5, 19 B +4, 14 C +2, 13 none) and the dual kinds' each-team share
				// (25 = 15 + 10 S; 16 + 9 not S but A; 24 + 1 none), on made-up whole-1군 lists (slot C).
				{
					int dk = Array.FindIndex(Kinds, x => x.kind == Kind.DecadeTeam), sk = Array.FindIndex(Kinds, x => x.kind == Kind.SingleYearDualTeam);
					var decade = real.GroupBy(c => (line: Lineage(c.TeamCode), dec: c.Year / 10)).Where(g => g.Count() >= 25).OrderByDescending(g => g.Key.dec).First();
					// One card per club-year of other lines and decades, batters and pitchers both (no group of theirs reaches anything).
					List<CardInfo> outsiders = real.Where(c => Lineage(c.TeamCode) != decade.Key.line && c.Year / 10 != decade.Key.dec)
						.GroupBy(c => (c.TeamCode, c.Year, c.IsBatter)).Select(g => g.First()).Take(80).ToList();
					List<CardInfo> Mix(int members) => decade.Take(members).Concat(outsiders.Take(25 - members)).ToList();
					string Step(int members) { var r = Reach(dk, 0, Mix(members)); return r.grade < 0 ? "-" : $"{GradeNames[r.grade]} +{Value(dk, 0, r.grade)}"; }
					string steps = string.Join(" · ", new[] { 25, 24, 22, 21, 19, 18, 14, 13 }.Select(m => $"{m}명 {Step(m)}"));
					Check(steps == "25명 S +7 · 24명 A +5 · 22명 A +5 · 21명 B +4 · 19명 B +4 · 18명 C +2 · 14명 C +2 · 13명 -", $"연대 단일 팀 단계: {steps}");
					var season = real.GroupBy(c => c.Year).Select(y => (y.Key, teams: y.GroupBy(c => c.TeamCode).Where(t => t.Count() >= 24).Select(t => t.ToList()).Take(2).ToList()))
						.Where(x => x.teams.Count == 2).OrderByDescending(x => x.Key).FirstOrDefault();
					if (season.teams == null)
					{
						Check(false, "듀얼 팀 시험: 24장 넘는 두 구단이 있는 시즌이 없음");
					}
					else
					{
						List<CardInfo> Pair(int a) => season.teams[0].Take(a).Concat(season.teams[1].Take(25 - a)).ToList();
						string Dual(int a) { var r = Reach(sk, 0, Pair(a)); return r.grade < 0 ? "-" : GradeNames[r.grade]; }
						string duals = string.Join(" · ", new[] { 15, 16, 20, 24 }.Select(a => $"{a}+{25 - a} {Dual(a)}"));
						Check(duals == "15+10 S · 16+9 A · 20+5 C · 24+1 -", $"듀얼 팀 팀마다 40%: '{season.Key % 100:00} {duals}");
					}
					// Hints: a third of the members of the shown grade (9 of 25 for 연대 S's next step … 5 of the lowest C 14).
					// The outsiders' pitchers come on their own (2026-11-13: the catalog lists batters first, so the 80 above had none
					// and the test stopped here).
					// One card per other line and decade on each side, so no other 연대 group gets near a hint (two at most).
					List<CardInfo> Others(bool bat) => real.Where(c => c.IsBatter == bat && Lineage(c.TeamCode) != decade.Key.line && c.Year / 10 != decade.Key.dec)
						.GroupBy(c => (Lineage(c.TeamCode), c.Year / 10)).Select(g => g.First()).ToList();
					List<CardInfo> otherBats = Others(true), otherArms = Others(false).Take(11).ToList();
					bool DecadeHint(int members)
					{
						Lineup few = Make(decade.Where(c => c.IsBatter).Take(members).Concat(otherBats).Take(14).ToList(), otherArms);
						return Hints(few).Any(i => Parse(i)?.kind == Kind.DecadeTeam && i / 10_000_000 % 10 == 0);
					}
					int decadeBats = decade.Count(c => c.IsBatter);
					bool hint3 = DecadeHint(3), hint4 = DecadeHint(4), hint5 = decadeBats >= 5 && DecadeHint(5);
					Check(!hint3 && !hint4 && (hint5 || decadeBats < 5),
						$"전체 칸 연대 힌트 (C 14명의 1/3 = 5명부터): 3명 {(hint3 ? "보임" : "안 보임")} · 4명 {(hint4 ? "보임" : "안 보임")} · 5명 {(hint5 ? "보임" : decadeBats < 5 ? "(연대 타자 모자람)" : "안 보임")}");
				}

				// 6. 특성 CP: the dot when CP is left that a trait can take.
				int used = TeamPropertyHandlers.Points(user).Values.Sum(v => v), cp = TeamPropertyHandlers.AvailableCp(user);
				bool spend = TeamPropertyHandlers.CanSpend(user);
				Check(spend == (used < cp), $"특성 빨간 점: {used}/{cp} CP 사용 → {spend}");
			}
			finally
			{
				s_db.SaveLineup(user, original);
				SaveChoice(user, choice0);
				SetState(user, "manual", manual0);
				SetState(user, "fresh", fresh0);
				WriteBonuses(user);
			}
			ServerLog.Info($"팀컬러 자동 등록 점검: {problems} problem(s)");
			return problems;
		}

		// A line for the self-test: what a lineup qualifies for and the best total per batter/pitcher.
		public static string Describe(Lineup l)
		{
			HashSet<int> available = Available(l);
			int[] best = new int[3];
			for (int slot = 0; slot < 3; slot++)
			{
				best[slot] = available.Where(i => Parse(i)?.slot == slot).OrderByDescending(i => Value((int)Parse(i).Value.kind - 1, slot, Parse(i).Value.grade)).FirstOrDefault();
			}
			Dictionary<int, int> bonus = Bonuses(l, best);
			string Name(int i) => EntryName(i);
			int bat = (l?.Batters ?? Array.Empty<int>()).Where(i => i != 0).Select(i => bonus.GetValueOrDefault(i)).DefaultIfEmpty(0).Max();
			int pit = (l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0).Select(i => bonus.GetValueOrDefault(i)).DefaultIfEmpty(0).Max();
			return $"{available.Count} entries; best C {Name(best[0])}, H {Name(best[1])}, P {Name(best[2])} → batters +{bat}, pitchers +{pit}";
		}
	}
}
