"""엘리트 비교 엑셀: the elites by Statiz WAR picked two ways (user, 2026-11-01: "그냥 7등까지 뽑은 엑셀이랑, 연도별로 타자
4명, 투수 3명 뽑았을 때 엘리트가 어떻게 차이가 나는지 엘리트 카드만 뽑아서"):
  - the top 7 WAR of a season, batters and pitchers together (card_value.py's rule, Statiz WAR instead of ours);
  - the top 4 batters and the top 3 pitchers of a season.
Player-seasons come from the new cards (sim/out/cards_v4 reviews). WAR and the season lines come from statiz/표: 타격 기본
(batters, joined by year + name + club, else by club + plate appearances when the names differ, e.g. 래리.S = 서튼)
and 투구 기본 (pitchers, by the reviews' Statiz number). The live server's elites and the user's owned cards are read only.
Card abilities and balls are left out: they change with the stat rework.
Usage: python make_elite_book.py <out.xlsx>
"""
import collections
import csv
import os
import sqlite3
import sys

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

ROOT = r'C:\Ma9MProject'
OUT = sys.argv[1]
YEARS = range(1999, 2026)
FONT = '맑은 고딕'
INITIAL = {'류': '유', '유': '류', '이': '리', '리': '이', '임': '림', '림': '임', '나': '라', '라': '나', '노': '로', '로': '노'}
USERS = {7: 'SKY마토', 9: 'SKY준우', 10: 'SKY승환', 11: 'SKY해민', 14: '행복한현대'}
ROLE = {'SP': '선발', 'RP': '중계', 'SU': '셋업', 'CP': '마무리'}


def f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def tsv(path):
    with open(path, encoding='utf-8', newline='') as fh:
        return list(csv.DictReader(fh, delimiter='\t'))


def table(path):
    with open(path, encoding='utf-8') as fh:
        head = next(fh).rstrip('\r\n').split('\t')
        return [dict(zip(head, c)) for c in (l.rstrip('\r\n').split('\t') for l in fh) if len(c) >= 30]


bat_by_name, bat_by_club = collections.defaultdict(list), collections.defaultdict(list)
for r in tsv(os.path.join(ROOT, 'statiz', '표', '타격_합본_전체.tsv')):
    bat_by_name[(int(r['연도']), r['Name'])].append(r)
    bat_by_club[(int(r['연도']), r['team'])].append(r)
pit = {(int(r['연도']), r['p_no']): r for r in tsv(os.path.join(ROOT, 'statiz', '표', '투구_합본_전체.tsv'))}


def batter_line(year, name, teams, pa):
    """The batter's Statiz season: by name (or the other surname spelling) and club, the closest plate appearances
    when two share the name (LG 이병규 ×2); else the one line of his club with the same plate appearances."""
    for n in (name, INITIAL.get(name[:1], name[:1]) + name[1:]):
        rows = [r for r in bat_by_name.get((year, n), []) if r['team'] in teams] or bat_by_name.get((year, n), [])
        if len(rows) == 1:
            return rows[0]
        if rows:
            best = min(rows, key=lambda r: abs(int(r['PA'] or 0) - pa))
            if abs(int(best['PA'] or 0) - pa) <= 3:
                return best
    same = [r for t in teams for r in bat_by_club.get((year, t), []) if abs(int(r['PA'] or 0) - pa) <= 1]
    return same[0] if len(same) == 1 else None


seasons, unmatched = {}, collections.Counter()
with open(os.path.join(ROOT, 'sim', 'out', 'cards_v4', 'review_batters.csv'), encoding='utf-8-sig', newline='') as fh:
    for c in csv.DictReader(fh):
        y = int(c['year'])
        if c['card'] != 'made' or y not in YEARS:
            continue
        teams = set(c['teams'].replace('/', ' ').replace(',', ' ').split())
        s = batter_line(y, c['name'], teams, int(c['pa']))
        if s is None:
            unmatched['타자'] += 1
            continue
        line = f"타율 {s['AVG']} 홈런 {s['HR']} 타점 {s['RBI']} 도루 {s['SB']} OPS {s['OPS']}" + (f" wRC+ {s['wRC+']}" if s.get('wRC+') else '')
        seasons[('타자', y, c['sidx'])] = {'name': c['name'], 'statiz': s['Name'], 'team': c['teams'], 'pos': c['main_pos'],
                                          'war': f(s['WAR']), 'line': line}
with open(os.path.join(ROOT, 'sim', 'out', 'cards_v4', 'review_pitchers.csv'), encoding='utf-8-sig', newline='') as fh:
    for c in csv.DictReader(fh):
        y = int(c['year'])
        if c.get('card') != 'made' or y not in YEARS:
            continue
        s = pit.get((y, c['p_no']))
        if s is None or f(s['WAR']) is None:
            unmatched['투수'] += 1
            continue
        line = f"{s['W']}승 {s['L']}패" + (f" {s['S']}세이브" if s['S'] not in ('', '0') else '') + \
               (f" {s['HD']}홀드" if s['HD'] not in ('', '0') else '') + f" {s['IP']}이닝 평균자책 {s['ERA']} 탈삼진 {s['SO']}"
        seasons[('투수', y, c['sidx'])] = {'name': c['name'], 'statiz': s['Name'], 'team': c['teams'],
                                          'pos': ROLE.get(c['role'], c['role']), 'war': f(s['WAR']), 'line': line}

live = {}
for kind, name in (('타자', 'P_BatterTable.txt'), ('투수', 'P_PItcherTable.txt')):
    for r in table(os.path.join(ROOT, 'server', 'data', 'tables', name)):
        live[r['idx']] = ((kind, int(r['year']), r['sidx']), r['cardgrade'] == '4')
live_elite = {k for k, e in live.values() if e}
owners = collections.defaultdict(set)
db = sqlite3.connect('file:' + os.path.join(ROOT, 'server', 'data', 'ma9m.db') + '?mode=ro', uri=True)
for user, uname in USERS.items():
    for (idx,) in db.execute('SELECT DISTINCT card_idx FROM team_card WHERE user_code = ?', (user,)):
        if str(idx) in live:
            owners[live[str(idx)][0]].add(uname)
db.close()

# Rank, the two picks, per season.
rank, top7, fixed = {}, set(), set()
summary = []
for y in YEARS:
    pool = sorted(((v['war'], k) for k, v in seasons.items() if k[1] == y and v['war'] is not None), reverse=True)
    for i, (_, k) in enumerate(pool, 1):
        rank[k] = i
    a = {k for _, k in pool[:7]}
    b = {k for _, k in [p for p in pool if p[1][0] == '타자'][:4]} | {k for _, k in [p for p in pool if p[1][0] == '투수'][:3]}
    top7 |= a
    fixed |= b
    nb = sum(1 for k in a if k[0] == '타자')
    out_, in_ = sorted(a - b, key=lambda k: rank[k]), sorted(b - a, key=lambda k: rank[k])
    summary.append([y, nb, 7 - nb, pool[6][0], len(in_),
                    ', '.join(f"{seasons[k]['name']}({k[0][0]} {seasons[k]['war']:.2f})" for k in out_),
                    ', '.join(f"{seasons[k]['name']}({k[0][0]} {seasons[k]['war']:.2f}, {rank[k]}위)" for k in in_)])

wb = Workbook()
thin = Side(style='thin', color='C4CCD8')
head_fill = PatternFill('solid', start_color='1F3B6D', end_color='1F3B6D')
diff_fill = PatternFill('solid', start_color='FFF2B3', end_color='FFF2B3')
new_fill = PatternFill('solid', start_color='EDE3FB', end_color='EDE3FB')
kind_fill = {'타자': PatternFill('solid', start_color='E8F1FC', end_color='E8F1FC'), '투수': PatternFill('solid', start_color='FDF0E1', end_color='FDF0E1')}


def sheet(ws, header, rows, widths, marks=()):
    """rows; marks: (row, col) 0-based cells to shade yellow (the pick differs between the two ways)."""
    ws.append(header)
    for r in rows:
        ws.append(r)
    for c in range(1, len(header) + 1):
        cell = ws.cell(row=1, column=c)
        cell.font = Font(name=FONT, size=10, bold=True, color='FFFFFF')
        cell.fill = head_fill
        cell.alignment = Alignment(horizontal='center', vertical='center', wrap_text=True)
        ws.column_dimensions[get_column_letter(c)].width = widths.get(header[c - 1], 8)
    ws.row_dimensions[1].height = 30
    body = Font(name=FONT, size=10)
    for ri, row in enumerate(ws.iter_rows(min_row=2, max_row=ws.max_row)):
        for ci, cell in enumerate(row):
            cell.font = body
            cell.border = Border(bottom=thin)
            if (ri, ci) in marks:
                cell.fill = diff_fill
    ws.freeze_panes = 'D2'
    ws.auto_filter.ref = f'A1:{get_column_letter(len(header))}{ws.max_row}'


HEAD = ['연도', '순위', '구분', '이름', '팀', '포지션', '스탯티즈 WAR', '그 시즌 기록', '다른 방식에도', '지금 서버 엘리트', '보유 팀']
WIDTH = {'이름': 11, '팀': 10, '포지션': 8, '스탯티즈 WAR': 9, '그 시즌 기록': 52, '다른 방식에도': 9, '지금 서버 엘리트': 9, '보유 팀': 30}


def rows_of(picked, other):
    keys = sorted(picked, key=lambda k: (k[1], rank[k]))
    rows, marks = [], set()
    for i, k in enumerate(keys):
        v = seasons[k]
        name = v['name'] if v['statiz'] == v['name'] else f"{v['name']} ({v['statiz']})"
        rows.append([k[1], rank[k], k[0], name, v['team'], v['pos'], round(v['war'], 2), v['line'],
                     'O' if k in other else '—', 'O' if k in live_elite else '—', ', '.join(sorted(owners.get(k, [])))])
        if k not in other:
            marks.add((i, 8))
        if k not in live_elite:
            marks.add((i, 9))
    return rows, marks


ws = wb.active
ws.title = 'WAR 7위까지'
r, m = rows_of(top7, fixed)
sheet(ws, HEAD, r, WIDTH, m)
ws = wb.create_sheet('타자4·투수3')
r, m = rows_of(fixed, top7)
sheet(ws, HEAD, r, WIDTH, m)
ws = wb.create_sheet('차이')
diff_rows = []
for y in YEARS:
    for label, keys in (('7위까지에만', top7 - fixed), ('타자4·투수3에만', fixed - top7)):
        for k in sorted((k for k in keys if k[1] == y), key=lambda k: rank[k]):
            v = seasons[k]
            diff_rows.append([y, label, k[0], v['name'], v['team'], v['pos'], round(v['war'], 2), rank[k], v['line']])
sheet(ws, ['연도', '들어가는 방식', '구분', '이름', '팀', '포지션', '스탯티즈 WAR', '시즌 순위', '그 시즌 기록'], diff_rows,
      {'들어가는 방식': 14, '이름': 11, '팀': 10, '그 시즌 기록': 52, '스탯티즈 WAR': 9})
ws = wb.create_sheet('시즌별 요약')
sheet(ws, ['연도', '7위까지: 타자', '7위까지: 투수', '7위 WAR', '바뀌는 사람', '4·3에서 빠지는 선수 (WAR)', '4·3에서 들어오는 선수 (WAR, 시즌 순위)'],
      summary, {'4·3에서 빠지는 선수 (WAR)': 40, '4·3에서 들어오는 선수 (WAR, 시즌 순위)': 48, '7위까지: 타자': 9, '7위까지: 투수': 9})

# ---------- 설명 ----------
ws = wb.create_sheet('설명', 0)
kinds = lambda s: collections.Counter(k[0] for k in s)
ka, kb = kinds(top7), kinds(fixed)
moved = [k for k in top7 - fixed]
gained = [k for k in fixed - top7]
war_out = sum(seasons[k]['war'] for k in moved) / max(1, len(moved))
war_in = sum(seasons[k]['war'] for k in gained) / max(1, len(gained))
per_season_b = collections.Counter(s[1] for s in summary)
worst = max(summary, key=lambda s: abs(s[1] - 4))
notes = [
    '마구매니저 엘리트 비교 (스탯티즈 WAR, 1999~2025)',
    '엘리트는 시즌마다 7명입니다. 두 방식 모두 스탯티즈 WAR(타자: 타격 기본 탭, 투수: 투구 기본 탭)로 뽑았습니다.',
    f'방식 1 "WAR 7위까지": 타자·투수를 함께 WAR 순으로 7명. 27시즌 189명 중 타자 {ka["타자"]}명, 투수 {ka["투수"]}명.',
    f'방식 2 "타자4·투수3": 시즌마다 타자 WAR 상위 4명과 투수 WAR 상위 3명. 타자 {kb["타자"]}명, 투수 {kb["투수"]}명.',
    f'두 방식이 다른 사람: {len(moved)}명(방식 1에만 있는 선수) ↔ {len(gained)}명(방식 2에만 있는 선수). 같은 엘리트는 {len(top7 & fixed)}명입니다.',
    f'방식 2로 빠지는 선수의 WAR 평균 {war_out:.2f}, 대신 들어오는 선수의 WAR 평균 {war_in:.2f}입니다. 인원을 고정하면 WAR이 더 높은 선수가 빠지고 낮은 선수가 들어오는 해가 생깁니다.',
    '방식 1에서 시즌별 타자 수: ' + ', '.join(f'타자 {n}명 {per_season_b[n]}시즌' for n in sorted(per_season_b)) +
    f'. 가장 치우친 해는 {worst[0]}년(타자 {worst[1]}명·투수 {worst[2]}명)입니다.',
    '인원 고정의 의미: 해마다 투수 엘리트가 꼭 3명(타자 4명) 있어서, 연도덱을 짤 때 엘리트 투수·타자를 일정하게 구할 수 있습니다. '
    '대신 엘리트가 "그 해 가장 가치 있던 7명"이라는 뜻은 약해집니다.',
    f'지금 서버 엘리트와 다른 사람: 방식 1 {len(top7 - live_elite)}명, 방식 2 {len(fixed - live_elite)}명. '
    '"지금 서버 엘리트" 열이 — 인 줄이 새로 엘리트가 되는 선수입니다(노란 칸).',
    '"다른 방식에도" 열이 — 인 줄(노란 칸)은 그 방식에서만 엘리트입니다. "차이" 시트에 시즌별로 모아 두었습니다.',
    '이름 옆 괄호는 스탯티즈 표기입니다(예: 래리.S (서튼)). 이름이 다른 선수는 같은 해·같은 팀·같은 타석 수로 찾았습니다.',
    '"보유 팀": 지금 가진 사용자 팀(SKY마토, SKY준우, SKY승환, SKY해민, 행복한현대). 스탯 개편 때 기존 카드에도 바로 반영됩니다(사용자 결정).',
    '카드 능력치·볼은 스탯 개편(진행 중) 뒤에 바뀌므로 넣지 않았습니다. 엘리트는 실제 WAR로만 정해지므로 개편과 상관없이 이 명단 그대로입니다.',
    f'스탯티즈 WAR을 못 찾아 후보에서 빠진 카드 선수-시즌: 타자 {unmatched["타자"]}, 투수 {unmatched["투수"]}(대부분 출전이 적은 선수).',
]
for line in notes:
    ws.append([line])
ws.column_dimensions['A'].width = 150
ws['A1'].font = Font(name=FONT, size=14, bold=True)
for row in ws.iter_rows(min_row=2, max_row=ws.max_row):
    row[0].font = Font(name=FONT, size=10)
    row[0].alignment = Alignment(wrap_text=True, vertical='top')
wb.save(OUT)
print(f'wrote {OUT}: top7 {dict(ka)}, fixed {dict(kb)}, differ {len(moved)}, vs live {len(top7 - live_elite)}/{len(fixed - live_elite)}, '
      f'unmatched {dict(unmatched)}, war out {war_out:.2f} in {war_in:.2f}')
