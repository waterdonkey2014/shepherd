/* Streamed takes (aac-adts always; big pcm16/adpcm): the IO thread reads the file, decodes, unrolls loops and writes
 * int16 frames into a per-stream SPSC ring; the mixer only reads the ring. AAC seeking (start offsets, loop wrap)
 * re-decodes from SA_AAC_PREROLL ADTS frames before the target and drops the overlap, so loop seams are
 * sample-accurate. ADTS frame offsets: the loop target is recorded while decoding past it; other seeks use a lazily
 * built per-take index (every 8th frame). */
#include "sa_internal.h"

#define SA_AAC_PREROLL 2
#define SA_INBUF 32768
#define SA_DECBUF 2048          /* frames (AAC-LC = 1024/frame) */
#define SA_PREFILL 4096         /* frames before a stream voice may start */
#define SA_IDX_STEP 8

struct sa_seekidx { uint32_t n; uint32_t off[1]; };

int sa_streams_init(sa_system *sys)
{
    int n = sys->cfg.max_streams;
    uint32_t cap = 1024; while (cap < (uint32_t)sys->cfg.stream_buffer_frames) cap <<= 1;
    sys->streams = (sa_stream *)calloc((size_t)(n ? n : 1), sizeof(sa_stream));
    if (!sys->streams) return SA_ERR_NO_MEMORY;
    sys->nstreams = n;
    for (int i = 0; i < n; i++) {
        sa_stream *s = &sys->streams[i];
        s->cap = cap; s->mask = cap - 1;
        s->ring = (int16_t *)sa_alloc(sys, (size_t)cap * 2u * sizeof(int16_t), 16);
        s->inbuf = (uint8_t *)malloc(SA_INBUF);
        s->dec = (int16_t *)malloc(SA_DECBUF * 2 * sizeof(int16_t) * 2);
        if (!s->ring || !s->inbuf || !s->dec) return SA_ERR_NO_MEMORY;
    }
    return SA_OK;
}

static void stream_close_io(sa_system *sys, sa_stream *s)
{
    if (s->aac && sys->aac) sys->aac->close(s->aac);
    s->aac = NULL;
    if (s->f) fclose(s->f);
    s->f = NULL;
}

void sa_streams_free(sa_system *sys)
{
    if (!sys->streams) return;
    for (int i = 0; i < sys->nstreams; i++) {
        sa_stream *s = &sys->streams[i];
        stream_close_io(sys, s);
        sa_free(sys, s->ring); free(s->inbuf); free(s->dec);
    }
    free(sys->streams);
    sys->streams = NULL;
}

sa_stream *sa_stream_acquire(sa_system *sys, int take, uint32_t start_frame)
{
    for (int i = 0; i < sys->nstreams; i++) {
        sa_stream *s = &sys->streams[i];
        if (SA_LD_ACQ(&s->state) != SA_S_FREE) continue;
        const sa_take *tk = &sys->takes[take];
        s->take = take;
        s->channels = tk->channels;
        s->loop = tk->loop_start >= 0;
        s->loop_start = s->loop ? (uint32_t)tk->loop_start : 0;
        s->loop_end = s->loop ? (uint32_t)tk->loop_end : 0;
        s->total = tk->frames;
        if (s->loop && start_frame >= s->loop_end) start_frame = s->loop_start + (start_frame - s->loop_start) % (s->loop_end - s->loop_start);
        if (s->total && start_frame >= s->total) start_frame = s->total ? s->total - 1 : 0;
        s->start_frame = start_frame;
        SA_ST_RLX(&s->wpos, 0u); SA_ST_RLX(&s->rpos, 0u);
        SA_ST_RLX(&s->eof, 0); SA_ST_RLX(&s->failed, 0); SA_ST_RLX(&s->mixer_refs, 0);
        s->underruns = 0;
        SA_ST_REL(&s->state, SA_S_OPEN_REQ);
        return s;
    }
    return NULL;
}
void sa_stream_release(sa_system *sys, sa_stream *s)
{
    (void)sys;
    if (!s) return;
    int st = SA_LD_ACQ(&s->state);
    if (st == SA_S_OPEN_REQ || st == SA_S_ACTIVE) SA_ST_REL(&s->state, SA_S_CLOSE_REQ);
}
int sa_stream_ready(sa_stream *s)
{
    if (SA_LD_ACQ(&s->failed)) return -1;
    if (SA_LD_ACQ(&s->state) != SA_S_ACTIVE) return 0;
    if (SA_LD_ACQ(&s->eof)) return 1;
    uint32_t have = SA_LD_ACQ(&s->wpos) - SA_LD_ACQ(&s->rpos);
    uint32_t need = SA_PREFILL < s->cap / 2 ? SA_PREFILL : s->cap / 2;
    return have >= need ? 1 : 0;
}

/* ---------------- file reading ---------------- */
static int in_fill(sa_stream *s)
{
    if (s->in_pos > 0) {
        memmove(s->inbuf, s->inbuf + s->in_pos, (size_t)(s->in_len - s->in_pos));
        s->in_len -= s->in_pos; s->in_pos = 0;
    }
    size_t got = fread(s->inbuf + s->in_len, 1, (size_t)(SA_INBUF - s->in_len), s->f);
    s->in_len += (int)got;
    s->in_file_pos += (long)got;
    return (int)got;
}
static void in_seek(sa_stream *s, long off)
{
    fseek(s->f, off, SEEK_SET);
    s->in_len = s->in_pos = 0; s->in_file_pos = off;
}
/* file offset of the byte at inbuf[in_pos] */
static long in_tell(const sa_stream *s) { return s->in_file_pos - (s->in_len - s->in_pos); }

/* next complete ADTS frame in the input buffer; returns its length, 0 at EOF, <0 on corrupt data */
static int adts_next(sa_stream *s, const uint8_t **frame)
{
    for (int tries = 0; tries < 3; tries++) {
        sa_adts h;
        int avail = s->in_len - s->in_pos;
        int r = avail >= 7 ? sa_adts_parse(s->inbuf + s->in_pos, avail, &h) : 0;
        if (r == 1 && h.frame_len <= avail) { *frame = s->inbuf + s->in_pos; return h.frame_len; }
        if (r < 0) {   /* resync: scan for the next sync word */
            int k = s->in_pos + 1;
            while (k + 1 < s->in_len && !(s->inbuf[k] == 0xFF && (s->inbuf[k + 1] & 0xF6) == 0xF0)) k++;
            s->in_pos = k;
            continue;
        }
        if (in_fill(s) == 0 && s->in_len - s->in_pos < 7) return 0;
    }
    sa_adts h;
    int avail = s->in_len - s->in_pos;
    if (avail >= 7 && sa_adts_parse(s->inbuf + s->in_pos, avail, &h) == 1 && h.frame_len <= avail) { *frame = s->inbuf + s->in_pos; return h.frame_len; }
    return avail > 0 ? -1 : 0;
}

static struct sa_seekidx *build_index(sa_system *sys, sa_stream *s)
{
    struct sa_seekidx *ix = sys->seek[s->take];
    if (ix) return ix;
    FILE *f = fopen(sys->takes[s->take].file, "rb");
    if (!f) return NULL;
    uint32_t cap = 256, n = 0;
    ix = (struct sa_seekidx *)malloc(sizeof(struct sa_seekidx) + cap * sizeof(uint32_t));
    uint8_t hdr[9];
    long off = s->info.data_offset; uint32_t no = 0;
    while (ix) {
        fseek(f, off, SEEK_SET);
        if (fread(hdr, 1, 7, f) != 7) break;
        sa_adts h;
        if (sa_adts_parse(hdr, 7, &h) != 1) break;
        if (no % SA_IDX_STEP == 0) {
            if (n == cap) {
                cap *= 2;
                struct sa_seekidx *nx = (struct sa_seekidx *)realloc(ix, sizeof(struct sa_seekidx) + cap * sizeof(uint32_t));
                if (!nx) { free(ix); ix = NULL; break; }
                ix = nx;
            }
            ix->off[n++] = (uint32_t)off;
        }
        off += h.frame_len; no++;
    }
    fclose(f);
    if (ix) { ix->n = n; sys->seek[s->take] = ix; }
    return ix;
}

/* position the decoder so that the next emitted frame is source frame `frame` */
static int aac_seek(sa_system *sys, sa_stream *s, uint32_t frame)
{
    const sa_take *tk = &sys->takes[s->take];
    uint32_t t = frame + (uint32_t)tk->priming;
    uint32_t k = t / 1024u; k = k > SA_AAC_PREROLL ? k - SA_AAC_PREROLL : 0;
    long off;
    uint32_t at;
    if (k == 0) { off = s->info.data_offset; at = 0; }
    else if (s->loop_off_known && k == s->loop_adts) { off = s->loop_off; at = k; }
    else {
        struct sa_seekidx *ix = build_index(sys, s);
        if (!ix || !ix->n) return -1;
        uint32_t b = k / SA_IDX_STEP; if (b >= ix->n) b = ix->n - 1;
        off = (long)ix->off[b]; at = b * SA_IDX_STEP;
    }
    in_seek(s, off);
    s->adts_no = at;
    if (s->aac) sys->aac->reset(s->aac);
    s->dec_have = s->dec_pos = 0;
    s->discard = t - at * 1024u;
    s->src_next = frame;
    return 0;
}

static int stream_open(sa_system *sys, sa_stream *s)
{
    const sa_take *tk = &sys->takes[s->take];
    char err[160];
    s->f = fopen(tk->file, "rb");
    if (!s->f) return -1;
    if (sa_probe(s->f, &s->info, err, sizeof err) != SA_OK) return -1;
    if (s->info.fmt == SA_FMT_NONE) {
        if (tk->codec != SA_CODEC_PCM16) return -1;
        s->info.fmt = SA_FMT_PCM16LE; s->info.channels = tk->channels; s->info.rate = tk->rate;
        s->info.frames = s->info.data_bytes / (2u * (uint32_t)tk->channels);
    }
    s->channels = s->info.channels;
    if (s->info.frames && (!s->total || s->info.frames < s->total)) s->total = s->info.frames;
    s->loop_off_known = 0;
    s->in_len = s->in_pos = 0;
    s->dec_have = s->dec_pos = 0;
    if (s->info.fmt == SA_FMT_ADTS) {
        if (!sys->aac_ok) return -1;
        s->aac = sys->aac->open(s->info.channels, s->info.rate);
        if (!s->aac) return -1;
        if (s->loop) {
            uint32_t lt = s->loop_start + (uint32_t)tk->priming;
            uint32_t lk = lt / 1024u; s->loop_adts = lk > SA_AAC_PREROLL ? lk - SA_AAC_PREROLL : 0;
        }
        if (aac_seek(sys, s, s->start_frame)) return -1;
    } else if (s->info.fmt == SA_FMT_IMA_MS || s->info.fmt == SA_FMT_IMA_APPLE) {
        sa_adpcm_seek(&s->ac, s->info.frames_per_block, s->start_frame);
        s->have_loop_ac = 0;
        s->src_next = s->start_frame;
        in_seek(s, s->info.data_offset);   /* blocks are read individually (see fill) */
    } else if (s->info.fmt == SA_FMT_PCM16LE || s->info.fmt == SA_FMT_PCM16BE) {
        in_seek(s, s->info.data_offset + (long)s->start_frame * 2L * s->channels);
        s->src_next = s->start_frame;
    } else return -1;   /* float streams are not supported (bake pcm16) */
    return 0;
}

/* decode at most `max` frames of source into dst (no loop/total handling); returns frames, 0 = end of data, <0 error */
static int decode_some(sa_system *sys, sa_stream *s, int16_t *dst, int max)
{
    int ch = s->channels;
    if (s->info.fmt == SA_FMT_ADTS) {
        while (s->dec_pos >= s->dec_have || s->discard) {
            if (s->dec_pos < s->dec_have && s->discard) {
                uint32_t k = (uint32_t)(s->dec_have - s->dec_pos);
                if (k > s->discard) k = s->discard;
                s->dec_pos += (int)k; s->discard -= k;
                continue;
            }
            const uint8_t *fr; int len = adts_next(s, &fr);
            if (len <= 0) return len;
            if (s->loop && !s->loop_off_known && s->adts_no == s->loop_adts) { s->loop_off = in_tell(s); s->loop_off_known = 1; }
            int n = sys->aac->decode(s->aac, fr, len, s->dec, SA_DECBUF);
            s->in_pos += len; s->adts_no++;
            if (n < 0) return -1;
            s->dec_have = n; s->dec_pos = 0;
        }
        int k = s->dec_have - s->dec_pos; if (k > max) k = max;
        memcpy(dst, s->dec + (size_t)s->dec_pos * ch, (size_t)k * ch * sizeof(int16_t));
        s->dec_pos += k;
        return k;
    }
    if (s->info.fmt == SA_FMT_IMA_MS || s->info.fmt == SA_FMT_IMA_APPLE) {
        /* one block at a time through the input buffer: the cursor addresses block 0 = the buffered block */
        uint32_t nblocks_file = s->info.data_bytes / (uint32_t)s->info.block_bytes;
        if (s->ac.block >= nblocks_file) return 0;
        long boff = s->info.data_offset + (long)s->ac.block * s->info.block_bytes;
        if (in_tell(s) != boff || s->in_len - s->in_pos < s->info.block_bytes) {
            if (in_tell(s) != boff) in_seek(s, boff);
            if (s->in_len - s->in_pos < s->info.block_bytes) in_fill(s);
            if (s->in_len - s->in_pos < s->info.block_bytes) return 0;
        }
        uint32_t fpbu = (uint32_t)s->info.frames_per_block;
        if (s->loop && !s->have_loop_ac && s->ac.fib == 0 && !s->ac.skip && s->ac.block == s->loop_start / fpbu) { s->loop_ac = s->ac; s->have_loop_ac = 1; }
        sa_adpcm_cur c = s->ac; c.block = 0;
        int n = sa_adpcm_decode(s->info.fmt, s->inbuf + s->in_pos, 1, s->info.block_bytes, s->info.frames_per_block, ch, &c, dst, max);
        uint32_t blk = s->ac.block;
        s->ac = c; s->ac.block = blk + c.block;
        if (c.block) s->in_pos += s->info.block_bytes;   /* block finished */
        return n;
    }
    /* PCM16 */
    int need = max * ch * 2;
    if (s->in_len - s->in_pos < need) in_fill(s);
    int avail = (s->in_len - s->in_pos) / (2 * ch);
    if (avail <= 0) return 0;
    int k = avail < max ? avail : max;
    memcpy(dst, s->inbuf + s->in_pos, (size_t)k * ch * 2);
    if (s->info.fmt == SA_FMT_PCM16BE) for (int i = 0; i < k * ch; i++) { uint16_t u = (uint16_t)dst[i]; dst[i] = (int16_t)((u >> 8) | (u << 8)); }
    s->in_pos += k * ch * 2;
    return k;
}

static void loop_to_start(sa_system *sys, sa_stream *s)
{
    if (s->info.fmt == SA_FMT_ADTS) { if (aac_seek(sys, s, s->loop_start)) { SA_ST_REL(&s->eof, 1); } }
    else if (s->info.fmt == SA_FMT_IMA_MS || s->info.fmt == SA_FMT_IMA_APPLE) {
        if (s->have_loop_ac) { s->ac = s->loop_ac; s->ac.skip = s->loop_start % (uint32_t)s->info.frames_per_block; }
        else sa_adpcm_seek(&s->ac, s->info.frames_per_block, s->loop_start);
        s->src_next = s->loop_start;
    }
    else { in_seek(s, s->info.data_offset + (long)s->loop_start * 2L * s->channels); s->src_next = s->loop_start; }
}

/* fill the ring as far as possible */
static void stream_fill(sa_system *sys, sa_stream *s)
{
    int ch = s->channels;
    int16_t tmp[1024 * 2];
    for (int guard = 0; guard < 4096; guard++) {
        if (SA_LD_RLX(&s->eof)) return;
        uint32_t w = SA_LD_RLX(&s->wpos), r = SA_LD_ACQ(&s->rpos);
        uint32_t space = s->cap - (w - r);
        if (space < 256) return;
        uint32_t want = space < 1024 ? space : 1024;
        if (s->loop && s->src_next + want > s->loop_end) want = s->loop_end - s->src_next;
        if (!s->loop && s->total && s->src_next + want > s->total) want = s->total - s->src_next;
        if (want == 0) {
            if (s->loop) { loop_to_start(sys, s); continue; }
            SA_ST_REL(&s->eof, 1); return;
        }
        int n = decode_some(sys, s, tmp, (int)want);
        if (n < 0) { SA_ST_REL(&s->failed, 1); SA_ST_REL(&s->eof, 1); return; }
        if (n == 0) {   /* data ended before the manifest's frame count / loop end */
            if (s->loop && s->src_next > s->loop_start) { loop_to_start(sys, s); continue; }
            SA_ST_REL(&s->eof, 1); return;
        }
        uint32_t p = w & s->mask, first = s->cap - p; if (first > (uint32_t)n) first = (uint32_t)n;
        memcpy(s->ring + (size_t)p * ch, tmp, (size_t)first * ch * 2);
        if ((uint32_t)n > first) memcpy(s->ring, tmp + (size_t)first * ch, (size_t)((uint32_t)n - first) * ch * 2);
        s->src_next += (uint32_t)n;
        SA_ST_REL(&s->wpos, w + (uint32_t)n);
    }
}

void sa_io_service_streams(sa_system *sys)
{
    for (int i = 0; i < sys->nstreams; i++) {
        sa_stream *s = &sys->streams[i];
        int st = SA_LD_ACQ(&s->state);
        if (st == SA_S_OPEN_REQ) {
            if (stream_open(sys, s)) { stream_close_io(sys, s); SA_ST_REL(&s->failed, 1); SA_ST_REL(&s->eof, 1); }
            /* only move to ACTIVE if the game did not close it meanwhile */
            int exp = SA_S_OPEN_REQ;
            __atomic_compare_exchange_n(&s->state, &exp, SA_S_ACTIVE, 0, __ATOMIC_ACQ_REL, __ATOMIC_ACQUIRE);
            st = SA_LD_ACQ(&s->state);
        }
        if (st == SA_S_ACTIVE && !SA_LD_ACQ(&s->failed)) stream_fill(sys, s);
        else if (st == SA_S_CLOSE_REQ && SA_LD_ACQ(&s->mixer_refs) == 0) {
            stream_close_io(sys, s);
            SA_ST_REL(&s->state, SA_S_FREE);
        }
    }
}

void sa_io_thread_main(void *arg)
{
    sa_system *sys = (sa_system *)arg;
    while (!SA_LD_ACQ(&sys->io_quit)) {
        sa_io_service_loads(sys);
        sa_io_service_streams(sys);
        sa_sleep_us(2000);
    }
}
