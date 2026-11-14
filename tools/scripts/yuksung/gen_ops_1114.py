"""2026-11-14 (user 22:1x: "24, 01롯데의 육성표를 일부 수정했으니, 수정된 육성표를 바탕으로 작전실 및 개인 작전을 추천해줘. 도루 부분이
좀 변경되었다고 들었는데, 97쌍방울쪽도 다시 한 번 확인해줘."): 작전 variants for tcsim entry mode on the users' edited books, the
11-11 method (gen_ops_env.py, ops_round*.py) with each deck read from its book: nine in batting order with positions (머리 줄
"1번 1B"), staff SP1..CP with position changes ("SP5 (중계→선발·대성공)" → @SP!), 레전드 by card idx, the bench from the team's
saved 1군, the saved 작전 from team_operation / card_operation, development as the book says (TCSIM_DEV, book_dev.py).
  python gen_ops_1114.py A          → ops1114A_env.txt (round A: saved, neutral, two bases, groups, team 도루 steps)
  python gen_ops_1114.py B <file>   → ops1114B_env.txt from a round file defining ROUND (deck → [(tag, ops)]) with GROUPS in scope
Run: powershell -File run_entry.ps1 -Env ops1114A_env.txt -Log <log> -Games 2000 -Work <dir with build3 + data>."""
import os
import sqlite3
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
exec(open(os.path.join(HERE, "book_dev.py"), encoding="utf-8").read().split('if __name__ == "__main__":')[0])

DB = "file:C:/Ma9MProject/server/data/ma9m.db?mode=ro"
TABLES = r"C:\Ma9MProject\server\data\tables"
N = "50.50.50.50.50"
LEGEND = {"이대호": "800201", "최동원": "800101", "조규제": "800108"}
ROLE = {"선발": "SP", "중계": "RP", "셋업": "SU", "마무리": "CP"}
DECKS = {
    "2024 롯데": (r"C:\Ma9MProject\docs\육성표\한셀_1113_24롯데\육성표_1113_24롯데.cell", 9),
    "2001 롯데": (r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드\육성표_1110_01롯데_레전드.cell", 7),
    "1997 쌍방울": (r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드\육성표_1110_97쌍방울_레전드.cell", 16),
}


def fnum(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return 0.0


def card_names():
    names = {}
    for n in ("P_BatterTable.txt", "P_PitcherTable.txt"):
        with open(os.path.join(TABLES, n), encoding="utf-8") as f:
            head = f.readline().rstrip("\n").split("\t")
            k = head.index("name")
            for line in f:
                r = line.rstrip("\n").split("\t")
                if r[0].isdigit():
                    names[int(r[0])] = r[k]
    return names


def read_deck(deck):
    """The book's nine (token@pos, 경기 반영 stats), staff (token[@role], stats) and the team's bench and saved 작전."""
    path, user = DECKS[deck]
    c = cells(path)
    bats, pits = [], []
    for k in range(11):
        r = 5 + 9 * k
        if k < 9 and c.get(f"B{r + 1}"):
            label, head, name = c.get(f"B{r}") or "", c.get(f"H{r}") or "", c.get(f"B{r + 1}")
            token = LEGEND[name] if "레전드" in label else name
            stats = dict(zip(["컨택", "파워", "스피드", "선구", "수비", "정신"], (fnum(c.get(f"{col}{r + 7}")) for col in "CDEFGH")))
            bats.append({"token": token, "name": name, "pos": head.split()[-1], "stats": stats})
        if c.get(f"J{r + 1}"):
            label, head, name = c.get(f"J{r}") or "", c.get(f"P{r}") or "", c.get(f"J{r + 1}")
            token = LEGEND[name] if "레전드" in label else name
            role = head.split()[0]
            change = ""
            if "→" in head:
                to = head.split("→", 1)[1].split("·")[0].rstrip(")")
                change = "@" + ROLE[to] + ("!" if "대성공" in head else "")
            stats = dict(zip(["체력", "구속", "컨트롤", "테크닉", "구위", "정신"], (fnum(c.get(f"{col}{r + 7}")) for col in "KLMNOP")))
            pits.append({"token": token + change, "name": name, "role": role, "stats": stats})
    names = card_names()
    db = sqlite3.connect(DB, uri=True)
    lineup = db.execute("SELECT lineup FROM team_lineup WHERE user_code = ?", (user,)).fetchone()[0].split("|")
    first = [int(x) for x in lineup[0].split(",") + lineup[2].split(",") if x.strip().isdigit()]

    # 레전드 are 8001xx/8002xx; 900000+ are ordinary cards (a 1997 쌍방울 윤형배 is 901149).
    def token_of(i):
        return str(i) if 800000 <= i < 900000 else names.get(i, str(i))

    nine = {b["name"] for b in bats} | {b["token"] for b in bats}
    bench = []
    for x in lineup[0].split(","):
        if x.strip().isdigit():
            i = int(x)
            if token_of(i) not in nine and names.get(i) not in nine:
                bench.append(token_of(i))
    off, dfn = db.execute("SELECT offense, defense FROM team_operation WHERE user_code = ?", (user,)).fetchone() or (None, None)
    saved = [f"B={'.'.join(str(v) for v in off)}" if off else f"B={N}", f"P={'.'.join(str(v) for v in dfn)}" if dfn else f"P={N}"]
    # Only the 1군's own cards (by card number: an old 조규제 card's setting is not the 레전드 조규제's).
    for i, ops in db.execute("SELECT card_idx, ops FROM card_operation WHERE user_code = ?", (user,)):
        if i in first:
            saved.append(f"O{token_of(i)}={'.'.join(str(v) for v in ops)}")
    return {"bats": bats, "pits": pits, "bench": bench, "saved": "|".join(saved)}


def own(tokens, values):
    return "|".join(f"O{t.split('@')[0]}={values}" for t in tokens)


def groups(d):
    """Player groups by the book's 경기 반영 numbers (the 11-11 thresholds)."""
    b, p = d["bats"], d["pits"]
    sp = [x for x in p if x["role"].startswith("SP")]
    rp = [x for x in p if not x["role"].startswith("SP")]
    return {
        "small": [x["token"] for x in b if x["stats"]["파워"] < 75],                         # 파워 under 75: 스몰볼, 끈질긴, 도루
        "slow": [x["token"] for x in b if x["stats"]["스피드"] < 70],                        # 스피드 under 70: below the 도루 break-even
        "sp_good": [x["token"] for x in sp if x["stats"]["컨트롤"] >= 75],
        "sp_low": [x["token"] for x in sp if x["stats"]["컨트롤"] < 75],
        "rp_good": [x["token"] for x in rp if x["stats"]["컨트롤"] >= 80],
        "rp_low": [x["token"] for x in rp if x["stats"]["컨트롤"] < 75],
        "sp": [x["token"] for x in sp], "rp": [x["token"] for x in rp],
    }


def round_a(d):
    g = groups(d)
    B = "100.100.100.0.100"
    P97, PLG = "50.50.50.0.25", "0.50.0.50.0"

    def c2(team_steal=100, hook=25, slow0=False):
        parts = [f"B=100.100.{team_steal}.0.100", f"P=50.50.50.0.{hook}", own(g["small"], "0.100.100.0"),
                 own(g["sp_good"], "50.50.50.25"), own(g["sp_low"], "50.0.50.25"), own(g["rp_low"], "50.0.50.0")]
        if slow0:
            parts.append(own([t for t in g["slow"] if t not in g["small"]], "100.100.0.0"))
            parts.append(own([t for t in g["slow"] if t in g["small"]], "0.100.0.0"))
        return "|".join(x for x in parts if x)

    lg = "|".join(x for x in [f"B={B}", f"P={PLG}", own(g["small"], "0.100.100.0"), own(g["sp"], "0.100.0.50"),
                              own(g["rp_good"], "0.100.0.0"), own(g["rp_low"], "0.0.0.0")] if x)
    return [
        ("지금", d["saved"]),
        ("중립", f"B={N}|P={N}"),
        ("C97", f"B={B}|P={P97}"),
        ("CLG", f"B={B}|P={PLG}"),
        ("C97+스몰볼", "|".join(x for x in [f"B={B}", f"P={P97}", own(g["small"], "0.100.100.0")] if x)),
        ("C2", c2()),
        ("C2+느린주자도루0", c2(slow0=True)),
        ("C2팀도루75", c2(team_steal=75)),
        ("C2팀도루50", c2(team_steal=50)),
        ("C2교체50", c2(hook=50)),
        ("LG형", lg),
    ]


def field():
    for line in open(os.path.join(HERE, "entry_env3.txt"), encoding="utf-8"):
        if line.startswith("TCSIM_FIELD="):
            return line.strip()
    raise SystemExit("no field")


def write(path, rounds, note, data):
    decks, staffs, bats, ops, devs = [], [], [], [], []
    for deck, vs in rounds.items():
        d = data[deck]
        nine = ",".join(f"{b['token']}@{b['pos']}" for b in d["bats"])
        bat = nine + ("/" + ",".join(d["bench"]) if d["bench"] else "")
        staff = ",".join(p["token"] for p in d["pits"])
        dev = spec(DECKS[deck][0], nine, staff)[0]
        for tag, op in vs:
            label = f"{deck}#{tag}"
            decks.append(label)
            staffs.append(f"{label}={staff}")
            bats.append(f"{label}={bat}")
            ops.append(f"{label}={op}")
            devs.append(f"{label}={dev}")
    with open(path, "w", encoding="utf-8", newline=chr(10)) as f:
        f.write("# " + note + chr(10))
        f.write(field() + chr(10))
        for key, items in (("TCSIM_DECKS", decks), ("TCSIM_STAFF", staffs), ("TCSIM_BATS", bats), ("TCSIM_OPS", ops), ("TCSIM_DEV", devs)):
            f.write(key + "=" + ";".join(items) + chr(10))
        f.write("TCSIM_ENTRY_BONUS=3" + chr(10) + "TCSIM_LECTURE_MAIN=5" + chr(10) + "MA9M_FIELD_ENGINE=1" + chr(10))
    print(path, len(decks), "variants")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    data = {deck: read_deck(deck) for deck in DECKS}
    for deck, d in data.items():
        g = groups(d)
        print("##", deck)
        print("  타선", ", ".join(f"{b['token']}@{b['pos']}(파{b['stats']['파워']:.0f} 주{b['stats']['스피드']:.0f})" for b in d["bats"]), "| 후보", ",".join(d["bench"]))
        print("  투수", ", ".join(f"{p['role']} {p['token']}(컨{p['stats']['컨트롤']:.0f})" for p in d["pits"]))
        print("  지금", d["saved"])
        print("  묶음", {k: v for k, v in g.items() if k not in ("sp", "rp")})
    mode = sys.argv[1] if len(sys.argv) > 1 else "A"
    if mode == "A":
        write(os.path.join(HERE, "ops1114A_env.txt"), {deck: round_a(d) for deck, d in data.items()},
              "2026-11-14 round A: saved, neutral, two bases, groups, team 도루 steps on the edited books (gen_ops_1114.py A)", data)
    else:
        scope = {"data": data, "groups": groups, "own": own, "N": N}
        exec(open(sys.argv[2], encoding="utf-8").read(), scope)
        write(os.path.join(HERE, "ops1114B_env.txt"), scope["ROUND"], scope.get("NOTE", "round B"), data)
