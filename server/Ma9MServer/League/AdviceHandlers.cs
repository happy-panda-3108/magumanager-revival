using System;
using System.Collections.Generic;
using System.Linq;
using Ma9MPackets;
using NetworkLibrary.PacketType;

namespace Ma9MServer
{
	// 특성 추천 배분 · 자동 편성 (user 11-11 18:4x via the engine session, 10번 "볼 상한 안에서 가장 강한 라인업을 짜는 기능 … 일단
	// 진행해봅시다", 11번 "찍었을 때 가장 승률이 높게 나오는 쪽으로 자동으로 찍어주는 기능"; 21:2x "특성을 가장 승률 높은 조합으로 자동 배분해주는
	// 기능이 생길 때 같이 반영합시다" — MA9M_TRAIT_V2 goes on with this). The calculations are the engine session's
	// (LeagueService.AdviseTraits, AutoLineup); the packets and screens the systems session's (11-11 22시):
	//   app → REQ_SCENE_TUTORIAL scene_name "ma9m:traitadvice" | "ma9m:autolineup:keep" | "ma9m:autolineup:free"
	//   server: works it out on the loop (1-6 s) → POPUP type 10 [title, text, ok label]: the app's 적용 sends POPUP_ACK 10,
	//   취소 11 (types 1/2 send 0/1, so an unrelated OK never applies this) → applied, POPUP type 11 [title, text]; the app
	//   reloads its screen on that OK. A new request replaces the pending one; it expires after 10 minutes.
	// 2026-11-12 (user 01:0x, phone 1.0.45: "특강화면에서 무슨 버그가 나고 있는 것 같음 팝업이 생겼음"): type 10 is also the 특강 info
	// popup (LessonHandlers.InfoPopupType, since 10-22), and the 1.0.45 app shows every type 10 as this window. So the app from
	// 1.0.46 asks "ma9m:v2:…" and gets types 20 / 21 of their own; "ma9m:…" (1.0.45) still gets 10 / 11. Entering the 특강
	// screen drops an old-style pending advice (DropOld), so that window's 적용 (ACK 10) has nothing to apply.
	// Popup types in use: 0 tutorial lines, 1 / 2 original OK / OK-cancel, 9 시상식, 10 특강 info (+ advice for 1.0.45), 20 / 21 advice.
	// 2026-11-12 morning (AI testers' night run): type 20 is [title, text, ok, page…]: the 자동 편성 summary, then pages with every
	// slot that changes (the 1.0.46 app's 자세히 pages through them); 추천 배분 also lists what it takes away. 1.0.45 keeps the old text.
	public static class AdviceHandlers
	{
		public const byte AskType = 10, DoneType = 11, AckApply = 10, AckCancel = 11;

		public const byte AskType2 = 20, DoneType2 = 21;

		public const string Prefix = "ma9m:", V2 = "v2:";

		private static readonly Dictionary<uint, (DateTime at, string title, Func<string> apply, bool v2)> s_pending = new Dictionary<uint, (DateTime, string, Func<string>, bool)>();

		// LessonHandlers.Enter (see above): the 1.0.45 app's copy of the 특강 info must not apply a waiting advice.
		public static void DropOld(uint user)
		{
			if (s_pending.TryGetValue(user, out var p) && !p.v2)
			{
				s_pending.Remove(user);
			}
		}

		// LobbyHandlers.SceneTutorial: true when the request was one of ours.
		public static bool TryHandle(GameSession session, string scene)
		{
			if (scene == null || !scene.StartsWith(Prefix, StringComparison.Ordinal))
			{
				return false;
			}
			uint user = session.Team.UserCode;
			string what = scene.Substring(Prefix.Length);
			bool v2 = what.StartsWith(V2, StringComparison.Ordinal);
			if (v2)
			{
				what = what.Substring(V2.Length);
			}
			DateTime t0 = DateTime.UtcNow;
			try
			{
				if (what == "traitadvice")
				{
					Traits(session, user, v2);
				}
				else if (what.StartsWith("autolineup", StringComparison.Ordinal))
				{
					Lineup(session, user, what.EndsWith(":keep", StringComparison.Ordinal), v2);
				}
				else
				{
					Say(session, 1, "알림", "알 수 없는 요청입니다.");
				}
			}
			catch (Exception e)
			{
				ServerLog.Warn($"[{session.Id}] {what} failed: {e.GetType().Name}: {e.Message}");
				Say(session, 1, "알림", "계산하지 못했습니다. 잠시 뒤 다시 해 주세요.");
			}
			ServerLog.Info($"[{session.Id}] {what}: {(DateTime.UtcNow - t0).TotalMilliseconds:F0} ms");
			return true;
		}

		// LobbyHandlers' POPUP_ACK.
		public static void Ack(GameSession session, Ma9MPacket.PKS_CS_POPUP_ACK packet)
		{
			uint user = session.Team?.UserCode ?? 0;
			if (packet.Ack.n != AckApply && packet.Ack.n != AckCancel || !s_pending.TryGetValue(user, out var p))
			{
				return;
			}
			s_pending.Remove(user);
			if (packet.Ack.n == AckCancel)
			{
				ServerLog.Info($"[{session.Id}] {p.title}: cancelled");
				return;
			}
			if (DateTime.UtcNow - p.at > TimeSpan.FromMinutes(10))
			{
				Say(session, 1, p.title, "시간이 지나 다시 계산해야 합니다.\n버튼을 한 번 더 눌러 주세요.");
				return;
			}
			string done = p.apply();
			if (done.StartsWith("!", StringComparison.Ordinal))
			{
				Say(session, 1, p.title, done.Substring(1));
				return;
			}
			Say(session, p.v2 ? DoneType2 : DoneType, p.title, done);
		}

		// more: further pages after [title, text, ok] (1.0.46 app: a 자세히 button pages through them; only for v2 requests).
		private static void Say(GameSession session, byte type, string title, string text, string ok = null, IEnumerable<string> more = null)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = type;
			List<PLongString> m = new List<PLongString> { new PLongString { str = title }, new PLongString { str = text } };
			if (ok != null)
			{
				m.Add(new PLongString { str = ok });
				m.AddRange((more ?? Enumerable.Empty<string>()).Select(p => new PLongString { str = p }));
			}
			popup.Messages.array = m.ToArray();
			session.Send(popup);
		}

		// The items after head, " · " between them, a new line before one that would pass the box (PopupText).
		private static List<string> Wrapped(string head, IEnumerable<string> items)
		{
			List<string> list = items.ToList();
			if (list.Count == 0)
			{
				return new List<string>();
			}
			list[0] = head + list[0];
			return PopupText.Wrap(list, " · ");
		}

		// Pages of at most PageLines lines (the window grows to the longest).
		// 12 → 10 on 2026-11-12 (AI testers NC·kt·LG: the 자세히 투수 page's first line ran into the window's title).
		private const int PageLines = 10;

		private static IEnumerable<string> Paged(string head, List<string> lines)
		{
			for (int i = 0; i < lines.Count; i += PageLines - 1)
			{
				yield return (i == 0 ? head : head + " (계속)") + "\n" + string.Join("\n", lines.Skip(i).Take(PageLines - 1));
			}
		}

		// Entries joined with " · ", a new line every `per` (the popup's message does not wrap by itself).
		private static string Rows(IEnumerable<string> items, int per)
		{
			List<string> list = items.ToList();
			return string.Concat(list.Select((x, i) => (i == 0 ? string.Empty : i % per == 0 ? "\n" : " · ") + x));
		}

		private static void Traits(GameSession session, uint user, bool v2)
		{
			const string Title = "특성 추천 배분";
			LeagueService.TraitAdvice a = LeagueService.AdviseTraits(user);
			if (a == null)
			{
				Say(session, 1, Title, "리그에 들어간 뒤에 계산할 수 있습니다.");
				return;
			}
			Dictionary<byte, byte> advice = a.Points.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
			Dictionary<byte, byte> saved = a.Saved.Where(p => p.Value > 0).ToDictionary(p => p.Key, p => p.Value);
			if (advice.Count == saved.Count && advice.All(p => saved.TryGetValue(p.Key, out byte v) && v == p.Value))
			{
				Say(session, 1, Title, "지금 배분이 추천 배분과 같습니다.");
				return;
			}
			double gain = (a.Win - a.SavedWin) * 100;
			// The advice by tree, then what it takes away from the points of now (AI testers 11-12 night, LG 67 · NC: "창에 새 배분만
			// 있고 빠지는 것이 없다"), each wrapped to the box.
			IEnumerable<string> Names(char type, Dictionary<byte, byte> points) => TeamPropertyHandlers.Traits.Where(t => t.Type == type && points.ContainsKey(t.Index)).Select(t => $"{t.Name} {points[t.Index]}");
			Dictionary<byte, byte> removed = saved.Where(p => p.Value > advice.GetValueOrDefault(p.Key)).ToDictionary(p => p.Key, p => (byte)(p.Value - advice.GetValueOrDefault(p.Key)));
			List<string> lines = new List<string>
			{
				$"1군 선수와 같은 리그 상대들로 계산한 추천입니다(CP {advice.Values.Sum(v => v)}/{a.Cp}).",
				$"지금 배분보다 승률 약 {gain:+0.0;-0.0;0.0}%p"
			};
			lines.AddRange(Wrapped("타자: ", Names('H', advice)));
			lines.AddRange(Wrapped("투수: ", Names('P', advice)));
			lines.AddRange(Wrapped("빠짐(타자): ", Names('H', removed)));
			lines.AddRange(Wrapped("빠짐(투수): ", Names('P', removed)));
			string text = string.Join("\n", lines);
			s_pending[user] = (DateTime.UtcNow, Title, () =>
			{
				string problem = TeamPropertyHandlers.ApplyPoints(user, advice, "추천 배분");
				return problem != null ? "!" + problem : "추천대로 특성을 배분했습니다.\n특성관리 화면을 다시 엽니다.";
			}, v2);
			Say(session, v2 ? AskType2 : AskType, Title, text.TrimEnd('\n'), "적용");
			ServerLog.Info($"[{session.Id}] trait advice: {gain:+0.0;-0.0}%p, {string.Join(" ", advice.Select(p => $"{p.Key}:{p.Value}"))}");
		}

		private static string CardName(int idx)
		{
			CardInfo c = RosterHandlers.Catalog?.Get(idx);
			return c == null ? idx.ToString() : c.IsLegend ? c.Name + "(레전드)" : $"'{c.Year % 100:00} {c.Name}";
		}

		private static void Lineup(GameSession session, uint user, bool keepColour, bool v2)
		{
			string title = "자동 편성" + (keepColour ? " (팀컬러 유지)" : string.Empty);
			LeagueService.AutoLineupResult r = LeagueService.AutoLineup(user, keepColour);
			if (r == null || r.Lineup == null)
			{
				Say(session, 1, title, "리그에 들어간 뒤에 계산할 수 있습니다.");
				return;
			}
			IEnumerable<int> Cards(Lineup l) => l.Batters.Concat(l.Pitchers).Where(i => i != 0);
			HashSet<int> now = Cards(r.Current).ToHashSet(), next = Cards(r.Lineup).ToHashSet();
			List<int> comes = next.Where(i => !now.Contains(i)).ToList(), goes = now.Where(i => !next.Contains(i)).ToList();
			if (comes.Count == 0 && r.Lineup.Serialize() == r.Current.Serialize())
			{
				Say(session, 1, title, "지금 1군이 가장 강합니다.\n바꿀 선수가 없습니다." + (r.Locked.Count > 0 ? $"\n(선수보호 {r.Locked.Count}명은 고정)" : string.Empty));
				return;
			}
			string List(List<int> ids) => ids.Count == 0 ? "없음" : Rows(ids.Take(8).Select(CardName), 4) + (ids.Count > 8 ? $" 외 {ids.Count - 8}명" : string.Empty);
			// TeamValue counts a big change two to three times as strong as the games show (engine session, 11-11 and 11-12), so
			// since 11-12 the window shows AutoLineup's own games (SimNow → SimAdvice, SimKept; WinItems) instead of half of it.
			double gain = r.Gain * 100 / 2;
			string text = $"볼 {r.Balls}/{r.Cap}" + (comes.Count == 0 ? " · 같은 선수, 자리·타순만 바뀜" : string.Empty)
				+ (HasSim(r) ? $" · 예상 승률 {WinPct(r.SimNow)} → {WinPct(r.SimAdvice)}" : string.Empty) + "\n"
				+ (comes.Count > 0 ? "들어옴: " + List(comes) + "\n빠짐: " + List(goes) : string.Empty)
				+ (r.StartersOut.Count > 0 ? $"\n선발에서 빠지는 투수는 {SpBlock.Games}경기 동안 다시 선발 불가" : string.Empty);
			List<string> more = null;
			if (v2)
			{
				(text, more) = LineupPages(user, r, comes, goes, gain);
			}
			Lineup chosen = r.Lineup;
			s_pending[user] = (DateTime.UtcNow, title, () =>
			{
				string problem = RosterHandlers.SaveWhole(user, chosen, out string colours);
				return problem != null ? "!" + problem : "자동 편성을 적용했습니다." + (colours != null ? "\n" + colours : string.Empty);
			}, v2);
			Say(session, v2 ? AskType2 : AskType, title, text.TrimEnd('\n'), "적용", more);
			ServerLog.Info($"[{session.Id}] auto lineup{(keepColour ? " keep" : string.Empty)}: {r.Balls}/{r.Cap}, TeamValue +{r.Gain * 100:0.0}%p, games {r.SimNow:P1} → {r.SimAdvice:P1} ±{r.SimSe:P1} (clear {r.SimClear}), kept {r.SimKept:P1}, in {comes.Count}, out {goes.Count}, {r.Evaluations} evals, start {r.Start}");
		}

		// AutoLineup's paired games (engine session 11-12): SimGames 0 (the advice is the 1군 of now) leaves them NaN.
		private static bool HasSim(LeagueService.AutoLineupResult r) => r.SimGames > 0 && !double.IsNaN(r.SimNow) && !double.IsNaN(r.SimAdvice);

		private static string WinPct(double p) => $"{Math.Round(p * 100):0}%";

		// "예상 승률 84% → 94%", "(지금과 비슷)" when the gain is within 2 standard errors, "팀컬러를 지키면 87%" when 자유롭게 drops a
		// team colour the 1군 keeps now (engine 11-12: whole percents, the error is ±1-2%p).
		private static List<string> WinItems(LeagueService.AutoLineupResult r)
		{
			List<string> items = new List<string>();
			if (!HasSim(r))
			{
				return items;
			}
			items.Add($"예상 승률 {WinPct(r.SimNow)} → {WinPct(r.SimAdvice)}" + (r.SimClear ? string.Empty : " (지금과 비슷)"));
			if (!double.IsNaN(r.SimKept))
			{
				items.Add($"팀컬러를 지키면 {WinPct(r.SimKept)}");
			}
			return items;
		}

		private static readonly string[] FieldNames = { "C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH" };

		private static readonly string[] PitcherSlotNames = { "1선발", "2선발", "3선발", "4선발", "5선발", "중계1", "중계2", "중계3", "중계4", "셋업", "마무리" };

		private static int AtField(Lineup l, int pos)
		{
			int order = l.Fielders[pos];
			return order >= 1 && order <= 9 ? l.Batters[order - 1] : 0;
		}

		private static string Slot(string name, int was, int now) => $"{name} {(was == 0 ? "없음" : CardName(was))} → {(now == 0 ? "없음" : CardName(now))}";

		// The 1.0.46 window (AI testers 11-12 night, all four: "외 N명"으로 줄여 다 못 봄, 보직·선발 변화가 안 보임, 팀컬러가 계산에
		// 들어갔는지 모름, 계약·트레이닝을 한 선수를 말없이 뺌): a summary page, then 자세히 pages with every slot that changes.
		private static (string summary, List<string> pages) LineupPages(uint user, LeagueService.AutoLineupResult r, List<int> comes, List<int> goes, double gain)
		{
			Lineup was = r.Current, now = r.Lineup;
			HashSet<int> batters = was.Batters.Concat(now.Batters).Where(i => i != 0).ToHashSet();
			List<string> s = Wrapped(string.Empty, new[] { $"볼 {r.Balls}/{r.Cap}" }.Concat(WinItems(r)));
			s.Add(comes.Count == 0 ? "같은 선수로 자리·타순만 바뀝니다."
				: $"바뀌는 선수 {comes.Count}명 (타자 {comes.Count(batters.Contains)} · 투수 {comes.Count(i => !batters.Contains(i))})");
			List<int> starts = Enumerable.Range(0, 5).Where(k => was.Pitchers[k] != now.Pitchers[k]).ToList();
			if (starts.Count <= 2)
			{
				s.AddRange(Wrapped("선발: ", starts.Select(k => Slot(PitcherSlotNames[k], was.Pitchers[k], now.Pitchers[k]))));
			}
			else
			{
				s.Add($"선발 {starts.Count}자리가 바뀝니다(이름은 자세히)");
			}
			List<string> pen = new[] { 9, 10 }.Where(k => was.Pitchers[k] != now.Pitchers[k]).Select(k => Slot(PitcherSlotNames[k], was.Pitchers[k], now.Pitchers[k])).ToList();
			int middle = Enumerable.Range(5, 4).Count(k => was.Pitchers[k] != now.Pitchers[k]);
			if (middle > 0)
			{
				pen.Add($"중계 {middle}자리");
			}
			s.AddRange(Wrapped("불펜: ", pen));
			// The team colour each 1군 gets from the 자동 등록 (AutoLineup counts it in the value).
			List<string> colours = new List<string>();
			for (int k = 0; k < 3; k++)
			{
				int old = r.CurrentColours[k], got = r.Colours[k];
				if (old != got)
				{
					colours.Add(old != 0 && got != 0 ? $"{TeamColors.ShortName(old)} → {TeamColors.ShortName(got)}" : old != 0 ? $"{TeamColors.ShortName(old)} 해제" : $"{TeamColors.ShortName(got)} 등록");
				}
			}
			// 2026-11-12 (AI testers): NC with 팀컬러 유지 saw an entry change ("유지" keeps the registered ones, a stronger one may take
			// their place); kt with 자유롭게 expected another team colour where the best is none.
			bool keep = r.Kept == null && r.KeepsColour && r.CurrentColours.Any(c => c != 0);
			string head = colours.Count > 0 && keep ? "더 센 팀컬러로: " : "팀컬러: ";
			if (colours.Count > 0 && r.Colours.All(c => c == 0))
			{
				colours.Add("(팀컬러 없이 가장 강함)");
			}
			s.AddRange(colours.Count == 0 ? new List<string> { "팀컬러: 지금과 같음" } : Wrapped(head, colours));
			// Cards leaving with renewals, training or a 특강 on them.
			Dictionary<int, TeamCard> owned = RosterHandlers.Db.GetCards(user).ToDictionary(c => c.Idx);
			bool Developed(int idx) => owned.TryGetValue(idx, out TeamCard c) && (c.ContractCount > 0 || c.Training.Any(v => v != 0) || c.Educated.Any(v => v != 0));
			List<string> developed = goes.Where(Developed).Select(CardName).ToList();
			List<string> devLines = Wrapped("계약·트레이닝·특강을 한 선수가 빠짐: ", developed);
			if (devLines.Count > 2)
			{
				// Two lines, the rest counted.
				int shown = developed.Count;
				while (shown > 1 && (devLines = Wrapped("계약·트레이닝·특강을 한 선수가 빠짐: ", developed.Take(shown).Append($"외 {developed.Count - shown}명"))).Count > 2)
				{
					shown--;
				}
			}
			s.AddRange(devLines);
			if (r.StartersOut.Count > 0)
			{
				s.Add($"선발에서 빠지는 투수는 {SpBlock.Games}경기 동안 다시 선발 불가");
			}
			// 선수 고정 (user 11-12 10:4x): the 1군's 선수보호 cards stay where they are.
			if (r.Locked.Count > 0)
			{
				s.Add($"선수보호 {r.Locked.Count}명은 그 자리 그대로 둡니다");
			}
			// 2026-11-13 (user 16:5x 결정 10, engine: AutoLineup plays every condition as 보통): the line says so.
			s.Add("팀컬러·계약·트레이닝·특강을 넣어 계산(컨디션은 보통으로)");

			List<string> pages = new List<string>();
			List<string> bat = Enumerable.Range(0, Ma9MServer.Lineup.FielderSlots).Where(p => AtField(was, p) != AtField(now, p)).Select(p => Slot(FieldNames[p], AtField(was, p), AtField(now, p))).ToList();
			HashSet<int> benchWas = was.Batters.Skip(9).Where(i => i != 0).ToHashSet(), benchNow = now.Batters.Skip(9).Where(i => i != 0).ToHashSet();
			bat.AddRange(Wrapped("벤치 들어옴: ", benchNow.Where(i => !benchWas.Contains(i)).Select(CardName)));
			bat.AddRange(Wrapped("벤치 빠짐: ", benchWas.Where(i => !benchNow.Contains(i)).Select(CardName)));
			if (!was.Batters.Take(9).SequenceEqual(now.Batters.Take(9)))
			{
				bat.Add("타순도 바뀝니다");
			}
			if (bat.Count > 0)
			{
				pages.AddRange(Paged("타자 (지금 → 바뀜)", bat));
			}
			List<string> pit = Enumerable.Range(0, Ma9MServer.Lineup.PitcherSlots).Where(k => was.Pitchers[k] != now.Pitchers[k]).Select(k => Slot(PitcherSlotNames[k], was.Pitchers[k], now.Pitchers[k])).ToList();
			if (pit.Count > 0)
			{
				pages.AddRange(Paged("투수 (지금 → 바뀜)", pit));
			}
			// The summary holds PageLines too (2026-11-12): the last lines past it go to a page of their own at the end.
			if (s.Count > PageLines)
			{
				List<string> rest = s.Skip(PageLines - 1).ToList();
				s = s.Take(PageLines - 1).Append("(나머지는 자세히 마지막 쪽)").ToList();
				pages.AddRange(Paged("참고", rest));
			}
			return (string.Join("\n", s), pages);
		}
	}
}
