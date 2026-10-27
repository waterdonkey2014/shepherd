/* pb_scene.c -- PhysBench's synthetic scenes (ours). See pb_scene.h. Counts from scene-data.md; geometry generated. */
#include "pb_scene.h"

#include <math.h>
#include <string.h>

#define TWO_PI 6.28318531f

/* ---- roles: symmetric masks from a pair list (generic top-down rules, modelled on scene-data.md's gameplay reading of
 *      the matrix -- units ignore units and the player, dead bodies hit only walls and obstacles, volumes and doors only
 *      the player -- not the game's matrix) ---- */
static const uint16_t k_pairs[][2] = {
    {PB_R_PLAYER, PB_R_WALL}, {PB_R_PLAYER, PB_R_OBST}, {PB_R_PLAYER, PB_R_WEAPON}, {PB_R_PLAYER, PB_R_GRASS},
    {PB_R_PLAYER, PB_R_PROP}, {PB_R_PLAYER, PB_R_VOLUME}, {PB_R_PLAYER, PB_R_DOOR}, {PB_R_PLAYER, PB_R_SCENERY},
    {PB_R_PLAYER, PB_R_DECOR}, {PB_R_PLAYER, PB_R_PBLOCK},
    {PB_R_UNIT, PB_R_WALL}, {PB_R_UNIT, PB_R_OBST}, {PB_R_UNIT, PB_R_EWALL}, {PB_R_UNIT, PB_R_WEAPON},
    {PB_R_UNIT, PB_R_GRASS}, {PB_R_UNIT, PB_R_PROP}, {PB_R_UNIT, PB_R_SCENERY},
    {PB_R_DEAD, PB_R_WALL}, {PB_R_DEAD, PB_R_OBST},
    {PB_R_WEAPON, PB_R_OBST}, {PB_R_WEAPON, PB_R_WALL}, {PB_R_WEAPON, PB_R_GRASS}, {PB_R_WEAPON, PB_R_PROP},
    {PB_R_WEAPON, PB_R_SCENERY}, {PB_R_WEAPON, PB_R_NPC},
    {PB_R_WALL, PB_R_GRASS}, {PB_R_WALL, PB_R_PROP}, {PB_R_WALL, PB_R_VOLUME}, {PB_R_WALL, PB_R_DECOR}, {PB_R_WALL, PB_R_NPC},
    {PB_R_OBST, PB_R_GRASS}, {PB_R_OBST, PB_R_PROP}, {PB_R_OBST, PB_R_NPC},
    {PB_R_GRASS, PB_R_NPC}, {PB_R_PROP, PB_R_NPC}, {PB_R_PROP, PB_R_DECOR},
    {PB_R_VOLUME, PB_R_DECOR}, {PB_R_DECOR, PB_R_DECOR}, {PB_R_DECOR, PB_R_NPC},
};

uint32_t pb_role_mask(uint32_t role)
{
    uint32_t m = 0;
    for (unsigned i = 0; i < sizeof k_pairs / sizeof k_pairs[0]; ++i) {
        if (k_pairs[i][0] == role) m |= k_pairs[i][1];
        if (k_pairs[i][1] == role) m |= k_pairs[i][0];
    }
    return m;
}

/* ---- deterministic helpers ---- */
static float frand(pb_scene *s)   /* [0, 1): 24 random bits, exact in float */
{
    s->rng ^= s->rng << 13;
    s->rng ^= s->rng >> 17;
    s->rng ^= s->rng << 5;
    return (float)(s->rng >> 8) * (1.0f / 16777216.0f);
}
static float frange(pb_scene *s, float a, float b) { return a + (b - a) * frand(s); }

static int32_t ok(pb_scene *s, int32_t r)
{
    if (r <= 0 && s->status == 0) s->status = r ? r : SP_ERR_PARAM;
    return r;
}

/* ---- builders ---- */
enum { B_STATIC = 0, B_KIN = 1, B_DYN = 2, B_EMUL = 3 };   /* B_EMUL: sensor-only "kinematic" body built as dynamic */

static int32_t add_body(pb_scene *s, int32_t kind, float x, float y, int32_t allow_sleep, float damping)
{
    sp_body_def d;
    memset(&d, 0, sizeof d);
    d.type = kind == B_STATIC ? SP_BODY_STATIC : kind == B_KIN ? SP_BODY_KINEMATIC : SP_BODY_DYNAMIC;
    d.flags = SP_BODY_ACTIVE | SP_BODY_AWAKE | (allow_sleep ? SP_BODY_ALLOW_SLEEP : 0) |
              (kind >= B_DYN ? SP_BODY_FIXED_ROTATION : 0);
    d.x = x;
    d.y = y;
    d.linear_damping = damping;
    d.gravity_scale = 0.0f;   /* CotL: 834 of 864 dynamic bodies have gravityScale 0; top-down */
    d.user = s->nbody;
    int32_t h = ok(s, s->ops->body(s->u, &d));
    if (h > 0 && s->nbody < PB_MAX_BODIES) s->body[s->nbody++] = h;
    if (kind == B_DYN) s->n_dyn++;
    else if (kind == B_KIN) s->n_kin++;
    else if (kind == B_EMUL) s->n_emul++;
    return h;
}

static void sdef(sp_shape_def *d, int32_t kind, uint32_t role, int32_t sensor, float friction, float restitution)
{
    memset(d, 0, sizeof *d);
    d->kind = kind;
    d->flags = sensor ? SP_SHAPE_SENSOR : 0;
    d->density = 1.0f;
    d->friction = friction;
    d->restitution = restitution;
    d->category_bits = role;
    d->mask_bits = pb_role_mask(role);
}

static void count_shape(pb_scene *s, int32_t body, int32_t sensor)
{
    s->n_shapes++;
    s->n_sensors += sensor ? 1 : 0;
    if (body == s->ground) s->n_static++;
    else s->n_on_bodies++;
}

static int32_t add_circle(pb_scene *s, int32_t b, float r, float x, float y, uint32_t role, int32_t sensor, float fr,
                          float rest)
{
    sp_shape_def d;
    sdef(&d, SP_SHAPE_CIRCLE, role, sensor, fr, rest);
    d.radius = r;
    d.cx = x;
    d.cy = y;
    count_shape(s, b, sensor);
    return ok(s, s->ops->shape(s->u, b, &d, NULL));
}

static int32_t add_box(pb_scene *s, int32_t b, float hx, float hy, float x, float y, float angle, uint32_t role,
                       int32_t sensor)
{
    sp_shape_def d;
    sdef(&d, SP_SHAPE_BOX, role, sensor, 0.4f, 0.0f);
    d.hx = hx;
    d.hy = hy;
    d.cx = x;
    d.cy = y;
    d.angle = angle;
    count_shape(s, b, sensor);
    return ok(s, s->ops->shape(s->u, b, &d, NULL));
}

static int32_t add_ngon(pb_scene *s, int32_t b, int32_t n, float r, float x, float y, float rot, uint32_t role,
                        int32_t sensor)
{
    float xy[16];
    for (int32_t i = 0; i < n; ++i) {
        float a = rot + TWO_PI * (float)i / (float)n;
        xy[2 * i] = x + r * sp_cosf(a);
        xy[2 * i + 1] = y + r * sp_sinf(a);
    }
    sp_shape_def d;
    sdef(&d, SP_SHAPE_POLYGON, role, sensor, 0.4f, 0.0f);
    d.vertex_count = n;
    count_shape(s, b, sensor);
    return ok(s, s->ops->shape(s->u, b, &d, xy));
}

/* an island-like closed outline: a star-shaped (hence simple) smooth blob, counter-clockwise */
static void blob(pb_scene *s, float *xy, int32_t n, float cx, float cy, float rx, float ry, float wobble)
{
    float p1 = frange(s, 0.0f, TWO_PI), p2 = frange(s, 0.0f, TWO_PI), p3 = frange(s, 0.0f, TWO_PI);
    for (int32_t i = 0; i < n; ++i) {
        float th = TWO_PI * (float)i / (float)n;
        float r = 1.0f + wobble * (0.5f * sp_sinf(2.0f * th + p1) + 0.33f * sp_sinf(3.0f * th + p2) +
                                   0.25f * sp_sinf(5.0f * th + p3));
        xy[2 * i] = cx + rx * r * sp_cosf(th);
        xy[2 * i + 1] = cy + ry * r * sp_sinf(th);
    }
}

static int32_t add_loop(pb_scene *s, int32_t b, const float *xy, int32_t n, uint32_t role)
{
    sp_shape_def d;
    sdef(&d, SP_SHAPE_CHAIN, role, 0, 0.4f, 0.0f);
    d.flags |= SP_SHAPE_LOOP;
    d.vertex_count = n;
    count_shape(s, b, 0);
    s->n_chain_edges += n;
    return ok(s, s->ops->shape(s->u, b, &d, xy));
}

/* a point inside the ellipse (cx, cy, rx * f, ry * f): inside any blob() outline with wobble <= 0.12 when f <= 0.85 */
static void inside(pb_scene *s, float f, float *x, float *y)
{
    for (int32_t k = 0; k < 64; ++k) {
        float u = frange(s, -1.0f, 1.0f), v = frange(s, -1.0f, 1.0f);
        if (u * u + v * v <= 1.0f) {
            *x = s->cx + s->ax * f * u;
            *y = s->cy + s->ay * f * v;
            return;
        }
    }
    *x = s->cx;
    *y = s->cy;
}

static void bounds_of(pb_scene *s, const float *xy, int32_t n)
{
    for (int32_t i = 0; i < n; ++i) {
        float x = xy[2 * i], y = xy[2 * i + 1];
        if (x < s->bx0) s->bx0 = x;
        if (y < s->by0) s->by0 = y;
        if (x > s->bx1) s->bx1 = x;
        if (y > s->by1) s->by1 = y;
    }
}

/* GenerateRoom.SetAStar's shape: (int)size * 2 + Margin (5) nodes of 0.5 around the room bounds; r = 1.5 * 0.5 / 2 */
static void astar_room(pb_scene *s)
{
    float w = s->bx1 - s->bx0, h = s->by1 - s->by0;
    s->gstep = 0.5f;
    s->gw = (int32_t)w * 2 + 5;
    s->gh = (int32_t)h * 2 + 5;
    s->gx0 = 0.5f * (s->bx0 + s->bx1) - 0.5f * s->gstep * (float)(s->gw - 1);
    s->gy0 = 0.5f * (s->by0 + s->by1) - 0.5f * s->gstep * (float)(s->gh - 1);
    s->gr = 0.375f;
    s->gmask = PB_R_OBST | PB_R_WALL;   /* Obstacles + Island; useTriggers = false */
}

/* ---- actors ---- */
static void add_lamb(pb_scene *s, float x, float y)
{
    s->lamb = add_body(s, B_DYN, x, y, 0, 0.0f);   /* NeverSleep; drag 0 (prisoner) */
    add_circle(s, s->lamb, 0.3f, 0.0f, 0.0f, PB_R_PLAYER, 0, 0.4f, 0.0f);    /* solid */
    add_circle(s, s->lamb, 0.4f, 0.0f, 0.0f, PB_R_PLAYER, 1, 0.4f, 0.0f);    /* "Enlarged Collider" trigger */
    add_circle(s, s->lamb, 0.75f, 0.0f, 0.0f, PB_R_WEAPON, 1, 0.4f, 0.0f);   /* "Damage Collider" trigger */
    s->face_x = 1.0f;
    s->face_y = 0.0f;
}

static int32_t emul_or_kin(pb_scene *s) { return s->id == PB_SC_INTRO_STOCK ? B_KIN : B_EMUL; }

static void add_swipe(pb_scene *s)
{
    float x = 0.0f, y = 0.0f;
    float p[6];
    s->ops->pose(s->u, s->lamb, p);
    x = p[0];
    y = p[1];
    s->swipe = add_body(s, emul_or_kin(s), x + 0.8f, y, 0, 0.0f);
    add_ngon(s, s->swipe, 8, 1.1f, 0.0f, 0.0f, 0.0f, PB_R_WEAPON, 1);   /* an 8-point swipe polygon */
    s->ops->active(s->u, s->swipe, 0);
    s->swipe_on = 0;
}

static void add_unit(pb_scene *s, float x, float y, float r, uint8_t kind)
{
    int32_t b = add_body(s, B_DYN, x, y, 1, 10.0f);   /* drag 10, StartAwake, FreezeRotation */
    add_circle(s, b, r, 0.0f, 0.0f, PB_R_UNIT, 0, 0.01f, 1.0f);   /* "bouncy" material: friction 0.01, bounciness 1 */
    s->unit[s->nunit] = b;
    s->ukind[s->nunit] = kind;
    s->nunit++;
}

static void add_hitbox(pb_scene *s, int32_t owner, float r)
{
    float p[6];
    s->ops->pose(s->u, s->unit[owner], p);
    int32_t b = add_body(s, emul_or_kin(s), p[0], p[1], 0, 0.0f);
    add_circle(s, b, r, 0.0f, 0.0f, PB_R_WEAPON, 1, 0.4f, 0.0f);
    s->hit[s->nhit] = b;
    s->hit_owner[s->nhit] = owner;
    s->nhit++;
}

static void add_dead(pb_scene *s, float x, float y)
{
    int32_t b = add_body(s, B_DYN, x, y, 1, 10.0f);
    add_circle(s, b, 0.2f, 0.0f, 0.0f, PB_R_DEAD, 0, 0.01f, 1.0f);
    add_circle(s, b, 0.23f, 0.0f, 0.0f, PB_R_PROP, 1, 0.01f, 1.0f);
    s->dead[s->ndead++] = b;
}

/* ---- scenes ---- */
static void build_intro(pb_scene *s)
{
    float xy[2 * 64];
    s->cx = 5.4f; s->cy = 10.45f;     /* the outline box of Intro Room 1's composite sources: (-6,-8.5)-(16.8,29.4) */
    s->ax = 11.4f; s->ay = 18.95f;
    s->ground = add_body(s, B_STATIC, 0.0f, 0.0f, 1, 0.0f);
    s->island = add_body(s, B_KIN, 0.0f, 0.0f, 1, 0.0f);   /* the composite's Kinematic "Room Transform" */
    blob(s, xy, 49, s->cx, s->cy, s->ax, s->ay, 0.10f);
    bounds_of(s, xy, 49);
    add_loop(s, s->island, xy, 49, PB_R_WALL);
    /* 43 static circles: 34 grass triggers r 0.3 in 5 clumps along the fight's path, 6 prop triggers r 0.22, 3 solid r 0.2 */
    for (int32_t c = 0; c < 5; ++c) {
        float tc = 1.3f + 2.7f * (float)c;
        float gx = s->cx + 0.7f * s->ax * sp_sinf(0.7f * tc), gy = s->cy + 0.7f * s->ay * sp_sinf(0.45f * tc);
        int32_t n = c < 4 ? 7 : 6;
        for (int32_t i = 0; i < n; ++i)
            add_circle(s, s->ground, 0.3f, gx + frange(s, -1.6f, 1.6f), gy + frange(s, -1.6f, 1.6f), PB_R_GRASS, 1, 0.4f, 0.0f);
    }
    for (int32_t i = 0; i < 6; ++i) {
        float x, y;
        inside(s, 0.75f, &x, &y);
        add_circle(s, s->ground, 0.22f, x, y, PB_R_PROP, 1, 0.4f, 0.0f);
    }
    for (int32_t i = 0; i < 3; ++i) {
        float x, y;
        inside(s, 0.7f, &x, &y);
        add_circle(s, s->ground, 0.2f, x, y, PB_R_OBST, 0, 0.4f, 0.0f);
    }
    /* 36 static boxes: 27 triggers (5 music volumes 5x1, 1 lighting volume 30.5x43.5, 1 door, 1 goop, 6 scenery,
     * 13 breakables) and 9 solid (player-only blocking door 4x0.58, 3 enemy-only walls, 5 obstacles) */
    for (int32_t i = 0; i < 5; ++i) {
        float x, y;
        inside(s, 0.6f, &x, &y);
        add_box(s, s->ground, 2.5f, 0.5f, x, y, 0.0f, PB_R_VOLUME, 1);
    }
    add_box(s, s->ground, 15.25f, 21.75f, s->cx, s->cy, 0.0f, PB_R_VOLUME, 1);
    add_box(s, s->ground, 1.0f, 0.5f, s->cx, s->by1 - 1.5f, 0.0f, PB_R_DOOR, 1);
    add_box(s, s->ground, 1.05f, 0.35f, s->cx, s->by1 - 2.5f, 0.0f, PB_R_OBST, 1);
    for (int32_t i = 0; i < 6; ++i) {
        float x, y;
        inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, 0.205f, 0.285f, x, y, 0.0f, PB_R_SCENERY, 1);
    }
    for (int32_t i = 0; i < 13; ++i) {   /* breakables: 7 along the path, 6 anywhere */
        float x, y;
        if (i < 7) {
            float tc = 0.6f + 1.9f * (float)i;
            x = s->cx + 0.75f * s->ax * sp_sinf(0.7f * tc) + frange(s, -1.0f, 1.0f);
            y = s->cy + 0.75f * s->ay * sp_sinf(0.45f * tc) + frange(s, -1.0f, 1.0f);
        } else inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, 0.235f, 0.235f, x, y, 0.0f, PB_R_PROP, 1);
    }
    add_box(s, s->ground, 2.0f, 0.29f, s->cx, s->by1 - 3.0f, 0.0f, PB_R_PBLOCK, 0);
    add_box(s, s->ground, 1.05f, 2.0f, s->cx - 3.0f, s->by1 - 4.0f, 0.0f, PB_R_EWALL, 0);
    add_box(s, s->ground, 1.05f, 2.0f, s->cx + 3.0f, s->by1 - 4.0f, 0.0f, PB_R_EWALL, 0);
    add_box(s, s->ground, 2.5f, 0.5f, s->cx, s->by0 + 3.0f, 0.0f, PB_R_EWALL, 0);
    for (int32_t i = 0; i < 5; ++i) {
        float x, y;
        inside(s, 0.7f, &x, &y);
        add_box(s, s->ground, frange(s, 0.4f, 0.8f), frange(s, 0.3f, 0.6f), x, y, frange(s, 0.0f, 1.5f), PB_R_OBST, 0);
    }
    /* the Lamb, 19 units (10 formation grunts, 4 swordsmen, 4 cowards, 1 brute r 0.8), 12 dead bodies, 5 hitboxes
     * (r 0.8 on the swordsmen, r 2 on the brute), the swipe */
    add_lamb(s, s->cx, s->cy - 8.0f);
    for (int32_t i = 0; i < 10; ++i) add_unit(s, s->cx - 4.0f + 0.9f * (float)(i % 5), s->cy + 4.0f + 0.9f * (float)(i / 5), 0.3f, 0);
    for (int32_t i = 0; i < 4; ++i) add_unit(s, s->cx + 4.0f, s->cy - 2.0f + 1.2f * (float)i, 0.3f, 1);
    for (int32_t i = 0; i < 4; ++i) add_unit(s, s->cx - 5.0f, s->cy - 3.0f + 1.5f * (float)i, 0.3f, 2);
    add_unit(s, s->cx, s->cy + 9.0f, 0.8f, 3);
    for (int32_t i = 0; i < 12; ++i) add_dead(s, s->cx + 2.0f + 0.7f * (float)(i % 4), s->cy + 1.0f + 0.7f * (float)(i / 4));
    for (int32_t i = 0; i < 4; ++i) add_hitbox(s, 10 + i, 0.8f);
    add_hitbox(s, 18, 2.0f);
    add_swipe(s);
    astar_room(s);
}

static void build_dungeon(pb_scene *s)
{
    float xy[2 * 64];
    s->cx = 0.0f; s->cy = 0.0f;
    s->ax = 10.0f; s->ay = 7.0f;
    s->ground = add_body(s, B_STATIC, 0.0f, 0.0f, 1, 0.0f);
    s->island = add_body(s, B_KIN, 0.0f, 0.0f, 1, 0.0f);
    blob(s, xy, 64, s->cx, s->cy, s->ax, s->ay, 0.08f);
    bounds_of(s, xy, 64);
    add_loop(s, s->island, xy, 64, PB_R_WALL);
    /* 4 doors (N E S W), 5 colliders each: door trigger 2x1, two enemy blockers 2.1x4, goop trigger, blocking 2.1x0.7 */
    static const float dirs[4][2] = {{0.0f, 1.0f}, {1.0f, 0.0f}, {0.0f, -1.0f}, {-1.0f, 0.0f}};
    for (int32_t d = 0; d < 4; ++d) {
        float ux = dirs[d][0], uy = dirs[d][1];
        float px = s->cx + ux * (s->ax - 1.2f), py = s->cy + uy * (s->ay - 1.2f);
        float tx = -uy, ty = ux;   /* along the wall */
        float rot = d % 2 ? 1.5707964f : 0.0f;
        add_box(s, s->ground, 1.0f, 0.5f, px, py, rot, PB_R_DOOR, 1);
        add_box(s, s->ground, 1.05f, 2.0f, px + tx * 2.2f - ux * 1.0f, py + ty * 2.2f - uy * 1.0f, rot, PB_R_EWALL, 0);
        add_box(s, s->ground, 1.05f, 2.0f, px - tx * 2.2f - ux * 1.0f, py - ty * 2.2f - uy * 1.0f, rot, PB_R_EWALL, 0);
        add_box(s, s->ground, 1.05f, 0.35f, px - ux * 0.6f, py - uy * 0.6f, rot, PB_R_OBST, 1);
        add_box(s, s->ground, 1.05f, 0.35f, px - ux * 0.2f, py - uy * 0.2f, rot, PB_R_OBST, 0);
    }
    /* decorations: 12 grass triggers, 8 breakable triggers */
    for (int32_t i = 0; i < 12; ++i) {
        float x, y;
        inside(s, 0.7f, &x, &y);
        add_circle(s, s->ground, 0.3f, x, y, PB_R_GRASS, 1, 0.4f, 0.0f);
    }
    for (int32_t i = 0; i < 8; ++i) {
        float x, y;
        inside(s, 0.7f, &x, &y);
        add_box(s, s->ground, 0.235f, 0.235f, x, y, 0.0f, PB_R_PROP, 1);
    }
    add_lamb(s, s->cx, s->cy - 4.0f);
    for (int32_t i = 0; i < 4; ++i) add_unit(s, s->cx - 3.0f + 0.9f * (float)i, s->cy + 3.0f, 0.3f, 0);
    for (int32_t i = 0; i < 2; ++i) add_unit(s, s->cx + 4.0f, s->cy + 1.2f * (float)i, 0.3f, 1);
    add_unit(s, s->cx - 5.0f, s->cy, 0.3f, 2);
    add_unit(s, s->cx, s->cy + 5.0f, 0.3f, 3);
    for (int32_t i = 0; i < 8; ++i) add_hitbox(s, i, 0.4f + 0.05f * (float)i);
    s->hit_toggle = (1 << s->nhit) - 1;   /* all active at creation; drive_hitboxes keeps half of them active */
    for (int32_t i = 0; i < 2; ++i) add_dead(s, s->cx + 2.0f + 0.7f * (float)i, s->cy - 1.0f);
    for (int32_t i = 0; i < 3; ++i) {
        int32_t b = add_body(s, emul_or_kin(s), s->cx - s->ax + 1.0f, s->cy - 3.0f + 3.0f * (float)i, 0, 0.0f);
        add_circle(s, b, 0.25f, 0.0f, 0.0f, PB_R_WEAPON, 1, 0.4f, 0.0f);
        s->proj[s->nproj] = b;
        s->proj_v[s->nproj][0] = 6.0f;
        s->proj_v[s->nproj][1] = 0.8f * (float)(i - 1);
        s->nproj++;
    }
    add_swipe(s);
    astar_room(s);
}

static void build_base(pb_scene *s)
{
    float xy[2 * 170];
    int32_t sleep_roots = s->id == PB_SC_BASE_SLEEP;
    s->cx = 0.0f; s->cy = 0.0f;
    s->ax = 30.0f; s->ay = 25.0f;
    s->ground = add_body(s, B_STATIC, 0.0f, 0.0f, 1, 0.0f);
    s->island = add_body(s, B_KIN, 0.0f, 0.0f, 1, 0.0f);
    /* 3 room composites on Island: 170 / 49 / 42 outline vertices */
    blob(s, xy, 170, 0.0f, 0.0f, 30.0f, 25.0f, 0.10f);
    bounds_of(s, xy, 170);
    add_loop(s, s->island, xy, 170, PB_R_WALL);
    blob(s, xy, 49, -31.0f, 27.0f, 8.0f, 6.5f, 0.10f);
    bounds_of(s, xy, 49);
    add_loop(s, s->island, xy, 49, PB_R_WALL);
    blob(s, xy, 42, 27.0f, 29.0f, 7.0f, 6.0f, 0.10f);
    bounds_of(s, xy, 42);
    add_loop(s, s->island, xy, 42, PB_R_WALL);
    /* the obstacle composite: 8 paths, 145 points (7 x 18 + 19), on the static ground */
    for (int32_t i = 0; i < 8; ++i) {
        float x, y;
        inside(s, 0.75f, &x, &y);
        int32_t n = i < 7 ? 18 : 19;
        blob(s, xy, n, x, y, frange(s, 1.2f, 2.0f), frange(s, 1.0f, 1.8f), 0.10f);
        add_loop(s, s->ground, xy, n, PB_R_OBST);
    }
    /* the 4 never-moving "CustomTransform" roots: one carries 60 trigger circles r 0.25 (a shrine's bells), three carry
     * 12-13 trigger circles r 0.4-0.8 around a structure; trigger props (loot / Health) cluster at the structures */
    static const float roots[4][2] = {{27.0f, 29.0f}, {-15.0f, -5.0f}, {12.0f, -12.0f}, {-8.0f, 14.0f}};   /* the shrine sits
                                                                                                    in the small room, across its outline */
    for (int32_t r = 0; r < 4; ++r) {
        int32_t b = add_body(s, B_EMUL, roots[r][0], roots[r][1], sleep_roots, 0.0f);
        s->root[s->nroot++] = b;
        if (r == 0) {
            for (int32_t i = 0; i < 60; ++i)
                add_circle(s, b, 0.25f, -4.5f + 0.9f * (float)(i % 10), -2.5f + 0.9f * (float)(i / 10), PB_R_DECOR, 1, 0.4f, 0.0f);
        } else {
            int32_t n = r == 1 ? 13 : 12;
            for (int32_t i = 0; i < n; ++i)
                add_circle(s, b, frange(s, 0.4f, 0.8f), frange(s, -2.5f, 2.5f), frange(s, -2.5f, 2.5f), PB_R_DECOR, 1, 0.4f, 0.0f);
        }
    }
    /* 337 statics on the ground (the obstacle composite above counts as 1) */
    for (int32_t c = 0; c < 10; ++c) {   /* 119 grass triggers r 0.3 in 10 clumps */
        float gx, gy;
        inside(s, 0.8f, &gx, &gy);
        int32_t n = c < 9 ? 12 : 11;
        for (int32_t i = 0; i < n; ++i)
            add_circle(s, s->ground, 0.3f, gx + frange(s, -2.0f, 2.0f), gy + frange(s, -2.0f, 2.0f), PB_R_GRASS, 1, 0.4f, 0.0f);
    }
    for (int32_t i = 0; i < 87; ++i) {   /* 87 prop triggers r 0.22-0.47: 60 clustered at the structures, 27 scattered */
        float x, y;
        if (i < 60) {
            int32_t r = 1 + i % 3;
            x = roots[r][0] + frange(s, -3.0f, 3.0f);
            y = roots[r][1] + frange(s, -3.0f, 3.0f);
        } else inside(s, 0.8f, &x, &y);
        add_circle(s, s->ground, frange(s, 0.22f, 0.47f), x, y, PB_R_PROP, 1, 0.4f, 0.0f);
    }
    for (int32_t i = 0; i < 42; ++i) {   /* 42 solid obstacles */
        float x, y;
        inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, frange(s, 0.3f, 1.25f), frange(s, 0.3f, 1.0f), x, y, frange(s, 0.0f, 1.5f), PB_R_OBST, 0);
    }
    for (int32_t i = 0; i < 23; ++i) {   /* 23 scenery triggers */
        float x, y;
        inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, 0.3f, 0.4f, x, y, 0.0f, PB_R_SCENERY, 1);
    }
    for (int32_t i = 0; i < 16; ++i) {   /* 16 player-only triggers (doors, interaction areas) */
        float x, y;
        inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, 1.0f, 0.6f, x, y, 0.0f, PB_R_DOOR, 1);
    }
    for (int32_t i = 0; i < 24; ++i) {   /* 24 polygons: 13 solid 6-gons, 11 trigger 8-gons */
        float x, y;
        inside(s, 0.8f, &x, &y);
        if (i < 13) add_ngon(s, s->ground, 6, frange(s, 0.5f, 1.0f), x, y, frange(s, 0.0f, 1.0f), PB_R_OBST, 0);
        else add_ngon(s, s->ground, 8, frange(s, 0.6f, 1.2f), x, y, 0.0f, PB_R_PROP, 1);
    }
    for (int32_t i = 0; i < 22; ++i) {   /* 22 capsules as boxes: 5 solid, 17 triggers */
        float x, y;
        inside(s, 0.8f, &x, &y);
        add_box(s, s->ground, 0.25f, 0.6f, x, y, 0.0f, i < 5 ? PB_R_OBST : PB_R_PROP, i >= 5);
    }
    /* 3 huge volume triggers: a lighting volume 458 x 653 over everything, two 125 x 50 post-process boxes */
    add_box(s, s->ground, 229.0f, 326.5f, 0.0f, 0.0f, 0.0f, PB_R_VOLUME, 1);
    add_box(s, s->ground, 62.5f, 25.0f, 0.0f, 12.0f, 0.0f, PB_R_VOLUME, 1);
    add_box(s, s->ground, 62.5f, 25.0f, 0.0f, -14.0f, 0.0f, PB_R_VOLUME, 1);
    /* 16 wandering NPCs (8 with a trigger), the Lamb */
    for (int32_t i = 0; i < 16; ++i) {
        float x, y;
        inside(s, 0.7f, &x, &y);
        int32_t b = add_body(s, B_DYN, x, y, 1, 10.0f);
        add_circle(s, b, frange(s, 0.3f, 0.5f), 0.0f, 0.0f, PB_R_NPC, 0, 0.01f, 1.0f);
        if (i % 2 == 0) add_circle(s, b, 0.6f, 0.0f, 0.0f, PB_R_NPC, 1, 0.4f, 0.0f);
        s->npc[s->nnpc] = b;
        s->npc_t[s->nnpc][0] = x;
        s->npc_t[s->nnpc][1] = y;
        s->npc_timer[s->nnpc] = 0;
        s->npc_speed[s->nnpc] = frange(s, 1.2f, 2.0f);
        s->nnpc++;
    }
    add_lamb(s, 0.0f, -10.0f);
    s->ax = 24.0f;   /* the Lamb's path stays inside the main island */
    s->ay = 19.0f;
    /* the base graph: 150 x 150 nodes of 0.5, r 0.25, + "Obstacles Player Ignore" */
    s->gstep = 0.5f;
    s->gw = s->gh = 150;
    s->gx0 = -37.25f;
    s->gy0 = -32.25f;
    s->gr = 0.25f;
    s->gmask = PB_R_OBST | PB_R_WALL | PB_R_EWALL;
}

const char *pb_scene_name(int32_t id)
{
    static const char *const k[PB_SC_N] = {"intro_fight", "intro_fight_stock", "dungeon_room", "base_never", "base_sleep"};
    return id >= 0 && id < PB_SC_N ? k[id] : "?";
}

int32_t pb_scene_build(pb_scene *s, int32_t id, const pb_ops *ops, void *u)
{
    memset(s, 0, sizeof *s);
    s->id = id;
    s->ops = ops;
    s->u = u;
    s->rng = 0x9E3779B9u ^ (uint32_t)(id * 7919);
    if (id == PB_SC_INTRO_STOCK) s->rng = 0x9E3779B9u;   /* same layout as intro_fight */
    if (id == PB_SC_BASE_SLEEP) s->rng = 0x9E3779B9u ^ (uint32_t)(PB_SC_BASE_NEVER * 7919);
    s->bx0 = s->by0 = 1e30f;
    s->bx1 = s->by1 = -1e30f;
    switch (id) {
    case PB_SC_INTRO:
    case PB_SC_INTRO_STOCK: build_intro(s); break;
    case PB_SC_DUNGEON: build_dungeon(s); break;
    case PB_SC_BASE_NEVER:
    case PB_SC_BASE_SLEEP: build_base(s); break;
    default: s->status = SP_ERR_PARAM;
    }
    return s->status;
}

/* ---- the per-step script ---- */
static void pose(pb_scene *s, int32_t b, float *p) { s->ops->pose(s->u, b, p); }

static void move_toward(pb_scene *s, int32_t b, float tx, float ty, float speed, float *vout)
{
    float p[6];
    pose(s, b, p);
    float dx = tx - p[0], dy = ty - p[1], d = sqrtf(dx * dx + dy * dy), vx = 0.0f, vy = 0.0f;
    if (d > 1e-4f) {
        float sp = d / PB_DT;
        if (sp > speed) sp = speed;
        vx = dx / d * sp;
        vy = dy / d * sp;
    }
    s->ops->vel(s->u, b, vx, vy);
    if (vout) { vout[0] = vx; vout[1] = vy; }
}

static void drive_lamb_and_units(pb_scene *s, float t)
{
    float v[2], lp[6];
    float tx = s->cx + s->ax * 0.95f * sp_sinf(t * 0.7f), ty = s->cy + s->ay * 0.95f * sp_sinf(t * 0.45f);
    move_toward(s, s->lamb, tx, ty, 5.5f, v);   /* RunSpeed 5.5; pushes into the outline at the extremes */
    if (v[0] * v[0] + v[1] * v[1] > 0.01f) {
        float l = sqrtf(v[0] * v[0] + v[1] * v[1]);
        s->face_x = v[0] / l;
        s->face_y = v[1] / l;
    }
    pose(s, s->lamb, lp);
    for (int32_t i = 0; i < s->nunit; ++i) {
        float p[6];
        pose(s, s->unit[i], p);
        float gx = lp[0], gy = lp[1], speed = 2.5f;
        switch (s->ukind[i]) {
        case 0: {   /* formation grunt: a ring slot around the Lamb */
            float a = TWO_PI * (float)i / 10.0f;
            gx += 1.8f * sp_cosf(a);
            gy += 1.8f * sp_sinf(a);
            break;
        }
        case 1: {   /* swordsman: orbit at 3, lunge for 0.3 s every 2 s */
            float a = 1.5707964f * (float)i + t * 0.8f;
            if ((s->steps + 25 * i) % 100 < 15) speed = 8.0f;
            else { gx += 3.0f * sp_cosf(a); gy += 3.0f * sp_sinf(a); speed = 3.0f; }
            break;
        }
        case 2: {   /* coward: flee away from the Lamb, into the walls */
            float dx = p[0] - lp[0], dy = p[1] - lp[1], d = sqrtf(dx * dx + dy * dy) + 1e-3f;
            gx = p[0] + 3.0f * dx / d;
            gy = p[1] + 3.0f * dy / d;
            speed = 2.0f;
            break;
        }
        default: speed = 1.5f; break;   /* brute: slow chase */
        }
        move_toward(s, s->unit[i], gx, gy, speed, NULL);
    }
}

static void drive_hitboxes(pb_scene *s)
{
    for (int32_t i = 0; i < s->nhit; ++i) {   /* children: follow the owner's pose of the previous step (teleport + wake) */
        if (s->id == PB_SC_DUNGEON && (i % 2) != ((s->steps / 30) % 2)) {
            if (s->hit_toggle & (1 << i)) { s->ops->active(s->u, s->hit[i], 0); s->hit_toggle &= ~(1 << i); }
            continue;
        }
        if (s->id == PB_SC_DUNGEON && !(s->hit_toggle & (1 << i))) {
            s->ops->active(s->u, s->hit[i], 1);
            s->hit_toggle |= 1 << i;
        }
        float p[6];
        pose(s, s->unit[s->hit_owner[i]], p);
        s->ops->xform(s->u, s->hit[i], p[0], p[1], 0.0f);
    }
}

static void drive_dead_and_swipe(pb_scene *s)
{
    if (s->ndead && s->steps % 25 == 0) {   /* a knockback: velocity 25 (DoKnockBack 25 * K), slides and bounces */
        int32_t k = (s->steps / 25) % s->ndead;
        float a = 0.37f * (float)s->steps;
        s->ops->vel(s->u, s->dead[k], 25.0f * sp_cosf(a), 25.0f * sp_sinf(a));
    }
    int32_t on = (s->steps % 20) < 5;   /* the swipe: active 5 steps of every 20 */
    if (on != s->swipe_on) { s->ops->active(s->u, s->swipe, on); s->swipe_on = on; }
    if (on) {
        float lp[6];
        pose(s, s->lamb, lp);
        s->ops->xform(s->u, s->swipe, lp[0] + 0.8f * s->face_x, lp[1] + 0.8f * s->face_y, 0.0f);
    }
}

static void drive_base(pb_scene *s, float t)
{
    float tx = s->cx + s->ax * sp_sinf(t * 0.21f), ty = s->cy + s->ay * sp_sinf(t * 0.13f);
    move_toward(s, s->lamb, tx, ty, 5.5f, NULL);
    for (int32_t i = 0; i < s->nnpc; ++i) {
        float p[6];
        pose(s, s->npc[i], p);
        float dx = s->npc_t[i][0] - p[0], dy = s->npc_t[i][1] - p[1];
        if (dx * dx + dy * dy < 0.04f || --s->npc_timer[i] <= 0) {
            float x, y;
            inside(s, 0.75f, &x, &y);
            s->npc_t[i][0] = x;
            s->npc_t[i][1] = y;
            s->npc_timer[i] = 200 + (int32_t)(frand(s) * 100.0f);
        }
        move_toward(s, s->npc[i], s->npc_t[i][0], s->npc_t[i][1], s->npc_speed[i], NULL);
    }
}

void pb_scene_drive(pb_scene *s)
{
    float t = (float)s->steps * PB_DT;
    switch (s->id) {
    case PB_SC_INTRO:
    case PB_SC_INTRO_STOCK:
        drive_lamb_and_units(s, t);
        drive_hitboxes(s);
        drive_dead_and_swipe(s);
        break;
    case PB_SC_DUNGEON:
        drive_lamb_and_units(s, t);
        drive_hitboxes(s);
        drive_dead_and_swipe(s);
        for (int32_t i = 0; i < s->nproj; ++i) {
            float p[6];
            pose(s, s->proj[i], p);
            if (p[0] > s->bx1 + 2.0f) s->ops->xform(s->u, s->proj[i], s->bx0 - 1.0f, s->cy - 3.0f + 3.0f * (float)i, 0.0f);
            s->ops->vel(s->u, s->proj[i], s->proj_v[i][0], s->proj_v[i][1]);
        }
        break;
    default: drive_base(s, t); break;
    }
}

void pb_scene_advance(pb_scene *s)
{
    pb_scene_drive(s);
    s->ops->step(s->u);
    s->steps++;
}

static void mix(uint64_t *h, uint32_t w)
{
    for (int i = 0; i < 4; ++i) {
        *h ^= (w >> (8 * i)) & 0xFFu;
        *h *= 0x100000001b3ULL;
    }
}

void pb_scene_hash(const pb_scene *s, uint64_t *h)
{
    for (int32_t i = 0; i < s->nbody; ++i) {
        float p[6];
        s->ops->pose(s->u, s->body[i], p);
        for (int k = 0; k < 6; ++k) {
            uint32_t w;
            memcpy(&w, &p[k], 4);
            mix(h, w);
        }
    }
}

int32_t pb_scene_rays(pb_scene *s, sp_ray_query *out, int32_t max, uint32_t *rng)
{
    int32_t n = 0;
    float lp[6];
    pose(s, s->lamb, lp);
    for (int32_t i = 0; i < s->nunit && n < max; ++i) {   /* line-of-sight checks: unit -> Lamb, Obstacles | Island */
        float p[6];
        pose(s, s->unit[i], p);
        sp_ray_query q = {p[0], p[1], lp[0], lp[1], PB_R_OBST | PB_R_WALL, SP_RAY_CLOSEST};
        out[n++] = q;
    }
    uint32_t keep = s->rng;
    s->rng = *rng;
    while (n < max) {   /* wall probes of length 2-12 in random directions from random points */
        float x, y;
        inside(s, 0.8f, &x, &y);
        float a = frange(s, 0.0f, TWO_PI), l = frange(s, 2.0f, 12.0f);
        sp_ray_query q = {x, y, x + l * sp_cosf(a), y + l * sp_sinf(a), PB_R_OBST | PB_R_WALL, SP_RAY_CLOSEST};
        out[n++] = q;
    }
    *rng = s->rng;
    s->rng = keep;
    return n;
}

int32_t pb_scene_points(pb_scene *s, float *xy, int32_t max, uint32_t *rng)
{
    uint32_t keep = s->rng;
    s->rng = *rng;
    for (int32_t i = 0; i < max; ++i) inside(s, 0.9f, &xy[2 * i], &xy[2 * i + 1]);
    *rng = s->rng;
    s->rng = keep;
    return max;
}

/* ---- the libshepphys backend ---- */
static int32_t W_(void *u) { return *(int32_t *)u; }
static int32_t shim_body(void *u, const sp_body_def *d) { return sp_body_create(W_(u), d); }
static int32_t shim_shape(void *u, int32_t b, const sp_shape_def *d, const float *xy) { return sp_shape_create(W_(u), b, d, xy); }
static void shim_pose(void *u, int32_t b, float *o)
{
    sp_pose p;
    if (sp_body_get_pose(W_(u), b, &p) != SP_OK) memset(&p, 0, sizeof p);
    o[0] = p.x; o[1] = p.y; o[2] = p.angle; o[3] = p.vx; o[4] = p.vy; o[5] = p.w;
}
static void shim_vel(void *u, int32_t b, float vx, float vy)
{
    float v[3] = {vx, vy, 0.0f};
    sp_body_set_velocity(W_(u), b, v);
}
static void shim_xform(void *u, int32_t b, float x, float y, float a)
{
    float t[3] = {x, y, a};
    sp_body_set_transform(W_(u), b, t);
    sp_body_set_flag(W_(u), b, SP_BODY_AWAKE, 1);
}
static void shim_active(void *u, int32_t b, int32_t on) { sp_body_set_flag(W_(u), b, SP_BODY_ACTIVE, on); }
static void shim_step(void *u)
{
    sp_step_def d = {PB_DT, 8, 3, 0};
    sp_world_step(W_(u), &d, NULL);
}
static const pb_ops k_shim_ops = {shim_body, shim_shape, shim_pose, shim_vel, shim_xform, shim_active, shim_step};
const pb_ops *pb_shim_ops(void) { return &k_shim_ops; }

int32_t pb_shim_world_create(void)
{
    sp_world_def wd = {0.0f, -9.81f, SP_WORLD_DEFAULT_FLAGS, 0};
    return sp_world_create(&wd);
}

int32_t pb_scene_run_hash(int32_t id, int32_t steps, uint64_t *hash, sp_step_stats *stats)
{
    static pb_scene s;
    int32_t w = pb_shim_world_create();
    if (w <= 0) return w;
    int32_t st = pb_scene_build(&s, id, pb_shim_ops(), &w);
    for (int32_t i = 0; st == 0 && i < steps; ++i) pb_scene_advance(&s);
    uint64_t h = 0xcbf29ce484222325ULL;
    pb_scene_hash(&s, &h);   /* the bench's STEP hash: poses after warm-up + measured steps */
    if (stats) {             /* one more step for the statistics, after the hash (as pb_bench.c does) */
        sp_step_def d = {PB_DT, 8, 3, 0};
        pb_scene_drive(&s);
        sp_world_step(w, &d, stats);
    }
    sp_world_destroy(w);
    *hash = h;
    return st;
}
