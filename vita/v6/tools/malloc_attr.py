#!/usr/bin/env python3
"""Attribute every live malloc block of a held vita-sim process (malloc_history <pid> -allBySize, MallocStackLogging=1)
to a Mono / engine subsystem by its call stack. Mac-only measurement (vita/v6/sim.sh msl); nothing of it reaches a Vita build.

Input lines look like
  <n> calls for <bytes> bytes: thread_<id> |start | ... | frameA | frameB | malloc
(root first, leaf last). Each record is charged to the first rule (checked leaf -> root over the frames) that matches a frame.
Output: total, then MiB / blocks per category and the top frames inside the biggest ones."""
import re, sys
from collections import defaultdict

# (category, regex on a frame name). Checked from the leaf upwards: the first frame that matches any rule decides.
RULES = [
    ("interp: transformed code + IR (mono_interp_transform / interp_*)", r"^(interp_|mono_interp_transform|generate_code|generate|mono_interp_|tiering_|interp_create_|interp_alloc|mono_jiterp)"),
    ("JIT / AOT runtime (mini, wrappers, trampolines, jit_info)", r"^(mono_jit_|mini_|mono_compile_method|mono_codegen|mono_create_jit_trampoline|mono_arch_|mono_aot_|mono_marshal_get_|mono_mb_|mono_jit_info|jit_info_)"),
    ("sgen: GC internal (card table, gray queues, pinning, LOS index)", r"^(sgen_|mono_gc_|alloc_complex_descriptor|major_|ms_)"),
    ("metadata: generic instances / inflated types + signatures", r"^(mono_class_inflate|inflate_generic|mono_metadata_inflate|mono_metadata_get_generic_inst|mono_metadata_lookup_generic_class|mono_generic_class_get_class|mono_class_create_generic_inst|mono_metadata_get_canonical_generic_inst|mono_inflate_generic_signature|mono_metadata_type_dup|mono_class_get_generic_class|mono_method_get_signature_checked|mono_metadata_parse_method_signature|mono_class_inflate_generic_method|mono_class_get_inflated_method)"),
    ("metadata: classes, vtables, fields, methods, IMT (class-init / mono_class_*)", r"^(mono_class_|mono_method_|mono_field_|mono_type_|mono_get_method|mono_runtime_class_init|mono_vtable|class_|mono_metadata_|mono_signature_|mono_custom_attrs|mono_reflection_|mono_object_get_virtual_method|mono_param_|ves_icall_|mono_lookup_internal_call|imt_|build_imt|mono_marshal_)"),
    ("images: assembly/image structures, name caches, hash tables", r"^(mono_image_|do_mono_image|mono_assembly_|mono_alc_|mono_domain_|mono_loader_|mono_image_init_name_cache|mono_metadata_load|load_tables|mono_public_tokens|mono_cli_|mono_aot_get_|mono_dl)"),
    ("threads / TLS / handles / stacks", r"^(mono_thread|mono_threads_|thread_|start_wrapper|mono_handle|mono_gchandle|mono_os_|pthread_)"),
    ("BCL natives (System.Native, crypto, globalization shims)", r"^(SystemNative_|AppleCrypto|CryptoNative|GlobalizationNative|CompressionNative)"),
    ("dyld / system libraries", r"^(dyld|_dyld|objc_|_objc|CF|__CF|NS|_NS|_xpc|xpc_|dispatch_|_dispatch|libSystem|os_|_os_|__cxa|std::)"),
]
COMPILED = [(c, re.compile(r)) for c, r in RULES]
ALLOC = re.compile(r"^(malloc|calloc|realloc|_malloc_zone_|malloc_zone_|_malloc_type|malloc_type_|g_malloc|g_malloc0|g_calloc|g_realloc|monoeg_malloc|monoeg_malloc0|monoeg_g_calloc|monoeg_realloc|monoeg_try_|mono_mempool_|mono_mem_manager_|mono_image_alloc|mono_image_set_alloc|mono_mem_manager|g_hash_table_resize|monoeg_g_hash_table|g_ptr_array|monoeg_g_ptr|monoeg_g_array|g_array|g_string|monoeg_g_string|g_strdup|monoeg_strdup|monoeg_g_strdup|g_slist|monoeg_g_slist|g_list|monoeg_g_list|mono_conc_hashtable|mono_internal_hash|mono_property_hash|mono_g_hash|lock_free_|mono_lock_free|mono_bitset|dn_|_dn_)")
REC = re.compile(r"^\s*(\d+) calls? for (\d+) bytes: (.*)$")

FRAME = re.compile(r"^\s*0x[0-9a-fA-F]+ \(([^)]*)\) (.*?)\s*$")
VM_LEAF = re.compile(r"^(mmap|__mmap|mach_vm_allocate|vm_allocate|mach_vm_map|_kernelrpc_|__syscall|_pthread_create|pthread_create|mvm_|vm_map)")
def frame_name(f):
    m = FRAME.match(f)
    sym = m.group(2) if m else f.strip()
    sym = re.sub(r"\(.*$", "", sym).strip()          # C++ argument lists
    return sym

def main(path):
    cats = defaultdict(lambda: [0, 0]); top = defaultdict(lambda: defaultdict(lambda: [0, 0])); total = [0, 0]; vm = [0, 0]
    with open(path, errors="replace") as fh:
        for line in fh:
            m = REC.match(line)
            if not m: continue
            n, b, stack = int(m.group(1)), int(m.group(2)), m.group(3)
            frames = [frame_name(x) for x in stack.split(" | ")]
            frames = [f for f in frames if f]
            if not frames or VM_LEAF.match(frames[-1].lstrip("_")):
                vm[0] += b; vm[1] += n; continue      # a VM region (mmap / thread stack), not a malloc block
            cat = None; who = "?"
            # the first non-allocator frame from the leaf names the caller
            for f in reversed(frames):
                if not ALLOC.match(f.lstrip("_")):
                    who = f; break
            for f in reversed(frames):
                g = f.lstrip("_")
                for c, r in COMPILED:
                    if r.match(g): cat = c; break
                if cat: break
            if cat is None:
                cat = "managed / engine-driven (no runtime frame matched)" if frames else "?"
            cats[cat][0] += b; cats[cat][1] += n; top[cat][who][0] += b; top[cat][who][1] += n
            total[0] += b; total[1] += n
    print(f"live malloc blocks: {total[0]/1048576:.2f} MiB in {total[1]} blocks ({path}); VM regions skipped: {vm[0]/1048576:.2f} MiB in {vm[1]}")
    print("by subsystem:")
    for c, (b, n) in sorted(cats.items(), key=lambda kv: -kv[1][0]):
        print(f"  {b/1048576:8.2f} MiB {n:9d} blocks  {c}")
    print()
    for c, (b, n) in sorted(cats.items(), key=lambda kv: -kv[1][0])[:8]:
        print(f"top callers in: {c}")
        for w, (bb, nn) in sorted(top[c].items(), key=lambda kv: -kv[1][0])[:12]:
            print(f"    {bb/1048576:8.2f} MiB {nn:8d}  {w}")
    return 0

if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
