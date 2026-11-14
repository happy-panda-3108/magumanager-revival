# 2026-11-15 round C (user 00:0x: "스피드가 느린 선수도 그럼 적극적 100인 상태인데 이러면 무조건 뛰어서 죽잖아"): round A tried the slow
# runners (경기 반영 스피드 under 70) at 도루 0, which also makes them hold back on batted balls (the same per-player setting sends
# runners on hits since 11-11) and lost 0.06-0.46. Here they get 50 instead: their steal attempts and their running as at neutral,
# the team still 100. Each deck's best (롯데 LG형, 97쌍방울 C2) against it.
NOTE = "2026-11-15 round C: slow runners at 도루 50 on each deck's best (gen_ops_1114.py B ops1114_roundC.py)"


def lg(g, slow=None):
    parts = ["B=100.100.100.0.100", "P=0.50.0.50.0", own([t for t in g["small"] if t not in (slow or [])], "0.100.100.0"),
             own(g["sp"], "0.100.0.50"), own(g["rp_good"], "0.100.0.0"), own(g["rp_low"], "0.0.0.0")]
    if slow:
        parts.append(own([t for t in slow if t in g["small"]], "0.100.50.0"))
        parts.append(own([t for t in slow if t not in g["small"]], "100.100.50.0"))
    return "|".join(x for x in parts if x)


def c2(g, slow=None):
    parts = ["B=100.100.100.0.100", "P=50.50.50.0.25", own([t for t in g["small"] if t not in (slow or [])], "0.100.100.0"),
             own(g["sp_good"], "50.50.50.25"), own(g["sp_low"], "50.0.50.25"), own(g["rp_low"], "50.0.50.0")]
    if slow:
        parts.append(own([t for t in slow if t in g["small"]], "0.100.50.0"))
        parts.append(own([t for t in slow if t not in g["small"]], "100.100.50.0"))
    return "|".join(x for x in parts if x)


ROUND = {}
for deck in ("2024 롯데", "2001 롯데"):
    g = groups(data[deck])
    ROUND[deck] = [("LG형", lg(g)), ("LG형+느린주자도루50", lg(g, g["slow"]))]
g = groups(data["1997 쌍방울"])
ROUND["1997 쌍방울"] = [("C2", c2(g)), ("C2+느린주자도루50", c2(g, g["slow"]))]
