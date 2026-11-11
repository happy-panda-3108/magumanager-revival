using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// Dev check of the training rules and slot storage: `Ma9MServer.dll <copy of data dir> --trainingtest`.
	// It writes to that directory's ma9m.db (trains, releases and re-adds a card), so never run it on
	// the live data directory.
	public static class TrainingSelfTest
	{
		public static int Run(Database db, CardCatalog catalog, string dbPath)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok)
				{
					problems++;
				}
				ServerLog.Info($"[trainingtest] {(ok ? "ok  " : "FAIL")} {what}");
			}

			// Odds: grades 50/30/20, stats evenly, result squares evenly.
			Random rng = new Random(7);
			const int Rolls = 240000;
			int[] grades = new int[4], stats = new int[13], squares = new int[4];
			for (int n = 0; n < Rolls; n++)
			{
				int book = TrainingBooks.RollBook(rng);
				grades[TrainingBooks.Grade(book)]++;
				stats[TrainingBooks.Stat(book)]++;
				squares[TrainingBooks.RollResult(rng)]++;
			}
			ServerLog.Info($"[trainingtest] grades I {100.0 * grades[1] / Rolls:F1}% II {100.0 * grades[2] / Rolls:F1}% III {100.0 * grades[3] / Rolls:F1}%");
			Check(Math.Abs(grades[1] / (double)Rolls - 0.5) < 0.005 && Math.Abs(grades[2] / (double)Rolls - 0.3) < 0.005 && Math.Abs(grades[3] / (double)Rolls - 0.2) < 0.005, "book grades 50/30/20");
			Check(stats.Skip(1).All(s => Math.Abs(s / (double)Rolls - 1 / 12.0) < 0.003), "12 stats evenly");
			Check(squares.All(s => Math.Abs(s / (double)Rolls - 0.25) < 0.005), "4 results evenly");
			Check(Enumerable.Range(1, 3).All(g => Enumerable.Range(0, 4).Select(r => TrainingBooks.Value(TrainingBooks.Book(1, g), r)).SequenceEqual(Enumerable.Range(g - 2, 4))),
				"values I -1..+2, II 0..+3, III +1..+4");
			Check(ItemCatalog.Items.Count(i => i.Type == ItemType.Training) == 12 * TrainingBooks.TopGrade && ItemCatalog.Get(TrainingBooks.Pack) != null,  // IV with 코칭스태프
				$"{12 * TrainingBooks.TopGrade} books and the pack in the item table");
			Check(TrainingBooks.StatSlot(TrainingBooks.Book(4, 1)) == 3 && TrainingBooks.StatSlot(TrainingBooks.Book(7, 1)) == 0 && TrainingBooks.StatSlot(TrainingBooks.Book(12, 3)) == 5,
				"stat slots (선구 → 3, 체력 → 0, 투수 정신 → 5)");
			// Inventory order (2026-10-26): the MP주머니, the other items by number, the books last with III first.
			int[] order = InventoryHandlers.InventoryOrder(new[] { TrainingBooks.Book(1, 1), ShopHandlers.LuckyPouch, 30101, TrainingBooks.Book(12, 3), TrainingBooks.Pack, TrainingBooks.Book(2, 3) }
				.Select(i => (i, 1))).Select(i => i.item).ToArray();
			Check(order.SequenceEqual(new[] { ShopHandlers.LuckyPouch, TrainingBooks.Pack, 30101, TrainingBooks.Book(2, 3), TrainingBooks.Book(12, 3), TrainingBooks.Book(1, 1) }),
				$"inventory order: MP주머니, items, books (III first) — {string.Join(",", order)}");

			// Slots: train, overwrite, fill all three, and a card drawn again starts empty.
			TrainingStore store = new TrainingStore(dbPath);
			uint user = db.AllTeamCodes().FirstOrDefault(u => db.GetCards(u).Any(c => catalog.Get(c.Idx)?.IsBatter == true));
			if (user == 0)
			{
				ServerLog.Info("[trainingtest] no team with a batter card");
				return 1;
			}
			TeamCard card = db.GetCards(user).First(c => catalog.Get(c.Idx)?.IsBatter == true);
			int idx = card.Idx;
			sbyte[] before = card.Training;
			int contact3 = TrainingBooks.Book(1, 3), power1 = TrainingBooks.Book(2, 1), eye2 = TrainingBooks.Book(4, 2);
			store.Apply(user, idx, 0, contact3, 3, out sbyte[] t1);
			Check(t1.SequenceEqual(new sbyte[] { 4, 0, 0, 0, 0, 0 }), $"slot 1 컨택 III square 4 → [{string.Join(" ", t1)}]");
			store.Apply(user, idx, 0, power1, 0, out sbyte[] t2);
			Check(t2.SequenceEqual(new sbyte[] { 0, -1, 0, 0, 0, 0 }), $"slot 1 overwritten by 파워 I square 1 → [{string.Join(" ", t2)}]");
			store.Apply(user, idx, 1, contact3, 0, out _);
			(int item, int result)[] slots = store.Apply(user, idx, 2, eye2, 2, out sbyte[] t3);
			Check(t3.SequenceEqual(new sbyte[] { 1, -1, 0, 2, 0, 0 }), $"three slots → [{string.Join(" ", t3)}]");
			Check(slots.Select(s => s.item).SequenceEqual(new[] { power1, contact3, eye2 }) && slots.Select(s => s.result).SequenceEqual(new[] { 0, 0, 2 }), "slots read back");
			Check(db.GetCards(user).First(c => c.Idx == idx).Training.SequenceEqual(t3), "team_card.training follows the slots");
			db.RemoveCards(user, new[] { idx });
			db.AddCards(user, new[] { idx }, RosterHandlers.StartContract);
			Check(store.Get(user, idx).All(s => s.item == -1), "a card drawn again starts with empty slots");
			store.Apply(user, idx, 1, eye2, 1, out sbyte[] t4);
			Check(t4.SequenceEqual(new sbyte[] { 0, 0, 0, 1, 0, 0 }) && store.Get(user, idx).Count(s => s.item != -1) == 1, $"old slots do not come back → [{string.Join(" ", t4)}]");

			// TRAINING POINT (user, 2026-10-21): six charges, one per try; the 충전권 fills them; a copy drawn
			// again starts full.
			GameLoop loop = new GameLoop();
			InventoryHandlers.Register(loop, db, catalog, System.IO.Path.GetDirectoryName(dbPath));
			TrainingHandlers.Register(loop, db, catalog, dbPath);
			store.RefillTp(user, idx);
			List<int> lefts = Enumerable.Range(0, 7).Select(_ => store.UseTp(user, idx)).ToList();
			Check(lefts.SequenceEqual(new[] { 5, 4, 3, 2, 1, 0, 0 }) && store.TpLeft(user, idx) == 0, $"six charges, one per try: {string.Join(" ", lefts)}");
			InventoryHandlers.AddItem(user, TrainingHandlers.TpCharge, -InventoryHandlers.ItemAmount(user, TrainingHandlers.TpCharge));
			string none = TrainingHandlers.TryCharge(user, idx, TrainingHandlers.TpCharge, out _);
			InventoryHandlers.AddItem(user, TrainingHandlers.TpCharge, 2);
			string ok = TrainingHandlers.TryCharge(user, idx, TrainingHandlers.TpCharge, out short tp);
			string full = TrainingHandlers.TryCharge(user, idx, TrainingHandlers.TpCharge, out _);
			int ball = catalog.Get(idx).Ball;
			Check(none != null && ok == null && full != null && store.TpLeft(user, idx) == TrainingStore.TpCharges && tp == TrainingStore.TpCharges * TrainingHandlers.TpStep(ball)
				&& InventoryHandlers.ItemAmount(user, TrainingHandlers.TpCharge) == 1, $"충전권: without one '{none}', then ok (TP {tp}, {ball}볼), when full '{full}'");
			// The 아이템 사용 list as the client reads it: with no ticket left it still carries the 충전권 at 0 (an empty
			// array would arrive as null and the client would keep showing x1).
			InventoryHandlers.AddItem(user, TrainingHandlers.TpCharge, -InventoryHandlers.ItemAmount(user, TrainingHandlers.TpCharge));
			Ma9MPackets.Ma9MPacket.PKS_SC_USE_TRAINING_BOOSTER sent = new Ma9MPackets.Ma9MPacket.PKS_SC_USE_TRAINING_BOOSTER();
			sent.result.n = 0;
			TrainingHandlers.FillBoosters(user, sent.training_booster_list, sent.training_booster_amount);
			byte[] wire = new byte[sent.GetSize() + 64];
			int at = 0;
			sent.training_booster_list.ToBytes(wire, ref at);
			sent.training_booster_amount.ToBytes(wire, ref at);
			NetworkLibrary.PacketType.PArray<NetworkLibrary.PacketType.PInteger> gotList = new NetworkLibrary.PacketType.PArray<NetworkLibrary.PacketType.PInteger>();
			NetworkLibrary.PacketType.PArray<NetworkLibrary.PacketType.PShort> gotAmounts = new NetworkLibrary.PacketType.PArray<NetworkLibrary.PacketType.PShort>();
			at = 0;
			gotList.ToType(wire, ref at);
			gotAmounts.ToType(wire, ref at);
			Check(gotList.array != null && gotList.array.Length == 1 && gotList.array[0].n == TrainingHandlers.TpCharge && gotAmounts.array != null && gotAmounts.array[0].n == 0,
				"no ticket left: the list still arrives, 충전권 x0 (the popup hides 0)");
			store.UseTp(user, idx);
			db.RemoveCards(user, new[] { idx });
			db.AddCards(user, new[] { idx }, RosterHandlers.StartContract);
			Check(store.TpLeft(user, idx) == TrainingStore.TpCharges, "a card drawn again starts with six charges");
			Check(ItemCatalog.Get(TrainingHandlers.TpCharge)?.Type == ItemType.TrainingBooster, "충전권 in the item table");
			ServerLog.Info($"[trainingtest] card {idx} ({catalog.Get(idx).Year} {catalog.Get(idx).Name}) of team {user}; training before the test [{string.Join(" ", before)}]");
			ServerLog.Info($"[trainingtest] {(problems == 0 ? "all ok" : problems + " problem(s)")}");
			// 트레이닝북 조합 (2026-10-24): four books of one grade → one of the next grade; mixed grades, III, three books
			// and books the team lacks are refused and change nothing.
			int c1 = TrainingBooks.Book(1, 1), p1 = TrainingBooks.Book(8, 1), e2 = TrainingBooks.Book(4, 2), s3 = TrainingBooks.Book(11, 3);
			foreach (int b in new[] { c1, p1, e2, s3 })
			{
				InventoryHandlers.AddItem(user, b, 4 - InventoryHandlers.ItemAmount(user, b));
			}
			string mixed = TrainingHandlers.TryMix(user, new[] { c1, c1, c1, e2 }, out _);
			string three = TrainingHandlers.TryMix(user, new[] { c1, c1, c1 }, out _);
			string top = TrainingHandlers.TryMix(user, new[] { s3, s3, s3, s3 }, out _);
			int booksBefore = InventoryHandlers.ItemAmount(user, c1) + InventoryHandlers.ItemAmount(user, p1);
			string okMix = TrainingHandlers.TryMix(user, new[] { c1, c1, p1, p1 }, out int made);
			Check(mixed != null && three != null && top != null && okMix == null && TrainingBooks.Grade(made) == 2
				&& InventoryHandlers.ItemAmount(user, c1) + InventoryHandlers.ItemAmount(user, p1) == booksBefore - 4 && InventoryHandlers.ItemAmount(user, made) >= 1,
				$"트레이닝북 조합: I 4권 → {TrainingBooks.Name(made)}; 등급 섞임 '{mixed}', 3권 '{three}', III '{top}'");
			// Two 컨택 I are left: four are refused; four 선구 II make a III.
			string shortOf = TrainingHandlers.TryMix(user, new[] { c1, c1, c1, c1 }, out _);
			string okII = TrainingHandlers.TryMix(user, new[] { e2, e2, e2, e2 }, out int madeII);
			Check(shortOf != null && okII == null && TrainingBooks.Grade(madeII) == 3, $"II 4권 → {TrainingBooks.Name(madeII)}; 모자랄 때 '{shortOf}'");
			// 트레이닝북 교환소 (2026-10-25): one III book for the III book of another stat; II books, the same book and books
			// the team lacks are refused and change nothing.
			int k3 = TrainingBooks.Book(1, 3), want = TrainingBooks.Book(10, 3);
			InventoryHandlers.AddItem(user, s3, 1 - InventoryHandlers.ItemAmount(user, s3));
			InventoryHandlers.AddItem(user, k3, -InventoryHandlers.ItemAmount(user, k3));
			int wantBefore = InventoryHandlers.ItemAmount(user, want);
			string fromII = TrainingHandlers.TryExchange(user, e2, want, out _);
			string same = TrainingHandlers.TryExchange(user, s3, s3, out _);
			string lacking = TrainingHandlers.TryExchange(user, k3, want, out _);
			string okSwap = TrainingHandlers.TryExchange(user, s3, want, out int swapped);
			Check(fromII != null && same != null && lacking != null && okSwap == null && swapped == want
				&& InventoryHandlers.ItemAmount(user, s3) == 0 && InventoryHandlers.ItemAmount(user, want) == wantBefore + 1,
				$"트레이닝북 교환: {TrainingBooks.Name(s3)} → {TrainingBooks.Name(swapped)}; II '{fromII}', 같은 책 '{same}', 없는 책 '{lacking}'");
			// IV books (코칭스태프 on, user 11-10 02:2x: "4단계북 끼리도 교환소를 사용할 수 있도록"): a IV for the IV of another stat;
			// a IV for a III and a III for a IV are refused.
			if (TrainingBooks.TopGrade == 4)
			{
				int c4 = TrainingBooks.Book(1, 4), p4 = TrainingBooks.Book(7, 4), c3 = TrainingBooks.Book(1, 3);
				InventoryHandlers.AddItem(user, c4, 1 - InventoryHandlers.ItemAmount(user, c4));
				InventoryHandlers.AddItem(user, c3, 1 - InventoryHandlers.ItemAmount(user, c3));
				int p4Before = InventoryHandlers.ItemAmount(user, p4), c3Before = InventoryHandlers.ItemAmount(user, c3);
				string ivToIii = TrainingHandlers.TryExchange(user, c4, want, out _);
				string iiiToIv = TrainingHandlers.TryExchange(user, c3, p4, out _);
				string okIv = TrainingHandlers.TryExchange(user, c4, p4, out int swappedIv);
				Check(ivToIii != null && iiiToIv != null && okIv == null && swappedIv == p4 && InventoryHandlers.ItemAmount(user, c4) == 0
					&& InventoryHandlers.ItemAmount(user, p4) == p4Before + 1 && InventoryHandlers.ItemAmount(user, c3) == c3Before,
					$"트레이닝북 교환 IV: {TrainingBooks.Name(c4)} → {TrainingBooks.Name(swappedIv)}; IV → III '{ivToIii}', III → IV '{iiiToIv}'");
			}
			// 빠른 조합 (2026-11-11, TrainingQuick.cs): 13 I and 3 II, up to III → I 12 → II 3, II 6 → III 1 (I 1, II 2 left).
			void Clear(int grade)
			{
				for (int s = 1; s <= 12; s++)
				{
					int b = TrainingBooks.Book(s, grade);
					InventoryHandlers.AddItem(user, b, -InventoryHandlers.ItemAmount(user, b));
				}
			}
			int Total(int grade) => Enumerable.Range(1, 12).Sum(s => InventoryHandlers.ItemAmount(user, TrainingBooks.Book(s, grade)));
			for (int g = 1; g <= TrainingBooks.TopGrade; g++)
			{
				Clear(g);
			}
			InventoryHandlers.AddItem(user, TrainingBooks.Book(1, 1), 9);
			InventoryHandlers.AddItem(user, TrainingBooks.Book(2, 1), 4);
			InventoryHandlers.AddItem(user, TrainingBooks.Book(4, 2), 3);
			string badStop = TrainingHandlers.TryQuickMix(user, 1, out _, out _);
			string quick = TrainingHandlers.TryQuickMix(user, 3, out int quickMade, out string quickLog);
			Check(badStop != null && quick == null && quickMade == 1 && Total(1) == 1 && Total(2) == 2 && Total(3) == 1,
				$"빠른 조합 III까지: made {quickMade}, I {Total(1)} · II {Total(2)} · III {Total(3)} ({quickLog}); 등급 1 '{badStop}'");
			string nothing = TrainingHandlers.TryQuickMix(user, 3, out _, out _);
			// IV: refused without a 트레이닝코치, and with one too (III 1 left, a IV takes at least 2).
			string toIv = TrainingHandlers.TryQuickMix(user, 4, out _, out _);
			Check(nothing != null && toIv != null, $"책이 모자랄 때 '{nothing}'; IV까지 ({(TrainingHandlers.QuickTop(user) == 4 ? "트레이닝코치 있음" : "트레이닝코치 없음")}) '{toIv}'");
			// 목표 교환: III 컨택 5 · 선구 2 · 구위 3, targets 컨택 2 · 파워 3 · 선구 2 → the 구위 (target 0) books go to 파워 first;
			// 컨택's surplus stays.
			Clear(3);
			InventoryHandlers.AddItem(user, TrainingBooks.Book(1, 3), 5);
			InventoryHandlers.AddItem(user, TrainingBooks.Book(4, 3), 2);
			InventoryHandlers.AddItem(user, TrainingBooks.Book(11, 3), 3);
			int[] targets = new int[12];
			targets[0] = 2;
			targets[1] = 3;
			targets[3] = 2;
			string zero = TrainingHandlers.TryTargetExchange(user, 3, new int[12], out _, out _);
			string shortList = TrainingHandlers.TryTargetExchange(user, 3, new int[11], out _, out _);
			string gradeII = TrainingHandlers.TryTargetExchange(user, 2, targets, out _, out _);
			string swap = TrainingHandlers.TryTargetExchange(user, 3, targets, out int moved, out string swapLog);
			int[] now = Enumerable.Range(1, 12).Select(s => InventoryHandlers.ItemAmount(user, TrainingBooks.Book(s, 3))).ToArray();
			Check(zero != null && shortList != null && gradeII != null && swap == null && moved == 3 && now[0] == 5 && now[1] == 3 && now[3] == 2 && now[10] == 0 && now.Sum() == 10,
				$"목표 교환 III: {moved}권 ({swapLog}); 목표 0 '{zero}', 11칸 '{shortList}', II '{gradeII}'");
			targets[2] = 4;
			string more = TrainingHandlers.TryTargetExchange(user, 3, targets, out int moved2, out string moreLog);
			now = Enumerable.Range(1, 12).Select(s => InventoryHandlers.ItemAmount(user, TrainingBooks.Book(s, 3))).ToArray();
			string met = TrainingHandlers.TryTargetExchange(user, 3, new[] { 2, 3, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0 }, out _, out _);
			Check(more == null && moved2 == 3 && now[0] == 2 && now[2] == 3 && met != null,
				$"목표 교환 모자랄 때: {moved2}권 ({moreLog}); 다 채웠을 때 '{met}'");
			Clear(3);
			Clear(1);
			Clear(2);
			return problems == 0 ? 0 : 1;
		}
	}
}
