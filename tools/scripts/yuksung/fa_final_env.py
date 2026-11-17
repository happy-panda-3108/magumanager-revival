"""2026-11-17 14:3x (user 14:0x via systems: "선수들의 포지션변경, 선발이 중계로 가면서 진행되는 육성법 변경, fa영입 등을 다 고려한
최종 육성표를 전체적으로 뽑아줘"): the second FA picked again on the accounts as they stand, from fa_then_env.txt.

  python fa_final_env.py <env in> <env out> <deck> [<deck> …]

Per deck: the live 1군 staff (systems read it from the live DB, read-only), the first FA(s) and the second FAs that always play.
The second FA's other tries are screened by TeamValue (TCSIM_FA_THEN_KEEP best) as in the stage-2 run.
"""
import sys

FINAL = {
    # SKY마토 13:2x: 박석진·강상수 선발 대성공, 염종석 out, '01 위재영 CP (held).
    "2001 롯데#지금": dict(
        staff="800101,박지철,손민한,박석진@SP!,강상수@SP!,김사율,임경완,김영수,문동환,가득염,염종석",
        first=["7106503@CP"],
        also=["7108229@SU>RP1>RP2", "7108241@RP1>RP2", "7202025@DH", "7108226@SP5>RP3", "7201533@CF"]),
    # 최약체구단S 13:3x: as the book but 오상민·오봉옥 선발 대성공.
    "1997 쌍방울#지금": dict(
        staff="김원형,성영재,오상민@SP!,오봉옥@SP!,김기덕,최정환,정수찬,김민국,윤형배,김현욱,800108",
        first=["7207147@3B", "7101001@CP>RP2>RP3>RP4"],
        also=["7108506@SP4>RP2>RP3>RP4", "7108506@SP4>RP4", "7101001@CP>RP2>RP3>RP4", "7207145@DH", "7207148@3B"]),
    # SKY준우 14:1x: 한현희 reset (원래 중계) at RP2, 김강현 out, '24 원태인 SP5 (held); the staff's SP5 김강현 is the man 원태인 replaces.
    "2024 롯데#지금": dict(
        staff="윌커슨,800101,반즈B,박세웅,김강현,박진,한현희,전미르,구승민,김상수A,김원중",
        first=["7161069@SP5"],
        also=["7294199@SS", "7294596@SS", "7161073@CP>RP4", "7161073@CP"]),
}


def main():
    env_in, env_out = sys.argv[1:3]
    decks = sys.argv[3:] or list(FINAL)
    out = []
    for line in open(env_in, encoding="utf-8").read().rstrip("\n").split("\n"):
        if line.startswith("TCSIM_FA_THEN"):
            continue
        if line.startswith("#"):
            line = "# fa_final_env.txt (11-17 14:3x: 계정 그대로의 1군 위에서 둘째 FA, 최종 육성표용)"
        elif line.startswith("TCSIM_DECKS="):
            line = "TCSIM_DECKS=" + ";".join(decks)
        elif line.startswith("TCSIM_STAFF="):
            parts = line[len("TCSIM_STAFF="):].split(";")
            for d in decks:
                if FINAL[d].get("staff"):
                    parts = [(d + "=" + FINAL[d]["staff"]) if p.startswith(d + "=") else p for p in parts]
            line = "TCSIM_STAFF=" + ";".join(parts)
        out.append(line)
    out.append("TCSIM_FA_THEN=" + ";".join(f"{d}={'|'.join(FINAL[d]['first'])}" for d in decks))
    out.append("TCSIM_FA_THEN_ALSO=" + ";".join(f"{d}={','.join(FINAL[d]['also'])}" for d in decks))
    out.append("TCSIM_FA_THEN_KEEP=8")
    open(env_out, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")
    print("ok", decks)


if __name__ == "__main__":
    main()
