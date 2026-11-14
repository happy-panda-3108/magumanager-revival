using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// One club of a league: the user's own (Owner = user code, lineup from the roster) or an AI club. In a
	// shared league (챔피언스) the other real teams are clubs too: not IsUser, but Owner = their user code.
	public sealed class LeagueClub
	{
		public int Index;
		public bool IsUser;
		public string Name = string.Empty;
		public byte Emblem;
		public uint Owner;
		public string Lineup = string.Empty;  // AI clubs (Lineup.Serialize); a shared league's real teams: their latest lineup
		public int Ball;
		public int LastRank;
		public int W, L, D, RunsFor, RunsAgainst;
		public ClubTotals Totals = new ClubTotals();
		// 챔피언스 AI clubs: the team-year deck (AiTeams.Deck); -1 = the tier's fixed AI club by place (AiTeams.Get).
		public int AiKey = -1;

		// A real team: the user's own or, in a shared league, another member (AI owners have the top bit set).
		public bool IsHuman => IsUser || (Owner != 0 && (Owner & 0x8000_0000u) == 0);

		// A shallow copy (Totals shared) for LeagueStore.ConfirmedView.
		public LeagueClub Copy() => (LeagueClub)MemberwiseClone();
	}

	// Season totals for the standings screen.
	public sealed class ClubTotals
	{
		public int AB { get; set; }
		public int H { get; set; }
		public int HR { get; set; }
		public int SB { get; set; }
		public int BB { get; set; }
		public int E { get; set; }
		public int PH { get; set; }
		public int PHR { get; set; }
		public int PK { get; set; }
		public int PBB { get; set; }
		public int ER { get; set; }
		public int Outs { get; set; }
	}

	public sealed class LeagueState
	{
		public uint UserCode;
		public int Tier;
		public int Season;
		public DateTime Start;      // local time of game 1 (LeagueCalendar: a cycle start)
		public int NextGame = 1;    // 1..144, 145 = regular season over
		public int MyIndex;
		public List<LeagueClub> Clubs = new List<LeagueClub>();
		// The common calendar (engine session, 2026-10-21): the first game played (a club that joined
		// mid-season plays from its next slot), the next postseason slot (1..19, 20 = over), the number of
		// the latest game played (postseason games are 145-163) and, once the season has closed at
		// maintenance, the tier of the next season (-1 before).
		public int FirstGame = 1;
		public int PostSlot = 1;
		public int LastGame;
		public int NextTier = -1;
		// A shared league (챔피언스, user 2026-10-28: real teams meet each other, AI decks fill the rest): every member
		// keeps its own copy (this state, its games and lines) with the same clubs in the same places; the games are
		// played once for all of them (LeagueService.AdvanceShared). null = the user's own league.
		public string Share;

		public DateTime SlotTime(int game) => game > LeagueRules.Games ? LeagueCalendar.PostSlotTime(Start, game - LeagueRules.Games) : LeagueRules.SlotTime(Start, game);
		// The latest regular-season game number (the slots so far, from 1 even after a mid-season start).
		public int Played => NextGame - 1;
		// Regular-season games this league has played (fewer than Played after a mid-season start).
		public int GamesPlayed => NextGame - FirstGame;
		// The tier whose ball cap applies now: the next season's from maintenance on.
		public int CapTier => NextTier >= 0 ? NextTier : Tier;

		// Standings order: win share, then run difference. Ties counted half a win, but the standings show 승 / (승 + 패) as
		// KBO does now (draws left out), so 12승 2무 15패 (.444) stood above 13승 16패 (.448) (AI tester 1, 11-10). From the
		// season that opens at KboPctFrom the order is by the 승률 shown; seasons already running (their postseason seeds,
		// awards and promotion) keep the old count.
		public static readonly DateTime KboPctFrom = new DateTime(2026, 11, 11, 0, 0, 0);

		//
		// 상대 전적 (user, 2026-11-10 20:0x: "승률이 동률일 때는 상대 전적에서 앞 서는 팀이 등수가 높아짐"), also from the KboPctFrom
		// season: clubs level in 승률 are ordered by their wins less losses in the games among themselves (KBO: 해당 구단 간
		// 전적), then by run difference as before. HeadToHead[i, j] = club i's regular-season wins over club j; null = read
		// from the stored games (LeagueStore.HeadToHeadOf). A view of the standings (ConfirmedView) sets its own.
		public int[,] HeadToHead;

		public List<LeagueClub> Ranked()
		{
			if (Start < KboPctFrom)
			{
				return Clubs.OrderByDescending(c => c.W + c.L + c.D == 0 ? 0 : (c.W + 0.5 * c.D) / (c.W + c.L + c.D))
					.ThenByDescending(c => c.RunsFor - c.RunsAgainst).ThenBy(c => c.Index).ToList();
			}
			// Equal records give the same double (one division each), so level clubs group exactly.
			static double Pct(LeagueClub c) => c.W + c.L == 0 ? 0 : c.W / (double)(c.W + c.L);
			List<IGrouping<double, LeagueClub>> levels = Clubs.GroupBy(Pct).OrderByDescending(g => g.Key).ToList();
			int[,] wins = levels.Any(g => g.Count() > 1) ? HeadToHead ?? LeagueService.Store?.HeadToHeadOf(UserCode, Season, NextGame + LastGame) : null;
			List<LeagueClub> ranked = new List<LeagueClub>();
			foreach (IGrouping<double, LeagueClub> level in levels)
			{
				List<LeagueClub> tied = level.ToList();
				int Among(LeagueClub c) => wins == null ? 0 : tied.Where(o => o != c && c.Index < wins.GetLength(0) && o.Index < wins.GetLength(0)).Sum(o => wins[c.Index, o.Index] - wins[o.Index, c.Index]);
				ranked.AddRange(tied.OrderByDescending(Among).ThenByDescending(c => c.RunsFor - c.RunsAgainst).ThenBy(c => c.Index));
			}
			return ranked;
		}

		public int RankOf(int club) => Ranked().FindIndex(c => c.Index == club) + 1;
	}

	// Box-score detail of one game (stored as JSON).
	public sealed class GameDetail
	{
		public int[] HomeLine { get; set; } = Array.Empty<int>();
		public int[] AwayLine { get; set; } = Array.Empty<int>();
		// H, E, BB, HR, SB, K, DP by side (0 home, 1 away). BB is 4사구 (walks + HBP) for games from 2026-11-10 (BoxScore).
		public int[][] Stats { get; set; } = { new int[7], new int[7] };
		public int WinIdx { get; set; }
		public int SaveIdx { get; set; }
		public int LossIdx { get; set; }
		public int[] BestIdx { get; set; } = new int[2];
		public int[] WorstIdx { get; set; } = new int[2];
		public uint[] Owner { get; set; } = new uint[2];
		// Both clubs' lineups at game time (the broadcast refers to their batting and staff slots).
		public string HomeLineup { get; set; } = string.Empty;
		public string AwayLineup { get; set; } = string.Empty;
		// 경기 순간 업적 keys by side (0 home, 1 away), once per occurrence (LeagueService.BoxScore; the systems
		// session's Missions.OnLeagueGames counts the user's side on confirming). Empty for games before 2026-10-22.
		public string[][] Moments { get; set; } = { Array.Empty<string>(), Array.Empty<string>() };
	}

	// A card's season line in league games (one row per league owner, season, club and card).
	public sealed class PlayerSeason
	{
		public int Season { get; set; }
		public int Club { get; set; }
		public int CardIdx { get; set; }
		public bool Pitcher { get; set; }
		// Batting.
		public int G { get; set; }
		public int PA { get; set; }
		public int AB { get; set; }
		public int H { get; set; }
		public int D { get; set; }
		public int T { get; set; }
		public int HR { get; set; }
		public int RBI { get; set; }
		public int R { get; set; }
		public int BB { get; set; }
		public int HBP { get; set; }
		public int SO { get; set; }
		public int SB { get; set; }
		public int CS { get; set; }
		public int SF { get; set; }
		public int SH { get; set; }
		public int GIDP { get; set; }
		// Pitching (G counts appearances for pitchers).
		public int GS { get; set; }
		public int Outs { get; set; }
		public int BF { get; set; }
		public int PH { get; set; }
		public int PHR { get; set; }
		public int PBB { get; set; }
		public int PHBP { get; set; }
		public int PSO { get; set; }
		public int PR { get; set; }
		public int ER { get; set; }
		public int W { get; set; }
		public int L { get; set; }
		public int SV { get; set; }
		public int HLD { get; set; }
		public int QS { get; set; }
		public int CG { get; set; }
		// 완봉: a complete game without a run allowed, whatever the result (user, 2026-10-30: "완봉승도 완투승이면서 완봉승
		// 인 것이고, 노히트노런, 퍼펙트게임 승리도 완투승이면서 완봉승이야"). Kept from then; LeagueStore.BackfillShutouts.
		public int SHO { get; set; }
		public int NP { get; set; }  // pitches thrown (the broadcast's pitch counts)
		public int BS { get; set; }  // blown saves (no client field yet: kept for records)
		// Against the batters (LeagueRecorder.Against, from 2026-10-30): at-bats, total bases, double plays induced, for
		// 피장타율 and 병살 유도. Games before then have none, so that season's 피장타율 is over the games since.
		public int PAB { get; set; }
		public int PTB { get; set; }
		public int PDP { get; set; }
		// 득점권 (runner on 2nd or 3rd as the plate appearance began; engine SimStats.Risp, from 2026-10-21).
		public int RispPA { get; set; }
		public int RispAB { get; set; }
		public int RispH { get; set; }

		// A copy for another member of a shared league (its own season number).
		public PlayerSeason CopyFor(int season)
		{
			PlayerSeason p = (PlayerSeason)MemberwiseClone();
			p.Season = season;
			return p;
		}

		public void Add(PlayerSeason o)
		{
			G += o.G; PA += o.PA; AB += o.AB; H += o.H; D += o.D; T += o.T; HR += o.HR; RBI += o.RBI; R += o.R; BB += o.BB; HBP += o.HBP; SO += o.SO;
			SB += o.SB; CS += o.CS; SF += o.SF; SH += o.SH; GIDP += o.GIDP;
			RispPA += o.RispPA; RispAB += o.RispAB; RispH += o.RispH;
			GS += o.GS; Outs += o.Outs; BF += o.BF; PH += o.PH; PHR += o.PHR; PBB += o.PBB; PHBP += o.PHBP; PSO += o.PSO; PR += o.PR; ER += o.ER;
			W += o.W; L += o.L; SV += o.SV; HLD += o.HLD; QS += o.QS; CG += o.CG; NP += o.NP; BS += o.BS;
			SHO += o.SHO; PAB += o.PAB; PTB += o.PTB; PDP += o.PDP;
		}
	}

	// A pitcher's load carried between league games: fatigue units (TacticModel.CarryIn) and, for
	// the bullpen rest rules, games pitched in a row and the pitches of the last two games.
	public sealed class PitcherLoad
	{
		public double Units;
		public int Days, Last1, Last2;
	}

	// One card's line in one of the user's games (선수 정보 > 성적 > 최근성적), with the season batting
	// average (batters) or ERA (pitchers) right after that game, as box scores show it.
	public sealed class PlayerGame
	{
		public int Game { get; set; }
		public PlayerSeason Line { get; set; } = new PlayerSeason();
		public double SeasonRate { get; set; }
	}

	public sealed class LeagueGame
	{
		public int Season, Game, Home, Away, HomeScore, AwayScore;
		public GameDetail Detail;
		public int Mp, HomeRuns;
		public bool Confirmed;
		// The user's MP and 홈런볼 for the game are paid when its result is confirmed (engine session,
		// 2026-10-19: paying at play time gave results away before the unconfirmed-games screen).
		public bool Paid = true;
		public byte[] Buff;

		// The game as one member of a shared league keeps it: its season number, and its own club's MP and 홈런볼
		// (unconfirmed and unpaid) when it played in it (LeagueService.PlayGame does the same for the league it plays).
		public LeagueGame CopyFor(int season, int myIndex, int tier)
		{
			LeagueGame g = (LeagueGame)MemberwiseClone();
			g.Season = season;
			int mine = Home == myIndex ? 0 : Away == myIndex ? 1 : -1;
			g.Confirmed = mine < 0;
			g.Paid = mine < 0;
			g.Mp = mine < 0 ? 0 : LeagueRules.Reward(tier, mine == 0 ? HomeScore : AwayScore, mine == 0 ? AwayScore : HomeScore);
			g.HomeRuns = mine < 0 || Detail == null ? 0 : Detail.Stats[mine][3];
			return g;
		}
	}

	public sealed class LeagueStore
	{
		private readonly string m_cs;

		public LeagueStore(string path)
		{
			m_cs = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
			Execute(@"
				CREATE TABLE IF NOT EXISTS league (
					user_code INTEGER PRIMARY KEY,
					tier INTEGER NOT NULL,
					season INTEGER NOT NULL,
					start TEXT NOT NULL,
					next_game INTEGER NOT NULL,
					my_index INTEGER NOT NULL);
				CREATE TABLE IF NOT EXISTS league_club (
					user_code INTEGER NOT NULL,
					club INTEGER NOT NULL,
					is_user INTEGER NOT NULL,
					name TEXT NOT NULL,
					emblem INTEGER NOT NULL,
					owner INTEGER NOT NULL,
					lineup TEXT NOT NULL,
					ball INTEGER NOT NULL,
					last_rank INTEGER NOT NULL,
					w INTEGER NOT NULL, l INTEGER NOT NULL, d INTEGER NOT NULL,
					rf INTEGER NOT NULL, ra INTEGER NOT NULL,
					totals TEXT NOT NULL,
					PRIMARY KEY (user_code, club));
				CREATE TABLE IF NOT EXISTS league_game (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					game INTEGER NOT NULL,
					home INTEGER NOT NULL,
					away INTEGER NOT NULL,
					home_score INTEGER NOT NULL,
					away_score INTEGER NOT NULL,
					detail TEXT NOT NULL,
					mp INTEGER NOT NULL,
					home_runs INTEGER NOT NULL,
					confirmed INTEGER NOT NULL,
					buff BLOB,
					PRIMARY KEY (user_code, season, game, home));
				CREATE TABLE IF NOT EXISTS league_history (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					tier INTEGER NOT NULL,
					rank INTEGER NOT NULL,
					w INTEGER NOT NULL, l INTEGER NOT NULL, d INTEGER NOT NULL,
					next_tier INTEGER NOT NULL,
					PRIMARY KEY (user_code, season));
				CREATE TABLE IF NOT EXISTS league_player (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					club INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					line TEXT NOT NULL,
					PRIMARY KEY (user_code, season, club, card_idx));
				CREATE TABLE IF NOT EXISTS league_player_game (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					game INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					line TEXT NOT NULL,
					PRIMARY KEY (user_code, season, game, card_idx));
				CREATE TABLE IF NOT EXISTS league_fatigue (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					club INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					units REAL NOT NULL,
					PRIMARY KEY (user_code, season, club, card_idx));
				CREATE TABLE IF NOT EXISTS wallet (
					user_code INTEGER PRIMARY KEY,
					hball INTEGER NOT NULL DEFAULT 0);
				CREATE TABLE IF NOT EXISTS league_pending_lines (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					game INTEGER NOT NULL,
					my_club INTEGER NOT NULL,
					lines TEXT NOT NULL,
					PRIMARY KEY (user_code, season, game));
				CREATE TABLE IF NOT EXISTS league_post_player (
					user_code INTEGER NOT NULL,
					season INTEGER NOT NULL,
					series INTEGER NOT NULL,
					club INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					line TEXT NOT NULL,
					PRIMARY KEY (user_code, season, series, club, card_idx));");
			// Games stored before pay-on-confirm were paid when played (paid = 1).
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('league_game') WHERE name = 'paid'";
			if ((long)cmd.ExecuteScalar() == 0)
			{
				cmd.CommandText = "ALTER TABLE league_game ADD COLUMN paid INTEGER NOT NULL DEFAULT 1";
				cmd.ExecuteNonQuery();
			}
			// Bullpen rest: games in a row and the last two games' pitches (league_fatigue first had units only).
			foreach (string col in new[] { "days", "last1", "last2" })
			{
				cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('league_fatigue') WHERE name = '{col}'";
				if ((long)cmd.ExecuteScalar() == 0)
				{
					cmd.CommandText = $"ALTER TABLE league_fatigue ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0";
					cmd.ExecuteNonQuery();
				}
			}
			// The common calendar's league columns (LeagueState.FirstGame, PostSlot, LastGame, NextTier).
			foreach ((string col, string def) in new[] { ("first_game", "1"), ("post_slot", "1"), ("last_game", "-1"), ("next_tier", "-1") })
			{
				cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('league') WHERE name = '{col}'";
				if ((long)cmd.ExecuteScalar() == 0)
				{
					cmd.CommandText = $"ALTER TABLE league ADD COLUMN {col} INTEGER NOT NULL DEFAULT {def}";
					cmd.ExecuteNonQuery();
				}
			}
			cmd.CommandText = "UPDATE league SET last_game = next_game - 1 WHERE last_game < 0";
			cmd.ExecuteNonQuery();
			// 챔피언스 (2026-10-28): the shared league a team's league belongs to, and an AI club's team-year deck.
			cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('league') WHERE name = 'share'";
			if ((long)cmd.ExecuteScalar() == 0)
			{
				cmd.CommandText = "ALTER TABLE league ADD COLUMN share TEXT";
				cmd.ExecuteNonQuery();
			}
			cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('league_club') WHERE name = 'ai_key'";
			if ((long)cmd.ExecuteScalar() == 0)
			{
				cmd.CommandText = "ALTER TABLE league_club ADD COLUMN ai_key INTEGER NOT NULL DEFAULT -1";
				cmd.ExecuteNonQuery();
			}
			// The other clubs' last games, for their cards' 최근성적 (systems session, 2026-11-01; user: "상대팀이더라도 최근 5경기는
			// 보여줘야할거 아니냐"): written as each slot is revealed, ClubGamesKept per card (league_player_game is the user's club's).
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS league_club_game (
				user_code INTEGER NOT NULL,
				season INTEGER NOT NULL,
				club INTEGER NOT NULL,
				card_idx INTEGER NOT NULL,
				game INTEGER NOT NULL,
				line TEXT NOT NULL,
				PRIMARY KEY (user_code, season, club, card_idx, game))";
			cmd.ExecuteNonQuery();
		}

		private void Execute(string sql)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = sql;
			cmd.ExecuteNonQuery();
		}

		private SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(m_cs);
			c.Open();
			return c;
		}

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Teams that finished the tutorial, with name and emblem (the shared team table).
		public List<(uint code, string name, byte emblem, byte grade)> ActiveTeams()
		{
			List<(uint, string, byte, byte)> list = new List<(uint, string, byte, byte)>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT user_code, team_name, emblem, league_grade FROM team WHERE tutorial_done = 1 AND team_name <> ''";
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add(((uint)r.GetInt64(0), r.GetString(1), (byte)r.GetInt64(2), (byte)r.GetInt64(3)));
			return list;
		}

		// 레전드 리그 (systems session, 2026-11-08; LeagueRules.NewStructure): whether the six-tier switch is done (league_fix 'tiers6').
		public bool Tiers6Done()
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
			cmd.ExecuteNonQuery();
			cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'tiers6'";
			return (long)cmd.ExecuteScalar() > 0;
		}

		// The six-tier switch, once, at the first 편성 for a season from LeagueRules.NewStructureFrom (LeagueService.RenewAi): every
		// stored 메이저 (3) and 챔피언스 (4) tier moves up one (레전드 4, 챔피언스 5) with its next tier, in the leagues, their
		// history, the 시즌 완주 보너스 rows and the AI teams and rules; the fixed clubs of tiers that now draw decks (old 마이너 at 2,
		// old 메이저 at 3 → 4) go; every team's shown grade is set again from its latest league (its next tier once closed).
		// This season's 메이저 clubs thus go on by their finish under the 레전드 rule (1-3 챔피언스, 4-6 레전드, 7-10 메이저), as
		// they would have under the old one, and 마이너's top four go to the new 메이저 (3). Then LeagueRules runs six tiers.
		public void MigrateTiers6(string why)
		{
			if (Tiers6Done())
			{
				LeagueRules.UseNewStructure();
				return;
			}
			using SqliteConnection c = Open();
			void Sql(string text)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = text;
				cmd.ExecuteNonQuery();
			}
			int Count(string text)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = text;
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
			int leagues = Count("SELECT COUNT(*) FROM league WHERE tier >= 3"), history = Count("SELECT COUNT(*) FROM league_history WHERE tier >= 3");
			Sql("BEGIN");
			try
			{
				Sql("UPDATE league SET next_tier = CASE WHEN next_tier >= 0 THEN next_tier + 1 ELSE next_tier END, tier = tier + 1 WHERE tier >= 3");
				Sql("UPDATE league_history SET next_tier = CASE WHEN next_tier >= 0 THEN next_tier + 1 ELSE next_tier END, tier = tier + 1 WHERE tier >= 3");
				if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'season_bonus'") > 0)
				{
					Sql("UPDATE season_bonus SET tier = tier + 1 WHERE tier >= 3");
				}
				if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'league_ai_team'") > 0)
				{
					Sql("DELETE FROM league_ai_team WHERE tier IN (2, 3) AND slot < 9");
					Sql("UPDATE league_ai_team SET tier = 5 WHERE tier = 4");
					Sql("UPDATE league_ai_team SET tier = 4 WHERE tier = 3");
				}
				if (Count("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'league_ai_rule'") > 0)
				{
					Sql("UPDATE league_ai_rule SET tier = 5 WHERE tier = 4");
					Sql("UPDATE league_ai_rule SET tier = 4 WHERE tier = 3");
				}
				// Shown grades (eLeagueGrade): teams without a league by the old grade (마이너 2 → 트리플A 4, 메이저 5 → 레전드 7), the
				// rest from their latest league below.
				Sql("UPDATE team SET league_grade = CASE league_grade WHEN 2 THEN 4 WHEN 5 THEN 7 ELSE league_grade END");
				LeagueRules.UseNewStructure();
				List<(long user, int tier)> latest = new List<(long, int)>();
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = @"SELECT l.user_code, CASE WHEN l.next_tier >= 0 THEN l.next_tier ELSE l.tier END FROM league l
						JOIN (SELECT user_code, MAX(season) AS season FROM league GROUP BY user_code) m ON m.user_code = l.user_code AND m.season = l.season";
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) latest.Add((r.GetInt64(0), r.GetInt32(1)));
				}
				foreach ((long user, int tier) in latest)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "UPDATE team SET league_grade = $g WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$g", (int)LeagueRules.TierGrade[Math.Clamp(tier, 0, LeagueRules.Tiers - 1)]);
					cmd.Parameters.AddWithValue("$u", user);
					cmd.ExecuteNonQuery();
				}
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "INSERT INTO league_fix (name, done) VALUES ('tiers6', $t)";
					cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
					cmd.ExecuteNonQuery();
				}
				Sql("COMMIT");
			}
			catch
			{
				Sql("ROLLBACK");
				LeagueRules.UseNewStructure(false);
				throw;
			}
			ServerLog.Info($"league: six tiers from now ({why}): {leagues} league row(s) and {history} history row(s) of 메이저/챔피언스 moved up one "
				+ $"(레전드 4, 챔피언스 5), grades of {Count("SELECT COUNT(DISTINCT user_code) FROM league")} team(s) set again from their leagues");
		}

		// 더블A (systems session, 2026-11-14; LeagueRules.Seven): whether the seven-tier switch is done (league_fix 'tiers7').
		public bool Tiers7Done()
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
			cmd.ExecuteNonQuery();
			cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'tiers7'";
			return (long)cmd.ExecuteScalar() > 0;
		}

		// The seven-tier switch, once, at the first 편성 for a season from LeagueRules.SevenFrom (LeagueService.RenewAi), after the
		// six-tier one: every stored tier from 트리플A (2) up moves up one (트리플A 3, 메이저 4, 레전드 5, 챔피언스 6) in the leagues,
		// their history, the 시즌 완주 보너스 rows and the AI decks (top tier first, the keys being (tier, slot)). A next tier
		// from those tiers moves with them, and one going down to 루키 (트리플A's bottom three) goes to the new 더블A (2) instead;
		// 루키's leagues keep theirs, so its top four go to 더블A. The AI rules are dropped, so the 편성 right after makes every
		// deck tier again under the new ball caps (트리플A 145 … 챔피언스 160) and makes 더블A's. Grades are set again from each
		// team's latest league.
		public void MigrateTiers7(string why)
		{
			if (Tiers7Done())
			{
				LeagueRules.UseSeven();
				return;
			}
			if (!Tiers6Done())
			{
				ServerLog.Warn($"league: seven tiers asked ({why}) before the six-tier switch; not done");
				return;
			}
			using SqliteConnection c = Open();
			void Sql(string text)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = text;
				cmd.ExecuteNonQuery();
			}
			int Count(string text)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = text;
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
			bool Has(string table) => Count($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'") > 0;
			int leagues = Count("SELECT COUNT(*) FROM league WHERE tier >= 2"), history = Count("SELECT COUNT(*) FROM league_history WHERE tier >= 2"),
				toDoubleA = Count("SELECT COUNT(*) FROM league WHERE tier >= 2 AND next_tier = 1") + Count("SELECT COUNT(*) FROM league WHERE tier = 1 AND next_tier = 2");
			Sql("BEGIN");
			try
			{
				// One statement per table: SQLite reads the old tier in the CASE.
				const string NextOf = "CASE WHEN next_tier >= 2 THEN next_tier + 1 WHEN next_tier = 1 THEN 2 ELSE next_tier END";
				Sql($"UPDATE league SET next_tier = {NextOf}, tier = tier + 1 WHERE tier >= 2");
				Sql($"UPDATE league_history SET next_tier = {NextOf}, tier = tier + 1 WHERE tier >= 2");
				if (Has("season_bonus"))
				{
					Sql("UPDATE season_bonus SET tier = tier + 1 WHERE tier >= 2");
				}
				if (Has("league_ai_team"))
				{
					for (int t = 5; t >= 2; t--) Sql($"UPDATE league_ai_team SET tier = {t + 1} WHERE tier = {t}");
				}
				if (Has("league_ai_rule"))
				{
					Sql("DELETE FROM league_ai_rule");
				}
				LeagueRules.UseSeven();
				List<(long user, int tier)> latest = new List<(long, int)>();
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = @"SELECT l.user_code, CASE WHEN l.next_tier >= 0 THEN l.next_tier ELSE l.tier END FROM league l
						JOIN (SELECT user_code, MAX(season) AS season FROM league GROUP BY user_code) m ON m.user_code = l.user_code AND m.season = l.season";
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) latest.Add((r.GetInt64(0), r.GetInt32(1)));
				}
				foreach ((long user, int tier) in latest)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "UPDATE team SET league_grade = $g WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$g", (int)LeagueRules.TierGrade[Math.Clamp(tier, 0, LeagueRules.Tiers - 1)]);
					cmd.Parameters.AddWithValue("$u", user);
					cmd.ExecuteNonQuery();
				}
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "INSERT INTO league_fix (name, done) VALUES ('tiers7', $t)";
					cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o"));
					cmd.ExecuteNonQuery();
				}
				Sql("COMMIT");
			}
			catch
			{
				Sql("ROLLBACK");
				LeagueRules.UseSeven(false);
				throw;
			}
			ServerLog.Info($"league: seven tiers from now ({why}): {leagues} league row(s) and {history} history row(s) from 트리플A up moved up one "
				+ $"(트리플A 3 … 챔피언스 6), {toDoubleA} team(s) going to 더블A, AI rules dropped (decks made again), grades of "
				+ $"{Count("SELECT COUNT(DISTINCT user_code) FROM league")} team(s) set again from their leagues");
		}

		public void SetTeamGrade(uint user, byte grade)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "UPDATE team SET league_grade = $g WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$g", (int)grade);
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.ExecuteNonQuery();
		}

		public LeagueState Load(uint user)
		{
			using SqliteConnection c = Open();
			LeagueState s;
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT tier, season, start, next_game, my_index, first_game, post_slot, last_game, next_tier, share FROM league WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				using SqliteDataReader r = cmd.ExecuteReader();
				if (!r.Read()) return null;
				s = new LeagueState
				{
					UserCode = user, Tier = r.GetInt32(0), Season = r.GetInt32(1),
					Start = DateTime.Parse(r.GetString(2), Inv, DateTimeStyles.RoundtripKind), NextGame = r.GetInt32(3), MyIndex = r.GetInt32(4),
					FirstGame = r.GetInt32(5), PostSlot = r.GetInt32(6), LastGame = r.GetInt32(7), NextTier = r.GetInt32(8),
					Share = r.IsDBNull(9) ? null : r.GetString(9)
				};
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT club, is_user, name, emblem, owner, lineup, ball, last_rank, w, l, d, rf, ra, totals, ai_key FROM league_club WHERE user_code = $u ORDER BY club";
				cmd.Parameters.AddWithValue("$u", (long)user);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					s.Clubs.Add(new LeagueClub
					{
						Index = r.GetInt32(0), IsUser = r.GetInt32(1) != 0, Name = r.GetString(2), Emblem = (byte)r.GetInt32(3), Owner = (uint)r.GetInt64(4),
						Lineup = r.GetString(5), Ball = r.GetInt32(6), LastRank = r.GetInt32(7), W = r.GetInt32(8), L = r.GetInt32(9), D = r.GetInt32(10),
						RunsFor = r.GetInt32(11), RunsAgainst = r.GetInt32(12), Totals = JsonSerializer.Deserialize<ClubTotals>(r.GetString(13)) ?? new ClubTotals(),
						AiKey = r.GetInt32(14)
					});
				}
			}
			return s;
		}

		// The members of a shared league: every team whose league row has this share.
		public List<uint> ShareMembers(string share)
		{
			List<uint> list = new List<uint>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT user_code FROM league WHERE share = $s ORDER BY user_code";
			cmd.Parameters.AddWithValue("$s", share);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add((uint)r.GetInt64(0));
			return list;
		}

		// Every shared league that some team's league row belongs to.
		public List<string> Shares()
		{
			List<string> list = new List<string>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT DISTINCT share FROM league WHERE share IS NOT NULL ORDER BY share";
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add(r.GetString(0));
			return list;
		}

		// League row and every club, in one transaction.
		public void Save(LeagueState s)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = @"INSERT OR REPLACE INTO league (user_code, tier, season, start, next_game, my_index, first_game, post_slot, last_game, next_tier, share)
					VALUES ($u, $t, $s, $st, $n, $m, $fg, $ps, $lg, $nt, $sh)";
				cmd.Parameters.AddWithValue("$sh", (object)s.Share ?? DBNull.Value);
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$t", s.Tier);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$st", s.Start.ToString("o", Inv));
				cmd.Parameters.AddWithValue("$n", s.NextGame);
				cmd.Parameters.AddWithValue("$m", s.MyIndex);
				cmd.Parameters.AddWithValue("$fg", s.FirstGame);
				cmd.Parameters.AddWithValue("$ps", s.PostSlot);
				cmd.Parameters.AddWithValue("$lg", s.LastGame);
				cmd.Parameters.AddWithValue("$nt", s.NextTier);
				cmd.ExecuteNonQuery();
			}
			foreach (LeagueClub k in s.Clubs)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = @"INSERT OR REPLACE INTO league_club (user_code, club, is_user, name, emblem, owner, lineup, ball, last_rank, w, l, d, rf, ra, totals, ai_key)
					VALUES ($u, $c, $iu, $n, $e, $o, $li, $b, $lr, $w, $l, $d, $rf, $ra, $t, $ak)";
				cmd.Parameters.AddWithValue("$ak", k.AiKey);
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$c", k.Index);
				cmd.Parameters.AddWithValue("$iu", k.IsUser ? 1 : 0);
				cmd.Parameters.AddWithValue("$n", k.Name);
				cmd.Parameters.AddWithValue("$e", (int)k.Emblem);
				cmd.Parameters.AddWithValue("$o", (long)k.Owner);
				cmd.Parameters.AddWithValue("$li", k.Lineup);
				cmd.Parameters.AddWithValue("$b", k.Ball);
				cmd.Parameters.AddWithValue("$lr", k.LastRank);
				cmd.Parameters.AddWithValue("$w", k.W);
				cmd.Parameters.AddWithValue("$l", k.L);
				cmd.Parameters.AddWithValue("$d", k.D);
				cmd.Parameters.AddWithValue("$rf", k.RunsFor);
				cmd.Parameters.AddWithValue("$ra", k.RunsAgainst);
				cmd.Parameters.AddWithValue("$t", JsonSerializer.Serialize(k.Totals));
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		public void AddGames(uint user, IEnumerable<LeagueGame> games)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (LeagueGame g in games)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = @"INSERT OR REPLACE INTO league_game (user_code, season, game, home, away, home_score, away_score, detail, mp, home_runs, confirmed, buff, paid)
					VALUES ($u, $s, $g, $h, $a, $hs, $as, $d, $mp, $hr, $c, $b, $p)";
				cmd.Parameters.AddWithValue("$p", g.Paid ? 1 : 0);
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", g.Season);
				cmd.Parameters.AddWithValue("$g", g.Game);
				cmd.Parameters.AddWithValue("$h", g.Home);
				cmd.Parameters.AddWithValue("$a", g.Away);
				cmd.Parameters.AddWithValue("$hs", g.HomeScore);
				cmd.Parameters.AddWithValue("$as", g.AwayScore);
				cmd.Parameters.AddWithValue("$d", JsonSerializer.Serialize(g.Detail));
				cmd.Parameters.AddWithValue("$mp", g.Mp);
				cmd.Parameters.AddWithValue("$hr", g.HomeRuns);
				cmd.Parameters.AddWithValue("$c", g.Confirmed ? 1 : 0);
				cmd.Parameters.Add("$b", SqliteType.Blob).Value = (object)g.Buff ?? DBNull.Value;
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// Games of a season (optionally one game number, or only the user's club's games).
		public List<LeagueGame> Games(uint user, int season, int? game = null, int? club = null, bool unconfirmedOnly = false, bool withBuff = false)
		{
			List<LeagueGame> list = new List<LeagueGame>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = $@"SELECT season, game, home, away, home_score, away_score, detail, mp, home_runs, confirmed, paid{(withBuff ? ", buff" : "")}
				FROM league_game WHERE user_code = $u AND season = $s"
				+ (game != null ? " AND game = $g" : "")
				+ (club != null ? " AND (home = $c OR away = $c)" : "")
				+ (unconfirmedOnly ? " AND confirmed = 0" : "")
				+ " ORDER BY game, home";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			if (game != null) cmd.Parameters.AddWithValue("$g", game.Value);
			if (club != null) cmd.Parameters.AddWithValue("$c", club.Value);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				list.Add(new LeagueGame
				{
					Season = r.GetInt32(0), Game = r.GetInt32(1), Home = r.GetInt32(2), Away = r.GetInt32(3), HomeScore = r.GetInt32(4), AwayScore = r.GetInt32(5),
					Detail = JsonSerializer.Deserialize<GameDetail>(r.GetString(6)), Mp = r.GetInt32(7), HomeRuns = r.GetInt32(8), Confirmed = r.GetInt32(9) != 0, Paid = r.GetInt32(10) != 0,
					Buff = withBuff && !r.IsDBNull(11) ? (byte[])r[11] : null
				});
			}
			return list;
		}

		// 상대 전적 for the standings (LeagueState.Ranked): [i, j] = club i's wins over club j in the season's regular-season
		// games numbered below `before`. Kept per (user, season, before) until the season moves on (stamp).
		private readonly Dictionary<(uint user, int season, int before), (int stamp, int[,] wins)> m_headToHead = new Dictionary<(uint, int, int), (int, int[,])>();

		public int[,] HeadToHeadOf(uint user, int season, int stamp, int before = int.MaxValue)
		{
			lock (m_headToHead)
			{
				if (m_headToHead.TryGetValue((user, season, before), out var kept) && kept.stamp == stamp) return kept.wins;
				List<(int home, int away, int hs, int aws)> rows = new List<(int, int, int, int)>();
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT home, away, home_score, away_score FROM league_game WHERE user_code = $u AND season = $s AND game <= $max AND game < $b";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					cmd.Parameters.AddWithValue("$max", LeagueRules.Games);
					cmd.Parameters.AddWithValue("$b", before);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) rows.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
				}
				int n = rows.Count == 0 ? 0 : rows.Max(x => Math.Max(x.home, x.away)) + 1;
				int[,] wins = new int[n, n];
				foreach ((int home, int away, int hs, int aws) in rows)
				{
					if (hs > aws) wins[home, away]++;
					else if (aws > hs) wins[away, home]++;
				}
				if (m_headToHead.Count > 400) m_headToHead.Clear();
				m_headToHead[(user, season, before)] = (stamp, wins);
				return wins;
			}
		}

		// Adds one slot's game lines to the season rows (read, add, write in one transaction).
		public void AddPlayerLines(uint user, IEnumerable<PlayerSeason> lines)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (PlayerSeason p in lines)
			{
				PlayerSeason row = p;
				using (SqliteCommand get = c.CreateCommand())
				{
					get.Transaction = tx;
					get.CommandText = "SELECT line FROM league_player WHERE user_code = $u AND season = $s AND club = $c AND card_idx = $i";
					get.Parameters.AddWithValue("$u", (long)user);
					get.Parameters.AddWithValue("$s", p.Season);
					get.Parameters.AddWithValue("$c", p.Club);
					get.Parameters.AddWithValue("$i", p.CardIdx);
					if (get.ExecuteScalar() is string json && JsonSerializer.Deserialize<PlayerSeason>(json) is PlayerSeason old)
					{
						old.Add(p);
						row = old;
					}
				}
				using SqliteCommand put = c.CreateCommand();
				put.Transaction = tx;
				put.CommandText = "INSERT OR REPLACE INTO league_player (user_code, season, club, card_idx, line) VALUES ($u, $s, $c, $i, $l)";
				put.Parameters.AddWithValue("$u", (long)user);
				put.Parameters.AddWithValue("$s", p.Season);
				put.Parameters.AddWithValue("$c", p.Club);
				put.Parameters.AddWithValue("$i", p.CardIdx);
				put.Parameters.AddWithValue("$l", JsonSerializer.Serialize(row));
				put.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// Season lines of a league (every club, or one club).
		public List<PlayerSeason> PlayerLines(uint user, int season, int? club = null)
		{
			List<PlayerSeason> list = new List<PlayerSeason>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT line FROM league_player WHERE user_code = $u AND season = $s" + (club != null ? " AND club = $c" : "");
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			if (club != null) cmd.Parameters.AddWithValue("$c", club.Value);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				if (JsonSerializer.Deserialize<PlayerSeason>(r.GetString(0)) is PlayerSeason p) list.Add(p);
			}
			return list;
		}

		// Postseason lines (engine session, 2026-10-21): kept apart from the regular season's, by series
		// (PostSeries: 0 와일드카드, 1 준PO, 2 PO, 3 한국시리즈), added as each game is played.
		public void AddPostLines(uint user, int season, PostSeries series, IEnumerable<PlayerSeason> lines)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (PlayerSeason p in lines)
			{
				PlayerSeason row = p;
				using (SqliteCommand get = c.CreateCommand())
				{
					get.Transaction = tx;
					get.CommandText = "SELECT line FROM league_post_player WHERE user_code = $u AND season = $s AND series = $k AND club = $c AND card_idx = $i";
					get.Parameters.AddWithValue("$u", (long)user);
					get.Parameters.AddWithValue("$s", season);
					get.Parameters.AddWithValue("$k", (int)series);
					get.Parameters.AddWithValue("$c", p.Club);
					get.Parameters.AddWithValue("$i", p.CardIdx);
					if (get.ExecuteScalar() is string json && JsonSerializer.Deserialize<PlayerSeason>(json) is PlayerSeason old)
					{
						old.Add(p);
						row = old;
					}
				}
				using SqliteCommand put = c.CreateCommand();
				put.Transaction = tx;
				put.CommandText = "INSERT OR REPLACE INTO league_post_player (user_code, season, series, club, card_idx, line) VALUES ($u, $s, $k, $c, $i, $l)";
				put.Parameters.AddWithValue("$u", (long)user);
				put.Parameters.AddWithValue("$s", season);
				put.Parameters.AddWithValue("$k", (int)series);
				put.Parameters.AddWithValue("$c", p.Club);
				put.Parameters.AddWithValue("$i", p.CardIdx);
				put.Parameters.AddWithValue("$l", JsonSerializer.Serialize(row));
				put.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// Postseason lines of a season: one series, or every series (null; a card's lines of several series
		// come as separate rows), optionally one club.
		public List<PlayerSeason> PostLines(uint user, int season, PostSeries? series = null, int? club = null)
		{
			List<PlayerSeason> list = new List<PlayerSeason>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT line FROM league_post_player WHERE user_code = $u AND season = $s"
				+ (series != null ? " AND series = $k" : "") + (club != null ? " AND club = $c" : "");
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			if (series != null) cmd.Parameters.AddWithValue("$k", (int)series.Value);
			if (club != null) cmd.Parameters.AddWithValue("$c", club.Value);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				if (JsonSerializer.Deserialize<PlayerSeason>(r.GetString(0)) is PlayerSeason p) list.Add(p);
			}
			return list;
		}

		// The user's club's lines of one game, each with its season rate after the game. Call after
		// AddPlayerLines, so the season rows already include the game.
		public void AddGameLines(uint user, int game, IEnumerable<PlayerSeason> lines)
		{
			List<PlayerSeason> mine = lines.ToList();
			if (mine.Count == 0) return;
			Dictionary<int, PlayerSeason> totals = PlayerLines(user, mine[0].Season, mine[0].Club).ToDictionary(p => p.CardIdx);
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (PlayerSeason p in mine)
			{
				PlayerSeason t = totals.GetValueOrDefault(p.CardIdx) ?? p;
				double rate = p.Pitcher ? (t.Outs > 0 ? t.ER * 27.0 / t.Outs : 0) : (t.AB > 0 ? (double)t.H / t.AB : 0);
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = "INSERT OR REPLACE INTO league_player_game (user_code, season, game, card_idx, line) VALUES ($u, $s, $g, $i, $l)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", p.Season);
				cmd.Parameters.AddWithValue("$g", game);
				cmd.Parameters.AddWithValue("$i", p.CardIdx);
				cmd.Parameters.AddWithValue("$l", JsonSerializer.Serialize(new PlayerGame { Game = game, Line = p, SeasonRate = rate }));
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// A slot's player lines wait here until the user sees the game's result: its scoreboard or its
		// confirmation (user, 2026-10-20: the lines were in the records and the card's 최근성적 while the
		// game was still on air). RevealLines adds them to the season rows and the per-game lines.
		public void AddPendingLines(uint user, int season, int game, int myClub, IEnumerable<PlayerSeason> lines)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO league_pending_lines (user_code, season, game, my_club, lines) VALUES ($u, $s, $g, $m, $l)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$g", game);
			cmd.Parameters.AddWithValue("$m", myClub);
			cmd.Parameters.AddWithValue("$l", JsonSerializer.Serialize(lines.ToList()));
			cmd.ExecuteNonQuery();
		}

		// Whether a game's player lines still wait (not yet in the season rows). Engine session, 2026-10-30:
		// the broadcast's batter panel shows the season before the game and takes a revealed game out.
		public bool LinesPending(uint user, int season, int game)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT 1 FROM league_pending_lines WHERE user_code = $u AND season = $s AND game = $g";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$g", game);
			return cmd.ExecuteScalar() != null;
		}

		// Reveals the waiting lines of a season's games up to throughGame (all when null), oldest first,
		// so each per-game line carries the season rate after its own game.
		public void RevealLines(uint user, int season, int? throughGame = null)
		{
			List<(int game, int myClub, List<PlayerSeason> lines)> due = new List<(int, int, List<PlayerSeason>)>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT game, my_club, lines FROM league_pending_lines WHERE user_code = $u AND season = $s"
					+ (throughGame != null ? " AND game <= $g" : "") + " ORDER BY game";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", season);
				if (throughGame != null) cmd.Parameters.AddWithValue("$g", throughGame.Value);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					due.Add((r.GetInt32(0), r.GetInt32(1), JsonSerializer.Deserialize<List<PlayerSeason>>(r.GetString(2)) ?? new List<PlayerSeason>()));
				}
			}
			foreach ((int game, int myClub, List<PlayerSeason> lines) in due)
			{
				AddPlayerLines(user, lines);
				AddGameLines(user, game, lines.Where(l => l.Club == myClub));
				AddClubGameLines(user, game, lines.Where(l => l.Club != myClub));
				using SqliteConnection c = Open();
				using SqliteCommand del = c.CreateCommand();
				del.CommandText = "DELETE FROM league_pending_lines WHERE user_code = $u AND season = $s AND game = $g";
				del.Parameters.AddWithValue("$u", (long)user);
				del.Parameters.AddWithValue("$s", season);
				del.Parameters.AddWithValue("$g", game);
				del.ExecuteNonQuery();
			}
		}

		// 완봉 for the games before PlayerSeason.SHO (2026-10-30), counted once from the per-game lines (a complete game without
		// a run): every club's waiting lines, and the current season rows of the real clubs (a team's own from its per-game
		// lines, a shared league's other members from theirs, over the games revealed in this copy). AI clubs' revealed
		// shutouts have no per-game line and stay uncounted for that season. Returns the rows changed (0 once done).
		public int BackfillShutouts()
		{
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
				cmd.ExecuteNonQuery();
				cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'shutouts'";
				if ((long)cmd.ExecuteScalar() > 0) return 0;
			}
			static bool Shutout(PlayerSeason l) => l.Pitcher && l.CG > 0 && l.PR == 0;
			List<(uint user, int season, int me, string share)> leagues = new List<(uint, int, int, string)>();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT user_code, season, my_index, share FROM league";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) leagues.Add(((uint)r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
			}
			// Per league copy: its waiting lines by game, and the games of its own club's shutouts by card (revealed or waiting).
			Dictionary<uint, Dictionary<int, List<PlayerSeason>>> pending = new Dictionary<uint, Dictionary<int, List<PlayerSeason>>>();
			Dictionary<uint, Dictionary<int, HashSet<int>>> shutouts = new Dictionary<uint, Dictionary<int, HashSet<int>>>();
			foreach ((uint user, int season, int me, _) in leagues)
			{
				Dictionary<int, List<PlayerSeason>> waiting = new Dictionary<int, List<PlayerSeason>>();
				Dictionary<int, HashSet<int>> own = new Dictionary<int, HashSet<int>>();
				void Mark(int card, int game) => (own.TryGetValue(card, out HashSet<int> set) ? set : own[card] = new HashSet<int>()).Add(game);
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT game, lines FROM league_pending_lines WHERE user_code = $u AND season = $s";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) waiting[r.GetInt32(0)] = JsonSerializer.Deserialize<List<PlayerSeason>>(r.GetString(1)) ?? new List<PlayerSeason>();
				}
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT line FROM league_player_game WHERE user_code = $u AND season = $s";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read())
					{
						if (JsonSerializer.Deserialize<PlayerGame>(r.GetString(0)) is PlayerGame g && Shutout(g.Line)) Mark(g.Line.CardIdx, g.Game);
					}
				}
				foreach ((int game, List<PlayerSeason> lines) in waiting)
				{
					foreach (PlayerSeason l in lines.Where(l => l.Club == me && Shutout(l))) Mark(l.CardIdx, game);
				}
				pending[user] = waiting;
				shutouts[user] = own;
			}
			int changed = 0;
			using SqliteTransaction tx = c.BeginTransaction();
			foreach ((uint user, int season, int me, string share) in leagues)
			{
				// The real clubs of this copy and whose lines tell their shutouts.
				List<(int club, uint member)> real = new List<(int, uint)> { (me, user) };
				if (share != null) real.AddRange(leagues.Where(l => l.share == share && l.user != user).Select(l => (l.me, l.user)));
				foreach ((int club, uint member) in real)
				{
					List<(int card, PlayerSeason line)> rows = new List<(int, PlayerSeason)>();
					using (SqliteCommand get = c.CreateCommand())
					{
						get.Transaction = tx;
						get.CommandText = "SELECT card_idx, line FROM league_player WHERE user_code = $u AND season = $s AND club = $c";
						get.Parameters.AddWithValue("$u", (long)user);
						get.Parameters.AddWithValue("$s", season);
						get.Parameters.AddWithValue("$c", club);
						using SqliteDataReader r = get.ExecuteReader();
						while (r.Read())
						{
							if (JsonSerializer.Deserialize<PlayerSeason>(r.GetString(1)) is PlayerSeason p && p.Pitcher) rows.Add((r.GetInt32(0), p));
						}
					}
					foreach ((int card, PlayerSeason p) in rows)
					{
						int sho = shutouts[member].TryGetValue(card, out HashSet<int> games) ? games.Count(g => !pending[user].ContainsKey(g)) : 0;
						if (p.SHO == sho) continue;
						p.SHO = sho;
						using SqliteCommand put = c.CreateCommand();
						put.Transaction = tx;
						put.CommandText = "UPDATE league_player SET line = $l WHERE user_code = $u AND season = $s AND club = $c AND card_idx = $i";
						put.Parameters.AddWithValue("$l", JsonSerializer.Serialize(p));
						put.Parameters.AddWithValue("$u", (long)user);
						put.Parameters.AddWithValue("$s", season);
						put.Parameters.AddWithValue("$c", club);
						put.Parameters.AddWithValue("$i", card);
						put.ExecuteNonQuery();
						changed++;
					}
				}
				foreach ((int game, List<PlayerSeason> lines) in pending[user])
				{
					List<PlayerSeason> missing = lines.Where(l => Shutout(l) && l.SHO == 0).ToList();
					if (missing.Count == 0) continue;
					missing.ForEach(l => l.SHO = 1);
					using SqliteCommand put = c.CreateCommand();
					put.Transaction = tx;
					put.CommandText = "UPDATE league_pending_lines SET lines = $l WHERE user_code = $u AND season = $s AND game = $g";
					put.Parameters.AddWithValue("$l", JsonSerializer.Serialize(lines));
					put.Parameters.AddWithValue("$u", (long)user);
					put.Parameters.AddWithValue("$s", season);
					put.Parameters.AddWithValue("$g", game);
					put.ExecuteNonQuery();
					changed++;
				}
			}
			using (SqliteCommand done = c.CreateCommand())
			{
				done.Transaction = tx;
				done.CommandText = "INSERT INTO league_fix (name, done) VALUES ('shutouts', $t)";
				done.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", Inv));
				done.ExecuteNonQuery();
			}
			tx.Commit();
			return changed;
		}

		// One card's line of one game (null when there is none: not played, still pending, or an AI club before per-game
		// lines; engine session, 2026-10-31, for the broadcast's pitcher boxes).
		public PlayerSeason GameLine(uint user, int season, int game, int cardIdx)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT line FROM league_player_game WHERE user_code = $u AND season = $s AND game = $g AND card_idx = $i";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$g", game);
			cmd.Parameters.AddWithValue("$i", cardIdx);
			return cmd.ExecuteScalar() is string json && JsonSerializer.Deserialize<PlayerGame>(json) is PlayerGame pg ? pg.Line : null;
		}

		// A card's latest games of a season, newest first. A game's lines are only here once revealed
		// (RevealLines), so games still waiting on the unconfirmed-games screen are left out.
		public List<PlayerGame> RecentGames(uint user, int season, int cardIdx, int count)
		{
			List<PlayerGame> list = new List<PlayerGame>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"SELECT p.line FROM league_player_game p
				WHERE p.user_code = $u AND p.season = $s AND p.card_idx = $i
				ORDER BY p.game DESC LIMIT $n";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$i", cardIdx);
			cmd.Parameters.AddWithValue("$n", count);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				if (JsonSerializer.Deserialize<PlayerGame>(r.GetString(0)) is PlayerGame g) list.Add(g);
			}
			return list;
		}

		// How many of another club's games a card keeps for its 최근성적 (RecordHandlers shows five).
		public const int ClubGamesKept = 5;

		// Another club's lines of one revealed game; each card keeps its last ClubGamesKept games.
		public void AddClubGameLines(uint user, int game, IEnumerable<PlayerSeason> lines)
		{
			List<PlayerSeason> theirs = lines.ToList();
			if (theirs.Count == 0) return;
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (PlayerSeason p in theirs)
			{
				PutClubGame(c, tx, user, p.Season, p.Club, game, p);
			}
			tx.Commit();
		}

		private static void PutClubGame(SqliteConnection c, SqliteTransaction tx, uint user, int season, int club, int game, PlayerSeason line)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.Transaction = tx;
			cmd.CommandText = @"INSERT OR REPLACE INTO league_club_game (user_code, season, club, card_idx, game, line) VALUES ($u, $s, $c, $i, $g, $l);
				DELETE FROM league_club_game WHERE user_code = $u AND season = $s AND club = $c AND card_idx = $i AND game NOT IN
					(SELECT game FROM league_club_game WHERE user_code = $u AND season = $s AND club = $c AND card_idx = $i ORDER BY game DESC LIMIT $k)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$c", club);
			cmd.Parameters.AddWithValue("$i", line.CardIdx);
			cmd.Parameters.AddWithValue("$g", game);
			cmd.Parameters.AddWithValue("$l", JsonSerializer.Serialize(line));
			cmd.Parameters.AddWithValue("$k", ClubGamesKept);
			cmd.ExecuteNonQuery();
		}

		// Another club's card's latest revealed games of a season, newest first (SeasonRate is not kept for them).
		public List<PlayerGame> ClubRecentGames(uint user, int season, int club, int cardIdx, int count)
		{
			List<PlayerGame> list = new List<PlayerGame>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT game, line FROM league_club_game WHERE user_code = $u AND season = $s AND club = $c AND card_idx = $i ORDER BY game DESC LIMIT $n";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$c", club);
			cmd.Parameters.AddWithValue("$i", cardIdx);
			cmd.Parameters.AddWithValue("$n", count);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				if (JsonSerializer.Deserialize<PlayerSeason>(r.GetString(1)) is PlayerSeason p) list.Add(new PlayerGame { Game = r.GetInt32(0), Line = p });
			}
			return list;
		}

		// league_club_game starts with the slots revealed from 2026-11-01. Once, a shared league's other real clubs get their
		// current-season games from those members' own per-game lines (the games this copy has revealed); AI clubs fill from
		// the next revealed slot. Returns the rows written (0 once done).
		public int BackfillClubGames()
		{
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
				cmd.ExecuteNonQuery();
				cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'club_games'";
				if ((long)cmd.ExecuteScalar() > 0) return 0;
			}
			List<(uint user, int season, int me, string share)> leagues = new List<(uint, int, int, string)>();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT user_code, season, my_index, share FROM league";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) leagues.Add(((uint)r.GetInt64(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
			}
			int written = 0;
			using SqliteTransaction tx = c.BeginTransaction();
			foreach ((uint user, int season, int me, string share) in leagues.Where(l => l.share != null))
			{
				HashSet<int> waiting = new HashSet<int>();
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.Transaction = tx;
					cmd.CommandText = "SELECT game FROM league_pending_lines WHERE user_code = $u AND season = $s";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read()) waiting.Add(r.GetInt32(0));
				}
				foreach ((uint member, int memberSeason, int club, _) in leagues.Where(l => l.share == share && l.user != user))
				{
					List<PlayerGame> games = new List<PlayerGame>();
					using (SqliteCommand cmd = c.CreateCommand())
					{
						cmd.Transaction = tx;
						cmd.CommandText = "SELECT line FROM league_player_game WHERE user_code = $u AND season = $s";
						cmd.Parameters.AddWithValue("$u", (long)member);
						cmd.Parameters.AddWithValue("$s", memberSeason);
						using SqliteDataReader r = cmd.ExecuteReader();
						while (r.Read())
						{
							if (JsonSerializer.Deserialize<PlayerGame>(r.GetString(0)) is PlayerGame g && !waiting.Contains(g.Game)) games.Add(g);
						}
					}
					foreach (PlayerGame g in games.GroupBy(g => g.Line.CardIdx).SelectMany(cg => cg.OrderByDescending(g => g.Game).Take(ClubGamesKept)))
					{
						g.Line.Season = season;
						g.Line.Club = club;
						PutClubGame(c, tx, user, season, club, g.Game, g.Line);
						written++;
					}
				}
			}
			using (SqliteCommand done = c.CreateCommand())
			{
				done.Transaction = tx;
				done.CommandText = "INSERT INTO league_fix (name, done) VALUES ('club_games', $t)";
				done.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", Inv));
				done.ExecuteNonQuery();
			}
			tx.Commit();
			return written;
		}

		// For the card detail screen: this season's line of one of the team's own cards (null = no game yet).
		public PlayerSeason PlayerSeason(uint user, int cardIdx)
		{
			LeagueState s = Load(user);
			if (s == null) return null;
			return PlayerLines(user, s.Season, s.MyIndex).FirstOrDefault(p => p.CardIdx == cardIdx);
		}

		// Pitchers' fatigue carried between league games (TacticModel.CarryIn), by (club, card).
		public Dictionary<(int club, int card), PitcherLoad> Fatigue(uint user, int season)
		{
			Dictionary<(int, int), PitcherLoad> map = new Dictionary<(int, int), PitcherLoad>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT club, card_idx, units, days, last1, last2 FROM league_fatigue WHERE user_code = $u AND season = $s";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				map[(r.GetInt32(0), r.GetInt32(1))] = new PitcherLoad { Units = r.GetDouble(2), Days = r.GetInt32(3), Last1 = r.GetInt32(4), Last2 = r.GetInt32(5) };
			}
			return map;
		}

		// Replaces the user's carried fatigue (earlier seasons' rows go too).
		public void SaveFatigue(uint user, int season, Dictionary<(int club, int card), PitcherLoad> map)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			using (SqliteCommand del = c.CreateCommand())
			{
				del.Transaction = tx;
				del.CommandText = "DELETE FROM league_fatigue WHERE user_code = $u";
				del.Parameters.AddWithValue("$u", (long)user);
				del.ExecuteNonQuery();
			}
			foreach (((int club, int card), PitcherLoad p) in map)
			{
				if (p.Units <= 0 && p.Days == 0 && p.Last1 == 0 && p.Last2 == 0) continue;
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = @"INSERT INTO league_fatigue (user_code, season, club, card_idx, units, days, last1, last2)
					VALUES ($u, $s, $c, $i, $n, $d, $l1, $l2)";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", season);
				cmd.Parameters.AddWithValue("$c", club);
				cmd.Parameters.AddWithValue("$i", card);
				cmd.Parameters.AddWithValue("$n", p.Units);
				cmd.Parameters.AddWithValue("$d", p.Days);
				cmd.Parameters.AddWithValue("$l1", p.Last1);
				cmd.Parameters.AddWithValue("$l2", p.Last2);
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// exceptSeason/exceptGame: a game left out (the one still on air, LeagueHandlers.OnAir).
		// onlySeason (0 = any): that season's games alone (LeagueHandlers.HasUnconfirmed, 2026-11-10).
		public bool HasUnconfirmed(uint user, int exceptSeason = 0, int exceptGame = 0, int onlySeason = 0)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT 1 FROM league_game WHERE user_code = $u AND confirmed = 0 AND NOT (season = $s AND game = $g) AND ($only = 0 OR season = $only) LIMIT 1";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", exceptSeason);
			cmd.Parameters.AddWithValue("$g", exceptGame);
			cmd.Parameters.AddWithValue("$only", onlySeason);
			return cmd.ExecuteScalar() != null;
		}

		// Seasons before `season` that still hold unconfirmed games of the user (LeagueHandlers.SettleOldSeasons).
		public List<int> UnconfirmedSeasonsBefore(uint user, int season)
		{
			List<int> list = new List<int>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT DISTINCT season FROM league_game WHERE user_code = $u AND confirmed = 0 AND season < $s ORDER BY season";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add(r.GetInt32(0));
			return list;
		}

		// Confirm the user's games (all unconfirmed when games is null). Returns the MP and 홈런볼 of
		// the games not paid yet, for the caller to pay (LeagueService.ConfirmAndPay).
		public (int mp, int homeRuns) Confirm(uint user, int season, IEnumerable<int> games)
		{
			string where;
			List<int> list = games?.ToList();
			if (list == null)
			{
				where = "user_code = $u AND confirmed = 0";
			}
			else
			{
				if (list.Count == 0) return (0, 0);
				where = $"user_code = $u AND season = $s AND confirmed = 0 AND game IN ({string.Join(",", list)})";
			}
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			(int mp, int homeRuns) due = (0, 0);
			using (SqliteCommand sum = c.CreateCommand())
			{
				sum.Transaction = tx;
				sum.CommandText = $"SELECT COALESCE(SUM(mp), 0), COALESCE(SUM(home_runs), 0) FROM league_game WHERE {where} AND paid = 0";
				sum.Parameters.AddWithValue("$u", (long)user);
				if (list != null) sum.Parameters.AddWithValue("$s", season);
				using SqliteDataReader r = sum.ExecuteReader();
				if (r.Read()) due = ((int)r.GetInt64(0), (int)r.GetInt64(1));
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = $"UPDATE league_game SET confirmed = 1, paid = 1 WHERE {where}";
				cmd.Parameters.AddWithValue("$u", (long)user);
				if (list != null) cmd.Parameters.AddWithValue("$s", season);
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
			return due;
		}

		// What the user may see before confirming: the standings without the games from the user's
		// first unconfirmed one on (the unconfirmed-games screen shows them one by one, and the
		// client counts them onto this record). A copy; s itself is not changed.
		public LeagueState ConfirmedView(LeagueState s)
		{
			List<LeagueGame> pending = Games(s.UserCode, s.Season, club: s.MyIndex, unconfirmedOnly: true);
			if (pending.Count == 0) return s;
			int first = pending.Min(g => g.Game);
			LeagueState v = new LeagueState
			{
				UserCode = s.UserCode, Tier = s.Tier, Season = s.Season, Start = s.Start, NextGame = s.NextGame, MyIndex = s.MyIndex,
				FirstGame = s.FirstGame, PostSlot = s.PostSlot, LastGame = s.LastGame, NextTier = s.NextTier, Share = s.Share,
				Clubs = s.Clubs.Select(c => c.Copy()).ToList(),
				// 상대 전적 as this view counts the games: without the ones from the first unconfirmed on.
				HeadToHead = s.Start >= LeagueState.KboPctFrom ? HeadToHeadOf(s.UserCode, s.Season, s.NextGame + s.LastGame, first) : null
			};
			// Postseason games (145 on) are not in the standings.
			foreach (LeagueGame g in Games(s.UserCode, s.Season).Where(g => g.Game >= first && g.Game <= LeagueRules.Games))
			{
				LeagueClub h = v.Clubs[g.Home], a = v.Clubs[g.Away];
				h.RunsFor -= g.HomeScore;
				h.RunsAgainst -= g.AwayScore;
				a.RunsFor -= g.AwayScore;
				a.RunsAgainst -= g.HomeScore;
				if (g.HomeScore > g.AwayScore) { h.W--; a.L--; }
				else if (g.HomeScore < g.AwayScore) { h.L--; a.W--; }
				else { h.D--; a.D--; }
			}
			return v;
		}

		// The standings without the season's regular-season games numbered `first` and above (as ConfirmedView does from
		// the first unconfirmed game): the lobby's 순위 변동 compares with the standings at the end of the previous day
		// (LeagueHandlers.PreviousDayRank; systems session, user 2026-11-12).
		public LeagueState ViewBefore(LeagueState s, int first)
		{
			LeagueState v = new LeagueState
			{
				UserCode = s.UserCode, Tier = s.Tier, Season = s.Season, Start = s.Start, NextGame = s.NextGame, MyIndex = s.MyIndex,
				FirstGame = s.FirstGame, PostSlot = s.PostSlot, LastGame = s.LastGame, NextTier = s.NextTier, Share = s.Share,
				Clubs = s.Clubs.Select(c => c.Copy()).ToList(),
				HeadToHead = s.Start >= LeagueState.KboPctFrom ? HeadToHeadOf(s.UserCode, s.Season, s.NextGame + s.LastGame, first) : null
			};
			foreach (LeagueGame g in Games(s.UserCode, s.Season).Where(g => g.Game >= first && g.Game <= LeagueRules.Games))
			{
				LeagueClub h = v.Clubs[g.Home], a = v.Clubs[g.Away];
				h.RunsFor -= g.HomeScore;
				h.RunsAgainst -= g.AwayScore;
				a.RunsFor -= g.AwayScore;
				a.RunsAgainst -= g.HomeScore;
				if (g.HomeScore > g.AwayScore) { h.W--; a.L--; }
				else if (g.HomeScore < g.AwayScore) { h.L--; a.W--; }
				else { h.D--; a.D--; }
			}
			return v;
		}

		// A finished season: history row; old games are dropped except the user's unconfirmed ones.
		public void CloseSeason(LeagueState s, int rank, LeagueClub mine, int nextTier)
		{
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = "INSERT OR REPLACE INTO league_history (user_code, season, tier, rank, w, l, d, next_tier) VALUES ($u, $s, $t, $r, $w, $l, $d, $n)";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$t", s.Tier);
				cmd.Parameters.AddWithValue("$r", rank);
				cmd.Parameters.AddWithValue("$w", mine.W);
				cmd.Parameters.AddWithValue("$l", mine.L);
				cmd.Parameters.AddWithValue("$d", mine.D);
				cmd.Parameters.AddWithValue("$n", nextTier);
				cmd.ExecuteNonQuery();
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.Transaction = tx;
				cmd.CommandText = "DELETE FROM league_game WHERE user_code = $u AND season < $s AND confirmed = 1";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}

		// Drops everything a season left (games, player lines, pending lines, postseason lines, fatigue,
		// history) for LeagueService.Restart. Paid MP stays paid. Returns the rows deleted.
		public int DiscardSeason(uint user, int season)
		{
			int n = 0;
			using SqliteConnection c = Open();
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (string table in new[] { "league_game", "league_player", "league_player_game", "league_club_game", "league_pending_lines", "league_post_player", "league_fatigue", "league_history" })
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = $"DELETE FROM {table} WHERE user_code = $u AND season = $s";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", season);
				n += cmd.ExecuteNonQuery();
			}
			tx.Commit();
			return n;
		}

		// A closed season's history row: final rank and the next season's tier (null = not closed).
		public (int tier, int rank, int nextTier)? History(uint user, int season)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT tier, rank, next_tier FROM league_history WHERE user_code = $u AND season = $s";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			using SqliteDataReader r = cmd.ExecuteReader();
			return r.Read() ? (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : null;
		}

		public int AddHomeRunBalls(uint user, int count)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT INTO wallet (user_code, hball) VALUES ($u, MAX(0, $n)) ON CONFLICT(user_code) DO UPDATE SET hball = MAX(0, hball + $n); SELECT hball FROM wallet WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$n", count);
			return Convert.ToInt32(cmd.ExecuteScalar());
		}

		public int HomeRunBalls(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT hball FROM wallet WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			object v = cmd.ExecuteScalar();
			return v == null ? 0 : Convert.ToInt32(v);
		}
	}
}
