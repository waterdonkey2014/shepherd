#!/usr/bin/env python3
# SHEPHERD-VITA V5: AOT GUID gate (run by build.sh after the AOT loop).
# Usage: check_aot_guids.py <dir with the packaged *.dll> <dir with the AOT *.dll.s> [<owner dir>...]
# For every <Module>.dll.s it compares the image's own assembly_guid and every GUID in its image_table (the
# dependencies the AOT code was compiled against) with the MVID of the DLL of that name. The runtime does the
# same check at load time, and in aot-only mode a mismatch is fatal ("dependency ... out of date"), so a stale
# AOT image must fail the build here instead of on the device. Prints one line per module, then
# "TOTAL MISMATCHES <n>"; exit status 1 if n > 0.
# V6 stage 2: extra <owner dir>s (the opt-in engine-AOT build: the owner's staged ux0:data/shepherd/managed) resolve dependencies
# that are not packaged (the rewritten Unity / game assemblies Shepherd.Engine is compiled against); they are marked "(owner)".
import struct, sys, re, os, glob
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
def tables(sfile):
    txt=open(sfile,errors='replace').read()
    out={}
    for lbl in ('image_table:','assembly_guid:'):
        i=txt.index('\n'+lbl)
        if lbl=='assembly_guid:':
            out['__self__']=re.search(r'\.string "([^"]*)"',txt[i:i+200]).group(1); continue
        j=txt.index('\n.text',i+5); bs=bytes(int(x) for x in re.findall(r'\d+',' '.join(l[6:] for l in txt[i:j].splitlines() if l.strip().startswith('.byte'))))
        n=struct.unpack_from('<I',bs,0)[0]; parts=[x.decode() for x in bs[4:].split(b'\0') if x]
        gre=re.compile(r'^[0-9A-F]{8}-[0-9A-F]{4}-')
        for k,x in enumerate(parts):
            if gre.match(x): out[parts[k-1]]=x
        out['__count__']=n
    return out
il,aot=sys.argv[1],sys.argv[2]
mv={os.path.basename(f)[:-4]:mvid(f) for f in glob.glob(il+'/*.dll')}
owner={}
for od in sys.argv[3:]:
    for f in glob.glob(od+'/*.dll'):
        n=os.path.basename(f)[:-4]
        if n not in mv and n not in owner: owner[n]=mvid(f)
bad=0
for s in sorted(glob.glob(aot+'/*.dll.s')):
    a=os.path.basename(s)[:-6]; t=tables(s)
    ok=t['__self__']==(mv[a] if a in mv else owner.get(a)); bad+= not ok   # (owner: a module whose IL is not packaged, e.g. Shepherd.Engine.Derived)
    deps=[]
    for k,v in t.items():
        if k.startswith('__'): continue
        r=mv.get(k); tag=''
        if r is None and k in owner: r=owner[k]; tag=' (owner)'
        deps.append(f"{k}{tag}:{'ok' if r==v else 'MISMATCH '+str(r)+' vs '+v}"); bad+= r!=v
    print(f"{a}: self {'ok' if ok else 'MISMATCH'} {t['__self__']} | image_table {t['__count__']}: "+', '.join(deps))
print("TOTAL MISMATCHES", bad)
sys.exit(1 if bad else 0)
