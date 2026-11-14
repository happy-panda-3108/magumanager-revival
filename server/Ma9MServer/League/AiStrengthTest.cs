using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// "--aitest [seasons] [skip:grade:lecture[:backups] …]" (systems session, 2026-10-29; backups 2026-11-02, AiTeams.WithBackups): how league AI groups fare against each
	// other, to set 메이저's team-year decks (user: "볼 수 높은 팀보다는 약간 강하면서, 챔피언스리그의 AI보다는 확실히 약한 팀").
	// Each candidate pool (AiTeams.MakePool for 메이저 with that rule; grade 0 S, 1 A, 2 B) meets 메이저's fixed clubs and
	// 챔피언스's decks: a season is five clubs of each group in one league, played as league games are (the league
	// reference over the ten, 팀 특성, pitcher fatigue), and only the games between the groups count. Read-only.
	public static partial class LeagueService
	{
		public static int AiStrengthTest(int seasons, IEnumerable<string> variants, int games = LeagueRules.Games)
		{
			Dictionary<string, List<AiTeam>> groups = new Dictionary<string, List<AiTeam>>
			{
				["메이저 고정"] = AiTeams.OfTier(LeagueRules.MajorTier).ToList(),
				["챔피언스"] = AiTeams.Decks(LeagueRules.ChampionsTier)
			};
			List<string> candidates = new List<string>();
			foreach (string v in variants)
			{
				int[] p = v.Split(':').Select(int.Parse).ToArray();
				AiTeams.DeckRule rule = new AiTeams.DeckRule(p[0], p[1], p.Length > 2 && p[2] != 0, p.Length > 3 ? p[3] : 0);
				string name = $"덱 {p[0] + 1}~{p[0] + AiTeams.DeckPool}위 {"SABC"[rule.Grade]}{(rule.Lecture ? "+특강" : string.Empty)}"
					+ (rule.Backups > 0 ? $"+후보{rule.Backups}" : string.Empty);
				groups[name] = AiTeams.MakePool(s_data, LeagueRules.MajorTier, rule);
				candidates.Add(name);
				ServerLog.Info($"[aitest] {name}: {string.Join(", ", groups[name].Select(t => $"{t.Name} {t.Ball}볼"))}");
			}
			List<(string x, string y)> pairs = candidates.SelectMany(c => new[] { (c, "메이저 고정"), (c, "챔피언스") }).Append(("메이저 고정", "챔피언스")).ToList();
			foreach ((string x, string y) in pairs)
			{
				Stopwatch sw = Stopwatch.StartNew();
				int w = 0, l = 0, d = 0;
				Dictionary<string, (int w, int l)> byClub = new Dictionary<string, (int w, int l)>();
				for (int season = 0; season < seasons; season++)
				{
					foreach ((string club, bool won, bool lost) in Mixed(groups[x], groups[y], season, games))
					{
						if (won) w++; else if (lost) l++; else d++;
						byClub.TryGetValue(club, out (int w, int l) r);
						byClub[club] = (r.w + (won ? 1 : 0), r.l + (lost ? 1 : 0));
					}
				}
				double pct = w + l == 0 ? 0 : 100.0 * w / (w + l);
				ServerLog.Info($"[aitest] {x} vs {y}: {w}-{l}-{d}, {pct:F1}% ({seasons} seasons, {sw.Elapsed.TotalSeconds:F0} s)");
				ServerLog.Info($"[aitest]   {x} clubs: {string.Join(", ", byClub.Where(kv => groups[x].Any(t => t.Name == kv.Key)).OrderByDescending(kv => Pct(kv.Value)).Select(kv => $"{kv.Key} {Pct(kv.Value):F0}"))}");
			}
			return 0;
		}

		// "--aitest7 [seasons]" (2026-11-14, 더블A): with seven tiers, 루키's fixed clubs, 더블A's decks and 트리플A's decks two groups at
		// a time, as above. 더블A should land between 루키 and 트리플A (user: "트리플A 리그보다 AI 구단을 약하게").
		public static int AiStrengthTest7(int seasons, IEnumerable<string> variants = null, int games = LeagueRules.Games)
		{
			// Variants "skip:grade" of 더블A's rule (the rest as DoubleADecks): each pool against 루키's clubs and 트리플A's decks.
			foreach (string v in variants ?? Enumerable.Empty<string>())
			{
				int[] p = v.Split(':').Select(int.Parse).ToArray();
				AiTeams.DeckRule rule = AiTeams.DoubleADecks with { Skip = p[0], Grade = p[1] };
				List<AiTeam> pool = AiTeams.MakePool(s_data, LeagueRules.DoubleATier, rule);
				string name = $"더블A 안{p[0] + 1}~ {"SABC"[p[1]]}";
				ServerLog.Info($"[aitest7] {name} ({pool.Count}): {string.Join(", ", pool.Select(t => $"{t.Name} {t.Ball}볼"))}");
				foreach ((string y, List<AiTeam> other) in new[] { ("루키 고정", AiTeams.OfTier(1).ToList()), ("트리플A", AiTeams.Decks(3)) })
				{
					int w = 0, l = 0;
					for (int season = 0; season < seasons; season++)
					{
						foreach ((string club, bool won, bool lost) in Mixed(pool, other, season, games))
						{
							if (won) w++; else if (lost) l++;
						}
					}
					ServerLog.Info($"[aitest7] {name} vs {y}: {w}-{l}, {(w + l == 0 ? 0 : 100.0 * w / (w + l)):F1}%");
				}
			}
			if (!LeagueRules.Seven)
			{
				ServerLog.Warn("[aitest7] seven tiers are not on (MA9M_TIERS7=1 on a data copy)");
				return 2;
			}
			Dictionary<string, List<AiTeam>> groups = new Dictionary<string, List<AiTeam>>
			{
				["루키 고정"] = AiTeams.OfTier(1).ToList(),
				["더블A"] = AiTeams.Decks(LeagueRules.DoubleATier),
				["트리플A"] = AiTeams.Decks(3)
			};
			foreach (KeyValuePair<string, List<AiTeam>> g in groups)
			{
				ServerLog.Info($"[aitest7] {g.Key} ({g.Value.Count}): {string.Join(", ", g.Value.Select(t => $"{t.Name} {t.Ball}볼"))}");
			}
			if (groups.Values.Any(g => g.Count < 5)) return 3;
			foreach ((string x, string y) in new[] { ("더블A", "루키 고정"), ("트리플A", "더블A"), ("트리플A", "루키 고정") })
			{
				Stopwatch sw = Stopwatch.StartNew();
				int w = 0, l = 0, d = 0;
				Dictionary<string, (int w, int l)> byClub = new Dictionary<string, (int w, int l)>();
				for (int season = 0; season < seasons; season++)
				{
					foreach ((string club, bool won, bool lost) in Mixed(groups[x], groups[y], season, games))
					{
						if (won) w++; else if (lost) l++; else d++;
						byClub.TryGetValue(club, out (int w, int l) r);
						byClub[club] = (r.w + (won ? 1 : 0), r.l + (lost ? 1 : 0));
					}
				}
				double pct = w + l == 0 ? 0 : 100.0 * w / (w + l);
				ServerLog.Info($"[aitest7] {x} vs {y}: {w}-{l}-{d}, {pct:F1}% ({seasons} seasons, {sw.Elapsed.TotalSeconds:F0} s)");
				ServerLog.Info($"[aitest7]   {x} clubs: {string.Join(", ", byClub.OrderByDescending(kv => Pct(kv.Value)).Select(kv => $"{kv.Key} {Pct(kv.Value):F0}"))}");
			}
			return 0;
		}

		private static double Pct((int w, int l) r) => r.w + r.l == 0 ? 0 : 100.0 * r.w / (r.w + r.l);

		// One season of five clubs from x and five from y; per game between the groups: x's club, won, lost.
		private static List<(string club, bool won, bool lost)> Mixed(List<AiTeam> x, List<AiTeam> y, int season, int games)
		{
			Random rnd = new Random(7919 * season + x.Count * 31 + y.Count);
			List<AiTeam> picks = x.OrderBy(_ => rnd.Next()).Take(5).Concat(y.OrderBy(_ => rnd.Next()).Take(5)).OrderBy(_ => rnd.Next()).ToList();
			HashSet<int> fromX = picks.Select((t, i) => (t, i)).Where(p => x.Contains(p.t)).Select(p => p.i).ToHashSet();
			LeagueState s = new LeagueState { UserCode = 0x7F00_0000u + (uint)season, Tier = LeagueRules.MajorTier, Season = 9000 + season, MyIndex = 0 };
			for (int i = 0; i < LeagueRules.Clubs; i++)
			{
				AiTeam t = picks[i];
				s.Clubs.Add(new LeagueClub { Index = i, Name = t.Name, Emblem = t.Emblem, Owner = AiOwner(s.UserCode, i), Lineup = t.Lineup, Ball = t.Ball });
			}
			DateTime day = DateTime.Now;
			Dictionary<(int club, int card), PitcherLoad> fatigue = new Dictionary<(int club, int card), PitcherLoad>();
			List<(string club, bool won, bool lost)> results = new List<(string club, bool won, bool lost)>();
			for (int game = 1; game <= games; game++)
			{
				GameTeam[] teams = new GameTeam[LeagueRules.Clubs];
				Lineup[] lineups = new Lineup[LeagueRules.Clubs];
				for (int i = 0; i < LeagueRules.Clubs; i++)
				{
					lineups[i] = LineupOf(s, s.Clubs[i], out _);
					teams[i] = Team(s.Clubs[i], lineups[i], day, game, null);
					if (picks[i].Development.Count > 0) TeamStrength.AddBonuses(teams[i], lineups[i], picks[i].Development.Values);
				}
				TeamStrength.ApplyLeagueReference(teams);
				TraitEffects.Effect[] traits = picks.Select(t => TraitEffects.FromPoints(t.Traits)).ToArray();
				for (int i = 0; i < teams.Length; i++) TraitEffects.Raise(teams[i], traits[i]);
				Dictionary<(int club, int card), PitcherLoad> after = new Dictionary<(int club, int card), PitcherLoad>();
				foreach ((int away, int home) in LeagueRules.Pairs(game, s.Start))
				{
					LeagueGame g = PlayGame(s, game, home, away, teams, lineups, new List<PlayerSeason>(), traits, fatigue, after, post: false);
					if (fromX.Contains(home) == fromX.Contains(away)) continue;
					int xi = fromX.Contains(home) ? home : away;
					int xs = xi == home ? g.HomeScore : g.AwayScore, ys = xi == home ? g.AwayScore : g.HomeScore;
					results.Add((picks[xi].Name, xs > ys, xs < ys));
				}
				fatigue = after;
			}
			return results;
		}
	}
}
