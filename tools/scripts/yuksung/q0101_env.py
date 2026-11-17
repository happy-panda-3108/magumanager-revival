"""2026-11-17 20:1x-20:2x (user via systems): entry-mode checks of two final rosters, vs the deck league (134 decks × 1,500
games), each variant with the book's development.
- 01롯데 ("5볼 강상수 선발이 6볼 염종석 선발보다 나은거야?? 그리고 6볼 염종석의 중계 대성공이 문동환 중계보다 별로야??"):
  SKY마토's final 1군 (위재영 CP + 이병석 RP1, 82.13%) against 염종석 options.
- 97쌍방울 ("97 정민태 FA를 영입해서 육성하면 다른 스페셜 선발보다 스탯이 월등한 것 같은데 왜 4선발에 둔거지??"): 정민태 at SP1-4,
  the other starters one slot down each.

  python q0101_env.py <env in = fa_final1_env.txt> <env out>
"""
import sys

H01, T01 = "800101,박지철,손민한,박석진@SP!", "가득염,7106503"
P97 = "최정환,오봉옥@SP!,정수찬,김민국,김현욱,800108"
DECKS = {
    "2001 롯데#지금": ("2001 롯데", {
        "최종": f"{H01},강상수@SP!,7108241,김사율,임경완,문동환,{T01}",
        "염종석SP5": f"{H01},염종석,7108241,김사율,임경완,문동환,{T01}",
        "염종석RP2": f"{H01},강상수@SP!,7108241,염종석@RP!,김사율,임경완,{T01}",
        "염종석RP3": f"{H01},강상수@SP!,7108241,김사율,염종석@RP!,임경완,{T01}",
        "염종석RP4": f"{H01},강상수@SP!,7108241,김사율,임경완,염종석@RP!,{T01}",
        "염SP5강RP4": f"{H01},염종석,7108241,김사율,임경완,강상수,{T01}",
        "염SP5강RP2": f"{H01},염종석,7108241,강상수,김사율,임경완,{T01}",
    }),
    "1997 쌍방울#지금": ("1997 쌍방울", {
        "정민태SP4": f"김원형,성영재,오상민@SP!,7108506,김기덕,{P97}",
        "정민태SP3": f"김원형,성영재,7108506,오상민@SP!,김기덕,{P97}",
        "정민태SP2": f"김원형,7108506,성영재,오상민@SP!,김기덕,{P97}",
        "정민태SP1": f"7108506,김원형,성영재,오상민@SP!,김기덕,{P97}",
    }),
}
BATS97 = "최태원@2B,김기태A@1B,7207147@3B,심성보@RF,박경완@C,조원우@LF,김광림@DH,김호@SS,김실@CF"


def main():
    env_in, env_out = sys.argv[1:3]
    out, kv = [], {}
    for line in open(env_in, encoding="utf-8").read().rstrip("\n").split("\n"):
        if line.startswith("TCSIM_FA"):
            continue
        if "=" in line and not line.startswith("#"):
            k, v = line.split("=", 1)
            kv[k] = v
        out.append(line)

    def entry_of(key, base):
        for part in kv[key].split(";"):
            if part.startswith(base + "="):
                return part[len(base) + 1:]
        raise SystemExit(f"{key}: no {base}")

    labels, staff, bats, dev = [], [], [], []
    for base, (name, variants) in DECKS.items():
        b = BATS97 if name == "1997 쌍방울" else entry_of("TCSIM_BATS", base)
        d = entry_of("TCSIM_DEV", base)
        for tag, s in variants.items():
            lb = f"{name}#{tag}"
            labels.append(lb)
            staff.append(f"{lb}={s}")
            bats.append(f"{lb}={b}")
            # Only the players of this variant: a TCSIM_DEV name missing from the deck skips the variant ("육성 염종석 없음").
            here = {x.split("@")[0] for x in s.split(",") + b.split(",")}
            keep = [e for e in d.split("|") if e.split(":")[0] in here]
            dev.append(f"{lb}={'|'.join(keep)}")
    res = []
    for line in out:
        if line.startswith("#"):
            line = "# q0101_env.txt (11-17 20:2x: 01롯데 염종석 안들, 97쌍방울 정민태 선발 순서)"
        elif line.startswith("TCSIM_DECKS="):
            line = "TCSIM_DECKS=" + ";".join(labels)
        elif line.startswith("TCSIM_STAFF="):
            line = "TCSIM_STAFF=" + ";".join(staff)
        elif line.startswith("TCSIM_BATS="):
            line = "TCSIM_BATS=" + ";".join(bats)
        elif line.startswith("TCSIM_DEV="):
            line = "TCSIM_DEV=" + ";".join(dev)
        res.append(line)
    open(env_out, "w", encoding="utf-8", newline="\n").write("\n".join(res) + "\n")
    print("ok", len(labels), "variants")


if __name__ == "__main__":
    main()
