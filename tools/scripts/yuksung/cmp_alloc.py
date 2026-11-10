import json, sys
BAT = ['컨택', '파워', '스피드', '선구', '수비', 'H정신']
PIT = ['체력', '구속', '컨트롤', '테크닉', '구위', 'P정신']
old = json.load(open('alloc_before.json', encoding='utf-8'))
new = json.load(open('alloc_new.json', encoding='utf-8'))
def fmt(p, vec):
    names = BAT if p['kind'] == 'B' else PIT
    return ' '.join(f'{names[k]}{vec[k]}' for k in range(6) if vec[k]) or '-'
for label, d in new.items():
    o = old.get(label)
    op = {(p['slot'], p['name']): p for p in o['players']} if o else {}
    nb = sum(p['value'] for p in d['players'] if p['kind'] == 'B'); npv = sum(p['value'] for p in d['players'] if p['kind'] == 'P')
    ob = sum(p['value'] for p in o['players'] if p['kind'] == 'B'); opv = sum(p['value'] for p in o['players'] if p['kind'] == 'P')
    print(f"## {label} | {o['balls']}볼→{d['balls']}볼 | 합 {o['total']:.2f}→{d['total']:.2f} (타자 {ob:.2f}→{nb:.2f}, 투수 {opv:.2f}→{npv:.2f}) | 타격코치 {o['hc'][0][1]}→{d['hc'][0][1]} 투수코치 {o['pc'][0][1]}→{d['pc'][0][1]}")
    for p in d['players']:
        q = op.get((p['slot'], p['name']))
        if not q:
            print(f"  {p['slot']} {p['name']} (이전 없음)"); continue
        ch = []
        if q['coach'] != p['coach']: ch.append(f"특강 {q['coach']}→{p['coach']}")
        if q['training'] != p['training']: ch.append(f"트레이닝 {fmt(q, q['training'])}→{fmt(p, p['training'])}")
        if q['renewal'] != p['renewal']: ch.append(f"재계약 {fmt(q, q['renewal'])}→{fmt(p, p['renewal'])}")
        print(f"  {p['kind']} {p['slot']} {p['name']} {q['value']:.2f}→{p['value']:.2f} | " + ('; '.join(ch) if ch else '같음'))
