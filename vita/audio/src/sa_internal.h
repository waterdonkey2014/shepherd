/* shepaudio internals. Not a public interface. */
#ifndef SA_INTERNAL_H
#define SA_INTERNAL_H

#include "shepaudio.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>

/* ---------------- atomics (GCC/clang builtins: arm-vita-eabi-gcc and Apple clang) ---------------- */
#define SA_LD_ACQ(p) __atomic_load_n((p), __ATOMIC_ACQUIRE)
#define SA_LD_RLX(p) __atomic_load_n((p), __ATOMIC_RELAXED)
#define SA_ST_REL(p, v) __atomic_store_n((p), (v), __ATOMIC_RELEASE)
#define SA_ST_RLX(p, v) __atomic_store_n((p), (v), __ATOMIC_RELAXED)
#define SA_ADD(p, v) __atomic_add_fetch((p), (v), __ATOMIC_ACQ_REL)
#define SA_SUB(p, v) __atomic_sub_fetch((p), (v), __ATOMIC_ACQ_REL)

/* ---------------- platform (sa_plat_posix.c / sa_plat_vita.c) ---------------- */
typedef struct sa_mutex { union { long long align; unsigned char bytes[64]; } u; } sa_mutex;
typedef struct sa_thread { void *h; int id; int running; } sa_thread;
typedef void (*sa_thread_fn)(void *arg);
int sa_mutex_init(sa_mutex *m, const char *name);
void sa_mutex_destroy(sa_mutex *m);
void sa_mutex_lock(sa_mutex *m);
void sa_mutex_unlock(sa_mutex *m);
/* priority/affinity are platform values (Vita SceKernel); 0 = default */
int sa_thread_start(sa_thread *t, const char *name, sa_thread_fn fn, void *arg, int priority, int affinity, int stack_bytes);
void sa_thread_join(sa_thread *t);
void sa_sleep_us(unsigned us);
double sa_now(void);  /* seconds, monotonic */

/* ---------------- codecs / containers (sa_codec.c) ---------------- */
enum { SA_CODEC_PCM16 = 1, SA_CODEC_ADPCM = 2, SA_CODEC_AAC = 3 };
enum { SA_FMT_NONE = 0, SA_FMT_PCM16LE, SA_FMT_PCM16BE, SA_FMT_F32LE, SA_FMT_F32BE, SA_FMT_IMA_MS, SA_FMT_IMA_APPLE, SA_FMT_ADTS };

typedef struct sa_fileinfo {
    int fmt;               /* SA_FMT_* */
    int channels, rate;
    uint32_t frames;       /* from the container; 0 = unknown (ADTS) */
    long data_offset;
    uint32_t data_bytes;
    int block_bytes;       /* ADPCM: bytes per block (all channels) */
    int frames_per_block;  /* ADPCM */
} sa_fileinfo;

int sa_probe(FILE *f, sa_fileinfo *out, char *err, size_t errlen);   /* reads headers; leaves f anywhere */

/* cont: the running predictor carries into the next Apple IMA4 packet (QuickTime rule: keep it when the packet header's
 * step index matches and its 9-bit predictor is within 0x7F); cleared by a seek */
typedef struct sa_adpcm_cur { uint32_t block, fib, skip; int pred[2], index[2]; int cont; } sa_adpcm_cur;
/* Decodes `frames` frames from a block array (data = first block) starting at the cursor; returns frames decoded
 * (< frames at the end of data). Cursor advances. */
int sa_adpcm_decode(int fmt, const uint8_t *data, uint32_t nblocks, int block_bytes, int fpb, int ch,
                    sa_adpcm_cur *cur, int16_t *dst, int frames);
/* same, and copies the cursor into *cap when it reaches the start of block cap_block (loop-start state for exact
 * IMA4 loop seams); *captured set to 1 */
int sa_adpcm_decode_cap(int fmt, const uint8_t *data, uint32_t nblocks, int block_bytes, int fpb, int ch,
                        sa_adpcm_cur *cur, int16_t *dst, int frames, uint32_t cap_block, sa_adpcm_cur *cap, int *captured);
void sa_adpcm_seek(sa_adpcm_cur *cur, int fpb, uint32_t frame);   /* cursor to (block, frame-in-block); state loaded lazily */
int sa_ima_frames_per_block(int fmt, int block_bytes, int ch);

typedef struct sa_adts { int frame_len, header_len, channels, rate, sr_index, profile, raw_blocks; } sa_adts;
int sa_adts_parse(const uint8_t *p, int avail, sa_adts *h);  /* 1 ok, 0 need more, -1 not a header */

/* platform AAC decoder (sa_aac_apple.c / sa_aac_vita.c / sa_aac_none.c) */
typedef struct sa_aac_vtbl {
    const char *name;
    int (*lib_init)(int max_streams);
    void (*lib_term)(void);
    void *(*open)(int channels, int rate);
    /* frame = one complete ADTS frame including its header; returns interleaved frames written (<= max_frames), <0 error */
    int (*decode)(void *dec, const uint8_t *frame, int frame_len, int16_t *out, int max_frames);
    void (*reset)(void *dec);
    void (*close)(void *dec);
} sa_aac_vtbl;
const sa_aac_vtbl *sa_platform_aac(void);
extern const sa_aac_vtbl *sa_aac_override;  /* tests may swap the decoder */
#define SA_AAC_PRIMING_DEFAULT 2112   /* Apple AAC encoder delay (afconvert); measured, see notes */

/* ---------------- JSON (sa_json.c) ---------------- */
enum { SA_J_NULL, SA_J_BOOL, SA_J_NUM, SA_J_STR, SA_J_ARR, SA_J_OBJ };
typedef struct sa_json {
    int type, count;
    struct sa_json *child, *next;
    const char *key, *str;
    double num;
} sa_json;
typedef struct sa_jarena { struct sa_jblock *head; } sa_jarena;
sa_json *sa_json_parse(char *text, size_t len, sa_jarena *arena, char *err, size_t errlen); /* in situ */
void sa_json_free(sa_jarena *arena);
const sa_json *sa_json_get(const sa_json *obj, const char *key);
double sa_json_num(const sa_json *v, double def);
const char *sa_json_str(const sa_json *v, const char *def);
int sa_json_bool(const sa_json *v, int def);
char *sa_read_file(const char *path, size_t *len);   /* malloc'd, NUL-terminated */

/* ---------------- manifest model (sa_manifest.c) ---------------- */
typedef struct sa_param_def { const char *name; float min, max, def; int global; int gindex; int discrete; int nvariants; } sa_param_def;

typedef struct sa_take {
    const char *file;      /* resolved path */
    int codec, channels, rate;
    uint32_t frames;       /* manifest frames (0 = unknown) */
    int32_t loop_start, loop_end;  /* -1 = no loop; end exclusive */
    int32_t pv_first;      /* index into sys->pvals (event param count floats; NAN = unspecified) or -1 */
    float weight;
    int32_t priming;       /* aac encoder delay (frames) */
    int32_t stop_frame;    /* take frame at which FMOD reported STOPPED (bake "stopFrame"); -1 = none (loops, capped):
                              the take's end. Audio after it plays on as a tail voice, like FMOD's bus/reverb tails */
    int event, group;
} sa_take;

/* takes[first..] (via take_order). count 0 = the silent group: FMOD renders nothing at these parameter values (the bake's
 * "silentAtDefault", or inferred when no group sits at the defaults); first = -1, no take */
typedef struct sa_group { int32_t first, count; float weight_sum; int nspec; } sa_group;

/* measured 3D model of one event (bake "rolloff" + "pan"; vita/notes/sound-bake.md §3D). Distances in world units. */
#define SA_SP_ROLL 12
#define SA_SP_AZ 8
#define SA_SP_NEAR 8
typedef struct sa_spatial {
    float roll_d[SA_SP_ROLL], roll_g[SA_SP_ROLL];   /* on-axis amplitude gain vs distance (rel. distance 0) */
    float az[SA_SP_AZ], far[SA_SP_AZ];             /* far-channel energy share at pan_dist vs azimuth (deg, 0..90); (0, 0.5) implied */
    float near_d[SA_SP_NEAR], near_b[SA_SP_NEAR];   /* how much of that pan applies at distance d; (0, 0) and (pan_dist, 1) implied */
    float pan_dist;
    uint8_t nroll, naz, nnear, has_roll;
} sa_spatial;

typedef struct sa_event {
    const char *path;
    uint8_t guid[16];
    int oneshot, is3d, is_snapshot;
    int32_t length_ms;
    float min_dist, max_dist;
    int bus;
    int32_t first_param, nparams;
    int32_t first_take, ntakes;    /* into sys->take_order (grouped) */
    int32_t first_group, ngroups;
    int32_t first_busvol, nbusvol; /* snapshots */
    int32_t first_curve, ncurve;   /* snapshots driven by a parameter: measured (value -> busVolumes) points */
    float fade_in_ms, fade_out_ms; /* snapshot (optional manifest fields, else config) */
    int streamed_any, loops_any;
    int32_t spatial;               /* index into sys->spatials (measured 3D model), -1 = built-in fallback */
    int32_t silent_group;          /* index (within the event) of the silent group, -1 = none */
    int32_t silent_stop_frames;    /* one-shot in its silent group: STOPPED after this many output frames; -1 = lengthMs */
    int32_t first_relpt, nrelpt;   /* stop(fadeout): measured release curve (sys->relpts pairs: output frames, gain); 0 = cfg */
    uint32_t release_stop_frames;  /* ... and FMOD's time from stop() to STOPPED (output frames) */
} sa_event;

typedef struct sa_busvol { int bus; float db; float gain; } sa_busvol;
typedef struct sa_curvept { int param; float value; int32_t first_busvol, nbusvol; } sa_curvept;

typedef struct sa_bus {
    const char *path;
    int parent;
    float volume, def_volume;
    int mute, paused;
    float snap_db;          /* sum of active snapshot contributions (dB) */
    float eff_gain;         /* product through the hierarchy, incl. mute and snapshots */
    int eff_paused;
} sa_bus;

/* ---------------- cache entries (one per take; sa_cache.c) ---------------- */
enum { SA_E_EMPTY = 0, SA_E_QUEUED, SA_E_LOADING, SA_E_READY, SA_E_FAILED };
typedef struct sa_entry {
    int state;             /* atomic SA_E_* */
    int refs;              /* atomic: voices (mixer) reading data; game increments before START, mixer decrements */
    int pins;              /* preload pins (game) */
    int fmt, channels, rate;
    uint32_t frames;
    int block_bytes, fpb; uint32_t nblocks;
    void *data; size_t bytes;
    int lru_prev, lru_next, in_lru;
    int queued;
} sa_entry;

/* ---------------- streams (sa_stream.c) ---------------- */
enum { SA_S_FREE = 0, SA_S_OPEN_REQ, SA_S_ACTIVE, SA_S_CLOSE_REQ };
typedef struct sa_stream {
    /* config written by the game before OPEN_REQ */
    int take;
    uint32_t start_frame;
    int loop; uint32_t loop_start, loop_end;
    uint32_t total;          /* frames the stream will produce before EOF if not looping */
    int channels;
    /* shared */
    int16_t *ring; uint32_t cap, mask;
    uint32_t wpos, rpos;     /* atomic monotonic frame counters */
    int eof;                 /* atomic: 1 = no more frames after wpos */
    int state;               /* atomic SA_S_* */
    int mixer_refs;          /* atomic */
    int failed;              /* atomic */
    uint32_t underruns;
    /* IO-private */
    FILE *f; sa_fileinfo info; void *aac;
    uint8_t *inbuf; int in_len, in_pos; long in_file_pos;  /* file read buffer */
    int16_t *dec; int dec_have, dec_pos;                   /* decoded PCM not yet emitted */
    uint32_t src_next;       /* source frame the next emitted frame has */
    uint32_t discard;        /* decoded frames to drop (seek pre-roll / priming) */
    uint32_t adts_no;        /* index of the next ADTS frame to be read */
    long loop_off; uint32_t loop_adts; int loop_off_known;
    sa_adpcm_cur ac, loop_ac; int have_loop_ac;
} sa_stream;

/* ---------------- mixer (sa_mixer.c) ---------------- */
enum { SA_SRC_PCM = 1, SA_SRC_ADPCM = 2, SA_SRC_STREAM = 3 };
enum { CMD_START = 1, CMD_SET, CMD_STOP, CMD_PAUSE, CMD_MASTER };
typedef struct sa_cmd {
    uint8_t op, src_kind, channels, flags;
    int16_t voice, xfade_voice;
    uint32_t gen, xfade_gen;
    float g[4];              /* L<-L, L<-R, R<-L, R<-R (mono: g[0] = L, g[2] = R) */
    float step;              /* source frames per output frame */
    uint32_t ramp;           /* SET: gain ramp frames; STOP: fade frames; START: fade-in frames */
    uint32_t xfade;          /* START: crossfade frames for xfade_voice */
    void *src;               /* sa_entry* or sa_stream* */
    uint32_t start_pos, frames;
    int32_t loop_start, loop_end;
} sa_cmd;

#define SA_CMDQ_CAP 4096
#define SA_SUBBLOCK 128
#define SA_MAX_STEP 8.0f
#define SA_MAX_TAILS 8

typedef struct sa_voice {
    uint32_t gen;
    uint8_t active, kind, channels, src_done;
    uint8_t aligning, paused_req, ending, pad_;
    float pg;                /* pause gain 0..1 (ramped); paused_req && pg == 0 -> frozen */
    sa_entry *entry; sa_stream *stream;
    const int16_t *pcm; const uint8_t *adpcm;
    int fmt, block_bytes, fpb; uint32_t nblocks;
    sa_adpcm_cur ac, loop_ac; int have_loop_ac;
    uint32_t frames; int32_t loop_start, loop_end;
    uint32_t src_pos;        /* source frame of the next frame to fetch */
    uint32_t play_pos;       /* source frame at the interpolation point (reported) */
    int16_t carry[4]; int ncarry;
    uint64_t frac;           /* 32.32 phase (only the low 32 bits are fractional) */
    uint64_t step, step_target;
    float g[4], gt[4], gd[4]; uint32_t g_left;
    float env, env_t, env_d; uint32_t env_left; uint8_t env_end;
    const float *rel; uint32_t rel_n, rel_i, rel_t; float rel_scale;   /* release curve (stop with fade-out), NULL = none */
    int16_t xfade_voice; uint32_t xfade_gen, xfade_frames, align_wait;
    uint32_t fade_in;
    uint32_t zero_tail;      /* frames left to play after the source ran out (interpolation tail) */
} sa_voice;

/* ---------------- game side ---------------- */
enum { L_FREE = 0, L_WAIT, L_PHYS, L_VIRT, L_FADING, L_DONE };
#define SA_MAX_LAYERS 3
typedef struct sa_layer {
    uint8_t phase, loops, streamed, failed, resume;
    int16_t voice; uint32_t vgen;
    int32_t take;
    uint32_t start_pos;      /* source frame to start at */
    uint32_t vclock0, vpos0; /* virtual tracking */
    float step;              /* last computed */
    float g[4];              /* last sent gains */
    float score;
    sa_stream *stream;
    int16_t xfade_from_layer;
    uint32_t xfade_frames;
    uint32_t wait_since;     /* clock when WAIT began */
    uint32_t fade_in;        /* START: fade-in frames (0 = default rule) */
    uint32_t seen_pos, seen_clock;   /* PHYS: last published position and the clock it was read at */
} sa_layer;

typedef struct sa_instance {
    uint32_t gen;            /* 20 bits */
    int32_t event;
    int state;               /* SA_STATE_* */
    uint8_t used, released, paused, started, start_pending, stopped_notify, restart, has3d;
    uint8_t stopping, eff_paused, destroy_pending, oneshot;
    float volume, pitch;
    float *params;           /* sys->ipar + idx * max_params */
    sa_vec3 pos, vel;
    int group, variation;
    sa_layer layers[SA_MAX_LAYERS];
    sa_event_callback cb; uint32_t cb_mask; void *user;
    uint32_t clock_started, silent_end;   /* takeless events / silent group: clock to stop at */
    uint32_t stop_deadline; uint8_t has_deadline, in_silent;   /* measured release: STOPPED at stop_deadline; in the silent group */
    float snap_level, snap_target, snap_rate;  /* snapshots: level 0..1, per-frame rate */
    uint32_t param_serial;   /* global-param change seen */
    int32_t seek_ms;         /* sa_instance_set_timeline_ms before start: the next start begins there (-1 / 0 = none) */
    uint32_t start_serial;   /* order of sa_instance_start calls (snapshots: the newest instance of a snapshot has priority) */
    int prev_live, next_live;
    int next_free;
} sa_instance;

/* inst < 0: an orphan (tail) voice. Its instance reported STOPPED (bake stopFrame / release) while the take still had audio
 * after it; it plays out on its own, following its bus's gain and pause, and frees the slot (and stream) when it ends. */
typedef struct sa_vmirror {
    uint32_t gen; int32_t inst; int8_t layer; uint8_t busy; float score;
    sa_stream *ostream; int32_t obus; float og[4], obus_gain, olast, ostep; uint8_t opaused;   /* og, obus_gain: at orphaning */
} sa_vmirror;

typedef struct sa_pend_note { sa_handle h; uint32_t type; int32_t event; sa_event_callback cb; void *user; uint8_t poll; } sa_pend_note;

struct sa_system {
    sa_config cfg;
    sa_mutex api;
    char *base_dir;
    /* manifest */
    char *strpool; size_t strpool_len, strpool_cap;
    sa_event *events; int nevents;
    sa_take *takes; int ntakes;
    int32_t *take_order;     /* takes grouped per event */
    sa_group *groups; int ngroups;
    sa_param_def *params; int nparams;
    float *pvals; int npvals;
    sa_bus *buses; int nbuses, cap_buses;
    sa_busvol *busvols; int nbusvols;
    sa_curvept *curvepts; int ncurvepts;
    sa_spatial *spatials; int nspatials;
    float *relpts; int nrelpts;   /* release curves: (output frame, gain) pairs; never moves after load (the mixer reads it) */
    int max_event_params;
    /* hash tables: open addressing, value = index+1 */
    int32_t *h_event_path; int32_t *h_event_guid; uint32_t h_event_mask;
    int32_t *h_bus; uint32_t h_bus_mask;
    /* globals */
    char **gnames; float *gvals; int nglobals; uint32_t global_serial;
    /* instances */
    sa_instance *inst; int ninst; int free_head; int live_head; int nlive;
    uint32_t *inst_status;   /* atomic per slot: (gen << 8) | 0x80 valid | state */
    float *ipar;
    uint32_t inst_gen_counter;
    uint32_t start_counter;
    /* listener */
    sa_vec3 lpos, lvel, lfwd, lup, lright;
    float master;
    uint32_t rng;
    /* voices (game mirror) */
    sa_vmirror *vm; uint32_t voice_gen;
    /* cache */
    sa_entry *entries;
    int lru_head, lru_tail;
    size_t cache_bytes;      /* atomic */
    int *loadq; int loadq_cap, loadq_head, loadq_tail; sa_mutex loadq_lock;
    int *doneq; int doneq_head, doneq_tail;   /* loaded/failed entries for the game to link (loadq_lock) */
    struct sa_seekidx **seek;                 /* per take, IO-private ADTS seek index */
    uint32_t update_serial;
    /* streams */
    sa_stream *streams; int nstreams;
    const sa_aac_vtbl *aac; int aac_ok;
    /* IO thread */
    sa_thread io; int io_quit;
    /* command queue (game -> mixer) */
    sa_cmd *cmdq; uint32_t cmd_w, cmd_r;   /* atomic */
    /* mixer */
    sa_voice *voices; sa_voice tails[SA_MAX_TAILS];
    uint32_t *pub_end_gen, *pub_pos, *pub_pos_gen;  /* atomic arrays [max_voices] */
    float *acc; int16_t *scratch; float *fscratch; int scratch_frames; float *s16tmp;
    float master_cur, master_t, lim_gain, lim_rel;
    uint32_t mix_clock;      /* atomic (32-bit wrap-safe deltas) */
    uint64_t mix_clock64;    /* mixer-only */
    int timing; double mix_seconds, io_seconds;
    int out_thread;          /* an output thread calls sa_mix (else sa_cmd_push may drain inline) */
    uint32_t last_update_clock;
    /* notifications */
    sa_pend_note *pend; int npend, cap_pend;
    sa_notification *pollq; int poll_cap, poll_head, poll_count;
    uint32_t poll_mask;
    /* stats (mixer-written ones are atomic) */
    uint32_t st_steals, st_virtualized, st_devirt, st_hardcuts, st_underruns, st_hits, st_misses, st_evict, st_loadfail;
    uint32_t st_cmd_peak, st_cmd_drop, st_notify_drop, st_blocks;
    int st_tails_active;
    /* Stage C: limiter + cache high-water (mixer / loading thread write them, relaxed atomics; floats as their bits) */
    uint32_t st_lim_frames, st_lim_min_bits, st_pre_peak_bits;
    size_t st_cache_peak;
    /* Stage C: incremental file output (sa_file_open/write/close) */
    void *fout; float *fout_buf; int fout_cap;
    /* kit #3 review: the Vita output thread's failed sceAudioOutOutput calls (relaxed atomics, sa_out_vita.c) */
    uint32_t out_errors; int32_t out_first_error;
};

/* manifest */
int sa_manifest_load(sa_system *sys, const char *path, char *err, size_t errlen);
void sa_manifest_free(sa_system *sys);
int sa_find_event(sa_system *sys, const char *path_or_guid);
int sa_find_event_guid(sa_system *sys, const uint8_t g[16]);
int sa_find_bus(sa_system *sys, const char *path);
int sa_parse_guid(const char *s, uint8_t out[16]);
uint32_t sa_hash_ci(const char *s);
int sa_strieq(const char *a, const char *b);
int sa_global_index(sa_system *sys, const char *name, int create);

/* cache */
int sa_cache_init(sa_system *sys);
void sa_cache_free(sa_system *sys);
void sa_cache_request(sa_system *sys, int take);          /* game (api lock) */
void sa_cache_touch(sa_system *sys, int take);            /* game: entry used -> MRU */
void sa_cache_evict(sa_system *sys);                      /* game: enforce budget */
int sa_cache_load_now(sa_system *sys, int take);          /* IO thread (or sync) */
int sa_take_streamed(sa_system *sys, int take);
void *sa_alloc(sa_system *sys, size_t bytes, size_t align);
void sa_free(sa_system *sys, void *p);

/* streams */
int sa_streams_init(sa_system *sys);
void sa_streams_free(sa_system *sys);
sa_stream *sa_stream_acquire(sa_system *sys, int take, uint32_t start_frame);  /* game; NULL if none free */
void sa_stream_release(sa_system *sys, sa_stream *s);                          /* game */
int sa_stream_ready(sa_stream *s);                                             /* game: prefill done / failed */
void sa_io_service_loads(sa_system *sys);
void sa_io_service_streams(sa_system *sys);
void sa_io_thread_main(void *arg);

/* mixer */
int sa_mixer_init(sa_system *sys);
void sa_mixer_free(sa_system *sys);
int sa_cmd_push(sa_system *sys, const sa_cmd *c);   /* game (api lock) */
void sa_mixer_drain_cmds(sa_system *sys);
int sa_mix_float(sa_system *sys, float *out, int frames);
/* kernels (exposed for unit tests / bench) */
void sa_s16_to_f32(const int16_t *s, float *d, int n);
void sa_kernel_mono(const float *s, int n, uint64_t *frac, uint64_t step, float *acc, float g0, float g2, float d0, float d2);
void sa_kernel_stereo(const float *s, int n, uint64_t *frac, uint64_t step, float *acc, const float *g, const float *d);
uint64_t sa_step_fx(float step);

/* WAV writer (sa_out_file.c) */
typedef struct sa_wav { FILE *f; int rate, flt; uint32_t frames; } sa_wav;
int sa_wav_open(sa_wav *w, const char *path, int rate, int flt);
void sa_wav_write(sa_wav *w, const float *x, int frames);
void sa_wav_close(sa_wav *w);

/* game logic helpers (sa_system.c) used by tests */
void sa_pan_gains(float pan, float out[2]);   /* equal-power, centre = unity: L = sqrt2*cos, R = sqrt2*sin */
float sa_atten_inverse_tapered(float d, float mind, float maxd);   /* the fallback rolloff (no measured one) */
void sa_spatial_gains_at(sa_system *sys, int event, sa_vec3 rel, float out[2]);   /* per-channel 3D gains (L, R) at a listener-space offset */
uint32_t sa_rand(uint32_t *s);

#endif
