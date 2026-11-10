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
		// Statiz season line (statiz/표/타격_합본_전체.tsv, batters with 10+ G): his number, his main position that season
		public string PNo, StatizPos;
		public bool FromStatiz;

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
		public int GS; // estimated: the records have no games-started column (real from Statiz, see Records.StatizPitching)
		public bool Replacement;
		// Statiz season line (statiz/표/투구_합본_전체.tsv, pitchers with 10+ G): the player's number, real starts and the
		public string PNo;
		public bool RealGS, FromStatiz;
		public int StartOuts;

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
					Sidx = sidx, Name = NameOf('B', sidx, r[1]), Year = year, Team = team,
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
					Sidx = sidx, Name = NameOf('P', sidx, r[1]), Year = year, Team = team,
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

			PartSidxB.Clear();
			PartSidxP.Clear();
			if (StatizPitching != null && StatizBatting != null)
			{
				foreach (int y in StatizOnlyYears)
				{
					if (!seasons.TryGetValue(y, out Season part))
					{
						continue;
					}
					foreach (TeamSeason t in part.Teams)
					{
						foreach (BatterLine b in t.Batters) AddPart(PartSidxB, y, t.Code, b.Name, b.Sidx);
						foreach (PitcherLine p in t.Pitchers) AddPart(PartSidxP, y, t.Code, p.Name, p.Sidx);
					}
					seasons.Remove(y);
				}
				HashSet<int> recorded = new HashSet<int>(seasons.Keys);
				foreach (string path in new[] { StatizBatting, StatizPitching })
				{
					string[] head = null;
					int cy = -1, ct = -1;
					foreach (string line in File.ReadLines(path, Encoding.UTF8))
					{
						string[] c = line.Split('\t');
						if (head == null)
						{
							head = c;
							cy = Array.IndexOf(head, "연도");
							ct = Array.IndexOf(head, "team_code");
							continue;
						}
						if (cy >= 0 && ct >= 0 && c.Length > Math.Max(cy, ct) && int.TryParse(c[cy], out int y) && y >= firstYear && y <= lastYear
							&& !recorded.Contains(y) && StatizTeam.TryGetValue(c[ct], out int code))
						{
							TeamOf(SeasonOf(y), code);
						}
					}
				}
			}
			if (StatizPitching != null)
			{
				MergeStatizPitching(seasons, StatizPitching);
			}
			if (StatizBatting != null)
			{
				MergeStatizBatting(seasons, StatizBatting);
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
		// starts left over go to the team's replacement arm (or its longest relievers). Statiz lines
		// carry the real starts; only the others (pitchers under 10 G) are estimated, in the room left.
		private static void EstimateStarts(Season s, TeamSeason t)
		{
			// Saves and holds are relief outings, and even regular starters relieve now and then.
			List<PitcherLine> starters = t.Pitchers.Where(p => p.Starter && !p.Replacement && !p.RealGS).ToList();
			int known = t.Pitchers.Where(p => p.RealGS).Sum(p => p.GS);
			int room = Math.Max(0, s.Games - known);
			foreach (PitcherLine p in starters)
			{
				int relief = p.SV + p.HLD + (int)Math.Round(Math.Max(0, p.G - p.SV - p.HLD) * 0.05);
				p.GS = Math.Max(1, p.G - relief);
			}
			int claimed = starters.Sum(p => p.GS);
			double scale = claimed > room ? room / (double)claimed : 1.0;
			foreach (PitcherLine p in starters)
			{
				p.GS = Math.Max(room > 0 ? 1 : 0, (int)Math.Round(p.GS * scale));
				p.Starter = p.GS > 0;
			}
			int remaining = s.Games - known - starters.Sum(p => p.GS);
			PitcherLine repl = t.Pitchers.FirstOrDefault(p => p.Replacement);
			if (remaining > 0 && repl != null)
			{
				repl.Starter = true;
				repl.GS = remaining;
				repl.G = Math.Max(repl.G, remaining);
			}
			else if (remaining > 0)
			{
				// Pitchers whose starts are real keep them unless nobody else is left.
				foreach (PitcherLine p in t.Pitchers.Where(p => !p.Starter).OrderBy(p => p.RealGS).ThenByDescending(p => p.Outs / (double)Math.Max(1, p.G)))
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

		// ---- Statiz pitching (engine session, 2026-11-01) ----
		// statiz/표/투구_합본_전체.tsv: every pitcher with 10+ G, 1999-2026, from pages the user saved by hand. Where both
		// started and lacks 15-18% of the innings in 1999-2005 (5-10% later): 969 pitcher-seasons of 10+ G, 26,581 IP.
		// With this set, every Statiz line replaces or joins the season: real starts and innings per start, and the
		// missing pitchers themselves, so the replacement arm holds only what pitchers under 10 G left unrecorded.
		public static string StatizPitching;
		public static int StatizLinked, StatizNameLinked, StatizAdded;

		// files on, Statiz's lines alone make the season. A player new to the run takes the number his part-season line
		// carried (same club and name, one such line), else one of his own.
		public static readonly HashSet<int> StatizOnlyYears = new HashSet<int> { 2026 };
		private static readonly Dictionary<(int, int, string), int> PartSidxB = new Dictionary<(int, int, string), int>(), PartSidxP = new Dictionary<(int, int, string), int>();

		private static void AddPart(Dictionary<(int, int, string), int> map, int year, int team, string name, int sidx)
		{
			(int, int, string) k = (year, team, Norm(name));
			map[k] = map.TryGetValue(k, out int had) && had != sidx ? -1 : sidx;
		}

		private static int PartSidx(Dictionary<(int, int, string), int> map, int year, int team, string name) =>
			map.TryGetValue((year, team, Norm(name)), out int s) ? s : -1;

		// Statiz logo codes -> our club codes.
		private static readonly Dictionary<string, int> StatizTeam = new Dictionary<string, int>
		{
			{ "1001", 2 }, { "2001", 9 }, { "2002", 4 }, { "3001", 8 }, { "4004", 1 }, { "5002", 6 }, { "6002", 3 }, { "7002", 7 },
			{ "8001", 13 }, { "9001", 5 }, { "9002", 26 }, { "10001", 17 }, { "11001", 24 }, { "12001", 25 },
			// 1982-1998 clubs (2026-11-02: without them their lines neither linked by name nor joined): 삼미, 청보, 태평양, MBC, OB, 빙그레.
			{ "4001", 16 }, { "4002", 15 }, { "4003", 12 }, { "5001", 11 }, { "6001", 14 }, { "7001", 10 }
		};

		public const int StatizSidxBase = 5000000;

		private static readonly Dictionary<char, char> Initial = new Dictionary<char, char>
		{
			{ '류', '유' }, { '리', '이' }, { '림', '임' }, { '라', '나' }, { '로', '노' }, { '룡', '용' }, { '량', '양' }, { '려', '여' }, { '련', '연' },
			{ '렬', '열' }, { '례', '예' }, { '뢰', '뇌' }, { '루', '누' }, { '르', '느' }, { '란', '난' }, { '랑', '낭' }, { '래', '내' }, { '롱', '농' }
		};

		// Names compared with the 두음법칙 initials folded (류지현 = 유지현), spaces and dots dropped.
		private static string Norm(string name)
		{
			string n = name.Replace(" ", "").Replace(".", "").Trim();
			return n.Length > 0 && Initial.TryGetValue(n[0], out char c) ? c + n.Substring(1) : n;
		}

		private static void MergeStatizPitching(Dictionary<int, Season> seasons, string path)
		{
			StatizLinked = StatizNameLinked = StatizAdded = 0;
			string[] head = null;
			List<string[]> rows = new List<string[]>();
			foreach (string line in File.ReadLines(path, Encoding.UTF8))
			{
				string[] c = line.Split('\t');
				if (head == null)
				{
					head = c;
					continue;
				}
				rows.Add(c);
			}
			int Col(string n)
			{
				int i = Array.IndexOf(head, n);
				if (i < 0) throw new InvalidDataException($"{path}: no column {n}");
				return i;
			}
			int cYear = Col("연도"), cPno = Col("p_no"), cName = Col("Name"), cTeam = Col("team_code"), cG = Col("G"), cGS = Col("GS"), cIP = Col("IP"),
				cH = Col("H"), cHR = Col("HR"), cBB = Col("BB"), cSO = Col("SO"), cHP = Col("HP"), cER = Col("ER"), cW = Col("W"), cL = Col("L"),
				cS = Col("S"), cHD = Col("HD"), cSGS = Col("선발 성적 GS"), cSIP = Col("선발 성적 IP");

			void Apply(PitcherLine p, string[] r)
			{
				p.PNo = r[cPno];
				p.G = I(r[cG]);
				p.Outs = Outs(r[cIP]);
				p.H = I(r[cH]);
				p.HR = I(r[cHR]);
				p.BB = I(r[cBB]);
				p.SO = I(r[cSO]);
				p.HBP = I(r[cHP]);
				p.ER = I(r[cER]);
				p.W = I(r[cW]);
				p.L = I(r[cL]);
				p.SV = I(r[cS]);
				p.HLD = I(r[cHD]);
				p.GS = I(r[cGS]);
				p.RealGS = true;
				p.Starter = p.GS > 0;
				// Outs in his starts (선발 성적): the start tab's count, which matches 기본 GS.
				p.StartOuts = I(r[cSGS]) == p.GS ? Outs(r[cSIP]) : 0;
			}

			// 1. The same line: season, outs, strikeouts, walks, hits and homers (unique in both sources).
			Dictionary<(int, int, int, int, int, int), PitcherLine> bySig = new Dictionary<(int, int, int, int, int, int), PitcherLine>();
			HashSet<(int, int, int, int, int, int)> twice = new HashSet<(int, int, int, int, int, int)>();
			foreach (Season s in seasons.Values)
			{
				foreach (PitcherLine p in s.Pitchers)
				{
					(int, int, int, int, int, int) k = (p.Year, p.Outs, p.SO, p.BB, p.H, p.HR);
					if (!bySig.TryAdd(k, p))
					{
						twice.Add(k);
					}
				}
			}
			Dictionary<string, int> pnoSidx = new Dictionary<string, int>();
			List<string[]> left = new List<string[]>();
			foreach (string[] r in rows)
			{
				if (!int.TryParse(r[cYear], out int year) || !seasons.ContainsKey(year))
				{
					continue;
				}
				(int, int, int, int, int, int) k = (year, Outs(r[cIP]), I(r[cSO]), I(r[cBB]), I(r[cH]), I(r[cHR]));
				if (!twice.Contains(k) && bySig.TryGetValue(k, out PitcherLine p) && p.PNo == null)
				{
					Apply(p, r);
					pnoSidx[r[cPno]] = p.Sidx;
					StatizLinked++;
				}
				else
				{
					left.Add(r);
				}
			}
			// 2. Same season, club and name, innings within 10%: lines that differ a little (59 of them).
			List<string[]> missing = new List<string[]>();
			foreach (string[] r in left)
			{
				int year = int.Parse(r[cYear]);
				int team = StatizTeam.TryGetValue(r[cTeam], out int t) ? t : -1;
				int outs = Outs(r[cIP]);
				string name = Norm(r[cName]);
				List<PitcherLine> c = seasons[year].Pitchers.Where(p => p.PNo == null && Norm(p.Name) == name
					&& (p.Team == team || (year == 2001 && (p.Team == 4 || p.Team == 9) && (team == 4 || team == 9)))
					&& Math.Abs(p.Outs - outs) <= Math.Max(3, 0.1 * outs)).ToList();
				if (c.Count == 1)
				{
					Apply(c[0], r);
					pnoSidx[r[cPno]] = c[0].Sidx;
					StatizNameLinked++;
				}
				else
				{
					missing.Add(r);
				}
			}
			foreach (string[] r in missing)
			{
				int year = int.Parse(r[cYear]);
				Season s = seasons[year];
				if (!StatizTeam.TryGetValue(r[cTeam], out int team))
				{
					continue;
				}
				TeamSeason ts = s.Teams.FirstOrDefault(x => x.Code == team)
					?? (year == 2001 ? s.Teams.FirstOrDefault(x => x.Code == (team == 9 ? 4 : team == 4 ? 9 : -1)) : null);
				if (ts == null)
				{
					continue;
				}
				int part = PartSidx(PartSidxP, year, ts.Code, r[cName]);
				int sidx = pnoSidx.TryGetValue(r[cPno], out int known) && !s.Pitchers.Any(x => x.Sidx == known) ? known
					: part > 0 && !s.Pitchers.Any(x => x.Sidx == part) ? part : StatizSidxBase + int.Parse(r[cPno]);
				PitcherLine p = new PitcherLine { Sidx = sidx, Name = r[cName].Trim(), Year = year, Team = ts.Code, FromStatiz = true };
				Apply(p, r);
				if (p.Outs <= 0)
				{
					continue;
				}
				ts.Pitchers.Add(p);
				StatizAdded++;
			}
		}

		// ---- Statiz batting (engine session, 2026-11-01 night; user: the stat rework once all batting data is in) ----
		// lines are Statiz's give or take one PA or hit, except 9 namesake lines that carry the other player's PA (2009 LG
		// 김태완: 446 PA at 33 AB) and 1,442 seasons of 10+ G it lacks (60,058 PA, 2-9% of a season). Every Statiz line
		// replaces or joins the season, as with pitching.
		public static string StatizBatting;
		public static int StatizBatLinked, StatizBatNameLinked, StatizBatAdded;

		private static void MergeStatizBatting(Dictionary<int, Season> seasons, string path)
		{
			StatizBatLinked = StatizBatNameLinked = StatizBatAdded = 0;
			string[] head = null;
			List<string[]> rows = new List<string[]>();
			foreach (string line in File.ReadLines(path, Encoding.UTF8))
			{
				string[] c = line.Split('\t');
				if (head == null)
				{
					head = c;
					continue;
				}
				rows.Add(c);
			}
			int Col(string n)
			{
				int i = Array.IndexOf(head, n);
				if (i < 0) throw new InvalidDataException($"{path}: no column {n}");
				return i;
			}
			int cYear = Col("연도"), cPno = Col("p_no"), cName = Col("Name"), cTeam = Col("team_code"), cPos = Col("pos"), cG = Col("G"),
				cPA = Col("PA"), cAB = Col("AB"), cR = Col("R"), cH = Col("H"), cD = Col("2B"), cT = Col("3B"), cHR = Col("HR"), cRBI = Col("RBI"),
				cSB = Col("SB"), cBB = Col("BB"), cHP = Col("HP"), cSO = Col("SO");

			void Apply(BatterLine b, string[] r)
			{
				b.PNo = r[cPno];
				b.StatizPos = r[cPos].Trim();
				b.G = I(r[cG]);
				b.PA = I(r[cPA]);
				b.AB = I(r[cAB]);
				b.R = I(r[cR]);
				b.H = I(r[cH]);
				b.D = I(r[cD]);
				b.T = I(r[cT]);
				b.HR = I(r[cHR]);
				b.RBI = I(r[cRBI]);
				b.SB = I(r[cSB]);
				b.BB = I(r[cBB]);
				b.HBP = I(r[cHP]);
				b.SO = I(r[cSO]);
			}

			// 1. The same line without its PA (9 lines carry another player's): season, AB, hits, homers, strikeouts, walks.
			Dictionary<(int, int, int, int, int, int), BatterLine> bySig = new Dictionary<(int, int, int, int, int, int), BatterLine>();
			HashSet<(int, int, int, int, int, int)> twice = new HashSet<(int, int, int, int, int, int)>();
			foreach (Season s in seasons.Values)
			{
				foreach (BatterLine b in s.Batters)
				{
					(int, int, int, int, int, int) k = (b.Year, b.AB, b.H, b.HR, b.SO, b.BB);
					if (!bySig.TryAdd(k, b))
					{
						twice.Add(k);
					}
				}
			}
			Dictionary<string, int> pnoSidx = new Dictionary<string, int>();
			List<string[]> left = new List<string[]>();
			foreach (string[] r in rows)
			{
				if (!int.TryParse(r[cYear], out int year) || !seasons.ContainsKey(year))
				{
					continue;
				}
				(int, int, int, int, int, int) k = (year, I(r[cAB]), I(r[cH]), I(r[cHR]), I(r[cSO]), I(r[cBB]));
				if (!twice.Contains(k) && bySig.TryGetValue(k, out BatterLine b) && b.PNo == null)
				{
					Apply(b, r);
					pnoSidx[r[cPno]] = b.Sidx;
					StatizBatLinked++;
				}
				else
				{
					left.Add(r);
				}
			}
			// 2. Same season, club and name, PA or AB close: lines that differ by a PA or a hit (2019 로하스 576 / 578).
			List<string[]> missing = new List<string[]>();
			foreach (string[] r in left)
			{
				int year = int.Parse(r[cYear]);
				int team = StatizTeam.TryGetValue(r[cTeam], out int t) ? t : -1;
				int pa = I(r[cPA]), ab = I(r[cAB]);
				string name = Norm(r[cName]);
				List<BatterLine> c = seasons[year].Batters.Where(b => b.PNo == null && Norm(b.Name) == name
					&& (b.Team == team || (year == 2001 && (b.Team == 4 || b.Team == 9) && (team == 4 || team == 9)))
					&& (Math.Abs(b.PA - pa) <= Math.Max(5, 0.1 * pa) || Math.Abs(b.AB - ab) <= Math.Max(3, 0.05 * ab))).ToList();
				if (c.Count == 1)
				{
					Apply(c[0], r);
					pnoSidx[r[cPno]] = c[0].Sidx;
					StatizBatNameLinked++;
				}
				else
				{
					missing.Add(r);
				}
			}
			foreach (string[] r in missing)
			{
				int year = int.Parse(r[cYear]);
				Season s = seasons[year];
				if (!StatizTeam.TryGetValue(r[cTeam], out int team))
				{
					continue;
				}
				TeamSeason ts = s.Teams.FirstOrDefault(x => x.Code == team)
					?? (year == 2001 ? s.Teams.FirstOrDefault(x => x.Code == (team == 9 ? 4 : team == 4 ? 9 : -1)) : null);
				if (ts == null)
				{
					continue;
				}
				int part = PartSidx(PartSidxB, year, ts.Code, r[cName]);
				int sidx = pnoSidx.TryGetValue(r[cPno], out int known) && !s.Batters.Any(x => x.Sidx == known) ? known
					: part > 0 && !s.Batters.Any(x => x.Sidx == part) ? part : StatizSidxBase + int.Parse(r[cPno]);
				BatterLine b = new BatterLine { Sidx = sidx, Name = r[cName].Trim(), Year = year, Team = ts.Code, FromStatiz = true };
				Apply(b, r);
				if (b.PA <= 0)
				{
					continue;
				}
				ts.Batters.Add(b);
				StatizBatAdded++;
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

		// sidx can carry another player in other seasons (2026-10-20; it was sidx alone before).
		public static readonly Dictionary<(char kind, int sidx, string name), string> RealNames = new Dictionary<(char, int, string), string>();

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
					string kinds = c.Length >= 5 && c[4].Trim().Length > 0 ? c[4].Trim() : "BP";
					foreach (char kind in kinds)
					{
						RealNames[(kind, sidx, c[1].Trim())] = c[2].Trim();
					}
				}
			}
		}

		private static string NameOf(char kind, int sidx, string raw)
		{
			string name = CleanName(raw);
			return RealNames.TryGetValue((kind, sidx, name), out string real) ? real : name;
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
