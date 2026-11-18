using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ma9MSim;

namespace Ma9MServer
{
	// FA 선수카드 in the entry mode (engine session, 2026-11-15; user: "FA 선수들이 구현되면, 현재 있는 육성표를 기준으로 각 팀마다 어떤
	// FA 선수를 사용하는게 승률이 가장 크게 오르는지, 2순위는 어떤 선수인지 등 분석해서 육성표에 추가해주길 바람").
	// With TCSIM_FA="<batters>,<pitchers>" every TCSIM_DECKS deck without "#FA" in its label also plays its FA variants: one FA
	// card (team 250, CardInfo.IsFA) in one spot, the rest of the deck, its 육성 (TCSIM_DEV), batting order and 포지션 변경 as
	// they are. A batter FA takes a fielding spot or DH (the man there leaves the 1군), or takes a spot whose man moves to DH
	// in place of the DH ("+DH"); a pitcher FA takes a spot of his own role (선발 → SP1-5, 불펜 → RP1-SU-CP). The FA card is
	// developed as DevelopCard develops a card (팀컬러 included: an FA counts for any group). The same player twice (sidx) is
	// skipped.
	// Screening: every variant of every FA card valued whole as the games build the team (DevTeam: 육성 bonuses, the batting
	// graph and roles, league reference, 특성) by TeamValue.Of against a few field decks, less the deck as it is; each FA card
	// keeps its best variant and the best `batters` and `pitchers` play. (Card values alone missed the book's development and
	// the batting graph: '03 김동주A for 25LG's developed 8볼 3B 구본혁 screened +0.74 runs and played −0.85%p.)
	// TCSIM_FA_PICK="<deck>=<key>,<key>;…" plays only those (key "<fa idx>@<spot>[+DH]" or "<fa idx>@SP3" …), for the final run.
	public static partial class TeamColorSim
	{
		// An FA card's 재계약 gives 5 points, six times (user 2026-11-15 20시 전: "계약 연장때마다 5의 보너스 스탯 … 6회").
		private const int FaRenewal = 5;

		private static readonly string[] FaBatStats = { "컨택", "파워", "스피드", "선구", "수비", "정신" };
		private static readonly string[] FaPitStats = { "체력", "구속", "컨트롤", "테크닉", "구위", "정신" };

		// "특강 컨택+5·파워+2·선구+2 / 트레이닝 … / 재계약 …" (TeamCard order), for the books.
		private static string FaDevText(TeamCard d, bool batter)
		{
			string[] names = batter ? FaBatStats : FaPitStats;
			string Part(string label, sbyte[] v) => label + " " + string.Join("·", Enumerable.Range(0, 6).Where(k => v[k] != 0).Select(k => $"{names[k]}+{v[k]}"));
			return $"{Part("특강", d.Educated)} / {Part("트레이닝", d.Training)} / {Part("재계약", d.Contract)}";
		}

		private static bool FaMode => Environment.GetEnvironmentVariable("TCSIM_FA") != null || Environment.GetEnvironmentVariable("TCSIM_FA_PICK") != null;

		// The deck's strength in runs a game as TeamValue reads the built game, against each reference deck (averaged).
		private static double FaValue(DevDeck x, List<DevDeck> refs)
		{
			double sum = 0;
			foreach (DevDeck r in refs)
			{
				GameTeam ta = DevTeam(x, 0), tb = DevTeam(r, 0);
				TeamStrength.ApplyLeagueReference(new[] { ta, tb });
				TraitEffects.Raise(ta, x.Traits);
				TraitEffects.Raise(tb, r.Traits);
				SeasonModel m = CardGame.Build(ta, tb, s_data.Env);
				TraitEffects.Apply(m, 0, x.Traits);
				TraitEffects.Apply(m, 1, r.Traits);
				TeamValue.Parts p = TeamValue.Of(m, 0);
				TeamValue.Parts q = TeamValue.Of(m, 1);
				sum += p.Runs - q.Runs;
			}
			return sum / Math.Max(1, refs.Count);
		}

		// TCSIM_FA_CHAIN=1 (user 2026-11-16 00:3x: "대체하게 되는 선수가 포지션 변경을 통해 또 약점을 채울 수 있게 된다던가 … 기존 선수를
		// 밀어내고 기존 선수가 포지션 변경을 통해 다른 불펜을 밀어낸다던가, 원래 불펜이 선발로 포지션 변경했는데 그러지 않아도 된다던가"):
		// the man the FA displaces may stay in the 1군 one step on, and the man there leaves instead.
		// - Pitcher: to any other pitcher spot he may play — his own role (a 포지션 변경 he had made is then not made: pti 0, the MP
		//   saved), the role he was changed to (kept, 대성공 too), or a role his card may change to (ChangeFor, 성공).
		// - Batter: to another fielding spot his card has a defence value for (부포지션), and that man leaves or goes to DH (then
		//   the DH leaves).
		// - The FA pitcher also tries every pitcher spot, out of his role there (FA cards may not 포지션 변경 now).
		// Keys: "<fa>@SP5>RP3" (the SP5 man moves to RP3, the RP3 man leaves), "<fa>@SS>3B", "<fa>@SS>3B+DH".
		private static bool FaChain => Environment.GetEnvironmentVariable("TCSIM_FA_CHAIN") == "1";

		// The 포지션 변경 a displaced pitcher plays at `slot` (deck slot), or null when he may not play there in role.
		private static byte? MovedPti(Cand m, byte had, int slot)
		{
			int r = slot - FirstPitcher;
			if (LineupEffects.InRole(r < 5 ? 0 : r - 4, m.Info.PreferredOrder)) return 0;
			if (had != 0 && had / 10 + 11 == SlotRole(slot)) return had;
			byte c = ChangeFor(m, slot);
			return c != 0 ? c : null;
		}

		private sealed class FaTry
		{
			public string Key;
			public Cand Fa;
			public int At;
			public (int from, int to)[] Moves = Array.Empty<(int, int)>();
			public int Leaver;
			public bool Chain;   // a TCSIM_FA_CHAIN variant (else as the 11-15 run had it)
			public bool Great;   // (11-17, TCSIM_FA_PAIRS key ending "!") the moved man's 포지션 변경 is a 대성공, not a 성공
		}

		private static List<(string label, Deck deck, DevDeck dev, string line)> FaVariants(string label, Deck own, DevDeck y, List<DevDeck> field)
		{
			List<(string, Deck, DevDeck, string)> runs = new List<(string, Deck, DevDeck, string)>();
			if (label.Contains("#FA")) return runs;
			int[] keepN = (Environment.GetEnvironmentVariable("TCSIM_FA") ?? "30,20").Split(',').Select(int.Parse).ToArray();
			Dictionary<string, string> picks = (Environment.GetEnvironmentVariable("TCSIM_FA_PICK") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
			HashSet<string> only = picks.TryGetValue(label, out string pick) ? pick.Split(',').Select(k => k.Trim()).ToHashSet() : null;
			if (Environment.GetEnvironmentVariable("TCSIM_FA") == null && only == null) return runs;
			// Reference decks for the screen: five spread over the field by its order (strong to weak as the field lists them).
			List<DevDeck> refs = field.Count <= 5 ? field : Enumerable.Range(0, 5).Select(i => field[i * (field.Count - 1) / 4]).Distinct().ToList();
			double baseValue = FaValue(y, refs);
			// TCSIM_FA_YEAR=deck (user 2026-11-15 19:2x: "97쌍방울덱이면 97년도 FA 선수를 써야하는거야"): only the FA cards of the deck's own
			// season (the label's year, "1997 쌍방울#지금" → 1997).
			int onlyYear = Environment.GetEnvironmentVariable("TCSIM_FA_YEAR") == "deck" && int.TryParse(label.Split(' ')[0], out int ly) ? ly : 0;
			// TCSIM_FA_DOMESTIC=1 (user 2026-11-17 02:0x: "외국인 선수는 안 넣는게 맞는 것 같음. 국내선수들로만 나오게 해주고 … 국내선수FA로
			// 계산해서 다시 우선순위를 찾아주고"): domestic FA cards only (CardInfo.Foreigner), as the FA 시장 offers them from 11-17.
			bool domestic = Environment.GetEnvironmentVariable("TCSIM_FA_DOMESTIC") == "1";
			List<Cand> fas = s_data.Catalog.All.Where(c => c.IsFA && (onlyYear == 0 || c.Year == onlyYear) && (!domestic || !FaMarket.IsForeign(c))).OrderBy(c => c.Idx).Select(c => AnyCard(c.Idx)).Where(c => c != null).ToList();
			bool chain = FaChain;
			// TCSIM_FA_DEPTH=n (user 2026-11-17 10:2x: "01롯데처럼 두 칸 이상의 연쇄를 6가지 덱이 전부 적용해서 다시 재봅시다"): chains of up to n
			// moves — the man the FA displaces moves on, the man there moves on too, … and the last one leaves. Keys "<fa>@SP5>RP2>RP4"
			// (SP5's man to RP2, RP2's to RP4, RP4's leaves), "<fa>@SS>3B>DH". 1 (the default) is the 11-16 set exactly.
			int depth = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_FA_DEPTH"), out int dp) && dp > 1 ? dp : 1;
			string SpotOfSlot(int slot) => slot < FirstPitcher ? SpotNames[slot] : PlanRoles[slot - FirstPitcher];
			// Every try of the FA cards on deck d (development z): the 11-16 set, then (depth > 1) the longer chains. skip: an FA
			// already in d (the second FA's tries, TCSIM_FA_THEN).
			List<FaTry> TriesOn(Deck d, DevDeck z, int deep, int skip)
			{
				List<FaTry> list = new List<FaTry>();
				bool Twice(Cand fa, int leaving) => Enumerable.Range(0, Slots).Any(s => s != leaving && d.At[s] != null
					&& d.At[s].Info.IsBatter == fa.Info.IsBatter && d.At[s].Info.Sidx == fa.Info.Sidx);
				void Add(FaTry t)
				{
					if (!Twice(t.Fa, t.Leaver)) list.Add(t);
				}
				// From the man at `cur` (moving), one more step to t; the man at t leaves (emitted when the chain has 2+ moves)
				// or moves on while steps are left.
				void Deeper(Cand fa, int at, string key, List<(int, int)> moves, int cur, HashSet<int> used, int left)
				{
					Cand m = d.At[cur];
					bool batter = m.Info.IsBatter;
					int lo = batter ? 0 : FirstPitcher, hi = batter ? 9 : FirstPitcher + 11;
					for (int t = lo; t < hi; t++)
					{
						if (used.Contains(t) || d.At[t] == null) continue;
						if (batter ? !(t == 8 || Fits(m, t)) : MovedPti(m, z.Dev[cur].PositionTrainingInfo, t) == null) continue;
						List<(int, int)> next = new List<(int, int)>(moves) { (cur, t) };
						string k = $"{key}>{SpotOfSlot(t)}";
						if (next.Count >= 2) Add(new FaTry { Key = k, Fa = fa, At = at, Moves = next.ToArray(), Leaver = t, Chain = true });
						if (left > 1) Deeper(fa, at, k, next, t, new HashSet<int>(used) { t }, left - 1);
					}
				}
				foreach (Cand fa in fas)
				{
					if (fa.Info.Idx == skip) continue;
					if (fa.Info.IsBatter)
					{
						for (int s = 0; s < 9; s++)
						{
							if (!Fits(fa, s) || d.At[s] == null) continue;
							string at = $"{fa.Info.Idx}@{SpotNames[s]}";
							Add(new FaTry { Key = at, Fa = fa, At = s, Leaver = s });
							// The man at s stays (to DH); only the DH leaves.
							if (s < 8 && d.At[8] != null) Add(new FaTry { Key = at + "+DH", Fa = fa, At = s, Moves = new[] { (s, 8) }, Leaver = 8 });
							if (!chain) continue;
							// The man at s to another fielding spot t (his card plays it), the man there leaves or goes to DH.
							for (int t = 0; t < 8; t++)
							{
								if (t == s || d.At[t] == null || !Fits(d.At[s], t)) continue;
								Add(new FaTry { Key = $"{at}>{SpotNames[t]}", Fa = fa, At = s, Moves = new[] { (s, t) }, Leaver = t, Chain = true });
								if (s < 8 && d.At[8] != null) Add(new FaTry { Key = $"{at}>{SpotNames[t]}+DH", Fa = fa, At = s, Moves = new[] { (s, t), (t, 8) }, Leaver = 8, Chain = true });
							}
							if (deep > 1) Deeper(fa, s, at, new List<(int, int)>(), s, new HashSet<int> { s }, deep);
						}
					}
					else
					{
						bool starter = fa.Info.MainPosition == CardPos.SP;
						for (int r = 0; r < 11; r++)
						{
							int slot = FirstPitcher + r;
							if (d.At[slot] == null) continue;
							bool ownRole = starter ? r < 5 : r >= 5;
							if (!ownRole && !chain) continue;
							string at = $"{fa.Info.Idx}@{PlanRoles[r]}";
							Add(new FaTry { Key = at, Fa = fa, At = slot, Leaver = slot, Chain = !ownRole });
							if (!chain) continue;
							// The man at r to another pitcher spot he may play, the man there leaves.
							for (int q = 0; q < 11; q++)
							{
								int to = FirstPitcher + q;
								if (q == r || d.At[to] == null || MovedPti(d.At[slot], z.Dev[slot].PositionTrainingInfo, to) == null) continue;
								Add(new FaTry { Key = $"{at}>{PlanRoles[q]}", Fa = fa, At = slot, Moves = new[] { (slot, to) }, Leaver = to, Chain = true });
							}
							if (deep > 1) Deeper(fa, slot, at, new List<(int, int)>(), slot, new HashSet<int> { slot }, deep);
						}
					}
				}
				return list;
			}
			List<FaTry> tries = TriesOn(own, y, depth, 0);
			if (only != null) tries = tries.Where(t => only.Contains(t.Key)).ToList();
			ConcurrentDictionary<(int idx, bool catcher), TeamCard> devOf = new ConcurrentDictionary<(int, bool), TeamCard>();
			string SpotOf(int slot) => slot < FirstPitcher ? SpotNames[slot] : PlanRoles[slot - FirstPitcher];
			(Deck v, DevDeck z, string what) Variant(FaTry t) => VariantOn(t, own, y, $"{label}#FA {t.Key}");
			// (11-17, FA 2명) The try on any deck — the deck as it is, or one with another FA in already (TCSIM_FA_PAIRS).
			(Deck v, DevDeck z, string what) VariantOn(FaTry t, Deck own, DevDeck y, string vLabel)
			{
				Cand fa = t.Fa;
				Deck v = new Deck { Label = vLabel, At = (Cand[])own.At.Clone(), Value = own.Value };
				DevDeck z = new DevDeck { D = v, Traits = y.Traits, Build = y.Build, Order = (int[])y.Order.Clone(), Tactics = y.Tactics };
				for (int s = 0; s < Slots; s++) z.Dev[s] = y.Dev[s];
				int at = t.At;
				List<string> parts = new List<string>();
				if (at >= FirstPitcher && t.Chain && !LineupEffects.InRole(at - FirstPitcher < 5 ? 0 : at - FirstPitcher - 4, fa.Info.PreferredOrder)) parts.Add("FA는 역할 밖(포지션 변경 없음)");
				foreach ((int from, int to) in t.Moves)
				{
					Cand m = own.At[from];
					v.At[to] = m;
					if (to < FirstPitcher)
					{
						z.Dev[to] = y.Dev[from];
						parts.Add($"{SpotOf(from)} {m.Info.Name}→{SpotOf(to)}");
						continue;
					}
					// A pitcher moved: his card's development goes with him, his 포지션 변경 as he plays the new spot.
					TeamCard had = y.Dev[from];
					byte pti = MovedPti(m, had.PositionTrainingInfo, to) ?? 0;
					if (t.Great && pti % 10 == 2) pti--;   // 성공 → 대성공 (code role*10 + 1)
					TeamCard moved = new TeamCard { Idx = had.Idx, PositionTrainingInfo = pti };
					for (int k = 0; k < 6; k++)
					{
						moved.TeamColor[k] = had.TeamColor[k];
						moved.Contract[k] = had.Contract[k];
						moved.Training[k] = had.Training[k];
						moved.Educated[k] = had.Educated[k];
						moved.Coach[k] = had.Coach[k];
					}
					z.Dev[to] = moved;
					string note = pti == had.PositionTrainingInfo ? string.Empty : pti == 0 ? "(포지션 변경 안 함)" : $"(포지션 변경 → {PlanRoleNames[pti / 10 + 11]}{(pti % 10 == 1 ? " 대성공" : string.Empty)})";
					parts.Add($"{SpotOf(from)} {m.Info.Name}→{SpotOf(to)}{note}");
				}
				parts.Add($"{SpotOf(t.Leaver)} {own.At[t.Leaver].Info.Name}{own.At[t.Leaver].Info.Ball} 빠짐");
				string what = string.Join(", ", parts);
				v.At[at] = fa;
				TeamCard d = devOf.GetOrAdd((fa.Info.Idx, at == 0), k => DevelopCard(fa, k.catcher, FaRenewal, 5, 6, true));
				TeamCard copy = new TeamCard { Idx = d.Idx, PositionTrainingInfo = 0 };
				for (int k = 0; k < 6; k++)
				{
					copy.TeamColor[k] = d.TeamColor[k];
					copy.Contract[k] = d.Contract[k];
					copy.Training[k] = d.Training[k];
					copy.Educated[k] = d.Educated[k];
				}
				z.Dev[at] = copy;
				if (y.Own != null)
				{
					// 개인작전 by deck slot: the moved men keep theirs, the FA's and the leaver's spots have none.
					z.Own = new Dictionary<int, GameTactics>(y.Own);
					z.Own.Remove(at);
					z.Own.Remove(t.Leaver);
					foreach ((int from, int to) in t.Moves) z.Own.Remove(to);
					foreach ((int from, int to) in t.Moves)
					{
						if (y.Own.TryGetValue(from, out GameTactics g)) z.Own[to] = g;
					}
					if (t.Moves.Length == 1 && t.Moves[0].to == 8 && at < 8) z.Own.Remove(8);  // the 11-15 "+DH" as it ran
				}
				z.Lineup = LineupOf(v, z.Order);
				v.Balls = v.At.Where(c => c != null).Sum(c => c.Info.Ball);
				return (v, z, what);
			}
			// TCSIM_FA_CAP="<deck>=<cap>;…" (11-17): variants over the deck's ball cap are left out before the screen.
			Dictionary<string, int> caps = (Environment.GetEnvironmentVariable("TCSIM_FA_CAP") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2 && int.TryParse(kv[1], out _)).ToDictionary(kv => kv[0].Trim(), kv => int.Parse(kv[1]));
			int cap = caps.TryGetValue(label, out int capOf) ? capOf : int.MaxValue;
			string FaName(Cand c) => $"{c.Info.Year % 100:00} {c.Info.Name} {c.Info.Ball}볼 ({TeamColors.TeamName(c.Info.HomeTeam, c.Info.Year)})";
			// A key ending "!" (11-17, user 09:3x on 01롯데: "박석진 선발 포지션 대성공"): that try with its moved man's change a 대성공.
			FaTry PickFrom(Dictionary<string, FaTry> byKey, string k)
			{
				bool great = k.EndsWith("!");
				if (!byKey.TryGetValue(great ? k[..^1] : k, out FaTry t)) return null;
				return great ? new FaTry { Key = k, Fa = t.Fa, At = t.At, Moves = t.Moves, Leaver = t.Leaver, Chain = t.Chain, Great = true } : t;
			}
			bool PlayerTwice(Deck d) => d.At.Where(c => c != null).GroupBy(c => (c.Info.IsBatter, c.Info.Sidx)).Any(g => g.Count() > 1);
			// TCSIM_FA_THEN="<deck>=<keyA>|<keyA>…;…" (user 11-17 10:2x, two FAs with chains of two moves and more): for each first FA as its
			// key has it (from the single run's games), every try of a second FA on the deck with the first one in — chains up to
			// TCSIM_FA_THEN_DEPTH (default TCSIM_FA_DEPTH) — under the cap, valued by TeamValue; the best TCSIM_FA_THEN_KEEP (default 10,
			// two variants a card at most) play, and the TCSIM_FA_THEN_ALSO="<deck>=<keyB>,…;…" keys whenever valid there.
			Dictionary<string, string> Picks(string env) => (Environment.GetEnvironmentVariable(env) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
			if (Picks("TCSIM_FA_THEN").TryGetValue(label, out string thenList))
			{
				int keepB = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_FA_THEN_KEEP"), out int kb) && kb > 0 ? kb : 10;
				int deepB = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_FA_THEN_DEPTH"), out int dB) && dB >= 1 ? dB : depth;
				HashSet<string> also = Picks("TCSIM_FA_THEN_ALSO").TryGetValue(label, out string al)
					? al.Split(',').Select(k => k.Trim()).Where(k => k.Length > 0).ToHashSet() : new HashSet<string>();
				Dictionary<string, FaTry> byKeyA = tries.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
				foreach (string ka in thenList.Split('|').Select(s => s.Trim()).Where(s => s.Length > 0))
				{
					FaTry ta = PickFrom(byKeyA, ka);
					if (ta == null)
					{
						ServerLog.Warn($"tcsim entry: {label} FA 2명 먼저 {ka}: 경우가 없음, 뺌");
						continue;
					}
					(Deck va, DevDeck za, string wa) = VariantOn(ta, own, y, $"{label}#FA {ka}");
					List<FaTry> triesB = TriesOn(va, za, deepB, ta.Fa.Info.Idx);
					ConcurrentBag<(FaTry t, double gain)> valuedB = new ConcurrentBag<(FaTry, double)>();
					Parallel.ForEach(triesB, tb =>
					{
						(Deck v2, DevDeck z2, _) = VariantOn(tb, va, za, "screen");
						if (v2.Balls > cap || PlayerTwice(v2)) return;
						valuedB.Add((tb, FaValue(z2, refs) - baseValue));
					});
					List<(FaTry t, double gain)> ranked = valuedB.OrderByDescending(x => x.gain).ThenBy(x => x.t.Key, StringComparer.Ordinal).ToList();
					List<(FaTry t, double gain)> chosenB = new List<(FaTry, double)>();
					Dictionary<int, int> perCard = new Dictionary<int, int>();
					foreach ((FaTry t, double gain) x in ranked)
					{
						if (chosenB.Count >= keepB) break;
						perCard.TryGetValue(x.t.Fa.Info.Idx, out int n);
						if (n >= 2) continue;
						perCard[x.t.Fa.Info.Idx] = n + 1;
						chosenB.Add(x);
					}
					foreach ((FaTry t, double gain) x in ranked)
					{
						if (also.Contains(x.t.Key) && !chosenB.Exists(p => p.t.Key == x.t.Key)) chosenB.Add(x);
					}
					ServerLog.Info($"tcsim entry: {label} FA 2명 먼저 {ka}: 둘째 경우 {triesB.Count}개, 상한({(cap == int.MaxValue ? "-" : cap.ToString())}) 안 {valuedB.Count}개 → {chosenB.Count}개 경기 | 선별 "
						+ string.Join(", ", ranked.Take(15).Select((x, i) => $"{i + 1}.{x.t.Fa.Info.Name} {x.gain:+0.000;-0.000} {x.t.Key.Substring(x.t.Key.IndexOf('@') + 1)}")));
					foreach ((FaTry t, double gain) x in chosenB)
					{
						(Deck v2, DevDeck z2, string wb) = VariantOn(x.t, va, za, $"{label}#FA {ka}&{x.t.Key}");
						string dev = $"{FaDevText(z2.Dev[ta.At], ta.Fa.Info.IsBatter)} // {FaDevText(z2.Dev[x.t.At], x.t.Fa.Info.IsBatter)}";
						runs.Add((v2.Label, v2, z2, $"FA2 {FaName(ta.Fa)} → {wa} + {FaName(x.t.Fa)} → {wb}{(ta.Chain || x.t.Chain ? " [연쇄]" : string.Empty)} 육성 {dev}"));
					}
				}
				return runs;
			}
			// TCSIM_FA_PAIRS="<deck>=<keyA>&<keyB>,…;…" (user 2026-11-17 07:3x: "1군에도 FA선수를 2명 쓸 수 있다는 얘기야. 그래서 2명을 넣는다는
			// 계산 하에 승률을 다시 계산해줬으면 하는 것임. … FA 투수는 포지션 변경이 안 되니 그걸 고려해서"): two FA cards at once, each as
			// its single try had it (key as above, chains included; an FA pitcher plays his spot with no 포지션 변경, in or out of his role),
			// on spots apart. The pairs come from tools\scripts\yuksung\fa_pairs.py (the single run's games, the deck's cap); only they play.
			Dictionary<string, string> pairPicks = (Environment.GetEnvironmentVariable("TCSIM_FA_PAIRS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
			if (pairPicks.TryGetValue(label, out string pairList))
			{
				Dictionary<string, FaTry> byKey = tries.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First());
				HashSet<int> Touched(FaTry t) => new[] { t.At, t.Leaver }.Concat(t.Moves.SelectMany(m => new[] { m.from, m.to })).ToHashSet();
				int skipped = 0;
				foreach (string pk in pairList.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
				{
					string[] ab = pk.Split('&');
					FaTry ta = ab.Length == 2 ? PickFrom(byKey, ab[0]) : null, tb = ab.Length == 2 ? PickFrom(byKey, ab[1]) : null;
					if (ta == null || tb == null || ta.Fa.Info.Idx == tb.Fa.Info.Idx || Touched(ta).Overlaps(Touched(tb)))
					{
						ServerLog.Warn($"tcsim entry: {label} FA 2명 {pk}: 경우가 없거나 자리가 겹침, 뺌");
						skipped++;
						continue;
					}
					string vLabel = $"{label}#FA {pk}";
					(Deck va, DevDeck za, string wa) = VariantOn(ta, own, y, vLabel);
					(Deck vb, DevDeck zb, string wb) = VariantOn(tb, va, za, vLabel);
					if (PlayerTwice(vb))
					{
						ServerLog.Warn($"tcsim entry: {label} FA 2명 {pk}: 같은 선수 두 장, 뺌");
						skipped++;
						continue;
					}
					string dev = $"{FaDevText(zb.Dev[ta.At], ta.Fa.Info.IsBatter)} // {FaDevText(zb.Dev[tb.At], tb.Fa.Info.IsBatter)}";
					runs.Add((vb.Label, vb, zb, $"FA2 {FaName(ta.Fa)} → {wa} + {FaName(tb.Fa)} → {wb}{(ta.Chain || tb.Chain ? " [연쇄]" : string.Empty)} 육성 {dev}"));
				}
				ServerLog.Info($"tcsim entry: {label} FA 2명: 짝 {runs.Count}개 경기{(skipped > 0 ? $", {skipped}개 뺌" : string.Empty)}");
				return runs;
			}
			ConcurrentBag<(FaTry t, double gain)> valued = new ConcurrentBag<(FaTry, double)>();
			Parallel.ForEach(tries, t =>
			{
				(Deck v, DevDeck z, _) = Variant(t);
				if (v.Balls > cap) return;
				valued.Add((t, FaValue(z, refs) - baseValue));
			});
			// TCSIM_FA_PERFA=n (11-17, chains): an FA card that plays plays its best n variants (by the screen), not only the best.
			int perFa = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_FA_PERFA"), out int pf) && pf > 1 ? pf : 1;
			List<(FaTry t, double gain)> chosen;
			if (only != null)
			{
				chosen = valued.ToList();
			}
			else
			{
				// Each FA card's best variant; the best `batters` and `pitchers` play. With chains, an FA whose best variant is a
				// chain also plays his best variant as the 11-15 run had it (so the book shows what the chain adds, by games).
				List<(FaTry t, double gain)> best = valued.GroupBy(v => v.t.Fa.Info.Idx).Select(g => g.OrderByDescending(v => v.gain).ThenBy(v => v.t.Key, StringComparer.Ordinal).First()).ToList();
				// The whole screen in the log (11-17: '24 류현진 had been screened out of 24롯데's run unseen, and the user expected him
				// first): every FA card's best variant and its screen value, batters and pitchers.
				foreach (bool bat in new[] { true, false })
				{
					ServerLog.Info($"tcsim entry: {label} FA 선별 순위 {(bat ? "타자" : "투수")} " + string.Join(", ", best.Where(f => f.t.Fa.Info.IsBatter == bat)
						.OrderByDescending(f => f.gain).ThenBy(f => f.t.Key, StringComparer.Ordinal)
						.Select((f, i) => $"{i + 1}.{f.t.Fa.Info.Name}{(f.t.Fa.Info.Foreigner ? "(외)" : string.Empty)} {f.gain:+0.000;-0.000} {f.t.Key.Substring(f.t.Key.IndexOf('@') + 1)}")));
				}
				best = best.Where(f => f.t.Fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN[0])
					.Concat(best.Where(f => !f.t.Fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN.Length > 1 ? keepN[1] : keepN[0])).ToList();
				chosen = new List<(FaTry, double)>(best);
				if (perFa > 1)
				{
					foreach ((FaTry t, double gain) b in best.ToList())
					{
						chosen.AddRange(valued.Where(v => v.t.Fa.Info.Idx == b.t.Fa.Info.Idx && v.t.Key != b.t.Key)
							.OrderByDescending(v => v.gain).ThenBy(v => v.t.Key, StringComparer.Ordinal).Take(perFa - 1));
					}
				}
				foreach ((FaTry t, double gain) in best.Where(b => b.t.Chain))
				{
					var plain = valued.Where(v => !v.t.Chain && v.t.Fa.Info.Idx == t.Fa.Info.Idx).OrderByDescending(v => v.gain).ThenBy(v => v.t.Key, StringComparer.Ordinal).FirstOrDefault();
					if (plain.t != null) chosen.Add(plain);
				}
			}
			chosen = chosen.GroupBy(c => c.t.Key).Select(g => g.First()).ToList();   // a key once (PERFA and the plain variant may meet)
			foreach ((FaTry t, double gain) in chosen.OrderByDescending(c => c.gain).ThenBy(c => c.t.Key, StringComparer.Ordinal))
			{
				Cand fa = t.Fa;
				(Deck v, DevDeck z, string what) = Variant(t);
				string line = $"FA {fa.Info.Year % 100:00} {fa.Info.Name} {fa.Info.Ball}볼 ({TeamColors.TeamName(fa.Info.HomeTeam, fa.Info.Year)}) → {what} [선별 {gain:+0.000;-0.000}점]{(t.Chain ? " [연쇄]" : string.Empty)} 육성 {FaDevText(z.Dev[t.At], fa.Info.IsBatter)}";
				runs.Add((v.Label, v, z, line));
			}
			ServerLog.Info($"tcsim entry: {label} FA {fas.Count}장{(onlyYear != 0 ? $"('{onlyYear % 100:00}만)" : string.Empty)}{(domestic ? "(국내만)" : string.Empty)}, 경우 {tries.Count}개 선별(TeamValue, 기준 {refs.Count}덱) → {runs.Count}개 경기" + (only != null ? " (지정)" : $" (타자 {keepN[0]}·투수 {(keepN.Length > 1 ? keepN[1] : keepN[0])}까지)"));
			return runs;
		}
	}
}
