using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 구단앨범 (기록실 > 구단정보, last page; also the top bar's team emblem): every card of one club and
	// season, those the team has ever had in colour, those it owns now marked.
	// "Ever had" is table card_seen: a trigger on team_card adds every card a team gets (packs,
	// combination, starters, ...), and it was seeded with the cards owned when it was added
	// (2026-10-19) — cards released or combined away before then are not known.
	public static class AlbumHandlers
	{
		// The client's year byte for the 레전드 page (year 9999); this card set has no such cards.
		private const byte LegendYear = 255;

		// 등급별 pages (user, 2026-10-20): year byte 254, team byte = page — 0 10볼 엘리트, 1 9볼 엘리트,
		// 2 9볼 레어, 3 8볼 레어 (FusionRules rungs 10, 9, 8, 7), every season and club.
		private const byte GradePageYear = 254;

		private static readonly int[] GradePageRungs = { 10, 9, 8, 7 };

		// 등급별 page 4 = 레전드 (engine session, 2026-11-08; user: "앨범에서도 레전드 카드가 보이게"): every legend card (grade 5),
		// any ball. Clients before 1.0.35 have four pages and never ask for it.
		private const int LegendPage = 4;

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static string s_connectionString;

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dbPath)
		{
			s_db = db;
			s_catalog = catalog;
			s_connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (SqliteConnection connection = Open())
			using (SqliteCommand command = connection.CreateCommand())
			{
				command.CommandText = @"
					CREATE TABLE IF NOT EXISTS card_seen (
						user_code INTEGER NOT NULL,
						card_idx INTEGER NOT NULL,
						first_seen TEXT NOT NULL,
						PRIMARY KEY (user_code, card_idx));
					CREATE TRIGGER IF NOT EXISTS card_seen_on_add AFTER INSERT ON team_card BEGIN
						INSERT OR IGNORE INTO card_seen (user_code, card_idx, first_seen) VALUES (NEW.user_code, NEW.card_idx, NEW.acquired);
					END;
					INSERT OR IGNORE INTO card_seen (user_code, card_idx, first_seen) SELECT user_code, card_idx, acquired FROM team_card;";
				command.ExecuteNonQuery();
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_ALBUM>(Enter);
		}

		// The client asks with year % 100 (or 255 for 레전드) and a club; it splits the reply's cards into
		// batters and pitchers by its own list of that club and season, so only those may be sent.
		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_ALBUM packet)
		{
			uint user = session.Team.UserCode;
			int yy = packet.year.n, team = packet.team.n;
			int year = yy == LegendYear ? 0 : (yy >= 80 ? 1900 + yy : 2000 + yy);
			HashSet<int> seen = Seen(user);
			HashSet<int> owned = s_db.GetCards(user).Select(c => c.Idx).ToHashSet();
			List<int> page = yy == GradePageYear && team == LegendPage
				? s_catalog.All.Where(c => c.IsLegend).Select(c => c.Idx).ToList()
				: yy == GradePageYear
				? s_catalog.All.Where(c => FusionRules.RungOf(c) == GradePageRungs[Math.Clamp(team, 0, GradePageRungs.Length - 1)]).Select(c => c.Idx).ToList()
				: s_catalog.All.Where(c => c.Year == year && c.TeamCode == team).Select(c => c.Idx).ToList();
			Ma9MPacket.PKS_SC_ENTER_ALBUM reply = new Ma9MPacket.PKS_SC_ENTER_ALBUM();
			reply.year.n = packet.year.n;
			reply.team.n = packet.team.n;
			reply.index.array = page.Where(seen.Contains).Select(i => new PInteger { n = i }).ToArray();
			reply.total_count.n = (short)Math.Min(short.MaxValue, seen.Count(i => s_catalog.Get(i) != null));
			reply.possess_player.array = page.Where(owned.Contains).Select(i => new PInteger { n = i }).ToArray();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] album {year} team {team}: {reply.index.array.Length} of {page.Count} seen, {reply.possess_player.array.Length} owned; {reply.total_count.n} seen in all");
		}

		public static HashSet<int> Seen(uint userCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT card_idx FROM card_seen WHERE user_code = $code";
			command.Parameters.AddWithValue("$code", userCode);
			HashSet<int> seen = new HashSet<int>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				seen.Add((int)reader.GetInt64(0));
			}
			return seen;
		}

		private static SqliteConnection Open()
		{
			SqliteConnection connection = new SqliteConnection(s_connectionString);
			connection.Open();
			return connection;
		}
	}
}
