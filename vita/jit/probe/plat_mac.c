/* Shepherd JIT probe, Mac backend (vita/jit/probe/README.md): binds jitp.h to macOS so the probe harness runs natively before any
 * device step -- MAP_JIT pages with pthread_jit_write_protect_np as the write/execute switch and sys_icache_invalidate as the cache
 * sync, pthreads for the workers (affinity is advisory only on macOS: plat_pin_self reports "advisory"). The owner's console ISA is
 * A32; the Mac runs A64, so a Mac pass proves the harness logic and the handshakes, never the Vita's numbers (as with vlx: a pass is
 * evidence, not proof). Its W^X is per thread, so the Vita-only questions (code run while open, a write by a thread that never
 * opened) are reported n/a or as handshake checks. The A32 / T32 encodings are executed under qemu instead (qemu/run.sh).
 * Auto-confirmed. */
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <time.h>
#include <pthread.h>
#include <sys/mman.h>
#include <libkern/OSCacheControl.h>
#include "jitp.h"

static FILE *g_log; static char g_dir[1024];

const char *plat_name (void) { return "mac"; }
int plat_isas (int *out, int cap) { if (cap < 1) return 0; out[0] = ISA_A64; return 1; }
int plat_domain_process_wide (void) { return 0; }     /* pthread_jit_write_protect_np is per thread */

void plat_init (void)
{
	const char *d = getenv ("JITP_OUT"); if (!d || !*d) d = ".";
	snprintf (g_dir, sizeof g_dir, "%s", d);
}
void plat_exit (void) { if (g_log) fclose (g_log); }
uint64_t plat_us (void) { struct timespec t; clock_gettime (CLOCK_MONOTONIC, &t); return (uint64_t) t.tv_sec * 1000000ull + t.tv_nsec / 1000; }
void plat_sleep_us (uint32_t us) { struct timespec t = { us / 1000000, (long) (us % 1000000) * 1000 }; nanosleep (&t, 0); }

static void path_of (const char *which, char *out, size_t cap) { snprintf (out, cap, "%s/jitprobe.%s", g_dir, !strcmp (which, "log") ? "txt" : which); }
void plat_log_line (const char *line)
{
	char p[1100]; path_of ("log", p, sizeof p);
	FILE *f = fopen (p, "a"); if (f) { fprintf (f, "%s\n", line); fclose (f); }   /* appended across launches, never rotated */
	printf ("%s\n", line); fflush (stdout);
}
int plat_read_file (const char *which, char *buf, int cap)
{
	char p[1100]; path_of (which, p, sizeof p);
	FILE *f = fopen (p, "r"); if (!f) return -1;
	int n = (int) fread (buf, 1, (size_t) cap - 1, f); fclose (f); if (n >= 0) buf[n] = 0; return n;
}
int plat_put_file (const char *which, const char *text, int append)
{
	char p[1100]; path_of (which, p, sizeof p);
	FILE *f = fopen (p, append ? "a" : "w"); if (!f) return -1;
	fprintf (f, "%s\n", text); fclose (f); return 0;
}
void plat_describe (char *buf, int cap) { snprintf (buf, cap, "host: macOS arm64 (A64), affinity advisory; MAP_JIT + pthread_jit_write_protect_np + sys_icache_invalidate"); }
int plat_confirm (void) { return 1; }
void plat_wait_exit (void) {}
int plat_free_user_kib (void) { return -1; }
int plat_alloc_user (uint32_t bytes, int *id) { void *p = malloc (bytes); *id = 0; return p ? 0 : -1; }
void plat_free_user (int id) { (void) id; }

int plat_vm_alloc (const char *name, uint32_t bytes, jitp_block *out)
{
	(void) name;
	void *p = mmap (0, bytes, PROT_READ | PROT_WRITE | PROT_EXEC, MAP_PRIVATE | MAP_ANON | MAP_JIT, -1, 0);
	if (p == MAP_FAILED) return -1;
	out->id = 1; out->base = (uint8_t *) p; out->size = bytes; return 0;
}
int plat_vm_free (jitp_block *b) { if (b->base) munmap (b->base, b->size); b->base = 0; return 0; }
int plat_vm_open (void) { pthread_jit_write_protect_np (0); return 0; }     /* writable */
int plat_vm_close (void) { pthread_jit_write_protect_np (1); return 0; }    /* executable */
int plat_vm_sync (jitp_block *b, void *addr, uint32_t len) { (void) b; sys_icache_invalidate (addr, len); return 0; }

/* threads: a done flag so plat_thread_join can time out like the Vita's sceKernelWaitThreadEnd (a timed-out thread is detached and
 * left running; the probe then stops) */
typedef struct { void *(*fn) (void *); void *arg; pthread_t th; volatile int done; } tramp;
static void *tramp_fn (void *a) { tramp *t = (tramp *) a; t->fn (t->arg); __sync_synchronize (); t->done = 1; return 0; }
int plat_thread_start (jitp_thread *t, void *(*fn) (void *), void *arg, int core)
{
	(void) core; tramp *tr = calloc (1, sizeof *tr); if (!tr) return -1; tr->fn = fn; tr->arg = arg;
	if (pthread_create (&tr->th, 0, tramp_fn, tr)) { free (tr); return -1; }
	t->h = tr; return 0;
}
int plat_thread_join (jitp_thread *t, uint32_t timeout_ms)
{
	tramp *tr = t->h; uint64_t t0 = plat_us ();
	while (!tr->done) { if (timeout_ms && plat_us () - t0 > (uint64_t) timeout_ms * 1000u) { pthread_detach (tr->th); return -1; } plat_sleep_us (200); }
	pthread_join (tr->th, 0); free (tr); return 0;
}
int plat_pin_self (int core) { (void) core; return 0; }     /* advisory on macOS */
int plat_current_core_mask (void) { return -1; }
int plat_current_cpu (void) { return -1; }   /* macOS does not say which core a thread is on */
int plat_core3_probe (void) { return 0; }
