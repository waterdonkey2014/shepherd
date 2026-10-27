#!/bin/zsh
# SHEPHERD-VITA LLVM: the engine-AOT foreign-code gate for an AOT image that may have an LLVM part (vita/notes/llvm-aot.md §4.4, §7).
# Used by gates.sh and by shepherd-build-llvm.patch (vita/shepherd/build.sh with SHEPHERD_AOT_LLVM set), so both apply one rule.
#
# usage: foreign-gate.sh <assembly name> <Mono part: M.dll.o> <LLVM part: M-llvm.o | -> <out prefix>
#          writes <out prefix>-methods.txt (every code symbol of both parts as nm names them: Mono part first) and
#          <out prefix>-foreign.txt (one line per foreign symbol: part, symbol, reason); prints one summary line.
#          exit 0 = nothing foreign, 1 = foreign symbols (listed), 2 = the gate could not run (missing object, nm failure, usage)
#        foreign-gate.sh --self-test
#          synthetic symbol lists (incl. the names the pre-2026-10-07 gate let through) + a check that vita/shepherd/build.sh still
#          carries the allow-list copied below. exit 0 = pass
#
# Mono part (<M>.dll.o): the exact allow-list of vita/shepherd/build.sh's engine-AOT gate -- the engine's and the BCL's
#   namespaces, the BCL's primitive types as Mono mangles them, runtime wrappers / PLT / unbox trampolines, the image's own data
#   labels -- plus, when there is an LLVM part, the PLT entries the Mono part emits for LLVM callers (p_<n>_plt_<callee>_llvm: call
#   stubs named after their callee, Unity callees included, not bodies). Mono-backend method symbols carry no assembly prefix
#   (aot-compiler.c: get_debug_sym), so build.sh's test applies to them as it is.
# LLVM part (<M>-llvm.o): in a static build every LLVM method is named "<assembly_name_sym>_<name>" (aot-compiler.c
#   mono_aot_get_method_name; assembly_name_sym = get_assembly_prefix(): "corlib" for CoreLib, else the assembly name with every
#   character outside [A-Za-z0-9_] replaced by '_'). For Shepherd.Engine every LLVM method name starts with "Shepherd_Engine_",
#   BCL code and wrappers included, so build.sh's '^Shepherd_' test would let a Unity body such as
#   Shepherd_Engine_UnityEngine_Vector3_op_Addition through. Hence:
#   - a prefixed name is checked after stripping the prefix, against the managed part of build.sh's list (its namespaces, primitive
#     types and wrapper_; not Mono's own label prefixes plt_/ut_/unbox_/method_/mono_/jit_/got_/extra_/class_, which LLVM never
#     emits as method names) plus the one mangled-name kind mono_aot_get_method_name produces: the gsharedvt in/out signature
#     wrappers aot_wrapper_gsharedvt_{in,out}_sig_<signature> (signature-generic transition code, no method body);
#   - an unprefixed code symbol must be one of the LLVM module's own helpers (gc.safepoint_poll, llvm_code_start/end,
#     mono_aot_<assembly_name_sym>_*: init_method*, init_aotconst, eh_frame, icall_cold_wrapper_<n>); anything else is foreign.
#     This also makes a wrong prefix fail loudly (every method would be listed) instead of silently re-opening the hole.
# fid1-fixup D1: the owner-local Shepherd.Engine.Derived (derived render code, vita/shepherd/build.sh SHEPHERD_AOT_DERIVED=1) is AOT-compiled
#   as its own module. Its methods (Mono part: Shepherd_Engine_Derived_*; LLVM part: Shepherd_Engine_Derived_Shepherd_Engine_Derived_*)
#   pass the '^Shepherd_' test, so in every OTHER module's image a name that starts with Shepherd_Engine_Derived_ (after stripping the
#   module's prefix in the LLVM part) is foreign; only the module Shepherd.Engine.Derived accepts its own prefix. vita/shepherd/build.sh
#   applies the same deny to the engine image (DENY_RE below, verbatim; --self-test checks it).
set -uo pipefail
export PATH="$HOME/vitasdk/bin:$PATH"
HERE=${0:A:h}; V=${HERE:h}

# vita/shepherd/build.sh's two expressions, verbatim (--self-test checks they are still there)
ALLOW_RE='^(Shepherd_|System_|Microsoft_|Internal_|Interop_|wrapper_|plt_|ut_|unbox_|method_|mono_|jit_|got_|extra_|class_|(s?byte|u?int(16|32|64)?|u?long|u?short|single|double|bool|char|string|object|u?intptr|decimal)_)'
DATA_RE='(assembly_guid|assembly_name|blob|ex_info_offsets|image_table|mem_end|name_[0-9]+|plt|runtime_version|unwind_info|weak_field_indexes)'
# Mono part, LLVM images only: the LLVM-callable PLT entries
LLVM_PLT_RE='^p_[0-9]+_plt_.*_llvm$'
# LLVM part, after stripping "<assembly_name_sym>_": managed names only, + the gsharedvt signature wrappers
LLVM_ALLOW_RE='^(Shepherd_|System_|Microsoft_|Internal_|Interop_|wrapper_|(s?byte|u?int(16|32|64)?|u?long|u?short|single|double|bool|char|string|object|u?intptr|decimal)_|aot_wrapper_gsharedvt_(in|out)_sig_)'
# the owner-local derived module's prefix: foreign in any image but its own
DENY_RE='^Shepherd_Engine_Derived_'

asm_sym() {  # aot-compiler.c: assembly_name_sym
  case $1 in System.Private.CoreLib) print corlib ;; corlib) print __corlib__ ;; *) print -r -- ${1//[^A-Za-z0-9_]/_} ;; esac
}

# classify <assembly name> <Mono part names> <LLVM part names | ''> <foreign out>
#   -> "<foreign> <llvm prefixed> <llvm gsharedvt sig wrappers> <llvm helpers>" on stdout
classify() {
  local sym; sym=$(asm_sym "$1"); : > "$4"
  local deny=$DENY_RE; [ "$1" = Shepherd.Engine.Derived ] && deny='^$'   # (its own module: its prefix is its methods)
  DENY_RE=$deny P="${sym}_" HELPER_RE="^(gc[.]safepoint_poll|llvm_code_start|llvm_code_end|mono_aot_${sym}_[A-Za-z0-9_]+)\$" \
  ALLOW_RE=$ALLOW_RE DATA_RE="^${DATA_RE}\$" LLVM_PLT_RE=$LLVM_PLT_RE LLVM_ALLOW_RE=$LLVM_ALLOW_RE HAVE_LLVM=${3:+1} \
  awk -v foreign="$4" '
    BEGIN { p = ENVIRON["P"]; lp = length(p); mono = ARGV[1]; nf = ns = nw = nh = 0 }
    FILENAME == mono {
      if ($0 ~ ENVIRON["DENY_RE"]) { print "mono\t" $0 "\towner-local derived code outside the Shepherd.Engine.Derived module" > foreign; nf++; next }
      if ($0 ~ ENVIRON["ALLOW_RE"] || $0 ~ ENVIRON["DATA_RE"] || (ENVIRON["HAVE_LLVM"] == "1" && $0 ~ ENVIRON["LLVM_PLT_RE"])) next
      print "mono\t" $0 "\tnot an engine/BCL namespace, wrapper, stub or data label" > foreign; nf++; next
    }
    substr($0, 1, lp) == p {
      s = substr($0, lp + 1); ns++
      if (s ~ /^aot_wrapper_gsharedvt_(in|out)_sig_/) nw++
      if (s ~ ENVIRON["DENY_RE"]) { print "llvm\t" $0 "\towner-local derived code outside the Shepherd.Engine.Derived module" > foreign; nf++; next }
      if (s ~ ENVIRON["LLVM_ALLOW_RE"]) next
      print "llvm\t" $0 "\t" (s == "" ? "(empty)" : s) " is not an engine/BCL method or wrapper" > foreign; nf++; next
    }
    $0 ~ ENVIRON["HELPER_RE"] { nh++; next }
    { print "llvm\t" $0 "\tno " p " prefix and not one of the module'\''s helpers" > foreign; nf++ }
    END { printf "%d %d %d %d\n", nf, ns, nw, nh }' "$2" ${3:+"$3"}
}

code_syms() {  # defined code symbols of an object (build.sh: T/t; W/w for the LLVM module's weak gc.safepoint_poll)
  arm-vita-eabi-nm "$1" | awk '$2 ~ /^[TtWw]$/ {print $3}' | { grep -v '^\.' || true; }
}

self_test() {
  local t; t=$(mktemp -d "${TMPDIR:-/tmp}/foreign-gate.XXXXXX") || return 2
  local fail=0
  # each line: <part> <symbol> <expected: ok | FOREIGN>; the old gate's verdict is computed, not listed
  cat > $t/cases.txt <<'EOF'
mono Shepherd_Engine_Render_GlBackend_Present ok
mono Shepherd_Audio_FmodSelfTest__c__DisplayClass0_0__ctor ok
mono System_Collections_Generic_List_1_UnityEngine_Vector3_Add_UnityEngine_Vector3 ok
mono uint_CompareTo_object ok
mono wrapper_managed_to_native_Shepherd_Engine_Impl_RenderIcalls_UiAlpha_intptr ok
mono plt_Shepherd_Engine_UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3 ok
mono p_1002_plt_Shepherd_Engine_UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3_llvm ok
mono ut_123 ok
mono mem_end ok
mono name_0 ok
mono plt ok
mono unwind_info ok
mono UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3 FOREIGN
mono PlayerFarming_Update FOREIGN
mono aot_wrapper_gsharedvt_out_sig_bii_i4 FOREIGN
mono gc.safepoint_poll FOREIGN
llvm Shepherd_Engine_Shepherd_Engine_Render_SceneRenderer_DrawWorld ok
llvm Shepherd_Engine_Shepherd_Audio_FmodSelfTest_Run_System_Action_2_bool_string ok
llvm Shepherd_Engine_Shepherd_Assets_SerializedFile_Read ok
llvm Shepherd_Engine_System_Array_Copy_System_Array_int_System_Array_int_int ok
llvm Shepherd_Engine_System_Collections_Generic_Dictionary_2_int_UnityEngine_Matrix4x4__ctor ok
llvm Shepherd_Engine_uint_CompareTo_object ok
llvm Shepherd_Engine_wrapper_delegate_invoke_System_Action_invoke_void ok
llvm Shepherd_Engine_wrapper_other_single_interp_in ok
llvm Shepherd_Engine_aot_wrapper_gsharedvt_out_sig_bii_i4 ok
llvm Shepherd_Engine_aot_wrapper_gsharedvt_in_sig_void_bii ok
llvm gc.safepoint_poll ok
llvm mono_aot_Shepherd_Engine_init_method ok
llvm mono_aot_Shepherd_Engine_init_method_gshared_mrgctx ok
llvm mono_aot_Shepherd_Engine_eh_frame ok
llvm mono_aot_Shepherd_Engine_icall_cold_wrapper_264 ok
llvm Shepherd_Engine_UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3 FOREIGN
llvm Shepherd_Engine_UnityEngine_Transform_get_position FOREIGN
llvm Shepherd_Engine_PlayerFarming_Update FOREIGN
llvm Shepherd_Engine_Lamb_Bleat FOREIGN
llvm Shepherd_Engine_aot_wrapper_other_UnityEngine_Vector3_op_Addition FOREIGN
llvm Shepherd_Engine_plt_UnityEngine_Vector3_op_Addition FOREIGN
llvm Shepherd_Engine_mono_Foo FOREIGN
llvm Shepherd_Engine_ FOREIGN
llvm UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3 FOREIGN
llvm Shepherd_Assets_SerializedFile_Read FOREIGN
llvm System_Array_Copy FOREIGN
llvm mono_aot_System_Linq_init_method FOREIGN
mono Shepherd_Engine_Derived_Render_Shading_Draw_Shepherd_Engine_Render_Sdl_Vertex___int_int_intptr_uint FOREIGN
llvm Shepherd_Engine_Shepherd_Engine_Derived_Render_StencilLighting_RenderLightMask FOREIGN
EOF
  awk '$1 == "mono" {print $2}' $t/cases.txt > $t/mono.txt
  awk '$1 == "llvm" {print $2}' $t/cases.txt > $t/llvm.txt
  local r; r=$(classify Shepherd.Engine $t/mono.txt $t/llvm.txt $t/foreign.txt) || { echo "self-test: classify failed"; return 2; }
  awk '$3 == "FOREIGN" {print $1 "\t" $2}' $t/cases.txt | sort > $t/want.txt
  cut -f1,2 $t/foreign.txt | sort > $t/got.txt
  if cmp -s $t/want.txt $t/got.txt; then
    echo "self-test: $(wc -l < $t/cases.txt | tr -d ' ') synthetic symbols, exactly the expected $(wc -l < $t/want.txt | tr -d ' ') foreign"
  else
    echo "self-test FAIL: foreign set differs (< expected, > got)"; diff $t/want.txt $t/got.txt; fail=1
  fi
  # the pre-fix gate (build.sh's regex + the LLVM extras, applied to both parts' raw names): what it let through
  local old; old=$(awk '$3 == "FOREIGN" && $1 == "llvm" {print $2}' $t/cases.txt \
    | grep -vE "$ALLOW_RE" | grep -vxE "$DATA_RE" | grep -vxE '(gc\.safepoint_poll|llvm_code_start|llvm_code_end)' \
    | grep -vE "$LLVM_PLT_RE" | wc -l | tr -d ' ')
  echo "self-test: the pre-fix gate flagged $old of the $(awk '$3 == "FOREIGN" && $1 == "llvm"' $t/cases.txt | wc -l | tr -d ' ') foreign LLVM-part symbols"
  # a wrong prefix must fail loudly, not pass
  r=$(classify Shepherd.Engin $t/mono.txt $t/llvm.txt $t/foreign-wrongprefix.txt)
  grep -q $'^llvm\tShepherd_Engine_Shepherd_Engine_Render_SceneRenderer_DrawWorld\t' $t/foreign-wrongprefix.txt \
    && echo "self-test: wrong assembly prefix -> $(wc -l < $t/foreign-wrongprefix.txt | tr -d ' ') foreign (fails, as it must)" \
    || { echo "self-test FAIL: a wrong assembly prefix did not fail the gate"; fail=1; }
  # the owner-local derived module accepts its own prefix (and only there): its methods, engine generics over its types, its helpers
  cat > $t/dcases.txt <<'DCASES'
mono Shepherd_Engine_Derived_Render_Shading_Draw_Shepherd_Engine_Render_Sdl_Vertex___int_int_intptr_uint ok
mono Shepherd_Engine_Render_RenderKit_Project ok
mono System_Collections_Generic_Dictionary_2_Shepherd_Engine_Native_NMaterial_Shepherd_Engine_Derived_Render_Shading_Info_TryGetValue ok
mono UnityEngine_Vector3_op_Addition_UnityEngine_Vector3_UnityEngine_Vector3 FOREIGN
llvm Shepherd_Engine_Derived_Shepherd_Engine_Derived_Render_StencilLighting_RenderLightMask ok
llvm mono_aot_Shepherd_Engine_Derived_init_method ok
llvm Shepherd_Engine_Derived_UnityEngine_Vector3_op_Addition FOREIGN
llvm Shepherd_Engine_Shepherd_Engine_Derived_Render_Shading_Draw FOREIGN
DCASES
  awk '$1 == "mono" {print $2}' $t/dcases.txt > $t/dmono.txt; awk '$1 == "llvm" {print $2}' $t/dcases.txt > $t/dllvm.txt
  r=$(classify Shepherd.Engine.Derived $t/dmono.txt $t/dllvm.txt $t/dforeign.txt) || { echo "self-test: classify (derived) failed"; return 2; }
  awk '$3 == "FOREIGN" {print $1 "\t" $2}' $t/dcases.txt | sort > $t/dwant.txt; cut -f1,2 $t/dforeign.txt | sort > $t/dgot.txt
  if cmp -s $t/dwant.txt $t/dgot.txt; then
    echo "self-test: owner-local derived module: $(wc -l < $t/dcases.txt | tr -d ' ') synthetic symbols, exactly the expected $(wc -l < $t/dwant.txt | tr -d ' ') foreign (its prefix accepted only in its own image)"
  else
    echo "self-test FAIL: derived-module foreign set differs (< expected, > got)"; diff $t/dwant.txt $t/dgot.txt; fail=1
  fi
  # assembly_name_sym as aot-compiler.c derives it
  [ "$(asm_sym Shepherd.Engine)/$(asm_sym System.Private.CoreLib)/$(asm_sym Microsoft.Bcl.AsyncInterfaces)" = \
    Shepherd_Engine/corlib/Microsoft_Bcl_AsyncInterfaces ] || { echo "self-test FAIL: assembly_name_sym"; fail=1; }
  # the Mono-part allow-list is still build.sh's
  if [ -f "$V/shepherd/build.sh" ]; then
    grep -qF -- "grep -vE '$ALLOW_RE'" "$V/shepherd/build.sh" && grep -qF -- "grep -vxE '$DATA_RE'" "$V/shepherd/build.sh" \
      && grep -qF -- "grep -E '$DENY_RE' \"\$W/obj/engine-aot-methods.txt\"" "$V/shepherd/build.sh" \
      && echo "self-test: allow-list = vita/shepherd/build.sh's (verbatim)" \
      || { echo "self-test FAIL: vita/shepherd/build.sh's foreign-code allow-list changed; update foreign-gate.sh"; fail=1; }
  fi
  rm -rf $t
  return $fail
}

if [ "${1:-}" = --self-test ]; then self_test; exit $?; fi
[ $# = 4 ] || { echo "usage: foreign-gate.sh <assembly name> <M.dll.o> <M-llvm.o | -> <out prefix> | --self-test" >&2; exit 2; }
ASM=$1; MO=$2; LO=$3; OUTP=$4
[ -f "$MO" ] || { echo "foreign-gate: no $MO" >&2; exit 2; }
[ "$LO" = - ] || [ -f "$LO" ] || { echo "foreign-gate: no $LO" >&2; exit 2; }
T=$(mktemp -d "${TMPDIR:-/tmp}/foreign-gate.XXXXXX") || exit 2
trap 'rm -rf $T' EXIT
code_syms "$MO" > $T/mono.txt || { echo "foreign-gate: nm $MO failed" >&2; exit 2; }
: > $T/llvm.txt; if [ "$LO" != - ]; then code_syms "$LO" > $T/llvm.txt || { echo "foreign-gate: nm $LO failed" >&2; exit 2; }; fi
cat $T/mono.txt $T/llvm.txt > "$OUTP-methods.txt"
r=($(classify "$ASM" $T/mono.txt "${${LO:#-}:+$T/llvm.txt}" "$OUTP-foreign.txt")) || { echo "foreign-gate: classify failed" >&2; exit 2; }
(( ${#r} == 4 )) || { echo "foreign-gate: classify failed" >&2; exit 2; }
nm_=$(wc -l < $T/mono.txt | tr -d ' '); nl_=$(wc -l < $T/llvm.txt | tr -d ' ')
if [ "$LO" = - ]; then
  echo "$((nm_ + nl_)) code symbols in $ASM's image, ${r[1]} foreign"
else
  echo "$((nm_ + nl_)) code symbols in $ASM's image (Mono part $nm_; LLVM part $nl_: ${r[2]} $(asm_sym $ASM)_-prefixed checked after stripping the prefix, ${r[3]} of them gsharedvt sig wrappers, ${r[4]} module helpers), ${r[1]} foreign"
fi
[ "${r[1]}" = 0 ] || exit 1
