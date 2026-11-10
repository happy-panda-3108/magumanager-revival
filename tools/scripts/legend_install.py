# Legend cards into a copy of the live data (engine session, 2026-11-08). make_cards --legends appends the legends to
# its own tables and card_ratings.csv; this takes those rows and appends them to the live server's files, with the name
# the live tables give the player (namesake letters like 김광현B come from namesake_names.py after make_cards) and the
# live files' line ends (the tables are LF, card_ratings.csv CRLF; each file's own is kept).
#   python legend_install.py <make_cards out> <live data dir> <target data dir>
# Writes <target>/tables/P_BatterTable.txt, P_PItcherTable.txt and <target>/sim/card_ratings.csv (live + legends);
# refuses when the live files already hold a legend row.
import sys
from pathlib import Path

src, live, target = (Path(a) for a in sys.argv[1:4])
LEGEND_LO, LEGEND_HI = 800101, 800299
CRLF, LF = '\r\n', '\n'


def lines(path):
    return path.read_bytes().decode('utf-8').splitlines(keepends=True)  # no newline translation


def eol_of(rows):
    return CRLF if rows[0].endswith(CRLF) else LF


def append(path, have, add, eol):
    path.parent.mkdir(parents=True, exist_ok=True)
    gap = '' if have[-1].endswith(LF) else eol
    path.write_text(''.join(have) + gap + ''.join(add), encoding='utf-8', newline='')


for table in ('P_BatterTable.txt', 'P_PItcherTable.txt'):
    have = lines(live / 'tables' / table)
    eol = eol_of(have)
    head = have[0].rstrip(CRLF).split('\t')
    if lines(src / table)[0].rstrip(CRLF).split('\t') != head:
        raise SystemExit(f'{table}: columns differ from the live table')
    col = {k: i for i, k in enumerate(head)}
    names = {}
    for l in have[1:]:
        r = l.rstrip(CRLF).split('\t')
        if r[0].isdigit() and LEGEND_LO <= int(r[0]) <= LEGEND_HI:
            raise SystemExit(f'{table}: the live table already has legend {r[0]}')
        if r[0].isdigit():
            y = int(r[col['year']])
            if r[col['sidx']] not in names or y > names[r[col['sidx']]][0]:
                names[r[col['sidx']]] = (y, r[col['fullname']], r[col['name']])
    add = []
    for l in lines(src / table)[1:]:
        r = l.rstrip(CRLF).split('\t')
        if r[0].isdigit() and LEGEND_LO <= int(r[0]) <= LEGEND_HI:
            if r[col['sidx']] in names:
                r[col['fullname']], r[col['name']] = names[r[col['sidx']]][1:]
            add.append('\t'.join(r) + eol)
    append(target / 'tables' / table, have, add, eol)
    print(f'{table}: {len(have) - 1} live rows + {len(add)} legends')

have = lines(live / 'sim' / 'card_ratings.csv')
if any(l.split(',')[1] == '1981' for l in have[1:]):
    raise SystemExit('card_ratings.csv: the live file already has legend rows')
eol = eol_of(have)
add = [l.rstrip(CRLF) + eol for l in lines(src / 'card_ratings.csv')[1:] if l.split(',')[1] == '1981']
append(target / 'sim' / 'card_ratings.csv', have, add, eol)
print(f'card_ratings.csv: {len(have) - 1} live rows + {len(add)} legends')
