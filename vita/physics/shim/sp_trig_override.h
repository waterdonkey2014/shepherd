/* sp_trig_override.h -- force-included (compiler -include) into every Box2D translation unit when SHEPPHYS_TRIG_OWN=ON.
 * Ours, not Box2D. It changes no Box2D file: <math.h> (and <cmath>) are processed first, under their own include guards,
 * and only then are sinf / cosf routed to the deterministic sp_sinf / sp_cosf (shim/sp_trig.c). Box2D's later
 * `#include <math.h>` is then a no-op and its calls in b2Math.h (b2Rot::Set, b2Rot(angle)) resolve to ours.
 * Listed in vita/physics/box2d/PATCHES.md as a build-level substitution (behaviour differs from stock by trig last bits).
 */
#ifndef SP_TRIG_OVERRIDE_H
#define SP_TRIG_OVERRIDE_H

#include <math.h>
#ifdef __cplusplus
#include <cmath>
extern "C" {
#endif
__attribute__((visibility("default"))) float sp_sinf(float x);
__attribute__((visibility("default"))) float sp_cosf(float x);
#ifdef __cplusplus
}
#endif

#define sinf(x) sp_sinf(x)
#define cosf(x) sp_cosf(x)

#endif
