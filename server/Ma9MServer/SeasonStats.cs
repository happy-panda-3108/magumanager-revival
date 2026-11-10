using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;

namespace Ma9MServer
{
	// A card's league season line (LeagueStore.PlayerSeason) in the player window's record fields,
	// plus WAR and wRC+ against this league's own season so far (user's request, 2026-10-18:
	// "상세기록에 war, wrc+도 추가, 투수는 bwar만").
	//
	// Batters (FanGraphs-style, no park or fielding term — the engine keeps no per-player fielding):
	//   wOBA = (.69 BB + .72 HBP + .89 1B + 1.27 2B + 1.62 3B + 2.10 HR) / (AB + BB + SF + HBP)
	//   wRAA = (wOBA − league wOBA) / 1.2 × PA;  wRC+ = 100 × (wRAA/PA + league R/PA) / (league R/PA)
	//   WAR  = (wRAA + baserunning + position + replacement) / runs per win, with baserunning
	//          .2 SB − .4 CS, position per 600 PA (C +12.5, SS +7.5, 2B/3B/CF +2.5, LF/RF −7.5,
	//          1B −12.5, DH −17.5) and replacement 20 runs per 600 PA.
	// Pitchers (Baseball-Reference-style bWAR, from runs allowed):
	//   bWAR = (replacement RA9 − RA9) × IP/9 / runs per win; replacement RA9 = league RA9 × 1.2
	//   for starters (half their games started or more) and × 1.1 for relievers.
	// Runs per win = 1.5 × league runs per team-game + 3 (about 10 at 5 runs).
	public static class SeasonStats
	{
		public sealed class LeagueContext
		{
			public double WOba, RunsPerPa, Ra9, RunsPerWin;
		}

		private static readonly Dictionary<(uint, int), (int games, LeagueContext ctx)> s_cache = new Dictionary<(uint, int), (int, LeagueContext)>();

		public static void FillBatter(BATTER_RECORD r, PlayerSeason p, int idx)
		{
			int singles = p.H - p.D - p.T - p.HR;
			int tb = singles + 2 * p.D + 3 * p.T + 4 * p.HR;
			double obpDen = p.AB + p.BB + p.HBP + p.SF;
			double avg = p.AB > 0 ? (double)p.H / p.AB : 0;
			double obp = obpDen > 0 ? (p.H + p.BB + p.HBP) / obpDen : 0;
			double slg = p.AB > 0 ? (double)tb / p.AB : 0;
			r.index.n = idx;
			r.games.n = B(p.G);
			r.batting_average.f = (float)avg;
			r.PA.n = S(p.PA);
			r.AB.n = S(p.AB);
			r.hits.n = B(p.H);
			r.RBI.n = B(p.RBI);
			r.runs.n = B(p.R);
			r.homeruns.n = B(p.HR);
			r.errors.n = 0;
			r.singles.n = B(singles);
			r.doubles.n = B(p.D);
			r.triples.n = B(p.T);
			r.K.n = B(p.SO);
			r.BB.n = B(p.BB);
			r.SB.n = B(p.SB);
			r.CS.n = B(p.CS);
			r.sacrifice.n = B(p.SF + p.SH);
			r.DP.n = B(p.GIDP);
			r.OBP.f = (float)obp;
			r.SA.f = (float)slg;
			r.OPS.f = (float)(obp + slg);
			// 득점권 찬스 / 득점권 타율 (user, 2026-10-21: they stayed 0): plate appearances that began with a
			// runner on 2nd or 3rd, and the hits per at-bat in them (engine SimStats.Risp, kept since then).
			r.SP.n = B(p.RispPA);
			r.SPBA.f = p.RispAB > 0 ? (float)p.RispH / p.RispAB : 0f;
		}

		public static void FillPitcher(PITCHER_RECORD r, PlayerSeason p, int idx)
		{
			double ip = p.Outs / 3.0;
			r.index.n = idx;
			r.ERA.f = p.Outs > 0 ? (float)(p.ER * 27.0 / p.Outs) : 0f;
			r.games.n = B(p.G);
			r.innings_by3.n = S(p.Outs);
			r.wins.n = B(p.W);
			r.loses.n = B(p.L);
			r.saves.n = B(p.SV);
			r.holds.n = B(p.HLD);
			r.BS.n = B(p.BS);
			r.runs.n = S(p.PR);
			r.hits.n = S(p.PH);
			r.HR.n = B(p.PHR);
			r.K.n = S(p.PSO);
			r.BB.n = S(p.PBB);
			r.KperB.f = p.PBB > 0 ? (float)p.PSO / p.PBB : p.PSO;
			r.CG.n = B(p.CG);
			// 완봉 (shown as 완투(봉) "CG(SHO)"), 병살 유도 and 피장타율 (user, 2026-10-30: the shutouts stayed 0, 피장타율 0.000).
			// 피장타율 is over the games since then (PlayerSeason.PAB/PTB).
			r.shutout.n = B(p.SHO);
			r.DP.n = S(p.PDP);
			r.SA.f = p.PAB > 0 ? (float)p.PTB / p.PAB : 0f;
			r.QS.n = B(p.QS);
			int abAgainst = p.BF - p.PBB - p.PHBP;
			r.HperAB.f = abAgainst > 0 ? (float)p.PH / abAgainst : 0f;
			r.WHIP.f = ip > 0 ? (float)((p.PH + p.PBB) / ip) : 0f;
			r.KperInnings.f = ip > 0 ? (float)(p.PSO / ip) : 0f;
		}

		// WAR (bWAR for pitchers) and wRC+ (batters; 0 for pitchers). NaN when there is nothing yet.
		public static (double war, int wrcPlus) Value(PlayerSeason p, CardPos mainPos, LeagueContext lg)
		{
			if (lg == null)
			{
				return (double.NaN, 0);
			}
			if (p.Pitcher)
			{
				if (p.Outs <= 0)
				{
					return (double.NaN, 0);
				}
				double ra9 = p.PR * 27.0 / p.Outs;
				bool starter = p.GS * 2 >= p.G;
				double rep = lg.Ra9 * (starter ? 1.2 : 1.1);
				return ((rep - ra9) * p.Outs / 27.0 / lg.RunsPerWin, 0);
			}
			if (p.PA <= 0)
			{
				return (double.NaN, 0);
			}
			double wraa = (WOba(p) - lg.WOba) / 1.2 * p.PA;
			int wrc = (int)Math.Round(100 * (wraa / p.PA + lg.RunsPerPa) / lg.RunsPerPa);
			double baserunning = 0.2 * p.SB - 0.4 * p.CS;
			double position = PositionRuns(mainPos) * p.PA / 600.0;
			double replacement = 20.0 * p.PA / 600.0;
			return ((wraa + baserunning + position + replacement) / lg.RunsPerWin, wrc);
		}

		// The league's own season so far (every club's cards), cached until the next game.
		public static LeagueContext Context(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			return s == null ? null : Context(user, s.Season, s.GamesPlayed);
		}

		// A given season of the user's league (LeagueAwards reads the season that just ended).
		public static LeagueContext Context(uint user, int season, int played)
		{
			if (LeagueService.Store == null)
			{
				return null;
			}
			if (s_cache.TryGetValue((user, season), out var hit) && hit.games == played)
			{
				return hit.ctx;
			}
			List<PlayerSeason> lines = LeagueService.Store.PlayerLines(user, season);
			List<PlayerSeason> bats = lines.Where(l => !l.Pitcher).ToList();
			List<PlayerSeason> arms = lines.Where(l => l.Pitcher).ToList();
			double pa = bats.Sum(l => l.PA), runs = bats.Sum(l => l.R), outs = arms.Sum(l => l.Outs), allowed = arms.Sum(l => l.PR);
			if (pa <= 0 || outs <= 0)
			{
				return null;
			}
			PlayerSeason total = new PlayerSeason();
			foreach (PlayerSeason l in bats)
			{
				total.Add(l);
			}
			double teamGames = Math.Max(1, outs / 27.0);
			LeagueContext ctx = new LeagueContext
			{
				WOba = WOba(total),
				RunsPerPa = runs / pa,
				Ra9 = allowed * 27.0 / outs,
				RunsPerWin = 1.5 * (allowed / teamGames) + 3
			};
			s_cache[(user, season)] = (played, ctx);
			return ctx;
		}

		// "--wartest <user>": the team's season lines with WAR and wRC+, for checking the formulas.
		public static int Report(uint user, CardCatalog catalog)
		{
			LeagueContext lg = Context(user);
			LeagueState s = LeagueService.Store?.Load(user);
			if (lg == null || s == null)
			{
				ServerLog.Info($"wartest: no league season for {user}");
				return 1;
			}
			ServerLog.Info($"wartest: league wOBA {lg.WOba:F3}, R/PA {lg.RunsPerPa:F3}, RA9 {lg.Ra9:F2}, runs per win {lg.RunsPerWin:F1}");
			foreach (PlayerSeason p in LeagueService.Store.PlayerLines(user, s.Season, s.MyIndex).OrderBy(l => l.Pitcher).ThenByDescending(l => l.Pitcher ? l.Outs : l.PA))
			{
				CardInfo c = catalog.Get(p.CardIdx);
				(double war, int wrc) = Value(p, c?.MainPosition ?? CardPos.DH, lg);
				ServerLog.Info(p.Pitcher
					? $"  P {c?.Name,-6} G {p.G} IP {p.Outs / 3}.{p.Outs % 3} R {p.PR} ER {p.ER} K {p.PSO} BB {p.PBB} → bWAR {war:F2}"
					: $"  B {c?.Name,-6} PA {p.PA} H {p.H} 2B {p.D} 3B {p.T} HR {p.HR} BB {p.BB} SO {p.SO} SB {p.SB} → wRC+ {wrc}, WAR {war:F2}");
			}
			return 0;
		}

		private static double WOba(PlayerSeason p)
		{
			double den = p.AB + p.BB + p.SF + p.HBP;
			if (den <= 0)
			{
				return 0;
			}
			int singles = p.H - p.D - p.T - p.HR;
			return (0.69 * p.BB + 0.72 * p.HBP + 0.89 * singles + 1.27 * p.D + 1.62 * p.T + 2.10 * p.HR) / den;
		}

		private static double PositionRuns(CardPos pos) => pos switch
		{
			CardPos.C => 12.5,
			CardPos.SS => 7.5,
			CardPos.B2 or CardPos.B3 or CardPos.CF => 2.5,
			CardPos.LF or CardPos.RF => -7.5,
			CardPos.B1 => -12.5,
			_ => -17.5
		};

		private static byte B(int v) => (byte)Math.Clamp(v, 0, 255);

		private static short S(int v) => (short)Math.Clamp(v, 0, short.MaxValue);
	}
}
