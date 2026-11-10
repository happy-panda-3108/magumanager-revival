using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using Ma9MSim;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 친선경기 (exhibition): an AI club built from real cards inside the chosen ball range, one
	// game through the card engine, and its broadcast. 새벽 자동 시범경기 in ExhibitionAuto.cs (systems session, 2026-11-03).
	public static partial class ExhibitionHandlers
	{
		public const int TriesPerDay = 10;

		// Rewards (user, 2026-10-18): MP by result and difficulty (economy draft: about 350k a day
		// from ten games), and on a win a 20% chance at one random player of the opponent's club.
		// About a third more since 2026-10-24 (user: "시범경기 MP 보상을 조금 늘리고"; was 30/20/10k, 50/30/15k, 80/50/25k).
		// Exhibitions carry no fatigue and give no 홈런볼 (league games only).
		private static readonly int[,] RewardMp =
		{
			// win, tie, loss
			{ 40000, 25000, 15000 },  // easy
			{ 65000, 40000, 20000 },  // normal
			{ 100000, 65000, 30000 }  // hard
		};

		// User, 2026-10-19: 20% (was 10%).
		private const double PrizeChance = 0.20;

		private static Database s_db;

		private static GameData s_data;

		private sealed class Match
		{
			public string AiName;
			public byte AiEmblem;
			public uint AiCode;
			public Lineup Ai;
			public int AiBall;
			public byte CurSp;
			public byte[] Buff;
			public int HomeRuns, AwayRuns;
			public bool Played;
			public int Grade;
			public int Scenes;
			public int DevJoin = -1;   // dev: scene the test account's broadcast starts at
			public DateTime StartedAt;  // when the game was played (Start): a broadcast joined later picks up there
			public GameDetail Detail;   // box score (LeagueService.BoxScore): the scoreboard after SKIP
		}

		// The last finished game of each team and the MP it paid, for the scoreboard the client asks for
		// after SKIP (game 0, LeagueHandlers.Scoreboard; user, 2026-10-21: "스코어보드로 넘어가고 결과를
		// 바로 확인"). Kept until the next game is tried.
		private static readonly Dictionary<uint, (Match match, int earned)> s_finished = new Dictionary<uint, (Match, int)>();

		// Dev switches only ever apply to the automated test account.
		private static bool IsTester(GameSession session) => session.Account?.Email?.StartsWith("autotest", StringComparison.OrdinalIgnoreCase) == true;

		private static readonly Dictionary<uint, Match> s_matches = new Dictionary<uint, Match>();

		private static readonly Dictionary<uint, (DateTime day, int used)> s_tries = new Dictionary<uint, (DateTime, int)>();

		private static readonly Random s_rnd = new Random();

		private static readonly string[] AiNames =
		{
			"레전드 올스타", "드림 올스타", "나눔 올스타", "퓨처스 연합", "베테랑 연합", "루키 연합", "외인 군단", "왕조의 후예", "철벽 마운드", "홈런 군단"
		};

		public static void Register(GameLoop loop, Database db, GameData data, string dbPath = null)
		{
			s_db = db;
			s_data = data;
			if (dbPath != null)
			{
				s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
				Execute("CREATE TABLE IF NOT EXISTS exhibition_match (user_code INTEGER PRIMARY KEY, info TEXT NOT NULL, buff BLOB)");
				Execute("CREATE TABLE IF NOT EXISTS exhibition_tries (user_code INTEGER PRIMARY KEY, day TEXT NOT NULL, used INTEGER NOT NULL)");
				// Engine session, 2026-10-31: finished games kept a while (the last five per team) to look at a play the user
				// reports ("유격수가 공을 잡고 왜 아무데도 안 던지는 것인지" — the game was gone by then).
				Execute("CREATE TABLE IF NOT EXISTS exhibition_log (user_code INTEGER NOT NULL, ended TEXT NOT NULL, info TEXT NOT NULL, buff BLOB)");
				LoadTries();
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_EXHIBITION_GAME>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_TRY_EXHIBITION_GAME>(Try);
			loop.Register<Ma9MPacket.PKS_CS_START_EXHIBITION_GAME>(Start);
			loop.Register<Ma9MPacket.PKS_CS_GIVEUP_EXHIBITION_GAME>(GiveUp);
			loop.Register<Ma9MPacket.PKS_CS_ENTER_EXHIBITION_BROADCAST>(Broadcast);
			loop.Register<Ma9MPacket.PKS_CS_EXHIBITION_PAST_SCENE>(PastScene);
			loop.Register<Ma9MPacket.PKS_CS_EXHIBITION_GAME_RESULT>(Result);
		}

		// The game in progress is kept in the DB too (user, 2026-10-21): after closing the app, or a server
		// restart, the login goes back to its broadcast (LoginHandlers TransitScene 3) and then the result.
		private static string s_cs;

		private sealed class Saved
		{
			public string AiName { get; set; }
			public byte AiEmblem { get; set; }
			public uint AiCode { get; set; }
			public string Ai { get; set; }
			public int AiBall { get; set; }
			public byte CurSp { get; set; }
			public int HomeRuns { get; set; }
			public int AwayRuns { get; set; }
			public int Grade { get; set; }
			public int Scenes { get; set; }
			public DateTime StartedAt { get; set; }
			public GameDetail Detail { get; set; }
		}

		private static void Execute(string sql)
		{
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = sql;
			cmd.ExecuteNonQuery();
		}

		private static void Save(uint code, Match m)
		{
			if (s_cs == null)
			{
				return;
			}
			Saved s = new Saved
			{
				AiName = m.AiName, AiEmblem = m.AiEmblem, AiCode = m.AiCode, Ai = m.Ai.Serialize(), AiBall = m.AiBall, CurSp = m.CurSp,
				HomeRuns = m.HomeRuns, AwayRuns = m.AwayRuns, Grade = m.Grade, Scenes = m.Scenes, StartedAt = m.StartedAt, Detail = m.Detail
			};
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO exhibition_match (user_code, info, buff) VALUES ($u, $i, $b)";
			cmd.Parameters.AddWithValue("$u", (long)code);
			cmd.Parameters.AddWithValue("$i", JsonSerializer.Serialize(s));
			cmd.Parameters.Add("$b", SqliteType.Blob).Value = (object)m.Buff ?? DBNull.Value;
			cmd.ExecuteNonQuery();
		}

		// The day's tries are kept in the DB too: in memory only, every server restart gave each team its ten
		// games again (user, 2026-10-23: about 30 exhibition games in one day on SKY해민 over four restarts).
		private static void LoadTries()
		{
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT user_code, day, used FROM exhibition_tries";
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				if (DateTime.TryParseExact(r.GetString(1), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
					System.Globalization.DateTimeStyles.None, out DateTime day))
				{
					s_tries[(uint)r.GetInt64(0)] = (day, r.GetInt32(2));
				}
			}
		}

		private static void SaveTries(uint code, DateTime day, int used)
		{
			if (s_cs == null)
			{
				return;
			}
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR REPLACE INTO exhibition_tries (user_code, day, used) VALUES ($u, $d, $n)";
			cmd.Parameters.AddWithValue("$u", (long)code);
			cmd.Parameters.AddWithValue("$d", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$n", used);
			cmd.ExecuteNonQuery();
		}

		private static void Forget(uint code)
		{
			if (s_cs != null)
			{
				Execute($"INSERT INTO exhibition_log (user_code, ended, info, buff) SELECT user_code, datetime('now', 'localtime'), info, buff FROM exhibition_match WHERE user_code = {(long)code}");
				Execute($"DELETE FROM exhibition_log WHERE user_code = {(long)code} AND rowid NOT IN (SELECT rowid FROM exhibition_log WHERE user_code = {(long)code} ORDER BY rowid DESC LIMIT 5)");
				Execute($"DELETE FROM exhibition_match WHERE user_code = {(long)code}");
			}
		}

		// The game of this team, from memory or from the DB.
		private static bool TryGet(uint code, out Match match)
		{
			if (s_matches.TryGetValue(code, out match))
			{
				return true;
			}
			if (s_cs == null)
			{
				return false;
			}
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT info, buff FROM exhibition_match WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)code);
			using SqliteDataReader r = cmd.ExecuteReader();
			if (!r.Read() || JsonSerializer.Deserialize<Saved>(r.GetString(0)) is not Saved s)
			{
				return false;
			}
			match = new Match
			{
				AiName = s.AiName, AiEmblem = s.AiEmblem, AiCode = s.AiCode, Ai = Lineup.Parse(s.Ai), AiBall = s.AiBall, CurSp = s.CurSp,
				HomeRuns = s.HomeRuns, AwayRuns = s.AwayRuns, Grade = s.Grade, Scenes = s.Scenes, StartedAt = s.StartedAt, Detail = s.Detail,
				Buff = r.IsDBNull(1) ? null : (byte[])r[1]
			};
			s_matches[code] = match;
			return true;
		}

		// A played game whose result has not been given yet: the login sends the player to its broadcast.
		public static bool Pending(uint code) => TryGet(code, out Match m) && m.Buff != null && !m.Played;

		private static CardCatalog Catalog => s_data.Catalog;

		// Balls of the 1군: the 14 batters and 11 pitchers on the roster.
		private static int RosterBall(Lineup l) =>
			l.Batters.Concat(l.Pitchers).Where(i => i != 0).Sum(i => Catalog.Get(i)?.Ball ?? 0);

		// Easy / normal / hard around a ball base, 3 balls lower than at first (user, 2026-10-22: "상대팀 평균 볼수가 너무 높음 …
		// 3볼씩만 줄입시다"), and 4 more since the AI's bench is 1볼 (user, 2026-10-24: "그러면 난이도가 많이 오르니 평균적으로
		// 4볼씩 낮춥시다"). The base is the team's league ball cap (user, 2026-10-28: "메이저쪽으로 갈수록 더 볼수가 높아서
		// 난이도가 높아져야" — it was the team's own 1군 balls, so a 140-ball 마이너 club met stronger clubs than a 136-ball
		// 메이저 one), as the original's cur_max_ball suggests; the screen still shows the team's own balls.
		private static (int lo, int hi)[] Ranges(int cap)
		{
			int B(int v) => Math.Clamp(v, 25, 250);
			return new[] { (B(cap - 32), B(cap - 17)), (B(cap - 17), B(cap - 2)), (B(cap - 2), B(cap + 13)) };
		}

		private static int TriesLeft(uint code)
		{
			if (!s_tries.TryGetValue(code, out (DateTime day, int used) t) || t.day != DateTime.Today)
			{
				return TriesPerDay;
			}
			return Math.Max(0, TriesPerDay - t.used);
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_EXHIBITION_GAME packet)
		{
			Lineup mine = s_db.GetLineup(session.Team.UserCode);
			int ball = mine != null ? RosterBall(mine) : 0;
			(int lo, int hi)[] r = Ranges(RosterHandlers.MaxCostOf(session.Team.UserCode));
			Ma9MPacket.PKS_SC_ENTER_EXHIBITION_GAME reply = new Ma9MPacket.PKS_SC_ENTER_EXHIBITION_GAME();
			reply.cur_max_ball.n = (byte)Math.Clamp(ball, 0, 255);
			reply.opp_min_ball_easy.n = (byte)r[0].lo;
			reply.opp_max_ball_easy.n = (byte)r[0].hi;
			reply.opp_min_ball_norm.n = (byte)r[1].lo;
			reply.opp_max_ball_norm.n = (byte)r[1].hi;
			reply.opp_min_ball_hard.n = (byte)r[2].lo;
			reply.opp_max_ball_hard.n = (byte)r[2].hi;
			reply.remain_try_count.n = (byte)TriesLeft(session.Team.UserCode);
			reply.FriendBallCount.n = 0;
			reply.existNew.n = 0;
			session.Send(reply);
		}

		private static void Try(GameSession session, Ma9MPacket.PKS_CS_TRY_EXHIBITION_GAME packet)
		{
			Ma9MPacket.PKS_SC_TRY_EXHIBITION_GAME reply = new Ma9MPacket.PKS_SC_TRY_EXHIBITION_GAME();
			uint code = session.Team.UserCode;
			Lineup mine = s_db.GetLineup(code);
			if (mine == null || TriesLeft(code) <= 0)
			{
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			Match match = NewMatch(code, mine, packet.grade.n);

			reply.result.n = 0;
			reply.team_name.str = match.AiName;
			reply.batters.array = match.Ai.Batters.Select(i => Info(match.AiCode, i)).ToArray();
			reply.fielders.array = match.Ai.Fielders.Select(f => new PByte { n = f }).ToArray();
			reply.pitchers.array = match.Ai.Pitchers.Select(i => Info(match.AiCode, i)).ToArray();
			reply.next_sp.n = match.CurSp;
			reply.rotto.n = (byte)Math.Clamp(match.AiBall, 0, 255);
			reply.ball.n = (byte)Math.Clamp(match.AiBall, 0, 255);
			session.Send(reply);
		}

		// The opponent for one of today's tries at a difficulty (0 easy … 2 hard), kept as the team's game; the try is spent.
		private static Match NewMatch(uint code, Lineup mine, int grade)
		{
			grade = Math.Clamp(grade, 0, 2);
			(int lo, int hi) range = Ranges(RosterHandlers.MaxCostOf(code))[grade];
			Match match = new Match
			{
				AiName = AiNames[s_rnd.Next(AiNames.Length)],
				AiEmblem = (byte)s_rnd.Next(1, 11),
				AiCode = (uint)s_rnd.Next(1_000_000, int.MaxValue),
				// The roster's 선발예정 pitcher (next league starter); exhibitions leave the rotation alone.
				CurSp = (byte)RosterHandlers.NextStarterSlot(code, mine),
				Grade = grade
			};
			match.Ai = AiTeam(range.lo, range.hi, out match.AiBall);
			s_matches[code] = match;
			s_finished.Remove(code);
			Save(code, match);
			(DateTime day, int used) t = s_tries.TryGetValue(code, out var prev) && prev.day == DateTime.Today ? prev : (DateTime.Today, 0);
			s_tries[code] = (t.day, t.used + 1);
			SaveTries(code, t.day, t.used + 1);
			return match;
		}

		private static PUICardInfo Info(uint owner, int idx) =>
			new PUICardInfo(idx, idx == 0 ? (byte)2 : Condition.For(owner, idx, DateTime.Now), 30);

		// 25 real cards whose balls add up inside [lo, hi]: a regular at every position and a DH, a bench
		// of five 1볼 bats (a backup catcher and four more), five starters, four relievers, a set-up man and a
		// closer. The bench is always 1볼 (user, 2026-10-24: "후보 선수들을 무조건 1볼로 고정 … 남는 볼수만큼 주전
		// 라인업에 투자"), so the rest of the balls go to the regulars and the staff.
		public const int AiBenchBall = 1, AiBench = 5;

		private static Lineup AiTeam(int lo, int hi, out int total)
		{
			int target = s_rnd.Next(lo, hi + 1);
			(Func<CardInfo, bool> want, int count)[] slots =
			{
				(c => c.IsBatter && c.MainPosition == CardPos.C, 1), (c => c.IsBatter && c.MainPosition == CardPos.B1, 1), (c => c.IsBatter && c.MainPosition == CardPos.B2, 1),
				(c => c.IsBatter && c.MainPosition == CardPos.B3, 1), (c => c.IsBatter && c.MainPosition == CardPos.SS, 1), (c => c.IsBatter && c.MainPosition == CardPos.LF, 1),
				(c => c.IsBatter && c.MainPosition == CardPos.CF, 1), (c => c.IsBatter && c.MainPosition == CardPos.RF, 1), (c => c.IsBatter, 1),
				(c => !c.IsBatter && c.MainPosition == CardPos.SP, 5), (c => !c.IsBatter && c.MainPosition == CardPos.RP, 4),
				(c => !c.IsBatter && c.MainPosition == CardPos.SU, 1), (c => !c.IsBatter && c.MainPosition == CardPos.CP, 1)
			};
			(Func<CardInfo, bool> want, int count)[] bench = { (c => c.IsBatter && c.MainPosition == CardPos.C, 1), (c => c.IsBatter, AiBench - 1) };
			List<CardInfo> all = Catalog.Drawable.ToList();
			List<(Func<CardInfo, bool> want, CardInfo card)> picks = new List<(Func<CardInfo, bool>, CardInfo)>();
			HashSet<int> players = new HashSet<int>();
			double mean = (target - AiBench * AiBenchBall) / (25.0 - AiBench);
			CardInfo Draw(Func<CardInfo, bool> want, int ball)
			{
				for (int spread = 0; spread < 10; spread++)
				{
					List<CardInfo> pool = all.Where(c => want(c) && Math.Abs(c.Ball - ball) <= spread && !players.Contains(c.Sidx)).ToList();
					if (pool.Count > 0)
					{
						return pool[s_rnd.Next(pool.Count)];
					}
				}
				return all.First(c => want(c) && !players.Contains(c.Sidx));
			}
			foreach ((Func<CardInfo, bool> want, int count) in slots)
			{
				for (int i = 0; i < count; i++)
				{
					CardInfo c = Draw(want, (int)Math.Round(mean + s_rnd.Next(-2, 3)));
					players.Add(c.Sidx);
					picks.Add((want, c));
				}
			}
			List<CardInfo> benchCards = new List<CardInfo>();
			foreach ((Func<CardInfo, bool> want, int count) in bench)
			{
				for (int i = 0; i < count; i++)
				{
					CardInfo c = Draw(want, AiBenchBall);
					players.Add(c.Sidx);
					benchCards.Add(c);
				}
			}
			int benchBalls = benchCards.Sum(c => c.Ball);
			// Nudge single cards (never the bench) up or down a ball until the total lands in range.
			for (int round = 0; round < 400; round++)
			{
				int sum = picks.Sum(p => p.card.Ball) + benchBalls;
				if (sum >= lo && sum <= hi)
				{
					break;
				}
				int k = s_rnd.Next(picks.Count);
				int want = picks[k].card.Ball + (sum < lo ? 1 : -1);
				if (want < 1 || want > 10)
				{
					continue;
				}
				players.Remove(picks[k].card.Sidx);
				List<CardInfo> pool = all.Where(c => picks[k].want(c) && c.Ball == want && !players.Contains(c.Sidx)).ToList();
				CardInfo next = pool.Count > 0 ? pool[s_rnd.Next(pool.Count)] : picks[k].card;
				players.Add(next.Sidx);
				picks[k] = (picks[k].want, next);
			}
			total = picks.Sum(p => p.card.Ball) + benchBalls;
			// The regulars make the lineup and the 1볼 bench takes the bench slots after them. Built from all 25, the
			// builder now and then started a 1볼 card ahead of a weaker regular and benched the regular (2-3 teams in
			// 300, self-test "bench above 1볼"; systems session, 2026-10-24).
			Lineup lineup = LineupBuilder.Build(picks.Select(p => p.card.Idx), Catalog);
			for (int i = 0; i < benchCards.Count && 9 + i < Lineup.BatterSlots; i++)
			{
				lineup.Batters[9 + i] = benchCards[i].Idx;
			}
			return lineup;
		}

		// Game cards for a lineup: batting order and positions, the 11-man staff, today's starter.
		private static GameTeam Team(string name, uint owner, Lineup l, int starterSlot, IEnumerable<TeamCard> owned = null)
		{
			GameTeam t = new GameTeam { Name = name };
			for (int pos = 0; pos < Lineup.FielderSlots; pos++)
			{
				t.Position[l.Fielders[pos] - 1] = pos;
			}
			for (int slot = 0; slot < 9; slot++)
			{
				t.Order[slot] = s_data.Card(l.Batters[slot], Condition.For(owner, l.Batters[slot], DateTime.Now));
			}
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				t.Pitchers[s] = l.Pitchers[s] != 0 ? s_data.Card(l.Pitchers[s], Condition.For(owner, l.Pitchers[s], DateTime.Now)) : null;
			}
			t.StarterSlot = t.Pitchers[starterSlot] != null ? starterSlot : Array.FindIndex(t.Pitchers, p => p != null);
			// 작전실 and 개인작전 (systems session, OperationHandlers); owner 0 = no team, neutral.
			OperationHandlers.ApplyTo(t, owner, l);
			// 선수분석 graph (±5%), fielders out of position (−10%) and pitchers out of role (−15%), as in league games.
			LineupEffects.Apply(t, l, s_data.Catalog, owned);
			return t;
		}

		private static void Start(GameSession session, Ma9MPacket.PKS_CS_START_EXHIBITION_GAME packet)
		{
			Ma9MPacket.PKS_SC_START_EXHIBITION_GAME reply = new Ma9MPacket.PKS_SC_START_EXHIBITION_GAME();
			uint code = session.Team.UserCode;
			Lineup mine = s_db.GetLineup(code);
			if (!TryGet(code, out Match match) || mine == null)
			{
				reply.result.n = 1;
				session.Send(reply);
				return;
			}
			// Dev (test account only): MA9M_EXHIBITION_WANT=walkoff|hr4|hr5 replays the game with new
			// seeds until it has one, and the broadcast then joins two scenes before it.
			string want = IsTester(session) ? Environment.GetEnvironmentVariable("MA9M_EXHIBITION_WANT") : null;
			Play(code, session.Team.TeamName, mine, match, want, $"[{session.Id}]");
			reply.result.n = 0;
			session.Send(reply);
		}

		// The game of the team's match: simulated, its broadcast and box score kept (Save) until the result is given.
		// `who` starts the log lines (the session, or [auto]).
		private static void Play(uint code, string teamName, Lineup mine, Match match, string want, string who)
		{
			List<TeamCard> owned = s_db.GetCards(code).ToList();
			GameTeam home = Team(teamName, code, mine, match.CurSp, owned);
			GameTeam away = Team(match.AiName, match.AiCode, match.Ai, match.CurSp);
			// Bonus points at the development weight, then the league reference (TeamStrength).
			TeamStrength.AddBonuses(home, mine, owned);
			TeamStrength.ApplyLeagueReference(new[] { home, away });
			// 팀 특성: stat gains now (outside the league reference), rate effects on each model below.
			TraitEffects.Effect traits = TraitEffects.Of(code);
			TraitEffects.Raise(home, traits);
			BroadcastRecorder rec;
			LeagueRecorder line;
			SeasonModel model;
			SimStats st;
			int tries = 0;
			while (true)
			{
				int seed = s_rnd.Next();
				model = CardGame.Build(home, away, s_data.Env);
				TraitEffects.Apply(model, 0, traits);
				rec = new BroadcastRecorder(s_data.Presets, seed);
				rec.Attach(model, home, away);
				SeasonSimulator sim = new SeasonSimulator(model, new EngineParams { RunEnvironment = s_data.Env.RunEnvironment, ReachOnError = s_data.Env.ReachOnError, MaxInnings = LeagueRules.MaxInnings }, (ulong)seed);
				line = new LeagueRecorder(rec);  // line score, errors, double plays for the box score
				st = sim.PlayOne(0, 1, line);
				match.HomeRuns = model.TeamBatters[0].Sum(i => (int)st.Bat(i, BS.R));
				match.AwayRuns = model.TeamBatters[1].Sum(i => (int)st.Bat(i, BS.R));
				int at = string.IsNullOrEmpty(want) ? -1
					: want == "walkoff" ? rec.WalkOffScene(match.HomeRuns, match.AwayRuns)
					: rec.Events.Where(e => e.tag == want).Select(e => e.scene).DefaultIfEmpty(-1).Last();
				match.DevJoin = at >= 0 ? Math.Max(0, at - 2) : -1;
				if (string.IsNullOrEmpty(want) || at >= 0 || ++tries >= 5000)
				{
					if (!string.IsNullOrEmpty(want))
					{
						ServerLog.Info($"{who} dev: {want} at scene {at} after {tries} replays");
					}
					break;
				}
			}
			match.Buff = rec.Finish();
			match.Scenes = match.Buff[0];
			match.Detail = LeagueService.BoxScore(model, st, line, mine, match.Ai);
			match.Played = false;
			match.StartedAt = DateTime.Now;
			Save(code, match);
			ServerLog.Info($"{who} exhibition {teamName} {match.HomeRuns} : {match.AwayRuns} {match.AiName} ({rec.SceneCount} scenes, {match.Buff.Length} bytes)");
		}

		private static void Broadcast(GameSession session, Ma9MPacket.PKS_CS_ENTER_EXHIBITION_BROADCAST packet)
		{
			uint code = session.Team.UserCode;
			Lineup mine = s_db.GetLineup(code);
			if (!TryGet(code, out Match match) || match.Buff == null || mine == null)
			{
				ServerLog.Warn($"[{session.Id}] exhibition broadcast without a game");
				return;
			}
			Ma9MPacket.PKS_SC_ENTER_EXHIBITION_BROADCAST reply = new Ma9MPacket.PKS_SC_ENTER_EXHIBITION_BROADCAST();
			reply.weather.n = 0;
			// Dev: MA9M_EXHIBITION_SKIP=n joins the broadcast n scenes before the end (the client
			// fast-forwards to scene_num), so the ending can be checked without watching a game.
			// Only the automated test account is affected; real players always see the whole game.
			bool tester = IsTester(session);
			int skip = tester && int.TryParse(Environment.GetEnvironmentVariable("MA9M_EXHIBITION_SKIP"), out int n) ? Math.Max(0, match.Scenes - n) : 0;
			if (tester && match.DevJoin >= 0)
			{
				skip = match.DevJoin;
			}
			reply.scene_num.n = (short)skip;
			// Joined late (back after closing the app, user 2026-10-21): the game runs on in real time
			// (8 s a scene, the client's FastForwardingScene), so it picks up there, or at the end.
			reply.scene_time.n = skip == 0 && match.StartedAt != default ? (long)Math.Max(0, (DateTime.Now - match.StartedAt).TotalSeconds) : 0;
			reply.away_team_name.str = match.AiName;
			reply.home_team_name.str = session.Team.TeamName;
			reply.away_emblem.n = match.AiEmblem;
			reply.home_emblem.n = session.Team.Emblem;
			reply.away_batters.array = match.Ai.Batters.Where(i => i != 0).Select(i => Info(match.AiCode, i)).ToArray();
			reply.away_fielders.array = match.Ai.Fielders.Select(f => new PByte { n = (byte)(f - 1) }).ToArray();
			reply.away_pitchers.array = match.Ai.Pitchers.Select(i => Info(match.AiCode, i)).ToArray();
			reply.home_batters.array = mine.Batters.Where(i => i != 0).Select(i => Info(code, i)).ToArray();
			// The batter panel: the user's batters' league season (engine session, 2026-10-30); the client
			// adds this game's at-bats as they happen. An exhibition is not in the season rows.
			reply.home_batters_record.array = mine.Batters.Where(i => i != 0).Select(i => SeasonRecord(LeagueService.Store.PlayerSeason(session.Team.UserCode, i))).ToArray();
			reply.home_fielders.array = mine.Fielders.Select(f => new PByte { n = (byte)(f - 1) }).ToArray();
			reply.home_pitchers.array = mine.Pitchers.Select(i => Info(code, i)).ToArray();
			reply.home_pitchers_record.array = reply.home_pitchers.array.Select(_ => new BROADCAST_PITCHER_RECORD()).ToArray();
			reply.buff.bytes = match.Buff;
			reply.cur_sp.n = match.CurSp;
			reply.event_flag.n = 0;
			// The user's club in its team colour's season (Uniforms); the AI side keeps the client's default for its emblem.
			reply.home_team_uniform.array = Uniforms.For(session.Team.UserCode, session.Team.Emblem, null, true);
			session.Send(reply);
		}

		private static void PastScene(GameSession session, Ma9MPacket.PKS_CS_EXHIBITION_PAST_SCENE packet)
		{
			// The client reports the last scene when the game is over and waits for the result.
			if (TryGet(session.Team.UserCode, out Match match) && match.Buff != null && packet.scene_num.n >= match.Scenes - 1)
			{
				if (!match.Played)
				{
					SendResult(session);
				}
				return;
			}
			// Exhibitions are not live: every scene may follow at once.
			Ma9MPacket.PKS_SC_EXHIBITION_PAST_SCENE reply = new Ma9MPacket.PKS_SC_EXHIBITION_PAST_SCENE();
			reply.scene_num.n = packet.scene_num.n;
			session.Send(reply);
		}

		private static void Result(GameSession session, Ma9MPacket.PKS_CS_EXHIBITION_GAME_RESULT packet)
		{
			SendResult(session);
		}

		private static void SendResult(GameSession session)
		{
			uint code = session.Team.UserCode;
			Ma9MPacket.PKS_SC_EXHIBITION_GAME_RESULT reply = new Ma9MPacket.PKS_SC_EXHIBITION_GAME_RESULT();
			int earned = 0, prize = 0;
			byte prizeCondition = 2;
			if (TryGet(code, out Match match) && !match.Played)
			{
				earned = Settle(code, match, out prize, out prizeCondition);
			}
			int balance = s_db.AddMp(code, earned);
			session.Team.Mp = balance;
			reply.earned_point.n = earned;
			reply.cur_point.n = balance;
			reply.prize_card.n = prize;
			reply.prize_card_condition.n = prizeCondition;
			reply.CheerAddMP.n = 0;
			session.Send(reply);
		}

		// The result of a played match: its MP (the caller adds it), the missions, and on a win maybe one of the
		// opponent's cards (0 if none, with its condition).
		private static int Settle(uint code, Match match, out int prize, out byte prizeCondition)
		{
			prize = 0;
			prizeCondition = 2;
			match.Played = true;
			Forget(code);
			bool win = match.HomeRuns > match.AwayRuns;
			int earned = RewardMp[match.Grade, win ? 0 : match.HomeRuns == match.AwayRuns ? 1 : 2];
			s_finished[code] = (match, earned);
			// 오늘의 미션 시범경기 2회, 업적 시범경기 승리.
			Missions.Add(code, Missions.Exhibitions);
			if (win) Missions.Add(code, Missions.ExhibitionWins);
			if (win && match.Grade == 2) Missions.Add(code, Missions.ExhibitionHardWins);  // 업적 어려움 승리 (시스템 세션, 10-22)
			if (win && s_rnd.NextDouble() < PrizeChance)
			{
				HashSet<int> owned = s_db.GetCards(code).Select(c => c.Idx).ToHashSet();
				List<int> pool = match.Ai.Batters.Concat(match.Ai.Pitchers).Where(i => i != 0 && !owned.Contains(i)).ToList();
				if (pool.Count > 0)
				{
					prize = pool[s_rnd.Next(pool.Count)];
					s_db.AddCards(code, new[] { prize }, RosterHandlers.StartContract);
					prizeCondition = Condition.For(code, prize, DateTime.Now);
				}
			}
			return earned;
		}

		// The box score of the team's exhibition as a league scoreboard: the finished one (after its result,
		// with the MP it paid) or the one still on. Null when there is none (or it predates box scores).
		public static Ma9MPacket.PKS_SC_GAME_SCOREBOARD Board(GameSession session)
		{
			if (session.Team == null) return null;
			uint code = session.Team.UserCode;
			Match match;
			int mp = 0;
			if (s_finished.TryGetValue(code, out (Match match, int earned) f)) (match, mp) = f;
			else if (!TryGet(code, out match) || match.Buff == null) return null;
			GameDetail d = match.Detail;
			if (d == null) return null;
			DateTime day = DateTime.Now;  // a 컨디션 회복제 counts by the hour (Condition.For)
			Ma9MPacket.PKS_SC_GAME_SCOREBOARD r = new Ma9MPacket.PKS_SC_GAME_SCOREBOARD();
			r.is_my_home.n = 1;
			r.home_team_name.str = session.Team.TeamName;
			r.home_team_emblem.n = session.Team.Emblem;
			r.away_team_name.str = match.AiName;
			r.away_team_emblem.n = match.AiEmblem;
			// The user's cards open the user's own card window (eUserNum.ONESELF); the AI side's have no club
			// (253: the server's card window looks the card up without one).
			r.home_user_num.n = 254;
			r.away_user_num.n = 253;
			r.home_score.array = LeagueHandlers.Innings(d.HomeLine);
			r.away_score.array = LeagueHandlers.Innings(d.AwayLine);
			int[] h = d.Stats[0], a = d.Stats[1];
			r.home_hit.n = (byte)h[0]; r.away_hit.n = (byte)a[0];
			r.home_error.n = (byte)h[1]; r.away_error.n = (byte)a[1];
			r.home_BB.n = (byte)h[2]; r.away_BB.n = (byte)a[2];
			r.home_HR.n = (byte)h[3]; r.away_HR.n = (byte)a[3];
			r.home_SB.n = (byte)h[4]; r.away_SB.n = (byte)a[4];
			r.home_K.n = (byte)h[5]; r.away_K.n = (byte)a[5];
			r.home_DP.n = (byte)h[6]; r.away_DP.n = (byte)a[6];
			r.get_point.n = mp;
			LeagueHandlers.SetCard(r.home_best, code, d.BestIdx[0], day);
			LeagueHandlers.SetCard(r.home_worst, code, d.WorstIdx[0], day);
			LeagueHandlers.SetCard(r.away_best, match.AiCode, d.BestIdx[1], day);
			LeagueHandlers.SetCard(r.away_worst, match.AiCode, d.WorstIdx[1], day);
			r.win_pitcher_index.n = d.WinIdx;
			r.save_pitcher_index.n = d.SaveIdx;
			r.lose_pitcher_index.n = d.LossIdx;
			r.prev_game_num.n = 0;
			r.next_game_num.n = 0;
			r.CheerAddMP.n = 0;
			return r;
		}

		// Most spots C..RF the batters can cover at once, each by a man who can play it (a matching, augmenting paths).
		private static int Coverable(List<CardInfo> batters)
		{
			int[] holder = Enumerable.Repeat(-1, 8).ToArray();
			bool Place(int b, bool[] seen)
			{
				for (int pos = 0; pos < 8; pos++)
				{
					if (!batters[b].CanField(pos) || seen[pos]) continue;
					seen[pos] = true;
					if (holder[pos] < 0 || Place(holder[pos], seen))
					{
						holder[pos] = b;
						return true;
					}
				}
				return false;
			}
			return Enumerable.Range(0, batters.Count).Count(b => Place(b, new bool[8]));
		}

		// Fielders who do not play their spot (neither their main position nor one they can field, CardInfo.CanField)
		// and pitchers out of their role (SP1-5 starters, RP1-4 middle relievers, SU, CP).
		public static (int fielders, int pitchers, string where) Misfits(Lineup l, CardCatalog catalog)
		{
			int f = 0, p = 0;
			List<string> where = new List<string>();
			string[] spots = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF" };
			for (int pos = 0; pos < 8; pos++)
			{
				int slot = l.Fielders[pos] - 1;
				CardInfo c = slot >= 0 && slot < 9 ? catalog.Get(l.Batters[slot]) : null;
				if (c != null && (int)c.MainPosition != pos + 1 && !c.CanField(pos))
				{
					f++;
					where.Add($"{spots[pos]} {c.Name}({c.MainPosition})");
				}
			}
			CardPos[] role = { CardPos.SP, CardPos.SP, CardPos.SP, CardPos.SP, CardPos.SP, CardPos.RP, CardPos.RP, CardPos.RP, CardPos.RP, CardPos.SU, CardPos.CP };
			for (int s = 0; s < Lineup.PitcherSlots; s++)
			{
				CardInfo c = catalog.Get(l.Pitchers[s]);
				if (c != null && c.MainPosition != role[s])
				{
					p++;
					where.Add($"{role[s]}{(s < 5 ? s + 1 : s < 9 ? s - 4 : 0)} {c.Name}({c.MainPosition})");
				}
			}
			return (f, p, string.Join(", ", where));
		}

		// Dev (--exhibitiontest, a copy of the data): AI teams across every ball range — the total in range, every
		// spot's own man (user, 2026-10-22: "멀티포지션 선수가 아니면 안 맞는 포지션에서 뛰지 않게"), every pitcher in
		// his role ("중계 투수가 셋업에 뛰고"). The user teams' saved lineups are only reported.
		public static int SelfTest(Database db, GameData data, IEnumerable<(uint code, string name)> users, int teams)
		{
			s_db = db;
			s_data = data;
			int badBall = 0, badField = 0, badRole = 0, badBench = 0, shown = 0;
			double starters = 0;
			for (int k = 0; k < teams; k++)
			{
				int mine = 60 + k * 7 % 140;
				(int lo, int hi) = Ranges(mine)[k % 3];
				Lineup l = AiTeam(lo, hi, out int total);
				(int f, int p, string where) = Misfits(l, data.Catalog);
				if (total < lo || total > hi) badBall++;
				badField += f;
				badRole += p;
				// The bench (batting slots 10-14) is 1볼 (user, 2026-10-24).
				badBench += l.Batters.Skip(9).Count(i => i != 0 && data.Catalog.Get(i)?.Ball > AiBenchBall);
				starters += l.Batters.Take(9).Sum(i => data.Catalog.Get(i)?.Ball ?? 0) / 9.0;
				if ((f > 0 || p > 0) && shown++ < 5) ServerLog.Info($"  AI team {k} ({total} balls): {where}");
			}
			int bad = badBall + badField + badRole + badBench;
			ServerLog.Info($"{(bad == 0 ? "ok  " : "FAIL")} {teams} AI teams: balls out of range {badBall}, fielders out of position {badField}, pitchers out of role {badRole}, bench above {AiBenchBall}볼 {badBench} (regulars {starters / teams:F2}볼 on average)");
			// The team-year decks of 메이저/챔피언스 (AiTeams.MakePool) as LineupBuilder arranges them: fewer spots played by
			// men who can than the deck's batters could cover (a matching) is a fault (user, 2026-10-31: 2009 SK's DH in
			// centre field while 박재홍, who can play it, stood in right).
			int decks = 0, shortDecks = 0, outOf = 0;
			foreach (TeamColorSim.TeamYearDeck d in TeamColorSim.TeamYearDecks(data, LeagueRules.BallCap[LeagueRules.ChampionsTier]))
			{
				decks++;
				Lineup l = LineupBuilder.Build(d.Cards.Select(x => x.Idx), data.Catalog);
				int fielded = 0;
				for (int pos = 0; pos < 8; pos++)
				{
					int slot = l.Fielders[pos] - 1;
					CardInfo c = slot >= 0 && slot < 9 ? data.Catalog.Get(l.Batters[slot]) : null;
					if (c != null && c.CanField(pos)) fielded++;
				}
				int coverable = Coverable(d.Cards.Where(c => c.IsBatter).ToList());
				outOf += 8 - fielded;
				if (fielded < coverable)
				{
					if (shortDecks++ < 5) ServerLog.Info($"  team-year {d.Year} {d.TeamCode}: {fielded} spots fielded of {coverable} coverable — {Misfits(l, data.Catalog).where}");
				}
			}
			ServerLog.Info($"{(shortDecks == 0 ? "ok  " : "FAIL")} {decks} team-year decks: {shortDecks} field fewer spots than their batters could cover; spots nobody in the deck can play {outOf}");
			bad += shortDecks;
			foreach ((uint code, string name) in users)
			{
				Lineup l = db.GetLineup(code);
				if (l == null) continue;
				(int f, int p, string where) = Misfits(l, data.Catalog);
				ServerLog.Info($"     user {name}: fielders out of position {f}, pitchers out of role {p}{(where.Length > 0 ? " — " + where : string.Empty)}");
			}
			return bad == 0 ? 0 : 1;
		}

		private static void GiveUp(GameSession session, Ma9MPacket.PKS_CS_GIVEUP_EXHIBITION_GAME packet)
		{
			s_matches.Remove(session.Team.UserCode);
			Forget(session.Team.UserCode);
			Ma9MPacket.PKS_SC_GIVEUP_EXHIBITION_GAME reply = new Ma9MPacket.PKS_SC_GIVEUP_EXHIBITION_GAME();
			reply.result.n = 0;
			session.Send(reply);
		}

		private static BROADCAST_BATTER_RECORD SeasonRecord(PlayerSeason p)
		{
			BROADCAST_BATTER_RECORD r = new BROADCAST_BATTER_RECORD();
			if (p == null || p.Pitcher) return r;
			r.at_bats.n = (short)Math.Min(short.MaxValue, p.AB);
			r.hits.n = (byte)Math.Min(255, p.H);
			r.homeruns.n = (byte)Math.Min(255, p.HR);
			r.rbi.n = (byte)Math.Min(255, p.RBI);
			r.batting_average.f = p.AB > 0 ? p.H / (float)p.AB : 0f;
			r.runs.n = (byte)Math.Min(255, p.R);
			r.base_on_balls.n = (byte)Math.Min(255, p.BB);
			r.steal_base.n = (byte)Math.Min(255, p.SB);
			return r;
		}
	}
}
