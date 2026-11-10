# Legend card candidates (engine session, 2026-11-08): career and peak WAR per franchise from the Statiz season tables.
import csv, glob, os, re, collections, sys

T = r'C:\Ma9MProject\statiz\표'
FR = [('롯데', ['롯데']), ('삼성', ['삼성']), ('LG', ['MBC', 'LG']), ('두산', ['OB', '두산']), ('KIA', ['해태', 'KIA']),
      ('한화', ['빙그레', '한화']), ('현대', ['삼미', '청보', '태평양', '현대']), ('쌍방울', ['쌍방울']), ('SSG', ['SK', 'SSG']),
      ('키움', ['우리', '히어로즈', '넥센', '키움']), ('NC', ['NC']), ('KT', ['KT'])]
TEAM2FR = {t: f for f, ts in FR for t in ts}


def num(x):
    try:
        return float(x)
    except ValueError:
        return 0.0


def load(kind):
    out = collections.defaultdict(list)  # p_no -> seasons
    for path in sorted(glob.glob(os.path.join(T, f'*_{kind}_합본.tsv'))):
        year = int(os.path.basename(path)[:4])
        with open(path, encoding='utf-8', newline='') as f:
            rows = list(csv.reader(f, delimiter='\t'))
        h = rows[0]
        ix = {k: h.index(k) for k in ('p_no', 'Name', 'team', 'pos', 'WAR')}
        amt = h.index('IP') if kind == '투구' else h.index('PA')
        for r in rows[1:]:
            if len(r) <= ix['WAR']:
                continue
            ip = r[amt]
            if kind == '투구' and '.' in ip:  # 284.2 = 284 2/3
                a, b = ip.split('.')
                ipv = int(a) + int(b) / 3
            else:
                ipv = num(ip)
            out[r[ix['p_no']]].append(dict(year=year, name=r[ix['Name']], team=r[ix['team']], pos=r[ix['pos']],
                                           war=num(r[ix['WAR']]), amt=ipv))
    return out


def summary(seasons, fr=None):
    s = [x for x in seasons if fr is None or TEAM2FR.get(x['team']) == fr]
    wars = sorted((x['war'] for x in s), reverse=True)
    return dict(n=len(s), war=sum(wars), peak5=sum(wars[:5]), first=min(x['year'] for x in s) if s else 0,
                last=max(x['year'] for x in s) if s else 0, amt=sum(x['amt'] for x in s))


if __name__ == '__main__':
    pit, bat = load('투구'), load('타격')
    last_year = 2026
    res = []
    for kind, data in (('투', pit), ('타', bat)):
        for pno, seasons in data.items():
            active = any(x['year'] == last_year for x in seasons)
            name = seasons[-1]['name']
            for fr, _ in FR:
                sm = summary(seasons, fr)
                if sm['n'] == 0:
                    continue
                res.append(dict(kind=kind, pno=pno, name=name, fr=fr, active=active, career=summary(seasons), **sm))
    with open(sys.argv[1] if len(sys.argv) > 1 else 'legend_candidates.tsv', 'w', encoding='utf-8') as f:
        f.write('구분\t구단\t이름\tp_no\t현역\t구단시즌\t구단WAR\t구단전성기5\t구단기간\t통산WAR\t통산전성기5\t통산기간\t이닝/타석(구단)\n')
        for r in sorted(res, key=lambda r: (r['fr'], r['kind'], -r['war'])):
            c = r['career']
            f.write(f"{r['kind']}\t{r['fr']}\t{r['name']}\t{r['pno']}\t{'현역' if r['active'] else ''}\t{r['n']}\t{r['war']:.1f}\t{r['peak5']:.1f}\t{r['first']}-{r['last']}\t{c['war']:.1f}\t{c['peak5']:.1f}\t{c['first']}-{c['last']}\t{r['amt']:.0f}\n")
    # per franchise top retired by franchise WAR
    for fr, _ in FR:
        for kind in ('투', '타'):
            top = sorted((r for r in res if r['fr'] == fr and r['kind'] == kind and not r['active']), key=lambda r: -r['war'])[:6]
            print(fr, kind, ' / '.join(f"{r['name']} {r['war']:.1f}({r['first']}-{r['last']})" for r in top))
