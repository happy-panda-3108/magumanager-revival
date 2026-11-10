using System;
using Ma9MSim.Fielding;

namespace Ma9MSim.Fielding
{
	// The switch for the fielding engine. Off by default; MA9M_FIELD_ENGINE=1 (server or sim
	// process) or `--fieldengine on` (sim) turns it on.
	public static class FieldEngine
	{
		public static bool On = Environment.GetEnvironmentVariable("MA9M_FIELD_ENGINE") == "1";

		// Diagnostics (tests only, CountKeys): intent key × realized batter class. Off elsewhere: every
		// thread adding to the one table on each ball in play cost the parallel league runs (2026-10-31).
		public static bool CountKeys;
		public static readonly long[,] KeyClass = new long[BipLibrary.Keys, 5];
	}

	// --fieldvalue (tests only): every ball in play the measured team fields is played again with the same luck by the
	// same fielders, one man's tools changed (Variants), and each version is counted by what it leaves: runs on the play
	// and the base-out state after. Steal attempts against a measured catcher count by their chances (CaughtMult). The
	// run value of each change then follows from the run expectancy of those states (RE24 from the same games, Mark and
	// EndHalf), with no game-to-game noise.
	public sealed class FieldShadow
	{
		public int Team, Pos;
		public FielderSkill[] Variants = Array.Empty<FielderSkill>();
		public double[] CaughtMult;          // catcher variants' caught-stealing multiplier (null: steals not counted)
		public double[,] Ends = new double[1, 25];   // [0 as played, 1.. variants; state outs × 8 + bases, 24 inning over]
		public double[] Runs = new double[1];
		public long Plays, Steals;
		// As played, by fielder (deck position, 8 the pitcher): grounders fielded, balls caught in the air, errors.
		public readonly long[] Fielded = new long[9], Caught = new long[9], Errors = new long[9];
		// Balls in play and hits by direction, Statiz's five zones (0 L = third-base line ... 4 R = first-base line).
		public readonly long[] ZoneBalls = new long[5], ZoneHits = new long[5];
		public readonly double[] ReSum = new double[24];
		public readonly long[] ReN = new long[24];
		private readonly System.Collections.Generic.List<(int state, int score)> m_marks = new System.Collections.Generic.List<(int, int)>(16);

		public void Init(int variants)
		{
			Ends = new double[variants + 1, 25];
			Runs = new double[variants + 1];
		}

		public static int Bits(int[] bases) => (bases[0] >= 0 ? 1 : 0) | (bases[1] >= 0 ? 2 : 0) | (bases[2] >= 0 ? 4 : 0);

		public void Mark(int outs, int[] bases, int score) => m_marks.Add((outs * 8 + Bits(bases), score));

		public void EndHalf(int score)
		{
			foreach ((int s, int at) in m_marks)
			{
				ReSum[s] += score - at;
				ReN[s]++;
			}
			m_marks.Clear();
		}

		public void Count(int k, FieldOutcome fo, bool[] on, int outsBefore)
		{
			int runs = 0, bits = 0;
			for (int b = 1; b <= 3; b++)
			{
				if (!on[b]) continue;
				int d = fo.Dest[b];
				if (d == 4) runs++;
				else if (d == 0) bits |= 1 << (b - 1);
				else if (d <= 3) bits |= 1 << (d - 1);
			}
			int d0 = fo.Dest[0];
			if (d0 >= 1 && d0 <= 3) bits |= 1 << (d0 - 1);
			int outs = outsBefore + fo.Outs;
			Runs[k] += runs;
			Ends[k, outs >= 3 ? 24 : outs * 8 + bits]++;
		}

		// A steal chance: no try (the state stays), safe, or out, by their chances.
		public void CountSteal(int k, int stay, int safe, int caught, double attempt, double pSafe)
		{
			Ends[k, stay] += 1 - attempt;
			Ends[k, safe] += attempt * pSafe;
			Ends[k, caught] += attempt * (1 - pSafe);
		}

		public void Merge(FieldShadow o)
		{
			for (int k = 0; k < Runs.Length; k++)
			{
				Runs[k] += o.Runs[k];
				for (int s = 0; s < 25; s++) Ends[k, s] += o.Ends[k, s];
			}
			for (int s = 0; s < 24; s++)
			{
				ReSum[s] += o.ReSum[s];
				ReN[s] += o.ReN[s];
			}
			Plays += o.Plays;
			Steals += o.Steals;
			for (int f = 0; f < 9; f++)
			{
				Fielded[f] += o.Fielded[f];
				Caught[f] += o.Caught[f];
				Errors[f] += o.Errors[f];
			}
			for (int z = 0; z < 5; z++)
			{
				ZoneBalls[z] += o.ZoneBalls[z];
				ZoneHits[z] += o.ZoneHits[z];
			}
		}

		// Runs version k gave up more than the play as played, from run expectancy re (24 states).
		public double Delta(int k, double[] re)
		{
			double d = Runs[k] - Runs[0];
			for (int s = 0; s < 24; s++) d += re[s] * (Ends[k, s] - Ends[0, s]);
			return d;
		}
	}
}

namespace Ma9MSim
{
	public sealed partial class SeasonSimulator
	{
		private static readonly Fielding.Defence s_averageDefence = Fielding.Defence.Average();

		// Mean pitch-mix ground lean over the plate appearances played (to set PitchMix.LeagueGroundLean).
		public static bool GroundLeanTrack;
		private static long s_leanSum, s_leanCount;
		public static double GroundLeanMean => s_leanCount > 0 ? s_leanSum / 1e6 / s_leanCount : double.NaN;
		private readonly Resolver m_resolver = new Resolver();
		private readonly Choreo m_choreo = new Choreo();

		// The run environment the old rules scaled base running with (fitted per season in replays;
		// the card games' own value is the reference) moves how readily runners go for a base.
		// The league's run environment (EngineParams.RunEnvironment, fitted per season in replays) moves
		// the outfielders' gather time: a livelier league gives runners a little more. It used to move
		// the runners' aggression (RunEnvAggr), but more sends also mean more runners thrown out, so a
		// fit chasing more runs pushed it to its limit (2026-10-30); the gather time moves runs one way.
		public static double RunEnvAggr = 0.0, RunEnvExtra = 0.6, RunEnvRef = 0.909;

		// The 3D script of a steal (the engine's call; nothing random is drawn here).
		private void StealScript(Side fld, int runner, int fromBase, bool safe)
		{
			if (!FieldEngine.On || rec == null || runner < 0) return;
			play.Script = m_choreo.Steal(fld.Def ?? s_averageDefence, RunnerOf(runner), fromBase, safe).Encode();
		}

		// Card mode: each man's tools at the position he plays today (defence there, his 주력).
		private Fielding.Defence BuildDefence(int[] at)
		{
			Fielding.Defence d = new Fielding.Defence();
			for (int pos = 0; pos < 8; pos++)
			{
				int i = at[pos];
				double xs = m.RunX != null ? m.RunX[i] : 0;
				d.Set(pos, CardRules.X(m.Defs[i][pos]), xs, m.RunBaseX != null ? m.RunBaseX[i] : xs);
			}
			d.F[Geometry.Pit] = s_averageDefence.F[Geometry.Pit];
			d.Extra = Math.Clamp(Fielding.Defence.OfExtra + RunEnvExtra * Math.Log(Math.Max(0.3, prm.RunEnvironment) / RunEnvRef), 0.2, 1.6);
			return d;
		}

		// --fieldvalue: the measured side (null in games).
		public Fielding.FieldShadow Shadow;
		private Resolver m_shadowRes;
		private Fielding.Defence m_shadowDef;

		// The same ball with the same luck (its noise, and the resolver's draws from `luck`) for each variant of the
		// measured fielder.
		private void ShadowBip(BipLibrary.Entry e, Fielding.Defence d, BatterRun br, in BallPlan.Noise n, FieldOutcome fo, Rng luck, bool ground)
		{
			Fielding.FieldShadow sh = Shadow;
			Resolver res = m_resolver, s = m_shadowRes ??= new Resolver();
			Fielding.Defence v = m_shadowDef ??= new Fielding.Defence();
			sh.Count(0, fo, res.On, res.OutsBefore);
			sh.Plays++;
			int who = fo.Ball.Fielder;
			if (who >= 0 && who < 9 && !fo.Ball.Through)
			{
				if (ground && fo.Ball.Class <= 1) sh.Fielded[who]++;
				else if (!ground && fo.Ball.Class == 0) sh.Caught[who]++;
			}
			if (fo.Error && fo.ErrorFielder >= 0 && fo.ErrorFielder < 9) sh.Errors[fo.ErrorFielder]++;
			int zone = 4 - Math.Clamp((int)(e.Ball.H / 18), 0, 4);
			sh.ZoneBalls[zone]++;
			if (fo.Hit > 0) sh.ZoneHits[zone]++;
			Array.Copy(d.F, v.F, d.F.Length);
			Array.Copy(d.Pos, v.Pos, d.Pos.Length);
			v.Extra = d.Extra;
			s.D = v;
			s.Batter = br;
			s.OutsBefore = res.OutsBefore;
			s.Aggression = res.Aggression;
			s.ErrScale = res.ErrScale;
			for (int b = 1; b <= 3; b++)
			{
				s.On[b] = res.On[b];
				s.Run[b] = res.Run[b];
			}
			for (int k = 0; k < sh.Variants.Length; k++)
			{
				v.F[sh.Pos] = sh.Variants[k];
				BallPlan plan = BallPlan.Make(e.Ball, v, br);
				BallPlan.Result r = plan.Realize(n);
				if (r.Class >= 4) r.Class = 2;
				Rng g = luck;
				sh.Count(k + 1, s.Resolve(plan, r, ref g), s.On, s.OutsBefore);
			}
		}

		// A steal of second (fromBase 0) or third (1) the measured catcher faces: its chances with each variant's arm.
		private void ShadowSteal(Side fld, BatterTalent r, int runner, int fromBase, double attemptBase, int outs)
		{
			Fielding.FieldShadow sh = Shadow;
			if (sh.CaughtMult == null) return;
			int stay = outs * 8 + Fielding.FieldShadow.Bits(bases);
			int moved = Fielding.FieldShadow.Bits(bases) & ~(1 << fromBase) | (1 << (fromBase + 1));
			int gone = Fielding.FieldShadow.Bits(bases) & ~(1 << fromBase);
			int safe = outs * 8 + moved, caught = outs + 1 >= 3 ? 24 : (outs + 1) * 8 + gone;
			sh.Steals++;
			for (int k = 0; k <= sh.CaughtMult.Length; k++)
			{
				double mult = k == 0 ? fld.CaughtMult : sh.CaughtMult[k - 1];
				double p = StealSafeWith(r, mult);
				double attempt = Math.Min(1, attemptBase * (m.Tac != null ? m.Tac.StealTry(runner, p) : 1));
				p = Math.Clamp(p + (m.Tac != null ? m.Tac.StealSafeShift(runner) : 0), 0, 1);
				sh.CountSteal(k, stay, safe, caught, attempt, p);
			}
		}

		private RunnerSkill RunnerOf(int batter)
		{
			// Replays without cards: the talent's speed around the reference runner.
			double x = m.RunX != null ? m.RunX[batter] : Fielding.Defence.RefRunX + Math.Log(Math.Max(0.5, m.BT[batter].Speed)) / 0.08;
			return RunnerSkill.For(x);
		}

		// A ball in play under the fielding engine. The cards still decide what kind of result it is
		// (hit or out, its type, single/double/triple), exactly as the old rules did; a batted ball is
		// then drawn for it and played by these fielders and runners. Returns the event for Tally.
		private int PhysicalBip(Side bat, Side fld, int batter, int pitcher, BatterTalent bt, PitcherTalent pt, League lg,
			double mBabip, double mD, double mT, double mGB, ref int outs, out PlayKind kind, out bool ground)
		{
			int bo = batter * BS.Count, po = pitcher * PS.Count;
			st.B[bo + BS.AB]++;
			double babip = Math.Clamp(Log5.Combine(bt.Babip, pt.Babip, lg.Babip) * mBabip, 0.05, 0.6);
			int key;
			if (rng.Next() < babip)
			{
				double v = rng.Next();
				double triple = bt.Triple * mT, dbl = bt.Double * mD;
				key = v < triple ? BipLibrary.Triple : v < triple + dbl ? BipLibrary.Double : BipLibrary.Single;
			}
			else
			{
				double groundShare = Math.Min(0.75, prm.GroundShare * mGB);
				double flyShare = (1 - groundShare) * prm.FlyShare / (1 - prm.GroundShare);
				double kr = rng.Next();
				key = kr < groundShare ? BipLibrary.GroundOut : kr < groundShare + flyShare ? BipLibrary.FlyOut : BipLibrary.LineOut;
			}
			int hand = m.BatSide != null ? m.BatSide[batter] : 0;
			bool lefty = hand == 1 || (hand == 2 && m.PitcherType != null && m.PitcherType[pitcher] < 2);
			// A ball sorted for a runner of his speed: his card's hits already hold his legs (BipLibrary).
			BatterRun br = new BatterRun { R = RunnerOf(batter), Lefty = lefty };
			BipLibrary.Entry e = BipLibrary.Shared.Draw(lefty ? 1 : 0, br.R.Xs, key, rng.Next());

			Fielding.Defence d = fld.Def ?? s_averageDefence;
			BallPlan plan = BallPlan.Make(e.Ball, d, br);
			Rng nr = new Rng(e.Seed);
			BallPlan.Noise n = BallPlan.Noise.Draw(ref nr);
			BallPlan.Result r = plan.Realize(n);
			if (FieldEngine.CountKeys) System.Threading.Interlocked.Increment(ref FieldEngine.KeyClass[key, Math.Min(4, r.Class)]);
			if (r.Class >= 4)
			{
				r.Class = 2;   // cannot happen (the ball is the same as in the library), kept safe
			}

			Resolver res = m_resolver;
			res.D = d;
			res.Batter = br;
			res.OutsBefore = outs;
			res.Aggression = (m.Tac != null ? m.Tac.BatRun[batter] : 0) + RunEnvAggr * Math.Log(Math.Max(0.3, prm.RunEnvironment) / RunEnvRef);
			// Errors: the league's rate, the 작전실 field setting, and in replays the team's own rate (in card
			// games each fielder's defence already sets his own error rate, so ErrorMult is not applied twice).
			res.ErrScale = Math.Clamp(prm.ReachOnError / Resolver.ErrRefRoe
				* (m.Tac != null ? m.Tac.ErrorMult(fld.Team) : 1)
				* (m.ErrorBy == null && m.ErrorMult != null ? m.ErrorMult[fld.Team] : 1), 0.2, 4.0);
			for (int b = 1; b <= 3; b++)
			{
				res.On[b] = bases[b - 1] >= 0;
				if (res.On[b]) res.Run[b] = RunnerOf(bases[b - 1]);
			}
			Rng luck = rng;
			FieldOutcome fo = res.Resolve(plan, r, ref rng);
			if (Shadow != null && fld.Team == Shadow.Team)
			{
				ShadowBip(e, d, br, n, fo, luck, plan.Ground);
			}

			// Batter's line.
			int ev;
			ground = plan.Ground && fo.Hit == 0;
			if (fo.Hit > 0)
			{
				st.B[bo + BS.H]++;
				st.P[po + PS.H]++;
				if (fo.Hit == 2) st.B[bo + BS.D]++;
				if (fo.Hit == 3) st.B[bo + BS.T]++;
				ev = fo.Hit == 1 ? EvSingle : fo.Hit == 2 ? EvDouble : EvTriple;
			}
			else if (fo.BatterOnError)
			{
				ev = EvError;
			}
			else if (fo.SacFly)
			{
				st.B[bo + BS.AB]--;
				st.B[bo + BS.SF]++;
				ev = EvSacFly;
			}
			else
			{
				ev = EvOut;
			}
			if (fo.Gidp) st.B[bo + BS.GIDP]++;
			if (fo.ErrorExtends) inningExtended = true;

			// Runners, lead runner first so nobody is overwritten.
			for (int b = 3; b >= 1; b--)
			{
				if (bases[b - 1] < 0) continue;
				int dest = fo.Dest[b];
				if (dest == 0) continue;
				if (dest >= 1 && dest <= 3) Move(b - 1, dest - 1);
				else if (dest == 4) Score(b - 1, batter, fo.Rbi[b], bat, fld);
				else bases[b - 1] = -1;
			}
			int bd = fo.Dest[0];
			if (bd >= 1 && bd <= 3)
			{
				Place(bd - 1, batter, pitcher, fo.BatterOnError);
			}
			for (int k = 0; k < fo.Outs; k++)
			{
				outs++;
				AddOut(fld, pitcher);
			}

			kind = fo.Kind;
			if (rec != null)
			{
				for (int i = 0; i < 4; i++) play.Dest[i] = play.Before[i] < 0 ? 0 : fo.Dest[i];
				play.DestSet = true;
				play.Ball = e.Ball;
				play.Field = fo;
				// The 3D broadcast's script of the play (the client plays it back when it can).
				play.Script = m_choreo.Build(plan, r, fo, d, br, res.Run, res.On).Encode();
			}
			return ev;
		}
	}
}
