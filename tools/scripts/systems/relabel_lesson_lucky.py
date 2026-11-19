"""Atlas_ItemUsePopup.png: Lesson_Lucky sprite's first text line "특별강좌" -> "특강" (2026-11-19, AI testers LG 114, kt, 두산).
usage: relabel.py <atlas.png> <out atlas.png> <preview.png>"""
import sys
from PIL import Image, ImageDraw, ImageFont

src, out, preview = sys.argv[1:4]
im = Image.open(src).convert("RGBA")
X0, Y0, W, H = 176, 608, 87, 138
TOP, BOTTOM = 78, 91          # first text line rows (sprite space, inclusive)
LEFT, RIGHT = 14, 73          # columns to clear

# 1) clear the line: each row takes the background colour of the same row (the gradient is vertical only)
for ry in range(TOP, BOTTOM + 1):
    bg = im.getpixel((X0 + 10, Y0 + ry))
    for rx in range(LEFT, RIGHT + 1):
        im.putpixel((X0 + rx, Y0 + ry), bg)

# 2) draw "특강" 4x supersampled, white with a dark navy outline, centred where the old line was
S = 4
font = ImageFont.truetype(r"C:\Windows\Fonts\malgunbd.ttf", 14 * S)
text = "특강"
layer = Image.new("RGBA", (W * S, H * S), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)
bbox = d.textbbox((0, 0), text, font=font, stroke_width=1 * S)
tw, th = bbox[2] - bbox[0], bbox[3] - bbox[1]
cx = 43 * S
cy = (TOP + BOTTOM + 1) / 2 * S
d.text((cx - tw / 2 - bbox[0], cy - th / 2 - bbox[1]), text, font=font, fill=(255, 255, 255, 255),
       stroke_width=1 * S, stroke_fill=(0, 22, 52, 255))
small = layer.resize((W, H), Image.LANCZOS)
sprite = im.crop((X0, Y0, X0 + W, Y0 + H))
sprite.alpha_composite(small)
im.paste(sprite, (X0, Y0))
im.save(out)

pv = Image.new("RGBA", (W, H), (40, 40, 40, 255))
pv.alpha_composite(im.crop((X0, Y0, X0 + W, Y0 + H)))
pv.resize((W * 3, H * 3), Image.NEAREST).save(preview)
print("text box", tw / S, th / S)
