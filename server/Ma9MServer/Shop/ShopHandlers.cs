using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 상점 (user, 2026-10-19): 포지션팩, 전체·고급 선수팩, 트레이닝팩 and 로스터 확장, all for MP.
	// The client (Shop_Main) shows whatever product list the server sends: tab 1 선수, 2 트레이닝,
	// 3 기타, and tab 0 추천 holds featured copies. Player packs open at once on the card-open screen, and
	// so does the training pack since 2026-10-21 (its books go to the inventory). The shop's 홈런볼
	// gauge gives a 홈런볼 for every 10 player cards bought. The 홈런볼 상점 sells the MP주머니 for 10 홈런볼;
	// it is opened in the inventory (USE_ITEM_LUCKMP), one MP amount by PouchOdds on the client's slot
	// machine. Pictures: tools/scripts/make_shop_images.py writes cdn/KBO/ItemImage/1/ShopItem/<file>.png,
	// ShopItem/small/<file>_small.png and the pouch's inventory pictures.
	public static class ShopHandlers
	{
		public const short StartCapacity = 80, MaxCapacity = 1400, CapacityStep = 20;  // max 1400 (user, 2026-11-02: "선수카드가 많이 늘었으니 선수단을 1400명까지 확장합시다"; 1200 since 10-28, 1100 since 10-26, 900 since 10-24 evening, 800 that morning, 700 since 10-22 night, 600 that morning, 500 since 10-19, 400 before)
		// The 조합 list (FusionHandlers.Enter) sends every card in one packet, 16 bytes each, under the 32 KB frame: about 2,000
		// cards at most before it needs splitting like the roster list (1,400 cards ≈ 22 KB).
		// 로스터 확장: 30만, then 10만 more each time, up to a cap; 150만 until 2026-10-25 (user, 2026-10-23: "최대 200만 MP까지 상승하되, 그 이상으로는
		// 오르지 않도록", that evening "200만도 비싸다 150만으로 변경"; it went up to 330만 before). Teams that paid more get
		// the difference back (RefundOverCap).
		// 2026-10-25 (user: "선수 확장 이게 너무 비싸 … 선수단 확장 비용을 100만 mp로 바꾸고 그 차액만큼 모든 팀에게 주기"): never above
		// 100만 now; RefundOverCap pays back what was paid above it at the 150만 cap (SecondExpandCap).
		private const int ExpandBase = 300_000, ExpandStep = 100_000, ExpandCap = 1_000_000;
		private const int SecondExpandCap = 1_500_000;
		// The 200만 cap's version (2026-10-23 16:58) made table shop_refund but kept no shop_price row.
		private const int FirstExpandCap = 2_000_000;
		private const int CardsPerHomeRunBall = 10;
		private const int ShopIndex = 1;
		// 100 since 2026-11-09 (engine session; user: "상점이랑 인벤토리 모두 최대 100개이고, 100개 이하일 시 가능한 최대 수량으로
		// 깔 수 있도록"): client Ma9MCardSpread.MaxOpen for packs that open on purchase; other products list up to 50
		// (Shop_DataCenter.maxBuyCardCount). Apps before 1.0.40 offer 50 at most.
		private const int MaxPerPurchase = 100;

		// Training: an inventory item; TrainingPack: opened at once (user, 2026-10-21: "트레이닝 카드팩은 상점에서
		// 사면 인벤토리가 아니라 선수카드팩처럼 그 화면에서 바로 깜").
		// CoachPack: the 코칭스태프 카드팩, opened at once too (user, 2026-11-08: "코치 카드팩을 상점에서 구매하면 아이템칸으로
		// 이동하던데 바로 깔 수 있는 상태면 좋겠음 … 구매 가능한 최대 카드팩 개수로"); packs from coupons still open in the inventory.
		private enum Kind { Pack, Training, Roster, Pouch, TrainingPack, CoachPack }

		// MP주머니: % by MP amount (user, 2026-10-25: "10, 20, 30, 50, 100, 200, 300, 500, 1000만 MP 이렇게 설정하고, 기댓값을
		// 조금 올려줘"), about 576,000 MP on average. Before: 1만-1,000만 in eight amounts, 463,150 on average. The teams
		// get the difference for every pouch they opened before (RefundPouches).
		public const int LuckyPouch = 90981;

		public const int SetupCloserPack = 151;
		private const int PouchHomeRunBalls = 10;
		private static readonly (int mp, double pct)[] PouchOdds =
			{ (100_000, 33), (200_000, 24), (300_000, 15), (500_000, 11), (1_000_000, 8), (2_000_000, 4.5), (3_000_000, 2.5), (5_000_000, 1), (10_000_000, 1) };

		// The average the pouches opened so far were paid at (shop_price.pouch_ev); without a row, the odds before 2026-10-25.
		private const int OldPouchEv = 463_150;

		internal static int PouchEv => (int)Math.Round(PouchOdds.Sum(o => o.mp * o.pct) / PouchOdds.Sum(o => o.pct));

		private sealed class Product
		{
			public int Index;
			public short Tab, Order;
			public string Image, Type, Name, Description;
			public int Cost;
			public Kind Kind;
			public int Item;                        // inventory item granted, or the card pack it stands for
			public Func<CardInfo, bool> Filter;     // position packs
			public double[] Odds;                   // position packs, % by ball (index 0 = 1볼)
		}

		// % by ball, from the 전체 선수팩 odds cut to the pack's range.
		private static readonly double[] Low = { 20, 18, 15, 13, 11, 9, 6.5, 0, 0, 0 };
		private static readonly double[] Mid = { 0, 18, 15, 13, 11, 9, 6.5, 4, 0, 0 };

		// 셋업·마무리팩 (user, 2026-10-22: the 불펜 투수팩 split into 중계 and 셋업·마무리, the latter at 70,000 MP).
		// Setup men and closers are nearly all 5-7볼 (2~8볼: 2·6·18·229·150·60·12 cards), so the band odds above
		// would pile their 2-4볼 rolls onto 26 cards; instead each card has about the same chance (shares of
		// those counts).
		private static readonly double[] Closer = { 0, 0.5, 1.5, 4, 48, 31, 12.5, 2.5, 0, 0 };

		// 동군·서군 by card team code (the client's eTeamName): SK·SSG 9, 롯데 7, 삼성 8, 두산 0, OB 10, kt 19, 쌍방울 11 /
		// KIA 5, 해태 1, LG 6, 한화 2, 빙그레 14, 히어로즈 3, NC 13, 현대 4, 태평양 15. Declared before Products (static order).
		private static readonly HashSet<int> EastClubs = new HashSet<int> { 9, 7, 8, 0, 10, 19, 11 };
		private static readonly HashSet<int> WestClubs = new HashSet<int> { 5, 1, 6, 2, 14, 3, 13, 4, 15 };

		private static readonly Product[] Products = BuildProducts();

		// 홈런볼 상점 (its own list; the client opens on tab 0).
		private static readonly Product[] HomeRunBallProducts =
		{
			new Product { Index = 401, Tab = 0, Order = 1, Image = "shop_luckybag_mp", Type = "MP주머니", Name = "MP주머니", Cost = PouchHomeRunBalls,
				// A shop text is a PString: one length byte, at most 255 bytes of UTF-16 (127 characters). The 2026-10-25
				// odds text had 143, and its wrapped length broke the whole 홈런볼 상점 list in the client (DescriptionFits).
				Kind = Kind.Pouch, Item = LuckyPouch, Description = "10만~1,000만 MP 중 하나(평균 약 58만). 인벤토리에서 열어 주세요.\n10만 33·20만 24·30만 15·50만 11·100만 8·200만 4.5·300만 2.5·500만 1·1000만 1 (%)" }
		};

		// 추천 tab: copies of these, in this order.
		private static readonly int[] Featured = { 102, 101, 201, 301 };

		private static Database s_db;
		private static CardCatalog s_catalog;
		private static string s_cs;
		private static readonly Random s_rnd = new Random();

		private static Product[] BuildProducts()
		{
			List<Product> list = new List<Product>
			{
				// Prices (user, 2026-10-22): 전체 6만 → 5만, 고급 15만 → 10만, 2~8볼 포지션팩 6만 → 4만; 1~7볼 3만 stays.
				// Odds since 2026-11-11 (CardPacks; were 9볼 이상 3.5%·10볼 1% and 10%·3%).
				new Product { Index = 101, Tab = 1, Order = 1, Image = "shop_pack_all", Type = "선수카드", Name = "전체 선수팩", Cost = 50_000, Kind = Kind.Pack,
					Item = ItemCatalog.AllPlayersPack, Description = "1~10볼 선수카드 1장 (타자·투수 무작위).\n9볼 이상 1.3%, 10볼 0.3%" },
				new Product { Index = 102, Tab = 1, Order = 2, Image = "shop_pack_high", Type = "선수카드", Name = "고급 선수팩", Cost = 100_000, Kind = Kind.Pack,
					Item = ItemCatalog.HighPlayersPack, Description = "4~10볼 선수카드 1장 (타자·투수 무작위).\n9볼 이상 3.5%, 10볼 0.7%" },
				// 레전드 선수팩 (user, 2026-11-08: "7만 MP로 하고, 2천만 MP가 레전드 1장의 기댓값으로"), shown and sold once the tables hold
				// 레전드 cards (Visible).
				new Product { Index = 103, Tab = 1, Order = 3, Image = "shop_pack_legend", Type = "선수카드", Name = "레전드 선수팩", Cost = CardPacks.LegendPackPrice, Kind = Kind.Pack,
					Item = ItemCatalog.LegendPlayersPack, Description = $"3~10볼 선수카드 1장, 또는 레전드 선수 ({CardPacks.LegendChance * 100:0.##}%).\n레전드는 이 팩에서만 나옵니다." },
			};
			// 연대팩·동군팩·서군팩 (user, 2026-11-10 decision 6: "동군팩, 서군팩, 연대팩을 만들건데 … 팀별로 2.5억 MP정도"; 11-11: "동군, 서군팩,
			// 연대팩도 괜찮은 제안인 것 같음"): the 전체 선수팩's odds over one decade's seasons (6만) or one half of the clubs (5만), the
			// halves as the starting-club grid (Emblem.EastTeam/WestTeam) with each club's lineage. A single team-year set deck takes
			// about 4,300 decade packs on average (tools/scripts/pack_cost.py).
			const string allOdds = "9볼 이상 1.3%, 10볼 0.3%";
			(int index, string code, string name, int from, int to)[] decades =
			{
				(104, "era90", "1990년대 선수팩", 1993, 1999), (105, "era00", "2000년대 선수팩", 2000, 2009),
				(106, "era10", "2010년대 선수팩", 2010, 2019), (107, "era20", "2020년대 선수팩", 2020, 2029),
			};
			short packOrder = 4;
			foreach ((int index, string code, string name, int from, int to) in decades)
			{
				list.Add(new Product { Index = index, Tab = 1, Order = packOrder++, Image = $"shop_pack_{code}", Type = "선수카드", Name = name, Cost = 60_000, Kind = Kind.Pack,
					Item = index, Filter = c => c.Year >= from && c.Year <= to, Odds = CardPacks.AllOdds,
					Description = $"{from}~{Math.Min(to, 2026)}년 시즌 선수카드 1장 (타자·투수 무작위).\n1~10볼, {allOdds}" });
			}
			list.Add(new Product { Index = 108, Tab = 1, Order = packOrder++, Image = "shop_pack_east", Type = "선수카드", Name = "동군 선수팩", Cost = 50_000, Kind = Kind.Pack,
				Item = 108, Filter = c => EastClubs.Contains(c.TeamCode), Odds = CardPacks.AllOdds,
				Description = $"SK·SSG 롯데 삼성 두산·OB kt 쌍방울 선수 1장.\n1~10볼, {allOdds}" });
			list.Add(new Product { Index = 109, Tab = 1, Order = packOrder++, Image = "shop_pack_west", Type = "선수카드", Name = "서군 선수팩", Cost = 50_000, Kind = Kind.Pack,
				Item = 109, Filter = c => WestClubs.Contains(c.TeamCode), Odds = CardPacks.AllOdds,
				Description = $"KIA·해태 LG 한화·빙그레 히어로즈 NC 현대·태평양 선수 1장.\n1~10볼, {allOdds}" });
			(string code, string name, string who, Func<CardInfo, bool> filter)[] positions =
			{
				("sp", "선발 투수팩", "선발 투수", c => !c.IsBatter && c.MainPosition == CardPos.SP),
				// 중계 only since 2026-10-22 (셋업·마무리팩 below); new pictures so no cached 불펜 picture shows.
				("mr", "중계 투수팩", "중계 투수", c => !c.IsBatter && c.MainPosition == CardPos.RP),
				// 지명타자 in the 외야수팩 (user, 2026-10-22: "외야수팩 = 외야수 + 지명타자").
				("if", "내야수팩", "포수·내야수", c => c.IsBatter && c.MainPosition != CardPos.LF && c.MainPosition != CardPos.CF && c.MainPosition != CardPos.RF && c.MainPosition != CardPos.DH),
				("of", "외야수팩", "외야수·지명타자", c => c.IsBatter && (c.MainPosition == CardPos.LF || c.MainPosition == CardPos.CF || c.MainPosition == CardPos.RF || c.MainPosition == CardPos.DH))
			};
			short order = packOrder;
			for (int p = 0; p < positions.Length; p++)
			{
				(string code, string name, string who, Func<CardInfo, bool> filter) = positions[p];
				// 외야수팩 +5,000 (user, 2026-10-22): three outfield spots plus a few high-ball DH, so a wanted position
				// comes easier than from the five-position 내야수팩.
				int extra = code == "of" ? 5_000 : 0;
				list.Add(new Product { Index = 111 + 10 * p, Tab = 1, Order = order++, Image = $"shop_pack_{code}1", Type = "포지션팩", Name = $"{name} 1~7볼",
					Cost = 30_000 + extra, Kind = Kind.Pack, Item = 111 + 10 * p, Filter = filter, Odds = Low, Description = $"{who} 카드 1장, 1~7볼.\n5볼 이상 29%, 7볼 7%" });
				list.Add(new Product { Index = 112 + 10 * p, Tab = 1, Order = order++, Image = $"shop_pack_{code}2", Type = "포지션팩", Name = $"{name} 2~8볼",
					Cost = 40_000 + extra, Kind = Kind.Pack, Item = 112 + 10 * p, Filter = filter, Odds = Mid, Description = $"{who} 카드 1장, 2~8볼.\n5볼 이상 40%, 8볼 레어 5.2%" });
				if (code == "mr")
				{
					list.Add(new Product { Index = SetupCloserPack, Tab = 1, Order = order++, Image = "shop_pack_cl", Type = "포지션팩", Name = "셋업·마무리팩 2~8볼",
						Cost = 70_000, Kind = Kind.Pack, Item = SetupCloserPack, Filter = c => !c.IsBatter && (c.MainPosition == CardPos.SU || c.MainPosition == CardPos.CP), Odds = Closer,
						Description = "셋업·마무리 투수 카드 1장, 2~8볼.\n5볼 이상 94%, 7볼 이상 15%, 8볼 레어 2.5%" });
				}
			}
			list.Add(new Product { Index = 201, Tab = 2, Order = 1, Image = "shop_pack_training", Type = "트레이닝", Name = "트레이닝 카드팩", Cost = 30_000, Kind = Kind.TrainingPack,
				Item = TrainingBooks.Pack, Description = "트레이닝북 1권. 사면 바로 열리고 북은 인벤토리로 들어갑니다.\n북 I 50% · II 30% · III 20%" });
			// 트레이닝 횟수 충전권 (user, 2026-10-21; price set by the systems session, user may change it).
			list.Add(new Product { Index = 202, Tab = 2, Order = 2, Image = "shop_tp_charge", Type = "트레이닝", Name = "트레이닝 횟수 충전권", Cost = 100_000, Kind = Kind.Training,
				Item = TrainingHandlers.TpCharge, Description = "선수 한 명의 TRAINING POINT 6칸을 다시 채웁니다.\n선수 정보 > 트레이닝의 아이템 버튼으로 사용" });
			// 코칭스태프 카드팩 (user 2026-11-05: "가격은 한 팩에 30만 MP정도"), only while the coaching staff is on. Opened at once
			// since 2026-11-08 (BuyCoachPack; it went to the inventory before); the count starts at the most one can buy.
			if (CoachCatalog.Enabled)
			{
				Func<int, string> odds = CoachCatalog.PackPercentText;  // per mille since 11-10, written as % (83 · 14.5 · 2 · 0.5)
				list.Add(new Product { Index = 203, Tab = 2, Order = 3, Image = "shop_pack_coach", Type = "코칭스태프", Name = "코칭스태프 카드팩", Cost = CoachCatalog.PackPrice,
					Kind = Kind.CoachPack, Item = CoachCatalog.Pack, Description = $"코치 카드 1장. 사면 바로 엽니다.\n노말 {odds(1)}% · 스페셜 {odds(2)}% · 레어 {odds(3)}% · 엘리트 {odds(4)}%" });
			}
			list.Add(new Product { Index = 301, Tab = 3, Order = 1, Image = "shop_roster_plus", Type = "확장", Name = "로스터 확장 +20", Kind = Kind.Roster, Item = 301 });
			// User, 2026-10-19: 스탯재분배권 at 100,000 MP (an inventory item, bought like the training pack).
			list.Add(new Product { Index = 302, Tab = 3, Order = 2, Image = "shop_stat_redist", Type = "계약", Name = "스탯재분배권", Cost = 100_000, Kind = Kind.Training,
				Item = ContractHandlers.RedistItem, Description = "계약 보너스 능력치를 다시 나눕니다.\n계약관리에서 선수를 고르고 재분배 버튼으로 사용" });
			// 컨디션 회복제 (user, 2026-10-22): 초급 15만, 중급 30만 MP; 고급 is not sold (PlayerManageHandlers).
			list.Add(new Product { Index = 303, Tab = 3, Order = 3, Image = "shop_cond_1", Type = "선수관리", Name = "초급 컨디션 회복제", Cost = 150_000, Kind = Kind.Training,
				Item = PlayerManageHandlers.Basic, Description = "선수 한 명의 컨디션 +1칸, 24시간(리그 72경기).\n팀 로스터 > 선수관리에서 사용, 동시에 5명까지" });
			list.Add(new Product { Index = 304, Tab = 3, Order = 4, Image = "shop_cond_2", Type = "선수관리", Name = "중급 컨디션 회복제", Cost = 300_000, Kind = Kind.Training,
				Item = PlayerManageHandlers.Middle, Description = "선수 한 명의 컨디션 +2칸, 24시간(리그 72경기).\n팀 로스터 > 선수관리에서 사용, 동시에 5명까지" });
			// 특별강좌 (user, 2026-10-22): 초기화권 100만, 행운권 40만 MP (LessonHandlers).
			list.Add(new Product { Index = 305, Tab = 3, Order = 5, Image = "shop_lesson_reset", Type = "특별강좌", Name = "특별강좌 초기화권", Cost = 1_000_000, Kind = Kind.Training,
				Item = LessonHandlers.Reset, Description = "선수 한 명의 특별수강 결과를 지우고 다시 수강하게 합니다.\n결과가 있는 선수를 신청할 때 사용" });
			list.Add(new Product { Index = 306, Tab = 3, Order = 6, Image = "shop_lesson_lucky", Type = "특별강좌", Name = "특별강좌 행운권", Cost = 400_000, Kind = Kind.Training,
				Item = LessonHandlers.Lucky, Description = "특별수강 주 능력치 +5 확률 30% → 60%.\n수강신청 화면에서 사용" });
			// 포지션 변경 (user, 2026-10-24): 초기화권 80만 MP (PositionChangeHandlers).
			list.Add(new Product { Index = 307, Tab = 3, Order = 7, Image = "shop_pos_reset", Type = "포지션 변경", Name = "포지션 변경 초기화권", Cost = 800_000, Kind = Kind.Training,
				Item = PositionChangeHandlers.ResetItem, Description = "포지션을 바꾼 투수를 원래 보직·능력치로 되돌립니다.\n그 뒤 다시 포지션 변경 가능" });
			// 특강 즉시 완료권 (user, 2026-10-29: "즉시 전력감으로 사용할 수 있다는 점에서 비싸야한다고 생각함, 300만 MP면 될 듯").
			list.Add(new Product { Index = 308, Tab = 3, Order = 8, Image = "shop_lesson_instant", Type = "특별강좌", Name = "특강 즉시 완료권", Cost = 3_000_000, Kind = Kind.Training,
				Item = LessonHandlers.Instant, Description = "특별수강을 바로 끝내 결과를 받습니다.\n수강신청 때, 또는 수강 중인 선수 카드를 눌러 사용" });
			return list.ToArray();
		}

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dbPath)
		{
			Init(db, catalog, dbPath);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_NORMAL>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_HBALL>(EnterHomeRunBallShop);
			loop.Register<Ma9MPacket.PKS_CS_BUY_ITEM_SHOP_NEW>(Buy);
			loop.Register<Ma9MPacket.PKS_CS_USE_ITEM_LUCKMP>(OpenPouch);
			// 로스터 확장 차액 notice (RefundOverCap).
			GameLoop.AfterPacket += DeliverRefund;
			// MP주머니 차액 notice (RefundPouches).
			GameLoop.AfterPacket += DeliverPouchRefund;
		}

		private static void Init(Database db, CardCatalog catalog, string dbPath)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			int paidCap, paidEv;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'shop_refund'";
				bool capped = cmd.ExecuteScalar() != null;
				// Per team: card storage size, expansions bought, player cards bought (홈런볼 gauge).
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS team_shop (
					user_code INTEGER PRIMARY KEY,
					max_cards INTEGER NOT NULL,
					expansions INTEGER NOT NULL DEFAULT 0,
					cards_bought INTEGER NOT NULL DEFAULT 0);
					CREATE TABLE IF NOT EXISTS shop_refund (
					user_code INTEGER PRIMARY KEY,
					expansions INTEGER NOT NULL,
					mp INTEGER NOT NULL,
					made TEXT NOT NULL,
					shown INTEGER NOT NULL DEFAULT 0);
					CREATE TABLE IF NOT EXISTS shop_price (
					name TEXT PRIMARY KEY,
					value INTEGER NOT NULL);
					CREATE TABLE IF NOT EXISTS pouch_refund (
					user_code INTEGER PRIMARY KEY,
					pouches INTEGER NOT NULL,
					mp INTEGER NOT NULL,
					made TEXT NOT NULL,
					shown INTEGER NOT NULL DEFAULT 0)";
				cmd.ExecuteNonQuery();
				// expand_cap: the cap the expansions so far were charged at.
				cmd.CommandText = "SELECT value FROM shop_price WHERE name = 'expand_cap'";
				object paid = cmd.ExecuteScalar();
				paidCap = paid != null ? Convert.ToInt32(paid) : capped ? FirstExpandCap : int.MaxValue;
				cmd.CommandText = "SELECT value FROM shop_price WHERE name = 'pouch_ev'";
				object ev = cmd.ExecuteScalar();
				paidEv = ev != null ? Convert.ToInt32(ev) : OldPouchEv;
			}
			RefundOverCap(paidCap);
			RefundPouches(paidEv);
		}

		// MP주머니 opened by a team so far: the 홈런볼 it earned (league home runs, Missions' league_hr counter, and one per
		// CardsPerHomeRunBall player cards bought) less what it has left buy 10 each, less the pouches it still holds. The
		// 홈런볼 are spent on nothing else. A missing table (a new data directory) counts 0.
		internal static int OpenedPouches(SqliteConnection c, SqliteTransaction tx, uint user)
		{
			long Scalar(string table, string sql)
			{
				using SqliteCommand cmd = c.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
				cmd.Parameters.AddWithValue("$t", table);
				if (cmd.ExecuteScalar() == null) return 0;
				cmd.CommandText = sql;
				cmd.Parameters.AddWithValue("$u", (long)user);
				object v = cmd.ExecuteScalar();
				return v == null || v is DBNull ? 0 : Convert.ToInt64(v);
			}
			long homeRuns = Scalar("mission_counter", "SELECT value FROM mission_counter WHERE user_code = $u AND key = 'league_hr'");
			long cards = Scalar("team_shop", "SELECT cards_bought FROM team_shop WHERE user_code = $u");
			long left = Scalar("wallet", "SELECT hball FROM wallet WHERE user_code = $u");
			long held = Scalar("team_item", $"SELECT amount FROM team_item WHERE user_code = $u AND item_idx = {LuckyPouch}");
			return (int)Math.Max(0, (homeRuns + cards / CardsPerHomeRunBall - left) / PouchHomeRunBalls - held);
		}

		internal static int OpenedPouches(uint user)
		{
			using SqliteConnection c = Open();
			return OpenedPouches(c, null, user);
		}

		private static readonly HashSet<uint> s_pouchPending = new HashSet<uint>();

		// When the pouch's average rises above the one the pouches so far were paid at (shop_price.pouch_ev), every team gets
		// the difference for each pouch it opened. As RefundOverCap: the MP, the notice rows and the new average in one
		// transaction, so each rise is paid once; the notice waits for the team's next packet (DeliverPouchRefund).
		private static void RefundPouches(int paidEv)
		{
			int gain = PouchEv - paidEv;
			List<(uint user, int pouches, int mp)> refunds = new List<(uint, int, int)>();
			using (SqliteConnection c = Open())
			using (SqliteTransaction tx = c.BeginTransaction())
			{
				if (gain > 0)
				{
					List<uint> teams = new List<uint>();
					using (SqliteCommand cmd = c.CreateCommand())
					{
						cmd.Transaction = tx;
						cmd.CommandText = "SELECT user_code FROM team";
						using SqliteDataReader r = cmd.ExecuteReader();
						while (r.Read()) teams.Add((uint)r.GetInt64(0));
					}
					foreach (uint user in teams)
					{
						int pouches = OpenedPouches(c, tx, user);
						long mp = Math.Min(int.MaxValue, (long)pouches * gain);
						if (mp <= 0) continue;
						using SqliteCommand cmd = c.CreateCommand();
						cmd.Transaction = tx;
						cmd.CommandText = @"INSERT INTO pouch_refund (user_code, pouches, mp, made, shown) VALUES ($u, $p, $m, $t, 0)
							ON CONFLICT(user_code) DO UPDATE SET mp = CASE WHEN shown = 0 THEN mp + excluded.mp ELSE excluded.mp END,
							pouches = excluded.pouches, made = excluded.made, shown = 0;
							UPDATE team SET mp = MIN(2147483647, MAX(0, mp + $m)) WHERE user_code = $u";
						cmd.Parameters.AddWithValue("$u", (long)user);
						cmd.Parameters.AddWithValue("$p", pouches);
						cmd.Parameters.AddWithValue("$m", mp);
						cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
						cmd.ExecuteNonQuery();
						refunds.Add((user, pouches, (int)mp));
					}
				}
				if (paidEv != PouchEv)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.Transaction = tx;
					cmd.CommandText = "INSERT OR REPLACE INTO shop_price (name, value) VALUES ('pouch_ev', $ev)";
					cmd.Parameters.AddWithValue("$ev", PouchEv);
					cmd.ExecuteNonQuery();
				}
				tx.Commit();
			}
			foreach ((uint user, int pouches, int mp) in refunds)
			{
				ServerLog.Info($"shop: MP주머니 차액 user {user}: {pouches}개 × {gain:N0} = +{mp:N0} MP");
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT user_code FROM pouch_refund WHERE shown = 0";
				using SqliteDataReader r = cmd.ExecuteReader();
				lock (s_pouchPending)
				{
					while (r.Read()) s_pouchPending.Add((uint)r.GetInt64(0));
				}
			}
		}

		private static void DeliverPouchRefund(GameSession session)
		{
			if (session?.Team == null) return;
			uint user = session.Team.UserCode;
			lock (s_pouchPending)
			{
				if (!s_pouchPending.Contains(user) || LeagueHandlers.HasUnconfirmed(user)) return;
				s_pouchPending.Remove(user);
			}
			int mp = 0, pouches = 0;
			using (SqliteConnection c = Open())
			{
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT mp, pouches FROM pouch_refund WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$u", (long)user);
					using SqliteDataReader r = cmd.ExecuteReader();
					if (r.Read()) { mp = (int)r.GetInt64(0); pouches = (int)r.GetInt64(1); }
				}
				using (SqliteCommand mark = c.CreateCommand())
				{
					mark.CommandText = "UPDATE pouch_refund SET shown = 1 WHERE user_code = $u";
					mark.Parameters.AddWithValue("$u", (long)user);
					mark.ExecuteNonQuery();
				}
			}
			if (mp <= 0) return;
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[]
			{
				new PLongString { str = "MP주머니 기댓값 상향" },
				new PLongString { str = $"MP주머니가 10만~1,000만 MP로 바뀌고 평균이 약 {OldPouchEv / 10_000.0:F0}만에서 {PouchEv / 10_000.0:F0}만 MP로 올랐습니다.\n지금까지 연 MP주머니 {pouches}개의 차액\n{mp:N0} MP를 드렸습니다." }
			};
			session.Send(popup);
			session.Team.Mp = s_db.AddMp(user, 0);
			TopBar.Send(session);
			ServerLog.Info($"[{session.Id}] MP주머니 차액 알림: {mp:N0} MP");
		}

		// What a team paid above today's price for the expansions it has, charged with paidCap (the old price had no cap).
		internal static int OverCap(int expansions, int paidCap = int.MaxValue) =>
			Enumerable.Range(0, expansions).Sum(k => Math.Min(paidCap, ExpandBase + ExpandStep * k) - ExpandCost(k));

		private static int OverCapCount(int expansions) => Enumerable.Range(0, expansions).Count(k => ExpandBase + ExpandStep * k > ExpandCap);

		private static readonly HashSet<uint> s_refundPending = new HashSet<uint>();

		// When the cap drops below the one the expansions so far were charged at (shop_price.expand_cap; the old price had no
		// cap, and the 200만 version kept no row), every team gets back what it paid above today's price for the expansions
		// it has. The MP, the notice rows and the new cap are written in one transaction, so each drop is paid exactly once.
		// A team's notice row adds up amounts it has not seen yet; the notice waits for the team's next packet without
		// unconfirmed league games (DeliverRefund).
		private static void RefundOverCap(int paidCap)
		{
			List<(uint user, int expansions, int mp)> refunds = new List<(uint, int, int)>();
			using (SqliteConnection c = Open())
			using (SqliteTransaction tx = c.BeginTransaction())
			{
				if (paidCap > ExpandCap)
				{
					List<(uint user, int expansions)> teams = new List<(uint, int)>();
					using (SqliteCommand cmd = c.CreateCommand())
					{
						cmd.Transaction = tx;
						cmd.CommandText = "SELECT user_code, expansions FROM team_shop";
						using SqliteDataReader r = cmd.ExecuteReader();
						while (r.Read()) teams.Add(((uint)r.GetInt64(0), (int)r.GetInt64(1)));
					}
					foreach ((uint user, int expansions) in teams)
					{
						int mp = OverCap(expansions, paidCap);
						if (mp <= 0) continue;
						using SqliteCommand cmd = c.CreateCommand();
						cmd.Transaction = tx;
						// The MP as Database.AddMp adds it, inside this transaction.
						cmd.CommandText = @"INSERT INTO shop_refund (user_code, expansions, mp, made, shown) VALUES ($u, $e, $m, $t, 0)
							ON CONFLICT(user_code) DO UPDATE SET mp = CASE WHEN shown = 0 THEN mp + excluded.mp ELSE excluded.mp END,
							expansions = excluded.expansions, made = excluded.made, shown = 0;
							UPDATE team SET mp = MIN(2147483647, MAX(0, mp + $m)) WHERE user_code = $u";
						cmd.Parameters.AddWithValue("$u", (long)user);
						cmd.Parameters.AddWithValue("$e", expansions);
						cmd.Parameters.AddWithValue("$m", mp);
						cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
						cmd.ExecuteNonQuery();
						refunds.Add((user, expansions, mp));
					}
				}
				if (paidCap != ExpandCap)
				{
					using SqliteCommand cmd = c.CreateCommand();
					cmd.Transaction = tx;
					cmd.CommandText = "INSERT OR REPLACE INTO shop_price (name, value) VALUES ('expand_cap', $cap)";
					cmd.Parameters.AddWithValue("$cap", ExpandCap);
					cmd.ExecuteNonQuery();
				}
				tx.Commit();
			}
			foreach ((uint user, int expansions, int mp) in refunds)
			{
				ServerLog.Info($"shop: 로스터 확장 차액 user {user}: {expansions}번 중 {OverCapCount(expansions)}번이 {ExpandCap / 10_000}만 초과, +{mp:N0} MP");
			}
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT user_code FROM shop_refund WHERE shown = 0";
				using SqliteDataReader r = cmd.ExecuteReader();
				lock (s_refundPending)
				{
					while (r.Read()) s_refundPending.Add((uint)r.GetInt64(0));
				}
			}
		}

		private static void DeliverRefund(GameSession session)
		{
			if (session?.Team == null) return;
			uint user = session.Team.UserCode;
			lock (s_refundPending)
			{
				if (!s_refundPending.Contains(user) || LeagueHandlers.HasUnconfirmed(user)) return;
				s_refundPending.Remove(user);
			}
			int mp = 0, expansions = 0;
			using (SqliteConnection c = Open())
			{
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "SELECT mp, expansions FROM shop_refund WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$u", (long)user);
					using SqliteDataReader r = cmd.ExecuteReader();
					if (r.Read()) { mp = (int)r.GetInt64(0); expansions = (int)r.GetInt64(1); }
				}
				using (SqliteCommand mark = c.CreateCommand())
				{
					mark.CommandText = "UPDATE shop_refund SET shown = 1 WHERE user_code = $u";
					mark.Parameters.AddWithValue("$u", (long)user);
					mark.ExecuteNonQuery();
				}
			}
			if (mp <= 0) return;
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[]
			{
				new PLongString { str = "선수단 확장 가격 인하" },
				new PLongString { str = $"선수 보관함 확장은 이제 한 번에 최대 {ExpandCap / 10_000}만 MP입니다.\n이미 {ExpandCap / 10_000}만 MP 넘게 내고 산 확장 {OverCapCount(expansions)}번의 차액\n{mp:N0} MP를 돌려드렸습니다." }
			};
			session.Send(popup);
			session.Team.Mp = s_db.AddMp(user, 0);
			TopBar.Send(session);
			ServerLog.Info($"[{session.Id}] 로스터 확장 차액 알림: {mp:N0} MP");
		}

		// The team's player-card storage (보관함), 80 to MaxCapacity.
		public static short Capacity(uint user) => s_cs == null ? StartCapacity : State(user).cap;

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		// Cards bought from the shop's packs so far (업적 backfill, Missions.Backfill).
		public static int CardsBought(uint user) => State(user).bought;

		private static (short cap, int expansions, int bought) State(uint user)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT max_cards, expansions, cards_bought FROM team_shop WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", user);
			using SqliteDataReader r = cmd.ExecuteReader();
			return r.Read() ? ((short)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2)) : (StartCapacity, 0, 0);
		}

		private static void Save(uint user, short cap, int expansions, int bought)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = @"INSERT INTO team_shop (user_code, max_cards, expansions, cards_bought) VALUES ($u, $c, $e, $b)
				ON CONFLICT(user_code) DO UPDATE SET max_cards = $c, expansions = $e, cards_bought = $b";
			cmd.Parameters.AddWithValue("$u", user);
			cmd.Parameters.AddWithValue("$c", cap);
			cmd.Parameters.AddWithValue("$e", expansions);
			cmd.Parameters.AddWithValue("$b", bought);
			cmd.ExecuteNonQuery();
		}

		// The 레전드 선수팩 waits for the 레전드 cards (CardPacks.LegendsReady); every other product is always on.
		private static bool Visible(Product p) => p.Item != ItemCatalog.LegendPlayersPack || s_catalog != null && CardPacks.LegendsReady(s_catalog);

		private static int ExpandCost(int expansions) => Math.Min(ExpandCap, ExpandBase + ExpandStep * expansions);

		public static Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_NORMAL BuildEnter(uint user)
		{
			(short cap, int expansions, int bought) = State(user);
			Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_NORMAL reply = new Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_NORMAL();
			reply.error.SetNoError();
			reply.CurrShopIndex.n = ShopIndex;
			reply.ShopData.array = Products.Where(Visible).Select(p => ToShopData(p, cap, expansions)).ToArray();
			reply.CardCount.n = (short)s_db.GetCards(user).Count;
			reply.MaxCardCount.n = cap;
			// The client's coach count (Shop_DataCenter.CalculateItemBuyCount: the pack's count starts at min(MP / price, 50,
			// room for coaches); 0/0 made it 1).
			reply.CoachCount.n = (short)(CoachCatalog.Enabled ? CoachHandlers.Coaches(user).Count : 0);
			reply.MaxCoachCount.n = (short)(CoachCatalog.Enabled ? CoachCatalog.MaxCoaches : 0);
			reply.HBallCount.n = LeagueService.Store.HomeRunBalls(user);
			reply.PlayerCardBuyCount.n = (short)(bought % CardsPerHomeRunBall);
			reply.RewardPlayerCardBuyCount.n = CardsPerHomeRunBall;
			reply.ShopFeatured.array = Featured.Select((index, k) => new PShopFeatured(0, (short)(k + 1), index, k + 1)).ToArray();
			return reply;
		}

		private static PShopData ToShopData(Product p, short cap, int expansions)
		{
			int cost = p.Kind == Kind.Roster ? ExpandCost(expansions) : p.Cost;
			string description = p.Kind != Kind.Roster ? p.Description
				: cap >= MaxCapacity ? $"선수 보관함이 최대({MaxCapacity}칸)입니다."
				: $"선수 보관함 +{CapacityStep}칸 (지금 {cap}칸, 최대 {MaxCapacity}칸).\n살 때마다 {ExpandStep / 10_000}만 MP씩 오르고, 최대 {ExpandCap / 10_000}만 MP입니다.";
			PShopData data = new PShopData();
			data.SetData(p.Index, p.Tab, p.Order, p.Image, p.Type, p.Name, p.Kind == Kind.Pouch ? eShopCostType.HBALL : eShopCostType.MP, cost, description,
				p.Kind == Kind.Pack ? eBuyType.CARDPACK_PLAYER : p.Kind == Kind.TrainingPack ? eBuyType.CARDPACK_TRAINING : p.Kind == Kind.CoachPack ? eBuyType.CARDPACK_COACH : eBuyType.NONE,
				p.Kind == Kind.Pack ? eShowCountType.PLAYER : p.Kind == Kind.CoachPack ? eShowCountType.COACH : eShowCountType.NONE, 0);
			data.SetItems(new[] { p.Item }, new short[] { 1 });
			return data;
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_NORMAL packet) => session.Send(BuildEnter(session.Team.UserCode));

		private static void EnterHomeRunBallShop(GameSession session, Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_HBALL packet)
		{
			uint user = session.Team.UserCode;
			(short cap, int expansions, int bought) = State(user);
			Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_HBALL reply = new Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_HBALL();
			reply.error.SetNoError();
			reply.CurrShopIndex.n = ShopIndex + 1;
			reply.ShopData.array = HomeRunBallProducts.Select(p => ToShopData(p, cap, expansions)).ToArray();
			reply.CardCount.n = (short)s_db.GetCards(user).Count;
			reply.MaxCardCount.n = cap;
			reply.HBallCount.n = LeagueService.Store.HomeRunBalls(user);
			reply.PlayerCardBuyCount.n = (short)(bought % CardsPerHomeRunBall);
			reply.RewardPlayerCardBuyCount.n = CardsPerHomeRunBall;
			session.Send(reply);
		}

		private static void Buy(GameSession session, Ma9MPacket.PKS_CS_BUY_ITEM_SHOP_NEW packet)
		{
			List<Packet.PKS_PACKET> replies = Purchase(session.Team.UserCode, packet.ShopItemIndex.n, packet.Amount.n, out string log);
			ServerLog.Info($"[{session.Id}] shop: {log}");
			foreach (Packet.PKS_PACKET reply in replies)
			{
				session.Send(reply);
			}
		}

		// One purchase: the replies to send, in order, and a line for the log.
		public static List<Packet.PKS_PACKET> Purchase(uint user, int productIndex, int requested, out string log)
		{
			Product p = Products.Concat(HomeRunBallProducts).FirstOrDefault(x => x.Index == productIndex);
			int amount = Math.Clamp(requested, 1, MaxPerPurchase);
			if (p == null || !Visible(p))
			{
				return Refuse("없는 상품입니다.", out log);
			}
			switch (p.Kind)
			{
				case Kind.Pack:
					return BuyPack(user, p, amount, out log);
				case Kind.Training:
					return BuyTraining(user, p, amount, out log);
				case Kind.TrainingPack:
					return BuyTrainingPack(user, p, amount, out log);
				case Kind.CoachPack:
					return BuyCoachPack(user, p, amount, out log);
				case Kind.Pouch:
					return BuyPouch(user, p, amount, out log);
				default:
					return BuyRoster(user, amount, out log);
			}
		}

		private static List<Packet.PKS_PACKET> Refuse(string message, out string log)
		{
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_COMMON reply = new Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_COMMON();
			reply.error.SetErrorMessage(message);
			log = "refused: " + message;
			return new List<Packet.PKS_PACKET> { reply };
		}

		private static List<Packet.PKS_PACKET> BuyPack(uint user, Product p, int amount, out string log)
		{
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK reply = new Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK();
			reply.BuyType = eBuyType.CARDPACK_PLAYER;
			reply.SetCardIndices(new List<int>());
			reply.SetCardConditions(new List<byte>());
			int mp = s_db.AddMp(user, 0);
			List<TeamCard> owned = s_db.GetCards(user);
			(short cap, int expansions, int bought) = State(user);
			string refuse = mp < p.Cost * amount ? $"MP가 부족합니다. ({p.Cost * amount:N0} MP 필요)"
				: owned.Count + amount > cap ? $"선수 보관함이 부족합니다 ({owned.Count}/{cap}). 로스터 확장이나 방출로 자리를 만들어 주세요." : null;
			List<CardInfo> cards = refuse == null ? Draw(p, amount, owned.Select(c => c.Idx).ToList()) : new List<CardInfo>();
			if (refuse == null && cards.Count == 0)
			{
				refuse = "이 팩에서 더 나올 카드가 없습니다.";
			}
			if (refuse != null)
			{
				// The client shows the card-pack reply's error itself (and stays on the shop).
				reply.error.SetErrorMessage(refuse);
				reply.AfterMP = mp;
				log = $"refused {p.Name} x{amount}: {refuse}";
				return new List<Packet.PKS_PACKET> { reply };
			}
			s_db.AddCards(user, cards.Select(c => c.Idx), RosterHandlers.StartContract);
			int after = s_db.AddMp(user, -p.Cost * cards.Count);
			int total = bought + cards.Count;
			int balls = total / CardsPerHomeRunBall - bought / CardsPerHomeRunBall;
			Save(user, cap, expansions, total);
			// 오늘의 미션 카드팩 구매, 업적 카드팩 개봉.
			Missions.Add(user, Missions.CardPacksBought, cards.Count);
			Missions.Add(user, Missions.CardPacksOpened, cards.Count);
			int hball = balls > 0 ? LeagueService.Store.AddHomeRunBalls(user, balls) : LeagueService.Store.HomeRunBalls(user);
			DateTime today = DateTime.Now;
			reply.error.SetNoError();
			reply.AfterMP = after;
			reply.AfterCash = 0;
			reply.AfterHBall = (short)Math.Min(short.MaxValue, hball);
			reply.SetCardIndices(cards.Select(c => c.Idx).ToList());
			reply.SetCardConditions(cards.Select(c => Condition.For(user, c.Idx, today)).ToList());
			reply.GetHBall = (short)balls;
			log = $"{p.Name} x{cards.Count} ({p.Cost * cards.Count:N0} MP{(balls > 0 ? $", 홈런볼 +{balls}" : "")}): "
				+ string.Join(", ", cards.Select(c => $"{c.Year} {c.Name} {c.Ball}볼"));
			return new List<Packet.PKS_PACKET> { reply };
		}

		// Cards for a pack: 전체/고급 use the inventory packs' draw (CardPacks), position packs the same
		// rule over their positions: a ball from the odds, then a card of that ball the team lacks, the
		// nearest ball when none is left.
		public static List<CardInfo> Draw(int productIndex, int count, ICollection<int> owned)
		{
			Product p = Products.First(x => x.Index == productIndex);
			return Draw(p, count, owned);
		}

		private static List<CardInfo> Draw(Product p, int count, ICollection<int> owned)
		{
			if (p.Filter == null)
			{
				return CardPacks.Open(p.Item, count, s_catalog, owned);
			}
			HashSet<int> taken = new HashSet<int>(owned);
			Dictionary<int, List<CardInfo>> byBall = s_catalog.Drawable.Where(p.Filter).GroupBy(c => c.Ball).ToDictionary(g => g.Key, g => g.ToList());
			int lo = Array.FindIndex(p.Odds, x => x > 0) + 1, hi = Array.FindLastIndex(p.Odds, x => x > 0) + 1;
			List<CardInfo> cards = new List<CardInfo>();
			for (int n = 0; n < count; n++)
			{
				double u = s_rnd.NextDouble() * p.Odds.Sum();
				int ball = hi;
				for (int i = 0; i < p.Odds.Length; i++)
				{
					u -= p.Odds[i];
					if (u < 0)
					{
						ball = i + 1;
						break;
					}
				}
				CardInfo card = null;
				for (int spread = 0; spread <= 9 && card == null; spread++)
				{
					foreach (int b in new[] { ball - spread, ball + spread }.Distinct())
					{
						if (b < lo || b > hi || !byBall.TryGetValue(b, out List<CardInfo> pool))
						{
							continue;
						}
						List<CardInfo> free = pool.Where(c => !taken.Contains(c.Idx)).ToList();
						if (free.Count > 0)
						{
							card = free[s_rnd.Next(free.Count)];
							break;
						}
					}
				}
				if (card == null)
				{
					break;
				}
				taken.Add(card.Idx);
				cards.Add(card);
			}
			return cards;
		}

		private static Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_NONE Done(uint user, int mp)
		{
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_NONE reply = new Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_NONE();
			reply.error.SetNoError();
			reply.AfterMP = mp;
			reply.AfterCash = 0;
			reply.AfterHBall = (short)Math.Min(short.MaxValue, LeagueService.Store.HomeRunBalls(user));
			return reply;
		}

		private static List<Packet.PKS_PACKET> BuyTraining(uint user, Product p, int amount, out string log)
		{
			int mp = s_db.AddMp(user, 0);
			if (mp < p.Cost * amount)
			{
				return Refuse($"MP가 부족합니다. ({p.Cost * amount:N0} MP 필요)", out log);
			}
			InventoryHandlers.AddItem(user, p.Item, amount);
			int after = s_db.AddMp(user, -p.Cost * amount);
			if (p.Item == TrainingBooks.Pack) Missions.Add(user, Missions.TrainingPacksBought, amount);  // 오늘의 미션
			log = $"{p.Name} x{amount} ({p.Cost * amount:N0} MP) → inventory";
			return new List<Packet.PKS_PACKET> { Done(user, after) };
		}

		// 트레이닝 카드팩, opened at once: a book each into the inventory, shown on the card-open screen.
		private static List<Packet.PKS_PACKET> BuyTrainingPack(uint user, Product p, int amount, out string log)
		{
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK reply = new Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK();
			reply.BuyType = eBuyType.CARDPACK_TRAINING;
			reply.SetCardIndices(new List<int>());
			reply.SetCardConditions(new List<byte>());
			int mp = s_db.AddMp(user, 0);
			if (mp < p.Cost * amount)
			{
				reply.error.SetErrorMessage($"MP가 부족합니다. ({p.Cost * amount:N0} MP 필요)");
				reply.AfterMP = mp;
				log = $"refused {p.Name} x{amount}: MP";
				return new List<Packet.PKS_PACKET> { reply };
			}
			int[] books = Enumerable.Range(0, amount).Select(_ => TrainingBooks.RollBook(s_rnd)).ToArray();
			foreach (int book in books)
			{
				InventoryHandlers.AddItem(user, book, 1);
			}
			int after = s_db.AddMp(user, -p.Cost * amount);
			Missions.Add(user, Missions.TrainingPacksBought, amount);  // 오늘의 미션
			Missions.Add(user, Missions.TrainingPacksOpened, amount);  // 업적
			reply.error.SetNoError();
			reply.AfterMP = after;
			reply.AfterCash = 0;
			reply.AfterHBall = (short)Math.Min(short.MaxValue, LeagueService.Store.HomeRunBalls(user));
			reply.SetCardIndices(books.ToList());
			reply.SetCardConditions(books.Select(_ => (byte)0).ToList());
			reply.GetHBall = 0;
			log = $"{p.Name} x{amount} ({p.Cost * amount:N0} MP), opened: " + string.Join(", ", books.Select(TrainingBooks.Name));
			return new List<Packet.PKS_PACKET> { reply };
		}

		// 코칭스태프 카드팩, opened at once (2026-11-08): a coach each, as from the inventory (CoachHandlers.OpenPacks), shown on
		// the card-open screen (the client's CARDPACK_COACH branch). Refused whole when the coaches would pass 100.
		private static List<Packet.PKS_PACKET> BuyCoachPack(uint user, Product p, int amount, out string log)
		{
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK reply = new Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK();
			reply.BuyType = eBuyType.CARDPACK_COACH;
			reply.SetCardIndices(new List<int>());
			reply.SetCardConditions(new List<byte>());
			int mp = s_db.AddMp(user, 0), owned = CoachHandlers.Coaches(user).Count;
			string why = mp < p.Cost * amount ? $"MP가 부족합니다. ({p.Cost * amount:N0} MP 필요)"
				: owned + amount > CoachCatalog.MaxCoaches ? $"코치는 {CoachCatalog.MaxCoaches}장까지 가질 수 있습니다. (지금 {owned}장)"
				: null;
			if (why != null)
			{
				reply.error.SetErrorMessage(why);
				reply.AfterMP = mp;
				log = $"refused {p.Name} x{amount}: {why}";
				return new List<Packet.PKS_PACKET> { reply };
			}
			List<int> cards = Enumerable.Range(0, amount).Select(_ => CoachCatalog.Roll(s_rnd)).ToList();
			CoachHandlers.AddCoaches(user, cards);
			int after = s_db.AddMp(user, -p.Cost * amount);
			reply.error.SetNoError();
			reply.AfterMP = after;
			reply.AfterCash = 0;
			reply.AfterHBall = (short)Math.Min(short.MaxValue, LeagueService.Store.HomeRunBalls(user));
			reply.SetCardIndices(cards);
			reply.SetCardConditions(cards.Select(_ => (byte)0).ToList());
			reply.GetHBall = 0;
			log = $"{p.Name} x{amount} ({p.Cost * amount:N0} MP), opened: " + string.Join(", ", cards.Select(CoachHandlers.Label));
			return new List<Packet.PKS_PACKET> { reply };
		}

		private static List<Packet.PKS_PACKET> BuyPouch(uint user, Product p, int amount, out string log)
		{
			int balls = LeagueService.Store.HomeRunBalls(user);
			if (balls < p.Cost * amount)
			{
				return Refuse($"홈런볼이 부족합니다. ({balls}/{p.Cost * amount}개)", out log);
			}
			LeagueService.Store.AddHomeRunBalls(user, -p.Cost * amount);
			InventoryHandlers.AddItem(user, p.Item, amount);
			log = $"{p.Name} x{amount} (홈런볼 {p.Cost * amount}개) → inventory";
			return new List<Packet.PKS_PACKET> { Done(user, s_db.AddMp(user, 0)) };
		}

		private static void OpenPouch(GameSession session, Ma9MPacket.PKS_CS_USE_ITEM_LUCKMP packet)
		{
			Ma9MPacket.PKS_SC_USE_ITEM_LUCKMP reply = OpenPouch(session.Team.UserCode, packet.item_index.n, out string log);
			ServerLog.Info($"[{session.Id}] {log}");
			// No top bar here (2026-11-10, AI tester: MP 20만 too high until the next refresh): the client adds the prize to its
			// MP itself when the slot machine stops (Panel_Luckypocket.AllSlotStopAnimation), so a pushed total counted it twice.
			session.Send(reply);
		}

		// Opens one MP주머니: an amount by PouchOdds; the reply drives the client's slot machine.
		public static Ma9MPacket.PKS_SC_USE_ITEM_LUCKMP OpenPouch(uint user, int item, out string log)
		{
			Ma9MPacket.PKS_SC_USE_ITEM_LUCKMP reply = new Ma9MPacket.PKS_SC_USE_ITEM_LUCKMP();
			if (item != LuckyPouch || InventoryHandlers.ItemAmount(user, LuckyPouch) < 1)
			{
				reply.error.SetErrorMessage(item != LuckyPouch ? "열 수 없는 아이템입니다." : "MP주머니가 없습니다.");
				log = $"MP주머니 refused (item {item})";
				return reply;
			}
			int mp = RollPouch();
			int left = InventoryHandlers.AddItem(user, LuckyPouch, -1);
			s_db.AddMp(user, mp);
			reply.error.SetNoError();
			reply.RemainCount.n = (short)Math.Min(short.MaxValue, left);
			reply.GetMPAmount.n = mp;
			log = $"MP주머니 opened: {mp:N0} MP ({left} left)";
			return reply;
		}

		private static int RollPouch()
		{
			double u = s_rnd.NextDouble() * PouchOdds.Sum(o => o.pct);
			foreach ((int amount, double pct) in PouchOdds)
			{
				u -= pct;
				if (u < 0)
				{
					return amount;
				}
			}
			return PouchOdds[^1].mp;
		}

		// Several at once (user, 2026-10-19): each one at its own rising price, as if bought one by one;
		// before, only one was applied whatever the amount. Stops at the cap; refused if the MP does not
		// cover them all.
		private static List<Packet.PKS_PACKET> BuyRoster(uint user, int amount, out string log)
		{
			(short cap, int expansions, int bought) = State(user);
			int mp = s_db.AddMp(user, 0);
			if (cap >= MaxCapacity)
			{
				return Refuse($"선수 보관함은 최대 {MaxCapacity}칸입니다.", out log);
			}
			int count = Math.Min(amount, (MaxCapacity - cap + CapacityStep - 1) / CapacityStep);
			int cost = Enumerable.Range(expansions, count).Sum(ExpandCost);
			if (mp < cost)
			{
				return Refuse($"MP가 부족합니다. ({count}개 {cost:N0} MP 필요: 한 개씩 {ExpandStep / 10_000}만 MP씩 오르고 최대 {ExpandCap / 10_000}만 MP)", out log);
			}
			short next = (short)Math.Min(MaxCapacity, cap + count * CapacityStep);
			Save(user, next, expansions + count, bought);
			int after = s_db.AddMp(user, -cost);
			log = $"로스터 확장 x{count} {cap} → {next} ({cost:N0} MP)";
			// The client keeps the shop list it got on entry; send it again so the next price and the
			// new storage size show.
			return new List<Packet.PKS_PACKET> { Done(user, after), BuildEnter(user) };
		}

		// 로스터 확장권 (user, 2026-10-24, in the START2026 coupon): +20 slots, used in the inventory as the client's own
		// item 20021 (Inventory_Main says "구단이 확장되었습니다 N -> N+20"). It is free, so it does not count as a bought
		// expansion and the shop price stays where it was.
		public const int RosterTicket = 20021;

		public static bool UseRosterTicket(uint user, out short before, out short after)
		{
			(short cap, int expansions, int bought) = State(user);
			before = after = cap;
			if (cap >= MaxCapacity)
			{
				return false;
			}
			after = (short)Math.Min(MaxCapacity, cap + CapacityStep);
			Save(user, after, expansions, bought);
			return true;
		}

		// Self-check (logged by --leaguetest, on a copy of the data): packets build, every product fills
		// its item arrays, position packs give only their positions and balls, and purchases change MP,
		// cards, inventory and storage as they should for the first active team.
		public static int SelfTest(Database db, CardCatalog catalog, string dbPath)
		{
			Init(db, catalog, dbPath);
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok)
				{
					problems++;
				}
				ServerLog.Info($"shop self-test: {(ok ? "ok" : "FAIL")} {what}");
			}
			// DescriptionFits (2026-10-25): every product text goes out as a PString (one length byte, UTF-16).
			List<string> tooLong = Products.Concat(HomeRunBallProducts)
				.SelectMany(p => new[] { p.Image, p.Type, p.Name, p.Description }.Select(t => (p.Name, t)))
				.Where(x => x.t != null && System.Text.Encoding.Unicode.GetByteCount(x.t) > 255)
				.Select(x => $"{x.Name} {System.Text.Encoding.Unicode.GetByteCount(x.t)}B").ToList();
			Check(tooLong.Count == 0, $"product texts fit a PString (255 bytes){(tooLong.Count > 0 ? ": " + string.Join(", ", tooLong) : string.Empty)}");
			// 로스터 확장 100만 cap (2026-10-25): the 8th is the first at the cap; differences for expansions bought at the old
			// price, at the 200만 cap and at the 150만 cap.
			Check(ExpandCost(0) == 300_000 && ExpandCost(6) == 900_000 && ExpandCost(7) == 1_000_000 && ExpandCost(30) == 1_000_000
				&& OverCap(8) == 0 && OverCap(19) == 6_600_000 && OverCap(28) == 21_000_000
				&& OverCap(17, FirstExpandCap) == 4_500_000 && OverCap(19, FirstExpandCap) == 6_500_000 && OverCap(29, FirstExpandCap) == 16_500_000
				&& OverCap(27, SecondExpandCap) == 8_500_000 && OverCap(28, SecondExpandCap) == 9_000_000 && OverCap(37, SecondExpandCap) == 13_500_000,
				$"로스터 확장 최대 100만, 차액(옛 가격) 19번 {OverCap(19):N0} / 28번 {OverCap(28):N0}, (200만 상한) 17번 {OverCap(17, FirstExpandCap):N0} / 19번 {OverCap(19, FirstExpandCap):N0} / 29번 {OverCap(29, FirstExpandCap):N0}, (150만 상한) 27번 {OverCap(27, SecondExpandCap):N0} / 28번 {OverCap(28, SecondExpandCap):N0} / 37번 {OverCap(37, SecondExpandCap):N0} MP");
			new Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_NORMAL();
			new Ma9MPacket.PKS_CS_ENTER_SHOP_NEW_HBALL();
			new Ma9MPacket.PKS_CS_BUY_ITEM_SHOP_NEW();
			new Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_HBALL();
			foreach (Product p in Products)
			{
				PShopData data = ToShopData(p, StartCapacity, 0);
				byte[] buf = new byte[data.GetSize()];
				int at = 0;
				data.ToBytes(buf, ref at);
				PShopData back = new PShopData();
				at = 0;
				back.ToType(buf, ref at);
				Check(back.GetItemList.Count == 1 && back.GetItemAmount.Count == 1 && back.ShopItemName == p.Name && back.Cost == data.Cost, $"{p.Name} round-trips ({back.Cost:N0} MP)");
				if (p.Kind != Kind.Pack)
				{
					continue;
				}
				List<CardInfo> drawn = Draw(p, 200, new List<int>());
				int[] balls = new int[11];
				foreach (CardInfo c in drawn)
				{
					balls[c.Ball]++;
				}
				bool wrong = p.Filter != null && drawn.Any(c => !p.Filter(c) || p.Odds[c.Ball - 1] <= 0);
				Check(drawn.Count == 200 && !wrong, $"{p.Name}: 200 cards by ball " + string.Join(" ", Enumerable.Range(1, 10).Where(b => balls[b] > 0).Select(b => $"{b}:{balls[b]}")));
			}
			// 연대팩·동군·서군팩 (2026-11-11): every drawable card in exactly one decade pack and in exactly one of 동군·서군.
			Product[] decadePacks = Products.Where(p => p.Index >= 104 && p.Index <= 107).ToArray();
			Product[] halves = Products.Where(p => p.Index == 108 || p.Index == 109).ToArray();
			int strayDecade = catalog.Drawable.Count(c => decadePacks.Count(p => p.Filter(c)) != 1);
			int strayHalf = catalog.Drawable.Count(c => halves.Count(p => p.Filter(c)) != 1);
			Check(decadePacks.Length == 4 && halves.Length == 2 && strayDecade == 0 && strayHalf == 0,
				$"연대팩 4종·동서군팩: 카드 {catalog.Drawable.Count():N0}장, " + string.Join(" ", decadePacks.Concat(halves).Select(p => $"{p.Name} {catalog.Drawable.Count(p.Filter):N0}"))
				+ $", 연대 밖·겹침 {strayDecade}, 동서군 밖·겹침 {strayHalf}");

			uint user = LeagueService.Store.ActiveTeams().Select(t => t.code).FirstOrDefault();
			if (user == 0)
			{
				ServerLog.Info("shop self-test: no team for the purchase checks");
				return problems;
			}
			db.AddMp(user, 50_000_000);
			int Mp() => db.AddMp(user, 0);
			int Cards() => db.GetCards(user).Count;
			Ma9MPacket.PKS_SC_ENTER_SHOP_NEW_NORMAL enter = BuildEnter(user);
			Check(enter.ShopData.array.Length == Products.Length && enter.ShopFeatured.array.Length == Featured.Length, $"shop list {enter.ShopData.array.Length} products, cards {enter.CardCount.n}/{enter.MaxCardCount.n}");

			int mp0 = Mp(), cards0 = Cards(), hb0 = LeagueService.Store.HomeRunBalls(user);
			List<Packet.PKS_PACKET> r = Purchase(user, 101, 12, out string log);
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK pack = r[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			Check(pack != null && pack.error.error_code.n == 0 && pack.GetCardIndices.Count == 12 && Mp() == mp0 - 12 * 50_000 && Cards() == cards0 + 12
				&& pack.GetHBall >= 1 && LeagueService.Store.HomeRunBalls(user) == hb0 + pack.GetHBall, "전체 선수팩 x12: " + log);

			mp0 = Mp();
			r = Purchase(user, 111, 5, out log);
			pack = r[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			bool starters = pack != null && pack.GetCardIndices.All(i => catalog.Get(i) is CardInfo c && !c.IsBatter && c.MainPosition == CardPos.SP && c.Ball <= 7);
			Check(starters && Mp() == mp0 - 5 * 30_000, "선발 투수팩 1~7볼 x5: " + log);

			mp0 = Mp();
			// 트레이닝 카드팩 x3: opened at once, three books into the inventory (no pack left there).
			int packs0 = InventoryHandlers.ItemAmount(user, TrainingBooks.Pack);
			int books0 = InventoryHandlers.Items(user).Where(i => TrainingBooks.IsBook(i.item)).Sum(i => i.amount);
			r = Purchase(user, 201, 3, out log);
			Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK trainingPack = r[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			int books1 = InventoryHandlers.Items(user).Where(i => TrainingBooks.IsBook(i.item)).Sum(i => i.amount);
			Check(trainingPack != null && trainingPack.BuyType == eBuyType.CARDPACK_TRAINING && trainingPack.GetCardIndices.Count == 3 && trainingPack.GetCardIndices.All(TrainingBooks.IsBook)
				&& books1 == books0 + 3 && InventoryHandlers.ItemAmount(user, TrainingBooks.Pack) == packs0 && Mp() == mp0 - 90_000, "트레이닝 카드팩 x3 바로 개봉: " + log);
			mp0 = Mp();
			int charges0 = InventoryHandlers.ItemAmount(user, TrainingHandlers.TpCharge);
			r = Purchase(user, 202, 2, out log);
			Check(r[0] is Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_NONE && InventoryHandlers.ItemAmount(user, TrainingHandlers.TpCharge) == charges0 + 2 && Mp() == mp0 - 200_000, "트레이닝 횟수 충전권 x2: " + log);

			(short cap0, int exp0, _) = State(user);
			mp0 = Mp();
			r = Purchase(user, 301, 1, out log);
			Purchase(user, 301, 1, out string log2);
			(short cap1, int exp1, _) = State(user);
			Check(r.Count == 2 && cap1 == cap0 + 2 * CapacityStep && exp1 == exp0 + 2 && Mp() == mp0 - ExpandCost(exp0) - ExpandCost(exp0 + 1), $"로스터 확장 x2: {log}; {log2}");
			// Three in one purchase: three rising prices.
			(cap0, exp0, _) = State(user);
			mp0 = Mp();
			r = Purchase(user, 301, 3, out log);
			(cap1, exp1, _) = State(user);
			int three = Math.Min(3, (MaxCapacity - cap0) / CapacityStep);
			Check(three == 0 || (cap1 == cap0 + three * CapacityStep && exp1 == exp0 + three && Mp() == mp0 - Enumerable.Range(exp0, three).Sum(ExpandCost)),
				$"로스터 확장 3개 한 번에: {log}");
			// 로스터 확장권 (2026-10-24): +20 slots without counting as a bought expansion; refused at the maximum.
			(cap0, exp0, _) = State(user);
			bool ticket = UseRosterTicket(user, out short tBefore, out short tAfter);
			(cap1, exp1, _) = State(user);
			Check(cap0 >= MaxCapacity ? !ticket : ticket && tBefore == cap0 && tAfter == cap0 + CapacityStep && cap1 == tAfter && exp1 == exp0,
				$"로스터 확장권: {tBefore} → {tAfter}, 산 횟수 {exp0} → {exp1}");
			mp0 = Mp();
			int redist0 = InventoryHandlers.ItemAmount(user, ContractHandlers.RedistItem);
			r = Purchase(user, 302, 2, out log);
			Check(InventoryHandlers.ItemAmount(user, ContractHandlers.RedistItem) == redist0 + 2 && Mp() == mp0 - 200_000, $"스탯재분배권 x2: {log}");

			// The 불펜 split (2026-10-22): 중계 only, and 셋업·마무리 at 70,000 MP (after the expansions, so there is room).
			mp0 = Mp();
			pack = Purchase(user, 122, 5, out log)[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			Check(pack != null && pack.GetCardIndices.Count == 5 && pack.GetCardIndices.All(i => catalog.Get(i) is CardInfo c && !c.IsBatter && c.MainPosition == CardPos.RP && c.Ball >= 2 && c.Ball <= 8)
				&& Mp() == mp0 - 5 * 40_000, "중계 투수팩 2~8볼 x5: " + log);
			mp0 = Mp();
			pack = Purchase(user, SetupCloserPack, 5, out log)[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			Check(pack != null && pack.GetCardIndices.Count == 5 && pack.GetCardIndices.All(i => catalog.Get(i) is CardInfo c && !c.IsBatter && (c.MainPosition == CardPos.SU || c.MainPosition == CardPos.CP) && c.Ball >= 2 && c.Ball <= 8)
				&& Mp() == mp0 - 5 * 70_000, "셋업·마무리팩 x5: " + log);
			Check(Math.Abs(Closer.Sum() - 100) < 1e-9 && ItemCatalog.Get(SetupCloserPack) != null, "셋업·마무리팩 odds add up to 100% and the client's item row exists");
			mp0 = Mp();
			Purchase(user, 141, 2, out log);
			Check(Mp() == mp0 - 2 * 35_000 && Products.First(x => x.Index == 142).Cost == 45_000 && Products.First(x => x.Index == 132).Cost == 40_000,
				"외야수팩 +5천 (1~7볼 35,000, 2~8볼 45,000; 내야수팩 그대로): " + log);

			int free = State(user).cap - Cards();
			r = Purchase(user, 102, Math.Min(MaxPerPurchase, free + 1), out log);
			pack = r[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			Check(free + 1 > MaxPerPurchase || (pack != null && pack.error.error_code.n != 0), "보관함 초과 거절: " + log);

			// Exactly 25: two pouches (20) leave too few for a third, whatever the copied team had.
			LeagueService.Store.AddHomeRunBalls(user, 25 - LeagueService.Store.HomeRunBalls(user));
			int hb = LeagueService.Store.HomeRunBalls(user), pouches = InventoryHandlers.ItemAmount(user, LuckyPouch);
			r = Purchase(user, 401, 2, out log);
			Check(r[0] is Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_NONE && LeagueService.Store.HomeRunBalls(user) == hb - 20
				&& InventoryHandlers.ItemAmount(user, LuckyPouch) == pouches + 2, "MP주머니 x2 (홈런볼 20): " + log);
			r = Purchase(user, 401, 1, out log);
			Check(r[0] is Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_COMMON, "홈런볼 부족 거절: " + log);
			mp0 = Mp();
			int openedBefore = OpenedPouches(user);
			Ma9MPacket.PKS_SC_USE_ITEM_LUCKMP opened = OpenPouch(user, LuckyPouch, out log);
			Check(opened.error.error_code.n == 0 && Mp() == mp0 + opened.GetMPAmount.n && opened.RemainCount.n == pouches + 1
				&& PouchOdds.Any(o => o.mp == opened.GetMPAmount.n), "MP주머니 열기: " + log);
			// The count RefundPouches pays on: one more opened, the two bought not counted.
			Check(OpenedPouches(user) == openedBefore + 1 && PouchEv == 576_000,
				$"MP주머니 연 개수 {openedBefore} → {OpenedPouches(user)}, 평균 {PouchEv:N0} MP (예전 {OldPouchEv:N0})");
			int[] got = new int[PouchOdds.Length];
			for (int k = 0; k < 20000; k++)
			{
				int amount = RollPouch();
				got[Array.FindIndex(PouchOdds, o => o.mp == amount)]++;
			}
			Check(PouchOdds.Select((o, k) => Math.Abs(100.0 * got[k] / 20000 - o.pct)).Max() < 1.0,
				"MP주머니 20,000번: " + string.Join(" ", PouchOdds.Select((o, k) => $"{o.mp / 10_000}만 {100.0 * got[k] / 20000:F1}%")));

			db.AddMp(user, -Mp());
			r = Purchase(user, 102, 1, out log);
			pack = r[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK;
			Check(pack != null && pack.error.error_code.n != 0, "MP 부족 거절: " + log);
			return problems;
		}
	}
}
