"""2026-11-11: checks book_spec3.py's books against the backup taken before (the user's books as saved):
  python book_verify3.py <backup dir> <relief_alloc.json> [book ...]
- every pitcher kept from the backup has the same 기본 / 특강 / 트레이닝 / 재계약 wherever his block moved, the batters untouched;
- the swapped-in pitcher carries the planned numbers (or the 11-07 block), the 후보 blocks theirs;
- every 합계 and 경기 반영 cell of the 11 blocks and the 후보 blocks has a number, none above 140;
- the notes moved under the 후보 blocks (B103.. → B(103 + 9n + 1)..).
"""
import json
import os
import sys

import book_spec as b
import book_spec3 as s3

sys.stdout.reconfigure(encoding="utf-8")
backup, alloc = sys.argv[1], json.load(open(sys.argv[2], encoding="utf-8"))
bad = 0


def key(x):
    return (x['base'], x['coach'] or None, [v or None for v in x['training']], [v or None for v in x['renewal']])


def block_at(cells, comments, cols, r):
    return {'name': cells.get(f"{cols[0]}{r + 1}"), 'slot': cells.get(f"{cols[6]}{r}"),
            'base': [cells.get(f"{cols[1 + j]}{r + 2}") for j in range(6)], 'coach': cells.get(f"{cols[0]}{r + 3}"),
            'training': [cells.get(f"{cols[1 + j]}{r + 4}") for j in range(6)], 'renewal': [cells.get(f"{cols[1 + j]}{r + 5}") for j in range(6)],
            'sum': [cells.get(f"{cols[1 + j]}{r + 6}") for j in range(6)], 'game': [cells.get(f"{cols[1 + j]}{r + 7}") for j in range(6)]}


for book in (sys.argv[3:] or list(s3.LABEL)):
    label = s3.LABEL[book]
    name = f"육성표_1110_{book}_레전드"
    old_cells, old_com = b.read_book(os.path.join(backup, name + ".cell"))
    old_p = {x['name']: x for x in b.blocks(old_cells, old_com, 'P')}
    old_b = b.blocks(old_cells, old_com, 'B')
    for ext in (".cell", ".xlsx"):
        cells, com = b.read_book(os.path.join(s3.SRC, name + ext))
        problems = []
        if [key(x) for x in b.blocks(cells, com, 'B')] != [key(x) for x in old_b]:
            problems.append("타자 칸이 바뀜")
        cands = s3.CAND.get(book, [])
        rows = [b.FIRST + k * b.STRIDE for k in range(11)] + [s3.NOTES_ROW + 1 + 9 * n for n in range(len(cands))]
        swap = s3.SWAP.get(book)
        for k, r in enumerate(rows):
            x = block_at(cells, com, b.PIT_COLS, r)
            if any(not isinstance(v, (int, float)) for v in x['sum'] + x['game']) or any(v > 140 for v in x['game']):
                problems.append(f"{x['slot']} {x['name']}: 합계/경기 반영 {x['sum']} {x['game']}")
            if k >= 11:
                who, source = cands[k - 11][0], cands[k - 11][1]
                want, _ = s3.block_from(book, who, source, alloc, label)
            elif swap and x['name'] == swap[1]:
                want, _ = s3.block_from(book, swap[1], swap[3], alloc, label)
            elif x['name'] in old_p:
                want = old_p[x['name']]
            else:
                problems.append(f"{x['slot']} {x['name']}: 어디서 왔는지 모름")
                continue
            if key(x) != key(want):
                problems.append(f"{x['slot']} {x['name']}: {key(x)} != {key(want)}")
        shift = 9 * len(cands) + 1 if cands else 0
        for n in (103, 104, 105):
            if not (cells.get(f"B{n + shift}") or '').startswith((old_cells.get(f"B{n}") or '')[:40]):
                problems.append(f"메모 B{n} → B{n + shift} 안 맞음")
        starters = ', '.join(str(x['slot']) + ' ' + str(x['name']) for x in (block_at(cells, com, b.PIT_COLS, r) for r in rows[:5]))
        state = 'ok' if not problems else f"{len(problems)} problem(s)"
        print(f"{name}{ext}: {state} | 선발 {starters}" + (" | 후보 " + ', '.join(c[2] + ' ' + c[0] for c in cands) if cands else ""))
        for p in problems:
            print("   ", p)
        bad += len(problems)
print("problems:", bad)
