"""도움말 모음 (systems session, 2026-11-14): the app's (?) help pages (Ma9MHelp.cs) as one markdown file.

The user asked (11-14 19:25) whether a document of help material exists; the help lives only in the app code, so this reads
the Pages table of port\\Ma9MClient\\Assets\\Scripts\\Ma9MPort\\Ma9MHelp.cs and writes docs\\도움말_모음.md, a screen per
heading and a page per box, as the popup shows them. Run it again after a help text changes:

  python -I tools\\scripts\\systems\\help_to_md.py
"""
import io
import re
import sys

SRC = r"C:\Ma9MProject\port\Ma9MClient\Assets\Scripts\Ma9MPort\Ma9MHelp.cs"
OUT = r"C:\Ma9MProject\docs\도움말_모음.md"

# Where each scene's (?) sits, as the screen's path title reads.
SCREENS = {
    "Scene_Roster": "팀 관리 > 로스터",
    "Scene_ContractManage": "팀 관리 > 계약관리",
    "Scene_TeamProperty": "팀 관리 > 팀 특성",
    "Scene_TeamColor": "팀 관리 > 팀컬러",
    "Scene_OperationRoom": "팀 관리 > 작전실",
    "Scene_PositionChange": "전력강화 > 포지션 변경",
    "Scene_CoachingStaff": "전력강화 > 코칭스태프",
    "Scene_CoachCardCombine": "전력강화 > 코칭스태프 조합",
    "Scene_Combine": "전력강화 > 선수카드 조합",
    "Scene_SpecialLecture": "전력강화 > 특별수강",
    "Scene_CombineTraining": "전력강화 > 트레이닝북 교환소 · 조합",
    "Scene_LeagueRankingChart": "기록실 > 순위표",
    "Scene_ExhibitionGame": "시범경기",
    "Scene_Unconfirmed": "리그 경기 결과 확인",
}

TOKEN = re.compile(r'''\s+|//[^\n]*|/\*.*?\*/|"((?:[^"\\]|\\.)*)"|([A-Za-z_][A-Za-z0-9_.]*)|(.)''', re.S)


def tokens(text):
    out = []
    for m in TOKEN.finditer(text):
        if m.group(1) is not None:
            s = m.group(1)
            s = re.sub(r'\\(.)', lambda e: {"n": "\n", "t": "\t", '"': '"', "\\": "\\"}.get(e.group(1), e.group(1)), s)
            out.append(("str", s))
        elif m.group(2) is not None:
            out.append(("id", m.group(2)))
        elif m.group(3) is not None:
            out.append(("sym", m.group(3)))
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    text = io.open(SRC, encoding="utf-8-sig").read()
    # The shared pages (Condition, ConditionUp, TrainingPoint): private static readonly string X = "…" + "…";
    consts = {}
    for m in re.finditer(r'private static readonly string (\w+)\s*=(.*?);\s*\n', text, re.S):
        consts[m.group(1)] = "".join(v for k, v in tokens(m.group(2)) if k == "str")
    body = text[text.index("Pages = new Dictionary"):]
    body = body[body.index("{") + 1:]
    t = tokens(body)

    screens = []  # (scene, [variant pages])
    i = 0
    while i < len(t):
        if t[i] == ("sym", "[") and i + 1 < len(t) and t[i + 1][0] == "str":
            scene = t[i + 1][1]
            i += 2
            variants, pages, item, depth = [], None, None, 0
            while i < len(t):
                k, v = t[i]
                if (k, v) == ("sym", "{"):
                    depth += 1
                    if depth == 1:
                        pages, item = [], None
                elif (k, v) == ("sym", "}"):
                    if depth == 1:
                        if item is not None:
                            pages.append(item)
                        variants.append(pages)
                    depth -= 1
                    if depth < 0:
                        break
                elif depth == 1 and k == "str":
                    item = (item or "") + v
                elif depth == 1 and k == "id" and v in consts:
                    item = (item or "") + consts[v]
                elif depth == 1 and (k, v) == ("sym", ","):
                    if item is not None:
                        pages.append(item)
                    item = None
                elif depth == 0 and (k, v) == ("sym", ",") and variants:
                    break
                i += 1
            screens.append((scene, variants))
        i += 1

    lines = ["# 도움말 모음", "",
             "앱 화면 제목 오른쪽 노란 (?)를 누르면 나오는 도움말을 화면별로 모았다. 한 상자가 한 쪽이다(\"다음\"으로 넘김).",
             f"앱 코드(`port\\Ma9MClient\\Assets\\Scripts\\Ma9MPort\\Ma9MHelp.cs`)에서 `tools\\scripts\\systems\\help_to_md.py`로 뽑은 것이라, 도움말을 고치면 다시 뽑는다.",
             ""]
    for scene, variants in screens:
        lines += [f"## {SCREENS.get(scene, scene)}", ""]
        for vi, pages in enumerate(variants):
            if len(variants) > 1:
                lines += [f"### {'교환소가 있는 앱' if vi == 0 else '교환소가 없는 앱(조합)'}", ""]
            for pi, page in enumerate(pages, 1):
                lines.append(f"**{pi}쪽**")
                lines.append("")
                lines += ["> " + l if l else ">" for l in page.split("\n")]
                lines.append("")
    io.open(OUT, "w", encoding="utf-8", newline="\n").write("\n".join(lines))
    print(f"{OUT}: {len(screens)} screens, {sum(len(p) for _, v in screens for p in v)} pages")


if __name__ == "__main__":
    main()
