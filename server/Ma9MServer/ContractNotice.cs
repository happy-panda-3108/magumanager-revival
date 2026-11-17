using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 1군 계약 만료 알림 (systems session, 2026-11-16). User 18:3x 2번: "1군 계약 만료 알림을 로비에 띄우는게 좋을 것 같음. 팀 로스터
	// 들어가서 팝업이 뜨는 것보다 더 효과적일 것 같네" (AI tester kt, 11-16 night: 22 of the 25 had one day left and nothing said so).
	// At a lobby visit: the 1군 cards whose contract ends at midnight (1 day left) and those already ended (0 days: condition fixed at
	// 최하, RosterHandlers), once a day per team; the same day again only when more cards come to it.
	public static class ContractNotice
	{
		private const string Title = "계약 만료 알림";

		private static readonly Dictionary<uint, (DateTime day, int count)> s_shown = new Dictionary<uint, (DateTime, int)>();

		public static void OnLobby(GameSession session)
		{
			if (session.Team == null) return;
			uint user = session.Team.UserCode;
			string text = Text(user, out int count);
			if (text == null) return;
			DateTime today = DateTime.Today;
			if (s_shown.TryGetValue(user, out var last) && last.day == today && last.count >= count) return;
			s_shown[user] = (today, count);
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[] { new PLongString { str = Title }, new PLongString { str = text } };
			session.Send(popup);
			ServerLog.Info($"[{session.Id}] contract notice: {text.Replace("\n", " / ")}");
		}

		// The popup's text (five lines at most, each within PopupText.Width), null when no 1군 card is ending; count = cards named.
		internal static string Text(uint user, out int count)
		{
			count = 0;
			Lineup l = RosterHandlers.Db.GetLineup(user);
			if (l == null) return null;
			// The batting order and the 11 pitchers, not the bench (Batters 9-13; user 11-17 21:1x: "후보선수 5명에 대해서는 계약 만료
			// 알림이 나오지 않아야 한다고 이전에 얘기했었음. 팀 로스터에서 … 팝업에 후보는 포함 안 되는거랑 같은 이치임" — the app's
			// Roster_DataCenter.isExistExpired has left the bench out since 10-25).
			HashSet<int> first = l.Batters.Take(9).Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
			List<TeamCard> cards = RosterHandlers.Db.GetCards(user).Where(c => first.Contains(c.Idx)).ToList();
			List<TeamCard> ending = cards.Where(c => c.RemainContract == 1).ToList();
			List<TeamCard> ended = cards.Where(c => c.RemainContract == 0).ToList();
			count = ending.Count + ended.Count;
			if (count == 0) return null;
			List<string> lines = new List<string>();
			if (ending.Count > 0)
			{
				lines.Add($"오늘 자정에 계약이 끝나는 1군 선수 {ending.Count}명");
				lines.Add(Names(ending));
			}
			if (ended.Count > 0)
			{
				lines.Add($"계약이 끝나 컨디션이 최하로 고정된 1군 선수 {ended.Count}명");
				if (ending.Count == 0) lines.Add(Names(ended));
			}
			lines.Add("팀 관리 > 계약관리에서 연장할 수 있습니다.");
			if (ending.Count > 0 && lines.Count < 5) lines.Add("자정이 지나면 컨디션이 최하로 고정됩니다.");
			return string.Join("\n", lines.Take(5));
		}

		// "'21 양석환 · '09 김상현 외 n명" on one line.
		private static string Names(List<TeamCard> cards)
		{
			List<string> names = cards.Select(c => RosterHandlers.Catalog.Get(c.Idx)).Where(c => c != null)
				.Select(c => c.IsLegend ? c.Name : $"'{c.Year % 100:00} {c.Name}").ToList();
			for (int shown = names.Count; shown > 0; shown--)
			{
				string line = string.Join(" · ", names.Take(shown)) + (shown < names.Count ? $" 외 {names.Count - shown}명" : string.Empty);
				if (PopupText.Px(line) <= PopupText.Width) return line;
			}
			return $"{names.Count}명";
		}
	}
}
