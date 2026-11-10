"""Print atlas name -> texture size and sprite names for every NGUIAtlas in a bundle."""
import sys, json, collections
import UnityPy
env = UnityPy.load(sys.argv[1])
out = {}
for obj in env.objects:
    if obj.type.name == "MonoBehaviour":
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if "mSprites" in tree:
            out[tree["m_Name"]] = [s["name"] for s in tree["mSprites"]]
json.dump(out, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False, indent=0)
print(len(out), "atlases,", sum(map(len, out.values())), "sprites")
for k in sorted(out)[:400:12]:
    print(k, out[k][:8])
