"""2026-11-17 09:2x: the second FA for a deck that already holds one, as the account stands (24롯데 SKY준우 holds '24 원태인 since
09:18: SP5 원태인, 한현희 back to RP4 with no 포지션 변경 (reset 01:41), 김강현 out — the single run's key 7161069@SP5>RP4).

  python fa_pairs_held.py <single run log> <env in> <env out> "<deck>" <held key> <cap>

Every other single try of the deck whose spots are apart from the held one's and whose balls stay under the cap plays with it.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fa_book import read  # noqa: E402
from fa_pairs import touched  # noqa: E402


def main():
    log, env_in, env_out, deck, held, cap = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], int(sys.argv[6])
    base, rows = read(log)
    b = base[deck]
    recs = rows[deck]
    h = next(r for r in recs if r["key"] == held)
    keys = []
    for r in recs:
        if r["idx"] == h["idx"] or touched(r["key"]) & touched(h["key"]):
            continue
        balls = b["balls"] + (h["balls"] - b["balls"]) + (r["balls"] - b["balls"])
        if balls > cap:
            continue
        a, c = sorted((h, r), key=lambda x: x["key"])
        keys.append(f"{a['key']}&{c['key']}")
    out = []
    for line in open(env_in, encoding="utf-8").read().rstrip("\n").split("\n"):
        if line.startswith("#"):
            line = f"# fa_held_env.txt (11-17 09:2x: {deck} holding {held}, every second FA under the cap {cap})"
        elif line.startswith("TCSIM_DECKS="):
            line = f"TCSIM_DECKS={deck}#지금"
        elif line.startswith("TCSIM_FA_PAIRS="):
            line = f"TCSIM_FA_PAIRS={deck}#지금={','.join(keys)}"
        out.append(line)
    open(env_out, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
    sys.stdout.reconfigure(encoding="utf-8")
    print(f"{deck}: held {h['yy']} {h['name']} {h['key']} {h['balls']}볼, cap {cap}, second FA tries {len(keys)}")
    for k in keys:
        print("  " + k)


if __name__ == "__main__":
    main()
