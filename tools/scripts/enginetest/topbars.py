"""Stack the top score bars (and a strip of the field) of shots: topbars.py <out> <png>..."""
import sys
from PIL import Image, ImageDraw

out = sys.argv[1]
strips = []
for p in sys.argv[2:]:
    im = Image.open(p).convert("RGB")
    s = im.crop((0, 0, im.width, 300))
    ImageDraw.Draw(s).text((8, 40), p.replace("\\", "/").split("/")[-1], fill=(255, 255, 0))
    strips.append(s)
w = strips[0].width
sheet = Image.new("RGB", (w, sum(s.height for s in strips)))
y = 0
for s in strips:
    sheet.paste(s, (0, y))
    y += s.height
sheet.save(out)
print(out, sheet.size)
