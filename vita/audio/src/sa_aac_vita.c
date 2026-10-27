/* PS Vita: AAC-LC (ADTS) hardware decode through sceAudiodec. One decoder per stream; the library allows
 * SCE_AUDIODEC_AAC_MAX_STREAMS (8) at once. Buffers are SCE_AUDIODEC_ALIGNMENT_SIZE (256-byte) aligned.
 * Called only from the IO thread (blocking: the codec engine call returns when the frame is decoded). */
#include "sa_internal.h"
#include <psp2/audiodec.h>
#include <malloc.h>

typedef struct vita_dec {
    SceAudiodecCtrl ctrl;
    SceAudiodecInfo info;
    uint8_t *es; uint32_t es_cap;
    int16_t *pcm; uint32_t pcm_cap;
    int channels, rate;
} vita_dec;

static int g_lib_ok;

static int v_init(int max_streams)
{
    if (g_lib_ok) return 0;
    SceAudiodecInitParam ip;
    memset(&ip, 0, sizeof ip);
    ip.size = sizeof ip.aac;
    ip.aac.totalStreams = (SceUInt32)(max_streams < 1 ? 1 : (max_streams > SCE_AUDIODEC_AAC_MAX_STREAMS ? SCE_AUDIODEC_AAC_MAX_STREAMS : max_streams));
    int r = sceAudiodecInitLibrary(SCE_AUDIODEC_TYPE_AAC, &ip);
    if (r < 0 && (unsigned)r != (unsigned)SCE_AUDIODEC_ERROR_ALREADY_INITIALIZED) return r;
    g_lib_ok = 1;
    return 0;
}
static void v_term(void) { if (g_lib_ok) { sceAudiodecTermLibrary(SCE_AUDIODEC_TYPE_AAC); g_lib_ok = 0; } }

static void *v_open(int channels, int rate)
{
    vita_dec *d = (vita_dec *)calloc(1, sizeof *d);
    if (!d) return NULL;
    d->channels = channels; d->rate = rate;
    d->ctrl.size = sizeof d->ctrl;
    d->ctrl.wordLength = SCE_AUDIODEC_WORD_LENGTH_16BITS;
    d->ctrl.pInfo = &d->info;
    d->info.size = sizeof d->info.aac;
    d->info.aac.isAdts = 1;
    d->info.aac.ch = (SceUInt32)channels;
    d->info.aac.samplingRate = (SceUInt32)rate;
    d->info.aac.isSbr = 0;
    if (sceAudiodecCreateDecoder(&d->ctrl, SCE_AUDIODEC_TYPE_AAC) < 0) { free(d); return NULL; }
    uint32_t es = d->ctrl.maxEsSize > SCE_AUDIODEC_AAC_MAX_ES_SIZE ? d->ctrl.maxEsSize : SCE_AUDIODEC_AAC_MAX_ES_SIZE;
    uint32_t pcm = d->ctrl.maxPcmSize > (uint32_t)(SCE_AUDIODEC_AAC_MAX_SAMPLES * 2 * 2) ? d->ctrl.maxPcmSize : (uint32_t)(SCE_AUDIODEC_AAC_MAX_SAMPLES * 2 * 2);
    d->es_cap = SCE_AUDIODEC_ROUND_UP(es);
    d->pcm_cap = SCE_AUDIODEC_ROUND_UP(pcm);
    d->es = (uint8_t *)memalign(SCE_AUDIODEC_ALIGNMENT_SIZE, d->es_cap);
    d->pcm = (int16_t *)memalign(SCE_AUDIODEC_ALIGNMENT_SIZE, d->pcm_cap);
    if (!d->es || !d->pcm) { sceAudiodecDeleteDecoder(&d->ctrl); free(d->es); free(d->pcm); free(d); return NULL; }
    return d;
}

static int v_decode(void *dv, const uint8_t *frame, int len, int16_t *out, int max_frames)
{
    vita_dec *d = (vita_dec *)dv;
    if (len <= 0 || (uint32_t)len > d->es_cap) return -1;
    memcpy(d->es, frame, (size_t)len);
    if ((uint32_t)len < d->es_cap) memset(d->es + len, 0, d->es_cap - (uint32_t)len);
    d->ctrl.pEs = d->es;
    d->ctrl.maxEsSize = d->es_cap;
    d->ctrl.pPcm = d->pcm;
    d->ctrl.maxPcmSize = d->pcm_cap;
    d->ctrl.inputEsSize = 0;
    d->ctrl.outputPcmSize = 0;
    if (sceAudiodecDecode(&d->ctrl) < 0) return -1;
    int frames = (int)(d->ctrl.outputPcmSize / (2u * (uint32_t)d->channels));
    if (frames > max_frames) frames = max_frames;
    memcpy(out, d->pcm, (size_t)frames * 2u * (size_t)d->channels);
    return frames;
}
static void v_reset(void *dv) { vita_dec *d = (vita_dec *)dv; sceAudiodecClearContext(&d->ctrl); }
static void v_close(void *dv)
{
    vita_dec *d = (vita_dec *)dv;
    if (!d) return;
    sceAudiodecDeleteDecoder(&d->ctrl);
    free(d->es); free(d->pcm); free(d);
}

static const sa_aac_vtbl vita_vtbl = { "sceAudiodec (AAC, hardware)", v_init, v_term, v_open, v_decode, v_reset, v_close };
const sa_aac_vtbl *sa_platform_aac(void) { return &vita_vtbl; }
