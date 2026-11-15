# FA 선수카드 (user, 2026-11-15 13:2x; engine session). An FA card is a copy of a 8- or 9-ball 레어 card (cardgrade 3) in
# the FA team (teamcode 250 = the original client's eTeamName.FA): idx 7,000,000 + the source idx, the same year, sidx,
# names, stats, defence, records. The engine finds a card's ratings by (kind, sidx, year) (GameData.Row), so the FA card
# plays exactly like its source and card_ratings.csv needs no new rows. Elites, legends and lower cards get none (user:
# "레어만").
#   python -I fa_cards.py <tables dir in> <tables dir out> [list.tsv]
# Reads P_BatterTable.txt / P_PItcherTable.txt from <in>, drops any FA rows already there (so it can run again on the
# live tables after a card rebuild: run it last, after make_cards, legend_install and namesake_names), appends one FA
# row per source card in the source's order, and writes both tables to <out> with the input's line ends. The optional
# list.tsv names every FA card (fa_idx, src_idx, kind, year, team, name, ball, pos) for the card art and the analyses.
import sys
from pathlib import Path

FA_TEAM = 250
FA_BASE = 7_000_000
RARE = 3
CRLF, LF = '\r\n', '\n'


def lines(path):
    return path.read_bytes().decode('utf-8').splitlines(keepends=True)


def is_fa(idx, team):
    return FA_BASE <= idx < FA_BASE + 1_000_000 or team == FA_TEAM


def main():
    src, dst = Path(sys.argv[1]), Path(sys.argv[2])
    listing = Path(sys.argv[3]) if len(sys.argv) > 3 else None
    out_rows = []
    for table, kind in (('P_BatterTable.txt', 'B'), ('P_PItcherTable.txt', 'P')):
        have = lines(src / table)
        eol = CRLF if have[0].endswith(CRLF) else LF
        head = have[0].rstrip(CRLF).split('\t')
        col = {k: i for i, k in enumerate(head)}
        keep, add, dropped = [have[0]], [], 0
        seen = set()
        for l in have[1:]:
            r = l.rstrip(CRLF).split('\t')
            if not r[0].isdigit():
                keep.append(l)
                continue
            idx, team = int(r[0]), int(r[col['teamcode']])
            if is_fa(idx, team):
                dropped += 1
                continue
            if idx >= FA_BASE:
                raise SystemExit(f'{table}: idx {idx} is in the FA range but not an FA card')
            keep.append(l if l.endswith(LF) else l + eol)
            if int(r[col['cardgrade']]) == RARE and int(r[col['ball']]) >= 8:
                f = list(r)
                f[0] = str(FA_BASE + idx)
                f[col['teamcode']] = str(FA_TEAM)
                if f[0] in seen:
                    raise SystemExit(f'{table}: FA idx {f[0]} twice')
                seen.add(f[0])
                add.append('\t'.join(f) + eol)
                pos = r[col['mainposition']]
                out_rows.append((f[0], r[0], kind, r[col['year']], r[col['teamcode']], r[col['name']], r[col['ball']], pos))
        dst.mkdir(parents=True, exist_ok=True)
        (dst / table).write_text(''.join(keep) + ''.join(add), encoding='utf-8', newline='')
        print(f'{table}: {len(keep) - 1} cards kept, {dropped} old FA rows dropped, {len(add)} FA cards added')
    if listing:
        listing.parent.mkdir(parents=True, exist_ok=True)
        with listing.open('w', encoding='utf-8', newline='\n') as f:
            f.write('fa_idx\tsrc_idx\tkind\tyear\tteam\tname\tball\tpos\n')
            for row in out_rows:
                f.write('\t'.join(row) + '\n')
        print(f'{listing}: {len(out_rows)} FA cards')


if __name__ == '__main__':
    main()
