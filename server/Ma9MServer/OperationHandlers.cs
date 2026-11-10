using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Ma9MSim;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 팀 관리 > 작전실 (OperationRoom_Main) and the player window's 개인작전 tab, stored per team
	// (systems session, 2026-10-19; sliders designed and tuned in tools/traitsim, HANDOVER_engine.md).
	//
	// Team sliders, 0-100 with 50 neutral, in the client's order:
	//   타자 (offense): 타격 스몰볼↔빅볼, 공격 빠른↔끈질긴, 도루 소극↔적극, 번트 자제↔권장, 수비 안정↔적극
	//   투수 (defense): 투수 변화구↔직구, 승부 빠른↔유인구, 제구 몸쪽↔바깥쪽, 투구 전력↔완급, 교체 자제↔권장
	// 개인작전 overrides the first four of its side for one card (batters: offense 0-3, pitchers:
	// defense 0-3); OpOption 1 = follows the team, 2 = its own values. The engine reads both through
	// CardOps (GameTactics.From(offense, defense) for the team, the card's merged arrays for Own).
	public static class OperationHandlers
	{
		public const int TeamSliders = 5;

		public const int PersonalSliders = 4;

		private const byte Neutral = 50;

		private static string s_cs;

		private static CardCatalog s_catalog;

		public static void Register(GameLoop loop, CardCatalog catalog, string dbPath)
		{
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"
					CREATE TABLE IF NOT EXISTS team_operation (
						user_code INTEGER PRIMARY KEY,
						offense BLOB NOT NULL,
						defense BLOB NOT NULL);
					CREATE TABLE IF NOT EXISTS card_operation (
						user_code INTEGER NOT NULL,
						card_idx INTEGER NOT NULL,
						ops BLOB NOT NULL,
						PRIMARY KEY (user_code, card_idx));";
				cmd.ExecuteNonQuery();
			}
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_OPERATION_CENTER>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_OPERATION>(Apply);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_PERSONAL_OP>(ApplyPersonal);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// The team's sliders (all 50 until the first save).
		public static (byte[] offense, byte[] defense) TeamOps(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT offense, defense FROM team_operation WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			if (r.Read())
			{
				return (Fit((byte[])r[0], TeamSliders), Fit((byte[])r[1], TeamSliders));
			}
			return (Fit(null, TeamSliders), Fit(null, TeamSliders));
		}

		// A card's own four values, or null when it follows the team.
		public static byte[] PersonalOps(uint user, int idx)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT ops FROM card_operation WHERE user_code = $u AND card_idx = $i";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			return cmd.ExecuteScalar() is byte[] ops ? Fit(ops, PersonalSliders) : null;
		}

		// What one card plays with: the team's sliders with its 개인작전 laid over its side, or null
		// when it follows the team (GameCard.Own stays null).
		public static (byte[] offense, byte[] defense)? CardOps(uint user, int idx, bool pitcher)
		{
			byte[] own = PersonalOps(user, idx);
			if (own == null)
			{
				return null;
			}
			(byte[] offense, byte[] defense) = TeamOps(user);
			Array.Copy(own, pitcher ? defense : offense, PersonalSliders);
			return (offense, defense);
		}

		// A game team of a user's club: GameTeam.Tactics from the 작전실, GameCard.Own for every card
		// with 개인작전 (the engine's TacticModel reads both; tools/traitsim/HANDOVER_engine.md).
		public static void ApplyTo(GameTeam t, uint owner, Lineup l)
		{
			if (t == null || l == null || owner == 0)
			{
				return;
			}
			(byte[] offense, byte[] defense) = TeamOps(owner);
			t.Tactics = GameTactics.From(offense, defense);
			Dictionary<int, byte[]> own = new Dictionary<int, byte[]>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT card_idx, ops FROM card_operation WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)owner);
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					own[r.GetInt32(0)] = Fit((byte[])r[1], PersonalSliders);
				}
			}
			if (own.Count == 0)
			{
				return;
			}
			// Order[slot] and Pitchers[slot] were built from the lineup's slots (LeagueService/ExhibitionHandlers.Team).
			void Lay(GameCard card, int idx, bool pitcher)
			{
				if (card == null || !own.TryGetValue(idx, out byte[] ops))
				{
					return;
				}
				byte[] o = (byte[])offense.Clone(), d = (byte[])defense.Clone();
				Array.Copy(ops, pitcher ? d : o, PersonalSliders);
				card.Own = GameTactics.From(o, d);
			}
			for (int slot = 0; slot < t.Order.Length && slot < l.Batters.Length; slot++)
			{
				Lay(t.Order[slot], l.Batters[slot], false);
			}
			for (int slot = 0; slot < t.Pitchers.Length && slot < l.Pitchers.Length; slot++)
			{
				Lay(t.Pitchers[slot], l.Pitchers[slot], true);
			}
		}

		// The player window: the team's four values for the card's side, its own values and OpOption.
		public static void FillCardDetail(uint user, int idx, PArray<PByte> personal, PByte option, PArray<PByte> team)
		{
			bool pitcher = s_catalog?.Get(idx) is CardInfo info && !info.IsBatter;
			(byte[] offense, byte[] defense) = TeamOps(user);
			byte[] side = (pitcher ? defense : offense).Take(PersonalSliders).ToArray();
			byte[] own = PersonalOps(user, idx);
			team.array = side.Select(v => new PByte { n = v }).ToArray();
			personal.array = (own ?? side).Select(v => new PByte { n = v }).ToArray();
			option.n = (byte)(own == null ? 1 : 2);
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_OPERATION_CENTER packet)
		{
			if (session.Team == null)
			{
				return;
			}
			(byte[] offense, byte[] defense) = TeamOps(session.Team.UserCode);
			Ma9MPacket.PKS_SC_ENTER_OPERATION_CENTER reply = new Ma9MPacket.PKS_SC_ENTER_OPERATION_CENTER();
			// OperationRoom_DataCenter keeps five of each; more would run past its arrays.
			reply.op_offense.array = offense.Select(v => new PByte { n = v }).ToArray();
			reply.op_defense.array = defense.Select(v => new PByte { n = v }).ToArray();
			session.Send(reply);
		}

		private static void Apply(GameSession session, Ma9MPacket.PKS_CS_APPLY_OPERATION packet)
		{
			Ma9MPacket.PKS_SC_APPLY_OPERATION reply = new Ma9MPacket.PKS_SC_APPLY_OPERATION();
			if (session.Team == null || packet.op_offense.array == null || packet.op_defense.array == null)
			{
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			uint user = session.Team.UserCode;
			byte[] offense = Fit(packet.op_offense.array.Select(b => b.n).ToArray(), TeamSliders);
			byte[] defense = Fit(packet.op_defense.array.Select(b => b.n).ToArray(), TeamSliders);
			SaveTeam(user, offense, defense);
			reply.result.n = 0;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 작전실: 타자 {string.Join(",", offense)} / 투수 {string.Join(",", defense)}");
		}

		private static void ApplyPersonal(GameSession session, Ma9MPacket.PKS_CS_APPLY_PERSONAL_OP packet)
		{
			Ma9MPacket.PKS_SC_APPLY_PERSONAL_OP reply = new Ma9MPacket.PKS_SC_APPLY_PERSONAL_OP();
			if (session.Team == null)
			{
				reply.result.error_code.n = 1;
				session.Send(reply);
				return;
			}
			uint user = session.Team.UserCode;
			int idx = packet.index.n;
			bool own = packet.OpOption.n == 2 && packet.PersonalOP.array != null;
			byte[] ops = own ? Fit(packet.PersonalOP.array.Select(b => b.n).ToArray(), PersonalSliders) : null;
			SavePersonal(user, idx, ops);
			reply.result.SetNoError();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 개인작전 card {idx}: {(own ? string.Join(",", ops) : "팀 작전")}");
		}

		public static void SaveTeam(uint user, byte[] offense, byte[] defense)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO team_operation (user_code, offense, defense) VALUES ($u, $o, $d)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.Add("$o", SqliteType.Blob).Value = Fit(offense, TeamSliders);
			cmd.Parameters.Add("$d", SqliteType.Blob).Value = Fit(defense, TeamSliders);
			cmd.ExecuteNonQuery();
		}

		// A card's own values, or null to follow the team again.
		public static void SavePersonal(uint user, int idx, byte[] ops)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			if (ops != null)
			{
				cmd.CommandText = "INSERT OR REPLACE INTO card_operation (user_code, card_idx, ops) VALUES ($u, $i, $o)";
				cmd.Parameters.Add("$o", SqliteType.Blob).Value = Fit(ops, PersonalSliders);
			}
			else
			{
				cmd.CommandText = "DELETE FROM card_operation WHERE user_code = $u AND card_idx = $i";
			}
			cmd.ExecuteNonQuery();
		}

		// Round trips on a data copy (--leaguetest): team save, a pitcher's own values merged onto the
		// defense side, back to the team; 0 = ok.
		public static int SelfTest(uint user, int pitcherIdx)
		{
			(byte[] o0, byte[] d0) = TeamOps(user);
			byte[] own0 = PersonalOps(user, pitcherIdx);
			SaveTeam(user, new byte[] { 10, 20, 30, 40, 50 }, new byte[] { 60, 70, 80, 90, 200 });
			(byte[] o1, byte[] d1) = TeamOps(user);
			SavePersonal(user, pitcherIdx, new byte[] { 1, 2, 3, 4 });
			var merged = CardOps(user, pitcherIdx, true);
			SavePersonal(user, pitcherIdx, null);
			bool ok = o1.SequenceEqual(new byte[] { 10, 20, 30, 40, 50 }) && d1.SequenceEqual(new byte[] { 60, 70, 80, 90, 100 })
				&& merged != null && merged.Value.defense.SequenceEqual(new byte[] { 1, 2, 3, 4, 100 }) && merged.Value.offense.SequenceEqual(o1)
				&& CardOps(user, pitcherIdx, true) == null;
			SaveTeam(user, o0, d0);
			SavePersonal(user, pitcherIdx, own0);
			ServerLog.Info($"작전실 self-test: {(ok ? "ok" : "FAIL")} (team, 개인작전 merge, back to team)");
			return ok ? 0 : 1;
		}

		// Exactly n values, each 0-100 (missing ones neutral).
		private static byte[] Fit(byte[] values, int n)
		{
			byte[] fit = Enumerable.Repeat(Neutral, n).ToArray();
			for (int i = 0; values != null && i < Math.Min(n, values.Length); i++)
			{
				fit[i] = Math.Min(values[i], (byte)100);
			}
			return fit;
		}
	}
}
