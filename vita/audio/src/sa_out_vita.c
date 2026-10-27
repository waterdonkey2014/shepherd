/* PS Vita output backend: one SceKernel thread mixing into two alternating int16 buffers and handing them to
 * sceAudioOutOutput (which blocks until the port can take the next grain, so the thread sleeps there).
 * The thread is pure native: it never attaches to Mono and never calls managed code or user callbacks. */
#include "sa_internal.h"
#include <psp2/audioout.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>
#include <malloc.h>

typedef struct vita_out {
    sa_system *sys;
    int port, grain, quit;
    int16_t *buf[2];
    sa_thread th;
    uint32_t outputs, errors;
} vita_out;

static vita_out g_out;   /* one output per process (the Vita has one audio-out owner here) */

/* sceAudioOutOutput blocks until the port takes the grain: that is what paces the mixer -- the audio clock (sa_get_mix_clock), which
 * the engine's FMOD stubs and timeline callbacks run on, is the hardware's. When the port REFUSES a grain (kit #3 review), nothing would
 * pace it: the old loop slept 1 ms and mixed on, ~10x real time (instances ending early, the engine's ShepAudio.ClockSeconds racing).
 * Now a refused grain waits for its own deadline (one grain after the previous one, as the hardware would have taken it), the grain's
 * sound is lost but the clock keeps real time, and the next grain tries the port again. The refusals and the first error code are in
 * the stats (sa_stats out_errors / out_first_error: the engine's heartbeat and [audio] end lines). */
static void out_main(void *arg)
{
    vita_out *o = (vita_out *)arg;
    int cur = 0;
    const SceUInt64 grain_us = (SceUInt64)o->grain * 1000000ull / 48000u;
    SceUInt64 due = 0;
    while (!SA_LD_ACQ(&o->quit)) {
        sa_mix_s16(o->sys, o->buf[cur], o->grain);
        int r = sceAudioOutOutput(o->port, o->buf[cur]);
        if (r < 0) {
            o->errors++;
            if (o->errors == 1) SA_ST_RLX(&o->sys->out_first_error, (int32_t)r);
            SA_ST_RLX(&o->sys->out_errors, o->errors);
            SceUInt64 now = sceKernelGetProcessTimeWide();
            if (!due || now > due + 100000) due = now;   /* (more than 100 ms late: a new timeline, as after an underrun) */
            due += grain_us;
            if (due > now) sa_sleep_us((unsigned)(due - now));
            continue;
        }
        due = 0;
        o->outputs++;
        cur ^= 1;
    }
    sceAudioOutOutput(o->port, NULL);   /* wait for the last grain to drain */
}

int sa_vita_out_start(sa_system *sys, const sa_vita_out_config *cfg)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    if (g_out.sys) return SA_ERR_STATE;
    if (sys->cfg.flags & SA_FLAG_SYNC_IO) return SA_ERR_STATE;   /* SYNC_IO services streams on the API thread */
    if (sys->cfg.output_rate != 48000) return SA_ERR_UNSUPPORTED;
    int type = cfg ? cfg->port_type : SCE_AUDIO_OUT_PORT_TYPE_BGM;
    int grain = cfg && cfg->grain > 0 ? cfg->grain : 512;
    grain = (grain + 63) & ~63;
    if (grain < SCE_AUDIO_MIN_LEN) grain = SCE_AUDIO_MIN_LEN;
    if (grain > 4096) grain = 4096;
    int prio = cfg && cfg->priority > 0 ? cfg->priority : 64;      /* SCE_KERNEL_HIGHEST_PRIORITY_USER */
    int aff = cfg ? cfg->affinity : 0;
    int stack = cfg && cfg->stack_bytes > 0 ? cfg->stack_bytes : 64 * 1024;
    memset(&g_out, 0, sizeof g_out);
    g_out.port = sceAudioOutOpenPort((SceAudioOutPortType)type, grain, 48000, SCE_AUDIO_OUT_MODE_STEREO);
    if (g_out.port < 0) return SA_ERR_UNSUPPORTED;
    int vol[2] = { SCE_AUDIO_OUT_MAX_VOL, SCE_AUDIO_OUT_MAX_VOL };
    sceAudioOutSetVolume(g_out.port, (SceAudioOutChannelFlag)(SCE_AUDIO_VOLUME_FLAG_L_CH | SCE_AUDIO_VOLUME_FLAG_R_CH), vol);
    for (int k = 0; k < 2; k++) {
        g_out.buf[k] = (int16_t *)memalign(64, (size_t)grain * 4u);
        if (!g_out.buf[k]) { sceAudioOutReleasePort(g_out.port); free(g_out.buf[0]); return SA_ERR_NO_MEMORY; }
        memset(g_out.buf[k], 0, (size_t)grain * 4u);
    }
    g_out.sys = sys; g_out.grain = grain;
    SA_ST_REL(&sys->out_thread, 1);
    if (sa_thread_start(&g_out.th, "shepaudio_out", out_main, &g_out, prio, aff, stack)) {
        SA_ST_REL(&sys->out_thread, 0);
        sceAudioOutReleasePort(g_out.port); free(g_out.buf[0]); free(g_out.buf[1]); g_out.sys = NULL;
        return SA_ERR_NO_MEMORY;
    }
    return SA_OK;
}

int sa_vita_out_stop(sa_system *sys)
{
    if (!g_out.sys || g_out.sys != sys) return SA_ERR_STATE;
    SA_ST_REL(&g_out.quit, 1);
    sa_thread_join(&g_out.th);           /* blocks up to ~2 grains */
    sceAudioOutReleasePort(g_out.port);
    free(g_out.buf[0]); free(g_out.buf[1]);
    SA_ST_REL(&sys->out_thread, 0);
    memset(&g_out, 0, sizeof g_out);
    return SA_OK;
}
