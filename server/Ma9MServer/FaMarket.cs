using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// FA 시장 (systems session, 2026-11-15; user 15:4x via the engine session: FA 선수카드 of the 8·9볼 레어 cards only, "FA 명단도 괜찮고,
	// FA시장도 나쁘지 않을 것 같음. 계약금은 좀 비싸게 받아서 영입하면 될 것 같은데", one in the 1군, any number owned, never in AI clubs):
	// the album's FA pages (등급별 > FA: every FA card; a season's FA club) list the cards to sign (AlbumHandlers.FaPage). A tap asks
	// "ma9m:v2:fa:<idx>" (the app's Ma9MAdvice.Request), the server answers with the 계약금 and an 영입 button (AdviceHandlers.Offer),
	// and 영입 adds the card at the usual contract (RosterHandlers.StartContract) for the 계약금 (MP 장부 "FA 영입").
	public static class FaMarket
	{
		public const string Request = "fa:";

		private const string Title = "FA 영입";

		// 계약금 by ball (user 2026-11-15 18:3x via the engine session: "1,500만, 3,000만으로 합시다. 약간 FA 영입 계약금을 기존 카드들
		// 예상가격보다 비싸게 한 이유는, 보통 FA 계약때 대형 계약을 맺으며 팀에 합류하기 때문"; offered 2,000만 / 4,000만 and the like at 16:1x).
		// The contract runs 14 days from signing and each renewal costs 10% of this more than the last (ContractHandlers.FaCost).
		public static readonly Dictionary<int, int> PriceByBall = new Dictionary<int, int> { [8] = 15_000_000, [9] = 30_000_000 };

		public static int Price(CardInfo c) => PriceByBall.TryGetValue(c.Ball, out int p) ? p : PriceByBall.Values.Max();

		private static readonly Dictionary<CardPos, string> PosNames = new Dictionary<CardPos, string>
		{
			[CardPos.C] = "포수", [CardPos.B1] = "1루수", [CardPos.B2] = "2루수", [CardPos.B3] = "3루수", [CardPos.SS] = "유격수",
			[CardPos.LF] = "좌익수", [CardPos.CF] = "중견수", [CardPos.RF] = "우익수", [CardPos.DH] = "지명타자",
			[CardPos.SP] = "선발", [CardPos.RP] = "중계", [CardPos.SU] = "셋업", [CardPos.CP] = "마무리", [CardPos.P] = "투수"
		};

		// "'09 KIA 김상현 · 3루수 · 9볼": the source card's club and season.
		public static string Label(CardInfo c) =>
			$"'{c.Year % 100:00} {TeamColors.TeamName(c.HomeTeam, c.Year)} {c.Name} · {PosNames.GetValueOrDefault(c.MainPosition, "선수")} · {c.Ball}볼";

		// "1,650만" (whole 만), else the full number.
		private static string Man(int mp) => mp % 10_000 == 0 ? $"{mp / 10_000:N0}만" : $"{mp:N0}";

		// The 전력강화 > FA 시장 list of a team (AlbumHandlers.MarketPage). User 2026-11-17 00:0x (asked at 11-16 18:3x: all at once, a list
		// drawn each season, or a drawn list with the team's own share kept): "시즌마다 바뀌는 명단에 우리 1군 팀컬러에 맞는 FA 3명, 전체 무작위
		// 7명 해서 총 10명으로 합시다". Drawn at the team's first look in a season of the common calendar (LeagueCalendar.CycleStart) and kept
		// for that season (table fa_market): TeamColorPicks FA cards at random among those that join an entry the 1군 has registered (else
		// one it is gathering, TeamColors.MarketEntries) — for a club all-star entry, which every FA joins, only the club's own players —,
		// then RandomPicks of the rest at random. Left out: FA cards the team holds, and players already in its 1군 (one card of a player
		// there, RosterHandlers.FaProblem). Only the list signs.
		// Rule 2 (11-17 01:3x, user "응 재시작하자"): rule 1 (00:44) took the players of the entry's own club and season first, and for a
		// single-year team those are mostly in the 1군 already (7 of the first 21 picks on the live server); its lists are drawn again.
		// (The user's idea for a real game, 11-17: every FA at random and the users bidding against each other — docs\나중에_쓸_아이디어.md.)
		// Rule 3 (user 11-17 02:0x: "역시 외국인 선수는 안 넣는게 맞는 것 같음. 국내선수들로만 나오게 해주고, 시존 3/7을 4/8로 변경합시다"):
		// domestic players only (CardInfo.Foreigner), 4 of the team colour and 8 at random, 12 in all; rule 1-2 lists are drawn again.
		public const int TeamColorPicks = 4, RandomPicks = 8;

		// FA cards a team may hold (user 11-17 07:4x: "영입은 4명까지 가능하고 1군에 사용가능한 인원을 2명으로 제한합시다. 영입에는 자유롭게
		// 하면서 사용인원을 유동적으로 변경할 수 있는걸로"): four held, two of them in the 1군 at a time (CardInfo.MaxFaInFirstTeam).
		public const int MaxHeld = 4;

		private const int Rule = 4;

		private static string s_cs;

		private static readonly object s_lock = new object();

		private const string SeasonFormat = "yyyy-MM-dd HH:mm";

		// AlbumHandlers.Register.
		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS fa_market (
					user_code INTEGER NOT NULL, cycle_start TEXT NOT NULL, cards TEXT NOT NULL, colour_picks INTEGER NOT NULL,
					entries TEXT NOT NULL, made TEXT NOT NULL, PRIMARY KEY (user_code, cycle_start))";
				cmd.ExecuteNonQuery();
			}
			// The rule a list was drawn by (rows before 11-17 01:3x are rule 1 and are drawn again).
			bool hasRule;
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('fa_market') WHERE name = 'rule'";
				hasRule = Convert.ToInt32(cmd.ExecuteScalar()) > 0;
			}
			if (!hasRule)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "ALTER TABLE fa_market ADD COLUMN rule INTEGER NOT NULL DEFAULT 1";
				cmd.ExecuteNonQuery();
			}
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		public static List<int> MarketList(uint user) => MarketList(user, DateTime.Now);

		internal static List<int> MarketList(uint user, DateTime now)
		{
			if (s_cs == null)
			{
				return RosterHandlers.Catalog.All.Where(c => c.IsFA).OrderBy(c => c.Idx).Select(c => c.Idx).ToList();
			}
			string season = ListPeriod(now).ToString(SeasonFormat, CultureInfo.InvariantCulture);
			lock (s_lock)
			{
				List<int> stored = Load(user, season);
				if (stored != null)
				{
					return stored;
				}
				(List<int> cards, int colour, int[] entries) = Draw(user);
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					// Only when the period has no list yet (Load); the rule drawn by is kept in the row.
					cmd.CommandText = "INSERT OR REPLACE INTO fa_market (user_code, cycle_start, cards, colour_picks, entries, made, rule) VALUES ($u, $s, $c, $n, $e, $m, $r)";
					cmd.Parameters.AddWithValue("$r", Rule);
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					cmd.Parameters.AddWithValue("$c", string.Join(",", cards));
					cmd.Parameters.AddWithValue("$n", colour);
					cmd.Parameters.AddWithValue("$e", string.Join(",", entries));
					cmd.Parameters.AddWithValue("$m", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
					cmd.ExecuteNonQuery();
				}
				ServerLog.Info($"FA 시장: team {user} season {season} list {string.Join(",", cards)} (팀컬러 몫 {colour}, entries {string.Join(",", entries.Select(TeamColors.ShortName))})");
				return Load(user, season) ?? cards;
			}
		}

		// The list's period (user 11-18 00:4x: "무조건 시즌이 바뀌기 전 오전 11시 및 오후 11시부터 명단이 초기화 되고, 다음 오후 11시 또는 오전
		// 11시가 되기 전까지 해당 명단을 유지하는걸로 해줘"; asked after the 11-17 23:59 restart redrew the season's lists for rule 4):
		// from the next season's 편성 (LeagueCalendar.FormAt, an hour before it starts — 11:00 or 23:00) to the following one, keyed
		// by that season's start. Kept whatever happens in between (a restart, a new rule: a new rule takes the next period).
		// FaNegotiation's 결렬 counts by the same period.
		public static DateTime ListPeriod(DateTime now) => LeagueCalendar.CycleStart(now + (LeagueCalendar.Cycle - LeagueCalendar.FormAt));

		private static List<int> Load(uint user, string season)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			// Any rule's list of the period stays (until 11-18 00:4x, a list of an older rule was drawn again at the next look).
			cmd.CommandText = "SELECT cards FROM fa_market WHERE user_code = $u AND cycle_start = $s";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			object v = cmd.ExecuteScalar();
			if (v == null || v is DBNull)
			{
				return null;
			}
			string text = (string)v;
			return text.Length == 0 ? new List<int>() : text.Split(',').Select(int.Parse).ToList();
		}

		private static (List<int> cards, int colour, int[] entries) Draw(uint user)
		{
			HashSet<int> owned = RosterHandlers.Db.GetCards(user).Select(c => c.Idx).ToHashSet();
			// The 1군's players (the same player as RosterHandlers.FaProblem: batter or pitcher and Sidx).
			Lineup l = RosterHandlers.Db.GetLineup(user);
			HashSet<(bool, int)> fielded = (l?.Batters ?? Array.Empty<int>()).Concat(l?.Pitchers ?? Array.Empty<int>()).Where(i => i != 0)
				.Select(i => RosterHandlers.Catalog.Get(i)).Where(c => c != null).Select(c => (c.IsBatter, c.Sidx)).ToHashSet();
			// 11-18 (AI tester 효율파 12회차: '19·'20 황재균 on one list): one card a player, and none of a player the team holds as an FA
			// already (two of one player cannot both play, RosterHandlers.FaProblem).
			HashSet<(bool, int)> heldFa = RosterHandlers.Db.GetCards(user).Select(c => RosterHandlers.Catalog.Get(c.Idx)).Where(c => c != null && c.IsFA)
				.Select(c => (c.IsBatter, c.Sidx)).ToHashSet();
			List<CardInfo> free = RosterHandlers.Catalog.All.Where(c => c.IsFA && !IsForeign(c) && !owned.Contains(c.Idx) && !fielded.Contains((c.IsBatter, c.Sidx))
				&& !heldFa.Contains((c.IsBatter, c.Sidx))).ToList();
			int[] entries = TeamColors.MarketEntries(user);
			// Rule 4 (user 11-17 21:1x, asked whether the share is of the main club or of the others: "01롯데가 메인이면, 01롯데를 강화하기
			// 위해 01롯데가 아닌 01년도의 랜덤 FA4장을 뽑아. 삼성올스타면 삼성올스타를 강화하기 위해 삼성이 아닌 FA선수 4명이 무작위로
			// 나오는거고, 10년대 두산이면 10년대 두산을 강화하기 위해 10년도 FA중 두산이 아닌 FA 4장이 랜덤으로 나오는거야"): the share
			// strengthens the entry with players of other clubs — FA cards that join it (its season rule, TeamColors.IsMemberOf) whose
			// own club is not the one the entry is named after (TeamColors.IsOwnClubOf). Rules 1-3 took the entry's own club first.
			// Of the entry's scope too (a batter for a 타자진 entry, a pitcher for a 투수진 one; rules 1-3 let a batter count for a 투수진).
			bool Fits(CardInfo c) => entries.Any(e => TeamColors.IsMemberOf(e, c) && TeamColors.InScopeOf(e, c)
				&& !(RosterHandlers.Catalog.Get(c.SourceIdx) is CardInfo s && !s.IsFA && TeamColors.IsOwnClubOf(e, s)));
			List<CardInfo> picks = OnePerPlayer(Shuffled(free.Where(Fits).ToList()), new HashSet<(bool, int)>()).Take(TeamColorPicks).ToList();
			int colour = picks.Count;
			HashSet<(bool, int)> taken = picks.Select(c => (c.IsBatter, c.Sidx)).ToHashSet();
			picks.AddRange(OnePerPlayer(Shuffled(free.Where(c => !picks.Contains(c)).ToList()), taken).Take(TeamColorPicks + RandomPicks - picks.Count));
			return (picks.Select(c => c.Idx).ToList(), colour, entries);
		}

		// 11-18 (user 14:1x: "FA 선수 명단에 외국인 선수들은 빼달라고 부탁을 했거든?? 그게 반영이 안 된 것 같아"): the card tables mark a
		// player foreign season by season and miss it in a few seasons ('06 브라운A, '19 소사, '17 로하스A, '07 서튼, '11 고든, '03 프랭클린);
		// of the FA cards '06 브라운A could be drawn. A player counts as foreign when any of his cards says so.
		private static HashSet<(bool, int)> s_foreignPlayers;

		internal static bool IsForeign(CardInfo c)
		{
			if (c.Foreigner)
			{
				return true;
			}
			HashSet<(bool, int)> set = s_foreignPlayers ??= RosterHandlers.Catalog.All.Where(x => x.Foreigner).Select(x => (x.IsBatter, x.Sidx)).ToHashSet();
			return set.Contains((c.IsBatter, c.Sidx));
		}

		// The first card of each player in the order given, past those in `taken`.
		private static IEnumerable<CardInfo> OnePerPlayer(List<CardInfo> cards, HashSet<(bool, int)> taken)
		{
			HashSet<(bool, int)> seen = new HashSet<(bool, int)>(taken);
			foreach (CardInfo c in cards)
			{
				if (seen.Add((c.IsBatter, c.Sidx)))
				{
					yield return c;
				}
			}
		}

		private static List<CardInfo> Shuffled(List<CardInfo> list)
		{
			List<CardInfo> copy = new List<CardInfo>(list);
			for (int i = copy.Count - 1; i > 0; i--)
			{
				int j = Random.Shared.Next(i + 1);
				(copy[i], copy[j]) = (copy[j], copy[i]);
			}
			return copy;
		}

		// The 팀컬러 line of the offer: the entry of the 1군 the card joins now, else the joker rule.
		private static string TeamColorLine(uint user, CardInfo fa)
		{
			int entry = TeamColors.Registered(user).FirstOrDefault(e => e != 0 && TeamColors.IsMemberOf(e, fa));
			if (entry != 0)
			{
				string line = $"팀컬러: 지금 1군의 {TeamColors.ShortName(entry)}에 듭니다.";
				return PopupText.Px(line) <= PopupText.Width ? line : "팀컬러: 지금 1군의 팀컬러에 듭니다.";
			}
			return "팀컬러: 구단 상관없이 같은 해·연대 묶음에 듭니다.";
		}

		// AdviceHandlers.TryHandle: arg = the FA card's idx.
		public static void Ask(GameSession session, uint user, string arg, bool v2)
		{
			CardInfo card = int.TryParse(arg, out int idx) ? RosterHandlers.Catalog?.Get(idx) : null;
			string why = Problem(user, card, out int mp);
			if (why != null)
			{
				ServerLog.Info($"[{session.Id}] FA 영입 {arg}: {why.Replace('\n', ' ')}");
				// A full roster or too little MP still shows what the player costs (AI tester NC 64, 11-16: "보관함이 차 있으면 FA 값조차
				// 볼 수 없다"); the type-1 window has room for five lines.
				bool known = card != null && card.IsFA && !RosterHandlers.Db.GetCards(user).Any(c => c.Idx == card.Idx);
				string cost = why.Contains("계약금") ? string.Empty : $"계약금 {Price(card):N0} MP · 계약 {ContractHandlers.FaStartDays}일\n";
				AdviceHandlers.Tell(session, Title, known ? $"{Label(card)}\n{cost}{why}" : why);
				return;
			}
			int price = Price(card);
			List<string> lines = new List<string>
			{
				Label(card),
				$"계약금 {price:N0} MP · 계약 {ContractHandlers.FaStartDays}일",
				$"가진 MP {mp:N0} → {mp - price:N0}",
				BallLine(user, card),
				// 11-17 21:1x (user: "(웃돈) << 이 단어 안 보이게 해줬으면 함. 너무 짜침. 불필요한 정보나 too much information은 도움말, 안내
				// 멘트, 등등에 안 나왔으면 함"): the renewal prices 1-3 and after, and the early-renewal line, are gone; a renewal is the
				// player's negotiation now (FaNegotiation), its price his.
				$"재계약: {ContractHandlers.SurchargeAfter}일 이하 남으면 선수와 협상, 보너스 {ContractHandlers.FaBonusPerRenewal}점씩",
				// 두산 42, 쌍방울 54, kt 69: the joker rule was nowhere in the window (TeamColors.IsMember). Since 11-17 the 1군's entry
				// it joins now, when it does (the market's 팀컬러 share).
				TeamColorLine(user, card),
				FirstTeamLine(user, card)
			};
			ServerLog.Info($"[{session.Id}] FA 영입 asked: {card.Year} {card.Name} ({card.Idx}) {price:N0} MP, has {mp:N0}; {lines[3]}");
			AdviceHandlers.Offer(session, v2, Title, string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l))), "영입", () =>
			{
				string done = Sign(user, card.Idx);
				// The top bar's MP (11-17 12:2x: it kept the old MP until the next lobby, and the next renewal's "계약금" line, counted from
				// it, came out too big).
				TopBar.Send(session);
				return done;
			});
		}

		// The 1군 ball line (AI testers LG 92, kt 68, 11-16: signed 니퍼트 / 고영표 for 3,000만 and found 143/140 and 148/140 in the
		// roster): with the 1군 of now and the cap the roster checks (RosterHandlers.MaxCostOf), the least balls the man the FA
		// replaces must have.
		private static string BallLine(uint user, CardInfo fa)
		{
			Lineup l = RosterHandlers.Db.GetLineup(user);
			int cap = RosterHandlers.MaxCostOf(user);
			if (l == null)
			{
				return $"1군 볼 상한 {cap}볼";
			}
			List<CardInfo> in1 = l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(i => RosterHandlers.Catalog.Get(i)).Where(c => c != null).ToList();
			int balls = in1.Sum(c => c.Ball);
			bool room = l.Batters.Concat(l.Pitchers).Any(i => i == 0);
			int need = fa.Ball - (cap - balls);
			string head = $"1군 볼 {balls}/{cap}: ";
			if (room && balls + fa.Ball <= cap)
			{
				return head + "빈자리에 넣어도 상한 안입니다.";
			}
			if (need <= 0)
			{
				return head + "누구와 바꿔 넣어도 상한 안입니다.";
			}
			return in1.Any(c => c.Ball >= need) ? head + $"{need}볼 이상 선수와 바꿔야 들어갑니다." : head + "지금 1군에는 볼 상한을 넘어 넣을 수 없습니다.";
		}

		// The negotiation screen's header line (user 11-19 21:3x, 11-18 question 2 — AI tester 두산 48: since 1.0.57 the FA 시장 goes straight
		// to the negotiation and the offer window's 볼·팀컬러·1군 FA lines were gone; 두산 signed at 142/140): "화면 위에 한 줄로 영입 전 안내".
		// The 1군 balls with whether he fits, the 1군 FA places, the team colour he joins — short enough for the header (FaNegotiation.SendTurn).
		internal static string BriefLine(uint user, CardInfo fa)
		{
			Lineup l = RosterHandlers.Db.GetLineup(user);
			int cap = RosterHandlers.MaxCostOf(user);
			List<CardInfo> in1 = l == null ? new List<CardInfo>()
				: l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(i => RosterHandlers.Catalog.Get(i)).Where(c => c != null).ToList();
			int balls = in1.Sum(c => c.Ball);
			bool room = l != null && l.Batters.Concat(l.Pitchers).Any(i => i == 0);
			int need = fa.Ball - (cap - balls);
			string fit = room && balls + fa.Ball <= cap ? "넣어도 상한 안"
				: need <= 0 ? "바꿔도 상한 안"
				: in1.Any(c => c.Ball >= need) ? $"{need}볼 이상과 교체"
				// (tester_1119f screen, 11-19 22:0x: "상한 넘음" did not say what it means for the player)
				: "지금 1군에 못 넣음";
			int fas = in1.Count(c => c.IsFA);
			int entry = TeamColors.Registered(user).FirstOrDefault(e => e != 0 && TeamColors.IsMemberOf(e, fa));
			string colour = entry != 0 ? $"팀컬러 {TeamColors.ShortName(entry)}" : "팀컬러 해·연대만";
			string ballPart = $"1군 볼 {balls}/{cap}({fit})";
			// The header has about 430 of the 800 design's width at 13 between the title and the turns: about 460 in PopupText.Px.
			foreach (string line in new[]
			{
				$"{ballPart} · 1군 FA {fas}/{CardInfo.MaxFaInFirstTeam} · {colour}",
				$"{ballPart} · FA {fas}/{CardInfo.MaxFaInFirstTeam} · {colour}",
				$"{ballPart} · 1군 FA {fas}/{CardInfo.MaxFaInFirstTeam}"
			})
			{
				if (PopupText.Px(line) <= 460)
				{
					return line;
				}
			}
			return ballPart;
		}

		// One FA in the 1군 and the same player once (RosterHandlers.FaProblem): who stands in the way now.
		private static string FirstTeamLine(uint user, CardInfo fa)
		{
			Lineup l = RosterHandlers.Db.GetLineup(user);
			List<CardInfo> in1 = l == null ? new List<CardInfo>()
				: l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(i => RosterHandlers.Catalog.Get(i)).Where(c => c != null).ToList();
			List<CardInfo> fas = in1.Where(c => c.IsFA).ToList();
			CardInfo source = RosterHandlers.Catalog.Get(fa.SourceIdx);
			bool ownsSource = source != null && !source.IsFA && RosterHandlers.Db.GetCards(user).Any(c => c.Idx == source.Idx);
			// The app's window wrapped the longer "지금 1군의 FA ○○ 선수와 바꿔야 합니다" line (11-16 screen check), so the names go in brackets.
			// Two since 11-17 07:3x (CardInfo.MaxFaInFirstTeam).
			string line = fas.Count >= CardInfo.MaxFaInFirstTeam
				? $"1군 FA는 {CardInfo.MaxFaInFirstTeam}명까지입니다(지금 1군: {string.Join("·", fas.Select(c => c.Name))})."
				: fas.Count > 0 ? $"1군에 FA를 {CardInfo.MaxFaInFirstTeam - fas.Count}명 더 넣을 수 있습니다(지금 1군: {string.Join("·", fas.Select(c => c.Name))})."
				: $"1군에는 FA를 {CardInfo.MaxFaInFirstTeam}명까지 넣을 수 있습니다.";
			if (PopupText.Px(line) > PopupText.Width)
			{
				line = fas.Count >= CardInfo.MaxFaInFirstTeam ? $"1군 FA는 {CardInfo.MaxFaInFirstTeam}명까지입니다(지금 {fas.Count}명)." : $"1군에 FA를 {CardInfo.MaxFaInFirstTeam - fas.Count}명 더 넣을 수 있습니다.";
			}
			return ownsSource ? line + "\n가진 원래 카드와는 함께 1군에 넣을 수 없습니다." : line;
		}

		// Why the team cannot sign the card now (null when it can); mp = the team's MP.
		internal static string Problem(uint user, CardInfo card, out int mp)
		{
			mp = RosterHandlers.Db.AddMp(user, 0);
			if (card == null || !card.IsFA)
			{
				return "FA 시장에 없는 선수입니다.";
			}
			// Rule 3 (11-17): foreign players are not offered (a held one stays).
			if (IsForeign(card))
			{
				return "외국인 선수는 FA 시장에 나오지 않습니다.";
			}
			List<TeamCard> cards = RosterHandlers.Db.GetCards(user);
			if (cards.Any(c => c.Idx == card.Idx))
			{
				return $"{card.Name} 선수는 이미 영입했습니다.";
			}
			// Only this season's list signs (11-17; the 1.0.53 app still asks from the album).
			if (!MarketList(user).Contains(card.Idx))
			{
				return "이번 시즌 FA 시장 명단에 없는 선수입니다.\n전력강화 > FA 시장에서 이번 시즌 명단을 보세요.";
			}
			// At most MaxHeld FA cards a team (user 11-17 02:0x "팀별 FA영입 가능 선수는 2명으로 합시다.(투, 타 구분 없이 총 2명)"); one let go
			// (방출) makes room again. Teams holding more from before keep them.
			List<CardInfo> held = cards.Select(c => RosterHandlers.Catalog.Get(c.Idx)).Where(c => c != null && c.IsFA).ToList();
			if (held.Count >= MaxHeld)
			{
				return $"FA 선수는 {MaxHeld}명까지 영입할 수 있습니다.\n(지금 {held.Count}명: {string.Join("·", held.Take(3).Select(c => c.Name))}{(held.Count > 3 ? " 외" : string.Empty)})";
			}
			int cap = RosterHandlers.MaxPossessOf(user);
			if (cards.Count >= cap)
			{
				return $"선수단이 가득 찼습니다. ({cards.Count}/{cap}명)\n상점에서 로스터를 확장하거나 선수를 정리해 주세요.";
			}
			int price = Price(card);
			if (mp < price)
			{
				return $"MP가 부족합니다.\n계약금 {price:N0} MP · 가진 MP {mp:N0}";
			}
			return null;
		}

		// 2026-11-20 (AI tester 두산 16회차, 남은일 0-5: "보관함이 꽉 차 있으면 선수를 고르고 FA 영입을 눌러야 비로소 '가득 찼습니다'가 나옴 …
		// FA 시장 입장이나 카드 고르기 때 미리 안내"): on entering the market (AlbumHandlers.Enter, the market page) with a full roster, one
		// window says so and where room is made — not again for 30 minutes (the page is asked again after each signing and visit), and
		// not when the team may sign no more FAs anyway (MaxHeld; [FA 영입] says that one).
		private static readonly Dictionary<uint, DateTime> s_fullTold = new Dictionary<uint, DateTime>();

		internal static void FullRosterNotice(GameSession session, uint user)
		{
			List<TeamCard> cards = RosterHandlers.Db.GetCards(user);
			int cap = RosterHandlers.MaxPossessOf(user);
			if (cards.Count < cap || cards.Count(c => RosterHandlers.Catalog.Get(c.Idx)?.IsFA == true) >= MaxHeld)
			{
				return;
			}
			lock (s_fullTold)
			{
				if (s_fullTold.TryGetValue(user, out DateTime at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(30))
				{
					return;
				}
				s_fullTold[user] = DateTime.UtcNow;
			}
			AdviceHandlers.Tell(session, "FA 시장", $"선수 보관함이 가득 찼습니다({cards.Count}/{cap}명).\nFA를 영입하려면 먼저 자리를 1칸 만들어 주세요.\n(계약관리 > 선수방출, 또는 상점 > 로스터 확장)");
		}

		// 영입 (checked again: the MP or the roster may have changed since the question).
		internal static string Sign(uint user, int idx) => SignWith(user, idx, -1, ContractHandlers.FaStartDays);

		// A signing at the terms of a negotiation (FaNegotiation, 2026-11-17): its 계약금 (−1 = the list price) and days. The MP check
		// is against that price (a deal may be under or over the list).
		internal static string SignWith(uint user, int idx, int price, int days)
		{
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			if (card != null && price < 0)
			{
				price = Price(card);
			}
			string why = Problem(user, card, out int mp);
			if (why != null && !(why.StartsWith("MP가 부족", StringComparison.Ordinal) && mp >= price))
			{
				return "!" + why;
			}
			if (mp < price)
			{
				return $"!MP가 부족합니다.\n계약금 {price:N0} MP · 가진 MP {mp:N0}";
			}
			RosterHandlers.Db.AddCards(user, new[] { idx }, (byte)Math.Clamp(days, 1, byte.MaxValue));
			ContractHandlers.StartFa(user, idx, DateTime.Today, days);
			int after = RosterHandlers.Db.AddMp(user, -price, MpLedger.FaSigning);
			// Its picture in our club's uniform (FaArt, 2026-11-17).
			FaArt.Signed(user, idx);
			ServerLog.Info($"FA 영입: team {user} signed {card.Year} {card.Name} ({idx}, {card.Ball}볼) for {price:N0} MP, {days} days, {after:N0} left");
			return $"{card.Name} 선수를 영입했습니다. (계약 {days}일)\n로스터에서 1군에 넣을 수 있습니다.\n남은 MP {after:N0}";
		}
	}
}
