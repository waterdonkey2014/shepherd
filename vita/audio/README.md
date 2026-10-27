# shepaudio

A small portable C99 mixer that plays the baked FMOD event set (`runtime/audiobake/<set>/manifest.json`, the shared spec
in `vita/notes/sound-bake.md`) on the PS Vita, with no FMOD runtime. The design, the measurements and the risks are in
`vita/notes/sound-mixer.md`.

Private, local-only. Nothing here contains game data. Renders of the real bake are derived game data: they go under
`runtime/audiobake/` (gitignored) only, never into git, never uploaded.

**Silence.** Nothing in this directory opens an audio device on the Mac. The Mac build has no real-time output backend.
Its only audio framework calls are AudioToolbox `AudioConverter*` (AAC decode to memory). Tests and renders write WAV files
and are checked by numbers: bit-exactness, RMS, Goertzel and least-squares tone fits, cross-correlation, sample-step bounds.
Nothing is ever played.

## Layout

| path | what |
|---|---|
| `include/shepaudio.h` | the public C API (opaque handles, P/Invoke-friendly) |
| `src/sa_system.c` | instances and their state machine, take choice, voice allocation, stealing, virtual voices, 3D, buses, snapshots, notifications |
| `src/sa_mixer.c` | command queue consumer, voice rendering (gather, resample, gain-ramped accumulate), NEON fast paths, tails, limiter |
| `src/sa_stream.c` | streamed takes: IO thread, ring buffers, AAC seek with pre-roll, ADTS index, loop unrolling |
| `src/sa_cache.c` | short-take cache: loading, decode-on-load, LRU under a byte budget, refcounts |
| `src/sa_codec.c` | containers (WAV, CAF, AIFF/AIFC, ADTS, raw) and IMA ADPCM (MS and Apple IMA4, bit-exact) |
| `src/sa_manifest.c`, `src/sa_json.c` | manifest to tables (hash lookups by path and GUID) and a small in-situ JSON parser |
| `src/sa_aac_apple.c` / `sa_aac_vita.c` / `sa_aac_none.c` | AAC decoders: AudioToolbox (Mac tests), sceAudiodec (Vita), none |
| `src/sa_out_file.c` / `sa_out_vita.c` | output backends: offline WAV (Mac) and the sceAudioOut thread (Vita) |
| `src/sa_plat_posix.c` / `sa_plat_vita.c` | mutex, thread, sleep and clock (pthreads, or SceKernel LwMutex and threads) |
| `tools/sa_test.c` | unit tests, thread stress and CPU bench on synthetic fixtures |
| `tools/sa_render.c` | scenario renderer: a JSON script of timed API calls to a WAV plus a JSON log (for the verifier) |
| `tools/sa_check.c` | load-and-play check of a real bake set (numbers only) |
| `vita/linkcheck.c` | Vita link check: every entry point against the real SceAudio, SceAudiodec and SceKernel stubs |
| `scenarios/` | example scenario scripts for the synthetic set |

## Build and run

```
vita/audio/build.sh            # mac + vita + sanitizer builds (TMPDIR=$SHEPHERD_WORK/tmp when present)
vita/audio/build.sh mac        # build/mac/{libshepaudio.dylib, libshepaudio.a, sa_test, sa_render, sa_check}
vita/audio/build.sh vita       # build/vita/libshepaudio_vita.a + linkcheck.elf/.velf (link check only, nothing runs)
vita/audio/build.sh san        # build/san/sa_test_{address,thread}  (ASan+UBSan, TSan)

cd vita/audio
build/mac/sa_test              # all: unit + thread stress + bench   (unit | stress | bench, --quick)
build/san/sa_test_thread stress
build/mac/sa_render --manifest build/fixtures/manifest.json --script scenarios/selftest-basic.json --out build/selftest.wav
build/mac/sa_check --manifest ../../runtime/audiobake/menu/manifest.json [--wav ../../runtime/audiobake/_mixer/x.wav]
```
`sa_test` generates its fixtures in `build/fixtures/`. They are synthetic: sines, noise and counters, plus `afconvert`
encodes and reference decodes of those. `sa_check --wav` refuses any path outside `runtime/audiobake/`.

Scenario ops for `sa_render`: `create start play stop release set_param set_volume set_pitch set_paused set_3d listener
bus_volume bus_mute bus_paused bus_stop_all snapshot_start snapshot_stop global_param master expect_state mark`.
- Each call is `{"t":ms,"op":…,"id":…}`.
- A call applies at the first game frame whose time is at or after `t`. `updateHz` sets the game frame rate, default 60.
- The log has every notification (frame and ms), every `expect_state` result, call errors and the final stats. The exit
  code is non-zero if any expect or call failed.

## API

All `sa_*` calls are on `sa_system *`. Results: `SA_OK` = 0, negative = error. Instance handles are 32-bit (12-bit index +
20-bit generation), and 0 is never valid.

| group | functions |
|---|---|
| system | `sa_config_defaults`, `sa_init(manifest, rate)`, `sa_init_ex`, `sa_shutdown`, `sa_update` (once per game frame), `sa_mix(float*, frames)`, `sa_mix_s16`, `sa_set_master_volume`, `sa_set_listener(pos, vel, fwd, up)`, `sa_set_global_param`/`get`, `sa_get_stats`, `sa_set_poll_mask`, `sa_poll`, `sa_get_mix_clock`, `sa_last_error`, `sa_version` |
| descriptions | `sa_event_count`, `sa_event_find(path or "{guid}")`, `sa_event_find_guid(16 bytes)`, `sa_event_get_info`, `sa_event_get_param`, `sa_event_find_param`, `sa_event_preload(wait_ms)`, `sa_event_unload` |
| instances | `sa_event_create(path or guid)`, `_guid`, `_index`, `sa_instance_start`, `_stop(allow_fadeout)`, `_release`, `_set_param(name, v)`, `_set_param_index`, `_get_param`, `_set/get_volume`, `_set/get_pitch`, `_set/get_paused`, `_set_3d(pos, vel)`, `_get_playback_state` (lock-free), `_is_valid` (lock-free), `_get_event`, `_get_timeline_ms`, `_set_callback(fn, mask)`, `_set/get_user_data` |
| buses | `sa_bus_count`, `sa_bus_find`, `sa_bus_path`, `sa_bus_set_volume`/`get`, `sa_bus_set_mute`, `sa_bus_set_paused`, `sa_bus_stop_all(allow_fadeout)` (by path) |
| snapshots | `sa_snapshot_start(path)` (create + start), `sa_snapshot_stop(h, fade)` (stop + release). `sa_event_create("snapshot:/…")` works too. A snapshot's parameters (the game's volume sliders) go through `sa_instance_set_param`, and instance volume = intensity |
| backends | `sa_file_render(path, frames, block, float)` (Mac); `sa_vita_out_start(cfg)`, `sa_vita_out_stop` (Vita) |

What the mixer takes from the bake's optional fields (`vita/notes/sound-bake.md` has the spec, `vita/notes/sound-mixer.md` the numbers):
- `takes[].stopFrame`: STOPPED is reported when the take reaches that frame, as FMOD did. Audio after it (bus/reverb tail)
  plays on as an **orphan** voice that follows its bus's gain and pause and frees itself; if the take ends first, the timeline
  runs on silently until the frame.
- `releaseCurve` / `releaseStopMs`: `stop(allow_fadeout)` follows the measured release curve (sample-accurate in the mixer) and
  reports STOPPED after `releaseStopMs`; the rest of the fade plays on as an orphan. Events without one use `fadeout_ms`.
- `silentAtDefault` (or no take at the defaults, read the same way): the event has a silent group, so at those parameter values
  it plays nothing (PLAYING for loops; one-shots stop after `silentStopFrame`/`lengthMs`); a parameter change into or out of it
  fades over `param_xfade_ms`.
- `rolloff` + `pan`: per-output-channel 3D gains from FMOD's measured response. Without them: inverse-tapered rolloff and a
  built-in pan that keeps the far channel at FMOD's level (about -21 dB at 90°), never 0.

The playback state numbers are FMOD's `PLAYBACK_STATE`: PLAYING 0, STOPPED 2, STARTING 3, STOPPING 4. Notification bits are
`EVENT_CALLBACK_TYPE`: STARTING 0x4, STARTED 0x8, RESTARTED 0x10, STOPPED 0x20, DESTROYED 0x2, REAL_TO_VIRTUAL 0x8000,
VIRTUAL_TO_REAL 0x10000. Both were checked against the game's FMODUnity.dll by reflection.

### Mapping for the C# bridge (Shepherd.Audio.Fmod + ShepAudio; written in Stage C, vita/notes/m1-audio.md)

The bridge exists: `engine/Shepherd.Engine/Audio/ShepAudio.cs` binds the library through plain function pointers (the Vita host's
`shepaudio_proc` table, or a dylib on the Mac) and `Audio/Fmod.cs` routes the Vita profile's FMOD stubs to it when
`SHEPHERD_AUDIO_BAKE` is set. It uses the entry points added for it (no struct whose layout differs between 32 and 64 bits):
`sa_init_opts(manifest, "key=value,...", alloc, free, user)`, `sa_get_stats_v(sys, double[], n)` (`SA_STATV_*` order),
`sa_instance_set_timeline_ms` (FMOD setTimelinePosition: before start / right after it the take starts there, while playing the
take restarts there with a de-click), and the incremental file output `sa_file_open/write/close` (the engine mixes each game
frame's audio after its Studio update; never a device). Stats gained `limiter_frames`, `limiter_min_gain`, `pre_limiter_peak`,
`cache_peak_bytes`, `io_seconds`. Snapshots: of several live instances of the SAME snapshot, the most recently started one has
priority (FMOD's rule; an older copy keeps weight 1 - the newest's level), so two copies of a volume snapshot do not stack.
`tools/engine_wav_check.py` checks the engine's boot WAVs; `engine-check.sh` is Stage C's regression. Its A/B (mute-run) checks
tolerate the Mac's AudioToolbox AAC decode, which differs by 1 LSB between processes with different call sequences: isolation
allows 1 LSB per playing AAC stream (0 where none plays) and onsets come from a least-squares alignment of an isolated render, not
from the first non-zero sample (`vita/notes/m1-audio.md` §11).

The table below is the mapping as designed before the bridge; the bridge follows it (STOP_MODE inverted, PARAMETER_ID -> name
-> index, notifications polled after `sa_update` and delivered as the FMOD callbacks, unknown events on the silent stub).

| FMOD (FMODUnity P/Invoke) | shepaudio |
|---|---|
| `Studio_System_Create/Initialize/LoadBank*` | one `sa_init_ex` at boot (the banks are already baked; bank calls stay as today's stubs) |
| `Studio_System_Update` | `sa_update`, then `sa_poll` → invoke the managed `EVENT_CALLBACK` delegates |
| `GetEvent(path)` / `GetEventByID(guid)` / `LookupID` | `sa_event_find` / `sa_event_find_guid` (GUID = FMOD.GUID's 16 bytes) |
| `EventDescription_CreateInstance` | `sa_event_create_index`; unknown events keep the silent stub path |
| `EventDescription_IsOneshot/Is3D/GetLength/GetMinMaxDistance/GetParameterDescription*` | `sa_event_get_info`, `sa_event_get_param` |
| `EventDescription_LoadSampleData/UnloadSampleData` | `sa_event_preload(e, 0)` / `sa_event_unload` |
| `EventInstance_Start/Stop(mode)/Release` | `sa_instance_start` / `sa_instance_stop(h, mode == ALLOWFADEOUT)` (FMOD ALLOWFADEOUT = 0, IMMEDIATE = 1) / `sa_instance_release` |
| `EventInstance_GetPlaybackState` | `sa_instance_get_playback_state` (lock-free, `[SuppressGCTransition]`-safe) |
| `EventInstance_SetParameterByName/ByID` | `sa_instance_set_param` / `_set_param_index` (the bridge maps PARAMETER_ID to the index) |
| `EventInstance_Set3DAttributes(ATTRIBUTES_3D)` | `sa_instance_set_3d(position, velocity)` |
| `EventInstance_SetVolume/SetPitch/SetPaused/GetTimelinePosition` | the matching `sa_instance_*` |
| `EventInstance_SetCallback(cb, mask)` | `sa_instance_set_callback(h, NULL, mask)` + poll (recommended), or a native fn pointer |
| `Studio_System_SetListenerAttributes` | `sa_set_listener` (left-handed like Unity: right = up × forward) |
| `Studio_System_SetParameterByName` | `sa_set_global_param` |
| `Bus_SetVolume/SetMute/SetPaused/StopAllEvents` | `sa_bus_*` (by path; `Bus_GetPath` is the stub's) |
| `VCA_*` | nothing to do: the bake has 0 VCAs |

## Threading and blocking (Mono coop threads on the Vita)

Three kinds of threads touch the library:
- the **game** thread(s) call the API;
- the **output** thread calls `sa_mix*`;
- the **IO** thread, internal, reads files and decodes streams. It is absent with `SA_FLAG_SYNC_IO`.

The output and IO threads are pure native threads. They never attach to Mono, never touch managed memory and never call
callbacks, so the GC never needs to suspend them.

| call | blocks? | notes |
|---|---|---|
| `sa_instance_get_playback_state`, `sa_instance_is_valid`, `sa_get_mix_clock` | never | lock-free atomic reads; safe with `[SuppressGCTransition]` |
| every other `sa_instance_*`, `sa_event_create*`, `sa_bus_*`, `sa_set_*`, `sa_poll`, `sa_get_stats`, descriptions | short | takes the API mutex for microseconds; no I/O, no allocation on the hot path, no callbacks. Use a normal `[DllImport]` (GC-safe transition), **not** `SuppressGCTransition`: another thread may hold the mutex |
| `sa_update` | short, or I/O with `SYNC_IO` | runs native callbacks on the calling thread **after** dropping the mutex. Callbacks may re-enter the API. With managed callbacks, use the poll queue instead (no reverse P/Invoke) |
| command queue full (4096 entries) | ≤ 4 ms | the game thread waits for the mixer in 100 µs sleeps, then drops the command (counted). Never seen: the peak is about 50 |
| `sa_init*` | yes: file I/O | reads and parses the manifest (full set: 3.3 MB JSON, 8 ms on the Mac) and starts the IO thread |
| `sa_shutdown`, `sa_vita_out_stop` | yes | join threads (≤ 2 grains + one IO poll) |
| `sa_event_preload(e, wait_ms > 0)` | yes, ≤ wait_ms | sleeps while the IO thread loads |
| `sa_vita_out_start` | short | opens the port and creates the thread |
| `sa_mix*` | never | output thread only; one caller at a time; no mutex, no allocation, no I/O (except `SYNC_IO` stream decode) |

Call `sa_update` from one thread, normally the main thread.

## Containers read

These match `vita/notes/sound-bake.md`. The reader also accepts a few more, for robustness.
- pcm16:
  - RIFF/WAVE PCM 16-bit (also float32, WAVE_FORMAT_EXTENSIBLE)
  - CAF `lpcm`
  - AIFF/AIFC (`NONE`/`twos`/`sowt`/`fl32`)
  - headerless LE int16
- ima-adpcm:
  - WAVE format 0x11 (MS IMA, any blockAlign, `fact` honoured)
  - Apple IMA4 in CAF/AIFC, with QuickTime's predictor-continuity rule; bit-exact with afconvert
- aac-adts: AAC-LC ADTS, 1024 frames per ADTS frame, `encoderDelay` (default 2112) dropped, stopped at `frames`.

Loops are `[start, end)`.

## Config knobs (`sa_config`)

`max_voices` 48, `max_instances` 1024, `max_streams` 8 (the sceAudiodec AAC limit), `stream_buffer_frames` 32768,
`cache_budget_bytes` 24 MiB, `stream_threshold_bytes` 1 MiB, `adpcm_decode_max_frames` 48000 (ADPCM takes ≤ 1 s are
decoded at load: 4× the RAM, about 6× less mixer CPU), `fadeout_ms` 50 (only events without a measured release), `declick_ms` 4, `param_xfade_ms` 80,
`snapshot_fade_ms` 200, `doppler_scale` 0 (off), `limiter` on, `io_thread_priority`/`affinity` (Vita), `alloc`/`free`
hooks (serve the cache from a dedicated memblock on the Vita), `seed`, `flags` (`SYNC_IO`, `NO_AAC`, `STRICT_MANIFEST`).
