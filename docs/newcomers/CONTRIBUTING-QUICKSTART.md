# Contributing: the quick start

This is the short version of [CONTRIBUTING.md](../../CONTRIBUTING.md). When the two differ, CONTRIBUTING.md wins.

## The rules that matter most

1. **No game files, ever.** That means no assemblies, assets, audio banks, keys, saves or packs, no rewritten DLLs, and nothing
   built from a game. Shepherd reads your own install. What it builds from it stays in git-ignored places (`runtime/`,
   `$SHEPHERD_WORK`, your Vita).
2. **No decompiled code.** A port of a game's or a third party's code, compiled shaders included, stays in your local, git-ignored
   `engine/**/Derived/`. If you have such a folder, this must pass before you open a pull request:
   ```bash
   python3 vita/shepherd/derived_leaks.py --require
   ```
3. **Fidelity first.** The game must behave and look like the original. A change that alters gameplay or the picture goes behind
   an opt-in switch, off by default, and says so.
4. **Label every number:**
   - MEASURED: on a device, with the model, firmware and clock;
   - EMULATED: in vlx / QEMU;
   - DERIVED: worked out from other numbers;
   - ESTIMATE: a guess, with how it was made.
5. **No private data.** No absolute paths from your machine, no user names, IP addresses or tokens.
6. **As low as possible, everywhere.** Aim for the lowest frame, boot and load times the fidelity rule allows. A faster frame never
   means the game itself runs faster: game time, timers and animation keep the original's pace.

## Your first change, in seven steps

1. **Read the map.** [ARCHITECTURE.md](ARCHITECTURE.md) tells you which folder does what. [GLOSSARY.md](GLOSSARY.md) explains the
   jargon (kit, icall, Derived, vlx, ...).
2. **Run the Linux checks** on a clean checkout. They need no Mac, no Vita and no game, and they are what CI runs on every pull
   request ([BUILD.md, step 0](BUILD.md#0-the-linux-checks-no-mac-no-vita-no-game)):
   ```bash
   .github/ci/linux-checks.sh
   ```
3. **Pick something small.** See the list below.
4. **Read the header** of every script and source file you touch. They explain why things are the way they are, often with the
   measurement behind it.
5. **Make the change.** Keep it small and focused. Don't reformat, rename or move code you aren't changing.
6. **Check it the way the project checks things:**
   - off-device first: the Mac engine, vlx under QEMU, unit tests;
   - then a device run, if your change can affect the device.

   A test that never failed proves nothing: add a negative control (a case that must fail) next to your test.
7. **Open a pull request.** Say what changed, how you checked it, and label every number (rule 4). CI must be green.

## Good first contributions

- **Fix `tools/VitaBudget`.** It was written against an older `Shepherd.Assets` API and does not build (CHANGELOG 0.4.0). CI skips
  it for now; once it builds, remove it from the skip list in `.github/ci/linux-checks.sh`.
- **First-game seams.** [docs/PORTING-ANOTHER-GAME.md](../PORTING-ANOTHER-GAME.md) lists values still hard-wired to the first game
  (product name, window title, splash clip, time settings, data paths). Make one read the game's own data instead.
- **Scripts that assume one machine.** Many build scripts assume macOS on Apple silicon (for example
  `vita/toolchain/build-cross.sh`, or Apple-only linker flags in `vita/physics/CMakeLists.txt`). Making one work elsewhere, without
  changing what it does on the Mac, helps everyone. CI on Linux already works around a few of these; its comments say where.
- **Device reports** from other Vita models and firmwares (PCH-1000, PS TV). Label them MEASURED, with model, firmware and clock.
- **Missing icalls** your game needs. The engine lists them in `runtime/logs/missing-icalls.txt`.

## Where things go

| You have... | It goes... |
|---|---|
| Engine code (C#) | `engine/Shepherd.Engine/<area>/` |
| A tool that runs on your computer | `tools/<Name>/` |
| Vita-side C code | `vita/shepherd/` (host), `vita/physics/`, `vita/audio/`, ... |
| A test | next to what it tests; for C, follow `vita/shepherd/mac-check/test_*.c` (one file, build line in its header, exit 0 = pass) |
| Something you learned about the Vita | the companion Claude plugin's knowledge base ("Shepherd for PS Vita"), with the same evidence rules |
| Anything built from a game | **nowhere in git**: `runtime/` or `$SHEPHERD_WORK` |

## How CI checks a pull request

`.github/workflows/linux-checks.yml` runs `.github/ci/linux-checks.sh` on Linux. It covers:

- the derived-code leak check (vacuous without a local `Derived/`) and a check that no git-ignored file is tracked;
- that Python files compile and shell scripts parse;
- the C unit tests;
- physics against its goldens;
- the audio library build;
- every C# project that needs no game DLLs, and the managed cache test;
- the devlog receiver's tests.

It cannot run anything that needs your game, vitasdk, the runtime fork or macOS. Those checks are yours to run, and your pull
request should say which ones you ran.
