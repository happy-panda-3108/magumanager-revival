using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ma9MServer
{
	// ePos values used by the card tables.
	public enum CardPos
	{
		P = 0, C = 1, B1 = 2, B2 = 3, B3 = 4, SS = 5, LF = 6, CF = 7, RF = 8, DH = 10, SP = 12, RP = 13, SU = 14, CP = 15
	}

	public sealed class CardInfo
	{
		public int Idx;

		public bool IsBatter;

		public int Year;

		public int Sidx;

		public string Name;

		// 1 right, 2 left, 3 switch (batters).
		public int ThrowHand, HitHand;

		// Pitchers: 1 overhand, 2 sidearm, 3 submarine.
		public int ThrowType = 1;

		public int TeamCode;

		public int Ball;

		public CardPos MainPosition;

		public int Grade;

		public int Age;

		// 용병 (table column foreigner; 특별수강's coach conditions, LessonHandlers).
		public bool Foreigner;

		// 선호타순 / role as the client's 선수분석 graph reads it (TRRow_*Card.lineup). Batters (table
		// column lineup): 0 상위 (1-2번), 1 클린업 (3-5번), 2 하위 (6-9번), 3 밸런스. Pitchers by main
		// position: 0 선발, 1 중계, 2 셋업, 3 마무리.
		public int PreferredOrder;

		// Batters: con, pow, spd, eye, mental; pitchers: health, spd, cont, technic, breakball, mental.
		public int[] Stats;

		// Batters only: defence per eROSTER_BATTER_POS C..RF; 20 means cannot field there.
		public int[] Defence;

		public int Overall => Stats.Sum() / Stats.Length;

		// 레전드 카드 (user, 2026-11-08; cards by the engine session): grade 5, pitchers idx 800101-800199, batters 800201-800299. Their
		// team and year are the player's best club and season (for the uniform), so the 1군 limit (RosterHandlers), the 팀컬러
		// (any season of the club's lineage, TeamColors) and the random draws (CardCatalog.Drawable) go by this instead.
		public bool IsLegend => Grade == 5 || Idx >= 800101 && Idx <= 800299;

		// FA 선수카드 (user, 2026-11-15; rows by the engine session's tools/scripts/fa_cards.py): a copy of a 8- or 9-ball 레어 card in
		// the FA team (teamcode 250, the original client's eTeamName.FA), idx FaIdxBase + the source idx, the same year, sidx and
		// stats, so it plays as its source (GameData.Row goes by sidx and year). At most one in the 1군; it counts for any
		// 팀컬러 group; random draws never give it (Drawable). HomeTeam = the source card's club (coaches' lineage and the like).
		// Two since 2026-11-17 (user 07:3x: "1군에도 FA선수를 2명 쓸 수 있다는 얘기야"; 07:4x "영입은 4명까지 가능하고 1군에 사용가능한
		// 인원을 2명으로 제한합시다"): two FA cards in the 1군, four held (FaMarket.MaxHeld).
		public const int FaTeam = 250, FaIdxBase = 7_000_000, MaxFaInFirstTeam = 2;

		public bool IsFA => TeamCode == FaTeam;

		public int SourceIdx => IsFA && Idx >= FaIdxBase ? Idx - FaIdxBase : Idx;

		public int HomeTeam { get => m_homeTeam ?? TeamCode; set => m_homeTeam = value; }

		private int? m_homeTeam;

		public bool CanField(int rosterPos)
		{
			return IsBatter && Defence[rosterPos] > 20;
		}
	}

	// The same player-card tables the client downloads (data/tables), read on the server side.
	public sealed class CardCatalog
	{
		private readonly Dictionary<int, CardInfo> m_cards = new Dictionary<int, CardInfo>();

		public IReadOnlyList<int> BasicCards { get; private set; } = Array.Empty<int>();

		public static CardCatalog Load(string tablesDir)
		{
			CardCatalog catalog = new CardCatalog();
			catalog.ReadRows(Path.Combine(tablesDir, "P_BatterTable.txt"), true);
			catalog.ReadRows(Path.Combine(tablesDir, "P_PItcherTable.txt"), false);
			int fa = 0, orphans = 0;
			foreach (CardInfo c in catalog.m_cards.Values)
			{
				if (!c.IsFA) continue;
				fa++;
				CardInfo source = catalog.Get(c.SourceIdx);
				if (source != null && !source.IsFA) c.HomeTeam = source.TeamCode;
				else orphans++;
			}
			if (fa > 0) ServerLog.Info($"FA cards: {fa}" + (orphans > 0 ? $", {orphans} without a source card" : ""));
			string basic = Path.Combine(tablesDir, "BasicCard_list.txt");
			if (File.Exists(basic))
			{
				catalog.BasicCards = File.ReadAllLines(basic, Encoding.UTF8).Skip(1)
					.Where(line => line.Length > 0).Select(line => int.Parse(line.Split('\t')[0])).ToList();
			}
			if (catalog.m_cards.Count > 0 && (catalog.BasicCards.Count == 0 || catalog.BasicCards.Any(i => !catalog.m_cards.ContainsKey(i))))
			{
				catalog.BasicCards = catalog.PickBasicCards();
				File.WriteAllText(basic, "KBO\tMLB\n" + string.Concat(catalog.BasicCards.Select(i => $"{i}\t{i}\n")), new UTF8Encoding(false));
				ServerLog.Info($"starter list rebuilt from the current card tables: {basic}");
			}
			ServerLog.Info($"card catalog: {catalog.m_cards.Count} cards, {catalog.BasicCards.Count} basic cards");
			return catalog;
		}

		// 25 starter cards: a 3-4 ball regular at every fielding position plus six more bats,
		// five starters, four relievers, a set-up man and a closer (recent seasons, fixed seed).
		private List<int> PickBasicCards()
		{
			Random rnd = new Random(20261018);
			HashSet<int> taken = new HashSet<int>();
			List<int> picks = new List<int>();
			void Take(Func<CardInfo, bool> want, int count)
			{
				List<CardInfo> pool = m_cards.Values.Where(c => !taken.Contains(c.Idx) && !c.IsFA && c.Ball >= 3 && c.Ball <= 4 && c.Year >= 2015 && want(c)).OrderBy(c => c.Idx).ToList();
				for (int i = 0; i < count && pool.Count > 0; i++)
				{
					CardInfo c = pool[rnd.Next(pool.Count)];
					pool.Remove(c);
					taken.Add(c.Idx);
					picks.Add(c.Idx);
				}
			}
			foreach (CardPos pos in new[] { CardPos.C, CardPos.B1, CardPos.B2, CardPos.B3, CardPos.SS, CardPos.LF, CardPos.CF, CardPos.RF })
			{
				Take(c => c.IsBatter && c.MainPosition == pos, 1);
			}
			Take(c => c.IsBatter, 6);
			Take(c => !c.IsBatter && c.MainPosition == CardPos.SP, 5);
			Take(c => !c.IsBatter && c.MainPosition == CardPos.RP, 4);
			Take(c => !c.IsBatter && c.MainPosition == CardPos.SU, 1);
			Take(c => !c.IsBatter && c.MainPosition == CardPos.CP, 1);
			return picks;
		}

		public CardInfo Get(int idx)
		{
			m_cards.TryGetValue(idx, out CardInfo card);
			return card;
		}

		public IEnumerable<CardInfo> All => m_cards.Values;

		// The cards random draws may give (packs, 조합 results, starter and AI clubs, team-year decks): every card but the 레전드
		// ones, until the user decides how those are won (2026-11-08), and the FA ones (signed in the FA 시장 only; AI clubs never
		// use them, user 2026-11-15).
		public IEnumerable<CardInfo> Drawable => m_cards.Values.Where(c => !c.IsLegend && !c.IsFA);

		private void ReadRows(string path, bool batters)
		{
			if (!File.Exists(path))
			{
				ServerLog.Warn($"card table missing: {path}");
				return;
			}
			foreach (string line in File.ReadLines(path, Encoding.UTF8).Skip(1))
			{
				string[] c = line.Split('\t');
				if (c.Length < 30)
				{
					continue;
				}
				CardInfo card = new CardInfo
				{
					Idx = int.Parse(c[0]),
					IsBatter = batters,
					Year = int.Parse(c[1]),
					Name = c[2],
					ThrowHand = int.Parse(c[4]),
					HitHand = int.Parse(c[5]),
					Sidx = int.Parse(c[12]),
					TeamCode = int.Parse(c[6]),
					Ball = int.Parse(c[8]),
					MainPosition = (CardPos)int.Parse(c[9]),
					Grade = int.Parse(c[13]),
					Age = int.TryParse(c[7], out int age) ? age : 27,
					Foreigner = c[10] == "1"
				};
				if (batters)
				{
					card.Stats = Enumerable.Range(16, 5).Select(i => int.Parse(c[i])).ToArray();
					card.Defence = Enumerable.Range(21, 8).Select(i => int.Parse(c[i])).ToArray();
					card.PreferredOrder = int.TryParse(c[29], out int order) ? order : 3;
				}
				else
				{
					card.Stats = Enumerable.Range(17, 6).Select(i => int.Parse(c[i])).ToArray();
					card.ThrowType = int.Parse(c[16]);
					card.PreferredOrder = card.MainPosition switch { CardPos.RP => 1, CardPos.SU => 2, CardPos.CP => 3, _ => 0 };
				}
				m_cards[card.Idx] = card;
			}
		}
	}
}
