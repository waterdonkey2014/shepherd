// kit #10 fidelity (vita/notes/kit10-fidelity.md "2. Exact decoders"): the REFERENCE crunch decoder for the decoder pin -- Unity's own crunch
// fork (Unity-Technologies/crunch@unity, inc/crn_decomp.h, ZLIB licence; the source the Unity 2022.3 player unpacks DXT1Crunched /
// DXT5Crunched textures with, i.e. the PC original's decoded output) built as a Mac dylib that DecoderPin calls in process. Not game data.
//   int crnref_info (data, size, &w, &h, &levels, &faces, &fmt)        0 = ok
//   int crnref_unpack (data, size, level, out, out_size)              bytes written (bx * by * block bytes of face 0), < 0 = error
#include <cstdint>
#include <cstring>
#include "crn_decomp.h"

extern "C" {

__attribute__((visibility("default"))) int crnref_info(const void *data, uint32_t size, int *w, int *h, int *levels, int *faces, int *fmt)
{
    crnd::crn_texture_info ti;
    ti.m_struct_size = sizeof(ti);
    if (!crnd::crnd_get_texture_info(data, size, &ti)) return -1;
    *w = (int)ti.m_width; *h = (int)ti.m_height; *levels = (int)ti.m_levels; *faces = (int)ti.m_faces; *fmt = (int)ti.m_format;
    return 0;
}

__attribute__((visibility("default"))) int crnref_unpack(const void *data, uint32_t size, int level, void *out, uint32_t out_size)
{
    crnd::crn_texture_info ti;
    ti.m_struct_size = sizeof(ti);
    if (!crnd::crnd_get_texture_info(data, size, &ti)) return -1;
    if (level < 0 || (uint32_t)level >= ti.m_levels) return -2;
    uint32_t w = ti.m_width >> level, h = ti.m_height >> level;
    if (!w) w = 1;
    if (!h) h = 1;
    uint32_t bx = (w + 3) / 4, by = (h + 3) / 4, bs = crnd::crnd_get_bytes_per_dxt_block(ti.m_format);
    uint32_t need = bx * by * bs;
    if (out_size < need) return -3;
    crnd::crnd_unpack_context ctx = crnd::crnd_unpack_begin(data, size);
    if (!ctx) return -4;
    void *faces[1] = { out };
    bool ok = crnd::crnd_unpack_level(ctx, faces, need, bx * bs, (crnd::uint32)level);
    crnd::crnd_unpack_end(ctx);
    return ok ? (int)need : -5;
}

}
