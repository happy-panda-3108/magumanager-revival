using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	// Park effects per component (multipliers on per-PA rates for a game played there).
	public static class PC
	{
		public const int K = 0, BB = 1, HR = 2, Babip = 3, Double = 4, Triple = 5, Count = 6;
		public static readonly string[] Names = { "K", "BB", "HR", "BABIP", "2B", "3B" };
	}

	public sealed class ParkInfo
	{
		public string Id;
		public string Name;
		public readonly double[] Factor = Enumerable.Repeat(1.0, PC.Count).ToArray();
		public readonly double[] Se = new double[PC.Count];  // standard error of log(factor)
		public double RunIndex = 1.0;
		public double RawRunIndex = 1.0;    // before the wRC+ calibration
		public bool Calibrated;
		public int FirstYear = int.MaxValue, LastYear;
		public readonly SortedSet<string> Teams = new SortedSet<string>();
	}

	public sealed class ParkModel
	{
		public readonly Dictionary<string, ParkInfo> Parks = new Dictionary<string, ParkInfo>();
		public readonly List<string> Diagnostics = new List<string>();
		public bool Enabled = true;

		// (team, year) -> park.
		public ParkInfo HomeOf(int team, int year)
		{
			string id = Stadiums.HomeId(team, year);
			return id != null && Parks.TryGetValue(id, out ParkInfo p) ? p : null;
		}

		// Factors for the parks of one season, scaled so the season's average park is neutral
		// (the season's league rates already include its parks).
		public double[][] SeasonFactors(Season s)
		{
			int n = s.Teams.Count;
			double[][] f = new double[n][];
			for (int t = 0; t < n; t++)
			{
				ParkInfo p = Enabled ? HomeOf(s.Teams[t].Code, s.Year) : null;
				f[t] = p != null ? (double[])p.Factor.Clone() : Enumerable.Repeat(1.0, PC.Count).ToArray();
			}
			for (int c = 0; c < PC.Count; c++)
			{
				double mean = f.Average(x => x[c]);
				for (int t = 0; t < n; t++)
				{
					f[t][c] /= mean;
				}
			}
			return f;
		}
	}

	// Home stadium of each club by season (main park only; second homes such as 마산 for
	// 롯데 or 청주 for 한화 are ignored).
	public static class Stadiums
	{
		public static readonly Dictionary<string, string> Names = new Dictionary<string, string>
		{
			{ "DDM", "동대문" }, { "JAM", "잠실" }, { "DJN", "대전 한밭(~2011)" }, { "DJE", "대전 이글스파크(2012~2024)" },
			{ "DJB", "대전 한화생명볼파크" }, { "DGS", "대구 시민" }, { "DGL", "대구 라이온즈파크" }, { "GDK", "부산 구덕" },
			{ "SJK", "사직(~2021)" }, { "SJ2", "사직(2022~ 펜스 상향)" }, { "MDG", "광주 무등" }, { "GCF", "광주 챔피언스필드" },
			{ "ICS", "인천 숭의(도원)" }, { "MHK", "인천 문학" }, { "JJU", "전주" }, { "SWH", "수원(현대 시절)" },
			{ "SWK", "수원 KT위즈파크" }, { "MKD", "목동" }, { "GCD", "고척 스카이돔" }, { "MSN", "마산" }, { "CNC", "창원 NC파크" }
		};

		private static readonly (string park, int team, int from, int to)[] Homes =
		{
			("DDM", 11, 1982, 1982), ("JAM", 11, 1983, 1989), ("JAM", 6, 1990, 2026),
			("DJN", 14, 1982, 1984), ("DDM", 14, 1985, 1985), ("JAM", 14, 1986, 1998), ("JAM", 3, 1999, 2026),
			("DGS", 2, 1982, 2015), ("DGL", 2, 2016, 2026),
			("GDK", 8, 1982, 1985), ("SJK", 8, 1986, 2021), ("SJ2", 8, 2022, 2026),
			("MDG", 9, 1982, 2001), ("MDG", 4, 2001, 2013), ("GCF", 4, 2014, 2026),
			("ICS", 16, 1982, 1985), ("ICS", 15, 1985, 1987), ("ICS", 12, 1988, 1995), ("ICS", 1, 1996, 1999), ("ICS", 5, 2000, 2001),
			("MHK", 5, 2002, 2020), ("MHK", 26, 2021, 2026),
			("DJN", 10, 1986, 1993), ("DJN", 7, 1994, 2011), ("DJE", 7, 2012, 2024), ("DJB", 7, 2025, 2026),
			("JJU", 13, 1991, 1999),
			("SWH", 1, 2000, 2007), ("SWK", 25, 2015, 2026),
			("MKD", 17, 2008, 2015), ("GCD", 17, 2016, 2026),
			("MSN", 24, 2013, 2018), ("CNC", 24, 2019, 2026),
		};

		public static string HomeId(int team, int year)
		{
			foreach (var h in Homes)
			{
				if (h.team == team && year >= h.from && year <= h.to)
				{
					return h.park;
				}
			}
			return null;
		}
	}

	// Estimates park factors from season totals alone (no home/road splits exist in the data):
	// a Poisson log-linear model over every player-season,
	//   count ~ n * season * player(career block) * [0.5 f(home) + 0.5 avg f(road parks)],
	// fitted by EM. Parks are identified by players changing teams and clubs changing parks.
	public static class ParkEstimator
	{
		private sealed class Obs
		{
			public bool Pitcher;
			public int Player;      // index into player-block effects
			public int Year;        // index into season effects
			public int TeamYear;    // index into team-season defence effects (pitcher BABIP only)
			public int Home;        // park index of own team
			public int[] Road;      // park index of each other club that season
			public double Count, N;
		}

		public static ParkModel Estimate(List<Season> seasons, int iterations = 200)
		{
			ParkModel model = new ParkModel();
			Dictionary<string, int> parkIndex = new Dictionary<string, int>();
			List<ParkInfo> parks = new List<ParkInfo>();
			int ParkOf(int team, int year)
			{
				string id = Stadiums.HomeId(team, year) ?? $"T{team}";
				if (!parkIndex.TryGetValue(id, out int i))
				{
					i = parks.Count;
					parkIndex[id] = i;
					parks.Add(new ParkInfo { Id = id, Name = Stadiums.Names.TryGetValue(id, out string n) ? n : id });
				}
				ParkInfo p = parks[i];
				p.FirstYear = Math.Min(p.FirstYear, year);
				p.LastYear = Math.Max(p.LastYear, year);
				p.Teams.Add(Records.TeamNames.TryGetValue(team, out string tn) ? tn : team.ToString());
				return i;
			}

			// Career blocks of four seasons from each player's first recorded season absorb aging.
			Dictionary<int, int> debutB = new Dictionary<int, int>(), debutP = new Dictionary<int, int>();
			foreach (Season s in seasons)
			{
				foreach (BatterLine b in s.Batters.Where(b => !b.Replacement))
				{
					debutB[b.Sidx] = Math.Min(debutB.TryGetValue(b.Sidx, out int y) ? y : int.MaxValue, s.Year);
				}
				foreach (PitcherLine p in s.Pitchers.Where(p => !p.Replacement))
				{
					debutP[p.Sidx] = Math.Min(debutP.TryGetValue(p.Sidx, out int y) ? y : int.MaxValue, s.Year);
				}
			}
			Dictionary<(bool, int, int), int> playerIndex = new Dictionary<(bool, int, int), int>();
			Dictionary<(int, int), int> teamYearIndex = new Dictionary<(int, int), int>();
			int PlayerOf(bool pitcher, int sidx, int year)
			{
				int block = (year - (pitcher ? debutP[sidx] : debutB[sidx])) / 4;
				if (!playerIndex.TryGetValue((pitcher, sidx, block), out int i))
				{
					i = playerIndex.Count;
					playerIndex[(pitcher, sidx, block)] = i;
				}
				return i;
			}

			List<Obs>[] obs = Enumerable.Range(0, PC.Count).Select(_ => new List<Obs>()).ToArray();
			int[] yearOf = seasons.Select(s => s.Year).ToArray();
			for (int y = 0; y < seasons.Count; y++)
			{
				Season s = seasons[y];
				int[] homes = s.Teams.Select(t => ParkOf(t.Code, s.Year)).ToArray();
				for (int t = 0; t < s.Teams.Count; t++)
				{
					TeamSeason team = s.Teams[t];
					int[] road = homes.Where((_, j) => j != t).ToArray();
					if (!teamYearIndex.TryGetValue((team.Code, s.Year), out int ty))
					{
						ty = teamYearIndex.Count;
						teamYearIndex[(team.Code, s.Year)] = ty;
					}
					foreach (BatterLine b in team.Batters.Where(b => !b.Replacement && b.PA > 0))
					{
						int pl = PlayerOf(false, b.Sidx, s.Year);
						Obs Make(double count, double n) => new Obs { Player = pl, Year = y, TeamYear = -1, Home = homes[t], Road = road, Count = count, N = n };
						double hip = Math.Max(0, b.H - b.HR);
						obs[PC.K].Add(Make(b.SO, b.PA));
						obs[PC.BB].Add(Make(b.BB, b.PA));
						obs[PC.HR].Add(Make(b.HR, b.PA));
						obs[PC.Babip].Add(Make(hip, Math.Max(0, b.AB - b.SO - b.HR)));
						obs[PC.Double].Add(Make(b.D, hip));
						obs[PC.Triple].Add(Make(b.T, hip));
					}
					foreach (PitcherLine p in team.Pitchers.Where(p => !p.Replacement && p.BF > 0))
					{
						int pl = PlayerOf(true, p.Sidx, s.Year);
						Obs Make(double count, double n, int teamYear) => new Obs { Pitcher = true, Player = pl, Year = y, TeamYear = teamYear, Home = homes[t], Road = road, Count = count, N = n };
						obs[PC.K].Add(Make(p.SO, p.BF, -1));
						obs[PC.BB].Add(Make(p.BB, p.BF, -1));
						obs[PC.HR].Add(Make(p.HR, p.BF, -1));
						// A club's fielders shape its pitchers' BABIP in every park: give pitcher BABIP
						// a team-season effect so that only the hitters inform the BABIP park factor.
						obs[PC.Babip].Add(Make(Math.Max(0, p.H - p.HR), Math.Max(0, p.BF - p.SO - p.BB - p.HBP - p.HR), ty));
					}
				}
			}

			int nPark = parks.Count, nPlayer = playerIndex.Count, nYear = seasons.Count, nTy = teamYearIndex.Count;
			for (int c = 0; c < PC.Count; c++)
			{
				List<Obs> list = obs[c].Where(o => o.N > 0).ToList();
				double lgRate = list.Sum(o => o.Count) / list.Sum(o => o.N);
				// Prior strengths, in expected events: a player-block is pulled toward average by
				// ~150 PA worth, a park toward neutral by about a third of a club-season's home events.
				double playerPrior = (c == PC.Double || c == PC.Triple ? 40 : c == PC.Babip ? 120 : 150) * lgRate;
				// Parks barely move strikeouts and walks: hold those factors closer to neutral.
				double parkPrior = list.Where(o => !o.Pitcher).Sum(o => o.Count) / Math.Max(1, seasons.Sum(s => s.Teams.Count)) * 0.5 * (c == PC.K || c == PC.BB ? 1.0 : 0.35);
				double[] fPark = Enumerable.Repeat(1.0, nPark).ToArray();
				double[] fPlayer = Enumerable.Repeat(1.0, nPlayer).ToArray();
				double[,] fYear = new double[2, nYear];
				double[] fTy = Enumerable.Repeat(1.0, nTy).ToArray();
				for (int y = 0; y < nYear; y++)
				{
					fYear[0, y] = fYear[1, y] = lgRate;
				}
				double MixOf(Obs o)
				{
					double road = 0;
					foreach (int r in o.Road)
					{
						road += fPark[r];
					}
					return 0.5 * fPark[o.Home] + 0.5 * road / o.Road.Length;
				}
				double[] mixes = list.Select(MixOf).ToArray();
				Dictionary<Obs, int> at = new Dictionary<Obs, int>(ReferenceEqualityComparer.Instance);
				for (int i = 0; i < list.Count; i++)
				{
					at[list[i]] = i;
				}
				double Mix(Obs o) => mixes[at[o]];
				double Base(Obs o)
				{
					return o.N * fYear[o.Pitcher ? 1 : 0, o.Year] * fPlayer[o.Player] * (o.TeamYear >= 0 ? fTy[o.TeamYear] : 1.0);
				}
				double[] num = new double[Math.Max(Math.Max(nPark, nPlayer), Math.Max(nYear * 2, nTy))];
				double[] den = new double[num.Length];
				for (int it = 0; it < iterations; it++)
				{
					// Players.
					Array.Clear(num); Array.Clear(den);
					foreach (Obs o in list)
					{
						num[o.Player] += o.Count;
						den[o.Player] += Base(o) / fPlayer[o.Player] * Mix(o);
					}
					for (int i = 0; i < nPlayer; i++)
					{
						fPlayer[i] = (num[i] + playerPrior) / (den[i] + playerPrior);
					}
					// Seasons (separately for the batting and pitching records).
					Array.Clear(num); Array.Clear(den);
					foreach (Obs o in list)
					{
						int k = (o.Pitcher ? nYear : 0) + o.Year;
						num[k] += o.Count;
						den[k] += Base(o) / fYear[o.Pitcher ? 1 : 0, o.Year] * Mix(o);
					}
					for (int y = 0; y < nYear; y++)
					{
						if (den[y] > 0) fYear[0, y] = num[y] / den[y];
						if (den[nYear + y] > 0) fYear[1, y] = num[nYear + y] / den[nYear + y];
					}
					// Team-season defence (pitcher BABIP only).
					if (c == PC.Babip)
					{
						Array.Clear(num); Array.Clear(den);
						foreach (Obs o in list.Where(o => o.TeamYear >= 0))
						{
							num[o.TeamYear] += o.Count;
							den[o.TeamYear] += Base(o) / fTy[o.TeamYear] * Mix(o);
						}
						double prior = 300 * lgRate;
						for (int i = 0; i < nTy; i++)
						{
							fTy[i] = (num[i] + prior) / (den[i] + prior);
						}
					}
					// Parks: EM share of each observation's events attributed to each park.
					Array.Clear(num); Array.Clear(den);
					foreach (Obs o in list)
					{
						double b = Base(o), mix = Mix(o);
						double wHome = 0.5, wRoad = 0.5 / o.Road.Length;
						num[o.Home] += o.Count * wHome * fPark[o.Home] / mix;
						den[o.Home] += b * wHome;
						foreach (int r in o.Road)
						{
							num[r] += o.Count * wRoad * fPark[r] / mix;
							den[r] += b * wRoad;
						}
					}
					double logSum = 0, wSum = 0;
					for (int i = 0; i < nPark; i++)
					{
						fPark[i] = (num[i] + parkPrior) / (den[i] + parkPrior);
						logSum += Math.Log(fPark[i]) * den[i];
						wSum += den[i];
					}
					double g = Math.Exp(logSum / wSum);
					for (int i = 0; i < nPark; i++)
					{
						fPark[i] /= g;
					}
					for (int i = 0; i < list.Count; i++)
					{
						mixes[i] = MixOf(list[i]);
					}
					if (it == iterations - 1)
					{
						for (int i = 0; i < nPark; i++)
						{
							parks[i].Factor[c] = fPark[i];
							parks[i].Se[c] = 1 / Math.Sqrt(Math.Max(1, num[i] + parkPrior));
						}
					}
				}
			}

			foreach (ParkInfo p in parks)
			{
				model.Parks[p.Id] = p;
			}
			SetRunIndex(model, seasons);
			foreach (ParkInfo p in parks)
			{
				p.RawRunIndex = p.RunIndex;
			}
			return model;
		}

		// Where the records carry park-adjusted wRC+ (2016+), pull each park's run level toward
		// what those numbers imply. Expansion clubs (NC at 마산, KT) took in mostly declining
		// veterans, which the player-movement estimate reads as a pitcher's park.
		public static void Calibrate(ParkModel model, List<(int year, int team, double recordPf, double modelPf)> check)
		{
			foreach (var g in check.Where(x => !double.IsNaN(x.modelPf)).GroupBy(x => model.HomeOf(x.team, x.year)))
			{
				ParkInfo p = g.Key;
				if (p == null)
				{
					continue;
				}
				int n = g.Count();
				// Effective (half home, half road) gap -> whole-park gap, shrunk for few seasons.
				double target = p.RunIndex + 2 * g.Average(x => x.recordPf - x.modelPf) * n / (n + 3.0);
				double lo = -0.5, hi = 0.5;
				double hr0 = p.Factor[PC.HR], babip0 = p.Factor[PC.Babip];
				for (int i = 0; i < 40; i++)
				{
					double x = (lo + hi) / 2;
					p.Factor[PC.HR] = hr0 * Math.Exp(2 * x);
					p.Factor[PC.Babip] = babip0 * Math.Exp(x);
					SetRunIndex(model, p);
					if (p.RunIndex < target) lo = x; else hi = x;
				}
				p.Calibrated = true;
			}
		}

		private static double s_pa, s_ab, s_h, s_d, s_t, s_hr, s_bb, s_so, s_r;

		// Linear-weights run index of a park relative to the average park (league events of
		// the modern era, with each component scaled by the park's factor).
		private static void SetRunIndex(ParkModel model, List<Season> seasons)
		{
			IEnumerable<BatterLine> all = seasons.SelectMany(s => s.Batters).Where(b => !b.Replacement);
			s_pa = all.Sum(b => b.PA); s_ab = all.Sum(b => b.AB); s_h = all.Sum(b => b.H); s_d = all.Sum(b => b.D); s_t = all.Sum(b => b.T);
			s_hr = all.Sum(b => b.HR); s_bb = all.Sum(b => b.BB); s_so = all.Sum(b => b.SO); s_r = all.Sum(b => b.R);
			foreach (ParkInfo p in model.Parks.Values)
			{
				SetRunIndex(model, p);
			}
		}

		private static void SetRunIndex(ParkModel model, ParkInfo p)
		{
			double pa = s_pa, ab = s_ab, h = s_h, d = s_d, t = s_t, hr = s_hr, bb = s_bb, so = s_so, r = s_r;
			{
				double k = so / pa * p.Factor[PC.K], w = bb / pa * p.Factor[PC.BB], hrr = hr / pa * p.Factor[PC.HR];
				double bipShare = (ab - so - hr) / pa;
				double bip = Math.Max(0, bipShare - (k - so / pa) - (hrr - hr / pa) - (w - bb / pa));
				double hitRate = (h - hr) / (ab - so - hr) * p.Factor[PC.Babip];
				double dShare = d / (h - hr) * p.Factor[PC.Double], tShare = t / (h - hr) * p.Factor[PC.Triple];
				double hits = bip * hitRate;
				double runs = 0.33 * w + 0.47 * hits * (1 - dShare - tShare) + 0.77 * hits * dShare + 1.04 * hits * tShare + 1.40 * hrr - 0.27 * (bip - hits + k);
				double lgHits = (h - hr) / pa;
				double lgRuns = 0.33 * bb / pa + 0.47 * (h - hr - d - t) / pa + 0.77 * d / pa + 1.04 * t / pa + 1.40 * hr / pa - 0.27 * (ab - h) / pa;
				p.RunIndex = (r / pa + runs - lgRuns) / (r / pa);
			}
		}

		// Cross-check against the park-adjusted wRC+ that the records carry for 2016+: within a
		// season, wRC+ is linear in wOBA with a club-specific shift of -100 x (PF - 1).
		public static List<(int year, int team, double recordPf, double modelPf)> CrossCheck(string tablesDir, ParkModel model, List<Season> seasons)
		{
			Dictionary<(int, int), List<(double woba, double wrc, double pa)>> rows = new Dictionary<(int, int), List<(double, double, double)>>();
			foreach (string[] r in Records.ReadTsv(System.IO.Path.Combine(tablesDir, "Recordtable_B.txt")).Skip(2))
			{
				if (r.Length < 25 || !int.TryParse(r[3].Trim(), out int year) || !int.TryParse(r[5].Trim(), out int team) || !Records.TeamNames.ContainsKey(team))
				{
					continue;
				}
				if (double.TryParse(r[23].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double woba)
					&& double.TryParse(r[24].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double wrc)
					&& double.TryParse(r[10].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pa) && pa >= 100)
				{
					if (!rows.TryGetValue((year, team), out var l))
					{
						l = new List<(double, double, double)>();
						rows[(year, team)] = l;
					}
					l.Add((woba, wrc, pa));
				}
			}
			List<(int, int, double, double)> result = new List<(int, int, double, double)>();
			foreach (int year in rows.Keys.Select(k => k.Item1).Distinct().OrderBy(y => y))
			{
				var teams = rows.Where(kv => kv.Key.Item1 == year && kv.Value.Count >= 5).ToList();
				if (teams.Count < 6)
				{
					continue;
				}
				// Common slope from within-team variation.
				double sxy = 0, sxx = 0;
				foreach (var kv in teams)
				{
					double mw = kv.Value.Sum(x => x.woba * x.pa) / kv.Value.Sum(x => x.pa);
					double mr = kv.Value.Sum(x => x.wrc * x.pa) / kv.Value.Sum(x => x.pa);
					foreach (var x in kv.Value)
					{
						sxy += x.pa * (x.woba - mw) * (x.wrc - mr);
						sxx += x.pa * (x.woba - mw) * (x.woba - mw);
					}
				}
				double slope = sxy / sxx;
				Dictionary<int, double> shift = new Dictionary<int, double>();
				foreach (var kv in teams)
				{
					shift[kv.Key.Item2] = kv.Value.Sum(x => (x.wrc - slope * x.woba) * x.pa) / kv.Value.Sum(x => x.pa);
				}
				double mean = shift.Values.Average();
				Season season = seasons.FirstOrDefault(s => s.Year == year);
				foreach (var kv in shift)
				{
					double recordPf = 1 - (kv.Value - mean) / 100.0;
					double modelPf = double.NaN;
					if (season != null)
					{
						ParkInfo home = model.HomeOf(kv.Key, year);
						List<ParkInfo> others = season.Teams.Where(t => t.Code != kv.Key).Select(t => model.HomeOf(t.Code, year)).Where(p => p != null).ToList();
						if (home != null && others.Count > 0)
						{
							double all = season.Teams.Select(t => model.HomeOf(t.Code, year)).Where(p => p != null).Average(p => p.RunIndex);
							modelPf = (0.5 * home.RunIndex + 0.5 * others.Average(p => p.RunIndex)) / all;
						}
					}
					result.Add((year, kv.Key, recordPf, modelPf));
				}
			}
			return result;
		}
	}
}
