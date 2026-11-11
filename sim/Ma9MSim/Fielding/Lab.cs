using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Ma9MSim.Fielding
{
	// `--fieldlab <what>` experiments for the fielding engine (no records needed):
	//   lib    the batted-ball library under a league-average defence: class shares, BABIP, types
	//   value  hits saved/allowed when one fielder's defence (or speed) moves, per 1,000 balls in play
	public static class Lab
	{
		public static int Run(string what, Dictionary<string, string> opt, Action<string> log)
		{
			Stopwatch sw = Stopwatch.StartNew();
			int per = opt.TryGetValue("per", out string p) ? int.Parse(p) : BipLibrary.DefaultPerHand;
			double O(string k, double def) => opt.TryGetValue(k, out string v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : def;
			BallPlan.AirKeep = O("airkeep", BallPlan.AirKeep);
			BallPlan.AirDecel = O("airdecel", BallPlan.AirDecel);
			BallPlan.InfieldChaseMax = O("chasemax", BallPlan.InfieldChaseMax);
			BallPlan.Go2Default = O("go2", BallPlan.Go2Default);
			BallPlan.Go3Default = O("go3", BallPlan.Go3Default);
			FielderSkill.WallBase = O("wall", FielderSkill.WallBase);
			FielderSkill.PivotBase = O("pivot", FielderSkill.PivotBase);
			Defence.OfExtra = O("ofextra", Defence.OfExtra);
			Defence.OfDecay = O("ofdecay", Defence.OfDecay);
			Resolver.SecondGoBar = O("secondbar", Resolver.SecondGoBar);
			BipLibrary lib = BipLibrary.Build(per, 0x6D61676Bul);
			log($"library: {per} balls per hand in {sw.Elapsed.TotalSeconds:F1}s");
			if (what == "lib" || what == "all")
			{
				Library(lib, log);
			}
			if (what == "value" || what == "all")
			{
				Value(lib, opt, log);
			}
			if (what == "script")
			{
				Scripts(lib, opt, log);
			}
			if (what == "couple" || what == "all")
			{
				Couple(lib, opt, log);
			}
			if (what == "plays" || what == "all")
			{
				Plays(lib, opt, log);
			}
			return 0;
		}

		// Runner situations with league-average fielders and runners, against the old engine's rates:
		// double plays, the force, going first to third, scoring from second, sacrifice flies.
		private static void Plays(BipLibrary lib, Dictionary<string, string> opt, Action<string> log)
		{
			int n = opt.TryGetValue("samples", out string s) ? int.Parse(s) : 20000;
			double Opt(string k, double def) => opt.TryGetValue(k, out string v) ? double.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : def;
			Defence.OfExtra = Opt("ofextra", Defence.OfExtra);
			Defence.OfDecay = Opt("ofdecay", Defence.OfDecay);
			Resolver.TagSd = Opt("tagsd", Resolver.TagSd);
			Resolver.LineRead = Opt("lineread", Resolver.LineRead);
			Resolver.FlyLead = Opt("flylead", Resolver.FlyLead);
			Resolver.Turn2 = Opt("turn2", Resolver.Turn2);
			Resolver.NeedShift = Opt("needshift", Resolver.NeedShift);
			Resolver.EarlyPivot = Opt("earlypivot", Resolver.EarlyPivot);
			Resolver.RelaySd = Opt("relaysd", Resolver.RelaySd);
			Resolver.Partway[1] = Opt("partway1", Resolver.Partway[1]);
			Resolver.NeedHome[0] = Opt("needhome0", Resolver.NeedHome[0]);
			Resolver.NeedHome[1] = Opt("needhome1", Resolver.NeedHome[1]);
			Resolver.NeedHome[2] = Opt("needhome2", Resolver.NeedHome[2]);
			Resolver.NeedThird[0] = Opt("needthird0", Resolver.NeedThird[0]);
			Resolver.NeedThird[1] = Opt("needthird1", Resolver.NeedThird[1]);
			FielderSkill.PivotBase = Opt("pivot", FielderSkill.PivotBase);
			Resolver.TagJump = Opt("tagjump", Resolver.TagJump);
			log($"ofextra {Defence.OfExtra} ofdecay {Defence.OfDecay} tagsd {Resolver.TagSd} lineread {Resolver.LineRead} flylead {Resolver.FlyLead} tagjump {Resolver.TagJump} turn2 {Resolver.Turn2} pivot {FielderSkill.PivotBase}");
			Defence d = Defence.Average();
			Resolver res = new Resolver { D = d };
			Dictionary<string, double> metric = new Dictionary<string, double>();
			void Case(string name, int key, int outs, bool on1, bool on2, bool on3, string tag = null, int slot = 0, int[] good = null, int[] bad2 = null)
			{
				int hitGood = 0, hitBad = 0;
				Rng rng = new Rng(77);
				Dictionary<string, int> tally = new Dictionary<string, int>();
				int bad = 0, dbg = 0;
				for (int i = 0; i < n; i++)
				{
					int hand = rng.Next() < 0.62 ? 0 : 1;
					BatterRun br = new BatterRun { R = RunnerSkill.For(Defence.RefRunX), Lefty = hand == 1 };
					BipLibrary.Entry e = lib.Draw(hand, key, rng.Next());
					BallPlan plan = BallPlan.Make(e.Ball, d, br);
					Rng nr = new Rng(e.Seed);
					BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref nr));
					res.Batter = br;
					res.OutsBefore = outs;
					res.On[1] = on1; res.On[2] = on2; res.On[3] = on3;
					for (int b = 1; b <= 3; b++) res.Run[b] = RunnerSkill.For(Defence.RefRunX);
					FieldOutcome o = res.Resolve(plan, r, ref rng);
					// Consistency: one man per base afterwards, outs within the inning.
					int[] taken = new int[4];
					if (o.Dest[0] >= 1 && o.Dest[0] <= 3) taken[o.Dest[0]]++;
					for (int b = 1; b <= 3; b++)
					{
						if (!res.On[b]) continue;
						int at = o.Dest[b] == 0 ? b : o.Dest[b];
						if (at >= 1 && at <= 3) taken[at]++;
					}
					if (taken[1] > 1 || taken[2] > 1 || taken[3] > 1 || outs + o.Outs > 3)
					{
						bad++;
						if (opt.ContainsKey("debug") && bad <= 3)
							log($"   bad {o.Kind} outs {outs}+{o.Outs} [{string.Join(",", o.Dest)}] how {r.How} f {r.Fielder} cls {r.Class} throws {string.Join(";", o.Throws.Select(t => $"{t.From}->{t.Base} r{t.Runner} out {t.Out}"))}");
					}
					if (opt.ContainsKey("debug") && o.Kind == Ma9MSim.PlayKind.LineOut && o.Dest[2] == 6 && dbg++ < 4)
					{
						log($"   dbg {e.Ball} how {r.How} f {Geometry.PosName[Math.Max(0, r.Fielder)]} T {r.T:F2} at {r.At} liner {plan.LinerF} lt {plan.LinerTime:F2} catcher {plan.Catcher} ct {plan.CatchTime:F2} doubledOff {o.DoubledOff} throws {string.Join(";", o.Throws.Select(t => $"{t.From}->{t.Base} out {t.Out} step {t.Step} at {t.Arrive:F2}"))}");
					}
					if (tag != null)
					{
						if (slot < 0 ? o.Kind == Ma9MSim.PlayKind.DoublePlay : Array.IndexOf(good, o.Dest[slot]) >= 0) hitGood++;
						if (bad2 != null && Array.IndexOf(bad2, o.Dest[slot]) >= 0) hitBad++;
					}
					string k = $"{o.Kind}" + (o.Fc ? " FC" : "") + (o.Error ? " E" : "") + $" [{string.Join(",", Enumerable.Range(0, 4).Select(b => b == 0 || res.On[b] ? o.Dest[b].ToString() : "-"))}]";
					tally[k] = tally.TryGetValue(k, out int c) ? c + 1 : 1;
				}
				if (tag != null)
				{
					metric[tag] = (double)hitGood / n;
					if (bad2 != null) metric[tag + "Out"] = (double)hitBad / n;
				}
				if (!opt.ContainsKey("quiet"))
					log($"{name}: " + string.Join("  ", tally.OrderByDescending(x => x.Value).Take(9).Select(x => $"{x.Key} {100.0 * x.Value / n:F1}%")) + (bad > 0 ? $"  ** {bad} inconsistent" : ""));
			}
			Case("GO, 1루, 0사 (구엔진 병살 42%)", BipLibrary.GroundOut, 0, true, false, false, "dp", -1);
			Case("GO, 1루, 1사", BipLibrary.GroundOut, 1, true, false, false);
			Case("GO, 1·2루, 0사", BipLibrary.GroundOut, 0, true, true, false);
			Case("GO, 만루, 1사", BipLibrary.GroundOut, 1, true, true, true);
			Case("GO, 3루, 1사 (구엔진 득점 50%)", BipLibrary.GroundOut, 1, false, false, true);
			Case("GO, 2루, 0사", BipLibrary.GroundOut, 0, false, true, false);
			Case("1B, 1루, 0사 (구엔진 3루 27%)", BipLibrary.Single, 0, true, false, false, "s1to3", 1, new[] { 3, 4 }, new[] { 6, 7 });
			Case("1B, 2루, 0사", BipLibrary.Single, 0, false, true, false, "s2h0", 2, new[] { 4 }, new[] { 7, 8 });
			Case("1B, 2루, 1사 (구엔진 홈 60%)", BipLibrary.Single, 1, false, true, false, "s2h1", 2, new[] { 4 }, new[] { 7, 8 });
			Case("1B, 2루, 2사", BipLibrary.Single, 2, false, true, false, "s2h2", 2, new[] { 4 }, new[] { 7, 8 });
			Case("2B, 1루, 1사 (구엔진 홈 42%)", BipLibrary.Double, 1, true, false, false, "d1h", 1, new[] { 4 }, new[] { 7, 8 });
			Case("FO, 3루, 1사 (구엔진 희비 60%)", BipLibrary.FlyOut, 1, false, false, true, "sf", 3, new[] { 4 }, new[] { 8 });
			Case("FO, 2루, 0사 (구엔진 3루 25%)", BipLibrary.FlyOut, 0, false, true, false, "f23", 2, new[] { 3 }, new[] { 7 });
			Case("LO, 1·2루, 0사", BipLibrary.LineOut, 0, true, true, false);
			long h1 = lib.Count[0, BipLibrary.Single] + lib.Count[1, BipLibrary.Single], h2 = lib.Count[0, BipLibrary.Double] + lib.Count[1, BipLibrary.Double], h3 = lib.Count[0, BipLibrary.Triple] + lib.Count[1, BipLibrary.Triple];
			long all = 0;
			for (int k = 0; k < BipLibrary.HomeRun; k++) all += lib.Count[0, k] + lib.Count[1, k];
			metric["lib2b"] = (double)h2 / (h1 + h2 + h3);
			metric["lib3b"] = (double)h3 / (h1 + h2 + h3);
			metric["babip"] = (double)(h1 + h2 + h3) / all;
			log("TUNE " + string.Join(" ", metric.Select(kv => $"{kv.Key}={kv.Value:F3}")));
		}

		// The coupling: with the reference fielders and runner every drawn ball must give back its
		// bucket's class; then fielders spread around the reference (sd in card points) to see how
		// much a league of uneven fielders moves the totals.
		private static void Couple(BipLibrary lib, Dictionary<string, string> opt, Action<string> log)
		{
			int n = opt.TryGetValue("samples", out string s) ? int.Parse(s) : 100000;
			long[] cnt = Enumerable.Range(0, BipLibrary.Keys - 1).Select(k => lib.Count[0, k]).ToArray();
			double tot = cnt.Sum();
			foreach (double sd in new[] { 0.0, 5.0, 10.0 })
			{
				Rng rng = new Rng(5);
				long[] intent = new long[4], real = new long[4];
				long mismatch = 0;
				for (int i = 0; i < n; i++)
				{
					Defence d = Defence.Average();
					if (sd > 0)
					{
						for (int p = 1; p < 8; p++)
						{
							double dv = Defence.RefDefence[p] + sd * BallGenerator.Normal(ref rng);
							double sv = Defence.RefSpeed[p] + sd * BallGenerator.Normal(ref rng);
							d.Set(p, (dv - 65) / 10, (sv - 65) / 10);
						}
					}
					double rx = Defence.RefRunX + (sd > 0 ? sd / 10 * BallGenerator.Normal(ref rng) : 0);
					BatterRun br = new BatterRun { R = RunnerSkill.For(rx) };
					double u = rng.Next() * tot;
					int key = 0;
					while (key < cnt.Length - 1 && u >= cnt[key]) { u -= cnt[key]; key++; }
					BipLibrary.Entry e = lib.Draw(0, key, rng.Next());
					BallPlan plan = BallPlan.Make(e.Ball, d, br);
					Rng nr = new Rng(e.Seed);
					BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref nr));
					int ic = key >= BipLibrary.Single ? key - BipLibrary.Single + 1 : 0;
					intent[ic]++;
					real[Math.Min(3, r.Class)]++;
					if (Math.Min(3, r.Class) != ic) mismatch++;
				}
				log($"spread {sd}: mismatches {100.0 * mismatch / n:F2}%  intent out/1B/2B/3B {string.Join("/", intent.Select(x => (1000.0 * x / n).ToString("F1")))}  real {string.Join("/", real.Select(x => (1000.0 * x / n).ToString("F1")))} per 1000");
			}
		}

		// Broadcast scripts for many plays in random situations, checked for things a viewer would
		// notice (ScriptCheck); "--dump N" prints the first N scripts, "--only kind" limits to a play kind.
		private static void Scripts(BipLibrary lib, Dictionary<string, string> opt, Action<string> log)
		{
			int n = opt.TryGetValue("samples", out string s) ? int.Parse(s) : 5000;
			int dump = opt.TryGetValue("dump", out string ds) ? int.Parse(ds) : 0;
			string only = opt.TryGetValue("only", out string os) ? os : null;
			Defence d = Defence.Average();
			Resolver res = new Resolver { D = d };
			Choreo ch = new Choreo();
			ScriptCheck.Report rep = new ScriptCheck.Report();
			long[] cnt = Enumerable.Range(0, BipLibrary.Keys - 1).Select(k => lib.Count[0, k]).ToArray();
			double tot = cnt.Sum();
			Rng rng = new Rng(2027);
			long bytes = 0, segs = 0;
			int shown = 0;
			Dictionary<string, int> counts = new Dictionary<string, int>();
			ScriptCheck.Report kindRep = new ScriptCheck.Report();
			int kindShown = 0;
			for (int i = 0; i < n; i++)
			{
				int hand = rng.Next() < 0.62 ? 0 : 1;
				BatterRun br = new BatterRun { R = RunnerSkill.For(Defence.RefRunX), Lefty = hand == 1 };
				double u = rng.Next() * tot;
				int key = 0;
				while (key < cnt.Length - 1 && u >= cnt[key]) { u -= cnt[key]; key++; }
				BipLibrary.Entry e = lib.Draw(hand, key, rng.Next());
				BallPlan plan = BallPlan.Make(e.Ball, d, br);
				Rng nr = new Rng(e.Seed);
				BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref nr));
				res.Batter = br;
				res.OutsBefore = (int)(rng.Next() * 3);
				int bases = (int)(rng.Next() * 8);
				for (int b = 1; b <= 3; b++) { res.On[b] = (bases & (1 << (b - 1))) != 0; res.Run[b] = RunnerSkill.For(Defence.RefRunX); }
				FieldOutcome o = res.Resolve(plan, r, ref rng);
				if (only != null && o.Kind.ToString() != only) continue;
				PlayScript sc = ch.Build(plan, r, o, d, br, res.Run, res.On);
				string tag = $"#{i} {o.Kind} {e.Ball} outs{res.OutsBefore} on{bases}";
				ScriptCheck.Check(sc, d, rep, tag);
				byte[] enc = sc.Encode();
				// --export <file>: the first play where the batter is tagged out sliding (a test script
				// the broadcast client can play in every scene: -ma9mscriptfile).
				// --exportwhat doubledoff: the first runner doubled off on a liner instead.
				// --exportdir <dir> --exportset a,b,…: the first play of each kind to <dir>/<kind>.bin.
				// A kind ending in "0" wants the bases empty (it plays in any scene of a test game).
				// "kind=N" wants exactly those bases (bit 1 first, 2 second, 4 third).
				bool Is(string what) => what.Contains('=') ? bases == int.Parse(what.Split('=')[1]) && Kind(what.Split('=')[0])
					: what.EndsWith("0") ? bases == 0 && Kind(what.Substring(0, what.Length - 1)) : Kind(what);
				bool Kind(string what) => what switch
				{
					"doubledoff" => o.DoubledOff && sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor == Choreo.RunnerActor + 1),
					"tagbatter" => sc.Segs.Any(g => g.Kind == SegKind.Tag && g.Style == 1 && g.Arg == 0) && sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor == Choreo.RunnerActor),
					"triple" => o.TriplePlay,
					"wallcatch" => r.Class == 0 && r.How == Catch.Leaping,
					"offwall" => r.Class > 0 && plan.OffWall && !plan.Ground,
					"flyerror" => o.Kind == Ma9MSim.PlayKind.Error && !plan.Ground && o.BatterOnError,
					"grounderror" => o.Kind == Ma9MSim.PlayKind.Error && plan.Ground && !o.ErrorThrow,
					"wildthrow" => o.ErrorThrow,
					"ofbobble" => o.Error && r.Class > 0 && !o.ErrorThrow && o.Kind != Ma9MSim.PlayKind.Error,
					"tagupout" => r.Class == 0 && sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor > Choreo.RunnerActor && g.Arg == 0),
					"tagupscore" => r.Class == 0 && sc.Segs.Any(g => g.Kind == SegKind.Score && g.Actor == Choreo.RunnerActor + 3) && sc.Segs.Any(g => g.Kind == SegKind.Throw),
					"homeout" => r.Class > 0 && sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor > Choreo.RunnerActor && g.Arg == 0),
					"thirdout" => r.Class > 0 && sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor > Choreo.RunnerActor && g.Arg == 3),
					"homesafe" => r.Class > 0 && sc.Segs.Any(g => g.Kind == SegKind.Safe && g.Actor > Choreo.RunnerActor && g.Arg == 0),
					"divecatch" => r.Class == 0 && r.How == Catch.Diving,
					"ofdive" => r.Class == 0 && r.How == Catch.Diving && Geometry.IsOutfielder(r.Fielder) && !o.Error,
					"flyout" => r.Class == 0 && r.How == Catch.Routine && !plan.Ground && Geometry.IsOutfielder(r.Fielder) && !o.Error,
					"runcatch" => r.Class == 0 && r.How == Catch.Running && !o.Error,
					"double" => o.Kind == Ma9MSim.PlayKind.Double && !o.Error,
					"linerdive" => r.Class == 0 && r.How == Catch.Diving && plan.Ball.Type == BallType.Line && Geometry.IsInfielder(r.Fielder) && !o.Error,
					"twoout" => o.TwoOuts && o.Outs >= 1 && !o.Error,
					"diveerror" => r.Class == 0 && r.ShowDive && o.Error && o.BatterOnError,
					"divestop" => r.How == Catch.DiveStop && o.Kind == Ma9MSim.PlayKind.GroundOut,
					"ofassist" => r.Class > 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Actor >= Geometry.LF && g.Actor <= Geometry.RF)
						&& sc.Segs.Any(g => g.Kind == SegKind.Out && g.Actor >= Choreo.RunnerActor),
					"infieldhit" => o.Kind == Ma9MSim.PlayKind.Single && plan.Ground && !r.Through,
					"dp" => o.Kind == Ma9MSim.PlayKind.DoublePlay && !o.TriplePlay,
					"dp2b" => o.Kind == Ma9MSim.PlayKind.DoublePlay && !o.TriplePlay && sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Actor == Geometry.B2 && g.Arg == Geometry.B1)
						&& sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Arg == Geometry.B2),
					"triplehit" => o.Kind == Ma9MSim.PlayKind.Triple,
					"step2" => o.Throws.Any(t => t.Step && t.Base == 2 && t.Runner == 1),
					"step2safe" => o.Throws.Any(t => t.Step && t.Base == 2 && t.Runner == 1 && !t.Out),
					"step2nodp" => o.Throws.Any(t => t.Step && t.Base == 2 && t.Runner == 1 && t.Out) && !o.Throws.Any(t => t.Base == 1) && res.OutsBefore < 2,
					// A middle infielder's grounder with a runner on first: no throw drawn by him, or his only throw to first.
					"ssnothrow" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1] && !sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Actor == r.Fielder),
					"ssnothrowok" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1] && !o.Error && !sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Actor == r.Fielder),
					"ss1b" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1] && o.Throws.Count > 0 && o.Throws[0].Base == 1,
					"ssany" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1],
					"ss1b01" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1] && res.OutsBefore < 2 && o.Throws.Count > 0 && o.Throws[0].Base == 1,
					"ssany01" => plan.Ground && !r.Through && (r.Fielder == Geometry.SS || r.Fielder == Geometry.B2) && res.On[1] && res.OutsBefore < 2,
					// A grounder's fielder holding the ball half a second or more between his catch and his throw.
					"hold5" => plan.Ground && !r.Through && r.Fielder >= 0 && Hold(sc, r.Fielder) >= 0.5,
					"hold8" => plan.Ground && !r.Through && r.Fielder >= 0 && Hold(sc, r.Fielder) >= 0.8,
					"gthrow" => plan.Ground && !r.Through && r.Fielder >= 0 && Hold(sc, r.Fielder) >= 0,
					"step1" => o.Throws.Any(t => t.Step && t.Base == 1 && t.From == Geometry.B1),
					"ofhomelate" => r.Class > 0 && o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && Geometry.IsOutfielder(g.Actor) && g.Arg == Geometry.C),
					"bootthrow" => o.Error && plan.Ground && o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Actor == r.Fielder),
					"ofhome2" => bases == 2 && r.Class > 0 && o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && Geometry.IsOutfielder(g.Actor) && g.Arg == Geometry.C),
					"boot1two" => bases == 1 && res.OutsBefore == 2 && o.Error && plan.Ground && !o.ErrorThrow,
					"drop2" => o.Kind == Ma9MSim.PlayKind.Error && !plan.Ground && o.BatterOnError && res.OutsBefore == 2 && bases != 0,
					"latethrow" => o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && (g.Actor == Geometry.SS || g.Actor == Geometry.B2)),
					"latehome" => o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Arg == Geometry.C),
					"latesacfly" => o.SacFly && o.Throws.Count == 0 && sc.Segs.Any(g => g.Kind == SegKind.Throw && g.Arg == Geometry.C),
					// The runner from second thrown at third after the throw to first (Resolver.SecondOnThrowToFirst, 2026-11-11).
					"second3" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && o.Throws.Any(t => t.Base == 3 && t.Runner == 2),
					"second3out" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && o.Throws.Any(t => t.Base == 3 && t.Runner == 2 && t.Out),
					"second3safe" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && o.Throws.Any(t => t.Base == 3 && t.Runner == 2 && !t.Out),
					"second3hit" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && !o.Throws[0].Out && o.Throws.Any(t => t.Base == 3 && t.Runner == 2),
					// The runner from third thrown at home after the throw to first (Resolver.ThirdOnThrowToFirst).
					"third0" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && o.Throws.Any(t => t.Base == 0 && t.Runner == 3),
					"third0hit" => o.Throws.Count >= 2 && o.Throws[0].Base == 1 && !o.Throws[0].Out && o.Throws.Any(t => t.Base == 0 && t.Runner == 3),
					_ => false
				};
				if (opt.TryGetValue("export", out string ex) && Is(opt.TryGetValue("exportwhat", out string what) ? what : "tagbatter"))
				{
					System.IO.File.WriteAllBytes(ex, enc);
					log($"exported {tag} ({enc.Length} B) to {ex}");
					foreach (Seg g in sc.Segs.OrderBy(g => g.T0)) log("    " + Actor(g.Actor) + " " + g);
					opt.Remove("export");
				}
				if (opt.TryGetValue("exportdir", out string exDir) && opt.TryGetValue("exportset", out string set))
				{
					foreach (string kind in set.Split(','))
					{
						string file = System.IO.Path.Combine(exDir, kind + ".bin");
						if (kind.Length == 0 || System.IO.File.Exists(file) || !Is(kind)) continue;
						System.IO.File.WriteAllBytes(file, enc);
						log($"exported {kind}: {tag} dest [{string.Join(",", o.Dest)}] ({enc.Length} B)");
					}
				}
				if (opt.TryGetValue("count", out string countKinds) && !string.IsNullOrEmpty(countKinds))
				{
					foreach (string kind in countKinds.Split(','))
					{
						if (Is(kind)) counts[kind] = counts.TryGetValue(kind, out int cc) ? cc + 1 : 1;
					}
				}
				// --checkkind kind: the checks again for that kind's plays alone, and the first flagged scripts printed.
				if (opt.TryGetValue("checkkind", out string ck) && Is(ck))
				{
					ScriptCheck.Report one = new ScriptCheck.Report();
					ScriptCheck.Check(sc, d, one, tag);
					ScriptCheck.Check(sc, d, kindRep, tag);
					if (one.Count.Count > 0 && kindShown < 3)
					{
						kindShown++;
						log($"--- {ck} flagged {string.Join(", ", one.Count.Keys)}: {tag} dest [{string.Join(",", o.Dest)}]");
						foreach (Seg g in sc.Segs.OrderBy(g => g.T0)) log("    " + Actor(g.Actor) + " " + g);
					}
				}
				bytes += enc.Length;
				segs += sc.Segs.Count;
				if (shown < dump)
				{
					shown++;
					log($"--- {tag} dest [{string.Join(",", o.Dest)}] hit {o.Hit} how {r.How} dive {r.ShowDive} end {sc.End:F2} ({enc.Length} B)");
					foreach (Seg g in sc.Segs.OrderBy(g => g.T0)) log("    " + Actor(g.Actor) + " " + g);
				}
			}
			log($"{rep.Plays} plays: {(double)segs / Math.Max(1, rep.Plays):F1} pieces, {(double)bytes / Math.Max(1, rep.Plays):F0} bytes a play");
			foreach (var kv in counts) log($"  count {kv.Key}: {kv.Value} ({100.0 * kv.Value / Math.Max(1, rep.Plays):F2}%)");
			foreach (var kv in kindRep.Count.OrderByDescending(x => x.Value))
			{
				log($"  [{opt["checkkind"]} only] {kv.Key}: {kv.Value}");
				foreach (string ex in kindRep.Examples[kv.Key]) log("      " + ex);
			}
			foreach (var kv in rep.Count.OrderByDescending(x => x.Value))
			{
				log($"  {kv.Key}: {kv.Value} ({100.0 * kv.Value / rep.Plays:F1}%)");
				foreach (string ex in rep.Examples[kv.Key]) log("      " + ex);
			}
		}

		// Seconds from a fielder's catch to his first throw (-1 none).
		private static double Hold(PlayScript sc, int f)
		{
			double caught = sc.Segs.Where(g => g.Kind == SegKind.Catch && g.Actor == f).Select(g => (double)g.T0).DefaultIfEmpty(-1).Min();
			double thrown = sc.Segs.Where(g => g.Kind == SegKind.Throw && g.Actor == f).Select(g => (double)g.T0).DefaultIfEmpty(-1).Min();
			return caught < 0 || thrown < 0 ? -1 : thrown - caught;
		}

		private static string Actor(int a) => a == 30 ? "ball" : a >= Choreo.RunnerActor ? $"R{a - Choreo.RunnerActor}  " : $"{Geometry.PosName[a],-4}";

		private static void Library(BipLibrary lib, Action<string> log)
		{
			for (int hand = 0; hand < 2; hand++)
			{
				long all = 0;
				for (int k = 0; k < BipLibrary.Keys; k++) all += lib.Count[hand, k];
				long bip = all - lib.Count[hand, BipLibrary.HomeRun];
				long hits = lib.Count[hand, BipLibrary.Single] + lib.Count[hand, BipLibrary.Double] + lib.Count[hand, BipLibrary.Triple];
				log($"hand {(hand == 0 ? "R" : "L")}: HR {100.0 * lib.Count[hand, BipLibrary.HomeRun] / all:F1}% of contact, BABIP {(double)hits / bip:F3}");
				log("  " + string.Join("  ", Enumerable.Range(0, BipLibrary.Keys - 1).Select(k => $"{BipLibrary.KeyName[k]} {100.0 * lib.Count[hand, k] / bip:F1}%")));
				log($"  hits: 1B {100.0 * lib.Count[hand, BipLibrary.Single] / hits:F1}% 2B {100.0 * lib.Count[hand, BipLibrary.Double] / hits:F1}% 3B {100.0 * lib.Count[hand, BipLibrary.Triple] / hits:F1}%");
			}
			// Details of a right-handed sample: types, BABIP by type, catches, infield hits.
			Defence avg = Defence.Average();
			BatterRun br = new BatterRun { R = RunnerSkill.For(Defence.RefRunX) };
			BallGenerator gen = new BallGenerator();
			Rng rng = new Rng(99);
			int n = 60000;
			int[] typeN = new int[4], typeH = new int[4];
			Dictionary<Catch, int> how = new Dictionary<Catch, int>();
			int ifh = 0, hitsAll = 0, doveMiss = 0, offWall = 0, outStretch = 0, through = 0, wallCatch = 0, nBip = 0;
			int[] linerY = new int[4];
			double[] evSum = new double[4];
			for (int i = 0; i < n; i++)
			{
				BattedBall b = gen.Next(ref rng);
				BallPlan plan = BallPlan.Make(b, avg, br);
				if (plan.HomeRun) continue;
				nBip++;
				BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref rng));
				int t = (int)b.Type;
				typeN[t]++;
				evSum[t] += b.Mph;
				if (r.Class > 0) { typeH[t]++; hitsAll++; }
				if (r.InfieldHit) ifh++;
				if (r.DoveMissed) doveMiss++;
				if (r.OffWall) offWall++;
				if (r.OutStretching > 0) outStretch++;
				if (r.Through) through++;
				if (r.Class == 0) how[r.How] = how.TryGetValue(r.How, out int c) ? c + 1 : 1;
				if (r.Class == 0 && r.How == Catch.Leaping) wallCatch++;
				if (r.Class == 0 && plan.LinerF >= 0 && r.Fielder == plan.LinerF && r.T == plan.LinerTime)
				{
					// An infielder's snag of a liner: how high it was as it reached him (above BallPlan.StandReachY only with a leap).
					double y = Flight.At(b.Ev, b.La, plan.LinerPoint.R).height;
					linerY[y <= 1.6 ? 0 : y <= BallPlan.StandReachY ? 1 : y <= BallPlan.LinerReachY ? 2 : 3]++;
				}
			}
			log($"R sample {nBip} BIP: " + string.Join("  ", Enumerable.Range(0, 4).Select(t => $"{(BallType)t} {100.0 * typeN[t] / nBip:F1}% (BABIP {(double)typeH[t] / Math.Max(1, typeN[t]):F3}, {evSum[t] / Math.Max(1, typeN[t]):F0}mph)")));
			log($"  infield hits {100.0 * ifh / Math.Max(1, hitsAll):F1}% of hits, dove and missed {1000.0 * doveMiss / nBip:F1}/1000, off the wall {1000.0 * offWall / nBip:F1}/1000, thrown out stretching {1000.0 * outStretch / nBip:F1}/1000");
			log("  outs by catch: " + string.Join("  ", how.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {100.0 * x.Value / Math.Max(1, nBip - hitsAll):F1}%")));
			log($"  infield liner snags per 1000 BIP by height: <=1.6m {1000.0 * linerY[0] / nBip:F1}  <={BallPlan.StandReachY}m {1000.0 * linerY[1] / nBip:F1}  <={BallPlan.LinerReachY}m {1000.0 * linerY[2] / nBip:F1}  higher {1000.0 * linerY[3] / nBip:F1}; wall catches {1000.0 * wallCatch / nBip:F1}");
		}

		// Hits allowed per 1,000 balls in play when the defence at one position moves from 65 to
		// each level (all other fielders average), using the library's coupling: intent drawn from
		// the league mix, replayed by the changed defence with the same luck.
		private static void Value(BipLibrary lib, Dictionary<string, string> opt, Action<string> log)
		{
			int samples = opt.TryGetValue("samples", out string s) ? int.Parse(s) : 200000;
			if (opt.TryGetValue("ofrange", out string ofr)) FielderSkill.OfRange = double.Parse(ofr);
			if (opt.TryGetValue("ifrange", out string ifr)) FielderSkill.IfRange = double.Parse(ifr);
			if (opt.TryGetValue("ofarm", out string ofa)) FielderSkill.OfArm = double.Parse(ofa);
			log($"range scales: outfield {FielderSkill.OfRange} (arm {FielderSkill.OfArm}), infield {FielderSkill.IfRange}");
			double[] levels = { 40, 55, 65, 75, 85, 95, 110 };
			int[] positions = { Geometry.B1, Geometry.B2, Geometry.SS, Geometry.B3, Geometry.LF, Geometry.CF, Geometry.RF };
			// Runs per batter-hit class over an out (linear weights) and balls in play a team faces a season.
			double[] runOver = { 0, 0.74, 1.04, 1.31 };
			const double seasonBip = 3900, rightShare = 0.62;
			long[][] cnt = new long[2][];
			double[] tot = new double[2];
			for (int h = 0; h < 2; h++)
			{
				cnt[h] = Enumerable.Range(0, BipLibrary.Keys - 1).Select(k => lib.Count[h, k]).ToArray();
				tot[h] = cnt[h].Sum();
			}
			foreach (int pos in positions)
			{
				List<string> row = new List<string>();
				double baseRuns = 0;
				double[] runsAt = new double[levels.Length];
				for (int li = 0; li < levels.Length; li++)
				{
					double lv = levels[li];
					Defence d = Defence.Average();
					double x = (lv - 65) / 10;
					double xs = (Defence.RefSpeed[pos] - 65) / 10 + (lv - Defence.RefDefence[pos]) / 10 * (Geometry.IsOutfielder(pos) ? 0.5 : 0.3);
					d.Set(pos, x, xs);
					Rng rng = new Rng(4242);
					double hits = 0, runs = 0;
					for (int i = 0; i < samples; i++)
					{
						int hand = rng.Next() < rightShare ? 0 : 1;
						BatterRun br = new BatterRun { R = RunnerSkill.For(Defence.RefRunX), Lefty = hand == 1 };
						double u = rng.Next() * tot[hand];
						int key = 0;
						while (key < cnt[hand].Length - 1 && u >= cnt[hand][key]) { u -= cnt[hand][key]; key++; }
						BipLibrary.Entry e = lib.Draw(hand, key, rng.Next());
						BallPlan plan = BallPlan.Make(e.Ball, d, br);
						Rng nr = new Rng(e.Seed);
						BallPlan.Result r = plan.Realize(BallPlan.Noise.Draw(ref nr));
						if (r.Class > 0) { hits++; runs += runOver[Math.Min(3, r.Class)]; }
					}
					runsAt[li] = runs / samples * seasonBip;
					row.Add($"{lv:F0}: {1000.0 * hits / samples:F1}");
				}
				baseRuns = runsAt[Array.IndexOf(levels, 75.0)];
				log($"{Geometry.PosName[pos],-3} hits/1000  " + string.Join("  ", row) + "  | runs a season vs 75: " + string.Join(" ", levels.Select((lv, i) => $"{lv:F0}:{baseRuns - runsAt[i]:+0.0;-0.0}")));
			}
		}
	}
}
