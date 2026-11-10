"""Export NGUI atlas textures and individual sprites to PNG.
usage: export_atlas.py <bundle> <outdir> [atlas_name_filter,...]"""
import sys, os
import UnityPy
bundle, outdir = sys.argv[1], sys.argv[2]
filt = set(sys.argv[3].split(",")) if len(sys.argv) > 3 else None
env = UnityPy.load(bundle)
by_id = {o.path_id: o for o in env.objects}
os.makedirs(outdir, exist_ok=True)
count = 0
for obj in env.objects:
    if obj.type.name != "MonoBehaviour":
        continue
    try:
        tree = obj.read_typetree()
    except Exception:
        continue
    if "mSprites" not in tree:
        continue
    name = tree["m_Name"]
    if filt and name not in filt:
        continue
    mat = by_id.get(tree["material"]["m_PathID"])
    if mat is None:
        continue
    mtree = mat.read_typetree()
    tex_ptr = None
    for k, v in mtree["m_SavedProperties"]["m_TexEnvs"]:
        if k == "_MainTex":
            tex_ptr = v["m_Texture"]["m_PathID"]
    tex = by_id[tex_ptr].read()
    img = tex.image
    d = os.path.join(outdir, name)
    os.makedirs(d, exist_ok=True)
    img.save(os.path.join(d, "_atlas.png"))
    for s in tree["mSprites"]:
        x, y, w, h = s["x"], s["y"], s["width"], s["height"]
        img.crop((x, y, x + w, y + h)).save(os.path.join(d, s["name"] + ".png"))
        count += 1
print("exported sprites:", count)
