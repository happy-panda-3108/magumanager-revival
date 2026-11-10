"""카드별 최종 능력치 엑셀 (user, 2026-10-27: "카드별 스탯 최종 정보가 보고싶은 거야").

Reads the card tables the game uses now (server/data/tables P_BatterTable.txt / P_PItcherTable.txt, the same files the
client gets in its table bundle), the 포지션 변경 rules (ma9m_pitcher_roles.txt) and, read-only, which of the user's five
teams own each card. Writes 타자 / 투수 / 설명 sheets. Usage: make_card_book.py <server dir> <out.xlsx>
"""
import os
import sqlite3
import sys
from datetime import datetime

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

SRV, OUT = sys.argv[1], sys.argv[2]
TABLES = os.path.join(SRV, 'data', 'tables')
FONT = '맑은 고딕'


def team_name(code, year):
    """Server TeamColors.TeamName: club names by season."""
    names = {0: '두산', 1: '해태', 2: '한화', 4: '현대', 5: 'KIA', 6: 'LG', 7: '롯데', 8: '삼성', 11: '쌍방울', 13: 'NC', 19: 'kt'}
    if code == 3:
        return '우리' if year <= 2008 else '히어로즈' if year == 2009 else '넥센' if year <= 2018 else '키움'
    if code == 9:
        return 'SK' if year <= 2020 else 'SSG'
    return names.get(code, f'팀{code}')


POS = {1: '포수', 2: '1루수', 3: '2루수', 4: '3루수', 5: '유격수', 6: '좌익수', 7: '중견수', 8: '우익수', 10: '지명타자',
       12: '선발', 13: '중계', 14: '셋업', 15: '마무리'}
ROLE = {12: '선발', 13: '중계', 14: '셋업', 15: '마무리'}
GRADE = {1: '노말', 2: '스페셜', 3: '레어', 4: '엘리트'}
# Card face colours (make_card_images.py; the same as the 육성 계산기): header colour and text colour.
GRADE_FILL = {'노말': ('2896EC', 'FFFFFF'), '스페셜': ('FAC61C', '3D3000'), '레어': ('EC2E38', 'FFFFFF'), '엘리트': ('AC58F6', 'FFFFFF')}
ORDER = {0: '상위(1~2번)', 1: '클린업(3~5번)', 2: '하위(6~9번)', 3: '어디든'}
BAT_HAND = {1: '우타', 2: '좌타', 3: '양타'}
THROW_HAND = {1: '우투', 2: '좌투'}
THROW_TYPE = {1: '오버', 2: '사이드', 3: '언더'}
PITCHES = [('FAST', '포심'), ('CURT', '커터'), ('KNUKL', '너클'), ('HSLID', '고속슬라'), ('JSLID', 'V슬라이더'), ('SLID', '슬라이더'),
           ('CURV', '커브'), ('SCURV', '슬로우커브'), ('FORK', '포크'), ('SFF', '스플리터'), ('SINK', '싱커'), ('CHAG', '체인지업'),
           ('CCHAG', '서클체인지업'), ('TSEAM', '투심')]
DEF_COLS = ['def_C', 'def_1B', 'def_2B', 'def_3B', 'def_SS', 'def_LF', 'def_CF', 'def_RF']
DEF_OF_POS = {1: 'def_C', 2: 'def_1B', 3: 'def_2B', 4: 'def_3B', 5: 'def_SS', 6: 'def_LF', 7: 'def_CF', 8: 'def_RF'}
TEAMS = {7: 'SKY마토', 9: 'SKY준우', 10: 'SKY승환', 11: 'SKY해민', 14: '행복한현대'}


def rows_of(name):
    with open(os.path.join(TABLES, name), encoding='utf-8') as f:
        head = next(f).rstrip('\r\n').split('\t')
        for line in f:
            c = line.rstrip('\r\n').split('\t')
            if len(c) >= 30:
                yield dict(zip(head, c))


def num(v):
    try:
        f = float(v)
        return int(f) if f == int(f) else f
    except (TypeError, ValueError):
        return v


roles = {}
with open(os.path.join(TABLES, 'ma9m_pitcher_roles.txt'), encoding='utf-8') as f:
    next(f)
    for line in f:
        c = line.rstrip('\r\n').split('\t')
        roles[int(c[0])] = [int(x) for x in c[2].split(',')]

owners = {}
db = sqlite3.connect('file:' + os.path.join(SRV, 'data', 'ma9m.db') + '?mode=ro', uri=True)
for user, team in TEAMS.items():
    for (idx,) in db.execute('SELECT DISTINCT card_idx FROM team_card WHERE user_code = ?', (user,)):
        owners.setdefault(idx, []).append(team)
db.close()

wb = Workbook()
thin = Side(style='thin', color='C4CCD8')
head_fill = PatternFill('solid', start_color='1F3B6D', end_color='1F3B6D')


def sheet(ws, header, data, widths, grade_col):
    ws.append(header)
    for r in data:
        ws.append(r)
    for c in range(1, len(header) + 1):
        cell = ws.cell(row=1, column=c)
        cell.font = Font(name=FONT, size=10, bold=True, color='FFFFFF')
        cell.fill = head_fill
        cell.alignment = Alignment(horizontal='center', vertical='center', wrap_text=True)
        ws.column_dimensions[get_column_letter(c)].width = widths.get(header[c - 1], 7)
    ws.row_dimensions[1].height = 30
    body = Font(name=FONT, size=10)
    fills = {g: (PatternFill('solid', start_color=bg, end_color=bg), Font(name=FONT, size=10, bold=True, color=fg)) for g, (bg, fg) in GRADE_FILL.items()}
    for row in ws.iter_rows(min_row=2, max_row=ws.max_row):
        for cell in row:
            cell.font = body
            cell.border = Border(bottom=thin)
        g = row[grade_col].value
        if g in fills:
            row[grade_col].fill, row[grade_col].font = fills[g]
            row[grade_col].alignment = Alignment(horizontal='center')
    ws.freeze_panes = 'D2'
    ws.auto_filter.ref = f'A1:{get_column_letter(len(header))}{ws.max_row}'


# 타자: the six stats in the 육성 계산기's order (컨택 파워 스피드 선구 수비 H정신) so a row's cells paste straight in
# (user, 2026-10-27).
bat_header = ['연도', '팀', '이름', '주포지션', '등급', '볼', '합계', '컨택', '파워', '스피드', '선구', '수비(주)', 'H정신',
              '포수', '1루', '2루', '3루', '유격', '좌익', '중견', '우익', '선호타순', '타', '투', '나이', '용병',
              '경기', '타율', '타수', '안타', '홈런', '타점', '득점', '삼진', '도루', '보유 팀', '카드번호']
bat = []
for r in rows_of('P_BatterTable.txt'):
    year, code, pos = int(r['year']), int(r['teamcode']), int(r['mainposition'])
    defs = [int(r[c]) for c in DEF_COLS]
    main_def = int(r[DEF_OF_POS[pos]]) if pos in DEF_OF_POS else max(defs)
    stats = [int(r[k]) for k in ('con', 'pow', 'spd', 'eye')]
    mental = int(r['mental'])
    bat.append([year, team_name(code, year), r['name'], POS.get(pos, pos), GRADE.get(int(r['cardgrade']), r['cardgrade']), int(r['ball']),
                sum(stats) + main_def + mental, *stats, main_def, mental, *defs, ORDER.get(int(r['lineup']), r['lineup']),
                BAT_HAND.get(int(r['hithand']), r['hithand']), THROW_HAND.get(int(r['throwhand']), r['throwhand']), int(r['age']),
                '용병' if r['foreigner'] == '1' else '', num(r['G']), num(r['BA']), num(r['AB']), num(r['H']), num(r['HR']), num(r['RBI']),
                num(r['R']), num(r['K']), num(r['SB']), ', '.join(owners.get(int(r['idx']), [])), int(r['idx'])])
bat.sort(key=lambda x: (x[0], x[1], -x[6], x[2]))
ws = wb.active
ws.title = '타자'
sheet(ws, bat_header, bat, {'이름': 10, '주포지션': 8, '선호타순': 12, '보유 팀': 14, '카드번호': 9, '타율': 7}, 4)
for row in ws.iter_rows(min_row=2, min_col=28, max_col=28):
    for cell in row:
        cell.number_format = '0.000'

# 투수
pit_header = ['연도', '팀', '이름', '역할', '등급', '볼', '합계', '체력', '구속', '컨트롤', '테크닉', '구위', 'P정신', '구종 (등급)',
              '투', '투구폼', '나이', '용병', '포지션 변경', '경기', '평균자책', '승', '패', '홀드', '세이브', '이닝', '탈삼진', '피안타',
              '보유 팀', '카드번호']
pit = []
for r in rows_of('P_PItcherTable.txt'):
    year, code, pos, idx = int(r['year']), int(r['teamcode']), int(r['mainposition']), int(r['idx'])
    stats = [int(r[k]) for k in ('health', 'spd', 'cont', 'technic', 'breakball', 'mental')]
    pitches = sorted(((int(r[c]), n) for c, n in PITCHES if r.get(c, '0') not in ('', '0')), reverse=True)
    allowed = roles.get(idx, [])
    change = ', '.join(f'{ROLE.get(pos, pos)}→{ROLE[a]}' for a in allowed if a in ROLE)
    pit.append([year, team_name(code, year), r['name'], ROLE.get(pos, pos), GRADE.get(int(r['cardgrade']), r['cardgrade']), int(r['ball']),
                sum(stats), *stats, ', '.join(f'{n} {v}' for v, n in pitches), THROW_HAND.get(int(r['throwhand']), r['throwhand']),
                THROW_TYPE.get(int(r['throwtype']), r['throwtype']), int(r['age']), '용병' if r['foreigner'] == '1' else '', change,
                num(r['game']), num(r['ERA']), num(r['W']), num(r['L']), num(r['HLD']), num(r['SV']), round(int(r['IPx3']) / 3, 1),
                num(r['K']), num(r['H']), ', '.join(owners.get(idx, [])), idx])
pit.sort(key=lambda x: (x[0], x[1], -x[6], x[2]))
ws = wb.create_sheet('투수')
sheet(ws, pit_header, pit, {'이름': 10, '구종 (등급)': 34, '포지션 변경': 14, '보유 팀': 14, '카드번호': 9, '평균자책': 8}, 4)
for row in ws.iter_rows(min_row=2, min_col=21, max_col=21):
    for cell in row:
        cell.number_format = '0.00'

# 설명
ws = wb.create_sheet('설명')
notes = [
    '마구매니저 카드별 최종 능력치',
    f'게임 서버가 지금 쓰는 카드 표(server/data/tables, {datetime.fromtimestamp(os.path.getmtime(os.path.join(TABLES, "P_BatterTable.txt"))):%Y-%m-%d %H:%M} 기준)에서 뽑았습니다. 앱이 받는 표와 같은 파일입니다.',
    f'타자 {len(bat):,}장, 투수 {len(pit):,}장. 1볼 노멀 중 20경기 미만 선수처럼 게임에서 뺀 카드는 들어 있지 않습니다.',
    '능력치는 카드 기본값입니다. 팀컬러·재계약·트레이닝·특강·포지션 변경은 들어 있지 않습니다(경기에서는 카드값 + 0.34 × 육성).',
    '등급: 1~4볼 노말, 5~7볼 스페셜, 8~9볼 레어, 9~10볼 엘리트(9볼은 레어와 엘리트가 섞여 있음). 색은 카드 색깔입니다.',
    '합계: 타자 = 컨택 + 파워 + 스피드 + 선구 + 수비(주 포지션; 지명타자는 수비값 중 가장 높은 것) + H정신. 투수 = 체력 + 구속 + 컨트롤 + 테크닉 + 구위 + P정신.',
    '능력치 6칸은 팀 육성 계산기와 같은 순서입니다(타자 컨택·파워·스피드·선구·수비·H정신, 투수 체력·구속·컨트롤·테크닉·구위·P정신). 한 줄의 6칸을 복사해 계산기의 "기본" 줄에 그대로 붙이면 됩니다.',
    '포지션별 수비: 20은 그 포지션을 볼 수 없다는 뜻입니다(그 자리에 넣으면 −10%).',
    '선호타순: 상위 1~2번, 클린업 3~5번, 하위 6~9번, 어디든. 타순 그래프(선수 분석)에 쓰입니다.',
    '투수 구종: 그 카드가 던지는 구종과 등급, 높은 것부터. 투구폼은 오버 / 사이드 / 언더.',
    '포지션 변경: 전력강화 > 포지션 변경으로 바꿀 수 있는 역할(40만 MP, 카드당 한 번).',
    '기록: 그 시즌 실제 KBO 기록입니다(이닝은 아웃 수 ÷ 3).',
    '보유 팀: 지금 가진 사용자 팀(SKY마토, SKY준우, SKY승환, SKY해민, 행복한현대). 필터로 "내 카드만" 볼 수 있습니다.',
    '정렬: 연도 → 팀 → 합계 높은 순. 머리 줄의 ▼(필터)로 연도·팀·포지션·등급별로 고를 수 있습니다.',
]
for i, n in enumerate(notes):
    c = ws.cell(row=i + 1, column=1, value=n)
    c.font = Font(name=FONT, size=12 if i == 0 else 10, bold=i == 0)
ws.column_dimensions['A'].width = 140
wb.move_sheet('설명', offset=-2)
wb.active = 1
wb.save(OUT)
print('wrote', OUT, len(bat), len(pit))
