#!/usr/bin/env python3
"""ONE-FILE KIT: packs a device kit folder into a single zip the owner copies to the card (owner 2026-11-04: "start packing the kits
in zips (everything that is needed and or optional) so that I just need to copy one file").

  make-kit-zip.py <kit folder>                    -> <kit folder>/../shepherd-kit<N>.zip (or --out FILE)
  make-kit-zip.py --check-card <kit folder>       read-only: the card's ux0:data/shepherd/ against the kit (VitaShell START -> USB first;
                                                  --card <mount point> picks the volume). Run it when the card is mounted for the logs.
  make-kit-zip.py --one-folder <kit folder>       (kit #14) THE SAME FULL ZIP with ONE top folder: README-KIT<N>.txt goes inside shepherd/ (it lands
                                                  in ux0:data/shepherd/ with the paste), so pressing X on the zip shows only 'shepherd'
  make-kit-zip.py --update-from <older kit> --out FILE <kit folder>
                                                  AN UPDATE ZIP (kit 13.1): only what differs from the older kit -- see below.

THE ZIP (stored, no compression: the Vita unpacks it at copy speed):
  shepherd/...                     = the kit's data/shepherd/ (every file push-kit.sh copies: apps, probes, owner files, the env zip)
  shepherd/kit<N>-env/...          = the env files, already unpacked (the members of data/shepherd/kit<N>-env-files.zip, which must equal
                                     extras/card-env/kit<N>-env/ byte for byte)
  shepherd/vpk/alternates/*.vpk    = the kit's alternates/ (optional apps; they only sit on the card until the owner installs one)
  README-KIT<N>.txt                = the kit's README, at the top of the zip (readable in VitaShell; not copied by the steps)
ON THE VITA: copy the zip into ux0:data/; in VitaShell press X on it (it opens like a folder), TRIANGLE -> Copy on the folder shepherd,
go to ux0:data/, TRIANGLE -> Paste. VitaShell's paste from an archive MERGES (archive.c extractArchivePath: sceIoMkdir tolerates EEXIST;
extractArchiveFile: SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, no prompt; a failed file is removed): kit files replace the card's copies,
every other file in ux0:data/shepherd/ (sandbox saves, the game's DLLs, data, audio bake, shepherd.pack, cache) stays. That is push-kit.sh's
rule too (rsync -rt, never deletes). The Mac Finder is different: dragging a folder onto a same-name folder REPLACES it -- the zip is
copied as ONE file, never unpacked onto the card from the Mac.

REFUSED (nothing written): a kit file missing or not matching SHA256SUMS.txt; a file under data/ that SHA256SUMS.txt does not list; the
env zip not matching extras/card-env/; any file that would land at the TOP of ux0:data/shepherd/ named env*.txt or *args*.txt (a live
switch: every app reads it at start); a name that is not plain ASCII, has '..' / a backslash / a leading '/', or collides with another
name case-insensitively (the card is exFAT). Written as <out>.partial, re-read and checked member by member (sha256 against
SHA256SUMS.txt, the member list exactly the planned one, CRCs), then renamed; with bsdtar on the Mac (libarchive -- VitaShell reads
archives with libarchive) the zip is also unpacked into a temporary folder and compared file by file.
AN UPDATE ZIP (--update-from <older kit folder>, for a hotfix of a kit the card already holds): the same plan and the same refusals on both
kits, then only the members that are new or whose sha256 differs from the older kit's. Its ONE top folder is shepherd/: every README-KIT<N>[-x].txt
of the kit goes inside it, so the Copy -> Paste above merges it into ux0:data/shepherd/. A file the older kit has and this one lacks is
REFUSED (a paste cannot delete anything from the card).
Timestamps: each member keeps its kit file's modification time. Never a fixed date: the apps key their verify caches on size + mtime
(k10_aot.inc.c: cache/k10-ilz-verified.tsv; k10_ilb.inc.c: boot.ilb), so two builds of one file with one size and one date could
skip a check.
"""
import argparse
import hashlib
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile

CHUNK = 1 << 20


def sha256_file(p):
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for b in iter(lambda: f.read(CHUNK), b""):
            h.update(b)
    return h.hexdigest()


def die(msg):
    print("STOP: " + msg + " -- nothing was written")
    sys.exit(1)


def junk(name):
    return name == ".DS_Store" or name.startswith("._")


def load_sums(kit):
    p = os.path.join(kit, "SHA256SUMS.txt")
    if not os.path.isfile(p):
        die(f"{p} missing -- is this the kit folder?")
    sums = {}
    for n, line in enumerate(open(p, encoding="utf-8"), 1):
        line = line.rstrip("\n")
        if not line.strip():
            continue
        m = re.fullmatch(r"([0-9a-f]{64}) [ *](.+)", line)
        if not m:
            die(f"SHA256SUMS.txt line {n} is not '<sha256>  <path>'")
        sums[m.group(2)] = m.group(1)
    return sums


def kit_number(kit):
    readmes = [f for f in os.listdir(kit) if re.fullmatch(r"README-KIT(\d+)\.txt", f)]
    if len(readmes) != 1:
        die(f"expected exactly one README-KIT<N>.txt in {kit}, found {readmes or 'none'}")
    return int(re.fullmatch(r"README-KIT(\d+)\.txt", readmes[0]).group(1)), readmes[0]


def check_name(arc):
    if not arc.isascii() or not arc.isprintable():
        die(f"{arc!r}: not a plain ASCII name")
    if arc.startswith("/") or "\\" in arc or any(part in ("", ".", "..") for part in arc.split("/")):
        die(f"{arc!r}: not a clean relative path")


def plan(kit, sums, n, one_folder=False):
    """[(arcname, source bytes-or-path, sha256, mtime)] for every file, in a fixed order"""
    # 1. every listed kit file present and exact (push-kit.sh step 1, plus alternates/ and extras/)
    bad = []
    for rel, s in sorted(sums.items()):
        p = os.path.join(kit, rel)
        if not os.path.isfile(p):
            bad.append(f"missing: {rel}")
        elif sha256_file(p) != s:
            bad.append(f"damaged: {rel}")
    if bad:
        die(f"{len(bad)} kit file(s) do not match SHA256SUMS.txt:\n  " + "\n  ".join(bad))
    # 2. nothing under data/ that the sums do not list (push-kit.sh refuses those too)
    src = os.path.join(kit, "data", "shepherd")
    if not os.path.isdir(src):
        die(f"{src} missing")
    extra = []
    for root, dirs, files in os.walk(os.path.join(kit, "data")):
        dirs.sort()
        for f in sorted(files):
            if junk(f):
                continue
            rel = os.path.relpath(os.path.join(root, f), kit)
            if rel not in sums:
                extra.append(rel)
    if extra:
        die("files in data/ that SHA256SUMS.txt does not list:\n  " + "\n  ".join(extra))
    items = []
    for rel in sorted(r for r in sums if r.startswith("data/shepherd/")):
        p = os.path.join(kit, rel)
        items.append(("shepherd/" + rel[len("data/shepherd/"):], p, sums[rel], os.path.getmtime(p)))
    # 3. the env files, unpacked: the env zip's members, which must equal extras/card-env/kit<N>-env/ exactly
    envzip_rel = f"data/shepherd/kit{n}-env-files.zip"
    envdir_rel = f"extras/card-env/kit{n}-env"
    if envzip_rel in sums:
        envzip = os.path.join(kit, envzip_rel)
        zmt = os.path.getmtime(envzip)
        with zipfile.ZipFile(envzip) as z:
            if z.testzip() is not None:
                die(f"{envzip_rel}: a member fails its CRC")
            members = {}
            for zi in z.infolist():
                if zi.is_dir():
                    continue
                if not zi.filename.startswith(f"kit{n}-env/"):
                    die(f"{envzip_rel}: member {zi.filename} is outside kit{n}-env/")
                members[zi.filename] = z.read(zi)
        listed = {r[len("extras/card-env/"):]: s for r, s in sums.items() if r.startswith(envdir_rel + "/")}
        if not listed:
            die(f"{envdir_rel}/ is not in SHA256SUMS.txt (the env zip has nothing to be checked against)")
        if set(members) != set(listed):
            die(f"{envzip_rel} and {envdir_rel}/ differ: only in the zip {sorted(set(members) - set(listed))}, "
                f"only in the folder {sorted(set(listed) - set(members))}")
        for name, data in sorted(members.items()):
            s = hashlib.sha256(data).hexdigest()
            if s != listed[name]:
                die(f"{envzip_rel}: {name} differs from {envdir_rel}/")
            items.append(("shepherd/" + name, data, s, zmt))
    # 4. alternates (optional apps)
    for rel in sorted(r for r in sums if r.startswith("alternates/")):
        if not rel.endswith(".vpk") or rel.count("/") != 1:
            die(f"{rel}: alternates/ holds only .vpk files, one level")
        p = os.path.join(kit, rel)
        items.append(("shepherd/vpk/alternates/" + rel.split("/", 1)[1], p, sums[rel], os.path.getmtime(p)))
    # 5. the README at the top
    readme = f"README-KIT{n}.txt"
    rp = os.path.join(kit, readme)
    with open(rp, "rb") as f:
        rb = f.read()
    items.append((("shepherd/" + readme) if one_folder else readme, rb, hashlib.sha256(rb).hexdigest(), os.path.getmtime(rp)))
    check_names(items)
    if one_folder and {arc.split("/")[0] for arc, *_ in items} != {"shepherd"}:
        die("--one-folder: a member outside shepherd/")
    return items


def check_names(items):
    """names: clean, unique case-insensitively, no live switch at the top of ux0:data/shepherd/"""
    seen = {}
    for arc, *_ in items:
        check_name(arc)
        k = arc.lower()
        if k in seen:
            die(f"{arc} and {seen[k]} are the same name on the card (exFAT ignores case)")
        seen[k] = arc
        parts = arc.split("/")
        if len(parts) == 2 and parts[0] == "shepherd" and re.fullmatch(r"(env.*|.*args.*)\.txt", parts[1], re.I):
            die(f"{arc} would land at the TOP of ux0:data/shepherd/ -- a live env / args file every app reads at start")
        if parts[0] != "shepherd" and len(parts) > 1:
            die(f"{arc}: only shepherd/ and the README may be in the zip")
    for k, arc in seen.items():
        # a file named like a folder another member needs (shepherd/vpk as a file and shepherd/vpk/x)
        for i in range(1, arc.count("/") + 1):
            d = "/".join(arc.split("/")[:i]).lower()
            if d in seen:
                die(f"{seen[d]} is a file but {arc} needs it as a folder")


def update_items(kit, sums, n, items, old):
    """the update zip's plan: the members of this kit that are new or differ from the older kit, one top folder shepherd/ (the READMEs inside it)"""
    if not os.path.isdir(old):
        die(f"--update-from: {old} is not a folder")
    old_sums = load_sums(old)
    old_n, _ = kit_number(old)
    if old_n != n:
        die(f"--update-from: the older kit is kit #{old_n}, this one kit #{n}: an update zip stays inside one kit's numbering")
    was = {arc: s for arc, _, s, _ in plan(old, old_sums, old_n) if "/" in arc}
    now = {arc: (src, s, mt) for arc, src, s, mt in items if "/" in arc}
    gone = sorted(set(was) - set(now))
    if gone:
        die(f"{len(gone)} file(s) of the older kit are not in this one, and a paste cannot delete them from the card:\n  " + "\n  ".join(gone[:20]))
    out = [(arc, src, s, mt) for arc, (src, s, mt) in sorted(now.items()) if was.get(arc) != s]
    for f in sorted(os.listdir(kit)):
        if re.fullmatch(r"README-KIT\d+(-[0-9A-Za-z]+)?\.txt", f):
            if f not in sums:
                die(f"{f} is not in SHA256SUMS.txt")
            p = os.path.join(kit, f)
            out.append(("shepherd/" + f, p, sums[f], os.path.getmtime(p)))
    if not out:
        die("nothing differs from the older kit")
    check_names(out)
    if {arc.split("/")[0] for arc, *_ in out} != {"shepherd"}:
        die("an update zip has one top folder, shepherd/")
    return out


def zinfo(name, mtime, is_dir=False):
    t = time.localtime(max(mtime, 315532800))  # zip dates start at 1980
    zi = zipfile.ZipInfo(name, date_time=t[:6])
    zi.compress_type = zipfile.ZIP_STORED
    zi.create_system = 3  # unix
    zi.external_attr = ((0o40755 if is_dir else 0o100644) << 16) | (0x10 if is_dir else 0)
    return zi


def write_zip(items, out):
    tmp = out + ".partial"
    if os.path.exists(tmp):
        os.remove(tmp)
    dirs = {}
    for arc, _, _, mt in items:
        parts = arc.split("/")[:-1]
        for i in range(1, len(parts) + 1):
            d = "/".join(parts[:i]) + "/"
            dirs[d] = max(dirs.get(d, 0), mt)
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_STORED, allowZip64=True) as z:
        for d in sorted(dirs):
            z.writestr(zinfo(d, dirs[d], True), b"")
        for arc, src, _, mt in items:
            zi = zinfo(arc, mt)
            if isinstance(src, bytes):
                z.writestr(zi, src)
            else:
                with open(src, "rb") as f, z.open(zi, "w", force_zip64=os.path.getsize(src) > zipfile.ZIP64_LIMIT) as w:
                    shutil.copyfileobj(f, w, CHUNK)
    return tmp, sorted(dirs)


def verify_zip(path, items, dirs):
    want = {arc: s for arc, _, s, _ in items}
    with zipfile.ZipFile(path) as z:
        names = [zi.filename for zi in z.infolist()]
        if len(names) != len(set(names)):
            return "a member name appears twice"
        files = {zi.filename for zi in z.infolist() if not zi.is_dir()}
        dset = {zi.filename for zi in z.infolist() if zi.is_dir()}
        if files != set(want):
            return f"member list differs: extra {sorted(files - set(want))[:5]}, missing {sorted(set(want) - files)[:5]}"
        if dset != set(dirs):
            return "folder entries differ from the plan"
        for zi in z.infolist():
            if zi.compress_type != zipfile.ZIP_STORED:
                return f"{zi.filename} is compressed"
            if zi.is_dir():
                continue
            h = hashlib.sha256()
            with z.open(zi) as r:  # zipfile checks each member's CRC-32 at its end
                for b in iter(lambda: r.read(CHUNK), b""):
                    h.update(b)
            if h.hexdigest() != want[zi.filename]:
                return f"{zi.filename}: sha256 differs from the kit"
    return None


def libarchive_check(path, items):
    """unpack with bsdtar (libarchive, VitaShell's reader) and compare every file"""
    bsdtar = shutil.which("bsdtar") or ("/usr/bin/bsdtar" if os.path.exists("/usr/bin/bsdtar") else None)
    if not bsdtar:
        return "skipped (no bsdtar on this Mac)"
    with tempfile.TemporaryDirectory(prefix="kitzip-", dir=os.path.dirname(os.path.abspath(path))) as td:
        r = subprocess.run([bsdtar, "-xf", path, "-C", td], capture_output=True, text=True)
        if r.returncode != 0:
            return f"FAIL: bsdtar could not unpack it: {r.stderr.strip()[:300]}"
        want = {arc: s for arc, _, s, _ in items}
        got = set()
        for root, _, files in os.walk(td):
            for f in files:
                got.add(os.path.relpath(os.path.join(root, f), td))
        if got != set(want):
            return f"FAIL: libarchive unpacked a different file list (extra {sorted(got - set(want))[:5]}, missing {sorted(set(want) - got)[:5]})"
        for arc, s in want.items():
            if sha256_file(os.path.join(td, arc)) != s:
                return f"FAIL: libarchive unpacked {arc} with other bytes"
    return f"ok ({len(want)} files unpacked by libarchive, all exact)"


def build(kit, out, update_from=None, one_folder=False):
    sums = load_sums(kit)
    n, readme = kit_number(kit)
    if update_from and not out:
        die("--update-from needs --out: the update's own name, never the kit's")
    out = out or os.path.join(os.path.dirname(os.path.abspath(kit)), f"shepherd-kit{n}.zip")
    print(f"make-kit-zip: kit #{n} ({kit}) -> {out}" + (f" (UPDATE from {update_from})" if update_from else "") + (" (one top folder: the README inside shepherd/)" if one_folder else ""))
    if update_from and one_folder:
        die("--one-folder is the full zip's form; an update zip has one top folder already")
    items = plan(kit, sums, n, one_folder)
    if update_from:
        items = update_items(kit, sums, n, items, update_from)
    print(f"   ok: {len(sums)} kit files match SHA256SUMS.txt; {len(items)} files planned")
    tmp, dirs = write_zip(items, out)
    err = verify_zip(tmp, items, dirs)
    if err:
        os.remove(tmp)
        die(f"the written zip failed its check: {err}")
    la = libarchive_check(tmp, items)
    if la.startswith("FAIL"):
        os.remove(tmp)
        die(la)
    os.replace(tmp, out)
    zs = sha256_file(out)
    with open(out + ".sha256", "w") as f:
        f.write(f"{zs}  {os.path.basename(out)}\n")
    size = os.path.getsize(out)
    groups = {}
    for arc, src, _, _ in items:
        parts = arc.split("/")
        g = "/".join(parts[:3]) if parts[:2] == ["shepherd", "vpk"] and len(parts) > 3 else "/".join(parts[:2])
        groups.setdefault(g, [0, 0])
        groups[g][0] += 1
        groups[g][1] += len(src) if isinstance(src, bytes) else os.path.getsize(src)
    print(f"   zip checked member by member (sha256 + CRC); libarchive: {la}")
    for g in sorted(groups):
        c, b = groups[g]
        print(f"     {g:<44} {c:>4} file(s) {b / 1048576:8.1f} MiB")
    print(f"DONE: {out} ({size / 1048576:.1f} MiB, sha256 {zs[:16]}...)")
    print(f"   card space needed: about {2 * size / 1048576:.0f} MiB free for the copy + the paste (the zip can be deleted afterwards)")


# ---- read-only card check -------------------------------------------------------------------------------------------------------
def is_card(d):
    return all(os.path.isdir(os.path.join(d, x)) for x in ("VitaShell", "app", "data")) and not os.path.exists(os.path.join(d, "gc"))


def check_card(kit, card):
    sums = load_sums(kit)
    n, _ = kit_number(kit)
    if card:
        if not is_card(card):
            print(f"STOP: {card} is not a Vita memory card (needs VitaShell/, app/, data/ and no gc/)")
            sys.exit(1)
    else:
        cands = [os.path.join("/Volumes", d) for d in sorted(os.listdir("/Volumes")) if is_card(os.path.join("/Volumes", d))]
        if len(cands) != 1:
            print(f"STOP: {'no' if not cands else 'more than one'} volume looks like the Vita memory card {cands} "
                  "(VitaShell -> START -> USB; or --card <mount point>)")
            sys.exit(1)
        card = cands[0]
    root = os.path.join(card, "data", "shepherd")
    print(f"check-card (read-only): kit #{n} against {root}")
    want, optional = {}, set()
    for rel, s in sums.items():
        if rel.startswith("data/shepherd/"):
            want[rel[len("data/"):]] = s
        elif rel.startswith(f"extras/card-env/kit{n}-env/"):
            want["shepherd/" + rel[len("extras/card-env/"):]] = s
        elif rel.startswith("alternates/"):
            want["shepherd/vpk/alternates/" + rel.split("/", 1)[1]] = s
            optional.add("shepherd/vpk/alternates/" + rel.split("/", 1)[1])
    missing, differ, absent_opt, ok = [], [], [], 0
    for rel, s in sorted(want.items()):
        p = os.path.join(card, "data", rel)
        if not os.path.isfile(p):
            (absent_opt if rel in optional else missing).append(rel)
        elif sha256_file(p) != s:
            differ.append(rel)
        else:
            ok += 1
    live = sorted(f for f in os.listdir(root) if re.fullmatch(r"(env.*|.*args.*)\.txt", f, re.I)) if os.path.isdir(root) else []
    print(f"   {ok} / {len(want)} kit files on the card are exact")
    for r in absent_opt:
        print(f"   not on the card (optional, fine): ux0:data/{r}")
    for r in missing:
        print(f"   MISSING  ux0:data/{r}")
    for r in differ:
        print(f"   DIFFERS  ux0:data/{r}")
    print("   live env / args files at the top of ux0:data/shepherd/: " + (", ".join(live) if live else "none"))
    print("PASS" if not missing and not differ else "PROBLEM: the card does not hold the whole kit (see above)")
    sys.exit(0 if not missing and not differ else 1)


def main():
    ap = argparse.ArgumentParser(description="pack a Shepherd device kit into one zip, or check the card against it (read-only)")
    ap.add_argument("kit")
    ap.add_argument("--out")
    ap.add_argument("--update-from", metavar="OLDER_KIT", help="only what differs from this older kit, one top folder shepherd/")
    ap.add_argument("--one-folder", action="store_true", help="(kit #14) the full zip with the README inside shepherd/: one top folder")
    ap.add_argument("--check-card", action="store_true")
    ap.add_argument("--card")
    a = ap.parse_args()
    kit = os.path.abspath(a.kit)
    if a.check_card:
        check_card(kit, a.card)
    else:
        build(kit, a.out and os.path.abspath(a.out), a.update_from and os.path.abspath(a.update_from), a.one_folder)


if __name__ == "__main__":
    main()
