using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 코칭스태프 > 면담실 (CoachingStaff_ConsultRoom), the 수석코치's job (user, 2026-11-04: "매일 0시, 12시에 면담실에 선수가 찾아와서
	// SP150으로 면담을 하면 12시간 동안 컨디션이 랜덤하게 1~3단계 상승 … 찾아오는 선수는 후보선수를 제외하고 컨디션이 나쁨 또는 매우 나쁨인
	// 선수들 중에서 … 컨디션이 안 좋거나 최근 성적이 좋지 않은 선수들이 면담을 찾아온다는 느낌").
	// - Sittings 0:00-12:00 and 12:00-24:00. Visitors: the 1군's batting order and pitchers (no bench) of the 수석코치's club
	//   whose condition is 나쁨 or 매우 나쁨, whose contract has not run out and who have no 면담 effect running; up to Visitors[grade] (2-5) a sitting, picked
	//   when the room is first opened, more joining later in the sitting while there is room. 매우 나쁨 and a bad last five
	//   games each make a player twice as likely to be picked.
	// - 면담: 150 SP at the start (a started one goes on free), then the dialogue; at its end 1-3 steps (a third each) for 12
	//   hours, which Condition.For takes off like a 컨디션 회복제.
	// Slot labels (the client's SlotStatus): 0 waiting, 1 started, 2 done.
	public static partial class CoachHandlers
	{
		public static readonly TimeSpan ConsultEffect = TimeSpan.FromHours(12);

		// How long the dialogue may stay open (the client's mm:ss clock); a started one can be opened again.
		private static readonly TimeSpan ConsultClock = TimeSpan.FromMinutes(10);

		private const int Waiting = 0, Started = 1, Done = 2;

		// Running effects by team: (card, until, steps).
		private static readonly Dictionary<uint, List<(int card, DateTime until, int steps)>> s_effects = new Dictionary<uint, List<(int, DateTime, int)>>();

		public static DateTime SittingStart(DateTime t) => t.Date.AddHours(t.Hour < 12 ? 0 : 12);

		private static string Sitting(DateTime t) => SittingStart(t).ToString("yyyy-MM-dd HH");

		// Condition steps a 면담 gives the card at that time (Condition.For).
		public static int ConsultSteps(uint user, int cardIdx, DateTime at)
		{
			if (!CoachCatalog.Enabled || s_cs == null) return 0;
			if (!s_effects.TryGetValue(user, out var list))
			{
				list = new List<(int, DateTime, int)>();
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT card_idx, until, steps FROM coach_consult WHERE user_code = $u AND status = 2 AND until > $from";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$from", DateTime.Now.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss"));
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) list.Add((r.GetInt32(0), DateTime.Parse(r.GetString(1)), r.GetInt32(2)));
				s_effects[user] = list;
			}
			int steps = 0;
			foreach ((int card, DateTime until, int s) in list)
			{
				if (card == cardIdx && at < until && at >= until - ConsultEffect) steps = Math.Max(steps, s);
			}
			return steps;
		}

		private sealed class Visitor
		{
			public int Card, Status, Row, Steps;
			public bool Slump;
		}

		private static List<Visitor> Visitors(uint user, string sitting)
		{
			List<Visitor> list = new List<Visitor>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, status, slump, row_index, steps FROM coach_consult WHERE user_code = $u AND sitting = $s ORDER BY rowid";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", sitting);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add(new Visitor { Card = r.GetInt32(0), Status = r.GetInt32(1), Slump = r.GetInt32(2) != 0, Row = r.GetInt32(3), Steps = r.GetInt32(4) });
			return list;
		}

		// This sitting's visitors, new ones added while there is room.
		private static List<Visitor> Seat(uint user, int hd, DateTime now)
		{
			string sitting = Sitting(now);
			List<Visitor> visitors = Visitors(user, sitting);
			if (CoachCatalog.TestMode && visitors.Count > 0 && visitors.All(v => v.Status == Done))
			{
				// Test servers: everyone seen, so a new group may come (the done ones move to a sitting of their own and keep
				// their effects).
				using SqliteConnection m = Open();
				using SqliteCommand move = m.CreateCommand();
				move.CommandText = "UPDATE coach_consult SET sitting = sitting || '#' || rowid WHERE user_code = $u AND sitting = $s AND status = 2";
				move.Parameters.AddWithValue("$u", (long)user);
				move.Parameters.AddWithValue("$s", sitting);
				move.ExecuteNonQuery();
				visitors.Clear();
			}
			int room = CoachCatalog.Visitors[CoachCatalog.Grade(hd)] - visitors.Count;
			if (room <= 0) return visitors;
			Lineup lineup = s_db.GetLineup(user);
			if (lineup == null) return visitors;
			int club = CoachCatalog.LineOf(hd);  // the 수석코치's lineage (11-05)
			HashSet<int> seen = visitors.Select(v => v.Card).ToHashSet();
			LeagueState league = LeagueService.Store?.Load(user);
			// Not a card whose contract has run out: it stays at 최하 whatever happens (Condition.For).
			HashSet<int> expired = s_db.GetCards(user).Where(c => c.RemainContract == 0).Select(c => c.Idx).ToHashSet();
			List<(int card, bool slump, double weight)> pool = new List<(int, bool, double)>();
			foreach (int idx in lineup.Batters.Take(9).Concat(lineup.Pitchers).Where(i => i != 0 && !seen.Contains(i) && !expired.Contains(i)))
			{
				CardInfo info = s_catalog.Get(idx);
				if (info == null || !CoachCatalog.InLine(info, club) || ConsultSteps(user, idx, now) > 0) continue;
				int condition = Condition.For(user, idx, now);
				if (condition < 3) continue;
				bool slump = league != null && Slumping(user, league.Season, idx);
				pool.Add((idx, slump, (condition == 4 ? 2 : 1) * (slump ? 2 : 1)));
			}
			if (CoachCatalog.TestMode && pool.Count < room)
			{
				// Test servers (user 11-04: "컨디션 나쁨 또는 매우 나쁨인 선수만 등장하게 합시다"): too few by the rules, so more of the
				// club's 1군 are made 나쁨 or 매우 나쁨 for a day (TestLow) and come by the rules; never anyone else.
				List<int> fine = lineup.Batters.Take(9).Concat(lineup.Pitchers).Where(i => i != 0 && !seen.Contains(i) && !expired.Contains(i)
					&& s_catalog.Get(i) is CardInfo ci && CoachCatalog.InLine(ci, club) && ConsultSteps(user, i, now) == 0 && pool.All(p => p.card != i)).Distinct().ToList();
				while (pool.Count < room && fine.Count > 0)
				{
					int k = s_rng.Next(fine.Count), idx = fine[k];
					fine.RemoveAt(k);
					MakeTestLow(user, idx, 3 + s_rng.Next(2), now);
					int condition = Condition.For(user, idx, now);
					if (condition < 3) continue;
					bool slump = league != null && Slumping(user, league.Season, idx);
					pool.Add((idx, slump, (condition == 4 ? 2 : 1) * (slump ? 2 : 1)));
				}
			}
			using SqliteConnection c = Open();
			for (; room > 0 && pool.Count > 0; room--)
			{
				double u = s_rng.NextDouble() * pool.Sum(p => p.weight);
				int k = 0;
				while (k < pool.Count - 1 && (u -= pool[k].weight) >= 0) k++;
				(int card, bool slump, double _) = pool[k];
				pool.RemoveAt(k);
				using SqliteCommand ins = c.CreateCommand();
				ins.CommandText = "INSERT OR IGNORE INTO coach_consult (user_code, sitting, card_idx, status, slump) VALUES ($u, $s, $c, 0, $slump)";
				ins.Parameters.AddWithValue("$u", (long)user);
				ins.Parameters.AddWithValue("$s", sitting);
				ins.Parameters.AddWithValue("$c", card);
				ins.Parameters.AddWithValue("$slump", slump ? 1 : 0);
				ins.ExecuteNonQuery();
				visitors.Add(new Visitor { Card = card, Slump = slump });
			}
			return visitors;
		}

		// Test servers only: cards made 나쁨 (3) or 매우 나쁨 (4) for the 면담실, by team: card → (level, from, until).
		private static readonly Dictionary<uint, Dictionary<int, (int level, DateTime from, DateTime until)>> s_testLow = new Dictionary<uint, Dictionary<int, (int, DateTime, DateTime)>>();

		// The level a test server holds the card at, at least (Condition.Level takes the worse of it and the day's chain); 0 elsewhere.
		public static int TestLow(uint user, int cardIdx, DateTime at)
		{
			if (!CoachCatalog.TestMode || s_cs == null) return 0;
			return TestLows(user).TryGetValue(cardIdx, out var low) && at >= low.from && at < low.until ? low.level : 0;
		}

		private static Dictionary<int, (int level, DateTime from, DateTime until)> TestLows(uint user)
		{
			if (s_testLow.TryGetValue(user, out var map)) return map;
			map = new Dictionary<int, (int, DateTime, DateTime)>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, level, from_at, until FROM coach_testlow WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) map[r.GetInt32(0)] = (r.GetInt32(1), DateTime.Parse(r.GetString(2)), DateTime.Parse(r.GetString(3)));
			s_testLow[user] = map;
			return map;
		}

		private static void MakeTestLow(uint user, int card, int level, DateTime now)
		{
			DateTime until = now.AddDays(1);
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO coach_testlow (user_code, card_idx, level, from_at, until) VALUES ($u, $c, $l, $f, $t)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$c", card);
			cmd.Parameters.AddWithValue("$l", level);
			cmd.Parameters.AddWithValue("$f", now.ToString("yyyy-MM-dd HH:mm:ss"));
			cmd.Parameters.AddWithValue("$t", until.ToString("yyyy-MM-dd HH:mm:ss"));
			cmd.ExecuteNonQuery();
			TestLows(user)[card] = (level, now, until);
			ServerLog.Info($"코칭스태프 (test): {s_catalog.Get(card)?.Name} made {(level == 4 ? "매우 나쁨" : "나쁨")} until {until:MM-dd HH:mm} for the 면담실");
		}

		// The last five league games went badly: a batter under .200 with 10 at bats, a pitcher's ERA over 6.00 in 3 innings.
		private static bool Slumping(uint user, int season, int idx)
		{
			List<PlayerGame> games = LeagueService.Store.RecentGames(user, season, idx, 5);
			int ab = games.Sum(g => g.Line.AB), h = games.Sum(g => g.Line.H), outs = games.Sum(g => g.Line.Outs), er = games.Sum(g => g.Line.ER);
			return games.Any(g => g.Line.Pitcher) ? outs >= 9 && er * 27.0 / outs > 6.0 : ab >= 10 && h < 0.2 * ab;
		}

		// Dev (systems session, 2026-11-10; user: "박석민의 컨디션이 나쁨이지만 면담실에 찾아온 선수가 없다고 함"): each 1군 card
		// against Seat's rules now. Read-only. "--consultwhy <user code>" (MA9M_COACH=1).
		public static int ConsultWhy(uint user)
		{
			DateTime now = DateTime.Now;
			int hd = Staff(user)[CoachCatalog.HD];
			string sitting = Sitting(now);
			List<Visitor> seated = Visitors(user, sitting);
			int line = hd == 0 ? 0 : CoachCatalog.LineOf(hd);
			ServerLog.Info($"[consultwhy] team {user} at {now:MM-dd HH:mm}: 수석코치 {(hd == 0 ? "none" : $"{hd} {Label(hd)}")}, line {line}, sitting {sitting}, " +
				$"seated {seated.Count} of {(hd == 0 ? 0 : CoachCatalog.Visitors[CoachCatalog.Grade(hd)])} ({string.Join(", ", seated.Select(v => $"{s_catalog.Get(v.Card)?.Name}({v.Status})"))})");
			Lineup lineup = s_db.GetLineup(user);
			if (lineup == null) return 1;
			Dictionary<int, TeamCard> owned = s_db.GetCards(user).ToDictionary(c => c.Idx);
			IEnumerable<(string slot, int idx)> slots = lineup.Batters.Select((idx, i) => (i < 9 ? $"{i + 1}번" : $"후보{i - 8}", idx))
				.Concat(lineup.Pitchers.Select((idx, i) => (i < 5 ? $"SP{i + 1}" : i < 9 ? $"RP{i - 4}" : i == 9 ? "SU" : "CP", idx)));
			foreach ((string slot, int idx) in slots.Where(s => s.idx != 0))
			{
				CardInfo info = s_catalog.Get(idx);
				int cardLine = info == null ? -1 : info.IsFA ? line : CoachCatalog.Line(info.TeamCode);
				bool bench = slot.StartsWith("후보");
				bool expired = owned.TryGetValue(idx, out TeamCard tc) && tc.RemainContract == 0;
				int condition = Condition.For(user, idx, now), baseLevel = Condition.Base(user, idx, now);
				int steps = ConsultSteps(user, idx, now), item = PlayerManageHandlers.Effect(user, idx, now);
				List<string> why = new List<string>();
				if (bench) why.Add("후보");
				if (cardLine != line) why.Add($"계보 {cardLine}");
				if (expired) why.Add("계약 끝");
				if (steps > 0) why.Add($"면담 효과 {steps}");
				if (condition < 3) why.Add($"컨디션 {condition}");
				if (seated.Any(v => v.Card == idx)) why.Add("이미 옴");
				ServerLog.Info($"[consultwhy]   {slot} {info?.Year} {info?.Name} (team {info?.TeamCode}): 컨디션 {condition} (base {baseLevel}, 회복제 −{item}, 면담 −{steps}) → {(why.Count == 0 ? "올 수 있음" : string.Join(", ", why))}");
			}
			return 0;
		}

		// 면담 effects running now from earlier sittings (card, until), the one ending last first; a card's latest only.
		private static List<(int card, DateTime until)> RunningEffects(uint user, DateTime now)
		{
			List<(int, DateTime)> list = new List<(int, DateTime)>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT card_idx, MAX(until) FROM coach_consult WHERE user_code = $u AND status = 2 AND sitting <> $s AND until > $now GROUP BY card_idx ORDER BY MAX(until) DESC";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", Sitting(now));
			cmd.Parameters.AddWithValue("$now", now.ToString("yyyy-MM-dd HH:mm:ss"));
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read()) list.Add((r.GetInt32(0), DateTime.Parse(r.GetString(1))));
			return list;
		}

		// Another 수석코치 was registered: visitors not seen yet leave.
		private static void ForgetWaiting(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "DELETE FROM coach_consult WHERE user_code = $u AND sitting = $s AND status = 0";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", Sitting(DateTime.Now));
			cmd.ExecuteNonQuery();
		}

		private static void EnterRoom(GameSession session, Ma9MPacket.PKS_CS_ENTER_CONSULTING_ROOM packet)
		{
			uint user = session.Team.UserCode;
			DateTime now = DateTime.Now;
			int hd = Staff(user)[CoachCatalog.HD];
			Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM reply = new Ma9MPacket.PKS_SC_ENTER_CONSULTING_ROOM();
			if (hd == 0)
			{
				reply.error.SetErrorMessage("수석코치를 등록하지 않으면 면담실을 이용하실 수 없습니다.");
				reply.PlayerCard.array = Array.Empty<PUICardInfo>();
				reply.PlayerCardLabels.array = Array.Empty<PByte>();
				reply.EffectCard.array = Array.Empty<PUICardInfo>();
				reply.EffectRemainTicks.array = Array.Empty<PLong>();
				session.Send(reply);
				return;
			}
			List<Visitor> visitors = Seat(user, hd, now);
			Dictionary<int, TeamCard> owned = s_db.GetCards(user).ToDictionary(c => c.Idx);
			visitors = visitors.Where(v => owned.ContainsKey(v.Card)).ToList();
			reply.error.SetNoError();
			reply.PlayerCard.array = visitors.Select(v =>
			{
				TeamCard c = owned[v.Card];
				return new PUICardInfo(v.Card, Condition.For(user, v.Card, now), c.RemainContract, c.PositionTrainingInfo, (byte)(c.IsLock ? 2 : 1));
			}).ToArray();
			reply.PlayerCardLabels.array = visitors.Select(v => new PByte { n = (byte)v.Status }).ToArray();
			reply.HDCoachIndex.n = hd;
			reply.HDCoachString.n = visitors.Any(v => v.Status != Done) ? CoachCatalog.RoomVisitors : visitors.Count > 0 ? CoachCatalog.RoomDone : CoachCatalog.RoomNobody;
			reply.RemainTicks.n = (SittingStart(now).AddHours(12) - now).Ticks;
			// The second page (app 1.0.47, user 11-10 결정 2): 면담 effects of earlier sittings still running, the newest first.
			List<(int card, DateTime until)> running = RunningEffects(user, now).Where(e => owned.ContainsKey(e.card) && visitors.All(v => v.Card != e.card)).Take(5).ToList();
			reply.EffectCard.array = running.Select(e =>
			{
				TeamCard c = owned[e.card];
				return new PUICardInfo(e.card, Condition.For(user, e.card, now), c.RemainContract, c.PositionTrainingInfo, (byte)(c.IsLock ? 2 : 1));
			}).ToArray();
			reply.EffectRemainTicks.array = running.Select(e => new PLong { n = (e.until - now).Ticks }).ToArray();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 면담실 ({Label(hd)}): {string.Join(", ", visitors.Select(v => $"{s_catalog.Get(v.Card)?.Name}({v.Status}{(v.Slump ? ",부진" : string.Empty)})"))}");
		}

		private static void StartConsult(GameSession session, Ma9MPacket.PKS_CS_CONSULTING_START packet)
		{
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_CONSULTING_START reply = new Ma9MPacket.PKS_SC_CONSULTING_START();
			string refuse = TryStartConsult(user, packet.CardIndex.n, out int row);
			reply.my_sp.n = Sp(user);
			if (refuse != null)
			{
				reply.error.SetErrorMessage(refuse);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] 면담 refused: {refuse}");
				return;
			}
			reply.error.SetNoError();
			reply.ConsultintIndex.n = row;
			reply.RemainTime.n = ConsultClock.Ticks;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 면담 시작: {s_catalog.Get(packet.CardIndex.n)?.Name}, dialogue {row}, SP {reply.my_sp.n}");
		}

		internal static string TryStartConsult(uint user, int cardIdx, out int row)
		{
			row = 0;
			string sitting = Sitting(DateTime.Now);
			Visitor v = Visitors(user, sitting).FirstOrDefault(x => x.Card == cardIdx);
			if (Staff(user)[CoachCatalog.HD] == 0) return "수석코치를 등록하지 않으면 면담실을 이용하실 수 없습니다.";
			if (v == null) return "지금 면담실에 와 있는 선수가 아닙니다.";
			if (v.Status == Done) return "이미 면담을 마친 선수입니다.";
			if (v.Status == Waiting)
			{
				if (Sp(user) < CoachCatalog.ConsultSp) return "보유한 SP가 부족합니다.";
				AddSp(user, -CoachCatalog.ConsultSp);
				CardInfo info = s_catalog.Get(cardIdx);
				v.Row = CoachCatalog.ConsultRow(info?.IsBatter != false, v.Slump, s_rng.Next(2), s_rng.Next(CoachCatalog.ConsultVariants));
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "UPDATE coach_consult SET status = 1, row_index = $r WHERE user_code = $u AND sitting = $s AND card_idx = $c";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", sitting);
				cmd.Parameters.AddWithValue("$c", cardIdx);
				cmd.Parameters.AddWithValue("$r", v.Row);
				cmd.ExecuteNonQuery();
			}
			row = v.Row;
			return null;
		}

		private static void EndConsult(GameSession session, Ma9MPacket.PKS_CS_CONSULTING_END packet)
		{
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_CONSULTING_END reply = new Ma9MPacket.PKS_SC_CONSULTING_END();
			string refuse = TryEndConsult(user, packet.CardIndex.n, DateTime.Now, out int steps);
			if (refuse != null)
			{
				reply.error.SetErrorMessage(refuse);
				reply.EffectList.str = string.Empty;
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] 면담 종료 refused: {refuse}");
				return;
			}
			reply.error.SetNoError();
			reply.EffectList.str = $"CONDITION:{steps}";
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 면담 종료: {s_catalog.Get(packet.CardIndex.n)?.Name} 컨디션 +{steps} for {ConsultEffect.TotalHours:F0} h");
		}

		internal static string TryEndConsult(uint user, int cardIdx, DateTime now, out int steps)
		{
			steps = 0;
			string sitting = Sitting(now);
			Visitor v = Visitors(user, sitting).FirstOrDefault(x => x.Card == cardIdx);
			if (v == null || v.Status != Started) return "진행 중인 면담이 없습니다.";
			steps = s_rng.Next(1, 4);
			DateTime until = now + ConsultEffect;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "UPDATE coach_consult SET status = 2, steps = $n, until = $t WHERE user_code = $u AND sitting = $s AND card_idx = $c";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$s", sitting);
				cmd.Parameters.AddWithValue("$c", cardIdx);
				cmd.Parameters.AddWithValue("$n", steps);
				cmd.Parameters.AddWithValue("$t", until.ToString("yyyy-MM-dd HH:mm:ss"));
				cmd.ExecuteNonQuery();
			}
			s_effects.Remove(user);
			return null;
		}
	}
}
