/* pb_timer_stub.cpp -- ours, not Box2D. A LINK-level stand-in for Box2D's b2Timer (box2d/Box2D/Common/b2Timer.cpp) in the
 * Mac reference binary physbench_mac only; no Box2D file is changed and libshepphys.a is linked as built.
 *
 * Why: b2World::Step and b2Island::Solve time themselves for b2Profile with b2Timer. On the Mac (and Linux) b2Timer calls
 * gettimeofday -- four calls per island per step plus about eight per step, i.e. ~130 calls in an intro-sized fight. On the
 * Vita, Box2D compiles b2Timer.cpp's last branch (neither _WIN32 nor __linux__ nor __APPLE__): empty functions returning 0.
 * A Mac reference that pays for gettimeofday and a Vita run that does not would understate the Vita/Mac ratio, so the
 * reference binary gets the Vita's behaviour: these definitions are the same bodies as that branch. Because the
 * executable defines every b2Timer symbol, the linker never pulls b2Timer.o out of libshepphys.a.
 * physbench_mac_prof links without this file and reports b2Profile and the timers' own cost. */
#include "Box2D/Common/b2Timer.h"

b2Timer::b2Timer() {}
void b2Timer::Reset() {}
float32 b2Timer::GetMilliseconds() const { return 0.0f; }
