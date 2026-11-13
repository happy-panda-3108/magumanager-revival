"""2026-11-13 (user: "24롯데에 한현희 선발 완벽성공, 레전드 최동원 이대호를 넣은 라인업을 분석하고 육성표를 한 번 만들어봐"):
alloc.optimize for one club's real deck, keeping what its cards already have (재계약 so far, a 특강 taken, training slots used)
and planning only the rest.

  python alloc_owned.py <plan log> <template.xlsx> <club db> <user_code> <out.json> [tc set hcstat hcbonus pcstat pcbonus]

tc = 팀컬러 the 1군 will have, set = 코칭스태프 세트, hc/pc = 타격·투수코치 stat name (alloc.BAT / alloc.PIT) and bonus.
The output has alloc.py's shape (one deck) plus per player 'done' (educated / training / contract and the coach it matches),
so make_books.py can write the totals (done + recommended) and say what was done already.
"""
import json
import sqlite3
import sys

import alloc


def coach_of(ed, coaches, kind):
    """The 특강 coach (name, result) whose numbers are this card's educated values, or None."""
    for name, vec in coaches[kind].items():
        if [v or 0 for v in vec] == list(ed):
            return name
    return None


def main():
    log, template, dbpath, user, out_path = sys.argv[1:6]
    rest = sys.argv[6:]
    tc = int(rest[0]) if rest else 21
    setb = int(rest[1]) if len(rest) > 1 else 0
    hc = (alloc.BAT.index(rest[2]), int(rest[3])) if len(rest) > 3 and rest[2] in alloc.BAT else None
    pc = (alloc.PIT.index(rest[4]), int(rest[5])) if len(rest) > 5 and rest[4] in alloc.PIT else None
    coaches = alloc.load_coaches(template)
    # load_coaches keeps the +5 rows (main +5, subs +2); a +4 result is the same minus one on the main stat.
    coaches4 = {k: {n: [(x - 1 if x == 5 else x) for x in v] for n, v in d.items()} for k, d in coaches.items()}
    cards = alloc.load_cards()
    db = sqlite3.connect(f'file:{dbpath}?mode=ro', uri=True)
    owned = {r[0]: r[1:] for r in db.execute(
        'SELECT card_idx, educated, training, contract, position_training FROM team_card WHERE user_code = ?', (int(user),))}
    decks = alloc.parse_log(log)
    out = {}
    for label, deck in decks.items():
        year, club = label.split('#')[0].split(' ', 1)
        res, total = [], 0.0
        for p in deck['players']:
            kind = p['kind']
            row = cards.get((kind, year, str(alloc.TEAM[club]), p['name'])) or cards.get((kind, '1981', str(alloc.TEAM[club]), p['name']))
            idx = int(row['idx']) if row else None
            ed, tr, co, pti = owned.get(idx, (bytes(6), bytes(6), bytes(6), 0))
            ed, tr, co = [int.from_bytes(bytes([b]), 'big', signed=True) for b in ed], list(tr), list(co)
            fixed = [tc + setb] * 6
            stat = hc if kind == 'B' else pc
            if stat:
                fixed[stat[0]] += stat[1]
            for k in range(6):
                fixed[k] += ed[k] + tr[k] + co[k]
            renew_left = max(0, 6 - sum(co) // 3)
            train_left = max(0, 3 - sum(1 for x in tr if x))
            taken = None
            if any(ed):
                taken = coach_of(ed, coaches, kind)
                # The book's 특강 cell: the name alone = +5, "name +4" = a +4 result.
                taken = taken if taken else (coach_of(ed, coaches4, kind) or '?') + ' +4'
                opts = [(taken, [0] * 6)]
            else:
                opts = [(c, coaches[kind][c]) for c in p['coaches'] if c in coaches[kind]] or [(None, [0] * 6)]
            r = alloc.optimize(p, opts, fixed, train_left, 6, renew_left, 3)
            # Rows as the book shows them: 특강 = what the card has or the recommended coach's, 재계약 = done + recommended.
            r['lecture'] = ed if any(ed) else r['lecture']
            r['training'] = [tr[k] + r['training'][k] for k in range(6)]
            r['renewal'] = [co[k] + r['renewal'][k] for k in range(6)]
            r['done'] = {'educated': ed, 'training': tr, 'contract': co, 'coach': taken, 'renewals': sum(co) // 3}
            r['fixed'] = fixed
            r['idx'] = idx
            res.append(dict(p, **r))
            total += r['value']
        out[label.split('#')[0]] = {'balls': deck['balls'], 'hc': [(0, alloc.BAT[hc[0]])] if hc else [(0, '없음')],
                                    'pc': [(0, alloc.PIT[pc[0]])] if pc else [(0, '없음')], 'total': total, 'players': res,
                                    'settings': {'tc': tc, 'set': setb, 'hc': list(hc) if hc else None, 'pc': list(pc) if pc else None}}
        for p in res:
            names = alloc.BAT if p['kind'] == 'B' else alloc.PIT
            fmt = lambda v: ' '.join(f'{names[k]}{v[k]}' for k in range(6) if v[k])
            done = p['done']
            print(f"  {p['slot']:7} {p['name']:6} 특강 {p['coach']}{' (받음)' if done['coach'] else ''} | 트레이닝 {fmt(p['training'])} | "
                  f"재계약 {fmt(p['renewal'])} (한 것 {done['renewals']}번) | 표시 {p['shown']} | +{p['value']:.2f}%p")
    json.dump(out, open(out_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)


if __name__ == '__main__':
    main()
