"""2026-11-15 (user 13:2x, FA 선수카드: "FA 선수들이 구현되면, 현재 있는 육성표를 기준으로 각 팀마다 어떤 FA 선수를 사용하는게 승률이
가장 크게 오르는지, 2순위는 어떤 선수인지 등 분석해서 육성표에 추가해주길 바람"): tcsim entry env for the FA variants of every current
book (the eight 레전드 books and 24롯데, the users' edited 한셀 files): each deck as its book has it (nine in batting order with
positions, staff with 포지션 변경, 레전드 by card idx, 육성 numbers: book_deck.py / book_dev.py), neutral 작전, the 11-02 deck league
(133 decks), coaching set +3, 특강 main +5. TeamColorSim.Fa.cs plays the FA variants.
  python gen_fa_env.py screen [batters,pitchers]   → fa_screen_env.txt (TCSIM_FA: the best by card value play)
  python gen_fa_env.py final <screen log> [top]    → fa_final_env.txt (TCSIM_FA_PICK: each deck's best `top` of the screen, default 8)
Run: powershell -File run_entry.ps1 -Env fa_screen_env.txt -Log fa_screen.log -Games 500 -Work <dir with data> -Build <build dir>"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from book_deck import deck  # noqa: E402
from book_dev import spec  # noqa: E402

BOOKS = r"C:\Ma9MProject\docs\육성표"
LEG = BOOKS + r"\한셀_1110_레전드\육성표_1110_{}_레전드.cell"
DECKS = {
    "2025 LG": LEG.format("25LG"),
    "2012 삼성": LEG.format("12삼성"),
    "2001 롯데": LEG.format("01롯데"),
    "2011 롯데": LEG.format("11롯데"),
    "1993 롯데": LEG.format("93롯데"),
    "2001 현대": LEG.format("01현대"),
    "1996 쌍방울": LEG.format("96쌍방울"),
    "1997 쌍방울": LEG.format("97쌍방울"),
    "2024 롯데": BOOKS + r"\한셀_1113_24롯데\육성표_1113_24롯데.cell",
}
BASE = "지금"
FIELD_FROM = BOOKS + r"\1115_작전\ops1115B_env.txt"
COMMON = ["TCSIM_ENTRY_BONUS=3", "TCSIM_LECTURE_MAIN=5", "MA9M_FIELD_ENGINE=1"]


def field():
    for line in open(FIELD_FROM, encoding="utf-8"):
        if line.startswith("TCSIM_FIELD="):
            return line.strip()
    raise SystemExit("no TCSIM_FIELD in " + FIELD_FROM)


def decks():
    bats, staff, dev = {}, {}, {}
    for d, book in DECKS.items():
        b, s, _ = deck(book)
        label = f"{d}#{BASE}"
        bats[label], staff[label] = b, s
        dev[label], _ = spec(book, b, s)
    return bats, staff, dev


def write(path, extra):
    bats, staff, dev = decks()
    labels = list(bats)
    lines = [f"# {os.path.basename(path)} (gen_fa_env.py {' '.join(sys.argv[1:])})", field(),
             "TCSIM_DECKS=" + ";".join(labels),
             "TCSIM_BATS=" + ";".join(f"{k}={v}" for k, v in bats.items()),
             "TCSIM_STAFF=" + ";".join(f"{k}={v}" for k, v in staff.items()),
             "TCSIM_DEV=" + ";".join(f"{k}={v}" for k, v in dev.items())] + COMMON + extra
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")
    print(path, len(labels), "decks")


def screen_results(log):
    """deck label → [(win, key, line)] from a run's 'tcsim entry: <deck>#FA <key> ... 리그 평균 x%' lines, and the base win."""
    out, base = {}, {}
    pat = re.compile(r"tcsim entry: (.+?)#(FA (\S+)|" + BASE + r") .*?리그 평균 ([\d.]+)%")
    raw = open(log, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp949", errors="replace")
    for line in text.splitlines():
        m = pat.search(line)
        if not m:
            continue
        d, key, win = m.group(1), m.group(3), float(m.group(4))
        if key is None:
            base[d] = win
        else:
            out.setdefault(d, []).append((win, key, line))
    return out, base


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "screen"
    if mode == "screen":
        n = sys.argv[2] if len(sys.argv) > 2 else "30,20"
        # The deck's own season's FA only (user 11-15 19:2x: "97쌍방울덱이면 97년도 FA 선수를 써야하는거야").
        write(os.path.join(HERE, "fa_screen_env.txt"), [f"TCSIM_FA={n}", "TCSIM_FA_YEAR=deck"])
    elif mode == "final":
        res, base = screen_results(sys.argv[2])
        top = int(sys.argv[3]) if len(sys.argv) > 3 else 8
        picks = []
        for d, rows in res.items():
            rows.sort(reverse=True)
            # The best `top` by win, and the best pitcher and batter even when they are not among them.
            keys = [k for _, k, _ in rows[:top]]
            for want_bat in (True, False):
                for _, k, line in rows:
                    if ("@SP" in k or "@RP" in k or "@SU" in k or "@CP" in k) != want_bat:
                        if k not in keys:
                            keys.append(k)
                        break
            picks.append(f"{d}#{BASE}=" + ",".join(keys))
            print(d, base.get(d), "->", rows[0][:2] if rows else None, len(keys), "picks")
        write(os.path.join(HERE, "fa_final_env.txt"), ["TCSIM_FA_PICK=" + ";".join(picks)])
