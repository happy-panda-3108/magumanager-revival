# 2026-11-11 round 3 (after ops2.log): everything that helped in round 2 together (C2), and its thresholds.
NOTE = "2026-11-11 round 3: C2 = C1 + the helping 개인작전 groups, and their thresholds (gen_ops_env.py ops_round3.py)"


def own(names, values):
    return "|".join(f"O{n}={values}" for n in names)


# 1997 쌍방울. Round 2 on C1 (63.66): 저파워 스몰볼 +1.28, 선발 완급50 (불펜 전력) +0.64, 저제구 빠른 승부 +0.57, 교체25 +0.39,
# 투수성향·승부 50 +0.38; 공격100·번트0 together +0.13; 도루75 −1.34, 수비75 −0.46.
S_B = "100.100.100.0.100"
S_P = "50.50.50.0.25"                      # team: 투구 전력 (불펜), 교체 25
S_SMALL = ["김광림", "김호", "김실", "최태원", "조원우"]
S_SP_GOOD, S_SP_LOW = ["김원형", "김기덕"], ["성영재", "오봉옥", "오상민"]
S_RP_LOW = ["김민국", "윤형배"]


def s_ops(small=S_SMALL, sp_effort=50, low_ctrl=True, hook=25):
    p = f"50.50.50.0.{hook}"
    parts = [f"B={S_B}", f"P={p}", own(small, "0.100.100.0"), own(S_SP_GOOD, f"50.50.50.{sp_effort}")]
    parts.append(own(S_SP_LOW, f"50.{0 if low_ctrl else 50}.50.{sp_effort}"))
    if low_ctrl:
        parts.append(own(S_RP_LOW, "50.0.50.0"))
    return "|".join(parts)


s = [
    ("중립", f"B={N}|P={N}"),
    ("C2", s_ops()),
    ("C2+김성래스몰볼", s_ops(small=S_SMALL + ["김성래"])),
    ("C2선발투구25", s_ops(sp_effort=25)),
    ("C2선발투구75", s_ops(sp_effort=75)),
    ("C2저제구빼고", s_ops(low_ctrl=False)),
    ("C2교체0", s_ops(hook=0)),
]

# 2025 LG. Round 2 on C1 (83.06): 저파워 스몰볼 +0.51, 제구별 승부 +0.37, 불펜 전력 +0.32; 교체25 −0.32, 도루75 −0.96.
L_B = "100.100.100.0.100"
L_SMALL = ["박해민", "신민재", "문성주", "구본혁"]
L_SP = ["손주영", "치리노스", "임찬규", "송승기", "800103"]       # all 컨트롤 over 80
L_RP_GOOD, L_RP_LOW = ["김진성"], ["장현식", "이지강", "이정용", "김영우", "유영찬"]


def l_ops(small=L_SMALL, sp_effort=50, by_ctrl=True):
    parts = [f"B={L_B}", "P=0.50.0.50.0", own(small, "0.100.100.0")]
    parts.append(own(L_SP, f"0.{100 if by_ctrl else 50}.0.{sp_effort}"))
    parts.append(own(L_RP_GOOD, f"0.{100 if by_ctrl else 50}.0.0"))
    parts.append(own(L_RP_LOW, f"0.{0 if by_ctrl else 50}.0.0"))
    return "|".join(parts)


l = [
    ("중립", f"B={N}|P={N}"),
    ("C2", l_ops()),
    ("C2+김현수스몰볼", l_ops(small=L_SMALL + ["김현수A"])),
    ("C2선발투구25", l_ops(sp_effort=25)),
    ("C2제구별승부빼고", l_ops(by_ctrl=False)),
]
ROUND = {"1997 쌍방울": s, "2025 LG": l}
