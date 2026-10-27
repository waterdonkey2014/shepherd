/* DIAG-ONLY (devfail1, 2026-10-08): the "Shepherd Menu Diag" (SHPD00016) part of vita/shepherd/host.c, which #includes this file only
 * when built with SHP_DIAG (vita/shepherd/build.sh SHEPHERD_DIAG=1). The normal menu builds never see it.
 *
 *  - a heartbeat thread (a plain native thread, never attached to Mono; no signals, no runtime calls that need attachment or locks):
 *    every 5 s one "HB" line pair with cheap progress counters -- the engine's boot phase and frame, managed->native calls (P/Invoke
 *    and AOT direct icall counts + the last name, from the ld --wrap thunks of gen_diag_wraps.py), files opened / bytes read and
 *    written by managed code, assemblies loaded + the last one, the preload hook's nesting, threads created, GC counts (the host's
 *    own gc_event profiler counters), the Mono thread's kernel state and CPU time, free memory. If NOTHING moved for 30 s: a "STALL"
 *    line and the kernel state of every thread the app created (WAITING + no CPU time = a deadlock; RUNNING + CPU time = a loop).
 *  - boot-phase markers: the engine (engine-diag.patch, Diag/BootPhase.cs) writes each phase into g_dg_phase BEFORE it starts
 *    (plain stores; the heartbeat checks it every second) and then calls shepherd_vita_diag_phase (int-only P/Invoke) -> a PHASE line.
 *  - the preload hook's nesting (the 2026-10-07 hang was a same-thread re-entry) and an assembly load hook.
 *  - mode: a 4 s window at start: L = REPRO (the 2026-10-07 settings: a plain non-recursive load_lock + EventSource left on, to see
 *    the old hang with these counters; log menu-diag-repro.txt), nothing = NORMAL (the fixes; menu-diag.txt).
 *    SHEPHERD_DIAG_MODE=normal|repro in env_diag.txt does the same.
 */
#include "shp_diag.h"

volatile unsigned shp_diag_pinv_calls, shp_diag_icall_calls;
volatile int shp_diag_pinv_last = -1, shp_diag_icall_last = -1;
volatile unsigned shp_diag_files_opened, shp_diag_open_failed, shp_diag_reads, shp_diag_writes;
volatile unsigned long long shp_diag_bytes_read, shp_diag_bytes_written;
char shp_diag_last_path [96];
volatile unsigned shp_diag_threads_created;
char shp_diag_last_thread [32];
volatile int shp_diag_thread_uids [SHP_DIAG_MAX_THREADS];

static int g_dg_mode;                               /* 0 normal, 1 repro */
static volatile int g_dg_preload_depth, g_dg_preload_max_depth, g_dg_preload_waiting, g_dg_preload_same_thread;
static volatile SceUID g_dg_preload_tid [4];
static char g_dg_preload_name [4][64];              /* [d-1] = the assembly the hook at nesting depth d is loading */
static volatile unsigned g_dg_asm_loaded, g_dg_preload_calls;
static char g_dg_last_asm [64];
static volatile SceUID g_dg_mono_tid;
static struct { volatile int seq, frame, len; char text [244]; } g_dg_phase __attribute__ ((aligned (8)));
static volatile int g_dg_phase_logged;

static void dg_copy (char *d, size_t n, const char *s)
{
	size_t i = 0;
	for (; s && s [i] && i + 1 < n; ++i)
		d [i] = s [i];
	d [i] = 0;
}

/* Diag/BootPhase.cs (int-only P/Invoke, GC-safe): the phase text is already in g_dg_phase */
void shepherd_vita_diag_phase (int seq)
{
	char t [sizeof (g_dg_phase.text)];
	dg_copy (t, sizeof (t), g_dg_phase.text);
	g_dg_phase_logged = seq;
	LOG ("PHASE #%d %s (frame %d, wall %.1f s)", seq, t, g_dg_phase.frame, wall_s ());
}

static void dg_assembly_loaded (MonoAssembly *a, void *user_data)
{
	MonoAssemblyName *n = a ? mono_assembly_get_name (a) : NULL;
	dg_copy (g_dg_last_asm, sizeof (g_dg_last_asm), n ? mono_assembly_name_get_name (n) : "?");
	g_dg_asm_loaded++;
}

/* the preload hook, wrapped: nesting depth, which thread, what each level loads, whether it is waiting for load_lock */
static MonoAssembly *preload_hook_impl (MonoAssemblyName *aname, char **assemblies_path, void *user_data);
static MonoAssembly *preload_hook (MonoAssemblyName *aname, char **assemblies_path, void *user_data)
{
	int d = ++g_dg_preload_depth;
	SceUID me = sceKernelGetThreadId ();
	g_dg_preload_calls++;
	if (d > g_dg_preload_max_depth)
		g_dg_preload_max_depth = d;
	if (d >= 1 && d <= 4) {
		g_dg_preload_tid [d - 1] = me;
		dg_copy (g_dg_preload_name [d - 1], sizeof (g_dg_preload_name [0]), mono_assembly_name_get_name (aname));
	}
	g_dg_preload_same_thread = d >= 2 && d <= 4 && g_dg_preload_tid [d - 2] == me;
	MonoAssembly *a = preload_hook_impl (aname, assemblies_path, user_data);
	g_dg_preload_depth--;
	return a;
}

static const char *dg_status (unsigned st)
{
	return st & SCE_THREAD_RUNNING ? "RUNNING" : st & SCE_THREAD_READY ? "READY" : st & SCE_THREAD_WAITING ? "WAITING" :
	       st & SCE_THREAD_SUSPENDED ? "SUSPENDED" : st & SCE_THREAD_DORMANT ? "DORMANT" : st & SCE_THREAD_STANDBY ? "STANDBY" :
	       st & (SCE_THREAD_DEAD | SCE_THREAD_DELETED) ? "DEAD" : "?";
}

/* one kernel thread: status, what it waits on, its CPU time (and the change since the previous call, if prev) */
static void dg_thread_line (const char *tag, SceUID uid, unsigned long long *prev)
{
	SceKernelThreadInfo ti;
	memset (&ti, 0, sizeof (ti));
	ti.size = sizeof (ti);
	if (sceKernelGetThreadInfo (uid, &ti) < 0) {
		LOG ("%s thread 0x%x: gone", tag, (unsigned) uid);
		return;
	}
	unsigned long long run = (unsigned long long) ti.runClocks;
	LOG ("%s thread 0x%x '%s' %s wait type 0x%x on 0x%x | prio %d cpu %d | cpu time %.2f s%s", tag, (unsigned) uid, ti.name, dg_status (ti.status),
	     (unsigned) ti.waitType, (unsigned) ti.waitId, ti.currentPriority, ti.currentCpuId, run / 1e6, prev && *prev && run == *prev ? " (no CPU since the last beat)" : "");
	if (prev)
		*prev = run;
}

static const char *dg_name (const char *const *tab, int count, int idx)
{
	return idx >= 0 && idx < count ? tab [idx] : "-";
}

static void *dg_heartbeat (void *arg)
{
	int old;
	pthread_setcancelstate (PTHREAD_CANCEL_DISABLE, &old);
	unsigned long long last_sig = 0, mono_run = 0, last_mono_run = 0;
	int beat = 0, still_s = 0, seen_seq = 0;
	unsigned last_pinv = 0, last_icall = 0;
	for (;;) {
		sceKernelDelayThread (1000 * 1000);
		beat++;
		int seq = g_dg_phase.seq;
		if (seq && seq != seen_seq && seq != g_dg_phase_logged) {
			char t [sizeof (g_dg_phase.text)];
			dg_copy (t, sizeof (t), g_dg_phase.text);
			LOG ("PHASE #%d %s (seen by the heartbeat: the engine has not reported it itself yet)", seq, t);
		}
		seen_seq = seq;
		if (beat % 5)
			continue;
		unsigned pinv = shp_diag_pinv_calls, icall = shp_diag_icall_calls;
		unsigned long long sig = pinv + 7ull * icall + 13ull * shp_diag_reads + 17ull * shp_diag_writes + 19ull * g_dg_asm_loaded + 23ull * seq +
		                         29ull * g_dg_phase.frame + 31ull * (gcs.count [0] + gcs.count [1]) + 37ull * g_console_lines + 41ull * shp_diag_threads_created;
		still_s = sig == last_sig ? still_s + 5 : 0;
		last_sig = sig;
		char pre [320] = "";
		int pd = g_dg_preload_depth;
		if (pd > 0)
			snprintf (pre, sizeof (pre), " | preload hook depth %d%s: %s%s%s%s", pd, g_dg_preload_same_thread ? " (SAME thread re-entered)" : "",
			          g_dg_preload_name [0], pd >= 2 ? " -> " : "", pd >= 2 ? g_dg_preload_name [1] : "", g_dg_preload_waiting ? " (waiting for load_lock)" : "");
		SceKernelThreadInfo ti;
		memset (&ti, 0, sizeof (ti));
		ti.size = sizeof (ti);
		int mono_ok = g_dg_mono_tid > 0 && sceKernelGetThreadInfo (g_dg_mono_tid, &ti) >= 0;
		mono_run = mono_ok ? (unsigned long long) ti.runClocks : 0;
		SceKernelFreeMemorySizeInfo fi;
		memset (&fi, 0, sizeof (fi));
		fi.size = sizeof (fi);
		sceKernelGetFreeMemorySize (&fi);
		MonoVitaArenaStats as;
		int arena_ok = mono_vita_vm_arena_try_get_stats (&as) == 0;
		LOG ("HB t=%.0fs | phase #%d '%.70s' frame %d | P/Invoke %u (+%u) last %s | icall %u (+%u) last %s | GCs %u minor %u major",
		     wall_s (), seq, g_dg_phase.text, g_dg_phase.frame, pinv, pinv - last_pinv, dg_name (shp_diag_pinv_names, shp_diag_pinv_count, shp_diag_pinv_last),
		     icall, icall - last_icall, dg_name (shp_diag_icall_names, shp_diag_icall_count, shp_diag_icall_last), gcs.count [0], gcs.count [1]);
		LOG ("HB   files %u opened (%u failed), last '%s' | read %llu KiB in %u calls, wrote %llu KiB | assemblies %u loaded, last %s (preload calls %u, max depth %d)%s | threads %u, last '%s' | mono thread %s, cpu +%.2f s | free user %u KiB, arena %s%u KiB",
		     shp_diag_files_opened, shp_diag_open_failed, shp_diag_last_path, shp_diag_bytes_read / 1024, shp_diag_reads, shp_diag_bytes_written / 1024,
		     g_dg_asm_loaded, g_dg_last_asm, g_dg_preload_calls, g_dg_preload_max_depth, pre, shp_diag_threads_created, shp_diag_last_thread,
		     mono_ok ? dg_status (ti.status) : "?", last_mono_run ? (mono_run - last_mono_run) / 1e6 : 0.0, fi.size_user / 1024,
		     arena_ok ? "used " : "busy, last ", arena_ok ? (unsigned) (as.used / 1024) : 0u);
		last_mono_run = mono_run;
		last_pinv = pinv;
		last_icall = icall;
		if (still_s == 30 || (still_s > 30 && still_s % 120 == 30)) {
			LOG ("STALL nothing moved for %d s (no call, file, assembly, phase, frame, GC, console line or thread). Kernel state of every thread:", still_s);
			if (g_dg_mono_tid > 0)
				dg_thread_line ("STALL   [mono main]", g_dg_mono_tid, NULL);
			unsigned n = shp_diag_threads_created < SHP_DIAG_MAX_THREADS ? shp_diag_threads_created : SHP_DIAG_MAX_THREADS;
			for (unsigned i = 0; i < n; ++i)
				if (shp_diag_thread_uids [i] > 0 && shp_diag_thread_uids [i] != g_dg_mono_tid)
					dg_thread_line ("STALL  ", shp_diag_thread_uids [i], NULL);
			LOG ("STALL (WAITING with no CPU time on every thread = a deadlock; a thread RUNNING with growing CPU time = a loop. The app keeps going: close it with the PS button when you have seen this)");
		}
	}
	return NULL;
}

static void dg_start_heartbeat (void)
{
	pthread_t t;
	pthread_attr_t a;
	pthread_attr_init (&a);
	pthread_attr_setstacksize (&a, 64 * 1024);
	int rc = pthread_create (&t, &a, dg_heartbeat, NULL);
	pthread_attr_destroy (&a);
	LOG ("D0 heartbeat thread %s (every 5 s: HB lines; STALL after 30 s without progress)", rc == 0 ? "started" : "could NOT be started");
}

static const char *g_dg_mode_how = "";

/* before the log exists (the mode picks the log file): SHEPHERD_DIAG_MODE in env_diag.txt, else a 4 s window on the screen */
static void dg_choose_mode_early (void)
{
	void *data;
	unsigned size;
	if (read_file (DIR_ "/" SHP_ENV_NAME, &data, &size) == 0) {
		char *m = strstr ((char *) data, "SHEPHERD_DIAG_MODE=");
		if (m) {
			g_dg_mode = !strncmp (m + 19, "repro", 5) ? 1 : 0;
			g_dg_mode_how = " (SHEPHERD_DIAG_MODE in " SHP_ENV_NAME ")";
		}
		free (data);
	}
	if (!*g_dg_mode_how) {
		if (g_screen)
			psvDebugScreenPuts ("Shepherd Menu Diag: press L now = REPRO (the 2026-10-07 freeze, with counters), nothing = NORMAL (4 s)\n");
		SceCtrlData pad;
		for (int i = 0; i < 80 && !g_dg_mode; ++i) {
			memset (&pad, 0, sizeof (pad));
			sceCtrlPeekBufferPositive (0, &pad, 1);
			if (pad.buttons & (SCE_CTRL_LTRIGGER | SCE_CTRL_L1))
				g_dg_mode = 1;
			else
				sceKernelDelayThread (50 * 1000);
		}
		g_dg_mode_how = g_dg_mode ? " (L held)" : " (no button)";
	}
	if (g_dg_mode == 1)
		snprintf (g_log_path, sizeof (g_log_path), "%.*s-repro.txt", (int) (strlen (LOG_PATH) - 4), LOG_PATH);
}

/* after the log exists: say which mode, and in repro mode make load_lock a plain mutex again */
static void dg_apply_mode (void)
{
	if (g_dg_mode == 1) {
		pthread_mutex_init (&load_lock, NULL);   /* a NORMAL (non-recursive) mutex again, as on 2026-10-07 */
		LOG ("D0 mode REPRO%s -> log %s: plain load_lock + EventSource left on. Expected: the PHASE line for MonoInterpCompat.Install, then STALL lines (the old freeze). Close the app with the PS button after the first STALL block", g_dg_mode_how, g_log_path);
	} else
		LOG ("D0 mode NORMAL%s -> log %s: recursive load_lock, EventSource off, corrected sem_post (the fixes)", g_dg_mode_how, g_log_path);
}

static void dg_engine_env (void)
{
	char b [48];
	snprintf (b, sizeof (b), "%lx:%u", (unsigned long) (uintptr_t) &g_dg_phase, (unsigned) sizeof (g_dg_phase));
	setenv ("SHEPHERD_DIAG_PHASE_BUF", b, 1);
}
