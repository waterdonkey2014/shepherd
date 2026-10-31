#!/usr/bin/env python3
"""self_imports.py -- device kit #9 (kit stage; vita/notes/kit9-integration.md): the libraries a Vita module IMPORTS, read straight from the
file the Vita loads -- a fake-signed SELF (vita-make-fself output: eboot.bin, app0:module/shepherd.suprx), its segments zlib-compressed or
not -- or from a plain Vita ELF (.velf). The kit's check (vita/tools/check-kit.sh) uses it on every launcher VPK's module/shepherd.suprx:
the engine module may import only the libraries already loaded when the launcher loads it (vita/shepherd/module-load-libs.txt), else the
Vita refuses the module with 0x8002D003 (SCE_KERNEL_ERROR_MODULEMGR_NO_LIB: kit #8's launchers on the owner's Vita, 2026-10-22). The build
checks the same list on the linked ELF (check_vpk.py --module-load-libs); this reads the shipped bytes, so a kit folder can be checked
without its build directory.

How: the SCE header (magic "SCE\\0") gives the ELF header, the program headers and one segment-info record per program header (offset,
length, compression 1 = plain / 2 = zlib, encryption 2 = plain); sce_module_info sits in segment (e_entry >> 30) at offset (e_entry &
0x3fffffff); its import_top / import_end (offsets in that segment) bound the import records (0x34 or 0x24 bytes, the size in their first
16 bits); each record's library name is a pointer into a loadable segment (the module's link-time addresses: p_vaddr).

usage: self_imports.py <module.suprx | eboot.bin | module.velf> [--allow <module-load-libs.txt>] [--zip <vpk> --member <path in the vpk>]
   prints one library name per line; with --allow exits 1 (and names them) when a library is not on the list. Standard library only."""
import struct, sys, zlib, zipfile, argparse


def segments(blob):
    """[(p_vaddr, p_memsz, bytes)] and e_entry of a SELF or a plain ELF"""
    if blob[:4] == b"SCE\x00":
        (magic, ver, sdk, htype, meta, hlen, elf_sz, self_sz, unk, self_off, app_off, elf_off, phdr_off, shdr_off, sinfo_off,
         sver_off, ci_off, ci_sz, pad) = struct.unpack_from("<IIHHIQQQQQQQQQQQQQQ", blob, 0)
        ehdr = blob[elf_off:elf_off + 52]
        phoff_in_self = phdr_off
        sinfo = sinfo_off
    elif blob[:4] == b"\x7fELF":
        ehdr = blob[:52]; phoff_in_self = None; sinfo = None
    else:
        raise SystemExit("not a SELF (SCE\\0) or an ELF")
    e_entry, e_phoff = struct.unpack_from("<II", ehdr, 24)
    e_phentsize, e_phnum = struct.unpack_from("<HH", ehdr, 42)
    out = []
    for i in range(e_phnum):
        base = (phoff_in_self if phoff_in_self is not None else e_phoff) + i * e_phentsize
        p_type, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_flags, p_align = struct.unpack_from("<8I", blob, base)
        if sinfo is not None:
            off, ln, comp, enc = struct.unpack_from("<QQQQ", blob, sinfo + i * 32)
            if enc == 1:
                raise SystemExit(f"segment {i} is encrypted (a retail SELF): not readable here")
            data = blob[off:off + ln]
            if comp == 2:
                data = zlib.decompress(data)
        else:
            data = blob[p_offset:p_offset + p_filesz]
        out.append((p_type, p_vaddr, p_memsz, data))
    return out, e_entry


def imports(blob):
    segs, e_entry = segments(blob)
    si, so = e_entry >> 30, e_entry & 0x3fffffff
    seg0 = segs[si][3]
    import_top, import_end = struct.unpack_from("<II", seg0, so + 0x2C)
    loads = [(v, m, d) for (t, v, m, d) in segs if t == 1]

    def cstr(ptr):
        for v, m, d in loads:
            if v <= ptr < v + max(m, len(d)):
                o = ptr - v; e = d.index(b"\x00", o)
                return d[o:e].decode("ascii", "replace")
        raise SystemExit(f"a library name pointer 0x{ptr:08x} outside every loadable segment")
    names, p = [], import_top
    while p < import_end:
        size = struct.unpack_from("<H", seg0, p)[0]
        if size == 0x34:
            name_ptr = struct.unpack_from("<I", seg0, p + 0x14)[0]
        elif size == 0x24:
            name_ptr = struct.unpack_from("<I", seg0, p + 0x10)[0]
        else:
            raise SystemExit(f"an import record of size 0x{size:x} at 0x{p:x} (expected 0x34 or 0x24)")
        names.append(cstr(name_ptr)); p += size
    return names


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("file", nargs="?"); ap.add_argument("--allow"); ap.add_argument("--zip"); ap.add_argument("--member")
    a = ap.parse_args()
    if a.zip:
        blob = zipfile.ZipFile(a.zip).read(a.member or "module/shepherd.suprx")
    else:
        blob = open(a.file, "rb").read()
    libs = imports(blob)
    for n in libs: print(n)
    if a.allow:
        ok = {l.strip() for l in open(a.allow) if l.strip() and not l.lstrip().startswith("#")}
        late = [n for n in libs if n not in ok]
        if late:
            print("NOT LOADED YET when the launcher loads the module: " + " ".join(late), file=sys.stderr); sys.exit(1)


if __name__ == "__main__":
    main()
