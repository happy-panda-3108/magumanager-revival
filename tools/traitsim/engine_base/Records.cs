using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ma9MSim
{
	public sealed class BatterLine
	{
		public int Sidx;
		public string Name;
		public int Year;
		public int Team;
		public int G, PA, AB, H, D, T, HR, BB, SO, HBP, SB, R, RBI;
		public double Fpct = double.NaN; // fielding percentage (records have it from 2017)
		public bool Replacement;

		public int Singles => H - D - T - HR;
	}

	public sealed class PitcherLine
	{
		public int Sidx;
		public string Name;
		public int Year;
		public int Team;
		public int G, Outs, H, HR, BB, SO, HBP, ER, W, L, SV, HLD;
		public bool Starter;
		public int GS; // estimated: the records have no games-started column
		public bool Replacement;

		public int BF => Outs + H + BB + HBP;
	}

	public sealed class TeamSeason
	{
		public int Code;
		public List<BatterLine> Batters = new List<BatterLine>();
		public List<PitcherLine> Pitchers = new List<PitcherLine>();
	}

	public sealed class Season
	{
		public int Year;
		public int Games; // per team
		public List<TeamSeason> Teams = new List<TeamSeason>();

		public IEnumerable<BatterLine> Batters => Teams.SelectMany(t => t.Batters);

		public IEnumerable<PitcherLine> Pitchers => Teams.SelectMany(t => t.Pitchers);
	}

	public static class Records
	{
		private static readonly HashSet<int> KboTeams = new HashSet<int>(Enumerable.Range(1, 17).Concat(new[] { 24, 25, 26 }));

		public static readonly Dictionary<int, string> TeamNames = new Dictionary<int, string>
		{
			{ 1, "현대" }, { 2, "삼성" }, { 3, "두산" }, { 4, "KIA" }, { 5, "SK" }, { 6, "LG" }, { 7, "한화" }, { 8, "롯데" },
			{ 9, "해태" }, { 10, "빙그레" }, { 11, "MBC" }, { 12, "태평양" }, { 13, "쌍방울" }, { 14, "OB" }, { 15, "청보" },
			{ 16, "삼미" }, { 17, "히어로즈" }, { 24, "NC" }, { 25, "KT" }, { 26, "SSG" }
		};

		public static List<Season> Load(string tablesDir, string cardTablesDir, int firstYear, int lastYear)
		{
			Dictionary<(int, int), int> cardPos = LoadPitcherCardPositions(Path.Combine(cardTablesDir, "P_PItcherTable.txt"));
			Dictionary<int, Season> seasons = new Dictionary<int, Season>();
			Season SeasonOf(int year)
			{
				if (!seasons.TryGetValue(year, out Season s))
				{
					s = new Season { Year = year };
					seasons[year] = s;
				}
				return s;
			}
			TeamSeason TeamOf(Season s, int code)
			{
				TeamSeason t = s.Teams.FirstOrDefault(x => x.Code == code);
				if (t == null)
				{
					t = new TeamSeason { Code = code };
					s.Teams.Add(t);
				}
				return t;
			}

			// From 2017 the tables repeat a player-season once per card grade (LV); keep one.
			HashSet<(int, int, int)> seenB = new HashSet<(int, int, int)>(), seenP = new HashSet<(int, int, int)>();
			foreach (string[] r in ReadTsv(Path.Combine(tablesDir, "Recordtable_B.txt")).Skip(2))
			{
				if (!TryRow(r, firstYear, lastYear, out int sidx, out int year, out int team) || !seenB.Add((sidx, year, team)))
				{
					continue;
				}
				BatterLine b = new BatterLine
				{
					Sidx = sidx, Name = RealNames.TryGetValue(sidx, out string realB) ? realB : CleanName(r[1]), Year = year, Team = team,
					G = I(r[9]), PA = I(r[10]), AB = I(r[11]), R = I(r[12]), H = I(r[13]), D = I(r[14]), T = I(r[15]),
					HR = I(r[7]), RBI = I(r[16]), BB = I(r[17]), SO = I(r[18]), HBP = I(r[19]), SB = I(r[8])
				};
				if (r.Length > 27 && double.TryParse(r[27].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fpct) && fpct > 0.5 && fpct <= 1)
				{
					b.Fpct = fpct;
				}
				if (b.PA > 0)
				{
					TeamOf(SeasonOf(year), team).Batters.Add(b);
				}
			}
			foreach (string[] r in ReadTsv(Path.Combine(tablesDir, "Recordtable_P.txt")).Skip(2))
			{
				if (!TryRow(r, firstYear, lastYear, out int sidx, out int year, out int team) || !seenP.Add((sidx, year, team)))
				{
					continue;
				}
				PitcherLine p = new PitcherLine
				{
					Sidx = sidx, Name = RealNames.TryGetValue(sidx, out string realP) ? realP : CleanName(r[1]), Year = year, Team = team,
					W = I(r[7]), L = I(r[8]), SV = I(r[9]), HLD = I(r[10]), G = I(r[11]), Outs = Outs(r[12]),
					H = I(r[13]), HR = I(r[14]), BB = I(r[15]), SO = I(r[16]), HBP = I(r[17])
				};
				// Column 18 is earned runs in most seasons but total runs in 2014, 2015, 2018 and
				// 2024+; the ERA column is consistent, so earned runs come from it.
				p.ER = double.TryParse(r[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double era)
					? (int)Math.Round(era * p.Outs / 27.0) : I(r[18]);
				if (p.Outs <= 0)
				{
					continue;
				}
				if (!double.TryParse(r[6].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
				{
					p.ER = I(r[18]);
				}
				// Innings per appearance decide clear cases (the cards' positions are sometimes off,
				// e.g. 2009 류현진 is carded as a reliever); in between, the card's position decides.
				double perGame = p.Outs / (double)Math.Max(1, p.G);
				p.Starter = perGame >= 12 ? true
					: perGame < 6 ? false
					: cardPos.TryGetValue((sidx, year), out int pos) ? pos == 12 : p.Outs >= 10.5 * p.G;
				TeamOf(SeasonOf(year), team).Pitchers.Add(p);
			}

			List<Season> result = seasons.Values.Where(s => s.Teams.Count >= 6).OrderBy(s => s.Year).ToList();
			foreach (Season s in result)
			{
				s.Teams.RemoveAll(t => t.Batters.Count < 9 || t.Pitchers.Count == 0);
				s.Games = Median(s.Teams.Select(t => t.Batters.Max(b => b.G)));
				foreach (TeamSeason t in s.Teams)
				{
					FillGaps(s, t);
					EstimateStarts(s, t);
				}
				AssignMissingPitching(s);
			}
			return result;
		}

		// Pitching records are incomplete for some seasons; give each team a replacement-level
		// arm for the missing outs (and likewise for missing plate appearances).
		private static void FillGaps(Season s, TeamSeason t)
		{
			int expectedOuts = (int)(s.Games * 26.6);
			int missingOuts = expectedOuts - t.Pitchers.Sum(p => p.Outs);
			if (missingOuts > 27)
			{
				t.Pitchers.Add(new PitcherLine { Sidx = -t.Code, Name = "대체투수", Year = s.Year, Team = t.Code, Outs = missingOuts, G = Math.Max(1, missingOuts / 4), Replacement = true });
			}
			// Batting records also miss a few percent of plate appearances. A team-game has about
			// 25.3 batting outs (26.6 outs less double plays, caught stealing and other base outs),
			// so PA per game ~= 25.3 / (1 - OBP).
			List<BatterLine> all = s.Teams.SelectMany(x => x.Batters).Where(b => !b.Replacement).ToList();
			double obp = all.Sum(b => b.H + b.BB + b.HBP) / (double)all.Sum(b => b.PA);
			int missingPa = (int)(25.3 / (1 - obp) * s.Games) - t.Batters.Sum(b => b.PA);
			if (missingPa > 30)
			{
				t.Batters.Add(new BatterLine { Sidx = -t.Code, Name = "대체타자", Year = s.Year, Team = t.Code, PA = missingPa, G = s.Games, Replacement = true });
			}
		}

		// The records have no games-started column: pitchers classed as starters are taken as
		// starting every game they pitched (scaled down if that exceeds the schedule), and any
		// starts left over go to the team's replacement arm (or its longest relievers).
		private static void EstimateStarts(Season s, TeamSeason t)
		{
			// Saves and holds are relief outings, and even regular starters relieve now and then.
			List<PitcherLine> starters = t.Pitchers.Where(p => p.Starter && !p.Replacement).ToList();
			foreach (PitcherLine p in starters)
			{
				int relief = p.SV + p.HLD + (int)Math.Round(Math.Max(0, p.G - p.SV - p.HLD) * 0.05);
				p.GS = Math.Max(1, p.G - relief);
			}
			int claimed = starters.Sum(p => p.GS);
			double scale = claimed > s.Games ? s.Games / (double)claimed : 1.0;
			foreach (PitcherLine p in starters)
			{
				p.GS = Math.Max(1, (int)Math.Round(p.GS * scale));
			}
			int remaining = s.Games - starters.Sum(p => p.GS);
			PitcherLine repl = t.Pitchers.FirstOrDefault(p => p.Replacement);
			if (remaining > 0 && repl != null)
			{
				repl.Starter = true;
				repl.GS = remaining;
				repl.G = Math.Max(repl.G, remaining);
			}
			else if (remaining > 0)
			{
				foreach (PitcherLine p in t.Pitchers.Where(p => !p.Starter).OrderByDescending(p => p.Outs / (double)Math.Max(1, p.G)))
				{
					if (remaining <= 0)
					{
						break;
					}
					int extra = Math.Min(remaining, Math.Max(1, p.G / 2));
					p.Starter = true;
					p.GS = extra;
					remaining -= extra;
				}
			}
			if (t.Pitchers.All(p => p.GS == 0))
			{
				PitcherLine top = t.Pitchers.OrderByDescending(p => p.Outs).First();
				top.Starter = true;
				top.GS = s.Games;
			}
		}

		// Batting records are near complete but pitching records are not, so what the league's
		// hitters did beyond what recorded pitchers allowed was allowed by the missing pitchers.
		// Hand that residual to the replacement arms, in proportion to their innings.
		private static void AssignMissingPitching(Season s)
		{
			List<PitcherLine> repl = s.Pitchers.Where(p => p.Replacement).ToList();
			if (repl.Count == 0)
			{
				return;
			}
			List<BatterLine> bats = s.Batters.Where(b => !b.Replacement).ToList();
			List<PitcherLine> pits = s.Pitchers.Where(p => !p.Replacement).ToList();
			int replOuts = repl.Sum(p => p.Outs);
			double share(double total, double recorded, double replRate) => Math.Max(total - recorded, replRate);
			double lgSo = bats.Sum(b => b.SO), lgBb = bats.Sum(b => b.BB), lgHbp = bats.Sum(b => b.HBP), lgHr = bats.Sum(b => b.HR), lgH = bats.Sum(b => b.H);
			double lgEr = bats.Sum(b => b.R) * 0.92;
			// The batting records miss some batters too (the replacement batters' PA); add what
			// they would have done, at the same slightly-below-average level the simulator uses.
			double pa = bats.Sum(b => b.PA), missingPa = s.Batters.Where(b => b.Replacement).Sum(b => b.PA);
			if (missingPa > 0)
			{
				double k = lgSo / pa * 1.08, w = lgBb / pa * 0.92, hp = lgHbp / pa, h4 = lgHr / pa * 0.75;
				double babip = (lgH - lgHr) / Math.Max(1, bats.Sum(b => b.AB - b.SO - b.HR)) * 0.97;
				lgSo += missingPa * k;
				lgBb += missingPa * w;
				lgHbp += missingPa * hp;
				lgHr += missingPa * h4;
				lgH += missingPa * (h4 + (1 - k - w - hp - h4) * babip);
				lgEr += missingPa * bats.Sum(b => b.R) / pa * 0.85 * 0.92;
			}
			double perOut(double v) => v / Math.Max(1, bats.Sum(b => b.AB - b.H));
			double so = share(lgSo, pits.Sum(p => p.SO), perOut(lgSo) * replOuts * 0.8);
			double bb = share(lgBb, pits.Sum(p => p.BB), perOut(lgBb) * replOuts);
			double hbp = share(lgHbp, pits.Sum(p => p.HBP), perOut(lgHbp) * replOuts);
			double hr = share(lgHr, pits.Sum(p => p.HR), perOut(lgHr) * replOuts);
			double h = share(lgH, pits.Sum(p => p.H), perOut(lgH) * replOuts);
			double er = share(lgEr, pits.Sum(p => p.ER), perOut(lgEr) * replOuts);
			foreach (PitcherLine p in repl)
			{
				double f = p.Outs / (double)replOuts;
				p.SO = (int)Math.Round(so * f);
				p.BB = (int)Math.Round(bb * f);
				p.HBP = (int)Math.Round(hbp * f);
				p.HR = (int)Math.Round(hr * f);
				p.H = (int)Math.Round(h * f);
				p.ER = (int)Math.Round(er * f);
			}
		}

		private static bool TryRow(string[] r, int firstYear, int lastYear, out int sidx, out int year, out int team)
		{
			sidx = year = team = 0;
			return r.Length > 20 && int.TryParse(r[0].Trim(), out sidx) && int.TryParse(r[3].Trim(), out year)
				&& int.TryParse(r[5].Trim(), out team) && KboTeams.Contains(team) && year >= firstYear && year <= lastYear;
		}

		private static Dictionary<(int, int), int> LoadPitcherCardPositions(string path)
		{
			Dictionary<(int, int), int> map = new Dictionary<(int, int), int>();
			if (!File.Exists(path))
			{
				return map;
			}
			foreach (string line in File.ReadLines(path, Encoding.UTF8).Skip(1))
			{
				string[] c = line.Split('\t');
				if (c.Length > 12)
				{
					map[(int.Parse(c[12]), int.Parse(c[1]))] = int.Parse(c[9]);
				}
			}
			return map;
		}

		public static readonly Dictionary<int, string> RealNames = new Dictionary<int, string>();

		public static void LoadRealNames(string path)
		{
			if (!File.Exists(path))
			{
				return;
			}
			foreach (string line in File.ReadLines(path, Encoding.UTF8).Skip(1))
			{
				string[] c = line.Split('\t');
				if (c.Length >= 3 && int.TryParse(c[0], out int sidx))
				{
					RealNames[sidx] = c[2].Trim();
				}
			}
		}

		// Recent seasons prefix the name with the season ("18 양의지").
		private static string CleanName(string raw)
		{
			string n = raw.Trim();
			return n.Length > 3 && char.IsDigit(n[0]) && char.IsDigit(n[1]) && n[2] == ' ' ? n.Substring(3).Trim() : n;
		}

		private static int I(string text)
		{
			return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? (int)Math.Round(v) : 0;
		}

		private static int Outs(string ip)
		{
			string v = ip.Trim();
			if (v.Length == 0 || v == "-")
			{
				return 0;
			}
			// Some rows write thirds as "86 1/3".
			int space = v.IndexOf(' ');
			if (space > 0 && v.EndsWith("/3"))
			{
				return I(v.Substring(0, space)) * 3 + I(v.Substring(space + 1, 1));
			}
			int dot = v.IndexOf('.');
			int whole = I(dot < 0 ? v : v.Substring(0, dot));
			int frac = dot < 0 ? 0 : I(v.Substring(dot + 1));
			return whole * 3 + frac;
		}

		private static int Median(IEnumerable<int> values)
		{
			List<int> v = values.OrderBy(x => x).ToList();
			return v[v.Count / 2];
		}

		// Tab-separated with Excel-style quoting (quoted fields may hold tabs and newlines).
		public static IEnumerable<string[]> ReadTsv(string path)
		{
			string text = File.ReadAllText(path, Encoding.UTF8);
			List<string> row = new List<string>();
			StringBuilder field = new StringBuilder();
			bool quoted = false;
			for (int i = 0; i < text.Length; i++)
			{
				char c = text[i];
				if (quoted)
				{
					if (c == '"')
					{
						if (i + 1 < text.Length && text[i + 1] == '"')
						{
							field.Append('"');
							i++;
						}
						else
						{
							quoted = false;
						}
					}
					else
					{
						field.Append(c);
					}
				}
				else if (c == '"' && field.Length == 0)
				{
					quoted = true;
				}
				else if (c == '\t')
				{
					row.Add(field.ToString());
					field.Clear();
				}
				else if (c == '\n')
				{
					row.Add(field.ToString().TrimEnd('\r'));
					field.Clear();
					yield return row.ToArray();
					row.Clear();
				}
				else
				{
					field.Append(c);
				}
			}
			if (field.Length > 0 || row.Count > 0)
			{
				row.Add(field.ToString());
				yield return row.ToArray();
			}
		}
	}
}
