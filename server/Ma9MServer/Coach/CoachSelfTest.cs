using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MPackets;
using Microsoft.Data.Sqlite;

namespace Ma9MServer
{
	// Dev (--coachtest, MA9M_COACH=1, a copy of the data only): the 코칭스태프 rules on the autotest team.
	public static partial class CoachHandlers
	{
		// A Random whose Next(n) is always 0 (the coach always comes).
		private sealed class Always : Random
		{
			public override int Next(int maxValue) => 0;
		}

		public static int SelfTest(string dbPath)
		{
			if (Path.GetFullPath(dbPath).Equals(Path.GetFullPath(@"C:\Ma9MProject\server\data\ma9m.db"), StringComparison.OrdinalIgnoreCase))
			{
				ServerLog.Warn("[coachtest] refuses the live database; run it on a copy of the data directory");
				return 2;
			}
			if (!CoachCatalog.Enabled || s_cs == null)
			{
				ServerLog.Warn("[coachtest] needs MA9M_COACH=1");
				return 2;
			}
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[coachtest] {(ok ? "ok  " : "FAIL")} {what}");
			}

			// Tables as the client reads them.
			string tables = Path.Combine(Path.GetDirectoryName(dbPath), "tables");
			string[][] index = File.ReadAllLines(Path.Combine(tables, "ma9_Coach_Index.txt"), Encoding.UTF8).Skip(1).Select(l => l.Split('\t')).ToArray();
			List<int> all = CoachCatalog.All().ToList();
			Check(index.Length == all.Count && index.All(c => c.Length == 7) && index.Select(c => c[0]).Distinct().Count() == index.Length
				&& index.All(c => CoachCatalog.IsCoach(int.Parse(c[0])) && c[1] == "0") && all.Count == 19 * 36 && all.Select(i => i - CoachCatalog.Stat(i)).Distinct().Count() == all.Count,
				$"ma9_Coach_Index.txt: {index.Length} cards (19 clubs × 36), a picture each, 7 columns");
			string[][] strings = File.ReadAllLines(Path.Combine(tables, "ma9_Coach_String.txt"), Encoding.UTF8).Skip(1).Select(l => l.Split('\t')).ToArray();
			// Per grade: one 능력치 line for each role and the 치어리더's description (the others' are per card, Description).
			Check(strings.All(c => c.Length == 4) && strings.Select(c => c[0]).Distinct().Count() == strings.Length && strings.Length == 24 + 4 + 16,
				$"ma9_Coach_String.txt: {strings.Length} rows, unique codes");
			Check(index.All(c => !c[6].Contains("같은 구단") && !c[6].Contains("육성")) && strings.All(c => !c[3].Contains("같은 구단") && !c[3].Contains("육성")),
				"no '같은 구단' or '육성' in the coach texts (user 11-05)");
			string[][] rooms = File.ReadAllLines(Path.Combine(tables, "ConsultingRoom.txt"), Encoding.UTF8).Skip(1).Select(l => l.Split('\t')).ToArray();
			Check(rooms.Length == 8 * CoachCatalog.ConsultVariants && rooms.All(c => c.Length == 16 && c[10] == string.Empty && c[4] != string.Empty && c[13] == "CONDITION")
				&& rooms.All(c => c.Skip(2).All(v => !v.Contains('"'))), $"ConsultingRoom.txt: {rooms.Length} dialogues, 3 choices each");
			// Error text through the packet bytes and the app's JSON reader (면담실 without a 수석코치 threw, 11-10).
			string ErrorRoundTrip(string text)
			{
				try
				{
					ErrorInfo e = new ErrorInfo();
					e.SetErrorMessage(text);
					byte[] buff = new byte[e.GetSize()];
					int at = 0;
					e.ToBytes(buff, ref at);
					ErrorInfo back = new ErrorInfo();
					int read = 0;
					back.ToType(buff, ref read);
					return read == buff.Length && back.error_code.n == 1 ? LitJson.JsonMapper.ToObject<ErrorInfoObject>(back.error_msg.str).RawMessage : null;
				}
				catch (Exception ex)
				{
					ServerLog.Warn($"[coachtest] error text: {ex.Message}");
					return null;
				}
			}
			string noHead = "수석코치를 등록하지 않으면 면담실을 이용하실 수 없습니다.";
			string longText = string.Concat(Enumerable.Repeat("가나다 \"라\" \\마\n", 30));
			string cut = ErrorRoundTrip(longText);
			Check(ErrorRoundTrip(noHead) == noHead && ErrorRoundTrip("보유한 SP가 부족합니다.") == "보유한 SP가 부족합니다." && cut != null && cut.EndsWith("…")
				&& longText.StartsWith(cut.TrimEnd('…')) && ErrorInfo.Ma9MJson(null, longText).Length <= ErrorInfo.Ma9MMaxChars,
				$"error text in the packet: the 면담실 refusal whole, {longText.Length} letters cut to {cut?.Length}");
			Check(CoachCatalog.Name(CoachCatalog.Card(9, CoachCatalog.PC, 1)) == "SK·SSG 투수코치" && CoachCatalog.Name(CoachCatalog.Card(8, CoachCatalog.HC, 3, 1)) == "삼성 타격코치"
				&& CoachCatalog.Description(CoachCatalog.Card(8, CoachCatalog.HC, 3, 1)) == "삼성 타자의 트레이닝에 등장\\n50 SP를 사용하면 결과 +1\\n삼성 타자 컨택 +1"
				&& CoachCatalog.Description(CoachCatalog.Card(1, CoachCatalog.PC, 4, 2)) == "KIA·해태 투수의 트레이닝에 등장\\n50 SP를 사용하면 결과 +1\\nKIA·해태 투수 구속 +2"
				&& CoachCatalog.Description(CoachCatalog.Card(16, CoachCatalog.HD, 2)) == "1군의 LG 선수 중 컨디션 나쁜 선수가 면담실로 찾아옵니다.\\n150 SP 면담: 컨디션 1~3단계 회복(12시간)"
				&& CoachCatalog.Description(CoachCatalog.Card(16, CoachCatalog.CL, 2)) == null
				&& CoachCatalog.Card(8, CoachCatalog.HC, 3, 6) == 9_082_356 && CoachCatalog.Card(8, CoachCatalog.PC, 4, 2) == 9_081_412 && CoachCatalog.Card(8, CoachCatalog.HD, 4) == 9_080_410
				&& CoachCatalog.Card(8, CoachCatalog.HC, 2) == 9_082_210 && !CoachCatalog.IsCoach(9_082_355) && !CoachCatalog.IsCoach(9_080_420) && !CoachCatalog.IsCoach(9_082_111),
				"names and descriptions");
			// Lineages (user 11-05): MBC with LG, 해태 with KIA, 삼미·청보·태평양 with 현대; SK not with 쌍방울.
			int mbcHd = CoachCatalog.Card(16, CoachCatalog.HD, 3), lgPc = CoachCatalog.Card(6, CoachCatalog.PC, 4, 2), lgHc = CoachCatalog.Card(6, CoachCatalog.HC, 3, 1);
			int mbcSc = CoachCatalog.Card(16, CoachCatalog.SC, 3), lgCl = CoachCatalog.Card(6, CoachCatalog.CL, 4);
			Check(TeamColors.Lineage(16) == 6 && TeamColors.Lineage(17) == 4 && TeamColors.Lineage(1) == 5 && TeamColors.Lineage(9) == 9 && TeamColors.Lineage(11) == 11
				&& CoachCatalog.Serves(mbcHd, 6) && CoachCatalog.Serves(lgPc, 16) && !CoachCatalog.Serves(mbcHd, 7) && CoachCatalog.Serves(CoachCatalog.Card(1, CoachCatalog.HC, 1), 5)
				&& !CoachCatalog.Serves(CoachCatalog.Card(11, CoachCatalog.HC, 1), 9)
				&& CoachHandlers.SetBonusOf(new[] { mbcHd, lgPc, lgHc, mbcSc, lgCl }) == (6, 2)
				&& CoachHandlers.SetBonusOf(new[] { mbcHd, lgPc, lgHc, mbcSc, CoachCatalog.Card(7, CoachCatalog.CL, 4) }) == (-1, 0),
				"lineages: MBC·LG coaches serve both and mix into one LG set (lowest 레어 = +2), 쌍방울 ≠ SK");

			// Packs: grade odds and the bonus stats.
			Random rng = new Random(4);
			int[] grades = new int[5];
			int[] eliteRoles = new int[CoachCatalog.Roles];
			Dictionary<int, int> stats = new Dictionary<int, int>();
			Dictionary<int, int> lines = new Dictionary<int, int>();
			for (int n = 0; n < 100_000; n++)
			{
				int idx = CoachCatalog.Roll(rng);
				lines[CoachCatalog.LineOf(idx)] = lines.GetValueOrDefault(CoachCatalog.LineOf(idx)) + 1;
				grades[CoachCatalog.Grade(idx)]++;
				if (CoachCatalog.Grade(idx) == CoachCatalog.Elite) eliteRoles[CoachCatalog.Role(idx)]++;
				if (CoachCatalog.Role(idx) == CoachCatalog.HC && CoachCatalog.Grade(idx) >= CoachCatalog.Rare) stats[CoachCatalog.Stat(idx)] = stats.GetValueOrDefault(CoachCatalog.Stat(idx)) + 1;
			}
			Check(Enumerable.Range(1, 4).All(g => Math.Abs(grades[g] / 1000.0 - CoachCatalog.PackPercent(g)) < Math.Max(0.15, CoachCatalog.PackPercent(g) * 0.03)) && stats.Count == 5 && !stats.ContainsKey(5)
				&& stats.Values.All(v => Math.Abs(v / (double)stats.Values.Sum() - 0.2) < 3.5 * Math.Sqrt(0.16 / stats.Values.Sum()))  // 1 in 5 each, within 3.5 sd (fewer 레어·엘리트 since 11-10)
				&& eliteRoles.All(v => Math.Abs(v / (double)eliteRoles.Sum() - 0.2) < 3.5 * Math.Sqrt(0.16 / eliteRoles.Sum())),
				$"pack: {string.Join("/", grades.Skip(1).Select(g => (g / 1000.0).ToString("F1")))}%, 엘리트 roles {string.Join("/", eliteRoles.Select(v => (v * 100.0 / eliteRoles.Sum()).ToString("F0")))}%, "
				+ $"타격코치 stats {string.Join(" ", stats.OrderBy(k => k.Key).Select(k => $"{CoachCatalog.StatName(CoachCatalog.HC, k.Key)} {k.Value * 100.0 / stats.Values.Sum():F0}%"))}");
			Check(lines.Count == 12 && lines.Values.All(v => Math.Abs(v / 100_000.0 - 1 / 12.0) < 0.006),
				$"pack lineages alike: {string.Join(" ", lines.OrderBy(k => k.Key).Select(k => $"{TeamColors.LineageName(k.Key)} {k.Value / 1000.0:F1}%"))}");
			// 자팀 계보 (user, 2026-11-13): OwnLinePercent of the packs for the team's line (LG here), the other 11 alike; none → all alike.
			Dictionary<int, int> ownLines = new Dictionary<int, int>();
			int lgLine = CoachCatalog.Line(6), ownGrades = 0;
			for (int n = 0; n < 100_000; n++)
			{
				int idx = CoachCatalog.RollPack(rng, lgLine);
				ownLines[CoachCatalog.LineOf(idx)] = ownLines.GetValueOrDefault(CoachCatalog.LineOf(idx)) + 1;
				if (CoachCatalog.Grade(idx) == CoachCatalog.Elite) ownGrades++;
			}
			double ownShare = ownLines.GetValueOrDefault(lgLine) / 100_000.0, otherEach = (1 - CoachCatalog.OwnLinePercent / 100.0) / 11;
			Check(ownLines.Count == 12 && Math.Abs(ownShare - CoachCatalog.OwnLinePercent / 100.0) < 0.006
				&& ownLines.Where(k => k.Key != lgLine).All(k => Math.Abs(k.Value / 100_000.0 - otherEach) < 0.005) && Math.Abs(ownGrades / 1000.0 - CoachCatalog.PackPercent(CoachCatalog.Elite)) < 0.15
				&& CoachCatalog.LineOf(CoachCatalog.RollPack(rng, -1)) >= 0,
				$"pack for an LG 1군: LG {ownShare * 100:F1}%, others {string.Join(" ", ownLines.Where(k => k.Key != lgLine).OrderBy(k => k.Key).Select(k => $"{TeamColors.LineageName(k.Key)} {k.Value / 1000.0:F1}%"))}, 엘리트 {ownGrades / 1000.0:F2}%");
			// MBC·청보·삼미 (no player cards) never come out: not from a pack, nor when a 조합 names one of them.
			Dictionary<int, int> hyundai = new Dictionary<int, int>();
			int oldOut = 0;
			for (int n = 0; n < 100_000; n++)
			{
				int idx = CoachCatalog.Roll(rng), k = CoachCatalog.Club(idx);
				if (CoachCatalog.OldClubs.Contains(k)) oldOut++;
				if (CoachCatalog.LineOf(idx) == 4) hyundai[k] = hyundai.GetValueOrDefault(k) + 1;
				if (CoachCatalog.OldClubs.Contains(CoachCatalog.Club(CoachCatalog.Roll(rng, CoachCatalog.Rare, 16 + n % 3, CoachCatalog.HC)))) oldOut++;
			}
			Check(oldOut == 0 && hyundai.Keys.OrderBy(k => k).SequenceEqual(new[] { 4, 15 }) && CoachCatalog.LineClubs(16).SequenceEqual(new[] { 6 })
				&& CoachCatalog.PackClubs.Length == 16 && CoachCatalog.IsCoach(CoachCatalog.Card(17, CoachCatalog.CL, 1)),
				$"old clubs out of packs: {oldOut} of 200,000, 현대 line {string.Join(" ", hyundai.OrderBy(k => k.Key).Select(k => $"{TeamColors.FranchiseName(k.Key)} {k.Value}"))}, MBC → LG only");

			uint user;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
				cmd.CommandText = "DELETE FROM coach_card WHERE user_code = $u; DELETE FROM coach_state WHERE user_code = $u; DELETE FROM coach_consult WHERE user_code = $u; DELETE FROM coach_cheer WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.ExecuteNonQuery();
			}
			s_staff.Remove(user);
			s_effects.Remove(user);
			// The one-time 청보·삼미·MBC change (OldClubsOnce) on this copy: two old cards, one on the staff, become 현대·태평양 / LG cards.
			{
				int oldPc = CoachCatalog.Card(17, CoachCatalog.PC, CoachCatalog.Elite, 5), oldCl = CoachCatalog.Card(16, CoachCatalog.CL, CoachCatalog.Special);
				AddCoaches(user, new[] { oldPc, oldCl });
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "INSERT OR REPLACE INTO coach_state (user_code, staff, sp) VALUES ($u, $s, 0); DELETE FROM coach_fix WHERE name = 'old_clubs_1105'";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.Parameters.AddWithValue("$s", $"0,{oldPc},0,0,0");
					cmd.ExecuteNonQuery();
				}
				OldClubsOnce();
				s_staff.Remove(user);
				List<int> held = Coaches(user);
				int[] staffNow = Staff(user);
				int pcNow = held.FirstOrDefault(i => CoachCatalog.Role(i) == CoachCatalog.PC), clNow = held.FirstOrDefault(i => CoachCatalog.Role(i) == CoachCatalog.CL);
				Check(held.Count == 2 && new[] { 4, 15 }.Contains(CoachCatalog.Club(pcNow)) && CoachCatalog.Grade(pcNow) == CoachCatalog.Elite && CoachCatalog.Stat(pcNow) == 5
					&& CoachCatalog.Club(clNow) == 6 && CoachCatalog.Grade(clNow) == CoachCatalog.Special && staffNow[1] == pcNow,
					$"old cards changed once: {Label(oldPc)} → {Label(pcNow)} (staff kept), {Label(oldCl)} → {Label(clNow)}");
				using (SqliteConnection c = Open())
				using (SqliteCommand cmd = c.CreateCommand())
				{
					cmd.CommandText = "DELETE FROM coach_card WHERE user_code = $u; DELETE FROM coach_state WHERE user_code = $u; DELETE FROM coach_notice WHERE user_code = $u";
					cmd.Parameters.AddWithValue("$u", (long)user);
					cmd.ExecuteNonQuery();
				}
				s_staff.Remove(user);
			}
			// The test team's contracts may have run out (it is played only by tests): a month more on this copy.
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "UPDATE team_card SET contract_until = $d WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.Parameters.AddWithValue("$d", DateTime.Now.Date.AddDays(30).ToString("yyyy-MM-dd"));
				cmd.ExecuteNonQuery();
			}
			Lineup lineup = s_db.GetLineup(user);
			Check(user != 0 && lineup != null, $"test team {user}");
			if (user == 0 || lineup == null) return 1;

			// The club: the one with a 나쁨 / 매우 나쁨 regular now, else the one with most regulars.
			DateTime now = DateTime.Now;
			int[] regulars = lineup.Batters.Take(9).Concat(lineup.Pitchers).Where(i => i != 0).ToArray();
			int[] bench = lineup.Batters.Skip(9).Where(i => i != 0).ToArray();
			var clubs = regulars.Select(i => s_catalog.Get(i)).Where(i => i != null).GroupBy(i => i.TeamCode).ToList();
			int club = clubs.Where(g => g.Any(i => Condition.For(user, i.Idx, now) >= 3)).OrderByDescending(g => g.Count()).Select(g => g.Key)
				.DefaultIfEmpty(clubs.OrderByDescending(g => g.Count()).First().Key).First();
			int other = CoachCatalog.Clubs.First(k => CoachCatalog.Line(k) != CoachCatalog.Line(club) && !CoachCatalog.OldClubs.Contains(k));  // another lineage
			CardInfo batter = clubs.First(g => g.Key == club).FirstOrDefault(i => i.IsBatter) ?? s_catalog.All.First(i => i.IsBatter && i.TeamCode == club);
			CardInfo pitcher = clubs.First(g => g.Key == club).FirstOrDefault(i => !i.IsBatter);
			CardInfo stranger = regulars.Select(i => s_catalog.Get(i)).First(i => i.TeamCode != club);
			ServerLog.Info($"[coachtest] club {TeamColors.FranchiseName(club)}: {clubs.First(g => g.Key == club).Count()} regulars, batter {batter.Name}, pitcher {pitcher?.Name}, stranger {stranger.Name}");

			// Staff: slots by role, owned cards only; the set bonus by the lowest grade, the hitter's own stat.
			int[] elite = Enumerable.Range(0, CoachCatalog.Roles).Select(r => CoachCatalog.Card(club, r, CoachCatalog.Elite, r == CoachCatalog.HC ? 2 : r == CoachCatalog.PC ? 3 : 0)).ToArray();
			AddCoaches(user, elite);
			AddCoaches(user, new[] { CoachCatalog.Card(club, CoachCatalog.HD, CoachCatalog.Special), CoachCatalog.Card(other, CoachCatalog.CL, CoachCatalog.Normal) });
			Check(TryApply(user, new[] { elite[1], elite[0], 0, 0, 0 }) != null && TryApply(user, new[] { CoachCatalog.Card(club, CoachCatalog.HD, CoachCatalog.Rare), 0, 0, 0, 0 }) != null
				&& TryApply(user, new[] { elite[0], 0, 0, 0, 0 }) == null && TryApply(user, new[] { elite[0], 0, 0, 0, 0 }) == null, "wrong slot and unowned refused, a card in its slot ok");
			Check(TryApply(user, elite) == null && SetBonusOf(Staff(user)) == (club, 3), "five 엘리트 of one club: +3");
			Check(TryApply(user, new[] { CoachCatalog.Card(club, CoachCatalog.HD, CoachCatalog.Special), elite[1], elite[2], elite[3], elite[4] }) == null && SetBonusOf(Staff(user)) == (club, 1)
				&& TryApply(user, new[] { elite[0], elite[1], elite[2], elite[3], CoachCatalog.Card(other, CoachCatalog.CL, CoachCatalog.Normal) }) == null && SetBonusOf(Staff(user)).bonus == 0,
				"lowest grade 스페셜: +1; a 치어리더 of another club: none");
			TryApply(user, elite);
			List<TeamCard> cards = s_db.GetCards(user);
			TeamCard b = cards.First(x => x.Idx == batter.Idx), s = cards.First(x => x.Idx == stranger.Idx);
			Check(b.Coach.SequenceEqual(new sbyte[] { 3, 5, 3, 3, 3, 3 }) && s.Coach.All(v => v == 0) && TeamStrength.Bonus(b)[1] == b.Educated[1] + b.Contract[1] + b.Training[1] + b.TeamColor[1] + 5,
				$"{batter.Name}: +3 all and 파워 +2 ({string.Join(" ", b.Coach)}), {stranger.Name} none, in TeamStrength.Bonus");
			if (pitcher != null)
			{
				TeamCard p = cards.First(x => x.Idx == pitcher.Idx);
				Check(p.Coach.SequenceEqual(new sbyte[] { 3, 3, 5, 3, 3, 3 }), $"{pitcher.Name}: +3 all and 컨트롤 +2 ({string.Join(" ", p.Coach)})");
			}

			// 면담: visitors, SP, steps, 12 hours.
			AddSp(user, CoachCatalog.MaxSp);
			List<Visitor> visitors = Seat(user, elite[CoachCatalog.HD], now);
			Check(visitors.Count <= CoachCatalog.Visitors[CoachCatalog.Elite] && visitors.All(v => regulars.Contains(v.Card) && !bench.Contains(v.Card) && s_catalog.Get(v.Card).TeamCode == club && Condition.For(user, v.Card, now) >= 3),
				$"visitors: {visitors.Count} ({string.Join(", ", visitors.Select(v => $"{s_catalog.Get(v.Card).Name} {Condition.For(user, v.Card, now)}{(v.Slump ? " 부진" : string.Empty)}"))}), regulars of the club, 나쁨 or worse");
			if (CoachCatalog.TestMode)
			{
				// Test servers make more of the club's 1군 나쁨 or worse when too few are (user 11-04), never anyone else.
				int ofClub = regulars.Distinct().Count(i => s_catalog.Get(i)?.TeamCode == club);
				Check(visitors.Count == Math.Min(CoachCatalog.Visitors[CoachCatalog.Elite], ofClub), $"test server: {visitors.Count} seated of {ofClub} club regulars");
			}
			if (visitors.Count > 0)
			{
				int card = visitors[0].Card, before = Condition.For(user, card, now), sp0 = Sp(user);
				string r1 = TryStartConsult(user, card, out int row1), r2 = TryStartConsult(user, card, out int row2);
				Check(r1 == null && r2 == null && row1 == row2 && rooms.Any(c => c[0] == row1.ToString()) && Sp(user) == sp0 - CoachCatalog.ConsultSp, $"면담 start: −{CoachCatalog.ConsultSp} SP once, dialogue {row1}");
				string e1 = TryEndConsult(user, card, now, out int steps), e2 = TryEndConsult(user, card, now, out _);
				int after = Condition.For(user, card, now);
				Check(e1 == null && e2 != null && steps >= 1 && steps <= 3 && after == Math.Max(0, before - steps), $"면담 end: +{steps} steps ({before} → {after}), once");
				Check(ConsultSteps(user, card, now.AddHours(11.9)) == steps && ConsultSteps(user, card, now.AddHours(12.1)) == 0 && ConsultSteps(user, card, now.AddMinutes(-1)) == 0, "12 hours from the end");
				Check(TryStartConsult(user, card, out _) != null && Seat(user, elite[CoachCatalog.HD], now).Count(v => v.Card == card) == 1, "done stays done, no second seat");
			}
			int[] steps3 = new int[4];
			for (int n = 0; n < 30_000; n++) steps3[s_rng.Next(1, 4)]++;
			Check(steps3.Skip(1).All(v => Math.Abs(v / 300.0 - 33.3) < 1.5), $"steps 1/2/3: {string.Join("/", steps3.Skip(1).Select(v => (v / 300.0).ToString("F1")))}%");

			// 트레이닝코치's odds for his club, not another's.
			int[] tops = new int[2];
			CardInfo strangerBatter = s_catalog.All.First(i => i.IsBatter && i.TeamCode != club);
			for (int n = 0; n < 100_000; n++)
			{
				if (RollResult(user, batter, rng) == 3) tops[0]++;
				if (RollResult(user, strangerBatter, rng) == 3) tops[1]++;
			}
			// Elite 20/21/23/36 since 2026-11-12 (each square of 22/22/22/34 −2/−1/+1/+2).
			Check(Math.Abs(tops[0] / 1000.0 - 36) < 1 && Math.Abs(tops[1] / 1000.0 - 25) < 1, $"best square: own club {tops[0] / 1000.0:F1}%, other {tops[1] / 1000.0:F1}%");

			// 타격코치: comes 50% (엘리트), on the best result too, not to another club's card, not without SP, not on the lowest square
			// (2026-11-12); takes the slot up for 50 SP.
			int came = 0;
			for (int n = 0; n < 20_000; n++)
			{
				Ma9MPacket.PKS_SC_TRY_CARD_TRAINING t = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
				OfferTraining(user, batter, batter.Idx, 0, TrainingBooks.Book(1, 1), 1 + n % 3, t, rng);
				came += t.encounter_coach.n;
			}
			s_offers.Clear();
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING top = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING(), strange = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING(), lowest = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
			OfferTraining(user, batter, batter.Idx, 0, TrainingBooks.Book(1, 3), 3, top, new Always());
			OfferTraining(user, strangerBatter, strangerBatter.Idx, 0, TrainingBooks.Book(1, 3), 1, strange, new Always());
			OfferTraining(user, batter, batter.Idx, 0, TrainingBooks.Book(1, 3), 0, lowest, new Always());
			s_offers.Clear();
			Check(Math.Abs(came / 200.0 - CoachCatalog.TrainingChance[CoachCatalog.Elite]) < 1.5 && top.encounter_coach.n == 1 && strange.encounter_coach.n == 0 && lowest.encounter_coach.n == 0,
				$"타격코치 came {came / 200.0:F1}%, on the best result {top.encounter_coach.n}, to another club's card {strange.encounter_coach.n}, on the lowest square {lowest.encounter_coach.n}");
			int book = TrainingBooks.Book(2, 3);
			TrainingHandlers.SetSlot(user, batter.Idx, 2, book, 1);
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING offer = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
			OfferTraining(user, batter, batter.Idx, 2, book, 1, offer, new Always());
			int sp1 = Sp(user);
			string took = TryTake(user, offer.transaction_id.n, out int slot, out _, out string what);
			(int item, int result)[] slots = TrainingHandlers.SlotsOf(user, batter.Idx);
			Check(offer.encounter_coach.n == 1 && took == null && slot == 2 && slots[2] == (book, 2) && Sp(user) == sp1 - CoachCatalog.TrainingSp && TryTake(user, offer.transaction_id.n, out _, out _, out _) != null,
				$"코치 트레이닝: {what}, −{CoachCatalog.TrainingSp} SP, once");
			TrainingHandlers.SetSlot(user, batter.Idx, 2, book, 3);
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING best = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
			OfferTraining(user, batter, batter.Idx, 2, book, 3, best, new Always());
			string bestTook = TryTake(user, best.transaction_id.n, out _, out _, out string bestWhat);
			Check(best.encounter_coach.n == 1 && bestTook == null && TrainingHandlers.SlotsOf(user, batter.Idx)[2] == (book, 4) && TrainingBooks.Value(book, 4) == 5,
				$"best result +1: {bestWhat}");
			AddSp(user, -CoachCatalog.MaxSp);
			Ma9MPacket.PKS_SC_TRY_CARD_TRAINING poor = new Ma9MPacket.PKS_SC_TRY_CARD_TRAINING();
			OfferTraining(user, batter, batter.Idx, 2, book, 1, poor, new Always());
			Check(poor.encounter_coach.n == 0, "no coach under 50 SP");
			AddSp(user, CoachCatalog.MaxSp);

			// 트레이닝북 조합: III × 2 (엘리트) → IV; another count, or no 트레이닝코치, refused.
			int iii = TrainingBooks.Book(4, 3);
			InventoryHandlers.AddItem(user, iii, 5);
			string three = TrainingHandlers.TryMix(user, new[] { iii, iii, iii }, out _);
			string two = TrainingHandlers.TryMix(user, new[] { iii, iii }, out int iv);
			TryApply(user, new[] { elite[0], elite[1], elite[2], 0, elite[4] });
			string none = TrainingHandlers.TryMix(user, new[] { iii, iii }, out _);
			string ivUp = TrainingHandlers.TryMix(user, new[] { iv, iv, iv, iv }, out _);
			Check(three != null && two == null && TrainingBooks.Grade(iv) == 4 && TrainingBooks.IsBook(iv) && InventoryHandlers.ItemAmount(user, iv) >= 1 && none != null && ivUp != null && ItemCatalog.Get(iv) != null,
				$"III ×2 → {TrainingBooks.Name(iv)} ({TrainingBooks.Value(iv, 0)}…{TrainingBooks.Value(iv, 3)}); ×3 refused; no 트레이닝코치: {none}; IV: {ivUp}");
			TryApply(user, elite);

			// 치어리더: +15% of a game's MP (fixed when first looked at), paid once at the confirm with SP for the win; the gift once a day.
			LeagueGame game = new LeagueGame { Season = 999, Game = 1, Home = 0, Away = 1, HomeScore = 5, AwayScore = 2, Mp = 24_000, Confirmed = false, Detail = new GameDetail { Owner = new[] { user, 0u } } };
			short shown = Cheer(user, game);
			int mp0 = s_db.AddMp(user, 0), sp2 = Sp(user);
			OnLeagueConfirmed(user, new[] { game });
			int mp1 = s_db.AddMp(user, 0), sp3 = Sp(user);
			OnLeagueConfirmed(user, new[] { game });
			Check(shown == 3600 && mp1 - mp0 == 3600 && s_db.AddMp(user, 0) == mp1 && sp3 == Math.Min(CoachCatalog.MaxSp, sp2 + CoachCatalog.LeagueWinSp),
				$"치어리더: +{shown:N0} MP on 24,000, paid once, SP {sp2} → {sp3}");
			LeagueGame old = new LeagueGame { Season = 999, Game = 2, Mp = 24_000, Confirmed = true };
			Check(Cheer(user, old) == 0, "a game confirmed without the 치어리더 record: none");
			int potions = InventoryHandlers.ItemAmount(user, PlayerManageHandlers.Advanced), pouches = InventoryHandlers.ItemAmount(user, ShopHandlers.LuckyPouch);
			string g1 = TryGift(user), g2 = TryGift(user);
			Check(g1 == null && g2 != null && InventoryHandlers.ItemAmount(user, PlayerManageHandlers.Advanced) == potions + 1 && InventoryHandlers.ItemAmount(user, ShopHandlers.LuckyPouch) == pouches + 1,
				"선물: 고급 컨디션 회복제 1, MP주머니 1, once a day");
			// 조합 (user 11-05): four of one grade; four copies → that card up, others → a random card up, four 엘리트 → a random 엘리트;
			// 11-09/11-10: four of one line → that line, three 엘리트 of one line → a 엘리트 of that line.
			int pcN = CoachCatalog.Card(other, CoachCatalog.PC, CoachCatalog.Normal), hdN = CoachCatalog.Card(other, CoachCatalog.HD, CoachCatalog.Normal);
			int scN = CoachCatalog.Card(other, CoachCatalog.SC, CoachCatalog.Normal), clN = CoachCatalog.Card(other, CoachCatalog.CL, CoachCatalog.Normal);
			int staffHd = Staff(user)[CoachCatalog.HD];
			RemoveCoaches(user, Coaches(user).Where(i => CoachCatalog.Club(i) == other).ToArray());
			AddCoaches(user, new[] { pcN, pcN, pcN, pcN, hdN, scN, clN, pcN, CoachCatalog.Card(other, CoachCatalog.PC, CoachCatalog.Special) });
			string mixThree = TryCoachMix(user, new[] { pcN, pcN, pcN }, rng, out _, out _);
			string mixGrades = TryCoachMix(user, new[] { pcN, pcN, pcN, CoachCatalog.Card(other, CoachCatalog.PC, CoachCatalog.Special) }, rng, out _, out _);
			string mixUnheld = TryCoachMix(user, new[] { pcN, pcN, pcN, CoachCatalog.Card(other, CoachCatalog.HC, CoachCatalog.Normal) }, rng, out _, out _);
			string mixStaff = TryCoachMix(user, new[] { staffHd, staffHd, staffHd }, rng, out _, out _);
			int before5 = Coaches(user).Count;
			string up = TryCoachMix(user, new[] { pcN, pcN, pcN, pcN }, rng, out int upMade, out string upHow);
			List<int> afterUp = Coaches(user);
			Check(mixThree != null && mixGrades != null && mixUnheld != null && mixStaff?.Contains("등록") == true && up == null && upHow == "승급" && afterUp.Count == before5 - 3
				&& afterUp.Count(i => i == pcN) == 1 && upMade == CoachCatalog.Card(other, CoachCatalog.PC, CoachCatalog.Special) && afterUp.Count(i => i == upMade) == 2,
				$"조합 4 copies: {Label(pcN)} ×4 → {Label(upMade)} ({upHow}); three: {mixThree}; grades: {mixGrades}; unheld: {mixUnheld}; staff: {mixStaff}");
			string rand = TryCoachMix(user, new[] { pcN, hdN, scN, clN }, rng, out int randMade, out string randHow);
			Check(rand == null && randHow == "같은 구단" && CoachCatalog.LineOf(randMade) == CoachCatalog.Line(other) && CoachCatalog.Grade(randMade) == CoachCatalog.Special
				&& Coaches(user).Count == before5 - 6, $"조합 4 of one club, roles mixed (11-09): → {Label(randMade)} ({randHow})");
			// Four of one line many times: always that line, any role; four of two lines: any line (11-09 D-3).
			int[] lineRoles = new int[CoachCatalog.Roles];
			int lineOut = 0, anyLines = 0;
			HashSet<int> anyLineSeen = new HashSet<int>();
			int clubN = CoachCatalog.Card(club, CoachCatalog.HD, CoachCatalog.Normal);
			for (int n = 0; n < 60; n++)
			{
				AddCoaches(user, new[] { pcN, hdN, scN, clN });
				TryCoachMix(user, new[] { pcN, hdN, scN, clN }, rng, out int oneLineMade, out string oneLineHow);
				if (oneLineHow != "같은 구단" || CoachCatalog.LineOf(oneLineMade) != CoachCatalog.Line(other) || CoachCatalog.Grade(oneLineMade) != CoachCatalog.Special) lineOut++;
				lineRoles[CoachCatalog.Role(oneLineMade)]++;
				RemoveCoaches(user, new[] { oneLineMade });
				AddCoaches(user, new[] { pcN, pcN, clubN, clubN });
				TryCoachMix(user, new[] { pcN, clubN, pcN, clubN }, rng, out int twoLinesMade, out string twoLinesHow);
				if (twoLinesHow == "무작위" && CoachCatalog.Grade(twoLinesMade) == CoachCatalog.Special) anyLines++;
				anyLineSeen.Add(CoachCatalog.LineOf(twoLinesMade));
				RemoveCoaches(user, new[] { twoLinesMade });
			}
			Check(lineOut == 0 && lineRoles.All(v => v > 0) && anyLines == 60 && anyLineSeen.Count >= 8,
				$"조합 ×60: one club → its line every time, roles {string.Join("/", lineRoles)}; two clubs → 무작위 {anyLines}, {anyLineSeen.Count} lines seen");
			int[] rares = Enumerable.Range(0, 4).Select(_ => CoachCatalog.Roll(rng, CoachCatalog.Rare, other, CoachCatalog.HC)).ToArray();
			AddCoaches(user, rares);
			string rareUp = TryCoachMix(user, rares, rng, out int eliteMade, out string rareHow);
			Check(rareUp == null && rareHow == "승급" && CoachCatalog.Club(eliteMade) == other && CoachCatalog.Role(eliteMade) == CoachCatalog.HC && CoachCatalog.Grade(eliteMade) == CoachCatalog.Elite,
				$"조합 4 레어 타격코치 (stats {string.Join(" ", rares.Select(CoachCatalog.Stat))}) → {Label(eliteMade)}");
			// One lineage (user 11-05 19:35): 현대 2 + 태평양 2 타격코치 → a 스페셜 타격코치 of 현대 or 태평양, by the cards put in;
			// LG 2 + MBC 2 → LG only (MBC no longer made, 11-05 20:40).
			int[] MixLine(int a, int b)
			{
				int aN = CoachCatalog.Card(a, CoachCatalog.HC, CoachCatalog.Normal), bN = CoachCatalog.Card(b, CoachCatalog.HC, CoachCatalog.Normal);
				int[] made = new int[20];
				for (int n = 0; n < 40; n++)
				{
					AddCoaches(user, new[] { aN, aN, bN, bN });
					TryCoachMix(user, new[] { aN, bN, aN, bN }, rng, out int lineMade, out string lineHow);
					if (lineHow == "승급" && CoachCatalog.Role(lineMade) == CoachCatalog.HC && CoachCatalog.Grade(lineMade) == CoachCatalog.Special) made[CoachCatalog.Club(lineMade)]++;
					RemoveCoaches(user, new[] { lineMade });
				}
				return made;
			}
			int[] hdMix = MixLine(4, 15), lgMix = MixLine(6, 16);
			Check(hdMix[4] + hdMix[15] == 40 && hdMix[4] > 5 && hdMix[15] > 5 && lgMix[6] == 40,
				$"조합 현대 2 + 태평양 2 타격코치 ×40 → 스페셜 타격코치 현대 {hdMix[4]}, 태평양 {hdMix[15]}; LG 2 + MBC 2 → LG {lgMix[6]}, MBC {lgMix[16]}");
			// Three 엘리트 (user 11-05: "남는 엘리트 3장으로"); spare copies of a card on the staff mix, the staff's own copy stays.
			AddCoaches(user, new[] { staffHd, staffHd });
			int elites0 = Coaches(user).Count;
			string eliteFour = TryCoachMix(user, new[] { staffHd, staffHd, eliteMade, eliteMade }, rng, out _, out _);
			string eliteRe = TryCoachMix(user, new[] { staffHd, staffHd, eliteMade }, rng, out int reMade, out string reHow);
			Check(eliteFour != null && eliteRe == null && reHow == "엘리트 다시 뽑기" && CoachCatalog.Grade(reMade) == CoachCatalog.Elite && Coaches(user).Count == elites0 - 2
				&& Coaches(user).Contains(staffHd) && Staff(user)[CoachCatalog.HD] == staffHd,
				$"조합 3 엘리트 (2 spare copies of the staff's {Label(staffHd)}) → {Label(reMade)} ({reHow}), staff kept; four: {eliteFour}");
			// Three 엘리트 of one line (user 11-10 01:5x: "같은 구단 엘리트 코치 3장을 넣으면 같은 구단 랜덤 엘리트 코치 1장"): a 엘리트 of
			// that line, any role; MBC 1 + LG 2 count as one line and make LG only (MBC no longer made).
			int[] eliteLineRoles = new int[CoachCatalog.Roles];
			int eliteLineOut = 0, lgOut = 0;
			int mbcE = CoachCatalog.Card(16, CoachCatalog.CL, CoachCatalog.Elite), lgE = CoachCatalog.Card(6, CoachCatalog.HD, CoachCatalog.Elite);
			for (int n = 0; n < 60; n++)
			{
				int[] trio = Enumerable.Range(0, 3).Select(_ => CoachCatalog.Roll(rng, CoachCatalog.Elite, other)).ToArray();
				AddCoaches(user, trio);
				// Three of one 타격·투수코치 go the 11-13 way (below).
				bool trioSame = trio.All(c => CoachCatalog.Role(c) == CoachCatalog.Role(trio[0])) && CoachCatalog.HasStat(CoachCatalog.Role(trio[0]), CoachCatalog.Elite);
				string e3 = TryCoachMix(user, trio, rng, out int e3Made, out string e3How);
				if (e3 != null || e3How != (trioSame ? "같은 코치 능력치 다시" : "같은 구단 엘리트") || CoachCatalog.LineOf(e3Made) != CoachCatalog.Line(other) || CoachCatalog.Grade(e3Made) != CoachCatalog.Elite) eliteLineOut++;
				eliteLineRoles[CoachCatalog.Role(e3Made)]++;
				RemoveCoaches(user, new[] { e3Made });
				AddCoaches(user, new[] { mbcE, lgE, lgE });
				TryCoachMix(user, new[] { mbcE, lgE, lgE }, rng, out int lgMade, out string lgHow);
				if (lgHow != "같은 구단 엘리트" || CoachCatalog.Club(lgMade) != 6 || CoachCatalog.Grade(lgMade) != CoachCatalog.Elite) lgOut++;
				RemoveCoaches(user, new[] { lgMade });
			}
			Check(eliteLineOut == 0 && eliteLineRoles.All(v => v > 0) && lgOut == 0 && reHow == "엘리트 다시 뽑기",
				$"조합 3 엘리트 of one club ×60 → its line every time, roles {string.Join("/", eliteLineRoles)}; MBC 1 + LG 2 → LG ({60 - lgOut}/60); two clubs → {reHow}");
			// Three 엘리트 타격코치 (투수코치) of one line (user, 2026-11-13: "역할은 그대로 두고 능력치만 다시 뽑게"): the same coach, the stat
			// drawn again; three 엘리트 수석코치 still give any 엘리트 of the line.
			Dictionary<int, int> rerollStats = new Dictionary<int, int>();
			int rerollOut = 0, hdRoles = 0;
			for (int n = 0; n < 60; n++)
			{
				int role = n % 2 == 0 ? CoachCatalog.HC : CoachCatalog.PC;
				int[] trio = Enumerable.Range(0, 3).Select(_ => CoachCatalog.Roll(rng, CoachCatalog.Elite, other, role)).ToArray();
				AddCoaches(user, trio);
				string r3 = TryCoachMix(user, trio, rng, out int r3Made, out string r3How);
				if (r3 != null || r3How != "같은 코치 능력치 다시" || CoachCatalog.Role(r3Made) != role || CoachCatalog.LineOf(r3Made) != CoachCatalog.Line(other) || CoachCatalog.Grade(r3Made) != CoachCatalog.Elite) rerollOut++;
				if (role == CoachCatalog.HC) rerollStats[CoachCatalog.Stat(r3Made)] = rerollStats.GetValueOrDefault(CoachCatalog.Stat(r3Made)) + 1;
				RemoveCoaches(user, new[] { r3Made });
				int[] hds = Enumerable.Range(0, 3).Select(_ => CoachCatalog.Card(other, CoachCatalog.HD, CoachCatalog.Elite)).ToArray();
				AddCoaches(user, hds);
				TryCoachMix(user, hds, rng, out int hdMade, out string hdHow);
				if (hdHow != "같은 구단 엘리트") rerollOut++;
				if (CoachCatalog.Role(hdMade) != CoachCatalog.HD) hdRoles++;
				RemoveCoaches(user, new[] { hdMade });
			}
			Check(rerollOut == 0 && rerollStats.Count >= 4 && hdRoles > 0,
				$"조합 3 엘리트 타격·투수코치 of one line ×60 → the same coach, 타격코치 stats {string.Join(" ", rerollStats.OrderBy(k => k.Key).Select(k => $"{CoachCatalog.StatName(CoachCatalog.HC, k.Key)} {k.Value}"))}; 3 수석코치 → any role ({hdRoles}/60 not 수석)");
			// 자팀 (OwnLine): the 1군's most common line.
			int ownLine = OwnLine(user);
			Lineup ownLineup = s_db.GetLineup(user);
			int ownCount = ownLineup == null ? 0 : ownLineup.Batters.Concat(ownLineup.Pitchers).Where(i => i != 0).Distinct().Count(i => s_catalog.Get(i) is CardInfo ci && CoachCatalog.Line(ci.TeamCode) == ownLine);
			int bestOther = ownLineup == null ? 0 : ownLineup.Batters.Concat(ownLineup.Pitchers).Where(i => i != 0).Distinct().Select(i => s_catalog.Get(i)).Where(ci => ci != null && CoachCatalog.Line(ci.TeamCode) != ownLine)
				.GroupBy(ci => CoachCatalog.Line(ci.TeamCode)).Select(g => g.Count()).DefaultIfEmpty(0).Max();
			Check(ownLineup == null ? ownLine == -1 : ownLine >= 0 && ownCount >= bestOther,
				$"자팀 계보 of autotest's 1군: {(ownLine >= 0 ? TeamColors.LineageName(ownLine) : "none")} {ownCount}장 (next line {bestOther}장)");
			// The shop writes the odds as % (per mille since 11-10).
			Check(Enumerable.Range(1, 4).Select(CoachCatalog.PackPercentText).SequenceEqual(new[] { "83", "14.5", "2", "0.5" }),
				$"pack odds text {string.Join(" · ", Enumerable.Range(1, 4).Select(CoachCatalog.PackPercentText))}%");
			// 상점 203: 30만 MP → opened at once (2026-11-08): a coach more, no pack in the inventory, the card on the reply;
			// more packs than the room for coaches are refused whole, MP kept.
			int mpBuy = s_db.AddMp(user, CoachCatalog.PackPrice), packsBuy = InventoryHandlers.ItemAmount(user, CoachCatalog.Pack), coachesBuy = Coaches(user).Count;
			var bought = ShopHandlers.Purchase(user, 203, 1, out string buyLog);
			var openReply = bought.Count == 1 ? bought[0] as Ma9MPacket.PKS_SC_BUY_ITEM_SHOP_NEW_CARDPACK : null;
			bool room = coachesBuy < CoachCatalog.MaxCoaches;
			Check(room ? Coaches(user).Count == coachesBuy + 1 && InventoryHandlers.ItemAmount(user, CoachCatalog.Pack) == packsBuy && s_db.AddMp(user, 0) == mpBuy - CoachCatalog.PackPrice
					&& openReply != null && openReply.BuyType == eBuyType.CARDPACK_COACH && openReply.error.error_code.n == 0
				: Coaches(user).Count == coachesBuy && s_db.AddMp(user, 0) == mpBuy,
				$"상점 코칭스태프 카드팩 바로 열기 ({coachesBuy} coaches before): {buyLog}");
			// Filled up to 97 coaches first (a purchase is at most 50 packs): 4 packs refused, MP kept; 3 open.
			int roomLeft = CoachCatalog.MaxCoaches - Coaches(user).Count;
			if (roomLeft > 3)
			{
				AddCoaches(user, Enumerable.Range(0, roomLeft - 3).Select(_ => CoachCatalog.Roll(rng)).ToList());
				roomLeft = 3;
			}
			int mpOver = s_db.AddMp(user, CoachCatalog.PackPrice * (roomLeft + 1));
			ShopHandlers.Purchase(user, 203, roomLeft + 1, out string overLog);
			Check(Coaches(user).Count == CoachCatalog.MaxCoaches - roomLeft && s_db.AddMp(user, 0) == mpOver, $"{roomLeft + 1} packs with room for {roomLeft}: refused, MP kept ({overLog})");
			ShopHandlers.Purchase(user, 203, roomLeft, out string fitLog);
			Check(Coaches(user).Count == CoachCatalog.MaxCoaches && s_db.AddMp(user, 0) == mpOver - CoachCatalog.PackPrice * roomLeft, $"{roomLeft} packs fill the 100: {fitLog}");
			var enter = ShopHandlers.BuildEnter(user);
			Check(enter.CoachCount.n == Coaches(user).Count && enter.MaxCoachCount.n == CoachCatalog.MaxCoaches, $"shop sends coaches {enter.CoachCount.n}/{enter.MaxCoachCount.n}");
			Check(AddSp(user, 5000) == CoachCatalog.MaxSp && AddSp(user, -5000) == 0, $"SP within 0 … {CoachCatalog.MaxSp}");

			ServerLog.Info($"[coachtest] {(problems == 0 ? "all ok" : problems + " problem(s)")}");
			return problems == 0 ? 0 : 1;
		}
	}
}
