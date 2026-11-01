# Contributing to Shepherd

Shepherd was released early on purpose: people porting other games -- with Claude, other agents or by hand -- fill the gaps and send
findings back. Issues and pull requests are welcome, including failed experiments.

## Hard rules
1. **No game files, ever**: no assemblies, assets, audio banks, keys, saves, packs, rewritten DLLs or anything built from a game.
2. **No decompiled code.** A port of a game's or third party's code (or compiled shaders) stays in your local, git-ignored
   `engine/**/Derived/` folder. `python3 vita/shepherd/derived_leaks.py --require` must pass before you open a pull request: it fails
   when content from your Derived files appears in a file git would carry.
3. **Fidelity first.** The game must behave and look like the original. A change that alters gameplay or the picture goes behind an
   opt-in switch, off by default, and says so.
4. **Label every number**: MEASURED (device, with model / firmware / clock), EMULATED (vlx / QEMU), DERIVED or ESTIMATE.
5. **No private data**: no absolute paths from your machine, user names, IP addresses or tokens.
6. **As low as possible, everywhere.** Frame time in every scene, boot, loads and transitions aim for the lowest the fidelity rule
   allows; 60 / 30 fps targets are ceilings to meet, not goals to stop at, and small exact wins are welcome because they stack. Faster
   frames and loads never mean the game itself runs faster: game time, timers and animation pace stay the original's.

## Good first contributions
- The game-specific seams listed in [docs/PORTING-ANOTHER-GAME.md](docs/PORTING-ANOTHER-GAME.md): turn hard-wired first-game values
  into values read from the game's own data.
- Build scripts that still assume one machine: make them work from any checkout.
- Device reports from other Vita models / firmwares (PCH-1000, PS TV).
- Missing icalls your game needs (the engine logs them to `runtime/logs/missing-icalls.txt`).

## How changes are checked
Off-device first (the Mac engine, vlx under QEMU, unit tests), then a device run. A test that never failed proves nothing: add a
negative control. Device-only behaviour the harness doesn't model is a harness bug -- fix the harness too.

## Knowledge
Findings about the Vita itself (hardware, gotchas, techniques) belong in the companion Claude plugin's knowledge base
("Shepherd for PS Vita"), with the same evidence rules.
