/* sp_trace.cpp -- the Stage 0 trace writer of libshepphys (physics-plan-v2 section 4 "Stage 0: groundwork", the "trace writer";
 * SHEPHERD_PHYS_TRACE in Appendix A). Ours, not Box2D. The format is in sp_trace.h; tests/t1_runner.c `replay` reads it back.
 *
 * Every mutating C API call (shepphys.cpp's wrappers) appends one record while a trace is open: its arguments, what it read through
 * pointers, and its result, so a replay into fresh worlds re-issues exactly the same calls and checks every returned handle, every
 * step's pose hash and every event drain. Process-wide, one file at a time, buffered stdio (newlib on the Vita: ux0: paths).
 * A write error closes the trace (sp_trace_end then reports it). Diagnostic only: never on in the shipped default. */
#include "shepphys.h"
#include "sp_trace.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

extern "C" {

int sp_tr_on;

}

namespace {
FILE *g_f;
int32_t g_records;
bool g_err;
sp_pose *g_poses;
int32_t g_pose_cap;
const uint8_t k_zero[4] = {0, 0, 0, 0};

void put(const void *p, uint32_t n)
{
    if (g_err || n == 0) return;
    if (fwrite(p, 1, n, g_f) != n) g_err = true;
}
} /* namespace */

extern "C" {

SP_API uint64_t sp_tr_hash(const void *p, uint32_t n)
{
    uint64_t h = 14695981039346656037ull;
    const uint8_t *b = (const uint8_t *)p;
    for (uint32_t i = 0; b && i < n; ++i) h = (h ^ b[i]) * 1099511628211ull;
    return h;
}

SP_API uint64_t sp_tr_pose_hash(int32_t world)
{
    int32_t n = sp_poses(world, g_poses, g_pose_cap);
    if (n > g_pose_cap) {
        sp_pose *np = (sp_pose *)realloc(g_poses, (size_t)n * sizeof(sp_pose));
        if (!np) return 0;
        g_poses = np; g_pose_cap = n;
        n = sp_poses(world, g_poses, g_pose_cap);
    }
    return n > 0 ? sp_tr_hash(g_poses, (uint32_t)n * (uint32_t)sizeof(sp_pose)) : sp_tr_hash(nullptr, 0);
}

void sp_tr_rec(uint32_t op, const void *a, uint32_t na, const void *b, uint32_t nb)
{
    if (!g_f || g_err) return;
    uint32_t pad = (4 - ((na + nb) & 3)) & 3, hd[2] = {op, na + nb + pad};
    put(hd, 8); put(a, na); put(b, nb); put(k_zero, pad);
    g_records++;
    if (g_err) { fclose(g_f); g_f = nullptr; sp_tr_on = 0; }
}

SP_API int32_t sp_trace_begin(const char *path, int32_t flags)
{
    if (!path || g_f) return SP_ERR_PARAM;
    g_f = fopen(path, "wb");
    if (!g_f) return SP_ERR_FULL;
    setvbuf(g_f, nullptr, _IOFBF, 1 << 16);
    g_records = 0; g_err = false;
    struct { uint32_t magic, format, abi, flags; char info[112]; } h;
    memset(&h, 0, sizeof h);
    h.magic = SP_TR_MAGIC; h.format = SP_TR_FORMAT; h.abi = SHEPPHYS_ABI_VERSION; h.flags = (uint32_t)flags;
    strncpy(h.info, sp_build_info(), sizeof h.info - 1);
    put(&h, sizeof h);
    if (g_err) { fclose(g_f); g_f = nullptr; return SP_ERR_FULL; }
    sp_tr_on = 1;
    return SP_OK;
}

SP_API int32_t sp_trace_end(void)
{
    bool was_err = g_err;
    sp_tr_on = 0;
    if (!g_f && !was_err) return SP_ERR_PARAM;
    if (g_f) { if (fclose(g_f) != 0) was_err = true; g_f = nullptr; }
    free(g_poses); g_poses = nullptr; g_pose_cap = 0;
    int32_t n = g_records;
    g_records = 0; g_err = false;
    return was_err ? SP_ERR_FULL : n;
}

SP_API int32_t sp_trace_note(int32_t tag, const int32_t *words, int32_t n)
{
    if (n < 0 || n > 64 || (n > 0 && !words)) return SP_ERR_PARAM;
    if (sp_tr_on) { int32_t a[2] = {tag, n}; sp_tr_rec(SP_TR_NOTE, a, sizeof a, words, (uint32_t)n * 4u); }
    return SP_OK;
}

} /* extern "C" */
