"""투수 카드 개편 전후 (2026-11-05 19:35 live): backup P_PItcherTable vs live, the user's 1군 pitchers, and a workbook."""
import csv, json, sqlite3, sys
import openpyxl
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter

OLD = 'C:/Ma9MProject/server/data/backup/design_20261105_1935_before/server/data/tables/P_PItcherTable.txt'
NEW = 'C:/Ma9MProject/server/data/tables/P_PItcherTable.txt'
DB = 'file:C:/Ma9MProject/server/data/ma9m.db?mode=ro'
STATS = [('health', '체력'), ('spd', '구속'), ('cont', '컨트롤'), ('technic', '테크닉'), ('breakball', '구위'), ('mental', 'P정신')]
ROLE = {'12': '선발', '13': '중계', '14': '셋업', '15': '마무리'}
GRADE = {'1': '노말', '2': '스페셜', '3': '레어', '4': '엘리트'}
SLOTS = ['SP1', 'SP2', 'SP3', 'SP4', 'SP5', 'RP1', 'RP2', 'RP3', 'RP4', 'SU', 'CP']


def team_name(code, year):
    code = int(code)
    names = {0: '두산', 1: '해태', 2: '한화', 4: '현대', 5: 'KIA', 6: 'LG', 7: '롯데', 8: '삼성', 11: '쌍방울', 13: 'NC', 19: 'kt',
             10: 'OB', 14: '빙그레', 15: '태평양', 16: 'MBC', 17: '청보', 18: '삼미'}
    if code == 3:
        return '우리' if year <= 2008 else '히어로즈' if year == 2009 else '넥센' if year <= 2018 else '키움'
    if code == 9:
        return 'SK' if year <= 2020 else 'SSG'
    return names.get(code, f'팀{code}')


def load(path):
    return {r['idx']: r for r in csv.DictReader(open(path, encoding='utf-8'), delimiter='\t')}


def main(out_xlsx, out_json):
    old, new = load(OLD), load(NEW)
    rows = []
    for idx, n in new.items():
        o = old.get(idx)
        if o is None:
            continue
        y = int(n['year'])
        r = {'idx': int(idx), 'year': y, 'team': team_name(n['teamcode'], y), 'name': n['name'], 'role': ROLE.get(n['mainposition'], n['mainposition']),
             'role0': ROLE.get(o['mainposition'], o['mainposition']), 'grade': GRADE.get(n['cardgrade']), 'grade0': GRADE.get(o['cardgrade']),
             'ball': int(n['ball']), 'ball0': int(o['ball'])}
        for k, label in STATS:
            r[label] = int(n[k])
            r[label + '0'] = int(o[k])
        r['changed'] = r['ball'] != r['ball0'] or any(r[l] != r[l + '0'] for _, l in STATS)
        rows.append(r)
    byidx = {r['idx']: r for r in rows}

    # The user's teams: 1군 pitchers in slot order.
    c = sqlite3.connect(DB, uri=True)
    teams = {u: name for u, name in c.execute('SELECT user_code, team_name FROM team WHERE user_code IN (7, 9, 10, 11, 14, 16)')}
    mine = []
    for u, lineup in c.execute('SELECT user_code, lineup FROM team_lineup WHERE user_code IN (7, 9, 10, 11, 14, 16) ORDER BY user_code'):
        parts = lineup.split('|')
        pits = [int(x) for x in parts[2].split(',')]
        mine.append({'user': u, 'team': teams.get(u), 'pitchers': [(SLOTS[i], byidx.get(p)) for i, p in enumerate(pits) if p in byidx]})

    # Summary numbers.
    changed = [r for r in rows if r['changed']]
    summary = {
        'pitchers': len(rows), 'changed': len(changed),
        'ball_up': sum(r['ball'] > r['ball0'] for r in rows), 'ball_down': sum(r['ball'] < r['ball0'] for r in rows),
        'grade_changed': sum(r['grade'] != r['grade0'] for r in rows),
        'elite10_before': sum(r['ball0'] == 10 for r in rows), 'elite10_after': sum(r['ball'] == 10 for r in rows),
        'mean_delta': {l: round(sum(r[l] - r[l + '0'] for r in rows) / len(rows), 2) for _, l in STATS},
        'changed_stats': {l: sum(r[l] != r[l + '0'] for r in rows) for _, l in STATS},
    }
    json.dump({'summary': summary, 'mine': [{'team': m['team'], 'pitchers': [(s, r) for s, r in m['pitchers']]} for m in mine]},
              open(out_json, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)

    # Workbook.
    wb = openpyxl.Workbook()
    head_fill = PatternFill('solid', fgColor='FF374151')
    up, down = Font(color='FF15803D', bold=True), Font(color='FFDC2626', bold=True)

    def header(ws, cols, widths):
        for j, (h, w) in enumerate(zip(cols, widths)):
            cell = ws.cell(1, 1 + j, h)
            cell.font = Font(bold=True, color='FFFFFFFF')
            cell.fill = head_fill
            cell.alignment = Alignment(horizontal='center', vertical='center', wrap_text=True)
            ws.column_dimensions[get_column_letter(1 + j)].width = w
        ws.freeze_panes = 'A2'
        ws.row_dimensions[1].height = 30

    ws = wb.active
    ws.title = '요약'
    lines = [
        '투수 카드 개편 전후 비교 (2026-11-05 19:35 라이브 반영)',
        '',
        '사용자 설계(11-05 16:45): 테크닉은 범타(땅볼·내야 뜬공)와 인플레이 피안타 억제, 구위는 삼진과 피장타 억제로 다시 매겼습니다. 구속·컨트롤·체력·P정신은 매기는 법이 그대로이고, 타자 카드는 바뀌지 않았습니다.',
        '',
        f"투수 카드 {summary['pitchers']:,}장 중 능력치나 볼이 바뀐 카드 {summary['changed']:,}장",
        f"볼: 오른 카드 {summary['ball_up']}장, 내린 카드 {summary['ball_down']}장 / 등급이 바뀐 카드 {summary['grade_changed']}장",
        f"10볼: {summary['elite10_before']}장 → {summary['elite10_after']}장",
        '능력치별 평균 변화(전체 투수): ' + ', '.join(f"{k} {v:+.2f}" for k, v in summary['mean_delta'].items()),
        '능력치별 바뀐 카드 수: ' + ', '.join(f"{k} {v:,}장" for k, v in summary['changed_stats'].items()),
        '',
        "시트: '내 1군 투수' — 계정 6개의 1군 투수(SP1~CP) 전후, '바뀐 투수' — 능력치·볼이 바뀐 카드(볼 변화 큰 순), '전체 투수' — 4,726장 전부. 초록 = 오름, 빨강 = 내림. 열마다 필터를 쓸 수 있습니다.",
    ]
    for i, t in enumerate(lines):
        cell = ws.cell(1 + i, 1, t)
        cell.alignment = Alignment(wrap_text=True, vertical='top')
        if i == 0:
            cell.font = Font(bold=True, size=13)
    ws.column_dimensions['A'].width = 120

    cols = ['연도', '팀', '이름', '역할', '등급(전)', '등급', '볼(전)', '볼', '볼 변화'] + [x for _, l in STATS for x in (f'{l}(전)', l, f'{l} 변화')]
    widths = [6, 7, 10, 6, 7, 7, 6, 5, 6] + [7, 6, 6] * 6

    def put(ws, r_i, r):
        vals = [r['year'], r['team'], r['name'], r['role'], r['grade0'], r['grade'], r['ball0'], r['ball'], r['ball'] - r['ball0']]
        for _, l in STATS:
            vals += [r[l + '0'], r[l], r[l] - r[l + '0']]
        for j, v in enumerate(vals):
            cell = ws.cell(r_i, 1 + j, v)
            if j == 8 or (j >= 9 and (j - 9) % 3 == 2):
                cell.number_format = '+0;-0;0'
                if v > 0:
                    cell.font = up
                elif v < 0:
                    cell.font = down
        if r['grade'] != r['grade0']:
            ws.cell(r_i, 6).font = up if ['노말', '스페셜', '레어', '엘리트'].index(r['grade']) > ['노말', '스페셜', '레어', '엘리트'].index(r['grade0']) else down

    ws = wb.create_sheet('내 1군 투수')
    header(ws, ['구단', '자리'] + cols, [10, 5] + widths)
    i = 2
    for m in mine:
        for slot, r in m['pitchers']:
            ws.cell(i, 1, m['team'])
            ws.cell(i, 2, slot)
            # shift: write the row starting at column 3
            vals_ws = ws
            put_offset(vals_ws, i, r, 2, up, down)
            i += 1
        i += 1

    ws = wb.create_sheet('바뀐 투수')
    header(ws, cols, widths)
    for k, r in enumerate(sorted(changed, key=lambda r: (-abs(r['ball'] - r['ball0']), -r['ball'], r['year'], r['team'], r['name']))):
        put(ws, 2 + k, r)
    ws.auto_filter.ref = f"A1:{get_column_letter(len(cols))}{len(changed) + 1}"

    ws = wb.create_sheet('전체 투수')
    header(ws, cols, widths)
    for k, r in enumerate(sorted(rows, key=lambda r: (r['year'], r['team'], -r['ball'], r['name']))):
        put(ws, 2 + k, r)
    ws.auto_filter.ref = f"A1:{get_column_letter(len(cols))}{len(rows) + 1}"
    for w in wb.worksheets:
        for row in w.iter_rows():
            for cell in row:
                if cell.font is None or cell.font.name != 'Arial':
                    f = cell.font
                    cell.font = Font(name='Arial', size=f.size, bold=f.b, color=f.color)
    wb.save(out_xlsx)
    print(json.dumps(summary, ensure_ascii=False))


def put_offset(ws, r_i, r, off, up, down):
    vals = [r['year'], r['team'], r['name'], r['role'], r['grade0'], r['grade'], r['ball0'], r['ball'], r['ball'] - r['ball0']]
    for _, l in STATS:
        vals += [r[l + '0'], r[l], r[l] - r[l + '0']]
    for j, v in enumerate(vals):
        cell = ws.cell(r_i, 1 + off + j, v)
        if j == 8 or (j >= 9 and (j - 9) % 3 == 2):
            cell.number_format = '+0;-0;0'
            if v > 0:
                cell.font = up
            elif v < 0:
                cell.font = down


if __name__ == '__main__':
    main(sys.argv[1], sys.argv[2])
