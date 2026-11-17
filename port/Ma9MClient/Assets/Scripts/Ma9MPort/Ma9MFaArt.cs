using System.Collections.Generic;
using Ma9MPackets;

// FA 카드를 영입한 구단 유니폼으로 (systems session, 2026-11-17; user 10:2x after the engine session's previews: "오 유니폼 바뀐거
// 마음에 드는데, 아주 훌륭함. 내가 정말 원하는 작업임"). The server (FaArt) sends at each lobby visit POPUP type 34 [club, idx, idx, …]: our
// club's team code and our FA cards whose picture in our uniform is on the CDN (KBO/CardImage/<v>/fa/<idx>_<club>.png and
// KBO/CardDetailImage/<v>/fa/<idx>_<club>_detail.png, drawn by the engine session's fa_club_art.py). PlayerCardDrawer draws those cards
// from there (own cache names, no retries) when they are ours (owner ONESELF); everything else keeps the FA art. A picture the CDN
// does not have after all is not asked again this run. The app says it knows the list with %ma9m-faart1 (ClientBuildSetting).
public static class Ma9MFaArt
{
	public const byte ListType = 34;

	private static int s_club = -1;

	private static readonly HashSet<int> s_ready = new HashSet<int>();

	private static readonly HashSet<int> s_missing = new HashSet<int>();

	// NetworkConnector.ProcessPKS_SC_POPUP.
	public static bool Receive(Ma9MPacket.PKS_SC_POPUP packet)
	{
		if (packet.Type.n != ListType)
		{
			return false;
		}
		s_ready.Clear();
		s_club = -1;
		var a = packet.Messages.array;
		if (a != null && a.Length > 0 && a[0] != null && int.TryParse(a[0].str, out int club))
		{
			s_club = club;
			for (int i = 1; i < a.Length; i++)
			{
				if (a[i] != null && int.TryParse(a[i].str, out int idx))
				{
					s_ready.Add(idx);
				}
			}
		}
		return true;
	}

	// "<idx>_<club>" for one of our FA cards drawn in our uniform, else null.
	public static string Name(int idx)
	{
		return (s_club >= 0 && s_ready.Contains(idx) && !s_missing.Contains(idx)) ? (idx + "_" + s_club) : null;
	}

	public static void Missing(int idx)
	{
		s_missing.Add(idx);
	}
}
