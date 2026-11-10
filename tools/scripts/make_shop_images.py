"""Shop product pictures for the local CDN (server Shop/ShopHandlers.cs products), drawn in the same
style as make_item_images.py, whose helpers this reuses.

The client (ItemCardDrawer, ShopItem) shows the top-left part of each 128x128 texture:
- the selected product: 79x127 (LARGE_FRAME; a file name whose second part is "package" would use
  the 90x149 pack frame instead, so these names avoid it);
- the product list: 76x67, read from ShopItem/small/<file>_small.png.
Pictures are drawn at --scale (default 4); Ma9MCardArt keeps their slot size.

Writes <cdn>/KBO/ItemImage/<ver>/ShopItem/<file>.png and ShopItem/small/<file>_small.png.
Usage: tools/venv/Scripts/python.exe make_shop_images.py <server data dir> <version> [--scale 4] [--sheet <png>]
"""
import sys
from pathlib import Path

DATA_DIR, VERSION = sys.argv[1], sys.argv[2]
sys.argv_original = list(sys.argv)
SCALE = sys.argv[sys.argv.index('--scale') + 1] if '--scale' in sys.argv else '4'
SHEET = sys.argv[sys.argv.index('--sheet') + 1] if '--sheet' in sys.argv else None
# make_item_images reads its settings from argv when imported.
sys.argv = [sys.argv[0], DATA_DIR, VERSION, '--scale', SCALE]
sys.path.insert(0, str(Path(__file__).resolve().parent))
import make_item_images as mi  # noqa: E402
from PIL import Image, ImageDraw  # noqa: E402

GOLD, WHITE = (255, 214, 64), (255, 255, 255)
# file -> (title, subtitle, main colour, light colour, accent, emblem, header)
PRODUCTS = {
    'shop_pack_all': mi.PACKS['pack_all'],
    'shop_pack_high': mi.PACKS['pack_high'],
    'shop_pack_training': mi.PACKS['pack_training'],
    'shop_pack_legend': mi.PACKS['pack_legend'],
}
POSITIONS = [
    ('sp', '선발 투수팩', 'STARTER', (178, 40, 44), (255, 128, 112)),
    # 중계 only since 2026-10-22 (was rp '불펜 투수팩'; new file names so no cached picture shows).
    ('mr', '중계 투수팩', 'MIDDLE', (196, 96, 18), (255, 182, 96)),
    ('if', '내야수팩', 'INFIELD', (18, 120, 132), (110, 214, 222)),
    ('of', '외야수팩', 'OUTFIELD', (46, 128, 40), (150, 220, 120)),
]
for code, title, header, main, light in POSITIONS:
    PRODUCTS[f'shop_pack_{code}1'] = (title, '1~7볼', main, light, WHITE, 'ball', header)
    # The 2~8볼 pack in a darker foil with gold lettering.
    PRODUCTS[f'shop_pack_{code}2'] = (title, '2~8볼', tuple(round(c * 0.72) for c in main), main, GOLD, 'ball', header)
# 셋업·마무리팩 (2026-10-22, 70,000 MP): one pack, deep violet with gold lettering.
PRODUCTS['shop_pack_cl'] = ('셋업·마무리팩', '2~8볼', (92, 40, 150), (178, 120, 240), GOLD, 'ball', 'CLOSER')


def roster_plus(w, h, small=False, title='로스터 확장'):
    """로스터 확장 (and the 로스터 확장권 item): a storage box of player cards with a +20 badge."""
    u = mi.u
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), (70, 84, 108), (28, 34, 48)), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=(12, 14, 20), width=max(1, u(0.6)))
    # Three fanned cards.
    cw, ch = (w * 0.34, w * 0.48) if not small else (w * 0.22, h * 0.50)
    cx, cy = (w / 2, h * 0.36) if not small else (w * 0.34, h * 0.48)
    colours = [((236, 178, 120), (138, 82, 40)), ((110, 170, 255), (34, 96, 206)), ((255, 238, 140), (204, 140, 22))]
    for k, (top, bottom) in enumerate(colours):
        card = mi.gradient(u(cw), u(ch), top, bottom)
        cd = ImageDraw.Draw(card)
        cd.rounded_rectangle((0, 0, card.width - 1, card.height - 1), radius=u(1.6), outline=(20, 20, 26), width=max(1, u(0.5)))
        cd.ellipse((card.width * 0.30, card.height * 0.18, card.width * 0.70, card.height * 0.46), fill=(255, 255, 255, 170))
        cd.rounded_rectangle((card.width * 0.14, card.height * 0.58, card.width * 0.86, card.height * 0.80), radius=u(0.8), fill=(255, 255, 255, 150))
        rot = card.rotate((k - 1) * -14, resample=Image.BICUBIC, expand=True)
        img.alpha_composite(rot, (round(u(cx + (k - 1) * cw * 0.42) - rot.width / 2), round(u(cy) - rot.height / 2)))
    d = ImageDraw.Draw(img)
    # The +20 badge.
    bx, by, br = (w * 0.70, h * 0.56, w * 0.20) if not small else (w * 0.74, h * 0.46, h * 0.26)
    d.ellipse((u(bx - br), u(by - br), u(bx + br), u(by + br)), fill=(222, 50, 40), outline=(255, 236, 160), width=max(1, u(0.8)))
    mi.text_center(d, bx, by, '+20', mi.fit_font(d, '+20', mi.FONT_LABEL, br * 1.05, br * 1.6), WHITE, stroke=(120, 20, 14), sw=0.5)
    if small:
        return img
    mi.text_center(d, w / 2, h * 0.14, 'ROSTER', mi.font(mi.FONT_LABEL, 7.2), GOLD)
    mi.text_center(d, w / 2, h * 0.76, title, mi.fit_font(d, title, mi.FONT_KR, 9.6, w * 0.84), WHITE)
    mi.text_center(d, w / 2, h * 0.87, '보관함 +20칸', mi.fit_font(d, '보관함 +20칸', mi.FONT_KR, 8.0, w * 0.84), GOLD)
    return img


def mp_pouch(w, h, small=False):
    """MP주머니: a tied money pouch with MP on it and gold coins at its foot."""
    u = mi.u
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    if not small:
        mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), (255, 226, 140), (214, 120, 30)), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    if not small:
        d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=(90, 44, 8), width=max(1, u(0.6)))
    cx = w / 2 if not small else w * 0.5
    cy = h * 0.46 if not small else h * 0.40
    bw = w * 0.30 if not small else h * 0.30
    bh = bw * 1.05
    # Coins behind the pouch.
    for dx, dy, r in ((-1.15, 0.92, 0.30), (1.1, 0.88, 0.28), (0.55, 1.05, 0.24)):
        x, y, rr = cx + dx * bw, cy + dy * bh, r * bw
        d.ellipse((u(x - rr), u(y - rr * 0.55), u(x + rr), u(y + rr * 0.55)), fill=(255, 206, 60), outline=(150, 96, 0), width=max(1, u(0.4)))
    # Body, neck and the gold tie.
    body = mi.gradient(u(2 * bw), u(2 * bh), (230, 92, 60), (150, 36, 24))
    mask = Image.new('L', body.size, 0)
    ImageDraw.Draw(mask).ellipse((0, 0, body.width - 1, body.height - 1), fill=255)
    img.paste(body, (u(cx - bw), u(cy - bh * 0.62)), mask)
    d = ImageDraw.Draw(img)
    d.ellipse((u(cx - bw), u(cy - bh * 0.62), u(cx + bw), u(cy + bh * 1.38)), outline=(70, 14, 8), width=max(1, u(0.6)))
    nw, nt = bw * 0.55, cy - bh * 1.05
    d.polygon([(u(cx - nw * 0.55), u(cy - bh * 0.50)), (u(cx + nw * 0.55), u(cy - bh * 0.50)), (u(cx + nw), u(nt)), (u(cx - nw), u(nt))],
              fill=(200, 64, 40), outline=(70, 14, 8))
    d.rounded_rectangle((u(cx - nw * 0.75), u(cy - bh * 0.66), u(cx + nw * 0.75), u(cy - bh * 0.50)), radius=u(0.8), fill=(255, 210, 70), outline=(130, 80, 0), width=max(1, u(0.4)))
    mi.text_center(d, cx, cy + bh * 0.42, 'MP', mi.font(mi.FONT_LABEL, bw * 0.95), (255, 226, 96), stroke=(110, 20, 10), sw=0.7)
    if small:
        return img
    mi.text_center(d, w / 2, h * 0.09, 'LUCKY', mi.font(mi.FONT_LABEL, 7.2), (120, 52, 0), stroke=(255, 240, 200), sw=0.4)
    mi.text_center(d, w / 2, h * 0.79, 'MP주머니', mi.fit_font(d, 'MP주머니', mi.FONT_KR, 10, w * 0.84), WHITE, stroke=(110, 50, 0), sw=0.6)
    mi.text_center(d, w / 2, h * 0.89, '1만~1,000만', mi.fit_font(d, '1만~1,000만', mi.FONT_KR, 8, w * 0.84), (110, 40, 0), stroke=(255, 236, 190), sw=0.3)
    return img


def stat_redist(w, h, small=False):
    """스탯재분배권 (item 90611): a violet ticket with six stat bars and a pair of swap arrows."""
    u = mi.u
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), (176, 132, 255), (74, 38, 150)), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=(30, 12, 64), width=max(1, u(0.6)))
    # Six stat bars of uneven length.
    x0, x1 = (w * 0.18, w * 0.82) if not small else (w * 0.12, w * 0.56)
    top, step = (h * 0.24, h * 0.055) if not small else (h * 0.18, h * 0.115)
    for k, share in enumerate((0.8, 0.45, 0.65, 0.3, 0.9, 0.55)):
        y = top + k * step
        d.rounded_rectangle((u(x0), u(y), u(x1), u(y + step * 0.6)), radius=u(0.6), fill=(40, 20, 80))
        d.rounded_rectangle((u(x0), u(y), u(x0 + (x1 - x0) * share), u(y + step * 0.6)), radius=u(0.6), fill=(255, 214, 64))
    # Swap arrows.
    ax, ay, ar = (w * 0.5, h * 0.66, w * 0.17) if not small else (w * 0.78, h * 0.50, h * 0.26)
    d.ellipse((u(ax - ar), u(ay - ar), u(ax + ar), u(ay + ar)), fill=(255, 255, 255), outline=(255, 214, 64), width=max(1, u(0.7)))
    # Two opposite arrows (the font has no ⇄).
    violet = (74, 38, 150)
    for sign, dy in ((1, -0.28), (-1, 0.28)):
        y, half, head = ay + dy * ar, ar * 0.55, ar * 0.28
        x_from, x_to = ax - sign * half, ax + sign * half
        d.line((u(x_from), u(y), u(x_to - sign * head * 0.6), u(y)), fill=violet, width=max(1, u(ar * 0.16)))
        d.polygon([(u(x_to), u(y)), (u(x_to - sign * head), u(y - head * 0.75)), (u(x_to - sign * head), u(y + head * 0.75))], fill=violet)
    if small:
        return img
    mi.text_center(d, w / 2, h * 0.11, 'STAT', mi.font(mi.FONT_LABEL, 7.2), GOLD)
    mi.text_center(d, w / 2, h * 0.82, '스탯재분배권', mi.fit_font(d, '스탯재분배권', mi.FONT_KR, 9.6, w * 0.86), WHITE)
    mi.text_center(d, w / 2, h * 0.91, '계약 보너스 재분배', mi.fit_font(d, '계약 보너스 재분배', mi.FONT_KR, 7.4, w * 0.86), GOLD)
    return img


def tp_charge(w, h, small=False):
    """트레이닝 횟수 충전권 (item 30002): a teal ticket with the six TRAINING POINT balls and a refill arrow."""
    u = mi.u
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), (96, 226, 206), (16, 112, 118)), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=(6, 46, 52), width=max(1, u(0.6)))
    # Six balls in two rows of three (the training tab's TP gauge).
    if not small:
        r, xs, ys = w * 0.105, (w * 0.27, w * 0.5, w * 0.73), (h * 0.27, h * 0.43)
    else:
        r, xs, ys = h * 0.11, (w * 0.13, w * 0.33, w * 0.53), (h * 0.33, h * 0.67)
    red = (214, 40, 40)
    for y in ys:
        for x in xs:
            d.ellipse((u(x - r), u(y - r), u(x + r), u(y + r)), fill=(255, 255, 255), outline=(30, 60, 64), width=max(1, u(0.5)))
            # Two stitch arcs.
            d.arc((u(x - r * 1.55), u(y - r * 0.9), u(x - r * 0.35), u(y + r * 0.9)), -60, 60, fill=red, width=max(1, u(r * 0.14)))
            d.arc((u(x + r * 0.35), u(y - r * 0.9), u(x + r * 1.55), u(y + r * 0.9)), 120, 240, fill=red, width=max(1, u(r * 0.14)))
    # Refill arrow: a gold ring with an arrow head.
    ax, ay, ar = (w * 0.5, h * 0.63, w * 0.15) if not small else (w * 0.79, h * 0.5, h * 0.18)
    gold = (255, 214, 64)
    d.arc((u(ax - ar), u(ay - ar), u(ax + ar), u(ay + ar)), 40, 330, fill=gold, width=max(1, u(ar * 0.26)))
    head = ar * 0.42
    hx, hy = ax + ar * 0.77, ay - ar * 0.64
    d.polygon([(u(hx + head * 0.7), u(hy - head * 0.2)), (u(hx - head * 0.6), u(hy - head * 0.55)), (u(hx - head * 0.1), u(hy + head * 0.75))], fill=gold)
    mi.text_center(d, ax, ay, '6', mi.font(mi.FONT_LABEL, ar * 1.05), WHITE, stroke=(6, 46, 52), sw=0.5)
    if small:
        return img
    mi.text_center(d, w / 2, h * 0.11, 'TRAINING', mi.font(mi.FONT_LABEL, 7.2), (6, 46, 52), stroke=(200, 255, 246), sw=0.3)
    mi.text_center(d, w / 2, h * 0.82, '트레이닝 충전권', mi.fit_font(d, '트레이닝 충전권', mi.FONT_KR, 9.6, w * 0.86), WHITE, stroke=(6, 46, 52), sw=0.5)
    mi.text_center(d, w / 2, h * 0.91, 'TP 6칸 회복', mi.fit_font(d, 'TP 6칸 회복', mi.FONT_KR, 7.4, w * 0.86), gold, stroke=(6, 46, 52), sw=0.3)
    return img


# 컨디션 회복제 (items 30101-30103, server PlayerManageHandlers): steps -> (grade, light, dark).
CONDITION_ITEMS = {1: ('초급', (150, 230, 120), (30, 128, 52)), 2: ('중급', (120, 190, 255), (26, 76, 176)), 3: ('고급', (255, 206, 100), (186, 72, 18))}


def cond_item(steps, w, h, small=False):
    """컨디션 회복제: a drink bottle in the grade's colour, a rising arrow and +N (condition steps)."""
    grade, light, dark = CONDITION_ITEMS[steps]
    u = mi.u
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), light, dark), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    edge = tuple(c // 3 for c in dark)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=edge, width=max(1, u(0.6)))
    # The bottle: cap, neck, clear body with the drink and a white label.
    if not small:
        cx, top, bw, bh = w * 0.40, h * 0.17, w * 0.34, h * 0.52
    else:
        cx, top, bw, bh = w * 0.27, h * 0.10, w * 0.24, h * 0.80
    line = max(1, u(0.45))
    cap_w, neck_w = bw * 0.46, bw * 0.56
    d.rounded_rectangle((u(cx - cap_w / 2), u(top), u(cx + cap_w / 2), u(top + bh * 0.11)), radius=u(0.7), fill=(250, 250, 250), outline=edge, width=line)
    d.rectangle((u(cx - neck_w / 2), u(top + bh * 0.11), u(cx + neck_w / 2), u(top + bh * 0.19)), fill=(226, 244, 255), outline=edge, width=line)
    body_top = top + bh * 0.19
    d.rounded_rectangle((u(cx - bw / 2), u(body_top), u(cx + bw / 2), u(top + bh)), radius=u(bw * 0.2), fill=(232, 248, 255), outline=edge, width=line)
    inset = bw * 0.07
    d.rounded_rectangle((u(cx - bw / 2 + inset), u(body_top + bh * 0.22), u(cx + bw / 2 - inset), u(top + bh - inset)), radius=u(bw * 0.15), fill=dark)
    ly, lh = body_top + bh * 0.36, bh * 0.24
    d.rectangle((u(cx - bw / 2 + inset), u(ly), u(cx + bw / 2 - inset), u(ly + lh)), fill=(255, 255, 255))
    plus = f'+{steps}'
    mi.text_center(d, cx, ly + lh / 2, plus, mi.fit_font(d, plus, mi.FONT_LABEL, lh * 0.9, bw * 0.7, sw=0), dark, stroke=None, sw=0)
    # A rising arrow beside the bottle.
    ax = cx + bw * 0.95 if not small else w * 0.70
    a_top, a_bottom, a_w = (top + bh * 0.05, top + bh * 0.75, bw * 0.55) if not small else (h * 0.16, h * 0.62, h * 0.34)
    gold = (255, 236, 120)
    d.polygon([(u(ax), u(a_top)), (u(ax + a_w / 2), u(a_top + a_w * 0.55)), (u(ax - a_w / 2), u(a_top + a_w * 0.55))], fill=gold, outline=edge)
    d.rectangle((u(ax - a_w * 0.18), u(a_top + a_w * 0.55), u(ax + a_w * 0.18), u(a_bottom)), fill=gold, outline=edge, width=line)
    if small:
        mi.text_center(d, ax, h * 0.80, plus + '칸', mi.fit_font(d, plus + '칸', mi.FONT_KR, h * 0.24, w * 0.42), WHITE, stroke=edge, sw=0.5)
        return img
    mi.text_center(d, w / 2, h * 0.09, 'CONDITION', mi.fit_font(d, 'CONDITION', mi.FONT_LABEL, 7.2, w * 0.86), WHITE, stroke=edge, sw=0.4)
    title = f'{grade} 회복제'
    mi.text_center(d, w / 2, h * 0.80, title, mi.fit_font(d, title, mi.FONT_KR, 9.6, w * 0.86), WHITE, stroke=edge, sw=0.5)
    sub = f'컨디션 {plus}칸 · 24시간'
    mi.text_center(d, w / 2, h * 0.90, sub, mi.fit_font(d, sub, mi.FONT_KR, 7.0, w * 0.88), gold, stroke=edge, sw=0.3)
    return img


def save_cond_items(version_dir, sheet_path=None):
    """Only the 컨디션 회복제 pictures: the shop's 초급/중급 (고급 is not sold) and all three in the inventory."""
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for p in (shop / 'small', inven / 'small'):
        p.mkdir(parents=True, exist_ok=True)
    sheet = []
    for steps in CONDITION_ITEMS:
        big = mi.save_on((128, 128), cond_item(steps, 79, 127), inven / f'cond_{steps}.png')
        small = mi.save_on((128, 128), cond_item(steps, 76, 67, small=True), inven / 'small' / f'cond_{steps}.png')
        sheet.append((big.crop((0, 0, mi.u(79), mi.u(127))), small.crop((0, 0, mi.u(76), mi.u(67)))))
        if steps < 3:
            mi.save_on((128, 128), cond_item(steps, 79, 127), shop / f'shop_cond_{steps}.png')
            mi.save_on((128, 128), cond_item(steps, 76, 67, small=True), shop / 'small' / f'shop_cond_{steps}_small.png')
    print('wrote InvenItem/cond_1..3 and shop_cond_1, shop_cond_2')
    if sheet_path:
        cw, ch = mi.u(84), mi.u(127 + 72)
        img = Image.new('RGBA', (len(sheet) * cw, ch), (58, 62, 70, 255))
        for i, (big, small) in enumerate(sheet):
            img.alpha_composite(big, (i * cw + mi.u(2), 0))
            img.alpha_composite(small, (i * cw + mi.u(2), mi.u(130)))
        img.resize((img.width // 2, img.height // 2), Image.LANCZOS).save(sheet_path)
        print('sheet', sheet_path)


def lesson_ticket(kind, w, h, small=False):
    """특별강좌 초기화권 (item 90101, kind 'reset': a teal ticket with a circular arrow) and 행운권 (90631, 'lucky':
    a gold ticket with a four-leaf clover); 'pos' is the 포지션 변경 초기화권 (90841, 2026-10-24: a violet ticket with
    the circular arrow); 'instant' the 특강 즉시 완료권 (90621, 2026-10-29: a red-orange ticket with a lightning bolt)."""
    import math
    u = mi.u
    pos = kind == 'pos'
    reset = kind in ('reset', 'pos')
    instant = kind == 'instant'
    top, bottom, edge = ((200, 165, 255), (104, 56, 186), (36, 12, 76)) if pos else ((120, 220, 255), (30, 96, 180), (8, 30, 70)) if reset \
        else ((255, 170, 110), (206, 52, 30), (84, 12, 4)) if instant else ((255, 236, 140), (214, 150, 20), (90, 52, 0))
    img = Image.new('RGBA', (u(w), u(h)), (0, 0, 0, 0))
    m = 1.2
    mi.paste_rounded(img, mi.gradient(u(w - 2 * m), u(h - 2 * m), top, bottom), u(m), u(m), u(4))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((u(m), u(m), u(w - m), u(h - m)), radius=u(4), outline=edge, width=max(1, u(0.6)))
    # The ticket: a white card with notches and a dashed tear line.
    tx0, ty0, tx1, ty1 = (w * 0.14, h * 0.20, w * 0.86, h * 0.66) if not small else (w * 0.07, h * 0.16, w * 0.56, h * 0.84)
    d.rounded_rectangle((u(tx0), u(ty0), u(tx1), u(ty1)), radius=u(1.5), fill=(255, 255, 255), outline=edge, width=max(1, u(0.5)))
    nr = (ty1 - ty0) * 0.10
    for x in (tx0, tx1):
        d.ellipse((u(x - nr), u((ty0 + ty1) / 2 - nr), u(x + nr), u((ty0 + ty1) / 2 + nr)), fill=bottom)
    tear = tx0 + (tx1 - tx0) * 0.30
    for k in range(6):
        y = ty0 + (ty1 - ty0) * (0.1 + 0.15 * k)
        d.line((u(tear), u(y), u(tear), u(y + (ty1 - ty0) * 0.07)), fill=edge, width=max(1, u(0.4)))
    cx, cy, r = tear + (tx1 - tear) / 2, (ty0 + ty1) / 2, (ty1 - ty0) * 0.30
    if reset:
        # Circular arrow.
        d.arc((u(cx - r), u(cy - r), u(cx + r), u(cy + r)), 40, 330, fill=bottom, width=max(1, u(r * 0.28)))
        head = r * 0.55
        hx, hy = cx + r * 0.77, cy - r * 0.64
        d.polygon([(u(hx + head * 0.7), u(hy - head * 0.2)), (u(hx - head * 0.6), u(hy - head * 0.55)), (u(hx - head * 0.1), u(hy + head * 0.75))], fill=bottom)
    elif instant:
        # Lightning bolt.
        bolt = [(0.15, -1.0), (-0.55, 0.12), (-0.02, 0.12), (-0.2, 1.0), (0.55, -0.2), (0.03, -0.2), (0.3, -1.0)]
        d.polygon([(u(cx + x * r), u(cy + y * r)) for x, y in bolt], fill=(255, 196, 20), outline=bottom, width=max(1, u(r * 0.08)))
    else:
        # Four-leaf clover.
        green = (40, 150, 60)
        for k in range(4):
            a = math.pi / 4 + k * math.pi / 2
            lx, ly = cx + math.cos(a) * r * 0.45, cy + math.sin(a) * r * 0.45
            d.ellipse((u(lx - r * 0.5), u(ly - r * 0.5), u(lx + r * 0.5), u(ly + r * 0.5)), fill=green)
        d.line((u(cx), u(cy), u(cx + r * 0.5), u(cy + r * 1.0)), fill=green, width=max(1, u(r * 0.18)))
    label = 'RESET' if reset else 'NOW' if instant else 'LUCKY'
    lx = tx0 + (tear - tx0) / 2
    if small:
        short = '보직' if pos else '즉시' if instant else '특강'
        mi.text_center(d, w * 0.80, h * 0.50, short, mi.fit_font(d, short, mi.FONT_KR, h * 0.22, w * 0.30), (255, 255, 255), stroke=edge, sw=0.5)
        return img
    vert = Image.new('RGBA', (u(ty1 - ty0), u(tear - tx0)), (0, 0, 0, 0))
    vd = ImageDraw.Draw(vert)
    mi.text_center(vd, (ty1 - ty0) / 2, (tear - tx0) / 2, label, mi.fit_font(vd, label, mi.FONT_LABEL, (tear - tx0) * 0.7, (ty1 - ty0) * 0.9, sw=0), edge, stroke=None, sw=0)
    vert = vert.rotate(90, expand=True)
    img.alpha_composite(vert, (u(tx0), u(ty0)))
    d = ImageDraw.Draw(img)
    mi.text_center(d, w / 2, h * 0.11, 'POSITION' if pos else 'LECTURE', mi.font(mi.FONT_LABEL, 7.2), (255, 255, 255), stroke=edge, sw=0.4)
    title = '포지션 초기화권' if pos else '특강 초기화권' if reset else '특강 즉시 완료권' if instant else '특강 행운권'
    sub = '원래 보직으로 되돌림' if pos else '결과 지우고 다시 수강' if reset else '수강을 바로 끝냄' if instant else '+5 확률 30% → 60%'
    mi.text_center(d, w / 2, h * 0.80, title, mi.fit_font(d, title, mi.FONT_KR, 9.6, w * 0.86), (255, 255, 255), stroke=edge, sw=0.5)
    mi.text_center(d, w / 2, h * 0.90, sub, mi.fit_font(d, sub, mi.FONT_KR, 7.0, w * 0.88), (255, 246, 200), stroke=edge, sw=0.3)
    return img


def save_lesson_items(version_dir, sheet_path=None):
    """특별강좌 초기화권 / 행운권 / 즉시 완료권: shop and inventory pictures (ItemCatalog files lesson_reset,
    lesson_lucky, lesson_instant)."""
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for p in (shop / 'small', inven / 'small'):
        p.mkdir(parents=True, exist_ok=True)
    sheet = []
    for kind in ('reset', 'lucky', 'instant'):
        big = mi.save_on((128, 128), lesson_ticket(kind, 79, 127), inven / f'lesson_{kind}.png')
        small = mi.save_on((128, 128), lesson_ticket(kind, 76, 67, small=True), inven / 'small' / f'lesson_{kind}.png')
        mi.save_on((128, 128), lesson_ticket(kind, 79, 127), shop / f'shop_lesson_{kind}.png')
        mi.save_on((128, 128), lesson_ticket(kind, 76, 67, small=True), shop / 'small' / f'shop_lesson_{kind}_small.png')
        sheet.append((big.crop((0, 0, mi.u(79), mi.u(127))), small.crop((0, 0, mi.u(76), mi.u(67)))))
    print('wrote InvenItem/lesson_reset, lesson_lucky, lesson_instant and shop_lesson_reset, shop_lesson_lucky, shop_lesson_instant')
    if sheet_path:
        cw, ch = mi.u(84), mi.u(127 + 72)
        img = Image.new('RGBA', (len(sheet) * cw, ch), (58, 62, 70, 255))
        for i, (big, small) in enumerate(sheet):
            img.alpha_composite(big, (i * cw + mi.u(2), 0))
            img.alpha_composite(small, (i * cw + mi.u(2), mi.u(130)))
        img.resize((img.width // 2, img.height // 2), Image.LANCZOS).save(sheet_path)
        print('sheet', sheet_path)


def save_roster_ticket(version_dir):
    """로스터 확장권 (item 20021, START2026 coupon, 2026-10-24): inventory pictures only."""
    inven = version_dir / 'InvenItem'
    (inven / 'small').mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 128), roster_plus(79, 127, title='로스터 확장권'), inven / 'roster_ticket.png')
    mi.save_on((128, 128), roster_plus(76, 67, small=True, title='로스터 확장권'), inven / 'small' / 'roster_ticket.png')
    print('wrote InvenItem/roster_ticket')


def save_pos_reset(version_dir):
    """포지션 변경 초기화권 (item 90841): shop and inventory pictures (ItemCatalog file pos_reset)."""
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for p in (shop / 'small', inven / 'small'):
        p.mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 128), lesson_ticket('pos', 79, 127), inven / 'pos_reset.png')
    mi.save_on((128, 128), lesson_ticket('pos', 76, 67, small=True), inven / 'small' / 'pos_reset.png')
    mi.save_on((128, 128), lesson_ticket('pos', 79, 127), shop / 'shop_pos_reset.png')
    mi.save_on((128, 128), lesson_ticket('pos', 76, 67, small=True), shop / 'small' / 'shop_pos_reset_small.png')
    print('wrote InvenItem/pos_reset and shop_pos_reset')


def save_tp_charge(version_dir):
    """Only the 충전권's pictures (shop and inventory), without redrawing the others."""
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for p in (shop / 'small', inven / 'small'):
        p.mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 128), tp_charge(79, 127), shop / 'shop_tp_charge.png')
    mi.save_on((128, 128), tp_charge(76, 67, small=True), shop / 'small' / 'shop_tp_charge_small.png')
    mi.save_on((128, 128), tp_charge(79, 127), inven / 'tp_charge.png')
    mi.save_on((128, 128), tp_charge(76, 67, small=True), inven / 'small' / 'tp_charge.png')
    print('wrote shop_tp_charge and InvenItem/tp_charge')


# 코칭스태프 카드팩 (user 2026-11-05: "코칭스태프 느낌이 나는 이미지가 카드팩 중앙에", draft liked the same night): a charcoal
# foil with gold lettering and three coaches of different clubs from the coach detail art (CoachCardDetailImage, the 수석코치
# with sunglasses between a 투수코치 and a 치어리더).
COACH_PACK_FIGURES = ('9070110', '9000110', '9134110')


def coach_figure(idx, height_px):
    from PIL import ImageFilter
    im = Image.open(Path(DATA_DIR) / 'cdn' / 'KBO' / 'CoachCardDetailImage' / '1' / f'{idx}_detail.png').convert('RGBA')
    im = im.crop(im.getbbox())
    im = im.resize((round(im.width * height_px / im.height), height_px), Image.LANCZOS)
    # White outline, like the coach card faces.
    edge = max(1, round(height_px / 120))
    glow = Image.new('RGBA', im.size, (255, 255, 255, 255))
    glow.putalpha(im.split()[3].filter(ImageFilter.MaxFilter(edge * 2 + 1)))
    glow.alpha_composite(im)
    return glow


def coach_pack(w, h, small=False):
    main_c, light, accent = (28, 32, 46), (92, 100, 128), (255, 206, 84)
    book = mi.book
    mi.book = lambda *a, **k: None  # the pack's emblem spot takes the coaches instead
    try:
        img = mi.pack(w, h, '코칭스태프 카드팩', '노말~엘리트', main_c, light, accent, emblem='book', header='COACHING STAFF', small=small)
    finally:
        mi.book = book
    px0, px1 = (w * 0.02, w * 0.98) if not small else (w * 0.22, w * 0.78)
    cx = (px0 + px1) / 2
    top, bottom = (h * 0.19, h * 0.575) if not small else (h * 0.17, h * 0.66)
    d = ImageDraw.Draw(img)
    r, cy = (bottom - top) * 0.40, top + (bottom - top) * 0.45
    d.ellipse((mi.u(cx - r), mi.u(cy - r), mi.u(cx + r), mi.u(cy + r)), fill=(255, 206, 84, 70), outline=accent, width=max(1, mi.u(0.5)))
    height = mi.u(bottom - top)
    left, mid, right = (coach_figure(i, round(height * (1.0 if i == COACH_PACK_FIGURES[1] else 0.8))) for i in COACH_PACK_FIGURES)
    layer = Image.new('RGBA', img.size, (0, 0, 0, 0))
    span = (px1 - px0) * (0.30 if not small else 0.26)
    for f, x in ((left, cx - span), (right, cx + span), (mid, cx)):
        layer.alpha_composite(f, (round(mi.u(x) - f.width / 2), mi.u(bottom) - f.height))
    # Kept inside the foil, below the crimped top.
    mask = Image.new('L', img.size, 0)
    ImageDraw.Draw(mask).rectangle((mi.u(px0) + 2, mi.u(h * 0.04 + 4.2), mi.u(px1) - 2, mi.u(bottom)), fill=255)
    clipped = Image.new('RGBA', img.size, (0, 0, 0, 0))
    clipped.paste(layer, (0, 0), mask)
    img.alpha_composite(clipped)
    return img


def save_coach_pack(version_dir):
    """코칭스태프 카드팩: shop product shop_pack_coach and inventory item 400011 (ItemCatalog file pack_coach, pack frame 90x149)."""
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for p in (shop / 'small', inven / 'small'):
        p.mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 128), coach_pack(79, 127), shop / 'shop_pack_coach.png')
    mi.save_on((128, 128), coach_pack(76, 67, small=True), shop / 'small' / 'shop_pack_coach_small.png')
    mi.save_on((128, 256), coach_pack(90, 149), inven / 'pack_coach.png')
    mi.save_on((128, 128), coach_pack(76, 67, small=True), inven / 'small' / 'pack_coach.png')
    print('wrote shop_pack_coach and InvenItem/pack_coach')


def save_legend_pack(version_dir):
    """레전드 선수팩 (2026-11-08): the shop product and the inventory item (ItemCatalog file "pack_legend", a pack frame)."""
    v = mi.PACKS['pack_legend']
    shop, inven = version_dir / 'ShopItem', version_dir / 'InvenItem'
    for d in (shop / 'small', inven / 'small'):
        d.mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 128), mi.pack(79, 127, *v), shop / 'shop_pack_legend.png')
    mi.save_on((128, 128), mi.pack(76, 67, *v, small=True), shop / 'small' / 'shop_pack_legend_small.png')
    mi.save_on((128, 256), mi.pack(90, 149, *v), inven / 'pack_legend.png')
    mi.save_on((128, 128), mi.pack(76, 67, *v, small=True), inven / 'small' / 'pack_legend.png')
    print('wrote shop_pack_legend and InvenItem/pack_legend')


def main():
    out = Path(DATA_DIR) / 'cdn' / 'KBO' / 'ItemImage' / VERSION / 'ShopItem'
    if '--only-tp-charge' in sys.argv_original:
        save_tp_charge(out.parent)
        return
    if '--only-legend-pack' in sys.argv_original:
        save_legend_pack(out.parent)
        return
    if '--only-coach-pack' in sys.argv_original:
        save_coach_pack(out.parent)
        return
    if '--only-roster-ticket' in sys.argv_original:
        save_roster_ticket(out.parent)
        return
    if '--only-pos-reset' in sys.argv_original:
        save_pos_reset(out.parent)
        return
    if '--only-cond' in sys.argv_original:
        save_cond_items(out.parent, SHEET)
        return
    if '--only-lesson' in sys.argv_original:
        save_lesson_items(out.parent, SHEET)
        return
    (out / 'small').mkdir(parents=True, exist_ok=True)
    sheet = []
    pictures = {name: (lambda v=v: mi.pack(79, 127, *v), lambda v=v: mi.pack(76, 67, *v, small=True)) for name, v in PRODUCTS.items()}
    pictures['shop_roster_plus'] = (lambda: roster_plus(79, 127), lambda: roster_plus(76, 67, small=True))
    pictures['shop_luckybag_mp'] = (lambda: mp_pouch(79, 127), lambda: mp_pouch(76, 67, small=True))
    pictures['shop_stat_redist'] = (lambda: stat_redist(79, 127), lambda: stat_redist(76, 67, small=True))
    pictures['shop_tp_charge'] = (lambda: tp_charge(79, 127), lambda: tp_charge(76, 67, small=True))
    # The MP주머니 is also an inventory item (ItemCatalog file "luckybag_mp"): pack frame 90x149 of 128x256.
    inven = Path(DATA_DIR) / 'cdn' / 'KBO' / 'ItemImage' / VERSION / 'InvenItem'
    (inven / 'small').mkdir(parents=True, exist_ok=True)
    mi.save_on((128, 256), mp_pouch(90, 149), inven / 'luckybag_mp.png')
    mi.save_on((128, 128), mp_pouch(76, 67, small=True), inven / 'small' / 'luckybag_mp.png')
    print('wrote InvenItem/luckybag_mp')
    # 스탯재분배권 in the inventory (ItemCatalog file "stat_redist", an item card 79x127 of 128x128, like the books).
    mi.save_on((128, 128), stat_redist(79, 127), inven / 'stat_redist.png')
    mi.save_on((128, 128), stat_redist(76, 67, small=True), inven / 'small' / 'stat_redist.png')
    print('wrote InvenItem/stat_redist')
    mi.save_on((128, 128), tp_charge(79, 127), inven / 'tp_charge.png')
    mi.save_on((128, 128), tp_charge(76, 67, small=True), inven / 'small' / 'tp_charge.png')
    print('wrote InvenItem/tp_charge')
    save_cond_items(out.parent)
    save_lesson_items(out.parent)
    save_roster_ticket(out.parent)
    save_pos_reset(out.parent)
    save_coach_pack(out.parent)
    for name, (big_fn, small_fn) in pictures.items():
        big = mi.save_on((128, 128), big_fn(), out / f'{name}.png')
        small = mi.save_on((128, 128), small_fn(), out / 'small' / f'{name}_small.png')
        sheet.append((big.crop((0, 0, mi.u(79), mi.u(127))), small.crop((0, 0, mi.u(76), mi.u(67)))))
        print('wrote', name)
    if SHEET:
        cw, ch = mi.u(84), mi.u(127 + 72)
        img = Image.new('RGBA', (len(sheet) * cw, ch), (58, 62, 70, 255))
        for i, (big, small) in enumerate(sheet):
            img.alpha_composite(big, (i * cw + mi.u(2), 0))
            img.alpha_composite(small, (i * cw + mi.u(2), mi.u(130)))
        img = img.resize((img.width // 2, img.height // 2), Image.LANCZOS)
        img.save(SHEET)
        print('sheet', SHEET)


if __name__ == '__main__':
    main()
