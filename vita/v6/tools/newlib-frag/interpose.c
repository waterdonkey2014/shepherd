/* SHEPHERD-VITA V6 (vita/notes/v6-vita-build.md section 4): a DYLD interposer that records every malloc-family call of
 * the process (the mac-check host: Mono's mallocs, the IL buffers the host reads, the BCL's native shims) into the
 * binary trace $NEWLIB_FRAG_TRACE, for replay.c, which replays it through dlmalloc on an sbrk-only heap (newlib's shape on
 * the Vita) to measure the fragmentation term of the newlib heap.
 * One record per call, written under one lock that is held across the real call too, so the trace order is the real
 * order (a pointer cannot be reused by another thread between its free and the free's record). Calls before the
 * constructor ran are not recorded (replay.c ignores frees of pointers it never saw). Mac-only measurement tool.
 */
#include <fcntl.h>
#include <os/lock.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <malloc/malloc.h>

typedef struct { uint32_t op, pad; uint64_t p, n, q; } Rec;   /* 1 malloc(n)->p, 2 free(p), 3 realloc(p,n)->q, 4 memalign(q,n)->p */

static os_unfair_lock lk = OS_UNFAIR_LOCK_INIT;
static int fd = -1;
#define NBUF 16384                                       /* small: the host ends with _exit, which loses the last buffer */
static Rec buf [NBUF];
static unsigned nbuf;

__attribute__((constructor)) static void open_trace (void)
{
	const char *path = getenv ("NEWLIB_FRAG_TRACE");
	if (path)
		fd = open (path, O_WRONLY | O_CREAT | O_TRUNC, 0644);
}

__attribute__((destructor)) static void close_trace (void)
{
	if (fd < 0)
		return;
	os_unfair_lock_lock (&lk);
	if (nbuf)
		write (fd, buf, nbuf * sizeof (Rec));
	nbuf = 0;
	os_unfair_lock_unlock (&lk);
}

static inline void put (uint32_t op, const void *p, size_t n, uint64_t q)
{
	if (fd < 0)
		return;
	Rec *r = &buf [nbuf++];
	r->op = op; r->pad = 0; r->p = (uint64_t) (uintptr_t) p; r->n = n; r->q = q;
	if (nbuf == NBUF) {
		write (fd, buf, sizeof (buf));
		nbuf = 0;
	}
}

/* The real call runs under the lock. libsystem_malloc calls its own entry points through interposable stubs (reallocf ->
 * realloc), so a nested call on the owning thread passes straight through: the outer call records the net effect. */
static pthread_t owner;
#define ENTER(passthrough) \
	if (fd < 0 || pthread_equal (owner, pthread_self ())) return passthrough; \
	os_unfair_lock_lock (&lk); owner = pthread_self ()
#define LEAVE() owner = NULL; os_unfair_lock_unlock (&lk)

static void *t_malloc (size_t n) { ENTER (malloc (n)); void *p = malloc (n); put (1, p, n, 0); LEAVE (); return p; }
static void *t_calloc (size_t c, size_t n) { ENTER (calloc (c, n)); void *p = calloc (c, n); put (1, p, c * n, 0); LEAVE (); return p; }
static void t_free (void *p) { if (!p) return; if (fd < 0 || pthread_equal (owner, pthread_self ())) { free (p); return; } os_unfair_lock_lock (&lk); owner = pthread_self (); free (p); put (2, p, 0, 0); LEAVE (); }
static void *t_realloc (void *p, size_t n) { ENTER (realloc (p, n)); void *q = realloc (p, n); put (3, p, n, (uint64_t) (uintptr_t) q); LEAVE (); return q; }
static void *t_reallocf (void *p, size_t n) { ENTER (reallocf (p, n)); void *q = reallocf (p, n); put (3, p, n, (uint64_t) (uintptr_t) q); LEAVE (); return q; }
static void *t_valloc (size_t n) { ENTER (valloc (n)); void *p = valloc (n); put (4, p, n, 4096); LEAVE (); return p; }
static void *t_aligned_alloc (size_t a, size_t n) { ENTER (aligned_alloc (a, n)); void *p = aligned_alloc (a, n); put (4, p, n, a); LEAVE (); return p; }
static int t_posix_memalign (void **r, size_t a, size_t n)
{
	ENTER (posix_memalign (r, a, n));
	int e = posix_memalign (r, a, n);
	if (!e)
		put (4, *r, n, a);
	LEAVE ();
	return e;
}

#define INTERPOSE(rep, orig) \
	__attribute__((used)) static struct { const void *r, *o; } interpose_##orig __attribute__((section ("__DATA,__interpose"))) = { (const void *) &rep, (const void *) &orig }
INTERPOSE (t_malloc, malloc);
INTERPOSE (t_calloc, calloc);
INTERPOSE (t_free, free);
INTERPOSE (t_realloc, realloc);
INTERPOSE (t_reallocf, reallocf);
INTERPOSE (t_valloc, valloc);
INTERPOSE (t_aligned_alloc, aligned_alloc);
INTERPOSE (t_posix_memalign, posix_memalign);
