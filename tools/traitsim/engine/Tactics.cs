using System;
using System.Linq;

namespace Ma9MSim
{
	// 작전실 (operation room) settings: five batting and five pitching sliders, 0-100, 50 = neutral.
	// The client sends them in this order (APPLY_OPERATION op_offense / op_defense).
	public sealed class GameTactics
	{
		public int Hitting = 50;    // 타격 성향: 0 스몰볼 .. 100 빅볼
		public int Patience = 50;   // 공격 성향: 0 빠른승부 .. 100 끈질긴승부
		public int Running = 50;    // 도루 성향: 0 소극적 .. 100 적극적
		public int Bunting = 50;    // 번트 성향: 0 번트자제 .. 100 번트권장
		public int Fielding = 50;   // 수비 성향: 0 안정적으로 .. 100 적극적으로
		public int Repertoire = 50; // 투수 성향: 0 변화구위주 .. 100 직구위주
		public int Approach = 50;   // 승부 성향: 0 빠른승부 .. 100 유인구승부
		public int Location = 50;   // 제구 성향: 0 몸쪽승부 .. 100 바깥쪽승부
		public int Effort = 50;     // 투구 성향: 0 전력투구 .. 100 완급조절
		public int Hook = 50;       // 교체 성향: 0 교체 자제 .. 100 교체 권장

		public static GameTactics From(byte[] offense, byte[] defense)
		{
			GameTactics t = new GameTactics();
			int O(int i) => offense != null && i < offense.Length ? Math.Clamp((int)offense[i], 0, 100) : 50;
			int D(int i) => defense != null && i < defense.Length ? Math.Clamp((int)defense[i], 0, 100) : 50;
			t.Hitting = O(0); t.Patience = O(1); t.Running = O(2); t.Bunting = O(3); t.Fielding = O(4);
			t.Repertoire = D(0); t.Approach = D(1); t.Location = D(2); t.Effort = D(3); t.Hook = D(4);
			return t;
		}

		public GameTactics Clone() => (GameTactics)MemberwiseClone();

		// Slider as −1 .. +1 toward its right-hand label.
		public static double S(int v) => (v - 50) / 50.0;
	}

	// How the sliders, the pitch count and fatigue play. Effects are given at the right-hand end of
	// each slider (+1); the left end is the mirror image. All of it is tuned by simulation
	// (tools/traitsim) so that no setting wins on an average roster.
	public sealed class TacticRules
	{
		// 타격: 빅볼 swings for the fences, 스몰볼 puts the ball in play and bunts more.
		public double BigHR = 0.25, BigK = 0.12, BigBabip = -0.03, BigDouble = 0.07, BigBunt = -0.6;
		// 공격: 끈질긴승부 sees more pitches (tires the pitcher), walks and strikes out more.
		public double PatPitches = 0.15, PatBB = 0.11, PatK = 0.08, PatBabip = -0.005;
		// 도루: 적극적 adds attempts for runners whose odds are good (RunGood is a sure-enough bet,
		// RunBad not worth it), 소극적 cuts attempts across the board; safe share − RunSafe·s.
		public double RunTry = 0.8, RunSafe = 0.0, RunGood = 0.78, RunBad = 0.66;
		// 번트: the base rate of sacrifice bunts moves to close late games (BuntLate×, else
		// BuntEarly×; same total), weaker hitters first; 번트권장 bunts BuntMore more there.
		public double BuntMore = 1.0, BuntLate = 2.0, BuntEarly = 0.3;
		// 수비: 적극적 takes more hits away (more so with good fielders) and makes more errors.
		public double FieldBabip = -0.013, FieldError = 0.20, FieldDefScale = 0.4;
		// 투수 성향: 직구위주 throws the main fastball more; fewer pitches and walks.
		public double FastLean = 0.8, FastPitches = -0.05, FastBB = -0.05;
		// 승부: 유인구승부 nibbles: more pitches, strikeouts and walks, weaker contact.
		// The walk cost scales with control: × (1 − BaitCtrl·x컨트롤).
		public double BaitPitches = 0.12, BaitK = 0.05, BaitBB = 0.12, BaitBabip = -0.008, BaitHR = -0.03, BaitCtrl = 0.3;
		// 제구: 바깥쪽 keeps the ball in the park and off batters, gives up more walks and singles.
		// The homer saving scales with how homer-prone he is: × (1 − OutTech·x테크닉).
		public double OutHR = -0.05, OutBB = 0.04, OutBabip = 0.01, OutHbp = -0.5, OutK = -0.03, OutTech = 0.3;
		// 투구: 전력투구 (left end) plays every stat EffortStat higher and tires EffortCost faster.
		public double EffortStat = 0.01, EffortCost = 0.30;

		// Pitch limit from 체력 (the card's stat scale is outs per game: a regular starter sits near
		// 80, a reliever near 46): 110 pitches at 80, 1.8 per point.
		public double LimitAt80 = 110, LimitPerPoint = 1.8, LimitMin = 15, LimitMax = 170;
		// Fatigue from FatigueFrom of the limit: every stat FatigueSlope lower per limit share
		// (−10% at the limit), FatigueOver per share past it, at most FatigueMax.
		public double FatigueFrom = 0.8, FatigueSlope = 0.5, FatigueOver = 1.0, FatigueMax = 0.25;
		// Pitches a manager expects one inning to take.
		public double InningPitches = 16.5;
		// Between games (league days): fatigue left = units thrown, recovering RecoverShare of the
		// limit per day; a reliever above AvailShare of his limit is kept out (if anyone else can go).
		public double RecoverShare = 0.30, AvailShare = 0.5;

		public double Limit(double stamina) => Math.Clamp(LimitAt80 + LimitPerPoint * (stamina - 80), LimitMin, LimitMax);

		public double Fatigue(double count, double limit)
		{
			double f = count / limit;
			return Math.Min(FatigueMax, FatigueSlope * Math.Clamp(f - FatigueFrom, 0, 1 - FatigueFrom) + FatigueOver * Math.Max(0, f - 1));
		}
	}

	// Per-game tactics as the engine reads them: each player's effective settings (his own 개인작전
	// or the team's) and every pitcher's limit.
	public sealed class TacticModel
	{
		public TacticRules R = new TacticRules();
		public GameTactics[] Team = new GameTactics[2];
		public double[] BatHit, BatPat, BatRun, BatBunt;   // per batter, −1..+1
		public double[] PitRep, PitApp, PitLoc, PitEff;    // per pitcher, −1..+1 (PitEff +1 = 전력투구)
		public double[] Limit;                             // per pitcher, pitches
		public double[] CarryIn;                           // per pitcher, fatigue units left from earlier games

		public bool Rested(int p) => CarryIn[p] <= R.AvailShare * Limit[p];

		// Fatigue units a pitcher left in this game (pitches × effort cost).
		public double Units(int p, long pitches) => pitches * Cost(p);
		public double[] CtrlX, TechX;                      // per pitcher: (컨트롤 − 65) / 10, (테크닉 − 65) / 10
		public double[] TeamDefX = new double[2];          // per team: fielders' mean defence, (x − 65) / 10
		public double[] BuntWeak;                          // per batter: weaker hitters bunt first (mean ≈ 1)

		public double FieldOf(int team) => GameTactics.S(Team[team].Fielding);
		public double HookOf(int team) => Team[team].Hook / 100.0;

		// Pitch "cost" for fatigue: 전력투구 tires faster.
		public double Cost(int pitcher) => 1 + R.EffortCost * PitEff[pitcher];

		// Every stat of the pitcher up or down by this share: effort minus fatigue.
		public double PitcherBoost(int pitcher, double count) => R.EffortStat * PitEff[pitcher] - R.Fatigue(count, Limit[pitcher]);

		// Rate multipliers of one matchup from the batter's, the pitcher's and the fielders' settings.
		public void Shift(int batter, int pitcher, int fieldTeam, ref double k, ref double bb, ref double hr, ref double babip, ref double dbl, ref double hbp)
		{
			double b = BatHit[batter], a = BatPat[batter], u = PitApp[pitcher], c = PitLoc[pitcher], q = PitRep[pitcher], f = FieldOf(fieldTeam);
			double ctrl = Math.Clamp(1 - R.BaitCtrl * CtrlX[pitcher], 0.3, 2), tech = Math.Clamp(1 - R.OutTech * TechX[pitcher], 0.3, 2);
			double def = Math.Clamp(1 + R.FieldDefScale * TeamDefX[fieldTeam], 0.3, 2);
			k *= (1 + R.BigK * b) * (1 + R.PatK * a) * (1 + R.BaitK * u) * (1 + R.OutK * c);
			bb *= (1 + R.PatBB * a) * Math.Max(0.2, 1 + R.BaitBB * u * ctrl) * (1 + R.OutBB * c) * (1 + R.FastBB * q);
			hr *= (1 + R.BigHR * b) * (1 + R.BaitHR * u) * Math.Max(0.2, 1 + R.OutHR * c * tech);
			babip *= (1 + R.BigBabip * b) * (1 + R.PatBabip * a) * (1 + R.BaitBabip * u) * (1 + R.OutBabip * c) * (1 + R.FieldBabip * f * def);
			dbl *= 1 + R.BigDouble * b;
			hbp *= Math.Max(0.1, 1 + R.OutHbp * c);
		}

		public double PitchMult(int batter, int pitcher) =>
			(1 + R.PatPitches * BatPat[batter]) * (1 + R.BaitPitches * PitApp[pitcher]) * (1 + R.FastPitches * PitRep[pitcher]);

		public double StealTry(int runner, double safe)
		{
			double s = BatRun[runner];
			if (s <= 0) return Math.Max(0, 1 + R.RunTry * s);
			return 1 + R.RunTry * s * Math.Clamp((safe - R.RunBad) / (R.RunGood - R.RunBad), 0, 1.5);
		}

		public double StealSafeShift(int runner) => -R.RunSafe * BatRun[runner];

		public double BuntMult(int batter, bool lateClose)
		{
			double where = lateClose ? R.BuntLate : R.BuntEarly;
			double more = BatBunt[batter] > 0 ? R.BuntMore * BatBunt[batter] * (lateClose ? 1 : 0.2) : BatBunt[batter];
			return Math.Max(0, where * BuntWeak[batter] * (1 + more) + R.BigBunt * BatHit[batter]);
		}

		public double ErrorMult(int fieldTeam) => Math.Max(0.2, 1 + R.FieldError * FieldOf(fieldTeam));

		// Pitches in one plate appearance from its result (same shapes the broadcast used), stretched
		// or squeezed by patience and approach: only the pitches beyond the minimum scale.
		// ev: 0 K, 1 BB, 2 HBP, 3 HR, 4-6 hit, 7 out, 8 error, 9 sac fly, -1 bunt.
		// Means: strikeout 4.84, walk 5.71, ball in play 3.45 (≈ 3.9 pitches per plate appearance).
		private static readonly int[] KVals = { 3, 4, 5, 6, 7, 8, 9 }, KW = { 20, 27, 24, 15, 8, 4, 2 };
		private static readonly int[] BBVals = { 4, 5, 6, 7, 8, 9 }, BBW = { 20, 27, 28, 15, 7, 3 };
		private static readonly int[] InVals = { 1, 2, 3, 4, 5, 6, 7, 8 }, InW = { 17, 19, 19, 17, 12, 9, 5, 2 };

		public static int Draw(int ev, double mult, double u1, double u2)
		{
			int n, min;
			switch (ev)
			{
				case 0: n = Pick(KVals, KW, u1); min = 3; break;
				case 1: n = Pick(BBVals, BBW, u1); min = 4; break;
				case 2: n = 1 + (int)(u1 * 6); min = 1; break;
				case -1: n = 1 + (int)(u1 * 3); min = 1; break;
				default: n = Pick(InVals, InW, u1); min = 1; break;
			}
			double x = min + (n - min) * mult;
			int whole = (int)Math.Floor(x);
			return Math.Max(min, whole + (u2 < x - whole ? 1 : 0));
		}

		private static int Pick(int[] vals, int[] w, double u)
		{
			int total = w.Sum();
			double acc = 0;
			for (int i = 0; i < vals.Length; i++)
			{
				acc += w[i] / (double)total;
				if (u < acc) return vals[i];
			}
			return vals[^1];
		}
	}
}
