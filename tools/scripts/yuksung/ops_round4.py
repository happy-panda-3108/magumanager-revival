# 2026-11-11 round 4 (engine session 19:3x: the new 도루 rule — 적극 multiplies every runner's attempts by 1 + 0.8·s, success by
# runner and catcher; on a batted ball each runner goes by his own 도루 setting): the round 3 best settings again, and the team
# 도루 slider stepped down while the 개인작전 runners (the small-ball groups) keep 100. The old rule: ops_round4old.py.
NOTE = "2026-11-11 round 4: new 도루 rule, best settings with team 도루 100/75/50/0 (gen_ops_env.py ops_round4.py)"


def own(names, values):
    return "|".join(f"O{n}={values}" for n in names)


# 1997 쌍방울, round 3 best = C2선발투구25 (65.62).
S_SMALL = ["김광림", "김호", "김실", "최태원", "조원우"]
S_SP_GOOD, S_SP_LOW = ["김원형", "김기덕"], ["성영재", "오봉옥", "오상민"]
S_RP_LOW = ["김민국", "윤형배"]


def s_ops(team_steal=100, small_steal=100):
    parts = [f"B=100.100.{team_steal}.0.100", "P=50.50.50.0.25", own(S_SMALL, f"0.100.{small_steal}.0"), own(S_SP_GOOD, "50.50.50.25"),
             own(S_SP_LOW, "50.0.50.25"), own(S_RP_LOW, "50.0.50.0")]
    return "|".join(parts)


s = [
    ("중립", f"B={N}|P={N}"),
    ("추천", s_ops()),
    ("팀도루75", s_ops(team_steal=75)),
    ("팀도루50", s_ops(team_steal=50)),
    ("팀도루0", s_ops(team_steal=0)),
    ("모두도루50", s_ops(team_steal=50, small_steal=50)),
]

# 2025 LG, round 3 best = C2 (84.42).
L_SMALL = ["박해민", "신민재", "문성주", "구본혁"]
L_SP = ["손주영", "치리노스", "임찬규", "송승기", "800103"]
L_RP_GOOD, L_RP_LOW = ["김진성"], ["장현식", "이지강", "이정용", "김영우", "유영찬"]


def l_ops(team_steal=100, small_steal=100):
    parts = [f"B=100.100.{team_steal}.0.100", "P=0.50.0.50.0", own(L_SMALL, f"0.100.{small_steal}.0"), own(L_SP, "0.100.0.50"),
             own(L_RP_GOOD, "0.100.0.0"), own(L_RP_LOW, "0.0.0.0")]
    return "|".join(parts)


l = [
    ("중립", f"B={N}|P={N}"),
    ("추천", l_ops()),
    ("팀도루75", l_ops(team_steal=75)),
    ("팀도루50", l_ops(team_steal=50)),
    ("팀도루0", l_ops(team_steal=0)),
    ("모두도루50", l_ops(team_steal=50, small_steal=50)),
]
ROUND = {"1997 쌍방울": s, "2025 LG": l}
