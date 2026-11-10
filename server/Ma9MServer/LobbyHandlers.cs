using System;
using System.Linq;
using System.Collections.Generic;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// Main lobby entry and the in-game tutorial queries it makes.
	public static class LobbyHandlers
	{
		// eDetailMenuSecondRank values. The original-version menus are on; later additions
		// (coach, FA/operation room, position change) and online/community menus are off for now.
		private static readonly byte[] EnabledMenus =
		{
			0,  // TEAMMANAGEMENT_ROSTER
			1,  // TEAMMANAGEMENT_CONTRACT
			2,  // TEAMMANAGEMENT_TEAMCOLOR
			3,  // TEAMMANAGEMENT_PROPERTIES (팀특성)
			4,  // TEAMMANAGEMENT_VALUABLEPLAYER (수훈선수)
			5,  // TEAMMANAGEMENT_OPERATION (작전실, OperationHandlers)
			6,  // RECORDROOM_TEAMINFO
			7,  // RECORDROOM_LEAGUERANKINGCHART
			8,  // RECORDROOM_SCHEDULEANDRESULT
			9,  // RECORDROOM_PLAYERSTATINFO
			10, // RECORDROOM_PLAYERRECORD
			11, // RECORDROOM_POSTSEASON
			12, // POWERUP_SPECIALLECTURE (육성 특강)
			13, // POWERUP_COMBINEPLAYER
			14, // POWERUP_COMBINETRAININGCARD
			15, // POWERUP_POSITIONCHANGE (포지션 변경, PositionChangeHandlers; user 2026-10-24)
			16, // EXHIBITION_EXHIBITIONGAME
			24  // POWERUP_COACH_MIX slot = 트레이닝북 교환소 in Ma9M builds (TrainingHandlers.ExchangeMenu; user 2026-10-25)
		};

		// Client MainLobby_LeftWindow shows the next-match box when this is 0.
		private const byte NoNextMatch = 1;

		// 빨간 점: show_menu codes NoticeBase + eDetailMenuSecondRank (TEAMMANAGEMENT_TEAMCOLOR 2, _PROPERTIES 3); the lobby's
		// bottom buttons by eDetailMenuFirstRank (TEAM_MANAGEMENT 0).
		public const byte NoticeBase = 200, TeamColourMenu = 2, TraitsMenu = 3, TeamManagementButton = 0, MissionButton = 3;

		public static void Register(GameLoop loop)
		{
			loop.Register<Ma9MPacket.PKS_CS_ENTER_MAIN>(EnterMain);
			// The client asks for the top bar now and then (EventManager, tutorial alarms).
			loop.Register<Ma9MPacket.PKS_CS_GLOBAL_INFO>((session, packet) => TopBar.Send(session));
			loop.Register<Ma9MPacket.PKS_CS_SEND_DEVICE_ID>((session, packet) => { });
			// The OK of a server popup (PKS_SC_POPUP, e.g. the 시즌 완주 보너스 notice): nothing to do.
			loop.Register<Ma9MPacket.PKS_CS_POPUP_ACK>((session, packet) => { });
			loop.Register<Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL>(SceneTutorial);
			loop.Register<Ma9MPacket.PKS_CS_REQ_CURRENT_TUTORIAL>(CurrentTutorial);
		}

		private static void EnterMain(GameSession session, Ma9MPacket.PKS_CS_ENTER_MAIN packet)
		{
			Ma9MPacket.PKS_SC_ENTER_MAIN reply = new Ma9MPacket.PKS_SC_ENTER_MAIN();
			// 오늘의 미션 출석 (Missions).
			if (session.Team != null) Missions.OnLogin(session.Team.UserCode);
			reply.exist_next_match.n = NoNextMatch;
			reply.next_match_team_name.str = string.Empty;
			// 포지션 변경 only for builds that show its stat changes (an older app would look for the original factor table
			// on a changed card and fail).
			// 트레이닝북 교환소 likewise (an older app opens the 코치 조합 scene from that slot).
			byte[] menus = EnabledMenus
				.Where(m => m != PositionChangeHandlers.Menu || PositionChangeHandlers.ClientSupports(session))
				.Where(m => m != TrainingHandlers.ExchangeMenu || TrainingHandlers.ClientSupportsExchange(session))
				// 코칭스태프 (CoachHandlers): test servers with MA9M_COACH=1, builds with its screens only.
				.Concat(CoachHandlers.MenuOn(session) ? new[] { CoachHandlers.Menu } : Array.Empty<byte>()).ToArray();
			reply.enabled_menu.bytes = menus;
			reply.show_menu.bytes = menus;
			reply.menu_show_new_icon.bytes = new byte[9];
			// 빨간 점 (user, 2026-11-09 D-2): CP left in 특성관리 (TeamPropertyHandlers.CanSpend) or a 팀컬러 to look at (TeamColors.Notice)
			// light the original NEW mark on the lobby's 팀관리 button; show_menu also carries NoticeBase + the menu
			// (eDetailMenuSecondRank) for apps that put a dot on the button inside (the original app only looks for 0-30 there).
			if (session.Team != null)
			{
				List<byte> notices = new List<byte>();
				if (TeamPropertyHandlers.CanSpend(session.Team.UserCode)) notices.Add(NoticeBase + TraitsMenu);
				if (TeamColors.Notice(session.Team.UserCode)) notices.Add(NoticeBase + TeamColourMenu);
				if (notices.Count > 0)
				{
					reply.menu_show_new_icon.bytes[TeamManagementButton] = 1;
					reply.show_menu.bytes = menus.Concat(notices).ToArray();
				}
				// 오늘의 미션 with a reward to take (user, 2026-11-10 20:0x): the NEW mark on the lobby's 미션 button
				// (eDetailMenuFirstRank.MISSION), as 특성관리's on 팀관리.
				if (Missions.HasClaimable(session.Team.UserCode)) reply.menu_show_new_icon.bytes[MissionButton] = 1;
			}
			reply.datetime.dateTime = DateTime.Now;
			reply.uniformDataList.array = Array.Empty<PUniformData>();
			LeagueHandlers.FillMain(session, reply);
			session.Send(reply);
			// The top bar's league record, next-game timer and MP.
			TopBar.Send(session);
			// League games played since the last look: the client goes to the unconfirmed-games screen
			// (as the original did when a game ended), where the results, record and MP are revealed.
			if (session.Team != null && LeagueHandlers.HasUnconfirmed(session.Team.UserCode))
			{
				session.Send(new Ma9MPacket.PKS_SC_EXIST_UNCONFIRMED_GAME_RESULT());
			}
			else
			{
				// 시즌 완주 보너스 due (SeasonBonus): MP and a popup.
				SeasonBonus.Deliver(session);
				// 리그 시상식 not yet shown (LeagueAwards; the client opens it once the lobby is free of other popups).
				LeagueAwards.Deliver(session);
				// 특별수강 results since the last visit (the lobby's LessonResultPopup; not while the client is
				// about to leave for the unconfirmed games).
				LessonHandlers.OnLobby(session);
				// 새벽 자동 시범경기 결과 (ExhibitionAuto.cs).
				ExhibitionHandlers.DeliverAuto(session);
				// 코칭스태프: the day's SP and the 치어리더's gift (CoachHandlers; off unless MA9M_COACH=1).
				CoachHandlers.OnLobby(session);
			}
		}

		private static void SceneTutorial(GameSession session, Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL packet)
		{
			// No per-scene tutorial popups yet.
			Ma9MPacket.PKS_SC_REQ_SCENE_TUTORIAL reply = new Ma9MPacket.PKS_SC_REQ_SCENE_TUTORIAL();
			reply.tutorial_index_array.array = Array.Empty<PInteger>();
			session.Send(reply);
		}

		private static void CurrentTutorial(GameSession session, Ma9MPacket.PKS_CS_REQ_CURRENT_TUTORIAL packet)
		{
			// Index 0 has no tutorial row, so the client shows no tutorial reminder.
			Ma9MPacket.PKS_SC_REQ_CURRENT_TUTORIAL reply = new Ma9MPacket.PKS_SC_REQ_CURRENT_TUTORIAL();
			reply.error.SetNoError();
			reply.tutorial_index.n = 0;
			session.Send(reply);
		}
	}
}
