using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// 빠른 조합 · 목표 교환 (user 11-11 21:3x via the engine: "빠른선택 - 확인 - 확인 이걸 거의 150번 정도 눌러야 4레벨 트레이닝북이 다
	// 완성되고, 이 마저도 교환을 해야하는데 빠른 조합 이런 기능이 생겨서 한 방에 싹 다 정리해주면 좋겠음"; 21:4x picks "단계를 골라서",
	// "목표 권수대로 한 번에"). The app sends PROC_TCARD_MIX whose first entry is a tag:
	//   [QuickMixTag, stop]             every grade below `stop` (II, III or IV) combined as far as the books go, lowest first,
	//                                   so the books one grade makes join the next grade's pile.
	//   [TargetTag, grade, t1 … t12]    III or IV books exchanged toward the wanted count of each stat (1-12).
	// The reply's NewCard is the number of books made (at `stop`) or exchanged; the app reloads the list and shows the
	// difference (the reply's text field holds 127 characters only).
	public static partial class TrainingHandlers
	{
		public const int QuickMixTag = -1, TargetTag = -2;

		public static bool IsQuick(int[] entries) => entries.Length > 0 && (entries[0] == QuickMixTag || entries[0] == TargetTag);

		private static void Quick(GameSession session, int[] entries)
		{
			uint user = session.Team.UserCode;
			bool mix = entries[0] == QuickMixTag;
			int arg = entries.Length > 1 ? entries[1] : 0;
			string log;
			int done;
			string refuse = mix ? TryQuickMix(user, arg, out done, out log) : TryTargetExchange(user, arg, entries.Skip(2).ToArray(), out done, out log);
			Ma9MPackets.Ma9MPacket.PKS_SC_PROC_TCARD_MIX reply = new Ma9MPackets.Ma9MPacket.PKS_SC_PROC_TCARD_MIX();
			reply.error.SetNoError();
			if (refuse != null)
			{
				reply.error.SetErrorMessage(refuse);
			}
			reply.NewCard.n = done;
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] {(mix ? "트레이닝북 빠른 조합" : "트레이닝북 목표 교환")}{(refuse != null ? " refused: " + refuse : ": " + log)}");
		}

		private static string Numeral(int grade) => grade == 1 ? "I" : grade == 2 ? "II" : grade == 3 ? "III" : grade == 4 ? "IV" : grade.ToString();

		// The books a team holds by item (grade and stat from the number).
		private static Dictionary<int, int> HeldBooks(uint user) =>
			InventoryHandlers.Items(user).Where(i => TrainingBooks.IsBook(i.item) && i.amount > 0).ToDictionary(i => i.item, i => i.amount);

		private static void Apply(uint user, Dictionary<int, int> delta)
		{
			foreach (KeyValuePair<int, int> d in delta.Where(d => d.Value != 0))
			{
				InventoryHandlers.AddItem(user, d.Key, d.Value);
			}
		}

		// The highest grade 빠른 조합 can reach for the team: IV with a 트레이닝코치, else III.
		public static int QuickTop(uint user) => TrainingBooks.TopGrade > TrainingBooks.Grades && CoachHandlers.TopMixBooks(user) > 0 ? TrainingBooks.TopGrade : TrainingBooks.Grades;

		// Books one book of grade+1 takes: four, III books by the 트레이닝코치 (5/4/3/2).
		private static int Need(uint user, int grade) => grade == TrainingBooks.Grades ? CoachHandlers.TopMixBooks(user) : MixCount;

		internal static string TryQuickMix(uint user, int stop, out int made, out string log)
		{
			made = 0;
			log = null;
			if (stop < 2 || stop > TrainingBooks.TopGrade)
			{
				return "어느 등급까지 조합할지 골라 주세요.";
			}
			if (stop > QuickTop(user))
			{
				return "IV 등급은 코칭스태프에 트레이닝코치를 등록해야 만들 수 있습니다.";
			}
			Dictionary<int, int> have = HeldBooks(user);
			Dictionary<int, int> delta = new Dictionary<int, int>();
			List<string> steps = new List<string>();
			for (int grade = 1; grade < stop; grade++)
			{
				int need = Need(user, grade);
				int total = have.Where(h => TrainingBooks.Grade(h.Key) == grade).Sum(h => h.Value), count = total / need;
				if (count == 0)
				{
					continue;
				}
				// The most plentiful stat goes first, so the few books left over are of different stats.
				for (int take = count * need; take > 0; take--)
				{
					int book = have.Where(h => TrainingBooks.Grade(h.Key) == grade && h.Value > 0).OrderByDescending(h => h.Value).ThenBy(h => h.Key).First().Key;
					have[book]--;
					delta[book] = delta.GetValueOrDefault(book) - 1;
				}
				for (int k = 0; k < count; k++)
				{
					int book = TrainingBooks.Book(s_rng.Next(1, TrainingBooks.StatNames.Length), grade + 1);
					have[book] = have.GetValueOrDefault(book) + 1;
					delta[book] = delta.GetValueOrDefault(book) + 1;
				}
				steps.Add($"{Numeral(grade)} {count * need} → {Numeral(grade + 1)} {count}");
				if (grade + 1 == stop)
				{
					made = count;
				}
			}
			if (steps.Count == 0)
			{
				return $"조합할 책이 없습니다. (같은 등급 {MixCount}권부터{(stop > TrainingBooks.Grades ? $", III은 {Need(user, TrainingBooks.Grades)}권" : string.Empty)})";
			}
			Apply(user, delta);
			List<string> news = delta.Where(d => d.Value > 0 && TrainingBooks.Grade(d.Key) == stop).OrderBy(d => d.Key).Select(d => $"{TrainingBooks.StatNames[TrainingBooks.Stat(d.Key)]} {d.Value}").ToList();
			log = $"{Numeral(stop)}까지 {string.Join(", ", steps)}; 새 {Numeral(stop)}: {string.Join(" ", news)}";
			return null;
		}

		// targets: the wanted count of each stat's book of that grade (stats 1-12 in order). Books above a stat's target
		// (all of a stat at 0 first, then the largest surplus) go to the stat furthest below its target, one at a time.
		internal static string TryTargetExchange(uint user, int grade, int[] targets, out int moved, out string log)
		{
			moved = 0;
			log = null;
			int stats = TrainingBooks.StatNames.Length - 1;
			if (grade < ExchangeGrade || grade > Math.Min(ExchangeTopGrade, TrainingBooks.TopGrade))
			{
				return "III·IV 등급 트레이닝북만 교환할 수 있습니다.";
			}
			if (targets.Length != stats || targets.Any(t => t < 0 || t > 9999))
			{
				return "목표 권수가 잘못되었습니다.";
			}
			if (targets.All(t => t == 0))
			{
				return "능력치별 목표 권수를 먼저 정해 주세요.";
			}
			int[] held = new int[stats + 1], surplus = new int[stats + 1], deficit = new int[stats + 1];
			for (int s = 1; s <= stats; s++)
			{
				held[s] = InventoryHandlers.ItemAmount(user, TrainingBooks.Book(s, grade));
				surplus[s] = Math.Max(0, held[s] - targets[s - 1]);
				deficit[s] = Math.Max(0, targets[s - 1] - held[s]);
			}
			moved = Math.Min(surplus.Sum(), deficit.Sum());
			if (moved == 0)
			{
				return deficit.Sum() == 0 ? $"{Numeral(grade)} 책이 이미 목표 권수만큼 있습니다." : $"목표보다 많은 {Numeral(grade)} 책이 없어 바꿀 책이 없습니다.";
			}
			int[] after = (int[])held.Clone();
			for (int n = 0; n < moved; n++)
			{
				int from = Enumerable.Range(1, stats).Where(s => surplus[s] > 0).OrderByDescending(s => targets[s - 1] == 0 ? 1 : 0).ThenByDescending(s => surplus[s]).ThenBy(s => s).First();
				int to = Enumerable.Range(1, stats).Where(s => deficit[s] > 0).OrderByDescending(s => deficit[s]).ThenBy(s => s).First();
				surplus[from]--;
				deficit[to]--;
				after[from]--;
				after[to]++;
			}
			Dictionary<int, int> delta = Enumerable.Range(1, stats).ToDictionary(s => TrainingBooks.Book(s, grade), s => after[s] - held[s]);
			Apply(user, delta);
			log = $"{Numeral(grade)} {moved}권: " + string.Join(", ", Enumerable.Range(1, stats).Where(s => after[s] != held[s]).Select(s => $"{TrainingBooks.StatNames[s]} {held[s]}→{after[s]}"))
				+ (deficit.Sum() > 0 ? $"; 모자람 {deficit.Sum()}" : string.Empty) + (surplus.Sum() > 0 ? $"; 목표 넘는 책 {surplus.Sum()}권 그대로" : string.Empty);
			return null;
		}
	}
}
