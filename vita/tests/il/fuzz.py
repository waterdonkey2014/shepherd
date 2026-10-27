#!/usr/bin/env python3
"""Kit #7 tests track (vita/notes/kit7-tests.md "Random-program fuzzing of every tier", Fuzzlyn / Antigen / Jit-Picking style, written
in-house: no download). Generates random C# checksum programs, compiles them ONCE to IL (Unity's mcs, .NET 4.x profile), runs the same
IL on every tier through the IL conformance driver (vita/tests/il/run.sh: each program is a method fp_fuzzN returning its checksum), and
auto-reduces any program whose checksum differs between two tiers to a minimal statement list (delta debugging, re-compiling and
re-running on the two disagreeing tiers).

  fuzz.py gen <seed> <count> <out dir> [--float]   programs -> <out dir>/conf-fuzz-<seed>.dll (+ the .cs, kept in the work dir)
  fuzz.py reduce <program .cs> <method> <tierA> <tierB> <out .cs>

Two families: integer-only programs (any difference between tiers = a compiler / runtime bug) and float-mixed ones (--float: a
difference can also be the tiers' float evaluation rule -- double-precision intermediates vs float32 rounding, FMA, float->int
overflow conversion -- which the reduced program then names). Programs never divide by zero (divisors | 1), loops are bounded, shifts
are masked, overflow is unchecked (C#'s defined wrap-around), float->int conversions only from values in range unless --edges."""
import sys, os, random, subprocess, re, shutil

U = os.path.join(os.environ.get("UNITY_APPS", "/Applications/Unity"), "Unity.app/Contents/MonoBleedingEdge")
MCS = [f"{U}/bin/mono", f"{U}/lib/mono/4.5/mcs.exe"]
ITYPES = ["int", "long", "uint", "short", "byte", "ulong"]
FTYPES = ["float", "double"]

class Gen:
    def __init__(self, rnd, floats, edges):
        self.r = rnd; self.floats = floats; self.edges = edges
        self.types = ITYPES + (FTYPES if floats else [])
        self.vars = [(f"s{i}", self.r.choice(self.types)) for i in range(10)]
    def lit(self, t):
        r = self.r
        if t in ("float", "double"):
            v = r.choice([0.0, 1.0, -1.0, 0.5, 0.1, 3.0, 1e-3, 1e3, 16777217.0, 0.3, 2.5, -7.75]) if r.random() < 0.5 else round(r.uniform(-1000, 1000), r.randint(0, 6))
            return f"{v!r}f" if t == "float" else f"{v!r}"
        if t == "byte": return str(r.randint(0, 255))
        if t == "short": return f"(short){r.randint(-32768, 32767)}"
        if t == "uint": return f"{r.randint(0, 2**32-1)}u"
        if t == "ulong": return f"{r.randint(0, 2**64-1)}ul"
        if t == "long": return f"{r.randint(-2**63, 2**63-1)}L" if r.random() < 0.3 else f"{r.randint(-1000, 1000)}L"
        return str(r.randint(-2**31, 2**31-1)) if r.random() < 0.3 else str(r.randint(-1000, 1000))
    def expr(self, t, d=0):
        r = self.r
        if d > 3 or r.random() < 0.25:
            if r.random() < 0.6:
                n, vt = r.choice(self.vars)
                if vt == t: return n
                if vt in FTYPES and t not in FTYPES and not self.edges:   # in range: a float -> integer conversion of an out-of-range value is implementation-defined (ECMA conv.*): only --edges probes it
                    lo, hi = {"byte": (0, 255), "short": (-32768, 32767), "uint": (0, 4e9), "ulong": (0, 1e18), "int": (-2e9, 2e9), "long": (-9e18, 9e18)}[t]
                    return f"({t})Math.Max({float(lo)!r}, Math.Min({float(hi)!r}, (double)({n})))"
                return f"({t})({n})"
            return self.lit(t)
        if t in ("float", "double"):
            op = r.choice(["+", "-", "*", "/", "*", "+"])
            a, b = self.expr(t, d+1), self.expr(t, d+1)
            if op == "/": return f"({a} / ({b} + ({t})1.5))"
            k = r.random()
            if k < 0.08: return f"({t})Math.Sqrt(Math.Abs({a}))"
            if k < 0.12: return f"(({a}) < ({b}) ? ({a}) : ({b}))"
            return f"({a} {op} {b})"
        op = r.choice(["+", "-", "*", "&", "|", "^", "<<", ">>", "/", "%", "+", "*"])
        a, b = self.expr(t, d+1), self.expr(t, d+1)
        if op in ("<<", ">>"): return f"({t})({a} {op} ((int)({b}) & 31))"
        if op in ("/", "%"): return f"({t})({a} {op} ({t})(({b}) | 1))" if t not in ("short", "byte") else f"({t})({a} {op} (({b}) | 1))"
        if self.floats and r.random() < 0.08:
            ft = r.choice(FTYPES); fe = self.expr(ft, d+1)
            if self.edges: return f"({t})({fe})"
            return f"({t})(int)Math.Max(-1e6, Math.Min(1e6, (double)({fe})))"
        return f"({t})({a} {op} {b})"
    def stmt(self, d=0):
        r = self.r
        n, t = r.choice(self.vars)
        k = r.random()
        if d < 2 and k < 0.12:
            m, mt = r.choice(self.vars); c = f"({m}) {r.choice(['<', '>', '==', '!='])} ({mt})({self.expr(mt, 2)})"
            return f"if ({c}) {{ {self.stmt(d+1)} }} else {{ {self.stmt(d+1)} }}"
        if d < 2 and k < 0.18:
            return f"for (int i{d} = 0; i{d} < {r.randint(1, 7)}; i{d}++) {{ {self.stmt(d+1)} }}"
        if k < 0.24: return f"{n} = ({t})F{r.randint(0, 3)}(({self.vars[0][1]}){self.vars[0][0]});"
        if k < 0.28: return f"unchecked {{ {n} += ({t})({self.expr(t)}); }}"
        return f"{n} = ({t})({self.expr(t)});"
    def program(self, name, nstmt):
        r = self.r
        body = [self.stmt() for _ in range(nstmt)]
        lines = ["public static class " + name + " {"]
        for n, t in self.vars: lines.append(f"  static {t} {n} = ({t}){self.lit(t)};")
        for j in range(4):
            pt = self.vars[0][1]; lines.append(f"  static {pt} F{j}({pt} a) {{ unchecked {{ return ({pt})({self.expr(pt).replace(self.vars[0][0], 'a')}); }} }}")
        lines.append("  static int H(ulong h, ulong v) { unchecked { h ^= v; h *= 1099511628211UL; return (int)(h ^ (h >> 32)); } }")
        lines.append("  static ulong B(float f) { return (ulong)(uint)BitConverter.ToInt32(BitConverter.GetBytes(f), 0); }")
        lines.append("  static ulong B(double f) { return (ulong)BitConverter.DoubleToInt64Bits(f); }")
        lines.append("  public static int Run() { unchecked {")
        for s in body: lines.append("    " + s)
        lines.append("    ulong h = 14695981039346656037UL;")
        for n, t in self.vars:
            v = f"B({n})" if t in FTYPES else f"(ulong){n}"
            lines.append(f"    h = (ulong)(uint)H(h, {v}) | (h << 32);")
        lines.append("    return H(h, 0); } }")
        lines.append("}")
        return "\n".join(lines), body

def gen(seed, count, out, floats, edges):
    os.makedirs(out, exist_ok=True)
    rnd = random.Random(seed); progs = []
    src = ["using System;"]
    for i in range(count):
        g = Gen(rnd, floats, edges); text, body = g.program(f"Fz{seed}_{i}", rnd.randint(8, 40)); src.append(text); progs.append(f"Fz{seed}_{i}")
    src.append("public static class FuzzTests_%d {" % seed)
    for p in progs: src.append(f"  public static int fp_{p}() {{ return {p}.Run(); }}")
    src.append("}")
    cs = os.path.join(out, f"fuzz-{seed}{'-f' if floats else ''}.cs"); open(cs, "w").write("\n".join(src) + "\n")
    dll = os.path.join(out, f"conf-fuzz-{seed}{'-f' if floats else ''}.dll")
    r = subprocess.run(MCS + ["-nologo", "-target:library", "-nowarn:162,219,429,675", "-out:" + dll, cs], capture_output=True, text=True)
    if r.returncode != 0: print(r.stdout[-2000:], r.stderr[-2000:]); return 1
    print(f"{dll}: {count} programs ({'float-mixed' if floats else 'integer-only'})"); return 0

def run_one(dll_dir, tier):
    """One tier on a directory holding conformance.exe + conf-*.dll; returns {method: value}."""
    t = os.path.dirname(os.path.abspath(__file__)); out = os.path.join(dll_dir, f"r-{tier}")
    env = dict(os.environ, IL_OUT=dll_dir, IL_RUNS=out)
    subprocess.run(["zsh", os.path.join(t, "run.sh"), tier], env=env, capture_output=True, text=True)
    res = {}
    p = os.path.join(out, tier + ".tsv")
    if os.path.exists(p):
        for l in open(p):
            f = l.rstrip("\n").split("\t")
            if len(f) >= 3: res[f[1].split(".")[-1]] = f[2]
    return res

def reduce(cs, method, ta, tb, outcs, bindir):
    """Delta debugging over the Run() body's statements of one program: keep removing chunks while the two tiers still disagree."""
    text = open(cs).read()
    cls = method[3:]   # fp_<Class>
    m = re.search(r"public static class " + re.escape(cls) + r" \{.*?\n\}\n", text, re.S)
    prog = m.group(0)
    head, rest = prog.split("  public static int Run() { unchecked {\n", 1)
    body, tail = rest.split("    ulong h = 14695981039346656037UL;\n", 1)
    stmts = [s for s in body.split("\n") if s.strip()]
    work = os.path.join(bindir, "reduce"); os.makedirs(work, exist_ok=True)
    shutil.copy(os.path.join(bindir, "conformance.exe"), work)
    def differs(st):
        p = head + "  public static int Run() { unchecked {\n" + "\n".join(st) + "\n    ulong h = 14695981039346656037UL;\n" + tail
        src = "using System;\n" + p + "public static class FuzzTests_R { public static int fp_" + cls + "() { return " + cls + ".Run(); } }\n"
        for f in os.listdir(work):
            if f.startswith("conf-"): os.remove(os.path.join(work, f))
        open(os.path.join(work, "r.cs"), "w").write(src)
        r = subprocess.run(MCS + ["-nologo", "-target:library", "-nowarn:162,219,429,675", "-out:" + os.path.join(work, "conf-r.dll"), os.path.join(work, "r.cs")], capture_output=True)
        if r.returncode != 0: return False, src
        a, b = run_one(work, ta), run_one(work, tb)
        k = "fp_" + cls
        return (k in a and k in b and a[k] != b[k]), src
    ok, best = differs(stmts)
    if not ok: print("does not reproduce"); return 1
    n = 2
    while len(stmts) >= 2:
        chunk = max(1, len(stmts) // n); changed = False
        for i in range(0, len(stmts), chunk):
            trial = stmts[:i] + stmts[i + chunk:]
            d, src = differs(trial)
            if d: stmts = trial; best = src; changed = True; break
        if not changed:
            if chunk == 1: break
            n = min(len(stmts), n * 2)
    open(outcs, "w").write(best); print(f"reduced to {len(stmts)} statements: {outcs}"); return 0

if __name__ == "__main__":
    a = sys.argv
    if a[1] == "gen": sys.exit(gen(int(a[2]), int(a[3]), a[4], "--float" in a, "--edges" in a))
    if a[1] == "reduce": sys.exit(reduce(a[2], a[3], a[4], a[5], a[6], a[7] if len(a) > 7 else os.path.dirname(a[2])))
