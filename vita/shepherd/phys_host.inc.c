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
 *  - an END line with the block's use (the pool's and the arena's high water, B2's malloc fallbacks). */

#ifdef SHP_PHYS_LINK
#include <ctype.h>
#include "../physics/shim/shepphys.h"
#ifndef SHP_PHYS_DEFAULT
#define SHP_PHYS_DEFAULT "6"
#endif
#ifndef SHP_PHYS_BLOCK_MB
#define SHP_PHYS_BLOCK_MB 1u
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
	if (!p || strcmp (p, "1"))
		return;   /* kit #8 review fix: opt-in (SHEPHERD_PHYS_PIN=1) until a device A/B shows a gain; default = kit #7's affinity */
	shepherd_vita_pin (0x10000, "mono (physics: USER_0)");   /* SCE_KERNEL_CPU_MASK_USER_0 */
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
