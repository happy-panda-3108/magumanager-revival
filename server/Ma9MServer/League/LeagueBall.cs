namespace Ma9MServer
{
	// 공인구 for games outside the league chain (engine session, 2026-11-15): exhibitions and the 새벽 자동 시범경기 play the ball of
	// the club's league season (BallEffect.CorOf: the season's ball from 트리플A up, else 노말볼); the standard ball without a league.
	public static partial class LeagueService
	{
		public static double SeasonCor(uint userCode)
		{
			LeagueState s = s_store?.Load(userCode);
			return s == null || s.Clubs.Count == 0 ? SeasonBall.StandardCor : BallEffect.CorOf(s);
		}
	}
}
