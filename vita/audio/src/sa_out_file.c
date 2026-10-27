/* Mac/test output backend: render to a WAV file. Never opens an audio device. */
#include "sa_internal.h"

static void w16(FILE *f, uint16_t v) { uint8_t b[2] = { (uint8_t)v, (uint8_t)(v >> 8) }; fwrite(b, 1, 2, f); }
static void w32(FILE *f, uint32_t v) { uint8_t b[4] = { (uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24) }; fwrite(b, 1, 4, f); }

int sa_wav_open(sa_wav *w, const char *path, int rate, int flt)
{
    memset(w, 0, sizeof *w);
    w->f = fopen(path, "wb");
    if (!w->f) return SA_ERR_FILE;
    w->rate = rate; w->flt = flt;
    int bps = flt ? 4 : 2;
    fwrite("RIFF", 1, 4, w->f); w32(w->f, 0); fwrite("WAVE", 1, 4, w->f);
    fwrite("fmt ", 1, 4, w->f); w32(w->f, 16); w16(w->f, flt ? 3 : 1); w16(w->f, 2); w32(w->f, (uint32_t)rate);
    w32(w->f, (uint32_t)(rate * 2 * bps)); w16(w->f, (uint16_t)(2 * bps)); w16(w->f, (uint16_t)(8 * bps));
    fwrite("data", 1, 4, w->f); w32(w->f, 0);
    return SA_OK;
}
void sa_wav_write(sa_wav *w, const float *x, int frames)
{
    if (w->flt) {
        for (int k = 0; k < 2 * frames; k++) { uint32_t u; memcpy(&u, &x[k], 4); w32(w->f, u); }
    } else {
        for (int k = 0; k < 2 * frames; k++) {
            float v = x[k] * 32767.f;
            int s = (int)(v >= 0 ? v + 0.5f : v - 0.5f);
            if (s > 32767) s = 32767; else if (s < -32768) s = -32768;
            w16(w->f, (uint16_t)(int16_t)s);
        }
    }
    w->frames += (uint32_t)frames;
}
void sa_wav_close(sa_wav *w)
{
    if (!w->f) return;
    uint32_t data = w->frames * 2u * (w->flt ? 4u : 2u);
    fseek(w->f, 4, SEEK_SET); w32(w->f, 36 + data);
    fseek(w->f, 40, SEEK_SET); w32(w->f, data);
    fclose(w->f);
    w->f = NULL;
}

/* renders `frames` frames in `block`-frame calls, calling sa_update before each block */
int sa_file_render(sa_system *sys, const char *path, int frames, int block, int float_wav)
{
    if (!sys || !path || frames < 0) return SA_ERR_INVALID_PARAM;
    if (block <= 0) block = 256;
    sa_wav w;
    if (sa_wav_open(&w, path, sys->cfg.output_rate, float_wav)) return SA_ERR_FILE;
    float *buf = (float *)malloc(sizeof(float) * 2 * (size_t)block);
    if (!buf) { sa_wav_close(&w); return SA_ERR_NO_MEMORY; }
    int done = 0;
    while (done < frames) {
        int n = frames - done; if (n > block) n = block;
        sa_update(sys);
        sa_mix(sys, buf, n);
        sa_wav_write(&w, buf, n);
        done += n;
    }
    free(buf);
    sa_wav_close(&w);
    return SA_OK;
}

/* Stage C: incremental output for a host that runs sa_update itself (the engine bridge renders each game frame's audio right
 * after its Studio update). Mixes with sa_mix only; writes 16-bit or float WAV; never opens a device. */
int sa_file_open(sa_system *sys, const char *path, int float_wav)
{
    if (!sys || !path) return SA_ERR_INVALID_PARAM;
    if (sys->fout) return SA_ERR_STATE;
    sa_wav *w = (sa_wav *)calloc(1, sizeof *w);
    if (!w) return SA_ERR_NO_MEMORY;
    if (sa_wav_open(w, path, sys->cfg.output_rate, float_wav)) { free(w); return SA_ERR_FILE; }
    sys->fout_cap = 4096;
    sys->fout_buf = (float *)malloc(sizeof(float) * 2 * (size_t)sys->fout_cap);
    if (!sys->fout_buf) { sa_wav_close(w); free(w); return SA_ERR_NO_MEMORY; }
    sys->fout = w;
    return SA_OK;
}
int sa_file_write(sa_system *sys, int frames)
{
    if (!sys || frames < 0) return SA_ERR_INVALID_PARAM;
    if (!sys->fout) return SA_ERR_STATE;
    int done = 0;
    while (done < frames) {
        int n = frames - done; if (n > sys->fout_cap) n = sys->fout_cap;
        sa_mix(sys, sys->fout_buf, n);
        sa_wav_write((sa_wav *)sys->fout, sys->fout_buf, n);
        done += n;
    }
    return done;
}
int sa_file_close(sa_system *sys)
{
    if (!sys) return SA_ERR_INVALID_PARAM;
    if (!sys->fout) return SA_ERR_STATE;
    sa_wav_close((sa_wav *)sys->fout);
    free(sys->fout); sys->fout = NULL;
    free(sys->fout_buf); sys->fout_buf = NULL; sys->fout_cap = 0;
    return SA_OK;
}
