using System;
using System.Collections.Generic;

namespace Ma9MServer
{
	// Dev (systems session, 2026-11-12): the lobby's 순위 변동 (LeagueHandlers.PreviousDayRank) as it would read at 0시 of
	// each day of every running league, for a check against the stored games (scratch lobbyrank_check.py). Read only;
	// run it on a copy of the data anyway (--lobbyranktest).
	public static class LobbyRankSelfTest
	{
		public static int Run(string dbPath)
		{
			LeagueStore store = new LeagueStore(dbPath);
			int leagues = 0;
			foreach ((uint code, string name, byte _, byte _) in store.ActiveTeams())
			{
				LeagueState s = store.Load(code);
				if (s == null) continue;
				leagues++;
				int now = store.ConfirmedView(s).RankOf(s.MyIndex);
				List<string> days = new List<string>();
				for (DateTime day = s.Start.Date.AddDays(1); day <= DateTime.Today; day = day.AddDays(1))
				{
					days.Add($"{day:MM-dd} 0시 → {LeagueHandlers.PreviousDayRank(store, s, now, day)}");
				}
				Console.WriteLine($"user {code} {name}: season {s.Season} start {s.Start:MM-dd HH:mm} next game {s.NextGame}, rank now {now}; end of the previous day at {string.Join(", ", days)}");
			}
			Console.WriteLine($"{leagues} leagues");
			return leagues > 0 ? 0 : 1;
		}
	}
}
