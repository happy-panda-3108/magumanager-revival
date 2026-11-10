using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Ma9MSim;

namespace Ma9MServer
{
	// eSituation / eSituation_special / eTypeofPitch values the client replays.
	internal static class Sit
	{
		public const byte LookingSO = 1, FourBall = 2, DeadBall = 3, SwingSO = 4, GroundOut = 5, FlyOut = 6, LineDrive = 7,
			Single = 8, Double = 9, Triple = 10, HomeRun = 11, DoublePlay = 13, Steal = 14, Bunt = 15, Error = 16;

		public const byte SpGameStart = 1, SpChangePitcher = 3, SpChange = 4, SpGameEnd = 5;

		public static readonly string[] PitchCodes = { "FAST", "CURT", "KNUKL", "HSLID", "JSLID", "SLID", "CURV", "SCURV", "FORK", "SFF", "SINK", "CHAG", "CCHAG", "TSEAM" };

		// km/h taken off the fastball for each pitch type.
		public static readonly int[] SpeedOff = { 0, 5, 35, 8, 9, 10, 22, 30, 10, 8, 4, 14, 15, 2 };
	}

	// Turns the engine's plays into the client's SimulResult list (the broadcast buff): every
	// plate appearance with its pitch count, last pitch, location and a ball preset matching the
	// play, plus game start, half-inning changes, pitching changes and game end.
	public sealed class BroadcastRecorder : IGameRecorder
	{
		private readonly List<byte[]> m_scenes = new List<byte[]>();

		// Fielding engine: the 3D script of each scene's ball in play (null for the others). Old
		// clients read only the scene count's records, so the scripts ride behind them.
		private readonly List<byte[]> m_scripts = new List<byte[]>();

		private readonly BallPresetCatalog m_presets;

		private readonly Random m_rnd;

		private SeasonModel m_model;

		private GameTeam[] m_teams;

		public BroadcastRecorder(BallPresetCatalog presets, int seed)
		{
			m_presets = presets;
			m_rnd = new Random(seed);
			m_scenes.Add(new byte[] { Sit.SpGameStart, 0 });
			m_scripts.Add(null);
		}

		public void Attach(SeasonModel model, GameTeam home, GameTeam away)
		{
			m_model = model;
			m_teams = new[] { home, away };
		}

		public void HalfInningStart(int inning, bool bottom)
		{
		}

		public void HalfInningEnd(int inning, bool bottom)
		{
			m_scenes.Add(new byte[] { Sit.SpChange, 0 });
			m_scripts.Add(null);
		}

		public void PitcherChange(int team, int pitcher)
		{
			int slot = m_model.Decks[team].SlotOf(pitcher);
			m_scenes.Add(new[] { Sit.SpChangePitcher, (byte)Math.Max(0, slot) });
			m_scripts.Add(null);
		}

		// The finished buffer: drops the change after the last half-inning, ends the game.
		public byte[] Finish()
		{
			if (m_scenes.Count > 0 && m_scenes[^1][0] == Sit.SpChange)
			{
				m_scenes.RemoveAt(m_scenes.Count - 1);
				m_scripts.RemoveAt(m_scripts.Count - 1);
			}
			m_scenes.Add(new byte[] { Sit.SpGameEnd, 0 });
			m_scripts.Add(null);
			if (m_scenes.Count > 255)
			{
				// The client reads a one-byte scene count; a 12-inning slugfest could pass it.
				m_scenes.RemoveRange(254, m_scenes.Count - 255);
				m_scripts.RemoveRange(254, m_scripts.Count - 255);
			}
			List<byte> buff = new List<byte> { (byte)m_scenes.Count };
			foreach (byte[] s in m_scenes)
			{
				buff.AddRange(s);
			}
			AppendScripts(buff);
			return buff.ToArray();
		}

		// The scripts block: deflated [scene count][per scene: ushort length + PlayScript bytes], then a
		// footer of its offset (int) and "M9S2", so a client finds it from the end of the buffer.
		public const string ScriptMagic = "M9S2";

		private void AppendScripts(List<byte> buff)
		{
			if (!m_scripts.Any(x => x != null))
			{
				return;
			}
			using MemoryStream raw = new MemoryStream();
			using (BinaryWriter w = new BinaryWriter(raw, System.Text.Encoding.ASCII, true))
			{
				w.Write((byte)m_scenes.Count);
				for (int i = 0; i < m_scenes.Count; i++)
				{
					byte[] sc = i < m_scripts.Count ? m_scripts[i] : null;
					w.Write((ushort)(sc?.Length ?? 0));
					if (sc != null) w.Write(sc);
				}
			}
			byte[] packed;
			using (MemoryStream z = new MemoryStream())
			{
				using (DeflateStream d = new DeflateStream(z, CompressionLevel.Optimal, true))
				{
					d.Write(raw.GetBuffer(), 0, (int)raw.Length);
				}
				packed = z.ToArray();
			}
			int offset = buff.Count;
			buff.AddRange(BitConverter.GetBytes((int)raw.Length));
			buff.AddRange(BitConverter.GetBytes(packed.Length));
			buff.AddRange(packed);
			buff.AddRange(BitConverter.GetBytes(offset));
			buff.AddRange(System.Text.Encoding.ASCII.GetBytes(ScriptMagic));
			ScriptBytes = packed.Length;
		}

		// The scripts block of a finished buffer, one entry per scene (null where none), or null if
		// the buffer has none: what a client does with it.
		public static List<byte[]> ReadScripts(byte[] buff)
		{
			if (buff.Length < 8 || System.Text.Encoding.ASCII.GetString(buff, buff.Length - 4, 4) != ScriptMagic)
			{
				return null;
			}
			int offset = BitConverter.ToInt32(buff, buff.Length - 8);
			int rawLen = BitConverter.ToInt32(buff, offset), packedLen = BitConverter.ToInt32(buff, offset + 4);
			byte[] raw = new byte[rawLen];
			using (DeflateStream d = new DeflateStream(new MemoryStream(buff, offset + 8, packedLen), CompressionMode.Decompress))
			{
				int got = 0;
				while (got < rawLen)
				{
					int n = d.Read(raw, got, rawLen - got);
					if (n <= 0) break;
					got += n;
				}
			}
			List<byte[]> list = new List<byte[]>();
			using BinaryReader r = new BinaryReader(new MemoryStream(raw));
			int scenes = r.ReadByte();
			for (int i = 0; i < scenes; i++)
			{
				int len = r.ReadUInt16();
				list.Add(len > 0 ? r.ReadBytes(len) : null);
			}
			return list;
		}

		// Each batter's line in a finished buffer, [team (0 away, 1 home), batting order 1-9, stat (0 AB,
		// 1 H, 2 HR, 3 RBI)], by the rules the client counts them with during the broadcast
		// (BattingLine, DataCenter.Ma9MBatterToday): a game already in the season rows can be taken out
		// of the numbers shown before it (user, 2026-10-30: the batter panel's 타율·홈런·타점, live).
		public static int[,,] BatterLines(byte[] buff)
		{
			int[,,] t = new int[2, 10, 4];
			int ptr = 0, count = buff[ptr++];
			bool homeBats = false;
			int[] line = new int[4];
			for (int n = 0; n < count && ptr < buff.Length; n++)
			{
				byte sp = buff[ptr++];
				if (sp != 0)
				{
					ptr++;
					if (sp == Sit.SpChange) homeBats = !homeBats;
					continue;
				}
				byte sit = buff[ptr];
				byte[] order = { buff[ptr + 7], buff[ptr + 8], buff[ptr + 9], buff[ptr + 10] };
				byte[] next = { buff[ptr + 11], buff[ptr + 12], buff[ptr + 13], buff[ptr + 14] };
				ptr += 16 + buff[ptr + 15];
				if (order[0] < 1 || order[0] > 9) continue;
				Array.Clear(line);
				BattingLine(sit, order, next, line);
				for (int k = 0; k < 4; k++) t[homeBats ? 1 : 0, order[0], k] += line[k];
			}
			return t;
		}

		// One scene for the batter at the plate: at-bat (not on a walk, hit-by-pitch, sacrifice fly or
		// bunt), hit, homer, and the runners who scored on it (none on a double play or an error).
		public static void BattingLine(byte sit, byte[] order, byte[] next, int[] line)
		{
			int runs = 0;
			for (int k = 1; k < 4; k++) if (order[k] != 0 && next[k] == 4) runs++;
			switch (sit)
			{
				case Sit.LookingSO:
				case Sit.SwingSO:
				case Sit.DoublePlay:
				case Sit.Error:
					line[0]++;
					break;
				case Sit.GroundOut:
				case Sit.LineDrive:
				case 17:   // ST_POTENSKILL_HIGH_JUMP_CATCH
					line[0]++;
					line[3] += runs;
					break;
				case Sit.FlyOut:
					if (runs == 0) line[0]++;   // with a run in, a sacrifice fly
					line[3] += runs;
					break;
				case Sit.Bunt:
					line[3] += runs;
					break;
				case Sit.Single:
				case Sit.Double:
				case Sit.Triple:
					line[0]++;
					line[1]++;
					line[3] += runs;
					break;
				case Sit.HomeRun:
					line[0]++;
					line[1]++;
					line[2]++;
					line[3] += runs + 1;
					break;
			}
		}

		// Size of the packed scripts in the last Finish() (tests).
		public int ScriptBytes { get; private set; }

		public int SceneCount => m_scenes.Count;

		// Scenes worth a dev look: homers by kind ("hr4" wall-scraper, "hr5" no-doubter).
		public readonly List<(int scene, string tag)> Events = new List<(int, string)>();

		// Pitches thrown by model pitcher (the scenes' pitch counts), for the league's per-game lines
		// (선수 정보 > 성적 > 최근성적 NP; systems session, 2026-10-19).
		public readonly Dictionary<int, int> PitchesBy = new Dictionary<int, int>();

		// The game ended in a bottom half with the home side ahead: the last play was a walk-off.
		// Call before Finish(): every half played still has its change, so an even count means the
		// last half was a bottom one. Scene of that play, else -1.
		public int WalkOffScene(int homeRuns, int awayRuns)
		{
			int changes = m_scenes.Count(s => s[0] == Sit.SpChange);
			if (homeRuns <= awayRuns || changes % 2 != 0)
			{
				return -1;
			}
			for (int i = m_scenes.Count - 1; i >= 0; i--)
			{
				if (m_scenes[i][0] == 0)
				{
					return i;
				}
			}
			return -1;
		}

		public void Play(PlayInfo p)
		{
			GameCard pitcher = PitcherCard(p.Pitcher);
			GameCard batter = BatterCard(p.Batter);
			bool lefty = batter.Bats == Hand.L || (batter.Bats == Hand.S && pitcher.Throws == Hand.R);
			byte sit;
			BallPreset preset = null;
			int np;
			bool ballLast = false;
			switch (p.Kind)
			{
				case PlayKind.Strikeout:
					sit = m_rnd.NextDouble() < 0.25 ? Sit.LookingSO : Sit.SwingSO;
					np = Pick(new[] { 3, 4, 5, 6, 7, 8, 9 }, new[] { 12, 22, 24, 20, 12, 6, 4 });
					break;
				case PlayKind.Walk:
					sit = Sit.FourBall;
					np = Pick(new[] { 4, 5, 6, 7, 8, 9 }, new[] { 12, 25, 30, 18, 10, 5 });
					ballLast = true;
					break;
				case PlayKind.HitByPitch:
					sit = Sit.DeadBall;
					np = 1 + m_rnd.Next(6);
					ballLast = true;
					break;
				case PlayKind.StealSafe:
				case PlayKind.StealOut:
					sit = Sit.Steal;
					np = 1;
					break;
				default:
					(sit, preset) = BattedBall(p, lefty);
					np = Pick(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new[] { 15, 17, 17, 16, 14, 11, 7, 3 });
					break;
			}
			// The engine's own count (it drives fatigue and the pull); steals have none.
			if (p.Pitches > 0)
			{
				np = p.Pitches;
			}
			byte pitchType = 0;
			if (p.PitchSlot >= 0 && p.PitchSlot < pitcher.Row.Pitches.Count)
			{
				pitchType = (byte)Math.Max(0, Array.IndexOf(Sit.PitchCodes, pitcher.Row.Pitches[p.PitchSlot].code));
			}
			// A real repertoire (Statiz 2013+) carries each pitch's own speed; 구속 gained by development adds 0.36 km/h a
			// point on top. Before 2013, the fastball from 구속 and each type's step down from it.
			string pcode = p.PitchSlot >= 0 && p.PitchSlot < pitcher.Row.Pitches.Count ? pitcher.Row.Pitches[p.PitchSlot].code : null;
			int speed = pcode != null && pitcher.Row.PitchUse != null && pitcher.Row.PitchUse.TryGetValue(pcode, out var use) && use.kmh > 0
				? (int)Math.Round(use.kmh + (pitcher.Row.R[1] - pitcher.Row.BaseVelo) * 0.36 + m_rnd.Next(-2, 3))
				: (int)Math.Round(143 + (pitcher.Row.R[1] - 65) * 0.36 + m_rnd.Next(-2, 3)) - Sit.SpeedOff[pitchType];
			int zx, zy;
			if (ballLast)
			{
				zx = m_rnd.Next(2) == 0 ? -m_rnd.Next(4, 7) : m_rnd.Next(4, 7);
				zy = m_rnd.Next(-6, 7);
			}
			else
			{
				zx = m_rnd.Next(-3, 4);
				zy = m_rnd.Next(-3, 4);
			}
			byte zone = (byte)((zx + 7) + (zy + 7) * 16);
			short code = preset?.Code ?? (short)0;
			if (sit == Sit.HomeRun && preset != null)
			{
				Events.Add((m_scenes.Count, preset.HitType == 4 ? "hr4" : "hr5"));
			}
			if (sit == Sit.Bunt && preset != null)
			{
				Events.Add((m_scenes.Count, preset.Chase == FieldPos.B1 ? "bunt1b" : "bunt"));
			}
			// An extra-base hit to the outfield with runners on: the relay throw home.
			if ((sit == Sit.Double || sit == Sit.Triple) && (p.Before[1] >= 0 || p.Before[2] >= 0))
			{
				Events.Add((m_scenes.Count, "relay"));
			}

			byte[] s = new byte[17];
			s[0] = 0;
			s[1] = sit;
			s[2] = (byte)Math.Clamp(speed, 80, 170);
			s[3] = (byte)np;
			PitchesBy[p.Pitcher] = PitchesBy.GetValueOrDefault(p.Pitcher) + np;
			s[4] = pitchType;
			s[5] = zone;
			BitConverter.GetBytes(code).CopyTo(s, 6);
			for (int i = 0; i < 4; i++)
			{
				s[8 + i] = p.Before[i] < 0 ? (byte)0 : (byte)(p.Before[i] % 9 + 1);
				s[12 + i] = (byte)p.Dest[i];
			}
			s[16] = 0; // no potential-skill users
			m_scenes.Add(s);
			m_scripts.Add(p.Script);
		}

		private GameCard BatterCard(int model) => m_teams[m_model.BatterTeam[model]].Order[model % 9];

		private GameCard PitcherCard(int model)
		{
			for (int t = 0; t < 2; t++)
			{
				int slot = m_model.Decks[t].SlotOf(model);
				if (slot >= 0)
				{
					return m_teams[t].Pitchers[slot];
				}
			}
			throw new InvalidOperationException($"pitcher {model} is in no deck");
		}

		private int Pick(int[] values, int[] weights)
		{
			int roll = m_rnd.Next(weights.Sum());
			for (int i = 0; i < values.Length; i++)
			{
				roll -= weights[i];
				if (roll < 0)
				{
					return values[i];
				}
			}
			return values[^1];
		}

		private T Pick<T>(params (T value, int weight)[] options) => options[PickIndex(options.Select(o => o.weight).ToArray())].value;

		private int PickIndex(int[] weights)
		{
			int roll = m_rnd.Next(weights.Sum());
			for (int i = 0; i < weights.Length; i++)
			{
				roll -= weights[i];
				if (roll < 0)
				{
					return i;
				}
			}
			return weights.Length - 1;
		}

		// Where the ball went: pull side for the batter's hand, then a preset of that kind there.
		private (byte sit, BallPreset preset) BattedBall(PlayInfo p, bool lefty)
		{
			double u = m_rnd.NextDouble();
			BallPreset P(PresetKind kind, int key) => m_presets.Pick(kind, key, u);
			// Pull, centre, opposite field as fielder sides: (third-base side, first-base side).
			FieldPos Pull(FieldPos left, FieldPos right) => lefty ? right : left;
			FieldPos Opp(FieldPos left, FieldPos right) => lefty ? left : right;
			switch (p.Kind)
			{
				case PlayKind.GroundOut:
				case PlayKind.BuntForce:
				{
					FieldPos f = p.Kind == PlayKind.BuntForce
						? Pick((FieldPos.P, 4), (FieldPos.B3, 3), (FieldPos.B1, 3))
						: Pick((Pull(FieldPos.B3, FieldPos.B1), 20), (Pull(FieldPos.SS, FieldPos.B2), 30), (Opp(FieldPos.SS, FieldPos.B2), 22), (Opp(FieldPos.B3, FieldPos.B1), 12), (FieldPos.P, 16));
					return (Sit.GroundOut, P(PresetKind.GroundOut, (int)f));
				}
				case PlayKind.DoublePlay:
				{
					FieldPos f = Pick((Pull(FieldPos.SS, FieldPos.B2), 35), (Opp(FieldPos.SS, FieldPos.B2), 28), (Pull(FieldPos.B3, FieldPos.B1), 15), (FieldPos.P, 12), (Opp(FieldPos.B3, FieldPos.B1), 10));
					return (Sit.DoublePlay, P(PresetKind.DoublePlay, (int)f));
				}
				case PlayKind.FlyOut:
				{
					FieldPos f = Pick((Pull(FieldPos.LF, FieldPos.RF), 38), (FieldPos.CF, 37), (Opp(FieldPos.LF, FieldPos.RF), 25));
					return (Sit.FlyOut, P(PresetKind.FlyOut, (int)f));
				}
				case PlayKind.SacFly:
				{
					FieldPos f = Pick((Pull(FieldPos.LF, FieldPos.RF), 35), (FieldPos.CF, 40), (Opp(FieldPos.LF, FieldPos.RF), 25));
					return (Sit.FlyOut, P(PresetKind.SacFly, (int)f));
				}
				case PlayKind.LineOut:
				{
					if (m_rnd.NextDouble() < 0.5)
					{
						FieldPos f = Pick((FieldPos.C, 12), (FieldPos.B1, 18), (FieldPos.B2, 20), (FieldPos.SS, 20), (FieldPos.B3, 20), (FieldPos.P, 10));
						return (Sit.FlyOut, P(PresetKind.FlyOut, (int)f));
					}
					FieldPos l = Pick((FieldPos.P, 6), (FieldPos.B1, 10), (FieldPos.B2, 12), (FieldPos.SS, 14), (FieldPos.B3, 10), (FieldPos.LF, 16), (FieldPos.CF, 18), (FieldPos.RF, 14));
					return (Sit.LineDrive, P(PresetKind.LineDrive, (int)l));
				}
				case PlayKind.Error:
				{
					// The engine names the infielder (deck 1-4 = 1B, 2B, 3B, SS); replays without one pick by the batter's pull side.
					FieldPos f = p.ErrorFielder switch
					{
						1 => FieldPos.B1,
						2 => FieldPos.B2,
						3 => FieldPos.B3,
						4 => FieldPos.SS,
						_ => Pick((Pull(FieldPos.B3, FieldPos.B1), 25), (Pull(FieldPos.SS, FieldPos.B2), 35), (Opp(FieldPos.SS, FieldPos.B2), 25), (Opp(FieldPos.B3, FieldPos.B1), 15)),
					};
					return (Sit.Error, P(PresetKind.Error, (int)f));
				}
				case PlayKind.SacBunt:
				{
					FieldPos f = Pick((FieldPos.P, 4), (FieldPos.B3, 3), (FieldPos.B1, 3), (FieldPos.C, 1));
					return (Sit.Bunt, P(PresetKind.Bunt, (int)f));
				}
				case PlayKind.BuntHit:
					return (Sit.Single, P(PresetKind.InfieldHit, (int)BatterRecord.InfieldHit3B));
				case PlayKind.Single:
				{
					if (m_rnd.NextDouble() < 0.08)
					{
						BatterRecord inf = Pick((lefty ? BatterRecord.InfieldHit2B : BatterRecord.InfieldHitSS, 40), (lefty ? BatterRecord.InfieldHit1B : BatterRecord.InfieldHit3B, 30),
							(lefty ? BatterRecord.InfieldHitSS : BatterRecord.InfieldHit2B, 30));
						return (Sit.Single, P(PresetKind.InfieldHit, (int)inf));
					}
					BatterRecord r = Pick((lefty ? BatterRecord.SingleRF : BatterRecord.SingleLF, 38), (BatterRecord.SingleCF, 36), (lefty ? BatterRecord.SingleLF : BatterRecord.SingleRF, 26));
					return (Sit.Single, P(PresetKind.Single, (int)r));
				}
				case PlayKind.Double:
				{
					BatterRecord r;
					if (p.Stretched)
					{
						r = BatterRecord.DoubleBetweenLC;
					}
					else if (p.Held)
					{
						r = Pick((BatterRecord.DoubleBetweenCR, 2), (BatterRecord.DoubleBackRF, 1));
					}
					else
					{
						r = lefty
							? Pick((BatterRecord.DoubleRightRF, 20), (BatterRecord.DoubleBackRF, 18), (BatterRecord.DoubleBetweenCR, 24), (BatterRecord.DoubleBackCF, 14), (BatterRecord.DoubleBetweenLC, 12), (BatterRecord.DoubleBackLF, 6), (BatterRecord.DoubleLeftLF, 6))
							: Pick((BatterRecord.DoubleLeftLF, 20), (BatterRecord.DoubleBackLF, 18), (BatterRecord.DoubleBetweenLC, 24), (BatterRecord.DoubleBackCF, 14), (BatterRecord.DoubleBetweenCR, 12), (BatterRecord.DoubleBackRF, 6), (BatterRecord.DoubleRightRF, 6));
					}
					return (Sit.Double, P(PresetKind.Double, (int)r));
				}
				case PlayKind.Triple:
				{
					BatterRecord r = lefty
						? Pick((BatterRecord.TripleRightRF, 25), (BatterRecord.TripleBetweenCR, 30), (BatterRecord.TripleBackCF, 20), (BatterRecord.TripleBetweenLC, 15), (BatterRecord.TripleBackRF, 10))
						: Pick((BatterRecord.TripleLeftLF, 25), (BatterRecord.TripleBetweenLC, 30), (BatterRecord.TripleBackCF, 20), (BatterRecord.TripleBetweenCR, 15), (BatterRecord.TripleBackRF, 10));
					return (Sit.Triple, P(PresetKind.Triple, (int)r));
				}
				case PlayKind.HomeRun:
				{
					BatterRecord r = Pick((lefty ? BatterRecord.HrRF : BatterRecord.HrLF, 50), (BatterRecord.HrCF, 30), (lefty ? BatterRecord.HrLF : BatterRecord.HrRF, 20));
					return (Sit.HomeRun, P(PresetKind.HomeRun, (int)r));
				}
			}
			return (Sit.GroundOut, P(PresetKind.GroundOut, (int)FieldPos.SS));
		}
	}
}
