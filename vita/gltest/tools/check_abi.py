#!/usr/bin/env python3
"""Check the GL entry points (SHEPGL_COUNT in shepgl.h: 59 since kit #12) of engine/Shepherd.Engine/Render/Gl.cs against vitaGL.h, by ABI class (arm-vita-eabi:
32-bit AAPCS, hard float), and against vita/gltest/shepgl/shepgl_entries.h (names, order, count).

Per parameter and return value, each side is reduced to its calling-convention class:
  u32/i32 (one core register; signedness noted, not an error), u8 (GLboolean/byte), f32 (one VFP s-register),
  ptr (one core register), void.
C# nint is 32-bit on the Vita. Any class mismatch, a missing prototype or a table that disagrees with Gl.cs -> exit 1.

usage: check_abi.py <repo> <vitaGL.h> [table.md]
"""
import re, sys, hashlib

CS_CLASS = {"uint": "u32", "int": "i32", "nint": "i32", "nuint": "u32", "float": "f32", "byte": "u8", "sbyte": "i8",
            "ushort": "u16", "short": "i16", "void": "void", "bool": "BOOL?"}
C_CLASS = {"GLenum": "u32", "GLuint": "u32", "GLbitfield": "u32", "GLint": "i32", "GLsizei": "i32", "GLintptr": "i32",
           "GLsizeiptr": "u32", "GLfixed": "i32", "GLclampx": "i32", "GLfloat": "f32", "GLclampf": "f32",
           "GLboolean": "u8", "GLubyte": "u8", "GLbyte": "i8", "GLchar": "i8", "GLshort": "i16", "GLushort": "u16",
           "void": "void", "GLvoid": "void", "int": "i32", "uint32_t": "u32", "int32_t": "i32", "float": "f32"}


def cs_class(t):
    t = t.strip()
    return "ptr" if t.endswith("*") else CS_CLASS.get(t, "?" + t)


def c_param(p):
    """'const GLchar *const *string' -> ('ptr', text); 'GLsizei count' -> ('i32', ...)."""
    p = p.strip()
    if p in ("void", ""):
        return None
    if "*" in p:
        return "ptr"
    toks = [x for x in re.findall(r"[A-Za-z_][A-Za-z0-9_]*", p) if x != "const"]
    base = toks[0]  # type first, then the (optional) parameter name
    return C_CLASS.get(base, "?" + base)


def compat(a, b):
    if a == b:
        return "ok"
    if {a, b} == {"u32", "i32"}:
        return "ok (sign)"
    return "MISMATCH"


def main():
    repo, hdr = sys.argv[1], sys.argv[2]
    out_md = sys.argv[3] if len(sys.argv) > 3 else None
    gl_cs = open(f"{repo}/engine/Shepherd.Engine/Render/Gl.cs", encoding="utf-8").read()
    fields = {name: sig for sig, name in re.findall(r"(?:public )?static delegate\* unmanaged<([^>]*)>\s+(\w+);", gl_cs)}
    # device kit #5 (R2): a leaf setter is a private pointer _x (with the GC transition) + _xS (unmanaged[SuppressGCTransition], the same
    # signature, assigned from _x) behind a public method; the S twin must match its plain pointer exactly
    twins = {name: sig for sig, name in re.findall(r"static delegate\* unmanaged\[SuppressGCTransition\]<([^>]*)>\s+(\w+);", gl_cs)}
    loads = re.findall(r"^\s*(\w+) = \(delegate\* unmanaged<[^>]*>\)P\(\"(gl\w+)\"", gl_cs, re.M)
    entries = re.findall(r"^SHEPGL_E\(\s*(\d+),\s*(\w+),\s*(gl\w+),\s*(\w+)\)", open(
        f"{repo}/vita/gltest/shepgl/shepgl_entries.h", encoding="ascii").read(), re.M)
    hdr_text = open(hdr, encoding="utf-8", errors="replace").read()
    errors = []
    for tw, sig in twins.items():
        if not tw.endswith("S") or fields.get(tw[:-1]) != sig:
            errors.append(f"{tw}: its SuppressGCTransition signature <{sig}> does not match {tw[:-1]}'s <{fields.get(tw[:-1])}>")
        elif not re.search(r"^\s*" + tw[:-1] + r" = \(delegate\* unmanaged<[^>]*>\)P\(\"gl\w+\"\); " + tw + r" = \(delegate\* unmanaged\[SuppressGCTransition\]<" + re.escape(sig) + r">\)" + tw[:-1] + ";", gl_cs, re.M):
            errors.append(f"{tw}: not assigned from {tw[:-1]} right after it is resolved")
    m_count = re.search(r"^#define SHEPGL_COUNT (\d+)", open(f"{repo}/vita/gltest/shepgl/shepgl.h", encoding="ascii").read(), re.M)
    want = int(m_count.group(1)) if m_count else -1   # kit #12 boot: the count is shepgl.h's (51 before kit #12, 59 with the stencil entries)
    if len(loads) != want:
        errors.append(f"Gl.Load resolves {len(loads)} names, expected {want} (SHEPGL_COUNT)")
    pub = lambda f: f[1].upper() + f[2:] if f.startswith("_") else f   # _viewport (a kit #5 twin pair) is the public Viewport
    if [(pub(f), n) for f, n in loads] != [(f, n) for _, f, n, _ in entries]:
        errors.append("shepgl_entries.h does not list Gl.Load's names in Gl.Load's order")
    for i, (idx, _, _, _) in enumerate(entries):
        if int(idx) != i:
            errors.append(f"shepgl_entries.h: index {idx} at position {i}")
    rows = []
    for i, (field, name) in enumerate(loads):
        sig = [s.strip() for s in fields[field].split(",")]
        cs_ret, cs_args = cs_class(sig[-1]), [cs_class(s) for s in sig[:-1]]
        m = re.search(r"^\s*((?:const\s+)?[A-Za-z_][A-Za-z0-9_]*\s*\**)\s*\b" + name + r"\s*\(([^)]*)\)\s*;", hdr_text, re.M)
        how = entries[i][3] if i < len(entries) else "?"
        if not m:
            rows.append((i, field, name, ", ".join(sig), "MISSING in vitaGL.h", "MISSING", how))
            errors.append(f"{name}: no prototype in {hdr}")
            continue
        c_ret_txt, c_args_txt = m.group(1).strip(), m.group(2)
        c_ret = "ptr" if "*" in c_ret_txt else C_CLASS.get(c_ret_txt.replace("const", "").strip(), "?" + c_ret_txt)
        c_args = [a for a in (c_param(p) for p in c_args_txt.split(",")) if a is not None]
        verdicts = []
        if len(c_args) != len(cs_args):
            verdicts.append(f"arity {len(cs_args)} vs {len(c_args)}")
        for k, (a, b) in enumerate(zip(cs_args, c_args)):
            v = compat(a, b)
            if v != "ok":
                verdicts.append(f"arg{k} {a}/{b} {v}")
        rv = compat(cs_ret, c_ret)
        if rv != "ok":
            verdicts.append(f"ret {cs_ret}/{c_ret} {rv}")
        bad = [v for v in verdicts if "MISMATCH" in v or "arity" in v]
        if bad:
            errors.append(f"{name}: " + "; ".join(bad))
        proto = f"{c_ret_txt} {name}({' '.join(c_args_txt.split())})"
        rows.append((i, field, name, ", ".join(sig), proto, "; ".join(verdicts) if verdicts else "ok", how))
    sha = hashlib.sha256(hdr_text.encode("utf-8", "replace")).hexdigest()[:16]
    lines = [f"| # | Gl.cs field | GL name | C# delegate* unmanaged<args, ret> | vitaGL.h prototype | ABI check | table |",
             "|---|---|---|---|---|---|---|"]
    for r in rows:
        lines.append("| " + " | ".join(str(x).replace("|", "\\|") for x in r) + " |")
    table = "\n".join(lines)
    summary = (f"check_abi: {len(rows)} entry points ({len(twins)} also through a SuppressGCTransition twin), vitaGL.h sha256 {sha}; "
               f"{sum(1 for r in rows if r[5] == 'ok')} exact, {sum(1 for r in rows if 'sign' in r[5] and 'MISMATCH' not in r[5])} sign-only, "
               f"{len(errors)} errors; {sum(1 for r in rows if r[6] == 'WRAP')} wrapped by shepgl")
    if out_md:
        open(out_md, "w", encoding="utf-8").write(f"{summary}\n\n{table}\n")
    print(summary)
    for e in errors:
        print("  ERROR " + e)
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
