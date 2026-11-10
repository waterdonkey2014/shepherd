/* vita/vlx/k14_stackfail_vlx.c (device kit #14 integration, TEST ONLY: never part of an eboot, vita/shepherd/build.sh does not know this file).
 *
 * vlx-run.sh's VLX_K14_STACKFAIL=<n>:<KiB> links this with --wrap=mono_valloc_aligned: from the n-th interpreter stack request on (the first is the game thread's), a request over
 * <KiB> is refused the way the shared pool refuses: it goes on to the arena as a size the arena cannot hold, so the host sees a real ARENA FAIL line and the caller a real NULL.
 *   <n>:128 = the new thread must make do with 128 KiB; <n>:0 = no stack at all (the clean stop; with SHEPHERD_K14_OFF=0x400 the old NULL: the negative control).
 * K14SF_FROM / K14SF_KIB come from the compile line. */
#ifndef K14SF_FROM
#define K14SF_FROM 2
#endif
#ifndef K14SF_KIB
#define K14SF_KIB 0
#endif
#define ACCOUNT_INTERP_STACK 13   /* MONO_MEM_ACCOUNT_INTERP_STACK, utils/mono-mmap.h */
#define TOO_BIG (1536u * 1024u * 1024u)
extern void *__real_mono_valloc_aligned (unsigned size, unsigned alignment, int flags, int type);
static volatile int seen;
void *__wrap_mono_valloc_aligned (unsigned size, unsigned alignment, int flags, int type)
{
	if (type == ACCOUNT_INTERP_STACK && __atomic_add_fetch (&seen, 1, __ATOMIC_RELAXED) >= K14SF_FROM && size > K14SF_KIB * 1024u)
		return __real_mono_valloc_aligned (TOO_BIG, alignment, flags, type);
	return __real_mono_valloc_aligned (size, alignment, flags, type);
}
