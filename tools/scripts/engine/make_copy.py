"""Data copy for server dev checks (engine session; kept in the repo since 2026-11-14 after the scratch copy was lost).

  python -I make_copy.py <out dir>

Copies the live server's ma9m.db with sqlite's online backup (safe while the server runs, read-only on the source),
plus sim\, tables\, fences\ and uniforms.tsv — what the server's dev flags (--autolineup, --tcsim, --posttest, ...) need.
cdn\ and backup\ are left out. Run the server on the copy with MA9M_GAME_PORT set to a free port (dev flags exit before
the ports open, but set it anyway).
"""
import os
import shutil
import sqlite3
import sys

SRC = r"C:\Ma9MProject\server\data"


def main():
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(2)
    dst = sys.argv[1]
    os.makedirs(dst, exist_ok=True)
    a = sqlite3.connect("file:" + os.path.join(SRC, "ma9m.db") + "?mode=ro", uri=True)
    b = sqlite3.connect(os.path.join(dst, "ma9m.db"))
    a.backup(b)
    b.close()
    a.close()
    for d in ("sim", "tables", "fences"):
        t = os.path.join(dst, d)
        if os.path.exists(t):
            shutil.rmtree(t)
        shutil.copytree(os.path.join(SRC, d), t)
    shutil.copy2(os.path.join(SRC, "uniforms.tsv"), dst)
    print("copied to", dst)


if __name__ == "__main__":
    main()
