using System;

namespace Ma9MServer
{
	// One clock for every league (user, 2026-10-21: "고정된 시간에 같은 일차, 경기수가 진행된다는게 포인트").
	// A season is a 2.5-day cycle from its start:
	//   0 h      game 1; 144 games every 20 minutes (48 h, the last at 47:40)
	//   48 h     postseason (루키 and up): 19 slots every 30 minutes (the last at 57:00, over by 57:30)
	//   58 h     팀 정비: the season closes (awards, promotion, relegation; the new tier's ball cap applies)
	//   59 h     the next league is formed
	//   60 h     the next season's game 1
	// Cycles alternate between starting at 00:00 and 12:00. A team that joins mid-season plays the
	// remaining games only (a team made at game 50 plays games 51-144).
	public static class LeagueCalendar
	{
		// The phase of the cycle: the first common season starts at 00:00 on 2026-10-22 (user, 2026-10-21:
		// "차라리 22일 0시부터 새로운 리그를 시작"), its postseason 00:00-09:30 on 10-24, maintenance 10:00,
		// formation 11:00, the next season at 12:00.
		public static readonly DateTime Epoch = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Local);

		// The first try (deployed 2026-10-21 23:04) used a cycle from 10-21 00:00 and joined every team at
		// game 71. Leagues still on it are discarded without rewards and start again at Epoch (LeagueService.Restart).
		public static readonly DateTime AbandonedStart = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Local);
		public static readonly TimeSpan Cycle = TimeSpan.FromHours(60);
		public static readonly TimeSpan PostStart = TimeSpan.FromHours(48);
		public const int PostSlotMinutes = 30;
		public const int PostSlots = 19;
		// After a tie the 한국시리즈 goes on past game 7 (user, 2026-10-26: "8경기, 9경기 등등"): up to five more games,
		// 57:30 .. 59:30, played only while it is undecided; the close and the formation wait for it.
		public const int ExtraKsSlots = 5;
		public const int LastPostSlot = PostSlots + ExtraKsSlots;
		public static readonly TimeSpan CloseAt = TimeSpan.FromHours(58);
		public static readonly TimeSpan FormAt = TimeSpan.FromHours(59);

		private static long CycleIndex(DateTime t) => (long)Math.Floor((t - Epoch).TotalMinutes / Cycle.TotalMinutes);

		// The start of the season whose cycle t falls in.
		public static DateTime CycleStart(DateTime t) => Epoch.AddMinutes(CycleIndex(t) * Cycle.TotalMinutes);

		// Whether a season start is on the common clock (leagues made before it ran on their own).
		public static bool OnCalendar(DateTime start) => CycleStart(start) == start;

		public static DateTime PostSlotTime(DateTime start, int slot) => start + PostStart + TimeSpan.FromMinutes((slot - 1) * PostSlotMinutes);

		// Where a team made at time t starts: the running season from its next game slot while the regular
		// season still has one, else the next season from game 1.
		public static (DateTime start, int firstGame) JoinAt(DateTime t)
		{
			DateTime start = CycleStart(t);
			int due = (int)Math.Floor((t - start).TotalMinutes / LeagueRules.SlotMinutes) + 1;  // slots at or before t
			int first = t == LeagueRules.SlotTime(start, due) ? due : due + 1;
			// A slot that is due now is played at once, so a team joining exactly on it plays it.
			return first <= LeagueRules.Games ? (start, first) : (start + Cycle, 1);
		}
	}
}
