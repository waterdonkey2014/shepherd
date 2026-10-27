/* sp_timer_stub.cpp -- ours, not Box2D. Build-level substitution B3 (box2d/PATCHES.md; physics-plan-v2 section 1.2 item 5):
 * the Mac libshepphys (dylib and static archive) is built WITHOUT box2d/Box2D/Common/b2Timer.cpp and with this file instead.
 * No Box2D file is changed.
 *
 * Why: b2World::Step and b2Island::Solve time themselves for b2Profile with b2Timer. On the Mac (and Linux) b2Timer.cpp calls
 * gettimeofday -- four calls per island per step plus about eight per step -- which costs the Mac 1.0-1.5 us per intro-sized
 * step (6-16 %, PhysBench finding 1). On the Vita, Box2D compiles b2Timer.cpp's last branch (neither _WIN32 nor __linux__ nor
 * __APPLE__): these same empty bodies. So with B3 the Mac library and the Vita library do the same thing here, b2Profile reads
 * 0 on both, and Mac timings stop overstating the solver. The timers never feed arithmetic, so T1 is unchanged (8/8).
 * The bodies are b2Timer.cpp's #else branch (box2d/Box2D/Common/b2Timer.cpp:86-101), as physbench/pb_timer_stub.cpp has them.
 * The `prof` build (vita/physics/build.sh: -DSHEPPHYS_PROF=ON, build/mac-prof/) keeps the stock timer for b2Profile phase splits;
 * physbench_mac_prof links that one. */
#include "Box2D/Common/b2Timer.h"

b2Timer::b2Timer() {}
void b2Timer::Reset() {}
float32 b2Timer::GetMilliseconds() const { return 0.0f; }
