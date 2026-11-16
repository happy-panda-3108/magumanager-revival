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
			List<Cand> fas = s_data.Catalog.All.Where(c => c.IsFA && (onlyYear == 0 || c.Year == onlyYear)).OrderBy(c => c.Idx).Select(c => AnyCard(c.Idx)).Where(c => c != null).ToList();
			bool Twice(Cand fa, params int[] leaving) => Enumerable.Range(0, Slots).Any(s => !leaving.Contains(s) && own.At[s] != null
				&& own.At[s].Info.IsBatter == fa.Info.IsBatter && own.At[s].Info.Sidx == fa.Info.Sidx);
			bool chain = FaChain;
			List<FaTry> tries = new List<FaTry>();
			void Add(FaTry t)
			{
				if (!Twice(t.Fa, t.Leaver)) tries.Add(t);
			}
			foreach (Cand fa in fas)
			{
				if (fa.Info.IsBatter)
				{
					for (int s = 0; s < 9; s++)
					{
						if (!Fits(fa, s) || own.At[s] == null) continue;
						string at = $"{fa.Info.Idx}@{SpotNames[s]}";
						Add(new FaTry { Key = at, Fa = fa, At = s, Leaver = s });
						// The man at s stays (to DH); only the DH leaves.
						if (s < 8 && own.At[8] != null) Add(new FaTry { Key = at + "+DH", Fa = fa, At = s, Moves = new[] { (s, 8) }, Leaver = 8 });
						if (!chain) continue;
						// The man at s to another fielding spot t (his card plays it), the man there leaves or goes to DH.
						for (int t = 0; t < 8; t++)
						{
							if (t == s || own.At[t] == null || !Fits(own.At[s], t)) continue;
							Add(new FaTry { Key = $"{at}>{SpotNames[t]}", Fa = fa, At = s, Moves = new[] { (s, t) }, Leaver = t, Chain = true });
							if (s < 8 && own.At[8] != null) Add(new FaTry { Key = $"{at}>{SpotNames[t]}+DH", Fa = fa, At = s, Moves = new[] { (s, t), (t, 8) }, Leaver = 8, Chain = true });
						}
					}
				}
				else
				{
					bool starter = fa.Info.MainPosition == CardPos.SP;
					for (int r = 0; r < 11; r++)
					{
						int slot = FirstPitcher + r;
						if (own.At[slot] == null) continue;
						bool ownRole = starter ? r < 5 : r >= 5;
						if (!ownRole && !chain) continue;
						string at = $"{fa.Info.Idx}@{PlanRoles[r]}";
						Add(new FaTry { Key = at, Fa = fa, At = slot, Leaver = slot, Chain = !ownRole });
						if (!chain) continue;
						// The man at r to another pitcher spot he may play, the man there leaves.
						for (int q = 0; q < 11; q++)
						{
							int to = FirstPitcher + q;
							if (q == r || own.At[to] == null || MovedPti(own.At[slot], y.Dev[slot].PositionTrainingInfo, to) == null) continue;
							Add(new FaTry { Key = $"{at}>{PlanRoles[q]}", Fa = fa, At = slot, Moves = new[] { (slot, to) }, Leaver = to, Chain = true });
						}
					}
				}
			}
			if (only != null) tries = tries.Where(t => only.Contains(t.Key)).ToList();
			ConcurrentDictionary<(int idx, bool catcher), TeamCard> devOf = new ConcurrentDictionary<(int, bool), TeamCard>();
			string SpotOf(int slot) => slot < FirstPitcher ? SpotNames[slot] : PlanRoles[slot - FirstPitcher];
			(Deck v, DevDeck z, string what) Variant(FaTry t)
			{
				Cand fa = t.Fa;
				Deck v = new Deck { Label = $"{label}#FA {t.Key}", At = (Cand[])own.At.Clone(), Value = own.Value };
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
					string note = pti == had.PositionTrainingInfo ? string.Empty : pti == 0 ? "(포지션 변경 안 함)" : $"(포지션 변경 → {PlanRoleNames[pti / 10 + 11]})";
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
			ConcurrentBag<(FaTry t, double gain)> valued = new ConcurrentBag<(FaTry, double)>();
			Parallel.ForEach(tries, t =>
			{
				(_, DevDeck z, _) = Variant(t);
				valued.Add((t, FaValue(z, refs) - baseValue));
			});
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
				best = best.Where(f => f.t.Fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN[0])
					.Concat(best.Where(f => !f.t.Fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN.Length > 1 ? keepN[1] : keepN[0])).ToList();
				chosen = new List<(FaTry, double)>(best);
				foreach ((FaTry t, double gain) in best.Where(b => b.t.Chain))
				{
					var plain = valued.Where(v => !v.t.Chain && v.t.Fa.Info.Idx == t.Fa.Info.Idx).OrderByDescending(v => v.gain).ThenBy(v => v.t.Key, StringComparer.Ordinal).FirstOrDefault();
					if (plain.t != null) chosen.Add(plain);
				}
			}
			foreach ((FaTry t, double gain) in chosen.OrderByDescending(c => c.gain).ThenBy(c => c.t.Key, StringComparer.Ordinal))
			{
				Cand fa = t.Fa;
				(Deck v, DevDeck z, string what) = Variant(t);
				string line = $"FA {fa.Info.Year % 100:00} {fa.Info.Name} {fa.Info.Ball}볼 ({TeamColors.TeamName(fa.Info.HomeTeam, fa.Info.Year)}) → {what} [선별 {gain:+0.000;-0.000}점]{(t.Chain ? " [연쇄]" : string.Empty)} 육성 {FaDevText(z.Dev[t.At], fa.Info.IsBatter)}";
				runs.Add((v.Label, v, z, line));
			}
			ServerLog.Info($"tcsim entry: {label} FA {fas.Count}장{(onlyYear != 0 ? $"('{onlyYear % 100:00}만)" : string.Empty)}, 경우 {tries.Count}개 선별(TeamValue, 기준 {refs.Count}덱) → {runs.Count}개 경기" + (only != null ? " (지정)" : $" (타자 {keepN[0]}·투수 {(keepN.Length > 1 ? keepN[1] : keepN[0])}까지)"));
			return runs;
		}
	}
}
