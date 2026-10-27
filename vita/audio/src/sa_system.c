/* Game-side logic: init/shutdown, instances and their state machine, take selection (variations, parameter groups,
 * crossfades), voice allocation/stealing/virtualisation, 3D, buses, snapshots, notifications, public API. */
#include "sa_internal.h"
#include <stdarg.h>

#define IDX_BITS 12
#define IDX_MASK ((1u << IDX_BITS) - 1u)
#define GEN_MASK 0xFFFFFu
#define ST_VALID 0x80u

static char g_last_error[512];
const char *sa_last_error(void) { return g_last_error; }
static void set_err(const char *fmt, ...)
{
    va_list ap; va_start(ap, fmt);
    vsnprintf(g_last_error, sizeof g_last_error, fmt, ap);
    va_end(ap);
}
uint32_t sa_version(void) { return SHEPAUDIO_VERSION; }

const sa_aac_vtbl *sa_aac_override = NULL;

uint32_t sa_rand(uint32_t *s)
{
    uint32_t x = *s ? *s : 0x5EEDu;
    x ^= x << 13; x ^= x >> 17; x ^= x << 5;
    *s = x;
    return x;
}

void sa_pan_gains(float pan, float out[2])
{
    if (pan < -1.f) pan = -1.f; else if (pan > 1.f) pan = 1.f;
    float th = (pan + 1.f) * 0.78539816f;   /* 0..pi/2 */
    out[0] = 1.41421356f * cosf(th);
    out[1] = 1.41421356f * sinf(th);
}
/* FMOD inverse-tapered rolloff: min(inverse, linear-squared) between min and max distance */
float sa_atten_inverse_tapered(float d, float mind, float maxd)
{
    if (!(maxd > mind)) return 1.f;
    if (mind <= 0.f) mind = 1e-3f;
    if (d <= mind) return 1.f;
    if (d >= maxd) return 0.f;
    float inv = mind / d;
    float t = (maxd - d) / (maxd - mind);
    float lsq = t * t;
    return inv < lsq ? inv : lsq;
}

void sa_config_defaults(sa_config *c)
{
    memset(c, 0, sizeof *c);
    c->output_rate = 48000; c->max_voices = 48; c->max_instances = 1024; c->max_streams = 8;
    c->stream_buffer_frames = 32768; c->cache_budget_bytes = (size_t)24 << 20; c->stream_threshold_bytes = (size_t)1 << 20;
    c->adpcm_decode_max_frames = 48000;
    c->fadeout_ms = 50; c->declick_ms = 4; c->param_xfade_ms = 80; c->snapshot_fade_ms = 200;
    c->doppler_scale = 0; c->speed_of_sound = 343; c->seed = 0x5EED; c->limiter = 1;
}

/* ---------------- helpers ---------------- */
static uint32_t ms_frames(sa_system *sys, float ms) { float f = ms * 0.001f * (float)sys->cfg.output_rate; return f < 1 ? 1 : (uint32_t)f; }

static sa_instance *inst_get(sa_system *sys, sa_handle h)
{
    uint32_t idx = h & IDX_MASK;
    if (!h || (int)idx >= sys->ninst) return NULL;
    sa_instance *i = &sys->inst[idx];
    return i->used && i->gen == (h >> IDX_BITS) ? i : NULL;
}
static sa_handle inst_handle(sa_system *sys, const sa_instance *i) { return (i->gen << IDX_BITS) | (uint32_t)(i - sys->inst); }
static void publish(sa_system *sys, sa_instance *i)
{
    uint32_t w = i->used ? ((i->gen << 8) | ST_VALID | (uint32_t)i->state) : (i->gen << 8);
    SA_ST_REL(&sys->inst_status[i - sys->inst], w);
}
static void set_state(sa_system *sys, sa_instance *i, int st) { i->state = st; publish(sys, i); }

static void note(sa_system *sys, sa_instance *i, uint32_t type)
{
    int to_cb = i->cb && (i->cb_mask & type);
    int to_poll = (!i->cb && (i->cb_mask & type)) || (sys->poll_mask & type);
    if (!to_cb && !to_poll) return;
    if (to_poll) {
        if (sys->poll_count == sys->poll_cap) { sys->st_notify_drop++; sys->poll_head = (sys->poll_head + 1) % sys->poll_cap; sys->poll_count--; }
        sa_notification *n = &sys->pollq[(sys->poll_head + sys->poll_count) % sys->poll_cap];
        n->instance = inst_handle(sys, i); n->type = type; n->event = i->event; n->reserved = 0; n->user_data = (uint64_t)(uintptr_t)i->user;
        sys->poll_count++;
    }
    if (to_cb) {
        if (sys->npend == sys->cap_pend) {
            int nc = sys->cap_pend ? sys->cap_pend * 2 : 256;
            sa_pend_note *np = (sa_pend_note *)realloc(sys->pend, sizeof(sa_pend_note) * (size_t)nc);
            if (!np) { sys->st_notify_drop++; return; }
            sys->pend = np; sys->cap_pend = nc;
        }
        sa_pend_note *p = &sys->pend[sys->npend++];
        p->h = inst_handle(sys, i); p->type = type; p->event = i->event; p->cb = i->cb; p->user = i->user; p->poll = 0;
    }
}

static float param_value(sa_system *sys, const sa_instance *i, int k)
{
    const sa_event *ev = &sys->events[i->event];
    const sa_param_def *pd = &sys->params[ev->first_param + k];
    return pd->global && pd->gindex >= 0 ? sys->gvals[pd->gindex] : i->params[k];
}

/* Take group for the current parameter values (bake contract, vita/notes/sound-bake.md): a group's paramValues give
 * the value of the parameter(s) it varies; every other parameter is at its default (paramValues null = all defaults).
 * Distance: continuous params ((v - t) / range)^2, discrete/labeled params 0 or 1. Ties prefer the group whose
 * parameter has more variants (section selectors like room_id), then more specified params, then the current group. */
static float group_distance(sa_system *sys, const sa_instance *i, int g, int *variants, int *nspec)
{
    const sa_event *ev = &sys->events[i->event];
    const sa_group *gr = &sys->groups[ev->first_group + g];
    const sa_take *tk = gr->count > 0 ? &sys->takes[sys->take_order[gr->first]] : NULL;   /* silent group: all defaults */
    const float *pv = tk && tk->pv_first >= 0 ? sys->pvals + tk->pv_first : NULL;
    float d = 0; *variants = 0; *nspec = 0;
    for (int k = 0; k < ev->nparams; k++) {
        const sa_param_def *pd = &sys->params[ev->first_param + k];
        int spec = pv && pv[k] == pv[k];
        float t = spec ? pv[k] : pd->def, v = param_value(sys, i, k);
        if (spec) { *variants += pd->nvariants; (*nspec)++; }
        if (pd->discrete) d += (lrintf(v) != lrintf(t)) ? 1.f : 0.f;
        else { float range = pd->max - pd->min; if (!(range > 0)) range = 1; float x = (v - t) / range; d += x * x; }
    }
    return d;
}
static int best_group(sa_system *sys, const sa_instance *i, int current)
{
    const sa_event *ev = &sys->events[i->event];
    if (ev->ngroups <= 1) return 0;
    int best = 0, bvar = -1, bspec = -1; float bd = 1e30f;
    for (int g = 0; g < ev->ngroups; g++) {
        int var, spec;
        float d = group_distance(sys, i, g, &var, &spec);
        int better = d < bd - 1e-6f || (d <= bd + 1e-6f && (var > bvar || (var == bvar && spec > bspec)));
        if (better) { bd = d; best = g; bvar = var; bspec = spec; }
    }
    if (current >= 0 && current != best) {
        int var, spec;
        float d = group_distance(sys, i, current, &var, &spec);
        if (d <= bd + 1e-6f && var >= bvar && spec >= bspec) return current;
    }
    return best;
}
static int group_is_silent(sa_system *sys, const sa_event *ev, int g) { return g >= 0 && g < ev->ngroups && sys->groups[ev->first_group + g].count == 0; }
static int pick_variation(sa_system *sys, const sa_group *gr)
{
    if (gr->count <= 1) return 0;
    float r = (float)(sa_rand(&sys->rng) >> 8) * (1.0f / 16777216.0f) * gr->weight_sum;
    for (int k = 0; k < gr->count; k++) {
        r -= sys->takes[sys->take_order[gr->first + k]].weight;
        if (r < 0) return k;
    }
    return gr->count - 1;
}
static int take_of(sa_system *sys, const sa_instance *i, int group, int variation)
{
    const sa_event *ev = &sys->events[i->event];
    const sa_group *gr = &sys->groups[ev->first_group + group];
    if (variation >= gr->count) variation = gr->count - 1;
    return sys->take_order[gr->first + variation];
}

/* ---------------- gains / 3D ---------------- */
static sa_vec3 v3sub(sa_vec3 a, sa_vec3 b) { sa_vec3 r = { a.x - b.x, a.y - b.y, a.z - b.z }; return r; }
static float v3dot(sa_vec3 a, sa_vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
static sa_vec3 v3cross(sa_vec3 a, sa_vec3 b) { sa_vec3 r = { a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x }; return r; }

/* piecewise-linear y(v) through (x0, y0) and the n points (x[k] * xs, y[k]); clamped at the last point */
static float pwl(float v, float x0, float y0, const float *x, const float *y, int n, float xs)
{
    float px = x0, py = y0;
    for (int k = 0; k < n; k++) {
        float cx = x[k] * xs, cy = y[k];
        if (v <= cx) { float t = cx > px ? (v - px) / (cx - px) : 1.f; if (t < 0) t = 0; return py + (cy - py) * t; }
        px = cx; py = cy;
    }
    return py;
}

/* Built-in 3D model for events without a measured one (manifests from older bakes): FMOD inverse-tapered rolloff between
 * min and max distance, and the median pan the bake measured over the game's 1,227 3D events (the share of energy on the
 * channel away from the source vs azimuth at 4 x minDistance, and how much of it applies nearer the listener; see
 * vita/notes/sound-mixer.md §3D). The far channel never goes to 0: FMOD leaves about -19 dB there at 90 degrees. */
static const float k_def_az[6] = { 15, 30, 45, 60, 75, 90 };
static const float k_def_far[6] = { 0.3313f, 0.1867f, 0.0861f, 0.0331f, 0.0158f, 0.0131f };
static const float k_def_near_d[6] = { 0.25f, 0.5f, 0.75f, 1.f, 1.5f, 2.5f };   /* x minDistance */
static const float k_def_near_b[6] = { 0.0864f, 0.3214f, 0.5948f, 0.7948f, 0.9553f, 0.9970f };
#define K_DEF_PAN_DIST 4.0f                                                    /* x minDistance */

/* Per-output-channel gains of a 3D source at rel (listener space offset) relative to distance 0 (where the takes are baked).
 * Measured model (bake "rolloff" + "pan"): on-axis gain g(d); q = 0.5 + blend(d) * (far(az) - 0.5) is the energy share of
 * the channel away from the source; far channel g(d) * sqrt(2q), near channel g(d) * sqrt(2(1 - q)). Azimuth folds front
 * and back (FMOD's stereo panner does: measured identical). */
static void spatial_model(sa_system *sys, const sa_event *ev, float xr, float fz, float dist, float out[2])
{
    const sa_spatial *sp = ev->spatial >= 0 ? &sys->spatials[ev->spatial] : NULL;
    float mind = ev->min_dist > 0 ? ev->min_dist : 1.f;
    float att = sp && sp->has_roll ? pwl(dist, 0, 1, sp->roll_d, sp->roll_g, sp->nroll, 1) : sa_atten_inverse_tapered(dist, ev->min_dist, ev->max_dist);
    if (att < 0) att = 0;
    float far = 0.5f, blend = 0.f;
    if (dist > 1e-6f) {
        float h = sqrtf(xr * xr + fz * fz);
        float az = h > 1e-6f ? atan2f(fabsf(xr), fabsf(fz)) * 57.2957795f : 0.f;
        float nd[SA_SP_NEAR + 1], nb[SA_SP_NEAR + 1]; int nn = 0;
        if (sp && sp->naz) {
            far = pwl(az, 0, 0.5f, sp->az, sp->far, sp->naz, 1);
            for (int k = 0; k < sp->nnear; k++) { nd[nn] = sp->near_d[k]; nb[nn] = sp->near_b[k]; nn++; }
            nd[nn] = sp->pan_dist; nb[nn] = 1.f; nn++;
            blend = pwl(dist, 0, 0, nd, nb, nn, 1);
        } else {
            far = pwl(az, 0, 0.5f, k_def_az, k_def_far, 6, 1);
            for (int k = 0; k < 6; k++) { nd[nn] = k_def_near_d[k]; nb[nn] = k_def_near_b[k]; nn++; }
            nd[nn] = K_DEF_PAN_DIST; nb[nn] = 1.f; nn++;
            blend = pwl(dist, 0, 0, nd, nb, nn, mind);
        }
    }
    float q = 0.5f + blend * (far - 0.5f);
    if (q < 0) q = 0; else if (q > 1) q = 1;
    float gf = sqrtf(2.f * q), gn = sqrtf(2.f * (1.f - q));
    if (xr >= 0) { out[0] = att * gf; out[1] = att * gn; } else { out[0] = att * gn; out[1] = att * gf; }
}

void sa_spatial_gains_at(sa_system *sys, int e, sa_vec3 rel, float out[2])
{
    out[0] = out[1] = 1.f;
    if (!sys || e < 0 || e >= sys->nevents || !sys->events[e].is3d) return;
    float xr = v3dot(rel, sys->lright), fz = v3dot(rel, sys->lfwd);
    spatial_model(sys, &sys->events[e], xr, fz, sqrtf(v3dot(rel, rel)), out);
}

static void spatial(sa_system *sys, const sa_instance *i, float lr[2], float *doppler)
{
    const sa_event *ev = &sys->events[i->event];
    lr[0] = lr[1] = 1; *doppler = 1;
    if (!ev->is3d) return;
    sa_vec3 d = v3sub(i->pos, sys->lpos);
    float dist = sqrtf(v3dot(d, d));
    spatial_model(sys, ev, v3dot(d, sys->lright), v3dot(d, sys->lfwd), dist, lr);
    if (dist > 1e-6f && sys->cfg.doppler_scale > 0) {
        sa_vec3 u = { d.x / dist, d.y / dist, d.z / dist };
        float c = sys->cfg.speed_of_sound > 1 ? sys->cfg.speed_of_sound : 343.f;
        float num = c + v3dot(sys->lvel, u), den = c + v3dot(i->vel, u);
        if (den < c * 0.1f) den = c * 0.1f;
        if (num < c * 0.1f) num = c * 0.1f;
        float f = num / den;
        *doppler = 1.f + sys->cfg.doppler_scale * (f - 1.f);
        if (*doppler < 0.25f) *doppler = 0.25f; else if (*doppler > 4.f) *doppler = 4.f;
    }
}

static int layer_channels(sa_system *sys, const sa_layer *L)
{
    if (L->stream && SA_LD_ACQ(&L->stream->state) == SA_S_ACTIVE) return L->stream->channels;
    const sa_entry *e = &sys->entries[L->take];
    if (SA_LD_ACQ(&e->state) == SA_E_READY) return e->channels;
    return sys->takes[L->take].channels;
}
static int layer_rate(sa_system *sys, const sa_layer *L)
{
    if (L->stream && SA_LD_ACQ(&L->stream->state) == SA_S_ACTIVE && L->stream->info.rate > 0) return L->stream->info.rate;
    const sa_entry *e = &sys->entries[L->take];
    if (!L->stream && SA_LD_ACQ(&e->state) == SA_E_READY && e->rate > 0) return e->rate;
    return sys->takes[L->take].rate;
}

/* gains for a layer: g[4] = L<-L, L<-R, R<-L, R<-R (mono: g[0] L, g[2] R); returns the loudest gain */
static float layer_gains(sa_system *sys, const sa_instance *i, const sa_layer *L, float g[4], float *step)
{
    const sa_event *ev = &sys->events[i->event];
    float lr[2], dop;
    spatial(sys, i, lr, &dop);
    float vol = i->volume < 0 ? 0 : i->volume;
    float gain = vol * (ev->bus >= 0 ? sys->buses[ev->bus].eff_gain : 1.f);
    int ch = layer_channels(sys, L);
    /* per-output-channel gains: the takes are FMOD's own stereo output at distance 0, so 3D only rescales each channel */
    if (ch == 1) { g[0] = gain * lr[0]; g[1] = 0; g[2] = gain * lr[1]; g[3] = 0; }
    else { g[0] = gain * lr[0]; g[1] = 0; g[2] = 0; g[3] = gain * lr[1]; }
    float p = i->pitch > 0 ? i->pitch : 0.0001f;
    *step = (float)layer_rate(sys, L) / (float)sys->cfg.output_rate * p * dop;
    if (*step > SA_MAX_STEP) *step = SA_MAX_STEP;
    float m = 0; for (int k = 0; k < 4; k++) if (fabsf(g[k]) > m) m = fabsf(g[k]);
    return m;
}

/* ---------------- buses / snapshots ---------------- */
static void buses_recompute(sa_system *sys)
{
    for (int b = 0; b < sys->nbuses; b++) {
        sa_bus *bus = &sys->buses[b];
        float own = bus->mute ? 0.f : bus->def_volume * bus->volume * (bus->snap_db != 0.f ? powf(10.f, bus->snap_db / 20.f) : 1.f);
        if (bus->parent >= 0 && bus->parent < b) { own *= sys->buses[bus->parent].eff_gain; bus->eff_paused = bus->paused || sys->buses[bus->parent].eff_paused; }
        else bus->eff_paused = bus->paused;
        bus->eff_gain = own;
    }
}
static int bus_is_under(sa_system *sys, int b, int root)
{
    for (int guard = 0; b >= 0 && guard < 64; guard++) { if (b == root) return 1; b = sys->buses[b].parent; }
    return 0;
}

/* ---------------- voices ---------------- */
static uint32_t pos_now(sa_system *sys, const sa_layer *L, uint32_t now)
{
    if (L->phase == L_VIRT) {
        double p = (double)L->vpos0 + (double)(uint32_t)(now - L->vclock0) * (double)L->step;
        const sa_take *tk = &sys->takes[L->take];
        if (tk->loop_start >= 0 && p >= tk->loop_end) {
            double len = (double)(tk->loop_end - tk->loop_start);
            p = tk->loop_start + fmod(p - tk->loop_start, len);
        }
        return p > 4e9 ? 0xFFFFFFFFu : (uint32_t)p;
    }
    if ((L->phase == L_PHYS || L->phase == L_FADING) && L->voice >= 0) {
        uint32_t g1 = SA_LD_ACQ(&sys->pub_pos_gen[L->voice]);
        uint32_t p = SA_LD_ACQ(&sys->pub_pos[L->voice]);
        uint32_t g2 = SA_LD_ACQ(&sys->pub_pos_gen[L->voice]);
        if (g1 == L->vgen && g2 == L->vgen) return p;
    }
    return L->start_pos;
}

static void layer_release_data(sa_system *sys, sa_layer *L)
{
    if (L->stream) { sa_stream_release(sys, L->stream); L->stream = NULL; }
}
static void layer_done(sa_system *sys, sa_layer *L)
{
    if (L->voice >= 0 && sys->vm[L->voice].busy && sys->vm[L->voice].gen == L->vgen) sys->vm[L->voice].busy = 0;
    layer_release_data(sys, L);
    L->phase = L_DONE; L->voice = -1;
}
static void to_virtual(sa_system *sys, sa_instance *i, sa_layer *L, uint32_t now)
{
    uint32_t p = pos_now(sys, L, now);
    L->vpos0 = p; L->vclock0 = now;
    L->phase = L_VIRT; L->voice = -1;
    layer_release_data(sys, L);
    sys->st_virtualized++;
    note(sys, i, SA_CB_REAL_TO_VIRTUAL);
}

static int alloc_voice(sa_system *sys, float score, uint32_t now)
{
    int nv = sys->cfg.max_voices, lo = -1; float los = 1e30f;
    for (int v = 0; v < nv; v++) {
        if (!sys->vm[v].busy) return v;
        if (sys->vm[v].score < los) { los = sys->vm[v].score; lo = v; }
    }
    if (lo < 0 || !(los < score * 0.999f)) return -1;
    /* steal */
    sa_vmirror *m = &sys->vm[lo];
    m->busy = 0;
    sys->st_steals++;
    if (m->inst < 0) {   /* an orphan tail: just let the new START move it to a mixer tail (4 ms fade) */
        if (m->ostream) { sa_stream_release(sys, m->ostream); m->ostream = NULL; }
        return lo;
    }
    sa_instance *j = &sys->inst[m->inst];
    sa_layer *L = &j->layers[m->layer];
    if (L->phase == L_PHYS) to_virtual(sys, j, L, now);
    else if (L->phase == L_FADING) { L->voice = -1; layer_release_data(sys, L); L->phase = L_DONE; }
    return lo;
}

static float layer_score(sa_system *sys, const sa_layer *L, float loud)
{
    float s = loud * (L->loops ? 2.f : 1.f);
    if (L->phase == L_FADING) s *= 0.1f;
    (void)sys;
    return s + 1e-6f;
}

/* start a WAIT layer whose data is ready */
static int layer_start_voice(sa_system *sys, sa_instance *i, int li, uint32_t now)
{
    sa_layer *L = &i->layers[li];
    const sa_take *tk = &sys->takes[L->take];
    float g[4], step;
    float loud = layer_gains(sys, i, L, g, &step);
    float score = layer_score(sys, L, loud);
    int v = alloc_voice(sys, score, now);
    if (v < 0) { L->phase = L_VIRT; L->vpos0 = L->start_pos; L->vclock0 = now; L->step = step; layer_release_data(sys, L); sys->st_virtualized++; return 0; }
    sa_cmd c; memset(&c, 0, sizeof c);
    c.op = CMD_START; c.voice = (int16_t)v; c.gen = ++sys->voice_gen; if (!c.gen) c.gen = ++sys->voice_gen;
    c.xfade_voice = -1;
    for (int k = 0; k < 4; k++) c.g[k] = g[k];
    c.step = step;
    c.loop_start = tk->loop_start; c.loop_end = tk->loop_end;
    c.start_pos = L->start_pos;
    if (L->stream) {
        c.src_kind = SA_SRC_STREAM; c.src = L->stream; c.channels = (uint8_t)L->stream->channels; c.frames = L->stream->total;
        SA_ADD(&L->stream->mixer_refs, 1);
    } else {
        sa_entry *e = &sys->entries[L->take];
        c.src_kind = e->fmt == SA_FMT_PCM16LE ? SA_SRC_PCM : SA_SRC_ADPCM; c.src = e; c.channels = (uint8_t)e->channels; c.frames = e->frames;
        if (c.loop_end > (int32_t)e->frames) c.loop_end = (int32_t)e->frames;
        if (c.loop_end <= c.loop_start) c.loop_start = c.loop_end = -1;
        SA_ADD(&e->refs, 1);
        sa_cache_touch(sys, L->take);
    }
    c.ramp = L->fade_in ? L->fade_in : (L->start_pos > 0 ? ms_frames(sys, 20) : 0);   /* attacks are sample-exact; mid-take starts fade in */
    c.flags = (uint8_t)(i->paused || (sys->events[i->event].bus >= 0 && sys->buses[sys->events[i->event].bus].eff_paused));
    if (L->xfade_from_layer >= 0) {
        sa_layer *O = &i->layers[L->xfade_from_layer];
        if (O->phase == L_FADING && O->voice >= 0) { c.xfade_voice = O->voice; c.xfade_gen = O->vgen; c.xfade = L->xfade_frames; }
        else c.ramp = ms_frames(sys, sys->cfg.declick_ms > 1 ? sys->cfg.declick_ms * 4 : 16);
    }
    if (sa_cmd_push(sys, &c) != SA_OK) {
        if (L->stream) SA_SUB(&L->stream->mixer_refs, 1); else SA_SUB(&sys->entries[L->take].refs, 1);
        L->phase = L_VIRT; L->vpos0 = L->start_pos; L->vclock0 = now; L->step = step; layer_release_data(sys, L);
        return 0;
    }
    sa_vmirror *m = &sys->vm[v];
    m->busy = 1; m->gen = c.gen; m->inst = (int32_t)(i - sys->inst); m->layer = (int8_t)li; m->score = score; m->ostream = NULL;
    L->phase = L_PHYS; L->voice = (int16_t)v; L->vgen = c.gen; L->step = step;
    for (int k = 0; k < 4; k++) L->g[k] = g[k];
    L->xfade_from_layer = -1;
    return 1;
}

static void send_stop(sa_system *sys, sa_layer *L, uint32_t fade)
{
    if (L->voice < 0) return;
    sa_cmd c; memset(&c, 0, sizeof c);
    c.op = CMD_STOP; c.voice = L->voice; c.gen = L->vgen; c.ramp = fade;
    sa_cmd_push(sys, &c);
}
/* stop with the event's measured release curve (the mixer walks it sample-accurately) */
static void send_stop_curve(sa_system *sys, sa_layer *L, const sa_event *ev)
{
    if (L->voice < 0) return;
    sa_cmd c; memset(&c, 0, sizeof c);
    c.op = CMD_STOP; c.voice = L->voice; c.gen = L->vgen;
    c.src = (void *)(sys->relpts + 2 * (size_t)ev->first_relpt); c.frames = (uint32_t)ev->nrelpt;
    c.ramp = (uint32_t)sys->relpts[2 * (size_t)(ev->first_relpt + ev->nrelpt - 1)];
    sa_cmd_push(sys, &c);
}

/* The instance reports STOPPED but its voice still has audio after FMOD's STOPPED (the take's bus/reverb tail, or a release
 * fade that outlasts it): the voice becomes an orphan and plays out on its own. Other layers end. */
static void orphan_layers(sa_system *sys, sa_instance *i)
{
    const sa_event *ev = &sys->events[i->event];
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        if ((L->phase == L_PHYS || L->phase == L_FADING) && L->voice >= 0 && sys->vm[L->voice].busy && sys->vm[L->voice].gen == L->vgen &&
            SA_LD_ACQ(&sys->pub_end_gen[L->voice]) != L->vgen) {
            sa_vmirror *m = &sys->vm[L->voice];
            m->inst = -1; m->layer = -1; m->score *= 0.5f;
            m->ostream = L->stream; L->stream = NULL;
            m->obus = ev->bus; m->obus_gain = m->olast = ev->bus >= 0 ? sys->buses[ev->bus].eff_gain : 1.f;
            for (int q = 0; q < 4; q++) m->og[q] = L->g[q];
            m->ostep = L->step; m->opaused = i->eff_paused;
            L->voice = -1; L->phase = L_DONE;
        } else if (L->phase != L_FREE && L->phase != L_DONE) layer_done(sys, L);
    }
}

/* orphans: free the slot (and stream) when the voice ended; follow the bus's gain and pause meanwhile */
static void orphans_service(sa_system *sys)
{
    for (int v = 0; v < sys->cfg.max_voices; v++) {
        sa_vmirror *m = &sys->vm[v];
        if (!m->busy || m->inst >= 0) continue;
        if (SA_LD_ACQ(&sys->pub_end_gen[v]) == m->gen) {
            m->busy = 0;
            if (m->ostream) { sa_stream_release(sys, m->ostream); m->ostream = NULL; }
            continue;
        }
        if (m->obus < 0) continue;
        const sa_bus *b = &sys->buses[m->obus];
        if (fabsf(b->eff_gain - m->olast) > 1e-6f) {
            float r = m->obus_gain > 1e-9f ? b->eff_gain / m->obus_gain : 0.f;   /* orphaned while muted: stays silent */
            sa_cmd c; memset(&c, 0, sizeof c);
            c.op = CMD_SET; c.voice = (int16_t)v; c.gen = m->gen; c.ramp = 256; c.step = m->ostep;
            for (int q = 0; q < 4; q++) c.g[q] = m->og[q] * r;
            m->olast = b->eff_gain;
            sa_cmd_push(sys, &c);
        }
        if ((uint8_t)b->eff_paused != m->opaused) {
            sa_cmd c; memset(&c, 0, sizeof c);
            c.op = CMD_PAUSE; c.voice = (int16_t)v; c.gen = m->gen; c.flags = (uint8_t)b->eff_paused;
            sa_cmd_push(sys, &c);
            m->opaused = (uint8_t)b->eff_paused;
        }
    }
}

/* request the data for a layer (cache load or stream open) */
static void layer_request(sa_system *sys, sa_layer *L, uint32_t now)
{
    L->streamed = (uint8_t)sa_take_streamed(sys, L->take);
    L->loops = sys->takes[L->take].loop_start >= 0;
    L->wait_since = now;
    if (L->streamed) { if (!L->stream) L->stream = sa_stream_acquire(sys, L->take, L->start_pos); }
    else {
        if (SA_LD_ACQ(&sys->entries[L->take].state) == SA_E_READY) sys->st_hits++; else sys->st_misses++;
        sa_cache_request(sys, L->take);
    }
}

static int free_layer(sa_instance *i)
{
    for (int k = 0; k < SA_MAX_LAYERS; k++) if (i->layers[k].phase == L_FREE || i->layers[k].phase == L_DONE) return k;
    return -1;
}
static int main_layer(const sa_instance *i)
{
    int best = -1;
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        uint8_t ph = i->layers[k].phase;
        if (ph == L_WAIT || ph == L_PHYS || ph == L_VIRT) best = k;   /* newest non-fading */
    }
    return best;
}
static void layer_init(sa_layer *L, int take, uint32_t start_pos)
{
    memset(L, 0, sizeof *L);
    L->phase = L_WAIT; L->voice = -1; L->take = take; L->start_pos = start_pos; L->xfade_from_layer = -1;
}

static void stop_layers(sa_system *sys, sa_instance *i, uint32_t fade)
{
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        if (L->phase == L_PHYS || L->phase == L_FADING) { send_stop(sys, L, fade); L->phase = L_FADING; L->xfade_from_layer = -1; }
        else if (L->phase == L_WAIT || L->phase == L_VIRT) layer_done(sys, L);
    }
}

/* ---------------- instance lifecycle ---------------- */
static void inst_destroy(sa_system *sys, sa_instance *i)
{
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        if (L->phase == L_PHYS || L->phase == L_FADING) { send_stop(sys, L, ms_frames(sys, sys->cfg.declick_ms)); if (L->voice >= 0) sys->vm[L->voice].busy = 0; }
        layer_release_data(sys, L);
        L->phase = L_FREE;
    }
    note(sys, i, SA_CB_DESTROYED);
    int idx = (int)(i - sys->inst);
    if (i->prev_live >= 0) sys->inst[i->prev_live].next_live = i->next_live; else sys->live_head = i->next_live;
    if (i->next_live >= 0) sys->inst[i->next_live].prev_live = i->prev_live;
    i->used = 0;
    publish(sys, i);
    i->next_free = sys->free_head; sys->free_head = idx;
    sys->nlive--;
}

/* parameter-driven snapshot (the game's volume sliders: master/music/sfx/vo_bus): piecewise-linear gain per bus over the
 * measured points, with the parameter's default as gain 1 */
static void snapshot_curve(sa_system *sys, const sa_instance *i, float weight)
{
    const sa_event *ev = &sys->events[i->event];
    for (int k = 0; k < ev->nparams; k++) {
        const sa_param_def *pd = &sys->params[ev->first_param + k];
        float v = i->params[k];
        int done_bus[32], ndone = 0;
        for (int c = 0; c < ev->ncurve; c++) {
            const sa_curvept *cp = &sys->curvepts[ev->first_curve + c];
            if (cp->param != k) continue;
            for (int q = 0; q < cp->nbusvol; q++) {
                int bus = sys->busvols[cp->first_busvol + q].bus, seen = 0;
                for (int z = 0; z < ndone; z++) if (done_bus[z] == bus) seen = 1;
                if (seen || ndone >= 32) continue;
                done_bus[ndone++] = bus;
                /* points for this bus: (value, gain), plus (default, 1) */
                float xs[33], ys[33]; int n = 0;
                xs[n] = pd->def; ys[n] = 1.f; n++;
                for (int c2 = 0; c2 < ev->ncurve && n < 33; c2++) {
                    const sa_curvept *p2 = &sys->curvepts[ev->first_curve + c2];
                    if (p2->param != k) continue;
                    for (int q2 = 0; q2 < p2->nbusvol; q2++)
                        if (sys->busvols[p2->first_busvol + q2].bus == bus && n < 33) { xs[n] = p2->value; ys[n] = sys->busvols[p2->first_busvol + q2].gain; n++; }
                }
                for (int a1 = 1; a1 < n; a1++) for (int a2 = a1; a2 > 0 && xs[a2] < xs[a2 - 1]; a2--) {
                    float tx = xs[a2]; xs[a2] = xs[a2 - 1]; xs[a2 - 1] = tx; float ty = ys[a2]; ys[a2] = ys[a2 - 1]; ys[a2 - 1] = ty;
                }
                float g;
                if (v <= xs[0]) g = ys[0];
                else if (v >= xs[n - 1]) g = ys[n - 1];
                else { int j = 1; while (j < n && xs[j] < v) j++; float t = (v - xs[j - 1]) / (xs[j] - xs[j - 1] > 0 ? xs[j] - xs[j - 1] : 1); g = ys[j - 1] + (ys[j] - ys[j - 1]) * t; }
                float db = g > 1e-4f ? 20.f * log10f(g) : -80.f;
                sys->buses[bus].snap_db += weight * db;
            }
        }
    }
}

static void snapshot_process(sa_system *sys, sa_instance *i, uint32_t dt)
{
    if (i->start_pending) {
        i->start_pending = 0;
        set_state(sys, i, SA_STATE_PLAYING);
        note(sys, i, i->restart ? SA_CB_RESTARTED : SA_CB_STARTING);
        if (!i->restart) note(sys, i, SA_CB_STARTED);
        i->restart = 0;
    }
    if (i->snap_level != i->snap_target) {
        float stepv = i->snap_rate * (float)dt;
        if (i->snap_level < i->snap_target) { i->snap_level += stepv; if (i->snap_level > i->snap_target) i->snap_level = i->snap_target; }
        else { i->snap_level -= stepv; if (i->snap_level < i->snap_target) i->snap_level = i->snap_target; }
    }
    if (i->state == SA_STATE_STOPPING && i->snap_level <= 0.f) { set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
    if (i->stopped_notify) { i->stopped_notify = 0; note(sys, i, SA_CB_STOPPED); }
}

/* no free stream slot: virtualise the quietest streamed layer of another instance if it scores lower */
static void steal_stream(sa_system *sys, sa_instance *self, float score, uint32_t now)
{
    sa_instance *vi = NULL; sa_layer *vl = NULL; float lo = score * 0.999f;
    int ov = -1;
    for (int v = 0; v < sys->cfg.max_voices; v++)
        if (sys->vm[v].busy && sys->vm[v].inst < 0 && sys->vm[v].ostream && sys->vm[v].score < lo) { lo = sys->vm[v].score; ov = v; }
    for (int k = sys->live_head; k >= 0; k = sys->inst[k].next_live) {
        sa_instance *j = &sys->inst[k];
        if (j == self) continue;
        for (int q = 0; q < SA_MAX_LAYERS; q++) {
            sa_layer *L = &j->layers[q];
            if (!L->stream || (L->phase != L_PHYS && L->phase != L_WAIT)) continue;
            float sc = L->phase == L_PHYS && L->voice >= 0 ? sys->vm[L->voice].score : L->score;
            if (sc < lo) { lo = sc; vi = j; vl = L; }
        }
    }
    if (!vl && ov >= 0) {   /* the quietest is an orphan tail holding a stream */
        sa_vmirror *m = &sys->vm[ov];
        sa_cmd c; memset(&c, 0, sizeof c);
        c.op = CMD_STOP; c.voice = (int16_t)ov; c.gen = m->gen; c.ramp = ms_frames(sys, sys->cfg.declick_ms);
        sa_cmd_push(sys, &c);
        sa_stream_release(sys, m->ostream); m->ostream = NULL;
        sys->st_steals++;
        return;   /* the slot frees itself when the voice ends (orphans_service) */
    }
    if (!vl) return;
    if (vl->phase == L_PHYS) {
        send_stop(sys, vl, ms_frames(sys, sys->cfg.declick_ms));
        if (vl->voice >= 0 && sys->vm[vl->voice].busy && sys->vm[vl->voice].gen == vl->vgen) sys->vm[vl->voice].busy = 0;
        to_virtual(sys, vi, vl, now);
    } else { layer_release_data(sys, vl); vl->wait_since = now; }
    sys->st_steals++;
}

/* frames of a layer's take: the loaded data's count when there is one, else the manifest's */
static uint32_t take_frames(sa_system *sys, const sa_layer *L)
{
    uint32_t frames = sys->takes[L->take].frames;
    if (!L->stream && SA_LD_ACQ(&sys->entries[L->take].state) == SA_E_READY) frames = sys->entries[L->take].frames;
    return frames;
}

/* output frames a one-shot in the silent group (or a takeless one) lasts */
static uint32_t silent_frames(sa_system *sys, const sa_event *ev, int in_silent)
{
    if (!ev->oneshot) return 0;
    if (in_silent && ev->silent_stop_frames >= 0) return (uint32_t)ev->silent_stop_frames;
    return ev->length_ms > 0 ? ms_frames(sys, (float)ev->length_ms) : 0;
}

static void instance_process(sa_system *sys, sa_instance *i, uint32_t now, uint32_t ramp)
{
    const sa_event *ev = &sys->events[i->event];
    if (i->stopped_notify) { i->stopped_notify = 0; note(sys, i, SA_CB_STOPPED); }
    /* 1. reap ended voices, start ready layers */
    int waiting = 0;
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        switch (L->phase) {
        case L_PHYS: case L_FADING: {
            int ended = L->voice < 0 || SA_LD_ACQ(&sys->pub_end_gen[L->voice]) == L->vgen;
            if (!ended) break;
            const sa_take *tk = &sys->takes[L->take];
            uint32_t frames = take_frames(sys, L);
            if (L->phase == L_PHYS && L->voice >= 0 && tk->stop_frame >= 0 && (uint32_t)tk->stop_frame > frames && i->state == SA_STATE_PLAYING) {
                /* the take's audio ended before FMOD's STOPPED: the timeline runs on silently until stop_frame. The end
                 * clock is estimated from the last position seen, so STOPPED does not slip by an update */
                double st = L->step > 0 ? (double)L->step : 1.0;
                uint32_t endclk = L->seen_clock + (uint32_t)((double)(frames > L->seen_pos ? frames - L->seen_pos : 0) / st);
                if ((int32_t)(now - endclk) < 0) endclk = now;
                if (sys->vm[L->voice].busy && sys->vm[L->voice].gen == L->vgen) sys->vm[L->voice].busy = 0;
                layer_release_data(sys, L);
                L->voice = -1; L->phase = L_VIRT; L->failed = 1; L->vpos0 = frames; L->vclock0 = endclk;
            } else layer_done(sys, L);
            break;
        }
        case L_VIRT: {
            const sa_take *tk = &sys->takes[L->take];
            uint32_t frames = take_frames(sys, L);
            if (!frames && ev->length_ms > 0) frames = (uint32_t)((double)ev->length_ms * tk->rate / 1000.0);
            /* a take with FMOD's STOPPED frame ends in the stop detection below (STOPPED at stop_frame, not at the end) */
            if (tk->stop_frame >= 0 && i->state == SA_STATE_PLAYING) break;
            if (tk->loop_start < 0 && frames && pos_now(sys, L, now) >= frames) layer_done(sys, L);
            break;
        }
        case L_WAIT: {
            int ready;
            float g[4], step; float loud = layer_gains(sys, i, L, g, &step);
            L->score = layer_score(sys, L, loud);
            if (L->streamed) {
                if (!L->stream) L->stream = sa_stream_acquire(sys, L->take, L->start_pos);
                if (!L->stream) steal_stream(sys, i, L->score, now);
                ready = L->stream ? sa_stream_ready(L->stream) : 0;
                if (!L->stream && (uint32_t)(now - L->wait_since) > ms_frames(sys, 250)) ready = -1;   /* no stream slot */
            } else {
                int st = SA_LD_ACQ(&sys->entries[L->take].state);
                if (st == SA_E_EMPTY) sa_cache_request(sys, L->take);
                ready = st == SA_E_READY ? 1 : (st == SA_E_FAILED ? -1 : 0);
            }
            /* a layer resuming from virtual keeps its timeline running while it waits for data */
            uint32_t elapsed = L->resume ? (uint32_t)((double)(uint32_t)(now - L->wait_since) * step) : 0;
            if (ready > 0) {
                if (elapsed && !L->streamed) {
                    const sa_take *tk = &sys->takes[L->take];
                    uint32_t p = L->start_pos + elapsed;
                    if (tk->loop_start >= 0 && p >= (uint32_t)tk->loop_end) p = (uint32_t)tk->loop_start + (p - (uint32_t)tk->loop_start) % (uint32_t)(tk->loop_end - tk->loop_start);
                    L->start_pos = p;
                }
                layer_start_voice(sys, i, k, now);
            } else if (ready < 0) {   /* failed / no stream: play silent with correct timing */
                L->failed = (uint8_t)!(L->streamed && !L->stream);   /* data failed (vs. no stream slot: retried later) */
                /* the timeline started when the layer was requested: count the wait */
                uint32_t waited = (uint32_t)((double)(uint32_t)(now - L->wait_since) * step);
                L->phase = L_VIRT; L->vpos0 = L->start_pos + waited; L->vclock0 = now; L->step = step; layer_release_data(sys, L);
            } else waiting = L->resume ? waiting : 1;
            break;
        }
        default: break;
        }
    }
    /* 2. start transition */
    if (i->start_pending && !waiting) {
        i->start_pending = 0;
        if (i->state == SA_STATE_STARTING) set_state(sys, i, SA_STATE_PLAYING);
        if (i->restart) note(sys, i, SA_CB_RESTARTED);
        else { note(sys, i, SA_CB_STARTING); note(sys, i, SA_CB_STARTED); }
        i->restart = 0;
        i->clock_started = now;
        if (ev->ntakes == 0 || i->in_silent) i->silent_end = now + silent_frames(sys, ev, i->in_silent);
    }
    /* 3. parameter-driven take switch */
    if (i->state == SA_STATE_PLAYING && ev->ngroups > 1 && i->group >= 0) {
        int g = best_group(sys, i, i->group);
        if (g != i->group && group_is_silent(sys, ev, g)) {
            /* into the silent group (FMOD renders nothing there): the current take fades out */
            int ml = main_layer(i);
            uint32_t rest = 0;
            if (ml >= 0) {
                sa_layer *O = &i->layers[ml];
                const sa_take *tk = &sys->takes[O->take];
                uint32_t p = pos_now(sys, O, now), end = tk->stop_frame >= 0 ? (uint32_t)tk->stop_frame : take_frames(sys, O);
                rest = end > p ? (uint32_t)((double)(end - p) / (O->step > 0 ? O->step : 1.f)) : 0;
                if (O->phase == L_PHYS) { send_stop(sys, O, ms_frames(sys, sys->cfg.param_xfade_ms)); O->phase = L_FADING; O->xfade_from_layer = -1; }
                else layer_done(sys, O);
            }
            i->group = g; i->in_silent = 1;
            i->silent_end = now + rest;   /* a one-shot's timeline runs on silently */
        } else if (g != i->group && main_layer(i) < 0) {
            /* out of the silent group (or nothing playing): the new take starts where the event's timeline is */
            int take = take_of(sys, i, g, i->variation);
            const sa_take *tk = &sys->takes[take];
            double el = (double)(uint32_t)(now - i->clock_started) * (double)tk->rate / (double)sys->cfg.output_rate * (i->pitch > 0 ? i->pitch : 1.f);
            uint32_t p = el > 4e9 ? 0xFFFFFFFFu : (uint32_t)el;
            if (tk->loop_start >= 0 && p >= (uint32_t)tk->loop_end) p = (uint32_t)tk->loop_start + (p - (uint32_t)tk->loop_start) % (uint32_t)(tk->loop_end - tk->loop_start);
            int nl = free_layer(i);
            if (nl >= 0 && (tk->loop_start >= 0 || !tk->frames || p < tk->frames)) {
                sa_layer *N = &i->layers[nl];
                layer_init(N, take, p);
                N->fade_in = ms_frames(sys, sys->cfg.param_xfade_ms);
                layer_request(sys, N, now);
            }
            i->group = g; i->in_silent = 0;
        } else if (g != i->group) {
            int ml = main_layer(i);
            int nl = free_layer(i);
            if (nl < 0) {   /* all slots busy: drop the oldest fading layer */
                for (int k = 0; k < SA_MAX_LAYERS && nl < 0; k++) if (i->layers[k].phase == L_FADING) {
                    send_stop(sys, &i->layers[k], ms_frames(sys, sys->cfg.declick_ms)); layer_done(sys, &i->layers[k]); nl = k;
                }
            }
            if (nl >= 0 && ml >= 0) {
                sa_layer *O = &i->layers[ml];
                uint32_t pos = pos_now(sys, O, now);
                int take = take_of(sys, i, g, i->variation);
                sa_layer *N = &i->layers[nl];
                layer_init(N, take, pos);
                N->xfade_frames = ms_frames(sys, sys->cfg.param_xfade_ms);
                if (O->phase == L_PHYS) { O->phase = L_FADING; N->xfade_from_layer = (int16_t)ml; }
                else if (O->phase == L_VIRT || O->phase == L_WAIT) layer_done(sys, O);
                layer_request(sys, N, now);
                i->group = g;
            }
        }
    }
    /* 4. gains, pitch, pause, scores */
    int paused = i->paused || (ev->bus >= 0 && sys->buses[ev->bus].eff_paused);
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        if (L->phase == L_PHYS) {
            float g[4], step;
            float loud = layer_gains(sys, i, L, g, &step);
            L->seen_pos = pos_now(sys, L, now); L->seen_clock = now;
            int changed = fabsf(step - L->step) > 1e-6f * (L->step > 0 ? L->step : 1.f);
            for (int q = 0; q < 4; q++) if (fabsf(g[q] - L->g[q]) > 1e-5f) changed = 1;
            if (changed) {
                sa_cmd c; memset(&c, 0, sizeof c);
                c.op = CMD_SET; c.voice = L->voice; c.gen = L->vgen; c.ramp = ramp; c.step = step;
                for (int q = 0; q < 4; q++) { c.g[q] = g[q]; L->g[q] = g[q]; }
                L->step = step;
                sa_cmd_push(sys, &c);
            }
            if (L->voice >= 0) sys->vm[L->voice].score = layer_score(sys, L, loud);
        } else if (L->phase == L_FADING) {
            if (L->voice >= 0) sys->vm[L->voice].score = layer_score(sys, L, 0.0f);
        } else if (L->phase == L_VIRT) {
            float g[4], step; float loud = layer_gains(sys, i, L, g, &step);
            if (paused) step = 0;
            if (fabsf(step - L->step) > 1e-7f) { L->vpos0 = pos_now(sys, L, now); L->vclock0 = now; L->step = step; }
            L->score = layer_score(sys, L, loud);
        }
        if ((L->phase == L_PHYS || L->phase == L_FADING) && L->voice >= 0 && (uint8_t)paused != i->eff_paused) {
            sa_cmd c; memset(&c, 0, sizeof c);
            c.op = CMD_PAUSE; c.voice = L->voice; c.gen = L->vgen; c.flags = (uint8_t)paused;
            sa_cmd_push(sys, &c);
        }
    }
    i->eff_paused = (uint8_t)paused;
    /* 5. devirtualise held/long layers when a voice is free */
    for (int k = 0; k < SA_MAX_LAYERS; k++) {
        sa_layer *L = &i->layers[k];
        if (L->phase != L_VIRT || paused || L->failed || L->score <= 1e-4f) continue;
        const sa_take *tk = &sys->takes[L->take];
        uint32_t p = pos_now(sys, L, now);
        int worth = L->loops || (tk->frames > p && tk->frames - p > (uint32_t)tk->rate / 4);
        if (!worth) continue;
        int freev = 0;
        for (int v = 0; v < sys->cfg.max_voices; v++) if (!sys->vm[v].busy) { freev = 1; break; }
        if (!freev) break;
        if (sa_take_streamed(sys, L->take)) {
            int frees = 0;
            for (int q = 0; q < sys->nstreams; q++) if (SA_LD_ACQ(&sys->streams[q].state) == SA_S_FREE) { frees = 1; break; }
            if (!frees) continue;
        }
        layer_init(L, L->take, p);
        L->resume = 1;
        layer_request(sys, L, now);
        sys->st_devirt++;
        note(sys, i, SA_CB_VIRTUAL_TO_REAL);
    }
    /* 6. stop detection */
    int active = 0;
    for (int k = 0; k < SA_MAX_LAYERS; k++) { uint8_t ph = i->layers[k].phase; if (ph != L_FREE && ph != L_DONE) active++; }
    if (i->state == SA_STATE_STOPPING) {
        if (i->has_deadline) {
            /* measured release: STOPPED when FMOD reported it; a fade that outlasts it plays on as an orphan */
            if ((int32_t)(now - i->stop_deadline) >= 0) { i->has_deadline = 0; orphan_layers(sys, i); set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
        } else if (active == 0) { set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
    } else if (i->state == SA_STATE_PLAYING && !i->start_pending) {
        int ml = main_layer(i);
        int32_t sf = ml >= 0 && !i->in_silent ? sys->takes[i->layers[ml].take].stop_frame : -1;
        if (ev->ntakes == 0 || i->in_silent) {
            if (ev->oneshot && (int32_t)(now - i->silent_end) >= 0) { orphan_layers(sys, i); set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
        } else if (sf >= 0) {
            /* FMOD's STOPPED frame of the take (bake stopFrame): STOPPED there; audio after it is the tail and plays on */
            if (pos_now(sys, &i->layers[ml], now) >= (uint32_t)sf) { orphan_layers(sys, i); set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
        } else if (active == 0 && ev->oneshot) { set_state(sys, i, SA_STATE_STOPPED); note(sys, i, SA_CB_STOPPED); }
    }
}

/* ---------------- init / shutdown ---------------- */
sa_system *sa_init(const char *manifest_path, int output_rate)
{
    sa_config c; sa_config_defaults(&c);
    if (output_rate > 0) c.output_rate = output_rate;
    return sa_init_ex(manifest_path, &c);
}

sa_system *sa_init_ex(const char *manifest_path, const sa_config *cfg)
{
    char err[400];
    sa_system *sys = (sa_system *)calloc(1, sizeof *sys);
    if (!sys) { set_err("out of memory"); return NULL; }
    sa_config d; sa_config_defaults(&d);
    sys->cfg = cfg ? *cfg : d;
    sa_config *c = &sys->cfg;
    if (c->output_rate < 8000 || c->output_rate > 192000) c->output_rate = 48000;
    if (c->max_voices < 1) c->max_voices = d.max_voices;
    if (c->max_voices > 128) c->max_voices = 128;
    if (c->max_instances < 16) c->max_instances = d.max_instances;
    if (c->max_instances > (int)IDX_MASK + 1) c->max_instances = (int)IDX_MASK + 1;
    if (c->max_streams < 0) c->max_streams = d.max_streams;
    if (c->max_streams > 32) c->max_streams = 32;
    if (c->stream_buffer_frames < 4096) c->stream_buffer_frames = 4096;
    if (!c->cache_budget_bytes) c->cache_budget_bytes = d.cache_budget_bytes;
    if (!c->stream_threshold_bytes) c->stream_threshold_bytes = d.stream_threshold_bytes;
    if (c->adpcm_decode_max_frames == 0) c->adpcm_decode_max_frames = d.adpcm_decode_max_frames;
    if (!(c->fadeout_ms >= 0)) c->fadeout_ms = d.fadeout_ms;
    if (!(c->declick_ms > 0)) c->declick_ms = d.declick_ms;
    if (!(c->param_xfade_ms > 0)) c->param_xfade_ms = d.param_xfade_ms;
    if (!(c->snapshot_fade_ms >= 0)) c->snapshot_fade_ms = d.snapshot_fade_ms;
    if (!(c->speed_of_sound > 0)) c->speed_of_sound = d.speed_of_sound;
    if (c->limiter == 0) c->limiter = 1;
    if (sa_mutex_init(&sys->api, "shepaudio_api")) { set_err("mutex"); free(sys); return NULL; }
    if (sa_manifest_load(sys, manifest_path, err, sizeof err) != SA_OK) { set_err("%s", err); sa_manifest_free(sys); sa_mutex_destroy(&sys->api); free(sys); return NULL; }
    sys->ninst = c->max_instances;
    int mp = sys->max_event_params > 0 ? sys->max_event_params : 1;
    sys->inst = (sa_instance *)calloc((size_t)sys->ninst, sizeof(sa_instance));
    sys->inst_status = (uint32_t *)calloc((size_t)sys->ninst, sizeof(uint32_t));
    sys->ipar = (float *)calloc((size_t)sys->ninst * (size_t)mp, sizeof(float));
    sys->vm = (sa_vmirror *)calloc((size_t)c->max_voices, sizeof(sa_vmirror));
    sys->poll_cap = 1024;
    sys->pollq = (sa_notification *)calloc((size_t)sys->poll_cap, sizeof(sa_notification));
    sys->s16tmp = (float *)malloc(sizeof(float) * 2 * 512);
    if (!sys->inst || !sys->inst_status || !sys->ipar || !sys->vm || !sys->pollq || !sys->s16tmp ||
        sa_cache_init(sys) != SA_OK || sa_streams_init(sys) != SA_OK || sa_mixer_init(sys) != SA_OK) {
        set_err("out of memory"); sa_shutdown(sys); return NULL;
    }
    sys->free_head = -1;
    for (int k = sys->ninst - 1; k >= 0; k--) { sys->inst[k].next_free = sys->free_head; sys->free_head = k; sys->inst[k].params = sys->ipar + (size_t)k * (size_t)mp; }
    sys->live_head = -1;
    sys->lpos.x = sys->lpos.y = sys->lpos.z = 0; sys->lfwd.x = 0; sys->lfwd.y = 0; sys->lfwd.z = 1;
    sys->lup.x = 0; sys->lup.y = 1; sys->lup.z = 0; sys->lright.x = 1; sys->lright.y = 0; sys->lright.z = 0;
    sys->master = 1; sys->rng = c->seed ? c->seed : 0x5EED;
    sys->aac = sa_aac_override ? sa_aac_override : sa_platform_aac();
    sys->aac_ok = !(c->flags & SA_FLAG_NO_AAC) && sys->aac && (!sys->aac->lib_init || sys->aac->lib_init(c->max_streams) == 0);
    buses_recompute(sys);
    if (!(c->flags & SA_FLAG_SYNC_IO)) {
#if defined(__vita__)
        int prio = c->io_thread_priority > 0 ? c->io_thread_priority : 100;
#else
        int prio = 0;
#endif
        if (sa_thread_start(&sys->io, "shepaudio_io", sa_io_thread_main, sys, prio, c->io_thread_affinity, 128 * 1024)) { set_err("cannot start IO thread"); sa_shutdown(sys); return NULL; }
    }
    return sys;
}

/* "key=value,key=value" over the defaults (the engine bridge: no sa_config layout to mirror in managed code) */
sa_system *sa_init_opts(const char *manifest_path, const char *opts, sa_alloc_fn alloc, sa_free_fn free_fn, void *alloc_user)
{
    sa_config c; sa_config_defaults(&c);
    c.alloc = alloc; c.free = free_fn; c.alloc_user = alloc_user;
    if ((alloc == NULL) != (free_fn == NULL)) { set_err("sa_init_opts: alloc and free hooks go together"); return NULL; }
    const char *p = opts ? opts : "";
    while (*p) {
        while (*p == ',' || *p == ' ') p++;
        if (!*p) break;
        char key[40]; size_t kl = 0;
        while (*p && *p != '=' && *p != ',' && kl + 1 < sizeof key) key[kl++] = *p++;
        key[kl] = 0;
        if (*p != '=') { set_err("sa_init_opts: '%s' has no value", key); return NULL; }
        p++;
        char *end = NULL;
        double v = strtod(p, &end);
        if (end == p || (*end && *end != ',' && *end != ' ')) { set_err("sa_init_opts: bad value for '%s'", key); return NULL; }
        p = end;
        int iv = (int)v;
#define KEY(k) (!strcmp(key, k))
        if (KEY("rate")) c.output_rate = iv;
        else if (KEY("voices")) c.max_voices = iv;
        else if (KEY("instances")) c.max_instances = iv;
        else if (KEY("streams")) c.max_streams = iv;
        else if (KEY("stream_frames")) c.stream_buffer_frames = iv;
        else if (KEY("cache_kib")) c.cache_budget_bytes = (size_t)(v > 0 ? v : 0) * 1024u;
        else if (KEY("stream_threshold_kib")) c.stream_threshold_bytes = (size_t)(v > 0 ? v : 0) * 1024u;
        else if (KEY("adpcm_decode_frames")) c.adpcm_decode_max_frames = iv;
        else if (KEY("fadeout_ms")) c.fadeout_ms = (float)v;
        else if (KEY("declick_ms")) c.declick_ms = (float)v;
        else if (KEY("xfade_ms")) c.param_xfade_ms = (float)v;
        else if (KEY("snapshot_fade_ms")) c.snapshot_fade_ms = (float)v;
        else if (KEY("doppler")) c.doppler_scale = (float)v;
        else if (KEY("limiter")) c.limiter = iv;
        else if (KEY("seed")) c.seed = (uint32_t)v;
        else if (KEY("io_priority")) c.io_thread_priority = iv;
        else if (KEY("io_affinity")) c.io_thread_affinity = iv;
        else if (KEY("sync_io")) c.flags = iv ? (c.flags | SA_FLAG_SYNC_IO) : (c.flags & ~SA_FLAG_SYNC_IO);
        else if (KEY("no_aac")) c.flags = iv ? (c.flags | SA_FLAG_NO_AAC) : (c.flags & ~SA_FLAG_NO_AAC);
        else if (KEY("strict")) c.flags = iv ? (c.flags | SA_FLAG_STRICT_MANIFEST) : (c.flags & ~SA_FLAG_STRICT_MANIFEST);
        else { set_err("sa_init_opts: unknown key '%s'", key); return NULL; }
#undef KEY
    }
    return sa_init_ex(manifest_path, &c);
}

void sa_shutdown(sa_system *sys)
{
    if (!sys) return;
    if (sys->fout) sa_file_close(sys);
    if (sys->io.running) { SA_ST_REL(&sys->io_quit, 1); sa_thread_join(&sys->io); }
    sa_streams_free(sys);
    if (sys->aac_ok && sys->aac && sys->aac->lib_term) sys->aac->lib_term();
    sa_cache_free(sys);
    sa_mixer_free(sys);
    free(sys->inst); free(sys->inst_status); free(sys->ipar); free(sys->vm); free(sys->pollq); free(sys->pend); free(sys->s16tmp);
    sa_manifest_free(sys);
    sa_mutex_destroy(&sys->api);
    free(sys);
}

/* ---------------- update ---------------- */
int sa_update(sa_system *sys)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    uint32_t now = SA_LD_ACQ(&sys->mix_clock);
    uint32_t dt = now - sys->last_update_clock;
    sys->last_update_clock = now;
    uint32_t ramp = dt < 128 ? 128 : (dt > 4800 ? 4800 : dt);
    sys->update_serial++;
    if (sys->cfg.flags & SA_FLAG_SYNC_IO) { sa_io_service_loads(sys); sa_io_service_streams(sys); }
    sa_cache_evict(sys);
    /* snapshots first (they change bus gains) */
    for (int b = 0; b < sys->nbuses; b++) sys->buses[b].snap_db = 0;
    for (int k = sys->live_head; k >= 0; k = sys->inst[k].next_live) {
        sa_instance *i = &sys->inst[k];
        if (!sys->events[i->event].is_snapshot) continue;
        snapshot_process(sys, i, dt);
        const sa_event *ev = &sys->events[i->event];
        float inten = i->volume < 0 ? 0 : (i->volume > 1 ? 1 : i->volume);
        /* FMOD: of several active instances of the SAME snapshot, the most recently started one has priority: it overrides the
         * older ones by its intensity (an older copy keeps weight 1 - the newest's level, so a newer copy fading in does not
         * jump). A second AudioManager starting its own copy of a volume snapshot therefore does not attenuate twice. */
        float over = 0.f;
        for (int q = sys->live_head; q >= 0; q = sys->inst[q].next_live) {
            const sa_instance *j = &sys->inst[q];
            if (j != i && j->event == i->event && j->state != SA_STATE_STOPPED && j->start_serial > i->start_serial && j->snap_level > over) over = j->snap_level;
        }
        float w = i->snap_level * (1.f - over);
        if (w > 0) {
            for (int q = 0; q < ev->nbusvol; q++) {
                const sa_busvol *bv = &sys->busvols[ev->first_busvol + q];
                sys->buses[bv->bus].snap_db += w * inten * bv->db;
            }
            if (ev->ncurve) snapshot_curve(sys, i, w * inten);
        }
    }
    buses_recompute(sys);
    orphans_service(sys);
    for (int k = sys->live_head; k >= 0;) {
        sa_instance *i = &sys->inst[k];
        int next = i->next_live;
        if (!sys->events[i->event].is_snapshot) instance_process(sys, i, now, ramp);
        if (i->released && i->state == SA_STATE_STOPPED && !i->start_pending) inst_destroy(sys, i);
        k = next;
    }
    /* callbacks run outside the lock (they may call back into the API) */
    int n = sys->npend;
    sa_pend_note local[64];
    sa_pend_note *list = n <= 64 ? local : (sa_pend_note *)malloc(sizeof(sa_pend_note) * (size_t)n);
    if (list && n) memcpy(list, sys->pend, sizeof(sa_pend_note) * (size_t)n);
    sys->npend = 0;
    sa_mutex_unlock(&sys->api);
    if (list) for (int k = 0; k < n; k++) list[k].cb(list[k].type, list[k].h, list[k].user);
    if (list && list != local) free(list);
    return SA_OK;
}

int sa_mix(sa_system *sys, float *out, int frames)
{
    if (!sys || !out || frames < 0) return SA_ERR_INVALID_PARAM;
    return sa_mix_float(sys, out, frames);
}
int sa_mix_s16(sa_system *sys, int16_t *out, int frames)
{
    if (!sys || !out || frames < 0) return SA_ERR_INVALID_PARAM;
    int done = 0;
    while (done < frames) {
        int n = frames - done; if (n > 512) n = 512;
        sa_mix_float(sys, sys->s16tmp, n);
        for (int k = 0; k < 2 * n; k++) {
            float v = sys->s16tmp[k] * 32767.f;
            out[2 * done + k] = (int16_t)(v >= 0 ? v + 0.5f : v - 0.5f);
        }
        done += n;
    }
    return frames;
}

/* ---------------- system API ---------------- */
int sa_set_master_volume(sa_system *sys, float v)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    sys->master = v < 0 ? 0 : v;
    sa_cmd c; memset(&c, 0, sizeof c); c.op = CMD_MASTER; c.g[0] = sys->master; c.voice = -1;
    sa_cmd_push(sys, &c);
    sa_mutex_unlock(&sys->api);
    return SA_OK;
}
int sa_set_listener(sa_system *sys, const sa_vec3 *pos, const sa_vec3 *vel, const sa_vec3 *fwd, const sa_vec3 *up)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    if (pos) sys->lpos = *pos;
    if (vel) sys->lvel = *vel;
    if (fwd) sys->lfwd = *fwd;
    if (up) sys->lup = *up;
    sa_vec3 r = v3cross(sys->lup, sys->lfwd);   /* left-handed (Unity/FMOD): right = up x forward */
    float l = sqrtf(v3dot(r, r));
    if (l > 1e-6f) { r.x /= l; r.y /= l; r.z /= l; } else { r.x = 1; r.y = 0; r.z = 0; }
    sys->lright = r;
    sa_mutex_unlock(&sys->api);
    return SA_OK;
}
int sa_set_global_param(sa_system *sys, const char *name, float value)
{
    if (!sys || !name) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    int g = sa_global_index(sys, name, 0);
    if (g >= 0) { sys->gvals[g] = value; sys->global_serial++; }
    sa_mutex_unlock(&sys->api);
    return g >= 0 ? SA_OK : SA_ERR_NOT_FOUND;
}
int sa_get_global_param(sa_system *sys, const char *name, float *value)
{
    if (!sys || !name) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    int g = sa_global_index(sys, name, 0);
    if (g >= 0 && value) *value = sys->gvals[g];
    sa_mutex_unlock(&sys->api);
    return g >= 0 ? SA_OK : SA_ERR_NOT_FOUND;
}
void sa_set_timing(sa_system *sys, int enabled) { if (sys) sys->timing = enabled; }
uint64_t sa_get_mix_clock(sa_system *sys) { return sys ? (uint64_t)SA_LD_ACQ(&sys->mix_clock) : 0; }
int sa_set_poll_mask(sa_system *sys, uint32_t mask)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api); sys->poll_mask = mask; sa_mutex_unlock(&sys->api);
    return SA_OK;
}
int sa_poll(sa_system *sys, sa_notification *out, int max)
{
    if (!sys || !out || max <= 0) return 0;
    sa_mutex_lock(&sys->api);
    int n = 0;
    while (n < max && sys->poll_count) {
        out[n++] = sys->pollq[sys->poll_head];
        sys->poll_head = (sys->poll_head + 1) % sys->poll_cap;
        sys->poll_count--;
    }
    sa_mutex_unlock(&sys->api);
    return n;
}
int sa_get_stats(sa_system *sys, sa_stats *o)
{
    if (!sys || !o) return SA_ERR_INVALID_PARAM;
    memset(o, 0, sizeof *o);
    sa_mutex_lock(&sys->api);
    for (int v = 0; v < sys->cfg.max_voices; v++) if (sys->vm[v].busy) o->voices_active++;
    o->voices_max = sys->cfg.max_voices;
    o->voices_tail = SA_LD_RLX(&sys->st_tails_active);
    for (int k = sys->live_head; k >= 0; k = sys->inst[k].next_live)
        for (int q = 0; q < SA_MAX_LAYERS; q++) if (sys->inst[k].layers[q].phase == L_VIRT) o->layers_virtual++;
    o->instances_live = sys->nlive; o->instances_max = sys->ninst;
    for (int s = 0; s < sys->nstreams; s++) if (SA_LD_ACQ(&sys->streams[s].state) != SA_S_FREE) o->streams_active++;
    o->streams_max = sys->nstreams;
    o->steals = sys->st_steals; o->virtualized = sys->st_virtualized; o->devirtualized = sys->st_devirt;
    o->hard_cuts = SA_LD_RLX(&sys->st_hardcuts); o->underruns = SA_LD_RLX(&sys->st_underruns);
    o->cache_hits = sys->st_hits; o->cache_misses = sys->st_misses; o->cache_evictions = sys->st_evict;
    o->load_failures = SA_LD_RLX(&sys->st_loadfail);
    o->cache_bytes = SA_LD_RLX(&sys->cache_bytes); o->cache_budget = sys->cfg.cache_budget_bytes;
    o->cmd_queue_peak = sys->st_cmd_peak; o->cmd_dropped = sys->st_cmd_drop; o->notify_dropped = sys->st_notify_drop;
    o->mixed_blocks = SA_LD_RLX(&sys->st_blocks);
    o->mixed_frames = SA_LD_ACQ(&sys->mix_clock);
    o->mix_seconds = sys->mix_seconds;
    o->io_seconds = sys->io_seconds;
    o->limiter_frames = SA_LD_RLX(&sys->st_lim_frames);
    { uint32_t b = SA_LD_RLX(&sys->st_lim_min_bits); memcpy(&o->limiter_min_gain, &b, 4); }
    { uint32_t b = SA_LD_RLX(&sys->st_pre_peak_bits); memcpy(&o->pre_limiter_peak, &b, 4); }
    o->cache_peak_bytes = SA_LD_RLX(&sys->st_cache_peak);
    o->out_errors = SA_LD_RLX(&sys->out_errors); o->out_first_error = SA_LD_RLX(&sys->out_first_error);
    sa_mutex_unlock(&sys->api);
    return SA_OK;
}
int sa_get_stats_v(sa_system *sys, double *out, int max)
{
    if (!sys || !out || max <= 0) return 0;
    sa_stats s;
    if (sa_get_stats(sys, &s) != SA_OK) return 0;
    double v[SA_STATV_COUNT];
    v[SA_STATV_VOICES_ACTIVE] = s.voices_active; v[SA_STATV_VOICES_MAX] = s.voices_max; v[SA_STATV_VOICES_TAIL] = s.voices_tail;
    v[SA_STATV_LAYERS_VIRTUAL] = s.layers_virtual; v[SA_STATV_INSTANCES_LIVE] = s.instances_live; v[SA_STATV_INSTANCES_MAX] = s.instances_max;
    v[SA_STATV_STREAMS_ACTIVE] = s.streams_active; v[SA_STATV_STREAMS_MAX] = s.streams_max;
    v[SA_STATV_STEALS] = s.steals; v[SA_STATV_VIRTUALIZED] = s.virtualized; v[SA_STATV_DEVIRTUALIZED] = s.devirtualized;
    v[SA_STATV_HARD_CUTS] = s.hard_cuts; v[SA_STATV_UNDERRUNS] = s.underruns;
    v[SA_STATV_CACHE_HITS] = s.cache_hits; v[SA_STATV_CACHE_MISSES] = s.cache_misses; v[SA_STATV_CACHE_EVICTIONS] = s.cache_evictions;
    v[SA_STATV_LOAD_FAILURES] = s.load_failures; v[SA_STATV_CACHE_BYTES] = (double)s.cache_bytes; v[SA_STATV_CACHE_BUDGET] = (double)s.cache_budget;
    v[SA_STATV_CMD_QUEUE_PEAK] = s.cmd_queue_peak; v[SA_STATV_CMD_DROPPED] = s.cmd_dropped; v[SA_STATV_NOTIFY_DROPPED] = s.notify_dropped;
    v[SA_STATV_MIXED_BLOCKS] = s.mixed_blocks; v[SA_STATV_MIXED_FRAMES] = (double)s.mixed_frames;
    v[SA_STATV_MIX_SECONDS] = s.mix_seconds; v[SA_STATV_IO_SECONDS] = s.io_seconds;
    v[SA_STATV_LIMITER_FRAMES] = s.limiter_frames; v[SA_STATV_LIMITER_MIN_GAIN] = s.limiter_min_gain;
    v[SA_STATV_PRE_LIMITER_PEAK] = s.pre_limiter_peak; v[SA_STATV_CACHE_PEAK_BYTES] = (double)s.cache_peak_bytes;
    v[SA_STATV_OUT_ERRORS] = s.out_errors; v[SA_STATV_OUT_FIRST_ERROR] = s.out_first_error;
    int n = max < SA_STATV_COUNT ? max : SA_STATV_COUNT;
    memcpy(out, v, sizeof(double) * (size_t)n);
    return n;
}

/* ---------------- descriptions ---------------- */
int sa_event_count(sa_system *sys) { return sys ? sys->nevents : 0; }
int sa_event_find(sa_system *sys, const char *p) { return sys ? sa_find_event(sys, p) : SA_ERR_INVALID_PARAM; }
int sa_event_find_guid(sa_system *sys, const void *g) { return sys && g ? sa_find_event_guid(sys, (const uint8_t *)g) : SA_ERR_INVALID_PARAM; }
int sa_event_get_info(sa_system *sys, int e, sa_event_info *o)
{
    if (!sys || !o || e < 0 || e >= sys->nevents) return SA_ERR_INVALID_PARAM;
    const sa_event *ev = &sys->events[e];
    o->path = ev->path; memcpy(o->guid, ev->guid, 16);
    o->oneshot = ev->oneshot; o->is3d = ev->is3d; o->is_snapshot = ev->is_snapshot;
    o->length_ms = ev->length_ms; o->min_distance = ev->min_dist; o->max_distance = ev->max_dist;
    o->bus = ev->bus; o->param_count = ev->nparams; o->take_count = ev->ntakes;
    return SA_OK;
}
int sa_event_get_param(sa_system *sys, int e, int k, sa_param_info *o)
{
    if (!sys || !o || e < 0 || e >= sys->nevents || k < 0 || k >= sys->events[e].nparams) return SA_ERR_INVALID_PARAM;
    const sa_param_def *p = &sys->params[sys->events[e].first_param + k];
    o->name = p->name; o->minimum = p->min; o->maximum = p->max; o->default_value = p->def; o->global = p->global;
    return SA_OK;
}
int sa_event_find_param(sa_system *sys, int e, const char *name)
{
    if (!sys || !name || e < 0 || e >= sys->nevents) return SA_ERR_INVALID_PARAM;
    const sa_event *ev = &sys->events[e];
    for (int k = 0; k < ev->nparams; k++) if (sa_strieq(sys->params[ev->first_param + k].name, name)) return k;
    return SA_ERR_NOT_FOUND;
}
int sa_event_preload(sa_system *sys, int e, int wait_ms)
{
    if (!sys || e < 0 || e >= sys->nevents) return SA_ERR_INVALID_PARAM;
    const sa_event *ev = &sys->events[e];
    sa_mutex_lock(&sys->api);
    for (int k = 0; k < ev->ntakes; k++) {
        int t = sys->take_order[ev->first_take + k];
        if (sa_take_streamed(sys, t)) continue;
        sys->entries[t].pins++;
        sa_cache_request(sys, t);
    }
    if (sys->cfg.flags & SA_FLAG_SYNC_IO) sa_io_service_loads(sys);
    sa_mutex_unlock(&sys->api);
    if (wait_ms <= 0) return SA_OK;
    double until = sa_now() + wait_ms * 0.001;
    for (;;) {
        int pending = 0;
        for (int k = 0; k < ev->ntakes; k++) {
            int t = sys->take_order[ev->first_take + k];
            if (sa_take_streamed(sys, t)) continue;
            int st = SA_LD_ACQ(&sys->entries[t].state);
            if (st == SA_E_QUEUED || st == SA_E_LOADING) pending = 1;
        }
        if (!pending) return SA_OK;
        if (sa_now() > until) return SA_ERR_TIMEOUT;
        sa_sleep_us(1000);
    }
}
int sa_event_unload(sa_system *sys, int e)
{
    if (!sys || e < 0 || e >= sys->nevents) return SA_ERR_INVALID_PARAM;
    const sa_event *ev = &sys->events[e];
    sa_mutex_lock(&sys->api);
    for (int k = 0; k < ev->ntakes; k++) { sa_entry *en = &sys->entries[sys->take_order[ev->first_take + k]]; if (en->pins > 0) en->pins--; }
    sa_mutex_unlock(&sys->api);
    return SA_OK;
}

/* ---------------- instances ---------------- */
sa_handle sa_event_create_index(sa_system *sys, int e)
{
    if (!sys || e < 0 || e >= sys->nevents) return 0;
    sa_mutex_lock(&sys->api);
    int idx = sys->free_head;
    if (idx < 0) { sa_mutex_unlock(&sys->api); return 0; }
    sa_instance *i = &sys->inst[idx];
    sys->free_head = i->next_free;
    float *params = i->params;
    uint32_t gen = (i->gen + 1) & GEN_MASK; if (!gen) gen = 1;
    memset(i, 0, sizeof *i);
    i->params = params; i->gen = gen; i->used = 1; i->event = e; i->state = SA_STATE_STOPPED;
    i->volume = 1; i->pitch = 1; i->group = -1;
    for (int k = 0; k < SA_MAX_LAYERS; k++) { i->layers[k].phase = L_FREE; i->layers[k].voice = -1; }
    const sa_event *ev = &sys->events[e];
    i->oneshot = (uint8_t)ev->oneshot;
    for (int k = 0; k < ev->nparams; k++) i->params[k] = sys->params[ev->first_param + k].def;
    i->prev_live = -1; i->next_live = sys->live_head;
    if (sys->live_head >= 0) sys->inst[sys->live_head].prev_live = idx;
    sys->live_head = idx; sys->nlive++;
    publish(sys, i);
    sa_handle h = inst_handle(sys, i);
    sa_mutex_unlock(&sys->api);
    return h;
}
sa_handle sa_event_create(sa_system *sys, const char *p) { int e = sys ? sa_find_event(sys, p) : -1; return e < 0 ? 0 : sa_event_create_index(sys, e); }
sa_handle sa_event_create_guid(sa_system *sys, const void *g) { int e = sys && g ? sa_find_event_guid(sys, (const uint8_t *)g) : -1; return e < 0 ? 0 : sa_event_create_index(sys, e); }

#define LOCKED_INST(sys, h, i) \
    if (!(sys)) return SA_ERR_INVALID_PARAM; \
    sa_mutex_lock(&(sys)->api); \
    sa_instance *i = inst_get((sys), (h)); \
    if (!i) { sa_mutex_unlock(&(sys)->api); return SA_ERR_INVALID_HANDLE; }
#define UNLOCK_RET(sys, v) do { sa_mutex_unlock(&(sys)->api); return (v); } while (0)

static uint32_t seek_pos(sa_system *sys, int take, int ms);
int sa_instance_start(sa_system *sys, sa_handle h)
{
    LOCKED_INST(sys, h, i);
    const sa_event *ev = &sys->events[i->event];
    uint32_t now = SA_LD_ACQ(&sys->mix_clock);
    i->restart = i->state != SA_STATE_STOPPED;
    i->started = 1; i->start_pending = 1; i->stopped_notify = 0;
    i->start_serial = ++sys->start_counter;
    if (ev->is_snapshot) {
        float ms = ev->fade_in_ms >= 0 ? ev->fade_in_ms : sys->cfg.snapshot_fade_ms;
        i->snap_target = 1; i->snap_rate = ms > 0 ? 1.f / (float)ms_frames(sys, ms) : 1e9f;
        if (ms <= 0) i->snap_level = 1;
        set_state(sys, i, SA_STATE_STARTING);
        UNLOCK_RET(sys, SA_OK);
    }
    if (i->restart) stop_layers(sys, i, ms_frames(sys, sys->cfg.declick_ms));
    for (int k = 0; k < SA_MAX_LAYERS; k++) if (i->layers[k].phase == L_DONE) i->layers[k].phase = L_FREE;
    i->has_deadline = 0; i->in_silent = 0;
    if (ev->ntakes > 0) i->group = best_group(sys, i, -1);
    if (ev->ntakes > 0 && group_is_silent(sys, ev, i->group)) i->in_silent = 1;   /* FMOD renders nothing here: no layer */
    else if (ev->ntakes > 0) {
        i->variation = pick_variation(sys, &sys->groups[ev->first_group + i->group]);
        int nl = free_layer(i);
        if (nl < 0) { stop_layers(sys, i, 0); for (int k = 0; k < SA_MAX_LAYERS; k++) layer_done(sys, &i->layers[k]); nl = 0; }
        sa_layer *L = &i->layers[nl];
        int take = take_of(sys, i, i->group, i->variation);
        layer_init(L, take, i->seek_ms > 0 ? seek_pos(sys, take, i->seek_ms) : 0);
        layer_request(sys, L, now);
    }
    i->seek_ms = 0;
    set_state(sys, i, SA_STATE_STARTING);
    UNLOCK_RET(sys, SA_OK);
}

/* take frame for a timeline position: loops wrap into [loop_start, loop_end); one-shots clamp to the end */
static uint32_t seek_pos(sa_system *sys, int take, int ms)
{
    const sa_take *tk = &sys->takes[take];
    double f = (double)ms * (double)tk->rate / 1000.0;
    uint32_t p = f > 4e9 ? 0xFFFFFFFFu : (uint32_t)f;
    if (tk->loop_start >= 0 && tk->loop_end > tk->loop_start && p >= (uint32_t)tk->loop_end)
        p = (uint32_t)tk->loop_start + (p - (uint32_t)tk->loop_start) % (uint32_t)(tk->loop_end - tk->loop_start);
    else if (tk->frames && p > tk->frames) p = tk->frames;
    return p;
}

int sa_instance_set_timeline_ms(sa_system *sys, sa_handle h, int ms)
{
    LOCKED_INST(sys, h, i);
    const sa_event *ev = &sys->events[i->event];
    if (ms < 0) ms = 0;
    if (ev->is_snapshot) UNLOCK_RET(sys, SA_OK);
    if (i->state == SA_STATE_STOPPED && !i->start_pending) { i->seek_ms = ms; UNLOCK_RET(sys, SA_OK); }   /* applies at start */
    uint32_t now = SA_LD_ACQ(&sys->mix_clock);
    int ml = main_layer(i);
    if (ml < 0) {
        /* takeless / silent group: move the timeline clock (a one-shot's silent end moves with it) */
        uint32_t f = (uint32_t)((double)ms * sys->cfg.output_rate / 1000.0);
        if (i->start_pending) UNLOCK_RET(sys, SA_OK);   /* clock_started is set at the start transition */
        uint32_t dur = (uint32_t)(i->silent_end - i->clock_started);
        i->clock_started = now - f;
        if (ev->ntakes == 0 || i->in_silent) i->silent_end = i->clock_started + dur;
        UNLOCK_RET(sys, SA_OK);
    }
    sa_layer *L = &i->layers[ml];
    uint32_t p = seek_pos(sys, L->take, ms);
    if (L->phase == L_WAIT && !L->resume) {
        /* not playing yet (start() earlier this frame): start the take there */
        if (L->start_pos == p) UNLOCK_RET(sys, SA_OK);
        layer_release_data(sys, L);
        L->start_pos = p;
        layer_request(sys, L, now);
        UNLOCK_RET(sys, SA_OK);
    }
    if (L->phase == L_VIRT) { L->vpos0 = p; L->vclock0 = now; UNLOCK_RET(sys, SA_OK); }
    /* playing: the current voice fades out (de-click) and the take restarts at p on another layer slot */
    int take = L->take;
    if (L->phase == L_PHYS) { send_stop(sys, L, ms_frames(sys, sys->cfg.declick_ms)); L->phase = L_FADING; L->xfade_from_layer = -1; }
    else layer_done(sys, L);
    int nl = free_layer(i);
    if (nl < 0) {
        for (int k = 0; k < SA_MAX_LAYERS && nl < 0; k++) if (i->layers[k].phase == L_FADING) { send_stop(sys, &i->layers[k], ms_frames(sys, sys->cfg.declick_ms)); layer_done(sys, &i->layers[k]); nl = k; }
    }
    if (nl >= 0) { sa_layer *N = &i->layers[nl]; layer_init(N, take, p); layer_request(sys, N, now); }
    UNLOCK_RET(sys, SA_OK);
}

int sa_instance_stop(sa_system *sys, sa_handle h, int allow_fadeout)
{
    LOCKED_INST(sys, h, i);
    const sa_event *ev = &sys->events[i->event];
    if (i->state == SA_STATE_STOPPED) UNLOCK_RET(sys, SA_OK);
    i->start_pending = 0; i->restart = 0;
    if (ev->is_snapshot) {
        float ms = ev->fade_out_ms >= 0 ? ev->fade_out_ms : sys->cfg.snapshot_fade_ms;
        i->snap_target = 0;
        if (allow_fadeout && ms > 0) { i->snap_rate = 1.f / (float)ms_frames(sys, ms); set_state(sys, i, SA_STATE_STOPPING); }
        else { i->snap_level = 0; set_state(sys, i, SA_STATE_STOPPED); i->stopped_notify = 1; }
        UNLOCK_RET(sys, SA_OK);
    }
    if (allow_fadeout && ev->nrelpt >= 2 && i->state == SA_STATE_STOPPING) UNLOCK_RET(sys, SA_OK);   /* already releasing */
    if (allow_fadeout && ev->nrelpt >= 2) {
        /* the event's measured release (bake releaseCurve): voices follow the curve; STOPPED comes when FMOD reported it
         * (releaseStopMs), and whatever of the fade is left then plays on as an orphan */
        for (int k = 0; k < SA_MAX_LAYERS; k++) {
            sa_layer *L = &i->layers[k];
            if (L->phase == L_PHYS) { send_stop_curve(sys, L, ev); L->phase = L_FADING; L->xfade_from_layer = -1; }
            else if (L->phase == L_WAIT || L->phase == L_VIRT) layer_done(sys, L);
        }
        i->has_deadline = 1;
        i->stop_deadline = SA_LD_ACQ(&sys->mix_clock) + ev->release_stop_frames;
        set_state(sys, i, SA_STATE_STOPPING);
    } else if (allow_fadeout) {
        stop_layers(sys, i, ms_frames(sys, sys->cfg.fadeout_ms));
        int active = 0;
        for (int k = 0; k < SA_MAX_LAYERS; k++) { uint8_t ph = i->layers[k].phase; if (ph != L_FREE && ph != L_DONE) active++; }
        if (active) set_state(sys, i, SA_STATE_STOPPING);
        else { set_state(sys, i, SA_STATE_STOPPED); i->stopped_notify = 1; }
    } else {
        stop_layers(sys, i, ms_frames(sys, sys->cfg.declick_ms));
        i->has_deadline = 0;
        set_state(sys, i, SA_STATE_STOPPED);
        i->stopped_notify = 1;
    }
    UNLOCK_RET(sys, SA_OK);
}

int sa_instance_release(sa_system *sys, sa_handle h) { LOCKED_INST(sys, h, i); i->released = 1; UNLOCK_RET(sys, SA_OK); }

int sa_instance_set_param_index(sa_system *sys, sa_handle h, int k, float value)
{
    LOCKED_INST(sys, h, i);
    const sa_event *ev = &sys->events[i->event];
    if (k < 0 || k >= ev->nparams) UNLOCK_RET(sys, SA_ERR_INVALID_PARAM);
    const sa_param_def *pd = &sys->params[ev->first_param + k];
    if (pd->global) UNLOCK_RET(sys, SA_ERR_INVALID_PARAM);
    if (value < pd->min) value = pd->min;
    if (value > pd->max) value = pd->max;
    i->params[k] = value;
    UNLOCK_RET(sys, SA_OK);
}
int sa_instance_set_param(sa_system *sys, sa_handle h, const char *name, float value)
{
    if (!sys || !name) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    sa_instance *i = inst_get(sys, h);
    int k = i ? sa_event_find_param(sys, i->event, name) : SA_ERR_INVALID_HANDLE;
    sa_mutex_unlock(&sys->api);
    if (k < 0) return k;
    return sa_instance_set_param_index(sys, h, k, value);
}
int sa_instance_get_param(sa_system *sys, sa_handle h, const char *name, float *value)
{
    if (!name) return SA_ERR_INVALID_PARAM;
    LOCKED_INST(sys, h, i);
    int k = sa_event_find_param(sys, i->event, name);
    if (k < 0) UNLOCK_RET(sys, k);
    if (value) *value = param_value(sys, i, k);
    UNLOCK_RET(sys, SA_OK);
}
int sa_instance_set_volume(sa_system *sys, sa_handle h, float v) { LOCKED_INST(sys, h, i); i->volume = v; UNLOCK_RET(sys, SA_OK); }
int sa_instance_get_volume(sa_system *sys, sa_handle h, float *v) { LOCKED_INST(sys, h, i); if (v) *v = i->volume; UNLOCK_RET(sys, SA_OK); }
int sa_instance_set_pitch(sa_system *sys, sa_handle h, float p) { LOCKED_INST(sys, h, i); i->pitch = p; UNLOCK_RET(sys, SA_OK); }
int sa_instance_get_pitch(sa_system *sys, sa_handle h, float *p) { LOCKED_INST(sys, h, i); if (p) *p = i->pitch; UNLOCK_RET(sys, SA_OK); }
int sa_instance_set_paused(sa_system *sys, sa_handle h, int p) { LOCKED_INST(sys, h, i); i->paused = (uint8_t)(p != 0); UNLOCK_RET(sys, SA_OK); }
int sa_instance_get_paused(sa_system *sys, sa_handle h, int *p) { LOCKED_INST(sys, h, i); if (p) *p = i->paused; UNLOCK_RET(sys, SA_OK); }
int sa_instance_set_3d(sa_system *sys, sa_handle h, const sa_vec3 *pos, const sa_vec3 *vel)
{
    LOCKED_INST(sys, h, i);
    if (pos) i->pos = *pos;
    if (vel) i->vel = *vel;
    i->has3d = 1;
    UNLOCK_RET(sys, SA_OK);
}
int sa_instance_get_playback_state(sa_system *sys, sa_handle h)
{
    if (!sys || !h) return SA_ERR_INVALID_HANDLE;
    uint32_t idx = h & IDX_MASK;
    if ((int)idx >= sys->ninst) return SA_ERR_INVALID_HANDLE;
    uint32_t w = SA_LD_ACQ(&sys->inst_status[idx]);
    if (!(w & ST_VALID) || (w >> 8) != (h >> IDX_BITS)) return SA_ERR_INVALID_HANDLE;
    return (int)(w & 7u);
}
int sa_instance_is_valid(sa_system *sys, sa_handle h) { return sa_instance_get_playback_state(sys, h) >= 0; }
int sa_instance_get_event(sa_system *sys, sa_handle h) { LOCKED_INST(sys, h, i); UNLOCK_RET(sys, i->event); }
int sa_instance_get_timeline_ms(sa_system *sys, sa_handle h, int *ms)
{
    LOCKED_INST(sys, h, i);
    int ml = main_layer(i);
    uint32_t now = SA_LD_ACQ(&sys->mix_clock);
    int v = 0;
    if (ml >= 0) {
        sa_layer *L = &i->layers[ml];
        uint32_t p = pos_now(sys, L, now);
        int rate = layer_rate(sys, L);
        v = (int)((double)p * 1000.0 / (double)(rate > 0 ? rate : 48000));
    } else if (i->state != SA_STATE_STOPPED) v = (int)((double)(uint32_t)(now - i->clock_started) * 1000.0 / sys->cfg.output_rate);
    if (ms) *ms = v;
    UNLOCK_RET(sys, SA_OK);
}
int sa_instance_set_callback(sa_system *sys, sa_handle h, sa_event_callback fn, uint32_t mask)
{
    LOCKED_INST(sys, h, i);
    i->cb = fn; i->cb_mask = mask;
    UNLOCK_RET(sys, SA_OK);
}
int sa_instance_set_user_data(sa_system *sys, sa_handle h, void *u) { LOCKED_INST(sys, h, i); i->user = u; UNLOCK_RET(sys, SA_OK); }
int sa_instance_get_user_data(sa_system *sys, sa_handle h, void **u) { LOCKED_INST(sys, h, i); if (u) *u = i->user; UNLOCK_RET(sys, SA_OK); }

/* ---------------- buses ---------------- */
int sa_bus_count(sa_system *sys) { return sys ? sys->nbuses : 0; }
int sa_bus_find(sa_system *sys, const char *p) { return sys ? sa_find_bus(sys, p) : SA_ERR_INVALID_PARAM; }
const char *sa_bus_path(sa_system *sys, int b) { return sys && b >= 0 && b < sys->nbuses ? sys->buses[b].path : NULL; }
#define LOCKED_BUS(sys, path, b) \
    if (!(sys) || !(path)) return SA_ERR_INVALID_PARAM; \
    sa_mutex_lock(&(sys)->api); \
    int b = sa_find_bus((sys), (path)); \
    if (b < 0) { sa_mutex_unlock(&(sys)->api); return SA_ERR_NOT_FOUND; }
int sa_bus_set_volume(sa_system *sys, const char *p, float v) { LOCKED_BUS(sys, p, b); sys->buses[b].volume = v < 0 ? 0 : v; buses_recompute(sys); UNLOCK_RET(sys, SA_OK); }
int sa_bus_get_volume(sa_system *sys, const char *p, float *v) { LOCKED_BUS(sys, p, b); if (v) *v = sys->buses[b].volume; UNLOCK_RET(sys, SA_OK); }
int sa_bus_set_mute(sa_system *sys, const char *p, int m) { LOCKED_BUS(sys, p, b); sys->buses[b].mute = m != 0; buses_recompute(sys); UNLOCK_RET(sys, SA_OK); }
int sa_bus_set_paused(sa_system *sys, const char *p, int m) { LOCKED_BUS(sys, p, b); sys->buses[b].paused = m != 0; buses_recompute(sys); UNLOCK_RET(sys, SA_OK); }
int sa_bus_stop_all(sa_system *sys, const char *p, int allow_fadeout)
{
    if (!sys || !p) return SA_ERR_INVALID_PARAM;
    sa_mutex_lock(&sys->api);
    int b = sa_find_bus(sys, p);
    if (b < 0) UNLOCK_RET(sys, SA_ERR_NOT_FOUND);
    sa_handle hs[256]; int n = 0;
    for (int k = sys->live_head; k >= 0; k = sys->inst[k].next_live) {
        sa_instance *i = &sys->inst[k];
        const sa_event *ev = &sys->events[i->event];
        if (ev->is_snapshot || i->state == SA_STATE_STOPPED || !bus_is_under(sys, ev->bus, b)) continue;
        if (n < 256) hs[n++] = inst_handle(sys, i);
    }
    sa_mutex_unlock(&sys->api);
    for (int k = 0; k < n; k++) sa_instance_stop(sys, hs[k], allow_fadeout);
    return SA_OK;
}

/* ---------------- snapshots ---------------- */
sa_handle sa_snapshot_start(sa_system *sys, const char *p)
{
    sa_handle h = sa_event_create(sys, p);
    if (h) sa_instance_start(sys, h);
    return h;
}
int sa_snapshot_stop(sa_system *sys, sa_handle h, int allow_fadeout)
{
    int r = sa_instance_stop(sys, h, allow_fadeout);
    if (r == SA_OK) sa_instance_release(sys, h);
    return r;
}

#if !defined(__vita__)
int sa_vita_out_start(sa_system *sys, const sa_vita_out_config *cfg) { (void)sys; (void)cfg; return SA_ERR_UNSUPPORTED; }
int sa_vita_out_stop(sa_system *sys) { (void)sys; return SA_ERR_UNSUPPORTED; }
#endif
