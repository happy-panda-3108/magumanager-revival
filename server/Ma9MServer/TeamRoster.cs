using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// One owned card. The client identifies owned players by card idx, so a team holds at most
	// one copy of each card.
	public sealed class TeamCard
	{
		public int Idx;

		public byte RemainContract;

		public byte PositionTrainingInfo;

		public bool IsLock;

		// Six stat bonuses each (batter: con, pow, spd, eye, def, mental; pitcher: health, spd,
		// cont, technic, breakball, mental).
		public sbyte[] Educated = new sbyte[6];

		public sbyte[] Contract = new sbyte[6];

		public sbyte[] Training = new sbyte[6];

		public sbyte[] TeamColor = new sbyte[6];

		// 코칭스태프 (CoachHandlers.FillBonus when the cards are read; not stored).
		public sbyte[] Coach = new sbyte[6];

		// Contract (ContractHandlers): the first day it is expired, and renewals so far.
		public DateTime ContractUntil;

		public int ContractCount;
	}

	// Mirrors PKS_SC_ENTER_ROSTER_INFO.
	public sealed class Lineup
	{
		public const int BatterSlots = 14;  // batting order 1-9, then bench 1-5

		public const int FielderSlots = 9;  // C, 1B, 2B, 3B, SS, LF, CF, RF, DH -> batting order + 1

		public const int PitcherSlots = 11; // SP1-5, RP1-4, SU, CP

		public int[] Batters = new int[BatterSlots];

		public byte[] Fielders = new byte[FielderSlots];

		public int[] Pitchers = new int[PitcherSlots];

		public byte NextSp;

		public string Serialize()
		{
			return string.Join(",", Batters) + "|" + string.Join(",", Fielders) + "|" + string.Join(",", Pitchers) + "|" + NextSp;
		}

		public static Lineup Parse(string text)
		{
			string[] parts = text.Split('|');
			return new Lineup
			{
				Batters = parts[0].Split(',').Select(int.Parse).ToArray(),
				Fielders = parts[1].Split(',').Select(byte.Parse).ToArray(),
				Pitchers = parts[2].Split(',').Select(int.Parse).ToArray(),
				NextSp = byte.Parse(parts[3])
			};
		}

		// Valid when every listed card is owned, each position has a distinct batter in the
		// batting order, the five starting pitchers are set and no card appears twice.
		public bool IsValid(ISet<int> owned)
		{
			if (Batters.Length != BatterSlots || Fielders.Length != FielderSlots || Pitchers.Length != PitcherSlots)
			{
				return false;
			}
			IEnumerable<int> used = Batters.Concat(Pitchers).Where(i => i != 0);
			if (used.Any(i => !owned.Contains(i)) || used.Count() != used.Distinct().Count())
			{
				return false;
			}
			return Fielders.All(f => f >= 1 && f <= 9) && Fielders.Distinct().Count() == FielderSlots
				&& Batters.Take(9).All(i => i != 0) && Pitchers.Take(5).All(i => i != 0);
		}
	}

	public static class LineupBuilder
	{
		// Fill positions with the best available fielder, scarcest positions first.
		private static readonly int[] FieldOrder = { 0, 4, 2, 6, 3, 7, 5, 1 }; // C, SS, 2B, CF, 3B, RF, LF, 1B

		public static Lineup Build(IEnumerable<int> ownedCards, CardCatalog catalog)
		{
			List<CardInfo> cards = ownedCards.Select(catalog.Get).Where(c => c != null).ToList();
			List<CardInfo> batters = cards.Where(c => c.IsBatter).OrderByDescending(c => c.Overall).ToList();
			List<CardInfo> pitchers = cards.Where(c => !c.IsBatter).OrderByDescending(c => c.Overall).ToList();
			Lineup lineup = new Lineup();

			CardInfo[] atPosition = new CardInfo[Lineup.FielderSlots];
			HashSet<int> taken = new HashSet<int>();
			// Each spot's own man first (main position, CardPos 1-8 = C..RF), then anyone who can play
			// it, and only then anyone (out of position plays worse: LineupEffects).
			foreach (int pos in FieldOrder)
			{
				CardInfo pick = batters.Where(b => !taken.Contains(b.Idx) && (int)b.MainPosition == pos + 1).OrderByDescending(b => b.Overall).FirstOrDefault();
				if (pick != null)
				{
					atPosition[pos] = pick;
					taken.Add(pick.Idx);
				}
			}
			foreach (int pos in FieldOrder)
			{
				if (atPosition[pos] != null) continue;
				CardInfo pick = batters.Where(b => !taken.Contains(b.Idx) && b.CanField(pos)).OrderByDescending(b => b.Defence[pos] + b.Overall).FirstOrDefault()
					?? batters.FirstOrDefault(b => !taken.Contains(b.Idx));
				if (pick != null)
				{
					atPosition[pos] = pick;
					taken.Add(pick.Idx);
				}
			}
			// A spot left to anyone (no free man could play it) is played by a starter who can, moved there, his spot
			// refilled the same way down to a free man who can (Refill). User, 2026-10-31: 2009 SK's only centre fielder,
			// 박재홍 (RF 63 / CF 54, main RF), took right field in the first pass, so the DH 김재현 (20 everywhere) played
			// centre ("수비 못 하는 중견수에게 졌다").
			foreach (int pos in FieldOrder)
			{
				if (atPosition[pos] != null && !atPosition[pos].CanField(pos)) atPosition = Refill(atPosition, pos, batters);
			}
			taken = new HashSet<int>(atPosition.Take(8).Where(c => c != null).Select(c => c.Idx));
			atPosition[8] = batters.FirstOrDefault(b => !taken.Contains(b.Idx)); // DH
			if (atPosition[8] != null)
			{
				taken.Add(atPosition[8].Idx);
			}

			// Batting order: the best 선수분석 graph (선호타순 and neighbours; it plays ±5%), better
			// hitters higher where that ties.
			List<CardInfo> starters = atPosition.Where(c => c != null).ToList();
			List<CardInfo> order = BestOrder(starters);

			for (int i = 0; i < order.Count && i < 9; i++)
			{
				lineup.Batters[i] = order[i].Idx;
			}
			int bench = 9;
			foreach (CardInfo b in batters.Where(b => !taken.Contains(b.Idx)).Take(5))
			{
				lineup.Batters[bench++] = b.Idx;
			}
			for (int pos = 0; pos < Lineup.FielderSlots; pos++)
			{
				lineup.Fielders[pos] = (byte)(atPosition[pos] == null ? pos + 1 : order.IndexOf(atPosition[pos]) + 1);
			}

			// Every slot its own role first, then the nearest, then anyone: SP1-5 starters, CP the closer,
			// SU the set-up man, RP1-4 the middle relievers (user, 2026-10-22: "중계 투수가 셋업에 뛰고 있고" —
			// the SU slot took the best of SU and RP, so a better RP card stood in for the set-up man and
			// both played out of role, LineupEffects.OutOfRole).
			HashSet<int> usedPitchers = new HashSet<int>();
			int TakePitcher(params Func<CardInfo, bool>[] preferred)
			{
				CardInfo pick = null;
				foreach (Func<CardInfo, bool> want in preferred)
				{
					pick = pitchers.FirstOrDefault(p => !usedPitchers.Contains(p.Idx) && want(p));
					if (pick != null) break;
				}
				pick ??= pitchers.FirstOrDefault(p => !usedPitchers.Contains(p.Idx));
				if (pick == null)
				{
					return 0;
				}
				usedPitchers.Add(pick.Idx);
				return pick.Idx;
			}
			for (int i = 0; i < 5; i++)
			{
				lineup.Pitchers[i] = TakePitcher(p => p.MainPosition == CardPos.SP);
			}
			lineup.Pitchers[10] = TakePitcher(p => p.MainPosition == CardPos.CP, p => p.MainPosition == CardPos.SU);
			lineup.Pitchers[9] = TakePitcher(p => p.MainPosition == CardPos.SU, p => p.MainPosition == CardPos.RP || p.MainPosition == CardPos.CP);
			for (int i = 5; i < 9; i++)
			{
				lineup.Pitchers[i] = TakePitcher(p => p.MainPosition == CardPos.RP, p => p.MainPosition != CardPos.SP);
			}
			return lineup;
		}

		// The fielders (spots C..RF) with spot `pos` played by a man who can: a free batter who can, or a starter who can
		// moved there and his spot refilled the same way (no spot twice in one chain). Of every chain that ends in a free
		// man, the one with most spots played by men who can, then the best nine (the DH taken again as the best free
		// batter), then the most defence. Unchanged when no chain ends.
		private static CardInfo[] Refill(CardInfo[] at, int pos, List<CardInfo> batters)
		{
			CardInfo[] best = at;
			double bestScore = Score(at);
			int nodes = 0;
			double Score(CardInfo[] a)
			{
				HashSet<int> on = new HashSet<int>(a.Take(8).Where(c => c != null).Select(c => c.Idx));
				double s = 0;
				for (int p = 0; p < 8; p++)
				{
					if (a[p] == null) continue;
					s += a[p].Overall + (a[p].CanField(p) ? 1000 + 0.01 * a[p].Defence[p] : 0);
				}
				CardInfo dh = batters.FirstOrDefault(b => !on.Contains(b.Idx));
				return s + (dh?.Overall ?? 0);
			}
			void Search(CardInfo[] cur, int spot, HashSet<int> settled)
			{
				foreach (CardInfo b in batters.Where(b => b.CanField(spot)).OrderByDescending(b => b.Defence[spot] + b.Overall).Take(6))
				{
					if (++nodes > 20000) return;
					int from = Array.FindIndex(cur, 0, 8, c => c != null && c.Idx == b.Idx);
					if (from == spot || settled.Contains(from)) continue;
					CardInfo[] next = (CardInfo[])cur.Clone();
					next[spot] = b;
					if (from < 0)
					{
						double s = Score(next);
						if (s > bestScore)
						{
							bestScore = s;
							best = next;
						}
						continue;
					}
					next[from] = null;
					Search(next, from, new HashSet<int>(settled) { spot });
				}
			}
			Search(at, pos, new HashSet<int> { pos });
			return best;
		}

		// The order of the starters with the highest 선수분석 total (LineupEffects.BatterGraph), plus a
		// small pull for better hitters to bat higher. All orders of nine are tried (9! = 362,880) on
		// precomputed slot bases and neighbour factors.
		public static List<CardInfo> BestOrder(List<CardInfo> starters)
		{
			int n = starters.Count;
			if (n < 2) return starters.ToList();
			double[,] slotBase = new double[n, 9], pair = new double[n, n];
			for (int i = 0; i < n; i++)
			{
				for (int m = 0; m < 9; m++)
				{
					CardInfo[] one = new CardInfo[9];
					one[m] = starters[i];
					slotBase[i, m] = LineupEffects.BatterGraph(one)[m] / 1.0526316 + 0.1 * starters[i].Overall * (9 - m) / 9.0;
				}
				for (int j = 0; j < n; j++)
				{
					if (i == j) continue;
					CardInfo[] two = new CardInfo[9];
					two[0] = starters[i];
					two[1] = starters[j];
					double alone = LineupEffects.BatterGraph(new[] { starters[i], null, null, null, null, null, null, null, null })[0];
					pair[i, j] = alone > 0 ? Math.Min(100, LineupEffects.BatterGraph(two)[0]) / alone : 1;
				}
			}
			// Exact graph of one order: base × both neighbours' factors (capped at 100 like the client).
			double Score(int[] p)
			{
				double s = 0;
				for (int m = 0; m < n; m++)
				{
					int cur = p[m], next = p[(m + 1) % n], prev = p[(m + n - 1) % n];
					double quality = 0.1 * starters[cur].Overall * (9 - m) / 9.0;
					double graph = (slotBase[cur, m] - quality) * 1.0526316 * pair[cur, next] * pair[cur, prev];
					s += Math.Min(100, graph) + quality;
				}
				return s;
			}
			int[] perm = Enumerable.Range(0, n).ToArray(), best = (int[])perm.Clone();
			double bestScore = Score(perm);
			// Heap's algorithm over every order.
			int[] c = new int[n];
			int k = 0;
			while (k < n)
			{
				if (c[k] < k)
				{
					int a = k % 2 == 0 ? 0 : c[k];
					(perm[a], perm[k]) = (perm[k], perm[a]);
					double sc = Score(perm);
					if (sc > bestScore)
					{
						bestScore = sc;
						best = (int[])perm.Clone();
					}
					c[k]++;
					k = 0;
				}
				else
				{
					c[k] = 0;
					k++;
				}
			}
			return best.Select(i => starters[i]).ToList();
		}
	}
}
