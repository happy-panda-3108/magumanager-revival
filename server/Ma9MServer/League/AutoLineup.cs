using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 자동 편성 (engine session, 2026-11-11; user 18:4x 10번: "볼 상한 안에서 가장 강한 라인업을 짜는 기능도 요즘 매니징 게임의 특징이라
	// 구현을 해보고 싶긴 한데, 난이도가 있지 않을까 싶음. 일단 진행해봅시다"; keeping the team colour or not: the kt AI tester's
	// idea): the strongest 1군 of the club's own cards under its ball cap, as Ma9MSim.TeamValue counts it against its league.
	// - A 1군 is valued as league games build it: today's condition, development (교육·계약·트레이닝·코치), the team colour the
	//   자동 등록 would give it (TeamColors.AutoBonuses), lineup effects (선수분석 graph, out of position / role), the league
	//   reference of now (TeamStrength.LeagueShift), the club's 팀 특성; against the league's typical batter and pitcher.
	// - Rules: the cap (RosterHandlers.MaxCostOf), no card in a 특별수강 (LessonHandlers.InLesson), one 레전드 batter and one
	//   레전드 pitcher, one card of a player, no starter the 선발 5경기 제한 holds (SpBlock), fielders where they can field.
	// - Search: from the 1군 of now, from the best 1군 by each card's own value (λ·balls under the cap, as TeamColorSim.Build),
	//   and (unless the team colour is to be kept) from 1군s built round the biggest single-season and decade groups of the
	//   cards; each improved by single changes (a card in for one out, two slots swapped) valued whole, then the batting order
	//   by the 선수분석 graph (LineupBuilder.BestOrder). keepColour: every team colour working now must stay (same group, same or
	//   better grade).
	// Packets and the screen are the systems session's; AutoLineup is what they call. Dev: --autolineup <team name> [keep].
	public static partial class LeagueService
	{
		public sealed class AutoLineupResult
		{
			public Lineup Lineup, Current;
			public double Runs, CurrentRuns;   // TeamValue runs a game (with the team colour each would get)
			public double Gain => (Runs - CurrentRuns) * TeamValue.WinPerRun;
			public int Balls, CurrentBalls, Cap, Evaluations;
			public int[] Colours = new int[3], CurrentColours = new int[3];   // the 자동 등록's entries
			public List<int> StartersOut = new List<int>();                   // starters of now left out (선발 5경기 제한 follows)
			public TeamValue.Parts Parts, CurrentParts;
			public string Start = "";                                          // which start won
		}

		private sealed class LineupContext
		{
			public LeagueState S;
			public int Me, Other, Cap, PenaltyCap;
			public GameTeam[] Teams;     // the league after the reference and traits
			public (double[] bat, double[] pit, double def) Shift;
			public TraitEffects.Effect Traits;
			public BatterTalent OppBat;
			public PitcherTalent OppPit;
			public Dictionary<int, TeamCard> Owned;
			public Dictionary<int, byte> Cond;
			public Dictionary<int, CardInfo> Info;
			public HashSet<int> Blocked;   // may not start (선발 5경기 제한)
			public Lineup Current;
			public int Evaluations;
		}

		private static LineupContext Context(uint userCode)
		{
			LeagueState s = s_store?.Load(userCode);
			if (s == null || s.Clubs.Count == 0) return null;
			int me = s.MyIndex;
			int game = Math.Clamp(s.NextGame, 1, 144);
			var (raw, lineups, traits) = Teams(s, game, _ => game, raw: true);
			LineupContext x = new LineupContext { S = s, Me = me, Other = Enumerable.Range(0, raw.Length).First(i => i != me), Traits = traits[me] };
			x.Shift = TeamStrength.LeagueShift(raw);
			x.Teams = raw.Select(CopyTeam).ToArray();
			for (int i = 0; i < x.Teams.Length; i++)
			{
				TeamStrength.ApplyShift(x.Teams[i], x.Shift);
				TraitEffects.Raise(x.Teams[i], traits[i]);
			}
			(x.OppBat, x.OppPit) = TypicalOpponents(x.Teams, traits, me);
			x.Current = lineups[me];
			LeagueClub club = s.Clubs[me];
			List<TeamCard> owned = s_db.GetCards(userCode);
			x.Owned = owned.ToDictionary(c => c.Idx);
			DateTime day = s.SlotTime(game);
			x.Cond = owned.ToDictionary(c => c.Idx, c => Condition.For(club.Owner, c.Idx, day));
			HashSet<int> lesson = LessonHandlers.InLesson(userCode);
			x.Info = owned.Where(c => !lesson.Contains(c.Idx)).Select(c => s_data.Catalog.Get(c.Idx)).Where(c => c != null).ToDictionary(c => c.Idx);
			x.Blocked = SpBlock.ForRoster(userCode, x.Info.Values.Where(c => !c.IsBatter).Select(c => c.Idx), x.Current).Select(b => b.idx).ToHashSet();
			x.Cap = RosterHandlers.MaxCostOf(userCode);
			x.PenaltyCap = LeagueRules.BallCap[s.Tier];
			return x;
		}

		// The league's typical batter and pitcher as one club meets them (each club's traits on its own side).
		private static (BatterTalent bat, PitcherTalent pit) TypicalOpponents(GameTeam[] teams, TraitEffects.Effect[] traits, int me)
		{
			GameEnv env = s_data.Env;
			List<BatterTalent> bats = new List<BatterTalent>();
			List<PitcherTalent> pits = new List<PitcherTalent>();
			for (int j = 0; j < teams.Length; j++)
			{
				if (j == me) continue;
				SeasonModel mj = CardGame.Build(teams[me], teams[j], env);
				TraitEffects.Apply(mj, 1, traits[j]);
				bats.Add(TeamValue.TypicalBatter(mj, 1));
				PitcherTalent p = TeamValue.TypicalPitcher(mj, 1);
				if (traits[j] != null) p.Xbh *= traits[j].PXBH;
				pits.Add(p);
			}
			BatterTalent b = new BatterTalent
			{
				K = bats.Average(t => t.K), BB = bats.Average(t => t.BB), HBP = bats.Average(t => t.HBP), HR = bats.Average(t => t.HR), Babip = bats.Average(t => t.Babip),
				Double = bats.Average(t => t.Double), Triple = bats.Average(t => t.Triple), Steal = bats.Average(t => t.Steal), Speed = bats.Average(t => t.Speed)
			};
			PitcherTalent q = new PitcherTalent
			{
				K = pits.Average(t => t.K), BB = pits.Average(t => t.BB), HBP = pits.Average(t => t.HBP), HR = pits.Average(t => t.HR), Babip = pits.Average(t => t.Babip), Xbh = pits.Average(t => t.Xbh)
			};
			return (b, q);
		}

		// The club's team for a 1군 as a league game builds it, with the team colour that 1군 would get.
		private static GameTeam BuildTeam(LineupContext x, Lineup l, out int[] colours)
		{
			(Dictionary<int, int> bonus, int[] entries) = TeamColors.AutoBonuses(l);
			colours = entries;
			List<TeamCard> cards = new List<TeamCard>();
			foreach (int idx in l.Batters.Concat(l.Pitchers).Where(i => i != 0).Distinct())
			{
				if (!x.Owned.TryGetValue(idx, out TeamCard c)) continue;
				sbyte v = (sbyte)Math.Clamp(bonus.GetValueOrDefault(idx), 0, 100);
				cards.Add(new TeamCard
				{
					Idx = c.Idx, RemainContract = c.RemainContract, PositionTrainingInfo = c.PositionTrainingInfo, IsLock = c.IsLock,
					Educated = c.Educated, Contract = c.Contract, Training = c.Training, Coach = c.Coach, TeamColor = Enumerable.Repeat(v, 6).ToArray(),
					ContractUntil = c.ContractUntil, ContractCount = c.ContractCount
				});
			}
			GameTeam t = new GameTeam { Name = x.S.Clubs[x.Me].Name };
			for (int pos = 0; pos < Lineup.FielderSlots; pos++) t.Position[l.Fielders[pos] - 1] = pos;
			byte Cond(int idx) => x.Cond.TryGetValue(idx, out byte c) ? c : (byte)2;
			for (int slot = 0; slot < 9; slot++) t.Order[slot] = s_data.Card(l.Batters[slot], Cond(l.Batters[slot]));
			for (int s = 0; s < Lineup.PitcherSlots; s++) t.Pitchers[s] = l.Pitchers[s] != 0 ? s_data.Card(l.Pitchers[s], Cond(l.Pitchers[s])) : null;
			LineupEffects.Apply(t, l, s_data.Catalog, cards);
			// LineupEffects scales today's starter (slot 0 here) and the bullpen; SP2-5 as each would be on his own day (a
			// reliever starting −15%, his graph 40), so TeamValue's one-in-five of each is the starter that pitches.
			for (int s = 1; s < 5; s++)
			{
				GameCard g = t.Pitchers[s];
				CardInfo p = s_data.Catalog.Get(l.Pitchers[s]);
				if (g == null || p == null) continue;
				TeamCard own = cards.FirstOrDefault(c => c.Idx == p.Idx);
				int order = own != null && own.PositionTrainingInfo % 10 != 0 ? PositionChangeHandlers.OrderOf(p, own.PositionTrainingInfo) : p.PreferredOrder;
				double share = LineupEffects.FromGraph(LineupEffects.PitcherGraph(0, order));
				if (!LineupEffects.InRole(0, order)) share = (1 + share) * (1 + LineupEffects.OutOfRole) - 1;
				if (share != 0) g.Condition = (1 + g.Condition) * (1 + share) - 1;
			}
			TeamStrength.AddBonuses(t, l, cards);
			if (OverCapPenalty && RosterHandlers.LineupBalls(l) > x.PenaltyCap)
			{
				foreach (GameCard g in t.Order.Concat(t.Pitchers).Where(g => g != null)) g.Condition = (1 + g.Condition) * OverCapStrength - 1;
			}
			TeamStrength.ApplyShift(t, x.Shift);
			TraitEffects.Raise(t, x.Traits);
			return t;
		}

		private static TeamValue.Parts Evaluate(LineupContext x, Lineup l, out int[] colours)
		{
			x.Evaluations++;
			GameTeam t = BuildTeam(x, l, out colours);
			SeasonModel m = CardGame.Build(t, x.Teams[x.Other], s_data.Env);
			TraitEffects.Effect e = x.Traits ?? new TraitEffects.Effect();
			TraitEffects.Apply(m, 0, new TraitEffects.Effect { HR = e.HR, K = e.K, BB = e.BB, XBH = e.XBH, PK = e.PK, PBabip = e.PBabip, PHR = e.PHR, StartLimit = e.StartLimit });
			BatterTalent ob = new BatterTalent
			{
				K = x.OppBat.K, BB = x.OppBat.BB, HBP = x.OppBat.HBP, HR = x.OppBat.HR, Babip = x.OppBat.Babip, Steal = x.OppBat.Steal, Speed = x.OppBat.Speed,
				Double = Math.Clamp(x.OppBat.Double * e.PXBH, 0.03, 0.5), Triple = Math.Clamp(x.OppBat.Triple * e.PXBH, 0.0005, 0.15)
			};
			return TeamValue.Of(m, 0, e.StealTry, e.StealCaught, e.PStealTry, e.PCaught, oppBat: ob, oppPit: x.OppPit);
		}

		// Each card's own value without the team colour or the graph (to pick starts and to try the likely changes first):
		// a batter's runs a game in an average slot and his defence by position, a pitcher's runs saved a batter faced.
		private sealed class CardValue
		{
			public CardInfo Info;
			public double Off, PerBf, Stamina;
			public double[] Def = new double[8];
		}

		private static Dictionary<int, CardValue> CardValues(LineupContext x)
		{
			Dictionary<int, CardValue> v = new Dictionary<int, CardValue>();
			League lg = s_data.Env.League;
			BatterTalent lgB = new BatterTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip, Double = lg.Double, Triple = lg.Triple };
			PitcherTalent lgP = new PitcherTalent { K = lg.K, BB = lg.BB, HBP = lg.HBP, HR = lg.HR, Babip = lg.Babip };
			double baseBat = TeamValue.MatchupRuns(lgB, x.OppPit, lg), basePit = TeamValue.MatchupRuns(x.OppBat, lgP, lg);
			double pa = TeamValue.PaBySlot.Average();
			foreach (CardInfo info in x.Info.Values)
			{
				GameCard g = s_data.Card(info.Idx, x.Cond.TryGetValue(info.Idx, out byte cd) ? cd : (byte)2);
				if (g?.Row == null) continue;
				if (x.Owned.TryGetValue(info.Idx, out TeamCard tc))
				{
					TeamCard noColour = new TeamCard { Idx = tc.Idx, PositionTrainingInfo = tc.PositionTrainingInfo, Educated = tc.Educated, Contract = tc.Contract, Training = tc.Training, Coach = tc.Coach };
					TeamStrength.AddBonuses(g, noColour);
				}
				GameTeam one = new GameTeam();
				if (info.IsBatter) one.Order[0] = g; else one.Pitchers[0] = g;
				TeamStrength.ApplyShift(one, x.Shift);
				TraitEffects.Raise(one, x.Traits);
				CardRow r = g.Row.Clone();
				double f = 1 + g.Condition;
				for (int j = 0; j < 6; j++) r.R[j] *= f;
				for (int p = 0; p < 8; p++) r.Defs[p] *= f;
				r.Pitches = r.Pitches?.Select(q => (q.code, q.rating * f)).ToList();
				CardValue cv = new CardValue { Info = info };
				if (info.IsBatter)
				{
					BatterTalent bt = CardGame.BatterTalentOf(r, s_data.Env);
					bt.Speed = Math.Clamp(Math.Exp(s_data.Env.Rules.SpeedPer10 * CardRules.X(r.R[3])), 0.7, 1.35);
					cv.Off = pa * (TeamValue.MatchupRuns(bt, x.OppPit, lg) - baseBat) + pa * TeamValue.SpeedPerPa * (r.R[3] - 65)
						+ TeamValue.ClutchWeight * pa * TeamValue.MentalPerPa * (r.R[4] - 65);
					for (int p = 0; p < 8; p++) cv.Def[p] = info.CanField(p) ? TeamValue.DefPerPoint[p] * (r.Defs[p] - 65) : double.NaN;
				}
				else
				{
					PitcherTalent pt = CardGame.PitcherTalentOf(r, s_data.Env);
					cv.PerBf = basePit - TeamValue.MatchupRuns(x.OppBat, pt, lg) + TeamValue.PitClutchWeight * TeamValue.PitMentalPerBf * (r.R[5] - 65);
					cv.Stamina = TeamValue.LimitRuns / 5 * (Math.Min(TeamValue.LimitUseful, CardGame.Rules.Limit(r.R[0])) - 110);
				}
				v[info.Idx] = cv;
			}
			return v;
		}

		// Batting slot k's field position (0-7, 8 = DH), or -1 for the bench.
		private static int PositionOf(Lineup l, int k) => k >= 9 ? -1 : Array.IndexOf(l.Fielders, (byte)(k + 1));

		private static double SlotValue(CardValue c, Lineup l, int k, bool pitcher)
		{
			if (c == null) return double.NegativeInfinity;
			if (pitcher)
			{
				double bf = k < 5 ? TeamValue.BfStart / 5 : TeamValue.BfRelief[k - 5];
				return TeamValue.Leverage[k] * bf * c.PerBf + (k < 5 ? c.Stamina : 0);
			}
			int pos = PositionOf(l, k);
			if (pos < 0) return -0.001 * c.Info.Ball;   // the bench plays no part but its balls (and the team colour)
			if (pos == 8) return c.Off;
			return double.IsNaN(c.Def[pos]) ? c.Off - 0.5 : c.Off + c.Def[pos];
		}

		private static int PersonOf(CardInfo c) => c.IsBatter ? c.Sidx : -1 - c.Sidx;

		// Whether a 1군 keeps the rules (and, with keep, the team colours working now).
		private static bool Allowed(LineupContext x, Lineup l, int[] keep)
		{
			List<int> all = l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToList();
			if (all.Count != all.Distinct().Count() || all.Any(i => !x.Info.ContainsKey(i))) return false;
			if (all.Select(i => PersonOf(x.Info[i])).Distinct().Count() != all.Count) return false;
			if (all.Count(i => x.Info[i].IsLegend && x.Info[i].IsBatter) > RosterHandlers.MaxLegendBatters) return false;
			if (all.Count(i => x.Info[i].IsLegend && !x.Info[i].IsBatter) > RosterHandlers.MaxLegendPitchers) return false;
			if (l.Batters.Take(9).Any(i => i == 0 || !x.Info[i].IsBatter) || l.Batters.Skip(9).Any(i => i != 0 && !x.Info[i].IsBatter)) return false;
			if (l.Pitchers.Take(5).Any(i => i == 0) || l.Pitchers.Any(i => i != 0 && x.Info[i].IsBatter)) return false;
			if (l.Pitchers.Take(5).Any(i => x.Blocked.Contains(i))) return false;
			if (RosterHandlers.LineupBalls(l) > x.Cap) return false;
			if (keep != null && keep.Any(e => e != 0))
			{
				HashSet<int> available = TeamColors.Available(l);
				foreach (int e in keep.Where(e => e != 0))
				{
					// The same kind, slot and group at the same or a better grade (S = 0).
					int kind = e / 100_000_000, slot = e / 10_000_000 % 10, grade = e / 1_000_000 % 10, group = e % 1_000_000;
					if (!available.Any(a => a / 100_000_000 == kind && a / 10_000_000 % 10 == slot && a % 1_000_000 == group && a / 1_000_000 % 10 <= grade)) return false;
				}
			}
			return true;
		}

		private static Lineup Clone(Lineup l) => new Lineup { Batters = (int[])l.Batters.Clone(), Fielders = (byte[])l.Fielders.Clone(), Pitchers = (int[])l.Pitchers.Clone(), NextSp = l.NextSp };

		// The best 1군 by each card's own value under the cap (λ·balls slot by slot, then the rest spent on value per ball),
		// members of `group` (if any) worth `groupBonus` more a slot. The positions of the 1군 of now (its Fielders) are kept.
		private static Lineup Greedy(LineupContext x, Dictionary<int, CardValue> values, Lineup shape, Func<CardInfo, bool> group, double groupBonus)
		{
			int[] fillBat = Enumerable.Range(0, 9).OrderBy(k => Array.IndexOf(new[] { 0, 4, 2, 6, 3, 7, 5, 1, 8 }, Math.Max(0, PositionOf(shape, k)))).Concat(Enumerable.Range(9, 5)).ToArray();
			int[] fillPit = { 0, 1, 2, 3, 4, 10, 9, 5, 6, 7, 8 };
			double V(CardValue c, Lineup l, int k, bool pit) => SlotValue(c, l, k, pit) + (group != null && group(c.Info) ? groupBonus : 0);
			Lineup Assign(double lambda)
			{
				Lineup l = Clone(shape);
				Array.Clear(l.Batters);
				Array.Clear(l.Pitchers);
				HashSet<int> people = new HashSet<int>();
				int legendBat = 0, legendPit = 0;
				bool Take(CardValue c, bool pit)
				{
					if (people.Contains(PersonOf(c.Info))) return false;
					if (c.Info.IsLegend && (pit ? legendPit : legendBat) >= 1) return false;
					return true;
				}
				void Took(CardValue c, bool pit)
				{
					people.Add(PersonOf(c.Info));
					if (c.Info.IsLegend) { if (pit) legendPit++; else legendBat++; }
				}
				foreach (int k in fillBat)
				{
					CardValue best = null;
					double bs = double.NegativeInfinity;
					foreach (CardValue c in values.Values.Where(c => c.Info.IsBatter))
					{
						if (!Take(c, false)) continue;
						double sc = V(c, l, k, false) - lambda * c.Info.Ball;
						if (sc > bs) { bs = sc; best = c; }
					}
					if (best == null) { if (k < 9) return null; continue; }
					l.Batters[k] = best.Info.Idx;
					Took(best, false);
				}
				foreach (int k in fillPit)
				{
					CardValue best = null;
					double bs = double.NegativeInfinity;
					foreach (CardValue c in values.Values.Where(c => !c.Info.IsBatter))
					{
						if (!Take(c, true) || (k < 5 && x.Blocked.Contains(c.Info.Idx))) continue;
						double sc = V(c, l, k, true) - lambda * c.Info.Ball;
						if (sc > bs) { bs = sc; best = c; }
					}
					if (best == null) { if (k < 5) return null; continue; }
					l.Pitchers[k] = best.Info.Idx;
					Took(best, true);
				}
				return l;
			}
			double lo = 0, hi = 5;
			Lineup fit = Assign(hi);
			if (fit == null || RosterHandlers.LineupBalls(fit) > x.Cap) return null;
			for (int it = 0; it < 25; it++)
			{
				double mid = (lo + hi) / 2;
				Lineup d = Assign(mid);
				if (d != null && RosterHandlers.LineupBalls(d) <= x.Cap) { fit = d; hi = mid; } else lo = mid;
			}
			return fit;
		}

		// Single changes valued whole until none helps: each slot's likely replacements (by own value, then by value per
		// ball, then the members of the team colours the 1군 has), and every swap of two slots.
		private static (Lineup l, TeamValue.Parts v) Improve(LineupContext x, Dictionary<int, CardValue> values, Lineup start, int[] keep, int maxEvals)
		{
			Lineup best = Clone(start);
			TeamValue.Parts bv = Evaluate(x, best, out int[] colours);
			bool better = true;
			while (better && x.Evaluations < maxEvals)
			{
				better = false;
				List<Lineup> tries = new List<Lineup>();
				HashSet<int> inside = best.Batters.Concat(best.Pitchers).Where(i => i != 0).ToHashSet();
				(Dictionary<int, int> colourBonus, _) = TeamColors.AutoBonuses(best);
				for (int side = 0; side < 2; side++)
				{
					bool pit = side == 1;
					int[] slots = pit ? best.Pitchers : best.Batters;
					for (int k = 0; k < slots.Length; k++)
					{
						values.TryGetValue(slots[k], out CardValue now);
						double cur = now == null ? double.NegativeInfinity : SlotValue(now, best, k, pit);
						IEnumerable<CardValue> outside = values.Values.Where(c => c.Info.IsBatter != pit && !inside.Contains(c.Info.Idx));
						List<CardValue> byValue = outside.OrderByDescending(c => SlotValue(c, best, k, pit)).Take(5).ToList();
						List<CardValue> byBall = outside.Where(c => SlotValue(c, best, k, pit) > cur).OrderBy(c => c.Info.Ball).Take(3).ToList();
						// Cards of the groups the 1군's team colours count (a member in for a non-member keeps or lifts a grade).
						List<CardValue> members = colourBonus.Count == 0 ? new List<CardValue>() : outside
							.Where(c => values.ContainsKey(c.Info.Idx) && SameGroupAsColoured(c.Info, best, colourBonus)).OrderByDescending(c => SlotValue(c, best, k, pit)).Take(3).ToList();
						foreach (CardValue c in byValue.Concat(byBall).Concat(members).Distinct())
						{
							Lineup t = Clone(best);
							if (pit) t.Pitchers[k] = c.Info.Idx; else t.Batters[k] = c.Info.Idx;
							tries.Add(t);
						}
					}
					for (int i = 0; i < slots.Length; i++)
					{
						for (int j = i + 1; j < slots.Length; j++)
						{
							if (!pit && i >= 9 && j >= 9) continue;   // two bench slots: the same
							if (slots[i] == 0 && slots[j] == 0) continue;
							Lineup t = Clone(best);
							int[] s = pit ? t.Pitchers : t.Batters;
							(s[i], s[j]) = (s[j], s[i]);
							tries.Add(t);
						}
					}
				}
				foreach (Lineup t in tries)
				{
					if (x.Evaluations >= maxEvals) break;
					if (!Allowed(x, t, keep)) continue;
					TeamValue.Parts v = Evaluate(x, t, out _);
					if (v.Runs > bv.Runs + 1e-4)
					{
						best = t;
						bv = v;
						better = true;
						break;
					}
				}
			}
			return (best, bv);
		}

		// A card of the same season and club, or club and decade, as a card the team colour bonus counts.
		private static bool SameGroupAsColoured(CardInfo c, Lineup l, Dictionary<int, int> colourBonus)
		{
			foreach (int idx in colourBonus.Keys)
			{
				CardInfo m = s_data.Catalog.Get(idx);
				if (m == null) continue;
				if (m.TeamCode == c.TeamCode && m.Year == c.Year) return true;
				if (TeamColors.Lineage(m.TeamCode) == TeamColors.Lineage(c.TeamCode) && m.Year / 10 == c.Year / 10) return true;
			}
			return false;
		}

		public static AutoLineupResult AutoLineup(uint userCode, bool keepColour, int maxEvals = 4000)
		{
			LineupContext x = Context(userCode);
			if (x == null || x.Current == null) return null;
			Dictionary<int, CardValue> values = CardValues(x);
			AutoLineupResult r = new AutoLineupResult { Current = x.Current, Cap = x.Cap, CurrentBalls = RosterHandlers.LineupBalls(x.Current) };
			r.CurrentParts = Evaluate(x, x.Current, out r.CurrentColours);
			r.CurrentRuns = r.CurrentParts.Runs;
			int[] keep = keepColour ? TeamColors.Registered(userCode) : null;

			// Starts: the 1군 of now (when it keeps the rules), the best by own values, and round the biggest groups.
			List<(string name, Lineup l)> starts = new List<(string, Lineup)>();
			if (Allowed(x, x.Current, keep)) starts.Add(("지금 1군", x.Current));
			Lineup free = Greedy(x, values, x.Current, null, 0);
			if (free != null && Allowed(x, free, keep)) starts.Add(("능력 순", free));
			if (!keepColour)
			{
				var seasons = values.Values.GroupBy(c => (c.Info.TeamCode, c.Info.Year)).Where(g => g.Count() >= 11).OrderByDescending(g => g.Count()).Take(3);
				foreach (var g in seasons)
				{
					Lineup l = Greedy(x, values, x.Current, c => c.TeamCode == g.Key.TeamCode && c.Year == g.Key.Year, 0.6);
					if (l != null && Allowed(x, l, null)) starts.Add(($"'{g.Key.Year % 100:00} {TeamColors.TeamName(g.Key.TeamCode, g.Key.Year)}", l));
				}
				var decades = values.Values.GroupBy(c => (line: TeamColors.Lineage(c.Info.TeamCode), dec: c.Info.Year / 10)).Where(g => g.Count() >= 11).OrderByDescending(g => g.Count()).Take(2);
				foreach (var g in decades)
				{
					Lineup l = Greedy(x, values, x.Current, c => TeamColors.Lineage(c.TeamCode) == g.Key.line && c.Year / 10 == g.Key.dec, 0.4);
					if (l != null && Allowed(x, l, null)) starts.Add(($"{TeamColors.LineageName(g.Key.line)} {g.Key.dec % 10 * 10}년대", l));
				}
			}
			if (starts.Count == 0)
			{
				r.Lineup = x.Current;
				r.Parts = r.CurrentParts;
				r.Runs = r.CurrentRuns;
				r.Colours = r.CurrentColours;
				return r;
			}
			int perStart = Math.Max(200, maxEvals / starts.Count);
			(Lineup l, TeamValue.Parts v, string name) win = (null, null, null);
			foreach ((string name, Lineup l) in starts)
			{
				int budget = x.Evaluations + perStart;
				(Lineup best, TeamValue.Parts bv) = Improve(x, values, l, keep, budget);
				if (win.l == null || bv.Runs > win.v.Runs) win = (best, bv, name);
			}
			// The batting order of the nine by the 선수분석 graph, the positions going with the players.
			Lineup ordered = Reorder(win.l);
			if (ordered != null && Allowed(x, ordered, keep))
			{
				TeamValue.Parts ov = Evaluate(x, ordered, out _);
				if (ov.Runs >= win.v.Runs - 1e-4) win = (ordered, ov, win.name);
			}
			r.Lineup = win.l;
			r.Parts = win.v;
			r.Runs = win.v.Runs;
			r.Start = win.name;
			Evaluate(x, win.l, out r.Colours);
			r.Balls = RosterHandlers.LineupBalls(win.l);
			r.StartersOut = x.Current.Pitchers.Take(5).Where(i => i != 0 && !win.l.Pitchers.Take(5).Contains(i)).ToList();
			r.Evaluations = x.Evaluations;
			return r;
		}

		// The nine starters in LineupBuilder.BestOrder's order, each keeping his position.
		private static Lineup Reorder(Lineup l)
		{
			List<CardInfo> starters = l.Batters.Take(9).Select(i => s_data.Catalog.Get(i)).ToList();
			if (starters.Any(c => c == null)) return null;
			int[] posOf = Enumerable.Range(0, 9).Select(k => PositionOf(l, k)).ToArray();
			List<CardInfo> order = LineupBuilder.BestOrder(starters);
			Lineup r = Clone(l);
			for (int k = 0; k < 9; k++)
			{
				int from = starters.IndexOf(order[k]);
				r.Batters[k] = order[k].Idx;
				if (posOf[from] >= 0) r.Fielders[posOf[from]] = (byte)(k + 1);
			}
			return r;
		}

		// Dev: --autolineup <team name> [keep] on a copy of the data; AUTOLINEUP_SIM=<games a club> checks it in full games.
		public static int AutoLineupTest(string teamName, bool keep, string dbPath)
		{
			uint user = 0;
			string cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection(cs))
			{
				c.Open();
				using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT user_code FROM team WHERE team_name = $n";
				cmd.Parameters.AddWithValue("$n", teamName);
				object o = cmd.ExecuteScalar();
				if (o != null) user = Convert.ToUInt32(o);
			}
			System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
			AutoLineupResult a = user != 0 ? AutoLineup(user, keep) : null;
			if (a == null)
			{
				ServerLog.Warn($"[autolineup] no league or lineup for '{teamName}'");
				return 2;
			}
			string Colour(int[] e) => string.Join(", ", e.Where(i => i != 0).Select(i => $"{TeamColors.Label(i).name} {"SABC"[i / 1_000_000 % 10]}"));
			ServerLog.Info($"[autolineup] {teamName} (user {user}), keep colour {keep}, {a.Evaluations} lineups valued in {watch.ElapsedMilliseconds} ms, best from {a.Start}");
			ServerLog.Info($"[autolineup] now    {a.CurrentBalls}/{a.Cap}볼, {a.CurrentRuns:+0.000;-0.000} runs a game | {Colour(a.CurrentColours)} | {LineupEffects.Describe(a.Current, s_data.Catalog)}");
			ServerLog.Info($"[autolineup] advice {a.Balls}/{a.Cap}볼, {a.Runs:+0.000;-0.000} runs a game ({100 * a.Gain:+0.00;-0.00}%p) | {Colour(a.Colours)} | {LineupEffects.Describe(a.Lineup, s_data.Catalog)}");
			ServerLog.Info($"[autolineup]   now    {a.CurrentParts}");
			ServerLog.Info($"[autolineup]   advice {a.Parts}");
			if (a.StartersOut.Count > 0) ServerLog.Info($"[autolineup]   starters out (선발 5경기 제한): {string.Join(", ", a.StartersOut.Select(i => s_data.Catalog.Get(i)?.Name ?? i.ToString()))}");
			string[] roles = { "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP" };
			string Pitchers(Lineup l) => string.Join("  ", Enumerable.Range(0, 11).Select(k =>
			{
				CardInfo c = s_data.Catalog.Get(l.Pitchers[k]);
				return c == null ? $"{roles[k]} -" : $"{roles[k]} {c.Year % 100:00}{c.Name}({c.MainPosition},{c.Ball}볼,체{s_data.Card(c.Idx, 2)?.Row?.R[0]:F0})";
			}));
			string Batters(Lineup l) => string.Join("  ", Enumerable.Range(0, 14).Select(k =>
			{
				CardInfo c = s_data.Catalog.Get(l.Batters[k]);
				int pos = PositionOf(l, k);
				string at = pos < 0 ? "벤치" : new[] { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" }[pos];
				return c == null ? $"{at} -" : $"{at} {c.Year % 100:00}{c.Name}({c.Ball})";
			}));
			ServerLog.Info($"[autolineup]   now    투수 {Pitchers(a.Current)}");
			ServerLog.Info($"[autolineup]   advice 투수 {Pitchers(a.Lineup)}");
			ServerLog.Info($"[autolineup]   now    타자 {Batters(a.Current)}");
			ServerLog.Info($"[autolineup]   advice 타자 {Batters(a.Lineup)}");
			if (int.TryParse(Environment.GetEnvironmentVariable("AUTOLINEUP_SIM"), out int games) && games > 0)
			{
				watch.Restart();
				// Also the advice's batters with the pitchers of now, and its pitchers with the batters of now.
				Lineup bats = Clone(a.Current), pits = Clone(a.Current);
				bats.Batters = (int[])a.Lineup.Batters.Clone();
				bats.Fielders = (byte[])a.Lineup.Fielders.Clone();
				pits.Pitchers = (int[])a.Lineup.Pitchers.Clone();
				List<Lineup> set = new List<Lineup> { a.Current, a.Lineup, bats, pits };
				double[] w = LineupSimCheck(user, set, games);
				LineupContext x = Context(user);
				string[] names = { "now", "advice", "batters", "pitchers" };
				for (int k = 0; k < set.Count; k++)
				{
					double tv = Evaluate(x, set[k], out _).Runs;
					ServerLog.Info($"[autolineup] sim {names[k],-8} {100 * w[k]:F2}% ({100 * (w[k] - w[0]):+0.00;-0.00}%p) | TeamValue {tv:+0.000;-0.000} runs ({100 * (tv - a.CurrentRuns) * TeamValue.WinPerRun:+0.00;-0.00}%p)");
				}
				ServerLog.Info($"[autolineup] sim: {games} games against each club, {watch.Elapsed.TotalSeconds:F0} s");
			}
			return 0;
		}

		// Dev check: the club with each 1군 (as AutoLineup values it: team colour, development, condition) against every other
		// club, `games` home games each, the same seeds for every 1군.
		public static double[] LineupSimCheck(uint userCode, List<Lineup> lineups, int games, int threads = 4)
		{
			LineupContext x = Context(userCode);
			int[] opps = Enumerable.Range(0, x.Teams.Length).Where(j => j != x.Me).ToArray();
			GameEnv env = s_data.Env;
			LeagueState s = x.S;
			TraitEffects.Effect[] traits = s.Clubs.Select(c => c.IsHuman ? TraitEffects.Of(c.Owner) : TraitEffects.FromPoints(AiTeamOf(s, c)?.Traits ?? new Dictionary<byte, byte>())).ToArray();
			double[] wins = new double[lineups.Count];
			for (int k = 0; k < lineups.Count; k++)
			{
				GameTeam mine = BuildTeam(x, lineups[k], out _);
				(byte[] off, byte[] def) = OperationHandlers.TeamOps(userCode);
				mine.Tactics = GameTactics.From(off, def);
				double w = 0;
				object gate = new object();
				System.Threading.Tasks.Parallel.For(0, opps.Length * games, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = threads }, n =>
				{
					int j = opps[n / games];
					// Both rotations turn over (a team's StarterSlot is one game's: every game was SP1 against the same starter).
					GameTeam home = CopyTeam(mine), away = CopyTeam(x.Teams[j]);
					home.StarterSlot = n % 5;
					away.StarterSlot = (n / 5) % 5;
					SeasonModel m = CardGame.Build(home, away, env);
					TraitEffects.Apply(m, 0, x.Traits);
					TraitEffects.Apply(m, 1, traits[j]);
					SeasonSimulator sim = new SeasonSimulator(m, new EngineParams { RunEnvironment = env.RunEnvironment, ReachOnError = env.ReachOnError }, (ulong)(n + 1) * 7919);
					SimStats st = sim.PlayOne(0, 1, null);
					long r0 = m.TeamBatters[0].Sum(i => st.Bat(i, BS.R)), r1 = m.TeamBatters[1].Sum(i => st.Bat(i, BS.R));
					double v = r0 > r1 ? 1 : r0 == r1 ? 0.5 : 0;
					lock (gate) w += v;
				});
				wins[k] = w / (opps.Length * games);
			}
			return wins;
		}
	}
}
