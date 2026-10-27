# FmodBake

Offline, **silent** bake of Cult of the Lamb's FMOD Studio banks into the Vita audio set. The Vita has no FMOD 2.02 runtime,
so every event is rendered on the Mac with the game's own FMOD Studio 2.02.07 and stored as plain takes plus a manifest
that a small C mixer can play. The design, the manifest spec, the container layouts and the numbers are in
`vita/notes/sound-bake.md`.

Private, local-only. The output is derived game data: it is written only under `runtime/audiobake/` (gitignored), never
committed, never uploaded, never shared.

## Silence and secrets

- **Nothing makes sound.** FMOD runs with output type `NOSOUND_NRT` only. `setOutput` must succeed before `initialize`, and
  `getOutput` must still say `NOSOUND_NRT` after it, or the process exits (code 3) before the first mix. Audio is captured
  by a pass-through DSP on the master channel group. No device is opened. `SDL_AUDIO_DRIVER=dummy` is set in-process
  anyway. `afconvert` only converts files. Quality is checked by numbers (SNR, aligned residual, RMS, seams, spectra),
  never by listening.
- **The bank key** is read in memory from the `FMODStudioSettings` asset (`FMODUnity.Settings` in `resources.assets`,
  through Shepherd.Assets) and passed straight to `Studio::System::setAdvancedSettings`. It is never printed, logged,
  stored or embedded. The log shows its length only.
- No network: Studio is initialised without `LIVEUPDATE`, and nothing else opens a socket. Game files are only read.

## Build and run

```
export DOTNET_ROOT=$HOME/.dotnet PATH="$HOME/.dotnet:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
export TMPDIR=$SHEPHERD_WORK/tmp     # main disk is nearly full; afconvert temp files go here
cd tools/FmodBake && dotnet build -c Release -o bin -p:DisableTransitiveFrameworkReferenceDownloads=true

dotnet bin/FmodBake.dll probe                 # key found (length only), FMOD up, banks, counts
dotnet bin/FmodBake.dll catalog               # + runtime/audiobake/catalog.json (metadata of every event/bus/snapshot)
dotnet bin/FmodBake.dll subset                # runtime/audiobake/subset-menu.txt: boot + Main Menu + Game Biome Intro
dotnet bin/FmodBake.dll bake menu --events ../../runtime/audiobake/subset-menu.txt
dotnet bin/FmodBake.dll bake full             # every event (~18 min; half of it the 3D renders)
dotnet bin/FmodBake.dll verify menu|full      # objective re-check of every take against the manifest
dotnet bin/FmodBake.dll keycheck <dir>...     # hygiene: scans files for the bank key's bytes, prints counts only
```

`bake <set>` options: `--events <file>` (paths, one per line; text after a TAB is ignored), `--match <regex>`,
`--limit N`, `--no-snapshots`, `--workers N` (encode threads, default 6). A re-bake replaces `runtime/audiobake/<set>/ev/`.

Diagnostics (no files written): `try <regex> [n]` renders matching events twice and prints length, stop, lead, peak, the
take-to-take residual and the metered buses; `loopdiag <event> [seconds]` prints the periodicity profile of a render;
`snapev <snapshot> <regex>` compares event output with and without a snapshot; `leakhunt [probe]` renders events in bake
order until a probe event (default `event:/material/footstep_grass`) stops working; `statediff <event>` diffs globals, buses,
channel groups and instances before/after one event; `startdiag <event>…` prints state / timeline / audibility per block
after `start()`; `spatialdiag <regex> [n] [x,z;…]` renders 3D events at positions and prints per-channel levels and the 3D model the
bake would write; `spatialtime <event> x z` prints the per-block level of a render at a position vs distance 0; `dspdiag <regex> [n]
[summary]` dumps an instance's channel-group / DSP tree (or a histogram over all 3D events); `metertest <regex> [n]` compares
renders with and without locked bus channel groups (`METERTEST_STATS=1 METERTEST_STRIDE=k` for statistics).

Besides the takes, every bake now measures, from FMOD itself (details in `vita/notes/sound-bake.md`):
- **STOPPED** per take (`stopFrame`): the frame where FMOD reported STOPPED for that render;
- **releases** of loops (`releaseMs`, `releaseStopMs`, `releaseCurve`): `stop(ALLOWFADEOUT)` on a settled render;
- **3D** (`rolloff`, `pan`): the event rendered at 23 positions (34 for randomised events) around the listener;
- **silent defaults** (`silentAtDefault`): parameter-gated events FMOD renders silent at the defaults.

Bus channel groups are never locked while events render (the game never locks them, and locking changes FMOD's output for
about a quarter of the events). The routing meters attach to the groups Studio creates on demand; only the snapshot measurement
at the end locks them.

## Files

| file | what |
|---|---|
| `Program.cs` | entry, assembly/native resolution (FMODUnity.dll and fmodstudio from the game, never copied), session setup |
| `GameKey.cs` | the bank key from `FMODUnity.Settings` (type tree if available, else the validated serialized layout) |
| `Session.cs` | FMOD Studio + Core init (NOSOUND_NRT, 48 kHz stereo, 1024-frame blocks, fixed seed, synchronous/update-driven), capture DSP, bank + sample loading |
| `Catalog.cs` | enumeration: events, snapshots, parameters (labels, flags), user properties, buses |
| `Render.cs` | isolated deterministic renders (optionally at a 3D position, or with a stop(ALLOWFADEOUT) at a frame), per-bus metering without locking (routing), snapshot bus-gain measurement with a test signal |
| `Analysis.cs` | peaks, RMS, residuals, exact-loop search (timeline length + FFT autocorrelation), crossfaded loops, seam check |
| `Encode.cs` | pcm16 WAV, IMA ADPCM WAV (encoder + decoder), AAC ADTS via afconvert + decode-back, ADTS parser |
| `Baker.cs` | the policies (takes, variants, loops, codecs), manifest.json, report.json |
| `Verifier.cs` | `verify`: containers, frame counts, loops, references, decoded RMS vs baked RMS |
| `Subset.cs` | the menu subset (scene reachability scan for event GUIDs/strings + menu/intro code literals + families) |
| `Spatial.cs` | 3D measurement (rolloff + pan from content renders at positions), `spatialdiag`, `spatialtime`, `metertest` |
| `DspDiag.cs` | `dspdiag` |
| `Try.cs` | diagnostics |

Requirements: .NET 8 SDK, the game's Steam copy (path overridable with `SHEPHERD_GAME_APP`), macOS `afconvert`. The
engine's `Shepherd.Assets` sources are compiled in (own obj/bin, same pattern as `tools/VitaPack`); nothing in
`engine/` is modified or built.
