/* shepaudio — a small portable C99 mixer that plays a baked FMOD event set (the "audiobake" manifest).
 *
 * Private hobby port (Shepherd). Plays runtime/audiobake/<set>/manifest.json (SHARED SPEC, version 1):
 * events -> takes (pcm16 / ima-adpcm / aac-adts), buses, snapshots. No FMOD runtime, no network.
 *
 * Threads (see vita/audio/README.md "Threading and blocking"):
 *   - the GAME thread(s) call every sa_* function except sa_mix*; they serialise on one API mutex;
 *   - the OUTPUT backend calls sa_mix / sa_mix_s16 from its own thread (never takes the API mutex);
 *   - one IO thread ("streamer") reads files and decodes streams (absent with SA_FLAG_SYNC_IO).
 * Callbacks run only inside sa_update(), on the thread that called it, after the API mutex is dropped
 * (FMOD "deferred callbacks" model). The mixer and IO threads never call back.
 *
 * Handles are 32-bit (index + generation); 0 is never valid. Stale handles return SA_ERR_INVALID_HANDLE.
 * All strings are UTF-8, NUL-terminated. Paths are matched case-insensitively ("event:/...", "snapshot:/...",
 * "bus:/..."); a "{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}" string is accepted wherever a path is.
 */
#ifndef SHEPAUDIO_H
#define SHEPAUDIO_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define SA_API __declspec(dllexport)
#else
#define SA_API __attribute__((visibility("default")))
#endif

#define SHEPAUDIO_VERSION 0x00010000 /* 1.0.0 */

typedef struct sa_system sa_system;
typedef uint32_t sa_handle; /* event/snapshot instance; 0 = invalid */

/* ---- results ---- */
enum {
    SA_OK = 0,
    SA_ERR_INVALID_HANDLE = -1,
    SA_ERR_NOT_FOUND = -2,
    SA_ERR_INVALID_PARAM = -3,
    SA_ERR_NO_MEMORY = -4,
    SA_ERR_FILE = -5,
    SA_ERR_FORMAT = -6,
    SA_ERR_FULL = -7,
    SA_ERR_STATE = -8,
    SA_ERR_UNSUPPORTED = -9,
    SA_ERR_TIMEOUT = -10
};

/* ---- playback state: same numbers as FMOD.Studio.PLAYBACK_STATE ---- */
enum {
    SA_STATE_PLAYING = 0,
    SA_STATE_SUSTAINING = 1, /* never reported */
    SA_STATE_STOPPED = 2,
    SA_STATE_STARTING = 3,
    SA_STATE_STOPPING = 4
};

/* ---- notification / callback types: same bits as FMOD.Studio.EVENT_CALLBACK_TYPE ---- */
enum {
    SA_CB_CREATED = 0x00000001, /* never fired (callbacks are set after creation) */
    SA_CB_DESTROYED = 0x00000002,
    SA_CB_STARTING = 0x00000004,
    SA_CB_STARTED = 0x00000008,
    SA_CB_RESTARTED = 0x00000010,
    SA_CB_STOPPED = 0x00000020,
    SA_CB_START_FAILED = 0x00000040,
    SA_CB_REAL_TO_VIRTUAL = 0x00008000,
    SA_CB_VIRTUAL_TO_REAL = 0x00010000,
    SA_CB_ALL = 0x7FFFFFFF
};

/* ---- init flags ---- */
enum {
    SA_FLAG_SYNC_IO = 0x1,         /* no IO thread: loads happen inside sa_update, stream decode inside sa_mix
                                      (deterministic; used by the file backend and the tests) */
    SA_FLAG_NO_AAC = 0x2,          /* do not use the platform AAC decoder (aac takes play silent, state still correct) */
    SA_FLAG_STRICT_MANIFEST = 0x4  /* fail init on any malformed event instead of skipping it */
};

typedef struct sa_vec3 { float x, y, z; } sa_vec3;

/* Memory hooks (the Vita host can serve the take cache from a dedicated memblock). NULL = malloc/free. */
typedef void *(*sa_alloc_fn)(size_t bytes, size_t align, void *user);
typedef void (*sa_free_fn)(void *ptr, void *user);

typedef struct sa_config {
    int output_rate;            /* Hz, default 48000 */
    int max_voices;             /* physical voices, default 48 (max 128) */
    int max_instances;          /* live instances, default 1024 (max 4096) */
    int max_streams;            /* concurrent streamed takes, default 8 (Vita sceAudiodec AAC limit is 8) */
    int stream_buffer_frames;   /* per-stream PCM ring, frames (rounded up to a power of two), default 32768 */
    size_t cache_budget_bytes;  /* in-RAM short-take cache, default 24 MiB */
    size_t stream_threshold_bytes; /* pcm16/adpcm takes bigger than this are streamed from the file, default 1 MiB */
    int adpcm_decode_max_frames;   /* cached ima-adpcm takes up to this length are decoded to pcm16 at load (4x RAM, ~5x less
                                      mixer CPU); longer ones stay compressed and decode per voice. 0 = default 48000 (1 s), <0 = never */
    float fadeout_ms;           /* stop(allowFadeout) for events without a measured release (the bake's releaseCurve,
                                   measured on every loop), default 50 */
    float declick_ms;           /* immediate stop / steal / restart, default 4 */
    float param_xfade_ms;       /* take switch after set_param, default 80 */
    float snapshot_fade_ms;     /* snapshot in/out, default 200 */
    float doppler_scale;        /* 0 = off (default) */
    int limiter;                /* master peak limiter at -0.1 dBFS (instant attack, 50 ms release): 1 = on (default), -1 = off */
    float speed_of_sound;       /* world units per second, default 343 */
    uint32_t seed;              /* variation PRNG seed, default 0x5EED */
    int io_thread_priority;     /* Vita SceKernel priority of the IO/decode thread; 0 = 100 (above the default 160-class
                                   game threads so streams don't starve; below the output thread at 64). Ignored on POSIX */
    int io_thread_affinity;     /* Vita SCE_KERNEL_CPU_MASK_USER_*; 0 = inherit */
    int flags;                  /* SA_FLAG_* */
    sa_alloc_fn alloc; sa_free_fn free; void *alloc_user;
} sa_config;

/* ---- description info (for answering EventDescription getters) ---- */
typedef struct sa_event_info {
    const char *path;     /* owned by the system */
    uint8_t guid[16];     /* FMOD.GUID memory layout (Data1 LE u32, Data2 LE u16, Data3 LE u16, Data4[8]) */
    int32_t oneshot, is3d, is_snapshot;
    int32_t length_ms;    /* -1 when unknown/infinite */
    float min_distance, max_distance;
    int32_t bus;          /* bus index */
    int32_t param_count, take_count;
} sa_event_info;

typedef struct sa_param_info {
    const char *name;     /* owned by the system */
    float minimum, maximum, default_value;
    int32_t global;
} sa_param_info;

typedef struct sa_notification {
    sa_handle instance;
    uint32_t type;        /* one SA_CB_* bit */
    int32_t event;        /* event index */
    uint32_t reserved;
    uint64_t user_data;   /* the instance's user data (pointer-sized value widened) */
} sa_notification;

typedef void (*sa_event_callback)(uint32_t type, sa_handle instance, void *user_data);

typedef struct sa_stats {
    int32_t voices_active, voices_max, voices_tail;  /* physical voices playing; tails = stolen voices fading out */
    int32_t layers_virtual;                          /* started layers with no physical voice */
    int32_t instances_live, instances_max;
    int32_t streams_active, streams_max;
    uint32_t steals, virtualized, devirtualized, hard_cuts;
    uint32_t underruns;            /* stream ring empty while playing (frames of silence inserted, counted per event) */
    uint32_t cache_hits, cache_misses, cache_evictions, load_failures;
    uint64_t cache_bytes, cache_budget;
    uint32_t cmd_queue_peak, cmd_dropped, notify_dropped;
    uint32_t mixed_blocks;
    uint64_t mixed_frames;         /* the audio clock */
    double mix_seconds;            /* wall time spent inside sa_mix (only if timing enabled with sa_set_timing) */
    /* Stage C (appended; older readers ignore them) */
    double io_seconds;             /* of mix_seconds: SYNC_IO stream decode done inside sa_mix (timing only) */
    uint32_t limiter_frames;       /* output frames the master limiter turned down (the sum would have exceeded -0.1 dBFS) */
    float limiter_min_gain;        /* lowest limiter gain so far (1 = never engaged) */
    float pre_limiter_peak;        /* highest |sample| of the master sum before the limiter (1.0 = full scale) */
    uint64_t cache_peak_bytes;     /* high-water mark of cache_bytes */
    /* kit #3 review (appended): the Vita output thread's failed sceAudioOutOutput calls and the first one's error code (0 = none) */
    uint32_t out_errors;
    int32_t out_first_error;
} sa_stats;

/* sa_get_stats_v: the same numbers as doubles in this fixed order (no struct layout to mirror across 32/64-bit callers) */
enum {
    SA_STATV_VOICES_ACTIVE, SA_STATV_VOICES_MAX, SA_STATV_VOICES_TAIL, SA_STATV_LAYERS_VIRTUAL,
    SA_STATV_INSTANCES_LIVE, SA_STATV_INSTANCES_MAX, SA_STATV_STREAMS_ACTIVE, SA_STATV_STREAMS_MAX,
    SA_STATV_STEALS, SA_STATV_VIRTUALIZED, SA_STATV_DEVIRTUALIZED, SA_STATV_HARD_CUTS, SA_STATV_UNDERRUNS,
    SA_STATV_CACHE_HITS, SA_STATV_CACHE_MISSES, SA_STATV_CACHE_EVICTIONS, SA_STATV_LOAD_FAILURES,
    SA_STATV_CACHE_BYTES, SA_STATV_CACHE_BUDGET, SA_STATV_CMD_QUEUE_PEAK, SA_STATV_CMD_DROPPED, SA_STATV_NOTIFY_DROPPED,
    SA_STATV_MIXED_BLOCKS, SA_STATV_MIXED_FRAMES, SA_STATV_MIX_SECONDS, SA_STATV_IO_SECONDS,
    SA_STATV_LIMITER_FRAMES, SA_STATV_LIMITER_MIN_GAIN, SA_STATV_PRE_LIMITER_PEAK, SA_STATV_CACHE_PEAK_BYTES,
    SA_STATV_OUT_ERRORS, SA_STATV_OUT_FIRST_ERROR,
    SA_STATV_COUNT
};

/* ================= system ================= */
SA_API void sa_config_defaults(sa_config *cfg);
/* Reads and parses the manifest (blocking file I/O). Returns NULL on failure (see sa_last_error). */
SA_API sa_system *sa_init(const char *manifest_path, int output_rate);
SA_API sa_system *sa_init_ex(const char *manifest_path, const sa_config *cfg);
/* Stops the IO thread (joins it). The output backend must be stopped first. */
SA_API void sa_shutdown(sa_system *sys);
SA_API const char *sa_last_error(void);   /* last init/load error text (process-wide) */
SA_API uint32_t sa_version(void);

/* Once per game frame: applies queued changes to the mixer, advances state machines (STARTING->PLAYING,
 * one-shot end -> STOPPED, fades, snapshots), destroys released+stopped instances, then runs callbacks. */
SA_API int sa_update(sa_system *sys);

/* Render function for the output backend: interleaved stereo, `frames` frames. Never blocks, never allocates. */
SA_API int sa_mix(sa_system *sys, float *out, int frames);
SA_API int sa_mix_s16(sa_system *sys, int16_t *out, int frames);

SA_API int sa_set_master_volume(sa_system *sys, float volume);
SA_API int sa_set_listener(sa_system *sys, const sa_vec3 *pos, const sa_vec3 *vel, const sa_vec3 *forward, const sa_vec3 *up);
SA_API int sa_set_global_param(sa_system *sys, const char *name, float value);
SA_API int sa_get_global_param(sa_system *sys, const char *name, float *value);
SA_API int sa_get_stats(sa_system *sys, sa_stats *out);
SA_API void sa_set_timing(sa_system *sys, int enabled);
SA_API uint64_t sa_get_mix_clock(sa_system *sys);   /* lock-free: frames mixed so far */
/* Notifications for every instance (in addition to per-instance masks), delivered through sa_poll. */
SA_API int sa_set_poll_mask(sa_system *sys, uint32_t mask);
/* Drains up to `max` queued notifications (produced by sa_update). Returns the count. */
SA_API int sa_poll(sa_system *sys, sa_notification *out, int max);

/* ================= descriptions ================= */
SA_API int sa_event_count(sa_system *sys);
SA_API int sa_event_find(sa_system *sys, const char *path_or_guid);   /* event index, or SA_ERR_NOT_FOUND */
SA_API int sa_event_find_guid(sa_system *sys, const void *guid16);
SA_API int sa_event_get_info(sa_system *sys, int event, sa_event_info *out);
SA_API int sa_event_get_param(sa_system *sys, int event, int index, sa_param_info *out);
SA_API int sa_event_find_param(sa_system *sys, int event, const char *name); /* param index within the event */
/* Loads the event's short takes into the cache and pins them (FMOD loadSampleData). wait_ms > 0 blocks until loaded. */
SA_API int sa_event_preload(sa_system *sys, int event, int wait_ms);
SA_API int sa_event_unload(sa_system *sys, int event);

/* ================= instances ================= */
SA_API sa_handle sa_event_create(sa_system *sys, const char *path_or_guid);  /* 0 on failure */
SA_API sa_handle sa_event_create_guid(sa_system *sys, const void *guid16);
SA_API sa_handle sa_event_create_index(sa_system *sys, int event);
SA_API int sa_instance_start(sa_system *sys, sa_handle inst);
SA_API int sa_instance_stop(sa_system *sys, sa_handle inst, int allow_fadeout);
SA_API int sa_instance_release(sa_system *sys, sa_handle inst);
SA_API int sa_instance_set_param(sa_system *sys, sa_handle inst, const char *name, float value);
SA_API int sa_instance_set_param_index(sa_system *sys, sa_handle inst, int index, float value);
SA_API int sa_instance_get_param(sa_system *sys, sa_handle inst, const char *name, float *value);
SA_API int sa_instance_set_volume(sa_system *sys, sa_handle inst, float volume);
SA_API int sa_instance_get_volume(sa_system *sys, sa_handle inst, float *volume);
SA_API int sa_instance_set_pitch(sa_system *sys, sa_handle inst, float pitch);
SA_API int sa_instance_get_pitch(sa_system *sys, sa_handle inst, float *pitch);
SA_API int sa_instance_set_paused(sa_system *sys, sa_handle inst, int paused);
SA_API int sa_instance_get_paused(sa_system *sys, sa_handle inst, int *paused);
/* Source position/velocity (world units). Distance attenuation uses the event's min/max distance
 * (FMOD inverse-tapered curve), pan is equal-power over the listener's right axis. */
SA_API int sa_instance_set_3d(sa_system *sys, sa_handle inst, const sa_vec3 *pos, const sa_vec3 *vel);
/* Lock-free (safe for [SuppressGCTransition]): returns SA_STATE_* or SA_ERR_INVALID_HANDLE. */
SA_API int sa_instance_get_playback_state(sa_system *sys, sa_handle inst);
SA_API int sa_instance_is_valid(sa_system *sys, sa_handle inst);  /* lock-free: 1/0 */
SA_API int sa_instance_get_event(sa_system *sys, sa_handle inst);  /* event index */
SA_API int sa_instance_get_timeline_ms(sa_system *sys, sa_handle inst, int *ms);
/* fn != NULL: called from sa_update for the masked types. fn == NULL && mask != 0: queued for sa_poll. */
SA_API int sa_instance_set_callback(sa_system *sys, sa_handle inst, sa_event_callback fn, uint32_t mask);
SA_API int sa_instance_set_user_data(sa_system *sys, sa_handle inst, void *user_data);
SA_API int sa_instance_get_user_data(sa_system *sys, sa_handle inst, void **user_data);

/* ================= buses ================= */
SA_API int sa_bus_count(sa_system *sys);
SA_API int sa_bus_find(sa_system *sys, const char *path);  /* bus index or SA_ERR_NOT_FOUND */
SA_API const char *sa_bus_path(sa_system *sys, int bus);
SA_API int sa_bus_set_volume(sa_system *sys, const char *path, float volume);
SA_API int sa_bus_get_volume(sa_system *sys, const char *path, float *volume);
SA_API int sa_bus_set_mute(sa_system *sys, const char *path, int mute);
SA_API int sa_bus_set_paused(sa_system *sys, const char *path, int paused);
SA_API int sa_bus_stop_all(sa_system *sys, const char *path, int allow_fadeout);

/* ================= snapshots ================= */
/* Snapshots are also reachable through sa_event_create("snapshot:/..."); start/stop then fade their busVolumes
 * in/out. These two are shorthands: create+start, and stop+release. Instance volume = snapshot intensity (0..1). */
SA_API sa_handle sa_snapshot_start(sa_system *sys, const char *path_or_guid);
SA_API int sa_snapshot_stop(sa_system *sys, sa_handle snapshot, int allow_fadeout);

/* ================= the Shepherd engine bridge (Stage C, engine/Shepherd.Engine/Audio) =================
 * Entry points with no struct whose layout differs between the 32-bit Vita and a 64-bit Mac caller (sa_config has size_t and
 * pointers), so managed code can call them through plain function pointers on both. */
/* sa_init_ex with the config as "key=value,..." over sa_config_defaults. Keys: rate, voices, instances, streams, stream_frames,
 * cache_kib, stream_threshold_kib, adpcm_decode_frames, fadeout_ms, declick_ms, xfade_ms, snapshot_fade_ms, doppler, limiter
 * (1/-1), seed, io_priority, io_affinity, sync_io (0/1), no_aac (0/1), strict (0/1). An unknown key or a bad value fails
 * (NULL; sa_last_error says which). alloc/free_fn: the cache/stream-ring hooks of sa_config (NULL = malloc/free). */
SA_API sa_system *sa_init_opts(const char *manifest_path, const char *opts, sa_alloc_fn alloc, sa_free_fn free_fn, void *alloc_user);
/* every sa_stats number as a double, in SA_STATV_* order; returns how many were written (<= max) */
SA_API int sa_get_stats_v(sa_system *sys, double *out, int max);
/* FMOD EventInstance::setTimelinePosition: moves the event's timeline to `ms`. Before start (or right after it, before the
 * next update: RuntimeManager.PlayOneShot's pattern) the take simply starts there (mid-take starts fade in over 20 ms; 0 =
 * no-op). While playing, the current voice is de-clicked out and the take restarts at the new position. Loops wrap into the
 * loop region; a one-shot moved past its end stops at its next update. Snapshots ignore it. */
SA_API int sa_instance_set_timeline_ms(sa_system *sys, sa_handle inst, int ms);

/* ================= output backends (platform builds) ================= */
/* Mac/test build: offline WAV rendering (no audio device is ever opened). */
SA_API int sa_file_render(sa_system *sys, const char *wav_path, int frames, int block, int float_wav);
/* Incremental file output for a host that drives sa_update itself (the engine: one sa_update per game frame, then the frame's
 * audio): sa_file_write mixes `frames` frames (sa_mix, no sa_update) and appends them; sa_file_close patches the header.
 * One open file per system; sa_shutdown closes it. Never an audio device. */
SA_API int sa_file_open(sa_system *sys, const char *wav_path, int float_wav);
SA_API int sa_file_write(sa_system *sys, int frames);
SA_API int sa_file_close(sa_system *sys);

/* Mac/test build (kit3 play mode): the stand-in for the Vita's sceAudioOut thread -- an output thread that mixes `grain` frames, appends
 * them to a WAV and waits for the wall clock (the hardware's pace: one grain queued ahead of the speaker), independent of sa_update and
 * of any game frame. Never an audio device. Refuses SYNC_IO. sa_rt_out_stop's out (6 doubles, optional): frames written, seconds of
 * audio, wall seconds the thread ran, audio / wall, the worst lateness (s), grains mixed after their slot (a device would underrun). */
SA_API int sa_rt_out_start(sa_system *sys, const char *wav_path, int grain, int float_wav);
SA_API int sa_rt_out_stop(sa_system *sys, double *out);

/* Vita build (libshepaudio_vita.a): sceAudioOut on its own thread. port_type 0 = MAIN, 1 = BGM.
 * grain = frames per sceAudioOutOutput (64..4096, multiple of 64; 256..1024 recommended).
 * priority: SceKernel priority (default 64 = highest user); affinity: SCE_KERNEL_CPU_MASK_USER_* (0 = inherit). */
typedef struct sa_vita_out_config { int port_type, grain, priority, affinity, stack_bytes; } sa_vita_out_config;
SA_API int sa_vita_out_start(sa_system *sys, const sa_vita_out_config *cfg);
SA_API int sa_vita_out_stop(sa_system *sys);

#ifdef __cplusplus
}
#endif
#endif
