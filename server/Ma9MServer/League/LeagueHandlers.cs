using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// League screens: unconfirmed games (shown after login when there are any), schedule and
	// results, scoreboards, standings, league broadcasts, and the lobby's next-match box.
	public static class LeagueHandlers
	{
		private static LeagueStore Store => LeagueService.Store;

		public static void Register(GameLoop loop)
		{
			loop.Register<Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT>(Unconfirmed);
			loop.Register<Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT>(Confirm);
			loop.Register<Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP>(ConfirmAll);
			loop.Register<Ma9MPacket.PKS_CS_GAME_SCOREBOARD>(Scoreboard);
			loop.Register<Ma9MPacket.PKS_CS_OTHER_GAME_SCOREBOARD>(OtherScoreboard);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_GAME_RESULT>(GameResult);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_GAME_SCHEDULE>(Schedule);
			loop.Register<Ma9MPacket.PKS_CS_MY_GAME_PUSH_NOTICE>(PushNotice);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_RANK_INFO>(RankInfo);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_BROADCAST>(Broadcast);
			// While a league game is watched the client reports every scene it finished (BroadcastNet
			// SendPK_CS_PAST_SCENE) and plays the next one only after this echo; without it the
			// broadcast stopped after the starting-pitcher introduction (engine session, 2026-10-19).
			// League games are played at their slot time, so every scene may follow at once.
			loop.Register<Ma9MPacket.PKS_CS_PAST_SCENE>((session, packet) =>
			{
				Ma9MPacket.PKS_SC_PAST_SCENE reply = new Ma9MPacket.PKS_SC_PAST_SCENE();
				reply.scene_num.n = packet.scene_num.n;
				session.Send(reply);
			});
			loop.Register<Ma9MPacket.PKS_CS_LEAGUE_ORGANIZED>((session, packet) => { });
		}

		private static LeagueState State(GameSession session) => session.Team == null ? null : Store.Load(session.Team.UserCode);

		private static int Mp(GameSession session) => session.Team == null ? 0 : LeagueService.Balance(session.Team.UserCode);

		private static PString[] Names(LeagueState s) => s.Clubs.Select(c => new PString { str = c.Name }).ToArray();

		private static PByte[] Emblems(LeagueState s) => s.Clubs.Select(c => new PByte { n = c.Emblem }).ToArray();

		// The broadcast batter panel's season line before this game (engine session, 2026-10-30; user: "현재
		// 타율, 홈런, 타점 수 … 경기 중 성적에 따라 실시간으로"): the club's season rows, less this game when its
		// lines are already in them (BroadcastRecorder.BatterLines counts it from the buffer by the
		// client's rules); the client adds the game's at-bats as they happen. Postseason games show the
		// regular season (their lines go elsewhere).
		private static BROADCAST_BATTER_RECORD[] BatterRecords(LeagueState s, LeagueGame g, int club, IEnumerable<int> cards, int team)
		{
			Dictionary<int, PlayerSeason> rows = new Dictionary<int, PlayerSeason>();
			foreach (PlayerSeason p in Store.PlayerLines(s.UserCode, s.Season, club))
			{
				if (!p.Pitcher) rows[p.CardIdx] = p;
			}
			int[,,] game = !Postseason.IsPost(g.Game) && g.Buff != null && !Store.LinesPending(s.UserCode, s.Season, g.Game)
				? BroadcastRecorder.BatterLines(g.Buff) : null;
			List<BROADCAST_BATTER_RECORD> list = new List<BROADCAST_BATTER_RECORD>();
			int order = 0;
			foreach (int card in cards)
			{
				order++;
				BROADCAST_BATTER_RECORD r = new BROADCAST_BATTER_RECORD();
				if (rows.TryGetValue(card, out PlayerSeason p))
				{
					int ab = p.AB, h = p.H, hr = p.HR, rbi = p.RBI;
					if (game != null && order <= 9)
					{
						ab -= game[team, order, 0];
						h -= game[team, order, 1];
						hr -= game[team, order, 2];
						rbi -= game[team, order, 3];
					}
					ab = Math.Max(0, ab); h = Math.Clamp(h, 0, ab); hr = Math.Max(0, hr); rbi = Math.Max(0, rbi);
					r.at_bats.n = (short)Math.Min(short.MaxValue, ab);
					r.hits.n = (byte)Math.Min(255, h);
					r.homeruns.n = (byte)Math.Min(255, hr);
					r.rbi.n = (byte)Math.Min(255, rbi);
					r.batting_average.f = ab > 0 ? h / (float)ab : 0f;
					r.runs.n = (byte)Math.Min(255, p.R);
					r.base_on_balls.n = (byte)Math.Min(255, p.BB);
					r.steal_base.n = (byte)Math.Min(255, p.SB);
				}
				list.Add(r);
			}
			return list.ToArray();
		}

		// The broadcast's pitcher boxes (the starter, the pitching-change window) showed 0 for everything (user, 2026-10-31:
		// "포스트시즌에서 투수 교체 또는 선발 투수 정보 공개 시 리그 성적을 가져와주면 좋겠음"): each pitcher's season line
		// (engine session). In the postseason that is the regular season (its lines go elsewhere); a regular-season game
		// seen again leaves out its own line, as the batters' do (BatterRecords).
		private static BROADCAST_PITCHER_RECORD[] PitcherRecords(LeagueState s, LeagueGame g, int club, PUICardInfo[] pitchers)
		{
			Dictionary<int, PlayerSeason> rows = new Dictionary<int, PlayerSeason>();
			foreach (PlayerSeason p in Store.PlayerLines(s.UserCode, s.Season, club))
			{
				if (p.Pitcher) rows[p.CardIdx] = p;
			}
			bool inRows = !Postseason.IsPost(g.Game) && g.Buff != null && !Store.LinesPending(s.UserCode, s.Season, g.Game);
			List<BROADCAST_PITCHER_RECORD> list = new List<BROADCAST_PITCHER_RECORD>();
			foreach (PUICardInfo card in pitchers)
			{
				BROADCAST_PITCHER_RECORD r = new BROADCAST_PITCHER_RECORD();
				if (card != null && card.index.n != 0 && rows.TryGetValue(card.index.n, out PlayerSeason p))
				{
					PlayerSeason own = inRows ? Store.GameLine(s.UserCode, s.Season, g.Game, card.index.n) : null;
					int G(Func<PlayerSeason, int> f) => Math.Max(0, f(p) - (own != null ? f(own) : 0));
					int outs = G(x => x.Outs), er = G(x => x.ER);
					r.games.n = (byte)Math.Min(255, G(x => x.G));
					r.wins.n = (byte)Math.Min(255, G(x => x.W));
					r.loses.n = (byte)Math.Min(255, G(x => x.L));
					r.saves.n = (byte)Math.Min(255, G(x => x.SV));
					r.holds.n = (byte)Math.Min(255, G(x => x.HLD));
					r.innings_by3.n = (short)Math.Min(short.MaxValue, outs);
					r.strike_outs.n = (short)Math.Min(short.MaxValue, G(x => x.PSO));
					r.base_on_balls.n = (short)Math.Min(short.MaxValue, G(x => x.PBB + x.PHBP));
					r.era.f = outs > 0 ? er * 27f / outs : 0f;
				}
				list.Add(r);
			}
			return list.ToArray();
		}

		// LEAGUE_GAME_RESULT.get_point is a short, so a game's MP over 32,767 (메이저 win 48,000, 챔피언스 up to 60,000) showed
		// 32767 in 일정 및 결과 and the unconfirmed list (user, 2026-10-31: "실제로는 48,000MP가 들어오는 것 같은데 32767 이 값은
		// 뭐지??"). A client with this mark reads it unsigned (up to 65,535); older ones keep the clamp rather than a negative.
		public const string WideMpMark = "ma9m-mp16";

		private static bool ClientReadsWideMp(GameSession session) => session?.ClientVersion?.Contains(WideMpMark) == true;

		private static LEAGUE_GAME_RESULT MyResult(LeagueState s, LeagueGame g, bool wideMp)
		{
			bool home = g.Home == s.MyIndex;
			LEAGUE_GAME_RESULT r = new LEAGUE_GAME_RESULT();
			r.game_num.n = (byte)g.Game;
			r.get_score.n = (byte)Math.Min(255, home ? g.HomeScore : g.AwayScore);
			r.lose_score.n = (byte)Math.Min(255, home ? g.AwayScore : g.HomeScore);
			r.get_point.n = wideMp ? unchecked((short)(ushort)Math.Clamp(g.Mp, 0, ushort.MaxValue)) : (short)Math.Min(short.MaxValue, g.Mp);
			r.match_team_index.n = (byte)(home ? g.Away : g.Home);
			return r;
		}

		// Unconfirmed games of the current season (older seasons' are paid and closed quietly).
		private static void Unconfirmed(GameSession session, Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT packet)
		{
			// The login goes straight to this screen, so the top bar (emblem, team, league, MP) has had no
			// GLOBAL_INFO yet and showed "편성 대기중" with nothing else (user, 2026-10-19). It goes first:
			// the reply below then sets the record the screen counts the results onto.
			TopBar.Send(session);
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT reply = new Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT();
			LeagueState s = State(session);
			if (s == null)
			{
				reply.team_name.array = Array.Empty<PString>();
				reply.team_emblem.array = Array.Empty<PByte>();
				reply.game_result.array = Array.Empty<LEAGUE_GAME_RESULT>();
				reply.CheerAddMPs.array = Array.Empty<PShort>();
				reply.GetPoints.array = Array.Empty<PInteger>();
				session.Send(reply);
				return;
			}
			for (int old = 1; old < s.Season; old++) LeagueService.ConfirmAndPay(s.UserCode, old, Store.Games(s.UserCode, old, unconfirmedOnly: true).Select(g => g.Game));
			List<LeagueGame> games = Store.Games(s.UserCode, s.Season, club: s.MyIndex, unconfirmedOnly: true);
			LeagueGame locked = Locked(s, DateTime.Now);
			if (locked != null)
			{
				games = games.Where(g => g.Game != locked.Game).ToList();  // in its first 5 minutes
			}
			// The record before these games: the client shows it in the top bar and counts each
			// revealed game (and its MP) onto it (Common_TopMenu.SetFakeRecord / CalcFakeRecord).
			LeagueClub me = Store.ConfirmedView(s).Clubs[s.MyIndex];
			reply.season.n = (byte)s.Season;
			reply.sponsor.str = LeagueRules.Sponsor(s.Season);
			reply.cur_game_num.n = (byte)s.Played;
			reply.my_order.n = (byte)s.MyIndex;
			reply.team_name.array = Names(s);
			reply.team_emblem.array = Emblems(s);
			reply.game_result.array = games.Select(g => MyResult(s, g, ClientReadsWideMp(session))).ToArray();
			reply.wins.n = (byte)Math.Min(255, me.W);
			reply.loses.n = (byte)Math.Min(255, me.L);
			reply.draws.n = (byte)Math.Min(255, me.D);
			reply.CheerAddMPs.array = games.Select(g => new PShort { n = CoachHandlers.Cheer(s.UserCode, g) }).ToArray();
			// Each game's MP in full for a 1.0.35+ client (get_point stops at 65,535; user 2026-11-08: "MP를 65,535를 넘을 수 있도록").
			reply.GetPoints.array = games.Select(g => new PInteger { n = g.Mp }).ToArray();
			session.Send(reply);
		}

		private static void Confirm(GameSession session, Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT packet)
		{
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT reply = new Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT();
			LeagueState s = State(session);
			reply.season.n = packet.season.n;
			reply.game_num.n = packet.game_num.n;
			// No top bar until the app has stamped this game (GameSession.ConfirmedLast, 2026-11-09).
			session.ConfirmedLast = true;
			session.ConfirmedAt = DateTime.Now;
			if (s != null)
			{
				LeagueGame g = Store.Games(s.UserCode, packet.season.n, game: packet.game_num.n, club: s.MyIndex).FirstOrDefault();
				LeagueService.ConfirmAndPay(s.UserCode, packet.season.n, new[] { (int)packet.game_num.n });
				if (g != null)
				{
					bool home = g.Home == s.MyIndex;
					int rf = home ? g.HomeScore : g.AwayScore, ra = home ? g.AwayScore : g.HomeScore;
					reply.win_lose_draw.n = (byte)(rf > ra ? 0 : rf < ra ? 1 : 2);
				}
			}
			reply.my_point.n = Mp(session);
			session.Send(reply);
		}

		private static void ConfirmAll(GameSession session, Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP packet)
		{
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP reply = new Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP();
			if (session.Team != null)
			{
				// "Skip" confirms the games the app listed and pays their MP and 홈런볼 (not the game in its first 5 minutes).
				// The app sends its list (Unconfirmed_Main.GetUnconfirmedGameNums); it was ignored and every waiting game was
				// confirmed, so games played while the screen stayed open were paid without being shown (AI tester 1, 11-10: a
				// list made at 10:02 had 124-138, 139 being in its first 5 minutes; 자동결과확인 at 10:32 confirmed 139 and 140
				// too). The rest stay for the next results screen. No list (none sent): everything waiting, as before.
				LeagueState s = State(session);
				LeagueGame locked = s != null && s.Season == packet.season.n ? Locked(s, DateTime.Now) : null;
				List<int> shown = packet.game_num_list.array?.Select(b => (int)b.n).Where(n => n > 0).Distinct().ToList();
				LeagueService.ConfirmAndPay(session.Team.UserCode, packet.season.n,
					shown != null && shown.Count > 0 ? shown.Where(n => locked == null || n != locked.Game).ToList()
					: locked == null ? null
					: Store.Games(s.UserCode, s.Season, unconfirmedOnly: true).Select(g => g.Game).Where(n => n != locked.Game).ToList());
			}
			reply.my_point.n = Mp(session);
			session.Send(reply);
			// The record the games just confirmed made (2026-11-12, AI tester NC 23: after 모두 확인 the top bar showed the new
			// record only when an 업적 popup came with it, whose payment sends the bar; with none it stayed until the lobby).
			if (session.Team != null)
			{
				TopBar.Send(session);
			}
		}

		// The client starts both sides of a league broadcast on pitcher slot (game - 1) % 5 (DataCenter: its own rotation
		// by game number). A club's real starter follows its own postseason games in the postseason (LeagueService.RotationOf)
		// and skips an empty slot, so it was not always that one (user, 2026-10-26: '한국시리즈 6차전 … 중계에서는 15윤성환
		// … 스코어보드에는 15피가로'). The real starter takes that slot in the list sent; only SP1-5 trade places, and the
		// scenes name pitchers by slot only for relievers, who come from RP1-CP.
		private static void ShowRealStarter(PUICardInfo[] pitchers, Lineup l, LeagueState s, int club, int game)
		{
			int shown = (game - 1) % 5, real = LeagueService.StarterSlot(l, LeagueService.RotationOf(s, club, game));
			if (pitchers == null || shown == real || real >= 5 || shown >= pitchers.Length || real >= pitchers.Length) return;
			(pitchers[shown], pitchers[real]) = (pitchers[real], pitchers[shown]);
		}

		// The card as the client draws it. A user's own card carries its contract days, 포지션 변경 and lock (user,
		// 2026-10-24: the lobby's next-match starters showed 30 days, a placeholder from before the 7-day contracts);
		// an AI club's card shows the starting contract. Pass owned (Owned) when drawing many cards of one club.
		private static PUICardInfo Card(uint owner, int idx, DateTime day, Dictionary<int, TeamCard> owned = null)
		{
			if (idx == 0)
			{
				return new PUICardInfo();
			}
			owned ??= Owned(owner);
			return owned.TryGetValue(idx, out TeamCard c)
				? new PUICardInfo(idx, Condition.For(owner, idx, day), c.RemainContract, c.PositionTrainingInfo, (byte)(c.IsLock ? 2 : 1))
				: new PUICardInfo(idx, Condition.For(owner, idx, day), RosterHandlers.StartContract);
		}

		// The same card for other screens that draw another club's players (systems session, 2026-11-08, user: "다른 팀
		// 로스터 볼 때 포지션 변경 상태가 보이지 않음 … 김영우가 중계인 상태로 셋업에 있는 것으로 보임"): the 상대 1군 window
		// (NextMatchHandlers) and the league player ranking (RecordHandlers).
		public static PUICardInfo CardOf(uint owner, int idx, DateTime day, Dictionary<int, TeamCard> owned = null) => Card(owner, idx, day, owned);

		public static Dictionary<int, TeamCard> OwnedBy(uint owner) => Owned(owner);

		// A user's team cards by number; none for an AI club (LeagueService.AiOwner sets the top bit, and an exhibition
		// AI code owns no cards).
		private static Dictionary<int, TeamCard> Owned(uint owner) =>
			(owner & 0x8000_0000u) != 0 || RosterHandlers.Db == null ? new Dictionary<int, TeamCard>() : RosterHandlers.Db.GetCards(owner).ToDictionary(c => c.Idx);

		// A packet sends the field objects its constructor bound (fields[k]), so a single card field
		// is filled in place; assigning a new PUICardInfo to it would send the empty original.
		public static void SetCard(PUICardInfo into, uint owner, int idx, DateTime day)
		{
			PUICardInfo c = Card(owner, idx, day);
			into.index.n = c.index.n;
			into.condition.n = c.condition.n;
			into.remain_contract.n = c.remain_contract.n;
			into.position_training_info.n = c.position_training_info.n;
			into.IsLock.n = c.IsLock.n;
			into.potential_index.n = c.potential_index.n;
			into.potential_level.n = c.potential_level.n;
		}

		public static Ma9MPacket.PKS_SC_GAME_SCOREBOARD Board(LeagueState s, LeagueGame g, bool mine)
		{
			GameDetail d = g.Detail;
			DateTime day = s.SlotTime(g.Game);  // the slot's time: a 컨디션 회복제 counts by the hour (Condition.For)
			LeagueClub home = s.Clubs[g.Home], away = s.Clubs[g.Away];
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD r = new Ma9MPacket.PKS_SC_GAME_SCOREBOARD();
			r.is_my_home.n = (byte)(g.Home == s.MyIndex ? 1 : 0);
			r.home_team_name.str = home.Name;
			r.home_team_emblem.n = home.Emblem;
			r.away_team_name.str = away.Name;
			r.away_team_emblem.n = away.Emblem;
			// The owner of the BEST/WORST cards: 254 (eUserNum.ONESELF) for the user's club, so a tap opens
			// the user's own card window (records, 최근성적, development) instead of the others' window
			// (systems session, 2026-10-20); other clubs by their index.
			r.home_user_num.n = (byte)(g.Home == s.MyIndex ? 254 : g.Home);
			r.away_user_num.n = (byte)(g.Away == s.MyIndex ? 254 : g.Away);
			r.home_score.array = Innings(d.HomeLine);
			r.away_score.array = Innings(d.AwayLine);
			int[] h = d.Stats[0], a = d.Stats[1];
			r.home_hit.n = (byte)h[0]; r.away_hit.n = (byte)a[0];
			r.home_error.n = (byte)h[1]; r.away_error.n = (byte)a[1];
			r.home_BB.n = (byte)h[2]; r.away_BB.n = (byte)a[2];
			r.home_HR.n = (byte)h[3]; r.away_HR.n = (byte)a[3];
			r.home_SB.n = (byte)h[4]; r.away_SB.n = (byte)a[4];
			r.home_K.n = (byte)h[5]; r.away_K.n = (byte)a[5];
			r.home_DP.n = (byte)h[6]; r.away_DP.n = (byte)a[6];
			r.get_point.n = mine ? g.Mp : 0;
			SetCard(r.home_best, home.Owner, d.BestIdx[0], day);
			SetCard(r.away_best, away.Owner, d.BestIdx[1], day);
			SetCard(r.home_worst, home.Owner, d.WorstIdx[0], day);
			SetCard(r.away_worst, away.Owner, d.WorstIdx[1], day);
			r.win_pitcher_index.n = d.WinIdx;
			r.save_pitcher_index.n = d.SaveIdx;
			r.lose_pitcher_index.n = d.LossIdx;
			// 치어리더 (코칭스태프): the "(+n)" after the game's MP.
			r.CheerAddMP.n = mine ? CoachHandlers.Cheer(s.UserCode, g) : (short)0;
			return r;
		}

		// The user's game: prev/next step through the user's played games.
		private static void Scoreboard(GameSession session, Ma9MPacket.PKS_CS_GAME_SCOREBOARD packet)
		{
			// Game 0: the exhibition's box score (the client asks for it after SKIP, ExhibitionHandlers.Board).
			if (packet.game_num.n == 0 && ExhibitionHandlers.Board(session) is Ma9MPacket.PKS_SC_GAME_SCOREBOARD exhibition)
			{
				session.Send(exhibition);
				return;
			}
			LeagueState s = State(session);
			LeagueGame g = s == null ? null : Store.Games(s.UserCode, s.Season, game: packet.game_num.n, club: s.MyIndex).FirstOrDefault();
			LeagueGame locked = s == null ? null : Locked(s, DateTime.Now);
			if (g == null || g.Game == locked?.Game)
			{
				session.Send(EmptyBoard());
				return;
			}
			// Seeing the board is seeing the result: the game's player lines (and earlier ones still
			// waiting) go into the records and 최근성적 now, before its confirmation (user, 2026-10-20).
			Store.RevealLines(s.UserCode, s.Season, g.Game);
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD r = Board(s, g, true);
			// Prev / next among the user's played games (a mid-season start has none before its first game;
			// postseason games follow game 144), leaving out the one in its first 5 minutes.
			List<int> mine = Store.Games(s.UserCode, s.Season, club: s.MyIndex).Select(x => x.Game).Where(n => n != locked?.Game).ToList();
			int at = mine.IndexOf(g.Game);
			r.prev_game_num.n = (byte)(at > 0 ? mine[at - 1] : 0);
			r.next_game_num.n = (byte)(at >= 0 && at + 1 < mine.Count ? mine[at + 1] : 0);
			session.Send(r);
		}

		public static Ma9MPacket.PKS_SC_GAME_SCOREBOARD EmptyBoard()
		{
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD r = new Ma9MPacket.PKS_SC_GAME_SCOREBOARD();
			r.home_team_name.str = string.Empty;
			r.away_team_name.str = string.Empty;
			r.home_score.array = Innings(Array.Empty<int>());
			r.away_score.array = Innings(Array.Empty<int>());
			return r;
		}

		// ScoreBoard_Main draws 12 innings, counts the runs up until the first 255 (an inning not
		// played, also the home 9th when it was not needed) and reads both lines with the away line's
		// length. Lines of their real length (away 9, home 8 after a home win) threw there, and empty
		// lines never ended the count-up, so the board hung (engine session, 2026-10-19).
		// The 12th column is "X" (user, 2026-10-26): the runs from the 12th inning on, as the postseason's
		// 끝장승부 can go past it; "-" when the game ended sooner (LeagueRules.MaxInnings).
		private const int BoardInnings = 12;

		public static PByte[] Innings(int[] line) =>
			Enumerable.Range(0, BoardInnings).Select(i => new PByte
			{
				n = (byte)(i >= line.Length ? 255 : Math.Min(254, i < BoardInnings - 1 ? line[i] : line.Skip(i).Sum()))
			}).ToArray();

		// Another match of a game slot (result_num = its place in the schedule row).
		private static void OtherScoreboard(GameSession session, Ma9MPacket.PKS_CS_OTHER_GAME_SCOREBOARD packet)
		{
			LeagueState s = State(session);
			LeagueGame g = null;
			if (s != null && packet.game_num.n >= 1 && packet.game_num.n <= LeagueRules.Games && packet.result_num.n < LeagueRules.Clubs / 2
				&& Locked(s, DateTime.Now)?.Game != packet.game_num.n)
			{
				(int away, int home) = LeagueRules.Pairs(packet.game_num.n, s.Start)[packet.result_num.n];
				g = Store.Games(s.UserCode, s.Season, game: packet.game_num.n).FirstOrDefault(x => x.Home == home && x.Away == away);
			}
			session.Send(g == null ? EmptyBoard() : Board(s, g, g.Home == s.MyIndex || g.Away == s.MyIndex));
		}

		private static void GameResult(GameSession session, Ma9MPacket.PKS_CS_ENTER_GAME_RESULT packet)
		{
			Ma9MPacket.PKS_SC_ENTER_GAME_RESULT reply = new Ma9MPacket.PKS_SC_ENTER_GAME_RESULT();
			LeagueState s = State(session);
			if (s == null)
			{
				reply.team_name.array = Array.Empty<PString>();
				reply.team_emblem.array = Array.Empty<PByte>();
				reply.game_result.array = Array.Empty<LEAGUE_GAME_RESULT>();
				reply.CheerAddMPs.array = Array.Empty<PShort>();
				reply.GetPoints.array = Array.Empty<PInteger>();
				reply.sponsor.str = string.Empty;
				session.Send(reply);
				return;
			}
			// Results as far as the user has seen them (the rest wait on the unconfirmed-games screen).
			int seen = Math.Min(Seen(s), LeagueRules.Games);  // the regular season's (postseason: Postseason)
			List<LeagueGame> games = Store.Games(s.UserCode, s.Season, club: s.MyIndex).Where(g => g.Game <= seen).ToList();
			reply.season.n = (byte)s.Season;
			reply.sponsor.str = LeagueRules.Sponsor(s.Season);
			reply.league_grade.n = LeagueRules.TierGrade[s.Tier];
			reply.cur_game_num.n = (byte)seen;
			reply.my_order.n = (byte)s.MyIndex;
			reply.team_name.array = Names(s);
			reply.team_emblem.array = Emblems(s);
			reply.game_result.array = games.Select(g => MyResult(s, g, ClientReadsWideMp(session))).ToArray();
			reply.CheerAddMPs.array = games.Select(g => new PShort { n = CoachHandlers.Cheer(s.UserCode, g) }).ToArray();
			// Each game's MP in full for a 1.0.35+ client (get_point stops at 65,535; user 2026-11-08: "MP를 65,535를 넘을 수 있도록").
			reply.GetPoints.array = games.Select(g => new PInteger { n = g.Mp }).ToArray();
			session.Send(reply);
		}

		// Every club's runs in every played game slot.
		private static void Schedule(GameSession session, Ma9MPacket.PKS_CS_ENTER_GAME_SCHEDULE packet)
		{
			Ma9MPacket.PKS_SC_ENTER_GAME_SCHEDULE reply = new Ma9MPacket.PKS_SC_ENTER_GAME_SCHEDULE();
			LeagueState s = State(session);
			reply.alarm_info.array = Enumerable.Range(0, (LeagueRules.Games + 7) / 8).Select(_ => new PByte()).ToArray();
			if (s == null)
			{
				reply.team_name.array = Array.Empty<PString>();
				reply.team_emblem.array = Array.Empty<PByte>();
				reply.results.array = Array.Empty<LEAGUE_GAME_RESULT_ALL>();
				session.Send(reply);
				return;
			}
			List<LEAGUE_GAME_RESULT_ALL> results = new List<LEAGUE_GAME_RESULT_ALL>();
			// Slots as far as the user has seen their own game (the rest look unplayed, as in the standings);
			// the regular season's only.
			int seen = Math.Min(Seen(s), LeagueRules.Games);
			foreach (IGrouping<int, LeagueGame> slot in Store.Games(s.UserCode, s.Season).Where(g => g.Game <= seen).GroupBy(g => g.Game).OrderBy(x => x.Key))
			{
				byte[] scores = new byte[LeagueRules.Clubs];
				foreach (LeagueGame g in slot)
				{
					scores[g.Home] = (byte)Math.Min(255, g.HomeScore);
					scores[g.Away] = (byte)Math.Min(255, g.AwayScore);
				}
				LEAGUE_GAME_RESULT_ALL r = new LEAGUE_GAME_RESULT_ALL();
				r.game_num.n = (byte)slot.Key;
				r.scores.array = scores.Select(x => new PByte { n = x }).ToArray();
				r.push_notice.array = Enumerable.Range(0, LeagueRules.Clubs / 2).Select(_ => new PByte()).ToArray();
				results.Add(r);
			}
			reply.cur_game_num.n = (byte)seen;
			reply.team_name.array = Names(s);
			reply.team_emblem.array = Emblems(s);
			reply.results.array = results.ToArray();
			reply.league_grade.n = LeagueRules.TierGrade[s.Tier];
			session.Send(reply);
		}

		private static void PushNotice(GameSession session, Ma9MPacket.PKS_CS_MY_GAME_PUSH_NOTICE packet)
		{
			// No phone notifications in solo play; the toggle is acknowledged.
			Ma9MPacket.PKS_SC_MY_GAME_PUSH_NOTICE reply = new Ma9MPacket.PKS_SC_MY_GAME_PUSH_NOTICE();
			reply.result.n = 0;
			reply.active_state.n = packet.active.n;
			session.Send(reply);
		}

		private static void RankInfo(GameSession session, Ma9MPacket.PKS_CS_ENTER_RANK_INFO packet)
		{
			Ma9MPacket.PKS_SC_ENTER_RANK_INFO reply = new Ma9MPacket.PKS_SC_ENTER_RANK_INFO();
			LeagueState s = State(session);
			if (s == null)
			{
				reply.season_sponsor.str = string.Empty;
				reply.team_record.array = Array.Empty<LEAGUE_TEAM_RECORD>();
				session.Send(reply);
				return;
			}
			s = Store.ConfirmedView(s);  // no results ahead of the unconfirmed-games screen
			List<LeagueClub> ranked = s.Ranked();
			LeagueClub leader = ranked[0];
			reply.season.n = (byte)s.Season;
			reply.season_sponsor.str = LeagueRules.Sponsor(s.Season);
			reply.team_record.array = ranked.Select((c, i) =>
			{
				ClubTotals k = c.Totals;
				LEAGUE_TEAM_RECORD r = new LEAGUE_TEAM_RECORD();
				r.cur_rank.n = (byte)(i + 1);
				r.last_rank.n = (byte)(c.LastRank > 0 ? c.LastRank : i + 1);
				r.team_emblem.n = c.Emblem;
				r.team_name.str = c.Name;
				r.cur_game_num.n = (byte)(c.W + c.L + c.D);
				r.win.n = (byte)Math.Min(255, c.W);
				r.lose.n = (byte)Math.Min(255, c.L);
				r.draw.n = (byte)Math.Min(255, c.D);
				r.winlosediff.f = ((leader.W - leader.L) - (c.W - c.L)) / 2f;
				r.get_score.n = (short)c.RunsFor;
				r.lose_score.n = (short)c.RunsAgainst;
				r.batting_average.f = k.AB > 0 ? k.H / (float)k.AB : 0;
				r.ERA.f = k.Outs > 0 ? k.ER * 27f / k.Outs : 0;
				r.error.n = (short)k.E;
				r.hits.n = (short)k.H;
				r.homeruns.n = (short)k.HR;
				r.SB.n = (short)k.SB;
				r.BB.n = (short)k.BB;
				r.pitcher_hits.n = (short)k.PH;
				r.pitcher_homeruns.n = (short)k.PHR;
				r.pitcher_K.n = (short)k.PK;
				r.pitcher_BB.n = (short)k.PBB;
				return r;
			}).ToArray();
			reply.my_rank.n = (byte)s.RankOf(s.MyIndex);
			reply.remain_games.n = (byte)Math.Max(0, LeagueRules.Games - s.Played);
			session.Send(reply);
		}

		// A played league game from its stored broadcast (any match of the slot).
		private static void Broadcast(GameSession session, Ma9MPacket.PKS_CS_ENTER_BROADCAST packet)
		{
			LeagueState s = State(session);
			if (s == null) return;
			int gameNum = packet.game_num.n == 0 ? s.LastGame : packet.game_num.n;
			List<LeagueGame> slot = Store.Games(s.UserCode, s.Season, game: gameNum, withBuff: true);
			LeagueGame g = slot.FirstOrDefault(x => x.Home == s.MyIndex || x.Away == s.MyIndex);
			if (packet.result_num.n < LeagueRules.Clubs / 2 && gameNum >= 1 && gameNum <= LeagueRules.Games && packet.game_num.n != 0)
			{
				(int pa, int ph) = LeagueRules.Pairs(gameNum, s.Start)[packet.result_num.n];
				g = slot.FirstOrDefault(x => x.Home == ph && x.Away == pa) ?? g;
			}
			else if (Postseason.IsPost(gameNum))
			{
				// A postseason slot has one game: from the postseason scoreboards, and from the lobby's 중계
				// button also when the user's club is not in it (the series on now).
				g ??= slot.FirstOrDefault();
			}
			bool due = s.NextGame <= LeagueRules.Games ? s.SlotTime(s.NextGame) <= DateTime.Now
				: LeagueRules.HasPostseason(s.Tier) && s.PostSlot <= LeagueCalendar.LastPostSlot && LeagueCalendar.PostSlotTime(s.Start, s.PostSlot) <= DateTime.Now;
			if (packet.game_num.n == 0 && due)
			{
				// The slot is due but the 15-second league tick has not played it yet (engine session,
				// 2026-10-19: 중계 pressed at 01:20:01 waited forever, the game was played at 01:20:02):
				// play the due games now and look again. The client gets no reply otherwise. Also when the
				// previous slot's game is there (engine session, 2026-11-05; user: "딱 시작하자마자 클릭하면 이미
				// 종료된 경기 다시보기"): the button showed that finished game for up to 15 s after the start.
				LeagueService.Tick(DateTime.Now);
				s = State(session);
				gameNum = s.LastGame;
				slot = Store.Games(s.UserCode, s.Season, game: gameNum, withBuff: true);
				g = slot.FirstOrDefault(x => x.Home == s.MyIndex || x.Away == s.MyIndex) ?? (Postseason.IsPost(gameNum) ? slot.FirstOrDefault() : null);
			}
			if (g == null || g.Buff == null)
			{
				// No league game yet (a new team before its first slot): game 0 tells the client to drop
				// its loading screen and say so (Common_TopMenu, client build after rel18).
				ServerLog.Warn($"[{session.Id}] league broadcast: no game {gameNum}");
				Ma9MPacket.PKS_SC_ENTER_BROADCAST none = new Ma9MPacket.PKS_SC_ENTER_BROADCAST();
				none.game_num.n = 0;
				session.Send(none);
				return;
			}
			Ma9MPacket.PKS_SC_ENTER_BROADCAST r = BroadcastReply(s, g);
			bool othersLive = Postseason.IsPost(g.Game) && g.Game == s.LastGame && g.Home != s.MyIndex && g.Away != s.MyIndex
				&& DateTime.Now < s.SlotTime(g.Game).AddSeconds(Math.Min(g.Buff[0] * SceneSeconds, LeagueCalendar.PostSlotMinutes * 60 - 30));
			if (packet.game_num.n == 0 && (OnAir(s, DateTime.Now)?.Game == g.Game || othersLive))
			{
				// The 중계 button joins the game where it is now (8 s a scene from the slot's start). At least 1: the
				// client takes 0 for a finished game shown from its start (Ma9MReplayBanner, engine session 2026-10-31).
				r.scene_time.n = Math.Max(1, (long)(DateTime.Now - s.SlotTime(g.Game)).TotalSeconds);
			}
			session.Send(r);
		}

		// A stored game's broadcast from the start, with both clubs as the season has them (also the 허슬 플레이
		// clips, HustlePlays, which keep it for a closed season; engine session, 2026-10-31).
		internal static Ma9MPacket.PKS_SC_ENTER_BROADCAST BroadcastReply(LeagueState s, LeagueGame g)
		{
			DateTime day = s.SlotTime(g.Game);  // the slot's time: a 컨디션 회복제 counts by the hour (Condition.For)
			LeagueClub home = s.Clubs[g.Home], away = s.Clubs[g.Away];
			Lineup hl = Lineup.Parse(g.Detail.HomeLineup), al = Lineup.Parse(g.Detail.AwayLineup);
			// Ranks and record as the user may know them (before the unconfirmed games).
			LeagueState view = Store.ConfirmedView(s);
			LeagueClub me = view.Clubs[s.MyIndex];
			Ma9MPacket.PKS_SC_ENTER_BROADCAST r = new Ma9MPacket.PKS_SC_ENTER_BROADCAST();
			r.game_num.n = (byte)g.Game;
			r.result_num.n = Postseason.IsPost(g.Game) ? (byte)0 : (byte)Array.FindIndex(LeagueRules.Pairs(g.Game, s.Start), p => p.home == g.Home && p.away == g.Away);
			r.weather.n = 0;
			r.scene_num.n = 0;
			r.scene_time.n = 0;
			r.away_team_name.str = away.Name;
			r.home_team_name.str = home.Name;
			r.away_emblem.n = away.Emblem;
			r.home_emblem.n = home.Emblem;
			r.away_rank.n = (byte)view.RankOf(g.Away);
			r.home_rank.n = (byte)view.RankOf(g.Home);
			Dictionary<int, TeamCard> awayOwned = Owned(away.Owner), homeOwned = Owned(home.Owner);
			r.away_batters.array = al.Batters.Where(i => i != 0).Select(i => Card(away.Owner, i, day, awayOwned)).ToArray();
			r.away_batters_record.array = BatterRecords(s, g, g.Away, al.Batters.Where(i => i != 0), 0);
			r.away_fielders.array = al.Fielders.Select(f => new PByte { n = (byte)(f - 1) }).ToArray();
			r.away_pitchers.array = al.Pitchers.Select(i => Card(away.Owner, i, day, awayOwned)).ToArray();
			r.away_pitchers_record.array = r.away_pitchers.array.Select(_ => new BROADCAST_PITCHER_RECORD()).ToArray();
			r.home_batters.array = hl.Batters.Where(i => i != 0).Select(i => Card(home.Owner, i, day, homeOwned)).ToArray();
			r.home_batters_record.array = BatterRecords(s, g, g.Home, hl.Batters.Where(i => i != 0), 1);
			r.home_fielders.array = hl.Fielders.Select(f => new PByte { n = (byte)(f - 1) }).ToArray();
			r.home_pitchers.array = hl.Pitchers.Select(i => Card(home.Owner, i, day, homeOwned)).ToArray();
			r.home_pitchers_record.array = r.home_pitchers.array.Select(_ => new BROADCAST_PITCHER_RECORD()).ToArray();
			ShowRealStarter(r.away_pitchers.array, al, s, g.Away, g.Game);
			ShowRealStarter(r.home_pitchers.array, hl, s, g.Home, g.Game);
			r.away_pitchers_record.array = PitcherRecords(s, g, g.Away, r.away_pitchers.array);
			r.home_pitchers_record.array = PitcherRecords(s, g, g.Home, r.home_pitchers.array);
			r.buff.bytes = g.Buff;
			r.is_my_home.n = (byte)(g.Home == s.MyIndex ? 1 : 0);
			r.wins.n = (byte)Math.Min(255, me.W);
			r.loses.n = (byte)Math.Min(255, me.L);
			r.draws.n = (byte)Math.Min(255, me.D);
			r.event_flag.n = 0;
			r.home_team_uniform.array = Uniforms.For(home.Owner, home.Emblem, !home.IsHuman && home.AiKey >= 0 ? home.Name : null, true);
			r.away_team_uniform.array = Uniforms.For(away.Owner, away.Emblem, !away.IsHuman && away.AiKey >= 0 ? away.Name : null, false);
			return r;
		}

		// Whether the app should go to the results screen. Only this season's games count: the screen lists this season's alone
		// (Unconfirmed), so games left unconfirmed in an earlier season sent the app to an empty screen whose 자동결과확인
		// had nothing to confirm (user, 2026-11-10 23:2x: "시즌 끝나고 리그 바뀐 뒤 결과확인 화면으로 넘어가는데 경기가 없어서 확인을 누를
		// 수 없는 버그"; 행복한현대's season-7 postseason after the 23:00 편성). Those are settled here first (confirmed and paid,
		// as the results screen did on its way in).
		public static bool HasUnconfirmed(uint user)
		{
			if (Store == null) return false;
			LeagueState s = Store.Load(user);
			if (s == null) return false;
			SettleOldSeasons(s);
			LeagueGame locked = Locked(s, DateTime.Now);
			return Store.HasUnconfirmed(user, s.Season, locked?.Game ?? 0, onlySeason: s.Season);
		}

		// Games of seasons before the state's, still unconfirmed: confirmed and paid (MP, 홈런볼, missions) without a screen.
		public static void SettleOldSeasons(LeagueState s)
		{
			foreach (int old in Store.UnconfirmedSeasonsBefore(s.UserCode, s.Season))
			{
				List<int> games = Store.Games(s.UserCode, old, unconfirmedOnly: true).Select(g => g.Game).ToList();
				(int mp, int hr) = LeagueService.ConfirmAndPay(s.UserCode, old, games);
				ServerLog.Info($"league: user {s.UserCode} season {old} left {games.Count} game(s) unconfirmed ({games.Min()}-{games.Max()}); settled with the season {s.Season} league, +{mp:N0} MP, 홈런볼 {hr}");
			}
		}

		// How long the user's game of the latest slot is no result at all (user, 2026-10-20: "경기 결과확인은
		// 경기 시작하고 5분 뒤에"). After that the results screen has it while the broadcast may still run
		// (OnAir); its player lines show once the user has seen its scoreboard (LeagueStore.RevealLines).
		private static readonly TimeSpan ResultLock = TimeSpan.FromMinutes(5);

		public static LeagueGame Locked(LeagueState s, DateTime now)
		{
			// The latest slot played (a postseason one from game 145 on; LeagueState.LastGame).
			if (s.LastGame < 1) return null;
			DateTime start = s.SlotTime(s.LastGame);
			if (now < start || now >= start + ResultLock) return null;
			LeagueGame g = Store.Games(s.UserCode, s.Season, game: s.LastGame, club: s.MyIndex).FirstOrDefault();
			return g == null || g.Confirmed ? null : g;
		}

		// The latest slot's game the 중계 button joins, for its first 5 minutes (user, 2026-10-21: the button stays
		// those 5 minutes, also after another menu and back in the lobby; the result shows after them): the
		// user's game, or in the postseason the slot's game when the user's club is not in it. Returns the game
		// and the seconds left of the 5 minutes (TopBar: in_broadcast).
		public static (int game, double secondsLeft)? BroadcastWindow(LeagueState s, DateTime now)
		{
			if (s.LastGame < 1) return null;
			DateTime start = s.SlotTime(s.LastGame);
			if (now < start || now >= start + ResultLock) return null;
			return (s.LastGame, (start + ResultLock - now).TotalSeconds);
		}

		// The last game the user has seen the result of: the one before the first unconfirmed game
		// (ConfirmedView's line), for the schedule and the results list.
		public static int Seen(LeagueState s)
		{
			List<LeagueGame> pending = Store.Games(s.UserCode, s.Season, club: s.MyIndex, unconfirmedOnly: true);
			return pending.Count == 0 ? Math.Max(s.Played, s.LastGame) : pending.Min(g => g.Game) - 1;
		}

		// Seconds a broadcast scene takes in real time (the client's DataCenter.FastForwardingScene).
		private const int SceneSeconds = 8;

		// The user's game of the latest slot while its broadcast is still running: the slot plays the
		// game at once, but it is "on air" for its scenes × 8 s (within the slot), and the 중계 button
		// joins it live. Its result waits only 5 minutes (Locked; user, 2026-10-20); until 2026-10-20
		// it waited for the whole broadcast.
		public static LeagueGame OnAir(LeagueState s, DateTime now)
		{
			if (s.LastGame < 1) return null;
			DateTime start = s.SlotTime(s.LastGame);
			if (now < start) return null;
			LeagueGame g = Store.Games(s.UserCode, s.Season, game: s.LastGame, club: s.MyIndex, withBuff: true).FirstOrDefault();
			if (g?.Buff == null || g.Buff.Length == 0 || g.Confirmed) return null;
			int slotMinutes = Postseason.IsPost(g.Game) ? LeagueCalendar.PostSlotMinutes : LeagueRules.SlotMinutes;
			double seconds = Math.Min(g.Buff[0] * SceneSeconds, slotMinutes * 60 - 30);
			return now < start.AddSeconds(seconds) ? g : null;
		}

		// 순위 변동 in the lobby (user, 2026-11-12: "로비의 순위 표시는 전날 마지막 순위와 비교합시다. 전 시즌이랑 비교하는건줄은 몰랐네"):
		// the rank at the end of the previous day, from the season's games before today 0시 (and before the first
		// unconfirmed one, so no result shows ahead of its screen). It used to be last season's final rank (LastRank). With
		// no game before today in this season (its first day, a club that joined today) there is nothing to compare: no change.
		public static int PreviousDayRank(LeagueState s, int now) => PreviousDayRank(Store, s, now, DateTime.Today);

		// `today`: that day's 0시 (LobbyRankSelfTest asks for past days).
		internal static int PreviousDayRank(LeagueStore store, LeagueState s, int now, DateTime today)
		{
			int from = Math.Max(1, s.FirstGame);
			if (from > LeagueRules.Games || from >= s.NextGame || s.SlotTime(from) >= today) return now;
			int first = s.NextGame;
			for (int g = from; g < s.NextGame && g <= LeagueRules.Games; g++)
			{
				if (s.SlotTime(g) >= today) { first = g; break; }
			}
			List<LeagueGame> pending = store.Games(s.UserCode, s.Season, club: s.MyIndex, unconfirmedOnly: true);
			if (pending.Count > 0) first = Math.Min(first, pending.Min(g => g.Game));
			if (first <= from) return now;
			return store.ViewBefore(s, first).RankOf(s.MyIndex);
		}

		// The lobby's next-match box, ball caps, ranking and time to the next league.
		public static void FillMain(GameSession session, Ma9MPacket.PKS_SC_ENTER_MAIN reply)
		{
			LeagueState s = State(session);
			if (s == null) return;
			// The next season's cap from 팀 정비 on (LeagueState.CapTier).
			reply.today_max_cost.n = (byte)LeagueRules.BallCap[s.CapTier];
			reply.tomorrow_max_cost.n = (byte)LeagueRules.BallCap[s.CapTier];
			LeagueState view = Store.ConfirmedView(s);  // no results ahead of the unconfirmed-games screen
			reply.team_ranking_now.n = view.RankOf(s.MyIndex);
			reply.team_ranking_befor.n = PreviousDayRank(s, reply.team_ranking_now.n);
			// Hours to the next league's formation (LeagueCalendar: 59 h into the season).
			DateTime form = s.Start + LeagueCalendar.FormAt;
			reply.hours_to_next_league_organize.n = (byte)Math.Clamp(Math.Ceiling((form - DateTime.Now).TotalHours), 0, 255);
			// The user's next game: the league game, or the postseason game when the user's club plays next
			// (none while it waits for a series or is out; user, 2026-10-21).
			if (LeagueService.NextGameOf(s) is not (int next, int opp)) return;
			LeagueClub op = s.Clubs[opp];
			DateTime day = s.SlotTime(next);  // the slot's time: a 컨디션 회복제 counts by the hour (Condition.For)
			Lineup mine = LeagueService.LineupOf(s, s.Clubs[s.MyIndex], out _);
			Lineup theirs = LeagueService.LineupOf(s, op, out _);
			reply.exist_next_match.n = 0;
			reply.next_match_team_name.str = op.Name;
			reply.next_match_team_rank.n = (byte)view.RankOf(opp);
			SetCard(reply.next_match_my_starter, session.Team.UserCode, mine.Pitchers[LeagueService.StarterSlot(mine, LeagueService.RotationOf(s, s.MyIndex, next))], day);
			SetCard(reply.next_match_op_starter, op.Owner, theirs.Pitchers[LeagueService.StarterSlot(theirs, LeagueService.RotationOf(s, opp, next))], day);
			// 상대전적: the regular season's, in the postseason too (user, 2026-10-31: it showed the series so far, 0승 0무 0패
			// before a series' first game; the series score is on the 포스트시즌 screen).
			List<LeagueGame> h2h = Store.Games(s.UserCode, s.Season, club: s.MyIndex)
				.Where(g => g.Confirmed && (g.Home == opp || g.Away == opp) && g.Game <= LeagueRules.Games).ToList();
			int w = 0, l = 0, d = 0;
			foreach (LeagueGame g in h2h)
			{
				bool hm = g.Home == s.MyIndex;
				int rf = hm ? g.HomeScore : g.AwayScore, ra = hm ? g.AwayScore : g.HomeScore;
				if (rf > ra) w++; else if (rf < ra) l++; else d++;
			}
			reply.next_record_against_wins.n = (byte)w;
			reply.next_record_against_loses.n = (byte)l;
			reply.next_record_against_draws.n = (byte)d;
		}
	}

}
