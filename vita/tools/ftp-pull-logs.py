#!/usr/bin/env python3
"""ftp-pull-logs.py -- read-logs.sh's copy step over VitaShell's FTP instead of USB (first used for kit #11, 2026-11-04).

Usage:  python3 -I vita/tools/ftp-pull-logs.py $SHEPHERD_WORK/devicelogs/$(date +%Y%m%d-%H%M%S)
then run read-logs.sh's analysis half on that folder (lines after its copy step), which ends with perf_history.py add.
Read-only on the card: only LIST / RETR, never STOR / DELE / RNFR. Downloads only what read-logs.sh copies: ux0:data/shepherd's top-level
.txt + jitprobe.*, runtime/logs/, shots/, probes/, save-backups/, cache/jit-learn*.tsv + the kit #10 cache text files, (kit #13)
cache/gxp/'s listing,
ux0:data/shepherd-probes/, and ux0:data/psp2core-*.psp2dmp from the last 3 days -- never any other file of ux0:data.
VitaShell's LIST gives UTC times with minute resolution and no year: files get those times converted to local time; card-manifest.txt
lists every file under ux0:data/shepherd the same way (seconds :00; no card free figure over FTP)."""
import calendar, ftplib, os, re, sys, time, datetime

HOST, PORT = "192.0.2.126", 1337
OUT = sys.argv[1]
YEAR = 2026
MON = {m: i for i, m in enumerate("Jan Feb Mar Apr May Jun Jul Aug Sep Oct Nov Dec".split(), 1)}
LINE = re.compile(r"^([d-])\S+\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\w{3})\s+(\d+)\s+(\d+):(\d+)\s+(.+)$")

f = ftplib.FTP(); f.connect(HOST, PORT, timeout=30); f.login()


def ls(d):
    f.cwd(d); L = []; f.retrlines("LIST", L.append); out = []
    for x in L:
        m = LINE.match(x)
        if not m:
            print("  ?? unparsed LIST line:", x); continue
        kind, size, mon, day, hh, mm, name = m.groups()
        if name in (".", "..") or name.startswith("._"):
            continue
        t = datetime.datetime.fromtimestamp(calendar.timegm((YEAR, MON[mon], int(day), int(hh), int(mm), 0)))  # UTC -> local
        out.append((kind == "d", int(size), t, name))
    return out


got = [0, 0]


def get(remote, local, t):
    os.makedirs(os.path.dirname(local), exist_ok=True)
    with open(local + ".part", "wb") as fh:
        f.retrbinary("RETR " + remote, fh.write, 65536)
    os.replace(local + ".part", local)
    ts = time.mktime(t.timetuple()); os.utime(local, (ts, ts))
    got[0] += 1; got[1] += os.path.getsize(local)


def tree(rdir, ldir):
    for isdir, size, t, name in ls(rdir):
        if isdir:
            tree(rdir + name + "/", os.path.join(ldir, name))
        else:
            get(rdir + name, os.path.join(ldir, name), t)


S = "/ux0:/data/shepherd/"
top = ls(S)
names = {n: (d, s, t) for d, s, t, n in top}
n = 0
for d, s, t, name in top:
    if not d and name.endswith(".txt"):
        get(S + name, os.path.join(OUT, name), t); n += 1
print(f"   {n} .txt file(s) from ux0:data/shepherd/")
for j in ("jitprobe.faults", "jitprobe.state", "jitprobe.count"):
    if j in names and not names[j][0]:
        get(S + j, os.path.join(OUT, j), names[j][2]); print("  ", j)
for sub, dst in (("runtime/logs/", "runtime/logs"), ("shots/", "shots"), ("probes/", "probes"), ("save-backups/", "save-backups")):
    try:
        b = got[0]; tree(S + sub, os.path.join(OUT, dst)); print(f"   {sub}: {got[0] - b} file(s)")
    except ftplib.error_perm as e:
        print(f"   {sub}: not on the card ({e})")
cache = {(nm): (s, t) for d, s, t, nm in ls(S + "cache/") if not d}
for nm, (s, t) in cache.items():
    if nm.startswith("jit-learn") and nm.endswith(".tsv"):
        get(S + "cache/" + nm, os.path.join(OUT, "cache", nm), t); print("   cache/" + nm)
for kf in ("cache/k10index.tsv", "cache/k10-ilz-verified.tsv", "cache/il/k10-ilb.ok", "cache/boot/k10-bootscreen.txt"):
    dpath, base = kf.rsplit("/", 1)
    try:
        hit = [x for x in ls(S + dpath + "/") if not x[0] and x[3] == base]
    except ftplib.error_perm:
        hit = []
    if hit:
        get(S + kf, os.path.join(OUT, kf), hit[0][2]); print("  ", kf)
# (kit #13) cache/gxp/: the listing (names / sizes only: the .gxpc files are the console's own compiled shaders, not copied)
try:
    gx = [x for x in ls(S + "cache/gxp/") if not x[0]]
except ftplib.error_perm:
    gx = []
if gx:
    os.makedirs(os.path.join(OUT, "cache", "gxp"), exist_ok=True)
    with open(os.path.join(OUT, "cache", "gxp", "LISTING.txt"), "w") as lf:
        for d, s_, t, nm in gx: lf.write(f"{s_}\t{t:%Y-%m-%d %H:%M}\t{nm}\n")
    print(f"   cache/gxp/: {sum(1 for x in gx if x[3].endswith('.gxpc'))} program set(s) cached (listing only)")
try:
    b = got[0]; tree("/ux0:/data/shepherd-probes/", os.path.join(OUT, "shepherd-probes")); print(f"   shepherd-probes/: {got[0] - b} file(s)")
except ftplib.error_perm:
    print("   ux0:data/shepherd-probes/: not on the card (no probe run yet)")
cut = datetime.datetime.now() - datetime.timedelta(days=3)
cores = [x for x in ls("/ux0:/data/") if not x[0] and re.fullmatch(r"psp2core-.*\.psp2dmp", x[3]) and x[2] >= cut]
for d, s, t, nm in cores:
    get("/ux0:/data/" + nm, os.path.join(OUT, "coredumps", nm), t)
print(f"   coredumps/: {len(cores)} crash dump(s) from the last 3 days" if cores else "   no crash dumps from the last 3 days")

# card-manifest.txt: listing only (LIST has minute resolution; seconds written as :00)
rows = []


def walk(rdir, rel):
    for isdir, size, t, name in ls(rdir):
        if isdir:
            walk(rdir + name + "/", rel + name + "/")
        else:
            rows.append((size, t.strftime("%Y-%m-%d %H:%M:00"), rel + name))


walk(S, "")
rows.sort(key=lambda r: r[2])
with open(os.path.join(OUT, "card-manifest.txt"), "w") as fh:
    fh.write(f"# ux0:data/shepherd on {datetime.datetime.now():%Y-%m-%d %H:%M:%S}: size<TAB>mtime<TAB>path (pulled over FTP; local time)\n")
    fh.write("# card free: unknown (pulled over FTP, no df)\n")
    for r in rows:
        fh.write(f"{r[0]}\t{r[1]}\t{r[2]}\n")
print(f"   card-manifest.txt: {len(rows)} files listed")
f.quit()
print(f"pulled {got[0]} file(s), {got[1] / 1048576:.1f} MiB -> {OUT}")
