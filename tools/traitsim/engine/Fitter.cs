using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	// Fits player talents (and the season's run environment) so the simulated season
	// reproduces the real one: simulate, compare each player's rates with his record,
	// nudge the talent toward the gap, repeat.
	public sealed class SeasonFit
	{
		public SeasonModel Model;
		public EngineParams Params;
		public SimStats Validation;
		public SimStats Neutral;     // same talents, every game in an average park
		public int ValidationReps;
		public List<string> Progress = new List<string>();
	}

	public static class Fitter
	{
		private static double Eta = 0.8;

		public static SeasonFit Fit(Season season, EngineParams baseParams, int iterations, int reps, int validationReps, ulong seed,
			ParkModel parks = null, CardData cards = null, MatchupParams mp = null)
		{
			SeasonFit fit = new SeasonFit { Model = SeasonModel.Build(season, parks, cards, mp), Params = baseParams.Clone() };
			SeasonModel m = fit.Model;
			double realRpg = RealRunsPerGame(m);
			double realRpPa = RealRunsPerPa(m);
			int averageFrom = iterations * 2 / 3;
			TalentAverage avg = new TalentAverage(m);
			double envSum = 0, roeSum = 0;
			for (int it = 0; it < iterations; it++)
			{
				// Big steps first, small steps (and more simulated seasons) at the end.
				double progress = iterations > 1 ? it / (double)(iterations - 1) : 1;
				Eta = 0.8 - 0.55 * progress;
				int r = it < averageFrom ? reps : reps * 2;
				SimStats agg = Run(m, fit.Params, r, seed + (ulong)it * 1000);
				double simRpg = agg.Runs / (double)(agg.Games * 2);
				double simRpPa = agg.Runs / (double)TotalPa(m, agg);
				UpdateBatters(m, agg, r);
				UpdatePitchers(m, agg, r);
				Normalize(m);
				// Runs per PA, not per game: records miss some batters, which lowers real runs per game.
				fit.Params.RunEnvironment = Math.Clamp(fit.Params.RunEnvironment * Math.Pow(realRpPa / simRpPa, 0.4), 0.5, 1.8);
				// Unearned runs come from errors: move the error rate until the earned share fits.
				double simEr = 0, simR = 0;
				for (int i = 0; i < m.Pitchers.Length; i++)
				{
					simEr += agg.Pit(i, PS.ER);
					simR += agg.Pit(i, PS.R);
				}
				if (simR > 0)
				{
					double unearned = Math.Max(0.01, 1 - simEr / simR);
					fit.Params.ReachOnError = Math.Clamp(fit.Params.ReachOnError * Math.Pow((1 - fit.Params.EarnedShare) / unearned, 0.6), 0.015, 0.09);
				}
				if (it >= averageFrom)
				{
					avg.Add(m);
					envSum += fit.Params.RunEnvironment;
					roeSum += fit.Params.ReachOnError;
				}
				fit.Progress.Add($"{season.Year} it{it:00} R/PA real {realRpPa:F4} sim {simRpPa:F4} R/G sim {simRpg:F2} runEnv {fit.Params.RunEnvironment:F3} roe {fit.Params.ReachOnError:F3} batErr {BatterError(m, agg, r):F4} pitErr {PitcherError(m, agg, r):F4}");
			}
			avg.Apply(m);
			fit.Params.RunEnvironment = envSum / Math.Max(1, iterations - averageFrom);
			fit.Params.ReachOnError = roeSum / Math.Max(1, iterations - averageFrom);
			fit.Validation = Run(m, fit.Params, validationReps, seed + 999_999);
			fit.ValidationReps = validationReps;
			m.NeutralParks = true;
			fit.Neutral = Run(m, fit.Params, validationReps, seed + 777_777);
			m.NeutralParks = false;
			double vRpg = fit.Validation.Runs / (double)(fit.Validation.Games * 2);
			double vRpPa = fit.Validation.Runs / (double)TotalPa(m, fit.Validation);
			fit.Progress.Add($"{season.Year} final R/PA real {realRpPa:F4} sim {vRpPa:F4} R/G sim {vRpg:F2} runEnv {fit.Params.RunEnvironment:F3} batErr {BatterError(m, fit.Validation, validationReps):F4} pitErr {PitcherError(m, fit.Validation, validationReps):F4}");
			return fit;
		}

		// Log5 treats the league rate as the average opponent; keep the PA-weighted batter
		// talent and the BF-weighted pitcher talent centred on it so fitting cannot drift.
		private static void Normalize(SeasonModel m)
		{
			League lg = m.League;
			double w = 0, k = 0, bb = 0, hbp = 0, hr = 0, babip = 0;
			for (int i = 0; i < m.Batters.Length; i++)
			{
				double pa = m.Batters[i].PA;
				BatterTalent t = m.BT[i];
				w += pa; k += t.K * pa; bb += t.BB * pa; hbp += t.HBP * pa; hr += t.HR * pa; babip += t.Babip * pa;
			}
			for (int i = 0; i < m.Batters.Length; i++)
			{
				BatterTalent t = m.BT[i];
				t.K *= lg.K / (k / w); t.BB *= lg.BB / (bb / w); t.HBP *= lg.HBP / (hbp / w); t.HR *= lg.HR / (hr / w); t.Babip *= lg.Babip / (babip / w);
			}
			w = k = bb = hbp = hr = babip = 0;
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				double bf = m.Pitchers[i].BF;
				PitcherTalent t = m.PT[i];
				w += bf; k += t.K * bf; bb += t.BB * bf; hbp += t.HBP * bf; hr += t.HR * bf; babip += t.Babip * bf;
			}
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				PitcherTalent t = m.PT[i];
				t.K *= lg.K / (k / w); t.BB *= lg.BB / (bb / w); t.HBP *= lg.HBP / (hbp / w); t.HR *= lg.HR / (hr / w); t.Babip *= lg.Babip / (babip / w);
			}
		}

		// Mean of the talents over the last iterations (smooths simulation noise).
		private sealed class TalentAverage
		{
			private readonly double[,] m_b;
			private readonly double[,] m_p;
			private int m_n;

			public TalentAverage(SeasonModel m)
			{
				m_b = new double[m.Batters.Length, 10];
				m_p = new double[m.Pitchers.Length, 5];
			}

			public void Add(SeasonModel m)
			{
				for (int i = 0; i < m.Batters.Length; i++)
				{
					BatterTalent t = m.BT[i];
					m_b[i, 0] += t.K; m_b[i, 1] += t.BB; m_b[i, 2] += t.HBP; m_b[i, 3] += t.HR; m_b[i, 4] += t.Babip;
					m_b[i, 5] += t.Double; m_b[i, 6] += t.Triple; m_b[i, 7] += t.Steal; m_b[i, 8] += t.Speed; m_b[i, 9] += t.Sac;
				}
				for (int i = 0; i < m.Pitchers.Length; i++)
				{
					PitcherTalent t = m.PT[i];
					m_p[i, 0] += t.K; m_p[i, 1] += t.BB; m_p[i, 2] += t.HBP; m_p[i, 3] += t.HR; m_p[i, 4] += t.Babip;
				}
				m_n++;
			}

			public void Apply(SeasonModel m)
			{
				if (m_n == 0)
				{
					return;
				}
				for (int i = 0; i < m.Batters.Length; i++)
				{
					BatterTalent t = m.BT[i];
					t.K = m_b[i, 0] / m_n; t.BB = m_b[i, 1] / m_n; t.HBP = m_b[i, 2] / m_n; t.HR = m_b[i, 3] / m_n; t.Babip = m_b[i, 4] / m_n;
					t.Double = m_b[i, 5] / m_n; t.Triple = m_b[i, 6] / m_n; t.Steal = m_b[i, 7] / m_n; t.Speed = m_b[i, 8] / m_n; t.Sac = m_b[i, 9] / m_n;
				}
				for (int i = 0; i < m.Pitchers.Length; i++)
				{
					PitcherTalent t = m.PT[i];
					t.K = m_p[i, 0] / m_n; t.BB = m_p[i, 1] / m_n; t.HBP = m_p[i, 2] / m_n; t.HR = m_p[i, 3] / m_n; t.Babip = m_p[i, 4] / m_n;
				}
			}
		}

		public static SimStats Run(SeasonModel m, EngineParams prm, int reps, ulong seed)
		{
			SimStats agg = new SimStats(m.Batters.Length, m.Pitchers.Length, m.Season.Teams.Count);
			SeasonSimulator sim = new SeasonSimulator(m, prm, seed);
			for (int r = 0; r < reps; r++)
			{
				agg.Add(sim.RunSeason());
			}
			return agg;
		}

		public static double RealRunsPerPa(SeasonModel m)
		{
			IEnumerable<BatterLine> real = m.Batters.Where(b => !b.Replacement);
			return real.Sum(b => b.R) / (double)real.Sum(b => b.PA);
		}

		public static long TotalPa(SeasonModel m, SimStats s)
		{
			long pa = 0;
			for (int i = 0; i < m.Batters.Length; i++)
			{
				pa += s.Bat(i, BS.PA);
			}
			return pa;
		}

		public static double RealRunsPerGame(SeasonModel m)
		{
			// Replacement batters stand in for missing records at a typical run rate per PA.
			double runs = m.Batters.Where(b => !b.Replacement).Sum(b => b.R);
			double paReal = m.Batters.Where(b => !b.Replacement).Sum(b => b.PA);
			double paRepl = m.Batters.Where(b => b.Replacement).Sum(b => b.PA);
			runs += paRepl * runs / Math.Max(1, paReal) * 0.85;
			return runs / (m.Schedule.Length * 2.0);
		}

		private static void UpdateBatters(SeasonModel m, SimStats s, int reps)
		{
			League lg = m.League;
			for (int i = 0; i < m.Batters.Length; i++)
			{
				BatterLine b = m.Batters[i];
				if (b.Replacement)
				{
					continue;
				}
				BatterTalent t = m.BT[i];
				double simPa = s.Bat(i, BS.PA) / (double)reps;
				if (simPa < 1)
				{
					continue;
				}
				double sAb = s.Bat(i, BS.AB) / (double)reps, sH = s.Bat(i, BS.H) / (double)reps, sHr = s.Bat(i, BS.HR) / (double)reps;
				double sSo = s.Bat(i, BS.SO) / (double)reps, sBb = s.Bat(i, BS.BB) / (double)reps, sHbp = s.Bat(i, BS.HBP) / (double)reps;
				double sD = s.Bat(i, BS.D) / (double)reps, sT = s.Bat(i, BS.T) / (double)reps, sSb = s.Bat(i, BS.SB) / (double)reps;
				t.K = Nudge(t.K, b.SO, b.PA, sSo, simPa, lg.K, 8);
				t.BB = Nudge(t.BB, b.BB, b.PA, sBb, simPa, lg.BB, 8);
				t.HBP = Nudge(t.HBP, b.HBP, b.PA, sHbp, simPa, lg.HBP, 8);
				t.HR = Nudge(t.HR, b.HR, b.PA, sHr, simPa, lg.HR, 8);
				t.Babip = Nudge(t.Babip, b.H - b.HR, b.AB - b.SO - b.HR, sH - sHr, sAb - sSo - sHr, lg.Babip, 15);
				t.Double = Nudge(t.Double, b.D, b.H - b.HR, sD, sH - sHr, lg.Double, 5);
				t.Triple = Nudge(t.Triple, b.T, b.H - b.HR, sT, sH - sHr, lg.Triple, 5);
				double realOnFirst = b.Singles + b.BB + b.HBP, simOnFirst = sH - sD - sT - sHr + sBb + sHbp;
				t.Steal = Nudge(t.Steal, b.SB, realOnFirst, sSb, simOnFirst, lg.Steal, 5);
				double sSac = (s.Bat(i, BS.SH) + s.Bat(i, BS.SF)) / (double)reps;
				t.Sac = Nudge(t.Sac, Math.Max(0, b.PA - b.AB - b.BB - b.HBP), b.PA, sSac, simPa, lg.Sac, 8);
				t.Speed = Math.Clamp(Math.Pow(t.Triple / lg.Triple, 0.25) * Math.Pow(t.Steal / lg.Steal, 0.2), 0.7, 1.35);
			}
		}

		private static void UpdatePitchers(SeasonModel m, SimStats s, int reps)
		{
			League lg = m.League;
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				// Replacement arms are fitted too: their line holds the league's unrecorded pitching.
				PitcherLine p = m.Pitchers[i];
				PitcherTalent t = m.PT[i];
				double sBf = s.Pit(i, PS.BF) / (double)reps;
				if (sBf < 1)
				{
					continue;
				}
				double sSo = s.Pit(i, PS.SO) / (double)reps, sBb = s.Pit(i, PS.BB) / (double)reps, sHbp = s.Pit(i, PS.HBP) / (double)reps;
				double sHr = s.Pit(i, PS.HR) / (double)reps, sH = s.Pit(i, PS.H) / (double)reps;
				double realBip = p.BF - p.SO - p.BB - p.HBP - p.HR, simBip = sBf - sSo - sBb - sHbp - sHr;
				t.K = Nudge(t.K, p.SO, p.BF, sSo, sBf, lg.K, 10);
				t.BB = Nudge(t.BB, p.BB, p.BF, sBb, sBf, lg.BB, 10);
				t.HBP = Nudge(t.HBP, p.HBP, p.BF, sHbp, sBf, lg.HBP, 10);
				t.HR = Nudge(t.HR, p.HR, p.BF, sHr, sBf, lg.HR, 10);
				t.Babip = Nudge(t.Babip, p.H - p.HR, realBip, sH - sHr, simBip, lg.Babip, 20);
			}
		}

		// talent *= (real rate / simulated rate)^Eta, both rates padded with k league-rate events.
		private static double Nudge(double talent, double realCount, double realN, double simCount, double simN, double league, double k)
		{
			double real = (realCount + k * league) / (Math.Max(0, realN) + k);
			double sim = (simCount + k * league) / (Math.Max(0, simN) + k);
			return Math.Clamp(talent * Math.Pow(real / sim, Eta), 0.0005, 0.9);
		}

		// PA-weighted mean absolute error of OBP among real batters.
		public static double BatterError(SeasonModel m, SimStats s, int reps)
		{
			double err = 0, w = 0;
			for (int i = 0; i < m.Batters.Length; i++)
			{
				BatterLine b = m.Batters[i];
				if (b.Replacement || b.PA < 50)
				{
					continue;
				}
				double sPa = s.Bat(i, BS.PA) / (double)reps;
				if (sPa < 1)
				{
					continue;
				}
				double realObp = (b.H + b.BB + b.HBP) / (double)b.PA;
				double simObp = (s.Bat(i, BS.H) + s.Bat(i, BS.BB) + s.Bat(i, BS.HBP)) / (double)reps / sPa;
				err += Math.Abs(realObp - simObp) * b.PA;
				w += b.PA;
			}
			return w > 0 ? err / w : 0;
		}

		// Innings-weighted mean absolute ERA error among real pitchers.
		public static double PitcherError(SeasonModel m, SimStats s, int reps)
		{
			double err = 0, w = 0;
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				PitcherLine p = m.Pitchers[i];
				if (p.Replacement || p.Outs < 60)
				{
					continue;
				}
				double sOuts = s.Pit(i, PS.Outs) / (double)reps;
				if (sOuts < 3)
				{
					continue;
				}
				double realEra = p.ER * 27.0 / p.Outs;
				double simEra = s.Pit(i, PS.ER) / (double)reps * 27.0 / sOuts;
				err += Math.Abs(realEra - simEra) * p.Outs;
				w += p.Outs;
			}
			return w > 0 ? err / w : 0;
		}
	}
}
