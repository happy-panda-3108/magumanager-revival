using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// Player card packs: each pack gives one card whose ball count is drawn from the pack's odds
	// (ma9m-economy draft, confirmed by the systems session 2026-10-18), then a card of that ball the
	// team does not own yet. When a ball has no card left, the nearest ball that has one is used.
	public static class CardPacks
	{
		// The 전체 선수팩's percent by ball (index 0 = 1볼), shared with the 연대·동군·서군 packs.
		public static readonly double[] AllOdds = { 20, 18, 15, 13, 11, 9.6, 7.4, 4.7, 1.0, 0.3 };

		// Percent by ball, index 0 = 1볼.
		private static readonly Dictionary<int, double[]> Odds = new Dictionary<int, double[]>
		{
			// 전체 카드 선수팩 1~10볼: 9볼 이상 1.3%, 10볼 0.3% since 2026-11-11 (user: "엘리트 난이도도 어렵게 합시다" … "이대로 적용합시다";
			// was 20 18 15 13 11 9 6.5 4 2.5 1, 9볼 이상 3.5%, 10볼 1%). The 연대·동군·서군 packs (ShopHandlers) use the same odds.
			[ItemCatalog.AllPlayersPack] = AllOdds,
			// 고급 카드 선수팩 4~10볼: 9볼 이상 3.5%, 10볼 0.7% since 2026-11-11 (was 0 0 0 28 24 18 12 8 7 3, 9볼 이상 10%, 10볼 3%).
			[ItemCatalog.HighPlayersPack] = new double[] { 0, 0, 0, 29, 25, 20, 13.5, 9, 2.8, 0.7 },
			// 레전드 선수팩 3~10볼 (2026-11-08), like 고급's spread from 3볼, besides the 레전드 roll (LegendChance). 9볼 2.4%, 10볼 0.9% since
			// 2026-11-11 (user 18:4x: "레전드 선수팩의 확률도 낮춰주길 바람", the (가) proposal: a 엘리트 about 421만 MP, was 118만 and the
			// cheapest source; was 0 0 22 20 17 14 10 7 6 4).
			[ItemCatalog.LegendPlayersPack] = new double[] { 0, 0, 22, 20, 18, 15.5, 12, 9.2, 2.4, 0.9 }
		};

		// A 레전드 card per 레전드 선수팩 (user, 2026-11-08: "7만 MP로 하고, 2천만 MP가 레전드 1장의 기댓값으로"): 70,000 / 20,000,000.
		public const int LegendPackPrice = 70_000;
		public const double LegendChance = LegendPackPrice / 20_000_000.0;

		// Whether the tables hold any 레전드 card (the pack is sold and opened only then).
		public static bool LegendsReady(CardCatalog catalog) => catalog.All.Any(c => c.IsLegend);

		private static readonly Random s_rnd = new Random();

		public static bool IsPack(int item) => Odds.ContainsKey(item);

		// Draws count cards for one team (never one it owns, never the same card twice).
		public static List<CardInfo> Open(int item, int count, CardCatalog catalog, ICollection<int> owned)
		{
			double[] odds = Odds[item];
			HashSet<int> taken = new HashSet<int>(owned);
			Dictionary<int, List<CardInfo>> byBall = catalog.Drawable.GroupBy(c => c.Ball).ToDictionary(g => g.Key, g => g.ToList());
			// The pack's own ball range (a 4~10볼 pack never falls back to a 3볼 card).
			int lo = Array.FindIndex(odds, x => x > 0) + 1, hi = Array.FindLastIndex(odds, x => x > 0) + 1;
			List<CardInfo> cards = new List<CardInfo>();
			for (int n = 0; n < count; n++)
			{
				int ball = DrawBall(odds);
				CardInfo card = null;
				// 레전드 선수팩: a 레전드 card the team does not own, at LegendChance (none free: a 3~10볼 card).
				if (item == ItemCatalog.LegendPlayersPack && s_rnd.NextDouble() < LegendChance)
				{
					List<CardInfo> legends = catalog.All.Where(c => c.IsLegend && !taken.Contains(c.Idx)).ToList();
					if (legends.Count > 0) card = legends[s_rnd.Next(legends.Count)];
				}
				for (int spread = 0; spread <= 9 && card == null; spread++)
				{
					foreach (int b in new[] { ball - spread, ball + spread }.Distinct())
					{
						if (card != null || b < lo || b > hi || !byBall.TryGetValue(b, out List<CardInfo> pool))
						{
							continue;
						}
						List<CardInfo> free = pool.Where(c => !taken.Contains(c.Idx)).ToList();
						if (free.Count > 0)
						{
							card = free[s_rnd.Next(free.Count)];
							break;
						}
					}
				}
				if (card == null)
				{
					break;
				}
				taken.Add(card.Idx);
				cards.Add(card);
			}
			return cards;
		}

		private static int DrawBall(double[] odds)
		{
			double u = s_rnd.NextDouble() * odds.Sum();
			for (int i = 0; i < odds.Length; i++)
			{
				u -= odds[i];
				if (u < 0)
				{
					return i + 1;
				}
			}
			return Array.FindLastIndex(odds, p => p > 0) + 1;
		}
	}
}
