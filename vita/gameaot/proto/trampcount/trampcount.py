# lldb script (game-aot prototype): counts the CoreLib AOT trampoline pool entries a run takes, per pool type, by breaking on
# aot-runtime.c get_numerous_trampoline (tramp_type in x0; local symbol of the osx-arm64 Mono pack's libcoreclr.dylib).
# Types (MonoAotTrampoline): 0 SPECIFIC (ntrampolines), 1 STATIC_RGCTX (nrgctx-), 2 IMT (nimt-), 3 GSHAREDVT_ARG (ngsharedvt-),
# 4 FTNPTR (nftnptr-arg-), 5 UNBOX_ARBITRARY (nunbox-arbitrary-).  Usage: lldb -b -s <cmds> (see trampcount.sh).
import lldb, atexit, os
counts = {}
def hit(frame, bp_loc, extra_args, internal_dict):
    t = frame.FindRegister("x0").GetValueAsUnsigned()
    counts[t] = counts.get(t, 0) + 1
    return False
def dump(debugger, command, result, internal_dict):
    out = os.environ.get("TRAMPCOUNT_OUT", "/dev/stdout")
    with open(out, "w") as f:
        for t in sorted(counts): f.write(f"type {t}\t{counts[t]}\n")
    print("trampcount:", dict(sorted(counts.items())))
def __lldb_init_module(debugger, internal_dict):
    debugger.HandleCommand("command script add -f trampcount.dump trampdump")
