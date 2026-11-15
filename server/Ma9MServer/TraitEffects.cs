using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 팀 특성 in games: the effects the systems session balanced in tools/traitsim (traits.json, the
	// user's season-2 trees), applied the way its Arena applied them.
	// 1. Stat gains, in shown points, raise the cards at the development weight (like bonus points);
	//    starters and the set-up/closing pair have their own gains.
	// 2. Rate effects multiply the game model's talents after it is built: own batters' HR, K, BB,
	//    extra-base hits and steal attempts; the opponents' extra-base hits and steal attempts; own
	//    pitchers' strikeouts, hits on balls in play and home runs; caught stealing through the catchers.
	// Effects are left out of the league reference (the systems session's recommendation), so they
	// are applied after TeamStrength.ApplyLeagueReference.
	public static class TraitEffects
	{
		public sealed class Effect
		{
			public double[] Bat = new double[6];   // 컨택 파워 선구 주력 정신 수비 (CardRow.R order)
			public double[] Pit = new double[6];   // 체력 구속 컨트롤 테크닉 구위 정신, every pitcher
			public double[] Late = new double[6];  // set-up man and closer only
			public double[] Start = new double[6]; // the five starters only
			public double HR = 1, K = 1, BB = 1, XBH = 1, StealTry = 1, StealCaught = 1;    // own batting
			public double PK = 1, PBabip = 1, PHR = 1, PXBH = 1, PStealTry = 1, PCaught = 1; // own pitching
			public double StartLimit;  // pitches added to a starter's limit (needs the fatigue engine)

			public bool IsEmpty => Bat.Concat(Pit).Concat(Late).Concat(Start).All(x => x == 0)
				&& new[] { HR, K, BB, XBH, StealTry, StealCaught, PK, PBabip, PHR, PXBH, PStealTry, PCaught }.All(x => x == 1);
		}

		// Effect per point, from traits.json (perPoint).
		private static readonly Dictionary<int, (string key, double value)[]> PerPoint = new Dictionary<int, (string, double)[]>
		{
			[101] = new[] { ("컨택", 1.0) },
			[102] = new[] { ("주력", 7.0) },
			[103] = new[] { ("H정신", 8.0) },
			[104] = new[] { ("파워", 1.1) },
			[105] = new[] { ("도루시도%", 20.0), ("도루자%", -15.0) },
			[106] = new[] { ("선구", 1.4) },
			[107] = new[] { ("수비", 9.5) },
			[110] = new[] { ("컨택", 1.0), ("H정신", 2.0) },
			[111] = new[] { ("도루자%", -20.0) },
			[112] = new[] { ("홈런%", 4.0) },
			[113] = new[] { ("컨택", 1.0), ("주력", 4.0) },
			[114] = new[] { ("삼진%", -2.0), ("볼넷%", 2.0) },
			[115] = new[] { ("컨택", 1.0), ("파워", 0.5) },
			[118] = new[] { ("타자전체", 1.0) },
			[120] = new[] { ("홈런%", 6.0), ("장타%", 6.0) },
			[121] = new[] { ("구위", 1.6) },
			[122] = new[] { ("구속", 2.8) },
			[123] = new[] { ("체력", 10.0), ("선발전체", 0.45) },
			[124] = new[] { ("P정신", 10.0) },
			[125] = new[] { ("테크닉", 2.2) },
			[126] = new[] { ("컨트롤", 3.3) },
			[127] = new[] { ("상대도루시도%", -5.0), ("도루저지%", 22.0) },
			[130] = new[] { ("컨트롤", 1.5), ("테크닉", 1.5) },
			[131] = new[] { ("구속", 2.0), ("구위", 1.0) },
			[132] = new[] { ("선발한계", 10.0), ("선발전체", 0.6) },
			[133] = new[] { ("피안타%", -0.5), ("탈삼진%", 2.0) },
			[134] = new[] { ("피장타%", -4.0), ("피홈런%", -3.0) },
			[135] = new[] { ("피안타%", -0.5), ("구속", 2.5) },
			[138] = new[] { ("투수전체", 1.5) },
			[140] = new[] { ("필승조전체", 10.0) },
		};

		// 특성 세기 다시 맞추기 (engine session, 2026-11-11; user 20:4x "특성 세기는 가 안으로 해줘", 21:2x "진행합시다. 특성을 가장 승률 높은
		// 조합으로 자동 배분해주는 기능이 생길 때 같이 반영합시다"): every trait worth its tier's target again in full games on average
		// rosters (--traitbalancesim 400 pairs × 100 games, paired; batter .36 .42 .48 .55 1.10 %p a point, pitcher ×1.1). The 10-18
		// amounts were set when 정신력 and 수비 were worth next to nothing in the engine; after the 10-30 clutch rework and the fielding
		// engine 103·124·107 played about 3× their tier, most pitcher traits 1.3-1.8×. Before → after (%p a point): 103 1.17 → 0.32,
		// 124 1.55 → 0.40, 107 1.47 → 0.43, 110 0.67 → 0.47, 114 0.39 → 0.62, 122 0.67 → 0.48, 126 0.67 → 0.47, 130 0.71 → 0.51,
		// 131 0.72 → 0.53, 135 0.93 → 0.69, 138 1.81 → 1.12, 140 2.23 → 1.08; the rest unchanged (within their tier's range). With it
		// the caught-stealing share scales by itself (SeasonModel.CaughtScale; ScaleCaught). Off until the 특성 자동 배분 screen ships:
		// MA9M_TRAIT_V2=1 turns it on (then flip the default).
		public static readonly bool V2 = Environment.GetEnvironmentVariable("MA9M_TRAIT_V2") == "1";

		private static readonly Dictionary<int, (string key, double value)[]> PerPointV2 = new Dictionary<int, (string, double)[]>(PerPoint)
		{
			[103] = new[] { ("H정신", 2.5) },
			[107] = new[] { ("수비", 2.8) },
			[110] = new[] { ("컨택", 0.7), ("H정신", 1.4) },
			[114] = new[] { ("삼진%", -2.8), ("볼넷%", 2.8) },
			[122] = new[] { ("구속", 1.65) },
			[124] = new[] { ("P정신", 2.6) },
			[126] = new[] { ("컨트롤", 2.3) },
			[130] = new[] { ("컨트롤", 1.1), ("테크닉", 1.1) },
			[131] = new[] { ("구속", 1.5), ("구위", 0.75) },
			[135] = new[] { ("피안타%", -0.33), ("구속", 1.6) },
			[138] = new[] { ("투수전체", 1.0) },
			[140] = new[] { ("필승조전체", 5.4) },
		};

		// 특성 값 실제 덱 기준 (systems session, 2026-11-15; user 03:2x "특성 수정 이후에는 타자 특성이 되게 유리한 것 같은데 맞나??",
		// 04:0x "가 추천안으로 한 번 가보자고"): V2 was met on average rosters at neutral tactics; on the real clubs' 1군 with their
		// own tactics the whole batting tree beat the whole pitching tree by 1.2-3.2%p (docs\특성균형_1115.md): 주력·도루 (102·105·
		// 111·113) with 도루 적극 played up to 2.5 times their tier, most pitcher traits below it. V3 = V2 × these shares, from
		// each trait alone at its max on the 9 clubs whose win share with no traits is 30-85% (median, 3,000 games against each
		// other club, one by one; tools\scripts\systems\trait_rescale.py --base 30:85 --median --keep-batting): the batting tree's
		// total as it was, each trait at its tier's share of it, the pitching tree 1.1 times it again. On with MA9M_TRAIT_V3=1
		// (needs V2).
		// Measured again 11-15 afternoon (engine session's request after the league reference moved to the designed 5.00 R/G,
		// RefBatDelta 0.5/5/1.5): the same run on 10 clubs. 123 체력 and 132 선발 투구 한계 kept at 1.48: on some clubs they cost
		// wins whatever the scale (starters left in longer than the bullpen would pitch; the pull logic, engine session). Whole trees
		// on 5 clubs (SKY마토, SKY승환, 행복한현대, 기댓값레이더스, AI테스터): pitching minus batting +0.31%p on average with the old
		// shares, +0.04%p with these (기댓값레이더스 left out: −6.3 / −6.9, its 123·132 below zero).
		public static readonly bool V3 = V2 && Environment.GetEnvironmentVariable("MA9M_TRAIT_V3") == "1";

		private static readonly Dictionary<int, double> ShareV3 = new Dictionary<int, double>
		{
			[101] = 0.93, [102] = 0.89, [103] = 1.00, [104] = 1.46, [105] = 0.66, [106] = 1.04, [107] = 1.27, [110] = 1.12,
			[111] = 0.89, [112] = 1.19, [113] = 1.41, [114] = 0.80, [115] = 1.19, [118] = 0.87, [120] = 1.26,
			[121] = 1.31, [122] = 1.45, [123] = 1.48, [124] = 1.10, [125] = 0.94, [126] = 1.23, [127] = 0.86, [130] = 1.33,
			[131] = 1.23, [132] = 1.48, [133] = 1.21, [134] = 1.43, [135] = 1.40, [138] = 1.46, [140] = 1.36,
		};

		private static readonly Dictionary<int, (string key, double value)[]> PerPointV3 = PerPointV2.ToDictionary(
			kv => kv.Key, kv => kv.Value.Select(p => (p.key, p.value * ShareV3.GetValueOrDefault(kv.Key, 1.0))).ToArray());

		// Dev (engine session, 2026-11-11, 특성 세기 다시 맞추기): MA9M_TRAIT_SCALE="103:0.29,124:0.3" plays those traits' per-point
		// amounts times the share (tests on data copies only).
		private static readonly Dictionary<byte, double> s_scale = (Environment.GetEnvironmentVariable("MA9M_TRAIT_SCALE") ?? string.Empty)
			.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Split(':')).Where(x => x.Length == 2)
			.ToDictionary(x => byte.Parse(x[0]), x => double.Parse(x[1], System.Globalization.CultureInfo.InvariantCulture));

		// The team's saved traits as one effect (null when it has none).
		public static Effect Of(uint userCode)
		{
			return FromPoints(TeamPropertyHandlers.Points(userCode));
		}

		public static Effect FromPoints(Dictionary<byte, byte> points)
		{
			if (points == null || points.Count == 0)
			{
				return null;
			}
			Effect e = new Effect();
			foreach ((byte index, byte p) in points)
			{
				if (!(V3 ? PerPointV3 : V2 ? PerPointV2 : PerPoint).TryGetValue(index, out (string key, double value)[] parts))
				{
					continue;
				}
				double scale = s_scale.TryGetValue(index, out double sc) ? sc : 1;
				foreach ((string key, double value) in parts)
				{
					Add(e, key, value * p * scale);
				}
			}
			return e.IsEmpty ? null : e;
		}

		private static void Add(Effect e, string key, double x)
		{
			double m = 1 + x / 100;
			switch (key)
			{
				case "컨택": e.Bat[0] += x; break;
				case "파워": e.Bat[1] += x; break;
				case "선구": e.Bat[2] += x; break;
				case "주력": e.Bat[3] += x; break;
				case "H정신": e.Bat[4] += x; break;
				case "수비": e.Bat[5] += x; break;
				case "타자전체": for (int j = 0; j < 6; j++) e.Bat[j] += x; break;
				case "체력": e.Pit[0] += x; break;
				case "구속": e.Pit[1] += x; break;
				case "컨트롤": e.Pit[2] += x; break;
				case "테크닉": e.Pit[3] += x; break;
				case "구위": e.Pit[4] += x; break;
				case "P정신": e.Pit[5] += x; break;
				case "투수전체": for (int j = 0; j < 6; j++) e.Pit[j] += x; break;
				case "필승조전체": for (int j = 0; j < 6; j++) e.Late[j] += x; break;
				case "선발전체": for (int j = 0; j < 6; j++) e.Start[j] += x; break;
				case "홈런%": e.HR *= m; break;
				case "삼진%": e.K *= m; break;
				case "볼넷%": e.BB *= m; break;
				case "장타%": e.XBH *= m; break;
				case "도루시도%": e.StealTry *= m; break;
				case "도루자%": e.StealCaught *= m; break;
				case "탈삼진%": e.PK *= m; break;
				case "피안타%": e.PBabip *= m; break;
				case "피홈런%": e.PHR *= m; break;
				case "피장타%": e.PXBH *= m; break;
				case "상대도루시도%": e.PStealTry *= m; break;
				case "도루저지%": e.PCaught *= m; break;
				case "선발한계": e.StartLimit += x; break;
				default: throw new ArgumentException("unknown trait effect " + key);
			}
		}

		// Step 1: the team's cards with the stat gains (after the league reference).
		public static void Raise(GameTeam t, Effect e)
		{
			if (t == null || e == null)
			{
				return;
			}
			for (int k = 0; k < t.Order.Length; k++)
			{
				if (t.Order[k] != null)
				{
					t.Order[k].Row = Raised(t.Order[k].Row, e.Bat);
				}
			}
			for (int s = 0; s < t.Pitchers.Length; s++)
			{
				if (t.Pitchers[s] == null)
				{
					continue;
				}
				double[] extra = s >= 9 ? e.Late : s < 5 ? e.Start : new double[6];
				t.Pitchers[s].Row = Raised(t.Pitchers[s].Row, e.Pit.Zip(extra, (a, b) => a + b).ToArray());
			}
		}

		// A card with shown gains, played at the development weight (gains capped at 140 shown).
		private static CardRow Raised(CardRow c, double[] gains)
		{
			if (c == null || gains.All(g => g == 0))
			{
				return c;
			}
			CardRow r = c.Clone();
			double Gain(double v, double d) => d > 0 ? Math.Max(0, Math.Min(TeamStrength.ShownCap, v + d) - v) : d;
			for (int j = 0; j < 6; j++)
			{
				r.R[j] += TeamStrength.DevWeight * Gain(c.R[j], gains[j]);
			}
			if (c.Kind == 'B' && gains[5] != 0)
			{
				for (int p = 0; p < 8; p++)
				{
					if (c.Defs[p] > 20)
					{
						r.Defs[p] += TeamStrength.DevWeight * Gain(c.Defs[p], gains[5]);
					}
				}
			}
			if (c.Kind == 'P' && gains[4] != 0)
			{
				r.Pitches = c.Pitches.Select(x => (x.code, x.rating + TeamStrength.DevWeight * Gain(x.rating, gains[4]))).ToList();
			}
			return r;
		}

		// Step 2: the rate effects on the built model. side = the team's index in the model
		// (CardGame.Build puts home at 0 and away at 1).
		public static void Apply(SeasonModel m, int side, Effect e)
		{
			if (m == null || e == null)
			{
				return;
			}
			int other = 1 - side;
			foreach (int i in m.TeamBatters[side])
			{
				BatterTalent t = m.BT[i];
				t.K = Math.Clamp(t.K * e.K, 0.01, 0.6);
				t.BB = Math.Clamp(t.BB * e.BB, 0.005, 0.35);
				t.HR = Math.Clamp(t.HR * e.HR, 0.0003, 0.2);
				t.Double = Math.Clamp(t.Double * e.XBH, 0.03, 0.5);
				t.Triple = Math.Clamp(t.Triple * e.XBH, 0.0005, 0.15);
				t.Steal = Math.Clamp(t.Steal * e.StealTry, 0.001, 0.6);
			}
			foreach (int i in m.TeamBatters[other])
			{
				BatterTalent t = m.BT[i];
				t.Double = Math.Clamp(t.Double * e.PXBH, 0.03, 0.5);
				t.Triple = Math.Clamp(t.Triple * e.PXBH, 0.0005, 0.15);
				t.Steal = Math.Clamp(t.Steal * e.PStealTry, 0.001, 0.6);
			}
			GameDeck d = m.Decks[side];
			for (int s = 0; s < d.Pitchers.Length; s++)
			{
				int p = d.Pitchers[s];
				if (p < 0)
				{
					continue;
				}
				PitcherTalent t = m.PT[p];
				t.K = Math.Clamp(t.K * e.PK, 0.02, 0.6);
				t.Babip = Math.Clamp(t.Babip * e.PBabip, 0.12, 0.5);
				t.HR = Math.Clamp(t.HR * e.PHR, 0.001, 0.15);
				// 선발한계: the five starters' pitch limit in the fatigue engine.
				if (s < 5 && m.Tac != null)
				{
					m.Tac.Limit[p] = Math.Min(m.Tac.R.LimitMax, m.Tac.Limit[p] + e.StartLimit);
				}
			}
			ScaleCaught(m, other, e.StealCaught);  // our runners against their catcher
			ScaleCaught(m, side, e.PCaught);       // their runners against our catcher
		}

		// The caught-stealing share against the team's catcher multiplied by f (SeasonModel.CaughtScale; engine session,
		// 2026-11-11). Before, the catcher's effective defence was moved for it, through an inverse that missed the fielding
		// tune (Defence.TunedX): 도루 억제 2점 (×1.49) set an 85 catcher's defence near 290 for the whole game, so 105·111·127
		// played 1.0-1.3%p a point in full games where the steals alone are worth about a third. Goes with V2 (the user: the trait
		// changes together, with the 특성 자동 배분 screen); MA9M_TRAIT_CAUGHT_OLD=1 / 0 forces the old / new way for tests.
		private static readonly bool CaughtOld = (Environment.GetEnvironmentVariable("MA9M_TRAIT_CAUGHT_OLD") ?? (V2 ? "0" : "1")) == "1";

		private static void ScaleCaught(SeasonModel m, int team, double f)
		{
			if (f == 1 || m.Defs == null)
			{
				return;
			}
			if (!CaughtOld)
			{
				m.CaughtScale ??= new double[] { 1, 1 };
				m.CaughtScale[team] *= f;
				return;
			}
			int i = m.Decks[team].At[0];
			if (i < 0)
			{
				return;
			}
			FieldRules fr = m.Field;
			double orig = m.Defs[i][0];
			double now = fr.CaughtMult((orig - 65) / 10.0);
			double y = (f * now - 1) / fr.CaughtPer10;
			double x = y <= 3 ? y : 3 + 2 * (y - 3);
			m.CatcherShown ??= Enumerable.Repeat(double.NaN, m.Batters.Length).ToArray();
			if (double.IsNaN(m.CatcherShown[i]))
			{
				m.CatcherShown[i] = orig;
			}
			m.Defs[i] = (double[])m.Defs[i].Clone();
			m.Defs[i][0] = 65 + 10 * x;
		}
	}
}
