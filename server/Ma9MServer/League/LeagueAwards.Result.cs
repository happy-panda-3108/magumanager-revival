using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// 시즌 결과 화면의 자료 (engine session, 2026-11-10; user: "시즌 끝나고 단순 팝업으로 안내하기 보다는 새로운 화면이 나오면서
	// 순위, 타이틀홀더 시상식을 진행했으면 함", and of the two times to show it "(가) 추천안": once when the regular season
	// ends — final standings, the titles, the league MVP, the club's rewards — and once at the close, after the
	// post-season — the champion, the 한국시리즈 MVP, promotion and the next season).
	//
	// The ceremony still goes out as PKS_SC_POPUP type 9 with its pages of three strings; this JSON rides as one more
	// string after them. A client that knows it (Ma9MSeasonResult, from APK 1.0.42) opens the screen instead of the
	// pages; an older one reads the strings three at a time and never reaches it. Short names keep it small: the string
	// holds at most 32,767 bytes of UTF-16.
	public static partial class LeagueAwards
	{
		public sealed class ResultClub
		{
			public int r { get; set; }        // place
			public string n { get; set; }     // name
			public int e { get; set; }        // emblem
			public int w { get; set; }
			public int l { get; set; }
			public int d { get; set; }
			public bool me { get; set; }
			public int mv { get; set; }       // next season: 1 up, 0 stays, -1 down; 2 = on to the post-season
		}

		public sealed class ResultWho
		{
			public int c { get; set; }        // card
			public string n { get; set; }     // player
			public int y { get; set; }        // the card's year, two digits
			public string t { get; set; }     // club
			public int e { get; set; }        // the club's emblem
			public bool me { get; set; }
		}

		public sealed class ResultTitle
		{
			public string n { get; set; }     // 타율, …, 리그 MVP, 한국시리즈 MVP
			public bool p { get; set; }       // a pitching title
			public string v { get; set; } = string.Empty;
			public int mp { get; set; }
			public int mp2 { get; set; }
			public int mp3 { get; set; }
			public List<ResultWho> a { get; set; } = new List<ResultWho>();   // winners
			public List<ResultWho> b { get; set; } = new List<ResultWho>();   // 2위
			public List<ResultWho> c { get; set; } = new List<ResultWho>();   // 3위
			public int xa { get; set; }       // winners not listed
		}

		public sealed class SeasonResult
		{
			public int kind { get; set; }             // 0 the regular season's end, 1 the close
			public int season { get; set; }
			public string tier { get; set; }
			public int grade { get; set; }            // eLeagueGrade of the tier (the badge)
			public List<ResultClub> clubs { get; set; } = new List<ResultClub>();
			public int rank { get; set; }             // my place in clubs
			public string verdict { get; set; }       // 우승, 승격, 잔류, 강등, 포스트시즌 진출
			public string verdict2 { get; set; }      // the line under it
			public List<ResultTitle> titles { get; set; } = new List<ResultTitle>();
			public ResultTitle mvp { get; set; }
			public int mine { get; set; }             // my club's titles
			public int placed { get; set; }           // my club's 2위·3위
			public int mp { get; set; }
			public string next { get; set; }
			public ResultClub champ { get; set; }     // the close: the champion
			public string series { get; set; }        // and the 한국시리즈's score
		}

		private const int ListedWinners = 3, ListedPlaced = 2;

		private static readonly JsonSerializerOptions s_resultJson = new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // Korean as it is, not six characters each
			DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
		};

		private static string TierName(int tier) => LeagueRules.TierNames[Math.Clamp(tier, 0, LeagueRules.TierNames.Length - 1)];

		private static ResultClub ClubOf(LeagueState s, LeagueClub c, int place, int move) => new ResultClub
		{
			r = place, n = c.Name, e = c.Emblem, w = c.W, l = c.L, d = c.D, me = c.Index == s.MyIndex, mv = move
		};

		private static ResultTitle TitleOf(LeagueState s, Award a)
		{
			CardCatalog catalog = LeagueService.Data?.Catalog;
			ResultWho Who(PlayerSeason p)
			{
				CardInfo card = catalog?.Get(p.CardIdx);
				LeagueClub club = s.Clubs.FirstOrDefault(x => x.Index == p.Club);
				return new ResultWho { c = p.CardIdx, n = card?.Name ?? $"#{p.CardIdx}", y = (card?.Year ?? 0) % 100, t = club?.Name ?? string.Empty, e = club?.Emblem ?? 0, me = p.Club == s.MyIndex };
			}
			List<ResultWho> Listed(List<PlayerSeason> list, int most) => list.OrderByDescending(w => w.Club == s.MyIndex).Take(most).Select(Who).ToList();
			return new ResultTitle
			{
				n = a.Category, p = a.Page == 1, v = a.Winners.Count > 0 ? a.Value : (a.Note ?? "-"), mp = a.Mp, mp2 = a.Mp2, mp3 = a.Mp3,
				a = Listed(a.Winners, ListedWinners), b = Listed(a.Second, ListedPlaced), c = Listed(a.Third, ListedPlaced),
				xa = Math.Max(0, a.Winners.Count - ListedWinners)
			};
		}

		private static string Sign(int tier, int next) => next > tier ? "승격" : next < tier ? "강등" : "잔류";

		private static string NextSeason(LeagueState s, int nextTier) => $"다음 시즌: {TierName(nextTier)} 리그 · {s.Start + LeagueCalendar.Cycle:M월 d일 HH:mm} 시작";

		// The regular season's end: the standings with what they mean for each club, the titles and the MVP.
		public static string ResultJson(LeagueState s, List<Award> awards, int mp)
		{
			bool post = LeagueRules.HasPostseason(s.Tier);
			List<LeagueClub> ranked = s.Ranked();
			SeasonResult r = new SeasonResult { kind = 0, season = s.Season, tier = TierName(s.Tier), grade = LeagueRules.TierGrade[Math.Clamp(s.Tier, 0, LeagueRules.TierGrade.Length - 1)], mp = mp };
			for (int i = 0; i < ranked.Count; i++)
			{
				int rank = i + 1;
				bool on = post && rank <= LeagueRules.PostseasonClubs;
				r.clubs.Add(ClubOf(s, ranked[i], rank, on ? 2 : Math.Sign(LeagueRules.NextTier(s.Tier, rank, 0) - s.Tier)));
			}
			int mine = Math.Max(1, ranked.FindIndex(c => c.Index == s.MyIndex) + 1);
			r.rank = mine;
			if (post && mine <= LeagueRules.PostseasonClubs)
			{
				// The first series of each seed and its first slot (Postseason: 와일드카드 1-2, 준PO 3-7, PO 8-12, KS 13-19).
				(string name, int slot) entry = mine == 1 ? ("한국시리즈 직행", 13) : mine == 2 ? ("플레이오프부터", 8) : mine == 3 ? ("준플레이오프부터", 3) : ("와일드카드 결정전부터", 1);
				r.verdict = "포스트시즌 진출";
				r.verdict2 = $"정규시즌 {mine}위 · {entry.name}";
				r.next = $"포스트시즌: {entry.name} · {LeagueCalendar.PostSlotTime(s.Start, entry.slot):M월 d일 HH:mm}";
			}
			else
			{
				int nextTier = LeagueRules.NextTier(s.Tier, mine, 0);
				r.verdict = !post && mine == 1 ? "우승" : Sign(s.Tier, nextTier);
				r.verdict2 = !post && mine == 1 ? $"정규시즌 1위 · {TierName(nextTier)} 리그 {Sign(s.Tier, nextTier)}"
					: nextTier == s.Tier ? $"정규시즌 {mine}위 · {TierName(nextTier)} 리그 잔류" : $"정규시즌 {mine}위 · {TierName(nextTier)} 리그로 {Sign(s.Tier, nextTier)}";
				r.next = NextSeason(s, nextTier);
			}
			foreach (Award a in awards.Where(a => a.Page != 2))
			{
				r.titles.Add(TitleOf(s, a));
			}
			Award mvp = awards.FirstOrDefault(a => a.Page == 2);
			r.mvp = mvp == null ? null : TitleOf(s, mvp);
			r.mine = awards.Sum(a => a.Winners.Count(w => w.Club == s.MyIndex));
			r.placed = awards.Sum(a => a.Second.Count(w => w.Club == s.MyIndex) + a.Third.Count(w => w.Club == s.MyIndex));
			return Fit(r);
		}

		// The close: the final places (the post-season's five, then the regular-season ranks), the champion and the
		// 한국시리즈 MVP, and where each club plays next. Null without a post-season (비기너: the regular season said it all).
		public static string CloseJson(LeagueState s, Award ks, int mp)
		{
			if (!LeagueRules.HasPostseason(s.Tier))
			{
				return null;
			}
			Bracket bracket;
			try
			{
				bracket = Postseason.Of(s);
			}
			catch (Exception)
			{
				return null;
			}
			if (bracket == null || bracket.Champion < 0)
			{
				return null;
			}
			List<LeagueClub> ranked = s.Ranked();
			int RankOf(LeagueClub c) => ranked.IndexOf(c) + 1;
			// 1 champion … 5 와일드카드 loser, then the rest as they finished the regular season.
			List<LeagueClub> order = ranked.OrderBy(c => bracket.PlaceOf(c.Index) is int p && p > 0 ? p : 100 + RankOf(c)).ToList();
			SeasonResult r = new SeasonResult { kind = 1, season = s.Season, tier = TierName(s.Tier), grade = LeagueRules.TierGrade[Math.Clamp(s.Tier, 0, LeagueRules.TierGrade.Length - 1)], mp = mp };
			for (int i = 0; i < order.Count; i++)
			{
				LeagueClub c = order[i];
				r.clubs.Add(ClubOf(s, c, i + 1, Math.Sign(LeagueRules.NextTier(s.Tier, RankOf(c), bracket.PlaceOf(c.Index)) - s.Tier)));
			}
			LeagueClub me = s.Clubs.First(c => c.Index == s.MyIndex);
			int place = bracket.PlaceOf(s.MyIndex), final = order.IndexOf(me) + 1;
			int nextTier = LeagueRules.NextTier(s.Tier, RankOf(me), place);
			r.rank = final;
			r.verdict = place == 1 ? "우승" : Sign(s.Tier, nextTier);
			string how = place == 1 ? "한국시리즈 우승" : place == 2 ? "한국시리즈 준우승" : place == 3 ? "플레이오프 탈락" : place == 4 ? "준플레이오프 탈락"
				: place == 5 ? "와일드카드 결정전 탈락" : $"정규시즌 {RankOf(me)}위";
			r.verdict2 = nextTier == s.Tier ? $"{how} · {TierName(nextTier)} 리그 잔류" : $"{how} · {TierName(nextTier)} 리그로 {Sign(s.Tier, nextTier)}";
			r.next = NextSeason(s, nextTier);
			SeriesState final7 = bracket[PostSeries.KoreanSeries];
			LeagueClub champion = s.Clubs.First(c => c.Index == bracket.Champion);
			r.champ = ClubOf(s, champion, 1, 0);
			int won = final7.Winner == final7.High ? final7.HighWins : final7.LowWins, lost = final7.Winner == final7.High ? final7.LowWins : final7.HighWins;
			int tied = Math.Max(0, final7.Games.Count - won - lost);
			r.series = $"한국시리즈 {won}승 {lost}패" + (tied > 0 ? $" {tied}무" : string.Empty);
			r.mvp = ks == null ? null : TitleOf(s, ks);
			r.mine = ks?.Winners.Count(w => w.Club == s.MyIndex) ?? 0;
			return Fit(r);
		}

		// Inside one PLongString: without the 2위·3위 lists, then without the titles, should it ever be too long.
		private static string Fit(SeasonResult r)
		{
			const int most = 15000;
			string json = JsonSerializer.Serialize(r, s_resultJson);
			if (json.Length > most)
			{
				foreach (ResultTitle t in r.titles)
				{
					t.b.Clear();
					t.c.Clear();
				}
				json = JsonSerializer.Serialize(r, s_resultJson);
			}
			if (json.Length > most)
			{
				r.titles.Clear();
				json = JsonSerializer.Serialize(r, s_resultJson);
			}
			return json;
		}

		// The screen's data never stands in the way of the ceremony itself: no data = the pages alone, as before.
		private static string SafeResult(Func<string> make)
		{
			try
			{
				return make();
			}
			catch (Exception e)
			{
				ServerLog.Warn($"league awards: result screen data not made: {e.Message}");
				return null;
			}
		}

		// The column next to a ceremony's pages (added on the first run with this build).
		private static void EnsureResultColumn(SqliteConnection c)
		{
			foreach (string table in new[] { "league_award", "league_award_post" })
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'data'";
				if ((long)cmd.ExecuteScalar() == 0)
				{
					cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN data TEXT";
					cmd.ExecuteNonQuery();
				}
			}
		}

		private static void SaveResult(string table, LeagueState s, string json)
		{
			if (string.IsNullOrEmpty(json))
			{
				return;
			}
			try
			{
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = $"UPDATE {table} SET data = $d WHERE user_code = $u AND season = $s";
				cmd.Parameters.AddWithValue("$d", json);
				cmd.Parameters.AddWithValue("$u", (long)s.UserCode);
				cmd.Parameters.AddWithValue("$s", s.Season);
				cmd.ExecuteNonQuery();
				ServerLog.Info($"league awards: user {s.UserCode} season {s.Season} result screen data, {json.Length} characters ({table})");
			}
			catch (Exception e)
			{
				ServerLog.Warn($"league awards: result screen data for user {s.UserCode} season {s.Season}: {e.Message}");
			}
		}
	}
}
