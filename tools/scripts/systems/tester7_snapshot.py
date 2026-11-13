"""자급자족 테스터 autotest7 관찰 (systems session, 2026-11-14; user 02:3x: "쿠폰 1개만 쓰는 tester7의 근황을 유심히 잘 관찰해야해").

  python tester7_snapshot.py [--note "회차 메모"]

Reads the live DB read-only (sqlite mode=ro) and appends one block to docs\\tester7_관찰.md: MP, coupons, cards (count, 1군
balls), items (packs, books, tickets), storage, league tier/season/record, mission and achievement MP, training and 특강 counts,
coaches, season bonuses and night exhibitions, plus the change since the previous block. The point is how far START2026 alone
carries a new club (content consumption speed, CLAUDE.md test accounts).
"""
import json
import os
import sqlite3
import sys
from datetime import datetime

DB = r"C:\Ma9MProject\server\data\ma9m.db"
OUT = r"C:\Ma9MProject\docs\tester7_관찰.md"
STATE = r"C:\Ma9MProject\docs\tester7_관찰_last.json"
EMAIL = "autotest7@ma9m.local"
ITEMS = {}
try:
    # Names from the item table the server writes at start (ItemCatalog.WriteTable).
    for line in open(r"C:\Ma9MProject\server\data\tables\ma9_itemlist.txt", encoding="utf-8").read().splitlines()[1:]:
        parts = line.split("\t")
        if len(parts) > 1 and parts[0].isdigit():
            ITEMS[int(parts[0])] = parts[1]
except OSError:
    pass
TIERS = ["비기너", "루키", "트리플A", "메이저", "레전드", "챔피언스"]


def snapshot(c):
    acct = c.execute("SELECT id FROM account WHERE email = ?", (EMAIL,)).fetchone()
    if not acct:
        return None
    team = c.execute("SELECT user_code, team_name, mp, created FROM team WHERE account_id = ? ORDER BY user_code LIMIT 1", (acct[0],)).fetchone()
    if not team:
        return {"team": None}
    u = team[0]
    s = {"user": u, "team": team[1], "mp": team[2], "created": team[3]}
    s["coupons"] = [r[0] for r in c.execute("SELECT code FROM coupon_use WHERE account_id = ? ORDER BY used", (acct[0],))]
    s["cards"] = c.execute("SELECT COUNT(*) FROM team_card WHERE user_code = ?", (u,)).fetchone()[0]
    shop = c.execute("SELECT max_cards, expansions, cards_bought FROM team_shop WHERE user_code = ?", (u,)).fetchone()
    s["storage"], s["expansions"], s["cards_bought"] = shop if shop else (None, 0, 0)
    s["items"] = {str(i): n for i, n in c.execute("SELECT item_idx, amount FROM team_item WHERE user_code = ? AND amount > 0", (u,))}
    s["books"] = sum(n for i, n in c.execute("SELECT item_idx, amount FROM team_item WHERE user_code = ? AND item_idx BETWEEN 31000 AND 39999", (u,)))
    lg = c.execute("SELECT tier, season, next_game, my_index FROM league WHERE user_code = ?", (u,)).fetchone()
    if lg:
        s["tier"], s["season"], s["next_game"] = lg[0], lg[1], lg[2]
        rec = c.execute("SELECT w, l, d, rf, ra, ball FROM league_club WHERE user_code = ? AND club = ?", (u, lg[3])).fetchone()
        if rec:
            s["w"], s["l"], s["d"], s["rf"], s["ra"], s["ball"] = rec
        s["rank"] = 1 + sum(1 for (w, l) in c.execute("SELECT w, l FROM league_club WHERE user_code = ? AND club <> ?", (u, lg[3]))
                            if rec and (w - l) > (rec[0] - rec[1]))
    s["mission_mp"] = c.execute("SELECT COALESCE(SUM(mp), 0) FROM mission_claim WHERE user_code = ?", (u,)).fetchone()[0]
    s["achievement_mp"] = c.execute("SELECT COALESCE(SUM(paid_mp), 0) FROM achievement WHERE user_code = ?", (u,)).fetchone()[0]
    s["season_mp"] = c.execute("SELECT COALESCE(SUM(mp), 0) FROM season_bonus WHERE user_code = ? AND paid = 1", (u,)).fetchone()[0]
    s["trainings"] = c.execute("SELECT COUNT(*) FROM card_training WHERE user_code = ?", (u,)).fetchone()[0]
    s["lessons"] = c.execute("SELECT COUNT(*) FROM card_lesson WHERE user_code = ?", (u,)).fetchone()[0]
    s["coaches"] = c.execute("SELECT COUNT(*) FROM coach_card WHERE user_code = ?", (u,)).fetchone()[0]
    s["auto_exh"] = c.execute("SELECT COUNT(*), COALESCE(SUM(mp), 0) FROM exhibition_auto WHERE user_code = ? AND games > 0", (u,)).fetchone()
    s["contracts"] = c.execute("SELECT COALESCE(SUM(contract_count), 0) FROM team_card WHERE user_code = ?", (u,)).fetchone()[0]
    return s


def block(s, prev, note):
    now = datetime.now().strftime("%m-%d %H:%M")
    if s is None:
        return f"\n### {now}\n- autotest7 계정이 아직 없음\n"
    if s.get("team") is None:
        return f"\n### {now}\n- 계정은 있고 구단이 아직 없음\n"
    def d(key):
        if not prev or key not in prev or prev[key] is None or s.get(key) is None:
            return ""
        diff = s[key] - prev[key]
        return f" ({diff:+,})" if diff else ""
    items = ", ".join(f"{ITEMS.get(int(i), i)} {n}" for i, n in sorted(s["items"].items(), key=lambda kv: int(kv[0])) if not (31000 <= int(i) <= 39999))
    tier = TIERS[s["tier"]] if s.get("tier") is not None and 0 <= s["tier"] < len(TIERS) else "-"
    lines = [f"\n### {now}" + (f" · {note}" if note else ""),
             f"- 구단 {s['team']} (user {s['user']}), 쿠폰 {', '.join(s['coupons']) or '없음'}",
             f"- MP {s['mp']:,}{d('mp')}",
             f"- 카드 {s['cards']}{d('cards')} / 보관함 {s['storage']} (확장 {s['expansions']}), 상점 카드 누계 {s['cards_bought']}{d('cards_bought')}",
             f"- 리그 {tier} 시즌 {s.get('season', '-')} 다음 경기 {s.get('next_game', '-')}: {s.get('w', 0)}승 {s.get('l', 0)}패 {s.get('d', 0)}무"
             f" (득 {s.get('rf', 0)} 실 {s.get('ra', 0)}, 대략 {s.get('rank', '-')}위), 1군 {s.get('ball', '-')}볼",
             f"- 받은 MP 누계: 미션 {s['mission_mp']:,}{d('mission_mp')} · 업적 {s['achievement_mp']:,}{d('achievement_mp')} · 시즌 보상 {s['season_mp']:,}{d('season_mp')}"
             f" · 새벽 시범경기 {s['auto_exh'][0]}일 {s['auto_exh'][1]:,}",
             f"- 육성: 트레이닝 {s['trainings']}{d('trainings')}번, 특강 {s['lessons']}{d('lessons')}번, 재계약 {s['contracts']}{d('contracts')}번, 코치 카드 {s['coaches']}{d('coaches')}장",
             f"- 아이템: {items or '없음'}, 트레이닝북 {s['books']}권{d('books')}"]
    return "\n".join(lines) + "\n"


def main():
    note = sys.argv[sys.argv.index("--note") + 1] if "--note" in sys.argv else ""
    c = sqlite3.connect(f"file:{DB}?mode=ro", uri=True)
    s = snapshot(c)
    prev = json.load(open(STATE, encoding="utf-8")) if os.path.exists(STATE) else None
    if not os.path.exists(OUT):
        open(OUT, "w", encoding="utf-8").write("# 자급자족 테스터 autotest7 관찰\n\n사용자 11-13: START2026 하나로 시작해 더는 지원하지 않고, 자급자족으로 얼마나 오래 할 수 있는지(콘텐츠 소모 속도). "
                                               "11-14 02:3x: \"쿠폰 1개만 쓰는 tester7의 근황을 유심히 잘 관찰해야해.\" 실서버 DB를 읽기만 해서 쌓는 기록(tools\\scripts\\systems\\tester7_snapshot.py). 괄호는 직전 기록과의 차이.\n")
    text = block(s, prev, note)
    open(OUT, "a", encoding="utf-8").write(text)
    if s and s.get("team"):
        json.dump(s, open(STATE, "w", encoding="utf-8"), ensure_ascii=False, default=str)
    sys.stdout.reconfigure(encoding="utf-8")
    print(text)


if __name__ == "__main__":
    main()
