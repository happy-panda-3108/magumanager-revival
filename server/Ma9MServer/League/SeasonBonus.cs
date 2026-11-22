using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 시즌 완주 보너스 (user, 2026-10-19: rank-based): 1,000,000 MP × the finished tier's multiplier
	// (LeagueRules.RewardMult, 비기너 1.0 … 챔피언스 2.5) × the final rank's factor (1위 ×3, 2위 ×2.5, 3위 ×2,
	// 4~6위 ×1.5, 7~10위 ×1). Recorded when the season ends (LeagueService.EndSeason) and paid, with a
	// popup (PKS_SC_POPUP), at the next lobby visit once none of that season's games await confirmation,
	// so neither the MP nor the popup gives a result away early. The same popup carries a pending 팀 특성 조정 notice
	// (TeamPropertyHandlers.TakeTrimNotice; the client shows one type-1 popup at a time).
	public static class SeasonBonus
	{
		public const int Base = 1_000_000;

		private static string s_cs;

		private static Database s_db;

		public static void Init(Database db, string dbPath)
		{
			s_db = db;
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS season_bonus (
				user_code INTEGER NOT NULL,
				season INTEGER NOT NULL,
				tier INTEGER NOT NULL,
				rank INTEGER NOT NULL,
				mp INTEGER NOT NULL,
				paid INTEGER NOT NULL DEFAULT 0,
				recorded TEXT NOT NULL,
				PRIMARY KEY (user_code, season))";
			cmd.ExecuteNonQuery();
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		public static double RankFactor(int rank) => rank == 1 ? 3.0 : rank == 2 ? 2.5 : rank == 3 ? 2.0 : rank <= 6 ? 1.5 : 1.0;

		public static int Amount(int tier, int rank) =>
			(int)Math.Round(Base * LeagueRules.RewardMult[Math.Clamp(tier, 0, LeagueRules.RewardMult.Length - 1)] * RankFactor(rank));

		public static void Record(uint user, int season, int tier, int rank)
		{
			if (s_cs == null) return;
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR IGNORE INTO season_bonus (user_code, season, tier, rank, mp, recorded) VALUES ($u, $s, $t, $r, $m, $now)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", season);
			cmd.Parameters.AddWithValue("$t", tier);
			cmd.Parameters.AddWithValue("$r", rank);
			cmd.Parameters.AddWithValue("$m", Amount(tier, rank));
			cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
			cmd.ExecuteNonQuery();
		}

		// Pays every recorded bonus whose season has no unconfirmed game left; the lines for the popup (Lines).
		public static List<string> Pay(uint user) => Lines(PayDue(user));

		private static string Tier(int tier) => LeagueRules.TierNames[Math.Clamp(tier, 0, LeagueRules.TierNames.Length - 1)];

		// The popup's lines (Panel_Popup1 does not wrap; PopupText). One season as before. Several at once (engine session's
		// AI test 11-09: five seasons' lines ran out of the box) as the total, then each season's place with runs of the
		// same tier and rank put together ("5~9시즌 비기너 10위"), at most three lines of places, the latest kept.
		internal static List<string> Lines(List<(int season, int tier, int rank, int mp)> paid)
		{
			List<string> lines = new List<string>();
			if (paid.Count == 0) return lines;
			if (paid.Count == 1)
			{
				(int season, int tier, int rank, int mp) p = paid[0];
				// 2026-11-20 (AI testers 두산 55 · NC · kt: "3위로 완주" here and "4위 · 준우승" in the close right after read as two
				// different places): the bonus goes by the regular season's place, and says so.
				lines.Add($"{p.season}시즌 {Tier(p.tier)} 정규시즌 {p.rank}위로 완주했습니다!");
				lines.Add($"보너스 {p.mp:N0} MP를 받았습니다(정규시즌 순위 기준).");
				return lines;
			}
			int first = paid[0].season, last = paid[paid.Count - 1].season;
			string seasons = last - first + 1 == paid.Count ? $"{first}~{last}시즌" : $"{paid.Count}개 시즌";
			lines.Add($"{seasons} 완주 보너스 {paid.Sum(p => (long)p.mp):N0} MP를 받았습니다.");
			List<string> runs = new List<string>();
			for (int i = 0; i < paid.Count;)
			{
				int j = i;
				while (j + 1 < paid.Count && paid[j + 1].season == paid[j].season + 1 && paid[j + 1].tier == paid[i].tier && paid[j + 1].rank == paid[i].rank) j++;
				runs.Add($"{(i == j ? $"{paid[i].season}시즌" : $"{paid[i].season}~{paid[j].season}시즌")} {Tier(paid[i].tier)} 정규 {paid[i].rank}위");
				i = j + 1;
			}
			List<string> places = PopupText.Wrap(runs, " · ");
			if (places.Count > 3)
			{
				places = places.Skip(places.Count - 3).ToList();
				places[0] = "… " + places[0];
			}
			lines.AddRange(places);
			return lines;
		}

		private static List<(int season, int tier, int rank, int mp)> PayDue(uint user)
		{
			List<(int season, int tier, int rank, int mp)> paid = new List<(int, int, int, int)>();
			if (s_cs == null) return paid;
			List<(int season, int tier, int rank, int mp)> due = new List<(int, int, int, int)>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"SELECT b.season, b.tier, b.rank, b.mp FROM season_bonus b WHERE b.user_code = $u AND b.paid = 0
					AND NOT EXISTS (SELECT 1 FROM league_game g WHERE g.user_code = b.user_code AND g.season = b.season AND g.confirmed = 0)
					ORDER BY b.season";
				cmd.Parameters.AddWithValue("$u", (long)user);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) due.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3)));
			}
			foreach ((int season, int tier, int rank, int mp) in due)
			{
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "UPDATE season_bonus SET paid = 1 WHERE user_code = $u AND season = $s AND paid = 0";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", season);
					if (cmd.ExecuteNonQuery() == 0) continue;
				}
				s_db.AddMp(user, mp, MpLedger.Season);
				paid.Add((season, tier, rank, mp));
			}
			return paid;
		}

		// At the lobby: pay what is due and say so in a popup (then the top bar shows the new MP), and the 팀 특성 조정 notice
		// of a relegation when there is one. Two popups since 11-09 (they stack, as the 새벽 시범경기 ones do; one box held both
		// and ran over): the notice first, so the bonus is on top.
		public static void Deliver(GameSession session)
		{
			if (session.Team == null) return;
			List<(int season, int tier, int rank, int mp)> paid = PayDue(session.Team.UserCode);
			List<string> lines = Lines(paid);
			string trim = TeamPropertyHandlers.TakeTrimNotice(session.Team.UserCode);
			if (trim != null)
			{
				Popup(session, "팀 특성 조정", trim);
				ServerLog.Info($"[{session.Id}] trait trim notice: {trim.Replace("\n", " / ")}");
			}
			if (lines.Count == 0) return;
			lines.AddRange(NowLines(session.Team.UserCode, paid.Max(p => p.season), 5 - lines.Count));
			Popup(session, "시즌 완주 보너스", string.Join("\n", lines));
			TopBar.Send(session);
			ServerLog.Info($"[{session.Id}] season bonus: {string.Join(" / ", lines)}");
		}

		// The season now under way (AI testers 11-11, NC 5 and 두산 25: nothing said a new season had begun, in which league or
		// what it takes to go up): its tier and the rule LeagueRules.NextTier plays, in the lines the box has left (five at most).
		internal static List<string> NowLines(uint user, int paidSeason, int room)
		{
			List<string> lines = new List<string>();
			LeagueState s = room > 0 ? LeagueService.Store?.Load(user) : null;
			if (s == null || s.Season <= paidSeason) return lines;
			lines.Add($"{s.Season}시즌 {Tier(s.Tier)} 리그가 시작됐습니다({LeagueRules.Games}경기).");
			if (room > 1) lines.Add(Rule(s.Tier));
			return lines;
		}

		internal static string Rule(int tier)
		{
			if (tier >= LeagueRules.ChampionsTier)
			{
				return $"최종 1~{LeagueRules.TopTierKept}위 잔류 · {LeagueRules.TopTierKept + 1}~{LeagueRules.Clubs}위 {Tier(LeagueRules.GateTier)}로 강등";
			}
			if (tier == LeagueRules.GateTier)
			{
				return $"최종 1~{LeagueRules.MajorPromoted}위 {Tier(LeagueRules.ChampionsTier)} 승격 · {LeagueRules.MajorPromoted + 1}~{LeagueRules.MajorKept}위 잔류 · "
					+ $"{LeagueRules.MajorKept + 1}~{LeagueRules.Clubs}위 강등";
			}
			if (!LeagueRules.HasPostseason(tier))
			{
				return $"1~{LeagueRules.PromotedPlaces}위 승격 · 포스트시즌 없음";
			}
			return $"포스트시즌 최종 1~{LeagueRules.PromotedPlaces}위 승격 · 정규 {LeagueRules.Clubs - LeagueRules.RelegatedPlaces + 1}~{LeagueRules.Clubs}위 강등";
		}

		private static void Popup(GameSession session, string title, string text)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[] { new PLongString { str = title }, new PLongString { str = text } };
			session.Send(popup);
		}
	}

	// Text for the type-1 popup (Panel_Popup1): 443 px wide, no wrapping, five lines between the title and the button
	// (ExhibitionAuto). About 14 px a Hangul syllable and 7.5 px anything else; lines are kept to Width.
	internal static class PopupText
	{
		public const double Width = 400;

		public const double Limit = 420;

		public static double Px(string line) => line.Sum(ch => ch >= '가' && ch <= '힣' ? 14 : 7.5);

		// The items joined by sep, a new line whenever the next would pass Width.
		public static List<string> Wrap(IEnumerable<string> items, string sep)
		{
			List<string> lines = new List<string>();
			string cur = string.Empty;
			foreach (string item in items)
			{
				string next = cur.Length == 0 ? item : cur + sep + item;
				if (cur.Length > 0 && Px(next) > Width)
				{
					lines.Add(cur);
					cur = item;
				}
				else
				{
					cur = next;
				}
			}
			if (cur.Length > 0) lines.Add(cur);
			return lines;
		}
	}
}
