# 2026-11-11 round 4, the old 도루 rule (MA9M_STEAL_ODDS=1, MA9M_AGGR_BATTER=1 appended to the env file): neutral and the round 3
# best of each deck, to check that the old numbers come back (65.62 / 84.42) on the same build.
NOTE = "2026-11-11 round 4 old rule: neutral and the round 3 best (gen_ops_env.py ops_round4old.py + MA9M_STEAL_ODDS=1, MA9M_AGGR_BATTER=1)"
exec(open(r"C:\Ma9MProject\tools\scripts\yuksung\ops_round4.py", encoding="utf-8").read())
ROUND = {"1997 쌍방울": [s[0], s[1]], "2025 LG": [l[0], l[1]]}
