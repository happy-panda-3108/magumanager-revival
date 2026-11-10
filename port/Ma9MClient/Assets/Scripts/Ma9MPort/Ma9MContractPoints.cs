using System.Collections.Generic;

// Ma9M port (user, 2026-10-20: "재계약할 때 한 스탯을 누르고 +3이 되는 것보다, 보너스 스탯 3을 주고 원하는대로
// 찍을 수 있게 … 컨택1, 파워2 이런식으로"): a renewal's three bonus points, one per + press, on the 계약관리
// screen (ContractManage_Main) and the player window's 계약 tab (PlayerInfo_ContractManagementWindow).
// They go in PKS_CS_EXTEND_CONTRACT.bonus_stat as 32 + a·36 + b·6 + c, +1 each to stats a, b and c
// (the server's ContractHandlers.BonusPoints; 0–5 still means +3 to that stat).
public static class Ma9MContractPoints
{
	public const int PerRenewal = 3;

	private static readonly List<int> s_picks = new List<int>(PerRenewal);

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

	// The request byte (255 until all three are placed).
	public static byte Code
	{
		get
		{
			return Full ? ((byte)(32 + s_picks[0] * 36 + s_picks[1] * 6 + s_picks[2])) : byte.MaxValue;
		}
	}

	// Points per stat in a request byte: 0–5 = +3 to that stat, 32–247 = the three single points.
	public static int[] Decode(byte code)
	{
		int[] points = new int[6];
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
