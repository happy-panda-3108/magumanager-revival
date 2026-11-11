"""2026-11-11 (user 20:1x: "육성표에 레전드 투수가 반영되면서 다시 포지션 변경을 돌려야하는 선수들도 반영해줘 (01롯데 문동환 중계) 그리고 선발들
승률별로 순서도 변경해서 다시 정렬해줘."; 20:3x: "못 먹은 선수에 대해서도 고려해서 최선의 육성표를 만들어주고, 중계, 선발 후보들의 경우
마무리 아래쪽에 더 육성 내용을 추가해서 작성해주는걸로 합시다. 후보도 육성은 해두고 주전을 획득하면 교체해야하니까", answers "이 매칭대로,
93롯데는 빼기" and "투수 후보만"): the 1110 레전드 books as the user saved them, with
- the bullpen slot a 레전드 pushed out filled again (legend3.py CHOSEN; numbers planned again in relief_alloc.json, or the
  user's own 11-07 block when the player keeps a bullpen role),
- SP1-5 in the order of each starter's win% when he starts every game (sporder.log, 50,000 games a starter),
- 후보 blocks under the 마무리 for the pitchers the club plays until it owns the starter (rows inserted before the notes,
  each a copy of the 마무리 block with the candidate's numbers: the user's 11-07 block, or relief_alloc.json).

  python book_spec3.py <relief_alloc.json> <sporder.log> <spec dir> [book ...]

Writes spec_<book>.json for book_edit.ps1 (in place: out_dir is the books' folder) and prints what moves.
"""
import json
import os
import re
import sys

import book_spec as b

sys.stdout.reconfigure(encoding="utf-8")
SRC = r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드"
OLD = r"C:\Ma9MProject\docs\육성표\한셀_1107"
LABEL = {"01롯데": "2001 롯데", "11롯데": "2011 롯데", "93롯데": "1993 롯데", "25LG": "2025 LG", "12삼성": "2012 삼성",
         "97쌍방울": "1997 쌍방울", "96쌍방울": "1996 쌍방울", "01현대": "2001 현대"}
COLS = b.PIT_COLS
NOTES_ROW = 103          # the first note row under the 마무리 block (B103-B105)
CP_BLOCK = "J95:P102"    # the 마무리 block, the copy source of a 후보 block

# legend3.log (11-11 22:2x, vs the 11-02 deck league, 133 decks × 4,000): book → (who leaves, who comes, slot, source, comment).
# source "alloc" = planned again in the new role (relief.log), "1107" = the user's 11-07 block (same kind of role).
SWAP = {
    "01롯데": ("기론", "문동환", "RP4", "alloc",
             "최동원(레전드)을 얻은 뒤의 자리입니다. 이 카드는 지금 선발로 포지션 변경(대성공)되어 있어, 포지션 변경 초기화권(80만 MP)으로 "
             "원래 보직 중계로 되돌려 기론 대신 이 칸에 넣습니다(승률 70.78% → 71.22%). 최동원을 얻기 전에는 아래 후보 칸처럼 SP5입니다. "
             "재계약 보너스는 되돌릴 수 없으니 후보 칸 숫자대로 둔 채, 옮길 때 트레이닝(덮어쓰기)·특강(초기화권)만 이 칸 숫자로 바꾸면 됩니다."),
    "11롯데": ("이재곤", "고원준", "RP3 (선발→중계)", "alloc",
             "최동원(레전드)을 얻은 뒤의 자리입니다. 중계로 포지션 변경(40만 MP, 성공 기준으로 계산)해 이재곤 대신 이 칸에 넣습니다"
             "(승률 63.68% → 64.20%; 강영식 대신 63.85, 진명호 대신 63.99, 김일엽 대신 63.94). 기본은 포지션 변경 전 카드값이고 "
             "바꾸면 {change}이 됩니다. 최동원을 얻기 전에는 아래 후보 칸처럼 SP3입니다. 재계약 보너스는 되돌릴 수 없으니 후보 칸 숫자대로 "
             "둔 채, 옮길 때 트레이닝·특강만 이 칸 숫자로 바꾸면 됩니다."),
    "93롯데": ("박동희", "김정훈A", "RP3", "1107",
             "최동원(레전드)이 SP5로 들어오며 역할 밖 RP3로 밀린 박동희(선발이라 중계로 못 바꿈) 대신입니다(승률 64.56% → 64.93%). "
             "숫자는 11/7 책(그때 RP4)의 값 그대로입니다."),
}

# Pitchers the club plays until it owns the starter (DB 11-11 20:3x: 01롯데 SKY마토 has no 최동원L; 11롯데 SKY준우 no 최동원L
# and no 진명호; 12삼성 SKY승환 no 오승환L). (who, source, slot, comment)
CAND = {
    "01롯데": [("문동환", "1107", "후보 SP5 (중계→선발·대성공)",
               "최동원(레전드)을 얻기 전까지 SP5입니다(그 투수진 67.15%). 숫자는 11/7 책 그대로입니다. 최동원을 얻으면 위 RP4 칸 설명대로 "
               "중계로 되돌려 옮깁니다.")],
    "11롯데": [("고원준", "1107", "후보 SP3",
               "최동원(레전드)을 얻기 전까지 SP3입니다. 숫자는 11/7 책 그대로입니다. 최동원을 얻으면 위 RP3 칸 설명대로 중계로 바꿔 옮깁니다."),
              ("허준혁A", "alloc", "후보 RP2",
               "진명호를 얻기 전까지 RP2입니다(가진 중계 중: 허준혁A 60.13%, 김수완 60.08%, 이명우 59.97% — 차이가 오차 안이라 셋 다 "
               "비슷합니다; 최동원·진명호 없이 고원준 SP3일 때). 진명호를 얻으면 위 RP2 칸으로 바꿉니다.")],
    "12삼성": [("오승환", "1107", "후보 CP ('12 오승환)",
               "레전드 오승환을 얻기 전까지 마무리입니다(그 투수진 73.38%). 숫자는 11/7 책 그대로입니다. 레전드를 얻으면 위 CP 칸으로 바꿉니다.")],
}


def grade_of(block):
    return (block.get('tag') or '').split(' ')[0]


def parse_starts(path):
    out = {}
    raw = open(path, 'rb').read()
    try:
        text = raw.decode('utf-8')
    except UnicodeDecodeError:
        text = raw.decode('cp949', errors='replace')
    for line in text.splitlines():
        m = re.search(r'tcsim plan: (.+?) 선발 등판 승률 \| (.*)$', line)
        if m:
            out[m.group(1)] = {re.sub(r'\(.*\)$', '', x.group(2)): float(x.group(3))
                               for x in re.finditer(r'(SP\d) (\S+) ([\d.]+)%', m.group(2))}
    return out


def card_row(name, year, club):
    for r in b.load_cards().values():
        if r['kind'] == 'P' and r.get('fullname', r.get('name')) == name and r.get('year') == year and r.get('teamcode') == club:
            return r
    return None


def block_from(book, who, source, alloc, label):
    """The block a player brings: the user's 11-07 block (source "1107") or a new one from the plan (source "alloc")."""
    old_cells, old_com = b.read_book(os.path.join(OLD, f"육성표_1107_{book}.cell"))
    old = {x['name']: x for x in b.blocks(old_cells, old_com, 'P')}
    a = alloc.get(label, {}).get(who)
    if source == "1107" and who in old:
        return dict(old[who]), "11/7 책 블록 그대로"
    if who in old:   # same card, numbers planned again in the new role
        blk = dict(old[who])
        blk.update(coach=a['coach'] or '', training=a['training'], renewal=a['renewal'])
        return blk, f"새 보직으로 다시 계산: 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"
    year, club = {"2011 롯데": ("2011", "7"), "2001 롯데": ("2001", "7"), "1993 롯데": ("1993", "7")}[label]
    c = card_row(who, year, club)
    grade = b.GRADE[c['cardgrade']]
    blk = {'tag': f"{grade} 투수", 'name': who, 'base': b.card_base(c), 'coach': a['coach'] or '', 'training': a['training'],
           'renewal': a['renewal'], 'comment': None}
    return blk, f"새 블록: 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"


def change_text(blk, a):
    moved = [(b.PIT[k], a['card'][k] - blk['base'][k]) for k in range(6) if a['card'][k] is not None and a['card'][k] != blk['base'][k]]
    return ', '.join(f"{n} {d:+d}" for n, d in moved)


def write_block(values, fills, fonts, notes, r, blk, slot, comment):
    grade = grade_of(blk)
    values += [[f"{COLS[0]}{r}", f"{grade} 투수"], [f"{COLS[1]}{r}", b.GRADE_EN[grade]], [f"{COLS[6]}{r}", slot],
               [f"{COLS[0]}{r + 1}", blk['name']], [f"{COLS[0]}{r + 3}", blk['coach'] or None]]
    for j in range(6):
        values += [[f"{COLS[1 + j]}{r + 2}", blk['base'][j]], [f"{COLS[1 + j]}{r + 4}", b.num(blk['training'][j])],
                   [f"{COLS[1 + j]}{r + 5}", b.num(blk['renewal'][j])]]
    b.paint(fills, fonts, COLS, r, grade)
    # 한셀 puts its author line ("honey:") in front of every comment it saves; written back as is it would stack.
    notes.append([f"{COLS[0]}{r + 1}", re.sub(r'^[^\n:]{1,30}:\n', '', comment or "")])


def sheet_rows(path, sheet):
    keep = b.SHEET
    b.SHEET = sheet
    try:
        cells, _ = b.read_book(path)
    finally:
        b.SHEET = keep
    return cells


def spec(book, alloc, starts, spec_dir):
    label = LABEL[book]
    path = os.path.join(SRC, f"육성표_1110_{book}_레전드.cell")
    cells, comments = b.read_book(path)
    staff = b.blocks(cells, comments, 'P')
    values, fills, fonts, notes, report, extra = [], [], [], [], [], []
    slots = [x['slot'] for x in staff]
    said = []
    # 1. The bullpen slot a 레전드 pushed out.
    if book in SWAP:
        out_name, in_name, slot, source, comment = SWAP[book]
        k = next(i for i, x in enumerate(staff) if x['name'] == out_name)
        blk, src = block_from(book, in_name, source, alloc, label)
        if '{change}' in comment:
            comment = comment.replace('{change}', change_text(blk, alloc[label][in_name]))
        blk['comment'] = comment
        staff[k] = blk
        slots[k] = slot
        report.append(f"  {slot}: {out_name} → {in_name} ({src})")
    # 2. SP1-5 by their win% when they start every game.
    win = starts.get(label, {})
    sp = list(range(5))
    if all(staff[i]['name'] in win for i in sp):
        ranked = sorted(sp, key=lambda i: -win[staff[i]['name']])
        suffix = [re.sub(r'^SP\d', '', slots[i] or '') for i in sp]
        new_staff = [staff[i] for i in ranked] + staff[5:]
        new_slots = [f"SP{n + 1}{suffix[i]}" for n, i in enumerate(ranked)] + slots[5:]
        order = ', '.join(f"{staff[i]['name']} {win[staff[i]['name']]:.2f}" for i in ranked)
        report.append(f"  선발 순서: {order}" + (" (그대로)" if ranked == sp else ""))
        said.append("선발은 매 경기 그 투수가 선발로 나올 때의 승률 순서로 다시 놓았습니다(5만 경기씩): " + order + "%"
                    + (" — 이전 순서와 같습니다." if ranked == sp else "."))
        staff, slots = new_staff, new_slots
    else:
        report.append(f"  선발 순서: 측정 없음 {sorted(win)} / {[staff[i]['name'] for i in sp]}")
    for k, blk in enumerate(staff):
        write_block(values, fills, fonts, notes, b.FIRST + k * b.STRIDE, blk, slots[k], blk.get('comment'))
    # 3. 후보 blocks under the 마무리.
    cands = CAND.get(book, [])
    insert, copy = [], []
    shift = 0
    if cands:
        shift = 9 * len(cands) + 1
        insert.append([NOTES_ROW, shift])
        values.append([f"{COLS[0]}{NOTES_ROW}", "후보 투수: 아직 없는 선수 대신 뛰는 동안의 육성입니다(그 선수를 얻으면 위 칸 선수로 바꿉니다)."])
        for n, (who, source, slot, comment) in enumerate(cands):
            r = NOTES_ROW + 1 + 9 * n
            copy.append([CP_BLOCK, f"{COLS[0]}{r}"])
            blk, src = block_from(book, who, source, alloc, label)
            # The 11-07 block's own note (what its 포지션 변경 moves) stays after ours.
            own = re.sub(r'^[^\n:]{1,30}:\n', '', blk.get('comment') or '')
            write_block(values, fills, fonts, notes, r, blk, slot, comment + (" " + own if own else ""))
            report.append(f"  {slot}: {who} ({src})")
        said.append("마무리 아래에 후보 칸을 넣었습니다: " + ", ".join(f"{c[2]} {c[0]}" for c in cands) + ".")
    if book in SWAP:
        out_name, in_name, slot, _, _ = SWAP[book]
        said.insert(0, f"{slot}: {out_name} 대신 {in_name}(이름 칸 메모에 이유와 승률).")
    # Notes moved down by the inserted rows; the 11/11 line goes to the end of the long note (B104).
    note_cell = f"B{104 + shift}"
    note_now = cells.get("B104") or ""
    if said:
        values.append([note_cell, note_now + " 11/11 투수진: " + " ".join(said)])
        title = cells.get('B2') or ''
        if '투수진 11/11' not in title:
            values.append(['B2', title + ' · 투수진 11/11'])
    # 스탯 가치: the players planned again, under the rows already there.
    stat = sheet_rows(path, '스탯 가치')
    row = 34
    while stat.get(f"A{row}") not in (None, ""):
        row += 1
    for who, a in alloc.get(label, {}).items():
        if not any(who == x['name'] for x in staff) and not any(who == c[0] for c in cands):
            continue
        shown = ' / '.join(f"{b.PIT[k]} {a['shown'][k]}" for k in range(6) if a['shown'][k] is not None and a['g9'][k] is not None)
        line = [a['slot'] + ' (11/11)', a['name'], a['ball']] + [a['g9'][k] for k in range(6)] + [a['coach'] or '', shown]
        extra += [['스탯 가치', f"{'ABCDEFGHIJK'[j]}{row}", v] for j, v in enumerate(line)]
        row += 1
    out = os.path.join(spec_dir, f"spec_{book}.json")
    s = {'src': path, 'out_dir': SRC, 'base': f"육성표_1110_{book}_레전드", 'sheet': b.SHEET, 'insert_rows': insert, 'copy': copy,
         'values': values, 'fills': fills, 'fonts': fonts, 'comments': notes, 'extra': extra}
    json.dump(s, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    print(f"== {book} → {out}")
    print("\n".join(report))


if __name__ == '__main__':
    alloc = json.load(open(sys.argv[1], encoding='utf-8'))
    starts = parse_starts(sys.argv[2])
    os.makedirs(sys.argv[3], exist_ok=True)
    for book in (sys.argv[4:] or list(LABEL)):
        spec(book, alloc, starts, sys.argv[3])
