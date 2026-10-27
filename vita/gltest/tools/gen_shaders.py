#!/usr/bin/env python3
"""Generate the gltest's embedded shader sources: the exact text GlBackend hands the driver under the essl100 dialect.

Source of truth = engine/Shepherd.Engine/Render/Shaders (read only) through a line-for-line port of GlBackend.Expand and
GlBackend.Prelude("essl100"). The checked-in dump vita/v4/shaders-essl100 (what check-gl.sh guards) is compared too: the
header records whether it agrees, and the build prints STALE when it does not (the engine text is what gets embedded).
Also embeds the gltest's own candidate shaders (src/candidates/*), which are NOT part of GlBackend.
glslangValidator (ES 1.00 front end) must accept every embedded file when it is on PATH.
fid1-fixup D1: the stylizer programs' fragment shader is owner-local (engine/Derived/Shepherd.Engine.Derived/Shaders/stylizer.frag, a
port of a third-party effect; its dumps in engine/Derived/shaders-essl100, its CPU reference engine/Derived/vita-gltest/stylizer_ref.c).
With --owner (fid1-fixup round 2: opt-in, build.sh GLTEST_OWNER_LOCAL=1; both files must be present) the header embeds the 5 programs
and sets GLTEST_HAVE_STYLIZER 1 (the build then holds derived code: the OWNER-ONLY VPK, never shared); otherwise the 3 tracked programs
and GLTEST_HAVE_STYLIZER 0 (the gltest skips its Stylizer checks), whatever this machine has in engine/Derived.

usage: gen_shaders.py <repo> <out.h> [--owner]
"""
import hashlib, os, shutil, subprocess, sys, tempfile

PROGRAMS = [  # GlBackend.Programs: (name, vertex file, fragment file, variant)
    ("sprite", "sprite.vert", "sprite.frag", None),
    ("sprite+SDF", "sprite.vert", "sprite.frag", "SDF"),
    ("copy", "post.vert", "copy.frag", None),
]
OWNER_PROGRAMS = [  # owner-local (engine/Derived): only with the owner-local shader + reference
    ("stylizer", "post.vert", "stylizer.frag", None),
    ("stylizer+TWO_PALETTES", "post.vert", "stylizer.frag", "TWO_PALETTES"),
]


def expand(src, variant):
    """GlBackend.Expand, line for line."""
    out, stack, on = [], [], True
    for raw in src.split("\n"):
        l = raw.rstrip("\r")
        t = l.lstrip()
        if t.startswith("#ifdef ") or t.startswith("#ifndef "):
            d = t.split()[1] == variant
            cond = d if t.startswith("#ifdef ") else not d
            stack.append((on, cond))
            on = on and cond
            continue
        if t.startswith("#else"):
            outer, cond = stack[-1]
            on = outer and not cond
            continue
        if t.startswith("#endif"):
            on = stack.pop()[0]
            continue
        if not on or t.startswith("//") or len(t) == 0:
            continue
        out.append(l + "\n")
    return "".join(out)


def prelude(fragment, precision="highp"):
    """GlBackend.Prelude for the essl100 dialect."""
    return "#version 100\n" + (f"precision {precision} float;\n" if fragment else "precision highp float;\n")


def cstr(s):
    lines = s.split("\n")
    body = []
    for i, ln in enumerate(lines):
        if i == len(lines) - 1 and ln == "":
            break
        esc = ln.replace("\\", "\\\\").replace('"', '\\"')
        body.append(f'\t"{esc}\\n"')
    return "\n".join(body) if body else '\t""'


def ident(name):
    return name.replace("+", "_").replace("-", "_")


def main():
    repo, out_h = sys.argv[1], sys.argv[2]
    sh_dir = os.path.join(repo, "engine/Shepherd.Engine/Render/Shaders")
    dump_dir = os.path.join(repo, "vita/v4/shaders-essl100")
    own_sh = os.path.join(repo, "engine/Derived/Shepherd.Engine.Derived/Shaders")
    own_dump = os.path.join(repo, "engine/Derived/shaders-essl100")
    owner = "--owner" in sys.argv[3:]
    if owner and not (os.path.exists(os.path.join(own_sh, "stylizer.frag")) and os.path.exists(os.path.join(repo, "engine/Derived/vita-gltest/stylizer_ref.c"))):
        sys.exit("gen_shaders.py --owner: no owner-local stylizer here (engine/Derived: Shaders/stylizer.frag + vita-gltest/stylizer_ref.c)")
    programs = PROGRAMS + (OWNER_PROGRAMS if owner else [])
    cand_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "candidates")
    files = {}
    stale = []
    for name, vsf, fsf, var in programs:
        for f, frag in ((vsf, False), (fsf, True)):
            src = os.path.join(sh_dir, f) if os.path.exists(os.path.join(sh_dir, f)) else os.path.join(own_sh, f)
            with open(src, encoding="ascii") as fh:
                text = prelude(frag) + expand(fh.read(), var)
            key = f"{name}.essl100.{'frag' if frag else 'vert'}"
            files[key] = text
            dp = os.path.join(own_dump if (name, vsf, fsf, var) in OWNER_PROGRAMS else dump_dir, key)
            if not os.path.exists(dp) or open(dp, encoding="ascii").read() != text:
                stale.append(key)
    cands = {}
    for fn in sorted(os.listdir(cand_dir)):
        if fn.endswith((".vert", ".frag")):
            cands[fn] = open(os.path.join(cand_dir, fn), encoding="ascii").read()

    # glslang ES 1.00 front end over everything we embed
    gl = shutil.which("glslangValidator")
    glres = "glslang not on PATH: skipped"
    if gl:
        bad = []
        with tempfile.TemporaryDirectory(dir=os.environ.get("TMPDIR")) as td:
            for k, t in list(files.items()) + list(cands.items()):
                p = os.path.join(td, k.replace("+", "_"))
                open(p, "w", encoding="ascii").write(t)
                stage = "frag" if k.endswith(".frag") else "vert"
                r = subprocess.run([gl, "-S", stage, p], capture_output=True, text=True)
                if r.returncode != 0:
                    bad.append(f"{k}: {r.stdout.strip()} {r.stderr.strip()}")
        if bad:
            print("glslang refused:\n  " + "\n  ".join(bad), file=sys.stderr)
            sys.exit(1)
        glres = f"glslang ES 1.00 front end: {len(files) + len(cands)}/{len(files) + len(cands)} OK"

    allsha = hashlib.sha256("".join(files[k] for k in sorted(files)).encode()).hexdigest()
    with open(out_h, "w", encoding="ascii") as o:
        o.write("/* GENERATED by vita/gltest/tools/gen_shaders.py -- do not edit.\n")
        o.write(f" * GlBackend's {len(programs)} programs of these kinds as handed to the driver under the essl100 dialect (engine/Shepherd.Engine/\n")
        o.write(" * Render/Shaders + the owner-local engine/Derived ones when present, through GlBackend.Expand + Prelude), plus the gltest's own\n")
        o.write(" * candidates (src/candidates, not in GlBackend). */\n")
        o.write(f'#define GLTEST_HAVE_STYLIZER {1 if owner else 0}   /* owner-local stylizer shader + reference built in: derived code, never share */\n')
        o.write(f'#define GLTEST_NPROGRAMS {len(programs)}\n')
        o.write(f'#define SHADERS_SHA256 "{allsha}"\n')
        o.write(f'#define SHADERS_DUMP_AGREES {0 if stale else 1}   /* vita/v4/shaders-essl100 == engine text */\n')
        o.write(f'#define SHADERS_DUMP_STALE "{",".join(stale)}"\n')
        o.write(f'#define SHADERS_GLSLANG "{glres}"\n')
        for k, t in files.items():
            o.write(f"static const char SRC_{ident(k.replace('.essl100.', '_'))}[] =\n{cstr(t)};\n")
        for k, t in cands.items():
            o.write(f"static const char CAND_{ident(k.replace('.', '_'))}[] =\n{cstr(t)};\n")
        o.write("typedef struct { const char *name, *vs, *fs; } shader_program_src;\n")
        o.write("static const shader_program_src k_backend_programs[GLTEST_NPROGRAMS] = {\n")
        for name, _, _, _ in programs:
            b = ident(name)
            o.write(f'\t{{ "{name}", SRC_{b}_vert, SRC_{b}_frag }},\n')
        o.write("};\n")
        for k in files:
            o.write(f'/* {k}: sha256 {hashlib.sha256(files[k].encode()).hexdigest()} */\n')
    print(f"shaders: {len(programs)} programs{' (incl. the owner-local stylizer pair)' if owner else ' (no owner-local stylizer)'} (sha {allsha[:16]}), {len(cands)} candidate files; dump {'agrees' if not stale else 'STALE: ' + ', '.join(stale)}; {glres}")


if __name__ == "__main__":
    main()
