using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;

namespace Ma9MServer
{
	// The client's top bar (Common_TopMenu): team, league rank and record, time to the next league
	// game and the MP balance all come from PKS_SC_GLOBAL_INFO. Sent on lobby entry and whenever
	// the balance changes outside a screen that reports it itself (coupons).
	public static class TopBar
	{
		// eLeagueGrade.NONE: the bar shows "편성대기중" with no record.
		private const byte NoLeague = 100;

		// "N일차" = a block of 24 games, as the schedule screen's six day tabs (client
		// LeagueSchedule.GamesPerTab; the original season was six days of 24 games).
		private const int GamesPerDay = 24;

		public static void Send(GameSession session)
		{
			TeamRow team = session.Team;
			if (team == null)
			{
				return;
			}
			Ma9MPacket.PKS_SC_GLOBAL_INFO info = new Ma9MPacket.PKS_SC_GLOBAL_INFO();
			info.team_name.str = team.TeamName ?? string.Empty;
			info.team_emblem.n = team.Emblem;
			info.league_grade.n = NoLeague;
			LeagueState s = LeagueService.Store?.Load(team.UserCode);
			if (s != null && s.MyIndex >= 0 && s.MyIndex < s.Clubs.Count)
			{
				// Rank and record before the unconfirmed games: their results show on the unconfirmed-games
				// screen first (user, 2026-10-19: the top bar gave them away).
				LeagueState view = LeagueService.Store.ConfirmedView(s);
				LeagueClub me = view.Clubs[s.MyIndex];
				info.league_grade.n = LeagueRules.TierGrade[Math.Clamp(s.Tier, 0, LeagueRules.Tiers - 1)];
				info.league_rank.n = (byte)view.RankOf(s.MyIndex);
				info.league_win.n = (byte)Math.Min(255, me.W);
				info.league_lose.n = (byte)Math.Min(255, me.L);
				info.league_draw.n = (byte)Math.Min(255, me.D);
				DateTime now = DateTime.Now;
				int shown = Math.Min(s.NextGame, LeagueRules.Games);
				(int game, int opponent)? next = LeagueService.NextGameOf(s);
				info.in_broadcast.n = 0;
				if (LeagueHandlers.BroadcastWindow(s, now) is (int live, double left))
				{
					// The game started less than 5 minutes ago: 경기중 and the 중계 button, also after another menu
					// (user, 2026-10-21). The time is what is left of the 5 minutes; then the client asks again.
					info.in_broadcast.n = 1;
					info.remain_time_to_next_game.n = (int)Math.Ceiling(left);
					shown = live;
				}
				else
				{
					// Time to the user's next game: the league game, the postseason game the club plays next, or
					// the next season's first one (until 2026-10-21 this was 0, so the bar said 경기중 for good).
					DateTime at = next is (int nextGame, _) ? s.SlotTime(nextGame) : s.Start + LeagueCalendar.Cycle;
					info.remain_time_to_next_game.n = (int)Math.Max(0, Math.Ceiling((at - now).TotalSeconds));
					if (next is (int g, _)) shown = g;
				}
				// "포스트시즌 1일차 N경기" with N the postseason slot (145 on); otherwise 24 games a day.
				bool post = Postseason.IsPost(shown) || (s.NextGame > LeagueRules.Games && LeagueRules.HasPostseason(s.Tier) && s.NextTier < 0);
				int postSlot = Postseason.IsPost(shown) ? Postseason.SlotOf(shown) : Math.Clamp(s.PostSlot, 1, LeagueCalendar.LastPostSlot);
				info.cur_game_type.n = (byte)(post ? 1 : 0);
				info.cur_game_date.n = (byte)(post ? 1 : (shown - 1) / GamesPerDay + 1);
				info.cur_game_num.n = (short)(post ? postSlot : shown);
			}
			info.my_cash.n = 0;
			info.my_point.n = RosterHandlers.Db.AddMp(team.UserCode, 0);
			team.Mp = info.my_point.n;
			info.tutorial_num.n = 0;
			info.server_local_time.dateTime = DateTime.Now;
			session.Send(info);
			s_shown[session.Id] = GameKey(s);
		}

		// 2026-11-09 (AI tester round 5: a lobby left open for 50 minutes kept "84경기 경기중" and never showed games 85-86):
		// the client counts down to the next game and, at 0, shows 경기중 without asking again; the original server announced
		// each game. After every league tick (LeagueService.Tick, every 15 s) a connected session whose bar shows an older game
		// than the team's latest gets the bar again: 경기중 and the 중계 button for the game's 5 minutes (then the client asks
		// itself, as before), then the countdown to the next one. Sessions that never got the bar (not in the lobby yet) are left.
		private static readonly Dictionary<int, long> s_shown = new Dictionary<int, long>();

		private static long GameKey(LeagueState s) => s == null ? -1 : s.Season * 1000L + s.LastGame;

		// No push this long after a results-screen CONFIRM (the count-up and the stamp take a few seconds).
		private static readonly TimeSpan ConfirmQuiet = TimeSpan.FromSeconds(30);

		public static void PushLeague()
		{
			List<GameSession> online = LoginHandlers.Online();
			HashSet<int> ids = new HashSet<int>(online.Select(o => o.Id));
			foreach (int gone in s_shown.Keys.Where(id => !ids.Contains(id)).ToList())
			{
				s_shown.Remove(gone);
			}
			DateTime clock = DateTime.Now;
			foreach (GameSession session in online)
			{
				if (!s_shown.TryGetValue(session.Id, out long shown)) continue;
				// A results-screen scoreboard counting up (GameSession.ConfirmedLast): the next tick tries again.
				if (clock - session.ConfirmedAt < ConfirmQuiet) continue;
				LeagueState s = LeagueService.Store?.Load(session.Team.UserCode);
				long now = GameKey(s);
				if (now == shown) continue;
				ServerLog.Info($"[{session.Id}] top bar: game {s?.LastGame} played, bar sent again");
				Send(session);
			}
		}
	}
}
