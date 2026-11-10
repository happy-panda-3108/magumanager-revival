"""Software preview of a skinned mesh in its rest pose with a texture (front / back / side), for checking
uniform transfers without Unity.

usage: mesh_preview.py <out png> <npz>:<submesh>:<texture png or #rrggbb>[:<uv offset u,v,scale>] [...]
         [--rot deg] [--size px]
Several parts may be listed; each is drawn with its own texture. Lambert light from the front-left.
"""
import sys

import numpy as np
from PIL import Image


def load_part(spec):
    bits = spec.split("|")
    path, sub, tex = bits[0], int(bits[1]), bits[2]
    d = np.load(path)
    tri = d[f"tri{sub}"]
    if tex.startswith("#"):
        img = np.zeros((2, 2, 3), np.float32)
        img[:] = [int(tex[i:i + 2], 16) for i in (1, 3, 5)]
    else:
        img = np.asarray(Image.open(tex).convert("RGB"), np.float32)
    return d["pos"].astype(np.float64), d["uv0"].astype(np.float64), tri, img


def render(parts, yaw_deg, size):
    yaw = np.radians(yaw_deg)
    # View: camera looks along +Y at the model (front faces -Y), Z up; yaw turns the model.
    rot = np.array([[np.cos(yaw), -np.sin(yaw), 0], [np.sin(yaw), np.cos(yaw), 0], [0, 0, 1]])
    allp = np.concatenate([p[np.unique(t)] for p, _, t, _ in parts])   # frame what is drawn (feet alone fill the view)
    lo, hi = allp.min(0), allp.max(0)
    centre = (lo + hi) / 2
    scale = size * 0.9 / max(hi[2] - lo[2], hi[0] - lo[0], hi[1] - lo[1])
    img = np.full((size, size, 3), 40.0)
    zbuf = np.full((size, size), np.inf)
    light = np.array([-0.4, -0.7, 0.6])
    light /= np.linalg.norm(light)
    for pos, uv, tri, tex in parts:
        p = (pos - centre) @ rot.T
        sx = -p[:, 0] * scale + size / 2   # Unity mesh space is left-handed: +X is the model's right
        sy = -p[:, 2] * scale + size / 2
        depth = p[:, 1]
        th, tw = tex.shape[:2]
        for a, b, c in tri:
            xs, ys = np.array([sx[a], sx[b], sx[c]]), np.array([sy[a], sy[b], sy[c]])
            n = np.cross(p[b] - p[a], p[c] - p[a])
            nn = np.linalg.norm(n)
            if nn == 0:
                continue
            n /= nn
            shade = 0.45 + 0.55 * abs(np.dot(n, light))
            minx, maxx = int(max(np.floor(xs.min()), 0)), int(min(np.ceil(xs.max()), size - 1))
            miny, maxy = int(max(np.floor(ys.min()), 0)), int(min(np.ceil(ys.max()), size - 1))
            if maxx < minx or maxy < miny:
                continue
            gx, gy = np.meshgrid(np.arange(minx, maxx + 1) + 0.5, np.arange(miny, maxy + 1) + 0.5)
            den = (ys[1] - ys[2]) * (xs[0] - xs[2]) + (xs[2] - xs[1]) * (ys[0] - ys[2])
            if abs(den) < 1e-9:
                continue
            l0 = ((ys[1] - ys[2]) * (gx - xs[2]) + (xs[2] - xs[1]) * (gy - ys[2])) / den
            l1 = ((ys[2] - ys[0]) * (gx - xs[2]) + (xs[0] - xs[2]) * (gy - ys[2])) / den
            l2 = 1 - l0 - l1
            m = (l0 >= 0) & (l1 >= 0) & (l2 >= 0)
            if not m.any():
                continue
            z = l0 * depth[a] + l1 * depth[b] + l2 * depth[c]
            iy, ix = (gy - 0.5).astype(int), (gx - 0.5).astype(int)
            closer = m & (z < zbuf[iy, ix])
            if not closer.any():
                continue
            u = l0 * uv[a, 0] + l1 * uv[b, 0] + l2 * uv[c, 0]
            v = l0 * uv[a, 1] + l1 * uv[b, 1] + l2 * uv[c, 1]
            tx = np.clip((u % 1.0) * tw, 0, tw - 1).astype(int)
            ty = np.clip((1 - v % 1.0) * th, 0, th - 1).astype(int)
            col = tex[ty, tx] * shade
            zbuf[iy[closer], ix[closer]] = z[closer]
            img[iy[closer], ix[closer]] = col[closer]
    return img


def main():
    out = sys.argv[1]
    args = sys.argv[2:]
    size, rots = 360, [0, 180, 90]
    if "--size" in args:
        size = int(args[args.index("--size") + 1])
    if "--rot" in args:
        rots = [float(x) for x in args[args.index("--rot") + 1].split(",")]
    parts = [load_part(a) for a in args if "|" in a]
    views = [render(parts, r, size) for r in rots]
    Image.fromarray(np.clip(np.concatenate(views, 1), 0, 255).astype(np.uint8)).save(out)


if __name__ == "__main__":
    main()
