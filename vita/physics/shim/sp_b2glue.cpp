/* sp_b2glue.cpp -- B2, a build-level substitution (box2d/PATCHES.md; physics-plan-v2 section 1.2 item 5): the library is built
 * WITHOUT Box2D's Common/b2Settings.cpp and this file defines its four symbols instead. No Box2D file is changed.
 *  - b2_version: {2, 3, 1}, as b2Settings.cpp.
 *  - b2Alloc / b2Free: Box2D's allocations go to libshepphys's fixed arena (shim/sp_core.cpp, sp_b2_alloc / sp_b2_free: the pool's
 *    deterministic allocator on a block that never moves); before sp_init, with SP_INIT_B2_MALLOC, or when the arena is full they fall
 *    back to malloc / free -- stock Box2D's own bodies -- and the fallback is counted (sp_b2_get_stats).
 *  - b2Log: b2Settings.cpp's body (vprintf).
 * Ours. The CMake option SHEPPHYS_B2=OFF builds stock b2Settings.cpp instead (the switch back at build level; SP_INIT_B2_MALLOC is the
 * run-time one). */
#include <Box2D/Common/b2Settings.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>

void *sp_b2_alloc(int32 size);
bool sp_b2_free(void *mem);

b2Version b2_version = {2, 3, 1};

void *b2Alloc(int32 size)
{
    void *p = sp_b2_alloc(size);
    return p ? p : malloc(size);
}

void b2Free(void *mem)
{
    if (!sp_b2_free(mem)) free(mem);
}

void b2Log(const char *string, ...)
{
    va_list args;
    va_start(args, string);
    vprintf(string, args);
    va_end(args);
}
