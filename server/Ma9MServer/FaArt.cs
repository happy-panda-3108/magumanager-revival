using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// FA 카드를 영입한 구단 유니폼으로 (systems session, 2026-11-17; user 10:2x to the engine session after its previews: "오 유니폼 바뀐거
	// 마음에 드는데, 아주 훌륭함. 내가 정말 원하는 작업임"). The engine session's tools\scripts\fa_club_art.py draws an FA card in a club's
	// uniform of the card's season into cdn\KBO\CardImage\<v>\fa\<idx>_<club>.png and cdn\KBO\CardDetailImage\<v>\fa\<idx>_<club>_detail.png
	// (club = team code 0-20, a season logo's base: EmblemYears.BaseOf). This side:
	//   - the render list: <data>\fa_art_queue.tsv, "idx<TAB>club" a line, written at a signing (FaMarket.SignWith) and at a lobby visit
	//     for any FA card of the team still without its picture (a club change, cards signed before this); then the script is started at
	//     BelowNormal without a window when MA9M_FA_ART = the repo folder (run_server.bat; test servers leave it unset and only write the
	//     list). One run at a time; lines added meanwhile start another run when it ends. The script skips pictures that exist.
	//   - the app (ClientMark) gets POPUP type ListType [club, idx, idx, …, idx_club, …] at each lobby visit: its FA cards whose picture
	//     is there, then (11-18, Others) the other real clubs' FA cards of its league. It draws those from fa\ (Ma9MFaArt); everything
	//     else (AI clubs, other leagues) keeps the FA art.
	public static class FaArt
	{
		public const byte ListType = 34;

		public const string ClientMark = "ma9m-faart1";

		private static string s_dataDir;

		private static string s_cs;

		private static string s_repo;

		private static readonly object s_lock = new object();

		private static readonly HashSet<string> s_queued = new HashSet<string>();

		private static Process s_run;

		private static bool s_dirty;

		private static string QueueFile => Path.Combine(s_dataDir, "fa_art_queue.tsv");

		private static string LogFile => Path.Combine(s_dataDir, "fa_art.log");

		// AlbumHandlers.Register (dbPath is <data>\ma9m.db).
		public static void Init(string dbPath)
		{
			s_dataDir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
			s_cs = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
			string repo = Environment.GetEnvironmentVariable("MA9M_FA_ART");
			s_repo = !string.IsNullOrWhiteSpace(repo) && Directory.Exists(repo) ? repo : null;
			ServerLog.Info($"FA 유니폼 그림: {(s_repo == null ? "그릴 목록만 씀(MA9M_FA_ART 없음)" : "켜짐, " + Script)}");
		}

		private static string Python => Path.Combine(s_repo, "tools", "venv", "Scripts", "python.exe");

		private static string Script => Path.Combine(s_repo, "tools", "scripts", "fa_club_art.py");

		public static string FacePath(int idx, int club) => Path.Combine(s_dataDir, "cdn", "KBO", "CardImage", Cdn.CardArtVersion, "fa", $"{idx}_{club}.png");

		// The club whose uniform a team's FA cards wear: its emblem's club (a season logo counts as its club).
		public static int ClubOf(uint user)
		{
			if (s_cs == null)
			{
				return -1;
			}
			using SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			using SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = "SELECT emblem FROM team WHERE user_code = $u";
			cmd.Parameters.AddWithValue("$u", (long)user);
			object v = cmd.ExecuteScalar();
			return v == null || v is DBNull ? -1 : EmblemYears.BaseOf(Convert.ToInt32(v));
		}

		// FaMarket.SignWith after a signing.
		public static void Signed(uint user, int idx)
		{
			try
			{
				int club = ClubOf(user);
				if (club >= 0 && !File.Exists(FacePath(idx, club)))
				{
					Enqueue(idx, club);
				}
			}
			catch (Exception e)
			{
				ServerLog.Warn($"FA 유니폼 그림 목록: {e.Message}");
			}
		}

		// The other real clubs' FA cards in the user's league as "<idx>_<club>" (2026-11-18, user 23:5x: "01롯데 계정에서 볼 때는 97쌍방울의
		// 김경원이 FA팀으로 보이네? 이걸 쌍방울로 보이게끔 할 수 있을까?? 즉, 타팀이 보는 내 FA도 구단 유니폼을 입은걸로 보였으면 해"): only
		// those whose picture is there (a club's own lobby visit and its signings make them; any still missing goes onto the list now),
		// and no card two clubs both hold (which uniform would be a guess). The app draws them in that club's uniform (Ma9MFaArt); an
		// older app's int parse skips the entries.
		private static IEnumerable<string> Others(uint user)
		{
			LeagueState s = LeagueService.Store?.Load(user);
			if (s == null || s.Clubs.Count == 0)
			{
				return Enumerable.Empty<string>();
			}
			Dictionary<int, int> owner = new Dictionary<int, int>();
			HashSet<int> twice = new HashSet<int>();
			foreach (LeagueClub c in s.Clubs.Where(c => c.IsHuman && c.Owner != user).GroupBy(c => c.Owner).Select(g => g.First()))
			{
				int club = ClubOf(c.Owner);
				if (club < 0)
				{
					continue;
				}
				foreach (int i in RosterHandlers.Db.GetCards(c.Owner).Select(x => x.Idx).Distinct().Where(i => RosterHandlers.Catalog.Get(i)?.IsFA == true))
				{
					if (!File.Exists(FacePath(i, club)))
					{
						Enqueue(i, club);
						continue;
					}
					if (owner.TryGetValue(i, out int had) && had != club)
					{
						twice.Add(i);
					}
					owner[i] = club;
				}
			}
			return owner.Where(kv => !twice.Contains(kv.Key)).Select(kv => $"{kv.Key}_{kv.Value}").ToList();
		}

		// LobbyHandlers.EnterMain: the team's FA cards with their picture (to the app), the others onto the list.
		public static void OnLobby(GameSession session)
		{
			if (s_dataDir == null || session?.Team == null)
			{
				return;
			}
			try
			{
				uint user = session.Team.UserCode;
				int club = EmblemYears.BaseOf(session.Team.Emblem);
				List<int> fa = RosterHandlers.Db.GetCards(user).Select(c => c.Idx).Distinct().Where(i => RosterHandlers.Catalog.Get(i)?.IsFA == true).ToList();
				List<int> ready = fa.Where(i => File.Exists(FacePath(i, club))).ToList();
				foreach (int i in fa.Except(ready))
				{
					Enqueue(i, club);
				}
				if (session.ClientVersion?.Contains(ClientMark) == true)
				{
					Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
					popup.Type.n = ListType;
					popup.Messages.array = new[] { club.ToString() }.Concat(ready.Select(i => i.ToString())).Concat(Others(user))
						.Select(s => new PLongString { str = s }).ToArray();
					session.Send(popup);
				}
			}
			catch (Exception e)
			{
				ServerLog.Warn($"FA 유니폼 그림 목록(로비): {e.Message}");
			}
		}

		private static void Enqueue(int idx, int club)
		{
			lock (s_lock)
			{
				if (!s_queued.Add($"{idx}\t{club}"))
				{
					return;
				}
				File.AppendAllText(QueueFile, $"{idx}\t{club}\n");
				ServerLog.Info($"FA 유니폼 그림 목록: {idx} 구단 {club}");
				s_dirty = true;
				Launch();
			}
		}

		// Under s_lock.
		private static void Launch()
		{
			if (s_repo == null || !s_dirty || (s_run != null && !s_run.HasExited))
			{
				return;
			}
			if (!File.Exists(Python) || !File.Exists(Script))
			{
				ServerLog.Warn($"FA 유니폼 그림: {(File.Exists(Python) ? Script : Python)} 없음, 목록만 남김");
				s_dirty = false;
				return;
			}
			s_dirty = false;
			try
			{
				ProcessStartInfo psi = new ProcessStartInfo(Python)
				{
					UseShellExecute = false,
					CreateNoWindow = true,
					WorkingDirectory = Path.GetDirectoryName(Script),
					RedirectStandardOutput = true,
					RedirectStandardError = true
				};
				// The script draws into the live server\data (its own fixed paths), so only the live server starts it (MA9M_FA_ART).
				psi.ArgumentList.Add(Script);
				psi.ArgumentList.Add("--queue");
				psi.ArgumentList.Add(QueueFile);
				Process p = new Process { StartInfo = psi, EnableRaisingEvents = true };
				p.OutputDataReceived += (s, e) => Log(e.Data);
				p.ErrorDataReceived += (s, e) => Log(e.Data);
				p.Exited += (s, e) =>
				{
					int code = -1;
					try
					{
						code = p.ExitCode;
					}
					catch (InvalidOperationException)
					{
					}
					ServerLog.Info($"FA 유니폼 그림: 끝남(code {code})");
					lock (s_lock)
					{
						s_run = null;
						Launch();
					}
				};
				p.Start();
				try
				{
					p.PriorityClass = ProcessPriorityClass.BelowNormal;
				}
				catch (Exception)
				{
				}
				p.BeginOutputReadLine();
				p.BeginErrorReadLine();
				s_run = p;
				ServerLog.Info($"FA 유니폼 그림: 그리기 시작(pid {p.Id})");
			}
			catch (Exception e)
			{
				ServerLog.Warn($"FA 유니폼 그림: 시작 못 함: {e.Message}");
			}
		}

		private static readonly object s_logLock = new object();

		private static void Log(string line)
		{
			if (line == null)
			{
				return;
			}
			lock (s_logLock)
			{
				try
				{
					File.AppendAllText(LogFile, $"{DateTime.Now:MM-dd HH:mm:ss} {line}\n");
				}
				catch (IOException)
				{
				}
			}
		}
	}
}
