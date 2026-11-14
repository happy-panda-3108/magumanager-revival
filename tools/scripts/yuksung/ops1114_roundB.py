# 2026-11-14 round B (after opsA.log, 2,000 games): 24롯데 LG형 73.98 (+5.94 on 지금/중립 68.04), CLG 72.14, C2 71.77;
# 01롯데 LG형 80.91 (+4.43 on 지금 76.48), C2 80.74; 97쌍방울 지금 = C2 70.07 best (팀도루75 −0.32, 50 −0.61, 느린 김성래 도루0 −0.06,
# LG형 −0.34). Team 도루 100 best in all three; 느린 주자 도루0 no help. Round B: the LG형 around its parts for the two 롯데 decks,
# C2 around its parts for 97쌍방울. Run by gen_ops_1114.py B (data, groups, own, N in scope).
NOTE = "2026-11-14 round B: LG형 (롯데) and C2 (97쌍방울) around their parts (gen_ops_1114.py B ops1114_roundB.py)"


def lg(g, team_steal=100, hook=0, sp_eff=50, small=True, rp_bait_all=False, sp_bait=100):
    parts = [f"B=100.100.{team_steal}.0.100", f"P=0.50.0.50.{hook}"]
    if small:
        parts.append(own(g["small"], "0.100.100.0"))
    parts.append(own(g["sp"], f"0.{sp_bait}.0.{sp_eff}"))
    if rp_bait_all:
        parts.append(own(g["rp"], "0.100.0.0"))
    else:
        parts.append(own(g["rp_good"], "0.100.0.0"))
        parts.append(own(g["rp_low"], "0.0.0.0"))
    return "|".join(x for x in parts if x)


def c2(g, team_steal=100, hook=25, sp_eff=25, good_sp_lg=False):
    parts = [f"B=100.100.{team_steal}.0.100", f"P=50.50.50.0.{hook}", own(g["small"], "0.100.100.0"),
             own(g["sp_good"], f"0.100.0.{sp_eff}" if good_sp_lg else f"50.50.50.{sp_eff}"),
             own(g["sp_low"], f"50.0.50.{sp_eff}"), own(g["rp_low"], "50.0.50.0")]
    return "|".join(x for x in parts if x)


ROUND = {}
for deck in ("2024 롯데", "2001 롯데"):
    g = groups(data[deck])
    ROUND[deck] = [
        ("LG형", lg(g)),
        ("C2", c2(g)),
        ("LG형선발투구25", lg(g, sp_eff=25)),
        ("LG형선발투구75", lg(g, sp_eff=75)),
        ("LG형교체25", lg(g, hook=25)),
        ("LG형불펜전원유인구", lg(g, rp_bait_all=True)),
        ("LG형선발승부50", lg(g, sp_bait=50)),
        ("LG형스몰볼빼고", lg(g, small=False)),
        ("LG형팀도루75", lg(g, team_steal=75)),
    ]
g = groups(data["1997 쌍방울"])
ROUND["1997 쌍방울"] = [
    ("C2", c2(g)),
    ("C2선발투구50", c2(g, sp_eff=50)),
    ("C2교체0", c2(g, hook=0)),
    ("C2좋은선발LG형", c2(g, good_sp_lg=True)),
]
