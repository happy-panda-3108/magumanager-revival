using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ma9MSim;

namespace Ma9MServer
{
	// Deck and development reports on the team-colour simulator (systems session, 2026-10-20).
	public static partial class TeamColorSim
	{
		private static readonly string[] OneTeamFamilies = { "단일 년도 팀", "듀얼 년도 단일 팀", "연대 단일 팀", "단일 팀 올스타" };

		// TeamCard bonus order (TeamStrength): batter con, pow, spd, eye, def, mental; pitcher health, spd,
		// cont, technic, breakball, mental. CardRow.R index of each batter one: 컨택 파워 선구 주력 정신 수비.
		private static readonly string[] BatStatNames = { "컨택", "파워", "주력", "선구", "수비", "정신" };
		private static readonly string[] PitStatNames = { "체력", "구속", "컨트롤", "테크닉", "구위", "정신" };
		private static readonly int[] BatRowIndex = { 0, 1, 3, 2, 5, 4 };

		private static sbyte[] Same(int v) => Enumerable.Repeat((sbyte)v, 6).ToArray();

		private static sbyte[] One(int k, int v)
		{
			sbyte[] b = new sbyte[6];
			b[k] = (sbyte)v;
			return b;
		}

		// "--tcsim [cap] [games] teams" (user: "각 팀별 가장 강한 덱"): for every franchise, the best deck of
		// each one-team family under the cap (top 3 single years, top 2 year pairs, best decade, all years),
		// each with the best team colour its 25 can register (TeamColors.BestTotals), played against the
		// free deck (no team colour) with and without that bonus.
		private static int RunTeams(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int[] keep = { 3, 2, 1, 1 };
			(int freeBat, int freePit) = BestTotals(free);
			ServerLog.Info($"tcsim teams: cap {cap}, {games} games per deck; free deck {free.Balls}볼, own team colour +{freeBat}/+{freePit}, free vs free {100 * PlayBP(free, free, null, null, games, 1).win:F1}%");
			var summary = new List<(string team, string family, Deck d, int bat, int pit, double raw, double tc, double rf, double ra)>();
			foreach (int t in all.Select(x => x.Info.TeamCode).Distinct().OrderBy(x => x))
			{
				string team = TeamNames[t];
				List<Cand> mine = all.Where(x => x.Info.TeamCode == t).ToList();
				int[] years = mine.Select(x => x.Info.Year).Distinct().OrderBy(x => x).ToArray();
				var instances = new[]
				{
					years.Select(y => ($"{y} {team}", (Func<CardInfo, bool>)(x => x.Year == y))).ToList(),
					(from y1 in years from y2 in years where y1 < y2 select ($"{y1}+{y2} {team}", (Func<CardInfo, bool>)(x => x.Year == y1 || x.Year == y2))).ToList(),
					years.Select(y => y / 10).Distinct().Select(d => ($"{d * 10}년대 {team}", (Func<CardInfo, bool>)(x => x.Year / 10 == d))).ToList(),
					new List<(string, Func<CardInfo, bool>)> { ($"{team} 올스타", x => true) },
				};
				var rows = new List<(string family, Deck d, int bat, int pit, double raw, double tc, double rf, double ra)>();
				for (int f = 0; f < instances.Length; f++)
				{
					List<Deck> decks = new List<Deck>();
					object gate = new object();
					Parallel.ForEach(instances[f], inst =>
					{
						List<Cand> pool = mine.Where(x => inst.Item2(x.Info)).ToList();
						Deck d = Build(inst.Item1, pool, cap);
						// Rebuilt with the catcher valued at the defence he shows with the deck's team colour
						// (the boost starts at 90 shown; +19 lifts a 72 catcher there).
						int expect = d == null ? 0 : BestTotals(d).bat;
						if (expect > 0) d = Build(inst.Item1, WithCatcherBonus(pool, expect), cap) ?? d;
						if (d != null)
						{
							lock (gate) decks.Add(d);
						}
					});
					foreach (Deck d in decks.OrderByDescending(d => d.Value).Take(keep[f]))
					{
						(int bat, int pit) = BestTotals(d);
						double raw = PlayBP(d, free, null, null, games, 1).win;
						var r = PlayBP(d, free, Same(bat), Same(pit), games, 1);
						rows.Add((OneTeamFamilies[f], d, bat, pit, raw, r.win, r.rf, r.ra));
					}
				}
				if (rows.Count == 0)
				{
					ServerLog.Info($"tcsim teams: {team}: no complete one-team deck");
					continue;
				}
				rows = rows.OrderByDescending(r => r.tc).ToList();
				foreach (var r in rows)
				{
					ServerLog.Info($"tcsim teams: {team} | {r.family} {r.d.Label}: {r.d.Balls}볼, 팀컬러 +{r.bat}/+{r.pit} → 승률 {100 * r.tc:F1}% ({r.rf:F2}-{r.ra:F2}), 팀컬러 없이 {100 * r.raw:F1}%");
				}
				ServerLog.Info($"tcsim teams: {team} 최강 {rows[0].d.Label}: {Describe(rows[0].d)}");
				summary.Add((team, rows[0].family, rows[0].d, rows[0].bat, rows[0].pit, rows[0].raw, rows[0].tc, rows[0].rf, rows[0].ra));
			}
			ServerLog.Info($"tcsim teams: ===== 팀별 최강 덱 (cap {cap}, 자유 덱 상대 승률) =====");
			foreach (var s in summary.OrderByDescending(s => s.tc))
			{
				ServerLog.Info($"tcsim teams: {s.team,-5} {s.d.Label} ({s.family}) {s.d.Balls}볼 +{s.bat}/+{s.pit}: {100 * s.tc:F1}% (없이 {100 * s.raw:F1}%) | {Describe(s.d)}");
			}
			return 0;
		}

		// "--tcsim [cap] [games] stats" (user: "스탯 육성 순서"): +N bonus points (TCSIM_POINTS, default 18 =
		// six contract renewals) in one stat for the nine starters, or for all eleven pitchers, against the
		// same deck without it, on the same seeds. Two decks: the free deck (strong cards, many stats past
		// shown 100 with team colour, where the curve counts 75%) and the median single team-year deck (ordinary cards).
		private static int RunStats(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int pts = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_POINTS"), out int p) ? p : 18;
			List<Deck> years = new List<Deck>();
			object gate = new object();
			Parallel.ForEach(families.First(f => f.name == "단일 년도 팀").instances, inst =>
			{
				Deck d = Build(inst.label, all.Where(x => inst.ok(x.Info)).ToList(), cap);
				if (d != null)
				{
					lock (gate) years.Add(d);
				}
			});
			years = years.OrderByDescending(d => d.Value).ToList();
			Deck median = years[years.Count / 2];
			// TCSIM_DECKS="2018 두산;2023 LG": team-year decks to test instead of the free and the median one.
			string pick = Environment.GetEnvironmentVariable("TCSIM_DECKS");
			Deck[] tested = string.IsNullOrEmpty(pick) ? new[] { free, median } : pick.Split(';').Select(l => years.FirstOrDefault(y => y.Label == l.Trim())).Where(y => y != null).ToArray();
			foreach (Deck d in tested)
			{
				string bats = string.Join(" ", Enumerable.Range(0, 6).Select(k => $"{BatStatNames[k]} {Enumerable.Range(0, 9).Average(s => d.At[s].Row.R[BatRowIndex[k]]):F0}"));
				string pits = string.Join(" ", Enumerable.Range(0, 6).Select(k => $"{PitStatNames[k]} {Enumerable.Range(FirstPitcher, 11).Average(s => d.At[s].Row.R[k]):F0}"));
				ServerLog.Info($"tcsim stats: {d.Label} {d.Balls}볼, +{pts}점 (경기 반영 최대 {TeamStrength.DevWeight * TeamStrength.DevScale * pts:F1}, 표시값 곡선 전), {games}경기씩 | 주전 평균 {bats} | 투수 평균 {pits}");
				var baseline = PlayBP(d, d, null, null, games, 7);
				void Report(string who, string name, sbyte[] bat, sbyte[] pit, int batSlot = -1)
				{
					var r = PlayBP(d, d, bat, pit, games, 7, batSlot);
					ServerLog.Info($"tcsim stats: {d.Label} | {who} {name} +{pts}: 승률 {100 * (r.win - baseline.win):+0.0;-0.0}%p, 득점 {r.rf - baseline.rf:+0.00;-0.00}, 실점 {r.ra - baseline.ra:+0.00;-0.00}, 득실 {(r.rf - r.ra) - (baseline.rf - baseline.ra):+0.00;-0.00}/경기");
				}
				for (int k = 0; k < 6; k++) Report("타자", BatStatNames[k], One(k, pts), null);
				Report("타자", "전 스탯", Same(pts), null);
				for (int k = 0; k < 6; k++) Report("투수", PitStatNames[k], null, One(k, pts));
				Report("투수", "전 스탯", null, Same(pts));
				// The catcher alone: his shown defence over 90 lifts every pitcher (Field.CatcherBoost, full
				// bonus points, not the development weight), against the same points in 컨택.
				Cand c = d.At[0];
				ServerLog.Info($"tcsim stats: {d.Label} | 포수 {c.Info.Year % 100:00}{c.Info.Name}: 카드 수비 {c.Row.Defs[0]:F0}, 컨택 {c.Row.R[0]:F0}");
				Report("포수만", "수비", One(4, pts), null, 0);
				Report("포수만", "컨택", One(0, pts), null, 0);
				Report("유격수만", "수비", One(4, pts), null, 4);
				Report("유격수만", "컨택", One(0, pts), null, 4);
			}
			return 0;
		}

		// "--tcsim [cap] [games] dev" (user, 2026-10-20: "팀컬러랑 특성, 트레이닝 등등 육성이 적용된 단일년도
		// 덱끼리 붙어야할 것임"): every franchise's top single team-year decks, fully developed, in a round
		// robin. Development per card: team colour S (+19 all, C15+H4+P4), six 재계약 (+3 each) and three
		// training slots at +4, put where they are worth most (the stats report's order with the stat weights,
		// counted on the shown curve and halved past the in-play knee as TeamStrength.AddBonuses plays them, the
		// catcher's 수비 first when his shown defence clears 90), and the 메이저 trait points in the deck's best
		// of the three AI builds.
		// Team colour S of a single team-year deck: H 6 + C 15 = P 6 + C 15 = 21 since 2026-10-24 (TeamColors; 19 before).
		private const int DevTeamColour = 21, DevRenewals = 6, DevRenewal = 3, DevSlots = 3, DevSlotGain = 4;

		// 특별수강 (user, 2026-10-24: "특강, 타순, 트레이닝, 재계약 등등 고려해서 계산"): every card takes the coach worth most
		// to it among those it may take (LessonHandlers.Eligible: batter or pitcher, age band, foreigner), with the plain
		// result, main +4 and the two subs +2 (+5 comes 30% of the time). The daily rotation is left out: given time every
		// coach comes round. TCSIM_LECTURE=0 leaves the lectures out, as the runs before 2026-10-24 did.
		private const int DevLectureMain = 4, DevLectureSub = 2;

		private static readonly bool DevLecture = Environment.GetEnvironmentVariable("TCSIM_LECTURE") != "0";

		// Worth per point at a stat's start (stats report, the weights before 2026-10-27): batter 컨택 파워 주력 선구
		// 수비 정신, pitcher 체력 구속 컨트롤 테크닉 구위 정신 (TeamCard order). The stat weights
		// (TeamStrength.BatDevStat / PitDevStat, 구위 ×1.15) come on top.
		private static readonly double[] BatWorth = { 1.0, 0.85, 0.15, 0.75, 0.15, 0.1 };
		private static readonly double[] PitWorth = { 0.1, 0.55, 0.6, 0.7, 1.0, 0.1 };

		// renewal: the points one 재계약 gives (FA 선수카드: 5, user 2026-11-15 "계약 연장때마다 5의 보너스 스탯", six times as others).
		// lectureMain / slotGain / stack (FA cards in the books, 2026-11-15): the users' books take 특강 main +5 (행운권·초기화권) and
		// three 트레이닝 slots of +6 (IV book + coach) that may share a stat (25LG 박해민 컨택 +18).
		private static TeamCard DevelopCard(Cand c, bool catcher, int renewal = DevRenewal, int lectureMain = DevLectureMain, int slotGain = DevSlotGain, bool stack = false)
		{
			TeamCard d = new TeamCard { Idx = c.Info.Idx };
			for (int k = 0; k < 6; k++) d.TeamColor[k] = DevTeamColour;
			bool bat = c.Info.IsBatter;
			double Base(int k) => bat ? c.Row.R[BatRowIndex[k]] : c.Row.R[k];
			// The next `extra` points' worth each: counted on the shown curve (DevCurve: 100 → 75%, 120 → 50%) and
			// halved again where the game value is past the in-play knee (105), as TeamStrength.AddBonuses plays them.
			double Worth(int k, int extra)
			{
				int bonus = d.TeamColor[k] + d.Contract[k] + d.Training[k] + d.Educated[k] + extra;
				if (Base(k) + bonus > TeamStrength.ShownCap) return 0;
				double statWeight = bat ? TeamStrength.BatDevStat[BatRowIndex[k]] : TeamStrength.PitDevStat[k];
				double w = (bat ? BatWorth[k] : PitWorth[k]) * statWeight;
				if (bat && catcher && k == 4)
				{
					// 포수 수비: the boost reads the shown defence (card + every bonus point) on TeamStrength.CatcherCurve
					// (2026-10-31: 100 → 75%, 120 → 25%, so past 120 a point is worth less than one in 컨택).
					double from = c.Row.Defs[0] + bonus - extra;
					double part = TeamStrength.Counted(from, extra, TeamStrength.CatcherCurve) / extra;
					return (c.Row.Defs[0] + bonus > s_data.Env.Field.CatcherBoostFrom ? 2.2 : 0.3) * part;
				}
				double counted = TeamStrength.Counted(Base(k), bonus);
				double share = (counted - TeamStrength.Counted(Base(k), bonus - extra)) / extra;
				double inPlay = Base(k) + TeamStrength.DevWeight * TeamStrength.DevScale * statWeight * counted;
				return w * share * (inPlay >= TeamStrength.InPlayKnee ? 0.5 : 1);
			}
			if (DevLecture)
			{
				LessonHandlers.Coach best = null;
				double bestGain = 0;
				foreach (LessonHandlers.Coach co in (bat ? LessonHandlers.Batting : LessonHandlers.Pitching).Where(co => LessonHandlers.Eligible(co, c.Info)))
				{
					double gain = lectureMain * Worth(co.Main, lectureMain) + DevLectureSub * (Worth(co.Sub1, DevLectureSub) + Worth(co.Sub2, DevLectureSub));
					if (gain > bestGain)
					{
						bestGain = gain;
						best = co;
					}
				}
				if (best != null)
				{
					d.Educated[best.Main] += (sbyte)lectureMain;
					d.Educated[best.Sub1] += DevLectureSub;
					d.Educated[best.Sub2] += DevLectureSub;
				}
			}
			for (int r = 0; r < DevRenewals; r++)
			{
				int k = Enumerable.Range(0, 6).OrderByDescending(j => Worth(j, renewal)).First();
				d.Contract[k] += (sbyte)renewal;
			}
			for (int slot = 0; slot < DevSlots; slot++)
			{
				int k = Enumerable.Range(0, 6).Where(j => stack || d.Training[j] == 0).OrderByDescending(j => Worth(j, slotGain)).First();
				d.Training[k] += (sbyte)slotGain;
			}
			return d;
		}

		// The deck as the game plays it (user, 2026-10-20: "자기 포지션에서 던지게 … 타순, 포지션을 전부
		// 맞춰서"): the staff by role — the five best 선발 cards in SP1-5, the four best 중계 in RP1-4, the
		// 셋업 and the 마무리 in their spots; other cards only where a role has too few (and then they play
		// out of role, LineupEffects.OutOfRole). Fielders stay where the builder put them (spots they can
		// play); the batting order is chosen by BestOrder.
		private static Deck RoleDeck(Deck d, List<Cand> pool)
		{
			Deck r = new Deck { Label = d.Label, At = (Cand[])d.At.Clone() };
			HashSet<int> taken = new HashSet<int>(Enumerable.Range(0, FirstPitcher).Where(s => r.At[s] != null).Select(s => Person(r.At[s])));
			List<Cand> pitchers = pool.Where(c => !c.Info.IsBatter && !taken.Contains(Person(c))).ToList();
			HashSet<Cand> used = new HashSet<Cand>();
			void Fill(int from, int count, int role, Func<Cand, double> value)
			{
				List<Cand> mine = pitchers.Where(c => c.Info.PreferredOrder == role && !used.Contains(c)).OrderByDescending(value).Take(count).ToList();
				for (int i = 0; i < count; i++)
				{
					Cand pick = i < mine.Count ? mine[i] : null;
					if (pick != null) used.Add(pick);
					r.At[from + i] = pick;
				}
			}
			Fill(FirstPitcher, 5, 0, c => c.Starter);
			Fill(FirstPitcher + 5, 4, 1, c => c.Stuff);
			Fill(FirstPitcher + 9, 1, 2, c => c.Stuff);
			Fill(FirstPitcher + 10, 1, 3, c => c.Stuff);
			// Empty spots (a role with too few cards): the best of those who may change to the spot's role (포지션 변경,
			// ChangeFor; user, 2026-11-02: "포지션 변경으로 어지간하면 팀 구멍이 다 메워지고"), else the best cards left,
			// whatever their role (they then play out of role).
			for (int s = FirstPitcher; s < Slots; s++)
			{
				if (r.At[s] != null) continue;
				int slot = s;
				Cand pick = pitchers.Where(c => !used.Contains(c) && ChangeFor(c, slot) != 0).OrderByDescending(c => Value(c, slot)).FirstOrDefault()
					?? pitchers.Where(c => !used.Contains(c)).OrderByDescending(c => Value(c, slot)).FirstOrDefault();
				if (pick != null) used.Add(pick);
				r.At[s] = pick ?? d.At[s];
			}
			r.Balls = r.At.Where(c => c != null).Sum(c => c.Info.Ball);
			r.Value = Enumerable.Range(0, Slots).Sum(s => Value(r.At[s], s));
			return r;
		}

		// A deck over the cap after RoleDeck (each role's best cards, whatever their balls; '07 SK and '19 히어로즈 came to 153
		// in the 11-02 league): swaps for cheaper cards until it fits. A batter for a cheaper one who fields the spot (the
		// bench costs nothing), a pitcher for a cheaper one of the spot's role or one who may change to it (포지션 변경). The set
		// of up to three swaps (different spots and players) that loses least value; when three are not enough, one at a
		// time by least value per ball. A team whose cheapest deck is over the cap stays over ('94 LG: 151).
		private static Deck CapDeck(Deck d, List<Cand> pool, int cap)
		{
			int balls = d.At.Where(c => c != null).Sum(c => c.Info.Ball);
			if (balls <= cap) return d;
			Deck r = new Deck { Label = d.Label, At = (Cand[])d.At.Clone() };
			HashSet<int> taken = new HashSet<int>(r.At.Where(c => c != null).Select(Person));
			bool May(Cand c, int slot) => slot < FirstPitcher ? Fits(c, slot) : !c.Info.IsBatter && ((int)c.Info.MainPosition == SlotRole(slot) || ChangeFor(c, slot) != 0);
			List<(int slot, Cand card, int save, double loss)> swaps = new List<(int, Cand, int, double)>();
			for (int s = 0; s < Slots; s++)
			{
				Cand old = r.At[s];
				if (old == null) continue;
				foreach (Cand c in pool.Where(x => x.Info.Ball < old.Info.Ball && !taken.Contains(Person(x)) && May(x, s)))
				{
					swaps.Add((s, c, old.Info.Ball - c.Info.Ball, Value(old, s) - Value(c, s)));
				}
			}
			int excess = balls - cap;
			double bestLoss = double.MaxValue;
			List<int> bestSet = null;
			List<int> chosen = new List<int>();
			void Search(int from, int saved, double loss)
			{
				if (saved >= excess)
				{
					if (loss < bestLoss)
					{
						bestLoss = loss;
						bestSet = new List<int>(chosen);
					}
					return;
				}
				if (chosen.Count == 3) return;
				for (int i = from; i < swaps.Count; i++)
				{
					if (chosen.Any(j => swaps[j].slot == swaps[i].slot || Person(swaps[j].card) == Person(swaps[i].card))) continue;
					chosen.Add(i);
					Search(i + 1, saved + swaps[i].save, loss + swaps[i].loss);
					chosen.RemoveAt(chosen.Count - 1);
				}
			}
			Search(0, 0, 0);
			foreach (int i in bestSet ?? new List<int>())
			{
				taken.Remove(Person(r.At[swaps[i].slot]));
				taken.Add(Person(swaps[i].card));
				balls -= swaps[i].save;
				r.At[swaps[i].slot] = swaps[i].card;
			}
			while (balls > cap)
			{
				int bestSlot = -1;
				Cand bestCard = null;
				double bestCost = double.MaxValue;
				for (int s = 0; s < Slots; s++)
				{
					Cand old = r.At[s];
					if (old == null) continue;
					foreach (Cand c in pool.Where(x => x.Info.Ball < old.Info.Ball && !taken.Contains(Person(x)) && May(x, s)))
					{
						double cost = (Value(old, s) - Value(c, s)) / (old.Info.Ball - c.Info.Ball);
						if (cost < bestCost)
						{
							bestSlot = s;
							bestCard = c;
							bestCost = cost;
						}
					}
				}
				if (bestCard == null) break;
				taken.Remove(Person(r.At[bestSlot]));
				taken.Add(Person(bestCard));
				balls -= r.At[bestSlot].Info.Ball - bestCard.Info.Ball;
				r.At[bestSlot] = bestCard;
			}
			r.Balls = balls;
			r.Value = Enumerable.Range(0, Slots).Sum(s => Value(r.At[s], s));
			ServerLog.Info($"tcsim dev: {d.Label} 볼 상한 {cap}: {d.Balls}볼 → {balls}볼 ({string.Join(", ", Enumerable.Range(0, Slots).Where(s => r.At[s] != d.At[s]).Select(s => $"{d.At[s]?.Info.Name}{d.At[s]?.Info.Ball}→{r.At[s].Info.Name}{r.At[s].Info.Ball}"))})");
			return r;
		}

		// Batting order (fielding spot per order slot): each batter's offence weighted by the plate
		// appearances of his slot, plus his 선수분석 share (LineupEffects: preferred order band and
		// neighbours) at about 2.4 runs a game per unit of share; best order by swaps from the
		// band-respecting start (상위 1-2번, 클린업 3-5번, 하위 6-9번, 밸런스 anywhere).
		private static int[] BestOrder(Deck d) => BestOrder(d, out _);

		private static int[] BestOrder(Deck d, out double score)
		{
			double PaWeight(int k) => (4.65 - 0.105 * k) / PaPerGame;
			double Score(int[] o)
			{
				CardInfo[] infos = o.Select(s => d.At[s].Info).ToArray();
				double[] graph = LineupEffects.BatterGraph(infos);
				return Enumerable.Range(0, 9).Sum(k => PaWeight(k) * d.At[o[k]].Off + 2.4 * LineupEffects.FromGraph(graph[k]));
			}
			int[][] bands = { new[] { 0, 1 }, new[] { 2, 3, 4 }, new[] { 5, 6, 7, 8 } };
			int InBands(int[] o) => Enumerable.Range(0, 9).Count(k => d.At[o[k]].Info.PreferredOrder >= 3 || bands[d.At[o[k]].Info.PreferredOrder].Contains(k));
			int[] order = Enumerable.Repeat(-1, 9).ToArray();
			List<int> left = Enumerable.Range(0, 9).OrderByDescending(s => d.At[s].Off).ToList();
			for (int b = 0; b < 3; b++)
			{
				foreach (int s in left.Where(s => d.At[s].Info.PreferredOrder == b).ToList())
				{
					int k = bands[b].FirstOrDefault(i => order[i] < 0, -1);
					if (k < 0) break;
					order[k] = s;
					left.Remove(s);
				}
			}
			foreach (int s in left)
			{
				order[Array.IndexOf(order, -1)] = s;
			}
			double best = Score(order);
			for (bool better = true; better;)
			{
				better = false;
				for (int i = 0; i < 9; i++)
				{
					for (int j = i + 1; j < 9; j++)
					{
						int inBand = InBands(order);
						(order[i], order[j]) = (order[j], order[i]);
						// Only orders that keep as many batters in their 타순 band (user: 타순을 맞춰서).
						double v = InBands(order) >= inBand ? Score(order) : double.MinValue;
						if (v > best + 1e-9)
						{
							best = v;
							better = true;
						}
						else
						{
							(order[i], order[j]) = (order[j], order[i]);
						}
					}
				}
			}
			score = best;
			return order;
		}

		// The nine and their spots (user, 2026-10-20: "24롯데 … 외야 4명 … 이 중 1명이 지명타자로 … 멀티포지션 …
		// 팀의 전문 DH보다 포지션을 가진 선수가 공격력이 더 높은 경우"): every batter at any spot he can field (main
		// or sub position) or at DH, chosen together rather than spot by spot (Build fills spot by spot, so a
		// multi-position player taken early can leave a later spot to a weak card). The best sets by offence
		// + defence (k-best over filled spots), each scored with its best batting order (BestOrder: plate
		// appearances, 타순 band, neighbours) plus its defence.
		private const int LineupKeep = 40;

		private sealed class LineupPlan
		{
			public Cand[] At = new Cand[9];  // spots C..RF, DH
			public int[] Order;
			public double Score;
		}

		private static List<LineupPlan> BestLineups(List<Cand> pool)
		{
			List<Cand> bats = pool.Where(c => c.Info.IsBatter).OrderByDescending(c => c.Off).Take(64).ToList();
			int n = bats.Count;
			if (n < 9) return new List<LineupPlan>();
			// A spot nobody can field is played out of position (Value: −15), as Build does.
			bool[] open = Enumerable.Range(0, 8).Select(s => !bats.Any(c => Fits(c, s))).ToArray();
			double V(int i, int s) => s == 8 ? bats[i].Off : Fits(bats[i], s) ? Value(bats[i], s) : open[s] ? bats[i].Off - 15 : double.NaN;
			ulong[] clash = Enumerable.Range(0, n).Select(i => Enumerable.Range(0, n).Where(j => j != i && Person(bats[j]) == Person(bats[i])).Aggregate(0UL, (m, j) => m | 1UL << j)).ToArray();
			var states = new Dictionary<int, List<(double v, ulong set, int[] at)>> { [0] = new List<(double, ulong, int[])> { (0, 0, Enumerable.Repeat(-1, 9).ToArray()) } };
			for (int i = 0; i < n; i++)
			{
				var next = states.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
				foreach (var (mask, list) in states)
				{
					foreach (var p in list)
					{
						if ((p.set & clash[i]) != 0) continue;
						for (int s = 0; s < 9; s++)
						{
							if ((mask & 1 << s) != 0) continue;
							double v = V(i, s);
							if (double.IsNaN(v)) continue;
							int[] at = (int[])p.at.Clone();
							at[s] = i;
							int m2 = mask | 1 << s;
							if (!next.TryGetValue(m2, out var l2)) next[m2] = l2 = new List<(double, ulong, int[])>();
							l2.Add((p.v + v, p.set | 1UL << i, at));
						}
					}
				}
				foreach (int m in next.Keys.ToList())
				{
					next[m] = next[m].GroupBy(p => p.set).Select(g => g.OrderByDescending(p => p.v).First()).OrderByDescending(p => p.v).Take(LineupKeep).ToList();
				}
				states = next;
			}
			if (!states.TryGetValue(511, out var full)) return new List<LineupPlan>();
			List<LineupPlan> plans = new List<LineupPlan>();
			foreach (var p in full)
			{
				LineupPlan plan = new LineupPlan();
				Deck tmp = new Deck();
				for (int s = 0; s < 9; s++) tmp.At[s] = plan.At[s] = bats[p.at[s]];
				plan.Order = BestOrder(tmp, out double offence);
				plan.Score = offence + Enumerable.Range(0, 8).Sum(s => V(p.at[s], s) - bats[p.at[s]].Off);
				plans.Add(plan);
			}
			return plans.OrderByDescending(x => x.Score).ToList();
		}

		// The deck with the best nine (BestLineups) and the cheapest other batters on the bench; unchanged
		// when that would go over the cap.
		private static Deck LineupDeck(Deck d, List<Cand> pool, int cap, out List<LineupPlan> plans)
		{
			plans = BestLineups(pool);
			if (plans.Count == 0) return d;
			Deck r = new Deck { Label = d.Label, At = (Cand[])d.At.Clone() };
			for (int s = 0; s < 9; s++) r.At[s] = plans[0].At[s];
			HashSet<int> taken = r.At.Take(9).Select(Person).ToHashSet();
			for (int s = FirstPitcher; s < Slots; s++) if (r.At[s] != null) taken.Add(Person(r.At[s]));
			List<Cand> bench = pool.Where(c => c.Info.IsBatter && !taken.Contains(Person(c))).OrderBy(c => c.Info.Ball).ThenByDescending(c => c.Off).ToList();
			for (int b = 0; b < 5; b++)
			{
				Cand pick = bench.FirstOrDefault(c => !taken.Contains(Person(c)));
				if (pick == null) return d;
				taken.Add(Person(pick));
				r.At[Bench + b] = pick;
			}
			r.Balls = r.At.Where(c => c != null).Sum(c => c.Info.Ball);
			if (r.Balls > cap) return d;
			r.Value = Enumerable.Range(0, Slots).Sum(s => Value(r.At[s], s));
			return r;
		}

		private static readonly string[] SpotNames = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" };

		// A spot as the user reads it: "황성빈 DH(CF)" when the player's main position is elsewhere.
		private static string SpotText(Cand c, int s)
		{
			int main = (int)c.Info.MainPosition - 1;
			string own = main >= 0 && main < 8 ? SpotNames[main] : "DH";
			return own == SpotNames[s] ? $"{c.Info.Name}{c.Info.Ball} {SpotNames[s]}" : $"{c.Info.Name}{c.Info.Ball} {SpotNames[s]}({own})";
		}

		private static string PlanText(LineupPlan p) => string.Join(", ", p.Order.Select((s, k) => $"{k + 1}.{SpotText(p.At[s], s)}"));

		// What changes from the chosen nine to another: who comes in or goes out, who moves.
		private static string PlanDiff(LineupPlan a, LineupPlan b)
		{
			List<string> parts = new List<string>();
			for (int s = 0; s < 9; s++)
			{
				if (a.At[s] != b.At[s]) parts.Add($"{SpotNames[s]} {a.At[s].Info.Name}→{b.At[s].Info.Name}");
			}
			return string.Join(", ", parts);
		}

		// The game's Lineup for a deck and order (LineupEffects.Apply reads it).
		private static Lineup LineupOf(Deck d, int[] order)
		{
			Lineup l = new Lineup();
			for (int k = 0; k < 9; k++)
			{
				l.Batters[k] = d.At[order[k]].Info.Idx;
				l.Fielders[order[k]] = (byte)(k + 1);
			}
			for (int b = 0; b < 5; b++) l.Batters[9 + b] = d.At[Bench + b]?.Info.Idx ?? 0;
			for (int s = 0; s < 11; s++) l.Pitchers[s] = d.At[FirstPitcher + s]?.Info.Idx ?? 0;
			return l;
		}

		private sealed class DevDeck
		{
			public Deck D;
			public TeamCard[] Dev = new TeamCard[Slots];
			public TraitEffects.Effect Traits;
			public int Build;
			public int[] Order;     // fielding spot per batting order slot (BestOrder)
			public Lineup Lineup;   // for LineupEffects
			public int FixedStarter = -1;  // plan: this SP slot starts every game (else the rotation)
			// 작전 (2026-11-11, TCSIM_OPS in entry mode): the 작전실 and deck slots' 개인작전; null = neutral / follows the team.
			public GameTactics Tactics;
			public Dictionary<int, GameTactics> Own;
		}

		private static DevDeck Develop(Deck d)
		{
			DevDeck x = new DevDeck { D = d, Order = BestOrder(d) };
			x.Lineup = LineupOf(d, x.Order);
			for (int s = 0; s < Slots; s++)
			{
				if (d.At[s] == null) continue;
				x.Dev[s] = DevelopCard(d.At[s], s == 0);
				// A pitcher in another role's spot has made the 포지션 변경 there when his card may.
				if (s >= FirstPitcher) x.Dev[s].PositionTrainingInfo = ChangeFor(d.At[s], s);
			}
			return x;
		}

		// The role of a pitcher spot (deck slots 14-18 SP1-5, 19-22 RP1-4, 23 SU, 24 CP) as ePos 12-15.
		private static int SlotRole(int slot) => slot - FirstPitcher < 5 ? 12 : slot - FirstPitcher < 9 ? 13 : slot - FirstPitcher < 10 ? 14 : 15;

		// 포지션 변경 for a pitcher in spot `slot` (PositionTrainingInfo, 성공 not 대성공): 0 when he plays his own role there
		// or his card may not change to it (ma9m_pitcher_roles.txt, PositionChangeHandlers.AllowedRoles) — then he plays out
		// of role (LineupEffects.OutOfRole).
		private static byte ChangeFor(Cand c, int slot)
		{
			int role = SlotRole(slot);
			if (c == null || c.Info.IsBatter || slot < FirstPitcher || (int)c.Info.MainPosition == role) return 0;
			return PositionChangeHandlers.AllowedRoles(c.Info.Idx).Contains((byte)role) ? (byte)((role - 11) * 10 + 2) : (byte)0;
		}

		// As LeagueService plays a club: the cards in the lineup's order and spots, the arrangement's
		// effects (LineupEffects: 선수분석 shares, out of position, out of role), then the bonuses.
		private static GameTeam DevTeam(DevDeck x, int game)
		{
			Deck d = x.D;
			GameTeam t = new GameTeam { Name = d.Label };
			int[] order = x.Order ?? Enumerable.Range(0, 9).OrderByDescending(s => d.At[s].Off).ToArray();
			for (int i = 0; i < 9; i++)
			{
				t.Order[i] = s_data.Card(d.At[order[i]].Info.Idx, 2);
				t.Position[i] = order[i];
			}
			for (int s = 0; s < 11; s++)
			{
				t.Pitchers[s] = s_data.Card(d.At[FirstPitcher + s].Info.Idx, 2);
			}
			t.StarterSlot = x.FixedStarter >= 0 ? x.FixedStarter : game % 5;
			// The deck's own cards, so a pitcher's 포지션 변경 (Dev PositionTrainingInfo) also plays his new role.
			LineupEffects.Apply(t, x.Lineup ?? LineupOf(d, order), s_data.Catalog, x.Dev.Where(c => c != null));
			for (int i = 0; i < 9; i++) TeamStrength.AddBonuses(t.Order[i], x.Dev[order[i]]);
			for (int s = 0; s < 11; s++) TeamStrength.AddBonuses(t.Pitchers[s], x.Dev[FirstPitcher + s]);
			if (x.Tactics != null) t.Tactics = x.Tactics;
			if (x.Own != null)
			{
				for (int i = 0; i < 9; i++) if (x.Own.TryGetValue(order[i], out GameTactics o)) t.Order[i].Own = o;
				for (int s = 0; s < 11; s++) if (x.Own.TryGetValue(FirstPitcher + s, out GameTactics o)) t.Pitchers[s].Own = o;
			}
			return t;
		}

		// TCSIM_SERIES=<n> (user, 2026-11-05: "가 안으로 합시다" — 중계 체력 measured on league days): the games are played in
		// chains of n, one after another, each pitcher carrying his fatigue to the next game as LeagueService.PlayGame does
		// (TacticModel.CarryIn, days in a row, pitches in the last two; tired or overused relievers sit out). Chains start
		// rested and run in parallel; game g keeps its seed, starter and home side, so changes stay paired.
		private static readonly int s_series = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_SERIES"), out int sr) ? sr : 0;

		// Both developed, as the league plays them: bonuses, league reference, trait gains, model, trait rates.
		private static (double win, double rf, double ra) PlayDev(DevDeck a, DevDeck b, int games, int salt)
		{
			if (s_series > 0)
			{
				return PlayDevSeries(a, b, games, salt, s_series);
			}
			double wins = 0, rf = 0, ra = 0;
			object gate = new object();
			Parallel.For(0, games, () => (0.0, 0.0, 0.0), (g, _, acc) =>
			{
				GameTeam ta = DevTeam(a, g), tb = DevTeam(b, g);
				TeamStrength.ApplyLeagueReference(new[] { ta, tb });
				TraitEffects.Raise(ta, a.Traits);
				TraitEffects.Raise(tb, b.Traits);
				bool aHome = g % 2 == 0;
				SeasonModel m = aHome ? CardGame.Build(ta, tb, s_data.Env) : CardGame.Build(tb, ta, s_data.Env);
				TraitEffects.Apply(m, aHome ? 0 : 1, a.Traits);
				TraitEffects.Apply(m, aHome ? 1 : 0, b.Traits);
				SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(g * 7919 + 13 + salt)).PlayOne(0, 1, null);
				int x = m.TeamBatters[aHome ? 0 : 1].Sum(i => (int)st.Bat(i, BS.R));
				int y = m.TeamBatters[aHome ? 1 : 0].Sum(i => (int)st.Bat(i, BS.R));
				return (acc.Item1 + (x > y ? 1 : x == y ? 0.5 : 0), acc.Item2 + x, acc.Item3 + y);
			}, acc =>
			{
				lock (gate)
				{
					wins += acc.Item1;
					rf += acc.Item2;
					ra += acc.Item3;
				}
			});
			return (wins / games, rf / games, ra / games);
		}

		private static (double win, double rf, double ra) PlayDevSeries(DevDeck a, DevDeck b, int games, int salt, int len)
		{
			double wins = 0, rf = 0, ra = 0;
			object gate = new object();
			Parallel.For(0, (games + len - 1) / len, c =>
			{
				// Per deck (0 = a, 1 = b) and pitcher spot (SP1 … CP).
				double[,] units = new double[2, 11];
				int[,] days = new int[2, 11], last1 = new int[2, 11], last2 = new int[2, 11];
				double w = 0, f = 0, r = 0;
				for (int g = c * len; g < Math.Min(games, (c + 1) * len); g++)
				{
					GameTeam ta = DevTeam(a, g), tb = DevTeam(b, g);
					TeamStrength.ApplyLeagueReference(new[] { ta, tb });
					TraitEffects.Raise(ta, a.Traits);
					TraitEffects.Raise(tb, b.Traits);
					bool aHome = g % 2 == 0;
					SeasonModel m = aHome ? CardGame.Build(ta, tb, s_data.Env) : CardGame.Build(tb, ta, s_data.Env);
					TraitEffects.Apply(m, aHome ? 0 : 1, a.Traits);
					TraitEffects.Apply(m, aHome ? 1 : 0, b.Traits);
					int[] side = { aHome ? 0 : 1, aHome ? 1 : 0 };
					for (int d = 0; d < 2 && m.Tac != null; d++)
					{
						for (int slot = 0; slot < 11 && slot < m.Decks[side[d]].Pitchers.Length; slot++)
						{
							int p = m.Decks[side[d]].Pitchers[slot];
							if (p < 0) continue;
							m.Tac.CarryIn[p] = units[d, slot];
							m.Tac.DaysInRow[p] = days[d, slot];
							m.Tac.Last1[p] = last1[d, slot];
							m.Tac.Last2[p] = last2[d, slot];
						}
					}
					SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(g * 7919 + 13 + salt)).PlayOne(0, 1, null);
					for (int d = 0; d < 2 && m.Tac != null; d++)
					{
						for (int slot = 0; slot < 11 && slot < m.Decks[side[d]].Pitchers.Length; slot++)
						{
							int p = m.Decks[side[d]].Pitchers[slot];
							if (p < 0) continue;
							int np = (int)st.Pit(p, PS.NP);
							units[d, slot] = m.Tac.R.CarryOut(m.Tac.CarryIn[p], m.Tac.Units(p, np), m.Tac.Limit[p]);
							days[d, slot] = st.Pit(p, PS.G) > 0 ? days[d, slot] + 1 : 0;
							last2[d, slot] = last1[d, slot];
							last1[d, slot] = np;
						}
					}
					int x = m.TeamBatters[side[0]].Sum(i => (int)st.Bat(i, BS.R));
					int y = m.TeamBatters[side[1]].Sum(i => (int)st.Bat(i, BS.R));
					w += x > y ? 1 : x == y ? 0.5 : 0;
					f += x;
					r += y;
				}
				lock (gate)
				{
					wins += w;
					rf += f;
					ra += r;
				}
			});
			return (wins / games, rf / games, ra / games);
		}

		private static int RunDev(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int perTeam = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_PER_TEAM"), out int pt) ? pt : 3;
			List<Deck> decks = new List<Deck>();
			Dictionary<string, List<LineupPlan>> plansOf = new Dictionary<string, List<LineupPlan>>();
			bool oldLineup = Environment.GetEnvironmentVariable("TCSIM_OLDLINEUP") == "1";
			object gate = new object();
			Parallel.ForEach(families.First(f => f.name == "단일 년도 팀").instances, inst =>
			{
				List<Cand> pool = all.Where(x => inst.ok(x.Info)).ToList();
				pool = oldLineup ? WithCatcherBonus(pool, DevTeamColour) : WithDevCatcher(pool);
				Deck d = Build(inst.label, pool, cap);
				// TCSIM_POOL="2013 삼성;…": each batter's estimate (offence, defence per spot he can field).
				string poolPick = Environment.GetEnvironmentVariable("TCSIM_POOL");
				if (!string.IsNullOrEmpty(poolPick) && poolPick.Split(';').Any(l => l.Trim() == inst.label))
				{
					foreach (Cand c in pool.Where(c => c.Info.IsBatter).OrderByDescending(c => c.Off))
					{
						ServerLog.Info($"tcsim pool: {inst.label} {c.Info.Name}{c.Info.Ball} 공격 {c.Off:+0.000;-0.000} 수비 " + string.Join(" ", Enumerable.Range(0, 8).Where(s => !double.IsNaN(c.Def[s])).Select(s => $"{SpotNames[s]}{c.Def[s]:+0.000;-0.000}")));
					}
				}
				if (d == null && !oldLineup && pool.Count(c => c.Info.IsBatter) >= 14 && pool.Count(c => !c.Info.IsBatter) >= 11)
				{
					// Build fills spot by spot and gives up when a spot's only fielders went to an earlier spot
					// ('03 두산, '07 삼성); the staff by role (RoleDeck) and the nine (LineupDeck) make the deck.
					Deck staffed = RoleDeck(new Deck { Label = inst.label }, pool);
					Deck full = LineupDeck(staffed, pool, cap, out _);
					if (full != staffed && full.At.All(c => c != null)) d = full;
				}
				if (d == null && pool.Count > 0)
				{
					ServerLog.Info($"tcsim dev: {inst.label} 덱 불가 (타자 {pool.Count(c => c.Info.IsBatter)}, 투수 {pool.Count(c => !c.Info.IsBatter)}, 볼 최소 합 {pool.Where(c => c.Info.IsBatter).OrderBy(c => c.Info.Ball).Take(14).Sum(c => c.Info.Ball) + pool.Where(c => !c.Info.IsBatter).OrderBy(c => c.Info.Ball).Take(11).Sum(c => c.Info.Ball)})");
				}
				if (d != null)
				{
					d = CapDeck(RoleDeck(d, pool), pool, cap);
					List<LineupPlan> plans = new List<LineupPlan>();
					if (!oldLineup) d = LineupDeck(d, pool, cap, out plans);
					lock (gate)
					{
						decks.Add(d);
						plansOf[d.Label] = plans;
					}
				}
			});
			string[] builds = LeagueRules.AiTraitBuilds[LeagueRules.MajorTier];
			TraitEffects.Effect[] effects = builds.Select(b => TraitEffects.FromPoints(b.Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1])))).ToArray();
			// TCSIM_SCREEN=<games> (user, 2026-10-20: "21롯데보다 나머지 롯데들은 더 쓰레기팀이라는건가?"): every
			// team-year plays each franchise's best-estimate deck that many games (mixed traits), and the top perTeam
			// per franchise by that win rate, not by the builder's estimate, go into the league.
			int screen = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_SCREEN"), out int sg) ? sg : 0;
			List<Deck> built = decks.ToList();
			if (screen > 0)
			{
				List<DevDeck> every = decks.Select(Develop).ToList();
				foreach (DevDeck x in every) x.Traits = effects[Math.Min(2, effects.Length - 1)];
				List<DevDeck> refs = every.GroupBy(x => x.D.At[0].Info.TeamCode).Select(g => g.OrderByDescending(x => x.D.Value).First()).ToList();
				Dictionary<Deck, double> score = new Dictionary<Deck, double>();
				foreach (DevDeck x in every)
				{
					score[x.D] = refs.Where(r => r != x).Average(r => PlayDev(x, r, screen, 3).win);
				}
				ServerLog.Info($"tcsim screen: {every.Count} team-years, each against {refs.Count} franchise decks ({string.Join(", ", refs.Select(r => r.D.Label))}), {screen} games a pairing");
				foreach (var g in decks.GroupBy(d => d.At[0].Info.TeamCode).OrderByDescending(g => g.Max(d => score[d])))
				{
					ServerLog.Info($"tcsim screen: {g.First().Label.Substring(5)} ({g.Count()}): " + string.Join(", ", g.OrderByDescending(d => score[d]).Select(d => $"{d.Label.Substring(0, 4)} {100 * score[d]:F1}%")));
				}
				decks = decks.GroupBy(d => d.At[0].Info.TeamCode).SelectMany(g => g.OrderByDescending(d => score[d]).Take(perTeam)).OrderByDescending(d => score[d]).ToList();
			}
			else
			{
				decks = decks.GroupBy(d => d.At[0].Info.TeamCode).SelectMany(g => g.OrderByDescending(d => d.Value).Take(perTeam)).OrderByDescending(d => d.Value).ToList();
			}
			List<DevDeck> field = decks.Select(Develop).ToList();
			// Trait build: each deck's best of the 메이저 builds against the field's median deck (mixed build).
			DevDeck reference = Develop(decks[decks.Count / 2]);
			reference.Traits = effects[Math.Min(2, effects.Length - 1)];
			foreach (DevDeck x in field)
			{
				double[] w = effects.Select(e => { x.Traits = e; return PlayDev(x, reference, games, 101).win; }).ToArray();
				x.Build = Array.IndexOf(w, w.Max());
				x.Traits = effects[x.Build];
			}
			string[] buildNames = { "타격형", "투수형", "혼합" };
			ServerLog.Info($"tcsim dev: {field.Count} decks (top {perTeam} single team-years per franchise), {games} games a pairing; 팀컬러 +{DevTeamColour}, 재계약 {DevRenewals}x{DevRenewal}, 트레이닝 {DevSlots}x{DevSlotGain}, 특강 {(DevLecture ? $"주 +{DevLectureMain}·보조 +{DevLectureSub}" : "없음")}, 특성 메이저 빌드");
			string show = Environment.GetEnvironmentVariable("TCSIM_SHOW");
			if (!string.IsNullOrEmpty(show))
			{
				foreach (string label in show.Split(';'))
				{
					DevDeck x = field.FirstOrDefault(f => f.D.Label == label.Trim());
					// A team-year outside the field (user: "삼성에 13이 없는건 의외네") is built the same way, with its best build.
					Deck outside = x == null ? built.FirstOrDefault(d => d.Label == label.Trim()) : null;
					if (outside != null)
					{
						x = Develop(outside);
						double[] w = effects.Select(e => { x.Traits = e; return PlayDev(x, reference, games, 101).win; }).ToArray();
						x.Build = Array.IndexOf(w, w.Max());
						x.Traits = effects[x.Build];
					}
					if (x != null) ShowDeck(x, field, Math.Max(200, games / 4));
					if (x != null && plansOf.TryGetValue(x.D.Label, out var shownPlans))
					{
						foreach (LineupPlan alt in shownPlans.Skip(1).Take(5))
						{
							ServerLog.Info($"tcsim show:   차선 {alt.Score - shownPlans[0].Score:+0.000;-0.000;0.000}득점/경기: {PlanDiff(shownPlans[0], alt)}");
						}
					}
					// TCSIM_SWAP="17:21,18:22": the same deck with those slots exchanged (deck slots: 14-18 SP1-5,
					// 19-22 RP1-4, 23 SU, 24 CP) — e.g. its weakest relievers moved into the rotation.
					string swap = Environment.GetEnvironmentVariable("TCSIM_SWAP");
					if (x != null && !string.IsNullOrEmpty(swap))
					{
						Deck swapped = new Deck { Label = x.D.Label + " (자리 바꿈)", At = (Cand[])x.D.At.Clone(), Value = x.D.Value, Balls = x.D.Balls };
						foreach (string pair in swap.Split(','))
						{
							string[] ab = pair.Split(':');
							int a = int.Parse(ab[0]), b = int.Parse(ab[1]);
							(swapped.At[a], swapped.At[b]) = (swapped.At[b], swapped.At[a]);
						}
						DevDeck y = Develop(swapped);
						y.Traits = x.Traits;
						y.Build = x.Build;
						ShowDeck(y, field.Where(f => f != x).ToList(), Math.Max(200, games / 4));
					}
				}
				return 0;
			}
			int n = field.Count;
			double[,] win = new double[n, n];
			double[] rs = new double[n], ra = new double[n];
			for (int i = 0; i < n; i++)
			{
				for (int j = i + 1; j < n; j++)
				{
					var r = PlayDev(field[i], field[j], games, 7 + i * 131 + j);
					win[i, j] = r.win;
					win[j, i] = 1 - r.win;
					rs[i] += r.rf; ra[i] += r.ra; rs[j] += r.ra; ra[j] += r.rf;
				}
			}
			var table = Enumerable.Range(0, n).Select(i => (i, avg: Enumerable.Range(0, n).Where(j => j != i).Average(j => win[i, j]))).OrderByDescending(t => t.avg).ToList();
			// TCSIM_ROW="2021 롯데;…": those decks' results against every other deck (user: who it played).
			string rowPick = Environment.GetEnvironmentVariable("TCSIM_ROW");
			if (!string.IsNullOrEmpty(rowPick))
			{
				foreach (string label in rowPick.Split(';').Select(x => x.Trim()))
				{
					int i = field.FindIndex(f => f.D.Label == label);
					if (i < 0) continue;
					var opp = Enumerable.Range(0, n).Where(j => j != i).OrderByDescending(j => win[i, j]).ToList();
					ServerLog.Info($"tcsim row: {label} 상대별 승률 ({games}경기씩): " + string.Join(", ", opp.Select(j => $"{field[j].D.Label} {100 * win[i, j]:F0}%")));
				}
			}
			ServerLog.Info("tcsim dev: ===== 육성 완료 단일 년도 덱 리그 (평균 승률) =====");
			int rank = 0;
			foreach ((int i, double avg) in table)
			{
				DevDeck x = field[i];
				ServerLog.Info($"tcsim dev: {++rank,2}. {x.D.Label} {x.D.Balls}볼 특성 {buildNames[Math.Min(x.Build, 2)]}: {100 * avg:F1}% (득 {rs[i] / (n - 1):F2} 실 {ra[i] / (n - 1):F2}) | 타선 {Parts(x.D).bat:+0.00;-0.00} 수비 {Parts(x.D).field:+0.00;-0.00} 선발 {Parts(x.D).rotation:+0.00;-0.00} 불펜 {Parts(x.D).pen:+0.00;-0.00}");
			}
			// Each deck's nine (BestLineups) and the next sets with what changes, for the user to check
			// (user: "의문이 생기는 덱이 있으면 나에게 제시할 경우 내가 올바른 포지션을 정해주겠음").
			rank = 0;
			foreach ((int i, double avg) in table)
			{
				DevDeck x = field[i];
				++rank;
				if (!plansOf.TryGetValue(x.D.Label, out var plans) || plans.Count == 0) continue;
				ServerLog.Info($"tcsim lineup: {rank,2}. {x.D.Label}: {PlanText(plans[0])} | 벤치 " + string.Join(" ", Enumerable.Range(Bench, 5).Select(s => $"{x.D.At[s].Info.Name}{x.D.At[s].Info.Ball}")));
				foreach (LineupPlan alt in plans.Skip(1).Take(2))
				{
					ServerLog.Info($"tcsim lineup:       차선 {alt.Score - plans[0].Score:+0.000;-0.000;0.000}득점/경기: {PlanDiff(plans[0], alt)}");
				}
			}
			foreach ((int i, double avg) in table.Take(12))
			{
				DevDeck x = field[i];
				ServerLog.Info($"tcsim dev: {x.D.Label}: {Describe(x.D)}");
				ServerLog.Info($"tcsim dev:    육성 타자 " + string.Join(" ", Enumerable.Range(0, 9).Select(s => $"{x.D.At[s].Info.Name}[{DevText(x.Dev[s], true)}]")));
				ServerLog.Info($"tcsim dev:    육성 투수 " + string.Join(" ", Enumerable.Range(FirstPitcher, 11).Select(s => $"{x.D.At[s].Info.Name}[{DevText(x.Dev[s], false)}]")));
			}
			return 0;
		}

		// TCSIM_SHOW="2001 롯데;2018 두산" (user, 2026-10-20: why is a deck with 1–2 ball starters so high?):
		// the deck against every other deck of the field, with each player's line.
		private static void ShowDeck(DevDeck x, List<DevDeck> field, int games)
		{
			long[,] pit = new long[11, PS.Count];
			long[,] bat = new long[9, BS.Count];
			int[] order = x.Order ?? Enumerable.Range(0, 9).OrderByDescending(s => x.D.At[s].Off).ToArray();
			object gate = new object();
			int played = 0, wins = 0;
			// Per starter slot (DevTeam: game % 5) and runs (user, 2026-10-20: why does '24 롯데 rank low?).
			long[] slotGames = new long[5], slotWins = new long[5];
			long runsFor = 0, runsAgainst = 0;
			foreach (DevDeck opp in field.Where(f => f != x))
			{
				Parallel.For(0, games, () => (new long[11, PS.Count], new long[9, BS.Count], 0, 0), (g, _, acc) =>
				{
					GameTeam ta = DevTeam(x, g), tb = DevTeam(opp, g);
					TeamStrength.ApplyLeagueReference(new[] { ta, tb });
					TraitEffects.Raise(ta, x.Traits);
					TraitEffects.Raise(tb, opp.Traits);
					bool aHome = g % 2 == 0;
					SeasonModel m = aHome ? CardGame.Build(ta, tb, s_data.Env) : CardGame.Build(tb, ta, s_data.Env);
					int side = aHome ? 0 : 1;
					TraitEffects.Apply(m, side, x.Traits);
					TraitEffects.Apply(m, 1 - side, opp.Traits);
					SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(g * 7919 + 13 + 555)).PlayOne(0, 1, null);
					for (int s = 0; s < 11 && s < m.TeamPitchers[side].Length; s++)
						for (int k = 0; k < PS.Count; k++) acc.Item1[s, k] += st.Pit(m.TeamPitchers[side][s], k);
					for (int i = 0; i < 9 && i < m.TeamBatters[side].Length; i++)
						for (int k = 0; k < BS.Count; k++) acc.Item2[i, k] += (long)st.Bat(m.TeamBatters[side][i], k);
					int r0 = m.TeamBatters[side].Sum(i => (int)st.Bat(i, BS.R)), r1 = m.TeamBatters[1 - side].Sum(i => (int)st.Bat(i, BS.R));
					System.Threading.Interlocked.Increment(ref slotGames[g % 5]);
					if (r0 > r1) System.Threading.Interlocked.Increment(ref slotWins[g % 5]);
					System.Threading.Interlocked.Add(ref runsFor, r0);
					System.Threading.Interlocked.Add(ref runsAgainst, r1);
					return (acc.Item1, acc.Item2, acc.Item3 + 1, acc.Item4 + (r0 > r1 ? 1 : 0));
				}, acc =>
				{
					lock (gate)
					{
						for (int s = 0; s < 11; s++) for (int k = 0; k < PS.Count; k++) pit[s, k] += acc.Item1[s, k];
						for (int i = 0; i < 9; i++) for (int k = 0; k < BS.Count; k++) bat[i, k] += acc.Item2[i, k];
						played += acc.Item3;
						wins += acc.Item4;
					}
				});
			}
			string[] roles = { "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };
			string[] posNames = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" };
			ServerLog.Info($"tcsim show: {x.D.Label} {x.D.Balls}볼, {played}경기 (상대 {field.Count - 1}덱), 승 {wins} ({100.0 * wins / Math.Max(1, played):F1}%, 무 제외), 득 {(double)runsFor / Math.Max(1, played):F2} 실 {(double)runsAgainst / Math.Max(1, played):F2}");
			ServerLog.Info($"tcsim show:   선발별 승률: " + string.Join(", ", Enumerable.Range(0, 5).Select(k => $"SP{k + 1} {x.D.At[FirstPitcher + k].Info.Name} {100.0 * slotWins[k] / Math.Max(1, slotGames[k]):F1}%")));
			var parts = Parts(x.D);
			ServerLog.Info($"tcsim show:   추정 (리그 평균 대비 득점/경기): 타선 {parts.bat:+0.00;-0.00} 수비 {parts.field:+0.00;-0.00} 선발 {parts.rotation:+0.00;-0.00} 불펜 {parts.pen:+0.00;-0.00}");
			for (int s = 0; s < 11; s++)
			{
				Cand c = x.D.At[FirstPitcher + s];
				double ip = pit[s, PS.Outs] / 3.0;
				double era = ip > 0 ? 9.0 * pit[s, PS.ER] / ip : 0;
				string r = string.Join("/", Enumerable.Range(0, 5).Select(k => c.Row.R[k].ToString("F0")));
				ServerLog.Info($"tcsim show:   {roles[s],-3} {c.Info.Name} {c.Info.Ball}볼 (체구컨테위 {r}) | 선발 {pit[s, PS.GS]} 이닝 {ip / played:F2}/경기 ERA {era:F2} WHIP {(ip > 0 ? (pit[s, PS.H] + pit[s, PS.BB]) / ip : 0):F2} K/9 {(ip > 0 ? 9.0 * pit[s, PS.SO] / ip : 0):F1} 승 {pit[s, PS.W]} 패 {pit[s, PS.L]}");
			}
			for (int i = 0; i < 9; i++)
			{
				Cand c = x.D.At[order[i]];
				long pa = bat[i, BS.PA], ab = bat[i, BS.AB], h = bat[i, BS.H];
				double avg = ab > 0 ? (double)h / ab : 0, obp = pa > 0 ? (double)(h + bat[i, BS.BB] + bat[i, BS.HBP]) / pa : 0;
				double slg = ab > 0 ? (double)(h + bat[i, BS.D] + 2 * bat[i, BS.T] + 3 * bat[i, BS.HR]) / ab : 0;
				string r = string.Join("/", new[] { 0, 1, 2, 3 }.Select(k => c.Row.R[k].ToString("F0")));
				ServerLog.Info($"tcsim show:   {i + 1}번 {posNames[order[i]],-2} {c.Info.Name} {c.Info.Ball}볼 (컨파선주 {r}) | 타율 {avg:F3} 출루 {obp:F3} 장타 {slg:F3} OPS {obp + slg:F3} 홈런 {(double)bat[i, BS.HR] / played * 144:F0}/144경기");
			}
		}

		// The builder's own estimate of a deck's parts, in runs a game against a league-average club:
		// lineup (the nine's offence), fielding (C..RF, the catcher's boost included), rotation, bullpen.
		private static (double bat, double field, double rotation, double pen) Parts(Deck d)
		{
			double bat = Enumerable.Range(0, 9).Sum(s => d.At[s].Off);
			double field = Enumerable.Range(0, 8).Sum(s => double.IsNaN(d.At[s].Def[s]) ? -15 : d.At[s].Def[s]);
			double rotation = Enumerable.Range(FirstPitcher, 5).Sum(s => d.At[s].Starter);
			double pen = Enumerable.Range(FirstPitcher + 5, 6).Sum(s => Value(d.At[s], s));
			return (bat, field, rotation, pen);
		}

		// "--tcsim [cap] [games] devstats" (user, 2026-10-20: 선수 육성 가이드): on decks that already have
		// team colour S (+19) and traits, what six 재계약 (+18 in one stat) are worth — for the whole
		// lineup or staff, and for one player at a time (C, SS, cleanup hitter, SP1, SP5, closer, set-up,
		// middle reliever) — against the same deck without them, same seeds. TCSIM_DECKS picks the decks.
		private static int RunDevStats(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int pts = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_POINTS"), out int p) ? p : 18;
			string pick = Environment.GetEnvironmentVariable("TCSIM_DECKS");
			string[] labels = string.IsNullOrEmpty(pick) ? new[] { "2018 두산", "2009 SK", "2023 LG" } : pick.Split(';').Select(x => x.Trim()).ToArray();
			string[] builds = LeagueRules.AiTraitBuilds[LeagueRules.MajorTier];
			TraitEffects.Effect mixed = TraitEffects.FromPoints(builds[Math.Min(2, builds.Length - 1)].Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1])));
			foreach (string label in labels)
			{
				var inst = families.First(f => f.name == "단일 년도 팀").instances.FirstOrDefault(i => i.label == label);
				if (inst.ok == null) continue;
				Deck d = Build(label, WithCatcherBonus(all.Where(x => inst.ok(x.Info)).ToList(), DevTeamColour), cap);
				if (d == null) continue;
				DevDeck Base()
				{
					DevDeck x = new DevDeck { D = d, Traits = mixed };
					for (int s = 0; s < Slots; s++)
					{
						x.Dev[s] = new TeamCard { Idx = d.At[s].Info.Idx };
						for (int k = 0; k < 6; k++) x.Dev[s].TeamColor[k] = DevTeamColour;
					}
					return x;
				}
				// TCSIM_SPLIT_PLAYER="이대호", TCSIM_TRAIN="0,4,0,8,0,0", TCSIM_SPLITS="1,7,10;0,18,0;…" (user,
				// 2026-10-20: "11이대호 … 재계약으로 컨택 1, 파워 7, 선구 10을 찍어서 110, 110, 110을 만드는거 어때?"):
				// one batter's renewal points split over 컨택/파워/선구, the deck arranged as the league plays it.
				string splitPlayer = Environment.GetEnvironmentVariable("TCSIM_SPLIT_PLAYER");
				if (!string.IsNullOrEmpty(splitPlayer))
				{
					List<Cand> pool = WithDevCatcher(all.Where(x => inst.ok(x.Info)).ToList());
					Deck arranged = LineupDeck(RoleDeck(d, pool), pool, cap, out _);
					int slot = Enumerable.Range(0, 9).FirstOrDefault(s => arranged.At[s].Info.Name == splitPlayer, -1);
					if (slot < 0) continue;
					int[] train = (Environment.GetEnvironmentVariable("TCSIM_TRAIN") ?? "0,0,0,0,0,0").Split(',').Select(int.Parse).ToArray();
					DevDeck Arranged(int[] contract)
					{
						DevDeck x = new DevDeck { D = arranged, Traits = mixed, Order = BestOrder(arranged) };
						x.Lineup = LineupOf(arranged, x.Order);
						for (int s = 0; s < Slots; s++)
						{
							x.Dev[s] = new TeamCard { Idx = arranged.At[s].Info.Idx };
							for (int k = 0; k < 6; k++) x.Dev[s].TeamColor[k] = DevTeamColour;
						}
						for (int k = 0; k < 6; k++)
						{
							x.Dev[slot].Training[k] = (sbyte)train[k];
							x.Dev[slot].Contract[k] = (sbyte)contract[k];
						}
						return x;
					}
					DevDeck plain = Arranged(new int[6]);
					Cand who = arranged.At[slot];
					// Stat k as shown and as played: card + 0.374 × the bonus counted on the shown curve
					// (TeamStrength.Counted), half past the in-play knee (CardRules.KneeX).
					string Line(int[] contract) => string.Join(" ", new[] { 0, 1, 3 }.Select(k =>
					{
						double card = who.Row.R[BatRowIndex[k]];
						int bonus = DevTeamColour + train[k] + contract[k];
						double play = card + TeamStrength.DevWeight * TeamStrength.DevScale * TeamStrength.BatDevStat[BatRowIndex[k]] * TeamStrength.Counted(card, bonus);
						double knee = TeamStrength.InPlayKnee;
						double eff = play > knee ? knee + 0.5 * (play - knee) : play;
						return $"{BatStatNames[k]} {card + bonus:F0}(경기 {play:F1}→{eff:F1})";
					}));
					ServerLog.Info($"tcsim split: {label} {who.Info.Name} {SpotNames[slot]}, {games}경기씩, 기준(재계약 없음) {Line(new int[6])}");
					foreach (string spec in (Environment.GetEnvironmentVariable("TCSIM_SPLITS") ?? "1,7,10").Split(';'))
					{
						int[] cpb = spec.Split(',').Select(int.Parse).ToArray();
						int[] contract = new int[6];
						contract[0] = cpb[0]; contract[1] = cpb[1]; contract[3] = cpb[2];
						var r = PlayDev(Arranged(contract), plain, games, int.TryParse(Environment.GetEnvironmentVariable("TCSIM_SALT"), out int salt) ? salt : 5);
						ServerLog.Info($"tcsim split:   컨{cpb[0]} 파{cpb[1]} 선{cpb[2]}: 승률 {100 * (r.win - 0.5):+0.00;-0.00}%p, 득실 {r.rf - r.ra:+0.000;-0.000}/경기 | {Line(contract)}");
					}
					continue;
				}
				DevDeck baseDeck = Base();
				var baseline = PlayDev(baseDeck, baseDeck, games, 3);
				int[] order = Enumerable.Range(0, 9).OrderByDescending(s => d.At[s].Off).ToArray();
				int cleanup = order[3];
				ServerLog.Info($"tcsim devstats: {label} (팀컬러 +{DevTeamColour}, 특성 혼합형) +{pts}점 = 재계약 {pts / 3}번, {games}경기씩; 포수 {d.At[0].Info.Name} 수비 {d.At[0].Row.Defs[0]:F0}, 유격수 {d.At[4].Info.Name} 수비 {d.At[4].Row.Defs[4]:F0}, 4번 {d.At[cleanup].Info.Name}, 1선발 {d.At[FirstPitcher].Info.Name}, 마무리 {d.At[Slots - 1].Info.Name}");
				void Report(string who, string stat, IEnumerable<int> slots, int k)
				{
					DevDeck x = Base();
					foreach (int s in slots) x.Dev[s].Contract[k] = (sbyte)pts;
					var r = PlayDev(x, baseDeck, games, 3);
					ServerLog.Info($"tcsim devstats: {label} | {who} {stat} +{pts}: 승률 {100 * (r.win - baseline.win):+0.0;-0.0}%p, 득실 {(r.rf - r.ra) - (baseline.rf - baseline.ra):+0.00;-0.00}/경기");
				}
				int[] batters = Enumerable.Range(0, 9).ToArray(), pitchers = Enumerable.Range(FirstPitcher, 11).ToArray();
				for (int k = 0; k < 6; k++) Report("타자 전원", BatStatNames[k], batters, k);
				for (int k = 0; k < 6; k++) Report("투수 전원", PitStatNames[k], pitchers, k);
				foreach (int k in new[] { 0, 1, 3, 4 }) Report($"포수 {d.At[0].Info.Name}", BatStatNames[k], new[] { 0 }, k);
				foreach (int k in new[] { 0, 4 }) Report($"유격수 {d.At[4].Info.Name}", BatStatNames[k], new[] { 4 }, k);
				foreach (int k in new[] { 0, 1, 3 }) Report($"4번 {d.At[cleanup].Info.Name}", BatStatNames[k], new[] { cleanup }, k);
				foreach (int k in new[] { 0, 1, 2, 3, 4 }) Report($"1선발 {d.At[FirstPitcher].Info.Name}", PitStatNames[k], new[] { FirstPitcher }, k);
				foreach (int k in new[] { 0, 4 }) Report($"5선발 {d.At[FirstPitcher + 4].Info.Name}", PitStatNames[k], new[] { FirstPitcher + 4 }, k);
				Report($"마무리 {d.At[Slots - 1].Info.Name}", "구위", new[] { Slots - 1 }, 4);
				Report($"셋업 {d.At[Slots - 2].Info.Name}", "구위", new[] { Slots - 2 }, 4);
				Report($"중계1 {d.At[FirstPitcher + 5].Info.Name}", "구위", new[] { FirstPitcher + 5 }, 4);
			}
			return 0;
		}

		private static string DevText(TeamCard d, bool bat)
		{
			string[] names = bat ? BatStatNames : PitStatNames;
			return string.Join(",", Enumerable.Range(0, 6).Where(k => d.Contract[k] + d.Training[k] + d.Educated[k] > 0).Select(k => $"{names[k]}+{d.Contract[k] + d.Training[k] + d.Educated[k]}"));
		}

		// Copies of the pool with each catcher's value at his card defence plus the batters' team-colour
		// bonus (shown defence, as FieldRules.CatcherBoost reads it).
		private static List<Cand> WithCatcherBonus(List<Cand> pool, int bonus)
		{
			double from = s_data.Env.Field.CatcherBoostFrom;
			return pool.Select(c =>
			{
				if (!c.Info.IsBatter || !c.Info.CanField(0)) return c;
				Cand copy = new Cand { Info = c.Info, Row = c.Row, Off = c.Off, Stuff = c.Stuff, Starter = c.Starter, Def = (double[])c.Def.Clone() };
				copy.Def[0] += CatcherBoostRuns * (Math.Max(0, c.Row.Defs[0] + bonus - from) - Math.Max(0, c.Row.Defs[0] - from));
				return copy;
			}).ToList();
		}

		// The developed catcher's shown defence as DevelopCard raises it: past 90 with the team colour and one
		// renewal, every renewal and a training slot go to 포수 수비 (+41 in all); otherwise the team colour only.
		private static int DevCatcherBonus(double defence)
		{
			int full = DevTeamColour + DevRenewals * DevRenewal + DevSlotGain;
			return defence + DevTeamColour + DevRenewal > s_data.Env.Field.CatcherBoostFrom ? full : DevTeamColour;
		}

		private static List<Cand> WithDevCatcher(List<Cand> pool) =>
			pool.Select(c => c.Info.IsBatter && c.Info.CanField(0) ? WithCatcherBonus(new List<Cand> { c }, DevCatcherBonus(c.Row.Defs[0]))[0] : c).ToList();

		private static (int bat, int pit) BestTotals(Deck d) =>
			TeamColors.BestTotals(d.At.Take(FirstPitcher).Where(c => c != null).Select(c => c.Info).ToList(), d.At.Skip(FirstPitcher).Where(c => c != null).Select(c => c.Info).ToList());

		// Play with separate bonus arrays (TeamCard order) for the deck's batters and pitchers; the
		// opponent plays without. Same seeds as Play.
		private static (double win, double rf, double ra) PlayBP(Deck deck, Deck opp, sbyte[] bat, sbyte[] pit, int games, int salt, int batSlot = -1)
		{
			double wins = 0, rf = 0, ra = 0;
			object gate = new object();
			Parallel.For(0, games, () => (0.0, 0.0, 0.0), (g, _, acc) =>
			{
				GameTeam a = TeamBP(deck, bat, pit, g, batSlot), b = TeamBP(opp, null, null, g);
				TeamStrength.ApplyLeagueReference(new[] { a, b });
				bool aHome = g % 2 == 0;
				SeasonModel m = aHome ? CardGame.Build(a, b, s_data.Env) : CardGame.Build(b, a, s_data.Env);
				SimStats st = new SeasonSimulator(m, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError }, (ulong)(g * 7919 + 13)).PlayOne(0, 1, null);
				int x = m.TeamBatters[aHome ? 0 : 1].Sum(i => (int)st.Bat(i, BS.R));
				int y = m.TeamBatters[aHome ? 1 : 0].Sum(i => (int)st.Bat(i, BS.R));
				return (acc.Item1 + (x > y ? 1 : x == y ? 0.5 : 0), acc.Item2 + x, acc.Item3 + y);
			}, acc =>
			{
				lock (gate)
				{
					wins += acc.Item1;
					rf += acc.Item2;
					ra += acc.Item3;
				}
			});
			return (wins / games, rf / games, ra / games);
		}

		// batSlot ≥ 0: the batter bonus goes only to the one fielding that spot (0 = C … 7 = RF).
		private static GameTeam TeamBP(Deck d, sbyte[] bat, sbyte[] pit, int game, int batSlot = -1)
		{
			GameTeam t = new GameTeam { Name = d.Label };
			TeamCard tb = new TeamCard(), tp = new TeamCard();
			if (bat != null) tb.TeamColor = (sbyte[])bat.Clone();
			if (pit != null) tp.TeamColor = (sbyte[])pit.Clone();
			int[] order = Enumerable.Range(0, 9).OrderByDescending(s => d.At[s].Off).ToArray();
			for (int i = 0; i < 9; i++)
			{
				t.Order[i] = s_data.Card(d.At[order[i]].Info.Idx, 2);
				t.Position[i] = order[i];
				if (bat != null && (batSlot < 0 || order[i] == batSlot)) TeamStrength.AddBonuses(t.Order[i], tb);
			}
			for (int s = 0; s < 11; s++)
			{
				t.Pitchers[s] = s_data.Card(d.At[FirstPitcher + s].Info.Idx, 2);
				if (pit != null) TeamStrength.AddBonuses(t.Pitchers[s], tp);
			}
			t.StarterSlot = game % 5;
			return t;
		}
	}
}
