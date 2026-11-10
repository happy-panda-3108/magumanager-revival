"""2026-11-10 (user: "레전드 포함 육성표를 작성해주길 바람. 한셀 육성표에서 내가 조금씩 수정해두었는데, 거기에 이어서 작업해주면 될 것 같음"):
the user's 한셀 book with its blocks put in a new order (batting order, staff) and 레전드 blocks added, the user's numbers kept.

  python book_spec.py <legend_alloc.json> <out dir> [deck ...]

For each deck in DECKS it reads the user's book (OOXML; openpyxl cannot read 한셀 files), writes a book_edit.ps1 spec
(<out dir>/spec_<deck>.json) and prints what moves. book_edit.ps1 then writes the cells through a hidden 한셀 and saves
.cell + .xlsx. Only input cells change: tag/slot labels, name, 기본, 특강 coach, 트레이닝, 재계약, the grade colours and the
name comments; every formula stays.
"""
import csv
import json
import os
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
BOOKS = r"C:\Ma9MProject\docs\육성표\한셀_1107"
TABLES = r"C:\Ma9MProject\server\data\tables"
NS = {"m": "http://schemas.openxmlformats.org/spreadsheetml/2006/main",
      "r": "http://schemas.openxmlformats.org/officeDocument/2006/relationships"}
RELNS = "{http://schemas.openxmlformats.org/package/2006/relationships}"
SHEET = "육성 계산기"
FIRST, STRIDE = 5, 9
BAT_COLS, PIT_COLS = "BCDEFGH", "JKLMNOP"
BAT = ['컨택', '파워', '스피드', '선구', '수비', 'H정신']
PIT = ['체력', '구속', '컨트롤', '테크닉', '구위', 'P정신']
GRADE = {'1': '노말', '2': '스페셜', '3': '레어', '4': '엘리트', '5': '레전드'}
GRADE_EN = {'노말': 'NORMAL', '스페셜': 'SPECIAL', '레어': 'RARE', '엘리트': 'ELITE', '레전드': 'LEGEND'}
# make_books.py colours, plus 레전드 in dark gold (the app's legend frame), apart from 스페셜 yellow.
GRADE_RGB = {'노말': 'FF2896EC', '스페셜': 'FFFAC61C', '레어': 'FFEC2E38', '엘리트': 'FFAC58F6', '레전드': 'FFB8860B'}
GRADE_LIGHT = {'노말': 'FFEEF6FE', '스페셜': 'FFFFF9E0', '레어': 'FFFDEEEF', '엘리트': 'FFF6EEFE', '레전드': 'FFFBF4E2'}
GRADE_MID = {'노말': 'FFCFE6FC', '스페셜': 'FFFFEDA6', '레어': 'FFFAC9CC', '엘리트': 'FFE3CBFC', '레전드': 'FFF0DBA8'}
HEAD_FONT = {'스페셜': 'FF3D3000'}
DEF = ['def_C', 'def_1B', 'def_2B', 'def_3B', 'def_SS', 'def_LF', 'def_CF', 'def_RF']
SPOT_POS = {'C': 1, '1B': 2, '2B': 3, '3B': 4, 'SS': 5, 'LF': 6, 'CF': 7, 'RF': 8}


def read_book(path):
    """{addr: value} of the calculator sheet and {addr: comment text}."""
    z = zipfile.ZipFile(path)
    shared = []
    if "xl/sharedStrings.xml" in z.namelist():
        for si in ET.fromstring(z.read("xl/sharedStrings.xml")).findall("m:si", NS):
            shared.append("".join(t.text or "" for t in si.iter("{%s}t" % NS["m"])))
    wb = ET.fromstring(z.read("xl/workbook.xml"))
    rels = {r.get("Id"): r.get("Target") for r in ET.fromstring(z.read("xl/_rels/workbook.xml.rels")).iter(RELNS + "Relationship")}
    sheet = next(s for s in wb.find("m:sheets", NS) if s.get("name") == SHEET)
    target = rels[sheet.get("{%s}id" % NS["r"])].lstrip("/")
    target = target if target.startswith("xl/") else "xl/" + target
    cells = {}
    for c in ET.fromstring(z.read(target)).iter("{%s}c" % NS["m"]):
        t, v, isel = c.get("t"), c.find("m:v", NS), c.find("m:is", NS)
        if t == "s" and v is not None:
            val = shared[int(v.text)]
        elif t == "inlineStr" and isel is not None:
            val = "".join(x.text or "" for x in isel.iter("{%s}t" % NS["m"]))
        elif v is not None:
            try:
                f = float(v.text)
                val = int(f) if f == int(f) else f
            except ValueError:
                val = v.text
        else:
            val = None
        cells[c.get("r")] = val
    # The sheet's comments: its rels name the comments part.
    comments = {}
    srels = target.replace("worksheets/", "worksheets/_rels/") + ".rels"
    if srels in z.namelist():
        for r in ET.fromstring(z.read(srels)).iter(RELNS + "Relationship"):
            if r.get("Type", "").endswith("/comments"):
                part = os.path.normpath(os.path.join(os.path.dirname(target), r.get("Target"))).replace("\\", "/")
                for cm in ET.fromstring(z.read(part)).iter("{%s}comment" % NS["m"]):
                    comments[cm.get("ref").split(":")[0]] ="".join(x.text or "" for x in cm.iter("{%s}t" % NS["m"]))
    return cells, comments


def blocks(cells, comments, kind):
    """The book's blocks of one side, in sheet order."""
    cols = BAT_COLS if kind == 'B' else PIT_COLS
    out = []
    for k in range(9 if kind == 'B' else 11):
        r = FIRST + k * STRIDE
        a = lambda c, rr: f"{c}{rr}"
        out.append({
            'tag': cells.get(a(cols[0], r)), 'slot': cells.get(a(cols[6], r)),
            'name': cells.get(a(cols[0], r + 1)), 'comment': comments.get(a(cols[0], r + 1)),
            'base': [cells.get(a(cols[1 + j], r + 2)) for j in range(6)],
            'coach': cells.get(a(cols[0], r + 3)),
            'training': [cells.get(a(cols[1 + j], r + 4)) for j in range(6)],
            'renewal': [cells.get(a(cols[1 + j], r + 5)) for j in range(6)],
        })
    return out


def load_cards():
    cards = {}
    for fn, kind in (('P_BatterTable.txt', 'B'), ('P_PItcherTable.txt', 'P')):
        for r in csv.DictReader(open(os.path.join(TABLES, fn), encoding='utf-8'), delimiter='\t'):
            r['kind'] = kind
            cards[r['idx']] = r
    return cards


def card_base(r):
    if r['kind'] == 'B':
        pos = int(r['mainposition'])
        d = int(r[DEF[pos - 1]]) if 1 <= pos <= len(DEF) else max(int(r[k]) for k in DEF)
        return [int(r['con']), int(r['pow']), int(r['spd']), int(r['eye']), d, int(r['mental'])]
    return [int(r[k]) for k in ('health', 'spd', 'cont', 'technic', 'breakball', 'mental')]


def paint(fills, fonts, cols, r, grade):
    """make_books.fill_side's colours for a block at row r."""
    rgb, light, mid = GRADE_RGB[grade], GRADE_LIGHT[grade], GRADE_MID[grade]
    fonts.append([f"{cols[0]}{r}:{cols[1]}{r}", rgb])
    fills.append([f"{cols[1]}{r + 1}:{cols[6]}{r + 1}", rgb])
    fonts.append([f"{cols[1]}{r + 1}:{cols[6]}{r + 1}", HEAD_FONT.get(grade, 'FFFFFFFF')])
    fills.append([f"{cols[0]}{r + 2}", light])
    fills.append([f"{cols[0]}{r + 3}:{cols[6]}{r + 5}", light])
    fills.append([f"{cols[0]}{r + 6}:{cols[6]}{r + 6}", mid])


def num(v):
    return v if isinstance(v, (int, float)) and v != 0 else None


def spec_for(deck, conf, alloc, cards, out_dir):
    path = os.path.join(BOOKS, conf['book'])
    cells, comments = read_book(path)
    values, fills, fonts, notes = [], [], [], []
    report = []
    for kind, cols, key in (('B', BAT_COLS, 'bats'), ('P', PIT_COLS, 'staff')):
        old = blocks(cells, comments, kind)
        by_name = {b['name']: b for b in old}
        # None = the book's own order; a dict {position: (who, slot)} replaces those blocks only.
        entries = conf.get(key)
        if entries is None or isinstance(entries, dict):
            entries = [(entries or {}).get(k, (b['name'], b['slot'])) for k, b in enumerate(old)]
        for k, (who, slot) in enumerate(entries):
            r = FIRST + k * STRIDE
            if who in by_name:
                b = dict(by_name[who])
                grade = (b['tag'] or '').split(' ')[0]
                src = f"{who}: 사용자 책 그대로"
                if '→' not in slot:
                    b['comment'] = None   # no 포지션 변경 in this spot any more
                a = alloc.get(deck, {}).get(who)
                if a:   # measured again in his new role
                    b.update(coach=a['coach'] or '', training=a['training'], renewal=a['renewal'])
                    src = f"{who}: 새 보직으로 다시 계산, 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"
            else:
                c = cards[who]
                a = alloc[deck][who]
                grade = GRADE[c['cardgrade']]
                b = {'name': c['name'], 'base': card_base(c), 'coach': a['coach'] or '',
                     'training': a['training'], 'renewal': a['renewal'], 'comment': change_note(c, a) if '→' in slot else None}
                src = f"{c['name']}({who}): 새 블록, 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"
            names = BAT if kind == 'B' else PIT
            values += [[f"{cols[0]}{r}", f"{grade} {'타자' if kind == 'B' else '투수'}"], [f"{cols[1]}{r}", GRADE_EN[grade]],
                       [f"{cols[6]}{r}", slot], [f"{cols[0]}{r + 1}", b['name']], [f"{cols[0]}{r + 3}", b['coach'] or None]]
            for j in range(6):
                values += [[f"{cols[1 + j]}{r + 2}", b['base'][j]], [f"{cols[1 + j]}{r + 4}", num(b['training'][j])],
                           [f"{cols[1 + j]}{r + 5}", num(b['renewal'][j])]]
            paint(fills, fonts, cols, r, grade)
            notes.append([f"{cols[0]}{r + 1}", b.get('comment') or ""])
            old_k = next((i for i, o in enumerate(old) if o['name'] == b['name']), None)
            report.append(f"  {kind} {k + 1:2} {slot:16} {src}{'' if old_k == k else f' (전에는 {old_k + 1}번째 칸)' if old_k is not None else ''}")
    note_cell = 'B104'
    values.append([note_cell, (cells.get(note_cell) or '') + ' ' + conf['note']])
    values.append(['B2', (cells.get('B2') or '') + ' · 레전드 포함 (11/10)'])
    # 스탯 가치 sheet: the newly measured players under the old table (rows 34 on), as that sheet's rows are laid out.
    extra = [['스탯 가치', 'A33', "11/10 레전드 포함 구성에서 새로 잰 선수 (+9 승률 변화 %p, 20만 경기)"]]
    row = 34
    for who, a in alloc.get(deck, {}).items():
        names = BAT if a['kind'] == 'B' else PIT
        shown = ' / '.join(f"{names[k]} {a['shown'][k]}" for k in range(6) if a['shown'][k] is not None and a['g9'][k] is not None)
        line = [a['slot'], a['name'] + (' (레전드)' if who.isdigit() else ''), a['ball']] + [a['g9'][k] for k in range(6)] + [a['coach'] or '', shown]
        extra += [['스탯 가치', f"{'ABCDEFGHIJK'[j]}{row}", v] for j, v in enumerate(line)]
        row += 1
    spec = {'src': path, 'out_dir': out_dir, 'base': conf['out'], 'sheet': SHEET,
            'values': values, 'fills': fills, 'fonts': fonts, 'comments': notes, 'extra': extra}
    out = os.path.join(out_dir, f"spec_{conf['out']}.json")
    json.dump(spec, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
    print(f"== {deck} → {out}")
    print("\n".join(report))


def change_note(card, a):
    """A 포지션 변경 레전드: 기본 stays the card's values; what the change moves (plan log card values − table)."""
    names = PIT
    base = card_base(card)
    moved = [(names[k], a['card'][k] - base[k]) for k in range(6) if a['card'][k] is not None and a['card'][k] != base[k]]
    return ('기본은 포지션 변경 전 카드값입니다. 이 보직으로 바꾸면(대성공 기준) '
            + ', '.join(f"{n} {d:+d}" for n, d in moved) + ' 이 되고, 경기에서는 바뀐 값으로 뜁니다.')


def order(*pairs):
    """("이름 또는 카드번호", "자리") in batting order → [(who, "n번 자리")]."""
    return [(who, f"{k + 1}번 {spot}") for k, (who, spot) in enumerate(pairs)]


# Filled from the 11-10 entry results (legend1.log, legend2.log; win% vs the 11-02 deck league, 133 decks × 4,000 games);
# `who` is a name in the user's book or a 레전드 card idx.
N = ("11/10 레전드 포함: 덱 경쟁 리그 133덱과 4,000경기씩 붙여 레전드 타자·투수를 넣는 구성을 비교해 승률이 가장 높은 안으로 "
     "칸을 다시 놓았습니다. 금색 LEGEND 칸이 새로 넣은 레전드이고(배분은 11/10에 20만 경기씩 새로 잰 추천), 나머지 선수의 숫자는 "
     "이전 책 그대로입니다(직접 고치신 값 포함). ")
DECKS = {
    '2001 현대': {
        'book': '육성표_1107_01현대.cell', 'out': '육성표_1110_01현대_레전드',
        'bats': order(('박진만', 'SS'), ('전준호A', 'LF'), ('800207', 'CF'), ('심정수', 'RF'), ('박경완', 'C'), ('이숭용', '1B'),
                      ('퀸란', '3B'), ('필립스', 'DH'), ('박종호', '2B')),
        'staff': [('임선동', 'SP1'), ('마일영', 'SP2'), ('전준호B', 'SP3'), ('테일러A', 'SP4'), ('800107', 'SP5'), ('권준헌', 'RP1'),
                  ('송신영', 'RP2'), ('박장희', 'RP3'), ('이상열', 'RP4'), ('신철인', 'SU'), ('위재영', 'CP')],
        'note': N + "승률 64.93% → 레전드 박재홍(3번 중견수, '01 박재홍 대신) 66.82% → 정민태(SP5, 김수경 대신)까지 68.62%. 퀸란은 7번, "
                    "박경완은 5번이 가장 좋았고(박경완 7번·퀸란 5번은 66.20%), 지명타자는 필립스가 '01 이명수보다 1.6%p 높았습니다. "
                    "정민태가 들어오면 송신영은 원래 보직인 중계(RP2)로 돌아가 그 칸을 다시 계산했습니다. 정민태가 없으면 투수진은 11/7 그대로"
                    "(송신영 SP5, 김수경 RP4) 두면 됩니다.",
    },
    '1997 쌍방울': {
        'book': '육성표_1107_97쌍방울.cell', 'out': '육성표_1110_97쌍방울_레전드',
        'staff': {10: ('800108', 'CP')},
        'note': N + "승률 59.08% → 레전드 조규제(마무리, '97 조규제 대신) 59.65%. 레전드 김기태는 '97 엘리트 김기태보다 낮아(58.15%) "
                    "넣지 않았습니다.",
    },
    '1996 쌍방울': {
        'book': '육성표_1107_96쌍방울.cell', 'out': '육성표_1110_96쌍방울_레전드',
        'bats': order(('김광림', 'CF'), ('김실', 'RF'), ('800208', '1B'), ('박경완', 'C'), ('심성보', 'LF'), ('석수철', '3B'),
                      ('조원우', 'DH'), ('최태원', '2B'), ('김호', 'SS')),
        'note': N + "승률 66.94% → 레전드 김기태(3번 1루, '96 김기태 대신) 69.15%. 레전드 조규제는 '96 조규제보다 낮아(65.81%) "
                    "넣지 않았습니다.",
    },
    '2025 LG': {
        'book': '육성표_1107_25LG.cell', 'out': '육성표_1110_25LG_레전드',
        'bats': order(('박해민', 'CF'), ('신민재', '2B'), ('오스틴', '1B'), ('800203', 'LF'), ('박동원', 'C'), ('김현수A', 'DH'),
                      ('문성주', 'RF'), ('구본혁', '3B'), ('오지환', 'SS')),
        'staff': {4: ('800103', 'SP5 (마무리→선발·대성공)')},
        'note': N + "승률 71.11% → 레전드 박용택(4번 좌익, 김현수 지명, 문보경 빠짐) 73.11% → 김용수(선발 대성공, 에르난데스 대신)까지 "
                    "78.42%. 1군 볼 합계가 154라 챔피언스(상한 155)에서는 되지만 레전드 리그(150)에서는 후보를 낮은 볼로 바꿔야 합니다.",
    },
    '2012 삼성': {
        'book': '육성표_1107_12삼성.cell', 'out': '육성표_1110_12삼성_레전드',
        'bats': order(('800202', 'DH'), ('박한이', 'RF'), ('박석민', '3B'), ('최형우', 'LF'), ('이승엽', '1B'), ('진갑용', 'C'),
                      ('조동찬', '2B'), ('정형식', 'CF'), ('김상수B', 'SS')),
        'staff': {10: ('800102', 'CP')},
        'note': N + "승률 69.34% → 레전드 양준혁(1번 지명, 배영섭 빠짐) 73.39% → 레전드 오승환('12 오승환 대신)까지 73.86%.",
    },
    '2001 롯데': {
        'book': '육성표_1107_01롯데.cell', 'out': '육성표_1110_01롯데_레전드',
        'bats': order(('김주찬', 'DH'), ('박현승', '3B'), ('호세', 'LF'), ('800201', '1B'), ('조경환', 'RF'), ('최기문', 'C'),
                      ('김민재', 'SS'), ('박정태A', '2B'), ('김대익', 'CF')),
        'staff': {4: ('800101', 'SP5')},
        'note': N + "승률 62.77% → 레전드 이대호(4번 1루, 얀 빠짐) 67.14% → 최동원(SP5, 문동환 대신)까지 70.75%.",
    },
    '2011 롯데': {
        'book': '육성표_1107_11롯데.cell', 'out': '육성표_1110_11롯데_레전드',
        'staff': {2: ('800101', 'SP3')},
        'note': N + "승률 60.62% → 최동원(SP3, 고원준 대신) 63.62%. 레전드 이대호는 '11 이대호와 승률이 같아(60.61%) 넣지 않았습니다.",
    },
    '1993 롯데': {
        'book': '육성표_1107_93롯데.cell', 'out': '육성표_1110_93롯데_레전드',
        'bats': order(('김민재', '2B'), ('전준호A', 'CF'), ('800201', 'DH'), ('김민호A', '1B'), ('김응국', 'LF'), ('공필성', '3B'),
                      ('박계원', 'SS'), ('이종운', 'RF'), ('강성우', 'C')),
        'staff': [('김상현A', 'SP1'), ('염종석', 'SP2'), ('윤형배', 'SP3'), ('윤학길', 'SP4'), ('800101', 'SP5'), ('김태석', 'RP1'),
                  ('이상번', 'RP2'), ('박동희', 'RP3 (역할 밖)'), ('가득염', 'RP4'), ('김태형B', 'SU'), ('서호진', 'CP')],
        'note': N + "승률 58.12% → 레전드 이대호(3번 지명, 김종헌 빠짐) 62.38% → 최동원(SP5)까지 64.33%. 최동원이 들어오면 가득염은 "
                    "원래 보직인 중계(RP4, 김정훈 대신)로 돌아가 그 칸을 다시 계산했습니다.",
    },
}

if __name__ == '__main__':
    alloc = json.load(open(sys.argv[1], encoding='utf-8'))
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    cards = load_cards()
    for deck in (sys.argv[3:] or list(DECKS)):
        spec_for(deck, DECKS[deck], alloc, cards, out_dir)
