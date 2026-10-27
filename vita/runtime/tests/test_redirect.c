/* device kit #5 B-2: Mac unit test of the System.Native shim's one-shot read redirect (pal_vita.c "pure redirect" block, with the
 * "pure path helpers" it uses): any spelling of the armed path matches, only a plain read-only open takes it, it is spent after one
 * open, other files never match, the target is what the open then uses. Exit 0 = PASS. */
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
#include "path_helpers.inc"
#include "redirect.inc"

static int fails, checks;
#define CHECK(c, ...) do { checks++; if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

static int take (redirect_state *r, const char *managed_path, int rdonly_plain, char *out)
{
	char p [VITA_PATH_MAX];
	if (resolve_path (managed_path, p, sizeof p) < 0) return -1;
	int t = redirect_take (r, p, sizeof p, rdonly_plain);
	strcpy (out, p);
	return t;
}

int main (void)
{
	const char *cat = "/ux0:data/shepherd/data/StreamingAssets/aa/catalog.json", *tok = "/ux0:data/shepherd/cache/json/catalog.token.json";
	char a [VITA_PATH_MAX], b [VITA_PATH_MAX], out [VITA_PATH_MAX];
	redirect_state r; memset (&r, 0, sizeof r);
	resolve_path (cat, a, sizeof a); resolve_path (tok, b, sizeof b);
	CHECK (take (&r, cat, 1, out) == 0 && !strcmp (out, a), "unarmed: the open keeps its own path");
	CHECK (redirect_arm (&r, a, b) == 0 && r.armed, "armed");
	CHECK (take (&r, "/ux0:data/shepherd/data/StreamingAssets/aa/settings.json", 1, out) == 0 && r.armed, "another file in the folder does not take it");
	CHECK (take (&r, cat, 0, out) == 0 && r.armed && !strcmp (out, a), "an open for writing / creating / truncating does not take it");
	CHECK (take (&r, "UX0:/data//shepherd/DATA/StreamingAssets/aa/x/../catalog.JSON", 1, out) == 1 && !strcmp (out, b) && !r.armed && r.hits == 1,
	       "another spelling of the same file takes it (case, slashes, ..): the open reads the target (%s)", out);
	CHECK (take (&r, cat, 1, out) == 0 && !strcmp (out, a) && r.hits == 1, "spent after one open: the next open reads the real file");
	CHECK (redirect_arm (&r, a, b) == 0 && take (&r, cat, 1, out) == 1 && r.hits == 2, "re-armed: taken once more");
	char longp [VITA_PATH_MAX + 8]; memset (longp, 'x', sizeof longp - 1); longp [sizeof longp - 1] = 0;
	CHECK (redirect_arm (&r, a, longp) == -1, "a target longer than a path buffer is refused");
	r.armed = 0;
	CHECK (take (&r, cat, 1, out) == 0, "disarmed: never taken");
	printf ("test_redirect: %d checks, %d FAIL\n", checks, fails);
	return fails ? 1 : 0;
}
