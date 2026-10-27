#!/bin/zsh
# V4 part 2 frame-time matrix (Release build, engine/Shepherd.Engine/binrel): SDL vs GL (vs GL+DXT), MemReport window 1300-1399.
# Interleaved rounds; logs -> runtime/logs/v4r/perf-<cfg>-r<n>/ (engine.log [render] summary + memreport-1299.txt).
V4="${SHEPHERD_REPO:-$HOME/src/shepherd}/vita/v4"
common=(SHEPHERD_PRESS=1050:32 SHEPHERD_MEMREPORT=1299 SHEPHERD_RENDER_STATS=1300:1399)
for r in ${=ROUNDS:-1 2}; do
  BIN=binrel $V4/run1.sh perf-sdl-r$r $common SHEPHERD_RENDER=sdl -- boot 1400 1350
  BIN=binrel $V4/run1.sh perf-gl-r$r $common SHEPHERD_RENDER=gl -- boot 1400 1350
  BIN=binrel $V4/run1.sh perf-gldxt-r$r $common SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 -- boot 1400 1350
done
if [[ -z "$NOEXTRA" ]]; then
  BIN=binrel $V4/run1.sh perf-sdl-sync $common SHEPHERD_RENDER=sdl SHEPHERD_RENDER_SYNC=1 -- boot 1400 1350
  BIN=binrel $V4/run1.sh perf-gl-sync $common SHEPHERD_RENDER=gl SHEPHERD_RENDER_SYNC=1 -- boot 1400 1350
  BIN=binrel $V4/run1.sh perf-vita-sdl SHEPHERD_PROFILE=vita "SHEPHERD_PAD=1050:cross" SHEPHERD_MEMREPORT=1299 SHEPHERD_RENDER_STATS=1300:1399 SHEPHERD_RENDER=sdl -- boot 1400 1350
  BIN=binrel $V4/run1.sh perf-vita-gl SHEPHERD_PROFILE=vita "SHEPHERD_PAD=1050:cross" SHEPHERD_MEMREPORT=1299 SHEPHERD_RENDER_STATS=1300:1399 SHEPHERD_RENDER=gl SHEPHERD_GL_DXT=1 -- boot 1400 1350
fi
