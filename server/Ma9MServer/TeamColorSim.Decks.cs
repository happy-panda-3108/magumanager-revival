using System.Collections.Generic;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// 챔피언스 AI decks (systems session, 2026-10-28; AiTeams.Decks): the best single team-year deck of every team-year,
	// built as the --tcsim study builds its decks (Build: the best 25 cards under the cap, one card a player), and the
	// deck's value (runs a game against a league-average side) to rank the team-years by. The staff is then taken by
	// role (5 SP, 4 RP, SU, CP, the best of each by the same values), so the decks play nobody out of role.
	public static partial class TeamColorSim
	{
		public sealed class TeamYearDeck
		{
			public int Year, TeamCode;
			public List<CardInfo> Cards = new List<CardInfo>();
			public double Value;
			public int Balls;
		}

		private static readonly (CardPos role, int count)[] Staff = { (CardPos.SP, 5), (CardPos.RP, 4), (CardPos.SU, 1), (CardPos.CP, 1) };

		// Every team-year with a full deck: 14 batters who cover the eight fielding spots and a staff with every role.
		public static List<TeamYearDeck> TeamYearDecks(GameData data, int cap)
		{
			s_data = data;
			League league = data.Env.League;
			s_lgPerPa = PerPa(league.K, league.BB, league.HBP, league.HR, league.Babip, league.Double, league.Triple);
			List<Cand> all = data.Catalog.Drawable
				.Where(x => x.Year >= 1999 && TeamNames.ContainsKey(x.TeamCode) && x.Ball >= 1)
				.Select(x => Make(x)).Where(x => x != null).ToList();
			List<TeamYearDeck> decks = new List<TeamYearDeck>();
			foreach (IGrouping<(int year, int team), Cand> g in all.GroupBy(c => (c.Info.Year, c.Info.TeamCode)))
			{
				List<Cand> pool = g.ToList();
				if (Staff.Any(r => pool.Count(c => !c.Info.IsBatter && c.Info.MainPosition == r.role) < r.count)) continue;
				if (Enumerable.Range(0, 8).Any(p => !pool.Any(c => c.Info.CanField(p)))) continue;
				Deck d = Build($"{g.Key.year} {TeamNames[g.Key.team]}", pool, cap);
				if (d == null) continue;
				List<CardInfo> cards = d.At.Take(FirstPitcher).Select(c => c.Info).ToList();
				foreach ((CardPos role, int count) in Staff)
				{
					IEnumerable<Cand> ofRole = pool.Where(c => !c.Info.IsBatter && c.Info.MainPosition == role);
					cards.AddRange((role == CardPos.SP ? ofRole.OrderByDescending(c => c.Starter) : ofRole.OrderByDescending(c => c.Stuff)).Take(count).Select(c => c.Info));
				}
				if (cards.Select(c => (c.IsBatter, c.Sidx)).Distinct().Count() != Slots) continue;
				decks.Add(new TeamYearDeck { Year = g.Key.year, TeamCode = g.Key.team, Cards = cards, Value = d.Value, Balls = cards.Sum(c => c.Ball) });
			}
			return decks;
		}
	}
}
