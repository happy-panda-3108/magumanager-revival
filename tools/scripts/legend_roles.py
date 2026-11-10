"""레전드 투수의 실제 보직 → server/data/tables/ma9m_legend_roles.txt (engine session, 2026-11-10).

User 11-10 07:5x: "김용수 선발, 선동열, 최동원의 마무리 가능 등 포지션 변경이 가능하게끔 했으면 함. 모든 포지션이 가능하다기보다는
실제로 뛰었던 포지선들에 변경이 되도록". Rule (systems session's proposal, same as the season cards): over the whole KBO career,
any season with GS >= 5 -> SP (12), relief games >= 10 -> RP (13), holds >= 5 -> SU (14), saves >= 5 -> CP (15). Seasons before
holds were kept give no SU. The card's own role is left out of `allowed`; the server reads idx, role, allowed (4th column = why).

  python -I tools/scripts/legend_roles.py [--out <path>]
"""
import csv
import glob
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STATIZ = os.path.join(ROOT, 'statiz', '표')
LEGENDS = os.path.join(ROOT, 'data', 'legends.tsv')
PITCHERS = os.path.join(ROOT, 'server', 'data', 'tables', 'P_PItcherTable.txt')
OUT = os.path.join(ROOT, 'server', 'data', 'tables', 'ma9m_legend_roles.txt')
CODES = {'SP': 12, 'RP': 13, 'SU': 14, 'CP': 15}
# Only the seasons with the legend's own club line (user 11-10 10:2x: "쌍방울 레전드 조규제는 쌍방울에서 선발 이력이 없다면
# 선발로 포지션변경이 안 되는게 맞지 않을까?? … 고증이 안 맞는 것 같네"): legends.tsv club → the Statiz team names of its line.
LINES = {'LG': {'MBC', 'LG'}, 'KIA': {'해태', 'KIA'}, '두산': {'OB', '두산'}, '한화': {'빙그레', '한화'},
         '현대': {'삼미', '청보', '태평양', '현대'}, 'SSG': {'SK', 'SSG'}, '롯데': {'롯데'}, '삼성': {'삼성'}, '쌍방울': {'쌍방울'},
         'NC': {'NC'}, '키움': {'우리', '히어로즈', '넥센', '키움'}, 'KT': {'kt', 'KT'}}


def num(s):
    try:
        return int(float(s))
    except (TypeError, ValueError):
        return 0


def main():
    out = OUT
    if '--out' in sys.argv:
        out = sys.argv[sys.argv.index('--out') + 1]
    legends = [r for r in csv.reader((l for l in open(LEGENDS, encoding='utf-8') if not l.startswith('#')), delimiter='\t')]
    head, legends = legends[0], [dict(zip(legends[0], r)) for r in legends[1:]]
    pitchers = {l['p_no']: l for l in legends if l['kind'] == 'P'}
    seasons = {p: [] for p in pitchers}
    for path in sorted(glob.glob(os.path.join(STATIZ, '*_투구_기본.tsv'))):
        year = int(os.path.basename(path)[:4])
        rows = list(csv.reader(open(path, encoding='utf-8'), delimiter='\t'))
        ix = {k: i for i, k in enumerate(rows[0])}
        for r in rows[1:]:
            if len(r) <= ix['HD'] or r[ix['p_no']] not in seasons:
                continue
            g, gs = num(r[ix['G']]), num(r[ix['GS']])
            gr = num(r[ix['GR']]) if r[ix['GR']] != '' else g - gs
            seasons[r[ix['p_no']]].append((year, r[ix['team']], g, gs, gr, num(r[ix['S']]), num(r[ix['HD']])))
    card_role = {}
    with open(PITCHERS, encoding='utf-8') as f:
        h = f.readline().rstrip('\r\n').split('\t')
        for line in f:
            c = dict(zip(h, line.rstrip('\r\n').split('\t')))
            card_role[c['idx']] = int(c['mainposition'])
    lines = ['idx\trole\tallowed\t근거']
    for p_no, leg in pitchers.items():
        played = {}
        line = LINES[leg['club']]
        for year, team, g, gs, gr, sv, hd in sorted(seasons[p_no]):
            if team not in line:
                continue
            for role, ok, n in (('SP', gs >= 5, f'선발 {gs}'), ('RP', gr >= 10, f'구원 {gr}'), ('SU', hd >= 5, f'홀드 {hd}'), ('CP', sv >= 5, f'세이브 {sv}')):
                if ok:
                    played.setdefault(role, []).append(f"{year % 100:02d} {team} {n}")
        own = card_role[leg['idx']]
        allowed = [CODES[r] for r in ('SP', 'RP', 'SU', 'CP') if r in played and CODES[r] != own]
        why = ' / '.join(f"{r}: {len(v)}시즌 ({', '.join(v[:3])}{' …' if len(v) > 3 else ''})" for r, v in played.items())
        lines.append(f"{leg['idx']}\t{own}\t{','.join(map(str, allowed))}\t{leg['name']} — {why}")
        print(leg['idx'], leg['name'], 'own', own, '->', allowed, '|', why)
    with open(out, 'w', encoding='utf-8', newline='') as f:
        f.write('\r\n'.join(lines) + '\r\n')
    print('wrote', out)


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
