/* SHEPHERD-VITA: Mac unit test of the System.Native shim's pure path helpers (pal_vita.c): Vita path resolution
 * (resolve_path) and the synthesized file identity (path_identity: FileStatus Dev/Ino, which .NET compares in
 * File.Replace / File.Move / Directory.Move / FileStream DeleteOnClose). */
#include <stdio.h>
#include <string.h>
#include <errno.h>
#include <pthread.h>
#include <stdint.h>
#include <stdlib.h>
#define VITA_PATH_MAX 1024
static int fail_errno (int e) { errno = e; return -1; }
static pthread_mutex_t cwd_lock = PTHREAD_MUTEX_INITIALIZER;
static char cwd [VITA_PATH_MAX] = "app0:";
/* --- copied verbatim from pal_vita.c by run-tests.sh (the "pure path helpers" block) --- */
#include "path_helpers.inc"

static int check (const char *in, const char *want)
{
	char out [VITA_PATH_MAX];
	int r = resolve_path (in, out, sizeof out);
	int ok = r == 0 && !strcmp (out, want);
	printf ("%s %-40s -> %s\n", ok ? "ok  " : "FAIL", in, r == 0 ? out : "(error)");
	return ok;
}

/* identity of two .NET-side paths (resolved like the shim does), expected same or different */
static int same_file (const char *x, const char *y, int want_same, int want_same_dev)
{
	char rx [VITA_PATH_MAX], ry [VITA_PATH_MAX];
	if (resolve_path (x, rx, sizeof rx) || resolve_path (y, ry, sizeof ry)) {
		printf ("FAIL resolve %s / %s\n", x, y);
		return 0;
	}
	int64_t dx, ix, dy, iy;
	path_identity (rx, &dx, &ix);
	path_identity (ry, &dy, &iy);
	int same = dx == dy && ix == iy;
	int ok = same == want_same && (dx == dy) == want_same_dev && ix != 0 && iy != 0 && dx != 0;
	printf ("%s %-34s %s %-34s (dev %s)\n", ok ? "ok  " : "FAIL", x, same ? "==" : "!=", y, dx == dy ? "same" : "differs");
	return ok;
}

int main (void)
{
	int ok = 1;
	ok &= check ("managed/hello-data.txt", "app0:managed/hello-data.txt");   /* sceIo accepts app0:x and app0:/x */
	ok &= check ("app0:/managed/hello-data.txt", "app0:/managed/hello-data.txt");
	ok &= check ("app0:/ux0:data/shepherd/x.txt", "ux0:data/shepherd/x.txt");   /* Path.GetFullPath("ux0:data/...") */
	ok &= check ("ux0:data/shepherd/x.txt", "ux0:data/shepherd/x.txt");
	ok &= check ("/managed/x", "app0:managed/x");
	ok &= check ("ux0:/data/a/b", "ux0:/data/a/b");

	/* identity: same file under different spellings (exFAT is case-insensitive) */
	ok &= same_file ("app0:/ux0:data/shepherd/a.txt", "ux0:data/shepherd/a.txt", 1, 1);
	ok &= same_file ("ux0:data/shepherd/A.TXT", "UX0:/data//shepherd/./a.txt", 1, 1);
	ok &= same_file ("ux0:data/shepherd/x/../a.txt", "ux0:data/shepherd/a.txt", 1, 1);
	ok &= same_file ("ux0:data/shepherd/", "ux0:data/shepherd", 1, 1);
	ok &= same_file ("managed/hello-data.txt", "app0:/managed/hello-data.txt", 1, 1);
	ok &= same_file ("ux0:data/../../data/a", "ux0:data/a", 1, 1);   /* ".." stops at the device root */
	/* different files (File.Replace(a, b) must not see them as one; File.Move(a, b) must not rename over b) */
	ok &= same_file ("ux0:data/shepherd/a.txt", "ux0:data/shepherd/b.txt", 0, 1);
	ok &= same_file ("ux0:data/shepherd/a.txt", "ux0:data/shepherd/a.txt.bak", 0, 1);
	ok &= same_file ("ux0:data/shepherd/ab", "ux0:data/shepherda/b", 0, 1);
	ok &= same_file ("ux0:data/a.txt", "uma0:data/a.txt", 0, 0);              /* other device: rename -> EXDEV */
	ok &= same_file ("app0:managed/x", "ux0:managed/x", 0, 0);
	/* non-ASCII bytes are compared exactly (no Unicode case folding) */
	ok &= same_file ("ux0:data/\xc3\xa9", "ux0:data/\xc3\x89", 0, 1);

	/* the case-preserving canonical form the shim's Rename compares to detect a no-op */
	char na [VITA_PATH_MAX], nb [VITA_PATH_MAX];
	size_t dl;
	normalize_path ("ux0:data//shepherd/./A.txt", na, sizeof na, 0, &dl);
	normalize_path ("ux0:/data/shepherd/A.txt", nb, sizeof nb, 0, &dl);
	int canon = !strcmp (na, nb) && !strcmp (na, "ux0:/data/shepherd/A.txt") && dl == 4;
	printf ("%s canonical %s\n", canon ? "ok  " : "FAIL", na);
	ok &= canon;

	/* V6: the cwd is reported rooted ("/app0:"), so Path.GetFullPath ("x") = "/app0:/x" and the host's rooted
	 * "/ux0:data/shepherd/..." paths resolve to the device path */
	char mc [VITA_PATH_MAX];
	int mcn = managed_cwd (cwd, mc, sizeof mc);
	int mok = mcn == 6 && !strcmp (mc, "/app0:") && managed_cwd ("app0:", mc, 6) == -1 && managed_cwd ("app0:", mc, 7) == 6;
	printf ("%s managed cwd %s\n", mok ? "ok  " : "FAIL", mc);
	ok &= mok;
	ok &= check ("/app0:/managed/x.dll", "app0:/managed/x.dll");
	ok &= check ("/app0:/ux0:data/shepherd/x.txt", "ux0:data/shepherd/x.txt");
	ok &= check ("/ux0:data/shepherd/pack/shepherd.pack", "ux0:data/shepherd/pack/shepherd.pack");
	ok &= check ("/ux0:data/shepherd/runtime/logs", "ux0:data/shepherd/runtime/logs");
	ok &= check ("/app0:/app0:managed/a", "app0:managed/a");
	ok &= same_file ("/ux0:data/shepherd/a.txt", "ux0:data/shepherd/a.txt", 1, 1);
	ok &= same_file ("/app0:/ux0:data/shepherd/a.txt", "/ux0:data/shepherd/A.txt", 1, 1);

	strcpy (cwd, "ux0:data/shepherd");
	ok &= check ("save/0.json", "ux0:data/shepherd/save/0.json");
	ok &= same_file ("save/0.json", "ux0:data/shepherd/SAVE/0.JSON", 1, 1);
	mcn = managed_cwd (cwd, mc, sizeof mc);
	ok &= mcn > 0 && !strcmp (mc, "/ux0:data/shepherd");
	ok &= check ("/ux0:data/shepherd/save/0.json", "ux0:data/shepherd/save/0.json");
	printf ("%s paths\n", ok ? "PASS" : "FAIL");
	return ok ? 0 : 1;
}
