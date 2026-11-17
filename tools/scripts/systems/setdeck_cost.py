"""세트덱(팀컬러) 비용 몬테카를로 (systems session, 2026-11-17).

User 11-17: "지금 단일 년도 팀 세트덱, 듀얼 년도 팀 세트덱, 연대 팀올스타 세트덱, 팀 올스타 세트덱 등을 만들 때 평균적으로 얼마의
MP가 소모되는지 계산할 수 있나?? 이 부분도 굉장히 중요할 것 같음." A self-sufficient player climbs e.g. 삼성 올스타 → 2010년대 삼성
→ 2011·2015 삼성 → 2015 삼성; how much MP does each step take on average?

Read-only: reads server\\data\\tables\\P_BatterTable.txt and P_PItcherTable.txt (no DB, no server).

Model (the server code as of 2026-11-17):
- Packs (Shop/ShopHandlers.BuildProducts, Inventory/CardPacks): 전체 5만 1~10볼, 고급 10만 4~10볼, 1990/2000/2010/2020년대
  6만 (1993-1999, 2000-2009, 2010-2019, 2020-2029), 동군·서군 5만 (by card team code). 연대·동서군 use the 전체 odds.
- A pack draws a ball from the odds, then a random card of that ball in the pack's pool the team does not own; when that ball
  has none free, the nearest ball (b, b-1, b+1, b-2, b+2 ...) inside the pack's range (CardPacks.Open / ShopHandlers.Draw).
  레전드 and FA cards are never drawn (CardCatalog.Drawable).
- The player keeps every card that is a member of the target (so it never comes again). Two policies for the other cards:
  * 보관 (main, pack by pack): held too while 1군 25 + members + others fit the 보관함 (--storage, default 1,400 =
    ShopHandlers.MaxCapacity), so they cannot come again either; when full, 200 are released (first those outside the pack's
    pool or of a ball with no member left to find, then the lowest balls). The 보관함 (80 → 1,400: 6,320만) is a one-time buy
    shown apart. Held cards count as released when a grade is recorded.
  * 바로 방출 (event-driven, fast): every other card released at once; a released card can come again. Here the 보관함
    expansions for the members beyond 50 free slots are included.
  Release refund = ball x 5,000 MP (ContractHandlers.RefundPerBall).
- Grade = the C slot (1군 25) of TeamColors: members placed >= 11/14/19/25 for C/B/A/S (연대 단일 팀 14/19/22/25,
  DecadeNeeded); 단일 팀 올스타's C slot has no C grade (Value rounds to 0). Members (TeamColors.IsMember): 단일 년도 팀 = the
  card's own club code and season; 듀얼 년도 = the lineage in either season; 연대 = the lineage in the decade; 올스타 = the lineage.
- Placement: one card per player (IsBatter + sidx) in the 1군 (RosterHandlers.FaProblem); a fielder only where he can field
  (Defence > 20, CardInfo.CanField) at C 1B 2B 3B SS LF CF RF, the DH and 5 bench any batter; pitchers in role (AutoLineup
  포지션 엄수): SP x5, RP x4, SU x1, CP x1, a pitcher also in the roles ma9m_pitcher_roles.txt allows him (포지션 변경,
  PositionChangeHandlers.Fee 40만 once, no failure; counted as placed pitchers beyond their own roles' matching x 40만).
  --no-poschange: own roles only. --relaxed: the 6 bullpen slots take any RP/SU/CP (out of role, -15%).
- "FA 2장" rows: the grade with two FA jokers of the right season filling the last two slots (TeamColors FA rule, 1군 up to 2
  FA since 11-17): members placed >= need - 2, plus 2 FA signings (8볼 1,500만, 9볼 3,000만) not in the pack MP.
- Engine: event-driven (between two member hits the state does not change, so the packs in between are geometric and their
  refunds are counted at the mean); --check compares it with a pack-by-pack run.

Usage (python -I, Normal priority, one process):
  python -I tools\\scripts\\systems\\setdeck_cost.py                  report (보관 500 trials, 바로 방출 1,000), ~4-5 min
  python -I tools\\scripts\\systems\\setdeck_cost.py --ptrials 200 --trials 300 --seed 2 --income 5780000 --storage 700
  python -I tools\\scripts\\systems\\setdeck_cost.py --explore        pool sizes, members by role, S 필수 카드, hit rates
  python -I tools\\scripts\\systems\\setdeck_cost.py --check          event-driven vs pack-by-pack (same target)
  python -I tools\\scripts\\systems\\setdeck_cost.py --storagecompare 2015 삼성 by 보관함 size (0 … 1,400)
  python -I tools\\scripts\\systems\\setdeck_cost.py --relaxed        bullpen roles interchangeable
  python -I tools\\scripts\\systems\\setdeck_cost.py --no-poschange   no 포지션 변경
  python -I tools\\scripts\\systems\\setdeck_cost.py --only 2015      only the targets whose name has "2015" (no ladder)
Income default 578만 a day = autotest7 (self-sufficient 삼성) 11-15~17 ledger: 리그 296만 + 시범경기 216만 + 미션 262.5만 +
시즌 보상 959만 (one 2.5-day cycle, 1위 + 시상) over 3 days; 업적 and the FA signing left out (mp_ledger.py).

First run 2026-11-17: 2015 삼성 바로 방출 C 4,410만 / B 6,442만 / A 1.23억 / S 5.04억 (12,000 2010년대팩); with a full
1,400 보관함 S ≈ 1.97억 (4,700 packs) + the 6,320만 보관함 (--storagecompare). A single team-year's S needs nearly all of its
~30-34 cards, and one card of a crowded ball is rare (2010년대팩: a given 5볼 1 in 7,409 packs, 8볼 1 in 3,447, 9볼 1 in
14,900, 10볼 1 in 6,667), which is why holding the other cards (they cannot come again) pays.
"""
import argparse
import csv
import io
import math
import random
import statistics
import sys

TABLES = r"C:\Ma9MProject\server\data\tables"

ALL_ODDS = [20, 18, 15, 13, 11, 9.6, 7.4, 4.7, 1.0, 0.3]       # CardPacks.AllOdds (11-11)
HIGH_ODDS = [0, 0, 0, 29, 25, 20, 13.5, 9, 2.8, 0.7]          # 고급 (11-11)
EAST = {9, 7, 8, 0, 10, 19, 11}                               # ShopHandlers.EastClubs
WEST = {5, 1, 6, 2, 14, 3, 13, 4, 15}                         # ShopHandlers.WestClubs
REFUND_PER_BALL = 5_000
FREE_SLOTS = 50                                               # storage 80 - the 1군 and a few others
FA_COST = (15_000_000, 30_000_000)                            # 8볼, 9볼 FA signing
POS_FEE = 400_000                                             # PositionChangeHandlers.Fee

PACKS = {
    "전체": (50_000, ALL_ODDS, lambda c: True),
    "고급": (100_000, HIGH_ODDS, lambda c: True),
    "1990년대": (60_000, ALL_ODDS, lambda c: 1993 <= c.year <= 1999),
    "2000년대": (60_000, ALL_ODDS, lambda c: 2000 <= c.year <= 2009),
    "2010년대": (60_000, ALL_ODDS, lambda c: 2010 <= c.year <= 2019),
    "2020년대": (60_000, ALL_ODDS, lambda c: 2020 <= c.year <= 2029),
    "동군": (50_000, ALL_ODDS, lambda c: c.team in EAST),
    "서군": (50_000, ALL_ODDS, lambda c: c.team in WEST),
}

GRADES = ["C", "B", "A", "S"]
NEED = {"normal": [11, 14, 19, 25], "decade": [14, 19, 22, 25]}   # C slot, TeamColors.Needed[0] / DecadeNeeded[0]
# TeamColors.Kinds (h, p, c) and shares by grade C, B, A, S; Value = floor(s x share + 0.5).
KIND_HPC = {"year": (6, 6, 15), "dualyear": (4, 4, 14), "decade": (3, 3, 7), "allstar": (1, 1, 2)}
SHARE = {"normal": [0.15, 0.25, 0.45, 1.0], "decade": [0.3, 0.5, 0.7, 1.0]}
KIND_NAME = {"year": "단일 년도 팀", "dualyear": "듀얼 년도 단일 팀", "decade": "연대 단일 팀", "allstar": "단일 팀 올스타"}

POS_SP, POS_RP, POS_SU, POS_CP = 12, 13, 14, 15


def lineage(code):
    return {1: 5, 10: 0, 14: 2, 16: 6, 15: 4, 17: 4, 18: 4}.get(code, code)


class Card:
    __slots__ = ("idx", "bat", "year", "team", "line", "ball", "pos", "sidx", "mask", "own", "name")


def load(tables=TABLES, poschange=False):
    """poschange: a pitcher may also fill the roles ma9m_pitcher_roles.txt allows him (포지션 변경, 40만 once, no failure)."""
    allowed = {}
    if poschange:
        role_bit = {POS_SP: 0, POS_RP: 1, POS_SU: 2, POS_CP: 3}
        with open(f"{tables}\\ma9m_pitcher_roles.txt", encoding="utf-8") as f:
            rows = csv.reader(f, delimiter="\t")
            next(rows)
            for r in rows:
                if len(r) >= 3 and r[2].strip():
                    allowed[int(r[0])] = sum(1 << role_bit[int(x)] for x in r[2].split(",") if int(x) in role_bit)
    cards = []
    for fn, bat in (("P_BatterTable.txt", True), ("P_PItcherTable.txt", False)):
        with open(f"{tables}\\{fn}", encoding="utf-8") as f:
            rows = csv.reader(f, delimiter="\t")
            next(rows)
            for r in rows:
                if len(r) < 30:
                    continue
                idx, grade, team = int(r[0]), int(r[13]), int(r[6])
                if grade == 5 or 800101 <= idx <= 800299 or team == 250:
                    continue   # 레전드, FA: not drawable
                c = Card()
                c.idx, c.bat, c.year, c.team, c.line = idx, bat, int(r[1]), team, lineage(team)
                c.ball, c.pos, c.sidx, c.name = int(r[8]), int(r[9]), int(r[12]), r[3]
                if bat:
                    c.mask = sum(1 << p for p in range(8) if int(r[21 + p]) > 20)   # def_C .. def_RF > 20
                else:
                    c.own = 1 << {POS_SP: 0, POS_RP: 1, POS_SU: 2, POS_CP: 3}.get(c.pos, 1)
                    c.mask = c.own | allowed.get(idx, 0)
                if bat:
                    c.own = c.mask
                cards.append(c)
    return cards


class Target:
    def __init__(self, label, kind, line=None, team=None, years=None, decade=None):
        self.label, self.kind, self.line, self.team, self.years, self.decade = label, kind, line, team, years, decade
        self.need = NEED["decade" if kind == "decade" else "normal"]
        hpc, share = KIND_HPC[kind], SHARE["decade" if kind == "decade" else "normal"]
        self.value = [math.floor(hpc[2] * s + 0.5) for s in share]          # C slot bonus by grade C..S
        self.bonus_s = hpc[0] + hpc[2]                                     # a batter's bonus at S (H + C)

    def member(self, c):
        if self.kind == "year":
            return c.team == self.team and c.year == self.years[0]
        if self.kind == "dualyear":
            return c.line == self.line and c.year in self.years
        if self.kind == "decade":
            return c.line == self.line and c.year // 10 == self.decade
        return c.line == self.line

    def available(self, g):
        return self.value[g] > 0


def max_match(slot_masks, player_masks):
    """Kuhn: slots (accept masks) to players (masks), each once."""
    owner = [-1] * len(player_masks)

    def try_slot(s, seen):
        for p, m in enumerate(player_masks):
            if m & slot_masks[s] and not seen[p]:
                seen[p] = True
                if owner[p] < 0 or try_slot(owner[p], seen):
                    owner[p] = s
                    return True
        return False

    n = 0
    for s in range(len(slot_masks)):
        if try_slot(s, [False] * len(player_masks)):
            n += 1
    return n


class Deck:
    """The members a team holds for one target, and how many of them fit a legal 1군."""
    FIELD = [1 << p for p in range(8)]

    def __init__(self, relaxed):
        self.bat, self.pit, self.pit_own = {}, {}, {}
        self.pslots = [1] * 5 + ([2 | 4 | 8] * 6 if relaxed else [2] * 4 + [4] + [8])
        self.bplaced = self.pplaced = self.pown = 0

    def add(self, c):
        d = self.bat if c.bat else self.pit
        old = d.get(c.sidx, 0)
        if old | c.mask == old and c.sidx in d:
            return False
        d[c.sidx] = old | c.mask
        if c.bat and self.bplaced < 14:
            m = max_match(self.FIELD, list(self.bat.values()))
            self.bplaced = min(len(self.bat), m + 6)
        elif not c.bat and self.pplaced < 11:
            self.pplaced = max_match(self.pslots, list(self.pit.values()))
        if not c.bat:
            self.pit_own[c.sidx] = self.pit_own.get(c.sidx, 0) | c.own
            if self.pown < 11:
                self.pown = max_match(self.pslots, list(self.pit_own.values()))
        return True

    @property
    def changes(self):
        """포지션 변경 needed: pitchers placed beyond what their own roles place (40만 each)."""
        return max(0, self.pplaced - self.pown)

    @property
    def placed(self):
        return self.bplaced + self.pplaced


def storage_cost(held):
    """MP for the 보관함 expansions to hold `held` members on top of FREE_SLOTS."""
    buys = max(0, math.ceil((held - FREE_SLOTS) / 20))
    return sum(min(300_000 + 100_000 * n, 1_000_000) for n in range(buys))


class Run:
    """One trial: steps = [(pack, stop_target_index, stop_grade)], keep = which cards are held, targets recorded."""

    def __init__(self, cards, targets, keep, relaxed, fa_slack=0):
        self.cards, self.targets, self.keep = cards, targets, keep
        self.decks = [Deck(relaxed) for _ in targets]
        self.fa = fa_slack
        self.owned = set()
        self.packs = 0
        self.gross = 0.0
        self.refund = 0.0
        self.held = 0
        # other cards held (storage policy, play_packwise); their refund counts as if released when a grade is recorded
        self.junk, self.junk_ids, self.junk_value = [], set(), 0.0
        self.count_storage = True   # False: the 보관함 is already bought (storage policy), not charged per trial
        # first[t][g] = (packs, gross, refund, held, fee) when target t first reaches grade g (or None)
        self.first = [[None] * 4 for _ in targets]

    def grade_of(self, t):
        placed = self.decks[t].placed + self.fa
        tg = self.targets[t]
        best = -1
        for g in range(4):
            if placed >= tg.need[g] and tg.available(g):
                best = g
        return best

    def note(self, t):
        g = self.grade_of(t)
        for k in range(g + 1):
            if self.first[t][k] is None:
                self.first[t][k] = (self.packs, self.gross, self.refund + self.junk_value,
                                    self.held if self.count_storage else 0, self.decks[t].changes * POS_FEE)

    def take(self, c):
        self.owned.add(c.idx)
        self.held += 1
        for t, tg in enumerate(self.targets):
            if tg.member(c) and self.decks[t].add(c):
                self.note(t)

    def done(self, t, g):
        return self.first[t][g] is not None

    def play(self, steps, rng, pack_cache):
        for pack, stop_t, stop_g in steps:
            if self.done(stop_t, stop_g):
                continue
            price, odds, pool_by_ball = pack_cache[pack]
            lo = next(i for i, x in enumerate(odds) if x > 0) + 1
            hi = max(i for i, x in enumerate(odds) if x > 0) + 1
            total = sum(odds)
            n = {b: sum(1 for c in pool_by_ball.get(b, ()) if c.idx not in self.owned) for b in range(1, 11)}
            kf = {b: [c for c in pool_by_ball.get(b, ()) if self.keep(c) and c.idx not in self.owned] for b in range(1, 11)}
            while not self.done(stop_t, stop_g):
                # each drawn ball's effective ball (nearest with a free card inside the range)
                hit_w, non_r, p_hit = {}, 0.0, 0.0
                for d in range(1, 11):
                    q = odds[d - 1] / total
                    if q <= 0:
                        continue
                    e = None
                    for s in range(10):
                        for b in (d - s, d + s):
                            if lo <= b <= hi and n[b] > 0:
                                e = b
                                break
                        if e is not None:
                            break
                    if e is None:
                        continue
                    share = len(kf[e]) / n[e]
                    hit_w[e] = hit_w.get(e, 0.0) + q * share
                    p_hit += q * share
                    non_r += q * (1 - share) * REFUND_PER_BALL * e
                if p_hit <= 0:
                    return False   # nothing of the target left in this pack
                u = rng.random()
                k = 1 if p_hit >= 1 else int(math.log(1 - u) / math.log(1 - p_hit)) + 1
                self.packs += k
                self.gross += k * price
                if p_hit < 1:
                    self.refund += (k - 1) * non_r / (1 - p_hit)
                x = rng.random() * p_hit
                for e, w in hit_w.items():
                    x -= w
                    if x < 0:
                        break
                lst = kf[e]
                j = rng.randrange(len(lst))
                lst[j], lst[-1] = lst[-1], lst[j]
                c = lst.pop()
                n[e] -= 1
                self.take(c)
        return True

    def play_packwise(self, steps, rng, pack_cache, storage=0):
        """The same, one pack at a time. storage = 보관함 size: 0 releases every other card at once; with a size the team
        also holds other cards (so they cannot come again) while 1군 25 + members + others fit, then releases 200: first those
        outside the pack's pool or of a ball with no member left to find, then the lowest balls."""
        if storage:
            self.count_storage = False
        for pack, stop_t, stop_g in steps:
            if self.done(stop_t, stop_g):
                continue
            price, odds, pool_by_ball = pack_cache[pack]
            lo = next(i for i, x in enumerate(odds) if x > 0) + 1
            hi = max(i for i, x in enumerate(odds) if x > 0) + 1
            in_pool = {c.idx for v in pool_by_ball.values() for c in v}
            free = {b: [c for c in pool_by_ball.get(b, ()) if c.idx not in self.owned and c.idx not in self.junk_ids] for b in range(1, 11)}
            left = {b: sum(1 for c in free[b] if self.keep(c)) for b in range(1, 11)}
            balls = list(range(1, 11))
            cum, acc = [], 0.0
            for x in odds:
                acc += x
                cum.append(acc)
            while not self.done(stop_t, stop_g):
                if not any(free[b] for b in range(lo, hi + 1)):
                    return False
                d = rng.choices(balls, cum_weights=cum)[0]
                c = None
                for s in range(10):
                    for b in (d - s, d + s):
                        if lo <= b <= hi and free[b]:
                            lst = free[b]
                            j = rng.randrange(len(lst))
                            lst[j], lst[-1] = lst[-1], lst[j]
                            c = lst.pop()
                            break
                    if c is not None:
                        break
                self.packs += 1
                self.gross += price
                if self.keep(c):
                    left[c.ball] -= 1
                    self.take(c)
                elif storage > 0:
                    self.junk.append(c)
                    self.junk_ids.add(c.idx)
                    self.junk_value += REFUND_PER_BALL * c.ball
                    if 25 + self.held + len(self.junk) > storage:
                        self.junk.sort(key=lambda o: (o.idx in in_pool and left[o.ball] > 0, o.ball))
                        for o in self.junk[:200]:
                            self.junk_ids.discard(o.idx)
                            self.junk_value -= REFUND_PER_BALL * o.ball
                            self.refund += REFUND_PER_BALL * o.ball
                            if o.idx in in_pool:
                                free[o.ball].append(o)
                        del self.junk[:200]
                else:
                    free[c.ball].append(c)
                    self.refund += REFUND_PER_BALL * c.ball
        return True


def pack_cache_for(cards):
    cache = {}
    for name, (price, odds, filt) in PACKS.items():
        by = {}
        for c in cards:
            if filt(c):
                by.setdefault(c.ball, []).append(c)
        cache[name] = (price, odds, by)
    return cache


def initial_rate(target, pack_cache, name):
    """Members per pack at the start and the net MP per pack (price - mean refund of a non-member)."""
    price, odds, by = pack_cache[name]
    total = sum(odds)
    p = r = 0.0
    lo = next(i for i, x in enumerate(odds) if x > 0) + 1
    hi = max(i for i, x in enumerate(odds) if x > 0) + 1
    for d in range(1, 11):
        q = odds[d - 1] / total
        if q <= 0:
            continue
        e = next((b for s in range(10) for b in (d - s, d + s) if lo <= b <= hi and by.get(b)), None)
        if e is None:
            continue
        m = sum(1 for c in by[e] if target.member(c))
        p += q * m / len(by[e])
        r += q * (1 - m / len(by[e])) * REFUND_PER_BALL * e
    return p, price - r


def won(x):
    if x is None:
        return "-"
    if abs(x) >= 1e8:
        return f"{x / 1e8:.2f}억"
    return f"{x / 1e4:,.0f}만"


def quant(xs):
    xs = sorted(xs)
    if len(xs) < 10:
        return xs[0], xs[-1]
    q = statistics.quantiles(xs, n=10)
    return q[0], q[8]


def summarize(runs, t, g, income):
    got = [r.first[t][g] for r in runs if r.first[t][g] is not None]
    if not got:
        return None
    net = [gr - rf + storage_cost(h) + fee for (_, gr, rf, h, fee) in got]
    packs = [x[0] for x in got]
    q10, q90 = quant(net)
    return {"share": len(got) / len(runs), "mean": statistics.mean(net), "q10": q10, "q90": q90,
            "packs": statistics.mean(packs), "days": statistics.mean(net) / income,
            "store": statistics.mean(storage_cost(x[3]) for x in got), "fee": statistics.mean(x[4] for x in got),
            "refund": statistics.mean(x[2] for x in got), "gross": statistics.mean(x[1] for x in got)}


def simulate(cards, cache, targets, steps, keep, trials, seed, relaxed, fa_slack=0, storage=0):
    """storage 0: every other card released at once (event-driven, fast); else held up to that 보관함 (pack by pack)."""
    rng = random.Random(seed)
    runs = []
    for _ in range(trials):
        r = Run(cards, targets, keep, relaxed, fa_slack)
        if storage:
            r.play_packwise(steps, rng, cache, storage=storage)
        else:
            r.play(steps, rng, cache)
        runs.append(r)
    return runs


MAX_STORAGE = 1400   # ShopHandlers.MaxCapacity


def full_storage_cost(size=MAX_STORAGE):
    """MP from the 80 starting slots to `size` (+20 a buy, 30만 + 10만 x bought, 100만 cap)."""
    return sum(min(300_000 + 100_000 * n, 1_000_000) for n in range(math.ceil((size - 80) / 20)))


SAMSUNG = [
    Target("삼성 올스타", "allstar", line=8),
    Target("2010년대 삼성", "decade", line=8, decade=201),
    Target("2011·2015 삼성", "dualyear", line=8, years=(2011, 2015)),
    Target("2015 삼성", "year", team=8, years=(2015,)),
]
HAITAI = [
    Target("KIA·해태 올스타", "allstar", line=5),
    Target("1990년대 해태", "decade", line=5, decade=199),
    Target("1996·1997 해태", "dualyear", line=5, years=(1996, 1997)),
    Target("1997 해태", "year", team=1, years=(1997,)),
]
LADDERS = [("삼성", SAMSUNG, ["동군", "2010년대", "2010년대", "2010년대"], 8),
           ("해태", HAITAI, ["서군", "1990년대", "1990년대", "1990년대"], 5)]


def explore(cards, cache):
    print(f"뽑을 수 있는 카드 {len(cards):,}장 (레전드·FA 제외)")
    for name, (price, odds, by) in cache.items():
        print(f"  {name}팩 {price:,} MP: {sum(len(v) for v in by.values()):,}장 · 볼별 " + " ".join(f"{b}:{len(by.get(b, []))}" for b in range(1, 11)))
    for _, targets, _, _ in LADDERS:
        for t in targets:
            mem = [c for c in cards if t.member(c)]
            bats = [c for c in mem if c.bat]
            pits = [c for c in mem if not c.bat]
            roles = {k: len({c.sidx for c in pits if c.pos == v}) for k, v in (("SP", POS_SP), ("RP", POS_RP), ("SU", POS_SU), ("CP", POS_CP))}
            field = [len({c.sidx for c in bats if c.mask >> p & 1}) for p in range(8)]
            full = Deck(False)
            for c in mem:
                full.add(c)
            relaxed = Deck(True)
            for c in mem:
                relaxed.add(c)
            print(f"\n{t.label} ({KIND_NAME[t.kind]}): 카드 {len(mem)}장 (타자 {len(bats)}·투수 {len(pits)}), 선수 타자 {len({c.sidx for c in bats})}·투수 {len({c.sidx for c in pits})}, "
                  f"투수 보직 {roles}, 수비 가능 C 1B 2B 3B SS LF CF RF = {field}, 전부 모으면 {full.placed}명 (불펜 섞기 {relaxed.placed}명), "
                  f"볼별 " + " ".join(f"{b}:{sum(1 for c in mem if c.ball == b)}" for b in range(1, 11)))
            print(f"  C칸 보너스 C/B/A/S = {t.value}, 필요 인원 {t.need}")
            rates = sorted(((initial_rate(t, cache, name), name) for name in cache), key=lambda x: -x[0][0] / x[0][1] if x[0][1] else 0)
            print("  팩별 첫 팩 적중률 (팩당 대상 카드, 100만 MP당 대상 카드): " + ", ".join(
                f"{name} {p * 100:.2f}% · {p / net * 1e6:.2f}" for (p, net), name in rates if p > 0))
            # 필수 카드: without it the whole set cannot fill 25 (S); one card's chance a pack in the best pack.
            best = rates[0][1]
            _, odds, by = cache[best]
            must = []
            for c in mem:
                d = Deck(False)
                for o in mem:
                    if o is not c:
                        d.add(o)
                if d.placed < 25:
                    p1 = odds[c.ball - 1] / sum(odds) / len(by[c.ball])
                    must.append((1 / p1, c))
            must.sort(key=lambda x: -x[0])
            print(f"  S 필수 카드 {len(must)}장 ({best}팩에서 한 장 나올 평균 팩 수): " + ", ".join(
                f"{c.year % 100:02d} {c.name} {c.ball}볼 {'타' if c.bat else {POS_SP: 'SP', POS_RP: 'RP', POS_SU: 'SU', POS_CP: 'CP'}[c.pos]} {k:,.0f}" for k, c in must[:12]))


def best_packs(t, cache, k=2):
    rates = [(initial_rate(t, cache, name), name) for name in cache]
    rates = [(p / net, name) for (p, net), name in rates if p > 0]
    rates.sort(reverse=True)
    return [name for _, name in rates[:k]]


def table_rows(runs, t, income, other=None):
    """Rows for grades C..S; `other` = runs of the other policy (its mean in the last column)."""
    rows = []
    for g in range(4):
        tg = runs[0].targets[t]
        if not tg.available(g):
            rows.append(f"| {GRADES[g]} | 없음 (보너스 0) | | | | |")
            continue
        s = summarize(runs, t, g, income)
        if s is None:
            rows.append(f"| {GRADES[g]} | 못 만듦 | | | | |")
            continue
        note = "" if s["share"] >= 0.999 else f" ({s['share'] * 100:.0f}%만 됨)"
        o = summarize(other, t, g, income) if other else None
        rows.append(f"| {GRADES[g]} (+{tg.value[g]}) | {won(s['mean'])}{note} | {won(s['q10'])}~{won(s['q90'])} | {s['packs']:,.0f} | "
                    f"{s['days']:.1f}일 | {won(o['mean']) if o else '-'} |")
    return rows


ONLY = ""
POSCHANGE = False


def report(cards, cache, trials, seed, relaxed, income, ptrials, storage):
    rule = "불펜 보직 섞기 허용" if relaxed else "포지션·보직 엄수" + (" + 투수 포지션 변경(40만)" if POSCHANGE else "")
    print(f"# 세트덱 비용 (시행 보관 {ptrials:,}번·바로 방출 {trials:,}번, 시드 {seed}, {rule}, 하루 수입 {won(income)})\n")
    print(f"평균 MP = 팩값 - 방출 환급(볼 x 5,000) + 포지션 변경. 주 전략: 대상이 아닌 카드도 보관함 {storage:,}칸이 찰 때까지 들고 있다가 "
          f"(같은 카드가 다시 안 나옴) 가득 차면 200장씩 방출; 보관함 확장 {won(full_storage_cost(storage))}(80칸부터)은 한 번 사 두는 값이라 따로. "
          "'바로 방출' = 대상이 아닌 카드를 곧바로 방출(보관함 확장은 대상 카드 몫만 포함). 등급은 1군 전체(C칸) 기준, ( ) 안은 C칸 보너스.\n")
    for lname, targets, packs, line in LADDERS:
        for ti, t in enumerate(targets):
            if ONLY and ONLY not in t.label:
                continue
            cands = best_packs(t, cache, 2)
            results = []
            for pk in cands:
                runs = simulate(cards, cache, [t], [(pk, 0, 3)], t.member, trials, seed, relaxed)
                s = summarize(runs, 0, 3, income) or summarize(runs, 0, 2, income)
                results.append(((s["mean"] if s else float("inf")), pk, runs))
            results.sort(key=lambda x: x[0])
            _, pk, quick = results[0]
            other = ", ".join(f"{p} {won(m)}" for m, p, _ in results[1:])
            runs = simulate(cards, cache, [t], [(pk, 0, 3)], t.member, ptrials, seed + 3, relaxed, storage=storage)
            print(f"## {t.label} ({KIND_NAME[t.kind]}) · {pk}팩{' (바로 방출 S로 비교한 다음 팩: ' + other + ')' if other else ''}\n")
            print("| 등급 | 평균 MP | 10~90% | 팩 수 | 며칠 | 바로 방출하면 |")
            print("|---|---:|---:|---:|---:|---:|")
            for row in table_rows(runs, 0, income, quick):
                print(row)
            fa_runs = simulate(cards, cache, [t], [(pk, 0, 3)], t.member, ptrials, seed + 1, relaxed, fa_slack=2, storage=storage)
            s = summarize(fa_runs, 0, 3, income)
            fq = summarize(simulate(cards, cache, [t], [(pk, 0, 3)], t.member, trials, seed + 1, relaxed, fa_slack=2), 0, 3, income)
            if s:
                print(f"| S, FA 2장 | {won(s['mean'])} + FA {won(2 * FA_COST[0])}~{won(2 * FA_COST[1])} | {won(s['q10'])}~{won(s['q90'])} | "
                      f"{s['packs']:,.0f} | {s['days']:.1f}일 + FA | {won(fq['mean']) if fq else '-'} |")
            s3 = summarize(runs, 0, 3, income)
            if s3:
                print(f"\nS: 팩값 {won(s3['gross'])}, 환급 {won(s3['refund'])} ({s3['refund'] / s3['gross'] * 100:.0f}%), 포지션 변경 {won(s3['fee'])}")
            print()
        if ONLY:
            continue
        # the ladder: keep the whole lineage, each step's pack until its S
        steps = [(packs[i], i, 3) for i in range(len(targets))]
        runs = simulate(cards, cache, targets, steps, lambda c, line=line: c.line == line, ptrials, seed + 7, relaxed, storage=storage)
        print(f"## 사다리 ({' → '.join(t.label for t in targets)}; 팩 {' → '.join(packs)}, 계보 카드는 모두 보관, 단계마다 S가 되면 다음 단계로)\n")
        print("| 단계 | C | B | A | S | S까지 그 단계 몫 |")
        print("|---|---:|---:|---:|---:|---:|")
        prev = 0.0
        for ti, t in enumerate(targets):
            cells = []
            for g in range(4):
                s = summarize(runs, ti, g, income) if t.available(g) else None
                cells.append("-" if s is None else won(s["mean"]))
            s = summarize(runs, ti, 3, income)
            step = (s["mean"] - prev) if s else None
            print(f"| {t.label} | " + " | ".join(cells) + f" | {won(step)}{'' if step is None else f' ({step / income:.1f}일)'} |")
            if s:
                prev = s["mean"]
        print()


def check(cards, cache, trials, seed, relaxed):
    t = SAMSUNG[3]
    a = simulate(cards, cache, [t], [("2010년대", 0, 3)], t.member, trials, seed, relaxed)
    rng = random.Random(seed + 99)
    b = []
    for _ in range(trials):
        r = Run(cards, [t], t.member, relaxed)
        r.play_packwise([("2010년대", 0, 3)], rng, cache)
        b.append(r)
    for g in range(4):
        sa, sb = summarize(a, 0, g, 1), summarize(b, 0, g, 1)
        print(f"{t.label} {GRADES[g]}: 사건 단위 {sa['packs']:,.0f}팩 {won(sa['mean'])} / 팩 단위 {sb['packs']:,.0f}팩 {won(sb['mean'])}")


def storage_compare(cards, cache, trials, seed, relaxed):
    """2015 삼성 by 보관함 size: how much holding other cards saves, against what the slots cost."""
    t = SAMSUNG[3]
    for size in (0, 200, 400, 700, 1000, MAX_STORAGE):
        runs = simulate(cards, cache, [t], [("2010년대", 0, 3)], t.member, trials, seed, relaxed, storage=size)
        cells = []
        for g in range(4):
            s = summarize(runs, 0, g, 1)
            cells.append(f"{GRADES[g]} {s['packs']:,.0f}팩 {won(s['mean'])}")
        print(f"보관함 {size or '바로 방출'}{'칸 (확장 ' + won(full_storage_cost(size)) + ')' if size else ''}: " + ", ".join(cells))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--trials", type=int, default=1000)
    ap.add_argument("--seed", type=int, default=20261117)
    ap.add_argument("--income", type=float, default=5_780_000, help="MP a day for packs (autotest7 11-15~17 ledger)")
    ap.add_argument("--relaxed", action="store_true")
    ap.add_argument("--no-poschange", dest="poschange", action="store_false",
                    help="pitchers only in their card's own role (default: also the roles 포지션 변경 allows, 40만 each)")
    ap.add_argument("--only", default="", help="only targets whose label contains this (e.g. 2015)")
    ap.add_argument("--explore", action="store_true")
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--storage", type=int, default=MAX_STORAGE, help="보관함 size for the main (holding) policy")
    ap.add_argument("--ptrials", type=int, default=500, help="trials of the pack-by-pack (holding) runs")
    ap.add_argument("--storagecompare", action="store_true", help="2015 삼성 by 보관함 size")
    ap.add_argument("--tables", default=TABLES)
    a = ap.parse_args()
    sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8")
    cards = load(a.tables, a.poschange)
    cache = pack_cache_for(cards)
    global ONLY, POSCHANGE
    ONLY, POSCHANGE = a.only, a.poschange
    if a.explore:
        explore(cards, cache)
    elif a.check:
        check(cards, cache, a.trials, a.seed, a.relaxed)
    elif a.storagecompare:
        storage_compare(cards, cache, a.ptrials, a.seed, a.relaxed)
    else:
        report(cards, cache, a.trials, a.seed, a.relaxed, a.income, a.ptrials, a.storage)


if __name__ == "__main__":
    main()
