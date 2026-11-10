using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using UnityEngine;

// Ma9M port (engine session, 2026-10-30): the server's fielding engine sends, behind the broadcast's
// SimulResult records, a script of every ball in play: the ball's path, each fielder's runs, catches,
// throws, covers and tags, each runner's legs, slides and calls (server: Ma9MSim.Fielding.PlayScript,
// BroadcastRecorder.AppendScripts). Old clients never read past the records. This file holds the
// script data, the block reader and the batted-ball flight; Ma9MScriptPlayer plays a script back.
public static class Ma9MScripts
{
	public const string Magic = "M9S2";

	private static List<Ma9MPlayScript> s_scenes;

	// Reads the scripts block of a broadcast buffer (or forgets the last one if there is none).
	private static Vector3[] s_originalLeads;

	private static void SetLeads(bool engine)
	{
		if (s_originalLeads == null) s_originalLeads = (Vector3[])POSITION_INFO.LeadPosition.Clone();
		if (!engine)
		{
			s_originalLeads.CopyTo(POSITION_INFO.LeadPosition, 0);
			return;
		}
		POSITION_INFO.LeadPosition[1] = new Vector3(27.43f, 0f, 3.5f);
		POSITION_INFO.LeadPosition[2] = new Vector3(22.93f, 0f, 27.43f);
		POSITION_INFO.LeadPosition[3] = new Vector3(0f, 0f, 24.93f);
	}

	public static void Load(byte[] buff)
	{
		s_scenes = null;
		SetLeads(false);
		try
		{
			if (buff == null || buff.Length < 8 || System.Text.Encoding.ASCII.GetString(buff, buff.Length - 4, 4) != Magic)
			{
				return;
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
			List<Ma9MPlayScript> list = new List<Ma9MPlayScript>();
			using (BinaryReader r = new BinaryReader(new MemoryStream(raw)))
			{
				int scenes = r.ReadByte();
				for (int i = 0; i < scenes; i++)
				{
					int len = r.ReadUInt16();
					list.Add(len > 0 ? Ma9MPlayScript.Decode(r.ReadBytes(len)) : null);
				}
			}
			s_scenes = list;
			Debug.Log("[Ma9MScripts] " + list.FindAll(x => x != null).Count + " scripted plays");
			// The fielding engine's runners lead off by its own distances (the timing of every play is
			// worked out from them); the original stood them 8 m off every base.
			SetLeads(true);
		}
		catch (Exception ex)
		{
			Debug.LogWarning("[Ma9MScripts] could not read the scripts block: " + ex.Message);
			s_scenes = null;
		}
	}

	// The script of scene index n (the SimulResult's index), or null.
	public static Ma9MPlayScript For(int scene)
	{
		if (s_scenes == null || scene < 0 || scene >= s_scenes.Count) return null;
		return s_scenes[scene];
	}

	// Dev: "-ma9mscriptfile <file|dir>[,…]" plays those scripts (sim --fieldlab script --export /
	// --exportdir) in the scripted scenes instead of the game's own: each scene takes the least
	// played one whose runners are on base there (Ma9MScriptPlayer.TryBegin), to see every kind of
	// play in one game.
	public static readonly List<(string name, Ma9MPlayScript script)> Overrides = ReadOverrides();

	private static List<(string, Ma9MPlayScript)> ReadOverrides()
	{
		string[] args = Environment.GetCommandLineArgs();
		int i = Array.IndexOf(args, "-ma9mscriptfile");
		if (i < 0 || i + 1 >= args.Length) return null;
		List<(string, Ma9MPlayScript)> list = new List<(string, Ma9MPlayScript)>();
		foreach (string part in args[i + 1].Split(','))
		{
			try
			{
				string[] files = Directory.Exists(part) ? Directory.GetFiles(part, "*.bin") : new[] { part };
				Array.Sort(files);
				foreach (string f in files)
				{
					Ma9MPlayScript s = Ma9MPlayScript.Decode(File.ReadAllBytes(f));
					if (s != null) list.Add((Path.GetFileNameWithoutExtension(f), s));
				}
			}
			catch (Exception ex)
			{
				Debug.LogWarning("[Ma9MScripts] " + ex.Message);
			}
		}
		Debug.Log("[Ma9MScripts] " + list.Count + " test scripts");
		return list.Count > 0 ? list : null;
	}

	// Switch for comparisons: "-ma9mnoscript" plays every scene the old way.
	public static readonly bool Disabled = Array.IndexOf(Environment.GetCommandLineArgs(), "-ma9mnoscript") >= 0;
}

public enum Ma9MSeg : byte
{
	BallFly = 1, BallRoll = 2, BallHeld = 3, BallThrow = 4, BallLoose = 5,
	Move = 10, Catch = 11, Dive = 12, Throw = 13, Cover = 14, Tag = 15, Wait = 16,
	Run = 20, Slide = 21, Out = 22, Safe = 23, Score = 24, Back = 25, Stand = 26, Peel = 27
}

public struct Ma9MPiece
{
	public Ma9MSeg Kind;
	public byte Actor, Arg, Style;
	public float T0, T1;
	public Vector2 P0, P1;   // x along the first-base line, y here = z along the third-base line (metres)

	public const int Ball = 30, RunnerBase = 20;
}

public sealed class Ma9MPlayScript
{
	public float Ev, La, H, End;
	public readonly List<Ma9MPiece> Pieces = new List<Ma9MPiece>(32);

	public static Ma9MPlayScript Decode(byte[] data)
	{
		using (BinaryReader r = new BinaryReader(new MemoryStream(data)))
		{
			if (r.ReadByte() != 1) return null;
			Ma9MPlayScript s = new Ma9MPlayScript();
			s.Ev = r.ReadUInt16() / 100f;
			s.La = r.ReadInt16() / 10f;
			s.H = r.ReadInt16() / 10f;
			s.End = r.ReadUInt16() / 1000f;
			int n = r.ReadByte();
			for (int i = 0; i < n; i++)
			{
				Ma9MPiece p = new Ma9MPiece();
				p.Kind = (Ma9MSeg)r.ReadByte();
				p.Actor = r.ReadByte();
				p.Arg = r.ReadByte();
				p.Style = r.ReadByte();
				p.T0 = r.ReadUInt16() / 1000f;
				p.T1 = r.ReadUInt16() / 1000f;
				p.P0 = new Vector2(r.ReadInt16() / 100f, r.ReadInt16() / 100f);
				p.P1 = new Vector2(r.ReadInt16() / 100f, r.ReadInt16() / 100f);
				s.Pieces.Add(p);
			}
			return s;
		}
	}
}

// The batted ball's flight with drag and backspin lift, the same model as the server's
// Ma9MSim.Fielding.Flight (constants fitted to Statcast carries), integrated once per play.
public sealed class Ma9MFlight
{
	private const float Mass = 0.145f, Radius = 0.0366f, Rho = 1.20f, Gravity = 9.81f, Cd0 = 0.36f, CdSpin = 0.15f, LiftK = 1.2f;
	private const float Dt = 0.01f;
	private readonly List<Vector2> m_path = new List<Vector2>(800);   // (horizontal distance, height) every Dt

	public float Carry { get; private set; }
	public float Hang { get; private set; }

	public Ma9MFlight(float ev, float laDeg, float startHeight = 1.0f)
	{
		float la = laDeg * Mathf.Deg2Rad;
		float x = 0, y = startHeight, vx = ev * Mathf.Cos(la), vy = ev * Mathf.Sin(la), t = 0;
		float rpm = laDeg <= 0 ? -800f : Mathf.Min(3000f, 200f + 55f * laDeg);
		float w = Mathf.Abs(rpm) * 2 * Mathf.PI / 60;
		float k0 = 0.5f * Rho * Mathf.PI * Radius * Radius / Mass;
		m_path.Add(new Vector2(x, y));
		while (t < 12f)
		{
			float sp = Mathf.Sqrt(vx * vx + vy * vy);
			float s = Radius * w / Mathf.Max(sp, 1e-3f);
			float cd = Cd0 + CdSpin * s;
			float cl = LiftK * s / (1 + 2.6f * s) * (rpm >= 0 ? 1 : -1);
			float kk = k0 * sp;
			float ax = -kk * cd * vx - kk * cl * vy;
			float ay = -kk * cd * vy + kk * cl * vx - Gravity;
			float nx = x + vx * Dt, ny = y + vy * Dt;
			vx += ax * Dt;
			vy += ay * Dt;
			t += Dt;
			if (ny < 0)
			{
				float f = y / (y - ny);
				x += (nx - x) * f;
				m_path.Add(new Vector2(x, 0));
				Hang = t - Dt + Dt * f;
				Carry = x;
				return;
			}
			x = nx;
			y = ny;
			m_path.Add(new Vector2(x, y));
		}
		Hang = t;
		Carry = x;
	}

	// (horizontal distance, height) t seconds after contact.
	public Vector2 At(float t)
	{
		if (t <= 0) return m_path[0];
		float i = t / Dt;
		int k = Mathf.FloorToInt(i);
		if (k >= m_path.Count - 1) return m_path[m_path.Count - 1];
		return Vector2.Lerp(m_path[k], m_path[k + 1], i - k);
	}
}
