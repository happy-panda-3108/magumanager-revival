using System;
using Ma9MSim;

namespace Ma9MServer
{
	// 공인구 in the game (engine session, 2026-11-15). User: "공인구 반발력이 높은 시즌은 타고투저의 평균득점이 예산되는 리그인거고,
	// 반발력이 평범한 시즌은 평균적인 리그, 반발력이 낮은 시즌은 93년처럼 투고타저 리그가 되는거지. 공인구의 반발력 계수는 평균 kbo
	// 공인구 반발력에서 랜덤하게 분포되어서 등장하게하면 되지 않을까?" — and "선택지 가에 공인구 시스템을 얹어보자". The season's
	// ball (SeasonBall, systems session: drawn per calendar season, the same for every league) changes how far a batted ball
	// carries, for both clubs alike: home runs, hits on balls in play and the extra-base share of hits. Strikeouts and walks
	// stay. The standard ball (SeasonBall.StandardCor) changes nothing; the league reference (TeamStrength) sets that world to
	// the designed environment (R/G 5.00).
	//
	// The rates a team-run off the standard ball come from KBO's two ball years in the Statiz pitching tables (league sums):
	//   2018 → 2019 (the COR standard lowered about 0.01): R/9 5.49 → 4.49, HR per PA ×0.586, BABIP ×0.942, 2B+3B per hit ×0.930
	//   2023 → 2024:                                    R/9 4.50 → 5.31, HR ×1.555, BABIP ×1.048, 2B+3B per hit ×1.038
	// i.e. e^(0.54·x) for home runs, e^(0.06·x) for BABIP and e^(0.065·x) for the extra-base share at x team-runs. The engine
	// combines batter and pitcher by Log5 around the league, so one side carries each: the batters' HR and 2B/3B shares, the
	// pitchers' BABIP. Fit scales x so the leagues play the ball's expected runs (SeasonBall.Runs), measured with --runenv
	// (RUNENV_COR) on a data copy.
	public static class BallEffect
	{
		public const double HrPerRun = 0.54, BabipPerRun = 0.06, XbhPerRun = 0.065;

		// The engine's answer to x (--runenv on a copy of the live leagues, 11-15, every card at 보통 condition): runs a team-game
		// × e^(−0.209) at x = −1 and × e^(+0.241) at x = +1 (mean of the six tiers; 5.06 → 4.07 and 6.27 in 챔피언스): the leagues
		// score in proportion, a little more on the way up. So a ball drawn for r = runs / 5.00 plays x = ln(r) / K on its side.
		public static double KDown = 0.209, KUp = 0.241;

		// Team-runs off the standard ball that the ball is drawn for (SeasonBall: runs = 5.00 + (cor − standard) / CorPerRun).
		public static double RunsOff(double cor) => (cor - SeasonBall.StandardCor) / SeasonBall.CorPerRun;

		public static bool IsStandard(double cor) => double.IsNaN(cor) || Math.Abs(cor - SeasonBall.StandardCor) < 1e-7;

		// Which leagues play the season's ball (user, 11-15 12:1x: "트리플, 레전드, 챔피언스 리그에만 해당 기능을 적용하고 그 하위 리그는
		// 그냥 N볼 상태를 고정한채로 따로 공인구 시스템 적용 없이 원래 설계한 평균득점인 리그로 합시다. 좀 덱처럼 갖춰진 리그에서 해당
		// 기능이 쓸모 있을 것 같음"): 트리플A and up (메이저 between them included); 비기너, 루키 and 더블A keep the 노말볼 (N). 트리플A
		// is the tier under 메이저 in both the six- and the seven-tier structure.
		public static bool Applies(int tier) => tier >= LeagueRules.MajorTier - 1;

		// The ball a league plays this season: the season's ball (SeasonBall) from 트리플A up, else the standard ball.
		public static double CorOf(LeagueState s) => s != null && Applies(s.Tier) ? SeasonBall.Cor(s.Start) : SeasonBall.StandardCor;

		// x as the engine plays it for the ball's expected runs.
		public static double EngineX(double cor)
		{
			double r = Math.Max(0.5, (SeasonBall.StandardRuns + RunsOff(cor)) / SeasonBall.StandardRuns);
			double l = Math.Log(r);
			return l / (l < 0 ? KDown : KUp);
		}

		public static (double hr, double babip, double xbh) Factors(double cor)
		{
			if (IsStandard(cor)) return (1, 1, 1);
			double x = EngineX(cor);
			return (Math.Exp(HrPerRun * x), Math.Exp(BabipPerRun * x), Math.Exp(XbhPerRun * x));
		}

		// Both clubs of a built game (CardGame.Build), after the 팀 특성 (TraitEffects.Apply).
		public static void Apply(SeasonModel m, double cor)
		{
			if (m == null || IsStandard(cor)) return;
			(double hr, double babip, double xbh) f = Factors(cor);
			for (int side = 0; side < 2; side++)
			{
				foreach (int i in m.TeamBatters[side]) Scale(m.BT[i], f);
				GameDeck d = m.Decks[side];
				for (int s = 0; s < d.Pitchers.Length; s++)
				{
					int p = d.Pitchers[s];
					if (p >= 0) Scale(m.PT[p], f);
				}
			}
		}

		// One talent (the evaluators' league-average opponents, a card's own value).
		public static void Scale(BatterTalent t, double cor)
		{
			if (t != null && !IsStandard(cor)) Scale(t, Factors(cor));
		}

		public static void Scale(PitcherTalent t, double cor)
		{
			if (t != null && !IsStandard(cor)) Scale(t, Factors(cor));
		}

		private static void Scale(BatterTalent t, (double hr, double babip, double xbh) f)
		{
			t.HR = Math.Clamp(t.HR * f.hr, 0.0003, 0.2);
			t.Double = Math.Clamp(t.Double * f.xbh, 0.03, 0.5);
			t.Triple = Math.Clamp(t.Triple * f.xbh, 0.0005, 0.15);
		}

		private static void Scale(PitcherTalent t, (double hr, double babip, double xbh) f)
		{
			t.Babip = Math.Clamp(t.Babip * f.babip, 0.12, 0.5);
		}
	}
}
