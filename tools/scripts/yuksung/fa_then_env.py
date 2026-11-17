"""2026-11-17 (user 10:2x: "01롯데처럼 두 칸 이상의 연쇄를 6가지 덱이 전부 적용해서 다시 재봅시다"): the second stage of the chain run.

  python fa_then_env.py <stage-1 log> <domestic log> <env in> <env out> [first FAs] [also]

Stage 1 (fa_chain3_env.txt, TCSIM_FA_DEPTH=3) played each deck's single FAs with chains of up to three moves. Here each deck's best
`first` FA cards (their best variant by games) become the first FA of TCSIM_FA_THEN; the second FA's tries are made again on the deck
with the first one in (TeamColorSim), and the `also` best single variants by games (stage 1, else the domestic run) always play
when valid there. 24롯데 is measured as the account stands (SKY준우 holds '24 원태인 at SP5, 한현희 back to RP4 with no change,
김강현 out = the key 7161069@SP5>RP4), so its first FA is that key.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from fa_book import read  # noqa: E402

HELD = {"2024 롯데": "7161069@SP5>RP4"}


def main():
    log1, logd, env_in, env_out = sys.argv[1:5]
    first = int(sys.argv[5]) if len(sys.argv) > 5 else 3
    also_n = int(sys.argv[6]) if len(sys.argv) > 6 else 8
    _, rows1 = read(log1)
    _, rowsd = read(logd)
    then, also, report = {}, {}, []
    for deck in list(rows1) + [d for d in HELD if d not in rows1]:
        recs = rows1.get(deck) or rowsd.get(deck) or []
        ranked = sorted(recs, key=lambda r: -r["win"])
        if deck in HELD:
            firsts = [HELD[deck]]
        else:
            seen, firsts = set(), []
            for r in ranked:
                if r["idx"] not in seen:
                    seen.add(r["idx"])
                    firsts.append(r["key"])
                if len(firsts) >= first:
                    break
        held_idx = {k.split("@")[0] for k in firsts}
        pool = sorted(recs + [r for r in rowsd.get(deck, []) if r["key"] not in {x["key"] for x in recs}], key=lambda r: -r["win"])
        bs, keys = [], set()
        for r in pool:
            if r["key"] not in keys and r["idx"] not in held_idx:
                keys.add(r["key"])
                bs.append(r["key"])
            if len(bs) >= also_n:
                break
        then[deck] = firsts
        also[deck] = bs
        report.append(f"{deck}: 첫 FA {', '.join(firsts)} | 늘 재는 둘째 {len(bs)}개")
    out = []
    for line in open(env_in, encoding="utf-8").read().rstrip("\n").split("\n"):
        if line.startswith("#"):
            line = "# fa_then_env.txt (11-17 2단계: 첫 FA를 넣은 덱 위에서 둘째 FA의 연쇄, user 10:2x \"두 칸 이상의 연쇄를 6가지 덱이 전부\")"
        elif line.startswith("TCSIM_DECKS="):
            line = "TCSIM_DECKS=" + ";".join(d + "#지금" for d in then)
        out.append(line)
    out.append("TCSIM_FA_THEN=" + ";".join(f"{d}#지금={'|'.join(k)}" for d, k in then.items()))
    out.append("TCSIM_FA_THEN_ALSO=" + ";".join(f"{d}#지금={','.join(k)}" for d, k in also.items()))
    out.append("TCSIM_FA_THEN_KEEP=8")
    open(env_out, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(report))


if __name__ == "__main__":
    main()
