using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Ma9MServer
{
	public static partial class TeamColorSim
	{
		// "--tcsim [cap] [games] entry" (user, 2026-11-03: "97쌍방울에서 승률이 가장 높게 나오도록 포지션변경을 최대한 활용해서
		// 엔트리를 한 번 짜볼래??"): single team-year decks with chosen staffs, each against the deck league's field, all
		// developed as the league plays them (Develop: 팀컬러 S, 재계약, 트레이닝, 특강).
		// - TCSIM_FIELD="<label>:<build>;…" is the field: the league table of a dev run, build 0 타격형 / 1 투수형 / 2 혼합.
		//   Its decks are built as RunDev builds them (Build, RoleDeck, CapDeck, LineupDeck).
		// - TCSIM_DECKS and TCSIM_STAFF as plan mode: "<deck>#<tag>" is the deck with that staff (SP1-5, RP1-4, SU, CP;
		//   "@SP|RP|SU|CP" = 포지션 변경 성공, "!" 대성공). A pitcher put in another role's spot without "@" changes as
		//   the league builder's do (ChangeFor, 성공) when his card may.
		// - Each deck plays every field deck but its own team-year `games` games (the same seeds for every deck), with the
		//   build TCSIM_ENTRY_BUILD (0/1/2, "all" = each in turn; default: its team-year's build in the field, else 혼합).
		// - TCSIM_ENTRY_BONUS="0,1,2,3" (user, 2026-11-03: 코칭스태프 "스페셜 이상의 단일팀 코칭스태프 사용 시 해당 팀의 육성
		//   능력치를 +1 … 레어 이상은 +2, 엘리트 이상은 +3"): each deck again with that many more development points on
		//   every stat of every card (counted as the other bonuses, TeamStrength.AddBonuses).
		private static int RunEntry(List<(string name, List<(string label, Func<CardInfo, bool> ok)> instances)> families, List<Cand> all, Deck free, int cap, int games)
		{
			string[] builds = LeagueRules.AiTraitBuilds[LeagueRules.MajorTier];
			TraitEffects.Effect[] effects = builds.Select(b => TraitEffects.FromPoints(b.Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1])))).ToArray();
			string[] buildNames = { "타격형", "투수형", "혼합" };
			var single = families.First(f => f.name == "단일 년도 팀").instances;
			List<Cand> PoolOf(string label)
			{
				var inst = single.FirstOrDefault(i => i.label == label);
				return inst.ok == null ? null : WithDevCatcher(all.Where(x => inst.ok(x.Info)).ToList());
			}
			// As RunDev builds a team-year's deck.
			Deck LeagueDeck(string label)
			{
				List<Cand> pool = PoolOf(label);
				if (pool == null) return null;
				Deck d = Build(label, pool, cap);
				if (d == null && pool.Count(c => c.Info.IsBatter) >= 14 && pool.Count(c => !c.Info.IsBatter) >= 11)
				{
					Deck staffed = RoleDeck(new Deck { Label = label }, pool);
					Deck full = LineupDeck(staffed, pool, cap, out _);
					if (full != staffed && full.At.All(c => c != null)) d = full;
				}
				if (d == null) return null;
				d = CapDeck(RoleDeck(d, pool), pool, cap);
				return LineupDeck(d, pool, cap, out _);
			}
			List<(string label, int build)> fieldList = (Environment.GetEnvironmentVariable("TCSIM_FIELD") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split(':')).Where(x => x.Length == 2).Select(x => (x[0].Trim(), int.Parse(x[1]))).ToList();
			DevDeck[] field = new DevDeck[fieldList.Count];
			Parallel.For(0, fieldList.Count, i =>
			{
				Deck d = LeagueDeck(fieldList[i].label);
				if (d == null) return;
				DevDeck x = Develop(d);
				x.Traits = effects[Math.Min(fieldList[i].build, effects.Length - 1)];
				x.Build = fieldList[i].build;
				field[i] = x;
			});
			foreach (int i in Enumerable.Range(0, field.Length).Where(i => field[i] == null)) ServerLog.Info($"tcsim entry: 상대 {fieldList[i].label} 못 만듦");
			List<(DevDeck deck, int salt)> opponents = Enumerable.Range(0, field.Length).Where(i => field[i] != null).Select(i => (field[i], 7 + 131 * i)).ToList();
			ServerLog.Info($"tcsim entry: 상대 {opponents.Count}덱 (덱 경쟁 리그), 한 상대에 {games}경기; 팀컬러 +{DevTeamColour}, 재계약 {DevRenewals}x{DevRenewal}, 트레이닝 {DevSlots}x{DevSlotGain}, 특강 {(DevLecture ? $"주 +{DevLectureMain}·보조 +{DevLectureSub}" : "없음")}");
			Dictionary<string, string> staffOf = (Environment.GetEnvironmentVariable("TCSIM_STAFF") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries)
				.Select(s => s.Split('=', 2)).Where(kv => kv.Length == 2).ToDictionary(kv => kv[0].Trim(), kv => kv[1]);
			Dictionary<string, string> batsOf = BatsSpecs();
			Dictionary<string, string> opsOf = OpsSpecs();
			Dictionary<string, string> devOf = DevSpecs();
			string buildPick = Environment.GetEnvironmentVariable("TCSIM_ENTRY_BUILD");
			Dictionary<string, Deck> baseOf = new Dictionary<string, Deck>();
			foreach (string label in (Environment.GetEnvironmentVariable("TCSIM_DECKS") ?? string.Empty).Split(';').Select(x => x.Trim()).Where(x => x.Length > 0))
			{
				string baseLabel = label.Split('#')[0].Trim();
				if (!baseOf.TryGetValue(baseLabel, out Deck d))
				{
					d = LeagueDeck(baseLabel);
					baseOf[baseLabel] = d;
					if (d != null)
					{
						int[] o = BestOrder(d);
						ServerLog.Info($"tcsim entry: {baseLabel} 타선 " + string.Join(", ", Enumerable.Range(0, 9).Select(i => $"{i + 1}.{d.At[o[i]].Info.Name}{d.At[o[i]].Info.Ball} {SpotNames[o[i]]}")) + " | 벤치 " + string.Join(" ", Enumerable.Range(Bench, 5).Select(s => $"{d.At[s].Info.Name}{d.At[s].Info.Ball}")));
					}
				}
				if (d == null)
				{
					ServerLog.Info($"tcsim entry: {label} 덱을 못 만듦");
					continue;
				}
				List<Cand> pool = PoolOf(baseLabel);
				Deck own = new Deck { Label = label, At = (Cand[])d.At.Clone(), Value = d.Value };
				byte[] pti = new byte[11];
				// TCSIM_BATS (TeamColorSim.Legends.cs): chosen nine, bench and batting order, 레전드 by card idx.
				int[] fixedOrder = null;
				if (batsOf.TryGetValue(label, out string bats))
				{
					string problem = ApplyBats(own, bats, pool, out fixedOrder);
					if (problem != null)
					{
						ServerLog.Info($"tcsim entry: {label} 타선 {problem}");
						continue;
					}
				}
				if (staffOf.TryGetValue(label, out string staff))
				{
					string[] names = staff.Split(',');
					string missing = names.Length != 11 ? $"{names.Length}명(11명이어야 함)" : null;
					for (int r = 0; r < 11 && missing == null; r++)
					{
						(string name, byte changed) = StaffEntry(names[r]);
						Cand c = FindCard(name, false, pool);
						if (c == null) missing = name + " 없음";
						else
						{
							own.At[FirstPitcher + r] = c;
							pti[r] = changed;
						}
					}
					if (missing != null)
					{
						ServerLog.Info($"tcsim entry: {label} 투수진 {missing}");
						continue;
					}
				}
				if (batsOf.ContainsKey(label) || staffOf.ContainsKey(label))
				{
					List<Cand> used = own.At.Where(c => c != null).ToList();
					var twice = used.GroupBy(c => (c.Info.IsBatter, c.Info.Sidx)).Where(g => g.Count() > 1).Select(g => string.Join("·", g.Select(c => $"{c.Info.Name}({c.Info.Idx})"))).ToList();
					string problem = twice.Count > 0 ? "같은 선수 두 장: " + string.Join(", ", twice) : LegendLimit(used);
					if (problem != null)
					{
						ServerLog.Info($"tcsim entry: {label} {problem}");
						continue;
					}
				}
				own.Balls = own.At.Sum(c => c.Info.Ball);
				DevDeck y = Develop(own);
				if (fixedOrder != null)
				{
					y.Order = fixedOrder;
					y.Lineup = LineupOf(own, fixedOrder);
				}
				if (batsOf.ContainsKey(label)) ServerLog.Info($"tcsim entry: {label} 타선 {BatsText(own, y.Order)}");
				// TCSIM_OPS (TeamColorSim.Legends.cs): the deck's 작전실 and 개인작전.
				if (opsOf.TryGetValue(label, out string ops))
				{
					string problem = ApplyOps(y, ops);
					if (problem != null)
					{
						ServerLog.Info($"tcsim entry: {label} {problem}");
						continue;
					}
				}
				for (int r = 0; r < 11; r++)
				{
					if (pti[r] != 0) y.Dev[FirstPitcher + r].PositionTrainingInfo = pti[r];
				}
				// TCSIM_DEV (TeamColorSim.Legends.cs): the book's 특강·트레이닝·재계약 numbers per card.
				if (devOf.TryGetValue(label, out string devSpec))
				{
					string problem = ApplyDev(y, devSpec);
					if (problem != null)
					{
						ServerLog.Info($"tcsim entry: {label} {problem}");
						continue;
					}
				}
				string Who(int r)
				{
					Cand c = own.At[FirstPitcher + r];
					byte p = y.Dev[FirstPitcher + r].PositionTrainingInfo;
					string name = $"{c.Info.Name} {c.Info.Ball}";
					if (p % 10 == 0) return LineupEffects.InRole(r < 5 ? 0 : r - 4, c.Info.PreferredOrder) ? name : name + "(역할 밖)";
					return $"{name}({PlanRoleNames[(int)c.Info.MainPosition]}→{PlanRoleNames[p / 10 + 11]}{(p % 10 == 1 ? "·대성공" : string.Empty)})";
				}
				string staffLine = string.Join(", ", Enumerable.Range(0, 11).Select(r => $"{PlanRoles[r]} {Who(r)}"));
				int fieldBuild = fieldList.FindIndex(f => f.label == baseLabel);
				int[] tries = buildPick == "all" ? new[] { 0, 1, 2 } : int.TryParse(buildPick, out int bp) ? new[] { bp } : new[] { fieldBuild >= 0 ? fieldList[fieldBuild].build : 2 };
				int[] bonuses = (Environment.GetEnvironmentVariable("TCSIM_ENTRY_BONUS") ?? "0").Split(',').Select(int.Parse).ToArray();
				foreach (int b in tries)
				{
					y.Traits = effects[Math.Min(b, effects.Length - 1)];
					y.Build = b;
					foreach (int extra in bonuses)
					{
						DevDeck z = y;
						if (extra != 0)
						{
							z = new DevDeck { D = y.D, Traits = y.Traits, Build = y.Build, Order = y.Order, Lineup = y.Lineup, Tactics = y.Tactics, Own = y.Own };
							for (int s = 0; s < Slots; s++)
							{
								TeamCard t = y.Dev[s];
								z.Dev[s] = new TeamCard { Idx = t.Idx, PositionTrainingInfo = t.PositionTrainingInfo };
								for (int k = 0; k < 6; k++)
								{
									z.Dev[s].TeamColor[k] = (sbyte)(t.TeamColor[k] + extra);
									z.Dev[s].Contract[k] = t.Contract[k];
									z.Dev[s].Training[k] = t.Training[k];
									z.Dev[s].Educated[k] = t.Educated[k];
								}
							}
						}
						double win = 0, rf = 0, ra = 0;
						int n = 0;
						foreach (var (opp, salt) in opponents)
						{
							if (opp.D.Label == baseLabel) continue;
							var res = PlayDev(z, opp, games, salt);
							win += res.win;
							rf += res.rf;
							ra += res.ra;
							n++;
						}
						ServerLog.Info($"tcsim entry: {label}{(extra != 0 ? $" (모든 카드 육성 +{extra})" : string.Empty)} {own.Balls}볼{(own.Balls > cap ? $" (상한 {cap} 넘음)" : string.Empty)} 특성 {buildNames[Math.Min(b, 2)]}: 리그 평균 {100 * win / n:F2}% (득 {rf / n:F2} 실 {ra / n:F2}, {n}덱) | {staffLine}");
					}
				}
			}
			return 0;
		}
	}
}
