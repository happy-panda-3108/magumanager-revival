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
			List<(string key, Cand fa, int[] put)> tries = new List<(string, Cand, int[])>();
			foreach (Cand fa in fas)
			{
				if (fa.Info.IsBatter)
				{
					for (int s = 0; s < 9; s++)
					{
						if (!Fits(fa, s) || own.At[s] == null) continue;
						if (!Twice(fa, s)) tries.Add(($"{fa.Info.Idx}@{SpotNames[s]}", fa, new[] { s }));
						// The man at s stays (to DH); only the DH leaves.
						if (s < 8 && own.At[8] != null && !Twice(fa, 8)) tries.Add(($"{fa.Info.Idx}@{SpotNames[s]}+DH", fa, new[] { s, 8 }));
					}
				}
				else
				{
					bool starter = fa.Info.MainPosition == CardPos.SP;
					for (int r = starter ? 0 : 5; r < (starter ? 5 : 11); r++)
					{
						int slot = FirstPitcher + r;
						if (own.At[slot] != null && !Twice(fa, slot)) tries.Add(($"{fa.Info.Idx}@{PlanRoles[r]}", fa, new[] { slot }));
					}
				}
			}
			if (only != null) tries = tries.Where(t => only.Contains(t.key)).ToList();
			ConcurrentDictionary<(int idx, bool catcher), TeamCard> devOf = new ConcurrentDictionary<(int, bool), TeamCard>();
			(Deck v, DevDeck z, string what) Variant(string key, Cand fa, int[] put)
			{
				Deck v = new Deck { Label = $"{label}#FA {key}", At = (Cand[])own.At.Clone(), Value = own.Value };
				DevDeck z = new DevDeck { D = v, Traits = y.Traits, Build = y.Build, Order = (int[])y.Order.Clone(), Tactics = y.Tactics };
				for (int s = 0; s < Slots; s++) z.Dev[s] = y.Dev[s];
				int at = put[0];
				string what;
				if (put.Length == 2)
				{
					what = $"{SpotNames[at]} {own.At[at].Info.Name}→DH, DH {own.At[8].Info.Name}{own.At[8].Info.Ball} 빠짐";
					v.At[8] = own.At[at];
					z.Dev[8] = y.Dev[at];
				}
				else
				{
					what = $"{(at < FirstPitcher ? SpotNames[at] : PlanRoles[at - FirstPitcher])} {own.At[at].Info.Name}{own.At[at].Info.Ball} 빠짐";
				}
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
					z.Own = new Dictionary<int, GameTactics>(y.Own);
					foreach (int s in put) z.Own.Remove(s);
				}
				z.Lineup = LineupOf(v, z.Order);
				v.Balls = v.At.Where(c => c != null).Sum(c => c.Info.Ball);
				return (v, z, what);
			}
			ConcurrentBag<(string key, Cand fa, int[] put, double gain)> valued = new ConcurrentBag<(string, Cand, int[], double)>();
			Parallel.ForEach(tries, t =>
			{
				(_, DevDeck z, _) = Variant(t.key, t.fa, t.put);
				valued.Add((t.key, t.fa, t.put, FaValue(z, refs) - baseValue));
			});
			List<(string key, Cand fa, int[] put, double gain)> chosen = valued.GroupBy(t => t.fa.Info.Idx).Select(g => g.OrderByDescending(t => t.gain).First()).ToList();
			if (only != null)
			{
				chosen = valued.ToList();
			}
			else
			{
				chosen = chosen.Where(f => f.fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN[0])
					.Concat(chosen.Where(f => !f.fa.Info.IsBatter).OrderByDescending(f => f.gain).Take(keepN.Length > 1 ? keepN[1] : keepN[0])).ToList();
			}
			foreach ((string key, Cand fa, int[] put, double gain) in chosen.OrderByDescending(c => c.gain))
			{
				(Deck v, DevDeck z, string what) = Variant(key, fa, put);
				string line = $"FA {fa.Info.Year % 100:00} {fa.Info.Name} {fa.Info.Ball}볼 ({TeamColors.TeamName(fa.Info.HomeTeam, fa.Info.Year)}) → {what} [선별 {gain:+0.000;-0.000}점] 육성 {FaDevText(z.Dev[put[0]], fa.Info.IsBatter)}";
				runs.Add((v.Label, v, z, line));
			}
			ServerLog.Info($"tcsim entry: {label} FA {fas.Count}장{(onlyYear != 0 ? $"('{onlyYear % 100:00}만)" : string.Empty)}, 경우 {tries.Count}개 선별(TeamValue, 기준 {refs.Count}덱) → {runs.Count}개 경기" + (only != null ? " (지정)" : $" (타자 {keepN[0]}·투수 {(keepN.Length > 1 ? keepN[1] : keepN[0])}까지)"));
			return runs;
		}
	}
}
