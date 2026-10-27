/*
 * SHEPHERD-VITA: libSystem.Native for the PS Vita (static, linked into the eboot).
 *
 * Implements the SystemNative_* entry points the .NET 8 BCL (CoreLib + System.Console + friends) reaches for startup,
 * console output, file reads/writes, time, randomness, environment and threading, on top of sceIo/sceKernel/newlib.
 * Prototypes and data layouts come from the upstream headers (src/native/libs/System.Native/pal_*.h, MIT), so the
 * managed side sees exactly the ABI it expects. Everything that cannot exist on the Vita (signals, terminals, users,
 * symlinks, processes) fails with ENOTSUP/ENOSYS or reports "not a terminal", like a minimal POSIX system would.
 *
 * File descriptors: real files and directories are sceIo UIDs (always > 0x1000). 0/1/2 are the standard streams;
 * Dup() of a standard stream returns 1000+n. Writes to 1/2 go to shepherd_vita_console_write (weak; the host
 * overrides it to reach its log file and the debug screen).
 * Paths: sceIo needs device paths ("ux0:data/x", "app0:/x"). Relative paths are resolved against the emulated
 * current directory (initially "app0:"). Because Path.GetFullPath on Unix treats "ux0:data/x" as relative, it
 * arrives here as "/app0:/ux0:data/x": the LAST "<device>:" component wins. GetCwd reports the cwd ROOTED ("/app0:",
 * V6): Path.GetFullPath then yields paths that start with '/', so it is idempotent (a second GetFullPath no longer
 * prepends the cwd again) and AssemblyLoadContext/IsPathFullyQualified accept them. The Vita host passes its
 * ux0:data paths the same way ("/ux0:data/shepherd/...").
 * File identity: sceIo has no inode numbers, but .NET compares FileStatus Dev+Ino to decide whether two paths are
 * the same file (File.Replace, File.Move, Directory.Move, FileStream DeleteOnClose). Dev/Ino are synthesized from
 * the normalized, case-folded device path (exFAT is case-insensitive); FStat uses the path the fd was opened with.
 */
#include "pal_config.h"
#include "pal_errno.h"
#include "pal_io.h"
#include "pal_memory.h"
#include "pal_time.h"
#include "pal_datetime.h"
#include "pal_random.h"
#include "pal_threading.h"
#include "pal_console.h"
#include "pal_signal.h"
#include "pal_environment.h"
#include "pal_process.h"
#include "pal_uid.h"
#include "pal_log.h"
#include "pal_mount.h"
#include "pal_runtimeinformation.h"
#include "pal_networking.h"
#include "pal_dynamicload.h"
#include "pal_string.h"

#include <errno.h>
#include <malloc.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <time.h>

#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/io/dirent.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/rng.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/rtc.h>

extern char **environ;

#define VITA_STD_DUP_BASE 1000
#define VITA_PATH_MAX 1024

/* ------------------------------------------------------------------ host hooks (weak defaults) */

__attribute__((weak)) void shepherd_vita_console_write (int fd, const char *buf, int len)
{
	sceClibPrintf ("%.*s", len, buf);
}

__attribute__((weak)) void shepherd_vita_native_log (const char *msg)
{
	sceClibPrintf ("%s\n", msg);
}

/* ------------------------------------------------------------------ helpers */

static int sce_to_errno (int res)
{
	/* SCE errno-class errors are 0x8001xxxx with the POSIX errno number in the low bits */
	if (((unsigned)res & 0xFFFF0000u) == 0x80010000u)
		return res & 0xFFFF;
	return EIO;
}

static int fail_sce (int res)
{
	errno = sce_to_errno (res);
	return -1;
}

static int fail_errno (int e)
{
	errno = e;
	return -1;
}

static int is_std_fd (intptr_t fd)
{
	return (fd >= 0 && fd <= 2) || (fd >= VITA_STD_DUP_BASE && fd <= VITA_STD_DUP_BASE + 2);
}

static int std_index (intptr_t fd)
{
	return fd >= VITA_STD_DUP_BASE ? (int)(fd - VITA_STD_DUP_BASE) : (int)fd;
}

static pthread_mutex_t cwd_lock = PTHREAD_MUTEX_INITIALIZER;
static char cwd [VITA_PATH_MAX] = "app0:";

/* ---- BEGIN pure path helpers (vita/runtime/tests/run-tests.sh extracts this block into the Mac unit test) ---- */
/* Device paths look like "ux0:..." / "app0:..."; a component ending in ':' marks a device root. */
static int resolve_path (const char *in, char *out, size_t outsize)
{
	if (!in || !*in)
		return fail_errno (ENOENT);
	const char *root = NULL;
	for (const char *p = in; *p; ++p) {
		if (*p == ':' && (p == in || p[-1] != '/')) {
			/* find the start of this component */
			const char *s = p;
			while (s > in && s[-1] != '/')
				--s;
			root = s;
		}
	}
	size_t n;
	if (root) {
		n = strlcpy (out, root, outsize);
	} else {
		pthread_mutex_lock (&cwd_lock);
		const char *sep = (cwd [strlen (cwd) - 1] == '/' || cwd [strlen (cwd) - 1] == ':') ? "" : "/";
		n = (size_t) snprintf (out, outsize, "%s%s%s", cwd, sep, in [0] == '/' ? in + 1 : in);
		pthread_mutex_unlock (&cwd_lock);
	}
	if (n >= outsize)
		return fail_errno (ENAMETOOLONG);
	return 0;
}

static char fold_ascii (char c)
{
	return c >= 'A' && c <= 'Z' ? (char) (c - 'A' + 'a') : c;
}

/* Canonical form of a resolved device path: "<dev>:" + "/component" for each component, with empty and "."
 * components dropped and ".." applied (never above the device root); with fold, ASCII letters are lower-cased
 * (exFAT compares names case-insensitively; non-ASCII names are compared as bytes). Returns the length; *devlen
 * gets the length of the "<dev>:" prefix (0 without a device). */
static size_t normalize_path (const char *path, char *key, size_t keysize, int fold, size_t *devlen)
{
	size_t k = 0;
	const char *rest = path;
	const char *colon = strchr (path, ':');
	if (colon) {
		for (const char *c = path; c < colon && k + 2 < keysize; ++c)
			key [k++] = fold ? fold_ascii (*c) : *c;
		key [k++] = ':';
		rest = colon + 1;
	}
	size_t root = k;
	for (const char *p = rest; *p;) {
		while (*p == '/')
			p++;
		if (!*p)
			break;
		const char *c = p;
		while (*p && *p != '/')
			p++;
		size_t n = (size_t) (p - c);
		if (n == 1 && c [0] == '.')
			continue;
		if (n == 2 && c [0] == '.' && c [1] == '.') {
			while (k > root && key [k - 1] != '/')
				k--;
			if (k > root)
				k--;
			continue;
		}
		if (k + 1 + n + 1 > keysize)
			n = keysize > k + 2 ? keysize - k - 2 : 0;
		key [k++] = '/';
		for (size_t i = 0; i < n; ++i)
			key [k++] = fold ? fold_ascii (c [i]) : c [i];
	}
	key [k] = 0;
	*devlen = root;
	return k;
}

static uint64_t fnv1a64 (const char *s, size_t n)
{
	uint64_t h = 0xcbf29ce484222325ull;
	for (size_t i = 0; i < n; ++i) {
		h ^= (unsigned char) s [i];
		h *= 0x100000001b3ull;
	}
	return h;
}

/* FileStatus Dev/Ino of a resolved device path: Dev = the device, Ino = the case-folded canonical path, so
 * "ux0:data/A.txt", "UX0:/data//a.txt" and "ux0:data/x/../a.txt" are the same file, as on the exFAT volume. */
static void path_identity (const char *path, int64_t *dev, int64_t *ino)
{
	char key [VITA_PATH_MAX];
	size_t devlen;
	size_t n = normalize_path (path, key, sizeof (key), 1, &devlen);
	uint64_t d = fnv1a64 (key, devlen) & 0x7fffffffffffffffull;
	uint64_t i = fnv1a64 (key, n) & 0x7fffffffffffffffull;
	*dev = (int64_t) (d ? d : 1);
	*ino = (int64_t) (i ? i : 1);
}

/* The cwd as managed code sees it: "/" + the device path ("app0:" -> "/app0:"). resolve_path drops the '/' again (the
 * device component wins), so every path built from it resolves to the same sceIo path. Returns the length, or -1. */
static int managed_cwd (const char *dir, char *out, size_t outsize)
{
	size_t n = strlen (dir);
	if (n + 2 > outsize)
		return -1;
	out [0] = '/';
	memcpy (out + 1, dir, n + 1);
	return (int) (n + 1);
}
/* ---- END pure path helpers ---- */

/* ---- BEGIN pure redirect (device kit #5 B-2; vita/runtime/tests/test_redirect.c compiles this block, with the path helpers, on the Mac) ----
 * One armed redirect for the engine's JSON bake (engine/Shepherd.Engine/Runtime/JsonBake.cs): the NEXT read-only open of one file is
 * served from another file, once. The engine arms it just before the game reads its Addressables catalog, so the game's
 * File.ReadAllText reads the engine's small token file instead of the 9.9 MB document; the engine then fills the game's
 * ContentCatalogData from its bake (no 19 MiB string, no parse). A match is by the shim's file identity (path_identity), so every
 * spelling of the path matches; an open for writing, creating or truncating never matches; the first match disarms it. */
typedef struct { int armed; int64_t dev, ino; char target [VITA_PATH_MAX]; int hits; } redirect_state;
/* from / target: resolved device paths. 0 = armed, -1 = a path too long */
static int redirect_arm (redirect_state *r, const char *from, const char *target)
{
	size_t n = strlen (target);
	if (n >= sizeof (r->target))
		return -1;
	path_identity (from, &r->dev, &r->ino);
	memcpy (r->target, target, n + 1);
	r->armed = 1;
	return 0;
}
/* 1 = this open (resolved path p, buffer of psize bytes) takes the redirect: p now holds the target and the redirect is spent */
static int redirect_take (redirect_state *r, char *p, size_t psize, int rdonly_plain)
{
	if (!r->armed || !rdonly_plain)
		return 0;
	int64_t dev, ino;
	path_identity (p, &dev, &ino);
	if (dev != r->dev || ino != r->ino || strlen (r->target) >= psize)
		return 0;
	strcpy (p, r->target);
	r->armed = 0;
	r->hits++;
	return 1;
}
/* ---- END pure redirect ---- */

static int64_t datetime_to_unix (const SceDateTime *dt)
{
	time_t t = 0;
	if (sceRtcGetTime_t (dt, &t) < 0)
		return 0;
	return (int64_t) t;
}

static void convert_stat (const SceIoStat *st, int64_t dev, int64_t ino, FileStatus *out)
{
	memset (out, 0, sizeof (*out));
	int32_t type = SCE_S_ISDIR (st->st_mode) ? PAL_S_IFDIR : PAL_S_IFREG;
	int32_t perm = 0;
	if (st->st_mode & (SCE_S_IRUSR | SCE_S_IRSYS))
		perm |= PAL_S_IRUSR | PAL_S_IRGRP | PAL_S_IROTH;
	if (st->st_mode & (SCE_S_IWUSR | SCE_S_IWSYS))
		perm |= PAL_S_IWUSR | PAL_S_IWGRP | PAL_S_IWOTH;
	if (type == PAL_S_IFDIR)
		perm |= PAL_S_IXUSR | PAL_S_IXGRP | PAL_S_IXOTH;
	if (!perm)
		perm = type == PAL_S_IFDIR ? 0777 : 0666;
	out->Mode = type | perm;
	out->Size = st->st_size;
	out->ATime = datetime_to_unix (&st->st_atime);
	out->MTime = datetime_to_unix (&st->st_mtime);
	out->CTime = datetime_to_unix (&st->st_mtime);
	out->BirthTime = datetime_to_unix (&st->st_ctime);
	out->Flags = FILESTATUS_FLAGS_HAS_BIRTHTIME;
	out->Dev = dev;
	out->Ino = ino;
}

/* fd -> resolved path of EVERY fd SystemNative_Open returns: FStat derives Dev/Ino from it (it must match Stat of
 * the same path, or FileStream's DeleteOnClose check re-opens forever), and the FTruncate fallback reopens it.
 * An open file keeps the identity of the path it was opened with (a rename while it is open is not followed). */
typedef struct { SceUID fd; char *path; } VitaFdPath;
static VitaFdPath *fd_paths;
static int n_fd_paths, cap_fd_paths;
static pthread_mutex_t fd_paths_lock = PTHREAD_MUTEX_INITIALIZER;

static int fd_path_set (SceUID fd, const char *path)
{
	char *copy = strdup (path);
	if (!copy)
		return -1;
	pthread_mutex_lock (&fd_paths_lock);
	if (n_fd_paths == cap_fd_paths) {
		int ncap = cap_fd_paths ? cap_fd_paths * 2 : 32;
		VitaFdPath *t = (VitaFdPath *) realloc (fd_paths, (size_t) ncap * sizeof (*t));
		if (!t) {
			pthread_mutex_unlock (&fd_paths_lock);
			free (copy);
			return -1;
		}
		fd_paths = t;
		cap_fd_paths = ncap;
	}
	fd_paths [n_fd_paths].fd = fd;
	fd_paths [n_fd_paths].path = copy;
	n_fd_paths++;
	pthread_mutex_unlock (&fd_paths_lock);
	return 0;
}

static void fd_path_clear (SceUID fd)
{
	pthread_mutex_lock (&fd_paths_lock);
	for (int i = 0; i < n_fd_paths; ++i) {
		if (fd_paths [i].fd == fd) {
			free (fd_paths [i].path);
			fd_paths [i] = fd_paths [--n_fd_paths];
			break;
		}
	}
	pthread_mutex_unlock (&fd_paths_lock);
}

static int fd_path_get (SceUID fd, char *out, size_t outsize)
{
	int found = 0;
	pthread_mutex_lock (&fd_paths_lock);
	for (int i = 0; i < n_fd_paths; ++i) {
		if (fd_paths [i].fd == fd) {
			strlcpy (out, fd_paths [i].path, outsize);
			found = 1;
			break;
		}
	}
	pthread_mutex_unlock (&fd_paths_lock);
	return found;
}

/* kit #8 (save backups' pruning, pal_vita_savebak.inc.c): 1 = some fd is open on this resolved path (exFAT: case-insensitive) */
static int fd_path_open (const char *path)
{
	int found = 0;
	pthread_mutex_lock (&fd_paths_lock);
	for (int i = 0; i < n_fd_paths && !found; ++i)
		found = !strcasecmp (fd_paths [i].path, path);
	pthread_mutex_unlock (&fd_paths_lock);
	return found;
}

static void std_stat (FileStatus *out)
{
	memset (out, 0, sizeof (*out));
	out->Mode = PAL_S_IFCHR | 0666;
	out->Dev = 2;
}

/* ------------------------------------------------------------------ errno */

int32_t SystemNative_ConvertErrorPlatformToPal (int32_t platformErrno)
{
	return ConvertErrorPlatformToPal (platformErrno);
}

int32_t SystemNative_ConvertErrorPalToPlatform (int32_t error)
{
	return ConvertErrorPalToPlatform (error);
}

const char *SystemNative_StrErrorR (int32_t platformErrno, char *buffer, int32_t bufferSize)
{
	return StrErrorR (platformErrno, buffer, bufferSize);
}

int32_t SystemNative_GetErrNo (void)
{
	return errno;
}

void SystemNative_SetErrNo (int32_t errorCode)
{
	errno = errorCode;
}

/* ------------------------------------------------------------------ memory */

void *SystemNative_Malloc (uintptr_t size) { return malloc (size); }
void *SystemNative_Calloc (uintptr_t num, uintptr_t size) { return calloc (num, size); }
void *SystemNative_Realloc (void *ptr, uintptr_t new_size) { return realloc (ptr, new_size); }
void SystemNative_Free (void *ptr) { free (ptr); }
void *SystemNative_AlignedAlloc (uintptr_t alignment, uintptr_t size) { return memalign (alignment, size ? size : 1); }
void SystemNative_AlignedFree (void *ptr) { free (ptr); }

void *SystemNative_AlignedRealloc (void *ptr, uintptr_t alignment, uintptr_t size)
{
	void *res = memalign (alignment, size ? size : 1);
	if (res && ptr) {
		size_t old = malloc_usable_size (ptr);
		memcpy (res, ptr, old < size ? old : size);
		free (ptr);
	}
	return res;
}

/* ------------------------------------------------------------------ read-ahead (kit #4 B-3)
 * The card serves one request at a time at ~11 MiB/s and every request costs a few ms on top (3.25 ms per random 4 KiB read,
 * probe v2): small reads are what is slow, and the BCL makes a lot of them -- a FileStream / StreamReader reads 4 KiB per call, so
 * the boot's JSON documents (RuntimeInitializeOnLoads, the Addressables catalog and settings, the game's own settings and the 9.9 M
 * character JsonUtility document) came in as ~2,400 pread calls (device diag counters, RuntimeInit). For a file opened read-only,
 * a small read (<= 16 KiB) that continues where the previous one ended fills a per-fd buffer of SHEPHERD_IO_READAHEAD_KB (default
 * 128; 0 = off, every read straight to the card as before) and the next small reads are copied from it: the same bytes, ~30x fewer
 * requests. A file read once in one small read gets no buffer; big reads (the pack's windows) never touch it. A read never comes back
 * shorter than it would from the card (a request that is not wholly in the buffer refills it from its own offset), and EOF stays the
 * card's answer: only a fill that returned 0 bytes marks the end (k4 review: a fill that came back short is NOT taken as the end of the
 * file -- a card that serves less than asked would otherwise have ended a sequential reader early; the next read goes to the card). A write to the same file through any fd (Write/PWrite/FTruncate, an open for writing, Unlink, Rename) drops the
 * buffers of that file; Close frees the fd's. Counters: shepherd_vita_io_readahead_stats (the host's END IO line). */
/* BEGIN pure read-ahead (vita/runtime/tests/test_readahead.c compiles this block on the Mac) */
typedef int (*RaPreadFn) (intptr_t fd, void *buf, int32_t size, int64_t off);
typedef struct { intptr_t fd; int64_t dev, ino; int64_t start, next; int32_t len; int eof; char *buf; } RaSlot;
#define RA_SLOTS 8
#define RA_SMALL (16 * 1024)
static RaSlot ra_slots [RA_SLOTS];
static pthread_mutex_t ra_lock = PTHREAD_MUTEX_INITIALIZER;
static int32_t ra_size = -1;   /* bytes; 0 = off; -1 = not read from the environment yet */
static unsigned ra_calls, ra_served, ra_fills, ra_direct, ra_untracked, ra_invalidated;
static unsigned long long ra_fill_bytes;

static void ra_configure (const char *kib)
{
	long v = kib && *kib ? strtol (kib, NULL, 10) : 128;
	ra_size = v <= 0 ? 0 : (int32_t) (v > 4096 ? 4096 : v) * 1024;
}

static RaSlot *ra_find (intptr_t fd)
{
	for (int i = 0; i < RA_SLOTS; ++i)
		if (ra_slots [i].fd == fd)
			return &ra_slots [i];
	return NULL;
}

/* a read-only fd was opened: track it (no buffer until a second, sequential small read) */
static void ra_track (intptr_t fd, int64_t dev, int64_t ino)
{
	pthread_mutex_lock (&ra_lock);
	RaSlot *s = ra_find (0);
	if (s) {
		memset (s, 0, sizeof (*s));
		s->fd = fd; s->dev = dev; s->ino = ino; s->start = -1; s->next = -1;
	} else
		ra_untracked++;
	pthread_mutex_unlock (&ra_lock);
}

static void ra_close (intptr_t fd)
{
	pthread_mutex_lock (&ra_lock);
	RaSlot *s = ra_find (fd);
	if (s) {
		free (s->buf);
		memset (s, 0, sizeof (*s));
	}
	pthread_mutex_unlock (&ra_lock);
}

/* the file (dev, ino) is being changed: drop what any read-only fd buffered of it */
static void ra_invalidate (int64_t dev, int64_t ino)
{
	pthread_mutex_lock (&ra_lock);
	for (int i = 0; i < RA_SLOTS; ++i) {
		RaSlot *s = &ra_slots [i];
		if (s->fd && s->dev == dev && s->ino == ino && s->len) {
			s->start = -1; s->len = 0; s->eof = 0; s->next = -1;
			ra_invalidated++;
		}
	}
	pthread_mutex_unlock (&ra_lock);
}

static int32_t ra_pread (intptr_t fd, void *buffer, int32_t size, int64_t off, RaPreadFn real)
{
	if (ra_size <= 0 || size <= 0 || size > RA_SMALL || size > ra_size || off < 0)
		return real (fd, buffer, size, off);
	pthread_mutex_lock (&ra_lock);
	RaSlot *s = ra_find (fd);
	if (!s) {
		pthread_mutex_unlock (&ra_lock);
		return real (fd, buffer, size, off);
	}
	ra_calls++;
	if (s->len > 0 && off >= s->start && off + size <= s->start + s->len) {   /* wholly in the buffer */
		memcpy (buffer, s->buf + (off - s->start), (size_t) size);
		s->next = off + size;
		ra_served++;
		pthread_mutex_unlock (&ra_lock);
		return size;
	}
	if (s->eof && off == s->start) {   /* the card said "end of file" at this offset (a 0-byte fill): it still is */
		int32_t n = (int32_t) (s->start + s->len - off);
		memcpy (buffer, s->buf + (off - s->start), (size_t) n);
		s->next = off + n;
		ra_served++;
		pthread_mutex_unlock (&ra_lock);
		return n;
	}
	if (off == s->next) {   /* a sequential continuation: fill from this offset (a jump elsewhere reads straight, below) */
		if (!s->buf && !(s->buf = (char *) malloc ((size_t) ra_size))) {
			pthread_mutex_unlock (&ra_lock);
			return real (fd, buffer, size, off);
		}
		int r = real (fd, s->buf, ra_size, off);
		if (r < 0) {
			s->start = -1; s->len = 0; s->eof = 0;
			pthread_mutex_unlock (&ra_lock);
			return r;
		}
		ra_fills++;
		ra_fill_bytes += (unsigned) r;
		s->start = off; s->len = r; s->eof = r == 0;   /* a short fill is not the end: only the card's 0 is (k4 review) */
		int32_t n = r < size ? r : size;
		memcpy (buffer, s->buf, (size_t) n);
		s->next = off + n;
		ra_served++;
		pthread_mutex_unlock (&ra_lock);
		return n;
	}
	/* the fd's first small read, or a jump elsewhere (a reader that seeks around pays no fills): straight to the card, remembering
	 * where a sequential reader would go on */
	int r = real (fd, buffer, size, off);
	if (r > 0)
		s->next = off + r;
	ra_direct++;
	pthread_mutex_unlock (&ra_lock);
	return r;
}
/* END pure read-ahead */

static int ra_on (void)
{
	if (ra_size < 0)
		ra_configure (getenv ("SHEPHERD_IO_READAHEAD_KB"));
	return ra_size > 0;
}

static void ra_invalidate_path (const char *p)
{
	int64_t dev, ino;
	path_identity (p, &dev, &ino);
	ra_invalidate (dev, ino);
}

static void ra_invalidate_fd (intptr_t fd)
{
	char p [VITA_PATH_MAX];
	if (fd_path_get ((SceUID) fd, p, sizeof (p)))
		ra_invalidate_path (p);
}

static int sce_pread (intptr_t fd, void *buf, int32_t size, int64_t off)
{
	return sceIoPread ((SceUID) fd, buf, (SceSize) size, off);
}

void shepherd_vita_io_readahead_stats (unsigned *calls, unsigned *served, unsigned *fills, unsigned long long *fill_bytes, unsigned *direct,
				       int32_t *size_kib)
{
	pthread_mutex_lock (&ra_lock);
	*calls = ra_calls; *served = ra_served; *fills = ra_fills; *fill_bytes = ra_fill_bytes; *direct = ra_direct;
	*size_kib = ra_size < 0 ? -1 : ra_size / 1024;
	pthread_mutex_unlock (&ra_lock);
}

/* ------------------------------------------------------------------ files */

static redirect_state g_redirect;
static pthread_mutex_t redirect_lock = PTHREAD_MUTEX_INITIALIZER;

/* kit #5 B-2: the engine's one-shot read redirect (static P/Invoke table, the shepherd_vita_engine_ prefix). from + to: the next
 * read-only open of `from` reads `to` (paths as managed code passes them); from or to NULL / empty: disarm. Returns 1 armed, 0 disarmed
 * (*was_armed = whether it was still armed, i.e. never taken), -1 a path that does not resolve. */
int32_t shepherd_vita_engine_redirect (const char *from, const char *to, int32_t *was_armed)
{
	char a [VITA_PATH_MAX], b [VITA_PATH_MAX];
	pthread_mutex_lock (&redirect_lock);
	if (was_armed)
		*was_armed = g_redirect.armed;
	int res;
	if (!from || !*from || !to || !*to) {
		g_redirect.armed = 0;
		res = 0;
	} else
		res = resolve_path (from, a, sizeof (a)) < 0 || resolve_path (to, b, sizeof (b)) < 0 || redirect_arm (&g_redirect, a, b) < 0 ? -1 : 1;
	pthread_mutex_unlock (&redirect_lock);
	return res;
}
/* how many opens took the redirect so far */
int32_t shepherd_vita_engine_redirect_hits (void) { return g_redirect.hits; }

/* device kit #8, track play: save backups + the read-back check of every save write (SHEPHERD_K8_SAVEBAK=0: off) */
#include "pal_vita_savebak.inc.c"

intptr_t SystemNative_Open (const char *path, int32_t flags, int32_t mode)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	if (g_redirect.armed) {   /* kit #5 B-2 (a plain int read first: an unarmed open costs nothing) */
		pthread_mutex_lock (&redirect_lock);
		redirect_take (&g_redirect, p, sizeof (p), (flags & PAL_O_ACCESS_MODE_MASK) == PAL_O_RDONLY && !(flags & (PAL_O_CREAT | PAL_O_TRUNC)));
		pthread_mutex_unlock (&redirect_lock);
	}
	int f;
	switch (flags & PAL_O_ACCESS_MODE_MASK) {
	case PAL_O_RDONLY: f = SCE_O_RDONLY; break;
	case PAL_O_WRONLY: f = SCE_O_WRONLY; break;
	case PAL_O_RDWR: f = SCE_O_RDWR; break;
	default: return fail_errno (EINVAL);
	}
	if (flags & PAL_O_CREAT) f |= SCE_O_CREAT;
	if (flags & PAL_O_EXCL) f |= SCE_O_EXCL;
	if (flags & PAL_O_TRUNC) f |= SCE_O_TRUNC;
	/* O_CLOEXEC/O_SYNC/O_NOFOLLOW: meaningless on the Vita */
	SceIoStat st;
	int exists = sceIoGetstat (p, &st) >= 0;
	if (exists && SCE_S_ISDIR (st.st_mode)) {
		/* POSIX lets you open a directory read-only; .NET then rejects it via FStat. sceIo cannot. */
		return fail_errno ((flags & PAL_O_ACCESS_MODE_MASK) == PAL_O_RDONLY ? EACCES : EISDIR);
	}
	/* FileMode.CreateNew (and File.Copy/Move without overwrite) depend on O_CREAT|O_EXCL failing with EEXIST; do not
	 * rely on sceIo for it (single process, so the check-then-open window does not matter) */
	if (exists && (flags & PAL_O_CREAT) && (flags & PAL_O_EXCL))
		return fail_errno (EEXIST);
	if ((flags & PAL_O_ACCESS_MODE_MASK) != PAL_O_RDONLY || (flags & PAL_O_TRUNC))
		ra_invalidate_path (p);   /* read-ahead: a writer of this file appears */
	int sbw = g_sb_state >= 0 && (flags & PAL_O_ACCESS_MODE_MASK) != PAL_O_RDONLY && (flags & (PAL_O_TRUNC | PAL_O_CREAT)) && sb_name (p);   /* kit #8 */
	if (sbw && exists)
		sb_before_write (p);   /* kit #8: the save file as it is, into save-backups/, before this open replaces it */
	SceUID fd = sceIoOpen (p, f, 0777);
	if (fd < 0)
		return fail_sce (fd);
	if (fd_path_set (fd, p) != 0) {
		sceIoClose (fd);
		return fail_errno (ENOMEM);
	}
	if (sbw)
		sb_track (fd, p);   /* kit #8: follow what the game writes (read back + parsed at the close) */
	if ((flags & PAL_O_ACCESS_MODE_MASK) == PAL_O_RDONLY && ra_on ()) {
		int64_t dev, ino;
		path_identity (p, &dev, &ino);
		ra_track (fd, dev, ino);
	}
	return fd;
}

int32_t SystemNative_Close (intptr_t fd)
{
	if (is_std_fd (fd))
		return 0;
	ra_close (fd);
	char sbp [VITA_PATH_MAX];
	int sb = g_sb_n && fd_path_get ((SceUID) fd, sbp, sizeof (sbp));   /* kit #8: a followed save write (the path, before it is forgotten) */
	fd_path_clear ((SceUID) fd);
	int res = sceIoClose ((SceUID) fd);
	if (g_sb_n)
		sb_closed ((SceUID) fd, sb ? sbp : NULL);   /* kit #8: read back, compare, parse */
	return res < 0 ? fail_sce (res) : 0;
}

intptr_t SystemNative_Dup (intptr_t oldfd)
{
	if (is_std_fd (oldfd))
		return VITA_STD_DUP_BASE + std_index (oldfd);
	return fail_errno (ENOTSUP);
}

int32_t SystemNative_Read (intptr_t fd, void *buffer, int32_t bufferSize)
{
	if (is_std_fd (fd))
		return 0; /* stdin is always at EOF */
	int res = sceIoRead ((SceUID) fd, buffer, (SceSize) bufferSize);
	return res < 0 ? fail_sce (res) : res;
}

int32_t SystemNative_Write (intptr_t fd, const void *buffer, int32_t bufferSize)
{
	if (is_std_fd (fd)) {
		int i = std_index (fd);
		if (i == 0)
			return fail_errno (EBADF);
		shepherd_vita_console_write (i, (const char *) buffer, bufferSize);
		return bufferSize;
	}
	ra_invalidate_fd (fd);
	int res = sceIoWrite ((SceUID) fd, buffer, (SceSize) bufferSize);
	if (g_sb_n && res > 0)
		sb_wrote ((SceUID) fd, buffer, res, -1);   /* kit #8 */
	return res < 0 ? fail_sce (res) : res;
}

int32_t SystemNative_PRead (intptr_t fd, void *buffer, int32_t bufferSize, int64_t fileOffset)
{
	if (is_std_fd (fd))
		return fail_errno (ESPIPE);
	int res = ra_pread (fd, buffer, bufferSize, fileOffset, sce_pread);   /* sceIoPread, through the read-ahead for small reads */
	return res < 0 ? fail_sce (res) : res;
}

int32_t SystemNative_PWrite (intptr_t fd, void *buffer, int32_t bufferSize, int64_t fileOffset)
{
	if (is_std_fd (fd))
		return fail_errno (ESPIPE);
	ra_invalidate_fd (fd);
	int res = sceIoPwrite ((SceUID) fd, buffer, (SceSize) bufferSize, fileOffset);
	if (g_sb_n && res > 0)
		sb_wrote ((SceUID) fd, buffer, res, fileOffset);   /* kit #8 */
	return res < 0 ? fail_sce (res) : res;
}

int64_t SystemNative_PReadV (intptr_t fd, IOVector *vectors, int32_t vectorCount, int64_t fileOffset)
{
	int64_t total = 0;
	for (int32_t i = 0; i < vectorCount; ++i) {
		int32_t n = SystemNative_PRead (fd, vectors [i].Base, (int32_t) vectors [i].Count, fileOffset + total);
		if (n < 0)
			return total ? total : -1;
		total += n;
		if ((uintptr_t) n < vectors [i].Count)
			break;
	}
	return total;
}

int64_t SystemNative_PWriteV (intptr_t fd, IOVector *vectors, int32_t vectorCount, int64_t fileOffset)
{
	int64_t total = 0;
	for (int32_t i = 0; i < vectorCount; ++i) {
		int32_t n = SystemNative_PWrite (fd, vectors [i].Base, (int32_t) vectors [i].Count, fileOffset + total);
		if (n < 0)
			return total ? total : -1;
		total += n;
		if ((uintptr_t) n < vectors [i].Count)
			break;
	}
	return total;
}

int64_t SystemNative_LSeek (intptr_t fd, int64_t offset, int32_t whence)
{
	if (is_std_fd (fd))
		return fail_errno (ESPIPE);
	int w = whence == PAL_SEEK_SET ? SCE_SEEK_SET : whence == PAL_SEEK_CUR ? SCE_SEEK_CUR : SCE_SEEK_END;
	SceOff res = sceIoLseek ((SceUID) fd, offset, w);
	if (res < 0)
		return fail_sce ((int) res);
	return res;
}

int32_t SystemNative_FStat (intptr_t fd, FileStatus *output)
{
	if (is_std_fd (fd)) {
		std_stat (output);
		return 0;
	}
	SceIoStat st;
	int res = sceIoGetstatByFd ((SceUID) fd, &st);
	if (res < 0)
		return fail_sce (res);
	char p [VITA_PATH_MAX];
	int64_t dev, ino;
	if (fd_path_get ((SceUID) fd, p, sizeof (p))) {
		path_identity (p, &dev, &ino);
	} else {
		/* every fd comes from SystemNative_Open, which records its path; keep an untracked one distinct anyway */
		dev = 0;
		ino = (int64_t) ((1ull << 62) | (uint32_t) fd);
	}
	convert_stat (&st, dev, ino, output);
	return 0;
}

int32_t SystemNative_Stat (const char *path, FileStatus *output)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	SceIoStat st;
	int res = sceIoGetstat (p, &st);
	if (res < 0)
		return fail_sce (res);
	int64_t dev, ino;
	path_identity (p, &dev, &ino);
	convert_stat (&st, dev, ino, output);
	return 0;
}

int32_t SystemNative_LStat (const char *path, FileStatus *output)
{
	return SystemNative_Stat (path, output); /* no symlinks */
}

int32_t SystemNative_FTruncate (intptr_t fd, int64_t length)
{
	if (is_std_fd (fd))
		return fail_errno (EINVAL);
	ra_invalidate_fd (fd);
	SceIoStat st;
	memset (&st, 0, sizeof (st));
	st.st_size = length;
	int res = sceIoChstatByFd ((SceUID) fd, &st, SCE_CST_SIZE);
	if (res >= 0)
		return 0;
	/* Fallback for FileMode.Create (FileStream truncates to 0 after locking): reopen the same path with O_TRUNC. */
	char p [VITA_PATH_MAX];
	if (length == 0 && fd_path_get ((SceUID) fd, p, sizeof (p))) {
		SceUID t = sceIoOpen (p, SCE_O_WRONLY | SCE_O_TRUNC, 0777);
		if (t >= 0) {
			sceIoClose (t);
			return 0;
		}
	}
	return fail_sce (res);
}

int32_t SystemNative_FSync (intptr_t fd)
{
	if (is_std_fd (fd))
		return 0;
	int res = sceIoSyncByFd ((SceUID) fd, 0);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_FLock (intptr_t fd, int32_t operation) { return 0; }   /* single process: advisory locks always succeed */
int32_t SystemNative_LockFileRegion (intptr_t fd, int64_t offset, int64_t length, int16_t lockType) { return 0; }
int32_t SystemNative_PosixFAdvise (intptr_t fd, int64_t offset, int64_t length, int32_t advice) { return 0; }
int32_t SystemNative_FAllocate (intptr_t fd, int64_t offset, int64_t length) { return fail_errno (ENOTSUP); }
int32_t SystemNative_FChMod (intptr_t fd, int32_t mode) { return 0; }
int32_t SystemNative_ChMod (const char *path, int32_t mode) { return 0; }
int32_t SystemNative_FChflags (intptr_t fd, uint32_t flags) { return fail_errno (ENOTSUP); }
int32_t SystemNative_LChflags (const char *path, uint32_t flags) { return fail_errno (ENOTSUP); }
int32_t SystemNative_CanGetHiddenFlag (void) { return 0; }
int32_t SystemNative_LChflagsCanSetHiddenFlag (void) { return 0; }
int32_t SystemNative_FUTimens (intptr_t fd, TimeSpec *times) { return fail_errno (ENOTSUP); }
int32_t SystemNative_UTimensat (const char *path, TimeSpec *times) { return fail_errno (ENOTSUP); }
int32_t SystemNative_Link (const char *source, const char *linkTarget) { return fail_errno (ENOTSUP); }
int32_t SystemNative_SymLink (const char *target, const char *linkPath) { return fail_errno (ENOTSUP); }
int32_t SystemNative_ReadLink (const char *path, char *buffer, int32_t bufferSize) { return fail_errno (EINVAL); }
char *SystemNative_MkdTemp (char *pathTemplate) { errno = ENOTSUP; return NULL; }
intptr_t SystemNative_MksTemps (char *pathTemplate, int32_t suffixLength) { return fail_errno (ENOTSUP); }
uint32_t SystemNative_GetFileSystemType (intptr_t fd) { return 0; }

int32_t SystemNative_Access (const char *path, int32_t mode)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	SceIoStat st;
	int res = sceIoGetstat (p, &st);
	if (res < 0)
		return fail_sce (res);
	if ((mode & PAL_W_OK) && !strncmp (p, "app0:", 5))
		return fail_errno (EROFS);
	return 0;
}

int32_t SystemNative_MkDir (const char *path, int32_t mode)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	int res = sceIoMkdir (p, 0777);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_RmDir (const char *path)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	int res = sceIoRmdir (p);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_Unlink (const char *path)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	ra_invalidate_path (p);
	int res = sceIoRemove (p);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_Rename (const char *oldPath, const char *newPath)
{
	char a [VITA_PATH_MAX], b [VITA_PATH_MAX];
	if (resolve_path (oldPath, a, sizeof (a)) < 0 || resolve_path (newPath, b, sizeof (b)) < 0)
		return -1;
	ra_invalidate_path (a);
	ra_invalidate_path (b);
	int64_t da, ia, db, ib;
	path_identity (a, &da, &ia);
	path_identity (b, &db, &ib);
	if (da != db)
		return fail_errno (EXDEV); /* .NET then falls back to copy + delete */
	if (ia == ib) {
		/* The same file (case-insensitive volume): the target must NOT be removed, it is the source. POSIX makes
		 * this a no-op; a change of case only (File.Move ("a.txt", "A.txt")) is a real rename. */
		char na [VITA_PATH_MAX], nb [VITA_PATH_MAX];
		size_t dl;
		normalize_path (a, na, sizeof (na), 0, &dl);
		normalize_path (b, nb, sizeof (nb), 0, &dl);
		if (!strcmp (na, nb))
			return 0;
		int res = sceIoRename (a, b);
		if (res >= 0)
			return 0;
		/* if sceIo refuses a case-only rename, go through a temporary name */
		char t [VITA_PATH_MAX];
		if ((size_t) snprintf (t, sizeof (t), "%s.shepherd-rename", na) >= sizeof (t) || sceIoRename (a, t) < 0)
			return fail_sce (res);
		res = sceIoRename (t, b);
		if (res < 0) {
			sceIoRename (t, a);
			return fail_sce (res);
		}
		return 0;
	}
	SceIoStat st;
	if (sceIoGetstat (b, &st) >= 0 && !SCE_S_ISDIR (st.st_mode))
		sceIoRemove (b); /* POSIX rename replaces the target */
	int res = sceIoRename (a, b);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_CopyFile (intptr_t sourceFd, intptr_t destinationFd, int64_t sourceLength)
{
	static char buf [64 * 1024];
	static pthread_mutex_t lock = PTHREAD_MUTEX_INITIALIZER;
	int64_t done = 0;
	int32_t res = 0;
	pthread_mutex_lock (&lock);
	while (done < sourceLength) {
		int32_t n = SystemNative_PRead (sourceFd, buf, sizeof (buf), done);
		if (n <= 0) {
			res = n < 0 ? -1 : 0;
			break;
		}
		if (SystemNative_PWrite (destinationFd, buf, n, done) != n) {
			res = -1;
			break;
		}
		done += n;
	}
	pthread_mutex_unlock (&lock);
	return res;
}

typedef struct {
	SceUID uid;
	SceIoDirent ent;
} VitaDir;

DIR *SystemNative_OpenDir (const char *path)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return NULL;
	SceUID d = sceIoDopen (p);
	if (d < 0) {
		fail_sce (d);
		return NULL;
	}
	VitaDir *vd = (VitaDir *) calloc (1, sizeof (VitaDir));
	if (!vd) {
		sceIoDclose (d);
		errno = ENOMEM;
		return NULL;
	}
	vd->uid = d;
	return (DIR *) vd;
}

int32_t SystemNative_GetReadDirRBufferSize (void)
{
	return 0; /* the entry lives in the VitaDir, valid until the next ReadDirR on the same handle */
}

int32_t SystemNative_ReadDirR (DIR *dir, uint8_t *buffer, int32_t bufferSize, DirectoryEntry *outputEntry)
{
	VitaDir *vd = (VitaDir *) dir;
	memset (&vd->ent, 0, sizeof (vd->ent));
	int res = sceIoDread (vd->uid, &vd->ent);
	if (res == 0) {
		memset (outputEntry, 0, sizeof (*outputEntry));
		return -1; /* end of directory */
	}
	if (res < 0) {
		memset (outputEntry, 0, sizeof (*outputEntry));
		return sce_to_errno (res);
	}
	outputEntry->Name = vd->ent.d_name;
	outputEntry->NameLength = (int32_t) strlen (vd->ent.d_name);
	outputEntry->InodeType = SCE_S_ISDIR (vd->ent.d_stat.st_mode) ? PAL_DT_DIR : PAL_DT_REG;
	return 0;
}

int32_t SystemNative_CloseDir (DIR *dir)
{
	VitaDir *vd = (VitaDir *) dir;
	int res = sceIoDclose (vd->uid);
	free (vd);
	return res < 0 ? fail_sce (res) : 0;
}

int32_t SystemNative_Poll (PollEvent *pollEvents, uint32_t eventCount, int32_t milliseconds, uint32_t *triggered)
{
	/* files and the console are always ready */
	for (uint32_t i = 0; i < eventCount; ++i)
		pollEvents [i].TriggeredEvents = pollEvents [i].Events & (PAL_POLLIN | PAL_POLLOUT);
	*triggered = eventCount;
	return Error_SUCCESS;
}

int64_t SystemNative_SysConf (int32_t name)
{
	switch (name) {
	case PAL_SC_CLK_TCK: return 100;
	case PAL_SC_PAGESIZE: return 4096;
	default: errno = EINVAL; return -1;
	}
}

int32_t SystemNative_ChDir (const char *path)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (path, p, sizeof (p)) < 0)
		return -1;
	SceIoStat st;
	int res = sceIoGetstat (p, &st);
	if (res < 0)
		return fail_sce (res);
	if (!SCE_S_ISDIR (st.st_mode))
		return fail_errno (ENOTDIR);
	pthread_mutex_lock (&cwd_lock);
	strlcpy (cwd, p, sizeof (cwd));
	pthread_mutex_unlock (&cwd_lock);
	return 0;
}

char *SystemNative_GetCwd (char *buffer, int32_t bufferSize)
{
	pthread_mutex_lock (&cwd_lock);
	char *res = buffer;
	if (bufferSize < 0 || managed_cwd (cwd, buffer, (size_t) bufferSize) < 0) {
		errno = ERANGE;
		res = NULL;
	}
	pthread_mutex_unlock (&cwd_lock);
	return res;
}

int32_t SystemNative_GetAllMountPoints (MountPointFound onFound, void *context)
{
	onFound (context, "app0:");
	onFound (context, "ux0:");
	return 0;
}

/* ------------------------------------------------------------------ console (never a terminal) */

int32_t SystemNative_IsATty (intptr_t fd) { return 0; }
int32_t SystemNative_InitializeTerminalAndSignalHandling (void) { return 1; }
void SystemNative_InitializeConsoleBeforeRead (uint8_t minChars, uint8_t decisecondsTimeout) { }
void SystemNative_UninitializeConsoleAfterRead (void) { }
void SystemNative_SetKeypadXmit (const char *terminfoString) { }
int32_t SystemNative_GetWindowSize (WinSize *windowsSize) { return fail_errno (ENOTTY); }
int32_t SystemNative_SetWindowSize (WinSize *windowsSize) { return fail_errno (ENOTTY); }
int32_t SystemNative_StdinReady (void) { return 0; }
int32_t SystemNative_ReadStdin (void *buffer, int32_t bufferSize) { return 0; }
int32_t SystemNative_GetSignalForBreak (void) { return 0; }
int32_t SystemNative_SetSignalForBreak (int32_t signalForBreak) { return 1; }

void SystemNative_GetControlCharacters (int32_t *controlCharacterNames, uint8_t *controlCharacterValues,
					int32_t controlCharacterLength, uint8_t *posixDisableValue)
{
	memset (controlCharacterValues, 0, (size_t) controlCharacterLength);
	*posixDisableValue = 0;
}

/* ------------------------------------------------------------------ signals: none on the Vita */

static PosixSignalHandler posix_signal_handler;
void SystemNative_SetPosixSignalHandler (PosixSignalHandler signalHandler) { posix_signal_handler = signalHandler; }
int32_t SystemNative_GetPlatformSignalNumber (PosixSignal signal) { return 0; }
int32_t SystemNative_EnablePosixSignalHandling (int signalCode) { return 1; }   /* accepted; never delivered */
void SystemNative_DisablePosixSignalHandling (int signalCode) { }
void SystemNative_HandleNonCanceledPosixSignal (int32_t signalCode) { }
void SystemNative_SetTerminalInvalidationHandler (TerminalInvalidationCallback callback) { }

/* ------------------------------------------------------------------ time */

uint64_t SystemNative_GetTimestamp (void)
{
	/* Stopwatch.Frequency is 1e9 on Unix: nanoseconds of a monotonic clock (process time, 1 us resolution) */
	return (uint64_t) sceKernelGetProcessTimeWide () * 1000u;
}

int64_t SystemNative_GetSystemTimeAsTicks (void)
{
	struct timespec ts;
	if (clock_gettime (CLOCK_REALTIME, &ts) != 0)
		return 0;
	/* 100 ns ticks since 1970-01-01 (the upstream contract, pal_datetime.c: DateTime.UtcNow adds UnixEpochTicks itself;
	 * adding it here too made UtcNow read year 3995 -- seen in vlx, devfail1/repro.md). newlib's CLOCK_REALTIME is
	 * sceRtcGetCurrentClock -> sceRtcGetTime_t: Unix seconds. */
	return (int64_t) ts.tv_sec * 10000000 + ts.tv_nsec / 100;
}

double SystemNative_GetCpuUtilization (ProcessCpuInformation *previousCpuInfo)
{
	/* the thread pool's hill climbing treats 0 as "idle"; there is no per-process CPU accounting here */
	return 0.0;
}

/* ------------------------------------------------------------------ random */

static void vita_random (uint8_t *buffer, int32_t len)
{
	while (len > 0) {
		int32_t n = len > 64 ? 64 : len;
		sceKernelGetRandomNumber (buffer, (SceSize) n);
		buffer += n;
		len -= n;
	}
}

void SystemNative_GetNonCryptographicallySecureRandomBytes (uint8_t *buffer, int32_t bufferLength)
{
	vita_random (buffer, bufferLength);
}

int32_t SystemNative_GetCryptographicallySecureRandomBytes (uint8_t *buffer, int32_t bufferLength)
{
	vita_random (buffer, bufferLength);
	return 0;
}

/* CryptoNative_GetRandomBytes (pal_vita_crypto.c): the same kernel RNG */
void shepherd_vita_crypto_rng (uint8_t *buffer, int32_t len)
{
	vita_random (buffer, len);
}

/* ------------------------------------------------------------------ threading */

struct LowLevelMonitor {
	pthread_mutex_t mutex;
	pthread_cond_t cond;
};

LowLevelMonitor *SystemNative_LowLevelMonitor_Create (void)
{
	LowLevelMonitor *m = (LowLevelMonitor *) malloc (sizeof (LowLevelMonitor));
	if (!m)
		return NULL;
	if (pthread_mutex_init (&m->mutex, NULL) != 0) {
		free (m);
		return NULL;
	}
	if (pthread_cond_init (&m->cond, NULL) != 0) {
		pthread_mutex_destroy (&m->mutex);
		free (m);
		return NULL;
	}
	return m;
}

void SystemNative_LowLevelMonitor_Destroy (LowLevelMonitor *m)
{
	pthread_cond_destroy (&m->cond);
	pthread_mutex_destroy (&m->mutex);
	free (m);
}

void SystemNative_LowLevelMonitor_Acquire (LowLevelMonitor *m) { pthread_mutex_lock (&m->mutex); }
void SystemNative_LowLevelMonitor_Release (LowLevelMonitor *m) { pthread_mutex_unlock (&m->mutex); }
void SystemNative_LowLevelMonitor_Wait (LowLevelMonitor *m) { pthread_cond_wait (&m->cond, &m->mutex); }

int32_t SystemNative_LowLevelMonitor_TimedWait (LowLevelMonitor *m, int32_t timeoutMilliseconds)
{
	/* pthread-embedded measures the absolute timeout against CLOCK_REALTIME (ftime) */
	struct timespec ts;
	clock_gettime (CLOCK_REALTIME, &ts);
	uint64_t ns = (uint64_t) timeoutMilliseconds * 1000000u + (uint64_t) ts.tv_nsec;
	ts.tv_sec += (time_t)(ns / 1000000000u);
	ts.tv_nsec = (long)(ns % 1000000000u);
	int error = pthread_cond_timedwait (&m->cond, &m->mutex, &ts);
	return error == 0;
}

void SystemNative_LowLevelMonitor_Signal_Release (LowLevelMonitor *m)
{
	pthread_cond_signal (&m->cond);
	pthread_mutex_unlock (&m->mutex);
}

int32_t SystemNative_SchedGetCpu (void)
{
	return -1; /* not available: callers fall back */
}

/* ------------------------------------------------------------------ environment / process / users */

char *SystemNative_GetEnv (const char *variable) { return getenv (variable); }
char **SystemNative_GetEnviron (void) { return environ; }
void SystemNative_FreeEnviron (char **e) { }
int32_t SystemNative_GetPid (void) { return (int32_t) sceKernelGetProcessId (); }
char *SystemNative_GetProcessPath (void) { return strdup ("app0:eboot.bin"); }
uint32_t SystemNative_GetEUid (void) { return 0; }
uint32_t SystemNative_GetEGid (void) { return 0; }
int32_t SystemNative_GetGroups (int32_t ngroups, uint32_t *groups) { return 0; }
int32_t SystemNative_GetPwUidR (uint32_t uid, Passwd *pwd, char *buf, int32_t buflen) { return -1; } /* no user database */
void *SystemNative_GetDefaultSearchOrderPseudoHandle (void) { return NULL; }
int32_t SystemNative_GetOSArchitecture (void) { return 2; /* ARCH_ARM */ }
char *SystemNative_GetUnixRelease (void) { return strdup ("psvita"); }

int32_t SystemNative_GetUnixVersion (char *version, int *capacity)
{
	static const char v [] = "PS Vita (vitasdk newlib)";
	if (*capacity < (int) sizeof (v)) {
		*capacity = (int) sizeof (v);
		return -1;
	}
	memcpy (version, v, sizeof (v));
	return 0;
}

int32_t SystemNative_GetHostName (uint8_t *name, int32_t nameLength)
{
	static const char h [] = "psvita";
	if (nameLength < (int32_t) sizeof (h))
		return fail_errno (ENAMETOOLONG);
	memcpy (name, h, sizeof (h));
	return 0;
}

/* ------------------------------------------------------------------ logging */

void SystemNative_SysLog (SysLogPriority priority, const char *message, const char *arg1)
{
	char buf [512];
	snprintf (buf, sizeof (buf), message ? message : "", arg1);
	shepherd_vita_native_log (buf);
}

static void log_bytes (const char *prefix, const uint8_t *buffer, int32_t length)
{
	char buf [512];
	snprintf (buf, sizeof (buf), "%s%.*s", prefix, (int) length, (const char *) buffer);
	shepherd_vita_native_log (buf);
}

void SystemNative_Log (uint8_t *buffer, int32_t length) { log_bytes ("", buffer, length); }
void SystemNative_LogError (uint8_t *buffer, int32_t length) { log_bytes ("error: ", buffer, length); }

int32_t SystemNative_SNPrintF (char *string, int32_t size, const char *format, ...)
{
	va_list ap;
	va_start (ap, format);
	int r = vsnprintf (string, (size_t) size, format, ap);
	va_end (ap);
	return r;
}
