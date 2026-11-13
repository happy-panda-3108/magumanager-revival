"""Append one entry to docs/기획기록.md (CRLF, UTF-8): python append_log.py <entry text file>

Systems session tool (kept in the repo since 2026-11-13; the scratch copy was lost twice). Re-read the log's tail just before
running it: both sessions append to the same file (CLAUDE.md rule 7).
"""
import sys

path = r"C:\Ma9MProject\docs\기획기록.md"
text = open(sys.argv[1], encoding="utf-8").read().strip("\n")
data = open(path, "rb").read()
if not data.endswith(b"\r\n"):
    data += b"\r\n"
block = "\r\n" + text.replace("\r\n", "\n").replace("\n", "\r\n") + "\r\n"
open(path, "wb").write(data + block.encode("utf-8"))
print("appended", len(block), "chars")
