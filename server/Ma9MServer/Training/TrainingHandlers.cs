using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// The player window's 트레이닝 tab (PlayerInfo_SubWindow_Training): the books the team owns, and
	// training a card with one (see TrainingBooks for the rules). Books come from 트레이닝 카드팩
	// (InventoryHandlers.UseCardPack).
	public static partial class TrainingHandlers
	{
		// The client allows training only with at least one TP charge (1,000-3,000 by ball) and shows up
		// to six. Before TRAINING POINT existed the gauge was always full.
		public const short FullTp = 18000;

		// 트레이닝 횟수 충전권 (user, 2026-10-21): used from the training tab's 아이템 사용 (the original's
		// training booster list), it fills the card's six charges.
		public const int TpCharge = 30002;

		// One charge in the client's units: PlayerInfo_SubWindow_Training.decreaseValues by GetLevel(ball).
		public static int TpStep(int ball) => ball < 5 ? 1000 : ball < 8 ? 1500 : ball < 10 ? 2000 : 3000;

		// The card window's training_point: charges left × one charge.
		public static short Tp(uint userCode, int cardIdx, int ball) => (short)(s_store.TpLeft(userCode, cardIdx) * TpStep(ball));

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static TrainingStore s_store;

		private static readonly Random s_rng = new Random();

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dbPath)
		{
			s_db = db;
			s_catalog = catalog;
			s_store = new TrainingStore(dbPath);
			loop.Register<Ma9MPacket.PKS_CS_TRAINING_CARD_LIST>(List);
			loop.Register<Ma9MPacket.PKS_CS_TRY_CARD_TRAINING>(Try);
			loop.Register<Ma9MPacket.PKS_CS_USE_TRAINING_BOOSTER>(Booster);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TCARD_MIX>(EnterMix);
			loop.Register<Ma9MPacket.PKS_CS_PROC_TCARD_MIX>(Mix);
		}

		// 트레이닝카드 조합 (user, 2026-10-24: "확률 없이 같은 등급의 트레이닝북 4개를 사용해서 상위 등급의 랜덤
		// 트레이닝북"): exactly four books of one grade below III, any stats, become one book of the next grade for a
		// random stat (any of the twelve, as from a pack). The screen lists the same books as the training tab.
		public const int MixCount = 4;

		private static void EnterMix(GameSession session, Ma9MPacket.PKS_CS_ENTER_TCARD_MIX packet)
		{
			List(session, null);
		}

		private static void Mix(GameSession session, Ma9MPacket.PKS_CS_PROC_TCARD_MIX packet)
		{
			int[] books = (packet.Cards.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			// 빠른 조합 · 목표 교환 (TrainingQuick.cs, 2026-11-11).
			if (IsQuick(books))
			{
				Quick(session, books);
				return;
			}
			Ma9MPacket.PKS_SC_PROC_TCARD_MIX reply = new Ma9MPacket.PKS_SC_PROC_TCARD_MIX();
			reply.error.SetNoError();
			// A combine of III books (트레이닝코치, CoachHandlers.TopMixBooks) ends in a 0 from the client, so two of them are not
			// taken for an exchange.
			bool exchange = books.Length == ExchangeEntries && books.All(b => b != 0);
			books = books.Where(b => b != 0).ToArray();
			int made;
			string refuse = exchange ? TryExchange(session.Team.UserCode, books[0], books[1], out made) : TryMix(session.Team.UserCode, books, out made);
			string what = exchange ? "트레이닝북 교환" : "트레이닝북 조합";
			if (refuse != null)
			{
				reply.error.SetErrorMessage(refuse);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] {what} refused: {refuse}");
				return;
			}
			reply.NewCard.n = made;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] {what}: {string.Join(", ", books.Take(exchange ? 1 : books.Length).Select(TrainingBooks.Name))} → {TrainingBooks.Name(made)}");
		}

		// 트레이닝북 교환소 (user, 2026-10-25: "3단계 트레이닝 북 교환소를 하나 만들어서, 내가 가지고 있는 트레이닝 북 하나로 원하는
		// 트레이닝 북을 교환할 수 있는 컨텐츠 … 전력강화 - 트레이닝북 교환소 … 트레이닝북 조합 아래에"): one III book for the III book
		// of another stat, no fee. The client's exchange mode (전력강화 menu slot 24, the unused 코치 조합 one) sends PROC_TCARD_MIX
		// with two entries, the book given up and the book wanted; a combine always sends four.
		// IV books too since 2026-11-10 (user 02:2x: "4단계북 끼리도 교환소를 사용할 수 있도록"): a book for one of its own grade.
		public const int ExchangeEntries = 2, ExchangeGrade = 3, ExchangeTopGrade = 4;

		// The menu slot, shown only to builds that add this mark to their login's bundle_version: an older app would open
		// the 코치 조합 scene there.
		public const byte ExchangeMenu = 24;

		public const string ExchangeMark = "ma9m-tbx1";

		public static bool ClientSupportsExchange(GameSession session) => session?.ClientVersion?.Contains(ExchangeMark) == true;

		internal static string TryExchange(uint user, int given, int wanted, out int made)
		{
			made = 0;
			string refuse =
				!TrainingBooks.IsBook(given) || !TrainingBooks.IsBook(wanted) ? "트레이닝북만 교환할 수 있습니다." :
				TrainingBooks.Grade(given) < ExchangeGrade || TrainingBooks.Grade(given) > ExchangeTopGrade ? "III·IV 등급 트레이닝북만 교환할 수 있습니다." :
				TrainingBooks.Grade(wanted) != TrainingBooks.Grade(given) ? "같은 등급의 트레이닝북으로만 교환할 수 있습니다." :
				given == wanted ? "같은 트레이닝북으로는 바꿀 수 없습니다." :
				InventoryHandlers.ItemAmount(user, given) < 1 ? "트레이닝북이 없습니다." : null;
			if (refuse != null)
			{
				return refuse;
			}
			InventoryHandlers.AddItem(user, given, -1);
			InventoryHandlers.AddItem(user, wanted, 1);
			made = wanted;
			return null;
		}

		// The books used up and the new one in the inventory; the refusal shown to the user, or null.
		internal static string TryMix(uint user, int[] books, out int made)
		{
			made = 0;
			// III books make a IV with a 트레이닝코치 (코칭스태프, 2026-11-04): 5 / 4 / 3 / 2 of them by his grade.
			int grade = books.Length > 0 && TrainingBooks.IsBook(books[0]) ? TrainingBooks.Grade(books[0]) : 0;
			int need = grade == TrainingBooks.Grades ? CoachHandlers.TopMixBooks(user) : MixCount;
			string refuse =
				books.Length == 0 ? $"같은 등급의 트레이닝북 {MixCount}권을 올려 주세요." :
				books.Any(b => !TrainingBooks.IsBook(b)) ? "트레이닝북만 조합할 수 있습니다." :
				books.Select(TrainingBooks.Grade).Distinct().Count() != 1 ? "같은 등급의 트레이닝북끼리만 조합할 수 있습니다." :
				grade >= TrainingBooks.TopGrade ? $"{(grade == 3 ? "III" : "IV")} 등급 트레이닝북은 더 올릴 수 없습니다." :
				need == 0 ? "III 등급 트레이닝북은 트레이닝코치를 등록해야 조합할 수 있습니다." :
				books.Length != need ? $"같은 등급의 트레이닝북 {need}권을 올려 주세요." :
				books.GroupBy(b => b).Any(g => InventoryHandlers.ItemAmount(user, g.Key) < g.Count()) ? "트레이닝북이 모자랍니다." : null;
			if (refuse != null)
			{
				return refuse;
			}
			foreach (IGrouping<int, int> g in books.GroupBy(b => b))
			{
				InventoryHandlers.AddItem(user, g.Key, -g.Count());
			}
			made = TrainingBooks.Book(s_rng.Next(1, TrainingBooks.StatNames.Length), TrainingBooks.Grade(books[0]) + 1);
			InventoryHandlers.AddItem(user, made, 1);
			return null;
		}

		// For the card window (CARD_DETAIL_INFO): the card's three slots.
		public static void FillSlots(uint userCode, int cardIdx, PArray<PInteger> cards, PArray<PByte> effects)
		{
			(int item, int result)[] slots = s_store.Get(userCode, cardIdx);
			cards.array = slots.Select(s => new PInteger { n = s.item }).ToArray();
			effects.array = slots.Select(s => new PByte { n = (byte)s.result }).ToArray();
		}

		private static void List(GameSession session, Ma9MPacket.PKS_CS_TRAINING_CARD_LIST packet)
		{
			// The client keeps the batter or pitcher books by itself (trainingEffectStat).
			List<(int item, int amount)> books = InventoryHandlers.Items(session.Team.UserCode).Where(i => TrainingBooks.IsBook(i.item)).ToList();
			Ma9MPacket.PKS_SC_TRAINING_CARD_LIST reply = new Ma9MPacket.PKS_SC_TRAINING_CARD_LIST();
			reply.training_card_list.array = books.Select(b => new PInteger { n = b.item }).ToArray();
			reply.amount_list.array = books.Select(b => new PShort { n = (short)Math.Min(short.MaxValue, b.amount) }).ToArray();
			FillBoosters(session.Team.UserCode, reply.training_booster_list, reply.training_booster_amount);
			session.Send(reply);
		}

		// The training tab's 아이템 사용 list: the 충전권, also at 0 (user, 2026-10-21: after the last one was used the
		// list still showed x1). An empty array arrives as null (PArray.ToType) and the client's
		// SetUseTrainingBooster then keeps its old list; a row at 0 replaces it, and the popup skips 0 rows.
		internal static void FillBoosters(uint user, PArray<PInteger> list, PArray<PShort> amounts)
		{
			int charges = InventoryHandlers.ItemAmount(user, TpCharge);
			list.array = new[] { new PInteger { n = TpCharge } };
			amounts.array = new[] { new PShort { n = (short)Math.Min(short.MaxValue, charges) } };
		}

		private static void Try(GameSession session, Ma9MPacket.PKS_CS_TRY_CARD_TRAINING packet)
		{
			uint user = session.Team.UserCode;
			int cardIdx = packet.card_index.n, item = packet.training_card_index.n, slot = packet.training_card_slot.n;
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING reply = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
			reply.card_index.n = cardIdx;
			reply.training_card_index.n = item;
			reply.training_point.n = FullTp;
			reply.training_stat.sbytes = new sbyte[6];
			reply.training_cards.array = Array.Empty<PInteger>();
			reply.training_effect.array = Array.Empty<PByte>();
			reply.encounter_coach.n = 0;
			reply.transaction_id.n = 0;
			reply.CoachMent_Encounter.n = 0;
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == cardIdx);
			CardInfo info = s_catalog.Get(cardIdx);
			string problem =
				card == null || info == null ? "보유한 선수가 아닙니다." :
				!TrainingBooks.IsBook(item) ? "트레이닝북이 아닙니다." :
				TrainingBooks.ForPitcher(item) == info.IsBatter ? (info.IsBatter ? "타자에게는 타자용 트레이닝북만 쓸 수 있습니다." : "투수에게는 투수용 트레이닝북만 쓸 수 있습니다.") :
				slot < 0 || slot >= TrainingStore.Slots ? "트레이닝 슬롯이 잘못되었습니다." :
				InventoryHandlers.ItemAmount(user, item) < 1 ? "트레이닝북이 없습니다." :
				s_store.TpLeft(user, cardIdx) < 1 ? "TRAINING POINT가 없습니다. 트레이닝 횟수 충전권을 써 주세요." :
				null;
			if (info != null)
			{
				reply.training_point.n = Tp(user, cardIdx, info.Ball);
			}
			if (problem != null)
			{
				reply.result.n = 1;
				reply.error_info.SetErrorMessage(problem);
				session.Send(reply);
				ServerLog.Info($"[{session.Id}] training refused: card {cardIdx}, book {item}, slot {slot}: {problem}");
				return;
			}
			// The four squares at 25% each, or the 트레이닝코치's odds for his club's cards.
			int result = CoachHandlers.RollResult(user, info, s_rng);
			InventoryHandlers.AddItem(user, item, -1);
			(int item, int result)[] slots = s_store.Apply(user, cardIdx, slot, item, result, out sbyte[] training);
			int left = s_store.UseTp(user, cardIdx);
			reply.training_point.n = (short)(left * TpStep(info.Ball));
			Missions.Add(user, Missions.Trainings);  // 업적
			reply.result.n = 0;
			reply.training_result.n = (byte)result;
			reply.training_stat.sbytes = training;
			reply.training_cards.array = slots.Select(s => new PInteger { n = s.item }).ToArray();
			reply.training_effect.array = slots.Select(s => new PByte { n = (byte)s.result }).ToArray();
			reply.error_info.SetNoError();
			// 타격·투수코치 (코칭스태프): may come to raise the result for SP.
			CoachHandlers.OfferTraining(user, info, cardIdx, slot, item, result, reply);
			session.Send(reply);
			int value = TrainingBooks.Value(item, result);
			ServerLog.Info($"[{session.Id}] training {info.Year} {info.Name} slot {slot + 1}: {TrainingBooks.Name(item)} → {(value > 0 ? "+" : string.Empty)}{value}, training [{string.Join(" ", training)}], TP {left}/{TrainingStore.TpCharges}");
		}

		// 아이템 사용 in the training tab: the 트레이닝 횟수 충전권 fills the card's charges (item_index 0: no
		// lasting booster, so the tab keeps saying none is in use).
		private static void Booster(GameSession session, Ma9MPacket.PKS_CS_USE_TRAINING_BOOSTER packet)
		{
			uint user = session.Team.UserCode;
			int cardIdx = packet.player.n, item = packet.training_booster.n;
			Ma9MPacket.PKS_SC_USE_TRAINING_BOOSTER reply = new Ma9MPacket.PKS_SC_USE_TRAINING_BOOSTER();
			string problem = TryCharge(user, cardIdx, item, out short tp);
			reply.item_index.n = 0;
			reply.training_point.n = tp;
			// Why it was refused (user, 2026-10-21: a 충전권 used on a full card stayed at x1 and looked unused):
			// 2 = the card's gauge is full (the ticket is kept), 3 = no ticket, 1 = anything else. PlayerInfo_UseOfItem
			// says which; older clients show their one failure message for any of them.
			reply.result.n = (byte)(problem == null ? 0 : problem == Full ? 2 : problem == NoTicket ? 3 : 1);
			FillBoosters(user, reply.training_booster_list, reply.training_booster_amount);
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] training charge card {cardIdx}, item {item}: {problem ?? "ok"} (TP {tp})");
		}

		private const string Full = "TRAINING POINT가 이미 가득 찼습니다.", NoTicket = "트레이닝 횟수 충전권이 없습니다.";

		// Null = the card's six charges are back; tp = its gauge afterwards.
		public static string TryCharge(uint user, int cardIdx, int item, out short tp)
		{
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == cardIdx);
			CardInfo info = s_catalog.Get(cardIdx);
			tp = info != null ? Tp(user, cardIdx, info.Ball) : (short)0;
			string problem =
				card == null || info == null ? "보유한 선수가 아닙니다." :
				item != TpCharge ? "쓸 수 없는 아이템입니다." :
				InventoryHandlers.ItemAmount(user, TpCharge) < 1 ? NoTicket :
				s_store.TpLeft(user, cardIdx) >= TrainingStore.TpCharges ? Full :
				null;
			if (problem != null)
			{
				return problem;
			}
			InventoryHandlers.AddItem(user, TpCharge, -1);
			s_store.RefillTp(user, cardIdx);
			tp = Tp(user, cardIdx, info.Ball);
			return null;
		}

		// The charges a card has left (self-tests).
		public static int TpLeft(uint user, int cardIdx) => s_store.TpLeft(user, cardIdx);

		// A card's three slots, and one slot set again (코치 트레이닝, CoachHandlers.TryTake): the card's new training sums.
		internal static (int item, int result)[] SlotsOf(uint user, int cardIdx) => s_store.Get(user, cardIdx);

		internal static sbyte[] SetSlot(uint user, int cardIdx, int slot, int item, int result)
		{
			s_store.Apply(user, cardIdx, slot, item, result, out sbyte[] training);
			return training;
		}
	}
}
