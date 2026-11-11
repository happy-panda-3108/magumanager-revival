# 2026-11-11 round 2 (after ops1.log): the best side of each slider together (C1), steps back to 75/25, the small sliders off,
# and 개인작전 by player group where the rules differ by card (TacticRules: 빅볼 pays with power, 유인구 costs walks by 컨트롤,
# 전력투구 tires: long outings vs short). Run by gen_ops_env.py (DECKS, SAVED, N, sliders in scope).
NOTE = "2026-11-11 round 2: combined best sliders, 75/25 steps, groups of 개인작전 (gen_ops_env.py ops_round2.py)"


def own(names, values):
    return "|".join(f"O{n}={values}" for n in names)


# 1997 쌍방울: round 1 도루100 +2.70, 수비100 +0.92, 투구0 +0.68, 타격100 +0.32, 공격100 +0.19, 투수성향0 +0.17, 승부100 +0.14,
# 번트0 +0.10, 제구 ±0.05, 교체 0/100 both lower (−0.47/−0.36).
S_B, S_P = "100.100.100.0.100", "0.100.50.0.50"
S_SP = ["김원형", "김기덕", "성영재", "오봉옥", "오상민"]
S_RP = ["최정환", "정수찬", "김민국", "윤형배", "김현욱", "800108"]
S_LOWPOW = ["김광림", "김호", "김실", "최태원", "조원우"]          # 파워 (book 경기 반영) under 75
S_LOWCTRL = ["성영재", "오봉옥", "오상민", "김민국", "윤형배"]      # 컨트롤 under 75
s = [
    ("중립", f"B={N}|P={N}"),
    ("C1", f"B={S_B}|P={S_P}"),
    ("C1도루75", f"B=100.100.75.0.100|P={S_P}"),
    ("C1수비75", f"B=100.100.100.0.75|P={S_P}"),
    ("C1투구25", f"B={S_B}|P=0.100.50.25.50"),
    ("C1교체25", f"B={S_B}|P=0.100.50.0.25"),
    ("C1교체75", f"B={S_B}|P=0.100.50.0.75"),
    ("큰것만", "B=100.50.100.50.100|P=50.50.50.0.50"),
    ("C1선발완급50", f"B={S_B}|P={S_P}|" + own(S_SP, "0.100.50.50")),
    ("C1팀투구50불펜전력", f"B={S_B}|P=0.100.50.50.50|" + own(S_RP, "0.100.50.0")),
    ("C1저파워스몰볼", f"B={S_B}|P={S_P}|" + own(S_LOWPOW, "0.100.100.0")),
    ("C1저파워타격50", f"B={S_B}|P={S_P}|" + own(S_LOWPOW, "50.100.100.0")),
    ("C1저제구빠른승부", f"B={S_B}|P={S_P}|" + own(S_LOWCTRL, "0.0.50.0")),
    ("C1투수성향50승부50", f"B={S_B}|P=50.50.50.0.50"),
]

# 2025 LG: round 1 도루100 +2.34, 투수성향0 +0.75, 수비100 +0.72, 교체0 +0.34 (100 −1.42), 타격100 +0.30, 공격100 +0.20, 제구0 +0.11,
# 번트0 +0.10, 승부 0/100 both lower, 투구 0/100 both lower (−0.27/−1.14). The saved setting (개인작전 18 cards) 81.17 = +2.85.
L_B, L_P = "100.100.100.0.100", "0.50.0.50.0"
L_RP = ["김진성", "장현식", "이지강", "이정용", "김영우", "유영찬"]
L_LOWPOW = ["박해민", "신민재", "문성주", "구본혁"]                 # 파워 under 75
L_GOODCTRL = ["손주영", "치리노스", "임찬규", "송승기", "800103", "김진성"]   # 컨트롤 over 80
L_LOWCTRL = ["장현식", "이지강", "이정용", "김영우", "유영찬"]
saved = SAVED["2025 LG"]
saved_ops = saved.split("|", 2)[2]                                   # the 18 개인작전 of the saved setting
saved_run = (saved_ops.replace("O오스틴=100.100.0.0", "O오스틴=100.100.100.0").replace("O박동원=100.0.0.0", "O박동원=100.0.100.0")
             .replace("O김현수A=0.100.0.0", "O김현수A=0.100.100.0").replace("O문성주=0.100.50.0", "O문성주=0.100.100.0"))
l = [
    ("중립", f"B={N}|P={N}"),
    ("지금", saved),
    ("C1", f"B={L_B}|P={L_P}"),
    ("C1도루75", f"B=100.100.75.0.100|P={L_P}"),
    ("C1교체25", f"B={L_B}|P=0.50.0.50.25"),
    ("C1투구25", f"B={L_B}|P=0.50.0.25.0"),
    ("C1불펜전력", f"B={L_B}|P={L_P}|" + own(L_RP, "0.50.0.0")),
    ("큰것만", "B=100.50.100.50.100|P=0.50.50.50.0"),
    ("C1저파워스몰볼", f"B={L_B}|P={L_P}|" + own(L_LOWPOW, "0.100.100.0")),
    ("C1저파워타격50", f"B={L_B}|P={L_P}|" + own(L_LOWPOW, "50.100.100.0")),
    ("C1제구별승부", f"B={L_B}|P={L_P}|" + own(L_GOODCTRL, "0.100.0.50") + "|" + own(L_LOWCTRL, "0.0.0.50")),
    ("지금+수비100교체0", "B=50.50.50.0.100|P=50.50.50.50.0|" + saved_ops),
    ("지금+전원도루+수비100교체0", "B=50.50.50.0.100|P=50.50.50.50.0|" + saved_run),
]
ROUND = {"1997 쌍방울": s, "2025 LG": l}
