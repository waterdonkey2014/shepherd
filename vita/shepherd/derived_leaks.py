#!/usr/bin/env python3
"""Derived content in TRACKED files (fid1-fixup round 2, after the hygiene verifier's finding: check_vpk.py 3b built its markers from
engine/**/Derived and silently dropped whatever also occurred in a non-derived source, so a copy of derived code put back into a tracked
file made the VPK gate weaker instead of failing it; nothing guarded the tracked tree but a manual git grep).

The repo rule (.gitignore line 1, vita/PLAN.md "Hygiene"): ports of the game's / a third party's code and compiled shaders -- formulas
and compiler constants included -- live only in the git-ignored engine/**/Derived/. This check takes markers from those files and fails
when one occurs in a file git would carry: tracked (`git ls-files`) or untracked and not ignored (what `git add -A` would add), outside
any Derived/ directory:
  - a shader code line: *.frag|*.vert|*.glsl under Derived/, comments removed, >= 24 characters (compared whitespace-insensitively)
  - a C#/C code line: *.cs|*.c|*.h under Derived/, comments removed, >= 32 non-blank characters, as a whole line of a file
    (whitespace-insensitive): a copied formula line
  - a string literal: >= 10 characters, from *.cs|*.c|*.h under Derived/ (interpolation holes split out), except a bare name (one
    identifier-like token: a type, member, shader or keyword name -- the engine reads the game's objects by such names, PLAN Hygiene)
  - a numeric literal with >= 7 significant digits (e.g. a shader compiler's float printout), from the same code and shader files
  - a shader file name: a file of git's named like a derived shader (a copy of lut.frag anywhere); a mention of the name in text is not
    a leak (the engine names its owner-local programs: GlBackend.Programs)
Markers listed in the owner-local engine/Derived/leak-allow.txt (one per line as printed, '#' comments) are generic text that may also
sit in tracked files (e.g. the engine's own vertex-shader line); that list lives with the derived files, never in git.
Every hit is listed (file:line: marker); exit 1 on a hit. With no engine/**/Derived on this machine the check is VACUOUS (exit 0; with
--require exit 1). Run by vita/shepherd/build-derived.sh (before it builds) and by check_vpk.py 3b (build.sh).
usage: derived_leaks.py [--repo DIR] [--require] [--markers OUT] [--quiet]"""
import argparse, os, re, subprocess, sys

SHADER = (".frag", ".vert", ".glsl")
CODE = (".cs", ".c", ".h")
STR = re.compile(r'@?\$?"((?:[^"\\\n]|\\.)*)"')
NAME = re.compile(r"^[A-Za-z_][A-Za-z0-9_./+-]*$")   # a bare name (type, member, shader, keyword): a lookup key, not content
NUM = re.compile(r"(?<![\w.])(\d+\.\d*|\.\d+|\d+)(?:[eE][-+]?\d+)?[fFdDmMuUlL]*(?![\w.])")

def derived_files(repo):
    eng = os.path.join(repo, "engine")
    for dp, dn, fs in os.walk(eng):
        dn[:] = [d for d in dn if d not in ("bin", "obj")]
        if "Derived" not in os.path.relpath(dp, eng).split(os.sep): continue
        for f in fs: yield os.path.join(dp, f)

def strip_comments(t):
    return re.sub(r"/\*.*?\*/", " ", re.sub(r"//[^\n]*", "", t), flags=re.S)

def sig_digits(lit):
    d = lit.replace(".", "").lstrip("0")
    if "." in lit: d = d.rstrip("0")
    return len(d)

def literals(code):
    out = set()
    for m in STR.finditer(code):
        for frag in re.split(r"\{[^{}]*\}", m.group(1)):
            frag = frag.replace('\\"', '"').replace("\\\\", "\\")
            if len(frag) >= 10 and "\\" not in frag and "\n" not in frag and not NAME.match(frag): out.add(frag)
    return out

def allowed(repo):
    p = os.path.join(repo, "engine", "Derived", "leak-allow.txt")
    if not os.path.exists(p): return set()
    return {l.rstrip("\n") for l in open(p, errors="replace") if l.strip() and not l.lstrip().startswith("#")}

def collect(repo):
    """(markers {text: kind}, shader file names, derived file count, allow-list)"""
    files = list(derived_files(repo))
    allow = allowed(repo)
    markers, shnames = {}, set()
    for p in files:
        ext = os.path.splitext(p)[1]
        if ext not in SHADER + CODE: continue
        code = strip_comments(open(p, errors="replace").read())
        if ext in SHADER:
            shnames.add(os.path.basename(p))
            for l in code.splitlines():
                l = " ".join(l.split())
                if len(l) >= 24: markers.setdefault(l, "shader line")
        if ext in CODE:
            for l in literals(code): markers.setdefault(l, "string literal")
            for l in code.splitlines():
                if len("".join(l.split())) >= 32: markers.setdefault(" ".join(l.split()), "code line")
        for m in NUM.finditer(STR.sub(" ", code)):
            if sig_digits(m.group(1)) >= 7: markers.setdefault(m.group(1), "numeric literal")
    nallow = {"".join(a.split()) for a in allow}
    for k in list(markers):
        if k in allow or (markers[k] in ("shader line", "code line") and "".join(k.split()) in nallow): del markers[k]
    return markers, {n for n in shnames if n not in allow}, len(files), allow

def is_git(repo):
    return subprocess.run(["git", "-C", repo, "rev-parse", "--is-inside-work-tree"], capture_output=True).returncode == 0

def git_files(repo):
    """Files git carries or would carry (tracked + untracked-not-ignored), outside any Derived/ directory. Not a git checkout (a git
    archive): every file under engine/ and vita/ outside Derived/, bin/, obj/ and out/ directories."""
    if not is_git(repo):
        res = []
        for top in ("engine", "vita", "tools"):
            for dp, dn, fs in os.walk(os.path.join(repo, top)):
                dn[:] = [d for d in dn if d not in ("Derived", "bin", "obj", "out", "runs") and not d.startswith(".")]
                res += [os.path.relpath(os.path.join(dp, f), repo) for f in fs]
        return sorted(res)
    out = subprocess.run(["git", "-C", repo, "ls-files", "-z", "--cached", "--others", "--exclude-standard"], capture_output=True, check=True).stdout
    return sorted({x for x in out.decode().split("\0") if x and "Derived" not in x.split("/")[:-1]})

def leaks(repo, markers, shnames, files):
    hits = []
    for x in files:
        if os.path.basename(x) in shnames: hits.append(f"{x}: a file named like the derived shader '{os.path.basename(x)}'")
    plain = {k: v for k, v in markers.items() if v != "code line"}   # code lines: whole lines only (below)
    if plain and is_git(repo):
        pf = os.path.join(os.environ.get("TMPDIR", "/tmp"), f"derived-leaks-{os.getpid()}.txt")
        with open(pf, "w") as f: f.write("\n".join(plain) + "\n")
        try:
            r = subprocess.run(["git", "-C", repo, "grep", "--untracked", "-n", "-I", "-F", "-f", pf, "--", ".", ":(exclude,glob)**/Derived/**"], capture_output=True)
        finally: os.unlink(pf)
        grep_hits = r.stdout.decode(errors="replace").splitlines()
    else:   # no git: the same search in Python
        grep_hits = []
        for x in files:
            try: b = open(os.path.join(repo, x), "rb").read()
            except OSError: continue
            if b"\0" in b: continue
            for i, l in enumerate(b.decode(errors="replace").splitlines(), 1):
                if any(k in l for k in plain): grep_hits.append(f"{x}:{i}:{l}")
    if markers:
        for h in grep_hits:
            path, ln, text = h.split(":", 2)
            found = [k for k in plain if k in text]
            if found: hits.append(f"{path}:{ln}: " + "; ".join(f"{markers[k]} {k!r}" for k in found[:4]))
        # shader and code lines as whole lines, whitespace-insensitively: a re-indented or re-spaced copy of a line is still a copy
        whole = {"".join(k.split()): kind for k, kind in markers.items() if kind in ("shader line", "code line")}
        for x in files:
            try: b = open(os.path.join(repo, x), "rb").read()
            except OSError: continue
            if b"\0" in b: continue
            for i, l in enumerate(b.decode(errors="replace").splitlines(), 1):
                n = "".join(l.split())
                if len(n) >= 20 and x.endswith((".patch", ".diff")) and n[:1] in "+-" and n[1:] in whole: n = n[1:]   # a diff line of it
                if len(n) >= 20 and n in whole: hits.append(f"{x}:{i}: {whole[n]} {' '.join(l.split())!r}")
    return sorted(set(hits))

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", default=os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")))
    ap.add_argument("--require", action="store_true", help="fail when there is no engine/**/Derived here")
    ap.add_argument("--markers", help="also write the marker list to this file (for inspection)")
    ap.add_argument("--quiet", action="store_true")
    a = ap.parse_args()
    markers, shnames, nfiles, allow = collect(a.repo)
    if nfiles == 0:
        print("derived leaks: VACUOUS (no engine/**/Derived on this machine: nothing to compare git's files with)")
        return 1 if a.require else 0
    if a.markers:
        with open(a.markers, "w") as f:
            for k in sorted(markers): f.write(f"{markers[k]}\t{k}\n")
    files = git_files(a.repo)
    hits = leaks(a.repo, markers, shnames, files)
    kinds = {k: sum(1 for v in markers.values() if v == k) for k in ("shader line", "code line", "string literal", "numeric literal")}
    summary = (f"derived leaks: {len(markers)} markers from {nfiles} derived files ({kinds['shader line']} shader lines, {kinds['code line']} code lines, {kinds['string literal']} "
               f"string literals, {kinds['numeric literal']} numeric literals of >= 7 significant digits) + {len(shnames)} shader file names "
               f"({len(allow)} generic entries in engine/Derived/leak-allow.txt) searched in {len(files)} files git carries")
    if hits:
        print(summary + f": {len(hits)} LEAK(S) -- derived content in files git carries:")
        for h in hits: print("  " + h)
        return 1
    if not a.quiet: print(summary + ": none")
    return 0

if __name__ == "__main__":
    sys.exit(main())
