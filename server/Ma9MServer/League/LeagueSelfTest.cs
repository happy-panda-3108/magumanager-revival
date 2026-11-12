using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// "--leaguetest <hours>": runs the league clock that many hours ahead on the given data directory
	// (use a copy: it writes games, MP and 홈런볼), then checks the schedule, standings and box scores.
	public static class LeagueSelfTest
	{
		public static int Run(Database db, GameData data, string dbPath, double hours)
		{
			int starterProblems = StarterCheck(data.Catalog);
			FusionHandlers.SelfTest(data.Catalog);

			// The trait store (team traits play in league games); the live server registers it before the league starts.
			TeamPropertyHandlers.Register(new GameLoop(), System.IO.Path.GetDirectoryName(dbPath));
			LeagueService.Init(db, data, dbPath);
			LeagueStore store = LeagueService.Store;
			// The fixed AI clubs of each tier: balls in range, no shared players, 90% trait builds that pass the tree rules.
			int aiProblems = 0;
			for (int tier = 0; tier < LeagueRules.Tiers; tier++)
			{
				if (AiTeams.HasDecks(tier))
				{
					// The team-year decks (챔피언스; 메이저 from 2026-10-29): 25 cards of one team-year each, under the cap, every
					// card with 단일 년도 팀 at the pool's grade (S, 메이저 A) and a 특별수강 when the pool has one, no
					// 재계약/트레이닝, 90% trait builds.
					AiTeams.DeckRule rule = AiTeams.RuleOf(tier);
					List<AiTeam> decks = AiTeams.Decks(tier);
					int points4 = AiTeams.TraitPoints(tier);
					(int bat, int pit) s = TeamColors.SingleYearTeam(rule.Grade);
					int bad = decks.Count(t =>
					{
						Lineup l = Lineup.Parse(t.Lineup);
						List<CardInfo> cards = l.Batters.Concat(l.Pitchers).Where(i => i != 0).Select(data.Catalog.Get).ToList();
						return cards.Count != 25 || cards.Any(c => c == null) || cards.Select(c => (c.Year, c.TeamCode)).Distinct().Count() != 1
							|| t.Ball != cards.Sum(c => c.Ball) || t.Ball > LeagueRules.BallCap[tier] || t.Development.Count != 25
							// Six tiers (2026-11-08): 재계약 Renewals × 3, 트레이닝 TrainSlots × TrainEach, the coaching set on every stat.
							|| t.Development.Values.Any(d => d.Contract.Sum(x => x) != rule.Renewals * 3 || d.Training.Sum(x => x) != rule.TrainSlots * rule.TrainEach
								|| (rule.Lecture ? d.Educated.Sum(x => x) < 8 : d.Educated.Any(x => x != 0))
								|| d.Coach.Any(x => x != (rule.Coach > 0 ? CoachCatalog.SetBonus[rule.Coach] : 0))
								|| d.TeamColor.Any(x => x != (data.Catalog.Get(d.Idx).IsBatter ? s.bat : s.pit)))
							|| t.Traits.Values.Sum(v => v) != points4 || TeamPropertyHandlers.Validate(t.Traits, points4) != null;
					});
					aiProblems += bad + (decks.Count == AiTeams.DeckPool ? 0 : 1);
					ServerLog.Info($"AI decks {LeagueRules.TierNames[tier]} ({decks.Count}, {rule}, 특성 {points4}, 팀컬러 {s.bat}/{s.pit}): {string.Join(", ", decks.Select(t => $"{t.Name} {t.Ball}볼"))}; bad decks {bad}");
					if (!AiTeams.HasClubs(tier)) continue;
				}
				List<AiTeam> clubs = AiTeams.OfTier(tier).ToList();
				List<int> players = clubs.SelectMany(t => { Lineup l = Lineup.Parse(t.Lineup); return l.Batters.Concat(l.Pitchers).Where(i => i != 0); })
					.Select(i => data.Catalog.Get(i)?.Sidx ?? -i).ToList();
				int shared = players.Count - players.Distinct().Count();
				(int lo, int hi) = LeagueRules.AiBalls[tier];
				int badBall = clubs.Count(t => t.Ball < lo || t.Ball > hi);
				int points = AiTeams.TraitPoints(tier);
				int badTraits = clubs.Count(t => t.Traits.Values.Sum(v => v) != points || TeamPropertyHandlers.Validate(t.Traits, points) != null);
				aiProblems += shared + badBall + badTraits;
				double dev = clubs.SelectMany(t => t.Development.Values).Select(d => Enumerable.Range(0, 6).Average(k => d.Contract[k] + d.Training[k])).DefaultIfEmpty(0).Average();
				ServerLog.Info($"AI clubs {LeagueRules.TierNames[tier]} (특성 {points}, 육성 +{dev:F1}): {string.Join(", ", clubs.Select(t => $"{t.Name} {t.Ball}볼"))}; "
					+ $"shared players {shared}, balls out of range {badBall}, bad trait builds {badTraits}");
			}
			// 레전드 AI의 수석코치 면담 (six tiers, 2026-11-08): one 레전드 deck over 30 days of sittings — at most Visitors[grade] a
			// sitting, only 나쁨/매우 나쁨 players, 1-3 steps, and some sittings with visitors.
			if (LeagueRules.NewStructure && AiTeams.Decks(LeagueRules.LegendTier).FirstOrDefault() is AiTeam legendDeck)
			{
				LeagueClub club = new LeagueClub { Name = legendDeck.Name, Owner = 0x8000_0000u | 0x1234u, Lineup = legendDeck.Lineup, AiKey = 0 };
				Lineup ll = Lineup.Parse(legendDeck.Lineup);
				int grade = AiTeams.RuleOf(LeagueRules.LegendTier).Coach, sittings = 0, visited = 0, badConsult = 0, steps = 0;
				for (DateTime t = DateTime.Today.AddHours(1); t < DateTime.Today.AddDays(30); t = t.AddHours(12))
				{
					Dictionary<int, int> up = LeagueService.AiConsult(club, ll, t, grade);
					sittings++;
					if (up.Count > 0) visited++;
					steps += up.Values.Sum();
					badConsult += up.Count(kv => kv.Value < 1 || kv.Value > 3 || Condition.For(club.Owner, kv.Key, t) < 3) + Math.Max(0, up.Count - CoachCatalog.Visitors[grade]);
				}
				aiProblems += badConsult + (visited == 0 ? 1 : 0);
				ServerLog.Info($"AI 면담 ({LeagueRules.TierNames[LeagueRules.LegendTier]}, 코치 등급 {grade}, 한 번에 {CoachCatalog.Visitors[grade]}명): {visited}/{sittings} sittings with visitors, {steps} steps in all, {badConsult} bad");
			}
			ServerLog.Info($"AI clubs: {aiProblems} problem(s)");

			// The shop's purchases need the league store (홈런볼) and the inventory.
			InventoryHandlers.Register(new GameLoop(), db, data.Catalog, System.IO.Path.GetDirectoryName(dbPath));
			RecordHandlers.Register(new GameLoop(), db, data.Catalog);
			// 작전실 store round trips (OperationHandlers) on the first team and its first pitcher.
			OperationHandlers.Register(null, data.Catalog, dbPath);
			uint opUser = store.ActiveTeams().Select(t => t.code).FirstOrDefault();
			int opPitcher = db.GetCards(opUser).Select(c => c.Idx).FirstOrDefault(i => data.Catalog.Get(i) is CardInfo ci && !ci.IsBatter);
			int opProblems = OperationHandlers.SelfTest(opUser, opPitcher);
			ContractHandlers.Register(null, db, data.Catalog, System.IO.Path.GetDirectoryName(dbPath));
			TeamColors.Register(null, db, data.Catalog, System.IO.Path.GetDirectoryName(dbPath));
			Missions.Register(null, db, System.IO.Path.GetDirectoryName(dbPath));
			Achievements.Register(null, db, data.Catalog, System.IO.Path.GetDirectoryName(dbPath));
			// 시즌 완주 보너스: amounts, and a record → pay round trip on a test season number.
			{
				uint bonusUser = store.ActiveTeams().Select(t => t.code).FirstOrDefault();
				// Bonuses the copy already owes (e.g. seasons closed by the 2026-10-21 move to the common
				// calendar) are paid first, so the check sees only the test season's.
				SeasonBonus.Pay(bonusUser);
				int mpBeforeBonus = LeagueService.Balance(bonusUser);
				SeasonBonus.Record(bonusUser, 9999, 0, 1);
				List<string> paidLines = SeasonBonus.Pay(bonusUser);
				int got = LeagueService.Balance(bonusUser) - mpBeforeBonus;
				bool bonusOk = SeasonBonus.Amount(0, 1) == 3_000_000 && SeasonBonus.Amount(3, 1) == 6_000_000 && SeasonBonus.Amount(LeagueRules.ChampionsTier, 1) == (LeagueRules.NewStructure ? 7_800_000 : 7_500_000) && SeasonBonus.Amount(0, 10) == 1_000_000
					&& paidLines.Count == 2 && got == 3_000_000 && SeasonBonus.Pay(bonusUser).Count == 0;
				if (!bonusOk) opProblems++;
				ServerLog.Info($"시즌 완주 보너스: {(bonusOk ? "ok" : "FAIL")} — 비기너 1위 {SeasonBonus.Amount(0, 1):N0}, 4위 {SeasonBonus.Amount(0, 4):N0}, 10위 {SeasonBonus.Amount(0, 10):N0}, 메이저 1위 {SeasonBonus.Amount(3, 1):N0}; 지급 {got:N0} ({string.Join(" ", paidLines)})");
				// Several seasons at once (2026-11-09: five seasons ran out of the popup): the total and the places, every line in
				// the box. Five paid ones on the copy, then twelve varied ones as text only.
				int mpBeforeMany = LeagueService.Balance(bonusUser);
				for (int s = 9990; s <= 9994; s++) SeasonBonus.Record(bonusUser, s, 0, 10);
				List<string> many = SeasonBonus.Pay(bonusUser);
				int gotMany = LeagueService.Balance(bonusUser) - mpBeforeMany;
				List<(int, int, int, int)> varied = Enumerable.Range(1, 12).Select(s => (s, s % 4, s % 3 == 0 ? 1 : 10 - s % 7, SeasonBonus.Amount(s % 4, 1))).ToList();
				List<string> variedLines = SeasonBonus.Lines(varied);
				bool Fits(List<string> l) => l.Count is > 0 and <= 5 && l.All(x => PopupText.Px(x) <= PopupText.Limit);
				bool manyOk = gotMany == 5_000_000 && many.Count == 2 && many[0].Contains("9990~9994시즌") && many[1] == "9990~9994시즌 비기너 10위"
					&& Fits(many) && Fits(variedLines) && Fits(paidLines);
				if (!manyOk) opProblems++;
				ServerLog.Info($"시즌 완주 보너스 여러 시즌: {(manyOk ? "ok" : "FAIL")} — 지급 {gotMany:N0}: {string.Join(" / ", many)}; 12시즌 글 {variedLines.Count}줄 (가장 넓은 {variedLines.Max(PopupText.Px):F0} px): {string.Join(" / ", variedLines)}");
				// The season now under way (systems session, 2026-11-11): two lines after the bonus, every tier's rule in the box.
				List<string> nowLines = SeasonBonus.NowLines(bonusUser, (store.Load(bonusUser)?.Season ?? 1) - 1, 3);
				List<string> rules = Enumerable.Range(0, LeagueRules.TierNames.Length).Select(SeasonBonus.Rule).ToList();
				bool nowOk = nowLines.Count == 2 && Fits(nowLines) && rules.All(x => PopupText.Px(x) <= PopupText.Limit)
					&& SeasonBonus.NowLines(bonusUser, 99999, 3).Count == 0;
				if (!nowOk) opProblems++;
				ServerLog.Info($"새 시즌 안내: {(nowOk ? "ok" : "FAIL")} — {string.Join(" / ", nowLines)}; 등급별 {string.Join(" / ", rules.Select(r => $"{r} ({PopupText.Px(r):F0} px)"))}");
			}
			// 포지션 변경 in the games (2026-10-24): the first team's SP1 changed (a starter to RP, else to SP), as a team card
			// only (no DB write): its stats move at full weight by PositionChangeHandlers.Delta (체력 −ToBullpenHealth /
			// +ToStarterHealth) and the slot it holds is out of role now.
			{
				Lineup pl = db.GetLineup(opUser);
				int pIdx = pl?.Pitchers[0] ?? 0;
				CardInfo pInfo = data.Catalog.Get(pIdx);
				if (pInfo != null && !pInfo.IsBatter)
				{
					bool starter = pInfo.MainPosition == CardPos.SP;
					TeamCard changed = new TeamCard { Idx = pIdx, PositionTrainingInfo = (byte)((starter ? 13 - 11 : 12 - 11) * 10 + PositionChangeHandlers.Success) };
					GameCard plain = data.Card(pIdx, 2), moved = data.Card(pIdx, 2);
					TeamStrength.AddBonuses(moved, changed);
					double[] want = PositionChangeHandlers.Delta((int)pInfo.MainPosition, changed.PositionTrainingInfo).Select(x => (double)x).ToArray();
					bool statsOk = Enumerable.Range(0, 6).All(j => Math.Abs(moved.Row.R[j] - Math.Max(1, plain.Row.R[j] + want[j])) < 1e-9);
					GameTeam before = new GameTeam { StarterSlot = 0 }, after = new GameTeam { StarterSlot = 0 };
					before.Pitchers[0] = data.Card(pIdx, 2);
					after.Pitchers[0] = data.Card(pIdx, 2);
					LineupEffects.Apply(before, pl, data.Catalog);
					LineupEffects.Apply(after, pl, data.Catalog, new[] { changed });
					bool roleOk = PositionChangeHandlers.OrderOf(pInfo, changed.PositionTrainingInfo) == (starter ? 1 : 0)
						&& (starter ? after.Pitchers[0].Condition < before.Pitchers[0].Condition : after.Pitchers[0].Condition > before.Pitchers[0].Condition);
					if (!statsOk || !roleOk) opProblems++;
					ServerLog.Info($"포지션 변경 경기 반영: {(statsOk && roleOk ? "ok" : "FAIL")} — {pInfo.Name} {pInfo.MainPosition} → {(starter ? "RP" : "SP")}, 체력 {plain.Row.R[0]:F0} → {moved.Row.R[0]:F0}, 구속 {plain.Row.R[1]:F1} → {moved.Row.R[1]:F1}; SP1 자리 컨디션 몫 {before.Pitchers[0].Condition:+0.000;-0.000} → {after.Pitchers[0].Condition:+0.000;-0.000}");
				}
			}
			foreach ((uint tcUser, string tcName, _, _) in store.ActiveTeams())
			{
				ServerLog.Info($"팀컬러 {tcName}: {TeamColors.Describe(db.GetLineup(tcUser))}");
				Missions.Backfill(tcUser);  // 업적 지난 기록 (data copy)
				opProblems += TeamColors.SelfTest(tcUser, out string tcLine);
				ServerLog.Info($"팀컬러 {tcName}: {tcLine}");
				Ma9MPackets.Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER opp = NextMatchHandlers.Build(tcUser);
				// No next match between the regular season and the next league for a club out of (or between) the
				// postseason series: the window is empty then (the test failed whenever it ran in those hours).
				bool noNext = store.Load(tcUser) is LeagueState nl && LeagueService.NextGameOf(nl) == null;
				bool oppOk = noNext ? opp.batters.array.Length == 0 && opp.pitchers.array.Length == 0
					: opp.batters.array.Length >= 9 && opp.fielders.array.Length == 9 && opp.pitchers.array.Length == 11;
				opp.ToBytes(new byte[opp.GetSize() + 14], 14);
				if (!oppOk) opProblems++;
				ServerLog.Info($"상대 라인업 {tcName}: {(oppOk ? "ok" : "FAIL")} vs {opp.team_name.str}, 타자 {opp.batters.array.Length} 투수 {opp.pitchers.array.Length} 선발 슬롯 {opp.next_starter.n}");
				// 기록실 순위표 i 버튼: every club of the league by name, and an unknown name.
				if (store.Load(tcUser) is LeagueState tcLeague)
				{
					int clubsOk = 0;
					foreach (LeagueClub club in tcLeague.Clubs)
					{
						Ma9MPackets.Ma9MPacket.PKS_SC_OPPONENT_TEAM_ROSTER r = NextMatchHandlers.ForClub(tcUser, club.Name);
						r.ToBytes(new byte[r.GetSize() + 14], 14);
						if (r.error.error_code.n == 0 && r.team_name.str == club.Name && r.batters.array.Length >= 9 && r.fielders.array.Length == 9 && r.pitchers.array.Length == 11) clubsOk++;
					}
					bool unknownOk = NextMatchHandlers.ForClub(tcUser, "없는 팀").error.error_code.n != 0;
					bool boardOk = clubsOk == tcLeague.Clubs.Count && unknownOk;
					if (!boardOk) opProblems++;
					ServerLog.Info($"순위표 팀 라인업 {tcName}: {(boardOk ? "ok" : "FAIL")} — {clubsOk}/{tcLeague.Clubs.Count}개 팀, 없는 이름 거절 {unknownOk}");
				}
			}
			opProblems += ContractHandlers.SelfTest(opUser);
			// 도전과제: 오늘의 미션 counters and the 업적 list on the data copy.
			opProblems += Missions.SelfTest(opUser, out string missionLine);
			ServerLog.Info(missionLine);
			opProblems += Achievements.SelfTest(opUser, out string achievementLine);
			ServerLog.Info(achievementLine);
			int shopProblems = ShopHandlers.SelfTest(db, data.Catalog, dbPath);
			ServerLog.Info($"shop self-test: {shopProblems} problem(s)");
			DateTime now = DateTime.Now;
			List<(uint code, string name, byte emblem, byte grade)> teams = store.ActiveTeams();
			Dictionary<uint, int> mpBefore = teams.ToDictionary(t => t.code, t => LeagueService.Balance(t.code));
			Dictionary<uint, int> hbBefore = teams.ToDictionary(t => t.code, t => store.HomeRunBalls(t.code));
			Dictionary<uint, (int season, int played)> playedBefore = teams.ToDictionary(t => t.code, t => store.Load(t.code) is LeagueState l0 ? (l0.Season, l0.Played) : (1, 0));
			LeagueService.Tick(now);
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			LeagueService.Tick(now.AddHours(hours));
			ServerLog.Info($"league test: {hours} h caught up in {sw.Elapsed.TotalSeconds:F1} s");
			int problems = 0;
			foreach ((uint code, string name, _, _) in teams)
			{
				LeagueState s = store.Load(code);
				LeagueClub me = s.Clubs[s.MyIndex];
				ServerLog.Info($"[{name}] {LeagueRules.TierNames[s.Tier]} season {s.Season}, game {s.Played}/{LeagueRules.Games}, me #{s.RankOf(s.MyIndex)} {me.W}-{me.L}-{me.D}, "
					+ $"MP +{LeagueService.Balance(code) - mpBefore[code]:N0}, 홈런볼 +{store.HomeRunBalls(code) - hbBefore[code]}, unconfirmed {store.Games(code, s.Season, club: s.MyIndex, unconfirmedOnly: true).Count}");
				// Pay-on-confirm (engine session, 2026-10-19): playing pays nothing; confirming pays each
				// game once; the standings the user sees leave out the unconfirmed games.
				if (playedBefore[code].season == s.Season)
				{
					// Regular-season games only: s.Played stops at 144, so postseason games played (and paid) before the test
					// would count as new in postseason hours.
					List<LeagueGame> mineNew = store.Games(code, s.Season, club: s.MyIndex).Where(g => g.Game > playedBefore[code].played && g.Game <= LeagueRules.Games).ToList();
					List<LeagueGame> pending = store.Games(code, s.Season, club: s.MyIndex, unconfirmedOnly: true);
					int mpPlayed = LeagueService.Balance(code);
					// Due = every unpaid game, also those the copy already held from before the test.
					// Confirming everything also pays games left unconfirmed in earlier seasons (LeagueStore.Confirm
					// has no season filter), so those count too.
					int dueExpected = Enumerable.Range(1, s.Season).Sum(se => store.Games(code, se, unconfirmedOnly: true).Where(g => !g.Paid).Sum(g => g.Mp));
					LeagueState view = store.ConfirmedView(s);
					// The standings count regular-season games only; in postseason hours the pending games include postseason ones.
					List<LeagueGame> pendingRegular = pending.Where(g => g.Game <= LeagueRules.Games).ToList();
					int hidden = pendingRegular.Count == 0 ? 0 : store.Games(code, s.Season, club: s.MyIndex).Count(g => g.Game >= pendingRegular.Min(x => x.Game) && g.Game <= LeagueRules.Games);
					LeagueClub seen = view.Clubs[s.MyIndex];
					bool viewOk = me.W + me.L + me.D - (seen.W + seen.L + seen.D) == hidden;
					(int dueMp, int dueHr) = LeagueService.ConfirmAndPay(code, s.Season, null);
					int paid = LeagueService.Balance(code) - mpPlayed;
					// 오늘의 미션/업적 rewards for the confirmed games come on top (Missions.OnLeagueGames).
					bool payOk = mpPlayed == mpBefore[code] && paid >= dueMp && dueMp == dueExpected && mineNew.All(g => !g.Paid);
					LeagueState after = store.Load(code);
					LeagueClub meAfter = store.ConfirmedView(after).Clubs[after.MyIndex];
					bool afterOk = meAfter.W == me.W && meAfter.L == me.L && meAfter.D == me.D && !store.HasUnconfirmed(code);
					if (!viewOk || !payOk || !afterOk) problems++;
					ServerLog.Info($"   pay on confirm: {(payOk ? "ok" : "FAIL")} (MP while unconfirmed +{mpPlayed - mpBefore[code]:N0}, paid on confirm {dueMp:N0} of {dueExpected:N0} + 도전과제 {paid - dueMp:N0}, 홈런볼 {dueHr}); "
						+ $"shown record {seen.W}-{seen.L}-{seen.D} hides {hidden} game(s) {(viewOk ? "ok" : "FAIL")}; after confirming {(afterOk ? "ok" : "FAIL")}");
				}
				foreach (LeagueClub c in s.Ranked())
				{
					ClubTotals k = c.Totals;
					ServerLog.Info($"   {s.RankOf(c.Index),2}. {c.Name,-8} {c.W,3}-{c.L,3}-{c.D,2}  RS {c.RunsFor,4} RA {c.RunsAgainst,4}  AVG {(k.AB > 0 ? k.H / (double)k.AB : 0):F3} ERA {(k.Outs > 0 ? k.ER * 27.0 / k.Outs : 0):F2} HR {k.HR} {(c.IsUser ? "◀" : "")}");
				}
				// Regular-season games (postseason ones, 145 on, have two clubs a slot and no standings; --posttest).
				List<LeagueGame> games = store.Games(code, s.Season, withBuff: true).Where(g => g.Game <= LeagueRules.Games).ToList();
				// 상대 전적 (LeagueState.Ranked, seasons from KboPctFrom): clubs level in 승률 stand by their games among themselves,
				// recounted here from the stored games.
				if (s.Start >= LeagueState.KboPctFrom)
				{
					List<LeagueClub> order = s.Ranked();
					int levels = 0, wrong = 0;
					foreach (IGrouping<double, LeagueClub> level in order.GroupBy(c => c.W + c.L == 0 ? 0 : c.W / (double)(c.W + c.L)).Where(g => g.Count() > 1))
					{
						levels++;
						HashSet<int> ids = level.Select(c => c.Index).ToHashSet();
						int Net(int i) => games.Where(g => ids.Contains(g.Home) && ids.Contains(g.Away) && (g.Home == i || g.Away == i) && g.HomeScore != g.AwayScore)
							.Sum(g => (g.Home == i) == (g.HomeScore > g.AwayScore) ? 1 : -1);
						List<int> nets = level.Select(c => Net(c.Index)).ToList();
						if (Enumerable.Range(1, nets.Count - 1).Any(k => nets[k] > nets[k - 1])) wrong++;
						ServerLog.Info($"   상대 전적: {string.Join(", ", level.Select((c, k) => $"{c.Name} {c.W}-{c.L} ({nets[k]:+0;-0;0})"))}");
					}
					if (wrong > 0) problems++;
					ServerLog.Info($"   상대 전적 order: {levels} level group(s) {(wrong == 0 ? "ok" : "FAIL")}");
				}
				foreach (IGrouping<int, LeagueGame> slot in games.GroupBy(g => g.Game))
				{
					List<int> clubs = slot.SelectMany(g => new[] { g.Home, g.Away }).ToList();
					if (clubs.Count != LeagueRules.Clubs || clubs.Distinct().Count() != LeagueRules.Clubs) problems++;
				}
				foreach (LeagueGame g in games)
				{
					if (g.Detail.HomeLine.Sum() != g.HomeScore || g.Detail.AwayLine.Sum() != g.AwayScore || g.Buff == null || g.Buff.Length < 10) problems++;
				}
				int played = s.Clubs.Sum(c => c.W + c.L + c.D) / 2;
				if (played != games.Count) problems++;
				// Player lines wait until the user sees each result (LeagueStore.RevealLines, engine session
				// 2026-10-20): none of the still-unconfirmed games may be in them yet; revealed, the card
				// season lines add up to the club totals.
				List<LeagueGame> waiting = store.Games(code, s.Season, club: s.MyIndex, unconfirmedOnly: true)
					.Where(g => (g.Game > playedBefore[code].played || playedBefore[code].season != s.Season) && g.Game <= LeagueRules.Games).ToList();
				if (waiting.Count > 0)
				{
					int newest = waiting.Max(g => g.Game);
					if (newest == s.Played)
					{
						// The newest game is no result for its first 5 minutes (LeagueHandlers.Locked).
						DateTime start = s.SlotTime(s.Played);
						bool lockOk = LeagueHandlers.Locked(s, start.AddMinutes(1))?.Game == s.Played && LeagueHandlers.Locked(s, start.AddMinutes(5)) == null;
						if (!lockOk) problems++;
						ServerLog.Info($"   result lock of game {s.Played}: {(lockOk ? "ok" : "FAIL")} (locked at 1 min, open at 5 min)");
					}
					bool hidden = store.PlayerLines(code, s.Season, s.MyIndex).All(p => store.RecentGames(code, s.Season, p.CardIdx, 1).All(x => x.Game < waiting.Min(g => g.Game)));
					store.RevealLines(code, s.Season);
					bool shown = store.PlayerLines(code, s.Season, s.MyIndex).Any(p => store.RecentGames(code, s.Season, p.CardIdx, 1).Any(x => x.Game == newest));
					if (!hidden || !shown) problems++;
					ServerLog.Info($"   player lines of {waiting.Count} unconfirmed game(s): hidden {(hidden ? "ok" : "FAIL")}, shown after reveal {(shown ? "ok" : "FAIL")}");
				}
				// Card season lines add up to the club totals.
				foreach (LeagueClub c in s.Clubs)
				{
					List<PlayerSeason> lines = store.PlayerLines(code, s.Season, c.Index);
					int h = lines.Where(x => !x.Pitcher).Sum(x => x.H), hr = lines.Where(x => !x.Pitcher).Sum(x => x.HR), w = lines.Where(x => x.Pitcher).Sum(x => x.W);
					int outs = lines.Where(x => x.Pitcher).Sum(x => x.Outs), er = lines.Where(x => x.Pitcher).Sum(x => x.ER);
					if (h != c.Totals.H || hr != c.Totals.HR || w != c.W || outs != c.Totals.Outs || er != c.Totals.ER)
					{
						problems++;
						ServerLog.Warn($"   lines of {c.Name}: H {h}/{c.Totals.H} HR {hr}/{c.Totals.HR} W {w}/{c.W} outs {outs}/{c.Totals.Outs} ER {er}/{c.Totals.ER}");
					}
				}
				// 완봉 (2026-10-30): my club's season rows count the complete games without a run in their per-game lines (earlier
				// games through LeagueStore.BackfillShutouts at Init); 피장타율 and 병살 유도 from the recorder's counts.
				{
					List<PlayerSeason> arms = store.PlayerLines(code, s.Season, s.MyIndex).Where(x => x.Pitcher).ToList();
					int badSho = arms.Count(a => store.RecentGames(code, s.Season, a.CardIdx, 1000).Count(g => g.Line.CG > 0 && g.Line.PR == 0) != a.SHO);
					if (badSho > 0) problems++;
					PlayerSeason ace = arms.OrderByDescending(a => a.SHO).ThenByDescending(a => a.CG).ThenByDescending(a => a.Outs).FirstOrDefault();
					Ma9MPackets.PITCHER_RECORD pr = new Ma9MPackets.PITCHER_RECORD();
					if (ace != null) SeasonStats.FillPitcher(pr, ace, ace.CardIdx);
					ServerLog.Info($"   완봉: {(badSho == 0 ? "ok" : $"FAIL ({badSho} pitchers)")} — club CG {arms.Sum(a => a.CG)}, SHO {arms.Sum(a => a.SHO)}"
						+ (ace == null ? string.Empty : $"; card {ace.CardIdx} 완투(봉) {pr.CG.n}({pr.shutout.n}), 피장타율 {pr.SA.f:F3} over {ace.PAB} AB, 병살 유도 {pr.DP.n}"));
				}
				PlayerSeason best = store.PlayerLines(code, s.Season, s.MyIndex).Where(x => !x.Pitcher).OrderByDescending(x => x.HR).FirstOrDefault();
				if (best != null) ServerLog.Info($"   top HR of my club: card {best.CardIdx} G {best.G} PA {best.PA} H {best.H} HR {best.HR} RBI {best.RBI}; PlayerSeason lookup {(store.PlayerSeason(code, best.CardIdx)?.HR == best.HR ? "ok" : "MISMATCH")}");
				// 최근성적 (per-game rows, newest first; the newest row's rate is the season figure) and 수훈선수 (RecordHandlers).
				PlayerSeason arm = store.PlayerLines(code, s.Season, s.MyIndex).Where(x => x.Pitcher).OrderByDescending(x => x.Outs).FirstOrDefault();
				foreach (PlayerSeason who in new[] { best, arm }.Where(x => x != null))
				{
					List<PlayerGame> recent = store.RecentGames(code, s.Season, who.CardIdx, 5);
					if (recent.Count == 0)
					{
						ServerLog.Info($"   recent games of card {who.CardIdx}: none stored yet");
						continue;
					}
					double rate = who.Pitcher ? (who.Outs > 0 ? who.ER * 27.0 / who.Outs : 0) : (who.AB > 0 ? (double)who.H / who.AB : 0);
					bool order = recent.Zip(recent.Skip(1), (a, b) => a.Game > b.Game).All(x => x);
					bool rateOk = Math.Abs(recent[0].SeasonRate - rate) < 1e-9;
					bool npOk = !who.Pitcher || recent.All(g => g.Line.NP > 0);
					if (recent.Count > 5 || !order || !rateOk || !npOk) problems++;
					ServerLog.Info($"   recent games of card {who.CardIdx} ({(who.Pitcher ? "P" : "B")}): {(order && rateOk && npOk ? "ok" : "FAIL")} "
						+ string.Join(" | ", recent.Select(g => who.Pitcher
							? $"G{g.Game} {g.Line.Outs / 3}.{g.Line.Outs % 3}IP {g.Line.PH}H {g.Line.PSO}K {g.Line.ER}ER NP{g.Line.NP} ERA {g.SeasonRate:F2}"
							: $"G{g.Game} {g.Line.H}/{g.Line.AB} HR{g.Line.HR} RBI{g.Line.RBI} AVG {g.SeasonRate:F3}")));
				}
				List<(int idx, int value)> mvp = RecordHandlers.Picks(code, out string mvpNote);
				List<(int idx, int value)> saber = RecordHandlers.SaberPicks(code);
				if (mvp.Count != 10 || saber.Count != 10) problems++;
				ServerLog.Info($"   수훈선수 ({mvpNote}): {(mvp.Count == 10 ? "ok" : "FAIL")} " + string.Join(", ", mvp.Select(x => $"{x.idx}={x.value}")));
				ServerLog.Info($"   수훈선수 2쪽 (출루 장타 OPS wRC+ WAR / K9 KBB WHIP FIP bWAR): {(saber.Count == 10 ? "ok" : "FAIL")} " + string.Join(", ", saber.Select(x => $"{x.idx}={x.value}")));
				// 기록실 > 선수기록 and 순위표 > 선수순위: shapes the client indexes blindly, and both serialize.
				Ma9MPackets.Ma9MPacket.PKS_SC_ENTER_PLAYERS_RECORD rec = RecordHandlers.BuildPlayersRecord(code);
				Ma9MPackets.Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK rank = RecordHandlers.BuildPlayerRank(code);
				List<(int club, int idx, int value)> leaders = RecordHandlers.LeagueLeaders(s, code);
				bool recOk = rec.batter_record.array.Length > 0 && rec.pitcher_record.array.Length == rec.pitcher_position_training_info.array.Length
					&& rank != null && rank.players.array.Length == 45 && rank.players.array.All(x => x.user_num.n < rank.team_name.array.Length) && leaders.All(x => x.idx != 0);
				try
				{
					rec.ToBytes(new byte[rec.GetSize() + 14], 14);
					rank?.ToBytes(new byte[rank.GetSize() + 14], 14);
				}
				catch (Exception e)
				{
					recOk = false;
					ServerLog.Warn($"   record packets do not serialize: {e.Message}");
				}
				if (!recOk) problems++;
				ServerLog.Info($"   선수기록 {rec.batter_record.array.Length} batters / {rec.pitcher_record.array.Length} pitchers, 선수순위 {rank?.players.array.Length}: {(recOk ? "ok" : "FAIL")}; "
					+ $"타율 1위 {s.Clubs[leaders[0].club].Name} {leaders[0].idx} {leaders[0].value / 1000.0:F3}, 홈런 1위 {s.Clubs[leaders[3].club].Name} {leaders[3].idx} {leaders[3].value}, "
					+ $"방어율 1위 {s.Clubs[leaders[15].club].Name} {leaders[15].idx} {leaders[15].value / 1000.0:F2}, 세이브 1위 {leaders[24].value}");
				// 상세기록 최고/최악: the club's most frequent BEST card shows its count in the card window.
				(Dictionary<int, int> bestCounts, _) = RecordHandlers.BestWorst(code, s, s.MyIndex);
				if (bestCounts.Count > 0)
				{
					KeyValuePair<int, int> top = bestCounts.OrderByDescending(kv => kv.Value).First();
					PlayerSeason topLine = LeagueService.Store.PlayerLines(code, s.Season, s.MyIndex).FirstOrDefault(p => p.CardIdx == top.Key);
					Ma9MPackets.BATTER_RECORD bRec = new Ma9MPackets.BATTER_RECORD();
					Ma9MPackets.PITCHER_RECORD pRec = new Ma9MPackets.PITCHER_RECORD();
					RecordHandlers.FillBestWorst(code, topLine, bRec, pRec);
					int shown = topLine == null ? -1 : topLine.Pitcher ? pRec.best.n : bRec.best.n;
					bool bwOk = shown == Math.Min(255, top.Value);
					if (!bwOk) problems++;
					ServerLog.Info($"   상세기록 BEST {(bwOk ? "ok" : "FAIL")}: card {top.Key} {top.Value}회, 카드 창 {shown}");
				}
				LeagueGame sample = games.LastOrDefault(g => g.Home == s.MyIndex || g.Away == s.MyIndex);
				if (sample != null)
				{
					ServerLog.Info($"   last game {sample.Game}: {s.Clubs[sample.Away].Name} {string.Join("", sample.Detail.AwayLine)} = {sample.AwayScore} / {s.Clubs[sample.Home].Name} {string.Join("", sample.Detail.HomeLine)} = {sample.HomeScore}, "
						+ $"H {sample.Detail.Stats[1][0]}-{sample.Detail.Stats[0][0]}, MP {sample.Mp}, HR {sample.HomeRuns}, buff {sample.Buff.Length} B");
				}
			}
			// 리그 시상 (LeagueAwards): the test account's (오토테스트) season as if it ended now, paid once (data copy).
			uint awardUser = store.ActiveTeams().OrderByDescending(t => t.name == "오토테스트").Select(t => t.code).FirstOrDefault();
			if (awardUser != 0)
			{
				problems += LeagueAwards.SelfTest(db, dbPath, awardUser, out string awardLine);
				ServerLog.Info(awardLine);
			}
			// 팀 특성 강등 순서 (2026-11-08): the last points put in come off first, skipping one that would break the tree.
			if (awardUser != 0) problems += TeamPropertyHandlers.OrderSelfTest(awardUser);
			// 선발 5경기 제한 (2026-11-09): the count, a block from a save, the refusal, the roster list, the 특강 exemption.
			problems += SpBlock.SelfTest(db, data.Catalog);
			// The AI clubs' and decks' checks above (counted since 2026-11-08).
			problems += aiProblems;
			// 피장타율's counts (LeagueRecorder.Against) matched the batters' lines in every game played.
			if (LeagueService.AgainstMismatches > 0) problems++;
			ServerLog.Info($"피장타율 counts: {(LeagueService.AgainstMismatches == 0 ? "ok" : $"FAIL ({LeagueService.AgainstMismatches} game sides)")}");
			ServerLog.Info($"league test: {problems} problem(s), starter clubs: {starterProblems} problem(s)");
			return problems + starterProblems + opProblems == 0 ? 0 : 1;
		}

		// New teams: random 8볼 레어 key players (a batter and a pitcher) and the club drawn around them.
		private static int StarterCheck(CardCatalog catalog)
		{
			Random rnd = new Random(5);
			int bad = 0, coloured = 0;
			List<int> sums = new List<int>();
			for (int t = 0; t < 200; t++)
			{
				int club = StarterTeam.Clubs[t % StarterTeam.Clubs.Length];
				HashSet<int> codes = new HashSet<int>(StarterTeam.ClubCodes(club));
				List<CardInfo> rareBats = catalog.All.Where(c => c.IsBatter && c.Ball == 8 && c.Grade == 3 && codes.Contains(c.TeamCode)).ToList();
				List<CardInfo> rareArms = catalog.All.Where(c => !c.IsBatter && c.Ball == 8 && c.Grade == 3 && codes.Contains(c.TeamCode)).ToList();
				List<CardInfo> keys = new List<CardInfo> { rareBats[rnd.Next(rareBats.Count)], rareArms[rnd.Next(rareArms.Count)] };
				int cap = LeagueRules.BallCap[0];
				List<CardInfo> roster = StarterTeam.DrawClub(rnd, catalog, club, keys);
				int fives = roster.Count(c => c.Ball == 5), others = roster.Count(c => !codes.Contains(c.TeamCode));
				if (t < 10) ServerLog.Info($"club {club}: {roster.Count} cards, {roster.Sum(c => c.Ball)} balls, 5볼 {fives}, other clubs {others}, balls {string.Join(" ", roster.Select(c => c.Ball))}");
				Lineup l = StarterTeam.LineupWith(roster.Select(c => c.Idx), keys, catalog);
				int sum = roster.Sum(c => c.Ball);
				sums.Add(sum);
				bool batterStarts = Array.IndexOf(l.Batters, keys[0].Idx) is int b && b >= 0 && b < 9;
				bool pitcherIn = Array.IndexOf(l.Pitchers, keys[1].Idx) >= 0;
				HashSet<int> idx = roster.Select(c => c.Idx).ToHashSet();
				bool valid = l.IsValid(idx) && roster.Count == 25 && roster.Select(c => c.Sidx).Distinct().Count() == 25;
				// 2026-11-12: random clubs and seasons, no team colour (StarterTeam.DrawClub).
				bool colour = TeamColors.AnyAvailable(roster.Where(c => c.IsBatter).ToList(), roster.Where(c => !c.IsBatter).ToList());
				if (colour) coloured++;
				if (sum > cap || !batterStarts || !pitcherIn || !valid || fives < 2 || fives > 3 || colour)
				{
					bad++;
					if (bad <= 3) ServerLog.Warn($"starter club: sum {sum}, batter starts {batterStarts}, pitcher in {pitcherIn}, valid {valid} ({keys[0].Name} {keys[0].MainPosition}, {keys[1].Name} {keys[1].MainPosition})");
				}
			}
			ServerLog.Info($"starter clubs: 200 drawn, balls {sums.Min()}-{sums.Max()} (mean {sums.Average():F1}), {coloured} with a team colour, {bad} bad");
			return bad;
		}
	}
}
