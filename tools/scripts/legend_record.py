# 레전드 통산 기록 table (engine session, 2026-11-09; user: "레전드들은 통산 기록이 나오게끔 해줄 수 있나?"). The card tables
# keep one season in their record columns (the client reads W/L/HLD/SV as bytes: club totals there froze every phone on
# 11-08), so the KBO career totals go to their own table, ma9m_legend_record.txt, which only clients from 1.0.38 read
# (Ma9MLegendRecord; older ones ignore an unknown table in the bundle). Cells are the record window's eight texts:
# pitcher G, ERA, W, L, SV, IP, K, H; batter G, AVG, AB, H, HR, RBI, R, SB. Totals over every season and club in the
# Statiz tables (statiz/표 <year>_투구_합본 / _타격_합본).
#   python legend_record.py <out table>   (default server/data/tables/ma9m_legend_record.txt)
import csv
import glob
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
T = ROOT / 'statiz' / '표'
out = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'server' / 'data' / 'tables' / 'ma9m_legend_record.txt'


def num(x):
    try:
        return float(x)
    except ValueError:
        return 0.0


def outs_of(ip):
    a, _, b = ip.partition('.')
    return int(a or 0) * 3 + (int(b) if b else 0)


lines = [l for l in open(ROOT / 'data' / 'legends.tsv', encoding='utf-8') if not l.startswith('#')]
legends = list(csv.DictReader(lines, delimiter='\t'))
want = {(r['kind'], r['p_no']): r for r in legends}
tot = {k: {} for k in want}
seasons = {k: set() for k in want}
for kind, tag in (('P', '투구'), ('B', '타격')):
    for path in sorted(glob.glob(str(T / f'*_{tag}_합본.tsv'))):
        year = int(os.path.basename(path)[:4])
        rows = list(csv.reader(open(path, encoding='utf-8', newline=''), delimiter='\t'))
        h = rows[0]
        for r in rows[1:]:
            k = (kind, r[0])
            if k not in want:
                continue
            d = dict(zip(h, r))
            t = tot[k]
            seasons[k].add(year)
            if kind == 'P':
                for f in ('G', 'W', 'L', 'S', 'ER', 'SO', 'H'):
                    t[f] = t.get(f, 0) + num(d[f])
                t['outs'] = t.get('outs', 0) + outs_of(d['IP'])
            else:
                for f in ('G', 'AB', 'H', 'HR', 'RBI', 'R', 'SB'):
                    t[f] = t.get(f, 0) + num(d[f])

with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('idx\tkind\tname\tc1\tc2\tc3\tc4\tc5\tc6\tc7\tc8\n')
    for r in legends:
        k = (r['kind'], r['p_no'])
        t = tot[k]
        if r['kind'] == 'P':
            o = int(t['outs'])
            cells = [int(t['G']), f"{t['ER'] * 27 / o:.2f}" if o else '0.00', int(t['W']), int(t['L']), int(t['S']), f'{o // 3}.{o % 3}', int(t['SO']), int(t['H'])]
        else:
            ab = int(t['AB'])
            cells = [int(t['G']), f"{t['H'] / ab:.3f}" if ab else '0.000', ab, int(t['H']), int(t['HR']), int(t['RBI']), int(t['R']), int(t['SB'])]
        f.write('\t'.join([r['idx'], r['kind'], r['name']] + [str(c) for c in cells]) + '\n')
        print(r['kind'], r['name'], f"{min(seasons[k])}-{max(seasons[k])}", cells)
print(out)
