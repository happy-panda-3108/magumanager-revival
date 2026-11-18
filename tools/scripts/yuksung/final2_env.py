"""2026-11-18 06:xx: after the rule-3 search (rule3.py wide → close → pen), the last checks before the books are rebuilt.

  python final2_env.py

- q01: 01롯데 at 6,000 games — 신철인 셋업 안 (81.92), 2군 염종석 중계 안 (81.91), the two together (pen 81.95 at 1,500).
- final2_plan_env.txt: plan (+9) for the slots the search changed that no plan measured yet, with every SP measured where the
  rotation changed (선발 등판 승률 for rule 4: "선발 순서를 작성할 때 가장 높은 승률의 선발부터 차례대로").
"""
import json
import os

import final_1117 as F
import rule3 as R

HERE = os.path.dirname(os.path.abspath(__file__))
H01 = "800101,박지철,손민한,박석진@SP!,강상수@SP!"
Q01 = {"01롯데": {
    "2001 롯데#q_sin": (f"{H01},가득염@RP,김사율,임경완,문동환,7108229,7106503", "신철인 셋업, 가득염 1번 중계"),
    "2001 롯데#q_yeom": (f"{H01},7108241,김사율,임경완,108234@RP,가득염,7106503", "이병석 1번 중계 + 염종석 중계로 문동환 대신"),
    "2001 롯데#q_both": (f"{H01},가득염@RP,김사율,임경완,108234@RP,7108229,7106503", "신철인 셋업 + 염종석 중계"),
}}
PLAN = {
    "2001 현대": ("마일영,800107,임선동,테일러A,108245@SP,권준헌,101332@RP,박장희,이상열,신철인,위재영",
                F.FINAL["01현대"]["bats"], "B0,SP1,SP5,RP2"),
    "2011 롯데": ("800101,장원준,사도스키,코리@SP!,송승준,105761@RP,진명호,고원준@RP,105791,임경완,7105705",
                F.FINAL["11롯데"]["bats"], "B0,RP1"),
    "1997 쌍방울": ("김원형,성영재,오상민@SP!,오봉옥@SP!,김기덕,최정환,800108@RP,정수찬,김민국,김현욱,7106579",
                 F.FINAL["97쌍방울"]["bats"], "B0,SP1,SP4,RP2,CP"),
    "2001 롯데": (f"{H01},가득염@RP,김사율,임경완,108234@RP,7108229,7106503", F.FINAL["01롯데"]["bats"], "B0,RP1,RP4,SU"),
}


def main():
    R.write_env(os.path.join(HERE, "fa_then_env.txt"), os.path.join(HERE, "q01_env.txt"), Q01)
    lines = ["# final2_plan_env.txt (11-18 06:xx: 규칙 3 탐색 뒤 새로 바뀐 자리, 선발 등판 승률)",
             "TCSIM_DECKS=" + ";".join(PLAN),
             "TCSIM_STAFF=" + ";".join(f"{k}={v[0]}" for k, v in PLAN.items()),
             "TCSIM_BATS=" + ";".join(f"{k}={v[1]}" for k, v in PLAN.items()),
             "TCSIM_PLAN_SLOTS=" + ";".join(f"{k}={v[2]}" for k, v in PLAN.items()),
             "TCSIM_LECTURE_MAIN=5", "TCSIM_PLAN_COACHES=0", "TCSIM_COACH_SET=3", "MA9M_FIELD_ENGINE=1", ""]
    open(os.path.join(HERE, "final2_plan_env.txt"), "w", encoding="utf-8", newline="\n").write("\n".join(lines))
    print("ok")


if __name__ == "__main__":
    main()
