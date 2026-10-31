/* phys_host.inc.c -- included by host.c (SHEPHERD_GL builds). Device kit #8, Law of the Vita part 1 (physics-plan-v2 Stage 7 item 1;
 * vita/notes/kit8-physics.md): libshepphys (vita/physics: Box2D 2.3.1 + the Unity layer) linked into the eboot.
 *
 *  - shepphys_proc: the engine's Physics/ShepPhys.cs resolves every libshepphys entry point through it (SHEPHERD_PHYS_HOST=1; a direct
 *    __Internal P/Invoke in the static table). Builds that link the library (vita/shepherd/build.sh SHEPHERD_PHYS_LINK: the play apps 00097 /
 *    00098 / 00095 by default) take the library's own (shim/shepphys.cpp); every other GL build keeps kit #7's NULL stub (below) (the engine then logs "FAIL phys:
 *    libshepphys missing" at any level >= 1 and runs Stage 0 -- the measurement app 00099 never asks: its level is 0).
 *  - the physics LEVEL (SHEPHERD_PHYS, read by the engine's Physics/PhysConfig.cs): the play apps' default is SHP_PHYS_DEFAULT (build.sh:
 *    6 = Walking + Triggers + Combat + Shapes (Outlines, Splitting) + Ground -- the proven level that makes the intro walkable); the env file
 *    wins: SHEPHERD_PHYS=0 (the groundwork, inert) or =none is kit #7's behaviour. At level >= 1 the host sets SHEPHERD_PHYS_HOST=1.
 *  - the POOL, like the audio pool: one USER_RW memblock of SHEPHERD_PHYS_BLOCK_MB (default SHP_PHYS_BLOCK_MB = 1 MiB) carved from the
 *    arena's share (allocated right after the arena), handed to libshepphys before the engine's sp_init (sp_host_memory): its pool (the
 *    pose mirrors; a quarter of the block) and B2's Box2D arena (the rest) live there instead of the newlib heap. Only at level >= 1 (level
 *    0 / none: no block, kit #7's partition). SHEPHERD_PHYS_BLOCK_MB=0 -- or a block the kernel refuses -- (kit #8 review fix): the same
 *    layout from the newlib heap instead (a SHP_PHYS_BLOCK_MB block: pool 256 KiB + B2 arena 768 KiB), never libshepphys's 4 + 4 MiB calloc
 *    defaults (the Mac's sizes: 8 MiB of the play apps' fixed 116 MiB newlib heap). Measured need (Mac, the intro's combat drive at level 6):
 *    pool 6.9 KiB, B2 arena 260 KiB high water.
 *  - the Mono thread on USER_0 (physics runs on it; plan Stage 7 item 1) ONLY with SHEPHERD_PHYS_PIN=1 when kit 3's pinning is off (kit #8
 *    review fix: off by default -- part 1 has no physics workers, so the pin cannot help yet and would only change 00097's scheduling against
 *    kit #7's; a device A/B decides it). With kit 3's pinning on (the MT apps) the engine pins it there already (Diag/VitaHost.cs).
 *  - SHEPHERD_PHYS2D_SETTINGS (level >= 1): the owner folder's data/physics2d-settings.bin -- the project's Physics2DSettings record, which the
 *    card's strict pack (recorded with physics off) does not carry (Runtime/Physics2DSettings.cs; stage-owner.sh writes it). Kit #8 review fix:
 *    the file is REQUIRED at level >= 1 -- missing or empty, the app runs level 0 (kit #7's groundwork) with a WARN PHYS line instead of a world
 *    on Unity's default gravity / layer matrix (the engine refuses its core too when the file does not decode: Physics/PhysWorld.Stage1.cs).
 *  - SHEPHERD_PHYS is parsed exactly as the engine parses it (trimmed, case-insensitive) and written back in that form, so the host and the
 *    engine always run the same level (kit #8 review fix: "6 " was 0 here and 6 in the engine).
 *  - an END line with the block's use (the pool's and the arena's high water, B2's malloc fallbacks).
 *  - Kit #9 (Law of the Vita part 2; physics-plan-v2 Stage 7 item 2, patch P3): the physics WORKERS. SHEPHERD_PHYS_WORKERS (env file;
 *    default SHP_PHYS_WORKERS = build.sh SHEPHERD_PHYS_WORKERS_DEFAULT, 0 = kit #8's serial step) native threads that the step's forked
 *    phases run on (vita/physics/shim/sp_p3.cpp: bit-identical to the serial step). The host gives libshepphys its threads (sp_host_jobs):
 *    SceKernel threads at the CALLING thread's priority (the Mono thread waits on them inside a step: latency, not background work), 64 KiB
 *    stacks, never attached to Mono, on the cores SHEPHERD_PHYS_CORES names (hex masks, one per worker) or by default the cores the step
 *    does not need: with kit 3's pinning (the MT apps: USER_1 = the render thread, which prepares the previous frame while the step runs)
 *    USER_2 and -- when the C3 probe found it -- CPU3; without pinning (00097: render on the main thread) USER_1 and USER_2. A worker the
 *    list has no core for is not started (the engine logs how many run). Only at level >= 1; level 0 / none: nothing. The END line counts
 *    the forks.
 *  - Kit #9 review fixes: the host joins its workers (thread_join: sceKernelWaitThreadEnd + delete), so sp_jobs_stop really ends them; the
 *    CPU3 worker follows Settings > Graphics > 4th CPU Core (Off = no worker on CPU3: the pool restarts with worker 1 alone, as when the row
 *    is Off at boot; On = USER_2 + CPU3 again); without kit 3's pinning (00097) the Mono thread goes to USER_0 while the workers run
 *    (their cores there are USER_1 and USER_2; SHEPHERD_PHYS_PIN=0 keeps it unpinned); a worker that shares a core the calling thread may
 *    run on gets a WARN in its PHYS line -- the join no longer spins without end (libshepphys sleeps after SHEPHERD_PHYS_SPIN_US), so that
 *    costs speed, never a hang; the END line reports the pool's scratch (malloc'd) and how often the join slept. */

#ifdef SHP_PHYS_LINK
#include <ctype.h>
#include "../physics/shim/shepphys.h"
#ifndef SHP_PHYS_DEFAULT
#define SHP_PHYS_DEFAULT "6"
#endif
#ifndef SHP_PHYS_BLOCK_MB
#define SHP_PHYS_BLOCK_MB 1u
#endif
#ifndef SHP_PHYS_WORKERS
#define SHP_PHYS_WORKERS "0"   /* kit #9: the play apps' default worker count (build.sh SHEPHERD_PHYS_WORKERS_DEFAULT) */
#endif
static int g_phys_level = -2;       /* the level the engine will run (as Physics/PhysConfig.cs parses SHEPHERD_PHYS), -1 = none */
static unsigned g_phys_block_mb;    /* the block reserved from the arena's share (0 = none) */
static void *g_phys_block;
static int g_phys_block_rc;
static int g_phys_newlib;           /* kit #8 review: the pool's block came from the newlib heap (SHEPHERD_PHYS_BLOCK_MB=0 or the memblock refused) */
static int g_phys_nofile;           /* kit #8 review: level >= 1 asked, the settings file missing -> level 0 */
/* SHEPHERD_PHYS exactly as the engine parses it (Physics/PhysConfig.cs: Trim().ToLowerInvariant(); none / off / kit4 = -1; empty = 0;
 * int.TryParse = optional sign + ASCII digits; outside 0..7 or not a number = 0). Kit #8 review fix: the old strtol parse was case-sensitive
 * and untrimmed ("6 " = 0 here, 6 in the engine; "None" = 0 here, none there). Returns 0..7 or -1. */
static int phys_parse_level (const char *v)
{
	char t [24];
	size_t n = 0;
	if (!v)
		return 0;
	while (*v && isspace ((unsigned char) *v))
		v++;
	while (*v && n < sizeof (t) - 1)
		t [n++] = (char) tolower ((unsigned char) *v++);
	if (*v)
		return 0;   /* longer than any value the engine takes */
	while (n && isspace ((unsigned char) t [n - 1]))
		n--;
	t [n] = 0;
	if (!n)
		return 0;
	if (!strcmp (t, "none") || !strcmp (t, "off") || !strcmp (t, "kit4"))
		return -1;
	const char *p = t;
	int neg = *p == '-';
	if (*p == '+' || *p == '-')
		p++;
	if (!*p)
		return 0;
	long x = 0;
	for (; *p; p++) {
		if (*p < '0' || *p > '9' || x > 1000)
			return 0;
		x = x * 10 + (*p - '0');
	}
	if (neg)
		x = -x;
	return x < 0 || x > 7 ? 0 : (int) x;
}

/* ---- kit #9: the physics workers (patch P3's pool runs on these threads) ---- */
static int g_phys_workers;          /* asked (SHEPHERD_PHYS_WORKERS, 0..3) */
static int g_phys_cores [4];        /* SHEPHERD_PHYS_CORES: per worker 1..3, 0 = the default rule */
#ifndef SHEPHERD_MAC_CHECK
struct phys_wstart { void (*entry) (void *); void *arg; };
static struct phys_wstart g_phys_ws [4];
static SceUID g_phys_wtid [4];
static int phys_worker_tramp (SceSize len, void *argp)
{
	(void) len;
	struct phys_wstart *ws = *(struct phys_wstart **) argp;
	ws->entry (ws->arg);
	return 0;
}
/* worker index (1..3) -> its cpu mask: SHEPHERD_PHYS_CORES, else the cores the step leaves free (the comment above); 0 = none */
static int phys_core_mask (int index, const char **why)
{
	if (g_phys_cores [index]) { *why = "SHEPHERD_PHYS_CORES"; return g_phys_cores [index]; }
	if (pin_requested ()) {   /* the MT apps: USER_1 is the render thread's */
		*why = "kit 3's pinning: USER_2, then CPU3 when the C3 probe found it";
		if (index == 1) return SHP_USER2_MASK;
		if (index == 2 && g_core3 > 0 && g_core3_use) return SHP_CORE3_MASK;
		if (index == 2 && g_core3 > 0)   /* kit #9 review fix: say why */
			*why = "kit 3's pinning: CPU3 is open but Settings > Graphics > 4th CPU Core is Off (nothing on CPU3; On starts it)";
		return 0;
	}
	*why = "no pinning (render on the main thread): USER_1, then USER_2";
	return index == 1 ? 0x20000 /* SCE_KERNEL_CPU_MASK_USER_1 */ : index == 2 ? SHP_USER2_MASK : 0;
}
static int32_t phys_thread_start (int32_t index, void (*entry) (void *), void *arg)
{
	if (index < 1 || index > 3)
		return -1;
	const char *why = "";
	int mask = phys_core_mask (index, &why);
	if (!mask) {
		LOG ("PHYS worker %d: not started (no free core for it: %s; SHEPHERD_PHYS_CORES names one)", (int) index, why);
		return -1;
	}
	SceKernelThreadInfo ti;
	memset (&ti, 0, sizeof ti);
	ti.size = sizeof ti;
	int prio = 0x10000100;
	if (sceKernelGetThreadInfo (sceKernelGetThreadId (), &ti) >= 0)
		prio = ti.currentPriority;   /* the calling (Mono) thread's: it waits on the worker inside the step */
	/* kit #9 review fix: a worker on a core the calling thread may run on (its affinity; 0 = the kernel's default, any user core) cannot run
	 * while that thread spins there -- equal priority, no preemption. libshepphys's join sleeps after SHEPHERD_PHYS_SPIN_US, so this costs
	 * speed, never a hang; the line says so */
	int cur = sceKernelGetThreadCpuAffinityMask (sceKernelGetThreadId ());
	int shared = cur <= 0 || (cur & mask) != 0;
	g_phys_ws [index].entry = entry;
	g_phys_ws [index].arg = arg;
	struct phys_wstart *p = &g_phys_ws [index];
	SceUID t = sceKernelCreateThread ("shepherd-phys-worker", phys_worker_tramp, prio, 64 * 1024, 0, mask, NULL);
	int r = t < 0 ? t : sceKernelStartThread (t, sizeof p, &p);
	if (t >= 0 && r < 0)
		sceKernelDeleteThread (t);
	char warn [200] = "";
	if (r >= 0 && shared)
		snprintf (warn, sizeof warn, " -- WARN the calling thread may run on this core too (its affinity 0x%x): then a join sleeps after "
		          "SHEPHERD_PHYS_SPIN_US instead of spinning (slower forks, never a hang)", (unsigned) cur);
	LOG ("PHYS worker %d: %s SceUID 0x%x, cpu mask 0x%x (%s), priority %d (the calling thread's), 64 KiB stack%s%s", (int) index,
	     r < 0 ? "NOT STARTED," : "started,", (unsigned) t, (unsigned) mask, why, prio, r < 0 ? " -- that step part runs on fewer threads" : "", warn);
	if (r < 0)
		return r;
	g_phys_wtid [index] = t;
	return 0;
}
/* kit #9 review fix: the join sp_jobs_stop needs (it was NULL: the workers were never joined, and a worker that had not yet read the pool's quit
 * flag could run a stale task). The worker's entry returns once it reads quit; this waits for that, then frees the thread */
static void phys_thread_join (int32_t index)
{
	if (index < 1 || index > 3 || g_phys_wtid [index] <= 0)
		return;
	SceUID t = g_phys_wtid [index];
	int st = 0;
	int wr = sceKernelWaitThreadEnd (t, &st, NULL);
	int dr = sceKernelDeleteThread (t);
	g_phys_wtid [index] = 0;
	LOG ("PHYS worker %d: ended (SceUID 0x%x; wait rc 0x%08x, exit status %d, delete rc 0x%08x)", (int) index, (unsigned) t, (unsigned) wr, st, (unsigned) dr);
}
static void *phys_sem_create (void)
{
	SceUID s = sceKernelCreateSema ("shepherd-phys-sema", 0, 0, 0x7FFFFFFF, NULL);
	return s <= 0 ? NULL : (void *) (intptr_t) s;
}
static void phys_sem_post (void *s) { sceKernelSignalSema ((SceUID) (intptr_t) s, 1); }
static void phys_sem_wait (void *s) { sceKernelWaitSema ((SceUID) (intptr_t) s, 1, NULL); }
static void phys_sem_destroy (void *s) { if (s) sceKernelDeleteSema ((SceUID) (intptr_t) s); }
static uint64_t phys_now_us (void) { return sceKernelGetProcessTimeWide (); }
static const sp_jobs_host g_phys_jobs_host = { phys_thread_start, phys_thread_join, phys_sem_create, phys_sem_post, phys_sem_wait, phys_sem_destroy, phys_now_us };
/* kit #9 review fix: Settings > Graphics > 4th CPU Core (k6_core3 in host.c calls this after the row changed). Worker 2 runs on CPU3 only while
 * the row is On, so a change restarts the pool on the cores phys_core_mask gives now: Off = worker 1 alone on USER_2 (= the row Off at boot),
 * On = USER_2 + CPU3. The row's handler runs on the Mono thread, which also steps the physics, so no fork is in flight: sp_jobs_stop joins
 * the workers (phys_thread_join), sp_jobs_start makes them again. Results never depend on the workers (bit-identical), only the speed does.
 * A pool the engine has not started yet needs nothing: sp_jobs_start reads the row when it runs. */
static void phys_core3_changed (int use)
{
	if (!pin_requested ())
		return;   /* (without kit 3's pinning no worker is on CPU3 -- and k6_core3 changes nothing then) */
	int live = (int) sp_jobs_get (SP_JOBS_WORKERS);
	int on3 = use && g_core3 > 0 && !g_phys_cores [2] && g_phys_workers >= 2;   /* worker 2 belongs on CPU3 now */
	if (live <= 0 || g_phys_cores [2] || (live >= 2) == on3)
		return;
	int sr = (int) sp_jobs_stop ();
	int rr = (int) sp_jobs_start (g_phys_workers);
	LOG ("PHYS workers: 4th CPU Core %s -> the pool restarted between steps (stop rc %d, start rc %d): %d live of %d (was %d)%s", use ? "On" : "Off",
	     sr, rr, (int) sp_jobs_get (SP_JOBS_WORKERS), g_phys_workers, live, use ? "" : " -- nothing on CPU3");
}
#endif
/* SHEPHERD_PHYS_WORKERS / SHEPHERD_PHYS_CORES after the level (level >= 1 only): the host's threads for libshepphys */
static void phys_workers_config (void)
{
	setenv ("SHEPHERD_PHYS_WORKERS", SHP_PHYS_WORKERS, 0);   /* the env file wins */
	char raw [32];
	snprintf (raw, sizeof (raw), "%s", getenv ("SHEPHERD_PHYS_WORKERS") ? getenv ("SHEPHERD_PHYS_WORKERS") : "0");
	int w = atoi (raw);
	g_phys_workers = w < 0 ? 0 : w > 3 ? 3 : w;
	const char *c = getenv ("SHEPHERD_PHYS_CORES");
	for (int i = 1; c && *c && i <= 3; i++) {
		g_phys_cores [i] = (int) strtol (c, NULL, 16);
		c = strchr (c, ',');
		if (c) c++;
	}
	if (g_phys_level < 1) {
		g_phys_workers = 0;
		return;
	}
	static const char *const plain [] = { "0", "1", "2", "3" };
	setenv ("SHEPHERD_PHYS_WORKERS", plain [g_phys_workers], 1);   /* (the engine reads the same count) */
#ifndef SHEPHERD_MAC_CHECK
	int rc = g_phys_workers ? sp_host_jobs (&g_phys_jobs_host) : 0;
	if (g_phys_workers && rc == 0)   /* (not pin_requested () here: the MT default reaches the environment after this; the hook asks at the row's change) */
		g_k6_core3_hook = phys_core3_changed;   /* kit #9 review fix: the CPU3 worker follows Settings > Graphics > 4th CPU Core */
#else
	int rc = 0;
#endif
	LOG ("PHYS workers %d (SHEPHERD_PHYS_WORKERS=%s, %s): %s", g_phys_workers, raw, strcmp (raw, SHP_PHYS_WORKERS) ? "set in " SHP_ENV_NAME : "this app's default",
	     g_phys_workers ? (rc == 0 ? "patch P3's forked phases (Collide, the trigger pass, the islands) on the host's threads, bit-identical to the serial step; "
	                               "cores: SHEPHERD_PHYS_CORES or the free ones (PHYS worker lines when the engine starts them)" : "sp_host_jobs refused: serial")
	                    : "the serial step (kit #8; SHEPHERD_PHYS_WORKERS=1..3 = patch P3's workers)");
}

/* after the env file: the level in force, SHEPHERD_PHYS_HOST, the block's size (one PHYS line) */
static void phys_config (void)
{
	setenv ("SHEPHERD_PHYS", SHP_PHYS_DEFAULT, 0);   /* the env file wins */
	char raw [64];
	snprintf (raw, sizeof (raw), "%s", getenv ("SHEPHERD_PHYS") ? getenv ("SHEPHERD_PHYS") : "");
	g_phys_level = phys_parse_level (raw);
	/* kit #8 review fix: the settings file is required at level >= 1 (the core on Unity's default gravity / layer matrix is a game-visible
	 * difference); missing or empty -> level 0 (kit #7's groundwork, inert), said loudly */
	char sp [256];
	if (g_phys_level >= 1) {
		setenv ("SHEPHERD_PHYS2D_SETTINGS", ROOTED "/data/physics2d-settings.bin", 0);   /* (the env file may name another file) */
		const char *path = getenv ("SHEPHERD_PHYS2D_SETTINGS");
		snprintf (sp, sizeof (sp), "%s", path ? path : "");
		const char *io = sp [0] == '/' && strchr (sp, ':') ? sp + 1 : sp;   /* the managed form "/ux0:..." (pal_vita.c "Paths") -> "ux0:..." */
		unsigned fsz = 0;
		if (!*io || file_size (io, &fsz) != 0 || fsz == 0) {
			g_phys_nofile = 1;
			g_phys_level = 0;
		}
	}
	/* the level as the engine will parse it, written back in its plain form (the host and the engine run the same level) */
	static const char *const plain [] = { "0", "1", "2", "3", "4", "5", "6", "7" };
	setenv ("SHEPHERD_PHYS", g_phys_level < 0 ? "none" : plain [g_phys_level], 1);
	if (g_phys_level >= 1) {
		setenv ("SHEPHERD_PHYS_HOST", "1", 0);
		g_phys_block_mb = env_uint ("SHEPHERD_PHYS_BLOCK_MB", 0, 64, SHP_PHYS_BLOCK_MB);
	}
	if (g_phys_nofile)
		LOG ("WARN PHYS the physics settings file %s is missing (or empty): physics level 0 instead of %d (kit #7's groundwork, inert; no core, no "
		     "pool block) -- copy the kit's data/physics2d-settings.bin to ux0:data/shepherd/data/ (vita/shepherd/stage-owner.sh makes it)",
		     sp [0] ? sp : "(SHEPHERD_PHYS2D_SETTINGS empty)", phys_parse_level (raw));
	LOG ("PHYS level %d (SHEPHERD_PHYS=%s, %s): %s; libshepphys linked (%s); %s", g_phys_level, raw, strcmp (raw, SHP_PHYS_DEFAULT) ? "set in " SHP_ENV_NAME : "this app's default",
	     g_phys_level >= 1 ? "the core simulates (Physics/PhysConfig.cs levels; SHEPHERD_PHYS=0 in " SHP_ENV_NAME " = kit #7's groundwork, inert)"
	                       : g_phys_nofile ? "no core: the settings file is missing (WARN above)" : "no core: kit #7's behaviour (SHEPHERD_PHYS=0 / none)",
	     sp_build_info (),
	     g_phys_level >= 1 ? (g_phys_block_mb ? "the pool block comes out of the arena's share (SHEPHERD_PHYS_BLOCK_MB)"
	                                          : "no pool memblock (SHEPHERD_PHYS_BLOCK_MB=0): the same 1-MiB layout from the newlib heap")
	                       : "no pool block");
	phys_workers_config ();   /* kit #9 */
}
/* the partition: the block's MiB come out of the arena's share (as the audio pool's) */
static unsigned phys_reserve_mb (void) { return g_phys_level >= 1 ? g_phys_block_mb : 0; }
/* right after the arena (and the IL / audio pools): the block, handed to libshepphys before the engine's sp_init */
/* kit #8 review fix: SHEPHERD_PHYS_BLOCK_MB=0, or a memblock the kernel refused: the same layout from the newlib heap -- a SHP_PHYS_BLOCK_MB
 * block (pool 256 KiB + B2 arena 768 KiB), zeroed like a fresh memblock -- instead of libshepphys's own 4 + 4 MiB calloc defaults */
static void phys_newlib_block (const char *why)
{
	size_t bytes = (size_t) SHP_PHYS_BLOCK_MB * MiB;
	void *nb = calloc (1, bytes);
	int rc = nb ? sp_host_memory (nb, (uint32_t) bytes) : -1;
	if (nb && rc == 0) {
		g_phys_block = nb;
		g_phys_newlib = 1;
		LOG ("PHYS pool block: %u MiB from the newlib heap at %p (%s; the memblock's layout: libshepphys's pool -- a quarter -- and B2's Box2D "
		     "arena -- the rest)", (unsigned) SHP_PHYS_BLOCK_MB, nb, why);
	} else {
		free (nb);
		LOG ("WARN PHYS no newlib block either (rc %d): libshepphys callocs its default pool and arena (4 + 4 MiB of newlib)", rc);
	}
}
static void phys_block_alloc (void)
{
	if (g_phys_level < 1)
		return;
	if (!g_phys_block_mb) {
		phys_newlib_block ("SHEPHERD_PHYS_BLOCK_MB=0: no memblock, the arena keeps its share");
		log_mem ("phys-pool");
		return;
	}
	void *b = NULL;
#ifdef SHEPHERD_MAC_CHECK
	b = malloc ((size_t) g_phys_block_mb * MiB);   /* the Mac compat layer has no real memblocks */
	g_phys_block_rc = b ? sp_host_memory (b, g_phys_block_mb * MiB) : -1;
	if (b && g_phys_block_rc != 0) {
		free (b);
		b = NULL;
	}
#else
	SceUID pb = sceKernelAllocMemBlock ("shepherd-phys-pool", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, g_phys_block_mb * MiB, NULL);
	g_phys_block_rc = pb < 0 ? pb : sceKernelGetMemBlockBase (pb, &b) < 0 || !b ? -1 : sp_host_memory (b, g_phys_block_mb * MiB);
	if (g_phys_block_rc != 0) {
		if (pb >= 0)
			sceKernelFreeMemBlock (pb);   /* kit #8 review fix: a block allocated but not usable is given back, not leaked */
		b = NULL;
	}
#endif
	if (b) {
		g_phys_block = b;
		LOG ("PHYS pool block: %u MiB USER_RW at %p (libshepphys's pool -- a quarter, the pose mirrors -- and B2's Box2D arena -- the rest; "
		     "the arena's share is %u MiB smaller)", g_phys_block_mb, b, g_phys_block_mb);
	} else {
		LOG ("WARN PHYS cannot allocate / register the %u MiB pool memblock (rc 0x%x): the same layout from the newlib heap", g_phys_block_mb, (unsigned) g_phys_block_rc);
		phys_newlib_block ("the memblock was refused");
	}
	log_mem ("phys-pool");
}
/* the Mono thread's first act: USER_0 for the physics thread, unless kit 3's pinning does it (the engine) or SHEPHERD_PHYS_PIN=0 */
static void phys_pin_mono (void)
{
	if (g_phys_level < 1 || pin_requested ())
		return;
	const char *p = getenv ("SHEPHERD_PHYS_PIN");
	/* kit #9 review fix: with the physics workers on (SHEPHERD_PHYS_WORKERS >= 1; their cores without pinning: USER_1, USER_2) the Mono thread
	 * goes to USER_0, off the cores its workers wait on (it spins for them in each join); SHEPHERD_PHYS_PIN=0 keeps it unpinned (the join
	 * then sleeps after its spin when it shares a worker's core) */
	int forWorkers = g_phys_workers > 0 && !g_phys_cores [1] && !g_phys_cores [2] && !g_phys_cores [3] && !(p && !strcmp (p, "0"));
	if (!forWorkers && (!p || strcmp (p, "1")))
		return;   /* kit #8 review fix: opt-in (SHEPHERD_PHYS_PIN=1) until a device A/B shows a gain; default = kit #7's affinity */
	shepherd_vita_pin (0x10000, forWorkers ? "mono (physics workers on: USER_0, off their USER_1 / USER_2)" : "mono (physics: USER_0)");   /* SCE_KERNEL_CPU_MASK_USER_0 */
}
/* the END line: the block's use */
static void phys_end_line (void)
{
	if (g_phys_level < 1)
		return;
	sp_pool_stats ps, as;
	int up = sp_pool_base () != NULL;
	void *base = NULL;
	uint32_t bytes = 0, pool = 0, b2 = 0;
	sp_host_memory_info (&base, &bytes, &pool, &b2);
	memset (&ps, 0, sizeof (ps));
	memset (&as, 0, sizeof (as));
	if (up)
		sp_pool_get_stats (&ps);
	sp_b2_get_stats (&as);
	LOG ("END physics: level %d, %s; pool %s%u KiB, high water %u KiB, %u live blocks, %u failures; B2 arena %u KiB, high water %u KiB, "
	     "%u malloc fallbacks (%u live)", g_phys_level,
	     !base ? "no block (libshepphys's calloc defaults)" : g_phys_newlib ? "the newlib block" : "the pool block",
	     up ? "" : "(the engine never started the core) ", ps.bytes / 1024u, ps.high_water / 1024u, ps.live_blocks, ps.failures,
	     as.bytes / 1024u, as.high_water / 1024u, as.failures, as.base_gen);
	if (g_phys_workers)   /* kit #9: what the workers did */
		LOG ("END physics workers: %d live of %d; steps %d, forks %d (wakes %d, sleeps %d); collide %d forks / %d contacts; triggers %d / %d; islands %d "
		     "forks / %d islands (%d big); serial: collide %d, triggers %d, islands %d below the thresholds, %d statics (%d sleepers left asleep); "
		     "anomalies %d; the join slept %d times; scratch %d KiB (newlib: the contact scratch to the largest forked world, the workers' "
		     "island allocators; freed when the pool stops)",
		     (int) sp_jobs_get (SP_JOBS_WORKERS), g_phys_workers, (int) sp_jobs_get (SP_JOBS_ST_STEPS), (int) sp_jobs_get (SP_JOBS_ST_FORKS),
		     (int) sp_jobs_get (SP_JOBS_ST_WAKES), (int) sp_jobs_get (SP_JOBS_ST_SLEEPS), (int) sp_jobs_get (SP_JOBS_ST_COLLIDE_FORKS),
		     (int) sp_jobs_get (SP_JOBS_ST_COLLIDE_ITEMS), (int) sp_jobs_get (SP_JOBS_ST_TRIGGER_FORKS), (int) sp_jobs_get (SP_JOBS_ST_TRIGGER_ITEMS),
		     (int) sp_jobs_get (SP_JOBS_ST_ISLAND_FORKS), (int) sp_jobs_get (SP_JOBS_ST_ISLANDS), (int) sp_jobs_get (SP_JOBS_ST_ISLANDS_BIG),
		     (int) sp_jobs_get (SP_JOBS_ST_COLLIDE_SMALL), (int) sp_jobs_get (SP_JOBS_ST_TRIGGER_SMALL), (int) sp_jobs_get (SP_JOBS_ST_ISLAND_SMALL),
		     (int) sp_jobs_get (SP_JOBS_ST_ISLAND_STATICS), (int) sp_jobs_get (SP_JOBS_ST_HANDBACK_SLEEPERS), (int) sp_jobs_get (SP_JOBS_ST_ANOMALIES),
		     (int) sp_jobs_get (SP_JOBS_ST_JOIN_SLEEPS), (int) ((sp_jobs_get (SP_JOBS_ST_SCRATCH_BYTES) + 1023) / 1024));
}
#else
static void phys_config (void) {}
static unsigned phys_reserve_mb (void) { return 0; }
static void phys_block_alloc (void) {}
static void phys_pin_mono (void) {}
static void phys_end_line (void) {}
#endif

/* The engine's Physics/ShepPhys.cs resolves libshepphys through shepphys_proc under SHEPHERD_PHYS_HOST=1 (a direct __Internal P/Invoke, so
 * the GL build's static table needs the symbol). With SHP_PHYS_LINK the library defines it (vita/physics/shim/shepphys.cpp: every entry
 * point by name, the shepaudio_proc / shepgl_proc pattern). Without it (kit #7's eboots, the measurement app, SHEPHERD_PHYS_LINK=0): this
 * NULL stub -- the engine logs "FAIL phys: libshepphys missing" at level >= 1 and keeps stage 0 behaviour. */
#ifndef SHP_PHYS_LINK
void *shepphys_proc (const char *name)
{
	(void) name;
	return NULL;
}
#endif
