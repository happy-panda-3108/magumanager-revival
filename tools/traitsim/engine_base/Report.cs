using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace Ma9MSim
{
	// CSV dumps plus a one-page HTML summary of a fitting run.
	public static class Report
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		// Seasons people tend to remember; shown side by side in the report.
		private static readonly (int Year, string Name)[] FamousBatters =
		{
			(1982, "백인천"), (1994, "이종범"), (1999, "이승엽"), (2003, "이승엽"), (2010, "이대호"), (2014, "서건창"), (2014, "박병호"),
			(2015, "테임즈"), (2018, "김재환"), (2020, "로하스"), (2024, "김도영"), (1987, "장효조"), (2016, "최형우"), (2012, "박병호"), (2022, "이정후")
		};

		private static readonly (int Year, string Name)[] FamousPitchers =
		{
			(1983, "장명부"), (1984, "최동원"), (1986, "선동열"), (1993, "선동열"), (1996, "구대성"), (2006, "류현진"), (2008, "김광현"),
			(2011, "오승환"), (2011, "윤석민"), (2019, "린드블럼"), (2023, "페디"), (2017, "양현종")
		};

		public static void Write(string dir, IReadOnlyList<SeasonFit> fits, string config, TimeSpan elapsed, ParkModel parks,
			List<(int year, int team, double recordPf, double modelPf)> check, CardData cards)
		{
			Directory.CreateDirectory(dir);
			EraTable era = EraTable.Build(fits);
			List<DefenceScore> defence = DefenceScore.Build(fits, cards);
			ReportExtra.WriteCsv(dir, fits, era, defence);
			ReportExtra.WriteCardInput(dir, fits, parks);
			WriteBatters(Path.Combine(dir, "batters.csv"), fits);
			WritePitchers(Path.Combine(dir, "pitchers.csv"), fits);
			WriteTalents(Path.Combine(dir, "talents.csv"), fits);
			File.WriteAllLines(Path.Combine(dir, "progress.log"), fits.SelectMany(f => f.Progress), Encoding.UTF8);
			File.WriteAllText(Path.Combine(dir, "report.html"), Html(fits, config, elapsed, parks, check, era, defence), Encoding.UTF8);
		}

		public static void WriteParks(string dir, ParkModel parks, List<(int year, int team, double recordPf, double modelPf)> check)
		{
			StringBuilder sb = new StringBuilder("park,name,first,last,teams,run_index,raw_run_index,calibrated," + string.Join(",", PC.Names.Select(n => "f_" + n)) + "," + string.Join(",", PC.Names.Select(n => "se_" + n)) + "\n");
			foreach (ParkInfo p in parks.Parks.Values.OrderByDescending(p => p.RunIndex))
			{
				sb.Append(string.Join(",", p.Id, Csv(p.Name), p.FirstYear, p.LastYear, Csv(string.Join("/", p.Teams)), F(p.RunIndex, 3), F(p.RawRunIndex, 3), p.Calibrated ? 1 : 0,
					string.Join(",", p.Factor.Select(v => F(v, 3))), string.Join(",", p.Se.Select(v => F(v, 3))))).Append('\n');
			}
			File.WriteAllText(Path.Combine(dir, "parks.csv"), sb.ToString(), new UTF8Encoding(true));
			sb = new StringBuilder("year,team,record_pf,model_pf\n");
			foreach (var x in check)
			{
				sb.Append(string.Join(",", x.year, Team(x.team), F(x.recordPf, 3), F(x.modelPf, 3))).Append('\n');
			}
			File.WriteAllText(Path.Combine(dir, "parks_crosscheck.csv"), sb.ToString(), new UTF8Encoding(true));
		}

		private static void WriteBatters(string path, IReadOnlyList<SeasonFit> fits)
		{
			StringBuilder sb = new StringBuilder("year,team,name,real_pa,sim_pa,real_avg,sim_avg,real_obp,sim_obp,real_slg,sim_slg,real_hr,sim_hr,real_rbi,sim_rbi,real_r,sim_r,real_sb,sim_sb,real_so,sim_so,real_bb,sim_bb\n");
			foreach (SeasonFit f in fits)
			{
				for (int i = 0; i < f.Model.Batters.Length; i++)
				{
					BatterLine b = f.Model.Batters[i];
					Bat s = Bat.From(f, i);
					sb.Append(string.Join(",", b.Year, Team(b.Team), Csv(b.Name), b.PA, F(s.PA, 1), F(Avg(b), 3), F(s.Avg, 3), F(Obp(b), 3), F(s.Obp, 3),
						F(Slg(b), 3), F(s.Slg, 3), b.HR, F(s.HR, 1), b.RBI, F(s.RBI, 1), b.R, F(s.R, 1), b.SB, F(s.SB, 1), b.SO, F(s.SO, 1), b.BB, F(s.BB, 1))).Append('\n');
				}
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WritePitchers(string path, IReadOnlyList<SeasonFit> fits)
		{
			StringBuilder sb = new StringBuilder("year,team,name,starter,real_g,sim_g,real_gs,sim_gs,real_ip,sim_ip,real_era,sim_era,sim_r,real_w,sim_w,real_l,sim_l,real_sv,sim_sv,real_hld,sim_hld,real_so,sim_so,real_bb,sim_bb,real_hr,sim_hr\n");
			foreach (SeasonFit f in fits)
			{
				for (int i = 0; i < f.Model.Pitchers.Length; i++)
				{
					PitcherLine p = f.Model.Pitchers[i];
					Pit s = Pit.From(f, i);
					sb.Append(string.Join(",", p.Year, Team(p.Team), Csv(p.Name), p.Starter ? 1 : 0, p.G, F(s.G, 1), p.GS, F(s.GS, 1), F(p.Outs / 3.0, 1), F(s.Outs / 3, 1),
						F(Era(p), 2), F(s.Era, 2), F(s.R, 1), p.W, F(s.W, 1), p.L, F(s.L, 1), p.SV, F(s.SV, 1), p.HLD, F(s.HLD, 1), p.SO, F(s.SO, 1), p.BB, F(s.BB, 1), p.HR, F(s.HR, 1))).Append('\n');
				}
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static void WriteTalents(string path, IReadOnlyList<SeasonFit> fits)
		{
			StringBuilder sb = new StringBuilder("kind,year,team,sidx,name,k,bb,hbp,hr,babip,double,triple,steal,speed,lg_k,lg_bb,lg_hr,lg_babip\n");
			foreach (SeasonFit f in fits)
			{
				League lg = f.Model.League;
				for (int i = 0; i < f.Model.Batters.Length; i++)
				{
					BatterLine b = f.Model.Batters[i];
					BatterTalent t = f.Model.BT[i];
					sb.Append(string.Join(",", "B", b.Year, Team(b.Team), b.Sidx, Csv(b.Name), F(t.K, 4), F(t.BB, 4), F(t.HBP, 4), F(t.HR, 4), F(t.Babip, 4), F(t.Double, 4), F(t.Triple, 4), F(t.Steal, 4), F(t.Speed, 3),
						F(lg.K, 4), F(lg.BB, 4), F(lg.HR, 4), F(lg.Babip, 4))).Append('\n');
				}
				for (int i = 0; i < f.Model.Pitchers.Length; i++)
				{
					PitcherLine p = f.Model.Pitchers[i];
					PitcherTalent t = f.Model.PT[i];
					sb.Append(string.Join(",", "P", p.Year, Team(p.Team), p.Sidx, Csv(p.Name), F(t.K, 4), F(t.BB, 4), F(t.HBP, 4), F(t.HR, 4), F(t.Babip, 4), "", "", "", "",
						F(lg.K, 4), F(lg.BB, 4), F(lg.HR, 4), F(lg.Babip, 4))).Append('\n');
				}
			}
			File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
		}

		private static string Html(IReadOnlyList<SeasonFit> fits, string config, TimeSpan elapsed, ParkModel parks,
			List<(int year, int team, double recordPf, double modelPf)> check, EraTable era, List<DefenceScore> defence)
		{
			StringBuilder h = new StringBuilder();
			h.Append("<!doctype html><html lang=\"ko\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>Ma9M 시뮬레이션 검증</title><style>");
			h.Append(":root{--bg:#fbfaf7;--fg:#1d1d1b;--muted:#6b6a65;--line:#e4e1d8;--good:#2f7d4f;--bad:#b3412e;--head:#f1eee6}");
			h.Append("@media (prefers-color-scheme:dark){:root{--bg:#18181a;--fg:#ecebe6;--muted:#a19f98;--line:#33322f;--good:#6cc08f;--bad:#e8826f;--head:#232325}}");
			h.Append("body{background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,'Malgun Gothic',sans-serif;margin:0;padding:24px 16px;max-width:1200px;margin-inline:auto}");
			h.Append("h1{font-size:22px;margin:0 0 4px}h2{font-size:17px;margin:28px 0 8px}h3{font-size:15px;margin:18px 0 6px}p.note{color:var(--muted);margin:4px 0 12px}");
			h.Append(".wrap{overflow-x:auto}table{border-collapse:collapse;font-variant-numeric:tabular-nums;white-space:nowrap}th,td{border-bottom:1px solid var(--line);padding:4px 8px;text-align:right}");
			h.Append("th{background:var(--head);position:sticky;top:0}td:first-child,th:first-child,td.l{text-align:left}.good{color:var(--good)}.bad{color:var(--bad)}");
			h.Append(".kpi{display:flex;gap:16px;flex-wrap:wrap}.kpi div{border:1px solid var(--line);border-radius:8px;padding:10px 14px;min-width:150px}.kpi b{display:block;font-size:20px}");
			h.Append("</style></head><body>");
			h.Append("<h1>마구매니저 시뮬레이션 엔진 검증</h1>");
			h.Append($"<p class=\"note\">{WebUtility.HtmlEncode(config)} · {fits.Count}시즌 · 소요 {elapsed.TotalMinutes:F1}분 · {DateTime.Now:yyyy-MM-dd HH:mm}</p>");

			List<(SeasonFit f, int i, Bat s)> regulars = fits.SelectMany(f => Enumerable.Range(0, f.Model.Batters.Length).Select(i => (f, i, Bat.From(f, i))))
				.Where(x => !x.f.Model.Batters[x.i].Replacement && x.f.Model.Batters[x.i].PA >= 300).ToList();
			List<(SeasonFit f, int i, Pit s)> mainPitchers = fits.SelectMany(f => Enumerable.Range(0, f.Model.Pitchers.Length).Select(i => (f, i, Pit.From(f, i))))
				.Where(x => !x.f.Model.Pitchers[x.i].Replacement && x.f.Model.Pitchers[x.i].Outs >= 240).ToList();
			h.Append("<h2>한눈에 보기</h2><div class=\"kpi\">");
			Kpi(h, "규정급 타자 타율 오차", regulars.Average(x => Math.Abs(x.s.Avg - Avg(x.f.Model.Batters[x.i]))).ToString(".000", Inv), $"{regulars.Count}명 평균 |실제−시뮬|");
			Kpi(h, "홈런 오차", regulars.Average(x => Math.Abs(x.s.HR - x.f.Model.Batters[x.i].HR)).ToString("F1", Inv) + "개", "시즌당 평균");
			Kpi(h, "타점 상관계수", Corr(regulars.Select(x => (double)x.f.Model.Batters[x.i].RBI), regulars.Select(x => x.s.RBI)).ToString("F3", Inv), "실제 타점 vs 시뮬 타점");
			Kpi(h, "득점 상관계수", Corr(regulars.Select(x => (double)x.f.Model.Batters[x.i].R), regulars.Select(x => x.s.R)).ToString("F3", Inv), "실제 득점 vs 시뮬 득점");
			Kpi(h, "주력 투수 ERA 오차", mainPitchers.Average(x => Math.Abs(x.s.Era - Era(x.f.Model.Pitchers[x.i]))).ToString("F2", Inv), $"{mainPitchers.Count}명 (80이닝+)");
			Kpi(h, "승수 상관계수", Corr(mainPitchers.Select(x => (double)x.f.Model.Pitchers[x.i].W), mainPitchers.Select(x => x.s.W)).ToString("F3", Inv), "실제 승 vs 시뮬 승");
			h.Append("</div>");

			h.Append("<h2>시즌별 리그 비교 (실제 / 시뮬)</h2><p class=\"note\">득점 환경 계수는 주루 적극성 보정값입니다. 1.0에서 멀수록 엔진의 기본 주루 규칙이 그 시즌과 안 맞는다는 뜻입니다.</p><div class=\"wrap\"><table><tr><th>시즌</th><th>팀</th><th>경기</th><th>타율</th><th>출루율</th><th>장타율</th><th>홈런/타석</th><th>삼진%</th><th>볼넷%</th><th>경기당 득점</th><th>평균자책점</th><th>득점환경</th></tr>");
			foreach (SeasonFit f in fits)
			{
				LeagueLine real = LeagueLine.Real(f), sim = LeagueLine.Sim(f);
				h.Append($"<tr><td>{f.Model.Season.Year}</td><td>{f.Model.Season.Teams.Count}</td><td>{f.Model.Season.Games}</td>");
				Pair(h, real.Avg, sim.Avg, ".000", 0.005);
				Pair(h, real.Obp, sim.Obp, ".000", 0.005);
				Pair(h, real.Slg, sim.Slg, ".000", 0.008);
				Pair(h, real.HrPct * 100, sim.HrPct * 100, "F2", 0.15);
				Pair(h, real.KPct * 100, sim.KPct * 100, "F1", 0.6);
				Pair(h, real.BbPct * 100, sim.BbPct * 100, "F1", 0.5);
				Pair(h, real.Rpg, sim.Rpg, "F2", 0.15);
				Pair(h, real.Era, sim.Era, "F2", 0.15);
				h.Append($"<td>{f.Params.RunEnvironment.ToString("F3", Inv)}</td></tr>");
			}
			h.Append("</table></div>");

			h.Append("<h2>기억에 남는 타자 시즌</h2><div class=\"wrap\"><table><tr><th>선수</th><th>타석</th><th>타율</th><th>출루율</th><th>장타율</th><th>홈런</th><th>타점</th><th>득점</th><th>도루</th></tr>");
			foreach ((int year, string name) in FamousBatters)
			{
				foreach (var x in Find(fits, year, name, true))
				{
					BatterLine b = x.f.Model.Batters[x.i];
					Bat s = Bat.From(x.f, x.i);
					h.Append($"<tr><td>{year} {WebUtility.HtmlEncode(b.Name)} ({Team(b.Team)})</td>");
					Pair(h, b.PA, s.PA, "F0", 15);
					Pair(h, Avg(b), s.Avg, ".000", 0.010);
					Pair(h, Obp(b), s.Obp, ".000", 0.010);
					Pair(h, Slg(b), s.Slg, ".000", 0.015);
					Pair(h, b.HR, s.HR, "F1", 3);
					Pair(h, b.RBI, s.RBI, "F1", 8);
					Pair(h, b.R, s.R, "F1", 8);
					Pair(h, b.SB, s.SB, "F1", 4);
					h.Append("</tr>");
				}
			}
			h.Append("</table></div>");

			h.Append("<h2>기억에 남는 투수 시즌</h2><div class=\"wrap\"><table><tr><th>선수</th><th>경기</th><th>이닝</th><th>평균자책점</th><th>승</th><th>패</th><th>세이브</th><th>탈삼진</th><th>볼넷</th></tr>");
			foreach ((int year, string name) in FamousPitchers)
			{
				foreach (var x in Find(fits, year, name, false))
				{
					PitcherLine p = x.f.Model.Pitchers[x.i];
					Pit s = Pit.From(x.f, x.i);
					h.Append($"<tr><td>{year} {WebUtility.HtmlEncode(p.Name)} ({Team(p.Team)})</td>");
					Pair(h, p.G, s.G, "F0", 3);
					Pair(h, p.Outs / 3.0, s.Outs / 3, "F1", 10);
					Pair(h, Era(p), s.Era, "F2", 0.30);
					Pair(h, p.W, s.W, "F1", 3);
					Pair(h, p.L, s.L, "F1", 3);
					Pair(h, p.SV, s.SV, "F1", 4);
					Pair(h, p.SO, s.SO, "F1", 12);
					Pair(h, p.BB, s.BB, "F1", 8);
					h.Append("</tr>");
				}
			}
			h.Append("</table></div>");

			h.Append("<h2>타점 차이가 가장 큰 타자 (엔진 점검용)</h2><div class=\"wrap\"><table><tr><th>선수</th><th>타순 영향 지표: 타석</th><th>타점</th><th>득점</th><th>홈런</th><th>출루율</th></tr>");
			foreach (var x in regulars.OrderByDescending(x => Math.Abs(x.s.RBI - x.f.Model.Batters[x.i].RBI)).Take(15))
			{
				BatterLine b = x.f.Model.Batters[x.i];
				h.Append($"<tr><td>{b.Year} {WebUtility.HtmlEncode(b.Name)} ({Team(b.Team)})</td>");
				Pair(h, b.PA, x.s.PA, "F0", 15);
				Pair(h, b.RBI, x.s.RBI, "F1", 8);
				Pair(h, b.R, x.s.R, "F1", 8);
				Pair(h, b.HR, x.s.HR, "F1", 3);
				Pair(h, Obp(b), x.s.Obp, ".000", 0.01);
				h.Append("</tr>");
			}
			h.Append("</table></div>");
			h.Append("<p class=\"note\">각 칸: 실제 / 시뮬레이션(검증 반복 평균). 초록은 허용 오차 이내, 빨강은 벗어난 값.</p>");
			ReportExtra.Html(h, fits, parks, check, era, defence, FamousBatters, FamousPitchers);
			h.Append("</body></html>");
			return h.ToString();
		}

		private static IEnumerable<(SeasonFit f, int i)> Find(IReadOnlyList<SeasonFit> fits, int year, string name, bool batter)
		{
			SeasonFit f = fits.FirstOrDefault(x => x.Model.Season.Year == year);
			if (f == null)
			{
				yield break;
			}
			if (batter)
			{
				int best = -1;
				for (int i = 0; i < f.Model.Batters.Length; i++)
				{
					if (f.Model.Batters[i].Name == name && (best < 0 || f.Model.Batters[i].PA > f.Model.Batters[best].PA))
					{
						best = i;
					}
				}
				if (best >= 0)
				{
					yield return (f, best);
				}
			}
			else
			{
				int best = -1;
				for (int i = 0; i < f.Model.Pitchers.Length; i++)
				{
					if (f.Model.Pitchers[i].Name == name && (best < 0 || f.Model.Pitchers[i].Outs > f.Model.Pitchers[best].Outs))
					{
						best = i;
					}
				}
				if (best >= 0)
				{
					yield return (f, best);
				}
			}
		}

		private static void Kpi(StringBuilder h, string label, string value, string note)
		{
			h.Append($"<div>{label}<b>{value}</b><span class=\"note\">{note}</span></div>");
		}

		private static void Pair(StringBuilder h, double real, double sim, string fmt, double tol)
		{
			string cls = Math.Abs(real - sim) <= tol ? "good" : "bad";
			h.Append($"<td>{real.ToString(fmt, Inv)} / <span class=\"{cls}\">{sim.ToString(fmt, Inv)}</span></td>");
		}

		public static double Corr(IEnumerable<double> xs, IEnumerable<double> ys)
		{
			double[] x = xs.ToArray(), y = ys.ToArray();
			double mx = x.Average(), my = y.Average();
			double sxy = 0, sxx = 0, syy = 0;
			for (int i = 0; i < x.Length; i++)
			{
				sxy += (x[i] - mx) * (y[i] - my);
				sxx += (x[i] - mx) * (x[i] - mx);
				syy += (y[i] - my) * (y[i] - my);
			}
			return sxy / Math.Sqrt(sxx * syy);
		}

		private static double Avg(BatterLine b) => b.AB > 0 ? b.H / (double)b.AB : 0;

		private static double Obp(BatterLine b) => b.PA > 0 ? (b.H + b.BB + b.HBP) / (double)b.PA : 0;

		private static double Slg(BatterLine b) => b.AB > 0 ? (b.H + b.D + 2 * b.T + 3 * b.HR) / (double)b.AB : 0;

		private static double Era(PitcherLine p) => p.Outs > 0 ? p.ER * 27.0 / p.Outs : 0;

		private static string Team(int code) => Records.TeamNames.TryGetValue(code, out string n) ? n : code.ToString();

		private static string F(double v, int digits) => v.ToString("F" + digits, Inv);

		private static string Csv(string s) => s.Contains(',') ? "\"" + s + "\"" : s;

		private struct Bat
		{
			public double PA, AB, H, D, T, HR, BB, SO, HBP, SB, R, RBI;

			public double Avg => AB > 0 ? H / AB : 0;

			public double Obp => PA > 0 ? (H + BB + HBP) / PA : 0;

			public double Slg => AB > 0 ? (H + D + 2 * T + 3 * HR) / AB : 0;

			public static Bat From(SeasonFit f, int i)
			{
				double n = f.ValidationReps;
				SimStats s = f.Validation;
				return new Bat
				{
					PA = s.Bat(i, BS.PA) / n, AB = s.Bat(i, BS.AB) / n, H = s.Bat(i, BS.H) / n, D = s.Bat(i, BS.D) / n, T = s.Bat(i, BS.T) / n,
					HR = s.Bat(i, BS.HR) / n, BB = s.Bat(i, BS.BB) / n, SO = s.Bat(i, BS.SO) / n, HBP = s.Bat(i, BS.HBP) / n,
					SB = s.Bat(i, BS.SB) / n, R = s.Bat(i, BS.R) / n, RBI = s.Bat(i, BS.RBI) / n
				};
			}
		}

		private struct Pit
		{
			public double G, GS, Outs, ER, R, W, L, SV, HLD, SO, BB, HR;

			public double Era => Outs > 0 ? ER * 27 / Outs : 0;

			public static Pit From(SeasonFit f, int i)
			{
				double n = f.ValidationReps;
				SimStats s = f.Validation;
				return new Pit
				{
					G = s.Pit(i, PS.G) / n, GS = s.Pit(i, PS.GS) / n, Outs = s.Pit(i, PS.Outs) / n, ER = s.Pit(i, PS.ER) / n, R = s.Pit(i, PS.R) / n, W = s.Pit(i, PS.W) / n,
					L = s.Pit(i, PS.L) / n, SV = s.Pit(i, PS.SV) / n, HLD = s.Pit(i, PS.HLD) / n, SO = s.Pit(i, PS.SO) / n, BB = s.Pit(i, PS.BB) / n, HR = s.Pit(i, PS.HR) / n
				};
			}
		}

		private struct LeagueLine
		{
			public double Avg, Obp, Slg, HrPct, KPct, BbPct, Rpg, Era;

			public static LeagueLine Real(SeasonFit f)
			{
				IEnumerable<BatterLine> bs = f.Model.Batters.Where(b => !b.Replacement);
				double pa = bs.Sum(b => b.PA), ab = bs.Sum(b => b.AB), h = bs.Sum(b => b.H), d = bs.Sum(b => b.D), t = bs.Sum(b => b.T), hr = bs.Sum(b => b.HR);
				double bb = bs.Sum(b => b.BB), so = bs.Sum(b => b.SO), hbp = bs.Sum(b => b.HBP);
				IEnumerable<PitcherLine> ps = f.Model.Pitchers.Where(p => !p.Replacement);
				return new LeagueLine
				{
					Avg = h / ab, Obp = (h + bb + hbp) / pa, Slg = (h + d + 2 * t + 3 * hr) / ab, HrPct = hr / pa, KPct = so / pa, BbPct = bb / pa,
					// Real runs per PA at the simulated PA per game (records miss some batters).
					Rpg = Fitter.RealRunsPerPa(f.Model) * Fitter.TotalPa(f.Model, f.Validation) / (f.Validation.Games * 2.0), Era = ps.Sum(p => p.ER) * 27.0 / ps.Sum(p => p.Outs)
				};
			}

			public static LeagueLine Sim(SeasonFit f)
			{
				SimStats s = f.Validation;
				double pa = 0, ab = 0, h = 0, d = 0, t = 0, hr = 0, bb = 0, so = 0, hbp = 0;
				for (int i = 0; i < f.Model.Batters.Length; i++)
				{
					if (f.Model.Batters[i].Replacement)
					{
						continue;
					}
					pa += s.Bat(i, BS.PA); ab += s.Bat(i, BS.AB); h += s.Bat(i, BS.H); d += s.Bat(i, BS.D); t += s.Bat(i, BS.T); hr += s.Bat(i, BS.HR);
					bb += s.Bat(i, BS.BB); so += s.Bat(i, BS.SO); hbp += s.Bat(i, BS.HBP);
				}
				double er = 0, outs = 0;
				for (int i = 0; i < f.Model.Pitchers.Length; i++)
				{
					if (f.Model.Pitchers[i].Replacement)
					{
						continue;
					}
					er += s.Pit(i, PS.ER);
					outs += s.Pit(i, PS.Outs);
				}
				return new LeagueLine
				{
					Avg = h / ab, Obp = (h + bb + hbp) / pa, Slg = (h + d + 2 * t + 3 * hr) / ab, HrPct = hr / pa, KPct = so / pa, BbPct = bb / pa,
					Rpg = s.Runs / (double)(s.Games * 2), Era = er * 27 / outs
				};
			}
		}
	}
}
