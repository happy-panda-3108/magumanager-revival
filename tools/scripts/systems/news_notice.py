"""Add 공지 lines to the lobby news band (server NewsFeed, table news): python -I news_notice.py [--db <ma9m.db>] "<text>" ["<text>" ...]
Change words in the 공지 already there (their time and order kept): python -I news_notice.py [--db <ma9m.db>] --replace "<old>" "<new>"

Systems session tool (2026-11-17; user 11:5x: "새로 업데이트되는 기능들에 대해서는 공지로 담아도 될 것 같음"). Each text becomes a news row
starting with "[공지] " (added when missing). The server (NewsFeed.Recent) shows the notices of the last 7 days first, at most 4,
then the news of the last 48 hours; a server before 11-17 12:xx shows them as plain news for 48 hours. Safe while the server runs
(one small insert). Default DB: the live server\\data\\ma9m.db — pass --db for a copy.
"""
import datetime
import sqlite3
import sys

MARK = "[공지]"


def main():
    args = sys.argv[1:]
    db = r"C:\Ma9MProject\server\data\ma9m.db"
    if args[:1] == ["--db"]:
        db, args = args[1], args[2:]
    if not args:
        print(__doc__)
        return 1
    c = sqlite3.connect(db, timeout=10)
    c.execute("CREATE TABLE IF NOT EXISTS news (id INTEGER PRIMARY KEY AUTOINCREMENT, at TEXT NOT NULL, text TEXT NOT NULL)")
    # 11-17 18:xx: the 공지 of 11-17 12:06 still said "에이전트와" after the user's "선수와 협상" change.
    if args[:1] == ["--replace"]:
        if len(args) != 3:
            print(__doc__)
            return 1
        old, new = args[1], args[2]
        rows = c.execute("SELECT id, text FROM news WHERE text LIKE ? AND instr(text, ?) > 0", (MARK + "%", old)).fetchall()
        for rid, text in rows:
            c.execute("UPDATE news SET text = ? WHERE id = ?", (text.replace(old, new), rid))
            print("changed %d: %s" % (rid, text.replace(old, new)))
        c.commit()
        c.close()
        print("%d 공지 changed" % len(rows))
        return 0
    # Oldest first, so the first text given is the newest (shown first).
    for text in reversed(args):
        text = " ".join(text.split())
        if not text.startswith(MARK):
            text = MARK + " " + text
        # As the server writes it (DateTime.Now "o": local time with its offset), so the time comparisons line up.
        at = datetime.datetime.now().astimezone().isoformat(timespec="microseconds")
        c.execute("INSERT INTO news (at, text) VALUES (?, ?)", (at, text))
        print("added:", text)
    c.commit()
    c.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
