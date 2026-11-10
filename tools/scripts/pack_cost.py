"""2026-11-10 (user decisions 6·7: "동군팩, 서군팩, 연대팩을 만들건데, 확률을 어떻게 할지 고민 … 팀별로 2.5억 MP정도", "엘리트가 너무 흔함"):
how many packs a single team-year set deck (팀컬러 S: all 25 of the 1군 from one team-year) takes, per pack design.

  python pack_cost.py [runs]            (reads C:\\Ma9MProject\\server\\data\\tables, read-only)

A pack gives one card: the ball from the pack's odds, then a random card of that ball in the pack's pool that the team does
not own (CardPacks.Open; the nearest ball when none is free). The team keeps what it draws up to the storage (1,400) and then
releases its lowest non-target cards, which can be drawn again. Target = the deck's 9 starters + 11 pitchers (the 11-07 books)
and any 5 more batters of the team-year. Also prints when the 8볼+ targets alone are complete.
"""
import csv
import io
import random
import statistics
import sys

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
TABLES = r"C:\Ma9MProject\server\data\tables"
EAST = {9, 7, 8, 0, 10, 19, 11}          # SK·SSG, 롯데, 삼성, 두산·OB, kt, 쌍방울 (Emblem.EastTeam + lineage)
WEST = {5, 1, 6, 2, 14, 3, 13, 4, 15}    # KIA·해태, LG, 한화·빙그레, 히어로즈, NC, 현대·태평양
ALL = [20, 18, 15, 13, 11, 9, 6.5, 4, 2.5, 1]
HIGH = [0, 0, 0, 28, 24, 18, 12, 8, 7, 3]
# Proposed with decision 7 (fewer 9·10볼): 전체 9볼+ 3.5 → 1.9%, 고급 9볼+ 10 → 5%.
ALL2 = [20, 18, 15, 13, 11, 9.5, 7.2, 4.4, 1.5, 0.4]
HIGH2 = [0, 0, 0, 29, 25, 19, 13, 9, 4, 1]
STORAGE = 1400

DECKS = {
    "2001 현대": (2001, 4, "박진만,전준호A,심정수,박경완,퀸란,이숭용,박재홍,필립스,박종호", "임선동,마일영,전준호B,테일러A,송신영,권준헌,박장희,이상열,김수경,신철인,위재영"),
    "1997 쌍방울": (1997, 11, "최태원,김기태A,김성래,심성보,박경완,조원우,김광림,김호,김실", "김원형,김기덕,성영재,오봉옥,오상민,최정환,정수찬,김민국,윤형배,김현욱,조규제"),
    "2025 LG": (2025, 6, "박해민,신민재,오스틴,문보경,박동원,김현수A,문성주,구본혁,오지환", "손주영,치리노스,임찬규,송승기,에르난데스C,김진성,장현식,이지강,이정용,김영우,유영찬"),
    "2012 삼성": (2012, 8, "박한이,배영섭,이승엽,박석민,최형우,진갑용,조동찬,정형식,김상수B", "배영수,장원삼,윤성환,고든,탈보트,권오준,심창민,권혁,차우찬,안지만,오승환"),
    "2001 롯데": (2001, 7, "김주찬,박현승,호세,조경환,얀A,최기문,김민재,박정태A,김대익", "박지철,손민한,염종석,강상수,문동환,김사율,김영수,임경완,기론,가득염,박석진"),
    "1993 롯데": (1993, 7, "전준호A,김종헌,김응국,김민재,김민호A,박계원,이종운,공필성,강성우", "김상현A,염종석,윤형배,윤학길,가득염,김태석,이상번,박동희,김정훈A,김태형B,서호진"),
    "1996 쌍방울": (1996, 11, "김실,김광림,박경완,김기태A,심성보,석수철,조원우,최태원,김호", "오봉옥,성영재,박성기,김원형,박진석,최정환,박주언,김현욱,김민국,김기덕,조규제"),
    "2011 롯데": (2011, 7, "전준우,김주찬,이대호,강민호,손아섭,황재균,홍성흔,조성환,문규현", "장원준,사도스키,고원준,송승준,코리,강영식,진명호,이재곤,김일엽,임경완,김사율"),
}


def load():
    cards = []
    for fn, kind in (("P_BatterTable.txt", "B"), ("P_PItcherTable.txt", "P")):
        for r in csv.DictReader(open(TABLES + "\\" + fn, encoding="utf-8"), delimiter="\t"):
            if int(r["cardgrade"]) >= 5:
                continue   # 레전드: not in packs
            cards.append({"idx": int(r["idx"]), "kind": kind, "year": int(r["year"]), "team": int(r["teamcode"]),
                          "name": r["name"], "ball": int(r["ball"]), "grade": int(r["cardgrade"])})
    return cards


def simulate(cards, pool_ok, odds, need, extra_bat, rnd, storage=STORAGE, limit=60000):
    """Packs until `need` (idx set) and 5 of `extra_bat` are owned. Returns (packs to all 8볼+ targets, packs to all)."""
    free = {b: [] for b in range(1, 11)}
    info = {}
    for c in cards:
        if pool_ok(c):
            free[c["ball"]].append(c["idx"])
            info[c["idx"]] = c
    lo = next(i for i, x in enumerate(odds) if x > 0) + 1
    hi = max(i for i, x in enumerate(odds) if x > 0) + 1
    balls = list(range(1, 11))
    owned = []          # non-target cards held (may be released)
    left = set(need)
    high_left = {i for i in need if info[i]["ball"] >= 8}
    extra = 0
    packs = 0
    high_at = 0 if not high_left else None
    while (left or extra < 5) and packs < limit:
        packs += 1
        b = rnd.choices(balls, odds)[0]
        idx = None
        for spread in range(10):
            for bb in {b - spread, b + spread}:
                if lo <= bb <= hi and free[bb]:
                    k = rnd.randrange(len(free[bb]))
                    free[bb][k], free[bb][-1] = free[bb][-1], free[bb][k]
                    idx = free[bb].pop()
                    break
            if idx is not None:
                break
        if idx is None:
            break
        if idx in left:
            left.discard(idx)
            high_left.discard(idx)
            if not high_left and high_at is None:
                high_at = packs
        elif idx in extra_bat:
            extra += 1
        else:
            owned.append(idx)
            if len(owned) + 25 >= storage:
                # release the 200 lowest-ball cards; they can come again
                owned.sort(key=lambda i: info[i]["ball"])
                for i in owned[:200]:
                    free[info[i]["ball"]].append(i)
                del owned[:200]
    return high_at if high_at is not None else packs, packs


def main():
    runs = int(sys.argv[1]) if len(sys.argv) > 1 else 40
    cards = load()
    rnd = random.Random(7)
    by = {(c["kind"], c["year"], c["team"], c["name"]): c for c in cards}
    print(f"cards {len(cards)}: 동군 {sum(c['team'] in EAST for c in cards)}, 서군 {sum(c['team'] in WEST for c in cards)}; "
          + ", ".join(f"{d}년대 {sum(c['year'] // 10 == d // 10 for c in cards)}" for d in (1990, 2000, 2010, 2020)))
    east = lambda c, y, t: (c["team"] in EAST) == (t in EAST)
    decade = lambda c, y, t: c["year"] // 10 == y // 10
    designs = [("연대팩 지금확률", decade, ALL), ("연대팩 줄인확률", decade, ALL2), ("동·서군팩 지금확률", east, ALL), ("동·서군팩 줄인확률", east, ALL2),
               ("연대팩 고급지금", decade, HIGH), ("연대팩 고급줄임", decade, HIGH2), ("동·서군 고급줄임", east, HIGH2)]
    if len(sys.argv) > 2 and sys.argv[2] == "now":
        designs = [("전체팩(지금)", lambda c, y, t: True, ALL), ("고급팩(지금)", lambda c, y, t: True, HIGH)]
    for name, (year, team, bats, pits) in DECKS.items():
        need = {by[("B", year, team, n)]["idx"] for n in bats.split(",")} | {by[("P", year, team, n)]["idx"] for n in pits.split(",")}
        extra = {c["idx"] for c in cards if c["kind"] == "B" and c["year"] == year and c["team"] == team} - need
        tb = sorted((by_c["ball"] for by_c in cards if by_c["idx"] in need), reverse=True)
        print(f"== {name}: 주전 20장 볼 {tb}, 남는 타자 {len(extra)}명")
        for dn, ok, odds in designs:
            pool = lambda c, ok=ok: ok(c, year, team)
            if min(odds.index(x) for x in odds if x > 0) + 1 > min(tb):
                # a 4~10볼 pack cannot give the deck's lower cards: only the 8볼+ part is meaningful
                res = [simulate(cards, pool, odds, {i for i in need if next(c for c in cards if c["idx"] == i)["ball"] >= 4}, extra, rnd) for _ in range(runs)]
                note = " (4볼 이상 주전만; 3볼 이하와 후보는 다른 팩으로)"
            else:
                res = [simulate(cards, pool, odds, need, extra, rnd) for _ in range(runs)]
                note = ""
            hi_m, all_m = statistics.mean(r[0] for r in res), statistics.mean(r[1] for r in res)
            print(f"  {dn:12} 8볼 이상 주전까지 {hi_m:7.0f}팩, 전부 {all_m:7.0f}팩 (중앙 {statistics.median(r[1] for r in res):.0f}){note}")


if __name__ == "__main__":
    main()
