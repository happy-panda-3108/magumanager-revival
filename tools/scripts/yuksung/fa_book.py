"""2026-11-15 (user 13:2x, FA 선수카드: "FA 선수들이 구현되면, 현재 있는 육성표를 기준으로 각 팀마다 어떤 FA 선수를 사용하는게 승률이 가장
크게 오르는지, 2순위는 어떤 선수인지 등 분석해서 육성표에 추가해주길 바람"): the FA run's log (gen_fa_env.py screen → run_entry.ps1)
into an "FA 추천" sheet per book: book_edit.ps1 specs that copy each of the users' latest 한셀 books to new files (the users' own
files stay) with the sheet added after the last one, plus a text summary.
  python fa_book.py <run log> <spec dir> <book out dir> [top]   → spec_<tag>.json per deck, summary.txt
2026-11-16 (user 00:3x, 연쇄): a TCSIM_FA_CHAIN=1 run (fa_chain_env.txt) also has "[연쇄]" variants (keys "<fa>@SP5>RP3", "<fa>@SS>3B+DH");
the ranking shows each FA once (his best by games) and a "연쇄로 바뀐 것" block pairs an FA's 11-15 style and chain variants.
YUKSUNG_DATE (file names, default 1115) and YUKSUNG_NOTE (sheet title) name the run."""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from gen_fa_env import DECKS, BASE  # noqa: E402

# Owner and league from the 11-16 season (seven tiers: 메이저 150 · 레전드 155 · 챔피언스 160), as the DB stood on 11-15.
OWNERS = {
    "2025 LG": ("SKY해민", "챔피언스", 160), "2001 롯데": ("SKY마토", "챔피언스", 160), "1997 쌍방울": ("최약체구단S", "챔피언스", 160),
    "2024 롯데": ("SKY준우", "메이저", 150), "2012 삼성": ("SKY승환", "레전드", 155), "2001 현대": ("행복한현대", "레전드", 155),
}
PRICE = {8: "1,500만", 9: "3,000만"}
LINE = re.compile(r"tcsim entry: (?P<deck>.+?)#" + BASE + r"(?:#FA (?P<key>\S+))? \(모든 카드 육성 \+\d+\) (?P<balls>\d+)볼.*?"
                  r"리그 평균 (?P<win>[\d.]+)% \(득 (?P<rf>[\d.]+) 실 (?P<ra>[\d.]+), \d+덱\) \| (?P<rest>.*)$")
FA = re.compile(r"FA (?P<yy>\d\d) (?P<name>\S+) (?P<ball>\d+)볼 \((?P<club>[^)]+)\) → (?P<what>.+?) \[선별 (?P<screen>[-+][\d.]+)점\](?P<chain> \[연쇄\])?(?: 육성 (?P<dev>.+))?$")
# 11-16 run (TCSIM_FA_CHAIN=1, user 00:3x): the displaced man may move on and push out another; the files and titles of that run.
DATE = os.environ.get("YUKSUNG_DATE", "1115")
TITLE = os.environ.get("YUKSUNG_NOTE", "11-15 엔진 분석")
# 2026-11-17 (user 07:3x, 1군 FA 2명): YUKSUNG_PAIRS=<the TCSIM_FA_PAIRS run's log> adds an "FA 2명 추천" sheet after "FA 추천".
PAIRS_LOG = os.environ.get("YUKSUNG_PAIRS")
PAIR = re.compile(r"FA2 (?P<a>\d\d \S+ \d+볼 \([^)]+\)) → (?P<wa>.+?) \+ (?P<b>\d\d \S+ \d+볼 \([^)]+\)) → (?P<wb>.+?)(?P<chain> \[연쇄\])? 육성 (?P<dev>.+)$")
FA_ONE = re.compile(r"(?P<yy>\d\d) (?P<name>\S+) (?P<ball>\d+)볼 \((?P<club>[^)]+)\)")
PRICE_MAN = {8: 1500, 9: 3000}
# Each deck's FA already held (the pair block "<that FA> + 한 명"): 24롯데's SKY준우 signed '24 박찬호B on 11-17 07:25.
HELD = {"2024 롯데": "7294199@SS"}


def read(log):
    raw = open(log, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp949", errors="replace")
    base, rows = {}, {}
    for line in text.splitlines():
        m = LINE.search(line)
        if not m:
            continue
        d = m.group("deck")
        rec = {"balls": int(m.group("balls")), "win": float(m.group("win")), "rf": float(m.group("rf")), "ra": float(m.group("ra"))}
        if m.group("key") is None:
            base[d] = rec
            continue
        f = FA.search(m.group("rest"))
        if not f:
            continue
        key = m.group("key")
        rec.update(key=key, spot=key.split("@")[1], yy=f.group("yy"), name=f.group("name"), ball=int(f.group("ball")), club=f.group("club"),
                   what=f.group("what"), screen=float(f.group("screen")), dev=f.group("dev") or "", chain=bool(f.group("chain")),
                   idx=key.split("@")[0])
        rows.setdefault(d, []).append(rec)
    return base, rows


def read_pairs(log):
    """The pair run's rows by deck: both FAs (key order), what each changes, games."""
    raw = open(log, "rb").read()
    try:
        text = raw.decode("utf-8")
    except UnicodeDecodeError:
        text = raw.decode("cp949", errors="replace")
    out = {}
    for line in text.splitlines():
        m = LINE.search(line)
        if not m or m.group("key") is None or "&" not in m.group("key"):
            continue
        p = PAIR.search(m.group("rest"))
        if not p:
            continue
        ka, kb = m.group("key").split("&")
        fa = []
        for who, key, what in (("a", ka, "wa"), ("b", kb, "wb")):
            f = FA_ONE.match(p.group(who)).groupdict()
            f.update(key=key, spot=key.split("@")[1], idx=key.split("@")[0], ball=int(f["ball"]), what=p.group(what))
            fa.append(f)
        out.setdefault(m.group("deck"), []).append({"win": float(m.group("win")), "rf": float(m.group("rf")), "ra": float(m.group("ra")),
                                                     "balls": int(m.group("balls")), "fa": fa, "chain": bool(p.group("chain")), "dev": p.group("dev"),
                                                     "key": m.group("key")})
    return out


def pair_sheet(deck, tag, b, single_best, owner, recs):
    """The "FA 2명 추천" sheet spec and its summary lines."""
    cap = owner[2] if owner else None
    head = ["순위", "FA 1", "FA 1 자리", "FA 2", "FA 2 자리", "바뀌는 것", "승률(%)", "오름", "한 명 1위보다", "득점", "실점", "볼 합계", "상한", "계약금 합계", "FA 육성안 (FA 1 // FA 2)"]
    cols = "ABCDEFGHIJKLMNO"
    sb = single_best
    values = [
        ["A1", f"FA 2명 추천 · {tag} ({TITLE})"],
        ["A2", "1군 FA 2명(투수·타자 상관없이, 사용자 11-17 07:3x). FA 추천 시트(한 명) 위 10명의 배치끼리, 자리가 겹치지 않고 볼 상한 안인 짝을 "
               "한 명씩 오른 폭의 합으로 줄 세워 위 24짝(타자+투수 짝 6개 더)을 경기로 잼. 조건은 한 명 시트와 같음(덱 경쟁 리그 133덱 × 1,500경기). "
               "FA 투수는 포지션 변경 없음(자기 역할 자리), 연쇄(밀린 선수의 다른 자리·DH)는 기존 선수만."],
        ["A3", f"지금(FA 없음): {b['win']:.2f}% · {b['balls']}볼"
               + (f" · 한 명 1위 '{sb['yy']} {sb['name']} {sb['spot']} {sb['win']:.2f}%" if sb else "")
               + (f" · {owner[0]} {owner[1]} 리그 상한 {cap}볼" if owner else " · 상한 150볼로 봄")],
    ]
    r0 = 5
    values += [[f"{cols[k]}{r0}", h] for k, h in enumerate(head)]
    fills = [[f"A{r0}:O{r0}", "FF2A2A30"]]
    fonts = [[f"A{r0}:O{r0}", "FFF0C040"]]
    bold = ["A1", f"A{r0}:O{r0}"]

    def name(f):
        # Not "'01 위재영": 한셀 takes a leading apostrophe as its text mark and drops it (11-17 read-back: 20 cells differed).
        return f"{f['name']} '{f['yy']} {f['ball']}볼({f['club']})"

    def row(r, rank, x):
        a, c = x["fa"]
        price = PRICE_MAN.get(a["ball"], 0) + PRICE_MAN.get(c["ball"], 0)
        return [[f"A{r}", rank], [f"B{r}", name(a)], [f"C{r}", spot_text(a["spot"])], [f"D{r}", name(c)], [f"E{r}", spot_text(c["spot"])],
                [f"F{r}", f"{a['what']} / {c['what']}"], [f"G{r}", f"{x['win']:.2f}"], [f"H{r}", f"{x['win'] - b['win']:+.2f}%p"],
                [f"I{r}", f"{x['win'] - sb['win']:+.2f}%p" if sb else "-"], [f"J{r}", f"{x['rf']:.2f}"], [f"K{r}", f"{x['ra']:.2f}"],
                [f"L{r}", f"{x['balls']}볼"], [f"M{r}", f"{cap}볼" if cap else "150볼"], [f"N{r}", f"{price:,}만"], [f"O{r}", x["dev"]]]

    ranked = sorted(recs, key=lambda x: -x["win"])
    lines = [f"## {deck} ({tag}) FA 2명 — 지금 {b['win']:.2f}%" + (f", 한 명 1위 {sb['name']} {sb['win']:.2f}%" if sb else "")]
    for i, x in enumerate(ranked[:10]):
        r = r0 + 1 + i
        values += row(r, i + 1, x)
        if i == 0:
            fills.append([f"A{r}:O{r}", "FFFFF2CC"])
        a, c = x["fa"]
        lines.append(f"{i + 1}. {name(a)} {a['spot']} + {name(c)} {c['spot']} — {x['win']:.2f}% ({x['win'] - b['win']:+.2f}%p, 한 명 1위보다 "
                     f"{(x['win'] - sb['win']) if sb else 0:+.2f}%p), {x['balls']}볼 | {a['what']} / {c['what']}")
    end = r0 + min(10, len(ranked)) + 2
    held = HELD.get(deck)
    if held:
        with_held = [x for x in ranked if held in x["key"].split("&")]
        hn = next((f for x in with_held for f in x["fa"] if f["key"] == held), None)
        if with_held and hn:
            values.append([f"A{end}", f"지금 가진 '{hn['yy']} {hn['name']} + 한 명 ({len(with_held)}짝, 모두 경기로 잼)"])
            bold.append(f"A{end}")
            h = end + 1
            values += [[f"{cols[k]}{h}", t] for k, t in enumerate(head)]
            fills.append([f"A{h}:O{h}", "FF2A2A30"])
            fonts.append([f"A{h}:O{h}", "FFF0C040"])
            bold.append(f"A{h}:O{h}")
            for k, x in enumerate(with_held):
                values += row(h + 1 + k, k + 1, x)
            lines.append(f"  '{hn['yy']} {hn['name']} + 한 명: " + "; ".join(
                f"{k + 1}. {name(f)} {f['spot']} {x['win']:.2f}%" for k, x in enumerate(with_held[:6]) for f in x["fa"] if f["key"] != held))
            end = h + len(with_held) + 2
    values.append([f"A{end}", "읽는 법: 승률은 리그 133덱 상대 평균. 0.3%p 안의 차이는 거의 같음. 짝은 한 명 결과로 골랐으므로, 한 명 위 10명 밖 선수의 짝은 재지 않음."])
    sheet = {"name": "FA 2명 추천", "values": values, "fills": fills, "fonts": fonts, "bold": bold,
             "widths": [["A", 6], ["B", 20], ["C", 12], ["D", 20], ["E", 12], ["F", 44], ["G", 8], ["H", 8], ["I", 10], ["J", 6], ["K", 6],
                        ["L", 8], ["M", 6], ["N", 9], ["O", 80]],
             "formats": [["G:G", "0.00"], ["J:K", "0.00"]]}
    return sheet, lines


def tag_of(deck):
    """"25LG", "24롯데" … from the book's file name (육성표_1110_25LG_레전드.cell)."""
    t = os.path.basename(DECKS[deck]).replace("육성표_", "").replace(".cell", "").replace("_레전드", "")
    return re.sub(r"^\d{4}_", "", t)


def spot_text(spot):
    """"SS+DH" → "SS (원래 선수는 DH로)", "SP5>RP3" → "SP5 (원래 선수는 RP3로)", "SS>3B+DH" → "SS (원래 선수는 3B로, 3B 선수는 DH로)"."""
    dh = spot.endswith("+DH")
    s = spot[:-3] if dh else spot
    if ">" in s:
        at, to = s.split(">", 1)
        return f"{at} (원래 선수는 {to}로" + (f", {to} 선수는 DH로)" if dh else ")")
    return s + (" (원래 선수는 DH로)" if dh else "")


def spot_of(spot):
    """The FA's own spot: "SP5>RP3" → "SP5", "SS+DH" → "SS"."""
    return spot.replace("+DH", "").split(">")[0]


def main():
    log, spec_dir, out_dir = sys.argv[1:4]
    top = int(sys.argv[4]) if len(sys.argv) > 4 else 10
    base, rows = read(log)
    pair_rows = read_pairs(PAIRS_LOG) if PAIRS_LOG else {}
    os.makedirs(spec_dir, exist_ok=True)
    summary = []
    for deck in DECKS:
        if deck not in base or deck not in rows:
            summary.append(f"{deck}: 결과 없음")
            continue
        b = base[deck]
        every = sorted(rows[deck], key=lambda r: -r["win"])
        # One row per FA card in the ranking (his best variant by games); the chain run also played the 11-15 style one.
        rs, seen = [], set()
        for x in every:
            if x["idx"] not in seen:
                seen.add(x["idx"])
                rs.append(x)
        chained = any(x["chain"] for x in every)
        owner = OWNERS.get(deck)
        cap = owner[2] if owner else None
        tag = tag_of(deck)
        head = ["순위", "시즌", "FA 선수", "볼", "원래 구단", "들어갈 자리", "바뀌는 것", "승률(%)", "오름", "득점", "실점", "볼 합계", "상한", "계약금", "FA 육성안 (특강 / 트레이닝 / 재계약)"]
        cols = "ABCDEFGHIJKLMNO"
        values = [
            ["A1", f"FA 선수 추천 · {tag} ({TITLE})"],
            ["A2", "기준: 이 육성표의 타순·투수진·육성 숫자 그대로, 덱 경쟁 리그 133덱과 덱마다 1,500경기(작전 보통, 코칭스태프 +3). "
                   "FA는 팀컬러·코칭스태프 보너스를 받고 특강 주 +5·트레이닝 +6 × 3·재계약 +5 × 6으로 키운 기준. "
                   + ("이 시트는 FA 한 명씩(1군 FA는 2명까지: FA 2명 추천 시트)." if PAIRS_LOG else "FA는 1군에 1장.")
                   + (" 연쇄 포함: FA에게 밀린 선수가 다른 자리(투수는 원래 역할·포지션 변경 가능한 역할, 포지션 변경으로 바꾼 투수는 원래 역할로 돌아가면 변경 안 함, "
                      "야수는 부포지션·DH)로 가서 그 자리 선수를 밀어내는 경우와, FA 투수가 다른 역할 자리에 드는 경우(포지션 변경 없이)까지 견줌." if chained else "")],
            ["A3", f"지금(FA 없음): 승률 {b['win']:.2f}% · 득 {b['rf']:.2f} 실 {b['ra']:.2f} · {b['balls']}볼"
                   + (f" · {owner[0]} {owner[1]} 리그 상한 {cap}볼" if owner else " · 11-16 시즌부터 상한: 메이저 150 · 레전드 155 · 챔피언스 160")],
            ["A4", "계약금 8볼 1,500만 · 9볼 3,000만, 계약 14일. 연장 +14일(1~3번째는 영입가 +10%씩, 4번째부터 3번째 값의 40%로 고정), 연장 보너스 5점 × 처음 6번."],
        ]
        r0 = 6
        values += [[f"{cols[k]}{r0}", h] for k, h in enumerate(head)]
        fonts, fills = [], [[f"A{r0}:O{r0}", "FF2A2A30"]]
        fonts.append([f"A{r0}:O{r0}", "FFF0C040"])

        def row(r, rank, x):
            over = cap is not None and x["balls"] > cap
            return [[f"A{r}", rank], [f"B{r}", (2000 if int(x["yy"]) < 50 else 1900) + int(x["yy"])], [f"C{r}", x["name"]], [f"D{r}", x["ball"]], [f"E{r}", x["club"]],
                    [f"F{r}", spot_text(x["spot"])], [f"G{r}", x["what"]], [f"H{r}", f"{x['win']:.2f}"],
                    [f"I{r}", f"{x['win'] - b['win']:+.2f}%p"], [f"J{r}", f"{x['rf']:.2f}"], [f"K{r}", f"{x['ra']:.2f}"],
                    [f"L{r}", f"{x['balls']}볼" + (" (넘음)" if over else "")], [f"M{r}", f"{cap}볼" if cap else "-"],
                    [f"N{r}", PRICE.get(x["ball"], "-")], [f"O{r}", x["dev"]]], over
        lines = [f"## {deck} ({tag}) — 지금 {b['win']:.2f}% · {b['balls']}볼" + (f" · {owner[0]} {owner[1]} 상한 {cap}" if owner else "")]
        for i, x in enumerate(rs[:top]):
            r = r0 + 1 + i
            cells, over = row(r, i + 1, x)
            values += cells
            if i == 0:
                fills.append([f"A{r}:O{r}", "FFFFF2CC"])
            if over:
                fonts.append([f"L{r}", "FFC00000"])
            lines.append(f"{i + 1}. '{x['yy']} {x['name']} {x['ball']}볼({x['club']}) {x['spot']}{' [연쇄]' if x['chain'] else ''} — {x['win']:.2f}% ({x['win'] - b['win']:+.2f}%p), "
                         f"{x['balls']}볼{' 상한 넘음' if over else ''} | {x['what']}")
        # 자리별 1위: the best of every spot the run tried (the FA's own role first; one line per spot).
        r = r0 + top + 3
        values.append([f"A{r - 1}", "자리별 1위"])
        values += [[f"{cols[k]}{r}", h] for k, h in enumerate(head)]
        fills.append([f"A{r}:O{r}", "FF2A2A30"])
        fonts.append([f"A{r}:O{r}", "FFF0C040"])
        best = {}
        for x in every:
            s = spot_of(x["spot"])
            if s not in best:
                best[s] = x
        order = ["C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH", "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP"]
        for k, s in enumerate(sorted(best, key=lambda s: order.index(s) if s in order else 99)):
            cells, over = row(r + 1 + k, s, best[s])
            values += cells
            if over:
                fonts.append([f"L{r + 1 + k}", "FFC00000"])
        end = r + len(best) + 2
        # 연쇄로 바뀐 것: FA cards played both ways (11-15 style and with the chain), by games.
        pairs = []
        for x in rs:
            if not x["chain"]:
                continue
            plain = next((p for p in every if p["idx"] == x["idx"] and not p["chain"]), None)
            if plain is not None:
                pairs.append((plain, x))
        if pairs:
            values.append([f"A{end}", "연쇄로 바뀐 것 (같은 FA를 연쇄 없이 / 연쇄로, 둘 다 경기로 잼)"])
            h = end + 1
            values += [[f"{cols[k]}{h}", t] for k, t in enumerate(head)]
            fills.append([f"A{h}:O{h}", "FF2A2A30"])
            fonts.append([f"A{h}:O{h}", "FFF0C040"])
            for k, (plain, x) in enumerate(pairs):
                for j, (label, v) in enumerate((("연쇄 없이", plain), ("연쇄", x))):
                    cells, over = row(h + 1 + 2 * k + j, label, v)
                    values += cells
                    if over:
                        fonts.append([f"L{h + 1 + 2 * k + j}", "FFC00000"])
            lines.append("  연쇄로 바뀐 것: " + "; ".join(f"'{x['yy']} {x['name']} {p['spot']} {p['win']:.2f}% → {x['spot']} {x['win']:.2f}% ({x['win'] - p['win']:+.2f}%p)" for p, x in pairs))
            end = h + 2 * len(pairs) + 2
        values.append([f"A{end}", "읽는 법: 승률은 리그 133덱 상대 평균. 순위 사이 차이가 0.3%p 안이면 거의 같음. 볼 합계가 상한을 넘으면 그 리그에서 1군이 30%로 뜀 → 다른 자리 볼을 줄여야 씀."])
        spec = {
            "src": DECKS[deck], "out_dir": out_dir, "base": f"육성표_{DATE}_{tag}_FA", "sheet": "육성 계산기", "values": [],
            "add_sheet": {"name": "FA 추천", "values": values, "fills": fills, "fonts": fonts, "bold": ["A1", f"A{r0}:O{r0}", f"A{r - 1}", f"A{r}:O{r}"]
                          + ([f"A{end - 2 * len(pairs) - 3}", f"A{end - 2 * len(pairs) - 2}:O{end - 2 * len(pairs) - 2}"] if pairs else []),
                          "widths": [["A", 6], ["B", 5], ["C", 11], ["D", 4], ["E", 9], ["F", 12], ["G", 30], ["H", 8], ["I", 8], ["J", 6], ["K", 6],
                                     ["L", 10], ["M", 6], ["N", 8], ["O", 62]],
                          # 한셀 reads "7.40" as the number 7.4; show two decimals in 승률·득점·실점.
                          "formats": [["H:H", "0.00"], ["J:K", "0.00"]]},
        }
        if pair_rows.get(deck):
            sheet, plines = pair_sheet(deck, tag, b, rs[0] if rs else None, owner, pair_rows[deck])
            spec["add_sheets"] = [sheet]
            lines += [""] + plines
        with open(os.path.join(spec_dir, f"spec_fa_{tag}.json"), "w", encoding="utf-8") as f:
            json.dump(spec, f, ensure_ascii=False, indent=1)
        summary += lines + [""]
    with open(os.path.join(spec_dir, "fa_summary.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(summary))
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(summary))


if __name__ == "__main__":
    main()
