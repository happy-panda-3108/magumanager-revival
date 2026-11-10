using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// Each card's three training slots (table card_training in ma9m.db): the book used and its result
	// square. A slot row belongs to one copy of the card — it stores the card's team_card.acquired —
	// so a card released or combined away and drawn again starts with empty slots.
	// The slots' sum is kept in team_card.training, which the roster, the card window and the games read.
	public sealed class TrainingStore
	{
		public const int Slots = 3;

		// TRAINING POINT (user, 2026-10-21): six charges per card copy; each try uses one, the 트레이닝 횟수
		// 충전권 fills them again (table card_tp: charges used by the copy with that acquired date).
		public const int TpCharges = 6;

		private readonly string m_connectionString;

		public TrainingStore(string dbPath)
		{
			m_connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = @"
				CREATE TABLE IF NOT EXISTS card_training (
					user_code INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					slot INTEGER NOT NULL,
					acquired TEXT NOT NULL,
					item INTEGER NOT NULL,
					result INTEGER NOT NULL,
					PRIMARY KEY (user_code, card_idx, slot));
				CREATE TABLE IF NOT EXISTS card_tp (
					user_code INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					acquired TEXT NOT NULL,
					used INTEGER NOT NULL,
					PRIMARY KEY (user_code, card_idx));";
			command.ExecuteNonQuery();
		}

		// (book, result square) per slot; book -1 = empty.
		public (int item, int result)[] Get(uint userCode, int cardIdx)
		{
			using SqliteConnection connection = Open();
			return Read(connection, null, userCode, cardIdx);
		}

		// Puts a result in a slot and rewrites the card's training stats from all its slots.
		// Returns the slots, or null when the team has no such card.
		public (int item, int result)[] Apply(uint userCode, int cardIdx, int slot, int item, int result, out sbyte[] training)
		{
			training = null;
			using SqliteConnection connection = Open();
			using SqliteTransaction transaction = connection.BeginTransaction();
			string acquired;
			using (SqliteCommand select = Command(connection, transaction, "SELECT acquired FROM team_card WHERE user_code = $code AND card_idx = $idx", userCode, cardIdx))
			{
				acquired = select.ExecuteScalar() as string;
			}
			if (acquired == null)
			{
				return null;
			}
			using (SqliteCommand write = Command(connection, transaction, @"
				DELETE FROM card_training WHERE user_code = $code AND card_idx = $idx AND acquired <> $acquired;
				INSERT INTO card_training (user_code, card_idx, slot, acquired, item, result) VALUES ($code, $idx, $slot, $acquired, $item, $result)
				ON CONFLICT(user_code, card_idx, slot) DO UPDATE SET acquired = $acquired, item = $item, result = $result", userCode, cardIdx))
			{
				write.Parameters.AddWithValue("$slot", slot);
				write.Parameters.AddWithValue("$acquired", acquired);
				write.Parameters.AddWithValue("$item", item);
				write.Parameters.AddWithValue("$result", result);
				write.ExecuteNonQuery();
			}
			(int item, int result)[] slots = Read(connection, transaction, userCode, cardIdx);
			training = Sum(slots);
			byte[] blob = new byte[training.Length];
			for (int i = 0; i < blob.Length; i++)
			{
				blob[i] = unchecked((byte)training[i]);
			}
			using (SqliteCommand update = Command(connection, transaction, "UPDATE team_card SET training = $training WHERE user_code = $code AND card_idx = $idx", userCode, cardIdx))
			{
				update.Parameters.AddWithValue("$training", blob);
				update.ExecuteNonQuery();
			}
			transaction.Commit();
			return slots;
		}

		// Charges the card copy has left (all six until it trains).
		public int TpLeft(uint userCode, int cardIdx)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = Command(connection, null, @"
				SELECT t.used FROM card_tp t
				JOIN team_card c ON c.user_code = t.user_code AND c.card_idx = t.card_idx AND c.acquired = t.acquired
				WHERE t.user_code = $code AND t.card_idx = $idx", userCode, cardIdx);
			object used = command.ExecuteScalar();
			return Math.Max(0, TpCharges - (used == null || used is DBNull ? 0 : (int)(long)used));
		}

		// One charge used (a training try); returns the charges left.
		public int UseTp(uint userCode, int cardIdx)
		{
			int left = Math.Max(0, TpLeft(userCode, cardIdx) - 1);
			SetUsed(userCode, cardIdx, TpCharges - left);
			return left;
		}

		// All six again (트레이닝 횟수 충전권).
		public void RefillTp(uint userCode, int cardIdx) => SetUsed(userCode, cardIdx, 0);

		private void SetUsed(uint userCode, int cardIdx, int used)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = Command(connection, null, @"
				INSERT INTO card_tp (user_code, card_idx, acquired, used)
				SELECT user_code, card_idx, acquired, $used FROM team_card WHERE user_code = $code AND card_idx = $idx
				ON CONFLICT(user_code, card_idx) DO UPDATE SET acquired = excluded.acquired, used = excluded.used", userCode, cardIdx);
			command.Parameters.AddWithValue("$used", used);
			command.ExecuteNonQuery();
		}

		// The stat changes of all slots, in the card's stat order.
		public static sbyte[] Sum((int item, int result)[] slots)
		{
			int[] sum = new int[6];
			foreach ((int item, int result) in slots)
			{
				if (TrainingBooks.IsBook(item))
				{
					sum[TrainingBooks.StatSlot(item)] += TrainingBooks.Value(item, result);
				}
			}
			sbyte[] training = new sbyte[6];
			for (int i = 0; i < training.Length; i++)
			{
				training[i] = (sbyte)Math.Clamp(sum[i], sbyte.MinValue, sbyte.MaxValue);
			}
			return training;
		}

		private static (int item, int result)[] Read(SqliteConnection connection, SqliteTransaction transaction, uint userCode, int cardIdx)
		{
			(int item, int result)[] slots = new (int, int)[Slots];
			for (int i = 0; i < Slots; i++)
			{
				slots[i] = (-1, 0);
			}
			using SqliteCommand command = Command(connection, transaction, @"
				SELECT t.slot, t.item, t.result FROM card_training t
				JOIN team_card c ON c.user_code = t.user_code AND c.card_idx = t.card_idx AND c.acquired = t.acquired
				WHERE t.user_code = $code AND t.card_idx = $idx", userCode, cardIdx);
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				int slot = (int)reader.GetInt64(0);
				if (slot >= 0 && slot < Slots)
				{
					slots[slot] = ((int)reader.GetInt64(1), (int)reader.GetInt64(2));
				}
			}
			return slots;
		}

		private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, uint userCode, int cardIdx)
		{
			SqliteCommand command = connection.CreateCommand();
			command.Transaction = transaction;
			command.CommandText = sql;
			command.Parameters.AddWithValue("$code", userCode);
			command.Parameters.AddWithValue("$idx", cardIdx);
			return command;
		}

		private SqliteConnection Open()
		{
			SqliteConnection connection = new SqliteConnection(m_connectionString);
			connection.Open();
			return connection;
		}
	}
}
