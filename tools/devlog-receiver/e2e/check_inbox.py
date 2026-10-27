#!/usr/bin/env python3
"""Helpers for e2e_test.sh (standard library only).

  check_inbox.py run <inbox> <app> <run> [--file NAME=LOCALPATH]... [--has NAME]... [--only NAME,NAME,...]
                 [--prefix NAME=LOCALPATH]... [--outcome X] [--meta KEY=VALUE]... [--done] [--absent-text TEXT]...
      exits 0 when the run is in the inbox and every check holds; prints what failed otherwise
  check_inbox.py runs <inbox> <app>             prints the run ids received for app (one per line)
  check_inbox.py grep <dir> <text>              like grep: exit 0 if <text> occurs in a file under <dir>, else 1
  check_inbox.py timed <outfile> -- cmd ...     runs cmd, stdout -> outfile, appends wall_ms=<n>, returns its exit code
"""

import glob
import hashlib
import json
import os
import subprocess
import sys
import time


def find_run(inbox, app, run):
    hits = glob.glob(os.path.join(inbox, "*", app, run))
    return hits[0] if hits else None


def cmd_run(a):
    inbox, app, run = a[0], a[1], a[2]
    rest = a[3:]
    errs = []
    rd = find_run(inbox, app, run)
    if not rd:
        print("run %s/%s not in %s" % (app, run, inbox))
        return 1
    try:
        man = json.load(open(os.path.join(rd, "manifest.json")))
    except (OSError, ValueError):
        man = {"files": {}}
    try:
        meta = json.load(open(os.path.join(rd, "meta.json")))["meta"]
    except (OSError, ValueError, KeyError):
        meta = {}
    files = man.get("files", {})
    i = 0
    while i < len(rest):
        opt, val = rest[i], rest[i + 1] if i + 1 < len(rest) else None
        i += 2
        if opt == "--file":
            name, local = val.split("=", 1)
            p = os.path.join(rd, "files", name)
            if not os.path.exists(p):
                errs.append("missing file %s" % name)
                continue
            got = open(p, "rb").read()
            want = open(local, "rb").read()
            if got != want:
                errs.append("%s differs from %s (%d vs %d bytes)" % (name, local, len(got), len(want)))
            elif files.get(name, {}).get("sha256") != hashlib.sha256(got).hexdigest():
                errs.append("manifest sha256 of %s is wrong" % name)
        elif opt == "--prefix":
            # the device log keeps growing after the upload (devlog reports into it): the upload must be a prefix
            name, local = val.split("=", 1)
            p = os.path.join(rd, "files", name)
            if not os.path.exists(p):
                errs.append("missing file %s" % name)
                continue
            got = open(p, "rb").read()
            want = open(local, "rb").read()
            if not got or not want.startswith(got):
                errs.append("%s is not a prefix of %s (%d vs %d bytes)" % (name, local, len(got), len(want)))
            elif files.get(name, {}).get("sha256") != hashlib.sha256(got).hexdigest():
                errs.append("manifest sha256 of %s is wrong" % name)
        elif opt == "--has":
            if val not in files:
                errs.append("missing file %s" % val)
        elif opt == "--only":
            want = set(x for x in val.split(",") if x)
            if set(files) != want:
                errs.append("files %s, expected exactly %s" % (sorted(files), sorted(want)))
        elif opt == "--outcome":
            o = man.get("outcome") or meta.get("outcome")
            if o != val:
                errs.append("outcome %r, expected %r" % (o, val))
        elif opt == "--meta":
            k, v = val.split("=", 1)
            if meta.get(k) != v:
                errs.append("meta %s=%r, expected %r" % (k, meta.get(k), v))
        elif opt == "--done":
            i -= 1
            if not man.get("done"):
                errs.append("run not marked done")
        elif opt == "--absent-text":
            for dp, _, fns in os.walk(rd):
                for fn in fns:
                    if val.encode() in open(os.path.join(dp, fn), "rb").read():
                        errs.append("%s contains a forbidden text" % os.path.relpath(os.path.join(dp, fn), rd))
        else:
            errs.append("unknown option %s" % opt)
    for e in errs:
        print("  - " + e)
    return 1 if errs else 0


def cmd_runs(a):
    inbox, app = a
    for p in sorted(glob.glob(os.path.join(inbox, "*", app, "*"))):
        print(os.path.basename(p))
    return 0


def cmd_grep(a):
    d, text = a
    hit = False
    for dp, _, fns in os.walk(d):
        for fn in fns:
            try:
                if text.encode() in open(os.path.join(dp, fn), "rb").read():
                    print("  found in %s" % os.path.join(dp, fn))
                    hit = True
            except OSError:
                pass
    return 0 if hit else 1


def cmd_timed(a):
    out = a[0]
    cmd = a[a.index("--") + 1:]
    t = time.monotonic()
    with open(out, "w") as f:
        rc = subprocess.call(cmd, stdout=f, stderr=subprocess.STDOUT)
    with open(out, "a") as f:
        f.write("wall_ms=%d\nexit=%d\n" % ((time.monotonic() - t) * 1000, rc))
    return rc


if __name__ == "__main__":
    cmds = {"run": cmd_run, "runs": cmd_runs, "grep": cmd_grep, "timed": cmd_timed}
    if len(sys.argv) < 2 or sys.argv[1] not in cmds:
        print(__doc__)
        sys.exit(2)
    sys.exit(cmds[sys.argv[1]](sys.argv[2:]))
