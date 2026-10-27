/* SHEPHERD-VITA Stage C: the audio pool -- one fixed block of memory (on the Vita a USER_RW memblock carved out of the Mono
 * arena's share, vita/notes/m1-audio.md) serving shepaudio's alloc/free hooks: the short-take cache and the stream rings. A
 * first-fit allocator with boundary tags and immediate coalescing; 16-byte alignment; thread-safe through the lock hooks
 * (the Vita host passes a SceKernel LwMutex: shepaudio's IO thread is a SceKernel thread, not a pthread). Portable C99: the
 * Mac test (mac-check/test_audio_pool.c) runs the same file. Never touches newlib's heap. */
#ifndef SHEPHERD_AUDIO_POOL_H
#define SHEPHERD_AUDIO_POOL_H
#include <stddef.h>
#include <stdint.h>

typedef struct ap_pool {
	uint8_t *base;            /* first block header (16-aligned) */
	uint32_t size;            /* bytes from base to the end sentinel */
	uint32_t used, peak;      /* bytes in used blocks, headers included */
	uint32_t allocs, frees, failures;
	void (*lock) (void *);
	void (*unlock) (void *);
	void *lock_arg;
} ap_pool;

/* 0 on success (the pool needs at least 64 bytes); lock/unlock may be NULL (single-threaded use) */
int ap_init (ap_pool *p, void *mem, size_t bytes, void (*lock) (void *), void (*unlock) (void *), void *lock_arg);
void *ap_alloc (ap_pool *p, size_t bytes, size_t align);   /* align <= 16; NULL when nothing fits (counted) */
void ap_free (ap_pool *p, void *ptr);                     /* NULL is ignored */
/* consistency walk (tests): returns 0 when every header chain/prev link/size is coherent and no two free blocks touch;
 * fills the largest free block and the free block count */
int ap_check (ap_pool *p, uint32_t *largest_free, uint32_t *free_blocks);
#endif
