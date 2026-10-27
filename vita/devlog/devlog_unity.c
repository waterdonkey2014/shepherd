/* Shepherd devlog client, single translation unit: #include "../devlog/devlog_unity.c" in ONE .c file of an app
 * (or compile this file on its own). Picks the platform layer: sceNet/sceIo on the Vita (__vita__, defined by
 * arm-vita-eabi-gcc), POSIX elsewhere (the Mac test build). Only the devlog_* functions of devlog.h are exported;
 * everything else is static, so it cannot collide with the app's own symbols. */
#include "devlog.h"
#include "devlog_sha256.c"
#if defined(__vita__)
#include "devlog_plat_vita.c"
#else
#include "devlog_plat_posix.c"
#endif
#include "devlog.c"
