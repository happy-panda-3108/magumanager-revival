"""Cut the figure out of an illustration-framed card detail (engine session, 2026-11-10).

make_card_images.py draws elite/legend details from the face's illustration since 11-10; its rim-based mask leaves
background on some pictures. This script refines a list of details with GrabCut (OpenCV, in tools/venv): the raw
framing (make_card_images ... --details-only --detail-raw) gives the colours, the rim mask (a normal --details-only
run) the starting guess.

  tools/venv/Scripts/python.exe tools/scripts/detail_cutout.py <raw dir> <rim-mask dir> <out dir> idx,idx,...
"""
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image
from scipy import ndimage


def cutout(raw_path, rim_path, prior_path=None):
    raw = np.asarray(Image.open(raw_path).convert('RGBA'))
    rim = np.asarray(Image.open(rim_path).convert('RGBA'))[..., 3]
    rgb = raw[..., :3].copy()
    h, w = rim.shape
    guess = rim > 200
    sure_bg = None
    if prior_path is not None:
        # The old sprite detail in the same pose (이종범, 홍성흔 on busy backgrounds): its outline, widened, as the guess.
        old = np.asarray(Image.open(prior_path).convert('RGBA').resize((w, h)))[..., 3] > 128
        guess = ndimage.binary_dilation(old, iterations=12)
        sure_bg = ~ndimage.binary_dilation(old, iterations=40)
    elif guess.mean() > 0.8:
        # The rim mask failed (it kept the whole picture): a bust-shaped guess instead.
        yy, xx = np.mgrid[:h, :w]
        guess = ((xx - w / 2) / (0.42 * w)) ** 2 + ((yy - 0.62 * h) / (0.6 * h)) ** 2 < 1
    mask = np.full((h, w), cv2.GC_PR_BGD, np.uint8)
    mask[guess] = cv2.GC_PR_FGD
    core = ndimage.binary_erosion(guess, iterations=25)
    mask[core] = cv2.GC_FGD
    # Sure background: the top rows, both sides all the way down and the bottom corners (a bust meets the bottom
    # edge only in its middle; background there survived the first tries: 켈리A, 홍성흔, 오승환, 이종범).
    edge = np.zeros((h, w), bool)
    edge[:6, :] = edge[:, :6] = edge[:, -6:] = True
    edge[-6:, :int(0.16 * w)] = edge[-6:, -int(0.16 * w):] = True
    mask[edge & ~core] = cv2.GC_BGD
    if sure_bg is not None:
        mask[sure_bg] = cv2.GC_BGD
    bgd, fgd = np.zeros((1, 65)), np.zeros((1, 65))
    cv2.grabCut(cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR), mask, None, bgd, fgd, 6, cv2.GC_INIT_WITH_MASK)
    fg = (mask == cv2.GC_FGD) | (mask == cv2.GC_PR_FGD)
    lab, n = ndimage.label(fg)
    if n > 1:
        sizes = ndimage.sum(fg, lab, range(1, n + 1))
        fg = np.isin(lab, [k + 1 for k, s in enumerate(sizes) if s >= 0.02 * fg.sum()])
    fg = ndimage.binary_fill_holes(fg)
    alpha = (np.clip(cv2.GaussianBlur(fg.astype(np.float32), (0, 0), 1.2), 0, 1) * 255).astype(np.uint8)
    return Image.fromarray(np.dstack([rgb, alpha]), 'RGBA')


def main():
    raw, rim, out = (Path(a) for a in sys.argv[1:4])
    # --prior <old detail dir> <idx,idx>: those cards start from the old sprite detail's outline.
    prior_dir, prior_ids = None, set()
    if '--prior' in sys.argv:
        k = sys.argv.index('--prior')
        prior_dir, prior_ids = Path(sys.argv[k + 1]), set(sys.argv[k + 2].split(','))
    out.mkdir(parents=True, exist_ok=True)
    for idx in sys.argv[4].split(','):
        name = f'{idx}_detail.png'
        prior = prior_dir / name if idx in prior_ids else None
        cutout(raw / name, rim / name, prior).save(out / name, optimize=True)
        print('cut', name)


if __name__ == '__main__':
    main()
