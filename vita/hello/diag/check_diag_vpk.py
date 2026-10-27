#!/usr/bin/env python3
"""VPK gate: a VPK the Vita's installer and LiveArea accept, and that is really THIS build. Written for the devfail1 diag
apps; since the integration branch (2026-10-13) every hello and menu VPK runs it (vita/hello/build.sh, vita/shepherd/build.sh).
  - eboot.bin, sce_sys/param.sfo (TITLE_ID / TITLE as expected), sce_sys/icon0.png
  - TITLE_ID and TITLE read from param.sfo's key table and compared EXACTLY; an empty expected title is refused (device kit #2
    review: a caller passing "" had made the TITLE check pass vacuously, and a substring test let "Shepherd Menu" match
    "Shepherd Menu Diag")
  - sce_sys/livearea/contents/{template.xml,bg.png,startup.png}; template.xml names bg.png + startup.png
  - every PNG: 8-bit indexed (bit depth 8, colour type 3) -- LiveArea refuses other PNGs
  - eboot.bin inside == the freshly built eboot (sha256), and the .vpk is newer than that eboot (no stale .vpk reuse)
  - --bcl=<dir>: every managed/*.dll inside == <dir>/<name> (sha256: the build's own work/bcl copy, no stale DLL);
    --bcl-all: also every <dir>/*.dll is packaged (the menu packages its whole work/bcl; hello packages a chosen subset)
  - extra required entries (e.g. managed/HelloDiag.dll)
usage: check_diag_vpk.py [--bcl=<dir> [--bcl-all]] <vpk> <eboot.bin> <TITLE_ID> <title> [required entry ...]"""
import hashlib, os, struct, sys, zipfile
opts = [x for x in sys.argv[1:] if x.startswith('--')]
args = [x for x in sys.argv[1:] if not x.startswith('--')]
bcl = next((x[len('--bcl='):] for x in opts if x.startswith('--bcl=')), None)
bcl_all = '--bcl-all' in opts
unknown_opts = [x for x in opts if not (x.startswith('--bcl=') or x == '--bcl-all')]
if unknown_opts or len(args) < 4 or (bcl_all and not bcl):
    sys.exit(__doc__ + (f'\nunknown option(s): {unknown_opts}' if unknown_opts else ''))
vpk, eboot, tid, title = args[0:4]
req = args[4:]
if not tid.strip() or not title.strip():
    sys.exit(__doc__ + '\nthe expected TITLE_ID and title must not be empty')
def sfo_params(b):
    """param.sfo -> {key: value} (strings without their NUL padding, integers as int); {} if it is not a PSF."""
    if b[:4] != b'\0PSF' or len(b) < 20:
        return {}
    kt, dt, n = struct.unpack_from('<III', b, 8)
    out = {}
    for i in range(n):
        ko, fmt, ln, mx, do = struct.unpack_from('<HHIII', b, 20 + 16 * i)
        k = b[kt + ko:b.index(b'\0', kt + ko)].decode('utf-8', 'replace')
        v = b[dt + do:dt + do + ln]
        out[k] = struct.unpack('<I', v[:4])[0] if fmt == 0x0404 else v.split(b'\0', 1)[0].decode('utf-8', 'replace')
    return out
z = zipfile.ZipFile(vpk)
names = set(z.namelist())
bad = []
def ok(msg): print('ok   ' + msg)
def fail(msg): bad.append(msg); print('FAIL ' + msg)
need = ['eboot.bin', 'sce_sys/param.sfo', 'sce_sys/icon0.png', 'sce_sys/livearea/contents/template.xml',
        'sce_sys/livearea/contents/bg.png', 'sce_sys/livearea/contents/startup.png'] + req
for n in need:
    (ok if n in names else fail)(f'entry {n}' + ('' if n in names else ' MISSING'))
for n in sorted(names):
    if n.lower().endswith('.png'):
        b = z.read(n)
        if b[:8] != b'\x89PNG\r\n\x1a\n' or b[12:16] != b'IHDR':
            fail(f'{n}: not a PNG'); continue
        w, h, bd, ct = struct.unpack('>IIBB', b[16:26])
        (ok if (bd, ct) == (8, 3) else fail)(f'{n}: {w}x{h}, bit depth {bd}, colour type {ct}' + ('' if (bd, ct) == (8, 3) else ' (must be 8-bit indexed)'))
if 'sce_sys/livearea/contents/template.xml' in names:
    t = z.read('sce_sys/livearea/contents/template.xml').decode('utf-8', 'replace')
    (ok if 'bg.png' in t and 'startup.png' in t else fail)('template.xml references bg.png and startup.png')
if 'sce_sys/param.sfo' in names:
    sp = sfo_params(z.read('sce_sys/param.sfo'))
    (ok if sp.get('TITLE_ID') == tid else fail)(f'param.sfo TITLE_ID {tid}' + ('' if sp.get('TITLE_ID') == tid else f' (it holds {sp.get("TITLE_ID")!r})'))
    (ok if sp.get('TITLE') == title else fail)(f'param.sfo TITLE "{title}"' + ('' if sp.get('TITLE') == title else f' (it holds {sp.get("TITLE")!r})'))
if 'eboot.bin' in names:
    a = hashlib.sha256(z.read('eboot.bin')).hexdigest(); b = hashlib.sha256(open(eboot, 'rb').read()).hexdigest()
    (ok if a == b else fail)(f'eboot.bin in the VPK == the fresh build ({a[:16]})')
(ok if os.path.getmtime(vpk) >= os.path.getmtime(eboot) else fail)('the .vpk is newer than eboot.bin (no stale VPK)')
if bcl is not None:
    inside = {n[len('managed/'):]: hashlib.sha256(z.read(n)).hexdigest() for n in names if n.startswith('managed/') and n.endswith('.dll')}
    local = {f: hashlib.sha256(open(os.path.join(bcl, f), 'rb').read()).hexdigest() for f in os.listdir(bcl) if f.endswith('.dll')} if os.path.isdir(bcl) else {}
    miss = sorted(set(inside) - set(local)); diff = sorted(f for f in set(inside) & set(local) if inside[f] != local[f])
    (ok if inside and not (miss or diff) else fail)(f'managed/: {len(inside)} DLL(s) == {bcl} (sha256)' +
        ('' if inside else ' -- none packaged') + (f' -- not in {bcl}: {miss[:5]}' if miss else '') + (f' -- differ: {diff[:5]}' if diff else ''))
    if bcl_all:
        extra = sorted(set(local) - set(inside))
        (ok if not extra else fail)(f'every DLL of {bcl} is packaged' + (f' -- missing {len(extra)}: {extra[:5]}' if extra else ''))
print(f'{len(names)} entries, {os.path.getsize(vpk)} bytes')
print('PASS VPK gate (LiveArea set, 8-bit PNGs, fresh contents)' if not bad else f'FAIL VPK gate: {len(bad)} problem(s)')
sys.exit(1 if bad else 0)
