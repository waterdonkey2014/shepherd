#!/usr/bin/env python3
"""kit13_bootc_env.py -- SHEPHERD-VITA device kit #13, track bootc (vita/notes/kit13-bootc.md section 6): the bootc A/B pairs' env files and the
scripted pads, for the kit's extras/card-env/kit13-env/ (the integration copies or calls this; every file is our own text: switches and presses).

Every env file: comment lines first (the host skips '#' lines; never a comment after a value), then the switches, then its own log folder.
Both arms of a pair name the SAME pad file (check-kit.sh refuses a pair whose arms differ) and the same SHEPHERD_STOP_AT.

  pads/ab-menu.txt     the menu-only pairs (= kit #12's): the title press, one menu move after the [sim] window, stop at m+700.
  pads/ab-newgame.txt  the pairs that need New Game -- kit #13: SCREEN-STATE WAITS (Diag/MenuClock.cs, "@Screen+N"): kit #12's device run pressed
                       the slot at a fixed frame and in 2 of 4 cleanup starts that press found the game's DLC slots (the menu's selection had
                       moved: the down / up before Play) and the owner pressed by hand. Now no menu move before Play, and the slot press waits
                       for the game's own "SaveSlots shown" (+30 frames); should the DLC slots still show, the pad backs out and takes Play.
  ab-bootseq-a1 / b1 / a2 / b2      (00193, the MT launcher; ab-menu) A = as shipped: the boot sequence (kit #13: the Massive Monster clip's
                       decoder fixed, so the sequence runs); B = SHEPHERD_K12_BOOTSEQ=0 (kit #11's boot: the game's own splash). Kit #12's
                       pair was void (the clip never played: both arms ran kit #11's boot).
  ab-bootscreen-a1 / b1 / a2 / b2   (00095, the game AOT eboot; ab-menu) A = as shipped: the boot loading screen (kit #13: the emblem
                       crossfade); B = SHEPHERD_K10_BOOTSCREEN=0 (black until the game's first frame, kit #11's default). Kit #12's warm boots
                       of the eboot apps were slower than kit #11's (game AOT ~104.5 vs 98.2 s) while the launchers' were not: this pair says
                       how much of it is the loading screen (its thread inflates from uncached video memory on USER_1 while Mono boots).

usage: kit13_bootc_env.py <kit dir>   (writes <kit>/extras/card-env/kit13-env/...; prints the folders)
"""
import os, sys

PADM = 'SHEPHERD_PAD_FILE=/ux0:data/shepherd/kit13-env/pads/ab-menu.txt'
STOP = 'SHEPHERD_STOP_AT=m+700'
COPY = 'VitaShell: copy this file from ux0:data/shepherd/kit13-env/{f}/ into the TOP of ux0:data/shepherd/ (replace: yes); DELETE it after the step.'

AB_MENU = """# kit #13 A/B pad script -- BOTH arms of an A/B pair name this file (check-kit.sh refuses a pair whose arms differ). The engine reads it
# (Icalls/VitaPad.cs: frame:button[:holdFrames]; m+N = N frames after the menu's scene arrived; '#' = comment). The menu-only pairs end by
# themselves (SHEPHERD_STOP_AT=m+700 in the env file). The same presses as kit #12's ab-menu.txt.
m+90:cross      # the title press (Press X to Start takes only cross). The menu buttons settle ~45-60 frames later
m+620:down      # one menu move (the menu-is-interactive check), after the [sim] window 1301-1400
m+640:up        # back to the first button
"""
AB_NEWGAME = """# kit #13 A/B pad script for the pairs that need New Game. Kit #12's device run: the slot press at a fixed frame found the game's DLC slots in
# 2 of 4 starts (the menu's selection had moved) and the owner pressed by hand. Kit #13: no menu move before Play, and the slot press WAITS FOR
# THE SCREEN -- "@SaveSlots+30" = 30 frames after the game showed its save slots (its own "SaveSlots_SHOWN" announcement; Diag/MenuClock.cs).
m+90:cross            # the title press (Press X to Start takes only cross)
m+760:cross           # the first button (Play) -> the save slots, after the [sim] window
@SaveSlots+30:cross   # the first slot (empty in the sandbox saves) -> New Game; goes green by itself after the quotes
@DLCSlots+30:circle   # (only if the DLC slots showed instead: back to the menu,
@DLCSlots+60:up       #  up to Play,
@DLCSlots+80:cross    #  Play -> the save slots: the slot press above follows)
"""

def w(E, folder, name, what, lines):
    d = os.path.join(E, folder); os.makedirs(d, exist_ok=True)
    body = [f'# device kit #13, {what}', '# ' + COPY.format(f=folder)] + lines + [f'SHEPHERD_LOG_DIR=/ux0:data/shepherd/runtime/logs/k13-{folder}']
    open(os.path.join(d, name), 'w', newline='\n').write('\n'.join(body) + '\n')

def main(kit):
    E = os.path.join(kit, 'extras/card-env/kit13-env')
    os.makedirs(os.path.join(E, 'pads'), exist_ok=True)
    open(os.path.join(E, 'pads/ab-menu.txt'), 'w', newline='\n').write(AB_MENU)
    open(os.path.join(E, 'pads/ab-newgame.txt'), 'w', newline='\n').write(AB_NEWGAME)
    P = 'env_play.txt'
    for ab in ('a1', 'b1', 'a2', 'b2'):
        b = ab[0] == 'b'
        w(E, f'ab-bootseq-{ab}', P, f'the BOOT SEQUENCE A/B on Cult of the Lamb (play MT, launcher), part {ab.upper()} '
          f'({"kit #11 boot: SHEPHERD_K12_BOOTSEQ=0" if b else "as shipped: the Devolver video, the Massive Monster logo, the loading screen until the menu"}). '
          'The app presses for you (the pad script, the same in both parts): tap the bubble, put the Vita down, press NOTHING until green.',
          [PADM, STOP] + (['SHEPHERD_K12_BOOTSEQ=0'] if b else []))
        w(E, f'ab-bootscreen-{ab}', P, f'the BOOT LOADING SCREEN A/B on Cult of the Lamb (play, game AOT), part {ab.upper()} '
          f'({"no loading screen: SHEPHERD_K10_BOOTSCREEN=0, black until the game draws" if b else "as shipped: the loading screen with the emblem crossfade"}). '
          'The app presses for you (the pad script, the same in both parts): tap the bubble, put the Vita down, press NOTHING until green.',
          [PADM, STOP] + (['SHEPHERD_K10_BOOTSCREEN=0'] if b else []))
    print(sorted(os.listdir(E)))
    return 0

if __name__ == '__main__':
    if len(sys.argv) != 2:
        print(__doc__); sys.exit(2)
    sys.exit(main(sys.argv[1]))
