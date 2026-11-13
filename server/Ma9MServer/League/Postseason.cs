using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	public enum PostSeries { WildCard = 0, SemiPlayoff = 1, Playoff = 2, KoreanSeries = 3 }

	// One postseason series: the higher seed against the lower, game by game in fixed slots.
	public sealed class SeriesState
	{
		public PostSeries Kind;
		public int FirstSlot, Slots, WinsNeeded;
		public int Advantage;               // wins the higher seed starts with (와일드카드: 1)
		public int High = -1, Low = -1;     // club indexes; -1 = not known yet
		public int HighWins, LowWins;       // games won (Advantage not included)
		public readonly List<LeagueGame> Games = new List<LeagueGame>();
		public int Winner = -1, Loser = -1;

		public bool Decided => Winner >= 0;
		public int LastSlot => FirstSlot + Slots - 1;
		public bool Has(int club) => club >= 0 && (High == club || Low == club);

		// Home games of the higher seed (KBO): every 와일드카드 game, games 1, 2, 5 of five, 1, 2, 6, 7 of seven (and the
		// 한국시리즈's games after 7, which only follow ties).
		public bool HighHome(int i) => Slots switch { 2 => true, 5 => i is 1 or 2 or 5, _ => i is 1 or 2 or >= 6 };
	}

	// The postseason of a league (user, 2026-10-21, KBO): 와일드카드 4위-5위 (4위 1승 안고 최대 2경기),
	// 준플레이오프 3위-WC 승자 5전3선승, 플레이오프 2위-준PO 승자 5전3선승, 한국시리즈 1위-PO 승자 7전4선승.
	// Games are numbered after the regular season by slot (145-163; LeagueCalendar.PostSlotTime): WC
	// slots 1-2, 준PO 3-7, PO 8-12, KS 13-19. A series that has its winner skips its remaining slots.
	// Ties (user, 2026-10-26: "한국시리즈 이전까지는 무승부가 안나오도록 … 한국시리즈는 연장 11회까지 하고도 무승부가
	// 나오면 이후에 8경기, 9경기"): before the 한국시리즈 a game plays on until someone wins (끝장승부, the board's
	// "X" column holding the 12th inning on; LeagueRules.EndlessInnings, LeagueService.PlayPostSlot); the 한국시리즈 goes to
	// 11 innings, a tie counts for neither side, and the series goes on in extra slots (LeagueCalendar.ExtraKsSlots)
	// until four wins. A series whose slots still run out undecided goes to the side with more wins, the higher seed
	// when level.
	public sealed class Bracket
	{
		public int[] Seeds = new int[LeagueRules.PostseasonClubs];   // club index by regular-season rank 1..5
		public SeriesState[] Series = new SeriesState[4];

		public SeriesState this[PostSeries k] => Series[(int)k];

		public SeriesState AtSlot(int slot) => Series.FirstOrDefault(x => slot >= x.FirstSlot && slot <= x.LastSlot);

		public int Champion => Series[3].Winner;

		// 1 champion, 2 KS loser, 3 PO loser, 4 준PO loser, 5 WC loser; 0 = not in it (or not decided yet).
		public int PlaceOf(int club)
		{
			if (club < 0) return 0;
			if (Series[3].Winner == club) return 1;
			if (Series[3].Loser == club) return 2;
			if (Series[2].Loser == club) return 3;
			if (Series[1].Loser == club) return 4;
			if (Series[0].Loser == club) return 5;
			return 0;
		}
	}

	public static class Postseason
	{
		private static LeagueStore Store => LeagueService.Store;

		// The first slot of each series: 와일드카드, 준플레이오프, 플레이오프, 한국시리즈 (Build).
		private static readonly int[] FirstSlots = { 1, 3, 8, 13 };

		// The series (0-3) a postseason game number belongs to, -1 none.
		public static int SeriesOf(int game)
		{
			int slot = SlotOf(game);
			if (slot < 1 || slot > LeagueCalendar.LastPostSlot) return -1;
			for (int k = FirstSlots.Length - 1; k >= 0; k--) if (slot >= FirstSlots[k]) return k;
			return -1;
		}

		public static int GameNumber(int slot) => LeagueRules.Games + slot;
		public static int SlotOf(int game) => game - LeagueRules.Games;
		public static bool IsPost(int game) => game > LeagueRules.Games;

		// The bracket from the regular-season standings and the postseason games given; slotsPassed = the
		// slots that are over (an undecided series whose slots are all over is settled on wins and seed).
		public static Bracket Build(IList<LeagueClub> ranked, IEnumerable<LeagueGame> postGames, int slotsPassed)
		{
			Bracket b = new Bracket();
			for (int i = 0; i < b.Seeds.Length; i++) b.Seeds[i] = i < ranked.Count ? ranked[i].Index : -1;
			b.Series[0] = new SeriesState { Kind = PostSeries.WildCard, FirstSlot = FirstSlots[0], Slots = 2, WinsNeeded = 2, Advantage = 1, High = b.Seeds[3], Low = b.Seeds[4] };
			b.Series[1] = new SeriesState { Kind = PostSeries.SemiPlayoff, FirstSlot = FirstSlots[1], Slots = 5, WinsNeeded = 3, High = b.Seeds[2] };
			b.Series[2] = new SeriesState { Kind = PostSeries.Playoff, FirstSlot = FirstSlots[2], Slots = 5, WinsNeeded = 3, High = b.Seeds[1] };
			b.Series[3] = new SeriesState { Kind = PostSeries.KoreanSeries, FirstSlot = FirstSlots[3], Slots = 7 + LeagueCalendar.ExtraKsSlots, WinsNeeded = 4, High = b.Seeds[0] };
			List<LeagueGame> games = postGames.Where(g => IsPost(g.Game)).OrderBy(g => g.Game).ToList();
			for (int k = 0; k < b.Series.Length; k++)
			{
				SeriesState sr = b.Series[k];
				if (k > 0) sr.Low = b.Series[k - 1].Winner;
				if (sr.High < 0 || sr.Low < 0) break;
				foreach (LeagueGame g in games.Where(g => SlotOf(g.Game) >= sr.FirstSlot && SlotOf(g.Game) <= sr.LastSlot))
				{
					sr.Games.Add(g);
					int high = g.Home == sr.High ? g.HomeScore : g.AwayScore, low = g.Home == sr.High ? g.AwayScore : g.HomeScore;
					if (high > low) sr.HighWins++;
					else if (low > high) sr.LowWins++;
				}
				if (sr.HighWins + sr.Advantage >= sr.WinsNeeded) sr.Winner = sr.High;
				else if (sr.LowWins >= sr.WinsNeeded) sr.Winner = sr.Low;
				else if (slotsPassed >= sr.LastSlot) sr.Winner = sr.LowWins > sr.HighWins + sr.Advantage ? sr.Low : sr.High;
				if (sr.Winner >= 0) sr.Loser = sr.Winner == sr.High ? sr.Low : sr.High;
			}
			return b;
		}

		// The bracket as it stands in the league's own data (every game played so far).
		public static Bracket Of(LeagueState s) => Build(s.Ranked(), Store.Games(s.UserCode, s.Season).Where(g => IsPost(g.Game)), s.PostSlot - 1);

		// The pairing of a slot that is due: (home, away, game in the series), or null when its series is
		// decided already (or its clubs are not known).
		public static (int home, int away, int i, SeriesState series)? Pairing(Bracket b, int slot)
		{
			SeriesState sr = b.AtSlot(slot);
			if (sr == null || sr.Decided || sr.High < 0 || sr.Low < 0) return null;
			int i = slot - sr.FirstSlot + 1;
			return sr.HighHome(i) ? (sr.High, sr.Low, i, sr) : (sr.Low, sr.High, i, sr);
		}

		// The user's next postseason game: its number and the opponent, or null when the user's club does
		// not play next (out, or waiting for a series that is not decided yet; user, 2026-10-21: no NEXT then).
		public static (int game, int opponent)? NextOf(LeagueState s)
		{
			if (!LeagueRules.HasPostseason(s.Tier) || s.NextGame <= LeagueRules.Games || s.NextTier >= 0) return null;
			Bracket b = Of(s);
			for (int slot = s.PostSlot; slot <= LeagueCalendar.LastPostSlot; slot++)
			{
				SeriesState sr = b.AtSlot(slot);
				if (sr == null || sr.Decided) continue;
				if (!sr.Has(s.MyIndex) || sr.High < 0 || sr.Low < 0) return null;
				return (GameNumber(slot), sr.High == s.MyIndex ? sr.Low : sr.High);
			}
			return null;
		}

		// The first slot of the series the user's club waits for: a higher seed whose opponent is not decided yet (1위 for the
		// 한국시리즈, 2위 for the 플레이오프, 3위 for the 준플레이오프), its first game in that slot whoever wins. null when the club
		// plays next (NextOf), is out, or the league has no postseason. 2026-11-13 (AI testers LG·kt·쌍방울: the top bar counted
		// to the next season's game 1, "12:00", while the 한국시리즈 began at 06:00; the season summary said 06:00).
		public static int? WaitingSlot(LeagueState s)
		{
			if (!LeagueRules.HasPostseason(s.Tier) || s.NextGame <= LeagueRules.Games || s.NextTier >= 0) return null;
			foreach (SeriesState sr in Of(s).Series)
			{
				if (!sr.Decided && sr.High == s.MyIndex && sr.Low < 0 && sr.FirstSlot >= s.PostSlot) return sr.FirstSlot;
			}
			return null;
		}

		public static void Register(GameLoop loop)
		{
			loop.Register<Ma9MPacket.PKS_CS_ENTER_POSTSEASON_INFO>(Info);
			loop.Register<Ma9MPacket.PKS_CS_POSTSEASON_SCOREBOARD>(Scoreboard);
		}

		// How long a postseason result is hidden after its slot starts (as the user's own league games).
		private static readonly TimeSpan ResultLock = TimeSpan.FromMinutes(5);

		// What the user may see now: the slots up to the first one with a game still hidden (the user's
		// game before it is confirmed, any game in its first 5 minutes).
		public static Bracket Visible(LeagueState s, DateTime now, out List<LeagueGame> shown)
		{
			int seen = LeagueHandlers.Seen(s);
			List<LeagueGame> post = Store.Games(s.UserCode, s.Season).Where(g => IsPost(g.Game)).OrderBy(g => g.Game).ToList();
			int through = s.PostSlot - 1;
			foreach (LeagueGame g in post)
			{
				if (g.Game > seen || now < s.SlotTime(g.Game) + ResultLock)
				{
					through = Math.Min(through, SlotOf(g.Game) - 1);
					break;
				}
			}
			shown = post.Where(g => SlotOf(g.Game) <= through).ToList();
			// Seeds from the standings as the user knows them (during the regular season, the bracket as it would be now).
			return Build(Store.ConfirmedView(s).Ranked(), shown, through);
		}

		// 0 정규시즌 중 (현재 순위로 짠 대진), 1 포스트시즌 중, 2 끝남, 3 포스트시즌이 없는 리그 (비기너).
		public static byte StateOf(LeagueState s, Bracket b)
		{
			if (!LeagueRules.HasPostseason(s.Tier)) return 3;
			if (s.NextGame <= LeagueRules.Games) return 0;
			return (byte)(b.Champion >= 0 ? 2 : 1);
		}

		private static void Info(GameSession session, Ma9MPacket.PKS_CS_ENTER_POSTSEASON_INFO packet)
		{
			LeagueState s = session.Team == null ? null : Store.Load(session.Team.UserCode);
			session.Send(BuildInfo(s, DateTime.Now));
		}

		public static Ma9MPacket.PKS_SC_ENTER_POSTSEASON_INFO BuildInfo(LeagueState s, DateTime now)
		{
			Ma9MPacket.PKS_SC_ENTER_POSTSEASON_INFO r = new Ma9MPacket.PKS_SC_ENTER_POSTSEASON_INFO();
			PString[] names = { r.team_name_1st, r.team_name_2nd, r.team_name_3rd, r.team_name_4th, r.team_name_wc_high, r.team_name_wc_low };
			foreach (PString n in names) n.str = string.Empty;
			foreach (PByte n in new[] { r.user_num_1st, r.user_num_2nd, r.user_num_3rd, r.user_num_4th, r.user_num_wc_high, r.user_num_wc_low,
				r.user_num_semi_po_win, r.user_num_po_win, r.user_num_final_win, r.user_num_wc_win })
			{
				n.n = byte.MaxValue;
			}
			PByte[] Scores(int length, Func<int, int> runs) => Enumerable.Range(0, length).Select(i => new PByte { n = (byte)Math.Min(255, runs(i)) }).ToArray();
			if (s == null)
			{
				r.result.n = 1;
				r.home_game_score_semi_po.array = r.away_game_score_semi_po.array = r.home_game_score_po.array = r.away_game_score_po.array = Scores(5, _ => 255);
				r.home_game_score_final.array = r.away_game_score_final.array = Scores(7, _ => 255);
				r.home_game_score_wc.array = r.away_game_score_wc.array = Scores(2, _ => 255);
				r.post_state.n = 3;
				return r;
			}
			Bracket b = Visible(s, now, out _);
			r.result.n = 0;
			r.season.n = (byte)s.Season;
			r.post_state.n = StateOf(s, b);
			void Club(int index, PByte num, PString name, PByte emblem)
			{
				if (index < 0) return;
				num.n = (byte)index;
				name.str = s.Clubs[index].Name;
				emblem.n = s.Clubs[index].Emblem;
			}
			SeriesState wc = b[PostSeries.WildCard], semi = b[PostSeries.SemiPlayoff], po = b[PostSeries.Playoff], ks = b[PostSeries.KoreanSeries];
			Club(b.Seeds[0], r.user_num_1st, r.team_name_1st, r.user_emblem_1st);
			Club(b.Seeds[1], r.user_num_2nd, r.team_name_2nd, r.user_emblem_2nd);
			Club(b.Seeds[2], r.user_num_3rd, r.team_name_3rd, r.user_emblem_3rd);
			// The original's four-club ladder has the 준PO's lower club fourth: the WC winner once known.
			Club(wc.Winner >= 0 ? wc.Winner : b.Seeds[3], r.user_num_4th, r.team_name_4th, r.user_emblem_4th);
			Club(b.Seeds[3], r.user_num_wc_high, r.team_name_wc_high, r.user_emblem_wc_high);
			Club(b.Seeds[4], r.user_num_wc_low, r.team_name_wc_low, r.user_emblem_wc_low);
			void Fill(SeriesState sr, PByte win, PByte first, PByte second, PArray<PByte> high, PArray<PByte> low)
			{
				win.n = sr.Winner >= 0 ? (byte)sr.Winner : byte.MaxValue;
				first.n = (byte)sr.HighWins;
				second.n = (byte)sr.LowWins;
				// The client's popup has seven game boxes: the 한국시리즈's games after a tie (8th on) count in the wins only.
				int shown = Math.Min(sr.Slots, 7);
				high.array = Scores(shown, i => i < sr.Games.Count ? (sr.Games[i].Home == sr.High ? sr.Games[i].HomeScore : sr.Games[i].AwayScore) : 255);
				low.array = Scores(shown, i => i < sr.Games.Count ? (sr.Games[i].Home == sr.High ? sr.Games[i].AwayScore : sr.Games[i].HomeScore) : 255);
			}
			Fill(wc, r.user_num_wc_win, r.score_wc_first, r.score_wc_second, r.home_game_score_wc, r.away_game_score_wc);
			Fill(semi, r.user_num_semi_po_win, r.score_semi_po_first, r.score_semi_po_second, r.home_game_score_semi_po, r.away_game_score_semi_po);
			Fill(po, r.user_num_po_win, r.score_po_first, r.score_po_second, r.home_game_score_po, r.away_game_score_po);
			Fill(ks, r.user_num_final_win, r.score_final_first, r.score_final_second, r.home_game_score_final, r.away_game_score_final);
			return r;
		}

		// The client asks by its own numbers: 145-163 (this server's), or the original's 109-125 (준PO 1 =
		// 109 ... KS 7 = 125; the 1.0.5 client's PostSeason_Popup), which are slots 3-19.
		public static int GameOfRequest(int n) => n >= 109 && n <= 125 ? n + (GameNumber(3) - 109) : n;

		private static void Scoreboard(GameSession session, Ma9MPacket.PKS_CS_POSTSEASON_SCOREBOARD packet)
		{
			LeagueState s = session.Team == null ? null : Store.Load(session.Team.UserCode);
			int game = GameOfRequest(packet.game_num.n);
			if (s == null || !IsPost(game))
			{
				session.Send(LeagueHandlers.EmptyBoard());
				return;
			}
			Bracket b = Visible(s, DateTime.Now, out List<LeagueGame> shown);
			LeagueGame g = shown.FirstOrDefault(x => x.Game == game);
			if (g == null)
			{
				session.Send(LeagueHandlers.EmptyBoard());
				return;
			}
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD r = LeagueHandlers.Board(s, g, g.Home == s.MyIndex || g.Away == s.MyIndex);
			// Prev / next within the series.
			SeriesState sr = b.AtSlot(SlotOf(game));
			int at = sr.Games.FindIndex(x => x.Game == game);
			r.prev_game_num.n = (byte)(at > 0 ? sr.Games[at - 1].Game : 0);
			r.next_game_num.n = (byte)(at >= 0 && at + 1 < sr.Games.Count ? sr.Games[at + 1].Game : 0);
			session.Send(r);
		}
	}
}
