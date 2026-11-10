"""The fielding scripts of one league game's broadcast buffer (read-only), the plays where a runner scores or a ball is
thrown home. python dump_scripts.py <user> <season> <game> <home club> [max scene]"""
import sqlite3
import struct
import sys
import zlib

sys.stdout.reconfigure(encoding='utf-8')
user, season, game, home = (int(a) for a in sys.argv[1:5])
upto = int(sys.argv[5]) if len(sys.argv) > 5 else 999
c = sqlite3.connect('file:C:/Ma9MProject/server/data/ma9m.db?mode=ro', uri=True)
buff = c.execute('select buff from league_game where user_code=? and season=? and game=? and home=?', (user, season, game, home)).fetchone()[0]
assert buff[-4:] == b'MSCR' or True
offset = struct.unpack_from('<i', buff, len(buff) - 8)[0]
raw_len, packed_len = struct.unpack_from('<ii', buff, offset)
raw = zlib.decompress(bytes(buff[offset + 8: offset + 8 + packed_len]), -15)
pos = 0
scenes = raw[0]
pos = 1
scripts = []
for i in range(scenes):
    n = struct.unpack_from('<H', raw, pos)[0]
    pos += 2
    scripts.append(raw[pos:pos + n] if n else None)
    pos += n
KIND = {1: 'BallFly', 2: 'BallRoll', 3: 'BallHeld', 4: 'BallThrow', 5: 'BallLoose', 10: 'Move', 11: 'Catch', 12: 'Dive', 13: 'Throw', 14: 'Cover',
        15: 'Tag', 16: 'Wait', 20: 'Run', 21: 'Slide', 22: 'Out', 23: 'Safe', 24: 'Score', 25: 'Back', 26: 'Stand', 27: 'Peel'}
print('scenes', scenes, 'with scripts', sum(1 for s in scripts if s))
for i, s in enumerate(scripts):
    if not s or i > upto:
        continue
    ver, ev, la, h, end, count = struct.unpack_from('<BHhhHB', s, 0)
    p = 10
    segs = []
    for k in range(count):
        kind, actor, arg, style, t0, t1, x0, z0, x1, z1 = struct.unpack_from('<BBBBHHhhhh', s, p)
        p += 16
        segs.append((KIND.get(kind, kind), actor, arg, style, t0 / 1000, t1 / 1000, x0 / 100, z0 / 100, x1 / 100, z1 / 100))
    interesting = any(g[0] in ('Score', 'Tag', 'Slide') for g in segs) or any(g[0] == 'BallThrow' and abs(g[8]) < 3 and abs(g[9]) < 3 for g in segs)
    if not interesting:
        continue
    print(f'=== scene {i}: ev {ev / 100:.1f} m/s la {la / 10:.1f} h {h / 10:.1f} end {end / 1000:.2f}s, {count} segs')
    for g in segs:
        print(f'  {g[0]:<9} a{g[1]:<2} {g[4]:6.2f}-{g[5]:6.2f}  ({g[6]:7.2f},{g[7]:7.2f}) -> ({g[8]:7.2f},{g[9]:7.2f}) arg{g[2]} st{g[3]}')
