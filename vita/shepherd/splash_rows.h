/* SHEPHERD-VITA kit #6 review fix (host.c splash_rebase; test: mac-check/test_splash_rows.c): does a video-length table hold a Devolver
 * splash row the ENGINE takes? The host re-bases the frame clock by the splash's 376 frames only then, so it must use the engine's own
 * rules (engine/Shepherd.Engine/Runtime/K6Engine.cs LoadLengths) -- kit #6's first cut searched the whole file for the clip's name, so a
 * '#' comment or a malformed row made the host re-base while the engine listed no clip (the logo 0 s, the Main Menu at kit #5's f806).
 * Per line: cut at '#'; split on TAB; at least 3 fields (the third ends at the next TAB); field 0 trimmed = the Devolver clip (the engine's
 * key is the clip name, case-insensitive: it starts with "Devolver_Animated_Logo"); field 1 trimmed a whole decimal number > 0; field 2
 * trimmed "num" or "num/den" with num a number and num / den > 0 (a den that does not parse or is <= 0 counts as 1, as the engine's).
 * Pure C, no host calls (the buffer is changed in place: NULs at '#', TAB and line ends). */
#ifndef SHEPHERD_SPLASH_ROWS_H
#define SHEPHERD_SPLASH_ROWS_H
#include <stdlib.h>
#include <string.h>
#include <strings.h>

static int splash_space (char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\v' || c == '\f'; }
static char *splash_trim (char *s)
{
	while (splash_space (*s)) s++;
	char *e = s + strlen (s);
	while (e > s && splash_space (e [-1])) *--e = 0;
	return s;
}
/* one line (NUL-terminated, no '\n'): 1 = a splash row the engine takes; *named = 1 when field 0 names the clip at all */
static int splash_row (char *line, int *named)
{
	char *p = strchr (line, '#'), *f [3];
	if (p)
		*p = 0;
	int n = 0;
	p = line;
	f [n++] = p;
	while (n < 3 && (p = strchr (p, '\t')) != NULL) { *p++ = 0; f [n++] = p; }
	if (strncasecmp (splash_trim (f [0]), "Devolver_Animated_Logo", 22) != 0)
		return 0;
	*named = 1;
	if (n < 3)
		return 0;
	if ((p = strchr (f [2], '\t')) != NULL)
		*p = 0;
	char *num = splash_trim (f [1]), *end;
	long frames = strtol (num, &end, 10);
	if (end == num || *end || frames <= 0)
		return 0;
	char *rate = splash_trim (f [2]), *slash = strchr (rate, '/');
	if (slash)
		*slash = 0;
	char *rn = splash_trim (rate);
	double r = strtod (rn, &end);
	if (end == rn || *end)
		return 0;
	double den = 1.0;
	if (slash) {
		char *rd = splash_trim (slash + 1);
		double d = strtod (rd, &end);
		if (end != rd && !*end && d > 0)
			den = d;
	}
	return r / den > 0;
}
/* a whole table (NUL-terminated buffer, changed in place): 1 = some line is a splash row the engine takes */
static int splash_rows_scan (char *buf, int *named)
{
	*named = 0;
	for (char *s = buf; s && *s;) {
		char *nl = strchr (s, '\n');
		if (nl)
			*nl = 0;
		if (splash_row (s, named))
			return 1;
		s = nl ? nl + 1 : NULL;
	}
	return 0;
}
#endif
