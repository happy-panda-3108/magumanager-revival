"""List a team-year's pitcher cards with role, ball, stats and 포지션 변경 targets (ma9m_pitcher_roles.txt)."""
import csv, sys
from collections import defaultdict
T = 'C:/Ma9MProject/server/data/tables/'
ROLE = {'12': 'SP', '13': 'RP', '14': 'SU', '15': 'CP'}
TEAM = {'롯데': '7', '삼성': '8', 'LG': '6', '쌍방울': '11', '현대': '4'}
allowed = defaultdict(set)
for r in csv.DictReader(open(T + 'ma9m_pitcher_roles.txt', encoding='utf-8'), delimiter='\t'):
    allowed[r['idx']].add(ROLE.get(r['allowed'], r['allowed']))
rows = list(csv.DictReader(open(T + 'P_PItcherTable.txt', encoding='utf-8'), delimiter='\t'))
for label in sys.argv[1:]:
    y, club = label.split(' ', 1)
    print('==', label)
    ps = [r for r in rows if r['year'] == y and r['teamcode'] == TEAM[club]]
    for r in sorted(ps, key=lambda r: (r['mainposition'], -int(r['ball']))):
        print(f"  {r['name']:6} {ROLE.get(r['mainposition'], r['mainposition'])} {r['ball']}볼 체{r['health']} 속{r['spd']} 컨{r['cont']} 테{r['technic']} 위{r['breakball']} 정{r['mental']} | 변경→{','.join(sorted(allowed[r['idx']])) or '-'} | G{r['game']} ERA{r['ERA']} HLD{r['HLD']} SV{r['SV']}")
