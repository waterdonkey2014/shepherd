#!/usr/bin/env python3
# GAME-AOT: read / write Mono .aotprofile files (mono/profiler/aot.h; read by mono-aot-cross --aot=...,profile=<f>,profile-only).
#   aotprofile.py dump <file>                    every record, methods as "image | ns.Class[/Nested] | name | params | signature"
#   aotprofile.py empty <file>                   a valid profile with no records (profile-only floor: what is compiled anyway)
#   aotprofile.py select <in> <out> <list>       keep only the METHOD records whose "image|class|name|signature" line (as dump
#                                                prints it, without the params column) is in <list>; images/types kept as needed
#   aotprofile.py merge <out> <in...>            one profile holding every method record of the inputs (ids renumbered)
# Format: "AOTPROFILE", int32 version (1<<16|0), records {byte type, int32 id, data}, type 0 ends. int32 little endian,
# string = int32 length + UTF-8 bytes. IMAGE: name, mvid. TYPE: byte 0x12, int32 image id, int32 ginst id (-1), name.
# GINST: int32 argc, argc type ids. METHOD: int32 class id, int32 ginst id (-1), int32 param count, name, signature.
import struct, sys

NONE, IMAGE, TYPE, GINST, METHOD = range(5)

def read(path):
    d = open(path, 'rb').read()
    assert d[:10] == b'AOTPROFILE', 'not an aotprofile'
    ver = struct.unpack_from('<i', d, 10)[0]
    assert ver == (1 << 16), 'version %x' % ver
    p = 14; recs = []
    def i32():
        nonlocal p; v = struct.unpack_from('<i', d, p)[0]; p += 4; return v
    def s():
        n = i32(); nonlocal p; v = d[p:p + n].decode(); p += n; return v
    while True:
        t = d[p]; p += 1; rid = i32()
        if t == NONE: break
        if t == IMAGE: recs.append((t, rid, s(), s()))
        elif t == TYPE:
            k = d[p]; p += 1; assert k == 0x12, 'type kind %x' % k
            recs.append((t, rid, i32(), i32(), s()))
        elif t == GINST:
            n = i32(); recs.append((t, rid, [i32() for _ in range(n)]))
        elif t == METHOD: recs.append((t, rid, i32(), i32(), i32(), s(), s()))
        else: raise SystemExit('unknown record type %d' % t)
    return recs

def write(path, recs):
    o = bytearray(b'AOTPROFILE') + struct.pack('<i', 1 << 16)
    def s(v): b = v.encode(); return struct.pack('<i', len(b)) + b
    for r in recs:
        o += bytes([r[0]]) + struct.pack('<i', r[1])
        if r[0] == IMAGE: o += s(r[2]) + s(r[3])
        elif r[0] == TYPE: o += bytes([0x12]) + struct.pack('<ii', r[2], r[3]) + s(r[4])
        elif r[0] == GINST: o += struct.pack('<i', len(r[2])) + b''.join(struct.pack('<i', x) for x in r[2])
        elif r[0] == METHOD: o += struct.pack('<iii', r[2], r[3], r[4]) + s(r[5]) + s(r[6])
    o += bytes([NONE]) + struct.pack('<i', 0)
    open(path, 'wb').write(bytes(o))

def lines(recs):
    img = {r[1]: r for r in recs if r[0] == IMAGE}; typ = {r[1]: r for r in recs if r[0] == TYPE}
    for r in recs:
        if r[0] == METHOD:
            t = typ[r[2]]; yield r, '%s|%s|%s|%s' % (img[t[2]][2], t[4], r[5], r[6])

def main():
    a = sys.argv[1:]
    if a[:1] == ['dump']:
        recs = read(a[1])
        n = {k: sum(1 for r in recs if r[0] == k) for k in (IMAGE, TYPE, GINST, METHOD)}
        print('# %s: %d images, %d types, %d ginsts, %d methods' % (a[1], n[IMAGE], n[TYPE], n[GINST], n[METHOD]))
        for r in recs:
            if r[0] == IMAGE: print('IMAGE %s %s' % (r[2], r[3]))
        for r, l in lines(recs): print('METHOD %s (params %d)' % (l, r[4]))
    elif a[:1] == ['empty']:
        write(a[1], [])
    elif a[:1] == ['select']:
        recs = read(a[1]); want = set(x.strip() for x in open(a[3]) if x.strip() and not x.startswith('#'))
        keep = [r for r, l in lines(recs) if l in want]
        tids = {r[2] for r in keep}; types = [r for r in recs if r[0] == TYPE and r[1] in tids]
        iids = {t[2] for t in types}; imgs = [r for r in recs if r[0] == IMAGE and r[1] in iids]
        write(a[2], imgs + types + keep); print('%d of %d methods kept' % (len(keep), sum(1 for r in recs if r[0] == METHOD)))
    elif a[:1] == ['merge']:
        out = []; nid = 0; seen = {}
        for f in a[2:]:
            recs = read(f); remap = {}
            for r in recs:
                if r[0] == IMAGE:
                    key = ('I', r[2])
                    if key not in seen: seen[key] = nid; out.append((IMAGE, nid, r[2], r[3])); nid += 1
                    remap[r[1]] = seen[key]
                elif r[0] == TYPE:
                    key = ('T', remap[r[2]], r[4])
                    if key not in seen: seen[key] = nid; out.append((TYPE, nid, remap[r[2]], -1, r[4])); nid += 1
                    remap[r[1]] = seen[key]
                elif r[0] == METHOD:
                    key = ('M', remap[r[2]], r[5], r[6])
                    if key not in seen: seen[key] = nid; out.append((METHOD, nid, remap[r[2]], -1, r[4], r[5], r[6])); nid += 1
        write(a[1], out); print('%d methods' % sum(1 for r in out if r[0] == METHOD))
    else:
        print(open(__file__).read().split('\n# Format')[0]); sys.exit(2)

if __name__ == '__main__':
    main()
