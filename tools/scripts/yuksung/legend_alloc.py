"""2026-11-10: 특강 / 트레이닝 / 재계약 for the 레전드 (and other newly measured) players of the 11-10 books.

  python legend_alloc.py <out.json> <plan log> [<plan log> ...]

Reads the plan logs (tcsim plan with TCSIM_BATS / TCSIM_PLAN_SLOTS, only the new slots measured) and plans each measured
player with alloc.optimize, using the settings of the user's own book for that deck (팀컬러 O2, 코칭스태프 세트 S2,
타격코치 S3/S4, 투수코치 S5/S6) and the 특강 coach table of the 11-07 books. Keys: deck label → card idx (or name).
"""
import json
import os
import sys

import alloc
import book_spec

BOOK_OF = {'2025 LG': '25LG', '2012 삼성': '12삼성', '2001 롯데': '01롯데', '2011 롯데': '11롯데', '1997 쌍방울': '97쌍방울',
           '2001 현대': '01현대', '1993 롯데': '93롯데', '1996 쌍방울': '96쌍방울'}
XLSX = r"C:\Ma9MProject\docs\육성표\1107\육성표_1107_{}.xlsx"


def settings(deck):
    cells, _ = book_spec.read_book(os.path.join(book_spec.BOOKS, f"육성표_1107_{BOOK_OF[deck]}.cell"))
    tc, setb = int(cells.get('O2') or 0), int(cells.get('S2') or 0)
    hc = (alloc.BAT.index(cells['S3']), int(cells.get('S4') or 0)) if cells.get('S3') in alloc.BAT else None
    pc = (alloc.PIT.index(cells['S5']), int(cells.get('S6') or 0)) if cells.get('S5') in alloc.PIT else None
    return tc, setb, hc, pc


def main():
    out_path, logs = sys.argv[1], sys.argv[2:]
    cards = book_spec.load_cards()
    legend_idx = {(c['kind'], c['name']): idx for idx, c in cards.items() if 800101 <= int(idx) <= 800299}
    out = json.load(open(out_path, encoding='utf-8')) if os.path.exists(out_path) else {}
    for log in logs:
        for label, deck in alloc.parse_log(log).items():
            base = label.split('#')[0]
            tc, setb, hc, pc = settings(base)
            coaches = alloc.load_coaches(XLSX.format(BOOK_OF[base]))
            for p in deck['players']:
                fixed = [tc + setb] * 6
                stat = hc if p['kind'] == 'B' else pc
                if stat:
                    fixed[stat[0]] += stat[1]
                opts = [(c, coaches[p['kind']][c]) for c in p['coaches'] if c in coaches[p['kind']]] or [(None, [0] * 6)]
                r = alloc.optimize(p, opts, fixed, 3, 6, 6, 3)
                key = legend_idx.get((p['kind'], p['name']), p['name'])
                out.setdefault(base, {})[key] = dict(p, **r, fixed=fixed, source=os.path.basename(log))
                names = alloc.BAT if p['kind'] == 'B' else alloc.PIT
                fmt = lambda v: ' '.join(f'{names[k]}{v[k]}' for k in range(6) if v[k])
                print(f"{base} {p['slot']} {p['name']}({key}) 특강 {r['coach']} | 트레이닝 {fmt(r['training'])} | 재계약 {fmt(r['renewal'])} | "
                      f"+9 승률 {[p['g9'][k] for k in range(6)]} | 표시 {r['shown']} | +{r['value']:.2f}%p")
    json.dump(out, open(out_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)


if __name__ == '__main__':
    main()
