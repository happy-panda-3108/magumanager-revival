"""List object types and sample names inside a Unity bundle."""
import sys, collections
import UnityPy
env = UnityPy.load(sys.argv[1])
limit = int(sys.argv[2]) if len(sys.argv) > 2 else 15
by_type = collections.defaultdict(list)
for obj in env.objects:
    t = obj.type.name
    name = None
    try:
        name = obj.peek_name()
    except Exception:
        pass
    by_type[t].append(name)
for t, names in sorted(by_type.items(), key=lambda x: -len(x[1])):
    print(f"{t}: {len(names)}  e.g. {[n for n in names if n][:limit]}")
