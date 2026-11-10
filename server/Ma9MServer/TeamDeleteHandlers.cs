using System;
using System.Collections.Generic;
using System.IO;
using Ma9MPackets;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// 구단 삭제 (user, 2026-10-18): an account cannot be made again, so a team can be deleted from the
	// 구단 선택 screen (client ServerSelect, delete mode) to start over. Every row keyed by the team's
	// user_code goes, in every table that has that column (tables added later are covered too), except
	// coupon_use: a coupon stays used. The database is copied to data/backup first (VACUUM INTO), so a
	// deletion can be undone by hand. Team codes are AUTOINCREMENT and never handed out again, so
	// in-memory state keyed by the old code (exhibition tries, season stats cache) is harmless.
	public static class TeamDeleteHandlers
	{
		private static readonly HashSet<string> Keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coupon_use" };

		private static string s_dbPath;

		public static void Register(GameLoop loop, string dbPath)
		{
			s_dbPath = dbPath;
			loop.Register<Ma9MPacket.PKS_CS_DELETE_TEAM>(Delete);
		}

		// result: 0 deleted, 1 not a team of this account, 2 failed.
		private static void Delete(GameSession session, Ma9MPacket.PKS_CS_DELETE_TEAM packet)
		{
			Ma9MPacket.PKS_SC_DELETE_TEAM reply = new Ma9MPacket.PKS_SC_DELETE_TEAM();
			uint code = packet.userInfo.user_code.n;
			try
			{
				using SqliteConnection c = Open();
				string name = session.Account == null ? null : TeamOf(c, session.Account.Id, code);
				if (name == null)
				{
					ServerLog.Info($"[{session.Id}] delete team {code} refused: not a team of this account");
					reply.result.n = 1;
				}
				else
				{
					string backup = Backup(c, code);
					int rows = DeleteRows(c, code, out int tables);
					if (session.Team != null && session.Team.UserCode == code)
					{
						session.Team = null;
					}
					ServerLog.Info($"[{session.Id}] deleted team {code} \"{name}\": {rows} rows in {tables} tables (backup {Path.GetFileName(backup)})");
					reply.result.n = 0;
					reply.error.SetNoError();
				}
			}
			catch (Exception e)
			{
				ServerLog.Info($"[{session.Id}] delete team {code} failed: {e.Message}");
				reply.result.n = 2;
			}
			session.Send(reply);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = s_dbPath }.ToString());
			c.Open();
			return c;
		}

		// The team's name when it belongs to the account, else null.
		private static string TeamOf(SqliteConnection c, long accountId, uint code)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT team_name FROM team WHERE user_code = $code AND account_id = $account";
			cmd.Parameters.AddWithValue("$code", code);
			cmd.Parameters.AddWithValue("$account", accountId);
			return cmd.ExecuteScalar() as string;
		}

		private static string Backup(SqliteConnection c, uint code)
		{
			string dir = Path.Combine(Path.GetDirectoryName(s_dbPath), "backup");
			Directory.CreateDirectory(dir);
			string path = Path.Combine(dir, $"ma9m_before_delete_team{code}_{DateTime.Now:yyyyMMdd_HHmmss}.db");
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "VACUUM INTO $path";
			cmd.Parameters.AddWithValue("$path", path);
			cmd.ExecuteNonQuery();
			return path;
		}

		public static int DeleteRows(SqliteConnection c, uint code, out int tables)
		{
			List<string> names = new List<string>();
			using (SqliteCommand list = c.CreateCommand())
			{
				list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
				using SqliteDataReader r = list.ExecuteReader();
				while (r.Read())
				{
					names.Add(r.GetString(0));
				}
			}
			int rows = 0;
			tables = 0;
			using SqliteTransaction tx = c.BeginTransaction();
			foreach (string table in names)
			{
				if (Keep.Contains(table) || !HasUserCode(c, tx, table))
				{
					continue;
				}
				using SqliteCommand del = c.CreateCommand();
				del.Transaction = tx;
				del.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"")}\" WHERE user_code = $code";
				del.Parameters.AddWithValue("$code", code);
				int n = del.ExecuteNonQuery();
				rows += n;
				if (n > 0)
				{
					tables++;
				}
			}
			tx.Commit();
			return rows;
		}

		private static bool HasUserCode(SqliteConnection c, SqliteTransaction tx, string table)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.Transaction = tx;
			cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table.Replace("'", "''")}') WHERE name = 'user_code'";
			return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
		}
	}
}
