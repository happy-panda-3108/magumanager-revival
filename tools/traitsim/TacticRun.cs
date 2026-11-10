using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MSim;

namespace TraitSim
{
	// Fatigue, pitch counts and the 작전실 sliders in the prototype engine (engine/).
	public static class TacticRun
	{
		private static readonly (string name, string left, string right, Action<GameTactics, int> set, Func<Roster, double> metric, string metricName)[] Sliders =
		{
			("타격 성향", "스몰볼", "빅볼", (t, v) => t.Hitting = v, r => r.Order.Average(c => c.R[1] - c.R[0]), "파워−컨택"),
			("공격 성향", "빠른승부", "끈질긴승부", (t, v) => t.Patience = v, r => r.Order.Average(c => c.R[2]), "선구"),
			("도루 성향", "소극적", "적극적", (t, v) => t.Running = v, r => r.Order.Average(c => c.R[3]), "주력"),
			("번트 성향", "번트자제", "번트권장", (t, v) => t.Bunting = v, r => -r.Order.Average(c => c.R[0] + c.R[1] + c.R[2]), "−타격"),
			("수비 성향", "안정적", "적극적", (t, v) => t.Fielding = v, r => Enumerable.Range(0, 9).Where(k => r.Position[k] < 8).Average(k => r.Order[k].Defs[r.Position[k]]), "수비"),
			("투수 성향", "변화구위주", "직구위주", (t, v) => t.Repertoire = v, r => r.Pitchers.Average(FastballEdge), "직구−변화구"),
			("승부 성향", "빠른승부", "유인구승부", (t, v) => t.Approach = v, r => r.Pitchers.Average(c => c.R[2]), "컨트롤"),
			("제구 성향", "몸쪽", "바깥쪽", (t, v) => t.Location = v, r => -r.Pitchers.Average(c => c.R[3]), "−테크닉"),
			("투구 성향", "전력투구", "완급조절", (t, v) => t.Effort = v, r => r.Pitchers.Take(5).Average(c => c.R[0]), "선발 체력"),
			("교체 성향", "교체 자제", "교체 권장", (t, v) => t.Hook = v, r => r.Pitchers.Skip(5).Average(Sum5) - r.Pitchers.Take(5).Average(Sum5), "불펜−선발"),
		};

		private static double Sum5(CardRow c) => c.R[1] + c.R[2] + c.R[3] + c.R[4];

		private static double FastballEdge(CardRow c)
		{
			double fb = c.Pitches.Where(p => p.code == "FAST" || p.code == "TSEAM").Select(p => p.rating).DefaultIfEmpty(65).Max();
			double other = c.Pitches.Where(p => p.code != "FAST" && p.code != "TSEAM").Select(p => p.rating).DefaultIfEmpty(fb).Max();
			return fb - other;
		}

		public static void Run(Arena arena, int games, string outDir, string parts, string only)
		{
			StringBuilder report = new StringBuilder();
			void Log(string s)
			{
				Console.WriteLine(s);
				report.AppendLine(s);
			}
			bool Part(string k) => parts == "all" || parts.Split(',').Contains(k);
			ulong seed = 501;
			TacticRules R = CardGame.Rules;

			if (Part("base"))
			{
				Log("## 체력 → 한계 투구 수 (피로 시작 = 80%)");
				Log(string.Join("  ", new[] { 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100, 110 }.Select(s => $"{s}:{R.Limit(s):F0}({R.Limit(s) * R.FatigueFrom:F0})")));
				Log("");
				Log("## 기존 규칙 vs 피로·투구수 규칙 (작전 전부 50, 같은 로스터끼리)");
				Log($"{"",-14} {"R/G",5} {"AVG",5} {"HR/G",5} {"K%",5} {"BB%",5} {"선발IP",6} {"선발투구",7} {"한계대비",7} {"투구/타석",8} {"투수/경기",8}");
				foreach (bool on in new[] { false, true })
				{
					CardGame.UseTactics = on;
					MatchResult r = arena.Play(null, null, games, seed++);
					PrintEnv(Log, on ? "피로 규칙" : "기존 규칙", r);
				}
				CardGame.UseTactics = true;
			}

			if (Part("stamina"))
			{
				Log("");
				Log("## 체력의 가치 (전 투수 체력 +20 표시 = +6.8 실효, 미러전)");
				foreach (bool on in new[] { false, true })
				{
					CardGame.UseTactics = on;
					Effect e = new Effect();
					e.Pit[0] = 20;
					MatchResult r = arena.Play(e, null, games, seed++);
					Log($"{(on ? "피로 규칙" : "기존 규칙"),-10} 승률 {100 * r.WinPct:F2} (±{100 * r.WinSE:F2})  선발IP {r.OutsSpA / 3.0 / r.StartsA:F2}  선발투구 {r.PitchesSpA / (double)r.StartsA:F1}");
				}
				CardGame.UseTactics = true;
				foreach ((string label, Action<Effect> set) in new (string, Action<Effect>)[] { ("선발만 체력 +20", e => e.Start[0] = 20), ("불펜만 체력 +20", e => { e.Late[0] = 20; }),
					("선발 한계 +10구", e => e.StartLimit = 10), ("선발 한계 +20구", e => e.StartLimit = 20), ("선발 전 능력 +1", e => { for (int j = 1; j < 6; j++) e.Start[j] = 1; }) })
				{
					Effect e = new Effect();
					set(e);
					MatchResult r = arena.Play(e, null, games, seed++);
					Log($"{label,-14} 승률 {100 * r.WinPct:F2} (±{100 * r.WinSE:F2})  선발IP {r.OutsSpA / 3.0 / r.StartsA:F2}");
				}
			}

			if (Part("sliders"))
			{
				Log("");
				Log("## 작전 슬라이더 (A팀만 설정, 상대는 50; 같은 로스터끼리)");
				Log($"{"슬라이더",-16} {"값",3} {"승률",6} {"RS",5} {"RA",5} {"AVG",5} {"HR/G",5} {"K%",5} {"BB%",5} {"SB-CS",9} {"투구/타석(상대)",9} {"선발IP",6}");
				StringBuilder csv = new StringBuilder("slider,value,win,se,rs,ra\n");
				foreach (var sl in Sliders.Where(x => only == "" || only.Split('/').Contains(x.name.Split(' ')[0])))
				{
					foreach (int v in new[] { 0, 25, 75, 100 })
					{
						GameTactics ta = new GameTactics();
						sl.set(ta, v);
						MatchResult r = arena.Play(null, null, games, seed++, tacA: ta);
						double g = r.Games;
						string label = v < 50 ? sl.left : sl.right;
						Log($"{sl.name + " " + label,-16} {v,3} {100 * r.WinPct,6:F2} {r.RsPerG,5:F2} {r.RaPerG,5:F2} {r.HitsA / (double)r.AbA,5:F3} {r.HrA / g,5:F2} "
							+ $"{100.0 * r.SoA / Math.Max(1, r.AbA + r.BbA),5:F1} {100.0 * r.BbA / Math.Max(1, r.AbA + r.BbA),5:F1} {r.SbA / g,4:F2}-{r.CsA / g,4:F2} {r.PitchesA / (double)r.PaB,9:F2} {r.OutsSpA / 3.0 / r.StartsA,6:F2}");
						csv.AppendLine(string.Join(",", sl.name, v, r.WinPct.ToString("F5"), r.WinSE.ToString("F5"), r.RsPerG.ToString("F4"), r.RaPerG.ToString("F4")));
					}
				}
				File.WriteAllText(Path.Combine(outDir, "sliders.csv"), csv.ToString(), new UTF8Encoding(true));
			}

			if (Part("fit"))
			{
				// Who should use which end: rosters split into thirds by the slider's metric.
				Log("");
				Log("## 로스터 성향별 효과 (하위 1/3 vs 상위 1/3 로스터, 극단값 0/100의 승률)");
				foreach (var sl in Sliders.Where(x => only == "" || only.Split('/').Contains(x.name.Split(' ')[0])))
				{
					double[] vals = arena.Rosters.Select(sl.metric).OrderBy(x => x).ToArray();
					double lo = vals[vals.Length / 3], hi = vals[2 * vals.Length / 3];
					StringBuilder line = new StringBuilder($"{sl.name,-8} ({sl.metricName}) ");
					foreach ((string tag, Func<Roster, bool> f) in new (string, Func<Roster, bool>)[] { ("하위", r => sl.metric(r) <= lo), ("상위", r => sl.metric(r) >= hi) })
					{
						foreach (int v in new[] { 0, 100 })
						{
							GameTactics ta = new GameTactics();
							sl.set(ta, v);
							MatchResult r = arena.Play(null, null, games, seed++, tacA: ta, filter: f);
							line.Append($"{tag} {(v == 0 ? sl.left : sl.right)} {100 * r.WinPct:F1}  ");
						}
					}
					Log(line.ToString());
				}
			}
			if (Part("seasondebug"))
			{
				Arena.Debug = true;
				arena.PlaySeason(null, null, null, null, 12, true, 1, rosters: 1);
				Arena.Debug = false;
			}

			if (Part("season"))
			{
				// Fatigue carried between games: can the bullpen throw 전력투구 every day?
				Log("");
				Log($"## 시즌 모드 (같은 로스터 A·B가 144일 연속 경기, 회복 {100 * R.RecoverShare:F0}%/일, 휴식 기준 {100 * R.AvailShare:F0}%)");
				Log($"{"A팀 작전",-28} {"피로",4} {"승률",6} {"RS",5} {"RA",5} {"선발IP",6} {"불펜/경기",8} {"지친 등판",8} {"마무리/시즌",10}");
				GameTactics Eff(int v) { GameTactics g = new GameTactics(); g.Effort = v; return g; }
				GameTactics[] Pen(int v) => Enumerable.Range(0, 11).Select(s => s >= 5 ? Eff(v) : null).ToArray();
				GameTactics[] Split(int sp, int rp) => Enumerable.Range(0, 11).Select(s => Eff(s < 5 ? sp : rp)).ToArray();
				foreach ((string label, GameTactics tac, GameTactics[] own, Effect e) in new (string, GameTactics, GameTactics[], Effect)[]
				{
					("기본 (전원 50)", null, null, null),
				}.Concat(only == "base" ? Array.Empty<(string, GameTactics, GameTactics[], Effect)>() : new (string, GameTactics, GameTactics[], Effect)[]
				{
					("불펜만 전력투구 (개인작전)", null, Pen(0), null),
					("팀 전원 전력투구", Eff(0), null, null),
					("팀 전원 완급조절", Eff(100), null, null),
					("선발 완급 + 불펜 전력", null, Split(100, 0), null),
					("체력 +20 (전 투수)", null, null, new Effect { Pit = new double[] { 20, 0, 0, 0, 0, 0 } }),
				}))
				{
					foreach (bool carry in new[] { false, true })
					{
						MatchResult r = arena.PlaySeason(e, tac, own, null, 144, carry, seed++, rosters: Math.Min(arena.Rosters.Count, games));
						Log($"{label,-28} {(carry ? "누적" : "없음"),4} {100 * r.WinPct,6:F2} {r.RsPerG,5:F2} {r.RaPerG,5:F2} {r.OutsSpA / 3.0 / r.StartsA,6:F2} {r.ReliefAppsA / (double)r.Games,8:F2} "
							+ $"{100.0 * r.TiredAppsA / Math.Max(1, r.ReliefAppsA),7:F1}% {144.0 * r.CloserAppsA / r.Games,10:F1}");
					}
				}
			}

			if (Part("league"))
			{
				Log("");
				Log($"## 리그 평균 보정 (기준: 타자 {string.Join(" ", Arena.RefBat.Select(x => x.ToString("F1")))} / 투수 {string.Join(" ", Arena.RefPit.Select(x => x.ToString("F1")))})");
				Log($"{"",-26} {"보정",4} {"승률A",6} {"R/G",5} {"AVG",5} {"HR/G",5} {"RS",5} {"RA",5}");
				Effect Dev(double d) { Effect e = new Effect(); for (int j = 0; j < 6; j++) { e.Bat[j] = d; e.Pit[j] = j == 0 ? 0 : d; } return e; }
				foreach ((string label, Effect a, Effect b) in new (string, Effect, Effect)[]
				{
					("주전급 카드끼리 (육성 없음)", null, null),
					("양 팀 모두 육성 +40", Dev(40), Dev(40)),
					("A팀만 육성 +10", Dev(10), null),
					("A팀만 육성 +30", Dev(30), null),
					("A +40 vs B +30 (육성 차이 10)", Dev(40), Dev(30)),
				})
				{
					foreach (bool on in new[] { false, true })
					{
						Arena.LeagueReference = on;
						MatchResult r = arena.Play(a, b, games, seed++);
						double avg = (r.HitsA + r.HitsB) / (double)(r.AbA + r.AbB);
						Log($"{label,-26} {(on ? "있음" : "없음"),4} {100 * r.WinPct,6:F1} {r.RPerG,5:F2} {avg,5:F3} {(r.HrA + r.HrB) / (2.0 * r.Games),5:F2} {r.RsPerG,5:F2} {r.RaPerG,5:F2}");
					}
				}
				Arena.LeagueReference = false;
			}
			File.WriteAllText(Path.Combine(outDir, "tactics_report.txt"), report.ToString(), new UTF8Encoding(true));
		}

		private static void PrintEnv(Action<string> log, string label, MatchResult r)
		{
			double avg = (r.HitsA + r.HitsB) / (double)(r.AbA + r.AbB);
			double pa = r.AbA + r.AbB + r.BbA + r.BbB;
			log($"{label,-14} {r.RPerG,5:F2} {avg,5:F3} {(r.HrA + r.HrB) / (2.0 * r.Games),5:F2} {100.0 * (r.SoA + r.SoB) / pa,5:F1} {100.0 * (r.BbA + r.BbB) / pa,5:F1} "
				+ $"{r.OutsSpA / 3.0 / r.StartsA,6:F2} {r.PitchesSpA / (double)r.StartsA,7:F1} {(r.SpLimitShareA > 0 ? 100 * r.SpLimitShareA / r.StartsA : double.NaN),6:F0}% {(r.PaB > 0 ? r.PitchesA / (double)r.PaB : double.NaN),8:F2} {r.PitchersUsedA / (double)r.Games,8:F2}");
		}
	}
}
