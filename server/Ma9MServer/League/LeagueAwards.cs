using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 리그 시상 (systems session; user, 2026-10-21: "리그 종료 후 주요선수 성적 수상자가 있는 팀에 MP 보상 … 리그 종료 때
	// 어떤 팀의 어떤 선수가 수상했는지 보여 주는 화면 추가"). When the regular season ends (Settle, from 48 h, before the
	// post-season), the leaders of 7 batting and 7 pitching categories and the league MVP (highest WAR, SeasonStats) win;
	// each title pays the user whose club holds the card. At the season's close (SettlePost, 58 h) the 한국시리즈 MVP, the
	// champion's best WAR in the series (LeagueStore.PostLines; none in 비기너), gets a ceremony of its own.
	// Rewards rise with the league (AwardMult). Qualifiers as in the KBO: 규정타석 3.1 × the club's games for the
	// batting rates, 규정이닝 1 × games for ERA and WHIP, 10 wins for 승률; ties share the title.
	// The ceremony reaches the client as PKS_SC_POPUP type 9: pages of (title, subtitle, lines), shown in the lobby's
	// LeagueChangePopup (Ma9MAwards on the client); a 1.0.5 client ignores the type, the MP still comes.
	public static partial class LeagueAwards   // partial: the season result screen's data is in LeagueAwards.Result.cs (engine session)
	{
		// Rewards in the 비기너 league (user, 2026-10-21: "이 정도는 비기너 보상으로 충분"), rising with the league
		// ("리그별로 점점 보상이 높아지는걸로"): × AwardMult by tier (비기너, 루키, 마이너, 메이저, 챔피언스; six tiers from the 11-11 season: … 레전드 2.3, 챔피언스 2.6). Since 2026-10-28 the
		// league-game multipliers (user: "모든 배율을 … 시상도 동일하게"; it was 1 / 1.5 / 2 / 3), now 1.0 / 1.25 / 1.5 / 2.0 / 2.5.
		public const int TitleMp = 300_000, MvpMp = 2_000_000, KsMvpMp = 1_000_000;

		public static double[] AwardMult => LeagueRules.RewardMult;

		public static int Reward(int baseMp, int tier) => (int)Math.Round(baseMp * AwardMult[Math.Clamp(tier, 0, AwardMult.Length - 1)] / 10_000.0) * 10_000;

		public const byte PopupType = 9;

		public sealed class Award
		{
			public string Category;
			public int Page;                 // 0 타자, 1 투수, 2 MVP
			public List<PlayerSeason> Winners = new List<PlayerSeason>();
			public string Value = string.Empty;
			public int Mp;
			public string Note;              // shown when nobody won (a league without a post-season)
			// 2위·3위 of a title (user, 2026-11-08: "2, 3위 타이틀 홀더도 MP 보상을 줬으면 함. 1위 보상의 70%, 30%"), ranked as ties
			// count (two co-winners leave no 2위, the next is 3위); not for the MVPs.
			public List<PlayerSeason> Second = new List<PlayerSeason>(), Third = new List<PlayerSeason>();
			public int Mp2, Mp3;

			// The MP this award pays a club's players (titles, 2위, 3위).
			public int MpFor(int club) => Mp * Winners.Count(w => w.Club == club) + Mp2 * Second.Count(w => w.Club == club) + Mp3 * Third.Count(w => w.Club == club);
		}

		public const double SecondShare = 0.7, ThirdShare = 0.3;

		private sealed class Category
		{
			public string Name;
			public bool Pitcher;
			public bool Lower;               // lower is better (ERA, WHIP)
			public bool Rate;                // needs the qualifier; a counting stat needs a value above 0
			public Func<PlayerSeason, double> Value;
			public Func<double, string> Format;
		}

		private static string Avg(double v) => v.ToString(".000");

		private static string Two(double v) => v.ToString("0.00");

		private static string Count(double v) => ((int)Math.Round(v)).ToString();

		private static readonly Category[] Categories =
		{
			new Category { Name = "타율", Rate = true, Value = p => p.AB > 0 ? (double)p.H / p.AB : 0, Format = Avg },
			new Category { Name = "홈런", Value = p => p.HR, Format = Count },
			new Category { Name = "타점", Value = p => p.RBI, Format = Count },
			// User, 2026-11-08: "순위표 타이틀 경쟁에 득점 부분도 추가".
			new Category { Name = "득점", Value = p => p.R, Format = Count },
			new Category { Name = "안타", Value = p => p.H, Format = Count },
			new Category { Name = "출루율", Rate = true, Value = p => p.AB + p.BB + p.HBP + p.SF > 0 ? (double)(p.H + p.BB + p.HBP) / (p.AB + p.BB + p.HBP + p.SF) : 0, Format = Avg },
			new Category { Name = "장타율", Rate = true, Value = p => p.AB > 0 ? (double)(p.H + p.D + 2 * p.T + 3 * p.HR) / p.AB : 0, Format = Avg },
			new Category { Name = "도루", Value = p => p.SB, Format = Count },
			new Category { Name = "승리", Pitcher = true, Value = p => p.W, Format = Count },
			new Category { Name = "평균자책", Pitcher = true, Rate = true, Lower = true, Value = p => p.Outs > 0 ? p.ER * 27.0 / p.Outs : 99, Format = Two },
			new Category { Name = "탈삼진", Pitcher = true, Value = p => p.PSO, Format = Count },
			new Category { Name = "홀드", Pitcher = true, Value = p => p.HLD, Format = Count },
			new Category { Name = "세이브", Pitcher = true, Value = p => p.SV, Format = Count },
			new Category { Name = "WHIP", Pitcher = true, Rate = true, Lower = true, Value = p => p.Outs > 0 ? (p.PH + p.PBB) * 3.0 / p.Outs : 99, Format = Two },
			new Category { Name = "승률", Pitcher = true, Rate = true, Value = p => p.W + p.L > 0 ? (double)p.W / (p.W + p.L) : 0, Format = Avg },
		};

		private static Database s_db;

		private static string s_cs;

		private static readonly HashSet<uint> s_pending = new HashSet<uint>();

		public static void Register(GameLoop loop, Database db, string dbPath)
		{
			Init(db, dbPath);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				// 2026-11-05 (user: SKY해민 got its 시상식 MP but never saw the ceremony): the 12:00 ceremonies went out after any
				// packet, mostly right after the unconfirmed-games skip or the team choice, before the lobby was up, and were
				// marked shown at once. Those six are shown once more; ceremonies now go out at the lobby (LobbyHandlers).
				cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
				cmd.ExecuteNonQuery();
				cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'awards_reshow_1105'";
				if ((long)cmd.ExecuteScalar() == 0)
				{
					// The same seasons' 한국시리즈 MVP ceremonies too: the 22:00 close comes minutes before this restart.
					const string seasons = "(user_code, season) IN (VALUES (7, 8), (9, 8), (10, 7), (11, 6), (14, 5), (16, 2))";
					cmd.CommandText = $"UPDATE league_award SET shown = 0 WHERE {seasons}";
					int again = cmd.ExecuteNonQuery();
					cmd.CommandText = $"UPDATE league_award_post SET shown = 0 WHERE {seasons}";
					again += cmd.ExecuteNonQuery();
					cmd.CommandText = "INSERT INTO league_fix (name, done) VALUES ('awards_reshow_1105', $t)";
					cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("s"));
					cmd.ExecuteNonQuery();
					cmd.Parameters.Clear();
					ServerLog.Info($"league awards: {again} ceremonies of 11-05 12:00 to show again");
				}
				cmd.CommandText = "SELECT user_code FROM league_award WHERE shown = 0 UNION SELECT user_code FROM league_award_post WHERE shown = 0";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) s_pending.Add((uint)r.GetInt64(0));
			}
		}

		public static void Init(Database db, string dbPath)
		{
			s_db = db;
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			// One ceremony per user and season: the pages to show and the MP paid with them.
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS league_award (
				user_code INTEGER NOT NULL,
				season INTEGER NOT NULL,
				pages TEXT NOT NULL,
				mp INTEGER NOT NULL,
				shown INTEGER NOT NULL DEFAULT 0,
				PRIMARY KEY (user_code, season))";
			cmd.ExecuteNonQuery();
			// The 한국시리즈 MVP's ceremony at the close (SettlePost); its row also marks the season's 업적 as done.
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS league_award_post (
				user_code INTEGER NOT NULL,
				season INTEGER NOT NULL,
				pages TEXT NOT NULL,
				mp INTEGER NOT NULL,
				shown INTEGER NOT NULL DEFAULT 0,
				PRIMARY KEY (user_code, season))";
			cmd.ExecuteNonQuery();
			EnsureResultColumn(c);   // data: the season result screen (LeagueAwards.Result.cs)
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// The season's titles from the user's view of the league (every club's regular-season lines).
		public static List<Award> Compute(LeagueState s)
		{
			List<PlayerSeason> lines = LeagueService.Store.PlayerLines(s.UserCode, s.Season);
			Dictionary<int, int> games = s.Clubs.ToDictionary(c => c.Index, c => Math.Max(1, c.W + c.L + c.D));
			List<Award> awards = new List<Award>();
			foreach (Category cat in Categories)
			{
				List<(PlayerSeason p, double v)> field = lines.Where(p => p.Pitcher == cat.Pitcher && Qualifies(cat, p, games.GetValueOrDefault(p.Club, 1)))
					.Select(p => (p, cat.Value(p))).Where(x => cat.Rate || x.Item2 > 0).ToList();
				Award a = new Award { Category = cat.Name, Page = cat.Pitcher ? 1 : 0, Mp = Reward(TitleMp, s.Tier) };
				a.Mp2 = (int)Math.Round(a.Mp * SecondShare / 10_000.0) * 10_000;
				a.Mp3 = (int)Math.Round(a.Mp * ThirdShare / 10_000.0) * 10_000;
				if (field.Count > 0)
				{
					double best = cat.Lower ? field.Min(x => x.v) : field.Max(x => x.v);
					a.Winners = field.Where(x => Math.Abs(x.v - best) < 1e-9).Select(x => x.p).ToList();
					a.Value = cat.Format(best);
					// Rank = 1 + how many are strictly better.
					int RankOf(double v) => 1 + field.Count(x => cat.Lower ? x.v < v - 1e-9 : x.v > v + 1e-9);
					a.Second = field.Where(x => RankOf(x.v) == 2).Select(x => x.p).ToList();
					a.Third = field.Where(x => RankOf(x.v) == 3).Select(x => x.p).ToList();
				}
				awards.Add(a);
			}
			// League MVP: the highest WAR (bWAR for pitchers) of the season.
			SeasonStats.LeagueContext lg = SeasonStats.Context(s.UserCode, s.Season, s.GamesPlayed);
			CardCatalog catalog = LeagueService.Data?.Catalog;
			Award mvp = new Award { Category = "리그 MVP", Page = 2, Mp = Reward(MvpMp, s.Tier) };
			if (lg != null && catalog != null)
			{
				List<(PlayerSeason p, double war)> wars = lines.Select(p => (p, SeasonStats.Value(p, catalog.Get(p.CardIdx)?.MainPosition ?? CardPos.DH, lg).war))
					.Where(x => !double.IsNaN(x.war)).ToList();
				if (wars.Count > 0)
				{
					double top = wars.Max(x => x.war);
					mvp.Winners = wars.Where(x => Math.Abs(x.war - top) < 1e-9).Select(x => x.p).ToList();
					mvp.Value = $"WAR {top:F2}";
				}
			}
			awards.Add(mvp);
			return awards;
		}

		// 한국시리즈 MVP: the champion's best WAR in the series (LeagueStore.PostLines; 비기너 and a migrated season have
		// no post-season, Champion -1, and no winner).
		public static Award ComputeKs(LeagueState s)
		{
			SeasonStats.LeagueContext lg = SeasonStats.Context(s.UserCode, s.Season, s.GamesPlayed);
			CardCatalog catalog = LeagueService.Data?.Catalog;
			Award ks = new Award { Category = "한국시리즈 MVP", Page = 2, Mp = Reward(KsMvpMp, s.Tier) };
			int champion = -1;
			try
			{
				champion = Postseason.Of(s).Champion;
			}
			catch (Exception e)
			{
				ServerLog.Warn($"league awards: no bracket for user {s.UserCode} season {s.Season}: {e.Message}");
			}
			if (champion < 0)
			{
				ks.Note = "포스트시즌이 없는 시즌입니다";
			}
			else if (lg != null && catalog != null)
			{
				List<(PlayerSeason p, double war)> series = LeagueService.Store.PostLines(s.UserCode, s.Season, PostSeries.KoreanSeries, champion)
					.Select(p => (p, SeasonStats.Value(p, catalog.Get(p.CardIdx)?.MainPosition ?? CardPos.DH, lg).war)).Where(x => !double.IsNaN(x.war)).ToList();
				if (series.Count > 0)
				{
					double top = series.Max(x => x.war);
					ks.Winners = series.Where(x => Math.Abs(x.war - top) < 1e-9).Select(x => x.p).ToList();
					ks.Value = $"KS WAR {top:F2}";
				}
			}
			return ks;
		}

		private static bool Qualifies(Category cat, PlayerSeason p, int clubGames)
		{
			if (!cat.Rate)
			{
				return true;
			}
			if (cat.Name == "승률")
			{
				return p.W >= 10;
			}
			return cat.Pitcher ? p.Outs >= 3 * clubGames : p.PA >= Math.Floor(3.1 * clubGames);
		}

		// The regular season's ceremony, once its 144 games are over (LeagueService.Tick from 48 h, before the
		// post-season; user, 2026-10-24: "리그가 종료되고나서 보여주고, 그 다음에 포스트시즌이 진행되는게 순서" — it
		// used to wait for the close at 58 h, and 비기너, which has no post-season, waited for nothing): pays the
		// user's titles and keeps the ceremony for the next time the user is in the lobby. Returns the MP paid, or -1
		// when this season was settled.
		public static int Settle(LeagueState s)
		{
			if (s == null || s_cs == null)
			{
				return -1;
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM league_award WHERE user_code = $u AND season = $s";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				if ((long)cmd.ExecuteScalar() > 0) return -1;
			}
			List<Award> awards = Compute(s);
			int mp = awards.Sum(a => a.MpFor(s.MyIndex));
			List<string> pages = Pages(s, awards, mp);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "INSERT INTO league_award (user_code, season, pages, mp, shown) VALUES ($u, $s, $p, $m, 0)";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(pages));
				cmd.Parameters.AddWithValue("$m", mp);
				cmd.ExecuteNonQuery();
			}
			SaveResult("league_award", s, SafeResult(() => ResultJson(s, awards, mp)));   // the season result screen
			if (mp > 0)
			{
				s_db.AddMp(s.UserCode, mp);
			}
			s_pending.Add(s.UserCode);
			ServerLog.Info($"league awards: user {s.UserCode} season {s.Season}, {awards.Count(a => a.Winners.Count > 0)} titles, mine {mp:N0} MP");
			return mp;
		}

		// At the season's close (LeagueService.Close, 58 h): the 한국시리즈 MVP's own ceremony when the league had a
		// post-season, and the season's 업적 (records, post-season, promotion, titles), once per user and season
		// (row in league_award_post, also when there is nothing to show). Returns the MP paid, or -1 when done.
		public static int SettlePost(LeagueState s)
		{
			if (s == null || s_cs == null)
			{
				return -1;
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT COUNT(*) FROM league_award_post WHERE user_code = $u AND season = $s";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				if ((long)cmd.ExecuteScalar() > 0) return -1;
			}
			Award ks = ComputeKs(s);
			int mine = ks.Winners.Count(w => w.Club == s.MyIndex);
			int mp = ks.Mp * mine;
			List<string> pages = new List<string>();
			if (ks.Winners.Count > 0)
			{
				// The MVP page of the regular ceremony's layout, with this one award on it.
				pages.Add($"{s.Season}시즌 {LeagueRules.TierNames[Math.Clamp(s.Tier, 0, LeagueRules.TierNames.Length - 1)]} 리그 한국시리즈 MVP");
				pages.Add("한국시리즈 MVP");
				pages.Add(Pages(s, new List<Award> { ks }, mp)[8]);
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "INSERT INTO league_award_post (user_code, season, pages, mp, shown) VALUES ($u, $s, $p, $m, $shown)";
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(pages));
				cmd.Parameters.AddWithValue("$m", mp);
				cmd.Parameters.AddWithValue("$shown", pages.Count > 0 ? 0 : 1);
				cmd.ExecuteNonQuery();
			}
			if (pages.Count > 0)
			{
				SaveResult("league_award_post", s, SafeResult(() => CloseJson(s, ks, mp)));   // the season result screen
			}
			if (mp > 0)
			{
				s_db.AddMp(s.UserCode, mp);
			}
			if (pages.Count > 0)
			{
				s_pending.Add(s.UserCode);
			}
			ServerLog.Info($"league awards: user {s.UserCode} season {s.Season} 한국시리즈 MVP {(ks.Winners.Count > 0 ? ks.Value : "없음")}, mine {mp:N0} MP");
			// 업적 시즌 기록·포스트시즌·승격·시상 (once per season): the regular titles and the 한국시리즈 MVP.
			try
			{
				List<Award> awards = Compute(s);
				awards.Add(ks);
				Achievements.OnSeasonClosed(s, awards);
			}
			catch (Exception e)
			{
				ServerLog.Warn($"업적 시즌 기록 user {s.UserCode} season {s.Season}: {e.Message}");
			}
			return mp;
		}

		// Pages of three strings each (title, subtitle, lines): 타자, 투수, MVP and the user's own reward.
		public static List<string> Pages(LeagueState s, List<Award> awards, int mp)
		{
			// Short lines (user, 2026-10-21: the 타자 부문 page ran out of the popup's black box): category and value
			// first, then one winner with the card's year and club; co-winners by name only (mine first), three or more
			// as "외 n명". The client also scales the page down to fit the box (LeagueChangePopup).
			CardCatalog catalog = LeagueService.Data?.Catalog;
			string Name(PlayerSeason p) => catalog?.Get(p.CardIdx)?.Name ?? $"#{p.CardIdx}";
			string Who(PlayerSeason p)
			{
				CardInfo c = catalog?.Get(p.CardIdx);
				string club = s.Clubs.FirstOrDefault(x => x.Index == p.Club)?.Name ?? string.Empty;
				return c == null ? $"#{p.CardIdx} ({club})" : $"{c.Name} ('{c.Year % 100:00} {club})";
			}
			string Line(Award a)
			{
				if (a.Winners.Count == 0)
				{
					return $"{a.Category}  {a.Note ?? "-"}";
				}
				List<PlayerSeason> winners = a.Winners.OrderByDescending(w => w.Club == s.MyIndex).ToList();
				int mine = winners.Count(w => w.Club == s.MyIndex);
				string reward = mine > 0 ? $" [FFD200]★+{Man(a.Mp * mine)}[-]" : string.Empty;
				string who = winners.Count == 1 ? Who(winners[0])
					: winners.Count == 2 ? $"{Name(winners[0])}·{Name(winners[1])} (공동)"
					: $"{Name(winners[0])} 외 {winners.Count - 1}명 (공동)";
				// Our club's 2위 and 3위 (2026-11-08), by name with their MP.
				string place = string.Concat(new[] { (a.Second, a.Mp2, 2), (a.Third, a.Mp3, 3) }
					.Select(x => (list: x.Item1.Where(w => w.Club == s.MyIndex).ToList(), mp: x.Item2, rank: x.Item3))
					.Where(x => x.list.Count > 0)
					.Select(x => $" [FFD200]{x.rank}위 {string.Join("·", x.list.Select(Name))} +{Man(x.mp * x.list.Count)}[-]"));
				return $"{a.Category} {a.Value}  {who}{reward}{place}";
			}
			List<string> pages = new List<string>();
			string[] subtitles = { "타자 부문", "투수 부문", "MVP" };
			for (int page = 0; page < 3; page++)
			{
				List<string> lines = awards.Where(a => a.Page == page).Select(Line).ToList();
				if (page == 2)
				{
					lines.Add(string.Empty);
					int titles = awards.Sum(a => a.Winners.Count(w => w.Club == s.MyIndex));
					int placed = awards.Sum(a => a.Second.Count(w => w.Club == s.MyIndex) + a.Third.Count(w => w.Club == s.MyIndex));
					lines.Add(titles + placed > 0
						? $"[FFD200]우리 구단 수상 {titles}개{(placed > 0 ? $" · 2·3위 {placed}명" : string.Empty)} · 보상 {mp:N0} MP[-]"
						: "우리 구단 수상 없음");
				}
				pages.Add($"{s.Season}시즌 {LeagueRules.TierNames[Math.Clamp(s.Tier, 0, LeagueRules.TierNames.Length - 1)]} 리그 시상식 ({page + 1}/3)");
				pages.Add(subtitles[page]);
				pages.Add(string.Join("\n", lines));
			}
			return pages;
		}

		private static string Man(int mp) => mp % 10_000 == 0 ? $"{mp / 10_000}만" : $"{mp:N0}";

		// Shows the ceremonies the user has not seen, once no league result waits for confirmation. Called when the lobby
		// is entered (LobbyHandlers, as the 시즌 완주 보너스); until 11-05 it ran after every packet and could go out while the
		// client was between scenes.
		public static void Deliver(GameSession session)
		{
			if (session?.Team == null || !s_pending.Contains(session.Team.UserCode))
			{
				return;
			}
			uint user = session.Team.UserCode;
			if (LeagueHandlers.HasUnconfirmed(user))
			{
				return;
			}
			s_pending.Remove(user);
			// The regular season's ceremonies and the 한국시리즈 MVP's, by season, the regular one first.
			List<(int season, List<string> pages)> due = new List<(int, List<string>)>();
			using (SqliteConnection c = Open())
			{
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT season, pages, 0 AS part, data FROM league_award WHERE user_code = $u AND shown = 0"
						+ " UNION ALL SELECT season, pages, 1 AS part, data FROM league_award_post WHERE user_code = $u AND shown = 0 ORDER BY season, part";
					cmd.Parameters.AddWithValue("$u", (long)user);
					using SqliteDataReader r = cmd.ExecuteReader();
					while (r.Read())
					{
						List<string> pages = JsonSerializer.Deserialize<List<string>>(r.GetString(1)) ?? new List<string>();
						// The season result screen's data, one string after the pages of three (LeagueAwards.Result.cs): a
						// client from 1.0.42 opens the screen, an older one never reads it.
						if (pages.Count % 3 == 0 && !r.IsDBNull(3) && r.GetString(3).Length > 0)
						{
							pages.Add(r.GetString(3));
						}
						due.Add((r.GetInt32(0), pages));
					}
				}
				foreach (string table in new[] { "league_award", "league_award_post" })
				{
					using SqliteCommand mark = c.CreateCommand();
					mark.CommandText = $"UPDATE {table} SET shown = 1 WHERE user_code = $u";
					mark.Parameters.AddWithValue("$u", (long)user);
					mark.ExecuteNonQuery();
				}
			}
			foreach ((int season, List<string> pages) in due)
			{
				session.Send(Popup(pages));
				ServerLog.Info($"[{session.Id}] league awards shown: season {season}");
			}
			TopBar.Send(session);
		}

		// Dev (--posttest): whether a season's regular ceremony and its close (한국시리즈 MVP, 업적) are settled, and
		// whether the close has a ceremony to show.
		public static (bool regular, bool post, bool postPages) Settled(uint user, int season)
		{
			using SqliteConnection c = Open();
			bool Has(string sql)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = sql;
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", season);
				return (long)cmd.ExecuteScalar() > 0;
			}
			return (Has("SELECT COUNT(*) FROM league_award WHERE user_code = $u AND season = $s"),
				Has("SELECT COUNT(*) FROM league_award_post WHERE user_code = $u AND season = $s"),
				Has("SELECT COUNT(*) FROM league_award_post WHERE user_code = $u AND season = $s AND pages <> '[]'"));
		}

		public static Ma9MPacket.PKS_SC_POPUP Popup(List<string> pages)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = PopupType;
			popup.Messages.array = pages.Select(p => new PLongString { str = p }).ToArray();
			return popup;
		}

		// Data copy (--leaguetest): the current season's titles as if it ended now, settled once and only once.
		public static int SelfTest(Database db, string dbPath, uint user, out string line)
		{
			Init(db, dbPath);
			LeagueState s = LeagueService.Store.Load(user);
			if (s == null)
			{
				line = "league awards: no league";
				return 0;
			}
			List<Award> awards = Compute(s);
			int mine = awards.Sum(a => a.MpFor(s.MyIndex));
			// Since the ceremony comes at 48 h, a copy taken after the regular season's end has it settled already:
			// then nothing more is paid.
			bool already = Settled(user, s.Season).regular;
			int mp0 = db.AddMp(user, 0);
			int paid = Settle(s);
			int again = Settle(s);
			int mp1 = db.AddMp(user, 0);
			List<string> pages = Pages(s, awards, mine);
			Ma9MPacket.PKS_SC_POPUP popup = Popup(pages);
			popup.ToBytes(new byte[popup.GetSize() + 14], 14);
			bool scale = Reward(TitleMp, 0) == 300_000 && Reward(TitleMp, 3) == 600_000 && Reward(MvpMp, 1) == 2_500_000 && Reward(KsMvpMp, 2) == 1_500_000
				&& Reward(MvpMp, LeagueRules.ChampionsTier) == (LeagueRules.NewStructure ? 5_200_000 : 5_000_000)
				&& awards.All(a => a.Mp == Reward(a.Category == "리그 MVP" ? MvpMp : a.Category == "한국시리즈 MVP" ? KsMvpMp : TitleMp, s.Tier));
			bool payOk = already ? paid == -1 && mp1 == mp0 : paid == mine && mp1 - mp0 == mine && s_pending.Contains(user);
			// 2위·3위 (2026-11-08): 70% / 30% of the title's MP, nobody in two places, ranks below the winners.
			bool places = awards.Where(a => a.Page < 2).All(a => a.Mp2 == (int)Math.Round(a.Mp * SecondShare / 10_000.0) * 10_000 && a.Mp3 == (int)Math.Round(a.Mp * ThirdShare / 10_000.0) * 10_000
					&& !a.Second.Intersect(a.Winners).Any() && !a.Third.Intersect(a.Winners).Any() && !a.Third.Intersect(a.Second).Any())
				&& awards.Count(a => a.Page < 2 && a.Second.Count + a.Third.Count > 0) >= 10 && awards.Any(a => a.Category == "득점");
			bool ok = scale && payOk && places && again == -1 && pages.Count == 9 && awards.Count(a => a.Winners.Count > 0) >= 12;
			line = $"league awards {(ok ? "ok" : "FAIL")}: {LeagueRules.TierNames[Math.Clamp(s.Tier, 0, LeagueRules.Tiers - 1)]} (부문 {Reward(TitleMp, s.Tier):N0}, MVP {Reward(MvpMp, s.Tier):N0}), {awards.Count(a => a.Winners.Count > 0)}/{awards.Count} titles, 2·3위 MP {awards.Where(a => a.Page < 2).Select(a => $"{a.Category} {a.Mp2 / 10_000}/{a.Mp3 / 10_000}만").FirstOrDefault()}, places {(places ? "ok" : "FAIL")}, mine {mine:N0} MP (paid {paid:N0}, again {again}{(already ? ", settled at 48 h already" : string.Empty)})\n"
				+ string.Join("\n", pages.Where((p, i) => i % 3 == 2));
			return ok ? 0 : 1;
		}
	}
}
