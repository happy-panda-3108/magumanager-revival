"""2026-11-17 20:3x 육성표 규칙 (user, via systems: "1. 가장 높은 승률을 추구한다. 2. 사용자가 수정한 육성표는 건드리지 않는다.
3. 새로운 카드가 추가되면 기존 투수의 포지션 변경, 불펜 1~4중계 위치 순서, 새로운 카드의 육성 등을 모두 따져서 승률을 비교해 가장 높은
조합을 만들어낸다. 4. 선발 순서를 작성할 때 가장 높은 승률의 선발부터 차례대로 작성된다."): the staff search around each final
roster of final_1117.py, entry mode vs the deck league.

  python rule3.py wide  <env in> <env out> <json out> [book ...]    base + 2군 cards into each slot + 1군 swaps
  python rule3.py rank  <json> <log> [n]                            best n per book (win%)
  python rule3.py close <json> <log> <env in> <env out> <json out> n   the best n per book again (run with more games)
  python rule3.py pen   <json> <log> <env in> <env out> <json out>  the 24 RP1-4 orders of each book's winner

Candidates: account books take the club's own 2군 pitchers of the deck season and club (계정_1군밖_투수_1117.tsv, systems 20:4x,
read-only from the live DB, with their 포지션 변경); book decks take the deck season's pitcher cards. A card goes into a slot in its own
role, or via 포지션 변경 where ma9m_pitcher_roles.txt allows it (a new change counted as 성공; a change the account already made as
it is). FA cards stay where they are. A candidate goes only where its ball ≥ the man it replaces, under the deck's ball cap.
"""
import csv
import itertools
import json
import os
import re
import sys

import final_1117 as F

sys.stdout.reconfigure(encoding="utf-8")
T = r"C:\Ma9MProject\server\data\tables"
BENCH_TSV = r"C:\Ma9MProject\docs\육성표\1117_최종\계정_1군밖_투수_1117.tsv"
ACCOUNT = {"01롯데": "SKY마토", "25LG": "SKY해민", "97쌍방울": "최약체구단S", "24롯데": "SKY준우"}
CLUB = {"롯데": "7", "삼성": "8", "LG": "6", "쌍방울": "11", "현대": "4"}
CAP = {"01롯데": 160, "25LG": 160, "12삼성": 155, "01현대": 155, "97쌍방울": 160, "24롯데": 150, "11롯데": 155, "93롯데": 155, "96쌍방울": 160}
SLOT_ROLE = [12] * 5 + [13] * 4 + [14, 15]
SLOTS = ["SP1", "SP2", "SP3", "SP4", "SP5", "RP1", "RP2", "RP3", "RP4", "SU", "CP"]
MARK = {12: "SP", 13: "RP", 14: "SU", 15: "CP"}


def tables():
    pit = {r['idx']: r for r in csv.DictReader(open(os.path.join(T, 'P_PItcherTable.txt'), encoding='utf-8'), delimiter='\t')}
    bat = {r['idx']: r for r in csv.DictReader(open(os.path.join(T, 'P_BatterTable.txt'), encoding='utf-8'), delimiter='\t')}
    allowed = {}
    for r in csv.DictReader(open(os.path.join(T, 'ma9m_pitcher_roles.txt'), encoding='utf-8'), delimiter='\t'):
        allowed.setdefault(r['idx'], set()).update(int(x) for x in r['allowed'].split(',') if x.strip())
    return pit, bat, allowed


def bench_of(book, pit):
    """The club's 2군 pitchers of the deck season: [(idx, pt)]."""
    year, club = F.LABEL[book].split(' ')
    if book in ACCOUNT:
        out, on = [], False
        for line in open(BENCH_TSV, encoding='utf-8'):
            if line.startswith('== '):
                on = line.startswith('== ' + ACCOUNT[book] + ' ')
                continue
            f = line.rstrip('\n').split('\t')
            if on and len(f) >= 6 and f[0].isdigit() and f[1] == year:
                r = pit.get(f[0])
                if r and r['teamcode'] == CLUB[club]:
                    out.append((f[0], int(f[5] or 0)))
        return out
    return [(i, 0) for i, r in pit.items() if r['year'] == year and r['teamcode'] == CLUB[club]]


def card_of(token, book, pit, bat, kind):
    """The card row a staff/bats token names (an idx, or a name of the deck season)."""
    name = token.split('@')[0]
    rows = pit if kind == 'P' else bat
    if name.isdigit():
        return rows.get(name)
    year, club = F.LABEL[book].split(' ')
    for r in rows.values():
        if r['name'] == name and r['year'] == year and r['teamcode'] == CLUB[club]:
            return r
    return None


def token(r, slot, pt, allowed):
    """How card r plays slot (None = not allowed): its own role, the account's change, a new change (성공), or back to its own role
    with the 초기화권 (80만 MP) when it was changed to another one."""
    role, main = SLOT_ROLE[slot], int(r['mainposition'])
    if pt:
        to, res = pt // 10 + 11, pt % 10
        if to == role:
            return f"{r['idx']}@{MARK[role]}{'!' if res == 1 else ''}"
        return r['idx'] if main == role else None
    if main == role:
        return r['idx']
    if role in allowed.get(r['idx'], set()):
        return f"{r['idx']}@{MARK[role]}"
    return None


def how(r, tk, pt):
    """What a placement costs, for the description."""
    if '@' in tk:
        return f", 포지션 변경 {tk.split('@')[1]}" + ("" if pt else "(새로, 40만)")
    return ", 초기화권으로 원래 보직(80만)" if pt else ""


# Rule 1 ("가장 높은 승률"): the other FA picks within 0.1%p of the final one play in the same run (11-17 15:xx the final took the one
# needing no new 포지션 변경).
ALT = {
    "01롯데": [("800101,박지철,손민한,박석진@SP!,강상수@SP!,가득염@RP,김사율,임경완,문동환,7108229,7106503",
               "FA 둘째를 이병석 대신 '01 신철인(셋업): 가득염 셋업→1번 중계(포지션 변경, 40만), 김사율 2번·임경완 3번 중계, 김영수 빠짐")],
    "97쌍방울": [("김원형,성영재,오상민@SP!,오봉옥@SP!,김기덕,최정환,800108@RP,정수찬,김민국,김현욱,7106579",
                "FA 둘째를 정민태 대신 '97 김경원(마무리): 조규제 마무리→2번 중계(포지션 변경, 40만), 정수찬 3번·김민국 4번 중계, 윤형배 빠짐")],
    "24롯데": [("윌커슨,800101,반즈B,박세웅,7161069,박진,한현희,전미르,구승민,김상수A,7161073",
               "주현상 마무리에 김원중을 중계로 돌리지 않고 뺌(박진 1번 중계 그대로)")],
}

# The final rosters' balls as the sim counts them (bench batters included), from the FA runs.
SIM_BALLS = {"01롯데": 147, "24롯데": 150, "25LG": 160, "97쌍방울": 147, "12삼성": 153, "01현대": 151, "11롯데": 147, "93롯데": 149, "96쌍방울": 144}


def wide(env_in, env_out, json_out, books):
    pit, bat, allowed = tables()
    variants = {}
    for book in books or list(F.FINAL):
        conf = F.FINAL[book]
        staff, bats = conf['staff'].split(','), conf['bats'].split(',')
        label = F.LABEL[book]
        rows = [card_of(t, book, pit, bat, 'P') for t in staff]
        balls = SIM_BALLS[book]
        fa = {k for k, t in enumerate(staff) if t.split('@')[0].isdigit() and int(t.split('@')[0]) >= 7000000}
        sids = {r['sidx'] for r in rows}
        vs = {f"{label}#r3_base": (conf['staff'], "최종안 그대로")}
        for st, desc in ALT.get(book, []):
            vs[f"{label}#r3_{len(vs)}"] = (st, desc)
        ball = lambda r: int(r['ball'])
        # A 1군 card changed to another role but playing its own role's slot: back to its own role in place (초기화권).
        for s in range(11):
            pt = pt_of(staff[s])
            if s in fa or not pt or pt // 10 + 11 == SLOT_ROLE[s]:
                continue
            tk = token(rows[s], s, pt, allowed)
            if tk is not None:
                st = list(staff)
                st[s] = tk
                vs[f"{label}#r3_{len(vs)}"] = (','.join(st), f"{rows[s]['name']} {SLOTS[s]} 그대로, 초기화권으로 원래 보직(80만)")
        # 2군 cards into a slot, and (two steps) the man there moving on to another slot whose man leaves.
        for idx, pt in bench_of(book, pit):
            r = pit[idx]
            if r['sidx'] in sids:
                continue
            for s in range(11):
                if s in fa or ball(r) < ball(rows[s]):
                    continue
                tk = token(r, s, pt, allowed)
                if tk is None:
                    continue
                if balls - ball(rows[s]) + ball(r) <= CAP[book]:
                    st = list(staff)
                    st[s] = tk
                    vs[f"{label}#r3_{len(vs)}"] = (','.join(st), f"2군 {r['name']}({r['ball']}볼{how(r, tk, pt)}) → {SLOTS[s]}, {rows[s]['name']} 빠짐")
                for t in range(11):
                    if t == s or t in fa or ball(rows[s]) < ball(rows[t]):
                        continue
                    to = token(rows[s], t, pt_of(staff[s]), allowed)
                    if to is None or balls - ball(rows[t]) + ball(r) > CAP[book]:
                        continue
                    st = list(staff)
                    st[s], st[t] = tk, to
                    vs[f"{label}#r3_{len(vs)}"] = (','.join(st), f"2군 {r['name']}({r['ball']}볼{how(r, tk, pt)}) → {SLOTS[s]}, "
                                                   f"{rows[s]['name']} → {SLOTS[t]}{how(rows[s], to, pt_of(staff[s]))}, {rows[t]['name']} 빠짐")
        # 1군 swaps between two pitchers (not two starters: rule 4 orders the rotation by 선발 등판 승률).
        for i, j in itertools.combinations(range(11), 2):
            if i in fa or j in fa or (i < 5 and j < 5):
                continue
            ti = token(rows[i], j, pt_of(staff[i]), allowed)
            tj = token(rows[j], i, pt_of(staff[j]), allowed)
            if ti is None or tj is None:
                continue
            st = list(staff)
            st[i], st[j] = tj, ti
            vs[f"{label}#r3_{len(vs)}"] = (','.join(st), f"{rows[i]['name']} {SLOTS[i]}→{SLOTS[j]}{how(rows[i], ti, pt_of(staff[i]))}, "
                                           f"{rows[j]['name']} {SLOTS[j]}→{SLOTS[i]}{how(rows[j], tj, pt_of(staff[j]))}")
        variants[book] = vs
        print(f"{book}: {len(vs)}개 (2군 후보 {len(bench_of(book, pit))}장, 볼 {balls}/{CAP[book]})")
    write_env(env_in, env_out, variants)
    json.dump(variants, open(json_out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)


def pt_of(tok):
    """The account's change a staff token carries ("@SP!" = 대성공 to SP) as position_training, 0 for none."""
    if '@' not in tok:
        return 0
    role = {v: k for k, v in MARK.items()}[tok.split('@')[1].rstrip('!')]
    return (role - 11) * 10 + (1 if tok.endswith('!') else 2)


def write_env(env_in, env_out, variants):
    kv, lines = {}, []
    for line in open(env_in, encoding='utf-8').read().rstrip('\n').split('\n'):
        if line.startswith('TCSIM_FA'):
            continue
        if '=' in line and not line.startswith('#'):
            k, v = line.split('=', 1)
            kv[k] = v
        lines.append(line)

    def entry(key, base):
        for part in kv[key].split(';'):
            if part.startswith(base + '='):
                return part[len(base) + 1:]
        raise SystemExit(f"{key}: no {base}")

    rows = []
    pit, _, _ = tables()
    for book, vs in variants.items():
        base = F.LABEL[book] + '#지금'
        b = F.FINAL[book]['bats']
        d = entry('TCSIM_DEV', base)
        # The book's development is keyed by name; a staff token given as a card idx (2군 cards, swapped men) gets its name's.
        for lb, (st, _) in vs.items():
            here = set()
            for x in st.split(',') + b.split(','):
                t = x.split('@')[0]
                here.add(t)
                if t.isdigit() and t in pit:
                    here.add(pit[t]['name'])
            rows.append((lb, st, b, '|'.join(e for e in d.split('|') if e.split(':')[0] in here)))
    # Windows keeps an environment variable under 32,767 characters: 153 variants' TCSIM_DEV went past it and the whole value was
    # dropped (11-17 21:xx, every variant played with the generic development). So batches of CHUNK variants, one env file each.
    paths = []
    for k in range(0, len(rows), CHUNK):
        part = rows[k:k + CHUNK]
        out = []
        for line in lines:
            if line.startswith('#'):
                line = f'# rule3 (11-17 육성표 규칙 3: 투수진 탐색) {k // CHUNK + 1}'
            elif line.startswith('TCSIM_DECKS='):
                line = 'TCSIM_DECKS=' + ';'.join(r[0] for r in part)
            elif line.startswith('TCSIM_STAFF='):
                line = 'TCSIM_STAFF=' + ';'.join(f"{r[0]}={r[1]}" for r in part)
            elif line.startswith('TCSIM_BATS='):
                line = 'TCSIM_BATS=' + ';'.join(f"{r[0]}={r[2]}" for r in part)
            elif line.startswith('TCSIM_DEV='):
                line = 'TCSIM_DEV=' + ';'.join(f"{r[0]}={r[3]}" for r in part)
            if len(line) > 30000:
                raise SystemExit(f"{line[:20]}: {len(line)} characters, over the environment limit")
            out.append(line)
        path = env_out.replace('.txt', f'_{k // CHUNK + 1}.txt')
        open(path, 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
        paths.append(path)
    print(len(rows), 'variants in', len(paths), 'files:', ' '.join(os.path.basename(p) for p in paths))


CHUNK = 18


def results(log):
    """{label: win%} from one log or a glob ("rule3_wide_*.log"); a variant without the book's development (no "육성표 숫자"
    line before its result) is left out."""
    import glob
    out = {}
    for path in sorted(glob.glob(log)):
        raw = open(path, 'rb').read()
        try:
            text = raw.decode('utf-8')
        except UnicodeDecodeError:
            text = raw.decode('cp949', errors='replace')
        dev = set()
        for line in text.splitlines():
            d = re.search(r'tcsim entry: (\S+ \S+#\S+) 육성표 숫자', line)
            if d:
                dev.add(d.group(1))
            m = re.search(r'tcsim entry: (\S+ \S+#\S+) .*?리그 평균 ([\d.]+)%', line)
            if m and m.group(1) in dev:
                out[m.group(1)] = float(m.group(2))
    return out


def ranked(variants, res):
    for book, vs in variants.items():
        yield book, sorted(((res[lb], lb, vs[lb]) for lb in vs if lb in res), key=lambda x: -x[0])


def rank(json_path, log, n):
    variants = json.load(open(json_path, encoding='utf-8'))
    res = results(log)
    for book, rs in ranked(variants, res):
        base = next((w for w, lb, _ in rs if lb.endswith('r3_base')), None)
        print(f"== {book}: {len(rs)}/{len(variants[book])}개, 최종안 {base}")
        for w, lb, (st, desc) in rs[:n]:
            print(f"  {w:.2f}% {lb.split('#')[1]:8} {desc} | {st}")


def close(json_path, log, env_in, env_out, json_out, n):
    variants = json.load(open(json_path, encoding='utf-8'))
    res = results(log)
    keep = {}
    for book, rs in ranked(variants, res):
        top = [lb for _, lb, _ in rs[:n]]
        base = next(lb for lb in variants[book] if lb.endswith('r3_base'))
        if base not in top:
            top.append(base)
        keep[book] = {lb: variants[book][lb] for lb in top}
    write_env(env_in, env_out, keep)
    json.dump(keep, open(json_out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)


# Two independent changes that each beat the final at 6,000 games, together (11-18 00:2x: 01롯데 신철인 81.92, 염종석 중계 81.91).
COMBO = {
    "01롯데": [("800101,박지철,손민한,박석진@SP!,강상수@SP!,가득염@RP,김사율,임경완,108234@RP,7108229,7106503",
               "신철인 셋업(가득염 1번 중계) + 2군 염종석 중계(포지션 변경, 40만)로 문동환 대신")],
}


def pen(json_path, log, env_in, env_out, json_out):
    variants = json.load(open(json_path, encoding='utf-8'))
    res = results(log)
    out = {}
    for book, rs in ranked(variants, res):
        w, lb, (st, desc) = rs[0]
        label = F.LABEL[book]
        vs = {}
        for n, (st2, desc2) in enumerate([(st, desc)] + COMBO.get(book, [])):
            staff = st2.split(',')
            for perm in itertools.permutations(range(4)):
                s2 = staff[:5] + [staff[5 + p] for p in perm] + staff[9:]
                tag = ''.join(str(p + 1) for p in perm)
                vs[f"{label}#pen{n}_{tag}"] = (','.join(s2), f"{desc2}; 중계 순서 {tag}")
        out[book] = vs
    write_env(env_in, env_out, out)
    json.dump(out, open(json_out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)


if __name__ == '__main__':
    mode = sys.argv[1]
    if mode == 'wide':
        wide(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])
    elif mode == 'rank':
        rank(sys.argv[2], sys.argv[3], int(sys.argv[4]) if len(sys.argv) > 4 else 8)
    elif mode == 'close':
        close(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], sys.argv[6], int(sys.argv[7]))
    elif mode == 'pen':
        pen(sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5], sys.argv[6])
