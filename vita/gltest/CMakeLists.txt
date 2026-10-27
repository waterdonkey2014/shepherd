# vita/gltest: libshepgl.a (the GL host layer for GlBackend on vitaGL) + the "Shepherd GL Test" VPK.
# Configure with the ~/src path (no spaces):  cmake -S $SHEPHERD_REPO/vita/gltest -B <build> -G Ninja   (build.sh does all of it)
cmake_minimum_required(VERSION 3.16)
if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  if(DEFINED ENV{VITASDK})
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  else()
    message(FATAL_ERROR "Please define VITASDK to point to your SDK path!")
  endif()
endif()

project(shepherd_gltest C)
include("${VITASDK}/share/vita.cmake" REQUIRED)

set(VITA_APP_NAME "Shepherd GL Test")
set(VITA_TITLEID  "SHPD00007")
set(VITA_VERSION  "01.00")
set(VITA_MKSFOEX_FLAGS "-d ATTRIBUTE2=12" CACHE STRING "vita-mksfoex flags" FORCE)   # +109 MiB main RAM budget (365 MiB), like the engine

if(NOT DEFINED GLTEST_BUILD)
  set(GLTEST_BUILD "dev")
endif()
if(NOT DEFINED GEN_DIR)
  set(GEN_DIR "${CMAKE_BINARY_DIR}/gen")
endif()

set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -std=gnu11 -O2 -ffp-contract=off -Wall -Wextra -Wno-unused-parameter")
set(CMAKE_C_FLAGS_RELEASE "-DNDEBUG")
set(SAMPLES_COMMON "$ENV{VITASDK}/share/gcc-arm-vita-eabi/samples/common")

# the shaders the test embeds: GlBackend's text (engine/Shepherd.Engine/Render/Shaders through Expand + the essl100 prelude), and --
# only with GLTEST_OWNER_LOCAL=ON (fid1-fixup round 2: opt-in, no longer automatic) -- the owner-local stylizer shader + its CPU reference
# (engine/Derived, git-ignored; fid1-fixup D1). That build holds derived code: it is named shepherd_gltest-OWNER-ONLY.vpk, has its own
# owner-only TITLE_ID (SHPD00092, the SHPD0009x block) and is never shared. Default: the tracked programs only, the Stylizer skipped,
# shepherd_gltest.vpk (SHPD00007), whatever this machine has in engine/Derived.
option(GLTEST_OWNER_LOCAL "build the owner-local Stylizer parts in (an OWNER-ONLY VPK, SHPD00092; never shared)" OFF)
get_filename_component(REPO "${CMAKE_SOURCE_DIR}/../.." ABSOLUTE)
set(OWNER_STY_FRAG "${REPO}/engine/Derived/Shepherd.Engine.Derived/Shaders/stylizer.frag")
set(OWNER_STY_REF "${REPO}/engine/Derived/vita-gltest/stylizer_ref.c")
set(OWNER_DEPS)
set(OWNER_SRCS)
set(OWNER_ARG)
set(GLTEST_VPK shepherd_gltest.vpk)
set(OWNER_STATE_TEXT "stylizer=absent")
if(GLTEST_OWNER_LOCAL)
  if(NOT EXISTS "${OWNER_STY_FRAG}" OR NOT EXISTS "${OWNER_STY_REF}")
    message(FATAL_ERROR "GLTEST_OWNER_LOCAL=ON, but this machine has no owner-local stylizer (engine/Derived: Shaders/stylizer.frag + vita-gltest/stylizer_ref.c)")
  endif()
  set(OWNER_DEPS "${OWNER_STY_FRAG}")
  set(OWNER_SRCS "${OWNER_STY_REF}")
  set(OWNER_ARG "--owner")
  set(OWNER_STATE_TEXT "stylizer=owner-local")
  set(VITA_TITLEID "SHPD00092")
  set(VITA_APP_NAME "Shepherd GL Test OWNER")
  set(GLTEST_VPK shepherd_gltest-OWNER-ONLY.vpk)
  message(STATUS "gltest: GLTEST_OWNER_LOCAL: the owner-local stylizer is built in -- ${GLTEST_VPK} (SHPD00092) holds derived code: never share it")
elseif(EXISTS "${OWNER_STY_FRAG}")
  message(STATUS "gltest: the owner-local stylizer is on this machine but not built in (GLTEST_OWNER_LOCAL=1 builds the OWNER-ONLY test)")
endif()
# a configure-time stamp of that state: when it flips (the owner-local files appear or go), the header is regenerated
set(OWNER_STATE "${GEN_DIR}/owner-state.txt")
file(MAKE_DIRECTORY "${GEN_DIR}")
set(_owner_old "")
if(EXISTS "${OWNER_STATE}")
  file(READ "${OWNER_STATE}" _owner_old)
endif()
if(NOT "${_owner_old}" STREQUAL "${OWNER_STATE_TEXT}\n")
  file(WRITE "${OWNER_STATE}" "${OWNER_STATE_TEXT}\n")
endif()
add_custom_command(OUTPUT "${GEN_DIR}/shaders_gen.h"
  COMMAND ${CMAKE_COMMAND} -E make_directory "${GEN_DIR}"
  COMMAND python3 "${CMAKE_SOURCE_DIR}/tools/gen_shaders.py" "${REPO}" "${GEN_DIR}/shaders_gen.h" ${OWNER_ARG}
  DEPENDS "${CMAKE_SOURCE_DIR}/tools/gen_shaders.py" "${OWNER_STATE}"
          "${REPO}/engine/Shepherd.Engine/Render/Shaders/sprite.vert" "${REPO}/engine/Shepherd.Engine/Render/Shaders/sprite.frag"
          "${REPO}/engine/Shepherd.Engine/Render/Shaders/post.vert" "${REPO}/engine/Shepherd.Engine/Render/Shaders/copy.frag"
          ${OWNER_DEPS}
          "${CMAKE_SOURCE_DIR}/src/candidates/tintblack.vert" "${CMAKE_SOURCE_DIR}/src/candidates/tintblack.frag"
  COMMENT "gen_shaders.py")
add_custom_target(gen_shaders DEPENDS "${GEN_DIR}/shaders_gen.h")

# ---- libshepgl.a: what the engine host links (plus vitaGL) ----
add_library(shepgl STATIC shepgl/shepgl_vita.c)
target_include_directories(shepgl PUBLIC "${CMAKE_SOURCE_DIR}/shepgl")

# ---- the smoke test ----
add_executable(shepherd_gltest src/gltest.c src/ref.c ${OWNER_SRCS} src/plat_vita.c ${SAMPLES_COMMON}/debugScreen.c)
add_dependencies(shepherd_gltest gen_shaders)
target_include_directories(shepherd_gltest PRIVATE "${CMAKE_SOURCE_DIR}/src" "${GEN_DIR}" ${SAMPLES_COMMON})
target_compile_definitions(shepherd_gltest PRIVATE GLTEST_BUILD="${GLTEST_BUILD}")
target_link_options(shepherd_gltest PRIVATE -Wl,--undefined=sceUserMainThreadStackSize -Wl,--undefined=_newlib_heap_size_user)
target_link_libraries(shepherd_gltest
  shepgl vitaGL vitashark SceShaccCgExt mathneon taihen_stub
  SceShaccCg_stub SceKernelDmacMgr_stub SceGxm_stub SceDisplay_stub SceAppMgr_stub
  SceCommonDialog_stub SceIme_stub SceSysmodule_stub SceCtrl_stub ScePower_stub SceAppUtil_stub
  m stdc++)

vita_create_self(eboot.bin shepherd_gltest UNSAFE)
# the LiveArea set (8-bit indexed PNGs; vita/probe's known-good template/bg/startup): VitaShell refuses a VPK without it
set(LIVEAREA "${CMAKE_SOURCE_DIR}/../probe/sce_sys/livearea/contents")
vita_create_vpk(${GLTEST_VPK} ${VITA_TITLEID} eboot.bin
  VERSION ${VITA_VERSION}
  NAME ${VITA_APP_NAME}
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/icon0.png sce_sys/icon0.png
  FILE ${LIVEAREA}/bg.png sce_sys/livearea/contents/bg.png
  FILE ${LIVEAREA}/startup.png sce_sys/livearea/contents/startup.png
  FILE ${LIVEAREA}/template.xml sce_sys/livearea/contents/template.xml)
