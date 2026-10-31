#!/usr/bin/env python3
"""k10_learn.py -- SHEPHERD-VITA device kit #10, track aot: AOT THAT LEARNS FROM PLAY (vita/notes/kit10-aot.md).

The tool is generic; its INPUTS AND OUTPUTS name the owner's game methods (device logs, vlx outputs, the lists the build reads): keep
them owner-local, never in git (build.sh refuses the lists inside the repo).

The runtime (vita/patches/k10-aot-learn.patch) records, once per start, every method it looked for in the AOT images and did not find --
the interpreter ran it -- in <logs>/aot-learn.tsv (vita/shepherd/k10_aot.inc.c; read-logs.sh brings the device's back, every vlx run
leaves one; a start that finds the file at its 4 MiB cap moves it to aot-learn.prev.tsv first -- both names are read). Lines: flag TAB image TAB token TAB desc TAB name (flag m = a method of an AOT image the build left out, g / h = a generic
instance compiled code asked for that no image holds, its definition's image AOT / interpreted; desc = the exact method: definition
token + generic arguments, '?' = not nameable by a build).

  k10_learn.py merge <out.tsv> <file|dir> ...       every record of every aot-learn.tsv given (directories searched), one line per exact
                                                    method + the number of files (starts) that recorded it; the compiler's
                                                    SHEPHERD_AOT_LEARNED reads the 4th column (build.sh SHEPHERD_K10_AOTLEARNED=<out.tsv>)
  k10_learn.py filter <in.tsv> <out.tsv> [--min-starts N] [--flags mgh] [--images a,b] [--not-images a,b]
                                                    a subset (e.g. only what N starts saw: compile what play keeps reaching)
  k10_learn.py hot <out.tsv> <file|dir> ...         the methods that RAN: every (image, token) of the first-call traces (prof-calls.tsv,
                                                    any kind) and of the learning records -- SHEPHERD_K10_COLD's list (cold = everything
                                                    else) and k10_ilz.py's hot list (the bodies kept unpacked)
  k10_learn.py safe <in.tsv> <out.tsv> <dll dir> ...  drops every record whose method's IL (the generic definition's for an instance)
                                                    can load or store a float / struct through an unmanaged address -- ldind.r4 / r8,
                                                    stind.r4 / r8, ldobj / stobj / cpobj, the unaligned. prefix, a call to a *Unaligned
                                                    method: compiled ARM code reads those with VLDR / VSTR (alignment-faulting: the
                                                    interpreter's reads are not), so such a method stays interpreted (exact); the dll dirs
                                                    are searched for <image>.dll (the owner's managed folder, the build's bcl)
                                                    -- and (v2, kit #10 review) every record a callee of which the AOT compiler
                                                    may INLINE carries such an access (callees resolved through MethodDef /
                                                    MemberRef / MethodSpec, inline rules of method-to-ir.c, depth <= 10)
                                                    -- v3 (kit #10 review of v2): conservative -- a callee the scan cannot resolve
                                                    drops the record (type forwarders of the facades followed first); an ldfld /
                                                    stfld of a float / 64-bit / struct field in a method that handles unmanaged
                                                    pointers counts too (p->x, Unsafe.As<byte, T> (ref b [i]).x). build.sh refuses
                                                    any list but v3's, and SHEPHERD_K10_HOTINL with it (safe assumes the 30-B limit)
  k10_learn.py why <tsv> <dll dir> ...              each record safe drops and why (the instruction, or the inlinable callee)
  k10_learn.py report <tsv>                         counts by flag and image
  k10_learn.py moved <before.tsv> <after.tsv>       which records of a run before a learned build are gone after it (= now AOT)
"""
import sys, os, collections, bisect

def files(args, name):
    out = []
    for a in args:
        if os.path.isdir(a):
            for root, ds, fs in os.walk(a):
                for f in fs:
                    if f == name or (name == 'aot-learn.tsv' and f.startswith('aot-learn') and f.endswith('.tsv')):
                        out.append(os.path.join(root, f))
        elif os.path.exists(a):
            out.append(a)
    return sorted(set(out))

def records(path):
    for l in open(path, errors='replace'):
        if not l.strip() or l.startswith('#'):
            continue
        p = l.rstrip('\n').split('\t')
        if len(p) < 5 or p[0] not in ('m', 'g', 'h'):
            continue
        yield p

def merge(out, args):
    fs = files(args, 'aot-learn.tsv')
    seen = collections.OrderedDict()
    starts = collections.Counter()
    for f in fs:
        sess = set()
        for p in records(f):
            d = p[3]
            if d not in seen:
                seen[d] = p[:5]
            sess.add(d)
        for d in sess:
            starts[d] += 1
    with open(out, 'w') as o:
        o.write('# k10_learn.py merge: %d methods from %d aot-learn.tsv file(s) -- flag image token desc name starts (owner-local)\n' % (len(seen), len(fs)))
        for d, p in seen.items():
            o.write('\t'.join(p + [str(starts[d])]) + '\n')
    c = collections.Counter(p[0] for p in seen.values())
    unn = sum(1 for d in seen if '?' in d)
    print('%d methods (m %d, g %d, h %d; %d not nameable by a build) from %d file(s) -> %s' % (len(seen), c['m'], c['g'], c['h'], unn, len(fs), out))

def filt(inp, out, opts):
    mins = int(opts.get('--min-starts', 1)); flags = opts.get('--flags', 'mgh')
    imgs = set(x for x in opts.get('--images', '').split(',') if x); nimgs = set(x for x in opts.get('--not-images', '').split(',') if x)
    n = k = 0
    with open(out, 'w') as o:
        o.write('# k10_learn.py filter of %s: %s\n' % (os.path.basename(inp), ' '.join('%s=%s' % kv for kv in sorted(opts.items()))))
        for p in records(inp):
            n += 1
            st = int(p[5]) if len(p) > 5 and p[5].isdigit() else 1
            if st < mins or p[0] not in flags or (imgs and p[1] not in imgs) or p[1] in nimgs:
                continue
            o.write('\t'.join(p) + '\n'); k += 1
    print('%d of %d records -> %s' % (k, n, out))

def hot(out, args):
    seen = set()
    for f in files(args, 'prof-calls.tsv'):
        for l in open(f, errors='replace'):
            p = l.rstrip('\n').split('\t')
            if len(p) >= 6 and not l.startswith('#') and p[0] != 'seq' and p[5][:2] == '06':
                seen.add((p[4], p[5].lower()))
    for f in files(args, 'aot-learn.tsv'):
        for p in records(f):
            if p[2][:2] == '06':
                seen.add((p[1], p[2].lower()))
    with open(out, 'w') as o:
        o.write('# k10_learn.py hot: the methods that ran (image <TAB> token): first-call traces + learning records (owner-local)\n')
        for img, tok in sorted(seen):
            o.write('%s\t%s\n' % (img, tok))
    print('%d methods that ran -> %s' % (len(seen), out))

def report(path):
    c = collections.Counter(); im = collections.Counter()
    for p in records(path):
        c[p[0]] += 1; im[(p[0], p[1])] += 1
    print('total %d: %s' % (sum(c.values()), ', '.join('%s %d' % kv for kv in sorted(c.items()))))
    for (f, i), n in im.most_common(40):
        print('  %s %-40s %d' % (f, i, n))

def moved(a, b):
    before = {p[3]: p for p in records(a)}; after = {p[3]: p for p in records(b)}
    gone = [d for d in before if d not in after]; new = [d for d in after if d not in before]
    c = collections.Counter(before[d][0] for d in gone)
    print('before %d, after %d: %d gone (now compiled: %s), %d new' % (len(before), len(after), len(gone), ', '.join('%s %d' % kv for kv in sorted(c.items())), len(new)))
    for d in new[:20]:
        print('  new: %s %s' % (after[d][0], after[d][4]))

# ---- the IL scan (safe): ECMA-335 III opcode operand sizes ----
def _op_sizes():
    one = [0] * 256
    for op in (0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13, 0x1F, 0xDE) + tuple(range(0x2B, 0x38)): one[op] = 1
    for op in (0x20, 0x22, 0x27, 0x28, 0x29, 0x6F, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x79, 0x7B, 0x7C, 0x7D, 0x7E, 0x7F, 0x80, 0x81, 0x8C, 0x8D,
               0x8F, 0xA3, 0xA4, 0xA5, 0xC2, 0xC6, 0xD0, 0xDD) + tuple(range(0x38, 0x45)): one[op] = 4
    for op in (0x21, 0x23): one[op] = 8
    two = [0] * 256
    for op in (0x06, 0x07, 0x15, 0x16, 0x1C): two[op] = 4
    for op in (0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E): two[op] = 2
    for op in (0x12, 0x19): two[op] = 1
    return one, two
_ONE, _TWO = _op_sizes()
_RISKY_ONE = {0x4E: 'ldind.r4', 0x4F: 'ldind.r8', 0x56: 'stind.r4', 0x57: 'stind.r8', 0x70: 'cpobj', 0x71: 'ldobj', 0x81: 'stobj'}

def _il_risk(img, data, token, k):
    """the first risky instruction of MethodDef `token`'s IL, or None (no body: None)"""
    row = (token & 0xffffff) - 1
    if row < 0 or row >= img.row_count.get(0x06, 0): return None
    rva = img.read_col(0x06, row, 0); impl = img.read_col(0x06, row, 1)
    if not rva or impl & 3: return None
    o = img.off(rva); f = data[o]
    if f & 3 == 2: code, n = o + 1, f >> 2
    else: code, n = o + ((k.u16(data, o) >> 12) * 4), k.u32(data, o + 4)
    i, end = code, code + n
    while i < end:
        op = data[i]; i += 1
        if op == 0xFE:
            op2 = data[i]; i += 1
            if op2 == 0x12: return 'unaligned.'
            i += _TWO[op2]; continue
        if op in _RISKY_ONE: return _RISKY_ONE[op]
        if op == 0x45:
            cnt = k.u32(data, i); i += 4 + 4 * cnt; continue
        if op in (0x28, 0x6F):   # call / callvirt: a *Unaligned callee (Unsafe.ReadUnaligned / WriteUnaligned / CopyBlockUnaligned ...)
            tok = k.u32(data, i)
            if _callee_name(img, data, tok).endswith('Unaligned'): return 'call *Unaligned'
        i += _ONE[op]
    return None

def _callee_name(img, data, tok):
    t, r = tok >> 24, (tok & 0xffffff) - 1
    try:
        if t == 0x2B:   # MethodSpec -> its method (MethodDefOrRef)
            c = img.read_col(0x2B, r, 0); t, r = (0x06 if c & 1 == 0 else 0x0A), (c >> 1) - 1
        if t == 0x0A: si = img.read_col(0x0A, r, 1)
        elif t == 0x06: si = img.read_col(0x06, r, 3)
        else: return ''
        so = img.streams['#Strings'][0] + si
        return data[so:data.index(b'\0', so)].decode('utf-8', 'replace')
    except Exception:
        return ''

_PTR_OPS = {0xD3: 'conv.i', 0xE0: 'conv.u', 0xD4: 'conv.ovf.i', 0xD5: 'conv.ovf.u', 0x8A: 'conv.ovf.i.un', 0x8B: 'conv.ovf.u.un', 0x4D: 'ldind.i', 0xDF: 'stind.i'}
_PTR_TYPES = ('System.Runtime.CompilerServices.Unsafe', 'System.Runtime.InteropServices.MemoryMarshal', 'Internal.Runtime.CompilerServices.Unsafe')

class _Unresolved(Exception):
    pass

class _Scan:
    """the IL scan of `safe` over a set of DLL folders: a method's own IL and the IL of every callee the AOT compiler may INLINE into it
    (method-to-ir.c inline_method: no exception clauses, not NoInlining / Synchronized, IL < the inline limit -- 20 B Mono backend, 30 B LLVM
    AOT: 30 used for both -- or [AggressiveInlining] below MONO_AGGRESSIVE_INLINELIMIT 256, inline depth <= 10). An inlined callee's
    pointer float / struct access is compiled into the caller: a learned caller must not carry one (kit #10 review: the first safe scanned
    only the method's own IL). Callees: call / callvirt / newobj / ldftn tokens -- MethodDef, MemberRef (resolved by type name through the
    AssemblyRef, following the target's ExportedType forwarders -- the UnityEngine / netstandard / System.Core facades --, then
    System.Private.CoreLib; a TypeSpec parent: a generic instance's definition, an array's methods = the runtime's, no IL), MethodSpec.
    CONSERVATIVE (v3, kit #10 review of v2): a callee that resolves to several overloads is risky when any is; a callee that does NOT resolve
    (an unknown TypeSpec form, a ModuleRef / File parent, a type or method no folder holds, no overload of that name and shape) makes the
    record risky -- v2 skipped it. The own-IL rule (v3): besides ldind.r4 / r8, stind.r4 / r8, ldobj / stobj / cpobj, unaligned., *Unaligned
    calls -- an ldfld / stfld of a float, 64-bit or struct field in a method that handles unmanaged pointers (a pointer / pinned / function
    pointer in its signature or locals, localloc, conv.i / conv.u (+ ovf), ldind.i / stind.i, a call into Unsafe / MemoryMarshal): C#'s
    p->x and Unsafe.As<byte, T> (ref b [i]).x are ldfld / stfld on a possibly unaligned address (VLDR / VSTR, LDRD), the interpreter's reads
    are not. Not covered (the residual, documented): a reinterpreted ref / span that reaches a method from another one that is not inlined
    into it -- no IL-local scan can see where an address came from."""
    INLINE_LIMIT, AGGR_LIMIT, MAX_DEPTH = 30, 256, 10

    def __init__(self, dirs, k):
        self.dirs, self.k, self.cache, self.tdefs, self.memo, self.exported = dirs, k, {}, {}, {}, {}
        self.unresolved = collections.Counter(); self.owners = {}

    def image(self, name):
        if name not in self.cache:
            self.cache[name] = None
            for d in self.dirs:
                pth = os.path.join(d, name + '.dll')
                if os.path.exists(pth):
                    data = open(pth, 'rb').read()
                    try: self.cache[name] = (self.k.Image(data), data)
                    except Exception: pass
                    break
        return self.cache[name]

    def sget(self, img, data, si):
        so = img.streams['#Strings'][0] + si
        return data[so:data.index(b'\0', so)].decode('utf-8', 'replace')

    def blob(self, img, data, bi):
        o = img.streams['#Blob'][0] + bi; b0 = data[o]
        if b0 & 0x80 == 0: return data[o + 1:o + 1 + b0]
        if b0 & 0xC0 == 0x80: n = ((b0 & 0x3F) << 8) | data[o + 1]; return data[o + 2:o + 2 + n]
        n = ((b0 & 0x1F) << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3]; return data[o + 4:o + 4 + n]

    @staticmethod
    def cuint(b, i):
        b0 = b[i]
        if b0 & 0x80 == 0: return b0, i + 1
        if b0 & 0xC0 == 0x80: return ((b0 & 0x3F) << 8) | b[i + 1], i + 2
        return ((b0 & 0x1F) << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3], i + 4

    def sig_type(self, b, i, fl):
        """skip one type of a signature blob at b[i]; fl (a set) gets 'ptr' for a pointer / function pointer, 'pinned' for a pinned local"""
        while True:
            et = b[i]; i += 1
            if et in (0x1F, 0x20): _, i = self.cuint(b, i); continue        # CMOD_REQD / CMOD_OPT <token>
            if et == 0x45: fl.add('pinned'); continue                         # PINNED <type>
            if et == 0x41: continue                                           # SENTINEL
            break
        if et in (0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x16, 0x18, 0x19, 0x1C):
            return i
        if et == 0x0F: fl.add('ptr'); return self.sig_type(b, i, fl)       # PTR
        if et in (0x10, 0x1D): return self.sig_type(b, i, fl)              # BYREF, SZARRAY
        if et in (0x11, 0x12, 0x13, 0x1E): _, i = self.cuint(b, i); return i
        if et == 0x14:                                                       # ARRAY <type> rank nsizes sizes nlo lobounds
            i = self.sig_type(b, i, fl); _, i = self.cuint(b, i); n, i = self.cuint(b, i)
            for _ in range(n): _, i = self.cuint(b, i)
            n, i = self.cuint(b, i)
            for _ in range(n): _, i = self.cuint(b, i)
            return i
        if et == 0x15:                                                       # GENERICINST class|vt <token> argc args
            i += 1; _, i = self.cuint(b, i); n, i = self.cuint(b, i)
            for _ in range(n): i = self.sig_type(b, i, fl)
            return i
        if et == 0x1B:                                                       # FNPTR <method sig>
            fl.add('ptr'); return self.sig_method(b, i, fl)
        raise ValueError('signature element %x' % et)

    def sig_method(self, b, i, fl):
        cc = b[i]; i += 1
        if cc & 0x10: _, i = self.cuint(b, i)
        n, i = self.cuint(b, i)
        i = self.sig_type(b, i, fl)
        for _ in range(n): i = self.sig_type(b, i, fl)
        return i

    def typedefs(self, name):
        """{(namespace, name): row} of the top-level types, {(enclosing row, name): row} of the nested ones"""
        if name not in self.tdefs:
            im = self.image(name); t = {}
            if im:
                img, data = im
                nested = {}
                for r in range(img.row_count.get(0x29, 0)):
                    nested[img.read_col(0x29, r, 0) - 1] = img.read_col(0x29, r, 1) - 1
                for r in range(img.row_count.get(0x02, 0)):
                    n = self.sget(img, data, img.read_col(0x02, r, 1))
                    if r in nested: t[(nested[r], n)] = r
                    else: t[(self.sget(img, data, img.read_col(0x02, r, 2)), n)] = r
            self.tdefs[name] = t
        return self.tdefs[name]

    def forwarders(self, name):
        """{(namespace, name): assembly} of image `name`'s ExportedType rows that forward a top-level type to another assembly (a facade's)"""
        if name not in self.exported:
            im = self.image(name); t = {}
            if im:
                img, data = im
                for r in range(img.row_count.get(0x27, 0)):
                    impl = img.read_col(0x27, r, 4)
                    if impl & 3 == 1:   # AssemblyRef
                        t[(self.sget(img, data, img.read_col(0x27, r, 3)), self.sget(img, data, img.read_col(0x27, r, 2)))] = \
                            self.sget(img, data, img.read_col(0x23, (impl >> 2) - 1, 6))
            self.exported[name] = t
        return self.exported[name]

    def find_type(self, asm, ns, tn, depth=0):
        """(assembly, TypeDef row) of a top-level type named in assembly `asm`, following its type forwarders; None"""
        if depth > 8 or not self.image(asm): return None
        r = self.typedefs(asm).get((ns, tn))
        if r is not None: return (asm, r)
        fw = self.forwarders(asm).get((ns, tn))
        return self.find_type(fw, ns, tn, depth + 1) if fw else None

    def type_methods(self, img, trow):
        a = img.read_col(0x02, trow, 5) - 1
        b = img.read_col(0x02, trow + 1, 5) - 1 if trow + 1 < img.row_count.get(0x02, 0) else img.row_count.get(0x06, 0)
        return range(a, b)

    def type_name(self, img, data, trow):
        return self.sget(img, data, img.read_col(0x02, trow, 2)) + '.' + self.sget(img, data, img.read_col(0x02, trow, 1))

    def resolve_typeref(self, name, rrow, depth=0):
        """(image name, TypeDef row) of TypeRef row `rrow` of image `name`, or None"""
        im = self.image(name)
        if not im or depth > 8: return None
        img, data = im
        rs = img.read_col(0x01, rrow, 0); tn = self.sget(img, data, img.read_col(0x01, rrow, 1)); ns = self.sget(img, data, img.read_col(0x01, rrow, 2))
        tag, row = rs & 3, (rs >> 2) - 1
        if tag == 3:   # nested: the enclosing TypeRef
            enc = self.resolve_typeref(name, row, depth + 1)
            if not enc: return None
            r = self.typedefs(enc[0]).get((enc[1], tn))
            return (enc[0], r) if r is not None else None
        if tag == 2:   # AssemblyRef (then its forwarders; then CoreLib, where the BCL's forwarded types end)
            targets = [self.sget(img, data, img.read_col(0x23, row, 6)), 'System.Private.CoreLib']
        elif tag == 0:  # the same module
            targets = [name]
        else:          # a ModuleRef: another module of a multi-module assembly -- not followed
            return None
        for t in targets:
            r = self.find_type(t, ns, tn)
            if r is not None: return r
        return None

    def resolve_typespec(self, name, srow):
        """(image name, TypeDef row) of a TypeSpec parent; 'array' for an array type (its methods are the runtime's: no IL); None"""
        im = self.image(name)
        if not im: return None
        img, data = im
        b = self.blob(img, data, img.read_col(0x1B, srow, 0))
        if len(b) >= 2 and b[0] in (0x1D, 0x14): return 'array'
        if len(b) < 3 or b[0] != 0x15 or b[1] not in (0x11, 0x12): return None   # (only GENERICINST of a class / value type)
        coded, _ = self.cuint(b, 2)
        tag, row = coded & 3, (coded >> 2) - 1
        if tag == 0: return (name, row)
        if tag == 1: return self.resolve_typeref(name, row)
        return None

    def callee_rows(self, name, tok):
        """[(image name, MethodDef row)] a call token may name ([] = a method with no IL: an array's); raises _Unresolved"""
        im = self.image(name)
        if not im: raise _Unresolved('image %s' % name)
        img, data = im
        t, r = tok >> 24, (tok & 0xffffff) - 1
        try:
            if t == 0x2B:
                c = img.read_col(0x2B, r, 0); t, r = (0x06 if c & 1 == 0 else 0x0A), (c >> 1) - 1
            if t == 0x06: return [(name, r)]
            if t != 0x0A: raise _Unresolved('token %08x' % tok)
            parent = img.read_col(0x0A, r, 0); mname = self.sget(img, data, img.read_col(0x0A, r, 1))
            ptag, prow = parent & 7, (parent >> 3) - 1
            if ptag == 0: owner = (name, prow)
            elif ptag == 1: owner = self.resolve_typeref(name, prow)
            elif ptag == 4: owner = self.resolve_typespec(name, prow)
            elif ptag == 3: return [(name, prow)]
            else: raise _Unresolved('a ModuleRef parent (%s)' % mname)
            if owner == 'array': return []
            if not owner: raise _Unresolved('the type of %s (%s)' % (mname, 'TypeSpec' if ptag == 4 else 'TypeRef'))
            oi = self.image(owner[0])
            want = self.sig_shape(img, data, img.read_col(0x0A, r, 2))
            rows = [(owner[0], m) for m in self.type_methods(oi[0], owner[1])
                    if self.sget(oi[0], oi[1], oi[0].read_col(0x06, m, 3)) == mname and self.sig_shape(oi[0], oi[1], oi[0].read_col(0x06, m, 4)) == want]
            if not rows: raise _Unresolved('no %s.%s of that shape' % (self.type_name(oi[0], oi[1], owner[1]), mname))
            return rows
        except _Unresolved:
            raise
        except Exception as e:
            raise _Unresolved('unreadable (%s)' % e)

    def callee_type(self, name, tok):
        """'Namespace.Type' of a call token's declaring type ('' when unknown) -- for the pointer-context rule"""
        im = self.image(name)
        if not im: return ''
        img, data = im
        t, r = tok >> 24, (tok & 0xffffff) - 1
        try:
            if t == 0x2B:
                c = img.read_col(0x2B, r, 0); t, r = (0x06 if c & 1 == 0 else 0x0A), (c >> 1) - 1
            if t == 0x0A:
                parent = img.read_col(0x0A, r, 0); ptag, prow = parent & 7, (parent >> 3) - 1
                if ptag == 1:
                    return self.sget(img, data, img.read_col(0x01, prow, 2)) + '.' + self.sget(img, data, img.read_col(0x01, prow, 1))
                if ptag == 0: return self.type_name(img, data, prow)
                return ''
            if t == 0x06:
                tr = self.method_owner(name, r)
                return self.type_name(img, data, tr) if tr is not None else ''
        except Exception:
            pass
        return ''

    def method_owner(self, name, row):
        """the TypeDef row that declares MethodDef `row` of image `name`: the last type whose method list starts at or before it (a type
        with no methods shares its start with the next one)"""
        if name not in self.owners:
            img, data = self.image(name)
            self.owners[name] = [img.read_col(0x02, tr, 5) - 1 for tr in range(img.row_count.get(0x02, 0))]
        i = bisect.bisect_right(self.owners[name], row) - 1
        return i if i >= 0 else None

    def field_kind(self, img, data, tok):
        """'wide' for a float / double / 64-bit / struct field (a load of it from an unaligned address can fault), '' otherwise"""
        t, r = tok >> 24, (tok & 0xffffff) - 1
        try:
            if t == 0x04: b = self.blob(img, data, img.read_col(0x04, r, 2))
            elif t == 0x0A: b = self.blob(img, data, img.read_col(0x0A, r, 2))
            else: return 'wide'
            i = 1
            while b[i] in (0x1F, 0x20): _, i = self.cuint(b, i + 1)
            et = b[i]
            if et in (0x0A, 0x0B, 0x0C, 0x0D, 0x11): return 'wide'
            if et == 0x15 and b[i + 1] == 0x11: return 'wide'
            if et in (0x13, 0x1E): return 'wide'   # (a generic parameter: may be a float / struct)
            return ''
        except Exception:
            return 'wide'

    def sig_shape(self, img, data, bi):
        """(has this, generic parameter count, parameter count) of a method signature blob: the overloads a MemberRef may name"""
        try:
            b = self.blob(img, data, bi); cc = b[0]; i = 1; g = 0
            if cc & 0x10: g, i = self.cuint(b, i)
            n, i = self.cuint(b, i)
            return (cc & 0x20, g, n)
        except Exception:
            return None

    def body(self, img, data, row):
        """(code offset, code size, has clauses, local var sig token) or None"""
        if row < 0 or row >= img.row_count.get(0x06, 0): return None
        rva = img.read_col(0x06, row, 0); impl = img.read_col(0x06, row, 1)
        if not rva or impl & 3: return None
        o = img.off(rva); f = data[o]
        if f & 3 == 2: return o + 1, f >> 2, False, 0
        fl = self.k.u16(data, o)
        return o + ((fl >> 12) * 4), self.k.u32(data, o + 4), bool(fl & 0x8), self.k.u32(data, o + 8)

    def pointer_sig(self, img, data, row, ltok):
        """'ptr' / 'pinned' when the method's signature or locals hold a pointer, a function pointer or a pinned local; '' otherwise"""
        fl = set()
        try: self.sig_method(self.blob(img, data, img.read_col(0x06, row, 4)), 0, fl)
        except Exception: fl.add('ptr')   # (an unreadable signature: assume the worst)
        if ltok and ltok >> 24 == 0x11:
            try:
                b = self.blob(img, data, img.read_col(0x11, (ltok & 0xffffff) - 1, 0))
                if b[0] == 0x07:
                    n, i = self.cuint(b, 1)
                    for _ in range(n): i = self.sig_type(b, i, fl)
            except Exception: fl.add('ptr')
        return 'pinned' if 'pinned' in fl else ('ptr' if 'ptr' in fl else '')

    def scan(self, name, row):
        """(own risk or None, [call tokens]) of MethodDef `row`"""
        im = self.image(name); img, data = im; k = self.k
        bd = self.body(img, data, row)
        if not bd: return None, []
        code, n, _, ltok = bd
        i, end, toks, risk = code, code + n, [], None
        ptr = self.pointer_sig(img, data, row, ltok)   # (the pointer-context rule, v3)
        wide = None
        while i < end:
            op = data[i]; i += 1
            if op == 0xFE:
                op2 = data[i]; i += 1
                if op2 == 0x12 and not risk: risk = 'unaligned.'
                if op2 == 0x0F and not ptr: ptr = 'localloc'
                if op2 == 0x06: toks.append(k.u32(data, i))   # ldftn (a delegate's target: not inlined, but cheap to follow)
                i += _TWO[op2]; continue
            if op in _RISKY_ONE and not risk: risk = _RISKY_ONE[op]
            if op in _PTR_OPS and not ptr: ptr = _PTR_OPS[op]
            if op == 0x45:
                cnt = k.u32(data, i); i += 4 + 4 * cnt; continue
            if op in (0x7B, 0x7D) and not wide and self.field_kind(img, data, k.u32(data, i)):
                wide = 'ldfld' if op == 0x7B else 'stfld'
            if op in (0x28, 0x6F, 0x73):
                tok = k.u32(data, i); toks.append(tok)
                if not risk and op != 0x73 and _callee_name(img, data, tok).endswith('Unaligned'): risk = 'call *Unaligned'
                if not ptr and self.callee_type(name, tok) in _PTR_TYPES: ptr = 'a call into ' + self.callee_type(name, tok).split('.')[-1]
            i += _ONE[op]
        if not risk and ptr and wide:
            risk = '%s of a float / 64-bit / struct field in a method with %s' % (wide, ptr)
        return risk, toks

    def inlinable(self, name, row):
        im = self.image(name)
        if not im: return False
        img, data = im
        bd = self.body(img, data, row)
        if not bd: return False
        _, n, clauses, _ = bd
        impl = img.read_col(0x06, row, 1)
        if clauses or impl & 0x0008 or impl & 0x0020: return False   # (NoInlining, Synchronized)
        return n < self.INLINE_LIMIT or (impl & 0x0100 and n < self.AGGR_LIMIT)

    def risk(self, name, row, depth=0, path=()):
        """the first risky instruction in the method or in a callee the compiler may inline into it (with the inline path), or an unresolved
        callee (v3: conservative), or None"""
        key = (name, row)
        if key in self.memo: return self.memo[key]
        if depth > self.MAX_DEPTH: return None
        self.memo[key] = None   # (recursion guard)
        r, toks = self.scan(name, row)
        if not r:
            for tok in toks:
                try:
                    rows = self.callee_rows(name, tok)
                except _Unresolved as e:
                    self.unresolved[str(e).split(' (')[0] if str(e).startswith('the type') else str(e).split(' ')[0]] += 1
                    r = 'unresolved callee: %s' % e; break
                for cn, cr in rows:
                    if (cn, cr) == key or not self.inlinable(cn, cr): continue
                    sub = self.risk(cn, cr, depth + 1)
                    if sub:
                        ci = self.image(cn)
                        r = 'inlined %s.%s: %s' % (cn, self.sget(ci[0], ci[1], ci[0].read_col(0x06, cr, 3)), sub.split(': ', 1)[-1] if sub.startswith('inlined ') else sub); break
                if r: break
        self.memo[key] = r
        return r

def safe(inp, out, dirs):
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import k10_ilz as k
    sc = _Scan(dirs, k)
    kept = dropped = unknown = 0; why = collections.Counter(); deep = 0
    with open(out, 'w') as o:
        o.write('# k10_learn.py safe v3 of %s: records whose IL -- or the IL of a callee the AOT compiler may inline into them -- may load / store a float, 64-bit value or struct through an unmanaged address, or that call a method the scan cannot resolve, dropped (they stay interpreted)\n' % os.path.basename(inp))
        for p in records(inp):
            if not sc.image(p[1]):
                unknown += 1; why['image not found'] += 1; continue   # (not scanned: not compiled)
            try:
                r = sc.risk(p[1], (int(p[2], 16) & 0xffffff) - 1)
            except Exception:
                r = 'unreadable IL'
            if r:
                dropped += 1
                if r.startswith('inlined '):
                    deep += 1; why['via an inlinable callee' + (' (unresolved there)' if 'unresolved callee' in r else '')] += 1
                elif r.startswith('unresolved callee'): why['an unresolved callee'] += 1
                elif ' in a method with ' in r: why['ldfld / stfld of a wide field + pointers'] += 1
                else: why[r] += 1
                continue
            o.write('\t'.join(p) + '\n'); kept += 1
    print('%d kept, %d dropped (%s; %d of them through a callee the compiler may inline), %d not scanned (image not found) -> %s' % (kept, dropped, ', '.join('%s %d' % kv for kv in why.most_common()), deep, unknown, out))
    if sc.unresolved:
        print('  unresolved callees: %s' % ', '.join('%s %d' % kv for kv in sc.unresolved.most_common(12)))

def why(inp, dirs):
    """k10_learn.py why <tsv> <dll dir> ...: each record safe would drop, and why (the inline path)"""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import k10_ilz as k
    sc = _Scan(dirs, k)
    for p in records(inp):
        if not sc.image(p[1]): continue
        r = sc.risk(p[1], (int(p[2], 16) & 0xffffff) - 1)
        if r: print('%s\t%s\t%s\t%s' % (p[1], p[2], p[4], r))

def main(argv):
    if len(argv) < 3:
        print(__doc__); return 2
    cmd = argv[1]; rest = argv[2:]
    opts = {}; args = []
    i = 0
    while i < len(rest):
        if rest[i].startswith('--'):
            opts[rest[i]] = rest[i + 1]; i += 2
        else:
            args.append(rest[i]); i += 1
    if cmd == 'merge': merge(args[0], args[1:])
    elif cmd == 'filter': filt(args[0], args[1], opts)
    elif cmd == 'hot': hot(args[0], args[1:])
    elif cmd == 'safe': safe(args[0], args[1], args[2:])
    elif cmd == 'why': why(args[0], args[1:])
    elif cmd == 'report': report(args[0])
    elif cmd == 'moved': moved(args[0], args[1])
    else:
        print(__doc__); return 2
    return 0

if __name__ == '__main__':
    sys.exit(main(sys.argv))
