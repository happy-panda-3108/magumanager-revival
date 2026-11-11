using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MPackets;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// 팀 특성 (Scene_TeamProperty): the season-2 batter and pitcher trees the user designed
	// (2026-10-18; tools/traitsim/traits.json, memory ma9m-team-traits). The common tree is not
	// designed yet, so it has no rows and its tab is disabled in the client.
	// Rules: CP by league tier (11/14/17/20/23); a tier opens with enough points in the tier directly
	// below it (6, 4, 4, 3); a trait under an arrow needs the trait above it mastered. A team that goes down to a
	// league with fewer CP than its traits use loses the excess from its deepest traits (TrimToCp).
	public static class TeamPropertyHandlers
	{
		public sealed class Trait
		{
			public byte Index;

			public char Type;   // H batter, P pitcher

			public byte Level;

			public byte Max;

			public byte Essential;

			public string Name;

			public string Desc;
		}

		// Points needed in the tier directly below to open tiers 1..5.
		public static readonly byte[] Gate = { 0, 0, 6, 4, 4, 3 };

		// The client picks the season-2 pages (TPPagePrefabs_Season2) when this is not 0.
		private const byte Season = 1;

		public static readonly Trait[] Traits =
		{
			T(101, 'H', 1, 2, 0, "컨택", "팀 타자들의 컨택이 소폭 상승합니다."),
			T(102, 'H', 1, 2, 0, "주력", "팀 타자들의 주력이 소폭 상승합니다."),
			T(103, 'H', 1, 2, 0, "정신력", "팀 타자들의 정신력이 소폭 상승합니다."),
			T(104, 'H', 1, 2, 0, "파워", "팀 타자들의 파워가 소폭 상승합니다."),
			T(105, 'H', 2, 2, 0, "도루 시도·성공", "도루 시도가 늘고 도루 성공률이 소폭 상승합니다."),
			T(106, 'H', 2, 2, 102, "선구", "팀 타자들의 선구안이 소폭 상승합니다. (주력 마스터 필요)"),
			T(107, 'H', 2, 2, 0, "수비", "팀 타자들의 수비력이 소폭 상승합니다."),
			T(110, 'H', 3, 2, 0, "컨택·정신력", "팀 타자들의 컨택과 정신력이 상승합니다."),
			T(111, 'H', 3, 1, 0, "도루 성공", "도루 성공률이 상승합니다."),
			T(112, 'H', 3, 2, 104, "홈런 확률", "홈런이 나올 확률이 상승합니다. (파워 마스터 필요)"),
			T(113, 'H', 4, 1, 105, "컨택·주력", "팀 타자들의 컨택과 주력이 상승합니다. (도루 시도·성공 마스터 필요)"),
			T(114, 'H', 4, 2, 0, "삼진↓ 볼넷↑", "타자들의 삼진이 줄고 볼넷이 늘어납니다."),
			T(115, 'H', 4, 2, 0, "컨택·파워", "팀 타자들의 컨택과 파워가 상승합니다."),
			T(118, 'H', 5, 1, 0, "타자 전 능력치", "팀 타자들의 모든 능력치가 대폭 상승합니다."),
			T(120, 'H', 5, 1, 112, "장타·홈런", "장타와 홈런이 대폭 늘어납니다. (홈런 확률 마스터 필요)"),
			T(121, 'P', 1, 2, 0, "구위", "팀 투수들의 구위가 소폭 상승합니다."),
			T(122, 'P', 1, 2, 0, "구속", "팀 투수들의 구속이 소폭 상승합니다."),
			T(123, 'P', 1, 2, 0, "체력", "팀 투수들의 체력이 소폭 상승합니다."),
			T(124, 'P', 1, 2, 0, "정신력", "팀 투수들의 정신력이 소폭 상승합니다."),
			T(125, 'P', 2, 2, 0, "테크닉", "팀 투수들의 테크닉이 소폭 상승합니다."),
			T(126, 'P', 2, 2, 0, "컨트롤", "팀 투수들의 컨트롤이 소폭 상승합니다."),
			T(127, 'P', 2, 2, 0, "도루 억제", "상대의 도루 시도가 줄고 도루 저지율이 상승합니다."),
			T(130, 'P', 3, 1, 0, "컨트롤·테크닉", "팀 투수들의 컨트롤과 테크닉이 상승합니다."),
			T(131, 'P', 3, 2, 0, "구속·구위", "팀 투수들의 구속과 구위가 상승합니다."),
			T(132, 'P', 3, 2, 124, "선발 투구 한계", "선발투수가 더 많은 공을 던질 수 있습니다. (정신력 마스터 필요)"),
			T(133, 'P', 4, 1, 125, "피안타↓ 탈삼진↑", "피안타가 줄고 탈삼진이 늘어납니다. (테크닉 마스터 필요)"),
			T(134, 'P', 4, 2, 0, "피장타·피홈런↓", "장타와 홈런을 덜 맞습니다."),
			T(135, 'P', 4, 2, 0, "피안타↓ 구속↑", "피안타가 줄고 구속이 상승합니다."),
			T(138, 'P', 5, 1, 0, "투수 전 능력치", "팀 투수들의 모든 능력치가 대폭 상승합니다."),
			T(140, 'P', 5, 1, 132, "셋업·마무리 대폭", "셋업맨과 마무리 투수의 능력치가 대폭 상승합니다. (선발 투구 한계 마스터 필요)")
		};

		private static readonly Dictionary<byte, Trait> s_byIndex = Traits.ToDictionary(t => t.Index);

		private static string s_connectionString;

		private static Trait T(byte index, char type, byte level, byte max, byte essential, string name, string desc)
		{
			return new Trait { Index = index, Type = type, Level = level, Max = max, Essential = essential, Name = name, Desc = desc };
		}

		public static void Register(GameLoop loop, string dataDir)
		{
			s_connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			Execute("CREATE TABLE IF NOT EXISTS team_property (user_code INTEGER PRIMARY KEY, buff BLOB NOT NULL)", null);
			Execute("CREATE TABLE IF NOT EXISTS team_property_notice (user_code INTEGER PRIMARY KEY, text TEXT NOT NULL)", null);
			// The order the points were put in, one trait index per point, oldest first (Order, 2026-11-08).
			Execute("CREATE TABLE IF NOT EXISTS team_property_order (user_code INTEGER PRIMARY KEY, seq BLOB NOT NULL)", null);
			WriteTable(Path.Combine(dataDir, "tables"));
			loop.Register<Ma9MPacket.PKS_CS_ENTER_TEAM_PROPERTY>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_APPLY_TEAM_PROPERTY>(Apply);
		}

		// The team's traits as (index, points); empty when none are set.
		public static Dictionary<byte, byte> Points(uint userCode)
		{
			byte[] buff = Load(userCode);
			Dictionary<byte, byte> points = new Dictionary<byte, byte>();
			for (int i = 0; i + 1 < buff.Length; i += 2)
			{
				if (buff[i + 1] > 0)
				{
					points[buff[i]] = buff[i + 1];
				}
			}
			return points;
		}

		// 강등 (user, 2026-10-28: "가장 깊은 특성부터 자동으로 빼기"; 2026-11-08: "마지막으로 추가한 특성이 빠지도록 … 이전 저장된 것에서
		// 마지막으로 추가로 등록된 특성들이 빠지게끔"): when the team's traits use more than cp, points come off in the reverse of
		// the order they were put in (Order), skipping a point whose removal would leave a tier unopened or a prerequisite
		// unmastered, so what is left is still a valid build. A team with no order saved yet (traits set before 11-08) gets the
		// old rule: the deepest first (tier 5 down; within a tier the tree with more points, then the higher index). The lobby
		// says so once (TakeTrimNotice, with the 시즌 완주 보너스 popup). Returns the points taken off each trait (empty when none).
		public static Dictionary<byte, int> TrimToCp(uint userCode, int cp, string why)
		{
			Dictionary<byte, int> removed = new Dictionary<byte, int>();
			if (s_connectionString == null) return removed;
			Dictionary<byte, byte> points = Points(userCode);
			int used = points.Values.Sum(v => v);
			if (used <= cp) return removed;
			bool remembered = StoredOrderMatches(userCode, points);
			List<byte> order = Order(userCode, points);
			int Tree(char type) => points.Where(p => s_byIndex.TryGetValue(p.Key, out Trait t) && t.Type == type).Sum(p => p.Value);
			while (used > cp && points.Any(p => p.Value > 0))
			{
				int at = -1;
				for (int i = order.Count - 1; i >= 0 && at < 0; i--)
				{
					byte t = order[i];
					points[t]--;
					if (Validate(points, int.MaxValue) == null) at = i;
					points[t]++;
				}
				byte pick = at >= 0 ? order[at] : points.Where(p => p.Value > 0)
					.OrderByDescending(p => s_byIndex.TryGetValue(p.Key, out Trait t) ? t.Level : 99)
					.ThenByDescending(p => s_byIndex.TryGetValue(p.Key, out Trait t) ? Tree(t.Type) : 0)
					.ThenByDescending(p => p.Key).First().Key;
				if (at < 0) at = order.LastIndexOf(pick);
				if (at >= 0) order.RemoveAt(at);
				points[pick]--;
				used--;
				removed[pick] = removed.GetValueOrDefault(pick) + 1;
			}
			Save(userCode, points);
			SaveOrder(userCode, order);
			string Name(byte i)
			{
				if (!s_byIndex.TryGetValue(i, out Trait t)) return i.ToString();
				string side = t.Type == 'H' ? "타자" : "투수";
				return t.Name.StartsWith(side) ? t.Name : $"{side} {t.Name}";
			}
			// Short lines for the popup, which does not wrap (PopupText; 11-09: one line was about 510 px of 443).
			string text = $"{why} 팀 특성이 {cp}CP가 되었습니다.\n{(remembered ? "마지막으로 찍은 특성부터" : "가장 깊은 특성부터")} {removed.Values.Sum()}포인트를 뺐습니다.\n"
				+ string.Join("\n", PopupText.Wrap(removed.Select(kv => $"{Name(kv.Key)} -{kv.Value}"), ", "));
			Execute("INSERT INTO team_property_notice (user_code, text) VALUES ($code, $text) ON CONFLICT(user_code) DO UPDATE SET text = text || char(10) || $text",
				c => { c.Parameters.AddWithValue("$code", userCode); c.Parameters.AddWithValue("$text", text); });
			ServerLog.Info($"team traits of {userCode} trimmed to {cp} CP: {string.Join(", ", removed.Select(kv => $"{kv.Key} -{kv.Value}"))}"
				+ (Validate(points, cp) is string problem ? $" (still invalid: {problem})" : string.Empty));
			return removed;
		}

		// The order the team's points were put in (one trait index per point, oldest first), when the stored one matches the
		// points; otherwise one made from the points with the deepest last (so the end comes off as the old rule took it).
		public static List<byte> Order(uint userCode, Dictionary<byte, byte> points)
		{
			List<byte> stored = LoadOrder(userCode);
			if (Matches(stored, points)) return stored;
			int Tree(char type) => points.Where(p => s_byIndex.TryGetValue(p.Key, out Trait t) && t.Type == type).Sum(p => p.Value);
			return points.Where(p => p.Value > 0)
				.OrderBy(p => s_byIndex.TryGetValue(p.Key, out Trait t) ? t.Level : 99)
				.ThenBy(p => s_byIndex.TryGetValue(p.Key, out Trait t) ? Tree(t.Type) : 0)
				.ThenBy(p => p.Key)
				.SelectMany(p => Enumerable.Repeat(p.Key, p.Value)).ToList();
		}

		private static bool StoredOrderMatches(uint userCode, Dictionary<byte, byte> points) => Matches(LoadOrder(userCode), points);

		private static bool Matches(List<byte> seq, Dictionary<byte, byte> points) =>
			seq.Count == points.Values.Sum(v => v) && points.Where(p => p.Value > 0).All(p => seq.Count(x => x == p.Key) == p.Value);

		// A save's order: points taken off leave from that trait's latest; points added go on the end, the shallower tiers first.
		public static List<byte> NextOrder(List<byte> order, Dictionary<byte, byte> before, Dictionary<byte, byte> after)
		{
			List<byte> seq = new List<byte>(order);
			foreach (KeyValuePair<byte, byte> b in before)
			{
				int fewer = b.Value - (after.TryGetValue(b.Key, out byte a) ? a : 0);
				for (int n = 0; n < fewer; n++)
				{
					int at = seq.LastIndexOf(b.Key);
					if (at >= 0) seq.RemoveAt(at);
				}
			}
			foreach (KeyValuePair<byte, byte> a in after.Where(p => p.Value > 0)
				.OrderBy(p => s_byIndex.TryGetValue(p.Key, out Trait t) ? t.Level : 99).ThenBy(p => p.Key))
			{
				int more = a.Value - (before.TryGetValue(a.Key, out byte b) ? b : 0);
				for (int n = 0; n < more; n++) seq.Add(a.Key);
			}
			return seq;
		}

		private static List<byte> LoadOrder(uint userCode)
		{
			if (s_connectionString == null) return new List<byte>();
			using SqliteConnection connection = new SqliteConnection(s_connectionString);
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT seq FROM team_property_order WHERE user_code = $code";
			command.Parameters.AddWithValue("$code", (long)userCode);
			return (command.ExecuteScalar() as byte[])?.ToList() ?? new List<byte>();
		}

		private static void SaveOrder(uint userCode, List<byte> seq) =>
			Execute("INSERT INTO team_property_order (user_code, seq) VALUES ($code, $seq) ON CONFLICT(user_code) DO UPDATE SET seq = $seq",
				c => { c.Parameters.AddWithValue("$code", (long)userCode); c.Parameters.AddWithValue("$seq", seq.ToArray()); });

		// Dev (LeagueSelfTest, a data copy): three saves, then a trim; the points added last come off first, and a point whose
		// removal would close a tier is skipped for the next older one.
		public static int OrderSelfTest(uint user)
		{
			int problems = 0;
			Dictionary<byte, byte> P(string text) => text.Split(',').Select(kv => kv.Split(':')).ToDictionary(kv => byte.Parse(kv[0]), kv => byte.Parse(kv[1]));
			void Put(string text)
			{
				Dictionary<byte, byte> next = P(text);
				SaveOrder(user, NextOrder(Order(user, Points(user)), Points(user), next));
				Save(user, next);
			}
			string Show() => string.Join(",", Points(user).Where(p => p.Value > 0).OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[traitorder] {(ok ? "ok  " : "FAIL")} {what}");
			}
			Execute("DELETE FROM team_property_order WHERE user_code = $code", c => c.Parameters.AddWithValue("$code", (long)user));
			Save(user, new Dictionary<byte, byte>());
			// 1. Pitching tier 1, then tier 2, then batting tier 1 last: going down 2 takes the batting points (the old rule took
			// the tier-2 pitching ones).
			Put("121:2,122:2,124:2");
			Put("121:2,122:2,124:2,125:2,126:2");
			Put("101:2,121:2,122:2,124:2,125:2,126:2");
			TrimToCp(user, 10, "시험");
			string notice = TakeTrimNotice(user);
			Check(Show() == "121:2,122:2,124:2,125:2,126:2" && notice != null && notice.Contains("마지막으로 찍은"), $"last added (101 ×2) off first: {Show()}; {notice?.Replace('\n', ' ')}");
			// 2. A 124 point traded for 123 (tier 1 still 6): 123 is the last, but taking it would close tier 2, so 126 goes.
			Put("121:2,122:2,123:1,124:1,125:2,126:2");
			TrimToCp(user, 9, "시험");
			TakeTrimNotice(user);
			Check(Show() == "121:2,122:2,123:1,124:1,125:2,126:1", $"123 kept (tier 2 needs 6 below), 126 off: {Show()}");
			// 3. No order saved (traits from before): the deepest first, as before.
			Execute("DELETE FROM team_property_order WHERE user_code = $code", c => c.Parameters.AddWithValue("$code", (long)user));
			Save(user, P("101:2,121:2,122:2,124:2,125:2,126:2"));
			TrimToCp(user, 10, "트리플A 리그로 내려가");
			notice = TakeTrimNotice(user);
			Check(Show() == "101:2,121:2,122:2,124:2,125:2" && notice != null && notice.Contains("가장 깊은"), $"no order: deepest first (126 ×2): {Show()}");
			// The notice fits the popup (2026-11-09): no line wider than the box.
			string[] noticeLines = notice?.Split('\n') ?? Array.Empty<string>();
			Check(noticeLines.Length is > 0 and <= 5 && noticeLines.All(l => PopupText.Px(l) <= PopupText.Limit),
				$"notice {noticeLines.Length} lines, widest {(noticeLines.Length > 0 ? noticeLines.Max(PopupText.Px) : 0):F0} px: {string.Join(" / ", noticeLines)}");
			Execute("DELETE FROM team_property_order WHERE user_code = $code", c => c.Parameters.AddWithValue("$code", (long)user));
			Save(user, new Dictionary<byte, byte>());
			ServerLog.Info($"[traitorder] {problems} problem(s)");
			return problems;
		}

		// The pending 팀 특성 조정 notice, once (null when there is none).
		public static string TakeTrimNotice(uint userCode)
		{
			if (s_connectionString == null) return null;
			using SqliteConnection connection = new SqliteConnection(s_connectionString);
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "DELETE FROM team_property_notice WHERE user_code = $code RETURNING text";
			command.Parameters.AddWithValue("$code", userCode);
			return command.ExecuteScalar() as string;
		}

		// 특성 추천 배분 (AdviceHandlers, 2026-11-11): the points as an 적용 from the screen would save them; the refusal or null.
		public static string ApplyPoints(uint userCode, Dictionary<byte, byte> points, string why)
		{
			string problem = Validate(points, AvailableCp(userCode));
			if (problem != null)
			{
				ServerLog.Warn($"team traits ({why}) of {userCode} rejected: {problem}");
				return "특성 배분을 저장하지 못했습니다. 다시 계산해 주세요.";
			}
			SaveOrder(userCode, NextOrder(Order(userCode, Points(userCode)), Points(userCode), points));
			Save(userCode, points);
			ServerLog.Info($"team traits ({why}) of {userCode}: {string.Join(" ", points.Where(p => p.Value > 0).Select(p => $"{p.Key}:{p.Value}"))}");
			return null;
		}

		private static void Save(uint userCode, Dictionary<byte, byte> points)
		{
			byte[] clean = points.Where(p => p.Value > 0).OrderBy(p => p.Key).SelectMany(p => new[] { p.Key, p.Value }).ToArray();
			Execute("INSERT INTO team_property (user_code, buff) VALUES ($code, $buff) ON CONFLICT(user_code) DO UPDATE SET buff = $buff",
				c => { c.Parameters.AddWithValue("$code", userCode); c.Parameters.AddWithValue("$buff", clean); });
		}

		// CP for the team's league tier (비기너 when it has no league yet).
		public static int AvailableCp(uint userCode)
		{
			LeagueState s = LeagueService.Store?.Load(userCode);
			int tier = s == null ? 0 : Math.Clamp(s.Tier, 0, LeagueRules.TraitPoints.Length - 1);
			return LeagueRules.TraitPoints[tier];
		}

		// 빨간 점 (user, 2026-11-09 D-2: "특성을 사용하지 않았거나 포인트가 남았으면 빨간 점"): CP left over that some trait can still take.
		public static bool CanSpend(uint userCode)
		{
			Dictionary<byte, byte> points = Points(userCode);
			int cp = AvailableCp(userCode);
			if (points.Values.Sum(v => v) >= cp) return false;
			foreach (Trait t in Traits)
			{
				Dictionary<byte, byte> trial = new Dictionary<byte, byte>(points);
				trial[t.Index] = (byte)((trial.TryGetValue(t.Index, out byte v) ? v : 0) + 1);
				if (Validate(trial, cp) == null) return true;
			}
			return false;
		}

		// Checks a whole tree assignment against the rules; null when it is valid.
		public static string Validate(Dictionary<byte, byte> points, int cp)
		{
			int used = 0;
			foreach (KeyValuePair<byte, byte> p in points)
			{
				if (!s_byIndex.TryGetValue(p.Key, out Trait t))
				{
					return $"unknown trait {p.Key}";
				}
				if (p.Value > t.Max)
				{
					return $"trait {p.Key} over its max";
				}
				if (p.Value == 0)
				{
					continue;
				}
				used += p.Value;
				int below = Traits.Where(o => o.Type == t.Type && o.Level == t.Level - 1).Sum(o => points.TryGetValue(o.Index, out byte v) ? v : 0);
				if (below < Gate[t.Level])
				{
					return $"trait {p.Key}: tier {t.Level} not open ({below}/{Gate[t.Level]})";
				}
				if (t.Essential != 0 && (!points.TryGetValue(t.Essential, out byte e) || e < s_byIndex[t.Essential].Max))
				{
					return $"trait {p.Key} needs {t.Essential} mastered";
				}
			}
			return used > cp ? $"{used} points over {cp}" : null;
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_TEAM_PROPERTY packet)
		{
			uint user = session.Team.UserCode;
			Ma9MPacket.PKS_SC_ENTER_TEAM_PROPERTY reply = new Ma9MPacket.PKS_SC_ENTER_TEAM_PROPERTY();
			reply.cp.n = (byte)AvailableCp(user);
			reply.buff.bytes = Load(user);
			reply.season.n = Season;
			session.Send(reply);
		}

		private static void Apply(GameSession session, Ma9MPacket.PKS_CS_APPLY_TEAM_PROPERTY packet)
		{
			uint user = session.Team.UserCode;
			byte[] buff = packet.buff.bytes ?? Array.Empty<byte>();
			Dictionary<byte, byte> points = new Dictionary<byte, byte>();
			for (int i = 0; i + 1 < buff.Length; i += 2)
			{
				points[buff[i]] = buff[i + 1];
			}
			string problem = Validate(points, AvailableCp(user));
			Ma9MPacket.PKS_SC_APPLY_TEAM_PROPERTY reply = new Ma9MPacket.PKS_SC_APPLY_TEAM_PROPERTY();
			if (problem == null)
			{
				SaveOrder(user, NextOrder(Order(user, Points(user)), Points(user), points));
				Save(user, points);
				ServerLog.Info($"[{session.Id}] team traits: {string.Join(" ", points.Where(p => p.Value > 0).Select(p => $"{p.Key}:{p.Value}"))}");
				reply.result.n = 0;
			}
			else
			{
				ServerLog.Warn($"[{session.Id}] team traits rejected: {problem}");
				reply.result.n = 1;
			}
			session.Send(reply);
		}

		// ma9_character_tree.txt (TableReader.TR_TeamProperty): index, type, level, essential,
		// necessary_point (points needed in the essential trait), name, (unused), desc, max_point,
		// required_point (points needed in the tier below; the client is patched to count that tier only).
		private static void WriteTable(string tablesDir)
		{
			StringBuilder sb = new StringBuilder("index\ttype\tlevel\tessential\tnecessary_point\tname\ticon\tdesc\tmax_point\trequired_point\n");
			foreach (Trait t in Traits)
			{
				byte necessary = t.Essential != 0 ? s_byIndex[t.Essential].Max : (byte)0;
				sb.Append($"{t.Index}\t{t.Type}\t{t.Level}\t{t.Essential}\t{necessary}\t{t.Name}\t-\t{t.Desc}\t{t.Max}\t{Gate[t.Level]}\n");
			}
			string path = Path.Combine(tablesDir, "ma9_character_tree.txt");
			string text = sb.ToString();
			if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
			{
				File.WriteAllText(path, text, new UTF8Encoding(false));
			}
		}

		private static byte[] Load(uint userCode)
		{
			using SqliteConnection connection = new SqliteConnection(s_connectionString);
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = "SELECT buff FROM team_property WHERE user_code = $code";
			command.Parameters.AddWithValue("$code", userCode);
			return command.ExecuteScalar() as byte[] ?? Array.Empty<byte>();
		}

		private static void Execute(string sql, Action<SqliteCommand> bind)
		{
			using SqliteConnection connection = new SqliteConnection(s_connectionString);
			connection.Open();
			using SqliteCommand command = connection.CreateCommand();
			command.CommandText = sql;
			bind?.Invoke(command);
			command.ExecuteNonQuery();
		}
	}
}
