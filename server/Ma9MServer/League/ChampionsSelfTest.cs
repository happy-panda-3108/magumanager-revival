using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// "--championstest" (systems session, 2026-10-28): 챔피언스 on a copy of the data directory (it writes games, MP and
	// seasons). The current cycle runs to its close; on the copy every 메이저 team then goes up whatever its place, so
	// real teams meet; then 편성 (one shared league, team-year decks in the free places), a whole season of it (every
	// member's copy the same, each with its own games to confirm and pay), its close by 챔피언스's rule, and the next 편성
	// (who stayed: a new shared league; who went down: a 메이저 league of its own).
	public static class ChampionsSelfTest
	{
		private static int s_problems;

		private static void Check(bool ok, string what)
		{
			if (!ok) s_problems++;
			ServerLog.Info($"{(ok ? "ok  " : "FAIL")} {what}");
		}

		private static void Tick(DateTime t)
		{
			System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
			LeagueService.Tick(t);
			ServerLog.Info($"     tick to {t:MM-dd HH:mm} in {sw.Elapsed.TotalSeconds:F1} s");
		}

		// Every member's copy holds the same clubs, records and games (scores by game and home club).
		private static string Differences(LeagueStore store, List<LeagueState> members)
		{
			LeagueState a = members[0];
			string Club(LeagueClub c) => $"{c.Index}:{c.Name}:{c.Owner}:{c.AiKey}:{c.W}-{c.L}-{c.D}:{c.RunsFor}-{c.RunsAgainst}:{c.Totals.H}:{c.Totals.HR}:{c.Totals.Outs}";
			string Games(LeagueState s) => string.Join(";", store.Games(s.UserCode, s.Season).Select(g => $"{g.Game}/{g.Home}/{g.Away}/{g.HomeScore}-{g.AwayScore}/{g.Detail?.WinIdx}"));
			string clubsA = string.Join("|", a.Clubs.Select(Club)), gamesA = Games(a);
			List<string> bad = new List<string>();
			foreach (LeagueState m in members.Skip(1))
			{
				if (string.Join("|", m.Clubs.Select(Club)) != clubsA) bad.Add($"{m.UserCode} clubs");
				if (Games(m) != gamesA) bad.Add($"{m.UserCode} games");
				if (m.NextGame != a.NextGame || m.PostSlot != a.PostSlot || m.LastGame != a.LastGame) bad.Add($"{m.UserCode} progress {m.NextGame}/{m.PostSlot}/{m.LastGame} vs {a.NextGame}/{a.PostSlot}/{a.LastGame}");
			}
			return bad.Count == 0 ? null : string.Join(", ", bad);
		}

		// Each member's own games wait for it (unconfirmed, unpaid, with MP), every other game is confirmed.
		private static bool OwnGamesPending(LeagueStore store, LeagueState s, bool confirmedAlready = false) => store.Games(s.UserCode, s.Season).All(g =>
		{
			bool mine = g.Home == s.MyIndex || g.Away == s.MyIndex;
			return !mine ? g.Confirmed && g.Mp == 0 : confirmedAlready ? g.Confirmed : !g.Confirmed && !g.Paid && g.Mp > 0;
		});

		private static string Table(LeagueState s) => string.Join(", ", s.Ranked().Select((c, i) => $"{i + 1}. {c.Name}{(c.IsHuman ? "(계정)" : string.Empty)} {c.W}-{c.L}-{c.D}"));

		public static int Run(Database db, GameData data, string dbPath)
		{
			string dir = System.IO.Path.GetDirectoryName(dbPath);
			TeamPropertyHandlers.Register(new GameLoop(), dir);
			LeagueService.Init(db, data, dbPath);
			InventoryHandlers.Register(new GameLoop(), db, data.Catalog, dir);
			RecordHandlers.Register(new GameLoop(), db, data.Catalog);
			OperationHandlers.Register(null, data.Catalog, dbPath);
			ContractHandlers.Register(null, db, data.Catalog, dir);
			TeamColors.Register(null, db, data.Catalog, dir);
			Missions.Register(null, db, dir);
			Achievements.Register(null, db, data.Catalog, dir);
			LeagueAwards.Init(db, dbPath);
			LeagueStore store = LeagueService.Store;
			List<(uint code, string name, byte emblem, byte grade)> teams = store.ActiveTeams();

			// 0. The tier and its decks.
			List<AiTeam> decks = AiTeams.Decks(LeagueRules.ChampionsTier);
			// The structure in force (five tiers, or six from the 11-11 season: 챔피언스 5, 24 CP).
			int top = LeagueRules.ChampionsTier;
			// Seven tiers (더블A, 2026-11-14): 챔피언스 26 CP, cap 160.
			Check(LeagueRules.Tiers == (LeagueRules.Seven ? 7 : LeagueRules.NewStructure ? 6 : 5) && LeagueRules.TierNames[top] == "챔피언스" && LeagueRules.TierGrade[top] == 9
				&& LeagueRules.TraitPoints[top] == (LeagueRules.Seven ? 26 : LeagueRules.NewStructure ? 24 : 23) && LeagueRules.BallCap[top] == (LeagueRules.Seven ? 160 : 155)
				&& LeagueRules.RewardMult[top] == (LeagueRules.NewStructure ? 2.6 : 2.5)
				&& LeagueAwards.Reward(LeagueAwards.TitleMp, top) == (LeagueRules.NewStructure ? 780_000 : 750_000),
				$"챔피언스 (tier {top} of {LeagueRules.Tiers}): grade 9 (CHAMPIONS slot, blue badge), {LeagueRules.TraitPoints[top]} CP, cap {LeagueRules.BallCap[top]}, MP ×{LeagueRules.RewardMult[top]}, 시상 ×{LeagueRules.RewardMult[top]}");
			Check(decks.Count == AiTeams.DeckPool && decks.Select(d => d.Name).Distinct().Count() == decks.Count && decks.All(d => d.Ball <= 155 && d.Development.Count == 25),
				$"{decks.Count} team-year decks: {string.Join(", ", decks.Select(d => $"{d.Name} {d.Ball}볼"))}");

			// 1. The current cycle to its close.
			DateTime now = DateTime.Now;
			DateTime start = LeagueCalendar.CycleStart(now);
			Tick(now);
			Tick(start + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1));
			Dictionary<uint, LeagueState> closed = teams.Select(t => store.Load(t.code)).Where(s => s != null && s.Start == start).ToDictionary(s => s.UserCode);
			foreach (LeagueState s in closed.Values)
			{
				int rank = s.RankOf(s.MyIndex), place = LeagueRules.HasPostseason(s.Tier) ? Postseason.Of(s).PlaceOf(s.MyIndex) : 0;
				Check(s.NextTier == LeagueRules.NextTier(s.Tier, rank, place),
					$"[{s.Clubs[s.MyIndex].Name}] {LeagueRules.TierNames[s.Tier]} #{rank}{(place > 0 ? $", 포스트시즌 {place}위" : string.Empty)} → {LeagueRules.TierNames[s.NextTier]}");
			}
			// On the copy, every 메이저 (and 챔피언스) team goes up, so that real teams meet.
			List<uint> going = closed.Values.Where(s => s.Tier >= LeagueRules.GateTier).Select(s => s.UserCode).OrderBy(x => x).ToList();
			foreach (uint code in going)
			{
				LeagueState s = closed[code];
				if (s.NextTier == LeagueRules.ChampionsTier) continue;
				s.NextTier = LeagueRules.ChampionsTier;
				store.Save(s);
			}
			Check(going.Count >= 2, $"{going.Count} team(s) going to 챔피언스: {string.Join(", ", going.Select(c => closed[c].Clubs[closed[c].MyIndex].Name))}");
			if (going.Count == 0) return 1;

			// 2. 편성 (59 h): one shared league.
			Tick(start + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1));
			DateTime next = start + LeagueCalendar.Cycle;
			List<LeagueState> members = going.Select(store.Load).ToList();
			LeagueState a = members[0];
			Check(members.All(m => m.Share != null && m.Share == a.Share && m.Tier == LeagueRules.ChampionsTier && m.Season == closed[m.UserCode].Season + 1
					&& m.Start == next && m.NextGame == 1 && m.FirstGame == 1 && m.NextTier == -1 && store.Games(m.UserCode, m.Season).Count == 0
					&& m.Clubs[m.MyIndex].LastRank == closed[m.UserCode].RankOf(closed[m.UserCode].MyIndex)),
				$"편성: shared {a.Share} from {next:MM-dd HH:mm}, members {string.Join(", ", members.Select(m => $"{m.UserCode} at {m.MyIndex} (season {m.Season})"))}");
			Check(members.Select(m => m.MyIndex).Distinct().Count() == members.Count
					&& members.All(m => m.Clubs.Count(c => c.IsUser) == 1 && m.Clubs[m.MyIndex].IsUser && m.Clubs[m.MyIndex].Owner == m.UserCode)
					&& members.All(m => members.All(o => m.Clubs[o.MyIndex].Owner == o.UserCode && m.Clubs[o.MyIndex].IsHuman)),
				"every member is the user's club in its own copy and a real team (its owner) in the others'");
			HashSet<(int, int)> own = members.SelectMany(m =>
			{
				Lineup l = db.GetLineup(m.UserCode);
				return AiTeams.TeamYearsOf(l.Batters.Concat(l.Pitchers).Where(i => i != 0));
			}).ToHashSet();
			List<LeagueClub> ai = a.Clubs.Where(c => !c.IsHuman).ToList();
			Check(ai.Count == LeagueRules.Clubs - members.Count && ai.All(c => c.AiKey >= 0 && AiTeams.Deck(LeagueRules.ChampionsTier, c.AiKey) != null && (c.Owner & 0x8000_0000u) != 0)
					&& ai.Select(c => c.AiKey).Distinct().Count() == ai.Count
					&& ai.All(c =>
					{
						Lineup l = Lineup.Parse(c.Lineup);
						return !own.Contains(AiTeams.TeamYearOf(l.Batters.Concat(l.Pitchers).Where(i => i != 0)));
					}),
				$"AI places: {string.Join(", ", ai.Select(c => c.Name))} (decks, none of the members' own team-years {string.Join("/", own.Select(o => $"{o.Item1} {o.Item2}"))})");
			string diff = Differences(store, members);
			Check(diff == null, $"members' copies the same at 편성{(diff == null ? string.Empty : ": " + diff)}");
			List<(uint code, string name, byte emblem, byte grade)> after = store.ActiveTeams();
			// The 편성 above may have been the six-tier switch (a season from 11-11): read the tiers again.
			int topCp = LeagueRules.TraitPoints[LeagueRules.ChampionsTier], gateCp = LeagueRules.TraitPoints[LeagueRules.GateTier];
			int topCap = LeagueRules.BallCap[LeagueRules.ChampionsTier];
			Check(members.All(m => after.First(t => t.code == m.UserCode).grade == 9 && RosterHandlers.MaxCostOf(m.UserCode) == topCap && TeamPropertyHandlers.AvailableCp(m.UserCode) == topCp),
				$"members: league grade 9 (CHAMPIONS slot), cap {topCap}, {topCp} CP ({LeagueRules.Tiers} tiers now)");
			// The lobby notes (NoteSpareCp since 11-15: "올라 … 남은 nCP") of the members who came up are seen now, as in the lobby; the
			// check after the next 편성 is about that 편성's notes only.
			foreach (LeagueState m in members) TeamPropertyHandlers.TakeTrimNotice(m.UserCode);

			// 3. The season's first two hours (6 slots), played once for every member.
			Tick(next + TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1));
			members = going.Select(store.Load).ToList();
			a = members[0];
			diff = Differences(store, members);
			Check(diff == null && a.NextGame == 7 && a.LastGame == 6 && store.Games(a.UserCode, a.Season).Count == 6 * LeagueRules.Clubs / 2,
				$"6 slots: every copy the same{(diff == null ? string.Empty : ": " + diff)}; {Table(a)}");
			Check(members.All(m => OwnGamesPending(store, m)), "each member's own games unconfirmed with their MP, the others' confirmed");
			List<LeagueGame> between = store.Games(a.UserCode, a.Season).Where(g => a.Clubs[g.Home].IsHuman && a.Clubs[g.Away].IsHuman).ToList();
			if (between.Count > 0)
			{
				LeagueGame g = between[0];
				LeagueState home = members.First(m => m.MyIndex == g.Home), away = members.First(m => m.MyIndex == g.Away);
				LeagueGame gh = store.Games(home.UserCode, home.Season, g.Game, withBuff: true).First(x => x.Home == g.Home);
				LeagueGame ga = store.Games(away.UserCode, away.Season, g.Game, withBuff: true).First(x => x.Home == g.Home);
				Check(!gh.Confirmed && !ga.Confirmed && gh.Mp == LeagueRules.Reward(LeagueRules.ChampionsTier, g.HomeScore, g.AwayScore) && ga.Mp == LeagueRules.Reward(LeagueRules.ChampionsTier, g.AwayScore, g.HomeScore)
						&& gh.HomeRuns == g.Detail.Stats[0][3] && ga.HomeRuns == g.Detail.Stats[1][3] && gh.Buff.Length == ga.Buff.Length,
					$"game {g.Game} between two members ({a.Clubs[g.Away].Name} {g.AwayScore}-{g.HomeScore} {a.Clubs[g.Home].Name}): both wait, MP {ga.Mp:N0} / {gh.Mp:N0}, 홈런볼 {ga.HomeRuns} / {gh.HomeRuns}");
			}
			else
			{
				ServerLog.Info("     no game between two members in the first 6 slots");
			}
			// Confirming is each member's own.
			LeagueState first = members[0];
			// Every game still waiting (the last season's catch-up too: confirming without a list takes them all).
			int due = Enumerable.Range(first.Season - 1, 2).Sum(season => store.Games(first.UserCode, season, unconfirmedOnly: true).Sum(g => g.Mp));
			int mp0 = db.AddMp(first.UserCode, 0);
			(int mp, int homeRuns) paid = LeagueService.ConfirmAndPay(first.UserCode, first.Season, null);
			int mp1 = db.AddMp(first.UserCode, 0);
			Check(paid.mp == due && mp1 - mp0 == due && !store.HasUnconfirmed(first.UserCode) && store.PlayerLines(first.UserCode, first.Season).Count > 0,
				$"[{first.Clubs[first.MyIndex].Name}] confirms {paid.mp:N0} MP and {paid.homeRuns} 홈런볼 (its lines now in the records)");
			Check(members.Skip(1).All(m => store.HasUnconfirmed(m.UserCode) && store.PlayerLines(m.UserCode, m.Season).Count == 0 && OwnGamesPending(store, m)),
				"the other members' games and lines still wait for them");
			// 최근성적 of the other clubs' cards (systems session, 2026-11-01): each played card keeps its last games, up to five.
			List<PlayerSeason> others = store.PlayerLines(first.UserCode, first.Season).Where(p => p.Club != first.MyIndex && p.G > 0).ToList();
			List<PlayerSeason> short5 = others.Where(p => store.ClubRecentGames(first.UserCode, first.Season, p.Club, p.CardIdx, 10).Count != Math.Min(p.G, LeagueStore.ClubGamesKept)).ToList();
			Check(others.Count > 0 && short5.Count == 0,
				$"[{first.Clubs[first.MyIndex].Name}] the other clubs' {others.Count} cards each show min(G, 5) recent games ({short5.Count} off)");

			// Every member uses all of 챔피언스's CP from here (a valid build: 23, or 24 with six tiers), so the next 편성 shows who goes
			// down keeping only the gate tier's (20, or 레전드's 23).
			// Seven tiers (2026-11-14): 26. The 23-point build, then a point at a time on the first pitching trait that stays valid.
			string Build23 = "121:2,122:2,124:2,123:1,125:2,126:2,131:2,132:2,133:1,134:2,138:1,140:1,127:2,130:1";
			Dictionary<byte, byte> points23 = Build23.Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1]));
			for (int guard = 0; points23.Values.Sum(v => v) < topCp && guard < 50; guard++)
			{
				bool added = false;
				for (byte k = 121; k <= 140 && !added; k++)
				{
					points23.TryGetValue(k, out byte v);
					points23[k] = (byte)(v + 1);
					if (TeamPropertyHandlers.Validate(points23, topCp) == null) added = true;
					else if (v == 0) points23.Remove(k);
					else points23[k] = v;
				}
				if (!added) break;
			}
			byte[] buff23 = points23.OrderBy(p => p.Key).SelectMany(p => new[] { p.Key, p.Value }).ToArray();
			using (Microsoft.Data.Sqlite.SqliteConnection c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
			{
				c.Open();
				foreach (LeagueState m in members)
				{
					using Microsoft.Data.Sqlite.SqliteCommand cmd = c.CreateCommand();
					cmd.CommandText = "INSERT OR REPLACE INTO team_property (user_code, buff) VALUES ($u, $b)";
					cmd.Parameters.AddWithValue("$u", (long)m.UserCode);
					cmd.Parameters.AddWithValue("$b", buff23);
					cmd.ExecuteNonQuery();
				}
			}
			Check(TeamPropertyHandlers.Validate(points23, topCp) == null && members.All(m => TeamPropertyHandlers.Points(m.UserCode).Values.Sum(v => v) == topCp),
				$"members' traits set to a valid {topCp}-point build");

			// 4. The rest of the season, the postseason and the close.
			Tick(next + LeagueCalendar.PostStart - TimeSpan.FromMinutes(1));
			members = going.Select(store.Load).ToList();
			a = members[0];
			diff = Differences(store, members);
			Check(diff == null && a.NextGame == LeagueRules.Games + 1 && a.Clubs.All(c => c.W + c.L + c.D == LeagueRules.Games),
				$"regular season: every copy the same{(diff == null ? string.Empty : ": " + diff)}; {Table(a)}");
			Tick(next + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1));
			members = going.Select(store.Load).ToList();
			a = members[0];
			diff = Differences(store, members);
			Bracket b = Postseason.Of(a);
			Check(diff == null && b.Champion >= 0 && members.All(m => Postseason.Of(m).Champion == b.Champion),
				$"postseason: every copy the same{(diff == null ? string.Empty : ": " + diff)}; champion {a.Clubs[b.Champion].Name}, places {string.Join(" ", b.Seeds.Select(x => $"{a.Clubs[x].Name}:{b.PlaceOf(x)}"))}");
			foreach (LeagueState m in members)
			{
				int rank = m.RankOf(m.MyIndex), place = Postseason.Of(m).PlaceOf(m.MyIndex);
				int final = place > 0 ? place : rank;
				Check(m.NextTier == (final <= 3 ? LeagueRules.ChampionsTier : LeagueRules.GateTier) && store.History(m.UserCode, m.Season)?.nextTier == m.NextTier,
					$"[{m.Clubs[m.MyIndex].Name}] 챔피언스 #{rank}{(place > 0 ? $", 포스트시즌 {place}위" : string.Empty)} → {LeagueRules.TierNames[m.NextTier]}");
			}

			// 5. The next 편성: who stayed share a new league, who went down play 메이저 on their own.
			Dictionary<uint, LeagueState> closed2 = members.ToDictionary(m => m.UserCode);
			Tick(next + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1));
			List<LeagueState> again = going.Select(store.Load).ToList();
			List<LeagueState> stayed = again.Where(s => closed2[s.UserCode].NextTier == LeagueRules.ChampionsTier).ToList();
			List<LeagueState> down = again.Where(s => closed2[s.UserCode].NextTier == LeagueRules.GateTier).ToList();
			// Who went down: a league of its own, or (LeagueRules.SharedAll, 11-18) with two or more one shared 레전드 league.
			bool downShared = LeagueRules.SharedAll && down.Count >= 2;
			Check(stayed.All(s => s.Tier == LeagueRules.ChampionsTier && s.Share != null && s.Share != a.Share && s.Share == stayed[0].Share && s.Start == next + LeagueCalendar.Cycle)
					&& down.All(s => s.Tier == LeagueRules.GateTier
						&& (downShared ? s.Share != null && s.Share == down[0].Share && s.Clubs.Count(c => c.IsHuman) == down.Count : s.Share == null && s.Clubs.Count(c => c.IsHuman) == 1)
						&& s.Clubs.Where(c => !c.IsHuman).All(c => c.AiKey >= 0 && AiTeams.Deck(LeagueRules.GateTier, c.AiKey)?.Name == c.Name)),
				$"next 편성: stayed {string.Join(", ", stayed.Select(s => s.Clubs[s.MyIndex].Name))} ({stayed.FirstOrDefault()?.Share}), down to {LeagueRules.TierNames[LeagueRules.GateTier]} {string.Join(", ", down.Select(s => s.Clubs[s.MyIndex].Name))}"
					+ $" with {LeagueRules.TierNames[LeagueRules.GateTier]} decks {string.Join(", ", down.Take(1).SelectMany(s => s.Clubs.Where(c => !c.IsUser)).Select(c => c.Name))}");
			// Who went down kept 20 CP of traits (the deepest taken off) and gets the notice once; who stayed kept all 23.
			foreach (LeagueState s in down)
			{
				Dictionary<byte, byte> p = TeamPropertyHandlers.Points(s.UserCode);
				string notice = TeamPropertyHandlers.TakeTrimNotice(s.UserCode);
				Check(p.Values.Sum(v => v) == gateCp && TeamPropertyHandlers.Validate(p, gateCp) == null && (topCp - gateCp < 3 || !p.ContainsKey(138) && !p.ContainsKey(140)) && notice != null
						&& TeamPropertyHandlers.TakeTrimNotice(s.UserCode) == null,
					$"[{s.Clubs[s.MyIndex].Name}] traits trimmed to {gateCp}: {string.Join(",", p.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"))}; notice: {notice?.Replace('\n', ' ')}");
			}
			Check(stayed.All(s => TeamPropertyHandlers.Points(s.UserCode).Values.Sum(v => v) == topCp && TeamPropertyHandlers.TakeTrimNotice(s.UserCode) == null),
				$"who stayed kept all {topCp} points, no notice");
			Tick(next + LeagueCalendar.Cycle + TimeSpan.FromMinutes(21));
			again = going.Select(store.Load).ToList();
			Check(again.All(s => s.NextGame == 3 && store.Games(s.UserCode, s.Season).Count == LeagueRules.Clubs), "the season after: games 1-2 played in every league");
			ServerLog.Info($"champions test: {s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}
	}
}
