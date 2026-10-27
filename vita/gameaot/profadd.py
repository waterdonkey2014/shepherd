#!/usr/bin/env python3
"""profadd.py (kit #6 track fps). The tool is generic; its INPUTS AND OUTPUTS name the owner's game methods: keep them owner-local
(never in git), like the profiles themselves (build.sh refuses a profile inside the repo).
Adds the hot interpreted leaves that AOT code calls across (the runtime's SHEPHERD_INTERP_CICACHE=count lists, interp-entries.tsv)
to the game-AOT profile, taking each method's record from a profile that already holds it (the game-aot study's Mac profiles) or
building a generic-method-instance record.
  profadd.py render <profile>                      every METHOD as "image|Class<inst>:Name (params)|signature" (the interp naming)
  profadd.py add <base> <out> <wanted.txt> <src...> base + the wanted methods (interp names, one per line, '#' comments),
                                                   each found in a src profile; prints what was found / missing
  profadd.py build <in> <out> <wanted.txt> <sigs.txt> + the wanted methods built from the owner DLLs' signatures (tools/sigdump output:
                                                   "image|Class|Name|N|ret(params)|generic params"), class / method instances included
Format: vita/gameaot/mechanics/aotprof/aotprofile.py (ids renumbered on write, generic instances kept as profsel.py does)."""
import sys, os, collections
_here = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(_here, 'mechanics', 'aotprof'))
sys.path.insert(0, os.path.join(_here, 'proto'))
import aotprofile as ap
import profsel as ps
IMAGE, TYPE, GINST, METHOD = ap.IMAGE, ap.TYPE, ap.GINST, ap.METHOD

def tname(idx, tid):
    t = idx[tid]
    n = t[4]
    if t[3] != -1:
        g = idx[t[3]]
        n += '<' + ','.join(tname(idx, a) for a in g[2]) + '>'
    return n

def render(recs):
    idx = ps.index(recs)
    for r in recs:
        if r[0] != METHOD: continue
        t = idx[r[2]]; img = idx[t[2]][2]
        cls = tname(idx, r[2])
        sig = r[6]; params = sig[sig.index('('):]
        mname = r[5]
        if r[3] != -1: mname += '<' + ','.join(tname(idx, a) for a in idx[r[3]][2]) + '>'
        yield r, idx, img, '%s:%s %s' % (cls, mname, params.replace(',', ',')), sig

def norm(name):
    # interp names: "Ns.Class:Name (p1,p2)"; generic args "<A, B>" with a space after the comma in class instances
    return name.replace(', ', ',')


PRIM = {'void': 'System.Void', 'bool': 'System.Boolean', 'char': 'System.Char', 'sbyte': 'System.SByte', 'byte': 'System.Byte', 'int16': 'System.Int16',
        'uint16': 'System.UInt16', 'int': 'System.Int32', 'uint': 'System.UInt32', 'long': 'System.Int64', 'ulong': 'System.UInt64', 'single': 'System.Single',
        'double': 'System.Double', 'string': 'System.String', 'object': 'System.Object', 'intptr': 'System.IntPtr', 'uintptr': 'System.UIntPtr'}
def split_args(t):
    """'A, B<C, D>, E' -> ['A', 'B<C, D>', 'E'] (also without the blanks)"""
    out = []; depth = 0; cur = ''
    for ch in t:
        if ch == '<': depth += 1
        if ch == '>': depth -= 1
        if ch == ',' and depth == 0: out.append(cur.strip()); cur = ''; continue
        cur += ch
    if cur.strip(): out.append(cur.strip())
    return out
def parse_gen(t):
    """'Ns.C`1<A, B>' -> ('Ns.C`1', ['A', 'B']); 'Ns.C' -> ('Ns.C', [])"""
    i = t.find('<')
    if i < 0 or not t.endswith('>'): return t, []
    return t[:i], split_args(t[i + 1:-1])
def type_rec_name(cls):
    head = cls.split('/')[0]
    if '.' in head: return cls
    return '.' + cls
def inflate(sig, cargs, margs):
    import re
    def sub(m):
        return (margs if m.group(1) == '!!' else cargs)[int(m.group(2))]
    return re.sub(r'(!!|!)(\d+)', sub, sig)
def build(base, out, wanted, sigsf):
    sigs = collections.defaultdict(list); classes = {}
    for l in open(sigsf):
        p = l.rstrip('\n').split('|')
        if len(p) != 6: continue
        img, cls, name, n, sig, gp = p
        sigs[(cls, name, int(n))].append((img, sig, int(gp))); classes[cls] = img
    recs = ap.read(base); idx = ps.index(recs); o = ps.Out(); memo = {}
    for r in recs:
        if r[0] == METHOD: o.method(idx, r, memo)
    n0 = len(o.meth)
    have = set(norm(n) for _, _, _, n, _ in render(recs))
    extra = {}; nid = [-10]
    def new(rec):
        i = nid[0]; nid[0] -= 1; extra[i] = (rec[0], i) + rec[1:]; return i
    def img_id(name):
        for k, v in list(idx.items()) + list(extra.items()):
            if v[0] == IMAGE and v[2] == name: return k
        return new((IMAGE, name, ''))
    def type_id(t):
        """a TYPE record for a type as Mono names it in signatures: primitive, owner class, CoreLib class, generic instance"""
        base_, args = parse_gen(t)
        if base_ in PRIM: image, nm = 'System.Private.CoreLib', PRIM[base_]
        elif base_ in classes: image, nm = classes[base_], type_rec_name(base_)
        elif base_.startswith('System.'): image, nm = 'System.Private.CoreLib', base_
        else: raise KeyError('no image for type ' + t)
        g = new((GINST, [type_id(x) for x in args])) if args else -1
        ii = img_id(image)
        if g == -1:
            for k, v in list(idx.items()) + list(extra.items()):
                if v[0] == TYPE and v[2] == ii and v[3] == -1 and v[4] == nm: return k
        return new((TYPE, ii, g, nm))
    import re
    for l in open(wanted):
        if not l.strip() or l.startswith('#'): continue
        w = norm(l.rstrip('\n').split('\t')[-1].strip())
        if w in have: continue
        m = re.match(r'^(.*?):([^ :]+?)(<.*>)? \((.*)\)$', w)
        if not m: print('UNPARSED %s' % w); continue
        clsfull, name, mg, params = m.group(1), m.group(2), m.group(3), m.group(4)
        cls, cargs = parse_gen(clsfull); margs = split_args(mg[1:-1]) if mg else []
        np = len(split_args(params)) if params else 0
        cands = sigs.get((cls, name, np), [])
        hit = None
        for img, sig, gp in cands:
            if gp != len(margs): continue
            inf = inflate(sig, cargs, margs)
            if norm(inf[inf.index('('):]) == '(' + params + ')': hit = (img, inf); break
        if not hit: print('NOSIG   %s (%d candidates)' % (w, len(cands))); continue
        img, inf = hit
        try:
            c = type_id(cls + ('<' + ', '.join(cargs) + '>' if cargs else ''))
            g = new((GINST, [type_id(x) for x in margs])) if margs else -1
        except KeyError as e:
            print('NOTYPE  %s: %s' % (w, e)); continue
        allidx = dict(idx); allidx.update(extra)
        o.method(allidx, (METHOD, None, c, g, np, name, inf), {})
        print('built   %s  [%s] %s' % (w, img, inf))
    ap.write(out, o.recs)
    print('%s: %d methods (%d + %d built)' % (out, len(o.meth), n0, len(o.meth) - n0))

def main(a):
    if a[0] == 'render':
        for r, idx, img, n, sig in render(ap.read(a[1])): print('%s|%s|%s' % (img, norm(n), sig))
    elif a[0] == 'add':
        base, out, wanted, srcs = a[1], a[2], a[3], a[4:]
        want = [norm(l.rstrip('\n').split('\t')[-1].strip()) for l in open(wanted) if l.strip() and not l.startswith('#')]
        found = {}
        for f in srcs:
            for r, idx, img, n, sig in render(ap.read(f)):
                if norm(n) in want and norm(n) not in found: found[norm(n)] = (r, idx, img, f)
        o = ps.Out()
        for f in [base]:
            recs = ap.read(f); idx = ps.index(recs); memo = {}
            for r in recs:
                if r[0] == METHOD: o.method(idx, r, memo)
        n0 = len(o.meth)
        for w in want:
            if w in found:
                r, idx, img, f = found[w]; o.method(idx, r, {})
                print('found  %s  (%s, %s)' % (w, img, os.path.basename(f)))
            else: print('MISSING %s' % w)
        ap.write(out, o.recs)
        print('%s: %d methods (%d from the base, %d added)' % (out, len(o.meth), n0, len(o.meth) - n0))
    elif a[0] == 'build':
        build(a[1], a[2], a[3], a[4])
    elif a[0] == 'addgm':
        recs = ap.read(a[1]); idx = ps.index(recs); o = ps.Out(); memo = {}
        for r in recs:
            if r[0] == METHOD: o.method(idx, r, memo)
        n0 = len(o.meth)
        # new records go through a private id space (negative ids) so Out.emit can canonicalise them
        extra = {}; nid = [-10]
        def new(rec):
            i = nid[0]; nid[0] -= 1; extra[i] = (rec[0], i) + rec[1:]; return i
        def img_id(name):
            for k, v in list(idx.items()) + list(extra.items()):
                if v[0] == IMAGE and v[2] == name: return k
            return new((IMAGE, name, ''))
        def type_id(name, image):
            ii = img_id(image)
            for k, v in list(idx.items()) + list(extra.items()):
                if v[0] == TYPE and v[2] == ii and v[3] == -1 and v[4] == name: return k
            return new((TYPE, ii, -1, name))
        for l in open(a[3]):
            if not l.strip() or l.startswith('#'): continue
            image, cls, name, npar, sig, targs = l.rstrip('\n').split('|')
            c = type_id(cls, image)
            g = new((GINST, [type_id(*x.split('@')) for x in targs.split(',')]))
            allidx = dict(idx); allidx.update(extra)
            o.method(allidx, (METHOD, None, c, g, int(npar), name, sig), {})
            print('added generic method instance %s.%s<%s> %s' % (cls, name, targs, sig))
        ap.write(a[2], o.recs)
        print('%s: %d methods (%d + %d)' % (a[2], len(o.meth), n0, len(o.meth) - n0))
    else:
        print(__doc__); sys.exit(2)

if __name__ == '__main__':
    main(sys.argv[1:])
