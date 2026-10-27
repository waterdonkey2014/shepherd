/* kit #7 spine track, "native clipping" (vita/notes/kit7-spine.md): the C entry points the engine calls for Spine's SkeletonClipping
 * (Runtime/SpineClipNative.cs; the owner's spine-unity.dll rewritten by tools/Rewriter --spine-clip hands it ClipStart's polygon and every
 * ClipTriangles call). The implementation is a port of the runtime's own clipping code, so it lives owner-local: engine/Derived/native/
 * spine_clip_impl.h (git-ignored, never in git or a VPK). host.c compiles it in when build.sh finds it (SHP_SPINE_CLIP_DERIVED) and else
 * a stub whose shepherd_spine_clip_api answers 0: the engine then never calls the others and the managed code runs, as in kit #6.
 * Contract (the port must hold it, the engine's check mode tests it call by call): the same scalar float operations in the same order as
 * the managed code -- every output float bit-identical -- compiled without contraction (no fused multiply-add). */
#ifndef SHEPHERD_SPINE_CLIP_ABI_H
#define SHEPHERD_SPINE_CLIP_ABI_H

/* 1 = the owner-local port is linked in; 0 = the stub (nothing else may be called). */
int shepherd_spine_clip_api (void);

/* ClipStart's polygon work: poly[0..n) (n floats, x/y pairs) is made clockwise IN PLACE as the managed code makes it, triangulated and
 * decomposed into convex polygons; each polygon is made clockwise and closed with its first vertex, written one after the other to out,
 * its float count to counts. ws = int workspace. With V = n / 2 vertices and S = max(V, 1): needs out_cap >= S * (2V + 2), counts_cap >= S,
 * ws_cap >= 8V + S * (V + 1) + 8. Returns the polygon count (>= 0); -1 = a buffer too small (nothing written); -2 = n < 2 (the managed code
 * throws there: the caller leaves that call to it). */
int shepherd_spine_clip_start (float *poly, int n, float *out, int out_cap, int *counts, int counts_cap, int *ws, int ws_cap);

/* ClipTriangles: the triangles tris[0..tlen) of verts / uvs (x/y pairs) clipped by the npolys polygons ClipStart made (polys / counts as it
 * wrote them). On return 0: res[0] = floats written to out_v and to out_uv each, res[1] = indices written to out_t (the managed lists'
 * Count after the call), res[2] = the largest index count during the call (the managed triangle list grows to it before it can shrink:
 * its capacity). On return 1 nothing is usable: res[0] / res[1] / res[2] = out_v (and out_uv), out_t and scratch sizes that are enough
 * -- grow the buffers and call again. scratch = float workspace of the per-polygon clipper. The caller checks the indices first (the
 * managed code throws IndexOutOfRangeException for an index outside verts / uvs; the port reads no bounds). */
int shepherd_spine_clip_tris (const float *verts, int vlen, const int *tris, int tlen, const float *uvs, const float *polys, const int *counts,
                              int npolys, float *out_v, float *out_uv, int cap_v, int *out_t, int cap_t, float *scratch, int scratch_cap, int *res);

#endif
