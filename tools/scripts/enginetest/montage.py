"""Contact sheet of chosen shots: montage.py <dir> <prefix> <out> t1 t2 ... (shot names <prefix>_<tttt>.png), 3 per row."""
import sys
from PIL import Image, ImageDraw

d, prefix, out = sys.argv[1], sys.argv[2], sys.argv[3]
ts = sys.argv[4:]
W = 640
imgs = []
for t in ts:
    im = Image.open(f"{d}/{prefix}_{t}.png").convert("RGB")
    im = im.resize((W, int(im.height * W / im.width)))
    ImageDraw.Draw(im).text((8, 8), f"{int(t) / 100:.2f}s", fill=(255, 255, 0))
    imgs.append(im)
cols = 3
rows = (len(imgs) + cols - 1) // cols
h = imgs[0].height
sheet = Image.new("RGB", (W * cols, h * rows))
for i, im in enumerate(imgs):
    sheet.paste(im, ((i % cols) * W, (i // cols) * h))
sheet.save(out)
print(out, sheet.size)
