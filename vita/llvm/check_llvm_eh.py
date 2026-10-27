#!/usr/bin/env python3
"""check_llvm_eh.py <aot dir> <linked elf>: static checks of the LLVM AOT images in a linked Vita ELF, done the way the Mono
runtime will read them (aot-runtime.c decode_llvm_mono_eh_frame / compute_llvm_code_range, unwind.c
mono_unwind_decode_llvm_mono_fde), for every module that has an <M>-llvm.o in <aot dir>:

 1. the method address table of the Mono part (<M>.dll.s "method_addresses": one bl/blx per method index) resolves, in the ELF,
    to the method symbols (LLVM methods are global symbols of <M>-llvm.o, the Mono-backend ones local labels of <M>.dll.o), and
    the linked table, decoded like aot-runtime.c get_call_table_entry (BLX imm -> Thumb target | 1, BL -> A32), gives exactly those
    addresses (no veneer, no BL/BLX mix-up);
 2. the Mono EH table (mono_aot_<prefix>_eh_frame, emitted by llc -enable-mono-eh-frame): version 3, fde_count, the
    (method index, FDE offset) table + the end entry, the CIE (code align 1, data align -4, return column 14 = lr: the values
    mono_unwind_decode_llvm_mono_fde asserts);
 3. the table order is the address order in the ELF: the runtime binary-searches it and takes [methods[t0], methods[tlast] +
    len) as the module's LLVM code range (is_llvm_code / is_thumb_code). llc puts every LLVM method in its own retained section
    (.text.<name>, SHF_GNU_RETAIN, because of llvm.used), so this depends on ld keeping input order;
 4. no Mono-backend method of the module lies inside its LLVM code range, and in Thumb mode every table method is a Thumb symbol;
 5. every FDE decodes: CFA program ops are counted (an LLVM method with a frame must describe it, or the runtime cannot unwind
    through it), LSDA presence (try/catch/finally clauses) is counted.
Prints one line per module and a summary; exit 1 on any failure.
"""
import os, re, struct, subprocess, sys

def u32(d, o): return struct.unpack_from("<I", d, o)[0]
def s32(d, o): return struct.unpack_from("<i", d, o)[0]

class Elf:
    def __init__(self, path):
        d = self.d = open(path, "rb").read()
        assert d[:4] == b"\x7fELF" and d[4] == 1
        shoff = u32(d, 0x20); shentsize, shnum, shstrndx = struct.unpack_from("<HHH", d, 0x2e)
        sh = [struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize) for i in range(shnum)]
        so = sh[shstrndx][4]
        name = lambda off, base: d[base + off: d.index(b"\0", base + off)].decode()
        self.secs = [dict(name=name(s[0], so), type=s[1], flags=s[2], addr=s[3], off=s[4], size=s[5], link=s[6]) for s in sh]
        self.syms = {}
        for s in self.secs:
            if s["type"] != 2: continue  # SHT_SYMTAB
            strtab = self.secs[s["link"]]["off"]
            for i in range(s["size"] // 16):
                st_name, st_value, st_size, st_info, st_other, st_shndx = struct.unpack_from("<IIIBBH", d, s["off"] + i * 16)
                if not st_name or st_shndx == 0: continue
                n = name(st_name, strtab)
                self.syms.setdefault(n, []).append((st_value, st_size, st_info & 0xf, st_info >> 4))
    def read(self, addr, n):
        for s in self.secs:
            if s["type"] != 8 and s["addr"] <= addr < s["addr"] + s["size"] and s["flags"] & 2:
                o = s["off"] + addr - s["addr"]; return self.d[o:o + n]
        raise KeyError(hex(addr))

def uleb(b, p):
    r = sh = 0
    while True:
        x = b[p]; p += 1; r |= (x & 0x7f) << sh; sh += 7
        if not x & 0x80: return r, p
def sleb(b, p):
    r = sh = 0
    while True:
        x = b[p]; p += 1; r |= (x & 0x7f) << sh; sh += 7
        if not x & 0x80:
            if x & 0x40: r -= 1 << sh
            return r, p

MONO_OPS = {0x40: "advance_loc", 0x80: "offset", 0x02: "advance_loc1", 0x03: "advance_loc2", 0x04: "advance_loc4",
            0x05: "offset_extended", 0x08: "same_value", 0x0a: "remember_state", 0x0b: "restore_state", 0x0c: "def_cfa",
            0x0d: "def_cfa_register", 0x0e: "def_cfa_offset", 0x11: "offset_extended_sf"}   # what unwind.c mono_unwind_frame decodes
def reg_ok(r): return 0 <= r <= 15 or 264 <= r <= 271   # ARM: r0-r15, d8-d15 (unwind.c map_hw_reg_to_dwarf_reg)

def cfa_ops(b, stats):
    """decode the DW_CFA program in b (until the first DW_CFA_nop); count ops by name, flag ops/registers Mono cannot unwind"""
    p = n = 0
    while p < len(b):
        op = b[p]; p += 1
        hi = op & 0xc0
        if op == 0: break
        n += 1
        key = hi if hi else op
        stats[key] = stats.get(key, 0) + 1
        regs = []
        if hi == 0x40: pass
        elif hi == 0x80: _, p = uleb(b, p); regs.append(op & 0x3f)
        elif hi == 0xc0: regs.append(op & 0x3f)
        elif op == 0x02: p += 1
        elif op == 0x03: p += 2
        elif op == 0x04: p += 4
        elif op == 0x0c: r, p = uleb(b, p); _, p = uleb(b, p); regs.append(r)
        elif op in (0x0d, 0x07, 0x08, 0x06): r, p = uleb(b, p); regs.append(r)
        elif op == 0x0e: _, p = uleb(b, p)
        elif op == 0x05: r, p = uleb(b, p); _, p = uleb(b, p); regs.append(r)
        elif op == 0x09: r, p = uleb(b, p); r2, p = uleb(b, p); regs += [r, r2]
        elif op in (0x0a, 0x0b): pass
        elif op == 0x11: r, p = uleb(b, p); _, p = sleb(b, p); regs.append(r)
        elif op == 0x12: r, p = uleb(b, p); _, p = sleb(b, p); regs.append(r)
        elif op == 0x13: _, p = sleb(b, p)
        else: return n, f"undecodable op 0x{op:02x}"
        if key not in MONO_OPS: stats["unsupported"] = stats.get("unsupported", 0) + 1
        for r in regs:
            if not reg_ok(r): stats["badreg"] = stats.get("badreg", 0) + 1
    return n, None

def main(aotdir, elfpath):
    elf = Elf(elfpath)
    def addr(name):
        v = elf.syms.get(name)
        return v[0][0] if v and len(v) == 1 else (None if not v else "dup")
    bad = 0; tot = dict(fdes=0, with_cfa=0, lsda=0); tot_ops = {}
    for f in sorted(os.listdir(aotdir)):
        if not f.endswith("-llvm.o"): continue
        m = f[:-7]
        sfile = os.path.join(aotdir, m + ".dll.s")
        nmout = subprocess.run([os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-nm"), os.path.join(aotdir, f)], capture_output=True, text=True).stdout
        mm = re.search(r" t (mono_aot_(\S+)_eh_frame)$", nmout, re.M)
        if not mm: print(f"{m}: no Mono EH table (no LLVM methods?)"); continue
        ehsym = mm.group(1)
        llvm_globals = set(l.split()[2] for l in nmout.splitlines() if len(l.split()) == 3 and l.split()[1] == "T")
        # 1. the method address table, by index
        txt = open(sfile, errors="replace").read()
        i = txt.index("\nmethod_addresses:\n")
        tabsym = re.match(r"\nmethod_addresses:\n(mono_aot_\S+method_addresses):", txt[i:]).group(1)
        table = []
        for line in txt[i:].splitlines()[1:]:
            line = line.strip()
            if not line or line.startswith((".globl", ".type", ".local", "mono_aot_")) or line == "method_addresses:": continue
            mt = re.match(r"^(blx?) (\S+)$", line)
            if not mt: break
            table.append(None if mt.group(2) == "method_addresses" else mt.group(2))
        # 1b. decode the linked call table the way the runtime does (aot-runtime.c get_call_table_entry): every entry must land
        #     exactly on its method (a linker veneer or a BL/BLX mix-up would hand the runtime a wrong method address)
        ta = addr(tabsym); calltab_bad = 0; calltab_n = 0
        if isinstance(ta, int):
            raw = elf.read(ta & ~1, 4 * len(table))
            for k, sym in enumerate(table):
                if not sym: continue
                ins = u32(raw, 4 * k); pc = (ta & ~1) + 4 * k
                if ins >> 28 == 0xf:   # blx imm: H bit, Thumb target
                    off = ((((ins & 0xffffff) << 1) | ((ins >> 24) & 1)) << 7)
                    off = (off & 0xffffffff); off = off - (1 << 32) if off & 0x80000000 else off
                    tgt = pc + (off >> 7) * 2 + 8 + 1
                elif ins >> 28 == 0xe and (ins >> 24) & 0xf == 0xb:   # bl
                    off = (ins & 0xffffff); off = off - (1 << 24) if off & 0x800000 else off
                    tgt = pc + off * 4 + 8
                else:
                    calltab_bad += 1; continue
                # Mono-backend methods are local labels, and the same generic instance can be compiled into several modules:
                # the entry must land on one of the symbols of that name
                cands = [x[0] for x in elf.syms.get(sym, [])]
                calltab_n += 1
                if tgt not in cands: calltab_bad += 1
        else:
            calltab_bad = -1
        # 2. the EH table
        ehaddr = addr(ehsym)
        if not isinstance(ehaddr, int): print(f"{m}: {ehsym} {ehaddr}"); bad += 1; continue
        size = elf.syms[ehsym][0][1]
        b = elf.read(ehaddr, size)
        ver = b[0]; p = 4  # version, func encoding, align 4
        fde_count = u32(b, p); p += 4
        tab = [(s32(b, p + 8 * k), s32(b, p + 8 * k + 4)) for k in range(fde_count + 1)]
        cie = p + (fde_count + 1) * 8
        ca, q = uleb(b, cie); da, q = sleb(b, q); rr, q = uleb(b, q)
        errs = []
        if ver != 3: errs.append(f"version {ver}")
        if (ca, da, rr) != (1, -4, 14): errs.append(f"CIE align/ra {ca}/{da}/{rr}")
        # 3./4. addresses in table order
        addrs = []; thumb_bad = 0; missing = 0
        for (mi, fo) in tab[:-1]:
            sym = table[mi] if 0 <= mi < len(table) else None
            a = addr(sym) if sym else None
            if not isinstance(a, int): missing += 1; addrs.append(None); continue
            if sym not in llvm_globals: errs.append(f"table method {sym} is not an LLVM symbol")
            addrs.append(a)
            if "thumb" in aotdir and not (a & 1): thumb_bad += 1
        good = [a for a in addrs if a is not None]
        mono_sorted = all(good[k] < good[k + 1] for k in range(len(good) - 1))
        if missing: errs.append(f"{missing} table methods unresolved")
        if not mono_sorted: errs.append("EH table order != address order")
        if thumb_bad: errs.append(f"{thumb_bad} table methods without the Thumb bit")
        lo = good[0] & ~1 if good else 0; hi = (good[-1] & ~1) + tab[-1][0] if good else 0
        # Mono-backend methods of this module inside [lo, hi)?
        inside = 0; nmono = 0
        for sym in table:
            if not sym or sym in llvm_globals: continue
            a = addr(sym)
            if isinstance(a, int):
                nmono += 1
                if lo <= (a & ~1) < hi: inside += 1
        if inside: errs.append(f"{inside} Mono-backend methods inside the LLVM code range")
        if calltab_bad: errs.append(f"call table: {calltab_bad} entries do not decode to their method" if calltab_bad > 0 else "call table symbol not found")
        # 5. FDEs
        nf = nc = nl = 0; cfa_err = None; opstats = {}
        for k in range(fde_count):
            fo, fe = tab[k][1], tab[k + 1][1]
            fde = b[fo:fe] if fe > fo else b[fo:]
            has_aug = fde[0]; q = 1; aug_len = 0
            if has_aug: aug_len = u32(fde, 1); q = 5; nl += 1
            n, e = cfa_ops(fde[q + aug_len:], opstats)
            nf += 1; nc += 1 if n else 0
            if e and not cfa_err: cfa_err = e
        if cfa_err: errs.append("CFA decode: " + cfa_err)
        if opstats.get("unsupported"): errs.append(f"{opstats['unsupported']} CFA ops Mono's unwinder does not decode")
        if opstats.get("badreg"): errs.append(f"{opstats['badreg']} CFA register numbers outside r0-r15/d8-d15")
        for k, v in opstats.items():
            if isinstance(k, int): tot_ops[MONO_OPS.get(k, f"0x{k:02x}")] = tot_ops.get(MONO_OPS.get(k, f"0x{k:02x}"), 0) + v
        tot["fdes"] += nf; tot["with_cfa"] += nc; tot["lsda"] += nl
        bad += 1 if errs else 0
        print(f"{m}: {'OK' if not errs else 'FAIL'} | {ehsym} {size} B, {fde_count} FDEs ({nc} with CFA ops, {nl} with an LSDA) | "
              f"methods: {len(table)} indexes, {len(good)} LLVM in the table, {nmono} Mono-backend | LLVM code range "
              f"0x{lo:08x}-0x{hi:08x} ({(hi - lo) / 1048576:.2f} MiB), table in address order: {mono_sorted}, call table {calltab_n} entries decode to their methods"
              + ("" if not errs else " | " + "; ".join(errs)))
    print(f"TOTAL: {tot['fdes']} FDEs, {tot['with_cfa']} with CFA programs, {tot['lsda']} with LSDA; modules failing: {bad}")
    print("CFA ops used: " + ", ".join(f"{k} {v}" for k, v in sorted(tot_ops.items(), key=lambda kv: -kv[1])))
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2]))
