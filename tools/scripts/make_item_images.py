"""Item pictures for the local CDN (inventory, shop, training), drawn here since the originals died
with the Netmarble CDN.

The client (ItemCardDrawer) shows the top-left part of each texture:
- small icons: 76x67 of a 128x128 texture (inventory list);
- card packs: 90x149 of a 128x256 texture (inventory item info, card-open cover);
- other items such as training books: 79x127 of a 128x128 texture (item info, training window,
  card-open screen).
Pictures are drawn at --scale (default 4) times that size; Ma9MCardArt keeps their slot size and
shrinks them to the screen.

Writes <cdn>/KBO/ItemImage/<ver>/InvenItem/<file>.png (large) and InvenItem/small/<file>.png.
Usage: tools/venv/Scripts/python.exe make_item_images.py <server data dir> <version> [--scale 4] [--sheet <png>] [--grade 4]
--sheet also writes a contact sheet of everything for a quick look.
--grade N writes only the training books of that grade (2026-11-04: IV, from the 트레이닝코치's 조합; the rest untouched)."""
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

DATA = Path(sys.argv[1])
VERSION = sys.argv[2]
S = int(sys.argv[sys.argv.index('--scale') + 1]) if '--scale' in sys.argv else 4
SHEET = sys.argv[sys.argv.index('--sheet') + 1] if '--sheet' in sys.argv else None
ONLY_GRADE = int(sys.argv[sys.argv.index('--grade') + 1]) if '--grade' in sys.argv else 0
FONT_KR = 'C:/Windows/Fonts/malgunbd.ttf'
FONT_LABEL = 'C:/Windows/Fonts/seguibli.ttf'
FONT_NUMERAL = 'C:/Windows/Fonts/timesbd.ttf'

# file -> (title, subtitle, main colour, light colour, accent, emblem, header)
PACKS = {
    'pack_all': ('전체 선수팩', '1~10볼', (34, 96, 206), (110, 170, 255), (255, 255, 255), 'ball', 'PLAYER CARD'),
    'pack_high': ('고급 선수팩', '4~10볼', (124, 52, 196), (200, 140, 255), (255, 214, 64), 'ball', 'PLAYER CARD'),
    'pack_training': ('트레이닝 카드팩', '북 I~III', (22, 128, 92), (120, 214, 168), (255, 232, 96), 'book', 'TRAINING'),
    # 레전드 선수팩 (2026-11-08, 70,000 MP: 3~10볼 and the 레전드 cards), gold foil.
    'pack_legend': ('레전드 선수팩', '3~10볼', (168, 112, 12), (255, 214, 110), (255, 255, 255), 'ball', 'LEGEND'),
}

# Training books (server Training/TrainingBooks.cs): stat 1-12 (batter 1-6, pitcher 7-12), grade 1-3.
# Names as the client shows them (user, 2026-10-19: 주력 -> 스피드, 정신 -> H정신 / P정신).
STATS = [None, '컨택', '파워', '스피드', '선구', '수비', 'H정신', '체력', '구속', '컨트롤', '테크닉', '구위', 'P정신']
NUMERALS = [None, 'I', 'II', 'III', 'IV']
RANGES = [None, '-1 ~ +2', '0 ~ +3', '+1 ~ +4', '+2 ~ +5']
# grade -> (light, dark, numeral fill, numeral outline): bronze, silver, gold, mint (IV, 코칭스태프 2026-11-04; user:
# "좀 더 민트색에 가까운 색상", the first lavender draft replaced).
GRADE_COLOURS = {
    1: ((236, 178, 120), (138, 82, 40), (255, 222, 186), (92, 50, 18)),
    2: ((240, 244, 250), (128, 138, 156), (255, 255, 255), (62, 70, 88)),
    3: ((255, 238, 140), (204, 140, 22), (255, 246, 196), (120, 72, 0)),
    4: ((208, 255, 236), (38, 168, 132), (240, 255, 250), (6, 94, 72)),
}
# Cover colours: batter books red, pitcher books blue.
COVER = {False: ((214, 74, 58), (140, 34, 26)), True: ((62, 112, 214), (26, 52, 130))}


def u(v):
    return round(v * S)


def font(path, size):
    return ImageFont.truetype(path, u(size))


def text_center(d, cx, cy, text, f, fill, stroke=(0, 0, 0), sw=0.6):
    box = d.textbbox((0, 0), text, font=f, stroke_width=u(sw))
    d.text((u(cx) - (box[2] - box[0]) // 2 - box[0], u(cy) - (box[3] - box[1]) // 2 - box[1]), text, font=f, fill=fill,
           stroke_width=u(sw), stroke_fill=stroke)


def fit_font(d, text, path, size, max_w, sw=0.6):
    """The largest font up to size whose text fits max_w (logical units)."""
    while size > 4:
        f = font(path, size)
        box = d.textbbox((0, 0), text, font=f, stroke_width=u(sw))
        if box[2] - box[0] <= u(max_w):
            return f
        size -= 0.25
    return font(path, size)


def gradient(w, h, top, bottom):
    img = Image.new('RGBA', (max(1, w), max(1, h)))
    d = ImageDraw.Draw(img)
    for y in range(img.height):
        t = y / max(1, img.height - 1)
        d.line((0, y, img.width, y), fill=tuple(round(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    return img


def paste_rounded(img, src, x, y, radius):
    mask = Image.new('L', src.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, src.width - 1, src.height - 1), radius=radius, fill=255)
    img.paste(src, (x, y), mask)


def ball(d, cx, cy, r):
    d.ellipse((u(cx - r), u(cy - r), u(cx + r), u(cy + r)), fill=(250, 250, 246), outline=(120, 120, 120), width=max(1, u(0.4)))
    k, q, w = 1.55 * r, 1.08 * r, max(1, u(0.5))
    d.arc((u(cx - k - q), u(cy - q), u(cx - k + q), u(cy + q)), -38, 38, fill=(214, 44, 44), width=w)
    d.arc((u(cx + k - q), u(cy - q), u(cx + k + q), u(cy + q)), 142, 218, fill=(214, 44, 44), width=w)


def book(img, x, y, w, h, cover, label=None, label_size=8.0):
    """A closed hardcover book seen from the front: page block behind, spine on the left, gold rules
    and a name plate on the cover. Logical units."""
    d = ImageDraw.Draw(img)
    light, dark = cover
    dx, dy = w * 0.07, h * 0.045
    # Page block behind the cover (right and bottom edges), with page lines.
    d.rounded_rectangle((u(x + dx), u(y + dy), u(x + w + dx * 0.6), u(y + h + dy * 0.6)), radius=u(1.2), fill=(246, 238, 216),
                        outline=(120, 104, 80), width=max(1, u(0.35)))
    for i in range(1, 4):
        t = i / 4
        d.line((u(x + w + dx * 0.6 * t), u(y + dy * (1 + t * 0.2)), u(x + w + dx * 0.6 * t), u(y + h + dy * 0.6 * t)), fill=(206, 192, 164), width=max(1, u(0.25)))
        d.line((u(x + dx * (1 + t * 0.2)), u(y + h + dy * 0.6 * t), u(x + w + dx * 0.6 * t), u(y + h + dy * 0.6 * t)), fill=(206, 192, 164), width=max(1, u(0.25)))
    # Cover.
    body = gradient(u(w), u(h), light, dark)
    shine = Image.new('RGBA', body.size, (0, 0, 0, 0))
    ImageDraw.Draw(shine).polygon([(body.width * 0.30, 0), (body.width * 0.62, 0), (body.width * 0.22, body.height), (-body.width * 0.10, body.height)],
                                  fill=(255, 255, 255, 34))
    body.alpha_composite(shine)
    paste_rounded(img, body, u(x), u(y), u(1.6))
    d = ImageDraw.Draw(img)
    # Spine.
    sw = w * 0.13
    d.rectangle((u(x), u(y + 0.6), u(x + sw), u(y + h - 0.6)), fill=tuple(round(c * 0.72) for c in dark))
    d.line((u(x + sw), u(y + 0.6), u(x + sw), u(y + h - 0.6)), fill=(255, 220, 120), width=max(1, u(0.45)))
    for fy in (0.12, 0.88):
        d.line((u(x + sw * 0.18), u(y + h * fy), u(x + sw * 0.82), u(y + h * fy)), fill=(255, 220, 120), width=max(1, u(0.5)))
    d.rounded_rectangle((u(x), u(y), u(x + w), u(y + h)), radius=u(1.6), outline=(28, 18, 14), width=max(1, u(0.55)))
    # Gold rules and the name plate.
    cx0, cx1 = x + sw + w * 0.07, x + w - w * 0.07
    for fy in (0.10, 0.90):
        d.line((u(cx0), u(y + h * fy), u(cx1), u(y + h * fy)), fill=(255, 220, 120), width=max(1, u(0.45)))
    if label:
        py0, py1 = y + h * 0.33, y + h * 0.67
        d.rounded_rectangle((u(cx0), u(py0), u(cx1), u(py1)), radius=u(1.4), fill=(252, 248, 236), outline=(255, 220, 120), width=max(1, u(0.45)))
        f = fit_font(d, label, FONT_KR, label_size, (cx1 - cx0) * 0.86, sw=0)
        text_center(d, (cx0 + cx1) / 2, (py0 + py1) / 2, label, f, (40, 26, 20), sw=0)


def pack(w, h, title, sub, main, light, accent, emblem='ball', header='PLAYER CARD', small=False):
    """A sealed foil pack: crimped ends, a diagonal shine, an emblem and the pack's name."""
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # Large packs fill the frame's width (user, 2026-10-19: the opening screen's white glow showed at the
    # sides of a pack drawn with 14% margins); the small list icons keep theirs.
    px0, py0, px1, py1 = (w * 0.02, h * 0.04, w * 0.98, h * 0.96) if not small else (w * 0.22, h * 0.05, w * 0.78, h * 0.95)
    body = gradient(u(px1 - px0), u(py1 - py0), light, main)
    shine = Image.new('RGBA', body.size, (0, 0, 0, 0))
    sd = ImageDraw.Draw(shine)
    sw = body.width * 0.35
    sd.polygon([(body.width * 0.15, 0), (body.width * 0.15 + sw, 0), (body.width * 0.15 + sw - body.height * 0.5, body.height),
                (body.width * 0.15 - body.height * 0.5, body.height)], fill=(255, 255, 255, 52))
    body.alpha_composite(shine)
    paste_rounded(img, body, u(px0), u(py0), u(2.5))
    d = ImageDraw.Draw(img)
    # Crimped ends.
    teeth = 9
    tw = (px1 - px0) / teeth
    for y0, dy in ((py0, 1), (py1, -1)):
        band = 4.0 if not small else 3.0
        d.rectangle((u(px0), u(min(y0, y0 + dy * band)), u(px1), u(max(y0, y0 + dy * band))), fill=tuple(round(c * 0.75) for c in main))
        for i in range(teeth):
            x = px0 + i * tw
            d.polygon([(u(x), u(y0)), (u(x + tw / 2), u(y0 - dy * 1.4)), (u(x + tw), u(y0))], fill=tuple(round(c * 0.75) for c in main))
    d.rounded_rectangle((u(px0), u(py0), u(px1), u(py1)), radius=u(2.5), outline=(20, 20, 30), width=max(1, u(0.6)))
    cx = (px0 + px1) / 2

    def draw_emblem(cy, r):
        if emblem == 'ball':
            ball(d, cx, cy, r)
        else:
            bw, bh = r * 1.9, r * 2.45
            book(img, cx - bw / 2 - bw * 0.03, cy - bh / 2, bw, bh, COVER[False])

    if small:
        draw_emblem(h * 0.40, (px1 - px0) * 0.22)
        d = ImageDraw.Draw(img)
        text_center(d, cx, h * 0.78, sub, fit_font(d, sub, FONT_KR, 8.5, (px1 - px0) * 0.95), accent)
    else:
        text_center(d, cx, h * 0.14, header, font(FONT_LABEL, 7.2), accent)
        draw_emblem(h * 0.40, (px1 - px0) * 0.26)
        d = ImageDraw.Draw(img)
        text_center(d, cx, h * 0.65, title, fit_font(d, title, FONT_KR, 9.6, (px1 - px0) * 0.92), (255, 255, 255))
        text_center(d, cx, h * 0.78, sub, font(FONT_KR, 11), accent)
    return img


def numeral(d, cx, cy, grade, size):
    _, _, fill, outline = GRADE_COLOURS[grade]
    text_center(d, cx, cy, NUMERALS[grade], font(FONT_NUMERAL, size), fill, stroke=outline, sw=size * 0.07)


def book_card(stat, grade, w=79, h=127):
    """Large picture: a card in the grade's metal with the book, its numeral and the value range."""
    light, dark, _, outline = GRADE_COLOURS[grade]
    pitcher = stat >= 7
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    paste_rounded(img, gradient(u(w - 2 * m), u(h - 2 * m), light, dark), u(m), u(m), u(4))
    # Inner panel, blended over the metal (drawing a translucent fill straight onto the picture would
    # leave see-through pixels).
    layer = Image.new('RGBA', img.size, (0, 0, 0, 0))
    ImageDraw.Draw(layer).rounded_rectangle((u(m + 3), u(m + 13), u(w - m - 3), u(h - m - 22)), radius=u(3), fill=(255, 255, 255, 84),
                                            outline=outline + (150,), width=max(1, u(0.4)))
    ImageDraw.Draw(layer).rounded_rectangle((u(m + 6), u(h - m - 17), u(w - m - 6), u(h - m - 6)), radius=u(2.5), fill=(30, 24, 20, 200))
    img.alpha_composite(layer)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=(24, 18, 14), width=max(1, u(0.8)))
    # Header: TRAINING and the batter / pitcher tag.
    text_center(d, w / 2, m + 6.6, 'TRAINING', font(FONT_LABEL, 7.4), (255, 255, 255), stroke=outline, sw=0.7)
    tag = '투수' if pitcher else '타자'
    tag_c = COVER[pitcher][1]
    d.rounded_rectangle((u(w / 2 - 9), u(m + 16), u(w / 2 + 9), u(m + 24)), radius=u(2), fill=tag_c, outline=(255, 255, 255), width=max(1, u(0.35)))
    text_center(d, w / 2, m + 20, tag, font(FONT_KR, 5.6), (255, 255, 255), sw=0)
    # The book.
    book(img, w * 0.20, 31, w * 0.56, 50, COVER[pitcher], STATS[stat], label_size=10)
    d = ImageDraw.Draw(img)
    # Numeral and range.
    numeral(d, w / 2, 94, grade, 20)
    text_center(d, w / 2, h - m - 11.5, RANGES[grade], font(FONT_KR, 6.6), (255, 255, 255), sw=0)
    return img


def book_icon(stat, grade, w=76, h=67):
    """Small icon: the book with its stat, the numeral beside it."""
    light, dark, _, outline = GRADE_COLOURS[grade]
    pitcher = stat >= 7
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    # A metal badge behind the numeral, wider for II and III.
    half = (13, 13, 15, 17, 17)[grade]
    d.rounded_rectangle((u(57 - half), u(26), u(57 + half), u(52)), radius=u(13), fill=dark, outline=outline, width=max(1, u(0.6)))
    d.rounded_rectangle((u(57 - half + 2), u(28), u(57 + half - 2), u(50)), radius=u(11), fill=light)
    book(img, 6, 6, 34, 50, COVER[pitcher], STATS[stat], label_size=8)
    d = ImageDraw.Draw(img)
    numeral(d, 57, 39, grade, (0, 15, 14, 13, 13)[grade])
    tag = '투수' if pitcher else '타자'
    text_center(d, 57, 15, tag, font(FONT_KR, 6.4), (255, 255, 255), stroke=COVER[pitcher][1], sw=0.8)
    return img


def save_on(canvas_size, picture, path):
    canvas = Image.new('RGBA', (u(canvas_size[0]), u(canvas_size[1])), (0, 0, 0, 0))
    canvas.alpha_composite(picture)
    canvas.save(path, optimize=True)
    return canvas


def main():
    out = DATA / 'cdn' / 'KBO' / 'ItemImage' / VERSION / 'InvenItem'
    (out / 'small').mkdir(parents=True, exist_ok=True)
    sheet = []
    for name, (title, sub, main_c, light, accent, emblem, header) in PACKS.items():
        if ONLY_GRADE:
            break
        big = save_on((128, 256), pack(90, 149, title, sub, main_c, light, accent, emblem, header), out / f'{name}.png')
        small = save_on((128, 128), pack(76, 67, title, sub, main_c, light, accent, emblem, header, small=True), out / 'small' / f'{name}.png')
        sheet.append((big.crop((0, 0, u(90), u(149))), small.crop((0, 0, u(76), u(67)))))
        print('wrote', name)
    for stat in range(1, 13):
        for grade in ([ONLY_GRADE] if ONLY_GRADE else range(1, 4)):
            name = f'book_{stat:02}_{grade}'
            big = save_on((128, 128), book_card(stat, grade), out / f'{name}.png')
            small = save_on((128, 128), book_icon(stat, grade), out / 'small' / f'{name}.png')
            sheet.append((big.crop((0, 0, u(79), u(127))), small.crop((0, 0, u(76), u(67)))))
    print(f'wrote {12 if ONLY_GRADE else 36} training books')
    if SHEET:
        cw, ch = u(96), u(150 + 72)
        cols = 13
        rows = (len(sheet) + cols - 1) // cols
        img = Image.new('RGBA', (cols * cw, rows * ch), (58, 62, 70, 255))
        for i, (big, small) in enumerate(sheet):
            x, y = (i % cols) * cw, (i // cols) * ch
            img.alpha_composite(big, (x + u(3), y + u(2)))
            img.alpha_composite(small, (x + u(3), y + u(153)))
        img = img.resize((img.width // 2, img.height // 2), Image.LANCZOS)
        img.save(SHEET)
        print('sheet', SHEET)


if __name__ == '__main__':
    main()
