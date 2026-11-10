"""육성 배분 (2026-11-05): plan log (+9 gains measured at 팀컬러 21 + 코칭스태프 3) -> best 특강 / 트레이닝 / 재계약 per player.

Value model: win% is linear in the in-play stat. In-play = knee(card + w * counted(card, bonus)), counted on the shown
scale with TeamStrength.DevCurve (100 -> 75%, 120 -> 50%, cap 140), knee = CardRules half slope past 105. A catcher's
수비 uses CatcherCurve on the shown scale. Each stat's gain per in-play point comes from its measured +9.
"""
import csv, itertools, json, re, sys
from collections import defaultdict

W = 0.374
CAP = 140
DEV = [(100, 0.75), (120, 0.5)]
CATCH = [(100, 0.75), (120, 0.25)]
BAT = ['컨택', '파워', '스피드', '선구', '수비', 'H정신']
PIT = ['체력', '구속', '컨트롤', '테크닉', '구위', 'P정신']
LOGBAT = {'컨택': 0, '파워': 1, '주력': 2, '스피드': 2, '선구': 3, '수비': 4, '정신': 5}
LOGPIT = {'체력': 0, '구속': 1, '컨트롤': 2, '테크닉': 3, '구위': 4, '정신': 5}
TEAM = {'롯데': 7, '삼성': 8, 'LG': 6, '쌍방울': 11, '현대': 4}
GRADE = {'1': '노말', '2': '스페셜', '3': '레어', '4': '엘리트'}


def counted(frm, d, curve=DEV):
    if d <= 0:
        return d
    to = min(CAP, frm + d)
    if to <= frm:
        return 0.0
    s, at, mult = 0.0, frm, 1.0
    for edge, m in curve:
        if edge > at:
            end = min(to, edge)
            if end > at:
                s += (end - at) * mult
                at = end
        mult = m
        if at >= to:
            break
    if to > at:
        s += (to - at) * mult
    return s


def knee(x):
    return min(x, (x + 105) / 2)


def play(card, d, kind, k, catcher=False):
    if kind == 'B' and k == 4 and catcher:
        return counted(card, d, CATCH)
    w = W * (1.15 if kind == 'P' and k == 4 else 1)
    return knee(card + w * counted(card, d))


def parse_log(path):
    decks = {}
    cur = None
    raw = open(path, 'rb').read()
    try:
        text = raw.decode('utf-8')  # logs written under a UTF-8 console (11-07 runs)
    except UnicodeDecodeError:
        text = raw.decode('cp949', errors='replace')
    for line in text.splitlines():
        if 'tcsim plan:' not in line:
            continue
        body = line.split('tcsim plan: ', 1)[1].rstrip()
        m = re.match(r'===== (.+?) \((\d+)볼', body)
        if m:
            cur = {'label': m.group(1), 'balls': int(m.group(2)), 'players': []}
            decks[cur['label']] = cur
            continue
        if cur is None or not body.startswith(cur['label'] + ' | '):
            continue
        parts = body.split(' | ')
        if len(parts) < 4:
            continue
        who, stats, lec = parts[1], parts[2], parts[3]
        mb = re.match(r'(\d)번 (\S+) (\S+) (\d+)볼 (\d+)세', who)
        mp = re.match(r'(SP\d|RP\d|SU|CP) (\S+?)(\(.*?\))? (\d+)볼 (\d+)세', who)
        if mb:
            p = {'kind': 'B', 'slot': mb.group(1) + '번 ' + mb.group(2), 'pos': mb.group(2), 'name': mb.group(3), 'ball': int(mb.group(4)), 'age': int(mb.group(5))}
            idx = LOGBAT
        elif mp:
            p = {'kind': 'P', 'slot': mp.group(1), 'pos': mp.group(1), 'name': mp.group(2), 'change': (mp.group(3) or '').strip('()'), 'ball': int(mp.group(4)), 'age': int(mp.group(5))}
            idx = LOGPIT
        else:
            continue
        p['card'] = [None] * 6
        p['g9'] = [None] * 6
        for s in stats.split(', '):
            ms = re.match(r'(\S+?)\((\d+)\*{0,2}\) ([+-]?[\d.]+)', s)
            if ms:
                k = idx[ms.group(1)]
                p['card'][k] = int(ms.group(2))
                p['g9'][k] = float(ms.group(3))
        p['coaches'] = [c.strip() for c in lec.replace('특강 계산', '').split(',') if c.strip()]
        cur['players'].append(p)
    return decks


def load_coaches(book):
    import openpyxl
    ws = openpyxl.load_workbook(book)['특강 코치']
    out = {'B': {}, 'P': {}}
    for kind, rows in (('B', range(6, 28)), ('P', range(31, 53))):
        for r in rows:
            name, res = ws.cell(r, 1).value, ws.cell(r, 2).value
            if name and res == 5:
                out[kind][name] = [ws.cell(r, 3 + j).value or 0 for j in range(6)]
    return out


def load_cards():
    cards = {}
    base = 'C:/Ma9MProject/server/data/tables/'
    for fn, kind in (('P_BatterTable.txt', 'B'), ('P_PItcherTable.txt', 'P')):
        for r in csv.DictReader(open(base + fn, encoding='utf-8'), delimiter='\t'):
            cards[(kind, r['year'], r['teamcode'], r['name'])] = r
    return cards


def optimize(p, lec_options, fixed, train_chunks, train_gain, renew_n, renew_gain):
    """Best (coach, lecture, training, renewal) for one player. fixed: shown bonus already on each stat."""
    kind = p['kind']
    catcher = kind == 'B' and p['pos'] == 'C'
    ks = [k for k in range(6) if p['g9'][k] is not None]
    v = {}
    for k in ks:
        d0 = fixed[k]
        e9 = play(p['card'][k], d0 + 9, kind, k, catcher) - play(p['card'][k], d0, kind, k, catcher)
        v[k] = p['g9'][k] / e9 if e9 > 1e-9 else 0.0
    useful = [k for k in ks if v[k] > 0] or ks[:1]

    def val(D):
        return sum(v[k] * (play(p['card'][k], D[k], kind, k, catcher) - play(p['card'][k], fixed[k], kind, k, catcher)) for k in ks)

    best = None
    for coach, L in lec_options:
        for combo in itertools.combinations_with_replacement(useful, train_chunks):
            D = [fixed[k] + L[k] for k in range(6)]
            T = [0] * 6
            for k in combo:
                T[k] += train_gain
                D[k] += train_gain
            R = [0] * 6
            for _ in range(renew_n):
                gains = [(v[k] * (play(p['card'][k], D[k] + renew_gain, kind, k, catcher) - play(p['card'][k], D[k], kind, k, catcher)), k) for k in useful]
                g, k = max(gains)
                R[k] += renew_gain
                D[k] += renew_gain
            total = val(D)
            if best is None or total > best[0] + 1e-12:
                best = (total, coach, L, T, R, D)
    total, coach, L, T, R, D = best
    return {'value': total, 'coach': coach, 'lecture': L, 'training': T, 'renewal': R, 'shown': [ (p['card'][k] + D[k]) if p['card'][k] is not None else None for k in range(6)], 'v': v}


def plan_deck(deck, coaches, setb, hc, pc, train_chunks=3, train_gain=6, renew_n=6, renew_gain=3, tc=21):
    res = []
    total = 0.0
    for p in deck['players']:
        fixed = [tc + setb] * 6
        stat = hc if p['kind'] == 'B' else pc
        if stat:
            fixed[stat[0]] += stat[1]
        opts = [(c, coaches[p['kind']][c]) for c in p['coaches'] if c in coaches[p['kind']]] or [(None, [0] * 6)]
        r = optimize(p, opts, fixed, train_chunks, train_gain, renew_n, renew_gain)
        r['fixed'] = fixed
        res.append(r)
        total += r['value']
    return total, res


if __name__ == '__main__':
    decks = parse_log(sys.argv[1])
    coaches = load_coaches(sys.argv[2])
    out = {}
    for label, deck in decks.items():
        if len(deck['players']) < 20:
            print('(측정 중)', label, len(deck['players']))
            continue
        # The 타격/투수코치 stat that helps the deck most (+2, 엘리트), every candidate fully re-planned.
        hc_rank = sorted(((plan_deck(deck, coaches, 3, (k, 2), None)[0], BAT[k]) for k in (0, 1, 2, 3, 5)), reverse=True)
        pc_rank = sorted(((plan_deck(deck, coaches, 3, None, (k, 2))[0], PIT[k]) for k in (1, 2, 3, 4, 5)), reverse=True)
        hc = BAT.index(hc_rank[0][1])
        pc = PIT.index(pc_rank[0][1])
        total, res = plan_deck(deck, coaches, 3, (hc, 2), (pc, 2))
        old_total, old = plan_deck(deck, coaches, 0, None, None, train_chunks=3, train_gain=4, renew_n=6, renew_gain=3)
        out[label] = {'balls': deck['balls'], 'hc': hc_rank, 'pc': pc_rank, 'total': total, 'old_total': old_total,
                      'players': [dict(p, **r, old=o) for p, r, o in zip(deck['players'], res, old)]}
    json.dump(out, open(sys.argv[3], 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)
    for label, d in out.items():
        print('==', label, f"{d['balls']}볼", '타격코치', [(n, round(t, 2)) for t, n in d['hc']], '투수코치', [(n, round(t, 2)) for t, n in d['pc']])
        for p in d['players']:
            names = BAT if p['kind'] == 'B' else PIT
            def fmt(vec):
                return ' '.join(f'{names[k]}{vec[k]}' for k in range(6) if vec[k])
            print(f"  {p['slot']:7} {p['name']:6} 특강 {p['coach']} | 트레이닝 {fmt(p['training'])} | 재계약 {fmt(p['renewal'])} | 표시 {[x for x in p['shown']]} | +{p['value']:.2f}%p")
