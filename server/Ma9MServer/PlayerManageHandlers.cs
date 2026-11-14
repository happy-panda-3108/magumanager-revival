using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 선수관리 (팀 로스터 > 선수관리 토글 → 선수 카드, client Roster_Manager): 컨디션 회복제 (user, 2026-10-22).
	// 초급/중급/고급 raise one player's condition by 1/2/3 steps (never above 최상) for 24 hours from their use:
	// "하루, 즉 72경기만 적용". At most 5 players have one at a time (the screen's 5 slots, "하루 회복 인원
	// 5명"). Using one on a player who already has one replaces it (the old one is lost and the 24 hours start
	// again, as the client's confirm says); 적용취소 ends it at once and frees the slot (the item is not
	// returned). Only expired contracts (0 days) are refused, as the client also checks (1 day or fewer until 2026-11-08,
	// 2 or fewer until 2026-10-25). A card with 1 day left keeps its item until midnight; from then it is expired and
	// plays at 최하 whatever the item (Condition.Level), unless renewed in time.
	// Condition.For takes off the steps for any time inside the window, so league games (at their slot
	// times), exhibitions and the screens (now) agree; the daily condition underneath keeps changing at
	// midnight. Table card_manage: one row per card copy (team_card.acquired), so a card released or
	// combined away loses its item. Shop: 초급 15만, 중급 30만 MP; 고급 is not sold.
	public static class PlayerManageHandlers
	{
		public const int Basic = 30101, Middle = 30102, Advanced = 30103;

		public const int Slots = 5;

		public const int Hours = 24;

		// Roster_Manager.OnBtn_Apply refuses remain_contract < 1 (user, 2026-11-08: "계약일수가 1일 남은 선수들도 컨디션
		// 관리가 가능하도록"; 2026-10-25 "계약기간이 1일 이하로 남은 선수만 … 먹일 수 없도록" = 2 until then, 3 before).
		public const int MinContractDays = 1;

		// Apply results (Roster_Manager shows them): 0 ok, 1 no such item or card, 2 contract too short, 3 slots full.
		public const byte Ok = 0, NoItem = 1, ShortContract = 2, SlotsFull = 3;

		private static readonly (int item, string grade, int steps)[] Grades = { (Basic, "초급", 1), (Middle, "중급", 2), (Advanced, "고급", 3) };

		private sealed class Use
		{
			public int Item;

			public DateTime At;

			public DateTime Until => At.AddHours(Hours);

			public bool ActiveAt(DateTime at) => at >= At && at < Until;
		}

		private static Database s_db;

		private static string s_cs;

		// Per team: card → its item, reloaded after a minute (cards released or combined away drop out) and
		// dropped on every change here. Condition.For reads it from the league's thread too.
		private static readonly Dictionary<uint, (DateTime loaded, Dictionary<int, Use> cards)> s_cache = new Dictionary<uint, (DateTime, Dictionary<int, Use>)>();

		public static int Steps(int item)
		{
			foreach ((int i, string _, int steps) in Grades)
			{
				if (i == item)
				{
					return steps;
				}
			}
			return 0;
		}

		// ma9_itemlist rows (ItemCatalog): MANAGE_PLAYER with conditionEffect and hour, pictures cond_1..3
		// (tools/scripts/make_shop_images.py).
		public static IEnumerable<ItemDef> Items()
		{
			return Grades.Select(g => new ItemDef
			{
				Index = g.item, Name = $"{g.grade} 컨디션 회복제", Type = ItemType.ManagePlayer, TypeText = "선수관리 아이템", File = $"cond_{g.steps}",
				ConditionSteps = g.steps, Hours = Hours,
				Desc = $"선수 한 명의 컨디션을 {g.steps}칸 올립니다(최상까지). 쓴 때부터 24시간(리그 72경기) 동안 적용됩니다. 팀 로스터 > 선수관리에서 사용하고, 동시에 5명까지 쓸 수 있습니다."
			});
		}

		public static void Register(GameLoop loop, Database db, string dbPath)
		{
			s_db = db;
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS card_manage (
					user_code INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					acquired TEXT NOT NULL,
					item INTEGER NOT NULL,
					applied TEXT NOT NULL,
					PRIMARY KEY (user_code, card_idx))";
				cmd.ExecuteNonQuery();
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_PLAYER_MANAGE_ENDTIME>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_ITEM_TO_PLAYER_ENDTIME>(Apply);
			loop.Register<Ma9MPacket.PKS_CS_TAKE_OFF_ITEM_ENDTIME>(TakeOff);
		}

		// Condition steps the card's 회복제 gives at that time (0 = none); Condition.For takes them off.
		public static int Effect(uint user, int idx, DateTime at)
		{
			if (s_cs == null)
			{
				return 0;
			}
			return Uses(user).TryGetValue(idx, out Use use) && use.ActiveAt(at) ? Steps(use.Item) : 0;
		}

		private static Dictionary<int, Use> Uses(uint user)
		{
			lock (s_cache)
			{
				if (!s_cache.TryGetValue(user, out var entry) || (DateTime.Now - entry.loaded).TotalMinutes > 1)
				{
					entry = (DateTime.Now, Load(user));
					s_cache[user] = entry;
				}
				return entry.cards;
			}
		}

		private static Dictionary<int, Use> Load(uint user)
		{
			Dictionary<int, Use> cards = new Dictionary<int, Use>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			// Only the copy the item was used on.
			cmd.CommandText = @"SELECT m.card_idx, m.item, m.applied FROM card_manage m
				JOIN team_card t ON t.user_code = m.user_code AND t.card_idx = m.card_idx AND t.acquired = m.acquired
				WHERE m.user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				cards[r.GetInt32(0)] = new Use { Item = r.GetInt32(1), At = DateTime.Parse(r.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) };
			}
			return cards;
		}

		private static void Invalidate(uint user)
		{
			lock (s_cache)
			{
				s_cache.Remove(user);
			}
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// The players with an item now, the oldest first (the screen's slots). Packets serialize the field
		// objects bound in their constructors, so callers fill reply.apply_players.array with this.
		private static PUICardInfoPlayerManage[] Applied(uint user, List<TeamCard> cards, DateTime now)
		{
			Dictionary<int, Use> uses = Uses(user);
			return cards.Where(c => uses.TryGetValue(c.Idx, out Use u) && u.ActiveAt(now)).OrderBy(c => uses[c.Idx].At)
				.Select(c => Row(user, c, uses[c.Idx], now)).ToArray();
		}

		private static PUICardInfoPlayerManage Row(uint user, TeamCard card, Use use, DateTime now)
		{
			PUICardInfoPlayerManage row = new PUICardInfoPlayerManage();
			row.ui_info = new PUICardInfo(card.Idx, Condition.For(user, card.Idx, now), card.RemainContract, card.PositionTrainingInfo, (byte)(card.IsLock ? 2 : 1));
			row.players_condition_itemoff.n = Condition.Base(user, card.Idx, now);
			row.apply_item.n = use.Item;
			// "{0}시간 남음": whole hours, rounded up (1 in the last hour).
			row.apply_time.n = (short)Math.Clamp((int)Math.Ceiling((use.Until - now).TotalHours), 1, Hours);
			return row;
		}

		// The screen's stat column: the card table's stats plus these (육성, 계약, 트레이닝, 팀컬러).
		private static sbyte[] AddStat(TeamCard card)
		{
			sbyte[] add = new sbyte[6];
			if (card != null)
			{
				for (int i = 0; i < add.Length; i++)
				{
					// + 코칭스태프 (systems session 2026-11-14: tester_w found 선구 88 here against 89 in the 선수교체 창 and the game,
					// TeamStrength; a 레어 LG 타격코치's +1 선구 was missing).
					add[i] = (sbyte)Math.Clamp(card.Educated[i] + card.Contract[i] + card.Training[i] + card.TeamColor[i] + (card.Coach?[i] ?? 0), sbyte.MinValue, sbyte.MaxValue);
				}
			}
			return add;
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_PLAYER_MANAGE_ENDTIME packet)
		{
			if (session.Team != null)
			{
				session.Send(EnterReply(session.Team.UserCode, packet.index.n, DateTime.Now));
			}
		}

		internal static Ma9MPacket.PKS_SC_ENTER_PLAYER_MANAGE EnterReply(uint user, int target, DateTime now)
		{
			List<TeamCard> cards = s_db.GetCards(user);
			List<(int item, int amount)> items = Grades.Select(g => (g.item, InventoryHandlers.ItemAmount(user, g.item))).Where(i => i.Item2 > 0).ToList();
			Ma9MPacket.PKS_SC_ENTER_PLAYER_MANAGE reply = new Ma9MPacket.PKS_SC_ENTER_PLAYER_MANAGE();
			reply.item_list.array = items.Select(i => new PInteger { n = i.item }).ToArray();
			reply.amount_list.array = items.Select(i => new PShort { n = (short)Math.Min(short.MaxValue, i.amount) }).ToArray();
			reply.apply_players.array = Applied(user, cards, now);
			reply.max_apply_count.n = Slots;
			reply.target_card_addstat.sbytes = AddStat(cards.Find(c => c.Idx == target));
			return reply;
		}

		private static void Apply(GameSession session, Ma9MPacket.PKS_CS_APPLY_ITEM_TO_PLAYER_ENDTIME packet)
		{
			if (session.Team == null)
			{
				return;
			}
			Ma9MPacket.PKS_SC_APPLY_ITEM_TO_PLAYER reply = ApplyItem(session.Team.UserCode, packet.item_index.n, packet.player_index.n, DateTime.Now);
			ServerLog.Info($"[{session.Id}] 컨디션 회복제 {packet.item_index.n} → card {packet.player_index.n}: result {reply.result.n}");
			session.Send(reply);
		}

		internal static Ma9MPacket.PKS_SC_APPLY_ITEM_TO_PLAYER ApplyItem(uint user, int item, int idx, DateTime now)
		{
			List<TeamCard> cards = s_db.GetCards(user);
			TeamCard card = cards.Find(c => c.Idx == idx);
			Dictionary<int, Use> uses = Uses(user);
			int busy = cards.Count(c => c.Idx != idx && uses.TryGetValue(c.Idx, out Use u) && u.ActiveAt(now));
			Ma9MPacket.PKS_SC_APPLY_ITEM_TO_PLAYER reply = new Ma9MPacket.PKS_SC_APPLY_ITEM_TO_PLAYER();
			if (Steps(item) == 0 || card == null || InventoryHandlers.ItemAmount(user, item) < 1)
			{
				reply.result.n = NoItem;
			}
			else if (card.RemainContract < MinContractDays)
			{
				reply.result.n = ShortContract;
			}
			else if (busy >= Slots)
			{
				reply.result.n = SlotsFull;
			}
			else
			{
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = @"INSERT INTO card_manage (user_code, card_idx, acquired, item, applied)
						SELECT user_code, card_idx, acquired, $item, $now FROM team_card WHERE user_code = $u AND card_idx = $idx
						ON CONFLICT(user_code, card_idx) DO UPDATE SET acquired = excluded.acquired, item = excluded.item, applied = excluded.applied";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$idx", idx);
					cmd.Parameters.AddWithValue("$item", item);
					cmd.Parameters.AddWithValue("$now", now.ToString("o", CultureInfo.InvariantCulture));
					cmd.ExecuteNonQuery();
				}
				InventoryHandlers.AddItem(user, item, -1);
				Invalidate(user);
				reply.result.n = Ok;
				Missions.Add(user, Missions.ConditionItems);  // 업적
			}
			// On a refusal the list goes out empty: a client without the result check would otherwise count the
			// item as used.
			reply.apply_players.array = reply.result.n == Ok ? Applied(user, cards, now) : Array.Empty<PUICardInfoPlayerManage>();
			reply.target_card_addstat.sbytes = AddStat(card);
			return reply;
		}

		private static void TakeOff(GameSession session, Ma9MPacket.PKS_CS_TAKE_OFF_ITEM_ENDTIME packet)
		{
			if (session.Team == null)
			{
				return;
			}
			Ma9MPacket.PKS_SC_TAKE_OFF_ITEM reply = TakeOffItem(session.Team.UserCode, packet.take_off_player_index.n, packet.target_card_index.n, DateTime.Now);
			ServerLog.Info($"[{session.Id}] 컨디션 회복제 적용취소 card {packet.take_off_player_index.n}: result {reply.result.n}");
			session.Send(reply);
		}

		// Ends the card's item (also one whose 24 hours are over: the list comes back without it either way).
		internal static Ma9MPacket.PKS_SC_TAKE_OFF_ITEM TakeOffItem(uint user, int idx, int target, DateTime now)
		{
			List<TeamCard> cards = s_db.GetCards(user);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "DELETE FROM card_manage WHERE user_code = $u AND card_idx = $idx";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$idx", idx);
				cmd.ExecuteNonQuery();
			}
			Invalidate(user);
			Ma9MPacket.PKS_SC_TAKE_OFF_ITEM reply = new Ma9MPacket.PKS_SC_TAKE_OFF_ITEM();
			reply.result.n = cards.Exists(c => c.Idx == idx) ? Ok : NoItem;
			reply.apply_players.array = Applied(user, cards, now);
			reply.take_off_player_index.n = idx;
			reply.take_off_player_condition.n = Condition.For(user, idx, now);
			reply.target_card_addstat.sbytes = AddStat(cards.Find(c => c.Idx == target));
			return reply;
		}

		// Dev check: `Ma9MServer.dll <copy of data dir> --managetest`. Uses the test account's team
		// (autotest@ma9m.local) and writes to that directory's DB (items, card_manage, a card released and
		// drawn again), so never run it on the live data directory.
		public static int SelfTest(Database db, CardCatalog catalog)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok)
				{
					problems++;
				}
				ServerLog.Info($"[managetest] {(ok ? "ok  " : "FAIL")} {what}");
			}

			Check(Grades.All(g => ItemCatalog.Get(g.item) is ItemDef d && d.Type == ItemType.ManagePlayer && d.ConditionSteps == g.steps && d.Hours == 24),
				"초급/중급/고급 in the item table: MANAGE_PLAYER, +1/+2/+3, 24 hours");
			uint user;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
				cmd.CommandText = "DELETE FROM card_manage WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.ExecuteNonQuery();
				// 7-day contracts (2026-10-24) run out within the week: give the test team's cards a fresh one.
				cmd.CommandText = "UPDATE team_card SET contract_until = $d WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$d", DateTime.Today.AddDays(ContractHandlers.StartDays).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
				cmd.ExecuteNonQuery();
			}
			Invalidate(user);
			ContractHandlers.Invalidate(user);
			List<TeamCard> cards = db.GetCards(user).Where(c => c.RemainContract >= MinContractDays).ToList();
			if (user == 0 || cards.Count < Slots + 5)
			{
				ServerLog.Info($"[managetest] the test team needs {Slots + 5} cards with {MinContractDays}+ contract days (team {user}, {cards.Count})");
				return 1;
			}
			foreach ((int item, string _, int _) in Grades)
			{
				InventoryHandlers.AddItem(user, item, 10 - InventoryHandlers.ItemAmount(user, item));
			}
			DateTime now = DateTime.Now;
			int a = cards[0].Idx;
			byte baseA = Condition.Base(user, a, now);

			// One player: +1 for 24 hours, the item used up, the list and the remaining hours.
			var r1 = ApplyItem(user, Basic, a, now);
			Check(r1.result.n == Ok && r1.apply_players.array.Length == 1 && r1.apply_players.array[0].apply_time.n == 24 && InventoryHandlers.ItemAmount(user, Basic) == 9,
				$"초급 on card {a}: ok, listed with 24시간, 9 left");
			Check(Condition.For(user, a, now) == Math.Max(0, baseA - 1) && Condition.Base(user, a, now) == baseA && r1.apply_players.array[0].players_condition_itemoff.n == baseA,
				$"condition {baseA} → {Condition.For(user, a, now)} (미적용 {baseA})");
			Check(Effect(user, a, now.AddHours(23.99)) == 1 && Effect(user, a, now.AddHours(24)) == 0 && Effect(user, a, now.AddMinutes(-1)) == 0,
				"lasts 24 hours from its use (72 league games), not before or after");
			Check(Row(user, cards[0], Uses(user)[a], now.AddHours(23.5)).apply_time.n == 1 && Row(user, cards[0], Uses(user)[a], now.AddMinutes(30)).apply_time.n == 24,
				"남은 시간 rounds up (24 after half an hour, 1 in the last hour)");

			// Five slots: the sixth player is refused and keeps the item.
			for (int k = 1; k < Slots; k++)
			{
				ApplyItem(user, Basic, cards[k].Idx, now);
			}
			var r6 = ApplyItem(user, Basic, cards[Slots].Idx, now);
			Check(r6.result.n == SlotsFull && (r6.apply_players.array?.Length ?? 0) == 0 && InventoryHandlers.ItemAmount(user, Basic) == 5 && Effect(user, cards[Slots].Idx, now) == 0,
				"6th player refused (slots full), item kept, empty list for old clients");

			// Replacing on a player in a slot: no new slot, the new steps, its 24 hours start again.
			DateTime later = now.AddHours(5);
			var rr = ApplyItem(user, Middle, a, later);
			Check(rr.result.n == Ok && rr.apply_players.array.Length == Slots && Effect(user, a, later) == 2 && Effect(user, a, now.AddHours(28)) == 2 && Effect(user, a, later.AddHours(24)) == 0,
				"replace 초급 → 중급 on the same player: still 5, +2 until 24 hours after the replacement");
			Check(Condition.For(user, a, later) == Math.Max(0, Condition.Base(user, a, later) - 2), "중급 takes two steps off (never past 최상)");

			// 적용취소 frees the slot; the card is back to its own condition.
			int b = cards[1].Idx;
			var rt = TakeOffItem(user, b, a, later);
			Check(rt.result.n == Ok && rt.apply_players.array.Length == Slots - 1 && Effect(user, b, later) == 0 && rt.take_off_player_condition.n == Condition.Base(user, b, later),
				$"적용취소 on card {b}: 4 left, condition back to {rt.take_off_player_condition.n}");
			Check(ApplyItem(user, Basic, cards[Slots].Idx, later).result.n == Ok, "the freed slot takes another player");

			// 고급 clamps at 최상; no item left; an expired window frees its slot.
			int c7 = cards[Slots + 1].Idx;
			TakeOffItem(user, cards[2].Idx, a, later);
			ApplyItem(user, Advanced, c7, later);
			Check(Condition.For(user, c7, later) == Math.Max(0, Condition.Base(user, c7, later) - 3) && Condition.For(user, c7, later) <= 1, "고급 takes three steps off, never past 최상");
			InventoryHandlers.AddItem(user, Advanced, -InventoryHandlers.ItemAmount(user, Advanced));
			Check(ApplyItem(user, Advanced, cards[2].Idx, later).result.n == NoItem, "no 고급 left: refused");
			Check(ApplyItem(user, Basic, cards[Slots + 2].Idx, now.AddHours(24)).result.n == Ok, "a day later the old slots are free again");

			// Contract: an expired card (0 days) refused, 1 day taken (two cards' contracts cut to 0 and 1 days in this copy).
			void CutContract(int idx, int days)
			{
				using SqliteConnection c = Open();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "UPDATE team_card SET contract_until = $until WHERE user_code = $u AND card_idx = $idx";
				cmd.Parameters.AddWithValue("$until", DateTime.Now.Date.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$idx", idx);
				cmd.ExecuteNonQuery();
			}
			int shortIdx = cards[Slots + 3].Idx, edgeIdx = cards[Slots + 4].Idx;
			CutContract(shortIdx, MinContractDays - 1);
			CutContract(edgeIdx, MinContractDays);
			byte shortDays = db.GetCards(user).First(c => c.Idx == shortIdx).RemainContract, edgeDays = db.GetCards(user).First(c => c.Idx == edgeIdx).RemainContract;
			Check(ApplyItem(user, Basic, shortIdx, now.AddHours(30)).result.n == ShortContract, $"card {shortIdx} with {shortDays} contract day(s) refused");
			Check(ApplyItem(user, Basic, edgeIdx, now.AddHours(30)).result.n == Ok, $"card {edgeIdx} with {edgeDays} contract days taken");

			// A card released and drawn again starts without the item.
			int d = cards[3].Idx;
			Check(Effect(user, d, now) == 1, $"card {d} has 초급 before the release");
			db.RemoveCards(user, new[] { d });
			db.AddCards(user, new[] { d }, RosterHandlers.StartContract);
			Invalidate(user);
			Check(Effect(user, d, now) == 0, "the card drawn again has no item");

			// The screen's packets as the client reads them.
			T Wire<T>(T sent) where T : NetworkLibrary.Packet.PKS_PACKET, new()
			{
				byte[] bytes = new byte[sent.GetSize() + 64];
				sent.ToBytes(bytes, 0);
				T back = new T();
				back.ToType(bytes, 0);
				return back;
			}
			var gotApply = Wire(r1);
			Check(gotApply.result.n == Ok && gotApply.apply_players.array?.Length == 1 && gotApply.apply_players.array[0].ui_info.index.n == a
				&& gotApply.apply_players.array[0].apply_item.n == Basic && gotApply.target_card_addstat.sbytes?.Length == 6, "적용 packet round trip");
			var gotTakeOff = Wire(rt);
			Check(gotTakeOff.result.n == Ok && gotTakeOff.apply_players.array?.Length == Slots - 1 && gotTakeOff.take_off_player_index.n == b
				&& gotTakeOff.take_off_player_condition.n == rt.take_off_player_condition.n, "적용취소 packet round trip");
			var enter = EnterReply(user, a, later);
			var got = Wire(enter);
			Check(got.max_apply_count.n == Slots && got.item_list.array != null && got.item_list.array.Select(i => i.n).SequenceEqual(new[] { Basic, Middle })
				&& got.apply_players.array != null && got.apply_players.array.Length == enter.apply_players.array.Length
				&& got.apply_players.array.All(p => p.apply_time.n >= 1 && p.apply_time.n <= 24 && Steps(p.apply_item.n) > 0)
				&& got.target_card_addstat.sbytes?.Length == 6,
				$"입장 packet round trip: items {string.Join(",", got.item_list.array?.Select(i => i.n) ?? Array.Empty<int>())}, {got.apply_players.array?.Length ?? 0} players");
			ServerLog.Info($"[managetest] team {user}: {string.Join(", ", enter.apply_players.array.Select(p => $"{catalog.Get(p.ui_info.index.n)?.Name} {p.players_condition_itemoff.n}→{p.ui_info.condition.n} ({Steps(p.apply_item.n)}칸, {p.apply_time.n}h)"))}");
			ServerLog.Info($"[managetest] {(problems == 0 ? "all ok" : problems + " problem(s)")}");
			return problems == 0 ? 0 : 1;
		}
	}
}
