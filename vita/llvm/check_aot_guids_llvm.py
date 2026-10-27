#!/usr/bin/env python3
"""check_aot_guids_llvm.py <dir with the packaged *.dll> <aot dir> [<owner dir>...]: the AOT GUID gate of
vita/hello/check_aot_guids.py for LLVM AOT output. With LLVM, Mono emits the AOT data tables into the LLVM module
(aot-compiler.c emit_aot_data -> mono_llvm_emit_aot_data), so `assembly_guid` and `image_table` are local .rodata symbols of
<M>-llvm.o instead of labels in <M>.dll.s. Same comparison, same output format ("TOTAL MISMATCHES <n>", exit 1 if n > 0):
the image's own GUID and every image_table dependency GUID against the MVID of the DLL of that name (owner dirs marked)."""
import glob, os, re, struct, subprocess, sys
# mvid(): copied verbatim from vita/hello/check_aot_guids.py (that script runs at import time, so it cannot be imported)
def mvid(path):
    d=open(path,'rb').read()
    pe=struct.unpack_from('<I',d,0x3c)[0]
    nsec=struct.unpack_from('<H',d,pe+6)[0]; opt=struct.unpack_from('<H',d,pe+20)[0]
    oh=pe+24; magic=struct.unpack_from('<H',d,oh)[0]
    ddir=oh+(96 if magic==0x10b else 112)
    clirva,_=struct.unpack_from('<II',d,ddir+14*8)
    secs=[struct.unpack_from('<8sIIII',d,oh+opt+40*i) for i in range(nsec)]
    def off(rva):
        for n,vs,va,rs,rp in secs:
            if va<=rva<va+max(vs,rs): return rva-va+rp
    c=off(clirva); mdrva,_=struct.unpack_from('<II',d,c+8); m=off(mdrva)
    vl=struct.unpack_from('<I',d,m+12)[0]; p=m+16+vl; ns=struct.unpack_from('<H',d,p+2)[0]; p+=4
    st={}
    for _ in range(ns):
        o,s=struct.unpack_from('<II',d,p); p+=8; e=d.index(b'\0',p); name=d[p:e].decode(); p=(e+4)&~3; st[name]=(m+o,s)
    t=st['#~'][0]; hs=d[t+6]; valid=struct.unpack_from('<Q',d,t+8)[0]
    rows=t+24; nt=bin(valid).count('1'); tbl=rows+4*nt
    # Module row: Generation(2) Name(str) Mvid(guid) ...
    sidx=4 if hs&1 else 2; gidx=4 if hs&2 else 2
    q=tbl+2+sidx
    g=struct.unpack_from('<I' if gidx==4 else '<H',d,q)[0]
    go=st['#GUID'][0]+16*(g-1); b=d[go:go+16]
    a,b1,c1=struct.unpack_from('<IHH',b,0)
    return ('%08X-%04X-%04X-%s-%s'%(a,b1,c1,b[8:10].hex().upper(),b[10:].hex().upper()))

NM = os.path.expanduser("~/vitasdk/bin/arm-vita-eabi-nm")

def sym_bytes(obj, name):
    """bytes of the symbol NAME in the ELF32 relocatable OBJ (handles extended section numbering: llc puts every LLVM method in
    its own section, so big modules have more than 0xff00 sections: e_shnum = 0, SHN_XINDEX symbols, SHT_SYMTAB_SHNDX)"""
    d = open(obj, "rb").read()
    shoff = struct.unpack_from("<I", d, 0x20)[0]; shentsize, shnum, shstrndx = struct.unpack_from("<HHH", d, 0x2e)
    sh0 = struct.unpack_from("<IIIIIIIIII", d, shoff)
    if shnum == 0: shnum = sh0[5]
    sh = [struct.unpack_from("<IIIIIIIIII", d, shoff + i * shentsize) for i in range(shnum)]
    xidx = {s[6]: s for s in sh if s[1] == 18}   # SHT_SYMTAB_SHNDX, keyed by the symtab it belongs to
    for k, s in enumerate(sh):
        if s[1] != 2: continue
        strtab = sh[s[6]][4]
        for i in range(s[5] // 16):
            st_name, st_value, st_size, st_info, st_other, st_shndx = struct.unpack_from("<IIIBBH", d, s[4] + i * 16)
            n = d[strtab + st_name: d.index(b"\0", strtab + st_name)].decode(errors="replace")
            if n == name:
                if st_shndx == 0xffff: st_shndx = struct.unpack_from("<I", d, xidx[k][4] + 4 * i)[0]
                sec = sh[st_shndx]
                return d[sec[4] + st_value: sec[4] + st_value + st_size]
    raise KeyError(name)

def tables(obj):
    out = {}
    g = sym_bytes(obj, "assembly_guid").split(b"\0")[0].decode()
    out["__self__"] = g
    bs = sym_bytes(obj, "image_table")
    n = struct.unpack_from("<I", bs, 0)[0]; parts = [x.decode(errors="replace") for x in bs[4:].split(b"\0") if x]
    gre = re.compile(r"^[0-9A-F]{8}-[0-9A-F]{4}-")
    for k, x in enumerate(parts):
        if gre.match(x): out[parts[k - 1]] = x
    out["__count__"] = n
    return out

def main(il, aot, owners):
    mv = {os.path.basename(f)[:-4]: mvid(f) for f in glob.glob(il + "/*.dll")}
    owner = {}
    for od in owners:
        for f in glob.glob(od + "/*.dll"):
            n = os.path.basename(f)[:-4]
            if n not in mv and n not in owner: owner[n] = mvid(f)
    bad = 0
    for o in sorted(glob.glob(aot + "/*-llvm.o")):
        a = os.path.basename(o)[:-7]; t = tables(o)
        ok = t["__self__"] == (mv[a] if a in mv else owner.get(a)); bad += not ok   # (owner: a module whose IL is not packaged, e.g. Shepherd.Engine.Derived)
        deps = []
        for k, v in t.items():
            if k.startswith("__"): continue
            r = mv.get(k); tag = ""
            if r is None and k in owner: r = owner[k]; tag = " (owner)"
            deps.append(f"{k}{tag}:{'ok' if r == v else 'MISMATCH ' + str(r) + ' vs ' + v}"); bad += r != v
        print(f"{a}: self {'ok' if ok else 'MISMATCH'} {t['__self__']} | image_table {t['__count__']}: " + ", ".join(deps))
    print("TOTAL MISMATCHES", bad)
    return 1 if bad else 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1], sys.argv[2], sys.argv[3:]))
