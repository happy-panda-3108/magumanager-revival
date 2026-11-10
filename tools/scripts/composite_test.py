"""Composite test cards: uniform pose + face + eyebrow (+beard)."""
import sys, os
from PIL import Image
base = sys.argv[1]
def L(atlas, sprite):
    return Image.open(os.path.join(base, atlas, sprite + ".png")).convert("RGBA")
cards = [
    ("ob1", "BPose1", "FaceBPose", "Pose1_Face_1_1", "EyebrowBpose", "Pose1_Eyebrow1_1", None),
    ("lot6", "BPose2", "FaceBPose", "Pose2_Face_2_2", "EyebrowBpose", "Pose2_Eyebrow2_2", ("BeardBPose", "Pose2_Beard3_2")),
    ("hyn1", "RPPose1", "FaceRPPose", "Pose1_Face_3_3", "EyebrowRPPose", "Pose1_Eyebrow3_3", None),
    ("doo4", "BPose3", "FaceBPose", "Pose3_Face_4_1", "EyebrowBpose", "Pose3_Eyebrow1_1", None),
]
row = Image.new("RGBA", (118 * len(cards) * 2, 162 * 2), (40, 40, 40, 255))
for i, (ua, us, fa, fs, ea, es, extra) in enumerate(cards):
    img = L(ua, us)
    for a, s in [(fa, fs), (ea, es)] + ([extra] if extra else []):
        layer = L(a, s)
        canvas = Image.new("RGBA", img.size)
        canvas.paste(layer, (0, 0))
        img = Image.alpha_composite(img, canvas)
    bg = Image.new("RGBA", img.size, (40, 40, 40, 255))
    row.paste(Image.alpha_composite(bg, img).resize((236, 324)), (i * 236, 0))
row.save(sys.argv[2])
print("ok", [L(c[0], c[1]).size for c in cards], L("FaceBPose", "Pose1_Face_1_1").size)
