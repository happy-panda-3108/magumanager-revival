"""특성 값 다시 맞추기 (systems session, 2026-11-15; user 04:0x "가 추천안으로 한 번 가보자고").

Reads --traitadvice outputs of each trait alone at its max (TRAITADVICE_SETS t<idx>=<idx>:<max>, one file per club) and prints,
per trait, the full-game win %p a point averaged over the clubs, the tier target (batter .36 .42 .48 .55 1.10, pitcher x1.1)
and the share of its per-point amounts that meets it (MA9M_TRAIT_SCALE form).

  python -I trait_rescale.py <dir> <tag> <club,club,...> [--now "idx:share,..."]

--now: the MA9M_TRAIT_SCALE the runs were made with (the new share is multiplied by it).
"""
import os
import re
import sys

MAX = {101: 2, 102: 2, 103: 2, 104: 2, 105: 2, 106: 2, 107: 2, 110: 2, 111: 1, 112: 2, 113: 1, 114: 2, 115: 2, 118: 1, 120: 1,
       121: 2, 122: 2, 123: 2, 124: 2, 125: 2, 126: 2, 127: 2, 130: 1, 131: 2, 132: 2, 133: 1, 134: 2, 135: 2, 138: 1, 140: 1}
LEVEL = {101: 1, 102: 1, 103: 1, 104: 1, 105: 2, 106: 2, 107: 2, 110: 3, 111: 3, 112: 3, 113: 4, 114: 4, 115: 4, 118: 5, 120: 5,
         121: 1, 122: 1, 123: 1, 124: 1, 125: 2, 126: 2, 127: 2, 130: 3, 131: 3, 132: 3, 133: 4, 134: 4, 135: 4, 138: 5, 140: 5}
TARGET = [0.36, 0.42, 0.48, 0.55, 1.10]


def target(i):
    return TARGET[LEVEL[i] - 1] * (1.1 if i >= 121 else 1)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    d, tag, clubs = sys.argv[1], sys.argv[2], sys.argv[3].split(",")
    now = {}
    if "--now" in sys.argv:
        for kv in sys.argv[sys.argv.index("--now") + 1].split(","):
            k, v = kv.split(":")
            now[int(k)] = float(v)
    # --base lo:hi keeps the clubs whose win share with no traits is in [lo, hi] % (far from 50% the values are squeezed);
    # --median takes each trait's median over the clubs; --keep-batting keeps the batting tree's total as measured and sets the
    # pitching tree to 1.1 times it (each trait at its tier's share of that level).
    lo, hi = (float(x) for x in sys.argv[sys.argv.index("--base") + 1].split(":")) if "--base" in sys.argv else (0, 100)
    median = "--median" in sys.argv
    keep = "--keep-batting" in sys.argv
    vals, tv = {}, {}
    used = []
    for c in clubs:
        p = os.path.join(d, f"{tag}_{c}.txt")
        if not os.path.exists(p):
            continue
        # Server stdout captured through a Python subprocess is cp949 on this PC; the patterns below are ASCII only.
        text = open(p, encoding="utf-8", errors="replace").read()
        mb = re.search(r"\] sim none\s+: win ([\d.]+)%", text)
        if not mb or not lo <= float(mb.group(1)) <= hi:
            continue
        got = False
        for line in text.splitlines():
            m = re.search(r"\] sim t(\d+)\s.*win [\d.]+% \(([+-][\d.]+)%p over none.*TeamValue ([+-][\d.]+)%p", line)
            if m:
                i = int(m.group(1))
                vals.setdefault(i, []).append(float(m.group(2)) / MAX[i])
                tv.setdefault(i, []).append(float(m.group(3)) / MAX[i])
                got = True
        if got:
            used.append(c)
    print(f"clubs ({len(used)}): {', '.join(used)}")

    def mid(xs):
        if not median:
            return sum(xs) / len(xs)
        s = sorted(xs)
        n = len(s)
        return s[n // 2] if n % 2 else (s[n // 2 - 1] + s[n // 2]) / 2

    v_of = {i: mid(vals[i]) for i in vals}
    bat_now = sum(v_of[i] * MAX[i] for i in vals if i <= 120)
    bat_target = sum(target(i) * MAX[i] for i in vals if i <= 120)
    k = bat_now / bat_target if keep else 1.0
    print(f"목표 배율 k = {k:.3f}" + (" (타자 트리 합을 지금대로)" if keep else ""))
    print("특성 | 경기(점당) | 최소~최대 | TeamValue | 목표×k | 배율")
    shares = {}
    for i in sorted(vals):
        v, t = v_of[i], target(i) * k
        f = max(0.4, min(2.0, t / v)) if v > 0.02 else 2.0
        shares[i] = round(f * now.get(i, 1.0), 2)
        print(f"{i} | {v:.2f} | {min(vals[i]):.2f}~{max(vals[i]):.2f} | {sum(tv[i]) / len(tv[i]):.2f} | {t:.2f} | x{f:.2f}")
    for tree, a, b in (("타자", 101, 120), ("투수", 121, 140)):
        s = sum(v_of[i] * MAX[i] for i in vals if a <= i <= b)
        t = sum(target(i) * k * MAX[i] for i in vals if a <= i <= b)
        print(f"{tree} 트리 26점: 경기 {s:.2f} / 목표 {t:.2f}")
    print("MA9M_TRAIT_SCALE=" + ",".join(f"{i}:{shares[i]}" for i in sorted(shares)))


if __name__ == "__main__":
    main()
