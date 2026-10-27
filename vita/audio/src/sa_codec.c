/* Containers (RIFF/WAVE, CAF, AIFF/AIFC, ADTS, raw PCM) and the software decoders (PCM16, IMA ADPCM). */
#include "sa_internal.h"

static uint16_t le16(const uint8_t *p) { return (uint16_t)(p[0] | (p[1] << 8)); }
static uint32_t le32(const uint8_t *p) { return (uint32_t)p[0] | ((uint32_t)p[1] << 8) | ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24); }
static uint16_t be16(const uint8_t *p) { return (uint16_t)((p[0] << 8) | p[1]); }
static uint32_t be32(const uint8_t *p) { return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }
static uint64_t be64(const uint8_t *p) { return ((uint64_t)be32(p) << 32) | be32(p + 4); }
static double be_f64(const uint8_t *p) { uint64_t u = be64(p); double d; memcpy(&d, &u, 8); return d; }
static double be_ext80(const uint8_t *p)
{
    int e = ((p[0] & 0x7F) << 8) | p[1];
    uint64_t m = be64(p + 2);
    if (!e && !m) return 0;
    double v = ldexp((double)m, e - 16383 - 63);
    return (p[0] & 0x80) ? -v : v;
}
static int ferr(char *err, size_t n, const char *m) { if (err && n) snprintf(err, n, "%s", m); return SA_ERR_FORMAT; }

int sa_ima_frames_per_block(int fmt, int block_bytes, int ch)
{
    if (ch < 1) return 0;
    if (fmt == SA_FMT_IMA_APPLE) return 64;
    if (fmt == SA_FMT_IMA_MS) return 1 + (block_bytes / ch - 4) * 2;
    return 0;
}

static int probe_wav(FILE *f, long fsize, sa_fileinfo *o, char *err, size_t en)
{
    uint8_t h[12], ck[8], fmt[64];
    int have_fmt = 0, tag = 0, bits = 0, align = 0, spb = 0; uint32_t fact = 0;
    fseek(f, 0, SEEK_SET);
    if (fread(h, 1, 12, f) != 12) return ferr(err, en, "wav: short header");
    long pos = 12;
    while (pos + 8 <= fsize) {
        fseek(f, pos, SEEK_SET);
        if (fread(ck, 1, 8, f) != 8) break;
        uint32_t sz = le32(ck + 4);
        if (!memcmp(ck, "fmt ", 4)) {
            size_t n = sz < sizeof fmt ? sz : sizeof fmt;
            memset(fmt, 0, sizeof fmt);
            if (fread(fmt, 1, n, f) != n) return ferr(err, en, "wav: short fmt");
            tag = le16(fmt); o->channels = le16(fmt + 2); o->rate = (int)le32(fmt + 4);
            align = le16(fmt + 12); bits = le16(fmt + 14);
            if (tag == 0xFFFE && sz >= 26) tag = le16(fmt + 24);          /* EXTENSIBLE: SubFormat GUID's first 2 bytes */
            if (tag == 0x11 && sz >= 20) spb = le16(fmt + 18);
            have_fmt = 1;
        } else if (!memcmp(ck, "fact", 4) && sz >= 4) {
            uint8_t b[4]; if (fread(b, 1, 4, f) == 4) fact = le32(b);
        } else if (!memcmp(ck, "data", 4)) {
            if (!have_fmt) return ferr(err, en, "wav: data before fmt");
            o->data_offset = pos + 8;
            o->data_bytes = sz;
            if ((long)o->data_offset + (long)sz > fsize || sz == 0xFFFFFFFFu) o->data_bytes = (uint32_t)(fsize - o->data_offset);
            if (o->channels < 1 || o->channels > 2) return ferr(err, en, "wav: only mono/stereo");
            if (tag == 1 && bits == 16) { o->fmt = SA_FMT_PCM16LE; o->frames = o->data_bytes / (2u * o->channels); }
            else if (tag == 3 && bits == 32) { o->fmt = SA_FMT_F32LE; o->frames = o->data_bytes / (4u * o->channels); }
            else if (tag == 0x11 && bits == 4) {
                o->fmt = SA_FMT_IMA_MS; o->block_bytes = align;
                o->frames_per_block = sa_ima_frames_per_block(SA_FMT_IMA_MS, align, o->channels);
                if (spb && spb != o->frames_per_block) return ferr(err, en, "wav: IMA samplesPerBlock mismatch");
                if (align < 4 * o->channels || (align % (4 * o->channels))) return ferr(err, en, "wav: bad IMA blockAlign");
                uint32_t nb = o->data_bytes / (uint32_t)align;
                o->frames = nb * (uint32_t)o->frames_per_block;
                if (fact && fact < o->frames) o->frames = fact;
            } else return ferr(err, en, "wav: unsupported format tag/bits (need PCM16, float32 or IMA ADPCM 0x11)");
            return SA_OK;
        }
        pos += 8 + (long)sz + (sz & 1);
    }
    return ferr(err, en, "wav: no data chunk");
}

static int probe_caf(FILE *f, long fsize, sa_fileinfo *o, char *err, size_t en)
{
    uint8_t ck[12], d[32];
    int have_desc = 0; uint32_t fid = 0, flags = 0, bpp = 0, fpp = 0, bits = 0; int64_t valid = -1;
    long pos = 8;
    while (pos + 12 <= fsize) {
        fseek(f, pos, SEEK_SET);
        if (fread(ck, 1, 12, f) != 12) break;
        int64_t sz = (int64_t)be64(ck + 4);
        if (!memcmp(ck, "desc", 4)) {
            if (fread(d, 1, 32, f) != 32) return ferr(err, en, "caf: short desc");
            o->rate = (int)(be_f64(d) + 0.5); fid = be32(d + 8); flags = be32(d + 12); bpp = be32(d + 16);
            fpp = be32(d + 20); o->channels = (int)be32(d + 24); bits = be32(d + 28);
            have_desc = 1;
        } else if (!memcmp(ck, "pakt", 4)) {
            uint8_t p[24];
            if (fread(p, 1, 24, f) == 24) valid = (int64_t)be64(p + 8);
        } else if (!memcmp(ck, "data", 4)) {
            if (!have_desc) return ferr(err, en, "caf: data before desc");
            o->data_offset = pos + 12 + 4;   /* skip mEditCount */
            int64_t n = sz < 0 ? (fsize - o->data_offset) : sz - 4;
            if (o->data_offset + n > fsize) n = fsize - o->data_offset;
            o->data_bytes = (uint32_t)n;
            if (o->channels < 1 || o->channels > 2) return ferr(err, en, "caf: only mono/stereo");
            if (fid == 0x6C70636Du /* lpcm */) {
                int fl = flags & 1, little = (flags & 2) != 0;
                if (!fl && bits == 16) o->fmt = little ? SA_FMT_PCM16LE : SA_FMT_PCM16BE;
                else if (fl && bits == 32) o->fmt = little ? SA_FMT_F32LE : SA_FMT_F32BE;
                else return ferr(err, en, "caf: lpcm must be int16 or float32");
                o->frames = o->data_bytes / (uint32_t)(bits / 8 * o->channels);
            } else if (fid == 0x696D6134u /* ima4 */) {
                o->fmt = SA_FMT_IMA_APPLE; o->block_bytes = (int)(bpp ? bpp : 34u * o->channels); o->frames_per_block = 64;
                if (fpp && fpp != 64) return ferr(err, en, "caf: ima4 framesPerPacket != 64");
                o->frames = (o->data_bytes / (uint32_t)o->block_bytes) * 64u;
                if (valid >= 0 && (uint64_t)valid < o->frames) o->frames = (uint32_t)valid;
            } else return ferr(err, en, "caf: unsupported data format (need lpcm or ima4)");
            if (sz < 0) return SA_OK;
        }
        if (sz < 0) break;
        pos += 12 + (long)sz;
    }
    return o->fmt ? SA_OK : ferr(err, en, "caf: no data chunk");
}

static int probe_aiff(FILE *f, long fsize, sa_fileinfo *o, int aifc, char *err, size_t en)
{
    uint8_t ck[8], c[40];
    int have = 0, bits = 0; uint32_t nframes = 0, comp = 0x4E4F4E45u; /* NONE */
    long pos = 12;
    while (pos + 8 <= fsize) {
        fseek(f, pos, SEEK_SET);
        if (fread(ck, 1, 8, f) != 8) break;
        uint32_t sz = be32(ck + 4);
        if (!memcmp(ck, "COMM", 4)) {
            size_t n = sz < sizeof c ? sz : sizeof c;
            memset(c, 0, sizeof c);
            if (fread(c, 1, n, f) != n) return ferr(err, en, "aiff: short COMM");
            o->channels = (int)be16(c); nframes = be32(c + 2); bits = (int)be16(c + 6);
            o->rate = (int)(be_ext80(c + 8) + 0.5);
            if (aifc && sz >= 22) comp = be32(c + 18);
            have = 1;
        } else if (!memcmp(ck, "SSND", 4)) {
            uint8_t s[8];
            if (!have) return ferr(err, en, "aiff: SSND before COMM");
            if (fread(s, 1, 8, f) != 8) return ferr(err, en, "aiff: short SSND");
            o->data_offset = pos + 16 + (long)be32(s);
            o->data_bytes = sz - 8 - be32(s);
            if (o->data_offset + (long)o->data_bytes > fsize) o->data_bytes = (uint32_t)(fsize - o->data_offset);
            if (o->channels < 1 || o->channels > 2) return ferr(err, en, "aiff: only mono/stereo");
            if ((comp == 0x4E4F4E45u || comp == 0x74776F73u /* twos */) && bits == 16) { o->fmt = SA_FMT_PCM16BE; o->frames = nframes; }
            else if (comp == 0x736F7774u /* sowt */ && bits == 16) { o->fmt = SA_FMT_PCM16LE; o->frames = nframes; }
            else if (comp == 0x666C3332u /* fl32 */) { o->fmt = SA_FMT_F32BE; o->frames = nframes; }
            else if (comp == 0x696D6134u /* ima4 */) {
                o->fmt = SA_FMT_IMA_APPLE; o->block_bytes = 34 * o->channels; o->frames_per_block = 64;
                uint32_t packets = o->data_bytes / (uint32_t)o->block_bytes;
                o->frames = packets * 64u;     /* COMM counts packets for ima4 */
                (void)nframes;
            } else return ferr(err, en, "aiff: unsupported compression (need NONE/twos/sowt/fl32/ima4)");
            uint32_t maxf = o->fmt == SA_FMT_IMA_APPLE ? o->frames : o->data_bytes / (uint32_t)((o->fmt == SA_FMT_F32BE ? 4 : 2) * o->channels);
            if (o->frames > maxf) o->frames = maxf;
            return SA_OK;
        }
        pos += 8 + (long)sz + (sz & 1);
    }
    return ferr(err, en, "aiff: no SSND chunk");
}

int sa_adts_parse(const uint8_t *p, int avail, sa_adts *h)
{
    static const int rates[13] = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
    if (avail < 7) return 0;
    if (p[0] != 0xFF || (p[1] & 0xF6) != 0xF0) return -1;   /* sync 0xFFF, layer 00 */
    int prot_absent = p[1] & 1;
    h->profile = (p[2] >> 6) & 3;
    h->sr_index = (p[2] >> 2) & 15;
    if (h->sr_index > 12) return -1;
    h->rate = rates[h->sr_index];
    h->channels = ((p[2] & 1) << 2) | ((p[3] >> 6) & 3);
    h->frame_len = ((p[3] & 3) << 11) | (p[4] << 3) | ((p[5] >> 5) & 7);
    h->raw_blocks = (p[6] & 3) + 1;
    h->header_len = prot_absent ? 7 : 9;
    if (h->frame_len < h->header_len) return -1;
    return 1;
}

int sa_probe(FILE *f, sa_fileinfo *o, char *err, size_t en)
{
    uint8_t h[16];
    memset(o, 0, sizeof *o);
    if (fseek(f, 0, SEEK_END) != 0) return ferr(err, en, "seek failed");
    long fsize = ftell(f);
    fseek(f, 0, SEEK_SET);
    size_t n = fread(h, 1, sizeof h, f);
    if (n >= 12 && !memcmp(h, "RIFF", 4) && !memcmp(h + 8, "WAVE", 4)) return probe_wav(f, fsize, o, err, en);
    if (n >= 8 && !memcmp(h, "caff", 4)) return probe_caf(f, fsize, o, err, en);
    if (n >= 12 && !memcmp(h, "FORM", 4) && (!memcmp(h + 8, "AIFC", 4) || !memcmp(h + 8, "AIFF", 4)))
        return probe_aiff(f, fsize, o, !memcmp(h + 8, "AIFC", 4), err, en);
    long off = 0;
    if (n >= 10 && !memcmp(h, "ID3", 3)) {   /* ID3v2 in front of ADTS */
        off = 10 + (((long)(h[6] & 0x7F) << 21) | ((h[7] & 0x7F) << 14) | ((h[8] & 0x7F) << 7) | (h[9] & 0x7F));
        fseek(f, off, SEEK_SET);
        n = fread(h, 1, sizeof h, f);
    }
    sa_adts a;
    if (n >= 7 && sa_adts_parse(h, (int)n, &a) == 1) {
        if (a.profile != 1) return ferr(err, en, "adts: only AAC-LC (profile 1) is supported");
        o->fmt = SA_FMT_ADTS; o->channels = a.channels; o->rate = a.rate; o->data_offset = off;
        o->data_bytes = (uint32_t)(fsize - off); o->frames = 0;
        if (o->channels < 1 || o->channels > 2) return ferr(err, en, "adts: only mono/stereo");
        return SA_OK;
    }
    /* headerless: little-endian int16 interleaved (the caller supplies channels/rate from the manifest) */
    o->fmt = SA_FMT_NONE; o->data_offset = 0; o->data_bytes = (uint32_t)fsize;
    return SA_OK;
}

/* ---------------- IMA ADPCM ---------------- */
static const int16_t ima_step[89] = {
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
    130, 143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060,
    1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
    7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767 };
static const int8_t ima_index[16] = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

/* bit-exact reference arithmetic (shift-add), branch-free */
static inline int ima_nibble(int n, int *pred, int *index)
{
    int step = ima_step[*index];
    int diff = (step >> 3) + ((step >> 2) & -(n & 1)) + ((step >> 1) & -((n >> 1) & 1)) + (step & -((n >> 2) & 1));
    int neg = -((n >> 3) & 1);
    int p = *pred + ((diff ^ neg) - neg);
    p = p > 32767 ? 32767 : (p < -32768 ? -32768 : p);
    *pred = p;
    int i = *index + ima_index[n];
    *index = i < 0 ? 0 : (i > 88 ? 88 : i);
    return p;
}

void sa_adpcm_seek(sa_adpcm_cur *cur, int fpb, uint32_t frame)
{
    cur->block = frame / (uint32_t)fpb;
    cur->fib = 0;
    cur->skip = frame % (uint32_t)fpb;
    cur->cont = 0;
}

/* one frame at the cursor (block-local index fib) */
static inline void ima_frame(int fmt, const uint8_t *blk, int ch, sa_adpcm_cur *c, int16_t *dst)
{
    if (fmt == SA_FMT_IMA_MS) {
        if (c->fib == 0) {
            for (int k = 0; k < ch; k++) {
                const uint8_t *hh = blk + 4 * k;
                c->pred[k] = (int16_t)le16(hh);
                c->index[k] = hh[2] > 88 ? 88 : hh[2];
                dst[k] = (int16_t)c->pred[k];
            }
            return;
        }
        uint32_t s = c->fib - 1, grp = s >> 3, within = s & 7;
        for (int k = 0; k < ch; k++) {
            uint8_t b = blk[4 * ch + grp * 4 * ch + 4 * k + (within >> 1)];
            int nib = (within & 1) ? (b >> 4) : (b & 15);
            dst[k] = (int16_t)ima_nibble(nib, &c->pred[k], &c->index[k]);
        }
    } else {
        for (int k = 0; k < ch; k++) {
            const uint8_t *pk = blk + 34 * k;
            if (c->fib == 0) {
                int hdr = (pk[0] << 8) | pk[1];
                int hp = (int16_t)(hdr & 0xFF80), hi = (hdr & 0x7F) > 88 ? 88 : (hdr & 0x7F);
                int d = hp - c->pred[k];
                if (!(c->cont && c->index[k] == hi && d <= 0x7F && d >= -0x7F)) { c->pred[k] = hp; c->index[k] = hi; }
            }
            uint8_t b = pk[2 + (c->fib >> 1)];
            int nib = (c->fib & 1) ? (b >> 4) : (b & 15);
            dst[k] = (int16_t)ima_nibble(nib, &c->pred[k], &c->index[k]);
        }
    }
}

/* decode frames [fib, fib+n) of one block; fib advances by n */
static void ima_run(int fmt, const uint8_t *blk, int ch, sa_adpcm_cur *c, int16_t *o, int n)
{
    int i = 0;
    if (c->fib == 0 && n > 0) { ima_frame(fmt, blk, ch, c, o); c->fib++; o += ch; i = 1; }
    if (fmt == SA_FMT_IMA_MS && ch == 1) {           /* nibbles follow the 4-byte header sequentially */
        uint32_t sidx = c->fib - 1;
        const uint8_t *bp = blk + 4 + (sidx >> 1);
        int hi = (int)(sidx & 1), pr = c->pred[0], ix = c->index[0];
        c->fib += (uint32_t)(n - i);
        for (; i < n; i++) {
            int nib = hi ? (*bp++ >> 4) : (*bp & 15);
            hi ^= 1;
            *o++ = (int16_t)ima_nibble(nib, &pr, &ix);
        }
        c->pred[0] = pr; c->index[0] = ix;
        return;
    }
    if (fmt == SA_FMT_IMA_APPLE) {                    /* packet per channel, nibble fib of each */
        uint32_t f = c->fib;
        c->fib += (uint32_t)(n - i);
        if (ch == 1) {
            int pr = c->pred[0], ix = c->index[0];
            for (; i < n; i++, f++) { uint8_t b = blk[2 + (f >> 1)]; *o++ = (int16_t)ima_nibble((f & 1) ? (b >> 4) : (b & 15), &pr, &ix); }
            c->pred[0] = pr; c->index[0] = ix;
        } else {
            int p0 = c->pred[0], p1 = c->pred[1], x0 = c->index[0], x1 = c->index[1];
            for (; i < n; i++, f++) {
                uint8_t b0 = blk[2 + (f >> 1)], b1 = blk[36 + (f >> 1)];
                int sh = (int)(f & 1) << 2;
                o[0] = (int16_t)ima_nibble((b0 >> sh) & 15, &p0, &x0);
                o[1] = (int16_t)ima_nibble((b1 >> sh) & 15, &p1, &x1);
                o += 2;
            }
            c->pred[0] = p0; c->pred[1] = p1; c->index[0] = x0; c->index[1] = x1;
        }
        return;
    }
    for (; i < n; i++) { ima_frame(fmt, blk, ch, c, o); o += ch; c->fib++; }
}

int sa_adpcm_decode_cap(int fmt, const uint8_t *data, uint32_t nblocks, int block_bytes, int fpb, int ch,
                        sa_adpcm_cur *cur, int16_t *dst, int frames, uint32_t cap_block, sa_adpcm_cur *cap, int *captured)
{
    int16_t junk[2];
    int done = 0;
    if (cap && !*captured && cur->fib == 0 && cur->block == cap_block && !cur->skip) { *cap = *cur; *captured = 1; }
    while (cur->skip) {   /* pending seek within the block: decode and drop */
        if (cur->block >= nblocks) return 0;
        ima_frame(fmt, data + (size_t)cur->block * (size_t)block_bytes, ch, cur, junk);
        cur->fib++; cur->skip--; cur->cont = 1;
        if (cur->fib == (uint32_t)fpb) { cur->fib = 0; cur->block++; }
    }
    while (done < frames) {
        if (cur->block >= nblocks) break;
        if (cap && !*captured && cur->fib == 0 && cur->block == cap_block) { *cap = *cur; *captured = 1; }
        const uint8_t *blk = data + (size_t)cur->block * (size_t)block_bytes;
        int n = fpb - (int)cur->fib;
        if (n > frames - done) n = frames - done;
        ima_run(fmt, blk, ch, cur, dst + (size_t)done * ch, n);
        cur->cont = 1;
        done += n;
        if (cur->fib == (uint32_t)fpb) { cur->fib = 0; cur->block++; }
    }
    return done;
}
int sa_adpcm_decode(int fmt, const uint8_t *data, uint32_t nblocks, int block_bytes, int fpb, int ch,
                    sa_adpcm_cur *cur, int16_t *dst, int frames)
{
    return sa_adpcm_decode_cap(fmt, data, nblocks, block_bytes, fpb, ch, cur, dst, frames, 0, NULL, NULL);
}
