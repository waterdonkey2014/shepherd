/* Short-take cache: one entry per take, loaded by the IO thread (or inline in SYNC mode), LRU-evicted by the game
 * thread under a byte budget. PCM16 takes are stored as native int16; IMA ADPCM takes stay compressed (decoded by the
 * mixer per voice), so the budget holds ~4x more ADPCM audio. An entry is never evicted while a voice reads it
 * (refs, incremented by the game before START, decremented by the mixer when the voice or its tail ends). */
#include "sa_internal.h"

void *sa_alloc(sa_system *sys, size_t bytes, size_t align)
{
    if (sys->cfg.alloc) return sys->cfg.alloc(bytes, align, sys->cfg.alloc_user);
    (void)align;
    return malloc(bytes);
}
void sa_free(sa_system *sys, void *p)
{
    if (!p) return;
    if (sys->cfg.free) sys->cfg.free(p, sys->cfg.alloc_user); else free(p);
}

int sa_cache_init(sa_system *sys)
{
    int n = sys->ntakes ? sys->ntakes : 1;
    sys->entries = (sa_entry *)calloc((size_t)n, sizeof(sa_entry));
    sys->loadq = (int *)malloc(sizeof(int) * (size_t)(n + 1));
    sys->doneq = (int *)malloc(sizeof(int) * (size_t)(n + 1));
    sys->seek = (struct sa_seekidx **)calloc((size_t)n, sizeof(void *));
    if (!sys->entries || !sys->loadq || !sys->doneq || !sys->seek) return SA_ERR_NO_MEMORY;
    sys->loadq_cap = n + 1;
    for (int i = 0; i < n; i++) { sys->entries[i].lru_prev = sys->entries[i].lru_next = -1; }
    sys->lru_head = sys->lru_tail = -1;
    if (sa_mutex_init(&sys->loadq_lock, "shepaudio_loadq")) return SA_ERR_NO_MEMORY;
    return SA_OK;
}
void sa_cache_free(sa_system *sys)
{
    if (sys->entries) for (int i = 0; i < sys->ntakes; i++) sa_free(sys, sys->entries[i].data);
    if (sys->seek) for (int i = 0; i < sys->ntakes; i++) free(sys->seek[i]);
    free(sys->entries); free(sys->loadq); free(sys->doneq); free(sys->seek);
    sa_mutex_destroy(&sys->loadq_lock);
}

int sa_take_streamed(sa_system *sys, int t)
{
    const sa_take *tk = &sys->takes[t];
    if (tk->codec == SA_CODEC_AAC) return 1;
    size_t bytes = tk->codec == SA_CODEC_ADPCM ? (size_t)tk->frames * (size_t)tk->channels / 2u : (size_t)tk->frames * 2u * (size_t)tk->channels;
    return bytes > sys->cfg.stream_threshold_bytes;
}

static void lru_unlink(sa_system *sys, int i)
{
    sa_entry *e = &sys->entries[i];
    if (!e->in_lru) return;
    if (e->lru_prev >= 0) sys->entries[e->lru_prev].lru_next = e->lru_next; else sys->lru_head = e->lru_next;
    if (e->lru_next >= 0) sys->entries[e->lru_next].lru_prev = e->lru_prev; else sys->lru_tail = e->lru_prev;
    e->lru_prev = e->lru_next = -1; e->in_lru = 0;
}
static void lru_push_mru(sa_system *sys, int i)
{
    sa_entry *e = &sys->entries[i];
    e->lru_prev = sys->lru_tail; e->lru_next = -1;
    if (sys->lru_tail >= 0) sys->entries[sys->lru_tail].lru_next = i; else sys->lru_head = i;
    sys->lru_tail = i; e->in_lru = 1;
}

void sa_cache_request(sa_system *sys, int t)
{
    sa_entry *e = &sys->entries[t];
    if (SA_LD_ACQ(&e->state) != SA_E_EMPTY) return;
    SA_ST_REL(&e->state, SA_E_QUEUED);
    sa_mutex_lock(&sys->loadq_lock);
    sys->loadq[sys->loadq_tail] = t;
    sys->loadq_tail = (sys->loadq_tail + 1) % sys->loadq_cap;
    sa_mutex_unlock(&sys->loadq_lock);
}

void sa_cache_touch(sa_system *sys, int t)
{
    sa_entry *e = &sys->entries[t];
    if (SA_LD_ACQ(&e->state) != SA_E_READY) return;
    lru_unlink(sys, t);
    lru_push_mru(sys, t);
}

/* game: link newly loaded entries, then evict LRU entries nobody reads until under budget */
void sa_cache_evict(sa_system *sys)
{
    sa_mutex_lock(&sys->loadq_lock);
    while (sys->doneq_head != sys->doneq_tail) {
        int t = sys->doneq[sys->doneq_head];
        sys->doneq_head = (sys->doneq_head + 1) % sys->loadq_cap;
        if (SA_LD_ACQ(&sys->entries[t].state) == SA_E_READY && !sys->entries[t].in_lru) lru_push_mru(sys, t);
    }
    sa_mutex_unlock(&sys->loadq_lock);
    size_t budget = sys->cfg.cache_budget_bytes;
    int i = sys->lru_head;
    while (i >= 0 && SA_LD_ACQ(&sys->cache_bytes) > budget) {
        sa_entry *e = &sys->entries[i];
        int next = e->lru_next;
        if (SA_LD_ACQ(&e->refs) == 0 && e->pins == 0) {
            lru_unlink(sys, i);
            void *d = e->data; size_t b = e->bytes;
            e->data = NULL; e->bytes = 0;
            SA_ST_REL(&e->state, SA_E_EMPTY);
            sa_free(sys, d);
            SA_SUB(&sys->cache_bytes, b);
            sys->st_evict++;
        }
        i = next;
    }
}

static int16_t f2s(float x)
{
    float v = x * 32768.f;
    if (v >= 32767.f) return 32767;
    if (v <= -32768.f) return -32768;
    return (int16_t)lrintf(v);
}

/* IO thread (or inline): read + convert one take. */
int sa_cache_load_now(sa_system *sys, int t)
{
    sa_entry *e = &sys->entries[t];
    const sa_take *tk = &sys->takes[t];
    SA_ST_REL(&e->state, SA_E_LOADING);
    char err[160];
    sa_fileinfo fi;
    void *data = NULL; size_t bytes = 0;
    FILE *f = fopen(tk->file, "rb");
    if (!f) goto fail;
    if (sa_probe(f, &fi, err, sizeof err) != SA_OK) goto fail;
    if (fi.fmt == SA_FMT_NONE) {   /* headerless: int16 LE per the manifest */
        if (tk->codec != SA_CODEC_PCM16) goto fail;
        fi.fmt = SA_FMT_PCM16LE; fi.channels = tk->channels; fi.rate = tk->rate; fi.frames = fi.data_bytes / (2u * (uint32_t)tk->channels);
    }
    uint32_t frames = fi.frames;
    if (tk->frames && tk->frames < frames) frames = tk->frames;
    if (fi.fmt == SA_FMT_IMA_MS || fi.fmt == SA_FMT_IMA_APPLE) {
        uint32_t nb = (frames + (uint32_t)fi.frames_per_block - 1) / (uint32_t)fi.frames_per_block;
        uint32_t maxb = fi.data_bytes / (uint32_t)fi.block_bytes;
        if (nb > maxb) nb = maxb;
        bytes = (size_t)nb * (size_t)fi.block_bytes;
        data = sa_alloc(sys, bytes ? bytes : 1, 16);
        if (!data) goto fail;
        fseek(f, fi.data_offset, SEEK_SET);
        if (fread(data, 1, bytes, f) != bytes) goto fail;
        e->block_bytes = fi.block_bytes; e->fpb = fi.frames_per_block; e->nblocks = nb;
        if ((uint64_t)nb * (uint64_t)fi.frames_per_block < frames) frames = nb * (uint32_t)fi.frames_per_block;
        if (sys->cfg.adpcm_decode_max_frames > 0 && frames <= (uint32_t)sys->cfg.adpcm_decode_max_frames) {
            /* short take: decode once here (IO thread) so the mixer takes the cheap pcm16 path */
            size_t pbytes = (size_t)frames * (size_t)fi.channels * 2u;
            int16_t *pcm = (int16_t *)sa_alloc(sys, pbytes ? pbytes : 2, 16);
            if (!pcm) goto fail;
            sa_adpcm_cur c; memset(&c, 0, sizeof c);
            int got = sa_adpcm_decode(fi.fmt, (const uint8_t *)data, nb, fi.block_bytes, fi.frames_per_block, fi.channels, &c, pcm, (int)frames);
            if (got < (int)frames) frames = got > 0 ? (uint32_t)got : 0;
            sa_free(sys, data);
            data = pcm; bytes = pbytes;
            fi.fmt = SA_FMT_PCM16LE;
        }
    } else if (fi.fmt == SA_FMT_PCM16LE || fi.fmt == SA_FMT_PCM16BE || fi.fmt == SA_FMT_F32LE || fi.fmt == SA_FMT_F32BE) {
        size_t samples = (size_t)frames * (size_t)fi.channels;
        bytes = samples * 2u;
        data = sa_alloc(sys, bytes ? bytes : 1, 16);
        if (!data) goto fail;
        fseek(f, fi.data_offset, SEEK_SET);
        int16_t *d = (int16_t *)data;
        if (fi.fmt == SA_FMT_PCM16LE || fi.fmt == SA_FMT_PCM16BE) {
            if (fread(d, 2, samples, f) != samples) goto fail;
            int be = fi.fmt == SA_FMT_PCM16BE;
            const uint16_t one = 1; int host_le = *(const uint8_t *)&one == 1;
            if (be == host_le) for (size_t i = 0; i < samples; i++) { uint16_t u = (uint16_t)d[i]; d[i] = (int16_t)((u >> 8) | (u << 8)); }
        } else {
            uint8_t buf[4096];
            size_t done = 0;
            while (done < samples) {
                size_t k = samples - done; if (k > sizeof buf / 4) k = sizeof buf / 4;
                if (fread(buf, 4, k, f) != k) goto fail;
                for (size_t i = 0; i < k; i++) {
                    const uint8_t *p = buf + 4 * i; uint32_t u;
                    if (fi.fmt == SA_FMT_F32LE) u = (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
                    else u = ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3];
                    float x; memcpy(&x, &u, 4);
                    d[done + i] = f2s(x);
                }
                done += k;
            }
        }
        fi.fmt = SA_FMT_PCM16LE;  /* stored as native int16 */
    } else goto fail;            /* ADTS is never cached */
    fclose(f);
    e->fmt = fi.fmt; e->channels = fi.channels; e->rate = fi.rate; e->frames = frames;
    e->data = data; e->bytes = bytes;
    {   /* Stage C: the high-water mark (only the loading thread adds) */
        size_t now_bytes = SA_ADD(&sys->cache_bytes, bytes);
        if (now_bytes > SA_LD_RLX(&sys->st_cache_peak)) SA_ST_RLX(&sys->st_cache_peak, now_bytes);
    }
    SA_ST_REL(&e->state, SA_E_READY);
    goto queue_done;
fail:
    if (getenv("SA_DEBUG")) fprintf(stderr, "shepaudio: load failed: %s (state %d, f=%p)\n", tk->file, SA_LD_RLX(&e->state), (void *)f);
    if (f) fclose(f);
    sa_free(sys, data);
    SA_ADD(&sys->st_loadfail, 1u);
    SA_ST_REL(&e->state, SA_E_FAILED);
queue_done:
    sa_mutex_lock(&sys->loadq_lock);
    sys->doneq[sys->doneq_tail] = t;
    sys->doneq_tail = (sys->doneq_tail + 1) % sys->loadq_cap;
    sa_mutex_unlock(&sys->loadq_lock);
    return SA_LD_ACQ(&e->state) == SA_E_READY ? SA_OK : SA_ERR_FILE;
}

void sa_io_service_loads(sa_system *sys)
{
    for (;;) {
        int t = -1;
        sa_mutex_lock(&sys->loadq_lock);
        if (sys->loadq_head != sys->loadq_tail) { t = sys->loadq[sys->loadq_head]; sys->loadq_head = (sys->loadq_head + 1) % sys->loadq_cap; }
        sa_mutex_unlock(&sys->loadq_lock);
        if (t < 0) break;
        if (SA_LD_ACQ(&sys->entries[t].state) == SA_E_QUEUED) sa_cache_load_now(sys, t);
    }
}
