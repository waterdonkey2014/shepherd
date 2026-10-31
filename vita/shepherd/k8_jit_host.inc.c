/* k8_jit_host.inc.c: SHEPHERD-VITA device kit #8, track jit (vita/notes/kit8-jit.md) -- the host side of the baseline JIT tier.
 * Included by host.c ONLY in a JIT app (build.sh SHEPHERD_JIT_APP=1 -> -DSHEPHERD_JIT; the app links the kit #8 JIT runtime,
 * vita/toolchain/build-runtime-k8.sh jit). Every other app compiles none of this.
 *
 * SHEPHERD_JIT (env file; the JIT app's default is 1): 0 / off = no code block, no entry points: the kit #7 interpreter + AOT path (the
 * runtime then inserts nothing, every method's IR is kit #7's); 1 / on = the tier; lockstep = the tier checked against the interpreter on
 * every trace run (vlx / test runs). SHEPHERD_JIT_MB (1-16, default SHP_JIT_MB): the code block. SHEPHERD_JIT_PROBE=0 skips the
 * largest-block probe.
 *
 * MEMORY. The code block is HENkaku's ForVM memory (sceKernelAllocMemBlockForVM), which costs USER memory like any block (the owner's
 * console: 333,824 -> 329,728 KiB for 4 MiB). It is allocated here, BEFORE the partition reads the free memory, so the Mono arena is
 * smaller by exactly the block (the H2 line shows it) and nothing else -- stacks, safety, vitaGL's pool -- loses a byte.
 * THE CONSOLE'S RULES (devicelogs/20261021-kit7-owner-extras/jitprobe.txt): a second 16 MiB block was refused after a 4 MiB one (the
 * per-process VM quota, 0x80024B0B). The code block is allocated FIRST (review fix: the console never ran a probe-then-allocate order --
 * if the quota counted allocations rather than live blocks, a 16 MiB probe before it would cost the JIT; if a freed block came back late,
 * the arena would shrink unseen); then the largest ADDITIONAL single block is measured (allocated and freed at once, largest first) and
 * logged with the free user memory before and after it (a WARN line when the probe's memory did not all come back);
 * the thread that Opens the VM domain must do the writes and Open is not nestable -- the runtime's one compiler thread owns Open / write /
 * Sync / Close (this host never calls them); direct BL from the block to the runtime was out of reach (~38 MiB) -- the stencils call
 * through registers (MOVW/MOVT + BLX), so the block may be anywhere. */
#include <psp2/kernel/sysmem.h>
extern int sceKernelFreeMemBlockForVM (SceUID uid);   /* in libSceSysmem_stub.a, not in the vitasdk header (as vita/jit/probe) */
extern void mono_shepjit_set_block (int uid, void *base, unsigned size) __attribute__ ((weak));
extern void mono_shepjit_set_pin (void (*pin) (const char *who)) __attribute__ ((weak));
extern int mono_shepjit_report (char *buf, int len) __attribute__ ((weak));
extern void mono_shepjit_dump (void) __attribute__ ((weak));
int shepherd_vita_pin (int mask, const char *who);
int shepherd_vita_worker_mask (void);
#ifndef SHP_JIT_MB
#define SHP_JIT_MB 2u
#endif

static SceUID g_jit_block = -1;
static unsigned g_jit_mb;

/* the compiler thread is a latency-tolerant worker: the 4th core when the C3 probe found it open, else USER_2 (and the kit #6 4th-core
 * row may move it with the others). An app that does not pin its workers (no C3 probe: the single-thread play app 00097) still offers
 * the 4th core to the compiler alone -- the owner's core-unlock plugin opens it -- unless SHEPHERD_CORE3=0 or the owner's saved
 * Settings > Graphics > 4th CPU Core choice is Off (vita-settings.txt, the file the engine's row writes; not read with
 * SHEPHERD_K6_TOGGLES=0, as the engine), and only after the kit's own run check (review fix: a throwaway thread asks for CPU3, reads the
 * mask back and must run there again within 2 s, as core3_probe does; before, an accepted mask alone was enough). Otherwise USER_2. The
 * C3 jit-compiler line says which and why; g_core3 is not touched, so the Settings rows and every other thread behave exactly as before
 * (in such an app the 4th CPU Core row stays greyed out: the engine never asks the host to move threads there). */
static volatile int g_k8_c3_rc, g_k8_c3_back, g_k8_c3_ran;
static void *k8_c3_check_thread (void *arg)
{
	(void) arg;
	SceUID me = sceKernelGetThreadId ();
	g_k8_c3_rc = sceKernelChangeThreadCpuAffinityMask (me, SHP_CORE3_MASK);
	g_k8_c3_back = sceKernelGetThreadCpuAffinityMask (me);
	sceKernelDelayThread (1000);   /* off the CPU and back: with the new mask it can only come back on CPU3 */
	g_k8_c3_ran = 1;
	return NULL;
}
/* the owner's saved choice: 1 = core3=off in vita-settings.txt (parsed as Runtime/VitaToggles.cs does: key=value lines, # comments) */
static int k8_settings_core3_off (void)
{
	const char *tg = getenv ("SHEPHERD_K6_TOGGLES");
	if (tg && !strcmp (tg, "0"))
		return 0;
	SceUID fd = sceIoOpen (DIR_ "/vita-settings.txt", SCE_O_RDONLY, 0);
	if (fd < 0)
		return 0;
	char buf [1024];
	int n = sceIoRead (fd, buf, sizeof buf - 1);
	sceIoClose (fd);
	if (n <= 0)
		return 0;
	buf [n] = 0;
	int off = 0;
	for (char *line = buf; line && *line; ) {
		char *nl = strchr (line, '\n');
		if (nl) *nl = 0;
		while (*line == ' ' || *line == '\t') line++;
		size_t len = strlen (line);
		while (len && (line [len - 1] == '\r' || line [len - 1] == ' ' || line [len - 1] == '\t')) line [--len] = 0;
		char *eq = strchr (line, '=');
		if (*line && *line != '#' && eq) {
			char *k = line, *v = eq + 1, *ke = eq;
			while (ke > k && (ke [-1] == ' ' || ke [-1] == '\t')) ke--;
			*ke = 0;
			while (*v == ' ' || *v == '\t') v++;
			if (!strcmp (k, "core3") && (!strcmp (v, "on") || !strcmp (v, "off")))
				off = !strcmp (v, "off");   /* (the last one wins, as in the engine) */
		}
		line = nl ? nl + 1 : NULL;
	}
	return off;
}
static void k8_jit_pin (const char *who)
{
	if (g_core3 >= 0) {   /* an app with the C3 probe: the workers' mask (CPU3 only when the probe passed and the row is On) */
		shepherd_vita_pin (shepherd_vita_worker_mask (), who);
		return;
	}
	const char *e = getenv ("SHEPHERD_CORE3");
	const char *why;
	if (e && !strcmp (e, "0"))
		why = "SHEPHERD_CORE3=0";
	else if (k8_settings_core3_off ())
		why = "Settings > Graphics > 4th CPU Core is Off (vita-settings.txt)";
	else {
		pthread_t th;
		pthread_attr_t a;
		pthread_attr_init (&a);
		pthread_attr_setstacksize (&a, 16 * 1024);
		pthread_attr_setdetachstate (&a, PTHREAD_CREATE_DETACHED);
		int cr = pthread_create (&th, &a, k8_c3_check_thread, NULL);
		pthread_attr_destroy (&a);
		int waited = 0;
		while (cr == 0 && !g_k8_c3_ran && waited < 2000) {
			sceKernelDelayThread (1000);
			waited++;
		}
		int ok = cr == 0 && g_k8_c3_rc >= 0 && g_k8_c3_back == SHP_CORE3_MASK && g_k8_c3_ran;
		LOG ("C3 jit-compiler: no C3 probe in this app (core pinning off), so the 4th core (CPU3, mask 0x%x) is checked for the JIT compiler alone: rc 0x%08x, read back 0x%x, %s",
		     SHP_CORE3_MASK, (unsigned) g_k8_c3_rc, (unsigned) g_k8_c3_back,
		     cr ? "check thread not created" : g_k8_c3_rc < 0 ? "the kernel refused the mask" : g_k8_c3_ran ? "the check thread ran on it" : "the check thread did not run again within 2 s");
		if (ok) {
			SceUID me = sceKernelGetThreadId ();
			if (shepherd_vita_pin (SHP_CORE3_MASK, who) == 0 && sceKernelGetThreadCpuAffinityMask (me) == SHP_CORE3_MASK) {
				LOG ("C3 jit-compiler -> CPU3 (the check passed; SHEPHERD_CORE3=0 or the 4th CPU Core row Off keeps it on USER_2)");
				return;
			}
			why = "the compiler's own CPU3 mask was refused or read back differently";
		} else
			why = "the 4th core did not pass the check (no core-unlock plugin, or the thread did not run there)";
	}
	LOG ("C3 jit-compiler -> USER_2 (%s)", why);
	shepherd_vita_pin (SHP_USER2_MASK, who);
}

static unsigned k8_free_user_kib (void)
{
	SceKernelFreeMemorySizeInfo fi;
	memset (&fi, 0, sizeof (fi));
	fi.size = sizeof (fi);
	sceKernelGetFreeMemorySize (&fi);
	return (unsigned) (fi.size_user / 1024);
}

static void k8_jit_setup (void)
{
	setenv ("SHEPHERD_JIT", "1", 0);   /* the JIT app's default; the env file wins */
	const char *j = getenv ("SHEPHERD_JIT");
	if (!(j && (!strcmp (j, "1") || !strcmp (j, "on") || !strcmp (j, "lockstep")))) {
		LOG ("J1 baseline JIT: OFF (SHEPHERD_JIT=%s): no code block, no entry points -- the kit #7 interpreter + AOT path", j ? j : "unset");
		return;
	}
	if (!&mono_shepjit_set_block) {
		LOG ("WARN J1 baseline JIT: this eboot's runtime has no JIT tier: SHEPHERD_JIT=%s ignored (the interpreter + AOT path)", j);
		return;
	}
	unsigned mb = SHP_JIT_MB;
	const char *m = getenv ("SHEPHERD_JIT_MB");
	if (m && atoi (m) >= 1 && atoi (m) <= 16)
		mb = (unsigned) atoi (m);
	/* the code block FIRST (review fix: before, a largest-block probe of up to 16 MiB ran before it -- an order the console never ran) */
	unsigned free0 = k8_free_user_kib ();
	SceUID b = sceKernelAllocMemBlockForVM ("shepherd-jit-code", mb * MiB);
	void *base = NULL;
	if (b < 0 || sceKernelGetMemBlockBase (b, &base) < 0 || !base) {
		LOG ("WARN J1 baseline JIT: the %u MiB code block was refused (0x%08x; free user memory %u KiB): the JIT stays off (the interpreter + AOT path); SHEPHERD_JIT_MB in " SHP_ENV_NAME " sets a smaller one",
		     mb, (unsigned) b, free0);
		if (b >= 0)
			sceKernelFreeMemBlockForVM (b);
		return;
	}
	unsigned free1 = k8_free_user_kib ();
	g_jit_block = b;
	g_jit_mb = mb;
	mono_shepjit_set_block (b, base, mb * MiB);
	mono_shepjit_set_pin (k8_jit_pin);
	LOG ("J1 baseline JIT %s (SHEPHERD_JIT=%s): code block %u MiB of ForVM at %p (SceUID 0x%x; free user memory %u -> %u KiB), taken from user memory BEFORE the partition (the arena below is %u MiB smaller); "
	     "the compiler thread (a latency-tolerant worker) owns Open / write / Sync / Close and publishes in batches",
	     !strcmp (j, "lockstep") ? "ON, LOCKSTEP (every trace run checked against the interpreter; the interpreter's result is kept)" : "ON", j, mb, base, (unsigned) b, free0, free1, mb);
	const char *pr = getenv ("SHEPHERD_JIT_PROBE");
	if (!(pr && !strcmp (pr, "0"))) {
		/* how much more ForVM the kernel would grant now (the console's open question: is the quota 16 MiB in all, can one block have it?):
		 * the largest additional single block, each size allocated and freed at once, largest first */
		static const unsigned sizes [] = { 16, 14, 12, 10, 8, 6, 4, 3, 2, 1 };
		char tried [320] = "";
		size_t tl = 0;
		unsigned largest = 0;
		for (unsigned i = 0; i < sizeof sizes / sizeof sizes [0] && !largest; i++) {
			SceUID u = sceKernelAllocMemBlockForVM ("shepherd-jit-probe", sizes [i] * MiB);
			if (tl < sizeof tried)
				tl += (size_t) snprintf (tried + tl, sizeof tried - tl, "%s%u MiB %s0x%08x", i ? ", " : "", sizes [i], u >= 0 ? "granted " : "", (unsigned) u);
			if (u >= 0) {
				largest = sizes [i];
				sceKernelFreeMemBlockForVM (u);
			}
		}
		unsigned free2 = k8_free_user_kib ();
		LOG ("J1 ForVM probe (after the %u MiB code block): the largest ADDITIONAL single block the kernel grants this app now: %u MiB, so at least %u MiB of ForVM in all (each tried, largest first, freed at once: %s); free user memory %u KiB before the probe, %u KiB after it",
		     mb, largest, largest + mb, tried, free1, free2);
		if (free2 + 1024 <= free1)
			LOG ("WARN J1 ForVM probe: %u KiB of the probe's memory did not come back before the partition: the arena below is that much smaller (SHEPHERD_JIT_PROBE=0 in " SHP_ENV_NAME " skips the probe)",
			     free1 - free2);
	}
}

static void k8_jit_end (void)
{
	if (!&mono_shepjit_report)
		return;
	/* (kit #9 review fix, vita/notes/kit9-jit.md "Review fixes") kit #9's runtime appends its fields to the report ("; kit #9..."): the
	 * whole report no longer fits one log line (LOG's buffer is 1024 bytes too: the lockstep start's line was cut inside the transform
	 * counters), so kit #9's part goes on a J2 line of its own. Kit #8's runtime prints no "; kit #9" part: one line, as kit #8 did. */
	char b [2048];
	mono_shepjit_report (b, sizeof b);
	char *k9 = strstr (b, "; kit #9");
	if (k9)
		*k9 = 0;
	LOG ("J2 baseline JIT at the end: %s", b);
	if (k9)
		LOG ("J2 %s", k9 + 2);
	if (&mono_shepjit_dump)
		mono_shepjit_dump ();   /* runtime/logs/<app>/shepjit-traces.tsv + shepjit-aborts.tsv */
}
