using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace TraitSim
{
	// One trait of the 팀 특성 tree with its effect per point.
	public sealed class Trait
	{
		public int Index, Level, Max, Essential;
		public char Type;
		public string Name, Desc;
		public Effect PerPoint = new Effect();
		public Dictionary<string, double> Raw = new Dictionary<string, double>();

		// Effect of p points: gains times p, percentage multipliers as 1 + p × pct.
		public Effect At(int p)
		{
			Effect e = new Effect();
			foreach ((string k, double v) in Raw)
			{
				double x = v * p;
				switch (k)
				{
					case "컨택": e.Bat[0] += x; break;
					case "파워": e.Bat[1] += x; break;
					case "선구": e.Bat[2] += x; break;
					case "주력": e.Bat[3] += x; break;
					case "H정신": e.Bat[4] += x; break;
					case "수비": e.Bat[5] += x; break;
					case "타자전체": for (int j = 0; j < 6; j++) e.Bat[j] += x; break;
					case "체력": e.Pit[0] += x; break;
					case "구속": e.Pit[1] += x; break;
					case "컨트롤": e.Pit[2] += x; break;
					case "테크닉": e.Pit[3] += x; break;
					case "구위": e.Pit[4] += x; break;
					case "P정신": e.Pit[5] += x; break;
					case "투수전체": for (int j = 0; j < 6; j++) e.Pit[j] += x; break;
					case "필승조전체": for (int j = 0; j < 6; j++) e.Late[j] += x; break;
					case "선발전체": for (int j = 0; j < 6; j++) e.Start[j] += x; break;
					case "홈런%": e.HR *= 1 + x / 100; break;
					case "삼진%": e.K *= 1 + x / 100; break;
					case "볼넷%": e.BB *= 1 + x / 100; break;
					case "장타%": e.XBH *= 1 + x / 100; break;
					case "도루시도%": e.StealTry *= 1 + x / 100; break;
					case "도루자%": e.StealCaught *= 1 + x / 100; break;
					case "탈삼진%": e.PK *= 1 + x / 100; break;
					case "피안타%": e.PBabip *= 1 + x / 100; break;
					case "피홈런%": e.PHR *= 1 + x / 100; break;
					case "피장타%": e.PXBH *= 1 + x / 100; break;
					case "상대도루시도%": e.PStealTry *= 1 + x / 100; break;
					case "도루저지%": e.PCaught *= 1 + x / 100; break;
					case "선발아웃": e.StartOuts += x; break;
					case "선발한계": e.StartLimit += x; break;
					default: throw new InvalidDataException("unknown effect " + k);
				}
			}
			return e;
		}
	}

	public sealed class TraitTable
	{
		public int[] Gates;   // points needed in tier n-1 to open tier n (index = n)
		public Dictionary<string, int> LeaguePoints = new Dictionary<string, int>();
		public List<Trait> Traits = new List<Trait>();
		public List<(string league, string name, Dictionary<int, int> pts)> Builds = new List<(string, string, Dictionary<int, int>)>();
		public Dictionary<int, double> TargetPerPoint = new Dictionary<int, double>();
		public double PitcherFactor = 1.0;

		public Trait this[int index] => Traits.First(t => t.Index == index);

		public static TraitTable Load(string path)
		{
			TraitTable tt = new TraitTable();
			using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
			JsonElement root = doc.RootElement;
			tt.Gates = root.GetProperty("gates").EnumerateArray().Select(x => x.GetInt32()).ToArray();
			foreach (JsonProperty p in root.GetProperty("leaguePoints").EnumerateObject()) tt.LeaguePoints[p.Name] = p.Value.GetInt32();
			if (root.TryGetProperty("targetPerPoint", out JsonElement tp))
			{
				foreach (JsonProperty p in tp.EnumerateObject()) tt.TargetPerPoint[int.Parse(p.Name)] = p.Value.GetDouble();
			}
			if (root.TryGetProperty("pitcherFactor", out JsonElement pf)) tt.PitcherFactor = pf.GetDouble();
			foreach (JsonElement t in root.GetProperty("traits").EnumerateArray())
			{
				Trait tr = new Trait
				{
					Index = t.GetProperty("index").GetInt32(),
					Type = t.GetProperty("type").GetString()[0],
					Level = t.GetProperty("level").GetInt32(),
					Max = t.GetProperty("max").GetInt32(),
					Essential = t.TryGetProperty("essential", out JsonElement es) ? es.GetInt32() : 0,
					Name = t.GetProperty("name").GetString(),
					Desc = t.TryGetProperty("desc", out JsonElement d) ? d.GetString() : ""
				};
				foreach (JsonProperty p in t.GetProperty("perPoint").EnumerateObject()) tr.Raw[p.Name] = p.Value.GetDouble();
				tr.PerPoint = tr.At(1);
				tt.Traits.Add(tr);
			}
			foreach (JsonElement b in root.GetProperty("builds").EnumerateArray())
			{
				Dictionary<int, int> pts = new Dictionary<int, int>();
				foreach (JsonProperty p in b.GetProperty("points").EnumerateObject()) pts[int.Parse(p.Name)] = p.Value.GetInt32();
				tt.Builds.Add((b.GetProperty("league").GetString(), b.GetProperty("name").GetString(), pts));
			}
			return tt;
		}

		// The user's rules: tier n opens with Gates[n] points spent in tier n-1 of the same tree;
		// a trait under an arrow needs the trait above it mastered; the league's point budget.
		public string Check(Dictionary<int, int> pts, int budget)
		{
			int used = pts.Values.Sum();
			if (pts.TryGetValue(155, out int bonus) && bonus > 0) budget += 2;
			if (used > budget) return $"포인트 초과 {used}/{budget}";
			foreach ((int idx, int p) in pts)
			{
				if (p == 0) continue;
				Trait t = this[idx];
				if (p > t.Max) return $"{idx} 최대 {t.Max} 초과";
				if (t.Level > 1)
				{
					int prev = pts.Where(kv => kv.Value > 0 && this[kv.Key].Type == t.Type && this[kv.Key].Level == t.Level - 1).Sum(kv => kv.Value);
					if (prev < Gates[t.Level]) return $"{idx}: {t.Level - 1}단계 {prev}/{Gates[t.Level]}";
				}
				if (t.Essential != 0)
				{
					Trait e = this[t.Essential];
					if (!pts.TryGetValue(t.Essential, out int ep) || ep < e.Max) return $"{idx}: 선행 {t.Essential} 마스터 필요";
				}
			}
			return null;
		}

		public Effect EffectOf(Dictionary<int, int> pts)
		{
			Effect e = new Effect();
			foreach ((int idx, int p) in pts)
			{
				if (p > 0) e = e + this[idx].At(p);
			}
			return e;
		}
	}

	public static class TraitRun
	{
		private static string Pct(double x) => (100 * x).ToString("F1", CultureInfo.InvariantCulture);

		public static void Run(Arena arena, int games, string outDir, string tablePath, string parts)
		{
			TraitTable tt = TraitTable.Load(tablePath);
			bool Part(string k) => parts == "all" || parts.Split(',').Contains(k);
			StringBuilder report = new StringBuilder();
			void Log(string s)
			{
				Console.WriteLine(s);
				report.AppendLine(s);
			}
			ulong seed = 101;

			if (Part("traits"))
			{
			// 1. Every trait alone at full points against the same team without it.
			Log("## 특성별 가치 (최대 포인트, 같은 로스터 미러전, 승률 %p)");
			Log($"{"idx",4} {"단계",3} {"이름",-18} {"pt",2} {"승률",6} {"±",4} {"pt당",5} {"목표",5} {"RS",5} {"RA",5}");
			StringBuilder csv = new StringBuilder("index,type,level,name,max,win,se,perPoint,target,rs,ra,rd\n");
			foreach (Trait t in tt.Traits.OrderBy(t => t.Index))
			{
				MatchResult r = arena.Play(t.At(t.Max), null, games, seed++);
				double per = (r.WinPct - 0.5) * 100 / t.Max;
				double target = tt.TargetPerPoint.TryGetValue(t.Level, out double v) ? v * (t.Type == 'P' ? tt.PitcherFactor : 1) : double.NaN;
				Log($"{t.Index,4} {t.Level,3} {t.Name,-18} {t.Max,2} {100 * r.WinPct,6:F2} {100 * r.WinSE,4:F2} {per,5:F2} {target,5:F2} {r.RsPerG,5:F2} {r.RaPerG,5:F2}");
				csv.AppendLine(string.Join(",", t.Index, t.Type, t.Level, t.Name, t.Max, r.WinPct.ToString("F5"), r.WinSE.ToString("F5"), per.ToString("F4"), target.ToString("F3"), r.RsPerG.ToString("F4"), r.RaPerG.ToString("F4"), r.RunDiff.ToString("F4")));
			}
			File.WriteAllText(Path.Combine(outDir, "trait_values.csv"), csv.ToString(), new UTF8Encoding(true));
			}

			// 2. Builds: legal under the rules, then each against an untraited team and against the
			// other builds of its league; and every team of a league on the same build (run level).
			Log("");
			Log("## 빌드 검사");
			foreach ((string league, string name, Dictionary<int, int> pts) in tt.Builds)
			{
				string err = tt.Check(pts, tt.LeaguePoints[league]);
				Log($"{league} {name}: {pts.Values.Sum()}pt {(err == null ? "OK" : "규칙 위반 — " + err)}");
			}
			if (Part("builds"))
			{
			MatchResult baseEnv = arena.Play(null, null, games, seed++);
			Log("");
			Log($"## 빌드 vs 특성 없음 / 리그 전원이 같은 빌드일 때 (기준 R/G {baseEnv.RPerG:F2}, AVG {baseEnv.HitsA / (double)baseEnv.AbA:F3})");
			Log($"{"리그",-4} {"빌드",-16} {"vs 없음",7} {"±",4} {"RS",5} {"RA",5} {"전원 R/G",8} {"AVG",5} {"HR/G",5}");
			StringBuilder bcsv = new StringBuilder("league,build,points,win_vs_none,se,rs,ra,env_rpg,env_avg,env_hr\n");
			foreach ((string league, string name, Dictionary<int, int> pts) in tt.Builds)
			{
				Effect e = tt.EffectOf(pts);
				MatchResult r = arena.Play(e, null, games, seed++);
				MatchResult env = arena.Play(e, e, games, seed++);
				double avg = (env.HitsA + env.HitsB) / (double)(env.AbA + env.AbB);
				double hr = (env.HrA + env.HrB) / (2.0 * env.Games);
				Log($"{league,-4} {name,-16} {100 * r.WinPct,7:F2} {100 * r.WinSE,4:F2} {r.RsPerG,5:F2} {r.RaPerG,5:F2} {env.RPerG,8:F2} {avg,5:F3} {hr,5:F2}");
				bcsv.AppendLine(string.Join(",", league, name, pts.Values.Sum(), r.WinPct.ToString("F5"), r.WinSE.ToString("F5"), r.RsPerG.ToString("F4"), r.RaPerG.ToString("F4"), env.RPerG.ToString("F4"), avg.ToString("F4"), hr.ToString("F4")));
			}
			File.WriteAllText(Path.Combine(outDir, "builds.csv"), bcsv.ToString(), new UTF8Encoding(true));
			}
			if (Part("matrix"))
			{

			Log("");
			Log("## 같은 리그 빌드끼리 (행 빌드의 승률 %, 미러 로스터)");
			StringBuilder mcsv = new StringBuilder("league,row,col,win,se\n");
			foreach (IGrouping<string, (string league, string name, Dictionary<int, int> pts)> g in tt.Builds.GroupBy(b => b.league))
			{
				var list = g.ToList();
				Log($"[{g.Key}] " + string.Join(" | ", list.Select((b, i) => $"{(char)('A' + i)}={b.name}")));
				Log("     " + string.Join(" ", list.Select((b, i) => $"{(char)('A' + i),6}")));
				for (int i = 0; i < list.Count; i++)
				{
					StringBuilder line = new StringBuilder($"  {(char)('A' + i)}  ");
					for (int j = 0; j < list.Count; j++)
					{
						if (i == j)
						{
							line.Append($"{"-",6} ");
							continue;
						}
						if (j < i)
						{
							line.Append($"{"",6} ");
							continue;
						}
						MatchResult r = arena.Play(tt.EffectOf(list[i].pts), tt.EffectOf(list[j].pts), games, seed++);
						line.Append($"{100 * r.WinPct,6:F1} ");
						mcsv.AppendLine(string.Join(",", g.Key, list[i].name, list[j].name, r.WinPct.ToString("F5"), r.WinSE.ToString("F5")));
					}
					Log(line.ToString());
				}
			}
			File.WriteAllText(Path.Combine(outDir, "build_matrix.csv"), mcsv.ToString(), new UTF8Encoding(true));
			}
			if (Part("env"))
			{
				// Run level of a league whose clubs use a mix of builds: every pairing (mirror rosters).
				Log("");
				Log("## 혼합 리그 득점 환경 (빌드 조합별 경기당 득점, 타율)");
				foreach (IGrouping<string, (string league, string name, Dictionary<int, int> pts)> g in tt.Builds.GroupBy(b => b.league))
				{
					var list = g.ToList();
					double sumR = 0, sumAvg = 0;
					int n = 0;
					for (int i = 0; i < list.Count; i++)
					{
						for (int j = i; j < list.Count; j++)
						{
							MatchResult r = arena.Play(tt.EffectOf(list[i].pts), tt.EffectOf(list[j].pts), games, seed++);
							double avg = (r.HitsA + r.HitsB) / (double)(r.AbA + r.AbB);
							int w = i == j ? 1 : 2;   // a league of one club per build: each pairing twice, mirrors once
							sumR += w * r.RPerG;
							sumAvg += w * avg;
							n += w;
							Log($"[{g.Key}] {list[i].name} vs {list[j].name}: R/G {r.RPerG:F2}, AVG {avg:F3}");
						}
					}
					Log($"[{g.Key}] 빌드가 골고루 섞인 리그 평균: R/G {sumR / n:F2}, AVG {sumAvg / n:F3}");
				}
			}
			File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString(), new UTF8Encoding(true));
		}
	}
}
