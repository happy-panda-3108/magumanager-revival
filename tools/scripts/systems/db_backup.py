"""Consistent copy of a live SQLite DB (the server may be running): python -I db_backup.py <source.db> <copy.db>

Systems session tool (in the repo since 2026-11-17; the scratch copy vanished). Opens the source read-only and uses SQLite's
online backup, so the copy is a consistent snapshot even while the server writes. Used for data copies that test servers run on.
"""
import sqlite3
import sys

src = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
dst = sqlite3.connect(sys.argv[2])
src.backup(dst)
dst.close()
src.close()
print("backed up", sys.argv[1], "->", sys.argv[2])
