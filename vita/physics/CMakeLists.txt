# vita/physics: libshepphys = vendored Box2D 2.3.1 (patches P1 / P2 / P3 / P5: box2d/PATCHES.md) + our flat C shim + the T1 determinism scenarios.
#   Mac (host toolchain):  libshepphys.dylib (engine P/Invoke later), libshepphys_s.a, t1_runner (record / check goldens)
#   Vita (VITASDK toolchain file): libshepphys_vita.a, the T1 test eboot (shepherd_phys_t1 + eboot.bin + VPK)
# Configure with the ~/src path (no spaces); build.sh does all of it, with build trees on $SHEPHERD_WORK.
cmake_minimum_required(VERSION 3.18)

option(SHEPPHYS_TRIG_OWN "route Box2D's sinf/cosf to the deterministic sp_sinf/sp_cosf (build-level, no Box2D file changed)" ON)
set(SHEPPHYS_FP_CONTRACT "off" CACHE STRING "-ffp-contract mode (off is the contract; 'on'/'fast' only for the FMA-risk check)")
set(SHEPPHYS_OPT "-O2" CACHE STRING "optimisation flag")
option(SHEPPHYS_NDEBUG "compile Box2D's b2Assert out (NDEBUG)" ON)
option(SHEPPHYS_PROF "Mac: keep Box2D's stock b2Timer (gettimeofday) for b2Profile phase splits -- the 'prof' build; OFF = B3" OFF)
option(SHEPPHYS_GJK_STATS "P3: keep b2Distance.cpp's GJK statistics counters (stock; the Shepherd build compiles them out)" OFF)
option(SHEPPHYS_B2 "B2: build without Box2D's b2Settings.cpp and route b2Alloc / b2Free to the arena (shim/sp_b2glue.cpp); OFF = stock" ON)

if(VITA_BUILD)
  if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  endif()
endif()

project(shepphys C CXX)

set(B2 "${CMAKE_SOURCE_DIR}/box2d")
if(SHEPPHYS_GJK_STATS)
  set(SP_GJK_STATS 1)
else()
  set(SP_GJK_STATS 0)
endif()
file(GLOB_RECURSE B2_SRCS CONFIGURE_DEPENDS "${B2}/Box2D/*.cpp")
list(LENGTH B2_SRCS B2_N)
if(NOT B2_N EQUAL 46)
  message(FATAL_ERROR "expected Box2D 2.3.1's 46 .cpp files, found ${B2_N}")
endif()
set(SHIM_SRCS "${CMAKE_SOURCE_DIR}/shim/shepphys.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_trig.c"
  "${CMAKE_SOURCE_DIR}/shim/sp_core.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_trace.cpp"   # Stage 0: runtime + pool, settings check, trace
  "${CMAKE_SOURCE_DIR}/shim/sp_unity.cpp"                                          # Stage 1: the Unity layer
  "${CMAKE_SOURCE_DIR}/shim/sp_query.cpp"                                          # Stage 2: the Unity queries
  "${CMAKE_SOURCE_DIR}/shim/sp_mesh3d.cpp"                                         # Stage 6: the 3D island-ground ray
  "${CMAKE_SOURCE_DIR}/shim/sp_p3.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_jobs.cpp")     # Stage 7 item 2 (kit #9): patch P3's forked phases + the worker pool
set(T1_SRCS "${CMAKE_SOURCE_DIR}/tests/sp_t1.cpp" "${CMAKE_SOURCE_DIR}/tests/sp_s0.c" "${CMAKE_SOURCE_DIR}/tests/sp_s1.cpp"
  "${CMAKE_SOURCE_DIR}/tests/sp_s2.cpp" "${CMAKE_SOURCE_DIR}/tests/sp_s3.cpp" "${CMAKE_SOURCE_DIR}/tests/sp_s6.cpp"
  "${CMAKE_SOURCE_DIR}/tests/sp_s7.cpp")   # kit #9: patch P3's self-test

# B3 (box2d/PATCHES.md; physics-plan-v2 section 1.2 item 5): the Mac library leaves out Box2D's Common/b2Timer.cpp and links
# shim/sp_timer_stub.cpp -- the same empty bodies b2Timer.cpp's last branch gives the Vita build -- so b2Profile reads 0 and the Mac
# pays no gettimeofday per island. No Box2D file is changed. The prof build (SHEPPHYS_PROF=ON, build.sh: build/mac-prof/) keeps it.
set(TIMER_SRCS "")
if(NOT VITA AND NOT SHEPPHYS_PROF)
  list(FILTER B2_SRCS EXCLUDE REGEX "/Common/b2Timer\\.cpp$")
  list(LENGTH B2_SRCS B2_N3)
  if(NOT B2_N3 EQUAL 45)
    message(FATAL_ERROR "B3: expected 45 Box2D .cpp files without b2Timer.cpp, found ${B2_N3}")
  endif()
  set(TIMER_SRCS "${CMAKE_SOURCE_DIR}/shim/sp_timer_stub.cpp")
endif()

# B2 (box2d/PATCHES.md; physics-plan-v2 section 1.2 item 5, Stage 1): every build leaves out Box2D's Common/b2Settings.cpp and links
# shim/sp_b2glue.cpp (b2_version, b2Log as stock; b2Alloc / b2Free through libshepphys's arena). No Box2D file is changed.
# SHEPPHYS_B2=OFF keeps the stock file (the build-level switch back).
set(B2GLUE_SRCS "")
set(B2_DEFS "")
if(SHEPPHYS_B2)
  list(LENGTH B2_SRCS B2_NB0)
  list(FILTER B2_SRCS EXCLUDE REGEX "/Common/b2Settings\\.cpp$")
  list(LENGTH B2_SRCS B2_NB1)
  math(EXPR B2_NB_EXPECT "${B2_NB0} - 1")
  if(NOT B2_NB1 EQUAL B2_NB_EXPECT)
    message(FATAL_ERROR "B2: b2Settings.cpp not found in the Box2D sources")
  endif()
  set(B2GLUE_SRCS "${CMAKE_SOURCE_DIR}/shim/sp_b2glue.cpp")
  set(B2_DEFS SHEPPHYS_B2_POOL=1)
endif()

# ---- flags shared by every target: the determinism contract (physics-plan.md 1.9) ----
set(FP_FLAGS "-ffp-contract=${SHEPPHYS_FP_CONTRACT}")      # no fused multiply-add anywhere
set(CXX_ONLY "-std=c++11 -fno-exceptions -fno-rtti")
set(COMMON "${SHEPPHYS_OPT} ${FP_FLAGS} -fno-strict-aliasing")
if(VITA)
  set(TARGET_FLAGS "-mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -ffunction-sections -fdata-sections -g0")
  set(COMMON "${COMMON} ${TARGET_FLAGS} -ffile-prefix-map=${CMAKE_SOURCE_DIR}=/shepphys")
else()
  set(COMMON "${COMMON} -fPIC -fvisibility=hidden")
  set(CXX_ONLY "${CXX_ONLY} -fvisibility-inlines-hidden")
endif()
if(SHEPPHYS_NDEBUG)
  add_compile_definitions(NDEBUG)
endif()
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${COMMON} -std=gnu11")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${COMMON} ${CXX_ONLY}")
set(CMAKE_C_FLAGS_RELEASE "")
set(CMAKE_CXX_FLAGS_RELEASE "")
set(CMAKE_C_FLAGS_DEBUG "-g")
set(CMAKE_CXX_FLAGS_DEBUG "-g")
string(REPLACE "\"" "" FLAGS_STR "${SHEPPHYS_OPT} ${FP_FLAGS} ${CXX_ONLY} ${TARGET_FLAGS}")
string(STRIP "${FLAGS_STR}" FLAGS_STR)

set(TRIG_DEFS "")
set(TRIG_OPT "")
if(SHEPPHYS_TRIG_OWN)
  set(TRIG_DEFS SHEPPHYS_TRIG_OWN=1)
  set(TRIG_OPT -include "${CMAKE_SOURCE_DIR}/shim/sp_trig_override.h")
endif()

# Box2D: warnings off (third-party, unmodified); our trig override force-included; Box2D's include root is box2d/
function(sp_core_objects name)
  add_library(${name} OBJECT ${B2_SRCS} ${TIMER_SRCS} ${B2GLUE_SRCS} ${SHIM_SRCS} ${T1_SRCS})
  target_include_directories(${name} PUBLIC "${B2}" "${CMAKE_SOURCE_DIR}/shim" "${CMAKE_SOURCE_DIR}/tests")
  # B2_GJK_STATS=0 (patch P3, box2d/PATCHES.md): b2Distance.cpp's GJK statistics counters compiled out (the altered file's default, 1, is
  # stock); SHEPPHYS_GJK_STATS=ON keeps them (the T1 flag-off proof: build.sh verify's gjk-stats variant)
  target_compile_definitions(${name} PRIVATE ${TRIG_DEFS} ${B2_DEFS} SHEPPHYS_FLAGS="${FLAGS_STR}" B2_GJK_STATS=${SP_GJK_STATS})
  set_source_files_properties(${B2_SRCS} PROPERTIES COMPILE_OPTIONS "-w;${TRIG_OPT}")
  # the shim and T1 C++ files include Box2D headers (b2Rot::Set is inline there), so they get the same trig routing;
  # sp_trig.c defines sp_sinf / sp_cosf and calls no libm
  set_source_files_properties("${CMAKE_SOURCE_DIR}/shim/shepphys.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_core.cpp"
    "${CMAKE_SOURCE_DIR}/shim/sp_trace.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_unity.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_query.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_mesh3d.cpp"
    "${CMAKE_SOURCE_DIR}/shim/sp_p3.cpp" "${CMAKE_SOURCE_DIR}/shim/sp_jobs.cpp" ${B2GLUE_SRCS} ${T1_SRCS} PROPERTIES
    COMPILE_OPTIONS "-Wall;-Wextra;-Wno-unused-parameter;${TRIG_OPT}")
  if(TIMER_SRCS)
    set_source_files_properties(${TIMER_SRCS} PROPERTIES COMPILE_OPTIONS "-Wall;-Wextra;-Wno-unused-private-field")
  endif()
  set_source_files_properties("${CMAKE_SOURCE_DIR}/shim/sp_trig.c" PROPERTIES COMPILE_OPTIONS "-Wall;-Wextra")
endfunction()

if(VITA)
  include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
  sp_core_objects(shepphys_obj)
  add_library(shepphys_vita STATIC $<TARGET_OBJECTS:shepphys_obj>)

  # ---- the T1 test eboot: runs every scenario, compares with the golden hashes, prints FPSCR ----
  set(VITA_APP_NAME "Shepherd Phys T1")
  set(VITA_TITLEID  "SHPD00020")
  set(VITA_VERSION  "01.00")
  set(SAMPLES_COMMON "$ENV{VITASDK}/share/gcc-arm-vita-eabi/samples/common")
  add_executable(shepherd_phys_t1 "${CMAKE_SOURCE_DIR}/vita/t1_main.c" ${SAMPLES_COMMON}/debugScreen.c)
  target_include_directories(shepherd_phys_t1 PRIVATE "${CMAKE_SOURCE_DIR}/shim" "${CMAKE_SOURCE_DIR}/tests" ${SAMPLES_COMMON})
  set_source_files_properties("${CMAKE_SOURCE_DIR}/vita/t1_main.c" ${SAMPLES_COMMON}/debugScreen.c PROPERTIES COMPILE_OPTIONS "-Wall")
  target_link_options(shepherd_phys_t1 PRIVATE -Wl,--gc-sections -Wl,-Map=${CMAKE_BINARY_DIR}/shepherd_phys_t1.map
    -Wl,--undefined=sceUserMainThreadStackSize -Wl,--undefined=_newlib_heap_size_user)
  target_link_libraries(shepherd_phys_t1 shepphys_vita SceDisplay_stub SceCtrl_stub SceSysmem_stub SceLibKernel_stub ScePower_stub stdc++ m)
  vita_create_self(eboot.bin shepherd_phys_t1 UNSAFE)
  # the LiveArea set (8-bit indexed PNGs; vita/probe's known-good template/bg/startup): VitaShell refuses a VPK without it
  set(LIVEAREA "${CMAKE_SOURCE_DIR}/../probe/sce_sys/livearea/contents")
  vita_create_vpk(shepherd_phys_t1.vpk ${VITA_TITLEID} eboot.bin
    VERSION ${VITA_VERSION}
    NAME ${VITA_APP_NAME}
    FILE ${CMAKE_SOURCE_DIR}/vita/sce_sys/icon0.png sce_sys/icon0.png
    FILE ${LIVEAREA}/bg.png sce_sys/livearea/contents/bg.png
    FILE ${LIVEAREA}/startup.png sce_sys/livearea/contents/startup.png
    FILE ${LIVEAREA}/template.xml sce_sys/livearea/contents/template.xml)
else()
  sp_core_objects(shepphys_obj)
  add_library(shepphys SHARED $<TARGET_OBJECTS:shepphys_obj>)
  set_target_properties(shepphys PROPERTIES OUTPUT_NAME shepphys INSTALL_NAME_DIR "@rpath" LINKER_LANGUAGE CXX)
  # the link map (B3's check: b2Timer's symbols come from sp_timer_stub.o, not b2Timer.o)
  target_link_options(shepphys PRIVATE "-Wl,-map,${CMAKE_BINARY_DIR}/libshepphys.map")
  add_library(shepphys_s STATIC $<TARGET_OBJECTS:shepphys_obj>)
  set_target_properties(shepphys_s PROPERTIES OUTPUT_NAME shepphys)
  add_executable(t1_runner "${CMAKE_SOURCE_DIR}/tests/t1_runner.c")
  target_include_directories(t1_runner PRIVATE "${CMAKE_SOURCE_DIR}/shim" "${CMAKE_SOURCE_DIR}/tests")
  set_source_files_properties("${CMAKE_SOURCE_DIR}/tests/t1_runner.c" PROPERTIES COMPILE_OPTIONS "-Wall;-Wextra;-Wno-unused-parameter")
  target_link_libraries(t1_runner shepphys)
  set_target_properties(t1_runner PROPERTIES BUILD_RPATH "${CMAKE_BINARY_DIR}")
endif()
