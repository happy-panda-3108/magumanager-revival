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
		// for that season (table fa_market): TeamColorPicks FA cards that join an entry the 1군 has registered (else one it is gathering,
		// TeamColors.MarketEntries) — first those whose own card joins it too (the same club and season: "LG 올스타" → LG players), then
		// the jokers of the rule —, then RandomPicks of the rest at random. FA cards the team holds are left out; only the list signs.
		// (The user's idea for a real game, 11-17: every FA at random and the users bidding against each other — docs\나중에_쓸_아이디어.md.)
		public const int TeamColorPicks = 3, RandomPicks = 7;

		private static string s_cs;

		private static readonly object s_lock = new object();

		private const string SeasonFormat = "yyyy-MM-dd HH:mm";

		// AlbumHandlers.Register.
		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS fa_market (
				user_code INTEGER NOT NULL, cycle_start TEXT NOT NULL, cards TEXT NOT NULL, colour_picks INTEGER NOT NULL,
				entries TEXT NOT NULL, made TEXT NOT NULL, PRIMARY KEY (user_code, cycle_start))";
			cmd.ExecuteNonQuery();
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
			string season = LeagueCalendar.CycleStart(now).ToString(SeasonFormat, CultureInfo.InvariantCulture);
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
					cmd.CommandText = "INSERT OR IGNORE INTO fa_market (user_code, cycle_start, cards, colour_picks, entries, made) VALUES ($u, $s, $c, $n, $e, $m)";
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

		private static List<int> Load(uint user, string season)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
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
			List<CardInfo> free = RosterHandlers.Catalog.All.Where(c => c.IsFA && !owned.Contains(c.Idx)).ToList();
			int[] entries = TeamColors.MarketEntries(user);
			List<CardInfo> fit = free.Where(c => entries.Any(e => TeamColors.IsMemberOf(e, c))).ToList();
			List<CardInfo> natural = fit.Where(c => RosterHandlers.Catalog.Get(c.SourceIdx) is CardInfo s && !s.IsFA && entries.Any(e => TeamColors.IsMemberOf(e, s))).ToList();
			List<CardInfo> picks = Shuffled(natural).Take(TeamColorPicks).ToList();
			picks.AddRange(Shuffled(fit.Where(c => !picks.Contains(c)).ToList()).Take(TeamColorPicks - picks.Count));
			int colour = picks.Count;
			picks.AddRange(Shuffled(free.Where(c => !picks.Contains(c)).ToList()).Take(TeamColorPicks + RandomPicks - picks.Count));
			return (picks.Select(c => c.Idx).ToList(), colour, entries);
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
				// Renewals 1-3 and the fixed price after (ContractHandlers.FaCost; user 18:3x·18:46).
				$"연장 {ContractHandlers.FaExtendDays}일씩: {Man(ContractHandlers.FaCost(0, price, 0))} · {Man(ContractHandlers.FaCost(1, price, 0))} · {Man(ContractHandlers.FaCost(2, price, 0))} MP,",
				$"4번째부터 {Man(ContractHandlers.FaCost(ContractHandlers.FaRisingRenewals, price, 0))} MP · 연장 보너스 {ContractHandlers.FaBonusPerRenewal}점씩(처음 {ContractHandlers.BonusRenewals}번)",
				// AI testers 11-16 (all six): the first renewal right after signing showed 1억 1,220만 (14 days left, the early surcharge).
				$"남은 날이 {ContractHandlers.SurchargeAfter}일 이하일 때 연장해야 웃돈이 없습니다.",
				// 두산 42, 쌍방울 54, kt 69: the joker rule was nowhere in the window (TeamColors.IsMember). Since 11-17 the 1군's entry
				// it joins now, when it does (the market's 팀컬러 share).
				TeamColorLine(user, card),
				FirstTeamLine(user, card)
			};
			ServerLog.Info($"[{session.Id}] FA 영입 asked: {card.Year} {card.Name} ({card.Idx}) {price:N0} MP, has {mp:N0}; {lines[3]}");
			AdviceHandlers.Offer(session, v2, Title, string.Join("\n", lines.Where(l => !string.IsNullOrEmpty(l))), "영입", () => Sign(user, card.Idx));
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

		// One FA in the 1군 and the same player once (RosterHandlers.FaProblem): who stands in the way now.
		private static string FirstTeamLine(uint user, CardInfo fa)
		{
			Lineup l = RosterHandlers.Db.GetLineup(user);
			List<CardInfo> in1 = l == null ? new List<CardInfo>()
				: l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(i => RosterHandlers.Catalog.Get(i)).Where(c => c != null).ToList();
			CardInfo otherFa = in1.FirstOrDefault(c => c.IsFA);
			CardInfo source = RosterHandlers.Catalog.Get(fa.SourceIdx);
			bool ownsSource = source != null && !source.IsFA && RosterHandlers.Db.GetCards(user).Any(c => c.Idx == source.Idx);
			// The app's window wrapped the longer "지금 1군의 FA ○○ 선수와 바꿔야 합니다" line (11-16 screen check), so the name goes in brackets.
			string line = otherFa != null ? $"1군 FA는 한 명뿐입니다(지금 1군: {otherFa.Name})." : "1군에는 FA를 한 명만 넣을 수 있습니다.";
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

		// 영입 (checked again: the MP or the roster may have changed since the question).
		internal static string Sign(uint user, int idx)
		{
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			string why = Problem(user, card, out _);
			if (why != null)
			{
				return "!" + why;
			}
			int price = Price(card);
			RosterHandlers.Db.AddCards(user, new[] { idx }, ContractHandlers.FaStartDays);
			ContractHandlers.StartFa(user, idx, DateTime.Today);
			int after = RosterHandlers.Db.AddMp(user, -price, MpLedger.FaSigning);
			ServerLog.Info($"FA 영입: team {user} signed {card.Year} {card.Name} ({idx}, {card.Ball}볼) for {price:N0} MP, {after:N0} left");
			return $"{card.Name} 선수를 영입했습니다. (계약 {ContractHandlers.FaStartDays}일)\n로스터에서 1군에 넣을 수 있습니다.\n남은 MP {after:N0}";
		}
	}
}
