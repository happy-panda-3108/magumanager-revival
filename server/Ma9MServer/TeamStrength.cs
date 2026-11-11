using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// How strong a card plays in a game:
	// 1. Bonus points (교육·계약·훈련·팀컬러) count at the development weight: card + 0.374 × bonus (0.34 × 1.1),
	//    less past shown 100 and 120 (DevCurve), the shown stat capped at 140. Card stats themselves play at 100%
	//    so real seasons replay.
	// 2. League reference: every stat plays relative to its league's own average. The league's
	//    playing-time-weighted mean is moved back to the reference world's (the real cards', share-
	//    weighted), so a league whose clubs all developed alike plays like the real league and games
	//    turn on 육성 차이 and card strength. 체력 is not moved (it is the pitch limit), nor are
	//    pitch ratings (only their ratios matter). Team traits stay out of it: AI clubs mix builds.
	// Order of play: card + bonus → league reference → today's condition (CardGame.Build).
	public static class TeamStrength
	{
		public const double DevWeight = 0.34;
		public const double ShownCap = 140;

		// Development curve on the shown scale (user, 2026-10-27: all-in on one stat paid best, so books and stats went
		// lopsided; "깡스탯 + 육성값이 120을 넘으면 효과가 반토막"): a bonus point counts `mult` of a full one at the shown
		// level it lands on, from each `from` up (empty = every point full). Shown 100 is purple and 120 극상 on the
		// client (Ma9MColorInfo). Rational 39-point builds (--devvalue): an ace (구위 92) 구위 138 테크닉 120, an average
		// starter 구위 119 테크닉 100, a batter 컨택 106 파워 100 선구 100 (it was 컨트롤 125 and 컨택 139 all-in).
		public static (double from, double mult)[] DevCurve = { (100, 0.75), (120, 0.5) };

		// The catcher's boost to his pitchers (ShownCatcher) on its own curve (user, 2026-10-31: "깡스탯 + 육성값 합이 120
		// 초과되면 효과가 감소하는데도 포수는 그냥 수비만 찍는게 최고야?? 그건 좀 수정하는 것이 좋아보임"; chose a quarter past
		// 120): the boost runs on the shown scale, so at DevCurve's half a point past 120 still paid 1.6 times a batting
		// stat's and the guide put all 39 points in 수비. A quarter keeps 100-120 as it was and sends the rest to batting.
		public static (double from, double mult)[] CatcherCurve = { (100, 0.75), (120, 0.25) };

		// Development weight by stat on top of DevWeight, CardRow.R order (batter 컨택 파워 선구 주력 정신 수비,
		// pitcher 체력 구속 컨트롤 테크닉 구위 정신). 구위 ×1.15 (user: 구위, 변화구 as was, is the stat to push and see
		// purple; 테크닉 had paid best).
		public static double[] BatDevStat = { 1, 1, 1, 1, 1, 1 }, PitDevStat = { 1, 1, 1, 1, 1.15, 1 };

		// Scale on DevWeight for card bonuses only (team traits keep DevWeight): ×1.1 makes up for part of the curve
		// (a club +30 on every stat against a bare mirror wins .939, was .928; on top of team colour 21, .877 was .924).
		public static double DevScale = 1.1;

		// Where the in-play half slope starts (CardRules.KneeX), set by Init. The shown curve does the diminishing
		// now; the old knee at the card range's top (95) cut in after team colour alone for a 90-구위 ace.
		public const double InPlayKnee = 105;

		// Shown points from `from` up `d` (capped at ShownCap) as counted under DevCurve (or `curve`); a loss counts in full.
		public static double Counted(double from, double d, (double from, double mult)[] curve = null)
		{
			if (d <= 0) return d;
			double to = Math.Min(ShownCap, from + d);
			if (to <= from) return 0;
			double sum = 0, at = from, mult = 1;
			foreach ((double edge, double m) in curve ?? DevCurve)
			{
				if (edge > at)
				{
					double end = Math.Min(to, edge);
					sum += mult * (end - at);
					at = end;
					if (at >= to) return sum;
				}
				mult = m;
			}
			return sum + mult * (to - at);
		}

		// Innings share of each staff slot (SP1-5, RP1-4, SU, CP) for the pitchers' mean.
		private static readonly double[] SlotWeight = { 0.118, 0.118, 0.118, 0.118, 0.118, 0.06, 0.06, 0.06, 0.06, 0.085, 0.085 };

		private static double[] s_refBat, s_refPit;

		// How the reference moves a league: Full = both ways, Down = only a league above the
		// reference is brought back (development), None = off (tests: --refmode). Down is the rule:
		// lifting a low-ball league (비기너, ~85-ball clubs) to the reference inflated its offence
		// (3 runs × 120 games: Full R/G 5.92, ERA 5.59; Down 4.99, 4.37; None 5.50, 4.93).
		public enum RefMode { Full, Down, None }

		public static RefMode Mode = RefMode.Down;

		// Reference = a league of real regulars (cards at 90%+ of the qualifier), since that is what
		// clubs field: batters as a lineup (one per position + DH), pitchers starters 59% /
		// relievers 41% (their innings).
		// Measured in tools/traitsim: such a league plays unshifted; the share-weighted mean of all
		// real cards (bench players included, ~6 points lower) sank it to R/G 4.2 and AVG .256.
		// Order: batter 컨택 파워 선구 주력 정신 수비, pitcher 체력 구속 컨트롤 테크닉 구위 정신.
		public static void Init(IEnumerable<CardRow> rows)
		{
			CardRules.KneeX = (InPlayKnee - 65) / 10;
			double[] Mean(IEnumerable<CardRow> rs)
			{
				List<CardRow> l = rs.ToList();
				return Enumerable.Range(0, 6).Select(j => l.Count > 0 ? l.Average(r => r.R[j]) : 65).ToArray();
			}
			List<CardRow> regulars = rows.Where(r => r.Share >= 0.9).ToList();
			// A lineup holds one regular per position (MainPos 1-8 = C..RF) plus a DH from anyone.
			List<CardRow> bats = regulars.Where(r => r.Kind == 'B').ToList();
			double[][] byPos = Enumerable.Range(1, 8).Select(p => Mean(bats.Where(r => r.MainPos == p))).Append(Mean(bats)).ToArray();
			s_refBat = Enumerable.Range(0, 6).Select(j => byPos.Average(m => m[j])).ToArray();
			double[] sp = Mean(regulars.Where(r => r.Kind == 'P' && r.Role == "SP")), rp = Mean(regulars.Where(r => r.Kind == 'P' && r.Role != "SP"));
			s_refPit = sp.Zip(rp, (a, b) => 0.59 * a + 0.41 * b).ToArray();
			ServerLog.Info($"league reference: batters {string.Join(" ", s_refBat.Select(x => x.ToString("F1")))}, pitchers {string.Join(" ", s_refPit.Select(x => x.ToString("F1")))}");
		}

		// TeamCard bonus order → CardRow.R index. Batter: con, pow, spd, eye, def, mental → 컨택 파워
		// 선구 주력 정신 수비; pitcher: health, spd, cont, technic, breakball, mental (same order).
		private static readonly int[] BatterSlot = { 0, 1, 3, 2, 5, 4 };

		public static double[] Bonus(TeamCard c)
		{
			double[] b = new double[6];
			for (int j = 0; j < 6; j++)
			{
				// + 코칭스태프 육성스탯 (CoachHandlers.FillBonus, systems session 2026-11-04).
				b[j] = c.Educated[j] + c.Contract[j] + c.Training[j] + c.TeamColor[j] + c.Coach[j];
			}
			return b;
		}

		// The card with its bonus points at the development weight (a copy; rows are shared). A pitcher's 포지션 변경
		// (PositionChangeHandlers.Delta) changes the card itself, so it is added at full weight first (user, 2026-10-24);
		// its 체력 is the pitch limit (PositionChangeHandlers.ToStarterHealth up, ToBullpenHealth down).
		public static void AddBonuses(GameCard g, TeamCard owned)
		{
			if (g == null || owned == null) return;
			double[] bonus = Bonus(owned);
			sbyte[] moved = g.Row.Kind == 'P' ? PositionChangeHandlers.DeltaOf(owned.Idx, owned.PositionTrainingInfo) : new sbyte[6];
			if (bonus.All(x => x == 0) && moved.All(x => x == 0)) return;
			CardRow c = g.Row;
			if (moved.Any(x => x != 0))
			{
				c = c.Clone();
				for (int j = 0; j < 6; j++)
				{
					int m = moved[j];
					if (m == 0) continue;
					c.R[j] = Math.Max(1, c.R[j] + m);
					if (j == 4) c.Pitches = c.Pitches.Select(x => (x.code, Math.Max(1, x.rating + m))).ToList();
				}
			}
			CardRow r = c.Clone();
			if (c.Kind == 'B') r.SpeedBase = c.R[3];   // fielding range counts only the 주력 above the card's own
			for (int k = 0; k < 6; k++)
			{
				int j = c.Kind == 'B' ? BatterSlot[k] : k;
				double d = bonus[k];
				if (d == 0) continue;
				double w = DevWeight * DevScale * (c.Kind == 'B' ? BatDevStat : PitDevStat)[j];
				r.R[j] += w * Counted(c.R[j], d);
				if (c.Kind == 'B' && j == 5)
				{
					// The catcher's boost to his pitchers reads the shown defence (card + bonus, full weight) on CatcherCurve.
					r.ShownCatcher = Math.Min(ShownCap, c.Defs[0] + Counted(c.Defs[0], Math.Max(0, d), CatcherCurve));
					for (int p = 0; p < 8; p++)
					{
						if (c.Defs[p] > 20) r.Defs[p] += w * Counted(c.Defs[p], d);
					}
				}
				if (c.Kind == 'P' && j == 4)
				{
					r.Pitches = c.Pitches.Select(x => (x.code, x.rating + w * Counted(x.rating, d))).ToList();
				}
			}
			g.Row = r;
		}

		// Bonuses for a lineup's cards from the owner's roster.
		public static void AddBonuses(GameTeam t, Lineup l, IEnumerable<TeamCard> owned)
		{
			Dictionary<int, TeamCard> byIdx = owned.ToDictionary(c => c.Idx);
			for (int slot = 0; slot < 9; slot++)
			{
				if (byIdx.TryGetValue(l.Batters[slot], out TeamCard c)) AddBonuses(t.Order[slot], c);
			}
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				if (t.Pitchers[s] != null && byIdx.TryGetValue(l.Pitchers[s], out TeamCard c)) AddBonuses(t.Pitchers[s], c);
			}
		}

		// The league's clubs (for now the two clubs of a game; with leagues, every club of the tier).
		public static (double[] bat, double[] pit, double def) LeagueShift(IEnumerable<GameTeam> league)
		{
			double[] bat = new double[6], pit = new double[6];
			double wb = 0, wp = 0, def = 0, wd = 0;
			foreach (GameTeam t in league)
			{
				for (int slot = 0; slot < 9; slot++)
				{
					CardRow c = t.Order[slot]?.Row;
					if (c == null) continue;
					for (int j = 0; j < 6; j++) bat[j] += c.R[j];
					wb++;
					int pos = t.Position[slot];
					if (pos < 8)
					{
						def += c.Defs[pos];
						wd++;
					}
				}
				for (int s = 0; s < t.Pitchers.Length; s++)
				{
					CardRow c = t.Pitchers[s]?.Row;
					if (c == null) continue;
					double w = s < SlotWeight.Length ? SlotWeight[s] : 0.05;
					for (int j = 0; j < 6; j++) pit[j] += w * c.R[j];
					wp += w;
				}
			}
			for (int j = 0; j < 6; j++)
			{
				bat[j] = wb > 0 ? bat[j] / wb - s_refBat[j] : 0;
				pit[j] = wp > 0 ? pit[j] / wp - s_refPit[j] : 0;
			}
			pit[0] = 0;  // 체력 = pitch limit: not relative
			double defShift = wd > 0 ? def / wd - s_refBat[5] : 0;
			if (Mode == RefMode.Down)
			{
				for (int j = 0; j < 6; j++)
				{
					bat[j] = Math.Max(0, bat[j]);
					pit[j] = Math.Max(0, pit[j]);
				}
				defShift = Math.Max(0, defShift);
			}
			return (bat, pit, defShift);
		}

		public static void ApplyLeagueReference(IList<GameTeam> league)
		{
			if (s_refBat == null || Mode == RefMode.None) return;
			var shift = LeagueShift(league);
			foreach (GameTeam t in league)
			{
				ApplyShift(t, shift);
			}
		}

		// One team moved by a league's shift (LeagueShift); the engine session's 자동 편성 (2026-11-11) applies the league's
		// shift of now to candidate 1군s.
		public static void ApplyShift(GameTeam t, (double[] bat, double[] pit, double def) shift)
		{
			if (s_refBat == null || Mode == RefMode.None) return;
			foreach (GameCard g in t.Order.Concat(t.Pitchers))
			{
				if (g == null) continue;
				CardRow c = g.Row.Clone();
				if (c.Kind == 'B' && double.IsNaN(c.ShownCatcher)) c.ShownCatcher = c.Defs[0];
				if (c.Kind == 'B' && double.IsNaN(c.SpeedBase)) c.SpeedBase = c.R[3];
				double[] s = c.Kind == 'B' ? shift.bat : shift.pit;
				for (int j = 0; j < 6; j++) c.R[j] -= s[j];
				if (c.Kind == 'B')
				{
					for (int p = 0; p < 8; p++)
					{
						if (c.Defs[p] > 20) c.Defs[p] -= shift.def;
					}
				}
				g.Row = c;
			}
		}
	}
}
