# 2026-11-15 round B for 25LG (SKY해민; user 03:2x "지금 25LG 최종 육성표를 저장했으니 25LG의 작전실 및 개인 작전도 알려줘"),
# after opsLG_A.log (2,000 games): LG형 87.69 (+2.45 on 지금 85.24), C2 86.49, C2교체50 86.39, CLG 86.32, C2팀도루75 86.23,
# 느린주자도루0 86.06, 중립 82.76. Round B: the LG형 around its parts, as for the 롯데 decks on 11-14, plus slow runners at 50.
# Run: OPS_DECKS="2025 LG" OPS_TAG=1115 python gen_ops_1114.py B ops1115_roundB.py
NOTE = "2026-11-15 round B: 25LG LG형 around its parts (gen_ops_1114.py B ops1115_roundB.py, OPS_TAG=1115)"


def lg(g, team_steal=100, hook=0, sp_eff=50, small=True, rp_bait_all=False, sp_bait=100, slow50=False):
    parts = [f"B=100.100.{team_steal}.0.100", f"P=0.50.0.50.{hook}"]
    slow = g["slow"] if slow50 else []
    if small:
        parts.append(own([t for t in g["small"] if t not in slow], "0.100.100.0"))
    parts.append(own(g["sp"], f"0.{sp_bait}.0.{sp_eff}"))
    if rp_bait_all:
        parts.append(own(g["rp"], "0.100.0.0"))
    else:
        parts.append(own(g["rp_good"], "0.100.0.0"))
        parts.append(own(g["rp_low"], "0.0.0.0"))
    if slow:
        parts.append(own([t for t in slow if t in g["small"]], "0.100.50.0"))
        parts.append(own([t for t in slow if t not in g["small"]], "100.100.50.0"))
    return "|".join(x for x in parts if x)


g = groups(data["2025 LG"])
ROUND = {"2025 LG": [
    ("LG형", lg(g)),
    ("LG형선발투구25", lg(g, sp_eff=25)),
    ("LG형선발투구75", lg(g, sp_eff=75)),
    ("LG형교체25", lg(g, hook=25)),
    ("LG형불펜전원유인구", lg(g, rp_bait_all=True)),
    ("LG형선발승부50", lg(g, sp_bait=50)),
    ("LG형스몰볼빼고", lg(g, small=False)),
    ("LG형팀도루75", lg(g, team_steal=75)),
    ("LG형느린주자도루50", lg(g, slow50=True)),
]}
