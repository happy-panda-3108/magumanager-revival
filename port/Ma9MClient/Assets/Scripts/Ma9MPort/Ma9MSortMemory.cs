using System.Collections.Generic;
using System.Linq;
using TableReader;
using UnityEngine;

// Ma9M port: the card lists' filters and sort orders stay as the user left them (user, 2026-10-19:
// 보유선수, 선수교체 and 선수조합 lost their filters after a swap or on coming back). The lists keep
// their values here (Roster_PossessPlayer, Roster_ChangePlayer, LazyLoading_CardList), SortPopup keeps
// its buttons' chosen labels; both are keyed so they match.
//
// Since the second request the same evening ("계속 적용되게"; the phone app restarts often) they are
// also saved in PlayerPrefs, so they survive a restart. Each list also keeps 제외 sets: balls and
// teams picked from the extra "… 제외" entries of the 볼/팀 popups (SortPopupPanel), shown in no list.
public static class Ma9MSortMemory
{
	// Values the 볼/팀 popups send for their 제외 entries (SortPopupPanel): 100 + ball, 1000 + team; the
	// clear entries send 199 / 1999.
	public const int ExcludeBall = 100, ClearBalls = 199, ExcludeTeam = 1000, ClearTeams = 1999;

	// Multi picks from the Normal popups (user, 2026-10-24: "7, 6, 5, 3, 2를 누르고 확인 … 제외 필터링까지 같이 섞이면"):
	// PickBase + value toggles a value in, ExBase + value toggles it out; PickClear empties that button's picks and
	// 제외, PickDone closes the popup. LazyLoading_CardList keeps them in its CardList memory.
	public const int PickBase = 20000, ExBase = 30000, PickClear = 39998, PickDone = 39999;

	// A screen's own "보기" entries at the end of the 포지션 popup (2026-11-09, 계약관리: 1군만·잠금만), ViewBase + the view
	// (LazyLoading_CardList.Ma9MViews). Above PickDone, so the popup sends them like picks and leaves their marks alone.
	public const int ViewBase = 40000;

	public static bool IsMulti(int value)
	{
		return value >= PickBase;
	}

	// Clubs with cards in this game, for the team picks (1993-2026 since 2026-11-02: OB 10, 빙그레 14 and 태평양 15 joined).
	public static readonly int[] CardTeams = new int[16] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 14, 15, 19 };

	private const string Prefix = "Ma9M_Sort/";

	// Per team (user, 2026-10-20: account A's filters showed up after logging in to account B). Keys
	// carry the team name the lobby's top bar reports (one team per account); the lists' in-memory
	// values and the other screens' static filters start over when it changes. Each team starts from
	// the defaults (the shared values saved before were some other account's).
	private static string s_owner = string.Empty;

	public static void SetOwner(string owner)
	{
		owner = owner ?? string.Empty;
		if (owner == s_owner)
		{
			return;
		}
		s_owner = owner;
		s_rosterLists.Clear();
		s_cardLists.Clear();
		s_chosenLabels.Clear();
		Roster_DataCenter.m_bSaveFilter = false;
		Roster_DataCenter.m_fMapScrollValue = 0f;
		Roster_DataCenter.m_fPossessScrollValue = 0f;
		CombineTraining_DataCenter.Ma9MResetFilters();
		SpecialLecture_DataCenter.Ma9MResetSort();
		ResetCombine();
	}

	// 선수조합's filters last only while the user stays in that menu (user, 2026-10-22: "뒤로가기로 로비에
	// 가면 필터를 초기화 … 계속 적용돼 있는 것도 불편"): the lobby (and a new team) clears the list's values,
	// its 제외 sets and the sort popup's chosen labels. Inside the menu they stay, also after a combine.
	public const string CombineList = "Combine_CardList", CombineScene = "Scene_Combine";

	public static void ResetCombine()
	{
		s_cardLists.Remove(CombineList);
		Delete("C/" + CombineList);
		foreach (string key in s_chosenLabels.Keys.Where(k => k.StartsWith(CombineScene + "/")).ToList())
		{
			s_chosenLabels.Remove(key);
		}
		foreach (object type in System.Enum.GetValues(typeof(SortPopup.eSortPopupType)))
		{
			Delete("L/" + CombineScene + "/" + type);
		}
	}

	private static void Delete(string key)
	{
		try
		{
			PlayerPrefs.DeleteKey(OwnerKey(key));
			PlayerPrefs.Save();
		}
		catch (System.Exception)
		{
		}
	}

	// The PlayerPrefs key for this team (other per-team settings, e.g. Ma9MCombineRecipes).
	public static string PrefsKey(string key)
	{
		return OwnerKey(key);
	}

	private static string OwnerKey(string key)
	{
		return string.IsNullOrEmpty(s_owner) ? (Prefix + key) : (Prefix + s_owner + "/" + key);
	}

	private static string LoadText(string key)
	{
		return PlayerPrefs.GetString(OwnerKey(key), null);
	}

	public abstract class Memory
	{
		public string Key;

		public readonly HashSet<int> ExBalls = new HashSet<int>();

		public readonly HashSet<int> ExTeams = new HashSet<int>();

		private bool m_saved;

		// Set by the lists after they copy their values in: also writes them to PlayerPrefs.
		public bool Saved
		{
			get
			{
				return m_saved;
			}
			set
			{
				m_saved = value;
				if (value)
				{
					Persist();
				}
			}
		}

		protected abstract string Body();

		protected abstract void Read(string[] parts);

		public void Persist()
		{
			try
			{
				PlayerPrefs.SetString(OwnerKey(Key), Body() + "|" + Join(ExBalls) + "|" + Join(ExTeams));
				PlayerPrefs.Save();
			}
			catch (System.Exception)
			{
			}
		}

		public void Load()
		{
			string text = LoadText(Key);
			if (string.IsNullOrEmpty(text))
			{
				return;
			}
			try
			{
				string[] parts = text.Split('|');
				Read(parts);
				Fill(ExBalls, parts[parts.Length - 2]);
				Fill(ExTeams, parts[parts.Length - 1]);
				m_saved = true;
			}
			catch (System.Exception)
			{
				m_saved = false;
			}
		}

		// A 제외 entry of the 볼/팀 popup (toggles it), or a clear entry; false for an ordinary value.
		public bool TakeExclude(int value)
		{
			if (value == ClearBalls)
			{
				ExBalls.Clear();
			}
			else if (value == ClearTeams)
			{
				ExTeams.Clear();
			}
			else if (value > ExcludeTeam && value < ClearTeams)
			{
				Toggle(ExTeams, value - ExcludeTeam);
			}
			else if (value > ExcludeBall && value < ClearBalls)
			{
				Toggle(ExBalls, value - ExcludeBall);
			}
			else
			{
				return false;
			}
			Persist();
			return true;
		}

		public bool Excluded(TRRow_PlayerCard card)
		{
			return card != null && (ExBalls.Contains(card.ball) || ExTeams.Contains(card.teamcode));
		}

		public static void Toggle(HashSet<int> set, int v)
		{
			if (!set.Remove(v))
			{
				set.Add(v);
			}
		}

		protected static string Join(IEnumerable<int> values)
		{
			return string.Join(",", values.Select(v => v.ToString()).ToArray());
		}

		protected static int[] Ints(string text)
		{
			return string.IsNullOrEmpty(text) ? new int[0] : text.Split(',').Select(int.Parse).ToArray();
		}

		protected static void Fill(HashSet<int> set, string text)
		{
			set.Clear();
			foreach (int v in Ints(text))
			{
				set.Add(v);
			}
		}
	}

	// 보유선수 / 선수교체 lists: filter on/off and value per sort type, the sort and its direction.
	public sealed class RosterList : Memory
	{
		public readonly bool[] IsFilters = new bool[6];

		public readonly int[] FilterValues = new int[6];

		public int SortType;

		public bool Toggle;

		protected override string Body()
		{
			return Join(IsFilters.Select(b => b ? 1 : 0)) + "|" + Join(FilterValues) + "|" + SortType + "|" + (Toggle ? 1 : 0);
		}

		protected override void Read(string[] parts)
		{
			int[] on = Ints(parts[0]);
			int[] values = Ints(parts[1]);
			for (int i = 0; i < IsFilters.Length && i < on.Length; i++)
			{
				IsFilters[i] = on[i] != 0;
			}
			for (int i = 0; i < FilterValues.Length && i < values.Length; i++)
			{
				FilterValues[i] = values[i];
			}
			SortType = int.Parse(parts[2]);
			Toggle = parts[3] == "1";
		}
	}

	// LazyLoading_CardList screens (선수조합, 계약/방출, 포지션 변경, ...): the chosen values, and since 2026-10-24 the
	// multi picks (values in) and the positions out (the balls and teams out are ExBalls / ExTeams).
	public sealed class CardList : Memory
	{
		public int Year;

		public int Ball;

		public int Pos;

		public int Team;

		public readonly HashSet<int> Balls = new HashSet<int>();

		public readonly HashSet<int> Teams = new HashSet<int>();

		public readonly HashSet<int> Years = new HashSet<int>();

		public readonly HashSet<int> Positions = new HashSet<int>();

		public readonly HashSet<int> ExPositions = new HashSet<int>();

		protected override string Body()
		{
			return Year + "|" + Ball + "|" + Pos + "|" + Team + "|" + Join(Balls) + "|" + Join(Teams) + "|" + Join(Years) + "|" + Join(Positions) + "|" + Join(ExPositions);
		}

		protected override void Read(string[] parts)
		{
			Year = int.Parse(parts[0]);
			Ball = int.Parse(parts[1]);
			Pos = int.Parse(parts[2]);
			Team = int.Parse(parts[3]);
			// Saved before the multi picks: four values, then the two 제외 sets.
			if (parts.Length >= 11)
			{
				Fill(Balls, parts[4]);
				Fill(Teams, parts[5]);
				Fill(Years, parts[6]);
				Fill(Positions, parts[7]);
				Fill(ExPositions, parts[8]);
			}
		}
	}

	// A popup button's picks and 제외 set in a card list (null where the button has none).
	public static HashSet<int> Picks(CardList list, SortPopup.eSortType type)
	{
		switch (type)
		{
		case SortPopup.eSortType.Ball:
			return list.Balls;
		case SortPopup.eSortType.Team:
			return list.Teams;
		case SortPopup.eSortType.Year:
			return list.Years;
		case SortPopup.eSortType.Position:
			return list.Positions;
		default:
			return null;
		}
	}

	public static HashSet<int> Excludes(CardList list, SortPopup.eSortType type)
	{
		switch (type)
		{
		case SortPopup.eSortType.Ball:
			return list.ExBalls;
		case SortPopup.eSortType.Team:
			return list.ExTeams;
		case SortPopup.eSortType.Position:
			return list.ExPositions;
		default:
			return null;
		}
	}

	private static readonly Dictionary<int, string> PositionKeys = new Dictionary<int, string>
	{
		{ 16, "Position_Batter" }, { 0, "Position_Pitcher" }, { 1, "Position_C" }, { 2, "Position_1B" }, { 3, "Position_2B" },
		{ 4, "Position_3B" }, { 5, "Position_SS" }, { 6, "Position_LF" }, { 7, "Position_CF" }, { 8, "Position_RF" },
		{ 10, "Position_DH" }, { 12, "Position_SP" }, { 13, "Position_RP" }, { 14, "Position_SU" }, { 15, "Position_CP" }
	};

	public static string ValueName(SortPopup.eSortType type, int v)
	{
		switch (type)
		{
		case SortPopup.eSortType.Ball:
			return v + "볼";
		case SortPopup.eSortType.Team:
			return (v >= 0 && v < namestring_KR.teams.Length) ? namestring_KR.teams[v] : v.ToString();
		case SortPopup.eSortType.Year:
			return "'" + (v % 100).ToString("00");
		case SortPopup.eSortType.Position:
		{
			string key;
			return PositionKeys.TryGetValue(v, out key) ? Localization.instance.Get(key) : v.ToString();
		}
		default:
			return v.ToString();
		}
	}

	// The popup button's label for its picks and 제외 (short, it is a small button); null when there are none.
	public static string Summary(SortPopup.eSortType type, ICollection<int> picks, ICollection<int> excludes)
	{
		int inCount = (picks != null) ? picks.Count : 0;
		int outCount = (excludes != null) ? excludes.Count : 0;
		if (inCount == 0 && outCount == 0)
		{
			return null;
		}
		string unit = (type == SortPopup.eSortType.Ball) ? "볼" : ((type == SortPopup.eSortType.Team) ? "팀" : ((type == SortPopup.eSortType.Year) ? "연도" : "포지션"));
		if (inCount > 0 && outCount > 0)
		{
			return unit + inCount + "·제외" + outCount;
		}
		if (inCount > 0)
		{
			IEnumerable<int> ordered = (type == SortPopup.eSortType.Ball || type == SortPopup.eSortType.Year) ? picks.OrderByDescending(v => v) : picks.OrderBy(v => v);
			if (inCount == 1)
			{
				return ValueName(type, ordered.First());
			}
			if (inCount == 2 && type != SortPopup.eSortType.Team)
			{
				return string.Join("·", ordered.Select(v => (type == SortPopup.eSortType.Ball) ? v.ToString() : ValueName(type, v)).ToArray()) + ((type == SortPopup.eSortType.Ball) ? "볼" : string.Empty);
			}
			return unit + " " + inCount + "개";
		}
		return (outCount == 1) ? (ValueName(type, excludes.First()) + " 제외") : ("제외 " + outCount + "개");
	}

	private static readonly Dictionary<string, RosterList> s_rosterLists = new Dictionary<string, RosterList>();

	private static readonly Dictionary<string, CardList> s_cardLists = new Dictionary<string, CardList>();

	private static readonly Dictionary<string, Dictionary<int, string>> s_chosenLabels = new Dictionary<string, Dictionary<int, string>>();

	public static RosterList Roster(string key)
	{
		RosterList list;
		if (!s_rosterLists.TryGetValue(key, out list))
		{
			list = new RosterList { Key = "R/" + key };
			list.Load();
			s_rosterLists.Add(key, list);
		}
		return list;
	}

	public static CardList Cards(string key)
	{
		CardList list;
		if (!s_cardLists.TryGetValue(key, out list))
		{
			list = new CardList { Key = "C/" + key };
			list.Load();
			s_cardLists.Add(key, list);
		}
		return list;
	}

	// SortPopup button index → the chosen value's label, per scene and popup type.
	public static Dictionary<int, string> ChosenLabels(string key)
	{
		Dictionary<int, string> labels;
		if (!s_chosenLabels.TryGetValue(key, out labels))
		{
			labels = new Dictionary<int, string>();
			string text = LoadText("L/" + key);
			if (!string.IsNullOrEmpty(text))
			{
				foreach (string pair in text.Split('\n'))
				{
					int tab = pair.IndexOf('\t');
					int index;
					if (tab > 0 && int.TryParse(pair.Substring(0, tab), out index))
					{
						labels[index] = pair.Substring(tab + 1);
					}
				}
			}
			s_chosenLabels.Add(key, labels);
		}
		return labels;
	}

	// SortPopup calls this after changing a key's labels.
	public static void SaveLabels(string key)
	{
		Dictionary<int, string> labels;
		if (s_chosenLabels.TryGetValue(key, out labels))
		{
			try
			{
				PlayerPrefs.SetString(OwnerKey("L/" + key), string.Join("\n", labels.Select(p => p.Key + "\t" + p.Value).ToArray()));
				PlayerPrefs.Save();
			}
			catch (System.Exception)
			{
			}
		}
	}
}
