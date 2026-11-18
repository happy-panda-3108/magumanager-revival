using System;
using System.Collections.Generic;
using System.Linq;

// Ma9M port (systems session, 2026-11-13; user 16:5x 결정 11 (가): "로스터에서 구단과 볼을 함께 거르는 방법으로는 카드 목록 방식으로
// 통일하면 되겠음"; AI tester 45: "LG이면서 1~2볼인 타자"를 찾을 수 없었음): 보유선수 (Roster_PossessPlayer) and 선수교체
// (Roster_ChangePlayer) filter as the card lists do — a short press sorts, a long press opens the 볼·팀·연도·포지션 popup in its
// multi mode (values in, values out, 모두 해제, 확인; SortPopupPanel.Ma9MSetMulti), every button's picks hold together, the
// button shows them (Ma9MSortMemory.Summary), and the sort before the current one orders the cards inside its groups.
// The picks live in the list's Ma9MSortMemory.RosterList; a card passes a button when it is under one of its values in
// Roster_DataCenter's dictionary for that sort (the grouping the single filter and the sort have always used). The 타순
// button (Lineup) keeps its single value, cleared by a short press as before.
public static class Ma9MRosterPicks
{
	// The eROSTER_POSSESSVIEW_TYPE index of a popup button's single filter (IsFilters / FilterValues), -1 for none.
	public static int FilterIndex(SortPopup.eSortType type)
	{
		switch (type)
		{
		case SortPopup.eSortType.Position:
			return 1;
		case SortPopup.eSortType.Year:
			return 2;
		case SortPopup.eSortType.Team:
			return 3;
		case SortPopup.eSortType.Ball:
			return 4;
		case SortPopup.eSortType.Lineup:
			return 5;
		default:
			return -1;
		}
	}

	public static bool IsMultiButton(SortPopup.eSortType type)
	{
		return type == SortPopup.eSortType.Ball || type == SortPopup.eSortType.Team || type == SortPopup.eSortType.Year || type == SortPopup.eSortType.Position;
	}

	// A popup value → the key of Roster_DataCenter's dictionary (the pitchers' positions 12-15 are 0-3 there, as the lists'
	// single filter subtracts).
	private static int Key(SortPopup.eSortType type, int v, bool batter)
	{
		return (type == SortPopup.eSortType.Position && !batter) ? (v - 12) : v;
	}

	// A multi pick (Ma9MSortMemory.PickBase …) for one button: toggles a value in or out, clears the button, or (확인) does
	// nothing; a value chosen the old single way joins the picks first. False when the value is no multi pick.
	public static bool Take(Ma9MSortMemory.RosterList m, bool[] isFilters, int[] filterValues, SortPopup.eSortType type, int value)
	{
		if (!Ma9MSortMemory.IsMulti(value))
		{
			return false;
		}
		HashSet<int> picks = Ma9MSortMemory.Picks(m, type);
		if (picks == null)
		{
			return false;
		}
		HashSet<int> excludes = Ma9MSortMemory.Excludes(m, type);
		int f = FilterIndex(type);
		if (f >= 0 && isFilters[f])
		{
			picks.Add(filterValues[f]);
			isFilters[f] = false;
		}
		if (value == Ma9MSortMemory.PickClear)
		{
			picks.Clear();
			if (excludes != null)
			{
				excludes.Clear();
			}
		}
		else if (value == Ma9MSortMemory.PickDone || value >= Ma9MSortMemory.ViewBase)
		{
		}
		else if (value >= Ma9MSortMemory.ExBase)
		{
			if (excludes != null)
			{
				Ma9MSortMemory.Memory.Toggle(excludes, value - Ma9MSortMemory.ExBase);
			}
		}
		else
		{
			Ma9MSortMemory.Memory.Toggle(picks, value - Ma9MSortMemory.PickBase);
		}
		return true;
	}

	// The cards that pass every button's picks and the positions left out (the balls and teams out are the lists' own
	// SortMemory.Excluded, as before).
	// singleYear / singleTeam: the lists' old single year and team filter (IsFilters / FilterValues), −1 for none; they join the picks
	// here (2026-11-18) so FA and 레전드 cards are let through the same way.
	public static List<T> Apply<T>(Ma9MSortMemory.RosterList m, List<T> cards, bool batter, Func<T, int> idx, MultiSortedDictionary<int, T> pos,
		MultiSortedDictionary<int, T> year, MultiSortedDictionary<int, T> team, MultiSortedDictionary<int, T> ball, int singleYear = -1, int singleTeam = -1)
	{
		IEnumerable<T> result = cards;
		HashSet<int> teamPicks = With(Ma9MSortMemory.Picks(m, SortPopup.eSortType.Team), singleTeam);
		foreach ((SortPopup.eSortType type, MultiSortedDictionary<int, T> dict) in new[]
		{
			(SortPopup.eSortType.Position, pos), (SortPopup.eSortType.Year, year), (SortPopup.eSortType.Team, team), (SortPopup.eSortType.Ball, ball)
		})
		{
			HashSet<int> picks = (type == SortPopup.eSortType.Team) ? teamPicks : (type == SortPopup.eSortType.Year) ? With(Ma9MSortMemory.Picks(m, type), singleYear) : Ma9MSortMemory.Picks(m, type);
			if (picks != null && picks.Count > 0 && dict != null)
			{
				HashSet<int> keep = Members(dict, picks.Select(v => Key(type, v, batter)), idx);
				if (type == SortPopup.eSortType.Team)
				{
					// FA 선수카드 (2026-11-15): an FA card also under its source card's club, and (11-18) under our own club.
					foreach (T c in cards)
					{
						if (Ma9MSortMemory.TeamIn(picks, idx(c)))
						{
							keep.Add(idx(c));
						}
					}
				}
				else if (type == SortPopup.eSortType.Year && teamPicks.Count > 0)
				{
					// (2026-11-18) An FA or 레전드 card under a picked club, whatever its season.
					foreach (T c in cards)
					{
						if (Ma9MSortMemory.YearFree(teamPicks, idx(c)))
						{
							keep.Add(idx(c));
						}
					}
				}
				result = result.Where(c => keep.Contains(idx(c)));
			}
		}
		if (m.ExPositions.Count > 0 && pos != null)
		{
			HashSet<int> gone = Members(pos, m.ExPositions.Select(v => Key(SortPopup.eSortType.Position, v, batter)), idx);
			result = result.Where(c => !gone.Contains(idx(c)));
		}
		return result.ToList();
	}

	private static HashSet<int> With(HashSet<int> picks, int single)
	{
		HashSet<int> all = (picks != null) ? new HashSet<int>(picks) : new HashSet<int>();
		if (single >= 0)
		{
			all.Add(single);
		}
		return all;
	}

	// The single filter of a type that Apply takes over (year, team), or −1.
	public static int Single(bool[] isFilters, int[] filterValues, SortPopup.eSortType type)
	{
		int f = FilterIndex(type);
		return (f >= 0 && f < isFilters.Length && isFilters[f]) ? filterValues[f] : -1;
	}

	// The single filters the lists leave to Apply (year 2, team 3).
	public static bool ByApply(int filterIndex)
	{
		return filterIndex == FilterIndex(SortPopup.eSortType.Year) || filterIndex == FilterIndex(SortPopup.eSortType.Team);
	}

	private static HashSet<int> Members<T>(MultiSortedDictionary<int, T> dict, IEnumerable<int> keys, Func<T, int> idx)
	{
		HashSet<int> set = new HashSet<int>();
		foreach (int k in keys)
		{
			if (!dict.ContainsKey(k))
			{
				continue;
			}
			foreach (T c in dict[k])
			{
				set.Add(idx(c));
			}
		}
		return set;
	}

	// For SortPopupPanel's marks and the button label (IMa9MPickList of the lists).
	public static bool IsPicked(Ma9MSortMemory.RosterList m, bool[] isFilters, int[] filterValues, SortPopup.eSortType type, int v)
	{
		HashSet<int> picks = Ma9MSortMemory.Picks(m, type);
		int f = FilterIndex(type);
		return (picks != null && picks.Contains(v)) || (f >= 0 && isFilters[f] && filterValues[f] == v);
	}

	public static bool IsExcluded(Ma9MSortMemory.RosterList m, SortPopup.eSortType type, int v)
	{
		HashSet<int> excludes = Ma9MSortMemory.Excludes(m, type);
		return excludes != null && excludes.Contains(v);
	}

	public static string Summary(Ma9MSortMemory.RosterList m, bool[] isFilters, int[] filterValues, SortPopup.eSortType type)
	{
		HashSet<int> picks = Ma9MSortMemory.Picks(m, type);
		if (picks == null)
		{
			return null;
		}
		List<int> all = picks.ToList();
		int f = FilterIndex(type);
		if (f >= 0 && isFilters[f] && !all.Contains(filterValues[f]))
		{
			all.Add(filterValues[f]);
		}
		return Ma9MSortMemory.Summary(type, all, Ma9MSortMemory.Excludes(m, type));
	}

	// The current sort's groups in its order (desc reversed, as the lists' IsToggleSort does), the cards inside a group in the
	// order of the sort before it (결정 11 (가): "직전 정렬을 2순위로 유지"). Cards a dictionary lacks go last, in their order.
	public static List<T> SortTwo<T>(List<T> cards, MultiSortedDictionary<int, T> primary, bool primaryDesc, MultiSortedDictionary<int, T> secondary, bool secondaryDesc, Func<T, int> idx)
	{
		Dictionary<int, int> group = new Dictionary<int, int>();
		int g = 0;
		foreach (List<T> list in primary.values)
		{
			foreach (T c in list)
			{
				if (!group.ContainsKey(idx(c)))
				{
					group[idx(c)] = g;
				}
			}
			g++;
		}
		Dictionary<int, int> second = new Dictionary<int, int>();
		int n = 0;
		foreach (List<T> list in secondary.values)
		{
			foreach (T c in list)
			{
				if (!second.ContainsKey(idx(c)))
				{
					second[idx(c)] = n++;
				}
			}
		}
		int Rank(Dictionary<int, int> ranks, T c, bool desc)
		{
			int r;
			return ranks.TryGetValue(idx(c), out r) ? (desc ? -r : r) : int.MaxValue;
		}
		return cards.OrderBy(c => Rank(group, c, primaryDesc)).ThenBy(c => Rank(second, c, secondaryDesc)).ToList();
	}

	// Whether a 2순위 applies: a sort before the current one that is a real sort and a different one.
	public static bool HasSecond(int current, int previous)
	{
		return previous > 0 && current > 0 && previous != current;
	}
}
