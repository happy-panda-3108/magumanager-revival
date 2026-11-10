using UnityEngine;

// Ma9M port (engine session, 2026-10-31; user: "경기별 하이라이트 … 득점 장면 또는 호수비 장면 등
// 경기결과에 영향을 준 순간들"): how much a scene is worth in a game's highlights. The original kept
// the scenes with the most hits and runs; this rates each by how far it moved the win probability
// (inning, half, outs, bases, score), plus the runs it brought in, a home run, a walk-off, and the
// defence the script shows (diving or wall catches, outfield assists, double and triple plays, a
// play at the plate). The game's buffer is kept on the server, so any device can watch them.
public static class Ma9MHighlight
{
	public const int Keep = 8, Threshold = 70;

	// Runs a team still expects in the half-inning: bases (bit 1 first, 2 second, 4 third) × outs.
	private static readonly float[,] RunsExpected =
	{
		{ 0.48f, 0.25f, 0.10f }, { 0.85f, 0.50f, 0.22f }, { 1.10f, 0.66f, 0.32f }, { 1.44f, 0.88f, 0.43f },
		{ 1.35f, 0.95f, 0.35f }, { 1.78f, 1.13f, 0.48f }, { 1.96f, 1.36f, 0.57f }, { 2.29f, 1.54f, 0.75f }
	};

	// The home side's chance to win with this state: the expected final margin over its spread.
	public static float HomeWins(int inning, bool homeBats, int outs, int bases, int homeRuns, int awayRuns)
	{
		int lead = homeRuns - awayRuns;
		if (outs >= 3)
		{
			// The half is over (and with it the game, from the bottom of the ninth on, if someone leads).
			if (homeBats && inning >= 9 && lead != 0) return lead > 0 ? 1f : 0f;
			if (homeBats) { inning++; homeBats = false; } else { homeBats = true; }
			outs = 0;
			bases = 0;
		}
		inning = Mathf.Max(1, inning);
		if (inning >= 9 && homeBats && lead > 0) return 1f;
		int awayHalves = Mathf.Max(0, 9 - inning + (homeBats ? 0 : 1));   // whole halves still to come
		int homeHalves = Mathf.Max(0, 9 - inning + 1) - (homeBats ? 1 : 0);
		if (!homeBats) awayHalves--;
		float now = RunsExpected[Mathf.Clamp(bases, 0, 7), Mathf.Clamp(outs, 0, 2)];
		float mean = lead + (homeBats ? now : -now) + 0.5f * (homeHalves - awayHalves);
		float spread = Mathf.Sqrt(1.0f * (homeHalves + awayHalves) + 0.9f + 0.3f);
		return Phi(mean / spread);
	}

	private static float Phi(float x) => 0.5f * (1f + Erf(x / Mathf.Sqrt(2f)));

	private static float Erf(float x)
	{
		// Abramowitz-Stegun 7.1.26.
		float t = 1f / (1f + 0.3275911f * Mathf.Abs(x));
		float y = 1f - (((((1.061405429f * t - 1.453152027f) * t) + 1.421413741f) * t - 0.284496736f) * t + 0.254829592f) * t * Mathf.Exp(-x * x);
		return x >= 0 ? y : -y;
	}

	// A scene's worth: inning, half and outs before it, the bases before and after (bits), the score
	// before, the runs it brought in and the outs after.
	public static int Grade(SimulResult r, int scene, int inning, bool homeBats, int outsBefore, int outsAfter, int homeRuns, int awayRuns, int runs)
	{
		if (r == null || r.m_base_batterOrder == null || r.m_base_nextbase == null) return 0;
		int before = 0, after = 0;
		for (int k = 1; k < 4; k++) if (r.m_base_batterOrder[k] != 0) before |= 1 << (k - 1);
		for (int k = 0; k < 4; k++)
		{
			int n = r.m_base_nextbase[k];
			if (r.m_base_batterOrder[k] != 0 && n >= 1 && n <= 3) after |= 1 << (n - 1);
		}
		float w0 = HomeWins(inning, homeBats, outsBefore, before, homeRuns, awayRuns);
		int h1 = homeRuns + (homeBats ? runs : 0), a1 = awayRuns + (homeBats ? 0 : runs);
		float w1 = HomeWins(inning, homeBats, outsAfter, after, h1, a1);
		int grade = Mathf.RoundToInt(Mathf.Abs(w1 - w0) * 1000f);
		grade += 40 * runs;
		if (r.m_sit == eSituation.ST_HR) grade += 60;
		if (homeBats && inning >= 9 && h1 > a1 && homeRuns <= awayRuns) grade += 300;   // walk-off
		grade += DefenceBonus(scene, r);
		return grade;
	}

	private static int DefenceBonus(int scene, SimulResult r)
	{
		int bonus = r.m_sit == eSituation.ST_DOUBLEPLAY ? 60 : 0;
		Ma9MPlayScript s = Ma9MScripts.For(scene);
		if (s == null) return bonus;
		int outs = 0;
		bool ofThrow = false;
		foreach (Ma9MPiece p in s.Pieces)
		{
			if (p.Kind == Ma9MSeg.Catch && (p.Style == 2 || p.Style == 3)) bonus += 120;   // diving or wall catch
			if (p.Kind == Ma9MSeg.Catch && p.Style == 6) bonus += 50;                        // diving stop
			if (p.Kind == Ma9MSeg.Throw && p.Actor >= 5 && p.Actor <= 7) ofThrow = true;
			if (p.Kind == Ma9MSeg.Out && p.Actor > Ma9MPiece.RunnerBase) outs++;
			if (p.Kind == Ma9MSeg.Tag && p.Style == 1 && p.P1.magnitude < 3f) bonus += 80;   // a play at the plate
		}
		if (ofThrow && outs > 0) bonus += 120;   // an outfield assist
		if (outs >= 2) bonus += 200;             // a triple play (or a double off the bases)
		return bonus;
	}
}
