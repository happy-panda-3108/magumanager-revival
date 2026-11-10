"""레전드 타자 수비 = 통산 그 자리를 맡은 비율 (engine session, 2026-11-10).

User 11-10 07:5x: "레전드의 경우 수비만 전 시즌 합쳐서 해당 포지션을 얼마나 소화했는지 비율로 수비력을 줍시다. 가령 이대호의 경우
어릴때는 3루를 보고 거의 1루만 봤으니까 1루의 수비능력치가 더 높은거고, 정근우도 외야를 본 시즌은 한 시즌 정도로 기억해서 가능은
하지만 수비력이 낮은걸로".

For each legend batter and fielding position p:
  share_p = career KBO fielding innings at p / all his fielding innings (Statiz 수비_종합, every season; games x 8 when a
            season has no innings);
  Q_p     = his season cards' defence at p, averaged over the seasons he played p there (cards >= 1993), weighted by the
            innings; the legend card's old value when no season card has it;
  def_p   = 20 + (Q_p - 20) * (share_p / share_max) ** EXP   (EXP 0.5: the main position keeps its whole value, a
            position played a tenth as much keeps about a third of it above 20). Positions with no innings stay 20.
The main position becomes the one with the most innings. Ball and other stats stay as they are.

  python -I tools/scripts/legend_defence.py            # table only
  python -I tools/scripts/legend_defence.py --write    # patch server/data/tables/P_BatterTable.txt (backup first)
"""
import csv
import glob
import os
import shutil
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STATIZ = os.path.join(ROOT, 'statiz', '표')
LEGENDS = os.path.join(ROOT, 'data', 'legends.tsv')
BATTERS = os.path.join(ROOT, 'server', 'data', 'tables', 'P_BatterTable.txt')
POS = ['C', '1B', '2B', '3B', 'SS', 'LF', 'CF', 'RF']   # mainposition 1..8, def_* columns
EXP = 0.5


def num(s):
    try:
        return float(s)
    except (TypeError, ValueError):
        return 0.0


def main():
    write = '--write' in sys.argv
    rows = [r for r in csv.reader((l for l in open(LEGENDS, encoding='utf-8') if not l.startswith('#')), delimiter='\t')]
    legends = [dict(zip(rows[0], r)) for r in rows[1:] if r[0] == 'B']
    by_pno = {l['p_no']: l for l in legends}
    innings = {p: {} for p in by_pno}          # p_no -> {(year, pos): innings}
    for path in sorted(glob.glob(os.path.join(STATIZ, '*_수비_종합.tsv'))):
        year = int(os.path.basename(path)[:4])
        t = list(csv.reader(open(path, encoding='utf-8'), delimiter='\t'))
        ix = {k: i for i, k in enumerate(t[0])}
        for r in t[1:]:
            if len(r) <= ix['IP'] or r[ix['p_no']] not in innings or r[ix['pos']] not in POS:
                continue
            ip = num(r[ix['IP']]) or num(r[ix['G']]) * 8
            key = (year, r[ix['pos']])
            innings[r[ix['p_no']]][key] = innings[r[ix['p_no']]].get(key, 0) + ip
    lines = open(BATTERS, encoding='utf-8').read().split('\n')
    head = lines[0].rstrip('\r').split('\t')
    ix = {k: i for i, k in enumerate(head)}
    cards = [l.rstrip('\r').split('\t') for l in lines[1:] if l.strip()]
    legend_row = {c[ix['idx']]: c for c in cards if c[ix['idx']] in {l['idx'] for l in legends}}
    out = {}
    for p_no, leg in by_pno.items():
        lc = legend_row[leg['idx']]
        sidx = lc[ix['sidx']]
        season = {int(c[ix['year']]): c for c in cards if c[ix['sidx']] == sidx and c[ix['cardgrade']] != '5'}
        tot = {}
        for (y, pos), ip in innings[p_no].items():
            tot[pos] = tot.get(pos, 0) + ip
        allip = sum(tot.values()) or 1
        share = {p: tot.get(p, 0) / allip for p in POS}
        smax = max(share.values()) or 1
        # The main position: the card's own (its peak seasons' most-played) while he played it at least half as much as his
        # most-played one (이종범 SS 32% vs CF 35% stays SS), else the most-played (박용택 CF 20% vs LF 73% → LF).
        own = int(lc[ix['mainposition']])
        main = POS[own - 1] if 1 <= own <= 8 and share[POS[own - 1]] >= 0.5 * smax else max(POS, key=lambda p: share[p])
        smax = share[main]

        def quality(p):
            w = q = 0.0
            for (y, pos), ip in innings[p_no].items():
                c = season.get(y)
                if pos == p and c is not None and int(c[ix['def_' + p]]) > 20:
                    q += int(c[ix['def_' + p]]) * ip
                    w += ip
            return q / w if w else max(int(lc[ix['def_' + p]]), 20)

        # Another position never above the main one (user: 이대호 "거의 1루만 봤으니까 1루의 수비능력치가 더 높은거고";
        # his young 3B seasons alone rated higher than his 1B ones).
        qmain = quality(main)
        new = {}
        for p in POS:
            if share[p] <= 0:
                new[p] = 20
                continue
            Q = qmain if p == main else min(quality(p), qmain)
            new[p] = max(20, round(20 + (Q - 20) * min(1.0, share[p] / smax) ** EXP))
        old = {p: int(lc[ix['def_' + p]]) for p in POS}
        oldmain = int(lc[ix['mainposition']])
        out[leg['idx']] = (new, POS.index(main) + 1)
        played = ', '.join(f"{p} {share[p] * 100:.0f}%" for p in sorted(POS, key=lambda p: -share[p]) if share[p] > 0)
        chg = ', '.join(f"{p} {old[p]}→{new[p]}" for p in POS if old[p] != new[p] or (new[p] > 20))
        print(f"{leg['name']}\t주 {POS[oldmain - 1] if oldmain <= 8 else 'DH'}→{main}\t{played}\t{chg}")
    if write:
        stamp = time.strftime('%Y%m%d_%H%M')
        shutil.copy2(BATTERS, BATTERS + f'.before_legend_defence_{stamp}')
        for i, l in enumerate(lines):
            c = l.rstrip('\r').split('\t')
            if len(c) > 1 and c[ix['idx']] in out:
                new, main = out[c[ix['idx']]]
                for p in POS:
                    c[ix['def_' + p]] = str(new[p])
                if int(c[ix['mainposition']]) != 10:
                    c[ix['mainposition']] = str(main)
                lines[i] = '\t'.join(c) + ('\r' if l.endswith('\r') else '')
        open(BATTERS, 'w', encoding='utf-8', newline='').write('\n'.join(lines))
        print('patched', BATTERS, 'backup', BATTERS + f'.before_legend_defence_{stamp}')


if __name__ == '__main__':
    sys.stdout.reconfigure(encoding='utf-8')
    main()
