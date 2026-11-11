using System;
using System.Linq;

namespace Ma9MSim
{
	// TeamValue (engine session, 2026-11-11; user 18:4x 10번 "볼 상한 안에서 가장 강한 라인업을 짜는 기능", 11번 "찍었을 때 가장
	// 승률이 높게 나오는 쪽으로 자동으로 찍어주는 기능"): a team's strength in runs a game against an average club, counted
	// from its built game model without playing a game — quick and with no luck in it, so every 팀 특성 choice or many 1군
	// candidates can be compared (playing them would need ~15,000 games each to see a trait point: ±0.6%p at 4,000).
	// - Batting: each batter's plate appearances by his slot (PaBySlot) × the run value of his event rates against an average
	//   pitcher (EventRuns: this engine's runs from each event to the end of the half-inning less the run expectancy of its
	//   base-out state; --teamvalue, 40,000 games).
	// - Pitching: each pitcher's batters faced a team-game by deck slot (today's starter one game in five) × the run value
	//   of his event rates against an average batter, times his slot's leverage (late relievers pitch closer games).
	// - What the event rates do not hold, per row point away from 65 (measured in full games, --statvalue +10 paired, less
	//   what the event rates already give): 주력 (steals, running, infield hits), 정신력 (clutch), 수비 by position, pitchers'
	//   체력 through the pitch limit and P정신.
	// - Steals as attempts × (safe·SB − caught·CS) with the run values of a steal and a caught stealing (--stealvalue).
	// Wins: WinPerRun of a run a game (--statvalue: +10 컨택 1.55 runs → 12.9%p, +10 파워 0.98 → 8.9%p).
	public static class TeamValue
	{
		// K, BB, HBP, HR, 1B, 2B, 3B, ball-in-play out (--teamvalue 2026-11-11, cards_live5, fielding engine). HR 1.30, not the
		// 1.48 of the run expectancy: with it the events as played match the runs of +10 컨택 / 파워 / 선구 in full games
		// (1.58 / 0.98 / 1.29 against 1.55 / 0.98 / 1.29; with 1.48 파워 came out 12% high).
		public static double WK = -0.306, WBB = 0.355, WHBP = 0.325, WHR = 1.30, W1B = 0.469, W2B = 0.795, W3B = 1.049, WOut = -0.303;
		public static readonly double[] PaBySlot = { 4.876, 4.756, 4.645, 4.537, 4.428, 4.318, 4.212, 4.105, 3.992 };
		// Batters faced a team-game: today's starter (a start), RP1-4, SU, CP.
		public static double BfStart = 23.17;
		// RP1 and RP2 as one (2.61 / 3.58 measured): the engine sends both to the 6th-7th of close games (HoldOrder) and the
		// big spots by quality, so which of the two holds which slot does not matter (자동 편성 swapped them for +1.5%p counted,
		// −0.5 ± 0.3%p in full games).
		public static readonly double[] BfRelief = { 3.095, 3.095, 3.38, 2.99, 2.32, 1.80 };
		// Wins per run by deck slot, the batters-faced mean 1 (--statvalue pit:-1 +10 --slots, 20,000 paired games: %p a run allowed
		// SP 9.15, RP1-2 9.27, RP3 6.78, RP4 5.78, SU 11.5, CP 18.1 → 0.99 1.00 0.73 0.62 1.24 1.95), then each slot set so that
		// TeamValue's runs for +10 there match the games' (with the starters' batters faced by their limits: SP ×0.86, RP1-2
		// ×1.13, RP3 ×1.15, RP4 ×1.10, SU ×1.37, CP ×1.04). The closer's and set-up man's runs count about twice.
		public static readonly double[] Leverage = { 0.85, 0.85, 0.85, 0.85, 0.85, 1.13, 1.13, 0.84, 0.68, 1.70, 2.03 };
		// --statvalue +10 (20,000 paired games): 컨택 12.91%p for 1.554 runs, 파워 8.89 / 0.992, 선구 10.86 / 1.305.
		public static double WinPerRun = 0.085;
		// Runs a game per row point away from 65, per plate appearance (batters) or per batter faced (pitchers), past what
		// the event rates give (--statvalue +10: 주력 +0.269 runs, of it 0.098 triples and steals; 정신력 +0.352). 정신력 plays in
		// the close spots, so its runs count ClutchWeight times in wins (+10: 4.62%p for 0.35 runs, 13.1%p a run).
		// Pitchers' P정신 +10: −0.361 runs, 3.90%p (10.8%p a run).
		public static double SpeedPerPa = 0.000429, MentalPerPa = 0.000883, PitMentalPerBf = 0.000906, ClutchWeight = 1.5, PitClutchWeight = 1.27;
		// Runs a game per row point of defence away from 65 at each position (C 1B 2B 3B SS LF CF RF): the fielding engine's
		// runs per 10 points a season (Fielding.Defence, cards_v6: 6.1 7.1 6.3 6.6 5.9 7.1 6.4 6.9) × 1.29, so +10 at every
		// position is the 0.471 runs a game of --statvalue bat:5 (the catcher's throwing on steals in it).
		public static readonly double[] DefPerPoint = new[] { 6.1, 7.1, 6.3, 6.6, 5.9, 7.1, 6.4, 6.9 }.Select(x => x * 1.293 / 1440).ToArray();
		// Runs a game per pitch of a starter's limit (one start in five), up to LimitUseful: tiring later within the start.
		public static double LimitRuns = 0.003, LimitUseful = 130;
		// A starter's batters faced by his pitch limit (--teamvalue, 2026-11-11, starters' 체력 drawn 40-100 in half the games:
		// limit 35 → 7.9, 45 → 9.4 … 115 → 24.3, 125 and more 25.6); what he does not face the bullpen does (RP1-4 by their
		// shares, at LongLeverage). Before, every starter counted 23.17 a start and relievers starting looked as good as
		// starters (자동 편성 moved the set-up man into a 체력 56 starter's spot: +0.5%p counted, −4.7%p in full games).
		private static readonly double[] StartBf = { 7.9, 9.4, 11.9, 14.3, 16.5, 18.5, 20.6, 22.2, 24.3, 25.6 };
		public static double LongLeverage = 0.80;

		public static double StarterBf(double limit)
		{
			double x = (limit - 35) / 10;
			if (x <= 0) return StartBf[0] * Math.Max(0.3, limit / 35);
			if (x >= StartBf.Length - 1) return StartBf[^1];
			int i = (int)x;
			return StartBf[i] + (StartBf[i + 1] - StartBf[i]) * (x - i);
		}
		// A steal and a caught stealing (RE24, the attempt mix of 0 and 1 out), and attempts a game for a lineup of average
		// runners (League.Steal) at the league's odds.
		public static double SbRuns = 0.188, CsRuns = -0.507, AttemptsPerGame = 1.09;

		public sealed class Parts
		{
			public double Bat, Speed, Mental, Steal, Field, Pitch, Stamina, PitMental, OppSteal;
			public double Runs => Bat + Speed + Mental + Steal + Field + Pitch + Stamina + PitMental + OppSteal;
			public double Win => Runs * WinPerRun;

			public override string ToString() => $"bat {Bat:+0.000;-0.000} speed {Speed:+0.000;-0.000} mental {Mental:+0.000;-0.000} steal {Steal:+0.000;-0.000} field {Field:+0.000;-0.000}"
				+ $" pitch {Pitch:+0.000;-0.000} stamina {Stamina:+0.000;-0.000} pmental {PitMental:+0.000;-0.000} oppsteal {OppSteal:+0.000;-0.000} = {Runs:+0.000;-0.000} runs ({100 * Win:+0.00;-0.00}%p)";
		}

		// Run value of a plate appearance with these rates (in-play hits split by the 2B / 3B shares of non-HR hits).
		public static double PerPa(double k, double bb, double hbp, double hr, double babip, double dbl, double tpl)
		{
			double inPlay = Math.Max(0, 1 - k - bb - hbp - hr);
			double hits = inPlay * babip;
			return WK * k + WBB * bb + WHBP * hbp + WHR * hr + hits * ((1 - dbl - tpl) * W1B + dbl * W2B + tpl * W3B) + (inPlay - hits) * WOut;
		}

		public static double BatterRuns(BatterTalent t) => PerPa(t.K, t.BB, t.HBP, t.HR, t.Babip, t.Double, t.Triple);

		// The rates of a plate appearance as the engine combines them (Log5 around the league; Engine.HalfInning): batter
		// against pitcher, extra-base hits by the batter's shares and the pitcher's Xbh. K, BB, HBP, HR, BABIP, 2B and 3B shares.
		public static (double k, double bb, double hbp, double hr, double babip, double dbl, double tpl) Matchup(BatterTalent b, PitcherTalent p, League lg)
		{
			double k = Log5.Combine(b.K, p.K, lg.K), bb = Log5.Combine(b.BB, p.BB, lg.BB), hbp = Log5.Combine(b.HBP, p.HBP, lg.HBP), hr = Log5.Combine(b.HR, p.HR, lg.HR);
			double sum = k + bb + hbp + hr;
			if (sum > 0.9)
			{
				double sc = 0.9 / sum;
				k *= sc; bb *= sc; hbp *= sc; hr *= sc;
			}
			double babip = Math.Clamp(Log5.Combine(b.Babip, p.Babip, lg.Babip), 0.05, 0.6);
			return (k, bb, hbp, hr, babip, Math.Clamp(b.Double * p.Xbh, 0, 0.6), Math.Clamp(b.Triple * p.Xbh, 0, 0.2));
		}

		public static double MatchupRuns(BatterTalent b, PitcherTalent p, League lg)
		{
			var x = Matchup(b, p, lg);
			return PerPa(x.k, x.bb, x.hbp, x.hr, x.babip, x.dbl, x.tpl);
		}

		// A side's typical batter (by plate appearances a slot) and typical pitcher (by batters faced a slot), rate by rate.
		public static BatterTalent TypicalBatter(SeasonModel m, int side)
		{
			BatterTalent r = new BatterTalent { K = 0, BB = 0, HBP = 0, HR = 0, Babip = 0, Double = 0, Triple = 0, Steal = 0, Speed = 0 };
			double w = 0;
			int[] order = m.TeamBatters[side];
			for (int k = 0; k < order.Length && k < 9; k++)
			{
				BatterTalent t = m.BT[order[k]];
				double a = PaBySlot[k];
				r.K += a * t.K; r.BB += a * t.BB; r.HBP += a * t.HBP; r.HR += a * t.HR; r.Babip += a * t.Babip; r.Double += a * t.Double; r.Triple += a * t.Triple;
				r.Steal += a * t.Steal; r.Speed += a * t.Speed;
				w += a;
			}
			r.K /= w; r.BB /= w; r.HBP /= w; r.HR /= w; r.Babip /= w; r.Double /= w; r.Triple /= w; r.Steal /= w; r.Speed /= w;
			return r;
		}

		public static PitcherTalent TypicalPitcher(SeasonModel m, int side)
		{
			PitcherTalent r = new PitcherTalent { K = 0, BB = 0, HBP = 0, HR = 0, Babip = 0, Xbh = 0 };
			double w = 0;
			GameDeck d = m.Decks[side];
			for (int s = 0; s < d.Pitchers.Length; s++)
			{
				if (d.Pitchers[s] < 0) continue;
				PitcherTalent t = m.PT[d.Pitchers[s]];
				double a = s < 5 ? BfStart / 5 : BfRelief[s - 5];
				r.K += a * t.K; r.BB += a * t.BB; r.HBP += a * t.HBP; r.HR += a * t.HR; r.Babip += a * t.Babip; r.Xbh += a * t.Xbh;
				w += a;
			}
			r.K /= w; r.BB /= w; r.HBP /= w; r.HR /= w; r.Babip /= w; r.Xbh /= w;
			return r;
		}

		// K, BB, HBP, HR, 1B, 2B, 3B a plate appearance from these rates (tests: against the events as played).
		public static double[] Events(double k, double bb, double hbp, double hr, double babip, double dbl, double tpl)
		{
			double hits = Math.Max(0, 1 - k - bb - hbp - hr) * babip;
			return new[] { k, bb, hbp, hr, hits * (1 - dbl - tpl), hits * dbl, hits * tpl };
		}

		// Against him: an average batter's rates through his own (the engine's matchup is about his rates against an average
		// batter), extra-base hits by his Xbh.
		public static double PitcherRuns(PitcherTalent t, League lg) => PerPa(t.K, t.BB, t.HBP, t.HR, t.Babip, lg.Double * t.Xbh, lg.Triple * t.Xbh);

		// The side's strength; steal multipliers as 팀 특성 sets them (attempts, caught share; the other side's attempts and our
		// catcher's caught share), the steal odds with an average catcher on the other side.
		// Against the other side of the model unless typical opponents are given (the league's clubs, averaged).
		public static Parts Of(SeasonModel m, int side, double stealTry = 1, double stealCaught = 1, double oppStealTry = 1, double oppCaught = 1, double stealSuccess = 0.70,
			BatterTalent oppBat = null, PitcherTalent oppPit = null)
		{
			Parts p = new Parts();
			League lg = m.League;
			oppBat ??= TypicalBatter(m, 1 - side);
			oppPit ??= TypicalPitcher(m, 1 - side);
			BatterTalent lgB = new BatterTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip, Double = lg.Double, Triple = lg.Triple, Steal = lg.Steal };
			PitcherTalent lgP = new PitcherTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip };
			double lgBat = MatchupRuns(lgB, oppPit, lg);
			int[] order = m.TeamBatters[side];
			double paAll = 0, attemptsW = 0, safeW = 0;
			for (int k = 0; k < order.Length && k < 9; k++)
			{
				int i = order[k];
				double pa = PaBySlot[k];
				paAll += pa;
				p.Bat += pa * (MatchupRuns(m.BT[i], oppPit, lg) - lgBat);
				if (m.RunX != null) p.Speed += pa * SpeedPerPa * 10 * m.RunX[i];
				if (m.BatMental != null) p.Mental += ClutchWeight * pa * MentalPerPa * (m.BatMental[i] - 65);
				// Steals: his attempts by his rate against the league's, at his odds against an average catcher.
				double tries = pa * m.BT[i].Steal / Math.Max(1e-6, lg.Steal) * stealTry;
				double safe = Math.Clamp(stealSuccess * Math.Sqrt(m.BT[i].Speed), 0.4, 0.92);
				double caught = Math.Clamp((1 - safe) * stealCaught, 0.02, 0.75);
				attemptsW += tries;
				safeW += tries * ((1 - caught) * SbRuns + caught * CsRuns);
			}
			if (paAll > 0)
			{
				double lgValue = AttemptsPerGame * (stealSuccess * SbRuns + (1 - stealSuccess) * CsRuns);
				p.Steal = AttemptsPerGame / paAll * safeW - lgValue;
			}
			GameDeck d = m.Decks?[side];
			if (d != null && m.Defs != null)
			{
				for (int pos = 0; pos < 8; pos++)
				{
					int i = d.At[pos];
					if (i >= 0) p.Field += DefPerPoint[pos] * (m.Defs[i][pos] - 65);
				}
			}
			double lgPit = MatchupRuns(oppBat, lgP, lg);
			if (d != null)
			{
				double PerBf(int q) => lgPit - MatchupRuns(oppBat, m.PT[q], lg) + (m.PitMental != null ? PitClutchWeight * PitMentalPerBf * (m.PitMental[q] - 65) : 0);
				// The bullpen that takes a short start's innings: RP1-4 by their shares.
				double penW = 0, pen = 0;
				for (int s = 5; s <= 8 && s < d.Pitchers.Length; s++)
				{
					if (d.Pitchers[s] < 0) continue;
					pen += BfRelief[s - 5] * PerBf(d.Pitchers[s]);
					penW += BfRelief[s - 5];
				}
				pen = penW > 0 ? pen / penW : 0;
				for (int s = 0; s < d.Pitchers.Length; s++)
				{
					int q = d.Pitchers[s];
					if (q < 0) continue;
					double mental = m.PitMental != null ? PitClutchWeight * PitMentalPerBf * (m.PitMental[q] - 65) : 0;
					if (s < 5)
					{
						double bf = m.Tac != null ? StarterBf(m.Tac.Limit[q]) : BfStart;
						p.Pitch += Leverage[s] / 5 * bf * (PerBf(q) - mental) + LongLeverage / 5 * (BfStart - bf) * pen;
						p.PitMental += Leverage[s] / 5 * bf * mental;
						if (m.Tac != null) p.Stamina += LimitRuns / 5 * (Math.Min(LimitUseful, m.Tac.Limit[q]) - 110);
					}
					else
					{
						double bf = BfRelief[s - 5];
						p.Pitch += Leverage[s] * bf * (PerBf(q) - mental);
						p.PitMental += Leverage[s] * bf * mental;
					}
				}
			}
			// Their runners against our catcher (an average lineup's attempts).
			double oppTries = AttemptsPerGame * oppStealTry;
			double oc = Math.Clamp((1 - stealSuccess) * oppCaught, 0.02, 0.75);
			p.OppSteal = -(oppTries * ((1 - oc) * SbRuns + oc * CsRuns) - AttemptsPerGame * (stealSuccess * SbRuns + (1 - stealSuccess) * CsRuns));
			return p;
		}
	}
}
