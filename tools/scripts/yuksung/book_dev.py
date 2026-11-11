"""2026-11-11 (user 20:0x: "25LG 육성표도 방금 약간 수정했으니 그걸로 반영해서 진행해줘"): the book's 특강·트레이닝·재계약 numbers
per card as a TCSIM_DEV spec (TeamColorSim.Legends.cs ApplyDev). The book is read without 한셀 (read_cell-style: the OOXML zip's
cached values), its nine batter blocks in batting order and eleven pitcher blocks SP1..CP matched in order to the deck's tokens
(gen_ops_env.DECKS: names, 레전드 by card idx).
  python book_dev.py <book.cell> "<batter tokens>" "<staff tokens>"  → prints the spec"""
import re
import sys
import zipfile
import xml.etree.ElementTree as ET

M = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"


def cells(path, sheet="육성 계산기"):
    z = zipfile.ZipFile(path)
    wb = ET.fromstring(z.read("xl/workbook.xml"))
    rels = {r.get("Id"): r.get("Target") for r in ET.fromstring(z.read("xl/_rels/workbook.xml.rels"))}
    target = [rels[s.get(R + "id")] for s in wb.find(M + "sheets") if s.get("name") == sheet][0].lstrip("/")
    shared = []
    if "xl/sharedStrings.xml" in z.namelist():
        shared = ["".join(t.text or "" for t in si.iter(M + "t")) for si in ET.fromstring(z.read("xl/sharedStrings.xml")).findall(M + "si")]
    out = {}
    for c in ET.fromstring(z.read(target if target.startswith("xl/") else "xl/" + target)).iter(M + "c"):
        v = c.find(M + "v")
        if v is None:
            continue
        out[c.get("r")] = shared[int(v.text)] if c.get("t") == "s" else v.text
    return out


def num(x):
    try:
        return int(round(float(x)))
    except (TypeError, ValueError):
        return 0


def spec(book, batters, staff):
    c = cells(book)
    tokens_b = [t.split("@")[0] for t in batters.split(",")]
    tokens_p = [t.split("@")[0] for t in staff.split(",")]
    parts, names = [], []
    for k in range(11):
        r = 5 + 9 * k
        for cols, tokens, name_col in (("CDEFGH", tokens_b, "B"), ("KLMNOP", tokens_p, "J")):
            if k >= len(tokens):
                continue
            name = c.get(f"{name_col}{r + 1}")
            if not name:
                continue
            rows = ["/".join([]) for _ in ()]
            e, t, ren = ([num(c.get(f"{col}{r + off}")) for col in cols] for off in (3, 4, 5))
            parts.append(f"{tokens[k]}:" + "/".join(".".join(str(v) for v in row) for row in (e, t, ren)))
            names.append(f"{tokens[k]}={name}")
    return "|".join(parts), names


if __name__ == "__main__":
    s, names = spec(sys.argv[1], sys.argv[2], sys.argv[3])
    print(s)
    print("#", ", ".join(names), file=sys.stderr)
