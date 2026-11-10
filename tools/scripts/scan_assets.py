"""Scan Unity data folder: count object types and collect names of interest."""
import sys, os, collections, json
import UnityPy

data_dir = sys.argv[1]
out_path = sys.argv[2]
env = UnityPy.load(data_dir)
types = collections.Counter()
names = collections.defaultdict(list)
for obj in env.objects:
    t = obj.type.name
    types[t] += 1
    if t in ("AudioClip", "TextAsset", "Texture2D", "GameObject", "MonoScript", "Mesh"):
        try:
            d = obj.read()
            n = getattr(d, "m_Name", None) or getattr(d, "name", None)
        except Exception as e:
            n = f"<err {e}>"
        names[t].append(n)
print(json.dumps(types.most_common(30), ensure_ascii=False))
with open(out_path, "w", encoding="utf-8") as f:
    json.dump({k: sorted(set(map(str, v))) for k, v in names.items()}, f, ensure_ascii=False, indent=1)
for k, v in names.items():
    print(k, len(v))
