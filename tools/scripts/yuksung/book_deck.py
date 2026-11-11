"""2026-11-11 (user 20:1x: "레전드 투수가 반영되면서 다시 포지션 변경을 돌려야하는 선수들도 반영해줘 … 선발들 승률별로 순서도 변경"):
a 한셀 book's nine and staff as tcsim tokens, read without 한셀 (book_dev.cells): TCSIM_BATS "name@spot" in batting order and
TCSIM_STAFF SP1..CP with the 포지션 변경 the book notes ("(중계→선발·대성공)" → @SP!, "(중계→선발)" → @SP), 레전드 by card idx.
  python book_deck.py <book.cell>  → prints bats and staff"""
import re
import sys

sys.path.insert(0, r"C:\Ma9MProject\tools\scripts\yuksung")
from book_dev import cells  # noqa: E402

LEGENDS = {"최동원": "800101", "오승환": "800102", "김용수": "800103", "박철순": "800104", "선동열": "800105", "송진우": "800106",
           "정민태": "800107", "조규제": "800108", "김광현B": "800109", "이재학": "800110",
           "이대호": "800201", "양준혁": "800202", "박용택": "800203", "김동주A": "800204", "이종범": "800205", "김태균B": "800206",
           "박재홍": "800207", "김기태A": "800208", "정근우": "800209", "박병호B": "800210", "박경수": "800211"}
ROLE = {"선발": "SP", "중계": "RP", "셋업": "SU", "마무리": "CP"}


def token(name, grade):
    return LEGENDS[name] if grade and "레전드" in grade else name


def deck(book):
    c = cells(book)
    bats, staff, names = [], [], []
    for k in range(11):
        r = 5 + 9 * k
        spot, name, grade = c.get(f"H{r}"), c.get(f"B{r + 1}"), c.get(f"B{r}")
        if k < 9 and name and spot:
            bats.append(f"{token(name, grade)}@{spot.split()[-1]}")
        tag, pname, pgrade = c.get(f"P{r}") or "", c.get(f"J{r + 1}"), c.get(f"J{r}")
        m = re.search(r"\((\S+)→(\S+?)(·대성공)?\)", tag)
        change = f"@{ROLE[m.group(2)]}{'!' if m.group(3) else ''}" if m else ""
        staff.append(token(pname, pgrade) + change)
        names.append(f"{tag.split()[0] if tag else '?'} {pname}")
    return ",".join(bats), ",".join(staff), names


if __name__ == "__main__":
    b, s, n = deck(sys.argv[1])
    print(b)
    print(s)
    print(" / ".join(n), file=sys.stderr)
