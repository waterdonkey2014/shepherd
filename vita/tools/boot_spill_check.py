#!/usr/bin/env python3
"""Device kit #10, track boot (vita/notes/kit10-boot.md section 4b): the BOOT SPILL CHECK / startup-prefix guard -- run after every
rebuild or re-stage, on the Mac, before a layout regression costs the device card seeks or cache misses. It only READS its inputs and
prints; it writes nothing (its output names the owner's DLLs / methods: keep it in the work dir, never in git).

  boot_spill_check.py ilb <boot.ilb> <assemblies.txt> [<assemblies.txt> ...] [--managed <card managed dir>]
      The IL bundle (vita/tools/il_bundle.py, read by vita/shepherd/k10_ilb.inc.c) against the boot order a start RECORDED (any start's
      runtime/logs/<app>/assemblies.txt: the loaded assemblies in load order; the device's own or a vlx run's). Per order file:
        spill    a card DLL (ux0:data/shepherd/managed/, not Shepherd.*) the start loaded that is NOT in the bundle: Mono reads it from
                 its own file on the boot thread (a directory lookup, an open, a seek) -- the cost the bundle exists to remove
        order    the bundled DLLs in the order the start asked for them vs the bundle's order: an inversion is a DLL Mono may wait for
                 while the stream reads others first (the host's "N waited for")
        unused   bundled DLLs the start never loaded: read and kept in memory for nothing (the host's "read but never asked for") --
                 IL pool / newlib memory for the whole run, so it makes the result LOOK as spill and order do (kit #10 review fix)
      --managed: each bundled DLL compared with the card's own file (size + FNV-1a): a STALE entry means the next start's verify fails
      and the host falls back to the loose files for every DLL until the bundle is re-made (stage-owner.sh STAGE_ILBUNDLE=...).
      Also checks the bundle's own header / index checksums and every entry's FNV-1a over its bytes.

  boot_spill_check.py prefix <order list> <image> [<image> ...]
      The kit #9 R5 hot-first list (SHEPHERD_K9_ORDER, vita/tools/k9_order.py; owner-local) against a BUILT image (a build's
      work/obj/shepherd_menu.velf or a vlx .elf; read with arm-vita-eabi-nm from ~/vitasdk/bin). The kit #9 compiler puts the listed
      methods first, in rank order, in each AOT module's two code regions: the Mono-backend methods (mono_aot_<M>jit_code_start .. _end;
      images with only the local jit_code_start / _end labels -- the game-AOT apps' non-LLVM runtime -- get one region per label pair)
      and the LLVM methods (right after mono_aot_<M>_icall_cold_wrapper_*, where llvm_code_start sits, up to its init_method markers).
      After a rebuild a renamed or removed method no longer matches and silently drops out of the hot prefix. Per image:
        matched  list names that are AOT methods of the image, by region, and how many sit in the region's leading (ranked) run
        spilled  matched methods placed after an unlisted method (outside the prefix: the order did not take)
        C / PLT  list names that are runtime C functions or PLT stubs / unbox trampolines (ut_*): the compiler orders neither (expected)
        MISSING  list names not in the image at all: renamed or removed since the list was made (or vlx-only functions: the list comes
                 from vlx runs) -- re-make the list or accept the loss
      Two images = the same list on both (e.g. kit #9's build and this one): what a change cost the prefix.

Exit status: 0 = no spill, nothing stale or missing in the AOT part beyond --allow-missing; 1 = something to look at; 2 = unreadable input.
Standard library only."""
import argparse, os, re, struct, subprocess, sys, collections

GAME_PREFIX = "/ux0:data/shepherd/managed/"

def fnv32(b, h=2166136261):
    for x in b:
        h = ((h ^ x) * 16777619) & 0xffffffff
    return h

# ------------------------------------------------------------------------------------------------------------------- the IL bundle

ILZ = set()   # kit #10 integration: the bundle's compressed-IL entries (flags bit 0; vita/tools/il_bundle.py --ilz)

def read_bundle(path):
    """The bundle's index [(name, offset, size, fnv)] + the problems found in its own checksums."""
    raw = open(path, "rb").read()
    probs = []
    if len(raw) < 32: raise SystemExit(f"{path}: {len(raw)} bytes: not a bundle")
    magic, ver, count, idx_off, data_off, data_bytes, idx_fnv, hdr_fnv = struct.unpack_from("<8I", raw, 0)
    if magic != 0x31424c49: raise SystemExit(f"{path}: not an ILB1 bundle")
    if ver != 1: probs.append(f"version {ver} (this check knows 1)")
    if fnv32(raw[:28]) != hdr_fnv: probs.append("the header's checksum does not match (damaged)")
    idx = raw[idx_off:idx_off + 64 * count]
    if len(idx) != 64 * count or fnv32(idx) != idx_fnv: probs.append("the index's checksum does not match (damaged)")
    if data_off + data_bytes != len(raw): probs.append(f"the file is {len(raw)} B, the header says {data_off + data_bytes}")
    ents = []
    for i in range(count):
        e = idx[64 * i:64 * i + 64]
        if len(e) < 64: break
        name = e[:48].split(b"\x00", 1)[0].decode("utf-8", "replace")
        off, size, fnv, fl = struct.unpack_from("<4I", e, 48)
        if fl & 1: ILZ.add(name)   # kit #10 integration (boot x aot): the entry holds the compressed-IL form (managed-ilz/<name>.dll)
        blob = raw[data_off + off:data_off + off + size]
        if len(blob) != size: probs.append(f"{name}: its bytes run past the file's end")
        elif fnv32(blob) != fnv: probs.append(f"{name}: its bytes do not match its index checksum (damaged)")
        ents.append((name, off, size, fnv))
    return ents, probs, len(raw)

def read_order(path):
    """The card DLLs a start loaded, in load order (Shepherd.* excluded: the build's own, never bundled) + every assembly's location."""
    card, every = [], []
    for line in open(path, encoding="utf-8", errors="replace"):
        if line.startswith("#"): continue
        f = line.rstrip("\n").split("\t")
        if len(f) < 4: continue
        every.append(f[3])
        if not f[3].startswith(GAME_PREFIX): continue
        fn = f[3][len(GAME_PREFIX):]
        if not fn.endswith(".dll") or "/" in fn: continue
        n = fn[:-4]
        if n.startswith("Shepherd.") or n in card: continue
        card.append(n)
    return card, every

def inversions(seq):
    """How many pairs are out of order (O(n^2) is fine for ~100 DLLs) and the first DLL that comes after a later-placed one."""
    inv, first = 0, None
    for i in range(len(seq)):
        for j in range(i + 1, len(seq)):
            if seq[i][1] > seq[j][1]:
                inv += 1
                if first is None: first = (seq[j][0], seq[i][0])
    return inv, first

def check_ilb(a):
    ents, probs, fbytes = read_bundle(a.bundle)
    pos = {n: i for i, (n, _, _, _) in enumerate(ents)}
    size = {n: s for (n, _, s, _) in ents}
    bad = bool(probs)
    print(f"bundle {a.bundle}: {len(ents)} DLLs, {sum(size.values())} B of DLLs, file {fbytes} B")
    for p in probs: print(f"  DAMAGED  {p}")
    if a.managed:
        stale = []
        on_card = {f[:-4] for f in os.listdir(a.managed) if f.endswith(".dll")}
        ilzdir = os.path.join(os.path.dirname(os.path.abspath(a.managed)), "managed-ilz")
        for (n, _, s, fnv) in ents:
            p = os.path.join(a.managed, n + ".dll")
            if n not in on_card: stale.append(f"{n}: not in the card's managed folder any more"); continue
            if n in ILZ:   # kit #10 integration: a compressed-IL entry is compared with the card's managed-ilz/ file (the host's verify does the same)
                p = os.path.join(ilzdir, n + ".dll")
                if not os.path.isfile(p): stale.append(f"{n}: a compressed-IL entry, but {ilzdir} has no {n}.dll"); continue
            st = os.stat(p)
            if st.st_size != s: stale.append(f"{n}: {st.st_size} B on the card, {s} B in the bundle"); continue
            if fnv32(open(p, "rb").read()) != fnv: stale.append(f"{n}: the card's bytes differ (same size)")
        print(f"  fresh    {len(ents) - len(stale)} of {len(ents)} bundled DLLs = the card's own files ({a.managed}" + (f"; {len(ILZ)} compressed-IL entries vs {ilzdir}" if ILZ else "") + ")"
              + ("" if not stale else " -- STALE: the host's verify start will fail and every DLL comes from its own file until the bundle is re-made"))
        for s in stale: print(f"  STALE    {s}")
        bad |= bool(stale)
    for o in a.order:
        card, every = read_order(o)
        spill = [n for n in card if n not in pos]
        inb = [(n, pos[n]) for n in card if n in pos]
        unused = [n for (n, _, _, _) in ents if n not in set(card)]
        inv, first = inversions(inb)
        boot_bytes = sum(size[n] for n, _ in inb)
        spill_bytes = 0
        if a.managed:
            for n in spill:
                p = os.path.join(a.managed, n + ".dll")
                if os.path.isfile(p): spill_bytes += os.path.getsize(p)
        # the contiguous boot region: the loaded DLLs all before the first unused one in the bundle
        last_used = max((p for _, p in inb), default=-1)
        holes = [n for (n, _, _, _) in ents[:last_used + 1] if n not in set(card)]
        print(f"order {o}: {len(every)} assemblies loaded, {len(card)} from the card's managed/ folder (Shepherd.* excluded)")
        print(f"  bundled  {len(inb)} of {len(card)} card DLLs ({boot_bytes} B in one stream)")
        print(f"  spill    {len(spill)}" + (f" -- read from their own files on the boot thread ({spill_bytes} B" + ("" if a.managed else ", sizes need --managed") + "): " + " ".join(spill) if spill else " (every card DLL the start loaded is in the bundle)"))
        if inv: print(f"  order    NOT the bundle's order: {inv} inversion(s); first: {first[0]} was asked for after {first[1]}, which the bundle places after it")
        else: print(f"  order    the bundle's order ({len(inb)} DLLs asked for in the order the stream reads them)")
        print(f"  unused   {len(unused)}" + (f" ({sum(size[n] for n in unused)} B read and kept for nothing in this start): " + " ".join(unused) if unused else "") + (f"; {len(holes)} of them inside the boot region (before the last DLL it used)" if holes else ""))
        # kit #10 review fix (finding 12): bundled DLLs a start never asked for are read up front and keep their IL pool / newlib memory for
        # the whole run (k10_ilb_end: "their memory stays") -- memory the walk's arena budget cannot spare: LOOK, not CLEAN
        bad |= bool(spill) or bool(inv) or bool(unused)
    print("RESULT: " + ("LOOK -- see SPILL / ORDER / UNUSED / STALE / DAMAGED above" if bad else "CLEAN -- every recorded boot DLL comes from the one stream, in order, and every bundled DLL is used"))
    return 1 if bad else 0

# ------------------------------------------------------------------------------------------------------------- the R5 hot prefix

NM = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-nm")
JSTART = re.compile(r"^mono_aot_(.+)jit_code_start$")
JEND = re.compile(r"^mono_aot_(.+)jit_code_end$")
LSTART = re.compile(r"^mono_aot_(.+)_icall_cold_wrapper_\d+$")

def read_image(path):
    """The image's code symbols [(addr, size, name)] in address order (t / T / w / W; aliases at one address kept once) and the AOT
    modules' code regions [(kind, module, first index, end index)] into that list: 'mono' = the Mono-backend methods between
    mono_aot_<M>jit_code_start / _end; 'llvm' = the LLVM methods from mono_aot_<M>_icall_cold_wrapper_* (where the compiler's
    llvm_code_start sits: the ranked methods follow it) for as long as the symbols are the module's own (<M>_... / mono_aot_<M>_...)."""
    nm = NM if os.path.exists(NM) else "arm-vita-eabi-nm"
    try: out = subprocess.run([nm, "-n", "-S", "--defined-only", path], capture_output=True, text=True, check=True).stdout
    except (OSError, subprocess.CalledProcessError) as e: raise SystemExit(f"{path}: nm failed ({e})")
    syms, seen = [], set()
    for line in out.splitlines():
        p = line.split()
        if len(p) == 4: addr, sz, t, name = int(p[0], 16), int(p[1], 16), p[2], p[3]
        elif len(p) == 3: addr, sz, t, name = int(p[0], 16), 0, p[1], p[2]
        else: continue
        if t not in "tTwW": continue
        addr &= ~1
        if (addr, name) in seen: continue
        seen.add((addr, name)); syms.append((addr, sz, name))
    syms.sort(key=lambda s: (s[0], s[2]))
    regions, jlo, anon = [], {}, None
    named_at = {syms[i][0] for i in range(len(syms)) if JSTART.match(syms[i][2])}
    for i, (addr, sz, name) in enumerate(syms):
        # the local jit_code_start / _end labels: a region of their own only where no mono_aot_<M>jit_code_start names it (the game-AOT
        # images of the non-LLVM runtime carry only these); named after the first method in it
        if name == "jit_code_start" and addr not in named_at: anon = i; continue
        if name == "jit_code_end" and anon is not None:
            first = next((s[2] for s in syms[anon + 1:i] if not s[2].startswith(("mono_aot_", "plt_", "jit_code"))), "?")
            regions.append(("mono", "@" + format(syms[anon][0], "x") + " " + first[:40], anon + 1, i)); anon = None; continue
        m = JSTART.match(name)
        if m: jlo[m.group(1)] = i; continue
        m = JEND.match(name)
        if m and m.group(1) in jlo: regions.append(("mono", m.group(1), jlo.pop(m.group(1)) + 1, i)); continue
        m = LSTART.match(name)
        if m:
            mod = m.group(1); own, mk = mod + "_", "mono_aot_" + mod + "_"
            j = i + 1
            while j < len(syms) and (syms[j][2].startswith(own) or syms[j][2].startswith(mk)): j += 1
            regions.append(("llvm", mod, i + 1, j))
    return syms, regions

def check_prefix(a):
    rank = []
    for line in open(a.order, encoding="utf-8", errors="replace"):
        s = line.strip()
        if s and not s.startswith("#") and s not in rank: rank.append(s)
    rk = {n: i for i, n in enumerate(rank)}
    print(f"list {a.order}: {len(rank)} names, hottest first")
    rc = 0
    per_image = []
    for img in a.images:
        syms, regions = read_image(img)
        names = {s[2] for s in syms}
        where = {}                                   # list name -> (kind, module)
        lead = collections.Counter(); lead_b = collections.Counter(); inv = collections.Counter(); listed = collections.Counter()
        spilled = collections.defaultdict(list)
        for kind, mod, i0, i1 in regions:
            key = (kind, mod)
            inrun, last, done = True, -1, set()
            for addr, sz, name in syms[i0:i1]:
                if name.startswith(("mono_aot_", "plt_", "ut_")) or name.endswith("_veneer"):
                    if name.startswith("mono_aot_") and "init_method" in name: inrun = False   # the ranked block ends at init_method
                    continue
                if name in rk:
                    if name in done: continue
                    done.add(name); where[name] = key; listed[key] += 1
                    if inrun:
                        lead[key] += 1; lead_b[key] += sz
                        if rk[name] < last: inv[key] += 1
                        last = max(last, rk[name])
                    else: spilled[key].append(name)
                elif sz > 0: inrun = False
        cfun, plt, missing = [], [], []
        for n in rank:
            if n in where: continue
            if n.startswith(("plt_", "ut_")): plt.append(n)   # stubs / trampolines: numbered per build, never ordered
            elif n not in names: missing.append(n)
            else: cfun.append(n)
        tot_m, tot_p, tot_s = sum(listed.values()), sum(lead.values()), sum(len(v) for v in spilled.values())
        nmods = len({m for _, m, _, _ in regions})
        print(f"image {img}: {len(syms)} code symbols, {nmods} AOT modules ({sum(1 for r in regions if r[0] == 'llvm')} LLVM regions)")
        print(f"  matched  {tot_m} of {len(rank)} list names are AOT methods here: {tot_p} in their region's leading (ranked) run, {tot_s} SPILLED behind unlisted code")
        for key in sorted(listed, key=lambda k: -listed[k]):
            kind, mod = key
            print(f"    {mod + ' (' + kind + ')':46s} {listed[key]:5d} listed, prefix {lead[key]:5d} ({lead_b[key]} B), spilled {len(spilled[key]):4d}, rank inversions in the prefix {inv[key]}")
            for n in spilled[key][:a.show]: print(f"      spilled  {n}")
        print(f"  C / PLT  {len(cfun)} list names are runtime C functions, {len(plt)} PLT stubs / unbox trampolines (the compiler orders neither: expected)")
        print(f"  MISSING  {len(missing)} list names are not in this image (renamed / removed since the list was made, or vlx-only: out of the hot prefix)")
        for n in missing[:a.show]: print(f"    gone   {n}")
        if len(missing) > a.show: print(f"    ... {len(missing) - a.show} more (--show N)")
        per_image.append((img, set(missing), tot_s, tot_m))
        if tot_s or len(missing) > a.allow_missing: rc = 1
    if len(per_image) == 2:
        (i0, m0, s0, t0), (i1, m1, s1, t1) = per_image
        lost, won = sorted(m1 - m0), sorted(m0 - m1)
        print(f"between the two images: matched {t0} -> {t1}; spilled {s0} -> {s1}; {len(lost)} list names found in the first and missing in the second, {len(won)} the other way")
        for n in lost[:a.show]: print(f"    lost   {n}")
        for n in won[:a.show]: print(f"    found  {n}")
    print("RESULT: " + ("LOOK -- methods spilled out of the prefix or list names missing beyond --allow-missing (see above)" if rc else "CLEAN -- every matched method sits in its region's prefix, missing names within the allowance"))
    return rc

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("ilb"); p.add_argument("bundle"); p.add_argument("order", nargs="+"); p.add_argument("--managed")
    q = sub.add_parser("prefix"); q.add_argument("order"); q.add_argument("images", nargs="+")
    q.add_argument("--allow-missing", type=int, default=0); q.add_argument("--show", type=int, default=20)
    a = ap.parse_args()
    try: return check_ilb(a) if a.cmd == "ilb" else check_prefix(a)
    except (OSError, ValueError, struct.error) as e:
        print(f"unreadable input: {e}", file=sys.stderr); return 2

if __name__ == "__main__":
    sys.exit(main())
