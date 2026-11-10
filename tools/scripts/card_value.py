"""Card value for 마구매니저 cards: a park-adjusted WAR per player-season, the six-stat total,
and ball cost / grade.

WAR here is our own estimate from the season records (no official WAR exists in the data):
  batters   (park-adjusted batting runs above average + position + replacement) / runs per win
  pitchers  runs allowed per nine (bWAR style) or FIP (fWAR style), park-adjusted, against a
            replacement level of .380 (starters) / .470 (relievers) winning percentage.
Relievers are credited by leverage as fWAR/bWAR do, (1 + gmLI) / 2, with the entry leverage
estimated from saves and holds per relief appearance (no game-state data exists).
Batters' fielding (2026-11-01): Statiz fielding runs and positional runs at Statiz's runs per win
(연도별상수) when make_cards has them (data/fielding/season_fielding.csv). Not modelled yet:
baserunning beyond hits.
"""
import math
from collections import defaultdict

# Linear weights: runs relative to an average plate appearance, for a league scoring
# ~0.12 runs per PA; scaled by each season's run environment.
LW = {'bb': 0.30, 'hbp': 0.32, 'single': 0.46, 'double': 0.77, 'triple': 1.04, 'hr': 1.40, 'out': -0.27}
LW_RUN_ENV = 0.12
# Positional adjustment, runs per 162 games played at the position (ePos keys; 10 = DH).
POSITION_RUNS = {1: 12.5, 2: -12.5, 3: 2.5, 4: 2.5, 5: 7.5, 6: -7.5, 7: 2.5, 8: -7.5, 10: -17.5}
# With real fielding the positional runs are Statiz's (KBO, per 144 G: SS +9.4, C +9.2, 2B +4.9, 3B/CF 0, LF -4.8,
# RF -5.3, 1B -10.1); a designated hitter's games count this.
DH_RUNS_PER_144 = -15.0


def _statiz_rpw():
    """Statiz's runs per win by season (statiz/표/연도별상수.tsv, the user's 연도별상수), {} if absent."""
    import csv
    from pathlib import Path
    p = Path(__file__).resolve().parents[2] / 'statiz' / '표' / '연도별상수.tsv'
    if not p.exists():
        return {}
    return {int(r['Year']): float(r['RPW']) for r in csv.DictReader(open(p, encoding='utf-8'), delimiter='\t')}


STATIZ_RPW = _statiz_rpw()
REPLACEMENT_WINS_PER_600_PA = 2.0
REPLACEMENT_WINPCT_STARTER, REPLACEMENT_WINPCT_RELIEVER = 0.38, 0.47

# Ball cost from the "ball value" (six-stat total / 6), bands set by the user (2026-10-18).
BALL_BANDS = [(74, 9), (71, 8), (68, 7), (64, 6), (58, 5), (54, 4), (50, 3), (46, 2)]
# Elite cards below this ball value cost 9 balls (75 until 11-02; user, 2026-11-02 via the systems session: "74.2로
# 바꿉시다", which makes 100 of the 238 elites 10-ball cards, 42%), read on the rounded value as the bands are.
# 74.0 since 2026-11-05 (user via the systems session's AskUserQuestion, 20:07): the 테크닉/구위 rework left 93 elites at
# 10 balls (pitchers 43 → 36); 74.0 makes it 99.
ELITE_TEN_BALL_FROM = 74.0
GRADE_OF_BALL = {1: 1, 2: 1, 3: 1, 4: 1, 5: 2, 6: 2, 7: 2, 8: 3, 9: 3, 10: 4}  # NORMAL/SPECIAL/RARE/ELITE
GRADE_NAMES = {1: '노멀', 2: '스페셜', 3: '레어', 4: '엘리트'}
ELITE_PER_SEASON = 7   # top-7 WAR per season (user, 2026-10-18)
DH_BALL_DEFENCE = 40   # the defence a designated hitter's ball value counts (his cards field nowhere)
# Ball value (user, 2026-11-01): 'weighted' = 0.2 for each of the four main stats and 0.1 for the two others (batters:
# 수비, 정신; pitchers: 체력, 정신), which sum to 1, so it stays on the stats' scale; 'even' = the plain mean of six.
BALL_WEIGHTS = 'even'
# Batters' 'weighted' (user, 2026-11-02: "컨택, 파워, 선구를 0.2 / 스피드, 정신이 0.15 / 수비가 0.1을 먹게끔 합시다"; until then
# 스피드 0.2 and 정신 0.1): 컨택, 파워, 선구, 스피드, 정신, 수비.
BAT_WEIGHTS = (0.2, 0.2, 0.2, 0.15, 0.15, 0.1)
# Elite picks: 'top7' = the season's top 7 WAR, batters and pitchers together; 'split43' = top 4 batters and top 3
# pitchers (the user compares both, 2026-11-01).
ELITE_MODE = 'top7'


def i(r, k):
    return int(r[k])


def season_constants(bat_rows, pit_rows):
    by_year = defaultdict(lambda: defaultdict(float))
    for r in bat_rows:
        c = by_year[int(r['year'])]
        for k in ('pa', 'ab', 'h', 'd', 't', 'hr', 'bb', 'hbp', 'so', 'r'):
            c[k] += i(r, k)
    for r in pit_rows:
        c = by_year[int(r['year'])]
        c['p_outs'] += i(r, 'outs')
        c['p_er'] += i(r, 'er')
        c['p_hr'] += i(r, 'hr')
        c['p_bbhbp'] += i(r, 'bb') + i(r, 'hbp')
        c['p_so'] += i(r, 'so')
    out = {}
    for y, c in by_year.items():
        obp = (c['h'] + c['bb'] + c['hbp']) / c['pa']
        r_pa = c['r'] / c['pa']
        rpg = r_pa * 25.3 / (1 - obp)               # runs per team game
        era = c['p_er'] * 27 / c['p_outs']
        ip = c['p_outs'] / 3
        fip_raw = (13 * c['p_hr'] + 3 * c['p_bbhbp'] - 2 * c['p_so']) / ip
        singles = c['h'] - c['d'] - c['t'] - c['hr']
        k = r_pa / LW_RUN_ENV
        lw_total = k * (LW['bb'] * c['bb'] + LW['hbp'] * c['hbp'] + LW['single'] * singles + LW['double'] * c['d']
                        + LW['triple'] * c['t'] + LW['hr'] * c['hr'] + LW['out'] * (c['ab'] - c['h']))
        out[y] = {'r_pa': r_pa, 'rpg': rpg, 'rpw': 1.5 * rpg + 3, 'era': era, 'ra_per_er': rpg / era,
                  'fip_c': era - fip_raw, 'lw_k': k, 'lw_per_pa': lw_total / c['pa']}
    return out


def batter_war(p, const, games_in_season):
    """WAR of one batter season (stints combined)."""
    y = p['year']
    c = const[y]
    war_runs = 0.0
    pa = 0
    for s in p['stints']:
        singles = i(s, 'h') - i(s, 'd') - i(s, 't') - i(s, 'hr')
        lw = c['lw_k'] * (LW['bb'] * i(s, 'bb') + LW['hbp'] * i(s, 'hbp') + LW['single'] * singles + LW['double'] * i(s, 'd')
                          + LW['triple'] * i(s, 't') + LW['hr'] * i(s, 'hr') + LW['out'] * (i(s, 'ab') - i(s, 'h')))
        spa = i(s, 'pa')
        raa = lw - spa * c['lw_per_pa']
        raa -= (float(s['park_pf']) - 1) * c['r_pa'] * spa          # park
        war_runs += raa
        pa += spa
    g = sum(i(s, 'g') for s in p['stints'])
    f = p.get('field')
    if f is not None and f['raa'] is not None:
        # Real fielding (Statiz, 2026-11-01): his fielding runs and Statiz's positional runs for the games he fielded,
        # a designated hitter's for the rest, all at that season's runs per win (연도별상수 RPW).
        rpw = f['rpw']
        war_runs += f['raa'] + f['pos_runs'] + DH_RUNS_PER_144 * max(0.0, g - f['g']) / 144
        return war_runs / rpw + REPLACEMENT_WINS_PER_600_PA * pa / 600
    war_runs += POSITION_RUNS.get(p['main_pos'], -17.5) * g / 162
    war_runs += REPLACEMENT_WINS_PER_600_PA * c['rpw'] * pa / 600
    return war_runs / c['rpw']


def pitcher_war(p, const):
    """(bWAR-style, fWAR-style) of one pitcher season."""
    c = const[p['year']]
    outs = p['outs']
    ip = outs / 3
    er = sum(i(s, 'er') for s in p['stints'])
    hr = sum(i(s, 'hr') for s in p['stints'])
    bbhbp = sum(i(s, 'bb') + i(s, 'hbp') for s in p['stints'])
    so = sum(i(s, 'so') for s in p['stints'])
    pf = sum(float(s['park_pf']) * i(s, 'outs') for s in p['stints']) / outs
    winpct = REPLACEMENT_WINPCT_STARTER if p['starter'] else REPLACEMENT_WINPCT_RELIEVER
    p['gmli'] = 1.0 if p['starter'] else reliever_leverage(p)
    leverage = (1 + p['gmli']) / 2
    repl_ra9 = c['rpg'] * math.sqrt((1 - winpct) / winpct)
    ra9 = er * 27 / outs * c['ra_per_er'] / pf
    fip = ((13 * hr + 3 * bbhbp - 2 * so) / ip + c['fip_c']) * c['ra_per_er'] / pf
    # Statiz's runs per win (연도별상수) when it is there, as for batters (one scale for the season's elite picks).
    rpw = STATIZ_RPW.get(p['year'], c['rpw'])
    return (repl_ra9 - ra9) * ip / 9 / rpw * leverage, (repl_ra9 - fip) * ip / 9 / rpw * leverage


def reliever_leverage(p):
    """Average leverage when entering: closers ~1.6-1.9, set-up men ~1.3, mop-up ~0.85."""
    apps = sum(i(s, 'g') - i(s, 'gs') for s in p['stints'])
    sv = sum(i(s, 'sv') for s in p['stints'])
    hld = sum(i(s, 'hld') for s in p['stints'])
    return 0.85 + 1.1 * min(1.0, (sv + 0.6 * hld) / max(1, apps))


# Bands are read on the ball value rounded to one decimal (BALL_ROUND, 2026-11-02: as the workbook shows it); None = raw.
BALL_ROUND = None


def ball_of(avg6):
    v = round(avg6, BALL_ROUND) if BALL_ROUND is not None else avg6
    for limit, ball in BALL_BANDS:
        if v >= limit:
            return ball
    return 1


def apply_balls(players):
    """Ball and grade from the current BALL_BANDS (elites as in assign)."""
    for p in players:
        if p['elite']:
            v = round(p['avg6'], BALL_ROUND) if BALL_ROUND is not None else p['avg6']
            p['ball'] = 10 if v >= ELITE_TEN_BALL_FROM else 9
            p['grade'] = 4
        else:
            p['ball'] = ball_of(p['avg6'])
            p['grade'] = GRADE_OF_BALL[p['ball']]


def ratio_bands(values, shares):
    """BALL_BANDS so that the top shares[0] of `values` cost 9 balls, the next up to shares[1] 8 … (cumulative, from the
    top), on values rounded as ball_of reads them: each limit is the value at that rank."""
    vals = sorted((round(v, BALL_ROUND) if BALL_ROUND is not None else v for v in values), reverse=True)
    bands = []
    for share, ball in zip(shares, range(9, 1, -1)):
        k = max(1, round(share * len(vals)))
        bands.append((vals[k - 1], ball))
    return bands


def assign(batters, pitchers, bat_rows, pit_rows, war_kind='b'):
    """Adds war, sum6, avg6, elite, ball and grade to every player-season."""
    const = season_constants(bat_rows, pit_rows)
    for p in batters:
        p['war_b'] = p['war_f'] = batter_war(p, const, None)
        rt = p['rating']
        # Main position. A designated hitter cannot field (20 everywhere since 2026-10-20) but his
        # ball value still counts the 40 it had, so no DH card changes ball or grade (user: "괜히
        # 레어가 스페셜이되고 이러면 안되니까").
        # With the season-contribution defence (make_cards 'season') the main position's value counts: another spot's
        # value moves with that spot's positional adjustment (a first baseman's third-base number sits higher).
        defence = DH_BALL_DEFENCE if p.get('main_pos') == 10 else p['def_ball'] if p.get('def_ball') is not None else max(p['defs'])
        p['sum6'] = rt['contact'] + rt['power'] + rt['eye'] + rt['speed'] + rt['mental'] + defence
        w = BAT_WEIGHTS
        p['ball_value'] = (w[0] * rt['contact'] + w[1] * rt['power'] + w[2] * rt['eye'] + w[3] * rt['speed'] + w[4] * rt['mental'] + w[5] * defence
                           if BALL_WEIGHTS == 'weighted' else p['sum6'] / 6)
    for p in pitchers:
        p['war_b'], p['war_f'] = pitcher_war(p, const)
        rt = p['rating']
        p['sum6'] = rt['stamina'] + rt['velocity'] + rt['control'] + rt['technic'] + round(rt['stuff']) + rt['mental']
        p['ball_value'] = (0.2 * (rt['velocity'] + rt['control'] + rt['technic'] + round(rt['stuff'])) + 0.1 * (rt['stamina'] + rt['mental'])
                           if BALL_WEIGHTS == 'weighted' else p['sum6'] / 6)
    by_year = defaultdict(lambda: ([], []))
    for kind, group in ((0, batters), (1, pitchers)):
        for p in group:
            p['avg6'] = p['ball_value']
            # 'statiz': Statiz's WAR of the season (make_cards sets p['statiz_war']); ours where Statiz has no line (< 10 G).
            p['war'] = p['statiz_war'] if war_kind == 'statiz' and p.get('statiz_war') is not None else p['war_' + ('b' if war_kind == 'statiz' else war_kind)]
            p['elite'] = False
            # A season make_cards will not card (p['skip']: a Statiz-only pitcher without --new-pitchers) takes no elite place.
            if not p.get('skip'):
                by_year[p['year']][kind].append(p)
    for y, (bs, ps) in by_year.items():
        if ELITE_MODE == 'split43':
            picks = sorted(bs, key=lambda p: -p['war'])[:4] + sorted(ps, key=lambda p: -p['war'])[:3]
        else:
            picks = sorted(bs + ps, key=lambda p: -p['war'])[:ELITE_PER_SEASON]
        for p in picks:
            p['elite'] = True
    apply_balls(batters + pitchers)
    return const
