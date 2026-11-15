using System.Collections.Generic;

// Ma9M port (user, 2026-10-20: "재계약할 때 한 스탯을 누르고 +3이 되는 것보다, 보너스 스탯 3을 주고 원하는대로
// 찍을 수 있게 … 컨택1, 파워2 이런식으로"): a renewal's three bonus points, one per + press, on the 계약관리
// screen (ContractManage_Main) and the player window's 계약 tab (PlayerInfo_ContractManagementWindow).
// They go in PKS_CS_EXTEND_CONTRACT.bonus_stat as 32 + a·36 + b·6 + c, +1 each to stats a, b and c
// (the server's ContractHandlers.BonusPoints; 0–5 still means +3 to that stat).
public static class Ma9MContractPoints
{
	public const int CardPoints = 3;

	// FA 선수카드 (systems session, 2026-11-15; user 18:3x via the engine session: "계약 연장 시 한 번에 14일씩 증가 … 계약 연장때마다 5의
	// 보너스 스탯"): an FA card's renewal has five points and 14 days (server ContractHandlers.FaBonusPerRenewal / FaExtendDays). The
	// screens say which card it is (ForCard) before its points go; its request byte is the index of the five-point split (FaSplits,
	// the server's order), as a byte cannot hold both kinds of code.
	public const int FaPoints = 5;

	public const int CardDays = 7;

	public const int FaDays = 14;

	public const int FaTeam = 250;

	public static bool Fa;

	public static int PerRenewal
	{
		get
		{
			return Fa ? FaPoints : CardPoints;
		}
	}

	public static bool IsFa(int cardIdx)
	{
		TableReader.TRRow_PlayerCard card = (ResourceManager.g_playerTable != null) ? ResourceManager.g_playerTable.FindPlayer(cardIdx) : null;
		return card != null && card.teamcode == FaTeam;
	}

	// Days a renewal adds to this card.
	public static int ExtendDaysFor(int cardIdx)
	{
		return IsFa(cardIdx) ? FaDays : CardDays;
	}

	// The card whose renewal is being set up: its kind, no points yet.
	public static void ForCard(int cardIdx)
	{
		Fa = IsFa(cardIdx);
		s_picks.Clear();
	}

	// Every split of FaPoints over the six stats: a ascending, then b, … each 0..what is left, the last stat the rest (the server's
	// ContractHandlers.Splits). 252 of them.
	private static int[][] s_faSplits;

	private static int[][] FaSplits
	{
		get
		{
			if (s_faSplits == null)
			{
				List<int[]> list = new List<int[]>();
				int[] cur = new int[6];
				FillSplits(list, cur, 0, FaPoints);
				s_faSplits = list.ToArray();
			}
			return s_faSplits;
		}
	}

	private static void FillSplits(List<int[]> list, int[] cur, int k, int left)
	{
		if (k == 5)
		{
			cur[5] = left;
			list.Add((int[])cur.Clone());
			return;
		}
		for (int v = 0; v <= left; v++)
		{
			cur[k] = v;
			FillSplits(list, cur, k + 1, left - v);
		}
	}

	private static byte FaCode()
	{
		int[] counts = new int[6];
		for (int i = 0; i < s_picks.Count; i++)
		{
			counts[s_picks[i]]++;
		}
		int[][] splits = FaSplits;
		for (int s = 0; s < splits.Length; s++)
		{
			bool same = true;
			for (int k = 0; k < 6 && same; k++)
			{
				same = splits[s][k] == counts[k];
			}
			if (same)
			{
				return (byte)s;
			}
		}
		return byte.MaxValue;
	}

	private static readonly List<int> s_picks = new List<int>(FaPoints);

	// What the last renewal request gave each stat, for the screens to update their copy on success.
	public static int[] Sent = new int[6];

	public static int Used
	{
		get
		{
			return s_picks.Count;
		}
	}

	public static int Left
	{
		get
		{
			return PerRenewal - s_picks.Count;
		}
	}

	public static bool Full
	{
		get
		{
			return s_picks.Count >= PerRenewal;
		}
	}

	public static void Clear()
	{
		s_picks.Clear();
	}

	public static bool Add(int stat)
	{
		if (Full || stat < 0 || stat >= 6)
		{
			return false;
		}
		s_picks.Add(stat);
		return true;
	}

	// Points on one stat so far.
	public static int On(int stat)
	{
		int n = 0;
		for (int i = 0; i < s_picks.Count; i++)
		{
			if (s_picks[i] == stat)
			{
				n++;
			}
		}
		return n;
	}

	// The request byte (255 until all the points are placed).
	public static byte Code
	{
		get
		{
			if (!Full)
			{
				return byte.MaxValue;
			}
			return Fa ? FaCode() : ((byte)(32 + s_picks[0] * 36 + s_picks[1] * 6 + s_picks[2]));
		}
	}

	// Points per stat in a request byte: 0–5 = +3 to that stat, 32–247 = the three single points; for an FA card (Fa) the split's index.
	public static int[] Decode(byte code)
	{
		int[] points = new int[6];
		if (Fa)
		{
			if (code < FaSplits.Length)
			{
				points = (int[])FaSplits[code].Clone();
			}
			return points;
		}
		if (code < 6)
		{
			points[code] = PerRenewal;
		}
		else if (code >= 32 && code < 32 + 216)
		{
			int v = code - 32;
			points[v / 36]++;
			points[v / 6 % 6]++;
			points[v % 6]++;
		}
		return points;
	}

	// The renewal request is going out: remember its points for the success reply.
	public static void MarkSent(byte code)
	{
		Sent = Decode(code);
		s_mpBefore = TopMenu_DataCenter.MyMP;
	}

	// User, 2026-11-10 19:0x (decision 1 (나): an app on old tables showed 320,000 and paid 640,000): the success popup says
	// what the renewal actually took, the MP before the request less the reply's cur_point.
	private static int s_mpBefore = -1;

	public static int LastPaid = -1;

	public static void MarkPaid(int curPoint)
	{
		LastPaid = s_mpBefore >= 0 && curPoint >= 0 && curPoint < s_mpBefore ? s_mpBefore - curPoint : -1;
		s_mpBefore = -1;
	}

	// The success message with "계약금 N MP" on a new line (the plain message when the amount is unknown).
	public static string WithPaid(string message)
	{
		return LastPaid > 0 ? message + "\n계약금 " + LastPaid.ToString("#,0") + " MP" : message;
	}

	// User, 2026-11-10 20:0x ("계약연장 안내를 확인 창에 적읍시다. 남은 계약 기간이 2일 초과 시 재계약 하면 추가 계약금이
	// 붙는다는 것을 공지해줘야함"): the surcharge rule of TableReader.ContractCost for the days left now.
	public static string SurchargeNote(int remainDays)
	{
		int after = TableReader.ContractCost.Ma9MSurchargeAfter;
		if (remainDays > after)
		{
			return "[FF8A3D]남은 계약 " + remainDays + "일: 계약금 +" + (remainDays - after) * 20 + "%[-]  (" + after + "일을 넘는 하루마다 +20%)";
		}
		return "남은 계약이 " + after + "일을 넘을 때 재계약하면 하루마다 계약금 +20%";
	}

	// "보너스 2점 남음" / "보너스 배분 완료" after the contract count while points are being placed.
	public static string Hint()
	{
		return Full ? "  [FFD200](보너스 배분 완료)[-]" : ("  [FFD200](보너스 " + Left + "점 남음)[-]");
	}
}
