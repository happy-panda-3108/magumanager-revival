using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// Items a team owns, and which coupons have been used (tables team_item and coupon_use in ma9m.db).
	public sealed class InventoryStore
	{
		private readonly string m_connectionString;

		public InventoryStore(string dbPath)
		{
			m_connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = @"
				CREATE TABLE IF NOT EXISTS team_item (
					user_code INTEGER NOT NULL,
					item_idx INTEGER NOT NULL,
					amount INTEGER NOT NULL,
					PRIMARY KEY (user_code, item_idx));
				CREATE TABLE IF NOT EXISTS coupon_use (
					code TEXT NOT NULL,
					user_code INTEGER NOT NULL,
					used TEXT NOT NULL,
					account_id INTEGER NOT NULL DEFAULT 0,
					PRIMARY KEY (code, user_code));";
			command.ExecuteNonQuery();
			// Coupons are once per account (user, 2026-10-19); rows from before then get their team's account.
			command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('coupon_use') WHERE name = 'account_id'";
			if ((long)command.ExecuteScalar() == 0)
			{
				command.CommandText = @"
					ALTER TABLE coupon_use ADD COLUMN account_id INTEGER NOT NULL DEFAULT 0;
					UPDATE coupon_use SET account_id = COALESCE((SELECT account_id FROM team WHERE team.user_code = coupon_use.user_code), 0);";
				command.ExecuteNonQuery();
			}
		}

		// (item index, amount) for every item the team has at least one of.
		public List<(int item, int amount)> Items(uint userCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT item_idx, amount FROM team_item WHERE user_code = $code AND amount > 0 ORDER BY item_idx";
			command.Parameters.AddWithValue("$code", userCode);
			List<(int, int)> items = new List<(int, int)>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				items.Add(((int)reader.GetInt64(0), (int)reader.GetInt64(1)));
			}
			return items;
		}

		public int Amount(uint userCode, int item)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT amount FROM team_item WHERE user_code = $code AND item_idx = $item";
			command.Parameters.AddWithValue("$code", userCode);
			command.Parameters.AddWithValue("$item", item);
			object value = command.ExecuteScalar();
			return value == null ? 0 : Convert.ToInt32(value);
		}

		// Adds count (negative takes away, never below zero); returns the new amount.
		public int Add(uint userCode, int item, int count)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = @"INSERT INTO team_item (user_code, item_idx, amount) VALUES ($code, $item, MAX(0, $count))
				ON CONFLICT(user_code, item_idx) DO UPDATE SET amount = MAX(0, amount + $count);
				SELECT amount FROM team_item WHERE user_code = $code AND item_idx = $item";
			command.Parameters.AddWithValue("$code", userCode);
			command.Parameters.AddWithValue("$item", item);
			command.Parameters.AddWithValue("$count", count);
			return Convert.ToInt32(command.ExecuteScalar());
		}

		// How many times a coupon was used, by anyone, and whether this account used it (with any team,
		// including deleted ones: coupon_use rows outlive their team).
		public (int total, bool mine) CouponUses(string code, long accountId)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT COUNT(*), SUM(account_id = $account) FROM coupon_use WHERE code = $code";
			command.Parameters.AddWithValue("$code", code);
			command.Parameters.AddWithValue("$account", accountId);
			using SqliteDataReader reader = command.ExecuteReader();
			reader.Read();
			return ((int)reader.GetInt64(0), !reader.IsDBNull(1) && reader.GetInt64(1) > 0);
		}

		public void UseCoupon(string code, uint userCode, long accountId)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT OR IGNORE INTO coupon_use (code, user_code, used, account_id) VALUES ($code, $user, $now, $account)";
			command.Parameters.AddWithValue("$code", code);
			command.Parameters.AddWithValue("$user", userCode);
			command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
			command.Parameters.AddWithValue("$account", accountId);
			command.ExecuteNonQuery();
		}

		private SqliteConnection Open()
		{
			SqliteConnection connection = new SqliteConnection(m_connectionString);
			connection.Open();
			return connection;
		}
	}
}
