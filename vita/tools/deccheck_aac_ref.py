#!/usr/bin/env python3
"""deccheck_aac_ref.py -- SHEPHERD-VITA device kit #13, track bootc (vita/notes/kit13-bootc.md section 7): the AAC half of the decoder checks.

  ref <card dir> [--set menu]      the Mac's reference: every aac-adts take of the card's audio bake (audiobake/<set>/manifest.json) decoded by
                                   vita/audio/tools/sa_deccheck.c over AudioToolbox (sa_aac_apple.c: what the bake's verify listened to) ->
                                   <card>/audiobake/<set>/decref-aac.tsv (owner-local: hashes of the owner's audio, never in git)
  compare <ref.tsv> <device.tsv>   the device's dec-aac.tsv (the host, SHEPHERD_K12_DECCHECK=aac) against the reference: every reference take
                                   must come back with its own line; equal hashes = the device plays what the Mac's analyses heard; a differing
                                   take is listed with both frame counts and peaks (its first 64 KiB of samples are in dec-aac-<n>.pcm on the
                                   device: AAC is not bit-exact by the standard, so a difference is an answer, not an error); a missing take,
                                   an unknown one or an empty reference = FAIL (exit 1)
  --self-test                      the compare's own checks (a missing take, an extra take, an empty reference fail; a differing hash is reported)

The tool is generic; its inputs and outputs are the owner's (the bake's takes): owner-local.
"""
import json, os, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
AUDIO = os.path.join(os.path.dirname(HERE), 'audio')

def build_tool(tmp):
    lib = os.path.join(AUDIO, 'build/mac/libshepaudio.a')
    if not os.path.exists(lib):
        subprocess.run(['bash', os.path.join(AUDIO, 'build.sh'), 'mac'], check=True, stdout=subprocess.DEVNULL)
    exe = os.path.join(tmp, 'sa_deccheck')
    subprocess.run(['clang', '-std=c99', '-O2', '-I' + os.path.join(AUDIO, 'include'), '-I' + os.path.join(AUDIO, 'src'),
                    os.path.join(AUDIO, 'tools/sa_deccheck.c'), lib, '-framework', 'AudioToolbox', '-framework', 'CoreFoundation', '-o', exe], check=True)
    return exe

def takes(bake):
    m = json.load(open(os.path.join(bake, 'manifest.json')))
    seen, out = set(), []
    for e in m.get('events', []):
        for t in e.get('takes', []) or []:
            if t.get('codec') == 'aac-adts' and t['file'] not in seen:
                seen.add(t['file']); out.append((t['file'], int(t['channels']), int(t['rate'])))
    return out

def ref(card, set_='menu'):
    bake = os.path.join(card, 'audiobake', set_)
    tk = takes(bake)
    with tempfile.TemporaryDirectory(prefix='deccheck-aac-', dir=os.environ.get('TMPDIR')) as tmp:
        exe = build_tool(tmp)
        p = subprocess.run([exe, bake], input=''.join(f"{f}\t{c}\t{r}\n" for f, c, r in tk), capture_output=True, text=True)
    lines = [l for l in p.stdout.splitlines() if l.count('\t') == 7]
    if p.returncode or len(lines) != len(tk):
        sys.stderr.write(p.stderr); raise SystemExit(f"the reference decode failed: {len(lines)} of {len(tk)} takes")
    hdr = ["# the AAC decoder check's reference (vita/tools/deccheck_aac_ref.py): sa_deccheck.c over AudioToolbox, every aac-adts take of the bake",
           "# file\tchannels\trate\tframes\tfnv64\tpeak\taccess units\trefused"]
    out = os.path.join(bake, 'decref-aac.tsv')
    open(out, 'w', newline='\n').write('\n'.join(hdr + lines) + '\n')
    fr = sum(int(l.split('\t')[3]) for l in lines)
    print(f"decref-aac.tsv: {len(lines)} takes, {fr} frames decoded by AudioToolbox -> {out}")
    return 0

def read_tsv(p):
    d = {}
    for l in open(p):
        if l.startswith('#') or not l.strip(): continue
        f = l.rstrip('\n').split('\t')
        if len(f) >= 6: d[f[0]] = (int(f[3]), f[4].lower(), int(f[5]))
    return d

def compare(refp, devp, quiet=False):
    r, d = read_tsv(refp), read_tsv(devp)
    if not r: print("FAIL: the reference is empty"); return 1
    missing = [k for k in r if k not in d]; extra = [k for k in d if k not in r]
    same = [k for k in r if k in d and d[k][1] == r[k][1]]; diff = [k for k in r if k in d and d[k][1] != r[k][1]]
    for k in diff[:20]:
        print(f"  differs: {k}: device {d[k][0]} frames peak {d[k][2]}, reference {r[k][0]} frames peak {r[k][2]}")
    ok = not missing and not extra
    print(f"{'PASS' if ok else 'FAIL'}: {len(same)} of {len(r)} takes = the Mac's (AudioToolbox), {len(diff)} differ, {len(missing)} missing, {len(extra)} unknown")
    return 0 if ok else 1

def self_test():
    with tempfile.TemporaryDirectory(dir=os.environ.get('TMPDIR')) as t:
        R = os.path.join(t, 'r.tsv'); open(R, 'w').write("# x\na.aac\t2\t48000\t100\t00000000000000aa\t5\t3\t0\nb.aac\t2\t48000\t50\t00000000000000bb\t7\t2\t0\n")
        cases = {'whole': ("a.aac\t2\t48000\t100\t00000000000000aa\t5\t3\t0\nb.aac\t2\t48000\t50\t00000000000000bb\t7\t2\t0\n", 0),
                 'differs': ("a.aac\t2\t48000\t100\t00000000000000aa\t5\t3\t0\nb.aac\t2\t48000\t50\t00000000000000cc\t7\t2\t0\n", 0),
                 'missing': ("a.aac\t2\t48000\t100\t00000000000000aa\t5\t3\t0\n", 1),
                 'extra': ("a.aac\t2\t48000\t100\t00000000000000aa\t5\t3\t0\nb.aac\t2\t48000\t50\t00000000000000bb\t7\t2\t0\nc.aac\t1\t48000\t1\t1\t1\t1\t0\n", 1)}
        ok = True
        for k, (body, want) in cases.items():
            D = os.path.join(t, k + '.tsv'); open(D, 'w').write(body)
            got = compare(R, D)
            print(f"self-test {k}: rc {got} -- {'as wanted' if got == want else 'WRONG'}"); ok &= got == want
        E = os.path.join(t, 'e.tsv'); open(E, 'w').write("# empty\n")
        got = compare(E, os.path.join(t, 'whole.tsv')); print(f"self-test empty reference: rc {got} -- {'as wanted' if got == 1 else 'WRONG'}"); ok &= got == 1
    print("SELF-TEST " + ("PASS" if ok else "FAIL"))
    return 0 if ok else 1

if __name__ == '__main__':
    a = sys.argv[1:]
    if a[:1] == ['--self-test']: sys.exit(self_test())
    if len(a) >= 2 and a[0] == 'ref': sys.exit(ref(a[1], a[3] if len(a) > 3 and a[2] == '--set' else 'menu'))
    if len(a) == 3 and a[0] == 'compare': sys.exit(compare(a[1], a[2]))
    print(__doc__); sys.exit(2)
