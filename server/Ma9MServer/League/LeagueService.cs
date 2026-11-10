using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// Line score, errors and double plays of one game, passing every play on to the broadcast.
	public sealed class LeagueRecorder : IGameRecorder
	{
		private readonly IGameRecorder m_inner;
		private int m_bat;  // batting model team: 0 home, 1 away

		public readonly List<int>[] Line = { new List<int>(), new List<int>() };
		public readonly int[] Errors = new int[2], DoublePlays = new int[2];  // by fielding team
		public readonly int[] GrandSlams = new int[2];                         // by batting team
		// Per model pitcher: at-bats against, total bases allowed, double plays induced (the engine's pitching lines keep
		// none of them; user 2026-10-30, 상세기록 피장타율 0.000). PlayGame checks them against the batters' lines.
		public readonly Dictionary<int, int[]> Against = new Dictionary<int, int[]>();

		public LeagueRecorder(IGameRecorder inner)
		{
			m_inner = inner;
		}

		public void HalfInningStart(int inning, bool bottom)
		{
			m_bat = bottom ? 0 : 1;
			Line[m_bat].Add(0);
			m_inner?.HalfInningStart(inning, bottom);
		}

		public void HalfInningEnd(int inning, bool bottom) => m_inner?.HalfInningEnd(inning, bottom);

		public void PitcherChange(int team, int pitcher) => m_inner?.PitcherChange(team, pitcher);

		public void Play(PlayInfo p)
		{
			Line[m_bat][^1] += p.Runs;
			if (p.Kind == PlayKind.Error) Errors[1 - m_bat]++;
			if (p.Kind == PlayKind.DoublePlay) DoublePlays[1 - m_bat]++;
			if (p.Kind == PlayKind.HomeRun && p.Runs == 4) GrandSlams[m_bat]++;
			if (p.Kind != PlayKind.StealSafe && p.Kind != PlayKind.StealOut)
			{
				if (!Against.TryGetValue(p.Pitcher, out int[] a)) Against[p.Pitcher] = a = new int[3];
				if (p.Kind is not (PlayKind.Walk or PlayKind.HitByPitch or PlayKind.SacFly or PlayKind.SacBunt)) a[0]++;
				a[1] += p.Kind switch { PlayKind.Single or PlayKind.BuntHit => 1, PlayKind.Double => 2, PlayKind.Triple => 3, PlayKind.HomeRun => 4, _ => 0 };
				// The fielding engine's infield hit with a wild throw at a lead runner is an Error play, but the batter
				// keeps his single in his line (engine session, 2026-10-31: --leaguetest TB 5/6 now and then).
				if (p.Kind == PlayKind.Error && p.Field != null) a[1] += p.Field.Hit;
				if (p.Kind == PlayKind.DoublePlay) a[2]++;
			}
			m_inner?.Play(p);
		}
	}

	// League play: every active team has its own ten-club league. Due games are played on the game
	// loop, including every slot missed while the server was down (user, 2026-10-18: compute them
	// all, show them as unconfirmed games, pay the MP).
	public static partial class LeagueService
	{
		private static Database s_db;
		private static GameData s_data;
		private static LeagueStore s_store;

		// Seasons whose regular-season ceremony this process has settled (LeagueAwards.Settle checks the DB as well).
		private static readonly HashSet<(uint user, int season)> s_regularAwards = new HashSet<(uint, int)>();

		public static LeagueStore Store => s_store;

		// Games whose LeagueRecorder.Against counts did not match the batters' lines (PlayGame; --leaguetest wants 0).
		public static int AgainstMismatches;

		// MP balance (AddMp with 0 returns it).
		public static int Balance(uint user) => s_db.AddMp(user, 0);

		// Confirms the user's games (all unconfirmed when games is null) and pays the MP and 홈런볼
		// they earned; returns what was paid.
		public static (int mp, int homeRuns) ConfirmAndPay(uint user, int season, IEnumerable<int> games)
		{
			List<int> list = games?.ToList();
			// 오늘의 미션 / 업적 count the games being confirmed (systems session): hits, strikeouts, wins, homers.
			List<LeagueGame> confirming = list == null ? s_store.Games(user, season, unconfirmedOnly: true)
				: list.SelectMany(g => s_store.Games(user, season, g)).Where(g => !g.Confirmed).ToList();
			(int mp, int homeRuns) due = s_store.Confirm(user, season, list);
			if (list == null) s_store.RevealLines(user, season);
			else if (list.Count > 0) s_store.RevealLines(user, season, list.Max());
			Missions.OnLeagueGames(user, confirming);
			if (due.mp != 0) s_db.AddMp(user, due.mp);
			if (due.homeRuns > 0) s_store.AddHomeRunBalls(user, due.homeRuns);
			// 코칭스태프: the 치어리더's MP and SP for the wins (CoachHandlers; nothing unless MA9M_COACH=1).
			CoachHandlers.OnLeagueConfirmed(user, confirming);
			return due;
		}
		public static GameData Data => s_data;

		public static void Init(Database db, GameData data, string dbPath)
		{
			s_db = db;
			s_data = data;
			s_store = new LeagueStore(dbPath);
			int shutouts = s_store.BackfillShutouts();
			if (shutouts > 0) ServerLog.Info($"league: 완봉 counted again from the per-game lines ({shutouts} rows)");
			int clubGames = s_store.BackfillClubGames();
			if (clubGames > 0) ServerLog.Info($"league: 최근성적 of the shared leagues' other real clubs filled from their own per-game lines ({clubGames} rows)");
			// Six tiers once the switch has run (LeagueStore.MigrateTiers6), before the AI teams are read. MA9M_TIERS6=1 (tests on
			// a data copy only) runs the switch now.
			if (Environment.GetEnvironmentVariable("MA9M_TIERS6") == "1") s_store.MigrateTiers6("MA9M_TIERS6");
			if (s_store.Tiers6Done()) LeagueRules.UseNewStructure();
			AiTeams.Init(dbPath, data.Catalog, data);
			if (Environment.GetEnvironmentVariable("MA9M_TIERS6") == "1") AiTeams.RenewNow(data);
			SeasonBonus.Init(db, dbPath);
		}

		private static System.Threading.Timer s_timer;

		// Catch up now, then check for due games every 15 seconds (on the game loop thread).
		public static void Start(GameLoop loop)
		{
			// Traits saved over the team's CP (a relegation before 2026-10-28 kept them): trimmed once, the deepest first.
			loop.Post(() =>
			{
				foreach ((uint code, _, _, _) in s_store.ActiveTeams())
				{
					TeamPropertyHandlers.TrimToCp(code, TeamPropertyHandlers.AvailableCp(code), "리그 한도에 맞춰");
				}
			});
			loop.Post(() => Tick(DateTime.Now));
			s_timer = new System.Threading.Timer(_ => loop.Post(() => Tick(DateTime.Now)), null, 15000, 15000);
		}

		private static uint AiOwner(uint user, int club) => 0x8000_0000u | (user << 4) | (uint)club;

		// Which of the tier's nine fixed AI teams a league club is (the user's club skipped).
		private static int AiSlot(LeagueState s, LeagueClub c) => c.Index < s.MyIndex ? c.Index : c.Index - 1;

		// An AI club's team: its team-year deck (메이저, 챔피언스), else the tier's fixed club for its place (null for a real team).
		private static AiTeam AiTeamOf(LeagueState s, LeagueClub c) => c.IsHuman ? null : c.AiKey >= 0 ? AiTeams.Deck(s.Tier, c.AiKey) : AiTeams.Get(s.Tier, AiSlot(s, c));

		// League clubs follow their fixed AI team (also turns leagues made before the fixed teams into
		// theirs, keeping the standings). Returns whether anything changed.
		private static bool SyncAiClubs(LeagueState s)
		{
			bool changed = false;
			foreach (LeagueClub c in s.Clubs.Where(c => !c.IsHuman))
			{
				AiTeam t = AiTeamOf(s, c);
				if (t == null || c.Name == t.Name && c.Emblem == t.Emblem && c.Lineup == t.Lineup && c.Ball == t.Ball) continue;
				c.Name = t.Name;
				c.Emblem = t.Emblem;
				c.Lineup = t.Lineup;
				c.Ball = t.Ball;
				changed = true;
			}
			return changed;
		}

		// Every due game of every active team (catch-up after downtime included), on the common calendar
		// (LeagueCalendar): the regular season, the postseason slots, the close at 팀 정비 and the next
		// league at 편성.
		public static void Tick(DateTime now)
		{
			List<(uint code, string name, byte emblem, byte grade)> teams = s_store.ActiveTeams();
			// 챔피언스: the shared leagues' due games, once for all their members, before each team's own steps.
			AdvanceShared(now, teams);
			// The AI clubs and decks for a season whose 편성 is due (RenewAi); a cycle still playing somewhere holds its 편성.
			HashSet<DateTime> waiting = RenewAi(now, teams);
			foreach ((uint code, string name, byte emblem, byte grade) in teams)
			{
				LeagueState s = s_store.Load(code);
				if (s == null)
				{
					// A new team joins the running season from its next slot (or the next season); a league of its
					// own, so 메이저 at most (챔피언스 leagues are only made at 편성, FormChampions).
					(DateTime start, int first) = LeagueCalendar.JoinAt(now);
					s = NewLeague(code, name, emblem, Math.Min(LeagueRules.TierOfGrade(grade), LeagueRules.GateTier), 1, start, first);
				}
				else if (s.Start == LeagueCalendar.AbandonedStart && s.NextTier < 0)
				{
					s = Restart(s, name, emblem);
				}
				else if (!LeagueCalendar.OnCalendar(s.Start))
				{
					s = Migrate(s, name, emblem, now);
				}
				// A closed league keeps the clubs it played (the AI teams may be made again for the next season by then).
				if (s.NextTier < 0 && SyncAiClubs(s))
				{
					s_store.Save(s);
					ServerLog.Info($"league: {name}'s {LeagueRules.TierNames[s.Tier]} league now plays the fixed AI clubs");
				}
				LeagueClub me = s.Clubs[s.MyIndex];
				me.Name = name;
				me.Emblem = emblem;
				int played = 0;
				while (true)
				{
					if (s.NextGame <= LeagueRules.Games)
					{
						// A shared league's games are AdvanceShared's (all members at once).
						if (s.Share != null || s.SlotTime(s.NextGame) > now) break;
						PlaySlot(s);
						s.LastGame = s.NextGame;
						s.NextGame++;
						s_store.Save(s);
						played++;
						continue;
					}
					if (s.Start + LeagueCalendar.PostStart <= now && s_regularAwards.Add((s.UserCode, s.Season)))
					{
						// 리그 시상 when the regular season is over, before the post-season (user, 2026-10-24: "리그가
						// 종료되고나서 보여주고, 그 다음에 포스트시즌"; it waited for the close at 58 h, 비기너 too). The
						// 144 games' lines go into the season rows first; once per season (LeagueAwards checks the DB).
						s_store.RevealLines(s.UserCode, s.Season, LeagueRules.Games);
						LeagueAwards.Settle(s);
					}
					if (LeagueRules.HasPostseason(s.Tier) && s.PostSlot <= LeagueCalendar.LastPostSlot)
					{
						if (s.Share != null) break;
						// The 한국시리즈's extra slots (after ties) only while it is undecided; otherwise the close need not wait.
						if (s.PostSlot > LeagueCalendar.PostSlots && Postseason.Of(s).Champion >= 0)
						{
							s.PostSlot = LeagueCalendar.LastPostSlot + 1;
							s_store.Save(s);
							continue;
						}
						if (LeagueCalendar.PostSlotTime(s.Start, s.PostSlot) > now) break;
						if (PlayPostSlot(s, s.PostSlot, name))
						{
							s.LastGame = Postseason.GameNumber(s.PostSlot);
							played++;
						}
						s.PostSlot++;
						s_store.Save(s);
						continue;
					}
					if (s.NextTier < 0)
					{
						if (s.Start + LeagueCalendar.CloseAt > now) break;
						Close(s, name, migrated: false);
						continue;
					}
					if (s.Start + LeagueCalendar.FormAt > now) break;
					// 챔피언스 leagues are made for every club going there at once (FormChampions, after this loop).
					if (s.NextTier == LeagueRules.ChampionsTier) break;
					if (waiting.Contains(s.Start)) break;
					s = Form(s, name, emblem);
				}
				if (played > 0) ServerLog.Info($"league: {name} played {played} slot(s), season {s.Season} game {s.LastGame} ({LeagueRules.TierNames[s.Tier]})");
			}
			// 챔피언스 편성 once every league of the cycle that can still send a club there has closed; a league made now
			// plays its due games at once (after downtime).
			if (FormChampions(now, teams, waiting)) AdvanceShared(now, teams);
			// Connected apps whose top bar shows an older game get it again (TopBar.PushLeague, 2026-11-09).
			TopBar.PushLeague();
		}

		// 다음 시즌 AI (user, 2026-11-02: "다음시즌부터 새 볼값으로 짜줘"): once a season's 편성 is due and every league of it has
		// closed, the AI clubs and decks are made again from the current cards where stale (AiTeams.RenewForSeason), before the
		// first league of the next season. A league of that season still playing (catching up after downtime) reads the AI
		// teams in its games, so the cycle's 편성 waits (the returned season starts) until it closes, or until two minutes
		// before the next season as FormChampions does.
		private static HashSet<DateTime> RenewAi(DateTime now, List<(uint code, string name, byte emblem, byte grade)> teams)
		{
			HashSet<DateTime> waiting = new HashSet<DateTime>();
			List<LeagueState> states = teams.Select(t => s_store.Load(t.code)).Where(x => x != null && LeagueCalendar.OnCalendar(x.Start)).ToList();
			foreach (IGrouping<DateTime, LeagueState> cycle in states.GroupBy(x => x.Start))
			{
				DateTime next = cycle.Key + LeagueCalendar.Cycle;
				if (cycle.Key + LeagueCalendar.FormAt > now || AiTeams.RenewedFor(next)) continue;
				if (cycle.Any(x => x.NextTier < 0) && now < next - TimeSpan.FromMinutes(2))
				{
					waiting.Add(cycle.Key);
					continue;
				}
				// 레전드 리그 (2026-11-08): the first 편성 for a season from LeagueRules.NewStructureFrom moves to six tiers first, so
				// the AI made just below and the leagues formed after are the six tiers'.
				if (!LeagueRules.NewStructure && next >= LeagueRules.NewStructureFrom)
				{
					s_store.MigrateTiers6($"편성 for {next:MM-dd HH:mm}");
					AiTeams.Reload();
				}
				AiTeams.RenewForSeason(s_data, next);
			}
			return waiting;
		}

		// ---- 챔피언스 (user, 2026-10-28): "실제 존재하는 계정들을 만날 수 있게끔 … 실제 계정 매치가 가장 최우선으로 되며 이후 빈자리를
		// 팀덱을 갖춘 ai팀으로". Every real team going to 챔피언스 at a 편성 plays in one shared league (ten clubs; more than ten
		// teams make several leagues, as even as possible), and team-year decks (AiTeams.Decks) fill the free places. Each
		// member keeps its own copy of the league, as every league is kept (state, games, lines, fatigue under its own
		// user code and season number), with the same clubs in the same places; a slot is played once, on the first
		// member's copy, and given to every member: its own club's games unconfirmed with their MP and 홈런볼, the rest
		// confirmed. The screens, records, awards and the close then work on a member's copy as on any league.

		// Every shared league's due games (regular season and postseason), for all its members at once.
		private static void AdvanceShared(DateTime now, List<(uint code, string name, byte emblem, byte grade)> teams)
		{
			Dictionary<uint, (string name, byte emblem)> active = teams.ToDictionary(t => t.code, t => (t.name, t.emblem));
			foreach (string share in s_store.Shares())
			{
				List<LeagueState> members = s_store.ShareMembers(share).Where(active.ContainsKey).Select(s_store.Load).Where(x => x != null).ToList();
				if (members.Count == 0) continue;
				LeagueState lead = members[0];
				if (members.Any(m => m.NextGame != lead.NextGame || m.PostSlot != lead.PostSlot))
				{
					ServerLog.Warn($"league: shared {share} out of step ({string.Join(", ", members.Select(m => $"{m.UserCode} game {m.NextGame} slot {m.PostSlot}"))}); following {lead.UserCode}");
				}
				int played = 0;
				while (true)
				{
					if (lead.NextGame <= LeagueRules.Games)
					{
						if (lead.SlotTime(lead.NextGame) > now) break;
						PlaySharedSlot(members, active);
						played++;
						continue;
					}
					if (LeagueRules.HasPostseason(lead.Tier) && lead.PostSlot <= LeagueCalendar.LastPostSlot)
					{
						// The 한국시리즈's extra slots only while it is undecided (as PlayPostSlot's leagues).
						if (lead.PostSlot > LeagueCalendar.PostSlots && Postseason.Of(lead).Champion >= 0)
						{
							foreach (LeagueState m in members.Skip(1).Append(lead))
							{
								m.PostSlot = LeagueCalendar.LastPostSlot + 1;
								s_store.Save(m);
							}
							continue;
						}
						if (LeagueCalendar.PostSlotTime(lead.Start, lead.PostSlot) > now) break;
						if (PlaySharedPostSlot(members, lead.PostSlot)) played++;
						int nextSlot = lead.PostSlot + 1;
						foreach (LeagueState m in members.Skip(1).Append(lead))
						{
							m.PostSlot = nextSlot;
							s_store.Save(m);
						}
						continue;
					}
					break;
				}
				if (played > 0)
				{
					ServerLog.Info($"league: shared {LeagueRules.TierNames[lead.Tier]} {share} ({string.Join(", ", members.Select(m => m.Clubs[m.MyIndex].Name))}) played {played} slot(s), game {lead.LastGame}");
				}
			}
		}

		// A member's copy follows the copy the slot was played on: every club's record, and the real teams' names and
		// latest lineups.
		private static void CopyClubs(LeagueState from, LeagueState to)
		{
			for (int i = 0; i < from.Clubs.Count && i < to.Clubs.Count; i++)
			{
				LeagueClub f = from.Clubs[i], t = to.Clubs[i];
				t.W = f.W;
				t.L = f.L;
				t.D = f.D;
				t.RunsFor = f.RunsFor;
				t.RunsAgainst = f.RunsAgainst;
				t.Totals = System.Text.Json.JsonSerializer.Deserialize<ClubTotals>(System.Text.Json.JsonSerializer.Serialize(f.Totals));
				if (f.IsHuman)
				{
					t.Name = f.Name;
					t.Emblem = f.Emblem;
					t.Lineup = f.Lineup;
					t.Ball = f.Ball;
				}
			}
		}

		// A shared league's regular-season slot: played on the first member's copy, then given to every member (the first
		// last, so a slot cut short by a crash is played again from it).
		private static void PlaySharedSlot(List<LeagueState> members, Dictionary<uint, (string name, byte emblem)> active)
		{
			LeagueState lead = members[0];
			int game = lead.NextGame;
			(GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) = Teams(lead, game, _ => game);
			List<LeagueGame> results = new List<LeagueGame>();
			List<PlayerSeason> lines = new List<PlayerSeason>();
			Dictionary<(int club, int card), PitcherLoad> fatigue = s_store.Fatigue(lead.UserCode, lead.Season);
			Dictionary<(int club, int card), PitcherLoad> fatigueAfter = new Dictionary<(int club, int card), PitcherLoad>();
			foreach ((int away, int home) in LeagueRules.Pairs(game, lead.Start))
			{
				results.Add(PlayGame(lead, game, home, away, teams, lineups, lines, traits, fatigue, fatigueAfter, post: false));
			}
			// The real teams as they played: name, emblem and lineup (a member whose team is deleted plays on with its last one).
			foreach (LeagueClub c in lead.Clubs.Where(c => c.IsHuman))
			{
				if (active.TryGetValue(c.Owner, out (string name, byte emblem) t))
				{
					c.Name = t.name;
					c.Emblem = t.emblem;
				}
				c.Lineup = lineups[c.Index].Serialize();
				c.Ball = RosterHandlers.LineupBalls(lineups[c.Index]);
			}
			foreach (LeagueState m in members.Skip(1).Append(lead))
			{
				if (m != lead) CopyClubs(lead, m);
				m.LastGame = game;
				m.NextGame = game + 1;
				s_store.SaveFatigue(m.UserCode, m.Season, fatigueAfter);
				s_store.AddGames(m.UserCode, results.Select(g => g.CopyFor(m.Season, m.MyIndex, m.Tier)));
				// The lines wait for each member's own scoreboard or confirmation (RevealLines), as in any league.
				s_store.AddPendingLines(m.UserCode, m.Season, game, m.MyIndex, lines.Select(l => l.CopyFor(m.Season)));
				s_store.Save(m);
			}
		}

		// A shared league's postseason slot, as PlayPostSlot plays one, given to every member; false when the slot has no game.
		private static bool PlaySharedPostSlot(List<LeagueState> members, int slot)
		{
			LeagueState lead = members[0];
			Bracket b = Postseason.Of(lead);
			if (Postseason.Pairing(b, slot) is not (int home, int away, int i, SeriesState sr)) return false;
			int game = Postseason.GameNumber(slot);
			(GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) = Teams(lead, game, c => RotationOf(lead, c, game));
			List<PlayerSeason> lines = new List<PlayerSeason>();
			Dictionary<(int club, int card), PitcherLoad> fatigue = s_store.Fatigue(lead.UserCode, lead.Season);
			Dictionary<(int club, int card), PitcherLoad> fatigueAfter = new Dictionary<(int club, int card), PitcherLoad>();
			bool ks = sr.Kind == PostSeries.KoreanSeries;
			LeagueGame g = PlayGame(lead, game, home, away, teams, lineups, lines, traits, fatigue, fatigueAfter, post: true,
				maxInnings: ks ? LeagueRules.MaxInnings : LeagueRules.EndlessInnings, replayTies: !ks);
			foreach (LeagueState m in members.Skip(1).Append(lead))
			{
				s_store.SaveFatigue(m.UserCode, m.Season, fatigueAfter);
				s_store.AddGames(m.UserCode, new[] { g.CopyFor(m.Season, m.MyIndex, m.Tier) });
				s_store.AddPostLines(m.UserCode, m.Season, sr.Kind, lines.Select(l => l.CopyFor(m.Season)));
				m.LastGame = game;
			}
			ServerLog.Info($"league: shared {LeagueRules.TierNames[lead.Tier]} {lead.Share} {sr.Kind} game {i}: {lead.Clubs[away].Name} {g.AwayScore} - {g.HomeScore} {lead.Clubs[home].Name}");
			return true;
		}

		// 챔피언스 편성: the clubs a cycle sends to 챔피언스 (stayed or promoted) get their shared leagues once every league of the
		// cycle that can still send one there (메이저 and 챔피언스) has closed (a 한국시리즈 past game 7 keeps its league open
		// till 59:30); two minutes before the next season it goes ahead without the late ones (they then get a league of
		// their own). Returns whether a league was made.
		private static bool FormChampions(DateTime now, List<(uint code, string name, byte emblem, byte grade)> teams, HashSet<DateTime> waiting)
		{
			List<(LeagueState s, string name, byte emblem)> states = teams.Select(t => (s: s_store.Load(t.code), t.name, t.emblem)).Where(x => x.s != null).ToList();
			bool formed = false;
			foreach (IGrouping<DateTime, (LeagueState s, string name, byte emblem)> cycle in states
				.Where(x => x.s.NextTier == LeagueRules.ChampionsTier && x.s.Start + LeagueCalendar.FormAt <= now).GroupBy(x => x.s.Start))
			{
				DateTime start = cycle.Key;
				if (waiting.Contains(start)) continue;   // the AI decks are made again first (RenewAi)
				bool open = states.Any(x => x.s.Start == start && x.s.NextTier < 0 && x.s.Tier >= LeagueRules.GateTier);
				if (open && now < start + LeagueCalendar.Cycle - TimeSpan.FromMinutes(2)) continue;
				List<(LeagueState s, string name, byte emblem)> going = cycle.OrderBy(x => x.s.UserCode).ToList();
				int leagues = (going.Count + LeagueRules.Clubs - 1) / LeagueRules.Clubs;
				for (int k = 0; k < leagues; k++)
				{
					FormShared(going.Where((x, i) => i % leagues == k).ToList(), start + LeagueCalendar.Cycle, k);
				}
				formed = true;
			}
			return formed;
		}

		// The 1군 cards of a team (its saved lineup).
		private static IEnumerable<int> LineupCards(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			return l == null ? Enumerable.Empty<int>() : l.Batters.Concat(l.Pitchers).Where(i => i != 0);
		}

		// One shared league from game 1 at start: the members in random places, team-year decks in the others (none of the
		// members' own team-years), every member's copy with its own club as the user's.
		private static void FormShared(List<(LeagueState s, string name, byte emblem)> members, DateTime start, int k)
		{
			string share = $"C{start:yyyyMMddHHmm}-{k}";
			Random rnd = new Random(start.Year * 100_000 + start.DayOfYear * 100 + start.Hour * 4 + k);
			HashSet<(int year, int team)> own = members.SelectMany(m => AiTeams.TeamYearsOf(LineupCards(m.s.UserCode))).ToHashSet();
			IEnumerable<int> DeckCards(AiTeam d)
			{
				Lineup l = Lineup.Parse(d.Lineup);
				return l.Batters.Concat(l.Pitchers).Where(i => i != 0);
			}
			List<AiTeam> decks = AiTeams.Decks(LeagueRules.ChampionsTier).Where(d => !own.Contains(AiTeams.TeamYearOf(DeckCards(d))))
				.OrderBy(_ => rnd.Next()).Take(LeagueRules.Clubs - members.Count).ToList();
			int[] places = Enumerable.Range(0, LeagueRules.Clubs).OrderBy(_ => rnd.Next()).ToArray();
			uint aiBase = members[0].s.UserCode;
			List<LeagueClub> clubs = new List<LeagueClub>();
			for (int i = 0; i < LeagueRules.Clubs; i++)
			{
				int at = places[i];
				if (i < members.Count)
				{
					(LeagueState s, string name, byte emblem) m = members[i];
					Lineup l = s_db.GetLineup(m.s.UserCode);
					clubs.Add(new LeagueClub
					{
						Index = at, Name = m.name, Emblem = m.emblem, Owner = m.s.UserCode, Lineup = l?.Serialize() ?? string.Empty,
						Ball = l == null ? 0 : RosterHandlers.LineupBalls(l), LastRank = m.s.RankOf(m.s.MyIndex)
					});
				}
				else if (i - members.Count < decks.Count)
				{
					AiTeam d = decks[i - members.Count];
					clubs.Add(new LeagueClub { Index = at, Name = d.Name, Emblem = d.Emblem, Owner = AiOwner(aiBase, at), Lineup = d.Lineup, Ball = d.Ball, AiKey = AiTeams.KeyOf(d) });
				}
				else
				{
					// Fewer decks than places (not enough built): the highest fixed clubs fill in (메이저's; 루키's with six tiers).
					AiTeam t = AiTeams.Get(LeagueRules.GateTier, i - members.Count - decks.Count);
					clubs.Add(new LeagueClub { Index = at, Name = t.Name, Emblem = t.Emblem, Owner = AiOwner(aiBase, at), Lineup = t.Lineup, Ball = t.Ball, AiKey = -1 });
				}
			}
			clubs = clubs.OrderBy(c => c.Index).ToList();
			foreach ((LeagueState s, string name, byte emblem) m in members)
			{
				LeagueState next = new LeagueState
				{
					UserCode = m.s.UserCode, Tier = LeagueRules.ChampionsTier, Season = m.s.Season + 1, Start = start, NextGame = 1, FirstGame = 1, LastGame = 0,
					PostSlot = 1, NextTier = -1, Share = share
				};
				foreach (LeagueClub c in clubs)
				{
					LeagueClub x = c.Copy();
					x.Totals = new ClubTotals();
					x.IsUser = x.Owner == m.s.UserCode;
					if (x.IsUser) next.MyIndex = x.Index;
					next.Clubs.Add(x);
				}
				s_store.Save(next);
				s_store.SetTeamGrade(m.s.UserCode, LeagueRules.TierGrade[LeagueRules.ChampionsTier]);
			}
			ServerLog.Info($"league: 챔피언스 {share} formed from {start:MM-dd HH:mm}: {string.Join(", ", clubs.Select(c => $"{c.Index}:{c.Name}{(c.IsHuman ? "(계정)" : string.Empty)}"))}");
		}

		private static LeagueState NewLeague(uint user, string name, byte emblem, int tier, int season, DateTime start, int firstGame)
		{
			Random rnd = new Random(HashCode.Combine(user, season, tier));
			LeagueState s = new LeagueState
			{
				UserCode = user, Tier = tier, Season = season, Start = start, NextGame = firstGame, FirstGame = firstGame, LastGame = 0, PostSlot = 1, NextTier = -1,
				MyIndex = rnd.Next(LeagueRules.Clubs)
			};
			// 메이저 (user, 2026-10-29: "팀덱으로 갖춰서 ai팀 … 다음 시즌부터"): nine team-year decks drawn for the season, none of the
			// team's own team-years (as 챔피언스); without a full pool, the fixed clubs.
			// Six tiers (2026-11-08): every deck tier (트리플A up; a late 챔피언스 club's own league too).
			List<AiTeam> decks = new List<AiTeam>();
			if (LeagueRules.NewStructure ? AiTeams.HasDecks(tier) : tier == LeagueRules.MajorTier)
			{
				HashSet<(int year, int team)> own = AiTeams.TeamYearsOf(LineupCards(user));
				decks = AiTeams.Decks(tier).Where(d => !own.Contains(AiTeams.TeamYearOf(d))).OrderBy(_ => rnd.Next()).Take(LeagueRules.Clubs - 1).ToList();
				if (decks.Count < LeagueRules.Clubs - 1) decks.Clear();
			}
			for (int i = 0; i < LeagueRules.Clubs; i++)
			{
				LeagueClub c = new LeagueClub { Index = i };
				if (i == s.MyIndex)
				{
					c.IsUser = true;
					c.Name = name;
					c.Emblem = emblem;
					c.Owner = user;
				}
				else if (decks.Count > 0)
				{
					AiTeam d = decks[i < s.MyIndex ? i : i - 1];
					c.Name = d.Name;
					c.Emblem = d.Emblem;
					c.Owner = AiOwner(user, i);
					c.Lineup = d.Lineup;
					c.Ball = d.Ball;
					c.AiKey = AiTeams.KeyOf(d);
				}
				else
				{
					// The tier's fixed AI clubs (AiTeams), the same nine in every league of the tier.
					AiTeam t = AiTeams.Get(tier, i < s.MyIndex ? i : i - 1);
					c.Name = t.Name;
					c.Emblem = t.Emblem;
					c.Owner = AiOwner(user, i);
					c.Lineup = t.Lineup;
					c.Ball = t.Ball;
				}
				s.Clubs.Add(c);
			}
			s_store.Save(s);
			s_store.SetTeamGrade(user, LeagueRules.TierGrade[tier]);
			// A team that went down keeps only the traits the new league's CP allows (the deepest go first).
			TeamPropertyHandlers.TrimToCp(user, LeagueRules.TraitPoints[tier], $"{LeagueRules.TierNames[tier]} 리그로 내려가");
			ServerLog.Info($"league: new {LeagueRules.TierNames[tier]} league for {name}, season {season} from {start:yyyy-MM-dd HH:mm}"
				+ (firstGame > 1 ? $", joining at game {firstGame} ({LeagueRules.Games - firstGame + 1} games)" : string.Empty));
			return s;
		}

		// 팀 정비 (58 h): the season closes. Promotion and relegation by the postseason place and the
		// regular-season rank (LeagueRules.NextTier), history, 시즌 완주 보너스, 업적, 리그 시상; the next tier's
		// ball cap applies from now on (LeagueState.CapTier) and the next league is formed at 59 h (Form).
		// migrated: a league that ran on its own clock closes with its standings as they are, its top five
		// standing in for the postseason places (user, 2026-10-21: "지금 순위로 마감 후 공통 시즌 시작").
		private static void Close(LeagueState s, string name, bool migrated)
		{
			int rank = s.RankOf(s.MyIndex);
			Bracket bracket = !migrated && LeagueRules.HasPostseason(s.Tier) ? Postseason.Of(s) : null;
			int place = bracket?.PlaceOf(s.MyIndex) ?? (migrated && rank <= LeagueRules.PostseasonClubs ? rank : 0);
			int tier = LeagueRules.NextTier(s.Tier, rank, place);
			// Every player line into the season rows before the awards read them (the user still sees each
			// result first on the unconfirmed-games screen; the records show the new season by then).
			s_store.RevealLines(s.UserCode, s.Season);
			// 허슬 플레이 (engine session): the season's best plays keep their games before the next league comes.
			HustlePlays.Archive(s);
			s_store.CloseSeason(s, rank, s.Clubs[s.MyIndex], tier);
			// 시즌 완주 보너스 (systems session): paid at the lobby once the season's games are confirmed.
			SeasonBonus.Record(s.UserCode, s.Season, s.Tier, rank);
			// 업적 시즌 완주 / 우승 (shown once the season's results are confirmed).
			Missions.Add(s.UserCode, Missions.SeasonsDone);
			if (rank == 1) Missions.Add(s.UserCode, Missions.SeasonsWon);
			// 리그 시상 (systems session, LeagueAwards), once per user and season while the season's lines and bracket are
			// here: the regular titles were settled at 48 h (Tick; here only for a season closed early), the
			// 한국시리즈 MVP and the season's 업적 now.
			LeagueAwards.Settle(s);
			LeagueAwards.SettlePost(s);
			s.NextTier = tier;
			s_store.Save(s);
			s_store.SetTeamGrade(s.UserCode, LeagueRules.TierGrade[tier]);
			string post = bracket == null ? string.Empty : $", 포스트시즌 {(place > 0 ? place + "위" : "탈락")} (우승 {s.Clubs[Math.Max(0, bracket.Champion)].Name})";
			ServerLog.Info($"league: {name} season {s.Season} closed{(migrated ? " early (own clock → common calendar)" : string.Empty)}, rank {rank}{post} → {LeagueRules.TierNames[tier]}");
		}

		// 리그 편성 (59 h): the next season's league, from game 1 at the next cycle's start.
		private static LeagueState Form(LeagueState s, string name, byte emblem)
		{
			LeagueState next = NewLeague(s.UserCode, name, emblem, s.NextTier, s.Season + 1, s.Start + LeagueCalendar.Cycle, 1);
			next.Clubs[next.MyIndex].LastRank = s.RankOf(s.MyIndex);
			s_store.Save(next);
			return next;
		}

		// A league made before the common calendar ran on its own clock: its due games are played, it closes
		// with the standings as they are, and the team joins the common season from its next slot.
		private static LeagueState Migrate(LeagueState s, string name, byte emblem, DateTime now)
		{
			int played = 0;
			while (s.NextGame <= LeagueRules.Games && s.SlotTime(s.NextGame) <= now)
			{
				PlaySlot(s);
				s.LastGame = s.NextGame;
				s.NextGame++;
				s_store.Save(s);
				played++;
			}
			ServerLog.Info($"league: {name}'s season {s.Season} ran on its own clock (from {s.Start:MM-dd HH:mm}): {played} due slot(s) played, closing at game {s.Played}");
			Close(s, name, migrated: true);
			(DateTime start, int first) = LeagueCalendar.JoinAt(now);
			LeagueState next = NewLeague(s.UserCode, name, emblem, s.NextTier, s.Season + 1, start, first);
			next.Clubs[next.MyIndex].LastRank = s.RankOf(s.MyIndex);
			s_store.Save(next);
			return next;
		}

		// A league of the abandoned first cycle (LeagueCalendar.AbandonedStart): its games, lines and fatigue
		// are dropped with no rewards and no close, and the same season starts again from game 1 at the
		// calendar's start (user, 2026-10-21: "현재 리그에서 보상 없이 0시에 초기화").
		private static LeagueState Restart(LeagueState s, string name, byte emblem)
		{
			int lastRank = s.Clubs[s.MyIndex].LastRank;
			int dropped = s_store.DiscardSeason(s.UserCode, s.Season);
			LeagueState next = NewLeague(s.UserCode, name, emblem, s.Tier, s.Season, LeagueCalendar.CycleStart(LeagueCalendar.Epoch), 1);
			next.Clubs[next.MyIndex].LastRank = lastRank;
			s_store.Save(next);
			ServerLog.Info($"league: {name}'s season {s.Season} (from game {s.FirstGame}, {s.GamesPlayed} played) discarded without rewards ({dropped} rows); it starts again at {next.Start:MM-dd HH:mm}");
			return next;
		}

		// The game number a club's starter follows (StarterSlot): the league game number in the regular
		// season. In the postseason the club's own games: its ace opens its first series, and SP1-SP4 take turns
		// from series to series (user, 2026-10-31: "준플레이오프 플레이오프 한국시리즈에서는 4선발까지 사용하고 시리즈를
		// 이길경우 3대1 / 이때는 다음 시리즈에서 1선발부터 … 3대0으로 이긴 팀은 첫 게임에서 4선발이 나가고 그 다음에
		// 1선발"). SP5 takes the turn after SP4 once all four have started in that series, and does not move the four
		// ("플레이오프를 3대0으로 이기고 한국시리즈 진출한 팀은 1차전이 4선발, 2차전이 1선발이니까 6차전이 5선발"): a series
		// opened by SP1 has SP5 in game 5, one opened by SP4 in game 6, one opened by SP2 in game 8. So a 3-1 or 3-2
		// winner opens the next series with SP1, a 3-0 winner with SP4 (then SP1); a 와일드카드 game moves the four too.
		public static int RotationOf(LeagueState s, int club, int game)
		{
			if (game <= LeagueRules.Games) return game;
			return PostRotation(s_store.Games(s.UserCode, s.Season, club: club).Where(g => Postseason.IsPost(g.Game)).Select(g => g.Game), game);
		}

		// The rule above on the club's postseason game numbers: 1-4 for SP1-SP4, 5 for SP5.
		public static int PostRotation(IEnumerable<int> clubGames, int game)
		{
			int four = 0, series = -1, seen = 0;
			bool fifthDue = false;
			foreach (int g in clubGames.Where(x => x < game).OrderBy(x => x))
			{
				int k = Postseason.SeriesOf(g);
				if (k != series)
				{
					series = k;
					seen = 0;
					fifthDue = false;
				}
				if (fifthDue)
				{
					// SP5's start; the four go on where they were, and need all four again for his next.
					fifthDue = false;
					seen = 0;
					continue;
				}
				int sp = four % 4;
				four++;
				seen |= 1 << sp;
				if (sp == 3 && seen == 15) fifthDue = true;
			}
			return Postseason.SeriesOf(game) == series && fifthDue ? 5 : 1 + four % 4;
		}

		// The user's next game: its number, the opponent club and the starters' rotation number; null when
		// there is none (the season over, or the user's club not playing next in the postseason).
		public static (int game, int opponent)? NextGameOf(LeagueState s)
		{
			if (s.NextGame <= LeagueRules.Games)
			{
				(int away, int home) = LeagueRules.Pairs(s.NextGame, s.Start).First(p => p.home == s.MyIndex || p.away == s.MyIndex);
				return (s.NextGame, home == s.MyIndex ? away : home);
			}
			return Postseason.NextOf(s);
		}

		// The rotation number of the user's next start (the roster's 선발예정 band).
		public static int NextRotation(LeagueState s) => s.NextGame <= LeagueRules.Games ? s.NextGame : RotationOf(s, s.MyIndex, Postseason.NextOf(s)?.game ?? byte.MaxValue);

		// Between a club's last game of a cycle (season, or its postseason) and the first game of the next season no start
		// is due, so no starter is 선발예정 and every one may move (user, 2026-10-26: "포스트시즌이 끝나고 남는 3시간 동안은
		// 선발투수들을 자유롭게 변경 … 리그 시작 준비 기간 동안은 선발 순서를 조절"). Game 1 of a season is SP1's
		// (StarterSlot), so the order set then is the new season's rotation.
		public static bool StartersFree(LeagueState s, DateTime now) => s == null || now < s.Start || NextGameOf(s) == null;

		// The lineup a club plays: a real team's saved one (rebuilt from the roster when unusable; in a shared league a
		// member whose team is gone plays on with the lineup it last played, as an AI club would), else the AI club's.
		public static Lineup LineupOf(LeagueState s, LeagueClub c, out List<TeamCard> owned)
		{
			owned = null;
			if (c.IsHuman)
			{
				List<TeamCard> cards = s_db.GetCards(c.Owner);
				if (cards.Count > 0 || c.IsUser)
				{
					owned = cards;
					Lineup mine = s_db.GetLineup(c.Owner);
					HashSet<int> have = cards.Select(x => x.Idx).ToHashSet();
					// A lineup over the 레전드 limit (RosterHandlers.LegendProblem) is built again too, without the extra 레전드.
					if (mine == null || !mine.IsValid(have) || RosterHandlers.LegendProblem(mine) != null) mine = LineupBuilder.Build(RosterHandlers.LegendLimited(have), s_data.Catalog);
					return mine;
				}
			}
			// AI clubs field their stored 25 arranged by the current LineupBuilder: every spot's own
			// man, and the batting order with the best 선수분석 graph (engine session, 2026-10-19).
			lock (s_aiArranged)
			{
				if (!s_aiArranged.TryGetValue(c.Lineup, out Lineup arranged))
				{
					Lineup stored = Lineup.Parse(c.Lineup);
					System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
					arranged = LineupBuilder.Build(stored.Batters.Concat(stored.Pitchers).Where(i => i != 0), s_data.Catalog);
					s_aiArranged[c.Lineup] = arranged;
					ServerLog.Info($"league: AI {c.Name} arranged in {sw.ElapsedMilliseconds} ms: {LineupEffects.Describe(stored, s_data.Catalog)} → {LineupEffects.Describe(arranged, s_data.Catalog)}");
				}
				return arranged;
			}
		}

		public static int StarterSlot(Lineup l, int game)
		{
			int slot = (game - 1) % 5;
			if (l.Pitchers[slot] != 0) return slot;
			for (int s = 0; s < Lineup.PitcherSlots; s++) if (l.Pitchers[s] != 0) return s;
			return 0;
		}

		// aiDev: an AI deck's per-card development, for its 포지션 변경 (six tiers, 2026-11-08; real teams use owned).
		// aiCoach: the AI deck's coaching set grade (AiTeams.DeckRule.Coach), for its 수석코치's 면담 (AiConsult).
		private static GameTeam Team(LeagueClub c, Lineup l, DateTime day, int game, List<TeamCard> owned, IEnumerable<TeamCard> aiDev = null, int aiCoach = 0)
		{
			GameTeam t = new GameTeam { Name = c.Name };
			Dictionary<int, int> consult = owned == null ? AiConsult(c, l, day, aiCoach) : null;
			byte Cond(int idx) => consult != null && consult.TryGetValue(idx, out int up) ? (byte)Math.Max(0, Condition.For(c.Owner, idx, day) - up) : Condition.For(c.Owner, idx, day);
			for (int pos = 0; pos < Lineup.FielderSlots; pos++) t.Position[l.Fielders[pos] - 1] = pos;
			for (int slot = 0; slot < 9; slot++) t.Order[slot] = s_data.Card(l.Batters[slot], Cond(l.Batters[slot]));
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				t.Pitchers[s] = l.Pitchers[s] != 0 ? s_data.Card(l.Pitchers[s], Cond(l.Pitchers[s])) : null;
			}
			t.StarterSlot = StarterSlot(l, game);
			// 작전실 and 개인작전 of a real team (the user's, or a shared league's other members; AI clubs play neutral 50s).
			if (owned != null) OperationHandlers.ApplyTo(t, c.Owner, l);
			// 선수분석 graph (±5%), fielders out of position (−10%), pitchers out of role (−15%, after a 포지션 변경 by the
			// owner's card).
			LineupEffects.Apply(t, l, s_data.Catalog, owned ?? aiDev);
			return t;
		}

		// The AI decks' 수석코치 (user, 2026-11-08, asked about the 노말 set doing nothing: "레전드는 노말 코치진 세트덱이니까 12시간마다
		// 컨디션 조절이나 조금씩 해줄 수 있는 효과로 만들려고 했음"): the 면담실 as a real team's head coach of that grade runs it
		// (CoachConsult) — each sitting (0-12 and 12-24 h) up to CoachCatalog.Visitors[grade] of the 1군 (batting order and
		// pitchers) in 나쁨 or 매우 나쁨 (매우 나쁨 twice as likely) get 1-3 steps (a third each) for that sitting. Fixed by a hash of
		// (club, card, sitting), so every game of the sitting agrees. Card → steps.
		internal static Dictionary<int, int> AiConsult(LeagueClub c, Lineup l, DateTime at, int grade)
		{
			Dictionary<int, int> up = new Dictionary<int, int>();
			if (grade <= 0 || grade >= CoachCatalog.Visitors.Length) return up;
			DateTime sitting = CoachHandlers.SittingStart(at);
			// FNV-1a over the inputs (HashCode is seeded per process, so a restart would change a sitting).
			uint Hash(int idx, int salt)
			{
				uint h = 2166136261u;
				foreach (long v in new long[] { c.Owner, idx, sitting.Ticks / TimeSpan.TicksPerHour, salt, 20261108 })
				{
					for (int b = 0; b < 8; b++) h = (h ^ (byte)(v >> (8 * b))) * 16777619u;
				}
				return h & 0x7FFF_FFFFu;
			}
			var bad = l.Batters.Take(9).Concat(l.Pitchers).Where(i => i != 0).Distinct()
				.Select(i => (idx: i, cond: Condition.For(c.Owner, i, at))).Where(x => x.cond >= 3).ToList();
			foreach (var x in bad.OrderBy(x => Hash(x.idx, 1) / (x.cond == 4 ? 2.0 : 1.0)).Take(CoachCatalog.Visitors[grade]))
			{
				up[x.idx] = 1 + (int)(Hash(x.idx, 2) % 3);
			}
			return up;
		}

		private static TeamCard AiDevelopment(int tier)
		{
			sbyte b = (sbyte)LeagueRules.AiBonus[tier];
			TeamCard t = new TeamCard();
			for (int j = 0; j < 6; j++) t.Training[j] = b;
			return t;
		}

		// AI clubs' stored lineups as LineupBuilder arranges them, by stored lineup text.
		private static readonly Dictionary<string, Lineup> s_aiArranged = new Dictionary<string, Lineup>();

		// Strength of a user club whose 1군 is over the ball cap (share of every rating).
		private const double OverCapStrength = 0.3;

		// On from 2026-10-19 21:10, once the user had brought their clubs under the cap (SKY마토 119,
		// SKY준우 120 of 120).
		private static readonly bool OverCapPenalty = true;

		// Every club's team for a slot (the league reference is over the whole league, also in the
		// postseason); rotation(club) = the game number the club's starter follows (RotationOf).
		private static (GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) Teams(LeagueState s, int game, Func<int, int> rotation)
		{
			DateTime day = s.SlotTime(game);  // the slot's time: a 컨디션 회복제 counts by the hour (Condition.For)
			GameTeam[] teams = new GameTeam[LeagueRules.Clubs];
			Lineup[] lineups = new Lineup[LeagueRules.Clubs];
			TeamCard aiDev = AiDevelopment(s.Tier);
			for (int i = 0; i < LeagueRules.Clubs; i++)
			{
				LeagueClub c = s.Clubs[i];
				lineups[i] = LineupOf(s, c, out List<TeamCard> owned);
				AiTeam ai = owned == null ? AiTeamOf(s, c) : null;
				int aiCoach = ai != null && c.AiKey >= 0 && LeagueRules.NewStructure && AiTeams.HasDecks(s.Tier) ? AiTeams.RuleOf(s.Tier).Coach : 0;
				teams[i] = Team(c, lineups[i], day, rotation(i), owned, ai?.Development.Values, aiCoach);
				if (owned != null) TeamStrength.AddBonuses(teams[i], lineups[i], owned);
				else if (ai != null && ai.Development.Count > 0)
				{
					// 메이저 fixed clubs and 챔피언스 decks: each card's own bonus (재계약/트레이닝, or 팀컬러/특별수강).
					TeamStrength.AddBonuses(teams[i], lineups[i], ai.Development.Values);
				}
				else if (ai != null && aiDev.Training[0] != 0)
				{
					foreach (GameCard g in teams[i].Order.Concat(teams[i].Pitchers)) TeamStrength.AddBonuses(g, aiDev);
				}
				// A 1군 over the tier's ball cap (a relegated club that kept its roster) plays at 30% of
				// its strength: every rating, fielding and pitch rating × 0.3 for the game (user,
				// 2026-10-19: idle accounts sink to the bottom). The roster screen warns about it.
				if (OverCapPenalty && owned != null && RosterHandlers.LineupBalls(lineups[i]) > LeagueRules.BallCap[s.Tier])
				{
					foreach (GameCard g in teams[i].Order.Concat(teams[i].Pitchers).Where(g => g != null))
					{
						g.Condition = (1 + g.Condition) * OverCapStrength - 1;
					}
				}
			}
			// League reference over the whole league.
			TeamStrength.ApplyLeagueReference(teams);
			// 팀 특성, outside the league reference: a real team's saved points, each AI club's 90% build.
			TraitEffects.Effect[] traits = s.Clubs.Select(c => c.IsHuman ? TraitEffects.Of(c.Owner)
				: TraitEffects.FromPoints(AiTeamOf(s, c)?.Traits ?? new Dictionary<byte, byte>())).ToArray();
			for (int i = 0; i < teams.Length; i++)
			{
				TraitEffects.Raise(teams[i], traits[i]);
			}
			return (teams, lineups, traits);
		}

		private static void PlaySlot(LeagueState s)
		{
			int game = s.NextGame;
			(GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) = Teams(s, game, _ => game);
			List<LeagueGame> results = new List<LeagueGame>();
			List<PlayerSeason> lines = new List<PlayerSeason>();
			// Pitchers' fatigue carried between games (one league game = one day of recovery).
			Dictionary<(int club, int card), PitcherLoad> fatigue = s_store.Fatigue(s.UserCode, s.Season);
			Dictionary<(int club, int card), PitcherLoad> fatigueAfter = new Dictionary<(int club, int card), PitcherLoad>();
			foreach ((int away, int home) in LeagueRules.Pairs(game, s.Start))
			{
				results.Add(PlayGame(s, game, home, away, teams, lineups, lines, traits, fatigue, fatigueAfter, post: false));
			}
			s_store.SaveFatigue(s.UserCode, s.Season, fatigueAfter);
			s_store.AddGames(s.UserCode, results);
			// Player lines (season rows, and the user's club game by game for 최근성적) wait until the
			// user sees the result: its scoreboard or its confirmation (LeagueStore.RevealLines).
			s_store.AddPendingLines(s.UserCode, s.Season, game, s.MyIndex, lines);
		}

		// A postseason slot (Postseason): the next game of its series while the series is on; false when the
		// series has its winner (or the slot belongs to none). Standings stay as the regular season left
		// them; the lines go to the postseason rows by series; the user's club earns MP and 홈런볼 as in
		// a league game, paid on confirming.
		private static bool PlayPostSlot(LeagueState s, int slot, string name)
		{
			Bracket b = Postseason.Of(s);
			if (Postseason.Pairing(b, slot) is not (int home, int away, int i, SeriesState sr)) return false;
			int game = Postseason.GameNumber(slot);
			(GameTeam[] teams, Lineup[] lineups, TraitEffects.Effect[] traits) = Teams(s, game, c => RotationOf(s, c, game));
			List<PlayerSeason> lines = new List<PlayerSeason>();
			Dictionary<(int club, int card), PitcherLoad> fatigue = s_store.Fatigue(s.UserCode, s.Season);
			Dictionary<(int club, int card), PitcherLoad> fatigueAfter = new Dictionary<(int club, int card), PitcherLoad>();
			// Before the 한국시리즈 no game ends level (끝장승부); the 한국시리즈 goes to 11 innings (LeagueRules.MaxInnings).
			bool ks = sr.Kind == PostSeries.KoreanSeries;
			LeagueGame g = PlayGame(s, game, home, away, teams, lineups, lines, traits, fatigue, fatigueAfter, post: true,
				maxInnings: ks ? LeagueRules.MaxInnings : LeagueRules.EndlessInnings, replayTies: !ks);
			// Only the two clubs' loads carry on; the clubs that did not play rest.
			s_store.SaveFatigue(s.UserCode, s.Season, fatigueAfter);
			s_store.AddGames(s.UserCode, new[] { g });
			s_store.AddPostLines(s.UserCode, s.Season, sr.Kind, lines);
			ServerLog.Info($"league: {name} {sr.Kind} game {i}: {s.Clubs[away].Name} {g.AwayScore} - {g.HomeScore} {s.Clubs[home].Name}");
			return true;
		}

		// The box score of one game: line score, H / E / BB / HR / SB / K / DP by side, the W / S / L pitchers
		// and each side's BEST and WORST card by a simple game score. League games and exhibitions (the
		// scoreboard after SKIP, ExhibitionHandlers). Team 0 of the model is the home side.
		public static GameDetail BoxScore(SeasonModel m, SimStats st, LeagueRecorder lr, Lineup home, Lineup away)
		{
			Lineup[] lineup = { home, away };
			long Bat(int t, int stat) => m.TeamBatters[t].Sum(i => st.Bat(i, stat));
			GameDetail d = new GameDetail
			{
				HomeLine = lr.Line[0].ToArray(), AwayLine = lr.Line[1].ToArray(), HomeLineup = home.Serialize(), AwayLineup = away.Serialize()
			};
			for (int t = 0; t < 2; t++)
			{
				// The B column is 4사구 (walks + hit by pitch), as on KBO scoreboards and in the client's 문자중계 scoreboard,
				// which counts both (engine session, 2026-11-10; AI tester 1, game 117: 스코어보드 B 7 vs 문자중계 B 8 = 7 + 1 HBP).
				d.Stats[t] = new[] { (int)Bat(t, BS.H), lr.Errors[t], (int)(Bat(t, BS.BB) + Bat(t, BS.HBP)), (int)Bat(t, BS.HR), (int)Bat(t, BS.SB), (int)Bat(t, BS.SO), lr.DoublePlays[t] };
			}
			int CardOfPitcher(int p)
			{
				int t = m.TeamPitchers[0].Contains(p) ? 0 : 1;
				int slot = m.Decks[t].SlotOf(p);
				return slot >= 0 ? lineup[t].Pitchers[slot] : 0;
			}
			int CardOfBatter(int i)
			{
				int t = i < 9 ? 0 : 1;
				return lineup[t].Batters[i - 9 * t];
			}
			for (int p = 0; p < m.Pitchers.Length; p++)
			{
				if (st.Pit(p, PS.W) > 0) d.WinIdx = CardOfPitcher(p);
				if (st.Pit(p, PS.SV) > 0) d.SaveIdx = CardOfPitcher(p);
				if (st.Pit(p, PS.L) > 0) d.LossIdx = CardOfPitcher(p);
			}
			// Best and worst of each side by a simple game score.
			for (int t = 0; t < 2; t++)
			{
				List<(int idx, double score)> scored = new List<(int, double)>();
				foreach (int i in m.TeamBatters[t])
				{
					double tb = st.Bat(i, BS.H) + st.Bat(i, BS.D) + 2 * st.Bat(i, BS.T) + 3 * st.Bat(i, BS.HR);
					double v = tb + st.Bat(i, BS.BB) + st.Bat(i, BS.SB) + st.Bat(i, BS.R) + st.Bat(i, BS.RBI) - 0.5 * st.Bat(i, BS.SO) - 0.3 * (st.Bat(i, BS.AB) - st.Bat(i, BS.H));
					scored.Add((CardOfBatter(i), v));
				}
				foreach (int p in m.TeamPitchers[t])
				{
					if (st.Pit(p, PS.G) == 0) continue;
					double v = 0.6 * st.Pit(p, PS.Outs) + 0.5 * st.Pit(p, PS.SO) - 2 * st.Pit(p, PS.ER) - 0.6 * (st.Pit(p, PS.H) + st.Pit(p, PS.BB));
					scored.Add((CardOfPitcher(p), v));
				}
				d.BestIdx[t] = scored.OrderByDescending(x => x.score).First().idx;
				d.WorstIdx[t] = scored.OrderBy(x => x.score).First().idx;
			}
			d.Moments = Moments(m, st, lr);
			return d;
		}

		// 경기 순간 업적 (user, 2026-10-22, keys from the systems session's Missions) by side, once per occurrence.
		private static string[][] Moments(SeasonModel m, SimStats st, LeagueRecorder lr)
		{
			List<string>[] moments = { new List<string>(), new List<string>() };
			long Bat(int t, int stat) => m.TeamBatters[t].Sum(i => st.Bat(i, stat));
			int[] runs = { (int)Bat(0, BS.R), (int)Bat(1, BS.R) };
			// The biggest deficit each side faced: at the end of the other side's half-innings (runs only come
			// in one's own half, so in between it can only shrink).
			int[] deficit = new int[2];
			int home = 0, away = 0;
			for (int inning = 0; inning < lr.Line[1].Count; inning++)
			{
				away += lr.Line[1][inning];
				deficit[0] = Math.Max(deficit[0], away - home);
				if (inning < lr.Line[0].Count)
				{
					home += lr.Line[0][inning];
					deficit[1] = Math.Max(deficit[1], home - away);
				}
			}
			for (int t = 0; t < 2; t++)
			{
				int o = 1 - t;
				bool won = runs[t] > runs[o];
				foreach (int i in m.TeamBatters[t])
				{
					long h = st.Bat(i, BS.H), d2 = st.Bat(i, BS.D), d3 = st.Bat(i, BS.T), hr = st.Bat(i, BS.HR);
					if (h - d2 - d3 - hr >= 1 && d2 >= 1 && d3 >= 1 && hr >= 1) moments[t].Add(Missions.GameCycle);
					if (hr >= 3) moments[t].Add(Missions.GameThreeHomers);
				}
				for (int k = 0; k < lr.GrandSlams[t]; k++) moments[t].Add(Missions.GameGrandSlam);
				// Walk-off: the home side won with its last half played, in the 9th or later (a home side ahead
				// after the top of the 9th does not bat).
				if (t == 0 && won && lr.Line[0].Count == lr.Line[1].Count && lr.Line[1].Count >= 9) moments[t].Add(Missions.GameWalkOff);
				List<int> used = m.TeamPitchers[t].Where(p => st.Pit(p, PS.G) > 0).ToList();
				long outs = used.Sum(p => st.Pit(p, PS.Outs));
				if (outs >= 27 && Bat(o, BS.H) == 0 && runs[o] == 0)
				{
					bool perfect = Bat(o, BS.BB) == 0 && Bat(o, BS.HBP) == 0 && lr.Errors[t] == 0;
					moments[t].Add(Missions.GameNoHitter);
					if (perfect) moments[t].Add(Missions.GamePerfect);
					// The starter alone (user, 2026-11-05: the ones above allow relievers and are now the 팀 업적).
					if (won && used.Count == 1)
					{
						moments[t].Add(Missions.GameNoHitterSolo);
						if (perfect) moments[t].Add(Missions.GamePerfectSolo);
					}
				}
				if (won && runs[o] == 0 && used.Count == 1) moments[t].Add(Missions.GameShutout);
				if (won && used.Count == 1) moments[t].Add(Missions.GameComplete);
				foreach (int p in used.Where(p => st.Pit(p, PS.SO) >= 12)) moments[t].Add(Missions.GameTwelveK);
				if (won && deficit[t] >= 5) moments[t].Add(Missions.GameComeback5);
			}
			return new[] { moments[0].ToArray(), moments[1].ToArray() };
		}

		private static LeagueGame PlayGame(LeagueState s, int game, int home, int away, GameTeam[] teams, Lineup[] lineups, List<PlayerSeason> lines, TraitEffects.Effect[] traits,
			Dictionary<(int club, int card), PitcherLoad> fatigue, Dictionary<(int club, int card), PitcherLoad> fatigueAfter, bool post,
			int maxInnings = LeagueRules.MaxInnings, bool replayTies = false)
		{
			int seed = HashCode.Combine(s.UserCode, s.Season, game, home);
			GameEnv env = s_data.Env;
			int[] club = { home, away };
			SeasonModel m;
			BroadcastRecorder br;
			LeagueRecorder lr;
			SimStats st;
			// replayTies: a game still level after maxInnings is played again on another seed (the 끝장승부's
			// fallback past LeagueRules.EndlessInnings).
			for (int attempt = 0; ; attempt++)
			{
				m = CardGame.Build(teams[home], teams[away], env);
				TraitEffects.Apply(m, 0, traits[home]);
				TraitEffects.Apply(m, 1, traits[away]);
				// Each pitcher starts at the fatigue left from earlier games; tired or overused relievers sit out.
				for (int t = 0; t < 2 && m.Tac != null; t++)
				{
					for (int slot = 0; slot < m.Decks[t].Pitchers.Length; slot++)
					{
						int p = m.Decks[t].Pitchers[slot];
						if (p < 0 || !fatigue.TryGetValue((club[t], lineups[club[t]].Pitchers[slot]), out PitcherLoad load)) continue;
						m.Tac.CarryIn[p] = load.Units;
						m.Tac.DaysInRow[p] = load.Days;
						m.Tac.Last1[p] = load.Last1;
						m.Tac.Last2[p] = load.Last2;
					}
				}
				br = new BroadcastRecorder(s_data.Presets, seed);
				br.Attach(m, teams[home], teams[away]);
				lr = new LeagueRecorder(br);
				SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError, MaxInnings = maxInnings }, (ulong)(uint)seed);
				st = sim.PlayOne(0, 1, lr);
				SeasonModel played = m;
				SimStats result = st;
				long RunsOf(int t) => played.TeamBatters[t].Sum(i => result.Bat(i, BS.R));
				if (!replayTies || RunsOf(0) != RunsOf(1) || attempt >= 50) break;
				seed = HashCode.Combine(seed, attempt + 1);
			}
			// Fatigue left after the game: pitches × effort, less a day's recovery.
			for (int t = 0; t < 2 && m.Tac != null; t++)
			{
				for (int slot = 0; slot < m.Decks[t].Pitchers.Length; slot++)
				{
					int p = m.Decks[t].Pitchers[slot];
					if (p < 0) continue;
					int np = (int)st.Pit(p, PS.NP);
					bool pitched = st.Pit(p, PS.G) > 0;
					fatigueAfter[(club[t], lineups[club[t]].Pitchers[slot])] = new PitcherLoad
					{
						Units = m.Tac.R.CarryOut(m.Tac.CarryIn[p], m.Tac.Units(p, np), m.Tac.Limit[p]), Days = pitched ? m.Tac.DaysInRow[p] + 1 : 0, Last1 = np, Last2 = m.Tac.Last1[p]
					};
				}
			}
			long Bat(int t, int stat) => m.TeamBatters[t].Sum(i => st.Bat(i, stat));
			long Pit(int t, int stat) => m.TeamPitchers[t].Sum(p => st.Pit(p, stat));
			int[] runs = { (int)Bat(0, BS.R), (int)Bat(1, BS.R) };

			GameDetail d = BoxScore(m, st, lr, lineups[home], lineups[away]);
			d.Owner = new[] { s.Clubs[home].Owner, s.Clubs[away].Owner };
			int CardOfPitcher(int p)
			{
				int t = m.TeamPitchers[0].Contains(p) ? 0 : 1;
				int slot = m.Decks[t].SlotOf(p);
				return slot >= 0 ? lineups[club[t]].Pitchers[slot] : 0;
			}
			int CardOfBatter(int i)
			{
				int t = i < 9 ? 0 : 1;
				return lineups[club[t]].Batters[i - 9 * t];
			}

			// Standings and season totals (the regular season's only).
			for (int t = 0; t < 2 && !post; t++)
			{
				LeagueClub c = s.Clubs[club[t]];
				int rf = runs[t], ra = runs[1 - t];
				if (rf > ra) c.W++; else if (rf < ra) c.L++; else c.D++;
				c.RunsFor += rf;
				c.RunsAgainst += ra;
				ClubTotals k = c.Totals;
				k.AB += (int)Bat(t, BS.AB); k.H += (int)Bat(t, BS.H); k.HR += (int)Bat(t, BS.HR); k.SB += (int)Bat(t, BS.SB); k.BB += (int)Bat(t, BS.BB);
				k.E += lr.Errors[t];
				k.PH += (int)Bat(1 - t, BS.H); k.PHR += (int)Bat(1 - t, BS.HR); k.PK += (int)Bat(1 - t, BS.SO); k.PBB += (int)Bat(1 - t, BS.BB);
				k.ER += (int)Pit(t, PS.ER); k.Outs += (int)Pit(t, PS.Outs);
			}

			// Every card's line of this game (the nine starters bat; pitchers who appeared).
			for (int t = 0; t < 2; t++)
			{
				foreach (int i in m.TeamBatters[t])
				{
					lines.Add(new PlayerSeason
					{
						Season = s.Season, Club = club[t], CardIdx = CardOfBatter(i), G = 1,
						PA = (int)st.Bat(i, BS.PA), AB = (int)st.Bat(i, BS.AB), H = (int)st.Bat(i, BS.H), D = (int)st.Bat(i, BS.D), T = (int)st.Bat(i, BS.T),
						HR = (int)st.Bat(i, BS.HR), RBI = (int)st.Bat(i, BS.RBI), R = (int)st.Bat(i, BS.R), BB = (int)st.Bat(i, BS.BB), HBP = (int)st.Bat(i, BS.HBP),
						SO = (int)st.Bat(i, BS.SO), SB = (int)st.Bat(i, BS.SB), CS = (int)st.Bat(i, BS.CS), SF = (int)st.Bat(i, BS.SF), SH = (int)st.Bat(i, BS.SH),
						GIDP = (int)st.Bat(i, BS.GIDP),
						RispPA = (int)st.RispOf(i, SL.PA), RispAB = (int)st.RispOf(i, SL.AB), RispH = (int)st.RispOf(i, SL.H)
					});
				}
				List<int> used = m.TeamPitchers[t].Where(p => st.Pit(p, PS.G) > 0).ToList();
				foreach (int p in used)
				{
					bool start = st.Pit(p, PS.GS) > 0, complete = start && used.Count == 1;
					int outs = (int)st.Pit(p, PS.Outs), er = (int)st.Pit(p, PS.ER), pr = (int)st.Pit(p, PS.R);
					int[] against = lr.Against.GetValueOrDefault(p) ?? new int[3];
					lines.Add(new PlayerSeason
					{
						Season = s.Season, Club = club[t], CardIdx = CardOfPitcher(p), Pitcher = true, G = 1, GS = start ? 1 : 0,
						Outs = outs, BF = (int)st.Pit(p, PS.BF), PH = (int)st.Pit(p, PS.H), PHR = (int)st.Pit(p, PS.HR), PBB = (int)st.Pit(p, PS.BB),
						PHBP = (int)st.Pit(p, PS.HBP), PSO = (int)st.Pit(p, PS.SO), PR = pr, ER = er,
						W = (int)st.Pit(p, PS.W), L = (int)st.Pit(p, PS.L), SV = (int)st.Pit(p, PS.SV), HLD = (int)st.Pit(p, PS.HLD), BS = (int)st.Pit(p, PS.BS),
						QS = start && outs >= 18 && er <= 3 ? 1 : 0, CG = complete ? 1 : 0, SHO = complete && pr == 0 ? 1 : 0, NP = br.PitchesBy.GetValueOrDefault(p),
						PAB = against[0], PTB = against[1], PDP = against[2]
					});
				}
				// The recorder's counts against this side's pitchers should match the other side's batting lines (a new
				// PlayKind in the engine would break them: logged once a process, --leaguetest fails).
				int ab = m.TeamPitchers[t].Sum(p => lr.Against.GetValueOrDefault(p)?[0] ?? 0), tb = m.TeamPitchers[t].Sum(p => lr.Against.GetValueOrDefault(p)?[1] ?? 0);
				long bab = Bat(1 - t, BS.AB), btb = Bat(1 - t, BS.H) + Bat(1 - t, BS.D) + 2 * Bat(1 - t, BS.T) + 3 * Bat(1 - t, BS.HR);
				if (ab != bab || tb != btb)
				{
					if (AgainstMismatches++ == 0) ServerLog.Warn($"league: 피장타율 counts off in game {game} (side {t}): AB {ab}/{bab}, TB {tb}/{btb}");
				}
			}

			LeagueGame g = new LeagueGame { Season = s.Season, Game = game, Home = home, Away = away, HomeScore = runs[0], AwayScore = runs[1], Detail = d, Buff = br.Finish() };
			int mine = home == s.MyIndex ? 0 : away == s.MyIndex ? 1 : -1;
			g.Confirmed = mine < 0;
			if (mine >= 0)
			{
				// The user's game: MP by result and tier, 홈런볼 for every home run hit, paid when the
				// user confirms the result (ConfirmAndPay) so neither gives the result away early.
				g.Mp = LeagueRules.Reward(s.Tier, runs[mine], runs[1 - mine]);
				g.HomeRuns = (int)Bat(mine, BS.HR);
				g.Paid = false;
			}
			return g;
		}
	}
}
