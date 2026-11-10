using System;

// Ma9M port (2026-11-10, AI tester: "MP주머니 을(를) 사용하시겠습니까?"): the original texts write both particles, 을(를), 이(가),
// 은(는), 과(와) and (으)로, after a name filled in at run time. Fix picks the one that fits the last Hangul syllable before it
// (a final consonant or not; 로 after ㄹ), skipping colour tags ([FF4242], [-]) and quotes, and drops the space the texts put
// before it ("{0:G} 을(를)"). After anything else (Latin letters, digits) the text stays as it is. PopupManager runs every
// popup message through it.
public static class Ma9MJosa
{
	private static readonly string[][] Pairs = new string[7][]
	{
		new string[3] { "으로(로)", "으로", "로" },
		new string[3] { "(으)로", "으로", "로" },
		new string[3] { "을(를)", "을", "를" },
		new string[3] { "이(가)", "이", "가" },
		new string[3] { "은(는)", "은", "는" },
		new string[3] { "과(와)", "과", "와" },
		new string[3] { "와(과)", "과", "와" }
	};

	public static string Fix(string text)
	{
		if (string.IsNullOrEmpty(text) || text.IndexOf(')') < 0)
		{
			return text;
		}
		foreach (string[] pair in Pairs)
		{
			int from = 0;
			int at;
			while (from < text.Length && (at = text.IndexOf(pair[0], from, StringComparison.Ordinal)) >= 0)
			{
				int start = (at > 0 && text[at - 1] == ' ') ? (at - 1) : at;
				int c = start - 1;
				while (c >= 0)
				{
					if (text[c] == ']')
					{
						int open = text.LastIndexOf('[', c);
						if (open < 0)
						{
							break;
						}
						c = open - 1;
					}
					else if (text[c] == '"' || text[c] == '\'')
					{
						c--;
					}
					else
					{
						break;
					}
				}
				int final;
				if (c >= 0 && text[c] >= '가' && text[c] <= '힣')
				{
					final = (text[c] - '가') % 28;
				}
				else if (Roman(text, c, out final))
				{
				}
				else
				{
					from = at + pair[0].Length;
					continue;
				}
				bool rieul = final == 8;
				bool withFinal = final != 0;
				bool ro = pair[2] == "로";
				string pick = ((ro ? (withFinal && !rieul) : withFinal) ? pair[1] : pair[2]);
				// The space goes only when the particle follows the name directly (no tag or quote between).
				int cut = (start < at && c == start - 1) ? start : at;
				text = text.Substring(0, cut) + pick + text.Substring(at + pair[0].Length);
				from = cut + pick.Length;
			}
		}
		return text;
	}

	// A book grade at the end of a name (트레이닝북 I, II, III, IV, read 일 이 삼 사): its final consonant as a Hangul syllable's
	// (8 = ㄹ, 16 = ㅁ, 0 = none).
	private static bool Roman(string text, int c, out int final)
	{
		final = 0;
		int b = c;
		while (b >= 0 && (text[b] == 'I' || text[b] == 'V'))
		{
			b--;
		}
		if (b == c || (b >= 0 && text[b] != ' '))
		{
			return false;
		}
		switch (text.Substring(b + 1, c - b))
		{
		case "I":
			final = 8;
			return true;
		case "II":
			final = 0;
			return true;
		case "III":
			final = 16;
			return true;
		case "IV":
			final = 0;
			return true;
		default:
			return false;
		}
	}
}
