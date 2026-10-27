/* ------------------------------------------------------------------ device kit #8, track play: SAVE BACKUPS (vita/notes/kit8-play.md)
 * Included by pal_vita.c (the Vita's System.Native: every file the game writes goes through SystemNative_Open / Write / PWrite / Close).
 * Roadmap "Save backups": "each slot's previous versions are kept (the last 5, rotating) before the game writes ... every write is read back
 * and parsed to confirm it is valid; never touches the save format".
 *  - BEFORE the game replaces a file of its save folder (<SHEPHERD_PERSISTENT>/saves/<name>: slot_N.mp / .json, meta_N.json, settings.json,
 *    persistence.json, RewiredSaveData.json -- an open for writing that truncates or creates an existing, non-empty file), the file as it is
 *    is copied to <the owner folder>/save-backups/<name>.<YYYYMMDD-HHMMSS> (the Vita's local time), unless it is byte-identical to that
 *    file's newest backup; the 5 newest per file are kept, older ones deleted. The game's own backup/ folder (its MakeBackup, up to 10 per
 *    file) is not touched; the backups live outside the game's folder, so the game never sees them. A copy that fails leaves the game's
 *    write as it was (logged).
 *  - every such write is followed byte by byte (an FNV-1a hash of what the game wrote, in order) and, when the game closes the file, READ
 *    BACK from the card and compared (size + hash), then PARSED as far as Shepherd can without the game's key: an encrypted file (the game's
 *    'E' + 16-byte IV + AES-CBC blocks) must be a whole number of blocks, a plain JSON file must be one complete JSON value (strings,
 *    escapes, nesting, nothing after it). One SAVE line per write: "read back identical, ... OK" or what differs.
 *  - Settings > Graphics > Restore Save Backup (Runtime/VitaToggles.cs) lists the slots' backups; a restore happens at the next start,
 *    before the game reads anything, and goes through this same open: the file it replaces is backed up first (a restore can be undone).
 *  Review fixes (vita/notes/kit8-play.md "Review fixes"): the pruning never deletes a backup that is open (a restore's source while File.Copy
 *  reads it: kept until the next backup, the SAVE line says so and gives the real count); a new backup is always named after the newest one
 *  (the name = the local time, or the newest backup's name + 1 s when the clock is behind it: the order by name stays the order made, so the
 *  pruning / the dedupe / the Restore row never take a new backup for an old one); the copy runs outside the tracking lock (an untracked
 *  write never takes it: no thread's file writes wait behind a slot copy).
 * Off: SHEPHERD_K8_SAVEBAK=0 or SHEPHERD_K8_PLAY=0 in the env file (kit #7: no copy, no tracking, no SAVE line). Never the real CotL saves:
 * SHEPHERD_PERSISTENT is the sandbox folder the host sets (ux0:data/shepherd/persistent). */

#include <psp2/rtc.h>
#define SB_KEEP 5
#define SB_TRACK 4   /* save files written at the same time (the game writes one, two with its meta) */
static int g_sb_state;   /* 0 = not set up yet, 1 = on, -1 = off (set once, under g_sb_lock; read with acquire) */
static char *g_sb_saves, *g_sb_dir;   /* (allocated once: eboot room) */
static size_t g_sb_saves_len;
static unsigned g_sb_lines;
static pthread_mutex_t g_sb_lock = PTHREAD_MUTEX_INITIALIZER;
typedef struct { SceUID fd; int seq; uint64_t hash, bytes; char name [40]; } SbWrite;
static SbWrite g_sb_w [SB_TRACK];
static volatile int g_sb_n;   /* tracked writes now (a plain int read first: an untracked write costs nothing) */

static void sb_log (const char *fmt, ...)
{
	if (g_sb_lines >= 200)
		return;
	char b [600];
	va_list ap;
	va_start (ap, fmt);
	vsnprintf (b, sizeof b, fmt, ap);
	va_end (ap);
	g_sb_lines++;
	shepherd_vita_native_log (b);
}
static int sb_off (const char *k) { const char *e = getenv (k); return e && !strcmp (e, "0"); }
static void sb_setup (void)
{
	const char *p = getenv ("SHEPHERD_PERSISTENT");
	char r [VITA_PATH_MAX];
	if (sb_off ("SHEPHERD_K8_PLAY") || sb_off ("SHEPHERD_K8_SAVEBAK") || !p || !*p || resolve_path (p, r, sizeof r) < 0) {
		__atomic_store_n (&g_sb_state, -1, __ATOMIC_RELEASE);
		return;
	}
	size_t n = strlen (r);
	while (n > 1 && r [n - 1] == '/')
		r [--n] = 0;
	g_sb_saves = (char *) malloc (n + 8);
	g_sb_dir = (char *) malloc (n + 16);
	if (!g_sb_saves || !g_sb_dir) {
		__atomic_store_n (&g_sb_state, -1, __ATOMIC_RELEASE);
		return;
	}
	snprintf (g_sb_saves, n + 8, "%s/saves/", r);
	g_sb_saves_len = strlen (g_sb_saves);
	char *slash = strrchr (r, '/');
	if (slash)
		*slash = 0;
	snprintf (g_sb_dir, n + 16, "%s/save-backups", r);
	__atomic_store_n (&g_sb_state, 1, __ATOMIC_RELEASE);
}
/* a file directly in the save folder (not its backup/ or any sub-folder): its name, else NULL */
static const char *sb_name (const char *path)
{
	int st = __atomic_load_n (&g_sb_state, __ATOMIC_ACQUIRE);
	if (!st) {   /* (the first save-file write: set up once, two threads at once included) */
		pthread_mutex_lock (&g_sb_lock);
		if (!g_sb_state)
			sb_setup ();
		st = g_sb_state;
		pthread_mutex_unlock (&g_sb_lock);
	}
	if (st < 0 || strncmp (path, g_sb_saves, g_sb_saves_len))
		return NULL;
	const char *n = path + g_sb_saves_len;
	return *n && !strchr (n, '/') && strlen (n) < 40 ? n : NULL;
}
static uint64_t sb_fnv (uint64_t h, const unsigned char *b, size_t n)
{
	for (size_t i = 0; i < n; i++)
		h = (h ^ b [i]) * 1099511628211ull;
	return h;
}
/* the newest backup of <name> (its full path in out) and how many there are; the oldest's path in oldest */
static int sb_list (const char *name, char *newest, char *oldest, size_t cap)
{
	newest [0] = oldest [0] = 0;
	SceUID d = sceIoDopen (g_sb_dir);
	if (d < 0)
		return 0;
	size_t nl = strlen (name);
	int count = 0;
	char hi [64] = "", lo [64] = "";
	SceIoDirent e;
	memset (&e, 0, sizeof e);
	while (sceIoDread (d, &e) > 0) {
		if (strncmp (e.d_name, name, nl) || e.d_name [nl] != '.' || strlen (e.d_name + nl + 1) < 15 || strlen (e.d_name + nl + 1) > 40)
			continue;
		const char *st = e.d_name + nl + 1;
		if (st [0] < '0' || st [0] > '9')
			continue;
		count++;
		if (!hi [0] || strcmp (st, hi) > 0)
			strlcpy (hi, st, sizeof hi);
		if (!lo [0] || strcmp (st, lo) < 0)
			strlcpy (lo, st, sizeof lo);
		memset (&e, 0, sizeof e);
	}
	sceIoDclose (d);
	if (hi [0])
		snprintf (newest, cap, "%s/%s.%s", g_sb_dir, name, hi);
	if (lo [0])
		snprintf (oldest, cap, "%s/%s.%s", g_sb_dir, name, lo);
	return count;
}
/* 1 = the two files have the same bytes */
static int sb_same (const char *a, const char *b, unsigned char *buf, size_t half)
{
	SceIoStat sa, sb;
	if (sceIoGetstat (a, &sa) < 0 || sceIoGetstat (b, &sb) < 0 || sa.st_size != sb.st_size)
		return 0;
	SceUID fa = sceIoOpen (a, SCE_O_RDONLY, 0), fb = fa < 0 ? -1 : sceIoOpen (b, SCE_O_RDONLY, 0);
	int same = fa >= 0 && fb >= 0;
	while (same) {
		int ra = sceIoRead (fa, buf, half), rb = sceIoRead (fb, buf + half, half);
		if (ra != rb || ra < 0) { same = 0; break; }
		if (ra == 0) break;
		if (memcmp (buf, buf + half, (size_t) ra)) same = 0;
	}
	if (fa >= 0) sceIoClose (fa);
	if (fb >= 0) sceIoClose (fb);
	return same;
}
static int sb_copy (const char *from, const char *to, unsigned char *buf, size_t cap, int64_t *bytes)
{
	SceUID fi = sceIoOpen (from, SCE_O_RDONLY, 0);
	if (fi < 0)
		return fi;
	SceUID fo = sceIoOpen (to, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
	if (fo < 0) { sceIoClose (fi); return fo; }
	int rc = 0;
	*bytes = 0;
	for (;;) {
		int r = sceIoRead (fi, buf, cap);
		if (r < 0) { rc = r; break; }
		if (r == 0) break;
		int w = sceIoWrite (fo, buf, (SceSize) r);
		if (w != r) { rc = w < 0 ? w : -1; break; }
		*bytes += r;
	}
	sceIoClose (fi);
	sceIoClose (fo);
	if (rc < 0)
		sceIoRemove (to);
	return rc;
}
/* "YYYYMMDD-HHMMSS" (15 characters) + 1 second, through the calendar; 0 = not such a stamp (left as it was) */
static int sb_stamp_inc (char *st)
{
	static const unsigned char w [6] = { 4, 2, 2, 2, 2, 2 }, dim [12] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
	unsigned v [6];
	const char *p = st;
	for (int i = 0; i < 6; i++) {
		v [i] = 0;
		for (unsigned j = 0; j < w [i]; j++, p++) {
			if (*p < '0' || *p > '9')
				return 0;
			v [i] = v [i] * 10 + (unsigned) (*p - '0');
		}
		if (i == 2 && *p++ != '-')
			return 0;
	}
	unsigned y = v [0], mo = v [1], d = v [2], h = v [3], mi = v [4], s = v [5];
	if (mo < 1 || mo > 12 || d < 1 || h > 23 || mi > 59 || s > 59)
		return 0;
	unsigned md = dim [mo - 1] + (mo == 2 && y % 4 == 0 && (y % 100 || y % 400 == 0));
	if (++s == 60 && (s = 0, ++mi == 60) && (mi = 0, ++h == 24) && (h = 0, ++d > md) && (d = 1, ++mo == 13)) {
		mo = 1;
		y++;
	}
	snprintf (st, 16, "%04u%02u%02u-%02u%02u%02u", y % 10000, mo, d, h, mi, s);
	return 1;
}
/* SystemNative_Open, before a write that truncates / creates: the file as it is into save-backups/ (the game's write then goes ahead).
 * No lock: two save files are backed up independently (their own names); the game never opens one file twice for writing. */
static void sb_before_write (const char *path)
{
	const char *name = sb_name (path);
	SceIoStat st;
	if (!name || sceIoGetstat (path, &st) < 0 || SCE_S_ISDIR (st.st_mode) || st.st_size <= 0)
		return;
	unsigned char *buf = (unsigned char *) malloc (32768);
	char newest [VITA_PATH_MAX], oldest [VITA_PATH_MAX], to [VITA_PATH_MAX];
	if (!buf)
		goto out;
	sceIoMkdir (g_sb_dir, 0777);
	sb_list (name, newest, oldest, sizeof newest);
	if (newest [0] && sb_same (path, newest, buf, 16384)) {
		sb_log ("SAVE backup: %s = its newest backup: no copy", name);
		goto out;
	}
	SceDateTime t;
	memset (&t, 0, sizeof t);
	sceRtcGetCurrentClockLocalTime (&t);
	char stamp [40];
	snprintf (stamp, sizeof stamp, "%04u%02u%02u-%02u%02u%02u", (unsigned) t.year, (unsigned) t.month, (unsigned) t.day, (unsigned) t.hour, (unsigned) t.minute, (unsigned) t.second);
	/* (review fix: never named before the newest backup -- a clock set back, a DST hour -- or the pruning would delete the new one at once) */
	const char *nst = newest [0] ? strrchr (newest, '.') : NULL;
	int behind = 0, c = nst ? strncmp (stamp, nst + 1, 15) : 1;
	if (c <= 0) {
		char b [16];
		memcpy (b, nst + 1, 15);
		b [15] = 0;
		if (sb_stamp_inc (b)) {
			memcpy (stamp, b, 16);
			behind = c < 0;
		}
	}
	snprintf (to, sizeof to, "%s/%s.%s", g_sb_dir, name, stamp);
	SceIoStat ts;
	for (int k = 2; sceIoGetstat (to, &ts) >= 0 && k < 100; k++)   /* (two writes in the same second: -2, -3 ...) */
		snprintf (to, sizeof to, "%s/%s.%s-%d", g_sb_dir, name, stamp, k);
	int64_t bytes = 0;
	int rc = sb_copy (path, to, buf, 32768, &bytes);
	if (rc < 0) {
		sb_log ("SAVE backup: %s NOT backed up (0x%08x)", name, (unsigned) rc);
		goto out;
	}
	/* the 5 newest kept -- never a backup that is open (a restore's source while File.Copy reads it: kept until the next backup) */
	int removed = 0, inuse = 0, kept;
	while ((kept = sb_list (name, newest, oldest, sizeof newest)) > SB_KEEP && oldest [0] && !(inuse = fd_path_open (oldest)) && sceIoRemove (oldest) >= 0)
		removed++;
	sb_log ("SAVE backup: %s (%lld B) -> save-backups/%s (%d kept%s%s%s)", name, (long long) bytes, strrchr (to, '/') + 1, kept, removed ? ", oldest deleted" : "",
		kept <= SB_KEEP ? "" : inuse ? ", oldest open (a restore's source): kept" : ", the oldest NOT deleted",
		behind ? "; clock behind the newest: named after it" : "");
out:
	free (buf);
}
/* SystemNative_Open, after a write open succeeded: follow what the game writes into this file */
static void sb_track (SceUID fd, const char *path)
{
	const char *name = sb_name (path);
	if (!name)
		return;
	pthread_mutex_lock (&g_sb_lock);
	for (int i = 0; i < SB_TRACK; i++)
		if (!g_sb_w [i].fd) {
			g_sb_w [i].fd = fd;
			g_sb_w [i].hash = 14695981039346656037ull;
			g_sb_w [i].bytes = 0;
			g_sb_w [i].seq = 1;
			strlcpy (g_sb_w [i].name, name, sizeof g_sb_w [i].name);
			g_sb_n++;
			break;
		}
	pthread_mutex_unlock (&g_sb_lock);
}
/* SystemNative_Write / PWrite (off < 0: at the current position) after n bytes were written */
static void sb_wrote (SceUID fd, const void *b, int n, int64_t off)
{
	if (!g_sb_n || n <= 0)
		return;
	int i = 0;
	while (i < SB_TRACK && g_sb_w [i].fd != fd)   /* (unlocked: only a followed fd takes the lock -- its entry was set before its open returned) */
		i++;
	if (i == SB_TRACK)
		return;
	pthread_mutex_lock (&g_sb_lock);
	if (g_sb_w [i].fd == fd) {
		if (off >= 0 && (uint64_t) off != g_sb_w [i].bytes)
			g_sb_w [i].seq = 0;   /* not in order: only the size is compared */
		g_sb_w [i].hash = sb_fnv (g_sb_w [i].hash, (const unsigned char *) b, (size_t) n);
		g_sb_w [i].bytes += (uint64_t) n;
	}
	pthread_mutex_unlock (&g_sb_lock);
}
/* one complete JSON value, nothing after it (structure only: strings with escapes, nesting up to 256 deep), fed in chunks */
typedef struct { int depth, instr, esc, done, bad; uint64_t pos; } SbJson;
static void sb_json (SbJson *j, const unsigned char *b, size_t n)
{
	for (size_t i = 0; i < n && !j->bad; i++, j->pos++) {
		unsigned char c = b [i];
		if (j->instr) {
			if (j->esc) j->esc = 0;
			else if (c == '\\') j->esc = 1;
			else if (c == '"') j->instr = 0;
			continue;
		}
		if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || (j->pos < 3 && (c == 0xEF || c == 0xBB || c == 0xBF)))
			continue;
		if (j->done) { j->bad = 1; break; }
		if (c == '"') j->instr = 1;
		else if (c == '{' || c == '[') { if (++j->depth > 256) j->bad = 1; }
		else if (c == '}' || c == ']') { if (--j->depth < 0) j->bad = 1; else if (!j->depth) j->done = 1; }
	}
}
/* SystemNative_Close, after the close: read the file back (32 KiB at a time), compare, parse; one SAVE line */
static void sb_closed (SceUID fd, const char *path)
{
	if (!g_sb_n)
		return;
	SbWrite w;
	int found = 0;
	pthread_mutex_lock (&g_sb_lock);
	for (int i = 0; i < SB_TRACK; i++)
		if (g_sb_w [i].fd == fd) {
			w = g_sb_w [i];
			g_sb_w [i].fd = 0;
			g_sb_n--;
			found = 1;
			break;
		}
	pthread_mutex_unlock (&g_sb_lock);
	if (!found || !path)
		return;
	SceUID f = sceIoOpen (path, SCE_O_RDONLY, 0);
	unsigned char *buf = f < 0 ? NULL : (unsigned char *) malloc (32768);
	if (!buf) {
		if (f >= 0) sceIoClose (f);
		sb_log ("SAVE verify: %s NOT read back (0x%08x)", w.name, (unsigned) f);
		return;
	}
	uint64_t h = 14695981039346656037ull, got = 0;
	int rerr = 0;
	unsigned char head = 0;
	SbJson j;
	memset (&j, 0, sizeof j);
	for (;;) {
		int r = sceIoRead (f, buf, 32768);
		if (r < 0) { rerr = r; break; }
		if (r == 0) break;
		if (!got) head = buf [0];
		h = sb_fnv (h, buf, (size_t) r);
		if (head == '{' || head == '[' || head == 0xEF)
			sb_json (&j, buf, (size_t) r);
		got += (uint64_t) r;
	}
	sceIoClose (f);
	free (buf);
	const char *fmt = "empty";
	int ok = rerr == 0 && got == w.bytes && (!w.seq || h == w.hash);
	if (ok && got) {
		if (head == 'E')
			fmt = got >= 33 && (got - 17) % 16 == 0 ? "encrypted container OK" : (ok = 0, "BROKEN encrypted container");
		else if (head == '{' || head == '[' || head == 0xEF)
			fmt = j.done && !j.bad && !j.instr ? "JSON OK" : (ok = 0, "INCOMPLETE JSON");
		else
			fmt = "plain data, not parsed";
	}
	if (ok)
		sb_log ("SAVE verify: %s %llu B read back identical%s; %s", w.name, (unsigned long long) w.bytes, w.seq ? "" : " (size only)", fmt);
	else
		sb_log ("SAVE verify: %s FAILED: wrote %llu B, read %llu%s%s; %s; the previous version is in save-backups/", w.name, (unsigned long long) w.bytes,
			(unsigned long long) got, rerr ? " (read error)" : "", got == w.bytes && w.seq && h != w.hash ? " DIFFERENT" : "", fmt);
}
