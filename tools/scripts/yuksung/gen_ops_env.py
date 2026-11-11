"""2026-11-11 (user: "97 쌍방울덱, 25LG 2개의 덱에 대해서 육성표 수정을 마쳤는데 작전 방침이랑 작전실 방침도 같이 승률이 높은 쪽으로
설정하는 방법 알려줘"): 작전 variants for tcsim entry mode (TCSIM_OPS, TeamColorSim.Legends.cs) on the users' edited 11/10 books.
Round 1 (default): the saved setting, everything neutral, each of the ten 작전실 sliders at 0 and 100 on the neutral base.
Later rounds: python gen_ops_env.py <round file .py> fills ROUND (deck → [(tag, ops)]) from that file.
Run: powershell -File run_entry.ps1 -Env ops_env.txt -Log ops1.log -Work <dir with build3 + data>."""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

BAT = ["타격", "공격", "도루", "번트", "수비"]       # 스몰볼0-빅볼100, 빠른0-끈질긴100, 소극0-적극100, 자제0-권장100, 안정0-적극100
PIT = ["투수성향", "승부", "제구", "투구", "교체"]   # 변화구0-직구100, 빠른0-유인구100, 몸쪽0-바깥쪽100, 전력0-완급100, 자제0-권장100
N = "50.50.50.50.50"

# The users' books (docs/육성표/한셀_1110_레전드, 97쌍방울 saved 11-11 00:22, 25LG 11-10 22:37): nine in batting order, staff.
DECKS = {
    "1997 쌍방울": ("최태원@2B,김기태A@1B,김성래@3B,심성보@RF,박경완@C,조원우@LF,김광림@DH,김호@SS,김실@CF",
                 "김원형,김기덕,성영재,오봉옥@SP,오상민@SP,최정환,정수찬,김민국,윤형배,김현욱,800108"),
    "2025 LG": ("박해민@CF,신민재@2B,오스틴@1B,800203@LF,박동원@C,김현수A@DH,문성주@RF,구본혁@3B,오지환@SS",
                "손주영,치리노스,임찬규,송승기,800103@SP!,김진성@RP!,장현식,이지강,이정용,김영우@SU!,유영찬"),
}

# Saved 11-11 00:37 (team_operation / card_operation of 최약체구단S user 16 and SKY해민 user 11; cards outside the 1군 left out).
SAVED = {
    "1997 쌍방울": f"B={N}|P={N}|O김현욱=50.50.50.0|O윤형배=50.50.50.0",
    "2025 LG": "B=50.50.50.0.50|P=" + N + "|" + "|".join([
        "O박해민=0.100.100.0", "O신민재=0.100.100.0", "O오스틴=100.100.0.0", "O박동원=100.0.0.0", "O김현수A=0.100.0.0",
        "O문성주=0.100.50.0", "O구본혁=0.100.100.0", "O오지환=100.0.100.0",
        "O손주영=100.50.50.100", "O치리노스=0.100.40.100", "O임찬규=0.100.50.100", "O송승기=50.50.60.100", "O김진성=50.50.50.0",
        "O장현식=0.0.100.0", "O이지강=0.0.100.50", "O이정용=100.0.100.50", "O김영우=100.0.40.0", "O유영찬=0.0.60.0"]),
}


def sliders(i, v, base=N):
    a = base.split(".")
    a[i] = str(v)
    return ".".join(a)


def round1():
    out = {}
    for deck in DECKS:
        vs = [("지금", SAVED[deck]), ("중립", f"B={N}|P={N}")]
        for i, name in enumerate(BAT):
            for v in (0, 100):
                vs.append((f"{name}{v}", f"B={sliders(i, v)}|P={N}"))
        for i, name in enumerate(PIT):
            for v in (0, 100):
                vs.append((f"{name}{v}", f"B={N}|P={sliders(i, v)}"))
        out[deck] = vs
    return out


def field():
    for line in open(os.path.join(HERE, "entry_env3.txt"), encoding="utf-8"):
        if line.startswith("TCSIM_FIELD="):
            return line.strip()
    raise SystemExit("no field")


def write(path, variants, note, dev=None):
    """dev: deck → TCSIM_DEV spec (book_dev.py; 2026-11-11, the books' own 특강·트레이닝·재계약 numbers), for every variant of it."""
    decks, staffs, bats, ops, devs = [], [], [], [], []
    for deck, vs in variants.items():
        b, st = DECKS[deck]
        for tag, op in vs:
            label = f"{deck}#{tag}"
            decks.append(label)
            staffs.append(f"{label}={st}")
            bats.append(f"{label}={b}")
            ops.append(f"{label}={op}")
            if dev and deck in dev:
                devs.append(f"{label}={dev[deck]}")
    with open(path, "w", encoding="utf-8", newline=chr(10)) as f:
        f.write("# " + note + chr(10))
        f.write(field() + chr(10))
        for key, items in (("TCSIM_DECKS", decks), ("TCSIM_STAFF", staffs), ("TCSIM_BATS", bats), ("TCSIM_OPS", ops), ("TCSIM_DEV", devs)):
            if items:
                f.write(key + "=" + ";".join(items) + chr(10))
        f.write("TCSIM_ENTRY_BONUS=3" + chr(10) + "TCSIM_LECTURE_MAIN=5" + chr(10) + "MA9M_FIELD_ENGINE=1" + chr(10))
    print(path, len(decks), "variants")


if __name__ == "__main__":
    if len(sys.argv) > 1:
        scope = {"DECKS": DECKS, "SAVED": SAVED, "N": N, "sliders": sliders}
        exec(open(sys.argv[1], encoding="utf-8").read(), scope)
        name = os.path.splitext(os.path.basename(sys.argv[1]))[0]
        write(os.path.join(HERE, f"{name}_env.txt"), scope["ROUND"], scope.get("NOTE", name), scope.get("DEV"))
    else:
        write(os.path.join(HERE, "ops_env.txt"), round1(), "2026-11-11 round 1: saved, neutral, each 작전실 slider at 0/100 (gen_ops_env.py)")
