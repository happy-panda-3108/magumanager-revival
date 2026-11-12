using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 자동 편성's team colour plans (engine session, 2026-11-12; user 10:4x: "팀컬로를 유지하면서 바꿀지, 수집현황에 따라 높은
	// 팀컬러로 변경하면서 선수를 갈아치울지, 이런 연산이 많이 들어가는데 이를 최적화해서 계산한 다음 자동으로 선수단을 바꿀 수는
	// 없을까??", and "적당히 깡스탯 높은 선수 + 저볼 선수들은 팀컬러 유지 이걸로 루키 리그를 미는게 정석").
	// Every team colour the club's cards can make is a plan: a group (each kind's biggest groups of the cards) and the members
	// it needs among the 14 batters and among the 11 pitchers (each grade's count, or none on a side). A plan's 1군 is the
	// greedy one (Greedy) with at least those members, the members valued with the bonus they would get, so the cheapest
	// members good enough fill the count and the balls left go to the strongest cards. All plans are screened at the free
	// 1군's ball price, the best are built under the cap and valued whole (TeamValue), and the best of those are improved.
	public static partial class LeagueService
	{
		private sealed class ColourPlan
		{
			public string Name;
			public int Kind, Group;                             // kind index 0-6 and the group as TeamColors numbers it
			public Func<CardInfo, bool> MemberBat, MemberPit;   // null: no count on that side
			public int NeedBat, NeedPit, BonusBat, BonusPit;
			public Dictionary<int, CardValue> Valued = new Dictionary<int, CardValue>();   // members' values with the bonus
			public double Screen;
		}

		// The biggest groups per kind (0 단일 년도 팀 … 6 듀얼 팀 올스타) as TeamColors numbers them.
		private static List<(int kind, int group)> ColourGroups(List<CardInfo> cards)
		{
			List<CardInfo> normal = cards.Where(c => !c.IsLegend).ToList();
			List<(int, int)> r = new List<(int, int)>();
			int Yy(int year) => year % 100;
			void Top(int kind, IEnumerable<(int group, int n)> groups, int count)
			{
				foreach ((int group, int n) g in groups.OrderByDescending(g => g.n).ThenBy(g => g.group).Take(count)) r.Add((kind, g.group));
			}
			IEnumerable<(T a, T b)> Pairs<T>(List<T> l)
			{
				for (int i = 0; i < l.Count; i++) for (int j = i + 1; j < l.Count; j++) yield return (l[i], l[j]);
			}
			Top(0, normal.GroupBy(c => (c.TeamCode, c.Year)).Select(g => (g.Key.TeamCode * 100 + Yy(g.Key.Year), g.Count())), 8);
			// 단일 년도 듀얼 팀: a season's three biggest clubs in pairs.
			Top(1, normal.GroupBy(c => c.Year).SelectMany(y =>
			{
				List<(int team, int n)> t = y.GroupBy(c => c.TeamCode).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList();
				return Pairs(t).Select(p => (Yy(y.Key) * 10000 + Math.Min(p.a.team, p.b.team) * 100 + Math.Max(p.a.team, p.b.team), p.a.n + p.b.n));
			}), 4);
			// 듀얼 년도 단일 팀: a lineage's three biggest seasons in pairs.
			Top(2, normal.GroupBy(c => TeamColors.Lineage(c.TeamCode)).SelectMany(t =>
			{
				List<(int year, int n)> y = t.GroupBy(c => c.Year).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList();
				return Pairs(y).Select(p => (t.Key * 10000 + Yy(Math.Min(p.a.year, p.b.year)) * 100 + Yy(Math.Max(p.a.year, p.b.year)), p.a.n + p.b.n));
			}), 4);
			Top(3, normal.GroupBy(c => (line: TeamColors.Lineage(c.TeamCode), dec: c.Year / 10)).Select(g => (g.Key.line * 100 + g.Key.dec % 100, g.Count())), 5);
			Top(4, normal.GroupBy(c => c.Year).Select(g => (Yy(g.Key), g.Count())), 3);
			Top(5, normal.GroupBy(c => TeamColors.Lineage(c.TeamCode)).Select(g => (g.Key, g.Count())), 3);
			List<(int line, int n)> lines = normal.GroupBy(c => TeamColors.Lineage(c.TeamCode)).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList();
			Top(6, Pairs(lines).Select(p => (Math.Min(p.a.line, p.b.line) * 100 + Math.Max(p.a.line, p.b.line), p.a.n + p.b.n)), 3);
			return r;
		}

		// Per group, every count it can reach: batters at a grade or none, pitchers at a grade or none (not both none).
		private static List<ColourPlan> ColourPlans(LineupContext x, Dictionary<int, CardValue> values)
		{
			List<CardInfo> cards = values.Values.Select(v => v.Info).ToList();
			List<ColourPlan> plans = new List<ColourPlan>();
			string[] grades = { "S", "A", "B", "C" };
			foreach ((int kind, int group) in ColourGroups(cards))
			{
				int any = TeamColors.InstanceOf(kind, 0, 0, group);
				bool Member(CardInfo c) => TeamColors.IsMemberOf(any, c);
				int bats = cards.Count(c => c.IsBatter && Member(c)), pits = cards.Count(c => !c.IsBatter && Member(c));
				for (int gh = -1; gh < 4; gh++)
				{
					for (int gp = -1; gp < 4; gp++)
					{
						if (gh < 0 && gp < 0) continue;
						// Members needed by kind since 2026-11-12 (systems session: 연대 단일 팀 has steps of its own, TeamColors.Need).
						int nb = gh < 0 ? 0 : TeamColors.NeededFor(kind, 1, gh), np = gp < 0 ? 0 : TeamColors.NeededFor(kind, 2, gp);
						if (nb > bats || np > pits) continue;
						int gc = Enumerable.Range(0, 4).Where(g => nb + np >= TeamColors.NeededFor(kind, 0, g)).DefaultIfEmpty(-1).First();
						int whole = gc < 0 ? 0 : TeamColors.Value(kind, 0, gc);
						int bb = gh < 0 ? 0 : TeamColors.Value(kind, 1, gh) + whole, bp = gp < 0 ? 0 : TeamColors.Value(kind, 2, gp) + whole;
						// A count worth nothing on a side, or a side's count that a lower count gives as much: skip.
						if ((gh >= 0 && bb == 0) || (gp >= 0 && bp == 0)) continue;
						if (gh >= 0 && gh < 3 && SameBonus(kind, 1, gh, gp, nb, np)) continue;
						if (gp >= 0 && gp < 3 && SameBonus(kind, 2, gp, gh, np, nb)) continue;
						string label = TeamColors.Label(TeamColors.InstanceOf(kind, 0, 0, group)).name;
						plans.Add(new ColourPlan
						{
							Name = $"{label} 타자 {(gh < 0 ? "-" : grades[gh])}·투수 {(gp < 0 ? "-" : grades[gp])}",
							Kind = kind, Group = group,
							MemberBat = gh < 0 ? null : Member, MemberPit = gp < 0 ? null : Member,
							NeedBat = nb, NeedPit = np, BonusBat = bb, BonusPit = bp
						});
					}
				}
			}
			foreach (ColourPlan p in plans)
			{
				foreach (CardValue c in values.Values)
				{
					Func<CardInfo, bool> m = c.Info.IsBatter ? p.MemberBat : p.MemberPit;
					int bonus = c.Info.IsBatter ? p.BonusBat : p.BonusPit;
					if (m == null || !m(c.Info)) continue;
					CardValue v = ValueOf(x, c.Info, bonus);
					if (v != null) p.Valued[c.Info.Idx] = v;
				}
			}
			return plans;
		}

		// Whether one grade lower on this side gives its members the same (the next grade's count is cheaper).
		private static bool SameBonus(int kind, int slot, int g, int otherGrade, int n, int otherN)
		{
			int lower = g + 1, nLower = TeamColors.NeededFor(kind, slot, lower);
			int Whole(int total) => Enumerable.Range(0, 4).Where(q => total >= TeamColors.NeededFor(kind, 0, q)).Select(q => TeamColors.Value(kind, 0, q)).DefaultIfEmpty(0).First();
			return TeamColors.Value(kind, slot, g) + Whole(n + otherN) <= TeamColors.Value(kind, slot, lower) + Whole(nLower + otherN);
		}

		// The plans' 1군s: all screened at the free 1군's ball price (λ, one greedy each, valued by the cards' own values less λ
		// for each ball over the cap), the best `build` built under the cap and valued whole; returns those, best first. With
		// keep only the plans of the registered entries' group (when they are all of one kind and group).
		private static List<(string name, Lineup l, double runs)> PlanStarts(LineupContext x, Dictionary<int, CardValue> values, double lambda, int build, int[] keep)
		{
			List<ColourPlan> plans = ColourPlans(x, values);
			List<(int kind, int group)> kept = (keep ?? Array.Empty<int>()).Where(e => e != 0).Select(e => (e / 100_000_000 - 1, e % 1_000_000)).Distinct().ToList();
			if (kept.Count == 1) plans = plans.Where(p => p.Kind == kept[0].kind && p.Group == kept[0].group).ToList();
			foreach (ColourPlan p in plans)
			{
				Lineup l = Greedy(x, values, x.Current, null, 0, p, lambda);
				p.Screen = l == null ? double.NegativeInfinity : PlanScore(x, values, p, l) - lambda * (RosterHandlers.LineupBalls(l) - x.Cap);
			}
			List<(string, Lineup, double)> r = new List<(string, Lineup, double)>();
			foreach (ColourPlan p in plans.Where(p => !double.IsNegativeInfinity(p.Screen)).OrderByDescending(p => p.Screen).Take(build))
			{
				Lineup l = Greedy(x, values, x.Current, null, 0, p);
				if (l == null || !Allowed(x, l, null)) continue;
				r.Add((p.Name, l, Evaluate(x, l, out _).Runs));
			}
			return r.OrderByDescending(t => t.Item3).ToList();
		}

		// Per Improve pass: for each slot (side 0 batters, 1 pitchers) and ball count, the two best cards outside the 1군 of
		// that many balls or fewer by own value there, among all and among the members of the 1군's team colour groups.
		private sealed class CheapTable
		{
			public int MaxBall;
			public List<CardValue>[][][] All = new List<CardValue>[2][][], Member = new List<CardValue>[2][][];
		}

		private static CheapTable Cheap(LineupContext x, Dictionary<int, CardValue> values, Lineup l, HashSet<int> inside, Dictionary<int, int> colourBonus)
		{
			CheapTable t = new CheapTable { MaxBall = values.Values.Select(c => c.Info.Ball).DefaultIfEmpty(0).Max() };
			for (int side = 0; side < 2; side++)
			{
				bool pit = side == 1;
				int slots = pit ? l.Pitchers.Length : l.Batters.Length;
				t.All[side] = new List<CardValue>[slots][];
				t.Member[side] = new List<CardValue>[slots][];
				List<(CardValue c, bool member)> outside = values.Values.Where(c => c.Info.IsBatter != pit && !inside.Contains(c.Info.Idx))
					.Select(c => (c, colourBonus.Count > 0 && SameGroupAsColoured(c.Info, l, colourBonus))).ToList();
				for (int k = 0; k < slots; k++)
				{
					List<(CardValue c, double v, bool m)> scored = outside.Where(o => !(pit && k < 5 && x.Blocked.Contains(o.c.Info.Idx)))
						.Select(o => (o.c, SlotValue(o.c, l, k, pit), o.member)).ToList();
					t.All[side][k] = Prefix(scored, t.MaxBall);
					t.Member[side][k] = Prefix(scored.Where(s => s.m).ToList(), t.MaxBall);
				}
			}
			return t;
		}

		private static List<CardValue>[] Prefix(List<(CardValue c, double v, bool m)> scored, int maxBall)
		{
			List<CardValue>[] r = new List<CardValue>[maxBall + 1];
			List<(CardValue c, double v)> top = new List<(CardValue, double)>();
			foreach (IGrouping<int, (CardValue c, double v, bool m)> g in scored.GroupBy(s => s.c.Info.Ball).OrderBy(g => g.Key))
			{
				int b = Math.Clamp(g.Key, 0, maxBall);
				top = top.Concat(g.Select(s => (s.c, s.v))).OrderByDescending(s => s.Item2).Take(3).ToList();
				r[b] = top.Select(s => s.Item1).ToList();
			}
			for (int b = 0; b <= maxBall; b++) r[b] ??= b > 0 ? r[b - 1] : new List<CardValue>();
			return r;
		}

		// A change over the cap paid for by the slot whose change to a card of fewer balls (a member for a member of the team
		// colours) loses the least own value; null when none pays.
		private static Lineup PayBalls(LineupContext x, Dictionary<int, CardValue> values, CheapTable cheap, Lineup t, bool changedPit, int changedK, CardValue changed, Dictionary<int, int> colourBonus)
		{
			int over = RosterHandlers.LineupBalls(t) - x.Cap;
			if (over <= 0) return t;
			int bestSide = -1, bestK = -1;
			CardValue bestCard = null;
			double bestLoss = double.PositiveInfinity;
			for (int side = 0; side < 2; side++)
			{
				bool pit = side == 1;
				int[] row = pit ? t.Pitchers : t.Batters;
				for (int k = 0; k < row.Length; k++)
				{
					if ((pit == changedPit && k == changedK) || row[k] == 0 || x.LockedIdx.Contains(row[k])) continue;
					if (!values.TryGetValue(row[k], out CardValue cur)) continue;
					int limit = cur.Info.Ball - over;
					if (limit < 0) continue;
					List<CardValue> pool = (colourBonus.ContainsKey(cur.Info.Idx) ? cheap.Member : cheap.All)[side][k][Math.Min(limit, cheap.MaxBall)];
					CardValue c = pool.FirstOrDefault(p => p.Info.Idx != changed.Info.Idx && PersonOf(p.Info) != PersonOf(changed.Info));
					if (c == null) continue;
					double loss = SlotValue(cur, t, k, pit) - SlotValue(c, t, k, pit);
					if (loss < bestLoss) { bestLoss = loss; bestSide = side; bestK = k; bestCard = c; }
				}
			}
			if (bestCard == null) return null;
			Lineup r = Clone(t);
			(bestSide == 1 ? r.Pitchers : r.Batters)[bestK] = bestCard.Info.Idx;
			return r;
		}

		// A 1군's value by the cards' own values (no team colour), to order Improve's tries.
		private static double OwnScore(Dictionary<int, CardValue> values, Lineup l)
		{
			double s = 0;
			for (int k = 0; k < l.Batters.Length; k++) if (l.Batters[k] != 0 && values.TryGetValue(l.Batters[k], out CardValue c)) s += SlotValue(c, l, k, false);
			for (int k = 0; k < l.Pitchers.Length; k++) if (l.Pitchers[k] != 0 && values.TryGetValue(l.Pitchers[k], out CardValue c)) s += SlotValue(c, l, k, true);
			return s;
		}

		// A 1군's value by the cards' own values (members with the plan's bonus).
		private static double PlanScore(LineupContext x, Dictionary<int, CardValue> values, ColourPlan p, Lineup l)
		{
			double s = 0;
			for (int k = 0; k < l.Batters.Length; k++)
			{
				if (l.Batters[k] == 0 || !values.TryGetValue(l.Batters[k], out CardValue c)) continue;
				s += SlotValue(p.Valued.GetValueOrDefault(c.Info.Idx) ?? c, l, k, false);
			}
			for (int k = 0; k < l.Pitchers.Length; k++)
			{
				if (l.Pitchers[k] == 0 || !values.TryGetValue(l.Pitchers[k], out CardValue c)) continue;
				s += SlotValue(p.Valued.GetValueOrDefault(c.Info.Idx) ?? c, l, k, true);
			}
			return s;
		}
	}
}
