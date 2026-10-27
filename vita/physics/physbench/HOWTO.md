# Shepherd PhysBench: what to do with it (owner one-pager)

**What it is.** A small test app for the Vita, title **"Shepherd PhysBench"** (SHPD00012). It times the physics engine
the Shepherd port will use (Box2D 2.3.1 plus our wrapper) on scenes shaped like the intro fight, a dungeon room and the
base. It also checks that the Vita computes exactly the same physics as the Mac. The physics plan's Vita budget rests on
these numbers; today they are only estimates. The app contains no game code or game data, makes no sound, uses no
network, and writes only to `ux0:data/shepherd/`.

**Your Vita must be modded first.** As of 2026-10-07 it is stock, so this waits. You need the same setup as the
Shepherd Probe: HENkaku/Ensō and VitaShell. You do **not** need libshacccg, kubridge or a network connection.
PSVshell is optional. It is used only if you already have it, for an extra 500 MHz run. Shepherd never installs it.

## Install and run

1. On the Mac, the file is `$SHEPHERD_REPO/vita/physics/physbench/out/shepherd_physbench.vpk` (about 157 KB). If it is
   missing, rebuild it with `zsh $SHEPHERD_REPO/vita/physics/physbench/build.sh` (about 20 s).
2. Copy it to the Vita (VitaShell: USB or FTP) and install it.
3. Launch **Shepherd PhysBench**. Progress scrolls on screen for about 1 to 3 minutes.
   - The CPU clock is set to 444 MHz, then 333 MHz, then 500 MHz if PSVshell is listed. Every change is logged, and the
     launch clocks are restored at the end.
   - To skip the 500 MHz run even with PSVshell, first create an empty file `ux0:data/shepherd/physbench-no500`.
4. When the screen says **DONE**, press **START** to exit. It also exits by itself after 2 minutes.
   - Holding **START** during the run stops it early (clocks are still restored).
   - If it ever freezes, hold the power button. The log keeps every line up to the step that hung, and the previous run's
     log is kept as `physbench.prev.txt`.

## What to send back

- **`ux0:data/shepherd/physbench.txt`**, always.
- `ux0:data/shepherd/physbench.prev.txt`, if a run stopped halfway.
- Optional, in the same session: also run **"Shepherd Phys T1"** (SHPD00020, file
  `$SHEPHERD_REPO/vita/physics/build/vita/shepherd_phys_t1.vpk`) and send `ux0:data/shepherd/physics-t1.txt`.

## What the log says

- **`ENV`**: the console model and the floating-point mode the Vita starts apps in (still unverified), the clocks at
  launch, and whether the Mac reference matches this build.
- **`T1 ... EXACT`**: the eight determinism tests give bit-for-bit the Mac's results. `DIFFERS` would show how far off they
  are (ulp) and where the first difference is.
- **`STEP`**: physics cost per 20 ms step for each scene. Each line also gives the Mac time and the "N× slower" ratio. The
  `pose hash ... check ok` part confirms that the Vita simulated exactly what the Mac did.
- **`CALL` / `ASTAR`**: the cost of single physics calls and of A*-style grid scans, native side only.
- **`FJ` / `NP` / `MW`**: what the second and third CPU cores can add (fork-join cost, split phases, parallel worlds).
- **`SUMMARY <MHz>`**: one line per clock with the key numbers.

Nothing on the console is changed, apart from the log files in `ux0:data/shepherd/`.
