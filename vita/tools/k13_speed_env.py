#!/usr/bin/env python3
"""k13_speed_env.py -- device kit #13, track speed (vita/notes/kit13-speed.md): the speed track's env-file ARMS for the owner's run order.

Writes <out>/<folder>/<env file> for every arm below (the kit #12 format: comment lines first -- the host skips '#' lines; never a comment
after a value -- then the switches, then the arm's own log folder). The integration copies the folders it puts in the README into the kit's
card-env tree (kit13-env/) and names each STEP; nothing here installs anything.

Measure arms (env_gl_measure.txt): Shepherd Measure presses for itself (pad.txt, frame clock), so every arm sees the same screens at the same
frames -- compare each with the session's Measure-as-shipped steps (first and last) on the [sim] window, frames 1677..1776 (the menu
buttons; host-relative 1301..1400 after the 376-frame splash re-base).
  m-prof          the idle menu PROFILED (kit #7's profiler, time mode, its window = the [sim] window): read with vita/tools/k13_idle_profile.py
                  -- a split, not a timing step (the profiler's own cost is in it);
  m-k13off        the speed track's ENGINE rows off (SHEPHERD_K13_SPEED=0) with FloatPc kept as shipped (SHEPHERD_K10_FPSCR=pc: the master
                  also drops the FloatPc default, and the float rules must not be a second difference) -- the A/B of the track's engine rows;
  m-rect-k12      the rect size checks as kit #12 made them (SHEPHERD_K13_RECTDIRTY=0 alone) -- the RECTDIRTY row's own A/B;
  m-masks-k12     the stencil masks' pop lookups as kit #12 made them (SHEPHERD_K13_MASKS=0 alone; MASKDETACH is opt-in and off) -- the
                  MASKS row's own A/B (the run's "[k13] masks" lines say whether the window skipped at all);
  m-fpscr-k12     OPTIONAL: the Vita's own float rules on the menu (SHEPHERD_K10_FPSCR=0) -- FloatPc's share of a menu difference, if the
                  integration wants every default isolated on the fps window (kit #10's device bench: x1.00);
  m-gpu1-n / -u / -r / -x   kit #11 gpu1's exact rows with the slow call fixed: NOPIX alone, UNIFORMS alone, REUSE alone, all three
                  (kit #11's X1 had all three at once and the NOPIX interpreter calls).
Play MT arms (env_play.txt), each with the scripted A/B pad (the same pad in both parts; the owner presses nothing):
  pace-a / pace-b Frame Pacing forced ON / off for that start (SHEPHERD_K13_PACING; the saved row untouched): the menu's frame-time p95 from
                  the end line "[k8] frame times" and the P lines (pad <menu pad>, stop at m+700);
  fpscr-off-walk  THE OWNER'S FloatPc A/B ARM: the Vita's own float rules (SHEPHERD_K10_FPSCR=0) on the WALK (pad <walk pad>: the walk
                  step's own pad -- the integration writes it with the walk pass, then both parts run the same pad and stop line) --
                  compare with the shipped walk step (PASS END, the frame times, any FAIL / assertion, the owner's look);
  fpscr-off       the same on New Game (pad <newgame pad>): a fallback when the walk is not in the run order (the walk is the arm the owner
                  decided: physics runs there).
  gc-split / gc-nscene / gc-ends2 / gc-conc0   kit #9's GC arms on New Game (pad <newgame pad>): M6 split nursery, per-scene nursery,
                  M8 two-ended arena, lever 32 (concurrent mark) OFF -- memory levers: compare the K9 GC end line, the MEM lines and New
                  Game's PASS with the shipped New Game step.
usage: k13_speed_env.py <out dir> [--menu-pad PATH] [--newgame-pad PATH] [--walk-pad PATH] [--card-root ux0:data/shepherd/kit13-env]"""
import argparse, os

ap = argparse.ArgumentParser()
ap.add_argument('out')
ap.add_argument('--card-root', default='ux0:data/shepherd/kit13-env')
ap.add_argument('--menu-pad', default='/ux0:data/shepherd/kit13-env/pads/ab-menu.txt')
ap.add_argument('--newgame-pad', default='/ux0:data/shepherd/kit13-env/pads/ab-newgame.txt')
ap.add_argument('--walk-pad', default='/ux0:data/shepherd/kit13-env/pads/ab-walk.txt')
a = ap.parse_args()
M, P = 'env_gl_measure.txt', 'env_play.txt'
COPY = 'VitaShell: copy this file from {root}/{f}/ into the TOP of ux0:data/shepherd/ (replace: yes); DELETE it after the step.'

def w(folder, name, what, lines):
    d = os.path.join(a.out, folder); os.makedirs(d, exist_ok=True)
    body = [f'# device kit #13 (speed), {what}', '# ' + COPY.format(root=a.card_root, f=folder)] + lines + \
           [f'SHEPHERD_LOG_DIR=/ux0:data/shepherd/runtime/logs/k13-{folder}']
    with open(os.path.join(d, name), 'w', newline='\n') as f: f.write('\n'.join(body) + '\n')

w('m-prof', M, 'Shepherd Measure with the idle menu PROFILED on the Vita (kit #7 profiler, its window = the [sim] window). Not a timing '
  'step: read it with k13_idle_profile.py.', ['SHEPHERD_K7_PROF=time,win=1677:1776'])
w('m-k13off', M, 'Shepherd Measure with the kit #13 speed track\'s engine rows OFF (kit #12\'s menu code) and the PC float rules kept as '
  'shipped -- compare with Measure as shipped.', ['SHEPHERD_K13_SPEED=0', 'SHEPHERD_K10_FPSCR=pc'])
w('m-rect-k12', M, 'Shepherd Measure with the rect size checks as in kit #12 (SHEPHERD_K13_RECTDIRTY=0 only) -- compare with Measure as '
  'shipped.', ['SHEPHERD_K13_RECTDIRTY=0'])
w('m-masks-k12', M, 'Shepherd Measure with the stencil masks\' pop lookups as in kit #12 (SHEPHERD_K13_MASKS=0 only) -- compare with '
  'Measure as shipped.', ['SHEPHERD_K13_MASKS=0'])
w('m-fpscr-k12', M, 'OPTIONAL: Shepherd Measure with the Vita\'s own float rules (SHEPHERD_K10_FPSCR=0 only) -- compare with Measure as '
  'shipped.', ['SHEPHERD_K10_FPSCR=0'])
for k, lines, what in (('n', ['SHEPHERD_K11_NOPIX=1'], 'NOPIX (draws that cannot change a pixel skipped; kit #13 fix: no interpreter calls)'),
                       ('u', ['SHEPHERD_K11_UNIFORMS=1'], 'UNIFORMS (shader values sent only when they change)'),
                       ('r', ['SHEPHERD_K11_REUSE=1'], 'REUSE (still world vertices reused)'),
                       ('x', ['SHEPHERD_K11_UNIFORMS=1', 'SHEPHERD_K11_NOPIX=1', 'SHEPHERD_K11_REUSE=1'], 'all three exact gpu1 rows')):
    w(f'm-gpu1-{k}', M, f'Shepherd Measure with kit #11 gpu1\'s {what} -- compare with Measure as shipped (kit #11 X1 was all three with the '
      'slow call: +4.85 ms render).', lines)
for ab, on in (('a', True), ('b', False)):
    w(f'pace-{ab}', P, f'the FRAME PACING menu A/B, part {ab.upper()} (Frame Pacing forced {"ON" if on else "off"} for this start; the saved '
      'Settings row is not changed). The app presses for you: tap the bubble, put the Vita down, press NOTHING until green.',
      [f'SHEPHERD_PAD_FILE={a.menu_pad}', 'SHEPHERD_STOP_AT=m+700', f'SHEPHERD_K13_PACING={"on" if on else "off"}'])
w('fpscr-off-walk', P, 'Cult of the Lamb (play MT), THE WALK with the Vita\'s own float rules (SHEPHERD_K10_FPSCR=0) -- the OFF arm of '
  'FloatPc on the walk, as decided (the PC float rules are ON by default in kit #13); the same pad as the shipped walk step. The app presses '
  'for you: press NOTHING until green.', [f'SHEPHERD_PAD_FILE={a.walk_pad}', 'SHEPHERD_K10_FPSCR=0'])
w('fpscr-off', P, 'Cult of the Lamb (play MT), New Game with the Vita\'s own float rules (SHEPHERD_K10_FPSCR=0) -- the OFF arm of FloatPc '
  'on New Game (a fallback: the walk arm is the decided one). The app presses for you: press NOTHING until green.',
  [f'SHEPHERD_PAD_FILE={a.newgame_pad}', 'SHEPHERD_K10_FPSCR=0'])
for k, line, what in (('split', 'SHEPHERD_GC_SPLIT=1', 'M6, the split nursery'), ('nscene', 'SHEPHERD_GC_NURSERY=scene', 'the per-scene nursery'),
                      ('ends2', 'SHEPHERD_ARENA_ENDS=2', 'M8, the two-ended arena'), ('conc0', 'SHEPHERD_GC_CONC=0', 'lever 32 (the concurrent collector) OFF')):
    w(f'gc-{k}', P, f'Cult of the Lamb (play MT), New Game with kit #9\'s GC arm: {what} -- a memory A/B against the shipped New Game step. '
      'The app presses for you: press NOTHING until green.', [f'SHEPHERD_PAD_FILE={a.newgame_pad}', line])
print(sorted(os.listdir(a.out)))
