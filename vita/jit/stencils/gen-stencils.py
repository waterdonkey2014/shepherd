#!/usr/bin/env python3
"""SHEPHERD-VITA device kit #8 (track jit): the copy-and-patch stencil generator of the baseline JIT tier (vita/notes/kit8-jit.md).

Reads the relocatable Thumb-2 object that arm-vita-eabi-gcc made of shepjit-stencils.c (every stencil is one function
sj_st_<OPCODE>, built from the interpreter's own opcode body in shepjit-ops.h: the one definition both tiers use) and writes
shepjit-stencils.inc: each stencil's bytes plus its holes, which the runtime's trace compiler (shepjit.c) patches on the Vita:

  hole symbol      relocation(s)                     patched with
  _sj_a<N>         R_ARM_THM_MOVW_ABS_NC             ip [N]            (the instruction's 16-bit operand slot N)
  _sj_w<N>         R_ARM_THM_MOVW_ABS_NC / MOVT_ABS  READ32 (ip + N)   (low / high half)
  _sj_d<N>         R_ARM_THM_MOVW_ABS_NC / MOVT_ABS  data_items [ip [N]] (a pointer the interpreter would read)
  _sj_continue     R_ARM_THM_JUMP24 / JUMP19         the next instruction's code (stripped when it is the stencil's last word)
  _sj_taken        R_ARM_THM_JUMP24 / JUMP19         the branch target's code (or its exit stub)
  _sj_exit         R_ARM_THM_JUMP24 / JUMP19         this instruction's exit stub (back to the interpreter AT this instruction)
  any other name   R_ARM_THM_MOVW_ABS_NC / MOVT_ABS  that runtime function / variable (a helper: the interpreter's own C code)

Anything else (another relocation type, a reference to a data section, a non-zero addend) stops the generator: a stencil the
runtime could not patch exactly is never emitted. Usage: gen-stencils.py <stencils.o> <out.inc> [listing.txt]
"""
import struct, sys, hashlib, re

R_ARM_ABS32, R_ARM_THM_CALL, R_ARM_THM_JUMP24, R_ARM_THM_MOVW_ABS_NC, R_ARM_THM_MOVT_ABS, R_ARM_THM_JUMP19 = 2, 10, 30, 47, 48, 51
RNAME = {2: 'ABS32', 10: 'THM_CALL', 30: 'THM_JUMP24', 47: 'THM_MOVW_ABS_NC', 48: 'THM_MOVT_ABS', 51: 'THM_JUMP19'}

def die(msg):
    sys.stderr.write('gen-stencils.py: ' + msg + '\n'); sys.exit(1)

class Elf:
    def __init__(self, data):
        self.d = data
        if data[:4] != b'\x7fELF' or data[4] != 1 or data[5] != 1: die('not a little-endian ELF32 object')
        (e_type, e_machine, _, _, _, e_shoff, _, _, _, _, e_shentsize, e_shnum, e_shstrndx) = struct.unpack_from('<HHIIIIIHHHHHH', data, 16)
        if e_machine != 40 or e_type != 1: die('not an ARM relocatable object')
        self.sh = []
        for i in range(e_shnum):
            name, typ, flags, addr, off, size, link, info, align, entsize = struct.unpack_from('<IIIIIIIIII', data, e_shoff + i * e_shentsize)
            self.sh.append(dict(name=name, type=typ, flags=flags, off=off, size=size, link=link, info=info, align=align, entsize=entsize))
        shstr = self.sh[e_shstrndx]
        for s in self.sh: s['n'] = self.cstr(shstr['off'] + s['name'])
        symtab = [s for s in self.sh if s['type'] == 2]
        if len(symtab) != 1: die('expected one .symtab')
        st = symtab[0]; strtab = self.sh[st['link']]
        self.syms = []
        for i in range(st['size'] // 16):
            name, value, size, info, other, shndx = struct.unpack_from('<IIIBBH', data, st['off'] + i * 16)
            self.syms.append(dict(n=self.cstr(strtab['off'] + name), value=value, size=size, type=info & 15, bind=info >> 4, shndx=shndx))
    def cstr(self, o):
        e = self.d.index(b'\0', o); return self.d[o:e].decode()
    def bytes(self, s): return self.d[s['off']:s['off'] + s['size']]
    def rels(self, target_index):
        out = []
        for s in self.sh:
            if s['type'] == 9 and s['info'] == target_index:
                for i in range(s['size'] // 8):
                    off, info = struct.unpack_from('<II', self.d, s['off'] + i * 8)
                    out.append((off, info & 255, self.syms[info >> 8]))
            if s['type'] == 4 and s['info'] == target_index: die('RELA relocations: not expected from arm-vita-eabi-gcc')
        return sorted(out, key=lambda r: r[0])

def hw(code, off): return struct.unpack_from('<H', code, off)[0]

def movw_imm16(code, off):
    h1, h2 = hw(code, off), hw(code, off + 2)
    return ((h1 & 0xf) << 12) | (((h1 >> 10) & 1) << 11) | (((h2 >> 12) & 7) << 8) | (h2 & 0xff)

def is_movw(code, off): return (hw(code, off) & 0xfbf0) == 0xf240 and (hw(code, off + 2) & 0x8000) == 0
def is_movt(code, off): return (hw(code, off) & 0xfbf0) == 0xf2c0 and (hw(code, off + 2) & 0x8000) == 0
def is_b24(code, off): return (hw(code, off) & 0xf800) == 0xf000 and (hw(code, off + 2) & 0xd000) == 0x9000
def is_b19(code, off): return (hw(code, off) & 0xf800) == 0xf000 and (hw(code, off + 2) & 0xd000) == 0x8000

HOLE_A, HOLE_WLO, HOLE_WHI, HOLE_DLO, HOLE_DHI, HOLE_CONT, HOLE_TAKEN, HOLE_EXIT, HOLE_HLO, HOLE_HHI = range(10)
KNAME = ['SJH_A16', 'SJH_W32_LO', 'SJH_W32_HI', 'SJH_DATA_LO', 'SJH_DATA_HI', 'SJH_CONT', 'SJH_TAKEN', 'SJH_EXIT', 'SJH_HELPER_LO', 'SJH_HELPER_HI']

def main():
    if len(sys.argv) < 3: die('usage: gen-stencils.py <stencils.o> <out.inc> [listing.txt]')
    raw = open(sys.argv[1], 'rb').read()
    e = Elf(raw)
    helpers = []      # names, in first-use order
    stencils = []
    for idx, s in enumerate(e.sh):
        if not s['n'].startswith('.text.sj_st_'):
            if s['type'] == 1 and (s['flags'] & 2) and s['size'] and s['n'] not in ('.text',) and not s['n'].startswith('.ARM'):
                die(f'unexpected allocated section {s["n"]} ({s["size"]} bytes): a stencil may use no data / rodata / bss')
            continue
        op = s['n'][len('.text.sj_st_'):]
        fn = [y for y in e.syms if y['shndx'] == idx and y['type'] == 2]
        if len(fn) != 1 or fn[0]['n'] != 'sj_st_' + op or fn[0]['value'] & ~1: die(f'{s["n"]}: expected one function sj_st_{op} at its start')
        code = bytearray(e.bytes(s)); size = fn[0]['size']
        if size > len(code) or size & 1: die(f'{op}: odd size {size}')
        data_maps = [y['value'] for y in e.syms if y['shndx'] == idx and y['n'].startswith('$d')]
        holes = []
        for off, typ, sym in e.rels(idx):
            name = sym['n']
            if sym['shndx'] != 0: die(f'{op}: relocation against a defined symbol {name or "(section)"}: not a hole')
            if off >= size: die(f'{op}: relocation beyond the function ({off} >= {size})')
            m = re.fullmatch(r'_sj_([awd])(\d+)', name)
            if typ in (R_ARM_THM_MOVW_ABS_NC, R_ARM_THM_MOVT_ABS):
                movw = typ == R_ARM_THM_MOVW_ABS_NC
                if not (is_movw(code, off) if movw else is_movt(code, off)): die(f'{op}+{off}: {RNAME[typ]} on a non-MOVW/MOVT instruction')
                if movw_imm16(code, off) != 0: die(f'{op}+{off}: non-zero addend in a {RNAME[typ]} hole')
                if m and m.group(1) == 'a':
                    if not movw: die(f'{op}+{off}: MOVT on a 16-bit operand hole {name}')
                    holes.append((off, HOLE_A, int(m.group(2))))
                elif m and m.group(1) == 'w': holes.append((off, HOLE_WLO if movw else HOLE_WHI, int(m.group(2))))
                elif m and m.group(1) == 'd': holes.append((off, HOLE_DLO if movw else HOLE_DHI, int(m.group(2))))
                elif name in ('_sj_continue', '_sj_taken', '_sj_exit'): die(f'{op}+{off}: {name} taken by address')
                else:
                    if name.startswith('_sj_'): die(f'{op}+{off}: unknown hole {name}')
                    if name not in helpers: helpers.append(name)
                    holes.append((off, HOLE_HLO if movw else HOLE_HHI, helpers.index(name)))
            elif typ in (R_ARM_THM_JUMP24, R_ARM_THM_JUMP19):
                if not (is_b24(code, off) if typ == R_ARM_THM_JUMP24 else is_b19(code, off)): die(f'{op}+{off}: {RNAME[typ]} on a non-B.W instruction (a BL?)')
                kind = {'_sj_continue': HOLE_CONT, '_sj_taken': HOLE_TAKEN, '_sj_exit': HOLE_EXIT}.get(name)
                if kind is None: die(f'{op}+{off}: a branch to {name} (helpers must be long calls: MOVW/MOVT + BLX)')
                holes.append((off, kind, 0 if typ == R_ARM_THM_JUMP24 else 1))
            else:
                die(f'{op}+{off}: relocation {RNAME.get(typ, typ)} against {name}: not patchable')
        # strip the trailing B.W to the next stencil (it falls through instead)
        falls = 0
        last = [h for h in holes if h[0] == size - 4]
        if last and last[0][1] == HOLE_CONT and last[0][2] == 0 and not any(d >= size - 4 for d in data_maps):
            holes = [h for h in holes if h[0] != size - 4]; size -= 4; falls = 1
        stencils.append(dict(op=op, code=bytes(code[:size]), holes=holes, falls=falls, align4=1 if data_maps else 0))
    if not stencils: die('no stencils')
    sha = hashlib.sha256(raw).hexdigest()
    o = []
    o.append('/* GENERATED by vita/jit/stencils/gen-stencils.py from shepjit-stencils.o (sha256 %s): DO NOT EDIT.' % sha[:16])
    o.append(' * %d stencils, %d helper references; each: Thumb-2 bytes + holes (offset, kind, argument). vita/notes/kit8-jit.md */' % (len(stencils), len(helpers)))
    for h in helpers:
        o.append('extern char %s;' % h) if False else None
    for st in stencils:
        o.append('static const guint8 sj_code_%s [%d] = { %s };' % (st['op'], len(st['code']), ', '.join('0x%02x' % b for b in st['code'])))
        if st['holes']:
            o.append('static const SjHole sj_holes_%s [%d] = { %s };' % (st['op'], len(st['holes']), ', '.join('{ %d, %s, %d }' % (h[0], KNAME[h[1]], h[2]) for h in st['holes'])))
    o.append('#define SJ_TABLE_ID "%s"   /* names this table (the learning cache of another table is ignored) */' % sha[:16])
    o.append('#define SJ_STENCIL_COUNT %d' % len(stencils))
    o.append('#define SJ_HELPER_COUNT %d' % len(helpers))
    o.append('static const SjStencil sj_stencils [SJ_STENCIL_COUNT] = {')
    for st in stencils:
        o.append('\t{ %s, sj_code_%s, %s, %d, %d, %d, %d },' % (st['op'], st['op'], ('sj_holes_' + st['op']) if st['holes'] else 'NULL', len(st['code']), len(st['holes']), st['falls'], st['align4']))
    o.append('};')
    o.append('#define SJ_HELPERS(X) \\')
    for h in helpers: o.append('\tX (%s) \\' % h)
    o.append('\t/* end */')
    open(sys.argv[2], 'w').write('\n'.join(x for x in o if x is not None) + '\n')
    total = sum(len(s['code']) for s in stencils)
    print(f'gen-stencils.py: {len(stencils)} stencils, {total} bytes ({total / len(stencils):.1f} average), {sum(len(s["holes"]) for s in stencils)} holes, {sum(s["falls"] for s in stencils)} fall through, {sum(s["align4"] for s in stencils)} with a literal pool, {len(helpers)} helpers: {" ".join(helpers)}')
    if len(sys.argv) > 3:
        with open(sys.argv[3], 'w') as f:
            for st in stencils:
                f.write(f'{st["op"]}: {len(st["code"])} B, falls {st["falls"]}, align4 {st["align4"]}, holes ' + ' '.join(f'{h[0]}:{KNAME[h[1]]}:{h[2]}' for h in st['holes']) + '\n')

main()
