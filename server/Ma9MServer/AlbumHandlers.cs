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
		internal const byte GradePageYear = 254;

		private static readonly int[] GradePageRungs = { 10, 9, 8, 7 };

		// 등급별 page 4 = 레전드 (engine session, 2026-11-08; user: "앨범에서도 레전드 카드가 보이게"): every legend card (grade 5),
		// any ball. Clients before 1.0.35 have four pages and never ask for it.
		private const int LegendPage = 4;

		// FA 시장 (systems session, 2026-11-15; user via the engine: "FA 명단도 괜찮고, FA시장도 나쁘지 않을 것 같음"): 등급별 page 5 = every FA
		// card, and a season's club 20 (the original's FA page, the app shows the FA team 250's cards of that season). Every card of
		// those pages comes as seen (in colour: the list to sign from); a tap signs (FaMarket).
		public const int FaPage = 5, FaAlbumTeam = 20;

		// 전력강화 > FA 시장 (systems session 2026-11-16; user 18:3x 8·9번: the album shows every FA card, signing is the market's):
		// 등급별 page 6 = the team's market list (FaMarket.MarketList), asked by the 1.0.54+ app only.
		public const int MarketPage = 6;

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
			// FA 시장's per-season lists (2026-11-17).
			FaMarket.Init(dbPath);
			// FA 에이전트 협상 (결렬 기록) and the lobby's news band (2026-11-17).
			FaNegotiation.Init(dbPath);
			NewsFeed.Init(dbPath);
			// FA cards in the signing club's uniform: the render list and the app's list (FaArt, 2026-11-17).
			FaArt.Init(dbPath);
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
			bool market = yy == GradePageYear && team == MarketPage;
			bool faPage = market || (yy == GradePageYear ? team == FaPage : team == FaAlbumTeam);
			List<int> page = market ? FaMarket.MarketList(user) : Page(s_catalog, yy, team, year);
			Ma9MPacket.PKS_SC_ENTER_ALBUM reply = new Ma9MPacket.PKS_SC_ENTER_ALBUM();
			reply.year.n = packet.year.n;
			reply.team.n = packet.team.n;
			reply.index.array = page.Where(i => faPage || seen.Contains(i)).Select(i => new PInteger { n = i }).ToArray();
			// The FA cards are copies to sign, not to collect: out of the count (the app's total leaves them out too, 2026-11-15).
			reply.total_count.n = (short)Math.Min(short.MaxValue, seen.Count(i => s_catalog.Get(i) is CardInfo c && !c.IsFA));
			reply.possess_player.array = page.Where(owned.Contains).Select(i => new PInteger { n = i }).ToArray();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] album {year} team {team}: {reply.index.array.Length} of {page.Count} seen, {reply.possess_player.array.Length} owned; {reply.total_count.n} seen in all");
			if (market)
			{
				FaMarket.FullRosterNotice(session, user);
			}
		}

		// The cards of a page (yy = the request's year byte, year = the season it means).
		internal static List<int> Page(CardCatalog catalog, int yy, int team, int year) =>
			yy == GradePageYear && team == LegendPage
				? catalog.All.Where(c => c.IsLegend).Select(c => c.Idx).ToList()
				: yy == GradePageYear && team == FaPage
				? catalog.All.Where(c => c.IsFA).Select(c => c.Idx).ToList()
				: yy == GradePageYear
				? catalog.All.Where(c => !c.IsFA && FusionRules.RungOf(c) == GradePageRungs[Math.Clamp(team, 0, GradePageRungs.Length - 1)]).Select(c => c.Idx).ToList()
				: team == FaAlbumTeam
				? catalog.All.Where(c => c.IsFA && c.Year == year).Select(c => c.Idx).ToList()
				: catalog.All.Where(c => c.Year == year && c.TeamCode == team).Select(c => c.Idx).ToList();

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
