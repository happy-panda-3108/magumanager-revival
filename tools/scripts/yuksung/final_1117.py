"""2026-11-17 최종 육성표 (user 14:0x via the systems session: "선수들의 포지션변경, 선발이 중계로 가면서 진행되는 육성법 변경, fa영입
등을 다 고려한 최종 육성표를 전체적으로 뽑아줘. 영향이 없는 선수들은 내가 계산한 육성표 그대로 쓰는걸로 해줘. … 1110_육성표랑
1113_24롯데인가 그거만 참고하면 될 것 같네.").

  python final_1117.py plan  <plan env out>                 the changed slots of the final rosters (run_legend_plan.ps1)
  python final_1117.py alloc <plan log> <alloc json out>    특강·트레이닝·재계약 for them with each book's own settings
  python final_1117.py spec  <alloc json> <spec dir>        book_edit.ps1 specs: only the changed blocks are written

Final roster per book: two FA cards (the 1군 holds two) and the moves they bring, from the FA runs of 11-17 (fa_pairs.log one-move
pairs, fa_then.log chains up to three moves, fa_final*.log the accounts as they stand). Accounts as they stand where the club plays
the deck (SKY마토 01롯데, SKY해민 25LG = the book, 최약체구단S 97쌍방울, SKY준우 24롯데); the book's 1군 elsewhere (12삼성 SKY승환 and
01현대 행복한현대 have lapsed 1군 contracts and lack the books' 레전드; 11롯데, 93롯데, 96쌍방울 are no club's deck now).
Blocks: "fa" = a new block for an FA card (card table + plan; FA 재계약 is +5 a time, 6 times), "replan" = the user's block with
특강/트레이닝/재계약 planned again in the new role, "move" = the user's block as it is in another spot. Every other block is left
exactly as the user saved it (values, colours, notes).
"""
import json
import os
import re
import sys

import alloc
import book_spec as b

sys.stdout.reconfigure(encoding="utf-8")
SRC = r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드"
SRC24 = r"C:\Ma9MProject\docs\육성표\한셀_1113_24롯데\육성표_1113_24롯데.cell"
OUT = r"C:\Ma9MProject\docs\육성표\한셀_1117_최종"
COACH_XLSX = r"C:\Ma9MProject\docs\육성표\1107\육성표_1107_{}.xlsx"
COACH24 = r"C:\Ma9MProject\docs\육성표\1113_24롯데\육성표_1113_24롯데.xlsx"
MARK = " ★11/17"
TAG = "11/17"                    # the date in notes and titles
BASE = "육성표_1117_{}_최종"

LABEL = {"01롯데": "2001 롯데", "11롯데": "2011 롯데", "93롯데": "1993 롯데", "25LG": "2025 LG", "12삼성": "2012 삼성",
         "97쌍방울": "1997 쌍방울", "96쌍방울": "1996 쌍방울", "01현대": "2001 현대", "24롯데": "2024 롯데"}


def src_of(book):
    return SRC24 if book == "24롯데" else os.path.join(SRC, f"육성표_1110_{book}_레전드.cell")


# book → staff (SP1-5, RP1-4, SU, CP), bats (batting order), plan slots, basis, win%, changes [(kind, block now there, who, how,
# slot text, why)]. `who` = an FA card idx or a name in the book.
FINAL = {
    "25LG": dict(
        staff="800103@SP!,치리노스,손주영,임찬규,송승기,김진성@RP!,유영찬@RP,이지강,이정용,김영우@SU!,7162576",
        bats="박해민@CF,신민재@2B,오스틴@1B,800203@LF,박동원@C,7296232@DH,문성주@RF,구본혁@3B,오지환@SS",
        slots="RP2,CP,B6", basis="SKY해민 1군 = 책", win="82.71 → 85.66",
        changes=[("P", "유영찬", "7162576", "fa", "CP · FA", "유영찬 대신 마무리"),
                 ("P", "장현식", "유영찬", "replan", "RP2 (마무리→중계)", "조병현이 마무리로 오며 중계로 포지션 변경(40만 MP, 성공 기준), 장현식 대신"),
                 ("B", "김현수A", "7296232", "fa", "6번 DH · FA", "김현수A 대신 지명")]),
    "12삼성": dict(
        staff="배영수,7106162,장원삼,윤성환,탈보트,권오준,심창민,권혁,차우찬,안지만,800102",
        bats="800202@DH,박한이@RF,박석민@3B,최형우@LF,이승엽@1B,진갑용@C,조동찬@2B,정형식@CF,7206694@SS",
        slots="SP2,B9", basis="책 1군(SKY승환은 1군 계약이 끝나 가고 레전드가 없어 책 기준)", win="77.38 → 79.99",
        changes=[("P", "장원삼", "7106162", "fa", "SP2 · FA", "2선발; 장원삼 3선발, 윤성환 4선발, 고든 빠짐"),
                 ("P", "윤성환", "장원삼", "move", "SP3", "윤석민이 2선발로 오며 한 칸 뒤로(같은 선발이라 육성 그대로)"),
                 ("P", "고든", "윤성환", "move", "SP4", "한 칸 뒤로(같은 선발이라 육성 그대로); 고든 빠짐"),
                 ("B", "김상수B", "7206694", "fa", "9번 SS · FA", "김상수B 대신 유격")]),
    "01현대": dict(
        staff="마일영,800107,임선동,테일러A,전준호B,권준헌,송신영,박장희,이상열,신철인,위재영",
        bats="7202625@SS,전준호A@LF,800207@CF,심정수@RF,박경완@C,이숭용@1B,7209350@3B,박진만@DH,박종호@2B",
        slots="B1,B7,B8", basis="책 1군(행복한현대는 1군 계약이 끝나 책 기준)", win="72.69 → 79.54",
        changes=[("B", "박진만", "7202625", "fa", "1번 SS · FA", "박진만 대신 유격"),
                 ("B", "퀸란", "7209350", "fa", "7번 3B · FA", "퀸란 대신 3루"),
                 ("B", "필립스", "박진만", "replan", "8번 DH (유격→지명)", "유지현에게 유격을 내주고 지명으로(필립스 대신); 수비를 안 쓰니 육성을 다시 계산")]),
    "11롯데": dict(
        staff="800101,장원준,사도스키,코리@SP!,송승준,강영식,진명호,고원준@RP,김일엽,임경완,7105705",
        bats="전준우@CF,김주찬@LF,이대호@1B,강민호@C,손아섭@RF,황재균@3B,홍성흔@DH,7206141@2B,문규현@SS",
        slots="CP,B8", basis="책 1군(지금 쓰는 구단 없음)", win="69.73 → 75.31",
        changes=[("P", "김사율", "7105705", "fa", "CP · FA", "김사율 대신 마무리"),
                 ("B", "조성환", "7206141", "fa", "8번 2B · FA", "조성환 대신 2루")]),
    "93롯데": dict(
        staff="윤형배,800101,윤학길,김상현A,염종석,김태석,이상번,서호진@RP,가득염,김태형B,7106775",
        bats="7228843@2B,전준호A@CF,800201@DH,김민호A@1B,김응국@LF,공필성@3B,박계원@SS,이종운@RF,강성우@C",
        slots="RP3,CP,B1", basis="책 1군(지금 쓰는 구단 없음)", win="70.76 → 76.86",
        changes=[("P", "서호진", "7106775", "fa", "CP · FA", "서호진 대신 마무리"),
                 ("P", "김정훈A", "서호진", "replan", "RP3 (마무리→중계)", "조규제가 마무리로 오며 중계로 포지션 변경(40만 MP, 성공 기준), 김정훈A 대신"),
                 ("B", "김민재", "7228843", "fa", "1번 2B · FA", "김민재 대신 2루")]),
    # Accounts as they stand (fa_final1.log / fa_final2.log, 11-17 15:xx). Within 0.1%p the pick that needs no new 포지션 변경.
    "01롯데": dict(
        staff="800101,박지철,손민한,박석진@SP!,강상수@SP!,7108241,김사율,임경완,문동환,가득염,7106503",
        bats="김주찬@DH,박현승@3B,호세@LF,800201@1B,조경환@RF,최기문@C,김민재@SS,박정태A@2B,김대익@CF",
        slots="SP4,RP1,CP", basis="SKY마토 1군 그대로(위재영 보유, 박석진·강상수 선발 대성공, 염종석 빠짐)",
        win="지금 1군 80.29 → 82.13",
        changes=[("P", "박석진", "7106503", "fa", "CP · FA", "계정이 이미 영입해 마무리로 쓰는 FA"),
                 ("P", "강상수", "박석진", "replan", "SP4 (마무리→선발·대성공)", "계정에서 선발로 포지션 변경(대성공)해 4선발로 쓰는 중"),
                 ("P", "염종석", "강상수", "move", "SP5 (중계→선발·대성공)", "계정대로 5선발; 염종석은 1군에서 빠짐"),
                 ("P", "김사율", "7108241", "fa", "RP1 · FA", "1번 중계; 김사율 2번, 임경완 3번 중계, 김영수 빠짐(신철인 셋업 안 82.17%와 같은 수준이고 포지션 변경이 필요 없음)"),
                 ("P", "김영수", "김사율", "move", "RP2", "이병석이 1번 중계로 오며 한 칸 뒤로(같은 중계라 육성 그대로)")]),
    "97쌍방울": dict(
        staff="김원형,성영재,오상민@SP!,7108506,김기덕,최정환,오봉옥@SP!,정수찬,김민국,김현욱,800108",
        bats="최태원@2B,김기태A@1B,7207147@3B,심성보@RF,박경완@C,조원우@LF,김광림@DH,김호@SS,김실@CF",
        slots="SP3,SP4,RP2,B3", basis="최약체구단S 1군 그대로(오상민·오봉옥 선발 대성공)", win="66.92 → 73.28",
        changes=[("B", "김성래", "7207147", "fa", "3번 3B · FA", "김성래 대신 3루"),
                 ("P", "오봉옥", "7108506", "fa", "SP4 · FA", "오봉옥 대신 4선발(김경원 마무리 안 73.31%와 같은 수준이고 조규제 포지션 변경이 필요 없음)"),
                 ("P", "정수찬", "오봉옥", "replan", "RP2 (선발 대성공 카드, 역할 밖)",
                  "정민태에게 4선발을 내주고 2번 중계로; 선발로 바뀐 카드라 중계에서는 역할 밖으로 뛰고, 초기화권(80만)으로 되돌리는 안은 재지 않음"),
                 ("P", "김민국", "정수찬", "move", "RP3", "한 칸 뒤로(같은 중계라 육성 그대로)"),
                 ("P", "윤형배", "김민국", "move", "RP4", "한 칸 뒤로(같은 중계라 육성 그대로); 윤형배 빠짐"),
                 ("P", "오상민", "오상민", "replan", "SP3 (중계→선발·대성공)", "계정의 포지션 변경 결과가 대성공(책은 성공 기준)")]),
    "24롯데": dict(
        staff="윌커슨,800101,반즈B,박세웅,7161069,김원중@RP,한현희,전미르,구승민,김상수A,7161073",
        bats="나승엽@1B,황성빈@LF,800201@DH,레이예스B@RF,손호영@3B,고승민@2B,윤동희@CF,박승욱@SS,손성빈@C",
        slots="SP5,RP1,RP2,CP", basis="SKY준우 1군 그대로(원태인 보유, 한현희 초기화로 중계, 김강현 빠짐)",
        win="원태인 없이 66.29 → 72.33",
        changes=[("P", "한현희", "7161069", "fa", "SP5 · FA", "계정이 이미 영입해 5선발로 쓰는 FA"),
                 ("P", "김원중", "7161073", "fa", "CP · FA", "김원중 대신 마무리"),
                 ("P", "박진", "김원중", "replan", "RP1 (마무리→중계)", "주현상이 마무리로 오며 중계로 포지션 변경(40만 MP, 성공 기준), 박진 대신. 김원중을 빼는 안은 72.02%"),
                 ("P", "구승민", "한현희", "replan", "RP2 (원래 보직 중계)", "계정에서 포지션 변경을 초기화해 원래 보직 중계로 2번 중계를 맡는 중"),
                 ("P", "김강현", "구승민", "move", "RP4", "계정대로 4번 중계; 김강현은 1군에서 빠짐")]),
    "96쌍방울": dict(
        staff="성영재,7108535,김원형,박성기,오봉옥,최정환,박주언,김현욱,김민국,김기덕,조규제",
        bats="김광림@CF,김실@RF,800208@1B,박경완@C,심성보@LF,석수철@3B,7205065@DH,최태원@2B,김호@SS",
        slots="SP2,B7", basis="책 1군(지금 쓰는 구단 없음)", win="74.33 → 79.78",
        changes=[("P", "오봉옥", "7108535", "fa", "SP2 · FA", "2선발; 오봉옥 5선발, 박진석 빠짐"),
                 ("P", "박진석", "오봉옥", "move", "SP5", "이강철이 2선발로 오며 5선발로(같은 선발이라 육성 그대로); 박진석 빠짐"),
                 ("B", "조원우", "7205065", "fa", "7번 DH · FA", "조원우 대신 지명")]),
}


def plan(path, books=None):
    sel = {k: v for k, v in FINAL.items() if not books or k in books}
    decks = [LABEL[k] for k in sel]
    lines = ["# 2026-11-17 최종 육성표: plan (+9) for the changed slots of the final rosters (final_1117.py plan)",
             "TCSIM_DECKS=" + ";".join(decks),
             "TCSIM_STAFF=" + ";".join(f"{LABEL[k]}={v['staff']}" for k, v in sel.items()),
             "TCSIM_BATS=" + ";".join(f"{LABEL[k]}={v['bats']}" for k, v in sel.items()),
             # "B0" = no batter: without a B<n> slot the plan measures every batter (11-17: 01롯데/24롯데 cost 18 extra slots).
             "TCSIM_PLAN_SLOTS=" + ";".join(f"{LABEL[k]}={v['slots'] if 'B' in v['slots'] else 'B0,' + v['slots']}" for k, v in sel.items()),
             "TCSIM_LECTURE_MAIN=5", "TCSIM_PLAN_COACHES=0", "TCSIM_COACH_SET=3", "MA9M_FIELD_ENGINE=1", ""]
    open(path, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
    print(path, len(decks), "decks")


def settings(book):
    cells, _ = b.read_book(src_of(book))
    tc, setb = int(cells.get('O2') or 0), int(cells.get('S2') or 0)
    hc = (alloc.BAT.index(cells['S3']), int(cells.get('S4') or 0)) if cells.get('S3') in alloc.BAT else None
    pc = (alloc.PIT.index(cells['S5']), int(cells.get('S6') or 0)) if cells.get('S5') in alloc.PIT else None
    return tc, setb, hc, pc


def fa_names(book):
    cards = b.load_cards()
    return {cards[c[2]]['name']: c[2] for c in FINAL[book]['changes'] if c[3] == 'fa'}


def do_alloc(log, out_path):
    book_of = {v: k for k, v in LABEL.items()}
    out = json.load(open(out_path, encoding='utf-8')) if os.path.exists(out_path) else {}
    for label, deck in alloc.parse_log(log).items():
        book = book_of[label.split('#')[0]]
        tc, setb, hc, pc = settings(book)
        coaches = alloc.load_coaches(COACH24 if book == "24롯데" else COACH_XLSX.format(book))
        fas = fa_names(book)
        for p in deck['players']:
            fixed = [tc + setb] * 6
            stat = hc if p['kind'] == 'B' else pc
            if stat:
                fixed[stat[0]] += stat[1]
            opts = [(c, coaches[p['kind']][c]) for c in p['coaches'] if c in coaches[p['kind']]] or [(None, [0] * 6)]
            fa = p['name'] in fas
            r = alloc.optimize(p, opts, fixed, 3, 6, 6, 5 if fa else 3)
            out.setdefault(book, {})[p['name']] = dict(p, **r, fixed=fixed, fa=fa)
            names = alloc.BAT if p['kind'] == 'B' else alloc.PIT
            fmt = lambda v: ' '.join(f'{names[k]}{v[k]}' for k in range(6) if v[k])
            print(f"{book} {p['slot']} {p['name']}{' (FA)' if fa else ''} 특강 {r['coach']} | 트레이닝 {fmt(r['training'])} | "
                  f"재계약 {fmt(r['renewal'])} | 표시 {r['shown']} | +{r['value']:.2f}%p")
    json.dump(out, open(out_path, 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)


def write_block(values, fills, fonts, notes, kind, r, blk, slot, comment):
    cols = b.BAT_COLS if kind == 'B' else b.PIT_COLS
    grade = blk['grade']
    values += [[f"{cols[0]}{r}", f"{grade} {'타자' if kind == 'B' else '투수'}"], [f"{cols[1]}{r}", b.GRADE_EN[grade]],
               [f"{cols[6]}{r}", slot], [f"{cols[0]}{r + 1}", blk['name']], [f"{cols[0]}{r + 3}", blk['coach'] or None]]
    for j in range(6):
        values += [[f"{cols[1 + j]}{r + 2}", blk['base'][j]], [f"{cols[1 + j]}{r + 4}", b.num(blk['training'][j])],
                   [f"{cols[1 + j]}{r + 5}", b.num(blk['renewal'][j])]]
    b.paint(fills, fonts, cols, r, grade)
    notes.append([f"{cols[0]}{r + 1}", comment])


def strip(text):
    # 한셀 puts its author line ("honey:") in front of every comment it saves; written back as is it would stack.
    return re.sub(r'^[^\n:]{1,30}:\n', '', text or '')


def change_text(names, base, card):
    moved = [(names[k], card[k] - base[k]) for k in range(6) if card[k] is not None and base[k] is not None and card[k] != base[k]]
    return ', '.join(f"{n} {d:+d}" for n, d in moved)


def spec(al, spec_dir, books=None):
    cards = b.load_cards()
    os.makedirs(spec_dir, exist_ok=True)
    for book, conf in FINAL.items():
        if books and book not in books:
            continue
        path = src_of(book)
        cells, comments = b.read_book(path)
        values, fills, fonts, notes, extra, report = [], [], [], [], [], []
        old = {k: b.blocks(cells, comments, k) for k in "BP"}
        for kind, at, who, how, slot, why in conf['changes']:
            k = next(i for i, x in enumerate(old[kind]) if x['name'] == at)
            r = b.FIRST + k * b.STRIDE
            names = b.BAT if kind == 'B' else b.PIT
            if how == 'fa':
                c = cards[who]
                a = al[book][c['name']]
                blk = dict(name=c['name'], grade=b.GRADE[c['cardgrade']], base=b.card_base(c), coach=a['coach'] or '',
                           training=a['training'], renewal=a['renewal'])
                comment = (f"{TAG} 최종: FA '{c['year'][2:]} {c['name']} 영입 — {why}. {conf.get('fa_note') or '덱 시즌 국내 FA 중 두 명 짝 1위(' + conf['win'] + '%)'}. "
                           f"재계약은 FA 규칙대로 한 번에 +5(6번)라 이 칸의 재계약 줄은 그 값입니다. 특강·트레이닝·재계약은 이 자리에서 새로 잰 값(20만 경기)으로 배분했습니다.")
                src = f"FA {c['name']}({who}) 새 칸: 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"
            else:
                mine = next(x for x in old[kind] if x['name'] == who)
                blk = dict(mine, grade=(mine['tag'] or '').split(' ')[0])
                if how == 'replan':
                    a = al[book][who]
                    blk.update(coach=a['coach'] or '', training=a['training'], renewal=a['renewal'])
                    moved = change_text(names, blk['base'], a['card']) if kind == 'P' else ''
                    comment = (f"{TAG} 최종: {why}. 새 보직 기준으로 특강·트레이닝·재계약을 다시 계산했습니다(20만 경기)."
                               + (f" 기본은 포지션 변경 전 카드값이고, 바뀐 보직에서는 {moved}." if moved else "")
                               + " 이미 한 재계약 보너스는 되돌릴 수 없으니, 한 것은 그대로 두고 남은 것만 이 칸대로 하세요.")
                    src = f"{who} 다시 계산: 특강 {a['coach']} 트레이닝 {a['training']} 재계약 {a['renewal']}"
                else:
                    comment = f"{TAG} 최종: {why}." + (" " + strip(mine['comment']) if mine.get('comment') else "")
                    src = f"{who} 자리만 옮김(숫자 그대로)"
            write_block(values, fills, fonts, notes, kind, r, blk, slot + MARK, comment)
            report.append(f"  {kind} {at} 칸 → {src} [{slot}]")
        title = cells.get('B2') or ''
        values.append(['B2', title + f' · 최종 {TAG}(FA·포지션 변경, ★ 칸만 바뀜)'])
        # 스탯 가치: the newly measured players under the rows already there.
        stat = sheet_rows(path, '스탯 가치')
        row = 34
        while stat.get(f"A{row}") not in (None, ""):
            row += 1
        # Only the players this book's changes planned (a plan without a B<n> slot measures every batter too).
        wanted = {cards[c[2]]['name'] if c[3] == 'fa' else c[2] for c in conf['changes'] if c[3] != 'move'}
        for name, a in al.get(book, {}).items():
            if name not in wanted:
                continue
            nm = alloc.BAT if a['kind'] == 'B' else alloc.PIT
            shown = ' / '.join(f"{nm[k]} {a['shown'][k]}" for k in range(6) if a['shown'][k] is not None and a['g9'][k] is not None)
            line = [a['slot'] + f' ({TAG})', name + (' (FA)' if a.get('fa') else ''), a['ball']] + [a['g9'][k] for k in range(6)] + [a['coach'] or '', shown]
            extra += [['스탯 가치', f"{'ABCDEFGHIJK'[j]}{row}", v] for j, v in enumerate(line)]
            row += 1
        base_name = BASE.format(book)
        s = {'src': path, 'out_dir': OUT, 'base': base_name, 'sheet': b.SHEET, 'values': values, 'fills': fills, 'fonts': fonts,
             'comments': notes, 'extra': extra}
        out = os.path.join(spec_dir, f"spec_{book}.json")
        json.dump(s, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
        print(f"== {book} ({conf['basis']}, {conf['win']}) → {out}")
        print("\n".join(report))


def sheet_rows(path, sheet):
    keep = b.SHEET
    b.SHEET = sheet
    try:
        cells, _ = b.read_book(path)
    finally:
        b.SHEET = keep
    return cells


if __name__ == '__main__':
    mode = sys.argv[1]
    if mode == 'plan':
        plan(sys.argv[2], sys.argv[3:])
    elif mode == 'alloc':
        do_alloc(sys.argv[2], sys.argv[3])
    elif mode == 'spec':
        spec(json.load(open(sys.argv[2], encoding='utf-8')), sys.argv[3], sys.argv[4:])
