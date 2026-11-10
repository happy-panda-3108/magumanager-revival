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
		// 80, a reliever near 46): 110 pitches at 80, 1.8 per point below it. Above 80 the gain tapers
		// toward LimitTop (user, 2026-10-19: development, team colours and 체력 traits can lift a
		// starter's 체력 far past 100). The 선발한계 trait adds on top, up to LimitMax; no pitcher is
		// left in past PullMax pitches, whatever the 교체 성향 (user, 2026-10-30: a 157-pitch complete game
		// "감독이 사람이 아니거나 투수가 철인 괴물" — at the limit a reliever comes in, even with one out to go
		// in the ninth; was 160).
		public double LimitAt80 = 110, LimitPerPoint = 1.8, LimitMin = 15, LimitTop = 140, LimitMax = 150, PullMax = 130;
		// How far 교체 성향 moves the pull point (자제 +HookStretch … 권장 −HookStretch) and how much a
		// blowout stretches it (were 0.15 and 0.10).
		public double HookStretch = 0.10, BlowoutStretch = 0.05;
		// Fatigue from FatigueFrom of the limit: every stat FatigueSlope lower per limit share
		// (−10% at the limit), FatigueOver per share past it, at most FatigueMax.
		public double FatigueFrom = 0.8, FatigueSlope = 0.5, FatigueOver = 1.0, FatigueMax = 0.25;
		// Pitches a manager expects one inning to take.
		public double InningPitches = 16.5;
		// A starter does not start an inning he would finish past his limit or past NewInningCap pitches thrown, and not
		// after 5 runs, nor after 4 once past ShakyTired of his limit (user, 2026-11-01: "6이닝동안 5점이나 준 선발이
		// 110구나 던졌는데 투수를 안 바꿔주고 무사 1, 3루 만들고 얻어터진 다음에 바꿔주냐고"; he went out for the 7th
		// whenever he could finish it under the limit, 131 at 체력 100, and stayed in to 7 runs).
		public double NewInningCap = 120;
		// Relief outings (user, 2026-10-31: "불펜투수가 2이닝에 71개를 던졌는데 다른 투수로 안 바꿔주는 로직이 너무 불쾌함"):
		// the pitches a reliever is there for by role — CP, SU, RP1 and RP2 an inning (Short), anyone else in a normal
		// game Middle, RP3 eating innings after an early exit Long, RP4 in a lopsided game MopUp — moved by the 교체 성향
		// like the limit. He comes out there, and does not start another inning past ReliefNewInning of it.
		public double BudgetShort = 30, BudgetMiddle = 40, BudgetLong = 60, BudgetMopUp = 50, ReliefNewInning = 0.6;
		// 승부처 (Clutch.Leverage, mean 1) from BigSpot on, the 5th inning on and not lopsided: a starter who lets the
		// inning get away (runs this half plus men on reach ShakyTraffic, one fewer once he is past ShakyTired of his
		// limit) comes out, a reliever at RelieverShaky when a rested arm is better by ShakyMargin runs a batter, and
		// the arm that comes in is the best rested one (the set-up man too, the closer from the ninth).
		public double BigSpot = 1.6, ShakyTired = 0.75, ShakyMargin = 0.006;
		public int ShakyTraffic = 3, RelieverShaky = 2;
		// A reliever being hit comes out (user, 2026-11-10: "얻어맞고 있는 투수를 현실감 있게 내리고 다른 투수를 사용하는
		// 것이 맞음"; a set-up man had stayed for 5 runs in a third of an inning, a pen gave up 8 in an inning): at
		// ReliefRuns runs charged to him (was 4 anywhere), or once ReliefReached of the batters he faced this half-inning
		// are not out (0 = off); the 교체 성향 moves both by one. A man who came into a lopsided game (4 or more behind,
		// 7 or more either way) keeps ReliefRunsLopsided and no reached rule: he is there to eat innings.
		// On since the 2026-11-10 23:5x restart (user: "진행합시다" after the league-day comparison); MA9M_BULLPEN2=0 = the
		// rule before, for comparisons.
		public static readonly bool Bullpen2 = Environment.GetEnvironmentVariable("MA9M_BULLPEN2") != "0";
		public int ReliefRuns = Bullpen2 ? 3 : 4, ReliefRunsLopsided = 4, ReliefReached = Bullpen2 ? 4 : 0;
		// A close late game (|lead| ≤ 2 from the 7th) starts each inning with a fresh arm once a reliever has thrown
		// FreshAfter pitches.
		public int FreshAfter = 20;
		// Between games (league days): fatigue left = units thrown, recovering RecoverShare of the
		// limit per day; a reliever above AvailShare of his limit is kept out (if anyone else can go).
		public double RecoverShare = 0.30, AvailShare = 0.5;
		// Bullpen rest (KBO custom): a reliever sits out after RestDays games in a row, after RestPitches
		// or more the game before, or RestTwoDays or more over the last two games.
		public int RestDays = 3, RestPitches = 30, RestTwoDays = 50;
		// 체력 and rest (user, 2026-11-05: "체력의 경우 정상적인 이닝 소화 및 연투가 가능하도록 만들어주는 스탯"): the pitch
		// counts above scale with his pitch limit against a regular reliever's (RestRefStamina, the cards' regular relievers'
		// mean 체력), within RestScaleMin..Max, and from RestFourthDay of it he may go a fourth day in a row.
		public double RestRefStamina = 50, RestScaleMin = 0.7, RestScaleMax = 1.6, RestFourthDay = 1.3;
		public bool RestByStamina = Environment.GetEnvironmentVariable("MA9M_REST_STAMINA") != "0";   // tests: MA9M_REST_STAMINA=0 = the fixed counts

		public double RestScale(double limit) => RestByStamina ? Math.Clamp(limit / Limit(RestRefStamina), RestScaleMin, RestScaleMax) : 1;
		// Fatigue carried to the next game at most this share of the limit (the penalty is capped anyway).
		public double CarryMax = 1.0;

		// Fatigue units left for the next game: today's start, plus pitches × effort, less a day's recovery.
		public double CarryOut(double carryIn, double units, double limit) => Math.Clamp(carryIn + units - RecoverShare * limit, 0, CarryMax * limit);

		public double Limit(double stamina)
		{
			if (stamina <= 80)
			{
				return Math.Max(LimitMin, LimitAt80 + LimitPerPoint * (stamina - 80));
			}
			double room = LimitTop - LimitAt80;  // same slope at 80, levelling off at LimitTop
			return LimitAt80 + room * (1 - Math.Exp(-LimitPerPoint * (stamina - 80) / room));
		}

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
		public int[] DaysInRow, Last1, Last2;              // per pitcher: games in a row up to the last one, pitches in the last two

		public bool Rested(int p)
		{
			double s = R.RestScale(Limit[p]);
			return CarryIn[p] <= R.AvailShare * Limit[p]
				&& DaysInRow[p] < R.RestDays + (s >= R.RestFourthDay ? 1 : 0) && Last1[p] < R.RestPitches * s && Last1[p] + Last2[p] < R.RestTwoDays * s;
		}

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
