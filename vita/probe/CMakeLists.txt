cmake_minimum_required(VERSION 3.16)
if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  if(DEFINED ENV{VITASDK})
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  else()
    message(FATAL_ERROR "Please define VITASDK to point to your SDK path!")
  endif()
endif()

project(shepherd_probe C)
include("${VITASDK}/share/vita.cmake" REQUIRED)

set(VITA_APP_NAME "Shepherd Probe")
set(VITA_TITLEID  "SHPD00001")
set(VITA_VERSION  "02.00")
set(VITA_MKSFOEX_FLAGS "-d ATTRIBUTE2=12" CACHE STRING "vita-mksfoex flags" FORCE)   # +109 MiB main RAM budget (365 MiB)

# bench.c must be built with the same flags as the Mac reference (see build.sh)
set(BENCH_FLAGS "-O2 -ffp-contract=off -fno-tree-vectorize -fno-tree-slp-vectorize")
set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -std=gnu11 ${BENCH_FLAGS} -Wall")
set(CMAKE_C_FLAGS_RELEASE "-DNDEBUG")   # no extra -O3: bench.c must match the Mac reference's -O2
if(NOT DEFINED BENCH_SHA)
  set(BENCH_SHA "unknown")
endif()
set(SAMPLES_COMMON "$ENV{VITASDK}/share/gcc-arm-vita-eabi/samples/common")
include_directories(${SAMPLES_COMMON} ${CMAKE_SOURCE_DIR} ${CMAKE_SOURCE_DIR}/out)
# the owner-local stylizer kernel + shader (engine/Derived/vita-probe, git-ignored; fid1-fixup D1): only with PROBE_OWNER_LOCAL=ON
# (fid1-fixup round 2: opt-in, no longer automatic). That probe holds derived code: shepherd_probe-OWNER-ONLY.vpk, owner-only TITLE_ID
# SHPD00093 (the SHPD0009x block), never shared. Default: no stylizer measurements, shepherd_probe.vpk (SHPD00001).
option(PROBE_OWNER_LOCAL "build the owner-local stylizer kernel + shader in (an OWNER-ONLY VPK, SHPD00093; never shared)" OFF)
get_filename_component(REPO "${CMAKE_SOURCE_DIR}/../.." ABSOLUTE)
set(PROBE_VPK shepherd_probe.vpk)
if(PROBE_OWNER_LOCAL)
  if(NOT EXISTS "${REPO}/engine/Derived/vita-probe/probe_stylizer_cpu.h")
    message(FATAL_ERROR "PROBE_OWNER_LOCAL=ON, but this machine has no engine/Derived/vita-probe")
  endif()
  include_directories("${REPO}/engine/Derived/vita-probe")
  set(VITA_TITLEID "SHPD00093")
  set(VITA_APP_NAME "Shepherd Probe OWNER")
  set(PROBE_VPK shepherd_probe-OWNER-ONLY.vpk)
  message(STATUS "probe: PROBE_OWNER_LOCAL: the owner-local stylizer is built in -- ${PROBE_VPK} (SHPD00093) holds derived code: never share it")
elseif(EXISTS "${REPO}/engine/Derived/vita-probe/probe_stylizer_cpu.h")
  message(STATUS "probe: the owner-local stylizer is on this machine but not built in (PROBE_OWNER_LOCAL=1 builds the OWNER-ONLY probe)")
endif()

add_executable(shepherd_probe main.c bench.c ${SAMPLES_COMMON}/debugScreen.c)
target_compile_definitions(shepherd_probe PRIVATE BENCH_SHA256="${BENCH_SHA}" VITA_BENCH_FLAGS="${BENCH_FLAGS}")
# the link map (device kit #2): the vitaGL-app lint (GL Diag's lint.py) proves from it that only vitaShaRK loads a module
target_link_options(shepherd_probe PRIVATE "-Wl,-Map=${CMAKE_BINARY_DIR}/shepherd_probe.map")
target_link_libraries(shepherd_probe
  vitaGL vitashark SceShaccCgExt mathneon taihen_stub
  SceShaccCg_stub SceKernelDmacMgr_stub SceGxm_stub SceDisplay_stub SceAppMgr_stub
  SceCommonDialog_stub SceIme_stub SceSysmodule_stub SceCtrl_stub ScePower_stub SceAppUtil_stub
  m stdc++)

vita_create_self(eboot.bin shepherd_probe UNSAFE)
vita_create_vpk(${PROBE_VPK} ${VITA_TITLEID} eboot.bin
  VERSION ${VITA_VERSION}
  NAME ${VITA_APP_NAME}
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/icon0.png sce_sys/icon0.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/bg.png sce_sys/livearea/contents/bg.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/startup.png sce_sys/livearea/contents/startup.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/template.xml sce_sys/livearea/contents/template.xml)
