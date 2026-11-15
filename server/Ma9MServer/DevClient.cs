using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Ma9MPackets;
using NetworkLibrary;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// Dev: a headless game client that drives the running server over TCP the way the real client
	// does (same packet classes and connection code), for end-to-end checks without opening the
	// Unity client on the user's screen. Test accounts only.
	//   Ma9MServer.dll <data dir> --devclient training [autotest e-mail] [password]
	public static class DevClient
	{
		private const int TimeoutMs = 10000;

		private static Connection s_connection;

		private static int s_problems;

		public static int Run(string dataDir, string[] args)
		{
			int at = Array.IndexOf(args, "--devclient");
			string scenario = at + 1 < args.Length ? args[at + 1] : "training";
			string email = at + 2 < args.Length ? args[at + 2] : "autotest@ma9m.local";
			string password = at + 3 < args.Length ? args[at + 3] : "autotest";
			if (!email.StartsWith("autotest", StringComparison.OrdinalIgnoreCase))
			{
				Log("the dev client only logs in with the autotest accounts");
				return 2;
			}
			CardCatalog catalog = CardCatalog.Load(Path.Combine(dataDir, "tables"));
			try
			{
				Connect();
				// The 코칭스태프 menu shows only to builds that say they have its screens.
				Login(email, password, scenario == "coach" ? "devclient%" + CoachHandlers.ClientMark : string.Empty);
				switch (scenario)
				{
				case "training":
					Training(catalog);
					break;
				case "album":
					Album(catalog);
					break;
				case "league":
					League();
					break;
				case "exhibition":
					Exhibition(email, password);
					break;
				case "broadcast":
					Broadcast(catalog, at + 4 < args.Length && int.TryParse(args[at + 4], out int bg) ? bg : 1);
					break;
				case "coach":
					Coach(catalog);
					break;
				case "spblock":
					StarterBlock(catalog);
					break;
				case "topbar":
					TopBarPush();
					break;
				case "playersstat":
					PlayersStat();
					break;
				case "confirmbar":
					ConfirmBar(at + 4 < args.Length && args[at + 4] == "now");
					break;
				case "teamcolour":
					TeamColour(catalog);
					break;
				case "skiplist":
					SkipList();
					break;
				case "missiondot":
					MissionDot();
					break;
				case "oldseason":
					OldSeason();
					break;
				case "rosterticket":
					RosterTickets();
					break;
				case "lessoncoupon":
					LessonCoupon();
					break;
				case "advice":
					Advice(catalog);
					break;
				case "othercard":
					OtherCard(catalog);
					break;
				case "achievebundle":
					AchieveBundle();
					break;
				case "consultroom":
					ConsultRoom(catalog);
					break;
				case "lobbyrank":
					LobbyRank();
					break;
				case "famarket":
					FaMarketTest(catalog);
					break;
				default:
					Log($"unknown scenario {scenario}");
					return 2;
				}
			}
			catch (Exception e)
			{
				Check(false, $"{e.GetType().Name}: {e.Message}");
			}
			finally
			{
				s_connection?.Disconnect();
			}
			Log(s_problems == 0 ? "all ok" : $"{s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}

		// 트레이닝: coupon → open training packs → train a batter (slot, overwrite, wrong book) and a pitcher.
		private static void Training(CardCatalog catalog)
		{
			Send(new Ma9MPacket.PKS_CS_USE_COUPON { coupon_code = { str = "TESTTRAIN" } });
			Ma9MPacket.PKS_SC_USE_COUPON coupon = Await<Ma9MPacket.PKS_SC_USE_COUPON>();
			Log($"coupon TESTTRAIN: result {coupon.result.n} (0 given now, 2 used before)");
			Dictionary<int, int> items = Inventory();
			int packs = items.GetValueOrDefault(TrainingBooks.Pack);
			int booksBefore = items.Where(i => TrainingBooks.IsBook(i.Key)).Sum(i => i.Value);
			// TESTTRAIN gives 100 packs once per account, so after ten runs the account has none left.
			if (packs < 10)
			{
				Log($"skip opening packs: {packs} test pack(s) left (TESTTRAIN is once per account)");
			}
			else
			{
				Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM open = new Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM();
				open.index.n = TrainingBooks.Pack;
				open.amount.n = 10;
				Send(open);
				Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM opened = Await<Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM>();
				int[] got = opened.card_index.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
				Check(opened.result.n == 0 && opened.return_type.n == 1 && got.Length == 10 && got.All(TrainingBooks.IsBook) && opened.card_condition.array?.Length == 10,
					$"opened 10 packs: {string.Join(", ", got.Select(TrainingBooks.Name))}");
				items = Inventory();
				Check(items.GetValueOrDefault(TrainingBooks.Pack) == packs - 10 && items.Where(i => TrainingBooks.IsBook(i.Key)).Sum(i => i.Value) == booksBefore + 10,
					"inventory: 10 packs fewer, 10 books more");
			}

			List<PUICardInfoStat> roster = Roster();
			int batter = roster.Select(c => c.ui_info.index.n).First(i => catalog.Get(i)?.IsBatter == true);
			int pitcher = roster.Select(c => c.ui_info.index.n).First(i => catalog.Get(i)?.IsBatter == false);
			Log($"batter {batter} {catalog.Get(batter).Name}, pitcher {pitcher} {catalog.Get(pitcher).Name}");

			Ma9MPacket.PKS_SC_CARD_DETAIL_INFO detail = Detail(batter);
			// TRAINING POINT is charges left × one charge by ball (FullTp only before TP existed), so a full 3볼 card shows 6,000.
			int step = TrainingHandlers.TpStep(catalog.Get(batter).Ball), fullTp = TrainingStore.TpCharges * step;
			Check(detail.training_point.n > 0 && detail.training_point.n <= fullTp && detail.training_point.n % step == 0
				&& detail.training_cards.array?.Length == 3 && detail.training_effect.array?.Length == 3,
				$"card window: TP {detail.training_point.n}/{fullTp}, slots [{Slots(detail.training_cards, detail.training_effect)}]");

			Dictionary<int, int> books = BookList();
			int batterBook = books.Keys.FirstOrDefault(b => !TrainingBooks.ForPitcher(b));
			int pitcherBook = books.Keys.FirstOrDefault(TrainingBooks.ForPitcher);
			if (batterBook == 0)
			{
				// The test books came from TESTTRAIN's packs (once per account); once they are used up
				// only the card window above is checked.
				Log("skip training a card: no test batter books left");
				return;
			}
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING t1 = Train(batter, batterBook, 0);
			Check(t1.error_info.error_code.n == 0 && t1.result.n == 0 && t1.training_cards.array[0].n == batterBook && t1.training_result.n < 4
				&& t1.training_stat.sbytes.SequenceEqual(TrainingStore.Sum(Pairs(t1))),
				$"slot 1 ← {TrainingBooks.Name(batterBook)}: square {t1.training_result.n + 1} ({Signed(TrainingBooks.Value(batterBook, t1.training_result.n))}), training [{string.Join(" ", t1.training_stat.sbytes)}]");
			Dictionary<int, int> after = BookList();
			Check(after.GetValueOrDefault(batterBook) == books[batterBook] - 1, "the book was used up");

			if (pitcherBook != 0)
			{
				Ma9MPacket.PKS_SC_TRY_CARD_TRAINING wrong = Train(batter, pitcherBook, 1);
				Check(wrong.error_info.error_code.n != 0 && wrong.result.n != 0 && BookList().GetValueOrDefault(pitcherBook) == after[pitcherBook],
					$"pitcher book on a batter refused: {wrong.error_info.error_msg.str}");
			}

			int second = BookList().Keys.Where(b => !TrainingBooks.ForPitcher(b)).OrderBy(b => b == batterBook ? 1 : 0).FirstOrDefault();
			if (second != 0)
			{
				Ma9MPacket.PKS_SC_TRY_CARD_TRAINING t2 = Train(batter, second, 0);
				Check(t2.result.n == 0 && t2.training_cards.array[0].n == second && t2.training_stat.sbytes.SequenceEqual(TrainingStore.Sum(Pairs(t2))),
					$"slot 1 overwritten ← {TrainingBooks.Name(second)}: {Signed(TrainingBooks.Value(second, t2.training_result.n))}, training [{string.Join(" ", t2.training_stat.sbytes)}]");
				Ma9MPacket.PKS_SC_CARD_DETAIL_INFO again = Detail(batter);
				Check(Slots(again.training_cards, again.training_effect) == Slots(t2.training_cards, t2.training_effect) && again.card_info.training_stat.sbytes.SequenceEqual(t2.training_stat.sbytes),
					$"card window after training: slots [{Slots(again.training_cards, again.training_effect)}], training [{string.Join(" ", again.card_info.training_stat.sbytes)}]");
				PUICardInfoStat card = Roster().First(c => c.ui_info.index.n == batter);
				Check(card.training_stat.sbytes.SequenceEqual(t2.training_stat.sbytes), "roster shows the trained stats");
			}

			if (pitcherBook != 0)
			{
				Ma9MPacket.PKS_SC_TRY_CARD_TRAINING tp = Train(pitcher, pitcherBook, 2);
				Check(tp.result.n == 0 && tp.training_cards.array[2].n == pitcherBook && tp.training_stat.sbytes.SequenceEqual(TrainingStore.Sum(Pairs(tp))),
					$"pitcher slot 3 ← {TrainingBooks.Name(pitcherBook)}: {Signed(TrainingBooks.Value(pitcherBook, tp.training_result.n))}, training [{string.Join(" ", tp.training_stat.sbytes)}]");
			}
		}

		// 구단앨범: the page of an owned card's club and season lists it as seen and owned.
		private static void Album(CardCatalog catalog)
		{
			List<int> owned = Roster().Select(c => c.ui_info.index.n).ToList();
			CardInfo card = owned.Select(catalog.Get).First(c => c != null);
			List<int> page = catalog.All.Where(c => c.Year == card.Year && c.TeamCode == card.TeamCode).Select(c => c.Idx).ToList();
			Ma9MPacket.PKS_CS_ENTER_ALBUM request = new Ma9MPacket.PKS_CS_ENTER_ALBUM();
			request.year.n = (byte)(card.Year % 100);
			request.team.n = (byte)card.TeamCode;
			Send(request);
			Ma9MPacket.PKS_SC_ENTER_ALBUM reply = Await<Ma9MPacket.PKS_SC_ENTER_ALBUM>();
			int[] seen = reply.index.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			int[] mine = reply.possess_player.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			int[] ownedHere = page.Where(owned.Contains).ToArray();
			Check(reply.year.n == request.year.n && reply.team.n == request.team.n, $"album page {card.Year} team {card.TeamCode}: {page.Count} cards");
			Check(seen.All(page.Contains) && mine.All(page.Contains), "only cards of that page are sent");
			Check(ownedHere.All(seen.Contains) && ownedHere.OrderBy(i => i).SequenceEqual(mine.OrderBy(i => i)),
				$"owned cards there are seen and marked owned: {string.Join(", ", ownedHere.Select(i => catalog.Get(i).Name))}");
			Check(reply.total_count.n >= owned.Count, $"seen in all: {reply.total_count.n} (owned now {owned.Count})");
		}

		// 로비 순위 변동 (2026-11-12): the lobby's rank now and at the end of the previous day (LeagueHandlers.PreviousDayRank),
		// for a check against the stored games (scratch lobbyrank_check.py).
		private static void LobbyRank()
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
			Ma9MPacket.PKS_SC_ENTER_MAIN main = Await<Ma9MPacket.PKS_SC_ENTER_MAIN>();
			Check(main.team_ranking_now.n > 0 && main.team_ranking_befor.n > 0, $"lobby rank: now {main.team_ranking_now.n}, end of yesterday {main.team_ranking_befor.n}");
		}

		// 미확인 경기 / 스코어보드 / 리그 중계 (2026-10-19 fixes): the top bar comes with the unconfirmed
		// list, the record there is the one before those games, boards are 12 innings, broadcast scenes
		// are echoed, confirming pays the game's MP.
		// The top bar pushed when a league game is played (TopBar.PushLeague, 2026-11-09): the lobby's bar, then wait without
		// asking until the next slot; the bar must come by itself with 경기중 (in_broadcast) for a later game.
		private static void TopBarPush()
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
			Ma9MPacket.PKS_SC_GLOBAL_INFO first = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			int wait = first.in_broadcast.n == 1 ? first.remain_time_to_next_game.n + 20 * 60 : first.remain_time_to_next_game.n;
			Log($"lobby bar: game {first.cur_game_num.n}, in_broadcast {first.in_broadcast.n}, {first.remain_time_to_next_game.n} s; waiting up to {wait + 60} s without asking");
			Ma9MPacket.PKS_SC_GLOBAL_INFO pushed = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>((wait + 60) * 1000);
			int expected = first.in_broadcast.n == 1 ? first.cur_game_num.n + 1 : first.cur_game_num.n;
			Check(pushed.in_broadcast.n == 1 && pushed.cur_game_num.n >= expected && pushed.remain_time_to_next_game.n <= 300,
				$"bar pushed: game {pushed.cur_game_num.n}, in_broadcast {pushed.in_broadcast.n}, {pushed.remain_time_to_next_game.n} s of 경기중 left");
			Send(new Ma9MPacket.PKS_CS_GLOBAL_INFO());
			Ma9MPacket.PKS_SC_GLOBAL_INFO again = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			Check(again.cur_game_num.n == pushed.cur_game_num.n, $"asked again: game {again.cur_game_num.n}, in_broadcast {again.in_broadcast.n}");
		}

		// The results screen's last game (GameSession.ConfirmedLast, 2026-11-09: the bar showed the game's MP twice when an 업적
		// came with it). Its CONFIRM must be followed by nothing until the app's next packet (the stamp's GLOBAL_INFO request):
		// no 업적 bar, and no league push for 30 s. Without "now" the CONFIRM goes 5 s before the next league slot, so that
		// game is played during the count-up. An 업적 should be waiting (the test inserts one into its data copy).
		private static void ConfirmBar(bool now)
		{
			Send(new Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT());
			Ma9MPacket.PKS_SC_GLOBAL_INFO top = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			Stopwatch since = Stopwatch.StartNew();
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT list = Await<Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT>();
			LEAGUE_GAME_RESULT[] games = list.game_result.array ?? Array.Empty<LEAGUE_GAME_RESULT>();
			Log($"results screen: {games.Length} unconfirmed game(s), bar MP {top.my_point.n:N0}, next slot in {top.remain_time_to_next_game.n} s (in_broadcast {top.in_broadcast.n})");
			if (games.Length == 0)
			{
				Check(false, "no unconfirmed game to confirm");
				return;
			}
			void Confirm(byte game)
			{
				Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT c = new Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT();
				c.season.n = list.season.n;
				c.game_num.n = game;
				Send(c);
			}
			for (int i = 0; i < games.Length - 1; i++)
			{
				Confirm(games[i].game_num.n);
				Await<Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT>();
			}
			LEAGUE_GAME_RESULT last = games[games.Length - 1];
			if (!now)
			{
				// The bar's countdown, or its 경기중 seconds plus the rest of the 20-minute slot.
				int slot = top.in_broadcast.n == 1 ? top.remain_time_to_next_game.n + 15 * 60 : top.remain_time_to_next_game.n;
				int wait = slot - 5 - (int)since.Elapsed.TotalSeconds;
				Log($"waiting {Math.Max(0, wait)} s: the last game's CONFIRM goes 5 s before the next slot");
				if (wait > 0) Drain(wait * 1000);
			}
			Ma9MPacket.PKS_CS_GAME_SCOREBOARD board = new Ma9MPacket.PKS_CS_GAME_SCOREBOARD();
			board.game_num.n = last.game_num.n;
			Send(board);
			Await<Ma9MPacket.PKS_SC_GAME_SCOREBOARD>();
			Confirm(last.game_num.n);
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT done = Await<Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT>();
			Stopwatch quiet = Stopwatch.StartNew();
			List<string> early = Drain(now ? 5000 : 25000);
			Check(early.Count == 0, $"last game {last.game_num.n} confirmed (paid, MP {done.my_point.n:N0}); {(now ? 5 : 25)} s of count-up: " +
				(early.Count == 0 ? "nothing sent" : "sent " + string.Join(", ", early)));
			if (!now)
			{
				// The league push comes once the 30 s are over (the next 15-s tick).
				Ma9MPacket.PKS_SC_GLOBAL_INFO pushed = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>(60000);
				double at = quiet.Elapsed.TotalSeconds;
				Check(at >= 30 && pushed.in_broadcast.n == 1 && pushed.my_point.n == done.my_point.n,
					$"league push {at:F0} s after the CONFIRM: game {pushed.cur_game_num.n}, in_broadcast {pushed.in_broadcast.n}, MP {pushed.my_point.n:N0}");
			}
			// The stamp: the app counts the game onto its bar and asks for the bar (Common_TopMenu.Ma9MShowLeagueRecordAgain).
			Send(new Ma9MPacket.PKS_CS_GLOBAL_INFO());
			Ma9MPacket.PKS_SC_GLOBAL_INFO asked = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			Check(asked.my_point.n == done.my_point.n, $"bar asked at the stamp: MP {asked.my_point.n:N0} (= paid {done.my_point.n:N0})");
			Ma9MPacket.PKS_SC_GLOBAL_INFO paid = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>(5000);
			Check(paid.my_point.n > asked.my_point.n, $"업적 delivered after the stamp: bar MP {asked.my_point.n:N0} → {paid.my_point.n:N0} (+{paid.my_point.n - asked.my_point.n:N0})");
		}

		// Games left unconfirmed in an earlier season (LeagueHandlers.HasUnconfirmed, user 2026-11-10 23:2x: an empty results screen after the
		// season changed). On a copy where the test team's previous season still holds unconfirmed games and the new one none: the
		// login goes to the lobby (scene 0), the results list is empty, the lobby sends no "go to the results", and those games
		// are confirmed and paid (MP up). Says so when the copy is not in that state.
		private static void OldSeason()
		{
			Send(new Ma9MPacket.PKS_CS_GLOBAL_INFO());
			Send(new Ma9MPacket.PKS_CS_TRANSIT_SCENE());
			Ma9MPacket.PKS_SC_TRANSIT_SCENE scene = Await<Ma9MPacket.PKS_SC_TRANSIT_SCENE>();
			Send(new Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT());
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT list = Await<Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT>();
			int shown = list.game_result.array?.Length ?? 0;
			Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
			Await<Ma9MPacket.PKS_SC_ENTER_MAIN>();
			bool goes = true;
			try
			{
				Await<Ma9MPacket.PKS_SC_EXIST_UNCONFIRMED_GAME_RESULT>(1500);
			}
			catch (TimeoutException)
			{
				goes = false;
			}
			Check(shown > 0 ? scene.scene_num.n == 1 && goes : scene.scene_num.n != 1 && !goes,
				$"season {list.season.n}: login scene {scene.scene_num.n} (1 = results), results listed {shown}, lobby sends to results {goes}");
		}

		// 미션 빨간 점 (Missions.HasClaimable, user 2026-11-10 20:0x): the lobby's 미션 button (NEW byte 3) is lit exactly while a mission
		// of today shows 보상받기 (clear 1). A training pack bought (the 트레이닝 mission) makes one when none is waiting; after every
		// reward is taken the mark is off.
		private static void MissionDot()
		{
			byte Dot()
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
				return Await<Ma9MPacket.PKS_SC_ENTER_MAIN>().menu_show_new_icon.bytes[LobbyHandlers.MissionButton];
			}
			List<int> Waiting()
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_DAILY_MISSION());
				Ma9MPacket.PKS_SC_ENTER_DAILY_MISSION m = Await<Ma9MPacket.PKS_SC_ENTER_DAILY_MISSION>();
				byte[] clear = { m.clear_club.n, m.clear_batter.n, m.clear_pitcher.n, m.clear_competition.n, m.clear_social.n, m.clear_foster.n };
				return Enumerable.Range(0, 6).Where(k => clear[k] == 1).ToList();
			}
			byte first = Dot();
			List<int> waiting = Waiting();
			Check(first == (waiting.Count > 0 ? 1 : 0), $"lobby first: 미션 NEW {first}, 보상받기 {waiting.Count}개 ({string.Join(",", waiting)})");
			if (waiting.Count == 0)
			{
				Send(new Ma9MPacket.PKS_CS_BUY_ITEM_SHOP_NEW { ShopItemIndex = { n = 201 }, Amount = { n = 1 } });
				Await<Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK>();
				byte afterBuy = Dot();
				waiting = Waiting();
				Check(waiting.Count == 0 ? afterBuy == 0 : afterBuy == 1, $"after a training pack: 미션 NEW {afterBuy}, 보상받기 {waiting.Count}개 ({string.Join(",", waiting)})");
			}
			foreach (int category in waiting)
			{
				Send(new Ma9MPacket.PKS_CS_GET_DAILY_MISSION_REWARD { mission_category = { n = (byte)category } });
				Await<Ma9MPacket.PKS_SC_GET_DAILY_MISSION_REWARD>();
			}
			byte last = Dot();
			List<int> left = Waiting();
			Check(last == 0 && left.Count == 0, $"after taking {waiting.Count} reward(s): 미션 NEW {last}, 보상받기 {left.Count}개");
		}

		// 자동결과확인 confirms only the games the app listed (LeagueHandlers.ConfirmAll, 2026-11-10): skip with all but the last
		// two, and those two are still waiting (unpaid) on the next results screen; the record there counts the skipped ones.
		// Then a skip with no list (as before) confirms the rest. Needs 3+ unconfirmed games (a data copy).
		private static void SkipList()
		{
			// The standings order from the same report (LeagueState.Ranked, KboPctFrom): 13승 16패 (.448) above 12승 2무 15패
			// (.444) from the 11-11 season on; before it the draw counted half (both .448), the run difference decided.
			LeagueState Two(DateTime start) => new LeagueState
			{
				Start = start,
				Clubs =
				{
					new LeagueClub { Index = 0, Name = "무쇠 팔", W = 12, D = 2, L = 15, RunsFor = 120, RunsAgainst = 100 },
					new LeagueClub { Index = 1, Name = "나눔 올스타", W = 13, D = 0, L = 16, RunsFor = 100, RunsAgainst = 120 }
				}
			};
			string Order(LeagueState st) => string.Join(" > ", st.Ranked().Select(c => $"{c.Name} {c.W}-{c.D}-{c.L}"));
			LeagueState kbo = Two(LeagueState.KboPctFrom), old = Two(LeagueState.KboPctFrom.AddHours(-60));
			Check(kbo.Ranked()[0].Index == 1 && old.Ranked()[0].Index == 0, $"standings: from 11-11 {Order(kbo)}; the season before {Order(old)}");
			// 상대 전적 (user 11-10 20:0x): three clubs level at .500, a fourth above. A beat B 9-7, B beat C 10-6, C beat A 9-7 → among
			// the three A +2-2 = 0, B -2+4 = +2, C -4+2 = -2: B, A, C although C has the best run difference. Two level clubs: the
			// one that won more of their games. Without a 상대 전적 lead (or before 11-11) the run difference decides as before.
			LeagueState Level(DateTime start, int[,] wins) => new LeagueState
			{
				Start = start, HeadToHead = wins,
				Clubs =
				{
					new LeagueClub { Index = 0, Name = "A", W = 20, L = 20, RunsFor = 100, RunsAgainst = 100 },
					new LeagueClub { Index = 1, Name = "B", W = 10, L = 10, RunsFor = 90, RunsAgainst = 100 },
					new LeagueClub { Index = 2, Name = "C", W = 30, L = 30, D = 3, RunsFor = 130, RunsAgainst = 100 },
					new LeagueClub { Index = 3, Name = "D", W = 21, L = 19, RunsFor = 80, RunsAgainst = 100 }
				}
			};
			int[,] three = { { 0, 9, 7, 0 }, { 7, 0, 10, 0 }, { 9, 6, 0, 0 }, { 0, 0, 0, 0 } };
			string Names(LeagueState st) => string.Join("", st.Ranked().Select(c => c.Name));
			Check(Names(Level(LeagueState.KboPctFrom, three)) == "DBAC" && Names(Level(LeagueState.KboPctFrom, new int[4, 4])) == "DCAB"
				&& Names(Level(LeagueState.KboPctFrom.AddHours(-60), three)) == "DCAB",
				$"standings 상대 전적: {Names(Level(LeagueState.KboPctFrom, three))} (DBAC), none {Names(Level(LeagueState.KboPctFrom, new int[4, 4]))} (DCAB), before 11-11 {Names(Level(LeagueState.KboPctFrom.AddHours(-60), three))} (DCAB)");
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT List()
			{
				Send(new Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT());
				return Await<Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT>();
			}
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP Skip(byte season, IEnumerable<int> games)
			{
				Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP skip = new Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP();
				skip.season.n = season;
				skip.game_num_list.array = games.Select(g => new PByte((byte)g)).ToArray();
				Send(skip);
				return Await<Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP>();
			}
			// An empty array arrives as null.
			string Games(Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT l) =>
				(l.game_result.array?.Length ?? 0) == 0 ? "none" : $"{l.game_result.array.Length} ({l.game_result.array[0].game_num.n}-{l.game_result.array[^1].game_num.n})";
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT first = List();
			int[] all = first.game_result.array.Select(g => (int)g.game_num.n).ToArray();
			Log($"results screen: {Games(first)}, record {first.wins.n}-{first.loses.n}-{first.draws.n}");
			if (all.Length < 3)
			{
				Check(false, "needs 3+ unconfirmed games");
				return;
			}
			int[] shown = all.Take(all.Length - 2).ToArray(), left = all.Skip(all.Length - 2).ToArray();
			int[] rf = first.game_result.array.Select(g => (int)g.get_score.n).ToArray(), ra = first.game_result.array.Select(g => (int)g.lose_score.n).ToArray();
			int w = Enumerable.Range(0, shown.Length).Count(i => rf[i] > ra[i]), l = Enumerable.Range(0, shown.Length).Count(i => rf[i] < ra[i]);
			int d = shown.Length - w - l;
			long due = first.GetPoints.array.Take(shown.Length).Sum(p => (long)p.n);
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT before = first;
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP s1 = Skip(first.season.n, shown);
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT after = List();
			int[] rest = after.game_result.array.Select(g => (int)g.game_num.n).ToArray();
			Check(s1.result.n == 0 && rest.SequenceEqual(left), $"skip with {shown.Length} listed ({shown[0]}-{shown[^1]}): still waiting {Games(after)} (expected {string.Join(",", left)})");
			Check(after.wins.n == before.wins.n + w && after.loses.n == before.loses.n + l && after.draws.n == before.draws.n + d,
				$"record before the rest: {after.wins.n}-{after.loses.n}-{after.draws.n} (= {before.wins.n}-{before.loses.n}-{before.draws.n} + {w}-{l}-{d})");
			Log($"MP after the first skip {s1.my_point.n:N0} (the listed games' MP {due:N0}, plus any 업적/미션)");
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP s2 = Skip(after.season.n, Array.Empty<int>());
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT end = List();
			long rest_due = after.GetPoints.array.Sum(p => (long)p.n);
			Check(s2.result.n == 0 && (end.game_result.array?.Length ?? 0) == 0 && s2.my_point.n >= s1.my_point.n + rest_due,
				$"skip with no list: none left ({Games(end)}), MP {s1.my_point.n:N0} → {s2.my_point.n:N0} (the rest's MP {rest_due:N0})");
		}

		// 팀컬러 자동 등록 and the 빨간 점 over the wire (2026-11-09), on a copy whose test team has nothing registered (the test
		// clears its team_color_choice row): the lobby lights 팀관리 (NEW) with 특성 CP left (203); a 1군 save whose pitchers are
		// mostly one line's (the test team's own cards) registers by itself with a popup after its reply; the team colour screen
		// shows it; the lobby has no 202 (nothing left empty that could be filled).
		private static void TeamColour(CardCatalog catalog)
		{
			Ma9MPacket.PKS_SC_ENTER_MAIN Lobby()
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
				return Await<Ma9MPacket.PKS_SC_ENTER_MAIN>();
			}
			string Notices(Ma9MPacket.PKS_SC_ENTER_MAIN m) => $"NEW {m.menu_show_new_icon.bytes[LobbyHandlers.TeamManagementButton]}, show_menu {string.Join(",", m.show_menu.bytes.Where(b => b >= LobbyHandlers.NoticeBase))}";
			byte colourCode = LobbyHandlers.NoticeBase + LobbyHandlers.TeamColourMenu, traitCode = LobbyHandlers.NoticeBase + LobbyHandlers.TraitsMenu;
			Ma9MPacket.PKS_SC_ENTER_MAIN first = Lobby();
			bool anyFirst = first.show_menu.bytes.Contains(traitCode) || first.show_menu.bytes.Contains(colourCode);
			Check(first.menu_show_new_icon.bytes[LobbyHandlers.TeamManagementButton] == (anyFirst ? 1 : 0),
				$"lobby before: {Notices(first)} (팀관리 NEW = 203 또는 202가 있을 때)");
			List<PUICardInfoStat> roster = Roster();
			Send(new Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO());
			Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO info = Await<Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO>();
			int[] current = info.pitchers.array.Select(p => p.n).ToArray();
			// A group of the team's own non-레전드 pitchers that reaches a 투수진 entry worth something: 5 of one club-year
			// (단일 년도 팀 C), 6 of one line and decade (연대 단일 팀 B) or 6 of one year (단일 년도 올스타 B); then the old staff.
			List<CardInfo> owned = roster.Select(c => catalog.Get(c.ui_info.index.n)).Where(c => c != null && !c.IsBatter && c.Grade < 5).ToList();
			List<CardInfo> group = new[]
			{
				owned.GroupBy(c => (c.TeamCode, c.Year)).Where(g => g.Count() >= 5).Select(g => g.ToList()).FirstOrDefault(),
				owned.GroupBy(c => (TeamColors.Lineage(c.TeamCode), c.Year / 10)).Where(g => g.Count() >= 6).Select(g => g.ToList()).FirstOrDefault(),
				owned.GroupBy(c => c.Year).Where(g => g.Count() >= 6).Select(g => g.ToList()).FirstOrDefault()
			}.FirstOrDefault(g => g != null) ?? new List<CardInfo>();
			List<int> staff = group.Select(c => c.Idx).Take(11).ToList();
			staff.AddRange(current.Where(i => i != 0 && !staff.Contains(i)).Take(11 - staff.Count));
			Log($"pitchers: {Math.Min(11, group.Count)} of a group ({string.Join(", ", group.Take(3).Select(c => $"'{c.Year % 100:00} {c.Name}"))}…) + {staff.Count - Math.Min(11, group.Count)} of the old staff");
			Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER saved = ApplyPitchers(staff.ToArray());
			List<string> after = new List<string>();
			Ma9MPacket.PKS_SC_POPUP popup = null;
			Stopwatch watch = Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < 3000)
			{
				Packet.PKS_PACKET packet = s_connection.GetPacketFromQueue();
				if (packet == null)
				{
					System.Threading.Thread.Sleep(5);
					continue;
				}
				after.Add(packet.GetType().Name.Replace("PKS_SC_", string.Empty));
				if (packet is Ma9MPacket.PKS_SC_POPUP p) popup = p;
			}
			Check(saved.result.n == 0 && (saved.disappliedTeamColors.array?.Length ?? 0) == 0 && popup?.Messages.array?[0].str == "팀컬러 자동 등록",
				$"1군 저장(result {saved.result.n}) 뒤: {string.Join(", ", after)}; 팝업 \"{popup?.Messages.array?[0].str}\" {popup?.Messages.array?[1].str.Replace("\n", " | ")}");
			Send(new Ma9MPacket.PKS_CS_ENTER_TEAM_COLOR());
			Ma9MPacket.PKS_SC_ENTER_TEAM_COLOR screen = Await<Ma9MPacket.PKS_SC_ENTER_TEAM_COLOR>();
			int[] reg = { screen.team_color_c.n, screen.team_color_h.n, screen.team_color_p.n };
			Check(reg.Any(i => i != 0), $"팀컬러 화면: C {reg[0]}, H {reg[1]}, P {reg[2]} (쓸 수 있는 것 {screen.team_colors.array?.Count(x => x.n != 0) ?? 0}개)");
			Ma9MPacket.PKS_SC_ENTER_MAIN last = Lobby();
			Check(!last.show_menu.bytes.Contains(colourCode) && last.show_menu.bytes.Contains(traitCode) == first.show_menu.bytes.Contains(traitCode),
				$"lobby after: {Notices(last)} (202 꺼짐, 특성 203은 그대로)");
		}

		// Everything that arrives within ms (keep-alives answered), by packet name.
		private static List<string> Drain(int ms)
		{
			List<string> got = new List<string>();
			Stopwatch watch = Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < ms)
			{
				Packet.PKS_PACKET packet = s_connection.GetPacketFromQueue();
				if (packet == null)
				{
					System.Threading.Thread.Sleep(5);
					continue;
				}
				if (packet is Ma9MPacket.PKS_SYSTEM_KEEP_CONNECTION)
				{
					Send(new Ma9MPacket.PKS_SYSTEM_KEEP_CONNECTION());
					continue;
				}
				got.Add(packet.GetType().Name.Replace("PKS_SC_", string.Empty));
			}
			return got;
		}

		// 기록실 > 선수 육성정보: the 재계약 column (RecordHandlers.PlayersStat, 2026-11-09) follows a renewal, in the roster's order.
		private static void PlayersStat()
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_PLAYERS_STAT());
			byte[] before = Await<Ma9MPacket.PKS_SC_ENTER_PLAYERS_STAT>().contract_count.array.Select(p => p.n).ToArray();
			List<PUICardInfoStat> roster = Roster();
			Check(before.Length == roster.Count, $"재계약 column: {before.Length} values for {roster.Count} cards, {before.Count(b => b > 0)} renewed");
			int at = Array.FindIndex(before, b => b < 250);
			int card = roster[at].ui_info.index.n;
			Send(new Ma9MPacket.PKS_CS_EXTEND_CONTRACT { target_card = { n = card }, bonus_stat = { n = 0 } });
			Ma9MPacket.PKS_SC_EXTEND_CONTRACT extended = Await<Ma9MPacket.PKS_SC_EXTEND_CONTRACT>();
			Send(new Ma9MPacket.PKS_CS_ENTER_PLAYERS_STAT());
			byte[] after = Await<Ma9MPacket.PKS_SC_ENTER_PLAYERS_STAT>().contract_count.array.Select(p => p.n).ToArray();
			Check(extended.result.n == 0 && after[at] == before[at] + 1 && Enumerable.Range(0, after.Length).All(i => i == at || after[i] == before[i]),
				$"renewed card {card} (row {at + 1}): 재계약 {before[at]} → {after[at]}, the other rows unchanged");
		}

		private static void League()
		{
			Send(new Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT());
			Ma9MPacket.PKS_SC_GLOBAL_INFO top = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT list = Await<Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT>();
			LEAGUE_GAME_RESULT[] games = list.game_result.array ?? Array.Empty<LEAGUE_GAME_RESULT>();
			Check(top.league_grade.n != 255 && top.team_name.str?.Length > 0, $"top bar with the unconfirmed screen: {top.team_name.str}, grade {top.league_grade.n}, rank {top.league_rank.n}, {top.league_win.n}-{top.league_lose.n}-{top.league_draw.n}, MP {top.my_point.n:N0}");
			Check(list.wins.n == top.league_win.n && list.loses.n == top.league_lose.n && list.draws.n == top.league_draw.n,
				$"{games.Length} unconfirmed game(s); the record shown is the one before them ({list.wins.n}-{list.loses.n}-{list.draws.n})");
			if (games.Length == 0)
			{
				Log("no unconfirmed game to look at");
				return;
			}
			LEAGUE_GAME_RESULT first = games[0];
			Ma9MPacket.PKS_CS_GAME_SCOREBOARD board = new Ma9MPacket.PKS_CS_GAME_SCOREBOARD();
			board.game_num.n = first.game_num.n;
			Send(board);
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD b = Await<Ma9MPacket.PKS_SC_GAME_SCOREBOARD>();
			string Line(PArray<PByte> a) => string.Join("", (a.array ?? Array.Empty<PByte>()).Select(x => x.n == 255 ? "-" : x.n.ToString()));
			Check(b.home_score.array?.Length == 12 && b.away_score.array?.Length == 12, $"board of game {first.game_num.n}: {b.away_team_name.str} {Line(b.away_score)} / {b.home_team_name.str} {Line(b.home_score)}, MP {b.get_point.n:N0}");
			Ma9MPacket.PKS_CS_PAST_SCENE scene = new Ma9MPacket.PKS_CS_PAST_SCENE();
			scene.scene_num.n = 3;
			Send(scene);
			Check(Await<Ma9MPacket.PKS_SC_PAST_SCENE>().scene_num.n == 3, "league broadcast scene echoed");
			int mpBefore = top.my_point.n;
			Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT confirm = new Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT();
			confirm.season.n = list.season.n;
			confirm.game_num.n = first.game_num.n;
			Send(confirm);
			Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT done = Await<Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT>();
			Log($"confirmed game {first.game_num.n}: MP {mpBefore:N0} → {done.my_point.n:N0} (game MP {first.get_point.n:N0}; games stored before 2026-10-19 were paid when played)");
		}

		// A league game's broadcast as the client gets it (engine session, 2026-10-31): both staffs with the season lines
		// their pitcher boxes show ("--devclient broadcast <email> <password> <game>").
		private static void Broadcast(CardCatalog catalog, int game)
		{
			Ma9MPacket.PKS_CS_ENTER_BROADCAST p = new Ma9MPacket.PKS_CS_ENTER_BROADCAST();
			p.game_num.n = (byte)game;
			p.result_num.n = 255;
			Send(p);
			Ma9MPacket.PKS_SC_ENTER_BROADCAST r = Await<Ma9MPacket.PKS_SC_ENTER_BROADCAST>();
			Log($"game {r.game_num.n}: {r.away_team_name.str} at {r.home_team_name.str}");
			void Staff(string side, PUICardInfo[] cards, BROADCAST_PITCHER_RECORD[] recs)
			{
				for (int i = 0; i < (cards?.Length ?? 0); i++)
				{
					BROADCAST_PITCHER_RECORD x = recs != null && i < recs.Length ? recs[i] : null;
					string name = catalog.Get(cards[i].index.n)?.Name ?? "-";
					Log($"  {side} {i,2} {name,-8} " + (x == null ? "no record" : $"G {x.games.n} W-L {x.wins.n}-{x.loses.n} SV {x.saves.n} HLD {x.holds.n} IP {x.innings_by3.n / 3}.{x.innings_by3.n % 3} K {x.strike_outs.n} BB+HBP {x.base_on_balls.n} ERA {x.era.f:F2}"));
				}
			}
			Staff("away", r.away_pitchers.array, r.away_pitchers_record.array);
			Staff("home", r.home_pitchers.array, r.home_pitchers_record.array);
			Check(r.home_pitchers_record.array?.Any(x => x.games.n > 0) == true || r.away_pitchers_record.array?.Any(x => x.games.n > 0) == true, "pitcher boxes carry season lines");
		}

		// 시범경기 (user, 2026-10-21): try → start → leave without watching → log back in. The login goes back
		// to the unfinished exhibition (TRANSIT_SCENE 3), its broadcast joins at the time since the start, and
		// the last scene gives the result; after that the login goes to the lobby again.
		private static void Exhibition(string email, string password)
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_EXHIBITION_GAME());
			Ma9MPacket.PKS_SC_ENTER_EXHIBITION_GAME enter = Await<Ma9MPacket.PKS_SC_ENTER_EXHIBITION_GAME>();
			if (enter.remain_try_count.n == 0)
			{
				Log("no exhibition tries left today");
				return;
			}
			Ma9MPacket.PKS_CS_TRY_EXHIBITION_GAME tryGame = new Ma9MPacket.PKS_CS_TRY_EXHIBITION_GAME();
			tryGame.grade.n = 0;
			Send(tryGame);
			Ma9MPacket.PKS_SC_TRY_EXHIBITION_GAME tried = Await<Ma9MPacket.PKS_SC_TRY_EXHIBITION_GAME>();
			Check(tried.result.n == 0, $"exhibition against {tried.team_name.str} ({tried.ball.n} balls)");
			Send(new Ma9MPacket.PKS_CS_START_EXHIBITION_GAME());
			Check(Await<Ma9MPacket.PKS_SC_START_EXHIBITION_GAME>().result.n == 0, "exhibition played");
			s_connection.Disconnect();
			System.Threading.Thread.Sleep(1500);
			Connect();
			Login(email, password);
			Send(new Ma9MPacket.PKS_CS_TRANSIT_SCENE());
			Check(Await<Ma9MPacket.PKS_SC_TRANSIT_SCENE>().scene_num.n == 3, "logging back in goes to the unfinished exhibition (scene 3)");
			Send(new Ma9MPacket.PKS_CS_ENTER_EXHIBITION_BROADCAST());
			Ma9MPacket.PKS_SC_ENTER_EXHIBITION_BROADCAST b = Await<Ma9MPacket.PKS_SC_ENTER_EXHIBITION_BROADCAST>();
			int scenes = b.buff.bytes?.Length > 0 ? b.buff.bytes[0] : 0;
			Check(scenes > 10 && b.scene_time.n >= 1, $"broadcast rejoined {b.scene_time.n} s in ({scenes} scenes)");
			Ma9MPacket.PKS_CS_EXHIBITION_PAST_SCENE last = new Ma9MPacket.PKS_CS_EXHIBITION_PAST_SCENE();
			last.scene_num.n = (short)(scenes - 1);
			Send(last);
			Ma9MPacket.PKS_SC_EXHIBITION_GAME_RESULT result = Await<Ma9MPacket.PKS_SC_EXHIBITION_GAME_RESULT>();
			Check(result.earned_point.n > 0, $"result: +{result.earned_point.n:N0} MP, prize card {result.prize_card.n}");
			// SKIP shows the game's box score next (game 0 = the exhibition; client BroadcastNet.Ma9MSkipToBoard).
			Ma9MPacket.PKS_CS_GAME_SCOREBOARD board = new Ma9MPacket.PKS_CS_GAME_SCOREBOARD();
			board.game_num.n = 0;
			Send(board);
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD box = Await<Ma9MPacket.PKS_SC_GAME_SCOREBOARD>();
			int Runs(PByte[] line) => line == null ? -1 : line.Where(x => x.n != 255).Sum(x => x.n);
			Check(box.home_team_name.str?.Length > 0 && box.away_team_name.str == tried.team_name.str && box.home_score.array?.Length == 12
				&& box.get_point.n == result.earned_point.n && box.home_best.index.n != 0 && box.away_worst.index.n != 0,
				$"scoreboard: {box.away_team_name.str} {Runs(box.away_score.array)} - {Runs(box.home_score.array)} {box.home_team_name.str}, H {box.away_hit.n}-{box.home_hit.n}, "
				+ $"E {box.away_error.n}-{box.home_error.n}, W {box.win_pitcher_index.n} L {box.lose_pitcher_index.n}, MP {box.get_point.n:N0}");
			// The top bar: 경기중 for the first 5 minutes of the latest league game, else the countdown.
			Send(new Ma9MPacket.PKS_CS_GLOBAL_INFO());
			Ma9MPacket.PKS_SC_GLOBAL_INFO top = Await<Ma9MPacket.PKS_SC_GLOBAL_INFO>();
			Log($"top bar: in_broadcast {top.in_broadcast.n}, {top.remain_time_to_next_game.n} s, type {top.cur_game_type.n} day {top.cur_game_date.n} game {top.cur_game_num.n}");
			Send(new Ma9MPacket.PKS_CS_TRANSIT_SCENE());
			Check(Await<Ma9MPacket.PKS_SC_TRANSIT_SCENE>().scene_num.n != 3, "after the result the login no longer goes to it");
		}

		// 코칭스태프 (a test server with MA9M_COACH=1 and MA9M_COACH_TEST=1, 2026-11-04): the lobby menu, the screen, a 세트팩,
		// registering the five, the set bonus on a card, the 치어리더's gift, the 면담실 and a 면담 when someone has come.
		private static void Coach(CardCatalog catalog)
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_MAIN());
			Ma9MPacket.PKS_SC_ENTER_MAIN main = Await<Ma9MPacket.PKS_SC_ENTER_MAIN>();
			Check(main.enabled_menu.bytes?.Contains(CoachHandlers.Menu) == true, $"lobby menu {CoachHandlers.Menu} (코칭스태프) shown");

			Send(new Ma9MPacket.PKS_CS_ENTER_COACH_MAIN());
			Ma9MPacket.PKS_SC_ENTER_COACH_MAIN first = Await<Ma9MPacket.PKS_SC_ENTER_COACH_MAIN>();
			Check(first.error_info.error_code.n == 0 && first.ApplyCoachList.array?.Length == 5 && first.MaxSP.n == CoachCatalog.MaxSp && first.SP.n >= 500,
				$"코칭스태프: SP {first.SP.n}/{first.MaxSP.n}, staff [{string.Join(", ", first.ApplyCoachList.array?.Select(p => p.n) ?? Array.Empty<int>())}], {first.PossessCoachList.array?.Length ?? 0} more");
			if (first.ApplyCoachList.array?.Length == 5 && first.ApplyCoachList.array[0].n == 0)
			{
				// No 수석코치 yet: the refusal comes as the app's ErrorPopup reads it (AI testers 3·4, 11-10: it threw JsonException).
				Send(new Ma9MPacket.PKS_CS_ENTER_CONSULTING_ROOM());
				Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM shut = Await<Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM>();
				string text = null;
				try
				{
					text = LitJson.JsonMapper.ToObject<ErrorInfoObject>(shut.error.error_msg.str).RawMessage;
				}
				catch (Exception ex)
				{
					text = $"(JSON: {ex.Message})";
				}
				Check(shut.error.error_code.n == 1 && text == "수석코치를 등록하지 않으면 면담실을 이용하실 수 없습니다.", $"면담실 without a 수석코치: \"{text}\"");
			}
			Dictionary<int, int> items = Inventory();
			Check(items.GetValueOrDefault(CoachCatalog.Pack) >= 10 && items.GetValueOrDefault(CoachCatalog.SetPack) >= 3, $"test packs: {items.GetValueOrDefault(CoachCatalog.Pack)} + {items.GetValueOrDefault(CoachCatalog.SetPack)} sets");

			Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM open = new Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM();
			open.index.n = CoachCatalog.SetPack;
			open.amount.n = 1;
			Send(open);
			Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM opened = Await<Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM>();
			int[] got = opened.card_index.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			Check(opened.result.n == 0 && got.Length == 5 && got.All(CoachCatalog.IsCoach) && got.Select(CoachCatalog.Role).SequenceEqual(Enumerable.Range(0, 5))
				&& got.Select(CoachCatalog.Club).Distinct().Count() == 1 && got.Select(CoachCatalog.Grade).Distinct().Count() == 1,
				$"세트팩: {string.Join(", ", got.Select(i => $"{i} {CoachCatalog.GradeNames[CoachCatalog.Grade(i)]} {CoachCatalog.Name(i)}"))}");
			if (got.Length != 5) return;

			Ma9MPacket.PKS_CS_APPLY_COACH_CARD apply = new Ma9MPacket.PKS_CS_APPLY_COACH_CARD();
			apply.ApplyCoachList.array = got.Select(i => new PInteger { n = i }).ToArray();
			Send(apply);
			Ma9MPacket.PKS_SC_APPLY_COACH_CARD applied = Await<Ma9MPacket.PKS_SC_APPLY_COACH_CARD>();
			int club = CoachCatalog.Club(got[0]), bonus = CoachCatalog.SetBonus[CoachCatalog.Grade(got[0])];
			Check(applied.error_info.error_code.n == 0 && applied.BonusStat.n == bonus && (bonus == 0 || applied.BonusTeam.n == club), $"등록: set bonus +{applied.BonusStat.n} for team {applied.BonusTeam.n}");
			Ma9MPacket.PKS_CS_APPLY_COACH_CARD wrong = new Ma9MPacket.PKS_CS_APPLY_COACH_CARD();
			wrong.ApplyCoachList.array = new[] { got[1], got[0], got[2], got[3], got[4] }.Select(i => new PInteger { n = i }).ToArray();
			Send(wrong);
			Check(Await<Ma9MPacket.PKS_SC_APPLY_COACH_CARD>().error_info.error_code.n != 0, "a coach in another role's slot refused");

			Send(new Ma9MPacket.PKS_CS_ENTER_COACH_MAIN());
			Ma9MPacket.PKS_SC_ENTER_COACH_MAIN second = Await<Ma9MPacket.PKS_SC_ENTER_COACH_MAIN>();
			Check(second.ApplyCoachList.array.Select(p => p.n).SequenceEqual(got) && second.BonusStat.n == bonus, $"screen again: the five registered, bonus +{second.BonusStat.n}, 선물받기 {second.todayGiftAble.n}");
			if (second.todayGiftAble.n == 1)
			{
				Send(new Ma9MPacket.PKS_CS_GET_TODAY_CLITEM());
				Ma9MPacket.PKS_SC_GET_TODAY_CLITEM gift = Await<Ma9MPacket.PKS_SC_GET_TODAY_CLITEM>();
				Send(new Ma9MPacket.PKS_CS_GET_TODAY_CLITEM());
				Ma9MPacket.PKS_SC_GET_TODAY_CLITEM again = Await<Ma9MPacket.PKS_SC_GET_TODAY_CLITEM>();
				Check(gift.error.error_code.n == 0 && again.error.error_code.n != 0, "치어리더 선물 once");
			}

			PUICardInfoStat mine = Roster().FirstOrDefault(c => catalog.Get(c.ui_info.index.n)?.TeamCode == club);
			if (mine != null)
			{
				sbyte[] shown = mine.bingogame_stat.sbytes ?? new sbyte[6];
				Check(shown.Length == 6 && shown.Min() >= bonus, $"{catalog.Get(mine.ui_info.index.n).Name}: 코칭스태프 육성 [{string.Join(" ", shown)}] in the roster");
			}

			Send(new Ma9MPacket.PKS_CS_ENTER_CONSULTING_ROOM());
			Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM room = Await<Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM>();
			PUICardInfo[] visitors = room.PlayerCard.array ?? Array.Empty<PUICardInfo>();
			Check(room.error.error_code.n == 0 && room.HDCoachIndex.n == got[0] && room.RemainTicks.n > 0 && room.RemainTicks.n <= TimeSpan.FromHours(12).Ticks
				&& visitors.Where((v, k) => room.PlayerCardLabels.array[k].n == 0).All(v => catalog.Get(v.index.n)?.TeamCode == club && v.condition.n >= 3),
				$"면담실: {visitors.Length} visitor(s) [{string.Join(", ", visitors.Select((v, k) => $"{catalog.Get(v.index.n)?.Name} {v.condition.n}/{room.PlayerCardLabels.array[k].n}"))}] (condition/label; waiting ones of the club, 나쁨 or worse{(CoachCatalog.TestMode ? ", made so on a test server if too few" : string.Empty)}), line {room.HDCoachString.n}, {TimeSpan.FromTicks(room.RemainTicks.n).TotalHours:F1} h left");
			PUICardInfo waiting = visitors.Where((v, k) => room.PlayerCardLabels.array[k].n == 0).FirstOrDefault();
			if (waiting != null)
			{
				Ma9MPacket.PKS_CS_CONSULTING_START start = new Ma9MPacket.PKS_CS_CONSULTING_START();
				start.CardIndex.n = waiting.index.n;
				Send(start);
				Ma9MPacket.PKS_SC_CONSULTING_START started = Await<Ma9MPacket.PKS_SC_CONSULTING_START>();
				Ma9MPacket.PKS_CS_CONSULTING_END end = new Ma9MPacket.PKS_CS_CONSULTING_END();
				end.CardIndex.n = waiting.index.n;
				Send(end);
				Ma9MPacket.PKS_SC_CONSULTING_END ended = Await<Ma9MPacket.PKS_SC_CONSULTING_END>();
				Check(started.error.error_code.n == 0 && started.ConsultintIndex.n > 0 && started.RemainTime.n > 0 && ended.error.error_code.n == 0 && ended.EffectList.str.StartsWith("CONDITION:"),
					$"면담 {catalog.Get(waiting.index.n)?.Name}: dialogue {started.ConsultintIndex.n}, SP {started.my_sp.n}, effect {ended.EffectList.str}");
			}

			// 코칭스태프 조합 (전력강화 slot 5, user 11-05): four cards of one grade; the staff stays out of the list, copies show
			// each time. Packs are opened until some grade has four free cards (copies may come: user "중복해서 획득").
			Send(new Ma9MPacket.PKS_CS_ENTER_COACH_MIX());
			Ma9MPacket.PKS_SC_ENTER_COACH_MIX mixRoom = Await<Ma9MPacket.PKS_SC_ENTER_COACH_MIX>();
			int[] listed = mixRoom.CoachCardList.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			for (int tries = 0; tries < 6 && !listed.GroupBy(CoachCatalog.Grade).Any(g => g.Count() >= CoachCatalog.MixCardsOf(g.Key)); tries++)
			{
				Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM more = new Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM();
				more.index.n = CoachCatalog.Pack;
				more.amount.n = 4;
				Send(more);
				Await<Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM>();
				Send(new Ma9MPacket.PKS_CS_ENTER_COACH_MIX());
				mixRoom = Await<Ma9MPacket.PKS_SC_ENTER_COACH_MIX>();
				listed = mixRoom.CoachCardList.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			}
			Check(mixRoom.error_info.error_code.n == 0 && mixRoom.DeckCoachCount.n == 5 && mixRoom.CoachMax.n == CoachCatalog.MaxCoaches,
				$"조합 화면: {listed.Length} cards ({listed.Length - listed.Distinct().Count()} copies) + {mixRoom.DeckCoachCount.n} on the staff / {mixRoom.CoachMax.n}");
			int[] fodder = listed.GroupBy(CoachCatalog.Grade).Where(g => g.Count() >= CoachCatalog.MixCardsOf(g.Key)).Select(g => g.Take(CoachCatalog.MixCardsOf(g.Key)).ToArray()).FirstOrDefault() ?? Array.Empty<int>();
			Ma9MPacket.PKS_CS_PROC_COACH_MIX mix = new Ma9MPacket.PKS_CS_PROC_COACH_MIX();
			mix.CoachCardList.array = fodder.Select(i => new PInteger { n = i }).ToArray();
			Send(mix);
			Ma9MPacket.PKS_SC_PROC_COACH_MIX mixed = Await<Ma9MPacket.PKS_SC_PROC_COACH_MIX>();
			Send(new Ma9MPacket.PKS_CS_ENTER_COACH_MIX());
			int[] afterMix = Await<Ma9MPacket.PKS_SC_ENTER_COACH_MIX>().CoachCardList.array?.Select(p => p.n).ToArray() ?? Array.Empty<int>();
			int grade0 = fodder.Length > 0 ? CoachCatalog.Grade(fodder[0]) : 0, grade1 = CoachCatalog.Grade(mixed.NewCoachCard.n);
			Check(fodder.Length == CoachCatalog.MixCardsOf(grade0) && mixed.error_info.error_code.n == 0 && afterMix.Length == listed.Length - fodder.Length + 1 && afterMix.Contains(mixed.NewCoachCard.n)
				&& grade1 == Math.Min(grade0 + 1, CoachCatalog.Elite),
				$"조합: {string.Join(" + ", fodder.Select(i => $"{CoachCatalog.GradeNames[CoachCatalog.Grade(i)]} {CoachCatalog.Name(i)}"))} → {CoachCatalog.GradeNames[grade1]} {CoachCatalog.Name(mixed.NewCoachCard.n)}");
		}

		// 로스터 확장권 여러 장 (systems session, 2026-11-11): three at once (arg1 "3"), then one the way the 1.0.43 app asks (arg1
		// empty). Needs four tickets on the team (a copy of the data: put them in team_item first).
		private static void RosterTickets()
		{
			int before = Inventory().GetValueOrDefault(ShopHandlers.RosterTicket);
			Log($"로스터 확장권 {before}장");
			Send(new Ma9MPacket.PKS_CS_USE_ITEM { item_index = { n = ShopHandlers.RosterTicket }, arg1 = { str = "3" }, arg2 = { str = string.Empty }, arg3 = { str = string.Empty } });
			Ma9MPacket.PKS_SC_USE_ITEM three = Await<Ma9MPacket.PKS_SC_USE_ITEM>();
			int used = int.TryParse(three.arg1.str, out int u) ? u : -1;
			int from = int.TryParse(three.arg2.str, out int f) ? f : -1, to = int.TryParse(three.arg3.str, out int t) ? t : -1;
			int left = Inventory().GetValueOrDefault(ShopHandlers.RosterTicket);
			Check(three.result.n == 0 && used == Math.Min(3, before) && to - from == 20 * used && left == before - used,
				$"3장 요청: result {three.result.n}, {used}장 사용, 보관함 {from} → {to}, 남은 확장권 {left}");
			Send(new Ma9MPacket.PKS_CS_USE_ITEM { item_index = { n = ShopHandlers.RosterTicket }, arg1 = { str = string.Empty }, arg2 = { str = string.Empty }, arg3 = { str = string.Empty } });
			Ma9MPacket.PKS_SC_USE_ITEM one = Await<Ma9MPacket.PKS_SC_USE_ITEM>();
			int left2 = Inventory().GetValueOrDefault(ShopHandlers.RosterTicket);
			Check(one.result.n == 0 && one.arg1.str == "1" && one.arg2.str == to.ToString() && left2 == left - 1,
				$"옛 앱처럼 1장: result {one.result.n}, arg {one.arg1.str}/{one.arg2.str}/{one.arg3.str}, 남은 확장권 {left2}");
		}

		// 면담실 second page (2026-11-12, user 11-10 결정 2): the 1군 cards whose 면담 effect from an earlier sitting still runs, with
		// the time left (the copy's Prep adds such rows for the logged-in team).
		private static void ConsultRoom(CardCatalog catalog)
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_CONSULTING_ROOM());
			Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM room = Await<Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM>();
			PUICardInfo[] effects = room.EffectCard.array ?? Array.Empty<PUICardInfo>();
			PLong[] ticks = room.EffectRemainTicks.array ?? Array.Empty<PLong>();
			Log($"면담실: error {room.error.error_code.n}, visitors {room.PlayerCard.array?.Length ?? 0}, effects {string.Join(", ", effects.Select((c, i) => $"{catalog.Get(c.index.n)?.Name} {(i < ticks.Length ? TimeSpan.FromTicks(ticks[i].n).TotalHours : -1):F1}h left, 컨디션 {c.condition.n}"))}");
			Check(room.error.error_code.n == 0 && effects.Length > 0 && effects.Length == ticks.Length && ticks.All(t => t.n > 0) && effects.Length <= 5,
				$"second page: {effects.Length} card(s) with time left");
		}

		// 업적 at once (2026-11-12, user 11:5x 3번): the copy's Prep marks some cleared 업적 undelivered; once no league result waits,
		// the next packet brings one "도전과제 n개 달성" list instead of a 도전과제완료 box each.
		private static void AchieveBundle()
		{
			Send(new Ma9MPacket.PKS_CS_UNCONFIRMED_GAME_RESULT());
			Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT waiting = Await<Ma9MPacket.PKS_SC_UNCONFIRMED_GAME_RESULT>();
			if ((waiting.game_result.array?.Length ?? 0) > 0)
			{
				Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP skip = new Ma9MPacket.PKS_CS_CONFIRM_GAME_RESULT_SKIP();
				skip.season.n = waiting.season.n;
				skip.game_num_list.array = Array.Empty<PByte>();
				Send(skip);
				Await<Ma9MPacket.PKS_SC_CONFIRM_GAME_RESULT_SKIP>();
				Log($"confirmed {waiting.game_result.array.Length} waiting game(s)");
			}
			int boxes = 0;
			Ma9MPacket.PKS_SC_POPUP list = null;
			Send(new Ma9MPacket.PKS_CS_ENTER_TEAM_MISSION());
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < 4000 && list == null)
			{
				Packet.PKS_PACKET p = s_connection.GetPacketFromQueue();
				if (p == null) { System.Threading.Thread.Sleep(5); continue; }
				if (p is Ma9MPacket.PKS_SC_GET_TEAM_MISSION_REWARD) boxes++;
				if (p is Ma9MPacket.PKS_SC_POPUP pop && pop.Messages.array?.Length >= 2 && pop.Messages.array[0].str.StartsWith("도전과제")) list = pop;
			}
			string[] lines = list?.Messages.array[1].str.Split('\n') ?? Array.Empty<string>();
			Log($"popup: {list?.Messages.array[0].str} | {string.Join(" | ", lines)}");
			Check(list != null && boxes == 0 && lines.Length <= 5 && lines.All(l => PopupText.Px(l) <= PopupText.Limit),
				$"one list popup, {boxes} 도전과제완료 box(es), {lines.Length} line(s), widest {(lines.Length == 0 ? 0 : lines.Max(PopupText.Px)):F0} px / {PopupText.Limit:F0}");
		}

		// Another club's card (2026-11-12, user: "타팀 선수의 wrc+와 war값이 보이지 않는다"): the 수훈선수 leaders that are not the
		// team's own, opened as the scoreboard does (CARD_DETAIL_INFO_OF_OTHERS, every club searched) — WAR and wRC+ like an own card.
		private static void OtherCard(CardCatalog catalog)
		{
			// 팀별 선수 순위 (every club's leaders, user_num = club) — the 수훈선수 board may hold only the team's own cards.
			Send(new Ma9MPacket.PKS_CS_ENTER_TEAM_PLAYER_RANK());
			Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK rank = Await<Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK>();
			PUICardInfoMVP[] rows = rank.players.array ?? Array.Empty<PUICardInfoMVP>();
			List<int> others = rows.Where(r => r.user_num.n != rank.my_user_num.n).Select(r => r.ui_info.index.n).Where(i => i != 0).Distinct().ToList();
			Check(others.Count > 0, $"팀별 선수 순위: {rows.Length} rows, {others.Count} cards of other clubs");
			int batters = 0, pitchers = 0;
			foreach (int idx in others.Take(8))
			{
				Send(new Ma9MPacket.PKS_CS_CARD_DETAIL_INFO_OF_OTHERS { user_num = { n = 255 }, index = { n = idx } });
				Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_OF_OTHERS d = Await<Ma9MPacket.PKS_SC_CARD_DETAIL_INFO_OF_OTHERS>();
				CardInfo c = catalog.Get(idx);
				bool pitcher = c != null && !c.IsBatter;
				int played = pitcher ? d.pitcher_record.innings_by3.n : d.batter_record.PA.n;
				Log($"  {c?.Year} {c?.Name} ({(pitcher ? "투수" : "타자")}): {(pitcher ? "아웃 " : "타석 ")}{played}, WAR {d.war.f:F2}, wRC+ {d.wrc_plus.n}");
				if (played > 0 && pitcher) pitchers += d.war.f != 0 ? 1 : 0;
				if (played > 0 && !pitcher) batters += d.war.f != 0 && d.wrc_plus.n != 0 ? 1 : 0;
			}
			Check(batters > 0 && pitchers > 0, $"other clubs' cards with WAR (and wRC+ for batters): {batters} batter(s), {pitchers} pitcher(s)");
		}

		// LESSON2026 (2026-11-11; the user allowed it for the AI tester accounts): 1억 MP and ten of each 특강 item, once per account.
		// On a copy of the data only, with an AI tester account (autotest3..6).
		private static void LessonCoupon()
		{
			Dictionary<int, int> before = Inventory();
			Send(new Ma9MPacket.PKS_CS_USE_COUPON { coupon_code = { str = "LESSON2026" } });
			Ma9MPacket.PKS_SC_USE_COUPON first = Await<Ma9MPacket.PKS_SC_USE_COUPON>();
			Dictionary<int, int> after = Inventory();
			int Gain(int item) => after.GetValueOrDefault(item) - before.GetValueOrDefault(item);
			// 2.5억 MP and 40 of each since 11-14.
			Check(first.result.n == 0 && Gain(LessonHandlers.Lucky) == 40 && Gain(LessonHandlers.Instant) == 40 && Gain(LessonHandlers.Reset) == 40,
				$"LESSON2026: result {first.result.n}, 행운권 +{Gain(LessonHandlers.Lucky)}, 즉시 완료권 +{Gain(LessonHandlers.Instant)}, 초기화권 +{Gain(LessonHandlers.Reset)}");
			// 11-12: what it gave, in the reply's message field (error code 0) for the 1.0.46 app.
			string gift = first.error.error_msg.str ?? string.Empty;
			Check(first.error.error_code.n == 0 && gift.StartsWith("받은 것: 2.5억 MP") && gift.Length <= 127 && gift.Split('\n').All(line => PopupText.Px(line) <= PopupText.Limit),
				$"LESSON2026 받은 것 글: \"{gift.Replace("\n", " | ")}\"");
			Send(new Ma9MPacket.PKS_CS_USE_COUPON { coupon_code = { str = "LESSON2026" } });
			Ma9MPacket.PKS_SC_USE_COUPON again = Await<Ma9MPacket.PKS_SC_USE_COUPON>();
			Check(again.result.n == 2, $"LESSON2026 두 번째: result {again.result.n} (2 = 이미 씀)");
		}

		// 특성 추천 배분 · 자동 편성 (AdviceHandlers, 2026-11-11) on a data copy: each asked with REQ_SCENE_TUTORIAL "ma9m:…" comes
		// back as POPUP type 10; a plain OK (ACK 0, as types 1/2 send) changes nothing; 적용 (ACK 10) saves it and answers type 11.
		private static bool s_lessonSeen;

		private static void Advice(CardCatalog catalog)
		{
			Ma9MPacket.PKS_SC_POPUP Ask(string what)
			{
				Send(new Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL { scene_name = { str = what } });
				return Await<Ma9MPacket.PKS_SC_POPUP>(60000);
			}
			void Ack(byte n) => Send(new Ma9MPacket.PKS_CS_POPUP_ACK { Ack = { n = n } });
			string Text(Ma9MPacket.PKS_SC_POPUP p) => string.Join(" / ", (p.Messages.array ?? Array.Empty<PLongString>()).Select(m => m.str.Replace("\n", " | ")));
			string Traits()
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_TEAM_PROPERTY());
				Ma9MPacket.PKS_SC_ENTER_TEAM_PROPERTY r = Await<Ma9MPacket.PKS_SC_ENTER_TEAM_PROPERTY>();
				byte[] b = r.buff.bytes ?? Array.Empty<byte>();
				return string.Join(" ", Enumerable.Range(0, b.Length / 2).Where(i => b[2 * i + 1] > 0).Select(i => $"{b[2 * i]}:{b[2 * i + 1]}"));
			}
			string Lineup()
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO());
				Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO r = Await<Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO>();
				return string.Join(",", r.batters.array.Select(p => p.n)) + "|" + string.Join(",", r.fielders.array.Select(p => p.n)) + "|" + string.Join(",", r.pitchers.array.Select(p => p.n));
			}

			string traitsBefore = Traits();
			Ma9MPacket.PKS_SC_POPUP t = Ask("ma9m:traitadvice");
			Log($"traits now [{traitsBefore}]; asked: type {t.Type.n}: {Text(t)}");
			if (t.Type.n == AdviceHandlers.AskType)
			{
				Ack(0);
				Check(Traits() == traitsBefore, "a plain OK (ACK 0) did not apply the advice");
				Ack(AdviceHandlers.AckApply);
				Ma9MPacket.PKS_SC_POPUP done = Await<Ma9MPacket.PKS_SC_POPUP>(10000);
				string traitsAfter = Traits();
				Check(done.Type.n == AdviceHandlers.DoneType && traitsAfter != traitsBefore, $"적용: type {done.Type.n}: {Text(done)}; traits now [{traitsAfter}]");
				Ma9MPacket.PKS_SC_POPUP again = Ask("ma9m:traitadvice");
				Check(again.Type.n == 1, $"asked again: type {again.Type.n}: {Text(again)}");
			}
			else
			{
				Check(t.Type.n == 1, "no advice: a plain message");
			}

			// 11-12: "keep" the 1.0.45 way, first through the 특강 screen in between (its info popup is type 10 too: the old app's
			// 적용 there must apply nothing), then cancelled; "free" the 1.0.46 way (ma9m:v2:, types 20/21), applied.
			foreach (string mode in new[] { "keep", "keep", "free" })
			{
				bool v2 = mode == "free";
				string before = Lineup();
				Ma9MPacket.PKS_SC_POPUP l = Ask((v2 ? "ma9m:v2:" : "ma9m:") + "autolineup:" + mode);
				Log($"auto lineup {mode}{(v2 ? " (v2)" : string.Empty)}: type {l.Type.n}: {Text(l)}");
				if (v2 && l.Type.n == AdviceHandlers.AskType2)
				{
					// 11-12 morning: [title, summary, ok, page…], every page short enough for the window.
					string[] msgs = l.Messages.array.Select(m => m.str).ToArray();
					List<string> pages = new[] { msgs[1] }.Concat(msgs.Skip(3)).ToList();
					int longest = pages.Max(p => p.Split('\n').Length);
					Check(msgs.Length >= 4 && longest <= 14 && pages.All(p => p.Split('\n').All(line => PopupText.Px(line) <= PopupText.Limit)),
						$"v2 window: {pages.Count} page(s), longest {longest} lines, widest {pages.SelectMany(p => p.Split('\n')).Max(PopupText.Px):F0} px");
					for (int i = 0; i < pages.Count; i++)
					{
						Log($"  page {i + 1}:\n    " + pages[i].Replace("\n", "\n    "));
					}
				}
				if (l.Type.n != (v2 ? AdviceHandlers.AskType2 : AdviceHandlers.AskType))
				{
					Check(l.Type.n == 1, $"no lineup advice: a plain message (type {l.Type.n})");
					continue;
				}
				if (mode == "keep" && !s_lessonSeen)
				{
					s_lessonSeen = true;
					Send(new Ma9MPacket.PKS_CS_ENTER_LESSON());
					Ma9MPacket.PKS_SC_POPUP info = Await<Ma9MPacket.PKS_SC_POPUP>(10000);
					Ack(AdviceHandlers.AckApply);
					Check(info.Type.n == LessonHandlers.InfoPopupType && Lineup() == before, $"특강 info (type {info.Type.n}) then ACK 10: the 1군 stays");
					continue;
				}
				if (mode == "keep")
				{
					Ack(AdviceHandlers.AckCancel);
					Check(Lineup() == before, "취소 (ACK 11): the 1군 stays");
					continue;
				}
				Ack(AdviceHandlers.AckApply);
				Ma9MPacket.PKS_SC_POPUP done = Await<Ma9MPacket.PKS_SC_POPUP>(10000);
				string after = Lineup();
				Check(done.Type.n == AdviceHandlers.DoneType2 && after != before, $"적용 (v2): type {done.Type.n}: {Text(done)}; 1군 changed");
				Log($"  1군 now: {string.Join(", ", after.Split('|')[0].Split(',').Take(9).Select(s => catalog.Get(int.Parse(s))?.Name ?? s))} | {string.Join(", ", after.Split('|')[2].Split(',').Select(s => catalog.Get(int.Parse(s))?.Name ?? s))}");
			}
		}

		// FA 시장 (systems session, 2026-11-15) on a data copy holding the FA rows, the test team with the 계약금 in MP (set on the
		// copy): the album's FA pages (등급별 5, a season's club 20), then a tap's question the app sends (Ma9MAdvice "ma9m:v2:fa:<idx>"
		// → type 20), 취소 (ACK 11: not signed), 영입 (ACK 10 → type 21, the card owned, the MP taken), a second 영입 and a card that
		// is not FA refused (type 1).
		private static void FaMarketTest(CardCatalog catalog)
		{
			List<CardInfo> fa = catalog.All.Where(c => c.IsFA).ToList();
			Check(fa.Count > 0, $"{fa.Count} FA cards in the tables");
			if (fa.Count == 0)
			{
				return;
			}
			Ma9MPacket.PKS_SC_ENTER_ALBUM Page(int yy, int team)
			{
				Send(new Ma9MPacket.PKS_CS_ENTER_ALBUM { year = { n = (byte)yy }, team = { n = (byte)team } });
				return Await<Ma9MPacket.PKS_SC_ENTER_ALBUM>();
			}
			HashSet<int> Owned(Ma9MPacket.PKS_SC_ENTER_ALBUM p) => (p.possess_player.array ?? Array.Empty<PInteger>()).Select(i => i.n).ToHashSet();
			Ma9MPacket.PKS_SC_POPUP Ask(string what)
			{
				Send(new Ma9MPacket.PKS_CS_REQ_SCENE_TUTORIAL { scene_name = { str = what } });
				return Await<Ma9MPacket.PKS_SC_POPUP>(20000);
			}
			void Ack(byte n) => Send(new Ma9MPacket.PKS_CS_POPUP_ACK { Ack = { n = n } });
			string[] Msgs(Ma9MPacket.PKS_SC_POPUP p) => (p.Messages.array ?? Array.Empty<PLongString>()).Select(m => m.str ?? string.Empty).ToArray();
			string Text(Ma9MPacket.PKS_SC_POPUP p) => string.Join(" / ", Msgs(p).Select(m => m.Replace("\n", " | ")));

			Ma9MPacket.PKS_SC_ENTER_ALBUM all = Page(AlbumHandlers.GradePageYear, AlbumHandlers.FaPage);
			HashSet<int> shown = (all.index.array ?? Array.Empty<PInteger>()).Select(i => i.n).ToHashSet();
			HashSet<int> owned = Owned(all);
			Check(shown.Count == fa.Count && fa.All(c => shown.Contains(c.Idx)),
				$"등급별 FA page: {shown.Count} of {fa.Count} FA cards, all in colour; {owned.Count} owned; {all.total_count.n} seen in all (FA left out)");
			CardInfo card = fa.Where(c => !owned.Contains(c.Idx) && c.Ball == 8).OrderByDescending(c => c.Year).FirstOrDefault()
				?? fa.First(c => !owned.Contains(c.Idx));
			Ma9MPacket.PKS_SC_ENTER_ALBUM season = Page(card.Year % 100, AlbumHandlers.FaAlbumTeam);
			int seasonCount = season.index.array?.Length ?? 0;
			Check(seasonCount == fa.Count(c => c.Year == card.Year) && season.index.array.Any(i => i.n == card.Idx),
				$"'{card.Year % 100:00} FA page (club 20): {seasonCount} cards, {card.Name} among them");

			int price = global::Ma9MServer.FaMarket.Price(card);
			Ma9MPacket.PKS_SC_POPUP q = Ask("ma9m:v2:fa:" + card.Idx);
			string[] m = Msgs(q);
			double widest = m.Length > 1 ? m[1].Split('\n').Max(PopupText.Px) : 0;
			Check(q.Type.n == AdviceHandlers.AskType2 && m.Length >= 3 && m[1].Contains($"계약금 {price:N0} MP") && m[2] == "영입" && widest <= PopupText.Limit,
				$"asked {card.Year} {card.Name} ({card.Idx}): type {q.Type.n}: {Text(q)} (widest {widest:F0} px)");
			if (q.Type.n != AdviceHandlers.AskType2)
			{
				return;
			}
			string after = m[1].Split('\n').FirstOrDefault(line => line.StartsWith("가진 MP"))?.Split('→').Last().Trim() ?? "?";
			Ack(AdviceHandlers.AckCancel);
			Check(!Owned(Page(AlbumHandlers.GradePageYear, AlbumHandlers.FaPage)).Contains(card.Idx), "취소 (ACK 11): not signed");

			Ask("ma9m:v2:fa:" + card.Idx);
			Ack(AdviceHandlers.AckApply);
			Ma9MPacket.PKS_SC_POPUP done = Await<Ma9MPacket.PKS_SC_POPUP>(10000);
			string[] d = Msgs(done);
			bool signed = Owned(Page(AlbumHandlers.GradePageYear, AlbumHandlers.FaPage)).Contains(card.Idx);
			Check(done.Type.n == AdviceHandlers.DoneType2 && signed && d.Length > 1 && d[1].Contains("남은 MP " + after),
				$"영입 (ACK 10): type {done.Type.n}: {Text(done)}; owned {signed}; MP left as asked ({after})");

			Ma9MPacket.PKS_SC_POPUP again = Ask("ma9m:v2:fa:" + card.Idx);
			Check(again.Type.n == 1 && Text(again).Contains("이미"), $"asked again: type {again.Type.n}: {Text(again)}");
			CardInfo plain = catalog.Get(card.SourceIdx);
			Ma9MPacket.PKS_SC_POPUP notFa = Ask("ma9m:v2:fa:" + plain.Idx);
			Check(notFa.Type.n == 1 && Text(notFa).Contains("FA 시장에 없는"), $"the source card {plain.Idx}: type {notFa.Type.n}: {Text(notFa)}");
		}

		private static Dictionary<int, int> Inventory()
		{
			Send(new Ma9MPacket.PKS_CS_ENTER_INVENTORY());
			Ma9MPacket.PKS_SC_ENTER_INVENTORY reply = Await<Ma9MPacket.PKS_SC_ENTER_INVENTORY>();
			Dictionary<int, int> items = new Dictionary<int, int>();
			for (int i = 0; i < (reply.item_index.array?.Length ?? 0); i++)
			{
				items[reply.item_index.array[i].n] = reply.item_amount.array[i].n;
			}
			return items;
		}

		private static Dictionary<int, int> BookList()
		{
			Send(new Ma9MPacket.PKS_CS_TRAINING_CARD_LIST());
			Ma9MPacket.PKS_SC_TRAINING_CARD_LIST reply = Await<Ma9MPacket.PKS_SC_TRAINING_CARD_LIST>();
			Dictionary<int, int> books = new Dictionary<int, int>();
			for (int i = 0; i < (reply.training_card_list.array?.Length ?? 0); i++)
			{
				books[reply.training_card_list.array[i].n] = reply.amount_list.array[i].n;
			}
			return books;
		}

		// 선발 5경기 제한 (SpBlock, 2026-11-09), over the wire on a server running a data copy (it changes the test team's
		// lineup): a starter (not the next one) swapped with the first reliever is saved, the roster screen then lists him in
		// sp_blocked with 5 games, and putting him back is refused with the popup.
		private static void StarterBlock(CardCatalog catalog)
		{
			Roster();
			Send(new Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO());
			Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO info = Await<Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO>();
			int[] pitchers = info.pitchers.array.Select(p => p.n).ToArray();
			int slot = Enumerable.Range(0, 5).First(s => s != info.next_sp.n && s < pitchers.Length && pitchers[s] != 0);
			int rpSlot = Enumerable.Range(5, Math.Max(0, pitchers.Length - 5)).First(s => pitchers[s] != 0);
			int sp = pitchers[slot], rp = pitchers[rpSlot];
			Log($"next starter slot {info.next_sp.n}; SP{slot + 1} {catalog.Get(sp)?.Name} ↔ {catalog.Get(rp)?.Name}; blocked before: {info.sp_blocked.array?.Length ?? 0}");
			int[] swapped = (int[])pitchers.Clone();
			swapped[slot] = rp;
			swapped[rpSlot] = sp;
			Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER saved = ApplyPitchers(swapped);
			Check(saved.result.n == 0, $"swap saved (result {saved.result.n})");
			Send(new Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO());
			Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO after = Await<Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO>();
			int at = Array.FindIndex(after.sp_blocked.array ?? Array.Empty<PInteger>(), p => p.n == sp);
			Check(at >= 0 && after.sp_blocked_count.array?.Length == after.sp_blocked.array.Length && after.sp_blocked_count.array[at].n == SpBlock.Games,
				$"roster screen: sp_blocked [{string.Join(", ", (after.sp_blocked.array ?? Array.Empty<PInteger>()).Select((p, i) => $"{catalog.Get(p.n)?.Name} {after.sp_blocked_count.array[i].n}"))}]");
			Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER back = new Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER();
			back.pitchers.array = pitchers.Select(i => new PInteger { n = i }).ToArray();
			Send(back);
			Ma9MPacket.PKS_SC_POPUP popup = Await<Ma9MPacket.PKS_SC_POPUP>();
			Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER refused = Await<Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER>();
			Check(refused.result.n == 1 && popup.Messages.array?.Length == 2 && popup.Messages.array[1].str.Contains($"{SpBlock.Games}경기 남음"),
				$"putting him back refused (result {refused.result.n}): {popup.Messages.array?[0].str} / {popup.Messages.array?[1].str.Replace('\n', ' ')}");
		}

		private static Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER ApplyPitchers(int[] pitchers)
		{
			Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER request = new Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER();
			request.pitchers.array = pitchers.Select(i => new PInteger { n = i }).ToArray();
			Send(request);
			return Await<Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER>();
		}

		private static List<PUICardInfoStat> Roster()
		{
			Send(new Ma9MPacket.PKS_CS_ROSTER_PLAYER());
			List<PUICardInfoStat> cards = new List<PUICardInfoStat>();
			while (true)
			{
				Ma9MPacket.PKS_SC_ROSTER_PLAYER part = Await<Ma9MPacket.PKS_SC_ROSTER_PLAYER>();
				cards.AddRange(part.players.array ?? Array.Empty<PUICardInfoStat>());
				if (part.isEnd.n != 0)
				{
					return cards;
				}
			}
		}

		private static Ma9MPacket.PKS_SC_CARD_DETAIL_INFO Detail(int card)
		{
			Ma9MPacket.PKS_CS_CARD_DETAIL_INFO request = new Ma9MPacket.PKS_CS_CARD_DETAIL_INFO();
			request.index.n = card;
			Send(request);
			return Await<Ma9MPacket.PKS_SC_CARD_DETAIL_INFO>();
		}

		private static Ma9MPacket.PKS_SC_TRY_CARD_TRAINING Train(int card, int book, int slot)
		{
			Ma9MPacket.PKS_CS_TRY_CARD_TRAINING request = new Ma9MPacket.PKS_CS_TRY_CARD_TRAINING();
			request.card_index.n = card;
			request.training_card_index.n = book;
			request.training_card_slot.n = (byte)slot;
			Send(request);
			return Await<Ma9MPacket.PKS_SC_TRY_CARD_TRAINING>();
		}

		private static (int item, int result)[] Pairs(Ma9MPacket.PKS_SC_TRY_CARD_TRAINING reply)
		{
			return reply.training_cards.array.Select((c, i) => (c.n, (int)reply.training_effect.array[i].n)).ToArray();
		}

		private static string Slots(PArray<PInteger> cards, PArray<PByte> effects)
		{
			return string.Join(" | ", (cards.array ?? Array.Empty<PInteger>()).Select((c, i) => c.n == -1 ? "빈칸" : $"{TrainingBooks.Name(c.n)} {Signed(TrainingBooks.Value(c.n, effects.array[i].n))}"));
		}

		private static void Connect()
		{
			Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
			socket.Connect(IPAddress.Loopback, Program.GamePort);
			s_connection = new Connection(socket, new Ma9MPacket(), null, false, true, true);
			s_connection.Listen();
			Await<Ma9MPacket.PKS_SC_INITIALIZED>();
		}

		private static void Login(string email, string password, string bundle = "")
		{
			Ma9MPacket.PKS_CS_REQUEST_LOGIN login = new Ma9MPacket.PKS_CS_REQUEST_LOGIN();
			login.id.str = email;
			login.pw.str = password;
			login.device_id.str = "devclient";
			login.bundle_version.str = bundle;
			Send(login);
			Ma9MPacket.PKS_SC_REQUEST_LOGIN reply = Await<Ma9MPacket.PKS_SC_REQUEST_LOGIN>();
			if (reply.result.n != 0)
			{
				throw new InvalidOperationException($"login failed: result {reply.result.n}");
			}
			Ma9MPacket.PKS_CS_GET_TEAM_LIST list = new Ma9MPacket.PKS_CS_GET_TEAM_LIST();
			list.server_type.n = 0;
			Send(list);
			PUserInfo team = Await<Ma9MPacket.PKS_SC_GET_TEAM_LIST>().userInfoList.array.First(u => u.user_type.n != -1);
			Ma9MPacket.PKS_CS_CHOOSE_TEAM choose = new Ma9MPacket.PKS_CS_CHOOSE_TEAM();
			choose.userInfo.SetUserInfo(team.user_code.n, team.FBID.str ?? string.Empty, team.dbid.n, team.teamName.str ?? string.Empty, team.user_type.n,
				team.team_emblem.n, team.league_grade.n, team.MP.n, team.cash.n);
			choose.server_type.n = 0;
			choose.possess_team_count.n = 1;
			Send(choose);
			Ma9MPacket.PKS_SC_CHOOSE_TEAM chosen = Await<Ma9MPacket.PKS_SC_CHOOSE_TEAM>();
			Log($"logged in as {email}, team {team.user_code.n} (choose result {chosen.result.n})");
		}

		private static void Send(Packet.PKS_PACKET packet)
		{
			if (!s_connection.SendPacket(packet))
			{
				throw new InvalidOperationException($"send failed: {packet.GetType().Name}");
			}
		}

		// The next packet of type T; anything else that arrives first is skipped.
		private static T Await<T>(int timeoutMs = TimeoutMs) where T : Packet.PKS_PACKET
		{
			Stopwatch watch = Stopwatch.StartNew();
			while (watch.ElapsedMilliseconds < timeoutMs)
			{
				Packet.PKS_PACKET packet = s_connection.GetPacketFromQueue();
				if (packet == null)
				{
					System.Threading.Thread.Sleep(5);
					continue;
				}
				if (packet is T wanted)
				{
					return wanted;
				}
				if (packet is Ma9MPacket.PKS_SYSTEM_KEEP_CONNECTION)
				{
					Send(new Ma9MPacket.PKS_SYSTEM_KEEP_CONNECTION());
				}
			}
			throw new TimeoutException($"no {typeof(T).Name} within {timeoutMs} ms");
		}

		private static string Signed(int v) => v > 0 ? "+" + v : v.ToString();

		private static void Check(bool ok, string what)
		{
			if (!ok)
			{
				s_problems++;
			}
			Log($"{(ok ? "ok  " : "FAIL")} {what}");
		}

		private static void Log(string text)
		{
			ServerLog.Info("[devclient] " + text);
		}
	}
}
