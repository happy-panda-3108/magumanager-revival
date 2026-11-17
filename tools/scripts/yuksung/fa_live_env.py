"""2026-11-17 13:5x (systems session, for the user's FA 시장 picks): measure accounts as they stand, from fa_then_env.txt.

  python fa_live_env.py <env in> <pick env out> <pairs env out>

- 01롯데 = SKY마토's 1군 read from the live DB (read-only, systems 13:2x): SP '81 최동원, 박지철, 손민한, 박석진 (포지션 변경
  선발 대성공, code 11), 강상수 (선발 대성공), RP 김사율, 임경완, 김영수, 문동환, SU 가득염, CP '01 위재영 (FA); 염종석 out. In the
  staff the CP is 염종석, whom the FA key 7106503@CP replaces.
- The pick env plays that 1군 as it stands (TCSIM_FA_PICK 위재영@CP) — 01롯데 only.
- The pairs env plays 위재영@CP with each of this season's FA 시장 names, and the 25LG / 97쌍방울 pairs of the market names
  (book decks: their live 1군 not checked against the books).
"""
import sys

STAFF_0101 = "800101,박지철,손민한,박석진@SP!,강상수@SP!,김사율,임경완,김영수,문동환,가득염,염종석"
# 최약체구단S's 1군 (live, systems 13:3x): as the book but 오상민 and 오봉옥 are 선발 대성공 (code 11), not 성공. SKY해민 = the book.
STAFF_9701 = "김원형,성영재,오상민@SP!,오봉옥@SP!,김기덕,최정환,정수찬,김민국,윤형배,김현욱,800108"
PICK_97 = ["7101001@CP>RP2>RP3>RP4", "7101001@CP>RP4", "7207145@DH", "7202308@RF"]
PAIRS_97 = ["7101001@CP>RP2>RP3>RP4&7207145@DH", "7101001@CP>RP2>RP3>RP4&7202308@RF", "7101001@CP>RP4&7207145@DH"]

WJY = "7106503@CP"
PAIRS = {
    "2001 롯데#지금": [WJY + "&" + k for k in (
        "7201533@CF", "7201533@DH", "7202025@RF", "7202025@DH",
        "7108226@SP5", "7108226@SP4", "7108226@SP2", "7108226@SP5>RP4", "7108226@SP5>RP3", "7108226@SP4>RP4")],
    "2025 LG#지금": ["7161781@SP5&7297398@CF"],
    "1997 쌍방울#지금": ["7101001@CP>RP2>RP3>RP4&7207145@DH", "7101001@CP>RP2>RP3>RP4&7202308@RF",
                       "7101001@CP>RP4&7207145@DH"],
}


def main():
    env_in, pick_out, pairs_out = sys.argv[1:4]
    lines = open(env_in, encoding="utf-8").read().rstrip("\n").split("\n")
    base = []
    for line in lines:
        if line.startswith("TCSIM_FA_THEN"):
            continue
        if line.startswith("TCSIM_STAFF="):
            parts = line[len("TCSIM_STAFF="):].split(";")
            parts = [("2001 롯데#지금=" + STAFF_0101) if p.startswith("2001 롯데#지금=")
                     else ("1997 쌍방울#지금=" + STAFF_9701) if p.startswith("1997 쌍방울#지금=") else p for p in parts]
            line = "TCSIM_STAFF=" + ";".join(parts)
        base.append(line)

    def write(path, decks, extra, head):
        out = []
        for line in base:
            if line.startswith("#"):
                line = head
            elif line.startswith("TCSIM_DECKS="):
                line = "TCSIM_DECKS=" + ";".join(decks)
            out.append(line)
        out += extra
        open(path, "w", encoding="utf-8", newline="\n").write("\n".join(out) + "\n")

    write(pick_out, ["2001 롯데#지금"], ["TCSIM_FA_PICK=2001 롯데#지금=" + WJY],
          "# fa_live_pick_env.txt (11-17 13:5x: SKY마토 1군 그대로, 위재영 CP)")
    write(pairs_out, list(PAIRS), ["TCSIM_FA_PAIRS=" + ";".join(f"{d}={','.join(v)}" for d, v in PAIRS.items())],
          "# fa_live_pairs_env.txt (11-17 13:5x: 이번 시즌 FA 시장 명단 짝)")
    if len(sys.argv) > 5:
        # 97쌍방울 again as 최약체구단S stands (14:0x): singles, then pairs.
        write(sys.argv[4], ["1997 쌍방울#지금"], ["TCSIM_FA_PICK=1997 쌍방울#지금=" + ",".join(PICK_97)],
              "# fa_live97_pick_env.txt (11-17 14:0x: 최약체구단S 1군 그대로, 한 명씩)")
        write(sys.argv[5], ["1997 쌍방울#지금"], ["TCSIM_FA_PAIRS=1997 쌍방울#지금=" + ",".join(PAIRS_97)],
              "# fa_live97_pairs_env.txt (11-17 14:0x: 최약체구단S 1군 그대로, 두 명)")
    print("ok")


if __name__ == "__main__":
    main()
