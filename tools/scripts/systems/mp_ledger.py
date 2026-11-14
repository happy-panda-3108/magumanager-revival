"""MP 장부 합계 (systems session, 2026-11-14): a team's MP by day (0시 기준) and source, from mp_ledger.

Read-only (sqlite mode=ro), so it can run on the live database while the server is up.

  python -I tools\\scripts\\systems\\mp_ledger.py autotest7@ma9m.local
  python -I tools\\scripts\\systems\\mp_ledger.py autotest7@ma9m.local --days 3 --rows 20
  python -I tools\\scripts\\systems\\mp_ledger.py 21 --db C:\\copy\\ma9m.db      (a user_code instead of an e-mail)

Prints markdown tables: per day the balance at the start, each source's 들어옴/나감, the balance at the end and a check
(start + changes = end); then the whole period per source; with --rows N, the team's last N ledger rows.
"""
import argparse
import sqlite3
import sys
from collections import OrderedDict, defaultdict

LIVE_DB = r"C:\Ma9MProject\server\data\ma9m.db"


def won(n):
    return f"{n:+,}" if n else "0"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("who", help="account e-mail or user_code")
    ap.add_argument("--db", default=LIVE_DB)
    ap.add_argument("--days", type=int, default=0, help="only the last N days (0 = all)")
    ap.add_argument("--rows", type=int, default=0, help="also print the last N ledger rows")
    a = ap.parse_args()
    # The Korean comes out garbled through a cp949 console otherwise.
    sys.stdout.reconfigure(encoding="utf-8")

    con = sqlite3.connect(f"file:{a.db}?mode=ro", uri=True)
    if not con.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='mp_ledger'").fetchone():
        sys.exit("mp_ledger 표가 없습니다 (장부가 들어간 서버가 아직 켜지지 않음).")
    if a.who.isdigit():
        users = [int(a.who)]
    else:
        users = [r[0] for r in con.execute(
            "SELECT t.user_code FROM team t JOIN account c ON c.id = t.account_id WHERE c.email = ?", (a.who,))]
    if not users:
        sys.exit(f"팀을 찾지 못함: {a.who}")

    for user in users:
        name, mp_now = con.execute("SELECT team_name, mp FROM team WHERE user_code = ?", (user,)).fetchone()
        rows = con.execute(
            "SELECT at, source, amount, balance, caller FROM mp_ledger WHERE user_code = ? ORDER BY id", (user,)).fetchall()
        print(f"## {name} (user {user}) · 지금 {mp_now:,} MP · 장부 {len(rows)}줄")
        if not rows:
            continue
        days = OrderedDict()
        for at, source, amount, balance, caller in rows:
            d = days.setdefault(at[:10], {"start": balance - amount, "end": balance, "in": defaultdict(int),
                                          "out": defaultdict(int), "n": defaultdict(int)})
            d["end"] = balance
            if amount > 0:
                d["in"][source] += amount
            elif amount < 0:
                d["out"][source] += amount
            if amount:
                d["n"][source] += 1
        keys = list(days)[-a.days:] if a.days else list(days)

        total_in, total_out, total_n = defaultdict(int), defaultdict(int), defaultdict(int)
        for day in keys:
            d = days[day]
            change = sum(d["in"].values()) + sum(d["out"].values())
            ok = "맞음" if d["start"] + change == d["end"] else f"차이 {d['end'] - d['start'] - change:+,}"
            print(f"\n### {day} · 시작 {d['start']:,} → 끝 {d['end']:,} ({won(change)}, {ok})\n")
            print("| 출처 | 들어옴 | 나감 | 합 | 건수 |")
            print("|---|---:|---:|---:|---:|")
            for s in sorted(set(d["in"]) | set(d["out"]), key=lambda s: -(d["in"][s] - d["out"][s])):
                print(f"| {s} | {d['in'][s]:,} | {d['out'][s]:,} | {won(d['in'][s] + d['out'][s])} | {d['n'][s]} |")
                total_in[s] += d["in"][s]
                total_out[s] += d["out"][s]
                total_n[s] += d["n"][s]
            print(f"| **합계** | {sum(d['in'].values()):,} | {sum(d['out'].values()):,} | {won(change)} | {sum(d['n'].values())} |")

        if len(keys) > 1:
            first, last = days[keys[0]], days[keys[-1]]
            print(f"\n### 기간 합계 {keys[0]} ~ {keys[-1]} · {first['start']:,} → {last['end']:,}\n")
            print("| 출처 | 들어옴 | 나감 | 합 | 건수 |")
            print("|---|---:|---:|---:|---:|")
            for s in sorted(set(total_in) | set(total_out), key=lambda s: -(total_in[s] - total_out[s])):
                print(f"| {s} | {total_in[s]:,} | {total_out[s]:,} | {won(total_in[s] + total_out[s])} | {total_n[s]} |")
            print(f"| **합계** | {sum(total_in.values()):,} | {sum(total_out.values()):,} | "
                  f"{won(sum(total_in.values()) + sum(total_out.values()))} | {sum(total_n.values())} |")

        if a.rows:
            print(f"\n### 마지막 {a.rows}줄\n")
            print("| 시각 | 출처 | 금액 | 잔액 | 자리 |")
            print("|---|---|---:|---:|---|")
            for at, source, amount, balance, caller in rows[-a.rows:]:
                print(f"| {at} | {source} | {won(amount)} | {balance:,} | {caller} |")
        print()


if __name__ == "__main__":
    main()
