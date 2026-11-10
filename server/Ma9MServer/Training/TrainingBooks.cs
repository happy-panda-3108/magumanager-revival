using System;
using System.Collections.Generic;

namespace Ma9MServer
{
	// 트레이닝 (user's design, 2026-10-18):
	// - A 트레이닝 카드팩 gives one 트레이닝북 for one of the 12 stats, grade I / II / III at 50 / 30 / 20 %.
	// - Training with a book raises its stat by one of four values, each equally likely:
	//   I −1 … +2, II 0 … +3, III +1 … +4.
	// - A card has three training slots; training on a slot that already holds a result replaces it.
	// The four values are the client's four result squares (PlayerInfo_TrainingResult), and the grades
	// are its eTrainingCardGrade GRADE_1 … GRADE_3, which number the squares exactly this way.
	public static class TrainingBooks
	{
		public const int Pack = 30001;

		private const int BookBase = 31000;

		public const int Grades = 3;

		// IV (+2 … +5) books come only from a 트레이닝코치's 조합 (CoachHandlers.TopMixBooks); packs still give I-III.
		public static int TopGrade => CoachCatalog.Enabled ? 4 : Grades;

		// trainingEffectStat 1-12, the client's order: batter 1-6 then pitcher 7-12. Each half follows
		// the card's stat arrays (TeamCard.Training), so a book's slot there is (stat - 1) % 6.
		// Names as the client shows them (user, 2026-10-19: 주력 → 스피드, 정신 → H정신 / P정신).
		public static readonly string[] StatNames =
		{
			null,
			"컨택", "파워", "스피드", "선구", "수비", "H정신",
			"체력", "구속", "컨트롤", "테크닉", "구위", "P정신"
		};

		private static readonly string[] Numerals = { null, "I", "II", "III", "IV" };

		// Out of 100: grade I, II, III.
		private static readonly int[] GradeOdds = { 50, 30, 20 };

		public static int Book(int stat, int grade) => BookBase + stat * 10 + grade;

		public static int Stat(int item) => (item - BookBase) / 10;

		public static int Grade(int item) => (item - BookBase) % 10;

		public static bool IsBook(int item) => item > BookBase && Stat(item) >= 1 && Stat(item) <= 12 && Grade(item) >= 1 && Grade(item) <= TopGrade;

		public static bool ForPitcher(int item) => Stat(item) >= 7;

		public static int StatSlot(int item) => (Stat(item) - 1) % 6;

		// The stat change for result square 0-3.
		public static int Value(int item, int result) => Grade(item) - 2 + result;

		public static string Name(int item) => $"{StatNames[Stat(item)]} 트레이닝북 {Numerals[Grade(item)]}";

		public static int RollBook(Random rng)
		{
			int stat = rng.Next(1, StatNames.Length);
			int roll = rng.Next(100), grade = 1;
			while (grade < Grades && roll >= GradeOdds[grade - 1])
			{
				roll -= GradeOdds[grade - 1];
				grade++;
			}
			return Book(stat, grade);
		}

		public static int RollResult(Random rng) => rng.Next(4);

		// Rows for ma9_itemlist.txt: the pack, then 36 books (stat by stat, I-III; with coaches 48, I-IV).
		public static IEnumerable<ItemDef> Items()
		{
			yield return new ItemDef
			{
				Index = Pack, Name = "트레이닝 카드팩", Type = ItemType.TrainingCardPack, TypeText = "트레이닝 카드팩", File = "pack_training",
				Desc = "12가지 능력치 중 하나를 올리는 트레이닝북 1권을 얻습니다. 트레이닝북 I 50%, II 30%, III 20%."
			};
			for (int stat = 1; stat < StatNames.Length; stat++)
			{
				for (int grade = 1; grade <= TopGrade; grade++)
				{
					int item = Book(stat, grade);
					yield return new ItemDef
					{
						Index = item, Name = Name(item), Type = ItemType.Training, TypeText = "트레이닝북", File = $"book_{stat:00}_{grade}",
						TrainingStat = stat, TrainingGrade = grade,
						Desc = $"{(stat >= 7 ? "투수" : "타자")}용. {StatNames[stat]} 능력치가 "
							+ $"{Signed(Value(item, 0))}, {Signed(Value(item, 1))}, {Signed(Value(item, 2))}, {Signed(Value(item, 3))} 중 하나만큼 바뀝니다(각 25%). "
							// 2026-11-09 (AI tester could not find how to redo a slot; the user chose "칸을 눌러 고르게"): how to redo one.
							+ "선수 정보 > 트레이닝에서 위 3칸 중 하나에 올려 사용합니다. 이미 쓴 칸은 그 칸을 눌러 고른 뒤 책을 누르거나, "
							+ "책을 꾹 눌러 그 칸에 끌어다 놓으면 다시 하며 결과를 덮어씁니다."
					};
				}
			}
		}

		private static string Signed(int v) => v > 0 ? "+" + v : v.ToString();
	}
}
