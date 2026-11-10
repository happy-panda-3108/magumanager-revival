"""2026-11-10 (user decision 7: "엘리트가 너무 흔하긴 함 … 9볼 + 23볼 조합을 하면 셋 중 한 번은 엘리트 … 조합 시 기본 볼 보존을 위한
볼 합계를 올리던가 … 팩에서도 엘리트 확률이 너무 높은 것 같음"): MP per 엘리트 card for a player who buys one pack type and
combines every 8·9볼 레어 it gives, feeding the low cards of the same packs (FusionHandlers tables; fill = support balls ÷
(7 × main balls)). Compares today's numbers with proposals.

  python elite_cost.py [packs]

Simplified: only 8볼·9볼 레어 are used as main cards (5~7볼 cards climbing is left out, so the real cost is a little lower);
support beyond the packs' own low cards is bought as 1~7볼 포지션팩 (30,000 MP, 3.3 balls on average) when `buy` is on.
"""
import io
import random
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
ANCHOR = [0, 0.15, 0.35, 0.65, 1.0]
ELITE9_SHARE = 139 / 437      # 9볼 cards that are 엘리트 (card tables 11-10)
FODDER_MP_PER_BALL = 30000 / 3.3

# rung 7 = 8볼 레어, 8 = 9볼 레어, 9 = 9볼 엘리트, 10 = 10볼 엘리트, 6 = 7볼 (lost for this purpose)
NOW = {8: ([7, 8, 9, 10], [[45, 55, 0, 0], [15, 70, 12, 3], [0, 58, 30, 12], [0, 40, 37, 23], [0, 22, 40, 38]]),
       7: ([6, 7, 8, 9, 10], [[45, 55, 0, 0, 0], [15, 70, 12, 3, 0], [0, 62, 26, 9, 3], [0, 42, 36, 15, 7], [0, 25, 40, 22, 13]])}
# 안 A: the main card is safe only from fill 0.65 (was 0.35) and the 엘리트 weights are about half.
PLAN_A = {8: ([7, 8, 9, 10], [[60, 40, 0, 0], [40, 57, 3, 0], [20, 70, 8, 2], [0, 80, 15, 5], [0, 62, 26, 12]]),
          7: ([6, 7, 8, 9, 10], [[60, 40, 0, 0, 0], [40, 57, 3, 0, 0], [20, 66, 12, 2, 0], [0, 74, 20, 5, 1], [0, 58, 30, 9, 3]])}
# 안 B: stricter still; 엘리트 only from a well-filled 9볼 레어.
PLAN_B = {8: ([7, 8, 9, 10], [[60, 40, 0, 0], [45, 55, 0, 0], [25, 73, 2, 0], [0, 90, 8, 2], [0, 76, 17, 7]]),
          7: ([6, 7, 8, 9, 10], [[60, 40, 0, 0, 0], [45, 55, 0, 0, 0], [25, 68, 7, 0, 0], [0, 80, 18, 2, 0], [0, 66, 28, 5, 1]])}
# 안 C: 엘리트 almost only from packs; a full 9볼 레어 combination gives one about one time in eight.
PLAN_C = {8: ([7, 8, 9, 10], [[60, 40, 0, 0], [45, 55, 0, 0], [25, 75, 0, 0], [0, 96, 3, 1], [0, 88, 9, 3]]),
          7: ([6, 7, 8, 9, 10], [[60, 40, 0, 0, 0], [45, 55, 0, 0, 0], [25, 70, 5, 0, 0], [0, 86, 14, 0, 0], [0, 74, 25, 1, 0]])}
PACKS = {"전체팩 지금 (5만, 9볼 2.5%·10볼 1%)": (50000, [20, 18, 15, 13, 11, 9, 6.5, 4, 2.5, 1]),
         "고급팩 지금 (10만, 9볼 7%·10볼 3%)": (100000, [0, 0, 0, 28, 24, 18, 12, 8, 7, 3]),
         "전체팩 안 (5만, 9볼 1.5%·10볼 0.4%)": (50000, [20, 18, 15, 13, 11, 9.5, 7.2, 4.4, 1.5, 0.4]),
         "고급팩 안 (10만, 9볼 4%·10볼 1%)": (100000, [0, 0, 0, 29, 25, 19, 13, 9, 4, 1]),
         "전체팩 안2 (5만, 9볼 1%·10볼 0.3%)": (50000, [20, 18, 15, 13, 11, 9.6, 7.4, 4.7, 1.0, 0.3]),
         "고급팩 안2 (10만, 9볼 2.8%·10볼 0.7%)": (100000, [0, 0, 0, 29, 25, 20, 13.5, 9, 2.8, 0.7])}


def odds(table, rung, fill):
    rungs, w = table[rung]
    lo = 0
    while lo < len(ANCHOR) - 2 and fill > ANCHOR[lo + 1]:
        lo += 1
    t = (fill - ANCHOR[lo]) / (ANCHOR[lo + 1] - ANCHOR[lo])
    return rungs, [(1 - t) * w[lo][k] + t * w[lo + 1][k] for k in range(len(rungs))]


def run(price, pack, table, fill9, fill8, buy, n, rnd):
    balls = list(range(1, 11))
    mp, elites, fodder, r9, r8 = 0.0, 0, 0.0, 0, 0
    for _ in range(n):
        mp += price
        b = rnd.choices(balls, pack)[0]
        if b == 10 or (b == 9 and rnd.random() < ELITE9_SHARE):
            elites += 1
        elif b == 9:
            r9 += 1
        elif b == 8:
            r8 += 1
        else:
            fodder += b
        while True:
            if r9 > 0 and fill9 is not None:
                rung, need = 8, fill9 * 7 * 9
            elif r8 > 0 and fill8 is not None:
                rung, need = 7, fill8 * 7 * 8
            else:
                break
            if fodder < need:
                if not buy:
                    break
                mp += (need - fodder) * FODDER_MP_PER_BALL
                fodder = need
            fodder -= need
            rungs, w = odds(table, rung, fill9 if rung == 8 else fill8)
            out = rnd.choices(rungs, w)[0]
            if rung == 8:
                r9 -= 1
            else:
                r8 -= 1
            if out >= 9:
                elites += 1
            elif out == 8:
                r9 += 1
            elif out == 7:
                r8 += 1
    return mp / max(1, elites)


def main():
    n = int(sys.argv[1]) if len(sys.argv) > 1 else 300000
    rnd = random.Random(11)
    fills = [None, 0.35, 0.65, 1.0]
    for pname, (price, pack) in PACKS.items():
        print(f"== {pname}: 팩만 열 때 엘리트 1장 {run(price, pack, NOW, None, None, False, n, rnd) / 10000:,.0f}만 MP")
        for tname, table in (("조합 지금", NOW), ("조합 안 A", PLAN_A), ("조합 안 B", PLAN_B), ("조합 안 C", PLAN_C)):
            best = None
            for f9 in fills[1:]:
                for f8 in fills:
                    for buy in (False, True):
                        c = run(price, pack, table, f9, f8, buy, n // 3, rnd)
                        if best is None or c < best[0]:
                            best = (c, f9, f8, buy)
            c, f9, f8, buy = best
            print(f"   {tname}: 엘리트 1장 {c / 10000:,.0f}만 MP (9볼 레어에 재료 {f9 * 63:.0f}볼"
                  + (f", 8볼 레어에 {f8 * 56:.0f}볼" if f8 else ", 8볼 레어는 안 씀") + (", 모자란 재료는 포지션팩으로 삼" if buy else "") + ")")


if __name__ == "__main__":
    main()
