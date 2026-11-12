using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

// Ma9M port (systems session, 2026-11-11; user 18:4x: "조합할 때 메인 카드를 기준으로 볼 합계 게이지에 따라 몇 볼의 카드가 몇 %로
// 등장하고 이런 것을 구현하고 싶은데", 19:1x layout (가)): on the 선수카드 조합 screen the "조합될 카드" bar shows the support balls
// against a full support (main ball × 7) as a ten-square gauge, the squares past the line where the main card can no longer go
// down in green, and a line under the slots gives the outcome odds ("내려감 7% · 그대로 85% · 9엘 6% · 10엘 2%"). The numbers are the
// server's: its fusion table comes in the table bundle (ma9m_fusion_odds.txt, server FusionRules.OddsTable), so a change of odds
// needs no new app; without the table only the gauge shows. A 레전드 in the slots: its own rule (support balls / 60 kept).
public static class Ma9MFusionOdds
{
	public const string TableName = "ma9m_fusion_odds.txt";

	private static double[] s_anchor;

	private static double s_full = 7.0;

	private static double s_legendBalls = 60.0;

	// main rung → (outcome rung, weight % at each anchor)
	private static Dictionary<int, List<KeyValuePair<int, double[]>>> s_odds;

	public static bool Ready
	{
		get
		{
			return s_anchor != null && s_odds != null && s_odds.Count > 0;
		}
	}

	public static void Initialize(string text)
	{
		s_anchor = null;
		s_odds = null;
		if (string.IsNullOrEmpty(text))
		{
			return;
		}
		try
		{
			Dictionary<int, List<KeyValuePair<int, double[]>>> odds = new Dictionary<int, List<KeyValuePair<int, double[]>>>();
			double[] anchor = null;
			foreach (string raw in text.Split('\n'))
			{
				string[] f = raw.Trim('\r').Split('\t');
				// The header line ("kind main out w0 …") and anything unknown are skipped (the 11-11 19:5x test build read the header's
				// "w0" as a number and dropped the whole table).
				if (f.Length < 4 || (f[0] != "anchor" && f[0] != "full" && f[0] != "legend" && f[0] != "odds"))
				{
					continue;
				}
				double[] nums = f.Skip(3).Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
				switch (f[0])
				{
				case "anchor":
					anchor = nums;
					break;
				case "full":
					s_full = nums[0];
					break;
				case "legend":
					s_legendBalls = nums[0];
					break;
				case "odds":
				{
					int main = int.Parse(f[1]);
					List<KeyValuePair<int, double[]>> list;
					if (!odds.TryGetValue(main, out list))
					{
						list = new List<KeyValuePair<int, double[]>>();
						odds[main] = list;
					}
					list.Add(new KeyValuePair<int, double[]>(int.Parse(f[2]), nums));
					break;
				}
				}
			}
			if (anchor != null && anchor.Length >= 2 && odds.Values.All(l => l.All(o => o.Value.Length == anchor.Length)))
			{
				s_anchor = anchor;
				s_odds = odds;
			}
		}
		catch (System.Exception e)
		{
			Debug.Log("[Ma9MFusionOdds] table not read: " + e.Message);
			s_anchor = null;
			s_odds = null;
		}
	}

	public static double FullPerBall
	{
		get
		{
			return s_full;
		}
	}

	public static double LegendBalls
	{
		get
		{
			return s_legendBalls;
		}
	}

	// Outcome rung and probability (0..1) of a main rung at a fill, as the server's FusionRules.Odds (linear between anchors).
	public static List<KeyValuePair<int, double>> Odds(int mainRung, double fill)
	{
		List<KeyValuePair<int, double>> result = new List<KeyValuePair<int, double>>();
		List<KeyValuePair<int, double[]>> table;
		if (!Ready || !s_odds.TryGetValue(mainRung, out table))
		{
			return result;
		}
		double f = Mathf.Clamp01((float)fill);
		int lo = 0;
		while (lo < s_anchor.Length - 2 && f > s_anchor[lo + 1])
		{
			lo++;
		}
		double t = (f - s_anchor[lo]) / (s_anchor[lo + 1] - s_anchor[lo]);
		foreach (KeyValuePair<int, double[]> o in table)
		{
			double p = (1.0 - t) * o.Value[lo] + t * o.Value[lo + 1];
			if (p > 0.0)
			{
				result.Add(new KeyValuePair<int, double>(o.Key, p / 100.0));
			}
		}
		return result;
	}

	// The fill from which the main card no longer goes down (the first anchor with no weight below the main), 0 when it never can.
	public static double SafeFill(int mainRung)
	{
		List<KeyValuePair<int, double[]>> table;
		if (!Ready || !s_odds.TryGetValue(mainRung, out table))
		{
			return -1.0;
		}
		for (int i = 0; i < s_anchor.Length; i++)
		{
			if (table.Where(o => o.Key < mainRung).All(o => o.Value[i] <= 0.0))
			{
				return s_anchor[i];
			}
		}
		return 1.0;
	}

	// A short name of an outcome next to the main card: 내려감, 그대로, then the result ("8볼", "9엘").
	public static string OutcomeName(int mainRung, int rung)
	{
		if (rung < mainRung)
		{
			return "내려감";
		}
		if (rung == mainRung)
		{
			return "그대로";
		}
		if (rung >= 9)
		{
			return (rung == 10) ? "10엘" : "9엘";
		}
		if (rung >= 7)
		{
			return (rung == 8) ? "9볼" : "8볼";
		}
		return (rung + 1) + "볼";
	}
}

public static class Ma9MFusionGauge
{
	private const int Squares = 10;

	private static UILabel s_title;

	private static string s_titleText;

	private static UILabel s_oddsLine;

	// Called after the slots change (Combine_SlotList.Reposition).
	public static void Refresh(Combine_SlotList slots)
	{
		if (slots == null)
		{
			return;
		}
		if (!Find(slots))
		{
			return;
		}
		List<PlayerData> cards = new List<PlayerData>();
		for (int i = 0; i < Combine_DataCenter.GetCombineCardCount(); i++)
		{
			PlayerData card = Combine_DataCenter.GetCombineCardData(i);
			if (card != null && card.card_info != null)
			{
				cards.Add(card);
			}
		}
		if (cards.Count == 0)
		{
			s_title.text = s_titleText;
			s_title.transform.localScale = s_titleScale;
			s_oddsLine.text = string.Empty;
			return;
		}
		PlayerData main = cards.OrderByDescending(Ma9MCombineRecipes.RungOf).ThenByDescending(c => c.card_info.ball).First();
		int mainRung = Ma9MCombineRecipes.RungOf(main);
		int balls = cards.Where(c => c != main).Sum(c => (int)c.card_info.ball);
		bool legend = mainRung == Ma9MCombineRecipes.LegendRung;
		double full = legend ? Ma9MFusionOdds.LegendBalls : Ma9MFusionOdds.FullPerBall * main.card_info.ball;
		double fill = (full > 0.0) ? Mathf.Clamp01((float)(balls / full)) : 0.0;
		double safe = legend ? -1.0 : Ma9MFusionOdds.SafeFill(mainRung);
		s_title.transform.localScale = s_titleScale * 0.85f;
		s_title.text = "재료 " + balls + "/" + (int)System.Math.Round(full) + "볼 " + Bar(fill, safe) + " " + Mathf.RoundToInt((float)(fill * 100.0)) + "%";
		if (cards.Count < 2)
		{
			s_oddsLine.text = legend ? "레전드와 함께 넣을 카드를 고르세요." : "재료 카드를 넣으면 확률이 나옵니다.";
			return;
		}
		if (legend)
		{
			s_oddsLine.text = "레전드 " + Pct(fill) + " · 10엘 " + Pct(1.0 - fill);
			return;
		}
		List<KeyValuePair<int, double>> odds = Ma9MFusionOdds.Odds(mainRung, fill);
		s_oddsLine.text = (odds.Count == 0) ? string.Empty : string.Join(" · ", odds.Select(o => Ma9MFusionOdds.OutcomeName(mainRung, o.Key) + " " + Pct(o.Value)).ToArray());
	}

	// The chance (0..1) that the main card of the slots goes down (a 레전드: comes out a 10엘), as the odds line shows; 0 with
	// fewer than two cards or without the odds table. 2026-11-12 (user 10:4x: "선수카드가 강등당할 가능성이 있는 경우에만 확인
	// 팝업을 한 번 더"): Combine_Main asks before such a combine.
	public static double DownChance()
	{
		List<PlayerData> cards = new List<PlayerData>();
		for (int i = 0; i < Combine_DataCenter.GetCombineCardCount(); i++)
		{
			PlayerData card = Combine_DataCenter.GetCombineCardData(i);
			if (card != null && card.card_info != null)
			{
				cards.Add(card);
			}
		}
		if (cards.Count < 2)
		{
			return 0.0;
		}
		PlayerData main = cards.OrderByDescending(Ma9MCombineRecipes.RungOf).ThenByDescending(c => c.card_info.ball).First();
		int mainRung = Ma9MCombineRecipes.RungOf(main);
		int balls = cards.Where(c => c != main).Sum(c => (int)c.card_info.ball);
		bool legend = mainRung == Ma9MCombineRecipes.LegendRung;
		double full = legend ? Ma9MFusionOdds.LegendBalls : Ma9MFusionOdds.FullPerBall * main.card_info.ball;
		double fill = (full > 0.0) ? Mathf.Clamp01((float)(balls / full)) : 0.0;
		if (legend)
		{
			return 1.0 - fill;
		}
		return Ma9MFusionOdds.Odds(mainRung, fill).Where(o => o.Key < mainRung).Sum(o => o.Value);
	}

	public static string DownText(double p)
	{
		return Pct(p);
	}

	private static string Pct(double p)
	{
		double v = p * 100.0;
		return ((v > 0.0 && v < 1.0) ? v.ToString("0.0", CultureInfo.InvariantCulture) : Mathf.RoundToInt((float)v).ToString()) + "%";
	}

	// Ten squares: filled ones orange below the safe line and green past it, the rest grey.
	private static string Bar(double fill, double safe)
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		int filled = Mathf.Clamp(Mathf.RoundToInt((float)(fill * Squares)), 0, Squares);
		for (int i = 0; i < Squares; i++)
		{
			string color = (i >= filled) ? "4A4A4A" : ((safe >= 0.0 && (i + 1) / (double)Squares > safe + 1e-6) ? "5CD65C" : "FF8A3D");
			sb.Append("[" + color + "]■[-]");
		}
		return sb.ToString();
	}

	private static Vector3 s_titleScale;

	// The "조합될 카드" label (Combine_Right_Label1) and, made once from it, the odds line under the slots.
	private static bool Find(Combine_SlotList slots)
	{
		if (s_title != null && s_oddsLine != null)
		{
			return true;
		}
		s_title = null;
		foreach (UILocalize loc in Object.FindObjectsByType<UILocalize>(FindObjectsInactive.Include, FindObjectsSortMode.None))
		{
			if (loc.key == "Combine_Right_Label1")
			{
				s_title = loc.GetComponent<UILabel>();
				s_titleText = (s_title != null) ? s_title.text : "조합될 카드";
				loc.enabled = false;
				break;
			}
		}
		if (s_title == null || slots.CardGrid == null)
		{
			return false;
		}
		s_titleScale = s_title.transform.localScale;
		s_title.supportEncoding = true;
		GameObject go = Object.Instantiate(s_title.gameObject) as GameObject;
		go.name = "Ma9MFusionOddsLine";
		go.transform.parent = s_title.transform.parent;
		go.transform.localRotation = s_title.transform.localRotation;
		go.transform.localScale = s_titleScale * 0.85f;
		UILocalize copyLoc = go.GetComponent<UILocalize>();
		if (copyLoc != null)
		{
			Object.Destroy(copyLoc);
		}
		s_oddsLine = go.GetComponent<UILabel>();
		s_oddsLine.supportEncoding = true;
		s_oddsLine.pivot = UIWidget.Pivot.Center;
		s_oddsLine.text = string.Empty;
		// Centred under the two rows of four slots (UIGrid cells from the grid's origin, the first cell centred there), in the gap
		// above the button row.
		UIGrid grid = slots.CardGrid;
		Vector3 local = new Vector3(1.5f * grid.cellWidth, -1.5f * grid.cellHeight - 0.08f * grid.cellHeight, 0f);
		Vector3 world = grid.transform.TransformPoint(local);
		go.transform.position = new Vector3(world.x, world.y, s_title.transform.position.z);
		go.SetActive(true);
		return true;
	}
}
