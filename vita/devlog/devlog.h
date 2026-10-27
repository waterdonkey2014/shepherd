/* Shepherd devlog client: keeps a record of each app run and sends its LOG FILES to the owner's Mac -- the Archive
 * Codex server (preferred) or the standalone receiver tools/devlog-receiver/ -- over the same Wi-Fi.
 * Wire protocol: vita/devlog/PROTOCOL.md (shpdlog/1, revision 3).
 *
 *   - Off unless ux0:data/shepherd/devlog.txt exists with enabled=1 and a token (PROTOCOL.md §7): without it every
 *     call returns at once and touches nothing (no network, no files).
 *   - mode= in devlog.txt: auto (send at run end / next launch), ask (the default when the file has no mode= line:
 *     nothing is sent until the user says yes to a prompt after a run), off (like no file).
 *   - Zero cost in the app's thread: every transfer and keep copy runs on ONE low-priority background worker
 *     (Vita: priority 191 on core 2 = SCE_KERNEL_CPU_MASK_USER_2 unless the app picks another spare core). The app's
 *     thread only reads the config and writes a marker + one log line (devlog_begin: well under 1 ms on the Mac).
 *     Nothing touches the network during play: sends start at devlog_begin (runs kept from EARLIER launches) and at
 *     devlog_end / devlog_send_now (run end). devlog_cancel stops a send at once (its runs stay kept).
 *   - Logs only: a file is read only if it is a top-level *.txt / *.log of the root folder, or is under
 *     runtime/logs/, and its name does not look like a config file (devlog*, env*, args*, pad*, *-env, *_gl env/args
 *     variants, *-stress, settings, token, ...: PROTOCOL.md §4). Save data, game files and the owner folders
 *     (managed/, pack/, data/, persistent/) are refused by path, whatever the app lists.
 *   - Never sends a console or account identifier: the meta is app, TITLE_ID, build, firmware version, model family,
 *     free memory, outcome, times. App-provided strings are cleaned (printable ASCII; MAC-address-shaped text masked).
 *   - The token never goes on the network: requests carry HMACs, and nothing is sent to an address whose answer does
 *     not prove it knows the token.
 *   - Crashes: devlog_begin writes a marker for the run; a run that never reached devlog_end is found by the next
 *     launch of ANY Shepherd app by the "DEVLOG run=<id>" line devlog_begin put in its log, copied to
 *     ux0:data/shepherd/devlog/pending/ and sent (auto) or asked about (ask) then.
 *   - Retention: the current run + the previous one (keep_pending=2); devlog_clear / clear=1 wipe them.
 *
 * Use (one translation unit is enough, no build change: the sceNet stubs are in gcc's default link libraries):
 *     #include "../devlog/devlog_unity.c"
 *     static const devlog_app DL = { .app = "probe", .title_id = "SHPD00001", .build = BUILD_ID,
 *                                    .log_path = "ux0:data/shepherd/probe.txt",
 *                                    .prev_path = "ux0:data/shepherd/probe.prev.txt", .log = my_log_line };
 *     main: rotate the log, write the first line, then devlog_begin(&DL, NULL);
 *     end:  devlog_end(ok ? DEVLOG_OK : DEVLOG_FAIL, "short reason", NULL);     (auto: sends, waits for the worker)
 *           if (devlog_should_ask()) devlog_ask_console(devlog_pad_answer, 30000, NULL);   (ask: devlog_pad.h)
 *     fatal paths (no network there): devlog_note(DEVLOG_ABORT, "why");
 * Threads: call the API from the app's main thread (devlog_note, devlog_busy, devlog_cancel: any thread). The
 * worker never calls the app's log function; its lines are queued and handed over by the next devlog_* call on the
 * app's thread (devlog_wait(0, NULL) = "poll"). Memory: no malloc; static buffers only (README "Memory"); the worker's
 * stack is 32 KiB; the Vita's sceNet pool (a 256 KiB - 1 MiB memblock) exists only while sending.
 */
#ifndef SHEPHERD_DEVLOG_H
#define SHEPHERD_DEVLOG_H
#ifdef __cplusplus
extern "C" {
#endif

#define DEVLOG_CLIENT "devlog-c/3"

typedef enum devlog_outcome {
	DEVLOG_UNKNOWN = 0,
	DEVLOG_OK,        /* ran to the end, passed */
	DEVLOG_FAIL,      /* ran to the end, reported a failure */
	DEVLOG_CRASH,     /* set by devlog itself for a run that never reached devlog_end */
	DEVLOG_ABORT,     /* the app gave up on purpose (fatal error path) */
	DEVLOG_HANG       /* a watchdog in the app decided it hung */
} devlog_outcome;

typedef enum devlog_mode {
	DEVLOG_MODE_OFF = 0,   /* mode=off (or no usable devlog.txt): nothing is kept or sent */
	DEVLOG_MODE_ASK,       /* mode=ask, and the default: runs are kept; sent only after the user's yes */
	DEVLOG_MODE_AUTO       /* mode=auto: sent at run end, kept runs at the next launch */
} devlog_mode;

typedef struct devlog_app {
	const char *app;             /* ^[a-z][a-z0-9_-]{0,31}$  e.g. "probe" */
	const char *title_id;        /* "SHPD00001" */
	const char *build;           /* the app's build id string */
	const char *commit;          /* optional (NULL) */
	const char *root;            /* NULL = "ux0:data/shepherd" (Mac tests: a folder) */
	const char *log_path;        /* the app's current log (full path, under root) */
	const char *prev_path;       /* where the app moves it at the next launch, or NULL */
	const char *const *extra;    /* optional NULL-terminated list of more log files (full paths, under root) */
	void (*log)(const char *line);   /* where devlog reports (the app's own log function), or NULL */
	/* revision 3 (all optional: 0 = the default) */
	int thread_priority;         /* the worker's sceKernelCreateThread priority; 0 = 191 (the lowest user priority) */
	unsigned thread_affinity;    /* its CPU mask; 0 = SCE_KERNEL_CPU_MASK_USER_2 (core 2). An app that keeps core 2
	                                busy passes the mask of the core it leaves spare */
	int defer_launch_send;       /* 1: devlog_begin only copies kept runs (mode=auto, it returns DEVLOG_PENDING); the
	                                app calls devlog_send_now(1) when it is ready (e.g. after its big allocations: the
	                                sceNet pool is up to 1 MiB) -- only after that PENDING: in mode=ask (begin returns
	                                DEVLOG_ASK) devlog_send_now(1) is the user's yes and must come from the user */
} devlog_app;

typedef struct devlog_result {
	int enabled;                 /* devlog.txt says enabled=1, has a valid token and mode != off */
	int net;                     /* 1 link up, 0 no Wi-Fi, < 0 network library error, -100 not tried */
	int reached;                 /* a receiver answered ping with a valid proof */
	char server[24];             /* "a.b.c.d:port" when reached */
	int runs_sent, runs_pending, files_sent, files_dropped;
	int runs_rejected;           /* runs the receiver refused for good (4xx on meta even reduced): dropped */
	unsigned long long bytes_sent;
	unsigned ms;                 /* time spent in this call / background send, all of it (copies included) */
	char why[160];               /* one line: what happened (with the raw network error when there was one) */
	int mode;                    /* devlog_mode from devlog.txt */
} devlog_result;

/* return values */
#define DEVLOG_DISABLED 0        /* no config / not enabled / mode=off: nothing was done */
#define DEVLOG_SENT     1        /* everything there was to send was sent, or there was nothing to send */
#define DEVLOG_PENDING  2        /* something is left for a later launch (no Wi-Fi, no server, error, time) */
#define DEVLOG_REJECTED 3        /* devlog_end: the receiver refused THIS run for good (see why); it was dropped */
#define DEVLOG_STARTED  4        /* a background send is running: devlog_wait gives its result */
#define DEVLOG_ASK      5        /* mode=ask: runs are kept and wait for the user's answer (devlog_should_ask) */
#define DEVLOG_DROPPED  6        /* devlog_send_now(0) / devlog_clear: kept runs deleted, nothing sent */

/* the run */
int devlog_begin(const devlog_app *app, devlog_result *res);   /* marker + tag; kept runs: copied + (auto) sent in the
                                                                   background -> SENT (none kept) / STARTED / ASK */
int devlog_end(devlog_outcome outcome, const char *detail, devlog_result *res);   /* auto: send this run (+ kept) on
                                                                   the worker and wait: SENT / PENDING / REJECTED;
                                                                   ask: keep it, no network: ASK */
int devlog_end_async(devlog_outcome outcome, const char *detail, devlog_result *res);   /* the same, returns at
                                                                   once: STARTED (auto) / ASK; devlog_wait later */
void devlog_note(devlog_outcome outcome, const char *detail);  /* a run about to die: outcome on the card, no network */
const char *devlog_run_id(void);                               /* "" before begin / when disabled */
const char *devlog_outcome_name(devlog_outcome outcome);

/* the background worker */
int devlog_wait(unsigned timeout_ms, devlog_result *res);      /* waits for the running send (0: just poll, ~0u: until it
                                                                   ends); STARTED while it runs, else its result. Also
                                                                   hands the worker's log lines to the app's log */
int devlog_busy(void);                                         /* 1 while a background send runs */
void devlog_cancel(void);                                      /* stop it now (it ends within a few ms, also mid-copy:
                                                                   every loop checks the stop per piece; runs stay kept) */

/* mode=ask */
int devlog_should_ask(void);                                   /* > 0: that many runs wait for an answer (0 while the
                                                                   launch copies are still being made, or mode=auto) */
int devlog_send_now(int yes, devlog_result *res);              /* the answer. yes: send them all in the background
                                                                   (STARTED; also mode=auto with defer_launch_send);
                                                                   no: delete them (DROPPED); their logs stay on the
                                                                   card but never leave it (a tagged *.prev is never
                                                                   sent) */
int devlog_ask_console(int (*answer)(unsigned timeout_ms), unsigned timeout_ms, devlog_result *res);
	/* the whole prompt for debugScreen apps: two lines through the app's log function (= its screen), answer() returns
	 * 1 send / 0 delete / -1 not now (also on timeout; 0 = 30 s), then send + wait. devlog_pad.h has a ready answer()
	 * for the Vita's buttons (X / CIRCLE / START). Returns ASK (not now), DROPPED, or the send's result */

/* other */
int devlog_flush(devlog_result *res);                          /* send kept runs now and wait (auto; ask: only copies) */
int devlog_flush_app(const devlog_app *app, devlog_result *res);   /* the same without a run of its own */
int devlog_clear(const devlog_app *app, devlog_result *res);   /* delete every kept run, the cached address and
                                                                   the app's current + previous log (the
                                                                   current one is emptied in place). Works with devlog
                                                                   off too (app needed only before devlog_begin).
                                                                   devlog.txt clear=1 does it once at devlog_begin */

/* optional log-file writer for the app's log function. buffered=0 (dev builds, the default): every line is written
 * to the card at once, so a crash log is complete. buffered=1 (release builds): lines collect in a 16 KiB buffer and
 * are written every 64 lines, when the oldest waiting line is 2 s old (checked as lines arrive), on
 * devlog_logfile_flush / _close, and by devlog_begin (around the tag), devlog_note and devlog_end. One thread only. */
int devlog_logfile_open(const char *path, int append, int buffered);
void devlog_logfile_write(const char *line);                   /* one line; the '\n' is added */
void devlog_logfile_flush(void);
void devlog_logfile_close(void);

#ifdef __cplusplus
}
#endif
#endif
