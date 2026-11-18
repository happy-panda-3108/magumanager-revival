using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// FA 에이전트 협상 (systems session, 2026-11-17; user 09:3x: "에이전트 협상으로 게이지 채우는 것 마음에 듭니다. 정가의 +-20~30%도 괜찮은 것 같고,
	// 선수 만족 시 컨디션 최상 및 계약일수가 달라질 수 있는 것도 마음에 듭니다. 그리고 계약일자 만료되어서 추가 계약을 해야할 경우 … 다시 FA가 되어서
	// 재계약 협상하는 시스템이 더 재밌을 것 같으니 구현합시다"). Design: docs\FA협상_설계.md.
	// The app (ClientMark) asks "ma9m:v2:fan:<idx>:<action>" (AdviceHandlers.TryHandle) and gets POPUP types TurnType / DealType /
	// DoneType (types 0-2, 9-11, 20-21 are taken). A negotiation lives in memory for ten minutes; a struck deal too, until the
	// signing (영입: action sign → FaMarket.SignWith; 재계약: the 계약관리 renewal, ContractHandlers.TryExtend → TakeRenewalDeal).
	// A negotiation broken off (gauge under 0) is kept in fa_nego_fail for the season.
	public static class FaNegotiation
	{
		public const string Request = "fan:";

		public const string ClientMark = "ma9m-fan1";

		public const byte TurnType = 30, DealType = 31, DoneType = 33;

		public static bool On(GameSession session) => session?.ClientVersion?.Contains(ClientMark) == true;

		// The player's bent (by the player, so he always wants the same). 11-17 12:2x (user: "에이전트는 선수를 대변하는 역할이고, 구단과
		// 협상을 해야하는걸로 알고 있음, 에이전트 자리에 차라리 구단 엠블렘이 차지하고 조건을 선수가 제시하는게 좋아보임 … 선수 입장에서 말하는 멘트로"):
		// no agent any more; the player speaks for himself and the choices are the club's own lines.
		private enum Bent { Money, Win, Play, Loyal }

		private static readonly string[] BentNames = { "돈을 중시", "우승을 원함", "주전을 원함", "옛 동료를 아낌" };

		// The condition cards, each once a negotiation.
		private enum Term { Starter, Title, Mates, Long }

		private static readonly string[] TermNames = { "주전 자리를 약속하겠습니다", "우리는 우승에 도전하는 팀입니다", "옛 동료들이 함께 뜁니다", "계약 기간을 늘려 드리겠습니다 (+2시즌)" };

		public const int Turns = 4, StartGauge = 40, RenewalGauge = 55, TopGauge = 110, MinPct = 75, MaxPct = 130;

		// 1시즌 = 리그 한 바퀴 2.5일. Every FA contract starts at 4시즌(10일); the negotiation's events change it: the 계약 기간 card adds 2시즌
		// (6시즌 15일), the raises and cuts move the 계약금 (user 11-17 15:xx via the engine session: "FA선수 계약기간은 기본 4시즌(10일)에
		// 협상 중 계약 기간 추가, 계약금 추가, 감소 등이 이벤트로 일어나는거야 내가 설계한 내용 반영된거 맞지??"; until then a negotiated
		// contract was 6시즌 and the long one 8). 정가로 바로 계약 and 미리 연장 stay 4시즌 (10:1x "정가 계약 6시즌 -> 4시즌 10일이 맞음").
		public const int Seasons = 4, LongSeasons = 6, QuickSeasons = 4;

		// 크게 만족 (gauge TopGauge+ at the deal): 최상 for this long from the signing, without a 컨디션 회복제 slot (user 11-17 10:1x: "그냥
		// 시작 컨디션이 최상인 것 … 후자의 경우 fa선수 외에도 5명의 컨디션을 회복시켜줄 수 있는거라 꽤 다름. 난 후자의 경우를 더 선호해").
		public const int TopHours = 24;

		public static int DaysOf(int seasons) => (int)Math.Round(seasons * 2.5, MidpointRounding.AwayFromZero);

		private sealed class Talk
		{
			public int Idx;
			public bool Renewal;
			public Bent Bent;
			public int Gauge;
			public int Pct = 100;
			public int Turn;
			public bool Long;
			public bool Promise;
			public int Base;
			public string Agent;
			public bool Last;
			public readonly HashSet<Term> Used = new HashSet<Term>();
			public DateTime At = DateTime.UtcNow;
		}

		public sealed class Deal
		{
			public int Idx;
			public bool Renewal;
			public int Price;
			public int Days;
			public bool Top;
			public bool Promise;
			public DateTime At = DateTime.UtcNow;
		}

		private static readonly Dictionary<uint, Talk> s_talks = new Dictionary<uint, Talk>();

		private static readonly Dictionary<(uint, int), Deal> s_deals = new Dictionary<(uint, int), Deal>();

		private static readonly object s_lock = new object();

		private static string s_cs;

		private const string SeasonFormat = "yyyy-MM-dd HH:mm";

		// AlbumHandlers.Register.
		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS fa_nego_fail (
				user_code INTEGER NOT NULL, card_idx INTEGER NOT NULL, cycle_start TEXT NOT NULL, at TEXT NOT NULL,
				PRIMARY KEY (user_code, card_idx, cycle_start));
				CREATE TABLE IF NOT EXISTS fa_top (
				user_code INTEGER NOT NULL, card_idx INTEGER NOT NULL, until TEXT NOT NULL,
				PRIMARY KEY (user_code, card_idx))";
			cmd.ExecuteNonQuery();
		}

		// ---- 크게 만족: 최상 for TopHours (Condition.Level asks TopAt; its own lock and cache, read from the league's thread too) ----

		private static readonly object s_topLock = new object();

		private static readonly Dictionary<uint, (DateTime loaded, Dictionary<int, DateTime> until)> s_top = new Dictionary<uint, (DateTime, Dictionary<int, DateTime>)>();

		public static bool TopAt(uint user, int idx, DateTime at)
		{
			if (s_cs == null)
			{
				return false;
			}
			lock (s_topLock)
			{
				if (!s_top.TryGetValue(user, out var entry) || (DateTime.Now - entry.loaded).TotalMinutes > 1)
				{
					entry = (DateTime.Now, LoadTop(user));
					s_top[user] = entry;
				}
				return entry.until.TryGetValue(idx, out DateTime until) && at < until && at >= until.AddHours(-TopHours);
			}
		}

		private static Dictionary<int, DateTime> LoadTop(uint user)
		{
			Dictionary<int, DateTime> map = new Dictionary<int, DateTime>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, until FROM fa_top WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				map[r.GetInt32(0)] = DateTime.Parse(r.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
			}
			return map;
		}

		// At a signing or renewal: 최상 from now (top), or none (a card signed again without it loses an old one).
		private static void SetTop(uint user, int idx, bool top)
		{
			DateTime until = DateTime.Now.AddHours(TopHours);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = top
					? "INSERT INTO fa_top (user_code, card_idx, until) VALUES ($u, $i, $t) ON CONFLICT(user_code, card_idx) DO UPDATE SET until = excluded.until"
					: "DELETE FROM fa_top WHERE user_code = $u AND card_idx = $i";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$i", idx);
				cmd.Parameters.AddWithValue("$t", until.ToString("o", CultureInfo.InvariantCulture));
				cmd.ExecuteNonQuery();
			}
			lock (s_topLock)
			{
				s_top.Remove(user);
			}
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// The FA 시장 list's period (FaMarket.ListPeriod, 11-18): a 결렬 holds until the next list.
		private static string Season(DateTime now) => FaMarket.ListPeriod(now).ToString(SeasonFormat, CultureInfo.InvariantCulture);

		private static bool BrokenOff(uint user, int idx)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM fa_nego_fail WHERE user_code = $u AND card_idx = $i AND cycle_start = $s";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			cmd.Parameters.AddWithValue("$s", Season(DateTime.Now));
			return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
		}

		private static void RecordBreak(uint user, int idx)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR IGNORE INTO fa_nego_fail (user_code, card_idx, cycle_start, at) VALUES ($u, $i, $s, $a)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			cmd.Parameters.AddWithValue("$s", Season(DateTime.Now));
			cmd.Parameters.AddWithValue("$a", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
			cmd.ExecuteNonQuery();
		}

		// AdviceHandlers.TryHandle: arg = "<idx>:<action>", action start · c0..c3 · quick · accept · quit · sign.
		public static void Handle(GameSession session, uint user, string arg)
		{
			string[] p = (arg ?? string.Empty).Split(':');
			if (p.Length < 2 || !int.TryParse(p[0], out int idx))
			{
				Done(session, "FA 협상", "알 수 없는 요청입니다.", false);
				return;
			}
			string action = p[1];
			lock (s_lock)
			{
				Sweep();
				switch (action)
				{
				case "start":
					Start(session, user, idx);
					break;
				case "quick":
					Quick(session, user, idx);
					break;
				case "accept":
					Accept(session, user, idx);
					break;
				case "quit":
					s_talks.Remove(user);
					ServerLog.Info($"[{session.Id}] FA 협상 {idx}: 그만둠");
					break;
				case "sign":
					Sign(session, user, idx);
					break;
				default:
					if (action.Length == 2 && action[0] == 'c' && char.IsDigit(action[1]))
					{
						Choose(session, user, idx, action[1] - '0');
					}
					else
					{
						Done(session, "FA 협상", "알 수 없는 요청입니다.", false);
					}
					break;
				}
			}
		}

		private static void Sweep()
		{
			DateTime old = DateTime.UtcNow.AddMinutes(-10);
			foreach (uint u in s_talks.Where(kv => kv.Value.At < old).Select(kv => kv.Key).ToList())
			{
				s_talks.Remove(u);
			}
			foreach ((uint, int) k in s_deals.Where(kv => kv.Value.At < old).Select(kv => kv.Key).ToList())
			{
				s_deals.Remove(k);
			}
		}

		// The renewal of a held FA card, or why not; basePrice = its renewal now (the 계약관리 price: with the early surcharge when more than
		// SurchargeAfter days are left), early = more than SurchargeAfter days left (미리 연장, no negotiation).
		private static string RenewalProblem(uint user, CardInfo card, out int basePrice, out bool early, out int daysLeft)
		{
			basePrice = 0;
			early = false;
			daysLeft = 0;
			TeamCard held = RosterHandlers.Db.GetCards(user).FirstOrDefault(c => c.Idx == card.Idx);
			if (held == null)
			{
				return "가진 선수가 아닙니다.";
			}
			daysLeft = held.RemainContract;
			if (held.RemainContract >= ContractHandlers.MaxDays)
			{
				return $"계약이 이미 {ContractHandlers.MaxDays}일입니다.";
			}
			early = held.RemainContract > ContractHandlers.SurchargeAfter;
			basePrice = ContractHandlers.FaCost(held.ContractCount, FaMarket.Price(card), held.RemainContract);
			return null;
		}

		// 미리 연장 (user 11-17 11:5x: "미리 연장하는 기능도 만듭시다. 기존 FA계약 시스템 사용에 대화만 조금 수정하면 될 것 같음"): with more than
		// SurchargeAfter days left the agent does not negotiate; he offers the 계약관리 renewal at once (its price with the early surcharge,
		// ContractHandlers.FaCost) for the 정가 contract's 4시즌(10일), and the app goes straight to [재계약하기].
		private static void Early(GameSession session, uint user, CardInfo card, int price, int daysLeft)
		{
			int days = DaysOf(QuickSeasons);
			Deal deal = new Deal { Idx = card.Idx, Renewal = true, Price = price, Days = days };
			s_deals[(user, card.Idx)] = deal;
			int list = price;
			TeamCard held = RosterHandlers.Db.GetCards(user).FirstOrDefault(c => c.Idx == card.Idx);
			if (held != null)
			{
				list = ContractHandlers.FaCost(held.ContractCount, FaMarket.Price(card), 0);
			}
			// 11-17 21:1x (user: "(웃돈) << 이 단어 안 보이게 해줬으면 함. 너무 짜침"): the price is said, the word is not.
			string line = $"아직 계약이 {daysLeft}일 남았는데 벌써 연장하자고요?\n그럼 {QuickSeasons}시즌({days}일), {Man(price)} MP면 서명하겠습니다.";
			ServerLog.Info($"[{session.Id}] FA 미리 연장 제안: {card.Year} {card.Name} ({card.Idx}), {daysLeft}일 남음, {price:N0} MP (웃돈 없이 {list:N0}), {days}일");
			Bent bent = (Bent)(((card.Sidx * 31) + card.Year) & 3);
			SendDeal(session, user, deal, card, line, list, 100, QuickSeasons, $"{Speaker(card)} · {BentNames[(int)bent]}");
		}

		private static void Start(GameSession session, uint user, int idx)
		{
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			if (card == null || !card.IsFA)
			{
				Done(session, "FA 협상", "FA 선수가 아닙니다.", false);
				return;
			}
			bool renewal = RosterHandlers.Db.GetCards(user).Any(c => c.Idx == idx);
			int basePrice;
			bool early = false;
			int daysLeft = 0;
			if (renewal)
			{
				string why = RenewalProblem(user, card, out basePrice, out early, out daysLeft);
				if (why != null)
				{
					Done(session, "FA 재계약 협상", why, false);
					return;
				}
			}
			else
			{
				string why = FaMarket.Problem(user, card, out _);
				if (why != null)
				{
					Done(session, "FA 협상", $"{FaMarket.Label(card)}\n{why}", false);
					return;
				}
				basePrice = FaMarket.Price(card);
			}
			if (BrokenOff(user, idx))
			{
				Done(session, renewal ? "FA 재계약 협상" : "FA 협상", $"{card.Name} 선수와는 이번 시즌 협상이 결렬됐습니다.\n다음 시즌 명단에서 다시 만나면 영입할 수 있습니다.", false);
				return;
			}
			if (early)
			{
				s_talks.Remove(user);
				Early(session, user, card, basePrice, daysLeft);
				return;
			}
			Talk t = new Talk
			{
				Idx = idx,
				Renewal = renewal,
				Bent = (Bent)(((card.Sidx * 31) + card.Year) & 3),
				Gauge = renewal ? RenewalGauge : StartGauge,
				Base = basePrice,
				Agent = Speaker(card)
			};
			// The agent asks above the list price: a money agent the most. Low enough that two raises still count (up to MaxPct), so any
			// agent can end very satisfied (TopGauge) when the move it likes most comes last.
			t.Pct = t.Bent == Bent.Money ? 115 : 110;
			s_talks[user] = t;
			string open = renewal
				? $"이 팀에서 뛴 정은 있지만, 이제 저도 다시 FA입니다.\n{Seasons}시즌 보장에 계약금 {Man(PriceOf(t))} MP라면 재계약하겠습니다."
				: t.Bent switch
				{
					Bent.Money => $"제 몸값은 제가 압니다.\n{Seasons}시즌 보장에 계약금 {Man(PriceOf(t))} MP는 받아야겠습니다.",
					Bent.Win => $"저는 우승할 수 있는 팀에서 뛰고 싶습니다.\n{Seasons}시즌, {Man(PriceOf(t))} MP라면 생각해 보겠습니다.",
					Bent.Play => $"저는 경기에 나가야 합니다. 주전을 약속해 주시겠습니까?\n조건은 {Seasons}시즌, {Man(PriceOf(t))} MP입니다.",
					_ => $"옛 동료들이 있는 팀이라면 마음이 움직일 것 같습니다.\n{Seasons}시즌, {Man(PriceOf(t))} MP를 원합니다."
				};
			ServerLog.Info($"[{session.Id}] FA {(renewal ? "재계약 " : string.Empty)}협상 시작: {card.Year} {card.Name} ({idx}), {BentNames[(int)t.Bent]}, 기준 {basePrice:N0} MP");
			SendTurn(session, t, card, open, 0);
		}

		private static void Choose(GameSession session, uint user, int idx, int choice)
		{
			if (!s_talks.TryGetValue(user, out Talk t) || t.Idx != idx || t.Last)
			{
				Done(session, "FA 협상", "협상이 끝났거나 시간이 지났습니다.\n다시 시작해 주세요.", false);
				return;
			}
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			t.At = DateTime.UtcNow;
			List<(string label, Func<(int delta, string line)> act)> choices = Choices(user, t, card);
			if (choice < 0 || choice >= choices.Count || choices[choice].act == null)
			{
				SendTurn(session, t, card, "무슨 말씀이신지 잘 모르겠네요.", 0);
				return;
			}
			(int delta, string line) = choices[choice].act();
			t.Gauge += delta;
			t.Turn++;
			ServerLog.Info($"[{session.Id}] FA 협상 {card.Name}: {choices[choice].label} → {delta:+0;-0} (게이지 {t.Gauge}, {t.Pct}%, {(t.Long ? LongSeasons : Seasons)}시즌)");
			if (t.Gauge < 0)
			{
				s_talks.Remove(user);
				RecordBreak(user, idx);
				Done(session, t.Renewal ? "FA 재계약 협상" : "FA 협상", $"{line}\n\n협상이 결렬됐습니다. 이번 시즌에는 {card.Name} 선수와 다시 만날 수 없습니다.", false);
				return;
			}
			if (t.Gauge >= 100)
			{
				Strike(session, user, t, card, line);
				return;
			}
			if (t.Turn >= Turns)
			{
				// The last offer: what is left of the gauge in price (5%p a 10 points short), within the bounds.
				t.Last = true;
				t.Pct = Math.Min(MaxPct, t.Pct + (int)Math.Ceiling((100 - t.Gauge) / 10.0) * 5);
				SendTurn(session, t, card, $"{line}\n좋아요, 제 마지막 조건입니다. {(t.Long ? LongSeasons : Seasons)}시즌, {Man(PriceOf(t))} MP. 이 이상은 양보 못 합니다.", delta);
				return;
			}
			SendTurn(session, t, card, line, delta);
		}

		// The four answers of a turn: raise, two unused condition cards, lower (empty labels hide a button).
		private static List<(string label, Func<(int, string)> act)> Choices(uint user, Talk t, CardInfo card)
		{
			List<(string, Func<(int, string)>)> list = new List<(string, Func<(int, string)>)>();
			list.Add(("계약금을 올려 드리겠습니다 (+10%)", () =>
			{
				if (t.Pct >= MaxPct)
				{
					return (5, "돈은 그만하면 됐습니다. 다른 이야기를 해 보죠.");
				}
				t.Pct = Math.Min(MaxPct, t.Pct + 10);
				int d = t.Bent == Bent.Money ? 30 : 20;
				return (d, t.Bent == Bent.Money ? "역시 돈 이야기가 제일 빠르네요." : "성의는 알겠습니다.");
			}));
			foreach (Term term in Enum.GetValues(typeof(Term)).Cast<Term>().Where(x => !t.Used.Contains(x)).Take(2))
			{
				Term x = term;
				list.Add((TermNames[(int)x], () => { t.Used.Add(x); return TermEffect(user, t, card, x); }));
			}
			while (list.Count < 3)
			{
				list.Add((string.Empty, null));
			}
			list.Add(("그 계약금은 구단 사정상 어렵습니다 (−10%)", () =>
			{
				if (t.Pct <= MinPct)
				{
					return (-10, "더는 못 깎습니다. 기분만 상하네요.");
				}
				t.Pct = Math.Max(MinPct, t.Pct - 10);
				int d = t.Bent == Bent.Money ? -25 : -15;
				return (d, t.Bent == Bent.Money ? "지금 장난하시는 겁니까? 그 돈이면 다른 구단에 가겠습니다." : "조금 섭섭하네요.");
			}));
			return list;
		}

		private static (int, string) TermEffect(uint user, Talk t, CardInfo card, Term term)
		{
			switch (term)
			{
			case Term.Starter:
				t.Promise = true;
				return t.Bent == Bent.Play ? (35, "주전이라면 이야기가 달라지죠. 정말 기쁩니다!") : (10, "주전이야 당연히 그래야죠.");
			case Term.Title:
			{
				LeagueState s = LeagueService.Store?.Load(user);
				double strength = 0.3;
				string where = "지금 리그";
				if (s != null)
				{
					int top = Math.Max(1, LeagueRules.TierNames.Length - 1);
					int n = Math.Max(2, s.Clubs.Count);
					int rank = Math.Max(1, s.RankOf(s.MyIndex));
					strength = 0.6 * Math.Clamp(s.Tier, 0, top) / top + 0.4 * (1.0 - (rank - 1) / (double)(n - 1));
					where = $"{LeagueRules.TierNames[Math.Clamp(s.Tier, 0, LeagueRules.TierNames.Length - 1)]} {rank}위";
				}
				int d = t.Bent == Bent.Win ? 10 + (int)Math.Round(25 * strength) : 5 + (int)Math.Round(5 * strength);
				string line = strength >= 0.6 ? $"{where} 구단이라… 우승 반지가 보이네요." : strength >= 0.35 ? $"{where}라면 해 볼 만하겠네요." : $"{where}에서 우승이요? 글쎄요.";
				return (d, line);
			}
			case Term.Mates:
			{
				Lineup l = RosterHandlers.Db.GetLineup(user);
				CardInfo src = RosterHandlers.Catalog.Get(card.SourceIdx);
				int club = src?.TeamCode ?? card.HomeTeam;
				int mates = l == null ? 0 : l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(i => RosterHandlers.Catalog.Get(i))
					.Count(c => c != null && !c.IsFA && c.Year == card.Year && c.TeamCode == club && c.Sidx != card.Sidx);
				int d = t.Bent == Bent.Loyal ? (mates == 0 ? -5 : Math.Min(30, 6 * mates)) : Math.Min(10, 2 * mates);
				string line = mates == 0 ? "같이 뛰던 얼굴이 하나도 없네요." : $"{mates}명이나 같이 뛰던 동료가 있다니 반갑네요.";
				return (d, line);
			}
			default:
				t.Long = true;
				return (15, $"{LongSeasons}시즌({DaysOf(LongSeasons)}일) 보장이라면 안심하고 뛸 수 있겠네요.");
			}
		}

		private static int PriceOf(Talk t) => (int)((long)t.Base * t.Pct / 100 / 10_000 * 10_000);

		private static void Strike(GameSession session, uint user, Talk t, CardInfo card, string line)
		{
			s_talks.Remove(user);
			int seasons = t.Long ? LongSeasons : Seasons;
			Deal deal = new Deal { Idx = t.Idx, Renewal = t.Renewal, Price = PriceOf(t), Days = DaysOf(seasons), Top = t.Gauge >= TopGauge, Promise = t.Promise };
			s_deals[(user, t.Idx)] = deal;
			ServerLog.Info($"[{session.Id}] FA {(t.Renewal ? "재계약 " : string.Empty)}협상 성사: {card.Name} {seasons}시즌({deal.Days}일) {deal.Price:N0} MP (기준 {t.Base:N0}, {t.Pct}%), 게이지 {t.Gauge}{(deal.Top ? ", 컨디션 최상" : string.Empty)}");
			SendDeal(session, user, deal, card, $"{line}\n좋습니다. {seasons}시즌({deal.Days}일), {Man(deal.Price)} MP에 서명하겠습니다.", t.Base, t.Gauge, seasons);
		}

		// The last offer taken.
		private static void Accept(GameSession session, uint user, int idx)
		{
			if (!s_talks.TryGetValue(user, out Talk t) || t.Idx != idx || !t.Last)
			{
				Done(session, "FA 협상", "협상이 끝났거나 시간이 지났습니다.", false);
				return;
			}
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			t.Gauge = Math.Max(t.Gauge, 100);
			Strike(session, user, t, card, "좋습니다. 그 조건으로 하죠.");
		}

		// 정가로 바로 계약: the list price, 4시즌, no condition change.
		private static void Quick(GameSession session, uint user, int idx)
		{
			s_talks.Remove(user);
			CardInfo card = RosterHandlers.Catalog.Get(idx);
			if (card == null || !card.IsFA)
			{
				Done(session, "FA 협상", "FA 선수가 아닙니다.", false);
				return;
			}
			bool renewal = RosterHandlers.Db.GetCards(user).Any(c => c.Idx == idx);
			int basePrice = 0;
			string why = renewal ? RenewalProblem(user, card, out basePrice, out _, out _) : FaMarket.Problem(user, card, out _);
			if (!renewal)
			{
				basePrice = FaMarket.Price(card);
			}
			if (why != null)
			{
				Done(session, renewal ? "FA 재계약 협상" : "FA 협상", why, false);
				return;
			}
			// 4시즌(10일) (the old app's 영입·연장 stay ContractHandlers.FaStartDays / FaExtendDays).
			int days = DaysOf(QuickSeasons);
			Deal deal = new Deal { Idx = idx, Renewal = renewal, Price = basePrice, Days = days };
			s_deals[(user, idx)] = deal;
			SendDeal(session, user, deal, card, $"정가로 바로 하시겠다고요? 좋습니다. {QuickSeasons}시즌({days}일), {Man(basePrice)} MP에 서명하겠습니다.", basePrice, 100, QuickSeasons);
		}

		// 영입 서명 (after the app's signing scene): the deal's price and days.
		private static void Sign(GameSession session, uint user, int idx)
		{
			if (!s_deals.TryGetValue((user, idx), out Deal deal) || deal.Renewal)
			{
				Done(session, "FA 영입", "서명할 계약이 없습니다. 협상을 다시 해 주세요.", false);
				return;
			}
			s_deals.Remove((user, idx));
			string text = FaMarket.SignWith(user, idx, deal.Price, deal.Days);
			bool ok = !text.StartsWith("!", StringComparison.Ordinal);
			if (ok)
			{
				SetTop(user, idx, deal.Top);
				// The top bar's MP (tester_1117g, 11-17 12:2x: it stayed at the old MP, and the next 미리 연장's "계약금 N MP" line, which the
				// app counts from it, showed 5,790만 for a 4,290만 renewal).
				TopBar.Send(session);
			}
			if (ok && deal.Top)
			{
				text += "\n" + TopLine;
			}
			if (ok)
			{
				CardInfo card = RosterHandlers.Catalog.Get(idx);
				NewsFeed.Add($"{session.Team?.TeamName ?? "한 구단"}, FA {Short(card)}{Wa(card?.Name)} {(int)Math.Round(deal.Days / 2.5)}시즌 {Man(deal.Price)} MP 계약");
			}
			Done(session, "FA 영입", ok ? text : text.Substring(1), ok);
		}

		// ContractHandlers.TryExtend: the negotiated renewal of this card, taken (null when none).
		public static Deal TakeRenewalDeal(uint user, int idx)
		{
			lock (s_lock)
			{
				if (s_deals.TryGetValue((user, idx), out Deal d) && d.Renewal && d.At > DateTime.UtcNow.AddMinutes(-10))
				{
					s_deals.Remove((user, idx));
					return d;
				}
				return null;
			}
		}

		public static Deal PeekRenewalDeal(uint user, int idx)
		{
			lock (s_lock)
			{
				return s_deals.TryGetValue((user, idx), out Deal d) && d.Renewal && d.At > DateTime.UtcNow.AddMinutes(-10) ? d : null;
			}
		}

		public static bool HasRenewalDeal(uint user, int idx)
		{
			lock (s_lock)
			{
				return s_deals.TryGetValue((user, idx), out Deal d) && d.Renewal && d.At > DateTime.UtcNow.AddMinutes(-10);
			}
		}

		// ContractHandlers.TryExtend after a negotiated renewal: the top condition and the news; the line for the reply (null: none).
		public static string AfterRenewal(uint user, string teamName, CardInfo card, Deal deal)
		{
			SetTop(user, card.Idx, deal.Top);
			string line = deal.Top ? TopLine : null;
			NewsFeed.Add($"{teamName ?? NewsFeed.TeamName(user) ?? "한 구단"}, FA {Short(card)}{Wa(card?.Name)} {(int)Math.Round(deal.Days / 2.5)}시즌 {Man(deal.Price)} MP 재계약");
			return line;
		}

		private static readonly string TopLine = $"선수가 크게 만족해 {TopHours}시간 동안 컨디션 최상으로 뜁니다.\n(컨디션 회복제 칸은 쓰지 않습니다)";

		// Who speaks: the player himself (11-17 12:2x; until then a made-up agent). The app shows only the bent part since 1.0.56.
		private static string Speaker(CardInfo card) => card == null ? "선수" : card.Name + " 선수";

		private static string Short(CardInfo c) => c == null ? "선수" : $"'{c.Year % 100:00} {c.Name}";

		// 와/과 after a name (a final consonant → 과; names ending in a letter or digit read as 와).
		private static string Wa(string name)
		{
			char last = string.IsNullOrEmpty(name) ? '수' : name[name.Length - 1];
			return last >= '가' && last <= '힣' && (last - '가') % 28 != 0 ? "과" : "와";
		}

		private static string Man(int mp) => mp % 10_000 == 0 ? $"{mp / 10_000:N0}만" : $"{mp:N0}";

		// [title, line, gauge, terms, c0, c1, c2, c3, agent, turns, idx, state, delta, renewal]
		private static void SendTurn(GameSession session, Talk t, CardInfo card, string line, int delta)
		{
			int seasons = t.Long ? LongSeasons : Seasons;
			List<string> labels = t.Last
				? new List<string> { "그 조건으로 하죠", string.Empty, string.Empty, "이번에는 그만두죠" }
				: Choices(session.Team.UserCode, t, card).Select(c => c.label).ToList();
			string[] m =
			{
				t.Renewal ? "FA 재계약 협상" : "FA 협상",
				line,
				t.Gauge.ToString(CultureInfo.InvariantCulture),
				$"지금 조건: {seasons}시즌({DaysOf(seasons)}일) · {Man(PriceOf(t))} MP (기준 {Man(t.Base)})",
				labels[0], labels[1], labels[2], labels[3],
				$"{t.Agent} · {BentNames[(int)t.Bent]}",
				t.Last ? "마지막 제안" : $"남은 대화 {Turns - t.Turn}번",
				t.Idx.ToString(CultureInfo.InvariantCulture),
				t.Last ? "last" : "turn",
				delta.ToString(CultureInfo.InvariantCulture),
				t.Renewal ? "1" : "0"
			};
			Send(session, TurnType, m);
		}

		// [title, line, idx, name, fromClub, seasons, days, price, list, gauge, top, renewal, toClub, toEmblem, agent · bent]
		// (agent · bent since 11-17 12:xx for a deal that opens the screen, 미리 연장; apps up to 1.0.55 read the first 14.)
		private static void SendDeal(GameSession session, uint user, Deal deal, CardInfo card, string line, int list, int gauge, int seasons, string agent = null)
		{
			CardInfo src = RosterHandlers.Catalog.Get(card.SourceIdx);
			string from = TeamColors.TeamName(src?.TeamCode ?? card.HomeTeam, card.Year);
			string[] m =
			{
				deal.Renewal ? "FA 재계약" : "FA 계약",
				line,
				card.Idx.ToString(CultureInfo.InvariantCulture),
				card.Name,
				from,
				seasons.ToString(CultureInfo.InvariantCulture),
				deal.Days.ToString(CultureInfo.InvariantCulture),
				deal.Price.ToString(CultureInfo.InvariantCulture),
				list.ToString(CultureInfo.InvariantCulture),
				gauge.ToString(CultureInfo.InvariantCulture),
				deal.Top ? "1" : "0",
				deal.Renewal ? "1" : "0",
				session.Team?.TeamName ?? string.Empty,
				(session.Team?.Emblem ?? 0).ToString(CultureInfo.InvariantCulture),
				agent ?? string.Empty
			};
			Send(session, DealType, m);
		}

		// [title, text, ok]
		private static void Done(GameSession session, string title, string text, bool ok) => Send(session, DoneType, new[] { title, text, ok ? "1" : "0" });

		private static void Send(GameSession session, byte type, string[] m)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = type;
			popup.Messages.array = m.Select(s => new PLongString { str = s ?? string.Empty }).ToArray();
			session.Send(popup);
		}
	}
}
