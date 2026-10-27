/* sp_internal.h -- internal to libshepphys (not the C API): read access to the core's Box2D objects for the Unity layer
 * (shim/sp_unity.cpp, Stage 1). Ours. The Unity layer reads poses / velocities / mass through these and makes every change through
 * the traced C API (shepphys.h), so an engine run's trace replays without it (tests/t1_runner.c `replay`). */
#ifndef SP_INTERNAL_H
#define SP_INTERNAL_H
#include <stdint.h>
class b2World;
class b2Body;
b2World *spi_world(int32_t world);
b2Body *spi_body(int32_t world, int32_t body);
/* Stage 2 (shim/sp_query.cpp): a shape's fixture, its collider (sp_shape_def.user), Unity layer and own rounding radius; the world's
 * structural version (bumped by steps and shape / body changes) */
class b2Fixture;
b2Fixture *spi_fixture(int32_t world, int32_t shape);
bool spi_shape_meta(int32_t world, int32_t shape, int32_t *user, int32_t *layer, float *uradius);
float spi_shape_bradius(int32_t world, int32_t shape);   /* E1: the radius Collider2D.bounds include (H-BOUNDSSKIN) */
uint32_t spi_world_version(int32_t world);
int32_t spi_world_flags(int32_t world);   /* the sp_world_def flags (P1's) */
#endif
