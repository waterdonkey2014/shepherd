/* SHEPHERD-VITA device kit #14, track newlib (vita/notes/kit14-newlib.md): the runtime's rows of the track that takes memory off the C heap; the switches the runtime patch
 * (vita/patches/k14-newlib.patch) reads, and one line at the start. Included by host.c after k13_walkc.inc.c.
 *
 * Every row is exact: the same answers from smaller or later-made data. Each is ON by default with an env-file line back to kit #13:
 *   SHEPHERD_K14_NEWLIB=0        every row off = kit #13 (the engine reads it too: its native blocks; SHEPHERD_K14_BLOCKS=0 = that row alone)
 *   SHEPHERD_K14_OFF=<hex>       these rows off (the flags below), the others on
 *     0x1    interface bitmaps made at class setup (default: when first read)
 *     0x4    an inlined method's header not freed
 *     0x8    a table entry saying "no table" per interpreted method
 *     0x10   an AOT image's address -> method tables as two arrays the size of the whole image
 *     0x20   a mempool request that does not fit abandons its chunk's tail unless it is 8 KiB or more (default: 1 KiB)
 *     0x40   eglib's hash table entries one malloc each
 *     0x80   tier-up patch sites in malloc'd lists (and a tiered method's entry kept)
 *     0x400  a refused interpreter stack is a NULL the new thread faults on, and a refused request over one block is no FAIL line whoever asked (default, vita/patches/k14-stack-refused.patch:
 *            a smaller stack, under 64 KiB the clean stop; the runtime names sgen's multi-block requests, the only ones it halves, and every other refusal keeps a FAIL line)
 *     0x800  sgen's card table for all 4 GiB, 8 MiB (default: the 2 GiB the app's memory lies in, 4 MiB)
 *     0x1000 1 MiB of interpreter stack per thread (default: 1 MiB for the first thread, 256 KiB for the others)
 *     0x2000 newlib's guaranteed room above its top stays 8 MiB (default 6: what the walk's transients take, 5.2 MiB, + 0.8; a host row: an env file's SHEPHERD_K10_POOL_RESERVE_MB wins)
 *     0x4000 the play apps keep 10 MiB of free memory for thread stacks (default 6: 7 MiB stays free at the walk's end, the shared pool grows 4 MiB; a host row: an env file's
 *            SHEPHERD_STACK_RESERVE_MB or SHEPHERD_STACKS wins)
 *     0x8000 the GC heap's cap leaves kit #9's 22 MiB for the arena's other tenants (default: what they use, + a margin: 9 with the rows above)
 * Test only, in a runtime with vita/patches/k14-newlib-audit.patch: SHEPHERD_K14_BITMAP_AUDIT=1 (every deferred bitmap also computed at setup and compared when made; costs
 * memory; "K14 AUDIT" lines on stderr), =fake (+ one bit of each kept copy flipped: the audit's negative control, must count them all). The test runtime reads it.
 * The runtime keeps kit #13's behaviour until mono_vita_k14_set is called. A runtime without the patch (a weak symbol, NULL): no row at all, the host's included (the line reads
 * "flags 0x0 over 22"; a default run of a kit build reads "flags 0xfcfd over 9"). */

void mono_vita_k14_set (unsigned flags) __attribute__ ((weak));          /* vita/patches/k14-newlib.patch */
extern const int mono_vita_k14_isafe __attribute__ ((weak));             /* vita/patches/k14-stack-refused.patch: no such row in a runtime without it */

#define K14N_ROWS 0xFCFDu
#define K14N_ISAFE 0x400u
static unsigned k14n_rows (void)
{
	const char *m = getenv ("SHEPHERD_K14_NEWLIB"), *o = getenv ("SHEPHERD_K14_OFF");
	if (!mono_vita_k14_set || (m && !strcmp (m, "0")))   /* (no patch in the runtime: the arena holds kit #13's tenants, so the host's rows stay kit #13's too) */
		return 0;
	return K14N_ROWS & ~(o ? (unsigned) strtoul (o, NULL, 16) : 0u) & ~(&mono_vita_k14_isafe ? 0u : K14N_ISAFE);
}
/* with this row the runtime says which refusals sgen halves ("FAIL blocks"), so every other one keeps its FAIL line */
static int k14_refusals_named (void) { return (k14n_rows () & K14N_ISAFE) != 0; }

/* The GC heap's cap in the shared pool = the arena's room - this. Kit #9's formula reserves 22 MiB (card table 8, interpreter stacks 10, slack 4). What the arena
 * really held besides the GC heap when the cap was met (VLX, the walk at the device partitions): ~15.7 MiB (card table 8, 5 interpreter stacks, ~2.7 of sgen's own), ~7.5 with
 * this track's rows
 * -- the difference was room the GC could not use. The meter (K13 SHORT) compares the arena's peak with the room, so it never saw this slack. */
static unsigned k14n_over = SHP_CARD_TABLE_MB + SHP_INTERP_STACKS_MB + SHP_ARENA_SLACK_MB;
static unsigned k14_cap_over (void) { return k14n_over; }
/* the meter's device pools (K13 SHORT) were read at kit #6's stack reserve: what the reserve in force moves them by, whoever set it (this track's row, an env file, SHEPHERD_STACKS) */
static int k14_pool_more (void) { return (int) SHP_K6_STACK_RESERVE_MB - (int) g_stack_reserve_mb; }
static void k14_newlib_config (void)
{
	unsigned f = k14n_rows ();
	if (!f)
		return;
	/* what the rows leave: the card table 4 (8), interpreter stacks 2 (6), sgen's own + a margin 3 */
	if (f & 0x8000u)
		k14n_over = ((f & 0x800u) ? 4u : 8u) + ((f & 0x1000u) ? 2u : 6u) + 3u;
	if ((f & 0x2000u) && !getenv ("SHEPHERD_K10_POOL_RESERVE_MB"))
		setenv ("SHEPHERD_K10_POOL_RESERVE_MB", "6", 1);
#ifdef SHP_PLAY_APP
	if ((f & 0x4000u) && !getenv ("SHEPHERD_STACK_RESERVE_MB") && !getenv ("SHEPHERD_STACKS"))   /* (SHEPHERD_STACKS=kit4 / kit5 size their stacks and reserve as a set: stacks_config) */
		setenv ("SHEPHERD_STACK_RESERVE_MB", "6", 1);
#endif
}

static void k14_newlib_pre_init (void)
{
	unsigned f = k14n_rows ();
	if (mono_vita_k14_set)
		mono_vita_k14_set (f);
	LOG ("K14 NEWLIB flags 0x%x over %u", f, k14n_over);
}
