"""Client compile check without Unity (systems session; kept in the repo since 2026-11-13 after the scratch copy was lost).

  python make_csproj.py <out dir>      → <out dir>\\ClientCheck.csproj, then: dotnet build <out dir>\\ClientCheck.csproj -o <out dir>\\bin

From port\\Ma9MClient\\Assembly-CSharp.csproj (Unity's), as the engine session's checker does: the Compile list made absolute
plus any .cs under Assets\\Scripts that Unity has not listed yet (not under an Editor or Plugins folder), the firstpass project
reference replaced by Library\\ScriptAssemblies\\Assembly-CSharp-firstpass.dll, relative HintPaths (plugin dlls) made absolute
(without that, IgaworksUnityPluginAOS and the like are missing: CS0103), and obj under <out dir>.
"""
import os
import re
import sys

PROJ = r"C:\Ma9MProject\port\Ma9MClient"


def main():
    out = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    src = open(os.path.join(PROJ, "Assembly-CSharp.csproj"), encoding="utf-8-sig").read()
    listed = re.findall(r'<Compile Include="([^"]+)"', src)
    listed_abs = {os.path.normcase(os.path.join(PROJ, p)) for p in listed}
    extra = []
    for root, dirs, files in os.walk(os.path.join(PROJ, "Assets", "Scripts")):
        parts = {d.lower() for d in os.path.relpath(root, PROJ).split(os.sep)}
        if "editor" in parts or "plugins" in parts:
            continue
        for f in files:
            if f.endswith(".cs") and os.path.normcase(os.path.join(root, f)) not in listed_abs:
                extra.append(os.path.join(root, f))
    items = [os.path.join(PROJ, p) for p in listed] + extra
    items = [p for p in items if os.path.exists(p)]
    body = re.sub(r'\s*<Compile Include="[^"]+"\s*/>', "", src)
    body = re.sub(r'<ProjectReference Include="[^"]*firstpass[^"]*"[\s\S]*?</ProjectReference>', "", body)
    body = re.sub(r'<ProjectReference Include="[^"]*firstpass[^"]*"\s*/>', "", body)
    body = re.sub(r'<HintPath>(?![A-Za-z]:)([^<]+)</HintPath>', lambda m: "<HintPath>" + os.path.join(PROJ, m.group(1)) + "</HintPath>", body)
    body = body.replace(r"<BaseIntermediateOutputPath>Temp\obj\$(MSBuildProjectName)</BaseIntermediateOutputPath>",
                        "<BaseIntermediateOutputPath>" + os.path.join(out, "obj") + "\\</BaseIntermediateOutputPath>")
    firstpass = os.path.join(PROJ, "Library", "ScriptAssemblies", "Assembly-CSharp-firstpass.dll")
    add = "  <ItemGroup>\n" + "\n".join('    <Compile Include="%s" />' % p for p in items) + \
          '\n    <Reference Include="Assembly-CSharp-firstpass"><HintPath>%s</HintPath></Reference>\n  </ItemGroup>\n' % firstpass
    body = body.replace('<Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />', add + '  <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />')
    os.makedirs(out, exist_ok=True)
    open(os.path.join(out, "ClientCheck.csproj"), "w", encoding="utf-8").write(body)
    print("listed %d, extra %d %s, total %d" % (len(listed), len(extra), [os.path.basename(p) for p in extra], len(items)))


if __name__ == "__main__":
    main()
