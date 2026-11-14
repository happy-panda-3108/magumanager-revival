using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	public sealed class AccountRow
	{
		public long Id;

		public string Email;
	}

	public sealed class TeamRow
	{
		public uint UserCode;

		public long AccountId;

		public byte ServerType;

		public string TeamName;

		public byte Emblem;

		public byte LeagueGrade;

		public int Mp;

		public int Cash;

		public bool TutorialDone;
	}

	public sealed class Database
	{
		private readonly string m_connectionString;

		public Database(string path)
		{
			m_connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
			Execute(@"
				CREATE TABLE IF NOT EXISTS account (
					id INTEGER PRIMARY KEY AUTOINCREMENT,
					email TEXT NOT NULL UNIQUE,
					pw_hash TEXT NOT NULL,
					created TEXT NOT NULL);
				CREATE TABLE IF NOT EXISTS team (
					user_code INTEGER PRIMARY KEY AUTOINCREMENT,
					account_id INTEGER NOT NULL,
					server_type INTEGER NOT NULL,
					team_name TEXT NOT NULL DEFAULT '',
					emblem INTEGER NOT NULL DEFAULT 0,
					league_grade INTEGER NOT NULL DEFAULT 0,
					mp INTEGER NOT NULL DEFAULT 0,
					cash INTEGER NOT NULL DEFAULT 0,
					tutorial_done INTEGER NOT NULL DEFAULT 0,
					key_player INTEGER NOT NULL DEFAULT 0,
					created TEXT NOT NULL);
				CREATE TABLE IF NOT EXISTS team_card (
					user_code INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					remain_contract INTEGER NOT NULL,
					position_training INTEGER NOT NULL DEFAULT 0,
					is_lock INTEGER NOT NULL DEFAULT 0,
					educated BLOB NOT NULL,
					contract BLOB NOT NULL,
					training BLOB NOT NULL,
					team_color BLOB NOT NULL,
					acquired TEXT NOT NULL,
					PRIMARY KEY (user_code, card_idx));
				CREATE TABLE IF NOT EXISTS team_lineup (
					user_code INTEGER PRIMARY KEY,
					lineup TEXT NOT NULL);");
			// Contracts in real days (systems session, 2026-10-19, ContractHandlers): the end date (NULL =
			// acquired + ContractHandlers.StartDays), renewals so far, and the day a renewal of an expired
			// card restarted its condition at 보통.
			using SqliteConnection c = Open();
			foreach (string column in new[] { "contract_until TEXT", "contract_count INTEGER NOT NULL DEFAULT 0", "contract_restart TEXT" })
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('team_card') WHERE name = '{column.Split(' ')[0]}'";
				if ((long)cmd.ExecuteScalar() == 0)
				{
					cmd.CommandText = $"ALTER TABLE team_card ADD COLUMN {column}";
					cmd.ExecuteNonQuery();
				}
			}
			// MP 장부 (MpLedger, systems session 2026-11-14).
			MpLedger.Ensure(c);
		}

		// Unknown email: create the account with this password (the user asked for sign-up on first login).
		// Returns null when the email exists and the password does not match.
		public AccountRow LoginOrCreate(string email, string password, out bool created)
		{
			created = false;
			using SqliteConnection connection = Open();
			using (SqliteCommand select = connection.CreateCommand())
			{
				select.CommandText = "SELECT id, pw_hash FROM account WHERE email = $email";
				select.Parameters.AddWithValue("$email", email);
				using SqliteDataReader reader = select.ExecuteReader();
				if (reader.Read())
				{
					return VerifyPassword(password, reader.GetString(1)) ? new AccountRow { Id = reader.GetInt64(0), Email = email } : null;
				}
			}
			using SqliteCommand insert = connection.CreateCommand();
			insert.CommandText = "INSERT INTO account (email, pw_hash, created) VALUES ($email, $hash, $now); SELECT last_insert_rowid();";
			insert.Parameters.AddWithValue("$email", email);
			insert.Parameters.AddWithValue("$hash", HashPassword(password));
			insert.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
			created = true;
			return new AccountRow { Id = (long)insert.ExecuteScalar(), Email = email };
		}

		public List<TeamRow> GetTeams(long accountId, byte serverType)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT * FROM team WHERE account_id = $account AND server_type = $server ORDER BY user_code";
			command.Parameters.AddWithValue("$account", accountId);
			command.Parameters.AddWithValue("$server", serverType);
			List<TeamRow> teams = new List<TeamRow>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				teams.Add(ReadTeam(reader));
			}
			return teams;
		}

		public TeamRow CreateTeam(long accountId, byte serverType)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT INTO team (account_id, server_type, created) VALUES ($account, $server, $now); SELECT last_insert_rowid();";
			command.Parameters.AddWithValue("$account", accountId);
			command.Parameters.AddWithValue("$server", serverType);
			command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
			long userCode = (long)command.ExecuteScalar();
			return new TeamRow { UserCode = (uint)userCode, AccountId = accountId, ServerType = serverType, TeamName = string.Empty };
		}

		public bool IsTeamNameTaken(string teamName, uint exceptUserCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT COUNT(*) FROM team WHERE team_name = $name AND user_code <> $code";
			command.Parameters.AddWithValue("$name", teamName);
			command.Parameters.AddWithValue("$code", exceptUserCode);
			return (long)command.ExecuteScalar() > 0;
		}

		// Finishes the intro tutorial: name, emblem and the one key player picked there.
		public void CompleteTeam(TeamRow team, string teamName, byte emblem, int keyPlayer)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "UPDATE team SET team_name = $name, emblem = $emblem, key_player = $key, tutorial_done = 1 WHERE user_code = $code";
			command.Parameters.AddWithValue("$name", teamName);
			command.Parameters.AddWithValue("$emblem", emblem);
			command.Parameters.AddWithValue("$key", keyPlayer);
			command.Parameters.AddWithValue("$code", team.UserCode);
			command.ExecuteNonQuery();
			team.TeamName = teamName;
			team.Emblem = emblem;
			team.TutorialDone = true;
		}

		public List<TeamCard> GetCards(uint userCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT card_idx, remain_contract, position_training, is_lock, educated, contract, training, team_color, acquired, contract_until, contract_count FROM team_card WHERE user_code = $code ORDER BY card_idx";
			command.Parameters.AddWithValue("$code", userCode);
			List<TeamCard> cards = new List<TeamCard>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				cards.Add(new TeamCard
				{
					Idx = (int)reader.GetInt64(0),
					RemainContract = (byte)reader.GetInt64(1),
					PositionTrainingInfo = (byte)reader.GetInt64(2),
					IsLock = reader.GetInt64(3) != 0,
					Educated = ToSBytes((byte[])reader[4]),
					Contract = ToSBytes((byte[])reader[5]),
					Training = ToSBytes((byte[])reader[6]),
					TeamColor = ToSBytes((byte[])reader[7]),
					ContractUntil = ContractHandlers.Until(reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9)),
					ContractCount = (int)reader.GetInt64(10)
				});
				// Days left in real days (0 = expired), as the client shows them.
				cards[^1].RemainContract = ContractHandlers.DaysLeft(cards[^1].ContractUntil, DateTime.Now.Date);
			}
			// 코칭스태프 육성스탯 (systems session, 2026-11-04; nothing unless MA9M_COACH=1).
			CoachHandlers.FillBonus(userCode, cards);
			return cards;
		}

		// The team's cards, most recently acquired first (cards from one pack share a time: the later row
		// first). 선수조합 lists them in this order (user, 2026-10-22: "최근 영입한 순서").
		public List<int> CardsByRecent(uint userCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT card_idx FROM team_card WHERE user_code = $code ORDER BY acquired DESC, rowid DESC";
			command.Parameters.AddWithValue("$code", userCode);
			List<int> order = new List<int>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				order.Add((int)reader.GetInt64(0));
			}
			return order;
		}

		// Adds cards the team does not own yet; returns how many were new.
		public int AddCards(uint userCode, IEnumerable<int> cardIdx, byte contractGames)
		{
			using SqliteConnection connection = Open();
			using SqliteTransaction transaction = connection.BeginTransaction();
			int added = 0;
			foreach (int idx in cardIdx)
			{
				using SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;
				command.CommandText = @"INSERT OR IGNORE INTO team_card (user_code, card_idx, remain_contract, educated, contract, training, team_color, acquired)
					VALUES ($code, $idx, $contract, $zero, $zero, $zero, $zero, $now)";
				command.Parameters.AddWithValue("$code", userCode);
				command.Parameters.AddWithValue("$idx", idx);
				command.Parameters.AddWithValue("$contract", contractGames);
				command.Parameters.AddWithValue("$zero", new byte[6]);
				command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("o"));
				added += command.ExecuteNonQuery();
			}
			transaction.Commit();
			return added;
		}

		// 선수보호: flips a card's lock; returns the new state, or null when the team has no such card.
		public bool? ToggleCardLock(uint userCode, int cardIdx)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "UPDATE team_card SET is_lock = 1 - is_lock WHERE user_code = $code AND card_idx = $idx; SELECT is_lock FROM team_card WHERE user_code = $code AND card_idx = $idx";
			command.Parameters.AddWithValue("$code", userCode);
			command.Parameters.AddWithValue("$idx", cardIdx);
			object value = command.ExecuteScalar();
			return value == null ? null : Convert.ToInt64(value) != 0;
		}

		// Adds (or takes away) MP; returns the new balance. A change goes into the MP 장부 under source (MpLedger's names;
		// the caller's method name as well, so an unnamed one can still be traced).
		public int AddMp(uint userCode, int amount, string source = null, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			if (amount == 0)
			{
				// A balance read: as before, no ledger row.
				command.CommandText = "UPDATE team SET mp = MIN(2147483647, MAX(0, mp + $amount)) WHERE user_code = $code; SELECT mp FROM team WHERE user_code = $code";
				command.Parameters.AddWithValue("$amount", amount);
				command.Parameters.AddWithValue("$code", userCode);
				return Convert.ToInt32(command.ExecuteScalar());
			}
			using SqliteTransaction transaction = connection.BeginTransaction();
			command.Transaction = transaction;
			command.CommandText = "SELECT mp FROM team WHERE user_code = $code";
			command.Parameters.AddWithValue("$code", userCode);
			long before = Convert.ToInt64(command.ExecuteScalar() ?? 0L);
			// Kept inside the client's 32-bit MP field (an MP주머니 jackpot cannot overflow it).
			command.CommandText = "UPDATE team SET mp = MIN(2147483647, MAX(0, mp + $amount)) WHERE user_code = $code; SELECT mp FROM team WHERE user_code = $code";
			command.Parameters.AddWithValue("$amount", amount);
			int after = Convert.ToInt32(command.ExecuteScalar());
			// What really changed (a clamp at 0 or the 32-bit top takes less).
			MpLedger.Write(connection, transaction, userCode, source, after - before, caller);
			transaction.Commit();
			return after;
		}

		public void RemoveCards(uint userCode, IEnumerable<int> cardIdx)
		{
			using SqliteConnection connection = Open();
			using SqliteTransaction transaction = connection.BeginTransaction();
			foreach (int idx in cardIdx)
			{
				using SqliteCommand command = connection.CreateCommand();
				command.Transaction = transaction;
				command.CommandText = "DELETE FROM team_card WHERE user_code = $code AND card_idx = $idx";
				command.Parameters.AddWithValue("$code", userCode);
				command.Parameters.AddWithValue("$idx", idx);
				command.ExecuteNonQuery();
			}
			transaction.Commit();
		}

		public List<uint> AllTeamCodes()
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT user_code FROM team";
			List<uint> codes = new List<uint>();
			using SqliteDataReader reader = command.ExecuteReader();
			while (reader.Read())
			{
				codes.Add((uint)reader.GetInt64(0));
			}
			return codes;
		}

		public Lineup GetLineup(uint userCode)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT lineup FROM team_lineup WHERE user_code = $code";
			command.Parameters.AddWithValue("$code", userCode);
			object value = command.ExecuteScalar();
			return value == null ? null : Lineup.Parse((string)value);
		}

		public void SaveLineup(uint userCode, Lineup lineup)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "INSERT INTO team_lineup (user_code, lineup) VALUES ($code, $lineup) ON CONFLICT(user_code) DO UPDATE SET lineup = $lineup";
			command.Parameters.AddWithValue("$code", userCode);
			command.Parameters.AddWithValue("$lineup", lineup.Serialize());
			command.ExecuteNonQuery();
		}

		private static sbyte[] ToSBytes(byte[] data)
		{
			sbyte[] result = new sbyte[6];
			for (int i = 0; i < result.Length && i < data.Length; i++)
			{
				result[i] = unchecked((sbyte)data[i]);
			}
			return result;
		}

		private static TeamRow ReadTeam(SqliteDataReader reader)
		{
			return new TeamRow
			{
				UserCode = (uint)reader.GetInt64(reader.GetOrdinal("user_code")),
				AccountId = reader.GetInt64(reader.GetOrdinal("account_id")),
				ServerType = (byte)reader.GetInt64(reader.GetOrdinal("server_type")),
				TeamName = reader.GetString(reader.GetOrdinal("team_name")),
				Emblem = (byte)reader.GetInt64(reader.GetOrdinal("emblem")),
				LeagueGrade = (byte)reader.GetInt64(reader.GetOrdinal("league_grade")),
				Mp = (int)reader.GetInt64(reader.GetOrdinal("mp")),
				Cash = (int)reader.GetInt64(reader.GetOrdinal("cash")),
				TutorialDone = reader.GetInt64(reader.GetOrdinal("tutorial_done")) != 0
			};
		}

		private SqliteConnection Open()
		{
			SqliteConnection connection = new SqliteConnection(m_connectionString);
			connection.Open();
			return connection;
		}

		private void Execute(string sql)
		{
			using SqliteConnection connection = Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;
			command.ExecuteNonQuery();
		}

		private static string HashPassword(string password)
		{
			byte[] salt = RandomNumberGenerator.GetBytes(16);
			byte[] hash = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, 100000, HashAlgorithmName.SHA256, 32);
			return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(hash);
		}

		private static bool VerifyPassword(string password, string stored)
		{
			string[] parts = stored.Split(':');
			byte[] salt = Convert.FromBase64String(parts[0]);
			byte[] expected = Convert.FromBase64String(parts[1]);
			byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, 100000, HashAlgorithmName.SHA256, 32);
			return CryptographicOperations.FixedTimeEquals(expected, actual);
		}
	}
}
