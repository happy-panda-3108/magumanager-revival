using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// A 25-card club drawn from the real cards: a regular at every position, six more bats, five
	// starters, four relievers, a set-up man and a closer, with the balls adding up inside [lo, hi].
	// Fixed cards (the key players picked at team creation) take their slots first and are never
	// swapped out. Used for new teams' first rosters and for the league's AI clubs.
	public static class StarterTeam
	{
		private static readonly (Func<CardInfo, bool> want, int count)[] Slots =
		{
			(c => c.IsBatter && c.MainPosition == CardPos.C, 2), (c => c.IsBatter && c.MainPosition == CardPos.B1, 1), (c => c.IsBatter && c.MainPosition == CardPos.B2, 1),
			(c => c.IsBatter && c.MainPosition == CardPos.B3, 1), (c => c.IsBatter && c.MainPosition == CardPos.SS, 1), (c => c.IsBatter && c.MainPosition == CardPos.LF, 1),
			(c => c.IsBatter && c.MainPosition == CardPos.CF, 1), (c => c.IsBatter && c.MainPosition == CardPos.RF, 1), (c => c.IsBatter, 5),
			(c => !c.IsBatter && c.MainPosition == CardPos.SP, 5), (c => !c.IsBatter && c.MainPosition == CardPos.RP, 4),
			(c => !c.IsBatter && c.MainPosition == CardPos.SU, 1), (c => !c.IsBatter && c.MainPosition == CardPos.CP, 1)
		};

		// takenPlayers: players (sidx) to leave out, e.g. those already on another fixed AI club of the tier.
		public static List<CardInfo> Draw(Random rnd, CardCatalog catalog, int lo, int hi, IEnumerable<CardInfo> fixedCards = null, ISet<int> takenPlayers = null)
		{
			List<CardInfo> all = catalog.Drawable.ToList();  // no 레전드 (CardCatalog.Drawable)
			List<CardInfo> fixedList = (fixedCards ?? Enumerable.Empty<CardInfo>()).Where(c => c != null).ToList();
			int target = rnd.Next(lo, hi + 1);
			List<(Func<CardInfo, bool> want, CardInfo card, bool locked)> picks = new List<(Func<CardInfo, bool>, CardInfo, bool)>();
			HashSet<int> players = new HashSet<int>(fixedList.Select(c => c.Sidx));
			if (takenPlayers != null)
			{
				players.UnionWith(takenPlayers);
			}
			// Expand the slot list, then put each fixed card in the first slot it fits (any bat or arm as a fallback).
			List<Func<CardInfo, bool>> open = Slots.SelectMany(s => Enumerable.Repeat(s.want, s.count)).ToList();
			foreach (CardInfo f in fixedList)
			{
				int k = open.FindIndex(w => w(f));
				if (k < 0) k = open.FindIndex(w => w == (f.IsBatter ? Slots[8].want : Slots[10].want));  // a spare bat or a relief arm
				if (k < 0) continue;
				picks.Add((open[k], f, true));
				open.RemoveAt(k);
			}
			int fixedBalls = fixedList.Sum(c => c.Ball);
			double mean = Math.Max(1, (target - fixedBalls) / (double)Math.Max(1, open.Count));
			CardInfo Pick(Func<CardInfo, bool> want, int ball)
			{
				for (int spread = 0; spread < 10; spread++)
				{
					List<CardInfo> pool = all.Where(c => want(c) && Math.Abs(c.Ball - ball) <= spread && !players.Contains(c.Sidx)).ToList();
					if (pool.Count > 0) return pool[rnd.Next(pool.Count)];
				}
				return all.First(c => want(c) && !players.Contains(c.Sidx));
			}
			foreach (Func<CardInfo, bool> want in open)
			{
				CardInfo c = Pick(want, (int)Math.Round(mean + rnd.Next(-2, 3)));
				players.Add(c.Sidx);
				picks.Add((want, c, false));
			}
			// Nudge unlocked cards a ball up or down until the total lands in range.
			for (int round = 0; round < 400; round++)
			{
				int sum = picks.Sum(p => p.card.Ball);
				if (sum >= lo && sum <= hi) break;
				int k = rnd.Next(picks.Count);
				if (picks[k].locked) continue;
				int want = picks[k].card.Ball + (sum < lo ? 1 : -1);
				if (want < 1 || want > 10) continue;
				players.Remove(picks[k].card.Sidx);
				List<CardInfo> pool = all.Where(c => picks[k].want(c) && c.Ball == want && !players.Contains(c.Sidx)).ToList();
				CardInfo next = pool.Count > 0 ? pool[rnd.Next(pool.Count)] : picks[k].card;
				players.Add(next.Sidx);
				picks[k] = (picks[k].want, next, false);
			}
			return picks.Select(p => p.card).ToList();
		}

		// The clubs a new team can pick (eTeamName = the emblem the client sends), with the card
		// team codes that count as that club: 기아 includes 해태; SSG is SK's code, 키움 넥센's.
		// 현대 too since 2026-10-24 (user); its cards are 1999-2007 only. 쌍방울 (11) since 2026-11-02 (user), cards 1993-1999:
		// few low-ball shortstops and closers, so a slot the club cannot fill takes another club's card (DrawClub).
		public static int[] ClubCodes(int club) => club switch
		{
			5 => new[] { 5, 1 },
			_ => new[] { club }
		};

		public static readonly int[] Clubs = { 7, 5, 0, 6, 13, 8, 9, 3, 19, 2, 4, 11 };  // 롯데 기아 두산 LG NC 삼성 SSG 키움 KT 한화 현대 쌍방울

		// A new team's temporary club (user, 2026-10-18): the key players, then cards at 1-4볼 (노멀) with two or three at 5볼;
		// relievers stand in for the scarce low-ball set-up men and closers. Until 2026-11-12 they were the chosen club's own (another
		// club's only when it had none left), so 현대·쌍방울 (one decade of cards) started with a 연대 팀컬러 S and beat the 비기너 AI
		// with no pack opened. Now (user 11-10 결정 4 and 11-12 11:5x 8번: "시작덱은 키 플레이어 2명 외 무작위 팀이 만들어지게끔 해야함.
		// 그래서 기본 선수팀으로는 이길 수 없으니 카드팩을 통한 기초적인 덱 보강을 하고 경기를 임해야함"): random cards of every club and
		// season, drawn again (up to 30 times) while they reach any team colour.
		public static List<CardInfo> DrawClub(Random rnd, CardCatalog catalog, int club, IEnumerable<CardInfo> keys)
		{
			List<CardInfo> picks = null;
			for (int attempt = 0; attempt < 30; attempt++)
			{
				picks = DrawRandomClub(rnd, catalog, keys);
				if (!TeamColors.AnyAvailable(picks.Where(c => c.IsBatter).ToList(), picks.Where(c => !c.IsBatter).ToList())) break;
			}
			return picks;
		}

		private static List<CardInfo> DrawRandomClub(Random rnd, CardCatalog catalog, IEnumerable<CardInfo> keys)
		{
			List<CardInfo> all = catalog.Drawable.ToList();  // no 레전드 (CardCatalog.Drawable)
			List<CardInfo> keyList = keys.Where(c => c != null).ToList();
			List<CardInfo> picks = new List<CardInfo>(keyList);
			HashSet<int> players = new HashSet<int>(keyList.Select(c => c.Sidx));
			bool Relief(CardInfo c) => !c.IsBatter && c.MainPosition != CardPos.SP;
			List<Func<CardInfo, bool>> open = Slots.SelectMany(s => Enumerable.Repeat(s.want, s.count))
				.Select(w => w == Slots[11].want || w == Slots[12].want ? (Func<CardInfo, bool>)Relief : w).ToList();
			foreach (CardInfo k in keyList)
			{
				int at = open.FindIndex(w => w(k));
				if (at < 0) at = open.FindIndex(w => w == (k.IsBatter ? Slots[8].want : (Func<CardInfo, bool>)Relief));
				if (at >= 0) open.RemoveAt(at);
			}
			int fives = rnd.Next(2, 4);
			HashSet<int> fiveSlots = new HashSet<int>(Enumerable.Range(0, open.Count).OrderBy(_ => rnd.Next()).Take(fives));
			for (int i = 0; i < open.Count; i++)
			{
				Func<CardInfo, bool> want = open[i];
				Func<CardInfo, bool> ball = fiveSlots.Contains(i) ? c => c.Ball == 5 : c => c.Ball >= 1 && c.Ball <= 4;
				CardInfo pick = null;
				foreach (IEnumerable<CardInfo> pool in new[] { all.Where(c => want(c) && ball(c)), all.Where(c => want(c) && c.Ball <= 5) })
				{
					List<CardInfo> free = pool.Where(c => !players.Contains(c.Sidx)).ToList();
					if (free.Count > 0)
					{
						pick = free[rnd.Next(free.Count)];
						break;
					}
				}
				if (pick == null) continue;
				players.Add(pick.Sidx);
				picks.Add(pick);
			}
			return picks;
		}

		// The lineup for a roster with the key players starting: a key batter at his main position
		// (or DH), a key pitcher in his role's slot.
		public static Lineup LineupWith(IEnumerable<int> roster, IEnumerable<CardInfo> keys, CardCatalog catalog)
		{
			Lineup l = LineupBuilder.Build(roster, catalog);
			foreach (CardInfo k in keys.Where(c => c != null))
			{
				if (k.IsBatter)
				{
					if (Array.IndexOf(l.Batters, k.Idx) is int at && at >= 0 && at < 9) continue;
					// Take the batting slot of whoever plays his position (DH when he has none in the field).
					int pos = PositionIndex(k.MainPosition);
					int slot = l.Fielders[pos] - 1;
					int bench = Array.IndexOf(l.Batters, k.Idx);
					int replaced = l.Batters[slot];
					l.Batters[slot] = k.Idx;
					if (bench >= 9) l.Batters[bench] = replaced;
				}
				else
				{
					int role = k.MainPosition switch { CardPos.CP => 10, CardPos.SU => 9, CardPos.RP => 5, _ => 0 };
					int now = Array.IndexOf(l.Pitchers, k.Idx);
					if (now >= 0 && (role == 0 ? now < 5 : role == 5 ? now >= 5 && now <= 8 : now == role)) continue;
					int other = l.Pitchers[role];
					l.Pitchers[role] = k.Idx;
					if (now >= 0) l.Pitchers[now] = other;
				}
			}
			return l;
		}

		// Lineup.Fielders order: C, 1B, 2B, 3B, SS, LF, CF, RF, DH.
		private static int PositionIndex(CardPos p) => p switch
		{
			CardPos.C => 0, CardPos.B1 => 1, CardPos.B2 => 2, CardPos.B3 => 3, CardPos.SS => 4, CardPos.LF => 5, CardPos.CF => 6, CardPos.RF => 7, _ => 8
		};
	}
}
