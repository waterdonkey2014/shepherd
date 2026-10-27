/* Shepherd devlog client -- core (platform-neutral). See devlog.h and PROTOCOL.md (shpdlog/1 revision 3).
 * Part of the unity build (devlog_unity.c): everything except the devlog_* API is static. No malloc: all buffers
 * are static (see README "Memory" for the .bss size), so the client works the same in a nearly full newlib heap. No
 * function keeps a large array on the stack: the deepest call chain is ~5.6 KiB (arm-vita-eabi-gcc -fstack-usage,
 * the job -> dl_send_run -> dl_http -> dl_mac32).
 *
 * Threads (revision 3): every network transfer and the keep copies run as ONE job at a time on a background worker
 * (low priority, on the core the app leaves spare: DL_THREAD_*). The app's thread only reads the config, writes the
 * run marker / tag and starts or collects jobs, so devlog_begin costs well under 1 ms. While a job runs it owns all
 * static buffers below; the API functions that touch them first wait for it (devlog_end / devlog_clear / a second
 * devlog_begin cancel it first). The worker never calls the app's log function: its lines go through a small queue
 * that the app's thread empties (any devlog_* call, devlog_wait). devlog_note uses only its own stack buffer and a
 * separate <run>.note file, so it may run on any thread at any time. */
#include "devlog.h"
#include "devlog_plat.h"
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define DL_DEFAULT_ROOT "ux0:data/shepherd"
#define DL_TCP_PORT 47710
#define DL_UDP_PORT 47711
#define DL_REACH_MS 2000u               /* PROTOCOL.md §6: the most a missing server may cost a call */
#define DL_REACH_RESERVE_MS 300u        /* of the reach budget, kept for what follows a failed reach (copies, term) */
#define DL_BEGIN_MS 2500u               /* devlog_begin: transfer budget for kept runs (it runs at app start) */
#define DL_MAX_TOTAL_MS 20000u          /* devlog_end / flush: transfer budget */
#define DL_CONNECT_MS 600u
#define DL_STALL_MS 1500u               /* a request that makes no progress for this long is abandoned */
#define DL_RESP_MS 3000u                /* wait for the answer once a body is sent */
#define DL_FILE_CAP (8u * 1024u * 1024u)
#define DL_RUN_CAP (32u * 1024u * 1024u)
#define DL_KEEP_KB 1024u                /* a kept run keeps the last 1 MiB of each log (keep_kb=) */
#define DL_MAX_FILES 16
#define DL_MAX_ATTEMPTS 8
#define DL_MAX_AGE_DAYS 14
#define DL_KEEP_PENDING 2               /* retention: the current run + the previous one (keep_pending=) */
#define DL_MAX_MARKERS 64
#define DL_TAG "DEVLOG run="
#define DL_TAG_WINDOW (32u * 1024u)     /* read around the offset the tag was written at */
#define DL_TAG_SCAN (256u * 1024u)      /* how far a log is scanned for its FIRST tag (the *.prev check) */
#define DL_TAG_OVERLAP 128u
#define DL_PATH 320                  /* root <= 159 chars: every derived path fits; a truncated path is never used */
#define DL_CHUNK 8192                   /* network pieces */
#define DL_BIG (32u * 1024u)            /* card I/O pieces (tag scans, keep copies): one shared buffer */
#define DLH_BUDGET (-8)                 /* dl_http: the call's time budget ran out */
#define DL_CODEX_PORT 8770              /* the Archive Codex server (PROTOCOL.md §10) */
#define DL_CODEX_BASE "/vita/devlog"    /* its default base path */
#define DL_BASE_MAX 40
#define DL_PREFER_MS 200u               /* dl_reach: the short discovery for the Codex server when a standalone is known */
#define DL_TARGET 288                   /* request targets: base + /v1/upload?op=file&app=..&run=..&name=.. */
#define DL_THREAD_PRIORITY 191          /* the lowest user thread priority on the Vita (64 highest .. 191) */
#define DL_THREAD_AFFINITY 0x00040000u  /* SCE_KERNEL_CPU_MASK_USER_2: core 2, the one the Shepherd apps leave spare */
#define DL_THREAD_STACK (32u * 1024u)
#define DL_QN 32                        /* worker -> app log queue: lines (a power of two: the counters wrap cleanly) */
#define DL_QL 288                       /*                          bytes per line */

/* ------------------------------------------------------------------ small helpers */
static void dl_cpy(char *dst, size_t cap, const char *src)
{
	if (!cap) return;
	size_t n = src ? strlen(src) : 0;
	if (n >= cap) n = cap - 1;
	if (n) memcpy(dst, src, n);
	dst[n] = 0;
}

static int dl_starts(const char *s, const char *pre) { return strncmp(s, pre, strlen(pre)) == 0; }
static int dl_ends(const char *s, const char *suf)
{
	size_t a = strlen(s), b = strlen(suf);
	return a >= b && strcmp(s + a - b, suf) == 0;
}
static int dl_lower(int c) { return (c >= 'A' && c <= 'Z') ? c + 32 : c; }
static int dl_ieq(const char *a, const char *b)
{
	while (*a && *b) { if (dl_lower((unsigned char)*a) != dl_lower((unsigned char)*b)) return 0; a++; b++; }
	return *a == *b;
}
static int dl_ishex(int c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); }
static int dl_ishexc(int c) { return dl_ishex(dl_lower(c)); }
static const char *dl_base(const char *p)
{
	const char *b = p;
	for (const char *q = p; *q; q++) if (*q == '/' || *q == ':') b = q + 1;
	return b;
}
/* printable ASCII only, at most cap-1 chars (meta values, marker values) */
static void dl_clean(char *dst, size_t cap, const char *src)
{
	size_t k = 0;
	for (; src && *src && k + 1 < cap; src++) {
		unsigned char c = (unsigned char)*src;
		dst[k++] = (c >= 0x20 && c < 0x7f) ? (char)c : ' ';
	}
	if (cap) dst[k] = 0;
}
/* text shaped like a MAC address (6 hex pairs joined by ':' or '-', not inside a longer hex run) -> xx:xx:...:
 * the receiver refuses such meta values (identifier rule), and a CRC or hash that happens to look like one must not
 * cost the run its meta. Same rule as the receiver's MACADDR_RE. */
static void dl_mask_macs(char *s)
{
	size_t n = strlen(s);
	for (size_t i = 0; i + 17 <= n; i++) {
		if (i > 0 && dl_ishexc((unsigned char)s[i - 1])) continue;
		int ok = 1;
		for (int g = 0; g < 6 && ok; g++) {
			size_t p = i + (size_t)g * 3;
			if (!dl_ishexc((unsigned char)s[p]) || !dl_ishexc((unsigned char)s[p + 1])) ok = 0;
			else if (g < 5 && s[p + 2] != ':' && s[p + 2] != '-') ok = 0;
		}
		if (!ok || (i + 17 < n && dl_ishexc((unsigned char)s[i + 17]))) continue;
		for (int g = 0; g < 6; g++) { s[i + (size_t)g * 3] = 'x'; s[i + (size_t)g * 3 + 1] = 'x'; }
		i += 16;
	}
}
static void dl_meta_value(char *dst, size_t cap, const char *src) { dl_clean(dst, cap, src); dl_mask_macs(dst); }

/* ------------------------------------------------------------------ state */
typedef struct {
	int enabled;                    /* enabled=1, a valid token and mode != off */
	int mode;                       /* DEVLOG_MODE_OFF / ASK / AUTO (no mode= line: ASK) */
	int clear; long clear_off;      /* clear=1 (one-shot) and the file offset of its '1' (set to '0' after it ran) */
	char token[65];
	int has_server; uint32_t server_ip; uint16_t server_port; char server_base[DL_BASE_MAX];
	int has_discover; uint32_t discover_ip;
	uint16_t disc_port;
	unsigned max_total_ms, begin_ms, reach_ms, keep_kb;
	int keep_pending;
} dl_cfg;

typedef struct { char path[DL_PATH]; char prev[DL_PATH]; char name[72]; int kind; } dl_src;   /* kind: 'f' 'x' 'c' */

typedef struct {
	char app[33], run[65], title_id[16], build[100], commit[48], started[24];
	char state[12], outcome[12], detail[201];
	char platform[8], model[16], fw[16];
	unsigned free_kb, free_cdram_kb, free_phycont_kb;
	unsigned long long uptime_ms;
	long long tag_off;              /* where the log ended when devlog_begin wrote the tag (-1 unknown) */
	int attempt, late;
	int nsrc;
	dl_src src[DL_MAX_FILES];
} dl_run;

typedef struct {
	int tried, net, reached, auth_failed, gave_up;
	int fail;                       /* why sending stopped: 0 -, 1 out of time, 2 transfer error */
	int reject_status;              /* HTTP status of the last run the receiver refused for good */
	uint32_t ip; uint16_t port;
	char base[DL_BASE_MAX];         /* "" = the standalone receiver's paths, else the Codex binding (§10) */
	char server[24];
	uint64_t t0, reach_deadline, deadline;
	uint32_t max_file, max_run;
} dl_sess;

static struct {
	int active;                     /* between devlog_begin and devlog_end */
	int ended;                      /* devlog_end ran (mode=ask: G.cur waits for the user's answer) */
	devlog_app app;
	char root[160], state_dir[DL_PATH], pending_dir[DL_PATH], cfg_path[DL_PATH];
	dl_cfg cfg;
	dl_run cur;
	uint64_t t_begin;
} G;

static dl_run g_scratch;                  /* one pending run at a time */
static unsigned char g_buf[DL_CHUNK];     /* file streaming to the network */
static char g_big[DL_BIG + 1];            /* tag scans and keep copies (never both at once) */
static char g_text[8192];                 /* marker / config / meta */
static char g_resp[4096];
static uint64_t g_copy_left;              /* keep-copy bytes left in this call (2 x keep_kb) */

/* worker -> app log queue (single producer: the worker; single consumer: the app's thread) */
static char g_q[DL_QN][DL_QL];
static unsigned g_qhead, g_qtail, g_qdrop;

static void dl_q_push(const char *line)
{
	unsigned h = __atomic_load_n(&g_qhead, __ATOMIC_RELAXED), t = __atomic_load_n(&g_qtail, __ATOMIC_ACQUIRE);
	if (h - t >= DL_QN) { __atomic_fetch_add(&g_qdrop, 1u, __ATOMIC_RELAXED); return; }
	dl_cpy(g_q[h % DL_QN], DL_QL, line);
	__atomic_store_n(&g_qhead, h + 1, __ATOMIC_RELEASE);
}

/* the app's thread: hand the worker's lines to the app's log function, in order */
static void dl_q_drain(void)
{
	if (dlp_on_worker()) return;
	unsigned t = __atomic_load_n(&g_qtail, __ATOMIC_RELAXED), h = __atomic_load_n(&g_qhead, __ATOMIC_ACQUIRE);
	for (; t != h; t++) {
		if (G.app.log) G.app.log(g_q[t % DL_QN]);
		__atomic_store_n(&g_qtail, t + 1, __ATOMIC_RELEASE);
	}
	unsigned d = __atomic_exchange_n(&g_qdrop, 0u, __ATOMIC_RELAXED);
	if (d && G.app.log) {
		char m[96];
		snprintf(m, sizeof m, "DEVLOG (%u line(s) of the background send were dropped: queue full)", d);
		G.app.log(m);
	}
}

static void dl_log(const char *fmt, ...)
{
	char line[384];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(line, sizeof line, fmt, ap);
	va_end(ap);
	if (!G.app.log) return;
	if (dlp_on_worker()) { dl_q_push(line); return; }
	dl_q_drain();
	G.app.log(line);
}

static void dl_result_why(devlog_result *r, const char *fmt, ...)
{
	if (!r) return;
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(r->why, sizeof r->why, fmt, ap);
	va_end(ap);
}

/* ------------------------------------------------------------------ files */
static long dl_read_file(const char *path, char *buf, size_t cap)   /* NUL-terminated, at most cap-1 bytes */
{
	int fd = dlp_open_read(path);
	if (fd < 0) return -1;
	size_t n = 0;
	while (n + 1 < cap) {
		long r = dlp_read(fd, buf + n, cap - 1 - n);
		if (r <= 0) break;
		n += (size_t)r;
	}
	dlp_close(fd);
	buf[n] = 0;
	return (long)n;
}

static int dl_write_file(const char *path, const char *text, size_t n)
{
	int fd = dlp_open_write(path, 0);
	if (fd < 0) return -1;
	long w = dlp_write(fd, text, n);
	dlp_close(fd);
	return w == (long)n ? 0 : -1;
}

/* reads up to n bytes (fewer only at the end of the file) */
static long dl_read_full(int fd, void *buf, size_t n)
{
	size_t got = 0;
	while (got < n) {
		long r = dlp_read(fd, (char *)buf + got, n - got);
		if (r <= 0) break;
		got += (size_t)r;
	}
	return (long)got;
}

/* ------------------------------------------------------------------ name / path rules */
static int dl_valid_app(const char *s)          /* ^[a-z][a-z0-9_-]{0,31}$ */
{
	size_t n = strlen(s);
	if (n < 1 || n > 32 || !(s[0] >= 'a' && s[0] <= 'z')) return 0;
	for (size_t i = 1; i < n; i++) {
		char c = s[i];
		if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return 0;
	}
	return 1;
}
static int dl_valid_run(const char *s)          /* ^[A-Za-z0-9][A-Za-z0-9_-]{5,63}$ */
{
	size_t n = strlen(s);
	if (n < 6 || n > 64) return 0;
	for (size_t i = 0; i < n; i++) {
		char c = s[i];
		int an = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
		if (!an && (i == 0 || (c != '_' && c != '-'))) return 0;
	}
	return 1;
}
static int dl_ext_ok(const char *name)
{
	static const char *const EXT[] = { ".txt", ".log", ".tsv", ".csv", ".json", NULL };
	for (int i = 0; EXT[i]; i++) if (dl_ends(name, EXT[i])) return 1;
	return 0;
}
static int dl_valid_name(const char *s)         /* ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$, no "..", allowed extension */
{
	size_t n = strlen(s);
	if (n < 1 || n > 64 || strstr(s, "..")) return 0;
	for (size_t i = 0; i < n; i++) {
		char c = s[i];
		int an = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9');
		if (!an && (i == 0 || (c != '.' && c != '_' && c != '-'))) return 0;
	}
	return dl_ext_ok(s);
}
/* Config / input files live next to the logs and must never leave the console: devlog.txt, env.txt, args.txt,
 * pad.txt, hello-env.txt, hello-stress.txt, env_gl.txt, args_gl.txt, env_gl_audio.txt, args_gl_audio.txt, ...
 * Refused by name: the name (without extension and a ".prev") starts with one of PREFIX, or one of its words
 * (split at - _ .) is one of WORDS. Case-insensitive. */
static int dl_config_name(const char *base)
{
	static const char *const PREFIX[] = { "env", "args", "argv", "pad", "devlog", "token", "config", "settings",
	                                      "secret", "passw", NULL };
	static const char *const WORDS[] = { "env", "args", "arg", "argv", "pad", "devlog", "token", "tokens", "config",
	                                     "cfg", "conf", "settings", "setting", "stress", "secret", "secrets", "key",
	                                     "keys", "password", "passwd", "save", "saves", "savedata", "prefs",
	                                     "options", "opts", "param", "params", NULL };
	char low[72];
	size_t n = 0;
	for (const char *p = base; *p && n + 1 < sizeof low; p++) low[n++] = (char)dl_lower((unsigned char)*p);
	low[n] = 0;
	char *dot = strrchr(low, '.');
	if (dot) *dot = 0;
	if (dl_ends(low, ".prev")) low[strlen(low) - 5] = 0;
	for (int i = 0; PREFIX[i]; i++) if (dl_starts(low, PREFIX[i])) return 1;
	for (char *w = low; *w;) {
		size_t k = strcspn(w, "-_.");
		char save = w[k];
		w[k] = 0;
		for (int i = 0; WORDS[i]; i++) if (!strcmp(w, WORDS[i])) return 1;
		if (!save) break;
		w += k + 1;
	}
	return 0;
}
static int dl_denied_base(const char *b)
{
	return dl_ieq(b, "token") || dl_ieq(b, "token.txt") || dl_config_name(b);
}
/* The "logs only" rule. A path may be read for upload only if it is
 *   <root>/<name>                      a top-level .txt / .log (the apps' own logs)
 *   <root>/runtime/logs/<...>/<name>   (up to 3 directory levels; .txt .log .tsv .csv .json)
 *   <root>/devlog/pending/<run>/<name> (this client's own copies)
 * with a valid name that does not look like a config file. Everything else (save data, game files, managed/, pack/,
 * data/, persistent/, the config) is refused. */
static int dl_path_allowed(const char *path)
{
	size_t rl = strlen(G.root);
	if (strncmp(path, G.root, rl) != 0 || path[rl] != '/') return 0;
	const char *rel = path + rl + 1;
	if (!*rel || strstr(rel, "..") || strstr(rel, "//") || strchr(rel, ':') || strchr(rel, '\\')) return 0;
	const char *b = dl_base(rel);
	if (!dl_valid_name(b) || dl_denied_base(b)) return 0;
	if (b == rel) return dl_ends(b, ".txt") || dl_ends(b, ".log");
	if (dl_starts(rel, "runtime/logs/")) {
		int depth = 0;
		for (const char *q = rel + 13; *q; q++) if (*q == '/') depth++;
		return depth <= 3;
	}
	if (dl_starts(rel, "devlog/pending/")) {
		const char *q = rel + 15, *s = strchr(q, '/');
		if (!s || strchr(s + 1, '/')) return 0;
		char run[72];
		size_t n = (size_t)(s - q);
		if (n >= sizeof run) return 0;
		memcpy(run, q, n); run[n] = 0;
		return dl_valid_run(run);
	}
	return 0;
}

/* ------------------------------------------------------------------ config */
static int dl_parse_hostport(const char *v, uint32_t *ip, uint16_t *port, uint16_t defport)
{
	char h[32];
	const char *c = strchr(v, ':');
	size_t n = c ? (size_t)(c - v) : strlen(v);
	if (n == 0 || n >= sizeof h) return 0;
	memcpy(h, v, n); h[n] = 0;
	if (!dlp_parse_ipv4(h, ip)) return 0;
	*port = defport;
	if (c) {
		long p = strtol(c + 1, NULL, 10);
		if (p < 1 || p > 65535) return 0;
		*port = (uint16_t)p;
	}
	return 1;
}

/* a base path (PROTOCOL.md §10): ^(/[a-z0-9_-]{1,32}){1,4}$, shorter than DL_BASE_MAX */
static int dl_valid_base(const char *b)
{
	size_t n = strlen(b), seg = 0;
	int segs = 0;
	if (n < 2 || n >= DL_BASE_MAX || b[0] != '/') return 0;
	for (size_t i = 0; i < n; i++) {
		char c = b[i];
		if (c == '/') { if (i && !seg) return 0; segs++; seg = 0; continue; }
		if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return 0;
		if (++seg > 32) return 0;
	}
	return seg > 0 && segs <= 4;
}

/* "a.b.c.d[:port][/base]" (devlog.txt server=, devlog/server.txt). The Codex server's port without a path means
 * the Codex binding with its default base (PROTOCOL.md §10); any other address without a path is a standalone
 * receiver. */
static int dl_parse_target(const char *v, uint32_t *ip, uint16_t *port, char base[DL_BASE_MAX])
{
	char hp[40];
	const char *sl = strchr(v, '/');
	size_t n = sl ? (size_t)(sl - v) : strlen(v);
	if (n == 0 || n >= sizeof hp) return 0;
	memcpy(hp, v, n); hp[n] = 0;
	if (!dl_parse_hostport(hp, ip, port, DL_TCP_PORT)) return 0;
	base[0] = 0;
	if (sl) { if (!dl_valid_base(sl)) return 0; dl_cpy(base, DL_BASE_MAX, sl); }
	else if (*port == DL_CODEX_PORT) dl_cpy(base, DL_BASE_MAX, DL_CODEX_BASE);
	return 1;
}

static void dl_read_config(dl_cfg *c)
{
	memset(c, 0, sizeof *c);
	c->mode = DEVLOG_MODE_ASK;          /* a devlog.txt without mode= asks before sending (owner decision 2026-10-07) */
	c->disc_port = DL_UDP_PORT;
	c->max_total_ms = DL_MAX_TOTAL_MS;
	c->begin_ms = DL_BEGIN_MS;
	c->reach_ms = DL_REACH_MS;
	c->keep_kb = DL_KEEP_KB;
	c->keep_pending = DL_KEEP_PENDING;
	if (dlp_file_size(G.cfg_path) < 0 || dl_read_file(G.cfg_path, g_text, sizeof g_text) < 0) return;
	int enabled = 0;
	char *first = g_text;   /* a UTF-8 BOM (some editors add one) would hide "enabled": skipped; offsets stay file offsets */
	if ((unsigned char)g_text[0] == 0xEF && (unsigned char)g_text[1] == 0xBB && (unsigned char)g_text[2] == 0xBF) first += 3;
	for (char *line = first; line && *line;) {
		char *nl = strchr(line, '\n');
		if (nl) *nl = 0;
		char *k = line;
		line = nl ? nl + 1 : NULL;
		while (*k == ' ' || *k == '\t') k++;
		char *hash = strchr(k, '#');
		if (hash) *hash = 0;
		char *eq = strchr(k, '=');
		if (!eq) continue;
		*eq = 0;
		char *v = eq + 1;
		for (char *e = eq - 1; e >= k && (*e == ' ' || *e == '\t'); e--) *e = 0;
		while (*v == ' ' || *v == '\t') v++;
		for (char *e = v + strlen(v) - 1; e >= v && (*e == ' ' || *e == '\t' || *e == '\r'); e--) *e = 0;
		long t = strtol(v, NULL, 10);
		if (!strcmp(k, "enabled")) enabled = !strcmp(v, "1");
		else if (!strcmp(k, "mode"))   /* anything but auto / off (a typo) asks: never sends unasked by mistake */
			c->mode = !strcmp(v, "auto") ? DEVLOG_MODE_AUTO : !strcmp(v, "off") ? DEVLOG_MODE_OFF : DEVLOG_MODE_ASK;
		else if (!strcmp(k, "clear")) { if (!strcmp(v, "1")) { c->clear = 1; c->clear_off = (long)(v - g_text); } }
		else if (!strcmp(k, "token")) {
			if (strlen(v) == 64) {
				int ok = 1;
				for (int i = 0; i < 64; i++) { v[i] = (char)dl_lower((unsigned char)v[i]); if (!dl_ishex((unsigned char)v[i])) ok = 0; }
				if (ok) dl_cpy(c->token, sizeof c->token, v);
			}
		} else if (!strcmp(k, "server")) c->has_server = dl_parse_target(v, &c->server_ip, &c->server_port, c->server_base);
		else if (!strcmp(k, "discover")) c->has_discover = dlp_parse_ipv4(v, &c->discover_ip);
		else if (!strcmp(k, "discovery_port")) { if (t > 0 && t < 65536) c->disc_port = (uint16_t)t; }
		else if (!strcmp(k, "max_total_ms")) { if (t >= 1000 && t <= 120000) c->max_total_ms = (unsigned)t; }
		else if (!strcmp(k, "begin_ms")) { if (t >= 1000 && t <= 20000) c->begin_ms = (unsigned)t; }
		else if (!strcmp(k, "reach_ms")) { if (t >= 600 && t <= (long)DL_REACH_MS) c->reach_ms = (unsigned)t; }
		else if (!strcmp(k, "keep_kb")) { if (t >= 64 && t <= 8192) c->keep_kb = (unsigned)t; }
		else if (!strcmp(k, "keep_pending")) { if (t >= 1 && t <= 32) c->keep_pending = (int)t; }
	}
	memset(g_text, 0, sizeof g_text);   /* the token does not linger in the scratch buffer */
	c->enabled = enabled && c->token[0] && c->mode != DEVLOG_MODE_OFF;
}

/* ------------------------------------------------------------------ run ids, times */
static void dl_iso_now(char out[24])
{
	int y, mo, d, h, mi, s;
	char t[80];
	dlp_utc(&y, &mo, &d, &h, &mi, &s);
	snprintf(t, sizeof t, "%04d-%02d-%02dT%02d:%02d:%02dZ", y, mo, d, h, mi, s);
	dl_cpy(out, 24, t);
}
static void dl_new_run_id(char out[65])
{
	int y, mo, d, h, mi, s;
	uint8_t r[6];
	char hx[13];
	dlp_utc(&y, &mo, &d, &h, &mi, &s);
	dlp_random(r, sizeof r);
	dl_hex(r, sizeof r, hx);
	snprintf(out, 65, "%04d%02d%02dT%02d%02d%02dZ-%s", y, mo, d, h, mi, s, hx);
}
static long dl_days(int y, int m, int d)      /* days since 1970-01-01 (civil calendar) */
{
	y -= m <= 2;
	long era = (y >= 0 ? y : y - 399) / 400;
	long yoe = y - era * 400;
	long doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
	long doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
	return era * 146097 + doe - 719468;
}
static long dl_run_age_days(const char *run)
{
	int y, mo, d, h, mi, s;
	if (strlen(run) < 8) return 0;
	for (int i = 0; i < 8; i++) if (run[i] < '0' || run[i] > '9') return 0;
	int ry = (run[0] - '0') * 1000 + (run[1] - '0') * 100 + (run[2] - '0') * 10 + (run[3] - '0');
	int rm = (run[4] - '0') * 10 + (run[5] - '0'), rd = (run[6] - '0') * 10 + (run[7] - '0');
	dlp_utc(&y, &mo, &d, &h, &mi, &s);
	return dl_days(y, mo, d) - dl_days(ry, rm, rd);
}

/* ------------------------------------------------------------------ markers */
/* path builders: -1 (and "") when the result would not fit; such a path is never used */
static int dl_join(char *out, size_t cap, const char *a, const char *b, const char *suffix)
{
	int n = snprintf(out, cap, "%s/%s%s", a, b, suffix);
	if (n < 0 || (size_t)n >= cap) { if (cap) out[0] = 0; return -1; }
	return 0;
}
static int dl_marker_path(const char *run, char *out, size_t cap) { return dl_join(out, cap, G.pending_dir, run, ".run"); }
static int dl_copy_dir(const char *run, char *out, size_t cap) { return dl_join(out, cap, G.pending_dir, run, ""); }
/* <run>.note: written by devlog_note (any thread, own buffer), merged into the marker by every load, removed by every
 * save (the saved marker carries the merged values then) */
static int dl_note_path(const char *run, char *out, size_t cap) { return dl_join(out, cap, G.pending_dir, run, ".note"); }

static int dl_marker_save(const dl_run *r)
{
	char path[DL_PATH];
	if (dl_marker_path(r->run, path, sizeof path) != 0) return -1;
	size_t n = 0, cap = sizeof g_text;
#define DL_PUT(...) do { int w_ = snprintf(g_text + n, cap - n, __VA_ARGS__); if (w_ > 0) n += (size_t)w_ < cap - n ? (size_t)w_ : cap - n - 1; } while (0)
	DL_PUT("v=1\napp=%s\nrun=%s\ntitle_id=%s\nbuild=%s\ncommit=%s\nstarted=%s\nstate=%s\noutcome=%s\ndetail=%s\n",
	       r->app, r->run, r->title_id, r->build, r->commit, r->started, r->state, r->outcome, r->detail);
	DL_PUT("platform=%s\nmodel=%s\nfw=%s\nfree_mem_kb=%u\nfree_cdram_kb=%u\nfree_phycont_kb=%u\nuptime_ms=%llu\nattempt=%d\n",
	       r->platform, r->model, r->fw, r->free_kb, r->free_cdram_kb, r->free_phycont_kb, r->uptime_ms, r->attempt);
	DL_PUT("tag_off=%lld\n", r->tag_off);
	for (int i = 0; i < r->nsrc; i++) {
		const dl_src *s = &r->src[i];
		if (s->kind == 'f') DL_PUT("f=%s|%s\n", s->path, s->prev);
		else if (s->kind == 'x') DL_PUT("x=%s\n", s->path);
		else DL_PUT("c=%s|%s\n", s->path, s->name);
	}
#undef DL_PUT
	if (dl_write_file(path, g_text, n) != 0) return -1;
	if (dl_note_path(r->run, path, sizeof path) == 0 && dlp_file_size(path) >= 0) dlp_remove(path);
	return 0;
}

static void dl_split_bar(const char *v, char *a, size_t ac, char *b, size_t bc)
{
	const char *bar = strchr(v, '|');
	if (!bar) { dl_cpy(a, ac, v); if (bc) b[0] = 0; return; }
	size_t n = (size_t)(bar - v);
	if (n >= ac) n = ac - 1;
	memcpy(a, v, n); a[n] = 0;
	dl_cpy(b, bc, bar + 1);
}

static int dl_marker_load(const char *path, dl_run *r)
{
	memset(r, 0, sizeof *r);
	r->tag_off = -1;
	if (dl_read_file(path, g_text, sizeof g_text) <= 0) return -1;
	int version = 0;
	for (char *line = g_text; line && *line;) {
		char *nl = strchr(line, '\n');
		if (nl) *nl = 0;
		char *k = line;
		line = nl ? nl + 1 : NULL;
		char *eq = strchr(k, '=');
		if (!eq) continue;
		*eq = 0;
		const char *v = eq + 1;
		if (!strcmp(k, "v")) version = atoi(v);
		else if (!strcmp(k, "app")) dl_cpy(r->app, sizeof r->app, v);
		else if (!strcmp(k, "run")) dl_cpy(r->run, sizeof r->run, v);
		else if (!strcmp(k, "title_id")) dl_cpy(r->title_id, sizeof r->title_id, v);
		else if (!strcmp(k, "build")) dl_cpy(r->build, sizeof r->build, v);
		else if (!strcmp(k, "commit")) dl_cpy(r->commit, sizeof r->commit, v);
		else if (!strcmp(k, "started")) dl_cpy(r->started, sizeof r->started, v);
		else if (!strcmp(k, "state")) dl_cpy(r->state, sizeof r->state, v);
		else if (!strcmp(k, "outcome")) dl_cpy(r->outcome, sizeof r->outcome, v);
		else if (!strcmp(k, "detail")) dl_cpy(r->detail, sizeof r->detail, v);
		else if (!strcmp(k, "platform")) dl_cpy(r->platform, sizeof r->platform, v);
		else if (!strcmp(k, "model")) dl_cpy(r->model, sizeof r->model, v);
		else if (!strcmp(k, "fw")) dl_cpy(r->fw, sizeof r->fw, v);
		else if (!strcmp(k, "free_mem_kb")) r->free_kb = (unsigned)strtoul(v, NULL, 10);
		else if (!strcmp(k, "free_cdram_kb")) r->free_cdram_kb = (unsigned)strtoul(v, NULL, 10);
		else if (!strcmp(k, "free_phycont_kb")) r->free_phycont_kb = (unsigned)strtoul(v, NULL, 10);
		else if (!strcmp(k, "uptime_ms")) r->uptime_ms = strtoull(v, NULL, 10);
		else if (!strcmp(k, "attempt")) r->attempt = atoi(v);
		else if (!strcmp(k, "tag_off")) r->tag_off = strtoll(v, NULL, 10);
		else if ((!strcmp(k, "f") || !strcmp(k, "x") || !strcmp(k, "c")) && r->nsrc < DL_MAX_FILES) {
			dl_src *s = &r->src[r->nsrc];
			memset(s, 0, sizeof *s);
			s->kind = k[0];
			if (k[0] == 'f') dl_split_bar(v, s->path, sizeof s->path, s->prev, sizeof s->prev);
			else if (k[0] == 'x') dl_cpy(s->path, sizeof s->path, v);
			else dl_split_bar(v, s->path, sizeof s->path, s->name, sizeof s->name);
			r->nsrc++;
		}
	}
	if (version != 1 || !dl_valid_app(r->app) || !dl_valid_run(r->run)) return -1;
	/* a devlog_note written after the marker (a run about to die) wins over the marker's outcome / detail */
	char np[DL_PATH], nt[400];
	if (dl_note_path(r->run, np, sizeof np) == 0 && dl_read_file(np, nt, sizeof nt) > 0) {
		for (char *line = nt; line && *line;) {
			char *nl = strchr(line, '\n');
			if (nl) *nl = 0;
			char *eq = strchr(line, '=');
			if (eq) {
				*eq = 0;
				if (!strcmp(line, "outcome")) dl_cpy(r->outcome, sizeof r->outcome, eq + 1);
				else if (!strcmp(line, "detail")) dl_cpy(r->detail, sizeof r->detail, eq + 1);
				else if (!strcmp(line, "uptime_ms")) r->uptime_ms = strtoull(eq + 1, NULL, 10);
			}
			line = nl ? nl + 1 : NULL;
		}
	}
	return 0;
}

static int g_rm_n;
static char g_rm_names[DL_MAX_FILES * 2][72];
static int dl_collect_cb(const char *name, int is_dir, void *u)
{
	(void)u;
	if (!is_dir && g_rm_n < (int)(sizeof g_rm_names / sizeof g_rm_names[0])) dl_cpy(g_rm_names[g_rm_n++], 72, name);
	return 0;
}
static void dl_remove_run_files(const char *run)
{
	char dir[DL_PATH], p[DL_PATH];
	g_rm_n = 0;
	if (dl_copy_dir(run, dir, sizeof dir) == 0 && dlp_list_dir(dir, dl_collect_cb, NULL) == 0) {
		for (int i = 0; i < g_rm_n; i++) if (dl_join(p, sizeof p, dir, g_rm_names[i], "") == 0) dlp_remove(p);
		dlp_rmdir(dir);
	}
	if (dl_marker_path(run, p, sizeof p) == 0) dlp_remove(p);
	if (dl_note_path(run, p, sizeof p) == 0 && dlp_file_size(p) >= 0) dlp_remove(p);
}

/* ------------------------------------------------------------------ tags, copies */
/* Looks for "DEVLOG run=" tags in [from, from + span) of path, in DL_BIG pieces that overlap by DL_TAG_OVERLAP
 * bytes (so a tag on a piece boundary is seen whole). want != NULL: 1 when that run's tag is there.
 * want == NULL: 1 and first = the run id of the first tag. A stop request ends the scan early (callers that must not
 * act on a cut-short "not found" check dlp_stopping() afterwards). */
static int dl_scan_tags(const char *path, uint64_t from, uint64_t span, const char *want, char first[65])
{
	if (first) first[0] = 0;
	if (!path[0] || !dl_path_allowed(path)) return 0;
	int64_t size = dlp_file_size(path);
	if (size < 0 || (uint64_t)size <= from) return 0;
	int fd = dlp_open_read(path);
	if (fd < 0) return 0;
	if (from && dlp_seek(fd, (int64_t)from) != 0) { dlp_close(fd); return 0; }
	char wantstr[96];
	if (want) snprintf(wantstr, sizeof wantstr, DL_TAG "%s", want);
	size_t keep = 0, tl = strlen(DL_TAG);
	uint64_t done = 0;
	int found = 0;
	while (done < span && !found && !dlp_stopping()) {
		size_t room = DL_BIG - keep;
		if ((uint64_t)room > span - done) room = (size_t)(span - done);
		long r = dl_read_full(fd, g_big + keep, room);
		if (r <= 0) break;
		int eof = (size_t)r < room;
		size_t n = keep + (size_t)r;
		for (size_t i = keep; i < n; i++) if (!g_big[i]) g_big[i] = ' ';
		g_big[n] = 0;
		if (want) found = strstr(g_big, wantstr) != NULL;
		else {
			for (const char *t = strstr(g_big, DL_TAG); t; t = strstr(t + tl, DL_TAG)) {
				size_t at = (size_t)(t - g_big);
				if (at + tl + 65 > n && !eof) break;   /* maybe cut: whole in the next piece (or past the window) */
				size_t k = 0;
				t += tl;
				while (k < 64 && t[k] && t[k] != ' ' && t[k] != '\n' && t[k] != '\r') { first[k] = t[k]; k++; }
				first[k] = 0;
				if (dl_valid_run(first)) { found = 1; break; }
				first[0] = 0;
				t -= tl;
			}
		}
		done += (uint64_t)r;
		if (eof) break;
		keep = n < DL_TAG_OVERLAP ? n : DL_TAG_OVERLAP;
		memmove(g_big, g_big + n - keep, keep);
	}
	dlp_close(fd);
	return found;
}

/* does the log at path carry the tag of run? (looked for where the log ended when that run began) */
static int dl_file_has_tag(const char *path, const char *run, long long tag_off)
{
	if (tag_off >= 0) {
		uint64_t from = tag_off > 1024 ? (uint64_t)tag_off - 1024u : 0u;
		return dl_scan_tags(path, from, DL_TAG_WINDOW, run, NULL);
	}
	return dl_scan_tags(path, 0, DL_TAG_SCAN, run, NULL);   /* a marker without the offset */
}

/* the run id of the first tag in a file ("" if none within DL_TAG_SCAN) */
static void dl_file_tag(const char *path, char out[65]) { dl_scan_tags(path, 0, DL_TAG_SCAN, NULL, out); }

/* does the file carry any tag (it is the log of a devlog run)? 1 yes, 0 no (all of it read). A file bigger than
 * DL_ANY_TAG_MAX (the 8 MiB upload cap + the first-tag window; only an untagged one is read that far) or a scan cut
 * short by a stop counts as tagged: the caller then does not send it. */
#define DL_ANY_TAG_MAX (DL_FILE_CAP + DL_TAG_SCAN)
static int dl_file_any_tag(const char *path)
{
	char first[65];
	if (dl_scan_tags(path, 0, DL_ANY_TAG_MAX, NULL, first)) return 1;
	return dlp_stopping() || dlp_file_size(path) > (int64_t)DL_ANY_TAG_MAX;
}

/* copy the last cap bytes of src to dst (both checked against the logs-only rule); bytes copied, -1 error, -2 stopped
 * (devlog_cancel or a newer devlog call: the partial copy is deleted, the caller leaves the run for a later job). The
 * stop is checked per 32 KiB piece, so a stop request never waits for a whole copy. */
static long long dl_copy_tail(const char *src, const char *dst, uint64_t cap)
{
	if (!dl_path_allowed(src) || !dl_path_allowed(dst)) return -1;
	int64_t size = dlp_file_size(src);
	if (size < 0) return -1;
	int64_t off = (uint64_t)size > cap ? size - (int64_t)cap : 0;
	int in = dlp_open_read(src);
	if (in < 0) return -1;
	if (off && dlp_seek(in, off) != 0) { dlp_close(in); return -1; }
	int out = dlp_open_write(dst, 0);
	if (out < 0) { dlp_close(in); return -1; }
	int64_t left = size - off;
	long long copied = 0;
	while (left > 0) {
		if (dlp_stopping()) { copied = -2; break; }
		long r = dlp_read(in, g_big, left < (int64_t)DL_BIG ? (size_t)left : DL_BIG);
		if (r <= 0) break;   /* shrank while copying: keep what was read */
		if (dlp_write(out, g_big, (size_t)r) != r) { copied = -1; break; }
		left -= r;
		copied += r;
	}
	dlp_close(in);
	dlp_close(out);
	if (copied == -2) dlp_remove(dst);
	return copied;
}

/* ------------------------------------------------------------------ HTTP (one request per connection) */
typedef struct { int status; char body[512]; } dl_resp;

/* time left before deadline; 0 once a stop was requested (devlog_cancel): everything then ends as "out of time",
 * which keeps the runs and does not count as a failed attempt */
static unsigned dl_left(uint64_t deadline)
{
	uint64_t now = dlp_now_ms();
	if (dlp_stopping()) return 0u;
	return now >= deadline ? 0u : (unsigned)(deadline - now);
}

/* sleep that ends early on a stop request; against an absolute end, so the slices' timer slack does not add up */
static void dl_nap(unsigned ms)
{
	uint64_t end = dlp_now_ms() + ms;
	for (;;) {
		uint64_t now = dlp_now_ms();
		if (now >= end || dlp_stopping()) return;
		dlp_sleep_ms(end - now < 10u ? (unsigned)(end - now) : 10u);
	}
}

/* the request target of op ("ping", "meta", "done", "file") in the session's binding (PROTOCOL.md §4 / §10). Every
 * value comes from the strict app / run / name rules, so nothing needs percent-encoding. 0 ok, -1 too long. */
static int dl_target(const dl_sess *ss, const char *op, const dl_run *r, const char *name, char *out, size_t cap)
{
	int n;
	if (!ss->base[0]) {
		if (!r) n = snprintf(out, cap, "/v1/ping");
		else if (name) n = snprintf(out, cap, "/v1/runs/%s/%s/files/%s", r->app, r->run, name);
		else n = snprintf(out, cap, "/v1/runs/%s/%s/%s", r->app, r->run, op);
	} else {
		if (!r) n = snprintf(out, cap, "%s/v1/ping", ss->base);
		else n = snprintf(out, cap, "%s/v1/upload?op=%s&app=%s&run=%s%s%s", ss->base, op, r->app, r->run,
		                  name ? "&name=" : "", name ? name : "");
	}
	return (n > 0 && (size_t)n < cap) ? 0 : -1;
}
static unsigned dl_min(unsigned a, unsigned b) { return a < b ? a : b; }

/* the whole response, until min(now + wait_ms, lim) */
static int dl_read_response(int s, dl_resp *rp, unsigned wait_ms, uint64_t lim)
{
	uint64_t deadline = dlp_now_ms() + wait_ms;
	if (deadline > lim) deadline = lim;
	size_t n = 0;
	long clen = -1;
	char *hdr_end = NULL;
	rp->status = 0; rp->body[0] = 0;
	while (n + 1 < sizeof g_resp) {
		unsigned left = dl_left(deadline);
		if (!left) break;
		int r = dlp_tcp_recv(s, g_resp + n, sizeof g_resp - 1 - n, left);
		if (r <= 0) break;
		n += (size_t)r;
		g_resp[n] = 0;
		if (!hdr_end && (hdr_end = strstr(g_resp, "\r\n\r\n")) != NULL) {
			/* Content-Length, case-insensitive (header lines start after each CRLF before the blank line) */
			for (char *q = strstr(g_resp, "\r\n"); q && q < hdr_end; q = strstr(q, "\r\n")) {
				q += 2;
				char name[16];
				size_t k = 0;
				while (k < 15 && q[k] && q[k] != ':') { name[k] = (char)dl_lower((unsigned char)q[k]); k++; }
				name[k] = 0;
				if (q[k] == ':' && !strcmp(name, "content-length")) clen = strtol(q + k + 1, NULL, 10);
			}
		}
		if (hdr_end && clen >= 0 && (long)(n - (size_t)(hdr_end + 4 - g_resp)) >= clen) break;
	}
	g_resp[n] = 0;
	if (n < 12 || strncmp(g_resp, "HTTP/1.", 7) != 0) return -1;
	rp->status = atoi(g_resp + 9);
	if (hdr_end) dl_cpy(rp->body, sizeof rp->body, hdr_end + 4);
	return rp->status > 0 ? 0 : -1;
}

/* text guarantee (PROTOCOL.md §4.3): control bytes other than TAB LF CR ESC become '?' */
static unsigned dl_sanitize(unsigned char *p, size_t n)
{
	unsigned k = 0;
	for (size_t i = 0; i < n; i++) {
		unsigned char c = p[i];
		if ((c < 0x20 && c != '\t' && c != '\n' && c != '\r' && c != 0x1b) || c == 0x7f) { p[i] = '?'; k++; }
	}
	return k;
}

/* first pass over [off, off+len) of fd: the bytes dl_sanitize replaces and the SHA-256 of the range as it will be
 * sent (the request is authenticated with it before the body goes out). 0 ok, -1 the range cannot be read, -2 stopped. */
static int dl_prepass(int fd, uint64_t off, uint64_t len, long *nsan, char sha_hex[65])
{
	if (dlp_seek(fd, (int64_t)off) != 0) return -1;
	dl_sha256 h;
	dl_sha256_init(&h);
	long k = 0;
	while (len > 0) {
		if (dlp_stopping()) return -2;   /* a stop request does not wait for the hash of an 8 MiB file */
		long r = dlp_read(fd, g_buf, len < sizeof g_buf ? (size_t)len : sizeof g_buf);
		if (r <= 0) return -1;
		k += (long)dl_sanitize(g_buf, (size_t)r);
		dl_sha256_update(&h, g_buf, (size_t)r);
		len -= (uint64_t)r;
	}
	uint8_t d[32];
	dl_sha256_final(&h, d);
	dl_hex(d, 32, sha_hex);
	*nsan = k;
	return 0;
}

static int dl_send_rc(int r) { return r == 0 ? 0 : r == -3 ? DLH_BUDGET : r == -2 ? -2 : -1; }

/* One request. The body is mem (small) or a file range (streamed, sanitised, hashed: body_sha from dl_prepass).
 * Authenticated without sending the token: Authorization: SHPD1 nonce=<16hex> body=<sha256> mac=<MAC(req|...)>.
 * Every wait stops at the call's deadline (the reach deadline until the receiver has answered once, the transfer
 * budget after that); a send that makes no progress for DL_STALL_MS is abandoned.
 * Returns 0 with rp filled, DLH_BUDGET when the time budget ran out, other < 0 for transport errors. */
static int dl_http(dl_sess *ss, const char *method, const char *path, const char *ctype, const char *extra_hdrs,
                   const char *mem, size_t mem_len, int fd, uint64_t flen, const char *body_sha, uint8_t sha_out[32],
                   dl_resp *rp, unsigned connect_ms, unsigned resp_ms, char nonce_out[17])
{
	uint64_t lim = ss->reached ? ss->deadline : ss->reach_deadline;
	if (!dl_left(lim)) return DLH_BUDGET;
	char sha_hex[65], nonce[17], m[DL_TARGET + 120], mac[33], req[DL_TARGET + 760];
	if (body_sha) dl_cpy(sha_hex, sizeof sha_hex, body_sha);
	else {
		dl_sha256 h;
		uint8_t d[32];
		dl_sha256_init(&h); dl_sha256_update(&h, mem, mem_len); dl_sha256_final(&h, d);
		dl_hex(d, 32, sha_hex);
	}
	uint8_t nb[8];
	dlp_random(nb, sizeof nb);
	dl_hex(nb, sizeof nb, nonce);
	if (nonce_out) dl_cpy(nonce_out, 17, nonce);
	int mn = snprintf(m, sizeof m, "req|%s|%s|%s|%s", method, path, nonce, sha_hex);
	if (mn <= 0 || mn >= (int)sizeof m) return -10;
	dl_mac32(G.cfg.token, m, mac);
	uint64_t blen = fd >= 0 ? flen : mem_len;
	int hn = snprintf(req, sizeof req,
	                  "%s %s HTTP/1.1\r\nHost: %s\r\nAuthorization: SHPD1 nonce=%s body=%s mac=%s\r\n"
	                  "User-Agent: shepherd-devlog/3 (%s)\r\nContent-Type: %s\r\nContent-Length: %llu\r\n"
	                  "Connection: close\r\n%s\r\n",
	                  method, path, ss->server, nonce, sha_hex, mac, G.cur.app[0] ? G.cur.app : "devlog", ctype,
	                  (unsigned long long)blen, extra_hdrs ? extra_hdrs : "");
	if (hn <= 0 || hn >= (int)sizeof req) return -10;
	unsigned left = dl_left(lim);
	if (!left) return DLH_BUDGET;
	int s = dlp_tcp_connect(ss->ip, ss->port, dl_min(connect_ms, left));
	if (s < 0) return dl_left(lim) ? -1 : DLH_BUDGET;
	int rc = dl_send_rc(dlp_tcp_send_all(s, req, (size_t)hn, DL_STALL_MS, lim));
	if (rc == 0 && fd < 0 && mem_len) rc = dl_send_rc(dlp_tcp_send_all(s, mem, mem_len, DL_STALL_MS, lim));
	if (rc == 0 && fd >= 0) {
		dl_sha256 h;
		dl_sha256_init(&h);
		uint64_t rest = flen;
		while (rest > 0 && rc == 0) {
			long r = dlp_read(fd, g_buf, rest < sizeof g_buf ? (size_t)rest : sizeof g_buf);
			if (r <= 0) { rc = -5; break; }   /* the file shrank: abort, the receiver discards the short body */
			dl_sanitize(g_buf, (size_t)r);
			dl_sha256_update(&h, g_buf, (size_t)r);
			rc = dl_send_rc(dlp_tcp_send_all(s, g_buf, (size_t)r, DL_STALL_MS, lim));
			rest -= (uint64_t)r;
		}
		dl_sha256_final(&h, sha_out);
	}
	/* even after a failed send the receiver may have answered (413 after the headers, then close): read it */
	int got = dl_read_response(s, rp, rc == 0 ? resp_ms : dl_min(resp_ms, 500), lim);
	dlp_tcp_close(s);
	if (got == 0) return 0;
	if (rc == 0) dlp_err("no answer to the request", 0);
	if (rc < 0) return rc;
	return dl_left(lim) ? -6 : DLH_BUDGET;
}

/* ------------------------------------------------------------------ reaching the receiver */
static int dl_ping(dl_sess *ss, unsigned connect_ms, unsigned resp_ms)
{
	dl_resp rp;
	char nonce[17], host[16], m[96], want[33], target[DL_TARGET];
	if (dl_target(ss, "ping", NULL, NULL, target, sizeof target) != 0) return -7;
	int r = dl_http(ss, "GET", target, "text/plain", NULL, NULL, 0, -1, 0, NULL, NULL, &rp, connect_ms, resp_ms, nonce);
	if (r < 0) return r;
	/* a shpdlog server refuses a bad MAC with 401 and an empty body (or one starting "shpdlog" / "replayed"); a
	 * 401 / 403 with any other body is some other service's login wall (e.g. the Codex server before its devlog
	 * routes are installed): not our receiver, so the next address is tried instead of giving up on the token */
	if (rp.status == 401 || rp.status == 403) {
		if (!rp.body[0] || !strncmp(rp.body, "shpdlog", 7) || !strncmp(rp.body, "replayed", 8)) { ss->auth_failed = 1; return -401; }
		return -7;
	}
	if (rp.status != 200 || strncmp(rp.body, "ok shpdlog/1", 12) != 0) return -7;
	/* the answer must prove the receiver knows the token, for this nonce and this address: nothing else is sent to
	 * something that only looks like a receiver (a stale cached address, another device after a DHCP change) */
	dlp_format_ipv4(ss->ip, host);
	snprintf(m, sizeof m, "pong|%s|%s|%u", nonce, host, (unsigned)ss->port);
	dl_mac32(G.cfg.token, m, want);
	const char *pf = strstr(rp.body, "proof=");
	int diff = !pf || strlen(pf + 6) < 32;
	for (int i = 0; !diff && i < 32; i++) diff |= pf[6 + i] != want[i];
	if (diff) { dl_log("DEVLOG %s answered without a valid proof: not the receiver of this token, ignored", ss->server); return -7; }
	const char *c = strstr(rp.body, "max_file=");
	if (c) { unsigned long v = strtoul(c + 9, NULL, 10); if (v > 0 && v < ss->max_file) ss->max_file = (uint32_t)v; }
	c = strstr(rp.body, "max_run=");
	if (c) { unsigned long v = strtoul(c + 8, NULL, 10); if (v > 0 && v < ss->max_run) ss->max_run = (uint32_t)v; }
	return 0;
}

static void dl_set_server(dl_sess *ss, uint32_t ip, uint16_t port, const char *base)
{
	char a[16];
	ss->ip = ip; ss->port = port;
	dl_cpy(ss->base, sizeof ss->base, base ? base : "");
	dlp_format_ipv4(ip, a);
	snprintf(ss->server, sizeof ss->server, "%s:%u", a, (unsigned)port);
}

/* one HERE line: SHPDLOG1 HERE host=<ip> port=<n> nonce=<16hex> [base=<path>] proof=<32hex>
 * proof = MAC("here|" nonce "|" host "|" port), or with base= MAC("here|" nonce "|" host "|" port "|" base): a Codex
 * server says where its routes live (PROTOCOL.md §10), and the proof covers that too */
static int dl_check_here(const char *msg, const char nonces[2][17], int nn, uint32_t *ip, uint16_t *port, char base[DL_BASE_MAX])
{
	char host[20] = "", ports[8] = "", nonce[20] = "", proof[40] = "", b[DL_BASE_MAX + 8] = "";
	int has_base = 0;
	if (strncmp(msg, "SHPDLOG1 HERE ", 14) != 0) return 0;
	char tmp[256];
	dl_cpy(tmp, sizeof tmp, msg + 14);
	size_t tl = strlen(tmp);
	while (tl && (tmp[tl - 1] == '\n' || tmp[tl - 1] == '\r')) tmp[--tl] = 0;
	for (char *t = tmp; t && *t;) {
		char *sp = strchr(t, ' ');
		if (sp) *sp = 0;
		if (dl_starts(t, "host=")) dl_cpy(host, sizeof host, t + 5);
		else if (dl_starts(t, "port=")) dl_cpy(ports, sizeof ports, t + 5);
		else if (dl_starts(t, "nonce=")) dl_cpy(nonce, sizeof nonce, t + 6);
		else if (dl_starts(t, "proof=")) dl_cpy(proof, sizeof proof, t + 6);
		else if (dl_starts(t, "base=")) { dl_cpy(b, sizeof b, t + 5); has_base = 1; }
		t = sp ? sp + 1 : NULL;
	}
	int which = -1;
	for (int i = 0; i < nn; i++) if (!strcmp(nonce, nonces[i])) which = i;
	if (which < 0 || strlen(proof) != 32 || !dlp_parse_ipv4(host, ip)) return 0;
	if (has_base && !dl_valid_base(b)) return 0;
	long p = strtol(ports, NULL, 10);
	if (p < 1 || p > 65535) return 0;
	char m[160], want[33];
	if (has_base) snprintf(m, sizeof m, "here|%s|%s|%s|%s", nonce, host, ports, b);
	else snprintf(m, sizeof m, "here|%s|%s|%s", nonce, host, ports);
	dl_mac32(G.cfg.token, m, want);
	int diff = 0;
	for (int i = 0; i < 32; i++) diff |= want[i] ^ proof[i];
	if (diff) return 0;
	*port = (uint16_t)p;
	dl_cpy(base, DL_BASE_MAX, has_base ? b : "");
	return 1;
}

/* an address to try: from devlog.txt, the cache, or discovery */
typedef struct { uint32_t ip; uint16_t port; char base[DL_BASE_MAX]; int from_cfg, tries, dead; } dl_cand;

static int dl_cand_same(const dl_cand *a, const dl_cand *b) { return a->ip == b->ip && a->port == b->port && !strcmp(a->base, b->base); }

/* puts d first (a fresh discovery answer is the best guess); an address already known moves there with its tries
 * reset, unless it already proved not to be our receiver */
static void dl_cand_front(dl_cand *c, int *nc, const dl_cand *d)
{
	int at = -1;
	for (int i = 0; i < *nc; i++) if (dl_cand_same(&c[i], d)) at = i;
	dl_cand x = *d;
	if (at >= 0) {
		if (c[at].dead) return;
		x = c[at]; x.tries = 0;
		for (int i = at; i > 0; i--) c[i] = c[i - 1];
	} else {
		if (*nc < 4) (*nc)++;
		for (int i = *nc - 1; i > 0; i--) c[i] = c[i - 1];
	}
	c[0] = x;
}

/* the live address tried least often (ties: the preferred one, earlier in the list); -1 none left */
static int dl_cand_pick(const dl_cand *c, int nc)
{
	int k = -1;
	for (int i = 0; i < nc; i++) if (!c[i].dead && c[i].tries < 4 && (k < 0 || c[i].tries < c[k].tries)) k = i;
	return k;
}

/* DISCOVER carries a fresh nonce and its MAC, nothing stable about the install (no key id). Collects up to max
 * verified answers, the Codex server first (an answer with base=): once one standalone receiver answered, it listens
 * at most 150 ms more for a Codex answer; a Codex answer ends the listening at once. Returns how many. */
static int dl_discover(uint32_t my_ip, uint32_t my_mask, unsigned budget_ms, dl_cand *out, int max)
{
	uint32_t targets[2];
	int nt = 0;
	if (G.cfg.has_discover) targets[nt++] = G.cfg.discover_ip;
	else {
		targets[nt++] = 0xFFFFFFFFu;
		if (my_ip && my_mask && my_mask != 0xFFFFFFFFu) targets[nt++] = (my_ip & my_mask) | ~my_mask;
	}
	int s = dlp_udp_open();
	if (s < 0) return 0;
	char nonces[2][17];
	int found = 0, codex = 0;
	uint64_t end = dlp_now_ms() + dl_min(budget_ms, 700), settle = 0;
	for (int round = 0; round < 2 && !found; round++) {
		if (round && dl_left(end) < 50) break;   /* a short discovery (dl_reach's Codex check): one round only */
		uint8_t rb[8];
		dlp_random(rb, sizeof rb);
		dl_hex(rb, sizeof rb, nonces[round]);
		char m[64], mac[33], msg[160];
		snprintf(m, sizeof m, "discover|%s", nonces[round]);
		dl_mac32(G.cfg.token, m, mac);
		int ml = snprintf(msg, sizeof msg, "SHPDLOG1 DISCOVER nonce=%s mac=%s\n", nonces[round], mac);
		for (int i = 0; i < nt; i++) dlp_udp_sendto(s, targets[i], G.cfg.disc_port, msg, (size_t)ml);
		uint64_t wait_end = round == 0 ? dlp_now_ms() + 300 : end;
		if (wait_end > end) wait_end = end;
		while (!codex) {
			uint64_t lim = settle && settle < wait_end ? settle : wait_end;
			unsigned left = dl_left(lim);
			if (!left) break;
			char in[256];
			uint32_t from = 0;
			int r = dlp_udp_recvfrom(s, in, sizeof in - 1, left, &from);
			if (r <= 0) { if (r == -1 && dl_left(lim) > 5) dlp_sleep_ms(5); continue; }
			in[r] = 0;
			dl_cand c;
			memset(&c, 0, sizeof c);
			if (!dl_check_here(in, (const char (*)[17])nonces, round + 1, &c.ip, &c.port, c.base)) continue;
			int dup = 0;
			for (int i = 0; i < found; i++) dup |= dl_cand_same(&out[i], &c);
			if (dup) continue;
			if (c.base[0]) {          /* the Codex server: preferred, first */
				if (found == max) found--;
				for (int i = found; i > 0; i--) out[i] = out[i - 1];
				out[0] = c; found++; codex = 1;
			} else if (found < max) {
				out[found++] = c;
				if (!settle) settle = dlp_now_ms() + 150;
			}
		}
	}
	dlp_udp_close(s);
	memset(nonces, 0, sizeof nonces);
	return found;
}

static void dl_cache_server(const dl_sess *ss)
{
	char p[DL_PATH], t[24 + DL_BASE_MAX + 2];
	if (dl_join(p, sizeof p, G.state_dir, "server.txt", "") != 0) return;
	int n = snprintf(t, sizeof t, "%s%s\n", ss->server, ss->base);
	if (n > 0 && (size_t)n < sizeof t) dl_write_file(p, t, (size_t)n);
}
static int dl_cached_server(uint32_t *ip, uint16_t *port, char base[DL_BASE_MAX])
{
	char p[DL_PATH], t[24 + DL_BASE_MAX + 16];
	if (dl_join(p, sizeof p, G.state_dir, "server.txt", "") != 0) return 0;
	if (dl_read_file(p, t, sizeof t) <= 0) return 0;
	char *nl = strchr(t, '\n');
	if (nl) *nl = 0;
	return dl_parse_target(t, ip, port, base);
}

/* Find the receiver within the reach budget (PROTOCOL.md §6). Addresses: devlog.txt server= or the cached one, then
 * what discovery finds (the Codex server before a standalone receiver, §10); the least-tried live one goes next, an
 * address that answers without a valid proof is dropped. 1 reached, 0 not. */
static int dl_reach(dl_sess *ss)
{
	if (ss->tried) return ss->reached;
	ss->tried = 1;
	ss->max_file = DL_FILE_CAP;
	ss->max_run = DL_RUN_CAP;
	uint32_t my_ip = 0, my_mask = 0;
	ss->net = dlp_net_up(&my_ip, &my_mask);
	if (ss->net <= 0) return 0;
	dl_cand c[4];
	int nc = 0, discovered = 0, nb = 0;
	memset(c, 0, sizeof c);
	if (G.cfg.has_server) {
		c[0].ip = G.cfg.server_ip; c[0].port = G.cfg.server_port; c[0].from_cfg = 1;
		dl_cpy(c[0].base, sizeof c[0].base, G.cfg.server_base);
		nc = 1;
	} else if (dl_cached_server(&c[0].ip, &c[0].port, c[0].base)) nc = 1;
	/* the known address is a standalone receiver (no base: devlog.txt, or cached after a run went to the fallback):
	 * the Codex server is preferred (§10), so a short discovery looks for it first. A Codex answer goes in front;
	 * standalone answers are not used here (the known address is tried as before, then the normal discovery). */
	if (nc && !c[0].base[0] && dl_left(ss->reach_deadline) > DL_PREFER_MS + 300u) {
		dl_cand d[2];
		int nd = dl_discover(my_ip, my_mask, DL_PREFER_MS, d, 2);
		for (int i = nd - 1; i >= 0; i--)
			if (d[i].base[0]) { dl_cand_front(c, &nc, &d[i]); discovered = 1; }
	}
	static const unsigned BACKOFF[] = { 100, 250, 500, 500, 500 };
	for (;;) {
		unsigned left = dl_left(ss->reach_deadline);
		if (left < 20) break;
		int k = dl_cand_pick(c, nc), last = 0;
		if (k >= 0) {
			dl_set_server(ss, c[k].ip, c[k].port, c[k].base);
			last = ++c[k].tries;
			int r = dl_ping(ss, dl_min(DL_CONNECT_MS, left), 800);
			if (r == 0) {
				ss->reached = 1;
				if (!c[k].from_cfg) dl_cache_server(ss);
				return 1;
			}
			if (r == -401) return 0;
			if (r == -7) c[k].dead = 1;   /* something answered, but not our receiver */
		}
		left = dl_left(ss->reach_deadline);
		if (!discovered && left > 100) {
			/* no address, or the known one did not answer (the Mac's DHCP address may have changed) */
			discovered = 1;
			dl_cand d[2];
			int nd = dl_discover(my_ip, my_mask, left, d, 2);
			for (int i = nd - 1; i >= 0; i--) dl_cand_front(c, &nc, &d[i]);
			if (nd) continue;
		}
		int nk = dl_cand_pick(c, nc);
		if (nk < 0) break;                          /* nothing left to try */
		if (k >= 0 && c[nk].tries < last) continue; /* another address, tried less often: at once */
		left = dl_left(ss->reach_deadline);
		unsigned b = BACKOFF[nb < 4 ? nb++ : 4];
		if (left <= b + 20) break;
		dl_nap(b);
	}
	return 0;
}

/* ------------------------------------------------------------------ sending one run */
static int dl_status_permanent(int st) { return st == 400 || st == 404 || st == 405 || st == 411 || st == 413 || st == 415 || st == 422; }

/* the meta (PROTOCOL.md §4). Every value is printable ASCII with MAC-shaped text masked. reduced: only the keys this
 * client makes itself (no title_id / build / commit / detail / files): the retry after the receiver refused the full
 * meta, so a strange app string cannot cost the run its logs. */
static size_t dl_build_meta(const dl_run *r, char *out, size_t cap, int reduced)
{
	size_t n = 0;
	char det[201], files[257], tid[16], build[100], commit[48], plat[8], model[16], fw[16], outc[12], started[24];
	dl_meta_value(det, sizeof det, r->detail);
	dl_meta_value(tid, sizeof tid, r->title_id);
	dl_meta_value(build, sizeof build, r->build);
	dl_meta_value(commit, sizeof commit, r->commit);
	dl_meta_value(plat, sizeof plat, r->platform);
	dl_meta_value(model, sizeof model, r->model);
	dl_meta_value(fw, sizeof fw, r->fw);
	dl_meta_value(outc, sizeof outc, r->outcome[0] ? r->outcome : "unknown");
	dl_meta_value(started, sizeof started, r->started);
	files[0] = 0;
	for (int i = 0; i < r->nsrc; i++) {
		size_t fl = strlen(files);
		snprintf(files + fl, sizeof files - fl, "%s%s", i ? "," : "", r->src[i].name);
	}
	dl_mask_macs(files);
#define DL_PUT(...) do { int w_ = snprintf(out + n, cap - n, __VA_ARGS__); if (w_ > 0) n += (size_t)w_ < cap - n ? (size_t)w_ : cap - n - 1; } while (0)
	DL_PUT("proto=1\nclient=" DEVLOG_CLIENT "\nplatform=%s\napp=%s\n", plat, r->app);
	if (!reduced) {
		DL_PUT("title_id=%s\nbuild=%s\n", tid, build);
		if (commit[0]) DL_PUT("commit=%s\n", commit);
	}
	DL_PUT("fw=%s\nmodel=%s\nfree_mem_kb=%u\n", fw, model, r->free_kb);
	if (!strcmp(r->platform, "vita")) DL_PUT("free_cdram_kb=%u\nfree_phycont_kb=%u\n", r->free_cdram_kb, r->free_phycont_kb);
	DL_PUT("outcome=%s\n", outc);
	if (!reduced && det[0]) DL_PUT("detail=%s\n", det);
	DL_PUT("started=%s\nuptime_ms=%llu\nattempt=%d\nlate=%d\n", started, r->uptime_ms, r->attempt + 1, r->late);
	if (reduced) DL_PUT("meta=reduced\n");
	else DL_PUT("files=%s\n", files);
#undef DL_PUT
	return n;
}

/* the first line of a receiver answer, printable, for the app's log */
static void dl_reason(const dl_resp *rp, char *out, size_t cap)
{
	dl_clean(out, cap, rp->body);   /* line breaks become spaces */
	size_t k = strlen(out);
	while (k && out[k - 1] == ' ') out[--k] = 0;
}

/* POST meta; on a permanent refusal retry once with the reduced meta. 1 ok, 0 transient, 2 out of time,
 * -1 refused for good, -2 bad token */
static int dl_send_meta(dl_sess *ss, dl_run *r)
{
	char path[DL_TARGET], why[120];
	dl_resp rp;
	if (dl_target(ss, "meta", r, NULL, path, sizeof path) != 0) return -1;
	for (int reduced = 0; reduced < 2; reduced++) {
		size_t ml = dl_build_meta(r, g_text, sizeof g_text, reduced);
		unsigned left = dl_left(ss->deadline);
		if (!left) return 2;
		int h = dl_http(ss, "POST", path, "text/plain", NULL, g_text, ml, -1, 0, NULL, NULL, &rp, dl_min(1500, left), DL_RESP_MS, NULL);
		if (h == DLH_BUDGET) return 2;
		if (h < 0) return 0;
		if (rp.status == 401 || rp.status == 403) { ss->auth_failed = 1; return -2; }
		if (rp.status == 200) return 1;
		if (!dl_status_permanent(rp.status)) return 0;
		dl_reason(&rp, why, sizeof why);
		if (!reduced) dl_log("DEVLOG run %s: receiver refused the meta (HTTP %d: %s); retrying with the client's own keys only",
		                     r->run, rp.status, why);
		else { dl_log("DEVLOG run %s: receiver refused even the reduced meta (HTTP %d: %s): run dropped", r->run, rp.status, why);
		       ss->reject_status = rp.status; return -1; }
	}
	return 0;
}

/* 1 sent, 0 transient (keep, counts as an attempt), 2 out of time (keep, not an attempt), -1 refused for good
 * (drop), -2 bad token (keep, stop everything) */
static int dl_send_run(dl_sess *ss, dl_run *r, devlog_result *res, uint64_t *bytes_out)
{
	char path[DL_TARGET], hdr[160], why[120], fsha[65];
	dl_resp rp;
	uint8_t sha[32];
	/* upload names: base names, deduplicated */
	for (int i = 0; i < r->nsrc; i++) {
		if (r->src[i].kind != 'c' || !r->src[i].name[0]) dl_cpy(r->src[i].name, sizeof r->src[i].name, dl_base(r->src[i].path));
		for (int j = 0, guard = 0; j < i && guard < 4 * DL_MAX_FILES; j++) {
			if (strcmp(r->src[i].name, r->src[j].name)) continue;
			char nn[72], *dot = strrchr(r->src[i].name, '.');
			size_t stem = dot ? (size_t)(dot - r->src[i].name) : strlen(r->src[i].name);
			snprintf(nn, sizeof nn, "%.*s-%d%s", (int)(stem > 52 ? 52 : stem), r->src[i].name, ++guard + i, dot ? dot : "");
			dl_cpy(r->src[i].name, sizeof r->src[i].name, nn);
			j = -1;
		}
	}
	int ms = dl_send_meta(ss, r);
	if (ms != 1) return ms;
	uint64_t run_bytes = 0;
	unsigned left;
	int h;
	for (int i = 0; i < r->nsrc; i++) {
		dl_src *s = &r->src[i];
		if (!dl_path_allowed(s->path) || !dl_valid_name(s->name)) {
			dl_log("DEVLOG refused %s (not a log path)", s->path);
			if (res) res->files_dropped++;
			continue;
		}
		int64_t size = dlp_file_size(s->path);
		if (size < 0) continue;
		uint32_t cap = ss->max_file < DL_FILE_CAP ? ss->max_file : DL_FILE_CAP;
		uint64_t off = (uint64_t)size > cap ? (uint64_t)size - cap : 0, len = (uint64_t)size - off;
		uint32_t run_cap = ss->max_run < DL_RUN_CAP ? ss->max_run : DL_RUN_CAP;
		if (run_bytes + len > run_cap) {
			dl_log("DEVLOG %s skipped: run cap %u bytes", s->name, (unsigned)run_cap);
			if (res) res->files_dropped++;
			continue;
		}
		int fd = dlp_open_read(s->path);
		if (fd < 0) continue;
		/* first pass: the bytes the text guarantee replaces + the SHA-256 the request is authenticated with */
		long nsan = 0;
		int pp = dl_prepass(fd, off, len, &nsan, fsha);
		if (pp == -2) { dlp_close(fd); return 2; }   /* stopped: the run stays kept, not a failed attempt */
		if (pp != 0 || dlp_seek(fd, (int64_t)off) != 0) { dlp_close(fd); continue; }
		snprintf(hdr, sizeof hdr, "X-Shpd-File-Size: %lld\r\nX-Shpd-Offset: %llu\r\nX-Shpd-Sanitized: %ld\r\n",
		         (long long)size, (unsigned long long)off, nsan);
		if (dl_target(ss, "file", r, s->name, path, sizeof path) != 0) { dlp_close(fd); continue; }
		left = dl_left(ss->deadline);
		if (!left) { dlp_close(fd); return 2; }
		h = dl_http(ss, "POST", path, "text/plain; charset=utf-8", hdr, NULL, 0, fd, len, fsha, sha, &rp,
		            dl_min(1500, left), DL_RESP_MS, NULL);
		dlp_close(fd);
		if (h == DLH_BUDGET) return 2;
		if (h < 0) return 0;
		if (rp.status == 401 || rp.status == 403) { ss->auth_failed = 1; return -2; }
		if (rp.status == 200) {
			char hx[65];
			dl_hex(sha, 32, hx);
			const char *got = strstr(rp.body, "sha256=");
			if (!got || strncmp(got + 7, hx, 64) != 0) { dl_log("DEVLOG %s: receiver hash differs; retry later", s->name); return 0; }
			run_bytes += len;
			if (res) { res->files_sent++; res->bytes_sent += len; }
			if (nsan) dl_log("DEVLOG %s: %ld control byte(s) sent as '?'", s->name, nsan);
			continue;
		}
		if (dl_status_permanent(rp.status)) {
			dl_reason(&rp, why, sizeof why);
			dl_log("DEVLOG %s refused by the receiver (HTTP %d: %s): not sent", s->name, rp.status, why);
			if (res) res->files_dropped++;
			continue;
		}
		return 0;   /* 409 / 429 / 5xx / anything else: transient */
	}
	char done[32];
	char outc[12];
	dl_meta_value(outc, sizeof outc, r->outcome[0] ? r->outcome : "unknown");
	int dn = snprintf(done, sizeof done, "outcome=%s\n", outc);
	if (dl_target(ss, "done", r, NULL, path, sizeof path) != 0) return -1;
	left = dl_left(ss->deadline);
	if (!left) return 2;
	h = dl_http(ss, "POST", path, "text/plain", NULL, done, (size_t)dn, -1, 0, NULL, NULL, &rp, dl_min(1500, left), DL_RESP_MS, NULL);
	if (h == DLH_BUDGET) return 2;
	if (h < 0) return 0;
	if (rp.status == 401 || rp.status == 403) { ss->auth_failed = 1; return -2; }
	if (rp.status != 200 && !dl_status_permanent(rp.status)) return 0;
	if (bytes_out) *bytes_out = run_bytes;
	return 1;
}

/* ------------------------------------------------------------------ pending runs */
static int g_mk_n;
static char g_mk[DL_MAX_MARKERS][72];
static int dl_marker_cb(const char *name, int is_dir, void *u)
{
	(void)u;
	if (!is_dir && dl_ends(name, ".run") && strlen(name) < 72 && g_mk_n < DL_MAX_MARKERS) dl_cpy(g_mk[g_mk_n++], 72, name);
	return 0;
}
static int dl_cmp(const void *a, const void *b) { return strcmp((const char *)a, (const char *)b); }
static void dl_list_markers(void)
{
	g_mk_n = 0;
	dlp_list_dir(G.pending_dir, dl_marker_cb, NULL);
	qsort(g_mk, (size_t)g_mk_n, sizeof g_mk[0], dl_cmp);   /* run ids start with the UTC time: oldest first */
}
static char g_mk_isdir[DL_MAX_MARKERS];
static int dl_any_cb(const char *name, int is_dir, void *u)
{
	(void)u;
	if (strlen(name) < 72 && g_mk_n < DL_MAX_MARKERS) { g_mk_isdir[g_mk_n] = (char)is_dir; dl_cpy(g_mk[g_mk_n++], 72, name); }
	return 0;
}

static void dl_add_detail(dl_run *r, const char *fmt, const char *name)
{
	char d[400], m[160];
	snprintf(m, sizeof m, fmt, name);
	if (snprintf(d, sizeof d, "%s%s%s", r->detail, r->detail[0] ? "; " : "", m) > 0)
		dl_cpy(r->detail, sizeof r->detail, d);   /* truncated to the detail's 200 chars */
}

/* why an older run's log cannot be sent: gone, overwritten by later (tagged) launches, or no tag at all */
static void dl_note_missing(dl_run *r, const dl_src *s)
{
	const char *name = dl_base(s->path);
	int any = dlp_file_size(s->path) >= 0 || (s->prev[0] && dlp_file_size(s->prev) >= 0);
	char first[65] = "";
	if (!any) { dl_add_detail(r, "%.64s: no log file left", name); return; }
	/* a later run's tag in either file = overwritten by later (devlog-on) launches; the tag of the launch running
	 * now does not count (its own log took the name a moment ago) */
	if (s->prev[0]) dl_file_tag(s->prev, first);
	if (first[0] && G.active && !strcmp(first, G.cur.run)) first[0] = 0;
	if (!first[0]) dl_file_tag(s->path, first);
	if (first[0] && G.active && !strcmp(first, G.cur.run)) first[0] = 0;
	if (first[0]) dl_add_detail(r, "%.64s log overwritten by later launches", name);
	else dl_add_detail(r, "%.64s: this run's tag is not in the log (rewritten while devlog was off?)", name);
}

/* a run that is not 'copied' yet: find its logs (by tag), copy their tails into pending/<run>/. In place:
 * src[i] becomes src[no] (no <= i), read whole before it is overwritten. 1 done (marker saved as 'copied'), 0 stopped
 * (devlog_cancel or a newer devlog call): r is then half rewritten and must not be saved; the marker on the card is
 * unchanged, so a later job (the run-end job, the next launch) adopts the run again. */
static int dl_adopt(dl_run *r, int is_current)
{
	char dir[DL_PATH];
	if (dl_copy_dir(r->run, dir, sizeof dir) != 0) return 1;
	dlp_mkdir(dir);
	uint64_t keep = (uint64_t)G.cfg.keep_kb * 1024u;
	int no = 0;
	for (int i = 0; i < r->nsrc && no < DL_MAX_FILES; i++) {
		if (dlp_stopping()) return 0;
		dl_src s = r->src[i];
		const char *from = NULL;
		if (s.kind == 'c') { r->src[no++] = s; continue; }
		if (s.kind == 'f') {
			/* the current run's own log is certain; an older run's log is the candidate that carries its tag */
			if (is_current) from = s.path;
			else if (dl_file_has_tag(s.prev, r->run, r->tag_off)) from = s.prev;
			else if (dl_file_has_tag(s.path, r->run, r->tag_off)) from = s.path;
			else if (!dlp_stopping()) dl_note_missing(r, &s);   /* say so in the meta instead of sending a wrong log */
			if (dlp_stopping()) return 0;   /* a scan cut short proves nothing */
		} else {
			from = s.path;   /* extras carry no tag: taken as they are (meta says late=1) */
		}
		int64_t size = from ? dlp_file_size(from) : -1;
		if (size < 0) continue;
		uint64_t want = (uint64_t)size < keep ? (uint64_t)size : keep;
		if (want > g_copy_left) {
			if (g_copy_left < 4096) { dl_add_detail(r, "%.64s not kept (copy budget of this call used up)", dl_base(s.path)); continue; }
			want = g_copy_left;
		}
		dl_src c;
		memset(&c, 0, sizeof c);
		c.kind = 'c';
		dl_cpy(c.name, sizeof c.name, dl_base(s.path));
		for (int j = 0; j < no; j++)
			if (!strcmp(r->src[j].name, c.name)) { char nn[80]; snprintf(nn, sizeof nn, "x%d-%.60s", i, dl_base(s.path)); dl_cpy(c.name, sizeof c.name, nn); }
		if (dl_join(c.path, sizeof c.path, dir, c.name, "") != 0) continue;
		long long got = dl_copy_tail(from, c.path, want);
		if (got == -2) return 0;
		if (got < 0) continue;
		g_copy_left -= (uint64_t)got <= g_copy_left ? (uint64_t)got : g_copy_left;
		if ((uint64_t)size > (uint64_t)got) dl_log("DEVLOG kept the last %lld of %lld bytes of %s", got, (long long)size, c.name);
		r->src[no++] = c;
	}
	if (dlp_stopping()) return 0;
	r->nsrc = no;
	dl_cpy(r->state, sizeof r->state, "copied");
	dl_marker_save(r);
	return 1;
}

/* copy every older run that never got copied (crashed runs, interrupted ends, copies cut short by a stop), drop
 * expired ones, cap the count. Ends early on a stop request (what is left is adopted by a later job). */
static void dl_adopt_all(void)
{
	dl_list_markers();
	/* retention (keep_pending, default 2 = the current run + the previous one): with a run active, it is one of them */
	int alive = 0, cap = G.cfg.keep_pending - (G.active ? 1 : 0);
	if (cap < 1) cap = 1;
	for (int i = g_mk_n - 1; i >= 0; i--) {          /* newest first, so the cap drops the oldest */
		char p[DL_PATH];
		if (dl_join(p, sizeof p, G.pending_dir, g_mk[i], "") != 0) continue;
		if (G.active && !strncmp(g_mk[i], G.cur.run, strlen(G.cur.run)) && g_mk[i][strlen(G.cur.run)] == '.') continue;
		dl_run *r = &g_scratch;
		if (dl_marker_load(p, r) != 0) { dlp_remove(p); continue; }
		long age = dl_run_age_days(r->run);
		if (age > DL_MAX_AGE_DAYS || r->attempt >= DL_MAX_ATTEMPTS || alive >= cap) {
			dl_log("DEVLOG dropped pending run %s (%s)", r->run,
			       age > DL_MAX_AGE_DAYS ? "too old" : r->attempt >= DL_MAX_ATTEMPTS ? "too many failed attempts" : "too many pending runs");
			dl_remove_run_files(r->run);
			continue;
		}
		alive++;
		if (strcmp(r->state, "copied") != 0) {
			if (dlp_stopping()) break;
			if (!r->outcome[0] || !strcmp(r->outcome, "pending") || !strcmp(r->outcome, "unknown")) {
				dl_cpy(r->outcome, sizeof r->outcome, "crash");
				if (!r->detail[0]) dl_cpy(r->detail, sizeof r->detail, "no devlog_end: crash, closed from the LiveArea, or power loss");
			}
			if (!dl_adopt(r, 0)) break;   /* stopped mid-copy: the run stays as it was, a later job copies it */
			dl_log("DEVLOG kept run %s of %s (%s): %d file(s) copied for upload", r->run, r->app, r->outcome, r->nsrc);
		}
	}
}

/* send every copied run, oldest first, while the receiver is reachable and time is left */
static void dl_send_pending(dl_sess *ss, devlog_result *res)
{
	dl_list_markers();
	for (int i = 0; i < g_mk_n; i++) {
		char p[DL_PATH];
		if (dl_join(p, sizeof p, G.pending_dir, g_mk[i], "") != 0) continue;
		dl_run *r = &g_scratch;
		if (dl_marker_load(p, r) != 0 || strcmp(r->state, "copied") != 0) continue;
		if (G.active && !strcmp(r->run, G.cur.run)) continue;
		if (!dl_reach(ss) || ss->auth_failed || dl_left(ss->deadline) == 0) { ss->gave_up = 1; if (ss->reached && !ss->fail) ss->fail = 1; break; }
		r->late = 1;
		uint64_t b = 0;
		int s = dl_send_run(ss, r, res, &b);
		if (s == 1 || s == -1) {
			if (s == 1) { if (res) res->runs_sent++; dl_log("DEVLOG sent kept run %s of %s (%llu bytes)", r->run, r->app, (unsigned long long)b); }
			else if (res) res->runs_rejected++;
			dl_remove_run_files(r->run);
			continue;
		}
		if (s == 0) { r->attempt++; dl_marker_save(r); ss->fail = 2; }
		else if (s == 2) ss->fail = 1;   /* out of time: not counted as a failed attempt */
		ss->gave_up = 1;
		break;
	}
}

static int dl_count_pending(void)
{
	dl_list_markers();
	int n = 0;
	for (int i = 0; i < g_mk_n; i++)
		if (!(G.active && !strncmp(g_mk[i], G.cur.run, strlen(G.cur.run)) && g_mk[i][strlen(G.cur.run)] == '.')) n++;
	return n;
}

/* ------------------------------------------------------------------ API */
static void dl_res_init(devlog_result *r)
{
	if (!r) return;
	memset(r, 0, sizeof *r);
	r->net = -100;
}
static void dl_sess_init(dl_sess *ss, uint64_t t0, unsigned budget_ms)
{
	memset(ss, 0, sizeof *ss);
	ss->t0 = t0;
	ss->reach_deadline = t0 + G.cfg.reach_ms - DL_REACH_RESERVE_MS;   /* the reserve: copies, net term, the marker */
	ss->deadline = t0 + budget_ms;
	dlp_err_reset();
}
static void dl_sess_close(dl_sess *ss, devlog_result *res)
{
	if (ss->tried) dlp_net_down();
	if (!res) return;
	char et[72], ex[96] = "";
	dlp_err_text(et, sizeof et);
	if (et[0]) snprintf(ex, sizeof ex, " (last network error: %s)", et);
	res->enabled = 1;
	res->mode = G.cfg.mode;
	res->net = ss->tried ? ss->net : -100;
	res->reached = ss->reached;
	dl_cpy(res->server, sizeof res->server, ss->reached ? ss->server : "");
	res->runs_pending = dl_count_pending();
	res->ms = (unsigned)(dlp_now_ms() - ss->t0);
	if (res->why[0]) return;
	if (dlp_stopping() && !ss->reached)
		dl_result_why(res, "stopped (devlog_cancel or a newer devlog call) before anything was sent; %d run(s) kept", res->runs_pending);
	else if (!ss->tried && res->runs_pending) dl_result_why(res, "nothing sent; %d run(s) kept", res->runs_pending);
	else if (!ss->tried) dl_result_why(res, "nothing to send");
	else if (ss->net == 0) dl_result_why(res, "no Wi-Fi connection%s; %d run(s) kept for later", ex, res->runs_pending);
	else if (ss->net < 0) dl_result_why(res, "network library error %d%s; %d run(s) kept", ss->net, ex, res->runs_pending);
	else if (ss->auth_failed) dl_result_why(res, "receiver refused the token (401): check devlog.txt; %d run(s) kept", res->runs_pending);
	else if (!ss->reached) dl_result_why(res, "no receiver within %u ms%s; %d run(s) kept for later", G.cfg.reach_ms, ex, res->runs_pending);
	else dl_result_why(res, "%d run(s) sent, %d file(s), %llu bytes to %s%s; %d run(s) kept%s%s%s", res->runs_sent, res->files_sent,
	                   res->bytes_sent, ss->server, ss->base[0] ? " (codex)" : "", res->runs_pending,
	                   ss->fail == 1 ? (dlp_stopping() ? "; stopped (devlog_cancel)" : "; out of time") : ss->fail == 2 ? "; transfer failed" : "",
	                   ss->fail == 2 ? ex : "", res->runs_rejected ? "; rejected run(s) dropped" : "");
}

static void dl_fill_device(dl_run *r)
{
	dlp_device d;
	dlp_device_info(&d);
	dl_cpy(r->platform, sizeof r->platform, d.platform);
	dl_cpy(r->model, sizeof r->model, d.model);
	dl_cpy(r->fw, sizeof r->fw, d.fw);
	r->free_kb = d.free_kb; r->free_cdram_kb = d.free_cdram_kb; r->free_phycont_kb = d.free_phycont_kb;
}

static void dl_log_call(const char *what, const devlog_result *res)
{
	char note[120];
	dlp_net_note(note, sizeof note);
	dl_log("DEVLOG %s: %s (%u ms%s%s)", what, res->why, res->ms, note[0] ? "; " : "", note);
}

const char *devlog_outcome_name(devlog_outcome o)
{
	switch (o) {
	case DEVLOG_OK: return "ok";
	case DEVLOG_FAIL: return "fail";
	case DEVLOG_CRASH: return "crash";
	case DEVLOG_ABORT: return "abort";
	case DEVLOG_HANG: return "hang";
	default: return "unknown";
	}
}

const char *devlog_run_id(void) { return (G.active || G.ended) ? G.cur.run : ""; }

static void dl_copy_budget(void) { g_copy_left = 2ull * G.cfg.keep_kb * 1024u; }

/* ------------------------------------------------------------------ the optional log-file writer */
#define DL_LF_CAP (16u * 1024u)
#define DL_LF_LINES 64u                 /* buffered: write after this many lines ... */
#define DL_LF_MS 2000u                  /* ... or once the oldest waiting line is this old (checked per line) */
static struct { int fd, buffered; unsigned n, lines; uint64_t first_ms; char path[DL_PATH]; } g_lf = { -1, 0, 0, 0, 0, "" };
static char g_lf_buf[DL_LF_CAP];

void devlog_logfile_flush(void)
{
	if (g_lf.fd < 0 || !g_lf.n) return;
	dlp_write(g_lf.fd, g_lf_buf, g_lf.n);
	g_lf.n = 0; g_lf.lines = 0;
}

int devlog_logfile_open(const char *path, int append, int buffered)
{
	devlog_logfile_close();
	if (!path || strlen(path) >= sizeof g_lf.path) return -1;
	int fd = dlp_open_write(path, append);
	if (fd < 0) return -1;
	g_lf.fd = fd; g_lf.buffered = buffered ? 1 : 0; g_lf.n = 0; g_lf.lines = 0;
	dl_cpy(g_lf.path, sizeof g_lf.path, path);
	return 0;
}

void devlog_logfile_write(const char *line)
{
	if (g_lf.fd < 0 || !line) return;
	size_t len = strlen(line);
	if (!g_lf.buffered) {   /* write-through: the line is on the card when this returns (complete crash logs) */
		char b[512];
		if (len + 1 <= sizeof b) { memcpy(b, line, len); b[len] = '\n'; dlp_write(g_lf.fd, b, len + 1); }
		else { dlp_write(g_lf.fd, line, len); dlp_write(g_lf.fd, "\n", 1); }
		return;
	}
	if (g_lf.n + len + 1 > DL_LF_CAP) devlog_logfile_flush();
	if (len + 1 > DL_LF_CAP) { dlp_write(g_lf.fd, line, len); dlp_write(g_lf.fd, "\n", 1); return; }
	uint64_t now = dlp_now_ms();
	if (!g_lf.lines) g_lf.first_ms = now;
	memcpy(g_lf_buf + g_lf.n, line, len);
	g_lf_buf[g_lf.n + len] = '\n';
	g_lf.n += (unsigned)len + 1;
	g_lf.lines++;
	if (g_lf.lines >= DL_LF_LINES || now - g_lf.first_ms >= DL_LF_MS) devlog_logfile_flush();
}

void devlog_logfile_close(void)
{
	if (g_lf.fd < 0) return;
	devlog_logfile_flush();
	dlp_close(g_lf.fd);
	g_lf.fd = -1;
	g_lf.path[0] = 0;
}

/* devlog_clear: start the log over (its waiting lines go too) */
static int dl_logfile_truncate(const char *path)
{
	if (g_lf.fd < 0 || strcmp(g_lf.path, path) != 0) return 0;
	g_lf.n = 0; g_lf.lines = 0;
	dlp_close(g_lf.fd);
	g_lf.fd = dlp_open_write(path, 0);
	if (g_lf.fd < 0) g_lf.path[0] = 0;
	return 1;
}

/* ------------------------------------------------------------------ jobs: the background worker */
enum { DLJ_LAUNCH = 1, DLJ_END, DLJ_FLUSH };
static struct { int kind, send, rc; uint64_t t0; devlog_result res; } g_job;
static int g_job_state;                   /* 0 idle, 1 running, 2 finished (result not collected yet) */
static int g_job_threaded;
static int g_last_rc = DEVLOG_DISABLED;   /* the last collected job (devlog_wait when nothing runs) */
static devlog_result g_last_res;

/* LAUNCH (devlog_begin): copy what older launches left (crashes, unanswered asks, failed sends); send if allowed */
static int dl_job_launch(devlog_result *res)
{
	dl_copy_budget();
	dl_adopt_all();
	if (!g_job.send) {
		int n = dl_count_pending();
		res->enabled = 1; res->mode = G.cfg.mode; res->runs_pending = n;
		res->ms = (unsigned)(dlp_now_ms() - g_job.t0);
		dl_result_why(res, "%d run(s) kept, not sent: %s", n, G.cfg.mode == DEVLOG_MODE_ASK
		              ? "waiting for the user's answer (mode=ask)" : "the app sends them later (defer_launch_send)");
		return !n ? DEVLOG_SENT : G.cfg.mode == DEVLOG_MODE_ASK ? DEVLOG_ASK : DEVLOG_PENDING;
	}
	dl_sess ss;
	dl_sess_init(&ss, g_job.t0, G.cfg.begin_ms);   /* t0 = the job's start: the copies above count against the 2 s */
	dl_send_pending(&ss, res);
	dl_sess_close(&ss, res);
	if (ss.tried || dlp_stopping()) dl_log_call("launch send", res);
	return (ss.gave_up || res->runs_pending) ? DEVLOG_PENDING : DEVLOG_SENT;
}

/* END (devlog_end in mode=auto, devlog_send_now(1) after an ask): send the ended run straight from its files, then
 * the kept ones; if it cannot go, keep copies of its files for a later launch */
static int dl_job_end(devlog_result *res)
{
	dl_copy_budget();
	dl_run *r = &G.cur;
	/* what this run sends (PROTOCOL.md §4): its log, the previous log (*.prev, see below), the extras */
	dl_run *up = &g_scratch;
	*up = *r;
	up->nsrc = 0;
	for (int i = 0; i < r->nsrc && up->nsrc < DL_MAX_FILES; i++) {
		const dl_src *s = &r->src[i];
		if (dlp_file_size(s->path) >= 0) {
			dl_src *d = &up->src[up->nsrc++];
			memset(d, 0, sizeof *d);
			d->kind = 'x';
			dl_cpy(d->path, sizeof d->path, s->path);
		}
		/* the previous log goes too only when it carries no tag at all (it was written while devlog was off). A tagged
		 * one is the log of a devlog run, and every such run was dealt with on its own: sent, declined (the user said
		 * no), dropped (retention, attempts, rejected), cleared, or still kept (then it goes under that run, if the
		 * user allows). No list of sent ids can stand in for that (devlog_clear empties it, it holds a few ids), so a
		 * declined run's log can never leave as somebody's *.prev. A scan cut short counts as tagged. */
		if (s->kind == 'f' && s->prev[0] && dlp_file_size(s->prev) >= 0 && up->nsrc < DL_MAX_FILES) {
			if (dl_file_any_tag(s->prev)) continue;
			dl_src *d = &up->src[up->nsrc++];
			memset(d, 0, sizeof *d);
			d->kind = 'x';
			dl_cpy(d->path, sizeof d->path, s->prev);
		}
	}
	dl_sess ss;
	dl_sess_init(&ss, g_job.t0, G.cfg.max_total_ms);
	int st = 0;
	uint64_t b = 0;
	if (dl_reach(&ss) && !ss.auth_failed) st = dl_send_run(&ss, up, res, &b);
	if (st == 1 || st == -1) {
		if (st == 1) {
			res->runs_sent++;
			dl_log("DEVLOG sent run %s: %d file(s), %llu bytes to %s%s", r->run, res->files_sent, (unsigned long long)b,
			       ss.server, ss.base[0] ? " (codex)" : "");
		} else {
			res->runs_rejected++;
		}
		dl_remove_run_files(r->run);
		G.ended = 0;
		if (!ss.auth_failed) {
			/* older runs whose launch copy was cut short (this run ended while it ran) are copied now, then sent */
			dl_copy_budget();
			dl_adopt_all();
			dl_send_pending(&ss, res);
		}
	} else {
		/* keep it: copy the files now (the next launch renames the log); the marker becomes 'copied'. Out of time
		 * (st 2) is not a failed attempt; a transfer error with a reachable receiver (st 0) is. Stopped during the
		 * copy: the marker stays 'ended' and the next launch copies from the tagged log instead. */
		up->attempt = r->attempt + (st == 0 && ss.reached ? 1 : 0);
		if (ss.reached) ss.fail = st == 2 ? 1 : 2;
		if (dl_adopt(up, 1)) { dl_copy_budget(); dl_adopt_all(); }   /* + older runs whose launch copy was cut short */
		G.ended = 0;
		ss.gave_up = 1;
	}
	if (st == -1) {
		dl_sess_close(&ss, NULL);
		res->enabled = 1; res->mode = G.cfg.mode; res->net = ss.net; res->reached = 1;
		dl_cpy(res->server, sizeof res->server, ss.server);
		res->runs_pending = dl_count_pending();
		res->ms = (unsigned)(dlp_now_ms() - ss.t0);
		dl_result_why(res, "run rejected by the receiver (meta HTTP %d even reduced): dropped; %d other run(s) sent, %d kept",
		              ss.reject_status, res->runs_sent, res->runs_pending);
	} else {
		dl_sess_close(&ss, res);
	}
	char what[24];
	snprintf(what, sizeof what, "end (%s)", r->outcome);
	dl_log_call(what, res);
	if (st == -1) return DEVLOG_REJECTED;
	return (st == 1 && !res->runs_pending && !ss.gave_up) ? DEVLOG_SENT : DEVLOG_PENDING;
}

/* FLUSH (devlog_flush / devlog_flush_app / devlog_send_now(1) without an ended run): adopt, send every kept run */
static int dl_job_flush(devlog_result *res)
{
	dl_copy_budget();
	dl_adopt_all();
	dl_sess ss;
	dl_sess_init(&ss, g_job.t0, G.cfg.max_total_ms);
	dl_send_pending(&ss, res);
	dl_sess_close(&ss, res);
	if (ss.tried) dl_log_call("flush", res);
	return (ss.gave_up || res->runs_pending) ? DEVLOG_PENDING : DEVLOG_SENT;
}

static int dl_worker(void *u)
{
	(void)u;
	devlog_result *res = &g_job.res;
	dl_res_init(res);
	res->mode = G.cfg.mode;
	g_job.rc = g_job.kind == DLJ_LAUNCH ? dl_job_launch(res) : g_job.kind == DLJ_END ? dl_job_end(res) : dl_job_flush(res);
	__atomic_store_n(&g_job_state, 2, __ATOMIC_RELEASE);
	return 0;
}

/* starts a job on the worker (low priority, spare core); if no thread can be made it runs here and now */
static int dl_job_start(int kind, int send, uint64_t t0)
{
	g_job.kind = kind; g_job.send = send; g_job.t0 = t0; g_job.rc = DEVLOG_STARTED;
	dlp_stop_set(0);
	__atomic_store_n(&g_job_state, 1, __ATOMIC_RELEASE);
	int prio = G.app.thread_priority ? G.app.thread_priority : DL_THREAD_PRIORITY;
	unsigned aff = G.app.thread_affinity ? G.app.thread_affinity : DL_THREAD_AFFINITY;
	g_job_threaded = dlp_thread_start(dl_worker, NULL, prio, aff, DL_THREAD_STACK) == 0;
	if (!g_job_threaded) {
		char et[72];
		dlp_err_text(et, sizeof et);
		dl_log("DEVLOG no background thread (%s): this send runs on the app's thread", et[0] ? et : "refused");
		dl_worker(NULL);
	}
	return DEVLOG_STARTED;
}

static int dl_job_collect(void)
{
	if (__atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE) != 2) return 0;
	if (g_job_threaded) dlp_thread_join();
	g_job_threaded = 0;
	dlp_stop_set(0);
	g_last_rc = g_job.rc;
	g_last_res = g_job.res;
	__atomic_store_n(&g_job_state, 0, __ATOMIC_RELEASE);
	dl_q_drain();
	return 1;
}

/* waits up to timeout_ms (~0u: until it ends) for the running job, handing its log lines to the app meanwhile.
 * 1 = no job is running any more (collected), 0 = still running */
static int dl_job_wait(unsigned timeout_ms)
{
	uint64_t end = dlp_now_ms() + timeout_ms;
	for (;;) {
		dl_q_drain();
		int st = __atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE);
		if (st == 0) return 1;
		if (st == 2) { dl_job_collect(); return 1; }
		if (timeout_ms != ~0u && dlp_now_ms() >= end) return 0;
		dlp_sleep_ms(2);
	}
}

/* before an API call that uses the shared buffers: let the running job finish (cancel: ask it to stop first; it
 * then ends within a few ms, keeping its unsent runs, except that a keep copy in progress completes) */
static void dl_quiesce(int cancel)
{
	if (__atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE) == 0) { dl_q_drain(); return; }
	if (cancel && __atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE) == 1) dlp_stop_set(1);
	dl_job_wait(~0u);
}

int devlog_busy(void) { return __atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE) == 1; }

void devlog_cancel(void) { if (devlog_busy()) dlp_stop_set(1); }

int devlog_wait(unsigned timeout_ms, devlog_result *res)
{
	if (__atomic_load_n(&g_job_state, __ATOMIC_ACQUIRE) != 0 && !dl_job_wait(timeout_ms)) {
		if (res) {
			dl_res_init(res);
			res->enabled = 1; res->mode = G.cfg.mode;
			dl_result_why(res, "still sending in the background");
		}
		return DEVLOG_STARTED;
	}
	dl_q_drain();
	if (!g_last_res.why[0]) { dl_res_init(&g_last_res); dl_result_why(&g_last_res, "no background send has run"); }
	if (res) *res = g_last_res;
	return g_last_rc;
}

/* ------------------------------------------------------------------ setup, clearing */
/* the app, root and derived paths (no config): 1 ok */
static int dl_paths(const devlog_app *a, devlog_result *res)
{
	memset(&G, 0, sizeof G);
	if (!a || !a->app || !dl_valid_app(a->app) || !a->log_path) { dl_result_why(res, "bad devlog_app"); return 0; }
	const char *root = a->root ? a->root : DL_DEFAULT_ROOT;
	if (strlen(root) >= sizeof G.root) { dl_result_why(res, "root path too long"); return 0; }
	G.app = *a;
	dl_cpy(G.root, sizeof G.root, root);
	dl_join(G.cfg_path, sizeof G.cfg_path, G.root, "devlog.txt", "");
	dl_join(G.state_dir, sizeof G.state_dir, G.root, "devlog", "");
	dl_join(G.pending_dir, sizeof G.pending_dir, G.root, "devlog/pending", "");
	return 1;
}

/* Removes every kept run (markers, notes, copies) but the active one's marker, the cached address (and a sent.txt
 * left by the first revision-3 build), the app's previous log and, with current, its current log (emptied in place,
 * so an app that appends keeps working; an active run's tag is then written again). Local only, allowed whatever the
 * config says. */
static void dl_clear_files(int current, int *runs, int *logs)
{
	char p[DL_PATH], act_run[72] = "", act_note[72] = "";
	*runs = 0; *logs = 0;
	if (G.active) { snprintf(act_run, sizeof act_run, "%.64s.run", G.cur.run); snprintf(act_note, sizeof act_note, "%.64s.note", G.cur.run); }
	for (int pass = 0; pass < 8; pass++) {
		g_mk_n = 0;
		if (dlp_list_dir(G.pending_dir, dl_any_cb, NULL) != 0 || !g_mk_n) break;
		int full = g_mk_n == DL_MAX_MARKERS, did = 0;
		for (int i = 0; i < g_mk_n; i++) {
			const char *n = g_mk[i];
			if (!strcmp(n, act_run) || !strcmp(n, act_note)) continue;
			if (g_mk_isdir[i]) { if (dl_valid_run(n)) { dl_remove_run_files(n); did++; } continue; }
			if (dl_ends(n, ".run")) (*runs)++;
			if (dl_join(p, sizeof p, G.pending_dir, n, "") == 0 && dlp_remove(p) == 0) did++;
		}
		if (!full || !did) break;
	}
	if (dl_join(p, sizeof p, G.state_dir, "sent.txt", "") == 0 && dlp_file_size(p) >= 0) dlp_remove(p);
	if (dl_join(p, sizeof p, G.state_dir, "server.txt", "") == 0 && dlp_file_size(p) >= 0) dlp_remove(p);
	const char *prev = G.app.prev_path, *cur = G.app.log_path;
	if (prev && dl_path_allowed(prev) && dlp_file_size(prev) >= 0 && dlp_remove(prev) == 0) (*logs)++;
	if (current && cur && dl_path_allowed(cur) && dlp_file_size(cur) >= 0) {
		if (!dl_logfile_truncate(cur)) { int fd = dlp_open_write(cur, 0); if (fd >= 0) dlp_close(fd); }
		(*logs)++;
		if (G.active) {   /* the active run's log starts over: its tag goes in again, at offset 0 */
			char tag[160];
			G.cur.tag_off = 0;
			snprintf(tag, sizeof tag, DL_TAG "%s app=%s (devlog upload id; vita/devlog; log cleared)", G.cur.run, G.cur.app);
			if (G.app.log) G.app.log(tag);
			else { int fd = dlp_open_write(cur, 1); if (fd >= 0) { dlp_write(fd, tag, strlen(tag)); dlp_write(fd, "\n", 1); dlp_close(fd); } }
			devlog_logfile_flush();
			dl_marker_save(&G.cur);
		}
	}
	if (!G.active) { dlp_rmdir(G.pending_dir); dlp_rmdir(G.state_dir); }
}

/* root, paths, config (+ the one-shot clear=1); 1 when enabled. Creates nothing when not enabled. */
static int dl_setup(const devlog_app *a, devlog_result *res)
{
	if (!dl_paths(a, res)) return 0;
	dl_read_config(&G.cfg);
	if (res) res->mode = G.cfg.mode;
	if (G.cfg.clear) {
		/* clear=1: wipe the kept runs and this app's previous log (the log of the launch before; the current one has
		 * only this launch's first lines), then turn the line into clear=0 in place (one byte, nothing else moves) */
		int runs, logs;
		dl_clear_files(0, &runs, &logs);
		int fd = dlp_open_update(G.cfg_path), ok = 0;
		if (fd >= 0) { ok = dlp_seek(fd, (int64_t)G.cfg.clear_off) == 0 && dlp_write(fd, "0", 1) == 1; dlp_close(fd); }
		if (G.cfg.enabled)
			dl_log("DEVLOG clear=1: removed %d kept run(s), %d log(s) and the cached address%s", runs, logs,
			       ok ? "; devlog.txt now says clear=0" : "; COULD NOT reset it to clear=0 (it will clear again)");
	}
	if (!G.cfg.enabled) {
		dl_result_why(res, dlp_file_size(G.cfg_path) < 0 ? "no devlog.txt: off" : G.cfg.mode == DEVLOG_MODE_OFF && G.cfg.token[0]
		              ? "devlog.txt: mode=off" : "devlog.txt: not enabled or no valid token: off");
		G.app.log = NULL;
		return 0;
	}
	dlp_mkdir(G.state_dir);
	dlp_mkdir(G.pending_dir);
	return 1;
}

int devlog_begin(const devlog_app *a, devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	dl_quiesce(1);
	dl_res_init(res);
	if (!dl_setup(a, res)) return DEVLOG_DISABLED;
	G.t_begin = t0;

	dl_run *r = &G.cur;
	memset(r, 0, sizeof *r);
	dl_cpy(r->app, sizeof r->app, a->app);
	dl_new_run_id(r->run);
	dl_clean(r->title_id, sizeof r->title_id, a->title_id ? a->title_id : "");
	dl_clean(r->build, sizeof r->build, a->build ? a->build : "");
	dl_clean(r->commit, sizeof r->commit, a->commit ? a->commit : "");
	dl_iso_now(r->started);
	dl_cpy(r->state, sizeof r->state, "running");
	dl_cpy(r->outcome, sizeof r->outcome, "pending");
	dl_fill_device(r);
	r->tag_off = -1;
	devlog_logfile_flush();   /* the size below must count what the app logged so far */
	if (dl_path_allowed(a->log_path)) {
		dl_src *s = &r->src[r->nsrc++];
		s->kind = 'f';
		dl_cpy(s->path, sizeof s->path, a->log_path);
		dl_cpy(s->prev, sizeof s->prev, a->prev_path && dl_path_allowed(a->prev_path) ? a->prev_path : "");
		int64_t sz = dlp_file_size(a->log_path);
		r->tag_off = sz < 0 ? 0 : sz;   /* the tag goes at (about) this offset: a later launch looks there */
	} else {
		dl_log("DEVLOG refused log %s (not a log path)", a->log_path);
	}
	for (int i = 0; a->extra && a->extra[i]; i++) {
		if (!dl_path_allowed(a->extra[i])) { dl_log("DEVLOG refused extra file %s (not a log path)", a->extra[i]); continue; }
		if (r->nsrc >= DL_MAX_FILES - 1) break;   /* one slot stays for the .prev */
		dl_src *s = &r->src[r->nsrc++];
		s->kind = 'x';
		dl_cpy(s->path, sizeof s->path, a->extra[i]);
	}
	G.active = 1;
	dl_marker_save(r);

	/* the tag: lets a later launch recognise this run's log, also after it was renamed to *.prev.txt */
	char tag[160];
	snprintf(tag, sizeof tag, DL_TAG "%s app=%s (devlog upload id; vita/devlog)", r->run, r->app);
	if (G.app.log) G.app.log(tag);
	else if (dl_path_allowed(a->log_path)) {
		int fd = dlp_open_write(a->log_path, 1);
		if (fd >= 0) { dlp_write(fd, tag, strlen(tag)); dlp_write(fd, "\n", 1); dlp_close(fd); }
	}
	devlog_logfile_flush();   /* buffered logs: the tag must be on the card before anything can crash */

	/* older runs: copied (and, in mode=auto, sent) by the worker; this thread goes on at once */
	int kept = dl_count_pending();
	int send = G.cfg.mode == DEVLOG_MODE_AUTO && !a->defer_launch_send;
	if (kept) dl_job_start(DLJ_LAUNCH, send, t0);
	if (res) {
		res->enabled = 1; res->mode = G.cfg.mode; res->runs_pending = kept;
		res->ms = (unsigned)(dlp_now_ms() - t0);
		if (!kept) dl_result_why(res, "nothing kept from earlier launches");
		else if (send) dl_result_why(res, "%d kept run(s): sending in the background (devlog_wait for the result)", kept);
		else if (G.cfg.mode == DEVLOG_MODE_ASK) dl_result_why(res, "%d kept run(s): ask the user (devlog_should_ask) before sending", kept);
		else dl_result_why(res, "%d kept run(s): devlog_send_now(1) sends them (defer_launch_send)", kept);
	}
	if (!kept) return DEVLOG_SENT;
	return send ? DEVLOG_STARTED : G.cfg.mode == DEVLOG_MODE_ASK ? DEVLOG_ASK : DEVLOG_PENDING;
}

void devlog_note(devlog_outcome o, const char *detail)
{
	/* any thread, any time (a run about to die): only this run's <run>.note, own stack buffers, no network */
	if (!G.active) return;
	devlog_logfile_flush();
	char d[201], t[320], path[DL_PATH];
	dl_clean(d, sizeof d, detail ? detail : G.cur.detail);
	unsigned long long up = dlp_now_ms() - G.t_begin;
	dl_cpy(G.cur.outcome, sizeof G.cur.outcome, devlog_outcome_name(o));
	dl_cpy(G.cur.detail, sizeof G.cur.detail, d);
	G.cur.uptime_ms = up;
	if (dl_note_path(G.cur.run, path, sizeof path) != 0) return;
	int n = snprintf(t, sizeof t, "outcome=%s\ndetail=%s\nuptime_ms=%llu\n", devlog_outcome_name(o), d, up);
	if (n > 0) dl_write_file(path, t, (size_t)n < sizeof t ? (size_t)n : sizeof t - 1);
}

int devlog_end_async(devlog_outcome o, const char *detail, devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	devlog_result local;
	if (!res) res = &local;
	dl_res_init(res);
	if (!G.active) { dl_quiesce(0); dl_result_why(res, G.ended ? "devlog_end ran already" : "off"); return DEVLOG_DISABLED; }
	devlog_logfile_flush();
	dl_quiesce(1);   /* a launch send still running: stopped; its runs stay kept and go after this one */
	dl_run *r = &G.cur;
	dl_cpy(r->outcome, sizeof r->outcome, devlog_outcome_name(o));
	if (detail) dl_clean(r->detail, sizeof r->detail, detail);
	r->uptime_ms = dlp_now_ms() - G.t_begin;
	dl_cpy(r->state, sizeof r->state, "ended");
	dl_fill_device(r);
	dl_marker_save(r);
	G.active = 0;
	G.ended = 1;
	res->enabled = 1;
	res->mode = G.cfg.mode;
	if (G.cfg.mode != DEVLOG_MODE_AUTO) {
		res->runs_pending = dl_count_pending();
		res->ms = (unsigned)(dlp_now_ms() - t0);
		dl_result_why(res, "run kept, %d run(s) in all: ask the user (devlog_should_ask / devlog_ask_console)", res->runs_pending);
		dl_log("DEVLOG end (%s): %s (%u ms)", r->outcome, res->why, res->ms);
		return DEVLOG_ASK;
	}
	return dl_job_start(DLJ_END, 1, t0);
}

int devlog_end(devlog_outcome o, const char *detail, devlog_result *res)
{
	int rc = devlog_end_async(o, detail, res);
	if (rc == DEVLOG_STARTED) rc = devlog_wait(~0u, res);
	return rc;
}

/* sync flush: the job on the worker, this thread waits */
static int dl_flush_sync(uint64_t t0, devlog_result *res)
{
	if (G.cfg.mode != DEVLOG_MODE_AUTO) {   /* mode=ask: only the user's yes sends (devlog_send_now) */
		dl_job_start(DLJ_LAUNCH, 0, t0);
		return devlog_wait(~0u, res);
	}
	dl_job_start(DLJ_FLUSH, 1, t0);
	return devlog_wait(~0u, res);
}

int devlog_flush(devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	dl_res_init(res);
	if (!G.cfg.enabled) { dl_result_why(res, "off (devlog_flush needs devlog_begin first; see devlog_flush_app)"); return DEVLOG_DISABLED; }
	dl_quiesce(0);
	return dl_flush_sync(t0, res);
}

int devlog_flush_app(const devlog_app *a, devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	dl_res_init(res);
	if (G.active) return devlog_flush(res);   /* a run is open in this process: same as devlog_flush */
	dl_quiesce(1);
	if (!dl_setup(a, res)) return DEVLOG_DISABLED;
	return dl_flush_sync(t0, res);
}

int devlog_should_ask(void)
{
	if (!G.cfg.enabled || G.cfg.mode != DEVLOG_MODE_ASK) return 0;
	if (devlog_busy()) return 0;   /* the launch copies are still being made: ask a little later */
	dl_quiesce(0);
	return dl_count_pending();
}

/* the user said no: every kept run (and the ended one) is deleted. Their logs stay on the card (the app's own files)
 * but never leave it: they carry their run's tag, and a tagged *.prev is never sent (dl_job_end). */
static int dl_drop_kept(void)
{
	int n = 0;
	for (int pass = 0; pass < 8; pass++) {
		dl_list_markers();
		if (!g_mk_n) break;
		int full = g_mk_n == DL_MAX_MARKERS;
		for (int i = 0; i < g_mk_n; i++) {
			char run[72];
			dl_cpy(run, sizeof run, g_mk[i]);
			run[strlen(run) - 4] = 0;   /* without ".run" */
			if (G.active && !strcmp(run, G.cur.run)) continue;
			dl_remove_run_files(run);
			n++;
		}
		if (!full) break;
	}
	return n;
}

int devlog_send_now(int yes, devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	devlog_result local;
	if (!res) res = &local;
	dl_res_init(res);
	if (!G.cfg.enabled) { dl_result_why(res, "off"); return DEVLOG_DISABLED; }
	dl_quiesce(0);
	res->enabled = 1;
	res->mode = G.cfg.mode;
	if (!yes) {
		int n = dl_drop_kept();
		G.ended = 0;
		res->ms = (unsigned)(dlp_now_ms() - t0);
		dl_result_why(res, "%d run(s) deleted at the user's request; nothing sent", n);
		dl_log("DEVLOG %s", res->why);
		return DEVLOG_DROPPED;
	}
	if (!dl_count_pending()) { dl_result_why(res, "nothing to send"); return DEVLOG_SENT; }
	return dl_job_start(G.ended ? DLJ_END : DLJ_FLUSH, 1, t0);
}

int devlog_ask_console(int (*answer)(unsigned timeout_ms), unsigned timeout_ms, devlog_result *res)
{
	devlog_result local;
	if (!res) res = &local;
	dl_res_init(res);
	if (!G.cfg.enabled) { dl_result_why(res, "off"); return DEVLOG_DISABLED; }
	res->enabled = 1;
	res->mode = G.cfg.mode;
	if (G.cfg.mode != DEVLOG_MODE_ASK) { dl_result_why(res, "mode=auto: no question"); return DEVLOG_SENT; }
	dl_quiesce(0);   /* the launch copies, if they are still being made */
	int n = devlog_should_ask();
	if (n <= 0) { dl_result_why(res, "nothing to ask about"); return DEVLOG_SENT; }
	if (!timeout_ms) timeout_ms = 30000;
	dl_log("DEVLOG ask: send the logs of %d run(s)%s to the Mac?", n, G.ended ? " (this one included)" : " kept from earlier launches");
	dl_log("DEVLOG ask: X = send now   CIRCLE = delete them   START or %u s = ask again next time", timeout_ms / 1000u);
	int a = answer ? answer(timeout_ms) : -1;
	if (a < 0) {
		dl_result_why(res, "not answered: %d run(s) stay kept and are asked about again", n);
		res->runs_pending = n;
		dl_log("DEVLOG ask: %s", res->why);
		return DEVLOG_ASK;
	}
	int rc = devlog_send_now(a > 0, res);
	if (rc == DEVLOG_STARTED) {
		dl_log("DEVLOG ask: sending in the background ...");
		rc = devlog_wait(~0u, res);
	}
	dl_log("DEVLOG ask: %s", res->why);
	return rc;
}

int devlog_clear(const devlog_app *a, devlog_result *res)
{
	uint64_t t0 = dlp_now_ms();
	devlog_result local;
	if (!res) res = &local;
	dl_res_init(res);
	dl_quiesce(1);
	if (!G.active && !G.ended) {   /* standalone (before devlog_begin, or devlog is off): paths from a */
		if (!dl_paths(a, res)) return DEVLOG_DISABLED;
		dl_read_config(&G.cfg);  /* only for res->mode; clearing is allowed whatever it says */
		res->mode = G.cfg.mode;
	}
	int runs, logs;
	dl_clear_files(1, &runs, &logs);
	G.ended = 0;
	res->enabled = G.cfg.enabled;
	res->ms = (unsigned)(dlp_now_ms() - t0);
	dl_result_why(res, "cleared: %d kept run(s), %d log(s) of %s and the cached address", runs, logs, G.app.app);
	dl_log("DEVLOG %s", res->why);
	return DEVLOG_DROPPED;
}
