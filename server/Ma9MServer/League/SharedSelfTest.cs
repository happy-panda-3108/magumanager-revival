using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MServer
{
	// "--sharedtest" (systems session, 2026-11-18; user 01:0x: "이제부터는 같은 리그가 되면 최대한 유저 계정들끼리 매칭되게끔해줘"): shared
	// leagues in every tier (LeagueRules.SharedAll) on a copy of the data directory (it writes games, MP and seasons). The current
	// cycle runs to its close; on the copy the teams then go: 비기너·루키 → 루키 (a fixed-club tier), 더블A·트리플A → 더블A (a deck
	// tier), the rest where their places send them. Then 편성 (a shared league per tier with two or more, a league of its own for
	// one), the first slots, the rest of the season and the close (every copy the same), and the next 편성.
	public static class SharedSelfTest
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

		private static string Differences(LeagueStore store, List<LeagueState> members)
		{
			LeagueState a = members[0];
			string Club(LeagueClub c) => $"{c.Index}:{c.Name}:{c.Owner}:{c.AiKey}:{c.W}-{c.L}-{c.D}:{c.RunsFor}-{c.RunsAgainst}:{c.Totals.H}:{c.Totals.HR}";
			string Games(LeagueState s) => string.Join(";", store.Games(s.UserCode, s.Season).Select(g => $"{g.Game}/{g.Home}/{g.Away}/{g.HomeScore}-{g.AwayScore}"));
			string clubsA = string.Join("|", a.Clubs.Select(Club)), gamesA = Games(a);
			List<string> bad = new List<string>();
			foreach (LeagueState m in members.Skip(1))
			{
				if (string.Join("|", m.Clubs.Select(Club)) != clubsA) bad.Add($"{m.UserCode} clubs");
				if (Games(m) != gamesA) bad.Add($"{m.UserCode} games");
				if (m.NextGame != a.NextGame || m.PostSlot != a.PostSlot || m.LastGame != a.LastGame) bad.Add($"{m.UserCode} progress");
			}
			return bad.Count == 0 ? null : string.Join(", ", bad);
		}

		private static string Name(LeagueState s) => s.Clubs[s.MyIndex].Name;

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
			Check(LeagueRules.SharedAll, "MA9M_SHARED_ALL is not 0: every tier shares");

			// 1. The current cycle to its close.
			DateTime now = DateTime.Now;
			DateTime start = LeagueCalendar.CycleStart(now);
			Tick(now);
			Tick(start + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1));
			Dictionary<uint, LeagueState> closed = teams.Select(t => store.Load(t.code)).Where(s => s != null && s.Start == start && s.NextTier >= 0).ToDictionary(s => s.UserCode);
			int rookie = 1, doubleA = LeagueRules.DoubleATier;
			foreach (LeagueState s in closed.Values)
			{
				int to = s.Tier <= 1 ? rookie : s.Tier <= 3 ? doubleA : s.NextTier;
				if (to == s.NextTier) continue;
				s.NextTier = to;
				store.Save(s);
			}
			Dictionary<int, List<uint>> byTier = closed.Values.GroupBy(s => s.NextTier).ToDictionary(g => g.Key, g => g.Select(s => s.UserCode).OrderBy(x => x).ToList());
			ServerLog.Info("     going: " + string.Join(" / ", byTier.OrderBy(kv => kv.Key).Select(kv => $"{LeagueRules.TierNames[kv.Key]} {string.Join(",", kv.Value.Select(c => Name(closed[c])))}")));
			Check(byTier.TryGetValue(rookie, out List<uint> r) && r.Count >= 2 && byTier.TryGetValue(doubleA, out List<uint> d) && d.Count >= 2,
				"at least two teams going to 루키 (fixed clubs) and to 더블A (decks)");

			// 2. 편성 (59 h).
			Tick(start + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1));
			DateTime next = start + LeagueCalendar.Cycle;
			Dictionary<int, List<LeagueState>> leagues = new Dictionary<int, List<LeagueState>>();
			foreach ((int tier, List<uint> codes) in byTier)
			{
				List<LeagueState> members = codes.Select(store.Load).ToList();
				string tn = LeagueRules.TierNames[tier];
				Check(members.All(m => m.Start == next && m.Tier == tier && m.Season == closed[m.UserCode].Season + 1 && m.NextGame == 1 && m.NextTier == -1
						&& m.Clubs[m.MyIndex].LastRank == closed[m.UserCode].RankOf(closed[m.UserCode].MyIndex)),
					$"{tn}: {members.Count} team(s) in a new {tn} league from {next:MM-dd HH:mm}");
				if (members.Count == 1)
				{
					Check(members[0].Share == null && members[0].Clubs.Count(c => c.IsHuman) == 1, $"{tn}: one team, a league of its own ({Name(members[0])})");
					continue;
				}
				leagues[tier] = members;
				LeagueState a = members[0];
				Check(members.All(m => m.Share != null && m.Share == a.Share) && members.Select(m => m.MyIndex).Distinct().Count() == members.Count
						&& members.All(m => m.Clubs.Count(c => c.IsUser) == 1 && m.Clubs[m.MyIndex].Owner == m.UserCode)
						&& members.All(m => members.All(o => m.Clubs[o.MyIndex].Owner == o.UserCode && m.Clubs[o.MyIndex].IsHuman)),
					$"{tn}: shared {a.Share}, members {string.Join(", ", members.Select(m => $"{Name(m)} at {m.MyIndex}"))}");
				List<LeagueClub> ai = a.Clubs.Where(c => !c.IsHuman).ToList();
				bool decks = AiTeams.HasDecks(tier);
				Check(ai.Count == LeagueRules.Clubs - members.Count && ai.Select(c => c.AiKey).Distinct().Count() == ai.Count
						&& ai.All(c => decks ? c.AiKey >= 0 && AiTeams.Deck(tier, c.AiKey)?.Name == c.Name : c.AiKey <= -2 && AiTeams.Get(tier, -2 - c.AiKey).Name == c.Name),
					$"{tn}: AI places ({(decks ? "decks" : "fixed clubs")}): {string.Join(", ", ai.Select(c => $"{c.Name}[{c.AiKey}]"))}");
				string diff = Differences(store, members);
				Check(diff == null, $"{tn}: copies the same at 편성{(diff == null ? string.Empty : ": " + diff)}");
			}
			Check(teams.All(t => store.Load(t.code) is LeagueState s && s.Start == next), "every active team has a league for the next season");

			// 3. The first two hours (6 slots), then the rest of the season and the close.
			foreach (DateTime t in new[] { next + TimeSpan.FromHours(2) - TimeSpan.FromMinutes(1), next + LeagueCalendar.PostStart - TimeSpan.FromMinutes(1), next + LeagueCalendar.CloseAt + TimeSpan.FromMinutes(1) })
			{
				Tick(t);
				foreach ((int tier, List<LeagueState> old) in leagues.ToList())
				{
					List<LeagueState> members = old.Select(m => store.Load(m.UserCode)).ToList();
					leagues[tier] = members;
					string diff = Differences(store, members);
					LeagueState a = members[0];
					Check(diff == null && members.All(m => m.Clubs.Where(c => !c.IsHuman).All(c => old[0].Clubs[c.Index].Name == c.Name)),
						$"{LeagueRules.TierNames[tier]} at {t:MM-dd HH:mm}: copies the same{(diff == null ? string.Empty : ": " + diff)}, AI clubs unchanged; game {a.LastGame}, "
						+ string.Join(" ", a.Ranked().Take(4).Select((c, i) => $"{i + 1}.{c.Name}{(c.IsHuman ? "(계정)" : string.Empty)} {c.W}-{c.L}")));
				}
			}
			foreach ((int tier, List<LeagueState> members) in leagues)
			{
				foreach (LeagueState m in members)
				{
					int rank = m.RankOf(m.MyIndex), place = LeagueRules.HasPostseason(m.Tier) ? Postseason.Of(m).PlaceOf(m.MyIndex) : 0;
					Check(m.NextTier == LeagueRules.NextTier(m.Tier, rank, place),
						$"[{Name(m)}] {LeagueRules.TierNames[tier]} #{rank}{(place > 0 ? $", 포스트시즌 {place}위" : string.Empty)} → {LeagueRules.TierNames[m.NextTier]}");
				}
			}

			// 4. The next 편성: again by tier.
			Tick(next + LeagueCalendar.FormAt + TimeSpan.FromMinutes(1));
			List<LeagueState> all = teams.Select(t => store.Load(t.code)).Where(s => s != null).ToList();
			Check(all.All(s => s.Start == next + LeagueCalendar.Cycle), "next 편성: every active team has a league");
			foreach (IGrouping<int, LeagueState> g in all.GroupBy(s => s.Tier))
			{
				List<LeagueState> members = g.ToList();
				bool ok = members.Count == 1 ? members[0].Share == null : members.All(s => s.Share != null && s.Share == members[0].Share) || members.Count > LeagueRules.Clubs;
				Check(ok, $"next 편성 {LeagueRules.TierNames[g.Key]}: {string.Join(", ", members.Select(s => $"{Name(s)}({s.Share ?? "혼자"})"))}");
			}
			Tick(next + LeagueCalendar.Cycle + TimeSpan.FromMinutes(21));
			all = teams.Select(t => store.Load(t.code)).Where(s => s != null).ToList();
			Check(all.All(s => s.NextGame == 3), "the season after: games 1-2 played in every league");
			ServerLog.Info($"shared test: {s_problems} problem(s)");
			return s_problems == 0 ? 0 : 1;
		}
	}
}
