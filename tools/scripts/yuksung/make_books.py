"""육성표 (2026-11-05): the 11-02 calculator layout with a 육성 설정 box (코칭스태프 세트, 타격/투수코치) that every 합계 adds.

  make_books.py new  <alloc.json> <template.xlsx> <out dir>      recommended books for every deck in alloc.json
  make_books.py add  <in.xlsx> <out.xlsx> [set hcstat hcbonus pcstat pcbonus]   the box only, the user's numbers kept
"""
import os, copy, csv, json, sys
import openpyxl
from openpyxl.comments import Comment
from openpyxl.formatting.rule import FormulaRule
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.worksheet.datavalidation import DataValidation

BAT = ['컨택', '파워', '스피드', '선구', '수비', 'H정신']
PIT = ['체력', '구속', '컨트롤', '테크닉', '구위', 'P정신']
GRADE_EN = {'노말': 'NORMAL', '스페셜': 'SPECIAL', '레어': 'RARE', '엘리트': 'ELITE'}
GRADE_RGB = {'노말': 'FF2896EC', '스페셜': 'FFFAC61C', '레어': 'FFEC2E38', '엘리트': 'FFAC58F6'}
HEAD_FONT = {'스페셜': 'FF3D3000'}
# The template tints each block's 기본~재계약 rows (light) and 합계 row (mid) in its own player's grade colour; repaint them in
# the new card's grade, or a 레어 block keeps the template's 스페셜 yellow (user 11-07: "레어 박해민이 왜 노란색으로 칠해져있니").
GRADE_LIGHT = {'노말': 'FFEEF6FE', '스페셜': 'FFFFF9E0', '레어': 'FFFDEEEF', '엘리트': 'FFF6EEFE'}
GRADE_MID = {'노말': 'FFCFE6FC', '스페셜': 'FFFFEDA6', '레어': 'FFFAC9CC', '엘리트': 'FFE3CBFC'}
# Output names and the date in the notes; YUKSUNG_NOTE is appended to the B104 note (e.g. how the staff was chosen).
TAG, DATE, NOTE = os.environ.get('YUKSUNG_TAG', '1105'), os.environ.get('YUKSUNG_DATE', '11/5'), os.environ.get('YUKSUNG_NOTE', '')
SLOT_ROLE = {'SP': '12', 'RP': '13', 'SU': '14', 'CP': '15'}
GRADE = {'1': '노말', '2': '스페셜', '3': '레어', '4': '엘리트'}
TEAM = {'롯데': 7, '삼성': 8, 'LG': 6, '쌍방울': 11, '현대': 4}
DEF = ['def_C', 'def_1B', 'def_2B', 'def_3B', 'def_SS', 'def_LF', 'def_CF', 'def_RF']
FIRST, STRIDE, BLOCKS = 5, 9, 11
CHANGE_NOTE = ('기본은 포지션 변경 전 카드값입니다. 중계→선발은 성공하면 체력 +20, 구속 −2, 나머지 −1, 대성공이면 체력 +20, '
               '구속 −1, 나머지는 그대로입니다. 선발→중계·셋업은 체력이 줄어듭니다. 경기에서는 바뀐 값으로 뜁니다.')


def load_cards():
    cards = {}
    base = 'C:/Ma9MProject/server/data/tables/'
    for fn, kind in (('P_BatterTable.txt', 'B'), ('P_PItcherTable.txt', 'P')):
        for r in csv.DictReader(open(base + fn, encoding='utf-8'), delimiter='\t'):
            cards[(kind, r['year'], r['teamcode'], r['name'])] = r
    return cards


def card_base(r, kind):
    if kind == 'B':
        # A pure DH card (mainposition 10, e.g. '11 홍성흔) has no main fielding column; the engine uses its best one (20).
        pos = int(r['mainposition'])
        d = int(r[DEF[pos - 1]]) if 1 <= pos <= len(DEF) else max(int(r[k]) for k in DEF)
        return [int(r['con']), int(r['pow']), int(r['spd']), int(r['eye']), d, int(r['mental'])]
    return [int(r[k]) for k in ('health', 'spd', 'cont', 'technic', 'breakball', 'mental')]


def add_box(ws, setb=3, hcstat='없음', hcb=2, pcstat='없음', pcb=2):
    """육성 설정 box at R1:S6, validations, 합계 formulas, the 140 warning."""
    label, value = ws['N2'], ws['O2']
    ws['R1'] = '육성 설정 (모든 선수에게 반영)'
    ws['R1'].font = Font(bold=True, color='FF374151')
    rows = [('코칭스태프 세트', setb, '"0,1,2,3"',
             '코칭스태프 5명이 모두 같은 구단일 때 가장 낮은 등급의 보너스입니다: 스페셜 1, 레어 2, 엘리트 3. 그 구단 선수의 모든 능력치에 더해집니다. 세트가 아니면 0.'),
            ('타격코치 능력치', hcstat, '"없음,컨택,파워,스피드,선구,H정신"',
             '타격코치 카드(레어·엘리트)에 적힌 능력치를 고르세요. 같은 구단 타자의 그 능력치에 오른쪽 아래 보너스가 더해집니다.'),
            ('타격코치 보너스', hcb, '"0,1,2"', '타격코치 카드 등급: 레어 1, 엘리트 2 (노말·스페셜은 0).'),
            ('투수코치 능력치', pcstat, '"없음,구속,컨트롤,테크닉,구위,P정신"',
             '투수코치 카드(레어·엘리트)에 적힌 능력치를 고르세요. 같은 구단 투수의 그 능력치에 아래 보너스가 더해집니다.'),
            ('투수코치 보너스', pcb, '"0,1,2"', '투수코치 카드 등급: 레어 1, 엘리트 2 (노말·스페셜은 0).')]
    for i, (name, v, lst, note) in enumerate(rows):
        r = 2 + i
        a, b = ws.cell(r, 18), ws.cell(r, 19)
        a.value, b.value = name, v
        for src, dst in ((label, a), (value, b)):
            dst.font, dst.fill, dst.border, dst.alignment = copy.copy(src.font), copy.copy(src.fill), copy.copy(src.border), copy.copy(src.alignment)
        b.comment = Comment(note, 'Claude')
        dv = DataValidation(type='list', formula1=lst, allow_blank=True)
        ws.add_data_validation(dv)
        dv.add(b)
    ws.column_dimensions['Q'].width = 3
    ws.column_dimensions['R'].width = 17
    ws.column_dimensions['S'].width = 9
    sums = []
    for i in range(BLOCKS):
        r = FIRST + i * STRIDE
        for c0, stat, bonus in ((3, '$S$3', '$S$4'), (11, '$S$5', '$S$6')):
            if ws.cell(r + 7, c0).value is None and ws.cell(r + 6, c0).value is None:
                continue
            for j in range(6):
                col = openpyxl.utils.get_column_letter(c0 + j)
                ws[f'{col}{r + 6}'] = f'=IF({col}{r + 2}="","",SUM({col}{r + 2}:{col}{r + 5})+$O$2+$S$2+IF({stat}={col}${r + 1},{bonus},0))'
            sums.append(f'{openpyxl.utils.get_column_letter(c0)}{r + 6}:{openpyxl.utils.get_column_letter(c0 + 5)}{r + 6}')
    for rng in sums:
        tl = rng.split(':')[0]
        ws.conditional_formatting.add(rng, FormulaRule(formula=[f'AND(ISNUMBER({tl}),{tl}>140)'], font=Font(bold=True, color='FFFFFFFF'),
                                                       fill=PatternFill('solid', fgColor='FFDC2626')))
    ws['B3'] = ("흰색·연한 색 칸에 입력: 선수 이름(머리 줄 첫 칸) · 기본 · 특강 · 트레이닝 · 재계약    →  합계 = 기본 + 특강 + 트레이닝 + 재계약 "
                "+ 팀컬러 + 코칭스태프 (자동 계산, 기본을 비우면 빈칸, 140을 넘으면 빨간 칸)\n특강 칸(분홍 줄 왼쪽 '특강 …')에서 코치 이름을 고르면(▾) "
                "능력치 6칸이 자동으로 들어갑니다. 이름만 고르면 주 +5, 결과가 +4였으면 '이름 +4'를 고르세요. 오른쪽 '육성 설정'(R·S열) 값은 모든 선수에게 반영됩니다.")
    ws.row_dimensions[3].height = 46
    ws['B103'] = '양식이 더 필요하면 블록(태그 줄부터 경기 반영 줄까지 8줄)을 복사해 아래에 붙이면 됩니다. 합계 식은 팀컬러 칸($O$2)과 육성 설정 칸($S$2~$S$6)을 그대로 씁니다.'


def fill_side(ws, r, c0, kind, p, base, grade, out_of_role=False):
    names = BAT if kind == 'B' else PIT
    tag, head = ws.cell(r, c0), ws.cell(r, c0 + 1)
    tag.value, head.value = f"{grade} {'타자' if kind == 'B' else '투수'}", GRADE_EN[grade]
    for cell in (tag, head):
        cell.font = Font(name=cell.font.name, size=cell.font.size, bold=cell.font.b, color=GRADE_RGB[grade])
    slot = ws.cell(r, c0 + 6)
    slot.value = p['slot'] + (f" ({p['change']})" if p.get('change') else ' (역할 밖)' if out_of_role else '')
    slot.font = Font(size=9, color='FF6B7280')
    slot.alignment = Alignment(horizontal='right')
    for rr in range(r + 2, r + 7):
        for cc in range(c0, c0 + 7):
            cell = ws.cell(rr, cc)
            rgb = cell.fill.fgColor.rgb if cell.fill.fill_type == 'solid' else None
            if rgb in GRADE_LIGHT.values():
                cell.fill = PatternFill('solid', fgColor=GRADE_LIGHT[grade])
            elif rgb in GRADE_MID.values():
                cell.fill = PatternFill('solid', fgColor=GRADE_MID[grade])
    ws.cell(r + 1, c0).value = p['name']
    ws.cell(r + 1, c0).comment = Comment(CHANGE_NOTE, 'Claude') if p.get('change') else None
    for j in range(6):
        h = ws.cell(r + 1, c0 + 1 + j)
        h.value = names[j]
        h.fill = PatternFill('solid', fgColor=GRADE_RGB[grade])
        h.font = Font(name=h.font.name, size=h.font.size, bold=h.font.b, color=HEAD_FONT.get(grade, 'FFFFFFFF'))
        ws.cell(r + 2, c0 + 1 + j).value = base[j]
        ws.cell(r + 4, c0 + 1 + j).value = p['training'][j] or None
        ws.cell(r + 5, c0 + 1 + j).value = p['renewal'][j] or None
    ws.cell(r + 3, c0).value = p['coach'] or ''


def new_books(alloc_path, template, out_dir):
    alloc = json.load(open(alloc_path, encoding='utf-8'))
    cards = load_cards()
    made = []
    for label, d in alloc.items():
        year, club = label.split(' ', 1)
        wb = openpyxl.load_workbook(template)
        ws = wb['육성 계산기']
        for row in ws.iter_rows():
            for cell in row:
                if cell.comment and cell.coordinate != 'O2':
                    cell.comment = None
        ws['B2'] = f"마구매니저 팀 육성 계산기 · '{year[2:]} {club}"
        bats = [p for p in d['players'] if p['kind'] == 'B']
        pits = [p for p in d['players'] if p['kind'] == 'P']
        for i in range(BLOCKS):
            r = FIRST + i * STRIDE
            for kind, lst, c0 in (('B', bats, 2), ('P', pits, 10)):
                if i >= len(lst):
                    continue
                p = lst[i]
                row = cards[(kind, year, str(TEAM[club]), p['name'])]
                # A pitcher on another role's spot without a 포지션 변경 plays out of role (−15%), e.g. '01 현대 김수경 in RP4.
                out = kind == 'P' and not p.get('change') and row['mainposition'] != SLOT_ROLE[p['slot'][:2]]
                fill_side(ws, r, c0, kind, p, card_base(row, kind), GRADE[row['cardgrade']], out)
        hc, pc = d['hc'][0][1], d['pc'][0][1]
        add_box(ws, 3, hc, 2, pc, 2)
        ws['B104'] = (f"기본 줄은 11/5 라이브 카드값입니다. 특강·트레이닝·재계약 줄은 {DATE} 재계산 추천입니다(팀컬러 S 21 + 코칭스태프 엘리트 세트 +3 "
                      f"+ 타격코치 {hc} +2 · 투수코치 {pc} +2, 트레이닝 = IV북 +5에 코치 +1 = 칸당 6 × 3칸, 재계약 = +3 × 6번). "
                      "회색 숫자는 추천 배분이니 따를지 정해서 고쳐 쓰면 됩니다. 머리 줄 오른쪽 회색 글씨는 추천 타순·보직입니다." + (' ' + NOTE if NOTE else ''))
        value_sheet(wb, label, d)
        short = f"{year[2:]}{club}"
        path = f'{out_dir}/육성표_{TAG}_{short}.xlsx'
        wb.save(path)
        made.append(path)
    return made


def value_sheet(wb, label, d):
    ws = wb.create_sheet('스탯 가치')
    ws['A1'] = f'{label} · 능력치별 가치 ({DATE}, 새 엔진)'
    ws['A1'].font = Font(bold=True, size=13)
    ws['A2'] = ('각 칸은 팀컬러 S 21 + 코칭스태프 +3에서 그 능력치에 +9를 더했을 때의 승률 변화(%p, 같은 덱끼리 20만 경기)입니다. '
                '이미 높은 능력치는 100·120을 넘으면 육성 효과가 75%·50%로 줄고 140에서 멈추므로, 추천 배분은 이 값을 곡선에 맞춰 나눈 결과입니다.')
    ws['A2'].alignment = Alignment(wrap_text=True, vertical='top')
    ws.merge_cells('A2:N2')
    ws.row_dimensions[2].height = 44
    ws['A4'] = '타격코치 능력치 (엘리트 +2, 덱 전체 추천 배분 기준 승률 합)'
    ws['A5'] = ' > '.join(n for _, n in d['hc'])
    ws['A6'] = '투수코치 능력치'
    ws['A7'] = ' > '.join(n for _, n in d['pc'])
    for c in ('A4', 'A6'):
        ws[c].font = Font(bold=True)
    r = 9
    for kind, names in (('B', BAT), ('P', PIT)):
        head = ['자리', '이름', '볼'] + names + ['추천 특강', '육성 후 표시값 (140 상한)']
        for j, h in enumerate(head):
            c = ws.cell(r, 1 + j, h)
            c.font = Font(bold=True, color='FFFFFFFF')
            c.fill = PatternFill('solid', fgColor='FF374151')
        r += 1
        for p in d['players']:
            if p['kind'] != kind:
                continue
            ws.cell(r, 1, p['slot'])
            ws.cell(r, 2, p['name'])
            ws.cell(r, 3, p['ball'])
            g = [x for x in p['g9'] if x is not None]
            top = max(g) if g else 0
            for j in range(6):
                v = p['g9'][j]
                c = ws.cell(r, 4 + j, v)
                if v is not None:
                    c.number_format = '+0.00;-0.00;0.00'
                    if v == top and v > 0:
                        c.font = Font(bold=True, color='FF1D4ED8')
            ws.cell(r, 10, p['coach'] or '')
            ws.cell(r, 11, ' / '.join(f"{names[j]} {p['shown'][j]}" for j in range(6) if p['shown'][j] is not None))
            r += 1
        r += 1
    ws.column_dimensions['A'].width = 9
    ws.column_dimensions['B'].width = 11
    ws.column_dimensions['C'].width = 5
    for col in 'DEFGHI':
        ws.column_dimensions[col].width = 8
    ws.column_dimensions['J'].width = 10
    ws.column_dimensions['K'].width = 70


if __name__ == '__main__':
    if sys.argv[1] == 'new':
        for p in new_books(sys.argv[2], sys.argv[3], sys.argv[4]):
            print(p)
    else:
        wb = openpyxl.load_workbook(sys.argv[2])
        args = sys.argv[4:]
        add_box(wb['육성 계산기'], int(args[0]) if args else 3, args[1] if len(args) > 1 else '없음', int(args[2]) if len(args) > 2 else 2,
                args[3] if len(args) > 3 else '없음', int(args[4]) if len(args) > 4 else 2)
        wb.save(sys.argv[3])
        print(sys.argv[3])
