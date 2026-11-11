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
	public static class AdviceHandlers
	{
		public const byte AskType = 10, DoneType = 11, AckApply = 10, AckCancel = 11;

		public const string Prefix = "ma9m:";

		private static readonly Dictionary<uint, (DateTime at, string title, Func<string> apply)> s_pending = new Dictionary<uint, (DateTime, string, Func<string>)>();

		// LobbyHandlers.SceneTutorial: true when the request was one of ours.
		public static bool TryHandle(GameSession session, string scene)
		{
			if (scene == null || !scene.StartsWith(Prefix, StringComparison.Ordinal))
			{
				return false;
			}
			uint user = session.Team.UserCode;
			string what = scene.Substring(Prefix.Length);
			DateTime t0 = DateTime.UtcNow;
			try
			{
				if (what == "traitadvice")
				{
					Traits(session, user);
				}
				else if (what.StartsWith("autolineup", StringComparison.Ordinal))
				{
					Lineup(session, user, what.EndsWith(":keep", StringComparison.Ordinal));
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
			Say(session, DoneType, p.title, done);
		}

		private static void Say(GameSession session, byte type, string title, string text, string ok = null)
		{
			Ma9MPacket.PKS_SC_POPUP popup = new Ma9MPacket.PKS_SC_POPUP();
			popup.Type.n = type;
			List<PLongString> m = new List<PLongString> { new PLongString { str = title }, new PLongString { str = text } };
			if (ok != null)
			{
				m.Add(new PLongString { str = ok });
			}
			popup.Messages.array = m.ToArray();
			session.Send(popup);
		}

		// Entries joined with " · ", a new line every `per` (the popup's message does not wrap by itself).
		private static string Rows(IEnumerable<string> items, int per)
		{
			List<string> list = items.ToList();
			return string.Concat(list.Select((x, i) => (i == 0 ? string.Empty : i % per == 0 ? "\n" : " · ") + x));
		}

		private static void Traits(GameSession session, uint user)
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
			string Names(char type) => string.Join(" · ", TeamPropertyHandlers.Traits.Where(t => t.Type == type && advice.ContainsKey(t.Index)).Select(t => $"{t.Name} {advice[t.Index]}"));
			string bat = Names('H'), pit = Names('P');
			string text = $"1군 선수와 같은 리그 상대들로 계산한 추천입니다(CP {advice.Values.Sum(v => v)}/{a.Cp}).\n"
				+ $"지금 배분보다 승률 약 {gain:+0.0;-0.0;0.0}%p\n"
				+ (bat.Length > 0 ? "타자: " + bat + "\n" : string.Empty)
				+ (pit.Length > 0 ? "투수: " + pit : string.Empty);
			s_pending[user] = (DateTime.UtcNow, Title, () =>
			{
				string problem = TeamPropertyHandlers.ApplyPoints(user, advice, "추천 배분");
				return problem != null ? "!" + problem : "추천대로 특성을 배분했습니다.\n특성관리 화면을 다시 엽니다.";
			});
			Say(session, AskType, Title, text.TrimEnd('\n'), "적용");
			ServerLog.Info($"[{session.Id}] trait advice: {gain:+0.0;-0.0}%p, {string.Join(" ", advice.Select(p => $"{p.Key}:{p.Value}"))}");
		}

		private static string CardName(int idx)
		{
			CardInfo c = RosterHandlers.Catalog?.Get(idx);
			return c == null ? idx.ToString() : c.IsLegend ? c.Name + "(레전드)" : $"'{c.Year % 100:00} {c.Name}";
		}

		private static void Lineup(GameSession session, uint user, bool keepColour)
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
				Say(session, 1, title, "지금 1군이 가장 강합니다.\n바꿀 선수가 없습니다.");
				return;
			}
			string List(List<int> ids) => ids.Count == 0 ? "없음" : Rows(ids.Take(8).Select(CardName), 4) + (ids.Count > 8 ? $" 외 {ids.Count - 8}명" : string.Empty);
			// TeamValue counts a big change about twice as strong as the games show (engine session, 11-11): half of it.
			double gain = r.Gain * 100 / 2;
			string text = $"볼 {r.Balls}/{r.Cap}" + (comes.Count == 0 ? " · 같은 선수, 자리·타순만 바뀜" : string.Empty)
				+ (gain > 0.05 ? $" · 승률 약 +{gain:0.0}%p" : string.Empty) + "\n"
				+ (comes.Count > 0 ? "들어옴: " + List(comes) + "\n빠짐: " + List(goes) : string.Empty)
				+ (r.StartersOut.Count > 0 ? $"\n선발에서 빠지는 투수는 {SpBlock.Games}경기 동안 다시 선발 불가" : string.Empty);
			Lineup chosen = r.Lineup;
			s_pending[user] = (DateTime.UtcNow, title, () =>
			{
				string problem = RosterHandlers.SaveWhole(user, chosen, out string colours);
				return problem != null ? "!" + problem : "자동 편성을 적용했습니다." + (colours != null ? "\n" + colours : string.Empty);
			});
			Say(session, AskType, title, text.TrimEnd('\n'), "적용");
			ServerLog.Info($"[{session.Id}] auto lineup{(keepColour ? " keep" : string.Empty)}: {r.Balls}/{r.Cap}, +{r.Gain * 100:0.0}%p (shown half), in {comes.Count}, out {goes.Count}, {r.Evaluations} evals, start {r.Start}");
		}
	}
}
