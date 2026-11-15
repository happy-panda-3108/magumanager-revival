using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// "--fatest" (systems session, 2026-11-15): the FA 선수카드 rules on tables holding the FA rows (tools/scripts/fa_cards.py) in a
	// data copy; signs a card for the test team (autotest@), so a copy only. The cards, the 1군 limits, lineups the server builds, the
	// 팀컬러 wildcard, the album pages and the FA 시장 (MP, 장부, refusals).
	public static class FaSelfTest
	{
		private static int s_problems;

		private static void Check(bool ok, string what)
		{
			if (!ok) s_problems++;
			ServerLog.Info($"[fatest] {(ok ? "ok  " : "FAIL")} {what}");
		}

		public static int Run(CardCatalog catalog, Database db, string dbPath)
		{
			s_problems = 0;
			List<CardInfo> fa = catalog.All.Where(c => c.IsFA).OrderBy(c => c.Idx).ToList();
			// 1. The cards: copies of 8·9볼 레어 cards, idx 7,000,000 + the source, same season, player and stats, out of the draws.
			int bad = fa.Count(c =>
			{
				CardInfo s = catalog.Get(c.SourceIdx);
				return s == null || s.IsFA || c.Idx != CardInfo.FaIdxBase + s.Idx || s.Grade != 3 || (s.Ball != 8 && s.Ball != 9) || c.Ball != s.Ball
					|| c.Year != s.Year || c.Sidx != s.Sidx || c.IsBatter != s.IsBatter || !c.Stats.SequenceEqual(s.Stats) || c.HomeTeam != s.TeamCode;
			});
			int rares = catalog.All.Count(c => !c.IsFA && c.Grade == 3 && (c.Ball == 8 || c.Ball == 9));
			Check(fa.Count > 0 && fa.Count == rares && bad == 0 && !catalog.Drawable.Any(c => c.IsFA),
				$"{fa.Count} FA cards ({fa.Count(c => c.Ball == 8)} 8볼, {fa.Count(c => c.Ball == 9)} 9볼) for {rares} 레어 8·9볼 cards, {bad} unlike their source, none drawable");
			if (fa.Count < 4) return 1;
			CardInfo faBat = fa.First(c => c.IsBatter), faBat2 = fa.Where(c => c.IsBatter && c.Sidx != faBat.Sidx).OrderByDescending(c => c.Ball).First();
			CardInfo faArm = fa.First(c => !c.IsBatter);
			CardInfo src = catalog.Get(faBat.SourceIdx);
			CardInfo other = catalog.Drawable.First(c => c.IsBatter && c.Sidx != faBat.Sidx && c.Sidx != faBat2.Sidx);

			// 2. The 1군: one FA card, one card of a player; the batter step looks at the batters alone.
			Lineup Of(params CardInfo[] cards)
			{
				Lineup l = new Lineup();
				int b = 0, p = 0;
				foreach (CardInfo c in cards)
				{
					if (c.IsBatter) l.Batters[b++] = c.Idx;
					else l.Pitchers[p++] = c.Idx;
				}
				return l;
			}
			string two = RosterHandlers.FaProblem(Of(faBat, faArm));
			string same = RosterHandlers.FaProblem(Of(src, faBat));
			Check(RosterHandlers.FaProblem(Of(faBat, other)) == null && two != null && !two.StartsWith(RosterHandlers.SamePlayerHead)
					&& RosterHandlers.FaProblem(Of(faBat, faArm), battersOnly: true) == null
					&& same != null && same.StartsWith(RosterHandlers.SamePlayerHead) && RosterHandlers.FaProblem(Of(faBat, faBat2)) != null,
				$"1군: one FA ok; FA batter + FA pitcher refused ({two?.Replace('\n', ' ')}), but not at the batter step; FA + its source refused ({same?.Replace('\n', ' ')}); two FA batters refused");
			List<int> kept = RosterHandlers.LegendLimited(new[] { src.Idx, faBat.Idx, faBat2.Idx, faArm.Idx, other.Idx }).ToList();
			Check(kept.Contains(src.Idx) && !kept.Contains(faBat.Idx) && kept.Count(i => catalog.Get(i).IsFA) == 1 && kept.Contains(other.Idx),
				$"server-built lineups: of {src.Name} the original kept, his FA copy left out; one FA of the other two kept ({string.Join(", ", kept.Select(i => catalog.Get(i).Name + (catalog.Get(i).IsFA ? "(FA)" : "")))})");

			// 3. 팀컬러 (user 19:4x: "해당 년도 세트덱을 사용할 때 해당 년도의 FA선수를 쓸 수 있고, 20년대 롯데 << 이런 세트덱에는 20년대 FA 다 사용
			// 가능 … 년도 조건을 타는 조커 카드다"): an FA card of the deck's season in place of a member keeps the single-year S and gets
			// the bonus; one of another season does not, nor another club's card. The decade takes its decade's FA, the season pair
			// either season's, the year all-stars the season's, the team all-stars any.
			var club = catalog.Drawable.GroupBy(c => (c.Year, c.TeamCode)).Where(g => g.Count(c => c.IsBatter) >= 14 && g.Count(c => !c.IsBatter) >= 11
					&& fa.Any(f => f.IsBatter && f.Year == g.Key.Year && g.All(x => x.Sidx != f.Sidx)) && fa.Any(f => f.IsBatter && f.Year / 10 == g.Key.Year / 10 && f.Year != g.Key.Year))
				.OrderByDescending(g => g.Key.Year).First();
			int cy = club.Key.Year;
			List<CardInfo> bats = club.Where(c => c.IsBatter).Take(14).ToList(), arms = club.Where(c => !c.IsBatter).Take(11).ToList();
			bool HasS(List<CardInfo> b, List<CardInfo> p) => TeamColors.Available(Of(b.Concat(p).ToArray()))
				.Any(i => i / 100_000_000 == 1 && i / 10_000_000 % 10 == 0 && i / 1_000_000 % 10 == 0);
			CardInfo stranger = catalog.Drawable.First(c => c.IsBatter && c.TeamCode != club.Key.TeamCode && c.Year != cy && bats.All(x => x.Sidx != c.Sidx));
			CardInfo faIn = fa.First(c => c.IsBatter && c.Year == cy && club.All(x => x.Sidx != c.Sidx));
			CardInfo faDecade = fa.First(c => c.IsBatter && c.Year / 10 == cy / 10 && c.Year != cy);
			CardInfo faFar = fa.First(c => c.IsBatter && c.Year / 10 != cy / 10);
			List<CardInfo> withFa = bats.Take(13).Append(faIn).ToList(), withStranger = bats.Take(13).Append(stranger).ToList();
			List<CardInfo> withDecadeFa = bats.Take(13).Append(faDecade).ToList(), withFarFa = bats.Take(13).Append(faFar).ToList();
			int entry = TeamColors.Available(Of(withFa.Concat(arms).ToArray())).FirstOrDefault(i => i / 100_000_000 == 1 && i / 10_000_000 % 10 == 0);
			Check(HasS(bats, arms) && HasS(withFa, arms) && !HasS(withDecadeFa, arms) && !HasS(withStranger, arms) && entry != 0
					&& TeamColors.IsMemberOf(entry, faIn) && !TeamColors.IsMemberOf(entry, faDecade)
					&& TeamColors.Bonuses(Of(withFa.Concat(arms).ToArray()), new[] { entry, 0, 0 }).ContainsKey(faIn.Idx),
				$"팀컬러 단일 년도: '{cy % 100:00} {TeamColors.TeamName(club.Key.TeamCode, cy)} 25 S; one batter → '{faIn.Year % 100:00} FA {faIn.Name} still S and the FA gets the bonus; → '{faDecade.Year % 100:00} FA {faDecade.Name} no S; → {stranger.Name} of another club, no S");
			int line = TeamColors.Lineage(club.Key.TeamCode), dec = cy / 10 % 100, yy = cy % 100;
			int y2 = faDecade.Year % 100, pair = line * 10000 + Math.Min(yy, y2) * 100 + Math.Max(yy, y2);
			var decadeWith = TeamColors.BestGroupForTest(4, withDecadeFa);
			var decadeFar = TeamColors.BestGroupForTest(4, withFarFa);
			var teamAll = TeamColors.BestGroupForTest(6, withFarFa);
			var single = TeamColors.BestGroupForTest(1, withFa);
			Check(decadeWith == (line * 100 + dec, 14) && decadeFar.count == 13 && teamAll == (line, 14) && single == (club.Key.TeamCode * 100 + yy, 14)
					&& TeamColors.JoinsForTest(4, line * 100 + dec, faDecade) && !TeamColors.JoinsForTest(4, line * 100 + dec, faFar)
					&& TeamColors.JoinsForTest(3, pair, faDecade) && TeamColors.JoinsForTest(3, pair, faIn) && !TeamColors.JoinsForTest(3, pair, faFar)
					&& TeamColors.JoinsForTest(5, yy, faIn) && !TeamColors.JoinsForTest(5, yy, faDecade)
					&& TeamColors.JoinsForTest(6, line, faFar) && TeamColors.JoinsForTest(7, line * 100 + (line == 7 ? 8 : 7), faFar)
					&& TeamColors.JoinsForTest(2, yy * 10000 + 1 * 100 + 2, faIn) && !TeamColors.JoinsForTest(2, yy * 10000 + 1 * 100 + 2, faDecade),
				$"팀컬러 묶음별: 연대 {cy / 10 * 10}년대 13 + '{y2:00} FA → {decadeWith.count}, + '{faFar.Year % 100:00} FA → {decadeFar.count}; 단일 팀 올스타 + '{faFar.Year % 100:00} FA → {teamAll.count}; 단일 년도 + '{yy:00} FA → {single.count}; 듀얼 년도·년도 올스타·듀얼 팀은 그 해 FA만, 팀 올스타는 아무 해");

			// 4. Album pages: 등급별 FA = every FA card, a season's club 20 = its FA cards, the 레어 pages without them.
			List<int> all = AlbumHandlers.Page(catalog, 254, AlbumHandlers.FaPage, 0);
			int year = faBat.Year;
			List<int> season = AlbumHandlers.Page(catalog, year % 100, AlbumHandlers.FaAlbumTeam, year);
			List<int> rare9 = AlbumHandlers.Page(catalog, 254, 2, 0), rare8 = AlbumHandlers.Page(catalog, 254, 3, 0);
			Check(all.Count == fa.Count && season.Count == fa.Count(c => c.Year == year) && season.Contains(faBat.Idx)
					&& !rare9.Concat(rare8).Any(i => catalog.Get(i).IsFA) && rare9.Count + rare8.Count == rares,
				$"album: FA page {all.Count}, '{year % 100:00} FA {season.Count}, 9볼·8볼 레어 pages {rare9.Count} + {rare8.Count} without FA");

			// 5. FA 시장 on the test team: the 계약금 taken (장부 "FA 영입"), signed once, refused without the MP or when full.
			uint user = 0;
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				c.Open();
				using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
			}
			Check(user != 0, $"test team {user}");
			if (user == 0) return 1;
			CardInfo buy = faBat2, dear = fa.Where(c => c.Idx != buy.Idx).OrderByDescending(c => FaMarket.Price(c)).First();
			db.RemoveCards(user, new[] { buy.Idx, dear.Idx });
			int price = FaMarket.Price(buy);
			int mp0 = db.AddMp(user, 0);
			db.AddMp(user, price + 1_000 - mp0, MpLedger.Test);   // exactly the price and 1,000 more
			string ask = FaMarket.Problem(user, catalog.Get(buy.Idx), out int mpAsk);
			string done = FaMarket.Sign(user, buy.Idx);
			int mpAfter = db.AddMp(user, 0);
			long ledger = 0;
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				c.Open();
				using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT amount FROM mp_ledger WHERE user_code = $u AND source = $s ORDER BY id DESC LIMIT 1";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", MpLedger.FaSigning);
				ledger = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
			}
			bool owned = db.GetCards(user).Any(x => x.Idx == buy.Idx);
			Check(ask == null && mpAsk == price + 1_000 && !done.StartsWith("!") && owned && mpAfter == 1_000 && ledger == -price,
				$"signed {FaMarket.Label(buy)} for {price:N0} MP: owned {owned}, MP {mpAsk:N0} → {mpAfter:N0}, 장부 {ledger:N0} ({done.Replace('\n', ' ')})");
			string again = FaMarket.Sign(user, buy.Idx), poor = FaMarket.Sign(user, dear.Idx);
			Check(again.StartsWith("!") && again.Contains("이미") && poor.StartsWith("!") && poor.Contains("MP가 부족") && !db.GetCards(user).Any(x => x.Idx == dear.Idx) && db.AddMp(user, 0) == 1_000,
				$"refused: again ({again.Substring(1).Replace('\n', ' ')}), without the MP ({poor.Substring(1).Replace('\n', ' ')})");
			Check(FaMarket.Problem(user, catalog.Get(src.Idx), out _) != null, $"a card that is not FA ({src.Name}) is not sold");

			// 6. The FA contract (user 18:3x): 14 days from signing, 14 more a renewal at the 계약금 × (1 + 0.1 n), +5 for the first six.
			TeamCard signedCard = db.GetCards(user).First(x => x.Idx == buy.Idx);
			int p8 = FaMarket.PriceByBall[8], p9 = FaMarket.PriceByBall[9];
			Check(p8 == 15_000_000 && p9 == 30_000_000 && signedCard.RemainContract == ContractHandlers.FaStartDays
					&& ContractHandlers.FaCost(0, p8, 0) == 16_500_000 && ContractHandlers.FaCost(1, p8, 0) == 18_000_000 && ContractHandlers.FaCost(2, p8, 0) == 19_500_000
					&& ContractHandlers.FaCost(3, p8, 0) == 7_800_000 && ContractHandlers.FaCost(9, p8, 0) == 7_800_000
					&& ContractHandlers.FaCost(0, p9, 0) == 33_000_000 && ContractHandlers.FaCost(2, p9, 0) == 39_000_000 && ContractHandlers.FaCost(3, p9, 0) == 15_600_000
					&& ContractHandlers.FaCost(0, p8, ContractHandlers.SurchargeAfter + 1) == 19_800_000,
				$"계약금 8볼 {p8:N0} / 9볼 {p9:N0}; signed for {signedCard.RemainContract} days; renewals 8볼 {string.Join(" → ", Enumerable.Range(0, 5).Select(n => ContractHandlers.FaCost(n, p8, 0).ToString("N0")))}, 9볼 {string.Join(" → ", Enumerable.Range(0, 5).Select(n => ContractHandlers.FaCost(n, p9, 0).ToString("N0")))}; one day over {ContractHandlers.SurchargeAfter} × 1.2");
			int[][] splits = Enumerable.Range(0, 256).Select(ContractHandlers.FaBonusPoints).ToArray();
			int valid = splits.Count(s => s != null);
			Check(valid == 252 && splits.Take(252).All(s => s.Sum() == 5 && s.All(v => v >= 0) && ContractHandlers.FaBonusCode(s) >= 0)
					&& splits.Take(252).Select(s => string.Join(",", s)).Distinct().Count() == 252 && string.Join(",", splits[0]) == "0,0,0,0,0,5"
					&& string.Join(",", splits[251]) == "5,0,0,0,0,0" && ContractHandlers.FaBonusCode(new[] { 1, 2, 0, 0, 2, 0 }) >= 0,
				$"FA bonus codes: {valid} splits of 5 over six stats (0 = {string.Join(",", splits[0])}, 251 = {string.Join(",", splits[251])}), 252-255 none");
			int code = ContractHandlers.FaBonusCode(new[] { 2, 3, 0, 0, 0, 0 });
			int cost1 = ContractHandlers.Cost(signedCard.ContractCount, buy, signedCard.RemainContract);
			db.AddMp(user, cost1 + 500 - db.AddMp(user, 0), MpLedger.Test);
			string ext = ContractHandlers.TryExtend(user, buy.Idx, code, DateTime.Today, out int mpExt);
			TeamCard renewed = db.GetCards(user).First(x => x.Idx == buy.Idx);
			string refusedCode = ContractHandlers.TryExtend(user, buy.Idx, 252, DateTime.Today, out _);
			Check(ext == null && mpExt == 500 && renewed.RemainContract == ContractHandlers.FaStartDays + ContractHandlers.FaExtendDays && renewed.ContractCount == 1
					&& renewed.Contract[0] - signedCard.Contract[0] == 2 && renewed.Contract[1] - signedCard.Contract[1] == 3 && renewed.Contract.Sum(v => v) - signedCard.Contract.Sum(v => v) == 5
					&& refusedCode != null,
				$"renewed with {signedCard.RemainContract} days left for {cost1:N0} MP (surcharge after {ContractHandlers.SurchargeAfter}): {renewed.RemainContract} days, renewal {renewed.ContractCount}, bonus 컨택 +2 파워 +3; code 252 refused ({refusedCode})");

			// 7. 코칭스태프 (user 18:3x "자팀 효과"): an FA card is of every line; other cards of their own.
			CardInfo lg = catalog.Drawable.First(c => c.IsBatter && TeamColors.Lineage(c.TeamCode) == TeamColors.Lineage(16));
			int lgLine = CoachCatalog.Line(16), otherLine = CoachCatalog.Line(buy.HomeTeam) == lgLine ? CoachCatalog.Line(7) : CoachCatalog.Line(buy.HomeTeam);
			Check(CoachCatalog.InLine(buy, lgLine) && CoachCatalog.InLine(buy, otherLine) && CoachCatalog.InLine(lg, lgLine) && !CoachCatalog.InLine(lg, otherLine)
					&& !CoachCatalog.InLine(buy, -1),
				$"coach lines: FA {buy.Name} in LG's line {lgLine} and line {otherLine}; {lg.Year} {lg.Name} (LG) only in its own");
			ServerLog.Info($"[fatest] {s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}
	}
}
