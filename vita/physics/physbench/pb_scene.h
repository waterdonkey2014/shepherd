/* pb_scene.h -- PhysBench's synthetic scenes (ours). Private hobby port (Shepherd).
 *
 * The plan's representative scenes (physics-plan.md section 6.1; scene-data.md section 7), synthesized from COUNTS only:
 * no game data, no game geometry, no layer matrix. Shapes, sizes and role filters are generic top-down-action stand-ins
 * chosen so the body / shape / pair mix resembles the census (the build logs the pair counts it actually produced).
 *
 *   intro_fight        Intro-Room-1-shaped fight: a 49-vertex outline loop on a kinematic room body, 79 statics (43 circles
 *                      of which 40 sensors, 36 boxes of which 27 sensors), the Lamb (3 circles), 19 MovePosition units,
 *                      12 dead bodies kicked now and then, 5 teleported hitbox sensors, a swipe sensor toggled on and off
 *   intro_fight_stock  the same with stock Box2D kinematic sensor bodies (no kinematic x static sensor pairs): the cost of the
 *                      plan's patch 2 (H-KINTRIG) is the difference to intro_fight
 *   dungeon_room       a typical dungeon combat room: 64-vertex outline, 4 doors x 5 colliders, 20 decoration triggers,
 *                      8 units + 8 hitboxes (half active at a time), 2 dead bodies, 3 projectiles, the Lamb + swipe
 *   base_never         Base-Biome-1-shaped: 3 island loops (170 / 49 / 42 vertices) + an 8-loop obstacle composite
 *                      (145 edges), 337 statics incl. 3 huge volume triggers, 4 never-moving "CustomTransform" roots carrying
 *                      97 trigger circles (H-KINSLEEP = never: the roots never sleep), 16 wandering NPCs, the Lamb
 *   base_sleep         the same with the roots allowed to sleep (H-KINSLEEP = sleep)
 *
 * Kinematic SENSOR-ONLY bodies (hitboxes, swipe, projectiles, base roots) are built, except in intro_fight_stock, as
 * zero-velocity DYNAMIC bodies (gravity 0, fixed rotation, sensors only): stock Box2D 2.3.1 then creates the
 * kinematic x static / kinematic x kinematic sensor pairs the plan's patch 2 (H-KINTRIG = all but static-static) will
 * create, without altering a Box2D file. Sensor-only fixtures receive no impulses, so these bodies move exactly as
 * scripted (teleports / set velocities). The solid island outline stays a true kinematic body.
 *
 * Determinism: integer xorshift RNG, float + - * /, sqrtf and sp_sinf / sp_cosf only (compiled with -ffp-contract=off),
 * so a scene's pose hash must be the same on the Mac, on QEMU's Cortex-A9 and on the Vita.
 * No libc formatting or time here: this file also runs bare metal (qemu/pb_qemu.c).
 */
#ifndef PB_SCENE_H
#define PB_SCENE_H

#include <stdint.h>
#include "shepphys.h"

#ifdef __cplusplus
extern "C" {
#endif

enum { PB_SC_INTRO = 0, PB_SC_INTRO_STOCK = 1, PB_SC_DUNGEON = 2, PB_SC_BASE_NEVER = 3, PB_SC_BASE_SLEEP = 4, PB_SC_N = 5 };

#define PB_DT 0.02f            /* CotL's fixedDeltaTime */
#define PB_WARMUP_STEPS 50
#define PB_MEASURE_STEPS 500

/* role categories (b2Filter category bits; masks are built symmetric from a pair list in pb_scene.c) */
enum {
    PB_R_PLAYER = 1 << 0, PB_R_UNIT = 1 << 1, PB_R_DEAD = 1 << 2, PB_R_WEAPON = 1 << 3, PB_R_WALL = 1 << 4,
    PB_R_OBST = 1 << 5, PB_R_EWALL = 1 << 6, PB_R_GRASS = 1 << 7, PB_R_PROP = 1 << 8, PB_R_VOLUME = 1 << 9,
    PB_R_DOOR = 1 << 10, PB_R_SCENERY = 1 << 11, PB_R_DECOR = 1 << 12, PB_R_NPC = 1 << 13, PB_R_PBLOCK = 1 << 14
};
uint32_t pb_role_mask(uint32_t role);

/* the backend a scene is built in: libshepphys's C API (pb_shim_ops) or Box2D directly (pb_b2.cpp) */
typedef struct pb_ops {
    int32_t (*body)(void *u, const sp_body_def *d);                         /* > 0 handle, else error */
    int32_t (*shape)(void *u, int32_t body, const sp_shape_def *d, const float *xy);
    void (*pose)(void *u, int32_t body, float *xyavvw);                      /* x y angle vx vy w */
    void (*vel)(void *u, int32_t body, float vx, float vy);                  /* MovePosition-style velocity */
    void (*xform)(void *u, int32_t body, float x, float y, float a);         /* teleport + wake (plan patch 4 behaviour) */
    void (*active)(void *u, int32_t body, int32_t on);                       /* SetActive */
    void (*step)(void *u);                                                   /* one 0.02 s step, 8 / 3 iterations */
} pb_ops;

#define PB_MAX_BODIES 128

typedef struct pb_scene {
    int32_t id;
    const pb_ops *ops;
    void *u;
    uint32_t rng;
    int32_t steps;              /* steps taken so far */
    int32_t status;             /* 0, or the first error met while building */
    int32_t body[PB_MAX_BODIES];
    int32_t nbody;              /* every body, creation order (hash order) */
    int32_t ground, island, lamb, swipe, swipe_on;
    int32_t unit[24];
    uint8_t ukind[24];
    int32_t nunit;
    int32_t hit[16], hit_owner[16], nhit, hit_toggle;
    int32_t dead[16], ndead;
    int32_t proj[4], nproj;
    float proj_v[4][2];
    int32_t npc[20], nnpc, npc_timer[20];
    float npc_t[20][2], npc_speed[20];
    int32_t root[4], nroot;
    float face_x, face_y;       /* the Lamb's facing (for the swipe) */
    float cx, cy, ax, ay;       /* room centre; the Lamb's path amplitude */
    float bx0, by0, bx1, by1;   /* outline bounds */
    /* A* grid shaped like GenerateRoom.SetAStar's (node 0.5, Margin 5) or the base graph (150 x 150) */
    float gx0, gy0, gstep, gr;
    int32_t gw, gh;
    uint32_t gmask;
    /* counts (what was built) */
    int32_t n_shapes, n_sensors, n_static, n_on_bodies, n_chain_edges, n_dyn, n_kin, n_emul;
} pb_scene;

const char *pb_scene_name(int32_t id);
/* builds scene id through ops (u = the backend's world); returns s->status */
int32_t pb_scene_build(pb_scene *s, int32_t id, const pb_ops *ops, void *u);
/* the per-step script writes (FixedUpdate: MovePosition velocities, hitbox teleports, toggles, kicks) */
void pb_scene_drive(pb_scene *s);
/* drive + step (+ steps++) */
void pb_scene_advance(pb_scene *s);
/* FNV-1a 64 over every body's pose bits (creation order), folded into *h */
void pb_scene_hash(const pb_scene *s, uint64_t *h);
/* query inputs derived from the scene's current state */
int32_t pb_scene_rays(pb_scene *s, sp_ray_query *out, int32_t max, uint32_t *rng);     /* LOS rays + random rays */
int32_t pb_scene_points(pb_scene *s, float *xy, int32_t max, uint32_t *rng);           /* points inside the outline bounds */

/* libshepphys backend: u = pointer to the int32_t world handle */
const pb_ops *pb_shim_ops(void);
int32_t pb_shim_world_create(void);   /* gravity (0, -9.81), sleep + warm start, no continuous (CotL: all Discrete) */

/* convenience for QEMU: build scene id in a fresh shim world, advance `steps` steps, hash the poses (the same hash the bench's
 * STEP case logs for PB_WARMUP_STEPS + PB_MEASURE_STEPS); *stats from one more step if non-NULL; returns status */
int32_t pb_scene_run_hash(int32_t id, int32_t steps, uint64_t *hash, sp_step_stats *stats);

#ifdef __cplusplus
}
#endif
#endif
