"""2026-11-11 (user 20:1x: "육성표에 레전드 투수가 반영되면서 다시 포지션 변경을 돌려야하는 선수들도 반영해줘 (01롯데 문동환 중계) 그리고
선발들 승률별로 순서도 변경해서 다시 정렬해줘."): two steps on the 1110 레전드 books as saved now (book_deck.py).
  python legend3.py staff  → legend3_env.txt: entry variants for the pitchers a 레전드 pushed out (01롯데 문동환 back to 중계,
                             11롯데 고원준 선발→중계, 93롯데 김정훈A for 박동희 out of role) against the 11-02 deck league.
  python legend3.py order <deck=staff;…>  → sporder_env.txt: plan mode, each starter's win% when he starts every game
                             (TCSIM_PLAN_PITCHERS=1, TCSIM_PLAN_SLOTS deck=SPX), for all eight books with the chosen staffs.
Run: run_entry.ps1 -Env legend3_env.txt -Log legend3.log -Work <copy> -Build build4; run_legend_plan.ps1 -Env sporder_env.txt
-Log sporder.log -Games 50000 -Work <copy>."""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from book_deck import deck  # noqa: E402

BOOKS = r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드"
LABEL = {"01롯데": "2001 롯데", "11롯데": "2011 롯데", "93롯데": "1993 롯데", "25LG": "2025 LG", "12삼성": "2012 삼성",
         "97쌍방울": "1997 쌍방울", "96쌍방울": "1996 쌍방울", "01현대": "2001 현대"}


def book(d):
    return deck(os.path.join(BOOKS, f"육성표_1110_{d}_레전드.cell"))


def swap(staff, old, new):
    items = staff.split(",")
    hits = [i for i, it in enumerate(items) if re.split(r"[@!]", it)[0] == old]
    assert len(hits) == 1, (old, staff)
    items[hits[0]] = new
    return ",".join(items)


def field():
    for line in open(os.path.join(HERE, "entry_env3.txt"), encoding="utf-8"):
        if line.startswith("TCSIM_FIELD="):
            return line.strip()
    raise SystemExit("no field")


def write(path, rows, extra, note):
    with open(path, "w", encoding="utf-8", newline=chr(10)) as f:
        f.write("# " + note + chr(10))
        f.write(field() + chr(10))
        f.write("TCSIM_DECKS=" + ";".join(r[0] for r in rows) + chr(10))
        f.write("TCSIM_BATS=" + ";".join(f"{r[0]}={r[1]}" for r in rows) + chr(10))
        f.write("TCSIM_STAFF=" + ";".join(f"{r[0]}={r[2]}" for r in rows) + chr(10))
        for line in extra:
            f.write(line + chr(10))
    print(path, len(rows), "rows")


def staff_round():
    rows = []
    b, s, _ = book("01롯데")
    rows.append(("2001 롯데#현재", b, s))
    for old in ("김사율", "김영수", "임경완", "기론"):
        rows.append((f"2001 롯데#문동환중계_{old}대신", b, swap(s, old, "문동환")))
    b, s, _ = book("11롯데")
    rows.append(("2011 롯데#현재", b, s))
    for old in ("강영식", "진명호", "이재곤", "김일엽"):
        rows.append((f"2011 롯데#고원준중계_{old}대신", b, swap(s, old, "고원준@RP")))
    b, s, _ = book("93롯데")
    rows.append(("1993 롯데#현재", b, s))
    rows.append(("1993 롯데#김정훈A_박동희대신", b, swap(s, "박동희", "김정훈A")))
    # Before the club owns them (user 20:3x: "못 먹은 선수에 대해서도 고려해서 … 후보도 육성은 해두고 주전을 획득하면 교체"; owners by the
    # DB 20:3x: 01롯데 SKY마토 lacks 최동원L, 11롯데 SKY준우 최동원L·진명호, 12삼성 SKY승환 오승환L): the staffs they play until then.
    b, s, _ = book("01롯데")
    rows.append(("2001 롯데#얻기전_문동환선발", b, swap(s, "800101", "문동환@SP!")))
    b, s, _ = book("11롯데")
    pre = swap(s, "800101", "고원준")
    for rp in ("김수완", "이명우", "허준혁A"):
        rows.append((f"2011 롯데#얻기전_고원준선발_{rp}", b, swap(pre, "진명호", rp)))
    b, s, _ = book("12삼성")
    rows.append(("2012 삼성#얻기전_오승환12마무리", b, swap(s, "800102", "오승환")))
    write(os.path.join(HERE, "legend3_env.txt"), rows, ["TCSIM_ENTRY_BONUS=3", "TCSIM_LECTURE_MAIN=5", "MA9M_FIELD_ENGINE=1"],
          "2026-11-11 legend3 staff: pitchers a 레전드 pushed out (legend3.py staff)")


# Chosen from legend3.log (11-11 22:2x, 133 decks × 4,000): 01롯데 문동환 중계 for 기론 71.22 (now 70.78), 11롯데 고원준 중계 for
# 이재곤 64.20 (63.68), 93롯데 김정훈A for 박동희 64.93 (64.56).
CHOSEN = {"2001 롯데": ("기론", "문동환"), "2011 롯데": ("이재곤", "고원준@RP"), "1993 롯데": ("박동희", "김정훈A")}


def chosen_staffs():
    out = {}
    for d, label in LABEL.items():
        b, s, _ = book(d)
        out[label] = (b, swap(s, *CHOSEN[label]) if label in CHOSEN else s)
    return out


# Plan (+9 gains a stat, as gen_legend_env.py plan) for the pitchers that take a new bullpen slot, and the 11롯데 reliever
# until 진명호 is owned (허준혁A, 60.13; 김수완 60.08, 이명우 59.97 — within the noise).
def relief_round():
    staffs = chosen_staffs()
    rows, slots = [], []
    for label, slot in (("2001 롯데", "RP4"), ("2011 롯데", "RP3"), ("1993 롯데", "RP3")):
        rows.append((label, staffs[label][0], staffs[label][1]))
        slots.append(f"{label}={slot}")
    b, s, _ = book("11롯데")
    pre = swap(swap(s, "800101", "고원준"), "진명호", "허준혁A")
    rows.append(("2011 롯데#얻기전", b, pre))
    slots.append("2011 롯데#얻기전=RP2")
    path = os.path.join(HERE, "relief_env.txt")
    with open(path, "w", encoding="utf-8", newline=chr(10)) as f:
        f.write("# 2026-11-11 relief plan: new bullpen slots after the 레전드 (legend3.py relief)" + chr(10))
        f.write("TCSIM_DECKS=" + ";".join(r[0] for r in rows) + chr(10))
        f.write("TCSIM_BATS=" + ";".join(f"{r[0]}={r[1]}" for r in rows) + chr(10))
        f.write("TCSIM_STAFF=" + ";".join(f"{r[0]}={r[2]}" for r in rows) + chr(10))
        f.write("TCSIM_PLAN_SLOTS=" + ";".join(slots) + chr(10))
        f.write("TCSIM_PLAN_PITCHERS=1\nTCSIM_LECTURE_MAIN=5\nTCSIM_PLAN_COACHES=0\nTCSIM_COACH_SET=3\nMA9M_FIELD_ENGINE=1\n")
    print(path, len(rows), "rows")


def order_round(chosen):
    rows = []
    staffs = chosen_staffs()
    for d, label in LABEL.items():
        b, s = staffs[label]
        rows.append((label, b, chosen.get(label, s)))
    slots = "TCSIM_PLAN_SLOTS=" + ";".join(f"{r[0]}=SPX" for r in rows)
    write(os.path.join(HERE, "sporder_env.txt"), rows,
          [slots, "TCSIM_PLAN_PITCHERS=1", "TCSIM_PLAN_COACHES=0", "TCSIM_COACH_SET=3", "TCSIM_LECTURE_MAIN=5", "MA9M_FIELD_ENGINE=1"],
          "2026-11-11 sporder: each starter's win% when he starts every game (legend3.py order)")


if __name__ == "__main__":
    if sys.argv[1] == "staff":
        staff_round()
    elif sys.argv[1] == "relief":
        relief_round()
    else:
        chosen = dict(kv.split("=", 1) for kv in (sys.argv[2].split(";") if len(sys.argv) > 2 and sys.argv[2] else []))
        order_round(chosen)
