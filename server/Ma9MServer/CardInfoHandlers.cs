using System;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// The player card window (PlayerInfo_Main): opened by clicking a card. Name, club, age, hands,
	// pitches and base stats come from the client's own card tables; the server adds the team's
	// copy of the card (condition, contract, bonus stats, lock), season records and tactics.
	public static class CardInfoHandlers
	{
		// PlayerInfo_DataCenter.InitTeamOperationValue: four personal tactics sliders, 50 = neutral.
		private const int OperationSliders = 4;

		private static Database s_db;

		private static CardCatalog s_catalog;

		public static void Register(GameLoop loop, Database db, CardCatalog catalog)
		{
			s_db = db;
			s_catalog = catalog;
			loop.Register<Ma9MPacket.PKS_CS_CARD_DETAIL_INFO>(Detail);
			loop.Register<Ma9MPacket.PKS_CS_CARD_DETAIL_INFO_OF_OTHERS>(DetailOfOthers);
			loop.Register<Ma9MPacket.PKS_CS_STATE_CHANGE_CARD_LOCK>(Lock);
		}

		// 선수보호 (player window checkbox): a locked card cannot be combined (Combine_Main) or released
		// (ContractManage_Main); the client checks IsLock ≥ 2, and the server must refuse it too.
		private static void Lock(GameSession session, Ma9MPacket.PKS_CS_STATE_CHANGE_CARD_LOCK packet)
		{
			bool? locked = s_db.ToggleCardLock(session.Team.UserCode, packet.card_index.n);
			Ma9MPacket.PKS_SC_STATE_CHANGE_CARD_LOCK reply = new Ma9MPacket.PKS_SC_STATE_CHANGE_CARD_LOCK();
			reply.card_index.n = packet.card_index.n;
			reply.IsLock.n = (byte)(locked == true ? 2 : 1);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] card {packet.card_index.n} {(locked == true ? "protected" : "unprotected")}");
		}

		private static void Detail(GameSession session, Ma9MPacket.PKS_CS_CARD_DETAIL_INFO packet)
		{
			// A phone resuming after a server restart sends its queued request before logging in again.
			if (session.Team == null)
			{
				return;
			}
			int idx = packet.index.n;
			uint user = session.Team.UserCode;
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			CardInfo info = s_catalog.Get(idx);
			Ma9MPacket.PKS_SC_CARD_DETAIL_INFO reply = new Ma9MPacket.PKS_SC_CARD_DETAIL_INFO();
			if (card != null)
			{
				// The packet serializes the objects bound in its constructor, so copy into reply.card_info
				// rather than replacing it.
				PUICardInfoStat stat = RosterHandlers.ToCardInfoStat(user, card, DateTime.Now);
				reply.card_info.ui_info = stat.ui_info;
				reply.card_info.educated_stat = stat.educated_stat;
				reply.card_info.contract_stat = stat.contract_stat;
				reply.card_info.training_stat = stat.training_stat;
				reply.card_info.bingogame_stat = stat.bingogame_stat;
				reply.card_info.Poten_stat = stat.Poten_stat;
				reply.card_info.team_color_stat = stat.team_color_stat;
			}
			else
			{
				// Not one of the team's cards (a shop or opponent card): index 0 makes the window
				// read-only, and it shows the card table's own numbers.
				reply.card_info.ui_info.index.n = 0;
				reply.card_info.ui_info.condition.n = 2;
				reply.card_info.ui_info.remain_contract.n = RosterHandlers.StartContract;
				reply.card_info.educated_stat.sbytes = new sbyte[6];
				reply.card_info.contract_stat.sbytes = new sbyte[6];
				reply.card_info.training_stat.sbytes = new sbyte[6];
				reply.card_info.bingogame_stat.sbytes = new sbyte[6];
				reply.card_info.Poten_stat.bytes = new byte[6];
				reply.card_info.team_color_stat.sbytes = new sbyte[6];
			}
			// The 특강 line: the coach of the card's lesson result (0 while it has none, e.g. after a 초기화권).
			reply.director_index.n = card != null && card.Educated.Any(v => v != 0) ? LessonHandlers.ResultCoach(user, idx) : 0;
			reply.item_index.n = 0;
			// 트레이닝 tab: the three slots (book, result square) and the card's TRAINING POINT.
			reply.training_point.n = card != null && info != null ? TrainingHandlers.Tp(user, idx, info.Ball) : TrainingHandlers.FullTp;
			if (card != null)
			{
				TrainingHandlers.FillSlots(user, idx, reply.training_cards, reply.training_effect);
			}
			else
			{
				reply.training_cards.array = Array.Empty<PInteger>();
				reply.training_effect.array = Array.Empty<PByte>();
			}
			reply.batter_record.index.n = idx;
			reply.pitcher_record.index.n = idx;
			// This season's league line (LeagueStore keeps one per card), with WAR and wRC+.
			PlayerSeason line = card != null ? LeagueService.Store?.PlayerSeason(user, idx) : null;
			if (line != null)
			{
				if (line.Pitcher)
				{
					SeasonStats.FillPitcher(reply.pitcher_record, line, idx);
				}
				else
				{
					SeasonStats.FillBatter(reply.batter_record, line, idx);
				}
				RecordHandlers.FillBestWorst(user, line, reply.batter_record, reply.pitcher_record);
				// 상세기록's 4사구 (사사구 in the original's title): walks and hit by pitches.
				reply.batter_record.BB.n = (byte)Math.Clamp(line.BB + line.HBP, 0, 255);
				(double war, int wrc) = SeasonStats.Value(line, info?.MainPosition ?? CardPos.DH, SeasonStats.Context(user));
				reply.war.f = double.IsNaN(war) ? 0f : (float)Math.Round(war, 2);
				reply.wrc_plus.n = (short)Math.Clamp(wrc, short.MinValue, short.MaxValue);
			}
			// 최근성적: the card's last five league games (RecordHandlers, systems session).
			if (card != null)
			{
				RecordHandlers.FillRecent(user, idx, reply.recent_batter_record, reply.recent_pitcher_record);
			}
			else
			{
				reply.recent_batter_record.array = Array.Empty<RECENT_BATTER_RECORD>();
				reply.recent_pitcher_record.array = Array.Empty<RECENT_PITCHER_RECORD>();
			}
			if (card != null)
			{
				// 개인작전 tab: the team's 작전실 values for the card's side and its own (OperationHandlers).
				OperationHandlers.FillCardDetail(user, idx, reply.PersonalOP, reply.OpOption, reply.TeamOP);
			}
			else
			{
				reply.PersonalOP.array = Enumerable.Range(0, OperationSliders).Select(_ => new PByte { n = 50 }).ToArray();
				reply.OpOption.n = 0;
				reply.TeamOP.array = Enumerable.Range(0, OperationSliders).Select(_ => new PByte { n = 50 }).ToArray();
			}
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] card detail {idx} {info?.Year} {info?.Name}{(card == null ? " (not owned)" : string.Empty)}");
		}

		private static void DetailOfOthers(GameSession session, Ma9MPacket.PKS_CS_CARD_DETAIL_INFO_OF_OTHERS packet)
		{
			int idx = packet.index.n;
			Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_OF_OTHERS reply = new Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_OF_OTHERS();
			reply._card_info.index.n = idx;
			reply._card_info.condition.n = 2;
			reply._card_info.remain_contract.n = RosterHandlers.StartContract;
			reply.card_info.ui_info.index.n = idx;
			reply.card_info.ui_info.condition.n = 2;
			reply.card_info.ui_info.remain_contract.n = RosterHandlers.StartContract;
			reply.card_info.educated_stat.sbytes = new sbyte[6];
			reply.card_info.contract_stat.sbytes = new sbyte[6];
			reply.card_info.training_stat.sbytes = new sbyte[6];
			reply.card_info.bingogame_stat.sbytes = new sbyte[6];
			reply.card_info.Poten_stat.bytes = new byte[6];
			reply.card_info.team_color_stat.sbytes = new sbyte[6];
			reply.director_index.n = 0;
			reply.batter_record.index.n = idx;
			reply.pitcher_record.index.n = idx;
			reply.recent_batter_record.array = Array.Empty<RECENT_BATTER_RECORD>();
			reply.recent_pitcher_record.array = Array.Empty<RECENT_PITCHER_RECORD>();
			// This season's line from the user's league (user, 2026-10-20: cards opened from the scoreboard
			// showed no records). user_num is the club index on scoreboards (any other value: every club
			// is searched). 최근성적 for every club's card (user, 2026-11-01: "상대팀이더라도 최근 5경기는 보여줘야할거
			// 아니냐"): the user's own club from its per-game lines, another club's from LeagueStore.ClubRecentGames.
			uint user = session.Team?.UserCode ?? 0;
			LeagueState s = user == 0 ? null : LeagueService.Store?.Load(user);
			if (s != null)
			{
				int club = packet.user_num.n < s.Clubs.Count ? packet.user_num.n : -1;
				PlayerSeason line = LeagueService.Store.PlayerLines(user, s.Season, club >= 0 ? club : (int?)null).FirstOrDefault(p => p.CardIdx == idx);
				if (line != null)
				{
					if (line.Pitcher) SeasonStats.FillPitcher(reply.pitcher_record, line, idx);
					else SeasonStats.FillBatter(reply.batter_record, line, idx);
					RecordHandlers.FillBestWorst(user, line, reply.batter_record, reply.pitcher_record);
					reply.batter_record.BB.n = (byte)Math.Clamp(line.BB + line.HBP, 0, 255);
					RecordHandlers.FillRecent(user, idx, reply.recent_batter_record, reply.recent_pitcher_record, line.Club);
					// WAR / wRC+ as for the team's own cards (2026-11-12: another club's card showed none; app 1.0.47 reads them).
					(double war, int wrc) = SeasonStats.Value(line, s_catalog.Get(idx)?.MainPosition ?? CardPos.DH, SeasonStats.Context(user));
					reply.war.f = double.IsNaN(war) ? 0f : (float)Math.Round(war, 2);
					reply.wrc_plus.n = (short)Math.Clamp(wrc, short.MinValue, short.MaxValue);
				}
			}
			session.Send(reply);
		}
	}
}
