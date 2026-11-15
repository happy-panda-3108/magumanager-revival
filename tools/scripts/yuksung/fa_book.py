"""2026-11-15 (user 13:2x, FA 선수카드: "FA 선수들이 구현되면, 현재 있는 육성표를 기준으로 각 팀마다 어떤 FA 선수를 사용하는게 승률이 가장
크게 오르는지, 2순위는 어떤 선수인지 등 분석해서 육성표에 추가해주길 바람"): the FA run's log (gen_fa_env.py screen → run_entry.ps1)
into an "FA 추천" sheet per book: book_edit.ps1 specs that copy each of the users' latest 한셀 books to new files (the users' own
files stay) with the sheet added after the last one, plus a text summary.
  python fa_book.py <run log> <spec dir> <book out dir> [top]   → spec_<tag>.json per deck, summary.txt"""
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
FA = re.compile(r"FA (?P<yy>\d\d) (?P<name>\S+) (?P<ball>\d+)볼 \((?P<club>[^)]+)\) → (?P<what>.+?) \[선별 (?P<screen>[-+][\d.]+)점\](?: 육성 (?P<dev>.+))?$")


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
                   what=f.group("what"), screen=float(f.group("screen")), dev=f.group("dev") or "")
        rows.setdefault(d, []).append(rec)
    return base, rows


def tag_of(deck):
    """"25LG", "24롯데" … from the book's file name (육성표_1110_25LG_레전드.cell)."""
    t = os.path.basename(DECKS[deck]).replace("육성표_", "").replace(".cell", "").replace("_레전드", "")
    return re.sub(r"^\d{4}_", "", t)


def spot_text(spot):
    return spot.replace("+DH", " (원래 선수는 DH로)")


def main():
    log, spec_dir, out_dir = sys.argv[1:4]
    top = int(sys.argv[4]) if len(sys.argv) > 4 else 10
    base, rows = read(log)
    os.makedirs(spec_dir, exist_ok=True)
    summary = []
    for deck in DECKS:
        if deck not in base or deck not in rows:
            summary.append(f"{deck}: 결과 없음")
            continue
        b = base[deck]
        rs = sorted(rows[deck], key=lambda r: -r["win"])
        owner = OWNERS.get(deck)
        cap = owner[2] if owner else None
        tag = tag_of(deck)
        head = ["순위", "시즌", "FA 선수", "볼", "원래 구단", "들어갈 자리", "바뀌는 것", "승률(%)", "오름", "득점", "실점", "볼 합계", "상한", "계약금", "FA 육성안 (특강 / 트레이닝 / 재계약)"]
        cols = "ABCDEFGHIJKLMNO"
        values = [
            ["A1", f"FA 선수 추천 · {tag} (11-15 엔진 분석)"],
            ["A2", "기준: 이 육성표의 타순·투수진·육성 숫자 그대로, 덱 경쟁 리그 133덱과 덱마다 1,500경기(작전 보통, 코칭스태프 +3). "
                   "FA는 팀컬러·코칭스태프 보너스를 받고 특강 주 +5·트레이닝 +6 × 3·재계약 +5 × 6으로 키운 기준. FA는 1군에 1장."],
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
            lines.append(f"{i + 1}. '{x['yy']} {x['name']} {x['ball']}볼({x['club']}) {x['spot']} — {x['win']:.2f}% ({x['win'] - b['win']:+.2f}%p), "
                         f"{x['balls']}볼{' 상한 넘음' if over else ''} | {x['what']}")
        # 자리별 1위: the best of every spot the run tried (the FA's own role first; one line per spot).
        r = r0 + top + 3
        values.append([f"A{r - 1}", "자리별 1위"])
        values += [[f"{cols[k]}{r}", h] for k, h in enumerate(head)]
        fills.append([f"A{r}:O{r}", "FF2A2A30"])
        fonts.append([f"A{r}:O{r}", "FFF0C040"])
        best = {}
        for x in rs:
            s = x["spot"].replace("+DH", "")
            if s not in best:
                best[s] = x
        order = ["C", "1B", "2B", "3B", "SS", "LF", "CF", "RF", "DH", "SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP"]
        for k, s in enumerate(sorted(best, key=lambda s: order.index(s) if s in order else 99)):
            cells, over = row(r + 1 + k, s, best[s])
            values += cells
            if over:
                fonts.append([f"L{r + 1 + k}", "FFC00000"])
        values.append([f"A{r + len(best) + 2}", "읽는 법: 승률은 리그 133덱 상대 평균. 순위 사이 차이가 0.3%p 안이면 거의 같음. 볼 합계가 상한을 넘으면 그 리그에서 1군이 30%로 뜀 → 다른 자리 볼을 줄여야 씀."])
        spec = {
            "src": DECKS[deck], "out_dir": out_dir, "base": f"육성표_1115_{tag}_FA", "sheet": "육성 계산기", "values": [],
            "add_sheet": {"name": "FA 추천", "values": values, "fills": fills, "fonts": fonts, "bold": ["A1", f"A{r0}:O{r0}", f"A{r - 1}", f"A{r}:O{r}"],
                          "widths": [["A", 6], ["B", 5], ["C", 11], ["D", 4], ["E", 9], ["F", 12], ["G", 30], ["H", 8], ["I", 8], ["J", 6], ["K", 6],
                                     ["L", 10], ["M", 6], ["N", 8], ["O", 62]],
                          # 한셀 reads "7.40" as the number 7.4; show two decimals in 승률·득점·실점.
                          "formats": [["H:H", "0.00"], ["J:K", "0.00"]]},
        }
        with open(os.path.join(spec_dir, f"spec_fa_{tag}.json"), "w", encoding="utf-8") as f:
            json.dump(spec, f, ensure_ascii=False, indent=1)
        summary += lines + [""]
    with open(os.path.join(spec_dir, "fa_summary.txt"), "w", encoding="utf-8") as f:
        f.write("\n".join(summary))
    sys.stdout.reconfigure(encoding="utf-8")
    print("\n".join(summary))


if __name__ == "__main__":
    main()
