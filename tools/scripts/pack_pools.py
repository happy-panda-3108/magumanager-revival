"""2026-11-11 (user: "카드팩별, 조합 볼수별, 그리고 볼수, 포지션 별 몇 명이 있는지 이런 것들도 다시 한 번 정리해서 전달해주면 좋겠음"):
cards each pack can give, by ball (rare/elite split) and by main position. Reads the server tables read-only.

  python pack_pools.py [tables dir]
"""
import csv
import io
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
TABLES = sys.argv[1] if len(sys.argv) > 1 else r"C:\Ma9MProject\server\data\tables"
EAST = {9, 7, 8, 0, 10, 19, 11}          # SK·SSG, 롯데, 삼성, 두산·OB, kt, 쌍방울 (Emblem.EastTeam + lineage)
WEST = {5, 1, 6, 2, 14, 3, 13, 4, 15}    # KIA·해태, LG, 한화·빙그레, 히어로즈, NC, 현대·태평양 (Emblem.WestTeam + lineage)
POS = {"1": "포수", "2": "1루", "3": "2루", "4": "3루", "5": "유격", "6": "좌익", "7": "중견", "8": "우익", "9": "지명", "10": "지명",
       "12": "선발", "13": "중계", "14": "셋업", "15": "마무리"}
POS_ORDER = ["포수", "1루", "2루", "3루", "유격", "좌익", "중견", "우익", "지명", "선발", "중계", "셋업", "마무리"]


def load():
    cards = []
    for fn, kind in (("P_BatterTable.txt", "B"), ("P_PItcherTable.txt", "P")):
        for r in csv.DictReader(open(TABLES + "\\" + fn, encoding="utf-8-sig"), delimiter="\t"):
            grade, idx = int(r["cardgrade"]), int(r["idx"])
            if grade >= 5 or 800101 <= idx <= 800299:
                continue   # 레전드: not drawable
            cards.append({"kind": kind, "year": int(r["year"]), "team": int(r["teamcode"]), "ball": int(r["ball"]), "grade": grade,
                          "pos": POS.get(r["mainposition"], r["mainposition"])})
    return cards


PACKS = [("전체", lambda c: True), ("90년대", lambda c: c["year"] <= 1999), ("00년대", lambda c: 2000 <= c["year"] <= 2009),
         ("10년대", lambda c: 2010 <= c["year"] <= 2019), ("20년대", lambda c: c["year"] >= 2020),
         ("동군", lambda c: c["team"] in EAST), ("서군", lambda c: c["team"] in WEST)]


def label(c):
    if c["grade"] >= 4:
        return f"{c['ball']}볼 엘리트"
    if c["grade"] == 3:
        return f"{c['ball']}볼 레어"
    return f"{c['ball']}볼"


def main():
    cards = load()
    other = [c for c in cards if c["team"] not in EAST | WEST]
    print("years", min(c["year"] for c in cards), "-", max(c["year"] for c in cards), "| cards", len(cards), "| 동·서군 밖", len(other))
    rows = []
    for b in range(1, 11):
        for g in ("", "레어", "엘리트"):
            rows.append((b, g))
    print("| 볼 | " + " | ".join(n for n, _ in PACKS) + " |")
    print("|---|" + "---|" * len(PACKS))
    for b, g in rows:
        def ok(c):
            if c["ball"] != b:
                return False
            if g == "엘리트":
                return c["grade"] >= 4
            if g == "레어":
                return c["grade"] == 3
            return c["grade"] <= 2
        counts = [sum(1 for c in cards if f(c) and ok(c)) for _, f in PACKS]
        if any(counts):
            print(f"| {b}볼{(' ' + g) if g else ''} | " + " | ".join(f"{n:,}" for n in counts) + " |")
    print("| 합계 | " + " | ".join(f"{sum(1 for c in cards if f(c)):,}" for _, f in PACKS) + " |")
    print()
    print("| 포지션 | " + " | ".join(n for n, _ in PACKS) + " |")
    print("|---|" + "---|" * len(PACKS))
    for p in POS_ORDER:
        print(f"| {p} | " + " | ".join(f"{sum(1 for c in cards if f(c) and c['pos'] == p):,}" for _, f in PACKS) + " |")
    print()
    print("8볼+ by position (레어·엘리트):")
    print("| 포지션 | " + " | ".join(n for n, _ in PACKS) + " |")
    for p in POS_ORDER:
        print(f"| {p} | " + " | ".join(f"{sum(1 for c in cards if f(c) and c['pos'] == p and c['grade'] >= 3)}/{sum(1 for c in cards if f(c) and c['pos'] == p and c['grade'] >= 4)}" for _, f in PACKS) + " |")
    print("other pos values:", sorted({c["pos"] for c in cards} - set(POS_ORDER)))


if __name__ == "__main__":
    main()
