using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// "--posttest": the common calendar on a copy of the data directory (it writes games, MP and seasons).
	// Leagues on their own clock move to the calendar (closed with their standings, joined mid-season),
	// then the clock runs through one whole cycle: the rest of the regular season, the postseason slot
	// by slot, 팀 정비 (close, promotion), 편성 (the next league) and the next season's first games.
	public static class PostseasonSelfTest
	{
		private static int s_problems;

		private static void Check(bool ok, string what)
		{
			if (!ok) s_problems++;
			ServerLog.Info($"{(ok ? "ok  " : "FAIL")} {what}");
		}

		public static int Run(Database db, GameData data, string dbPath)
		{
			// The postseason rotation (engine session, 2026-10-31): SP1-SP4 in turn from series to series, SP5 after SP4
			// once all four have started in that series. Games 145-146 와일드카드, 147-151 준PO, 152-156 PO, 157- 한국시리즈.
			int R(int[] played, int game) => LeagueService.PostRotation(played, game);
			string Seq(int[] played, int upTo) => string.Join(" ", Enumerable.Range(0, played.Length).Where(i => played[i] <= upTo).Select(i => R(played.Take(i).ToArray(), played[i])));
			Check(Seq(new[] { 157, 158, 159, 160, 161, 162, 163 }, 163) == "1 2 3 4 5 1 2", "rotation: 한국시리즈 직행 1 2 3 4 5 1 2");
			Check(Seq(new[] { 152, 153, 154, 157, 158, 159, 160, 161, 162, 163 }, 163) == "1 2 3 4 1 2 3 4 5 1", "rotation: 플레이오프 3-0 → 한국시리즈 4 1 2 3 4 5 1 (6차전 5선발)");
			Check(R(new[] { 152, 153, 154, 155 }, 157) == 1 && R(new[] { 152, 153, 154, 155, 156 }, 157) == 1 && Seq(new[] { 152, 153, 154, 155, 156 }, 156) == "1 2 3 4 5",
				"rotation: 3-1 → SP1; 1 2 3 4 5 (3-2) → SP1");
			Check(R(new[] { 145 }, 147) == 2 && Seq(new[] { 145, 147, 148, 149, 150, 151, 152 }, 152) == "1 2 3 4 1 2 3",
				"rotation: 와일드카드 1경기 → 준PO 2 3 4 1 2 (5선발 없음) → PO 3");
			Check(R(new[] { 145, 147, 148, 149, 150 }, 152) == 2 && R(new[] { 145, 146, 147, 148, 149 }, 152) == 2, "rotation: 와일드카드 + 준PO 3-1 / 2경기 + 3-0 → PO SP2");
			string dir = System.IO.Path.GetDirectoryName(dbPath);
			TeamPropertyHandlers.Register(new GameLoop(), dir);
			LeagueService.Init(db, data, dbPath);
			InventoryHandlers.Register(new GameLoop(), db, data.Catalog, dir);
			RecordHandlers.Register(new GameLoop(), db, data.Catalog);
			OperationHandlers.Register(null, data.Catalog, dbPath);
			ContractHandlers.Register(null, db, data.Catalog, dir);
			TeamColors.Register(null, db, data.Catalog, dir);
			Missions.Register(null, db, dir);
			Achievements.Register(null, db, data.Catalog, dir);
			LeagueAwards.Init(db, dbPath);
			LeagueStore store = LeagueService.Store;

			// 1. The calendar itself.
			DateTime now = DateTime.Now;
			DateTime c0 = LeagueCalendar.CycleStart(now);
			Check(c0 <= now && now < c0 + LeagueCalendar.Cycle && c0.Minute == 0 && (c0.Hour == 0 || c0.Hour == 12), $"cycle of now: {c0:MM-dd HH:mm} (+60 h {c0 + LeagueCalendar.Cycle:MM-dd HH:mm})");
			Check(LeagueCalendar.OnCalendar(c0) && !LeagueCalendar.OnCalendar(c0.AddMinutes(20)) && LeagueCalendar.OnCalendar(c0 + LeagueCalendar.Cycle), "OnCalendar");
			Check(LeagueCalendar.JoinAt(c0) == (c0, 1) && LeagueCalendar.JoinAt(c0.AddSeconds(1)) == (c0, 2) && LeagueCalendar.JoinAt(c0.AddMinutes(20)) == (c0, 2)
				&& LeagueCalendar.JoinAt(c0.AddMinutes(49 * 20 + 7)) == (c0, 51) && LeagueCalendar.JoinAt(c0.AddMinutes(143 * 20)) == (c0, 144)
				&& LeagueCalendar.JoinAt(c0.AddMinutes(143 * 20 + 1)) == (c0 + LeagueCalendar.Cycle, 1) && LeagueCalendar.JoinAt(c0.AddHours(59.9)) == (c0 + LeagueCalendar.Cycle, 1),
				"JoinAt: on a slot plays it, between slots the next; a team made in the slot of game 50 plays 51-144 (94 games); after game 144 the next season");
			Check(LeagueCalendar.PostSlotTime(c0, 1) == c0.AddHours(48) && LeagueCalendar.PostSlotTime(c0, 19) == c0.AddHours(57), "postseason slots 48:00 .. 57:00");
			// The board's "X" column (2026-10-26): innings 1-11 as played, X the runs from the 12th on, "-" (255) unplayed.
			{
				string Board(int[] line) => string.Join(" ", LeagueHandlers.Innings(line).Select(p => p.n == 255 ? "-" : p.n.ToString()));
				string nine = Board(new[] { 1, 0, 0, 2, 0, 0, 0, 0 });
				string fifteen = Board(new[] { 0, 0, 1, 0, 0, 0, 0, 2, 0, 0, 0, 1, 1, 0, 1 });
				Check(nine == "1 0 0 2 0 0 0 0 - - - -" && fifteen == "0 0 1 0 0 0 0 2 0 0 0 3", $"board X column: [{nine}] [{fifteen}]");
			}
			// A 한국시리즈 with ties (2026-10-26): 3-2 with two ties after seven games is not decided, and game 8 (slot 20,
			// the higher seed at home) decides it.
			{
				List<LeagueClub> ranked = Enumerable.Range(0, LeagueRules.Clubs).Select(i => new LeagueClub { Index = i }).ToList();
				List<LeagueGame> made = new List<LeagueGame>();
				void Game(int slot, int high, int low, int highRuns, int lowRuns) =>
					made.Add(new LeagueGame { Game = Postseason.GameNumber(slot), Home = high, Away = low, HomeScore = highRuns, AwayScore = lowRuns });
				Game(1, 3, 4, 5, 1);
				foreach (int slot in new[] { 3, 4, 5 }) Game(slot, 2, 3, 4, 2);
				foreach (int slot in new[] { 8, 9, 10 }) Game(slot, 1, 2, 4, 2);
				int[][] ks = { new[] { 3, 1 }, new[] { 1, 2 }, new[] { 2, 2 }, new[] { 5, 0 }, new[] { 0, 1 }, new[] { 3, 3 }, new[] { 6, 4 } };
				for (int k = 0; k < ks.Length; k++) Game(13 + k, 0, 1, ks[k][0], ks[k][1]);
				SeriesState after7 = Postseason.Build(ranked, made, LeagueCalendar.PostSlots)[PostSeries.KoreanSeries];
				Game(LeagueCalendar.PostSlots + 1, 0, 1, 2, 1);
				Bracket after8 = Postseason.Build(ranked, made, LeagueCalendar.PostSlots + 1);
				SeriesState ks8 = after8[PostSeries.KoreanSeries];
				Check(!after7.Decided && after7.HighWins == 3 && after7.LowWins == 2 && ks8.Winner == 0 && ks8.Games.Count == 8 && ks8.HighHome(8) && after8.Champion == 0
					&& LeagueCalendar.PostSlotTime(c0, LeagueCalendar.LastPostSlot) == c0.AddHours(59.5),
					$"한국시리즈 with 2 ties: after 7 {after7.HighWins}-{after7.LowWins} undecided, game 8 (57:30, higher seed home) decides it ({ks8.HighWins}-{ks8.LowWins}); extra slots to 59:30");
			}
			int gate = LeagueRules.GateTier, top = LeagueRules.ChampionsTier;
			Check(LeagueRules.NextTier(1, 1, 5) == 1 && LeagueRules.NextTier(1, 5, 4) == 2 && LeagueRules.NextTier(1, 8, 0) == 0 && LeagueRules.NextTier(0, 4, 0) == 1 && LeagueRules.NextTier(0, 5, 0) == 0
				&& LeagueRules.NextTier(0, 10, 0) == 0 && LeagueRules.NextTier(2, 7, 0) == 2
				// The gate to 챔피언스 (메이저, with six tiers 레전드; user, 2026-10-28): final standing 1-3 up, 4-6 stay (준PO and wild-card
				// losers, regular 6th), 7-10 down.
				&& LeagueRules.NextTier(gate, 1, 1) == top && LeagueRules.NextTier(gate, 4, 3) == top && LeagueRules.NextTier(gate, 2, 4) == gate && LeagueRules.NextTier(gate, 4, 5) == gate
				&& LeagueRules.NextTier(gate, 6, 0) == gate && LeagueRules.NextTier(gate, 7, 0) == gate - 1 && LeagueRules.NextTier(gate, 10, 0) == gate - 1
				// 챔피언스, the top tier (user, 2026-10-28): the top three stay, everyone else goes down.
				&& LeagueRules.NextTier(top, 1, 1) == top && LeagueRules.NextTier(top, 2, 3) == top && LeagueRules.NextTier(top, 5, 4) == gate && LeagueRules.NextTier(top, 4, 5) == gate
				&& LeagueRules.NextTier(top, 6, 0) == gate
				// Six tiers (2026-11-08): the new 메이저 plays as 루키 and 트리플A (postseason top 4 up to 레전드, bottom 3 down).
				&& (!LeagueRules.NewStructure || LeagueRules.NextTier(3, 5, 4) == 4 && LeagueRules.NextTier(3, 1, 5) == 3 && LeagueRules.NextTier(3, 8, 0) == 2),
				$"promotion ({LeagueRules.Tiers} tiers): postseason top 4 (비기너 regular top 4), bottom 3 down; {LeagueRules.TierNames[gate]} 1-3 up, 4-6 stay, 7-10 down; 챔피언스 top 3 stay, the rest down");

			// 2. Leagues on their own clock move to the calendar.
			List<(uint code, string name, byte emblem, byte grade)> teams = store.ActiveTeams();
			Dictionary<uint, LeagueState> before = teams.ToDictionary(t => t.code, t => store.Load(t.code));
			LeagueService.Tick(now);
			(DateTime joinStart, int joinFirst) = LeagueCalendar.JoinAt(now);
			foreach ((uint code, string name, _, _) in teams)
			{
				LeagueState b = before[code], s = store.Load(code);
				if (b == null || LeagueCalendar.OnCalendar(b.Start))
				{
					ServerLog.Info($"     [{name}] already on the calendar (season {s.Season} from {s.Start:MM-dd HH:mm}, game {s.FirstGame}..)");
					continue;
				}
				var h = store.History(code, b.Season);
				int rank = h?.rank ?? -1;
				Check(s.Season == b.Season + 1 && s.Start == joinStart && s.FirstGame == joinFirst && h != null && h.Value.nextTier == s.Tier
					&& s.Tier == LeagueRules.NextTier(b.Tier, rank, rank <= LeagueRules.PostseasonClubs ? rank : 0) && s.Clubs[s.MyIndex].LastRank == rank,
					$"[{name}] season {b.Season} ({LeagueRules.TierNames[b.Tier]}, own clock from {b.Start:MM-dd HH:mm}) closed at #{rank} → season {s.Season} {LeagueRules.TierNames[s.Tier]} from game {s.FirstGame} ({s.Start:MM-dd HH:mm} cycle)");
			}

			// 3. To the end of the regular season.
			DateTime start = joinStart;
			Tick(start + LeagueCalendar.PostStart - TimeSpan.FromMinutes(1));
			Dictionary<uint, int[]> records = new Dictionary<uint, int[]>();
			foreach ((uint code, string name, _, _) in teams)
			{
				LeagueState s = store.Load(code);
				if (s.Start != start) continue;
				List<LeagueGame> games = store.Games(code, s.Season);
				int expected = (LeagueRules.Games - s.FirstGame + 1) * LeagueRules.Clubs / 2;
				Check(s.NextGame == LeagueRules.Games + 1 && s.PostSlot == 1 && games.Count == expected && games.All(g => g.Game >= s.FirstGame && g.Game <= LeagueRules.Games)
					&& s.Clubs.All(c => c.W + c.L + c.D == LeagueRules.Games - s.FirstGame + 1),
					$"[{name}] regular season over: games {s.FirstGame}-{s.Played}, {games.Count} played, my club #{s.RankOf(s.MyIndex)}");
				records[code] = s.Clubs.SelectMany(c => new[] { c.W, c.L, c.D, c.RunsFor }).ToArray();
				Check(LeagueAwards.Settled(code, s.Season) == (false, false, false), $"[{name}] 리그 시상 not yet a minute before 48 h");
				DateTime before48 = start + LeagueCalendar.PostStart - TimeSpan.FromMinutes(1);
				Check(LeagueService.StartersFree(s, before48) == (LeagueService.NextGameOf(s) == null), $"[{name}] 선발 교체 자유 only without a game due (a minute before 48 h)");
				if (LeagueRules.HasPostseason(s.Tier))
				{
					List<LeagueClub> ranked = s.Ranked();
					int seed = ranked.FindIndex(c => c.Index == s.MyIndex) + 1;
					var next = LeagueService.NextGameOf(s);
					Check(seed is 4 or 5 ? next?.game == 145 && next?.opponent == ranked[seed == 4 ? 4 : 3].Index : next == null,
						$"[{name}] seed {seed}: NEXT {(next == null ? "none" : $"game {next.Value.game} vs {s.Clubs[next.Value.opponent].Name}")}");
				}
			}

			// 4. The postseason slot by slot (the 한국시리즈's extra slots only while some league's is undecided).
			for (int slot = 1; slot <= LeagueCalendar.LastPostSlot; slot++)
			{
				if (slot > LeagueCalendar.PostSlots && teams.Select(t => store.Load(t.code)).All(x => x.Start != start || !LeagueRules.HasPostseason(x.Tier) || Postseason.Of(x).Champion >= 0)) break;
				Dictionary<uint, Bracket> pre = teams.Select(t => store.Load(t.code)).Where(s => s.Start == start && LeagueRules.HasPostseason(s.Tier))
					.ToDictionary(s => s.UserCode, Postseason.Of);
				Tick(LeagueCalendar.PostSlotTime(start, slot).AddSeconds(1));
				if (slot == 1)
				{
					// 리그 시상 when the regular season ends, before the postseason; the 한국시리즈 MVP waits for the close.
					foreach ((uint code, string name, _, _) in teams)
					{
						LeagueState s = store.Load(code);
						if (s.Start != start) continue;
						Check(LeagueAwards.Settled(code, s.Season) == (true, false, false), $"[{name}] 리그 시상 at 48 h ({LeagueRules.TierNames[s.Tier]}), 한국시리즈 MVP not yet");
					}
				}
				foreach ((uint code, Bracket b0) in pre)
				{
					LeagueState s = store.Load(code);
					List<LeagueGame> inSlot = store.Games(code, s.Season, game: Postseason.GameNumber(slot));
					SeriesState sr = b0.AtSlot(slot);
					bool playedOk = sr.Decided ? inSlot.Count == 0 : inSlot.Count == 1;
					if (inSlot.Count == 1)
					{
						int i = slot - sr.FirstSlot + 1;
						LeagueGame g = inSlot[0];
						playedOk &= (sr.HighHome(i) ? g.Home == sr.High && g.Away == sr.Low : g.Home == sr.Low && g.Away == sr.High) && sr.Games.Count == i - 1;
						bool mine = g.Home == s.MyIndex || g.Away == s.MyIndex;
						playedOk &= mine ? !g.Confirmed && g.Mp > 0 : g.Confirmed;
					}
					if (!playedOk || slot == sr.LastSlot || inSlot.Count == 1 && slot == sr.FirstSlot)
					{
						Check(playedOk, $"[{s.Clubs[s.MyIndex].Name}] slot {slot} {sr.Kind}: {(inSlot.Count == 0 ? "no game (decided)" : $"{s.Clubs[inSlot[0].Away].Name} {inSlot[0].AwayScore}-{inSlot[0].HomeScore} {s.Clubs[inSlot[0].Home].Name}")}");
					}
				}
			}
			foreach ((uint code, string name, _, _) in teams)
			{
				LeagueState s = store.Load(code);
				if (s.Start != start) continue;
				int[] rec = s.Clubs.SelectMany(c => new[] { c.W, c.L, c.D, c.RunsFor }).ToArray();
				List<LeagueGame> post = store.Games(code, s.Season).Where(g => Postseason.IsPost(g.Game)).ToList();
				if (!LeagueRules.HasPostseason(s.Tier))
				{
					Check(post.Count == 0 && s.PostSlot == 1, $"[{name}] {LeagueRules.TierNames[s.Tier]}: no postseason");
					continue;
				}
				Bracket b = Postseason.Of(s);
				Check(rec.SequenceEqual(records[code]), $"[{name}] standings unchanged by the postseason");
				int[] places = b.Seeds.Select(b.PlaceOf).OrderBy(x => x).ToArray();
				Check(s.PostSlot == LeagueCalendar.LastPostSlot + 1 && b.Champion >= 0 && places.SequenceEqual(new[] { 1, 2, 3, 4, 5 }),
					$"[{name}] champion {s.Clubs[b.Champion].Name}, places of seeds 1-5: {string.Join(" ", b.Seeds.Select(b.PlaceOf))}, my club {b.PlaceOf(s.MyIndex)}");
				// Each postseason club's starters, series by series (SP n), as the games were played.
				foreach (int club in b.Seeds.Where(c => c >= 0))
				{
					List<int> mine = post.Where(g => g.Home == club || g.Away == club).Select(g => g.Game).OrderBy(g => g).ToList();
					string seq = string.Join(" | ", mine.GroupBy(Postseason.SeriesOf).Select(grp => string.Join(" ", grp.Select(g => "SP" + LeagueService.PostRotation(mine, g)))));
					// SP1-SP4 in turn (SP5 aside), and SP5 only right after an SP4 of the same series that had all four.
					List<(int k, int sp)> starts = mine.Select(g => (Postseason.SeriesOf(g), LeagueService.PostRotation(mine, g))).ToList();
					List<int> fours = starts.Where(x => x.sp != 5).Select(x => x.sp).ToList();
					bool fifthOk = fours.Select((sp, i) => sp == 1 + i % 4).All(b => b)
						&& starts.Select((x, i) => x.sp != 5 || (i > 0 && starts[i - 1].k == x.k && starts[i - 1].sp == 4
							&& starts.Take(i).Where(y => y.k == x.k).Reverse().TakeWhile(y => y.sp != 5).Select(y => y.sp).Distinct().Count() == 4)).All(b => b);
					Check(fifthOk, $"[{name}] {s.Clubs[club].Name} 선발: {seq}");
				}
				foreach (SeriesState sr in b.Series)
				{
					int needLow = sr.WinsNeeded, needHigh = sr.WinsNeeded - sr.Advantage;
					// Decided as soon as one side had its wins: the last game gave it, none came after.
					bool ruleOk = sr.Games.Count <= sr.Slots
						&& (sr.Winner == sr.High ? sr.HighWins >= needHigh || sr.Games.Count == sr.Slots && sr.HighWins >= sr.LowWins - sr.Advantage
							: sr.LowWins >= needLow || sr.Games.Count == sr.Slots && sr.LowWins > sr.HighWins + sr.Advantage);
					for (int k = 1; k < sr.Games.Count; k++)
					{
						Bracket part = Postseason.Build(s.Ranked(), post.Where(g => g.Game < sr.Games[k].Game), Postseason.SlotOf(sr.Games[k].Game) - 1);
						if (part.Series[(int)sr.Kind].Decided) ruleOk = false;
					}
					// Ties (2026-10-26): none before the 한국시리즈 (끝장승부 up to EndlessInnings); the 한국시리즈 stops at 11 innings.
					bool tieOk = sr.Kind == PostSeries.KoreanSeries
						? sr.Games.All(g => g.Detail.HomeLine.Length <= LeagueRules.MaxInnings && g.Detail.AwayLine.Length <= LeagueRules.MaxInnings)
						: sr.Games.All(g => g.HomeScore != g.AwayScore && g.Detail.AwayLine.Length <= LeagueRules.EndlessInnings);
					Check(tieOk, $"[{name}] {sr.Kind}: {(sr.Kind == PostSeries.KoreanSeries ? "11 innings at most" : "no tie")} ({sr.Games.Count(g => g.HomeScore == g.AwayScore)} tie(s), longest {sr.Games.Select(g => g.Detail.AwayLine.Length).DefaultIfEmpty(0).Max()} innings)");
					int hits = sr.Games.Sum(g => g.Detail.Stats[0][0] + g.Detail.Stats[1][0]);
					int lineHits = store.PostLines(code, s.Season, sr.Kind).Where(p => !p.Pitcher).Sum(p => p.H);
					int runs = sr.Games.Sum(g => g.HomeScore + g.AwayScore), lineRuns = store.PostLines(code, s.Season, sr.Kind).Where(p => !p.Pitcher).Sum(p => p.R);
					Check(ruleOk && hits == lineHits && runs == lineRuns,
						$"[{name}] {sr.Kind}: {s.Clubs[sr.High].Name} {sr.HighWins}{(sr.Advantage > 0 ? "+1" : "")} - {sr.LowWins} {s.Clubs[sr.Low].Name} in {sr.Games.Count} → {s.Clubs[sr.Winner].Name}; "
						+ $"games {string.Join(" ", sr.Games.Select(g => $"{g.AwayScore}:{g.HomeScore}"))}; lines H {lineHits}/{hits} R {lineRuns}/{runs}");
				}
				// The screen: hidden until confirmed, then the whole bracket (seen 10 minutes after the last game: a 한국시리즈 with a
				// tie goes past slot 19).
				DateTime seen = LeagueCalendar.PostSlotTime(start, Math.Max(LeagueCalendar.PostSlots, post.Select(g => Postseason.SlotOf(g.Game)).DefaultIfEmpty(0).Max())).AddMinutes(10);
				Ma9MPackets.Ma9MPacket.PKS_SC_ENTER_POSTSEASON_INFO before57 = Postseason.BuildInfo(s, seen);
				LeagueService.ConfirmAndPay(code, s.Season, null);
				Ma9MPackets.Ma9MPacket.PKS_SC_ENTER_POSTSEASON_INFO info = Postseason.BuildInfo(s, seen);
				info.ToBytes(new byte[info.GetSize() + 14], 14);
				bool mineHidden = post.Any(g => g.Home == s.MyIndex || g.Away == s.MyIndex) ? before57.post_state.n == 1 || before57.user_num_final_win.n == b.Champion : true;
				Check(info.post_state.n == 2 && info.user_num_final_win.n == b.Champion && info.user_num_wc_high.n == b.Seeds[3] && info.user_num_wc_low.n == b.Seeds[4]
					&& info.user_num_4th.n == b.Series[0].Winner && info.home_game_score_final.array.Length == 7 && info.home_game_score_wc.array.Length == 2 && mineHidden,
					$"[{name}] 포스트시즌 화면: state {info.post_state.n}, 우승 {info.user_num_final_win.n}, WC {info.user_num_wc_high.n}-{info.user_num_wc_low.n} → {info.user_num_wc_win.n}, 준PO 4th {info.user_num_4th.n}, "
					+ $"KS {info.score_final_first.n}:{info.score_final_second.n} [{string.Join(",", info.home_game_score_final.array.Select(x => x.n))}] (before confirming: state {before57.post_state.n})");
			}

			// 5. 팀 정비: the season closes.
			Tick(start + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1));
			Dictionary<uint, LeagueState> closed = new Dictionary<uint, LeagueState>();
			foreach ((uint code, string name, _, _) in teams)
			{
				LeagueState s = store.Load(code);
				if (s.Start != start) continue;
				closed[code] = s;
				int rank = s.RankOf(s.MyIndex);
				int place = LeagueRules.HasPostseason(s.Tier) ? Postseason.Of(s).PlaceOf(s.MyIndex) : 0;
				var h = store.History(code, s.Season);
				Check(s.NextTier == LeagueRules.NextTier(s.Tier, rank, place) && h?.nextTier == s.NextTier && RosterHandlers.MaxCostOf(code) == LeagueRules.BallCap[s.NextTier],
					$"[{name}] 팀 정비: #{rank}{(place > 0 ? $", 포스트시즌 {place}위" : string.Empty)} → {LeagueRules.TierNames[s.NextTier]} (cap {RosterHandlers.MaxCostOf(code)}), next match {(LeagueService.NextGameOf(s) == null ? "none" : "?")}");
				Check(LeagueService.StartersFree(s, start + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1)), $"[{name}] 선발 교체 자유 after the close");
				Check(LeagueAwards.Settled(code, s.Season) == (true, true, LeagueRules.HasPostseason(s.Tier)),
					$"[{name}] 한국시리즈 MVP {(LeagueRules.HasPostseason(s.Tier) ? "ceremony" : "none (no postseason)")} and 업적 at the close");
			}

			// 6. 편성 and the next season's first games.
			Tick(start + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1));
			foreach ((uint code, string name, _, _) in teams)
			{
				if (!closed.TryGetValue(code, out LeagueState c)) continue;
				LeagueState s = store.Load(code);
				Check(s.Season == c.Season + 1 && s.Start == start + LeagueCalendar.Cycle && s.NextGame == 1 && s.FirstGame == 1 && s.Tier == c.NextTier && s.NextTier == -1
					&& store.Games(code, s.Season).Count == 0 && s.Clubs[s.MyIndex].LastRank == c.RankOf(c.MyIndex),
					$"[{name}] 편성: season {s.Season} {LeagueRules.TierNames[s.Tier]} from {s.Start:MM-dd HH:mm}");
				Check(LeagueService.StartersFree(s, start + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1)) && !LeagueService.StartersFree(s, s.Start + TimeSpan.FromMinutes(1)),
					$"[{name}] 선발 교체 자유 until the new season's game 1 (SP1 opens it)");
			}
			Tick(start + LeagueCalendar.Cycle + TimeSpan.FromMinutes(21));
			foreach ((uint code, string name, _, _) in teams)
			{
				if (!closed.ContainsKey(code)) continue;
				LeagueState s = store.Load(code);
				Check(s.NextGame == 3 && store.Games(code, s.Season).Count == LeagueRules.Clubs && s.LastGame == 2, $"[{name}] next season: games 1-2 at {s.SlotTime(1):HH:mm} and {s.SlotTime(2):HH:mm}");
			}
			ServerLog.Info($"postseason test: {s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}

		private static void Tick(DateTime t)
		{
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			LeagueService.Tick(t);
			if (sw.ElapsedMilliseconds > 2000) ServerLog.Info($"     tick to {t:MM-dd HH:mm} in {sw.Elapsed.TotalSeconds:F1} s");
		}
	}
}
