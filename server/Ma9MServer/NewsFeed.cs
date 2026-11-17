using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 로비 소식 띠 (systems session, 2026-11-17; user 09:3x: "로비 소식에 띠를 추가해서 약간 기존 게임들 공지사항 띠처럼 나와서 오른쪽에서
	// 왼쪽으로 글자가 흘러가며 볼 수 있듯이"): short news lines (FA signings and renewals of every real team, the season's ball, …)
	// kept in table news; an app with ClientMark gets the latest at each lobby visit as POPUP type Type [line, line, …], and its band
	// (Ma9MNewsTicker) scrolls them right to left. Older apps never see it.
	public static class NewsFeed
	{
		public const byte Type = 32;

		public const string ClientMark = "ma9m-news1";

		public const int Shown = 12, Hours = 48;

		private static string s_cs;

		// AlbumHandlers.Register.
		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "CREATE TABLE IF NOT EXISTS news (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, text TEXT NOT NULL)";
			cmd.ExecuteNonQuery();
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		public static void Add(string text)
		{
			if (s_cs == null || string.IsNullOrWhiteSpace(text))
			{
				return;
			}
			try
			{
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "INSERT INTO news (at, text) VALUES ($a, $t)";
				cmd.Parameters.AddWithValue("$a", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$t", text.Replace('\n', ' '));
				cmd.ExecuteNonQuery();
				ServerLog.Info($"소식: {text}");
			}
			catch (Exception e)
			{
				ServerLog.Warn($"소식 저장 실패: {e.Message}");
			}
		}

		// 공지 (user 11-17 11:5x: "새로 업데이트되는 기능들에 대해서는 공지로 담아도 될 것 같음"): news rows starting with this go first and stay
		// NoticeDays (tools\scripts\systems\news_notice.py writes them, also while the server runs).
		public const string NoticeMark = "[공지]";

		public const int NoticeDays = 7, MaxNotices = 4;

		// The band's lines: the notices of the last NoticeDays, then the news of the last Hours, newest first.
		public static List<string> Recent()
		{
			List<string> lines = new List<string>();
			if (s_cs == null)
			{
				return lines;
			}
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT text FROM news WHERE at >= $since AND text LIKE $mark ORDER BY id DESC LIMIT $n";
				cmd.Parameters.AddWithValue("$since", DateTime.Now.AddDays(-NoticeDays).ToString("o", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$mark", NoticeMark + "%");
				cmd.Parameters.AddWithValue("$n", MaxNotices);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					lines.Add(r.GetString(0));
				}
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT text FROM news WHERE at >= $since AND text NOT LIKE $mark ORDER BY id DESC LIMIT $n";
				cmd.Parameters.AddWithValue("$since", DateTime.Now.AddHours(-Hours).ToString("o", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$mark", NoticeMark + "%");
				cmd.Parameters.AddWithValue("$n", Shown - lines.Count);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					lines.Add(r.GetString(0));
				}
			}
			return lines;
		}

		// LobbyHandlers.EnterMain: the band's lines (an empty list sends one line about the band, so it shows).
		public static void OnLobby(GameSession session)
		{
			if (session?.ClientVersion?.Contains(ClientMark) != true)
			{
				return;
			}
			List<string> lines = Recent();
			if (lines.Count == 0)
			{
				lines.Add("마구매니저 소식: FA 영입·재계약 같은 리그 소식이 여기에 흐릅니다.");
			}
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = Type;
			popup.Messages.array = lines.Select(s => new PLongString { str = s }).ToArray();
			session.Send(popup);
		}

		public static string TeamName(uint user)
		{
			if (s_cs == null)
			{
				return null;
			}
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT team_name FROM team WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			object v = cmd.ExecuteScalar();
			return v == null || v is DBNull ? null : (string)v;
		}
	}
}
