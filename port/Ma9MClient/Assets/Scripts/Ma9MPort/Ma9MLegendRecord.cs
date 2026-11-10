using System.Collections.Generic;

// Ma9M port (user, 2026-11-09: "레전드들은 통산 기록이 나오게끔 해줄 수 있나?"): the legends' KBO career records, from the table
// bundle's ma9m_legend_record.txt (tools/scripts/legend_record.py). The card tables keep one season in their record columns,
// since a client reads W/L/HLD/SV as bytes there (club totals froze every phone on 2026-11-08). Each row holds the record
// window's eight texts in its order (pitcher G, ERA, W, L, SV, IP, K, H; batter G, AVG, AB, H, HR, RBI, R, SB); a missing or
// broken table only leaves the window on the card's season.
public static class Ma9MLegendRecord
{
	private static readonly Dictionary<int, string[]> s_rows = new Dictionary<int, string[]>();

	public static void Initialize(string table)
	{
		s_rows.Clear();
		if (string.IsNullOrEmpty(table))
		{
			return;
		}
		string[] lines = table.Split('\n');
		for (int i = 1; i < lines.Length; i++)
		{
			string[] c = lines[i].TrimEnd('\r').Split('\t');
			int idx;
			if (c.Length < 11 || !int.TryParse(c[0], out idx))
			{
				continue;
			}
			string[] cells = new string[8];
			System.Array.Copy(c, 3, cells, 0, 8);
			s_rows[idx] = cells;
		}
	}

	// Puts the career line into the record window's first row (labels[0] "통산", labels[1..8] the cells); false when the
	// table has no row for the card.
	public static bool Fill(UILabel[] labels, int cardIdx)
	{
		string[] cells;
		if (labels == null || labels.Length < 9 || !s_rows.TryGetValue(cardIdx, out cells))
		{
			return false;
		}
		labels[0].text = "통산";
		for (int i = 0; i < 8; i++)
		{
			labels[i + 1].text = cells[i];
		}
		return true;
	}
}
