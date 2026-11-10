#!/usr/bin/env python3
"""SHEPHERD-VITA V6 Stage B: post-build checks of shepherd_menu.vpk (run by build.sh; exit 1 on any violation).
  1. param.sfo: ATTRIBUTE2 == 12 (the extended ~365 MiB main-memory budget) and the TITLE_ID (--title-id: must match).
  2. imports: every function the eboot imports (the ELF's .vitalink.fstubs.<library> stub symbols), by library; nothing
     forbidden: no VM domains / ForVM memory (run-time code), no kubridge / taiHEN, no exception or abort handler
     registration, no network library, no kernel-only (ForDriver/ForKernel) library; with --allow-libs, every imported
     library must be on that list (Stage B: the headless and the GL eboot each have their exact list in build.sh).
     --allow-shacccg-ext (the GL eboot only): vitaShaRK's SceShaccCgExt (linked by vitaGL's shader-compiler bring-up,
     shark_init) hooks Sony's libshacccg.suprx module in user space through taiHEN; exactly its five taiHEN user imports are
     then accepted, and only if the link map shows libSceShaccCgExt.a is what references them. Every other taiHEN import,
     kubridge, ForVM/VM domains and exception handlers stay forbidden.
     --allow-forvm (kit #8: a JIT app only, build.sh SHEPHERD_JIT_APP=1): exactly the five SceSysmem code-memory functions of HENkaku's
     ForVM API (sceKernelAllocMemBlockForVM / FreeMemBlockForVM / OpenVMDomain / CloseVMDomain / SyncVMDomain) are accepted, and only if
     the link map shows the baseline JIT tier (the runtime's shepjit.c.obj) or the JIT host code (host.o, k8_jit_host.inc.c) is what
     references each. Every other ForVM / VM-domain / kubridge / handler import stays forbidden.
  3. VPK contents: only eboot.bin, sce_sys/{param.sfo,icon0.png} and managed/*.dll; every DLL byte-identical to the
     linux-arm 8.0.31 pack (BCL) or the engine build output (Shepherd.*, Microsoft.Bcl.AsyncInterfaces); no file equal to
     any file of the forbidden trees (the game's Managed, runtime/ManagedVita, the pack) and no pack/icall/JSON file.
     3b. (--derived-src, review 2026-10-07) no derived code compiled INTO a packaged DLL: the derived sources' own identifiers and
     string literals (engine/**/Derived/: ports of decompiled game / third-party code) must not occur in any managed/*.dll.
     fid1-fixup D1 (2026-10-07): also the derived SHADERS (Derived/**/*.{frag,vert,glsl}, ports of the game's / third-party compiled
     shaders): their code lines (comments removed, >= 24 chars, not in any non-derived shader) searched as UTF-8 (an embedded resource)
     and UTF-16 (a C# string), and their file names as manifest-resource names ("Shaders.<file>", UTF-8). Always (with or without
     --derived-src): no packaged DLL is, or references, the owner-local Shepherd.Engine.Derived (its file name, or the UTF-8 name in any
     DLL's metadata). --owner-derived (the derived-AOT build only, never shareable): the eboot links Shepherd.Engine.Derived's AOT image;
     without it, an ELF with that image fails.
     fid1-fixup round 2 (the hygiene verifier): the markers 3b leaves out because they also occur in a non-derived source are no longer
     just dropped: derived_leaks.py runs on the --derived-src tree's checkout and FAILs on derived content in any file git carries
     (tracked or untracked-not-ignored; shader lines, string literals, >= 7-significant-digit numbers, shader file names; generic text
     listed in the owner-local engine/Derived/leak-allow.txt). With no engine/**/Derived on the machine 3b says VACUOUS (--require-derived:
     FAIL). The VPK's eboot.bin must be the fself of --elf (re-made with vita-elf-create + vita-make-fself and compared byte for byte), so
     the --owner-derived / no-derived-image verdict is about the eboot actually packaged; and an owner-only TITLE_ID (the SHPD0009x block:
     SHPD00090/91 the derived-AOT builds, SHPD00092/93 the owner-local gltest / probe) fails unless --owner-derived is given. (The
     derived-AOT builds used SHPD00012/13 until main gave those to PhysBench and the devlog test, 2026-10-07.)
     Device kit #4 (game AOT, build.sh SHEPHERD_AOT_OWNER=1): --owner-game-aot <list> -- the eboot links exactly the listed owner modules'
     AOT images (compiled game / rewritten Unity code), none of their DLLs is in the VPK, and the TITLE_ID is owner-only (00095 / 00190).
     Always: every AOT module the eboot links (its mono_aot_module_<M>_info symbols) is a packaged DLL's, Shepherd.Engine.Derived's (with
     --owner-derived) or a listed owner module's (with --owner-game-aot); anything else (game code compiled into a shareable eboot) FAILs.
  4. the engine's Mac-only native imports that the Vita leaves unanswered (listed; they are never bound under the Vita profile).
  Device kit #8, track boot (vita/notes/kit8-boot.md): --launcher <launcher.elf> --module-yml <yml> (build.sh SHEPHERD_K8_LAUNCHER=1): the
  VPK's eboot.bin is the TINY LAUNCHER (the fself of --launcher) and module/shepherd.suprx the ENGINE MODULE (the fself of --elf made with
  vita-elf-create -e --module-yml); every check above about "the eboot" is about the module; the launcher's own imports are checked
  against the forbidden list and --launcher-allow-libs, and it links no newlib, runtime or AOT image."""
import argparse, hashlib, os, re, struct, subprocess, sys, zipfile

ap = argparse.ArgumentParser()
ap.add_argument("--vpk"); ap.add_argument("--sfo"); ap.add_argument("--elf"); ap.add_argument("--map")
ap.add_argument("--bcl"); ap.add_argument("--pack"); ap.add_argument("--engine"); ap.add_argument("--unanswered")
ap.add_argument("--forbid", action="append", default=[])
ap.add_argument("--title-id"); ap.add_argument("--allow-libs"); ap.add_argument("--allow-shacccg-ext", action="store_true")
ap.add_argument("--allow-forvm", action="store_true")             # kit #8: the JIT app's five ForVM imports (see the docstring)
ap.add_argument("--derived-src", action="append", default=[])   # source trees whose */Derived/*.{cs,frag,vert,glsl} must not be compiled into a packaged DLL
ap.add_argument("--owner-derived", action="store_true")         # the owner-local derived-AOT eboot: its ELF may (must) hold Shepherd.Engine.Derived's AOT image
ap.add_argument("--require-derived", action="store_true")       # an owner build: 3b may not be vacuous (engine/**/Derived must be here)
ap.add_argument("--owner-game-aot")                              # kit #4: a file of owner module names whose AOT images the eboot links (game AOT, owner-only)
ap.add_argument("--no-fself-check", action="store_true")        # skip re-making the fself from --elf (only for a VPK whose ELF is gone)
ap.add_argument("--allow-livearea", action="store_true")   # devfail1 diag VPKs: sce_sys/livearea/contents/{template.xml,bg.png,startup.png} too
ap.add_argument("--launcher"); ap.add_argument("--module-yml"); ap.add_argument("--launcher-allow-libs")   # kit #8: the launcher build
ap.add_argument("--vpk-ilz", action="store_true")   # kit #12 walkb: managed-ilz/*.dll allowed -- each the compressed-IL form of managed/<same name> (k10_ilz.py verify)
ap.add_argument("--module-load-libs")   # kit #8.1: the libraries loaded when the launcher loads the engine module (module-load-libs.txt)
a = ap.parse_args()
bad = []
def ok(cond, msg):
    print(("ok   " if cond else "FAIL ") + msg)
    if not cond: bad.append(msg)

# ---- 1. param.sfo
d = open(a.sfo, "rb").read()
magic, ver, kto, dto, n = struct.unpack_from("<4sIIII", d, 0)
sfo = {}
for i in range(n):
    ko, fmt, ln, mx, do = struct.unpack_from("<HHIII", d, 20 + 16 * i)
    k = d[kto + ko:d.index(b"\0", kto + ko)].decode()
    v = d[dto + do:dto + do + ln]
    sfo[k] = struct.unpack_from("<I", v)[0] if fmt == 0x0404 else v.rstrip(b"\0").decode(errors="replace")
ok(magic == b"\0PSF" and sfo.get("ATTRIBUTE2") == 12 and (not a.title_id or sfo.get("TITLE_ID") == a.title_id),
   f"param.sfo ATTRIBUTE2={sfo.get('ATTRIBUTE2')} (want 12), TITLE_ID={sfo.get('TITLE_ID')}" + (f" (want {a.title_id})" if a.title_id else "") + f", TITLE='{sfo.get('TITLE')}'")
# owner-only title IDs, the SHPD0009x block (derived code inside: build.sh SHEPHERD_AOT_DERIVED=1, gltest / probe with GLTEST_OWNER_LOCAL=1 /
# PROBE_OWNER_LOCAL=1): only with --owner-derived
OWNER_ONLY_IDS = {"SHPD00090": "derived-AOT menu", "SHPD00091": "derived-AOT GL menu", "SHPD00092": "owner-local gltest", "SHPD00093": "owner-local probe",
                  "SHPD00096": "derived-AOT GL menu, MT variant", "SHPD00097": "the play app (derived-AOT GL menu + audio)",
                  "SHPD00098": "the play app, MT variant", "SHPD00099": "derived-AOT GL menu, measurement variant",
                  "SHPD00095": "the play app with the owner-local game AOT", "SHPD00190": "the headless menu with the owner-local game AOT",
                  "SHPD00191": "the measurement app, LLVM (kit #5 A/B)", "SHPD00192": "the play app MT variant, LLVM (kit #5 A/B)",
                  "SHPD00193": "the play app MT variant as launcher + engine module (kit #8 A/B)",
                  "SHPD00194": "the play app with the owner-local game AOT as launcher + engine module (kit #8 A/B)"}
tid = sfo.get("TITLE_ID")
ok(tid not in OWNER_ONLY_IDS or a.owner_derived, f"TITLE_ID {tid}: " + (f"an OWNER-ONLY id ({OWNER_ONLY_IDS[tid]}): " + ("accepted with --owner-derived" if a.owner_derived else "refused without --owner-derived (a VPK with derived code is never shareable)") if tid in OWNER_ONLY_IDS else "not an owner-only id"))

# ---- 2. imports
def elf_imports(elf):
    secs = {}
    out = subprocess.run(["arm-vita-eabi-readelf", "-SW", elf], capture_output=True, text=True).stdout
    for m in re.finditer(r"^\s*\[\s*(\d+)\]\s+(\S+)", out, re.M): secs[int(m.group(1))] = m.group(2)
    syms = subprocess.run(["arm-vita-eabi-readelf", "-sW", elf], capture_output=True, text=True).stdout
    imp = {}
    for line in syms.splitlines():
        f = line.split()
        if len(f) < 8 or not f[6].isdigit(): continue
        sec = secs.get(int(f[6]), "")
        if sec.startswith(".vitalink.fstubs.") and f[3] in ("FUNC", "NOTYPE") and not f[7].startswith("$"):
            imp.setdefault(sec[len(".vitalink.fstubs."):], set()).add(f[7])
        if sec.startswith(".vitalink.vstubs."):
            imp.setdefault(sec[len(".vitalink.vstubs."):] + " (variables)", set()).add(f[7])
    return imp
imports = elf_imports(a.elf)
nf = sum(len(v) for v in imports.values())
ok(nf > 0, f"imports: {nf} functions from {len(imports)} libraries")
for lib in sorted(imports): print(f"       {lib:28s} {len(imports[lib]):3d}: {' '.join(sorted(imports[lib]))}")
forbidden = re.compile(r"ForVM|VMDomain|kubridge|^ku[A-Z]|taiHook|taiInject|ExceptionHandler|AbortHandler|SetUserException|ForDriver|ForKernel|SceHttp|SceSsl|SceNetCtl")
# newlib's fd layer (lib_a-syscalls.o: read/write/close/[gs]etsockopt on a socket fd) keeps these five SceNet stubs; they
# are unreachable because nothing can create a socket fd: sceNetSocket / sceNetInit / connect / the resolver are NOT imported
NEWLIB_NET = {"sceNetGetsockopt", "sceNetRecv", "sceNetSend", "sceNetSetsockopt", "sceNetSocketClose"}
SHACCCG_EXT_TAI = {"taiGetModuleInfo", "taiHookFunctionOffsetForUser", "taiHookRelease", "taiInjectDataForUser", "taiInjectRelease"}
ext_ok = set()
if a.allow_shacccg_ext:
    mp = open(a.map, errors="replace").read() if a.map and os.path.exists(a.map) else ""
    # the map's archive-member section: "<lib>(<member>)\n<spaces><who pulled it> (<symbol>)" -- every taiHEN symbol must be wanted by SceShaccCgExt
    wanted_by = {sym: who for who, sym in re.findall(r"\n\s+(\S+)\s+\((tai\w+)\)", mp)}
    for fn in imports.get("taihen", set()):
        if fn in SHACCCG_EXT_TAI and "libSceShaccCgExt.a" in wanted_by.get(fn, ""): ext_ok.add(fn)
    print(f"       taiHEN imports accepted for vitaShaRK's SceShaccCgExt (user-space hooks on libshacccg.suprx): {' '.join(sorted(ext_ok)) or 'none'}")
FORVM = {"sceKernelAllocMemBlockForVM", "sceKernelFreeMemBlockForVM", "sceKernelOpenVMDomain", "sceKernelCloseVMDomain", "sceKernelSyncVMDomain"}
forvm_ok = set()
if a.allow_forvm:
    mp = open(a.map, errors="replace").read() if a.map and os.path.exists(a.map) else ""
    wanted_by = {sym: who for who, sym in re.findall(r"\n\s+(\S+)\s+\((sceKernel\w*(?:ForVM|VMDomain))\)", mp)}
    for fn in imports.get("SceSysmem", set()):
        if fn in FORVM and ("shepjit.c.obj" in wanted_by.get(fn, "") or wanted_by.get(fn, "").endswith("host.o")): forvm_ok.add(fn)
    print(f"       ForVM imports accepted for the baseline JIT tier (kit #8, wanted by the runtime's shepjit.c.obj / host.o): {' '.join(sorted(forvm_ok)) or 'none'}")
hits = sorted({f"{lib}!{fn}" for lib, fns in imports.items() for fn in fns
               if (forbidden.search(fn) or forbidden.search(lib) or (lib.startswith("SceNet") and fn not in NEWLIB_NET)) and not (lib == "taihen" and fn in ext_ok)
               and not (lib == "SceSysmem" and fn in forvm_ok)})
ok(not hits, "no forbidden import (VM domains / ForVM / kubridge / taiHEN / exception handlers / kernel / socket creation, DNS, HTTP, SSL)" + (": " + ", ".join(hits) if hits else ""))
if a.allow_libs:
    allow = set(x.strip() for x in a.allow_libs.split(",") if x.strip())
    extra = sorted(lib for lib in imports if lib.split(" ")[0] not in allow)
    ok(not extra, f"every imported library is on the allow-list ({len(allow)} names)" + (": NOT ALLOWED " + ", ".join(extra) if extra else ""))
net = sorted(imports.get("SceNet", set()))
ok(set(net) <= NEWLIB_NET, f"SceNet: only newlib's fd-layer stubs ({' '.join(net) or 'none'}); sceNetSocket/sceNetInit/connect/resolver absent")
if a.module_load_libs:   # kit #8.1: the engine module may import only what is loaded when the launcher loads it (else 0x8002D003 on the Vita)
    loaded = set(l.strip() for l in open(a.module_load_libs) if l.strip() and not l.lstrip().startswith("#"))
    late = sorted(lib for lib in imports if lib.split(" ")[0] not in loaded)
    ok(not late, f"the engine module imports only libraries loaded before it ({len(loaded)} names, {os.path.basename(a.module_load_libs)})"
       + (": NOT LOADED YET " + ", ".join(f"{l} ({' '.join(sorted(imports[l]))})" for l in late) + " -> the Vita refuses the module with 0x8002D003 (MODULEMGR_NO_LIB)" if late else ""))
if a.launcher:   # kit #8: the launcher eboot's own imports
    limp = elf_imports(a.launcher)
    print(f"       launcher ({os.path.basename(a.launcher)}) imports: {sum(len(v) for v in limp.values())} functions from {len(limp)} libraries")
    for lib in sorted(limp): print(f"         {lib:26s} {len(limp[lib]):3d}: {' '.join(sorted(limp[lib]))}")
    lhits = sorted({f"{lib}!{fn}" for lib, fns in limp.items() for fn in fns if forbidden.search(fn) or forbidden.search(lib) or lib.startswith("SceNet") or lib == "taihen"})
    ok(not lhits, "the launcher has no forbidden import (and no network, no taiHEN)" + (": " + ", ".join(lhits) if lhits else ""))
    if a.launcher_allow_libs:
        lallow = set(x.strip() for x in a.launcher_allow_libs.split(",") if x.strip())
        lextra = sorted(lib for lib in limp if lib.split(" ")[0] not in lallow)
        ok(not lextra, f"every library the launcher imports is on its allow-list ({len(lallow)} names)" + (": NOT ALLOWED " + ", ".join(lextra) if lextra else ""))
    lnm = subprocess.run(["arm-vita-eabi-nm", a.launcher], capture_output=True, text=True).stdout
    lbad = sorted(set(re.findall(r" [TtDdBbRr] (malloc|_newlib_heap_size_user|_init_vita_newlib|mono_\w+|_init_vita_reent)$", lnm, re.M)))
    ok(not lbad, "the launcher links no newlib, runtime or AOT image (its main thread's TLS slot stays the module's newlib's)" + (": " + " ".join(lbad) if lbad else ""))
maplibs = sorted(set(re.findall(r"lib(Sce\w+?)_stub\.a\(", open(a.map, errors="replace").read()))) if a.map and os.path.exists(a.map) else []
print(f"       stub archives with members pulled into the link (before --gc-sections): {' '.join(maplibs)}")

# ---- 3. VPK contents
def sha(b): return hashlib.sha256(b).hexdigest()
def tree_hashes(root):
    h = {}
    for dp, _, fs in os.walk(root):
        for f in fs:
            p = os.path.join(dp, f)
            try:
                if os.path.getsize(p) > 0: h[sha(open(p, "rb").read())] = p
            except OSError: pass
    return h
provenance = {}
for root in (os.path.join(a.pack, "lib", "net8.0"), os.path.join(a.pack, "native"), a.engine):
    for f in os.listdir(root):
        if f.endswith(".dll"): provenance[sha(open(os.path.join(root, f), "rb").read())] = os.path.join(root, f)
forbid = {}
for r in a.forbid: forbid.update(tree_hashes(r))
z = zipfile.ZipFile(a.vpk)
names = z.namelist()
allowed = re.compile(r"^(eboot\.bin|sce_sys/param\.sfo|sce_sys/icon0\.png|managed/[A-Za-z0-9_.\-]+\.dll" + (r"|module/shepherd\.suprx" if a.launcher else "") +
                     (r"|managed-ilz/[A-Za-z0-9_.\-]+\.dll" if a.vpk_ilz else "") +
                     (r"|sce_sys/livearea/contents/(template\.xml|bg\.png|startup\.png)" if a.allow_livearea else "") +
                     r"|version-history\.txt" + r")$")   # kit #12 (menusb): Settings > PS Vita > Version History (our own text, checked below)
strays = [x for x in names if not allowed.match(x)]
ok(not strays, f"VPK holds only eboot.bin, sce_sys/, managed/*.dll and version-history.txt ({len(names)} entries)" + (f": strays {strays}" if strays else ""))
if "version-history.txt" in names:   # kit #12: Shepherd's own version history (vita/tools/k12_history.py) -- nothing else may ride in under that name
    vh = z.read("version-history.txt").decode("utf-8", "replace").splitlines()
    ok(len(vh) > 1 and vh[0].startswith("# Shepherd version history") and all(l.startswith(("#", "@ ")) for l in vh if l) and len(vh) < 200,
       f"version-history.txt is Shepherd's own version history ({len(vh)} lines, made by vita/tools/k12_history.py)")
dlls = [x for x in names if x.startswith("managed/")]
unknown, derived = [], []
total = 0
for x in dlls:
    b = z.read(x); h = sha(b); total += len(b)
    if h not in provenance: unknown.append(x)
    if h in forbid: derived.append(f"{x} == {forbid[h]}")
ok(not unknown, f"all {len(dlls)} managed DLLs ({total} bytes) are byte-identical to the linux-arm 8.0.31 pack or the engine build" + (f": unknown {unknown}" if unknown else ""))
# kit #12 walkb (--vpk-ilz): every managed-ilz/<name>.dll is the compressed-IL form of the VPK's own managed/<name>.dll -- k10_ilz.py's verify
# (every method body identical, the metadata streams identical except the RVA columns, the MVID, field data, resources, the header's record of
# the original); so its provenance and the derived-code scan below are the original's
ilzs = [x for x in names if x.startswith("managed-ilz/")]
if a.vpk_ilz or ilzs:
    sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tools"))
    import k10_ilz
    ilz_bad = []
    for x in ilzs:
        orig = "managed/" + os.path.basename(x)
        if orig not in names: ilz_bad.append(f"{x}: no {orig}"); continue
        # kit #12 fix + kit (review finding): a local name -- this block is module-level code, and rebinding the global `bad` here threw away
        # every FAIL recorded before it (param.sfo, strays, provenance ...) once the last pair verified clean
        try: vbad, _n = k10_ilz.verify(z.read(orig), z.read(x))
        except Exception as e: vbad = [f"unreadable ({e})"]
        if vbad: ilz_bad.append(f"{x}: {'; '.join(vbad)}")
    ok(a.vpk_ilz and ilzs and not ilz_bad, f"managed-ilz/: {len(ilzs)} DLL(s), each the verified compressed-IL form of its managed/ original (kit #12 vpkilz)"
       + (" -- none packaged" if not ilzs else "") + (" -- packaged without --vpk-ilz" if not a.vpk_ilz else "") + (f": {ilz_bad[:5]}" if ilz_bad else ""))
ok(not derived, f"no VPK file equals a file of the game's Managed / runtime/ManagedVita / the pack ({len(forbid)} files compared)" + (": " + "; ".join(derived) if derived else ""))
eng = sorted(x for x in dlls if os.path.basename(x)[:-4] in ("Shepherd.Engine", "Shepherd.Assets", "Shepherd.Hooks", "Microsoft.Bcl.AsyncInterfaces"))
print(f"       engine assemblies in the VPK: {' '.join(eng)}")
# the owner-local derived assembly is never packaged, and nothing packaged references it (fid1-fixup D1): by file name, and by its name in
# metadata (#Strings is UTF-8: an assembly / module named so, or an AssemblyRef to it; the engine names it only in UTF-16 user strings)
named = [x for x in names if os.path.basename(x).lower() == "shepherd.engine.derived.dll"]
refd = [x for x in dlls if b"Shepherd.Engine.Derived" in z.read(x)]
ok(not named and not refd, "no owner-local Shepherd.Engine.Derived.dll in the VPK and no packaged DLL named or referencing Shepherd.Engine.Derived"
   + (f": {' '.join(sorted(set(named + refd)))}" if named or refd else ""))
# the eboot in the VPK is the fself of --elf (fid1-fixup round 2): the nm verdict below is about the packaged eboot, not just any ELF
def fself_of(elf, entry, what, yml=None):
    import tempfile
    with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as td:
        velf, fself = os.path.join(td, "e.velf"), os.path.join(td, "e.self")
        r1 = subprocess.run(["vita-elf-create", "-v"] + (["-e", yml] if yml else []) + [elf, velf], capture_output=True)
        r2 = subprocess.run(["vita-make-fself", "-c", velf, fself], capture_output=True) if r1.returncode == 0 else r1
        same = r2.returncode == 0 and entry in names and open(fself, "rb").read() == z.read(entry)
        ok(same, f"the VPK's {entry} is the fself of {what} (re-made with vita-elf-create + vita-make-fself, byte-identical)"
           + ("" if same else (": tools failed (" + (r2.stderr or b"").decode(errors="replace").strip()[:200] + ")" if r2.returncode else ": it is NOT -- the ELF checks above do not describe this VPK")))
if a.elf and not a.no_fself_check:
    if a.launcher:   # kit #8: the engine module + the launcher eboot
        fself_of(a.elf, "module/shepherd.suprx", "--elf (the engine module, -e --module-yml)", a.module_yml)
        fself_of(a.launcher, "eboot.bin", "--launcher (the tiny launcher)")
    else:
        fself_of(a.elf, "eboot.bin", "--elf")
# the eboot: Shepherd.Engine.Derived's AOT image only in the owner-local derived-AOT build (build.sh SHEPHERD_AOT_DERIVED=1)
if a.elf:
    aotder = subprocess.run(["arm-vita-eabi-nm", a.elf], capture_output=True, text=True).stdout.count("mono_aot_module_Shepherd_Engine_Derived_info")
    if a.owner_derived:
        ok(aotder > 0, "OWNER-LOCAL derived-AOT eboot: Shepherd.Engine.Derived's AOT image is linked (derived code: this VPK is never installed for anyone but the owner, never shared)")
    else:
        ok(aotder == 0, "the eboot links no Shepherd.Engine.Derived AOT image" + (" -- it does: only an --owner-derived (derived-AOT, owner-local) build may" if aotder else ""))
    # kit #4: every linked AOT module is a packaged DLL's, the derived module's (--owner-derived) or a listed owner module's (--owner-game-aot)
    mangle = lambda n: re.sub(r"[.-]", "_", n)
    linked = set(re.findall(r" [DdRrBb] mono_aot_module_(\w+)_info$", subprocess.run(["arm-vita-eabi-nm", a.elf], capture_output=True, text=True).stdout, re.M))
    packaged = {mangle(os.path.basename(x)[:-4]) for x in dlls}
    owner = [l.strip() for l in open(a.owner_game_aot) if l.strip() and not l.startswith("#")] if a.owner_game_aot else []
    allowed = packaged | ({"Shepherd_Engine_Derived"} if a.owner_derived else set()) | {mangle(m) for m in owner}
    foreign = sorted(linked - allowed)
    ok(not foreign, f"every AOT module the eboot links ({len(linked)}) is a packaged DLL's" + (", the derived module's" if a.owner_derived else "")
       + (f" or one of the {len(owner)} listed owner game modules'" if owner else "") + (f": NOT {' '.join(foreign)}" if foreign else ""))
    if a.owner_game_aot:
        missing = sorted(m for m in owner if mangle(m) not in linked)
        inpkg = sorted(m for m in owner if mangle(m) in packaged)
        ok(owner and not missing and not inpkg, f"OWNER-LOCAL game AOT eboot: the {len(owner)} owner modules' AOT images are linked and none of their DLLs is in the VPK"
           + (f": missing {' '.join(missing)}" if missing else "") + (f"; packaged {' '.join(inpkg)}" if inpkg else "") + " (compiled game code: never shared)")
        ok(tid in ("SHPD00095", "SHPD00190", "SHPD00194"), f"game-AOT eboot under an owner-only game-AOT TITLE_ID (SHPD00095 / SHPD00190 / SHPD00194): {tid}")

# ---- 3b. derived code inside a packaged DLL (review 2026-10-07: the file-hash checks above cannot see code COMPILED IN; a partial
# method from engine/**/Derived/ once put the Febucci replay inside every VPK's Shepherd.Engine.dll). Markers = what the derived
# sources (every .cs under a Derived/ directory of each --derived-src tree; bin/obj skipped) name that nothing else names: their
# identifiers (>= 6 chars, comments and strings removed) that occur in no non-derived source of the same trees and in no packaged
# BCL DLL (so a hit can only be compiled-in derived code: metadata names, ASCII), and their string-literal fragments (>= 10 chars,
# interpolation holes split out) that occur in no non-derived source (ldstr user strings, UTF-16LE). Every managed/*.dll is searched.
# fid1-fixup D1: an identifier marker must match a whole metadata name (the #Strings heap's NUL-terminated entries: a type, method,
# field or parameter name), not a substring of a longer one ("LitShading" inside the engine's own "ILitShading" is not derived code).
if a.derived_src:
    def cs_files(root):
        for dp, dn, fs in os.walk(root):
            dn[:] = [x for x in dn if x not in ("bin", "obj")]
            for f in fs:
                if f.endswith(".cs"):
                    p = os.path.join(dp, f)
                    yield p, ("Derived" in os.path.relpath(p, root).split(os.sep)[:-1])
    STR = re.compile(r'@?\$?"((?:[^"\\\n]|\\.)*)"')
    def strip(text):   # code without comments and string literals
        text = re.sub(r"//[^\n]*|/\*.*?\*/", " ", text, flags=re.S)
        return STR.sub(" ", text)
    def literals(text):
        text = re.sub(r"//[^\n]*|/\*.*?\*/", " ", text, flags=re.S)
        out = set()
        for m in STR.finditer(text):
            for frag in re.split(r"\{[^{}]*\}", m.group(1)):
                frag = frag.replace('\\"', '"').replace("\\\\", "\\")
                if len(frag) >= 10 and "\\" not in frag: out.add(frag)
        return out
    IDENT = re.compile(r"\b[A-Za-z_][A-Za-z0-9_]{5,}\b")
    SHADER_EXT = (".frag", ".vert", ".glsl")
    def shader_files(root):
        for dp, dn, fs in os.walk(root):
            dn[:] = [x for x in dn if x not in ("bin", "obj")]
            for f in fs:
                if f.endswith(SHADER_EXT):
                    p = os.path.join(dp, f)
                    yield p, ("Derived" in os.path.relpath(p, root).split(os.sep)[:-1])
    def code_lines(text):   # statements as they sit in a shader file (an embedded resource keeps them verbatim)
        return {l.strip() for l in re.sub(r"/\*.*?\*/", " ", re.sub(r"//[^\n]*", "", text), flags=re.S).splitlines() if len(l.strip()) >= 24}
    d_sh, o_sh, d_shnames, o_shnames, nshaders = set(), [], set(), set(), 0
    for root in a.derived_src:
        for p, is_derived in shader_files(root):
            t = open(p, errors="replace").read()
            if is_derived:
                nshaders += 1; d_sh |= code_lines(t)
                if ".essl100." not in os.path.basename(p): d_shnames.add(os.path.basename(p))   # (the essl100 files are dumps, never embedded)
            else: o_sh.append(t); o_shnames.add(os.path.basename(p))
    o_sh_text = "\n".join(o_sh)
    sh_markers = sorted(l for l in d_sh if l not in o_sh_text)
    res_markers = sorted("Shaders." + n for n in d_shnames if n not in o_shnames)
    d_ids, d_lits, o_text, nfiles = set(), set(), [], 0
    for root in a.derived_src:
        for p, is_derived in cs_files(root):
            t = open(p, errors="replace").read()
            if is_derived:
                nfiles += 1; d_ids |= set(IDENT.findall(strip(t))); d_lits |= literals(t)
            else:
                o_text.append(t)
    other = "\n".join(o_text)
    other_ids = set(IDENT.findall(other))
    bcl_blob = b"".join(z.read(x) for x in dlls if os.path.basename(x)[:-4] not in ("Shepherd.Engine", "Shepherd.Assets", "Shepherd.Hooks"))
    id_markers = sorted(i for i in d_ids - other_ids if i.encode() not in bcl_blob)
    lit_markers = sorted(l for l in d_lits if l not in other)
    hits = {}
    for x in dlls:
        b = z.read(x)
        h = [i for i in id_markers if b"\0" + i.encode() + b"\0" in b] + [f'"{l}"' for l in lit_markers if l.encode("utf-16-le") in b]
        h += [f"<{l}>" for l in sh_markers if l.encode() in b or l.encode("utf-16-le") in b] + [f"[resource {r}]" for r in res_markers if r.encode() in b]
        if h: hits[x] = h
    def kinds(h):   # identifiers are bare, literals quoted, shader lines <...>, resource names [resource ...]
        q = chr(34); n = lambda pre: sum(k.startswith(pre) for k in h)
        return f"{len(h) - n(q) - n('<') - n('[')} identifiers, {n(q)} literals, {n('<')} shader lines, {n('[')} resource names"
    if nfiles == 0:
        print(f"note no .cs under a Derived/ directory of {', '.join(a.derived_src)}: no derived markers to look for")
    if nshaders == 0:
        print(f"note no shader under a Derived/ directory of {', '.join(a.derived_src)}: no derived shader markers to look for")
    vacuous = nfiles == 0 and nshaders == 0
    if vacuous:
        ok(not a.require_derived, "3b VACUOUS: no engine/**/Derived on this machine -- nothing to compare the DLLs with (the derived-code gate means "
                                  "something only on the owner's machine)" + (": FAIL, --require-derived (an owner build expects it)" if a.require_derived else ""))
    # fid1-fixup round 2: what the markers leave out because a non-derived source has it too is reported, not dropped: derived content in
    # any file git carries fails here (derived_leaks.py; generic text: the owner-local engine/Derived/leak-allow.txt)
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    sys.dont_write_bytecode = True   # (no __pycache__ in the checkout)
    import derived_leaks
    for root in a.derived_src:
        repo = os.path.dirname(os.path.normpath(root)) if os.path.basename(os.path.normpath(root)) == "engine" else root
        mk, shn, nf, allow = derived_leaks.collect(repo)
        if nf == 0: continue
        files = derived_leaks.git_files(repo)
        lk = derived_leaks.leaks(repo, mk, shn, files)
        over = (len(d_sh) - len(sh_markers), len(d_lits) - len(lit_markers))
        ok(not lk, f"3b: no derived content in the {len(files)} files git carries in {repo} ({len(mk)} markers + {len(shn)} shader file names; "
                   f"{over[0]} shader lines and {over[1]} literals also occur in non-derived sources, so the DLL search leaves them out: "
                   f"each one is a leak unless it is a bare name or listed as generic in engine/Derived/leak-allow.txt ({len(allow)} entries))"
                   + (f": {len(lk)} LEAK(S): " + "; ".join(lk[:10]) + (" ..." if len(lk) > 10 else "") if lk else ""))
    ok(not hits, ("3b VACUOUS -- " if vacuous else "") + f"no derived code compiled into any packaged DLL: {len(id_markers)} identifier + {len(lit_markers)} literal markers from {nfiles} "
                 f"file(s) + {len(sh_markers)} shader-line + {len(res_markers)} resource-name markers from {nshaders} shader file(s) under engine/**/Derived/ "
                 f"(e.g. {', '.join(id_markers[:4] + res_markers[:2])}) searched in {len(dlls)} DLLs"
                 + ("".join(f"; {x}: {' '.join(h[:8])}{' ...' if len(h) > 8 else ''} ({len(h)}: {kinds(h)})" for x, h in sorted(hits.items())) if hits else ""))

# ---- 4. unanswered engine imports
if a.unanswered and os.path.exists(a.unanswered):
    u = [l.strip() for l in open(a.unanswered) if l.strip()]
    print(f"note {len(u)} native imports deliberately unanswered on the Vita (engine/BCL code that the Vita profile never runs): " + "; ".join(u))
print("PASS checks" if not bad else f"FAIL checks ({len(bad)})")
sys.exit(1 if bad else 0)
