/* sp_trace.h -- internal to libshepphys (not part of the C API): the Stage 0 trace writer's hooks (shim/sp_trace.cpp) and the
 * record format tests/t1_runner.c's `replay` reads. Ours, not Box2D.
 *
 * File: a 128-byte header, then records. All little-endian 32-bit words (the Mac and the Vita agree; structs are the pinned
 * shepphys.h layouts, copied as bytes).
 *   header  u32 magic 'SPTR' (0x52545053), u32 format SP_TR_FORMAT, u32 SHEPPHYS_ABI_VERSION, u32 flags, char build_info[112]
 *   record  u32 op (SP_TR_*), u32 payload bytes, the payload (padded with zeros to a multiple of 4)
 * Payloads (a = the int32 words named, then the struct / floats the call read):
 *   WORLD_CREATE       a{ret, has_def} + sp_world_def
 *   WORLD_DESTROY      a{world, ret}
 *   WORLD_STEP         {world, ret, has_def, pose_hash_lo, pose_hash_hi} + sp_step_def   (pose hash: FNV-1a 64 over sp_poses)
 *   WORLD_SET_GRAVITY  a{world, ret, has} + 2 floats
 *   BODY_CREATE        a{world, ret, has_def} + sp_body_def
 *   BODY_DESTROY       a{world, body, ret}
 *   BODY_SET_TRANSFORM a{world, body, ret, has} + 3 floats;  BODY_SET_VELOCITY the same
 *   BODY_APPLY         a{world, body, kind, wake, ret, has} + 2 floats (force, impulse) or 1 (torque, angular impulse)
 *   BODY_SET_TYPE      a{world, body, type, ret};  BODY_SET_FLAG a{world, body, flag, on, ret}
 *   BODY_SET_FLOAT     a{world, body, prop, ret, has} + 1 float
 *   SHAPE_CREATE       {world, body, ret, has_def, has_xy, points} + sp_shape_def + points x 2 floats
 *   SHAPE_DESTROY      a{world, shape, ret};  SHAPE_SET_SENSOR a{world, shape, on, ret}
 *   SHAPE_SET_FILTER   a{world, shape, ret, has} + 3 uint32
 *   EVENTS             a{world, max, ret, has_out, hash_lo, hash_hi}   (hash: FNV-1a 64 over the returned sp_event records)
 *   NOTE               a{tag, n} + n int32 words (sp_trace_note: the engine's frame / step markers)
 * Stage 1 (the Unity layer's core calls; sp_unity.cpp itself is never recorded, only the core calls it makes):
 *   WORLD_SET_MATRIX   a{world, ret, has} + 32 uint32 rows
 *   SHAPE_SET_UNITY    a{world, shape, ret, has} + sp_shape_unity
 *   WORLD_IGNORE_PAIR  a{world, shape_a, shape_b, ignore, ret}
 *   BODY_SET_MASS      a{world, body, ret, has} + 4 floats (mass, centre x, centre y, I about the centre)
 * Stage 2:
 *   EVENTS2            a{world, max, ret, has_out, hash_lo, hash_hi}   (hash: FNV-1a 64 over the returned sp_event2 records)
 * Stage 6 (shim/sp_mesh3d.cpp, the 3D scene):
 *   SCENE3D_CREATE     a{ret, flags}
 *   SCENE3D_DESTROY    a{scene, ret}
 *   MESH3D_CREATE      a{scene, ret, has_def, has_xyz, has_tri} + sp_mesh3d_def + vertex_count x 3 floats + triangle_count x 3 int32
 *   MESH3D_DESTROY     a{scene, mesh, ret}
 *   COL3D_CREATE       a{scene, ret, has} + sp_col3d_def;  COL3D_SET a{scene, col, ret, has} + sp_col3d_def
 *   COL3D_DESTROY      a{scene, col, ret}
 *   RAY3D              a{scene, max, ret, has_q, hash_lo, hash_hi} + sp_ray3d_query   (hash: FNV-1a 64 over the first 44 bytes -- all but
 *                      the collider handle -- of each written sp_ray3d_hit)
 * E1:
 *   WORLD_SET_CLAMPS   a{world, ret, has} + 2 floats (patch P2: units / radians per step)
 */
#ifndef SP_TRACE_H
#define SP_TRACE_H

#include <stdint.h>
#include "shepphys.h"   /* SP_API */

#define SP_TR_MAGIC 0x52545053u   /* "SPTR" */
#define SP_TR_FORMAT 1u
enum {
    SP_TR_WORLD_CREATE = 1, SP_TR_WORLD_DESTROY = 2, SP_TR_WORLD_STEP = 3, SP_TR_WORLD_SET_GRAVITY = 4,
    SP_TR_BODY_CREATE = 5, SP_TR_BODY_DESTROY = 6, SP_TR_BODY_SET_TRANSFORM = 7, SP_TR_BODY_SET_VELOCITY = 8,
    SP_TR_BODY_APPLY = 9, SP_TR_BODY_SET_TYPE = 10, SP_TR_BODY_SET_FLAG = 11, SP_TR_BODY_SET_FLOAT = 12,
    SP_TR_SHAPE_CREATE = 13, SP_TR_SHAPE_DESTROY = 14, SP_TR_SHAPE_SET_SENSOR = 15, SP_TR_SHAPE_SET_FILTER = 16,
    SP_TR_EVENTS = 17, SP_TR_NOTE = 18,
    /* Stage 1: the Unity filter and mass data */
    SP_TR_WORLD_SET_MATRIX = 19, SP_TR_SHAPE_SET_UNITY = 20, SP_TR_WORLD_IGNORE_PAIR = 21, SP_TR_BODY_SET_MASS = 22,
    /* Stage 2: the events drain with contact serials */
    SP_TR_EVENTS2 = 23,
    /* Stage 6: the 3D scene */
    SP_TR_SCENE3D_CREATE = 24, SP_TR_SCENE3D_DESTROY = 25, SP_TR_MESH3D_CREATE = 26, SP_TR_MESH3D_DESTROY = 27,
    SP_TR_COL3D_CREATE = 28, SP_TR_COL3D_SET = 29, SP_TR_COL3D_DESTROY = 30, SP_TR_RAY3D = 31,
    /* E1: patch P2's clamps */
    SP_TR_WORLD_SET_CLAMPS = 32,
    SP_TR_N_OPS = 33
};

#ifdef __cplusplus
extern "C" {
#endif
extern int sp_tr_on;   /* non-zero while a trace is open (one predictable branch per mutating call otherwise) */
void sp_tr_rec(uint32_t op, const void *a, uint32_t na, const void *b, uint32_t nb);
/* exported (sp_* names) for tests/t1_runner.c's replay, which links the dylib */
SP_API uint64_t sp_tr_hash(const void *p, uint32_t n);   /* FNV-1a 64 */
SP_API uint64_t sp_tr_pose_hash(int32_t world);          /* FNV-1a 64 over sp_poses(world) */
SP_API uint64_t sp_tr_ray3d_hash(const sp_ray3d_hit *h, int32_t n);   /* Stage 6: the RAY3D record's hash (44 bytes of each hit) */
#ifdef __cplusplus
}
#endif
#endif /* SP_TRACE_H */
