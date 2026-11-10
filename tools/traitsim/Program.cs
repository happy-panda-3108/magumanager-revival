using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Ma9MSim;

namespace TraitSim
{
	public static class Program
	{
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

		public static int Main(string[] args)
		{
			CultureInfo.DefaultThreadCurrentCulture = Inv;
			Console.OutputEncoding = new UTF8Encoding(false);
			Dictionary<string, string> opt = new Dictionary<string, string>();
			for (int i = 0; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
			string Get(string k, string d) => opt.TryGetValue(k, out string v) ? v : d;
			string data = Get("data", "../../server/data/sim");
			string mode = Get("mode", "units");
			string outDir = Get("out", "../../sim/out/traits1");
			int rosters = int.Parse(Get("rosters", "240"));
			int games = int.Parse(Get("games", "400"));
			Directory.CreateDirectory(outDir);
			// --rules Name=value,... overrides TacticRules fields (tuning).
			foreach (string kv in Get("rules", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] p = kv.Split('=');
				System.Reflection.FieldInfo fi = typeof(TacticRules).GetField(p[0]) ?? throw new ArgumentException("no rule " + p[0]);
				fi.SetValue(CardGame.Rules, double.Parse(p[1], Inv));
			}
			Stopwatch sw = Stopwatch.StartNew();
			Arena arena = new Arena(data, rosters, int.Parse(Get("seed", "11")), 0.9);
			Console.WriteLine($"{arena.Rosters.Count} rosters loaded in {sw.Elapsed.TotalSeconds:F1}s");
			switch (mode)
			{
				case "units":
					Units.Run(arena, games, outDir, Units.Levers());
					break;
				case "units3":
					Units.Run(arena, games, outDir, Units.Levers3());
					break;
				case "units2":
					Units.Run(arena, games, outDir, Units.Levers2());
					break;
				case "traits":
					TraitRun.Run(arena, games, outDir, Get("table", "traits.json"), Get("parts", "all"));
					break;
				case "tactics":
					TacticRun.Run(arena, games, outDir, Get("parts", "all"), Get("slider", ""));
					break;
				default:
					Console.WriteLine("modes: units, traits");
					return 1;
			}
			Console.WriteLine($"done in {sw.Elapsed.TotalSeconds:F0}s");
			return 0;
		}
	}

	// Win value of each lever the traits can pull, in mirror matches (same roster on both sides,
	// team A with the lever).
	public static class Units
	{
		public static readonly string[] BatNames = { "컨택", "파워", "선구", "주력", "H정신", "수비" };
		public static readonly string[] PitNames = { "체력", "구속", "컨트롤", "테크닉", "구위", "P정신" };

		public static List<(string key, string label, Effect e)> Levers()
		{
			List<(string, string, Effect)> list = new List<(string, string, Effect)>();
			for (int j = 0; j < 6; j++)
			{
				Effect e = new Effect();
				e.Bat[j] = 10;
				list.Add(($"bat{j}", $"타자 {BatNames[j]} +10", e));
			}
			for (int j = 0; j < 6; j++)
			{
				Effect e = new Effect();
				e.Pit[j] = 10;
				list.Add(($"pit{j}", $"투수 {PitNames[j]} +10", e));
			}
			Effect allBat = new Effect();
			Effect allPit = new Effect();
			Effect late = new Effect();
			for (int j = 0; j < 6; j++)
			{
				allBat.Bat[j] = 10;
				allPit.Pit[j] = 10;
				late.Late[j] = 10;
			}
			list.Add(("batAll", "타자 전 능력 +10", allBat));
			list.Add(("pitAll", "투수 전 능력 +10", allPit));
			list.Add(("late", "셋업·마무리 전 능력 +10", late));
			list.Add(("hr", "홈런 ×1.10", new Effect { HR = 1.10 }));
			list.Add(("k", "삼진 ×0.90", new Effect { K = 0.90 }));
			list.Add(("bb", "볼넷 ×1.10", new Effect { BB = 1.10 }));
			list.Add(("xbh", "2·3루타 비중 ×1.10", new Effect { XBH = 1.10 }));
			list.Add(("sbTry", "도루 시도 ×1.30", new Effect { StealTry = 1.30 }));
			list.Add(("sbSafe", "도루자 비율 ×0.70", new Effect { StealCaught = 0.70 }));
			list.Add(("pk", "탈삼진 ×1.10", new Effect { PK = 1.10 }));
			list.Add(("pbabip", "인플레이 피안타 ×0.95", new Effect { PBabip = 0.95 }));
			list.Add(("phr", "피홈런 ×0.90", new Effect { PHR = 0.90 }));
			list.Add(("pxbh", "피 2·3루타 비중 ×0.90", new Effect { PXBH = 0.90 }));
			list.Add(("psbTry", "상대 도루 시도 ×0.70", new Effect { PStealTry = 0.70 }));
			list.Add(("pcaught", "도루저지 ×1.30", new Effect { PCaught = 1.30 }));
			list.Add(("outs", "선발 소화 +3아웃", new Effect { StartOuts = 3 }));
			// Linearity checks.
			Effect c20 = new Effect();
			c20.Bat[0] = 20;
			list.Add(("bat0x2", "타자 컨택 +20", c20));
			Effect g20 = new Effect();
			g20.Pit[4] = 20;
			list.Add(("pit4x2", "투수 구위 +20", g20));
			list.Add(("hrx2", "홈런 ×1.21", new Effect { HR = 1.21 }));
			return list;
		}

		// Precise unit values: every lever at a size worth about 2-4%p, for the per-point table.
		public static List<(string key, string label, Effect e)> Levers2()
		{
			List<(string, string, Effect)> list = new List<(string, string, Effect)>();
			double[] batSize = { 10, 10, 10, 40, 40, 40 };
			double[] pitSize = { 40, 15, 15, 10, 10, 40 };
			for (int j = 0; j < 6; j++)
			{
				Effect e = new Effect();
				e.Bat[j] = batSize[j];
				list.Add(($"bat{j}:{batSize[j]}", $"타자 {BatNames[j]} +{batSize[j]}", e));
			}
			for (int j = 0; j < 6; j++)
			{
				Effect e = new Effect();
				e.Pit[j] = pitSize[j];
				list.Add(($"pit{j}:{pitSize[j]}", $"투수 {PitNames[j]} +{pitSize[j]}", e));
			}
			Effect late = new Effect();
			for (int j = 0; j < 6; j++) late.Late[j] = 20;
			list.Add(("late:20", "셋업·마무리 전 능력 +20", late));
			list.Add(("hr:15", "홈런 ×1.15", new Effect { HR = 1.15 }));
			list.Add(("k:-15", "삼진 ×0.85", new Effect { K = 0.85 }));
			list.Add(("bb:15", "볼넷 ×1.15", new Effect { BB = 1.15 }));
			list.Add(("xbh:20", "2·3루타 비중 ×1.20", new Effect { XBH = 1.20 }));
			list.Add(("pk:15", "탈삼진 ×1.15", new Effect { PK = 1.15 }));
			list.Add(("pbabip:-6", "인플레이 피안타 ×0.94", new Effect { PBabip = 0.94 }));
			list.Add(("phr:-15", "피홈런 ×0.85", new Effect { PHR = 0.85 }));
			list.Add(("pxbh:-20", "피 2·3루타 비중 ×0.80", new Effect { PXBH = 0.80 }));
			list.Add(("sbTry:40", "도루 시도 ×1.40", new Effect { StealTry = 1.40 }));
			list.Add(("sbCaught:-30", "도루자 ×0.70", new Effect { StealCaught = 0.70 }));
			list.Add(("sb105", "도루 시도 ×1.40 + 도루자 ×0.72", new Effect { StealTry = 1.40, StealCaught = 0.72 }));
			list.Add(("sb105_111", "도루 시도 ×1.40 + 도루자 ×0.576", new Effect { StealTry = 1.40, StealCaught = 0.576 }));
			list.Add(("psbTry:-40", "상대 도루 시도 ×0.60", new Effect { PStealTry = 0.60 }));
			list.Add(("pcaught:40", "도루저지 ×1.40", new Effect { PCaught = 1.40 }));
			list.Add(("p127", "상대 도루 시도 ×0.60 + 도루저지 ×1.40", new Effect { PStealTry = 0.60, PCaught = 1.40 }));
			list.Add(("outs:6", "선발 소화 +6아웃", new Effect { StartOuts = 6 }));
			return list;
		}

		public static List<(string key, string label, Effect e)> Levers3()
		{
			List<(string, string, Effect)> list = new List<(string, string, Effect)>();
			Effect sp = new Effect();
			for (int j = 0; j < 6; j++) sp.Start[j] = 4;
			list.Add(("start:4", "선발 전 능력 +4", sp));
			list.Add(("outs:3", "선발 소화 +3아웃", new Effect { StartOuts = 3 }));
			list.Add(("sbCaught:-20", "도루자 ×0.80", new Effect { StealCaught = 0.80 }));
			list.Add(("sb105b", "도루 시도 ×1.40 + 도루자 ×0.70", new Effect { StealTry = 1.40, StealCaught = 0.70 }));
			list.Add(("p127b", "상대 도루 시도 ×0.80 + 도루저지 ×1.50", new Effect { PStealTry = 0.80, PCaught = 1.50 }));
			list.Add(("pcaught:25", "도루저지 ×1.25", new Effect { PCaught = 1.25 }));
			return list;
		}

		public static void Run(Arena arena, int games, string outDir, List<(string key, string label, Effect e)> levers)
		{
			StringBuilder csv = new StringBuilder("key,label,games,win,se,rd,rs,ra,rpg,avgA,avgB,hrA,hrB,soA,soB,bbA,bbB,sbA,csA,sbB,csB,spOuts\n");
			Console.WriteLine($"{"lever",-26} {"win%",7} {"±se",5} {"RD/G",6} {"RS",5} {"RA",5} {"AVG A/B",11} {"SB-CS A",8} {"SB-CS B",8} {"SP outs",7}");
			MatchResult baseRes = arena.Play(null, null, games, 1);
			Print("기준 (특성 없음 vs 없음)", baseRes);
			Row(csv, "base", "기준", baseRes);
			ulong seed = 2;
			foreach ((string key, string label, Effect e) in levers)
			{
				MatchResult r = arena.Play(e, null, games, seed++);
				Print(label, r);
				Row(csv, key, label, r);
			}
			File.WriteAllText(Path.Combine(outDir, "units.csv"), csv.ToString(), new UTF8Encoding(true));
		}

		public static void Print(string label, MatchResult r)
		{
			double g = r.Games;
			Console.WriteLine($"{label,-26} {100 * r.WinPct,7:F2} {100 * r.WinSE,5:F2} {r.RunDiff,6:F3} {r.RsPerG,5:F2} {r.RaPerG,5:F2} "
				+ $"{r.HitsA / (double)r.AbA,5:F3}/{r.HitsB / (double)r.AbB,5:F3} {r.SbA / g,4:F2}-{r.CsA / g,4:F2} {r.SbB / g,4:F2}-{r.CsB / g,4:F2} {r.OutsSpA / (double)r.StartsA,7:F2}");
		}

		public static void Row(StringBuilder csv, string key, string label, MatchResult r)
		{
			double g = r.Games;
			csv.AppendLine(string.Join(",", key, label, r.Games, r.WinPct.ToString("F5"), r.WinSE.ToString("F5"), r.RunDiff.ToString("F4"), r.RsPerG.ToString("F4"), r.RaPerG.ToString("F4"), r.RPerG.ToString("F4"),
				(r.HitsA / (double)r.AbA).ToString("F4"), (r.HitsB / (double)r.AbB).ToString("F4"), (r.HrA / g).ToString("F4"), (r.HrB / g).ToString("F4"), (r.SoA / g).ToString("F4"), (r.SoB / g).ToString("F4"),
				(r.BbA / g).ToString("F4"), (r.BbB / g).ToString("F4"), (r.SbA / g).ToString("F4"), (r.CsA / g).ToString("F4"), (r.SbB / g).ToString("F4"), (r.CsB / g).ToString("F4"), (r.OutsSpA / (double)r.StartsA).ToString("F3")));
		}
	}
}
