using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// What games are played with: the calibrated rules and reference world, every card's ratings
	// and hidden profile (data/sim, written by the simulator's card test), and the ball presets.
	public sealed class GameData
	{
		public GameEnv Env;

		public BallPresetCatalog Presets;

		public CardCatalog Catalog;

		private Dictionary<(char, int, int), CardRow> m_rows;

		public static GameData Load(string dataDir, CardCatalog catalog)
		{
			string simDir = Path.Combine(dataDir, "sim");
			GameData d = new GameData { Catalog = catalog };
			d.Env = CardGame.LoadEnv(simDir);
			d.m_rows = CardGame.LoadCards(simDir);
			d.Presets = BallPresetCatalog.Build(Path.Combine(dataDir, "fences"));
			// The client reads ball presets from the table bundle.
			d.Presets.WriteTable(Path.Combine(dataDir, "tables", "BallPreset.txt"));
			int missing = catalog.All.Count(c => d.Row(c) == null);
			ServerLog.Info($"game data: {d.m_rows.Count} card ratings ({missing} table cards without one), {d.Presets.All.Count} ball presets for parks {string.Join(", ", d.Presets.FenceNames.Distinct())}");
			if (Ma9MSim.Fielding.FieldEngine.On)
			{
				// The fielding engine's batted-ball library (120,000 a hand, seeded, so games stay the same) was built on
				// its first use: inside the first game after a restart, on the packet loop, which stalled every client
				// (2026-11-02: 14 s in this Debug build, the user's phone's login waited 13 s). Built here, beside it.
				new System.Threading.Thread(() =>
				{
					System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
					_ = Ma9MSim.Fielding.BipLibrary.Shared;
					ServerLog.Info($"fielding library ready ({watch.Elapsed.TotalSeconds:F1} s)");
				}) { IsBackground = true, Name = "fielding library" }.Start();
			}
			return d;
		}

		private CardRow Row(CardInfo info) =>
			m_rows.TryGetValue((info.IsBatter ? 'B' : 'P', info.Sidx, info.Year), out CardRow row) ? row : null;

		// condition: eCondition (0 best .. 4 worst).
		public GameCard Card(int idx, byte condition)
		{
			CardInfo info = Catalog.Get(idx);
			if (info == null)
			{
				return null;
			}
			CardRow row = Row(info) ?? FromTable(info);
			return new GameCard
			{
				Row = row,
				Bats = info.HitHand == 2 ? Hand.L : info.HitHand == 3 ? Hand.S : Hand.R,
				Throws = info.ThrowHand == 2 ? Hand.L : Hand.R,
				LowSlot = !info.IsBatter && info.ThrowType >= 2,
				Condition = ConditionShare(condition)
			};
		}

		// eCondition → stat share: 최상 +10%, 상 +5%, 보통 0, 하 -5%, 최하 -10%.
		public static double ConditionShare(byte condition) => condition switch { 0 => 0.10, 1 => 0.05, 3 => -0.05, 4 => -0.10, _ => 0 };

		// A card the simulator never rated (should not happen): its table stats, no hidden profile.
		private static CardRow FromTable(CardInfo info)
		{
			CardRow r = new CardRow { Kind = info.IsBatter ? 'B' : 'P', Year = info.Year, Sidx = info.Sidx, Name = info.Name, Share = 1, Weight = 0 };
			if (info.IsBatter)
			{
				// Table order con, pow, spd, eye, mental → sim order 컨택 파워 선구 주력 정신 수비.
				int[] s = info.Stats;
				r.R = new double[] { s[0], s[1], s[3], s[2], s[4], info.Defence.Max() };
				r.MainPos = (int)info.MainPosition;
				for (int p = 0; p < 8; p++)
				{
					r.Defs[p] = info.Defence[p];
				}
			}
			else
			{
				r.R = info.Stats.Select(v => (double)v).ToArray();
			}
			r.Raw = (double[])r.R.Clone();
			r.RawDefs = (double[])r.Defs.Clone();
			return r;
		}
	}
}
