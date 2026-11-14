# The 리그 정보 popup image (Atlas_TopMenu sprite 'leagueinfo', 782x414 at x0 y610) redrawn for six tiers (2026-11-08), then for
# seven (2026-11-14, 더블A between 루키 and 트리플A; caps 120/130/140/145/150/155/160).
# Keeps the frame, the panels and the original badges; clears the left panel's text and draws a new table.
# Input: the original sprite, port\ripped\ExportedProject\Assets\Texture2D\Atlas_TopMenu.png cropped at (0, 610, 782, 1024).
import sys
from PIL import Image, ImageDraw, ImageFont

src, out = sys.argv[1], sys.argv[2]
im = Image.open(src).convert('RGBA')
W, H = im.size
REG = 'C:/Windows/Fonts/malgun.ttf'
BOLD = 'C:/Windows/Fonts/malgunbd.ttf'

# Badges from the old table (row bands found by colour), before anything is painted over.
bands = {'챔피언스': 114, '레전드': 170, '메이저': 226, '트리플A': 254, '더블A': 282, '루키': 338, '비기너': 366}
badges = {k: im.crop((219, y - 2, 289, y + 23)) for k, y in bands.items()}

# The yellow of the panel titles.
title_rgb = max((im.getpixel((x, y)) for x in range(22, 120) for y in range(58, 80)), key=lambda p: (p[0] + p[1]) - 2 * p[2])[:3]

# Left panel: each row painted from its two text-free ends (x 22 and 190), which keeps both gradients.
L0, L1 = 22, 190
for y in range(48, 392):
    a, b = im.getpixel((L0, y)), im.getpixel((L1, y))
    for x in range(L0, L1 + 1):
        t = (x - L0) / (L1 - L0)
        im.putpixel((x, y), tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(4)))

d = ImageDraw.Draw(im)
ft_title = ImageFont.truetype(BOLD, 15)
ft_text = ImageFont.truetype(REG, 12)


def wrap(text, font, width):
    # By words (Korean breaks between 어절, as the original text did).
    lines, cur = [], ''
    for word in text.split(' '):
        trial = word if not cur else cur + ' ' + word
        if d.textlength(trial, font=font) > width and cur:
            lines.append(cur)
            cur = word
        else:
            cur = trial
    if cur:
        lines.append(cur)
    return lines


def paragraph(y, title, items):
    d.text((28, y), title, font=ft_title, fill=title_rgb)
    y += 24
    for item in items:
        for k, line in enumerate(wrap(item, ft_text, 158)):
            d.text((28 if k == 0 else 34, y), line, font=ft_text, fill=(235, 235, 235))
            y += 16
        y += 4
    return y


paragraph(58, '볼 제한', [
    '-볼 제한은 리그 등급에 따라 다릅니다.',
    '-1군 선수들의 볼 합이 볼 제한을 넘으면 경기에서 능력치가 크게 깎입니다.',
    '-윗 리그로 올라갈수록 볼 제한이 높아져 쓸 수 있는 선수의 폭이 넓어집니다.',
])
paragraph(298, '리그 승강 조건', [
    '-시즌이 끝나고 다음 시즌 리그를 짤 때 승강합니다.',
    '-최종 순위: 포스트시즌 순위 (못 나간 팀은 정규리그 순위)',
])

# The table.
X = [207, 300, 386, 516, 642, 769]
Y0, HDR1, HDR2, Y1 = 55, 80, 106, 390
rows = [
    ('챔피언스', '160', '-', '최종 1~3위', '나머지 (레전드로)'),
    ('레전드', '155', '최종 1~3위', '최종 4~6위', '최종 7~10위'),
    ('메이저', '150', '포스트시즌 1~4위', '나머지', '정규리그 8~10위'),
    ('트리플A', '145', '포스트시즌 1~4위', '나머지', '정규리그 8~10위'),
    ('더블A', '140', '포스트시즌 1~4위', '나머지', '정규리그 8~10위'),
    ('루키', '130', '포스트시즌 1~4위', '나머지', '정규리그 8~10위'),
    ('비기너', '120', '정규리그 1~4위', '나머지', '없음'),
]
RH = (Y1 - HDR2) / len(rows)
CELL, HEAD, GRID = (34, 34, 34, 255), (47, 47, 47, 255), (70, 70, 70, 255)
ft_head = ImageFont.truetype(BOLD, 13)
ft_cell = ImageFont.truetype(REG, 13)
ft_ball = ImageFont.truetype(BOLD, 14)

d.rectangle((X[0], Y0, X[-1], Y1), fill=CELL)
d.rectangle((X[0], Y0, X[-1], HDR2), fill=HEAD)


def centred(box, text, font, fill):
    x0, y0, x1, y1 = box
    w = d.textlength(text, font=font)
    bb = d.textbbox((0, 0), text, font=font)
    h = bb[3] - bb[1]
    d.text(((x0 + x1 - w) / 2, (y0 + y1 - h) / 2 - bb[1]), text, font=font, fill=fill)


# Header: 볼 제한 over 최대 볼 제한, 리그 승강 조건 over the three conditions.
centred((X[1], Y0, X[2], HDR1), '볼 제한', ft_head, (225, 225, 225))
centred((X[2], Y0, X[5], HDR1), '리그 승강 조건', ft_head, (225, 225, 225))
for i, t in zip(range(1, 5), ['최대 볼 제한', '승격 조건', '잔류 조건', '강등 조건']):
    centred((X[i], HDR1, X[i + 1], HDR2), t, ft_head, (225, 225, 225))

# Cells, merging runs of the same text down columns 2-4 (as the original table did).
for r, row in enumerate(rows):
    y0, y1 = HDR2 + r * RH, HDR2 + (r + 1) * RH
    b = badges[row[0]]
    im.alpha_composite(b, (int((X[0] + X[1] - b.width) / 2), int((y0 + y1 - b.height) / 2)))
    centred((X[1], y0, X[2], y1), row[1], ft_ball, (255, 255, 255))
spans = []
for c in (2, 3, 4):
    r = 0
    while r < len(rows):
        e = r
        while e + 1 < len(rows) and rows[e + 1][c] == rows[r][c]:
            e += 1
        spans.append((c, r, e))
        centred((X[c], HDR2 + r * RH, X[c + 1], HDR2 + (e + 1) * RH), rows[r][c], ft_cell, (255, 255, 255))
        r = e + 1

# Grid: the outline, column lines, header lines, and row lines except inside merged cells.
d.rectangle((X[0], Y0, X[-1], Y1), outline=GRID, width=2)
d.line((X[2], HDR1, X[5], HDR1), fill=GRID, width=1)
d.line((X[0], HDR2, X[5], HDR2), fill=GRID, width=2)
for i in range(1, 5):
    d.line((X[i], Y0 if i in (1, 2) else HDR1, X[i], Y1), fill=GRID, width=1)
for r in range(1, len(rows)):
    y = round(HDR2 + r * RH)
    d.line((X[0], y, X[2], y), fill=GRID, width=1)
    for c in (2, 3, 4):
        if not any(cc == c and s < r <= e for cc, s, e in spans):
            d.line((X[c], y, X[c + 1], y), fill=GRID, width=1)

im.save(out)
print('saved', out, im.size, 'title colour', title_rgb)
