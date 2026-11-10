"""Lists packet constructors in Ma9MPacket.cs whose fields[k] assignments do not match the
declared field count (base(sup, sub, count)): an index out of range throws when the packet is
created; a missing index leaves a null field that throws when the packet is serialized."""
import re
import sys

text = open(sys.argv[1], encoding='utf-8').read()
ctor = re.compile(r'public (\w+)\(\)\s*:\s*base\((\d+),\s*(\d+)(?:,\s*(\d+))?\)\s*\{(.*?)\n\t\t\t\}', re.S)
bad = 0
for m in ctor.finditer(text):
    name, count, body = m.group(1), m.group(4), m.group(5)
    count = int(count) if count else 0
    idx = sorted(int(k) for k in re.findall(r'fields\[(\d+)\]\s*=', body))
    if idx != list(range(count)):
        bad += 1
        line = text.count('\n', 0, m.start()) + 1
        print(f'{line}: {name} count={count} indices={idx}')
print(f'{bad} suspicious constructor(s)')
