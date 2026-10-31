/*
 * devfail1 "vlx": a Vita-on-Linux kernel shim. It implements the ~80 SceKernel/SceIo/SceRtc/... functions that the
 * PS Vita hello/menu ELFs import (normally NID stubs resolved by the Vita loader) on top of ARM32 Linux syscalls, so
 * the SAME objects that go into the eboot (newlib, pthread-embedded, the HOST_VITA Mono runtime, the static AOT images,
 * host.c, the System.Native shim) can be linked into a static ARM32 Linux executable and run under OrbStack's A32
 * user-mode emulation on this Mac: silent, offline, scriptable, with a register dump on any fault.
 *
 * Not a Vita: CPU timing, memory map, caches and scheduling differ; sceIo is a path-mapped host directory; no GPU,
 * audio, network. It does NOT create executable memory (mmap is always PROT_READ|PROT_WRITE) and contains no code
 * generation of any kind.
 *
 * Paths: "<dev>:<rest>" -> $VLX_ROOT/<dev>/<rest> (default VLX_ROOT=/vlx), e.g. app0:managed/x -> /vlx/app0/managed/x;
 * newlib's "tty0:" stdio goes to stderr.
 * Env: VLX_ROOT; VLX_TRACE=1 (log every kernel call that fails); VLX_USER_KIB / VLX_CDRAM_KIB / VLX_PHYCONT_KIB (budgets
 * that sceKernelGetFreeMemorySize reports; default = the device at hello start); debugging under QEMU_GDB=<port>:
 * VLX_BREAK=1 (bkpt at boot, so lldb can attach first), VLX_TRAP_AFTER_S=N (a watchdog thread hits bkpt every N s, which
 * stops every thread: qemu-user cannot halt threads blocked in futex, so lldb's own interrupt times out),
 * VLX_POLL_MS=N (slice blocking waits into N ms). See vita/vlx/README.md.
 */
#include <stdint.h>
#include <stddef.h>
#include <stdarg.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/rng.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/error.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/io/dirent.h>
#include <psp2/rtc.h>
#include <psp2/ctrl.h>
#include <psp2/display.h>
#include <psp2/power.h>
#include <psp2/net/net.h>

/* ------------------------------------------------------------------ raw Linux syscalls (ARM EABI) */
static inline long sys6 (long n, long a, long b, long c, long d, long e, long f)
{
	register long r0 asm ("r0") = a; register long r1 asm ("r1") = b; register long r2 asm ("r2") = c;
	register long r3 asm ("r3") = d; register long r4 asm ("r4") = e; register long r5 asm ("r5") = f;
	register long r7 asm ("r7") = n;
	asm volatile ("svc #0" : "+r" (r0) : "r" (r1), "r" (r2), "r" (r3), "r" (r4), "r" (r5), "r" (r7) : "memory");
	return r0;
}
#define sys0(n) sys6 (n, 0, 0, 0, 0, 0, 0)
#define sys1(n, a) sys6 (n, (long) (a), 0, 0, 0, 0, 0)
#define sys2(n, a, b) sys6 (n, (long) (a), (long) (b), 0, 0, 0, 0)
#define sys3(n, a, b, c) sys6 (n, (long) (a), (long) (b), (long) (c), 0, 0, 0)
#define sys4(n, a, b, c, d) sys6 (n, (long) (a), (long) (b), (long) (c), (long) (d), 0, 0)
#define sys5(n, a, b, c, d, e) sys6 (n, (long) (a), (long) (b), (long) (c), (long) (d), (long) (e), 0)

enum {
	NR_exit = 1, NR_read = 3, NR_write = 4, NR_open = 5, NR_close = 6, NR_unlink = 10, NR_lseek = 19, NR_rename = 38,
	NR_mkdir = 39, NR_rmdir = 40, NR_munmap = 91, NR_fsync = 118, NR_mprotect = 125, NR_llseek = 140, NR_nanosleep = 162, NR_timer_create = 257, NR_timer_settime = 258, NR_timer_delete = 261,
	NR_rt_sigaction = 174, NR_pread64 = 180, NR_pwrite64 = 181, NR_sigaltstack = 186, NR_mmap2 = 192, NR_ftruncate64 = 194,
	NR_getdents64 = 217, NR_gettid = 224, NR_futex = 240, NR_exit_group = 248, NR_clock_gettime = 263, NR_getrandom = 384,
	NR_statx = 397, NR_set_tls = 0xf0005,
};
#define L_O_WRONLY 01
#define L_O_RDWR 02
#define L_O_CREAT 0100
#define L_O_EXCL 0200
#define L_O_TRUNC 01000
#define L_O_APPEND 02000
#define L_O_DIRECTORY 040000
#define L_O_LARGEFILE 0400000
#define L_AT_FDCWD (-100)
#define L_AT_EMPTY_PATH 0x1000
#define L_AT_SYMLINK_NOFOLLOW 0x100
#define FUTEX_WAIT_P 128
#define FUTEX_WAKE_P 129

struct lts { long sec, nsec; };
struct lstatx_ts { int64_t sec; uint32_t nsec; int32_t pad; };
struct lstatx {
	uint32_t mask, blksize; uint64_t attributes; uint32_t nlink, uid, gid; uint16_t mode, pad0; uint64_t ino, size, blocks,
	attributes_mask; struct lstatx_ts atime, btime, ctime, mtime; uint32_t rdev_major, rdev_minor, dev_major, dev_minor;
	uint64_t spare [14];
};

extern int vlx_clone (unsigned flags, void *child_sp, volatile int *ptid, void *tls, volatile int *ctid, void (*fn) (void *), void *arg);
extern void vlx_sigreturn (void);
extern void _init (void), _fini (void);

/* ------------------------------------------------------------------ tiny libc-free helpers */
static size_t vstrlen (const char *s) { size_t n = 0; while (s [n]) n++; return n; }
static void vmemcpy (void *d, const void *s, size_t n) { char *a = d; const char *b = s; while (n--) *a++ = *b++; }
static void vmemset (void *d, int c, size_t n) { char *a = d; while (n--) *a++ = (char) c; }
static int vstrncmp (const char *a, const char *b, size_t n) { for (; n; n--, a++, b++) { if (*a != *b) return (unsigned char) *a - (unsigned char) *b; if (!*a) return 0; } return 0; }
static void vstrlcpy (char *d, const char *s, size_t cap) { size_t i = 0; if (!cap) return; for (; s && s [i] && i + 1 < cap; i++) d [i] = s [i]; d [i] = 0; }

typedef struct { char *p; size_t cap, len; } vbuf;
static void vb_putc (vbuf *b, char c) { if (b->len + 1 < b->cap) b->p [b->len] = c; b->len++; }
static void vb_puts (vbuf *b, const char *s, int prec) { if (!s) s = "(null)"; for (int i = 0; s [i] && (prec < 0 || i < prec); i++) vb_putc (b, s [i]); }
static void vb_num (vbuf *b, unsigned long long v, int base, int upper, int neg, int width, int zero, int left)
{
	char t [24]; int n = 0; const char *dg = upper ? "0123456789ABCDEF" : "0123456789abcdef";
	do { t [n++] = dg [v % (unsigned) base]; v /= (unsigned) base; } while (v);
	int len = n + neg, pad = width > len ? width - len : 0;
	if (!left && !zero) while (pad-- > 0) vb_putc (b, ' ');
	if (neg) vb_putc (b, '-');
	if (!left && zero) while (pad-- > 0) vb_putc (b, '0');
	while (n) vb_putc (b, t [--n]);
	if (left) while (pad-- > 0) vb_putc (b, ' ');
}
static int vvsnprintf (char *out, size_t cap, const char *f, va_list ap)
{
	vbuf b = { out, cap, 0 };
	for (; *f; f++) {
		if (*f != '%') { vb_putc (&b, *f); continue; }
		f++;
		int left = 0, zero = 0, width = 0, prec = -1, lng = 0;
		for (;; f++) { if (*f == '-') left = 1; else if (*f == '0') zero = 1; else if (*f == '+' || *f == ' ' || *f == '#') ; else break; }
		if (*f == '*') { width = va_arg (ap, int); f++; } else while (*f >= '0' && *f <= '9') width = width * 10 + (*f++ - '0');
		if (*f == '.') { f++; prec = 0; if (*f == '*') { prec = va_arg (ap, int); f++; } else while (*f >= '0' && *f <= '9') prec = prec * 10 + (*f++ - '0'); }
		for (;; f++) { if (*f == 'l') lng++; else if (*f == 'z' || *f == 't' || *f == 'j') lng = (*f == 'j') ? 2 : lng; else if (*f == 'h') ; else break; }
		switch (*f) {
		case 'd': case 'i': { long long v = lng >= 2 ? va_arg (ap, long long) : va_arg (ap, long); vb_num (&b, v < 0 ? -(unsigned long long) v : (unsigned long long) v, 10, 0, v < 0, width, zero, left); break; }
		case 'u': case 'x': case 'X': case 'o': { unsigned long long v = lng >= 2 ? va_arg (ap, unsigned long long) : va_arg (ap, unsigned long);
			vb_num (&b, v, *f == 'u' ? 10 : *f == 'o' ? 8 : 16, *f == 'X', 0, width, zero, left); break; }
		case 'p': vb_puts (&b, "0x", -1); vb_num (&b, (uintptr_t) va_arg (ap, void *), 16, 0, 0, 8, 1, 0); break;
		case 'c': vb_putc (&b, (char) va_arg (ap, int)); break;
		case 's': vb_puts (&b, va_arg (ap, const char *), prec); break;
		case 'f': case 'g': case 'e': { double d = va_arg (ap, double); long long ip = (long long) d; vb_num (&b, ip < 0 ? -ip : ip, 10, 0, d < 0, 0, 0, 0);
			vb_putc (&b, '.'); double fr = d - (double) ip; if (fr < 0) fr = -fr; for (int i = 0; i < 6; i++) { fr *= 10; int dd = (int) fr; vb_putc (&b, (char) ('0' + dd)); fr -= dd; } break; }
		case '%': vb_putc (&b, '%'); break;
		case 0: f--; break;
		default: vb_putc (&b, '%'); vb_putc (&b, *f); break;
		}
	}
	if (cap) out [b.len < cap ? b.len : cap - 1] = 0;
	return (int) b.len;
}
static void vwrite_all (int fd, const char *s, size_t n) { while (n) { long r = sys3 (NR_write, fd, s, n); if (r <= 0) return; s += r; n -= (size_t) r; } }
static void vlog (const char *fmt, ...)
{
	char buf [1024]; va_list ap; va_start (ap, fmt); int n = vvsnprintf (buf, sizeof (buf), fmt, ap); va_end (ap);
	if (n > (int) sizeof (buf) - 1) n = sizeof (buf) - 1;
	vwrite_all (2, buf, (size_t) n);
}

/* ------------------------------------------------------------------ time */
static uint64_t mono_us (void) { struct lts t; sys2 (NR_clock_gettime, 1, &t); return (uint64_t) t.sec * 1000000u + (uint64_t) t.nsec / 1000u; }
static uint64_t real_us (void) { struct lts t; sys2 (NR_clock_gettime, 0, &t); return (uint64_t) (uint32_t) t.sec * 1000000u + (uint64_t) t.nsec / 1000u; }
static uint64_t g_boot_us;
/* kit #8 boot track (declared here: the exit path and the sysmodule stand-ins use them before their own sections) */
static unsigned g_disp_sets, g_disp_nulls; static uint32_t g_disp_hash = 2166136261u; static uint32_t g_disp_last;
static void disp_line (const char *why);
static int g_avc_mod; static unsigned g_avc_fail_at;
/* kit #9 device track: the decoder's output delay (VLX_AVC_DELAY; kit #10 boot track: default 4 = the device's, MEASURED by kit #9's run
 * on both logos -- kit #9's default was 1), the library's PHYCONT (VLX_AVC_LIB_KIB, default 6144), a suspension (VLX_SUSPEND=<at s>:<for s>,
 * VLX_SUSPEND_CLK=0 keeps the clocks) -- the sections below */
static unsigned g_avc_delay = 4, g_avc_lib_kib = 6144;
static unsigned g_susp_at_s, g_susp_dur_s; static int g_susp_clk; static volatile int g_susp_fired; static uint64_t g_rtc_off_us;
static void vlx_susp_poll (void);
static int g_clk [4] = { 333, 222, 0, 111 };   /* the clocks (scePower stand-ins below); launch values = the PCH-2000's report at launch */
#define EPOCH_TICK 62135596800000000ULL   /* SceRtcTick (us since 0001-01-01) of 1970-01-01 */

static int64_t days_from_civil (int y, unsigned m, unsigned d)
{
	y -= m <= 2; const int64_t era = (y >= 0 ? y : y - 399) / 400; const unsigned yoe = (unsigned) (y - era * 400);
	const unsigned doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1; const unsigned doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
	return era * 146097 + (int64_t) doe - 719468;
}
static void civil_from_days (int64_t z, int *y, unsigned *m, unsigned *d)
{
	z += 719468; const int64_t era = (z >= 0 ? z : z - 146096) / 146097; const unsigned doe = (unsigned) (z - era * 146097);
	const unsigned yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365; const int64_t yy = (int64_t) yoe + era * 400;
	const unsigned doy = doe - (365 * yoe + yoe / 4 - yoe / 100); const unsigned mp = (5 * doy + 2) / 153;
	*d = doy - (153 * mp + 2) / 5 + 1; *m = mp + (mp < 10 ? 3 : -9); *y = (int) (yy + (*m <= 2));
}
static void tick_to_dt (uint64_t tick, SceDateTime *t)
{
	uint64_t us = tick - EPOCH_TICK; /* unsigned wrap is fine for dates after 1970 */
	int64_t secs = (int64_t) (us / 1000000u); int64_t days = secs / 86400; int64_t rem = secs % 86400;
	int y; unsigned m, d; civil_from_days (days, &y, &m, &d);
	t->year = (unsigned short) y; t->month = (unsigned short) m; t->day = (unsigned short) d;
	t->hour = (unsigned short) (rem / 3600); t->minute = (unsigned short) (rem % 3600 / 60); t->second = (unsigned short) (rem % 60);
	t->microsecond = (unsigned int) (us % 1000000u);
}
static uint64_t dt_to_tick (const SceDateTime *t)
{
	int64_t days = days_from_civil (t->year, t->month, t->day);
	int64_t secs = days * 86400 + t->hour * 3600 + t->minute * 60 + t->second;
	return (uint64_t) (secs * 1000000 + t->microsecond) + EPOCH_TICK;
}

/* ------------------------------------------------------------------ kernel lock + waiting */
static volatile int g_klock;
static unsigned g_poll_us;   /* VLX_POLL_MS: slice blocking waits so a debugger (qemu gdbstub) can halt every thread */
static void futex_wait (volatile int *w, int v, const struct lts *rel) { sys4 (NR_futex, w, FUTEX_WAIT_P, v, rel); }
static void futex_wake (volatile int *w, int n) { sys3 (NR_futex, w, FUTEX_WAKE_P, n); }
static void klock (void)
{
	int c = 0;
	if (__atomic_compare_exchange_n (&g_klock, &c, 1, 0, __ATOMIC_ACQUIRE, __ATOMIC_RELAXED)) return;
	if (c != 2) c = __atomic_exchange_n (&g_klock, 2, __ATOMIC_ACQUIRE);
	while (c != 0) { futex_wait (&g_klock, 2, NULL); c = __atomic_exchange_n (&g_klock, 2, __ATOMIC_ACQUIRE); }
}
static void kunlock (void)
{
	if (__atomic_fetch_sub (&g_klock, 1, __ATOMIC_RELEASE) != 1) { __atomic_store_n (&g_klock, 0, __ATOMIC_RELEASE); futex_wake (&g_klock, 1); }
}
/* called with klock held; drops it while sleeping on *seq; returns 1 if the deadline (us, 0 = none) passed */
static int kwait (volatile int *seq, uint64_t deadline)
{
	int s = *seq;
	kunlock ();
	uint64_t rem = 0;
	if (deadline) {
		uint64_t now = mono_us ();
		if (now >= deadline) { klock (); return 1; }
		rem = deadline - now;
	}
	if (g_poll_us && (!rem || rem > g_poll_us)) rem = g_poll_us;   /* spurious wake-ups are fine: callers re-check */
	if (rem) { struct lts ts = { (long) (rem / 1000000u), (long) (rem % 1000000u) * 1000 }; futex_wait (seq, s, &ts); }
	else futex_wait (seq, s, NULL);
	klock ();
	return deadline && mono_us () >= deadline;
}
static void kwake (volatile int *seq) { (*seq)++; futex_wake (seq, 0x7fffffff); }
static uint64_t deadline_of (const SceUInt *timeout) { return timeout ? mono_us () + *timeout + (*timeout == 0) : 0; }
static void timeout_left (SceUInt *timeout, uint64_t deadline)
{
	if (!timeout) return; uint64_t now = mono_us (); *timeout = now >= deadline ? 0 : (SceUInt) (deadline - now);
}

/* ------------------------------------------------------------------ objects */
enum { T_FREE, T_THREAD, T_SEMA, T_MUTEX, T_EVF, T_MEMBLOCK, T_LWMUTEX };
struct vthread;
typedef struct vobj {
	int type, idx; SceUID uid; char name [32]; volatile int seq; int deleted; unsigned attr;
	/* sema */ int count, max;
	/* mutex / lwmutex */ struct vthread *owner;
	/* evf */ unsigned pattern;
	/* memblock */ void *base; unsigned size, mtype, kind;
} vobj;
typedef struct vthread {
	vobj o;
	SceKernelThreadEntry entry; int init_prio, prio, affinity; unsigned stack_size; void *stack_lo, *map; unsigned map_size;
	volatile int ctid; volatile int state; int exit_status; SceSize arglen; void *argcopy; int linux_tid;
	unsigned start_guess; int stack_reported, stack_accounted; uint64_t created_us;   /* kit5 mem: VLX_STACK_REPORT / VLX_STACK_ACCOUNT */
	int prof_timer;   /* kit #6 fps: VLX_PROF's CPU timer of this thread + 1 (0: none) */
	unsigned tls_calls;   /* kit #9 runtime review: this thread's sceKernelGetTLSAddr calls (VLX_TLS_FOREIGN's lazy writer) */
	void *tls [0x100];
	char altstack [8192];
} vthread;
enum { TS_DORMANT = 0, TS_RUNNING = 1, TS_DEAD = 2 };
static int g_tls_foreign_slot = -1; static unsigned g_tls_foreign_after = 100000;   /* kit #9 runtime review: VLX_TLS_FOREIGN (sceKernelGetTLSAddr) */
#define VLX_TLS_FOREIGN_VALUE ((void *) 0x5EC0F00D)

#define MAX_OBJ 8192
#define MAX_THR 256
static vobj g_obj [MAX_OBJ];
static vthread g_thr [MAX_THR];
#define UID_OBJ(i) ((SceUID) (0x40010001 + ((i) << 1)))
#define UID_THR(i) ((SceUID) (0x40810001 + ((i) << 1)))
static unsigned parse_kib (const char *s);
static int g_trace, g_break; static unsigned g_trap_after_s;   /* VLX_TRAP_AFTER_S=N: a watchdog thread executes bkpt every N s (qemu gdbstub then stops all threads) */

/* UIDs are never reused quickly (as on the Vita): slots rotate and every UID carries a generation, so a call on a
 * deleted object's UID fails instead of hitting whatever object took the slot next */
static unsigned g_obj_cursor, g_obj_gen;
static vobj *obj_new (int type, const char *name)
{
	for (int n = 0; n < MAX_OBJ; n++) {
		unsigned i = 1 + (g_obj_cursor++ % (MAX_OBJ - 1));
		if (g_obj [i].type == T_FREE) {
			vobj *o = &g_obj [i]; vmemset (o, 0, sizeof (*o)); o->type = type; o->idx = (int) i;
			g_obj_gen = (g_obj_gen + 1) & 0x3ff;   /* UIDs stay in 0x40000001..0x407fffff: positive, below the thread UIDs */
			o->uid = (SceUID) (0x40000001u + ((((unsigned) g_obj_gen << 13) | i) << 1));
			vstrlcpy (o->name, name ? name : "", sizeof (o->name)); return o;
		}
	}
	vlog ("[vlx] FATAL object table full\n"); sys1 (NR_exit_group, 127); return 0;
}
static vobj *obj_get (SceUID uid, int type)
{
	unsigned i = (((unsigned) uid - 0x40000001u) >> 1) & (MAX_OBJ - 1);
	if (((unsigned) uid & 1) && i < MAX_OBJ && g_obj [i].uid == uid && g_obj [i].type == type && !g_obj [i].deleted) return &g_obj [i];
	return 0;
}
static vthread *self (void) { vthread *t; asm volatile ("mrc p15, 0, %0, c13, c0, 3" : "=r" (t)); return t; }
static vthread *thr_get (SceUID uid)
{
	if (uid == 0) return self ();
	unsigned i = ((unsigned) uid - 0x40810001u) >> 1;
	if (((unsigned) uid & 1) && i < MAX_THR && g_thr [i].o.type == T_THREAD) return &g_thr [i];
	return 0;
}
static int fail (const char *fn, int err) { if (g_trace) vlog ("[vlx] %s -> 0x%08x\n", fn, (unsigned) err); return err; }

/* ------------------------------------------------------------------ crash handler */
static uint32_t g_text_lo, g_text_hi;
/* kit5 mem (M3 stack right-sizing): VLX_STACK_REPORT=1 paints every new thread's stack with a pattern and reports, when the thread ends and
 * for every thread still alive at exit, its deepest use (the lowest word that no longer holds the pattern); plus the peak number of live
 * threads and the peak sum of their stacks. VLX_STACK_ACCOUNT=1 also takes each thread's stack (+ the 4 KiB rounding) from the user budget
 * that sceKernelGetFreeMemorySize reports, as the device does (without it vlx counts memblocks only). Both off by default (kit #4 vlx). */
static int g_stack_report, g_stack_account, g_stack_account_on;   /* (on once vlx_boot has made "main": its stack is in the calibrated T) */
static unsigned g_budget [3], g_used [3];   /* (defined with their values in "kernel lock + waiting" below) */
static unsigned g_live_threads, g_live_threads_peak, g_live_stack, g_live_stack_peak; static uint64_t g_live_stack_peak_us;
#define STACK_PAINT 0x5A5AA5A5u
static void stack_report_all (const char *why);
static unsigned stack_used (const vthread *t)
{
	const uint32_t *p = (const uint32_t *) t->stack_lo, *e = (const uint32_t *) ((const char *) t->stack_lo + t->stack_size);
	while (p < e && *p == STACK_PAINT) p++;
	return (unsigned) ((const char *) e - (const char *) p);
}
/* the deepest word holding data: neither the paint nor zero (a region a thread zeroed on purpose -- the engine's boot GC clears 256 KiB
 * under its frame -- counts as touched above but only frames that wrote real words count here) */
static unsigned stack_used_data (const vthread *t)
{
	const uint32_t *p = (const uint32_t *) t->stack_lo, *e = (const uint32_t *) ((const char *) t->stack_lo + t->stack_size);
	while (p < e && (*p == STACK_PAINT || *p == 0)) p++;
	return (unsigned) ((const char *) e - (const char *) p);
}
struct ksigaction { void (*h) (int, void *, void *); unsigned long flags; void (*restorer) (void); unsigned long mask [2]; };
static void crash (int sig, void *si, void *ucv)
{
	uint32_t *uc = ucv; uint32_t *r = uc + 8;   /* uc_flags, uc_link, uc_stack[3], trap_no, error_code, oldmask, r0.. */
	vthread *t = self ();
	vlog ("\n[vlx] *** FATAL signal %d (%s) in thread '%s' fault_addr=0x%08x si_addr=0x%08x\n", sig,
	      sig == 11 ? "SIGSEGV" : sig == 7 ? "SIGBUS" : sig == 4 ? "SIGILL" : sig == 8 ? "SIGFPE" : sig == 6 ? "SIGABRT" : "?",
	      t ? t->o.name : "?", r [17], ((uint32_t *) si) [3]);
	for (int i = 0; i < 13; i++) vlog ("  r%-2d=%08x%s", i, r [i], (i % 4 == 3) ? "\n" : "");
	vlog ("\n  sp =%08x  lr =%08x  pc =%08x  cpsr=%08x\n", r [13], r [14], r [15], r [16]);
	uint32_t *sp = (uint32_t *) r [13]; int n = 0;
	vlog ("  text-looking words on the stack (addr2line -e the .elf):");
	if (t && (uint8_t *) sp >= (uint8_t *) t->stack_lo && (uint8_t *) sp < (uint8_t *) t->stack_lo + t->stack_size)
		for (uint32_t *p = sp; (uint8_t *) p < (uint8_t *) t->stack_lo + t->stack_size && n < 48; p++)
			if (*p >= g_text_lo && *p < g_text_hi) { vlog (" %08x", *p); n++; }
	vlog ("\n");
	stack_report_all ("crash");
	sys1 (NR_exit_group, 128 + sig);
}
static void install_crash_handlers (void)
{
	struct ksigaction sa; vmemset (&sa, 0, sizeof (sa));
	sa.h = crash; sa.flags = 0x4 /* SA_SIGINFO */ | 0x04000000 /* SA_RESTORER */ | 0x08000000 /* SA_ONSTACK */; sa.restorer = vlx_sigreturn;
	int sigs [] = { 4, 6, 7, 8, 11 };
	for (unsigned i = 0; i < sizeof (sigs) / sizeof (sigs [0]); i++) sys4 (NR_rt_sigaction, sigs [i], &sa, 0, 8);
}
/* kit #6 fps: VLX_PROF=<us> -- a CPU-time sampling profile of the run (measurement only; never in an eboot). Every <us> of the process's
 * CPU time (setitimer ITIMER_PROF: the kernel signals the thread that is running) the SIGPROF handler records the interrupted thread's
 * pc + lr, its vlx thread slot and the time since boot (100 us units, the clock of sceKernelGetProcessTimeWide: the host's P lines' wall
 * times place the samples in frames). At exit the samples go to <root>/vlxprof.bin ("VLXPROF1", count, then 16-byte records).
 * Unset (the default): no timer, no handler, nothing changes. */
struct prof_rec { uint32_t t100us, pc, lr; uint16_t thr, pad; };
static char g_root [256];   /* (defined with its value under "files" below) */
#define PROF_CAP (1u << 20)
static struct prof_rec g_prof [PROF_CAP];
static volatile unsigned g_prof_n; static unsigned g_prof_us; static int g_prof_dumped;
static void prof_tick (int sig, void *si, void *ucv)
{
	(void) sig; (void) si;
	uint32_t *r = (uint32_t *) ucv + 8;   /* the saved r0..r15 (the crash handler's layout) */
	unsigned i = __atomic_fetch_add (&g_prof_n, 1, __ATOMIC_RELAXED);
	if (i >= PROF_CAP) return;
	vthread *t = self ();
	struct prof_rec *e = &g_prof [i];
	e->t100us = (uint32_t) ((mono_us () - g_boot_us) / 100u); e->pc = r [15]; e->lr = r [14];
	e->thr = (uint16_t) ((t >= g_thr && t < g_thr + MAX_THR) ? (unsigned) (t - g_thr) : 0xffffu);
}
static void prof_start (void)
{
	if (!g_prof_us) return;
	struct ksigaction sa; vmemset (&sa, 0, sizeof (sa));
	sa.h = prof_tick; sa.flags = 0x4 /* SA_SIGINFO */ | 0x04000000 /* SA_RESTORER */ | 0x08000000 /* SA_ONSTACK */ | 0x10000000 /* SA_RESTART */; sa.restorer = vlx_sigreturn;
	sys4 (NR_rt_sigaction, 27 /* SIGPROF */, &sa, 0, 8);
	vlog ("[vlx] VLX_PROF=%u: every thread samples itself every %u us of its own CPU time -> vlxprof.bin at exit\n", g_prof_us, g_prof_us);
}
/* the calling thread's CPU-time timer (CLOCK_THREAD_CPUTIME_ID, SIGEV_THREAD_ID): its SIGPROF interrupts this thread only, so the samples
 * are spread by CPU time (a process timer's signal goes to whichever thread the kernel picks, mostly the waiting ones) */
static void prof_thread (void)
{
	if (!g_prof_us) return;
	struct { int value, signo, notify, tid; int pad [12]; } ev; vmemset (&ev, 0, sizeof (ev));
	ev.signo = 27; ev.notify = 4 /* SIGEV_THREAD_ID */; ev.tid = (int) sys0 (NR_gettid);
	int timer = -1;
	vthread *t = self ();
	long rc = sys3 (NR_timer_create, 3 /* CLOCK_THREAD_CPUTIME_ID */, &ev, &timer);
	if (rc != 0) { vlog ("[vlx] VLX_PROF: no CPU timer for thread '%s' (timer_create %ld)\n", t ? t->o.name : "?", rc); return; }
	struct { long is, ins, vs, vns; } it = { (long) (g_prof_us / 1000000u), (long) (g_prof_us % 1000000u) * 1000, (long) (g_prof_us / 1000000u), (long) (g_prof_us % 1000000u) * 1000 };
	rc = sys4 (NR_timer_settime, timer, 0, &it, 0);
	if (t) t->prof_timer = rc == 0 ? timer + 1 : 0;
	if (rc != 0) vlog ("[vlx] VLX_PROF: timer_settime %ld for thread '%s'\n", rc, t ? t->o.name : "?");
}
static void prof_dump (void)
{
	if (!g_prof_us || g_prof_dumped) return;
	g_prof_dumped = 1;
	g_prof_us = 0;   /* (late ticks still record into the buffer; the dump takes the count first) */
	char path [300]; size_t rl = vstrlen (g_root); vmemcpy (path, g_root, rl); vmemcpy (path + rl, "/vlxprof.bin", 13);
	long fd = sys3 (NR_open, path, 01 | 0100 | 01000 /* O_WRONLY | O_CREAT | O_TRUNC */, 0644);
	if (fd < 0) { vlog ("[vlx] VLX_PROF: cannot write %s\n", path); return; }
	unsigned n = g_prof_n < PROF_CAP ? g_prof_n : PROF_CAP;
	vwrite_all ((int) fd, "VLXPROF1", 8); vwrite_all ((int) fd, (const char *) &n, 4);
	for (unsigned i = 0; i < MAX_THR; i++) { char nm [32]; vmemset (nm, 0, sizeof (nm)); vstrlcpy (nm, g_thr [i].o.name, sizeof (nm)); vwrite_all ((int) fd, nm, 32); }
	vwrite_all ((int) fd, (const char *) g_prof, n * sizeof (struct prof_rec));
	sys1 (NR_close, fd);
	vlog ("[vlx] VLX_PROF: %u samples (%u taken) -> %s\n", n, g_prof_n, path);
}
static void thread_altstack (vthread *t) { struct { void *sp; int flags; size_t size; } ss = { t->altstack, 0, sizeof (t->altstack) }; sys2 (NR_sigaltstack, &ss, 0); }

/* ------------------------------------------------------------------ threads */
/* kit #10 fidelity (vita/notes/kit10-fidelity.md section 1): VLX_THREAD_FPSCR=<hex> -- every thread (the main thread too) starts with that FPSCR,
 * as on the device, whose kernel starts every thread with 0x03000000 (FZ 1 DN 1: kit #9's device FP lines); unset = qemu-user's own rule (a new
 * thread inherits its creator's FPSCR; the first thread starts with 0). qemu honours FZ / DN (the host's self-test line shows it). */
static unsigned g_thread_fpscr; static int g_thread_fpscr_on;
static void thread_tramp (void *arg)
{
	vthread *t = arg;
	if (g_thread_fpscr_on)
		__asm__ volatile ("vmsr fpscr, %0" : : "r" (g_thread_fpscr) : "memory");
	thread_altstack (t);
	t->linux_tid = (int) sys0 (NR_gettid);
	prof_thread ();
	int r = t->entry (t->arglen, t->argcopy);
	sceKernelExitThread (r);
}

SceUID sceKernelCreateThread (const char *name, SceKernelThreadEntry entry, int initPriority, SceSize stackSize, SceUInt attr,
                              int cpuAffinityMask, const SceKernelThreadOptParam *option)
{
	(void) option;
	if (!entry) return fail ("sceKernelCreateThread", SCE_KERNEL_ERROR_ILLEGAL_ADDR);
	unsigned sz = (stackSize + 0xfff) & ~0xfffu; if (sz < 0x1000) sz = 0x1000;
	unsigned map_size = sz + 0x1000;   /* + a guard page below the stack (the Vita has none; this one turns overflow into a dump) */
	void *m = (void *) sys6 (NR_mmap2, 0, map_size, 3, 0x22, -1, 0);
	if ((unsigned long) m > 0xfffff000ul) return fail ("sceKernelCreateThread(mmap)", SCE_KERNEL_ERROR_NO_MEMORY);
	sys3 (NR_mprotect, m, 0x1000, 0);
	if (g_stack_report) for (uint32_t *p = (uint32_t *) ((char *) m + 0x1000), *e = (uint32_t *) ((char *) m + map_size); p < e; p++) *p = STACK_PAINT;
	klock ();
	int acct = g_stack_account && g_stack_account_on;
	if (acct && (g_used [0] + sz > g_budget [0] || g_used [0] + sz < g_used [0])) {
		kunlock (); sys2 (NR_munmap, m, map_size);
		vlog ("[vlx] sceKernelCreateThread('%s', %u KiB): NO MEMORY for the stack (user budget %u KiB, %u in use) -- as on the device\n", name, sz / 1024, g_budget [0] / 1024, g_used [0] / 1024);
		return fail ("sceKernelCreateThread(stack)", SCE_KERNEL_ERROR_NO_MEMORY);
	}
	vthread *t = 0; int i;
	for (i = 1; i < MAX_THR; i++) if (g_thr [i].o.type == T_FREE) { t = &g_thr [i]; break; }
	if (!t) { kunlock (); return fail ("sceKernelCreateThread(table)", SCE_KERNEL_ERROR_NO_MEMORY); }
	if (acct) g_used [0] += sz;
	g_live_threads++; g_live_stack += sz;
	if (g_live_threads > g_live_threads_peak) g_live_threads_peak = g_live_threads;
	if (g_live_stack > g_live_stack_peak) { g_live_stack_peak = g_live_stack; g_live_stack_peak_us = mono_us () - g_boot_us; }
	vmemset (t, 0, sizeof (*t));
	if (g_tls_foreign_slot >= 0 && !g_tls_foreign_after) t->tls [g_tls_foreign_slot] = VLX_TLS_FOREIGN_VALUE;   /* kit #9 runtime review */
	t->created_us = mono_us () - g_boot_us; t->stack_accounted = acct;
	t->o.type = T_THREAD; t->o.idx = i; t->o.uid = UID_THR (i); vstrlcpy (t->o.name, name, sizeof (t->o.name)); t->o.attr = attr;
	t->entry = entry; t->init_prio = t->prio = initPriority; t->affinity = cpuAffinityMask; t->stack_size = sz;
	t->map = m; t->map_size = map_size; t->stack_lo = (char *) m + 0x1000; t->state = TS_DORMANT;
	kunlock ();
	return t->o.uid;
}

int sceKernelStartThread (SceUID thid, SceSize arglen, void *argp)
{
	vthread *t = thr_get (thid);
	if (!t || thid == 0) return fail ("sceKernelStartThread", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID);
	if (t->state != TS_DORMANT) return fail ("sceKernelStartThread", SCE_KERNEL_ERROR_NOT_DORMANT);
	char *top = (char *) t->stack_lo + t->stack_size;
	char *cp = (char *) (((uintptr_t) top - arglen) & ~(uintptr_t) 15);
	if (arglen && argp) vmemcpy (cp, argp, arglen);
	t->arglen = arglen; t->argcopy = (arglen && argp) ? cp : argp;
	if (g_stack_report && t->argcopy) {   /* the start routine: the first text address in the argument block (pthread-embedded passes its thread record) */
		const uint32_t *a = (const uint32_t *) t->argcopy; unsigned n = arglen ? (arglen / 4 < 16 ? arglen / 4 : 16) : 16;
		if (!arglen) { const uint32_t *rec = (const uint32_t *) (uintptr_t) a [0]; if ((uintptr_t) rec > 0x10000) a = rec; }
		for (unsigned k = 0; k < n && !t->start_guess; k++) if (a [k] >= g_text_lo && a [k] < g_text_hi) t->start_guess = a [k];
	}
	char *sp = (char *) ((uintptr_t) (cp - 16) & ~(uintptr_t) 15);
	t->state = TS_RUNNING;
	int r = vlx_clone (0x3D0F00, sp, &t->ctid, t, &t->ctid, thread_tramp, t);
	if (r < 0) { t->state = TS_DORMANT; return fail ("sceKernelStartThread(clone)", SCE_KERNEL_ERROR_ERROR); }
	return 0;
}

static void stack_line (vthread *t, const char *when)
{
	if (!g_stack_report || t->stack_reported || !t->stack_lo) return;
	t->stack_reported = 1;
	unsigned u = stack_used (t), ud = stack_used_data (t);
	vlog ("[vlx] stack %s '%s' size %u KiB, deepest use %u KiB (%u B; deepest data word %u B), start~%08x, created t=%u ms, %s t=%u ms\n", when, t->o.name,
	      t->stack_size / 1024, (u + 1023) / 1024, u, ud, t->start_guess, (unsigned) (t->created_us / 1000), when, (unsigned) ((mono_us () - g_boot_us) / 1000));
}
static void stack_report_all (const char *why)
{
	if (!g_stack_report) return;
	for (int i = 1; i < MAX_THR; i++) if (g_thr [i].o.type == T_THREAD && g_thr [i].state != TS_DORMANT) stack_line (&g_thr [i], "alive-at-exit");
	vlog ("[vlx] stacks (%s): peak %u live threads, peak sum of their stacks %u KiB at t=%u ms; now %u live, %u KiB%s\n", why, g_live_threads_peak,
	      g_live_stack_peak / 1024, (unsigned) (g_live_stack_peak_us / 1000), g_live_threads, g_live_stack / 1024,
	      g_stack_account ? " (taken from the user budget: VLX_STACK_ACCOUNT=1)" : "");
}
static void thread_finish (int status)
{
	vthread *t = self ();
	if (t->prof_timer) { sys1 (NR_timer_delete, t->prof_timer - 1); t->prof_timer = 0; }   /* kit #6 fps: VLX_PROF's timer (qemu-user keeps 32) */
	stack_line (t, "ended");
	klock (); t->exit_status = status; t->state = TS_DEAD; kwake (&t->o.seq); kunlock ();
	for (;;) sys1 (NR_exit, 0);
}
int sceKernelExitThread (int status) { thread_finish (status); return 0; }
int sceKernelExitDeleteThread (int status) { thread_finish (status); return 0; }   /* the slot/stack are reclaimed by nobody: fine for tests */

int sceKernelWaitThreadEndCB (SceUID thid, int *stat, SceUInt *timeout)
{
	vthread *t = thr_get (thid);
	if (!t || thid == 0) return fail ("sceKernelWaitThreadEndCB", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID);
	uint64_t dl = deadline_of (timeout);
	klock ();
	while (t->state != TS_DEAD)
		if (kwait (&t->o.seq, dl) && t->state != TS_DEAD) { kunlock (); if (timeout) *timeout = 0; return SCE_KERNEL_ERROR_WAIT_TIMEOUT; }
	if (stat) *stat = t->exit_status;
	kunlock ();
	timeout_left (timeout, dl);
	return 0;
}

int sceKernelDeleteThread (SceUID thid)
{
	vthread *t = thr_get (thid);
	if (!t || thid == 0) return fail ("sceKernelDeleteThread", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID);
	if (t->state == TS_RUNNING) return fail ("sceKernelDeleteThread", SCE_KERNEL_ERROR_NOT_DORMANT);
	/* Linux thread really gone: the kernel clears ctid and wakes it with a SHARED futex wake (CLONE_CHILD_CLEARTID), which a
	 * PRIVATE wait never sees -- the cause of a rare vlx-only deadlock in pthread_join -> pthread_detach -> sceKernelDeleteThread
	 * (devfail1, 2026-10-08: hello-diag G5, the deleter asleep on a ctid that was already 0'd). Shared wait, re-checked every ms. */
	if (t->state == TS_DEAD) while (t->ctid) { struct lts ts = { 0, 1000000 }; int v = t->ctid; if (v) sys4 (NR_futex, &t->ctid, 0 /* FUTEX_WAIT, shared */, v, &ts); }
	sys2 (NR_munmap, t->map, t->map_size);
	klock (); t->o.type = T_FREE; g_live_threads--; g_live_stack -= t->stack_size; if (t->stack_accounted) g_used [0] -= t->stack_size; kunlock ();
	return 0;
}

SceUID sceKernelGetThreadId (void) { return self ()->o.uid; }
int sceKernelGetThreadInfo (SceUID thid, SceKernelThreadInfo *info)
{
	vthread *t = thr_get (thid);
	if (!t) return fail ("sceKernelGetThreadInfo", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID);
	if (!info || info->size != sizeof (*info)) return fail ("sceKernelGetThreadInfo(size)", SCE_KERNEL_ERROR_ILLEGAL_SIZE);
	SceSize s = info->size; vmemset (info, 0, sizeof (*info)); info->size = s;
	info->processId = 0x40010003; vstrlcpy (info->name, t->o.name, sizeof (info->name)); info->attr = t->o.attr;
	info->status = t->state == TS_RUNNING ? SCE_THREAD_RUNNING : SCE_THREAD_DORMANT; info->entry = t->entry;
	info->stack = t->stack_lo; info->stackSize = (SceInt32) t->stack_size; info->initPriority = t->init_prio;
	info->currentPriority = t->prio; info->initCpuAffinityMask = t->affinity; info->currentCpuAffinityMask = t->affinity;
	info->exitStatus = t->exit_status;
	return 0;
}
int sceKernelChangeThreadPriority (SceUID thid, int priority) { vthread *t = thr_get (thid); if (!t) return fail ("sceKernelChangeThreadPriority", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID); t->prio = priority; return 0; }
/* k4-display: CPU3 (mask 0x80000, the system-reserved 4th core) is refused as on a Vita without a core-unlock plugin; VLX_CORE3=1 accepts it
 * (as with the owner's CapUnlocker), so the host's C3 probe and both of its paths run here */
static int g_core3_ok;
int sceKernelChangeThreadCpuAffinityMask (SceUID thid, int mask) { vthread *t = thr_get (thid); if (!t) return fail ("sceKernelChangeThreadCpuAffinityMask", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID); if ((mask & 0x80000) && !g_core3_ok) return fail ("sceKernelChangeThreadCpuAffinityMask(CPU3)", SCE_KERNEL_ERROR_ILLEGAL_CPU_AFFINITY_MASK); int o = t->affinity; t->affinity = mask; return o; }
int sceKernelGetThreadCpuAffinityMask (SceUID thid) { vthread *t = thr_get (thid); if (!t) return fail ("sceKernelGetThreadCpuAffinityMask", SCE_KERNEL_ERROR_UNKNOWN_THREAD_ID); return t->affinity ? t->affinity : 0x70000; }
int sceKernelDelayThread (SceUInt delay)
{
	do {
		SceUInt d = (g_poll_us && delay > g_poll_us) ? g_poll_us : delay;
		struct lts ts = { (long) (d / 1000000u), (long) (d % 1000000u) * 1000 };
		if (!d) { ts.nsec = 1000; }
		sys2 (NR_nanosleep, &ts, 0);
		delay -= d;
	} while (delay);
	return 0;
}
/* kit #9 runtime review (the fast TLS slots, vita/patches/k9-fast-tls.patch): VLX_TLS_FOREIGN=<slot>[,<n>] models a system module that keeps
 * per-thread state in TLS slot <slot> (hex 0x.. or decimal): with n = 0 every thread is born with the value 0x5EC0F00D there; with n > 0
 * (default 100000) each thread gets it written at its n-th sceKernelGetTLSAddr call (the module first called on that thread later on).
 * Unset (every gate run): the slots are a private zeroed array per thread, as before. */
void *sceKernelGetTLSAddr (int key)
{
	if (key < 0 || key >= 0x100) return 0;
	vthread *t = self ();
	if (g_tls_foreign_slot >= 0 && g_tls_foreign_after && t->tls_calls < g_tls_foreign_after && ++t->tls_calls == g_tls_foreign_after)
		t->tls [g_tls_foreign_slot] = VLX_TLS_FOREIGN_VALUE;
	return &t->tls [key];
}
void *sceKernelGetThreadTLSAddr (SceUID thid, int key) { vthread *t = thr_get (thid); if (!t || key < 0 || key >= 0x100) return 0; return &t->tls [key]; }

/* ------------------------------------------------------------------ semaphores, mutexes, lw mutexes, event flags, msg pipes */
SceUID sceKernelCreateSema (const char *name, SceUInt attr, int initVal, int maxVal, SceKernelSemaOptParam *option)
{
	(void) option; klock (); vobj *o = obj_new (T_SEMA, name); o->attr = attr; o->count = initVal; o->max = maxVal; kunlock (); return o->uid;
}
int sceKernelDeleteSema (SceUID semaid)
{
	klock (); vobj *o = obj_get (semaid, T_SEMA); if (!o) { kunlock (); return fail ("sceKernelDeleteSema", SCE_KERNEL_ERROR_UNKNOWN_SEMA_ID); }
	o->deleted = 1; kwake (&o->seq); o->type = T_FREE; kunlock (); return 0;
}
int sceKernelWaitSema (SceUID semaid, int signal, SceUInt *timeout)
{
	uint64_t dl = deadline_of (timeout);
	klock (); vobj *o = obj_get (semaid, T_SEMA);
	if (!o) { kunlock (); return fail ("sceKernelWaitSema", SCE_KERNEL_ERROR_UNKNOWN_SEMA_ID); }
	if (signal <= 0 || signal > o->max) { kunlock (); return fail ("sceKernelWaitSema", SCE_KERNEL_ERROR_ILLEGAL_COUNT); }
	while (o->count < signal) {
		int to = kwait (&o->seq, dl);
		if (o->deleted || o->type != T_SEMA) { kunlock (); return SCE_KERNEL_ERROR_WAIT_DELETE; }
		if (to && o->count < signal) { kunlock (); if (timeout) *timeout = 0; return SCE_KERNEL_ERROR_WAIT_TIMEOUT; }
	}
	o->count -= signal; kunlock (); timeout_left (timeout, dl); return 0;
}
int sceKernelSignalSema (SceUID semaid, int signal)
{
	klock (); vobj *o = obj_get (semaid, T_SEMA);
	if (!o) { kunlock (); return fail ("sceKernelSignalSema", SCE_KERNEL_ERROR_UNKNOWN_SEMA_ID); }
	if (signal <= 0 || o->count + signal > o->max) { kunlock (); return fail ("sceKernelSignalSema", SCE_KERNEL_ERROR_SEMA_OVF); }
	o->count += signal; kwake (&o->seq); kunlock (); return 0;
}

static int mtx_lock (vobj *o, int n, SceUInt *timeout, int lw)
{
	vthread *me = self (); uint64_t dl = deadline_of (timeout);
	if (n <= 0) return lw ? SCE_KERNEL_ERROR_LW_MUTEX_LOCK_OVF : SCE_KERNEL_ERROR_ILLEGAL_COUNT;
	if (o->owner == me) {
		if (!(o->attr & 2)) return lw ? SCE_KERNEL_ERROR_LW_MUTEX_RECURSIVE : SCE_KERNEL_ERROR_MUTEX_RECURSIVE;
		o->count += n; return 0;
	}
	while (o->owner) {
		int to = kwait (&o->seq, dl);
		if (o->deleted) return lw ? SCE_KERNEL_ERROR_WAIT_DELETE_LW_MUTEX : SCE_KERNEL_ERROR_WAIT_DELETE_MUTEX;
		if (to && o->owner) { if (timeout) *timeout = 0; return SCE_KERNEL_ERROR_WAIT_TIMEOUT; }
	}
	o->owner = me; o->count = n; timeout_left (timeout, dl); return 0;
}
static int mtx_unlock (vobj *o, int n, int lw)
{
	if (o->owner != self () || n <= 0 || n > o->count) return lw ? SCE_KERNEL_ERROR_LW_MUTEX_UNLOCK_UDF : SCE_KERNEL_ERROR_MUTEX_UNLOCK_UDF;
	o->count -= n; if (!o->count) { o->owner = 0; kwake (&o->seq); }
	return 0;
}
SceUID sceKernelCreateMutex (const char *name, SceUInt attr, int initCount, SceKernelMutexOptParam *option)
{
	(void) option; klock (); vobj *o = obj_new (T_MUTEX, name); o->attr = attr;
	if (initCount > 0) { o->owner = self (); o->count = initCount; }
	kunlock (); return o->uid;
}
int sceKernelDeleteMutex (SceUID mutexid)
{
	klock (); vobj *o = obj_get (mutexid, T_MUTEX); if (!o) { kunlock (); return fail ("sceKernelDeleteMutex", SCE_KERNEL_ERROR_UNKNOWN_MUTEX_ID); }
	o->deleted = 1; kwake (&o->seq); o->type = T_FREE; kunlock (); return 0;
}
int sceKernelLockMutex (SceUID mutexid, int lockCount, unsigned int *timeout)
{
	klock (); vobj *o = obj_get (mutexid, T_MUTEX); if (!o) { kunlock (); return fail ("sceKernelLockMutex", SCE_KERNEL_ERROR_UNKNOWN_MUTEX_ID); }
	int r = mtx_lock (o, lockCount, timeout, 0); kunlock (); return r < 0 ? fail ("sceKernelLockMutex", r) : r;
}
int sceKernelUnlockMutex (SceUID mutexid, int unlockCount)
{
	klock (); vobj *o = obj_get (mutexid, T_MUTEX); if (!o) { kunlock (); return fail ("sceKernelUnlockMutex", SCE_KERNEL_ERROR_UNKNOWN_MUTEX_ID); }
	int r = mtx_unlock (o, unlockCount, 0); kunlock (); return r < 0 ? fail ("sceKernelUnlockMutex", r) : r;
}

#define LW_MAGIC 0x564c584dU   /* "VLXM" */
static vobj *lw_get (SceKernelLwMutexWork *w)
{
	uint32_t *d = (uint32_t *) w; if (!w || d [0] != LW_MAGIC) return 0; return obj_get ((SceUID) d [1], T_LWMUTEX);
}
int sceKernelCreateLwMutex (SceKernelLwMutexWork *pWork, const char *pName, unsigned int attr, int initCount, const SceKernelLwMutexOptParam *pOptParam)
{
	(void) pOptParam; if (!pWork) return fail ("sceKernelCreateLwMutex", SCE_KERNEL_ERROR_ILLEGAL_ADDR);
	klock (); vobj *o = obj_new (T_LWMUTEX, pName); o->attr = attr;
	if (initCount > 0) { o->owner = self (); o->count = initCount; }
	uint32_t *d = (uint32_t *) pWork; d [0] = LW_MAGIC; d [1] = (uint32_t) o->uid; kunlock (); return 0;
}
int sceKernelDeleteLwMutex (SceKernelLwMutexWork *pWork)
{
	klock (); vobj *o = lw_get (pWork); if (!o) { kunlock (); return fail ("sceKernelDeleteLwMutex", SCE_KERNEL_ERROR_LW_MUTEX_ERROR); }
	o->deleted = 1; kwake (&o->seq); o->type = T_FREE; ((uint32_t *) pWork) [0] = 0; kunlock (); return 0;
}
int sceKernelLockLwMutex (SceKernelLwMutexWork *pWork, int lockCount, unsigned int *pTimeout)
{
	klock (); vobj *o = lw_get (pWork); if (!o) { kunlock (); return fail ("sceKernelLockLwMutex", SCE_KERNEL_ERROR_LW_MUTEX_ERROR); }
	int r = mtx_lock (o, lockCount, pTimeout, 1); kunlock (); return r < 0 ? fail ("sceKernelLockLwMutex", r) : r;
}
int sceKernelUnlockLwMutex (SceKernelLwMutexWork *pWork, int unlockCount)
{
	klock (); vobj *o = lw_get (pWork); if (!o) { kunlock (); return fail ("sceKernelUnlockLwMutex", SCE_KERNEL_ERROR_LW_MUTEX_ERROR); }
	int r = mtx_unlock (o, unlockCount, 1); kunlock (); return r < 0 ? fail ("sceKernelUnlockLwMutex", r) : r;
}

SceUID sceKernelCreateEventFlag (const char *name, int attr, int bits, SceKernelEventFlagOptParam *opt)
{
	(void) opt; klock (); vobj *o = obj_new (T_EVF, name); o->attr = (unsigned) attr; o->pattern = (unsigned) bits; kunlock (); return o->uid;
}
int sceKernelDeleteEventFlag (int evid)
{
	klock (); vobj *o = obj_get (evid, T_EVF); if (!o) { kunlock (); return fail ("sceKernelDeleteEventFlag", SCE_KERNEL_ERROR_UNKNOWN_EVF_ID); }
	o->deleted = 1; kwake (&o->seq); o->type = T_FREE; kunlock (); return 0;
}
int sceKernelSetEventFlag (SceUID evid, unsigned int bits)
{
	klock (); vobj *o = obj_get (evid, T_EVF); if (!o) { kunlock (); return fail ("sceKernelSetEventFlag", SCE_KERNEL_ERROR_UNKNOWN_EVF_ID); }
	o->pattern |= bits; kwake (&o->seq); kunlock (); return 0;
}
int sceKernelPollEventFlag (int evid, unsigned int bits, unsigned int wait, unsigned int *outBits)
{
	klock (); vobj *o = obj_get (evid, T_EVF); if (!o) { kunlock (); return fail ("sceKernelPollEventFlag", SCE_KERNEL_ERROR_UNKNOWN_EVF_ID); }
	int ok = (wait & SCE_EVENT_WAITOR) ? (o->pattern & bits) != 0 : (o->pattern & bits) == bits;
	if (!ok) { kunlock (); return SCE_KERNEL_ERROR_EVF_COND; }
	if (outBits) *outBits = o->pattern;
	if (wait & SCE_EVENT_WAITCLEAR) o->pattern = 0; else if (wait & SCE_EVENT_WAITCLEAR_PAT) o->pattern &= ~bits;
	kunlock (); return 0;
}
/* message pipes: referenced by newlib, never created by our programs */
int sceKernelDeleteMsgPipe (SceUID uid) { (void) uid; return fail ("sceKernelDeleteMsgPipe", SCE_KERNEL_ERROR_UNKNOWN_UID); }
int sceKernelSendMsgPipe (SceUID uid, void *message, unsigned int size, int unk1, void *unk2, unsigned int *timeout)
{ (void) uid; (void) message; (void) size; (void) unk1; (void) unk2; (void) timeout; return fail ("sceKernelSendMsgPipe", SCE_KERNEL_ERROR_UNKNOWN_UID); }
int sceKernelReceiveMsgPipe (SceUID uid, void *message, SceSize size, int unk1, void *unk2, unsigned int *timeout)
{ (void) uid; (void) message; (void) size; (void) unk1; (void) unk2; (void) timeout; return fail ("sceKernelReceiveMsgPipe", SCE_KERNEL_ERROR_UNKNOWN_UID); }

/* ------------------------------------------------------------------ memory blocks */
static unsigned g_budget [3] = { 333824u * 1024u, 112640u * 1024u, 26624u * 1024u }, g_used [3];   /* user, cdram, phycont */
static int mem_kind (unsigned type) { return type == SCE_KERNEL_MEMBLOCK_TYPE_USER_CDRAM_RW ? 1 : (type == SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_RW || type == SCE_KERNEL_MEMBLOCK_TYPE_USER_MAIN_PHYCONT_NC_RW) ? 2 : 0; }
SceUID sceKernelAllocMemBlock (const char *name, SceKernelMemBlockType type, SceSize size, SceKernelAllocMemBlockOpt *opt)
{
	int kind = mem_kind ((unsigned) type);
	unsigned gran = kind == 1 ? 0x40000 : kind == 2 ? 0x100000 : 0x1000;
	if (!size || (size & (gran - 1))) return fail ("sceKernelAllocMemBlock(size)", SCE_KERNEL_ERROR_ILLEGAL_SIZE);
	unsigned align = (opt && (opt->attr & SCE_KERNEL_ALLOC_MEMBLOCK_ATTR_HAS_ALIGNMENT) && opt->alignment > 0x1000) ? opt->alignment : 0x1000;
	klock ();
	if (g_used [kind] + size > g_budget [kind] || g_used [kind] + size < g_used [kind]) { kunlock (); return fail ("sceKernelAllocMemBlock(budget)", SCE_KERNEL_ERROR_NO_MEMORY); }
	g_used [kind] += size;
	kunlock ();
	unsigned msz = size + (align > 0x1000 ? align : 0);
	char *m = (char *) sys6 (NR_mmap2, 0, msz, 3 /* RW, never X */, 0x22, -1, 0);
	if ((unsigned long) m > 0xfffff000ul) { klock (); g_used [kind] -= size; kunlock (); return fail ("sceKernelAllocMemBlock(mmap)", SCE_KERNEL_ERROR_NO_MEMORY); }
	char *base = m;
	if (align > 0x1000) {
		base = (char *) (((uintptr_t) m + align - 1) & ~(uintptr_t) (align - 1));
		if (base > m) sys2 (NR_munmap, m, base - m);
		char *end = base + size, *mend = m + msz; if (mend > end) sys2 (NR_munmap, end, mend - end);
	}
	klock (); vobj *o = obj_new (T_MEMBLOCK, name); o->base = base; o->size = size; o->mtype = (unsigned) type; o->kind = (unsigned) kind; kunlock ();
	return o->uid;
}
int sceKernelGetMemBlockBase (SceUID uid, void **basep)
{
	vobj *o = obj_get (uid, T_MEMBLOCK); if (!o || !basep) return fail ("sceKernelGetMemBlockBase", SCE_KERNEL_ERROR_UNKNOWN_UID);
	*basep = o->base; return 0;
}
/* ---- kit #8 jit track: HENkaku's ForVM code memory, modelled with the rules the owner's console showed (devicelogs/20261021-kit7-owner-
 * extras/jitprobe.txt, the kit #7 probe SHPD00040): a block costs USER memory (free user 333824 -> 329728 KiB for 4 MiB); the per-process VM
 * quota refused a second 16 MiB block after a 4 MiB one (0x80024B0B) -- the quota itself is unknown, modelled as VLX_FORVM_QUOTA_KIB
 * (default 16384, ESTIMATE) of ForVM in total and VLX_FORVM_MAX_KIB (default = the quota) for one block; Open is process-wide and NOT
 * nestable: a second Open while one is held -- on the same thread or another -- is refused with 0x80010058; code runs while the domain
 * is open (PASS) and after a Close; a thread that did not Open must not write (the probe's 'non-opener-write' test stopped the console):
 * here the block is writable only while the domain is open (closed = read + execute: a write into a closed window faults, as the
 * azaharvita race), and Sync / Close from a thread other than the opener is logged as a FORVM RULE line and fails. Vita3K does not model
 * ForVM at all; vlx models only these rules -- the cost of Open / Close / Sync (~11 / ~5.7 us on the console) is not modelled (qemu-user
 * keeps its translated code coherent with writes by itself, so Sync has nothing to do here). The owner's console decides (README). */
#define FORVM_MTYPE 0x0fe00000u   /* the vobj's mtype for a ForVM block (no real SceKernelMemBlockType has this value) */
static unsigned g_forvm_quota = 16384u * 1024u, g_forvm_max, g_forvm_used;
static int g_forvm_noprotect;   /* VLX_FORVM_NOPROTECT=1 (measurement only): the block stays read+write+execute -- qemu-user discards the
				 * translated code of a region whose protection gains write access, so the open / close model costs qemu (never
				 * the console) a retranslation of every trace at every window; the rule checks (opener, nesting) stay */
static volatile int g_vmdom_open; static SceUID g_vmdom_opener; static unsigned g_vmdom_opens, g_vmdom_syncs, g_vmdom_rule;
static void forvm_protect_all (int prot)
{
	if (g_forvm_noprotect) return;
	for (int i = 0; i < MAX_OBJ; i++) { vobj *o = &g_obj [i]; if (o->type == T_MEMBLOCK && o->mtype == FORVM_MTYPE) sys3 (NR_mprotect, o->base, o->size, prot); }
}
SceUID sceKernelAllocMemBlockForVM (const char *name, SceSize size)
{
	unsigned max = g_forvm_max ? g_forvm_max : g_forvm_quota;
	if (!size || (size & 0xfffff)) return fail ("sceKernelAllocMemBlockForVM(size: 1 MiB granule)", SCE_KERNEL_ERROR_ILLEGAL_SIZE);
	klock ();
	if (size > max || g_forvm_used + size > g_forvm_quota) { kunlock (); vlog ("[vlx] sceKernelAllocMemBlockForVM('%s', %u KiB): refused 0x80024b0b (VM quota model: %u KiB in ForVM, quota %u KiB, one block <= %u KiB)\n", name, size / 1024, g_forvm_used / 1024, g_forvm_quota / 1024, max / 1024); return (SceUID) 0x80024b0b; }
	if (g_used [0] + size > g_budget [0]) { kunlock (); return fail ("sceKernelAllocMemBlockForVM(user budget)", SCE_KERNEL_ERROR_NO_MEMORY); }
	g_used [0] += size; g_forvm_used += size;
	kunlock ();
	char *m = (char *) sys6 (NR_mmap2, 0, size, g_vmdom_open || g_forvm_noprotect ? 7 : 5 /* closed: R+X, open: R+W+X */, 0x22, -1, 0);
	if ((unsigned long) m > 0xfffff000ul) { klock (); g_used [0] -= size; g_forvm_used -= size; kunlock (); return fail ("sceKernelAllocMemBlockForVM(mmap)", SCE_KERNEL_ERROR_NO_MEMORY); }
	klock (); vobj *o = obj_new (T_MEMBLOCK, name); o->base = m; o->size = size; o->mtype = FORVM_MTYPE; o->kind = 0; kunlock ();
	vlog ("[vlx] sceKernelAllocMemBlockForVM('%s', %u KiB) -> %p (user budget: %u KiB in use of %u; ForVM %u of the %u KiB quota model)\n", name, size / 1024, m, g_used [0] / 1024, g_budget [0] / 1024, g_forvm_used / 1024, g_forvm_quota / 1024);
	return o->uid;
}
int sceKernelFreeMemBlockForVM (SceUID uid)
{
	klock (); vobj *o = obj_get (uid, T_MEMBLOCK); if (!o || o->mtype != FORVM_MTYPE) { kunlock (); return fail ("sceKernelFreeMemBlockForVM", SCE_KERNEL_ERROR_UNKNOWN_UID); }
	sys2 (NR_munmap, o->base, o->size); g_used [0] -= o->size; g_forvm_used -= o->size; o->type = T_FREE; kunlock (); return 0;
}
int sceKernelOpenVMDomain (void)
{
	klock ();
	if (g_vmdom_open) { kunlock (); return (int) 0x80010058; }   /* not nestable, on any thread (the probe's test 8) */
	g_vmdom_open = 1; g_vmdom_opener = self ()->o.uid; g_vmdom_opens++;
	forvm_protect_all (7);
	kunlock (); return 0;
}
int sceKernelCloseVMDomain (void)
{
	klock ();
	if (!g_vmdom_open) { kunlock (); return fail ("sceKernelCloseVMDomain(not open)", (int) 0x80010058); }
	if (self ()->o.uid != g_vmdom_opener) {
		g_vmdom_rule++; kunlock ();
		vlog ("[vlx] FORVM RULE: thread 0x%08x closed the VM domain that thread 0x%08x opened (the console: one owner of Open / write / Sync / Close)\n", (unsigned) self ()->o.uid, (unsigned) g_vmdom_opener);
		return (int) 0x80010058;
	}
	forvm_protect_all (5);
	g_vmdom_open = 0; g_vmdom_opener = 0;
	kunlock (); return 0;
}
int sceKernelSyncVMDomain (SceUID uid, void *data, SceSize size)
{
	vobj *o = obj_get (uid, T_MEMBLOCK);
	if (!o || o->mtype != FORVM_MTYPE) return fail ("sceKernelSyncVMDomain(uid)", SCE_KERNEL_ERROR_UNKNOWN_UID);
	if ((char *) data < (char *) o->base || (char *) data + size > (char *) o->base + o->size) return fail ("sceKernelSyncVMDomain(range)", SCE_KERNEL_ERROR_ILLEGAL_SIZE);
	if (!g_vmdom_open || self ()->o.uid != g_vmdom_opener) {
		g_vmdom_rule++;
		vlog ("[vlx] FORVM RULE: thread 0x%08x synced code it may not have written (domain %s, opener 0x%08x): on the console only the thread that Opened writes\n", (unsigned) self ()->o.uid, g_vmdom_open ? "open" : "CLOSED", (unsigned) g_vmdom_opener);
		return (int) 0x80010058;
	}
	g_vmdom_syncs++;
	return 0;
}
int sceKernelFreeMemBlock (SceUID uid)
{
	klock (); vobj *o = obj_get (uid, T_MEMBLOCK); if (!o) { kunlock (); return fail ("sceKernelFreeMemBlock", SCE_KERNEL_ERROR_UNKNOWN_UID); }
	if (o->mtype == FORVM_MTYPE) g_forvm_used -= o->size;
	sys2 (NR_munmap, o->base, o->size); g_used [o->kind] -= o->size; o->type = T_FREE; kunlock (); return 0;
}
int sceKernelGetFreeMemorySize (SceKernelFreeMemorySizeInfo *info)
{
	if (!info || info->size != sizeof (*info)) return fail ("sceKernelGetFreeMemorySize", SCE_KERNEL_ERROR_ILLEGAL_SIZE);
	info->size_user = (int) (g_budget [0] - g_used [0]);
	if (g_stack_account) info->size_user &= ~0xfffff;   /* kit5 mem: the device reports whole MiB (vita/vlx/run.sh, VLX_STACK_ACCOUNT=1) */
	info->size_cdram = (int) (g_budget [1] - g_used [1]); info->size_phycont = (int) (g_budget [2] - g_used [2]);
	return 0;
}

/* ------------------------------------------------------------------ sceIo on a host directory */
static char g_root [256] = "/vlx";
static int lerr (long r)   /* -errno (Linux) -> SCE_ERROR_ERRNO_* (newlib numbering in the low byte) */
{
	int e = (int) -r;
	switch (e) { case 36: e = 91; break; case 38: e = 88; break; case 39: e = 90; break; case 40: e = 92; break; default: if (e > 34) e = 5; }
	return (int) (0x80010000u | (unsigned) e);
}
/* kit #8 boot review: VLX_STRICT_PATHS=1 refuses a ROOTED device path ("/ux0:data/...", what managed code builds) as the Vita's sceIo does
 * (it takes "ux0:..." only; the PAL's resolve_path strips the '/', host code must too). Off by default: the stand-in's own path map
 * accepts it (Linux takes the '//'), which hid kit #8's video-open bug. */
static int g_strict_paths;
static int map_path (const char *in, char *out, size_t cap)
{
	const char *c = 0;
	for (int i = 0; in [i] && i < 12; i++) if (in [i] == ':') { c = in + i; break; }
	if (g_strict_paths && in [0] == '/' && c) { vlog ("[vlx] VLX_STRICT_PATHS: rooted device path refused (the Vita's sceIo takes \"ux0:...\"): %s\n", in); return -1; }
	size_t rl = vstrlen (g_root), n = 0;
	if (rl + 2 >= cap) return -1;
	vmemcpy (out, g_root, rl); n = rl; out [n++] = '/';
	const char *rest = in;
	if (c) { size_t dl = (size_t) (c - in); if (n + dl + 1 >= cap) return -1; vmemcpy (out + n, in, dl); n += dl; out [n++] = '/'; rest = c + 1; }
	while (*rest == '/') rest++;
	size_t l = vstrlen (rest); if (n + l + 1 >= cap) return -1;
	vmemcpy (out + n, rest, l); n += l; out [n] = 0;
	while (n > rl + 1 && out [n - 1] == '/') out [--n] = 0;
	return 0;
}
static void unix_to_dt (int64_t sec, uint32_t nsec, SceDateTime *t) { tick_to_dt ((uint64_t) (sec * 1000000 + nsec / 1000) + EPOCH_TICK, t); }
static void statx_to_sce (const struct lstatx *x, SceIoStat *st)
{
	vmemset (st, 0, sizeof (*st));
	unsigned type = x->mode & 0170000, perm = (x->mode & 0700);
	st->st_mode = (type == 0040000 ? SCE_S_IFDIR : type == 0120000 ? SCE_S_IFLNK : SCE_S_IFREG) | perm | (perm >> 6);
	st->st_attr = (type == 0040000 ? SCE_SO_IFDIR : SCE_SO_IFREG) | (perm >> 6);
	st->st_size = (SceOff) x->size;
	unix_to_dt (x->btime.sec ? x->btime.sec : x->ctime.sec, x->btime.sec ? x->btime.nsec : x->ctime.nsec, &st->st_ctime);
	unix_to_dt (x->atime.sec, x->atime.nsec, &st->st_atime);
	unix_to_dt (x->mtime.sec, x->mtime.nsec, &st->st_mtime);
}
#define FD_UID(fd) ((SceUID) (0x10000 + (fd)))
#define DIR_UID(fd) ((SceUID) (0x20000 + (fd)))
static int uid_fd (SceUID u) { return (u >= 0x10000 && u < 0x20000) ? u - 0x10000 : -1; }

SceUID sceIoOpen (const char *file, int flags, SceMode mode)
{
	if (file && !vstrncmp (file, "tty0:", 6)) return FD_UID ((flags & SCE_O_WRONLY) ? 2 : 0);   /* newlib's stdio: all output -> stderr (keeps order with sceClibPrintf) */
	char p [512]; if (!file || map_path (file, p, sizeof (p)) < 0) return fail ("sceIoOpen(path)", (int) 0x80010016);
	int lf = ((flags & SCE_O_RDWR) == SCE_O_RDWR) ? L_O_RDWR : (flags & SCE_O_WRONLY) ? L_O_WRONLY : 0;
	if (flags & SCE_O_APPEND) lf |= L_O_APPEND; if (flags & SCE_O_CREAT) lf |= L_O_CREAT;
	if (flags & SCE_O_TRUNC) lf |= L_O_TRUNC; if (flags & SCE_O_EXCL) lf |= L_O_EXCL;
	long fd = sys3 (NR_open, p, lf | L_O_LARGEFILE, mode ? (mode & 0777) | 0600 : 0666);
	if (fd < 0) return fail ("sceIoOpen", lerr (fd));
	struct lstatx x; if (sys5 (NR_statx, fd, "", L_AT_EMPTY_PATH, 0x7ff, &x) == 0 && (x.mode & 0170000) == 0040000) {
		sys1 (NR_close, fd); return fail ("sceIoOpen(dir)", (int) 0x80010015);   /* sceIo does not open directories */
	}
	return FD_UID (fd);
}
int sceIoClose (SceUID fd) { int f = uid_fd (fd); if (f < 0) return fail ("sceIoClose", (int) 0x80010009); long r = sys1 (NR_close, f); return r < 0 ? lerr (r) : 0; }
/* kit #8 boot track (timing only): VLX_CARD=1 models the memory card's read cost -- every read of a file costs 2.87 ms + 65.6 ns per byte on
 * top of the host's time (the PCH-2000's ux0 in kit #7's storage test: random 4 KiB reads 3.14 ms, random 128 KiB 11.47 ms), and the card
 * serves one request at a time (a lock around the sleep). Off by default: vlx timing means nothing unless a comparison asks for this. */
static int g_card_model; static volatile int g_card_lock;
static void card_cost (long bytes)
{
	if (!g_card_model || bytes <= 0) return;
	while (__sync_lock_test_and_set (&g_card_lock, 1)) { struct lts y = { 0, 200000 }; sys2 (NR_nanosleep, &y, 0); }
	uint64_t us = 2870u + (uint64_t) bytes * 656u / 10000u;
	struct lts ts = { (long) (us / 1000000u), (long) (us % 1000000u) * 1000L }; sys2 (NR_nanosleep, &ts, 0);
	__sync_lock_release (&g_card_lock);
}
int sceIoRead (SceUID fd, void *data, SceSize size) { int f = uid_fd (fd); if (f < 0) return fail ("sceIoRead", (int) 0x80010009); long r = sys3 (NR_read, f, data, size); if (r > 0 && f > 2) card_cost (r); return r < 0 ? fail ("sceIoRead", lerr (r)) : (int) r; }
int sceIoWrite (SceUID fd, const void *data, SceSize size) { int f = uid_fd (fd); if (f < 0) return fail ("sceIoWrite", (int) 0x80010009); long r = sys3 (NR_write, f, data, size); return r < 0 ? fail ("sceIoWrite", lerr (r)) : (int) r; }
int sceIoPread (SceUID fd, void *data, SceSize size, SceOff offset)
{
	int f = uid_fd (fd); if (f < 0) return fail ("sceIoPread", (int) 0x80010009);
	long r = sys6 (NR_pread64, f, (long) data, (long) size, 0, (long) (uint32_t) offset, (long) (uint32_t) ((uint64_t) offset >> 32));
	if (r > 0) card_cost (r);
	return r < 0 ? fail ("sceIoPread", lerr (r)) : (int) r;
}
/* device kit #10 io (vita/shepherd/k10_io.inc.c, SHEPHERD_K10_DMA): the DMA copy's stand-in -- a plain copy (timing means nothing here) */
int sceDmacMemcpy (void *dst, const void *src, SceSize size) { vmemcpy (dst, src, size); return 0; }
int sceIoPwrite (SceUID fd, const void *data, SceSize size, SceOff offset)
{
	int f = uid_fd (fd); if (f < 0) return fail ("sceIoPwrite", (int) 0x80010009);
	long r = sys6 (NR_pwrite64, f, (long) data, (long) size, 0, (long) (uint32_t) offset, (long) (uint32_t) ((uint64_t) offset >> 32));
	return r < 0 ? fail ("sceIoPwrite", lerr (r)) : (int) r;
}
SceOff sceIoLseek (SceUID fd, SceOff offset, int whence)
{
	int f = uid_fd (fd); if (f < 0) return fail ("sceIoLseek", (int) 0x80010009);
	int64_t res = 0; long r = sys5 (NR_llseek, f, (long) (uint32_t) ((uint64_t) offset >> 32), (long) (uint32_t) offset, &res, whence);
	return r < 0 ? fail ("sceIoLseek", lerr (r)) : res;
}
long sceIoLseek32 (SceUID fd, long offset, int whence) { int f = uid_fd (fd); if (f < 0) return fail ("sceIoLseek32", (int) 0x80010009); long r = sys3 (NR_lseek, f, offset, whence); return r < 0 ? fail ("sceIoLseek32", lerr (r)) : r; }
int sceIoGetstat (const char *file, SceIoStat *stat)
{
	char p [512]; if (!file || map_path (file, p, sizeof (p)) < 0) return fail ("sceIoGetstat(path)", (int) 0x80010016);
	struct lstatx x; long r = sys5 (NR_statx, L_AT_FDCWD, p, 0, 0x7ff, &x);
	if (r < 0) return fail ("sceIoGetstat", lerr (r));
	statx_to_sce (&x, stat); return 0;
}
int sceIoGetstatByFd (SceUID fd, SceIoStat *stat)
{
	int f = uid_fd (fd); if (f < 0) return fail ("sceIoGetstatByFd", (int) 0x80010009);
	struct lstatx x; long r = sys5 (NR_statx, f, "", L_AT_EMPTY_PATH, 0x7ff, &x);
	if (r < 0) return fail ("sceIoGetstatByFd", lerr (r));
	statx_to_sce (&x, stat); return 0;
}
int sceIoChstatByFd (SceUID fd, const SceIoStat *buf, unsigned int cbit)
{
	int f = uid_fd (fd); if (f < 0) return fail ("sceIoChstatByFd", (int) 0x80010009);
	if (cbit & SCE_CST_SIZE) { long r = sys3 (NR_ftruncate64, f, 0, 0); (void) r;
		uint64_t sz = (uint64_t) buf->st_size; r = sys4 (NR_ftruncate64, f, 0, (long) (uint32_t) sz, (long) (uint32_t) (sz >> 32));
		if (r < 0) return fail ("sceIoChstatByFd", lerr (r)); }
	return 0;
}
int sceIoChstat (const char *file, SceIoStat *stat, int bits)
{
	char p [512]; if (!file || map_path (file, p, sizeof (p)) < 0) return fail ("sceIoChstat(path)", (int) 0x80010016);
	(void) stat; (void) bits; struct lstatx x; long r = sys5 (NR_statx, L_AT_FDCWD, p, 0, 0x7ff, &x); return r < 0 ? fail ("sceIoChstat", lerr (r)) : 0;
}
#include <psp2/io/devctl.h>
/* kit #7 (track probes): 0x3001 (free space) answers for the stores vlx models -- ux0 always (64 GiB, 32 GiB free, 32 KiB clusters: fixed
 * numbers, so a run's device profile never depends on the Mac's disk), plus every store named in VLX_STORES (e.g. VLX_STORES=imc0,uma0:
 * 1 GiB, 0.5 GiB free each; its files are <root>/<store>/...); any other store: not mounted (0x80010013, as an absent device) */
static char g_stores [64];
int sceIoDevctl (const char *dev, unsigned int cmd, void *indata, int inlen, void *outdata, int outlen)
{
	(void) indata; (void) inlen;
	if (cmd != 0x3001 || !dev || !outdata || outlen < (int) sizeof (SceIoDevInfo)) return fail ("sceIoDevctl", (int) 0x80010016);
	size_t dl = 0; while (dev [dl] && dev [dl] != ':') dl++;
	int ux0 = dl == 3 && !vstrncmp (dev, "ux0", 3), listed = 0;
	for (const char *p = g_stores; *p; ) {
		size_t l = 0; while (p [l] && p [l] != ',') l++;
		if (l == dl && !vstrncmp (p, dev, dl)) listed = 1;
		p += l; if (*p == ',') p++;
	}
	if (!ux0 && !listed) return fail ("sceIoDevctl(not mounted)", (int) 0x80010013);
	SceIoDevInfo *di = (SceIoDevInfo *) outdata; vmemset (di, 0, sizeof (*di));
	di->max_size = ux0 ? (SceOff) 64 << 30 : (SceOff) 1 << 30; di->free_size = di->max_size / 2; di->cluster_size = 32768;
	return 0;
}
/* kit #7: the firmware / model a device profile asks for (the owner's PCH-2000 on 3.65; imc0 mounted only with VLX_STORES naming it) */
#include <psp2/kernel/modulemgr.h>
static int g_model_cd = 0x10000;   /* (kit #8: 0x20000 when VLX_MODEL=pstv) */
int sceKernelGetModelForCDialog (void) { return g_model_cd; }
int sceKernelGetModel (void) { return 0x10000; }
/* kit #8 boot track: VLX_FW_REPORTED=<x.yy> = what sceKernelGetSystemSwVersion reports (HENkaku's version spoof: e.g. 3.73; default 3.65 =
 * no spoof); the real firmware (SceVshBridge _vshSblGetSystemSwVersion) stays 3.65 */
static unsigned g_fw_reported = 0x03650011u;
static void fw_string (unsigned v, char *out, size_t n)
{
	char b [8]; b [0] = (char) ('0' + ((v >> 24) & 0xf)); b [1] = '.'; b [2] = (char) ('0' + ((v >> 20) & 0xf)); b [3] = (char) ('0' + ((v >> 16) & 0xf)); b [4] = 0;
	vmemset (out, 0, n); vstrlcpy (out, b, n);
}
int sceKernelGetSystemSwVersion (SceKernelSystemSwVersion *v)
{
	if (!v) return fail ("sceKernelGetSystemSwVersion", (int) 0x80020005);
	fw_string (g_fw_reported, v->versionString, sizeof (v->versionString));
	v->version = g_fw_reported; return 0;
}
/* kit #8 boot track: SceVshBridge's hardware checks, modelled on the owner's console (VLX_MODEL=pch2000, the default: a PCH-2000 with a memory
 * card in, so imc0 is not mounted unless VLX_STORES names it; pch1000 / pch1100 (3G) / pstv), VLX_VSH_FAIL=1 = every check refuses (the
 * fallback to kit #7's rule). The console ID: a made-up one (only its product fields are ever logged). */
static int g_model = 2000, g_vsh_fail;
#include <psp2/vshbridge.h>
#define VSH_ANSWER(fn, expr) int fn (void) { if (g_vsh_fail) return fail (#fn, (int) 0x80010058); return (expr); }
VSH_ANSWER (vshSblAimgrIsDolce, g_model == 9000)
VSH_ANSWER (vshSblAimgrIsVITA, g_model != 9000)
VSH_ANSWER (vshSysconIsMCEmuCapable, g_model == 2000 || g_model == 9000)
VSH_ANSWER (vshSysconHasWWAN, g_model == 1100)
VSH_ANSWER (vshMemoryCardGetCardInsertState, g_model != 9000)
int _vshSblAimgrGetConsoleId (char CID [32])
{
	if (g_vsh_fail) return fail ("_vshSblAimgrGetConsoleId", (int) 0x80010058);
	vmemset (CID, 0, 32); CID [3] = 1; CID [4] = 1; CID [5] = 4; CID [6] = 0; CID [7] = (char) (g_model == 2000 ? 0x10 : g_model == 9000 ? 0x20 : 0x02);   /* (made up) */
	return 0;
}
int _vshSblGetSystemSwVersion (SceKernelFwInfo *v)
{
	if (g_vsh_fail) return fail ("_vshSblGetSystemSwVersion", (int) 0x80010058);
	if (!v) return fail ("_vshSblGetSystemSwVersion", (int) 0x80020005);
	fw_string (0x03650011u, v->versionString, sizeof (v->versionString)); v->version = 0x03650011u; return 0;
}
/* kit #7, the measurement app's probes: vlx has no firmware modules -- sysmodule loads are refused (the NGS check and the PMU counters
 * then say "does not load" and stop); the NGS / PMU entry points exist only so the eboot's objects link, and refuse too */
int sceSysmoduleLoadModule (unsigned id) { if (id == 0x54) { g_avc_mod = 1; return 0; } return fail ("sceSysmoduleLoadModule", (int) 0x805A1000); }   /* (kit #8: AVCDEC "loads") */
int sceSysmoduleUnloadModule (unsigned id) { if (id == 0x54 && g_avc_mod) { g_avc_mod = 0; return 0; } return fail ("sceSysmoduleUnloadModule", (int) 0x805A1000); }
#define VLX_REFUSE(fn) int fn (void) { return fail (#fn, (int) 0x804A0001); }
VLX_REFUSE (scePerfArmPmonReset) VLX_REFUSE (scePerfArmPmonSelectEvent) VLX_REFUSE (scePerfArmPmonStart) VLX_REFUSE (scePerfArmPmonStop)
VLX_REFUSE (scePerfArmPmonGetCounterValue) VLX_REFUSE (scePerfArmPmonSetCounterValue)
VLX_REFUSE (sceNgsSystemGetRequiredMemorySize) VLX_REFUSE (sceNgsSystemInit) VLX_REFUSE (sceNgsSystemUpdate) VLX_REFUSE (sceNgsSystemRelease)
VLX_REFUSE (sceNgsRackGetRequiredMemorySize) VLX_REFUSE (sceNgsRackInit) VLX_REFUSE (sceNgsRackGetVoiceHandle) VLX_REFUSE (sceNgsRackRelease)
VLX_REFUSE (sceNgsPatchCreateRouting) VLX_REFUSE (sceNgsVoiceSetParamsBlock) VLX_REFUSE (sceNgsVoicePlay) VLX_REFUSE (sceNgsVoiceKill)
void *sceNgsVoiceDefGetMasterBuss (void) { return 0; }
void *sceNgsVoiceDefGetSimpleVoice (void) { return 0; }
int sceIoSyncByFd (SceUID fd, int flag) { (void) flag; int f = uid_fd (fd); if (f < 0) return fail ("sceIoSyncByFd", (int) 0x80010009); sys1 (NR_fsync, f); return 0; }
int sceIoMkdir (const char *dir, SceMode mode) { char p [512]; if (map_path (dir, p, sizeof (p)) < 0) return (int) 0x80010016; long r = sys2 (NR_mkdir, p, 0777); (void) mode; return r < 0 ? fail ("sceIoMkdir", lerr (r)) : 0; }
int sceIoRmdir (const char *path) { char p [512]; if (map_path (path, p, sizeof (p)) < 0) return (int) 0x80010016; long r = sys1 (NR_rmdir, p); return r < 0 ? fail ("sceIoRmdir", lerr (r)) : 0; }
int sceIoRemove (const char *file) { char p [512]; if (map_path (file, p, sizeof (p)) < 0) return (int) 0x80010016; long r = sys1 (NR_unlink, p); return r < 0 ? fail ("sceIoRemove", lerr (r)) : 0; }
/* kit #7 integration review (TEST ONLY): VLX_IO_DELAY=<path part>:<ms> -- an sceIoRename whose old name contains <path part> first sleeps
 * <ms> in the calling thread only (a slow card under load: the P3 watchdog's own blackbox-live.txt write, e.g. blackbox-live:2000, must not
 * be booked as a suspension). Unset: no effect. */
static char g_io_delay_pat [48]; static unsigned g_io_delay_ms, g_io_delay_n;
static void io_delay (const char *path)
{
	if (!g_io_delay_ms || !g_io_delay_pat [0] || !path) return;
	size_t pl = vstrlen (g_io_delay_pat);
	for (const char *q = path; *q; q++)
		if (!vstrncmp (q, g_io_delay_pat, pl)) {
			unsigned n = __sync_add_and_fetch (&g_io_delay_n, 1u);
			vlog ("[vlx] VLX_IO_DELAY #%u: sceIoRename (%s) sleeps %u ms first (at %u ms of the run)\n", n, path, g_io_delay_ms, (unsigned) ((mono_us () - g_boot_us) / 1000u));
			struct lts ts = { (long) (g_io_delay_ms / 1000u), (long) (g_io_delay_ms % 1000u) * 1000000 }; sys2 (NR_nanosleep, &ts, 0); return;
		}
}
int sceIoRename (const char *oldname, const char *newname)
{
	io_delay (oldname);
	char a [512], b [512]; if (map_path (oldname, a, sizeof (a)) < 0 || map_path (newname, b, sizeof (b)) < 0) return (int) 0x80010016;
	long r = sys2 (NR_rename, a, b); return r < 0 ? fail ("sceIoRename", lerr (r)) : 0;
}

#define MAX_DIRS 32
static struct { int fd, pos, len; char buf [4096]; } g_dirs [MAX_DIRS];
SceUID sceIoDopen (const char *dirname)
{
	char p [512]; if (!dirname || map_path (dirname, p, sizeof (p)) < 0) return fail ("sceIoDopen(path)", (int) 0x80010016);
	long fd = sys3 (NR_open, p, L_O_DIRECTORY | L_O_LARGEFILE, 0);
	if (fd < 0) return fail ("sceIoDopen", lerr (fd));
	klock ();
	for (int i = 0; i < MAX_DIRS; i++) if (!g_dirs [i].fd) { g_dirs [i].fd = (int) fd; g_dirs [i].pos = g_dirs [i].len = 0; kunlock (); return DIR_UID (i); }
	kunlock (); sys1 (NR_close, fd); return fail ("sceIoDopen(table)", (int) 0x80010018);
}
int sceIoDread (SceUID fd, SceIoDirent *dir)
{
	int i = fd - 0x20000; if (i < 0 || i >= MAX_DIRS || !g_dirs [i].fd) return fail ("sceIoDread", (int) 0x80010009);
	for (;;) {
		if (g_dirs [i].pos >= g_dirs [i].len) {
			long n = sys3 (NR_getdents64, g_dirs [i].fd, g_dirs [i].buf, sizeof (g_dirs [i].buf));
			if (n < 0) return fail ("sceIoDread", lerr (n));
			if (n == 0) return 0;
			g_dirs [i].len = (int) n; g_dirs [i].pos = 0;
		}
		char *e = g_dirs [i].buf + g_dirs [i].pos; unsigned short reclen = *(unsigned short *) (e + 16); const char *name = e + 19;
		g_dirs [i].pos += reclen;
		if ((name [0] == '.' && !name [1]) || (name [0] == '.' && name [1] == '.' && !name [2])) continue;
		vmemset (dir, 0, sizeof (*dir)); vstrlcpy (dir->d_name, name, sizeof (dir->d_name));
		struct lstatx x; if (sys5 (NR_statx, g_dirs [i].fd, name, L_AT_SYMLINK_NOFOLLOW, 0x7ff, &x) == 0) statx_to_sce (&x, &dir->d_stat);
		return 1;
	}
}
int sceIoDclose (SceUID fd)
{
	int i = fd - 0x20000; if (i < 0 || i >= MAX_DIRS || !g_dirs [i].fd) return fail ("sceIoDclose", (int) 0x80010009);
	sys1 (NR_close, g_dirs [i].fd); g_dirs [i].fd = 0; return 0;
}

/* ------------------------------------------------------------------ process, time, rtc, misc */
SceUInt64 sceKernelGetProcessTimeWide (void) { return mono_us () - g_boot_us; }
SceUInt32 sceKernelGetProcessTimeLow (void) { return (SceUInt32) (mono_us () - g_boot_us); }
/* kit #9 device track: a SUSPENSION as the owner's Vita has it (kit #7 / #8 STEP 9: no SUSP line although the PS button and POWER were
 * pressed) -- the PROCESS clock stops while the app is suspended and the RTC (the wall clock) runs on. VLX_SUSPEND=<at s>:<for s>: at <at>
 * s of process time the RTC jumps <for> s ahead (the process clock does not move: no thread sees a gap); the clocks are KEPT, as the device's
 * STEP 9 log shows (kit #8.1 "play-gaot 2.txt": every P line after the PS button and POWER presses reads arm 444 bus 222 gpu 222, the 5 s
 * clock watch logged no change, CLK end 444/222/222/166 -- kit #9 review fix, finding 6: the first version lowered them by default).
 * VLX_SUSPEND_CLK=1 = a standby that drops them to the system's launch values 333/222/0/111 (NOT seen on the device: a test of kit #7's
 * CLK resume path only). Unset: no suspension (every RTC reading is the Mac's, as before). */
static void vlx_susp_poll (void)
{
	if (!g_susp_at_s || g_susp_fired || mono_us () - g_boot_us < (uint64_t) g_susp_at_s * 1000000u) return;
	if (!__sync_bool_compare_and_swap (&g_susp_fired, 0, 1)) return;
	g_rtc_off_us += (uint64_t) g_susp_dur_s * 1000000u;
	if (g_susp_clk) { g_clk [0] = 333; g_clk [1] = 222; g_clk [2] = 0; g_clk [3] = 111; }
	vlog ("[vlx] VLX_SUSPEND: suspended for %u s at %u s of process time -- the process clock stops (it does not move), the RTC jumps %u s ahead%s\n",
	      g_susp_dur_s, g_susp_at_s, g_susp_dur_s, g_susp_clk ? "; VLX_SUSPEND_CLK=1: the standby left the clocks at 333/222/0/111 (not seen on the device)" : "; the clocks kept (as the device's STEP 9)");
}
static uint64_t rtc_us (void) { vlx_susp_poll (); return real_us () + g_rtc_off_us; }
int sceKernelLibcGettimeofday (SceKernelTimeval *tv, SceKernelTimezone *tz)
{
	uint64_t us = rtc_us (); if (tv) { tv->sec = (SceInt32) (us / 1000000u); tv->usec = (SceInt32) (us % 1000000u); } if (tz) tz->value = 0; return 0;
}
SceUID sceKernelGetProcessId (void) { return 0x40010003; }
int sceKernelPowerTick (SceKernelPowerTickType type) { (void) type; return 0; }
/* clocks (every host sets 444/222/222/166 at start since 2026-10-13): nothing to set here; remembered, so a host's CLK lines read
 * back what it asked for. Launch values = what the PCH-2000 reports at launch (probe/physbench 2026-10-09: 333/222/0/111). */
/* (g_clk: defined above the time section -- kit #9's suspension model lowers it) */
/* kit #7 (track probes): VLX_PLUGIN_ARM=<s>:<MHz> models a plugin (PSVshell) that changes the ARM clock <s> seconds after the start, outside
 * the app (the reported clock changes; the app's watchdog must log it); VLX_LAUNCH_ARM=<MHz> (below, at boot) a plugin's launch clock */
static unsigned g_plugin_arm_s, g_plugin_arm_mhz;
int scePowerGetArmClockFrequency (void)
{
	vlx_susp_poll ();   /* kit #9 */
	if (g_plugin_arm_mhz && mono_us () - g_boot_us >= (uint64_t) g_plugin_arm_s * 1000000u) { g_clk [0] = (int) g_plugin_arm_mhz; g_plugin_arm_mhz = 0; }
	return g_clk [0];
}
int scePowerGetBusClockFrequency (void) { return g_clk [1]; }
int scePowerGetGpuClockFrequency (void) { return g_clk [2]; }
int scePowerGetGpuXbarClockFrequency (void) { return g_clk [3]; }
/* kit #6 toggles (Settings > Graphics > CPU Speed): above 444 MHz a stock kernel does not run the ARM faster; modelled here as refused
 * (SCE_POWER_ERROR_INVALID_VALUE, the clock unchanged) -- the host and engine only trust the clock READ BACK, so the device's own answer,
 * whatever it is, takes the same path. VLX_ARM500=1 models a plugin that lets 500 MHz through (accepted up to 500). */
static int g_arm500;
int scePowerSetArmClockFrequency (int freq) { if (freq > (g_arm500 ? 500 : 444)) return (int) 0x802B0000; g_clk [0] = freq; return 0; }
int scePowerSetBusClockFrequency (int freq) { g_clk [1] = freq; return 0; }
int scePowerSetGpuClockFrequency (int freq) { g_clk [2] = freq; return 0; }
int scePowerSetGpuXbarClockFrequency (int freq) { g_clk [3] = freq; return 0; }
int sceKernelExitProcess (int res) { if (g_disp_sets || g_disp_nulls) disp_line ("process exit"); prof_dump (); stack_report_all ("exit"); if (g_vmdom_opens || g_forvm_used) vlog ("[vlx] ForVM at exit: %u KiB in blocks, %u Opens, %u Syncs, %u FORVM RULE lines, domain %s\n", g_forvm_used / 1024, g_vmdom_opens, g_vmdom_syncs, g_vmdom_rule, g_vmdom_open ? "OPEN" : "closed"); vlog ("[vlx] sceKernelExitProcess(%d) after %u ms\n", res, (unsigned) ((mono_us () - g_boot_us) / 1000)); sys1 (NR_exit_group, res & 0xff); for (;;) ; }
int sceKernelGetRandomNumber (void *output, SceSize size)
{
	char *p = output; while (size) { long r = sys3 (NR_getrandom, p, size, 0); if (r <= 0) return fail ("sceKernelGetRandomNumber", SCE_KERNEL_ERROR_ERROR); p += r; size -= (SceSize) r; } return 0;
}
int sceRtcGetCurrentClock (SceDateTime *time, int time_zone) { tick_to_dt (rtc_us () + EPOCH_TICK + (int64_t) time_zone * 60000000, time); return 0; }
int sceRtcGetCurrentTick (SceRtcTick *tick) { if (!tick) return (int) 0x80251000; tick->tick = rtc_us () + EPOCH_TICK; return 0; }   /* kit #9: the P3 watch */
int sceRtcGetCurrentClockLocalTime (SceDateTime *time) { return sceRtcGetCurrentClock (time, 0); }   /* kit #8 play (save backups' names): the Mac's UTC as local time */
int sceRtcGetTick (const SceDateTime *time, SceRtcTick *tick) { if (!time || !tick) return (int) 0x80251000; tick->tick = dt_to_tick (time); return 0; }
int sceRtcSetTick (SceDateTime *time, const SceRtcTick *tick) { if (!time || !tick) return (int) 0x80251000; tick_to_dt (tick->tick, time); return 0; }
int sceRtcGetTime_t (const SceDateTime *time, time_t *piTime) { if (!time || !piTime) return (int) 0x80251000; *piTime = (time_t) ((int64_t) (dt_to_tick (time) - EPOCH_TICK) / 1000000); return 0; }
int sceRtcSetTime_t (SceDateTime *time, time_t iTime) { if (!time) return (int) 0x80251000; tick_to_dt (EPOCH_TICK + (uint64_t) ((int64_t) iTime * 1000000), time); return 0; }
int sceRtcConvertUtcToLocalTime (const SceRtcTick *utc, SceRtcTick *local_time) { if (!utc || !local_time) return (int) 0x80251000; local_time->tick = utc->tick; return 0; }

int sceClibPrintf (const char *fmt, ...)
{
	char buf [2048]; va_list ap; va_start (ap, fmt); int n = vvsnprintf (buf, sizeof (buf), fmt, ap); va_end (ap);
	if (n > (int) sizeof (buf) - 1) n = sizeof (buf) - 1;
	vwrite_all (2, buf, (size_t) n); return n;
}
void *sceClibMemcpy (void *dst, const void *src, SceSize len) { vmemcpy (dst, src, len); return dst; }
void *sceClibMemset (void *dst, int ch, SceSize len) { vmemset (dst, ch, len); return dst; }
void *sceClibMemmove (void *dst, const void *src, SceSize len)
{
	char *d = dst; const char *s = src;
	if (d < s) while (len--) *d++ = *s++; else { d += len; s += len; while (len--) *--d = *--s; }
	return dst;
}
/* kit #8 play (G3 frame pacing, host.c k8_pace): a 60 Hz vblank counter on the process clock and the wait for the n-th next vblank */
/* kit #8 play (test): VLX_MEASURED_ARM=<MHz> models the ARM clock the app's watchdog would MEASURE on a device (a PSVshellPlus profile:
 * ~500 while scePower reports 444); host.c's k8_clk_shown reads it through a weak reference. Unset / 0: not modelled (the host shows the
 * report, as on a device before its measurement settles). */
static int g_measured_arm;
int vlx_model_measured_mhz (void) { return g_measured_arm; }
int sceDisplayGetVcount (void) { return (int) (sceKernelGetProcessTimeWide () * 60u / 1000000u); }
int sceDisplayWaitVblankStartMulti (unsigned int vcount)
{
	if (!vcount) vcount = 1;
	uint64_t now = sceKernelGetProcessTimeWide (), next = ((now * 60u / 1000000u) + vcount) * 1000000u / 60u;
	if (next > now) sceKernelDelayThread ((SceUInt) (next - now));
	return 0;
}
/* kit #8 boot track: the display is recorded for the launcher's splash -- every frame buffer set (the decoder stand-in below fills each picture
 * with one 32-bit value, FNV-1a 32 of its access unit, so the first word of a buffer names the frame on it): the count, a running FNV of the
 * named frames in the order they reached the "screen", and a NULL set (the display blanked); one [vlx] display line at the first set, every
 * 60th, at a NULL set and at the process exit. sceDisplayWaitVblankStart waits for the next 1/59.94 s boundary since boot. */
static void disp_line (const char *why) { vlog ("[vlx] display (%s): %u frame buffers set, %u NULL sets, order hash %08x, last frame word %08x, at %u ms\n", why, g_disp_sets, g_disp_nulls, g_disp_hash, g_disp_last, (unsigned) ((mono_us () - g_boot_us) / 1000)); }
int sceDisplaySetFrameBuf (const SceDisplayFrameBuf *pParam, SceDisplaySetBufSync sync)
{
	(void) sync;
	if (!pParam || !pParam->base) { g_disp_nulls++; disp_line ("NULL set: blanked"); return 0; }
	uint32_t w = *(const volatile uint32_t *) pParam->base;
	g_disp_sets++; g_disp_last = w;
	for (int i = 0; i < 4; i++) { g_disp_hash ^= (w >> (8 * i)) & 0xff; g_disp_hash *= 16777619u; }
	if (g_disp_sets == 1 || g_disp_sets % 60 == 0) disp_line (g_disp_sets == 1 ? "first frame" : "running");
	return 0;
}
int sceDisplayWaitVblankStart (void)
{
	uint64_t t = mono_us () - g_boot_us, period = 16683, next = (t / period + 1) * period;
	struct lts ts = { 0, (long) ((next - t) * 1000u) }; sys2 (NR_nanosleep, &ts, 0); return 0;
}

/* kit #8 boot track: the launcher's clib needs (sceClibSnprintf / Vsnprintf / Strncmp / Strnlen: vlx's own printf) */
int sceClibVsnprintf (char *dst, SceSize max, const char *fmt, va_list ap) { return vvsnprintf (dst, max, fmt, ap); }
int sceClibSnprintf (char *dst, SceSize max, const char *fmt, ...) { va_list ap; va_start (ap, fmt); int n = vvsnprintf (dst, max, fmt, ap); va_end (ap); return n; }
int sceClibStrncmp (const char *a, const char *b, SceSize n) { return vstrncmp (a, b, n); }
SceSize sceClibStrnlen (const char *s, SceSize max) { SceSize n = 0; while (n < max && s [n]) n++; return n; }

/* kit #8 boot track: the hardware video decoder (SceAvcdec through SceVideodec) -- a stand-in that checks the calls' contracts (the module
 * loaded first, the library initialised, the work area at least the queried size, the picture buffer set) and "decodes" each access unit into
 * a picture filled with one value (FNV-1a 32 of the access unit's bytes, alpha 0xFF), so the display / texture records can tell which frame
 * they got. VLX_AVC_FAIL=<n>: the n-th decode (1-based) refuses (0x8062000D: an invalid stream), the decode error path. */
#include <psp2/videodec.h>
static int g_avc_lib; static unsigned g_avc_need, g_avc_decodes, g_avc_handles;
/* kit #9 device track: the library's own memory -- on the owner's PCH-2000 sceVideodecInitLibrary (960 x 544, 3 refs) takes 6,144 KiB of
 * PHYCONT (the kit #8.1 launcher's MEM start / MEM k8-released lines), and with every PHYCONT page gone to vitaGL (glrec_vlx.c models its
 * init taking them) it refuses 0x80024302 NO_FREE_PHYSICAL_PAGE, as the engine's real video was refused on the device. VLX_AVC_LIB_KIB=<KiB>
 * (default 6144; 0 = no memory modelled, kit #8's stand-in). */
static unsigned g_avc_lib_taken;
/* kit #9 review (finding 4): VLX_AVC_DEC_KIB=<KiB> -- PHYCONT taken by sceAvcdecCreateDecoder itself (default 0: the device's 6,144 KiB were
 * measured with the decoder created, so how they split between the library and the decoder is unknown; e.g. VLX_AVC_LIB_KIB=4096
 * VLX_AVC_DEC_KIB=2048 tests the split); refused 0x80024302 when it is not there, given back by sceAvcdecDeleteDecoder */
static unsigned g_avc_dec_kib, g_avc_dec_taken;
int sceVideodecInitLibrary (SceVideodecType codec, const SceVideodecQueryInitInfoHwAvcdec *init)
{
	if (!g_avc_mod) return fail ("sceVideodecInitLibrary (the AVCDEC sysmodule is not loaded)", (int) 0x8062080B);
	if (codec != SCE_VIDEODEC_TYPE_HW_AVCDEC || !init || init->size != sizeof (*init) || !init->horizontal || !init->vertical || (init->horizontal & 15) || (init->vertical & 15) || init->numOfStreams != 1)
		return fail ("sceVideodecInitLibrary(param)", (int) 0x80620802);
	if (g_avc_lib) return fail ("sceVideodecInitLibrary (already initialised)", (int) 0x80620801);
	unsigned need = g_avc_lib_kib * 1024u;
	klock ();
	if (need && g_used [2] + need > g_budget [2]) {
		unsigned freek = (g_budget [2] - g_used [2]) / 1024u;
		kunlock ();
		vlog ("[vlx] sceVideodecInitLibrary: NO_FREE_PHYSICAL_PAGE (0x80024302) -- the library needs %u KiB of PHYCONT, %u KiB free (VLX_AVC_LIB_KIB; the device's figure)\n", g_avc_lib_kib, freek);
		return (int) 0x80024302;
	}
	g_used [2] += need; g_avc_lib_taken = need;
	kunlock ();
	g_avc_lib = 1; return 0;
}
int sceVideodecTermLibrary (SceVideodecType codec)
{
	(void) codec; if (!g_avc_lib) return fail ("sceVideodecTermLibrary", (int) 0x8062080C);
	klock (); g_used [2] -= g_avc_lib_taken; g_avc_lib_taken = 0; kunlock ();
	g_avc_lib = 0; return 0;
}
/* kit #9 device track: the decoder's OUTPUT DELAY -- on the owner's Vita the first access unit returned no picture (kit #8.1: "frame 0: the
 * decoder gave no picture"); the hardware hands pictures back some access units after it got them. The stand-in keeps the last
 * VLX_AVC_DELAY pictures (kit #10: default 4, the device's; 0 = kit #8's stand-in: a picture per access unit) and returns the oldest one
 * with each later access unit, in order, written into the buffer of THAT call.
 * Kit #10 boot track: sceAvcdecDecodeStop REFUSES by default, as the device did in kit #9's run (0x80620002 SCE_AVCDEC_ERROR_INVALID_PARAM,
 * 0 pictures, the pictures still held: the launcher with 4 buffers offered, the engine with up to 12) -- VLX_AVC_STOP=all is kit #9's
 * stand-in (as many held pictures as the array has room for), one / one-only / drop / hang the review's variants; one-only = refused unless
 * the array has ONE element, then one picture per call (kit #10's stop1 reading of the refusal, a test only: the device never showed it).
 * VLX_AVC_IDR=bump: an access unit holding an IDR picture first makes every held picture due (H.264's bumping at an IDR with
 * no_output_of_prior_pics_flag 0): they come out one per call before the delay rule applies again (default: the plain fixed-delay pipe, where
 * an IDR is just another access unit) -- kit #10's drain feeds the clip's own access units again from its IDR, so both models must give
 * every picture. The decoded-picture word of every flush picture is its own access unit's, so a picture of the second copy shown by mistake
 * shows up in the display / texture records. Kit #9's defaults: VLX_AVC_DELAY=1 VLX_AVC_STOP=all. */
#define VLX_AVC_RING 32
static uint32_t g_avc_pend [VLX_AVC_RING]; static unsigned g_avc_pend_n, g_avc_pend_r, g_avc_drained;
static int g_avc_idr_bump; static unsigned g_avc_due;   /* kit #10: VLX_AVC_IDR=bump -- held pictures made due by an IDR */
/* kit #10 review fix (finding 5): every held picture keeps its access unit's time stamp and hands it back in SceAvcdecPicture.info.pts (as a
 * decoder does), so the host's second-copy check (k8_avc.h K10_AVC_TAG) can be tested; and the two decoder models the review named, in which
 * a refed IDR does NOT bring every held picture out: VLX_AVC_IDR=drop -- an IDR access unit DROPS every held picture; VLX_AVC_IDR=one -- an
 * IDR access unit hands back the oldest held picture and drops the rest (one picture array, several pictures due). With either, the flush
 * access units after the IDR bring out pictures of the clip's second copy: the host must not count or show them. */
static SceVideodecTimeStamp g_avc_pend_pts [VLX_AVC_RING];
static int g_avc_idr_drop;   /* 1 = drop, 2 = one */
/* kit #10 integration review: VLX_AVC_PTS=0 -- a decoder that hands back NO time stamps (SceAvcdecPicture.info.pts left as the caller set it):
 * the host's second-copy check must then say it checked nothing; VLX_AVC_IDR=hang -- an IDR access unit fed while pictures are held (the
 * refeed's first flush access unit) does not come back for 40 s, then returns no picture: the engine's bounded refeed (and the launcher's
 * 15 s release cap) are tested with it */
static int g_avc_pts = 1, g_avc_idr_hang;
static unsigned g_avc_idr_dropped;
static int avc_has_idr (const unsigned char *b, unsigned n)
{
	for (unsigned i = 0; i + 3 < n; i++)
		if (b [i] == 0 && b [i + 1] == 0 && b [i + 2] == 1 && (b [i + 3] & 0x1f) == 5) return 1;
	return 0;
}
static void avc_fill (SceAvcdecPicture *p, uint32_t h)
{
	uint32_t *px = (uint32_t *) p->frame.pPicture [0];
	for (unsigned y = 0; y < p->frame.frameHeight; y++) for (unsigned x = 0; x < p->frame.frameWidth; x++) px [y * p->frame.framePitch + x] = h;
}
static void avc_fill_held (SceAvcdecPicture *p)   /* the oldest held picture (and its time stamp) into p; it leaves the decoder */
{
	avc_fill (p, g_avc_pend [g_avc_pend_r]); if (g_avc_pts) p->info.pts = g_avc_pend_pts [g_avc_pend_r];
	g_avc_pend_r = (g_avc_pend_r + 1) % VLX_AVC_RING; g_avc_pend_n--;
}
int sceAvcdecQueryDecoderMemSize (SceVideodecType codec, const SceAvcdecQueryDecoderInfo *q, SceAvcdecDecoderInfo *di)
{
	if (!g_avc_lib || codec != SCE_VIDEODEC_TYPE_HW_AVCDEC || !q || !di) return fail ("sceAvcdecQueryDecoderMemSize", (int) 0x80620002);
	g_avc_need = q->horizontal * q->vertical * 3u / 2u * (q->numOfRefFrames + 2u) + 0x40000u;   /* (a plausible size: reference frames + slack) */
	di->frameMemSize = g_avc_need; return 0;
}
int sceAvcdecCreateDecoder (SceVideodecType codec, SceAvcdecCtrl *c, const SceAvcdecQueryDecoderInfo *q)
{
	if (!g_avc_lib || !c || !q || !c->frameBuf.pBuf || c->frameBuf.size < g_avc_need) return fail ("sceAvcdecCreateDecoder", (int) 0x80620002);
	(void) codec;
	if (g_avc_dec_kib) {   /* kit #9 review (finding 4) */
		unsigned need = g_avc_dec_kib * 1024u;
		klock ();
		if (g_used [2] + need > g_budget [2]) {
			unsigned freek = (g_budget [2] - g_used [2]) / 1024u;
			kunlock ();
			vlog ("[vlx] sceAvcdecCreateDecoder: NO_FREE_PHYSICAL_PAGE (0x80024302) -- the decoder needs %u KiB of PHYCONT, %u KiB free (VLX_AVC_DEC_KIB)\n", g_avc_dec_kib, freek);
			return (int) 0x80024302;
		}
		g_used [2] += need; g_avc_dec_taken += need;
		kunlock ();
	}
	c->handle = 0x5000u + ++g_avc_handles; g_avc_pend_n = g_avc_pend_r = 0; g_avc_due = 0; return 0;
}
int sceAvcdecDeleteDecoder (SceAvcdecCtrl *c)
{
	if (!c || !c->handle) return fail ("sceAvcdecDeleteDecoder", (int) 0x80620002);
	if (g_avc_dec_taken) { unsigned give = g_avc_dec_kib * 1024u <= g_avc_dec_taken ? g_avc_dec_kib * 1024u : g_avc_dec_taken; klock (); g_used [2] -= give; g_avc_dec_taken -= give; kunlock (); }
	c->handle = 0; g_avc_pend_n = g_avc_pend_r = 0; g_avc_due = 0; return 0;
}
int sceAvcdecDecode (const SceAvcdecCtrl *c, const SceAvcdecAu *au, SceAvcdecArrayPicture *arr)
{
	if (!c || !c->handle || !au || !au->es.pBuf || !au->es.size || !arr || arr->numOfElm < 1 || !arr->pPicture || !arr->pPicture [0]) return fail ("sceAvcdecDecode(param)", (int) 0x80620002);
	SceAvcdecPicture *p = arr->pPicture [0];
	if (p->size != sizeof (*p) || !p->frame.pPicture [0] || p->frame.pixelType != SCE_AVCDEC_PIXELFORMAT_RGBA8888 || p->frame.framePitch < p->frame.frameWidth) return fail ("sceAvcdecDecode(picture)", (int) 0x80620006);
	if (g_avc_fail_at && ++g_avc_decodes == g_avc_fail_at) { arr->numOfOutput = 0; return fail ("sceAvcdecDecode (VLX_AVC_FAIL)", (int) 0x8062000D); }
	uint32_t h = 2166136261u; const unsigned char *b = (const unsigned char *) au->es.pBuf;
	for (SceUInt32 i = 0; i < au->es.size; i++) { h ^= b [i]; h *= 16777619u; }
	h = (h & 0x00FFFFFFu) | 0xFF000000u;
	if (!g_avc_delay) { avc_fill (p, h); if (g_avc_pts) p->info.pts = au->pts; arr->numOfOutput = 1; return 0; }   /* kit #8's stand-in */
	if (g_avc_idr_hang && g_avc_pend_n && avc_has_idr (b, au->es.size)) {   /* kit #10 integration review: VLX_AVC_IDR=hang */
		vlog ("[vlx] sceAvcdecDecode (VLX_AVC_IDR=hang): an IDR access unit with %u picture(s) held -- the call hangs for 40 s\n", g_avc_pend_n);
		sceKernelDelayThread (40u * 1000000u);
		vlog ("[vlx] sceAvcdecDecode (VLX_AVC_IDR=hang): returns after 40 s, no picture\n");
		arr->numOfOutput = 0; return 0;
	}
	unsigned d = g_avc_delay < VLX_AVC_RING ? g_avc_delay : VLX_AVC_RING - 1;
	if (g_avc_idr_bump && avc_has_idr (b, au->es.size)) g_avc_due = g_avc_pend_n;   /* kit #10: every held picture is due before this IDR's */
	int one_out = 0;
	if (g_avc_idr_drop && g_avc_pend_n && avc_has_idr (b, au->es.size)) {   /* kit #10 review (finding 5): the decoder models that lose held pictures */
		if (g_avc_idr_drop == 2) { avc_fill_held (p); one_out = 1; }
		if (g_avc_idr_dropped++ < 3)
			vlog ("[vlx] sceAvcdecDecode (VLX_AVC_IDR=%s): an IDR access unit with %u picture(s) held -- %s%u LOST\n", g_avc_idr_drop == 2 ? "one" : "drop",
			      g_avc_pend_n + (unsigned) one_out, one_out ? "the oldest handed back, " : "", g_avc_pend_n);
		g_avc_pend_n = 0;
	}
	if (g_avc_pend_n >= VLX_AVC_RING) return fail ("sceAvcdecDecode (vlx: more held pictures than the stand-in's ring)", (int) 0x8062000A);
	g_avc_pend_pts [(g_avc_pend_r + g_avc_pend_n) % VLX_AVC_RING] = au->pts;
	g_avc_pend [(g_avc_pend_r + g_avc_pend_n++) % VLX_AVC_RING] = h;
	if (one_out) { arr->numOfOutput = 1; return 0; }
	if (g_avc_due || g_avc_pend_n > d) {
		avc_fill_held (p); arr->numOfOutput = 1;
		if (g_avc_due) g_avc_due--;
	} else arr->numOfOutput = 0;
	return 0;
}
/* (VLX_AVC_STOP=one: one held picture per call, as Vita3K's HLE has it; default all: as many as the array has elements -- the device's
 * behaviour is not documented, so the gates run both; kit #9 review (finding 7): VLX_AVC_STOP=drop -- one call empties the decoder: as
 * many pictures as the array has room for, the rest LOST (the strictest reading: the caller must offer a buffer for every held picture)) */
static int g_avc_stop_one, g_avc_stop_drop;
/* kit #9 review, round 2 (finding 1): VLX_AVC_STOP=hang -- the call does not return for 40 s, then returns no picture (touching none of the
 * decoder's state: the caller has walked away by then) -- the launcher's 15 s release cap and the engine's bounded wait are tested with it */
static int g_avc_stop_hang;
static int g_avc_stop_refuse = 1, g_avc_stop_one_only;   /* kit #10: the device's refusal is the default; one-only (a test) */
static unsigned g_avc_stop_refused;
int sceAvcdecDecodeStop (const SceAvcdecCtrl *c, SceAvcdecArrayPicture *arr)
{
	if (!c || !c->handle || !arr || arr->numOfElm < 1 || !arr->pPicture) return fail ("sceAvcdecDecodeStop(param)", (int) 0x80620002);
	if (g_avc_stop_refuse || (g_avc_stop_one_only && arr->numOfElm != 1)) {   /* kit #10: as the device (kit #9's run) */
		arr->numOfOutput = 0;
		if (++g_avc_stop_refused <= 3)
			vlog ("[vlx] sceAvcdecDecodeStop: refused 0x80620002 (INVALID_PARAM) with %u picture(s) held and %u offered -- as the device did in kit #9's run (%s)\n",
			      g_avc_pend_n, (unsigned) arr->numOfElm, g_avc_stop_refuse ? "the default; VLX_AVC_STOP=all = kit #9's stand-in" : "VLX_AVC_STOP=one-only: only a one-picture array is taken");
		return (int) 0x80620002;
	}
	if (g_avc_stop_hang) {
		vlog ("[vlx] sceAvcdecDecodeStop (VLX_AVC_STOP=hang): the call hangs for 40 s\n");
		sceKernelDelayThread (40u * 1000000u);
		vlog ("[vlx] sceAvcdecDecodeStop (VLX_AVC_STOP=hang): returns after 40 s, no picture\n");
		arr->numOfOutput = 0; return 0;
	}
	unsigned k = 0;
	for (; k < arr->numOfElm && g_avc_pend_n && !(g_avc_stop_one && k); k++) {
		SceAvcdecPicture *p = arr->pPicture [k];
		if (!p || p->size != sizeof (*p) || !p->frame.pPicture [0] || p->frame.pixelType != SCE_AVCDEC_PIXELFORMAT_RGBA8888 || p->frame.framePitch < p->frame.frameWidth) return fail ("sceAvcdecDecodeStop(picture)", (int) 0x80620006);
		avc_fill_held (p); g_avc_drained++;
	}
	if (g_avc_stop_drop && g_avc_pend_n) {
		vlog ("[vlx] sceAvcdecDecodeStop (VLX_AVC_STOP=drop): %u held picture(s) LOST -- the array had room for %u\n", g_avc_pend_n, (unsigned) arr->numOfElm);
		g_avc_pend_n = 0;
	}
	arr->numOfOutput = k; return 0;
}

/* kit #8 boot track: the module loader for a launcher build, whose vlx ELF links the launcher AND the engine module (their objects): the
 * module's module_start is called with the arguments the launcher passed, as the kernel would after loading app0:module/shepherd.suprx --
 * which must exist in the root (stage.sh copies the build's). VLX_MODULE_MS=<ms> models the load's time (default 0: vlx timing means
 * nothing); the relocations of a real load are NOT modelled; the import binding is (kit #8.1, below: the device showed it matters). */
#include <psp2/kernel/modulemgr.h>
extern int module_start (SceSize args, void *argp) __attribute__ ((weak));
static unsigned g_module_ms;
/* kit #8.1: the import binding of a module load, as the Vita does it -- every library the module imports must already be loaded, else
 * the load fails with 0x8002D003 (SCE_KERNEL_ERROR_MODULEMGR_NO_LIB: what both launcher apps got on the owner's Vita while vlx loaded
 * them). stage.sh puts the build's module-imports.txt (build.sh: the libraries the module ELF imports) and vita/shepherd/
 * module-load-libs.txt (the libraries loaded when the launcher loads the module) next to the module. VLX_MODULE_IMPORTS=0 = not modelled
 * (kit #8's vlx). */
static int g_module_imports = 1;
static int read_small (const char *path, char *buf, int cap)
{
	SceUID fd = sceIoOpen (path, SCE_O_RDONLY, 0);
	if (fd < 0) return -1;
	int n = sceIoRead (fd, buf, (SceSize) (cap - 1));
	sceIoClose (fd);
	if (n < 0) return -1;
	buf [n] = 0;
	return n;
}
static int line_in (const char *list, const char *name, int len)   /* name (len chars) is a whole non-comment line of list */
{
	for (const char *l = list; *l; ) {
		const char *e = l; while (*e && *e != '\n' && *e != '\r') e++;
		if (*l != '#' && e - l == len && !vstrncmp (l, name, (unsigned) len)) return 1;
		l = e; while (*l == '\n' || *l == '\r') l++;
	}
	return 0;
}
static int module_import_check (void)
{
	static char imp [4096], loaded [4096];
	if (read_small ("app0:module/shepherd-imports.txt", imp, sizeof imp) < 0 || read_small ("app0:module/load-libs.txt", loaded, sizeof loaded) < 0) {
		vlog ("[vlx] sceKernelLoadStartModule: import binding NOT modelled (no app0:module/shepherd-imports.txt / load-libs.txt staged)\n");
		return 0;
	}
	int missing = 0;
	for (const char *l = imp; *l; ) {
		const char *e = l; while (*e && *e != '\n' && *e != '\r') e++;
		if (e > l && *l != '#' && !line_in (loaded, l, (int) (e - l))) {
			vlog ("[vlx] sceKernelLoadStartModule: the module imports %.*s, which is not loaded yet -> 0x8002D003 (MODULEMGR_NO_LIB, as the Vita)\n", (int) (e - l), l);
			missing++;
		}
		l = e; while (*l == '\n' || *l == '\r') l++;
	}
	if (!missing) vlog ("[vlx] sceKernelLoadStartModule: import binding modelled: every library the module imports is loaded\n");
	return missing ? -1 : 0;
}
/* kit #8.1: k8_latelibs.c's lookups. vlx loads no libshacccg / libnet and has no other modules: every lookup is "not found" (the
 * trampolines then answer as vlx's no-network stand-ins; vlx's GL stand-in never calls the shader compiler) */
int taiGetModuleExportFunc (const char *modname, unsigned int libnid, unsigned int funcnid, unsigned int *func)
{
	(void) modname; (void) libnid; (void) funcnid; if (func) *func = 0;
	return (int) 0x90010002;
}
int sceKernelGetModuleList (SceUInt8 type, SceUID *uids, SceSize *num) { (void) type; (void) uids; if (num) *num = 0; return 0; }
int sceKernelGetModuleInfo (SceUID uid, SceKernelModuleInfo *info) { (void) uid; (void) info; return (int) 0x8002D011; }
SceUID sceKernelLoadStartModule (const char *path, SceSize args, void *argp, int flags, SceKernelLMOption *option, int *status)
{
	(void) flags; (void) option;
	char p [512]; struct lstatx x;
	if (!path || map_path (path, p, sizeof (p)) < 0 || sys5 (NR_statx, L_AT_FDCWD, p, 0, 0x7ff, &x) < 0) return fail ("sceKernelLoadStartModule(no file)", (int) 0x80010002);
	if (!module_start) return fail ("sceKernelLoadStartModule(no module linked)", (int) 0x8002D01C);
	if (g_module_imports && module_import_check () < 0) return fail ("sceKernelLoadStartModule(a library the module imports is not loaded)", (int) 0x8002D003);
	if (g_module_ms) { struct lts ts = { (long) (g_module_ms / 1000u), (long) (g_module_ms % 1000u) * 1000000L }; sys2 (NR_nanosleep, &ts, 0); }
	vlog ("[vlx] sceKernelLoadStartModule(%s, %u B file): module_start of the linked engine module (load modelled %u ms)\n", path, (unsigned) x.size, g_module_ms);
	int r = module_start (args, argp);
	if (status) *status = r;
	return 0x40030001;
}
int sceCtrlPeekBufferPositive (int port, SceCtrlData *pad_data, int count)
{
	(void) port; for (int i = 0; i < count; i++) { vmemset (&pad_data [i], 0, sizeof (pad_data [i])); pad_data [i].timeStamp = sceKernelGetProcessTimeWide ();
		pad_data [i].lx = pad_data [i].ly = pad_data [i].rx = pad_data [i].ry = 128; }
	return count;
}
/* kit #5 touch: the front panel (host.c shepherd_vita_touch_read). Without VLX_TOUCH: sampling accepted, a PCH-2000-like panel info
 * (report space 1920 x 1088), and every peek one sample with no finger -- the host's latch stays inert. VLX_TOUCH="R:tap:x,y;R:hold:x,y:n"
 * scripts the host's reads (R = the R-th sceTouchPeek of the front port, 1-based; the engine reads once per frame from frame 1, so R is
 * the frame; x,y in report space): tap = between reads R-1 and R a finger touched (x,y) and left -- the peek at R returns 8 samples with
 * timestamps spread over the time since the previous peek, the 3rd..5th with the finger, the newest without (the latch must report the
 * tap); hold = reads R..R+n-1 return the finger (id 1) in their one sample. */
#include <psp2/touch.h>
static char g_touch_script [512], g_touch_back_script [512];   /* kit #8 play: VLX_TOUCH_BACK = the same script for the REAR panel (its own read count) */
static unsigned g_touch_reads, g_touch_back_reads;
static uint64_t g_touch_last_us;
int sceTouchSetSamplingState (SceUInt32 port, SceTouchSamplingState state) { (void) state; return port <= 1 ? 0 : (int) 0x80350001; }
int sceTouchGetPanelInfo (SceUInt32 port, SceTouchPanelInfo *pi)
{
	if (!pi || port > 1) return (int) 0x80350001;
	vmemset (pi, 0, sizeof (*pi)); pi->maxAaX = pi->maxDispX = 1919; pi->maxAaY = pi->maxDispY = port ? 889 : 1087; pi->minForce = 1; pi->maxForce = 128;
	return 0;
}
static const char *touch_num (const char *p, unsigned *v) { *v = 0; while (*p >= '0' && *p <= '9') *v = *v * 10 + (unsigned) (*p++ - '0'); return p; }
/* the item for read r: 1 tap, 2 hold (x, y set), 0 none */
static int touch_script_at_in (const char *p, unsigned r, unsigned *x, unsigned *y);
static int touch_script_at (unsigned r, unsigned *x, unsigned *y) { return touch_script_at_in (g_touch_script, r, x, y); }
static int touch_script_at_in (const char *p, unsigned r, unsigned *x, unsigned *y)
{
	while (*p) {
		unsigned R, n = 1; int kind = 0;
		p = touch_num (p, &R);
		if (*p == ':') p++;
		if (!vstrncmp (p, "tap:", 4)) { kind = 1; p += 4; } else if (!vstrncmp (p, "hold:", 5)) { kind = 2; p += 5; }
		p = touch_num (p, x); if (*p == ',') p++; p = touch_num (p, y);
		if (*p == ':') p = touch_num (p + 1, &n);
		while (*p && *p != ';' && *p != ' ') p++;
		while (*p == ';' || *p == ' ') p++;
		if ((kind == 1 && r == R) || (kind == 2 && r >= R && r < R + n)) return kind;
	}
	return 0;
}
int sceTouchPeek (SceUInt32 port, SceTouchData *d, SceUInt32 nBufs)
{
	if (!d || nBufs < 1 || port > 1) return (int) 0x80350001;
	uint64_t now = sceKernelGetProcessTimeWide (), last = g_touch_last_us;
	unsigned r = port == 0 ? ++g_touch_reads : ++g_touch_back_reads, x = 0, y = 0;
	if (port == 0) g_touch_last_us = now;
	int kind = port == 0 && g_touch_script [0] ? touch_script_at (r, &x, &y) : port == 1 && g_touch_back_script [0] ? touch_script_at_in (g_touch_back_script, r, &x, &y) : 0;
	if (port == 1) { if (kind == 1) kind = 2; last = now; }   /* (the rear script: holds only -- a rear tap is a one-read hold) */
	if (kind == 1 && nBufs >= 8 && now > last + 8) {
		for (int i = 0; i < 8; i++) {
			vmemset (&d [i], 0, sizeof (d [i])); d [i].timeStamp = last + (now - last) * (uint64_t) (i + 1) / 8u;
			if (i >= 2 && i <= 4) { d [i].reportNum = 1; d [i].report [0].id = 1; d [i].report [0].force = 64; d [i].report [0].x = (SceInt16) x; d [i].report [0].y = (SceInt16) y; }
		}
		vlog ("[vlx] touch read %u: a scripted tap at (%u,%u) between two reads (8 samples)\n", r, x, y);
		return 8;
	}
	vmemset (&d [0], 0, sizeof (d [0])); d [0].timeStamp = now;
	if (kind == 2) { d [0].reportNum = 1; d [0].report [0].id = 1; d [0].report [0].force = 64; d [0].report [0].x = (SceInt16) x; d [0].report [0].y = (SceInt16) y; }
	return 1;
}
/* no network */
int sceNetSocketClose (int s) { (void) s; return (int) 0x80410109; }
int sceNetSend (int s, const void *msg, unsigned int len, int flags) { (void) s; (void) msg; (void) len; (void) flags; return (int) 0x80410109; }
int sceNetRecv (int s, void *buf, unsigned int len, int flags) { (void) s; (void) buf; (void) len; (void) flags; return (int) 0x80410109; }
int sceNetSetsockopt (int s, int level, int optname, const void *optval, unsigned int optlen) { (void) s; (void) level; (void) optname; (void) optval; (void) optlen; return (int) 0x80410109; }
int sceNetGetsockopt (int s, int level, int optname, void *optval, unsigned int *optlen) { (void) s; (void) level; (void) optname; (void) optval; (void) optlen; return (int) 0x80410109; }

/* ------------------------------------------------------------------ boot */
extern void _start (unsigned int args, void *argp);
extern void _init (void), _fini (void);
extern const SceSize sceUserMainThreadStackSize __attribute__ ((weak));
static int vlx_trap_entry (SceSize args, void *argp)
{
	(void) args; (void) argp;
	for (unsigned n = 1;; n++) {
		for (unsigned i = 0; i < g_trap_after_s; i++) { struct lts ts = { 1, 0 }; sys2 (NR_nanosleep, &ts, 0); }
		vlog ("[vlx] trap #%u after %u s (bkpt: a debugger on QEMU_GDB stops every thread here)\n", n, n * g_trap_after_s);
		asm volatile ("bkpt #1");
	}
	return 0;
}
static int vlx_main_entry (SceSize args, void *argp) { (void) args; (void) argp; _start (0, 0); return 0; }
static unsigned parse_kib (const char *s) { unsigned v = 0; while (*s >= '0' && *s <= '9') v = v * 10 + (unsigned) (*s++ - '0'); return v; }

void vlx_boot (uint32_t *sp0)
{
	int argc = (int) sp0 [0]; char **envp = (char **) (sp0 + 1 + argc + 1);
	char **e = envp; while (*e) e++;
	g_text_lo = (uint32_t) (uintptr_t) _init; g_text_hi = (uint32_t) (uintptr_t) _fini + 0x1000;
	for (char **v = envp; *v; v++) {
		if (!vstrncmp (*v, "VLX_ROOT=", 9)) vstrlcpy (g_root, *v + 9, sizeof (g_root));
		else if (!vstrncmp (*v, "VLX_TRACE=", 10)) g_trace = (*v) [10] == '1';
		else if (!vstrncmp (*v, "VLX_BREAK=", 10)) g_break = (*v) [10] == '1';
		else if (!vstrncmp (*v, "VLX_TRAP_AFTER_S=", 17)) g_trap_after_s = parse_kib (*v + 17);
		else if (!vstrncmp (*v, "VLX_POLL_MS=", 12)) g_poll_us = parse_kib (*v + 12) * 1000u;
		else if (!vstrncmp (*v, "VLX_CORE3=", 10)) g_core3_ok = (*v) [10] == '1';
		else if (!vstrncmp (*v, "VLX_ARM500=", 11)) g_arm500 = (*v) [11] == '1';
		else if (!vstrncmp (*v, "VLX_STACK_REPORT=", 17)) g_stack_report = (*v) [17] == '1';
		else if (!vstrncmp (*v, "VLX_STACK_ACCOUNT=", 18)) g_stack_account = (*v) [18] == '1';
		else if (!vstrncmp (*v, "VLX_TOUCH=", 10)) vstrlcpy (g_touch_script, *v + 10, sizeof (g_touch_script));
		else if (!vstrncmp (*v, "VLX_MEASURED_ARM=", 17)) g_measured_arm = (int) parse_kib (*v + 17);   /* kit #8 play */
		else if (!vstrncmp (*v, "VLX_TOUCH_BACK=", 15)) vstrlcpy (g_touch_back_script, *v + 15, sizeof (g_touch_back_script));   /* kit #8 play */
		else if (!vstrncmp (*v, "VLX_USER_KIB=", 13)) g_budget [0] = parse_kib (*v + 13) * 1024u;
		else if (!vstrncmp (*v, "VLX_CDRAM_KIB=", 14)) g_budget [1] = parse_kib (*v + 14) * 1024u;
		else if (!vstrncmp (*v, "VLX_PHYCONT_KIB=", 16)) g_budget [2] = parse_kib (*v + 16) * 1024u;
		else if (!vstrncmp (*v, "VLX_PROF=", 9)) g_prof_us = parse_kib (*v + 9);
		else if (!vstrncmp (*v, "VLX_FORVM_QUOTA_KIB=", 20)) g_forvm_quota = parse_kib (*v + 20) * 1024u;   /* kit #8 jit: the ForVM model */
		else if (!vstrncmp (*v, "VLX_FORVM_MAX_KIB=", 18)) g_forvm_max = parse_kib (*v + 18) * 1024u;
		else if (!vstrncmp (*v, "VLX_FORVM_NOPROTECT=", 20)) g_forvm_noprotect = (*v) [20] == '1';
		else if (!vstrncmp (*v, "VLX_STORES=", 11)) vstrlcpy (g_stores, *v + 11, sizeof (g_stores));   /* kit #7 */
		else if (!vstrncmp (*v, "VLX_LAUNCH_ARM=", 15)) g_clk [0] = (int) parse_kib (*v + 15);       /* kit #7: a plugin's launch clock */
		else if (!vstrncmp (*v, "VLX_MODEL=", 10)) {                                                 /* kit #8 boot */
			const char *q = *v + 10;
			g_model = !vstrncmp (q, "pch1000", 8) ? 1000 : !vstrncmp (q, "pch1100", 8) ? 1100 : !vstrncmp (q, "pstv", 5) ? 9000 : 2000;
			g_model_cd = g_model == 9000 ? 0x20000 : 0x10000;
		}
		else if (!vstrncmp (*v, "VLX_VSH_FAIL=", 13)) g_vsh_fail = (*v) [13] == '1';                   /* kit #8 boot */
		else if (!vstrncmp (*v, "VLX_AVC_FAIL=", 13)) g_avc_fail_at = parse_kib (*v + 13);              /* kit #8 boot */
		else if (!vstrncmp (*v, "VLX_AVC_DELAY=", 14)) g_avc_delay = parse_kib (*v + 14);              /* kit #9 device */
		else if (!vstrncmp (*v, "VLX_AVC_STOP=", 13)) { g_avc_stop_one_only = !vstrncmp (*v + 13, "one-only", 8); g_avc_stop_one = !vstrncmp (*v + 13, "one", 3); g_avc_stop_drop = !vstrncmp (*v + 13, "drop", 4); g_avc_stop_hang = !vstrncmp (*v + 13, "hang", 4); g_avc_stop_refuse = !vstrncmp (*v + 13, "refuse", 6); }   /* kit #9 device (+ review: drop; kit #10: refuse = the default, one-only) */
		else if (!vstrncmp (*v, "VLX_AVC_IDR=", 12)) { g_avc_idr_bump = !vstrncmp (*v + 12, "bump", 4); g_avc_idr_drop = !vstrncmp (*v + 12, "drop", 4) ? 1 : !vstrncmp (*v + 12, "one", 3) ? 2 : 0; g_avc_idr_hang = !vstrncmp (*v + 12, "hang", 4); }   /* kit #10 boot (+ review: drop / one; integration review: hang) */
		else if (!vstrncmp (*v, "VLX_AVC_PTS=", 12)) g_avc_pts = (*v) [12] != '0';   /* kit #10 integration review: 0 = a decoder that hands back no time stamps */
		else if (!vstrncmp (*v, "VLX_AVC_DEC_KIB=", 16)) g_avc_dec_kib = parse_kib (*v + 16);          /* kit #9 review */
		else if (!vstrncmp (*v, "VLX_AVC_LIB_KIB=", 16)) g_avc_lib_kib = parse_kib (*v + 16);          /* kit #9 device */
		else if (!vstrncmp (*v, "VLX_SUSPEND_CLK=", 16)) g_susp_clk = (*v) [16] == '1';                /* kit #9 device (review: default 0) */
		else if (!vstrncmp (*v, "VLX_SUSPEND=", 12)) {                                                /* kit #9 device: <at s>:<for s> */
			const char *a = *v + 12; g_susp_at_s = parse_kib (a);
			while (*a && *a != ':') a++;
			g_susp_dur_s = *a == ':' ? parse_kib (a + 1) : 0; if (!g_susp_dur_s) g_susp_at_s = 0;
		}
		else if (!vstrncmp (*v, "VLX_MODULE_MS=", 14)) g_module_ms = parse_kib (*v + 14);              /* kit #8 boot */
		else if (!vstrncmp (*v, "VLX_MODULE_IMPORTS=", 19)) g_module_imports = (*v) [19] != '0';         /* kit #8.1 */
		else if (!vstrncmp (*v, "VLX_CARD=", 9)) g_card_model = (*v) [9] == '1';                       /* kit #8 boot (timing only) */
		else if (!vstrncmp (*v, "VLX_STRICT_PATHS=", 17)) g_strict_paths = (*v) [17] == '1';           /* kit #8 boot review */
		else if (!vstrncmp (*v, "VLX_THREAD_FPSCR=", 17)) {                                           /* kit #10 fidelity: <hex>, the device's 0x03000000 */
			const char *q = *v + 17; unsigned x = 0;
			if (q [0] == '0' && (q [1] == 'x' || q [1] == 'X')) q += 2;
			for (;; q++) { int d = *q >= '0' && *q <= '9' ? *q - '0' : *q >= 'a' && *q <= 'f' ? *q - 'a' + 10 : *q >= 'A' && *q <= 'F' ? *q - 'A' + 10 : -1; if (d < 0) break; x = x * 16 + (unsigned) d; }
			g_thread_fpscr = x; g_thread_fpscr_on = 1;
		}
		else if (!vstrncmp (*v, "VLX_TLS_FOREIGN=", 16)) {                                            /* kit #9 runtime review: <slot>[,<n>] */
			const char *q = *v + 16; int x = 0;
			if (q [0] == '0' && (q [1] == 'x' || q [1] == 'X')) { q += 2; for (;; q++) { int d = *q >= '0' && *q <= '9' ? *q - '0' : *q >= 'a' && *q <= 'f' ? *q - 'a' + 10 : *q >= 'A' && *q <= 'F' ? *q - 'A' + 10 : -1; if (d < 0) break; x = x * 16 + d; } }
			else { x = (int) parse_kib (q); while (*q >= '0' && *q <= '9') q++; }
			if (x > 0 && x < 0x100) g_tls_foreign_slot = x;
			if (*q == ',') g_tls_foreign_after = parse_kib (q + 1);
		}
		else if (!vstrncmp (*v, "VLX_FW_REPORTED=", 16)) {                                            /* kit #8 boot: <x.yy> */
			const char *q = *v + 16;
			if (q [0] >= '0' && q [0] <= '9' && q [1] == '.' && q [2] >= '0' && q [2] <= '9' && q [3] >= '0' && q [3] <= '9')
				g_fw_reported = ((unsigned) (q [0] - '0') << 24) | ((unsigned) (q [2] - '0') << 20) | ((unsigned) (q [3] - '0') << 16) | 0x11u;
		}
		else if (!vstrncmp (*v, "VLX_IO_DELAY=", 13)) {                                              /* kit #7 integration review: <path part>:<ms> */
			const char *q = *v + 13; size_t l = 0; while (q [l] && q [l] != ':' && l + 1 < sizeof (g_io_delay_pat)) { g_io_delay_pat [l] = q [l]; l++; }
			g_io_delay_pat [l] = 0; while (q [l] && q [l] != ':') l++; if (q [l] == ':') g_io_delay_ms = parse_kib (q + l + 1);
		}
		else if (!vstrncmp (*v, "VLX_PLUGIN_ARM=", 15)) {                                             /* kit #7: <s>:<MHz> */
			const char *q = *v + 15; g_plugin_arm_s = parse_kib (q); while (*q && *q != ':') q++;
			if (*q == ':') g_plugin_arm_mhz = parse_kib (q + 1);
		}
	}
	g_boot_us = mono_us ();
	vthread *boot = &g_thr [0]; boot->o.type = T_THREAD; boot->o.uid = UID_THR (0); vstrlcpy (boot->o.name, "vlx-boot", sizeof (boot->o.name));
	boot->state = TS_RUNNING;
	sys1 (NR_set_tls, boot);
	thread_altstack (boot);
	install_crash_handlers ();
	unsigned mts = &sceUserMainThreadStackSize ? sceUserMainThreadStackSize : 256u * 1024u;
	vlog ("[vlx] boot: root=%s text=%08x-%08x main stack %u KiB, user budget %u KiB\n", g_root, g_text_lo, g_text_hi, mts / 1024, g_budget [0] / 1024);
	if (g_thread_fpscr_on)
		vlog ("[vlx] VLX_THREAD_FPSCR: every thread starts with FPSCR 0x%08x (the device's default is 0x03000000: FZ 1 DN 1)\n", g_thread_fpscr);
	if (g_tls_foreign_slot >= 0)
		vlog ("[vlx] VLX_TLS_FOREIGN: TLS slot 0x%x holds 0x5EC0F00D in every thread %s %u\n", g_tls_foreign_slot, g_tls_foreign_after ? "from its sceKernelGetTLSAddr call no." : "from its birth", g_tls_foreign_after);
	if (g_break) { vlog ("[vlx] VLX_BREAK=1: SIGTRAP so a debugger on QEMU_GDB can attach and set breakpoints\n"); asm volatile ("bkpt #0"); }
	if (g_trap_after_s) { SceUID w = sceKernelCreateThread ("vlx-trap", vlx_trap_entry, 0x10000100, 0x4000, 0, 0, 0); if (w >= 0) sceKernelStartThread (w, 0, 0); }
	prof_start ();
	SceUID m = sceKernelCreateThread ("main", vlx_main_entry, 0x10000100, mts, 0, 0, 0);
	g_stack_account_on = 1;
	if (m < 0 || sceKernelStartThread (m, 0, 0) < 0) { vlog ("[vlx] cannot start the main thread\n"); sys1 (NR_exit_group, 126); }
	int st = 0; sceKernelWaitThreadEndCB (m, &st, 0);
	stack_report_all ("main returned");
	vlog ("[vlx] main thread returned %d\n", st);
	prof_dump ();
	sys1 (NR_exit_group, st & 0xff);
}
