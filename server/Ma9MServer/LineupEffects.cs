using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// How a lineup's arrangement plays (user, 2026-10-19):
	// - 선수분석 graph (roster screen): each batter's value from his 선호타순 and his neighbours in the
	//   order, each pitcher's from his role. The value (0-100, 50 neutral) plays as −5% .. +5% of every
	//   rating, like condition.
	// - A fielder at a spot he cannot play (defence 20 there) also plays 10% worse, besides fielding
	//   with that 20.
	// - A pitcher out of his role plays 15% worse (user, 2026-10-20: "SP는 선발로 쓰고, 계투에 들어가면
	//   크게 능력치 손해 … 중계투수도 RP에만 … SU, CP는 그 자리에서"): a starter in the bullpen, a
	//   reliever starting or in the SU/CP spot, a set-up man closing and the other way round (systems
	//   session; the share is its choice for "크게", next to the fielders' 10% plus defence 20).
	// The graph formula is the client's own (Roster_Analysis.MakeData), so the screen shows what plays.
	public static class LineupEffects
	{
		public const double OutOfPosition = -0.10;

		public const double OutOfRole = -0.15;

		// The pitcher's own role for the view (0 starter, 1-4 RP, 5 SU, 6 CP)? order = his CardInfo.PreferredOrder, or the
		// role he was changed to (포지션 변경, PositionChangeHandlers.OrderOf; user, 2026-10-24: "불펜에서 선발로 변경한 투수는
		// 불펜에서 사용하려고 하면 패널티"); −1 = no card.
		public static bool InRole(int view, int order) => order < 0 || order == Want(view);

		private static int Want(int view) => view == 0 ? 0 : view <= 4 ? 1 : view == 5 ? 2 : 3;

		private static double PitcherShare(int view, int order)
		{
			double share = FromGraph(PitcherGraph(view, order));
			return InRole(view, order) ? share : (1 + share) * (1 + OutOfRole) - 1;
		}

		// Dev: MA9M_NO_LINEUP_EFFECTS=1 plays without them (balance comparisons on data copies).
		private static readonly bool Off = Environment.GetEnvironmentVariable("MA9M_NO_LINEUP_EFFECTS") == "1";

		// Graph value → share of every rating: 0 → −5%, 50 → 0, 100 → +5%.
		public static double FromGraph(double value) => (value - 50) / 1000.0;

		// The nine batters in batting order (null = empty slot): their graph values.
		public static double[] BatterGraph(CardInfo[] order)
		{
			double[] v = new double[9];
			for (int m = 0; m < 9; m++)
			{
				CardInfo cur = order[m], next = order[(m + 1) % 9], prev = order[m == 0 ? 8 : m - 1];
				if (cur == null)
				{
					v[m] = 50;
					continue;
				}
				double num = 20;
				switch (cur.PreferredOrder)
				{
					case 0: if (m <= 1) num = 75; break;          // 상위: 1-2번
					case 1: if (m >= 2 && m <= 4) num = 75; break; // 클린업: 3-5번
					case 2: if (m >= 5) num = 75; break;           // 하위: 6-9번
					case 3: num = 60; break;                       // 밸런스: anywhere
				}
				foreach (CardInfo other in new[] { next, prev })
				{
					if (other == null) continue;
					num *= 1.05 - Math.Abs(cur.Age - other.Age) * 0.0075;
					num *= Team(cur.TeamCode, other.TeamCode);
					// Same batting hand ×0.95, mixed ×1.05, a switch hitter on either side ×1.05. The client tests "!= 2" (its
					// table's 0 L / 1 R / 2 S); ours is 1 R / 2 L / 3 S (CardInfo.HitHand), so lefties played as switch hitters
					// and switch hitters as a third hand until 2026-10-24.
					num *= cur.HitHand == other.HitHand && cur.HitHand != 3 && other.HitHand != 3 ? 0.95 : 1.05;
					if (cur.Year == other.Year) num *= 1.05;
				}
				v[m] = Math.Min(100, num * 1.0526316);
			}
			return v;
		}

		// Neighbours' clubs: same club ×1.05, rivals ×0.9 (codes 6 apart, KIA-롯데, 현대-SK), else ×0.95.
		private static double Team(int a, int b)
		{
			if (a == b) return 1.05;
			if (Math.Abs(a - b) == 6 || (a == 5 && b == 7) || (a == 7 && b == 5) || (a == 4 && b == 9) || (a == 9 && b == 4)) return 0.9;
			return 0.95;
		}

		// A pitcher in a staff spot: view 0 = today's starter, 1-4 = RP1-4, 5 = SU, 6 = CP (the roster's
		// pitcher list). His own role (PreferredOrder 0 선발, 1 중계, 2 셋업, 3 마무리) there 80, another
		// bullpen role 60, a starter in the bullpen (or a reliever starting) 40.
		public static double PitcherGraph(int view, int order)
		{
			if (order < 0) return 50;
			if (order == Want(view)) return 80;
			if (view == 0) return 40;
			return order != 0 ? 60 : 40;
		}

		// Sets the club's game cards' condition share for the arrangement. Call after the GameTeam is
		// built from this lineup (conditions set) and before CardGame.Build. owned: the club's team cards,
		// for pitchers' roles after a 포지션 변경 (AI clubs: null).
		public static void Apply(GameTeam t, Lineup l, CardCatalog catalog, IEnumerable<TeamCard> owned = null)
		{
			if (t == null || l == null || Off) return;
			Dictionary<int, byte> changed = owned?.Where(c => c.PositionTrainingInfo % 10 != 0).ToDictionary(c => c.Idx, c => c.PositionTrainingInfo);
			int Order(int idx)
			{
				CardInfo p = catalog.Get(idx);
				if (p == null) return -1;
				return changed != null && changed.TryGetValue(idx, out byte pti) ? PositionChangeHandlers.OrderOf(p, pti) : p.PreferredOrder;
			}
			CardInfo[] order = l.Batters.Take(9).Select(i => i != 0 ? catalog.Get(i) : null).ToArray();
			double[] graph = BatterGraph(order);
			for (int slot = 0; slot < 9; slot++)
			{
				GameCard g = t.Order[slot];
				if (g == null || order[slot] == null) continue;
				double share = FromGraph(graph[slot]);
				int pos = t.Position[slot];
				if (pos < 8 && !order[slot].CanField(pos))
				{
					share = (1 + share) * (1 + OutOfPosition) - 1;
				}
				Scale(g, share);
			}
			// The pitchers who can appear: today's starter and the bullpen.
			int sp = t.StarterSlot;
			if (sp >= 0 && sp < 5 && t.Pitchers[sp] != null) Scale(t.Pitchers[sp], PitcherShare(0, Order(l.Pitchers[sp])));
			for (int s = 5; s < Lineup.PitcherSlots; s++)
			{
				if (t.Pitchers[s] != null) Scale(t.Pitchers[s], PitcherShare(s - 4, Order(l.Pitchers[s])));
			}
		}

		// For logs: the batting graph per slot, its mean, and fielders out of position.
		public static string Describe(Lineup l, CardCatalog catalog)
		{
			CardInfo[] order = l.Batters.Take(9).Select(i => i != 0 ? catalog.Get(i) : null).ToArray();
			double[] graph = BatterGraph(order);
			int[] posOf = new int[9];
			for (int pos = 0; pos < Lineup.FielderSlots; pos++) posOf[l.Fielders[pos] - 1] = pos;
			int outOf = Enumerable.Range(0, 9).Count(s => order[s] != null && posOf[s] < 8 && !order[s].CanField(posOf[s]));
			return $"graph mean {graph.Average():F0} [{string.Join(" ", graph.Select(v => v.ToString("F0")))}], out of position {outOf}";
		}

		private static void Scale(GameCard g, double share)
		{
			if (share != 0) g.Condition = (1 + g.Condition) * (1 + share) - 1;
		}
	}
}
