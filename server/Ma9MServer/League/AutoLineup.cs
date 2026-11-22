using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 자동 편성 (engine session, 2026-11-11; user 18:4x 10번: "볼 상한 안에서 가장 강한 라인업을 짜는 기능도 요즘 매니징 게임의 특징이라
	// 구현을 해보고 싶긴 한데, 난이도가 있지 않을까 싶음. 일단 진행해봅시다"; keeping the team colour or not: the kt AI tester's
	// idea): the strongest 1군 of the club's own cards under its ball cap, as Ma9MSim.TeamValue counts it against its league.
	// - A 1군 is valued as league games build it, at 보통 condition (11-13; before: today's), development (교육·계약·트레이닝·코치), the team colour the
	//   자동 등록 would give it (TeamColors.AutoBonuses), lineup effects (선수분석 graph, out of position / role), the league
	//   reference of now (TeamStrength.LeagueShift), the club's 팀 특성; against the league's typical batter and pitcher.
	// - Rules: the cap (RosterHandlers.MaxCostOf), no card in a 특별수강 (LessonHandlers.InLesson), one 레전드 batter and one
	//   레전드 pitcher, one card of a player, no starter the 선발 5경기 제한 holds (SpBlock), fielders where they can field.
	// - Search: from the 1군 of now, from the best 1군 by each card's own value (λ·balls under the cap, as TeamColorSim.Build),
	//   and (unless the team colour is to be kept) from 1군s built round the biggest single-season and decade groups of the
	//   cards; each improved by single changes (a card in for one out, two slots swapped) valued whole, then the batting order
	//   by the 선수분석 graph (LineupBuilder.BestOrder). keepColour: every team colour working now must stay (same group, same or
	//   better grade).
	// Packets and the screen are the systems session's; AutoLineup is what they call. Dev: --autolineup <team name> [keep].
	public static partial class LeagueService
	{
		public sealed class AutoLineupResult
		{
			public Lineup Lineup, Current;
			public double Runs, CurrentRuns;   // TeamValue runs a game (with the team colour each would get)
			public double Gain => (Runs - CurrentRuns) * TeamValue.WinPerRun;
			public int Balls, CurrentBalls, Cap, Evaluations;
			public int[] Colours = new int[3], CurrentColours = new int[3];   // the 자동 등록's entries
			public List<int> StartersOut = new List<int>();                   // starters of now left out (선발 5경기 제한 follows)
			public TeamValue.Parts Parts, CurrentParts;
			public string Start = "";                                          // which start won
			public List<int> Locked = new List<int>();                         // 선수보호 cards of the 1군 kept where they were
			// 2026-11-12 (engine): without keep, the best 1군 that keeps today's registered team colours too (null when none is
			// registered or the 1군 of now breaks it), and whether the advice keeps them; Plans = team colour plans built.
			public Lineup Kept;
			public double KeptRuns;
			public double KeptGain => Kept == null ? 0 : (KeptRuns - CurrentRuns) * TeamValue.WinPerRun;
			public TeamValue.Parts KeptParts;
			public bool KeepsColour;
			public int Plans;
			// Win shares in full games against the league (LineupSimCheck, SimGames home games a club, the same seeds for each
			// 1군): TeamValue's linear runs run well past the games far from the league mean (rookie decks: +29%p for a real
			// +10), so the screen should tell these. NaN when not played (no change, or simGames 0).
			public double SimNow = double.NaN, SimAdvice = double.NaN, SimKept = double.NaN;
			public double SimSe, SimKeptSe;   // standard error of SimAdvice − SimNow and SimKept − SimNow (paired games)
			public int SimGames;
			// Whether the games tell the advice from the 1군 of now (its gain beyond twice its standard error).
			public bool SimClear => SimGames > 0 && SimAdvice - SimNow > 2 * SimSe;
			// 포지션 엄수 (engine 2026-11-16, StrictPositions): the 1군 of now's cards out of position (a fielder who cannot field his
			// spot, a pitcher out of his role; 선수 고정 cards left out), and why no 1군 in position could be built (null when one
			// was; then Lineup is the 1군 of now).
			public List<int> CurrentMisplaced = new List<int>();
			public string NoFit;
			// (engine 2026-11-17, AI tester 쌍방울 B21) The 1군 of now's cards whose contract has ended (RemainContract 0: condition
			// fixed at 최하): no 1군 of the advice takes them; a 선수 고정 card stays where the user put it.
			public List<int> LeftOutExpired = new List<int>();
			// (engine 2026-11-17, AI tester LG 96) The advice the search found when the games called it worse than the 1군 of now
			// (Lineup is then the 1군 of now), and its games' win share; null / NaN when not turned down.
			public Lineup TurnedDown;
			public double TurnedDownSim = double.NaN;
			// (engine 2026-11-20, AI 테스터 두산 39·쌍방울·LG·kt, 남은일 4번 "새 FA를 안 넣은 까닭 표시") FA 선수카드 the user holds
			// that the 1군 of the advice leaves out, each with why (one line, without the card's own name; FaReason). A swap of
			// one into the advice that keeps the rules and adds is taken into the advice instead (FaPass).
			public List<(int idx, string why)> FaLeftOut = new List<(int, string)>();
		}

		// Full games a club for AutoLineupResult.Sim* (2026-11-12: 100 → about 1.5 s for three 1군s in a league of eight on 3 threads).
		public const int AutoLineupSimGames = 100;

		// 자동 편성's team colour plans: how many are built under the cap and valued whole, and how many of the best improved.
		private const int PlanBuild = 16, PlanImprove = 4;

		private sealed class LineupContext
		{
			public LeagueState S;
			public int Me, Other, Cap, PenaltyCap;
			public GameTeam[] Teams;     // the league after the reference and traits
			public (double[] bat, double[] pit, double def) Shift;
			public TraitEffects.Effect Traits;
			public BatterTalent OppBat;
			public PitcherTalent OppPit;
			public Dictionary<int, TeamCard> Owned;
			public Dictionary<int, byte> Cond;
			public Dictionary<int, CardInfo> Info;
			public HashSet<int> Blocked;   // may not start (선발 5경기 제한)
			public HashSet<int> Expired = new HashSet<int>();   // contract ended (not 선수 고정): no candidates

			public Lineup Current;
			public int Evaluations;
			// 선수 고정 (systems session, user 11-12 10:4x: "선수 고정을 시켜 놓으면 그 선수는 자동 편성에 해당되지 않는걸로 …
			// 선수보호 카드를 기준으로"): the 1군's 선수보호 (IsLock) cards stay — a pitcher in his slot, a batter at his field position
			// (the batting order may change) or on the bench. pos: 0-7 field, 8 DH, -1 bench / a pitcher.
			public List<(int idx, bool pit, int slot, int pos)> Locked = new List<(int, bool, int, int)>();
			public HashSet<int> LockedIdx = new HashSet<int>();
			// Cards' own values by team colour points (ValueOf).
			public Dictionary<(int idx, int colour), CardValue> ValueCache = new Dictionary<(int, int), CardValue>();
			public double BaseBat = double.NaN, BasePit;
			public double LastLambda;   // the ball price of the last Greedy under the cap
			// The season's 공인구 (SeasonBall; engine 11-15): every evaluation plays it, as the league does (BallEffect).
			public double Cor = SeasonBall.StandardCor;
		}

		private static LineupContext Context(uint userCode)
		{
			LeagueState s = s_store?.Load(userCode);
			if (s == null || s.Clubs.Count == 0) return null;
			int me = s.MyIndex;
			int game = Math.Clamp(s.NextGame, 1, 144);
			var (raw, lineups, traits) = Teams(s, game, _ => game, raw: true);
			LineupContext x = new LineupContext { S = s, Me = me, Other = Enumerable.Range(0, raw.Length).First(i => i != me), Traits = traits[me], Cor = BallEffect.CorOf(s) };
			x.Shift = TeamStrength.LeagueShift(raw);
			x.Teams = raw.Select(CopyTeam).ToArray();
			for (int i = 0; i < x.Teams.Length; i++)
			{
				TeamStrength.ApplyShift(x.Teams[i], x.Shift);
				TraitEffects.Raise(x.Teams[i], traits[i]);
			}
			(x.OppBat, x.OppPit) = TypicalOpponents(x.Teams, traits, me, x.Cor);
			x.Current = lineups[me];
			LeagueClub club = s.Clubs[me];
			List<TeamCard> owned = s_db.GetCards(userCode);
			x.Owned = owned.ToDictionary(c => c.Idx);
			// Condition: none (user 11-13 16:5x: "자동 편성에서는 컨디션 반영 없이 순수 스탯 싸움으로 이 선수가 더 승률이 좋냐 그걸 따져서
			// 라인업을 만들기 바람. 그 날 그 날 선수가 달라지면 좀 이상할 것 같음") — every card at 보통 (an empty x.Cond reads 2), the league's
			// clubs too (Teams raw). Before: today's Condition.For.
			x.Cond = new Dictionary<int, byte>();
			HashSet<int> lesson = LessonHandlers.InLesson(userCode);
			// (11-17, AI tester 쌍방울 B21: the advice put two 9볼 starters whose contracts had ended into the 1군 and valued them at
			// 보통; they play at 최하 until renewed) A card whose contract has ended is no candidate — the 컨디션-free rule above is
			// about the day's condition, not this fixed one. 선수 고정 cards stay (the user's choice).
			x.Expired = owned.Where(c => c.RemainContract == 0 && !c.IsLock).Select(c => c.Idx).ToHashSet();
			// And the 1군 of now is valued with them at 최하 (LOW_LOW 4), as it plays, so the advice is told against what it replaces
			// (at 보통 the 1군 of now looked 3.5%p better than an advice without them). 선수 고정 ones too.
			foreach (TeamCard c in owned.Where(c => c.RemainContract == 0)) x.Cond[c.Idx] = 4;
			x.Info = owned.Where(c => !lesson.Contains(c.Idx) && !x.Expired.Contains(c.Idx)).Select(c => s_data.Catalog.Get(c.Idx)).Where(c => c != null).ToDictionary(c => c.Idx);
			x.Blocked = SpBlock.ForRoster(userCode, x.Info.Values.Where(c => !c.IsBatter).Select(c => c.Idx), x.Current).Select(b => b.idx).ToHashSet();
			x.Cap = RosterHandlers.MaxCostOf(userCode);
			x.PenaltyCap = LeagueRules.BallCap[s.Tier];
			// Dev (engine, 2026-11-12): AUTOLINEUP_NOLOCK=1 leaves the locks out (the user's own clubs lock the whole 1군; to time
			// and check the search on big collections in a data copy).
			bool noLock = Environment.GetEnvironmentVariable("AUTOLINEUP_NOLOCK") == "1";
			bool Locks(int idx) => !noLock && idx != 0 && x.Owned.TryGetValue(idx, out TeamCard tc) && tc.IsLock && x.Info.ContainsKey(idx) && x.LockedIdx.Add(idx);
			for (int k = 0; x.Current != null && k < x.Current.Batters.Length; k++)
			{
				if (Locks(x.Current.Batters[k])) x.Locked.Add((x.Current.Batters[k], false, k, PositionOf(x.Current, k)));
			}
			for (int k = 0; x.Current != null && k < x.Current.Pitchers.Length; k++)
			{
				if (Locks(x.Current.Pitchers[k])) x.Locked.Add((x.Current.Pitchers[k], true, k, -1));
			}
			return x;
		}

		// The league's typical batter and pitcher as one club meets them (each club's traits on its own side).
		private static (BatterTalent bat, PitcherTalent pit) TypicalOpponents(GameTeam[] teams, TraitEffects.Effect[] traits, int me, double cor)
		{
			GameEnv env = s_data.Env;
			List<BatterTalent> bats = new List<BatterTalent>();
			List<PitcherTalent> pits = new List<PitcherTalent>();
			for (int j = 0; j < teams.Length; j++)
			{
				if (j == me) continue;
				SeasonModel mj = CardGame.Build(teams[me], teams[j], env);
				TraitEffects.Apply(mj, 1, traits[j]);
				BallEffect.Apply(mj, cor);
				bats.Add(TeamValue.TypicalBatter(mj, 1));
				PitcherTalent p = TeamValue.TypicalPitcher(mj, 1);
				if (traits[j] != null) p.Xbh *= traits[j].PXBH;
				pits.Add(p);
			}
			BatterTalent b = new BatterTalent
			{
				K = bats.Average(t => t.K), BB = bats.Average(t => t.BB), HBP = bats.Average(t => t.HBP), HR = bats.Average(t => t.HR), Babip = bats.Average(t => t.Babip),
				Double = bats.Average(t => t.Double), Triple = bats.Average(t => t.Triple), Steal = bats.Average(t => t.Steal), Speed = bats.Average(t => t.Speed)
			};
			PitcherTalent q = new PitcherTalent
			{
				K = pits.Average(t => t.K), BB = pits.Average(t => t.BB), HBP = pits.Average(t => t.HBP), HR = pits.Average(t => t.HR), Babip = pits.Average(t => t.Babip), Xbh = pits.Average(t => t.Xbh)
			};
			return (b, q);
		}

		// The club's team for a 1군 as a league game builds it, with the team colour that 1군 would get.
		private static GameTeam BuildTeam(LineupContext x, Lineup l, out int[] colours)
		{
			(Dictionary<int, int> bonus, int[] entries) = TeamColors.AutoBonuses(l);
			colours = entries;
			List<TeamCard> cards = new List<TeamCard>();
			foreach (int idx in l.Batters.Concat(l.Pitchers).Where(i => i != 0).Distinct())
			{
				if (!x.Owned.TryGetValue(idx, out TeamCard c)) continue;
				sbyte v = (sbyte)Math.Clamp(bonus.GetValueOrDefault(idx), 0, 100);
				cards.Add(new TeamCard
				{
					Idx = c.Idx, RemainContract = c.RemainContract, PositionTrainingInfo = c.PositionTrainingInfo, IsLock = c.IsLock,
					Educated = c.Educated, Contract = c.Contract, Training = c.Training, Coach = c.Coach, TeamColor = Enumerable.Repeat(v, 6).ToArray(),
					ContractUntil = c.ContractUntil, ContractCount = c.ContractCount
				});
			}
			GameTeam t = new GameTeam { Name = x.S.Clubs[x.Me].Name };
			for (int pos = 0; pos < Lineup.FielderSlots; pos++) t.Position[l.Fielders[pos] - 1] = pos;
			byte Cond(int idx) => x.Cond.TryGetValue(idx, out byte c) ? c : (byte)2;
			for (int slot = 0; slot < 9; slot++) t.Order[slot] = s_data.Card(l.Batters[slot], Cond(l.Batters[slot]));
			for (int s = 0; s < Lineup.PitcherSlots; s++) t.Pitchers[s] = l.Pitchers[s] != 0 ? s_data.Card(l.Pitchers[s], Cond(l.Pitchers[s])) : null;
			LineupEffects.Apply(t, l, s_data.Catalog, cards);
			// LineupEffects scales today's starter (slot 0 here) and the bullpen; SP2-5 as each would be on his own day (a
			// reliever starting −15%, his graph 40), so TeamValue's one-in-five of each is the starter that pitches.
			for (int s = 1; s < 5; s++)
			{
				GameCard g = t.Pitchers[s];
				CardInfo p = s_data.Catalog.Get(l.Pitchers[s]);
				if (g == null || p == null) continue;
				TeamCard own = cards.FirstOrDefault(c => c.Idx == p.Idx);
				int order = own != null && own.PositionTrainingInfo % 10 != 0 ? PositionChangeHandlers.OrderOf(p, own.PositionTrainingInfo) : p.PreferredOrder;
				double share = LineupEffects.FromGraph(LineupEffects.PitcherGraph(0, order));
				if (!LineupEffects.InRole(0, order)) share = (1 + share) * (1 + LineupEffects.OutOfRole) - 1;
				if (share != 0) g.Condition = (1 + g.Condition) * (1 + share) - 1;
			}
			TeamStrength.AddBonuses(t, l, cards);
			if (OverCapPenalty && RosterHandlers.LineupBalls(l) > x.PenaltyCap)
			{
				foreach (GameCard g in t.Order.Concat(t.Pitchers).Where(g => g != null)) g.Condition = (1 + g.Condition) * OverCapStrength - 1;
			}
			TeamStrength.ApplyShift(t, x.Shift);
			TraitEffects.Raise(t, x.Traits);
			return t;
		}

		private static TeamValue.Parts Evaluate(LineupContext x, Lineup l, out int[] colours)
		{
			x.Evaluations++;
			GameTeam t = BuildTeam(x, l, out colours);
			SeasonModel m = CardGame.Build(t, x.Teams[x.Other], s_data.Env);
			TraitEffects.Effect e = x.Traits ?? new TraitEffects.Effect();
			TraitEffects.Apply(m, 0, new TraitEffects.Effect { HR = e.HR, K = e.K, BB = e.BB, XBH = e.XBH, PK = e.PK, PBabip = e.PBabip, PHR = e.PHR, StartLimit = e.StartLimit });
			BallEffect.Apply(m, x.Cor);   // x.OppBat / x.OppPit carry it already (TypicalOpponents)
			BatterTalent ob = new BatterTalent
			{
				K = x.OppBat.K, BB = x.OppBat.BB, HBP = x.OppBat.HBP, HR = x.OppBat.HR, Babip = x.OppBat.Babip, Steal = x.OppBat.Steal, Speed = x.OppBat.Speed,
				Double = Math.Clamp(x.OppBat.Double * e.PXBH, 0.03, 0.5), Triple = Math.Clamp(x.OppBat.Triple * e.PXBH, 0.0005, 0.15)
			};
			return TeamValue.Of(m, 0, e.StealTry, e.StealCaught, e.PStealTry, e.PCaught, oppBat: ob, oppPit: x.OppPit);
		}

		// Each card's own value without the team colour or the graph (to pick starts and to try the likely changes first):
		// a batter's runs a game in an average slot and his defence by position, a pitcher's runs saved a batter faced.
		private sealed class CardValue
		{
			public CardInfo Info;
			public double Off, PerBf, Stamina;
			public double[] Def = new double[8];
			// (11-14) A pitcher's runs saved a batter faced in each kind of slot — 0 starter, 1 RP1-4, 2 SU, 3 CP — with the
			// lineup effect of that slot for his role (LineupEffects: graph 80 in role, a starter in relief or a reliever
			// starting graph 40/60 and −15%), as a league game plays him.
			public double[] PerBfRole = new double[4];
			// A pitcher's role now (PitcherOrder: 0 선발, 1 중계, 2 셋업, 3 마무리); −1 for a batter.
			public int Order = -1;
		}

		// The LineupEffects view of each kind of pitcher slot (PerBfRole) and the kind of pitching slot k.
		private static readonly int[] RoleViews = { 0, 1, 5, 6 };

		private static int RoleOfSlot(int k) => k < 5 ? 0 : k < 9 ? 1 : k == 9 ? 2 : 3;

		// Dev (11-14): MA9M_AUTOLINEUP_ROLEVALUE=0 values pitchers as before (one PerBf for every slot, no role effect in the
		// cards' own values; the whole-1군 evaluation always had it) — to compare on a data copy.
		private static readonly bool RoleValue = Environment.GetEnvironmentVariable("MA9M_AUTOLINEUP_ROLEVALUE") != "0";

		// 포지션 엄수 (user 2026-11-16 18:3x: "자동편성 시 포지션이 안 맞게 편성을 절대 하지 말 것. 무조건 포지션에 맞게 하고, 타자도 해당
		// 포지션에 멀티 포지션이 있어서 수비 가능한게 아니면 넣지 말 것. 지명타자 자리에는 어떤 포지션이든지 들어갈 수 있음, 수비를 안 하기
		// 때문임." — the AI tester 쌍방울 B20 got RP cards in three of the five starter slots): a fielder only where he can field (main
		// or multi position, CardInfo.CanField — where a game plays him without LineupEffects.OutOfPosition), a pitcher only in his
		// role (RP1-4 중계, SU 셋업, CP 마무리, SP1-5 선발; after 포지션 변경 the changed one), the DH and the bench anyone. A bullpen slot
		// with nobody of its role stays empty. 선수 고정 cards stay where the user put them. Dev: MA9M_AUTOLINEUP_STRICT=0 as before.
		private static readonly bool StrictPositions = Environment.GetEnvironmentVariable("MA9M_AUTOLINEUP_STRICT") != "0";

		// Runs a game off a card's slot value out of position (SlotValue): never chosen while a card in position is there.
		private const double Misfit = -1000;

		// The LineupEffects view of pitcher slot k (0 starter, 1-4 RP1-4, 5 SU, 6 CP).
		private static int ViewOfSlot(int k) => k < 5 ? 0 : k - 4;

		private static bool FitsSlot(CardValue c, int k) => c.Order < 0 || LineupEffects.InRole(ViewOfSlot(k), c.Order);

		private static bool FitsField(CardValue c, int pos) => pos < 0 || pos >= 8 || !double.IsNaN(c.Def[pos]);

		// A pitcher's role now: the 포지션 변경 one, else his card's.
		private static int PitcherOrder(LineupContext x, CardInfo info) =>
			x.Owned.TryGetValue(info.Idx, out TeamCard tc) && tc.PositionTrainingInfo % 10 != 0 ? PositionChangeHandlers.OrderOf(info, tc.PositionTrainingInfo) : info.PreferredOrder;

		// The cards of a 1군 out of position (StrictPositions), 선수 고정 cards left out; with `first` only the first one.
		private static List<int> Misplaced(LineupContext x, Lineup l, bool first = false)
		{
			List<int> r = new List<int>();
			for (int k = 0; k < 9; k++)
			{
				int idx = l.Batters[k], pos = PositionOf(l, k);
				if (idx == 0 || pos < 0 || pos >= 8 || x.LockedIdx.Contains(idx)) continue;
				CardInfo c = s_data.Catalog.Get(idx);
				if (c == null || c.CanField(pos)) continue;
				r.Add(idx);
				if (first) return r;
			}
			for (int k = 0; k < Lineup.PitcherSlots; k++)
			{
				int idx = l.Pitchers[k];
				if (idx == 0 || x.LockedIdx.Contains(idx)) continue;
				CardInfo c = s_data.Catalog.Get(idx);
				if (c == null || LineupEffects.InRole(ViewOfSlot(k), PitcherOrder(x, c))) continue;
				r.Add(idx);
				if (first) return r;
			}
			return r;
		}

		// Why no 1군 in position could be built (for the window).
		private static string NoFitReason(LineupContext x)
		{
			string[] names = { "포수", "1루수", "2루수", "3루수", "유격수", "좌익수", "중견수", "우익수" };
			List<string> why = new List<string>();
			List<CardInfo> bats = x.Info.Values.Where(c => c.IsBatter).ToList();
			for (int p = 0; p < 8; p++)
			{
				if (!bats.Any(c => c.CanField(p))) why.Add($"{names[p]} 수비가 되는 타자가 없습니다.");
			}
			List<CardInfo> starters = x.Info.Values.Where(c => !c.IsBatter && PitcherOrder(x, c) == 0).ToList();
			int blocked = starters.Count(c => x.Blocked.Contains(c.Idx));
			int sp = starters.Where(c => !x.Blocked.Contains(c.Idx)).Select(c => c.Sidx).Distinct().Count();
			if (sp < 5) why.Add($"선발로 쓸 수 있는 투수가 {sp}명입니다(5명 필요" + (blocked > 0 ? $", 선발 {SpBlock.Games}경기 제한 {blocked}명 빼고" : "") + ").");
			if (why.Count == 0) why.Add("볼 상한 안에서 모든 선수를 맞는 포지션에 넣는 1군을 찾지 못했습니다.");
			return string.Join("\n", why);
		}

		private static Dictionary<int, CardValue> CardValues(LineupContext x)
		{
			Dictionary<int, CardValue> v = new Dictionary<int, CardValue>();
			foreach (CardInfo info in x.Info.Values)
			{
				CardValue cv = ValueOf(x, info, 0);
				if (cv != null) v[info.Idx] = cv;
			}
			return v;
		}

		// A card's own value with `colour` team colour points on every stat (0: none; 2026-11-12, the colour plans value their
		// members with the bonus they would get). Cached in the context; null for a card with no game row.
		private static CardValue ValueOf(LineupContext x, CardInfo info, int colour)
		{
			if (x.ValueCache.TryGetValue((info.Idx, colour), out CardValue hit)) return hit;
			League lg = s_data.Env.League;
			if (double.IsNaN(x.BaseBat))
			{
				BatterTalent lgB = new BatterTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip, Double = lg.Double, Triple = lg.Triple };
				PitcherTalent lgP = new PitcherTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip };
				BallEffect.Scale(lgB, x.Cor);
				BallEffect.Scale(lgP, x.Cor);
				x.BaseBat = TeamValue.MatchupRuns(lgB, x.OppPit, lg);
				x.BasePit = TeamValue.MatchupRuns(x.OppBat, lgP, lg);
			}
			double baseBat = x.BaseBat, basePit = x.BasePit;
			double pa = TeamValue.PaBySlot.Average();
			{
				GameCard g = s_data.Card(info.Idx, x.Cond.TryGetValue(info.Idx, out byte cd) ? cd : (byte)2);
				if (g?.Row == null)
				{
					x.ValueCache[(info.Idx, colour)] = null;
					return null;
				}
				if (x.Owned.TryGetValue(info.Idx, out TeamCard tc))
				{
					sbyte v = (sbyte)Math.Clamp(colour, 0, 100);
					TeamCard noColour = new TeamCard { Idx = tc.Idx, PositionTrainingInfo = tc.PositionTrainingInfo, Educated = tc.Educated, Contract = tc.Contract, Training = tc.Training, Coach = tc.Coach, TeamColor = Enumerable.Repeat(v, 6).ToArray() };
					TeamStrength.AddBonuses(g, noColour);
				}
				GameTeam one = new GameTeam();
				if (info.IsBatter) one.Order[0] = g; else one.Pitchers[0] = g;
				TeamStrength.ApplyShift(one, x.Shift);
				TraitEffects.Raise(one, x.Traits);
				CardRow r = g.Row.Clone();
				double f = 1 + g.Condition;
				for (int j = 0; j < 6; j++) r.R[j] *= f;
				for (int p = 0; p < 8; p++) r.Defs[p] *= f;
				r.Pitches = r.Pitches?.Select(q => (q.code, q.rating * f)).ToList();
				CardValue cv = new CardValue { Info = info };
				if (info.IsBatter)
				{
					BatterTalent bt = CardGame.BatterTalentOf(r, s_data.Env);
					bt.Speed = Math.Clamp(Math.Exp(s_data.Env.Rules.SpeedPer10 * CardRules.X(r.R[3])), 0.7, 1.35);
					BallEffect.Scale(bt, x.Cor);   // a power hitter is worth more with a lively ball
					cv.Off = pa * (TeamValue.MatchupRuns(bt, x.OppPit, lg) - baseBat) + pa * TeamValue.SpeedPerPa * (r.R[3] - 65)
						+ TeamValue.ClutchWeight * pa * TeamValue.MentalPerPa * (r.R[4] - 65);
					for (int p = 0; p < 8; p++) cv.Def[p] = info.CanField(p) ? TeamValue.DefPerPoint[p] * (r.Defs[p] - 65) : double.NaN;
				}
				else
				{
					PitcherTalent pt = CardGame.PitcherTalentOf(r, s_data.Env);
					BallEffect.Scale(pt, x.Cor);
					cv.PerBf = basePit - TeamValue.MatchupRuns(x.OppBat, pt, lg) + TeamValue.PitClutchWeight * TeamValue.PitMentalPerBf * (r.R[5] - 65);
					cv.Stamina = TeamValue.LimitRuns / 5 * (Math.Min(TeamValue.LimitUseful, CardGame.Rules.Limit(r.R[0])) - 110);
					// The role effect in the card's own value too (AI testers 두산 29·쌍방울 11-14: 자동 편성 put RP cards in 3 of the 5
					// starter slots — the seeds (Greedy, by SlotValue) valued a reliever starting as if in role, and the single
					// changes did not always undo it).
					int order = tc != null && tc.PositionTrainingInfo % 10 != 0 ? PositionChangeHandlers.OrderOf(info, tc.PositionTrainingInfo) : info.PreferredOrder;
					cv.Order = order;
					for (int role = 0; role < 4; role++)
					{
						if (!RoleValue)
						{
							cv.PerBfRole[role] = cv.PerBf;
							continue;
						}
						int view = RoleViews[role];
						double share = LineupEffects.FromGraph(LineupEffects.PitcherGraph(view, order));
						if (!LineupEffects.InRole(view, order)) share = (1 + share) * (1 + LineupEffects.OutOfRole) - 1;
						double fr = f * (1 + share);
						CardRow rr = g.Row.Clone();
						for (int j = 0; j < 6; j++) rr.R[j] *= fr;
						rr.Pitches = rr.Pitches?.Select(q => (q.code, q.rating * fr)).ToList();
						PitcherTalent prt = CardGame.PitcherTalentOf(rr, s_data.Env);
						BallEffect.Scale(prt, x.Cor);
						cv.PerBfRole[role] = basePit - TeamValue.MatchupRuns(x.OppBat, prt, lg) + TeamValue.PitClutchWeight * TeamValue.PitMentalPerBf * (rr.R[5] - 65);
						if (role == 0) cv.Stamina = TeamValue.LimitRuns / 5 * (Math.Min(TeamValue.LimitUseful, CardGame.Rules.Limit(rr.R[0])) - 110);
					}
				}
				x.ValueCache[(info.Idx, colour)] = cv;
				return cv;
			}
		}

		// Batting slot k's field position (0-7, 8 = DH), or -1 for the bench.
		private static int PositionOf(Lineup l, int k) => k >= 9 ? -1 : Array.IndexOf(l.Fielders, (byte)(k + 1));

		private static double SlotValue(CardValue c, Lineup l, int k, bool pitcher)
		{
			if (c == null) return double.NegativeInfinity;
			if (pitcher)
			{
				double bf = k < 5 ? TeamValue.BfStart / 5 : TeamValue.BfRelief[k - 5];
				double v = TeamValue.Leverage[k] * bf * c.PerBfRole[RoleOfSlot(k)] + (k < 5 ? c.Stamina : 0);
				return StrictPositions && !FitsSlot(c, k) ? v + Misfit : v;
			}
			int pos = PositionOf(l, k);
			if (pos < 0) return -0.001 * c.Info.Ball;   // the bench plays no part but its balls (and the team colour)
			if (pos == 8) return c.Off;
			return double.IsNaN(c.Def[pos]) ? c.Off + (StrictPositions ? Misfit : -0.5) : c.Off + c.Def[pos];
		}

		private static int PersonOf(CardInfo c) => c.IsBatter ? c.Sidx : -1 - c.Sidx;

		// Whether a 1군 keeps the rules (and, with keep, the team colours working now; with positions, 포지션 엄수 too).
		private static bool Allowed(LineupContext x, Lineup l, int[] keep, bool positions = true)
		{
			List<int> all = l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToList();
			if (all.Count != all.Distinct().Count() || all.Any(i => !x.Info.ContainsKey(i))) return false;
			if (all.Select(i => PersonOf(x.Info[i])).Distinct().Count() != all.Count) return false;
			if (all.Count(i => x.Info[i].IsLegend && x.Info[i].IsBatter) > RosterHandlers.MaxLegendBatters) return false;
			if (all.Count(i => x.Info[i].IsLegend && !x.Info[i].IsBatter) > RosterHandlers.MaxLegendPitchers) return false;
			if (all.Count(i => x.Info[i].IsFA) > CardInfo.MaxFaInFirstTeam) return false;   // FA 선수카드: one in the 1군 (user 2026-11-15)
			if (l.Batters.Take(9).Any(i => i == 0 || !x.Info[i].IsBatter) || l.Batters.Skip(9).Any(i => i != 0 && !x.Info[i].IsBatter)) return false;
			if (l.Pitchers.Take(5).Any(i => i == 0) || l.Pitchers.Any(i => i != 0 && x.Info[i].IsBatter)) return false;
			if (l.Pitchers.Take(5).Any(i => x.Blocked.Contains(i))) return false;
			if (RosterHandlers.LineupBalls(l) > x.Cap) return false;
			if (positions && StrictPositions && Misplaced(x, l, first: true).Count > 0) return false;
			// 선수 고정: each locked card where it was (a batter's batting slot may change, not his position or the bench).
			foreach ((int idx, bool pit, int slot, int pos) in x.Locked)
			{
				if (pit)
				{
					if (l.Pitchers[slot] != idx) return false;
					continue;
				}
				int at = Array.IndexOf(l.Batters, idx);
				if (at < 0 || (pos >= 0 ? PositionOf(l, at) != pos : at < 9)) return false;
			}
			if (keep != null && keep.Any(e => e != 0))
			{
				HashSet<int> available = TeamColors.Available(l);
				foreach (int e in keep.Where(e => e != 0))
				{
					// The same kind, slot and group at the same or a better grade (S = 0).
					int kind = e / 100_000_000, slot = e / 10_000_000 % 10, grade = e / 1_000_000 % 10, group = e % 1_000_000;
					if (!available.Any(a => a / 100_000_000 == kind && a / 10_000_000 % 10 == slot && a % 1_000_000 == group && a / 1_000_000 % 10 <= grade)) return false;
				}
			}
			return true;
		}

		private static Lineup Clone(Lineup l) => new Lineup { Batters = (int[])l.Batters.Clone(), Fielders = (byte[])l.Fielders.Clone(), Pitchers = (int[])l.Pitchers.Clone(), NextSp = l.NextSp };

		// The best 1군 by each card's own value under the cap (λ·balls slot by slot, then the rest spent on value per ball),
		// members of `group` (if any) worth `groupBonus` more a slot. The positions of the 1군 of now (its Fielders) are kept.
		// With a team colour plan (2026-11-12): its members valued with its bonus, and at least its counts of members among the
		// batters and the pitchers (the cheapest change in value less λ·balls brings each one in). With `fixedLambda` one
		// greedy at that ball price, over the cap or not (to screen the plans).
		private static Lineup Greedy(LineupContext x, Dictionary<int, CardValue> values, Lineup shape, Func<CardInfo, bool> group, double groupBonus,
			ColourPlan plan = null, double fixedLambda = double.NaN)
		{
			int[] fillBat = Enumerable.Range(0, 9).OrderBy(k => Array.IndexOf(new[] { 0, 4, 2, 6, 3, 7, 5, 1, 8 }, Math.Max(0, PositionOf(shape, k)))).Concat(Enumerable.Range(9, 5)).ToArray();
			int[] fillPit = { 0, 1, 2, 3, 4, 10, 9, 5, 6, 7, 8 };
			CardValue Eff(CardValue c) => plan != null && plan.Valued.TryGetValue(c.Info.Idx, out CardValue m) ? m : c;
			double V(CardValue c, Lineup l, int k, bool pit) => SlotValue(Eff(c), l, k, pit) + (group != null && group(c.Info) ? groupBonus : 0);
			List<CardValue> batters = values.Values.Where(c => c.Info.IsBatter).ToList(), pitchers = values.Values.Where(c => !c.Info.IsBatter).ToList();
			List<CardValue> memberBat = plan?.MemberBat == null ? new List<CardValue>() : batters.Where(c => plan.MemberBat(c.Info)).ToList();
			List<CardValue> memberPit = plan?.MemberPit == null ? new List<CardValue>() : pitchers.Where(c => plan.MemberPit(c.Info)).ToList();
			Lineup Assign(double lambda)
			{
				Lineup l = Clone(shape);
				Array.Clear(l.Batters);
				Array.Clear(l.Pitchers);
				HashSet<int> people = new HashSet<int>();
				int legendBat = 0, legendPit = 0, fa = 0;
				bool Take(CardValue c, bool pit)
				{
					if (people.Contains(PersonOf(c.Info))) return false;
					if (c.Info.IsLegend && (pit ? legendPit : legendBat) >= 1) return false;
					if (c.Info.IsFA && fa >= CardInfo.MaxFaInFirstTeam) return false;
					return true;
				}
				void Took(CardValue c, bool pit) => TookInfo(c.Info, pit);
				void TookInfo(CardInfo info, bool pit)
				{
					people.Add(PersonOf(info));
					if (info.IsLegend) { if (pit) legendPit++; else legendBat++; }
					if (info.IsFA) fa++;
				}
				void Untook(CardInfo info, bool pit)
				{
					people.Remove(PersonOf(info));
					if (info.IsLegend) { if (pit) legendPit--; else legendBat--; }
					if (info.IsFA) fa--;
				}
				// The plan's count on one side: while short, the change of a non-member (or an empty bench slot) for a member that
				// loses the least value less λ·balls.
				bool Force(bool pit)
				{
					Func<CardInfo, bool> m = pit ? plan?.MemberPit : plan?.MemberBat;
					int need = m == null ? 0 : pit ? plan.NeedPit : plan.NeedBat;
					int[] row = pit ? l.Pitchers : l.Batters;
					int have = row.Count(i => i != 0 && m != null && m(x.Info[i]));
					List<CardValue> pool = pit ? memberPit : memberBat;
					while (have < need)
					{
						int bk = -1;
						CardValue bc = null;
						double bd = double.NegativeInfinity;
						for (int k = 0; k < row.Length; k++)
						{
							int cur = row[k];
							if (cur != 0 && (x.LockedIdx.Contains(cur) || m(x.Info[cur]))) continue;
							CardValue now = cur != 0 && values.TryGetValue(cur, out CardValue nv) ? nv : null;
							if (cur != 0 && now == null) continue;
							double was = now == null ? 0 : V(now, l, k, pit) - lambda * now.Info.Ball;
							int curPerson = now == null ? int.MinValue : PersonOf(now.Info);
							foreach (CardValue c in pool)
							{
								int person = PersonOf(c.Info);
								if (person != curPerson && people.Contains(person)) continue;
								if (c.Info.IsLegend && (pit ? legendPit : legendBat) - (now != null && now.Info.IsLegend ? 1 : 0) >= 1) continue;
								if (c.Info.IsFA && fa - (now != null && now.Info.IsFA ? 1 : 0) >= CardInfo.MaxFaInFirstTeam) continue;
								if (pit && k < 5 && x.Blocked.Contains(c.Info.Idx)) continue;
								if (Array.IndexOf(row, c.Info.Idx) >= 0) continue;
								if (StrictPositions && (pit ? !FitsSlot(c, k) : k < 9 && !FitsField(c, PositionOf(l, k)))) continue;
								double d = V(c, l, k, pit) - lambda * c.Info.Ball - was;
								if (d > bd) { bd = d; bk = k; bc = c; }
							}
						}
						if (bc == null) return false;
						if (row[bk] != 0) Untook(x.Info[row[bk]], pit);
						row[bk] = bc.Info.Idx;
						TookInfo(bc.Info, pit);
						have++;
					}
					return true;
				}
				// 선수 고정: the locked cards first, where they are (a batter at the slot of his position in `shape`).
				foreach ((int idx, bool pit, int slot, int pos) in x.Locked)
				{
					int k = pit || pos < 0 ? slot : Enumerable.Range(0, 9).FirstOrDefault(j => PositionOf(shape, j) == pos, -1);
					int[] row = pit ? l.Pitchers : l.Batters;
					if (k < 0 || row[k] != 0) return null;
					row[k] = idx;
					TookInfo(x.Info[idx], pit);
				}
				foreach (int k in fillBat)
				{
					if (l.Batters[k] != 0) continue;
					CardValue best = null;
					double bs = double.NegativeInfinity;
					foreach (CardValue c in batters)
					{
						if (!Take(c, false)) continue;
						if (StrictPositions && k < 9 && !FitsField(c, PositionOf(l, k))) continue;
						double sc = V(c, l, k, false) - lambda * c.Info.Ball;
						if (sc > bs) { bs = sc; best = c; }
					}
					if (best == null) { if (k < 9) return null; continue; }
					l.Batters[k] = best.Info.Idx;
					Took(best, false);
				}
				if (!Force(false)) return null;
				foreach (int k in fillPit)
				{
					if (l.Pitchers[k] != 0) continue;
					CardValue best = null;
					double bs = double.NegativeInfinity;
					foreach (CardValue c in pitchers)
					{
						if (!Take(c, true) || (k < 5 && x.Blocked.Contains(c.Info.Idx))) continue;
						if (StrictPositions && !FitsSlot(c, k)) continue;
						double sc = V(c, l, k, true) - lambda * c.Info.Ball;
						if (sc > bs) { bs = sc; best = c; }
					}
					if (best == null) { if (k < 5) return null; continue; }
					l.Pitchers[k] = best.Info.Idx;
					Took(best, true);
				}
				if (!Force(true)) return null;
				return l;
			}
			if (!double.IsNaN(fixedLambda)) return Assign(fixedLambda);
			double lo = 0, hi = 5;
			Lineup fit = Assign(hi);
			if (fit == null || RosterHandlers.LineupBalls(fit) > x.Cap) return null;
			for (int it = 0; it < 25; it++)
			{
				double mid = (lo + hi) / 2;
				Lineup d = Assign(mid);
				if (d != null && RosterHandlers.LineupBalls(d) <= x.Cap) { fit = d; hi = mid; } else lo = mid;
			}
			x.LastLambda = hi;
			return fit;
		}

		// Single changes valued whole until none helps: each slot's likely replacements (by own value, then by value per
		// ball, then the members of the team colours the 1군 has), and every swap of two slots.
		private static (Lineup l, TeamValue.Parts v) Improve(LineupContext x, Dictionary<int, CardValue> values, Lineup start, int[] keep, int maxEvals)
		{
			Lineup best = Clone(start);
			TeamValue.Parts bv = Evaluate(x, best, out int[] colours);
			bool better = true;
			while (better && x.Evaluations < maxEvals)
			{
				better = false;
				List<Lineup> tries = new List<Lineup>();
				HashSet<int> inside = best.Batters.Concat(best.Pitchers).Where(i => i != 0).ToHashSet();
				(Dictionary<int, int> colourBonus, _) = TeamColors.AutoBonuses(best);
				CheapTable cheap = Cheap(x, values, best, inside, colourBonus);
				for (int side = 0; side < 2; side++)
				{
					bool pit = side == 1;
					int[] slots = pit ? best.Pitchers : best.Batters;
					for (int k = 0; k < slots.Length; k++)
					{
						if (x.LockedIdx.Contains(slots[k])) continue;   // 선수 고정
						values.TryGetValue(slots[k], out CardValue now);
						double cur = now == null ? double.NegativeInfinity : SlotValue(now, best, k, pit);
						IEnumerable<CardValue> outside = values.Values.Where(c => c.Info.IsBatter != pit && !inside.Contains(c.Info.Idx));
						List<CardValue> byValue = outside.OrderByDescending(c => SlotValue(c, best, k, pit)).Take(5).ToList();
						List<CardValue> byBall = outside.Where(c => SlotValue(c, best, k, pit) > cur).OrderBy(c => c.Info.Ball).Take(3).ToList();
						// Cards of the groups the 1군's team colours count (a member in for a non-member keeps or lifts a grade).
						// (2026-11-12: six by value and three by fewest balls among those better than now, where keeping a team
						// colour leaves only members to choose from.)
						List<CardValue> groupOut = colourBonus.Count == 0 ? new List<CardValue>() : outside
							.Where(c => values.ContainsKey(c.Info.Idx) && SameGroupAsColoured(c.Info, best, colourBonus)).ToList();
						List<CardValue> members = groupOut.OrderByDescending(c => SlotValue(c, best, k, pit)).Take(6)
							.Concat(groupOut.Where(c => SlotValue(c, best, k, pit) > cur).OrderBy(c => c.Info.Ball).Take(3)).ToList();
						foreach (CardValue c in byValue.Concat(byBall).Concat(members).Distinct())
						{
							Lineup t = Clone(best);
							if (pit) t.Pitchers[k] = c.Info.Idx; else t.Batters[k] = c.Info.Idx;
							// Over the cap (2026-11-12): paid for by one other slot taking a cheaper card (PayBalls).
							if (RosterHandlers.LineupBalls(t) > x.Cap) t = PayBalls(x, values, cheap, t, pit, k, c, colourBonus);
							if (t != null) tries.Add(t);
						}
					}
					for (int i = 0; i < slots.Length; i++)
					{
						for (int j = i + 1; j < slots.Length; j++)
						{
							if (!pit && i >= 9 && j >= 9) continue;   // two bench slots: the same
							if (slots[i] == 0 && slots[j] == 0) continue;
							if (x.LockedIdx.Contains(slots[i]) || x.LockedIdx.Contains(slots[j])) continue;   // 선수 고정
							Lineup t = Clone(best);
							int[] s = pit ? t.Pitchers : t.Batters;
							(s[i], s[j]) = (s[j], s[i]);
							tries.Add(t);
						}
					}
				}
				// The likely ones first (2026-11-12): by the change in the cards' own values (the first that helps is taken).
				double before = OwnScore(values, best);
				foreach (Lineup t in tries.Select(t => (t, d: OwnScore(values, t) - before)).OrderByDescending(p => p.d).Select(p => p.t))
				{
					if (x.Evaluations >= maxEvals) break;
					if (!Allowed(x, t, keep)) continue;
					TeamValue.Parts v = Evaluate(x, t, out _);
					if (v.Runs > bv.Runs + 1e-4)
					{
						best = t;
						bv = v;
						better = true;
						break;
					}
				}
			}
			return (best, bv);
		}

		// A card of the same season and club, or club and decade, as a card the team colour bonus counts.
		private static bool SameGroupAsColoured(CardInfo c, Lineup l, Dictionary<int, int> colourBonus)
		{
			foreach (int idx in colourBonus.Keys)
			{
				CardInfo m = s_data.Catalog.Get(idx);
				if (m == null) continue;
				// FA 선수카드: a joker of its own season (user 2026-11-15 19:4x: "해당 년도 세트덱을 사용할 때 해당 년도의 FA선수를 쓸 수 있고,
				// 20년대 롯데 << 이런 세트덱에는 20년대 FA 다 사용 가능 … 년도 조건을 타는 조커 카드다"): any club, the member's season or decade.
				if (m.IsFA) continue;
				if (c.IsFA)
				{
					if (m.Year == c.Year || m.Year / 10 == c.Year / 10) return true;
					continue;
				}
				if (m.TeamCode == c.TeamCode && m.Year == c.Year) return true;
				if (TeamColors.Lineage(m.TeamCode) == TeamColors.Lineage(c.TeamCode) && m.Year / 10 == c.Year / 10) return true;
			}
			return false;
		}

		public static AutoLineupResult AutoLineup(uint userCode, bool keepColour, int maxEvals = 4000, int simGames = AutoLineupSimGames)
		{
			AutoLineupResult r = Search(userCode, keepColour, maxEvals);
			if (r == null || simGames <= 0 || SameLineup(r.Lineup, r.Current)) return r;
			List<Lineup> set = new List<Lineup> { r.Current, r.Lineup };
			bool kept = r.Kept != null && !r.KeepsColour && !SameLineup(r.Kept, r.Current);
			if (kept) set.Add(r.Kept);
			double[] w = LineupSimCheck(userCode, set, simGames, out double[] se, threads: 3);
			r.SimGames = simGames;
			r.SimNow = w[0];
			r.SimAdvice = w[1];
			r.SimSe = se[1];
			if (kept) { r.SimKept = w[2]; r.SimKeptSe = se[2]; }
			// (11-17, AI tester LG 96: TeamValue put the advice +1.03%p above the 1군 of now, the games 78.5% → 74.5% ±1.9, and the
			// window called it "지금과 비슷") An advice the games call worse than the 1군 of now — below it by more than twice the
			// paired standard error — is turned down when the 1군 of now keeps the rules (in position, no ended contract): the advice
			// is the 1군 of now. The kept 1군 stays as it was (shown on its own).
			if (r.SimAdvice < r.SimNow - 2 * r.SimSe && r.CurrentMisplaced.Count == 0 && r.LeftOutExpired.Count == 0)
			{
				r.TurnedDown = r.Lineup;
				r.TurnedDownSim = r.SimAdvice;
				r.Lineup = r.Current;
				r.Runs = r.CurrentRuns;
				r.Parts = r.CurrentParts;
				r.Colours = r.CurrentColours;
				r.Balls = r.CurrentBalls;
				r.KeepsColour = true;
				r.StartersOut = new List<int>();
				r.SimAdvice = r.SimNow;
				r.SimSe = 0;
				// The FA reasons were told against the advice; now the 1군 of now is the answer (no swap taken here).
				LineupContext xc = Context(userCode);
				if (xc != null) FaPass(xc, r.Lineup, r.CurrentParts, keepColour ? TeamColors.Registered(userCode) : null, out r.FaLeftOut, take: false);
			}
			return r;
		}

		// FA 선수카드 (2026-11-20): the held FA cards not in `l` (its 25). Each is tried in every batting-order slot (a batter) or
		// pitcher slot (a pitcher) of `l`; when a swap keeps the rules and adds, the best such swap of all is taken and the pass
		// runs again (`take`), so `l` may come back changed. The cards left out get FaReason's line.
		private static (Lineup l, TeamValue.Parts v) FaPass(LineupContext x, Lineup l, TeamValue.Parts v, int[] keep, out List<(int idx, string why)> why, bool take = true)
		{
			why = new List<(int, string)>();
			List<int> held = x.Owned.Keys.Where(i => s_data.Catalog.Get(i)?.IsFA == true).OrderBy(i => i).ToList();
			if (held.Count == 0 || l == null || v == null) return (l, v);
			for (int round = 0; round < 6; round++)
			{
				why.Clear();
				(Lineup l, TeamValue.Parts v) best = (null, null);
				HashSet<int> inL = l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
				foreach (int fa in held.Where(i => !inL.Contains(i)))
				{
					string reason = FaReason(x, l, v, fa, keep, out Lineup gain, out TeamValue.Parts gainV);
					if (gain != null && take && round < 5)
					{
						if (best.l == null || gainV.Runs > best.v.Runs) best = (gain, gainV);
					}
					else if (gain != null)
					{
						// Not taken: the games turned the advice down (take false), or the rounds ran out.
						why.Add((fa, take ? "넣으면 나아질 수 있습니다(자동 편성을 한 번 더 눌러 보세요)" : "계산으로는 나아 보이지만 경기로 재 보니 지금 1군이 낫습니다"));
					}
					else why.Add((fa, reason ?? "넣어 볼 자리가 없습니다"));
				}
				if (best.l == null) break;
				(l, v) = best;
			}
			return (l, v);
		}

		// Why FA card `fa` is not in `l` (null with `gain` set when a swap keeps the rules and adds). The rules in Allowed's
		// order, the ball cap last: a swap out of position is not called a cap problem. Among the slots the reason of the swap
		// that got furthest wins (cap > team colour > FA count > same player > 선발 제한 > 선수 고정 > 레전드 > position).
		private static string FaReason(LineupContext x, Lineup l, TeamValue.Parts v, int fa, int[] keep, out Lineup gain, out TeamValue.Parts gainV)
		{
			gain = null;
			gainV = null;
			if (x.Expired.Contains(fa)) return "계약이 끝났습니다(재계약하면 후보가 됩니다)";
			if (!x.Info.TryGetValue(fa, out CardInfo c)) return "특별수강 중입니다";
			string[] order = { "cap", "colour", "fa", "same", "spblock", "locked", "legend", "position" };
			int worst = order.Length, minBalls = int.MaxValue, sameAs = 0;
			(Lineup t, TeamValue.Parts tv, int outIdx) best = (null, null, 0);
			int slots = c.IsBatter ? 9 : Lineup.PitcherSlots;
			for (int k = 0; k < slots; k++)
			{
				Lineup t = Clone(l);
				int[] row = c.IsBatter ? t.Batters : t.Pitchers;
				int outIdx = row[k];
				row[k] = fa;
				string p = SwapProblem(x, t, keep, out int balls, out int same);
				if (p == null)
				{
					TeamValue.Parts tv = Evaluate(x, t, out _);
					if (best.t == null || tv.Runs > best.tv.Runs) best = (t, tv, outIdx);
					continue;
				}
				int at = Array.IndexOf(order, p);
				if (at < 0) continue;
				if (p == "cap") minBalls = Math.Min(minBalls, balls);
				if (p == "same" && sameAs == 0) sameAs = same;
				worst = Math.Min(worst, at);
			}
			if (best.t != null)
			{
				if (best.tv.Runs > v.Runs + 1e-4)
				{
					gain = best.t;
					gainV = best.tv;
					return null;
				}
				return best.outIdx != 0
					? $"넣어 봐도 지금보다 약해집니다(가장 나은 자리: {CardName(best.outIdx)} 대신)"
					: "빈 자리에 넣어 봐도 지금보다 약해집니다";
			}
			if (worst >= order.Length) return null;
			switch (order[worst])
			{
				case "cap": return $"넣으면 볼 {minBalls}/{Ro(x.Cap)} 상한을 넘습니다";
				case "colour": return "넣으면 지금 등록한 팀컬러가 깨집니다";
				case "fa":
					string inFirst = string.Join("·", l.Batters.Concat(l.Pitchers).Where(i => i != 0 && x.Info.TryGetValue(i, out CardInfo f) && f.IsFA).Select(CardName));
					return $"1군 FA는 {CardInfo.MaxFaInFirstTeam}명까지입니다({inFirst})";
				case "same": return $"같은 선수의 다른 카드({CardName(sameAs)})가 1군에 있습니다";
				case "spblock": return "선발 5경기 제한에 걸립니다";
				case "locked": return "바꿀 수 있는 자리가 모두 선수 고정입니다";
				case "legend": return "레전드 인원 제한에 걸립니다";
				default: return c.IsBatter ? "맞는 수비 자리가 없습니다" : "보직(선발·중계·마무리)이 맞는 자리가 없습니다";
			}
		}

		// The first rule (Allowed's, the ball cap last) a 1군 breaks, or null. same: a card of the same player already there.
		private static string SwapProblem(LineupContext x, Lineup l, int[] keep, out int balls, out int same)
		{
			balls = RosterHandlers.LineupBalls(l);
			same = 0;
			List<int> all = l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToList();
			if (all.Count != all.Distinct().Count() || all.Any(i => !x.Info.ContainsKey(i))) return "invalid";
			var people = all.GroupBy(i => PersonOf(x.Info[i])).FirstOrDefault(g => g.Count() > 1);
			if (people != null)
			{
				same = people.FirstOrDefault(i => !x.Info[i].IsFA);
				return "same";
			}
			if (all.Count(i => x.Info[i].IsLegend && x.Info[i].IsBatter) > RosterHandlers.MaxLegendBatters) return "legend";
			if (all.Count(i => x.Info[i].IsLegend && !x.Info[i].IsBatter) > RosterHandlers.MaxLegendPitchers) return "legend";
			if (all.Count(i => x.Info[i].IsFA) > CardInfo.MaxFaInFirstTeam) return "fa";
			if (l.Batters.Take(9).Any(i => i == 0 || !x.Info[i].IsBatter) || l.Pitchers.Take(5).Any(i => i == 0) || l.Pitchers.Any(i => i != 0 && x.Info[i].IsBatter)) return "invalid";
			if (l.Pitchers.Take(5).Any(i => x.Blocked.Contains(i))) return "spblock";
			if (StrictPositions && Misplaced(x, l, first: true).Count > 0) return "position";
			foreach ((int idx, bool pit, int slot, int pos) in x.Locked)
			{
				int at = pit ? -1 : Array.IndexOf(l.Batters, idx);
				if (pit ? l.Pitchers[slot] != idx : at < 0 || (pos >= 0 ? PositionOf(l, at) != pos : at < 9)) return "locked";
			}
			if (keep != null && keep.Any(e => e != 0) && !Allowed(x, l, keep, positions: false) && RosterHandlers.LineupBalls(l) <= x.Cap) return "colour";
			if (balls > x.Cap) return "cap";
			return Allowed(x, l, keep) ? null : "invalid";
		}

		// "'22 이정후" (two cards of one player can meet in these lines).
		private static string CardName(int idx) => s_data.Catalog.Get(idx) is CardInfo c ? $"'{c.Year % 100:00} {c.Name}" : idx.ToString();

		// "120으로 · 145로" (systems session 11-20, tester_1120c: "볼 122/120로"): 십·백·천·만·영, 삼, 육 take 으로.
		private static string Ro(int n) => (Math.Abs(n) % 10) is 0 or 3 or 6 ? $"{n}으로" : $"{n}로";

		private static bool SameLineup(Lineup a, Lineup b) => a != null && b != null && a.Batters.SequenceEqual(b.Batters) && a.Pitchers.SequenceEqual(b.Pitchers) && a.Fielders.SequenceEqual(b.Fielders);

		private static AutoLineupResult Search(uint userCode, bool keepColour, int maxEvals)
		{
			LineupContext x = Context(userCode);
			if (x == null || x.Current == null) return null;
			Dictionary<int, CardValue> values = CardValues(x);
			AutoLineupResult r = new AutoLineupResult { Current = x.Current, Cap = x.Cap, CurrentBalls = RosterHandlers.LineupBalls(x.Current), Locked = x.Locked.Select(k => k.idx).ToList() };
			if (StrictPositions) r.CurrentMisplaced = Misplaced(x, x.Current);
			r.LeftOutExpired = x.Current.Batters.Concat(x.Current.Pitchers).Where(i => i != 0 && x.Expired.Contains(i)).Distinct().ToList();
			r.CurrentParts = Evaluate(x, x.Current, out r.CurrentColours);
			r.CurrentRuns = r.CurrentParts.Runs;
			int[] keep = keepColour ? TeamColors.Registered(userCode) : null;

			// Starts: the 1군 of now (when it keeps the rules), the best by own values, and the best team colour plans (2026-11-12:
			// every team colour the cards can make, its members' count filled by the cheapest good members, the balls left to the
			// strongest cards; AutoLineupPlans.cs). With keep only the plans that keep today's team colours.
			List<(string name, Lineup l)> starts = new List<(string, Lineup)>();
			if (Allowed(x, x.Current, keep)) starts.Add(("지금 1군", x.Current));
			Lineup free = Greedy(x, values, x.Current, null, 0);
			double lambda = free != null ? x.LastLambda : 0.05;
			if (free != null && Allowed(x, free, keep)) starts.Add(("능력 순", free));
			List<(string name, Lineup l, double runs)> plans = PlanStarts(x, values, lambda, PlanBuild, keep);
			r.Plans = plans.Count;
			foreach ((string name, Lineup l, double runs) in plans.Where(p => Allowed(x, p.l, keep)).Take(PlanImprove)) starts.Add((name, l));
			if (starts.Count == 0)
			{
				r.Lineup = x.Current;
				r.Parts = r.CurrentParts;
				r.Runs = r.CurrentRuns;
				r.Colours = r.CurrentColours;
				r.Balls = r.CurrentBalls;   // (11-17) it read 0 here
				if (StrictPositions && r.CurrentMisplaced.Count > 0) r.NoFit = NoFitReason(x);
				// (11-17, systems session's check: [팀컬러 유지] said "지금 1군이 가장 강합니다" with two ended contracts in the 1군) The
				// 1군 of now with ended contracts is no answer either: say why none was built.
				else if (r.LeftOutExpired.Count > 0)
				{
					r.NoFit = keep != null
						? "계약이 끝난 선수를 빼면 지금 팀컬러를 지키는 1군을 짤 수 없습니다."
						: "계약이 끝난 선수를 빼고는 볼 상한 안에서 1군을 짤 수 없습니다.";
				}
				// (11-20, AI 테스터 LG 110·kt, 남은일 0-3: a 1군 of 148/145 balls, not even registered, got "지금 1군이 가장 강합니다") Over
				// the cap the 1군 of now is no start, so with no other start it came back as the answer without a reason.
				else if (r.CurrentBalls > x.Cap)
				{
					r.NoFit = $"지금 1군은 볼 {r.CurrentBalls}/{Ro(x.Cap)} 상한을 넘어 등록되지 않습니다.\n"
						+ (keep != null ? "지금 팀컬러를 지키며 상한 안에 드는 1군을 짤 수 없습니다." : "상한 안에서 짤 수 있는 1군을 찾지 못했습니다.");
				}
				return r;
			}
			// Without keep, also the best 1군 that keeps today's team colours, so both can be told (팀컬러를 지키면 / 바꾸면). (11-16: the
			// 1군 of now keeping them is asked without 포지션 엄수; it starts the search only when in position.)
			int[] registered = TeamColors.Registered(userCode);
			bool compare = !keepColour && registered.Any(e => e != 0) && Allowed(x, x.Current, registered, positions: false);
			// Budget (2026-11-12): a third shared by the starts, then the two best go on with the rest (the 1군 keeping today's
			// team colours with a sixth of its own when compared).
			int end = x.Evaluations + maxEvals;
			int keptBudget = compare ? maxEvals / 6 : 0;
			int firstRound = Math.Max(150, (maxEvals - keptBudget) / 3 / starts.Count);
			List<(Lineup l, TeamValue.Parts v, string name)> round = new List<(Lineup, TeamValue.Parts, string)>();
			foreach ((string name, Lineup l) in starts)
			{
				(Lineup best, TeamValue.Parts bv) = Improve(x, values, l, keep, x.Evaluations + firstRound);
				round.Add((best, bv, name));
			}
			round = round.OrderByDescending(t => t.v.Runs).ToList();
			for (int i = 0; i < Math.Min(2, round.Count); i++)
			{
				int left = end - keptBudget - x.Evaluations;
				if (left <= 0) break;
				(Lineup best, TeamValue.Parts bv) = Improve(x, values, round[i].l, keep, x.Evaluations + left / (2 - i));
				if (bv.Runs > round[i].v.Runs) round[i] = (best, bv, round[i].name);
			}
			(Lineup l, TeamValue.Parts v, string name) win = round.OrderByDescending(t => t.v.Runs).First();
			(Lineup l, TeamValue.Parts v) kept = (null, null);
			if (compare)
			{
				foreach ((Lineup l, TeamValue.Parts v, string name) t in round.Where(t => Allowed(x, t.l, registered)))
				{
					if (kept.l == null || t.v.Runs > kept.v.Runs) kept = (t.l, t.v);
				}
				Lineup from = kept.l ?? (Allowed(x, x.Current, registered) ? x.Current : null);
				if (from != null)
				{
					(Lineup best, TeamValue.Parts bv) = Improve(x, values, from, registered, Math.Max(x.Evaluations + 100, end));
					if (kept.l == null || bv.Runs > kept.v.Runs) kept = (best, bv);
					if (bv.Runs > win.v.Runs) win = (best, bv, "지금 1군 (팀컬러 유지)");
				}
			}
			// The batting order of the nine by the 선수분석 graph, the positions going with the players.
			Lineup ordered = Reorder(win.l);
			if (ordered != null && Allowed(x, ordered, keep))
			{
				TeamValue.Parts ov = Evaluate(x, ordered, out _);
				if (ov.Runs >= win.v.Runs - 1e-4) win = (ordered, ov, win.name);
			}
			// FA 선수카드 left out: a swap that adds is taken, the others get their reason (2026-11-20).
			(Lineup fl, TeamValue.Parts fv) = FaPass(x, win.l, win.v, keep, out r.FaLeftOut);
			if (!ReferenceEquals(fl, win.l)) win = (fl, fv, win.name + " + FA");
			r.Lineup = win.l;
			r.Parts = win.v;
			r.Runs = win.v.Runs;
			r.Start = win.name;
			Evaluate(x, win.l, out r.Colours);
			if (compare)
			{
				r.KeepsColour = Allowed(x, win.l, registered);
				if (r.KeepsColour) kept = (win.l, win.v);
				else if (kept.l != null)
				{
					Lineup ko = Reorder(kept.l);
					if (ko != null && Allowed(x, ko, registered))
					{
						TeamValue.Parts kv = Evaluate(x, ko, out _);
						if (kv.Runs >= kept.v.Runs - 1e-4) kept = (ko, kv);
					}
				}
				r.Kept = kept.l;
				r.KeptParts = kept.v;
				r.KeptRuns = kept.v?.Runs ?? 0;
			}
			else r.KeepsColour = keepColour || registered.All(e => e == 0) || Allowed(x, win.l, registered);
			r.Balls = RosterHandlers.LineupBalls(win.l);
			r.StartersOut = x.Current.Pitchers.Take(5).Where(i => i != 0 && !win.l.Pitchers.Take(5).Contains(i)).ToList();
			r.Evaluations = x.Evaluations;
			return r;
		}

		// The nine starters in LineupBuilder.BestOrder's order, each keeping his position.
		private static Lineup Reorder(Lineup l)
		{
			List<CardInfo> starters = l.Batters.Take(9).Select(i => s_data.Catalog.Get(i)).ToList();
			if (starters.Any(c => c == null)) return null;
			int[] posOf = Enumerable.Range(0, 9).Select(k => PositionOf(l, k)).ToArray();
			List<CardInfo> order = LineupBuilder.BestOrder(starters);
			Lineup r = Clone(l);
			for (int k = 0; k < 9; k++)
			{
				int from = starters.IndexOf(order[k]);
				r.Batters[k] = order[k].Idx;
				if (posOf[from] >= 0) r.Fielders[posOf[from]] = (byte)(k + 1);
			}
			return r;
		}

		// Dev: --autolineup <team name> [keep] on a copy of the data; AUTOLINEUP_SIM=<games a club> checks it in full games.
		public static int AutoLineupTest(string teamName, bool keep, string dbPath)
		{
			uint user = 0;
			string cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
			{
				c.Open();
				using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code FROM team WHERE team_name = $n";
				cmd.Parameters.AddWithValue("$n", teamName);
				object o = cmd.ExecuteScalar();
				if (o != null) user = Convert.ToUInt32(o);
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			AutoLineupResult a = user != 0 ? AutoLineup(user, keep) : null;
			if (a == null)
			{
				ServerLog.Warn($"[autolineup] no league or lineup for '{teamName}'");
				return 2;
			}
			string Colour(int[] e) => string.Join(", ", e.Where(i => i != 0).Select(i => $"{TeamColors.Label(i).name} {"SABC"[i / 1_000_000 % 10]}"));
			ServerLog.Info($"[autolineup] {teamName} (user {user}), keep colour {keep}, {a.Evaluations} lineups valued in {watch.ElapsedMilliseconds} ms, best from {a.Start}");
			if (a.Locked.Count > 0) ServerLog.Info($"[autolineup] 선수 고정 (선수보호): {string.Join(", ", a.Locked.Select(i => s_data.Catalog.Get(i)?.Name ?? i.ToString()))}");
			if (a.LeftOutExpired.Count > 0) ServerLog.Info($"[autolineup] 계약 끝남(후보에서 뺌): {string.Join(", ", a.LeftOutExpired.Select(i => s_data.Catalog.Get(i)?.Name ?? i.ToString()))}");
			if (a.TurnedDown != null) ServerLog.Info($"[autolineup] 경기로 재 보니 찾은 안 {100 * a.TurnedDownSim:F1}% < 지금 {100 * a.SimNow:F1}% → 지금 1군 그대로 | 찾은 안: {LineupEffects.Describe(a.TurnedDown, s_data.Catalog)}");
			ServerLog.Info($"[autolineup] now    {a.CurrentBalls}/{a.Cap}볼, {a.CurrentRuns:+0.000;-0.000} runs a game | {Colour(a.CurrentColours)} | {LineupEffects.Describe(a.Current, s_data.Catalog)}");
			ServerLog.Info($"[autolineup] advice {a.Balls}/{a.Cap}볼, {a.Runs:+0.000;-0.000} runs a game ({100 * a.Gain:+0.00;-0.00}%p) | {Colour(a.Colours)} | {LineupEffects.Describe(a.Lineup, s_data.Catalog)}");
			ServerLog.Info($"[autolineup]   now    {a.CurrentParts}");
			ServerLog.Info($"[autolineup]   advice {a.Parts}");
			if (a.StartersOut.Count > 0) ServerLog.Info($"[autolineup]   starters out (선발 5경기 제한): {string.Join(", ", a.StartersOut.Select(i => s_data.Catalog.Get(i)?.Name ?? i.ToString()))}");
			ServerLog.Info($"[autolineup]   team colour plans built {a.Plans}, advice keeps today's colours {a.KeepsColour}" +
				(a.Kept != null ? $", best keeping them {RosterHandlers.LineupBalls(a.Kept)}볼 {a.KeptRuns:+0.000;-0.000} runs ({100 * a.KeptGain:+0.00;-0.00}%p)" : ""));
			if (a.SimGames > 0) ServerLog.Info($"[autolineup]   games ({a.SimGames} a club): now {100 * a.SimNow:F1}%, advice {100 * a.SimAdvice:F1}% (±{100 * a.SimSe:F1}, clear {a.SimClear})" +
				(double.IsNaN(a.SimKept) ? "" : $", kept {100 * a.SimKept:F1}% (±{100 * a.SimKeptSe:F1})") + $" ({watch.ElapsedMilliseconds} ms with the search)");
			string[] roles = { "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };
			string Pitchers(Lineup l) => string.Join("  ", Enumerable.Range(0, 11).Select(k =>
			{
				CardInfo c = s_data.Catalog.Get(l.Pitchers[k]);
				return c == null ? $"{roles[k]} -" : $"{roles[k]} {c.Year % 100:00}{c.Name}{(c.IsFA ? "[FA]" : "")}({c.MainPosition},{c.Ball}볼,체{s_data.Card(c.Idx, 2)?.Row?.R[0]:F0})";
			}));
			string Batters(Lineup l) => string.Join("  ", Enumerable.Range(0, 14).Select(k =>
			{
				CardInfo c = s_data.Catalog.Get(l.Batters[k]);
				int pos = PositionOf(l, k);
				string at = pos < 0 ? "벤치" : new[] { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" }[pos];
				return c == null ? $"{at} -" : $"{at} {c.Year % 100:00}{c.Name}{(c.IsFA ? "[FA]" : "")}({c.Ball})";
			}));
			// FA 선수카드 (2026-11-15): the advice holds at most one FA and never one player twice (FA and its own card).
			string FaCheck(Lineup l)
			{
				List<CardInfo> on = l.Batters.Concat(l.Pitchers).Select(i => s_data.Catalog.Get(i)).Where(c => c != null).ToList();
				int fa = on.Count(c => c.IsFA);
				int twice = on.GroupBy(c => (c.IsBatter, c.Sidx)).Count(g => g.Count() > 1);
				return $"FA {fa}장, 같은 선수 두 번 {twice}{(fa <= CardInfo.MaxFaInFirstTeam && twice == 0 ? "" : " ← 규칙 위반")}";
			}
			ServerLog.Info($"[autolineup]   now    {FaCheck(a.Current)} | advice {FaCheck(a.Lineup)} | start {a.Start}");
			foreach ((int idx, string why) in a.FaLeftOut) ServerLog.Info($"[autolineup]   FA 빠짐: {s_data.Catalog.Get(idx)?.Year % 100:00}{s_data.Catalog.Get(idx)?.Name} — {why}");
			// 포지션 엄수 (11-16): the advice has nobody out of position (선수 고정 cards apart).
			LineupContext cx = Context(user);
			string Mis(Lineup l)
			{
				List<int> m = Misplaced(cx, l);
				return m.Count == 0 ? "0" : $"{m.Count} ({string.Join(", ", m.Select(i => s_data.Catalog.Get(i)?.Name ?? i.ToString()))})";
			}
			ServerLog.Info($"[autolineup]   자리 안 맞음: now {Mis(a.Current)} | advice {Mis(a.Lineup)}{(StrictPositions && a.Lineup != a.Current && Misplaced(cx, a.Lineup).Count > 0 ? " ← 규칙 위반" : "")}" +
				(a.Kept != null ? $" | kept {Mis(a.Kept)}" : "") + (a.NoFit != null ? $" | 맞는 1군 없음: {a.NoFit.Replace("\n", " ")}" : "") + (StrictPositions ? "" : " (MA9M_AUTOLINEUP_STRICT=0)"));
			ServerLog.Info($"[autolineup]   now    투수 {Pitchers(a.Current)}");
			ServerLog.Info($"[autolineup]   advice 투수 {Pitchers(a.Lineup)}");
			ServerLog.Info($"[autolineup]   now    타자 {Batters(a.Current)}");
			ServerLog.Info($"[autolineup]   advice 타자 {Batters(a.Lineup)}");
			if (a.Kept != null && !a.KeepsColour)
			{
				ServerLog.Info($"[autolineup]   kept   투수 {Pitchers(a.Kept)}");
				ServerLog.Info($"[autolineup]   kept   타자 {Batters(a.Kept)}");
			}
			if (int.TryParse(Environment.GetEnvironmentVariable("AUTOLINEUP_SIM"), out int games) && games > 0)
			{
				watch.Restart();
				// Also the advice's batters with the pitchers of now, and its pitchers with the batters of now.
				Lineup bats = Clone(a.Current), pits = Clone(a.Current);
				bats.Batters = (int[])a.Lineup.Batters.Clone();
				bats.Fielders = (byte[])a.Lineup.Fielders.Clone();
				pits.Pitchers = (int[])a.Lineup.Pitchers.Clone();
				List<Lineup> set = new List<Lineup> { a.Current, a.Lineup, bats, pits };
				List<string> names = new List<string> { "now", "advice", "batters", "pitchers" };
				if (a.Kept != null && !a.KeepsColour) { set.Add(a.Kept); names.Add("kept"); }
				double[] w = LineupSimCheck(user, set, games);
				LineupContext x = Context(user);
				for (int k = 0; k < set.Count; k++)
				{
					double tv = Evaluate(x, set[k], out _).Runs;
					ServerLog.Info($"[autolineup] sim {names[k],-8} {100 * w[k]:F2}% ({100 * (w[k] - w[0]):+0.00;-0.00}%p) | TeamValue {tv:+0.000;-0.000} runs ({100 * (tv - a.CurrentRuns) * TeamValue.WinPerRun:+0.00;-0.00}%p)");
				}
				ServerLog.Info($"[autolineup] sim: {games} games against each club, {watch.Elapsed.TotalSeconds:F0} s");
			}
			return 0;
		}

		// Dev check: the club with each 1군 (as AutoLineup values it: team colour, development, condition) against every other
		// club, `games` home games each, the same seeds for every 1군.
		public static double[] LineupSimCheck(uint userCode, List<Lineup> lineups, int games, int threads = 4) => LineupSimCheck(userCode, lineups, games, out _, threads);

		// The same with each 1군's standard error of its difference from the first (the same games, paired).
		public static double[] LineupSimCheck(uint userCode, List<Lineup> lineups, int games, out double[] seVsFirst, int threads = 4)
		{
			LineupContext x = Context(userCode);
			int[] opps = Enumerable.Range(0, x.Teams.Length).Where(j => j != x.Me).ToArray();
			GameEnv env = s_data.Env;
			LeagueState s = x.S;
			// The pitching tree's share by the season's ball, as the league plays it (TraitEffects.PitchScale, 2026-11-18).
			TraitEffects.Effect[] traits = s.Clubs.Select(c => c.IsHuman ? TraitEffects.Of(c.Owner, x.Cor) : TraitEffects.FromPoints(AiTeamOf(s, c)?.Traits ?? new Dictionary<byte, byte>(), x.Cor)).ToArray();
			double[] wins = new double[lineups.Count];
			double[][] each = new double[lineups.Count][];
			for (int k = 0; k < lineups.Count; k++)
			{
				GameTeam mine = BuildTeam(x, lineups[k], out _);
				(byte[] off, byte[] def) = OperationHandlers.TeamOps(userCode);
				mine.Tactics = GameTactics.From(off, def);
				double w = 0;
				object gate = new object();
				double[] one = each[k] = new double[opps.Length * games];
				if (LineupSimSeries > 1)
				{
					w = PlaySeries(x, mine, opps, traits, games, threads, one);
					wins[k] = w / (opps.Length * games);
					continue;
				}
				System.Threading.Tasks.Parallel.For(0, opps.Length * games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, n =>
				{
					int j = opps[n / games];
					// Both rotations turn over (a team's StarterSlot is one game's: every game was SP1 against the same starter).
					GameTeam home = CopyTeam(mine), away = CopyTeam(x.Teams[j]);
					home.StarterSlot = n % 5;
					away.StarterSlot = (n / 5) % 5;
					SeasonModel m = CardGame.Build(home, away, env);
					TraitEffects.Apply(m, 0, x.Traits);
					TraitEffects.Apply(m, 1, traits[j]);
					BallEffect.Apply(m, x.Cor);
					SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(n + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					long r0 = m.TeamBatters[0].Sum(i => st.Bat(i, BS.R)), r1 = m.TeamBatters[1].Sum(i => st.Bat(i, BS.R));
					double v = r0 > r1 ? 1 : r0 == r1 ? 0.5 : 0;
					one[n] = v;
					lock (gate) w += v;
				});
				wins[k] = w / (opps.Length * games);
			}
			seVsFirst = new double[lineups.Count];
			for (int k = 1; k < lineups.Count; k++)
			{
				double[] d = each[k].Zip(each[0], (a, b) => a - b).ToArray();
				double mean = d.Average();
				seVsFirst[k] = d.Length > 1 ? Math.Sqrt(d.Sum(v => (v - mean) * (v - mean)) / (d.Length - 1) / d.Length) : 0;
			}
			return wins;
		}

		// League games carry pitchers' fatigue from one game to the next (LeagueService.PlayGame: TacticModel.CarryIn, days in a
		// row, pitches in the last two; tired or overused relievers sit out). Played one by one, a 1군 whose starters go 35
		// pitches (relievers in the rotation) has a fresh bullpen every game and looked strong: AI tester 두산 11-13, "79%" →
		// .538 in 14 games, the same as before the change. So the check plays chains of LineupSimSeries games in a row (the
		// club's rotation turning over, against the other clubs in turn); each opponent carries its own fatigue between its
		// games in the chain. MA9M_AUTOLINEUP_SERIES=0 plays them one by one as before.
		public static readonly int LineupSimSeries = int.TryParse(Environment.GetEnvironmentVariable("MA9M_AUTOLINEUP_SERIES"), out int ls) ? ls : 24;

		private static double PlaySeries(LineupContext x, GameTeam mine, int[] opps, TraitEffects.Effect[] traits, int games, int threads, double[] one)
		{
			GameEnv env = s_data.Env;
			int total = opps.Length * games, len = LineupSimSeries, chains = (total + len - 1) / len;
			double w = 0;
			object gate = new object();
			System.Threading.Tasks.Parallel.For(0, chains, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, c =>
			{
				double[] units = new double[11];
				int[] days = new int[11], last1 = new int[11], last2 = new int[11];
				Dictionary<int, (double[] u, int[] d, int[] l1, int[] l2)> opp = new Dictionary<int, (double[], int[], int[], int[])>();
				double cw = 0;
				for (int n = c * len; n < Math.Min(total, (c + 1) * len); n++)
				{
					int j = opps[n % opps.Length];
					if (!opp.TryGetValue(j, out var os)) opp[j] = os = (new double[11], new int[11], new int[11], new int[11]);
					GameTeam home = CopyTeam(mine), away = CopyTeam(x.Teams[j]);
					home.StarterSlot = n % 5;
					away.StarterSlot = (n / opps.Length) % 5;
					SeasonModel m = CardGame.Build(home, away, env);
					TraitEffects.Apply(m, 0, x.Traits);
					TraitEffects.Apply(m, 1, traits[j]);
					BallEffect.Apply(m, x.Cor);
					for (int t = 0; t < 2 && m.Tac != null; t++)
					{
						for (int slot = 0; slot < 11 && slot < m.Decks[t].Pitchers.Length; slot++)
						{
							int p = m.Decks[t].Pitchers[slot];
							if (p < 0) continue;
							m.Tac.CarryIn[p] = t == 0 ? units[slot] : os.u[slot];
							m.Tac.DaysInRow[p] = t == 0 ? days[slot] : os.d[slot];
							m.Tac.Last1[p] = t == 0 ? last1[slot] : os.l1[slot];
							m.Tac.Last2[p] = t == 0 ? last2[slot] : os.l2[slot];
						}
					}
					SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(n + 1) * 7919).PlayOne(0, 1, null);
					for (int t = 0; t < 2 && m.Tac != null; t++)
					{
						double[] u = t == 0 ? units : os.u;
						int[] d = t == 0 ? days : os.d, l1 = t == 0 ? last1 : os.l1, l2 = t == 0 ? last2 : os.l2;
						for (int slot = 0; slot < 11 && slot < m.Decks[t].Pitchers.Length; slot++)
						{
							int p = m.Decks[t].Pitchers[slot];
							if (p < 0) continue;
							int np = (int)st.Pit(p, PS.NP);
							u[slot] = m.Tac.R.CarryOut(m.Tac.CarryIn[p], m.Tac.Units(p, np), m.Tac.Limit[p]);
							d[slot] = st.Pit(p, PS.G) > 0 ? d[slot] + 1 : 0;
							l2[slot] = l1[slot];
							l1[slot] = np;
						}
					}
					long r0 = m.TeamBatters[0].Sum(i => st.Bat(i, BS.R)), r1 = m.TeamBatters[1].Sum(i => st.Bat(i, BS.R));
					double v = r0 > r1 ? 1 : r0 == r1 ? 0.5 : 0;
					one[n] = v;
					cw += v;
				}
				lock (gate) w += cw;
			});
			return w;
		}
	}
}
