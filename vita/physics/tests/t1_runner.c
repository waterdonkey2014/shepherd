/* t1_runner -- Mac driver for the T1 determinism scenarios (ours). Links libshepphys.dylib (the engine's library).
 *
 *   t1_runner info                         build info, ABI sizes, float probe
 *   t1_runner record <golden_dir>          write <golden_dir>/<name>.txt + t1_golden.h (the canonical build only)
 *   t1_runner check <golden_dir> <out_dir> run every scenario, write <out_dir>/<name>.txt, compare with the golden hash
 *                                          and with the hashes compiled into the library; print the first differing record
 *   t1_runner time [reps]                  wall time per scenario (min of reps, no output sink) and per step
 *   t1_runner s0                           Stage 0 self-test (tests/sp_s0.c: runtime + pool, settings check): records + hash
 *   t1_runner trace <out_dir>              every scenario under the trace writer (<out_dir>/<name>.sptr; its T1 hash must still match
 *                                          the compiled-in golden), then each trace replayed into fresh worlds (handles, pose hashes)
 *   t1_runner s1                           Stage 1 self-test (tests/sp_s1.cpp: the Unity layer): records + hash
 *   t1_runner s2                           Stage 2 self-test (tests/sp_s2.cpp: P1, contact serials, the Unity queries): records + hash
 *   t1_runner s3                           Stage 3 self-test (tests/sp_s3.cpp: forces, damping, restitution, clamp, frozen axes, one-sided chains,
 *                                          contact snapshots): records + hash
 *   t1_runner s6                           Stage 6 self-test (tests/sp_s6.cpp: the 3D island-ground ray, the +z fast path = the general path)
 *   t1_runner replay <file.sptr>           re-issue one trace (an engine run's SHEPHERD_PHYS_TRACE too: every core call the Unity layer
 *                                          made, the pose hash after every step, the event drains) -- the Mac = Vita check of a game run
 * Options (any position): --fz  set flush-to-zero first (arm64 FPCR.FZ / x86-64 MXCSR FTZ|DAZ), the Vita-FZ what-if.
 * Record lines: "TAG a b | hex words | decimals"; only the first two fields are compared, the decimals are for reading.
 */
#include <errno.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#if defined(__x86_64__)
#include <xmmintrin.h>
#endif

#include "sp_t1.h"
#include "../shim/sp_trace.h"

static void set_fz(void)
{
#if defined(__aarch64__)
    uint64_t fpcr;
    __asm__ volatile("mrs %0, fpcr" : "=r"(fpcr));
    fpcr |= (1ull << 24);
    __asm__ volatile("msr fpcr, %0" ::"r"(fpcr));
#elif defined(__x86_64__)
    _mm_setcsr(_mm_getcsr() | 0x8040);
#endif
}

static const char *arch(void)
{
#if defined(__aarch64__)
    return "arm64";
#elif defined(__x86_64__)
    return "x86_64";
#else
    return "?";
#endif
}

static void on_rec(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    FILE *f = (FILE *)user;
    char t[5] = {(char)(tag & 0xFF), (char)((tag >> 8) & 0xFF), (char)((tag >> 16) & 0xFF), (char)(tag >> 24), 0};
    fprintf(f, "%s %d %d |", t, a, b);
    for (int32_t i = 0; i < n; ++i) fprintf(f, " %08x", w[i]);
    fprintf(f, " |");
    for (int32_t i = 0; i < n; ++i) {
        if (i < 32 && ((fmask >> i) & 1u)) {   /* (a mask has 32 bits: words past 32 -- the ABI record's 34 sizes since Stage 6 -- print as ints) */
            float x;
            memcpy(&x, &w[i], 4);
            fprintf(f, " %.9g", (double)x);
        } else {
            fprintf(f, " %d", (int32_t)w[i]);
        }
    }
    fputc('\n', f);
}

static void mkdir_p(const char *dir)
{
    char p[1024];
    snprintf(p, sizeof p, "%s", dir);
    for (char *q = p + 1; *q; ++q)
        if (*q == '/') { *q = 0; mkdir(p, 0755); *q = '/'; }
    mkdir(p, 0755);
}

static uint64_t hash64(const sp_t1_result *r) { return ((uint64_t)r->hash_hi << 32) | r->hash_lo; }

static int run_to_file(int32_t i, const char *path, sp_t1_result *res, const char *header)
{
    FILE *f = fopen(path, "w");
    if (!f) { fprintf(stderr, "cannot write %s: %s\n", path, strerror(errno)); return -1; }
    fprintf(f, "# shepphys T1 scenario %s\n# %s\n# arch %s%s\n", sp_t1_name(i), sp_build_info(), arch(), header);
    sp_t1_run(i, on_rec, f, res);
    fprintf(f, "hash %016llx records %d words %d steps %d status %d\n", (unsigned long long)hash64(res), res->records,
            res->words, res->steps, res->status);
    fclose(f);
    return 0;
}

/* reads "hash <hex> records .." from the last such line of a golden file */
static int golden_hash(const char *path, uint64_t *h)
{
    FILE *f = fopen(path, "r");
    if (!f) return 0;
    char line[4096];
    int found = 0;
    while (fgets(line, sizeof line, f)) {
        unsigned long long v;
        if (sscanf(line, "hash %llx", &v) == 1) { *h = v; found = 1; }
    }
    fclose(f);
    return found;
}

/* the first record line (not '#', not the hash line) whose "TAG a b | words" part differs */
static void first_diff(const char *gpath, const char *opath)
{
    FILE *g = fopen(gpath, "r"), *o = fopen(opath, "r");
    if (!g || !o) { if (g) fclose(g); if (o) fclose(o); return; }
    char lg[4096], lo[4096];
    int ln = 0;
    for (;;) {
        char *pg, *po;
        do { pg = fgets(lg, sizeof lg, g); } while (pg && lg[0] == '#');
        do { po = fgets(lo, sizeof lo, o); } while (po && lo[0] == '#');
        ln++;
        if (!pg || !po) { printf("    first difference: record %d (one file ends)\n", ln); break; }
        char *cg = strchr(lg, '|'), *co = strchr(lo, '|');
        if (cg) cg = strchr(cg + 1, '|');
        if (co) co = strchr(co + 1, '|');
        size_t kg = cg ? (size_t)(cg - lg) : strlen(lg), ko = co ? (size_t)(co - lo) : strlen(lo);
        if (kg != ko || memcmp(lg, lo, kg) != 0) {
            printf("    first difference at record %d\n      golden: %s      got:    %s", ln, lg, lo);
            break;
        }
    }
    fclose(g);
    fclose(o);
}

static double now_s(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (double)ts.tv_sec + (double)ts.tv_nsec * 1e-9;
}

/* ---- Stage 0: trace replay (format: shim/sp_trace.h) ---- */
typedef struct HMap { int32_t *k, *v; uint32_t cap, n; } HMap;   /* recorded handle (tagged) -> replayed handle */
static uint32_t hm_slot(const HMap *m, int32_t k) { uint32_t h = (uint32_t)k * 2654435761u; return h & (m->cap - 1); }
static void hm_put(HMap *m, int32_t k, int32_t v)
{
    if ((m->n + 1) * 2 > m->cap) {
        HMap o = *m; m->cap = o.cap ? o.cap * 2 : 1024; m->n = 0;
        m->k = calloc(m->cap, 4); m->v = calloc(m->cap, 4);
        for (uint32_t i = 0; i < o.cap; ++i) if (o.k[i]) hm_put(m, o.k[i], o.v[i]);
        free(o.k); free(o.v);
    }
    uint32_t i = hm_slot(m, k);
    while (m->k[i] && m->k[i] != k) i = (i + 1) & (m->cap - 1);
    if (!m->k[i]) m->n++;
    m->k[i] = k; m->v[i] = v;
}
static int32_t hm_get(const HMap *m, int32_t k)
{
    if (!m->cap || k <= 0) return k;   /* errors / never-valid handles pass through unchanged */
    uint32_t i = hm_slot(m, k);
    while (m->k[i]) { if (m->k[i] == k) return m->v[i]; i = (i + 1) & (m->cap - 1); }
    return k;
}
/* worlds are mapped on their own; bodies and shapes per recorded world (world slot in the top bits of the key) */
/* (handles are (gen << 16) | (slot + 1), never 0 in the low 16 bits, so a key is never 0; bits 27..30 name the world slot and the kind) */
static int32_t bkey(int32_t world, int32_t h, int32_t kind) { return (int32_t)(((uint32_t)(world & 7) << 28) | ((uint32_t)kind << 27) | ((uint32_t)h & 0x07FFFFFFu)); }

static int32_t obj(const HMap *m, int32_t world, int32_t h, int32_t kind)
{
    if (h <= 0) return h;
    int32_t k = bkey(world, h, kind), v = hm_get(m, k);
    return v == k ? h : v;   /* never recorded: the handle as it was (the call then fails the same way, or not) */
}

typedef struct Rep { long records, steps, events, notes, mismatches, rays; char first[256]; } Rep;
static void mis(Rep *r, long at, const char *what, long a, long b)
{
    if (!r->mismatches) snprintf(r->first, sizeof r->first, "record %ld: %s: recorded %ld, replayed %ld", at, what, a, b);
    r->mismatches++;
}

static int replay_file(const char *path, Rep *rep)
{
    memset(rep, 0, sizeof *rep);
    FILE *f = fopen(path, "rb");
    if (!f) { snprintf(rep->first, sizeof rep->first, "cannot open %s", path); return -1; }
    struct { uint32_t magic, format, abi, flags; char info[112]; } h;
    if (fread(&h, 1, sizeof h, f) != sizeof h || h.magic != SP_TR_MAGIC || h.format != SP_TR_FORMAT || h.abi != SHEPPHYS_ABI_VERSION) {
        snprintf(rep->first, sizeof rep->first, "not a format-%u / ABI-%d trace", SP_TR_FORMAT, SHEPPHYS_ABI_VERSION);
        fclose(f); return -1;
    }
    HMap worlds = {0}, objs = {0}, scenes = {0}, objs3 = {0};   /* Stage 6: 3D scenes; meshes (kind 0) and colliders (kind 1) per recorded scene */
    sp_ray3d_hit *rh = NULL; int32_t rhcap = 0;
    uint8_t *buf = NULL; uint32_t cap = 0;
    sp_event *ev = NULL; int32_t evcap = 0;
    sp_event2 *ev2 = NULL; int32_t ev2cap = 0;
    uint32_t hd[2];
    while (fread(hd, 1, 8, f) == 8) {
        if (hd[1] > cap) { cap = hd[1] * 2 + 64; buf = realloc(buf, cap); }
        if (fread(buf, 1, hd[1], f) != hd[1]) { mis(rep, rep->records, "truncated record", 0, 0); break; }
        long at = rep->records++;
        int32_t *a = (int32_t *)buf;
        switch (hd[0]) {
        case SP_TR_WORLD_CREATE: {
            int32_t r = sp_world_create(a[1] ? (const sp_world_def *)(a + 2) : NULL);
            if ((a[0] > 0) != (r > 0) || (a[0] <= 0 && r != a[0])) mis(rep, at, "world_create", a[0], r);
            if (a[0] > 0) hm_put(&worlds, a[0], r);
            break; }
        case SP_TR_WORLD_DESTROY: { int32_t r = sp_world_destroy(hm_get(&worlds, a[0])); if (r != a[1]) mis(rep, at, "world_destroy", a[1], r); break; }
        case SP_TR_WORLD_STEP: {
            int32_t w = hm_get(&worlds, a[0]);
            int32_t r = sp_world_step(w, a[2] ? (const sp_step_def *)(a + 5) : NULL, NULL);
            if (r != a[1]) mis(rep, at, "world_step", a[1], r);
            uint64_t want = ((uint64_t)(uint32_t)a[4] << 32) | (uint32_t)a[3], got = r == SP_OK ? sp_tr_pose_hash(w) : 0;
            if (want != got) mis(rep, at, "pose hash (low 32 bits)", (long)(uint32_t)want, (long)(uint32_t)got);
            rep->steps++;
            break; }
        case SP_TR_WORLD_SET_GRAVITY: { int32_t r = sp_world_set_gravity(hm_get(&worlds, a[0]), a[2] ? (const float *)(a + 3) : NULL); if (r != a[1]) mis(rep, at, "set_gravity", a[1], r); break; }
        case SP_TR_WORLD_SET_CLAMPS: { int32_t r = sp_world_set_clamps(hm_get(&worlds, a[0]), a[2] ? (const float *)(a + 3) : NULL); if (r != a[1]) mis(rep, at, "set_clamps", a[1], r); break; }   /* E1: patch P2 */
        case SP_TR_BODY_CREATE: {
            int32_t r = sp_body_create(hm_get(&worlds, a[0]), a[2] ? (const sp_body_def *)(a + 3) : NULL);
            if ((a[1] > 0) != (r > 0) || (a[1] <= 0 && r != a[1])) mis(rep, at, "body_create", a[1], r);
            if (a[1] > 0) hm_put(&objs, bkey(a[0], a[1], 0), r);
            break; }
        case SP_TR_BODY_DESTROY: { int32_t r = sp_body_destroy(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0)); if (r != a[2]) mis(rep, at, "body_destroy", a[2], r); break; }
        case SP_TR_BODY_SET_TRANSFORM: case SP_TR_BODY_SET_VELOCITY: {
            int32_t w = hm_get(&worlds, a[0]), b = obj(&objs, a[0], a[1], 0);
            const float *v = a[3] ? (const float *)(a + 4) : NULL;
            int32_t r = hd[0] == SP_TR_BODY_SET_TRANSFORM ? sp_body_set_transform(w, b, v) : sp_body_set_velocity(w, b, v);
            if (r != a[2]) mis(rep, at, "body_set_transform/velocity", a[2], r);
            break; }
        case SP_TR_BODY_APPLY: { int32_t r = sp_body_apply(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), a[2], a[5] ? (const float *)(a + 6) : NULL, a[3]); if (r != a[4]) mis(rep, at, "body_apply", a[4], r); break; }
        case SP_TR_BODY_SET_TYPE: { int32_t r = sp_body_set_type(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), a[2]); if (r != a[3]) mis(rep, at, "body_set_type", a[3], r); break; }
        case SP_TR_BODY_SET_FLAG: { int32_t r = sp_body_set_flag(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), a[2], a[3]); if (r != a[4]) mis(rep, at, "body_set_flag", a[4], r); break; }
        case SP_TR_BODY_SET_FLOAT: { int32_t r = sp_body_set_float(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), a[2], a[4] ? (const float *)(a + 5) : NULL); if (r != a[3]) mis(rep, at, "body_set_float", a[3], r); break; }
        case SP_TR_SHAPE_CREATE: {
            const sp_shape_def *d = a[3] ? (const sp_shape_def *)(a + 6) : NULL;
            const float *xy = a[4] ? (const float *)((const uint8_t *)(a + 6) + sizeof(sp_shape_def)) : NULL;
            int32_t r = sp_shape_create(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), d, xy);
            if ((a[2] > 0) != (r > 0) || (a[2] <= 0 && r != a[2])) mis(rep, at, "shape_create", a[2], r);
            if (a[2] > 0) hm_put(&objs, bkey(a[0], a[2], 1), r);
            break; }
        case SP_TR_SHAPE_DESTROY: { int32_t r = sp_shape_destroy(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 1)); if (r != a[2]) mis(rep, at, "shape_destroy", a[2], r); break; }
        case SP_TR_SHAPE_SET_SENSOR: { int32_t r = sp_shape_set_sensor(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 1), a[2]); if (r != a[3]) mis(rep, at, "shape_set_sensor", a[3], r); break; }
        case SP_TR_SHAPE_SET_FILTER: { int32_t r = sp_shape_set_filter(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 1), a[3] ? (const uint32_t *)(a + 4) : NULL); if (r != a[2]) mis(rep, at, "shape_set_filter", a[2], r); break; }
        case SP_TR_EVENTS: {
            if (a[1] > evcap) { evcap = a[1]; ev = realloc(ev, (size_t)evcap * sizeof(sp_event)); }
            int32_t r = sp_events(hm_get(&worlds, a[0]), a[3] ? ev : NULL, a[1]);
            if (r != a[2]) mis(rep, at, "events", a[2], r);
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_hash(ev, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event) : 0);
            if (want != got) mis(rep, at, "event hash (low 32 bits)", (long)(uint32_t)want, (long)(uint32_t)got);
            rep->events += r > 0 ? r : 0;
            break; }
        case SP_TR_EVENTS2: {   /* Stage 2: the drain with contact serials (sp_event2) */
            if (a[1] > ev2cap) { ev2cap = a[1]; ev2 = realloc(ev2, (size_t)ev2cap * sizeof(sp_event2)); }
            int32_t r = sp_events2(hm_get(&worlds, a[0]), a[3] ? ev2 : NULL, a[1]);
            if (r != a[2]) mis(rep, at, "events2", a[2], r);
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_hash(ev2, r > 0 ? (uint32_t)r * (uint32_t)sizeof(sp_event2) : 0);
            if (want != got) mis(rep, at, "event2 hash (low 32 bits)", (long)(uint32_t)want, (long)(uint32_t)got);
            rep->events += r > 0 ? r : 0;
            break; }
        case SP_TR_NOTE: rep->notes++; break;
        /* Stage 1: the Unity filter and mass data */
        case SP_TR_WORLD_SET_MATRIX: { int32_t r = sp_world_set_matrix(hm_get(&worlds, a[0]), a[2] ? (const uint32_t *)(a + 3) : NULL); if (r != a[1]) mis(rep, at, "world_set_matrix", a[1], r); break; }
        case SP_TR_SHAPE_SET_UNITY: { int32_t r = sp_shape_set_unity(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 1), a[3] ? (const sp_shape_unity *)(a + 4) : NULL); if (r != a[2]) mis(rep, at, "shape_set_unity", a[2], r); break; }
        case SP_TR_WORLD_IGNORE_PAIR: { int32_t r = sp_world_ignore_pair(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 1), obj(&objs, a[0], a[2], 1), a[3]); if (r != a[4]) mis(rep, at, "world_ignore_pair", a[4], r); break; }
        case SP_TR_BODY_SET_MASS: { int32_t r = sp_body_set_mass(hm_get(&worlds, a[0]), obj(&objs, a[0], a[1], 0), a[3] ? (const float *)(a + 4) : NULL); if (r != a[2]) mis(rep, at, "body_set_mass", a[2], r); break; }
        /* Stage 6: the 3D scene (shim/sp_mesh3d.cpp) */
        case SP_TR_SCENE3D_CREATE: { int32_t r = sp_scene3d_create(a[1]); if ((a[0] > 0) != (r > 0) || (a[0] <= 0 && r != a[0])) mis(rep, at, "scene3d_create", a[0], r); if (a[0] > 0) hm_put(&scenes, a[0], r); break; }
        case SP_TR_SCENE3D_DESTROY: { int32_t r = sp_scene3d_destroy(hm_get(&scenes, a[0])); if (r != a[1]) mis(rep, at, "scene3d_destroy", a[1], r); break; }
        case SP_TR_MESH3D_CREATE: {
            const uint8_t *p = (const uint8_t *)(a + 5);
            const sp_mesh3d_def *d = a[2] ? (const sp_mesh3d_def *)p : NULL;
            if (d) p += sizeof *d;
            const float *xyz = a[3] ? (const float *)p : NULL;
            if (a[3] && d) p += (size_t)d->vertex_count * 12u;
            const int32_t *tri = a[4] ? (const int32_t *)p : NULL;
            int32_t r = sp_mesh3d_create(hm_get(&scenes, a[0]), d, xyz, tri);
            if ((a[1] > 0) != (r > 0) || (a[1] <= 0 && r != a[1])) mis(rep, at, "mesh3d_create", a[1], r);
            if (a[1] > 0) hm_put(&objs3, bkey(a[0], a[1], 0), r);
            break; }
        case SP_TR_MESH3D_DESTROY: { int32_t r = sp_mesh3d_destroy(hm_get(&scenes, a[0]), obj(&objs3, a[0], a[1], 0)); if (r != a[2]) mis(rep, at, "mesh3d_destroy", a[2], r); break; }
        case SP_TR_COL3D_CREATE: case SP_TR_COL3D_SET: {
            int32_t set = hd[0] == SP_TR_COL3D_SET, has = set ? a[3] : a[2];
            sp_col3d_def d;
            if (has) { memcpy(&d, a + (set ? 4 : 3), sizeof d); d.mesh = obj(&objs3, a[0], d.mesh, 0); }
            int32_t sc = hm_get(&scenes, a[0]);
            if (set) { int32_t r = sp_col3d_set(sc, obj(&objs3, a[0], a[1], 1), has ? &d : NULL); if (r != a[2]) mis(rep, at, "col3d_set", a[2], r); }
            else {
                int32_t r = sp_col3d_create(sc, has ? &d : NULL);
                if ((a[1] > 0) != (r > 0) || (a[1] <= 0 && r != a[1])) mis(rep, at, "col3d_create", a[1], r);
                if (a[1] > 0) hm_put(&objs3, bkey(a[0], a[1], 1), r);
            }
            break; }
        case SP_TR_COL3D_DESTROY: { int32_t r = sp_col3d_destroy(hm_get(&scenes, a[0]), obj(&objs3, a[0], a[1], 1)); if (r != a[2]) mis(rep, at, "col3d_destroy", a[2], r); break; }
        case SP_TR_RAY3D: {
            if (a[1] > rhcap) { rhcap = a[1]; rh = realloc(rh, (size_t)rhcap * sizeof(sp_ray3d_hit)); }
            int32_t r = sp_ray3d(hm_get(&scenes, a[0]), a[3] ? (const sp_ray3d_query *)(a + 6) : NULL, rh, a[1]);
            if (r != a[2]) mis(rep, at, "ray3d", a[2], r);
            int32_t w = r > 0 ? (r < a[1] ? r : a[1]) : 0;
            uint64_t want = ((uint64_t)(uint32_t)a[5] << 32) | (uint32_t)a[4], got = sp_tr_ray3d_hash(rh, w);
            if (want != got) mis(rep, at, "ray3d hit hash (low 32 bits)", (long)(uint32_t)want, (long)(uint32_t)got);
            rep->rays++;
            break; }
        default: mis(rep, at, "unknown op", (long)hd[0], 0); break;
        }
    }
    fclose(f);
    free(buf); free(ev); free(ev2); free(worlds.k); free(worlds.v); free(objs.k); free(objs.v);
    free(rh); free(scenes.k); free(scenes.v); free(objs3.k); free(objs3.v);
    return rep->mismatches ? 1 : 0;
}

static void s0_rec(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    printf("S0 "); on_rec(user, tag, a, b, w, n, fmask);
}
/* kit #8 `host`: the self-tests' records are hashed into the result either way; nothing printed */
static void quiet_rec(void *user, uint32_t tag, int32_t a, int32_t b, const uint32_t *w, int32_t n, uint32_t fmask)
{
    (void)user; (void)tag; (void)a; (void)b; (void)w; (void)n; (void)fmask;
}

int main(int argc, char **argv)
{
    const char *pos[6] = {0};   /* (kit #8: 6 -- `host` takes four hashes) */
    int np = 0, fz = 0;
    for (int i = 1; i < argc; ++i) {
        if (!strcmp(argv[i], "--fz")) fz = 1;
        else if (np < 6) pos[np++] = argv[i];
    }
    if (np < 1) { fprintf(stderr, "usage: t1_runner info | record <dir> | check <golden> <out> | time [reps] | s0 | s1 | s2 | s3 | s6 | trace <dir> | replay <file> [--fz]\n"); return 2; }
    if (fz) set_fz();
    const char *cmd = pos[0];
    int32_t n = sp_t1_count();

    if (!strcmp(cmd, "info")) {
        int32_t sizes[SP_ABI_N_SIZES];
        float probe = 0;
        int32_t v = sp_abi(sizes, SP_ABI_N_SIZES, &probe);
        printf("%s\narch %s abi %d probe %.9g (%s) sizes", sp_build_info(), arch(), v, (double)probe,
               probe == SHEPPHYS_ABI_PROBE ? "ok" : "BAD");
        for (int i = 0; i < SP_ABI_N_SIZES; ++i) printf(" %d", sizes[i]);
        printf("\nscenarios %d, compiled-in goldens: %s\n", n, sp_t1_golden_info());
        return probe == SHEPPHYS_ABI_PROBE ? 0 : 1;
    }
    if (!strcmp(cmd, "record") && np >= 2) {
        mkdir_p(pos[1]);
        char path[1024];
        snprintf(path, sizeof path, "%s/t1_golden.h", pos[1]);
        FILE *h = fopen(path, "w");
        if (!h) { fprintf(stderr, "cannot write %s\n", path); return 1; }
        time_t tt = time(NULL);
        char day[32];
        strftime(day, sizeof day, "%Y-%m-%d", gmtime(&tt));
        fprintf(h, "/* generated by `t1_runner record` (vita/physics/build.sh golden) -- do not edit.\n"
                   " * The T1 golden hashes, compiled into libshepphys so a device run can check itself. */\n");
        fprintf(h, "#define SP_T1_GOLDEN_N %d\n", n);
        fprintf(h, "#define SP_T1_GOLDEN_INFO \"recorded %s on %s: %s\"\n", day, arch(), sp_build_info());
        fprintf(h, "struct sp_t1_golden_row { const char *name; uint32_t hash_lo, hash_hi; int32_t records, words, steps; };\n");
        fprintf(h, "static const struct sp_t1_golden_row k_t1_golden[%d] = {\n", n);
        int bad = 0;
        for (int32_t i = 0; i < n; ++i) {
            sp_t1_result r;
            snprintf(path, sizeof path, "%s/%s.txt", pos[1], sp_t1_name(i));
            if (run_to_file(i, path, &r, "") != 0) return 1;
            if (r.status != 0) bad = 1;
            fprintf(h, "    {\"%s\", 0x%08xu, 0x%08xu, %d, %d, %d},\n", sp_t1_name(i), r.hash_lo, r.hash_hi, r.records, r.words,
                    r.steps);
            printf("T1 %-16s recorded hash %016llx records %6d words %7d steps %4d status %d\n", sp_t1_name(i),
                   (unsigned long long)hash64(&r), r.records, r.words, r.steps, r.status);
        }
        fprintf(h, "};\n");
        fclose(h);
        return bad;
    }
    if (!strcmp(cmd, "check") && np >= 3) {
        mkdir_p(pos[2]);
        int okc = 0, okc2 = 0, have2 = 0;
        char gpath[1024], opath[1024];
        for (int32_t i = 0; i < n; ++i) {
            sp_t1_result r, e;
            snprintf(gpath, sizeof gpath, "%s/%s.txt", pos[1], sp_t1_name(i));
            snprintf(opath, sizeof opath, "%s/%s.txt", pos[2], sp_t1_name(i));
            if (run_to_file(i, opath, &r, fz ? " fz" : "") != 0) return 1;
            uint64_t gh = 0;
            int have = golden_hash(gpath, &gh);
            int match = have && gh == hash64(&r) && r.status == 0;
            int compiled = sp_t1_expected(i, &e);
            int match2 = compiled && e.hash_lo == r.hash_lo && e.hash_hi == r.hash_hi;
            have2 += compiled;
            okc += match;
            okc2 += match2;
            printf("T1 %-16s %s hash %016llx (golden %016llx%s) records %d status %d\n", sp_t1_name(i), match ? "OK      " : "MISMATCH",
                   (unsigned long long)hash64(&r), (unsigned long long)gh, compiled ? (match2 ? ", compiled-in ok" : ", COMPILED-IN DIFFERS") : "",
                   r.records, r.status);
            if (!match && have) first_diff(gpath, opath);
        }
        printf("T1 SUMMARY %s%s: %d/%d match the golden files, %d/%d the compiled-in hashes\n", arch(), fz ? " fz" : "", okc, n,
               okc2, have2);
        return okc == n ? 0 : 1;
    }
    if (!strcmp(cmd, "time")) {
        int reps = np >= 2 ? atoi(pos[1]) : 5;
        if (reps < 1) reps = 1;
        double total = 0;
        for (int32_t i = 0; i < n; ++i) {
            double best = 1e9;
            sp_t1_result r;
            for (int k = 0; k < reps; ++k) {
                double t0 = now_s();
                sp_t1_run(i, NULL, NULL, &r);
                double dt = now_s() - t0;
                if (dt < best) best = dt;
            }
            total += best;
            printf("T1 %-16s %9.3f ms  %4d steps  %7.2f us/step\n", sp_t1_name(i), best * 1e3, r.steps,
                   r.steps ? best * 1e6 / r.steps : 0.0);
        }
        printf("T1 TIME total %.3f ms (%s, min of %d)\n", total * 1e3, arch(), reps);
        return 0;
    }
    if (!strcmp(cmd, "s0")) {
        sp_t1_result r;
        int32_t st = sp_s0_run(s0_rec, stdout, &r);
        printf("S0 RESULT %s hash %016llx records %d words %d status %d (%s)\n", st == 0 ? "PASS" : "FAIL", (unsigned long long)hash64(&r),
               r.records, r.words, st, arch());
        return st == 0 ? 0 : 1;
    }
    if (!strcmp(cmd, "s1")) {
        sp_t1_result r;
        int32_t st = sp_s1_run(s0_rec, stdout, &r);
        printf("S1 RESULT %s hash %016llx records %d words %d steps %d status %d (%s)\n", st == 0 ? "PASS" : "FAIL", (unsigned long long)hash64(&r),
               r.records, r.words, r.steps, st, arch());
        return st == 0 ? 0 : 1;
    }
    if (!strcmp(cmd, "s2")) {
        sp_t1_result r;
        int32_t st = sp_s2_run(s0_rec, stdout, &r);
        printf("S2 RESULT %s hash %016llx records %d words %d steps %d status %d (%s)\n", st == 0 ? "PASS" : "FAIL", (unsigned long long)hash64(&r),
               r.records, r.words, r.steps, st, arch());
        return st == 0 ? 0 : 1;
    }
    if (!strcmp(cmd, "s3")) {
        sp_t1_result r;
        int32_t st = sp_s3_run(s0_rec, stdout, &r);
        printf("S3 RESULT %s hash %016llx records %d words %d steps %d status %d (%s)\n", st == 0 ? "PASS" : "FAIL", (unsigned long long)hash64(&r),
               r.records, r.words, r.steps, st, arch());
        return st == 0 ? 0 : 1;
    }
    if (!strcmp(cmd, "s6")) {
        sp_t1_result r;
        int32_t st = sp_s6_run(s0_rec, stdout, &r);
        printf("S6 RESULT %s hash %016llx records %d words %d steps %d status %d (%s)\n", st == 0 ? "PASS" : "FAIL", (unsigned long long)hash64(&r),
               r.records, r.words, r.steps, st, arch());
        return st == 0 ? 0 : 1;
    }
    if (!strcmp(cmd, "host") && np >= 5) {
        /* kit #8 (sp_host_memory, the Vita host's pool block): S1 / S2 / S3 / S6 with a 2 MiB host block registered (the pool and B2's
         * arena carved from it), in a fresh process in the order build.sh's s0_trace runs them one per process -- every hash must equal
         * that run's (pos[1..4]: the S1 / S2 / S3 / S6 hashes; S1 repeated in one process after the others is not idempotent: its B2 record
         * counts what the earlier scenarios left, with or without a block) -- plus the block's own contract */
        sp_t1_result b[4];
        int32_t (*fn[4])(sp_t1_record_fn, void *, sp_t1_result *) = { sp_s1_run, sp_s2_run, sp_s3_run, sp_s6_run };
        const char *nm[4] = { "S1", "S2", "S3", "S6" };
        int bad = 0;
        uint32_t hb = 2u << 20;
        unsigned char *blk = (unsigned char *)malloc(hb + 32);   /* (malloc: 16-byte aligned) */
        if (!blk || sp_host_memory(blk, hb) != SP_OK) { printf("HOST sp_host_memory refused the block\n"); return 1; }
        for (int k = 0; k < 4; ++k) {
            int32_t st = fn[k](quiet_rec, NULL, &b[k]);
            char hx[24]; snprintf(hx, sizeof hx, "%016llx", (unsigned long long)hash64(&b[k]));
            int same = st == 0 && !strcmp(hx, pos[1 + k]);
            printf("HOST %s hash %s with the host block, %s without: %s\n", nm[k], hx, pos[1 + k], same ? "equal" : "DIFFERENT");
            bad |= !same;
        }
        /* the contract: a pool inside the block, the arena the rest, too big = SP_ERR_FULL, the block survives sp_shutdown, no grow */
        void *base = 0; uint32_t bytes = 0, pb = 0, ab = 0;
        sp_init_def d0 = {0, 0, SP_INIT_POOL_GROW, 0};
        int32_t r0 = sp_init(&d0);
        sp_host_memory_info(&base, &bytes, &pb, &ab);
        unsigned char *pbase = (unsigned char *)sp_pool_base();
        sp_pool_stats ps; sp_pool_get_stats(&ps);
        int big = sp_pool_alloc((int32_t)pb);   /* more than the pool: no grow with a host block */
        int c1 = r0 == SP_OK && pbase == blk && bytes == hb && pb == (512u << 10) && ab == hb - pb && ps.bytes == pb && big == SP_ERR_FULL;
        int32_t again = sp_host_memory(blk, hb);   /* while up: refused */
        sp_shutdown();
        sp_init_def d1 = {(int32_t)(hb + 4096), 0, 0, 0};
        int32_t r1 = sp_init(&d1);   /* a pool bigger than the block */
        sp_init_def d2 = {64 << 10, 0, 0, 64 << 10};
        int32_t r2 = sp_init(&d2);   /* explicit sizes inside it */
        uint32_t pb2 = 0, ab2 = 0; sp_host_memory_info(0, 0, &pb2, &ab2);
        sp_shutdown();
        int32_t ra = sp_host_memory(blk + 8, hb);   /* a misaligned block: its base is rounded up to 16 bytes */
        void *base4 = 0; uint32_t bytes4 = 0; sp_host_memory_info(&base4, &bytes4, 0, 0);
        sp_host_memory(0, 0);
        int32_t r3 = sp_init(0);   /* no block: calloc's 4 MiB default as before */
        sp_pool_stats ps3; sp_pool_get_stats(&ps3);
        sp_shutdown();
        int c2 = again == SP_ERR_PARAM && r1 == SP_ERR_FULL && r2 == SP_OK && pb2 == (64u << 10) && ab2 == (64u << 10) && r3 == SP_OK && ps3.bytes == (4u << 20)
                 && ra == SP_OK && base4 == blk + 16 && bytes4 == ((hb - 8) & ~15u);
        printf("HOST contract: block %u B, pool %u / arena %u B, no grow (%d), refused while up (%d), too big %d, explicit %u / %u, misaligned +%d %u B, calloc again %u B: %s\n",
               bytes, pb, ab, big, again, r1, pb2, ab2, (int)((unsigned char *)base4 - blk), bytes4, ps3.bytes, c1 && c2 ? "PASS" : "FAIL");
        bad |= !(c1 && c2);
        free(blk);
        printf("HOST RESULT %s (%s)\n", bad ? "FAIL" : "PASS", arch());
        return bad;
    }
    if (!strcmp(cmd, "trace") && np >= 2) {
        mkdir_p(pos[1]);
        int ok = 0;
        long trecs = 0, tsteps = 0;
        char path[1024];
        for (int32_t i = 0; i < n; ++i) {
            sp_t1_result r, e;
            snprintf(path, sizeof path, "%s/%s.sptr", pos[1], sp_t1_name(i));
            if (sp_trace_begin(path, 0) != SP_OK) { fprintf(stderr, "cannot trace to %s\n", path); return 1; }
            sp_t1_run(i, NULL, NULL, &r);
            int32_t nrec = sp_trace_end();
            int same = sp_t1_expected(i, &e) && e.hash_lo == r.hash_lo && e.hash_hi == r.hash_hi && r.status == 0;
            Rep rep;
            int rr = replay_file(path, &rep);
            ok += same && rr == 0 && nrec > 0;
            trecs += nrec; tsteps += rep.steps;
            printf("TRACE %-16s %6d records %5ld steps %5ld events; T1 hash with the trace on %s; replay %s%s%s\n", sp_t1_name(i), nrec,
                   rep.steps, rep.events, same ? "= golden" : "DIFFERS", rr == 0 ? "OK" : "MISMATCH",
                   rep.mismatches ? ": " : "", rep.mismatches ? rep.first : "");
        }
        printf("TRACE SUMMARY %s: %d/%d scenarios traced and replayed bit for bit (%ld records, %ld steps)\n", arch(), ok, n, trecs, tsteps);
        return ok == n ? 0 : 1;
    }
    if (!strcmp(cmd, "replay") && np >= 2) {
        Rep rep;
        int rr = replay_file(pos[1], &rep);
        printf("REPLAY %s: %ld records, %ld steps, %ld events, %ld notes, %ld 3D rays, %ld mismatches%s%s\n", rr == 0 ? "OK" : "FAILED", rep.records, rep.steps,
               rep.events, rep.notes, rep.rays, rep.mismatches, rep.first[0] ? "; " : "", rep.first);
        return rr == 0 ? 0 : 1;
    }
    fprintf(stderr, "unknown command\n");
    return 2;
}
