using System;
using System.Collections.Generic;
using System.IO;

namespace Ma9MSim.Fielding
{
	using P = Geometry.P;

	// A ball in play as the 3D broadcast should stage it: one list of timed pieces for the ball, the
	// nine fielders and the runners. Times are seconds from contact, points in the broadcast frame
	// (Geometry). Built by Choreo from the engine's play; the client only plays it back.
	public enum SegKind : byte
	{
		// The ball. BallFly: the batted ball's flight from contact (Ev/La/H of the script) to T1.
		BallFly = 1, BallRoll = 2, BallHeld = 3, BallThrow = 4, BallLoose = 5,
		// Fielders (Actor 0-8, deck positions; 8 the pitcher).
		Move = 10, Catch = 11, Dive = 12, Throw = 13, Cover = 14, Tag = 15, Wait = 16,
		// Runners (Actor 20 + slot: 20 the batter, 21-23 the runners from first, second, third).
		Run = 20, Slide = 21, Out = 22, Safe = 23, Score = 24, Back = 25, Stand = 26, Peel = 27
	}

	// Catch styles (Seg.Style of Catch / Dive).
	public static class CatchStyle
	{
		public const byte Fly = 0, Running = 1, Diving = 2, WallLeap = 3, Liner = 4, Grounder = 5, DiveStop = 6, Charging = 7, Pickup = 8, Receive = 9, DiveMiss = 10, Bobble = 11;
	}

	public struct Seg
	{
		public SegKind Kind;
		public byte Actor, Arg, Style;
		public float T0, T1;
		public P P0, P1;

		public override string ToString() => $"{Kind,-9} a{Actor,-2} {T0,5:F2}-{T1,5:F2} {P0}->{P1} arg{Arg} st{Style}";
	}

	public sealed class PlayScript
	{
		public const byte Version = 1;
		public BattedBall Ball;
		public float End;
		public readonly List<Seg> Segs = new List<Seg>(32);

		public void Add(SegKind kind, int actor, double t0, double t1, P p0, P p1, int arg = 0, int style = 0)
		{
			Segs.Add(new Seg { Kind = kind, Actor = (byte)actor, T0 = (float)t0, T1 = (float)t1, P0 = p0, P1 = p1, Arg = (byte)arg, Style = (byte)style });
			if (t1 > End) End = (float)t1;
		}

		// Binary form for the broadcast: version, ball (Ev cm/s, La and H in 0.1°), end (ms), segment
		// count, then per segment kind, actor, arg, style, t0/t1 (ms), p0/p1 (x, z in cm).
		public byte[] Encode()
		{
			using MemoryStream ms = new MemoryStream(32 + Segs.Count * 16);
			using BinaryWriter w = new BinaryWriter(ms);
			w.Write(Version);
			w.Write((ushort)Math.Clamp(Ball.Ev * 100, 0, 65535));
			w.Write((short)Math.Clamp(Ball.La * 10, -32000, 32000));
			w.Write((short)Math.Clamp(Ball.H * 10, -32000, 32000));
			w.Write(Ms(End));
			w.Write((byte)Math.Min(255, Segs.Count));
			for (int i = 0; i < Math.Min(255, Segs.Count); i++)
			{
				Seg s = Segs[i];
				w.Write((byte)s.Kind); w.Write(s.Actor); w.Write(s.Arg); w.Write(s.Style);
				w.Write(Ms(s.T0)); w.Write(Ms(s.T1));
				w.Write(Cm(s.P0.X)); w.Write(Cm(s.P0.Z)); w.Write(Cm(s.P1.X)); w.Write(Cm(s.P1.Z));
			}
			return ms.ToArray();
		}

		private static ushort Ms(double t) => (ushort)Math.Clamp(Math.Round(t * 1000), 0, 65535);

		private static short Cm(double x) => (short)Math.Clamp(Math.Round(x * 100), -32000, 32000);

		public static PlayScript Decode(byte[] data)
		{
			using BinaryReader r = new BinaryReader(new MemoryStream(data));
			PlayScript s = new PlayScript();
			if (r.ReadByte() != Version) return null;
			s.Ball = new BattedBall { Ev = r.ReadUInt16() / 100f, La = r.ReadInt16() / 10f, H = r.ReadInt16() / 10f };
			s.End = r.ReadUInt16() / 1000f;
			int n = r.ReadByte();
			for (int i = 0; i < n; i++)
			{
				Seg g = new Seg { Kind = (SegKind)r.ReadByte(), Actor = r.ReadByte(), Arg = r.ReadByte(), Style = r.ReadByte() };
				g.T0 = r.ReadUInt16() / 1000f; g.T1 = r.ReadUInt16() / 1000f;
				g.P0 = new P(r.ReadInt16() / 100.0, r.ReadInt16() / 100.0);
				g.P1 = new P(r.ReadInt16() / 100.0, r.ReadInt16() / 100.0);
				s.Segs.Add(g);
			}
			return s;
		}
	}
}
