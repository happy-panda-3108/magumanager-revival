"""2026-11-18 23:2x (user: "육성표에 왜 포지션 변경 대성공 기준으로 스탯 변경을 안 해놓는지 모르겠음 … 01 박석진의 경우, 선발로 포변
했으니까 체력+20, 구속-1, 나머지 4스탯 + 1 해서 계산을 해야하는데, 이런 포지션 변경 내용을 전부 내가 직접 하고 있음"):
the books' 기본 row of a pitcher whose block label says "(A→B…)" becomes the card value + the 포지션 변경 대성공 change
(PositionChangeHandlers.Delta: to SP +20/-1/+1/+1/+1/+1, SP to bullpen -25/+2/+1/+1/+1/+1, bullpen to bullpen 0/0/+1/+1/+1/0).
Rule 12-2 (never touch the user's own edits): a 기본 row is written only while it still equals the card table; one already at
the 대성공 value is left (the user did it), one with any other numbers is left and reported. The label gets "·대성공" and the
block memo's sentences about the change say 대성공 기준.

  python poschange_books.py check <book> ...          → what would change
  python poschange_books.py spec <out spec dir> <book> ...   → spec_<name>.json for book_edit.ps1 (out_dir = <spec dir>\\out)
"""
import csv
import json
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import book_spec as b

sys.stdout.reconfigure(encoding="utf-8")
T = "C:/Ma9MProject/server/data/tables/"
ROLE_KO = {"선발": "SP", "중계": "RP", "셋업": "SU", "마무리": "CP"}
TEAM = {"롯데": "7", "삼성": "8", "LG": "6", "쌍방울": "11", "현대": "4"}
STATS = ["health", "spd", "cont", "technic", "breakball", "mental"]
KO = ["체력", "구속", "컨트롤", "테크닉", "구위", "P정신"]
COLS = "KLMNOP"
ROWS = list(csv.DictReader(open(T + "P_PItcherTable.txt", encoding="utf-8"), delimiter="\t"))


def delta(own, new, great=True):
    if own == "SP" and new != "SP":
        return [-25, 2, 1, 1, 1, 1] if great else [-25, 1, -1, -1, -1, -1]
    if own != "SP" and new == "SP":
        return [20, -1, 1, 1, 1, 1] if great else [20, -2, -1, -1, -1, -1]
    return [0, 0, 1, 1, 1, 0] if great else [0, -1, -1, -1, -1, -1]


def delta_text(d):
    return ", ".join("%s %+d" % (k, v) for k, v in zip(KO, d) if v != 0).replace("+-", "-")


def deck_of(path):
    m = re.search(r"_(\d\d)(롯데|삼성|LG|쌍방울|현대)_", os.path.basename(path))
    yy = int(m.group(1))
    return str(2000 + yy if yy < 50 else 1900 + yy), TEAM[m.group(2)]


def card(name, year, team, tag):
    # 레전드 cards are year 1981 (idx 8001xx/8002xx); '97 쌍방울 has a normal 조규제 too, so a 레전드 block looks there first.
    if "레전드" in (tag or ""):
        cands = [r for r in ROWS if r["name"] == name and r["year"] == "1981"]
    else:
        cands = [r for r in ROWS if r["name"] == name and r["year"] == year and r["teamcode"] == team]
    return cands[0] if len(cands) == 1 else None


def blocks(path):
    cells, com = b.read_book(path)
    year, team = deck_of(path)
    maxr = max(int(re.sub(r"[A-Z]+", "", a)) for a in cells)
    out = []
    for r in range(5, maxr, 9):
        name, label, tag = cells.get("J%d" % (r + 1)), cells.get("P%d" % r), cells.get("J%d" % r)
        if not name or not isinstance(label, str):
            continue
        m = re.search(r"\((선발|중계|셋업|마무리)→(선발|중계|셋업|마무리)", label)
        if not m:
            continue
        own, new = ROLE_KO[m.group(1)], ROLE_KO[m.group(2)]
        c = card(name, year, team, tag)
        base = [cells.get("%s%d" % (col, r + 2)) for col in COLS]
        blk = {"row": r, "name": name, "label": label, "own": own, "new": new, "base": base,
               "memo_addr": "J%d" % (r + 1), "memo": com.get("J%d" % (r + 1))}
        if c is None:
            blk["status"] = "카드 못 찾음"
        else:
            cv = [int(c[s]) for s in STATS]
            if c["mainposition"] != {"SP": "12", "RP": "13", "SU": "14", "CP": "15"}[own]:
                blk["note"] = "카드 원래 보직 %s, 칸 이름은 %s" % (c["mainposition"], own)
            d = delta(own, new)
            great = [v + x for v, x in zip(cv, d)]
            blk["card"], blk["great"] = cv, great
            if base == cv:
                blk["status"] = "바꿈"
            elif base == great:
                blk["status"] = "이미 대성공 값"
            else:
                blk["status"] = "다르게 고친 값(그대로 둠)"
        out.append(blk)
    return out


def bare(text):
    """A comment's text without its author line: HCell's AddComment puts "honey:" in front by itself."""
    return re.sub(r"^honey:\s*", "", text) if text else text


def new_memo(text, own, new):
    if not text:
        return text
    text = bare(text)
    d, s = delta(own, new), delta(own, new, False)
    line = ("기본 줄은 포지션 변경 대성공 기준 값입니다(카드값에서 %s). 성공이면 카드값에서 %s이고, 경기에서는 바뀐 값으로 뜁니다."
            % (delta_text(d), delta_text(s)))
    t = text
    t = re.sub(r"기본은 포지션 변경 전 카드값이고, 바뀐 보직에서는[^.]*\.", line, t)
    t = re.sub(r"기본은 포지션 변경 전 카드값이고 바꾸면[^.]*이 됩니다\.", line, t)
    t = re.sub(r"기본은 포지션 변경 전 카드값입니다\. 중계→선발은 성공하면.*?경기에서는 바뀐 값으로 뜁니다\.", line, t, flags=re.S)
    t = re.sub(r"기본은 포지션 변경 전 카드값입니다\. 이 보직으로 바꾸면\(대성공 기준\).*?경기에서는 바뀐 값으로 뜁니다\.", line, t, flags=re.S)
    t = t.replace("(40만, 성공 기준)", "(40만, 승률은 성공 기준으로 잼)").replace("(40만 MP, 성공 기준)", "(40만 MP, 승률은 성공 기준으로 잼)")
    t = t.replace("(40만 MP, 성공 기준으로 계산)", "(40만 MP, 승률은 성공 기준으로 잼)")
    t = t.replace("(책은 성공 기준)", "(책도 대성공 기준)")
    if line not in t:
        t = t.rstrip() + " " + line
    return t


def new_label(label):
    return label if "대성공" in label else re.sub(r"\((선발|중계|셋업|마무리)→(선발|중계|셋업|마무리)\)", r"(\1→\2·대성공)", label)


def main():
    mode = sys.argv[1]
    if mode == "check":
        for path in sys.argv[2:]:
            print("==", os.path.basename(path))
            for k in blocks(path):
                print("  %3d %-6s %-28s %s → %s | 책 %s | 카드 %s | 대성공 %s | %s %s" % (
                    k["row"], k["name"], k["label"], k["own"], k["new"], k["base"], k.get("card"), k.get("great"),
                    k["status"], k.get("note", "")))
        return
    spec_dir = sys.argv[2]
    os.makedirs(spec_dir, exist_ok=True)
    for path in sys.argv[3:]:
        name = os.path.splitext(os.path.basename(path))[0]
        values, comments, report = [], [], []
        for k in blocks(path):
            if k["status"] == "바꿈":
                for col, v in zip(COLS, k["great"]):
                    values.append(["%s%d" % (col, k["row"] + 2), v])
            if k["status"] in ("바꿈", "이미 대성공 값"):
                if new_label(k["label"]) != k["label"]:
                    values.append(["P%d" % k["row"], new_label(k["label"])])
                m2 = new_memo(k["memo"], k["own"], k["new"])
                if m2 and m2 != bare(k["memo"]):
                    comments.append([k["memo_addr"], m2])
            report.append([k["row"], k["name"], k["status"]])
        # A block back in its card's own role after a reset ("원래 보직"): no change at all; the memo said otherwise.
        cells, com = b.read_book(path)
        for addr, label in cells.items():
            if addr.startswith("P") and isinstance(label, str) and "원래 보직" in label:
                r = int(addr[1:])
                memo = bare(com.get("J%d" % (r + 1)))
                if memo:
                    fixed = re.sub(r"기본은 포지션 변경 전 카드값이고, 바뀐 보직에서는[^.]*\.",
                                   "포지션 변경을 초기화해 카드의 원래 보직이라 능력치 변화는 없습니다(기본 줄 = 카드값).", memo)
                    if fixed != memo:
                        comments.append(["J%d" % (r + 1), fixed])
                        report.append([r, cells.get("J%d" % (r + 1)), "원래 보직: 메모만 바로잡음"])
        spec = {"src": path, "out_dir": os.path.join(spec_dir, "out"), "base": name, "sheet": "육성 계산기",
                "values": values, "fills": [], "fonts": [], "comments": comments, "report": report}
        json.dump(spec, open(os.path.join(spec_dir, "spec_%s.json" % name), "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(name, "칸", len(values), "메모", len(comments), report)


if __name__ == "__main__":
    main()
