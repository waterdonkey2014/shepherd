# vita/physics/physbench: the physics plan's Stage 0 device micro-benchmark (D4). Ours; synthetic scenes only.
#   Mac (host toolchain):        physbench_mac  -> the reference (out/mac_ref.h) and the Mac table; physbench_mac_prof
#   Vita (VITASDK toolchain):    shepherd_physbench (+ eboot.bin, shepherd_physbench.vpk, SHPD00012), libpbscene_vita.a (QEMU)
# Links the library build.sh of vita/physics produced (../build/mac/libshepphys.a, ../build/vita/libshepphys_vita.a), so the
# bench measures exactly the canonical libshepphys. PB_LIB_FROM_SOURCE=ON compiles the library sources in instead (the
# Mac TSan variant). Configure from the ~/src path (no spaces); physbench/build.sh does all of it.
cmake_minimum_required(VERSION 3.18)
project(physbench C CXX)

option(PB_LIB_FROM_SOURCE "compile Box2D + shim + T1 into the bench instead of linking ../build (sanitizer builds)" OFF)
set(PB_SRC_SHA "unknown" CACHE STRING "sha256 of the bench + library sources (the Mac reference is keyed by it)")
set(PB_SAN "" CACHE STRING "extra sanitizer flags (Mac only), e.g. -fsanitize=thread")
set(PB_MAC_LIB "${CMAKE_SOURCE_DIR}/../build/mac/libshepphys.a" CACHE FILEPATH "the Mac libshepphys.a to link (build.sh selftest
  points it at stage 0a's deliberately non-conforming risk-probe builds)")
# physics Stage 0 (B3): the canonical Mac library already carries the no-op b2Timer; the prof library (vita/physics/build.sh prof,
# -DSHEPPHYS_PROF=ON) keeps Box2D's stock gettimeofday timer, which physbench_mac_prof needs for b2Profile
set(PB_MAC_PROF_LIB "${CMAKE_SOURCE_DIR}/../build/mac-prof/libshepphys.a" CACHE FILEPATH "the Mac libshepphys.a with the stock b2Timer")

set(PB "${CMAKE_SOURCE_DIR}")
set(PHYS "${CMAKE_SOURCE_DIR}/..")
set(OUTDIR "${CMAKE_SOURCE_DIR}/out")

# ---- the determinism contract (physics-plan.md 1.9), same as libshepphys; bench code also not auto-vectorized ----
set(COMMON "-O2 -ffp-contract=off -fno-strict-aliasing")
if(VITA)
  set(COMMON "${COMMON} -mcpu=cortex-a9 -mfpu=neon -mfloat-abi=hard -ffunction-sections -fdata-sections -g0 -fno-tree-vectorize")
else()
  set(COMMON "${COMMON} -fno-vectorize -fno-slp-vectorize ${PB_SAN}")
endif()
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${COMMON} -std=gnu11")
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${COMMON} -std=c++11 -fno-exceptions -fno-rtti")
set(CMAKE_C_FLAGS_RELEASE "-DNDEBUG")
set(CMAKE_CXX_FLAGS_RELEASE "-DNDEBUG")
add_compile_definitions(PB_SRC_SHA="${PB_SRC_SHA}")

set(INC "${PB}" "${PHYS}/shim" "${PHYS}/tests" "${PHYS}/box2d" "${OUTDIR}")
set(WARN "-Wall;-Wextra;-Wno-unused-parameter")
set(TRIG "-include;${PHYS}/shim/sp_trig_override.h")   # Box2D's inline sinf / cosf -> sp_sinf / sp_cosf, as in the library

set(PB_CORE "${PB}/pb_bench.c" "${PB}/pb_pool.c" "${PB}/pb_b2.cpp")
set_source_files_properties("${PB}/pb_bench.c" "${PB}/pb_pool.c" "${PB}/pb_scene.c" PROPERTIES COMPILE_OPTIONS "${WARN}")
set_source_files_properties("${PB}/pb_b2.cpp" PROPERTIES COMPILE_OPTIONS "${WARN};${TRIG}")

if(PB_LIB_FROM_SOURCE)
  file(GLOB_RECURSE B2_SRCS CONFIGURE_DEPENDS "${PHYS}/box2d/Box2D/*.cpp")
  # (physics Stage 0 added sp_core.cpp, sp_trace.cpp and the self-test sp_s0.c to the library; the from-source build keeps Box2D's stock
  # b2Timer.cpp, as the canonical library did before B3)
  # (Stage 1 added the Unity layer sp_unity.cpp and its self-test sp_s1.cpp; this build keeps stock b2Settings.cpp -- B2 off -- as it
  # keeps stock b2Timer.cpp)
  set(LIB_SRCS ${B2_SRCS} "${PHYS}/shim/shepphys.cpp" "${PHYS}/shim/sp_trig.c" "${PHYS}/shim/sp_core.cpp" "${PHYS}/shim/sp_trace.cpp"
      "${PHYS}/shim/sp_unity.cpp" "${PHYS}/tests/sp_t1.cpp" "${PHYS}/tests/sp_s0.c" "${PHYS}/tests/sp_s1.cpp")
  set_source_files_properties(${B2_SRCS} PROPERTIES COMPILE_OPTIONS "-w;${TRIG}")
  set_source_files_properties("${PHYS}/shim/shepphys.cpp" "${PHYS}/shim/sp_core.cpp" "${PHYS}/shim/sp_trace.cpp" "${PHYS}/shim/sp_unity.cpp"
      "${PHYS}/tests/sp_t1.cpp" "${PHYS}/tests/sp_s0.c" "${PHYS}/tests/sp_s1.cpp" PROPERTIES COMPILE_OPTIONS "-w;${TRIG}")
  set_source_files_properties("${PHYS}/shim/sp_trig.c" PROPERTIES COMPILE_OPTIONS "-w")
endif()

if(VITA)
  include("$ENV{VITASDK}/share/vita.cmake" REQUIRED)
  add_library(pbscene_vita STATIC "${PB}/pb_scene.c")      # also linked into the QEMU check (build.sh qemu)
  target_include_directories(pbscene_vita PRIVATE ${INC})

  set(VITA_APP_NAME "Shepherd PhysBench")
  set(VITA_TITLEID  "SHPD00012")
  set(VITA_VERSION  "01.00")
  set(SAMPLES_COMMON "$ENV{VITASDK}/share/gcc-arm-vita-eabi/samples/common")
  add_executable(shepherd_physbench "${PB}/vita_main.c" "${PB}/pb_plat_vita.c" ${PB_CORE} ${SAMPLES_COMMON}/debugScreen.c)
  target_include_directories(shepherd_physbench PRIVATE ${INC} ${SAMPLES_COMMON})
  set_source_files_properties("${PB}/vita_main.c" "${PB}/pb_plat_vita.c" PROPERTIES COMPILE_OPTIONS "${WARN}")
  target_link_options(shepherd_physbench PRIVATE -Wl,--gc-sections -Wl,-Map=${CMAKE_BINARY_DIR}/shepherd_physbench.map
    -Wl,--undefined=sceUserMainThreadStackSize -Wl,--undefined=_newlib_heap_size_user)
  target_link_libraries(shepherd_physbench pbscene_vita "${PHYS}/build/vita/libshepphys_vita.a"
    SceDisplay_stub SceCtrl_stub SceSysmem_stub SceLibKernel_stub SceKernelThreadMgr_stub ScePower_stub stdc++ m)
  vita_create_self(eboot.bin shepherd_physbench UNSAFE)
  # the LiveArea set (8-bit indexed PNGs; vita/probe's known-good template/bg/startup): VitaShell refuses a VPK without it
  set(LIVEAREA "${PHYS}/../probe/sce_sys/livearea/contents")
  vita_create_vpk(shepherd_physbench.vpk ${VITA_TITLEID} eboot.bin
    VERSION ${VITA_VERSION}
    NAME ${VITA_APP_NAME}
    FILE ${PB}/sce_sys/icon0.png sce_sys/icon0.png
    FILE ${LIVEAREA}/bg.png sce_sys/livearea/contents/bg.png
    FILE ${LIVEAREA}/startup.png sce_sys/livearea/contents/startup.png
    FILE ${LIVEAREA}/template.xml sce_sys/livearea/contents/template.xml)
else()
  # physbench_mac: the reference. Box2D's b2Timer is replaced at link time by the Vita's no-op (pb_timer_stub.cpp; since physics
  # Stage 0 the canonical libshepphys.a carries the same no-op, B3, so the executable's own copy simply wins), so the Mac does not
  # pay gettimeofday for b2Profile while the Vita pays nothing. physbench_mac_prof: the prof library (stock b2Timer), with
  # b2Profile on (phase split + the timers' own cost).
  set(MAC_SRCS "${PB}/mac_main.c" "${PB}/pb_plat_mac.c" "${PB}/pb_scene.c" ${PB_CORE})
  set_source_files_properties("${PB}/mac_main.c" "${PB}/pb_plat_mac.c" PROPERTIES COMPILE_OPTIONS "${WARN}")
  set_source_files_properties("${PB}/pb_timer_stub.cpp" PROPERTIES COMPILE_OPTIONS "${WARN};-Wno-unused-private-field")
  if(PB_LIB_FROM_SOURCE)
    add_executable(physbench_mac ${MAC_SRCS} ${LIB_SRCS})
  else()
    add_executable(physbench_mac ${MAC_SRCS} "${PB}/pb_timer_stub.cpp")
    target_link_libraries(physbench_mac "${PB_MAC_LIB}")
    add_executable(physbench_mac_prof ${MAC_SRCS})
    target_link_libraries(physbench_mac_prof "${PB_MAC_PROF_LIB}")
    target_include_directories(physbench_mac_prof PRIVATE ${INC})
    set_target_properties(physbench_mac_prof PROPERTIES LINKER_LANGUAGE CXX)
  endif()
  target_include_directories(physbench_mac PRIVATE ${INC})
  target_link_options(physbench_mac PRIVATE ${PB_SAN})
  set_target_properties(physbench_mac PROPERTIES LINKER_LANGUAGE CXX)
endif()
