# 2026-11-11 round 5 (user 20:0x: "25LG 육성표도 방금 약간 수정했으니 그걸로 반영해서 진행해줘"): round 4's settings with each deck
# developed as its book says (TCSIM_DEV from book_dev.py: the books' own 특강·트레이닝·재계약; the 25LG book as saved 20:09,
# the 97쌍방울 book as saved 00:22), under the new 도루 rule.
NOTE = "2026-11-11 round 5: the books' own development (TCSIM_DEV), new 도루 rule, team 도루 100/75/50/0 (gen_ops_env.py ops_round5.py)"
exec(open(r"C:\Ma9MProject\tools\scripts\yuksung\ops_round4.py", encoding="utf-8").read())
exec(open(r"C:\Ma9MProject\tools\scripts\yuksung\book_dev.py", encoding="utf-8").read().split('if __name__ == "__main__":')[0])
BOOKS = r"C:\Ma9MProject\docs\육성표\한셀_1110_레전드"
DEV = {
    "1997 쌍방울": spec(BOOKS + r"\육성표_1110_97쌍방울_레전드.cell", *DECKS["1997 쌍방울"])[0],
    "2025 LG": spec(BOOKS + r"\육성표_1110_25LG_레전드.cell", *DECKS["2025 LG"])[0],
}
ROUND = {"1997 쌍방울": s[:5], "2025 LG": l[:5]}
