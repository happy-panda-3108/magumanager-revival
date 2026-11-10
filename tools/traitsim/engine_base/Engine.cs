using System;
using System.Collections.Generic;
using System.Linq;

namespace Ma9MSim
{
	public static class BS
	{
		public const int PA = 0, AB = 1, H = 2, D = 3, T = 4, HR = 5, BB = 6, SO = 7, HBP = 8, SB = 9, CS = 10, R = 11, RBI = 12, GIDP = 13, SH = 14, SF = 15, Count = 16;
	}

	public static class PS
	{
		public const int G = 0, GS = 1, Outs = 2, BF = 3, H = 4, HR = 5, BB = 6, SO = 7, HBP = 8, R = 9, ER = 10, W = 11, L = 12, SV = 13, HLD = 14, Count = 15;
	}

	// Split lines (batter vs RHP/LHP, home/road) and per-pitch results.
	public static class SL
	{
		public const int PA = 0, AB = 1, H = 2, TB = 3, HR = 4, BB = 5, SO = 6, Count = 7;
	}

	public static class PT
	{
		public const int Slots = 6;
		public const int Uses = 0, AB = 1, H = 2, HR = 3, SO = 4, BB = 5, GB = 6, Count = 7;
	}

	public static class PK
	{
		public const int G = 0, R = 1, HR = 2, PA = 3, H = 4, Count = 5;
	}

	public struct Rng
	{
		private ulong m_state;

		public Rng(ulong seed)
		{
			m_state = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL;
			if (m_state == 0)
			{
				m_state = 1;
			}
		}

		public double Next()
		{
			m_state ^= m_state >> 12;
			m_state ^= m_state << 25;
			m_state ^= m_state >> 27;
			return ((m_state * 0x2545F4914F6CDD1DUL) >> 11) * (1.0 / 9007199254740992.0);
		}
	}

	// A season prepared for simulation: players, talents, usage targets and a schedule.
	public sealed class SeasonModel
	{
		public Season Season;
		public League League;
		public BatterLine[] Batters;
		public PitcherLine[] Pitchers;
		public BatterTalent[] BT;
		public PitcherTalent[] PT;
		public int[][] TeamBatters;
		public int[][] TeamPitchers;
		public int[][] TeamStarters;
		public int[] TeamCloser;
		public double[] TargetPA;
		public double[] TargetGS;
		public double[] StartOuts;
		public double[] ReliefOuts;
		public int[] BatterTeam;
		public (int home, int away)[] Schedule;

		// Matchup context.
		public MatchupParams MP;
		public double[][] ParkF;        // per club (its home park), PC components
		public double[][] NeutralParkF;
		public double[] ErrorMult;      // per club
		public int[] BatSide;           // Hand per batter
		public int[] PitcherType;       // Platoon.TypeOf per pitcher
		public double[,,] PlatoonF;
		public PitchMix[] Mix;
		public bool NeutralParks;

		// Card mode: 정신 (mental) decides close and runners-in-scoring-position at-bats. The side
		// with the higher 정신 plays (difference / 10)% above its card; *T[c] hold how much one
		// percent of every stat moves component c (K, BB, HR, BABIP) in log terms.
		public bool Clutch;
		public double[] BatMental, PitMental;
		public double[][] BatT, PitT;
		// Card mode: a pitcher's hidden 위기관리 (pressure) multiplier on hits and homers allowed
		// with runners in scoring position; fitted so the card gives up its season's earned runs.
		public double[] PitRisp;
		// Card mode, fielding and running: each batter's card defence at the eight positions
		// (포수 1루 2루 3루 유격 좌익 중견 우익), who catches, 주력 and 구속 as x = (stat − 65) / 10,
		// and how one percent on 컨트롤·테크닉·구위 moves a pitcher's K, BB, HR, BABIP (the
		// catcher's boost). Null outside card mode: positions are not tracked.
		public double[][] Defs;
		public double[] CatcherShown;   // card mode: shown catcher defence when it differs from the effective one (NaN = same)
		public bool[] IsCatcher;
		public double[] RunX, VeloX;
		public double[][] PitCT;
		public FieldRules Field = new FieldRules();
		// Server card games: fixed lineups, positions and pitching decks per team (null = season usage).
		public GameDeck[] Decks;
		// Today's condition per player: every stat lifted or lowered by this share (null = none).
		public double[] BatCond, PitCond;

		public double[] ParkOf(int homeTeam) => NeutralParks ? NeutralParkF[homeTeam] : ParkF[homeTeam];

		public static SeasonModel Build(Season s, ParkModel parks = null, CardData cards = null, MatchupParams mp = null)
		{
			SeasonModel m = new SeasonModel { Season = s, League = League.From(s) };
			m.MP = mp ?? new MatchupParams();
			m.Batters = s.Batters.ToArray();
			m.Pitchers = s.Pitchers.ToArray();
			m.BT = m.Batters.Select(b => TalentInit.FromRecord(b, m.League)).ToArray();
			m.PT = m.Pitchers.Select(p => TalentInit.FromRecord(p, m.League)).ToArray();
			int teams = s.Teams.Count;
			m.TeamBatters = new int[teams][];
			m.TeamPitchers = new int[teams][];
			m.TeamStarters = new int[teams][];
			m.TeamCloser = new int[teams];
			m.BatterTeam = new int[m.Batters.Length];
			for (int t = 0; t < teams; t++)
			{
				int code = s.Teams[t].Code;
				m.TeamBatters[t] = Enumerable.Range(0, m.Batters.Length).Where(i => m.Batters[i].Team == code).ToArray();
				foreach (int i in m.TeamBatters[t])
				{
					m.BatterTeam[i] = t;
				}
				m.TeamPitchers[t] = Enumerable.Range(0, m.Pitchers.Length).Where(i => m.Pitchers[i].Team == code).ToArray();
				m.TeamStarters[t] = m.TeamPitchers[t].Where(i => m.Pitchers[i].GS > 0).ToArray();
				// The closer is the saves leader among mainly-relief arms (1980s aces who started
				// and also saved games are not used as closers).
				int[] relievers = m.TeamPitchers[t].Where(i => !m.Pitchers[i].Replacement && m.Pitchers[i].GS * 3 <= m.Pitchers[i].G).ToArray();
				int closer = relievers.Length > 0 ? relievers.OrderByDescending(i => m.Pitchers[i].SV).First() : -1;
				m.TeamCloser[t] = closer >= 0 && m.Pitchers[closer].SV >= 5 ? closer : -1;
			}
			m.TargetPA = m.Batters.Select(b => (double)b.PA).ToArray();
			m.TargetGS = m.Pitchers.Select(p => (double)p.GS).ToArray();
			m.StartOuts = new double[m.Pitchers.Length];
			m.ReliefOuts = new double[m.Pitchers.Length];
			// Typical relief outing of the era (1980s relievers went longer).
			IEnumerable<PitcherLine> pen = m.Pitchers.Where(p => p.GS == 0 && !p.Replacement && p.G > 0);
			double reliefPerApp = pen.Any() ? pen.Sum(p => p.Outs) / (double)pen.Sum(p => p.G) : 4;
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				PitcherLine p = m.Pitchers[i];
				if (p.GS > 0)
				{
					int reliefApps = Math.Max(0, p.G - p.GS);
					double perStart = (p.Outs - reliefApps * reliefPerApp) / p.GS;
					m.StartOuts[i] = Math.Clamp(perStart, 6, 27);
					m.ReliefOuts[i] = Math.Max(0, p.Outs - m.StartOuts[i] * p.GS);
				}
				else
				{
					m.ReliefOuts[i] = p.Outs;
				}
			}
			m.Schedule = MakeSchedule(teams, s.Games);

			m.ParkF = parks != null ? parks.SeasonFactors(s) : Enumerable.Range(0, teams).Select(_ => Enumerable.Repeat(1.0, PC.Count).ToArray()).ToArray();
			m.NeutralParkF = Enumerable.Range(0, teams).Select(_ => Enumerable.Repeat(1.0, PC.Count).ToArray()).ToArray();
			m.ErrorMult = m.MP.Defence ? Defence.ErrorMultipliers(s) : Enumerable.Repeat(1.0, teams).ToArray();
			cards ??= new CardData();
			m.BatSide = m.Batters.Select(b => b.Replacement ? (int)Hand.R : (int)cards.BatsOf(b.Sidx)).ToArray();
			m.PitcherType = m.Pitchers.Select(p => p.Replacement ? 0 : Platoon.TypeOf(cards.ThrowsOf(p.Sidx))).ToArray();
			double[] typeShare = new double[4];
			for (int i = 0; i < m.Pitchers.Length; i++)
			{
				typeShare[m.PitcherType[i]] += m.Pitchers[i].BF;
			}
			double bfSum = typeShare.Sum();
			for (int t = 0; t < 4; t++)
			{
				typeShare[t] /= bfSum;
			}
			double[] sideShare = new double[3];
			for (int i = 0; i < m.Batters.Length; i++)
			{
				sideShare[m.BatSide[i]] += m.Batters[i].PA;
			}
			double paSum = sideShare.Sum();
			for (int k = 0; k < 3; k++)
			{
				sideShare[k] /= paSum;
			}
			m.PlatoonF = Platoon.Build(sideShare, typeShare, m.MP);
			m.Mix = m.Pitchers.Select(p => p.Replacement ? null : PitchMix.Build(cards.PitchesOf(p.Sidx, p.Year), m.MP)).ToArray();
			return m;
		}

		// Circle-method round robin repeated until every team has its season's games.
		private static (int, int)[] MakeSchedule(int teams, int games)
		{
			int n = teams % 2 == 0 ? teams : teams + 1;
			int[] played = new int[teams];
			List<(int, int)> list = new List<(int, int)>();
			for (int round = 0; played.Any(g => g < games) && round < games * 3; round++)
			{
				int r = round % (n - 1);
				for (int k = 0; k < n / 2; k++)
				{
					int a = k == 0 ? n - 1 : (r + k) % (n - 1);
					int b = (r + n - 1 - k) % (n - 1);
					if (a >= teams || b >= teams || played[a] >= games || played[b] >= games)
					{
						continue;
					}
					played[a]++;
					played[b]++;
					list.Add(((round + k) % 2 == 0) ? (a, b) : (b, a));
				}
			}
			return list.ToArray();
		}
	}

	public sealed class SimStats
	{
		public readonly long[] B;
		public readonly long[] P;
		public readonly long[] Split;     // batter x {vs RHP, vs LHP} x SL
		public readonly long[] HomeRoad;  // batter x {home, road} x SL
		public readonly long[] Pitch;     // pitcher x slot x PT
		public readonly long[] Park;      // club (home park) x PK
		// Card mode fielding: steal attempts and runners caught by catcher defence (5-point bins
		// from 40), then gap singles tried / safe / thrown out and would-be triples held to doubles.
		public readonly long[] Field = new long[FieldBins * 2 + 4];
		// Gap singles by the runner's 주력 (5-point bins from 40): tried, safe, thrown out.
		public readonly long[] Stretch = new long[FieldBins * 3];
		public const int FieldBins = 22, FieldTry = FieldBins * 2, FieldSafe = FieldTry + 1, FieldOut = FieldTry + 2, FieldHeld = FieldTry + 3;
		public long Games;
		public long Runs;
		public long Ties;

		public SimStats(int batters, int pitchers, int teams)
		{
			B = new long[batters * BS.Count];
			P = new long[pitchers * PS.Count];
			Split = new long[batters * 2 * SL.Count];
			HomeRoad = new long[batters * 2 * SL.Count];
			Pitch = new long[pitchers * PT.Slots * PT.Count];
			Park = new long[teams * PK.Count];
		}

		public long SplitOf(int batter, int lefty, int stat) => Split[(batter * 2 + lefty) * SL.Count + stat];

		public long HomeRoadOf(int batter, int road, int stat) => HomeRoad[(batter * 2 + road) * SL.Count + stat];

		public long PitchOf(int pitcher, int slot, int stat) => Pitch[(pitcher * PT.Slots + slot) * PT.Count + stat];

		public long ParkOf(int team, int stat) => Park[team * PK.Count + stat];

		public long Bat(int i, int stat)
		{
			return B[i * BS.Count + stat];
		}

		public long Pit(int i, int stat)
		{
			return P[i * PS.Count + stat];
		}

		public void Add(SimStats other)
		{
			for (int i = 0; i < B.Length; i++)
			{
				B[i] += other.B[i];
			}
			for (int i = 0; i < P.Length; i++)
			{
				P[i] += other.P[i];
			}
			AddArray(Split, other.Split);
			AddArray(HomeRoad, other.HomeRoad);
			AddArray(Pitch, other.Pitch);
			AddArray(Park, other.Park);
			AddArray(Field, other.Field);
			AddArray(Stretch, other.Stretch);
			Games += other.Games;
			Runs += other.Runs;
			Ties += other.Ties;
		}

		private static void AddArray(long[] a, long[] b)
		{
			for (int i = 0; i < a.Length; i++)
			{
				a[i] += b[i];
			}
		}
	}

	// Simulates whole seasons of one SeasonModel. Not thread-safe; use one per thread.
	public sealed class SeasonSimulator
	{
		private readonly SeasonModel m;
		private readonly EngineParams prm;
		private Rng rng;
		private SimStats st;

		private readonly double[] simPA;
		private readonly double[] simGS;
		private readonly double[] simRelief;
		private readonly int[] teamGames;

		// per game
		private readonly Side[] side = { new Side(), new Side() };
		private readonly int[] bases = new int[3];
		private readonly int[] basePitcher = new int[3];
		private readonly bool[] baseUnearned = new bool[3];
		private bool inningExtended;

		// Server card games: who records the plays, and the play being recorded.
		private IGameRecorder rec;
		private readonly PlayInfo play = new PlayInfo();
		private readonly List<int> scoredNow = new List<int>(4);
		private bool stretchOut;

		// Share of plate appearances that come with nobody out and a runner on first or second.
		private const double BuntShare = 0.13; // an error kept the inning alive with two outs: later runs are unearned

		private sealed class Side
		{
			public int Team;
			public readonly int[] Lineup = new int[9];
			public int Slot;
			public int Score;
			public int Pitcher;
			public int Starter;
			public int StartTarget;
			public int PitcherOuts;      // outs by the current pitcher in this game
			public int PitcherRuns;      // runs charged to the current pitcher in this game
			public int AppearanceTarget; // outs a reliever is meant to get
			public int EntryLead;
			public bool CloserUsed;
			public readonly List<int> Used = new List<int>(8);
			public readonly List<int> EntryLeads = new List<int>(8);
			public readonly List<int> ExitLeads = new List<int>(8);
			public readonly Dictionary<int, int> OutsInGame = new Dictionary<int, int>(8);
			public int RecordPitcher = -1;
			// Card mode: today's fielders (lineup index by position) and what they do on defence.
			public readonly int[] At = new int[8];
			public double CatcherDef = 65, CatcherShown = 65, CaughtMult = 1, LeftCentreX, TripleKeep = 1;
		}

		// Positions filled scarcest first; whoever is left over is the designated hitter.
		private static readonly int[] FillOrder = { 0, 4, 6, 2, 3, 7, 5, 1 };
		private readonly bool[] m_taken = new bool[9];

		private void AssignPositions(Side s)
		{
			Array.Clear(m_taken);
			foreach (int pos in FillOrder)
			{
				int best = -1;
				for (int i = 0; i < 9; i++)
				{
					if (m_taken[i]) continue;
					double d = m.Defs[s.Lineup[i]][pos] + (pos == 0 && m.IsCatcher[s.Lineup[i]] ? 100 : 0);
					if (best < 0 || d > m.Defs[s.Lineup[best]][pos] + (pos == 0 && m.IsCatcher[s.Lineup[best]] ? 100 : 0))
					{
						best = i;
					}
				}
				m_taken[best] = true;
				s.At[pos] = s.Lineup[best];
			}
			SetFieldEffects(s);
		}

		private void SetFieldEffects(Side s)
		{
			double X(int pos) => (m.Defs[s.At[pos]][pos] - 65) / 10.0;
			s.CatcherDef = m.Field.CatcherOverride ?? m.Defs[s.At[0]][0];
			double shown = m.CatcherShown != null ? m.CatcherShown[s.At[0]] : double.NaN;
			s.CatcherShown = m.Field.CatcherOverride ?? (double.IsNaN(shown) ? s.CatcherDef : shown);
			s.CaughtMult = m.Field.CaughtMult((s.CatcherDef - 65) / 10.0);
			s.LeftCentreX = (X(5) + X(6)) / 2;
			s.TripleKeep = m.Field.TripleKeep((X(6) + X(7)) / 2);
		}

		public SeasonSimulator(SeasonModel model, EngineParams parameters, ulong seed)
		{
			m = model;
			prm = parameters;
			rng = new Rng(seed);
			simPA = new double[m.Batters.Length];
			simGS = new double[m.Pitchers.Length];
			simRelief = new double[m.Pitchers.Length];
			teamGames = new int[m.Season.Teams.Count];
		}

		public SimStats RunSeason()
		{
			st = new SimStats(m.Batters.Length, m.Pitchers.Length, m.Season.Teams.Count);
			Array.Clear(simPA);
			Array.Clear(simGS);
			Array.Clear(simRelief);
			Array.Clear(teamGames);
			foreach ((int home, int away) in m.Schedule)
			{
				PlayGame(home, away);
			}
			return st;
		}

		// One game between two teams of the model, every play reported to the recorder.
		public SimStats PlayOne(int home, int away, IGameRecorder recorder)
		{
			st = new SimStats(m.Batters.Length, m.Pitchers.Length, m.Season.Teams.Count);
			rec = recorder;
			PlayGame(home, away);
			rec = null;
			return st;
		}

		private void PlayGame(int home, int away)
		{
			Side h = side[0], a = side[1];
			Prepare(h, home);
			Prepare(a, away);
			int lossLeader = -1;   // pitcher charged with the go-ahead run for the eventual loser
			int inning = 1;
			bool walkOff = false;
			while (true)
			{
				rec?.HalfInningStart(inning, false);
				HalfInning(a, h, inning, false, ref walkOff, ref lossLeader);
				rec?.HalfInningEnd(inning, false);
				if (inning >= 9 && h.Score > a.Score)
				{
					break;
				}
				rec?.HalfInningStart(inning, true);
				HalfInning(h, a, inning, true, ref walkOff, ref lossLeader);
				rec?.HalfInningEnd(inning, true);
				if (walkOff || (inning >= 9 && h.Score != a.Score) || inning >= 12)
				{
					break;
				}
				inning++;
			}
			Finish(h, a, lossLeader);
			st.Park[home * PK.Count + PK.G]++;
			st.Park[home * PK.Count + PK.R] += h.Score + a.Score;
			teamGames[home]++;
			teamGames[away]++;
			st.Games++;
			st.Runs += h.Score + a.Score;
		}

		private void Prepare(Side s, int team)
		{
			s.Team = team;
			s.Slot = 0;
			s.Score = 0;
			s.CloserUsed = false;
			s.Used.Clear();
			s.EntryLeads.Clear();
			s.ExitLeads.Clear();
			s.OutsInGame.Clear();
			s.RecordPitcher = -1;
			if (m.Decks != null && m.Decks[team] != null)
			{
				PrepareDeck(s, m.Decks[team]);
				return;
			}
			double progress = (teamGames[team] + 1.0) / m.Season.Games;

			// Lineup: the nine batters furthest behind their real plate appearances.
			int[] pool = m.TeamBatters[team];
			Span<double> deficit = stackalloc double[pool.Length];
			for (int i = 0; i < pool.Length; i++)
			{
				deficit[i] = m.TargetPA[pool[i]] * progress - simPA[pool[i]] + rng.Next() * 2;
			}
			int first = 0;
			if (m.Defs != null)
			{
				// Card mode: one catcher always starts (the one furthest behind).
				int c = -1;
				for (int i = 0; i < pool.Length; i++)
				{
					if (m.IsCatcher[pool[i]] && (c < 0 || deficit[i] > deficit[c]))
					{
						c = i;
					}
				}
				if (c >= 0)
				{
					s.Lineup[0] = pool[c];
					deficit[c] = double.MinValue;
					first = 1;
				}
			}
			for (int k = first; k < 9; k++)
			{
				int best = -1;
				for (int i = 0; i < pool.Length; i++)
				{
					if (deficit[i] > double.MinValue && (best < 0 || deficit[i] > deficit[best]))
					{
						best = i;
					}
				}
				s.Lineup[k] = best >= 0 ? pool[best] : pool[k % pool.Length];
				if (best >= 0)
				{
					deficit[best] = double.MinValue;
				}
			}
			OrderLineup(s.Lineup);
			if (m.Defs != null)
			{
				AssignPositions(s);
			}

			int[] starters = m.TeamStarters[team];
			int starter = starters[0];
			double bestDeficit = double.MinValue;
			foreach (int p in starters)
			{
				double d = m.TargetGS[p] * progress - simGS[p] + rng.Next() * 0.5;
				if (d > bestDeficit)
				{
					bestDeficit = d;
					starter = p;
				}
			}
			s.Starter = starter;
			s.StartTarget = Math.Clamp((int)Math.Round(m.StartOuts[starter] * (0.75 + 0.5 * rng.Next())), 3, 27);
			simGS[starter]++;
			Enter(s, starter, 0, true);
		}

		// Server card games: the manager's batting order, positions and today's starter.
		private void PrepareDeck(Side s, GameDeck d)
		{
			Array.Copy(d.Order, s.Lineup, 9);
			Array.Copy(d.At, s.At, 8);
			SetFieldEffects(s);
			int starter = d.Pitchers[d.StarterSlot];
			s.Starter = starter;
			s.StartTarget = Math.Clamp((int)Math.Round(m.StartOuts[starter] * (0.75 + 0.5 * rng.Next())), 3, 27);
			Enter(s, starter, 0, true);
		}

		private static double OnBase(BatterTalent t)
		{
			return t.BB + t.HBP + t.HR + (1 - t.K - t.BB - t.HBP - t.HR) * t.Babip;
		}

		// Extra bases per PA (an isolated-power proxy).
		private static double Power(BatterTalent t)
		{
			return 3 * t.HR + (1 - t.K - t.BB - t.HBP - t.HR) * t.Babip * (t.Double + 2 * t.Triple);
		}

		// A conventional order: table-setter leading off, the three best power bats 3-4-5
		// (cleanup = most power), best remaining on-base hitter 2nd, the rest by quality.
		private readonly int[] m_order = new int[9];
		private readonly bool[] m_placed = new bool[9];

		private void OrderLineup(int[] lineup)
		{
			Array.Clear(m_placed);
			int Pick(Func<BatterTalent, double> score)
			{
				int best = -1;
				double bestScore = double.MinValue;
				for (int i = 0; i < 9; i++)
				{
					if (!m_placed[i])
					{
						double v = score(m.BT[lineup[i]]);
						if (v > bestScore)
						{
							bestScore = v;
							best = i;
						}
					}
				}
				m_placed[best] = true;
				return lineup[best];
			}
			// KBO habit: the fastest good on-base hitter leads off even with some pop.
			m_order[0] = Pick(t => OnBase(t) + 0.6 * t.Steal + 0.1 * (t.Speed - 1) - 0.25 * Power(t));
			m_order[3] = Pick(t => Power(t) + 0.3 * OnBase(t));
			m_order[2] = Pick(t => Power(t) + 0.6 * OnBase(t));
			m_order[4] = Pick(t => Power(t) + 0.3 * OnBase(t));
			m_order[1] = Pick(t => OnBase(t) + 0.2 * t.Steal);
			for (int k = 5; k < 9; k++)
			{
				m_order[k] = Pick(t => OnBase(t) + Power(t));
			}
			Array.Copy(m_order, lineup, 9);
		}

		private void Enter(Side s, int pitcher, int lead, bool start)
		{
			if (s.Used.Count > 0)
			{
				s.ExitLeads[s.Used.Count - 1] = lead;
			}
			s.Pitcher = pitcher;
			s.PitcherOuts = 0;
			s.PitcherRuns = 0;
			s.EntryLead = lead;
			s.Used.Add(pitcher);
			s.EntryLeads.Add(lead);
			s.ExitLeads.Add(lead);
			s.OutsInGame[pitcher] = 0;
			int o = pitcher * PS.Count;
			st.P[o + PS.G]++;
			if (start)
			{
				st.P[o + PS.GS]++;
			}
			else
			{
				double remaining = m.ReliefOuts[pitcher] / Math.Max(1, m.Pitchers[pitcher].G - m.Pitchers[pitcher].GS);
				s.AppearanceTarget = Math.Clamp((int)Math.Round(remaining * (0.7 + 0.6 * rng.Next())), 1, 9);
				rec?.PitcherChange(s.Team, pitcher);
			}
		}

		// Called before every plate appearance of the defending side.
		private void MaybeChangePitcher(Side fld, Side bat, int inning, int outs)
		{
			if (m.Decks != null && m.Decks[fld.Team] != null)
			{
				DeckChangePitcher(fld, bat, inning, outs, m.Decks[fld.Team]);
				return;
			}
			int lead = fld.Score - bat.Score;
			bool isStarter = fld.Pitcher == fld.Starter && fld.Used.Count == 1;
			bool change;
			if (isStarter)
			{
				change = (outs == 0 && fld.PitcherOuts >= fld.StartTarget) || fld.PitcherOuts >= fld.StartTarget + 3 || fld.PitcherRuns >= 7
					|| (fld.PitcherRuns >= 5 && fld.PitcherOuts < 12);
			}
			else
			{
				change = (outs == 0 && fld.PitcherOuts >= fld.AppearanceTarget) || fld.PitcherRuns >= 4;
			}
			int closer = m.TeamCloser[fld.Team];
			if (closer >= 0 && !fld.CloserUsed && fld.Pitcher != closer && outs == 0 && lead >= -1 && lead <= 3)
			{
				// Multi-inning relief aces come in earlier than one-inning closers, and (as was
				// common then) also in tied or one-run-down games; closers only protect leads or
				// hold a tie late.
				double perApp = m.ReliefOuts[closer] / Math.Max(1, m.Pitchers[closer].G - m.Pitchers[closer].GS);
				int entryInning = Math.Max(7, 9 - (int)Math.Floor((perApp - 1) / 3));
				bool ace = perApp >= 5;
				bool situation = lead >= 1 || (lead == 0 && inning >= 9) || (ace && lead >= -1);
				// Rest him once he is ahead of his real season workload.
				double season = (teamGames[fld.Team] + 1.0) / m.Season.Games;
				bool rested = m.ReliefOuts[closer] * season - simRelief[closer] > -2;
				if (inning >= entryInning && situation && rested)
				{
					fld.CloserUsed = true;
					Enter(fld, closer, lead, false);
					fld.AppearanceTarget = Math.Clamp((int)Math.Round(perApp * 1.6), 3, 12);
					return;
				}
			}
			if (!change)
			{
				return;
			}
			// Bullpen: the reliever furthest behind his real relief workload.
			int progressTeam = fld.Team;
			double progress = (teamGames[progressTeam] + 1.0) / m.Season.Games;
			int pick = -1;
			double best = double.MinValue;
			foreach (int p in m.TeamPitchers[fld.Team])
			{
				// The closer is kept for save spots (handled above).
				if (fld.OutsInGame.ContainsKey(p) || p == closer)
				{
					continue;
				}
				double d = m.ReliefOuts[p] * progress - simRelief[p] + rng.Next() * 3;
				if (d > best)
				{
					best = d;
					pick = p;
				}
			}
			if (pick < 0 && closer >= 0 && !fld.OutsInGame.ContainsKey(closer))
			{
				pick = closer;
			}
			if (pick < 0)
			{
				return; // bullpen exhausted: current pitcher stays in
			}
			if (pick == closer)
			{
				fld.CloserUsed = true;
			}
			Enter(fld, pick, lead, false);
		}

		// Server card games: the starter goes as his 체력 allows; the closer protects 1-3 run leads
		// (or a home tie) from the ninth, the set-up man holds the eighth, the other relievers come
		// in deck order when a pitcher is spent or knocked around.
		private void DeckChangePitcher(Side fld, Side bat, int inning, int outs, GameDeck d)
		{
			int lead = fld.Score - bat.Score;
			bool isStarter = fld.Pitcher == fld.Starter && fld.Used.Count == 1;
			bool change = isStarter
				? (outs == 0 && fld.PitcherOuts >= fld.StartTarget) || fld.PitcherOuts >= fld.StartTarget + 3 || fld.PitcherRuns >= 7
					|| (fld.PitcherRuns >= 5 && fld.PitcherOuts < 12)
				: (outs == 0 && fld.PitcherOuts >= fld.AppearanceTarget) || fld.PitcherRuns >= 4;
			bool Fresh(int p) => p >= 0 && !fld.OutsInGame.ContainsKey(p);
			int setup = d.Pitchers[9], closer = d.Pitchers[10];
			if (outs == 0 && Fresh(closer) && inning >= 9 && ((lead >= 1 && lead <= 3) || (lead == 0 && fld == side[0])))
			{
				Enter(fld, closer, lead, false);
				fld.AppearanceTarget = 3;
				return;
			}
			if (outs == 0 && Fresh(setup) && inning == 8 && lead >= 0 && lead <= 3)
			{
				Enter(fld, setup, lead, false);
				fld.AppearanceTarget = 3;
				return;
			}
			if (!change)
			{
				return;
			}
			int pick = -1;
			for (int k = 5; k <= 8 && pick < 0; k++)
			{
				if (Fresh(d.Pitchers[k])) pick = d.Pitchers[k];
			}
			for (int k = 9; k <= 10 && pick < 0; k++)
			{
				if (Fresh(d.Pitchers[k])) pick = d.Pitchers[k];
			}
			if (pick < 0)
			{
				return; // bullpen exhausted: current pitcher stays in
			}
			Enter(fld, pick, lead, false);
		}

		private void BeginPlay(int team, int batter, int slot, int outs, int pitcher)
		{
			if (rec == null)
			{
				return;
			}
			play.Team = team;
			play.Batter = batter;
			play.Slot = slot;
			play.Pitcher = pitcher;
			play.PitchSlot = -1;
			play.OutsBefore = outs;
			play.Stretched = play.Held = false;
			play.Before[0] = batter;
			for (int b = 0; b < 3; b++)
			{
				play.Before[b + 1] = bases[b];
			}
			scoredNow.Clear();
			stretchOut = false;
		}

		// Where everyone ended up: still on base, scored, or out (forced or thrown out at the next
		// base; the batter at first, or at second when thrown out stretching a single).
		private void EndPlay(PlayKind kind)
		{
			if (rec == null)
			{
				return;
			}
			play.Kind = kind;
			bool steal = kind == PlayKind.StealSafe || kind == PlayKind.StealOut;
			for (int i = 0; i < 4; i++)
			{
				int r = play.Before[i];
				if (r < 0 || (i == 0 && steal))
				{
					play.Dest[i] = 0;
					continue;
				}
				int now = Array.IndexOf(bases, r);
				if (now >= 0)
				{
					play.Dest[i] = now + 1 == i ? 0 : now + 1;
				}
				else if (scoredNow.Contains(r))
				{
					play.Dest[i] = 4;
				}
				else if (i == 0)
				{
					play.Dest[i] = kind is PlayKind.Strikeout or PlayKind.FlyOut or PlayKind.SacFly or PlayKind.LineOut ? 0 : stretchOut ? 6 : 5;
				}
				else
				{
					play.Dest[i] = 4 + Math.Min(4, i + 1);
				}
			}
			play.Runs = scoredNow.Count;
			rec.Play(play);
		}

		private void HalfInning(Side bat, Side fld, int inning, bool bottom, ref bool walkOff, ref int lossLeader)
		{
			int outs = 0;
			bases[0] = bases[1] = bases[2] = -1;
			inningExtended = false;
			while (outs < 3)
			{
				MaybeChangePitcher(fld, bat, inning, outs);
				int pitcher = fld.Pitcher;

				// Steal of second.
				if (bases[0] >= 0 && bases[1] < 0 && outs < 2)
				{
					BatterTalent r = m.BT[bases[0]];
					double attempt = r.Steal / (prm.StealSuccess * 1.6) * VeloAttempt(pitcher);
					if (rng.Next() < attempt)
					{
						bool safe = rng.Next() < StealSafe(r, fld);
						TallySteal(fld, safe);
						BeginPlay(bat.Team, bat.Lineup[bat.Slot], bat.Slot, outs, pitcher);
						if (safe)
						{
							st.B[bases[0] * BS.Count + BS.SB]++;
							Move(0, 1);
						}
						else
						{
							st.B[bases[0] * BS.Count + BS.CS]++;
							bases[0] = -1;
							outs++;
							AddOut(fld, pitcher);
						}
						EndPlay(safe ? PlayKind.StealSafe : PlayKind.StealOut);
						if (outs >= 3)
						{
							break;
						}
					}
				}

				// Steal of third (much rarer).
				if (bases[1] >= 0 && bases[2] < 0 && bases[0] < 0 && outs < 2)
				{
					BatterTalent r = m.BT[bases[1]];
					if (rng.Next() < r.Steal * 0.12 / prm.StealSuccess * VeloAttempt(pitcher))
					{
						bool safe = rng.Next() < StealSafe(r, fld);
						TallySteal(fld, safe);
						BeginPlay(bat.Team, bat.Lineup[bat.Slot], bat.Slot, outs, pitcher);
						if (safe)
						{
							st.B[bases[1] * BS.Count + BS.SB]++;
							Move(1, 2);
						}
						else
						{
							st.B[bases[1] * BS.Count + BS.CS]++;
							bases[1] = -1;
							outs++;
							AddOut(fld, pitcher);
						}
						EndPlay(safe ? PlayKind.StealSafe : PlayKind.StealOut);
						if (outs >= 3)
						{
							break;
						}
					}
				}

				int batter = bat.Lineup[bat.Slot];
				BeginPlay(bat.Team, batter, bat.Slot, outs, pitcher);
				PlayKind kind = PlayKind.GroundOut;
				bat.Slot = (bat.Slot + 1) % 9;
				simPA[batter]++;
				int bo = batter * BS.Count, po = pitcher * PS.Count;
				st.B[bo + BS.PA]++;
				st.P[po + PS.BF]++;

				BatterTalent bt = m.BT[batter];
				PitcherTalent pt = m.PT[pitcher];
				League lg = m.League;

				int scoredBefore = bat.Score;

				// Sacrifice bunt: nobody out, runner on first and/or second.
				if (outs == 0 && (bases[0] >= 0 || bases[1] >= 0) && rng.Next() < Math.Min(0.9, bt.Sac / BuntShare))
				{
					double b = rng.Next();
					if (b < 0.80)
					{
						kind = PlayKind.SacBunt;
						st.B[bo + BS.SH]++;
						Tally(batter, pitcher, -1, m.PitcherType[pitcher] >= 2 ? 1 : 0, bottom, EvSacFly, false);
						if (bases[2] >= 0 && bases[1] >= 0) Score(2, batter, true, bat, fld);
						if (bases[1] >= 0 && bases[2] < 0) Move(1, 2);
						if (bases[0] >= 0 && bases[1] < 0) Move(0, 1);
						outs++;
						AddOut(fld, pitcher);
					}
					else if (b < 0.88)
					{
						kind = PlayKind.BuntHit;
						st.B[bo + BS.AB]++;
						st.B[bo + BS.H]++;
						st.P[po + PS.H]++;
						Tally(batter, pitcher, -1, m.PitcherType[pitcher] >= 2 ? 1 : 0, bottom, EvSingle, false);
						if (bases[2] >= 0 && bases[1] >= 0 && bases[0] >= 0) Score(2, batter, true, bat, fld);
						if (bases[1] >= 0 && bases[2] < 0) Move(1, 2);
						if (bases[0] >= 0 && bases[1] < 0) Move(0, 1);
						Place(0, batter, pitcher, false);
					}
					else
					{
						// Lead runner forced out, batter safe at first.
						kind = PlayKind.BuntForce;
						st.B[bo + BS.AB]++;
						Tally(batter, pitcher, -1, m.PitcherType[pitcher] >= 2 ? 1 : 0, bottom, EvOut, true);
						int lead = bases[1] >= 0 ? 1 : 0;
						bases[lead] = -1;
						if (lead == 1 && bases[0] >= 0) Move(0, 1);
						Place(0, batter, pitcher, false);
						outs++;
						AddOut(fld, pitcher);
					}
					goto AfterPlay;
				}
				// Context beyond talent: the home club's park, the platoon matchup and the pitch
				// that decides the plate appearance.
				double[] pf = m.ParkOf(side[0].Team);
				double mK = pf[PC.K], mBB = pf[PC.BB], mHR = pf[PC.HR], mBabip = pf[PC.Babip], mD = pf[PC.Double], mT = pf[PC.Triple], mGB = 1.0;
				int ptype = m.PitcherType[pitcher];
				int lefty = ptype >= 2 ? 1 : 0;
				if (m.MP.Platoon)
				{
					int bs = m.BatSide[batter];
					mK *= m.PlatoonF[bs, ptype, 0];
					mBB *= m.PlatoonF[bs, ptype, 1];
					mHR *= m.PlatoonF[bs, ptype, 2];
					mBabip *= m.PlatoonF[bs, ptype, 3];
				}
				PitchMix mix = m.MP.PitchMix ? m.Mix[pitcher] : null;
				int slot = -1;
				if (mix != null)
				{
					slot = mix.Pick(rng.Next());
					play.PitchSlot = slot;
					mK *= mix.K[slot];
					mHR *= mix.HR[slot];
					mBabip *= mix.Babip[slot];
					mGB = mix.Ground[slot];
				}
				if (m.Clutch && (bases[1] >= 0 || bases[2] >= 0 || (inning >= 5 && Math.Abs(bat.Score - fld.Score) <= 3)))
				{
					double diff = m.BatMental[batter] - m.PitMental[pitcher];
					if (diff != 0)
					{
						double[][] t = diff > 0 ? m.BatT : m.PitT;
						int who = diff > 0 ? batter : pitcher;
						double boost = Math.Abs(diff) / 1000.0; // (difference / 10)%
						mK *= Math.Exp(boost * t[0][who]);
						mBB *= Math.Exp(boost * t[1][who]);
						mHR *= Math.Exp(boost * t[2][who]);
						mBabip *= Math.Exp(boost * t[3][who]);
					}
				}
				if (m.BatCond != null)
				{
					// Today's condition: every stat of the batter and of the pitcher up or down by this share.
					double cb = m.BatCond[batter], cp = m.PitCond[pitcher];
					if (cb != 0)
					{
						mK *= Math.Exp(cb * m.BatT[0][batter]);
						mBB *= Math.Exp(cb * m.BatT[1][batter]);
						mHR *= Math.Exp(cb * m.BatT[2][batter]);
						mBabip *= Math.Exp(cb * m.BatT[3][batter]);
					}
					if (cp != 0)
					{
						mK *= Math.Exp(cp * m.PitT[0][pitcher]);
						mBB *= Math.Exp(cp * m.PitT[1][pitcher]);
						mHR *= Math.Exp(cp * m.PitT[2][pitcher]);
						mBabip *= Math.Exp(cp * m.PitT[3][pitcher]);
					}
				}
				if (m.PitRisp != null && (bases[1] >= 0 || bases[2] >= 0))
				{
					mBabip *= m.PitRisp[pitcher];
					mHR *= m.PitRisp[pitcher];
				}
				if (m.PitCT != null)
				{
					// A 100+ catcher lifts the pitcher's 컨트롤·테크닉·구위 by a few percent.
					double boost = m.Field.CatcherBoost(fld.CatcherShown) / 100.0;
					if (boost > 0)
					{
						mK *= Math.Exp(boost * m.PitCT[0][pitcher]);
						mBB *= Math.Exp(boost * m.PitCT[1][pitcher]);
						mHR *= Math.Exp(boost * m.PitCT[2][pitcher]);
						mBabip *= Math.Exp(boost * m.PitCT[3][pitcher]);
					}
				}
				double pk = Log5.Combine(bt.K, pt.K, lg.K) * mK;
				double pbb = Log5.Combine(bt.BB, pt.BB, lg.BB) * mBB;
				double phbp = Log5.Combine(bt.HBP, pt.HBP, lg.HBP);
				double phr = Log5.Combine(bt.HR, pt.HR, lg.HR) * mHR;
				double sum = pk + pbb + phbp + phr;
				if (sum > 0.9)
				{
					double scale = 0.9 / sum;
					pk *= scale; pbb *= scale; phbp *= scale; phr *= scale;
				}
				int ev;
				bool ground = false;
				double u = rng.Next();
				if (u < pk)
				{
					ev = EvK;
					kind = PlayKind.Strikeout;
					st.B[bo + BS.AB]++;
					st.B[bo + BS.SO]++;
					st.P[po + PS.SO]++;
					outs++;
					AddOut(fld, pitcher);
				}
				else if (u < pk + pbb + phbp)
				{
					bool hbp = u >= pk + pbb;
					ev = hbp ? EvHbp : EvBb;
					kind = hbp ? PlayKind.HitByPitch : PlayKind.Walk;
					st.B[bo + (hbp ? BS.HBP : BS.BB)]++;
					st.P[po + (hbp ? PS.HBP : PS.BB)]++;
					ForceWalk(batter, pitcher, bat, fld);
				}
				else if (u < pk + pbb + phbp + phr)
				{
					ev = EvHr;
					kind = PlayKind.HomeRun;
					st.B[bo + BS.AB]++;
					st.B[bo + BS.H]++;
					st.B[bo + BS.HR]++;
					st.P[po + PS.H]++;
					st.P[po + PS.HR]++;
					for (int b = 2; b >= 0; b--)
					{
						if (bases[b] >= 0)
						{
							Score(b, batter, true, bat, fld);
						}
					}
					ScoreRunner(batter, pitcher, false, batter, true, bat, fld);
				}
				else
				{
					st.B[bo + BS.AB]++;
					double babip = Math.Clamp(Log5.Combine(bt.Babip, pt.Babip, lg.Babip) * mBabip, 0.05, 0.6);
					if (rng.Next() < babip)
					{
						st.B[bo + BS.H]++;
						st.P[po + PS.H]++;
						double v = rng.Next();
						double triple = bt.Triple * mT, dbl = bt.Double * mD;
						int stretch = 0;   // card mode, gap single: 1 safe at second, 2 thrown out
						if (m.Defs != null)
						{
							// The card's double share holds the average stretched single; the
							// right-centre fielders hold some would-be triples to doubles.
							double avg = m.Field.AverageStretch;
							dbl = Math.Max(0.01, (dbl - (1 - triple) * avg) / (1 - avg));
							double keep = fld.TripleKeep;
							if (v >= triple * keep && v < triple)
							{
								play.Held = true;
								st.Field[SimStats.FieldHeld]++;
							}
							dbl += triple * (1 - keep);
							triple *= keep;
							if (v >= triple + dbl && bases[0] < 0 && rng.Next() < m.Field.GapSingle && rng.Next() < m.Field.TryP(m.RunX[batter], fld.LeftCentreX))
							{
								stretch = rng.Next() < m.Field.SafeP(m.RunX[batter], fld.LeftCentreX) ? 1 : 2;
								if (stretch == 1)
								{
									play.Stretched = true;
									TallyStretch(batter, true);
									v = triple; // scored as a double
								}
							}
						}
						if (v < triple)
						{
							ev = EvTriple;
							kind = PlayKind.Triple;
							st.B[bo + BS.T]++;
							for (int b = 2; b >= 0; b--)
							{
								if (bases[b] >= 0)
								{
									Score(b, batter, true, bat, fld);
								}
							}
							Place(2, batter, pitcher, false);
						}
						else if (v < triple + dbl)
						{
							ev = EvDouble;
							kind = PlayKind.Double;
							st.B[bo + BS.D]++;
							if (bases[2] >= 0) Score(2, batter, true, bat, fld);
							if (bases[1] >= 0) Score(1, batter, true, bat, fld);
							if (bases[0] >= 0)
							{
								if (rng.Next() < Adv(prm.Double1ToHome, bases[0]))
								{
									Score(0, batter, true, bat, fld);
								}
								else
								{
									Move(0, 2);
								}
							}
							Place(1, batter, pitcher, false);
						}
						else
						{
							ev = EvSingle;
							kind = PlayKind.Single;
							if (bases[2] >= 0) Score(2, batter, true, bat, fld);
							if (bases[1] >= 0)
							{
								if (rng.Next() < Adv(prm.Single2ToHome, bases[1]))
								{
									Score(1, batter, true, bat, fld);
								}
								else
								{
									Move(1, 2);
								}
							}
							if (bases[0] >= 0)
							{
								if (bases[2] < 0 && rng.Next() < Adv(prm.Single1To3, bases[0]))
								{
									Move(0, 2);
								}
								else if (bases[1] < 0)
								{
									Move(0, 1);
								}
							}
							Place(0, batter, pitcher, false);
							// A runner already on second holds the batter at first: no try.
							if (stretch == 2 && bases[1] < 0)
							{
								TallyStretch(batter, false);
								stretchOut = true;
								// Thrown out trying for second: still a single, plus an out on the bases.
								bases[0] = -1;
								outs++;
								AddOut(fld, pitcher);
							}
						}
					}
					else if (rng.Next() < prm.ReachOnError * m.ErrorMult[fld.Team])
					{
						ev = EvError;
						kind = PlayKind.Error;
						// Runs after an error that would have ended the inning are unearned; with one
						// out the error often has the same effect (a double play was in order).
						if (outs == 2 || (outs == 1 && rng.Next() < 0.5))
						{
							inningExtended = true;
						}
						// Reached on error: everyone moves up one; runs from here on are unearned.
						if (bases[2] >= 0) Score(2, -1, false, bat, fld);
						if (bases[1] >= 0) Move(1, 2);
						if (bases[0] >= 0) Move(0, 1);
						Place(0, batter, pitcher, true);
					}
					else
					{
						ev = EvOut;
						kind = PlayKind.LineOut;
						// Ground-ball pitches shift outs from the air to the ground (more double plays).
						double groundShare = Math.Min(0.75, prm.GroundShare * mGB);
						double flyShare = (1 - groundShare) * prm.FlyShare / (1 - prm.GroundShare);
						double kindRoll = rng.Next();
						if (kindRoll < groundShare)
						{
							ground = true;
							if (bases[0] >= 0 && outs < 2 && rng.Next() < Math.Clamp(prm.DoublePlay / prm.RunEnvironment / m.BT[batter].Speed, 0.05, 0.8))
							{
								st.B[bo + BS.GIDP]++;
								kind = PlayKind.DoublePlay;
								bases[0] = -1;
								outs += 2;
								AddOut(fld, pitcher);
								AddOut(fld, pitcher);
								if (outs < 3)
								{
									if (bases[2] >= 0) Score(2, -1, true, bat, fld);
									if (bases[1] >= 0) Move(1, 2);
								}
							}
							else
							{
								kind = PlayKind.GroundOut;
								outs++;
								AddOut(fld, pitcher);
								if (outs < 3)
								{
									bool advance = rng.Next() < Adv(prm.GroundAdvance, batter);
									if (bases[2] >= 0 && advance) Score(2, batter, true, bat, fld);
									if (bases[1] >= 0 && bases[2] < 0 && advance) Move(1, 2);
									if (bases[0] >= 0)
									{
										// Fielder's choice / force: batter replaces the lead runner at first half the time.
										if (bases[1] < 0)
										{
											Move(0, 1);
										}
									}
								}
							}
						}
						else if (kindRoll < groundShare + flyShare)
						{
							kind = PlayKind.FlyOut;
							outs++;
							AddOut(fld, pitcher);
							if (outs < 3)
							{
								if (bases[2] >= 0 && rng.Next() < Adv(prm.SacFly, bases[2]))
								{
									Score(2, batter, true, bat, fld);
									st.B[bo + BS.AB]--;
									st.B[bo + BS.SF]++;
									ev = EvSacFly;
									kind = PlayKind.SacFly;
								}
								if (bases[1] >= 0 && bases[2] < 0 && rng.Next() < Adv(prm.Fly2To3, bases[1]))
								{
									Move(1, 2);
								}
							}
						}
						else
						{
							outs++;
							AddOut(fld, pitcher);
						}
					}
				}
				Tally(batter, pitcher, slot, lefty, bottom, ev, ground);

			AfterPlay:
				EndPlay(kind);
				// Lead change: remember who gets the decision.
				if (bat.Score > fld.Score && scoredBefore <= fld.Score)
				{
					bat.RecordPitcher = bat.Pitcher;
					fld.RecordPitcher = pitcher;
					lossLeader = pitcher;
				}
				if (bottom && inning >= 9 && bat.Score > fld.Score)
				{
					walkOff = true;
					return;
				}
			}
		}

		private const int EvK = 0, EvBb = 1, EvHbp = 2, EvHr = 3, EvSingle = 4, EvDouble = 5, EvTriple = 6, EvOut = 7, EvError = 8, EvSacFly = 9;

		// Split lines, park totals and per-pitch results for one plate appearance.
		private void Tally(int batter, int pitcher, int slot, int lefty, bool home, int ev, bool ground)
		{
			bool ab = ev != EvBb && ev != EvHbp && ev != EvSacFly;
			bool hit = ev >= EvHr && ev <= EvTriple;
			int tb = ev == EvHr ? 4 : ev == EvSingle ? 1 : ev == EvDouble ? 2 : ev == EvTriple ? 3 : 0;
			AddSplit(st.Split, (batter * 2 + lefty) * SL.Count, ab, hit, tb, ev);
			AddSplit(st.HomeRoad, (batter * 2 + (home ? 0 : 1)) * SL.Count, ab, hit, tb, ev);
			int pk = side[0].Team * PK.Count;
			st.Park[pk + PK.PA]++;
			if (hit) st.Park[pk + PK.H]++;
			if (ev == EvHr) st.Park[pk + PK.HR]++;
			if (slot >= 0 && slot < PT.Slots)
			{
				int o = (pitcher * PT.Slots + slot) * PT.Count;
				st.Pitch[o + PT.Uses]++;
				if (ab) st.Pitch[o + PT.AB]++;
				if (hit) st.Pitch[o + PT.H]++;
				if (ev == EvHr) st.Pitch[o + PT.HR]++;
				if (ev == EvK) st.Pitch[o + PT.SO]++;
				if (ev == EvBb || ev == EvHbp) st.Pitch[o + PT.BB]++;
				if (ground) st.Pitch[o + PT.GB]++;
			}
		}

		private static void AddSplit(long[] a, int o, bool ab, bool hit, int tb, int ev)
		{
			a[o + SL.PA]++;
			if (ab) a[o + SL.AB]++;
			if (hit) a[o + SL.H]++;
			a[o + SL.TB] += tb;
			if (ev == EvHr) a[o + SL.HR]++;
			if (ev == EvBb || ev == EvHbp) a[o + SL.BB]++;
			if (ev == EvK) a[o + SL.SO]++;
		}

		// Card mode: harder throwers keep runners closer (fewer attempts) ...
		private double VeloAttempt(int pitcher) => m.VeloX != null ? m.Field.AttemptMult(m.VeloX[pitcher]) : 1.0;

		// ... and the catcher's defence decides how many of them are thrown out.
		private double StealSafe(BatterTalent r, Side fld)
		{
			double safe = Math.Clamp(prm.StealSuccess * Math.Pow(r.Speed, 0.5), 0.4, 0.92);
			return m.Defs != null ? 1 - Math.Clamp((1 - safe) * fld.CaughtMult, 0.02, 0.75) : safe;
		}

		private void TallyStretch(int batter, bool safe)
		{
			int bin = Math.Clamp((int)((65 + 10 * m.RunX[batter] - 40) / 5), 0, SimStats.FieldBins - 1);
			st.Field[SimStats.FieldTry]++;
			st.Field[safe ? SimStats.FieldSafe : SimStats.FieldOut]++;
			st.Stretch[bin * 3]++;
			st.Stretch[bin * 3 + (safe ? 1 : 2)]++;
		}

		private void TallySteal(Side fld, bool safe)
		{
			if (m.Defs == null) return;
			int bin = Math.Clamp((int)((fld.CatcherDef - 40) / 5), 0, SimStats.FieldBins - 1);
			st.Field[bin * 2]++;
			if (!safe) st.Field[bin * 2 + 1]++;
		}

		private double Adv(double baseProb, int runner)
		{
			double sp = runner >= 0 ? m.BT[runner].Speed : 1.0;
			return Math.Clamp(baseProb * prm.RunEnvironment * sp, 0.02, 0.97);
		}

		private void AddOut(Side fld, int pitcher)
		{
			st.P[pitcher * PS.Count + PS.Outs]++;
			fld.PitcherOuts++;
			fld.OutsInGame[pitcher] = fld.OutsInGame[pitcher] + 1;
			if (pitcher != fld.Starter || fld.Used.Count > 1)
			{
				simRelief[pitcher]++;
			}
		}

		private void Place(int b, int runner, int pitcher, bool unearned)
		{
			bases[b] = runner;
			basePitcher[b] = pitcher;
			baseUnearned[b] = unearned;
		}

		private void Move(int from, int to)
		{
			bases[to] = bases[from];
			basePitcher[to] = basePitcher[from];
			baseUnearned[to] = baseUnearned[from];
			bases[from] = -1;
		}

		private void ForceWalk(int batter, int pitcher, Side bat, Side fld)
		{
			if (bases[0] >= 0)
			{
				if (bases[1] >= 0)
				{
					if (bases[2] >= 0)
					{
						Score(2, batter, true, bat, fld);
					}
					Move(1, 2);
				}
				Move(0, 1);
			}
			Place(0, batter, pitcher, false);
		}

		private void Score(int b, int rbiBatter, bool rbi, Side bat, Side fld)
		{
			ScoreRunner(bases[b], basePitcher[b], baseUnearned[b], rbiBatter, rbi, bat, fld);
			bases[b] = -1;
		}

		private void ScoreRunner(int runner, int pitcher, bool unearned, int rbiBatter, bool rbi, Side bat, Side fld)
		{
			if (rec != null)
			{
				scoredNow.Add(runner);
			}
			bat.Score++;
			st.B[runner * BS.Count + BS.R]++;
			if (rbi && rbiBatter >= 0)
			{
				st.B[rbiBatter * BS.Count + BS.RBI]++;
			}
			int po = pitcher * PS.Count;
			st.P[po + PS.R]++;
			if (!unearned && !inningExtended)
			{
				st.P[po + PS.ER]++;
			}
			if (pitcher == fld.Pitcher)
			{
				fld.PitcherRuns++;
			}
		}

		private void Finish(Side h, Side a, int lossLeader)
		{
			if (h.Score == a.Score)
			{
				st.Ties++;
				return;
			}
			Side win = h.Score > a.Score ? h : a;
			Side lose = win == h ? a : h;
			win.ExitLeads[win.Used.Count - 1] = win.Score - lose.Score;
			lose.ExitLeads[lose.Used.Count - 1] = lose.Score - win.Score;

			int w = win.RecordPitcher >= 0 ? win.RecordPitcher : win.Starter;
			if (w == win.Starter && win.Used.Count > 1 && win.OutsInGame[win.Starter] < 15)
			{
				w = win.Used[1]; // starter short of five innings: first reliever takes the win
			}
			int l = lose.RecordPitcher >= 0 ? lose.RecordPitcher : lose.Starter;
			st.P[w * PS.Count + PS.W]++;
			st.P[l * PS.Count + PS.L]++;

			int finisher = win.Used[win.Used.Count - 1];
			int entry = win.EntryLeads[win.Used.Count - 1];
			if (finisher != w && win.Used.Count > 1 && entry > 0 && (entry <= 3 || win.OutsInGame[finisher] >= 9))
			{
				st.P[finisher * PS.Count + PS.SV]++;
			}
			foreach (Side s in new[] { win, lose })
			{
				for (int i = 1; i < s.Used.Count - 1; i++)
				{
					int p = s.Used[i];
					if (p != w && p != l && s.EntryLeads[i] >= 1 && s.EntryLeads[i] <= 3 && s.ExitLeads[i] > 0)
					{
						st.P[p * PS.Count + PS.HLD]++;
					}
				}
			}
		}
	}
}
