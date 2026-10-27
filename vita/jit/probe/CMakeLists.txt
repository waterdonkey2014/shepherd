cmake_minimum_required(VERSION 3.16)
if(NOT DEFINED CMAKE_TOOLCHAIN_FILE)
  if(DEFINED ENV{VITASDK})
    set(CMAKE_TOOLCHAIN_FILE "$ENV{VITASDK}/share/vita.toolchain.cmake" CACHE PATH "toolchain file")
  else()
    message(FATAL_ERROR "Please define VITASDK to point to your SDK path!")
  endif()
endif()
project(shepherd_jitprobe C)
include("${VITASDK}/share/vita.cmake" REQUIRED)

# Kit #7 JIT stage 1 device probe (vita/notes/kit7-jit.md). BUILT but NOT RUN on a device: its first exec-memory step waits for the
# owner's confirmation (main_vita.c leaves auto_confirm off). Shareable: synthetic code only, no game code or assets.
set(VITA_APP_NAME "Shepherd JIT Probe")
set(VITA_TITLEID  "SHPD00040")   # claimed by the kit #7 jit track (vita/notes/title-ids.md); reconciled at integration
set(VITA_VERSION  "01.01")   # revision 2 (the kit #7 review fixes)
set(VITA_MKSFOEX_FLAGS "-d ATTRIBUTE2=12" CACHE STRING "vita-mksfoex flags" FORCE)

set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -std=gnu11 -O2 -Wall -Wextra")
set(SAMPLES_COMMON "$ENV{VITASDK}/share/gcc-arm-vita-eabi/samples/common")
include_directories(${SAMPLES_COMMON} ${CMAKE_SOURCE_DIR})

add_executable(shepherd_jitprobe main_vita.c probe_core.c emit.c native.c plat_vita.c ${SAMPLES_COMMON}/debugScreen.c)
target_link_options(shepherd_jitprobe PRIVATE "-Wl,-Map=${CMAKE_BINARY_DIR}/shepherd_jitprobe.map")
# SceSysmem carries the ForVM API (sceKernelAllocMemBlockForVM / Open / Close / SyncVMDomain) and sceKernelGetModel / IsPSVitaTV;
# the threads, power and ctrl stubs are the usual ones. HENkaku's own chain (not kubridge) unlocks ForVM for an unsafe eboot.
target_link_libraries(shepherd_jitprobe
  SceSysmem_stub SceKernelThreadMgr_stub SceLibKernel_stub SceKernelModulemgr_stub
  ScePower_stub SceCtrl_stub SceDisplay_stub SceProcessmgr_stub)

vita_create_self(eboot.bin shepherd_jitprobe UNSAFE)   # UNSAFE: ForVM needs an unsafe eboot (as every Vita dynarec homebrew)
vita_create_vpk(shepherd_jitprobe.vpk ${VITA_TITLEID} eboot.bin
  VERSION ${VITA_VERSION}
  NAME ${VITA_APP_NAME}
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/icon0.png sce_sys/icon0.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/bg.png sce_sys/livearea/contents/bg.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/startup.png sce_sys/livearea/contents/startup.png
  FILE ${CMAKE_SOURCE_DIR}/sce_sys/livearea/contents/template.xml sce_sys/livearea/contents/template.xml)
