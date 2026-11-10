using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	public static partial class TeamColorSim
	{
		// "--tcsim [cap] [games] plan" (user, 2026-10-22: "01롯데, 11롯데, 08롯데, 12삼성, 25LG 팀의 선수에 대해 추천하는 육성
		// 방법 … 수치적으로 자세히"). TCSIM_DECKS picks single team-year decks; each is built and arranged as the league
		// plays it, with team colour S (+DevTeamColour everywhere) and the mixed traits. For every starter (the nine and all
		// eleven pitchers): the win% from +TCSIM_POINTS (default 9 = three 재계약) in each useful stat, and from each 특별수강
		// coach the card can take (LessonHandlers: main +4, the two subs +2), against the same deck without, same seeds.
		// Data copy only (the server's startup writes to the data directory).
		//
		// 2026-10-26 (user: "12삼성 … 선발이 77766인데 왜 5선발에 4볼 차우찬을 넣어놨지?? 그리고 중계는 왜 작성한게 없을까?"):
		// - The builder picks starters by Starter = Stuff × innings, which ranks a below-average pitcher higher the shorter
		//   he lasts (차우찬 over 고든). TCSIM_STAFF gives the user's own staff instead, eleven names per deck in the order
		//   SP1-5, RP1-4, SU, CP: "2012 삼성=배영수,장원삼,…;2025 LG=…,김진성@RP,…,김영우@SU!,…". "@SP|RP|SU|CP" plays a
		//   포지션 변경 (성공, or 대성공 with a trailing "!"). A "<deck>#<tag>" entry in TCSIM_DECKS is a variant of the
		//   deck given before it: only its win% against that deck is measured.
		// - RP1-4 are measured too, and each starter's win% when he starts every game (the deck's rotation as the
		//   opponent), which is what the SP order should follow.
		// - TCSIM_ALT="2011 롯데|RP4|이재곤;…": the deck's win% with that card in that slot instead.
		// - TCSIM_PLAN_PITCHERS=1 skips the nine batters; TCSIM_PLAN_SLOTS="2011 롯데=RP3;…" measures only those pitchers
		//   of that deck (no 선발 등판 승률 unless an SP is listed).
		//
		// 2026-10-27 (development curve, TeamStrength.DevCurve): the card value printed with each stat carries "*" when team
		// colour S alone takes its shown value to 100 (each further point counts 75%) and "**" at 120 (50%). The 특강 coaches
		// are measured at main +TCSIM_LECTURE_MAIN (default 5, as the user plans them: 행운권 and 초기화권 until +5).
		//
		// 2026-10-31 (new fielding engine, MA9M_FIELD_ENGINE=1: range from every fielder's 수비, infield hits and double-play
		// turns from 주력, 정신 reworked; user 10-30: 파워·정신·수비 seemed worth too little): every batter's six stats and the
		// pitchers' 정신 too. TCSIM_PLAN_COACHES=0 leaves the coaches unmeasured (make_guide.py values them from the stats'
		// results): "특강 계산 <eligible names>".
		//
		// 2026-11-05 (user: "코칭스태프 보너스, 트레이닝북 1권당 최대 6의 스탯 증가 등을 고려해서 육성표를 다시 점검"): TCSIM_COACH_SET
		// (0-3) puts the 코칭스태프 set bonus on every stat of every card (TeamCard.Coach, as CoachHandlers.FillBonus does for a
		// single-club staff), so the gains are measured where a developed deck starts.
		private static readonly string[] PlanRoles = { "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };

		private static readonly Dictionary<string, int> PlanRoleCodes = new Dictionary<string, int> { ["SP"] = 12, ["RP"] = 13, ["SU"] = 14, ["CP"] = 15 };

		private static readonly Dictionary<int, string> PlanRoleNames = new Dictionary<int, string> { [12] = "선발", [13] = "중계", [14] = "셋업", [15] = "마무리" };

		// "김진성@RP" → (김진성, position_training of 성공 to RP); "김영우@SU!" → 대성공; a plain name → no change.
		// A card name without the 동명이인 letter that tools/scripts/namesake_names.py adds to the tables (2026-11-02), so the
		// staffs written before the letters keep matching.
		private static string NamesakeBase(string name) => name.Length > 1 && name[^1] >= 'A' && name[^1] <= 'Z' ? name[..^1] : name;

		private static (string name, byte pti) StaffEntry(string text)
		{
			string t = text.Trim();
			bool great = t.EndsWith("!");
			t = t.TrimEnd('!');
			int at = t.IndexOf('@');
			if (at < 0 || !PlanRoleCodes.TryGetValue(t.Substring(at + 1), out int role))
			{
				return (at < 0 ? t : t.Substring(0, at), 0);
			}
			return (t.Substring(0, at), (byte)((role - 11) * 10 + (great ? 1 : 2)));
		}

		private static int RunPlan(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			int pts = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_POINTS"), out int p) ? p : 9;
			int lectureMain = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_LECTURE_MAIN"), out int lm) ? lm : 5;
			int coachSet = int.TryParse(Environment.GetEnvironmentVariable("TCSIM_COACH_SET"), out int cs) ? cs : 0;
			string pick = Environment.GetEnvironmentVariable("TCSIM_DECKS") ?? "2011 롯데";
			string[] builds = LeagueRules.AiTraitBuilds[LeagueRules.MajorTier];
			TraitEffects.Effect mixed = TraitEffects.FromPoints(builds[Math.Min(2, builds.Length - 1)].Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1])));
			Dictionary<string, string> staffOf = (Environment.GetEnvironmentVariable("TCSIM_STAFF") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
			List<(string deck, string slot, string name)> alts = (Environment.GetEnvironmentVariable("TCSIM_ALT") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('|')).Where(x => x.Length == 3).Select(x => (x[0].Trim(), x[1].Trim(), x[2].Trim())).ToList();
			bool pitchersOnly = Environment.GetEnvironmentVariable("TCSIM_PLAN_PITCHERS") == "1";
			bool coachesMeasured = Environment.GetEnvironmentVariable("TCSIM_PLAN_COACHES") != "0";
			Dictionary<string, HashSet<string>> slotsOf = (Environment.GetEnvironmentVariable("TCSIM_PLAN_SLOTS") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => new HashSet<string>(kv[1].Split(',').Select(x => x.Trim())));
			Dictionary<string, string> batsOf = BatsSpecs();
			Dictionary<string, (DevDeck plain, double win)> measured = new Dictionary<string, (DevDeck, double)>();
			foreach (string label in pick.Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
			{
				string baseLabel = label.Split('#')[0].Trim();
				var inst = families.First(f => f.name == "단일 년도 팀").instances.FirstOrDefault(i => i.label == baseLabel);
				if (inst.ok == null)
				{
					ServerLog.Info($"tcsim plan: {label} 없음");
					continue;
				}
				List<Cand> pool = WithDevCatcher(all.Where(x => inst.ok(x.Info)).ToList());
				Deck d = Build(baseLabel, pool, cap) ?? RoleDeck(new Deck { Label = baseLabel }, pool);
				Deck arranged = LineupDeck(RoleDeck(d, pool), pool, cap, out _);
				if (arranged.At.Any(c => c == null))
				{
					ServerLog.Info($"tcsim plan: {label} 덱을 못 만듦");
					continue;
				}
				// The name as written, else the card's name without its 동명이인 letter (tables since 2026-11-02: 전준호B …).
				// A card idx also works (a 레전드, TeamColorSim.Legends.cs).
				Cand Pitcher(string name) => FindCard(name, false, pool);
				byte[] pti = new byte[11];
				// TCSIM_BATS (TeamColorSim.Legends.cs): chosen nine, bench and batting order.
				int[] fixedOrder = null;
				if (batsOf.TryGetValue(label, out string bats))
				{
					Deck withBats = new Deck { Label = label, At = (Cand[])arranged.At.Clone() };
					string problem = ApplyBats(withBats, bats, pool, out fixedOrder);
					if (problem != null)
					{
						ServerLog.Info($"tcsim plan: {label} 타선 {problem}");
						continue;
					}
					withBats.Balls = withBats.At.Sum(c => c.Info.Ball);
					arranged = withBats;
				}
				// A staff the builder chose plays its 포지션 변경 (a role filled from another one, RoleDeck/ChangeFor).
				if (!staffOf.ContainsKey(label))
				{
					for (int r = 0; r < 11; r++) pti[r] = ChangeFor(arranged.At[FirstPitcher + r], FirstPitcher + r);
				}
				if (staffOf.TryGetValue(label, out string staff))
				{
					string[] names = staff.Split(',');
					Deck own = new Deck { Label = label, At = (Cand[])arranged.At.Clone() };
					string missing = null;
					for (int r = 0; r < 11 && r < names.Length; r++)
					{
						(string name, byte changed) = StaffEntry(names[r]);
						Cand c = Pitcher(name);
						if (c == null)
						{
							missing = name;
							break;
						}
						own.At[FirstPitcher + r] = c;
						pti[r] = changed;
					}
					if (names.Length != 11 || missing != null)
					{
						ServerLog.Info($"tcsim plan: {label} 투수진 {(missing != null ? missing + " 없음" : $"{names.Length}명(11명이어야 함)")}");
						continue;
					}
					own.Balls = own.At.Sum(c => c.Info.Ball);
					arranged = own;
				}
				string twiceProblem = LegendLimit(arranged.At);
				var twice = arranged.At.Where(c => c != null).GroupBy(c => (c.Info.IsBatter, c.Info.Sidx)).Where(g => g.Count() > 1).Select(g => string.Join("·", g.Select(c => c.Info.Name))).ToList();
				if (twice.Count > 0 || twiceProblem != null)
				{
					ServerLog.Info($"tcsim plan: {label} {(twice.Count > 0 ? "같은 선수 두 장: " + string.Join(", ", twice) : twiceProblem)}");
					continue;
				}
				int[] order = fixedOrder ?? BestOrder(arranged);
				if (batsOf.ContainsKey(label)) ServerLog.Info($"tcsim plan: {label} 타선 {BatsText(arranged, order)}");
				DevDeck Make(Action<DevDeck> change)
				{
					DevDeck x = new DevDeck { D = arranged, Traits = mixed, Order = order };
					x.Lineup = LineupOf(arranged, order);
					for (int s = 0; s < Slots; s++)
					{
						x.Dev[s] = new TeamCard { Idx = arranged.At[s].Info.Idx };
						for (int k = 0; k < 6; k++)
						{
							x.Dev[s].TeamColor[k] = DevTeamColour;
							x.Dev[s].Coach[k] = (sbyte)coachSet;
						}
					}
					for (int r = 0; r < 11; r++) x.Dev[FirstPitcher + r].PositionTrainingInfo = pti[r];
					change?.Invoke(x);
					return x;
				}
				// Another card in slot s (TCSIM_ALT), with its own 포지션 변경.
				void Swap(DevDeck x, int s, Cand c, byte changed)
				{
					Deck other = new Deck { Label = arranged.Label, At = (Cand[])x.D.At.Clone() };
					other.At[s] = c;
					x.D = other;
					x.Lineup = LineupOf(other, order);
					x.Dev[s] = new TeamCard { Idx = c.Info.Idx, PositionTrainingInfo = changed };
					for (int k = 0; k < 6; k++)
					{
						x.Dev[s].TeamColor[k] = DevTeamColour;
						x.Dev[s].Coach[k] = (sbyte)coachSet;
					}
				}
				string Who(int r)
				{
					Cand c = arranged.At[FirstPitcher + r];
					if (pti[r] % 10 == 0) return c.Info.Name;
					int to = pti[r] / 10 + 11;
					return $"{c.Info.Name}({PlanRoleNames[(int)c.Info.MainPosition]}→{PlanRoleNames[to]}{(pti[r] % 10 == 1 ? "·대성공" : string.Empty)})";
				}
				DevDeck plain = Make(null);
				int salt = 17;
				// The same deck against itself first: the side and seed offset, taken off every change.
				double baseWin = PlayDev(plain, plain, games, salt).win;
				double Gain(Action<DevDeck> change) => 100 * (PlayDev(Make(change), plain, games, salt).win - baseWin);
				string staffLine = string.Join(", ", Enumerable.Range(0, 11).Select(r => $"{PlanRoles[r]} {Who(r)} {arranged.At[FirstPitcher + r].Info.Ball}"));
				if (label.Contains('#'))
				{
					if (!measured.TryGetValue(baseLabel, out var main))
					{
						ServerLog.Info($"tcsim plan: {label} 변형 | 기본 덱({baseLabel})을 먼저 재야 함");
						continue;
					}
					double vs = 100 * (PlayDev(plain, main.plain, games, salt).win - main.win);
					ServerLog.Info($"tcsim plan: {label} 변형 | 기본 덱 대비 {vs:+0.00;-0.00;0.00}%p ({arranged.Balls}볼) | {staffLine}");
					continue;
				}
				measured[label] = (plain, baseWin);
				// Card stat k (with its 포지션 변경) and where team colour S alone takes its shown value: "*" 100 or more, "**" 120.
				string Stat(Cand c, int k, int moved = 0)
				{
					double card = (c.Info.IsBatter ? c.Row.R[BatRowIndex[k]] : c.Row.R[k]) + moved;
					double shown = card + DevTeamColour + coachSet;
					return $"{card:F0}{(shown >= 120 ? "**" : shown >= 100 ? "*" : string.Empty)}";
				}
				ServerLog.Info($"tcsim plan: ===== {label} ({arranged.Balls}볼, 팀컬러 S +{DevTeamColour}, 코칭스태프 +{coachSet}, 특성 혼합형): +{pts}점마다 승률 변화, {games}경기씩, 기준 {100 * baseWin:F2}% =====");
				ServerLog.Info($"tcsim plan: {label} 투수진 | {staffLine}");
				// TCSIM_BULLPEN=<points> (2026-11-05, 중계 체력 on league days with TCSIM_SERIES): the whole bullpen (RP1-CP)
				// +points in one stat at a time; a bigger change than one arm's, so it stands out of the chains' noise.
				if (int.TryParse(Environment.GetEnvironmentVariable("TCSIM_BULLPEN"), out int pen) && pen > 0)
				{
					string penParts = string.Join(", ", Enumerable.Range(0, 6).Select(k => $"{PitStatNames[k]} {Gain(x => { for (int r = 5; r < 11; r++) x.Dev[FirstPitcher + r].Contract[k] = (sbyte)pen; }):+0.00;-0.00;0.00}"));
					ServerLog.Info($"tcsim plan: {label} 불펜 전체 +{pen}{(s_series > 0 ? $" (연속 {s_series}경기)" : " (한 경기씩)")} | {penParts}");
				}
				slotsOf.TryGetValue(label, out HashSet<string> only);
				// "B<n>" in TCSIM_PLAN_SLOTS (2026-11-10, a 레전드 batter's slot): only those batting slots; none = all nine.
				bool someBatters = only != null && only.Any(x => x.Length > 1 && x[0] == 'B' && char.IsDigit(x[1]));
				if (!pitchersOnly)
				{
					for (int i = 0; i < 9; i++)
					{
						if (someBatters && !only.Contains($"B{i + 1}")) continue;
						int s = order[i];
						Cand c = arranged.At[s];
						int[] stats = { 0, 1, 2, 3, 4, 5 };
						string parts = string.Join(", ", stats.Select(k => $"{BatStatNames[k]}({Stat(c, k)}) {Gain(x => x.Dev[s].Contract[k] = (sbyte)pts):+0.00;-0.00;0.00}"));
						List<LessonHandlers.Coach> eligible = LessonHandlers.Batting.Where(co => LessonHandlers.Eligible(co, c.Info)).ToList();
						string coaches = !coachesMeasured ? "계산 " + string.Join(", ", eligible.Select(co => co.Name)) : string.Join(", ", eligible.Select(co =>
							$"{co.Name}[{BatStatNames[co.Main]}+{lectureMain}·{BatStatNames[co.Sub1]}·{BatStatNames[co.Sub2]}+2] {Gain(x => { x.Dev[s].Educated[co.Main] += (sbyte)lectureMain; x.Dev[s].Educated[co.Sub1] += 2; x.Dev[s].Educated[co.Sub2] += 2; }):+0.00;-0.00;0.00}"));
						ServerLog.Info($"tcsim plan: {label} | {i + 1}번 {SpotNames[s]} {c.Info.Name} {c.Info.Ball}볼 {c.Info.Age}세{(c.Info.Foreigner ? " 용병" : string.Empty)} | {parts} | 특강 {coaches}");
					}
				}
				// Each starter's win% when he starts every game, against the deck with its rotation (50% = the deck's average start).
				if (only == null || only.Any(x => x.StartsWith("SP")))
				{
					string starts = string.Join(", ", Enumerable.Range(0, 5).Select(r => $"{PlanRoles[r]} {Who(r)} {50 + Gain(x => x.FixedStarter = r):F2}%"));
					ServerLog.Info($"tcsim plan: {label} 선발 등판 승률 | {starts}");
				}
				for (int r = 0; r < 11; r++)
				{
					if (only != null && !only.Contains(PlanRoles[r])) continue;
					int s = FirstPitcher + r;
					Cand c = arranged.At[s];
					sbyte[] moved = PositionChangeHandlers.DeltaOf(c.Info.Idx, pti[r]);
					// SU/CP 체력 only matters across league days (TCSIM_SERIES).
					int[] stats = r < 9 || s_series > 0 ? new[] { 0, 1, 2, 3, 4, 5 } : new[] { 1, 2, 3, 4, 5 };
					string parts = string.Join(", ", stats.Select(k => $"{PitStatNames[k]}({Stat(c, k, moved[k])}) {Gain(x => x.Dev[s].Contract[k] = (sbyte)pts):+0.00;-0.00;0.00}"));
					List<LessonHandlers.Coach> eligible = LessonHandlers.Pitching.Where(co => LessonHandlers.Eligible(co, c.Info)).ToList();
					string coaches = !coachesMeasured ? "계산 " + string.Join(", ", eligible.Select(co => co.Name)) : string.Join(", ", eligible.Select(co =>
						$"{co.Name}[{PitStatNames[co.Main]}+{lectureMain}·{PitStatNames[co.Sub1]}·{PitStatNames[co.Sub2]}+2] {Gain(x => { x.Dev[s].Educated[co.Main] += (sbyte)lectureMain; x.Dev[s].Educated[co.Sub1] += 2; x.Dev[s].Educated[co.Sub2] += 2; }):+0.00;-0.00;0.00}"));
					ServerLog.Info($"tcsim plan: {label} | {PlanRoles[r]} {Who(r)} {c.Info.Ball}볼 {c.Info.Age}세{(c.Info.Foreigner ? " 용병" : string.Empty)} | {parts} | 특강 {coaches}");
				}
				foreach (var alt in alts.Where(a => a.deck == label))
				{
					int r = Array.IndexOf(PlanRoles, alt.slot);
					(string name, byte changed) = StaffEntry(alt.name);
					Cand c = Pitcher(name);
					if (r < 0 || c == null)
					{
						ServerLog.Info($"tcsim plan: {label} 교체안 {alt.slot} {alt.name} 없음");
						continue;
					}
					double g = Gain(x => Swap(x, FirstPitcher + r, c, changed));
					ServerLog.Info($"tcsim plan: {label} 교체안 | {alt.slot} {Who(r)} → {name} {c.Info.Ball}볼 {g:+0.00;-0.00;0.00}%p");
				}
			}
			return 0;
		}
	}
}
