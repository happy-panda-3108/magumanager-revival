using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// "--legendtest" (systems session, 2026-11-08): the 레전드 card rules on tables that hold the 레전드 rows (a data copy; it
	// writes nothing): the 1군 limit, the 레전드 선수팩 and the 레전드 조합.
	public static class LegendSelfTest
	{
		private static int s_problems;

		private static void Check(bool ok, string what)
		{
			if (!ok) s_problems++;
			ServerLog.Info($"[legendtest] {(ok ? "ok  " : "FAIL")} {what}");
		}

		public static int Run(CardCatalog catalog, Database db = null, string dbPath = null)
		{
			s_problems = 0;
			List<CardInfo> legends = catalog.All.Where(c => c.IsLegend).OrderBy(c => c.Idx).ToList();
			List<CardInfo> bats = legends.Where(c => c.IsBatter).ToList(), arms = legends.Where(c => !c.IsBatter).ToList();
			Check(legends.Count > 0 && legends.All(c => c.Grade == 5) && CardPacks.LegendsReady(catalog) && !catalog.Drawable.Any(c => c.IsLegend),
				$"{legends.Count} 레전드 ({bats.Count} batters, {arms.Count} pitchers), grade 5, left out of the random draws: {string.Join(", ", legends.Select(c => $"{c.Name} {c.Ball}볼"))}");
			if (bats.Count < 3 || arms.Count < 2) return 1;

			// 1. The 1군: one 레전드 batter and one pitcher at most; lineups the server builds keep the best one of each.
			Lineup two = new Lineup();
			two.Batters[0] = bats[0].Idx;
			two.Batters[1] = bats[1].Idx;
			two.Pitchers[0] = arms[0].Idx;
			Lineup one = new Lineup();
			one.Batters[0] = bats[0].Idx;
			one.Pitchers[0] = arms[0].Idx;
			Lineup twoArms = new Lineup();
			twoArms.Pitchers[0] = arms[0].Idx;
			twoArms.Pitchers[5] = arms[1].Idx;
			string problem = RosterHandlers.LegendProblem(two);
			Check(problem != null && problem.Contains(bats[1].Name) && RosterHandlers.LegendProblem(one) == null && RosterHandlers.LegendProblem(twoArms) != null,
				$"1군 limit: two 레전드 batters refused ({problem?.Replace('\n', ' ')}), one each taken, two 레전드 pitchers refused");
			List<int> kept = RosterHandlers.LegendLimited(bats.Take(3).Concat(arms.Take(2)).Select(c => c.Idx).Append(100001)).ToList();
			Check(kept.Count(i => catalog.Get(i)?.IsLegend == true && catalog.Get(i).IsBatter) == 1 && kept.Count(i => catalog.Get(i)?.IsLegend == true && !catalog.Get(i).IsBatter) == 1 && kept.Contains(100001),
				$"server-built lineups: {kept.Count} of 6 cards left (one 레전드 batter, one pitcher, the others)");

			// 2. 레전드 선수팩: 3~10볼 cards, a 레전드 at LegendChance (10,000 cards in 50-card opens).
			Random rnd = new Random(8);
			int draws = 0, legendDraws = 0, outOfRange = 0;
			for (int k = 0; k < 200; k++)
			{
				List<CardInfo> got = CardPacks.Open(ItemCatalog.LegendPlayersPack, 50, catalog, new List<int>());
				draws += got.Count;
				legendDraws += got.Count(c => c.IsLegend);
				outOfRange += got.Count(c => !c.IsLegend && (c.Ball < 3 || c.Ball > 10));
			}
			double rate = draws == 0 ? 0 : (double)legendDraws / draws;
			Check(draws == 10_000 && outOfRange == 0 && rate > 0.0015 && rate < 0.0060,
				$"레전드 선수팩: {draws} cards, {legendDraws} 레전드 ({rate:P2}, set {CardPacks.LegendChance:P2} = 7만 / 2천만), out of 3~10볼 {outOfRange}");
			Check(CardPacks.Open(ItemCatalog.AllPlayersPack, 50, catalog, new List<int>()).All(c => !c.IsLegend), "전체 선수팩 gives no 레전드");

			// 3. 레전드 조합: kept at balls ÷ 60 as any free 레전드; otherwise a 10볼 엘리트 of the 레전드's side, or the 레전드 with the 보호 item.
			CardInfo main = bats[0];
			List<CardInfo> support30 = catalog.Drawable.Where(c => c.IsBatter && c.Ball == 10).Take(3).ToList();
			HashSet<int> skip = new HashSet<int>(support30.Select(c => c.Idx)) { main.Idx };
			int keptN = 0, eliteN = 0, wrong = 0, trials = 4000;
			for (int k = 0; k < trials; k++)
			{
				(CardInfo r, bool keptOne) = FusionHandlers.LegendOutcome(catalog, main, support30, skip, false, rnd);
				if (keptOne) { keptN++; if (!r.IsLegend || r.Idx == main.Idx) wrong++; }
				else { eliteN++; if (r.IsLegend || !r.IsBatter || FusionRules.RungOf(r) != 10) wrong++; }
			}
			double keptRate = (double)keptN / trials;
			Check(FusionHandlers.LegendKeep(60) == 1 && Math.Abs(FusionHandlers.LegendKeep(30) - 0.5) < 1e-9 && FusionHandlers.LegendKeep(0) == 0
					&& support30.Count == 3 && Math.Abs(keptRate - 0.5) < 0.04 && wrong == 0,
				$"레전드 조합 with 30 balls: kept {keptRate:P1} (set 50%), else 10볼 엘리트 batters ({eliteN}), wrong {wrong}");
			bool protectOk = Enumerable.Range(0, 200).All(_ =>
			{
				(CardInfo r, bool keptOne) = FusionHandlers.LegendOutcome(catalog, main, new List<CardInfo>(), skip, true, rnd);
				return !keptOne && r == main;
			});
			HashSet<int> allOwned = new HashSet<int>(legends.Select(c => c.Idx));
			(CardInfo none, bool noneKept) = FusionHandlers.LegendOutcome(catalog, main, catalog.Drawable.Where(c => c.Ball == 10).Take(6).ToList(), allOwned, false, rnd);
			Check(protectOk && !noneKept && !none.IsLegend, "0 balls with 레전드 보호: the 레전드 stays; every 레전드 owned: never a 레전드");
			// 4. 업적 (2026-11-09): the test team gets a 레전드 batter and pitcher (data copy): 첫 레전드 and 레전드 투·타 clear, 레전드 5장
			// does not, and a 레전드 counts neither as 엘리트 nor as a season.
			if (db != null && dbPath != null)
			{
				uint user = 0;
				using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
				{
					c.Open();
					using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
					user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
				}
				if (user != 0)
				{
					int[] give = { bats[0].Idx, arms[0].Idx };
					db.RemoveCards(user, give);
					HashSet<int> before = Achievements.ClearedOf(user);
					db.AddCards(user, give, RosterHandlers.StartContract);
					Achievements.Evaluate(user);
					HashSet<int> after = Achievements.ClearedOf(user);
					Check(after.Contains(251) && after.Contains(252) && !after.Contains(253) && (before.Contains(201) || !after.Contains(201)),
						$"업적: 레전드 투·타 1장씩 → 첫 레전드 {after.Contains(251)}, 레전드 투·타 {after.Contains(252)}, 레전드 5장 {after.Contains(253)} (team {user})");
				}
			}
			ServerLog.Info($"[legendtest] {s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}
	}
}
