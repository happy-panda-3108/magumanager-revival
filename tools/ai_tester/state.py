"""AI 테스터 계정의 지금 상태를 글로 (엔진 세션 2026-11-19, 사용자: "테스터가 실행될 때 사용되는 토큰 양만 줄이면 돼").

실서버 DB를 읽기 전용(mode=ro)으로 열어 숫자를 꺼낸다. 화면 캡처로 MP·순위·계약일을 읽지 않게 하려는 것이다. 아무것도 쓰지 않는다.
  python -I state.py <테스터 1-6 | 이메일> [--ledger N] [--games N]
테스터 번호: 1 NC autotest3 · 2 두산 autotest4 · 3 쌍방울 autotest5 · 4 kt autotest6 · 5 LG autotest2 · 6 자급자족 autotest7.
"""
import sqlite3
import sys
from datetime import datetime, timedelta

DB = 'file:C:/Ma9MProject/server/data/ma9m.db?mode=ro'
TABLES = 'C:/Ma9MProject/server/data/tables/'
EMAIL = {'1': 'autotest3', '2': 'autotest4', '3': 'autotest5', '4': 'autotest6', '5': 'autotest2', '6': 'autotest7'}
GRADE = {1: '비기너', 2: '루키', 3: '더블A', 4: '트리플A', 5: '메이저', 6: '레전드', 7: '챔피언스'}


def names():
    out = {}
    for f in ('P_BatterTable.txt', 'P_PItcherTable.txt'):
        try:
            with open(TABLES + f, encoding='utf-8') as fh:
                for line in fh:
                    p = line.split('\t')
                    if len(p) > 2 and p[0].isdigit():
                        out[int(p[0])] = f"'{p[1][-2:]} {p[2]}"
        except OSError:
            pass
    return out


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    who = sys.argv[1]
    email = (EMAIL.get(who, who.split('@')[0])) + '@ma9m.local'
    opt = {sys.argv[i].lstrip('-'): sys.argv[i + 1] for i in range(2, len(sys.argv) - 1, 2)}
    nled, ngames = int(opt.get('ledger', 8)), int(opt.get('games', 5))
    c = sqlite3.connect(DB, uri=True)
    acc = c.execute('select id from account where email=?', (email,)).fetchone()
    if not acc:
        print('없는 계정:', email)
        return 1
    team = c.execute('select user_code, team_name, league_grade, mp from team where account_id=? order by user_code limit 1', (acc[0],)).fetchone()
    u, tname, grade, mp = team
    now = datetime.now()
    print(f'[팀] {tname} ({email.split("@")[0]}) MP {mp:,} · {now:%m-%d %H:%M} 기준')
    lg = c.execute('select tier, season, next_game, my_index, last_game from league where user_code=?', (u,)).fetchone()
    if lg:
        tier, season, nxt, me, last = lg
        unconf = c.execute('select count(*) from league_game where user_code=? and season=? and game<=? and confirmed=0 and (home=? or away=?)', (u, season, last, me, me)).fetchone()[0]
        print(f'[리그] {GRADE.get(grade, grade)} {season}시즌, {last}경기까지 치름, 다음 {nxt}차전 · 확인 안 한 우리 경기 {unconf}')
        clubs = c.execute('select club, name, w, l, d, rf, ra, is_user from league_club where user_code=?', (u,)).fetchall()
        def pct(r):
            return r[2] / max(1, r[2] + r[3])
        clubs.sort(key=lambda r: (-pct(r), -(r[2] - r[3])))
        rank = {r[0]: i + 1 for i, r in enumerate(clubs)}
        line = []
        for r in clubs:
            k = rank[r[0]]
            if k <= 3 or abs(k - rank.get(me, 0)) <= 1:
                mark = '◀' if r[0] == me else ''
                line.append(f'{k} {r[1]} {r[2]}-{r[3]}-{r[4]} .{int(round(pct(r) * 1000)):03d} 득{r[5]} 실{r[6]}{mark}')
        print('[순위] ' + ' | '.join(line))
        games = c.execute('select game, home, away, home_score, away_score, confirmed from league_game where user_code=? and season=? and game<=? and (home=? or away=?) order by game desc limit ?', (u, season, last, me, me, ngames)).fetchall()
        cn = {r[0]: r[1] for r in clubs}
        res = []
        for g, h, a, hs, as_, conf in games:
            mine, theirs, opp = (hs, as_, a) if h == me else (as_, hs, h)
            wl = 'W' if mine > theirs else 'L' if mine < theirs else 'D'
            res.append(f'{g} {wl} {mine}:{theirs} {cn.get(opp, opp)}{"" if conf else "(미확인)"}')
        print('[최근] ' + ' | '.join(res))
    nm = names()
    lu = c.execute('select lineup from team_lineup where user_code=?', (u,)).fetchone()
    first = []
    if lu and lu[0]:
        parts = lu[0].split('|')
        for k in (0, 2):
            if len(parts) > k and parts[k]:
                first += [int(x) for x in parts[k].split(',') if x.strip().isdigit()]
    rows = {r[0]: r for r in c.execute('select card_idx, acquired, contract_until from team_card where user_code=?', (u,)).fetchall()}
    near = []
    today = now.date()
    for idx in first:
        r = rows.get(idx)
        if not r:
            continue
        # ContractHandlers.Until: the stored end, or the day it was acquired (local) + 7 days; days left = end - today.
        try:
            if r[2]:
                end = datetime.fromisoformat(str(r[2])[:10]).date()
            else:
                acq = datetime.fromisoformat(str(r[1]).replace('Z', '+00:00'))
                end = (acq.astimezone() if acq.tzinfo else acq).date() + timedelta(days=7)
        except ValueError:
            continue
        days = max(0, (end - today).days)
        if days <= 3:
            near.append((days, nm.get(idx, idx)))
    near.sort()
    print('[1군 계약 3일 이하] ' + (', '.join(f'{n} {d}일' for d, n in near) if near else '없음') + f' (1군 {len(first)}장, 보유 {len(rows)}장)')
    led = c.execute('select at, source, amount, balance from mp_ledger where user_code=? order by id desc limit ?', (u, nled)).fetchall()
    if led:
        print('[MP 장부 최근] ' + ' | '.join(f'{a[5:16]} {s} {m:+,}' for a, s, m, b in led))
    day = now.strftime('%Y-%m-%d')
    md = c.execute('select key, value from mission_daily where user_code=? and day=?', (u, day)).fetchall()
    cl = c.execute('select category, mp from mission_claim where user_code=? and day=?', (u, day)).fetchall()
    print('[오늘 미션 셈] ' + (', '.join(f'{k} {v}' for k, v in md) if md else '없음') + ' · 받은 보상 ' + (', '.join(f'{k} {m:,}' for k, m in cl) if cl else '없음'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
