/* Mac/test output backend (kit3 play mode, vita/notes/play-mode.md): the Mac stand-in for the Vita's sceAudioOut thread
 * (sa_out_vita.c). One thread mixes `grain` frames, appends them to a WAV and then waits until the wall clock reaches the time the
 * frames before them take to play at the output rate: the pace a blocking sceAudioOutOutput sets (it returns when the port can take
 * the next grain, i.e. when the previous one starts playing, so the mixer stays one grain ahead of the speaker). Nothing else paces
 * it: no game frame, no sa_update. So the WAV is what the Vita's speaker plays, sample for sample, whatever the game's frame rate.
 * It NEVER opens an audio device. POSIX build only (Mac + sanitizer tests); the Vita has the real thing.
 * Like the Vita backend: refuses SYNC_IO (the IO thread must service the streams), marks the system as having an output thread
 * (sa_cmd_push never drains inline), never calls managed code. Lateness (the mixer + file write taking longer than real time) is
 * measured: on a device that would be an output underrun. */
#include "sa_internal.h"
#ifdef __APPLE__
#include <pthread/qos.h>
#endif

typedef struct rt_out {
    sa_system *sys;
    sa_wav w;
    int grain, quit;
    float *buf;
    sa_thread th;
    double t0, t_end, max_late;
    uint64_t frames;
    uint32_t late_grains;   /* grains mixed later than their slot (the speaker would have run dry) */
} rt_out;

static rt_out g_rt;   /* one output per process, like the Vita backend */

static void rt_main(void *arg)
{
    rt_out *o = (rt_out *)arg;
    const double rate = (double)o->sys->cfg.output_rate;
#ifdef __APPLE__
    pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);   /* the stand-in for the Vita's priority-64 audio thread */
#endif
    o->t0 = sa_now();
    while (!SA_LD_ACQ(&o->quit)) {
        /* this grain's slot: it starts playing when the previous grains have played */
        double slot = o->t0 + (double)o->frames / rate;
        double now = sa_now();
        if (now > slot + 1e-3 && o->frames > 0) {
            double late = now - slot;
            if (late > o->max_late) o->max_late = late;
            o->late_grains++;
        }
        sa_mix(o->sys, o->buf, o->grain);
        sa_wav_write(&o->w, o->buf, o->grain);
        o->frames += (uint64_t)o->grain;
        /* blocking output: wait until the grain before this one has played (one grain queued ahead of the speaker) */
        double due = o->t0 + (double)(o->frames - (uint64_t)o->grain) / rate;
        now = sa_now();
        if (now < due) sa_sleep_us((unsigned)((due - now) * 1e6));
    }
    o->t_end = sa_now();
}

int sa_rt_out_start(sa_system *sys, const char *wav_path, int grain, int float_wav)
{
    if (!sys || !wav_path) return SA_ERR_INVALID_PARAM;
    if (g_rt.sys) return SA_ERR_STATE;
    if (sys->cfg.flags & SA_FLAG_SYNC_IO) return SA_ERR_STATE;   /* the IO thread services streams, as on the Vita */
    if (grain <= 0) grain = 512;
    grain = (grain + 63) & ~63;
    if (grain < 64) grain = 64;
    if (grain > 4096) grain = 4096;
    memset(&g_rt, 0, sizeof g_rt);
    if (sa_wav_open(&g_rt.w, wav_path, sys->cfg.output_rate, float_wav)) return SA_ERR_FILE;
    g_rt.buf = (float *)malloc(sizeof(float) * 2 * (size_t)grain);
    if (!g_rt.buf) { sa_wav_close(&g_rt.w); return SA_ERR_NO_MEMORY; }
    g_rt.sys = sys; g_rt.grain = grain;
    SA_ST_REL(&sys->out_thread, 1);
    if (sa_thread_start(&g_rt.th, "shepaudio_rt", rt_main, &g_rt, 0, 0, 256 * 1024)) {
        SA_ST_REL(&sys->out_thread, 0);
        sa_wav_close(&g_rt.w); free(g_rt.buf); memset(&g_rt, 0, sizeof g_rt);
        return SA_ERR_NO_MEMORY;
    }
    return SA_OK;
}

/* stops the thread and closes the WAV; out (optional, 6 doubles): frames written, seconds of audio, wall seconds the thread ran,
 * the ratio audio / wall, the worst lateness (s), late grains */
int sa_rt_out_stop(sa_system *sys, double *out)
{
    if (!g_rt.sys || g_rt.sys != sys) return SA_ERR_STATE;
    SA_ST_REL(&g_rt.quit, 1);
    sa_thread_join(&g_rt.th);
    sa_wav_close(&g_rt.w);
    free(g_rt.buf);
    SA_ST_REL(&sys->out_thread, 0);
    if (out) {
        double secs = (double)g_rt.frames / (double)sys->cfg.output_rate, wall = g_rt.t_end - g_rt.t0;
        out[0] = (double)g_rt.frames; out[1] = secs; out[2] = wall; out[3] = wall > 0 ? secs / wall : 0;
        out[4] = g_rt.max_late; out[5] = (double)g_rt.late_grains;
    }
    memset(&g_rt, 0, sizeof g_rt);
    return SA_OK;
}
