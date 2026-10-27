/* device kit #6 review fix: the host's splash check (vita/shepherd/splash_rows.h, host.c file_names_splash) takes exactly the rows the
 * engine's K6Engine.LoadLengths takes for the Devolver clip -- the table video_lengths.py writes (re-based), the row commented out, a
 * malformed row (2 fields, frames 0 / negative / not a number, rate 0 / not a number), the name only in a comment, other clips only, CRLF
 * lines, spaces around fields, a "num/den" rate with a bad den (counts as 1), the clip name in another case, the third field followed by
 * more TAB fields. Build + run (exit 0 = PASS):
 *   cc -O2 -Wall -I vita/shepherd -o /tmp/test_splash_rows vita/shepherd/mac-check/test_splash_rows.c && /tmp/test_splash_rows */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "splash_rows.h"

static int fails, checks;
#define CHECK(c, ...) do { checks++; if (!(c)) { fails++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

#define CLIP "Devolver_Animated_Logo_v9_test"   /* (a made-up name with the prefix of the real clip's name) */
static void t (const char *table, int want, int want_named, const char *what)
{
	char *b = strdup (table);
	int named = -1, got = splash_rows_scan (b, &named);
	CHECK (got == want && named == want_named, "%s: got %d (named %d), want %d (named %d)", what, got, named, want, want_named);
	free (b);
}

int main (void)
{
	const char *head = "# Shepherd VideoPlayer stand-in lengths (owner-local)\n";
	char buf [1024];
	snprintf (buf, sizeof buf, "%sDLC_Intro\t1421\t30/1\t# 47.367 s\n" CLIP "\t188\t30/1\t# 6.267 s\nIntro\t390\t30/1\t# 13.000 s\nUpdate_Video\t2515\t30/1\t# 83.833 s\n", head);
	t (buf, 1, 1, "the generated table (every clip)");
	t (CLIP "\t188\t30/1\n", 1, 1, "the clip alone");
	t (CLIP "\t188\t30/1", 1, 1, "no final newline");
	t (CLIP "\t188\t30\n", 1, 1, "a plain rate");
	t (CLIP "\t188\t29.97\n", 1, 1, "a decimal rate");
	t (CLIP "\t188\t30/0\n", 1, 1, "den 0 counts as 1 (the engine's rule)");
	t (CLIP "\t188\t30/x\n", 1, 1, "den not a number counts as 1");
	t (CLIP "\t188\t30/-2\n", 1, 1, "den < 0 counts as 1");
	t (" " CLIP " \t 188 \t 30/1 \r\n", 1, 1, "spaces around fields, CRLF");
	t ("devolver_animated_LOGO_V9_TEST\t188\t30/1\n", 1, 1, "another case (the engine's dictionary ignores case)");
	t (CLIP "\t188\t30/1\textra\tfields\n", 1, 1, "more TAB fields after the rate");
	t ("#" CLIP "\t188\t30/1\n", 0, 0, "the row commented out");
	t ("# " CLIP " was here\nIntro\t390\t30/1\n", 0, 0, "the name only in a comment");
	t ("Intro\t390\t30/1\t# " CLIP "\n", 0, 0, "the name in another row's comment");
	t (CLIP "\t188\n", 0, 1, "2 fields");
	t (CLIP " 188 30/1\n", 0, 1, "spaces, not TABs (one field: named, no row)");
	t (CLIP "\t0\t30/1\n", 0, 1, "frames 0");
	t (CLIP "\t-188\t30/1\n", 0, 1, "frames negative");
	t (CLIP "\t18x\t30/1\n", 0, 1, "frames not a number");
	t (CLIP "\t188.5\t30/1\n", 0, 1, "frames not whole");
	t (CLIP "\t\t30/1\n", 0, 1, "frames empty");
	t (CLIP "\t188\t0/1\n", 0, 1, "rate 0");
	t (CLIP "\t188\t-30\n", 0, 1, "rate negative");
	t (CLIP "\t188\tfast\n", 0, 1, "rate not a number");
	t (CLIP "\t188\t\n", 0, 1, "rate empty");
	t (CLIP "\t188\t30/1#\t6.267 s\n", 1, 1, "a comment right after the rate");
	t (CLIP "\t188#\t30/1\n", 0, 1, "the comment cuts the rate off (2 fields left)");
	t ("DLC_Intro\t1421\t30/1\nIntro\t390\t30/1\n", 0, 0, "other clips only");
	t ("", 0, 0, "an empty file");
	t ("\n\n\n", 0, 0, "empty lines");
	snprintf (buf, sizeof buf, "#" CLIP "\t188\t30/1\n" CLIP "\t188\t30/1\n");
	t (buf, 1, 1, "a commented row, then a good one");
	snprintf (buf, sizeof buf, CLIP "\t0\t30/1\n" CLIP "\t188\t30/1\n");
	t (buf, 1, 1, "a bad row, then a good one");
	printf ("test_splash_rows: %d checks, %d FAIL\n", checks, fails);
	return fails != 0;
}
