#!/usr/bin/env python3
"""test.sh helper (standard library only): checks what the receiver filed for one run.

  check.py <inbox> <app> <run> [checks...]
    --only a,b,c          exactly these file names
    --same NAME=PATH      file equals the local file
    --prefix NAME=PATH    file is a prefix of the local file (the device log grows after the upload: devlog reports)
    --contains NAME=TEXT  --lacks NAME=TEXT
    --meta K=V            meta.json value
    --info NAME.K=V       manifest entry field (sanitized / file_size / offset / bytes)
    --done                run marked done
  check.py <inbox> grep TEXT     exit 0 when TEXT is NOT anywhere under inbox (used for "never uploaded")
"""
import glob, json, os, sys

def main(a):
    inbox = a[0]
    if a[1] == "grep":
        needle = a[2].encode()
        for dp, _, fns in os.walk(inbox):
            for fn in fns:
                if needle in open(os.path.join(dp, fn), "rb").read():
                    print("found in", os.path.join(dp, fn)); return 1
        return 0
    app, run, rest = a[1], a[2], a[3:]
    dirs = glob.glob(os.path.join(inbox, "*", app, run))
    if len(dirs) != 1:
        print("run %s/%s: %d folders" % (app, run, len(dirs))); return 1
    rd = dirs[0]
    meta = json.load(open(os.path.join(rd, "meta.json"))) if os.path.exists(os.path.join(rd, "meta.json")) else {}
    if isinstance(meta.get("meta"), dict): meta = meta["meta"]
    man = json.load(open(os.path.join(rd, "manifest.json")))
    files = man.get("files", {})
    errs = []
    def body(name):
        p = os.path.join(rd, "files", name)
        return open(p, "rb").read() if os.path.exists(p) else None
    i = 0
    while i < len(rest):
        opt = rest[i]; val = rest[i + 1] if i + 1 < len(rest) else ""; i += 2
        if opt == "--done":
            i -= 1
            if not man.get("done"): errs.append("not done")
        elif opt == "--only":
            if set(files) != set(x for x in val.split(",") if x): errs.append("files %s != %s" % (sorted(files), val))
        elif opt in ("--same", "--prefix"):
            n, p = val.split("=", 1); got = body(n); want = open(p, "rb").read()
            if got is None: errs.append("missing " + n)
            elif opt == "--same" and got != want: errs.append("%s differs (%d vs %d)" % (n, len(got), len(want)))
            elif opt == "--prefix" and not (got and want.startswith(got)): errs.append("%s not a prefix (%d vs %d)" % (n, len(got), len(want)))
        elif opt in ("--contains", "--lacks"):
            n, t = val.split("=", 1); got = body(n) or b""
            if (t.encode() in got) != (opt == "--contains"): errs.append("%s %s %r" % (n, opt, t))
        elif opt == "--meta":
            k, v = val.split("=", 1)
            if str(meta.get(k)) != v: errs.append("meta %s=%r, want %r" % (k, meta.get(k), v))
        elif opt == "--info":
            nk, v = val.split("=", 1); n, k = nk.rsplit(".", 1)
            if str(files.get(n, {}).get(k)) != v: errs.append("%s.%s=%r, want %r" % (n, k, files.get(n, {}).get(k), v))
        else:
            errs.append("unknown option " + opt)
    for e in errs: print("  - " + e)
    return 1 if errs else 0

sys.exit(main(sys.argv[1:]))
