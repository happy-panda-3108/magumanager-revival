"""Reads 육성표 books: per block (stride 9 from row 5) the batter (B..H) and pitcher (J..P) rows."""
import sys, json, openpyxl

def num(v):
    return v if isinstance(v, (int, float)) else 0

def read(path):
    wb = openpyxl.load_workbook(path)
    ws = wb['육성 계산기']
    out = {'title': ws['B2'].value, 'tc': ws['O2'].value, 'sheets': wb.sheetnames, 'blocks': []}
    r = 5
    while r + 7 <= ws.max_row:
        tagb, tagp = ws.cell(r, 2).value, ws.cell(r, 10).value
        if not tagb and not tagp:
            r += 9
            continue
        blk = {}
        for side, c0 in (('bat', 2), ('pit', 10)):
            name = ws.cell(r + 1, c0).value
            if not name or name in ('컨택', '체력'):
                continue
            row = lambda k: [num(ws.cell(r + k, c0 + 1 + j).value) for j in range(6)]
            blk[side] = {
                'tag': ws.cell(r, c0).value, 'name': name,
                'base': row(2), 'coach': ws.cell(r + 3, c0).value,
                'training': row(4), 'renewal': row(5),
            }
        out['blocks'].append(blk)
        r += 9
    return out

if __name__ == '__main__':
    res = {p: read(p) for p in sys.argv[1:]}
    json.dump(res, sys.stdout, ensure_ascii=False, indent=None)
