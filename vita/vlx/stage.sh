#!/bin/zsh
# vita/vlx/stage.sh: builds a vlx root (what VLX_ROOT points at): <root>/app0 = the VPK's files, <root>/ux0/data = the card.
#   stage.sh hello <root>   app0:managed = hello's packaged DLLs + hello-data.txt
#   stage.sh menu  <root>   app0:managed = the menu VPK's DLLs; ux0:data/shepherd = a clone of the staged owner folder
#                           (vita/shepherd/stage-owner.sh output, default ${SHEPHERD_WORK:-$HOME/shepherd-work}/v6b/owner/shepherd; derived
#                           data, so the root must stay on the build volume and never be shared)
set -euo pipefail
HERE=${0:a:h}; B=${SHEPHERD_WORK:-$HOME/shepherd-work}
APP=${1:?usage: stage.sh hello|menu <root>}; R=${2:?usage: stage.sh hello|menu <root>}
case $R in $B/devfail1*) echo "refusing a root under devfail1: $R"; exit 1 ;; $B/*) ;; *) echo "refusing a root outside $B: $R"; exit 1 ;; esac
rm -rf "$R"; mkdir -p "$R/app0/managed" "$R/ux0/data"
case $APP in
  hello) W=${SHEPHERD_HELLO_WORK:-$B/hello-v5}
         for a in System.Private.CoreLib System.Runtime System.Console System.Collections System.Threading System.Threading.Thread \
                  System.Runtime.InteropServices Microsoft.Win32.Primitives System.Memory System.Collections.Concurrent Hello HelloInterp; do
           cp "$W/bcl/$a.dll" "$R/app0/managed/"; done
         for f in "$W"/bcl/HelloDiag*.dll(N); do cp "$f" "$R/app0/managed/"; done   # the devfail1 hello diag build
         cp "$HERE/../hello/managed/hello-data.txt" "$R/app0/managed/"; mkdir -p "$R/ux0/data/shepherd" ;;
  menu)  W=${SHEPHERD_V6_WORK:-$B/shepherd-v6}; OWN=${SHEPHERD_OWNER_DIR:-$B/v6b/owner/shepherd}
         cp "$W"/bcl/*.dll "$R/app0/managed/"; mkdir -p "$R/ux0/data/shepherd"
         # kit #8: a launcher build's engine module (the launcher's loader stand-in checks the file is there, as the device's would)
         [[ -f $W/obj/shepherd.suprx ]] && { mkdir -p "$R/app0/module"; cp "$W/obj/shepherd.suprx" "$R/app0/module/"; }
         # kit #8.1: the module's imported libraries + the libraries loaded at its load (vlx.c models the device's import binding: 0x8002D003)
         [[ -f $W/obj/shepherd.suprx && -f $W/obj/module-imports.txt ]] && cp "$W/obj/module-imports.txt" "$R/app0/module/shepherd-imports.txt"
         [[ -f $W/obj/shepherd.suprx ]] && cp "$HERE/../shepherd/module-load-libs.txt" "$R/app0/module/load-libs.txt"
         for d in managed pack data persistent; do cp -Rc "$OWN/$d" "$R/ux0/data/shepherd/" 2>/dev/null || cp -R "$OWN/$d" "$R/ux0/data/shepherd/"; done
         cp "$OWN/pad.txt" "$R/ux0/data/shepherd/" ;;
  *) echo "usage: stage.sh hello|menu <root>"; exit 2 ;;
esac
echo "staged $APP root: $R ($(du -sh "$R" | cut -f1))"
