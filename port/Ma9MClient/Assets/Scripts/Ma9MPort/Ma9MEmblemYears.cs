using System.Collections.Generic;

// 해에 맞는 구단 로고 (user 2026-11-16 18:3x: "그 해에 맞는 로고가 들어가야죠"). Engine session; the server's EmblemYears keeps the
// same table.
//
// Emblem.emblemNames has one emblem per club (eTeamName 0-20, the franchise's latest logo). A season's logo is an emblem past
// them: First + base * 10 + variant, base the eTeamName whose name the logo carries, variant its place among that name's logos
// that reads an emblem as a club (the stadium, cheer songs, default uniforms) takes BaseOf first.
public static class Ma9MEmblemYears
{
	public const int First = 32, Bases = 21, PerBase = 10;

	// Each franchise's logos in the order they began: { first season, base eTeamName, variant }.
	private static readonly int[][][] Lines = new int[][][]
	{
		new[] { new[] { 1982, 10, 0 }, new[] { 1999, 0, 0 }, new[] { 2010, 0, 1 }, new[] { 2025, 0, 2 } },                                                 // OB → 두산
		new[] { new[] { 1982, 1, 0 }, new[] { 1996, 1, 1 }, new[] { 2001, 5, 0 }, new[] { 2010, 5, 1 }, new[] { 2017, 5, 2 }, new[] { 2021, 5, 3 } },      // 해태 → KIA
		new[] { new[] { 1986, 14, 0 }, new[] { 1994, 2, 0 }, new[] { 1999, 2, 1 }, new[] { 2005, 2, 2 }, new[] { 2007, 2, 3 }, new[] { 2025, 2, 4 } },     // 빙그레 → 한화
		new[] { new[] { 1982, 16, 0 }, new[] { 1985, 16, 1 }, new[] { 1990, 6, 0 }, new[] { 2006, 6, 1 }, new[] { 2017, 6, 2 } },                         // MBC → LG
		new[] { new[] { 1982, 18, 0 }, new[] { 1985, 17, 0 }, new[] { 1988, 15, 0 }, new[] { 1994, 15, 1 }, new[] { 1996, 4, 0 } },                       // 삼미 → 청보 → 태평양 → 현대
		new[] { new[] { 2000, 9, 0 }, new[] { 2006, 9, 1 }, new[] { 2020, 9, 2 }, new[] { 2021, 9, 3 }, new[] { 2024, 9, 4 } },                           // SK → SSG
		new[] { new[] { 2008, 3, 0 }, new[] { 2009, 3, 1 }, new[] { 2010, 3, 2 }, new[] { 2019, 3, 3 } },                                                 // 히어로즈
		new[] { new[] { 1982, 7, 0 }, new[] { 1993, 7, 1 }, new[] { 1996, 7, 2 }, new[] { 2003, 7, 3 }, new[] { 2010, 7, 4 }, new[] { 2018, 7, 5 }, new[] { 2023, 7, 6 } }, // 롯데
		new[] { new[] { 1982, 8, 0 }, new[] { 1989, 8, 1 }, new[] { 1995, 8, 2 }, new[] { 2008, 8, 3 } },                                                 // 삼성
		new[] { new[] { 1991, 11, 0 } },                                                                                                                  // 쌍방울
		new[] { new[] { 2008, 12, 0 }, new[] { 2026, 12, 1 } },                                                                                           // 국가대표
		new[] { new[] { 2012, 13, 0 }, new[] { 2019, 13, 1 } },                                                                                           // NC
		new[] { new[] { 2015, 19, 0 } },                                                                                                                  // kt
	};

	// The variant each base's plain emblem shows.
	private static readonly int[] DefaultVariant = { 2, 1, 4, 3, 0, 3, 2, 6, 3, 4, 0, 0, 1, 1, 0, 1, 0, 0, 0, 0, 0 };

	public static bool IsEra(int emblem)
	{
		return emblem >= First && emblem < First + Bases * PerBase;
	}

	// The franchise (a row of Lines: OB and 두산 are one) club `team` (an eTeamName) belongs to; -1 for none (FA, 국가대표 stays its own).
	public static int Franchise(int team)
	{
		if (team < 0)
		{
			return -1;
		}
		for (int i = 0; i < Lines.Length; i++)
		{
			foreach (int[] e in Lines[i])
			{
				if (e[1] == team)
				{
					return i;
				}
			}
		}
		return -1;
	}

	// The club (eTeamName) an emblem stands for.
	public static int BaseOf(int emblem)
	{
		return IsEra(emblem) ? (emblem - First) / PerBase : emblem;
	}

	// An emblem the app can draw: a club's plain one or a season logo.
	public static bool IsDrawable(int emblem)
	{
		return emblem >= 0 && (emblem < Emblem.emblemNames.Length || IsEra(emblem));
	}

	// The file name part of an emblem ("sk", "sk_1"); past both lists the FA one (as before).
	public static string Name(int emblem)
	{
		if (IsEra(emblem))
		{
			return Emblem.emblemNames[(emblem - First) / PerBase] + "_" + (emblem - First) % PerBase;
		}
		return Emblem.emblemNames[emblem >= 0 && emblem < Emblem.emblemNames.Length ? emblem : 20];
	}

	// The emblem of club `team` (an eTeamName, a card's team code) in `year`: the logo its franchise used that season.
	public static int For(int team, int year)
	{
		int baseTeam = BaseOf(team);
		if (year <= 0)
		{
			return baseTeam;
		}
		foreach (int[][] line in Lines)
		{
			bool mine = false;
			foreach (int[] e in line)
			{
				if (e[1] == baseTeam)
				{
					mine = true;
				}
			}
			if (!mine)
			{
				continue;
			}
			int[] pick = line[0];
			foreach (int[] e in line)
			{
				if (e[0] <= year)
				{
					pick = e;
				}
			}
			return pick[2] == DefaultVariant[pick[1]] ? pick[1] : First + pick[1] * PerBase + pick[2];
		}
		return baseTeam;
	}
}
