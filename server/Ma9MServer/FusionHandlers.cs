using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 선수카드 조합 (user, 2026-10-18). Cards stand on one ladder of rungs:
	//   1-4볼 노멀 (0-3), 5-7볼 스페셜 (4-6), 8볼 레어 (7), 9볼 레어 (8), 9볼 엘리트 (9), 10볼 엘리트 (10).
	// The highest card put in is the main card; the others support it. Fill = the support's balls ÷
	// (7 × the main card's balls), so seven cards as good as the main card fill it (1.0). The result
	// is a new card of the main card's side (batter / pitcher) on a rung drawn from the main rung's
	// table at that fill: a thin support risks one rung down, a full one climbs. At most +2 balls in
	// one combination (5볼 never becomes 9볼); elites come only from a rare or elite main card.
	// A protection item (레어 90601, 엘리트 91461) keeps the main card when the result would be lower.
	public static class FusionRules
	{
		public static readonly int[] RungBall = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 9, 10 };
		public static readonly string[] RungName = { "1볼 노멀", "2볼 노멀", "3볼 노멀", "4볼 노멀", "5볼 스페셜", "6볼 스페셜", "7볼 스페셜", "8볼 레어", "9볼 레어", "9볼 엘리트", "10볼 엘리트" };

		public static int RungOf(CardInfo c)
		{
			if (c.Grade >= 4) return c.Ball >= 10 ? 10 : 9;   // elite (9볼 when its ball value is under 75)
			if (c.Grade == 3) return c.Ball >= 9 ? 8 : 7;     // rare
			return Math.Clamp(c.Ball - 1, 0, 6);              // normal 1-4, special 5-7
		}

		public static int GradeOfRung(int rung) => rung >= 9 ? 4 : rung >= 7 ? 3 : rung >= 4 ? 2 : 1;

		// Fill points of the tables (user, 2026-10-18: a little more preserving and climbing; a
		// support of about 2.5 main cards, e.g. 8볼 레어 + 7·5·5·5, no longer drops).
		private static readonly double[] Anchor = { 0, 0.15, 0.35, 0.65, 1.0 };

		// Outcome rungs and their weights (%) at each anchor fill (linear in between).
		private static (int[] rungs, double[][] w) Table(int r)
		{
			switch (r)
			{
				case 10:
					return (new[] { 9, 10 }, new[] { new[] { 45.0, 55 }, new[] { 15.0, 85 }, new[] { 0.0, 100 }, new[] { 0.0, 100 }, new[] { 0.0, 100 } });
				case 9:
					return (new[] { 8, 9, 10 }, new[] { new[] { 45.0, 55, 0 }, new[] { 15.0, 75, 10 }, new[] { 0.0, 75, 25 }, new[] { 0.0, 60, 40 }, new[] { 0.0, 45, 55 } });
				case 8:
					return (new[] { 7, 8, 9, 10 }, new[] { new[] { 45.0, 55, 0, 0 }, new[] { 15.0, 70, 12, 3 }, new[] { 0.0, 58, 30, 12 }, new[] { 0.0, 40, 37, 23 }, new[] { 0.0, 22, 40, 38 } });
				case 7:
					return (new[] { 6, 7, 8, 9, 10 }, new[] { new[] { 45.0, 55, 0, 0, 0 }, new[] { 15.0, 70, 12, 3, 0 }, new[] { 0.0, 62, 26, 9, 3 }, new[] { 0.0, 42, 36, 15, 7 }, new[] { 0.0, 25, 40, 22, 13 } });
				case 0:
					return (new[] { 0, 1, 2 }, new[] { new[] { 100.0, 0, 0 }, new[] { 85.0, 13, 2 }, new[] { 62.0, 30, 8 }, new[] { 42.0, 40, 18 }, new[] { 25.0, 45, 30 } });
				default:
					// Normals and specials: one down, same, one up, two up (7볼 tops out at 9볼 레어).
					return (new[] { r - 1, r, r + 1, r + 2 }, new[] { new[] { 45.0, 55, 0, 0 }, new[] { 15.0, 70, 13, 2 }, new[] { 0.0, 62, 30, 8 }, new[] { 0.0, 42, 40, 18 }, new[] { 0.0, 25, 45, 30 } });
			}
		}

		public static double Fill(CardInfo main, IEnumerable<CardInfo> support) =>
			Math.Clamp(support.Sum(c => c.Ball) / (7.0 * main.Ball), 0, 1);

		// The outcome distribution of a main rung at a fill.
		public static List<(int rung, double p)> Odds(int rung, double fill)
		{
			(int[] rungs, double[][] w) = Table(rung);
			double f = Math.Clamp(fill, 0, 1);
			int lo = 0;
			while (lo < Anchor.Length - 2 && f > Anchor[lo + 1]) lo++;
			double t = (f - Anchor[lo]) / (Anchor[lo + 1] - Anchor[lo]);
			List<(int, double)> odds = new List<(int, double)>();
			for (int k = 0; k < rungs.Length; k++)
			{
				double p = (1 - t) * w[lo][k] + t * w[lo + 1][k];
				if (p > 0) odds.Add((rungs[k], p / 100));
			}
			return odds;
		}

		public static int Draw(List<(int rung, double p)> odds, Random rnd)
		{
			double u = rnd.NextDouble(), acc = 0;
			foreach ((int rung, double p) in odds)
			{
				acc += p;
				if (u < acc) return rung;
			}
			return odds[^1].rung;
		}

		// A random card of a rung and side, skipping the given cards (nearest rung when none is free).
		// A card of the rung the user does not own; when that rung has none left, the nearest rung,
		// upward first, never below floor (systems session, 2026-10-20: with every 10볼 엘리트 owned, an
		// elite fusion fell back to a 9볼 레어 and even a protection item could not keep the main card).
		public static CardInfo Pick(CardCatalog catalog, int rung, bool batter, ISet<int> skip, Random rnd, int floor = 0)
		{
			List<CardInfo> all = catalog.Drawable.Where(c => c.IsBatter == batter && !skip.Contains(c.Idx)).ToList();
			for (int d = 0; d <= 10; d++)
			{
				foreach (int r in new[] { rung + d, rung - d }.Distinct())
				{
					if (r < 0 || r > 10 || r < floor) continue;
					List<CardInfo> pool = all.Where(c => RungOf(c) == r).ToList();
					if (pool.Count > 0) return pool[rnd.Next(pool.Count)];
				}
			}
			return null;
		}

		// Any card the user could still get at this rung or above.
		public static bool AnyLeft(CardCatalog catalog, int fromRung, bool batter, ISet<int> skip) =>
			catalog.Drawable.Any(c => c.IsBatter == batter && !skip.Contains(c.Idx) && RungOf(c) >= fromRung);
	}

	public static class FusionHandlers
	{
		public const int RareProtect = 90601, EliteProtect = 91461, LegendProtect = 91451;

		private const int MinKeep = 39;   // client Combine_Main: 보유 선수 − 조합 카드 ≥ 39
		private const int RouletteCards = 14;

		private static Database s_db;
		private static CardCatalog s_catalog;
		private static readonly Random s_rnd = new Random();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog)
		{
			s_db = db;
			s_catalog = catalog;
			loop.Register<Ma9MPacket.PKS_CS_ENTER_CARD_FUSION>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_TRY_CARD_FUSION>(Try);
		}

		private static PUICardInfo Info(uint user, TeamCard c, DateTime day) =>
			new PUICardInfo(c.Idx, Condition.For(user, c.Idx, day), c.RemainContract, c.PositionTrainingInfo, (byte)(c.IsLock ? 2 : 1));

		// A card in 특별수강 cannot be combined (LessonHandlers; user, 2026-10-22).
		internal static bool LockedForLesson(uint user, int idx) => LessonHandlers.InLesson(user, idx);

		private static HashSet<int> Deck(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			return l == null ? new HashSet<int>() : l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_CARD_FUSION packet)
		{
			uint user = session.Team.UserCode;
			DateTime today = DateTime.Now;  // the time, for 컨디션 회복제 (Condition.For)
			// Most recently acquired first: the client shows this order until a sort is picked (user,
			// 2026-10-22: "조합 메뉴에 들어올 때는 항상 '최근 영입한 순서'로").
			Dictionary<int, int> recent = s_db.CardsByRecent(user).Select((idx, i) => (idx, i)).ToDictionary(x => x.idx, x => x.i);
			List<TeamCard> cards = s_db.GetCards(user).OrderBy(c => recent.GetValueOrDefault(c.Idx, int.MaxValue)).ToList();
			Ma9MPacket.PKS_SC_ENTER_CARD_FUSION reply = new Ma9MPacket.PKS_SC_ENTER_CARD_FUSION();
			reply.players.array = cards.Select(c => Info(user, c, today)).ToArray();
			// Cards in the 1군 lineup are left out of the list (the client hides them).
			reply.deck.array = Deck(user).Select(i => new PInteger { n = i }).ToArray();
			// Cards in 특별수강 (LessonHandlers): the client leaves them out of the list.
			reply.lesson_players.array = LessonHandlers.InLesson(user).Select(i => new PInteger { n = i }).ToArray();
			reply._max_possess_count.n = (byte)Math.Min(255, (int)RosterHandlers.MaxPossessOf(user));
			List<(int item, int amount)> items = new[] { RareProtect, EliteProtect, LegendProtect }
				.Select(i => (i, InventoryHandlers.ItemAmount(user, i))).Where(x => x.Item2 > 0).ToList();
			reply.item_index.array = items.Select(x => new PInteger { n = x.item }).ToArray();
			reply.item_amount.array = items.Select(x => new PShort { n = (short)Math.Min(short.MaxValue, x.amount) }).ToArray();
			reply.max_possess_count.n = RosterHandlers.MaxPossessOf(user);
			session.Send(reply);
		}

		// result: 0 OK, 5 = two legends (client message), 1 = anything else refused.
		private static void Try(GameSession session, Ma9MPacket.PKS_CS_TRY_CARD_FUSION packet)
		{
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_TRY_CARD_FUSION reply = new Ma9MPacket.PKS_SC_TRY_CARD_FUSION();
			reply.DummyIndices.array = Array.Empty<PInteger>();
			List<int> picked = (packet.fusion_cards.array ?? Array.Empty<PInteger>()).Select(p => p.n).Where(i => i != 0).Distinct().ToList();
			List<TeamCard> owned = s_db.GetCards(user);
			Dictionary<int, TeamCard> byIdx = owned.ToDictionary(c => c.Idx);
			HashSet<int> deck = Deck(user);
			List<CardInfo> inputs = picked.Select(s_catalog.Get).Where(c => c != null).ToList();
			string refuse =
				picked.Count < 2 || picked.Count > 8 ? "card count" :
				inputs.Count != picked.Count ? "unknown card" :
				picked.Any(i => !byIdx.ContainsKey(i)) ? "not owned" :
				picked.Any(i => byIdx[i].IsLock) ? "locked card" :
				picked.Any(deck.Contains) ? "lineup card" :
				picked.Any(i => LockedForLesson(user, i)) ? "card in 특별수강" :
				owned.Count - picked.Count < MinKeep ? "too few players left" : null;
			if (inputs.Count(c => c.IsLegend) > 1)
			{
				reply.result.n = 5;
				session.Send(reply);
				return;
			}
			if (refuse != null)
			{
				ServerLog.Info($"[{session.Id}] card fusion refused: {refuse}");
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			if (inputs.Any(c => c.IsLegend))
			{
				Legend(session, packet, reply, inputs, picked, owned);
				return;
			}
			CardInfo main = inputs.OrderByDescending(FusionRules.RungOf).ThenByDescending(c => c.Ball).First();
			List<CardInfo> support = inputs.Where(c => c != main).ToList();
			int mainRung = FusionRules.RungOf(main);
			HashSet<int> skip = owned.Select(c => c.Idx).Concat(picked).ToHashSet();
			// Every card of the main card's rung and above is already owned (user: one copy of each card is
			// the game's charm): nothing is used up; result 6 tells the client (Ma9M_Combine_NoHigher).
			if (!FusionRules.AnyLeft(s_catalog, mainRung, main.IsBatter, skip))
			{
				ServerLog.Info($"[{session.Id}] card fusion refused: nothing left at {FusionRules.RungName[mainRung]} or above ({(main.IsBatter ? "batters" : "pitchers")})");
				reply.result.n = 6;
				session.Send(reply);
				return;
			}
			double fill = FusionRules.Fill(main, support);
			List<(int rung, double p)> odds = FusionRules.Odds(mainRung, fill);
			int rung = FusionRules.Draw(odds, s_rnd);

			// A protection item (consumed when used) keeps the main card if the result would be lower.
			int item = (packet.use_item.array ?? Array.Empty<PInteger>()).Select(p => p.n).FirstOrDefault(i => i != 0);
			bool protects = item == RareProtect && main.Grade == 3 || item == EliteProtect && main.Grade == 4 || item == LegendProtect && main.Grade == 5;
			if (item != 0)
			{
				if (InventoryHandlers.ItemAmount(user, item) <= 0)
				{
					reply.result.n = 1;
					session.Send(reply);
					return;
				}
				InventoryHandlers.AddItem(user, item, -1);
			}
			CardInfo result;
			if (rung < mainRung && protects)
			{
				result = main;   // kept: only the support cards are used up
				s_db.RemoveCards(user, support.Select(c => c.Idx));
			}
			else
			{
				// An exhausted rung falls back upward, and never below the main card's rung when the draw was at
				// or above it.
				result = FusionRules.Pick(s_catalog, rung, main.IsBatter, skip, s_rnd, rung >= mainRung ? mainRung : rung) ?? main;
				s_db.RemoveCards(user, picked);
				s_db.AddCards(user, new[] { result.Idx }, RosterHandlers.StartContract);
			}
			int resultRung = FusionRules.RungOf(result);
			Missions.Add(user, Missions.Fusions);  // 업적
			TeamCard got = s_db.GetCards(user).FirstOrDefault(c => c.Idx == result.Idx);
			PUICardInfo info = got != null ? Info(user, got, DateTime.Today) : new PUICardInfo(result.Idx, 2, RosterHandlers.StartContract);
			// The packet sends the field object its constructor bound, so the card is filled in place.
			reply.card_info.index.n = info.index.n;
			reply.card_info.condition.n = info.condition.n;
			reply.card_info.remain_contract.n = info.remain_contract.n;
			reply.card_info.position_training_info.n = info.position_training_info.n;
			reply.card_info.IsLock.n = info.IsLock.n;
			// Rare and elite results spin the roulette: dummy cards from the same odds, the result among them.
			if (resultRung >= 7)
			{
				HashSet<int> used = new HashSet<int>(skip) { result.Idx };
				List<PInteger> dummies = new List<PInteger>();
				for (int k = 0; k < RouletteCards; k++)
				{
					CardInfo d = FusionRules.Pick(s_catalog, FusionRules.Draw(odds, s_rnd), main.IsBatter, used, s_rnd);
					if (d == null) break;
					used.Add(d.Idx);
					dummies.Add(new PInteger { n = d.Idx });
				}
				reply.DummyIndices.array = dummies.ToArray();
			}
			reply.result.n = 0;
			ServerLog.Info($"[{session.Id}] card fusion: main {main.Name} {FusionRules.RungName[mainRung]}, {support.Count} support ({support.Sum(c => c.Ball)} balls, fill {fill:F2}) → "
				+ $"{FusionRules.RungName[resultRung]} {result.Name}{(result == main ? " (kept)" : "")}");
			session.Send(reply);
		}

		// 레전드 조합 (user, 2026-11-08: "레전드 1장을 넣은 다음 추가되는 볼수에 따라서 레전드 보존 확률이 달라지는 것으로 합시다. 랜덤
		// 레전드 카드 중에서 나오게끔 … 레전드 카드의 볼 상승, 감소는 고려하지 않음"; asked the same night: "1볼당 1.67%로 해서 총 60볼을
		// 넣으면 100% 보존", a failure gives a 10볼 엘리트 of the 레전드's side, a kept one is any 레전드 "전체 레전드에서"): one 레전드 and up
		// to seven other cards; kept at LegendKeep(their balls) as a 레전드 the team does not own; otherwise a 10볼 엘리트 (9볼 when
		// none is free), or with the 레전드 보호 item (LegendProtect, the original's) the 레전드 stays and only the others go.
		public const int LegendFullBalls = 60;

		public static double LegendKeep(int supportBalls) => Math.Clamp(supportBalls / (double)LegendFullBalls, 0, 1);

		// One 레전드 조합's card: a free 레전드 at LegendKeep, else the 레전드 itself with the 보호 item, else a 10볼 엘리트 of its side
		// (9볼 엘리트 when none is free). skip: the team's cards and the ones put in.
		public static (CardInfo result, bool kept) LegendOutcome(CardCatalog catalog, CardInfo legend, List<CardInfo> support, ISet<int> skip, bool protect, Random rnd)
		{
			List<CardInfo> free = catalog.All.Where(c => c.IsLegend && !skip.Contains(c.Idx)).ToList();
			if (free.Count > 0 && rnd.NextDouble() < LegendKeep(support.Sum(c => c.Ball)))
			{
				return (free[rnd.Next(free.Count)], true);
			}
			if (protect) return (legend, false);
			return (FusionRules.Pick(catalog, 10, legend.IsBatter, skip, rnd, 10) ?? FusionRules.Pick(catalog, 9, legend.IsBatter, skip, rnd, 9) ?? legend, false);
		}

		private static void Legend(GameSession session, Ma9MPacket.PKS_CS_TRY_CARD_FUSION packet, Ma9MPacket.PKS_SC_TRY_CARD_FUSION reply,
			List<CardInfo> inputs, List<int> picked, List<TeamCard> owned)
		{
			uint user = session.Team.UserCode;
			CardInfo legend = inputs.First(c => c.IsLegend);
			List<CardInfo> support = inputs.Where(c => c != legend).ToList();
			HashSet<int> skip = owned.Select(c => c.Idx).Concat(picked).ToHashSet();
			List<CardInfo> free = s_catalog.All.Where(c => c.IsLegend && !skip.Contains(c.Idx)).ToList();
			if (free.Count == 0)
			{
				// Every 레전드 owned (one copy of each card): nothing is used up (Ma9M_Combine_NoHigher).
				ServerLog.Info($"[{session.Id}] 레전드 조합 refused: every 레전드 owned");
				reply.result.n = 6;
				session.Send(reply);
				return;
			}
			int item = (packet.use_item.array ?? Array.Empty<PInteger>()).Select(p => p.n).FirstOrDefault(i => i != 0);
			if (item != 0)
			{
				if (InventoryHandlers.ItemAmount(user, item) <= 0)
				{
					reply.result.n = 1;
					session.Send(reply);
					return;
				}
				InventoryHandlers.AddItem(user, item, -1);
			}
			int balls = support.Sum(c => c.Ball);
			double keep = LegendKeep(balls);
			(CardInfo result, bool kept) = LegendOutcome(s_catalog, legend, support, skip, item == LegendProtect, s_rnd);
			if (result == legend)
			{
				s_db.RemoveCards(user, support.Select(c => c.Idx));   // kept: only the others are used up
			}
			else
			{
				s_db.RemoveCards(user, picked);
				s_db.AddCards(user, new[] { result.Idx }, RosterHandlers.StartContract);
			}
			Missions.Add(user, Missions.Fusions);  // 업적
			TeamCard got = s_db.GetCards(user).FirstOrDefault(c => c.Idx == result.Idx);
			PUICardInfo info = got != null ? Info(user, got, DateTime.Today) : new PUICardInfo(result.Idx, 2, RosterHandlers.StartContract);
			reply.card_info.index.n = info.index.n;
			reply.card_info.condition.n = info.condition.n;
			reply.card_info.remain_contract.n = info.remain_contract.n;
			reply.card_info.position_training_info.n = info.position_training_info.n;
			reply.card_info.IsLock.n = info.IsLock.n;
			// The roulette: other 레전드 and 10볼 엘리트 cards around the result.
			HashSet<int> used = new HashSet<int>(skip) { result.Idx };
			List<PInteger> dummies = new List<PInteger>();
			foreach (CardInfo d in free.Where(c => c.Idx != result.Idx).OrderBy(_ => s_rnd.Next()).Take(RouletteCards / 2))
			{
				used.Add(d.Idx);
				dummies.Add(new PInteger { n = d.Idx });
			}
			while (dummies.Count < RouletteCards)
			{
				CardInfo d = FusionRules.Pick(s_catalog, 10, legend.IsBatter, used, s_rnd, 10);
				if (d == null) break;
				used.Add(d.Idx);
				dummies.Add(new PInteger { n = d.Idx });
			}
			reply.DummyIndices.array = dummies.OrderBy(_ => s_rnd.Next()).ToArray();
			reply.result.n = 0;
			ServerLog.Info($"[{session.Id}] 레전드 조합: {legend.Name} + {support.Count} card(s) ({balls} balls, keep {keep:P0}){(item != 0 ? $", item {item}" : string.Empty)} → "
				+ (kept ? $"레전드 {result.Name}" : result == legend ? $"kept {legend.Name} (보호)" : $"{FusionRules.RungName[FusionRules.RungOf(result)]} {result.Name}"));
			session.Send(reply);
		}

		// Self-check: outcome frequencies for typical combinations (logged).
		public static void SelfTest(CardCatalog catalog)
		{
			Random rnd = new Random(3);
			(string label, int main, int[] support)[] cases =
			{
				("9볼 레어 + 9 9 9 8 8 8 8", 8, new[] { 9, 9, 9, 8, 8, 8, 8 }),
				("8볼 레어 + 3볼 1장", 7, new[] { 3 }),
				("8볼 레어 + 8볼 레어 1장", 7, new[] { 8 }),
				("8볼 레어 + 8볼 7장", 7, new[] { 8, 8, 8, 8, 8, 8, 8 }),
				("8볼 레어 + 7 5 5 5", 7, new[] { 7, 5, 5, 5 }),
				("8볼 레어 + 5볼 7장", 7, new[] { 5, 5, 5, 5, 5, 5, 5 }),
				("9볼 레어 + 5볼 7장", 8, new[] { 5, 5, 5, 5, 5, 5, 5 }),
				("10볼 엘리트 + 9볼 2장", 10, new[] { 9, 9 }),
				("10볼 엘리트 + 9볼 6장", 10, new[] { 9, 9, 9, 9, 9, 9 }),
				("9볼 엘리트 + 9볼 7장", 9, new[] { 9, 9, 9, 9, 9, 9, 9 }),
				("5볼 스페셜 + 5볼 1장", 4, new[] { 5 }),
				("5볼 스페셜 + 5볼 7장", 4, new[] { 5, 5, 5, 5, 5, 5, 5 }),
				("7볼 스페셜 + 7볼 7장", 6, new[] { 7, 7, 7, 7, 7, 7, 7 }),
			};
			foreach ((string label, int mainRung, int[] support) in cases)
			{
				double fill = Math.Clamp(support.Sum() / (7.0 * FusionRules.RungBall[mainRung]), 0, 1);
				List<(int rung, double p)> odds = FusionRules.Odds(mainRung, fill);
				int[] count = new int[11];
				for (int t = 0; t < 20000; t++) count[FusionRules.Draw(odds, rnd)]++;
				ServerLog.Info($"fusion {label} (fill {fill:F2}): " + string.Join(", ", odds.Select(o => $"{FusionRules.RungName[o.rung]} {100 * o.p:F1}% (drawn {100.0 * count[o.rung] / 20000:F1}%)")));
			}
		}
	}
}
