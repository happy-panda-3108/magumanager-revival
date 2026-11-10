using System;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// Access tokens kept in the DB (systems session, 2026-10-19): a client that was connected when the
	// server restarted reconnects with PKS_CS_REQUEST_ACCESS_TOKEN_LOGIN, and the token is no longer in
	// LoginHandlers' memory; it froze on "연결이 원활하지 않습니다" until the user logged in again.
	// A token holds the account and the team chosen with it, for 30 days.
	public static class TokenStore
	{
		private const int KeepDays = 30;

		private static string s_cs;

		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"
				CREATE TABLE IF NOT EXISTS access_token (
					token TEXT PRIMARY KEY,
					account_id INTEGER NOT NULL,
					email TEXT NOT NULL,
					user_code INTEGER NOT NULL DEFAULT 0,
					server_type INTEGER NOT NULL DEFAULT 0,
					issued TEXT NOT NULL);
				DELETE FROM access_token WHERE issued < $old;";
			cmd.Parameters.AddWithValue("$old", DateTime.UtcNow.AddDays(-KeepDays).ToString("o", CultureInfo.InvariantCulture));
			cmd.ExecuteNonQuery();
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		public static void Save(Guid token, AccountRow account)
		{
			if (s_cs == null || account == null)
			{
				return;
			}
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO access_token (token, account_id, email, issued) VALUES ($t, $a, $e, $now)";
			cmd.Parameters.AddWithValue("$t", token.ToString());
			cmd.Parameters.AddWithValue("$a", account.Id);
			cmd.Parameters.AddWithValue("$e", account.Email ?? string.Empty);
			cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
			cmd.ExecuteNonQuery();
		}

		public static void SetTeam(Guid token, TeamRow team)
		{
			if (s_cs == null || team == null)
			{
				return;
			}
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "UPDATE access_token SET user_code = $u, server_type = $s WHERE token = $t";
			cmd.Parameters.AddWithValue("$u", (long)team.UserCode);
			cmd.Parameters.AddWithValue("$s", (int)team.ServerType);
			cmd.Parameters.AddWithValue("$t", token.ToString());
			cmd.ExecuteNonQuery();
		}

		// The account and team of a stored token (team null when none was chosen or it was deleted).
		public static (AccountRow account, TeamRow team)? Find(Guid token, Database db)
		{
			if (s_cs == null || token == Guid.Empty)
			{
				return null;
			}
			long accountId;
			string email;
			uint userCode;
			byte serverType;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT account_id, email, user_code, server_type FROM access_token WHERE token = $t";
				cmd.Parameters.AddWithValue("$t", token.ToString());
				using SqliteDataReader r = cmd.ExecuteReader();
				if (!r.Read())
				{
					return null;
				}
				accountId = r.GetInt64(0);
				email = r.GetString(1);
				userCode = (uint)r.GetInt64(2);
				serverType = (byte)r.GetInt64(3);
			}
			AccountRow account = new AccountRow { Id = accountId, Email = email };
			TeamRow team = userCode == 0 ? null : db.GetTeams(accountId, serverType).Find(t => t.UserCode == userCode);
			return (account, team);
		}
	}
}
