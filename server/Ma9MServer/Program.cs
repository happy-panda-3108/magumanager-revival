using System;
using System.IO;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;

namespace Ma9MServer
{
	public static class Program
	{
		// MA9M_HTTP_PORT / MA9M_GAME_PORT (engine session, 2026-10-30): a second server on a copy of the
		// data (the fielding engine's 3D tests) next to the live one; unset = the usual ports.
		public static readonly int HttpPort = int.TryParse(Environment.GetEnvironmentVariable("MA9M_HTTP_PORT"), out int hp) ? hp : 18080;

		public static readonly int GamePort = int.TryParse(Environment.GetEnvironmentVariable("MA9M_GAME_PORT"), out int gp) ? gp : 18000;

		public static void Main(string[] args)
		{
			string[] paths = Array.FindAll(args, a => !a.StartsWith("--"));
			string dataDir = Path.GetFullPath(paths.Length > 0 ? paths[0] : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
			Directory.CreateDirectory(dataDir);
			ServerLog.Info($"data directory: {dataDir}");
			if (Array.IndexOf(args, "--devclient") >= 0)
			{
				// Dev: a headless client against the running server (test accounts only).
				Environment.Exit(DevClient.Run(dataDir, args));
			}

			Database db = new Database(Path.Combine(dataDir, "ma9m.db"));
			GameLoop loop = new GameLoop();
			LoginHandlers.Register(loop, db, Path.Combine(dataDir, "ma9m.db"));  // + TokenStore: tokens survive a restart
			LobbyHandlers.Register(loop);
			CardCatalog catalog = CardCatalog.Load(Path.Combine(dataDir, "tables"));
			RosterHandlers.Register(loop, db, catalog);
			// 선발 5경기 제한 (systems session, 2026-11-09): the roster save and screen read it.
			SpBlock.Init(Path.Combine(dataDir, "ma9m.db"));
			RosterHandlers.MigrateCards();
			// 팀 로스터 > 선수관리 (컨디션 회복제): before the league, whose games read it through Condition.For.
			PlayerManageHandlers.Register(loop, db, Path.Combine(dataDir, "ma9m.db"));
			// 전력강화 > 특별강좌 (특별수강): lessons end on a timer, so their results are in before the league's games read the cards.
			LessonHandlers.Register(loop, db, catalog, dataDir);
			// 전력강화 > 포지션 변경 (pitcher role change; the games read RoleOf / Delta).
			PositionChangeHandlers.Register(loop, db, catalog, dataDir);
			GameData gameData = GameData.Load(dataDir, catalog);
			TeamStrength.Init(Ma9MSim.CardGame.LoadCards(Path.Combine(dataDir, "sim")).Values);
			int leagueTest = Array.IndexOf(args, "--leaguetest");
			if (leagueTest >= 0)
			{
				// Dev: run the league clock ahead on a copy of the data directory.
				double hours = leagueTest + 1 < args.Length && double.TryParse(args[leagueTest + 1], out double h) ? h : 50;
				int refMode = Array.IndexOf(args, "--refmode");
				if (refMode >= 0 && refMode + 1 < args.Length) TeamStrength.Mode = Enum.Parse<TeamStrength.RefMode>(args[refMode + 1], true);
				Environment.Exit(LeagueSelfTest.Run(db, gameData, Path.Combine(dataDir, "ma9m.db"), hours));
			}
			if (Array.IndexOf(args, "--lobbyranktest") >= 0)
			{
				// Dev (systems session, 2026-11-12): the lobby's 순위 변동 at 0시 of each day of every running league; use a copy.
				Environment.Exit(LobbyRankSelfTest.Run(Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--posttest") >= 0)
			{
				// Dev: the common league calendar through one whole cycle (postseason, close, next league); use a copy.
				Environment.Exit(PostseasonSelfTest.Run(db, gameData, Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--championstest") >= 0)
			{
				// Dev (systems session, 2026-10-28): 챔피언스 — the close, the shared league's 편성, a season of it and the next 편성; use a copy.
				Environment.Exit(ChampionsSelfTest.Run(db, gameData, Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--exhibitiontest") >= 0)
			{
				// Dev: exhibition AI teams (ball ranges, positions, pitcher roles); use a copy of the data.
				LeagueStore teams = new LeagueStore(Path.Combine(dataDir, "ma9m.db"));
				Environment.Exit(ExhibitionHandlers.SelfTest(db, gameData, System.Linq.Enumerable.Select(teams.ActiveTeams(), t => (t.code, t.name)), 300));
			}
			if (Array.IndexOf(args, "--selftest") >= 0)
			{
				Environment.Exit(BroadcastSelfTest.Run(gameData, 200));
			}
			if (Array.IndexOf(args, "--traittest") >= 0)
			{
				Environment.Exit(BroadcastSelfTest.TraitTest(gameData, 3000));
			}
			if (Array.IndexOf(args, "--devvalue") >= 0)
			{
				// Dev (engine session): development points' worth by stat under the rules or a variant (DevValueTest; DV_* settings).
				Environment.Exit(DevValueTest.Run(gameData, Path.Combine(dataDir, "ma9m.db")));
			}
			int tcSim = Array.IndexOf(args, "--tcsim");
			if (tcSim >= 0)
			{
				// Dev: team-colour decks against the best free deck under a ball cap ([cap] [games] after the flag).
				Environment.Exit(TeamColorSim.Run(gameData, args, tcSim));
			}
			if (Array.IndexOf(args, "--trainingtest") >= 0)
			{
				// Dev: training rules and slots; writes to the data directory's DB, so use a copy.
				Environment.Exit(TrainingSelfTest.Run(db, catalog, Path.Combine(dataDir, "ma9m.db")));
			}
			ExhibitionHandlers.Register(loop, db, gameData, Path.Combine(dataDir, "ma9m.db"));
			// League: the client's schedule table, game slots every 20 minutes (missed ones caught up).
			File.WriteAllText(Path.Combine(dataDir, "tables", "ManagementLeagueSchedule.txt"), LeagueRules.ScheduleTable());
			LeagueService.Init(db, gameData, Path.Combine(dataDir, "ma9m.db"));
			int aiTest = Array.IndexOf(args, "--aitest");
			if (aiTest >= 0)
			{
				// Dev (systems session, 2026-10-29): league AI groups against each other, for 메이저's decks ([seasons] then
				// candidate pools "skip:grade:lecture"); read-only.
				int aiSeasons = aiTest + 1 < args.Length && int.TryParse(args[aiTest + 1], out int n) ? n : 6;
				string[] pools = Array.FindAll(args, a => a.Split(':').Length >= 2 && Array.TrueForAll(a.Split(':'), p => int.TryParse(p, out _)));
				Environment.Exit(LeagueService.AiStrengthTest(aiSeasons, pools));
			}
			int warTest = Array.IndexOf(args, "--wartest");
			if (warTest >= 0)
			{
				// Dev: this season's records, WAR and wRC+ for one team's cards (user code after the flag).
				Environment.Exit(SeasonStats.Report(uint.Parse(args[warTest + 1]), catalog));
			}
			LeagueHandlers.Register(loop);
			Postseason.Register(loop);  // 기록실 > 포스트시즌 대진표와 경기 결과 (엔진 세션)
			HustlePlays.Register(loop, Path.Combine(dataDir, "ma9m.db"));  // 일정 및 결과 > 허슬 플레이 (엔진 세션)
			// Inventory, card packs and coupons; team traits; the player card window.
			InventoryHandlers.Register(loop, db, catalog, dataDir);
			TeamPropertyHandlers.Register(loop, dataDir);
			CardInfoHandlers.Register(loop, db, catalog);
			TrainingHandlers.Register(loop, db, catalog, Path.Combine(dataDir, "ma9m.db"));  // 트레이닝 (선수 정보 > 트레이닝)
			AlbumHandlers.Register(loop, db, catalog, Path.Combine(dataDir, "ma9m.db"));  // 구단앨범 (구단정보 마지막 페이지, 상단 엠블렘)
			FusionHandlers.Register(loop, db, catalog);  // 선수카드 조합
			FusionRules.WriteOddsTable(Path.Combine(dataDir, "tables"));  // 조합 확률 게이지의 표 (시스템 세션 2026-11-11)
			TeamDeleteHandlers.Register(loop, Path.Combine(dataDir, "ma9m.db"));  // 구단 삭제 (구단 선택 화면)
			ShopHandlers.Register(loop, db, catalog, Path.Combine(dataDir, "ma9m.db"));  // 상점 (선수팩·트레이닝팩·로스터 확장)
			NextMatchHandlers.Register(loop);  // 다음 경기 상대 1군 (로비 i 버튼, 로스터 상대 버튼)
			TeamColors.Register(loop, db, catalog, dataDir);  // 팀 관리 > 팀컬러 (C/H/P 슬롯, S/A/B/C 등급)
			ContractHandlers.Register(loop, db, catalog, dataDir);  // 팀 관리 > 계약관리, 선수 정보 > 계약 (실제 날짜 30일)
			Missions.Register(loop, db, dataDir);  // 도전과제 > 오늘의 미션 (출석, 리그 안타·탈삼진, 시범경기, 카드팩, 트레이닝팩)
			Achievements.Register(loop, db, catalog, dataDir);  // 도전과제 > 업적 (원작 구단미션 칸: 성적/수집/육성/특별)
			LeagueAwards.Register(loop, db, Path.Combine(dataDir, "ma9m.db"));  // 리그 시상 (시즌 종료 때 LeagueAwards.Settle, 로비에서 시상식)
			OperationHandlers.Register(loop, catalog, Path.Combine(dataDir, "ma9m.db"));  // 팀 관리 > 작전실, 선수 정보 > 개인작전
			RecordHandlers.Register(loop, db, catalog);  // 팀 관리 > 수훈선수 (최근성적은 CardInfoHandlers가 RecordHandlers.FillRecent로)
			Uniforms.Load(dataDir);
			CoachHandlers.Register(loop, db, catalog, dataDir);  // 전력강화 > 코칭스태프 (시스템 세션 11-04; MA9M_COACH=1인 테스트 서버만)
			if (Array.IndexOf(args, "--managetest") >= 0)
			{
				// Dev: 컨디션 회복제 (slots, 24 hours, replace, 적용취소) on the test team; writes to the DB, so use a copy.
				Environment.Exit(PlayerManageHandlers.SelfTest(db, catalog));
			}
			if (Array.IndexOf(args, "--opptest") >= 0)
			{
				// Dev (systems session, 2026-11-08): other clubs' rosters carry their owners' 포지션 변경 (NextMatchHandlers.SelfTest); a copy.
				Environment.Exit(NextMatchHandlers.SelfTest(Path.Combine(dataDir, "ma9m.db"), catalog));
			}
			if (Array.IndexOf(args, "--legendtest") >= 0)
			{
				// Dev (systems session, 2026-11-08): 레전드 1군 limit, 레전드 선수팩, 레전드 조합, on tables holding the 레전드 rows (LegendSelfTest).
				Environment.Exit(LegendSelfTest.Run(catalog, db, Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--autoexhtest") >= 0)
			{
				// Dev (systems session, 2026-11-03): 새벽 자동 시범경기 on a fake clock (ExhibitionAuto.cs); writes, so a copy only.
				Environment.Exit(ExhibitionHandlers.AutoSelfTest(Path.Combine(dataDir, "ma9m.db")));
			}
			int deckCmp = Array.IndexOf(args, "--deckcompare");
			if (deckCmp >= 0)
			{
				// Dev (systems session, 2026-11-10): a real team's lineup variants (DECKCMP_VARIANTS) against its own league
				// ("<team name> [seasons]"); writes the variants' lineups into the DB, so a copy only.
				if (Path.GetFullPath(dataDir).TrimEnd('\\', '/').Equals(@"C:\Ma9MProject\server\data", StringComparison.OrdinalIgnoreCase))
				{
					ServerLog.Warn("--deckcompare writes to the DB: run it on a copy of the data directory");
					Environment.Exit(2);
				}
				int cmpSeasons = deckCmp + 2 < args.Length && int.TryParse(args[deckCmp + 2], out int cs) ? cs : 10;
				Environment.Exit(LeagueService.DeckCompare(args[deckCmp + 1], cmpSeasons, Environment.GetEnvironmentVariable("DECKCMP_VARIANTS"), Path.Combine(dataDir, "ma9m.db")));
			}
			int traitAdvice = Array.IndexOf(args, "--traitadvice");
			if (traitAdvice >= 0 && traitAdvice + 1 < args.Length)
			{
				// Dev (engine session, 2026-11-11): 특성 자동 배분 for "<team name>" (LeagueService.AdviseTraits); on a copy of the data.
				Environment.Exit(LeagueService.TraitAdviceTest(args[traitAdvice + 1], Path.Combine(dataDir, "ma9m.db")));
			}
			int traitBalanceSim = Array.IndexOf(args, "--traitbalancesim");
			if (traitBalanceSim >= 0)
			{
				// Dev (engine session, 2026-11-11): each 팀 특성's value on average rosters in full games ("[pairs] [games]").
				int Arg(int k, int def) => traitBalanceSim + k < args.Length && int.TryParse(args[traitBalanceSim + k], out int v) ? v : def;
				Environment.Exit(LeagueService.TraitBalanceSim(Arg(1, 200), Arg(2, 100)));
			}
			int traitBalance = Array.IndexOf(args, "--traitbalance");
			if (traitBalance >= 0)
			{
				// Dev (engine session, 2026-11-11): each 팀 특성's value on average rosters against its tier's target (TraitBalanceTest).
				Environment.Exit(LeagueService.TraitBalanceTest(traitBalance + 1 < args.Length && int.TryParse(args[traitBalance + 1], out int tbp) ? tbp : 300));
			}
			int autoLineup = Array.IndexOf(args, "--autolineup");
			if (autoLineup >= 0 && autoLineup + 1 < args.Length)
			{
				// Dev (engine session, 2026-11-11): 자동 편성 for "<team name>" [keep] (LeagueService.AutoLineup); on a copy of the data.
				bool keepColour = autoLineup + 2 < args.Length && args[autoLineup + 2] == "keep";
				Environment.Exit(LeagueService.AutoLineupTest(args[autoLineup + 1], keepColour, Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--coachtest") >= 0)
			{
				// Dev (systems session, 2026-11-04): 코칭스태프 (cards, staff, bonuses, 면담, coach training, IV books, 치어리더) on
				// the test team; needs MA9M_COACH=1 and writes, so a copy only.
				Environment.Exit(CoachHandlers.SelfTest(Path.Combine(dataDir, "ma9m.db")));
			}
			int consultWhy = Array.IndexOf(args, "--consultwhy");
			if (consultWhy >= 0 && consultWhy + 1 < args.Length && uint.TryParse(args[consultWhy + 1], out uint whyUser))
			{
				// Dev (systems session, 2026-11-10): why a team's 1군 players do or do not come to the 면담실 now; read-only, MA9M_COACH=1.
				Environment.Exit(CoachHandlers.ConsultWhy(whyUser));
			}
			if (Array.IndexOf(args, "--contracttest") >= 0)
			{
				// Dev (systems session, 2026-11-10): the renewal cost (MA9M_CONTRACT_FROM2) and the cost table; writes, so a copy only.
				if (Path.GetFullPath(dataDir).TrimEnd('\\', '/').Equals(@"C:\Ma9MProject\server\data", StringComparison.OrdinalIgnoreCase))
				{
					ServerLog.Warn("--contracttest writes to the DB: run it on a copy of the data directory");
					Environment.Exit(2);
				}
				Environment.Exit(ContractHandlers.SelfTestOnCopy(dataDir));
			}
			if (Array.IndexOf(args, "--tctest") >= 0)
			{
				// Dev (systems session, 2026-11-09): 팀컬러 자동 등록, the save popup and the 빨간 점 on the test team; writes, so a copy only.
				if (Path.GetFullPath(dataDir).TrimEnd('\\', '/').Equals(@"C:\Ma9MProject\server\data", StringComparison.OrdinalIgnoreCase))
				{
					ServerLog.Warn("--tctest writes to the DB: run it on a copy of the data directory");
					Environment.Exit(2);
				}
				Environment.Exit(TeamColors.AutoSelfTest(Path.Combine(dataDir, "ma9m.db")));
			}
			if (Array.IndexOf(args, "--lessontest") >= 0)
			{
				// Dev: 특별수강 (coaches, rotation, fees, six at once, results, 초기화권, locks) on the test team; use a copy.
				Environment.Exit(LessonHandlers.SelfTest(db, catalog));
			}
			if (Array.IndexOf(args, "--positiontest") >= 0)
			{
				// Dev: 포지션 변경 (role table, list, stat changes, 대성공 odds, once per card, 초기화권) on the test team; use a copy.
				Environment.Exit(PositionChangeHandlers.SelfTest(db));
			}
			if (Array.IndexOf(args, "--uniformtest") >= 0)
			{
				// Dev (engine session): the broadcast uniform of every team and its league's clubs; read-only.
				Environment.Exit(Uniforms.SelfTest());
			}
			int hustleTest = Array.IndexOf(args, "--hustletest");
			if (hustleTest >= 0)
			{
				// Dev (engine session): a user's plays found this season, then the season-close archive; writes, so use a copy.
				Environment.Exit(HustlePlays.SelfTest(uint.Parse(args[hustleTest + 1])));
			}
			loop.Start();
			LeagueService.Start(loop);
			ExhibitionHandlers.StartAuto(loop);  // 새벽 자동 시범경기 (ExhibitionAuto.cs, 시스템 세션 11-03)

			GameServer gameServer = new GameServer(loop);
			gameServer.SetServerInfo(GamePort, 100);
			gameServer.Start();
			ServerLog.Info($"game server listening on tcp/{GamePort}");

			WebApplicationBuilder builder = WebApplication.CreateBuilder();
			builder.Logging.ClearProviders();
			WebApplication app = builder.Build();
			app.Urls.Add($"http://0.0.0.0:{HttpPort}");
			Cdn cdn = new Cdn(dataDir, GamePort);
			app.MapGet("/ma9m/{**path}", cdn.Handle);
			ServerLog.Info($"cdn listening on http/{HttpPort}");
			app.Run();
		}
	}
}
