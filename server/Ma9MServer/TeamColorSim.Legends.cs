using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// 2026-11-10 (user: "각 덱별로 투, 타 레전드 카드를 사용했을 때 가장 승률이 높은 로스터를 만들어줘 … 01현대 … 박재홍도 추가하게
	// 되면 클린업에서 누가 또 빠져야함, 5볼 3루수 퀸란이 빠지는게 맞는지 … 박경완이 하위로 빠지는게 맞는지 … 필립스랑 01 하위 이명수를
	// 비교"): 레전드 cards and chosen batters in the entry and plan decks.
	// - A card token is a name in the deck's pool (with or without its 동명이인 letter) or a card idx, which may be any card of the
	//   tables: a 레전드 (800101-800299, year 1981, so outside every team-year pool and Drawable) or another season's card.
	// - TCSIM_BATS="<deck>#<tag>=<nine token@spot in batting order>[/<bench tokens>]", spot C 1B 2B 3B SS LF CF RF DH. A leading
	//   "*" lets BestOrder order those nine. Without a bench the deck's bench stays, a bench card now in the nine giving its place
	//   to the one it pushed out.
	// - TCSIM_STAFF names may be card idx too (a 레전드 pitcher: "800108@RP").
	// - The same player twice (sidx; the app refuses it, Roster_PlayerMap_Change_DuplicatePlayer) or more than one 레전드
	//   batter or pitcher (RosterHandlers.MaxLegendBatters/Pitchers) is refused.
	public static partial class TeamColorSim
	{
		private static Dictionary<int, Cand> s_anyCard;

		private static readonly object s_anyCardGate = new object();

		// Any card of the tables as a candidate (레전드 included), with the catcher bonus the pools carry (WithDevCatcher).
		private static Cand AnyCard(int idx)
		{
			lock (s_anyCardGate)
			{
				s_anyCard ??= new Dictionary<int, Cand>();
				if (!s_anyCard.TryGetValue(idx, out Cand c))
				{
					CardInfo info = s_data.Catalog.Get(idx);
					c = info == null ? null : Make(info);
					if (c != null) c = WithDevCatcher(new List<Cand> { c })[0];
					s_anyCard[idx] = c;
				}
				return c;
			}
		}

		// A batter or pitcher by token (see above), or null.
		private static Cand FindCard(string token, bool batter, List<Cand> pool)
		{
			string t = token.Trim();
			if (int.TryParse(t, out int idx))
			{
				Cand byIdx = pool.FirstOrDefault(x => x.Info.Idx == idx) ?? AnyCard(idx);
				return byIdx != null && byIdx.Info.IsBatter == batter ? byIdx : null;
			}
			return pool.FirstOrDefault(x => x.Info.IsBatter == batter && x.Info.Name == t)
				?? pool.FirstOrDefault(x => x.Info.IsBatter == batter && NamesakeBase(x.Info.Name) == t);
		}

		private static Dictionary<string, string> BatsSpecs() => (Environment.GetEnvironmentVariable("TCSIM_BATS") ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2)
			.ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());

		// The deck with the nine (and bench) of a TCSIM_BATS spec; order = the fixed batting order (spot per slot), or null
		// for BestOrder. Returns an error text instead when a token or spot is wrong.
		private static string ApplyBats(Deck own, string spec, List<Cand> pool, out int[] order)
		{
			order = null;
			bool auto = spec.StartsWith("*");
			string[] parts = spec.TrimStart('*').Split('/');
			string[] nine = parts[0].Split(',', StringSplitOptions.RemoveEmptyEntries);
			if (nine.Length != 9) return $"타자 {nine.Length}명(9명이어야 함)";
			Cand[] before = own.At.Take(FirstPitcher).ToArray();
			int[] fixedOrder = new int[9];
			HashSet<int> spots = new HashSet<int>();
			for (int k = 0; k < 9; k++)
			{
				string[] ts = nine[k].Split('@');
				if (ts.Length != 2) return $"{nine[k]}: 자리(@C …)가 없음";
				int s = Array.IndexOf(SpotNames, ts[1].Trim().ToUpperInvariant());
				if (s < 0) return $"{nine[k]}: 자리 {ts[1]}를 모름";
				if (!spots.Add(s)) return $"{SpotNames[s]} 자리가 두 번";
				Cand c = FindCard(ts[0], true, pool);
				if (c == null) return $"{ts[0]} 없음";
				own.At[s] = c;
				fixedOrder[k] = s;
			}
			if (parts.Length > 1)
			{
				string[] bench = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries);
				if (bench.Length > 5) return $"벤치 {bench.Length}명(5명까지)";
				for (int b = 0; b < bench.Length; b++)
				{
					Cand c = FindCard(bench[b], true, pool);
					if (c == null) return $"벤치 {bench[b]} 없음";
					own.At[Bench + b] = c;
				}
			}
			else
			{
				// The deck's bench; a bench card now in the nine gives its place to one of the nine it pushed out.
				Queue<Cand> pushed = new Queue<Cand>(before.Take(9).Where(c => c != null && !own.At.Take(9).Contains(c)));
				for (int b = Bench; b < Bench + 5; b++)
				{
					if (own.At.Take(9).Contains(own.At[b])) own.At[b] = pushed.Count > 0 ? pushed.Dequeue() : null;
				}
			}
			List<Cand> cards = own.At.Where(c => c != null).ToList();
			var twice = cards.GroupBy(c => (c.Info.IsBatter, c.Info.Sidx)).Where(g => g.Count() > 1).Select(g => string.Join("·", g.Select(c => $"{c.Info.Name}({c.Info.Idx})"))).ToList();
			if (twice.Count > 0) return "같은 선수 두 장: " + string.Join(", ", twice);
			string legendProblem = LegendLimit(cards);
			if (legendProblem != null) return legendProblem;
			order = auto ? null : fixedOrder;
			return null;
		}

		// More than one 레전드 batter or pitcher in the deck (RosterHandlers.MaxLegendBatters/Pitchers), or null.
		private static string LegendLimit(IEnumerable<Cand> cards)
		{
			List<CardInfo> legends = cards.Where(c => c != null && c.Info.IsLegend).Select(c => c.Info).Distinct().ToList();
			int bats = legends.Count(c => c.IsBatter), arms = legends.Count - bats;
			return bats > RosterHandlers.MaxLegendBatters || arms > RosterHandlers.MaxLegendPitchers
				? $"레전드 타자 {bats}명·투수 {arms}명(1명씩까지)" : null;
		}

		// "1.박진만7 SS, … | 벤치 …" for a deck and order.
		private static string BatsText(Deck d, int[] order) =>
			string.Join(", ", Enumerable.Range(0, 9).Select(i => $"{i + 1}.{SpotText(d.At[order[i]], order[i])}"))
			+ " | 벤치 " + string.Join(" ", Enumerable.Range(Bench, 5).Where(s => d.At[s] != null).Select(s => $"{d.At[s].Info.Name}{d.At[s].Info.Ball}"));
	}
}
