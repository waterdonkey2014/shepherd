/* shepphys.h -- the Shepherd physics core's flat C API (stage 0a: a thin shim over vendored Box2D 2.3.1).
 *
 * Private hobby port (Shepherd). This file and shepphys.cpp are OURS; Box2D itself (vita/physics/box2d/) is used unmodified.
 * Shaped after the plan's native ABI (physics-plan.md section 1.3), kept minimal for this stage:
 *   - no pointer crosses the ABI: worlds, bodies and shapes are int32 handles (index | generation << 16; 0 and negatives are
 *     never valid handles, negatives are SP_ERR_* codes);
 *   - shared structs hold int32 / uint32 / float only, 4-byte packed by construction, sizes pinned by static asserts and
 *     reported by sp_abi() so a managed binding ([StructLayout(Sequential, Pack = 4)]) can check them at load;
 *   - floats travel through memory (struct or float* arguments), never as hard-float call arguments;
 *   - one exported lookup, shepphys_proc(name), returns every entry point (the shepaudio_proc / shepgl_proc pattern).
 * Stage 0 (physics-plan-v2 section 4) added the runtime + pool (sp_init, sp_pool_*), the settings check against Box2D's compiled
 * constants (sp_settings_check), the trace writer (sp_trace_*) and sp_nop. Stage 1 added the Unity layer (shim/sp_unity.cpp: the
 * pose mirror in the pool, the Unity contact filter, movement states, interpolation, render poses; see the Stage 1 section below).
 * Stage 2 added patch P1 (box2d/PATCHES.md: Unity's trigger phase and kinematic triggers, behind world flags that default to off),
 * contact creation serials with richer events (sp_events2), and Unity-shaped queries walking the broadphase directly (sp_u_query,
 * bounds, closest point, point tests, contacts of a collider; shim/sp_query.cpp).
 * Stage 3 added the dynamics of combat (physics-plan-v2 section 4 "Stage 3"): force / torque accumulators and the inverse mass in the
 * mirror (AddForce / AddTorque with 0 native calls; impulses change the mirror's velocity), frozen position axes, the H-DAMP / H-CHAIN
 * variants (no Box2D alteration: damping mapped per step, one-sided chains by a PreSolve rule) and the contact snapshot a Collision2D
 * is built from (sp_u_contacts_between, with the pre- / post-solve relative velocity of H-RELVEL).
 * Stage 6 added the 3D island-ground ray (physics-plan-v2 section 4 "Stage 6", section 2 "Stage 6"; shim/sp_mesh3d.cpp): 3D scenes of static
 * colliders (MeshCollider triangles with a BVH, Box / Sphere / Capsule analytic), PhysX-shaped rays (the ray in the mesh's vertex space,
 * Moller-Trumbore with backface culling, the closest hit over every collider on the mask), the +z fast path (a world-XY grid over the
 * triangles a +z ray can hit, the same triangle test, bitwise = the general path) and the layers-present early-out.
 * Not here yet (later stages): the command rings.
 * Everything below is stock Box2D 2.3.1 behaviour unless a world sets the P1 flags (the Unity filter is off until a world gets a
 * matrix).
 *
 * Threads: one thread per world at a time; nothing here is thread-safe across calls on the same world.
 * Events: Box2D BeginContact/EndContact are queued in the order Box2D reports them (inside sp_world_step; EndContact also
 * from sp_body_destroy / sp_shape_destroy between steps; a filter change ends its contacts inside the next step) and are
 * handed out by sp_events() (drain).
 */
#ifndef SHEPPHYS_H
#define SHEPPHYS_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#define SP_API __declspec(dllexport)
#else
#define SP_API __attribute__((visibility("default")))
#endif

#define SHEPPHYS_ABI_VERSION 9          /* bump on any struct or signature change (2: Stage 0's runtime / pool / settings / trace;
                                           3: Stage 1's Unity layer -- filter, mirror, movement states, interpolation, render poses, B2;
                                           4: Stage 2 -- P1 world flags, contact serials + sp_events2, Unity queries;
                                           5: Stage 3 -- the mirror's force accumulators / inverse mass / pre-step velocity (160 B),
                                              one-sided chains, sp_ucontact + sp_u_contacts_between;
                                           6: Stage 6 -- the 3D scene: sp_mesh3d_def, sp_col3d_def, sp_ray3d_query, sp_ray3d_hit;
                                           7: E1 -- sp_world_set_clamps (patch P2), SP_USTEP_MOVEGRAV_ON, the TOI casts;
                                           8: Stage 4 step 4a -- SP_USF_CHAIN_WELD, Unity's chain vertex filter;
                                           9: Stage 4 step 4b -- SP_SHAPE_UNITY_WELD (patch P5), SP_USF_UNITY_POLY) */
#define SHEPPHYS_ABI_PROBE   51.75f     /* sp_abi()'s float probe: 1.5 + 0.25 * 3 + 2 * 24.75 computed from memory */

/* ---- results (negative int32) ---- */
enum {
    SP_OK = 0,
    SP_ERR_HANDLE = -1,     /* stale or invalid world / body / shape handle */
    SP_ERR_PARAM = -2,      /* bad argument (shape kind, vertex count, null pointer, ...) */
    SP_ERR_FULL = -3,       /* out of handle slots or memory */
    SP_ERR_LOCKED = -4      /* structural call made while the world is inside a step */
};

/* ---- body types: same numbers as Box2D's b2BodyType (Unity's RigidbodyType2D is Dynamic 0, Kinematic 1, Static 2:
 *      the binding maps them) ---- */
enum { SP_BODY_STATIC = 0, SP_BODY_KINEMATIC = 1, SP_BODY_DYNAMIC = 2 };

/* body flags (sp_body_def.flags, sp_body_set_flag) */
enum {
    SP_BODY_AWAKE = 1,
    SP_BODY_ALLOW_SLEEP = 2,
    SP_BODY_FIXED_ROTATION = 4,
    SP_BODY_BULLET = 8,
    SP_BODY_ACTIVE = 16
};

/* sp_body_set_float properties */
enum { SP_PROP_LINEAR_DAMPING = 0, SP_PROP_ANGULAR_DAMPING = 1, SP_PROP_GRAVITY_SCALE = 2 };

/* sp_body_apply kinds: args = {x, y} for the linear kinds (at the centre of mass), {t} for the angular ones */
enum { SP_APPLY_FORCE = 0, SP_APPLY_IMPULSE = 1, SP_APPLY_TORQUE = 2, SP_APPLY_ANGULAR_IMPULSE = 3 };

/* ---- shape kinds and flags ---- */
enum {
    SP_SHAPE_CIRCLE = 0,    /* radius, centre (cx, cy) in body space; no vertices */
    SP_SHAPE_BOX = 1,       /* half extents (hx, hy), centre (cx, cy), angle; b2PolygonShape::SetAsBox */
    SP_SHAPE_POLYGON = 2,   /* vertex_count 3..8 points (convex hull is taken by Box2D, as b2PolygonShape::Set does) */
    SP_SHAPE_EDGE = 3,      /* always 4 points v0 v1 v2 v3; the edge is v1-v2; v0 / v3 are ghosts, used only if flagged */
    SP_SHAPE_CHAIN = 4      /* vertex_count points; SP_SHAPE_LOOP closes it; for an open chain the GHOST flags take the first
                               and/or last point as the prev / next ghost vertex instead of a chain vertex */
};
enum {
    SP_SHAPE_SENSOR = 1,
    SP_SHAPE_LOOP = 2,
    SP_SHAPE_GHOST_PREV = 4,
    SP_SHAPE_GHOST_NEXT = 8,
    SP_SHAPE_RADIUS = 16,   /* Stage 1: POLYGON / EDGE / CHAIN take def->radius as their skin (m_radius) instead of the stock
                               b2_polygonRadius (a capsule as a rounded box, an edge radius); without the flag radius is ignored */
    SP_SHAPE_NO_SKIN = 32,  /* Stage 2 (queries only): with SP_SHAPE_RADIUS, the whole radius is the collider's own rounding (a capsule);
                               without it a POLYGON / EDGE / CHAIN radius includes Box2D's b2_polygonRadius skin (an edge radius) */
    SP_SHAPE_ONE_SIDED = 64, /* Stage 3, H-CHAIN=one-sided (EDGE / CHAIN only): a contact is disabled in PreSolve while the other shape's
                               centroid is behind the touched edge (the right-hand normal of v1 -> v2 is the front, Box2D 2.4's rule);
                               no Box2D alteration (the core's contact listener) */
    SP_SHAPE_UNITY_WELD = 128 /* Stage 4b (POLYGON only): b2PolygonShape::Set welds with Unity 2022.3's rule -- a vertex whose squared
                               distance to a vertex already kept is below (0.5 b2_linearSlop)^2 (2.5e-3 apart) -- instead of Box2D
                               2.3.1's (squared distance below 0.5 b2_linearSlop: 0.05 apart); patch P5 (box2d/PATCHES.md), the flag
                               traced with the shape so a replay welds alike. Without it Set is stock */
};

/* ---- shared structs (int32 / uint32 / float only) ---- */
typedef struct sp_world_def {
    float gravity_x, gravity_y;
    int32_t flags;          /* SP_WORLD_* below */
    int32_t reserved;
} sp_world_def;
enum { SP_WORLD_ALLOW_SLEEP = 1, SP_WORLD_CONTINUOUS = 2, SP_WORLD_WARM_START = 4, SP_WORLD_SUB_STEP = 8,
       /* Stage 2, patch P1 (box2d/PATCHES.md; physics-plan-v2 sections 1.2, 1.8): set around this world's b2World::Step only */
       SP_WORLD_TRIGGER_PHASE = 16,       /* H-TRIGPHASE=final: sensor touching evaluated after the solve (b2_unityTriggerPhase) */
       SP_WORLD_KINEMATIC_TRIGGERS = 32,  /* H-KINTRIG=all-but-static: a sensor pair needs one non-static body (b2_unityKinematicTriggers) */
       SP_WORLD_CONTACT_SERIALS = 64 };   /* Stage 2: every contact gets a creation serial (sp_events2; H-CBORDER=serial) */
#define SP_WORLD_DEFAULT_FLAGS (SP_WORLD_ALLOW_SLEEP | SP_WORLD_WARM_START)   /* CotL: every body Discrete, so no TOI */

typedef struct sp_body_def {
    int32_t type;           /* SP_BODY_STATIC / KINEMATIC / DYNAMIC */
    int32_t flags;          /* SP_BODY_* */
    float x, y, angle;
    float vx, vy, w;
    float linear_damping, angular_damping, gravity_scale;
    int32_t user;           /* returned in sp_pose.user */
} sp_body_def;

typedef struct sp_shape_def {
    int32_t kind;           /* SP_SHAPE_CIRCLE .. CHAIN */
    int32_t flags;          /* SP_SHAPE_* */
    float density, friction, restitution;
    float radius;           /* circle radius; ignored for the others (Box2D's polygon skin b2_polygonRadius applies) */
    float cx, cy;           /* circle / box centre in body space */
    float hx, hy, angle;    /* box half extents and angle */
    uint32_t category_bits, mask_bits;  /* b2Filter (16 bits used) */
    int32_t group_index;    /* b2Filter (int16 used) */
    int32_t vertex_count;   /* POLYGON / CHAIN: number of points in xy; EDGE: 4 */
    int32_t user;
} sp_shape_def;

typedef struct sp_pose {
    float x, y, angle;      /* body origin and angle (b2Body::GetPosition / GetAngle) */
    float vx, vy, w;
    int32_t flags;          /* bit 0 awake, bits 8..9 body type */
    int32_t body;           /* the body handle */
    int32_t user;
} sp_pose;

typedef struct sp_step_def {
    float dt;
    int32_t velocity_iterations, position_iterations;
    int32_t flags;          /* bit 0: clear forces after the step (Box2D's default auto-clear is on regardless) */
} sp_step_def;

typedef struct sp_step_stats {
    int32_t step;           /* index of the step just taken (0-based) */
    int32_t bodies, awake_bodies, shapes, contacts, touching, proxies, tree_height, events_queued;
} sp_step_stats;

enum { SP_EV_BEGIN = 1, SP_EV_END = 2 };
enum { SP_EVF_SENSOR = 1, SP_EVF_IN_STEP = 2 };
typedef struct sp_event {
    int32_t kind;           /* SP_EV_BEGIN / SP_EV_END */
    int32_t flags;          /* SP_EVF_*: either shape a sensor; reported inside sp_world_step (else by a destroy / refilter) */
    int32_t shape_a, shape_b;
    int32_t step;           /* the world's step counter when queued (the step being taken, or the next one between steps) */
} sp_event;

enum { SP_CF_TOUCHING = 1, SP_CF_SENSOR = 2, SP_CF_ENABLED = 4 };
typedef struct sp_contact {
    int32_t shape_a, shape_b, body_a, body_b;
    int32_t child_a, child_b;   /* chain edge indices */
    int32_t flags;              /* SP_CF_* */
    int32_t point_count;        /* 0..2 (sensors: 0) */
    float normal_x, normal_y;   /* world normal from a to b (b2WorldManifold) */
    float px[2], py[2];         /* world points */
    float separation[2];
    float normal_impulse[2], tangent_impulse[2];   /* the last solve's accumulated impulses (warm-start values) */
    float friction, restitution;
} sp_contact;

enum { SP_CONTACTS_TOUCHING_ONLY = 1, SP_CONTACTS_NO_SENSORS = 2 };

enum { SP_RAY_CLOSEST = 0, SP_RAY_ALL = 1, SP_RAY_SORTED = 2, SP_RAY_SKIP_SENSORS = 4 };
typedef struct sp_ray_query {
    float x1, y1, x2, y2;
    uint32_t mask_bits;     /* tested against each shape's category bits; 0xFFFF = everything */
    int32_t flags;          /* SP_RAY_* */
} sp_ray_query;

typedef struct sp_ray_hit {
    int32_t shape, body, child;
    int32_t reserved;
    float px, py, nx, ny, fraction;
} sp_ray_hit;

enum { SP_QUERY_SKIP_SENSORS = 1 };
typedef struct sp_aabb_query {
    float lower_x, lower_y, upper_x, upper_y;
    uint32_t mask_bits;
    int32_t flags;          /* SP_QUERY_* */
} sp_aabb_query;

/* ---- Stage 0 (physics-plan-v2 section 4 "Stage 0: groundwork"): runtime + pool, settings check, trace ---- */

/* sp_init: one process-wide runtime (the pool the Stage 1 mirror and B2's b2Alloc live in). Pointers never cross the ABI:
 * the pool is one base (sp_pool_base) and every block is a uint32 offset into it (plan section 1.3). */
typedef struct sp_init_def {
    int32_t pool_bytes;     /* SHEPHERD_PHYS_POOL_MB x 1 MiB; 0 = the default, 4 MiB */
    int32_t workers;        /* worker threads (Stage 7, P3); must be 0 until then */
    int32_t flags;          /* SP_INIT_* */
    int32_t b2_bytes;       /* Stage 1, B2: the Box2D arena (b2Alloc / b2Free); 0 = the default, 4 MiB (was `reserved`, always 0) */
} sp_init_def;
/* SP_INIT_POOL_GROW: Mac: an exhausted pool grows (and logs its high water) instead of failing; the Vita fails loudly.
 * SP_INIT_B2_MALLOC: B2 off at run time -- b2Alloc / b2Free stay malloc / free (stock Box2D's allocator), no arena. */
enum { SP_INIT_POOL_GROW = 1, SP_INIT_B2_MALLOC = 2 };

/* the pool's first block (offset 0): words the managed fast path reads through the base (plan section 1.3 "header words") */
typedef struct sp_header {
    uint32_t magic;         /* SP_HEADER_MAGIC */
    uint32_t abi_version;   /* SHEPPHYS_ABI_VERSION */
    uint32_t dirty_pending; /* the managed dirty set is non-empty: a world read must flush first (written by the binding, Stage 1) */
    uint32_t ring_pending;  /* structural edits queued (Stage 1) */
    uint32_t world_version; /* bumped by every structural change (Stage 1) */
    uint32_t base_gen;      /* bumped whenever the pool moved (a Mac grow): re-read sp_pool_base */
    uint32_t reserved[2];
} sp_header;
#define SP_HEADER_MAGIC 0x53504844u   /* 'SPHD' */

typedef struct sp_pool_stats {
    uint32_t bytes, used, high_water, live_blocks;   /* used / high_water: bytes in live blocks (headers included) */
    uint32_t allocs, frees, grows, failures;
    uint32_t base_gen, top;                          /* top: the bump pointer (bytes ever carved from the pool) */
} sp_pool_stats;

/* Physics2DSettings as decoded from the install at run time (Unity's units: degrees, per second), plus the fixed step */
typedef struct sp_settings {
    float gravity_x, gravity_y;
    int32_t velocity_iterations, position_iterations;
    float velocity_threshold, max_linear_correction, max_angular_correction_deg, max_translation_speed, max_rotation_speed_deg;
    float baumgarte_scale, baumgarte_toi_scale, time_to_sleep, linear_sleep_tolerance, angular_sleep_tolerance_deg;
    float default_contact_offset;
    float fixed_delta_time;  /* TimeManager's Fixed Timestep */
    int32_t reserved;
} sp_settings;
/* sp_settings_check's rows: one per Box2D 2.3.1 compiled constant a Physics2DSettings value maps to (plan section 1.2 table) */
enum {
    SP_SET_VELOCITY_THRESHOLD = 0, SP_SET_MAX_LINEAR_CORRECTION, SP_SET_MAX_ANGULAR_CORRECTION, SP_SET_BAUMGARTE,
    SP_SET_TOI_BAUMGARTE, SP_SET_TIME_TO_SLEEP, SP_SET_LINEAR_SLEEP_TOLERANCE, SP_SET_ANGULAR_SLEEP_TOLERANCE,
    SP_SET_POLYGON_RADIUS,  /* defaultContactOffset vs b2_polygonRadius = 2 x b2_linearSlop (H-SLOP) */
    SP_SET_MAX_TRANSLATION, /* maxTranslationSpeed x fixed step vs b2_maxTranslation (per step): H-MAXTRANS = per-second */
    SP_SET_MAX_ROTATION,    /* maxRotationSpeed x fixed step (radians) vs b2_maxRotation (per step): H-MAXROT */
    SP_SET_COUNT
};
enum { SP_SET_EXACT = 0, SP_SET_CLOSE = 1, SP_SET_DIFFERS = 2 };   /* bit-equal / within 1e-6 relative / differs */
typedef struct sp_settings_row {
    int32_t id;             /* SP_SET_* */
    int32_t verdict;        /* SP_SET_EXACT / CLOSE / DIFFERS */
    float unity;            /* the decoded value in Box2D's units (radians, per step) */
    float box2d;            /* the compiled constant */
} sp_settings_row;

/* sizes reported by sp_abi, in this order (the 11 stage-0a structs, then Stage 0's 5, then Stage 1's 10, then Stage 2's 3, then Stage 3's 1,
 * then Stage 6's 4) */
enum { SP_ABI_N_SIZES = 34 };

/* ---- entry points ---- */
/* ABI handshake: writes up to max sizes (sizeof of the 11 structs above, declaration order) and the float probe; returns
 * SHEPPHYS_ABI_VERSION. */
SP_API int32_t sp_abi(int32_t *sizes, int32_t max, float *probe);
SP_API int32_t sp_version(void);                    /* 0x00MMmmpp of our shim; Box2D's version is b2_version (2.3.1) */
SP_API const char *sp_build_info(void);             /* compiler, flags, trig mode */

SP_API int32_t sp_world_create(const sp_world_def *def);
SP_API int32_t sp_world_destroy(int32_t world);
SP_API int32_t sp_world_step(int32_t world, const sp_step_def *step, sp_step_stats *stats);
SP_API int32_t sp_world_set_gravity(int32_t world, const float *gxy);
/* E1, patch P2: Unity's per-step speed clamps for this world -- trans_rot[0] units, trans_rot[1] radians per step (Physics2DSettings'
 * maxTranslationSpeed and maxRotationSpeed x pi / 180, applied per step, the E1 oracle's MaxTransRot scene); <= 0 = Box2D's compiled
 * b2_maxTranslation / b2_maxRotation (the default: a world never given clamps steps as stock Box2D 2.3.1) */
SP_API int32_t sp_world_set_clamps(int32_t world, const float *trans_rot);

SP_API int32_t sp_body_create(int32_t world, const sp_body_def *def);
SP_API int32_t sp_body_destroy(int32_t world, int32_t body);
SP_API int32_t sp_body_get_pose(int32_t world, int32_t body, sp_pose *out);
SP_API int32_t sp_body_set_transform(int32_t world, int32_t body, const float *xya);      /* stock SetTransform: no wake */
SP_API int32_t sp_body_set_velocity(int32_t world, int32_t body, const float *vxvyw);     /* wakes if non-zero (stock) */
SP_API int32_t sp_body_apply(int32_t world, int32_t body, int32_t kind, const float *args, int32_t wake);
SP_API int32_t sp_body_set_type(int32_t world, int32_t body, int32_t type);
SP_API int32_t sp_body_set_flag(int32_t world, int32_t body, int32_t flag, int32_t on);   /* one SP_BODY_* bit */
SP_API int32_t sp_body_set_float(int32_t world, int32_t body, int32_t prop, const float *value);
SP_API int32_t sp_poses(int32_t world, sp_pose *out, int32_t max);   /* live bodies in handle order; returns the count */

SP_API int32_t sp_shape_create(int32_t world, int32_t body, const sp_shape_def *def, const float *xy);
SP_API int32_t sp_shape_destroy(int32_t world, int32_t shape);
SP_API int32_t sp_shape_set_sensor(int32_t world, int32_t shape, int32_t on);
SP_API int32_t sp_shape_set_filter(int32_t world, int32_t shape, const uint32_t *category_mask_group);
SP_API int32_t sp_shape_body(int32_t world, int32_t shape);           /* the owning body handle */
/* Stage 4b (ABI 9; a query, not traced): a POLYGON shape's vertices in body space as Box2D holds them (m_vertices, m_count; up to max points
 * into xy) -- the count; 0 for another kind. physcheck compares them with the Unity oracle's PhysicsShapeGroup2D vertices */
SP_API int32_t sp_shape_get_polygon(int32_t world, int32_t shape, float *xy, int32_t max);

SP_API int32_t sp_events(int32_t world, sp_event *out, int32_t max); /* drains up to max queued events, oldest first */
SP_API int32_t sp_contacts(int32_t world, sp_contact *out, int32_t max, int32_t flags);  /* Box2D contact-list order;
                                                                        returns the total count (may exceed max) */

SP_API int32_t sp_raycast(int32_t world, const sp_ray_query *q, sp_ray_hit *out, int32_t max);
SP_API int32_t sp_query_aabb(int32_t world, const sp_aabb_query *q, int32_t *shapes, int32_t max);  /* broadphase only */
SP_API int32_t sp_query_point(int32_t world, const float *xy, uint32_t mask_bits, int32_t *shapes, int32_t max);
/* exact overlap of a probe shape (def + xy as in sp_shape_create; CHAIN not allowed) placed at xf = {x, y, angle}, against
 * every shape whose AABB it touches (b2TestOverlap per child). Results in broadphase order. */
SP_API int32_t sp_overlap_shape(int32_t world, const sp_shape_def *def, const float *xy, const float *xf,
                                uint32_t mask_bits, int32_t flags, int32_t *shapes, int32_t max);

/* ---- Stage 0: runtime, pool, settings check, trace (shim/sp_core.cpp, shim/sp_trace.cpp) ---- */
SP_API int32_t sp_init(const sp_init_def *def);      /* SP_OK; SP_ERR_PARAM (workers != 0); SP_ERR_FULL (no memory); again = SP_OK */
SP_API int32_t sp_shutdown(void);                    /* frees the pool; worlds are the caller's to destroy first */
SP_API void *sp_pool_base(void);                     /* the pool's base (NULL before sp_init); moves only on a Mac grow (base_gen) */
SP_API int32_t sp_pool_alloc(int32_t bytes);         /* offset of a 16-byte-aligned block of >= bytes, or SP_ERR_FULL / SP_ERR_PARAM */
SP_API int32_t sp_pool_free(int32_t offset);         /* SP_OK, or SP_ERR_PARAM for an offset that is not a live block */
SP_API int32_t sp_pool_get_stats(sp_pool_stats *out);
/* Kit #8 (Law of the Vita, part 1; physics-plan-v2 Stage 7 item 1: "the pool in host.c like the audio pool"): the host hands the runtime ONE
 * block of memory before the engine's sp_init (the Vita GL eboot: a USER_RW memblock carved from the arena's share, vita/shepherd/
 * phys_host.inc.c). sp_init then takes the pool and B2's arena from that block instead of calloc: the pool = pool_bytes (0: a quarter of
 * the block, at least 256 KiB), the arena = b2_bytes (0: the rest of the block); more than the block = SP_ERR_FULL (the engine's loud
 * "FAIL phys capacity pool"); sp_shutdown keeps the block (never freed: the host owns it). Native only -- the host calls it, the managed
 * binding never does, so the ABI (version, struct sizes) is unchanged. No block registered (every Mac / QEMU build, the tests, an eboot
 * started with SHEPHERD_PHYS_POOL_MB=0): exactly the calloc path as before. Offsets are relative to the pool's base, so a trace replays
 * the same either way. Call before sp_init (after it: SP_ERR_PARAM); base NULL / bytes 0 = no block. */
SP_API int32_t sp_host_memory(void *base, uint32_t bytes);
/* the registered block (or NULL / 0): the host's END line */
SP_API int32_t sp_host_memory_info(void **base, uint32_t *bytes, uint32_t *pool_bytes, uint32_t *b2_bytes);
SP_API int32_t sp_nop(void);                         /* returns 0: the managed->native transition cost (physbench) */
/* compares the run-time-decoded settings with Box2D's compiled constants; writes up to max rows (SP_SET_COUNT); returns the number of
 * DIFFERS rows (CotL today: 1, SP_SET_MAX_ROTATION -- H-MAXROT) */
SP_API int32_t sp_settings_check(const sp_settings *s, sp_settings_row *rows, int32_t max);
/* the trace writer: every mutating call of this API (world / body / shape changes, steps with a post-step pose hash, event drains)
 * appended to a binary file, process-wide, until sp_trace_end; tests/t1_runner.c `replay` re-issues a trace into fresh worlds and
 * checks every returned handle and pose hash. path: UTF-8. Returns SP_OK, SP_ERR_PARAM (already tracing, no path), SP_ERR_FULL (open) */
SP_API int32_t sp_trace_begin(const char *path, int32_t flags);
SP_API int32_t sp_trace_end(void);                   /* closes the file; returns the number of records written (or SP_ERR_PARAM) */
SP_API int32_t sp_trace_note(int32_t tag, const int32_t *words, int32_t n);   /* a caller annotation (frame numbers, ...), n <= 64 */

/* ---- Stage 1 (physics-plan-v2 section 4 "Stage 1"; sections 1.2-1.6): the Unity layer ----
 * The core gains the Unity contact filter (section 1.8 steps 3-5: layer matrix, include / exclude, IgnoreCollision) and explicit mass
 * data, both traced. shim/sp_unity.cpp builds the rest on the traced C API (so a trace of an engine run replays without it): bodies
 * with a MIRROR record in the pool (section 1.3: the managed getters / setters read and write it, 0 native calls), shapes built from
 * the collider's own parameters under a collider-to-body affine (negative scale re-wound, circles scaled by the larger axis, polygons
 * decomposed into convex pieces of <= 8, capsules as rounded boxes, composite outlines as chain loops), the transform sync with
 * H-SYNC, the movement states (MovePosition -> velocity for one step, H-MOVEDRAG / H-MOVEVEL), the step with the history P[k-2..k],
 * write-back flags (C6), slot C's lerp and slot A's restore (H-INTERP, H-EARLY), and the render poses of section 1.6 (C2).
 * Every physics float is produced here (section 1.10); the managed side only copies them. Zero Box2D alterations. */

/* the Unity contact filter: a world with a matrix decides solid pairs by layers (Box2D's category / mask / group filter is then not
 * used); rows32 = 32 words, row i bit j = layers i and j collide; NULL = back to the stock filter. Every contact is re-filtered. */
SP_API int32_t sp_world_set_matrix(int32_t world, const uint32_t *rows32);
typedef struct sp_shape_unity {
    int32_t layer;          /* the collider's GameObject layer, 0..31 */
    uint32_t include_layers, exclude_layers;   /* collider | body masks (section 1.8 step 5; ties: exclude wins, H-TIE) */
    int32_t reserved;
} sp_shape_unity;
SP_API int32_t sp_shape_set_unity(int32_t world, int32_t shape, const sp_shape_unity *u);   /* re-filters its contacts */
SP_API int32_t sp_world_ignore_pair(int32_t world, int32_t shape_a, int32_t shape_b, int32_t ignore);   /* Physics2D.IgnoreCollision */
/* {mass, centre x, centre y, rotational inertia about the centre}: b2Body::SetMassData; NULL = ResetMassData (from the densities) */
SP_API int32_t sp_body_set_mass(int32_t world, int32_t body, const float *mass_cx_cy_i);
SP_API int32_t sp_body_get_mass(int32_t world, int32_t body, float *mass_cx_cy_i);   /* query (not traced) */

/* a Unity world: the Box2D world (gravity, the matrix, sleep on, warm start on, TOI off: every CotL body is Discrete) and a static
 * ground body at the origin that carries the colliders without a Rigidbody2D (in world space) */
typedef struct sp_uworld_def {
    float gravity_x, gravity_y;
    int32_t flags;          /* SP_WORLD_* (default SP_WORLD_DEFAULT_FLAGS) */
    int32_t use_matrix;     /* 1: the 32 rows below are the Unity filter */
    uint32_t matrix[32];
} sp_uworld_def;            /* 144 */
SP_API int32_t sp_uworld_create(const sp_uworld_def *def);           /* the world handle */
SP_API int32_t sp_uworld_destroy(int32_t world);
SP_API int32_t sp_uworld_ground(int32_t world);                      /* the ground body's handle */

/* the mirror record (section 1.3), one per Unity body, in the pool: sp_pool_base() + sp_ubody_mirror(); 160 bytes (128 until Stage 3) */
typedef struct sp_mirror {
    float x, y, angle;      /* the true pose (after the last step / sync / teleport); a position / rotation set writes it here */
    float vx, vy, w;        /* the velocity after the last step; a velocity set writes it here (with SP_MF_SET_VEL) */
    float tx, ty, ta;       /* MovePosition / MoveRotation targets (SP_MF_MOVE_POS / SP_MF_MOVE_ROT): the last call wins */
    int32_t flags;          /* SP_MF_* */
    float hx[3], hy[3], ha[3];   /* the history P[k-2..k] (index 2 = the newest step) */
    int32_t hstep[3];       /* the step index of each history entry (-1: none yet) */
    float ix, iy, ia;       /* slot C's interpolated (or extrapolated) pose */
    float wx, wy, wa;       /* the 2D pose physics last wrote to or synced from the Transform (C6) */
    int32_t body;           /* the Box2D body handle */
    int32_t serial;         /* the managed creation serial */
    int32_t mode;           /* SP_MM_*: interpolation, body type, simulated */
    int32_t user;
    /* Stage 3 */
    float fx, fy, torque;   /* AddForce / AddTorque (ForceMode2D.Force) accumulators (with SP_MF_FORCE): applied by the next step, then 0 --
                               a force added outside FixedUpdate waits for the next step; ticks without a step carry it (plan section 3) */
    float inv_mass, inv_i;  /* native: the body's inverse mass and rotational inertia (0 unless Dynamic), for the impulse modes */
    float pvx, pvy, pw;     /* native: the velocity at the start of the last step, after its moves and sets (H-RELVEL=pre-solve) */
} sp_mirror;
enum {   /* sp_mirror.flags. Managed sets: MOVE_POS, MOVE_ROT, TELEPORT, SET_VEL. Native sets / managed clears: WRITE, ANGLE, POSE */
    SP_MF_MOVE_POS = 1,     /* tx, ty: applied by the next step as a velocity (section 1.2 item 2), then cleared */
    SP_MF_MOVE_ROT = 2,     /* ta */
    SP_MF_TELEPORT = 4,     /* x, y, angle were set (rb.position / rotation): SetTransform + wake (H-WAKETP) + FORCE_WRITE + history reset */
    SP_MF_SET_VEL = 8,      /* vx, vy, w were set: SetLinearVelocity / SetAngularVelocity at the next flush */
    SP_MF_WRITE = 16,       /* write-back: x / y / angle differ bitwise from the last written pose, or FORCE_WRITE was set */
    SP_MF_ANGLE = 32,       /* with WRITE: the angle changed too (the Transform's rotation is written) */
    SP_MF_FORCE_WRITE = 64, /* a body-side teleport: written back after the next step even if the pose did not change (C6) */
    SP_MF_POSE = 128,       /* slot C / slot A: ix..ia (C) or x..angle (A) are to be written to the Transform */
    SP_MF_AWAKE = 256,      /* native: the body was awake after the last step */
    SP_MF_FORCE = 512       /* Stage 3: fx, fy, torque hold forces for the next step (ApplyForceToCenter / ApplyTorque with a wake) */
};
enum { SP_MM_INTERP_MASK = 3, SP_MM_NONE = 0, SP_MM_INTERPOLATE = 1, SP_MM_EXTRAPOLATE = 2,
       SP_MM_TYPE_SHIFT = 4,   /* bits 4..5: SP_BODY_* */
       SP_MM_SIMULATED = 64 };

typedef struct sp_ubody_def {
    int32_t type;           /* SP_BODY_* (Box2D's numbers; Unity's Dynamic 0 / Kinematic 1 / Static 2 are mapped by the binding) */
    int32_t flags;          /* SP_UB_* */
    float x, y;             /* the transform's world position */
    float m00, m10;         /* the transform's world x axis: the angle is atan2(m10, m00), computed here (sp_atan2f) */
    float mass, linear_drag, angular_drag, gravity_scale;
    int32_t interpolation;  /* RigidbodyInterpolation2D: 0 None, 1 Interpolate, 2 Extrapolate */
    int32_t serial, user, reserved;
} sp_ubody_def;             /* 56 */
enum { SP_UB_SIMULATED = 1, SP_UB_NEVER_SLEEP = 2, SP_UB_START_ASLEEP = 4, SP_UB_FREEZE_ROTATION = 8, SP_UB_AUTO_MASS = 16,
       SP_UB_FREEZE_X = 32, SP_UB_FREEZE_Y = 64 };
SP_API int32_t sp_ubody_create(int32_t world, const sp_ubody_def *def);   /* the body handle */
SP_API int32_t sp_ubody_destroy(int32_t world, int32_t body);
SP_API int32_t sp_ubody_mirror(int32_t world, int32_t body);              /* the pool offset of its sp_mirror */
/* the record changed (type, simulated, sleep mode, constraints, mass, drags, gravity scale, interpolation; x / y / m00 / m10 ignored) */
SP_API int32_t sp_ubody_set(int32_t world, int32_t body, const sp_ubody_def *def);
SP_API int32_t sp_ubody_mass(int32_t world, int32_t body);                /* Unity's mass data after the body's shapes changed */

/* a collider's shape(s) on a body (the ground body for a collider without a Rigidbody2D) */
enum { SP_US_CIRCLE = 0, SP_US_BOX = 1, SP_US_POLYGON = 2, SP_US_EDGE = 3, SP_US_CAPSULE = 4, SP_US_LOOP = 5 };
enum { SP_USF_SENSOR = 1, SP_USF_ADJ_START = 2, SP_USF_ADJ_END = 4,
       SP_USF_ONE_SIDED = 8,     /* Stage 3: H-CHAIN=one-sided for an edge / loop (SP_SHAPE_ONE_SIDED) */
       SP_USF_CHAIN_WELD = 16,   /* Stage 4a: Unity 2022.3's chain vertex filter on an edge / loop, in body space after the affine: a point
                                    within the threshold of the last point kept is dropped (the first is always kept; no wrap test on a
                                    loop); keep when |d|^2 > T2, T2 = b2_linearSlop^2 for a loop (a CompositeCollider2D outline), 2.01 x
                                    b2_linearSlop^2 for an open chain (an EdgeCollider2D); fewer than 3 (loop) / 2 (edge) left: no shape.
                                    The E1 oracle's Paths4a2 scene, physics-work/e1/results/s4a; vita/notes/physics-stage4.md */
       SP_USF_UNITY_POLY = 32 }; /* Stage 4b (SP_US_POLYGON only): xy is ONE piece of Unity's convex decomposition, already in body space
                                    (the engine decomposed the collider's paths: libtess2 polygons of <= 8 vertices); no affine, no offset,
                                    no native decomposition. Unity 2022.3's polygon build (the E1 oracle's Paths4b* scenes, physics-work/
                                    e1/results/s4b): Unity's weld (a point whose squared distance to a point already kept is below
                                    (0.5 b2_linearSlop)^2 is dropped, in order), Box2D's gift-wrapping hull, then Unity's collinear
                                    hull-vertex removal (in hull order from the rightmost-lowest vertex: a vertex whose unit edge vectors
                                    have a dot product above 0.99999 goes, the next one is tested in its place, while more than 3 remain);
                                    the fixture's Set welds with SP_SHAPE_UNITY_WELD. Fewer than 3 points after the weld or the hull: no
                                    fixture. radius > 0: b2_polygonRadius + radius (body space, unscaled) */
typedef struct sp_ushape_def {
    int32_t kind;           /* SP_US_* */
    int32_t flags;          /* SP_USF_* */
    float body2[4];         /* the body transform's world 2x2 (m00 m01 m10 m11); identity for the ground body */
    float rel[6];           /* collider space -> body transform space (x' = r0 x + r1 y + r2, y' = r3 x + r4 y + r5); for the ground
                               body: collider space -> world */
    float ox, oy;           /* the collider's offset (collider space) */
    float radius;           /* circle radius; box / polygon / edge / loop: the edge radius (0 = stock skin) */
    float sx, sy;           /* box / capsule size */
    int32_t direction;      /* capsule: 0 vertical, 1 horizontal */
    float density, friction, restitution;
    int32_t layer;
    uint32_t include_layers, exclude_layers;
    float adj[4];           /* edge: the adjacent start / end points (collider space) */
    int32_t vertex_count;   /* points in xy (collider space): polygon path, edge points, loop path */
    int32_t user;
} sp_ushape_def;            /* 120 */
/* builds the shape(s) under the affine body-space = R(-angle) * body2 * rel and creates the fixtures (traced sp_shape_create);
 * returns the number of fixtures (a concave polygon decomposes into several), their handles in out[0..min(n, max)) */
SP_API int32_t sp_ushape_create(int32_t world, int32_t body, const sp_ushape_def *def, const float *xy, int32_t *out, int32_t max);

/* the transform sync (section 1.5): the dirty body transforms' 2D poses. H-SYNC (mode): 0 = A (a pose within 1e-5 u / 1e-6 rad of the
 * last written one is not a teleport), 1 = B (every write teleports), 2 = C (B; slot A's restore wins first: the caller's order).
 * A teleport: SetTransform + wake (H-WAKETP) + history reset; the last written pose becomes the synced one (no forced write-back). */
typedef struct sp_usync { int32_t body; int32_t flags; float x, y, m00, m10; } sp_usync;   /* 24 */
SP_API int32_t sp_u_sync(int32_t world, const sp_usync *recs, int32_t n, int32_t mode);     /* returns the teleports */

typedef struct sp_ustep_def {
    float dt;
    int32_t velocity_iterations, position_iterations;
    int32_t step_index;     /* the fixed step's number (history stamps) */
    int32_t flags;          /* SP_USTEP_* (hypothesis variants; 0 = every default) */
    int32_t reserved;
} sp_ustep_def;             /* 24 */
enum { SP_USTEP_MOVEVEL_KEEP = 1, SP_USTEP_MOVEVEL_ZERO = 2,   /* H-MOVEVEL: restore (0) / keep / zero */
       SP_USTEP_MOVEDRAG_ON = 4,                               /* H-MOVEDRAG: off (0: no gravity / drag during a move) / on */
       SP_USTEP_WAKETP_STOCK = 8,                              /* H-WAKETP: wake (0) / stock (a teleport does not wake) */
       SP_USTEP_MOVEGRAV_ON = 64,                              /* E1, H-MOVEDRAG=drag-off-gravity-on: a move step keeps gravity, drag off
                                                                  (with 0 and not MOVEDRAG_ON: neither -- the plan's old `off`) */
       /* Stage 3, H-DAMP: Pade (0: Box2D's v *= 1 / (1 + h c)) / exp (v *= exp(-h c)) / linear (v *= max(0, 1 - h c)): for the step, each
        * body's Box2D damping is set to the c' with 1 / (1 + h c') = the variant's factor, and restored after (no Box2D alteration) */
       SP_USTEP_DAMP_EXP = 16, SP_USTEP_DAMP_LINEAR = 32 };
typedef struct sp_ustep_out {
    int32_t step, moved, teleported, written, awake, bodies, contacts, touching;
} sp_ustep_out;             /* 32 */
SP_API int32_t sp_u_flush(int32_t world, int32_t flags);   /* applies the mirror's pending teleports / velocity sets now */
SP_API int32_t sp_u_step(int32_t world, const sp_ustep_def *def, sp_ustep_out *out);
/* slot C (H-INTERP): ix..ia of every Interpolate / Extrapolate body from P[k-1], P[k] and *alpha = (time - fixedTime) / fdt; the last
 * written pose becomes it; SP_MF_POSE set. Returns the bodies flagged. */
SP_API int32_t sp_u_interp(int32_t world, const float *alpha_dt, int32_t flags);   /* alpha_dt = {alpha, fixed dt} */
/* slot A (H-EARLY): the interpolated bodies back to their true pose: the last written pose = x..angle; SP_MF_POSE set */
SP_API int32_t sp_u_early(int32_t world, int32_t flags);

/* render poses (section 1.6, C2): sp_u_snapshot at the end of a logic tick copies every non-static body's P[k-2..k] (and velocity)
 * into the world's snapshot; sp_render_poses evaluates the rule at a render time from the snapshot only (never the live mirror):
 * j = k + j_rel (j_rel 0 or -1, the last step with T_j <= t_r) and alpha = (t_r - T_j) / fdt; Interpolate: lerp(P[j-1], P[j], alpha);
 * None: P[j]; Extrapolate: P[j] + v * alpha * fdt. A request outside the served window (j_rel not 0 / -1, alpha outside [0, 1]) is
 * clamped to it and counted (the return value's high bit is set). */
SP_API int32_t sp_u_snapshot(int32_t world);
typedef struct sp_render_def { int32_t j_rel; float alpha, fdt; int32_t flags; } sp_render_def;   /* 16 */
typedef struct sp_rpose { int32_t body, serial; float x, y, angle; int32_t mode; } sp_rpose;       /* 24 */
SP_API int32_t sp_render_poses(int32_t world, const sp_render_def *def, sp_rpose *out, int32_t max);

/* B2's arena (b2Alloc / b2Free): the same 40-byte stats as the pool's; failures = allocations that fell back to malloc */
SP_API int32_t sp_b2_get_stats(sp_pool_stats *out);
SP_API float sp_atan2f(float y, float x);   /* own deterministic atan2 (shim/sp_trig.c), for the transform's angle */

/* ---- Stage 2 (physics-plan-v2 section 4 "Stage 2", sections 1.2 P1, 1.7, 1.8, 2 "Stage 2"): triggers, events, queries ----
 * Events: with SP_WORLD_CONTACT_SERIALS each b2Contact gets a creation serial (1, 2, ... per world, in creation order: assigned after
 * every step from Box2D's contact list, newest first, so the serials of the contacts one step created follow their creation order);
 * sp_events2 drains the same queue as sp_events with the serial and the chain children. sp_events and sp_events2 are two views of
 * one queue: a caller uses one of them. */
typedef struct sp_event2 {
    int32_t kind;           /* SP_EV_BEGIN / SP_EV_END */
    int32_t flags;          /* SP_EVF_* */
    int32_t shape_a, shape_b;
    int32_t step;
    int32_t serial;         /* the contact's creation serial (0 without SP_WORLD_CONTACT_SERIALS) */
    int32_t child_a, child_b;
} sp_event2;                /* 32 */
SP_API int32_t sp_events2(int32_t world, sp_event2 *out, int32_t max);

/* Unity-shaped queries (shim/sp_query.cpp; not traced: they change nothing). Overlap and point queries walk b2BroadPhase directly
 * (PhysBench finding 5): one test per proxy, so a chain tests only the touched edge; results are de-duplicated per collider (a
 * shape's sp_shape_def.user = the collider's instance id) in a scratch block the world keeps (no allocation per call); an "any" query
 * stops at the first hit. Rays use b2World::RayCast (rounded shapes -- capsules, edge radii -- by the cast sweep); shape casts are an
 * analytic sweep (H-CASTSLOP: conservative advancement on b2Distance, the exact shapes without Box2D's b2_polygonRadius skin); overlaps
 * keep the skin (H-QSKIN: b2TestOverlap). Filters: the query's layer mask against the shape's layer (sp_shape_set_unity), sensors only
 * with SP_UQF_TRIGGERS, one excluded collider. Depth and normal-angle filters are the caller's (managed: transform z). Order: overlaps
 * in broadphase order (H-OVERLAPORDER: tree order); *All casts by distance, ties in broadphase / report order. */
enum { SP_UQ_RAY = 0, SP_UQ_CIRCLE_CAST = 1, SP_UQ_BOX_CAST = 2, SP_UQ_CAPSULE_CAST = 3,
       SP_UQ_POINT = 4, SP_UQ_CIRCLE = 5, SP_UQ_BOX = 6, SP_UQ_CAPSULE = 7 };
enum { SP_UQF_ALL = 1,          /* every hit (de-duplicated per collider); else the closest (casts) / the first (overlaps) */
       SP_UQF_TRIGGERS = 2,     /* sensors take part (ContactFilter2D.useTriggers) */
       SP_UQF_START_IN = 4,     /* queriesStartInColliders: a cast / ray starting inside or overlapping reports it at distance 0 */
       SP_UQF_ANY = 8,          /* stop at max hits (an "any hit" query with max 1) */
       SP_UQF_SORT = 16,        /* SP_UQF_ALL casts: sort by distance (stable) */
       SP_UQF_BRUTE = 32 };     /* check mode (SHEPHERD_CHECK_PHYS=brute): every fixture child instead of the broadphase */
typedef struct sp_uquery {
    int32_t kind;           /* SP_UQ_* */
    int32_t flags;          /* SP_UQF_* */
    uint32_t layer_mask;    /* tested against the shape's Unity layer */
    int32_t exclude_user;   /* a collider (instance id) to skip; 0 = none */
    float ox, oy;           /* origin / point / centre */
    float dx, dy;           /* cast / ray direction (normalised here); zero = an overlap test at the origin */
    float distance;         /* cast / ray length (a non-finite or huge one is clamped to the world's bounds) */
    float radius;           /* circle / capsule radius (capsule: half its narrow side, derived from sx, sy when 0) */
    float sx, sy;           /* box / capsule size */
    float angle;            /* box / capsule angle, radians */
    int32_t direction;      /* capsule: 0 vertical, 1 horizontal */
} sp_uquery;                /* 56 */
typedef struct sp_uhit {
    int32_t shape;          /* the fixture handle that was hit (one of the collider's) */
    int32_t user;           /* the collider (sp_shape_def.user) */
    float cx, cy;           /* centroid: the probe's centre at impact (rays: the point) */
    float px, py;           /* the point on the collider's surface */
    float nx, ny;           /* the surface normal there */
    float distance, fraction;
} sp_uhit;                  /* 40 */
/* returns the number of hits written (<= max); SP_ERR_* on a bad call */
SP_API int32_t sp_u_query(int32_t world, const sp_uquery *q, sp_uhit *out, int32_t max);
/* the world AABB of a collider's shapes (their children), Unity's bounds: no Box2D skin, the collider's own rounding kept
 * (H-BOUNDSSKIN); out = {min x, min y, max x, max y}; returns the number of live shapes */
SP_API int32_t sp_u_bounds(int32_t world, const int32_t *shapes, int32_t n, float *out);
/* the point of a collider's shapes closest to p (p itself when a shape contains it); out = {x, y, distance}; returns 1, or 0 when none */
SP_API int32_t sp_u_closest(int32_t world, const int32_t *shapes, int32_t n, const float *p, float *out);
/* does any of the shapes contain p (b2Fixture::TestPoint: no skin; chains never contain)? 1 / 0 */
SP_API int32_t sp_u_test_point(int32_t world, const int32_t *shapes, int32_t n, const float *p);
/* the shapes touching any of `shapes` in the current contact list (the last step's), with SP_CONTACTS_* flags (touching only is implied;
 * SP_CONTACTS_NO_SENSORS); writes up to max other-shape handles (a pair once per contact); returns the count written */
SP_API int32_t sp_u_contacts_of(int32_t world, const int32_t *shapes, int32_t n, int32_t flags, int32_t *other, int32_t max);

/* the exact distance between two colliders' shapes (no Box2D skin, their own rounding kept; negative when their rounded parts overlap,
 * -(ra + rb) when their cores do); returns 1, 0 when none */
SP_API int32_t sp_u_distance(int32_t world, const int32_t *a, int32_t na, const int32_t *b, int32_t nb, float *out);
/* the colliders overlapping a collider's shapes (Physics2D.OverlapCollider), with the query's filter (q->kind is ignored); returns the
 * total found, writing up to max */
SP_API int32_t sp_u_overlap_shapes(int32_t world, const int32_t *shapes, int32_t n, const sp_uquery *q, sp_uhit *out, int32_t max);

/* check mode (SHEPHERD_CHECK_PHYS=brute): the touching sensor pairs against a brute-force evaluation without the broadphase;
 * out[6] = {core pairs, brute pairs, missing, extra, first difference's shape a, shape b}; returns the differences */
SP_API int32_t sp_u_brute_pairs(int32_t world, int32_t *out);

/* ---- Stage 3 (physics-plan-v2 section 4 "Stage 3", sections 1.8 "Collision2D", 3 "Forces"): combat ----
 * The mirror's Stage 3 words (forces, inverse mass, pre-step velocity) are documented with sp_mirror; frozen position axes (SP_UB_FREEZE_X /
 * Y) hold a Dynamic body's coordinate through the step (its velocity component is zeroed before and after, the coordinate restored after:
 * a Unity-layer rule, no Box2D alteration; UNVERIFIED against Unity's solver). sp_u_contacts_between is the contact snapshot of one
 * collider pair after a step, for Collision2D: every touching, non-sensor contact between a fixture of set a and a fixture of set b, in
 * set a's fixture order then the body's contact-edge order (deterministic), one record per manifold point. The normal points from a to b;
 * the relative velocity is b's point velocity minus a's (pre-solve: the bodies' velocities at the start of the step, the mirror's pv*;
 * SP_UCF_POST_SOLVE: their velocities now); rel2 = b's linear velocity minus a's, the same timing. Not traced (nothing changes). */
/* the mirror's force / impulse arithmetic, natively (section 1.10: every physics float is produced here; the glue copies): kinds
 * SP_APPLY_FORCE {x, y} / SP_APPLY_TORQUE {t} add to the accumulators (SP_MF_FORCE; the binding copies the first force of a step itself, 0
 * calls, and calls this only for a second one); SP_APPLY_IMPULSE {x, y} / SP_APPLY_ANGULAR_IMPULSE {t} change the mirror's velocity now
 * (v += inv_mass J, w += inv_i t; SP_MF_SET_VEL, applied at the next flush) for a simulated Dynamic body, as Box2D's ApplyLinearImpulse at the
 * centre of mass does at once (Unity's ForceMode2D.Impulse); others are ignored. Not traced: the step / flush apply them through the
 * traced API. Returns SP_OK, SP_ERR_HANDLE / SP_ERR_PARAM. */
SP_API int32_t sp_u_apply(int32_t world, int32_t body, int32_t kind, const float *args);
typedef struct sp_ucontact {
    int32_t shape_a, shape_b;   /* the fixtures (shape_a from set a) */
    float px, py;               /* the world point (b2WorldManifold) */
    float nx, ny;               /* the world normal, from a to b */
    float separation, normal_impulse, tangent_impulse;   /* the last solve's impulses */
    float rvx, rvy;             /* b's velocity at the point minus a's (H-RELVEL) */
    int32_t flags;              /* SP_UCPF_* */
} sp_ucontact;                  /* 48 */
enum { SP_UCPF_ENABLED = 1 };
enum { SP_UCF_POST_SOLVE = 1 };
/* returns the number of points found (records written: min(that, max)); rel2 may be NULL */
SP_API int32_t sp_u_contacts_between(int32_t world, const int32_t *a, int32_t na, const int32_t *b, int32_t nb, int32_t flags,
                                     sp_ucontact *out, int32_t max, float *rel2);

/* ---- Stage 6 (physics-plan-v2 section 4 "Stage 6", section 2 "Stage 6"; shim/sp_mesh3d.cpp): the 3D island-ground ray ----
 * A 3D scene holds static colliders only (CotL has no 3D simulation on the main path: plan section 3 "3D"). Its geometry is PhysX's
 * (Unity 2022.3's 3D physics), modelled from memory -- DOCUMENTED, not verified against PhysX (the editor oracle E1 can):
 *  - a collider is the transform's world pose (p, q) and lossy scale; a MeshCollider's triangles stay in the mesh's own (vertex) space
 *    with the scale as PhysX's PxMeshScale (vertex2world = diag(scale), world2vertex its PxMat33 inverse); Box / Sphere / Capsule are
 *    analytic (size x |scale|, radius x the largest |scale| component of the cross-section, the capsule along its direction axis);
 *  - a ray goes into vertex space as PhysX's raycast_triangleMesh does (q.rotateInv(o - p), then world2vertex), meets triangles by
 *    PhysX's Moller-Trumbore (intersectRayTriangle: det > 1e-6 with backface culling, u / v / t by one reciprocal of det), the closest t
 *    wins (ties: the lower triangle index, then the earlier collider); the hit point is PhysX's RayMeshColliderCallback's
 *    pose.transform(vertex2world x ((1 - u - v) p0 + u p1 + v p2)) -- or, with SP_R3F_POINT_RAY (H-RAYPOINT=ray), origin + t dir;
 *  - a ray starting inside a Box / Sphere / Capsule does not report it (Unity's documented Physics.Raycast rule); meshes have no inside;
 *  - backfaces: culled unless SP_R3F_BACKFACES (Physics.queriesHitBackfaces); the culling is in vertex space, flipped under a scale with
 *    a negative determinant (the world front face is the front); the normal is the triangle's own (single-sided meshes);
 *  - triggers take part only with SP_R3F_TRIGGERS; the layer mask is tested against the collider's layer (1 << layer).
 * Two traversals with one result: the general path (the scene's colliders in order; per mesh a BVH over its triangles in vertex space)
 * and the +z fast path (a ray whose direction is (0, 0, +): per mesh collider, after the same eligibility and world-box cull, a world-XY
 * grid over the collider's triangles a +z ray can hit with culling -- world normal z <= 0 --, each candidate through the same per-collider
 * ray and triangle test). Both take the minimum of (t, collider order, triangle index) over the triangles whose test passes, so they agree
 * bit for bit whenever a grid's candidates include every passing triangle (cells cover each triangle's inflated world AABB;
 * SHEPHERD_CHECK_PHYS=ray3d and S6 count the disagreements). A ray whose mask meets no layer present in the scene returns 0 at once (the
 * layers-present early-out).
 * Memory: Box2D's arena (b2Alloc / b2Free, B2); nothing is allocated inside a ray except a mesh collider's grid, built at the first +z ray
 * after the collider or its mesh changed. Traced
 * (scene / mesh / collider calls with their data; every ray with its query and a hash of its hits): an engine run's trace replays the
 * 3D scene bit for bit on QEMU's Cortex-A9. */
enum { SP_C3_BOX = 1, SP_C3_SPHERE = 2, SP_C3_CAPSULE = 3, SP_C3_MESH = 4 };
enum { SP_C3F_TRIGGER = 1 };
typedef struct sp_mesh3d_def {
    int32_t vertex_count;   /* xyz: 3 floats each, the mesh's own space */
    int32_t triangle_count; /* tri: 3 vertex indices each (every triangle submesh, in order) */
    int32_t flags, reserved;
} sp_mesh3d_def;            /* 16 */
typedef struct sp_col3d_def {
    int32_t kind;           /* SP_C3_* */
    int32_t flags;          /* SP_C3F_* */
    int32_t layer;          /* the GameObject's layer, 0..31 */
    int32_t user;           /* the collider's instance id (reported in hits) */
    int32_t mesh;           /* SP_C3_MESH: an sp_mesh3d handle of this scene (0 = none: never hit) */
    int32_t direction;      /* capsule: 0 X, 1 Y, 2 Z */
    float px, py, pz;       /* the transform's world position */
    float qx, qy, qz, qw;   /* the transform's world rotation */
    float sx, sy, sz;       /* the transform's lossy scale */
    float cx, cy, cz;       /* box / sphere / capsule: the centre (collider space, scaled by the transform) */
    float ex, ey, ez;       /* box: size; sphere: radius in ex; capsule: radius ex, height ey */
} sp_col3d_def;             /* 88 */
typedef struct sp_ray3d_query {
    float ox, oy, oz;       /* origin */
    float dx, dy, dz;       /* direction (unit: Unity normalises it before the icall) */
    float max_distance;     /* a non-finite or larger value is FLT_MAX */
    uint32_t layer_mask;
    int32_t flags;          /* SP_R3F_* */
    int32_t reserved;
} sp_ray3d_query;           /* 40 */
enum { SP_R3F_TRIGGERS = 1,     /* trigger colliders take part (queriesHitTriggers / QueryTriggerInteraction) */
       SP_R3F_BACKFACES = 2,    /* triangles facing away take part (queriesHitBackfaces) */
       SP_R3F_ALL = 4,          /* one hit per collider (its closest), up to max, in collider order (RaycastNonAlloc / RaycastAll) */
       SP_R3F_SORT = 8,         /* with SP_R3F_ALL: sorted by distance (ties: collider order) */
       SP_R3F_ANY = 16,         /* any hit: stop at the first found (RaycastTest); the hit written is that one */
       SP_R3F_POINT_RAY = 32,   /* H-RAYPOINT=ray: a mesh hit's point is origin + t dir instead of PhysX's pose(scale(barycentric)) */
       SP_R3F_GENERAL = 64,     /* check modes: never the +z fast path (the BVH) */
       SP_R3F_BRUTE = 128 };    /* check modes: every triangle of every eligible collider (no BVH, no grid, no world-box cull) */
typedef struct sp_ray3d_hit {
    float px, py, pz;       /* RaycastHit.point */
    float nx, ny, nz;       /* RaycastHit.normal (unit) */
    float distance;         /* RaycastHit.distance (t) */
    float u, v;             /* barycentric (RaycastHit.barycentricCoordinate = (1 - u - v, u, v)); 0 for primitives */
    int32_t face;           /* the triangle index (submeshes concatenated); -1 for primitives */
    int32_t user;           /* the collider (sp_col3d_def.user) */
    int32_t col;            /* the collider handle (not part of the traced hash) */
} sp_ray3d_hit;             /* 48 */
SP_API int32_t sp_scene3d_create(int32_t flags);                           /* a scene handle */
SP_API int32_t sp_scene3d_destroy(int32_t scene);                          /* its colliders and meshes go with it */
/* a mesh (its BVH built here); triangles with an index out of range are SP_ERR_PARAM; returns the mesh handle */
SP_API int32_t sp_mesh3d_create(int32_t scene, const sp_mesh3d_def *def, const float *xyz, const int32_t *tri);
SP_API int32_t sp_mesh3d_destroy(int32_t scene, int32_t mesh);             /* colliders still naming it are never hit afterwards */
SP_API int32_t sp_col3d_create(int32_t scene, const sp_col3d_def *def);    /* a collider handle (creation order = the tie order) */
SP_API int32_t sp_col3d_set(int32_t scene, int32_t col, const sp_col3d_def *def);   /* pose / shape / layer / mesh changed */
SP_API int32_t sp_col3d_destroy(int32_t scene, int32_t col);
/* the closest hit (or SP_R3F_ALL / SP_R3F_ANY): returns the number of hits found (written: min(that, max)); SP_ERR_* on a bad call */
SP_API int32_t sp_ray3d(int32_t scene, const sp_ray3d_query *q, sp_ray3d_hit *out, int32_t max);
/* counters (not traced): out[16] = {colliders, meshes, triangles, rays, early-outs (layers absent), fast-path rays, general rays,
 * triangle tests, primitive tests, grid builds, grid cells, grid entries (both summed over the colliders' current grids), layers present
 * (bits), BVH nodes, hits, reserved} */
SP_API int32_t sp_scene3d_stats(int32_t scene, int32_t *out16);

/* every entry above by name (NULL if unknown); the binding resolves them once */
SP_API void *shepphys_proc(const char *name);

/* own deterministic trig (shim/sp_trig.c); Box2D's sinf/cosf are routed here in SHEPPHYS_TRIG=own builds */
SP_API float sp_sinf(float x);
SP_API float sp_cosf(float x);

#ifdef __cplusplus
}
#endif
#endif /* SHEPPHYS_H */
