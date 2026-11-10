"""2026-11-10 (user: "각 덱별로 투, 타 레전드 카드를 사용했을 때 가장 승률이 높은 로스터를 만들어줘 …"): entry variants with 레전드 cards.
Writes legend_env.txt (round 1: 레전드 batter and pitcher options measured apart, on the 11-07 staffs) next to this script.
Card tokens: a name of the deck's season or a card idx (레전드 800101-800299). TCSIM_BATS: nine token@spot in batting order,
a leading "*" = BestOrder orders them. Run: powershell -File run_entry.ps1 -Env legend_env.txt -Log legend1.log -Work <copy>."""
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))

# 11-07 staffs (plan_env.txt + plan_env_pc.txt): SP1-5, RP1-4, SU, CP.
STAFF = {
    "2025 LG": "손주영,치리노스,임찬규,송승기,에르난데스C,김진성@RP!,장현식,이지강,이정용,김영우@SU!,유영찬",
    "2012 삼성": "배영수,장원삼,윤성환,고든,탈보트,권오준,심창민,권혁,차우찬,안지만,오승환",
    "2001 롯데": "박지철,손민한,염종석,강상수@SP!,문동환@SP!,김사율,김영수,임경완,기론@RP!,가득염,박석진",
    "2011 롯데": "장원준,사도스키,고원준,송승준,코리@SP!,강영식,진명호,이재곤,김일엽,임경완,김사율",
    "1997 쌍방울": "김원형,김기덕,성영재,오봉옥@SP,오상민@SP,최정환,정수찬,김민국,윤형배,김현욱,조규제",
    "2001 현대": "임선동,마일영,전준호B,테일러A,송신영@SP,권준헌,박장희,이상열,김수경,신철인,위재영",
    "1993 롯데": "김상현A,염종석,윤형배,윤학길,가득염@SP,김태석,이상번,박동희,김정훈A,김태형B,서호진",
    "1996 쌍방울": "오봉옥,성영재,박성기,김원형,박진석,최정환,박주언,김현욱,김민국,김기덕,조규제",
}

# The books' nine (11-07, BestOrder of the deck) in batting order.
NINE = {
    "2025 LG": "박해민@CF,신민재@2B,오스틴@1B,문보경@DH,박동원@C,김현수A@LF,문성주@RF,구본혁@3B,오지환@SS",
    "2012 삼성": "박한이@RF,배영섭@DH,이승엽@1B,박석민@3B,최형우@LF,진갑용@C,조동찬@2B,정형식@CF,김상수B@SS",
    "2001 롯데": "김주찬@DH,박현승@3B,호세@LF,조경환@RF,얀A@1B,최기문@C,김민재@SS,박정태A@2B,김대익@CF",
    "2011 롯데": "전준우@CF,김주찬@LF,이대호@1B,강민호@C,손아섭@RF,황재균@3B,홍성흔@DH,조성환@2B,문규현@SS",
    "1997 쌍방울": "최태원@2B,김기태A@1B,김성래@3B,심성보@RF,박경완@C,조원우@LF,김광림@DH,김호@SS,김실@CF",
    "2001 현대": "박진만@SS,전준호A@LF,심정수@RF,박경완@C,퀸란@3B,이숭용@1B,박재홍@CF,필립스@DH,박종호@2B",
    "1993 롯데": "전준호A@CF,김종헌@DH,김응국@LF,김민재@2B,김민호A@1B,박계원@SS,이종운@RF,공필성@3B,강성우@C",
    "1996 쌍방울": "김실@RF,김광림@CF,박경완@C,김기태A@1B,심성보@LF,석수철@3B,조원우@DH,최태원@2B,김호@SS",
}


def swap(text, old, new):
    """Replace one comma item (exact, or the item whose name part is `old`)."""
    items = text.split(",")
    out, hit = [], 0
    for it in items:
        name = re.split(r"[@!]", it)[0]
        if it == old or name == old:
            out.append(new)
            hit += 1
        else:
            out.append(it)
    assert hit == 1, (old, text)
    return ",".join(out)


def staff(deck, *pairs):
    s = STAFF[deck]
    for old, new in pairs:
        s = swap(s, old, new)
    return s


def nine(deck, *pairs, auto=True):
    s = NINE[deck]
    for old, new in pairs:
        s = swap(s, old, new)
    return ("*" if auto else "") + s


# (label tag, bats or None, staff)
V = {}


def add(deck, tag, bats=None, st=None):
    V.setdefault(deck, []).append((tag.replace(",", "·"), bats, st or STAFF[deck]))


# 2001 현대 (행복한현대; owns 레전드 박재홍 800207 and '01 이명수, not 정민태 800107).
D = "2001 현대"
add(D, "현재")
add(D, "박재홍L7번", "박진만@SS,전준호A@LF,심정수@RF,박경완@C,퀸란@3B,이숭용@1B,800207@CF,필립스@DH,박종호@2B")
add(D, "박재홍L4번퀸란6번", "박진만@SS,전준호A@LF,심정수@RF,800207@CF,박경완@C,퀸란@3B,이숭용@1B,필립스@DH,박종호@2B")
add(D, "박재홍L4번박경완6번", "박진만@SS,전준호A@LF,심정수@RF,800207@CF,퀸란@3B,박경완@C,이숭용@1B,필립스@DH,박종호@2B")
add(D, "박재홍L3번퀸란6번", "박진만@SS,전준호A@LF,800207@CF,심정수@RF,박경완@C,퀸란@3B,이숭용@1B,필립스@DH,박종호@2B")
add(D, "박재홍L자동", "*박진만@SS,전준호A@LF,심정수@RF,800207@CF,박경완@C,퀸란@3B,이숭용@1B,필립스@DH,박종호@2B")
add(D, "박재홍L4번퀸란6번이명수DH", "박진만@SS,전준호A@LF,심정수@RF,800207@CF,박경완@C,퀸란@3B,이숭용@1B,박종호@2B,이명수@DH")
add(D, "박재홍L4번박경완6번이명수DH", "박진만@SS,전준호A@LF,심정수@RF,800207@CF,퀸란@3B,박경완@C,이숭용@1B,박종호@2B,이명수@DH")
add(D, "박재홍L자동이명수DH", "*박진만@SS,전준호A@LF,심정수@RF,800207@CF,박경완@C,퀸란@3B,이숭용@1B,박종호@2B,이명수@DH")
add(D, "이명수DH(레전드없음)", "박진만@SS,전준호A@LF,심정수@RF,박경완@C,퀸란@3B,이숭용@1B,박재홍@CF,박종호@2B,이명수@DH")
add(D, "정민태L(테일러A대신)", st=staff(D, ("테일러A", "800107")))
add(D, "정민태L(이상열대신,테일러A중계)", st="임선동,마일영,전준호B,800107,송신영@SP,권준헌,박장희,테일러A,김수경,신철인,위재영")
add(D, "정민태L(김수경대신,송신영중계)", st="임선동,마일영,전준호B,테일러A,800107,권준헌,송신영,박장희,이상열,신철인,위재영")
add(D, "정민태L(테일러A대신,변경없음)", st="임선동,마일영,전준호B,800107,김수경,권준헌,송신영,박장희,이상열,신철인,위재영")

# 1997 쌍방울 (최약체구단S; owns no 쌍방울 레전드).
D = "1997 쌍방울"
add(D, "현재")
add(D, "조규제L마무리", st=staff(D, ("조규제", "800108")))
add(D, "김기태L같은자리", nine(D, ("김기태A", "800208@1B"), auto=False))
add(D, "김기태L자동", nine(D, ("김기태A", "800208@1B")))
add(D, "김기태L자동조규제L", nine(D, ("김기태A", "800208@1B")), staff(D, ("조규제", "800108")))

D = "1996 쌍방울"
add(D, "현재")
add(D, "조규제L마무리", st=staff(D, ("조규제", "800108")))
add(D, "김기태L자동", nine(D, ("김기태A", "800208@1B")))
add(D, "김기태L자동조규제L", nine(D, ("김기태A", "800208@1B")), staff(D, ("조규제", "800108")))

# 2025 LG (SKY해민 owns 박용택 800203 and 김용수 800103, 김용수 선발 대성공).
D = "2025 LG"
add(D, "현재")
add(D, "박용택L좌익(김현수A빠짐)", nine(D, ("김현수A", "800203@LF")))
add(D, "박용택L좌익김현수A지명(문보경빠짐)", nine(D, ("문보경", "김현수A@DH"), ("김현수A@LF", "800203@LF")))
add(D, "박용택L지명(문보경빠짐)", nine(D, ("문보경", "800203@DH")))
add(D, "김용수L마무리", st=staff(D, ("유영찬", "800103")))
add(D, "김용수L선발대성공(에르난데스C대신)", st=staff(D, ("에르난데스C", "800103@SP!")))
add(D, "김용수L선발성공(에르난데스C대신)", st=staff(D, ("에르난데스C", "800103@SP")))
add(D, "김용수L중계(이정용대신)", st=staff(D, ("이정용", "800103@RP")))

# 2012 삼성 (SKY승환 owns neither).
D = "2012 삼성"
add(D, "현재")
add(D, "양준혁L지명(배영섭빠짐)", nine(D, ("배영섭", "800202@DH")))
add(D, "양준혁L우익박한이지명(배영섭빠짐)", nine(D, ("배영섭", "박한이@DH"), ("박한이@RF", "800202@RF")))
add(D, "양준혁L좌익최형우지명(배영섭빠짐)", nine(D, ("배영섭", "최형우@DH"), ("최형우@LF", "800202@LF")))
add(D, "양준혁L1루이승엽지명(배영섭빠짐)", nine(D, ("배영섭", "이승엽@DH"), ("이승엽@1B", "800202@1B")))
add(D, "오승환L마무리", st=staff(D, ("오승환", "800102")))

# 2001 롯데 (SKY마토 owns 오승환·박재홍·박경수 레전드, no 롯데 one).
D = "2001 롯데"
add(D, "현재")
add(D, "이대호L1루(얀A빠짐)", nine(D, ("얀A", "800201@1B")))
add(D, "이대호L지명(김주찬빠짐)", nine(D, ("김주찬", "800201@DH")))
add(D, "이대호L1루얀A지명(김주찬빠짐)", nine(D, ("김주찬", "얀A@DH"), ("얀A@1B", "800201@1B")))
add(D, "최동원L(문동환대신)", st=staff(D, ("문동환", "800101")))
add(D, "최동원L(강상수대신)", st=staff(D, ("강상수", "800101")))
add(D, "최동원L(염종석대신)", st=staff(D, ("염종석", "800101")))

D = "2011 롯데"
add(D, "현재")
add(D, "이대호L('11이대호대신)", nine(D, ("이대호", "800201@1B")))
add(D, "최동원L(코리대신)", st=staff(D, ("코리", "800101")))
add(D, "최동원L(사도스키대신)", st=staff(D, ("사도스키", "800101")))
add(D, "최동원L(고원준대신)", st=staff(D, ("고원준", "800101")))

D = "1993 롯데"
add(D, "현재")
add(D, "이대호L지명(김종헌빠짐)", nine(D, ("김종헌", "800201@DH")))
add(D, "이대호L1루김민호A지명(김종헌빠짐)", nine(D, ("김종헌", "김민호A@DH"), ("김민호A@1B", "800201@1B")))
add(D, "최동원L(가득염대신)", st=staff(D, ("가득염", "800101")))
add(D, "최동원L(김상현A대신)", st=staff(D, ("김상현A", "800101")))

# Round 2 (after legend1.log): the best batter and pitcher options together, and the 01현대 questions in detail.
V1, V = V, {}
D = "2001 현대"
H_AUTO = "박진만@SS,전준호A@LF,800207@CF,심정수@RF,박경완@C,이숭용@1B,퀸란@3B,필립스@DH,박종호@2B"   # round 1 "박재홍L자동"
H_MIN = "임선동,마일영,전준호B,테일러A,800107,권준헌,송신영,박장희,이상열,신철인,위재영"                 # 정민태L(김수경대신·송신영중계)
add(D, "박재홍L3번·퀸란5번·박경완7번", "박진만@SS,전준호A@LF,800207@CF,심정수@RF,퀸란@3B,이숭용@1B,박경완@C,필립스@DH,박종호@2B")
add(D, "심정수3번·박재홍L4번·박경완5번·퀸란7번", "박진만@SS,전준호A@LF,심정수@RF,800207@CF,박경완@C,이숭용@1B,퀸란@3B,필립스@DH,박종호@2B")
add(D, "박재홍L3번·박경완4번·심정수5번·퀸란7번", "박진만@SS,전준호A@LF,800207@CF,박경완@C,심정수@RF,이숭용@1B,퀸란@3B,필립스@DH,박종호@2B")
add(D, "박재홍L3번·심정수4번·필립스5번·박경완7번·퀸란8번", "박진만@SS,전준호A@LF,800207@CF,심정수@RF,필립스@DH,이숭용@1B,박경완@C,퀸란@3B,박종호@2B")
add(D, "박재홍L자동·김인호DH", "*박진만@SS,전준호A@LF,800207@CF,심정수@RF,박경완@C,이숭용@1B,퀸란@3B,박종호@2B,김인호@DH")
add(D, "박재홍L자동+정민태L(김수경대신·송신영중계)", H_AUTO, H_MIN)
add(D, "박재홍L자동+정민태L·김수경RP4(이상열빠짐)", H_AUTO, "임선동,마일영,전준호B,테일러A,800107,권준헌,송신영,박장희,김수경,신철인,위재영")
add(D, "박재홍L자동+정민태L·이명수DH", "*박진만@SS,전준호A@LF,800207@CF,심정수@RF,박경완@C,이숭용@1B,퀸란@3B,박종호@2B,이명수@DH", H_MIN)
D = "2025 LG"
L_KIM = staff(D, ("에르난데스C", "800103@SP!"))
add(D, "박용택L좌익김현수A지명+김용수L선발대성공", nine(D, ("문보경", "김현수A@DH"), ("김현수A@LF", "800203@LF")), L_KIM)
add(D, "박용택L지명+김용수L선발대성공", nine(D, ("문보경", "800203@DH")), L_KIM)
D = "2012 삼성"
add(D, "양준혁L지명+오승환L", nine(D, ("배영섭", "800202@DH")), staff(D, ("오승환", "800102")))
D = "2001 롯데"
add(D, "이대호L1루+최동원L(문동환대신)", nine(D, ("얀A", "800201@1B")), staff(D, ("문동환", "800101")))
add(D, "이대호L1루+최동원L(염종석대신)", nine(D, ("얀A", "800201@1B")), staff(D, ("염종석", "800101")))
D = "2011 롯데"
add(D, "최동원L·코리중계(김일엽빠짐)", st="장원준,사도스키,고원준,송승준,800101,강영식,진명호,이재곤,코리,임경완,김사율")
D = "1993 롯데"
add(D, "이대호L지명+최동원L(가득염대신)", nine(D, ("김종헌", "800201@DH")), staff(D, ("가득염", "800101")))
add(D, "이대호L지명+최동원L·가득염중계(김정훈A빠짐)", nine(D, ("김종헌", "800201@DH")), "김상현A,염종석,윤형배,윤학길,800101,김태석,이상번,박동희,가득염,김태형B,서호진")
add(D, "이대호L지명+최동원L(김상현A대신)", nine(D, ("김종헌", "800201@DH")), staff(D, ("김상현A", "800101")))
V2, V = V, V1


# Final rosters (after legend1.log / legend2.log): deck → (bats in batting order or None = the deck's own nine, staff,
# plan slots to measure = the new 레전드 cards; "B0" = no batter).
FINAL = {
    # 박재홍L 3번, 퀸란 7번 (66.82 → with 정민태L 68.62); 송신영 back to 중계 (RP2), 김수경 out.
    "2001 현대": (H_AUTO, H_MIN, "B3,SP5,RP2"),
    # 조규제L 마무리 only (59.65); 김기태L loses to the '97 엘리트 card.
    "1997 쌍방울": (None, staff("1997 쌍방울", ("조규제", "800108")), "B0,CP"),
    # 김기태L 3번 only (69.15); 조규제L loses to the '96 card.
    "1996 쌍방울": ("김광림@CF,김실@RF,800208@1B,박경완@C,심성보@LF,석수철@3B,조원우@DH,최태원@2B,김호@SS", STAFF["1996 쌍방울"], "B3"),
    # 박용택L 4번 좌익, 김현수A 지명, 김용수L 선발 대성공 (78.42).
    "2025 LG": ("박해민@CF,신민재@2B,오스틴@1B,800203@LF,박동원@C,김현수A@DH,문성주@RF,구본혁@3B,오지환@SS", L_KIM, "B4,SP5"),
    # 양준혁L 1번 지명, 오승환L 마무리 (73.86).
    "2012 삼성": ("800202@DH,박한이@RF,박석민@3B,최형우@LF,이승엽@1B,진갑용@C,조동찬@2B,정형식@CF,김상수B@SS", staff("2012 삼성", ("오승환", "800102")), "B1,CP"),
    # 이대호L 4번 1루 (얀A out), 최동원L for 문동환 (70.75).
    "2001 롯데": ("김주찬@DH,박현승@3B,호세@LF,800201@1B,조경환@RF,최기문@C,김민재@SS,박정태A@2B,김대익@CF", staff("2001 롯데", ("문동환", "800101")), "B4,SP5"),
    # 최동원L for 고원준 (63.62); 이대호L = the '11 card (no gain).
    "2011 롯데": (None, staff("2011 롯데", ("고원준", "800101")), "B0,SP3"),
    # 이대호L 3번 지명, 최동원L SP5, 가득염 back to 중계 (RP4), 김정훈A out (64.33).
    "1993 롯데": ("김민재@2B,전준호A@CF,800201@DH,김민호A@1B,김응국@LF,공필성@3B,박계원@SS,이종운@RF,강성우@C",
                "김상현A,염종석,윤형배,윤학길,800101,김태석,이상번,박동희,가득염,김태형B,서호진", "B3,SP5,RP4"),
}


def write_plan(path):
    decks, staffs, bats, slots = [], [], [], []
    for deck, (b, st, sl) in FINAL.items():
        decks.append(deck)
        staffs.append(f"{deck}={st}")
        if b:
            bats.append(f"{deck}={b}")
        slots.append(f"{deck}={sl}")
    lines = ["# 2026-11-10: plan (+9 gains) for the 레전드 slots of the final rosters (gen_legend_env.py plan)",
             "TCSIM_DECKS=" + ";".join(decks), "TCSIM_STAFF=" + ";".join(staffs), "TCSIM_BATS=" + ";".join(bats),
             "TCSIM_PLAN_SLOTS=" + ";".join(slots), "TCSIM_LECTURE_MAIN=5", "TCSIM_PLAN_COACHES=0", "TCSIM_COACH_SET=3",
             "MA9M_FIELD_ENGINE=1", ""]
    with open(path, "w", encoding="utf-8", newline=chr(10)) as f:
        f.write(chr(10).join(lines))
    print(path, len(decks), "decks")


def field():
    for line in open(os.path.join(HERE, "entry_env3.txt"), encoding="utf-8"):
        if line.startswith("TCSIM_FIELD="):
            return line.strip()
    raise SystemExit("no field")


def write(path, variants, note):
    decks, staffs, bats = [], [], []
    for deck, vs in variants.items():
        for tag, b, st in vs:
            label = f"{deck}#{tag}"
            decks.append(label)
            staffs.append(f"{label}={st}")
            if b:
                bats.append(f"{label}={b}")
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        f.write(f"# {note}\n")
        f.write(field() + "\n")
        f.write("TCSIM_DECKS=" + ";".join(decks) + "\n")
        f.write("TCSIM_STAFF=" + ";".join(staffs) + "\n")
        f.write("TCSIM_BATS=" + ";".join(bats) + "\n")
        f.write("TCSIM_ENTRY_BONUS=3\nTCSIM_LECTURE_MAIN=5\nMA9M_FIELD_ENGINE=1\n")
    print(path, len(decks), "variants")


if __name__ == "__main__":
    import sys
    if len(sys.argv) > 1 and sys.argv[1] == "plan":
        write_plan(os.path.join(HERE, "legend_plan_env.txt"))
    elif len(sys.argv) > 1 and sys.argv[1] == "2":
        write(os.path.join(HERE, "legend_env2.txt"), V2, "2026-11-10 round 2: best 레전드 batter + pitcher together, 01현대 cleanup/DH questions (gen_legend_env.py 2)")
    else:
        write(os.path.join(HERE, "legend_env.txt"), V, "2026-11-10 round 1: 레전드 batter / pitcher options per deck on the 11-07 staffs (gen_legend_env.py)")
