using System;
using System.Collections.Generic;

// Ma9M port (AI 테스터 8회차 불편 63, 2026-11-11: "선수교체 창의 구단 정렬 시작 구단이 열 때마다 다르다 … LG를 찾는 데 매번
// 캡처 4~5장", 제안 "내 구단을 맨 앞에"): the club sort lists by team code (두산 0 … kt 19) and a second press reverses it, so
// the first club was 두산 or kt. Now my club's lineage (TopMenu_DataCenter.m_teamEmblem; 해태·KIA, MBC·LG, 빙그레·한화, OB·두산,
// 태평양·청보·삼미·현대) comes first either way; the rest keep their order.
public static class Ma9MClubOrder
{
	private static readonly int[][] Lineages =
	{
		new[] { 0, 10 },          // 두산 · OB
		new[] { 5, 1 },           // KIA · 해태
		new[] { 6, 16 },          // LG · MBC
		new[] { 2, 14 },          // 한화 · 빙그레
		new[] { 4, 15, 17, 18 },  // 현대 · 태평양 · 청보 · 삼미
	};

	public static bool SameLine(int a, int b)
	{
		if (a == b)
		{
			return true;
		}
		foreach (int[] line in Lineages)
		{
			if (Array.IndexOf(line, a) >= 0 && Array.IndexOf(line, b) >= 0)
			{
				return true;
			}
		}
		return false;
	}

	// The list with my club's lineage moved to the front (stable).
	public static List<T> MineFirst<T>(List<T> list, Func<T, int> teamOf)
	{
		int mine = TopMenu_DataCenter.m_teamEmblem;
		if (list == null || list.Count < 2)
		{
			return list;
		}
		List<T> front = new List<T>();
		List<T> rest = new List<T>();
		foreach (T item in list)
		{
			(SameLine(teamOf(item), mine) ? front : rest).Add(item);
		}
		front.AddRange(rest);
		return front;
	}
}
