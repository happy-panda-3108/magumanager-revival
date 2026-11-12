using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MPackets;
using Microsoft.Data.Sqlite;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 특별수강 (전력강화 > 특별강좌; user, 2026-10-22). Two coaches at a time, a batting one (the screen's first
	// page) and a pitching one (swipe right), from 22 former KBO managers taken in turn every RotationHours. A
	// coach takes players of his side who meet his condition (the original's ConditionCheck: 25세 이하 / 26~32세 /
	// 33세 이상 without 용병, or 용병 only) and are not in the 1군. Up to MaxStudents players at once over both
	// coaches; no ball limit. The fee is BaseFee × the original's factor by ball (1~4볼 2, 5~7볼 5, 8~9볼 10, 10볼
	// 20). A lesson lasts LessonHours with the coach it started under, whoever is on duty later; meanwhile the card
	// cannot go into the 1군, be combined away or released. When it ends the coach's main stat gets +4 (70%) or +5
	// (30%; 특별강좌 행운권 40/60) and his two sub stats +2 each, into team_card.educated (the 특강 share that the
	// card window and TeamStrength already read). A card holds one result: another lesson needs the 특별강좌
	// 초기화권, which clears the result (user: +4 → reset → the same coach again for +5). The result popup comes in
	// the lobby (PKS_SC_LESSON_RESULT). Table card_lesson: one row per card copy (team_card.acquired).
	public static class LessonHandlers
	{
		// Coaches change every RotationHours from 0시: once a day (user, 2026-10-22: "매일 0시"; 4 while testing).
		public const int RotationHours = 24;

		public const int LessonHours = 18;

		public const int MaxStudents = 6;

		public const int BaseFee = 40_000;

		// The original's item numbers: ItemListTable INDEX_LESSON_LUCKY_TICKET and INDEX_LESSON_RESET. Instant takes the
		// original 단축권's number and button (INDEX_LESSON_SHORTEN_TICKET): the 특강 즉시 완료권 (user, 2026-10-29: "선수의
		// 특강을 즉시 완료하는 아이템 … 비싸야한다고 생각함, 300만 MP"). Used when enrolling, the lesson ends at once; on a player
		// already in a lesson (a tap on his card), it ends now. Either way the result is in the card at once and the
		// 특강 screen shows it, so the lobby popup does not come again (state 2).
		public const int Lucky = 90631, Reset = 90101, Instant = 90621;

		// PKS_SC_POPUP type the client's SpecialLecture_Main reads (the coaches' times and the cards with a result).
		public const byte InfoPopupType = 10;

		// Enroll results (the client's messages): 0 ok, 1 other, 3 six players in lessons, 4 has a result (needs the
		// 초기화권), 5 no 초기화권, 6 no 행운권, 7 not enough MP, 8 not this coach's player, 9 no 즉시 완료권.
		public const byte Ok = 0, Failed = 1, Full = 3, HasResult = 4, NoResetTicket = 5, NoLuckyTicket = 6, NoMp = 7, NotEligible = 8, NoInstantTicket = 9;

		// 10 (2026-11-09): a 1군 player without the 즉시 완료권. The client has no message for it, so the server sends a popup
		// (FirstTeamText) and the client shows nothing more.
		public const byte InFirstTeam = 10;

		// 1군 선수 특강 (user, 2026-11-09, with the 선발 5경기 제한 SpBlock: "특강 보내기 위한 선발 교체는 상시로 되도록 … 보통은
		// 즉시완료권을 써서 팀전력 손실 없이 특강을 보낼 것 같음"; chosen "즉시 완료는 1군 그대로 + 예외"): 1군 players are listed too
		// and enroll with the 즉시 완료권 only, so the lesson ends before any game and the 1군, its 팀컬러 and the rotation stay
		// as they are. A pitcher taken out of the starters to go to a lesson without the ticket loses his 5-game block on
		// enrolling (SpBlock.Clear), so he may start again once the lesson is over.
		// The way to use one (AI tester LG, 2026-11-10: after buying 즉시 완료권 the same popup came again; a ticket is used only
		// once the box at the top right is tapped on, and nothing said so). With none held: where to get one.
		internal static string FirstTeamText(int tickets) => tickets > 0
			? $"1군 선수는 즉시 완료권을 함께 써야\n특강을 들을 수 있습니다.\n오른쪽 위 즉시 완료권 칸을 눌러 켠 뒤\n다시 신청해 주세요. (지금 {tickets}장)"
			: "1군 선수는 즉시 완료권을 함께 써야\n특강을 들을 수 있습니다. (상점에서 판매)\n(1군에서 빼고 신청하면 즉시 완료권 없이도 됩니다.)";

		private static readonly DateTime Epoch = new DateTime(2026, 10, 22, 0, 0, 0, DateTimeKind.Local);

		internal sealed class Coach
		{
			public short Code;
			public string Name, Year, Subject, Comment, Intro;
			public bool Pitcher;
			public int Condition, Main, Sub1, Sub2;
		}

		private static readonly string[] BatterStats = { "컨택", "파워", "스피드", "선구", "수비", "H정신" };

		private static readonly string[] PitcherStats = { "체력", "구속", "컨트롤", "테크닉", "구위", "P정신" };

		private static Coach C(short code, string name, string year, bool pitcher, int condition, int main, int sub1, int sub2, string subject, string comment, string intro)
		{
			string[] stats = pitcher ? PitcherStats : BatterStats;
			return new Coach
			{
				Code = code, Name = name, Year = year, Pitcher = pitcher, Condition = condition, Main = main, Sub1 = sub1, Sub2 = sub2, Subject = subject, Comment = comment,
				Intro = $"{intro} [{stats[main]} +4~5, {stats[sub1]}·{stats[sub2]} +2]"
			};
		}

		// The user's list (2026-10-22) in turn order: the age groups go round and a 용병 coach every fourth. Codes are
		// the sprites of the original's coach atlas (its 28 caricatures, named with a year); the six without one have
		// new sprites 10281-10331 (silhouettes with the name).
		internal static readonly Coach[] Batting =
		{
			C(10051, "김성근", "10", false, 1, 4, 2, 5, "김성근의 지옥 펑고", "'야신'의 끝없는 펑고로 수비 기본기를 다집니다.", "SK 왕조를 이끈 '야신' 김성근 감독. 지옥의 펑고 훈련으로 젊은 야수의 수비를 끌어올립니다."),
			C(10281, "백인천", "82", false, 2, 3, 0, 1, "백인천의 4할 타격론", "프로야구 유일의 4할 타자가 공을 고르는 눈을 가르칩니다.", "1982년 타율 .412, 프로야구 유일의 4할 타자 백인천 감독. 공 하나하나를 고르는 선구안을 길러 줍니다."),
			C(10031, "김인식", "09", false, 3, 5, 3, 0, "김인식의 믿음의 야구", "'국민 감독'이 베테랑 타자의 마음을 다잡습니다.", "WBC 4강과 준우승을 이끈 '국민 감독' 김인식. 믿음의 야구로 베테랑 타자의 정신력을 끌어올립니다."),
			C(10011, "로이스터", "09", false, 4, 1, 0, 5, "로이스터의 노 피어", "'No Fear!' 두려움 없는 스윙으로 장타를 노립니다.", "롯데를 3년 연속 가을야구로 이끈 제리 로이스터 감독. 겁 없는 스윙으로 용병 타자의 파워를 키웁니다."),
			C(10001, "김경문", "08", false, 1, 2, 4, 0, "김경문의 뛰는 야구", "두산 '화수분'의 발야구로 젊은 타자를 키웁니다.", "베이징 올림픽 금메달의 김경문 감독. 두산 화수분 야구의 뛰는 야구로 젊은 타자의 스피드를 끌어올립니다."),
			C(10141, "김재박", "09", false, 2, 4, 0, 2, "김재박의 여우 수비", "현대 왕조의 '여우'가 영리한 수비를 가르칩니다.", "현대 유니콘스 왕조를 이끈 '그라운드의 여우' 김재박 감독. 영리한 수비 위치 선정을 가르칩니다."),
			C(10111, "이만수", "11", false, 3, 1, 3, 5, "이만수의 헐크 파워", "'헐크'의 홈런 비결을 베테랑 타자에게 전합니다.", "KBO 첫 트리플 크라운의 '헐크' 이만수 감독. 홈런왕 포수의 장타 비결로 베테랑 타자의 파워를 살립니다."),
			C(10291, "서튼", "21", false, 4, 3, 0, 1, "서튼의 홈런왕 노트", "KBO 홈런왕 출신이 용병 타자의 눈을 다듬습니다.", "2005년 현대에서 홈런왕에 오른 래리 서튼 감독. 낯선 리그의 투수를 읽는 선구안을 용병 타자에게 가르칩니다."),
			C(10241, "이광환", "94", false, 1, 0, 3, 5, "이광환의 자율 야구", "스스로 생각하는 자율 야구로 타격을 다듬습니다.", "1994년 LG를 정상에 올린 '자율 야구'의 이광환 감독. 스스로 생각하는 타격으로 젊은 타자의 컨택을 높입니다."),
			C(10091, "김기태", "11", false, 2, 1, 0, 5, "김기태의 형님 리더십", "거포 출신 '형님'이 한창때 타자의 힘을 깨웁니다.", "2017년 KIA 통합 우승의 김기태 감독. 거포 출신다운 장타 지도와 형님 리더십으로 한창때 타자의 파워를 키웁니다."),
			C(10131, "한대화", "10", false, 3, 0, 1, 5, "한대화의 해결사 타격", "'해결사'의 노련한 타격을 베테랑에게 전합니다.", "결정적인 순간의 '해결사' 한대화 감독. 노련한 타격으로 베테랑 타자의 컨택을 끌어올립니다."),
		};

		internal static readonly Coach[] Pitching =
		{
			C(10021, "선동열", "10", true, 1, 4, 1, 2, "선동열의 국보 투구", "'국보 투수'가 젊은 투수의 공끝을 살립니다.", "통산 평균자책점 1.20의 '국보 투수' 선동열 감독. 묵직한 공끝으로 젊은 투수의 구위를 끌어올립니다."),
			C(10041, "김응용", "00", true, 2, 5, 0, 4, "김응용의 카리스마", "해태 왕조의 카리스마로 마운드의 배짱을 키웁니다.", "한국시리즈 10회 우승의 김응용 감독. 압도적인 카리스마로 투수의 배짱과 정신력을 다집니다."),
			C(10301, "김태형", "15", true, 3, 2, 3, 5, "김태형의 포수 리드", "포수 출신 감독이 베테랑 투수의 제구를 다듬습니다.", "두산을 7년 연속 한국시리즈로 이끈 포수 출신 김태형 감독. 포수의 눈으로 베테랑 투수의 제구를 가다듬습니다."),
			C(10311, "힐만", "18", true, 4, 2, 4, 5, "힐만의 마운드 운용", "SK 우승 감독이 용병 투수의 제구를 잡아 줍니다.", "2018년 SK 우승을 이끈 트레이 힐만 감독. 세심한 마운드 운용으로 용병 투수의 제구를 잡아 줍니다."),
			C(10071, "김시진", "10", true, 1, 2, 3, 0, "김시진의 투수 육성", "투수 육성의 대가가 젊은 투수의 제구를 잡습니다.", "현대와 넥센에서 수많은 투수를 길러 낸 김시진 감독. 기본기부터 젊은 투수의 제구를 잡아 줍니다."),
			C(10081, "류중일", "11", true, 2, 1, 0, 4, "류중일의 불펜 왕국", "삼성 불펜 왕국의 비결로 공의 속도를 높입니다.", "삼성 통합 4연패의 류중일 감독. 불펜 왕국을 만든 비결로 한창때 투수의 구속을 끌어올립니다."),
			C(10121, "조범현", "09", true, 3, 4, 2, 0, "조범현의 포수 눈", "포수 출신 우승 감독이 베테랑의 공끝을 살립니다.", "2009년 KIA를 정상에 올린 포수 출신 조범현 감독. 베테랑 투수의 공끝을 다시 살려 냅니다."),
			C(10321, "수베로", "21", true, 4, 3, 1, 4, "수베로의 육성 야구", "리빌딩을 이끈 감독이 용병 투수의 변화구를 다듬습니다.", "한화의 리빌딩을 맡았던 카를로스 수베로 감독. 데이터와 육성으로 용병 투수의 테크닉을 다듬습니다."),
			C(10331, "양상문", "14", true, 1, 3, 2, 5, "양상문의 투수 교실", "투수 코치 출신 감독이 젊은 투수의 변화구를 가르칩니다.", "투수 코치로 이름을 날린 양상문 감독. 젊은 투수에게 변화구와 완급 조절을 가르칩니다."),
			C(10221, "염경엽", "13", true, 2, 3, 2, 1, "염경엽의 데이터 야구", "데이터 야구로 한창때 투수의 볼 배합을 다듬습니다.", "2023년 LG 통합 우승의 염경엽 감독. 데이터 야구로 한창때 투수의 테크닉을 끌어올립니다."),
			C(10231, "강병철", "06", true, 3, 0, 4, 5, "강병철의 철완 관리", "최동원의 한국시리즈 4승을 이끈 감독이 체력을 다집니다.", "1984년 최동원의 한국시리즈 4승을 이끈 강병철 감독. 베테랑 투수의 체력과 지구력을 끌어올립니다."),
		};

		private static Database s_db;

		private static CardCatalog s_catalog;

		private static string s_cs;

		private static readonly Random s_rng = new Random();

		private static System.Threading.Timer s_timer;

		private static string s_tablePath;

		// ma9_itemlist rows (ItemCatalog), type 10 LESSON: used on the 특별강좌 screen, not in the inventory.
		public static IEnumerable<ItemDef> Items()
		{
			yield return new ItemDef
			{
				Index = Reset, Name = "특별강좌 초기화권", Type = ItemType.Lesson, TypeText = "특별강좌 아이템", File = "lesson_reset",
				Desc = "선수 한 명의 특별수강 결과를 지우고 다시 수강하게 합니다. 특별강좌에서 결과가 있는 선수를 신청할 때 씁니다."
			};
			yield return new ItemDef
			{
				Index = Lucky, Name = "특별강좌 행운권", Type = ItemType.Lesson, TypeText = "특별강좌 아이템", File = "lesson_lucky",
				Desc = "특별수강의 주 능력치가 +5로 나올 확률을 30%에서 60%로 올립니다. 특별강좌 수강신청 화면에서 씁니다."
			};
			yield return new ItemDef
			{
				Index = Instant, Name = "특강 즉시 완료권", Type = ItemType.Lesson, TypeText = "특별강좌 아이템", File = "lesson_instant",
				Desc = "특별수강을 기다리지 않고 바로 끝내 결과를 받습니다. 수강신청 화면에서 쓰거나, 수강 중인 선수 카드를 눌러 씁니다."
			};
		}

		internal static Coach CoachOf(int code) => Batting.Concat(Pitching).FirstOrDefault(c => c.Code == code);

		// The coach on duty for a side, and his shift.
		internal static (Coach coach, DateTime from, DateTime until) OnDuty(bool pitcher, DateTime now)
		{
			long shift = (long)Math.Floor((now - Epoch).TotalHours / RotationHours);
			Coach[] list = pitcher ? Pitching : Batting;
			int i = (int)(((shift % list.Length) + list.Length) % list.Length);
			DateTime from = Epoch.AddHours(shift * RotationHours);
			return (list[i], from, from.AddHours(RotationHours));
		}

		// 1~4볼 2, 5~7볼 5, 8~9볼 10, 10볼 20 (TRRow_LessonCoach.GetCost).
		public static int Fee(int ball) => BaseFee * (ball <= 4 ? 2 : ball <= 7 ? 5 : ball <= 9 ? 10 : 20);

		// TRRow_LessonCoach.ConditionCheck for the conditions in use.
		internal static bool Eligible(Coach coach, CardInfo card)
		{
			if (card == null || card.IsBatter == coach.Pitcher)
			{
				return false;
			}
			return coach.Condition switch
			{
				1 => !card.Foreigner && card.Age <= 25,
				2 => !card.Foreigner && card.Age >= 26 && card.Age <= 32,
				3 => !card.Foreigner && card.Age >= 33,
				4 => card.Foreigner,
				_ => !card.Foreigner
			};
		}

		// Main stat +4 or +5 (행운권: +5 more often), the two sub stats +2.
		internal static sbyte[] Roll(Coach coach, bool lucky, Random rng)
		{
			sbyte[] stat = new sbyte[6];
			stat[coach.Main] = (sbyte)(rng.NextDouble() < (lucky ? 0.6 : 0.3) ? 5 : 4);
			stat[coach.Sub1] += 2;
			stat[coach.Sub2] += 2;
			return stat;
		}

		public static void Register(GameLoop loop, Database db, CardCatalog catalog, string dataDir)
		{
			s_db = db;
			s_catalog = catalog;
			s_cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDir, "ma9m.db") }.ToString();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				// state 0 in the lesson, 1 ended (result written), 2 its result popup shown.
				cmd.CommandText = @"CREATE TABLE IF NOT EXISTS card_lesson (
					user_code INTEGER NOT NULL,
					card_idx INTEGER NOT NULL,
					acquired TEXT NOT NULL,
					coach INTEGER NOT NULL,
					started TEXT NOT NULL,
					ends TEXT NOT NULL,
					lucky INTEGER NOT NULL,
					state INTEGER NOT NULL,
					result BLOB,
					PRIMARY KEY (user_code, card_idx))";
				cmd.ExecuteNonQuery();
			}
			s_tablePath = Path.Combine(dataDir, "tables", "SpecialCourseCoach.txt");
			WriteTable(s_tablePath);
			if (loop == null)
			{
				return;
			}
			loop.Register<Ma9MPacket.PKS_CS_ENTER_LESSON>(Enter);
			loop.Register<Ma9MPacket.PKS_CS_ENROLL_LESSON>(Enroll);
			loop.Register<Ma9MPacket.PKS_CS_DROP_LESSON>(Drop);
			// Lessons end on their own: results reach the cards (and the games) within half a minute.
			loop.Post(() => FinishDue(DateTime.Now));
			s_timer = new System.Threading.Timer(_ => loop.Post(() => FinishDue(DateTime.Now)), null, 30_000, 30_000);
		}

		private static SqliteConnection Open()
		{
			SqliteConnection c = new SqliteConnection(s_cs);
			c.Open();
			return c;
		}

		private static SqliteCommand Command(SqliteConnection c, string sql, uint user, int idx = 0)
		{
			SqliteCommand cmd = c.CreateCommand();
			cmd.CommandText = sql;
			cmd.Parameters.AddWithValue("$u", (long)user);
			cmd.Parameters.AddWithValue("$i", idx);
			return cmd;
		}

		// SpecialCourseCoach.txt (TableReader.TR_LessonCoach): code, name, (side), contact … mental (12 one- or
		// two-digit columns: the main stat 5, the subs 2), comment, introduction, subject, condition, term (a unique
		// sort key: the turn order), tuition. A bad row would stop the client's table loading, so no quotes or tabs.
		private static void WriteTable(string path)
		{
			StringBuilder sb = new StringBuilder("code\tname\tside\tcontact\tpower\tspeed\teye\tdefence\tmen\thealth\tballSpeed\tballControl\ttechnique\tbreakingBall\tmental\tcomment\tintroduction\tsubject\tcondition\tterm\ttuition\n");
			int term = 1;
			foreach (Coach c in Batting.Concat(Pitching))
			{
				int[] stats = new int[12];
				int at = c.Pitcher ? 6 : 0;
				stats[at + c.Main] = 5;
				stats[at + c.Sub1] = 2;
				stats[at + c.Sub2] = 2;
				string Clean(string s) => s.Replace("\t", " ").Replace("\"", "'").Replace("\n", " ");
				sb.Append($"{c.Code}\t{c.Name}\t{(c.Pitcher ? "투수" : "타자")}\t{string.Join("\t", stats)}\t{Clean(c.Comment)}\t{Clean(c.Intro)}\t{Clean(c.Subject)}\t{c.Condition}\t{term++}\t{BaseFee}\n");
			}
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			string text = sb.ToString();
			if (!File.Exists(path) || File.ReadAllText(path, Encoding.UTF8) != text)
			{
				File.WriteAllText(path, text, new UTF8Encoding(false));
			}
		}

		private sealed class Lesson
		{
			public int Idx, Coach, State;
			public DateTime Ends;
			public bool Lucky;
			public byte[] Result;
		}

		// The user's lessons on cards the team still has (the copy the lesson was for).
		private static List<Lesson> Lessons(uint user)
		{
			List<Lesson> list = new List<Lesson>();
			using SqliteConnection c = Open();
			using SqliteCommand cmd = Command(c, @"SELECT l.card_idx, l.coach, l.state, l.ends, l.lucky, l.result FROM card_lesson l
				JOIN team_card t ON t.user_code = l.user_code AND t.card_idx = l.card_idx AND t.acquired = l.acquired WHERE l.user_code = $u", user);
			using SqliteDataReader r = cmd.ExecuteReader();
			while (r.Read())
			{
				list.Add(new Lesson
				{
					Idx = r.GetInt32(0), Coach = r.GetInt32(1), State = r.GetInt32(2),
					Ends = DateTime.Parse(r.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), Lucky = r.GetInt32(4) != 0,
					Result = r.IsDBNull(5) ? null : (byte[])r[5]
				});
			}
			return list;
		}

		// The coach whose finished lesson the card holds, for the card window's 특강 line (client TR_LessonCoach shows
		// "류중일(5)"; user, 2026-10-27), or 0.
		public static int ResultCoach(uint user, int idx) =>
			s_cs == null || user == 0 ? 0 : Lessons(user).FirstOrDefault(l => l.Idx == idx && l.State != 0)?.Coach ?? 0;

		// Cards in a lesson now (not usable in the 1군, combining or releasing).
		public static HashSet<int> InLesson(uint user)
		{
			if (s_cs == null || user == 0)
			{
				return new HashSet<int>();
			}
			return Lessons(user).Where(l => l.State == 0).Select(l => l.Idx).ToHashSet();
		}

		public static bool InLesson(uint user, int idx) => InLesson(user).Contains(idx);

		// A card that holds this coach's +5 (main stat) from a finished lesson is not his player any more (user, 2026-11-12
		// 10:4x: "해당 특강 코치의 5 특강을 받은 선수는 더이상 등장 안 하는걸로 … 염경엽 코치에 한해서는 더이상 리스트에 나오지 않게");
		// other coaches still list him (with the 초기화권 as before).
		private static bool TopFromCoach(Lesson l, Coach coach, TeamCard card) =>
			l != null && coach != null && card != null && l.State != 0 && l.Coach == coach.Code && l.Result != null && l.Result.Length > coach.Main
			&& (sbyte)l.Result[coach.Main] >= 5 && card.Educated.Any(v => v != 0);

		// Ends every lesson that is due: its result into the card, the 업적 counted. Lessons whose card is gone
		// (released or combined away; not possible while in a lesson, but an old row) are dropped.
		public static void FinishDue(DateTime now)
		{
			if (s_cs == null)
			{
				return;
			}
			List<uint> users = new List<uint>();
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT DISTINCT user_code FROM card_lesson WHERE state = 0";
				using SqliteDataReader r = cmd.ExecuteReader();
				while (r.Read()) users.Add((uint)r.GetInt64(0));
			}
			foreach (uint user in users)
			{
				Finish(user, now);
			}
		}

		internal static List<(int idx, sbyte[] stat)> Finish(uint user, DateTime now)
		{
			List<(int, sbyte[])> done = new List<(int, sbyte[])>();
			List<Lesson> lessons = Lessons(user);
			using SqliteConnection c = Open();
			using (SqliteCommand stale = Command(c, @"DELETE FROM card_lesson WHERE user_code = $u AND state = 0 AND NOT EXISTS
				(SELECT 1 FROM team_card t WHERE t.user_code = card_lesson.user_code AND t.card_idx = card_lesson.card_idx AND t.acquired = card_lesson.acquired)", user))
			{
				stale.ExecuteNonQuery();
			}
			foreach (Lesson l in lessons.Where(l => l.State == 0 && l.Ends <= now))
			{
				Coach coach = CoachOf(l.Coach);
				if (coach == null)
				{
					continue;
				}
				sbyte[] stat = Roll(coach, l.Lucky, s_rng);
				byte[] blob = stat.Select(v => (byte)v).ToArray();
				using SqliteTransaction tx = c.BeginTransaction();
				using (SqliteCommand card = Command(c, "UPDATE team_card SET educated = $e WHERE user_code = $u AND card_idx = $i", user, l.Idx))
				{
					card.Transaction = tx;
					card.Parameters.Add("$e", SqliteType.Blob).Value = blob;
					card.ExecuteNonQuery();
				}
				using (SqliteCommand row = Command(c, "UPDATE card_lesson SET state = 1, result = $r WHERE user_code = $u AND card_idx = $i", user, l.Idx))
				{
					row.Transaction = tx;
					row.Parameters.Add("$r", SqliteType.Blob).Value = blob;
					row.ExecuteNonQuery();
				}
				tx.Commit();
				done.Add((l.Idx, stat));
				Missions.Add(user, Missions.Lessons);
				if (stat[coach.Main] >= 5)
				{
					Missions.Add(user, Missions.LessonsTop);
				}
				ServerLog.Info($"특별수강 끝 user {user}: {s_catalog?.Get(l.Idx)?.Name} ← {coach.Name} [{Summary(coach.Pitcher, stat)}]");
			}
			return done;
		}

		internal static string Summary(bool pitcher, IReadOnlyList<sbyte> stat)
		{
			string[] names = pitcher ? PitcherStats : BatterStats;
			return string.Join(" · ", Enumerable.Range(0, 6).Where(i => stat[i] != 0).OrderByDescending(i => stat[i]).Select(i => $"{names[i]} +{stat[i]}"));
		}

		private static PUICardInfo Info(uint user, TeamCard card, DateTime now) =>
			new PUICardInfo(card.Idx, Condition.For(user, card.Idx, now), card.RemainContract, card.PositionTrainingInfo, (byte)(card.IsLock ? 2 : 1));

		private static HashSet<int> Lineup(uint user)
		{
			Lineup l = s_db.GetLineup(user);
			return l == null ? new HashSet<int>() : l.Batters.Concat(l.Pitchers).Where(i => i != 0).ToHashSet();
		}

		private static void Enter(GameSession session, Ma9MPacket.PKS_CS_ENTER_LESSON packet)
		{
			if (session.Team == null)
			{
				return;
			}
			// The 1.0.45 app also shows the info popup (type 10) as a 추천 배분 / 자동 편성 window (user 11-12 01:0x): its 적용 must
			// find nothing waiting (AdviceHandlers, types 20/21 from 1.0.46).
			AdviceHandlers.DropOld(session.Team.UserCode);
			foreach (NetworkLibrary.Packet.PKS_PACKET reply in EnterReplies(session.Team.UserCode, DateTime.Now))
			{
				session.Send(reply);
			}
		}

		// The info popup, then the batting page and the pitching page (the client keeps the two in that order).
		internal static List<NetworkLibrary.Packet.PKS_PACKET> EnterReplies(uint user, DateTime now)
		{
			Finish(user, now);
			List<TeamCard> cards = s_db.GetCards(user);
			List<Lesson> lessons = Lessons(user);
			HashSet<int> active = lessons.Where(l => l.State == 0).Select(l => l.Idx).ToHashSet();
			HashSet<int> lineup = Lineup(user);
			int mp = s_db.AddMp(user, 0);
			List<NetworkLibrary.Packet.PKS_PACKET> replies = new List<NetworkLibrary.Packet.PKS_PACKET>();
			string Shift(DateTime from, DateTime until) => $"{from:M.d HH:mm} ~ {until:M.d HH:mm}";
			var bat = OnDuty(false, now);
			var pit = OnDuty(true, now);
			// Cards with a result (특강 share above zero): the client asks before using the 초기화권 on them.
			string results = string.Join("|", cards.Where(c => c.Educated.Any(v => v != 0)).Select(c =>
				$"{c.Idx}={Summary(s_catalog.Get(c.Idx)?.IsBatter == false, c.Educated)}"));
			Ma9MPacket.PKS_SC_POPUP info = new Ma9MPacket.PKS_SC_POPUP();
			info.Type.n = InfoPopupType;
			info.Messages.array = new[]
			{
				new PLongString { str = "1" },
				new PLongString { str = Shift(bat.from, bat.until) },
				new PLongString { str = Shift(pit.from, pit.until) },
				new PLongString { str = RotationHours >= 24 ? " * 매일 0시에 코치가 바뀝니다. 수강 중인 선수는 신청한 코치에게 끝까지 배웁니다."
					: $" * {RotationHours}시간마다({string.Join("·", Enumerable.Range(0, 24 / RotationHours).Select(k => k * RotationHours))}시) 코치가 바뀝니다. 수강 중인 선수는 신청한 코치에게 끝까지 배웁니다." },
				new PLongString { str = results },
				new PLongString { str = LessonHours.ToString(CultureInfo.InvariantCulture) },
				// The 1군's cards (app 1.0.46: picking one turns the 즉시 완료권 on, user 11-12 10:4x; older apps read five or six).
				new PLongString { str = string.Join(",", lineup.OrderBy(i => i)) }
			};
			replies.Add(info);
			Dictionary<int, Lesson> byCard = lessons.GroupBy(l => l.Idx).ToDictionary(g => g.Key, g => g.First());
			foreach ((Coach coach, DateTime from, DateTime until) in new[] { bat, pit })
			{
				bool pitcher = coach.Pitcher;
				Ma9MPacket.PKS_SC_ENTER_LESSON page = new Ma9MPacket.PKS_SC_ENTER_LESSON();
				page.director_index.n = coach.Code;
				List<TeamCard> mine = cards.Where(c => active.Contains(c.Idx) && s_catalog.Get(c.Idx)?.IsBatter == !pitcher).ToList();
				int otherSide = active.Count - mine.Count;
				page.in_lesson.array = mine.Select(c => Info(user, c, now)).ToArray();
				// 1군 players too since 2026-11-09 (they enroll with the 즉시 완료권 only, FirstTeamText).
				// Not the cards that already hold this coach's +5 (TopFromCoach, 2026-11-12).
				page.players.array = cards.Where(c => !active.Contains(c.Idx) && Eligible(coach, s_catalog.Get(c.Idx)) && !TopFromCoach(byCard.GetValueOrDefault(c.Idx), coach, c))
					.Select(c => Info(user, c, now)).ToArray();
				page.max_student.n = (byte)Math.Max(0, MaxStudents - otherSide);
				// No ball limit: the 볼제한 box shows the players in lessons out of six.
				page.cur_ball.n = (byte)active.Count;
				page.max_ball.n = MaxStudents;
				page.start_year.n = (short)from.Year;
				page.start_month.n = (byte)from.Month;
				page.start_date.n = (byte)from.Day;
				page.end_year.n = (short)until.Year;
				page.end_month.n = (byte)until.Month;
				page.end_date.n = (byte)until.Day;
				page.pay.n = BaseFee;
				page.cur_point.n = mp;
				// Every ticket always (an empty array arrives as null and the client keeps its old counts).
				int[] tickets = { Lucky, Reset, Instant };
				page.item_index.array = tickets.Select(t => new PInteger { n = t }).ToArray();
				page.item_amount.array = tickets.Select(t => new PShort { n = (short)Math.Min(short.MaxValue, InventoryHandlers.ItemAmount(user, t)) }).ToArray();
				replies.Add(page);
			}
			return replies;
		}

		private static void Enroll(GameSession session, Ma9MPacket.PKS_CS_ENROLL_LESSON packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			int[] items = (packet.use_item_index.array ?? Array.Empty<PInteger>()).Select(p => p.n).ToArray();
			byte result = TryEnroll(user, packet.index.n, items.Contains(Lucky), items.Contains(Reset), DateTime.Now, out string log, items.Contains(Instant));
			ServerLog.Info($"[{session.Id}] 특별수강 신청 card {packet.index.n}: {log}");
			if (result == InFirstTeam)
			{
				Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
				popup.Type.n = 1;
				popup.Messages.array = new[] { new PLongString { str = "1군 선수" }, new PLongString { str = FirstTeamText(InventoryHandlers.ItemAmount(user, Instant)) } };
				session.Send(popup);
			}
			Ma9MPacket.PKS_SC_ENROLL_LESSON reply = new Ma9MPacket.PKS_SC_ENROLL_LESSON();
			reply.result.n = result;
			reply.index.n = packet.index.n;
			Fill(user, packet.index.n, reply.in_lesson, out byte count);
			reply.cur_ball.n = count;
			reply.max_ball.n = MaxStudents;
			reply.cur_point.n = session.Team.Mp = s_db.AddMp(user, 0);
			session.Send(reply);
			if (result == Ok)
			{
				TopBar.Send(session);
			}
		}

		// The card's side's players in lessons (the page the client is on) and the count over both sides.
		private static void Fill(uint user, int idx, PArray<PUICardInfo> into, out byte count)
		{
			DateTime now = DateTime.Now;
			HashSet<int> active = InLesson(user);
			bool batter = s_catalog.Get(idx)?.IsBatter ?? true;
			into.array = s_db.GetCards(user).Where(c => active.Contains(c.Idx) && s_catalog.Get(c.Idx)?.IsBatter == batter).Select(c => Info(user, c, now)).ToArray();
			count = (byte)active.Count;
		}

		internal static byte TryEnroll(uint user, int idx, bool lucky, bool reset, DateTime now, out string log, bool instant = false)
		{
			Finish(user, now);
			TeamCard card = s_db.GetCards(user).FirstOrDefault(c => c.Idx == idx);
			CardInfo info = s_catalog.Get(idx);
			HashSet<int> active = InLesson(user);
			bool hasInstant = InventoryHandlers.ItemAmount(user, Instant) >= 1;
			if (instant && active.Contains(idx))
			{
				// 즉시 완료권 on a player already in a lesson: it ends now, with the coach and 행운권 it started with.
				if (!hasInstant)
				{
					log = $"refused {NoInstantTicket}";
					return NoInstantTicket;
				}
				string done = FinishNow(user, idx, now);
				log = $"{info?.Name} 즉시 완료권 (수강 중) → {done}";
				return Ok;
			}
			Coach coach = info == null ? null : OnDuty(!info.IsBatter, now).coach;
			bool hasResult = card != null && card.Educated.Any(v => v != 0);
			int fee = info == null ? 0 : Fee(info.Ball);
			bool topDone = TopFromCoach(Lessons(user).FirstOrDefault(l => l.Idx == idx), coach, card);
			byte result =
				card == null || info == null || active.Contains(idx) ? Failed :
				!Eligible(coach, info) || topDone ? NotEligible :
				!instant && Lineup(user).Contains(idx) ? InFirstTeam :
				active.Count >= MaxStudents ? Full :
				hasResult && !reset ? HasResult :
				hasResult && InventoryHandlers.ItemAmount(user, Reset) < 1 ? NoResetTicket :
				lucky && InventoryHandlers.ItemAmount(user, Lucky) < 1 ? NoLuckyTicket :
				instant && !hasInstant ? NoInstantTicket :
				s_db.AddMp(user, 0) < fee ? NoMp : Ok;
			if (result != Ok)
			{
				log = $"refused {result}";
				return result;
			}
			s_db.AddMp(user, -fee);
			if (lucky)
			{
				InventoryHandlers.AddItem(user, Lucky, -1);
			}
			using (SqliteConnection c = Open())
			{
				if (reset && hasResult)
				{
					// 초기화권: the old result is gone (a ticket sent for a card without one is not used).
					InventoryHandlers.AddItem(user, Reset, -1);
					using SqliteCommand clear = Command(c, "UPDATE team_card SET educated = $e WHERE user_code = $u AND card_idx = $i", user, idx);
					clear.Parameters.Add("$e", SqliteType.Blob).Value = new byte[6];
					clear.ExecuteNonQuery();
				}
				using SqliteCommand cmd = Command(c, @"INSERT INTO card_lesson (user_code, card_idx, acquired, coach, started, ends, lucky, state, result)
					SELECT user_code, card_idx, acquired, $c, $s, $e, $l, 0, NULL FROM team_card WHERE user_code = $u AND card_idx = $i
					ON CONFLICT(user_code, card_idx) DO UPDATE SET acquired = excluded.acquired, coach = excluded.coach, started = excluded.started,
						ends = excluded.ends, lucky = excluded.lucky, state = 0, result = NULL", user, idx);
				cmd.Parameters.AddWithValue("$c", coach.Code);
				cmd.Parameters.AddWithValue("$s", now.ToString("o", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$e", now.AddHours(LessonHours).ToString("o", CultureInfo.InvariantCulture));
				cmd.Parameters.AddWithValue("$l", lucky ? 1 : 0);
				cmd.ExecuteNonQuery();
			}
			log = $"{info.Name} → {coach.Name}, {fee:N0} MP{(lucky ? ", 행운권" : string.Empty)}{(reset && hasResult ? ", 초기화권" : string.Empty)}";
			// 선발 5경기 제한: a starter taken out for this lesson may start again when it is over.
			if (!info.IsBatter && SpBlock.Clear(user, idx))
			{
				log += ", 선발 제한 풀림";
			}
			if (instant)
			{
				log += $", 즉시 완료권 → {FinishNow(user, idx, now)}";
			}
			return Ok;
		}

		// The 즉시 완료권: one ticket, the card's lesson ends now (its result into the card) and is marked shown, since
		// the 특강 screen shows it (SpecialLecture_Main). Returns the result for the log.
		private static string FinishNow(uint user, int idx, DateTime now)
		{
			InventoryHandlers.AddItem(user, Instant, -1);
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = Command(c, "UPDATE card_lesson SET ends = $e WHERE user_code = $u AND card_idx = $i AND state = 0", user, idx))
			{
				cmd.Parameters.AddWithValue("$e", now.ToString("o", CultureInfo.InvariantCulture));
				cmd.ExecuteNonQuery();
			}
			sbyte[] stat = Finish(user, now).FirstOrDefault(d => d.idx == idx).stat;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = Command(c, "UPDATE card_lesson SET state = 2 WHERE user_code = $u AND card_idx = $i AND state = 1", user, idx))
			{
				cmd.ExecuteNonQuery();
			}
			return stat == null ? "no result" : Summary(s_catalog.Get(idx)?.IsBatter == false, stat);
		}

		private static void Drop(GameSession session, Ma9MPacket.PKS_CS_DROP_LESSON packet)
		{
			if (session.Team == null)
			{
				return;
			}
			uint user = session.Team.UserCode;
			bool dropped = TryDrop(user, packet.index.n);
			ServerLog.Info($"[{session.Id}] 특별수강 취소 card {packet.index.n}: {(dropped ? "ok" : "not in a lesson")}");
			Ma9MPacket.PKS_SC_DROP_LESSON reply = new Ma9MPacket.PKS_SC_DROP_LESSON();
			reply.result.n = (byte)(dropped ? 0 : 1);
			reply.index.n = packet.index.n;
			Fill(user, packet.index.n, reply.in_lesson, out byte count);
			reply.cur_ball.n = count;
			reply.max_ball.n = MaxStudents;
			session.Send(reply);
		}

		// 수강취소: the fee is not returned (SpecialLecture_DlgDrop_Warning); the card's old result, if a 초기화권
		// cleared it, stays cleared.
		internal static bool TryDrop(uint user, int idx)
		{
			if (!InLesson(user, idx))
			{
				return false;
			}
			using SqliteConnection c = Open();
			using SqliteCommand cmd = Command(c, "DELETE FROM card_lesson WHERE user_code = $u AND card_idx = $i AND state = 0", user, idx);
			return cmd.ExecuteNonQuery() > 0;
		}

		// The lobby (LobbyHandlers.EnterMain): the results of lessons that ended since, in the original popup.
		public static void OnLobby(GameSession session)
		{
			if (s_cs == null || session.Team == null)
			{
				return;
			}
			Ma9MPacket.PKS_SC_LESSON_RESULT reply = ResultReply(session.Team.UserCode, DateTime.Now, true);
			if (reply != null)
			{
				session.Send(reply);
			}
		}

		internal static Ma9MPacket.PKS_SC_LESSON_RESULT ResultReply(uint user, DateTime now, bool markShown)
		{
			Finish(user, now);
			List<Lesson> ended = Lessons(user).Where(l => l.State == 1 && l.Result != null).OrderBy(l => l.Ends).Take(12).ToList();
			if (ended.Count == 0)
			{
				return null;
			}
			Dictionary<int, TeamCard> cards = s_db.GetCards(user).ToDictionary(c => c.Idx);
			Ma9MPacket.PKS_SC_LESSON_RESULT reply = new Ma9MPacket.PKS_SC_LESSON_RESULT();
			reply.error.SetNoError();
			// The popup has twelve places; the ones past this count show a lock.
			reply.SlotCount.n = Math.Max(MaxStudents, ended.Count);
			reply.cards.array = ended.Where(l => cards.ContainsKey(l.Idx)).Select(l =>
			{
				PUICardInfoLessonResult r = new PUICardInfoLessonResult();
				r.ui_info = Info(user, cards[l.Idx], now);
				r.is_batter.n = (byte)(s_catalog.Get(l.Idx)?.IsBatter == false ? 0 : 1);
				r.lesson_stat.bytes = l.Result;
				return r;
			}).ToArray();
			if (markShown)
			{
				using SqliteConnection c = Open();
				foreach (Lesson l in ended)
				{
					using SqliteCommand cmd = Command(c, "UPDATE card_lesson SET state = 2 WHERE user_code = $u AND card_idx = $i AND state = 1", user, l.Idx);
					cmd.ExecuteNonQuery();
				}
			}
			return reply;
		}

		// Dev check: `Ma9MServer.dll <copy of data dir> --lessontest`, on the test account's team (autotest@ma9m.local).
		// It writes to that directory's DB (lessons, MP, items, a card's 특강 share), so never on the live data.
		public static int SelfTest(Database db, CardCatalog catalog)
		{
			int problems = 0;
			void Check(bool ok, string what)
			{
				if (!ok) problems++;
				ServerLog.Info($"[lessontest] {(ok ? "ok  " : "FAIL")} {what}");
			}

			// The coach table as the client reads it: 21 columns, unique codes and terms, stats of one or two digits.
			string[] rows = File.ReadAllLines(s_tablePath, Encoding.UTF8).Skip(1).ToArray();
			string[][] cols = rows.Select(r => r.Split('\t')).ToArray();
			Check(cols.Length == 22 && cols.All(c => c.Length == 21) && cols.Select(c => c[0]).Distinct().Count() == 22 && cols.Select(c => c[19]).Distinct().Count() == 22
				&& cols.All(c => c.Skip(3).Take(12).All(v => v.Length <= 2 && int.TryParse(v, out _))) && cols.All(c => c.All(v => !v.Contains('"'))),
				$"SpecialCourseCoach.txt: {cols.Length} coaches, 21 columns, unique codes and terms");
			Check(Batting.All(c => !c.Pitcher) && Pitching.All(c => c.Pitcher) && Batting.Concat(Pitching).All(c => c.Main != c.Sub1 && c.Main != c.Sub2 && c.Sub1 != c.Sub2),
				"11 batting + 11 pitching coaches, main and subs different");

			// Rotation: every RotationHours both sides move on; a whole round brings each coach once.
			DateTime t0 = new DateTime(2026, 10, 23, 1, 30, 0);
			var a = OnDuty(false, t0);
			var b = OnDuty(false, t0.AddHours(RotationHours));
			HashSet<short> round = Enumerable.Range(0, 11).Select(k => OnDuty(true, t0.AddHours(k * RotationHours)).coach.Code).ToHashSet();
			Check(a.from.Hour % RotationHours == 0 && a.until == a.from.AddHours(RotationHours) && a.coach != b.coach && round.Count == 11,
				$"rotation every {RotationHours} h ({a.coach.Name} {a.from:HH:mm}~{a.until:HH:mm} → {b.coach.Name}), 11 pitching coaches in a round");

			// The result odds: +4 70% / +5 30%, with the 행운권 40 / 60; subs +2.
			Random rng = new Random(11);
			int Fives(bool lucky) => Enumerable.Range(0, 100_000).Count(_ => Roll(Batting[0], lucky, rng)[Batting[0].Main] == 5);
			int plain = Fives(false), luckyFives = Fives(true);
			sbyte[] one = Roll(Batting[0], false, rng);
			Check(Math.Abs(plain / 1000.0 - 30) < 1 && Math.Abs(luckyFives / 1000.0 - 60) < 1 && one[Batting[0].Sub1] == 2 && one[Batting[0].Sub2] == 2 && one.Count(v => v != 0) == 3,
				$"+5: {plain / 1000.0:F1}% (행운권 {luckyFives / 1000.0:F1}%), subs +2");
			Check(Fee(1) == 80_000 && Fee(5) == 200_000 && Fee(8) == 400_000 && Fee(10) == 800_000, "수강료 8만 / 20만 / 40만 / 80만");

			uint user;
			using (SqliteConnection c = Open())
			using (SqliteCommand cmd = c.CreateCommand())
			{
				cmd.CommandText = "SELECT t.user_code FROM team t JOIN account a ON a.id = t.account_id WHERE a.email = 'autotest@ma9m.local' AND t.team_name <> '' ORDER BY t.user_code LIMIT 1";
				user = Convert.ToUInt32(cmd.ExecuteScalar() ?? 0L);
				cmd.CommandText = "DELETE FROM card_lesson WHERE user_code = $u";
				cmd.Parameters.AddWithValue("$u", (long)user);
				cmd.ExecuteNonQuery();
			}
			if (user == 0)
			{
				ServerLog.Info("[lessontest] no test team");
				return problems + 1;
			}
			db.AddMp(user, 100_000_000);
			DateTime now = DateTime.Now;
			var batCoach = OnDuty(false, now).coach;
			HashSet<int> lineup = Lineup(user);
			// Test cards for the batting coach on duty: missing ones are added to this copy.
			List<CardInfo> fits = catalog.All.Where(ci => Eligible(batCoach, ci)).OrderBy(ci => ci.Idx).ToList();
			HashSet<int> owned = db.GetCards(user).Select(x => x.Idx).ToHashSet();
			List<int> picks = fits.Where(ci => owned.Contains(ci.Idx) && !lineup.Contains(ci.Idx)).Select(ci => ci.Idx).Take(7).ToList();
			List<int> extra = fits.Where(ci => !owned.Contains(ci.Idx)).Select(ci => ci.Idx).Take(7 - picks.Count).ToList();
			db.AddCards(user, extra, RosterHandlers.StartContract);
			picks.AddRange(extra);
			foreach (int idx in picks)
			{
				using SqliteConnection c = Open();
				using SqliteCommand clear = Command(c, "UPDATE team_card SET educated = $e WHERE user_code = $u AND card_idx = $i", user, idx);
				clear.Parameters.Add("$e", SqliteType.Blob).Value = new byte[6];
				clear.ExecuteNonQuery();
			}

			// The two pages: the batting coach's players fit him and are off the 1군.
			List<NetworkLibrary.Packet.PKS_PACKET> pages = EnterReplies(user, now);
			Ma9MPacket.PKS_SC_ENTER_LESSON batPage = pages[1] as Ma9MPacket.PKS_SC_ENTER_LESSON, pitPage = pages[2] as Ma9MPacket.PKS_SC_ENTER_LESSON;
			Check(pages[0] is Ma9MPacket.PKS_SC_POPUP p0 && p0.Type.n == InfoPopupType && batPage?.director_index.n == batCoach.Code && pitPage?.director_index.n == OnDuty(true, now).coach.Code
				&& batPage.players.array.All(x => Eligible(batCoach, catalog.Get(x.index.n))) && batPage.players.array.Any(x => x.index.n == picks[0])
				&& lineup.Where(i => Eligible(batCoach, catalog.Get(i))).All(i => batPage.players.array.Any(x => x.index.n == i))
				&& batPage.item_index.array.Length == 3 && batPage.max_ball.n == MaxStudents,
				$"입장: info popup, {batCoach.Name} {batPage?.players.array.Length}명 후보, {OnDuty(true, now).coach.Name} {pitPage?.players.array.Length}명 후보");

			// Enrolling: the fee, six at most, a card of the 1군 refused, a card in a lesson locked everywhere.
			int mp0 = db.AddMp(user, 0);
			byte r1 = TryEnroll(user, picks[0], false, false, now, out string log1);
			Check(r1 == Ok && db.AddMp(user, 0) == mp0 - Fee(catalog.Get(picks[0]).Ball) && InLesson(user, picks[0]), $"신청 ok: {log1}");
			for (int k = 1; k < MaxStudents; k++)
			{
				TryEnroll(user, picks[k], false, false, now, out _);
			}
			Check(TryEnroll(user, picks[MaxStudents], false, false, now, out _) == Full && InLesson(user).Count == MaxStudents, "7번째는 거절 (동시 6명)");
			// A 1군 player: listed (above), refused without the 즉시 완료권 (2026-11-09; before, refused always).
			int lineupCard = lineup.FirstOrDefault(i => catalog.Get(i)?.IsBatter == true && Eligible(batCoach, catalog.Get(i)));
			Check(lineupCard == 0 || TryEnroll(user, lineupCard, false, false, now, out _) == InFirstTeam,
				$"1군 선수는 즉시 완료권 없이 신청할 수 없음 ({(lineupCard == 0 ? "no eligible 1군 batter" : catalog.Get(lineupCard).Name)})");
			Check(ContractHandlers.TryRelease(user, picks[0], out _) != null, "수강 중 방출 거절");
			Check(TryDrop(user, picks[1]) && !InLesson(user, picks[1]) && InLesson(user).Count == MaxStudents - 1, "수강취소 (수강료는 돌려주지 않음)");

			// The end: after 18 hours the result is in the card (main +4/+5, subs +2), the popup lists it once.
			List<(int idx, sbyte[] stat)> ended = Finish(user, now.AddHours(LessonHours).AddMinutes(1));
			TeamCard done = db.GetCards(user).First(x => x.Idx == picks[0]);
			Check(ended.Count == MaxStudents - 1 && done.Educated[batCoach.Main] >= 4 && done.Educated[batCoach.Sub1] == 2 && done.Educated[batCoach.Sub2] == 2 && !InLesson(user, picks[0]),
				$"18시간 뒤 끝: {catalog.Get(picks[0]).Name} [{Summary(false, done.Educated)}]");
			// The card window's 특강 line (CardInfoHandlers.Detail → client "코치(4|5)").
			Check(ResultCoach(user, picks[0]) == batCoach.Code && ResultCoach(user, picks[1]) == 0,
				$"카드 창 특강 칸: {batCoach.Name}({done.Educated.Max()}), 취소한 카드는 빈칸");
			Ma9MPacket.PKS_SC_LESSON_RESULT shown = ResultReply(user, now.AddHours(LessonHours + 1), true);
			byte[] wire = new byte[shown.GetSize() + 64];
			shown.ToBytes(wire, 0);
			Ma9MPacket.PKS_SC_LESSON_RESULT back = new Ma9MPacket.PKS_SC_LESSON_RESULT();
			back.ToType(wire, 0);
			Check(back.cards.array?.Length == MaxStudents - 1 && back.cards.array.All(x => x.lesson_stat.bytes.Length == 6 && x.is_batter.n == 1) && ResultReply(user, now.AddHours(LessonHours + 1), true) == null,
				$"결과 창 {back.cards.array?.Length}장 (packet round trip), 두 번째는 없음");

			// One result per card: again only with the 초기화권, which clears it first.
			InventoryHandlers.AddItem(user, Reset, -InventoryHandlers.ItemAmount(user, Reset));
			DateTime later = now.AddHours(LessonHours + 2);
			Coach laterCoach = OnDuty(false, later).coach;
			int again = picks.Take(MaxStudents).Where(i => i != picks[1]).FirstOrDefault(i => Eligible(laterCoach, catalog.Get(i)));
			if (again == 0)
			{
				again = picks[0];
				later = now;
				laterCoach = batCoach;
			}
			byte noTicket = TryEnroll(user, again, false, false, later, out _);
			byte noReset = TryEnroll(user, again, false, true, later, out _);
			InventoryHandlers.AddItem(user, Reset, 1);
			InventoryHandlers.AddItem(user, Lucky, 1);
			byte withReset = TryEnroll(user, again, true, true, later, out string log2);
			TeamCard cleared = db.GetCards(user).First(x => x.Idx == again);
			Check(noTicket == HasResult && noReset == NoResetTicket && withReset == Ok && cleared.Educated.All(v => v == 0) && InventoryHandlers.ItemAmount(user, Reset) == 0
				&& InventoryHandlers.ItemAmount(user, Lucky) == 0 && ResultCoach(user, again) == 0, $"결과가 있으면 초기화권 필요 → 초기화 후 다시 신청, 특강 칸은 수강이 끝날 때까지 빈칸 ({log2})");

			// Locks while in a lesson: the 1군 save and the fusion list.
			Check(FusionHandlers.LockedForLesson(user, again), "수강 중 조합 재료 잠김");

			// 즉시 완료권 (90621): refused without one; on a player in a lesson it ends now; used when enrolling, the
			// lesson ends at once (the fee still paid). The 특강 screen shows these results, so no lobby popup follows.
			InventoryHandlers.AddItem(user, Instant, -InventoryHandlers.ItemAmount(user, Instant));
			byte noInstant = TryEnroll(user, again, false, false, later, out _, true);
			InventoryHandlers.AddItem(user, Instant, 2);
			byte finished = TryEnroll(user, again, false, false, later, out string log3, true);
			TeamCard now1 = db.GetCards(user).First(x => x.Idx == again);
			Check(noInstant == NoInstantTicket && finished == Ok && !InLesson(user, again) && now1.Educated[laterCoach.Main] >= 4 && InventoryHandlers.ItemAmount(user, Instant) == 1
				&& ResultCoach(user, again) == laterCoach.Code, $"수강 중 즉시 완료: {log3}");
			int mp1 = db.AddMp(user, 0);
			byte instantEnroll = TryEnroll(user, picks[1], false, false, now, out string log4, true);
			TeamCard now2 = db.GetCards(user).First(x => x.Idx == picks[1]);
			Check(instantEnroll == Ok && !InLesson(user, picks[1]) && now2.Educated[batCoach.Main] >= 4 && InventoryHandlers.ItemAmount(user, Instant) == 0
				&& db.AddMp(user, 0) == mp1 - Fee(catalog.Get(picks[1]).Ball), $"신청하면서 즉시 완료: {log4}");
			Ma9MPacket.PKS_SC_LESSON_RESULT afterInstant = ResultReply(user, later.AddMinutes(1), false);
			Check(afterInstant == null || afterInstant.cards.array.All(x => x.ui_info.index.n != again && x.ui_info.index.n != picks[1]),
				"즉시 완료한 결과는 로비 결과 창에 다시 나오지 않음");
			// A 1군 player with the 즉시 완료권 (2026-11-09): the lesson ends at once and he never leaves the 1군.
			if (lineupCard != 0)
			{
				using (SqliteConnection c = Open())
				using (SqliteCommand clear = Command(c, "UPDATE team_card SET educated = $e WHERE user_code = $u AND card_idx = $i", user, lineupCard))
				{
					clear.Parameters.Add("$e", SqliteType.Blob).Value = new byte[6];
					clear.ExecuteNonQuery();
				}
				InventoryHandlers.AddItem(user, Instant, 1);
				Lineup before1 = db.GetLineup(user);
				byte firstTeam = TryEnroll(user, lineupCard, false, false, now, out string log5, true);
				TeamCard now3 = db.GetCards(user).First(x => x.Idx == lineupCard);
				Check(firstTeam == Ok && !InLesson(user, lineupCard) && now3.Educated[batCoach.Main] >= 4 && db.GetLineup(user).Serialize() == before1.Serialize()
					&& InventoryHandlers.ItemAmount(user, Instant) == 0, $"1군 선수 즉시 완료: {log5}, 1군 그대로");
			}
			foreach (int held in new[] { 0, 99 })
			{
				string[] firstTeamLines = FirstTeamText(held).Split('\n');
				Check(firstTeamLines.Length <= 5 && firstTeamLines.All(l => PopupText.Px(l) <= PopupText.Limit),
					$"1군 안내 (즉시 완료권 {held}장) {firstTeamLines.Length}줄, 가장 넓은 {firstTeamLines.Max(PopupText.Px):F0} px / {PopupText.Limit:F0}");
			}
			List<NetworkLibrary.Packet.PKS_PACKET> pages2 = EnterReplies(user, now);
			Check(pages2[1] is Ma9MPacket.PKS_SC_ENTER_LESSON bp && bp.item_index.array.Length == 3 && bp.item_index.array.Any(x => x.n == Instant)
				&& LessonHandlers.Items().Any(d => d.Index == Instant && d.File == "lesson_instant"), "입장 패킷에 즉시 완료권 수량, 아이템 목록에 즉시 완료권");
			// The 1군 list in the info popup (app 1.0.46 turns the 즉시 완료권 on for those cards; 2026-11-12).
			string sent = (pages2[0] as Ma9MPacket.PKS_SC_POPUP)?.Messages.array is PLongString[] m6 && m6.Length > 6 ? m6[6].str : null;
			Check(sent == string.Join(",", Lineup(user).OrderBy(i => i)) && sent.Length > 0, $"정보 팝업 7번째 칸 = 1군 {Lineup(user).Count}장");
			// A card holding this coach's +5 is not his player any more; with +4 he is listed again (2026-11-12).
			void SetResult(int idx, Coach coach, sbyte main)
			{
				sbyte[] stat = new sbyte[6];
				stat[coach.Main] = main;
				stat[coach.Sub1] += 2;
				stat[coach.Sub2] += 2;
				byte[] blob = stat.Select(v => (byte)v).ToArray();
				using SqliteConnection c = Open();
				using (SqliteCommand card = Command(c, "UPDATE team_card SET educated = $e WHERE user_code = $u AND card_idx = $i", user, idx))
				{
					card.Parameters.Add("$e", SqliteType.Blob).Value = blob;
					card.ExecuteNonQuery();
				}
				using SqliteCommand row = Command(c, "UPDATE card_lesson SET coach = $c, state = 2, result = $r WHERE user_code = $u AND card_idx = $i", user, idx);
				row.Parameters.AddWithValue("$c", coach.Code);
				row.Parameters.Add("$r", SqliteType.Blob).Value = blob;
				row.ExecuteNonQuery();
			}
			bool Listed(int idx) => EnterReplies(user, now)[1] is Ma9MPacket.PKS_SC_ENTER_LESSON pg && pg.players.array.Any(x => x.index.n == idx);
			SetResult(picks[1], batCoach, 5);
			InventoryHandlers.AddItem(user, Reset, 1);
			bool hiddenAt5 = !Listed(picks[1]);
			byte refusedAt5 = TryEnroll(user, picks[1], false, true, now, out _);
			SetResult(picks[1], batCoach, 4);
			bool listedAt4 = Listed(picks[1]);
			Coach otherBat = Batting.FirstOrDefault(b => b != batCoach && Eligible(b, catalog.Get(picks[1])));
			bool otherCoachFine = otherBat == null || TopFromCoach(new Lesson { Idx = picks[1], Coach = batCoach.Code, State = 2, Result = new byte[] { 5, 5, 5, 5, 5, 5 } }, otherBat, db.GetCards(user).First(x => x.Idx == picks[1])) == false;
			InventoryHandlers.AddItem(user, Reset, -InventoryHandlers.ItemAmount(user, Reset));
			Check(hiddenAt5 && refusedAt5 == NotEligible && listedAt4 && otherCoachFine,
				$"{batCoach.Name} +5를 받은 {catalog.Get(picks[1]).Name}: 그 코치 목록에서 빠짐·신청 거절({refusedAt5}), +4면 다시 나옴, 다른 코치({otherBat?.Name ?? "없음"})에게는 그대로");
			ServerLog.Info($"[lessontest] {(problems == 0 ? "all ok" : problems + " problem(s)")}");
			return problems == 0 ? 0 : 1;
		}
	}
}
