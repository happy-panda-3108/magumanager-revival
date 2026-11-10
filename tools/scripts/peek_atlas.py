"""Read one NGUIAtlas MonoBehaviour typetree and print its sprite list sample."""
import sys, json
import UnityPy
env = UnityPy.load(sys.argv[1])
target = sys.argv[2]
for obj in env.objects:
    if obj.type.name == "MonoBehaviour":
        try:
            tree = obj.read_typetree()
        except Exception as e:
            print("typetree error", e); break
        if tree.get("m_Name") != target:
            continue
        keys = list(tree.keys())
        print("keys:", keys)
        sprites = tree.get("mSprites") or tree.get("sprites")
        print("sprite count:", len(sprites) if sprites else None)
        if sprites:
            print(json.dumps(sprites[:5], ensure_ascii=False, indent=0))
        mat = tree.get("material")
        print("material:", mat)
        break
