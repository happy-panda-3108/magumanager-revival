"""2026-11-17 최종 육성표 check: every cell of the calculator sheet the spec did not write is as the user saved it, and every written
value reads back. Comments outside the written blocks too. Formulas recalculate, so cells whose value depends on a written block
(합계 rows of the written blocks, the summary cells) are listed apart rather than counted as changed.

  python final_verify.py <spec dir> [book ...]
"""
import glob
import json
import os
import re
import sys

import book_spec as b

sys.stdout.reconfigure(encoding="utf-8")


def rows_of(addr):
    return int(re.sub(r'[A-Z]+', '', addr))


def main():
    spec_dir = sys.argv[1]
    books = sys.argv[2:]
    for path in sorted(glob.glob(os.path.join(spec_dir, "spec_*.json"))):
        book = os.path.basename(path)[5:-5]
        if books and book not in books:
            continue
        s = json.load(open(path, encoding="utf-8"))
        src_cells, src_com = b.read_book(s['src'])
        out = os.path.join(s['out_dir'], s['base'] + '.cell')
        out_cells, out_com = b.read_book(out)
        written = {v[0] for v in s['values']}
        # A written block's rows (header .. 합계) may recalc: rows r .. r+6 of each written block.
        block_rows = set()
        for a in written:
            if a == 'B2':
                continue
            block_rows.add((re.sub(r'\d+', '', a), rows_of(a)))
        bad_written = [a for a in written if v_str(out_cells.get(a)) != v_str(dict(s['values'])[a])]
        changed, recalc = [], []
        for a in set(src_cells) | set(out_cells):
            if a in written:
                continue
            if v_str(src_cells.get(a)) != v_str(out_cells.get(a)):
                # 합계 row of a written block (row+6) or a formula summary: recalculated, expected to move.
                r = rows_of(a)
                if any(r - 6 <= rr <= r for _, rr in block_rows):
                    recalc.append(a)
                else:
                    changed.append(a)
        com_written = {c[0] for c in s['comments']}
        com_changed = [a for a in set(src_com) | set(out_com) if a not in com_written and strip(src_com.get(a)) != strip(out_com.get(a))]
        print(f"{book}: 쓴 칸 {len(written)} (다르게 읽힘 {len(bad_written)}), 그 밖에 바뀐 칸 {len(changed)} {sorted(changed)[:12]}, "
              f"다시 계산된 합계 {len(recalc)}, 바뀐 메모(쓴 칸 밖) {len(com_changed)} {com_changed[:6]}")


def v_str(v):
    if isinstance(v, float) and v == int(v):
        v = int(v)
    return "" if v is None else str(v)


def strip(text):
    return re.sub(r'^[^\n:]{1,30}:\n', '', text or '')


if __name__ == '__main__':
    main()
