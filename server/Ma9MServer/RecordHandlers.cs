using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 팀 관리 > 수훈선수 (ValuablePlayer_Main) and the player window's 최근성적 rows, from the league's
	// season and per-game lines (user's request, 2026-10-19).
	//
	// 기록실 > 선수기록 (the club's season lines), 선수스탯 (contract/lesson columns) and 순위표 > 선수순위
	// (the league's top three per category) come from the same lines.
	//
	// 수훈선수: the club's leader in five batting and five pitching categories this season. Batting
	// average needs 3.1 plate appearances per team game and ERA one inning per team game (KBO 규정타석/
	// 규정이닝); while nobody qualifies yet the bar halves, then drops. Before the first game the
	// screen shows the lineup's first five batters and pitchers with zeros.
	public static class RecordHandlers
	{
		private const int RecentCount = 5;

		private static Database s_db;

		private static CardCatalog s_catalog;

		public static void Register(GameLoop loop, Database db, CardCatalog catalog)
		{
			s_db = db;
			s_catalog = catalog;
			loop.Register<Ma9MPacket.PKS_CS_ENTER_MVP>(EnterMvp);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_PLAYERS_RECORD>((session, packet) => PlayersRecord(session));
			loop.Register<Ma9MPacket.PKS_CS_ENTER_PLAYERS_RECORD_PITCHER>((session, packet) => PlayersRecord(session));
			loop.Register<Ma9MPacket.PKS_CS_ENTER_PLAYERS_STAT>(PlayersStat);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TEAM_PLAYER_RANK>(PlayerRank);
		}

		// 기록실 > 선수기록: every season line of the club, batters and pitchers in one reply (the client
		// clears both lists on each reply and asks again when the pitcher tab first opens).
		private static void PlayersRecord(GameSession session)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_ENTER_PLAYERS_RECORD reply = BuildPlayersRecord(user);
			session.Send(reply);
		}

		public static Ma9MPacket.PKS_SC_ENTER_PLAYERS_RECORD BuildPlayersRecord(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			List<PlayerSeason> lines = s != null ? LeagueService.Store.PlayerLines(user, s.Season, s.MyIndex) : new List<PlayerSeason>();
			Dictionary<int, TeamCard> owned = s_db.GetCards(user).ToDictionary(c => c.Idx);
			(Dictionary<int, int> best, Dictionary<int, int> worst) = s != null ? BestWorst(user, s, s.MyIndex) : (new Dictionary<int, int>(), new Dictionary<int, int>());
			Ma9MPacket.PKS_SC_ENTER_PLAYERS_RECORD reply = new Ma9MPacket.PKS_SC_ENTER_PLAYERS_RECORD();
			reply.batter_record.array = lines.Where(p => !p.Pitcher && p.PA > 0).OrderByDescending(p => p.PA).Select(p =>
			{
				BATTER_RECORD r = new BATTER_RECORD();
				SeasonStats.FillBatter(r, p, p.CardIdx);
				r.best.n = B(best.GetValueOrDefault(p.CardIdx));
				r.worst.n = B(worst.GetValueOrDefault(p.CardIdx));
				return r;
			}).ToArray();
			List<PlayerSeason> arms = lines.Where(p => p.Pitcher && p.G > 0).OrderByDescending(p => p.Outs).ToList();
			reply.pitcher_record.array = arms.Select(p =>
			{
				PITCHER_RECORD r = new PITCHER_RECORD();
				SeasonStats.FillPitcher(r, p, p.CardIdx);
				r.best.n = B(best.GetValueOrDefault(p.CardIdx));
				r.worst.n = B(worst.GetValueOrDefault(p.CardIdx));
				return r;
			}).ToArray();
			// One entry per pitcher record (the client reads them side by side).
			reply.pitcher_position_training_info.array = arms.Select(p => new PByte { n = owned.TryGetValue(p.CardIdx, out TeamCard c) ? c.PositionTrainingInfo : (byte)0 }).ToArray();
			return reply;
		}

		// 최고/최악: how often the box score named each card its side's best or worst player, in the club's
		// confirmed games this season (an unseen game must not show up in the counts).
		public static (Dictionary<int, int> best, Dictionary<int, int> worst) BestWorst(uint user, LeagueState s, int club)
		{
			Dictionary<int, int> best = new Dictionary<int, int>(), worst = new Dictionary<int, int>();
			foreach (LeagueGame g in LeagueService.Store.Games(user, s.Season, club: club).Where(g => g.Confirmed))
			{
				int side = g.Home == club ? 0 : 1;
				best[g.Detail.BestIdx[side]] = best.GetValueOrDefault(g.Detail.BestIdx[side]) + 1;
				worst[g.Detail.WorstIdx[side]] = worst.GetValueOrDefault(g.Detail.WorstIdx[side]) + 1;
			}
			return (best, worst);
		}

		// One card's 최고/최악 counts in the player window's 상세기록 (user, 2026-10-21: they stayed 0 there).
		public static void FillBestWorst(uint user, PlayerSeason line, BATTER_RECORD batter, PITCHER_RECORD pitcher)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			if (s == null || line == null)
			{
				return;
			}
			(Dictionary<int, int> best, Dictionary<int, int> worst) = BestWorst(user, s, line.Club);
			byte b = B(best.GetValueOrDefault(line.CardIdx)), w = B(worst.GetValueOrDefault(line.CardIdx));
			if (line.Pitcher)
			{
				pitcher.best.n = b;
				pitcher.worst.n = w;
			}
			else
			{
				batter.best.n = b;
				batter.worst.n = w;
			}
		}

		// 기록실 > 선수스탯: per-card 재계약 count and 특강 lesson, in the roster list's order (the client
		// then asks for ROSTER_PLAYER and reads these side by side). The 재계약 count is the card's renewals (2026-11-09, AI
		// tester round 5: it stayed 0 after six); the lesson column stays empty.
		private static void PlayersStat(GameSession session, Ma9MPacket.PKS_CS_ENTER_PLAYERS_STAT packet)
		{
			if (session.Team == null)
			{
				return;
			}
			List<TeamCard> cards = s_db.GetCards(session.Team.UserCode);
			int n = cards.Count;
			Ma9MPacket.PKS_SC_ENTER_PLAYERS_STAT reply = new Ma9MPacket.PKS_SC_ENTER_PLAYERS_STAT();
			reply.contract_count.array = cards.Select(c => new PByte { n = (byte)Math.Min(255, c.ContractCount) }).ToArray();
			reply.lesson_index.array = Enumerable.Range(0, n).Select(_ => new PInteger()).ToArray();
			session.Send(reply);
		}

		// 순위표 > 선수순위: the league's top three in 타율, 홈런, 타점, 안타, 도루 / 방어율, 탈삼진, 승,
		// 세이브, 홀드, then 출루율, 장타율, WHIP, 승률 (42 entries; the client indexes them 3 × category + rank). 타율 and 방어율 use
		// the qualifying bars of 수훈선수.
		private static void PlayerRank(GameSession session, Ma9MPacket.PKS_CS_ENTER_TEAM_PLAYER_RANK packet)
		{
			if (session.Team == null)
			{
				return;
			}
			Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK reply = BuildPlayerRank(session.Team.UserCode);
			if (reply != null)
			{
				session.Send(reply);
			}
		}

		public static Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK BuildPlayerRank(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			if (s == null)
			{
				return null;
			}
			List<(int club, int idx, int value)> ranks = LeagueLeaders(s, user);
			Dictionary<int, TeamCard> owned = s_db.GetCards(user).ToDictionary(c => c.Idx);
			DateTime day = DateTime.Now;  // the time, for 컨디션 회복제
			Dictionary<int, Dictionary<int, TeamCard>> others = new Dictionary<int, Dictionary<int, TeamCard>>();
			Dictionary<int, TeamCard> OthersOwned(int club) =>
				others.TryGetValue(club, out var o) ? o : others[club] = LeagueHandlers.OwnedBy(s.Clubs[club].Owner);
			Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK reply = new Ma9MPacket.PKS_SC_ENTER_TEAM_PLAYER_RANK();
			reply.team_name.array = s.Clubs.Select(c => new PString { str = c.Name }).ToArray();
			reply.players.array = ranks.Select(r => new PUICardInfoMVP
			{
				// Another club's card with its owner's 포지션 변경 (2026-11-08); each club's cards read once.
				ui_info = r.club == s.MyIndex ? Card(user, r.idx, owned, day) : LeagueHandlers.CardOf(s.Clubs[r.club].Owner, r.idx, day, OthersOwned(r.club)),
				user_num = new PByte { n = (byte)r.club },
				record = new PInteger { n = r.value }
			}).ToArray();
			reply.my_user_num.n = (byte)s.MyIndex;
			return reply;
		}

		// Forty-five (club, card, value) entries, three per category in the client's order (득점 last since 2026-11-08).
		public static List<(int club, int idx, int value)> LeagueLeaders(LeagueState s, uint user)
		{
			List<PlayerSeason> lines = LeagueService.Store.PlayerLines(user, s.Season);
			Dictionary<int, int> games = s.Clubs.ToDictionary(c => c.Index, c => c.W + c.L + c.D);
			List<PlayerSeason> bat = lines.Where(p => !p.Pitcher && p.PA > 0).ToList();
			List<PlayerSeason> pit = lines.Where(p => p.Pitcher && p.G > 0).ToList();
			List<PlayerSeason> qualBat = bat.Where(p => p.PA >= (int)(3.1 * games.GetValueOrDefault(p.Club))).ToList();
			List<PlayerSeason> qualPit = pit.Where(p => p.Outs >= 3 * games.GetValueOrDefault(p.Club)).ToList();
			if (qualBat.Count < 3) qualBat = bat.OrderByDescending(p => p.PA).Take(Math.Max(3, bat.Count / 3)).ToList();
			if (qualPit.Count < 3) qualPit = pit.OrderByDescending(p => p.Outs).Take(Math.Max(3, pit.Count / 3)).ToList();
			// Before the first game: the clubs' lineups, value 0.
			List<(int club, int idx)> batFill = s.Clubs.SelectMany(c => (LineupOf(c)?.Batters.Take(9) ?? Enumerable.Empty<int>()).Where(i => i != 0).Select(i => (c.Index, i))).ToList();
			List<(int club, int idx)> pitFill = s.Clubs.SelectMany(c => (LineupOf(c)?.Pitchers ?? Enumerable.Empty<int>()).Where(i => i != 0).Select(i => (c.Index, i))).ToList();
			List<(int club, int idx, int value)> list = new List<(int, int, int)>();
			void Add(IEnumerable<PlayerSeason> ordered, Func<PlayerSeason, int> value, List<(int club, int idx)> fill)
			{
				List<(int club, int idx, int value)> top = ordered.Take(3).Select(p => (p.Club, p.CardIdx, value(p))).ToList();
				for (int k = 0; top.Count < 3; k++)
				{
					(int club, int idx) f = fill.Count > 0 ? fill[k % fill.Count] : (s.MyIndex, 0);
					top.Add((f.club, f.idx, 0));
				}
				list.AddRange(top);
			}
			Add(qualBat.OrderByDescending(Avg).ThenByDescending(p => p.PA), p => (int)Math.Round(Avg(p) * 1000), batFill);
			Add(bat.OrderByDescending(p => p.HR).ThenByDescending(Avg), p => p.HR, batFill);
			Add(bat.OrderByDescending(p => p.RBI).ThenByDescending(Avg), p => p.RBI, batFill);
			Add(bat.OrderByDescending(p => p.H).ThenByDescending(Avg), p => p.H, batFill);
			Add(bat.OrderByDescending(p => p.SB).ThenByDescending(Avg), p => p.SB, batFill);
			Add(qualPit.OrderBy(Era).ThenByDescending(p => p.Outs), p => (int)Math.Round(Era(p) * 1000), pitFill);
			Add(pit.OrderByDescending(p => p.PSO).ThenBy(Era), p => p.PSO, pitFill);
			Add(pit.OrderByDescending(p => p.W).ThenBy(Era), p => p.W, pitFill);
			Add(pit.OrderByDescending(p => p.SV).ThenBy(Era), p => p.SV, pitFill);
			Add(pit.OrderByDescending(p => p.HLD).ThenBy(Era), p => p.HLD, pitFill);
			// After the original thirty (a 1.0.7 client reads those blindly), the rest of the 리그 시상 categories (user,
			// 2026-10-22: "시상하는 모든 기록이 다 보이게"): 출루율, 장타율 (×1000, 규정타석), WHIP (×1000, 규정이닝), 승률
			// (×1000, 10승 이상 as in LeagueAwards).
			Add(qualBat.OrderByDescending(Obp).ThenByDescending(p => p.PA), p => (int)Math.Round(Obp(p) * 1000), batFill);
			Add(qualBat.OrderByDescending(Slg).ThenByDescending(p => p.PA), p => (int)Math.Round(Slg(p) * 1000), batFill);
			Add(qualPit.OrderBy(Whip).ThenByDescending(p => p.Outs), p => (int)Math.Round(Whip(p) * 1000), pitFill);
			Add(pit.Where(p => p.W >= 10).OrderByDescending(WinPct).ThenByDescending(p => p.W), p => (int)Math.Round(WinPct(p) * 1000), pitFill);
			// 득점 (user, 2026-11-08: "순위표 타이틀 경쟁에 득점 부분도 추가"), category 14: a 1.0.35+ client shows it under 타점,
			// older ones skip what they do not know.
			Add(bat.OrderByDescending(p => p.R).ThenByDescending(Avg), p => p.R, batFill);
			return list;
		}

		// A real team's saved lineup (in a shared league also the other members'; the one it last played when it has none),
		// an AI club's stored one.
		private static Lineup LineupOf(LeagueClub c) => c.IsHuman ? s_db.GetLineup(c.Owner) ?? (c.Lineup.Length > 0 ? Lineup.Parse(c.Lineup) : null) : Lineup.Parse(c.Lineup);

		private static void EnterMvp(GameSession session, Ma9MPacket.PKS_CS_ENTER_MVP packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			List<(int idx, int value)> picks = Picks(user, out string note);
			// Page 2 (swipe, Ma9M client): slots 10-19, the sabermetric leaders.
			picks.AddRange(SaberPicks(user));
			Dictionary<int, TeamCard> owned = s_db.GetCards(user).ToDictionary(c => c.Idx);
			DateTime day = DateTime.Now;  // the time, for 컨디션 회복제
			Ma9MPacket.PKS_SC_ENTER_MVP reply = new Ma9MPacket.PKS_SC_ENTER_MVP();
			reply.cards.array = picks.Select(p => Card(user, p.idx, owned, day)).ToArray();
			reply.value.array = picks.Select(p => new PInteger { n = p.value }).ToArray();
			session.Send(reply);
			ServerLog.Info($"[{session.Id}] 수훈선수: {note}");
		}

		// Ten (card, value) pairs: batters 0-4, pitchers 5-9.
		public static List<(int idx, int value)> Picks(uint user, out string note)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			List<PlayerSeason> lines = s != null ? LeagueService.Store.PlayerLines(user, s.Season, s.MyIndex) : new List<PlayerSeason>();
			List<PlayerSeason> bat = lines.Where(p => !p.Pitcher && p.PA > 0).ToList();
			List<PlayerSeason> pit = lines.Where(p => p.Pitcher && p.G > 0).ToList();
			// Team games the user has seen (the players' lines only hold confirmed or opened games since
			// 2026-10-20), for the qualifying bars.
			LeagueClub mine = s == null ? null : LeagueService.Store.ConfirmedView(s).Clubs.ElementAtOrDefault(s.MyIndex);
			int teamGames = mine == null ? 0 : mine.W + mine.L + mine.D;
			note = $"{bat.Count} batters, {pit.Count} pitchers over {teamGames} games";

			// The client reads batters from slots 0-4 and pitchers from 5-9, in the title order:
			// 타율, 안타, 홈런, 도루, 타점 / 방어율, 탈삼진, 승, 홀드, 세이브.
			List<(int idx, int value)> picks = new List<(int, int)>();
			if (bat.Count > 0)
			{
				PlayerSeason avg = Qualified(bat, p => p.PA, (int)(3.1 * teamGames))
					.OrderByDescending(Avg).ThenByDescending(p => p.PA).First();
				picks.Add((avg.CardIdx, (int)Math.Round(Avg(avg) * 1000)));
				picks.Add(Most(bat, p => p.H, Avg));
				picks.Add(Most(bat, p => p.HR, Avg));
				picks.Add(Most(bat, p => p.SB, Avg));
				picks.Add(Most(bat, p => p.RBI, Avg));
			}
			else
			{
				picks.AddRange(Fallback(user, pitchers: false));
			}
			if (pit.Count > 0)
			{
				PlayerSeason era = Qualified(pit, p => p.Outs, 3 * teamGames)
					.OrderBy(Era).ThenByDescending(p => p.Outs).First();
				picks.Add((era.CardIdx, (int)Math.Round(Era(era) * 1000)));
				picks.Add(Most(pit, p => p.PSO, p => -Era(p)));
				picks.Add(Most(pit, p => p.W, p => -Era(p)));
				picks.Add(Most(pit, p => p.HLD, p => -Era(p)));
				picks.Add(Most(pit, p => p.SV, p => -Era(p)));
			}
			else
			{
				picks.AddRange(Fallback(user, pitchers: true));
			}

			return picks;
		}

		// 수훈선수 page 2 (user, 2026-10-19: "오른쪽으로 슬라이드 했을 때 WAR, wRC+, WHIP 같은 세이버메트릭스"):
		// batters 출루율, 장타율, OPS, wRC+, WAR; pitchers K/9, K/BB, WHIP, FIP, bWAR. Values are ×100
		// (WAR, WHIP, FIP, K/9, K/BB), ×1000 (OPS, 출루율, 장타율) or whole (wRC+). Rate stats use the
		// qualifying bars of page 1; WAR and wRC+ come from SeasonStats (this league's own season).
		public static List<(int idx, int value)> SaberPicks(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			List<PlayerSeason> lines = s != null ? LeagueService.Store.PlayerLines(user, s.Season, s.MyIndex) : new List<PlayerSeason>();
			List<PlayerSeason> bat = lines.Where(p => !p.Pitcher && p.PA > 0).ToList();
			List<PlayerSeason> pit = lines.Where(p => p.Pitcher && p.G > 0 && p.Outs > 0).ToList();
			// Team games the user has seen (the players' lines only hold confirmed or opened games since
			// 2026-10-20), for the qualifying bars.
			LeagueClub mine = s == null ? null : LeagueService.Store.ConfirmedView(s).Clubs.ElementAtOrDefault(s.MyIndex);
			int teamGames = mine == null ? 0 : mine.W + mine.L + mine.D;
			SeasonStats.LeagueContext ctx = SeasonStats.Context(user);
			double War(PlayerSeason p)
			{
				CardPos pos = s_catalog?.Get(p.CardIdx)?.MainPosition ?? CardPos.DH;
				double w = SeasonStats.Value(p, pos, ctx).war;
				return double.IsNaN(w) ? 0 : w;
			}
			int Wrc(PlayerSeason p) => SeasonStats.Value(p, s_catalog?.Get(p.CardIdx)?.MainPosition ?? CardPos.DH, ctx).wrcPlus;
			double Obp(PlayerSeason p) => p.AB + p.BB + p.HBP + p.SF > 0 ? (double)(p.H + p.BB + p.HBP) / (p.AB + p.BB + p.HBP + p.SF) : 0;
			double Slg(PlayerSeason p) => p.AB > 0 ? (double)(p.H + p.D + 2 * p.T + 3 * p.HR) / p.AB : 0;
			double Whip(PlayerSeason p) => (p.PH + p.PBB) * 3.0 / p.Outs;
			// FIP constant from the whole league's season: league ERA − league raw FIP.
			List<PlayerSeason> league = s != null ? LeagueService.Store.PlayerLines(user, s.Season).Where(p => p.Pitcher && p.Outs > 0).ToList() : new List<PlayerSeason>();
			int lgOuts = league.Sum(p => p.Outs);
			double fipC = lgOuts > 0 ? 27.0 * league.Sum(p => p.ER) / lgOuts - (13.0 * league.Sum(p => p.PHR) + 3.0 * league.Sum(p => p.PBB + p.PHBP) - 2.0 * league.Sum(p => p.PSO)) * 3.0 / lgOuts : 3.1;
			double Fip(PlayerSeason p) => (13.0 * p.PHR + 3.0 * (p.PBB + p.PHBP) - 2.0 * p.PSO) * 3.0 / p.Outs + fipC;
			double K9(PlayerSeason p) => p.PSO * 27.0 / p.Outs;
			double Kbb(PlayerSeason p) => p.PBB > 0 ? (double)p.PSO / p.PBB : p.PSO;
			List<(int idx, int value)> picks = new List<(int, int)>();
			if (bat.Count > 0)
			{
				// User's order (2026-10-19): 출루율, 장타율, OPS, wRC+, WAR.
				List<PlayerSeason> q = Qualified(bat, p => p.PA, (int)(3.1 * teamGames));
				PlayerSeason obp = q.OrderByDescending(Obp).First();
				picks.Add((obp.CardIdx, (int)Math.Round(Obp(obp) * 1000)));
				PlayerSeason slg = q.OrderByDescending(Slg).First();
				picks.Add((slg.CardIdx, (int)Math.Round(Slg(slg) * 1000)));
				PlayerSeason ops = q.OrderByDescending(p => Obp(p) + Slg(p)).First();
				picks.Add((ops.CardIdx, (int)Math.Round((Obp(ops) + Slg(ops)) * 1000)));
				PlayerSeason wrc = q.OrderByDescending(Wrc).ThenByDescending(p => p.PA).First();
				picks.Add((wrc.CardIdx, Wrc(wrc)));
				PlayerSeason war = bat.OrderByDescending(War).First();
				picks.Add((war.CardIdx, (int)Math.Round(War(war) * 100)));
			}
			else
			{
				picks.AddRange(Fallback(user, pitchers: false));
			}
			if (pit.Count > 0)
			{
				// User's order: K/9, K/BB, WHIP, FIP, bWAR.
				List<PlayerSeason> q = Qualified(pit, p => p.Outs, 3 * teamGames);
				PlayerSeason k9 = q.OrderByDescending(K9).First();
				picks.Add((k9.CardIdx, (int)Math.Round(K9(k9) * 100)));
				PlayerSeason kbb = q.OrderByDescending(Kbb).First();
				picks.Add((kbb.CardIdx, (int)Math.Round(Kbb(kbb) * 100)));
				PlayerSeason whip = q.OrderBy(Whip).ThenByDescending(p => p.Outs).First();
				picks.Add((whip.CardIdx, (int)Math.Round(Whip(whip) * 100)));
				PlayerSeason fip = q.OrderBy(Fip).ThenByDescending(p => p.Outs).First();
				picks.Add((fip.CardIdx, (int)Math.Round(Fip(fip) * 100)));
				PlayerSeason war = pit.OrderByDescending(War).First();
				picks.Add((war.CardIdx, (int)Math.Round(War(war) * 100)));
			}
			else
			{
				picks.AddRange(Fallback(user, pitchers: true));
			}
			return picks;
		}

		private static double Avg(PlayerSeason p) => p.AB > 0 ? (double)p.H / p.AB : 0;

		private static double Obp(PlayerSeason p) => p.AB + p.BB + p.HBP + p.SF > 0 ? (double)(p.H + p.BB + p.HBP) / (p.AB + p.BB + p.HBP + p.SF) : 0;

		private static double Slg(PlayerSeason p) => p.AB > 0 ? (double)(p.H + p.D + 2 * p.T + 3 * p.HR) / p.AB : 0;

		private static double Whip(PlayerSeason p) => p.Outs > 0 ? (p.PH + p.PBB) * 3.0 / p.Outs : 99.99;

		private static double WinPct(PlayerSeason p) => p.W + p.L > 0 ? (double)p.W / (p.W + p.L) : 0;

		private static double Era(PlayerSeason p) => p.Outs > 0 ? p.ER * 27.0 / p.Outs : (p.ER > 0 ? 99.99 : 0);

		// Players over the qualifying bar; while nobody is, half the bar, then everyone.
		private static List<PlayerSeason> Qualified(List<PlayerSeason> pool, Func<PlayerSeason, int> amount, int bar)
		{
			List<PlayerSeason> q = pool.Where(p => amount(p) >= bar).ToList();
			if (q.Count == 0) q = pool.Where(p => amount(p) >= bar / 2).ToList();
			return q.Count > 0 ? q : pool;
		}

		// The leader in a counting stat; ties go to the better rate.
		private static (int idx, int value) Most(List<PlayerSeason> pool, Func<PlayerSeason, int> stat, Func<PlayerSeason, double> tie)
		{
			PlayerSeason top = pool.OrderByDescending(stat).ThenByDescending(tie).First();
			return (top.CardIdx, stat(top));
		}

		// No league game yet: the lineup's first five batters or pitchers (padded with the first), value 0.
		private static IEnumerable<(int idx, int value)> Fallback(uint user, bool pitchers)
		{
			Lineup l = s_db.GetLineup(user);
			IEnumerable<int> order = l == null ? Enumerable.Empty<int>() : pitchers ? l.Pitchers : l.Batters;
			List<int> idx = order.Where(i => i != 0).Distinct().Take(5).ToList();
			while (idx.Count < 5)
			{
				idx.Add(idx.Count > 0 ? idx[0] : 0);
			}
			return idx.Select(i => (i, 0));
		}

		private static PUICardInfo Card(uint user, int idx, Dictionary<int, TeamCard> owned, DateTime day)
		{
			if (idx == 0)
			{
				return new PUICardInfo();
			}
			return owned.TryGetValue(idx, out TeamCard c)
				? new PUICardInfo(idx, Condition.For(user, idx, day), c.RemainContract, c.PositionTrainingInfo, (byte)(c.IsLock ? 2 : 1))
				: new PUICardInfo(idx, Condition.For(user, idx, day), RosterHandlers.StartContract);
		}

		// 선수 정보 > 성적 > 최근성적: the card's last five league games this season, newest first. The
		// client sums the rows for the bottom line; 타율/방어율 per row is the season figure after the game.
		// club: the card's club in the user's league when it is another club's (CardInfoHandlers.DetailOfOthers).
		public static void FillRecent(uint user, int idx, PArray<RECENT_BATTER_RECORD> batter, PArray<RECENT_PITCHER_RECORD> pitcher, int club = -1)
		{
			batter.array = Array.Empty<RECENT_BATTER_RECORD>();
			pitcher.array = Array.Empty<RECENT_PITCHER_RECORD>();
			LeagueState s = LeagueService.Store?.Load(user);
			if (s == null)
			{
				return;
			}
			List<PlayerGame> games = club >= 0 && club != s.MyIndex
				? LeagueService.Store.ClubRecentGames(user, s.Season, club, idx, RecentCount)
				: LeagueService.Store.RecentGames(user, s.Season, idx, RecentCount);
			batter.array = games.Where(g => !g.Line.Pitcher).Select(g => new RECENT_BATTER_RECORD
			{
				game_num = new PByte { n = B(g.Game) },
				// That game's own average (user, 2026-10-20: the season average after the game read as if
				// 4타수 1안타 were .363; the total row averages the five games itself).
				BA = new PFloat { f = g.Line.AB > 0 ? (float)g.Line.H / g.Line.AB : 0f },
				AB = new PByte { n = B(g.Line.AB) },
				Hit = new PByte { n = B(g.Line.H) },
				HR = new PByte { n = B(g.Line.HR) },
				RBI = new PByte { n = B(g.Line.RBI) },
				R = new PByte { n = B(g.Line.R) },
				BB = new PByte { n = B(g.Line.BB) },
				SB = new PByte { n = B(g.Line.SB) }
			}).ToArray();
			pitcher.array = games.Where(g => g.Line.Pitcher).Select(g => new RECENT_PITCHER_RECORD
			{
				game_num = new PByte { n = B(g.Game) },
				inning_by3 = new PByte { n = B(g.Line.Outs) },
				// That game's ERA (earned runs × 27 / outs; no out recorded with a run charged shows 99.99).
				ERA = new PFloat { f = g.Line.Outs > 0 ? 27f * g.Line.ER / g.Line.Outs : g.Line.ER > 0 ? 99.99f : 0f },
				// 1 승, 2 패, 3 세이브, 4 홀드 (PlayerInfo_SubWindow_Record).
				winlosesavehold = new PByte { n = (byte)(g.Line.W > 0 ? 1 : g.Line.L > 0 ? 2 : g.Line.SV > 0 ? 3 : g.Line.HLD > 0 ? 4 : 0) },
				NP = new PByte { n = B(g.Line.NP) },
				Hit = new PByte { n = B(g.Line.PH) },
				K = new PByte { n = B(g.Line.PSO) },
				R = new PByte { n = B(g.Line.PR) },
				ER = new PByte { n = B(g.Line.ER) }
			}).ToArray();
		}

		private static byte B(int v) => (byte)Math.Clamp(v, 0, 255);
	}
}
