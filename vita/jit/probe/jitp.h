/* Shepherd JIT probe (kit #7 JIT stage 1; vita/jit/probe/README.md, vita/notes/kit7-jit.md): the platform interface.
 * One set of measurement routines (probe_core.c) is written against this interface; plat_vita.c binds it to the PS Vita's own
 * user-mode code-memory API (HENkaku's ForVM: sceKernelAllocMemBlockForVM / Open / Close / SyncVMDomain; SceKernel threads pinned
 * with sceKernelChangeThreadCpuAffinityMask), plat_mac.c to the Mac (MAP_JIT pages, pthread_jit_write_protect_np,
 * sys_icache_invalidate) so the harness -- threads, handshakes, timing, the crash-safe log and the resume-after-restart state --
 * is exercised on the Mac before the owner confirms the first device run. */
#ifndef JITP_H
#define JITP_H
#include <stdint.h>
#include <stddef.h>

#define JITP_VERSION "shepherd-jitprobe-2"

typedef struct { int id; uint8_t *base; uint32_t size; } jitp_block;
typedef struct { void *h; } jitp_thread;

enum { ISA_A32 = 1, ISA_T32 = 2, ISA_A64 = 3 };

const char *plat_name (void);
int plat_isas (int *out, int cap);          /* the ISAs the probe generates code for here (Vita: A32 + T32; Mac: A64) */
int plat_domain_process_wide (void);        /* 1 = Open/Close is one state for the whole process (the Vita); 0 = per thread (Mac W^X) */
void plat_init (void);
void plat_exit (void);
uint64_t plat_us (void);
void plat_sleep_us (uint32_t us);
void plat_log_line (const char *line);
/* the probe's small files: "log" (the result log, appended across launches), "state" (the test last entered), "faults" (tests a
 * previous launch stopped inside, skipped from then on), "count" (the launch counter) */
int plat_read_file (const char *which, char *buf, int cap);
int plat_put_file (const char *which, const char *text, int append);
void plat_describe (char *buf, int cap);
int plat_confirm (void);
void plat_wait_exit (void);
int plat_free_user_kib (void);
int plat_alloc_user (uint32_t bytes, int *id);
void plat_free_user (int id);
int plat_vm_alloc (const char *name, uint32_t bytes, jitp_block *out);
int plat_vm_free (jitp_block *b);
int plat_vm_open (void);
int plat_vm_close (void);
int plat_vm_sync (jitp_block *b, void *addr, uint32_t len);
int plat_thread_start (jitp_thread *t, void *(*fn) (void *), void *arg, int core);
int plat_thread_join (jitp_thread *t, uint32_t timeout_ms);   /* 0 = joined; < 0 = still running after the timeout (or an error) */
int plat_pin_self (int core);
int plat_current_core_mask (void);
int plat_current_cpu (void);                /* the core this thread is running on now (Vita sceKernelGetCpuId); -1 = unknown */
int plat_core3_probe (void);

int emit_ret_imm (int isa, uint8_t *p, uint32_t v);
int emit_ret_arg_plus (int isa, uint8_t *p, uint32_t k);
int emit_countdown (int isa, uint8_t *p);
int emit_call_helper (int isa, uint8_t *p, void *helper, uint32_t arg);
int emit_bl_reach (int isa, uint8_t *p, void *helper, int64_t *dist);
int emit_bl_helper (int isa, uint8_t *p, void *helper, uint32_t arg, int64_t *dist);
void *emit_entry (int isa, void *p);
uint32_t emit_patch_word (int isa, uint32_t v, int *offset);
const char *isa_name (int isa);

int jitp_helper (int x);
void *jitp_helper_arm_ptr (void);            /* an ARM-mode twin of jitp_helper on ARM (the interworking call forms); NULL elsewhere */
int jitp_native_countdown (int isa, int n);   /* the eboot-compiled twin of emit_countdown (same two loop instructions) */

int jitp_main (int auto_confirm);
#endif
