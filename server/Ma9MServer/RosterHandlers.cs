using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// Owned players and the league lineup (Scene_Roster).
	public static class RosterHandlers
	{
		// Sum of balls the 25 lineup players may have. The original changed this daily
		// (lobby "Today/Tomorrow"); a fixed cap until that rule is designed.
		public const byte MaxCost = 150;

		// The 1군 ball cap of the team's league tier (LeagueRules.BallCap: 비기너 120 … 메이저 150), what the
		// roster screen checks; MaxCost was sent to every tier (user, 2026-10-19: "비기너인데 150볼까지 된다").
		public static int MaxCostOf(uint userCode)
		{
			int tier = LeagueService.Store?.Load(userCode)?.CapTier ?? 0;  // the next season's from 팀 정비 on
			return LeagueRules.BallCap[Math.Clamp(tier, 0, LeagueRules.BallCap.Length - 1)];
		}

		// Balls of the 1군: every batter slot (bench included) and every pitcher slot, as Roster_DataCenter counts.
		public static int LineupBalls(Lineup l) => l.Batters.Concat(l.Pitchers).Where(i => i != 0).Sum(i => s_catalog.Get(i)?.Ball ?? 0);

		public const short MaxPossess = 60;   // old fixed size; the storage is per team now (MaxPossessOf)

		// The team's player-card storage (보관함): 80 at the start, +20 per 로스터 확장 bought or 로스터 확장권 used, up to ShopHandlers.MaxCapacity (1,400 since 2026-11-02).
		public static short MaxPossessOf(uint userCode) => ShopHandlers.Capacity(userCode);

		// Days on a new contract (real days since 2026-10-19, ContractHandlers.StartDays; was 50 games).
		public const byte StartContract = ContractHandlers.StartDays;

		// PUICardInfoStat is ~70 bytes; keep each packet well under the 32 KB frame limit.
		private const int CardsPerPacket = 150;

		private static Database s_db;

		private static CardCatalog s_catalog;

		public static void Register(GameLoop loop, Database db, CardCatalog catalog)
		{
			s_db = db;
			s_catalog = catalog;
			loop.Register<Ma9MPacket.PKS_CS_ROSTER_PLAYER>(RosterPlayers);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO>(EnterRosterInfo);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_ROSTER_BATTER>(ApplyBatters);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER>(ApplyPitchers);
			loop.Register<Ma9MPacket.PKS_CS_CONDITION_SHUFFLE_GET_ITEM_AMOUNT>(ConditionItemAmount);
		}

		// After the card tables change: drop owned cards that no longer exist, top a team up with
		// the starter cards if it fell short of a full 25, and rebuild lineups that became invalid.
		public static void MigrateCards()
		{
			foreach (uint code in s_db.AllTeamCodes())
			{
				List<TeamCard> cards = s_db.GetCards(code);
				if (cards.Count == 0)
				{
					continue;
				}
				List<int> gone = cards.Where(c => s_catalog.Get(c.Idx) == null).Select(c => c.Idx).ToList();
				if (gone.Count > 0)
				{
					s_db.RemoveCards(code, gone);
					ServerLog.Info($"team {code}: removed {gone.Count} cards missing from the card tables");
				}
				if (cards.Count - gone.Count < 25)
				{
					int added = s_db.AddCards(code, s_catalog.BasicCards, StartContract);
					ServerLog.Info($"team {code}: topped up with {added} starter cards");
				}
				HashSet<int> owned = s_db.GetCards(code).Select(c => c.Idx).ToHashSet();
				Lineup lineup = s_db.GetLineup(code);
				if (lineup == null || !lineup.IsValid(owned))
				{
					s_db.SaveLineup(code, LineupBuilder.Build(LegendLimited(owned), s_catalog));
					ServerLog.Info($"team {code}: lineup rebuilt");
				}
			}
		}

		public static CardCatalog Catalog => s_catalog;

		public static Database Db => s_db;

		// Starter cards + key player for a team that has none yet, and a lineup built from them.
		// A new team: the key players starting at their positions, plus a temporary club from the
		// chosen club's 1-5볼 cards (StarterTeam.DrawClub). Teams that already own cards are left alone.
		public static void GrantStarterTeam(TeamRow team, IList<int> keys, int club)
		{
			if (s_db.GetCards(team.UserCode).Count > 0)
			{
				EnsureRoster(team);
				return;
			}
			List<CardInfo> keyCards = keys.Select(s_catalog.Get).Where(c => c != null).ToList();
			List<CardInfo> roster = StarterTeam.DrawClub(new Random(), s_catalog, club, keyCards);
			s_db.AddCards(team.UserCode, roster.Select(c => c.Idx), StartContract);
			s_db.SaveLineup(team.UserCode, StarterTeam.LineupWith(roster.Select(c => c.Idx), keyCards, s_catalog));
			ServerLog.Info($"team {team.UserCode}: starter club of {roster.Count} cards, {roster.Sum(c => c.Ball)} balls, key players {string.Join(", ", keyCards.Select(c => c.Name))}");
		}

		public static void EnsureRoster(TeamRow team, int keyPlayer = 0)
		{
			List<int> grant = new List<int>(s_catalog.BasicCards);
			if (keyPlayer != 0)
			{
				grant.Add(keyPlayer);
			}
			if (s_db.GetCards(team.UserCode).Count == 0 && grant.Count > 0)
			{
				int added = s_db.AddCards(team.UserCode, grant, StartContract);
				ServerLog.Info($"team {team.UserCode}: granted {added} starter cards");
			}
			if (s_db.GetLineup(team.UserCode) == null)
			{
				s_db.SaveLineup(team.UserCode, LineupBuilder.Build(LegendLimited(s_db.GetCards(team.UserCode).Select(c => c.Idx)), s_catalog));
			}
		}

		private static void RosterPlayers(GameSession session, Ma9MPacket.PKS_CS_ROSTER_PLAYER packet)
		{
			List<TeamCard> cards = s_db.GetCards(session.Team.UserCode);
			DateTime today = DateTime.Now;  // the time, for 컨디션 회복제 (Condition.For)
			for (int start = 0; start == 0 || start < cards.Count; start += CardsPerPacket)
			{
				Ma9MPacket.PKS_SC_ROSTER_PLAYER reply = new Ma9MPacket.PKS_SC_ROSTER_PLAYER();
				reply.needtoClear.n = (byte)(start == 0 ? 1 : 0);
				reply.cardCount.n = (short)cards.Count;
				reply.isEnd.n = (byte)(start + CardsPerPacket >= cards.Count ? 1 : 0);
				reply.players.array = cards.Skip(start).Take(CardsPerPacket).Select(c => ToCardInfoStat(session.Team.UserCode, c, today)).ToArray();
				session.Send(reply);
			}
		}

		private static void EnterRosterInfo(GameSession session, Ma9MPacket.PKS_CS_ENTER_ROSTER_INFO packet)
		{
			Lineup lineup = s_db.GetLineup(session.Team.UserCode);
			Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO reply = new Ma9MPacket.PKS_SC_ENTER_ROSTER_INFO();
			reply.max_cost.n = (byte)MaxCostOf(session.Team.UserCode);
			reply.possess_count.n = (short)s_db.GetCards(session.Team.UserCode).Count;
			reply.max_possess_count.n = MaxPossessOf(session.Team.UserCode);
			// 1 = a league game is coming (the roster's 상대 button opens OpponentLineUp; NextMatchHandlers).
			reply.exist_next_match.n = (byte)(NextMatchHandlers.Next(session.Team.UserCode) != null ? 1 : 0);
			reply.not_changeable_players.array = Array.Empty<PInteger>();
			// 선발 5경기 제한 (SpBlock, 2026-11-09; empty before): the client keeps these out of the starter slots.
			List<(int idx, int left)> spBlocked = SpBlock.ForRoster(session.Team.UserCode,
				s_db.GetCards(session.Team.UserCode).Select(c => c.Idx).Where(i => s_catalog.Get(i) is CardInfo ci && !ci.IsBatter), lineup);
			reply.sp_blocked.array = spBlocked.Select(b => Int(b.idx)).ToArray();
			reply.sp_blocked_count.array = spBlocked.Select(b => Byte((byte)b.left)).ToArray();
			// Cards in 특별수강: the client draws them as 특강 중 and keeps them out of 선수교체 (user, 2026-10-24: they showed
			// as ordinary cards there; Save refuses them anyway).
			reply.lesson_blocked.array = LessonHandlers.InLesson(session.Team.UserCode).Select(Int).ToArray();
			// The client keys slots by array position, so empty slots must not be sent.
			reply.batters.array = lineup.Batters.TakeWhile(i => i != 0).Select(Int).ToArray();
			reply.fielders.array = lineup.Fielders.Select(Byte).ToArray();
			reply.pitchers.array = lineup.Pitchers.TakeWhile(i => i != 0).Select(Int).ToArray();
			// No 선발예정 between seasons (LeagueService.StartersFree): the client then locks no starter (it marks the
			// pitcher whose slot equals next_sp, and 255 matches none). Exhibitions keep NextStarterSlot.
			bool free = LeagueService.Store != null && LeagueService.StartersFree(LeagueService.Store.Load(session.Team.UserCode), DateTime.Now);
			reply.next_sp.n = free ? byte.MaxValue : (byte)NextStarterSlot(session.Team.UserCode, lineup);
			session.Send(reply);
		}

		// The 선발예정 pitcher: the starter of the team's next league game (the rotation follows the league
		// game number, LeagueService.StarterSlot), so the roster band and the lobby's next match agree
		// (user, 2026-10-19: the band stayed on SP1 after game 1). Lineup.NextSp only without a league.
		public static int NextStarterSlot(uint userCode, Lineup lineup)
		{
			LeagueState league = LeagueService.Store?.Load(userCode);
			return league != null ? LeagueService.StarterSlot(lineup, LeagueService.NextRotation(league)) : Math.Min(4, (int)lineup.NextSp);
		}

		private static void ApplyBatters(GameSession session, Ma9MPacket.PKS_CS_APPLY_ROSTER_BATTER packet)
		{
			Lineup lineup = s_db.GetLineup(session.Team.UserCode);
			int[] batters = new int[Lineup.BatterSlots];
			int[] sent = (packet.batters.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			Array.Copy(sent, batters, Math.Min(sent.Length, batters.Length));
			Lineup candidate = new Lineup
			{
				Batters = batters,
				Fielders = (packet.fielders.array ?? Array.Empty<PByte>()).Select(p => p.n).ToArray(),
				Pitchers = lineup.Pitchers,
				NextSp = lineup.NextSp
			};
			Save(session, candidate, new Ma9MPacket.PKS_SC_APPLY_ROSTER_BATTER(), (reply, ok) => reply.result.n = (byte)(ok ? 0 : 1));
		}

		// The lineup before the client's two-step save (batters, then pitchers), for the 팀컬러 changes
		// the pitcher reply reports (systems session, TeamColors.Diff).
		private static readonly Dictionary<uint, Lineup> s_beforeEdit = new Dictionary<uint, Lineup>();

		// The 팀컬러 registered before that save (TeamColors.Registered; 자동 등록 may change it at each step, 2026-11-09).
		private static readonly Dictionary<uint, int[]> s_beforeColours = new Dictionary<uint, int[]>();

		private static void ApplyPitchers(GameSession session, Ma9MPacket.PKS_CS_APPLY_ROSTER_PITCHER packet)
		{
			Lineup lineup = s_db.GetLineup(session.Team.UserCode);
			int[] coloursNow = TeamColors.Registered(session.Team.UserCode);
			Ma9MPacket.PKS_SC_POPUP colourPopup = null;
			int[] pitchers = new int[Lineup.PitcherSlots];
			int[] sent = (packet.pitchers.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			// The client (Roster_Main.SendPK_CS_APPLY_ROSTER_PITCHER) sends an empty list when one of its
			// pitcher slots has no card: that means "pitchers unchanged". Saving it as all zeros wiped a
			// user's whole staff (2026-10-19).
			if (sent.Length == 0)
			{
				pitchers = lineup.Pitchers;
			}
			Array.Copy(sent, pitchers, Math.Min(sent.Length, pitchers.Length));
			Lineup candidate = new Lineup { Batters = lineup.Batters, Fielders = lineup.Fielders, Pitchers = pitchers, NextSp = lineup.NextSp };
			Save(session, candidate, new Ma9MPacket.PKS_SC_APPLY_ROSTER_PITCHER(), (reply, ok) =>
			{
				reply.result.n = (byte)(ok ? 0 : 1);
				Lineup before = s_beforeEdit.TryGetValue(session.Team.UserCode, out Lineup b) ? b : lineup;
				s_beforeEdit.Remove(session.Team.UserCode);
				// 자동 등록 (TeamColors.AfterSave): what changed comes in one popup after this reply, instead of the app's
				// "해제되었습니다" list; the app's "새로운 팀컬러가 활성화되었습니다" stays for new entries left unregistered.
				int[] wasColours = s_beforeColours.TryGetValue(session.Team.UserCode, out int[] w) ? w : coloursNow;
				s_beforeColours.Remove(session.Team.UserCode);
				(Ma9MPacket.PKS_SC_POPUP popup, int[] gained) = ok ? TeamColors.AfterSave(session.Team.UserCode, before, wasColours) : (null, Array.Empty<int>());
				colourPopup = popup;
				reply.disappliedTeamColors.array = Array.Empty<PInteger>();
				reply.newTeamColors.array = gained.Select(Int).ToArray();
			});
			if (colourPopup != null)
			{
				session.Send(colourPopup);
			}
		}

		private static void Save<T>(GameSession session, Lineup candidate, T reply, Action<T, bool> fill) where T : NetworkLibrary.Packet.PKS_PACKET
		{
			HashSet<int> owned = s_db.GetCards(session.Team.UserCode).Select(c => c.Idx).ToHashSet();
			bool ok = candidate.IsValid(owned);
			// 특별수강 (LessonHandlers, 2026-10-22): a card in a lesson cannot go into the 1군.
			HashSet<int> inLesson = LessonHandlers.InLesson(session.Team.UserCode);
			List<int> lessonCards = candidate.Batters.Concat(candidate.Pitchers).Where(inLesson.Contains).Distinct().ToList();
			if (ok && lessonCards.Count > 0)
			{
				ok = false;
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[]
				{
					new PLongString { str = "특별수강 중" },
					new PLongString { str = $"특별수강 중인 선수는 1군에 넣을 수 없습니다.\n{string.Join(", ", lessonCards.Select(i => s_catalog.Get(i)?.Name ?? i.ToString()))}" }
				};
				session.Send(popup);
			}
			// 레전드 카드 (user, 2026-11-08: "로스터별로 투타 레전드 1장씩만 사용 가능"): one 레전드 batter and one 레전드 pitcher at most.
			string legendProblem = ok ? LegendProblem(candidate) : null;
			if (legendProblem != null)
			{
				ok = false;
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[] { new PLongString { str = "레전드 카드" }, new PLongString { str = legendProblem } };
				session.Send(popup);
			}
			// 선발 5경기 제한 (SpBlock, user 2026-11-09): a pitcher taken out of the starters may not go back for 5 league games.
			// The client keeps him out already (sp_blocked); this is for a stale screen.
			Lineup stored = s_db.GetLineup(session.Team.UserCode);
			string spProblem = ok && stored != null ? SpBlock.Problem(session.Team.UserCode, stored, candidate, s_catalog) : null;
			if (spProblem != null)
			{
				ok = false;
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[] { new PLongString { str = "선발 등록 제한" }, new PLongString { str = spProblem } };
				session.Send(popup);
			}
			int balls = LineupBalls(candidate), cap = MaxCostOf(session.Team.UserCode);
			// Over the cap is allowed, as in the original (Roster_Main_BallLimitOver: "볼 제한초과시 선수들의
			// 능력치가 하락됩니다"; the client warns on entering the roster). Refusing it left a user stuck:
			// the client saves batters, then pitchers, so the batter step was checked against the old
			// pitchers and refused without a message (2026-10-19). The stat penalty is still to be set.
			if (ok && balls > cap)
			{
				ServerLog.Info($"[{session.Id}] lineup over the ball cap: {balls} > {cap} (saved)");
			}
			if (ok)
			{
				if (reply is Ma9MPacket.PKS_SC_APPLY_ROSTER_BATTER)
				{
					s_beforeEdit[session.Team.UserCode] = s_db.GetLineup(session.Team.UserCode);
					s_beforeColours[session.Team.UserCode] = TeamColors.Registered(session.Team.UserCode);
				}
				s_db.SaveLineup(session.Team.UserCode, candidate);
				// 선발 5경기 제한: the starters this save took out.
				if (stored != null)
				{
					SpBlock.Record(session.Team.UserCode, stored, candidate, out List<int> spOut);
					if (spOut.Count > 0) ServerLog.Info($"[{session.Id}] 선발 제한 {SpBlock.Games}경기: {string.Join(", ", spOut.Select(i => s_catalog.Get(i)?.Name ?? i.ToString()))}");
				}
				// 팀컬러 bonuses follow the 1군 (TeamColors).
				TeamColors.Refresh(session.Team.UserCode);
			}
			else
			{
				ServerLog.Warn($"[{session.Id}] rejected lineup {candidate.Serialize()}");
			}
			fill(reply, ok);
			session.Send(reply);
		}

		// 레전드 카드 in the 1군 (user, 2026-11-08: "로스터별로 투타 레전드 1장씩만 사용 가능"): at most this many of each.
		public const int MaxLegendBatters = 1, MaxLegendPitchers = 1;

		// Why a lineup breaks the 레전드 limit (null when it does not), for the popup.
		public static string LegendProblem(Lineup l)
		{
			List<CardInfo> legends = l.Batters.Concat(l.Pitchers).Where(i => i != 0).Distinct().Select(i => s_catalog?.Get(i)).Where(c => c != null && c.IsLegend).ToList();
			List<CardInfo> bats = legends.Where(c => c.IsBatter).ToList(), arms = legends.Where(c => !c.IsBatter).ToList();
			if (bats.Count <= MaxLegendBatters && arms.Count <= MaxLegendPitchers) return null;
			return $"1군에는 레전드 타자 {MaxLegendBatters}명, 레전드 투수 {MaxLegendPitchers}명까지 넣을 수 있습니다.\n"
				+ string.Join(", ", (bats.Count > MaxLegendBatters ? bats : new List<CardInfo>()).Concat(arms.Count > MaxLegendPitchers ? arms : new List<CardInfo>()).Select(c => c.Name));
		}

		// The cards with any 레전드 over the limit left out (the higher ball, then Overall, kept), for lineups the server builds.
		public static IEnumerable<int> LegendLimited(IEnumerable<int> cards)
		{
			List<int> list = cards.ToList();
			List<CardInfo> legends = list.Select(i => s_catalog?.Get(i)).Where(c => c != null && c.IsLegend).ToList();
			if (legends.Count == 0) return list;
			HashSet<int> keep = legends.Where(c => c.IsBatter).OrderByDescending(c => c.Ball).ThenByDescending(c => c.Overall).Take(MaxLegendBatters)
				.Concat(legends.Where(c => !c.IsBatter).OrderByDescending(c => c.Ball).ThenByDescending(c => c.Overall).Take(MaxLegendPitchers))
				.Select(c => c.Idx).ToHashSet();
			HashSet<int> drop = legends.Select(c => c.Idx).Where(i => !keep.Contains(i)).ToHashSet();
			return list.Where(i => !drop.Contains(i)).ToList();
		}

		private static void ConditionItemAmount(GameSession session, Ma9MPacket.PKS_CS_CONDITION_SHUFFLE_GET_ITEM_AMOUNT packet)
		{
			// No inventory yet.
			Ma9MPacket.PKS_SC_CONDITION_SHUFFLE_GET_ITEM_AMOUNT reply = new Ma9MPacket.PKS_SC_CONDITION_SHUFFLE_GET_ITEM_AMOUNT();
			session.Send(reply);
		}

		internal static PUICardInfoStat ToCardInfoStat(uint userCode, TeamCard card, DateTime day)
		{
			PUICardInfoStat stat = new PUICardInfoStat();
			stat.ui_info.index.n = card.Idx;
			stat.ui_info.condition.n = Condition.For(userCode, card.Idx, day);
			stat.ui_info.remain_contract.n = card.RemainContract;
			stat.ui_info.position_training_info.n = card.PositionTrainingInfo;
			// The client reads 2 as locked (선수보호) and 1 as unlocked.
			stat.ui_info.IsLock.n = (byte)(card.IsLock ? 2 : 1);
			stat.educated_stat.sbytes = card.Educated;
			stat.contract_stat.sbytes = card.Contract;
			stat.training_stat.sbytes = card.Training;
			// The unused 빙고게임 bonus carries the 코칭스태프 육성스탯 (CoachHandlers.FillBonus); the client adds it into every stat.
			stat.bingogame_stat.sbytes = card.Coach;
			stat.Poten_stat.bytes = new byte[6];
			stat.team_color_stat.sbytes = card.TeamColor;
			return stat;
		}

		private static PInteger Int(int value)
		{
			return new PInteger { n = value };
		}

		private static PByte Byte(byte value)
		{
			return new PByte { n = value };
		}
	}

	// eCondition: HIGH_HIGH 0 (최상), HIGH 1, MIDDLE 2 (보통), LOW 3, LOW_LOW 4 (최하).
	// Each card's condition is a daily chain (user's design, 2026-10-18): from yesterday's level it
	// rarely just stays, often moves one step (a Metropolis step, so the long-run mix holds), or is
	// drawn afresh from 10/20/40/20/10. Every step is a fixed hash of (team, card, day), so the
	// same card shows the same condition all day and nobody can read tomorrow off a table.
	public static class Condition
	{
		private static readonly double[] Mix = { 0.10, 0.20, 0.40, 0.20, 0.10 };

		// Shares of the daily step: keep yesterday's level, move one step, or draw afresh (the rest).
		// Rejected steps and fresh draws that land on the same level add to the stays, so about
		// a third of days repeat yesterday, about half move one step and a fifth move two or more.
		private const double StayShare = 0.0, StepShare = 0.40;

		// Chains start here (or at a re-contract, which begins at 보통).
		private static readonly DateTime Epoch = new DateTime(2026, 1, 1);

		// day: the time of the game or screen. The daily chain uses its date; a 컨디션 회복제 (PlayerManageHandlers,
		// 2026-10-22) takes its steps off while its 24 hours from the use contain that time, so callers pass
		// the time itself (a league game's slot time, DateTime.Now), not only the date.
		public static byte For(uint userCode, int cardIdx, DateTime day, DateTime? restart = null) => Level(userCode, cardIdx, day, restart, true);

		// Without the 회복제 (the 선수관리 screen's 미적용 상태).
		public static byte Base(uint userCode, int cardIdx, DateTime day, DateTime? restart = null) => Level(userCode, cardIdx, day, restart, false);

		private static byte Level(uint userCode, int cardIdx, DateTime day, DateTime? restart, bool items)
		{
			DateTime at = day;
			// Contracts (ContractHandlers): an expired card is fixed at 최하 (the original's rule); a renewal
			// of an expired card restarts the chain at 보통 on its day.
			var contract = restart == null ? ContractHandlers.State(userCode, cardIdx) : null;
			if (contract.HasValue)
			{
				(DateTime until, DateTime? renewed) = contract.Value;
				if (day.Date >= until)
				{
					return 4;
				}
				if (renewed.HasValue && day.Date >= renewed.Value)
				{
					restart = renewed;
				}
			}
			DateTime from = restart ?? Epoch;
			day = day.Date;
			int level = restart.HasValue ? 2 : Draw(Unit(userCode, cardIdx, from, 0));
			for (DateTime d = from.AddDays(1); d <= day; d = d.AddDays(1))
			{
				level = Step(level, Unit(userCode, cardIdx, d, 1), Unit(userCode, cardIdx, d, 2));
			}
			// Test servers only: a card made 나쁨/매우 나쁨 for the 면담실 (CoachHandlers.TestLow, 0 elsewhere).
			level = Math.Max(level, CoachHandlers.TestLow(userCode, cardIdx, at));
			if (items)
			{
				// And a 수석코치 면담's steps for its 12 hours (CoachHandlers, 코칭스태프).
				level = Math.Max(0, level - PlayerManageHandlers.Effect(userCode, cardIdx, at) - CoachHandlers.ConsultSteps(userCode, cardIdx, at));
			}
			return (byte)level;
		}

		private static int Step(int level, double u, double v)
		{
			if (u < StayShare)
			{
				return level;
			}
			if (u < StayShare + StepShare)
			{
				int next = level + (v < 0.5 ? -1 : 1);
				if (next < 0 || next > 4)
				{
					return level;
				}
				// Accept with min(1, mix[next] / mix[level]): keeps the 10/20/40/20/10 mix.
				double accept = Math.Min(1.0, Mix[next] / Mix[level]);
				return (v < 0.5 ? v * 2 : (v - 0.5) * 2) < accept ? next : level;
			}
			return Draw(v);
		}

		private static int Draw(double u)
		{
			double c = 0;
			for (int i = 0; i < Mix.Length; i++)
			{
				c += Mix[i];
				if (u < c)
				{
					return i;
				}
			}
			return Mix.Length - 1;
		}

		// A fixed uniform in [0, 1) per (team, card, day, stream); HashCode.Combine is reseeded per process.
		private static double Unit(uint userCode, int cardIdx, DateTime day, int stream)
		{
			ulong x = userCode * 0x9E3779B97F4A7C15UL ^ (ulong)cardIdx * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(day.Year * 1000 + day.DayOfYear) * 0x165667B19E3779F9UL ^ (ulong)(stream + 1) * 0xD6E8FEB86659FD93UL;
			x ^= x >> 33;
			x *= 0xFF51AFD7ED558CCDUL;
			x ^= x >> 33;
			x *= 0xC4CEB9FE1A85EC53UL;
			x ^= x >> 33;
			return (x >> 11) * (1.0 / (1UL << 53));
		}
	}
}
