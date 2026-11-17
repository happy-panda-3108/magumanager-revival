"""2026-11-17 (user 07:3x, via the systems session: "1군에도 FA선수를 2명 쓸 수 있다는 얘기야. 그래서 2명을 넣는다는 계산 하에 승률을
다시 계산해줬으면 하는 것임. 좀 복잡하겠지만 FA 투수는 포지션 변경이 안 되니 그걸 고려해서 잘 계산해주길 바람"): the pairs of FA cards
each 육성표 deck plays with two FAs at once (TeamColorSim TCSIM_FA_PAIRS).

  python fa_pairs.py <single run log> <env in> <env out> [top FAs] [pairs a deck]

From the single-FA run (fa_domestic.log, TCSIM_FA_DOMESTIC=1): each deck's best `top` FA cards (by their best variant's games),
every variant of theirs that played (the chain one and the plain one), every pair of two different FAs whose spots are apart
(the FA's spot, the man moved and where he goes, the man who leaves) and whose balls stay under the deck's cap, ranked by the
sum of the two single gains; the best `pairs` play, at least 6 batter+pitcher pairs among them. A deck in MUST also plays every
pair with that FA (24롯데 holds '24 박찬호B since 11-17 07:25). The TeamValue screen is not used: on 11-17 it ranked '24 양의지 1st
of 24롯데's batters and the games put him 10th.
"""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fa_book import read, OWNERS  # noqa: E402

DEFAULT_CAP = 150
MUST = {"2024 롯데": "7294199@SS"}  # '24 박찬호B, signed by SKY준우 (systems session, 11-17 07:25)


PITCHER_SPOTS = {"SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP"}


def is_pitcher(spot):
    """The FA's own spot (the part before ">" / "+DH") is a pitcher's."""
    return spot.split(">")[0].replace("+DH", "") in PITCHER_SPOTS


def touched(key):
    """The spots a single try's key touches: "<idx>@SS>3B+DH" → {SS, 3B, DH}."""
    spot = key.split("@", 1)[1]
    plus_dh = spot.endswith("+DH")
    if plus_dh:
        spot = spot[:-3]
    parts = set(spot.split(">"))
    if plus_dh:
        parts.add("DH")
    return parts


def main():
    log, env_in, env_out = sys.argv[1:4]
    top = int(sys.argv[4]) if len(sys.argv) > 4 else 10
    keep = int(sys.argv[5]) if len(sys.argv) > 5 else 24
    base, rows = read(log)
    picks = {}
    report = []
    for deck, recs in rows.items():
        b = base[deck]
        cap = OWNERS.get(deck, (None, None, DEFAULT_CAP))[2]
        by_fa = {}
        for r in recs:
            by_fa.setdefault(r["idx"], []).append(r)
        best = sorted(by_fa.values(), key=lambda v: -max(x["win"] for x in v))
        tops = best[:top]
        cand = [x for v in tops for x in v]
        must = MUST.get(deck)
        if must:
            cand_all = recs
        pairs = {}

        def add(a, c):
            if a["idx"] == c["idx"] or touched(a["key"]) & touched(c["key"]):
                return
            balls = b["balls"] + (a["balls"] - b["balls"]) + (c["balls"] - b["balls"])
            if balls > cap:
                return
            a, c = sorted((a, c), key=lambda x: x["key"])
            k = f"{a['key']}&{c['key']}"
            pairs[k] = (a["win"] - b["win"] + c["win"] - b["win"], a, c, balls)

        for i, a in enumerate(cand):
            for c in cand[i + 1:]:
                add(a, c)
        ranked = sorted(pairs.items(), key=lambda kv: (-kv[1][0], kv[0]))
        chosen = [k for k, _ in ranked[:keep]]
        mixed = [k for k, v in ranked if is_pitcher(v[1]["spot"]) != is_pitcher(v[2]["spot"])]
        for k in mixed[:6]:
            if k not in chosen:
                chosen.append(k)
        if must:
            m = next(r for r in recs if r["key"] == must)
            extra = {}
            for c in cand_all:
                if c["idx"] == m["idx"] or touched(m["key"]) & touched(c["key"]):
                    continue
                balls = b["balls"] + (m["balls"] - b["balls"]) + (c["balls"] - b["balls"])
                if balls > cap:
                    continue
                a2, c2 = sorted((m, c), key=lambda x: x["key"])
                extra[f"{a2['key']}&{c2['key']}"] = 1
            for k in extra:
                if k not in chosen:
                    chosen.append(k)
        picks[deck] = chosen
        report.append(f"{deck}: 기준 {b['win']:.2f}% {b['balls']}볼 상한 {cap}, FA {len(by_fa)}명 중 위 {len(tops)}명, 짝 {len(pairs)}개 → {len(chosen)}개 경기")
        for k in chosen[:5]:
            g, a, c, balls = pairs.get(k, (None, None, None, None)) if k in pairs else (None, None, None, None)
            if g is not None:
                report.append(f"  {a['yy']} {a['name']} {a['spot']} + {c['yy']} {c['name']} {c['spot']}: 한 명씩 더하면 +{g:.2f}%p, {balls}볼")
    text = open(env_in, encoding="utf-8").read().rstrip("\n").split("\n")
    out = []
    for line in text:
        if line.startswith("#"):
            line = "# fa_pairs_env.txt (11-17: fa_domestic_env.txt + TCSIM_FA_PAIRS from fa_pairs.py, user 07:3x \"FA선수를 2명\")"
        out.append(line)
    label_of = {d: d + "#지금" for d in picks}
    out.append("TCSIM_FA_PAIRS=" + ";".join(f"{label_of[d]}={','.join(ks)}" for d, ks in picks.items()))
    open(env_out, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(report))


if __name__ == "__main__":
    main()
