"""Disassemble IL2CPP methods with symbol annotations.

usage: il2cpp_disasm.py <GameAssembly.dll> <script.json> <method-name-substring or 0xRVA> [max_insns]
Annotates calls with method names and RIP-relative loads with string literals / metadata names.
"""
import bisect
import json
import sys

import pefile
from capstone import CS_ARCH_X86, CS_MODE_64, Cs
from capstone.x86 import X86_OP_MEM, X86_REG_RIP

dll_path, script_path, target = sys.argv[1], sys.argv[2], sys.argv[3]
max_insns = int(sys.argv[4]) if len(sys.argv) > 4 else 600

pe = pefile.PE(dll_path, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
script = json.load(open(script_path, encoding="utf-8"))

methods = {}
for m in script["ScriptMethod"]:
    methods.setdefault(m["Address"], m["Name"])
starts = sorted(set(script["Addresses"]) | set(methods))
syms = {}
for s in script["ScriptString"]:
    syms[s["Address"]] = "STR " + json.dumps(s["Value"], ensure_ascii=False)
for s in script["ScriptMetadata"]:
    syms[s["Address"]] = "META " + s["Name"]
for s in script["ScriptMetadataMethod"]:
    syms[s["Address"]] = "METHOD " + s["Name"]


def read(rva, size):
    return pe.get_data(rva, size)


def func_range(rva):
    i = bisect.bisect_right(starts, rva)
    end = starts[i] if i < len(starts) else rva + 0x2000
    return rva, min(end, rva + 0x4000)


if target.startswith("0x"):
    targets = [(int(target, 16), methods.get(int(target, 16), "?"))]
else:
    targets = [(a, n) for a, n in methods.items() if target in n]

md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True
for rva, name in targets:
    s, e = func_range(rva)
    print(f"\n===== {name} @ {hex(rva)} size={e - s:#x}")
    code = read(s, e - s)
    for n, ins in enumerate(md.disasm(code, base + s)):
        if n >= max_insns:
            break
        note = ""
        if ins.mnemonic in ("call", "jmp") and ins.operands and ins.operands[0].type == 2:
            t = ins.operands[0].imm - base
            note = methods.get(t, "")
        for op in ins.operands:
            if op.type == X86_OP_MEM and op.mem.base == X86_REG_RIP:
                addr = ins.address + ins.size + op.mem.disp - base
                note = syms.get(addr, methods.get(addr, note or f"[rva {addr:#x}]"))
        print(f"{ins.address - base:#09x}  {ins.mnemonic:6} {ins.op_str:40} {note}")
