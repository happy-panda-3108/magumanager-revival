using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// MP 장부 (user, 2026-11-14 19시 via the engine session: "MP 장부부터 만들어서 자급자족 테스터는 어떻게 자금을 사용하고 팀을
	// 운영하는지 확인하고 싶음."): every change of a team's MP, with where it came from and the balance after it.
	// Database.AddMp writes a row for each change it makes; the few MP updates written in their own transaction (the
	// startup refunds in ShopHandlers) call Write themselves. Sources are grouped by subject, in and out under one name
	// (a 로스터 확장 refund is a plus under 로스터 확장), so a day's total per name is what that part of the game cost or gave.
	// tools\scripts\systems\mp_ledger.py prints a team's totals per day and source (read-only).
	public static class MpLedger
	{
		public const string League = "리그 경기", Exhibition = "시범경기", Mission = "미션", Achievement = "업적",
			Pouch = "홈런볼·MP주머니", Season = "시즌 보상", Coupon = "쿠폰",
			PlayerPack = "선수팩 구매", Training = "트레이닝·충전권", Coach = "코치", Contract = "계약연장", Release = "선수 방출",
			Lesson = "특강", Roster = "로스터 확장", PositionChange = "포지션 변경", Reclaim = "회수", FaSigning = "FA 영입",
			Test = "시험", Start = "장부 시작", Other = "기타";

		// The table, once per database file; the first time, a 장부 시작 row with each team's balance (amount 0), so the first
		// day's totals start from a known balance.
		public static void Ensure(SqliteConnection c)
		{
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'mp_ledger'";
			if ((long)cmd.ExecuteScalar() > 0) return;
			using SqliteTransaction tx = c.BeginTransaction();
			cmd.Transaction = tx;
			cmd.CommandText = @"CREATE TABLE IF NOT EXISTS mp_ledger (id INTEGER PRIMARY KEY AUTOINCREMENT, user_code INTEGER NOT NULL, at TEXT NOT NULL,
					source TEXT NOT NULL, amount INTEGER NOT NULL, balance INTEGER NOT NULL, caller TEXT NOT NULL DEFAULT '');
				CREATE INDEX IF NOT EXISTS mp_ledger_user ON mp_ledger (user_code, at);
				INSERT INTO mp_ledger (user_code, at, source, amount, balance, caller) SELECT user_code, $at, $s, 0, mp, '' FROM team";
			cmd.Parameters.AddWithValue("$at", Now());
			cmd.Parameters.AddWithValue("$s", Start);
			cmd.ExecuteNonQuery();
			tx.Commit();
		}

		// A change already made to team.mp inside tx (or without one); the balance is read from the team row after it.
		// Never throws: a missing ledger must not undo the MP change itself.
		public static void Write(SqliteConnection c, SqliteTransaction tx, uint user, string source, long amount, string caller)
		{
			if (amount == 0) return;
			try
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = @"INSERT INTO mp_ledger (user_code, at, source, amount, balance, caller)
					SELECT $u, $at, $s, $a, mp, $c FROM team WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$at", Now());
				cmd.Parameters.AddWithValue("$s", string.IsNullOrEmpty(source) ? Other : source);
				cmd.Parameters.AddWithValue("$a", amount);
				cmd.Parameters.AddWithValue("$c", caller ?? string.Empty);
				cmd.ExecuteNonQuery();
			}
			catch (Exception e)
			{
				if (!s_warned)
				{
					s_warned = true;
					ServerLog.Info($"MP 장부: 기록하지 못함 ({e.Message})");
				}
			}
		}

		private static bool s_warned;

		private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
	}
}
