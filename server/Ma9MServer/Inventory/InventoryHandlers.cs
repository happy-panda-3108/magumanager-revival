using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// One coupon: what it gives and how often it can be used. Every coupon is once per account (user,
	// 2026-10-19: e.g. one per account for building single-season teams); MaxUses caps all accounts together.
	public sealed class CouponDef
	{
		public string Code;

		public int Mp;

		public (int item, int count)[] Items = Array.Empty<(int, int)>();

		public int MaxUses = int.MaxValue;

		public DateTime? Expires;

		// Only for the automated test accounts (autotest@...), so tests never use up a real coupon.
		public bool TesterOnly;
	}

	// Inventory (Scene_Inventory), card packs and coupons (lobby COUPON button).
	public static class InventoryHandlers
	{
		// Codes are matched exactly; the client says they are case-sensitive and strips everything
		// but letters and digits.
		public static readonly CouponDef[] Coupons =
		{
			// User's request (2026-10-18): 1억 MP and 100 of each player pack (once per account since 2026-10-19).
			new CouponDef
			{
				Code = "MAGU2026", Mp = 100_000_000,
				Items = new[] { (ItemCatalog.AllPlayersPack, 100), (ItemCatalog.HighPlayersPack, 100) }
			},
			// The same gift for the test accounts.
			new CouponDef
			{
				Code = "TESTGIFT", Mp = 100_000_000, TesterOnly = true,
				Items = new[] { (ItemCatalog.AllPlayersPack, 100), (ItemCatalog.HighPlayersPack, 100) }
			},
			// Training packs to try the training system with (2026-10-19), until the shop sells them.
			new CouponDef
			{
				Code = "TRAIN2026",
				Items = new[] { (TrainingBooks.Pack, 100) }
			},
			new CouponDef
			{
				Code = "TESTTRAIN", TesterOnly = true,
				Items = new[] { (TrainingBooks.Pack, 100) }
			},
			// The starter coupon for everyone who begins the game, kept after testing (user, 2026-10-21 as a small start;
			// 2026-10-24: "이 게임을 시작할 사람들을 위한 최초 보조 쿠폰 … 테스트 끝내도 유지"): 2천만 MP, 10 초급 컨디션
			// 회복제, 2 로스터 확장권, 50 전체 선수팩 and 100 트레이닝 카드팩, once per account.
			new CouponDef
			{
				Code = "START2026", Mp = 20_000_000,
				Items = new[] { (PlayerManageHandlers.Basic, 10), (ShopHandlers.RosterTicket, 2), (ItemCatalog.AllPlayersPack, 50), (TrainingBooks.Pack, 100) }
			},
			// The second starter coupon (user, 2026-10-25: "단일년도 팀덱을 만드는게 난이도가 진짜 뒤지게 높아 … 초보 지원금을 엄청
			// 뿌려야해 … 2차 쿠폰을 생성해서 선수단 확장권 5개, 3천만 MP, 트레이닝카드팩 200장"), once per account.
			new CouponDef
			{
				Code = "SUPPORT2026", Mp = 30_000_000,
				Items = new[] { (ShopHandlers.RosterTicket, 5), (TrainingBooks.Pack, 200) }
			},
			// The coaching-staff coupon (user, 2026-11-08: "테스트 팀들 MP를 많이 쓰고 있는데도 코치진 구성이 다 완료되지 않아서
			// 트레이닝을 못하고 있음 … 쿠폰을 하나 추가해서 … 1억 MP 및 코치 카드팩 200장"), once per account. The packs open in the
			// inventory; a team holds at most 100 coaches, so the rest wait for 조합.
			new CouponDef
			{
				Code = "COACH2026", Mp = 100_000_000,
				// + 1,000 레전드 선수팩 (user, 2026-11-08: "카드팩 준비가 완료되면, 이전에 1억 MP + 코치카드 200장 있는 쿠폰에 레전드 선수팩 1,000장을
				// 추가"); accounts that used it before get them once (TopUpCoachCoupon).
				Items = new[] { (CoachCatalog.Pack, 200), (ItemCatalog.LegendPlayersPack, CoachCouponLegendPacks) }
			},
			// The 특별수강 coupon (user, 2026-11-11 20:3x via the engine session: "육성 테스트 시간을 줄이기 위해 쿠폰을 하나 더 만듭시다.
			// ai테스터들도 사용할 수 있게 해줘. 1억 MP + 특강 행운권 10장 + 특강 즉시 완료권 10장 + 특강 초기화권 10장"), once per account,
			// the user's and the AI testers' accounts alike (not TesterOnly).
			new CouponDef
			{
				Code = "LESSON2026", Mp = 100_000_000,
				Items = new[] { (LessonHandlers.Lucky, 10), (LessonHandlers.Instant, 10), (LessonHandlers.Reset, 10) }
			}
		};

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static InventoryStore s_store;

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_store = new InventoryStore(Path.Combine(dataDir, "ma9m.db"));
			ItemCatalog.WriteTable(Path.Combine(dataDir, "tables"));
			TopUpCoachCoupon(Path.Combine(dataDir, "ma9m.db"));
			loop.Register<Ma9MPacket.PKS_CS_ENTER_INVENTORY>(EnterInventory);
			loop.Register<Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM>(UseCardPack);
			loop.Register<Ma9MPacket.PKS_CS_USE_ITEM>(UseItem);
			loop.Register<Ma9MPacket.PKS_CS_USE_COUPON>(UseCoupon);
		}

		// 레전드 선수팩 in COACH2026 (2026-11-08).
		public const int CoachCouponLegendPacks = 1000;

		// Teams whose account used COACH2026 before it carried the 레전드 선수팩 get them once (league_fix 'coach2026_legend').
		private static void TopUpCoachCoupon(string dbPath)
		{
			using Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
			c.Open();
			using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "CREATE TABLE IF NOT EXISTS league_fix (name TEXT PRIMARY KEY, done TEXT NOT NULL)";
			cmd.ExecuteNonQuery();
			cmd.CommandText = "SELECT COUNT(*) FROM league_fix WHERE name = 'coach2026_legend'";
			if ((long)cmd.ExecuteScalar() > 0) return;
			List<uint> teams = new List<uint>();
			cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'coupon_use'";
			if ((long)cmd.ExecuteScalar() > 0)
			{
				cmd.CommandText = "SELECT DISTINCT user_code FROM coupon_use WHERE code = 'COACH2026'";
				using Microsoft.Data.Sqlite.SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) teams.Add((uint)r.GetInt64(0));
			}
			foreach (uint team in teams) s_store.Add(team, ItemCatalog.LegendPlayersPack, CoachCouponLegendPacks);
			cmd.CommandText = "INSERT INTO league_fix (name, done) VALUES ('coach2026_legend', datetime('now', 'localtime'))";
			cmd.ExecuteNonQuery();
			ServerLog.Info($"COACH2026: {CoachCouponLegendPacks} 레전드 선수팩 each to {teams.Count} team(s) that used it before");
		}

		// For other systems (shop, rewards): gives a team count of an item; returns its new amount.
		public static int AddItem(uint userCode, int item, int count)
		{
			return s_store.Add(userCode, item, count);
		}

		public static int ItemAmount(uint userCode, int item)
		{
			return s_store.Amount(userCode, item);
		}

		// (item, amount) for every item the team has at least one of.
		public static List<(int item, int amount)> Items(uint userCode)
		{
			return s_store.Items(userCode);
		}

		// The inventory's order (user, 2026-10-26: "인벤토리의 MP 주머니가 가장 상위에 있으면 좋겠음 … 트레이닝북이 너무 많아서
		// 아이템칸의 가독성이 너무 떨어짐"): the MP주머니 first, the other items by number, the 36 training books last (III
		// first, then by stat). New clients also show the books on a tab of their own (Inventory_Main.Ma9MOnBtn_BookTab).
		internal static IEnumerable<(int item, int amount)> InventoryOrder(IEnumerable<(int item, int amount)> items)
		{
			return items.OrderBy(i => i.item == ShopHandlers.LuckyPouch ? 0 : TrainingBooks.IsBook(i.item) ? 2 : 1)
				.ThenByDescending(i => TrainingBooks.IsBook(i.item) ? TrainingBooks.Grade(i.item) : 0)
				.ThenBy(i => TrainingBooks.IsBook(i.item) ? TrainingBooks.Stat(i.item) : i.item);
		}

		private static void EnterInventory(GameSession session, Ma9MPacket.PKS_CS_ENTER_INVENTORY packet)
		{
			uint user = session.Team.UserCode;
			// Only items the client has a table row for; anything else would break its list.
			List<(int item, int amount)> items = InventoryOrder(s_store.Items(user).Where(i => ItemCatalog.Get(i.item) != null)).ToList();
			int owned = s_db.GetCards(user).Count;
			Ma9MPacket.PKS_SC_ENTER_INVENTORY reply = new Ma9MPacket.PKS_SC_ENTER_INVENTORY();
			reply.item_index.array = items.Select(i => new PInteger { n = i.item }).ToArray();
			reply.item_amount.array = items.Select(i => new PShort { n = (short)Math.Min(short.MaxValue, i.amount) }).ToArray();
			reply.gift_index.array = Array.Empty<PInteger>();
			reply.gift_remain_day.array = Array.Empty<PShort>();
			reply._possess_count.n = (byte)Math.Min(255, owned);
			reply._max_possess_count.n = (byte)Math.Min(255, (int)RosterHandlers.MaxPossessOf(user));
			// 코칭스태프 (CoachHandlers; 0 unless MA9M_COACH=1).
			reply.possess_coach_count.n = (byte)(CoachCatalog.Enabled ? Math.Min(255, CoachHandlers.Coaches(user).Count) : 0);
			reply.max_coach_count.n = (byte)(CoachCatalog.Enabled ? CoachCatalog.MaxCoaches : 0);
			reply.possess_count.n = (short)owned;
			reply.max_possess_count.n = RosterHandlers.MaxPossessOf(user);
			reply.GiftDataArr.array = Array.Empty<PGiftData>();
			session.Send(reply);
		}

		private static void UseCardPack(GameSession session, Ma9MPacket.PKS_CS_USE_CARDPACK_ITEM packet)
		{
			uint user = session.Team.UserCode;
			int item = packet.index.n;
			int amount = Math.Max(1, (int)packet.amount.n);
			Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM reply = new Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM();
			reply.index.n = item;
			reply.return_type.n = 1;
			reply.card_index.array = Array.Empty<PInteger>();
			reply.card_condition.array = Array.Empty<PByte>();
			reply.error_info.SetNoError();
			if (item == TrainingBooks.Pack)
			{
				OpenTrainingPacks(session, amount, reply);
				return;
			}
			if (CoachHandlers.IsPack(item))
			{
				CoachHandlers.OpenPacks(session, item, amount, reply);
				return;
			}
			List<TeamCard> owned = s_db.GetCards(user);
			// 레전드 선수팩 before the 레전드 cards are in the tables (2026-11-08): kept for later.
			if (item == ItemCatalog.LegendPlayersPack && !CardPacks.LegendsReady(s_catalog))
			{
				reply.result.n = 2;
				reply.error_info.SetErrorMessage("레전드 카드가 아직 준비 중입니다. 준비되면 열 수 있습니다.");
				session.Send(reply);
				return;
			}
			if (!CardPacks.IsPack(item) || s_store.Amount(user, item) < amount)
			{
				reply.result.n = 1;
			}
			else if (owned.Count + amount > RosterHandlers.MaxPossessOf(user))
			{
				reply.result.n = 2;
			}
			else
			{
				List<CardInfo> cards = CardPacks.Open(item, amount, s_catalog, owned.Select(c => c.Idx).ToList());
				s_db.AddCards(user, cards.Select(c => c.Idx), RosterHandlers.StartContract);
				s_store.Add(user, item, -cards.Count);
				Missions.Add(user, Missions.CardPacksOpened, cards.Count);  // 업적
				DateTime today = DateTime.Now;
				reply.result.n = (byte)(cards.Count > 0 ? 0 : 1);
				reply.card_index.array = cards.Select(c => new PInteger { n = c.Idx }).ToArray();
				reply.card_condition.array = cards.Select(c => new PByte { n = Condition.For(user, c.Idx, today) }).ToArray();
				ServerLog.Info($"[{session.Id}] pack {item} x{amount}: {string.Join(", ", cards.Select(c => $"{c.Year} {c.Name} {c.Ball}볼"))}");
			}
			session.Send(reply);
		}

		// 트레이닝 카드팩: one training book each (TrainingBooks), into the inventory; the card-open screen
		// shows the books.
		private static void OpenTrainingPacks(GameSession session, int amount, Ma9MPacket.PKS_SC_USE_CARDPACK_ITEM reply)
		{
			uint user = session.Team.UserCode;
			if (s_store.Amount(user, TrainingBooks.Pack) < amount)
			{
				reply.result.n = 1;
			}
			else
			{
				int[] books = Enumerable.Range(0, amount).Select(_ => TrainingBooks.RollBook(Random.Shared)).ToArray();
				foreach (int book in books)
				{
					s_store.Add(user, book, 1);
				}
				s_store.Add(user, TrainingBooks.Pack, -amount);
				Missions.Add(user, Missions.TrainingPacksOpened, amount);  // 업적
				reply.result.n = 0;
				reply.card_index.array = books.Select(b => new PInteger { n = b }).ToArray();
				reply.card_condition.array = books.Select(_ => new PByte { n = 0 }).ToArray();
				ServerLog.Info($"[{session.Id}] training pack x{amount}: {string.Join(", ", books.Select(TrainingBooks.Name))}");
			}
			session.Send(reply);
		}

		private static void UseItem(GameSession session, Ma9MPacket.PKS_CS_USE_ITEM packet)
		{
			// The only usable non-pack item so far: the 로스터 확장권 (the client adds the 20 slots on result 0).
			uint user = session.Team.UserCode;
			int item = packet.item_index.n;
			Ma9MPacket.PKS_SC_USE_ITEM reply = new Ma9MPacket.PKS_SC_USE_ITEM();
			reply.result.n = 1;
			reply.item_index.n = item;
			reply.arg1.str = packet.arg1.str ?? string.Empty;
			reply.arg2.str = packet.arg2.str ?? string.Empty;
			reply.arg3.str = packet.arg3.str ?? string.Empty;
			reply.error_info.SetNoError();
			if (item == ShopHandlers.RosterTicket && s_store.Amount(user, item) > 0)
			{
				// Several at once (user, 2026-11-11 19:1x: "로스터 확장권을 여러 장 한 번에 쓰는 기능을 만듭시다"): the count in arg1 (empty from
				// the 1.0.43 app and before: one), as many as the tickets held and the room to MaxCapacity allow. The reply's
				// arg1..3 = tickets used, storage before, after (the app's message; older apps add 20 themselves).
				int want = int.TryParse(packet.arg1.str, out int asked) && asked > 1 ? Math.Min(asked, 255) : 1;
				int have = s_store.Amount(user, item), used = 0;
				short first = 0, last = 0;
				while (used < want && used < have && ShopHandlers.UseRosterTicket(user, out short before, out short after))
				{
					if (used == 0) first = before;
					last = after;
					used++;
				}
				if (used > 0)
				{
					s_store.Add(user, item, -used);
					reply.result.n = 0;
					reply.arg1.str = used.ToString();
					reply.arg2.str = first.ToString();
					reply.arg3.str = last.ToString();
					ServerLog.Info($"[{session.Id}] 로스터 확장권 x{used}: 보관함 {first} → {last}");
				}
				else
				{
					reply.error_info.SetErrorMessage($"선수 보관함이 이미 최대({ShopHandlers.MaxCapacity}칸)입니다.");
				}
			}
			session.Send(reply);
		}

		// MainLobby_CouponPopup results: 0 ok, 1 no such coupon, 2 already used by this account (any of
		// its teams, deleted ones too), 3 use limit reached, 5 expired (endDate shown).
		private static void UseCoupon(GameSession session, Ma9MPacket.PKS_CS_USE_COUPON packet)
		{
			uint user = session.Team.UserCode;
			long account = session.Account?.Id ?? 0;
			string code = (packet.coupon_code.str ?? string.Empty).Trim();
			bool tester = session.Account?.Email?.StartsWith("autotest", StringComparison.OrdinalIgnoreCase) == true;
			CouponDef coupon = Array.Find(Coupons, c => c.Code == code && (!c.TesterOnly || tester));
			Ma9MPacket.PKS_SC_USE_COUPON reply = new Ma9MPacket.PKS_SC_USE_COUPON();
			reply.endDate.dateTime = DateTime.Now;
			reply.error.SetNoError();
			if (coupon == null)
			{
				reply.result.n = 1;
			}
			else if (coupon.Expires.HasValue && DateTime.Now > coupon.Expires.Value)
			{
				reply.result.n = 5;
				reply.endDate.dateTime = coupon.Expires.Value;
			}
			else
			{
				(int total, bool mine) = s_store.CouponUses(code, account);
				if (mine)
				{
					reply.result.n = 2;
				}
				else if (total >= coupon.MaxUses)
				{
					reply.result.n = 3;
				}
				else
				{
					s_store.UseCoupon(code, user, account);
					if (coupon.Mp != 0)
					{
						session.Team.Mp = s_db.AddMp(user, coupon.Mp);
					}
					foreach ((int item, int count) in coupon.Items)
					{
						s_store.Add(user, item, count);
					}
					reply.result.n = 0;
					ServerLog.Info($"[{session.Id}] coupon {code}: +{coupon.Mp} MP, {string.Join(", ", coupon.Items.Select(i => $"{i.item} x{i.count}"))}");
				}
			}
			session.Send(reply);
			if (reply.result.n == 0)
			{
				TopBar.Send(session);
			}
		}
	}
}
