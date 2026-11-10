/* SHEPHERD-VITA device kit #13, track bootc (vita/notes/kit13-bootc.md section 7): THE AAC DECODER CHECK -- one driver for both sides, so the
 * Mac's reference and the Vita's check decode the same way: every ADTS frame of a take, in order, through the platform's AAC decoder
 * (sa_platform_aac: AudioToolbox on the Mac -- what the bake's verify listened to --, sceAudiodec on the Vita), and one FNV-1a 64 of the int16
 * output (little-endian, interleaved, every frame the decoder hands back, the encoder delay included), its frame count, the largest |sample|
 * and how many frames the decoder refused. AAC decoders are not bit-exact by the standard, so a difference is an answer (how far), not a
 * failure of the check: the device side keeps the first 64 KiB of a differing take's samples for the Mac to compare.
 * Header only (static functions): included by vita/audio/tools/sa_deccheck.c (the Mac tool) and by the Vita host (vita/shepherd/k13_bootc.inc.c). */
#ifndef SA_DECCHECK_H
#define SA_DECCHECK_H
#include "../src/sa_internal.h"

typedef struct { unsigned long long fnv; unsigned frames, peak, aus, refused; } sa_dec_res;

/* one take (b: the whole .aac file, ch / rate: the manifest's) -> r; pcm: room for pcm_frames interleaved frames; dump (optional) sees every
 * decoded chunk (the device's first 64 KiB of a differing take). 0 = decoded to its end, < 0 = the decoder did not open */
static int sa_deccheck_buf (const sa_aac_vtbl *v, const unsigned char *b, int len, int ch, int rate, sa_dec_res *r, int16_t *pcm, int pcm_frames,
                            void (*dump) (const int16_t *p, int n, void *ctx), void *ctx)
{
    memset (r, 0, sizeof *r);
    r->fnv = 14695981039346656037ull;
    void *dec = v->open (ch, rate);
    if (!dec) return -1;
    int pos = 0;
    while (pos + 7 <= len) {
        sa_adts h;
        if (sa_adts_parse (b + pos, len - pos, &h) != 1 || pos + h.frame_len > len) break;
        int n = v->decode (dec, b + pos, h.frame_len, pcm, pcm_frames);
        r->aus++;
        if (n < 0) r->refused++;
        else if (n > 0) {
            const unsigned char *q = (const unsigned char *) pcm;
            unsigned bytes = (unsigned) n * (unsigned) ch * 2u;
            for (unsigned i = 0; i < bytes; i += 2) {   /* (little-endian int16 on both: ARM and the Mac's arm64 / x86) */
                r->fnv ^= q [i]; r->fnv *= 1099511628211ull;
                r->fnv ^= q [i + 1]; r->fnv *= 1099511628211ull;
                int s = pcm [i / 2]; if (s < 0) s = -s;
                if ((unsigned) s > r->peak) r->peak = (unsigned) s;
            }
            r->frames += (unsigned) n;
            if (dump) dump (pcm, n * ch, ctx);
        }
        pos += h.frame_len;
    }
    v->close (dec);
    return 0;
}
#endif
