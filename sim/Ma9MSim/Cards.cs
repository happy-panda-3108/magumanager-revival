using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Ma9MSim
{
	public enum Hand { R, L, S }

	// Pitch families share batted-ball and whiff tendencies.
	public enum PitchFamily { Fastball, Sinker, Cutter, Slider, Curve, Split, Change, Knuckle }

	public sealed class PitchSlot
	{
		public int Type;          // StuffInfoTable index
		public string Name;
		public PitchFamily Family;
		public double Rating;
		public bool TwoSeam;      // a fastball for usage purposes, though it plays like a sinker
		public double Usage;      // his real share of this pitch (Statiz 2013+); 0 = unknown, the usage rule decides
	}

	public sealed class Thrower
	{
		public Hand Throws;
		public bool LowSlot;      // sidearm / submarine
	}

	// only per-player traits the records lack.
	public sealed class CardData
	{
		public readonly Dictionary<int, Hand> Bats = new Dictionary<int, Hand>();
		public readonly Dictionary<int, Thrower> Throws = new Dictionary<int, Thrower>();
		public readonly Dictionary<(int sidx, int year), PitchSlot[]> Pitches = new Dictionary<(int, int), PitchSlot[]>();
		public readonly Dictionary<(int sidx, int year), int> BatterPosition = new Dictionary<(int, int), int>();

		public Hand BatsOf(int sidx)
		{
			return Bats.TryGetValue(sidx, out Hand h) ? h : Hand.R;
		}

		public Thrower ThrowsOf(int sidx)
		{
			return Throws.TryGetValue(sidx, out Thrower t) ? t : new Thrower { Throws = Hand.R };
		}

		public PitchSlot[] PitchesOf(int sidx, int year)
		{
			if (Pitches.TryGetValue((sidx, year), out PitchSlot[] p))
			{
				return p;
			}
			// Nearest season's card of the same pitcher, else a plain fastball/slider pitcher.
			for (int d = 1; d <= 5; d++)
			{
				if (Pitches.TryGetValue((sidx, year - d), out p) || Pitches.TryGetValue((sidx, year + d), out p))
				{
					return p;
				}
			}
			return null;
		}

		public static string NewPitchers;   // data/new_pitchers.tsv (set by Program from --root)

		public static CardData Load(string tablesDir)
		{
			CardData data = new CardData();
			string path = Path.Combine(tablesDir, "playertable.txt");
			if (!File.Exists(path))
			{
				return data;
			}
			Dictionary<int, string> stuffNames = LoadStuffNames(Path.Combine(tablesDir, "StuffInfoTable.txt"));
			Dictionary<int, Dictionary<Hand, int>> bats = new Dictionary<int, Dictionary<Hand, int>>();
			Dictionary<int, Dictionary<int, int>> throws = new Dictionary<int, Dictionary<int, int>>();
			Dictionary<(int, int), (double spread, double mean, PitchSlot[] slots)> best = new Dictionary<(int, int), (double, double, PitchSlot[])>();
			Dictionary<(int, int), Dictionary<int, int>> positions = new Dictionary<(int, int), Dictionary<int, int>>();

			foreach (string[] r in Records.ReadTsv(path).Skip(3))
			{
				if (r.Length < 49 || !int.TryParse(r[1].Trim(), out int sidx) || !int.TryParse(r[7].Trim(), out int pos)
					|| !int.TryParse(r[44].Trim(), out int year) || !int.TryParse(r[13].Trim(), out int main))
				{
					continue;
				}
				if (pos >= 10)
				{
					Count(throws, sidx, main);
					List<PitchSlot> slots = new List<PitchSlot>();
					for (int k = 0; k < 6; k++)
					{
						if (int.TryParse(r[24 + 3 * k].Trim(), out int type) && type > 0
							&& double.TryParse(r[26 + 3 * k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double rating) && rating > 0)
						{
							string name = stuffNames.TryGetValue(type, out string n) ? n : r[25 + 3 * k].Trim();
							slots.Add(new PitchSlot { Type = type, Name = name, Family = Classify(name), Rating = rating, TwoSeam = name.Contains("투심") });
						}
					}
					if (slots.Count == 0)
					{
						continue;
					}
					// Several cards exist per pitcher-season; flat "custom level" cards (every pitch
					// the same) carry no repertoire information. Keep the most differentiated card.
					double mean = slots.Average(s => s.Rating);
					double spread = slots.Max(s => s.Rating) - slots.Min(s => s.Rating);
					if (!best.TryGetValue((sidx, year), out var cur) || spread > cur.spread || (spread == cur.spread && mean > cur.mean))
					{
						best[(sidx, year)] = (spread, mean, slots.ToArray());
					}
				}
				else
				{
					Hand h = main == 2 ? Hand.L : main == 3 ? Hand.S : Hand.R;
					if (!bats.TryGetValue(sidx, out Dictionary<Hand, int> c))
					{
						c = new Dictionary<Hand, int>();
						bats[sidx] = c;
					}
					c[h] = c.TryGetValue(h, out int v) ? v + 1 : 1;
					Count(positions, (sidx, year), pos);
				}
			}
			foreach (var kv in bats)
			{
				data.Bats[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key;
			}
			// 우타 / 좌타 / 양타): only where no card spoke.
			string batExtra = NewPitchers == null ? null : Path.Combine(Path.GetDirectoryName(NewPitchers), "batter_hands_extra.tsv");
			if (batExtra != null && File.Exists(batExtra))
			{
				string[][] rows = Records.ReadTsv(batExtra).ToArray();
				int si = Array.IndexOf(rows[0], "sidx"), bi = Array.IndexOf(rows[0], "bats");
				foreach (string[] r in rows.Skip(1))
				{
					if (si < 0 || bi < 0 || r.Length <= Math.Max(si, bi) || !int.TryParse(r[si], out int sidx) || data.Bats.ContainsKey(sidx))
					{
						continue;
					}
					string b = r[bi];
					if (b.Contains("양타")) data.Bats[sidx] = Hand.S;
					else if (b.Contains("좌타")) data.Bats[sidx] = Hand.L;
					else if (b.Contains("우타")) data.Bats[sidx] = Hand.R;
				}
			}
			foreach (var kv in throws)
			{
				int code = kv.Value.OrderByDescending(x => x.Value).First().Key;
				// 4 overhand R, 5 sidearm R, 6 submarine R, 7 overhand L, 8 sidearm L, 9 submarine L.
				data.Throws[kv.Key] = new Thrower { Throws = code >= 7 ? Hand.L : Hand.R, LowSlot = code == 5 || code == 6 || code == 8 || code == 9 };
			}
			// data/pitcher_hands_extra.tsv (systems session, looked up by name; arm as 우완 … 좌언더) first, then new_pitchers.tsv.
			string[] armNames = { "우완", "우사이드", "우언더", "좌완", "좌사이드", "좌언더" };
			foreach (string file in new[] { NewPitchers == null ? null : Path.Combine(Path.GetDirectoryName(NewPitchers), "pitcher_hands_extra.tsv"), NewPitchers })
			{
				if (file == null || !File.Exists(file))
				{
					continue;
				}
				string[][] rows = Records.ReadTsv(file).ToArray();
				int si = Array.IndexOf(rows[0], "sidx"), ai = Array.IndexOf(rows[0], "arm");
				foreach (string[] r in rows.Skip(1))
				{
					if (r.Length <= Math.Max(si, ai) || !int.TryParse(r[si], out int sidx) || data.Throws.ContainsKey(sidx))
					{
						continue;
					}
					int code = int.TryParse(r[ai], out int c) ? c : Array.IndexOf(armNames, r[ai].Trim()) + 4;
					if (code >= 4 && code <= 9)
					{
						data.Throws[sidx] = new Thrower { Throws = code >= 7 ? Hand.L : Hand.R, LowSlot = code == 5 || code == 6 || code == 8 || code == 9 };
					}
				}
			}
			foreach (var kv in best)
			{
				data.Pitches[kv.Key] = kv.Value.slots;
			}
			foreach (var kv in positions)
			{
				data.BatterPosition[kv.Key] = kv.Value.OrderByDescending(x => x.Value).First().Key;
			}
			return data;
		}

		private static void Count<TKey>(Dictionary<TKey, Dictionary<int, int>> map, TKey key, int value)
		{
			if (!map.TryGetValue(key, out Dictionary<int, int> c))
			{
				c = new Dictionary<int, int>();
				map[key] = c;
			}
			c[value] = c.TryGetValue(value, out int v) ? v + 1 : 1;
		}

		private static Dictionary<int, string> LoadStuffNames(string path)
		{
			Dictionary<int, string> names = new Dictionary<int, string>();
			if (!File.Exists(path))
			{
				return names;
			}
			foreach (string[] r in Records.ReadTsv(path).Skip(1))
			{
				if (r.Length > 2 && int.TryParse(r[0].Trim(), out int idx))
				{
					names[idx] = r[2].Trim();
				}
			}
			return names;
		}

		// By name; signature pitches carry their base pitch in the name (e.g. "DY슬라", "HJ체").
		public static PitchFamily Classify(string name)
		{
			string n = name ?? "";
			if (n.Contains("커브") || n.Contains("슬러브") || n.Contains("슬럽") || n.Contains("파커") || n.Contains("슬커"))
			{
				return PitchFamily.Curve;
			}
			if (n.Contains("슬라") || n.Contains("스위퍼") || n.Contains("슬슬"))
			{
				return PitchFamily.Slider;
			}
			if (n.Contains("포크") || n.Contains("SF") || n.Contains("스플"))
			{
				return PitchFamily.Split;
			}
			if (n.Contains("체인지") || n.Contains("CH") || n.Contains("첸접") || n.Contains("팜볼") || n.Contains("스크") || n.EndsWith("체"))
			{
				return PitchFamily.Change;
			}
			if (n.Contains("싱커") || n.Contains("투심") || n.Contains("브투"))
			{
				return PitchFamily.Sinker;
			}
			if (n.Contains("커터"))
			{
				return PitchFamily.Cutter;
			}
			if (n.Contains("너클"))
			{
				return PitchFamily.Knuckle;
			}
			return PitchFamily.Fastball;
		}
	}
}
