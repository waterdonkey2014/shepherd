/* SHEPHERD-VITA device kit #12, track walka (vita/notes/kit12-walka.md): THE WALK, the GC side -- the host's half. Included by host.c
 * after k11_walk.inc.c. Every switch is an env-file line; SHEPHERD_K12_WALKA=0 = kit #11's behaviour for every row of this track,
 * whatever the other lines say. The runtime half is vita/patches/k12-walka.patch (weak symbols here: a kit #11 runtime has none of its
 * entry points -- the K12 WALKA line says so and every row stays kit #11's).
 *
 *   SHEPHERD_K12_CONCPIN=1|0   A REFUSED EVACUATION COPY UNDER THE CONCURRENT COLLECTOR (DEFAULT ON): sgen's major collection copies the live
 *                              objects of an evacuating size class into fresh blocks; when such a block is refused (the heap's cap, the
 *                              arena), sgen pins the object where it is -- and stock sgen ASSERTS there under marksweep-conc (the play apps'
 *                              lever 32), so a scene that runs memory out ended in sgen's pin_major_object assertion (00095 on the walk at
 *                              its device partition, kit #11). On: the object is marked in place exactly as the serial collector does
 *                              (only with the world stopped -- where every such copy happens), so the session ends in the memory guard's
 *                              clean FAIL MEM or the collection survives. Never reached while memory lasts (identical records on every
 *                              route). 0 = kit #11 (stock sgen: the assertion).
 *   SHEPHERD_K12_SCRUB=1|0     THE SHARED POOL'S "PLACEMENT COST" FOUND AND REMOVED (DEFAULT ON): kit #11 measured +4..+12 MiB of GC heap on the
 *                              walk in the pool against the split and blamed the pool's top-down placement. It is ONE conservative false pin:
 *                              sgen scans the Mono thread's stack conservatively, and mono_thread_body's frame (it never returns) kept the
 *                              MONO_GC_PARAMS text ("major=marksweep-conc,concurrent-sweep,...") in its gcp buffer for the whole session; the
 *                              word "conc" (0x636e6f63) is a valid address inside the pool's top under VLX (the pool block lies at
 *                              0x5c..0x64 there -- lowercase ASCII as the high byte -- the split's arena at 0x48..0x4e, uppercase), so a dead
 *                              3.6 MiB byte array stayed pinned at every major (K12X diagnostics, vita/notes/kit12-walka.md section 2). On: the
 *                              buffer is cleared once the parameters are in force (they were copied into the environment by setenv and
 *                              read by sgen at its init). Exact: no output changes; only a falsely retained dead object is freed. On the
 *                              device user memory lies at 0x81..0x9f, which no ASCII text can reach, so there the clear changes nothing at
 *                              all (the device's pool keeps its X2 gain). 0 = kit #11 (the text stays).
 * Lines: "K12 WALKA" (the switches in force, before the runtime starts), "K12 WALKA end" (objects marked in place). */

#if !defined (SHEPHERD_MAC_CHECK)
void mono_vita_gc_set_conc_pin_in_place (int on) __attribute__ ((weak));   /* vita/patches/k12-walka.patch (sgen-marksweep.c) */
unsigned mono_vita_gc_conc_pins (void) __attribute__ ((weak));
#endif

#ifndef SHP_K12_CONCPIN_DEFAULT
#define SHP_K12_CONCPIN_DEFAULT 1   /* every app (only the concurrent collector -- the play apps' default -- can reach it) */
#endif

static struct {
	int on;          /* SHEPHERD_K12_WALKA != 0 */
	int rt;          /* the runtime has k12-walka.patch's entry points */
	int concpin;     /* SHEPHERD_K12_CONCPIN in force */
	int scrub;       /* SHEPHERD_K12_SCRUB in force */
} k12w;

static int k12w_env (const char *k, int dflt)
{
	const char *e = getenv (k);
	return e && *e ? atoi (e) : dflt;
}

/* the Mono thread, before monovm_initialize (after the H4 line): read the switches, set the runtime's flags, one line */
static void k12_walka_before_runtime (void)
{
	const char *m = getenv ("SHEPHERD_K12_WALKA");
	k12w.on = !(m && !strcmp (m, "0"));
#if !defined (SHEPHERD_MAC_CHECK)
	k12w.rt = mono_vita_gc_set_conc_pin_in_place && mono_vita_gc_conc_pins;
#endif
	k12w.concpin = k12w.on && k12w.rt && k12w_env ("SHEPHERD_K12_CONCPIN", SHP_K12_CONCPIN_DEFAULT) == 1;
	k12w.scrub = k12w.on && k12w_env ("SHEPHERD_K12_SCRUB", 1) == 1;
#if !defined (SHEPHERD_MAC_CHECK)
	if (k12w.rt)
		mono_vita_gc_set_conc_pin_in_place (k12w.concpin);
#endif
	LOG ("K12 WALKA (kit #12 walk, GC side; SHEPHERD_K12_WALKA=0 = kit #11): a refused evacuation copy under the concurrent collector %s | "
	     "the GC parameters' text on the Mono thread's stack %s",
	     !k12w.on ? "kit #11's (SHEPHERD_K12_WALKA=0: sgen's assertion)" :
	     !k12w.rt ? "kit #11's (the runtime has no vita/patches/k12-walka.patch: sgen's assertion)" :
	     k12w.concpin ? "marked in place as the serial collector does (SHEPHERD_K12_CONCPIN=0: sgen's assertion, kit #11)" :
	     "kit #11's (SHEPHERD_K12_CONCPIN=0: sgen's assertion)",
	     k12w.scrub ? "cleared once in force (SHEPHERD_K12_SCRUB=0: kept, kit #11)" : "kept (kit #11)");
}

/* mono_thread_body, right after k12_walka_before_runtime: the MONO_GC_PARAMS buffer of the never-returning frame, cleared (SHEPHERD_K12_SCRUB) --
 * setenv copied it and nothing reads the buffer again. The store goes through a volatile pointer so the compiler cannot drop it as dead. */
static void k12_walka_scrub (char *buf, size_t n)
{
	if (!k12w.scrub)
		return;
	volatile char *p = buf;
	for (size_t i = 0; i < n; i++)
		p [i] = 0;
}

/* the end lines (host.c, after K10 MEM end) */
static void k12_walka_end_line (void)
{
	unsigned pins = 0;
#if !defined (SHEPHERD_MAC_CHECK)
	if (k12w.rt)
		pins = mono_vita_gc_conc_pins ();
#endif
	LOG ("K12 WALKA end: %u object(s) marked in place after a refused evacuation copy (concurrent collector)%s", pins,
	     k12w.concpin ? "" : " -- the in-place path is off");
}
