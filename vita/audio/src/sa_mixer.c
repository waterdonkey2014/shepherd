/* The mixer: command queue consumer, voice rendering (gather -> linear-interpolation resample -> gain-ramped
 * accumulate), tails for click-free stealing, master gain, output conversion. Runs on the output thread; never
 * blocks, never allocates, never takes the API mutex. */
#include "sa_internal.h"

#define ONE_FX ((uint64_t)1 << 32)

uint64_t sa_step_fx(float step)
{
    if (!(step > 0)) step = 1.0f / 64;
    if (step > SA_MAX_STEP) step = SA_MAX_STEP;
    return (uint64_t)((double)step * 4294967296.0 + 0.5);
}

int sa_mixer_init(sa_system *sys)
{
    int nv = sys->cfg.max_voices;
    sys->voices = (sa_voice *)calloc((size_t)nv, sizeof(sa_voice));
    sys->pub_end_gen = (uint32_t *)calloc((size_t)nv, sizeof(uint32_t));
    sys->pub_pos = (uint32_t *)calloc((size_t)nv, sizeof(uint32_t));
    sys->pub_pos_gen = (uint32_t *)calloc((size_t)nv, sizeof(uint32_t));
    sys->cmdq = (sa_cmd *)calloc(SA_CMDQ_CAP, sizeof(sa_cmd));
    sys->acc = (float *)malloc(sizeof(float) * 2 * SA_SUBBLOCK);
    sys->scratch_frames = (int)(SA_SUBBLOCK * SA_MAX_STEP) + 8;
    sys->scratch = (int16_t *)malloc(sizeof(int16_t) * 2 * (size_t)sys->scratch_frames);
    sys->fscratch = (float *)malloc(sizeof(float) * 2 * (size_t)sys->scratch_frames);
    if (!sys->voices || !sys->pub_end_gen || !sys->pub_pos || !sys->pub_pos_gen || !sys->cmdq || !sys->acc || !sys->scratch || !sys->fscratch) return SA_ERR_NO_MEMORY;
    memset(sys->tails, 0, sizeof sys->tails);
    sys->master_cur = sys->master_t = 1.0f;
    sys->lim_gain = 1.0f;
    { float one = 1.0f; uint32_t u; memcpy(&u, &one, 4); sys->st_lim_min_bits = u; sys->st_pre_peak_bits = 0; sys->st_lim_frames = 0; }
    sys->lim_rel = 1.0f - expf(-1.0f / (0.050f * (float)sys->cfg.output_rate));
    return SA_OK;
}
void sa_mixer_free(sa_system *sys)
{
    free(sys->voices); free(sys->pub_end_gen); free(sys->pub_pos); free(sys->pub_pos_gen); free(sys->cmdq); free(sys->acc); free(sys->scratch); free(sys->fscratch);
}

/* ---------------- command queue ---------------- */
int sa_cmd_push(sa_system *sys, const sa_cmd *c)
{
    uint32_t w = SA_LD_RLX(&sys->cmd_w);
    uint32_t r = SA_LD_ACQ(&sys->cmd_r);
    if (w - r >= SA_CMDQ_CAP) {
        if (!SA_LD_ACQ(&sys->out_thread)) sa_mixer_drain_cmds(sys);    /* same thread renders: apply now */
        else for (int i = 0; i < 40 && w - SA_LD_ACQ(&sys->cmd_r) >= SA_CMDQ_CAP; i++) sa_sleep_us(100);
        r = SA_LD_ACQ(&sys->cmd_r);
        if (w - r >= SA_CMDQ_CAP) { sys->st_cmd_drop++; return SA_ERR_FULL; }
    }
    sys->cmdq[w & (SA_CMDQ_CAP - 1)] = *c;
    SA_ST_REL(&sys->cmd_w, w + 1);
    if (w + 1 - r > sys->st_cmd_peak) sys->st_cmd_peak = w + 1 - r;
    return SA_OK;
}

static void voice_release_refs(sa_voice *v)
{
    if (v->entry) { SA_SUB(&v->entry->refs, 1); v->entry = NULL; }
    if (v->stream) { SA_SUB(&v->stream->mixer_refs, 1); v->stream = NULL; }
}
static void voice_end(sa_system *sys, sa_voice *v, int slot)
{
    voice_release_refs(v);
    v->active = 0;
    if (slot >= 0) SA_ST_REL(&sys->pub_end_gen[slot], v->gen);
}
static void env_ramp(sa_voice *v, float target, uint32_t frames, int end)
{
    v->env_t = target;
    if (frames == 0) { v->env = target; v->env_left = 0; v->env_d = 0; }
    else { v->env_left = frames; v->env_d = (target - v->env) / (float)frames; }
    v->env_end = (uint8_t)end;
}
static uint32_t declick_frames(sa_system *sys)
{
    uint32_t n = (uint32_t)(sys->cfg.declick_ms * 0.001f * (float)sys->cfg.output_rate);
    return n < 16 ? 16 : n;
}
static void move_to_tail(sa_system *sys, sa_voice *v)
{
    if (v->paused_req && v->pg <= 0.f) { voice_release_refs(v); v->active = 0; return; }   /* frozen: already silent */
    for (int i = 0; i < SA_MAX_TAILS; i++) {
        sa_voice *t = &sys->tails[i];
        if (t->active) continue;
        *t = *v;
        t->rel = NULL;
        env_ramp(t, 0.0f, declick_frames(sys), 1);
        t->xfade_voice = -1; t->aligning = 0; t->paused_req = 0;
        v->active = 0; v->entry = NULL; v->stream = NULL;
        return;
    }
    SA_ADD(&sys->st_hardcuts, 1u);
    voice_release_refs(v);
    v->active = 0;
}

static void exec_start(sa_system *sys, const sa_cmd *c)
{
    sa_voice *v = &sys->voices[c->voice];
    if (v->active) move_to_tail(sys, v);
    memset(v, 0, sizeof *v);
    v->gen = c->gen; v->active = 1; v->kind = c->src_kind; v->channels = c->channels;
    v->frames = c->frames; v->loop_start = c->loop_start; v->loop_end = c->loop_end;
    v->xfade_voice = -1;
    if (c->src_kind == SA_SRC_STREAM) {
        v->stream = (sa_stream *)c->src;
        v->src_pos = v->stream->start_frame;
    } else {
        v->entry = (sa_entry *)c->src;
        v->src_pos = c->start_pos;
        if (c->src_kind == SA_SRC_PCM) v->pcm = (const int16_t *)v->entry->data;
        else {
            v->adpcm = (const uint8_t *)v->entry->data; v->fmt = v->entry->fmt; v->block_bytes = v->entry->block_bytes;
            v->fpb = v->entry->fpb; v->nblocks = v->entry->nblocks;
        }
    }
    v->play_pos = v->src_pos;
    for (int k = 0; k < 4; k++) { v->g[k] = v->gt[k] = c->g[k]; v->gd[k] = 0; }
    v->step = v->step_target = sa_step_fx(c->step);
    v->env = 0; v->fade_in = c->ramp; v->pg = 1.0f;
    if (c->flags & 1) { v->paused_req = 1; v->pg = 0.0f; }   /* started while paused: frozen */
    if (c->xfade_voice >= 0) {
        sa_voice *o = &sys->voices[c->xfade_voice];
        if (o->active && o->gen == c->xfade_gen) {
            v->xfade_voice = c->xfade_voice; v->xfade_gen = c->xfade_gen; v->xfade_frames = c->xfade ? c->xfade : 1;
            if (c->src_kind != SA_SRC_STREAM) {          /* RAM sources align at once */
                uint32_t p = o->play_pos;
                if (v->loop_end > 0 && p >= (uint32_t)v->loop_end) p = (uint32_t)v->loop_start;
                if (v->frames && p >= v->frames) p = v->frames ? v->frames - 1 : 0;
                v->src_pos = v->play_pos = p;
            } else v->aligning = 1;
        }
    }
    if (v->kind == SA_SRC_ADPCM) sa_adpcm_seek(&v->ac, v->fpb, v->src_pos);
    if (!v->aligning) {
        if (v->xfade_voice >= 0) {
            sa_voice *o = &sys->voices[v->xfade_voice];
            env_ramp(v, 1.0f, v->xfade_frames, 0);
            env_ramp(o, 0.0f, v->xfade_frames, 1);
            v->xfade_voice = -1;
        } else env_ramp(v, 1.0f, v->fade_in, 0);
    }
    SA_ST_RLX(&sys->pub_pos[c->voice], v->play_pos);
    SA_ST_REL(&sys->pub_pos_gen[c->voice], v->gen);
}

static void exec(sa_system *sys, const sa_cmd *c)
{
    if (c->op == CMD_MASTER) { sys->master_t = c->g[0]; return; }
    if (c->voice < 0 || c->voice >= sys->cfg.max_voices) return;
    if (c->op == CMD_START) { exec_start(sys, c); return; }
    sa_voice *v = &sys->voices[c->voice];
    if (!v->active || v->gen != c->gen) return;
    switch (c->op) {
    case CMD_SET:
        for (int k = 0; k < 4; k++) v->gt[k] = c->g[k];
        if (c->ramp == 0) { for (int k = 0; k < 4; k++) { v->g[k] = c->g[k]; v->gd[k] = 0; } v->g_left = 0; }
        else { for (int k = 0; k < 4; k++) v->gd[k] = (v->gt[k] - v->g[k]) / (float)c->ramp; v->g_left = c->ramp; }
        v->step_target = sa_step_fx(c->step);
        break;
    case CMD_STOP:
        v->aligning = 0; v->xfade_voice = -1;
        if (v->paused_req && v->pg <= 0.f) voice_end(sys, v, c->voice);
        else if (c->src && c->frames >= 2) {
            /* release curve: (frame, gain) pairs from frame 0, scaled by the envelope at the stop; walked per sub-block */
            v->rel = (const float *)c->src; v->rel_n = c->frames; v->rel_i = 0; v->rel_t = 0;
            v->rel_scale = v->env; v->env_left = 0; v->env_d = 0;   /* a fade-in in progress stops where it is */
            v->env_end = 1;
        }
        else if (c->ramp == 0) voice_end(sys, v, c->voice);
        else { v->rel = NULL; env_ramp(v, 0.0f, c->ramp, 1); }
        break;
    case CMD_PAUSE:
        v->paused_req = c->flags ? 1 : 0;
        break;
    }
}

void sa_mixer_drain_cmds(sa_system *sys)
{
    uint32_t r = SA_LD_RLX(&sys->cmd_r), w = SA_LD_ACQ(&sys->cmd_w);
    while (r != w) { exec(sys, &sys->cmdq[r & (SA_CMDQ_CAP - 1)]); r++; }
    SA_ST_REL(&sys->cmd_r, r);
}

/* ---------------- kernels ----------------
 * The gather stage produces int16 frames; sa_s16_to_f32 converts them once per sub-block, then the kernels work on
 * floats (no per-tap int->float moves, which are slow on the Cortex-A9's VFP). acc is interleaved stereo. Gains are
 * pre-scaled by 1/32768 and ramp linearly (g += d per frame). NEON fast paths for step == 1.0 (the common case: takes
 * are baked at the output rate); the interpolating path is scalar. */
#if defined(__ARM_NEON) || defined(__ARM_NEON__)
#include <arm_neon.h>
#define SA_NEON 1
#endif
#define FRAC_SCALE (1.0f / 4294967296.0f)

void sa_s16_to_f32(const int16_t *s, float *d, int n)
{
    int k = 0;
#ifdef SA_NEON
    for (; k + 8 <= n; k += 8) {
        int16x8_t v = vld1q_s16(s + k);
        vst1q_f32(d + k, vcvtq_f32_s32(vmovl_s16(vget_low_s16(v))));
        vst1q_f32(d + k + 4, vcvtq_f32_s32(vmovl_s16(vget_high_s16(v))));
    }
#endif
    for (; k < n; k++) d[k] = (float)s[k];
}

void sa_kernel_mono(const float *s, int n, uint64_t *frac, uint64_t step, float *acc, float g0, float g2, float d0, float d2)
{
    uint64_t p = *frac;
    if (step == ONE_FX && (uint32_t)p == 0) {
        const float *a = s + (uint32_t)(p >> 32);
        int k = 0;
#ifdef SA_NEON
        if (n >= 4) {
            const float ramp[4] = { 0.f, 1.f, 2.f, 3.f };
            float32x4_t r = vld1q_f32(ramp);
            float32x4_t gl = vmlaq_n_f32(vdupq_n_f32(g0), r, d0), gr = vmlaq_n_f32(vdupq_n_f32(g2), r, d2);
            float32x4_t dl = vdupq_n_f32(4.f * d0), dr = vdupq_n_f32(4.f * d2);
            for (; k + 4 <= n; k += 4) {
                float32x4_t x = vld1q_f32(a + k);
                float32x4x2_t o = vld2q_f32(acc + 2 * k);
                o.val[0] = vmlaq_f32(o.val[0], x, gl);
                o.val[1] = vmlaq_f32(o.val[1], x, gr);
                vst2q_f32(acc + 2 * k, o);
                gl = vaddq_f32(gl, dl); gr = vaddq_f32(gr, dr);
            }
            g0 += d0 * (float)k; g2 += d2 * (float)k;
        }
#endif
        for (; k < n; k++) {
            float x = a[k];
            acc[2 * k] += x * g0; acc[2 * k + 1] += x * g2;
            g0 += d0; g2 += d2;
        }
        *frac = p + (uint64_t)n * step;
        return;
    }
    for (int k = 0; k < n; k++) {
        uint32_t i = (uint32_t)(p >> 32);
        float w = (float)(uint32_t)p * FRAC_SCALE;
        float x0 = s[i];
        float x = x0 + (s[i + 1] - x0) * w;
        acc[2 * k] += x * g0; acc[2 * k + 1] += x * g2;
        g0 += d0; g2 += d2;
        p += step;
    }
    *frac = p;
}

void sa_kernel_stereo(const float *s, int n, uint64_t *frac, uint64_t step, float *acc, const float *g, const float *d)
{
    uint64_t p = *frac;
    float g0 = g[0], g1 = g[1], g2 = g[2], g3 = g[3], d0 = d[0], d1 = d[1], d2 = d[2], d3 = d[3];
    int cross = !(g1 == 0 && g2 == 0 && d1 == 0 && d2 == 0);
    if (step == ONE_FX && (uint32_t)p == 0) {
        const float *a = s + 2 * (uint32_t)(p >> 32);
        int k = 0;
#ifdef SA_NEON
        if (n >= 4) {
            const float ramp[4] = { 0.f, 1.f, 2.f, 3.f };
            float32x4_t r = vld1q_f32(ramp);
            float32x4_t v0 = vmlaq_n_f32(vdupq_n_f32(g0), r, d0), v3 = vmlaq_n_f32(vdupq_n_f32(g3), r, d3);
            float32x4_t e0 = vdupq_n_f32(4.f * d0), e3 = vdupq_n_f32(4.f * d3);
            if (!cross) {
                for (; k + 4 <= n; k += 4) {
                    float32x4x2_t x = vld2q_f32(a + 2 * k), o = vld2q_f32(acc + 2 * k);
                    o.val[0] = vmlaq_f32(o.val[0], x.val[0], v0);
                    o.val[1] = vmlaq_f32(o.val[1], x.val[1], v3);
                    vst2q_f32(acc + 2 * k, o);
                    v0 = vaddq_f32(v0, e0); v3 = vaddq_f32(v3, e3);
                }
            } else {
                float32x4_t v1 = vmlaq_n_f32(vdupq_n_f32(g1), r, d1), v2 = vmlaq_n_f32(vdupq_n_f32(g2), r, d2);
                float32x4_t e1 = vdupq_n_f32(4.f * d1), e2 = vdupq_n_f32(4.f * d2);
                for (; k + 4 <= n; k += 4) {
                    float32x4x2_t x = vld2q_f32(a + 2 * k), o = vld2q_f32(acc + 2 * k);
                    o.val[0] = vmlaq_f32(vmlaq_f32(o.val[0], x.val[0], v0), x.val[1], v1);
                    o.val[1] = vmlaq_f32(vmlaq_f32(o.val[1], x.val[0], v2), x.val[1], v3);
                    vst2q_f32(acc + 2 * k, o);
                    v0 = vaddq_f32(v0, e0); v1 = vaddq_f32(v1, e1); v2 = vaddq_f32(v2, e2); v3 = vaddq_f32(v3, e3);
                }
            }
            g0 += d0 * (float)k; g1 += d1 * (float)k; g2 += d2 * (float)k; g3 += d3 * (float)k;
        }
#endif
        for (; k < n; k++) {
            float l = a[2 * k], r = a[2 * k + 1];
            acc[2 * k] += l * g0 + r * g1; acc[2 * k + 1] += l * g2 + r * g3;
            g0 += d0; g1 += d1; g2 += d2; g3 += d3;
        }
        *frac = p + (uint64_t)n * step;
        return;
    }
    for (int k = 0; k < n; k++) {
        uint32_t i = (uint32_t)(p >> 32);
        float w = (float)(uint32_t)p * FRAC_SCALE;
        const float *a = s + 2 * i;
        float l0 = a[0], r0 = a[1];
        float l = l0 + (a[2] - l0) * w, r = r0 + (a[3] - r0) * w;
        acc[2 * k] += l * g0 + r * g1; acc[2 * k + 1] += l * g2 + r * g3;
        g0 += d0; g1 += d1; g2 += d2; g3 += d3;
        p += step;
    }
    *frac = p;
}

/* ---------------- sources ---------------- */
/* fetch up to n frames into dst; returns frames, and sets *ended when the source has no more frames,
 * *underrun when a stream ring ran dry */
static int src_fetch(sa_system *sys, sa_voice *v, int16_t *dst, int n, int *ended, int *underrun)
{
    int ch = v->channels, done = 0;
    *ended = 0; *underrun = 0;
    int looping = v->loop_end > 0 && v->loop_end > v->loop_start;
    while (done < n) {
        if (looping && v->src_pos >= (uint32_t)v->loop_end) {
            v->src_pos = (uint32_t)v->loop_start;
            if (v->kind == SA_SRC_ADPCM) {
                if (v->have_loop_ac) { v->ac = v->loop_ac; v->ac.skip = (uint32_t)v->loop_start % (uint32_t)v->fpb; }
                else sa_adpcm_seek(&v->ac, v->fpb, v->src_pos);
            }
        }
        uint32_t limit = looping ? (uint32_t)v->loop_end : (v->frames ? v->frames : 0xFFFFFFFFu);
        if (v->src_pos >= limit) { *ended = 1; break; }
        uint32_t k = (uint32_t)(n - done);
        if (k > limit - v->src_pos) k = limit - v->src_pos;
        if (v->kind == SA_SRC_PCM) {
            memcpy(dst + (size_t)done * ch, v->pcm + (size_t)v->src_pos * ch, (size_t)k * ch * sizeof(int16_t));
        } else if (v->kind == SA_SRC_ADPCM) {
            int got = sa_adpcm_decode_cap(v->fmt, v->adpcm, v->nblocks, v->block_bytes, v->fpb, ch, &v->ac, dst + (size_t)done * ch, (int)k,
                                          looping ? (uint32_t)v->loop_start / (uint32_t)v->fpb : 0xFFFFFFFFu, &v->loop_ac, &v->have_loop_ac);
            if ((uint32_t)got < k) { v->frames = v->src_pos + (uint32_t)got; k = (uint32_t)got; if (!k) { *ended = 1; break; } }
        } else {
            sa_stream *s = v->stream;
            uint32_t w = SA_LD_ACQ(&s->wpos), r = SA_LD_RLX(&s->rpos);
            uint32_t avail = w - r;
            if (avail == 0) {
                if (SA_LD_ACQ(&s->eof) && SA_LD_ACQ(&s->wpos) == r) { *ended = 1; break; }
                *underrun = 1; break;
            }
            if (k > avail) k = avail;
            uint32_t p = r & s->mask, first = s->cap - p; if (first > k) first = k;
            memcpy(dst + (size_t)done * ch, s->ring + (size_t)p * ch, (size_t)first * ch * sizeof(int16_t));
            if (k > first) memcpy(dst + (size_t)(done + first) * ch, s->ring, (size_t)(k - first) * ch * sizeof(int16_t));
            SA_ST_REL(&s->rpos, r + k);
        }
        v->src_pos += k; done += (int)k;
    }
    return done;
}

/* stream voice waiting to line up with the voice it crossfades from */
static void stream_align(sa_system *sys, sa_voice *v, int n)
{
    sa_voice *o = &sys->voices[v->xfade_voice];
    int ok = o->active && o->gen == v->xfade_gen;
    int go = !ok;
    if (ok) {
        uint32_t target = o->play_pos, head = v->src_pos;
        if (head < target) {
            sa_stream *s = v->stream;
            uint32_t w = SA_LD_ACQ(&s->wpos), r = SA_LD_RLX(&s->rpos), avail = w - r, k = target - head;
            if (v->loop_end > 0 && head + k > (uint32_t)v->loop_end) k = (uint32_t)v->loop_end - head;
            if (k > avail) k = avail;
            SA_ST_REL(&s->rpos, r + k);
            v->src_pos += k; v->play_pos = v->src_pos;
            if (v->loop_end > 0 && v->src_pos >= (uint32_t)v->loop_end) v->src_pos = v->play_pos = (uint32_t)v->loop_start;
            if (v->src_pos == target) go = 1;
        } else if (head == target) go = 1;
        v->align_wait += (uint32_t)n;
        if (v->align_wait > (uint32_t)sys->cfg.output_rate * 2u) go = 1;   /* give up after ~2 s */
    }
    if (go) {
        v->aligning = 0;
        env_ramp(v, 1.0f, v->xfade_frames, 0);
        if (ok) env_ramp(o, 0.0f, v->xfade_frames, 1);
        v->xfade_voice = -1;
    }
}

/* render one voice into acc for n frames; slot >= 0 for physical voices (published), -1 for tails */
static void voice_render(sa_system *sys, sa_voice *v, int slot, int n)
{
    if (!v->active) return;
    if (v->aligning) { stream_align(sys, v, n); if (v->aligning) return; }
    /* pause gain: ramps to 0 over the de-click time, then the voice freezes (no source advance) */
    float dc = (float)declick_frames(sys);
    float pg0 = v->pg, pg1;
    if (v->paused_req) {
        if (pg0 <= 0.f) goto publish;   /* frozen */
        pg1 = pg0 - (float)n / dc; if (pg1 < 0.f) pg1 = 0.f;
    } else { pg1 = pg0 + (float)n / dc; if (pg1 > 1.f) pg1 = 1.f; }
    v->pg = pg1;
    /* envelope + gain ramps: endpoints at the sub-block boundaries, linear in between */
    float env0 = v->env, env1;
    if (v->rel) {
        /* release curve: evaluated at the sub-block end (piecewise linear in gain) */
        v->rel_t += (uint32_t)n;
        float t = (float)v->rel_t;
        while (v->rel_i + 1 < v->rel_n && v->rel[2 * (v->rel_i + 1)] <= t) v->rel_i++;
        if (v->rel_i + 1 >= v->rel_n) env1 = 0.0f;
        else {
            float t0 = v->rel[2 * v->rel_i], g0 = v->rel[2 * v->rel_i + 1], t1 = v->rel[2 * (v->rel_i + 1)], g1 = v->rel[2 * (v->rel_i + 1) + 1];
            env1 = v->rel_scale * (g0 + (g1 - g0) * (t1 > t0 ? (t - t0) / (t1 - t0) : 1.0f));
        }
        if (env1 <= 0.0f) { env1 = 0.0f; v->rel = NULL; v->env_left = 0; }
    }
    else if (v->env_left == 0) env1 = v->env;
    else if ((uint32_t)n >= v->env_left) { env1 = v->env_t; v->env_left = 0; v->env_d = 0; }
    else { env1 = v->env + v->env_d * (float)n; v->env_left -= (uint32_t)n; }
    v->env = env1;
    float g0[4], g1[4];
    uint32_t gl = v->g_left;
    for (int k = 0; k < 4; k++) {
        float cur = v->g[k], after;
        if (gl == 0) after = cur;
        else if ((uint32_t)n >= gl) after = v->gt[k];
        else after = cur + v->gd[k] * (float)n;
        v->g[k] = after;
        g0[k] = cur * env0 * pg0 * (1.0f / 32768.0f);
        g1[k] = after * env1 * pg1 * (1.0f / 32768.0f);
    }
    if (gl) { v->g_left = (uint32_t)n >= gl ? 0 : gl - (uint32_t)n; if (!v->g_left) for (int k = 0; k < 4; k++) v->gd[k] = 0; }
    v->step = v->step_target;   /* pitch changes apply per sub-block */
    {
        int ch = v->channels;
        uint64_t span = v->frac + (uint64_t)(n - 1) * v->step;
        int need = (int)(span >> 32) + 2;
        uint64_t endp = v->frac + (uint64_t)n * v->step;
        int adv = (int)(endp >> 32);
        if (adv + 1 > need) need = adv + 1;
        int16_t *sc = sys->scratch;
        int real = v->ncarry;
        if (real) memcpy(sc, v->carry, (size_t)real * ch * sizeof(int16_t));
        if (!v->src_done && need > real) {
            int ended = 0, under = 0;
            real += src_fetch(sys, v, sc + (size_t)real * ch, need - real, &ended, &under);
            if (ended) v->src_done = 1;
            else if (under) {
                /* stream underrun: render only what the data covers, keep the position, silence the rest */
                SA_ADD(&sys->st_underruns, 1u);
                if (v->stream) v->stream->underruns++;
                int covered = 0; uint64_t p = v->frac;
                while (covered < n && (int)(p >> 32) + 1 < real) { covered++; p += v->step; }
                n = covered; endp = p; adv = (int)(endp >> 32); need = real;
            }
        }
        if (real < need) memset(sc + (size_t)real * ch, 0, (size_t)(need - real) * ch * sizeof(int16_t));
        if (n > 0) {
            float d[4], inv = 1.0f / (float)n;
            for (int k = 0; k < 4; k++) d[k] = (g1[k] - g0[k]) * inv;
            uint64_t fr = v->frac;
            float *fs = sys->fscratch;
            sa_s16_to_f32(sc, fs, need * ch);
            if (ch == 1) sa_kernel_mono(fs, n, &fr, v->step, sys->acc, g0[0], g0[2], d[0], d[2]);
            else sa_kernel_stereo(fs, n, &fr, v->step, sys->acc, g0, d);
        }
        v->frac = endp & 0xFFFFFFFFu;
        /* carry the fetched frames at and after the new integer position (at most 2) */
        int keep = need - adv; if (keep > 2) keep = 2;
        if (adv + keep > real) keep = real - adv;
        if (keep < 0) keep = 0;
        if (keep) memmove(v->carry, sc + (size_t)adv * ch, (size_t)keep * ch * sizeof(int16_t));
        v->ncarry = keep;
        uint32_t back = (uint32_t)(real > adv ? real - adv : 0);
        v->play_pos = v->src_pos >= back ? v->src_pos - back : 0;
        if (v->src_done && adv >= real) { voice_end(sys, v, slot); return; }
    }
    if (v->env_end && v->env_left == 0 && v->env <= 0.0f) { voice_end(sys, v, slot); return; }
publish:
    if (slot >= 0) { SA_ST_RLX(&sys->pub_pos[slot], v->play_pos); SA_ST_REL(&sys->pub_pos_gen[slot], v->gen); }
}

int sa_mix_float(sa_system *sys, float *out, int frames)
{
    double t0 = sys->timing ? sa_now() : 0;
    sa_mixer_drain_cmds(sys);
    if (sys->cfg.flags & SA_FLAG_SYNC_IO) { double ti = sys->timing ? sa_now() : 0; sa_io_service_streams(sys); if (sys->timing) sys->io_seconds += sa_now() - ti; }
    int done = 0;
    while (done < frames) {
        int n = frames - done; if (n > SA_SUBBLOCK) n = SA_SUBBLOCK;
        memset(sys->acc, 0, sizeof(float) * 2 * (size_t)n);
        for (int i = 0; i < sys->cfg.max_voices; i++) voice_render(sys, &sys->voices[i], i, n);
        int tails = 0;
        for (int i = 0; i < SA_MAX_TAILS; i++) if (sys->tails[i].active) { voice_render(sys, &sys->tails[i], -1, n); tails++; }
        SA_ST_RLX(&sys->st_tails_active, tails);
        /* master: linear ramp over the sub-block */
        float m0 = sys->master_cur, m1 = sys->master_t;
        float dm = (m1 - m0) / (float)n;
        float *o = out + (size_t)done * 2;
        int limit = sys->cfg.limiter > 0;
        float peak = 0.f;   /* Stage C diagnostics: the master sum's peak before the limiter */
        if (limit && sys->lim_gain >= 1.f) {
            /* common case: nothing near full scale in this sub-block -> plain gain (vectorisable), no limiter state */
            float mx = 0.f, mm = m0 > m1 ? m0 : m1;
            for (int k = 0; k < 2 * n; k++) { float a = fabsf(sys->acc[k]); mx = a > mx ? a : mx; }
            peak = mx * mm;
            if (mx * mm <= 0.98855f) limit = 0;
        }
        if (limit) {
            /* peak limiter: instant attack to keep |x| <= 0.989 (-0.1 dBFS), exponential release */
            const float thr = 0.98855f; float lg = sys->lim_gain, rel = sys->lim_rel, lmin = 1.f;
            uint32_t reduced = 0;
            for (int k = 0; k < n; k++) {
                float m = m0 + dm * (float)k;
                float l = sys->acc[2 * k] * m, r = sys->acc[2 * k + 1] * m;
                float pk = fabsf(l) > fabsf(r) ? fabsf(l) : fabsf(r);
                if (pk > peak) peak = pk;
                if (pk * lg > thr) lg = thr / pk;
                else if (lg < 1.f) { lg += (1.f - lg) * rel; if (lg > 0.99999f) lg = 1.f; }
                if (lg < 1.f) { reduced++; if (lg < lmin) lmin = lg; }
                o[2 * k] = l * lg; o[2 * k + 1] = r * lg;
            }
            sys->lim_gain = lg;
            if (reduced) {
                SA_ADD(&sys->st_lim_frames, reduced);
                uint32_t ob = SA_LD_RLX(&sys->st_lim_min_bits); float omin; memcpy(&omin, &ob, 4);
                if (lmin < omin) { uint32_t nb; memcpy(&nb, &lmin, 4); SA_ST_RLX(&sys->st_lim_min_bits, nb); }
            }
        } else if (m0 == m1) {
            for (int k = 0; k < 2 * n; k++) { float x = sys->acc[k] * m0; o[k] = x > 1.f ? 1.f : (x < -1.f ? -1.f : x); }
        } else for (int k = 0; k < n; k++) {
            float m = m0 + dm * (float)k;
            float l = sys->acc[2 * k] * m, r = sys->acc[2 * k + 1] * m;
            o[2 * k] = l > 1.f ? 1.f : (l < -1.f ? -1.f : l);
            o[2 * k + 1] = r > 1.f ? 1.f : (r < -1.f ? -1.f : r);
        }
        if (peak > 0.f) {
            uint32_t ob = SA_LD_RLX(&sys->st_pre_peak_bits); float op; memcpy(&op, &ob, 4);
            if (peak > op) { uint32_t nb; memcpy(&nb, &peak, 4); SA_ST_RLX(&sys->st_pre_peak_bits, nb); }
        }
        sys->master_cur = m1;
        done += n;
        if (sys->cfg.flags & SA_FLAG_SYNC_IO) { double ti = sys->timing ? sa_now() : 0; sa_io_service_streams(sys); if (sys->timing) sys->io_seconds += sa_now() - ti; }
    }
    sys->mix_clock64 += (uint64_t)frames;
    SA_ST_REL(&sys->mix_clock, (uint32_t)sys->mix_clock64);
    SA_ADD(&sys->st_blocks, 1u);
    if (sys->timing) sys->mix_seconds += sa_now() - t0;
    return frames;
}
