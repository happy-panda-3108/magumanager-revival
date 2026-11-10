using System;

namespace Ma9MSim
{
	// A card team as the game server fields it: batting order, positions and the 11-man
	// pitching deck (SP1-5, RP1-4, SU, CP), all as indices into the SeasonModel.
	public sealed class GameDeck
	{
		public int[] Order = new int[9];          // model batter by batting slot
		public int[] At = new int[8];             // model batter at C, 1B, 2B, 3B, SS, LF, CF, RF
		public int[] Pitchers = new int[11];      // model pitcher by deck slot, -1 = empty
		public int StarterSlot;                   // deck slot of today's starter (0-4)

		public int SlotOf(int pitcher) => Array.IndexOf(Pitchers, pitcher);
	}

	public enum PlayKind
	{
		Strikeout, Walk, HitByPitch, HomeRun, Single, Double, Triple,
		GroundOut, DoublePlay, FlyOut, SacFly, LineOut, Error,
		SacBunt, BuntHit, BuntForce, StealSafe, StealOut
	}

	// One plate appearance (or steal) as it happened, for the game broadcast.
	public sealed class PlayInfo
	{
		public PlayKind Kind;
		public int Team;              // batting team (model team index)
		public int Batter;            // model batter at the plate
		public int Slot;              // his batting slot (0-8)
		public int Pitcher;           // model pitcher
		public int PitchSlot;         // deciding pitch (index into the pitcher's repertoire), -1 none
		public int OutsBefore;
		public readonly int[] Before = new int[4];  // batter, runners on 1st, 2nd, 3rd (model batter, -1 empty)
		public readonly int[] Dest = new int[4];    // eNextBase: 0 stay, 1-3 base, 4 home, 5-8 out at 1st..home
		public bool Stretched;        // a gap single stretched into a double
		public bool Held;             // a would-be triple held to a double
		public int Runs;              // runs scored on the play
		public int Pitches;           // tactics mode: pitches in this plate appearance (0 on a steal)
		public int ErrorFielder = -1; // card games, on an error: the fielder's deck position (1-4 = 1B, 2B, 3B, SS)
		// Fielding engine: Dest was set by the play itself (who was thrown out where), the batted ball
		// and the play in detail (fielder, catch, throws). Field is reused: read it during Play().
		public bool DestSet;
		public Fielding.BattedBall Ball;
		public Fielding.FieldOutcome Field;
		public byte[] Script;         // fielding engine: the play staged for the 3D broadcast (PlayScript.Encode)
	}

	public interface IGameRecorder
	{
		void HalfInningStart(int inning, bool bottom);

		void HalfInningEnd(int inning, bool bottom);

		// A relief pitcher comes in for the fielding team.
		void PitcherChange(int team, int pitcher);

		void Play(PlayInfo play);
	}
}
