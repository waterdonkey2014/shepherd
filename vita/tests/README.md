# vita/tests -- Shepherd's test tools (kit #7 tests track; vita/notes/kit7-tests.md has the results)

Every tool here runs on the Mac (silent: output muted and checked; sandbox saves only; the real game, Steam and real saves never touched;
nothing downloaded). Their outputs are derived game output and stay in the work dir (default $SHEPHERD_WORK/kit7/tests-work),
never in git. None of it is in any eboot: the engine hooks they use are compiled only into the Mac build (SHEPHERD_EBOOT leaves them out).

| tool | what | run |
|---|---|---|
| `macboot.sh` | one headless Mac boot (Vita profile, null backend, frame clock) in its own root with per-frame state + stream hashes; the shared runner | `macboot.sh <out> <frames> [PAD=] [HOST=1] [CACHE_IN=] [ENV=VAL ...]` |
| `fixtures/` | the recorded replay fixtures (idle, menu tour, route A, Credits, Settings -> New Game, New Game, the intro), replayed after every build, failing on the first mismatching frame with a state dump | `fixtures/run-fixtures.sh record` / `check` |
| `lockstep/rig.py` | the P2 lockstep hash rig: two runs compared frame by frame while they run, stopped at the first divergence, both dumped and diffed record by record; sources boot: / file: / tcp: (the device leg's shape) | `rig.py --a boot:<dir> --b boot:<dir>[;ENV=VAL] [--dump]` |
| `TestHost/` | the Mac test host (references the Debug engine): `boot` (the engine's Main, optionally under the PCT scheduler), `probe` (the shared probe suites on Shepherd), `mutate` (an icall fault seeded), `pct-pack`, `powercut`, `scene` | `dotnet TestHost/bin/Debug/net8.0/Shepherd.TestHost.dll ...` |
| `pct/` | seeded thread-schedule testing: `boot-seeds.sh` (real boots, the engine's threads under PCT, vs the single-threaded run), `astar-combat.sh` + `astar-combat-batch.sh` (the physics combat drive on a read-only copy of k6-physics with the A* / Spine test switches: the flake explained), `spine-hooks.py` (SHEPHERD_TEST_SPINE's hook lines for another engine tree) | see the scripts' headers |
| `probes/` | the probe suites shared with the Unity oracle (maths, formatting, JsonUtility, Transform model, RectTransform layout, Random) + the one-feature render scenes | compiled into both sides |
| `oracle/` | the Unity 2022.3.62f2 oracle project (E1's pattern: built-in modules + the editor's bundled ugui, registry blocked, batchmode players arm64 / x64), its runners and the comparison tools | `oracle/setup.sh build`; `oracle/run.sh unity|shepherd ...`; `compare.py`, `classify.py` |
| `drafted/` | machine-drafted probes and the strict filter (compile both sides, stable on real Unity, new engine coverage, catches a seeded fault) | `drafted/filter.sh` |
| `il/` | the IL conformance suite (Mono's mini tests + FpMode.cs) and the random-program fuzzer with its reducer, on Unity's Mono (editor and players), CoreCLR, and the Vita tiers under vlx (`vlx.sh`: the vlx-only hello build + run, `hello-gen.py`, `vita-compare.py`); `FpRandom.cs` (UnityRandom's formulas under both float evaluation modes) | `il/build.sh`, `il/run.sh [tiers incl. vlx]`, `il/vlx.sh`, `il/fuzz.py` |
| `powercut-e2e.sh` | damaged card caches fed to real boots: every start must run the clean start's frames | `powercut-e2e.sh [out]` |

The engine side (all Mac-only unless noted, each with its switch): `SHEPHERD_STATE_HASH` / `SHEPHERD_STATE_DUMP` (Diag/StateHash.cs),
`SHEPHERD_TEST_ASTAR` (Diag/TestAstar.cs), `SHEPHERD_TEST_SPINE` (Diag/TestSpine.cs), the PCT hooks (Shepherd.Assets/Sched.cs, used by
VitaPack's read-ahead and MtRender), and two switches that ARE in the eboots, both off by default: `SHEPHERD_UNITY_RANDOM=1`
(Icalls/UnityRandom.cs) and `SHEPHERD_STRICT_ORIGINAL=1` (Runtime/StrictOriginal.cs).
