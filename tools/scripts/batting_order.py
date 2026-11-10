"""선호타순 (card column 29: 0 상위, 1 클린업, 2 하위, 3 밸런스) from season records.

Real batting-order splits exist only on the KBO and Statiz sites, which forbid automated
collection (see the kbo-data-access note), so each club-season's lineups are rebuilt game by game:

1. Starts per batter from games and plate appearances (a start ~4.15 PA, a bench appearance ~0.9).
2. Every game the nine batters furthest behind their share of starts play (starts spread evenly
   over the season); slots no batter in the records can fill go to a weak replacement.
3. They are ordered the way KBO managers usually do it: the table-setter leads off (on-base,
   speed, runs scored well above runs driven in), the most power bats 4th, the best hitter 3rd,
   the next power 5th, the best remaining on-base hitter 2nd, the rest 6-9 by OPS. Runs minus
   RBI per PA is the one real trace of where a batter hit: leadoff men score far more than they
   drive in, cleanup men the reverse (home runs count once in each, so they cancel).
4. Shares: 상위 = slots 1-2, 클린업 = 3-5, 하위 = 6-9. Bench batters with few starts are also
   dropped into sampled lineups of their club to see where they would hit.

Type rule (user, 2026-10-18): the group with the highest share is the type when half of that
share is still above every other group's share (67%+ always qualifies; 66/34 does not);
otherwise 밸런스.

CLI: python batting_order.py <card_input_batters.csv> [P_BatterTable.txt ...]
prints the type counts and some known players, writes batting_order.csv next to the input, and
rewrites column 29 of each table given.
"""
import csv
import random
import sys
from collections import defaultdict
from pathlib import Path

TOP, CLEAN, BOTTOM, BALANCE = 0, 1, 2, 3
TYPE_NAMES = ['상위', '클린업', '하위', '밸런스']
GROUP_OF_SLOT = [TOP, TOP, CLEAN, CLEAN, CLEAN, BOTTOM, BOTTOM, BOTTOM, BOTTOM]

PA_PER_START = 4.15
PA_PER_BENCH = 0.9
PRIOR_PA = 120          # rates shrink toward the club's mean with this many plate appearances
BENCH_STARTS = 10       # below this many starts, sampled lineups add to the batter's picture
BENCH_SAMPLES = 60


def classify(shares):
    """shares: [상위, 클린업, 하위] summing to 1 -> type index (0-3)."""
    best = max(range(3), key=lambda k: shares[k])
    others = [shares[k] for k in range(3) if k != best]
    return best if shares[best] / 2 > max(others) else BALANCE


def _z(values, weights):
    tw = sum(weights) or 1.0
    mean = sum(v * w for v, w in zip(values, weights)) / tw
    var = sum(w * (v - mean) ** 2 for v, w in zip(values, weights)) / tw
    sd = var ** 0.5 or 1.0
    return [(v - mean) / sd for v in values]


def _features(rows):
    """Shrunk per-batter rates of one club-season and their z-scores (PA-weighted)."""
    n = lambda r, k: float(r[k])
    tot = {k: sum(n(r, k) for r in rows) for k in ('pa', 'ab', 'h', 'd', 't', 'hr', 'bb', 'hbp', 'sb', 'r', 'rbi')}
    pa_t, ab_t = max(1.0, tot['pa']), max(1.0, tot['ab'])
    mean = {
        'obp': (tot['h'] + tot['bb'] + tot['hbp']) / pa_t,
        'avg': tot['h'] / ab_t,
        'slg': (tot['h'] + tot['d'] + 2 * tot['t'] + 3 * tot['hr']) / ab_t,
        'hr': tot['hr'] / pa_t,
        'tilt': (tot['r'] - tot['rbi']) / pa_t,
        'sb': tot['sb'] / max(1.0, tot['h'] - tot['d'] - tot['t'] - tot['hr'] + tot['bb'] + tot['hbp']),
    }
    feats = []
    for r in rows:
        pa, ab = n(r, 'pa'), n(r, 'ab')
        on1 = n(r, 'h') - n(r, 'd') - n(r, 't') - n(r, 'hr') + n(r, 'bb') + n(r, 'hbp')
        shrink = lambda num, den, m, prior=PRIOR_PA: (num + m * prior) / (den + prior)
        obp = shrink(n(r, 'h') + n(r, 'bb') + n(r, 'hbp'), pa, mean['obp'])
        avg = shrink(n(r, 'h'), ab, mean['avg'])
        slg = shrink(n(r, 'h') + n(r, 'd') + 2 * n(r, 't') + 3 * n(r, 'hr'), ab, mean['slg'])
        feats.append({
            'obp': obp, 'iso': slg - avg, 'ops': obp + slg,
            'hr': shrink(n(r, 'hr'), pa, mean['hr']),
            'tilt': shrink(n(r, 'r') - n(r, 'rbi'), pa, mean['tilt']),
            'sb': shrink(n(r, 'sb'), on1, mean['sb'], 40),
            'spd': float(r.get('t_speed') or 1.0),
        })
    w = [max(1.0, n(r, 'pa')) if n(r, 'pa') >= 30 else 1.0 for r in rows]
    z = {k: _z([f[k] for f in feats], w) for k in ('obp', 'iso', 'ops', 'hr', 'tilt', 'sb', 'spd')}
    for i, f in enumerate(feats):
        zf = {k: z[k][i] for k in z}
        speed = 0.5 * zf['spd'] + 0.5 * zf['sb']
        f['lead'] = zf['obp'] + 0.5 * speed + 1.0 * zf['tilt'] - 0.4 * zf['iso']
        f['four'] = zf['iso'] + 0.6 * zf['hr'] + 0.3 * zf['obp'] - 0.6 * zf['tilt']
        f['three'] = zf['ops'] + 0.3 * zf['iso'] - 0.2 * zf['tilt']
        f['five'] = zf['iso'] + 0.4 * zf['hr'] + 0.3 * zf['obp'] - 0.4 * zf['tilt']
        f['two'] = zf['obp'] + 0.3 * speed + 0.6 * zf['tilt'] - 0.2 * zf['iso']
        f['rest'] = zf['ops']
    return feats


# A weak replacement for slots no batter in the records can fill.
PHANTOM = {'lead': -2.0, 'four': -2.0, 'three': -2.0, 'five': -2.0, 'two': -2.0, 'rest': -1.5}


NOISE = 0.35   # day-to-day lineup changes (platoons, slumps, rest), in score units


def _order(players, rnd=None):
    """players: 9 feature dicts -> list of the 9 in batting order (slot 1..9)."""
    left = list(players)
    slots = [None] * 9
    jitter = {id(f): {k: rnd.gauss(0, NOISE) if rnd else 0.0 for k in ('lead', 'four', 'three', 'five', 'two', 'rest')} for f in players}

    def take(key):
        best = max(left, key=lambda f: f[key] + jitter[id(f)][key])
        left.remove(best)
        return best
    slots[0] = take('lead')
    slots[3] = take('four')
    slots[2] = take('three')
    slots[4] = take('five')
    slots[1] = take('two')
    for k in range(5, 9):
        slots[k] = take('rest')
    return slots


def estimate(rows, season_games):
    """rows: card_input_batters rows. season_games(year, team_games) -> games in that season.
    Returns {(year, sidx): {'shares': [top, clean, bottom], 'starts': n, 'type': t}}."""
    clubs = defaultdict(list)
    for r in rows:
        if int(r['pa']) > 0:
            clubs[(int(r['year']), r['team'])].append(r)
    acc = defaultdict(lambda: [0.0, 0.0, 0.0, 0.0])   # top, clean, bottom counts, real starts
    rnd = random.Random(20261018)
    for (year, team), rs in sorted(clubs.items()):
        games = season_games(year, rs[0]['team_games'])
        feats = _features(rs)
        starts = [min(min(int(r['g']), games), max(0.0, (int(r['pa']) - PA_PER_BENCH * int(r['g'])) / (PA_PER_START - PA_PER_BENCH))) for r in rs]
        total = sum(starts)
        if total > 9 * games:
            starts = [s * 9 * games / total for s in starts]
        done = [0] * len(rs)
        lineups = []
        for d in range(1, games + 1):
            due = sorted((i for i in range(len(rs)) if done[i] < starts[i] - 1e-9),
                         key=lambda i: (starts[i] * d / games - done[i], starts[i]), reverse=True)[:9]
            for i in due:
                done[i] += 1
            nine = [feats[i] for i in due] + [PHANTOM] * (9 - len(due))
            order = _order(nine, rnd)
            lineup = []
            for slot, f in enumerate(order):
                if f is not PHANTOM:
                    i = feats.index(f)
                    acc[(year, int(rs[i]['sidx']))][GROUP_OF_SLOT[slot]] += 1
                    acc[(year, int(rs[i]['sidx']))][3] += 1
                    lineup.append(i)
            lineups.append(lineup)
        # Bench batters: where would they hit on a day they start? Take sampled lineups, put the
        # batter in for its weakest bat, and order again.
        for i, r in enumerate(rs):
            if done[i] >= BENCH_STARTS or not lineups:
                continue
            for _ in range(BENCH_SAMPLES):
                lineup = [j for j in rnd.choice(lineups) if j != i]
                if not lineup:
                    continue
                weakest = min(lineup, key=lambda j: feats[j]['rest'])
                nine = [feats[j] for j in lineup if j != weakest] + [feats[i]]
                nine += [PHANTOM] * (9 - len(nine))
                slot = _order(nine, rnd).index(feats[i])
                # Each sample counts as a tenth of a start, so real starts outweigh them.
                acc[(year, int(r['sidx']))][GROUP_OF_SLOT[slot]] += 0.1
    out = {}
    for key, (top, clean, bottom, real) in acc.items():
        tot = top + clean + bottom
        shares = [top / tot, clean / tot, bottom / tot] if tot else [0.0, 0.0, 1.0]
        out[key] = {'shares': shares, 'starts': real, 'type': classify(shares)}
    return out


def _season_games_default(year, fallback):
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from make_cards import season_games
    return season_games(year, fallback)


def main(argv):
    sys.stdout.reconfigure(encoding='utf-8')
    src = Path(argv[0])
    rows = list(csv.DictReader(open(src, encoding='utf-8-sig')))
    res = estimate(rows, _season_games_default)
    names = {(int(r['year']), int(r['sidx'])): (r['name'], r['team'], int(r['pa'])) for r in rows}
    with open(src.parent / 'batting_order.csv', 'w', encoding='utf-8-sig', newline='') as f:
        w = csv.writer(f)
        w.writerow(['year', 'sidx', 'name', 'team', 'pa', 'starts', 'top', 'cleanup', 'bottom', 'type'])
        for (y, s), v in sorted(res.items()):
            nm, tm, pa = names[(y, s)]
            w.writerow([y, s, nm, tm, pa, round(v['starts']), *(round(x, 3) for x in v['shares']), TYPE_NAMES[v['type']]])
    count = defaultdict(int)
    for v in res.values():
        count[v['type']] += 1
    print('all batter-seasons:', ', '.join(f'{TYPE_NAMES[k]} {count[k]}' for k in range(4)))
    reg = defaultdict(int)
    for key, v in res.items():
        if names[key][2] >= 400:
            reg[v['type']] += 1
    print('400+ PA:', ', '.join(f'{TYPE_NAMES[k]} {reg[k]}' for k in range(4)))
    for path in argv[1:]:
        p = Path(path)
        lines = p.read_text(encoding='utf-8').split('\n')
        changed = 0
        for k in range(1, len(lines)):
            c = lines[k].split('\t')
            if len(c) < 30:
                continue
            v = res.get((int(c[1]), int(c[12])))
            if v is None:
                continue
            if c[29] != str(v['type']):
                c[29] = str(v['type'])
                lines[k] = '\t'.join(c)
                changed += 1
        p.write_text('\n'.join(lines), encoding='utf-8', newline='')
        print(f'{p}: column 29 rewritten on {changed} rows')


if __name__ == '__main__':
    main(sys.argv[1:])
