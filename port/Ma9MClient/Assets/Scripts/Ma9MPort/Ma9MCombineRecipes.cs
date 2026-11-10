using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Ma9M port (user, 2026-10-22: "가장 최근 조합 방식을 그대로 다시 쓰거나, 조합 방식을 저장해 두고 쓰는 기능 … 7552222를
// 반복할 때 매번 7장을 올리는 게 시간 낭비 … 체계적으로 고민해서 잘 만들어 달라"): 선수조합 recipes. A recipe is the main
// card's rung (FusionRules ladder: grade and ball) and side, and the balls of the support cards (only balls count for
// support). Every successful combine is remembered as the last recipe and as the recipe for its main card's rung;
// 조합 저장 adds the slots to the saved list (newest first, up to 20). A saved recipe can be pinned (user, 2026-10-24:
// "저장 목록에서 불러온 다음, 해당 조합법으로 고정해두고 자동 등록"): while one is, Combine_Main's 고정 조합 always fills
// the slots from it, whatever was combined last. Kept per team in PlayerPrefs (Ma9MSortMemory's owner key).
public static class Ma9MCombineRecipes
{
	public sealed class Recipe
	{
		public int MainRung;

		public int MainBall;

		public int MainGrade;

		public bool MainBatter;

		public int[] Supports = new int[0];

		public string Text
		{
			get
			{
				string main = (MainRung == LegendRung) ? "레전드" : (MainBall + "볼 " + GradeName(MainGrade));
				return (Supports.Length == 0) ? main : (main + " + " + string.Join("·", Supports.Select(b => b.ToString()).ToArray()));
			}
		}

		public string Save()
		{
			return MainRung + "," + MainBall + "," + MainGrade + "," + (MainBatter ? 1 : 0) + ";" + string.Join(",", Supports.Select(b => b.ToString()).ToArray());
		}

		public static Recipe Load(string text)
		{
			try
			{
				string[] parts = text.Split(';');
				string[] main = parts[0].Split(',');
				Recipe r = new Recipe
				{
					MainRung = int.Parse(main[0]),
					MainBall = int.Parse(main[1]),
					MainGrade = int.Parse(main[2]),
					MainBatter = main[3] == "1"
				};
				r.Supports = (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) ? new int[0] : parts[1].Split(',').Select(int.Parse).ToArray();
				// A 레전드 recipe kept before 11-10 has the 엘리트 rung of its ball.
				if (r.MainGrade == 5)
				{
					r.MainRung = LegendRung;
				}
				return r;
			}
			catch (System.Exception)
			{
				return null;
			}
		}

		// The cards of a combine: the main card is the highest rung (then ball), as the server picks it.
		public static Recipe From(IList<PlayerData> cards)
		{
			if (cards == null || cards.Count == 0)
			{
				return null;
			}
			PlayerData main = cards.Where(c => c != null && c.card_info != null).OrderByDescending(c => RungOf(c)).ThenByDescending(c => c.card_info.ball).FirstOrDefault();
			if (main == null)
			{
				return null;
			}
			Recipe r = new Recipe
			{
				MainRung = RungOf(main),
				MainBall = main.card_info.ball,
				MainGrade = (int)main.card_info.cardgrade,
				MainBatter = main.card_info.isBatter
			};
			r.Supports = cards.Where(c => c != null && c != main && c.card_info != null).Select(c => (int)c.card_info.ball).OrderByDescending(b => b).ToArray();
			return r;
		}
	}

	// A 레전드 card of any ball (user, 2026-11-10 23:2x: "레전드 조합할 때 레전드 볼수에 관계없이 최근 조합 하면 레전드가 올라가고, 볼수에
	// 맞게 등록되게 … 레전드도 일반 9볼 8볼처럼 자동으로 올라가는 모습임"): above every other rung, so a combine with a 레전드 has it
	// as its main card (the server's 레전드 조합 does the same, and refuses two 레전드), its recipe is kept apart from the 엘리트
	// ones, and the fill never takes a 레전드 as a support.
	public const int LegendRung = 11;

	public static bool IsLegend(PlayerData card)
	{
		return card != null && card.card_info != null && (int)card.card_info.cardgrade == 5;
	}

	// FusionHandlers.RungOf: 1-4볼 노멀 0-3, 5-7볼 스페셜 4-6, 8볼 레어 7, 9볼 레어 8, 9볼 엘리트 9, 10볼 엘리트 10 (레전드 LegendRung).
	public static int RungOf(PlayerData card)
	{
		if (card == null || card.card_info == null)
		{
			return -1;
		}
		int grade = (int)card.card_info.cardgrade;
		int ball = card.card_info.ball;
		if (grade == 5)
		{
			return LegendRung;
		}
		if (grade >= 4)
		{
			return (ball >= 10) ? 10 : 9;
		}
		if (grade == 3)
		{
			return (ball >= 9) ? 8 : 7;
		}
		return Mathf.Clamp(ball - 1, 0, 6);
	}

	public static string GradeName(int grade)
	{
		switch (grade)
		{
		case 1:
			return "노멀";
		case 2:
			return "스페셜";
		case 3:
			return "레어";
		case 4:
			return "엘리트";
		case 5:
			return "레전드";
		case 6:
			return "골든글러브";
		default:
			return "카드";
		}
	}

	private const string Key = "Combine/Recipes";

	private const int MaxKept = 12;

	private const int MaxSaved = 20;

	private static string s_loadedFor;

	private static Recipe s_last;

	// By main rung, most recently used first.
	private static readonly List<Recipe> s_byMain = new List<Recipe>();

	private static Recipe s_pending;

	// 조합 저장, newest first.
	private static readonly List<Recipe> s_saved = new List<Recipe>();

	private static Recipe s_pinned;

	public static Recipe Last
	{
		get
		{
			Load();
			return s_last;
		}
	}

	public static Recipe ForMain(int rung)
	{
		Load();
		return s_byMain.FirstOrDefault(r => r.MainRung == rung);
	}

	public static List<Recipe> Saved()
	{
		Load();
		return s_saved.ToList();
	}

	public static Recipe Pinned
	{
		get
		{
			Load();
			return s_pinned;
		}
	}

	public static bool IsPinned(Recipe recipe)
	{
		return recipe != null && Pinned != null && Pinned.Save() == recipe.Save();
	}

	// 조합 저장: into the saved list (the same recipe once, newest first) and remembered like a combine.
	public static void Save(Recipe recipe)
	{
		if (recipe == null)
		{
			return;
		}
		Load();
		s_saved.RemoveAll(r => r.Save() == recipe.Save());
		s_saved.Insert(0, recipe);
		if (s_saved.Count > MaxSaved)
		{
			s_saved.RemoveRange(MaxSaved, s_saved.Count - MaxSaved);
		}
		Remember(recipe);
	}

	// null unpins.
	public static void Pin(Recipe recipe)
	{
		Load();
		s_pinned = recipe;
		Persist();
	}

	// A combine is being sent; it is remembered when it succeeds (Commit).
	public static void SetPending(Recipe recipe)
	{
		s_pending = recipe;
	}

	public static void CommitPending()
	{
		if (s_pending != null)
		{
			Remember(s_pending);
			s_pending = null;
		}
	}

	public static void Remember(Recipe recipe)
	{
		if (recipe == null)
		{
			return;
		}
		Load();
		s_last = recipe;
		s_byMain.RemoveAll(r => r.MainRung == recipe.MainRung);
		s_byMain.Insert(0, recipe);
		if (s_byMain.Count > MaxKept)
		{
			s_byMain.RemoveRange(MaxKept, s_byMain.Count - MaxKept);
		}
		Persist();
	}

	public static void Clear()
	{
		Load();
		s_last = null;
		s_byMain.Clear();
		s_saved.Clear();
		s_pinned = null;
		Persist();
	}

	private static void Load()
	{
		string owner = Ma9MSortMemory.PrefsKey(Key);
		if (s_loadedFor == owner)
		{
			return;
		}
		s_loadedFor = owner;
		s_last = null;
		s_byMain.Clear();
		s_saved.Clear();
		s_pinned = null;
		string text = null;
		try
		{
			text = PlayerPrefs.GetString(owner, null);
		}
		catch (System.Exception)
		{
		}
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		bool version2 = false;
		foreach (string line in text.Split('\n'))
		{
			if (line == "V|2")
			{
				version2 = true;
				continue;
			}
			if (line.Length < 2)
			{
				continue;
			}
			Recipe r = Recipe.Load(line.Substring(2));
			if (r == null)
			{
				continue;
			}
			if (line[0] == 'L')
			{
				s_last = r;
			}
			else if (line[0] == 'M' && s_byMain.All(x => x.MainRung != r.MainRung))
			{
				s_byMain.Add(r);
			}
			else if (line[0] == 'S' && s_saved.Count < MaxSaved)
			{
				s_saved.Add(r);
			}
			else if (line[0] == 'P')
			{
				s_pinned = r;
			}
		}
		// Before the saved list (10-24) 조합 저장 went into the remembered ones: they start the list.
		if (!version2)
		{
			s_saved.AddRange(s_byMain);
		}
	}

	private static void Persist()
	{
		List<string> lines = new List<string> { "V|2" };
		if (s_last != null)
		{
			lines.Add("L|" + s_last.Save());
		}
		foreach (Recipe r in s_byMain)
		{
			lines.Add("M|" + r.Save());
		}
		foreach (Recipe r in s_saved)
		{
			lines.Add("S|" + r.Save());
		}
		if (s_pinned != null)
		{
			lines.Add("P|" + s_pinned.Save());
		}
		try
		{
			PlayerPrefs.SetString(Ma9MSortMemory.PrefsKey(Key), string.Join("\n", lines.ToArray()));
			PlayerPrefs.Save();
		}
		catch (System.Exception)
		{
		}
	}
}
