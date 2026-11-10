"""2026-11-10: checks the 레전드 books against the user's books and the allocation.

  python book_verify.py <legend_alloc.json> <out dir>

Per deck (book_spec.DECKS), reading the written .cell and .xlsx back from their XML:
- every player kept from the user's book has the same 기본 / 특강 코치 / 트레이닝 / 재계약 as there (wherever his block moved);
- the new and re-planned blocks carry the allocation, and their 합계 equals the allocation's shown values (a 포지션 변경
  레전드: less what the change moves, as 기본 is the card before the change);
- every 합계 and 경기 반영 cell has a value, none above 140; the 특강 row is filled where a coach is named.
"""
import json
import os
import sys

import book_spec as b

sys.stdout.reconfigure(encoding="utf-8")
alloc = json.load(open(sys.argv[1], encoding="utf-8"))
out_dir = sys.argv[2]
cards = b.load_cards()
bad = 0


def rows(cells, cols, r, k):
    return [cells.get(f"{cols[1 + j]}{r + k}") for j in range(6)]


for deck, conf in b.DECKS.items():
    src_cells, src_com = b.read_book(os.path.join(b.BOOKS, conf["book"]))
    for ext in (".cell", ".xlsx"):
        path = os.path.join(out_dir, conf["out"] + ext)
        cells, com = b.read_book(path)
        kept = new = 0
        problems = []
        for kind, cols, key in (("B", b.BAT_COLS, "bats"), ("P", b.PIT_COLS, "staff")):
            old = {x["name"]: x for x in b.blocks(src_cells, src_com, kind)}
            now = b.blocks(cells, com, kind)
            entries = conf.get(key)
            if entries is None or isinstance(entries, dict):
                src_order = b.blocks(src_cells, src_com, kind)
                entries = [(entries or {}).get(k, (x["name"], x["slot"])) for k, x in enumerate(src_order)]
            for k, (who, slot) in enumerate(entries):
                r = b.FIRST + k * b.STRIDE
                blk = now[k]
                total, play, lec = rows(cells, cols, r, 6), rows(cells, cols, r, 7), rows(cells, cols, r, 3)
                if blk["slot"] != slot:
                    problems.append(f"{slot}: 자리 글 {blk['slot']}")
                if any(v is None for v in total + play) or any(v > 140 for v in total):
                    problems.append(f"{slot} {blk['name']}: 합계 {total} 경기 반영 {play}")
                if blk["coach"] and any(v is None for v in lec):
                    problems.append(f"{slot} {blk['name']}: 특강 줄 {lec}")
                a = alloc.get(deck, {}).get(who)
                num = lambda v: [x or 0 for x in v]
                if a is None:
                    o = old[who]
                    kept += 1
                    if blk["name"] != who or blk["base"] != o["base"] or blk["coach"] != o["coach"] or num(blk["training"]) != num(o["training"]) or num(blk["renewal"]) != num(o["renewal"]):
                        problems.append(f"{slot} {who}: 사용자 책과 다름")
                    continue
                new += 1
                card = cards[who] if who in cards else None
                base = b.card_base(card) if card else old[who]["base"]
                if blk["base"] != base or blk["coach"] != (a["coach"] or None) or num(blk["training"]) != a["training"] or num(blk["renewal"]) != a["renewal"]:
                    problems.append(f"{slot} {blk['name']}: 배분과 다름 {blk['base']} {blk['coach']} {blk['training']} {blk['renewal']}")
                if card and not blk["tag"].startswith("레전드"):
                    problems.append(f"{slot} {blk['name']}: 태그 {blk['tag']}")
                # 합계 = the allocation's shown value, less the position-change move (기본 is the card before it); a
                # batter's 수비 is left out (the log's defence is the spot's, the book's the main position's).
                for j in range(6):
                    if a["shown"][j] is None or a["card"][j] is None or (kind == "B" and j == 4):
                        continue
                    moved = a["card"][j] - blk["base"][j] if (card and "→" in slot) or (not card and kind == "P") else 0
                    if total[j] != a["shown"][j] - moved:
                        problems.append(f"{slot} {blk['name']} {(b.BAT if kind == 'B' else b.PIT)[j]}: 합계 {total[j]} ≠ 배분 {a['shown'][j]} − 변경 {moved}")
        bad += len(problems)
        print(f"{conf['out']}{ext}: 그대로 {kept}칸, 새로 {new}칸, 메모 {sorted(k for k in com if k[0] in 'BJ')}, 문제 {len(problems)}" + ("".join("\n    " + p for p in problems)))
print("모두 정상" if bad == 0 else f"문제 {bad}건")
