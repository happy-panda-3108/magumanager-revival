"""merge_plan.py <base plan log> <new plan log> <out log>  (2026-11-07)

A deck re-measured on a new 포지션 변경 staff (TCSIM_PLAN_PITCHERS=1, optionally TCSIM_PLAN_SLOTS) keeps its batters from
the base log. Its pitchers follow the new log's 투수진 line: a slot measured in the new log takes that line, any other slot
takes the base log's line for the same pitcher with the slot renamed (a reliever moved from RP3 to RP2 keeps his values).
Decks only in the base log are copied as they are. Output is UTF-8; alloc.py reads either encoding."""
import re, sys

SLOT = r'(SP\d|RP\d|SU|CP)'


def read(path):
    raw = open(path, 'rb').read()
    try:
        text = raw.decode('utf-8')
    except UnicodeDecodeError:
        text = raw.decode('cp949', errors='replace')
    decks, order, cur = {}, [], None
    for line in text.splitlines():
        if 'tcsim plan:' not in line:
            continue
        body = line.split('tcsim plan: ', 1)[1]
        m = re.match(r'===== (.+?) \(', body)
        if m:
            cur = m.group(1)
            decks[cur] = {'head': line, 'lines': []}
            order.append(cur)
        elif cur and body.startswith(cur + ' '):
            decks[cur]['lines'].append(line)
    return decks, order


def rest(line, label):
    return line.split('tcsim plan: ', 1)[1][len(label):]


def pitcher_of(line, label):
    """(slot, name) of a measured pitcher line, else None."""
    m = re.match(r' \| ' + SLOT + r' (\S+?)(\(.*?\))? \d+볼', rest(line, label))
    return (m.group(1), m.group(2)) if m else None


def is_pitcher_side(line, label):
    return pitcher_of(line, label) is not None or re.match(r' (투수진|선발 등판|불펜)', rest(line, label)) is not None


base, order = read(sys.argv[1])
new, _ = read(sys.argv[2])
out = []
for label in order:
    d = base[label]
    if label not in new:
        out += [d['head']] + d['lines']
        continue
    n = new[label]
    staff_line = next(l for l in n['lines'] if rest(l, label).startswith(' 투수진'))
    staff = re.findall(SLOT + r' (\S+?)(?:\(.*?\))? \d+(?:,|$)', staff_line.split(' 투수진 | ', 1)[1])
    measured = {pitcher_of(l, label)[0]: l for l in n['lines'] if pitcher_of(l, label)}
    old_by_name = {pitcher_of(l, label)[1]: l for l in d['lines'] if pitcher_of(l, label)}
    pit, how = [], []
    for slot, name in staff:
        if slot in measured:
            pit.append(measured[slot]); how.append(f'{slot} {name} 새로 잼')
        elif name in old_by_name:
            l = old_by_name[name]
            old_slot = pitcher_of(l, label)[0]
            pit.append(l.replace(f' | {old_slot} {name}', f' | {slot} {name}', 1)); how.append(f'{slot} {name} 이전({old_slot})')
        else:
            raise SystemExit(f'{label} {slot} {name}: neither measured nor in the base log')
    extra = [l for l in n['lines'] if not pitcher_of(l, label) and is_pitcher_side(l, label)]
    out += [n['head']] + [l for l in d['lines'] if not is_pitcher_side(l, label)] + extra + pit
    print(label, '|', ', '.join(how))
open(sys.argv[3], 'w', encoding='utf-8', newline='\n').write('\n'.join(out) + '\n')
