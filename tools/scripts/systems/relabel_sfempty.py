"""Atlas_CoachingStaff.png: sf_empty "면담할 / 선수가 / 없습니다" -> "컨디션이 / 나쁜 선수가 / 더 없습니다"
(2026-11-19 23:0x, user: "컨디션이 나쁜 선수가 더 없습니다로 합시다"; AI tester kt 12회차 면담실).
usage: relabel_sfempty.py <atlas.png> <out atlas.png> <preview.png>"""
import sys
from PIL import Image, ImageDraw, ImageFont

src, out, preview = sys.argv[1:4]
im = Image.open(src).convert("RGBA")
X0, Y0, W, H = 1600, 1086, 79, 73
GREY = (149, 149, 149, 247)

# 1) clear the old words: rows of the grey face take the face colour (columns 3..75, inside the frame)
for ry in range(H):
    if im.getpixel((X0 + 2, Y0 + ry)) != GREY:
        continue
    for rx in range(3, W - 3):
        im.putpixel((X0 + rx, Y0 + ry), GREY)

# 2) three lines, white with the old dark grey outline, 4x supersampled
S = 4
font = ImageFont.truetype(r"C:\Windows\Fonts\malgunbd.ttf", 12 * S)
lines = ["컨디션이", "나쁜 선수가", "더 없습니다"]
centres = [17.5, 34.5, 51.5]          # the old lines' middles (rows 11-24, 27-41, 45-59)
layer = Image.new("RGBA", (W * S, H * S), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)
widest = 0
for text, cy in zip(lines, centres):
    bbox = d.textbbox((0, 0), text, font=font, stroke_width=1 * S)
    tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
    widest = max(widest, tw / S)
    d.text((W * S / 2 - tw / 2 - bbox[0], cy * S - th / 2 - bbox[1]), text, font=font, fill=(255, 255, 255, 255),
           stroke_width=1 * S, stroke_fill=(93, 93, 93, 255))
small = layer.resize((W, H), Image.LANCZOS)
sprite = im.crop((X0, Y0, X0 + W, Y0 + H))
sprite.alpha_composite(small)
im.paste(sprite, (X0, Y0))
im.save(out)

pv = Image.new("RGBA", (W, H), (40, 40, 40, 255))
pv.alpha_composite(im.crop((X0, Y0, X0 + W, Y0 + H)))
pv.resize((W * 5, H * 5), Image.NEAREST).save(preview)
print("widest line", widest, "of", W - 6)
