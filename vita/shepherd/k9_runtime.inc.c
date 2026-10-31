/* SHEPHERD-VITA device kit #9, track runtime (vita/notes/kit9-runtime.md): the host side of the kit #9 runtime rows. Included by host.c.
 *
 * R1 fast TLS (vita/patches/k9-fast-tls.patch, built by vita/toolchain/build-runtime-k9.sh): the runtime mirrors its thread-local keys in
 * the Vita's per-thread TLS slots. The runtime's state is read through weak symbols: a runtime without the patch leaves them at address 0
 * and the H8 line says so. SHEPHERD_FAST_TLS (env file): unset / 1 / on = the slots; 0 / off = pthread_getspecific for every read (kit #8);
 * check = both, compared on every read -- the END line counts the mismatches (the runtime's own mirror; the slots are written);
 * probe = kit #8's reads AND the slots never written: every read counts a non-NULL value in its slot -- another module's per-thread state
 * -- and the END line reports it (review fix: the device test that no system module uses the runtime's slots; it disturbs nothing).
 * Before the Mono thread starts, k9_fast_tls_survey reads slots 0x8a-0x99 on the main thread (after the GL / audio / module start-up) and
 * hands the runtime the ones already in use (mono_shepherd_fast_tls_avoid): those are never claimed.
 * E2 (vita/patches/k9-e2-divmod.patch): AOT code's 64-bit division / remainder (the opcode emulation) of operands in [0, 2^32) done in
 * 32 bits (the same results); SHEPHERD_K9_DIVMOD=0 = kit #8's 64-bit division -- since the review fix also in divfast's inline 64-bit path.
 * E2 divfast (vita/patches/k9-divfast-compiler.patch, a build-time row: SHP_K9_DIVFAST from build.sh) has an env-file switch since the
 * review fix (vita/patches/k9-divfast-flag.patch: the runtime's word mono_shepherd_k9_divfast_flags, read by every fast path):
 * SHEPHERD_K9_DIVFAST=0 = every division through its wrapper (kit #8's code path); SHEPHERD_K9_DIVMOD=0 = the 64-bit fast path off too.
 * CoreLib trimming (vita/patches/k9-partial-unbox.patch: the gameaot fork's fix A in the LLVM / JIT runtimes; vita/patches/k9-unbox-cache-fix.patch
 * in the fix runtime): the END line counts the value-type methods that had no unbox trampoline in their image (not compiled: a trimmed
 * CoreLib, an owner module's partial image) and entered the interpreter through the arbitrary unbox trampoline -- cached per method since
 * the review fix (each with a jit info naming its method: AOT code's ldvirtftn + delegate constructor needs it) -- with the lookups and every
 * arbitrary trampoline taken from CoreLib's pool (1024, fatal when empty) by any path; 0 / 0 with complete images (every kit #8 eboot)
 * unless upstream's interpreted-image case takes some. SHEPHERD_K9_UNBOX_CACHE=0 (env file) = the uncached fallback (kit #8's fix A). */
#if !defined (SHEPHERD_MAC_CHECK)
extern int mono_shepherd_fast_tls_mode __attribute__ ((weak));
extern unsigned mono_shepherd_fast_tls_avoid __attribute__ ((weak));   /* review fix: the main thread's slots in use (set before the first key) */
extern void * volatile mono_shepherd_fast_tls_mismatch_value __attribute__ ((weak));
extern int mono_shepherd_fast_tls_keys __attribute__ ((weak));
extern int mono_shepherd_fast_tls_pthread_keys __attribute__ ((weak));
extern int mono_shepherd_fast_tls_busy __attribute__ ((weak));
extern int mono_shepherd_fast_tls_first __attribute__ ((weak));
extern int mono_shepherd_fast_tls_last __attribute__ ((weak));
extern volatile unsigned mono_shepherd_fast_tls_mismatches __attribute__ ((weak));
extern volatile int mono_shepherd_fast_tls_mismatch_slot __attribute__ ((weak));
extern volatile unsigned mono_shepherd_fast_tls_reads __attribute__ ((weak));   /* (b23b56a review fix) check / probe: the slot reads compared / watched */
extern int mono_shepherd_k9_divmod __attribute__ ((weak));   /* E2 (vita/patches/k9-e2-divmod.patch): -1 until the first 64-bit division */
extern int mono_shepherd_k9_unbox_fallbacks __attribute__ ((weak));   /* CoreLib trimming on the LLVM apps (vita/patches/k9-partial-unbox.patch) */
extern int mono_shepherd_k9_unbox_lookups __attribute__ ((weak));     /* (review fix) the fallback's lookups (it is cached per method) */
extern int mono_shepherd_k9_unbox_arbitrary __attribute__ ((weak));   /* (review fix) every arbitrary unbox trampoline taken, by any path */
extern int mono_shepherd_k9_divfast_flags __attribute__ ((weak));     /* (review fix) divfast's switch word (vita/patches/k9-divfast-flag.patch) */
#ifndef SHP_K9_DIVFAST
#define SHP_K9_DIVFAST 0
#endif

static unsigned g_k9_tls_survey;   /* the main thread's non-NULL slots 0x8a-0x99 (bit i = 0x8a + i) */
static void *g_k9_tls_survey_first;
/* main(), before the Mono thread: which of the runtime's candidate slots (0x8a-0x99) something already uses on the main thread */
static void k9_fast_tls_survey (void)
{
	g_k9_tls_survey = 0;
	for (int i = 0; i < 16; i++) {
		void **p = (void **) sceKernelGetTLSAddr (0x8a + i);
		if (p && *p) { if (!g_k9_tls_survey) g_k9_tls_survey_first = *p; g_k9_tls_survey |= 1u << i; }
	}
	if (&mono_shepherd_fast_tls_avoid)
		mono_shepherd_fast_tls_avoid = g_k9_tls_survey;
}

static void k9_log_fast_tls (void)
{
	const char *e = getenv ("SHEPHERD_FAST_TLS");
	if (!&mono_shepherd_fast_tls_mode) {
		LOG ("H8 runtime: no fast TLS in this runtime (every thread-local read is pthread_getspecific; SHEPHERD_FAST_TLS=%s ignored)", e ? e : "unset");
		return;
	}
	int m = mono_shepherd_fast_tls_mode;
	if (g_k9_tls_survey)
		LOG ("H8 runtime: fast TLS survey: the main thread's TLS slots 0x8a-0x99 in use before the runtime started: mask 0x%04x (first value 0x%08x) -- those slots are not claimed",
		     g_k9_tls_survey, (unsigned) (uintptr_t) g_k9_tls_survey_first);
	else
		LOG ("H8 runtime: fast TLS survey: the main thread's TLS slots 0x8a-0x99 all NULL before the runtime started");
	if (m == 4)
		LOG ("H8 runtime: fast TLS PROBE: %d runtime keys watched in the Vita's TLS slots 0x%x-0x%x (%d slot(s) skipped as not free, %d key(s) on pthread only): every read is pthread_getspecific (kit #8) and the slots are never written -- a non-NULL value there is another module's (the END line counts them) (SHEPHERD_FAST_TLS=%s)",
		     mono_shepherd_fast_tls_keys, (unsigned) mono_shepherd_fast_tls_first, (unsigned) (mono_shepherd_fast_tls_last < 0 ? mono_shepherd_fast_tls_first : mono_shepherd_fast_tls_last),
		     mono_shepherd_fast_tls_busy, mono_shepherd_fast_tls_pthread_keys, e ? e : "unset");
	else if (m == 1 || m == 2)
		LOG ("H8 runtime: fast TLS %s: %d runtime keys mirrored in the Vita's TLS slots 0x%x-0x%x (%d slot(s) skipped as not free, %d key(s) on pthread only)%s (SHEPHERD_FAST_TLS=%s; 0 = pthread_getspecific for every read, as kit #8)",
		     m == 2 ? "CHECK" : "ON", mono_shepherd_fast_tls_keys, (unsigned) mono_shepherd_fast_tls_first, (unsigned) (mono_shepherd_fast_tls_last < 0 ? mono_shepherd_fast_tls_first : mono_shepherd_fast_tls_last),
		     mono_shepherd_fast_tls_busy, mono_shepherd_fast_tls_pthread_keys, m == 2 ? " -- every read compared with pthread_getspecific" : "", e ? e : "unset");
	else if (m == 3)
		LOG ("H8 runtime: fast TLS OFF: SHEPHERD_FAST_TLS=%s is not a known value (unset / 1 / on = on, 0 / off = off, check = on + compared, probe = off + the slots watched), so every read is pthread_getspecific, as kit #8", e ? e : "unset");
	else
		LOG ("H8 runtime: fast TLS OFF (SHEPHERD_FAST_TLS=%s): every thread-local read is pthread_getspecific, as kit #8", e ? e : "unset");
	const char *d = getenv ("SHEPHERD_K9_DIVMOD");
	if (!&mono_shepherd_k9_divmod)
		LOG ("H8 runtime: no E2 64-bit division fast path in this runtime (SHEPHERD_K9_DIVMOD=%s ignored)", d ? d : "unset");
	else
		LOG ("H8 runtime: E2 64-bit division / remainder of 32-bit-sized operands in 32 bits %s (SHEPHERD_K9_DIVMOD=%s; 0 = libgcc's 64-bit division for every call, as kit #8 -- divfast's inline 64-bit path included)",
		     d && (!strcmp (d, "0") || !strcmp (d, "off")) ? "OFF" : "ON", d ? d : "unset");
	/* (review fix) E2 divfast's run-time switch: the word every compiled-in fast path checks */
	const char *f = getenv ("SHEPHERD_K9_DIVFAST");
	if (!SHP_K9_DIVFAST)
		LOG ("H8 runtime: E2 divfast not compiled into this eboot (every division is its wrapper call, as kit #8; SHEPHERD_K9_DIVFAST=%s has nothing to switch)", f ? f : "unset");
	else if (!&mono_shepherd_k9_divfast_flags)
		LOG ("WARN H8 runtime: E2 divfast compiled in but this runtime has no switch word (vita/patches/k9-divfast-flag.patch) -- a build error");
	else {
		int w = mono_shepherd_k9_divfast_flags;
		LOG ("H8 runtime: E2 divfast %s (switch word %d; SHEPHERD_K9_DIVFAST=%s, SHEPHERD_K9_DIVMOD=%s; SHEPHERD_K9_DIVFAST=0 = every division through its wrapper, kit #8's code path)",
		     w >= 2 ? "ON: every no-wrapper fast path" : w == 1 ? "32-bit + constant fast paths only (E2 off: 64-bit divisions through their wrapper)" : "OFF: every division through its wrapper, as kit #8",
		     w, f ? f : "unset", d ? d : "unset");
	}
}

/* the end (checkpoint kind 2): the check / probe mode's verdict; the partial-image unbox count */
static void k9_fast_tls_end (const char *tag)
{
	if (&mono_shepherd_k9_unbox_fallbacks)
		LOG ("K9 unbox (%s): %d value-type method(s) with no unbox trampoline in their image (not compiled there: a trimmed / partial image) entered the interpreter through the arbitrary unbox trampoline, %d lookup(s) (%s); arbitrary unbox trampolines taken by any path: %d of CoreLib's 1024 (the pool; fatal when empty) (vita/patches/k9-partial-unbox.patch / k9-unbox-cache-fix.patch; 0 methods with complete images)",
		     tag, mono_shepherd_k9_unbox_fallbacks, &mono_shepherd_k9_unbox_lookups ? mono_shepherd_k9_unbox_lookups : -1,
		     getenv ("SHEPHERD_K9_UNBOX_CACHE") && (!strcmp (getenv ("SHEPHERD_K9_UNBOX_CACHE"), "0") || !strcmp (getenv ("SHEPHERD_K9_UNBOX_CACHE"), "off"))
		       ? "SHEPHERD_K9_UNBOX_CACHE=0: uncached, no jit info -- kit #8's fix A; the method count stays 0" : "cached per method, each with a jit info naming it",
		     &mono_shepherd_k9_unbox_arbitrary ? mono_shepherd_k9_unbox_arbitrary : -1);
	if (!&mono_shepherd_fast_tls_mode || (mono_shepherd_fast_tls_mode != 2 && mono_shepherd_fast_tls_mode != 4))
		return;
	unsigned n = mono_shepherd_fast_tls_mismatches;
	unsigned v = &mono_shepherd_fast_tls_mismatch_value ? (unsigned) (uintptr_t) mono_shepherd_fast_tls_mismatch_value : 0;
	unsigned r = &mono_shepherd_fast_tls_reads ? mono_shepherd_fast_tls_reads : 0;
	if (mono_shepherd_fast_tls_mode == 4) {
		if (n)
			LOG ("WARN K9 fast TLS probe (%s): %u of %u read(s) found a non-NULL value in a slot the runtime never writes (first in slot 0x%x: 0x%08x) -- another module keeps per-thread state in the runtime's slots: SHEPHERD_FAST_TLS=0 in every env file", tag, n, r, (unsigned) mono_shepherd_fast_tls_mismatch_slot, v);
		else   /* (b23b56a review fix: the claim scoped to what ran -- a library first used later, or one that sets, uses and clears a slot within one call, is not seen) */
			LOG ("K9 fast TLS probe (%s): 0 foreign values in %u thread-local reads -- the runtime's slots 0x%x-0x%x held NULL at every read of THIS run (only what it exercised: a library first used later, or one that sets and clears a slot within one call, is not seen)", tag, r,
			     (unsigned) mono_shepherd_fast_tls_first, (unsigned) (mono_shepherd_fast_tls_last < 0 ? mono_shepherd_fast_tls_first : mono_shepherd_fast_tls_last));
		return;
	}
	if (n)
		LOG ("WARN K9 fast TLS check (%s): %u of %u read(s) whose TLS slot differed from pthread_getspecific (first in slot 0x%x: 0x%08x) -- something else writes the slots: SHEPHERD_FAST_TLS=0", tag, n, r, (unsigned) mono_shepherd_fast_tls_mismatch_slot, v);
	else
		LOG ("K9 fast TLS check (%s): 0 mismatches in %u reads -- every thread-local read's slot equalled pthread_getspecific", tag, r);
}
#else
static void k9_fast_tls_survey (void) { }
static void k9_log_fast_tls (void) { }
static void k9_fast_tls_end (const char *tag) { (void) tag; }
#endif
