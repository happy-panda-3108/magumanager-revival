using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	// League averages of one season, for era adjustment.
	public sealed class EraLine
	{
		public int Year;
		public double Avg, Obp, Slg, Era, Whip, K, BB;
	}

	// Era adjustment as decided with the user: a stat is scaled by how its league average
	// compares with the all-season reference (power by SLG, contact by AVG, on-base by OBP,
	// pitchers by ERA and WHIP). A 1999 slugger in a .400 SLG league is worth 0.33/0.40 of his
	// raw power in a .330 league.
	public sealed class EraTable
	{
		public readonly Dictionary<int, EraLine> Seasons = new Dictionary<int, EraLine>();
		public EraLine Reference;

		public static EraTable Build(IEnumerable<SeasonFit> fits)
		{
			EraTable e = new EraTable();
			foreach (SeasonFit f in fits)
			{
				Season s = f.Model.Season;
				List<BatterLine> bs = s.Batters.Where(b => !b.Replacement).ToList();
				double pa = bs.Sum(b => b.PA), ab = bs.Sum(b => b.AB), h = bs.Sum(b => b.H), d = bs.Sum(b => b.D), t = bs.Sum(b => b.T), hr = bs.Sum(b => b.HR);
				double bb = bs.Sum(b => b.BB), hbp = bs.Sum(b => b.HBP), so = bs.Sum(b => b.SO);
				// Pitching totals include the replacement arms that hold the unrecorded innings.
				List<PitcherLine> ps = s.Pitchers.ToList();
				double outs = ps.Sum(p => p.Outs), er = ps.Sum(p => p.ER), ph = ps.Sum(p => p.H), pbb = ps.Sum(p => p.BB);
				e.Seasons[s.Year] = new EraLine
				{
					Year = s.Year, Avg = h / ab, Obp = (h + bb + hbp) / pa, Slg = (h + d + 2 * t + 3 * hr) / ab,
					Era = er * 27 / outs, Whip = (ph + pbb) * 3 / outs, K = so / pa, BB = bb / pa
				};
			}
			List<EraLine> all = e.Seasons.Values.ToList();
			e.Reference = new EraLine
			{
				Avg = all.Average(x => x.Avg), Obp = all.Average(x => x.Obp), Slg = all.Average(x => x.Slg),
				Era = all.Average(x => x.Era), Whip = all.Average(x => x.Whip), K = all.Average(x => x.K), BB = all.Average(x => x.BB)
			};
			return e;
		}

		public double AvgRatio(int year) => Reference.Avg / Seasons[year].Avg;
		public double ObpRatio(int year) => Reference.Obp / Seasons[year].Obp;
		public double SlgRatio(int year) => Reference.Slg / Seasons[year].Slg;
		public double EraRatio(int year) => Reference.Era / Seasons[year].Era;
		public double WhipRatio(int year) => Reference.Whip / Seasons[year].Whip;
		public double KRatio(int year) => Reference.K / Seasons[year].K;
		public double BBRatio(int year) => Reference.BB / Seasons[year].BB;
	}

	// Fielding as decided with the user: fielding percentage, trusted in proportion to the
	// time spent at the position, so only a sure-handed regular rates highly and a utility
	// man spread over several spots is held back. The records carry fielding percentage from
	// 2017 only, and games (not innings per position), so games stand in for innings.
	public sealed class DefenceScore
	{
		public int Year, Sidx, Team, Position, G;
		public string Name;
		public double Fpct, Credible, Score;

		// Games of evidence that count as much as the "unproven" prior.
		public const double PriorGames = 80;
		// Unproven fielders are assumed one standard deviation below the position's average.
		public const double PriorGapSd = 1.0;

		public static List<DefenceScore> Build(IEnumerable<SeasonFit> fits, CardData cards)
		{
			List<DefenceScore> result = new List<DefenceScore>();
			foreach (SeasonFit f in fits)
			{
				Season s = f.Model.Season;
				List<DefenceScore> rows = new List<DefenceScore>();
				foreach (BatterLine b in s.Batters.Where(b => !b.Replacement && !double.IsNaN(b.Fpct) && b.G > 0))
				{
					int pos = cards.BatterPosition.TryGetValue((b.Sidx, b.Year), out int p) ? p : 0;
					if (pos < 2 || pos > 9)
					{
						continue; // designated hitters and unknowns
					}
					rows.Add(new DefenceScore { Year = s.Year, Sidx = b.Sidx, Team = b.Team, Name = b.Name, Position = pos, G = b.G, Fpct = b.Fpct });
				}
				foreach (var byPos in rows.GroupBy(r => r.Position))
				{
					double lg = byPos.Sum(r => r.Fpct * r.G) / byPos.Sum(r => r.G);
					double sd = Math.Sqrt(byPos.Sum(r => r.G * (r.Fpct - lg) * (r.Fpct - lg)) / byPos.Sum(r => r.G));
					foreach (DefenceScore r in byPos)
					{
						r.Credible = (r.Fpct * r.G + (lg - PriorGapSd * sd) * PriorGames) / (r.G + PriorGames);
						r.Score = (r.Credible - lg) / Math.Max(0.002, sd);
					}
				}
				result.AddRange(rows);
			}
			return result;
		}

		public static string PositionName(int pos)
		{
			return pos switch { 2 => "포수", 3 => "1루수", 4 => "2루수", 5 => "3루수", 6 => "유격수", 7 => "좌익수", 8 => "중견수", 9 => "우익수", _ => "?" };
		}
	}
}
