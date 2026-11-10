using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;

namespace Ma9MServer
{
	// Login → team (구단) list → choose/create team → scene transition.
	public static class LoginHandlers
	{
		// Client Login_Main: 0 = OK, 1..3 = wrong id/password popup.
		private const byte LoginOk = 0;

		private const byte LoginWrongPassword = 1;

		// Client ServerSelect: 0 = existing team → lobby, 1 = new team → intro tutorial.
		private const byte ChooseExisting = 0;

		private const byte ChooseNew = 1;

		// Client NetworkConnector.ProcessPK_SC_TRANSIT_SCENE scene numbers.
		private const byte SceneLobby = 0;

		private const byte SceneIntroTutorial = 2;

		private const byte SceneUnconfirmed = 1;

		private const byte SceneExhibition = 3;

		private const byte MaxTeams = 2;

		// Access tokens survive a dropped connection: the client reconnects with
		// PKS_CS_REQUEST_ACCESS_TOKEN_LOGIN and expects to keep its account and team.
		private static readonly Dictionary<Guid, GameSession> s_tokens = new Dictionary<Guid, GameSession>();

		// The connected sessions with a team (a token's latest session; loop thread only), for pushes such as TopBar.PushLeague.
		internal static List<GameSession> Online() => s_tokens.Values.Where(s => s.Connected && s.Team != null).Distinct().ToList();

		private static Database s_db;

		public static void Register(GameLoop loop, Database db, string dbPath = null)
		{
			s_db = db;
			if (dbPath != null)
			{
				TokenStore.Init(dbPath);
			}
			loop.Register<Ma9MPacket.PKS_SYSTEM_KEEP_CONNECTION>((session, packet) => { });
			loop.Register<Ma9MPacket.PKS_CS_REQUEST_LOGIN>((session, packet) =>
			{
				session.ClientVersion = packet.bundle_version.str ?? string.Empty;
				Login(session, db, packet.id.str, packet.pw.str);
			});
			loop.Register<Ma9MPacket.PKS_CS_REQUEST_GUEST_LOGIN>((session, packet) =>
			{
				session.ClientVersion = packet.bundle_version.str ?? string.Empty;
				Login(session, db, "guest:" + packet.device_id.str, string.Empty);
			});
			loop.Register<Ma9MPacket.PKS_CS_ISSUE_COMM_KEY>(IssueCommKey);
			loop.Register<Ma9MPacket.PKS_CS_GET_TEAM_LIST>((session, packet) => GetTeamList(session, db, packet));
			loop.Register<Ma9MPacket.PKS_CS_CHOOSE_TEAM>((session, packet) => ChooseTeam(session, db, packet));
			loop.Register<Ma9MPacket.PKS_CS_TRANSIT_SCENE>(TransitScene);
			loop.Register<Ma9MPacket.PKS_CS_MAKE_TEAM>((session, packet) => MakeTeam(session, db, packet));
			loop.Register<Ma9MPacket.PKS_CS_REQUEST_SIGN_UP>((session, packet) => SignUp(session, db, packet));
			loop.Register<Ma9MPacket.PKS_CS_PASS_TUTORIAL>((session, packet) => { });
			loop.Register<Ma9MPacket.PKS_CS_REQUEST_ACCESS_TOKEN_LOGIN>(TokenLogin);
			loop.Register<Ma9MPacket.PKS_CS_LOGIN_SUCCEED>((session, packet) => session.Send(new Ma9MPacket.PKS_SC_LOGIN_SUCCEED()));
		}

		private static void TokenLogin(GameSession session, Ma9MPacket.PKS_CS_REQUEST_ACCESS_TOKEN_LOGIN packet)
		{
			session.ClientVersion = packet.bundle_version.str ?? string.Empty;
			Ma9MPacket.PKS_SC_REQUEST_LOGIN reply = new Ma9MPacket.PKS_SC_REQUEST_LOGIN();
			reply.error.SetNoError();
			if (s_tokens.TryGetValue(packet.access_token.guid, out GameSession previous))
			{
				session.Account = previous.Account;
				session.Team = previous.Team;
				session.AccessToken = previous.AccessToken;
				s_tokens[session.AccessToken] = session;
				reply.result.n = LoginOk;
				reply.accessToken.guid = session.AccessToken;
				reply.headUserCode.n = (int)session.Account.Id;
				ServerLog.Info($"[{session.Id}] token login ok (was [{previous.Id}])");
			}
			else if (TokenStore.Find(packet.access_token.guid, s_db) is (AccountRow account, TeamRow team))
			{
				// Issued before a server restart (TokenStore): the same account and team as before.
				session.Account = account;
				session.Team = team;
				session.AccessToken = packet.access_token.guid;
				s_tokens[session.AccessToken] = session;
				if (team != null && team.TutorialDone)
				{
					RosterHandlers.EnsureRoster(team);
				}
				reply.result.n = LoginOk;
				reply.accessToken.guid = session.AccessToken;
				reply.headUserCode.n = (int)account.Id;
				ServerLog.Info($"[{session.Id}] token login ok after a restart: account {account.Id}, team {team?.UserCode.ToString() ?? "none"}");
			}
			else
			{
				// 10 = session expired: the client returns to the login screen. Its NetworkConnector reads
				// error 0 as a successful login whatever the result, so the error must be set too (with
				// SetNoError the client carried on logged out and froze, 2026-10-19).
				reply.result.n = 10;
				reply.error.error_code.n = 10;
				ServerLog.Info($"[{session.Id}] unknown access token");
			}
			session.Send(reply);
		}

		private static void Login(GameSession session, Database db, string id, string password)
		{
			string email = (id ?? string.Empty).Trim().ToLowerInvariant();
			Ma9MPacket.PKS_SC_REQUEST_LOGIN reply = new Ma9MPacket.PKS_SC_REQUEST_LOGIN();
			reply.error.SetNoError();
			AccountRow account = email.Length == 0 ? null : db.LoginOrCreate(email, (password ?? string.Empty).Trim(), out bool created);
			if (account == null)
			{
				reply.result.n = LoginWrongPassword;
				ServerLog.Info($"[{session.Id}] login rejected for {email}");
			}
			else
			{
				session.Account = account;
				session.AccessToken = Guid.NewGuid();
				s_tokens[session.AccessToken] = session;
				TokenStore.Save(session.AccessToken, account);
				reply.result.n = LoginOk;
				reply.accessToken.guid = session.AccessToken;
				reply.headUserCode.n = (int)account.Id;
				ServerLog.Info($"[{session.Id}] login ok: {email} (account {account.Id})");
			}
			session.Send(reply);
		}

		// 회원가입 (Login_SignUp): 0 = created (the client greets and returns to the login screen),
		// anything else = the e-mail is taken. Logging in with a new e-mail also creates the account.
		private static void SignUp(GameSession session, Database db, Ma9MPacket.PKS_CS_REQUEST_SIGN_UP packet)
		{
			string email = (packet.id.str ?? string.Empty).Trim().ToLowerInvariant();
			string password = (packet.pw.str ?? string.Empty).Trim();
			Ma9MPacket.PKS_SC_REQUEST_SIGN_UP reply = new Ma9MPacket.PKS_SC_REQUEST_SIGN_UP();
			bool created = false;
			if (email.Length > 0 && password.Length > 0)
			{
				db.LoginOrCreate(email, password, out created);
			}
			reply.result.n = (byte)(created ? 0 : 1);
			ServerLog.Info($"[{session.Id}] sign-up {email}: {(created ? "created" : "taken")}");
			session.Send(reply);
		}

		private static void IssueCommKey(GameSession session, Ma9MPacket.PKS_CS_ISSUE_COMM_KEY packet)
		{
			// No chat/club server yet; a non-zero result makes the client skip connecting to one.
			Ma9MPacket.PKS_SC_ISSUE_COMM_KEY reply = new Ma9MPacket.PKS_SC_ISSUE_COMM_KEY();
			reply.result.n = 1;
			reply.channel.n = packet.channel.n;
			session.Send(reply);
		}

		private static void GetTeamList(GameSession session, Database db, Ma9MPacket.PKS_CS_GET_TEAM_LIST packet)
		{
			Ma9MPacket.PKS_SC_GET_TEAM_LIST reply = new Ma9MPacket.PKS_SC_GET_TEAM_LIST();
			List<PUserInfo> list = new List<PUserInfo>();
			foreach (TeamRow team in db.GetTeams(session.Account.Id, packet.server_type.n))
			{
				list.Add(ToUserInfo(team));
			}
			if (list.Count < MaxTeams)
			{
				// user_type -1 marks the empty slot the client sends back when creating a team.
				PUserInfo empty = new PUserInfo();
				empty.SetUserInfo(0, string.Empty, 0, string.Empty, -1, 0, 0, 0, 0);
				list.Add(empty);
			}
			reply.result.n = 0;
			reply.userInfoList.array = list.ToArray();
			reply.max_slot_count.n = MaxTeams;
			session.Send(reply);
		}

		private static void ChooseTeam(GameSession session, Database db, Ma9MPacket.PKS_CS_CHOOSE_TEAM packet)
		{
			byte serverType = packet.server_type.n;
			TeamRow team;
			if (packet.userInfo.user_type.n == -1)
			{
				// A team left unfinished (no roster before MAKE_TEAM) is reused instead of piling up blank teams.
				team = db.GetTeams(session.Account.Id, serverType).Find(t => !t.TutorialDone) ?? db.CreateTeam(session.Account.Id, serverType);
				ServerLog.Info($"[{session.Id}] new team {team.UserCode}");
			}
			else
			{
				team = db.GetTeams(session.Account.Id, serverType).Find(t => t.UserCode == packet.userInfo.user_code.n);
			}
			Ma9MPacket.PKS_SC_CHOOSE_TEAM reply = new Ma9MPacket.PKS_SC_CHOOSE_TEAM();
			reply.error.SetNoError();
			reply.access_token.guid = session.AccessToken;
			if (team == null)
			{
				reply.result.n = 2;
			}
			else
			{
				session.Team = team;
				TokenStore.SetTeam(session.AccessToken, team);
				if (team.TutorialDone)
				{
					RosterHandlers.EnsureRoster(team);
				}
				reply.result.n = team.TutorialDone ? ChooseExisting : ChooseNew;
			}
			session.Send(reply);
		}

		private static void TransitScene(GameSession session, Ma9MPacket.PKS_CS_TRANSIT_SCENE packet)
		{
			Ma9MPacket.PKS_SC_TRANSIT_SCENE reply = new Ma9MPacket.PKS_SC_TRANSIT_SCENE();
			reply.scene_num.n = (session.Team != null && session.Team.TutorialDone) ? SceneLobby : SceneIntroTutorial;
			// An exhibition left unfinished (app closed mid-game) goes on first: its broadcast, then its result
			// (client: TRANSIT_SCENE 3 = exhibition broadcast; user 2026-10-21, as in the original).
			if (reply.scene_num.n == SceneLobby && ExhibitionHandlers.Pending(session.Team.UserCode))
			{
				reply.scene_num.n = SceneExhibition;
			}
			// League games played while away are shown first (client Scene_Unconfirmed).
			else if (reply.scene_num.n == SceneLobby && LeagueHandlers.HasUnconfirmed(session.Team.UserCode))
			{
				reply.scene_num.n = SceneUnconfirmed;
			}
			session.Send(reply);
		}

		// Client IntroTutorial_Main: 0 = OK, 2 = duplicated team name.
		private static void MakeTeam(GameSession session, Database db, Ma9MPacket.PKS_CS_MAKE_TEAM packet)
		{
			Ma9MPacket.PKS_SC_MAKE_TEAM reply = new Ma9MPacket.PKS_SC_MAKE_TEAM();
			string name = (packet.team_name.str ?? string.Empty).Trim();
			if (session.Team == null)
			{
				reply.result.n = 3;
			}
			else if (name.Length == 0 || db.IsTeamNameTaken(name, session.Team.UserCode))
			{
				reply.result.n = 2;
			}
			else
			{
				// Key players (8볼 레어 picked in IntroTutorial step 3): up to a batter and a pitcher.
				List<int> keys = new[] { packet.selected_batter_1.n, packet.selected_batter_2.n, packet.selected_pitcher_1.n, packet.selected_pitcher_2.n }
					.Where(i => i != 0).Distinct().ToList();
				int keyPlayer = keys.Count > 0 ? keys[0] : 0;
				db.CompleteTeam(session.Team, name, packet.team_emblem.n, keyPlayer);
				RosterHandlers.GrantStarterTeam(session.Team, keys, packet.team_emblem.n);
				ServerLog.Info($"[{session.Id}] team {session.Team.UserCode} named '{name}', emblem {packet.team_emblem.n}, key players {string.Join(", ", keys)}");
				reply.result.n = 0;
			}
			session.Send(reply);
		}

		private static PUserInfo ToUserInfo(TeamRow team)
		{
			PUserInfo info = new PUserInfo();
			// A team whose creation was left unfinished has no name yet; label it on the 구단 선택 screen.
			string name = team.TeamName.Length > 0 ? team.TeamName : UnfinishedTeamName;
			info.SetUserInfo(team.UserCode, string.Empty, 0, name, 0, team.Emblem, team.LeagueGrade, team.Mp, team.Cash);
			return info;
		}

		private const string UnfinishedTeamName = "(만드는 중)";
	}
}
