/* shepaudio unit tests, stress test and CPU bench (Mac, silent: output goes to memory/WAV files only).
 *   sa_test [all|unit|stress|bench] [--dir DIR] [--quick]
 * Fixtures are synthetic (sines, noise, counters) generated here; afconvert (encode/decode only) makes the IMA4/AAC
 * versions and the independent reference decodes. */
#include "sa_testutil.h"
#include <pthread.h>

static char D[1024];                       /* fixture dir */
static char P[1200];
static const char *fx(const char *name) { snprintf(P, sizeof P, "%s/%s", D, name); return P; }
static int g_quick = 0;
#define RATE 48000

/* ---------------- fixtures ---------------- */
static int16_t *gen_sine(int frames, int ch, int rate, double f0, double f1, double amp)
{
    int16_t *x = (int16_t *)malloc(sizeof(int16_t) * (size_t)frames * ch);
    for (int i = 0; i < frames; i++) {
        x[(size_t)i * ch] = (int16_t)lrint(amp * 32767 * sin(2 * M_PI * f0 * i / rate));
        if (ch == 2) x[(size_t)i * 2 + 1] = (int16_t)lrint(amp * 32767 * sin(2 * M_PI * f1 * i / rate));
    }
    return x;
}
static uint32_t lcg = 12345;
static double frand(void) { lcg = lcg * 1664525u + 1013904223u; return ((lcg >> 8) & 0xFFFFFF) / 16777216.0 * 2 - 1; }
static int16_t *gen_music(int frames, int ch, int rate)
{
    /* chord + slow AM + a little noise: broadband, AAC-friendly, non-periodic over the loop */
    int16_t *x = (int16_t *)malloc(sizeof(int16_t) * (size_t)frames * ch);
    const double fr[4] = { 220, 277.18, 329.63, 440 };
    for (int i = 0; i < frames; i++) {
        double t = (double)i / rate, a = 0.18 * (1 + 0.5 * sin(2 * M_PI * 0.37 * t));
        for (int c = 0; c < ch; c++) {
            double s = 0;
            for (int k = 0; k < 4; k++) s += sin(2 * M_PI * fr[k] * (1 + 0.002 * c) * t + k);
            s = a * s + 0.02 * frand();
            x[(size_t)i * ch + c] = (int16_t)lrint(s * 32767 * 0.9);
        }
    }
    return x;
}
static int16_t *gen_counter(int frames)
{
    int16_t *x = (int16_t *)malloc(sizeof(int16_t) * (size_t)frames);
    for (int i = 0; i < frames; i++) x[i] = (int16_t)((i * 37) % 30001 - 15000);
    return x;
}

static int make_fixtures(void)
{
    char cmd[64]; (void)cmd;
    run("mkdir -p '%s'", D);
    int16_t *x;
    x = gen_sine(RATE / 2, 1, RATE, 440, 0, 0.5); write_wav16(fx("sine440_m.wav"), x, RATE / 2, 1, RATE); free(x);
    x = gen_sine(RATE / 2, 2, RATE, 440, 660, 0.5); write_wav16(fx("st_440_660.wav"), x, RATE / 2, 2, RATE); free(x);
    x = gen_counter(1000); write_wav16(fx("counter.wav"), x, 1000, 1, RATE); free(x);
    x = gen_sine(RATE, 1, 24000, 1000, 0, 0.5); write_wav16(fx("sine1k_24k.wav"), x, RATE, 1, 24000); free(x);
    x = gen_sine(44100, 2, 44100, 1000, 1000, 0.5); write_wav16(fx("sine1k_44k_st.wav"), x, 44100, 2, 44100); free(x);
    x = gen_sine(RATE, 1, RATE, 440, 0, 0.4); write_wav16(fx("p440.wav"), x, RATE, 1, RATE); free(x);
    x = gen_sine(RATE, 1, RATE, 880, 0, 0.4); write_wav16(fx("p880.wav"), x, RATE, 1, RATE); free(x);
    x = gen_sine(2 * RATE, 1, RATE, 1000, 0, 0.5); write_wav16(fx("sine1k_2s.wav"), x, 2 * RATE, 1, RATE); free(x);
    x = gen_sine(RATE / 10, 1, RATE, 500, 0, 0.5); write_wav16(fx("short100.wav"), x, RATE / 10, 1, RATE); free(x);
    for (int k = 0; k < 3; k++) {
        char n[64]; snprintf(n, sizeof n, "var%d.wav", k);
        x = gen_sine(RATE / 5, 1, RATE, 220 + 110 * k, 0, 0.5); write_wav16(fx(n), x, RATE / 5, 1, RATE); free(x);
    }
    x = gen_sine(RATE, 1, RATE, 300, 0, 0.5); write_wav16(fx("g0.wav"), x, RATE, 1, RATE); free(x);
    x = gen_sine(RATE, 1, RATE, 600, 0, 0.5); write_wav16(fx("g1.wav"), x, RATE, 1, RATE); free(x);
    x = gen_sine(RATE, 1, RATE, 700, 0, 0.5); write_wav16(fx("bus_sine.wav"), x, RATE, 1, RATE); free(x);
    x = gen_music(15 * RATE, 2, RATE); write_wav16(fx("big_pcm.wav"), x, 15 * RATE, 2, RATE); free(x);
    /* IMA: MS (own reference encoder) + Apple ima4 (afconvert) */
    lcg = 777;
    int nf = RATE;
    int16_t *src = (int16_t *)malloc(sizeof(int16_t) * (size_t)nf * 2);
    for (int i = 0; i < nf; i++) for (int c = 0; c < 2; c++)
        src[i * 2 + c] = (int16_t)lrint(32767 * (0.4 * sin(2 * M_PI * (300 + 200 * c) * i / RATE) + 0.1 * frand()));
    int16_t *mono = (int16_t *)malloc(sizeof(int16_t) * (size_t)nf);
    for (int i = 0; i < nf; i++) mono[i] = src[i * 2];
    write_wav16(fx("ima_src_st.wav"), src, nf, 2, RATE);
    write_wav16(fx("ima_src_m.wav"), mono, nf, 1, RATE);
    int16_t *recon = (int16_t *)calloc((size_t)nf * 2, sizeof(int16_t));
    int nb;
    uint8_t *blk = ima_ms_encode(mono, nf, 1, 1024, &nb, recon);
    write_wav_ima(fx("ima_ms_m.wav"), blk, nb, 1024, 1, RATE, nf); free(blk);
    write_wav16(fx("ima_ms_m.recon.wav"), recon, nf, 1, RATE);
    blk = ima_ms_encode(src, nf, 2, 2048, &nb, recon);
    write_wav_ima(fx("ima_ms_st.wav"), blk, nb, 2048, 2, RATE, nf); free(blk);
    write_wav16(fx("ima_ms_st.recon.wav"), recon, nf, 2, RATE);
    free(recon); free(src); free(mono);
    run("/usr/bin/afconvert -f caff -d ima4 '%s/ima_src_st.wav' '%s/ima4_st.caf'", D, D);
    run("/usr/bin/afconvert -f AIFC -d ima4 '%s/ima_src_m.wav' '%s/ima4_m.aifc'", D, D);
    run("/usr/bin/afconvert -f WAVE -d LEI16 '%s/ima4_st.caf' '%s/ima4_st.ref.wav'", D, D);
    run("/usr/bin/afconvert -f WAVE -d LEI16 '%s/ima4_m.aifc' '%s/ima4_m.ref.wav'", D, D);
    run("/usr/bin/afconvert -f WAVE -d LEI16 '%s/ima_ms_st.wav' '%s/ima_ms_st.afref.wav'", D, D);
    /* AAC */
    x = gen_music(10 * RATE, 2, RATE); write_wav16(fx("music_src.wav"), x, 10 * RATE, 2, RATE); free(x);
    x = gen_music(3 * RATE, 1, RATE); write_wav16(fx("aac1_src.wav"), x, 3 * RATE, 1, RATE); free(x);
    run("/usr/bin/afconvert -f adts -d aac -b 160000 '%s/music_src.wav' '%s/music.aac'", D, D);
    run("/usr/bin/afconvert -f adts -d aac -b 96000 '%s/aac1_src.wav' '%s/aac1.aac'", D, D);
    run("/usr/bin/afconvert -f WAVE -d LEI16 '%s/music.aac' '%s/music.afref.wav'", D, D);
    /* manifest */
    FILE *m = fopen(fx("manifest.json"), "w");
    if (!m) return -1;
    fprintf(m, "{\n \"version\":1, \"sampleRate\":48000,\n"
      " \"buses\":[{\"path\":\"bus:/SFX\",\"inferredFrom\":\"test\",\"defaultVolume\":1},{\"path\":\"bus:/SFX/Hits\",\"inferredFrom\":\"test\",\"defaultVolume\":1},"
      "{\"path\":\"bus:/Music\",\"inferredFrom\":\"test\",\"defaultVolume\":0.8}],\n \"events\":[\n");
#define TK(file, codec, ch, rate, frames, loop) "{\"file\":\"" file "\",\"codec\":\"" codec "\",\"channels\":" #ch ",\"rate\":" #rate ",\"frames\":" #frames ",\"loop\":" loop ",\"paramValues\":null,\"weight\":1}"
#define EV(path, oneshot, len, is3d, bus, params, takes) "{\"path\":\"" path "\",\"guid\":null,\"oneshot\":" oneshot ",\"lengthMs\":" len ",\"is3D\":" is3d ",\"minDistance\":1,\"maxDistance\":20,\"bus\":\"" bus "\",\"params\":" params ",\"takes\":[" takes "]}"
    fprintf(m, "  " EV("event:/test/sine440_mono", "true", "500", "false", "bus:/SFX", "[]", TK("sine440_m.wav", "pcm16", 1, 48000, 24000, "null")) ",\n");
    fprintf(m, "  {\"path\":\"event:/test/stereo_pcm\",\"guid\":\"{01234567-89ab-cdef-0123-456789abcdef}\",\"oneshot\":true,\"lengthMs\":500,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],\"takes\":[" TK("st_440_660.wav", "pcm16", 2, 48000, 24000, "null") "]},\n");
    fprintf(m, "  " EV("event:/test/counter_loop", "false", "null", "false", "bus:/SFX", "[]", TK("counter.wav", "pcm16", 1, 48000, 1000, "{\"start\":200,\"end\":1000}")) ",\n");
    fprintf(m, "  " EV("event:/test/adpcm_ms", "true", "1000", "false", "bus:/SFX", "[]", TK("ima_ms_st.wav", "ima-adpcm", 2, 48000, 48000, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/adpcm_apple", "true", "1000", "false", "bus:/SFX", "[]", TK("ima4_st.caf", "ima-adpcm", 2, 48000, 48000, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/adpcm_loop", "false", "null", "false", "bus:/SFX", "[]", TK("ima_ms_m.wav", "ima-adpcm", 1, 48000, 48000, "{\"start\":5000,\"end\":30000}")) ",\n");
    fprintf(m, "  " EV("event:/test/ima4_loop", "false", "null", "false", "bus:/SFX", "[]", TK("ima4_st.caf", "ima-adpcm", 2, 48000, 48000, "{\"start\":3000,\"end\":40000}")) ",\n");
    fprintf(m, "  " EV("event:/test/rate44k_loop", "false", "null", "false", "bus:/SFX", "[]", TK("sine1k_44k_st.wav", "pcm16", 2, 44100, 44100, "{\"start\":0,\"end\":44100}")) ",\n");
    fprintf(m, "  " EV("event:/test/rate24k", "true", "2000", "false", "bus:/SFX", "[]", TK("sine1k_24k.wav", "pcm16", 1, 24000, 48000, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/rate44k", "true", "1000", "false", "bus:/SFX", "[]", TK("sine1k_44k_st.wav", "pcm16", 2, 44100, 44100, "null")) ",\n");
    fprintf(m, "  {\"path\":\"event:/test/param\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"x\",\"min\":0,\"max\":1,\"default\":0,\"global\":false}],\"takes\":["
               "{\"file\":\"p440.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"x\":0},\"weight\":1},"
               "{\"file\":\"p880.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"x\":1},\"weight\":1}]},\n");
    fprintf(m, "  " EV("event:/test/3d", "true", "2000", "true", "bus:/SFX", "[]", TK("sine1k_2s.wav", "pcm16", 1, 48000, 96000, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/aac_music", "false", "null", "false", "bus:/Music", "[]", TK("music.aac", "aac-adts", 2, 48000, 480000, "{\"start\":96000,\"end\":384000}")) ",\n");
    fprintf(m, "  " EV("event:/test/aac_oneshot", "true", "3000", "false", "bus:/Music", "[]", TK("aac1.aac", "aac-adts", 1, 48000, 144000, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/short100", "true", "100", "false", "bus:/SFX/Hits", "[]", TK("short100.wav", "pcm16", 1, 48000, 4800, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/hold_noloop", "false", "100", "false", "bus:/SFX", "[]", TK("short100.wav", "pcm16", 1, 48000, 4800, "null")) ",\n");
    fprintf(m, "  " EV("event:/test/takeless", "true", "250", "false", "bus:/SFX", "[]", "") ",\n");
    fprintf(m, "  {\"path\":\"event:/test/variations\",\"guid\":null,\"oneshot\":true,\"lengthMs\":200,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],\"takes\":["
               "{\"file\":\"var0.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":9600,\"loop\":null,\"paramValues\":null,\"weight\":1},"
               "{\"file\":\"var1.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":9600,\"loop\":null,\"paramValues\":null,\"weight\":2},"
               "{\"file\":\"var2.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":9600,\"loop\":null,\"paramValues\":null,\"weight\":1}]},\n");
    fprintf(m, "  " EV("event:/test/bus_sfx", "false", "null", "false", "bus:/SFX/Hits", "[]", TK("bus_sine.wav", "pcm16", 1, 48000, 48000, "{\"start\":0,\"end\":48000}")) ",\n");
    fprintf(m, "  {\"path\":\"event:/test/global\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"Global1\",\"min\":0,\"max\":1,\"default\":0,\"global\":true}],\"takes\":["
               "{\"file\":\"g0.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"Global1\":0},\"weight\":1},"
               "{\"file\":\"g1.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"Global1\":1},\"weight\":1}]},\n");
    fprintf(m, "  {\"path\":\"event:/test/sections\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"night\",\"min\":0,\"max\":1,\"default\":0,\"global\":false,\"discrete\":false,\"labels\":null},"
               "{\"name\":\"room_id\",\"min\":0,\"max\":20,\"default\":0,\"global\":false,\"discrete\":true,\"labels\":[\"a\",\"b\",\"c\"]}],\"takes\":["
               "{\"file\":\"g0.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":null,\"weight\":1},"
               "{\"file\":\"g1.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"night\":1},\"weight\":1},"
               "{\"file\":\"p440.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"room_id\":1},\"weight\":1},"
               "{\"file\":\"p880.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"room_id\":2},\"weight\":1}]},\n");
    fprintf(m, "  " EV("event:/test/big_pcm", "true", "15000", "false", "bus:/Music", "[]", TK("big_pcm.wav", "pcm16", 2, 48000, 720000, "null")) ",\n");
    /* measured 3D model (bake rolloff + pan) */
    fprintf(m, "  {\"path\":\"event:/test/3d_measured\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":true,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],"
               "\"takes\":[" TK("bus_sine.wav", "pcm16", 1, 48000, 48000, "{\"start\":0,\"end\":48000}") "],"
               "\"rolloff\":[{\"d\":1,\"gain\":1},{\"d\":5,\"gain\":0.5},{\"d\":20,\"gain\":0.1}],"
               "\"pan\":{\"distance\":4,\"front\":0.5,\"az\":[{\"az\":45,\"far\":0.2},{\"az\":90,\"far\":0.01}],\"near\":[{\"d\":1,\"blend\":0.5}]}},\n");
    /* FMOD's STOPPED frame (bake stopFrame): before the take's end (tail plays on) and after it (timeline runs on) */
    fprintf(m, "  {\"path\":\"event:/test/stop_early\",\"guid\":null,\"oneshot\":true,\"lengthMs\":200,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],"
               "\"takes\":[{\"file\":\"var0.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":9600,\"loop\":null,\"paramValues\":null,\"weight\":1,\"stopFrame\":4800}]},\n");
    fprintf(m, "  {\"path\":\"event:/test/stop_late\",\"guid\":null,\"oneshot\":true,\"lengthMs\":300,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],"
               "\"takes\":[{\"file\":\"short100.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":4800,\"loop\":null,\"paramValues\":null,\"weight\":1,\"stopFrame\":14400}]},\n");
    /* measured release: STOPPED 200 ms after stop(), fade 1 -> 0.5 (100 ms) -> 0.1 (300 ms) -> 0 (500 ms) */
    fprintf(m, "  {\"path\":\"event:/test/release\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\",\"params\":[],"
               "\"takes\":[" TK("bus_sine.wav", "pcm16", 1, 48000, 48000, "{\"start\":0,\"end\":48000}") "],"
               "\"releaseMs\":500,\"releaseStopMs\":200,\"releaseCurve\":[{\"ms\":0,\"gain\":1},{\"ms\":100,\"gain\":0.5},{\"ms\":300,\"gain\":0.1},{\"ms\":500,\"gain\":0}]},\n");
    /* parameter-gated: FMOD silent at the defaults; flagged by the bake, and inferred (no take at the defaults) */
    fprintf(m, "  {\"path\":\"event:/test/silent_default\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"power\",\"min\":0,\"max\":1,\"default\":0,\"global\":false}],\"silentAtDefault\":true,\"takes\":["
               "{\"file\":\"p440.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"power\":1},\"weight\":1}]},\n");
    fprintf(m, "  {\"path\":\"event:/test/silent_inferred\",\"guid\":null,\"oneshot\":false,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"shore_id\",\"min\":0,\"max\":4,\"default\":0,\"global\":false,\"discrete\":true}],\"takes\":["
               "{\"file\":\"p440.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"shore_id\":1},\"weight\":1},"
               "{\"file\":\"p880.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":48000,\"loop\":{\"start\":0,\"end\":48000},\"paramValues\":{\"shore_id\":2},\"weight\":1}]},\n");
    fprintf(m, "  {\"path\":\"event:/test/silent_oneshot\",\"guid\":null,\"oneshot\":true,\"lengthMs\":null,\"is3D\":false,\"minDistance\":1,\"maxDistance\":20,\"bus\":\"bus:/SFX\","
               "\"params\":[{\"name\":\"x\",\"min\":0,\"max\":1,\"default\":0,\"global\":false}],\"silentAtDefault\":true,\"silentStopFrame\":7200,\"takes\":["
               "{\"file\":\"short100.wav\",\"codec\":\"pcm16\",\"channels\":1,\"rate\":48000,\"frames\":4800,\"loop\":null,\"paramValues\":{\"x\":1},\"weight\":1}]},\n");
    fprintf(m, "  " EV("event:/test/missing_file", "true", "300", "false", "bus:/SFX", "[]", TK("does_not_exist.wav", "pcm16", 1, 48000, 14400, "null")) "\n");
    fprintf(m, " ],\n \"snapshots\":[{\"path\":\"snapshot:/test/duck\",\"guid\":\"{11111111-2222-3333-4444-555555555555}\",\"busVolumes\":{\"bus:/SFX\":0.25}},\n"
               "  {\"path\":\"snapshot:/test/sfx_volume\",\"guid\":null,\"busVolumes\":{},\"params\":[{\"name\":\"sfx_bus_volume\",\"min\":0,\"max\":1,\"default\":1,\"global\":false}],"
               "\"curve\":[{\"paramValues\":{\"sfx_bus_volume\":0},\"busVolumes\":{\"bus:/SFX\":0}},{\"paramValues\":{\"sfx_bus_volume\":0.25},\"busVolumes\":{\"bus:/SFX\":0.25}},"
               "{\"paramValues\":{\"sfx_bus_volume\":0.5},\"busVolumes\":{\"bus:/SFX\":0.5}},{\"paramValues\":{\"sfx_bus_volume\":0.75},\"busVolumes\":{\"bus:/SFX\":0.75}}]}]\n}\n");
    fclose(m);
    return 0;
}

/* ---------------- render harness ---------------- */
typedef struct rec { float *buf; int frames, cap; } rec;
static void rec_mix(sa_system *s, rec *r, int frames, int block, int update_every)
{
    if (r->frames + frames > r->cap) { r->cap = (r->frames + frames) * 2; r->buf = (float *)realloc(r->buf, sizeof(float) * 2 * (size_t)r->cap); }
    int done = 0, k = 0;
    while (done < frames) {
        int n = frames - done; if (n > block) n = block;
        if (update_every > 0 && (k++ % update_every) == 0) sa_update(s);
        sa_mix(s, r->buf + 2 * (size_t)(r->frames + done), n);
        done += n;
    }
    r->frames += frames;
}
static int g_adpcm_decode = -1;   /* unit tests exercise the per-voice ADPCM path unless a test opts in */
static sa_system *open_sys(int voices, int flags)
{
    sa_config c; sa_config_defaults(&c);
    c.max_voices = voices; c.flags = flags; c.seed = 1234; c.adpcm_decode_max_frames = g_adpcm_decode;
    sa_system *s = sa_init_ex(fx("manifest.json"), &c);
    if (!s) printf("  init failed: %s\n", sa_last_error());
    return s;
}

/* ---------------- tests ---------------- */
static void t_json(void)
{
    printf("[json/guid]\n");
    char txt[] = "{\"a\":[1,2.5e3,-3],\"s\":\"x\\u00e9\\n\\\"\",\"o\":{\"t\":true,\"n\":null}}";
    sa_jarena ar = { 0 }; char err[128];
    sa_json *j = sa_json_parse(txt, strlen(txt), &ar, err, sizeof err);
    CHECK(j != NULL, "parse: %s", err);
    if (j) {
        const sa_json *a = sa_json_get(j, "a");
        CHECK(a && a->count == 3 && a->child->next->num == 2500, "array");
        CHECK(!strcmp(sa_json_str(sa_json_get(j, "s"), ""), "x\xc3\xa9\n\""), "string escapes");
        CHECK(sa_json_bool(sa_json_get(sa_json_get(j, "o"), "t"), 0) == 1, "bool");
    }
    sa_json_free(&ar);
    char bad[] = "{\"a\":[1,2,}";
    CHECK(sa_json_parse(bad, strlen(bad), &ar, err, sizeof err) == NULL, "rejects malformed");
    sa_json_free(&ar);
    uint8_t g[16];
    CHECK(sa_parse_guid("{01234567-89ab-cdef-0123-456789abcdef}", g) == 0 && g[0] == 0x67 && g[3] == 0x01 && g[4] == 0xab && g[6] == 0xef && g[8] == 0x01 && g[15] == 0xef, "guid layout");
}

static void t_manifest(void)
{
    printf("[manifest]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    CHECK(s != NULL, "init");
    if (!s) return;
    CHECK(sa_event_count(s) == 32, "32 events incl. 2 snapshots (got %d)", sa_event_count(s));
    {   /* optional bake fields: stopFrame, release curve, silent group (flagged and inferred), measured 3D model */
        const sa_event *se = &s->events[sa_event_find(s, "event:/test/stop_early")], *re = &s->events[sa_event_find(s, "event:/test/release")];
        const sa_event *sd = &s->events[sa_event_find(s, "event:/test/silent_default")], *si = &s->events[sa_event_find(s, "event:/test/silent_inferred")];
        const sa_event *sp = &s->events[sa_event_find(s, "event:/test/3d_measured")], *pr = &s->events[sa_event_find(s, "event:/test/param")];
        CHECK(s->takes[s->take_order[se->first_take]].stop_frame == 4800, "take stopFrame parsed");
        CHECK(re->nrelpt == 4 && re->release_stop_frames == 9600 && s->relpts[2 * re->first_relpt + 2 * 3] == 24000.f, "release curve parsed (4 points, stop 200 ms)");
        CHECK(sd->silent_group >= 0 && si->silent_group >= 0 && pr->silent_group < 0 && sd->ngroups == 2 && si->ngroups == 3, "silent group: flagged, inferred, not added when a group is at the defaults");
        CHECK(sp->spatial >= 0 && s->spatials[sp->spatial].nroll == 3 && s->spatials[sp->spatial].naz == 2 && s->spatials[sp->spatial].nnear == 1, "3D model parsed");
    }
    int e = sa_event_find(s, "EVENT:/Test/Stereo_PCM");
    CHECK(e >= 0, "case-insensitive path lookup");
    CHECK(sa_event_find(s, "{01234567-89ab-cdef-0123-456789abcdef}") == e, "guid string lookup");
    uint8_t g[16]; sa_parse_guid("{01234567-89ab-cdef-0123-456789abcdef}", g);
    CHECK(sa_event_find_guid(s, g) == e, "guid bytes lookup");
    CHECK(sa_bus_find(s, "bus:/SFX/Hits") >= 0 && sa_bus_find(s, "bus:/") == 0, "bus hierarchy");
    CHECK(s->buses[sa_bus_find(s, "bus:/SFX/Hits")].parent == sa_bus_find(s, "bus:/SFX"), "bus parent");
    sa_event_info inf; sa_event_get_info(s, sa_event_find(s, "snapshot:/test/duck"), &inf);
    CHECK(inf.is_snapshot == 1, "snapshot as event");
    int pe = sa_event_find(s, "event:/test/param");
    sa_param_info pi; sa_event_get_param(s, pe, 0, &pi);
    CHECK(pi.maximum == 1 && !strcmp(pi.name, "x"), "param info");
    CHECK(s->events[pe].ngroups == 2, "param groups");
    CHECK(sa_event_create(s, "event:/nope") == 0, "unknown event -> 0");
    sa_shutdown(s);
}

/* decode a cached take through the cache loader */
static int16_t *load_take(sa_system *s, const char *ev, int *frames, int *ch)
{
    int e = sa_event_find(s, ev);
    int t = s->take_order[s->events[e].first_take];
    if (sa_cache_load_now(s, t) != SA_OK) return NULL;
    sa_entry *en = &s->entries[t];
    *frames = (int)en->frames; *ch = en->channels;
    int16_t *o = (int16_t *)malloc(sizeof(int16_t) * (size_t)en->frames * en->channels + 8);
    if (en->fmt == SA_FMT_PCM16LE) memcpy(o, en->data, (size_t)en->frames * en->channels * 2);
    else {
        sa_adpcm_cur c; memset(&c, 0, sizeof c);
        int got = sa_adpcm_decode(en->fmt, (const uint8_t *)en->data, en->nblocks, en->block_bytes, en->fpb, en->channels, &c, o, (int)en->frames);
        *frames = got;
    }
    return o;
}

static void t_decoders(void)
{
    printf("[decoders: pcm16 + IMA ADPCM vs references]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    int fr, ch, rfr, rch, rr;
    int16_t *x = load_take(s, "event:/test/stereo_pcm", &fr, &ch);
    float *ref = read_wav(fx("st_440_660.wav"), &rfr, &rch, &rr);
    int bad = 0;
    for (int i = 0; x && ref && i < fr * ch; i++) if (x[i] != (int16_t)lrintf(ref[i] * 32768.f)) bad++;
    CHECK(x && fr == 24000 && ch == 2 && bad == 0, "pcm16 exact (%d mismatches)", bad);
    free(x); free(ref);
    /* MS IMA (WAV 0x11): vs our encoder's reconstruction AND vs Apple's decoder (afconvert) */
    x = load_take(s, "event:/test/adpcm_ms", &fr, &ch);
    ref = read_wav(fx("ima_ms_st.recon.wav"), &rfr, &rch, &rr);
    float *af = read_wav(fx("ima_ms_st.afref.wav"), &rfr, &rch, &rr);
    int bad_enc = 0, bad_af = 0, maxd = 0;
    for (int i = 0; x && ref && af && i < fr * ch && i < rfr * rch; i++) {
        int a = (int16_t)lrintf(ref[i] * 32768.f), b = (int16_t)lrintf(af[i] * 32768.f);
        if (x[i] != a) bad_enc++;
        if (x[i] != b) { bad_af++; if (abs(x[i] - b) > maxd) maxd = abs(x[i] - b); }
    }
    CHECK(x && fr == 48000 && bad_enc == 0, "MS-IMA stereo == encoder reconstruction (%d mismatches)", bad_enc);
    CHECK(x && bad_af == 0, "MS-IMA stereo == Apple decoder (%d mismatches, max |d| %d)", bad_af, maxd);
    printf("  MS-IMA: %d frames, mismatches vs encoder %d, vs afconvert %d\n", fr, bad_enc, bad_af);
    /* SNR of the ADPCM vs the source */
    float *src = read_wav(fx("ima_src_st.wav"), &rfr, &rch, &rr);
    double se = 0, ss = 0;
    for (int i = 0; x && src && i < fr * ch; i++) { double d = x[i] / 32768.0 - src[i]; se += d * d; ss += (double)src[i] * src[i]; }
    printf("  MS-IMA SNR vs source: %.1f dB\n", 10 * log10(ss / (se > 0 ? se : 1e-30)));
    free(x); free(ref); free(af); free(src);
    /* Apple ima4 (CAF stereo + AIFC mono) vs afconvert's decode */
    x = load_take(s, "event:/test/adpcm_apple", &fr, &ch);
    af = read_wav(fx("ima4_st.ref.wav"), &rfr, &rch, &rr);
    bad_af = 0; maxd = 0;
    int n = x && af ? (fr * ch < rfr * rch ? fr * ch : rfr * rch) : 0;
    for (int i = 0; i < n; i++) { int b = (int16_t)lrintf(af[i] * 32768.f); if (x[i] != b) { bad_af++; if (abs(x[i] - b) > maxd) maxd = abs(x[i] - b); } }
    CHECK(x && n > 0 && bad_af == 0, "ima4 CAF == Apple decoder (%d/%d mismatches, max |d| %d)", bad_af, n, maxd);
    printf("  ima4 CAF: %d frames (ref %d), mismatches %d\n", fr, rfr, bad_af);
    free(x); free(af);
    FILE *f = fopen(fx("ima4_m.aifc"), "rb"); sa_fileinfo fi; char err[128];
    int pr = f ? sa_probe(f, &fi, err, sizeof err) : -1;
    if (f) {
        uint8_t *d = (uint8_t *)malloc(fi.data_bytes); fseek(f, fi.data_offset, SEEK_SET); size_t got = fread(d, 1, fi.data_bytes, f); (void)got; fclose(f);
        int16_t *o = (int16_t *)malloc(sizeof(int16_t) * fi.frames);
        sa_adpcm_cur c; memset(&c, 0, sizeof c);
        int k = sa_adpcm_decode(fi.fmt, d, fi.data_bytes / (uint32_t)fi.block_bytes, fi.block_bytes, 64, 1, &c, o, (int)fi.frames);
        af = read_wav(fx("ima4_m.ref.wav"), &rfr, &rch, &rr);
        bad_af = 0; int nn = k < rfr ? k : rfr;
        for (int i = 0; af && i < nn; i++) if (o[i] != (int16_t)lrintf(af[i] * 32768.f)) bad_af++;
        CHECK(pr == SA_OK && fi.fmt == SA_FMT_IMA_APPLE && nn > 40000 && bad_af == 0, "ima4 AIFC mono == Apple decoder (%d mismatches of %d)", bad_af, nn);
        free(d); free(o); free(af);
    }
    sa_shutdown(s);
}

static void t_aac(void)
{
    printf("[AAC-ADTS via %s]\n", sa_platform_aac() ? sa_platform_aac()->name : "none");
    const sa_aac_vtbl *a = sa_platform_aac();
    if (!a) { CHECK(0, "no platform AAC"); return; }
    size_t len; char *buf = sa_read_file(fx("music.aac"), &len);
    CHECK(buf != NULL, "music.aac");
    if (!buf) return;
    void *dec = a->open(2, 48000);
    CHECK(dec != NULL, "decoder open");
    int cap = 11 * RATE, total = 0, frames = 0;
    int16_t *pcm = (int16_t *)malloc(sizeof(int16_t) * 2 * (size_t)(cap + 4096));
    size_t off = 0;
    while (dec && off + 7 <= len) {
        sa_adts h;
        if (sa_adts_parse((const uint8_t *)buf + off, (int)(len - off), &h) != 1 || (size_t)h.frame_len > len - off) break;
        int n = a->decode(dec, (const uint8_t *)buf + off, h.frame_len, pcm + 2 * (size_t)total, 4096);
        if (n < 0) break;
        total += n; frames++; off += (size_t)h.frame_len;
        if (total > cap) break;
    }
    printf("  %d ADTS frames -> %d PCM frames\n", frames, total);
    int sf, sch, sr;
    float *src = read_wav(fx("music_src.wav"), &sf, &sch, &sr);
    float *dl = (float *)malloc(sizeof(float) * (size_t)total), *sl = (float *)malloc(sizeof(float) * (size_t)sf);
    for (int i = 0; i < total; i++) dl[i] = pcm[2 * i] / 32768.f;
    for (int i = 0; i < sf; i++) sl[i] = src[2 * i];
    double r;
    int lag = xcorr_lag(sl + 48000, 8192, dl, total, 48000 - 200, 48000 + 4200, &r);
    int priming = lag - 48000;
    printf("  decoder output lag vs source (priming): %d frames (r=%.4f); afconvert's nominal 2112\n", priming, r);
    CHECK(priming == SA_AAC_PRIMING_DEFAULT, "priming == %d (measured %d)", SA_AAC_PRIMING_DEFAULT, priming);
    double se = 0, ss = 0;
    for (int i = 48000; i < 9 * 48000; i++) { double d = (double)dl[i + priming] - sl[i]; se += d * d; ss += (double)sl[i] * sl[i]; }
    double snr = 10 * log10(ss / se);
    printf("  AAC 160 kbps SNR vs source (aligned): %.1f dB\n", snr);
    CHECK(snr > 20, "AAC SNR %.1f dB > 20", snr);
    /* afconvert's own decode for comparison */
    int af_f, af_ch; float *afd = read_wav(fx("music.afref.wav"), &af_f, &af_ch, &sr);
    if (afd) {
        float *al = (float *)malloc(sizeof(float) * (size_t)af_f);
        for (int i = 0; i < af_f; i++) al[i] = afd[2 * i];
        int lag2 = xcorr_lag(sl + 48000, 8192, al, af_f, 48000 - 3000, 48000 + 3000, &r);
        printf("  afconvert decode: %d frames (source %d), lag %d (afconvert trims priming itself)\n", af_f, sf, lag2 - 48000);
        free(al); free(afd);
    }
    /* decoder reset + pre-roll seek reproduces the linear decode exactly */
    if (dec) {
        a->reset(dec);
        int target_frame = 100;   /* ADTS frame index */
        size_t o2 = 0; int k = 0;
        while (k < target_frame - 2) { sa_adts h; sa_adts_parse((const uint8_t *)buf + o2, (int)(len - o2), &h); o2 += (size_t)h.frame_len; k++; }
        int16_t tmp[4096 * 2]; int got = 0;
        for (int q = 0; q < 3; q++) {
            sa_adts h; sa_adts_parse((const uint8_t *)buf + o2, (int)(len - o2), &h);
            got = a->decode(dec, (const uint8_t *)buf + o2, h.frame_len, tmp, 4096); o2 += (size_t)h.frame_len;
        }
        /* tmp = output of frame target_frame after 2 frames of pre-roll */
        int maxd = 0;
        for (int i = 0; i < got * 2; i++) { int d = abs(tmp[i] - pcm[(size_t)target_frame * 2048 + i]); if (d > maxd) maxd = d; }
        printf("  seek with 2-frame pre-roll vs linear decode: max |diff| = %d LSB over %d frames\n", maxd, got);
        CHECK(got == 1024 && maxd <= 1, "pre-roll seek exact (max diff %d)", maxd);
        a->close(dec);
    }
    free(dl); free(sl); free(src); free(pcm); free(buf);
}

/* least-squares fit of a*sin + b*cos (+dc) at f; returns tone amplitude, *resid = residual rms / tone rms */
static double fit_sine(const float *x, int n, int stride, double f, double rate, double *resid)
{
    double ss = 0, cc = 0, sc = 0, xs = 0, xc = 0, w = 2 * M_PI * f / rate;
    for (int i = 0; i < n; i++) { double si = sin(w * i), co = cos(w * i), v = x[(size_t)i * stride]; ss += si * si; cc += co * co; sc += si * co; xs += v * si; xc += v * co; }
    double det = ss * cc - sc * sc, a = (xs * cc - xc * sc) / det, b = (xc * ss - xs * sc) / det;
    double e = 0;
    for (int i = 0; i < n; i++) { double d = x[(size_t)i * stride] - a * sin(w * i) - b * cos(w * i); e += d * d; }
    double amp = sqrt(a * a + b * b);
    *resid = sqrt(e / n) / (amp / sqrt(2.0));
    return amp;
}
static void t_resampler(void)
{
    printf("[resampler: linear interpolation]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    struct { const char *ev; int ch; double expect_len; } cases[2] = { { "event:/test/rate24k", 1, 96000 }, { "event:/test/rate44k", 2, 48000 } };
    for (int c = 0; c < 2; c++) {
        rec r = { 0 };
        sa_handle h = sa_event_create(s, cases[c].ev);
        sa_set_poll_mask(s, SA_CB_STOPPED);
        sa_instance_start(s, h); sa_instance_release(s, h);
        int stopped_at = -1;
        for (int b = 0; b < 600 && stopped_at < 0; b++) {
            rec_mix(s, &r, 256, 256, 1);
            sa_notification n[8]; int k = sa_poll(s, n, 8);
            for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED) stopped_at = r.frames;
        }
        /* find last non-silent sample = rendered length */
        int last = 0; for (int i = 0; i < r.frames; i++) if (fabsf(r.buf[2 * i]) > 1e-4f) last = i;
        int first = -1; for (int i = 0; i < r.frames && first < 0; i++) if (fabsf(r.buf[2 * i]) > 1e-4f) first = i;
        int len = last - first + 1;
        /* tone accuracy and image level on a 16384-frame window */
        int w0 = first + 4800, wn = 16384;
        double rr; double a1 = fit_sine(r.buf + 2 * w0, wn, 2, 1000, RATE, &rr);
        double resid = rr * a1;
        double fbest = 1000, abest = 0;
        for (double f = 990; f <= 1010; f += 0.25) { double a = goertzel(r.buf + 2 * w0, wn, 2, f, RATE); if (a > abest) { abest = a; fbest = f; } }
        printf("  %s: length %d (expect ~%.0f), peak %.2f Hz, tone %.4f, residual (images+noise) %.1f dB rel tone, STOPPED at %d\n",
               cases[c].ev, len, cases[c].expect_len, fbest, a1, db(resid / a1), stopped_at);
        CHECK(fabs(len - cases[c].expect_len) <= 3, "length %d", len);
        CHECK(fabs(fbest - 1000) <= 0.5, "frequency %.2f", fbest);
        CHECK(db(resid / a1) < -40, "residual %.1f dB", db(resid / a1));
        free(r.buf);
    }
    /* pitch 1.5 on the 24 kHz take -> 1500 Hz */
    {
        rec r = { 0 };
        sa_handle h = sa_event_create(s, "event:/test/rate24k");
        sa_instance_set_pitch(s, h, 1.5f); sa_instance_start(s, h); sa_instance_release(s, h);
        rec_mix(s, &r, 48000, 256, 1);
        double fbest = 0, abest = 0;
        for (double f = 1490; f <= 1510; f += 0.25) { double a = goertzel(r.buf + 2 * 4800, 16384, 2, f, RATE); if (a > abest) { abest = a; fbest = f; } }
        printf("  pitch 1.5: peak %.2f Hz\n", fbest);
        CHECK(fabs(fbest - 1500) <= 0.5, "pitch 1.5 -> %.2f Hz", fbest);
        free(r.buf);
    }
    sa_shutdown(s);
}

static void t_loops_adpcm(int decode_on_load);
static void t_loops(void)
{
    printf("[loops: sample-exact]\n");
    t_loops_adpcm(0); t_loops_adpcm(1);
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    /* PCM counter: expected = [0..1000) then [200..1000) repeated */
    int16_t *cnt = gen_counter(1000);
    rec r = { 0 };
    sa_handle h = sa_event_create(s, "event:/test/counter_loop");
    sa_instance_start(s, h);
    sa_update(s);
    rec_mix(s, &r, 6000, 256, 0);
    int bad = 0, firstbad = -1;
    for (int i = 0; i < 6000; i++) {
        int src = i < 1000 ? i : 200 + (i - 1000) % 800;
        int got = (int)lrintf(r.buf[2 * i] * 32768.f), gotr = (int)lrintf(r.buf[2 * i + 1] * 32768.f);
        if (got != cnt[src] || gotr != cnt[src]) { if (firstbad < 0) firstbad = i; bad++; }
    }
    CHECK(bad == 0, "pcm loop exact over 6000 frames (%d bad, first %d)", bad, firstbad);
    printf("  pcm16 loop [200,1000): %d/6000 frames differ\n", bad);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_PLAYING, "looping event still PLAYING");
    free(r.buf); free(cnt);
    sa_instance_stop(s, h, 0); sa_instance_release(s, h);
    { float tmp[1024]; for (int k = 0; k < 4; k++) { sa_update(s); sa_mix(s, tmp, 256); } }
    sa_shutdown(s);
    /* AAC streamed loop: render through the ring (IO in sync mode) and compare to the linear decode unrolled */
    s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    const sa_aac_vtbl *a = sa_platform_aac();
    size_t len; char *buf = sa_read_file(fx("music.aac"), &len);
    void *dec = a->open(2, 48000);
    int16_t *lin = (int16_t *)malloc(sizeof(int16_t) * 2 * 520000); int tot = 0; size_t off = 0;
    while (off + 7 <= len && tot < 500000) {
        sa_adts hh; if (sa_adts_parse((const uint8_t *)buf + off, (int)(len - off), &hh) != 1) break;
        int n = a->decode(dec, (const uint8_t *)buf + off, hh.frame_len, lin + 2 * (size_t)tot, 4096); if (n < 0) break; tot += n; off += (size_t)hh.frame_len;
    }
    a->close(dec); free(buf);
    rec r3 = { 0 };
    sa_handle h3 = sa_event_create(s, "event:/test/aac_music");
    sa_instance_start(s, h3);
    for (int k = 0; k < 4; k++) sa_update(s);
    rec_mix(s, &r3, 400000 + 288000 + 20000, 512, 8);
    /* bus:/Music defaultVolume 0.8 */
    int first = -1; for (int i = 0; i < r3.frames && first < 0; i++) if (fabsf(r3.buf[2 * i]) > 1e-5f) first = i;
    double maxd = 0, maxd_seam = 0, se = 0, ss = 0;
    int pr = SA_AAC_PRIMING_DEFAULT;
    for (int i = 0; first >= 0 && i < 650000 && first + i < r3.frames; i++) {
        int src = i < 384000 ? i : 96000 + (i - 384000) % 288000;
        for (int c = 0; c < 2; c++) {
            double want = lin[2 * (size_t)(src + pr) + c] / 32768.0 * 0.8, got = r3.buf[2 * (size_t)(first + i) + c];
            double d = fabs(want - got);
            if (d > maxd) maxd = d;
            int seam = (i >= 384000 - 64 && i < 384000 + 64) || (i >= 672000 - 64 && i < 672000 + 64);
            if (seam && d > maxd_seam) maxd_seam = d;
            se += d * d; ss += want * want;
        }
    }
    printf("  aac stream: first audible frame %d; vs linear decode (x0.8 bus): max |d| %.6f (%.1f LSB), at seams %.6f, SNR %.1f dB\n",
           first, maxd, maxd * 32768, maxd_seam, 10 * log10(ss / (se > 0 ? se : 1e-30)));
    CHECK(first <= 1 && maxd * 32768 < 2.5, "aac loop == linear decode (max %.2f LSB)", maxd * 32768);
    sa_stats st; sa_get_stats(s, &st);
    CHECK(st.underruns == 0, "no underruns (%u)", st.underruns);
    sa_instance_stop(s, h3, 0); sa_instance_release(s, h3);
    for (int k = 0; k < 4; k++) { sa_update(s); float tmp[512]; sa_mix(s, tmp, 256); }
    sa_get_stats(s, &st);
    CHECK(st.instances_live == 0 && st.voices_active == 0, "aac instance cleaned up (%d live, %d voices)", st.instances_live, st.voices_active);
    free(r3.buf); free(lin);
    sa_shutdown(s);
}

static void t_loops_adpcm(int decode_on_load)
{
    int saved = g_adpcm_decode; g_adpcm_decode = decode_on_load ? 96000 : -1;
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    g_adpcm_decode = saved;
    if (!s) return;
    printf("  (ima-adpcm %s)\n", decode_on_load ? "decoded to pcm16 at load" : "decoded per voice in the mixer");
    /* ADPCM loop vs the linear decode unrolled */
    {
        int fr, ch;
        int16_t *lin = load_take(s, "event:/test/adpcm_loop", &fr, &ch);
        rec r2 = { 0 };
        sa_handle h2 = sa_event_create(s, "event:/test/adpcm_loop");
        sa_instance_start(s, h2); sa_update(s);
        rec_mix(s, &r2, 90000, 256, 0);
        int b2 = 0;
        for (int i = 0; i < 90000; i++) {
            int src = i < 30000 ? i : 5000 + (i - 30000) % 25000;
            if ((int)lrintf(r2.buf[2 * i] * 32768.f) != lin[src]) b2++;
        }
        CHECK(b2 == 0, "adpcm loop exact (%d bad of 90000)", b2);
        printf("  ima-adpcm loop [5000,30000): %d/90000 frames differ (3 seams)\n", b2);
        free(r2.buf); free(lin);
        sa_instance_stop(s, h2, 0); sa_instance_release(s, h2);
        { float tmp[1024]; for (int k = 0; k < 4; k++) { sa_update(s); sa_mix(s, tmp, 256); } }
    }
    /* Apple IMA4 loop: the loop-start decoder state is captured on the first pass, so the seam matches too */
    {
        int fr, ch;
        int16_t *lin = load_take(s, "event:/test/ima4_loop", &fr, &ch);
        rec r2 = { 0 };
        sa_handle h2 = sa_event_create(s, "event:/test/ima4_loop");
        sa_instance_start(s, h2); sa_update(s);
        rec_mix(s, &r2, 120000, 256, 0);
        int b2 = 0;
        for (int i = 0; i < 120000; i++) {
            int src = i < 40000 ? i : 3000 + (i - 40000) % 37000;
            for (int c = 0; c < 2; c++) if ((int)lrintf(r2.buf[2 * i + c] * 32768.f) != lin[2 * src + c]) b2++;
        }
        CHECK(b2 == 0, "ima4 stereo loop exact (%d bad samples)", b2);
        printf("  ima4 stereo loop [3000,40000): %d/240000 samples differ (2 seams)\n", b2);
        free(r2.buf); free(lin);
        sa_instance_stop(s, h2, 0); sa_instance_release(s, h2);
    }
    sa_shutdown(s);
}

static void t_pan3d(void)
{
    printf("[3D: measured rolloff + pan, fallback model]\n");
    double worst = 0;
    for (int k = -20; k <= 20; k++) {
        float g[2]; sa_pan_gains(k / 20.f, g);
        double p = (double)g[0] * g[0] + (double)g[1] * g[1];
        if (fabs(p - 2) > worst) worst = fabs(p - 2);
    }
    CHECK(worst < 1e-5, "equal-power helper (|L^2+R^2-2| max %.2e)", worst);
    float a1 = sa_atten_inverse_tapered(1, 1, 20), a2 = sa_atten_inverse_tapered(2, 1, 20), a10 = sa_atten_inverse_tapered(10, 1, 20), a19 = sa_atten_inverse_tapered(19, 1, 20), a20 = sa_atten_inverse_tapered(20, 1, 20);
    CHECK(a1 == 1 && fabsf(a2 - 0.5f) < 1e-6f && fabsf(a10 - 0.1f) < 1e-6f && fabsf(a19 - powf(1.f / 19, 2)) < 1e-6f && a20 == 0, "fallback curve (inverse tapered)");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_vec3 lp = { 0, 0, 0 }, fw = { 0, 0, 1 }, up = { 0, 1, 0 };
    sa_set_listener(s, &lp, NULL, &fw, &up);
    /* the measured model, numbers straight from the manifest's points */
    int em = sa_event_find(s, "event:/test/3d_measured");
    struct { sa_vec3 p; double l, r; const char *what; } cases[] = {
        { { 0, 0, 0 }, 1, 1, "at the listener" },
        { { 0, 0, 5 }, 0.5, 0.5, "front 5 (rolloff point)" },
        { { 0, 0, -5 }, 0.5, 0.5, "behind 5 (front/back fold)" },
        { { 0, 0, 12.5 }, 0.3, 0.3, "front 12.5 (between 5 and 20)" },
        { { 0, 0, 40 }, 0.1, 0.1, "beyond max: held at the last point" },
        { { 10, 0, 0 }, 0.36667 * sqrt(0.02), 0.36667 * sqrt(1.98), "right 10: far share 0.01" },
        { { -10, 0, 0 }, 0.36667 * sqrt(1.98), 0.36667 * sqrt(0.02), "left 10: mirrored" },
        { { 1, 0, 0 }, sqrt(2 * 0.255), sqrt(2 * 0.745), "right 1: near blend 0.5" },
        { { 2.828427f, 0, 2.828427f }, 0.625 * sqrt(0.4), 0.625 * sqrt(1.6), "45 deg at 4 (pan distance)" },
    };
    double werr = 0;
    for (size_t k = 0; k < sizeof cases / sizeof cases[0]; k++) {
        float g[2]; sa_spatial_gains_at(s, em, cases[k].p, g);
        double e = fmax(fabs(g[0] - cases[k].l), fabs(g[1] - cases[k].r));
        if (e > werr) werr = e;
        if (e > 1e-4) printf("  %s: L %.5f R %.5f (expect %.5f %.5f)\n", cases[k].what, g[0], g[1], cases[k].l, cases[k].r);
    }
    printf("  measured model: %zu positions, max |gain - expected| %.2e\n", sizeof cases / sizeof cases[0], werr);
    CHECK(werr < 1e-4, "measured rolloff + pan interpolation");
    /* through the mixer: the measured event at right 10 */
    sa_handle h = sa_event_create(s, "event:/test/3d_measured");
    sa_vec3 src = { 10, 0, 0 };
    sa_instance_set_3d(s, h, &src, NULL);
    sa_instance_start(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 24000, 256, 1);
    double L = rms(r.buf + 2 * 4800, 16000, 2), R = rms(r.buf + 2 * 4800 + 1, 16000, 2), a = 0.5 / sqrt(2.0);
    printf("  mixer, right 10: L %.5f R %.5f (expect %.5f %.5f; L-R %.1f dB)\n", L, R, a * cases[5].l, a * cases[5].r, 20 * log10(L / R));
    CHECK(fabs(L - a * cases[5].l) < 0.0015 && fabs(R - a * cases[5].r) < 0.002, "mixer applies the measured gains");
    sa_instance_stop(s, h, 0); sa_instance_release(s, h);
    /* the fallback (no measured model): inverse tapered + the built-in FMOD pan; the far side is not 0 */
    sa_vec3 r5 = { 5, 0, 0 };
    h = sa_event_create(s, "event:/test/3d");
    sa_instance_set_3d(s, h, &r5, NULL);
    sa_instance_start(s, h); sa_instance_release(s, h);
    rec_mix(s, &r, 24000, 256, 1);
    L = rms(r.buf + 2 * (24000 + 4800), 16000, 2); R = rms(r.buf + 2 * (24000 + 4800) + 1, 16000, 2);
    double att = sa_atten_inverse_tapered(5, 1, 20);
    printf("  fallback, right 5: L %.5f R %.5f (L-R %.1f dB; old model: L 0); expect %.5f %.5f\n", L, R, 20 * log10(L / R), a * att * sqrt(2 * 0.0131), a * att * sqrt(2 * (1 - 0.0131)));
    CHECK(fabs(L - a * att * sqrt(2 * 0.0131)) < 0.0005 && fabs(R - a * att * sqrt(2 * (1 - 0.0131))) < 0.002, "fallback far channel at about -19 dB, not 0");
    /* move to the front: centred, attenuation at d=5 */
    sa_vec3 front = { 0, 0, 5 };
    sa_instance_set_3d(s, h, &front, NULL);
    rec_mix(s, &r, 24000, 256, 1);
    L = rms(r.buf + 2 * (48000 + 9600), 9600, 2); R = rms(r.buf + 2 * (48000 + 9600) + 1, 9600, 2);
    printf("  fallback, front 5: L %.5f R %.5f (expect both %.5f)\n", L, R, a * att);
    CHECK(fabs(L - R) < 1e-4 && fabs(L - a * att) < 0.002, "front centred");
    /* the moving source must not click: max sample-to-sample delta stays at the sine's slope */
    double md = 0; for (int i = 24000 + 1; i < r.frames; i++) { double d = fabs(r.buf[2 * i] - r.buf[2 * i - 2]); if (d > md) md = d; }
    double slope = 2 * M_PI * 1000 / RATE * 0.5 * 1.42 * 0.2 * 1.05;
    printf("  max |dx| left %.5f (sine slope bound %.5f)\n", md, slope);
    CHECK(md <= slope, "no click on 3D move");
    free(r.buf);
    sa_shutdown(s);
}

static void t_param(void)
{
    printf("[parameter crossfade]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_handle h = sa_event_create(s, "event:/test/param");
    sa_instance_start(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 24000, 256, 4);          /* updates every 1024 frames (~47 Hz) */
    sa_instance_set_param(s, h, "x", 1.0f);
    int t_set = r.frames;
    rec_mix(s, &r, 24000, 256, 4);
    /* 440 vs 880 amplitude in 5 ms windows */
    int win = 240, start = -1, end = -1; double dip = 0;
    for (int w = 0; w + win <= r.frames; w += win) {
        double a = goertzel(r.buf + 2 * w, win, 2, 440, RATE), b = goertzel(r.buf + 2 * w, win, 2, 880, RATE);
        double tot = rms(r.buf + 2 * w, win, 2);
        if (w > t_set - 2 * win && start < 0 && b > 0.02) start = w;
        if (start >= 0 && end < 0 && a < 0.02) end = w;
        if (w > t_set && w < t_set + 9600) { double dd = db(tot / (0.4 / sqrt(2.0))); if (dd < dip) dip = dd; }
    }
    double a_before = goertzel(r.buf + 2 * 12000, 4800, 2, 440, RATE), b_after = goertzel(r.buf + 2 * (r.frames - 6000), 4800, 2, 880, RATE);
    double a_after = goertzel(r.buf + 2 * (r.frames - 6000), 4800, 2, 440, RATE);
    /* 10%..90% rise of the 880 Hz amplitude (2.5 ms windows): a linear 80 ms ramp gives 64 ms */
    int t10 = -1, t90 = -1;
    for (int w = t_set - 480; w + 120 <= r.frames && t90 < 0; w += 120) {
        double b = goertzel(r.buf + 2 * w, 480, 2, 880, RATE) / 0.4;
        if (t10 < 0 && b >= 0.1) t10 = w + 240;
        if (t10 >= 0 && b >= 0.9) t90 = w + 240;
    }
    printf("  set at %d; 880 Hz 10%%..90%% rise %.1f ms (linear %g ms ramp -> %.1f ms), starts %.1f ms after the set; 440 gone at +%.1f ms; level dip %.2f dB (equal-gain, uncorrelated takes)\n",
           t_set, (t90 - t10) * 1000.0 / RATE, 80.0, 64.0, (t10 - t_set) * 1000.0 / RATE - 8.0, (end - t_set) * 1000.0 / RATE, dip);
    CHECK(t10 >= 0 && t90 > t10 && fabs((t90 - t10) * 1000.0 / RATE - 64.0) < 8.0, "crossfade shape (10-90%% %.1f ms)", (t90 - t10) * 1000.0 / RATE);
    printf("  440 before %.3f, 880 after %.3f, 440 after %.5f\n", a_before, b_after, a_after);
    CHECK(a_before > 0.35 && b_after > 0.35 && a_after < 0.002, "switch complete");
    CHECK(start >= 0 && end > start && (end - start) * 1000.0 / RATE < 120, "crossfade ~80 ms");
    /* phase alignment: the takes are time-aligned renders; the new take starts at the old take's position */
    sa_instance_stop(s, h, 0); sa_instance_release(s, h);
    free(r.buf);
    /* global parameter */
    sa_handle g = sa_event_create(s, "event:/test/global");
    sa_instance_start(s, g);
    rec r2 = { 0 };
    rec_mix(s, &r2, 12000, 256, 4);
    CHECK(sa_instance_set_param(s, g, "Global1", 1) == SA_ERR_INVALID_PARAM, "global param not settable per instance");
    sa_set_global_param(s, "Global1", 1.0f);
    rec_mix(s, &r2, 24000, 256, 4);
    double a300 = goertzel(r2.buf + 2 * 2000, 4800, 2, 300, RATE), a600 = goertzel(r2.buf + 2 * (r2.frames - 6000), 4800, 2, 600, RATE);
    printf("  global param: 300 Hz before %.3f, 600 Hz after %.3f\n", a300, a600);
    CHECK(a300 > 0.45 && a600 > 0.45, "global param switch");
    sa_instance_stop(s, g, 0); sa_instance_release(s, g);
    free(r2.buf);
    /* section selection (bake contract): unspecified params sit at their default; discrete mismatch = 1; ties go to
     * the parameter with more variants (room_id: 2 variants vs night: 1) */
    {
        struct { float night, room; double f; const char *what; } c[] = {
            { 0, 0, 300, "all default -> null take" }, { 1, 0, 600, "night=1 -> night take" }, { 0, 2, 880, "room 2 -> room take" },
            { 1, 1, 440, "night=1 + room 1 -> room (more variants)" }, { 0.3f, 0, 300, "night=0.3 -> nearer the default" }, { 0.7f, 0, 600, "night=0.7 -> night take" } };
        for (int k = 0; k < 6; k++) {
            sa_handle h3 = sa_event_create(s, "event:/test/sections");
            sa_instance_set_param(s, h3, "night", c[k].night); sa_instance_set_param(s, h3, "room_id", c[k].room);
            sa_instance_start(s, h3);
            rec r3 = { 0 };
            rec_mix(s, &r3, 9600, 256, 1);
            double a = goertzel(r3.buf + 2 * 4800, 4800, 2, c[k].f, RATE);
            CHECK(a > 0.3, "%s (%.0f Hz amplitude %.3f)", c[k].what, c[k].f, a);
            sa_instance_stop(s, h3, 0); sa_instance_release(s, h3);
            rec_mix(s, &r3, 1024, 256, 1);
            free(r3.buf);
        }
        printf("  section selection: 6 parameter combinations checked\n");
    }
    sa_shutdown(s);
}

static void t_stopped(void)
{
    printf("[STOPPED semantics]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_handle h = sa_event_create(s, "event:/test/short100");
    sa_instance_set_callback(s, h, NULL, SA_CB_ALL);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED, "created = STOPPED");
    sa_instance_start(s, h);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STARTING, "start -> STARTING");
    sa_update(s);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_PLAYING, "update -> PLAYING");
    rec r = { 0 };
    int stopped_at = -1, started = 0, got_starting = 0;
    sa_notification n[16];
    int k = sa_poll(s, n, 16);
    for (int q = 0; q < k; q++) { if (n[q].type == SA_CB_STARTED) started = 1; if (n[q].type == SA_CB_STARTING) got_starting = 1; }
    for (int b = 0; b < 40 && stopped_at < 0; b++) {
        rec_mix(s, &r, 256, 256, 1);
        k = sa_poll(s, n, 16);
        for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED) stopped_at = r.frames;
    }
    printf("  100 ms one-shot (4800 frames): STOPPED seen at update after frame %d\n", stopped_at);
    CHECK(started && got_starting, "STARTING+STARTED");
    CHECK(stopped_at >= 4800 && stopped_at <= 4800 + 512, "STOPPED right after the end (%d)", stopped_at);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED, "state STOPPED");
    sa_instance_release(s, h);
    sa_update(s);
    k = sa_poll(s, n, 16);
    int destroyed = 0; for (int q = 0; q < k; q++) if (n[q].type == SA_CB_DESTROYED && n[q].instance == h) destroyed = 1;
    CHECK(destroyed && !sa_instance_is_valid(s, h), "released+stopped -> DESTROYED, handle invalid");
    CHECK(sa_instance_start(s, h) == SA_ERR_INVALID_HANDLE, "stale handle rejected");
    /* released while playing -> STOPPED then DESTROYED (PlayOneShot pattern) */
    h = sa_event_create(s, "event:/test/short100");
    sa_instance_set_callback(s, h, NULL, SA_CB_STOPPED | SA_CB_DESTROYED);
    sa_instance_start(s, h); sa_instance_release(s, h);
    int got_stop = 0, got_destroy = 0;
    for (int b = 0; b < 40; b++) { rec_mix(s, &r, 256, 256, 1); k = sa_poll(s, n, 16); for (int q = 0; q < k; q++) { if (n[q].type == SA_CB_STOPPED) got_stop = 1; if (n[q].type == SA_CB_DESTROYED) got_destroy = 1; } }
    CHECK(got_stop && got_destroy, "one-shot release: STOPPED + DESTROYED");
    /* held non-oneshot without loop stays PLAYING */
    h = sa_event_create(s, "event:/test/hold_noloop");
    sa_instance_start(s, h);
    rec_mix(s, &r, 9600, 256, 1);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_PLAYING, "non-oneshot holds PLAYING after its take");
    /* restart */
    sa_instance_set_callback(s, h, NULL, SA_CB_RESTARTED | SA_CB_STOPPED);
    sa_instance_start(s, h); sa_update(s);
    k = sa_poll(s, n, 16); int rs = 0; for (int q = 0; q < k; q++) if (n[q].type == SA_CB_RESTARTED) rs = 1;
    CHECK(rs, "start on a playing instance -> RESTARTED");
    /* immediate stop: STOPPED at once, callback at next update */
    sa_instance_stop(s, h, 0);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED, "stop(immediate) -> STOPPED at once");
    k = sa_poll(s, n, 16); CHECK(k == 0, "no callback before update");
    sa_update(s); k = sa_poll(s, n, 16);
    CHECK(k == 1 && n[0].type == SA_CB_STOPPED, "STOPPED delivered at next update");
    sa_instance_release(s, h);
    /* fade stop: STOPPING then STOPPED after ~fadeout_ms */
    h = sa_event_create(s, "event:/test/bus_sfx");
    sa_instance_start(s, h); rec_mix(s, &r, 4800, 256, 1);
    int t0 = r.frames;
    sa_instance_stop(s, h, 1);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STOPPING, "stop(fadeout) -> STOPPING");
    int t_stop = -1;
    for (int b = 0; b < 60 && t_stop < 0; b++) { rec_mix(s, &r, 256, 256, 1); if (sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED) t_stop = r.frames - t0; }
    printf("  fade stop: STOPPED after %.1f ms (fadeout 50 ms)\n", t_stop * 1000.0 / RATE);
    CHECK(t_stop >= 2400 && t_stop <= 2400 + 768, "fadeout timing (%d)", t_stop);
    sa_instance_release(s, h);
    /* takeless one-shot with lengthMs 250 */
    h = sa_event_create(s, "event:/test/takeless");
    sa_instance_start(s, h);
    int t1 = r.frames, t_end = -1;
    for (int b = 0; b < 100 && t_end < 0; b++) { rec_mix(s, &r, 256, 256, 1); if (sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED) t_end = r.frames - t1; }
    printf("  takeless 250 ms one-shot: STOPPED after %.1f ms\n", t_end * 1000.0 / RATE);
    CHECK(t_end >= 12000 && t_end <= 12000 + 512, "takeless timing");
    sa_instance_release(s, h);
    /* missing file: silent but STOPPED on time (virtual) */
    h = sa_event_create(s, "event:/test/missing_file");
    sa_instance_start(s, h);
    int t2 = r.frames, t_m = -1;
    for (int b = 0; b < 100 && t_m < 0; b++) { rec_mix(s, &r, 256, 256, 1); if (sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED) t_m = r.frames - t2; }
    printf("  missing take file (300 ms): STOPPED after %.1f ms\n", t_m * 1000.0 / RATE);
    CHECK(t_m >= 14400 && t_m <= 14400 + 768, "missing file timing");
    sa_instance_release(s, h);
    free(r.buf);
    sa_shutdown(s);
}

/* FMOD's STOPPED frame per take (bake stopFrame): STOPPED there, the rest of the take plays on as a tail */
static void t_stopframe(void)
{
    printf("[STOPPED at the bake's stopFrame]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_set_poll_mask(s, SA_CB_STOPPED | SA_CB_DESTROYED);
    rec r = { 0 };
    sa_notification n[16];
    /* early: STOPPED at 4800 of 9600 frames; released at STOPPED, the take must still play to its end */
    sa_handle h = sa_event_create(s, "event:/test/stop_early");
    sa_instance_start(s, h);
    int stopped_at = -1, destroyed_at = -1;
    for (int b = 0; b < 60; b++) {
        rec_mix(s, &r, 256, 256, 1);
        int k = sa_poll(s, n, 16);
        for (int q = 0; q < k; q++) {
            if (n[q].type == SA_CB_STOPPED && stopped_at < 0) { stopped_at = r.frames; sa_instance_release(s, h); }
            if (n[q].type == SA_CB_DESTROYED) destroyed_at = r.frames;
        }
    }
    double tail = rms(r.buf + 2 * 6000, 3000, 2), a = 0.5 / sqrt(2.0), after = rms(r.buf + 2 * 9900, 2000, 2);
    sa_stats st; sa_get_stats(s, &st);
    printf("  stopFrame 4800 of 9600: STOPPED seen at %d, DESTROYED at %d; tail rms after STOPPED %.4f (take %.4f), after the take %.5f; voices left %d\n",
           stopped_at, destroyed_at, tail, a, after, st.voices_active);
    CHECK(stopped_at >= 4800 && stopped_at <= 4800 + 512, "STOPPED at stopFrame (%d)", stopped_at);
    CHECK(fabs(tail - a) < 0.01 && after < 1e-4, "tail plays on after STOPPED + release, ends with the take");
    CHECK(st.voices_active == 0, "orphan voice freed when it ended");
    /* late: the take (4800) ends before FMOD's STOPPED (14400): STOPPED there, silence in between */
    int t0 = r.frames;
    h = sa_event_create(s, "event:/test/stop_late");
    sa_instance_start(s, h);
    stopped_at = -1;
    for (int b = 0; b < 100 && stopped_at < 0; b++) {
        rec_mix(s, &r, 256, 256, 1);
        int k = sa_poll(s, n, 16);
        for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED) stopped_at = r.frames - t0;
    }
    double gap = rms(r.buf + 2 * (t0 + 5400), 8000, 2);
    printf("  stopFrame 14400 after a 4800-frame take: STOPPED after %d frames; level in between %.5f\n", stopped_at, gap);
    CHECK(stopped_at >= 14400 && stopped_at <= 14400 + 512, "STOPPED at stopFrame after the take's end (%d)", stopped_at);
    CHECK(gap < 1e-4, "silent between the take's end and STOPPED");
    sa_instance_release(s, h);
    /* the tail follows its bus: mute while the orphan plays */
    h = sa_event_create(s, "event:/test/stop_early");
    sa_instance_start(s, h); sa_instance_release(s, h);
    int t1 = r.frames, muted_at = -1;
    for (int b = 0; b < 60; b++) {
        rec_mix(s, &r, 256, 256, 1);
        int k = sa_poll(s, n, 16);
        for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED && muted_at < 0) { sa_bus_set_mute(s, "bus:/SFX", 1); muted_at = r.frames - t1; }
    }
    double m = rms(r.buf + 2 * (t1 + muted_at + 1024), 2048, 2);
    printf("  bus muted at STOPPED: tail rms %.5f\n", m);
    CHECK(m < 1e-4, "orphan tail follows its bus");
    sa_bus_set_mute(s, "bus:/SFX", 0);
    free(r.buf);
    sa_shutdown(s);
}

/* stop(ALLOWFADEOUT) with the event's measured release (bake releaseCurve / releaseStopMs) */
static void t_release(void)
{
    printf("[measured release on stop(fadeout)]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_set_poll_mask(s, SA_CB_STOPPED);
    sa_handle h = sa_event_create(s, "event:/test/release");
    sa_instance_start(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 9600, 256, 1);
    int t0 = r.frames;
    sa_instance_stop(s, h, 1);
    int stop_state = sa_instance_get_playback_state(s, h), stopped_at = -1;
    sa_notification n[16];
    for (int b = 0; b < 160; b++) {
        rec_mix(s, &r, 256, 256, 1);
        int k = sa_poll(s, n, 16);
        for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED && stopped_at < 0) { stopped_at = r.frames - t0; sa_instance_release(s, h); }
    }
    double a = 0.5 / sqrt(2.0);
    /* expected envelope: 1 -> 0.5 at 100 ms, -> 0.1 at 300 ms, -> 0 at 500 ms (linear in gain between) */
    struct { double ms, g; } w[] = { { 50, 0.75 }, { 200, 0.3 }, { 400, 0.05 } };
    double worst = 0;
    for (int k = 0; k < 3; k++) {
        int c = t0 + (int)(w[k].ms * 48);
        double got = rms(r.buf + 2 * (c - 240), 480, 2) / a;
        printf("  %3.0f ms after stop: gain %.4f (expect %.4f)\n", w[k].ms, got, w[k].g);
        if (fabs(got - w[k].g) > worst) worst = fabs(got - w[k].g);
    }
    double after = rms(r.buf + 2 * (t0 + 520 * 48), 4800, 2);
    printf("  STOPPED after %.1f ms (releaseStopMs 200); level after 520 ms %.6f\n", stopped_at / 48.0, after);
    CHECK(stop_state == SA_STATE_STOPPING, "stop(fadeout) -> STOPPING");
    CHECK(stopped_at >= 200 * 48 && stopped_at <= 200 * 48 + 512, "STOPPED at releaseStopMs (%d)", stopped_at);
    CHECK(worst < 0.02, "fade follows the measured curve (max error %.4f)", worst);
    CHECK(after < 1e-5, "release ends at 0 (the rest after STOPPED played as a tail)");
    /* without a measured release the fade stays cfg.fadeout_ms */
    free(r.buf);
    sa_shutdown(s);
}

/* parameter-gated events: FMOD is silent at the defaults (the bake has no take there) */
static void t_silent_group(void)
{
    printf("[silent at the defaults]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    const char *evs[2] = { "event:/test/silent_default", "event:/test/silent_inferred" };
    const char *par[2] = { "power", "shore_id" };
    for (int e = 0; e < 2; e++) {
        sa_handle h = sa_event_create(s, evs[e]);
        sa_instance_start(s, h);
        rec r = { 0 };
        rec_mix(s, &r, 9600, 256, 1);
        double at_def = rms(r.buf + 2 * 1024, 8000, 2);
        int st_def = sa_instance_get_playback_state(s, h);
        sa_instance_set_param(s, h, par[e], 1);
        rec_mix(s, &r, 14400, 256, 1);
        double on = rms(r.buf + 2 * (9600 + 7200), 7000, 2);
        sa_instance_set_param(s, h, par[e], 0);
        rec_mix(s, &r, 14400, 256, 1);
        double off = rms(r.buf + 2 * (24000 + 7200), 7000, 2);
        printf("  %s: at the default %.6f (state %d), %s=1 %.4f, back to 0 %.6f\n", evs[e], at_def, st_def, par[e], on, off);
        CHECK(at_def < 1e-5 && st_def == SA_STATE_PLAYING, "%s: silent and PLAYING at the default", evs[e]);
        CHECK(fabs(on - 0.4 / sqrt(2.0)) < 0.01 && off < 1e-5, "%s: plays when the parameter opens it (p440 is a 0.4 sine), silent again after", evs[e]);
        sa_instance_stop(s, h, 0); sa_instance_release(s, h);
        free(r.buf);
    }
    /* one-shot silent at the default: STOPPED after the bake's silentStopFrame, no sound */
    sa_handle h = sa_event_create(s, "event:/test/silent_oneshot");
    sa_instance_start(s, h);
    rec r = { 0 };
    int t_end = -1;
    for (int b = 0; b < 100 && t_end < 0; b++) { rec_mix(s, &r, 256, 256, 1); if (sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED) t_end = r.frames; }
    double lvl = rms(r.buf, r.frames, 2);
    printf("  silent one-shot: STOPPED after %d frames (silentStopFrame 7200), level %.6f\n", t_end, lvl);
    CHECK(t_end >= 7200 && t_end <= 7200 + 512 && lvl < 1e-5, "silent one-shot stops on time");
    sa_instance_release(s, h);
    free(r.buf);
    sa_shutdown(s);
}

/* native callback that re-enters the API */
static sa_system *g_cb_sys; static int g_cb_count;
static void cb_release(uint32_t type, sa_handle h, void *u)
{
    (void)u;
    if (type == SA_CB_STOPPED) { g_cb_count++; sa_instance_release(g_cb_sys, h); sa_instance_get_playback_state(g_cb_sys, h); }
}
static void t_callbacks(void)
{
    printf("[callbacks: run in sa_update, may re-enter]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    g_cb_sys = s; g_cb_count = 0;
    for (int k = 0; k < 10; k++) { sa_handle h = sa_event_create(s, "event:/test/short100"); sa_instance_set_callback(s, h, cb_release, SA_CB_STOPPED); sa_instance_start(s, h); }
    rec r = { 0 };
    rec_mix(s, &r, 9600, 256, 1);
    rec_mix(s, &r, 1024, 256, 1);
    sa_stats st; sa_get_stats(s, &st);
    CHECK(g_cb_count == 10 && st.instances_live == 0, "10 STOPPED callbacks, all released+destroyed (%d, live %d)", g_cb_count, st.instances_live);
    free(r.buf);
    sa_shutdown(s);
}

static void t_variations(void)
{
    printf("[variations: weighted]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    int count[3] = { 0, 0, 0 };
    int e = sa_event_find(s, "event:/test/variations");
    for (int k = 0; k < 400; k++) {
        sa_handle h = sa_event_create_index(s, e);
        sa_instance_start(s, h);
        sa_instance *i = &s->inst[h & 0xFFF];
        int take = i->layers[0].take - s->events[e].first_take;
        if (take >= 0 && take < 3) count[take]++;
        sa_instance_stop(s, h, 0); sa_instance_release(s, h);
        sa_update(s);
    }
    printf("  400 starts: %d / %d / %d (weights 1/2/1)\n", count[0], count[1], count[2]);
    CHECK(count[1] > 160 && count[1] < 240 && count[0] > 60 && count[2] > 60, "distribution");
    sa_shutdown(s);
}

static void t_stealing(void)
{
    printf("[voice pool: stealing + virtual voices]\n");
    sa_system *s = open_sys(8, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_set_poll_mask(s, SA_CB_STOPPED);
    /* 12 one-shots (0.5 s) with decreasing volume on an 8-voice pool: the 4 quietest go virtual, all 12 STOP */
    sa_handle hs[12];
    for (int k = 0; k < 12; k++) {
        hs[k] = sa_event_create(s, "event:/test/sine440_mono");
        sa_instance_set_volume(s, hs[k], 1.0f - k * 0.07f);
        sa_instance_start(s, hs[k]); sa_instance_release(s, hs[k]);
    }
    rec r = { 0 };
    rec_mix(s, &r, 256, 256, 1);
    sa_stats st; sa_get_stats(s, &st);
    printf("  after start: %d voices, %d virtual, steals %u, virtualized %u\n", st.voices_active, st.layers_virtual, st.steals, st.virtualized);
    CHECK(st.voices_active == 8 && st.layers_virtual == 4, "8 real + 4 virtual");
    int stops = 0;
    for (int b = 0; b < 120; b++) { rec_mix(s, &r, 256, 256, 1); sa_notification n[32]; int k = sa_poll(s, n, 32); for (int q = 0; q < k; q++) if (n[q].type == SA_CB_STOPPED) stops++; }
    CHECK(stops == 12, "all 12 STOPPED (%d)", stops);
    /* steal: 8 quiet loops, then a loud one-shot steals the quietest */
    for (int k = 0; k < 8; k++) { sa_handle h = sa_event_create(s, "event:/test/bus_sfx"); sa_instance_set_volume(s, h, 0.05f + 0.01f * k); sa_instance_start(s, h); }
    rec_mix(s, &r, 2048, 256, 1);
    sa_stats st0; sa_get_stats(s, &st0);
    int before = r.frames;
    sa_handle loud = sa_event_create(s, "event:/test/sine440_mono");
    sa_instance_start(s, loud); sa_instance_release(s, loud);
    rec_mix(s, &r, 4096, 256, 1);
    sa_get_stats(s, &st);
    printf("  loud one-shot on a full pool: steals +%u, tails used, hard cuts %u\n", st.steals - st0.steals, st.hard_cuts);
    CHECK(st.steals - st0.steals == 1 && st.hard_cuts == 0, "one steal, no hard cut");
    /* click check around the steal: the quiet loop (700 Hz, amp 0.05*0.5) fades in 4 ms */
    double md = 0;
    for (int i = before + 1; i < before + 1024; i++) { double d = fabs(r.buf[2 * i] - r.buf[2 * i - 2]); if (d > md) md = d; }
    double bound = 2 * M_PI * 440 / RATE * 0.5 + 8 * 2 * M_PI * 700 / RATE * 0.5 * 0.12;
    printf("  max |dx| at the steal %.5f (smooth bound %.5f)\n", md, bound);
    CHECK(md < bound, "steal is click-free");
    /* the stolen loop comes back when a voice frees (devirtualisation) */
    rec_mix(s, &r, 48000, 256, 1);
    sa_get_stats(s, &st);
    printf("  after the one-shot ended: %d voices, %d virtual, devirtualized %u\n", st.voices_active, st.layers_virtual, st.devirtualized);
    CHECK(st.layers_virtual == 0 && st.devirtualized >= 1, "loop devirtualised");
    free(r.buf);
    sa_shutdown(s);
}

static void t_buses(void)
{
    printf("[buses + snapshots]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    sa_handle h = sa_event_create(s, "event:/test/bus_sfx");   /* bus:/SFX/Hits, 700 Hz, amp 0.5 */
    sa_instance_start(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 9600, 256, 1);
    double base = rms(r.buf + 2 * 4800, 4800, 2);
    sa_bus_set_volume(s, "bus:/SFX", 0.5f);
    rec_mix(s, &r, 9600, 256, 1);
    double half = rms(r.buf + 2 * (9600 + 4800), 4800, 2);
    printf("  parent bus 0.5: %.2f dB\n", db(half / base));
    CHECK(fabs(db(half / base) + 6.02) < 0.05, "bus volume through hierarchy");
    sa_bus_set_volume(s, "bus:/SFX", 1.0f);
    sa_bus_set_mute(s, "bus:/SFX/Hits", 1);
    rec_mix(s, &r, 4800, 256, 1);
    CHECK(rms(r.buf + 2 * (19200 + 2400), 2400, 2) < 1e-6, "mute");
    sa_bus_set_mute(s, "bus:/SFX/Hits", 0);
    rec_mix(s, &r, 4800, 256, 1);
    /* snapshot: SFX -> 0.25 (-12 dB) faded over 200 ms */
    int t0 = r.frames;
    sa_handle snap = sa_snapshot_start(s, "snapshot:/test/duck");
    rec_mix(s, &r, 24000, 256, 1);
    double ducked = rms(r.buf + 2 * (t0 + 14400), 4800, 2);
    int reach = -1;
    for (int w = t0; w + 480 <= r.frames; w += 480) if (reach < 0 && db(rms(r.buf + 2 * w, 480, 2) / base) < -11.0) reach = w - t0;
    printf("  snapshot duck: %.2f dB, reached -11 dB after %.1f ms\n", db(ducked / base), reach * 1000.0 / RATE);
    CHECK(fabs(db(ducked / base) + 12.04) < 0.1, "snapshot -12 dB");
    CHECK(reach > 6000 && reach < 12000, "snapshot fade ~200 ms");
    sa_snapshot_stop(s, snap, 1);
    rec_mix(s, &r, 24000, 256, 1);
    double back = rms(r.buf + 2 * (r.frames - 4800), 4800, 2);
    CHECK(fabs(db(back / base)) < 0.05, "snapshot released (%.2f dB)", db(back / base));
    /* bus pause freezes the voice; unpause resumes from the same position */
    sa_bus_set_paused(s, "bus:/SFX", 1);
    rec_mix(s, &r, 4800, 256, 1);
    CHECK(rms(r.buf + 2 * (r.frames - 2400), 2400, 2) < 1e-6, "bus paused -> silent");
    uint32_t pos_paused = s->pub_pos[s->inst[h & 0xFFF].layers[0].voice];
    rec_mix(s, &r, 4800, 256, 1);
    uint32_t pos_paused2 = s->pub_pos[s->inst[h & 0xFFF].layers[0].voice];
    CHECK(pos_paused == pos_paused2, "position frozen while paused");
    sa_bus_set_paused(s, "bus:/SFX", 0);
    rec_mix(s, &r, 4800, 256, 1);
    CHECK(rms(r.buf + 2 * (r.frames - 2400), 2400, 2) > base * 0.9, "resumed");
    /* parameter-driven snapshot (the game's volume sliders): 1 = no change, 0.5 -> -6 dB, 0.6 -> linear between points */
    sa_handle vs = sa_snapshot_start(s, "snapshot:/test/sfx_volume");
    rec_mix(s, &r, 14400, 256, 1);
    double v1 = rms(r.buf + 2 * (r.frames - 4800), 4800, 2);
    sa_instance_set_param(s, vs, "sfx_bus_volume", 0.5f);
    rec_mix(s, &r, 4800, 256, 1);
    double v05 = rms(r.buf + 2 * (r.frames - 2400), 2400, 2);
    sa_instance_set_param(s, vs, "sfx_bus_volume", 0.6f);
    rec_mix(s, &r, 4800, 256, 1);
    double v06 = rms(r.buf + 2 * (r.frames - 2400), 2400, 2);
    sa_instance_set_param(s, vs, "sfx_bus_volume", 0.0f);
    rec_mix(s, &r, 4800, 256, 1);
    double v0 = rms(r.buf + 2 * (r.frames - 2400), 2400, 2);
    printf("  volume snapshot: param 1 %.2f dB, 0.5 %.2f dB, 0.6 %.2f dB (expect %.2f), 0 %.1f dB\n", db(v1 / base), db(v05 / base), db(v06 / base), db(0.6), db(v0 / base + 1e-12));
    CHECK(fabs(db(v1 / base)) < 0.05 && fabs(db(v05 / base) + 6.02) < 0.1 && fabs(db(v06 / base) - db(0.6)) < 0.1 && v0 < base * 1e-3, "volume snapshot curve");
    /* a second instance of the same snapshot (a second AudioManager's copy): the newest has priority, nothing stacks */
    sa_instance_set_param(s, vs, "sfx_bus_volume", 0.5f);
    sa_handle vs2 = sa_snapshot_start(s, "snapshot:/test/sfx_volume");
    sa_instance_set_param(s, vs2, "sfx_bus_volume", 0.5f);
    double worst = 0;
    for (int b = 0; b < 40; b++) {   /* while the copy fades in (200 ms) and after: always -6 dB, never -12 */
        rec_mix(s, &r, 480, 256, 1);
        double x = db(rms(r.buf + 2 * (r.frames - 240), 240, 2) / base) + 6.02;
        if (fabs(x) > fabs(worst)) worst = x;
    }
    printf("  two instances of one volume snapshot at 0.5: worst %.2f dB off -6.02 during the copy's fade-in and after\n", worst);
    CHECK(fabs(worst) < 0.15, "same snapshot twice: newest has priority (no double attenuation)");
    sa_instance_set_param(s, vs2, "sfx_bus_volume", 0.25f);
    rec_mix(s, &r, 4800, 256, 1);
    double v025 = rms(r.buf + 2 * (r.frames - 2400), 2400, 2);
    CHECK(fabs(db(v025 / base) - db(0.25)) < 0.1, "the newest copy's value applies (%.2f dB)", db(v025 / base));
    sa_snapshot_stop(s, vs2, 0);
    rec_mix(s, &r, 4800, 256, 1);
    double vback = rms(r.buf + 2 * (r.frames - 2400), 2400, 2);
    CHECK(fabs(db(vback / base) + 6.02) < 0.1, "the older copy takes over again when the newest stops (%.2f dB)", db(vback / base));
    sa_snapshot_stop(s, vs, 0);
    rec_mix(s, &r, 2400, 256, 1);
    sa_bus_stop_all(s, "bus:/SFX", 0);
    sa_update(s);
    CHECK(sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED, "bus stop_all");
    sa_instance_release(s, h);
    free(r.buf);
    sa_shutdown(s);
}

static void t_limiter(void)
{
    printf("[master limiter]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    /* three 0.5-amplitude 440 Hz voices in phase sum to 1.5 */
    for (int k = 0; k < 3; k++) { sa_handle h = sa_event_create(s, "event:/test/sine440_mono"); sa_instance_start(s, h); sa_instance_release(s, h); }
    rec r = { 0 };
    rec_mix(s, &r, 12000, 256, 1);
    double pk = 0; for (int i = 0; i < 2 * r.frames; i++) if (fabs(r.buf[i]) > pk) pk = fabs(r.buf[i]);
    double a = goertzel(r.buf + 2 * 4800, 4800, 2, 440, RATE);
    printf("  sum 1.5 -> peak %.4f, 440 Hz amplitude %.3f (%.2f dB below the unlimited 1.5)\n", pk, a, db(a / 1.5));
    CHECK(pk <= 0.9886 && a > 0.9, "limited to -0.1 dBFS");
    free(r.buf);
    sa_shutdown(s);
}

/* Stage C: the engine-bridge entry points (sa_init_opts, sa_get_stats_v, sa_instance_set_timeline_ms, sa_file_open/write/close)
 * and the limiter / cache high-water stats */
static float *render_event(sa_system *s, const char *path, int seek_ms, int seek_when, int frames, int *stop_at)
{
    /* seek_when: 0 none, 1 before start, 2 right after start (before the update: RuntimeManager.PlayOneShot) */
    rec r = { 0 };
    sa_handle h = sa_event_create(s, path);
    if (seek_when == 1) sa_instance_set_timeline_ms(s, h, seek_ms);
    sa_instance_start(s, h);
    if (seek_when == 2) sa_instance_set_timeline_ms(s, h, seek_ms);
    *stop_at = -1;
    for (int done = 0; done < frames; done += 256) {
        rec_mix(s, &r, 256, 256, 1);
        if (*stop_at < 0 && sa_instance_get_playback_state(s, h) == SA_STATE_STOPPED) *stop_at = r.frames;
    }
    sa_instance_release(s, h); sa_update(s);
    return r.buf;
}
static void t_bridge(void)
{
    printf("[engine bridge: init by option string, stats vector, timeline seek, incremental file output]\n");
    sa_system *bad = sa_init_opts(fx("manifest.json"), "voices=48,bogus=1", NULL, NULL, NULL);
    CHECK(!bad && strstr(sa_last_error(), "bogus"), "unknown key rejected (%s)", sa_last_error());
    bad = sa_init_opts(fx("manifest.json"), "voices=4x", NULL, NULL, NULL);
    CHECK(!bad && strstr(sa_last_error(), "voices"), "bad value rejected (%s)", sa_last_error());
    sa_system *s = sa_init_opts(fx("manifest.json"), "voices=32,seed=1234,sync_io=1,adpcm_decode_frames=-1,cache_kib=1024,streams=4", NULL, NULL, NULL);
    CHECK(s != NULL, "sa_init_opts (%s)", s ? "ok" : sa_last_error());
    if (!s) return;
    double v[SA_STATV_COUNT + 4];
    int n = sa_get_stats_v(s, v, SA_STATV_COUNT + 4);
    CHECK(n == SA_STATV_COUNT && v[SA_STATV_VOICES_MAX] == 32 && v[SA_STATV_STREAMS_MAX] == 4 && v[SA_STATV_CACHE_BUDGET] == 1048576.0,
          "stats vector: %d values, voices_max %g, streams_max %g, budget %g", n, v[SA_STATV_VOICES_MAX], v[SA_STATV_STREAMS_MAX], v[SA_STATV_CACHE_BUDGET]);
    CHECK(s->cfg.flags & SA_FLAG_SYNC_IO && s->cfg.seed == 1234 && s->cfg.adpcm_decode_max_frames == -1, "options reach the config");
    CHECK(v[SA_STATV_LIMITER_FRAMES] == 0 && v[SA_STATV_LIMITER_MIN_GAIN] == 1.0 && v[SA_STATV_PRE_LIMITER_PEAK] == 0, "limiter stats start clean");
    /* timeline seek: sine440_mono is a 500 ms one-shot (24000 frames, 0.5 amplitude) */
    int st0, st1, st2, st3;
    float *a = render_event(s, "event:/test/sine440_mono", 0, 0, 30000, &st0);
    float *b = render_event(s, "event:/test/sine440_mono", 0, 2, 30000, &st1);
    int same = 1; for (int i = 0; i < 2 * 30000; i++) if (a[i] != b[i]) { same = 0; break; }
    CHECK(same && st0 == st1, "seek to 0 right after start = plain start (bit-identical, STOPPED at %d / %d)", st0, st1);
    float *c = render_event(s, "event:/test/sine440_mono", 250, 2, 30000, &st2);
    float *d = render_event(s, "event:/test/sine440_mono", 250, 1, 30000, &st3);
    /* after the 20 ms fade-in, c[k] == a[k + 12000] */
    double err = 0; for (int k = 1200; k < 11000; k++) { double e = fabs(c[2 * k] - a[2 * (k + 12000)]); if (e > err) err = e; }
    int same_cd = 1; for (int i = 0; i < 2 * 30000; i++) if (c[i] != d[i]) { same_cd = 0; break; }
    printf("  seek 250 ms: STOPPED at frame %d (unseeked %d); max |seeked - original@+12000| after the fade-in %.2e\n", st2, st0, err);
    CHECK(err < 1e-6 && st2 > 0 && abs((st0 - st2) - 12000) <= 256, "seek right after start: plays from 250 ms, STOPPED 250 ms earlier");
    CHECK(same_cd && st2 == st3, "seek before start == seek right after start");
    double head = 0; for (int k = 0; k < 64; k++) head = fabs(c[2 * k]) > head ? fabs(c[2 * k]) : head;
    CHECK(head < 0.05, "mid-take start fades in (first 64 frames peak %.3f)", head);
    free(a); free(b); free(c); free(d);
    /* while playing: the counter loop ([200, 1000)) seeked to 10 ms = frame 480: the timeline reads it back, loops wrap */
    sa_handle h = sa_event_create(s, "event:/test/counter_loop");
    sa_instance_start(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 2048, 256, 1);
    sa_instance_set_timeline_ms(s, h, 10);
    rec_mix(s, &r, 256, 256, 1);   /* update: new layer starts at 480 */
    int ms = -1; sa_instance_get_timeline_ms(s, h, &ms);
    CHECK(ms >= 10 && ms <= 10 + 12, "seek while playing: timeline %d ms after the seek to 10 ms (+ <= one block)", ms);
    /* 1000 ms = frame 48000, past loop_end 1000: 200 + (48000 - 200) % 800 = 800; the update starts it there and 256 frames
     * later it has wrapped once more: 800 + 256 -> 200 + 56 = 256 = 5.33 ms */
    sa_instance_set_timeline_ms(s, h, 1000);
    rec_mix(s, &r, 256, 256, 1);
    sa_instance_get_timeline_ms(s, h, &ms);
    CHECK(ms == 5, "seek past the loop end wraps into the loop (%d ms; expect frame 256 = 5 ms)", ms);
    sa_instance_stop(s, h, 0); sa_instance_release(s, h); sa_update(s);
    free(r.buf);
    sa_shutdown(s);
    /* limiter + cache stats (a fresh system: nothing else in the sum): three 0.5 sines in phase sum to 1.5 */
    s = sa_init_opts(fx("manifest.json"), "seed=1234,sync_io=1,adpcm_decode_frames=-1", NULL, NULL, NULL);
    if (!s) return;
    for (int k = 0; k < 3; k++) { sa_handle q = sa_event_create(s, "event:/test/sine440_mono"); sa_instance_start(s, q); sa_instance_release(s, q); }
    rec r2 = { 0 }; rec_mix(s, &r2, 12000, 256, 1); free(r2.buf);
    n = sa_get_stats_v(s, v, SA_STATV_COUNT);
    printf("  limiter: %g frames turned down, min gain %.4f (expect 0.98855/1.5 = %.4f), pre-limiter peak %.4f; cache peak %g bytes\n",
           v[SA_STATV_LIMITER_FRAMES], v[SA_STATV_LIMITER_MIN_GAIN], 0.98855 / 1.5, v[SA_STATV_PRE_LIMITER_PEAK], v[SA_STATV_CACHE_PEAK_BYTES]);
    CHECK(v[SA_STATV_LIMITER_FRAMES] > 1000 && fabs(v[SA_STATV_LIMITER_MIN_GAIN] - 0.98855 / 1.5) < 0.002 && fabs(v[SA_STATV_PRE_LIMITER_PEAK] - 1.5) < 0.01,
          "limiter stats");
    CHECK(v[SA_STATV_CACHE_PEAK_BYTES] >= 24000 * 2 && v[SA_STATV_CACHE_PEAK_BYTES] >= v[SA_STATV_CACHE_BYTES], "cache high-water mark");
    sa_shutdown(s);
    /* incremental file output == the same calls mixed to memory (float WAV, bit-exact) */
    char path[1200]; snprintf(path, sizeof path, "%s", fx("bridge_out.wav"));   /* a copy: fx() reuses one static buffer */
    sa_system *s1 = sa_init_opts(fx("manifest.json"), "seed=7,sync_io=1", NULL, NULL, NULL);
    sa_system *s2 = sa_init_opts(fx("manifest.json"), "seed=7,sync_io=1", NULL, NULL, NULL);
    CHECK(s1 && s2 && sa_file_open(s1, path, 1) == SA_OK && sa_file_open(s1, path, 1) == SA_ERR_STATE, "file open (a second open is refused)");
    rec m = { 0 };
    for (int f = 0; f < 90; f++) {   /* 90 game frames of 800 output frames, an event every 15 */
        if (f % 15 == 0) { sa_handle x1 = sa_event_create(s1, "event:/test/variations"), x2 = sa_event_create(s2, "event:/test/variations");
                           sa_instance_start(s1, x1); sa_instance_release(s1, x1); sa_instance_start(s2, x2); sa_instance_release(s2, x2); }
        sa_update(s1); sa_update(s2);
        CHECK(sa_file_write(s1, 800) == 800, "file write");
        rec_mix(s2, &m, 800, 800, 0);
    }
    CHECK(sa_file_close(s1) == SA_OK && sa_file_close(s1) == SA_ERR_STATE, "file close (twice: refused)");
    int fr = 0, ch = 0, rt = 0;
    float *w = read_wav(path, &fr, &ch, &rt);
    int bad_px = 0; double pk = 0;
    if (w) for (int i = 0; i < 2 * fr && i < 2 * m.frames; i++) { if (w[i] != m.buf[i]) bad_px++; if (fabs(w[i]) > pk) pk = fabs(w[i]); }
    CHECK(w && fr == 72000 && ch == 2 && rt == 48000 && bad_px == 0 && pk > 0.1, "file output == memory mix (%d frames, %d mismatches, peak %.3f)", fr, bad_px, pk);
    free(w); free(m.buf);
    sa_shutdown(s1); sa_shutdown(s2);
}

static void t_streamed_pcm(void)
{
    printf("[streamed pcm16 (> threshold) through the IO path]\n");
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    int e = sa_event_find(s, "event:/test/big_pcm");
    CHECK(sa_take_streamed(s, s->take_order[s->events[e].first_take]), "15 s stereo pcm16 is streamed");
    sa_handle h = sa_event_create_index(s, e);
    sa_instance_start(s, h); sa_instance_release(s, h);
    rec r = { 0 };
    rec_mix(s, &r, 96000, 512, 2);
    int sf, sch, sr; float *src = read_wav(fx("big_pcm.wav"), &sf, &sch, &sr);
    int first = -1; for (int i = 0; i < r.frames && first < 0; i++) if (fabsf(r.buf[2 * i]) > 1e-6f) first = i;
    int bad = 0;
    for (int i = 0; first >= 0 && i < 90000; i++) for (int c = 0; c < 2; c++) {
        float want = (float)lrintf(src[2 * i + c] * 32768.f) / 32768.f * 0.8f;   /* bus:/Music 0.8 */
        if (fabsf(want - r.buf[2 * (size_t)(first + i) + c]) > 1e-6f) bad++;
    }
    printf("  first audible %d, %d mismatches over 90000 frames\n", first, bad);
    CHECK(first == 0 && bad == 0, "streamed pcm exact");
    free(src); free(r.buf);
    sa_shutdown(s);
}

/* ---------------- threaded stress (also run under TSan) ---------------- */
typedef struct stress { sa_system *s; int quit; int id; long ops; } stress;
#define QUIT(st) SA_LD_ACQ(&(st)->quit)
static void *mix_thread(void *p)
{
    stress *st = (stress *)p; float buf[2 * 256];
    double t0 = sa_now(); long blocks = 0;
    while (!QUIT(st)) {
        sa_mix(st->s, buf, 256); blocks++;
        double ahead = blocks * 256.0 / RATE - (sa_now() - t0);   /* real-time pace (2x) */
        if (ahead > 0) sa_sleep_us((unsigned)(ahead * 1e6 / 2));
    }
    return NULL;
}
static void *update_thread(void *p) { stress *st = (stress *)p; while (!QUIT(st)) { sa_update(st->s); sa_sleep_us(16000); st->ops++; } return NULL; }
static const char *g_evs[] = { "event:/test/sine440_mono", "event:/test/stereo_pcm", "event:/test/counter_loop", "event:/test/adpcm_ms", "event:/test/adpcm_apple",
                               "event:/test/rate24k", "event:/test/param", "event:/test/3d", "event:/test/aac_music", "event:/test/aac_oneshot", "event:/test/short100",
                               "event:/test/variations", "event:/test/bus_sfx", "event:/test/global", "event:/test/big_pcm", "event:/test/takeless", "snapshot:/test/duck",
                               "event:/test/3d_measured", "event:/test/stop_early", "event:/test/stop_late", "event:/test/release", "event:/test/silent_default",
                               "event:/test/silent_oneshot" };
static void *game_thread(void *p)
{
    stress *st = (stress *)p; sa_system *s = st->s;
    uint32_t rng = 0x9E3779B9u * (uint32_t)(st->id + 1);
    sa_handle mine[64]; int n = 0;
    while (!QUIT(st)) {
        uint32_t r = sa_rand(&rng);
        int op = (int)(r % 16);
        if (op < 4 && n < 64) {
            sa_handle h = sa_event_create(s, g_evs[(r >> 8) % (sizeof g_evs / sizeof g_evs[0])]);
            if (h) { sa_instance_start(s, h); if (r & 0x10000) sa_instance_release(s, h); else mine[n++] = h; }
        } else if (op < 8 && n) {
            sa_handle h = mine[(r >> 8) % n];
            sa_vec3 pos = { (float)((int)(r >> 16) % 30 - 15), 0, (float)((int)(r >> 20) % 30 - 15) };
            sa_instance_set_3d(s, h, &pos, NULL); sa_instance_set_param(s, h, "x", (float)((r >> 4) & 1)); sa_instance_set_param(s, h, "power", (float)((r >> 5) & 1));
            sa_instance_set_volume(s, h, ((r >> 12) & 255) / 255.f); sa_instance_set_pitch(s, h, 0.5f + ((r >> 20) & 255) / 255.f);
            sa_instance_get_playback_state(s, h);
        } else if (op < 11 && n) {
            int k = (int)((r >> 8) % n); sa_instance_stop(s, mine[k], (r >> 3) & 1); sa_instance_release(s, mine[k]); mine[k] = mine[--n];
        } else if (op == 11) { sa_bus_set_volume(s, "bus:/SFX", ((r >> 8) & 255) / 255.f); sa_bus_set_paused(s, "bus:/Music", (r >> 17) & 1); sa_bus_set_paused(s, "bus:/SFX", ((r >> 18) & 3) == 3); }
        else if (op == 12) { sa_set_global_param(s, "Global1", (float)((r >> 9) & 1)); }
        else if (op == 13) { sa_vec3 lp = { (float)((r >> 8) % 10), 0, 0 }; sa_set_listener(s, &lp, NULL, NULL, NULL); }
        else if (op == 14) { sa_notification nn[16]; sa_poll(s, nn, 16); }
        else { sa_stats ss; sa_get_stats(s, &ss); }
        st->ops++;
        if ((r & 7) == 0) sa_sleep_us(200);
    }
    for (int k = 0; k < n; k++) { sa_instance_stop(s, mine[k], 0); sa_instance_release(s, mine[k]); }
    return NULL;
}
static void t_stress(double seconds)
{
    printf("[thread stress: mixer thread + IO thread + update thread + 3 game threads, %.1f s]\n", seconds);
    sa_config c; sa_config_defaults(&c); c.max_voices = 24; c.seed = 99; c.max_streams = 4;
    sa_system *s = sa_init_ex(fx("manifest.json"), &c);
    if (!s) { CHECK(0, "init: %s", sa_last_error()); return; }
    s->out_thread = 1;
    sa_set_poll_mask(s, SA_CB_STOPPED | SA_CB_DESTROYED);
    stress st[5]; pthread_t th[5];
    for (int k = 0; k < 5; k++) { memset(&st[k], 0, sizeof st[k]); st[k].s = s; st[k].id = k; }
    pthread_create(&th[0], NULL, mix_thread, &st[0]);
    pthread_create(&th[1], NULL, update_thread, &st[1]);
    for (int k = 2; k < 5; k++) pthread_create(&th[k], NULL, game_thread, &st[k]);
    sa_sleep_us((unsigned)(seconds * 1e6));
    for (int k = 2; k < 5; k++) { SA_ST_REL(&st[k].quit, 1); pthread_join(th[k], NULL); }
    SA_ST_REL(&st[1].quit, 1); pthread_join(th[1], NULL);
    /* drain: release everything still live (released-but-playing loops need a stop) */
    for (int it = 0; it < 400; it++) {
        sa_mutex_lock(&s->api);
        sa_handle hs[4096]; int n = 0;
        for (int k = s->live_head; k >= 0 && n < 4096; k = s->inst[k].next_live) hs[n++] = (s->inst[k].gen << 12) | (uint32_t)k;
        sa_mutex_unlock(&s->api);
        for (int k = 0; k < n; k++) { sa_instance_stop(s, hs[k], 0); sa_instance_release(s, hs[k]); }
        sa_update(s); sa_sleep_us(3000);
        sa_stats ss; sa_get_stats(s, &ss);
        if (ss.instances_live == 0 && ss.voices_active == 0 && ss.streams_active == 0 && ss.voices_tail == 0) break;
    }
    sa_sleep_us(30000);
    SA_ST_REL(&st[0].quit, 1); pthread_join(th[0], NULL);
    sa_stats ss; sa_get_stats(s, &ss);
    long ops = st[2].ops + st[3].ops + st[4].ops;
    int refs = 0; for (int t = 0; t < s->ntakes; t++) refs += s->entries[t].refs;
    printf("  %ld API ops, %ld updates, %llu frames mixed; steals %u, virtualized %u, underruns %u, cmd peak %u, dropped %u, load failures %u\n",
           ops, st[1].ops, (unsigned long long)ss.mixed_frames, ss.steals, ss.virtualized, ss.underruns, ss.cmd_queue_peak, ss.cmd_dropped, ss.load_failures);
    printf("  after drain: live %d, voices %d, tails %d, streams %d, entry refs %d, cache %llu bytes\n",
           ss.instances_live, ss.voices_active, ss.voices_tail, ss.streams_active, refs, (unsigned long long)ss.cache_bytes);
    CHECK(ss.instances_live == 0 && ss.voices_active == 0 && ss.streams_active == 0 && refs == 0, "clean drain");
    CHECK(ss.cmd_dropped == 0, "no dropped commands");
    CHECK(ss.load_failures <= 1000000, "loads ok");
    sa_shutdown(s);
}

/* ---------------- CPU bench ---------------- */
static double g_empty_ns_per_frame = 0;
static double bench_case(const char *name, const char *ev, float pitch, int n, int adpcm_decode, int quick)
{
    g_adpcm_decode = adpcm_decode;
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    g_adpcm_decode = -1;
    if (!s) return 0;
    float buf[2 * 512];
    sa_handle hs[48];
    for (int k = 0; k < n; k++) { hs[k] = sa_event_create(s, ev); sa_instance_set_pitch(s, hs[k], pitch); sa_instance_set_volume(s, hs[k], 0.02f); sa_instance_start(s, hs[k]); }
    for (int k = 0; k < 8; k++) { sa_update(s); sa_mix(s, buf, 512); }
    sa_stats st; sa_get_stats(s, &st);
    int frames = quick ? 48000 * 5 : 48000 * 20;
    sa_set_timing(s, 1);
    s->mix_seconds = s->io_seconds = 0;
    int upd = 0;
    for (int done = 0; done < frames; done += 256) { if ((upd++ & 3) == 0) sa_update(s); sa_mix(s, buf, 256); }
    double t = s->mix_seconds - s->io_seconds;   /* mixer only: SYNC_IO stream decode (AudioToolbox) excluded */
    sa_get_stats(s, &st);
    double nsvf = (t * 1e9 / frames - g_empty_ns_per_frame) / n;   /* marginal cost per voice (fixed per-block cost removed) */
    double pct = nsvf * 48000 / 1e9 * 100;
    printf("  %-60s voices %2d: %5.2f ns/voice/frame = %.4f%% of a core/voice; A9 (x43): %6.1f ns, %.2f%%/voice, 48 voices %4.1f%%\n",
           name, st.voices_active, nsvf, pct, nsvf * 43, pct * 43, pct * 43 * 48);
    sa_shutdown(s);
    return nsvf;
}
static void bench(void)
{
    printf("[CPU bench: mixer cost per voice, clang -O2, Apple M4 Pro; Vita estimate = x43 (CoreMark ratio, PLAN.md)]\n");
    {
        sa_system *s0 = open_sys(48, SA_FLAG_SYNC_IO); float b0[512];
        for (int k = 0; k < 2000; k++) sa_mix(s0, b0, 256);
        double t0 = now_s(); for (int k = 0; k < 40000; k++) sa_mix(s0, b0, 256);
        g_empty_ns_per_frame = (now_s() - t0) * 1e9 / (40000.0 * 256);
        sa_shutdown(s0);
        printf("  (fixed per-frame cost of an empty mix, subtracted below: %.2f ns/frame)\n", g_empty_ns_per_frame);
    }
    bench_case("mono pcm16, step 1.0 (NEON fast path)", "event:/test/bus_sfx", 1.0f, 48, -1, g_quick);
    bench_case("mono pcm16, pitch 1.01 (linear interpolation)", "event:/test/bus_sfx", 1.01f, 48, -1, g_quick);
    bench_case("mono pcm16, pitch 0.5 (interpolation, upsampling)", "event:/test/bus_sfx", 0.5f, 48, -1, g_quick);
    bench_case("stereo pcm16 44.1k -> 48k (interpolation)", "event:/test/rate44k_loop", 1.0f, 48, -1, g_quick);
    bench_case("stereo ima4 (CAF), step 1.0, decoded per voice", "event:/test/ima4_loop", 1.0f, 48, -1, g_quick);
    bench_case("stereo ima4, step 1.0, decoded to pcm16 at load", "event:/test/ima4_loop", 1.0f, 48, 96000, g_quick);
    bench_case("mono MS-IMA, pitch 1.01, decoded per voice", "event:/test/adpcm_loop", 1.01f, 48, -1, g_quick);
    bench_case("stereo aac stream x8, step 1.0 (ring read; decode excluded)", "event:/test/aac_music", 1.0f, 8, -1, g_quick);
    sa_system *s = open_sys(48, SA_FLAG_SYNC_IO);
    if (!s) return;
    float buf[2 * 512];
    double t0 = now_s();
    for (int k = 0; k < 20000; k++) sa_mix(s, buf, 256);
    double tm = (now_s() - t0) / 20000;
    sa_handle hs[300];
    for (int k = 0; k < 300; k++) hs[k] = sa_event_create(s, "event:/test/hold_noloop");
    t0 = now_s();
    for (int k = 0; k < 20000; k++) sa_update(s);
    double tu = (now_s() - t0) / 20000;
    printf("  fixed: empty sa_mix(256 frames) %.2f us (A9 ~%.0f us per 5.33 ms grain); sa_update, 300 idle instances %.2f us (A9 ~%.2f ms/frame)\n",
           tm * 1e6, tm * 1e6 * 43, tu * 1e6, tu * 1e3 * 43);
    for (int k = 0; k < 300; k++) sa_instance_release(s, hs[k]);
    sa_update(s);
    const sa_aac_vtbl *a = sa_platform_aac();
    size_t len; char *bufa = sa_read_file(fx("music.aac"), &len);
    void *dec = a->open(2, 48000); int16_t pcm[4096 * 2];
    t0 = now_s(); int fr = 0; size_t off = 0;
    while (off + 7 <= len) { sa_adts h; if (sa_adts_parse((const uint8_t *)bufa + off, (int)(len - off), &h) != 1) break; a->decode(dec, (const uint8_t *)bufa + off, h.frame_len, pcm, 4096); off += (size_t)h.frame_len; fr++; }
    double t = now_s() - t0;
    printf("  AAC-LC stereo decode on the Mac (AudioToolbox): %.1f us/frame = %.3f%% of a core per stream (the Vita decodes in hardware)\n", t * 1e6 / fr, t / (fr * 1024.0 / 48000) * 100);
    a->close(dec); free(bufa);
    sa_shutdown(s);
}

/* kit3 play mode (vita/notes/play-mode.md): sa_rt_out, the Mac stand-in for the Vita's sceAudioOut thread, plays at the output rate
 * whatever the game's update rate. A "6 fps game" (one sa_update every 166 ms) still gets every take contiguous and sample-exact at
 * 48 kHz of wall time: a RAM take (the PCM counter loop: exact integers) and an AAC stream (IO thread + ring, as on the Vita). */
static void rt_run(sa_system *s, const char *path, double secs, double *st, int *updates)
{
    double t0 = sa_now(); *updates = 0;
    while (sa_now() - t0 < secs) { sa_update(s); (*updates)++; sa_sleep_us(166000); }
    sa_rt_out_stop(s, st);
}
static void t_rt_out(void)
{
    printf("[rt output: the hardware's pace, not the game's (kit3 play mode)]\n");
    sa_system *sy = open_sys(48, SA_FLAG_SYNC_IO);
    if (sy) { CHECK(sa_rt_out_start(sy, fx("rt_refused.wav"), 512, 1) == SA_ERR_STATE, "SYNC_IO system refused (the IO thread must service streams)"); sa_shutdown(sy); }
    sa_system *s = open_sys(48, 0);   /* IO thread, as on the Vita */
    if (!s) return;
    char w1[1100], w2[1100]; snprintf(w1, sizeof w1, "%s", fx("rt_counter.wav")); snprintf(w2, sizeof w2, "%s", fx("rt_aac.wav"));
    CHECK(sa_rt_out_start(s, w1, 512, 1) == SA_OK, "rt output starts");
    CHECK(sa_rt_out_start(s, w1, 512, 1) == SA_ERR_STATE, "a second rt output is refused");
    sa_handle h = sa_event_create(s, "event:/test/counter_loop");
    sa_instance_start(s, h);
    double st[6] = { 0 }; int ups = 0;
    rt_run(s, w1, 1.5, st, &ups);
    int fr = 0, ch = 0; float *x = read_wav(w1, &fr, &ch, NULL);
    int16_t *cnt = gen_counter(1000);
    int on = -1, bad = 0, n = 0;
    for (int i = 0; x && i < fr && on < 0; i++) if (x[2 * i] != 0.f) on = i;
    for (int i = 0; on >= 0 && on + i < fr; i++, n++) {
        int src = i < 1000 ? i : 200 + (i - 1000) % 800;
        if ((int)lrintf(x[2 * (on + i)] * 32768.f) != cnt[src] || (int)lrintf(x[2 * (on + i) + 1] * 32768.f) != cnt[src]) bad++;
    }
    printf("  counter loop, %d updates at 6 fps: %.0f frames = %.3f s of audio in %.3f s of wall time (x%.4f), worst lateness %.2f ms, %.0f late grains; "
           "onset at frame %d, %d frames after it, %d differ\n", ups, st[0], st[1], st[2], st[3], st[4] * 1000, st[5], on, n, bad);
    CHECK(fr == (int)st[0] && st[3] > 0.98 && st[3] < 1.02, "audio advances at the output rate: x%.4f of wall time", st[3]);
    /* a voice starts at the first update that finds its data ready: the take loads on the IO thread after the first one (as on the
     * Vita, vita/notes/m1-audio.md 10.2), so the onset is within two updates (2 x 166 ms) + a grain */
    CHECK(on >= 0 && on < 2 * 7968 + 512, "starts within two updates of the start (onset frame %d)", on);
    CHECK(n > 60000 && bad == 0, "the take is contiguous and sample-exact over %d frames at 6 updates/s (%d differ)", n, bad);
    CHECK(st[5] <= 2, "the mixer kept up with real time (%.0f late grains)", st[5]);
    free(x); free(cnt);
    sa_instance_stop(s, h, 0); sa_instance_release(s, h);
    for (int k = 0; k < 4; k++) { sa_update(s); float tmp[512]; sa_mix(s, tmp, 256); }
    /* the AAC stream through the IO thread's ring */
    const sa_aac_vtbl *a = sa_platform_aac();
    size_t len; char *buf = sa_read_file(fx("music.aac"), &len);
    void *dec = a->open(2, 48000);
    int16_t *lin = (int16_t *)malloc(sizeof(int16_t) * 2 * 200000); int tot = 0; size_t off = 0;
    while (off + 7 <= len && tot < 190000) {
        sa_adts hh; if (sa_adts_parse((const uint8_t *)buf + off, (int)(len - off), &hh) != 1) break;
        int k = a->decode(dec, (const uint8_t *)buf + off, hh.frame_len, lin + 2 * (size_t)tot, 4096); if (k < 0) break; tot += k; off += (size_t)hh.frame_len;
    }
    a->close(dec); free(buf);
    CHECK(sa_rt_out_start(s, w2, 512, 1) == SA_OK, "rt output restarts");
    sa_handle h2 = sa_event_create(s, "event:/test/aac_music");
    sa_instance_start(s, h2);
    rt_run(s, w2, 1.5, st, &ups);
    x = read_wav(w2, &fr, &ch, NULL);
    on = -1; for (int i = 0; x && i < fr && on < 0; i++) if (fabsf(x[2 * i]) > 1e-5f) on = i;
    double maxd = 0; n = 0;
    int pr = SA_AAC_PRIMING_DEFAULT;
    for (int i = 0; on >= 0 && on + i < fr && i + pr < tot; i++, n++)   /* (no loop in 1.5 s: the loop end is 384000) */
        for (int c = 0; c < 2; c++) { double d = fabs(lin[2 * (size_t)(i + pr) + c] / 32768.0 * 0.8 - x[2 * (size_t)(on + i) + c]); if (d > maxd) maxd = d; }
    sa_stats ss; sa_get_stats(s, &ss);
    printf("  aac stream, %d updates at 6 fps: %.3f s of audio in %.3f s of wall time (x%.4f); onset %d, %d frames vs the linear decode: max |d| %.1f LSB; underruns %u\n",
           ups, st[1], st[2], st[3], on, n, maxd * 32768, ss.underruns);
    CHECK(st[3] > 0.98 && st[3] < 1.02, "stream audio advances at the output rate: x%.4f", st[3]);
    CHECK(n > 60000 && maxd * 32768 < 2.5, "the stream is contiguous and = the linear decode over %d frames (max %.2f LSB)", n, maxd * 32768);
    CHECK(ss.underruns == 0, "no stream underruns at 6 updates/s (%u)", ss.underruns);
    free(x); free(lin);
    sa_instance_stop(s, h2, 0); sa_instance_release(s, h2);
    for (int k = 0; k < 4; k++) { sa_update(s); float tmp[512]; sa_mix(s, tmp, 256); }
    sa_shutdown(s);
    remove(w1); remove(w2);
}

int main(int argc, char **argv)
{
    const char *mode = "all";
    const char *here = getenv("SA_FIXTURES");
    snprintf(D, sizeof D, "%s", here ? here : "build/fixtures");
    for (int k = 1; k < argc; k++) {
        if (!strcmp(argv[k], "--dir") && k + 1 < argc) snprintf(D, sizeof D, "%s", argv[++k]);
        else if (!strcmp(argv[k], "--quick")) g_quick = 1;
        else mode = argv[k];
    }
    setvbuf(stdout, NULL, _IOLBF, 0);
    printf("shepaudio %x tests, fixtures in %s\n", sa_version(), D);
    if (make_fixtures()) { printf("fixture generation failed\n"); return 2; }
    int all = !strcmp(mode, "all");
    if (all || !strcmp(mode, "unit")) {
        t_json(); t_manifest(); t_decoders(); t_aac(); t_resampler(); t_loops(); t_pan3d(); t_param(); t_stopped(); t_stopframe(); t_release(); t_silent_group(); t_callbacks();
        t_variations(); t_stealing(); t_buses(); t_limiter(); t_streamed_pcm(); t_bridge(); t_rt_out();
    }
    if (all || !strcmp(mode, "stress")) t_stress(g_quick ? 2.0 : 6.0);
    if (all || !strcmp(mode, "bench")) bench();
    printf("\n%d checks passed, %d failed\n", g_pass, g_fail);
    return g_fail ? 1 : 0;
}
