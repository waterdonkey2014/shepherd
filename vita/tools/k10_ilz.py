#!/usr/bin/env python3
"""k10_ilz.py -- SHEPHERD-VITA device kit #10, track aot: COMPRESSED IL for game methods that have not run yet (vita/notes/kit10-aot.md).

The tool is generic; its INPUTS AND OUTPUTS are the owner's game DLLs and lists naming their methods: keep them owner-local (never in
git), like every owner-managed file. It reads a .NET assembly (PE32 + ECMA-335 metadata) and writes the "ILZ" form of it:

  * every byte of the original is kept, in the original order, EXCEPT the IL method bodies of the methods that are not "hot" (not in
    the hot list: the methods the kit's routes / the device have seen run) -- those bodies are cut out of .text;
  * the cut bodies are concatenated into a virtual stream (each fat body 4-aligned; no body crosses a block boundary), split into
    blocks of --block bytes and each block compressed with raw deflate (RFC 1951, zlib level 9); the blocks + their table go at the end
    of the file behind a "SHPILZ1" header, which a new, zero-raw-size section header ".ilz" points at (PointerToRawData = the header,
    VirtualAddress / VirtualSize = the virtual stream's RVA range, after every other section);
  * every RVA that pointed into .text after a cut is moved down by the bytes cut before it (the cuts keep every shift a multiple of 4,
    so the alignment of everything kept -- fat headers, EH sections, field data -- is unchanged mod 4); every cold method's RVA points
    into the .ilz range. Patched: the optional header (entry point, sizes, data directories), the section table, the CLI header,
    MethodDef.RVA, FieldRVA.RVA, the import directory, the debug directory, the base relocation of the entry stub. The metadata
    streams are otherwise byte-identical (the #GUID heap: the MVID -- the AOT images' check -- is the original's).

The PS Vita runtime (vita/patches/k10-ilz.patch) maps an RVA inside the .ilz range to the body it unpacks on first use (exactly the
original bytes) -- so the IL every method runs is the original IL; only where it waits in memory changes. A runtime WITHOUT the patch
finds no raw data at those RVAs and fails the method loudly ("Method has zero rva"): never silently different. The host reads ILZ files
only from ux0:data/shepherd/managed-ilz/ and only with a runtime that has the patch (SHEPHERD_K10_ILZ=0 in the env file: the original
managed/ DLLs, kit #9).

  k10_ilz.py census <dll> [hot.tsv]                 bodies / hot / cold sizes and the compression of the cold set at several block sizes
  k10_ilz.py pack <dll> <out.dll> [hot.tsv] [--block N] [--level L]   write the ILZ form (hot.tsv: "<image>\\t<token hex>" lines; the
                                                    image column is matched against the assembly name; no hot list = every body cold)
  k10_ilz.py verify <orig.dll> <ilz.dll>            every method's body (the original's vs the one the ILZ form gives), the metadata
                                                    streams except the RVA columns, field data, resources, the MVID -> "VERIFY OK" / FAIL
  k10_ilz.py stage <managed dir> <out dir> [hot.tsv] [--block N]   pack + verify every *.dll of a folder (skips files that are not
                                                    assemblies, assemblies with nothing to cut and the Shepherd.* DLLs -- the derived
                                                    module is rebuilt with every eboot); writes <out dir>/ILZ-MANIFEST.txt
  k10_ilz.py hot <out.tsv> <prof-calls.tsv ...>     the hot list: every (image, token) of the first-call traces (any kind: A or I)
  k10_ilz.py stagevpk <bcl dir> <out dir> [hot.tsv] [--block N]   (kit #12 walkb) stage for the VPK's OWN DLLs at build time (build.sh
                                                    SHEPHERD_K12_VPKILZ=1 -> the VPK's managed-ilz/): as stage, but the Shepherd.* DLLs too --
                                                    they and the eboot are one build's output, packed together (no stale pair is possible);
                                                    every file is verified as stage does, a FAIL ends with exit 1
"""
import sys, os, struct, zlib, hashlib, collections

MAGIC = b'SHPILZ1\0'
ILZ_VERSION = 2

class BadImage(Exception):
    pass

def u16(b, o): return struct.unpack_from('<H', b, o)[0]
def u32(b, o): return struct.unpack_from('<I', b, o)[0]

# ---------------------------------------------------------------- ECMA-335 tables (II.22): column kinds
# 'n' = n-byte constant; 's' string heap; 'g' guid heap; 'b' blob heap; ('t', table) simple index; ('c', coded) coded index
TBL = {
    0x00: [2, 's', 'g', 'g', 'g'],
    0x01: [('c', 'RS'), 's', 's'],
    0x02: [4, 's', 's', ('c', 'TDOR'), ('t', 0x04), ('t', 0x06)],
    0x03: [('t', 0x04)],
    0x04: [2, 's', 'b'],
    0x05: [('t', 0x06)],
    0x06: [4, 2, 2, 's', 'b', ('t', 0x08)],
    0x07: [('t', 0x08)],
    0x08: [2, 2, 's'],
    0x09: [('t', 0x02), ('c', 'TDOR')],
    0x0A: [('c', 'MRP'), 's', 'b'],
    0x0B: [2, ('c', 'HC'), 'b'],
    0x0C: [('c', 'HCA'), ('c', 'CAT'), 'b'],
    0x0D: [('c', 'HFM'), 'b'],
    0x0E: [2, ('c', 'HDS'), 'b'],
    0x0F: [2, 4, ('t', 0x02)],
    0x10: [4, ('t', 0x04)],
    0x11: ['b'],
    0x12: [('t', 0x02), ('t', 0x14)],
    0x13: [('t', 0x14)],
    0x14: [2, 's', ('c', 'TDOR')],
    0x15: [('t', 0x02), ('t', 0x17)],
    0x16: [('t', 0x17)],
    0x17: [2, 's', 'b'],
    0x18: [2, ('t', 0x06), ('c', 'HS')],
    0x19: [('t', 0x02), ('c', 'MDOR'), ('c', 'MDOR')],
    0x1A: ['s'],
    0x1B: ['b'],
    0x1C: [2, ('c', 'MF'), 's', ('t', 0x1A)],
    0x1D: [4, ('t', 0x04)],
    0x1E: [4, 4],
    0x1F: [4],
    0x20: [4, 2, 2, 2, 2, 4, 'b', 's', 's'],
    0x21: [4],
    0x22: [4, 4, 4],
    0x23: [2, 2, 2, 2, 4, 'b', 's', 's', 'b'],
    0x24: [4, ('t', 0x23)],
    0x25: [4, 4, 4, ('t', 0x23)],
    0x26: [4, 's', 'b'],
    0x27: [4, 4, 's', 's', ('c', 'IMPL')],
    0x28: [4, 4, 's', ('c', 'IMPL')],
    0x29: [('t', 0x02), ('t', 0x02)],
    0x2A: [2, 2, ('c', 'TOMD'), 's'],
    0x2B: [('c', 'MDOR'), 'b'],
    0x2C: [('t', 0x2A), ('c', 'TDOR')],
}
CODED = {
    'TDOR': (2, [0x02, 0x01, 0x1B]),
    'HC': (2, [0x04, 0x08, 0x17]),
    'HCA': (5, [0x06, 0x04, 0x01, 0x02, 0x08, 0x09, 0x0A, 0x00, 0x0E, 0x17, 0x14, 0x11, 0x1A, 0x1B, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2A, 0x2C, 0x2B]),
    'HFM': (1, [0x04, 0x08]),
    'HDS': (2, [0x02, 0x06, 0x20]),
    'MRP': (3, [0x02, 0x01, 0x1A, 0x06, 0x1B]),
    'HS': (1, [0x14, 0x17]),
    'MDOR': (1, [0x06, 0x0A]),
    'MF': (1, [0x04, 0x06]),
    'IMPL': (2, [0x26, 0x23, 0x27]),
    'CAT': (3, [None, None, 0x06, 0x0A, None]),
    'RS': (2, [0x00, 0x1A, 0x23, 0x01]),
    'TOMD': (1, [0x02, 0x06]),
}

class Section:
    def __init__(self, raw, off):
        self.hdr_off = off
        self.name = raw[off:off + 8]
        self.vsize, self.va, self.rawsize, self.rawptr = struct.unpack_from('<IIII', raw, off + 8)
    def __repr__(self):
        return '%s va=%x vs=%x raw=%x+%x' % (self.name.rstrip(b'\0').decode('latin1'), self.va, self.vsize, self.rawptr, self.rawsize)

class Image:
    """a read-only parse of the parts Mono reads (and the parts this tool must keep consistent)"""
    def __init__(self, data):
        self.raw = data
        b = data
        if b[:2] != b'MZ':
            raise BadImage('no MZ header')
        self.pe = u32(b, 0x3c)
        if b[self.pe:self.pe + 4] != b'PE\0\0':
            raise BadImage('no PE signature')
        self.coff = self.pe + 4
        self.nsect = u16(b, self.coff + 2)
        self.opt_size = u16(b, self.coff + 16)
        self.opt = self.coff + 20
        magic = u16(b, self.opt)
        if magic != 0x10b:
            raise BadImage('not PE32 (optional header magic %x)' % magic)
        self.entry_rva = u32(b, self.opt + 16)
        self.file_align = u32(b, self.opt + 36)
        self.sect_align = u32(b, self.opt + 32)
        self.ndirs = u32(b, self.opt + 92)
        self.dirs_off = self.opt + 96
        self.dirs = [struct.unpack_from('<II', b, self.dirs_off + 8 * i) for i in range(self.ndirs)]
        self.sect_off = self.opt + self.opt_size
        self.sections = [Section(b, self.sect_off + 40 * i) for i in range(self.nsect)]
        if self.ndirs < 15 or self.dirs[14][0] == 0:
            raise BadImage('no CLI header (not a managed assembly)')
        self.cli_rva = self.dirs[14][0]
        cli = self.off(self.cli_rva)
        self.cli = cli
        self.md_rva, self.md_size = struct.unpack_from('<II', b, cli + 8)
        self.cli_flags = u32(b, cli + 16)
        self.res_rva, self.res_size = struct.unpack_from('<II', b, cli + 24)
        self.sn_rva, self.sn_size = struct.unpack_from('<II', b, cli + 32)
        md = self.off(self.md_rva)
        self.md = md
        if u32(b, md) != 0x424A5342:
            raise BadImage('bad metadata signature')
        vlen = u32(b, md + 12)
        p = md + 16 + vlen
        nstreams = u16(b, p + 2)
        p += 4
        self.streams = {}
        for _ in range(nstreams):
            so, ss = struct.unpack_from('<II', b, p)
            p += 8
            e = b.index(b'\0', p)
            name = b[p:e].decode('latin1')
            p = (e + 4) & ~3
            self.streams[name] = (md + so, ss)
        tname = '#~' if '#~' in self.streams else '#-'
        if tname not in self.streams:
            raise BadImage('no table stream')
        self.uncompressed_tables = tname == '#-'
        t = self.streams[tname][0]
        heaps = b[t + 6]
        valid = struct.unpack_from('<Q', b, t + 8)[0]
        self.row_count = {}
        q = t + 24
        for i in range(64):
            if valid >> i & 1:
                self.row_count[i] = u32(b, q)
                q += 4
        if heaps & 0x40:
            q += 4   # (the extra data word of an uncompressed / EnC table stream)
        for i in self.row_count:
            if i not in TBL:
                raise BadImage('unknown metadata table 0x%02x' % i)
        sidx = 4 if heaps & 1 else 2
        gidx = 4 if heaps & 2 else 2
        bidx = 4 if heaps & 4 else 2
        def rows(tb): return self.row_count.get(tb, 0)
        def tsize(tb): return 2 if rows(tb) < 0x10000 else 4
        def csize(cn):
            bits, tabs = CODED[cn]
            m = max((rows(x) for x in tabs if x is not None), default=0)
            return 2 if m < (1 << (16 - bits)) else 4
        self.cols = {}
        self.row_size = {}
        for tb in sorted(TBL):
            cs = []
            for c in TBL[tb]:
                if isinstance(c, int): cs.append(c)
                elif c == 's': cs.append(sidx)
                elif c == 'g': cs.append(gidx)
                elif c == 'b': cs.append(bidx)
                elif c[0] == 't': cs.append(tsize(c[1]))
                else: cs.append(csize(c[1]))
            self.cols[tb] = cs
            self.row_size[tb] = sum(cs)
        self.table_off = {}
        for tb in sorted(self.row_count):
            self.table_off[tb] = q
            q += self.row_count[tb] * self.row_size[tb]
        self.tables_end = q
        self.text = self.section_of(self.md_rva)

    def section_of(self, rva):
        for s in self.sections:
            if s.va <= rva < s.va + max(s.vsize, s.rawsize):
                return s
        return None

    def off(self, rva):
        s = self.section_of(rva)
        if s is None or rva - s.va >= s.rawsize:
            raise BadImage('rva %x not in raw data' % rva)
        return s.rawptr + rva - s.va

    def col(self, tb, row, ci):
        """(file offset, size) of column ci of row (0-based)"""
        o = self.table_off[tb] + row * self.row_size[tb] + sum(self.cols[tb][:ci])
        return o, self.cols[tb][ci]

    def read_col(self, tb, row, ci):
        o, n = self.col(tb, row, ci)
        return u16(self.raw, o) if n == 2 else u32(self.raw, o)

    def methods(self):
        """[(row, rva, implflags, flags)] of every MethodDef"""
        out = []
        for r in range(self.row_count.get(0x06, 0)):
            out.append((r, self.read_col(0x06, r, 0), self.read_col(0x06, r, 1), self.read_col(0x06, r, 2)))
        return out

    def assembly_name(self):
        if not self.row_count.get(0x20):
            return None
        si = self.read_col(0x20, 0, 7)
        so = self.streams['#Strings'][0] + si
        return self.raw[so:self.raw.index(b'\0', so)].decode('utf-8')

    def mvid(self):
        gi = self.read_col(0x00, 0, 2)
        go = self.streams['#GUID'][0] + (gi - 1) * 16
        return self.raw[go:go + 16]

def body_extent(b, o, limit):
    """length of the IL method body (header + code + extra sections) at file offset o (II.25.4)"""
    f = b[o]
    if f & 3 == 2:   # tiny
        n = 1 + (f >> 2)
    elif f & 3 == 3:   # fat
        fl = u16(b, o)
        hs = (fl >> 12) * 4
        if hs < 12:
            raise BadImage('fat header size %d at %x' % (hs, o))
        code = u32(b, o + 4)
        n = hs + code
        if fl & 0x8:   # MoreSects
            while True:
                n = (n + 3) & ~3
                k = b[o + n]
                if k & 0x40:   # fat section
                    ds = b[o + n + 1] | b[o + n + 2] << 8 | b[o + n + 3] << 16
                else:
                    ds = b[o + n + 1]
                if ds < 4:
                    raise BadImage('extra section size %d at %x' % (ds, o + n))
                n += ds
                if not k & 0x80:
                    break
    else:
        raise BadImage('bad method header %02x at %x' % (f, o))
    if o + n > limit:
        raise BadImage('method body at %x runs past its section' % o)
    return n

def read_hot(path, asm):
    hot = set()
    if not path:
        return hot
    for l in open(path):
        p = l.rstrip('\n').split('\t')
        if len(p) >= 2 and not l.startswith('#') and p[0] == asm:
            try:
                hot.add(int(p[1], 16))
            except ValueError:
                pass
    return hot

def bodies(img):
    """{rva: (file offset, length)} of every IL body, and {row: rva} (rows sharing a body share the rva)"""
    out = {}
    rows = {}
    for r, rva, impl, fl in img.methods():
        if rva == 0 or impl & 3 != 0:   # no body / native / runtime (CodeTypeMask = 3; IL = 0)
            continue
        s = img.section_of(rva)
        if s is not img.text:
            raise BadImage('method row %d body (rva %x) outside .text' % (r + 1, rva))
        rows[r] = rva
        if rva in out:
            continue
        o = img.off(rva)
        out[rva] = (o, body_extent(img.raw, o, img.text.rawptr + img.text.rawsize))
    return out, rows

def plan(img, hot_tokens, block):
    """which bodies go cold; the cut runs in .text (rva ranges, each a multiple of 4 long); the cold virtual layout"""
    bs, rows = bodies(img)
    cold_rvas = set()
    hot_rvas = set()
    for r, rva in rows.items():
        (hot_rvas if (0x06000000 | (r + 1)) in hot_tokens else cold_rvas).add(rva)
    cold_rvas -= hot_rvas   # a body shared with a hot row stays hot
    # runs of adjacent cold bodies (nothing kept between them) are cut as one; each cut rounded DOWN to a multiple of 4 (the tail bytes
    # stay as filler, never referenced) so every shift is 0 mod 4
    spans = sorted((rva, rva + bs[rva][1]) for rva in cold_rvas)
    keep_spans = sorted((rva, rva + bs[rva][1]) for rva in hot_rvas)
    cuts = []
    i = 0
    allspans = sorted([(a, e, 'c') for a, e in spans] + [(a, e, 'h') for a, e in keep_spans])
    run = None
    for a, e, k in allspans:
        if k == 'h':
            if run: cuts.append(run); run = None
            continue
        if run and a <= run[1] + 3 and run[1] <= a:   # adjacent (or only alignment padding between)
            run = (run[0], e)
        else:
            if run: cuts.append(run)
            run = (a, e)
    if run: cuts.append(run)
    cuts = [(a, a + ((e - a) & ~3)) for a, e in cuts if (e - a) >= 4]
    # the virtual stream: bodies in rva order, fat bodies 4-aligned, none crossing a block boundary (a body larger than a block gets a
    # block of its own); block i covers [voff_i, voff_i + len_i)
    blocks = []   # [(voff, bytearray)]
    vmap = {}
    cur = bytearray(); voff = 0
    for rva in sorted(cold_rvas):
        o, n = bs[rva]
        body = img.raw[o:o + n]
        pos = len(cur)
        if body[0] & 3 == 3:
            pos = (pos + 3) & ~3   # fat headers 4-aligned (as in the original .text)
        if cur and pos + n > block:
            blocks.append((voff, cur)); voff = (voff + len(cur) + 3) & ~3; cur = bytearray(); pos = 0
        cur += b'\0' * (pos - len(cur))
        vmap[rva] = voff + pos
        cur += body
    if cur:
        blocks.append((voff, cur))
    return bs, rows, cold_rvas, hot_rvas, cuts, blocks, vmap

def shift_fn(cuts, text):
    """rva -> new rva for an rva in .text that is not inside a cut"""
    import bisect
    starts = [a for a, e in cuts]
    acc = [0]
    for a, e in cuts:
        acc.append(acc[-1] + (e - a))
    def f(rva):
        if not (text.va <= rva <= text.va + max(text.vsize, text.rawsize)):
            return rva
        i = bisect.bisect_right(starts, rva) - 1
        if i >= 0 and cuts[i][0] <= rva < cuts[i][1]:
            raise BadImage('rva %x points into a cut' % rva)
        return rva - acc[i + 1] if i >= 0 else rva
    return f, acc[-1]

def pack(data, hot_tokens, block=8192, level=9):
    img = Image(data)
    bs, rows, cold, hot, cuts, blocks, vmap = plan(img, hot_tokens, block)
    if not cold:
        return None, {'bodies': len(bs), 'cold': 0}
    text = img.text
    f, removed = shift_fn(cuts, text)
    b = bytearray(data)
    # room for the .ilz section header? if not, the header area grows by one file-alignment unit (every raw pointer moves by `grow`;
    # no RVA changes)
    nhdr = img.sect_off + 40 * img.nsect
    first_raw = min(s.rawptr for s in img.sections if s.rawsize)
    grow = 0 if nhdr + 40 <= first_raw and not any(data[nhdr:nhdr + 40]) else img.file_align
    if any(data[nhdr:min(nhdr + 40, first_raw)]):
        raise BadImage('the bytes behind the section table are not free')
    cert = img.dirs[4] if img.ndirs > 4 and img.dirs[4][0] else None
    if cert and (cert[0] + cert[1] != len(data) or cert[0] < max(s.rawptr + s.rawsize for s in img.sections)):
        raise BadImage('a certificate table that is not the file\'s tail: not packed')
    # an Authenticode certificate table (the BCL's: Microsoft's signature) is a FILE offset at the end of the file; Mono never reads it and
    # the signature no longer matches the changed file anyway: it is dropped (data directory 4 = 0), the bytes before it are kept
    # (kit #10 review: the header records the ORIGINAL -- its full size and CRC-32, certificate table included --: the host compares it with
    #  managed/<name>.dll as it is on the card; the first version recorded the truncated bytes, so every packed BCL DLL failed verify)
    orig_len, orig_crc = len(data), zlib.crc32(data) & 0xffffffff
    if cert:
        data = data[:cert[0]]
        del b[cert[0]:]
    def new_off_of_old(o_old):   # a file offset inside .text's raw data, before -> after the cuts (+ the header growth)
        rva = text.va + (o_old - text.rawptr)
        return text.rawptr + grow + (f(rva) - text.va)
    # 1. the RVA columns first (in the original buffer; offsets are the original's): every MethodDef RVA in .text moves with the cuts
    #    (a cold row's is set once the .ilz range is known, below); FieldRVA likewise
    for r in range(img.row_count.get(0x06, 0)):
        rva = img.read_col(0x06, r, 0)
        if rva == 0 or (r in rows and rows[r] in cold):
            continue
        o, n = img.col(0x06, r, 0)
        struct.pack_into('<I', b, o, f(rva))
    for r in range(img.row_count.get(0x1D, 0)):
        o, n = img.col(0x1D, r, 0)
        struct.pack_into('<I', b, o, f(u32(data, o)))
    # 2. the CLI header's RVAs (metadata, resources, strong name, the rest left as they are: 0 in IL-only assemblies)
    for k in (8, 24, 32, 40, 48, 56, 64):
        rva = u32(data, img.cli + k)
        if rva:
            struct.pack_into('<I', b, img.cli + k, f(rva))
    # 3. the import directory (ILT / name / IAT rvas and the hint-name rvas they hold), the debug directory, the entry stub's relocation
    imp_rva, imp_size = img.dirs[1] if img.ndirs > 1 else (0, 0)
    if imp_rva and img.section_of(imp_rva) is text:
        io = img.off(imp_rva)
        while True:
            ilt, ts, fc, name, iat = struct.unpack_from('<IIIII', data, io)
            if not (ilt or name or iat):
                break
            for k, v in ((0, ilt), (12, name), (16, iat)):
                if v: struct.pack_into('<I', b, io + k, f(v))
            for tab in (ilt, iat):
                if not tab or img.section_of(tab) is not text: continue
                to = img.off(tab)
                while True:
                    e = u32(data, to)
                    if not e: break
                    if not e & 0x80000000:
                        struct.pack_into('<I', b, to, f(e))
                    to += 4
            io += 20
    dbg_rva, dbg_size = img.dirs[6] if img.ndirs > 6 else (0, 0)
    if dbg_rva and img.section_of(dbg_rva) is text:
        do = img.off(dbg_rva)
        for k in range(dbg_size // 28):
            e = do + 28 * k
            ad, po = u32(data, e + 20), u32(data, e + 24)
            if ad:
                struct.pack_into('<I', b, e + 20, f(ad))
            if po and text.rawptr <= po < text.rawptr + text.rawsize:   # (the debug data's file offset: in .text, it moves with it)
                struct.pack_into('<I', b, e + 24, new_off_of_old(po))
            elif po:
                raise BadImage('debug data outside .text (file offset %x): not packed' % po)
    # 4. cut the runs out of .text (the file bytes behind .text's raw data move down by `removed`, rounded to the file alignment)
    to = text.rawptr
    pieces = []
    prev = text.va
    for a, e in cuts:
        pieces.append(b[to + prev - text.va: to + a - text.va])
        prev = e
    pieces.append(b[to + prev - text.va: to + text.rawsize])
    newtext = b''.join(pieces)
    fa = img.file_align
    new_rawsize = (len(newtext) + fa - 1) // fa * fa
    newtext += b'\0' * (new_rawsize - len(newtext))
    head = bytearray(b[:first_raw]) + b'\0' * grow + b[first_raw:text.rawptr]
    after_text_old = text.rawptr + text.rawsize
    tail = bytes(b[after_text_old:])
    delta_raw = text.rawsize - new_rawsize
    out = head + newtext + tail
    # the virtual range of the cold bodies: after every section (section alignment)
    sa = img.sect_align
    vend = max(s.va + max(s.vsize, s.rawsize) for s in img.sections)
    vbase = (vend + sa - 1) // sa * sa
    vsize = blocks[-1][0] + len(blocks[-1][1])
    # now the cold rows' RVAs (vbase known): rewrite them in `out` (the metadata moved with .text: recompute its offset)
    for r in range(img.row_count.get(0x06, 0)):
        if r in rows and rows[r] in cold:
            o, n = img.col(0x06, r, 0)
            struct.pack_into('<I', out, new_off_of_old(o), vbase + vmap[rows[r]])
    # 5. the section table / optional header
    struct.pack_into('<I', out, text.hdr_off + 8, text.vsize - removed)
    struct.pack_into('<I', out, text.hdr_off + 16, new_rawsize)
    for s in img.sections:
        if s.rawptr > text.rawptr:
            struct.pack_into('<I', out, s.hdr_off + 20, s.rawptr - delta_raw + grow)
        elif s.rawsize:
            struct.pack_into('<I', out, s.hdr_off + 20, s.rawptr + grow)
    struct.pack_into('<I', out, img.opt + 60, u32(data, img.opt + 60) + grow)   # SizeOfHeaders
    struct.pack_into('<I', out, img.opt + 16, f(img.entry_rva) if img.entry_rva else 0)
    struct.pack_into('<I', out, img.opt + 4, u32(data, img.opt + 4) - delta_raw)   # SizeOfCode
    for i in range(img.ndirs):
        rva, sz = img.dirs[i]
        if rva and img.section_of(rva) is text:
            struct.pack_into('<I', out, img.dirs_off + 8 * i, f(rva))
    # base relocations (.reloc: blocks of (page rva, size) + 16-bit entries): entries that point into .text are moved
    rel_rva, rel_size = img.dirs[5] if img.ndirs > 5 else (0, 0)
    if rel_rva:
        rs = img.section_of(rel_rva)
        ro = rs.rawptr - (delta_raw if rs.rawptr > text.rawptr else 0) + grow + rel_rva - rs.va
        end = ro + rel_size
        blocks_out = []
        p = ro
        targets = []
        while p + 8 <= end:
            page, sz = struct.unpack_from('<II', out, p)
            if sz < 8: break
            for k in range((sz - 8) // 2):
                e = u16(out, p + 8 + 2 * k)
                if e >> 12:
                    targets.append((e >> 12, page + (e & 0xfff)))
            p += sz
        if all(img.section_of(t) is text for _, t in targets) and targets:
            # re-encode (same count of entries; page blocks rebuilt)
            moved = sorted((ty, f(t)) for ty, t in targets)
            enc = bytearray()
            bypage = collections.OrderedDict()
            for ty, t in moved:
                bypage.setdefault(t & ~0xfff, []).append((ty << 12) | (t & 0xfff))
            for pg, es in bypage.items():
                if len(es) % 2: es.append(0)
                enc += struct.pack('<II', pg, 8 + 2 * len(es)) + b''.join(struct.pack('<H', x) for x in es)
            if len(enc) <= rel_size:
                out[ro:ro + len(enc)] = enc
                out[ro + len(enc):end] = b'\0' * (rel_size - len(enc))
                struct.pack_into('<I', out, img.dirs_off + 8 * 5 + 4, len(enc))
    # 6. the compressed blocks + the ILZ header at the end (4-aligned), and the .ilz section header
    while len(out) % 16:
        out.append(0)
    hdr_off = len(out)
    comp = []
    for voff, raw in blocks:
        co = zlib.compressobj(level, zlib.DEFLATED, -15, 9)
        comp.append(co.compress(bytes(raw)) + co.flush())
    nb = len(blocks)
    HDR = 64
    table_size = 20 * nb
    coff = HDR + table_size
    tab = bytearray()
    for (voff, raw), c in zip(blocks, comp):   # each block: its offset in the virtual stream, where its deflate data is, the sizes, the CRC-32 of the unpacked bytes
        tab += struct.pack('<IIIII', voff, coff, len(c), len(raw), zlib.crc32(bytes(raw)) & 0xffffffff)
        coff += len(c)
    header = MAGIC + struct.pack('<IIIIIIIIIIII', ILZ_VERSION, HDR, vbase, vsize, nb, block, orig_len, orig_crc, len(cold), sum(bs[r][1] for r in cold), HDR,
                                 zlib.crc32(bytes(tab)) & 0xffffffff)   # (the last word: the CRC-32 of the block table, checked when the runtime opens it)
    header += b'\0' * (HDR - len(header))
    out += header + tab + b''.join(comp)
    while len(out) % 4:
        out.append(0)
    if any(out[nhdr:nhdr + 40]):
        raise BadImage('no room for the .ilz section header')
    struct.pack_into('<8sIIIIIIHHI', out, nhdr, b'.ilz\0\0\0\0', vsize, vbase, 0, hdr_off, 0, 0, 0, 0, 0x40000040)
    struct.pack_into('<H', out, img.coff + 2, img.nsect + 1)
    if cert:
        struct.pack_into('<II', out, img.dirs_off + 8 * 4, 0, 0)   # (the certificate table dropped: see above)
    struct.pack_into('<I', out, img.opt + 56, (vbase + vsize + sa - 1) // sa * sa)   # SizeOfImage
    # (kit #10 review: the whole file's CRC-32 -- header word 56 read as zero -- and flag word 60 = 1: the host checks the ILZ file itself
    #  on first reuse, as it checks the original against word 36; the runtime ignores both words)
    struct.pack_into('<II', out, hdr_off + 56, 0, 1)
    struct.pack_into('<I', out, hdr_off + 56, zlib.crc32(bytes(out)) & 0xffffffff)
    stats = {'bodies': len(bs), 'cold': len(cold), 'hot': len(hot), 'cold_bytes': sum(bs[r][1] for r in cold), 'hot_bytes': sum(bs[r][1] for r in hot),
             'cut': removed, 'blocks': nb, 'comp_bytes': sum(len(c) for c in comp), 'in': orig_len, 'out': len(out), 'cert': cert[1] if cert else 0}
    return bytes(out), stats

def ilz_bodies(data):
    """the ILZ form's view: {row: body bytes} for every method with a body (cold ones unpacked with zlib)"""
    img = Image(data)
    ilz = None
    for i in range(img.nsect):
        pass
    nsect = u16(data, img.coff + 2)
    for i in range(nsect):
        o = img.sect_off + 40 * i
        if data[o:o + 8] == b'.ilz\0\0\0\0':
            vs, va, rs, rp = struct.unpack_from('<IIII', data, o + 8)
            ilz = (va, vs, rp)
    out = {}
    blocks = []
    if ilz:
        va, vs, hp = ilz
        if data[hp:hp + 8] != MAGIC:
            raise BadImage('bad ILZ magic')
        ver, hs, vbase, vsize, nb, bsz = struct.unpack_from('<IIIIII', data, hp + 8)
        if vbase != va or vsize != vs:
            raise BadImage('ILZ header / section disagree')
        if zlib.crc32(data[hp + hs:hp + hs + 20 * nb]) & 0xffffffff != u32(data, hp + 52):
            raise BadImage('ILZ block table CRC differs')
        for k in range(nb):
            voff, coff, cs, rsz, crc = struct.unpack_from('<IIIII', data, hp + hs + 20 * k)
            raw = zlib.decompress(data[hp + coff: hp + coff + cs], -15)
            if len(raw) != rsz or zlib.crc32(raw) & 0xffffffff != crc:
                raise BadImage('block %d unpacks to %d B (crc %08x), not %d (%08x)' % (k, len(raw), zlib.crc32(raw) & 0xffffffff, rsz, crc))
            blocks.append((voff, raw))
    for r, rva, impl, fl in img.methods():
        if rva == 0 or impl & 3 != 0:
            continue
        if ilz and ilz[0] <= rva < ilz[0] + ilz[1]:
            v = rva - ilz[0]
            for voff, raw in blocks:
                if voff <= v < voff + len(raw):
                    o = v - voff
                    n = body_extent(raw, o, len(raw))
                    out[r] = bytes(raw[o:o + n])
                    break
            else:
                raise BadImage('row %d: rva %x in no block' % (r + 1, rva))
        else:
            o = img.off(rva)
            out[r] = bytes(data[o:o + body_extent(data, o, img.text.rawptr + img.text.rawsize)])
    return img, out

def field_data(img, data):
    """{FieldRVA row: (rva-mapped bytes, 16 of them)} -- enough to see that each field still points at the same data"""
    out = {}
    for r in range(img.row_count.get(0x1D, 0)):
        rva = img.read_col(0x1D, r, 0)
        o = img.off(rva)
        out[r] = data[o:o + 64]
    return out

def verify(orig, ilz):
    io, bo = ilz_bodies(orig)
    ii, bi = ilz_bodies(ilz)
    bad = []
    if set(bo) != set(bi):
        bad.append('method rows with a body differ')
    for r in bo:
        if bo[r] != bi.get(r):
            bad.append('row %d body differs' % (r + 1)); break
    # metadata streams: identical except the RVA columns (MethodDef col 0, FieldRVA col 0)
    if set(io.streams) != set(ii.streams):
        bad.append('stream set differs')
    for name in io.streams:
        so, ss = io.streams[name]; si, ss2 = ii.streams[name]
        a = bytearray(orig[so:so + ss]); c = bytearray(ilz[si:si + ss2])
        if name in ('#~', '#-'):
            for img, buf, base in ((io, a, so), (ii, c, si)):
                for tb in (0x06, 0x1D):
                    for r in range(img.row_count.get(tb, 0)):
                        o, n = img.col(tb, r, 0)
                        buf[o - base:o - base + 4] = b'\0\0\0\0'
        if a != c:
            bad.append('stream %s differs' % name)
    if io.mvid() != ii.mvid():
        bad.append('MVID differs')
    # field data: the first 64 bytes each FieldRVA row points at
    fo, fi = field_data(io, orig), field_data(ii, ilz)
    for r in fo:
        if fo[r][:16] != fi[r][:16]:
            bad.append('FieldRVA row %d data differs' % (r + 1)); break
    # managed resources (the CLI header's resource directory) and the strong name blob
    for (ra, sz), (rb, sz2) in (((io.res_rva, io.res_size), (ii.res_rva, ii.res_size)), ((io.sn_rva, io.sn_size), (ii.sn_rva, ii.sn_size))):
        if sz != sz2 or (sz and orig[io.off(ra):io.off(ra) + sz] != ilz[ii.off(rb):ii.off(rb) + sz2]):
            bad.append('resources / strong name differ')
    if io.cli_flags != ii.cli_flags or io.assembly_name() != ii.assembly_name():
        bad.append('CLI flags / assembly name differ')
    # the header's record of the original (size word 32, CRC-32 word 36) and the file's own CRC-32 (word 56 read as zero; flag word 60 = 1):
    # what the host checks on first reuse
    for k in range(u16(ilz, ii.coff + 2)):
        o = ii.sect_off + 40 * k
        if ilz[o:o + 8] == b'.ilz\0\0\0\0':
            hp = u32(ilz, o + 20)
            if u32(ilz, hp + 32) != len(orig) or u32(ilz, hp + 36) != zlib.crc32(orig) & 0xffffffff:
                bad.append('the header does not record this original (size / CRC-32)')
            z = bytearray(ilz); z[hp + 56:hp + 60] = b'\0\0\0\0'
            if u32(ilz, hp + 60) != 1 or u32(ilz, hp + 56) != zlib.crc32(bytes(z)) & 0xffffffff:
                bad.append('no / a wrong file CRC-32 in the header (word 56, flag word 60)')
    return bad, len(bo)

def census(path, hotf=None):
    data = open(path, 'rb').read()
    img = Image(data)
    asm = img.assembly_name()
    hot = read_hot(hotf, asm)
    bs, rows = bodies(img)
    total = sum(n for o, n in bs.values())
    hot_rvas = {rows[r] for r in rows if (0x06000000 | (r + 1)) in hot}
    cold = [r for r in bs if r not in hot_rvas]
    cb = sum(bs[r][1] for r in cold)
    print('%s (%s): file %d, %d bodies %d B (%.1f %%), hot %d bodies %d B, cold %d bodies %d B' % (os.path.basename(path), asm, len(data), len(bs), total,
          100.0 * total / len(data), len(hot_rvas), sum(bs[r][1] for r in hot_rvas), len(cold), cb))
    stream = b''.join(data[bs[r][0]:bs[r][0] + bs[r][1]] for r in sorted(cold))
    for blk in (2048, 4096, 8192, 16384, 65536):
        c = 0
        for i in range(0, len(stream), blk):
            co = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
            c += len(co.compress(stream[i:i + blk]) + co.flush())
        print('  block %6d: cold %d -> %d B (%.2fx)' % (blk, len(stream), c, len(stream) / max(c, 1)))

def make_hot(out, traces):
    seen = set()
    for t in traces:
        for l in open(t, errors='replace'):
            p = l.rstrip('\n').split('\t')
            if len(p) < 6 or l.startswith('#') or p[0] == 'seq':
                continue
            img, tok = p[4], p[5]
            if tok.startswith('06'):
                seen.add((img, tok.lower()))
    with open(out, 'w') as f:
        f.write('# k10_ilz.py hot list: every method of the first-call traces (image <TAB> token); owner-local\n')
        for img, tok in sorted(seen):
            f.write('%s\t%s\n' % (img, tok))
    print('%d methods from %d traces -> %s' % (len(seen), len(traces), out))

def main(argv):
    if len(argv) < 2:
        print(__doc__); return 2
    cmd = argv[1]
    opts = {'--block': 8192, '--level': 9}
    args = []
    i = 2
    while i < len(argv):
        if argv[i] in opts:
            opts[argv[i]] = int(argv[i + 1]); i += 2
        else:
            args.append(argv[i]); i += 1
    if cmd == 'census':
        census(args[0], args[1] if len(args) > 1 else None)
    elif cmd == 'pack':
        data = open(args[0], 'rb').read()
        img = Image(data)
        hot = read_hot(args[2] if len(args) > 2 else None, img.assembly_name())
        out, st = pack(data, hot, opts['--block'], opts['--level'])
        if out is None:
            print('nothing to cut'); return 1
        open(args[1], 'wb').write(out)
        print(st)
    elif cmd == 'verify':
        bad, n = verify(open(args[0], 'rb').read(), open(args[1], 'rb').read())
        print(('VERIFY OK: %d method bodies identical, metadata streams identical except the RVA columns, MVID, field data, resources' % n) if not bad else 'VERIFY FAIL: ' + '; '.join(bad))
        return 0 if not bad else 1
    elif cmd in ('stage', 'stagevpk'):
        src, dst = args[0], args[1]
        hotf = args[2] if len(args) > 2 else None
        os.makedirs(dst, exist_ok=True)
        lines = []
        tin = tout = 0
        nfail = 0
        for fn in sorted(os.listdir(src)):
            if not fn.endswith('.dll'):
                continue
            if fn.startswith('Shepherd.') and cmd == 'stage':   # (the build's own DLLs -- Shepherd.Engine.Derived.dll is rebuilt with every eboot: never staged)
                lines.append('skip %s: a Shepherd DLL (built with each eboot)' % fn); continue
            data = open(os.path.join(src, fn), 'rb').read()
            try:
                img = Image(data)
                hot = read_hot(hotf, img.assembly_name())
                out, st = pack(data, hot, opts['--block'], opts['--level'])
            except BadImage as e:
                lines.append('skip %s: %s' % (fn, e)); continue
            if out is None or st['out'] >= st['in']:
                lines.append('skip %s: nothing to gain' % fn); continue
            bad, n = verify(data, out)
            if bad:
                nfail += 1
                lines.append('FAIL %s: %s' % (fn, '; '.join(bad))); continue
            open(os.path.join(dst, fn), 'wb').write(out)
            tin += st['in']; tout += st['out']
            lines.append('ilz %s: %d -> %d B (cold %d bodies %d B -> %d B in %d blocks; hot %d bodies %d B) sha256 %s orig %s' % (fn, st['in'], st['out'], st['cold'],
                         st['cold_bytes'], st['comp_bytes'], st['blocks'], st['hot'], st['hot_bytes'], hashlib.sha256(out).hexdigest()[:16], hashlib.sha256(data).hexdigest()[:16]))
        lines.append('TOTAL %d -> %d B (-%d B, %.1f %%), %d FAIL; block %d level %d; hot list %s' % (tin, tout, tin - tout, 100.0 * (tin - tout) / max(tin, 1), nfail,
                     opts['--block'], opts['--level'], hotf or 'none (every body cold)'))
        open(os.path.join(dst, 'ILZ-MANIFEST.txt'), 'w').write('\n'.join(lines) + '\n')
        print('\n'.join(lines[-3:]))
        return 1 if nfail else 0
    elif cmd == 'hot':
        make_hot(args[0], args[1:])
    else:
        print(__doc__); return 2
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv))
