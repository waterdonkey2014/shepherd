/* kit #10 fidelity (vita/notes/kit10-fidelity.md "2. Exact decoders"): the Vita's OWN IMA ADPCM decoder (vita/audio/src/sa_codec.c -- the same
 * source libshepaudio_vita.a is built from; integer-only C) compiled as a Mac dylib, so DecoderPin can compare it, sample for sample, with the
 * bake's reference decoder (tools/FmodBake/Encode.cs ImaDecode, the encoder's own model) on every IMA take -- sequentially and from block-
 * aligned and mid-block seek points (the mixer's loop / random-access path, sa_adpcm_seek).
 *   int imaref_decode (data, data_bytes, block_bytes, frames_per_block, channels, start_frame, dst, frames)   frames decoded */
#include <stdint.h>
#include <string.h>
#include "sa_internal.h"

__attribute__((visibility("default"))) int imaref_decode(const uint8_t *data, uint32_t data_bytes, int block_bytes, int fpb, int ch,
                                                        uint32_t start_frame, int16_t *dst, int frames)
{
    sa_adpcm_cur cur;
    memset(&cur, 0, sizeof cur);
    sa_adpcm_seek(&cur, fpb, start_frame);
    uint32_t nblocks = data_bytes / (uint32_t)block_bytes;
    return sa_adpcm_decode(SA_FMT_IMA_MS, data, nblocks, block_bytes, fpb, ch, &cur, dst, frames);
}

__attribute__((visibility("default"))) int imaref_fpb(int block_bytes, int ch)
{
    return sa_ima_frames_per_block(SA_FMT_IMA_MS, block_bytes, ch);
}
