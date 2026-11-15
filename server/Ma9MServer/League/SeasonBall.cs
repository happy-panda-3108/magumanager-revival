using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 공인구 (user, 2026-11-15 via the engine session: 11:29 "리그 시작할 때마다 공인구를 다르게 해서 반발력 계수에 따라 해당 시즌의 평균
	// 득점과 실점이 다르게끔", 11:32 "공인구의 반발력 계수는 평균 kbo 공인구 반발력에서 랜덤하게 분포되어서 등장하게", 11:33 "선택지 가에
	// 공인구 시스템을 얹어보자"): one ball a season, the same for every league and game, its 반발계수 drawn around the KBO average.
	// This class (systems session) draws it, keeps it per common-calendar season (LeagueState.Start) and says it in the lobby; the
	// engine reads Cor / RunRatio for league games, 자동 편성 and 특성 추천 and turns them into its rates.
	// Until From is set (MA9M_BALL_FROM, the first season with a drawn ball) nothing is drawn and every season plays the standard ball.
	// Still open with the user (11-15 11:3x): the distribution (Basis), the wording, the preview at the postseason, the first season.
	public static class SeasonBall
	{
		// The standard ball: 팀당 5.00점 (the engine's reference after its 11-15 fix). The engine may set StandardCor.
		public const double StandardRuns = 5.00;

		public const double StandardCor = 0.4200;

		// 반발계수 0.01 ≈ 팀당 1점 (KBO 2018 → 2019: 5.49 → 4.49 when the rule moved the ball about 0.01).
		public const double CorPerRun = 0.01;

		// KBO runs per team per game (Statiz 투구 기본 1982~2026): mean and SD, as a ratio to the mean; the range is ±MaxZ SD,
		// 팀당 3.86~6.14점 (반발계수 0.4086~0.4314). User 11:54: "역대 전체 분포에서 나오게끔 하고, 굳이 정규분포식보다는 저반발 고반발 평균
		// 다 1/3 정도로 비슷한 분포로 나오게끔" — three bands of equal runs width (저반발 3.86~4.62, 표준 4.62~5.38, 고반발 5.38~6.14),
		// one picked at a third each and the runs drawn evenly inside it (so evenly over the whole range).
		public static (string name, double mean, double sd) Basis = ("역대", 4.56, 0.52);

		public const double MaxZ = 2.0;

		// z (in SD of the basis) of the band edges: −2, −2/3, +2/3, +2. Names (user 12:0x: "고반반, 저반발 3글자는 너무 짜치니까 B, S, N
		// 이렇게 하자. 빅볼 스몰볼 노말볼 줄임말로 쓰고 야구공 안에 글씨가 들어가있는 느낌으로"): 스몰볼 S (저반발), 노말볼 N (표준),
		// 빅볼 B (고반발); the app draws the letter in a baseball (Ma9MBallBadge).
		public static readonly string[] Bands = { "스몰볼", "노말볼", "빅볼" };

		public static readonly string[] BandMeaning = { "저반발", "표준", "고반발" };

		// 다음 시즌 공을 포스트시즌이 시작될 때(48 h) 미리 알림: off (user 2026-11-15 18:46 via the engine session, "시즌 시작 때만 알림" over
		// the recommended preview). The season's own ball still comes at its first lobby visit.
		public static bool PreviewAtPostseason = false;

		public static DateTime? From = ParseFrom(Environment.GetEnvironmentVariable("MA9M_BALL_FROM"));

		public sealed class Ball
		{
			public DateTime Start;
			public double Z;
			public double Cor;
			public double Runs;
			public string Label;
			public string Basis;
		}

		private static readonly Dictionary<DateTime, Ball> s_balls = new Dictionary<DateTime, Ball>();

		private static readonly object s_lock = new object();

		private static readonly Random s_rnd = new Random();

		private static string s_cs;

		private const string KeyFormat = "yyyy-MM-dd HH:mm";

		private static DateTime? ParseFrom(string text) =>
			DateTime.TryParseExact(text ?? string.Empty, KeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime t) ? t : null;

		public static void Init(string dbPath)
		{
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS season_ball (
					cycle_start TEXT PRIMARY KEY, z REAL NOT NULL, cor REAL NOT NULL, runs REAL NOT NULL,
					label TEXT NOT NULL, basis TEXT NOT NULL, made TEXT NOT NULL);
					CREATE TABLE IF NOT EXISTS season_ball_seen (
					user_code INTEGER NOT NULL, cycle_start TEXT NOT NULL, kind INTEGER NOT NULL, seen TEXT NOT NULL,
					PRIMARY KEY (user_code, cycle_start, kind))";
				cmd.ExecuteNonQuery();
			}
			lock (s_lock)
			{
				s_balls.Clear();
				using SqliteCommand cmd = c.CreateCommand();
				cmd.CommandText = "SELECT cycle_start, z, cor, runs, label, basis FROM season_ball";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read())
				{
					Ball b = Read(r);
					if (b != null) s_balls[b.Start] = b;
				}
			}
			ServerLog.Info($"공인구: {(From == null ? "표준 공(뽑기 시작 시즌 없음)" : $"{From:MM-dd HH:mm} 시즌부터 뽑음, 기준 {Basis.name} {Basis.mean}/{Basis.sd}")}, 저장된 공 {s_balls.Count}개");
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static Ball Read(SqliteDataReader r)
		{
			if (!DateTime.TryParseExact(r.GetString(0), KeyFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime start)) return null;
			return new Ball { Start = start, Z = r.GetDouble(1), Cor = r.GetDouble(2), Runs = r.GetDouble(3), Label = r.GetString(4), Basis = r.GetString(5) };
		}

		// 반발계수 of the season that starts at cycleStart (LeagueState.Start); StandardCor when it plays the standard ball.
		public static double Cor(DateTime cycleStart) => Get(cycleStart)?.Cor ?? StandardCor;

		// The season's expected runs over the standard ball's (1.0 = standard).
		public static double RunRatio(DateTime cycleStart) => (Get(cycleStart)?.Runs ?? StandardRuns) / StandardRuns;

		// The season's ball, drawn on first use when the season is From or later and no later than the next one; null = standard.
		// Safe from any thread (특성 추천 runs off the loop).
		public static Ball Get(DateTime cycleStart)
		{
			lock (s_lock)
			{
				if (s_balls.TryGetValue(cycleStart, out Ball b)) return b;
				if (s_cs == null || From == null || cycleStart < From.Value || !LeagueCalendar.OnCalendar(cycleStart)
					|| cycleStart > LeagueCalendar.CycleStart(DateTime.Now) + LeagueCalendar.Cycle) return null;
				return Draw(cycleStart);
			}
		}

		// Inside s_lock. INSERT OR IGNORE, then the stored row, so a ball is never drawn twice for a season.
		private static Ball Draw(DateTime start)
		{
			Ball b = Make(start, DrawZ());
			using SqliteConnection c = Open();
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "INSERT OR IGNORE INTO season_ball (cycle_start, z, cor, runs, label, basis, made) VALUES ($s, $z, $c, $r, $l, $b, $m)";
				cmd.Parameters.AddWithValue("$s", start.ToString(KeyFormat, CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$z", b.Z);
				cmd.Parameters.AddWithValue("$c", b.Cor);
				cmd.Parameters.AddWithValue("$r", b.Runs);
				cmd.Parameters.AddWithValue("$l", b.Label);
				cmd.Parameters.AddWithValue("$b", b.Basis);
				cmd.Parameters.AddWithValue("$m", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
				cmd.ExecuteNonQuery();
			}
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT cycle_start, z, cor, runs, label, basis FROM season_ball WHERE cycle_start = $s";
				cmd.Parameters.AddWithValue("$s", start.ToString(KeyFormat, CultureInfo.InvariantCulture));
				using SqliteDataReader r = cmd.ExecuteReader();
				if (r.Read()) b = Read(r) ?? b;
			}
			s_balls[start] = b;
			ServerLog.Info($"공인구: {start:MM-dd HH:mm} 시즌 반발계수 {b.Cor:0.0000} · {b.Label}, 예상 팀당 {b.Runs:0.00}점 (z {b.Z:+0.00;-0.00}, {b.Basis})");
			return b;
		}

		internal static Ball Make(DateTime start, double z)
		{
			double runs = Math.Round(StandardRuns * (Basis.mean + z * Basis.sd) / Basis.mean, 2);
			double cor = Math.Round(StandardCor + (runs - StandardRuns) * CorPerRun, 4);
			return new Ball { Start = start, Z = z, Cor = cor, Runs = runs, Label = Label(runs), Basis = Basis.name };
		}

		// A band at a third each, then evenly inside it (user 11:54).
		private static double DrawZ()
		{
			int band = s_rnd.Next(Bands.Length);
			double width = 2 * MaxZ / Bands.Length;
			return -MaxZ + width * (band + s_rnd.NextDouble());
		}

		// The band by the expected runs: the range cut in three equal parts.
		public static int BandOf(double runs)
		{
			double lo = RunsAt(-MaxZ), hi = RunsAt(MaxZ);
			return Math.Clamp((int)Math.Floor((runs - lo) / ((hi - lo) / Bands.Length)), 0, Bands.Length - 1);
		}

		private static double RunsAt(double z) => StandardRuns * (Basis.mean + z * Basis.sd) / Basis.mean;

		public static string Label(double runs) => Bands[BandOf(runs)];

		// The standings title's prefix (LeagueHandlers.RankInfo puts it before the sponsor: "[빅볼] 마구매니저배 프로야구 순위표"; user
		// 11:54 "리그 순위표에서도 … 공인구 표시가 되어 있으면"). Today's app shows it as text; a later app can draw it as a badge
		// (the same tag is the app's way to learn the season's ball). Empty for the standard ball.
		// The ball is only for 트리플A and up (user 12:1x, BallEffect.Applies); 비기너·루키·더블A say "[노말볼] " once the draws have
		// started, so their badge shows N.
		public static string Tag(LeagueState s)
		{
			if (s == null || From == null || s.Start < From.Value) return string.Empty;
			if (!BallEffect.Applies(s.Tier)) return $"[{Bands[1]}] ";
			return Get(s.Start) is Ball b ? $"[{b.Label}] " : string.Empty;
		}

		// The popup's lines (PopupText: no wrapping).
		internal static List<string> Lines(Ball b, bool next)
		{
			int band = BandOf(b.Runs);
			List<string> lines = new List<string>
			{
				$"{(next ? "다음 시즌" : "이번 시즌")} 공인구: {Bands[band]}({BandMeaning[band]})",
				$"반발계수 {b.Cor:0.0000} · 예상 득점 팀당 {b.Runs:0.0}점"
			};
			if (band == 2)
			{
				lines.Add("홈런과 장타가 늘어 파워 타자와");
				lines.Add("홈런을 덜 맞는 투수가 유리합니다.");
			}
			else if (band == 0)
			{
				lines.Add("홈런이 크게 줄어 출루·주루·수비와");
				lines.Add("투수진의 한 점이 더 귀해집니다.");
			}
			else
			{
				lines.Add("평균적인 득점이 나는 공입니다.");
			}
			return lines;
		}

		// At the lobby (LobbyHandlers, after 시즌 완주 보너스): this season's ball once per team, and, from the postseason on, the
		// next season's (PreviewAtPostseason). Nothing while every season plays the standard ball.
		public static void Deliver(GameSession session)
		{
			if (session?.Team == null || s_cs == null || From == null) return;
			uint user = session.Team.UserCode;
			LeagueState s = LeagueService.Store?.Load(user);
			if (s == null) return;
			// 트리플A and up only (user 12:1x); the leagues below keep the 노말볼 and get no popup.
			Ball now = BallEffect.Applies(s.Tier) ? Get(s.Start) : null;
			if (now != null && MarkSeen(user, now.Start, 0))
			{
				Popup(session, "공인구", Lines(now, false));
			}
			int nextTier = s.NextTier >= 0 ? s.NextTier : s.Tier;
			if (PreviewAtPostseason && BallEffect.Applies(nextTier) && DateTime.Now >= s.Start + LeagueCalendar.PostStart)
			{
				Ball next = Get(s.Start + LeagueCalendar.Cycle);
				if (next != null && MarkSeen(user, next.Start, 1))
				{
					Popup(session, "다음 시즌 공인구", Lines(next, true));
				}
			}
		}

		// True the first time (the row is new).
		private static bool MarkSeen(uint user, DateTime start, int kind)
		{
			using SqliteConnection c = Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "INSERT OR IGNORE INTO season_ball_seen (user_code, cycle_start, kind, seen) VALUES ($u, $s, $k, $t)";
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$s", start.ToString(KeyFormat, CultureInfo.InvariantCulture));
			cmd.Parameters.AddWithValue("$k", kind);
			cmd.Parameters.AddWithValue("$t", DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
			return cmd.ExecuteNonQuery() > 0;
		}

		private static void Popup(GameSession session, string title, List<string> lines)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = 1;
			popup.Messages.array = new[] { new PLongString { str = title }, new PLongString { str = string.Join("\n", lines) } };
			session.Send(popup);
			ServerLog.Info($"[{session.Id}] 공인구 알림: {string.Join(" / ", lines)}");
		}

		// Dev (--balltest, a data copy): draws from a set From, the stored ball read back after a restart (cache cleared), the
		// spread of many draws, popup widths.
		public static int SelfTest(string dbPath)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[balltest] {(ok ? "ok  " : "FAIL")} {what}");
			}
			DateTime cur = LeagueCalendar.CycleStart(DateTime.Now), next = cur + LeagueCalendar.Cycle;
			From = null;
			Init(dbPath);
			Check(Get(cur) == null && Cor(cur) == StandardCor && RunRatio(cur) == 1.0, "뽑기 시작 시즌이 없으면 표준 공");
			From = cur;
			Ball a = Get(cur), b = Get(next), far = Get(next + LeagueCalendar.Cycle), before = Get(cur - LeagueCalendar.Cycle);
			Check(a != null && b != null && far == null && before == null, $"이번 {a?.Cor:0.0000} {a?.Label} / 다음 {b?.Cor:0.0000} {b?.Label} / 그다음·지난 시즌은 표준");
			Init(dbPath);
			Ball a2 = Get(cur);
			Check(a2 != null && a2.Cor == a.Cor && a2.Runs == a.Runs && a2.Z == a.Z, "다시 켠 뒤 같은 공을 읽음");
			Check(Get(cur.AddHours(1)) == null, "달력에 없는 시각은 표준");
			LeagueState top = new LeagueState { Tier = LeagueRules.MajorTier - 1, Start = cur }, low = new LeagueState { Tier = LeagueRules.MajorTier - 2, Start = cur };
			LeagueState early = new LeagueState { Tier = LeagueRules.MajorTier - 1, Start = cur - LeagueCalendar.Cycle };
			Check(Tag(top) == $"[{a.Label}] " && Tag(low) == "[노말볼] " && Tag(early) == string.Empty,
				$"순위표 앞글: 트리플A \"{Tag(top)}마구매니저배 프로야구 순위표\", 그 아래 \"{Tag(low)}\", 뽑기 전 시즌은 없음");
			int n = 30000;
			List<Ball> many = Enumerable.Range(0, n).Select(_ => Make(cur, DrawZ())).ToList();
			double mean = many.Average(x => x.Runs), lo = many.Min(x => x.Runs), hi = many.Max(x => x.Runs);
			Dictionary<string, int> byLabel = many.GroupBy(x => x.Label).ToDictionary(g => g.Key, g => g.Count());
			Check(Math.Abs(mean - StandardRuns) < 0.03 && lo >= Make(cur, -MaxZ).Runs && hi <= Make(cur, MaxZ).Runs
				&& Bands.All(l => Math.Abs((double)byLabel.GetValueOrDefault(l) / n - 1.0 / 3) < 0.015),
				$"{n}번 뽑기: 평균 {mean:0.00}점, {lo:0.00}~{hi:0.00}, 반발계수 {many.Min(x => x.Cor):0.0000}~{many.Max(x => x.Cor):0.0000}; "
				+ string.Join(", ", Bands.Select(l => $"{l} {100.0 * byLabel.GetValueOrDefault(l) / n:0.0}% "
					+ $"({many.Where(x => x.Label == l).Min(x => x.Runs):0.00}~{many.Where(x => x.Label == l).Max(x => x.Runs):0.00}점)")));
			Check(Label(3.86) == "스몰볼" && Label(4.61) == "스몰볼" && Label(4.63) == "노말볼" && Label(5.37) == "노말볼" && Label(5.39) == "빅볼" && Label(6.14) == "빅볼",
				$"갈래 경계: 4.62 / 5.38점 ({RunsAt(-MaxZ / 3):0.000} / {RunsAt(MaxZ / 3):0.000})");
			foreach (double z in new[] { -MaxZ, -1.0, 0.0, 1.0, MaxZ })
			{
				foreach (bool isNext in new[] { false, true })
				{
					List<string> lines = Lines(Make(cur, z), isNext);
					Check(lines.Count <= 5 && lines.All(l => PopupText.Px(l) <= PopupText.Limit),
						$"창 글 z {z:+0.0;-0.0}: 가장 넓은 줄 {lines.Max(PopupText.Px):F0} px: {string.Join(" / ", lines)}");
				}
			}
			ServerLog.Info($"[balltest] {problems} problem(s)");
			return problems;
		}
	}
}
