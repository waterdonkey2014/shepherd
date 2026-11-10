#!/usr/bin/env python3
"""Kit #12 (track menusb): the Version history panel's text file (Settings > PS Vita > About > Version History).

One source, as the owner asked (roadmap-kits.md, 2026-10-31): the plain-language "Kit by kit" table of vita/notes/progress-log.md
(the same text as the release notes). Writes vita/shepherd/version-history.txt -- English, newest first, one line per version:

    @ <version> | <date> | <what happened>. Idle Main Menu: <...>. Boot to menu: <...>.

Each entry is kept WHOLE (review fix, finding 5: the owner's panel shows what's new, what was fixed and the numbers; a cut at 300
characters lost them): the Settings description strip grows to its text, up to ~12 lines (Runtime/VitaToggles.K12.cs StripMax), which
holds MAX characters -- an entry over MAX is an error (shorten its progress-log row), never cut silently. The engine reads the file from
the VPK (app0:version-history.txt; vita/shepherd/build.sh packs it) -- Runtime/K12View.cs K12History.
usage: k12_history.py [progress-log.md] [out]   |   k12_history.py --check (the committed file = a fresh run)
"""
import re, sys, os

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
SRC = os.path.join(REPO, 'vita', 'notes', 'progress-log.md')
OUT = os.path.join(REPO, 'vita', 'shepherd', 'version-history.txt')
MAX = 900   # characters of one entry's text: ~12 lines of ~86 at the strip's size 20 (StripMax 320 canvas units)


def plain(s):
    s = re.sub(r'\*\*|`', '', s).strip()
    return re.sub(r'\s+', ' ', s)


def make(src):
    rows, intable = [], False
    for line in open(src, encoding='utf-8'):
        if line.startswith('| Version | Kit | Date |'):
            intable = True
            continue
        if intable:
            if not line.startswith('|'):
                break
            cells = [c.strip() for c in line.strip().strip('|').split('|')]
            if len(cells) < 6 or cells[0].startswith('---'):
                continue
            ver, kit, date, what, idle, boot = cells[:6]
            text = plain(what).rstrip('.') + '.'
            if plain(idle) not in ('', '--'):
                text += ' Idle Main Menu: ' + plain(idle) + '.'
            if plain(boot) not in ('', '--'):
                text += ' Boot to menu: ' + plain(boot) + '.'
            rows.append((plain(ver), plain(date), text))
    # one entry per version (a version's "Built, not run yet" row gives way to its device-run row), newest first by number
    best = {}
    for r in rows:
        if r[0] not in best or best[r[0]][2].startswith('Built, not run yet') and not r[2].startswith('Built, not run yet'):
            best[r[0]] = r
    key = lambda v: tuple(int(x) for x in re.findall(r'\d+', v)[:2])
    rows = sorted(best.values(), key=lambda r: key(r[0]), reverse=True)
    head = '# Shepherd version history -- made by vita/tools/k12_history.py from vita/notes/progress-log.md (do not edit by hand)\n'
    return head + ''.join('@ %s | %s | %s\n' % r for r in rows), len(rows)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    src = args[0] if args else SRC
    out = args[1] if len(args) > 1 else OUT
    text, n = make(src)
    if n == 0:
        print('k12_history: no rows found in %s (the "Kit by kit" table) -- FAIL' % src)
        return 1
    long = [l.split(' | ')[0] for l in text.splitlines() if l.startswith('@ ') and len(l.split(' | ', 2)[2]) > MAX]
    if long:
        print('k12_history: FAIL -- entries over %d characters (the strip shows at most that; shorten their progress-log rows): %s'
              % (MAX, ', '.join(x[2:] for x in long)))
        return 1
    if '--check' in sys.argv:
        ok = os.path.exists(out) and open(out, encoding='utf-8').read() == text
        print('k12_history --check: %s (%d versions)' % ('PASS' if ok else 'FAIL: the committed file differs from a fresh run', n))
        return 0 if ok else 1
    open(out, 'w', encoding='utf-8').write(text)
    print('k12_history: %d versions -> %s' % (n, out))
    return 0


if __name__ == '__main__':
    sys.exit(main())
