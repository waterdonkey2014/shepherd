/* Test helpers: synthetic signals, WAV I/O, a reference MS-IMA ADPCM encoder, objective measurements.
 * Everything here is synthetic (generated) — no game data. Nothing is ever played. */
#ifndef SA_TESTUTIL_H
#define SA_TESTUTIL_H
#include "sa_internal.h"
#include <stdarg.h>

static int g_fail = 0, g_pass = 0;
#define CHECK(cond, ...) do { if (cond) { g_pass++; } else { g_fail++; printf("  FAIL %s:%d: ", __FILE__, __LINE__); printf(__VA_ARGS__); printf("\n"); } } while (0)

static void put16(FILE *f, int v) { fputc(v & 255, f); fputc((v >> 8) & 255, f); }
static void put32(FILE *f, uint32_t v) { put16(f, (int)(v & 0xFFFF)); put16(f, (int)(v >> 16)); }

static int write_wav16(const char *path, const int16_t *x, int frames, int ch, int rate)
{
    FILE *f = fopen(path, "wb"); if (!f) return -1;
    uint32_t data = (uint32_t)frames * (uint32_t)ch * 2u;
    fwrite("RIFF", 1, 4, f); put32(f, 36 + data); fwrite("WAVEfmt ", 1, 8, f); put32(f, 16); put16(f, 1); put16(f, ch);
    put32(f, (uint32_t)rate); put32(f, (uint32_t)(rate * ch * 2)); put16(f, ch * 2); put16(f, 16);
    fwrite("data", 1, 4, f); put32(f, data);
    fwrite(x, 2, (size_t)frames * (size_t)ch, f);
    fclose(f); return 0;
}
/* reads PCM16 or float32 WAV into float [-1,1]; returns frames, *ch */
static float *read_wav(const char *path, int *frames, int *ch, int *rate)
{
    FILE *f = fopen(path, "rb"); if (!f) return NULL;
    sa_fileinfo fi; char err[128];
    if (sa_probe(f, &fi, err, sizeof err) != SA_OK) { fclose(f); return NULL; }
    size_t n = (size_t)fi.frames * (size_t)fi.channels;
    float *o = (float *)malloc(sizeof(float) * (n + 1));
    fseek(f, fi.data_offset, SEEK_SET);
    for (size_t i = 0; i < n; i++) {
        if (fi.fmt == SA_FMT_PCM16LE) { uint8_t b[2]; if (fread(b, 1, 2, f) != 2) break; o[i] = (float)(int16_t)(b[0] | (b[1] << 8)) / 32768.f; }
        else if (fi.fmt == SA_FMT_F32LE) { float v; if (fread(&v, 4, 1, f) != 1) break; o[i] = v; }
        else { o[i] = 0; }
    }
    fclose(f);
    *frames = (int)fi.frames; *ch = fi.channels; if (rate) *rate = fi.rate;
    return o;
}

static int run(const char *fmt, ...)
{
    char cmd[2048]; va_list ap; va_start(ap, fmt); vsnprintf(cmd, sizeof cmd, fmt, ap); va_end(ap);
    int r = system(cmd);
    if (r) printf("  command failed (%d): %s\n", r, cmd);
    return r;
}

/* ---- reference MS-IMA (WAV 0x11) encoder: returns block-aligned data; recon receives the exact decode ---- */
static const int16_t T_step[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
    130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
    1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
    7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
static const int8_t T_idx[16] = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
static int ima_enc1(int s, int *pred, int *index)
{
    int step = T_step[*index], diff = s - *pred, n = 0;
    if (diff < 0) { n = 8; diff = -diff; }
    int d = step >> 3;
    if (diff >= step) { n |= 4; diff -= step; d += step; }
    step >>= 1; if (diff >= step) { n |= 2; diff -= step; d += step; }
    step >>= 1; if (diff >= step) { n |= 1; d += step; }
    int p = *pred + ((n & 8) ? -d : d);
    if (p > 32767) p = 32767; else if (p < -32768) p = -32768;
    *pred = p;
    int i = *index + T_idx[n]; *index = i < 0 ? 0 : (i > 88 ? 88 : i);
    return n;
}
static uint8_t *ima_ms_encode(const int16_t *x, int frames, int ch, int block, int *nblocks, int16_t *recon)
{
    int fpb = 1 + (block / ch - 4) * 2;
    int nb = (frames + fpb - 1) / fpb;
    uint8_t *o = (uint8_t *)calloc((size_t)nb, (size_t)block);
    int pred[2] = { 0, 0 }, index[2] = { 0, 0 };
    for (int b = 0; b < nb; b++) {
        uint8_t *blk = o + (size_t)b * block;
        int f0 = b * fpb;
        for (int c = 0; c < ch; c++) {
            int s = f0 < frames ? x[(size_t)f0 * ch + c] : 0;
            pred[c] = s;
            blk[4 * c] = (uint8_t)(s & 255); blk[4 * c + 1] = (uint8_t)((s >> 8) & 255); blk[4 * c + 2] = (uint8_t)index[c]; blk[4 * c + 3] = 0;
            if (f0 < frames) recon[(size_t)f0 * ch + c] = (int16_t)s;
        }
        for (int k = 1; k < fpb; k++) {
            int f = f0 + k;
            int sidx = k - 1, grp = sidx >> 3, within = sidx & 7;
            for (int c = 0; c < ch; c++) {
                int s = f < frames ? x[(size_t)f * ch + c] : 0;
                int n = ima_enc1(s, &pred[c], &index[c]);
                uint8_t *byte = &blk[4 * ch + grp * 4 * ch + 4 * c + (within >> 1)];
                if (within & 1) *byte |= (uint8_t)(n << 4); else *byte |= (uint8_t)n;
                if (f < frames) recon[(size_t)f * ch + c] = (int16_t)pred[c];
            }
        }
    }
    *nblocks = nb;
    return o;
}
static int write_wav_ima(const char *path, const uint8_t *blocks, int nblocks, int block, int ch, int rate, int frames)
{
    FILE *f = fopen(path, "wb"); if (!f) return -1;
    int fpb = 1 + (block / ch - 4) * 2;
    uint32_t data = (uint32_t)nblocks * (uint32_t)block;
    fwrite("RIFF", 1, 4, f); put32(f, 4 + 28 + 12 + 8 + data); fwrite("WAVEfmt ", 1, 8, f); put32(f, 20);
    put16(f, 0x11); put16(f, ch); put32(f, (uint32_t)rate); put32(f, (uint32_t)(rate * block / fpb)); put16(f, block); put16(f, 4);
    put16(f, 2); put16(f, fpb);
    fwrite("fact", 1, 4, f); put32(f, 4); put32(f, (uint32_t)frames);
    fwrite("data", 1, 4, f); put32(f, data);
    fwrite(blocks, 1, data, f);
    fclose(f); return 0;
}

/* ---- measurements ---- */
static double goertzel(const float *x, int n, int stride, double freq, double rate)
{
    double w = 2 * M_PI * freq / rate, c = 2 * cos(w), s1 = 0, s2 = 0;
    for (int i = 0; i < n; i++) { double s0 = x[(size_t)i * stride] + c * s1 - s2; s2 = s1; s1 = s0; }
    double p = s1 * s1 + s2 * s2 - c * s1 * s2;
    return sqrt(p > 0 ? p : 0) * 2.0 / n;   /* amplitude */
}
static double rms(const float *x, int n, int stride)
{
    double a = 0; for (int i = 0; i < n; i++) a += (double)x[(size_t)i * stride] * x[(size_t)i * stride];
    return n ? sqrt(a / n) : 0;
}
static double db(double v) { return v > 1e-12 ? 20 * log10(v) : -240; }
/* best lag of b against a in [lo, hi] (normalised cross-correlation) */
static int xcorr_lag(const float *a, int na, const float *b, int nb, int lo, int hi, double *best_r)
{
    int best = lo; double br = -2;
    for (int lag = lo; lag <= hi; lag++) {
        double s = 0, ea = 0, eb = 0;
        for (int i = 0; i < na; i++) { int j = i + lag; if (j < 0 || j >= nb) continue; s += (double)a[i] * b[j]; ea += (double)a[i] * a[i]; eb += (double)b[j] * b[j]; }
        double r = (ea > 0 && eb > 0) ? s / sqrt(ea * eb) : 0;
        if (r > br) { br = r; best = lag; }
    }
    if (best_r) *best_r = br;
    return best;
}
static double now_s(void) { return sa_now(); }
#endif
