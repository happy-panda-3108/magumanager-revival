using System.Collections.Generic;
using Ma9MPackets;

// FA 카드를 영입한 구단 유니폼으로 (systems session, 2026-11-17; user 10:2x after the engine session's previews: "오 유니폼 바뀐거
// 마음에 드는데, 아주 훌륭함. 내가 정말 원하는 작업임"). The server (FaArt) sends at each lobby visit POPUP type 34 [club, idx, idx, …]: our
// club's team code and our FA cards whose picture in our uniform is on the CDN (KBO/CardImage/<v>/fa/<idx>_<club>.png and
// KBO/CardDetailImage/<v>/fa/<idx>_<club>_detail.png, drawn by the engine session's fa_club_art.py). PlayerCardDrawer draws those cards
// from there (own cache names, no retries) when they are ours (owner ONESELF), and since 11-18 the other real clubs' FA cards of
// our league in their own uniform ("<idx>_<club>" entries); everything else keeps the FA art. A picture the CDN
// does not have after all is not asked again this run. The app says it knows the list with %ma9m-faart1 (ClientBuildSetting).
public static class Ma9MFaArt
{
	public const byte ListType = 34;

	private static int s_club = -1;

	private static readonly HashSet<int> s_ready = new HashSet<int>();

	// 2026-11-18 (user 23:5x "01롯데 계정에서 볼 때는 97쌍방울의 김경원이 FA팀으로 보이네? … 타팀이 보는 내 FA도 구단 유니폼을 입은걸로
	// 보였으면 해"): the other real clubs' FA cards in our league, "<idx>_<club>" entries after ours (an older app's int parse skips them).
	private static readonly Dictionary<int, int> s_others = new Dictionary<int, int>();

	private static readonly HashSet<string> s_missing = new HashSet<string>();

	// NetworkConnector.ProcessPKS_SC_POPUP.
	public static bool Receive(Ma9MPacket.PKS_SC_POPUP packet)
	{
		if (packet.Type.n != ListType)
		{
			return false;
		}
		s_ready.Clear();
		s_others.Clear();
		s_club = -1;
		var a = packet.Messages.array;
		if (a != null && a.Length > 0 && a[0] != null && int.TryParse(a[0].str, out int club))
		{
			s_club = club;
			for (int i = 1; i < a.Length; i++)
			{
				string s = (a[i] != null) ? a[i].str : null;
				if (string.IsNullOrEmpty(s))
				{
					continue;
				}
				int cut = s.IndexOf('_');
				if (cut < 0)
				{
					if (int.TryParse(s, out int idx))
					{
						s_ready.Add(idx);
					}
				}
				else if (int.TryParse(s.Substring(0, cut), out int idx) && int.TryParse(s.Substring(cut + 1), out int other))
				{
					s_others[idx] = other;
				}
			}
		}
		return true;
	}

	// Our club's team code (eTeamName), −1 until the lobby's list came (2026-11-18: the card lists count our FA cards for it, Ma9MSortMemory).
	public static int Club => s_club;

	// "<idx>_<club>" for an FA card drawn in a club's uniform, else null: ours (mine: the card shown is our team's) in our uniform,
	// another club's in our league in theirs.
	public static string Name(int idx, bool mine = true)
	{
		string name = null;
		if (mine && s_club >= 0 && s_ready.Contains(idx))
		{
			name = idx + "_" + s_club;
		}
		else if (s_others.TryGetValue(idx, out int club))
		{
			name = idx + "_" + club;
		}
		return (name != null && !s_missing.Contains(name)) ? name : null;
	}

	public static void Missing(string name)
	{
		s_missing.Add(name);
	}
}
