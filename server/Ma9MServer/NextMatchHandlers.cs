using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// The next league opponent's 1군 (systems session, 2026-10-19; user: "다음 경기 준비에서 상대 팀 이름 옆
	// i를 누르면 상대 팀이 보였는데, 육성 상태는 안 보임"). Opened from the lobby's next-match box and the
	// roster's 상대 button (OpponentLineUp → PKS_CS_OPPONENT_TEAM_ROSTER). Cards go as PUICardInfo (card,
	// condition, contract, 포지션 변경 since 2026-11-08): no development, as in the original; a card's window is the read-only one.
	public static class NextMatchHandlers
	{
		public static void Register(GameLoop loop)
		{
			loop.Register<Ma9MPacket.PKS_CS_OPPONENT_TEAM_ROSTER>(OpponentRoster);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TEAM_RECORD_OPPONENT_TEAM_ROSTER>(ClubRoster);
		}

		// The user's next league game: its number and the opponent club, or null when the season is over.
		public static (LeagueState s, int game, LeagueClub opponent)? Next(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			// The league game, or the postseason game the user's club plays next (engine session, 2026-10-21).
			if (s == null || LeagueService.NextGameOf(s) is not (int game, int opponent))
			{
				return null;
			}
			return (s, game, s.Clubs[opponent]);
		}

		private static void OpponentRoster(GameSession session, Ma9MPacket.PKS_CS_OPPONENT_TEAM_ROSTER packet)
		{
			if (session.Team == null) return;
			Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER reply = Build(session.Team.UserCode);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 상대 라인업: {reply.team_name.str}");
		}

		// Any club of the user's league by name (user, 2026-10-20: "기록실 순위표 i버튼 눌렀을 때 상대 팀 라인업이
		// 보이게"): the original's team-info roster request (TeamInfo_OwenerInfo.OnBtnRoster), answered with the
		// same roster packet; its starter is the one for the user's next game day (the last after the season).
		private static void ClubRoster(GameSession session, Ma9MPacket.PKS_CS_ENTER_TEAM_RECORD_OPPONENT_TEAM_ROSTER packet)
		{
			if (session.Team == null) return;
			string name = packet.team_name.str ?? string.Empty;
			Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER reply = ForClub(session.Team.UserCode, name);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 순위표 팀 라인업: {name}{(reply.error.error_code.n != 0 ? " (없음)" : string.Empty)}");
		}

		public static Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER ForClub(uint user, string name)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			LeagueClub club = s?.Clubs.FirstOrDefault(c => c.Name == name);
			int game = LeagueService.NextGameOf(s)?.game ?? Math.Clamp(s?.NextGame ?? 1, 1, LeagueRules.Games);
			return club == null ? Empty("팀 정보를 찾을 수 없습니다.") : Roster(s, game, club);
		}

		// Dev (--opptest, 2026-11-08; read-only, but run it on a data copy): every real team looks at every other club of
		// its league through the 순위표 roster request; each card must carry its owner's 포지션 변경 as team_card has it.
		public static int SelfTest(string dbPath, CardCatalog catalog)
		{
			List<(uint user, string team)> teams = new List<(uint, string)>();
			using (SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
			{
				c.Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code, team_name FROM team WHERE team_name <> '' ORDER BY user_code";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) teams.Add(((uint)r.GetInt64(0), r.GetString(1)));
			}
			int views = 0, cards = 0, changed = 0, wrong = 0;
			List<string> examples = new List<string>();
			foreach ((uint user, string team) in teams)
			{
				LeagueState s = LeagueService.Store?.Load(user);
				if (s == null) continue;
				foreach (LeagueClub club in s.Clubs.Where(c => c.Index != s.MyIndex))
				{
					Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER r = ForClub(user, club.Name);
					if (r.error.error_code.n != 0) continue;
					views++;
					Dictionary<int, TeamCard> owned = LeagueHandlers.OwnedBy(club.Owner);
					foreach (PUICardInfo card in r.batters.array.Concat(r.pitchers.array).Where(x => x.index.n != 0))
					{
						cards++;
						byte want = owned.TryGetValue(card.index.n, out TeamCard tc) ? tc.PositionTrainingInfo : (byte)0;
						if (card.position_training_info.n != want) wrong++;
						if (want == 0) continue;
						changed++;
						if (examples.Count < 12) examples.Add($"{team} → {club.Name} {catalog.Get(card.index.n)?.Name ?? card.index.n.ToString()} {want}");
					}
				}
			}
			foreach (string e in examples) ServerLog.Info($"[opptest] {e}");
			bool ok = views > 0 && changed > 0 && wrong == 0;
			ServerLog.Info($"[opptest] {(ok ? "ok" : "FAIL")}: {teams.Count} teams, {views} rosters, {cards} cards, {changed} with 포지션 변경 shown, {wrong} wrong");
			return ok ? 0 : 1;
		}

		private static Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER Empty(string message)
		{
			Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER reply = new Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER();
			reply.error.error_code.n = 1;
			reply.error.error_msg.str = message;
			reply.team_name.str = string.Empty;
			reply.batters.array = Array.Empty<PUICardInfo>();
			reply.fielders.array = Array.Empty<PByte>();
			reply.pitchers.array = Array.Empty<PUICardInfo>();
			return reply;
		}

		public static Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER Build(uint user)
		{
			var next = Next(user);
			if (next == null)
			{
				return Empty("다음 경기가 없습니다.");
			}
			(LeagueState s, int game, LeagueClub op) = next.Value;
			return Roster(s, game, op);
		}

		private static Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER Roster(LeagueState s, int game, LeagueClub op)
		{
			Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER reply = new Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER();
			Lineup l = LeagueService.LineupOf(s, op, out _);
			DateTime day = s.SlotTime(game);  // the slot's time: a 컨디션 회복제 counts by the hour (Condition.For)
			// The owner's card as it plays: 포지션 변경 (a 중계 changed to 셋업 shows as 셋업), contract and lock (2026-11-08).
			var owned = LeagueHandlers.OwnedBy(op.Owner);
			PUICardInfo Card(int idx) => LeagueHandlers.CardOf(op.Owner, idx, day, owned);
			reply.error.SetNoError();
			reply.team_name.str = op.Name;
			// The same shapes as ENTER_ROSTER_INFO: batting order (bench after the nine), fielders by
			// position as batting slot + 1, the staff by slot, the next starter's slot.
			reply.batters.array = l.Batters.TakeWhile(i => i != 0).Select(Card).ToArray();
			reply.fielders.array = l.Fielders.Select(f => new PByte { n = f }).ToArray();
			reply.pitchers.array = l.Pitchers.Select(Card).ToArray();
			reply.next_starter.n = (byte)LeagueService.StarterSlot(l, LeagueService.RotationOf(s, op.Index, game));
			return reply;
		}
	}
}
