"""Convert UTF-16 (or UTF-8) table text files to UTF-8."""
import os
import sys

src, dst = sys.argv[1], sys.argv[2]
os.makedirs(dst, exist_ok=True)
for name in os.listdir(src):
    raw = open(os.path.join(src, name), "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        text = raw.decode("utf-16")
    else:
        text = raw.decode("utf-8-sig", errors="replace")
    with open(os.path.join(dst, name), "w", encoding="utf-8", newline="") as f:
        f.write(text)
print("done", len(os.listdir(dst)))
