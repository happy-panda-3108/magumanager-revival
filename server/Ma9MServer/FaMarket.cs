using System;
using System.Collections.Generic;
using System.Linq;

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

		// AdviceHandlers.TryHandle: arg = the FA card's idx.
		public static void Ask(GameSession session, uint user, string arg, bool v2)
		{
			CardInfo card = int.TryParse(arg, out int idx) ? RosterHandlers.Catalog?.Get(idx) : null;
			string why = Problem(user, card, out int mp);
			if (why != null)
			{
				ServerLog.Info($"[{session.Id}] FA 영입 {arg}: {why.Replace('\n', ' ')}");
				AdviceHandlers.Tell(session, Title, why);
				return;
			}
			int price = Price(card);
			List<string> lines = new List<string>
			{
				Label(card),
				$"계약금 {price:N0} MP · 계약 {ContractHandlers.FaStartDays}일",
				$"가진 MP {mp:N0} → {mp - price:N0}",
				// Renewals 1-3 and the fixed price after (ContractHandlers.FaCost; user 18:3x·18:46).
				$"연장 {ContractHandlers.FaExtendDays}일씩: {Man(ContractHandlers.FaCost(0, price, 0))} · {Man(ContractHandlers.FaCost(1, price, 0))} · {Man(ContractHandlers.FaCost(2, price, 0))} MP,",
				$"4번째부터 {Man(ContractHandlers.FaCost(ContractHandlers.FaRisingRenewals, price, 0))} MP (남은 날이 {ContractHandlers.SurchargeAfter}일을 넘으면 웃돈)",
				"FA 선수는 1군에 한 명만 넣을 수 있습니다."
			};
			CardInfo source = RosterHandlers.Catalog.Get(card.SourceIdx);
			if (source != null && !source.IsFA && RosterHandlers.Db.GetCards(user).Any(c => c.Idx == source.Idx))
			{
				lines.Add("가진 원래 카드와는 함께 1군에 넣을 수 없습니다.");
			}
			ServerLog.Info($"[{session.Id}] FA 영입 asked: {card.Year} {card.Name} ({card.Idx}) {price:N0} MP, has {mp:N0}");
			AdviceHandlers.Offer(session, v2, Title, string.Join("\n", lines), "영입", () => Sign(user, card.Idx));
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
