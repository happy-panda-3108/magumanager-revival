using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ma9MServer
{
	// eItemType values the client knows (TableReader.eItemType).
	public enum ItemType
	{
		Common = 0, Training = 1, TrainingBooster = 2, ManagePlayer = 3, PlayerCardPack = 4, TrainingCardPack = 5,
		Event = 6, SupportCardPack = 7, Fusion = 8, Contract = 9, Lesson = 10, CustomCardPack = 11, ClubItem = 12,
		PositionTraining = 13, CoachCardPack = 14, EtcCardPack = 15
	}

	public sealed class ItemDef
	{
		public int Index;

		public string Name;

		public ItemType Type;

		// The small grey line above the name in the inventory.
		public string TypeText;

		// Pictures: <cdn>/KBO/ItemImage/<ver>/InvenItem/<File>.png (large) and InvenItem/small/<File>.png.
		public string File;

		public string Desc;

		// Training books (TrainingBooks): the client's trainingEffectStat 1-12 and eTrainingCardGrade 1-3.
		public int TrainingStat;

		public int TrainingGrade;

		// 컨디션 회복제 (PlayerManageHandlers): the client's conditionEffect (steps) and hour (how long it lasts).
		public int ConditionSteps;

		public int Hours;
	}

	// Items of this version. The client reads them from ma9_itemlist.txt (TableReader.ItemListTable),
	// which the server writes into data/tables at startup; add a row here for a new item.
	public static class ItemCatalog
	{
		// Player card packs (user's design, 2026-10-18): 전체 1~10볼 and 고급 4~10볼, one card each.
		public const int AllPlayersPack = 10021;

		public const int HighPlayersPack = 10031;

		// 레전드 선수팩 (user, 2026-11-08: "상점 레전드 카드팩 … 3~10볼 카드팩이고, 3~10볼 전체 선수 + 레전드 선수카드가 존재해서, 일정
		// 확률로 레전드 선수를 얻을 수 있었음"; "7만 MP로 하고, 2천만 MP가 레전드 1장의 기댓값으로"): CardPacks.LegendChance.
		public const int LegendPlayersPack = 10041;

		public static readonly ItemDef[] Items = new ItemDef[]
		{
			new ItemDef
			{
				Index = AllPlayersPack, Name = "전체 카드 선수팩", Type = ItemType.PlayerCardPack, TypeText = "선수 카드팩", File = "pack_all",
				Desc = "1~10볼 선수 카드 1장을 얻습니다. 모든 포지션, 모든 시즌의 선수가 나옵니다."
			},
			new ItemDef
			{
				Index = HighPlayersPack, Name = "고급 카드 선수팩", Type = ItemType.PlayerCardPack, TypeText = "선수 카드팩", File = "pack_high",
				Desc = "4~10볼 선수 카드 1장을 얻습니다. 높은 볼의 선수가 나올 확률이 높습니다."
			},
			new ItemDef
			{
				Index = LegendPlayersPack, Name = "레전드 선수팩", Type = ItemType.PlayerCardPack, TypeText = "선수 카드팩", File = "pack_legend",
				Desc = "3~10볼 선수 카드 1장, 낮은 확률로 레전드 선수 카드를 얻습니다. 레전드 선수는 이 팩에서만 나옵니다."
			},
			// 로스터 확장권 (user, 2026-10-24): the client's item 20021, used from the inventory (ShopHandlers.UseRosterTicket).
			new ItemDef
			{
				Index = ShopHandlers.RosterTicket, Name = "로스터 확장권", Type = ItemType.Common, TypeText = "확장 아이템", File = "roster_ticket",
				Desc = $"선수 보관함을 {ShopHandlers.CapacityStep}칸 늘립니다(최대 {ShopHandlers.MaxCapacity:N0}칸). 상점의 로스터 확장 가격은 오르지 않습니다."
			}
		}.Concat(TrainingBooks.Items()).Concat(ShopOnlyItems()).Concat(HomeRunBallShopItems()).Concat(PlayerManageHandlers.Items()).Concat(LessonHandlers.Items()).Concat(PositionChangeHandlers.Items()).Concat(CoachHandlers.Items()).ToArray();

		// Shop products that never go into the inventory (ShopHandlers.Products: position packs open at
		// once, 301 is the 로스터 확장). The client's buy button looks each product's item up in this
		// table (Shop_Main.OnBtn_Buy) and stopped with a NullReferenceException when it was missing,
		// so nothing was sent (user, 2026-10-19: 로스터 확장 could not be bought).
		private static IEnumerable<ItemDef> ShopOnlyItems()
		{
			string[] packs = { "선발 투수팩", "중계 투수팩", "내야수팩", "외야수팩" };
			for (int p = 0; p < packs.Length; p++)
			{
				yield return new ItemDef { Index = 111 + 10 * p, Name = packs[p] + " 1~7볼", Type = ItemType.PlayerCardPack, TypeText = "포지션팩", File = "pack_all", Desc = "상점 상품" };
				yield return new ItemDef { Index = 112 + 10 * p, Name = packs[p] + " 2~8볼", Type = ItemType.PlayerCardPack, TypeText = "포지션팩", File = "pack_all", Desc = "상점 상품" };
			}
			yield return new ItemDef { Index = ShopHandlers.SetupCloserPack, Name = "셋업·마무리팩 2~8볼", Type = ItemType.PlayerCardPack, TypeText = "포지션팩", File = "pack_all", Desc = "상점 상품" };
			yield return new ItemDef { Index = 301, Name = "로스터 확장 +20", Type = ItemType.Common, TypeText = "확장", File = "pack_all", Desc = "상점 상품" };
			// 트레이닝 횟수 충전권 (user, 2026-10-21): used in the player window's 트레이닝 tab (TrainingHandlers).
			yield return new ItemDef
			{
				Index = TrainingHandlers.TpCharge, Name = "트레이닝 횟수 충전권", Type = ItemType.TrainingBooster, TypeText = "트레이닝 아이템", File = "tp_charge",
				Desc = "선수 한 명의 TRAINING POINT 6칸을 다시 채웁니다. 선수 정보 > 트레이닝의 아이템 버튼으로 씁니다."
			};
			// 스탯재분배권 (user, 2026-10-19: 10만 MP): used in 계약관리 (ContractHandlers.RedistItem).
			yield return new ItemDef
			{
				Index = ContractHandlers.RedistItem, Name = "스탯재분배권", Type = ItemType.Contract, TypeText = "계약 아이템", File = "stat_redist",
				Desc = "계약으로 얻은 보너스 능력치를 다시 나눕니다. 계약관리에서 선수를 고른 뒤 재분배 버튼으로 사용합니다."
			};
		}

		// The 홈런볼 상점's MP주머니 (systems session, ShopHandlers.LuckyPouch). A card-pack type, so the
		// inventory opens it with USE_ITEM_LUCKMP (client Inventory_Main special-cases 90981).
		private static IEnumerable<ItemDef> HomeRunBallShopItems()
		{
			yield return new ItemDef
			{
				Index = ShopHandlers.LuckyPouch, Name = "MP주머니", Type = ItemType.EtcCardPack, TypeText = "행운의 주머니", File = "luckybag_mp",
				Desc = "열면 1만~1,000만 MP 중 하나가 나옵니다. 홈런볼 상점에서 홈런볼 10개로 바꿉니다."
			};
		}

		private static readonly Dictionary<int, ItemDef> s_byIndex = new Dictionary<int, ItemDef>();

		static ItemCatalog()
		{
			foreach (ItemDef item in Items)
			{
				s_byIndex[item.Index] = item;
			}
		}

		public static ItemDef Get(int index) => s_byIndex.TryGetValue(index, out ItemDef item) ? item : null;

		// ma9_itemlist.txt: index, name, type, type_text, filename, filename_small, hour,
		// playerShopCardIndex, trainingIndex, trainingGrade, trainingEffectStat, conditionEffect,
		// trainingBoosterEffect, trainingBoosterBallEffect, get_cash, get_point, desc.
		public static void WriteTable(string tablesDir)
		{
			StringBuilder sb = new StringBuilder("index\tname\ttype\ttype_text\tfilename\tfilename_small\thour\tshop_card\ttraining_index\ttraining_grade\ttraining_stat\tcondition\tbooster\tbooster_ball\tget_cash\tget_point\tdesc\n");
			foreach (ItemDef i in Items)
			{
				// The client also keys training books by trainingIndex; the item index is unique.
				int trainingIndex = i.Type == ItemType.Training ? i.Index : 0;
				sb.Append($"{i.Index}\t{i.Name}\t{(int)i.Type}\t{i.TypeText}\t{i.File}\t{i.File}\t{i.Hours}\t0\t{trainingIndex}\t{i.TrainingGrade}\t{i.TrainingStat}\t{i.ConditionSteps}\t0\t0\t0\t0\t{i.Desc}\n");
			}
			string path = Path.Combine(tablesDir, "ma9_itemlist.txt");
			string text = sb.ToString();
			if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
			{
				File.WriteAllText(path, text, new UTF8Encoding(false));
			}
		}
	}
}
