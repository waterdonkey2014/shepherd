/* devlog_cli: plays one Shepherd app run on the Mac with the devlog client (POSIX build of the same sources),
 * the way vita/devlog/integrate.patch wires it into the Vita apps: rotate the log, first line, devlog_begin,
 * N log lines, devlog_end. For test.sh (against tools/devlog-receiver/receiver.py on 127.0.0.1).
 *
 *   devlog_cli <root> <mode> [--app A] [--lines N] [--nul] [--big BYTES] [--pre BYTES] [--extra REL]...
 *                            [--outcome ok|fail] [--detail TEXT] [--answer yes|no|later] [--async]
 *                            [--logfile] [--buffered] [--no-wait] [--clear-mid] [--ask-launch]
 *     --pre: BYTES of log written before devlog_begin (the tag then sits that far into the log)
 *     --answer: mode=ask: after devlog_end, devlog_ask_console answers with this (default: later = no answer)
 *     --async: devlog_end_async + devlog_wait instead of devlog_end (end_call_us = the async call alone)
 *     --logfile / --buffered: the app logs through devlog_logfile_* (write-through / buffered)
 *     --no-wait: do not wait for a background launch send after devlog_begin (the end then cancels it)
 *     --clear-mid: devlog_clear(NULL) between the log lines (a "clear logs" button pressed during the run)
 *     --ask-launch: mode=ask: right after devlog_begin, devlog_ask_console about the runs kept from earlier launches
 *     mode: run    full run, devlog_end
 *           crash  begin, lines, "about to crash", _exit(3) without devlog_end
 *           note   begin, devlog_note(ABORT, ...), _exit(4)  (a fatal path that records why, then dies)
 *           flush  devlog_flush_app only (an app that just sends what was kept)
 *           clear  devlog_clear(app) only, before any devlog_begin (a "clear logs" menu item)
 *   prints key=value lines (begin.* / end.* / flush.* / ask.* / clear.*, run=, begin_call_us=, end_call_us=,
 *   begin_wall_ms=, end_wall_ms=, wall_ms=). begin.* is the result of the launch's background send when there was
 *   one (begin.call_rc = what devlog_begin itself returned). */
#include "../devlog_unity.c"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

static char g_log[1024];
static int g_use_lf, g_answer = -1;

static void log_line(const char *line)
{
	if (g_use_lf) { devlog_logfile_write(line); return; }
	FILE *f = fopen(g_log, "a");
	if (!f) return;
	fputs(line, f); fputc('\n', f);
	fclose(f);
}

static void pr(const char *tag, int rc, const devlog_result *r)
{
	printf("%s.rc=%d\n%s.enabled=%d\n%s.net=%d\n%s.reached=%d\n%s.server=%s\n%s.runs_sent=%d\n%s.runs_pending=%d\n"
	       "%s.files_sent=%d\n%s.files_dropped=%d\n%s.runs_rejected=%d\n%s.bytes_sent=%llu\n%s.ms=%u\n%s.why=%s\n%s.mode=%d\n",
	       tag, rc, tag, r->enabled, tag, r->net, tag, r->reached, tag, r->server, tag, r->runs_sent, tag, r->runs_pending,
	       tag, r->files_sent, tag, r->files_dropped, tag, r->runs_rejected, tag, r->bytes_sent, tag, r->ms, tag, r->why,
	       tag, r->mode);
	fflush(stdout);
}

static long long now_ms(void) { struct timeval tv; gettimeofday(&tv, NULL); return tv.tv_sec * 1000LL + tv.tv_usec / 1000; }
static long long now_us(void) { struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts); return ts.tv_sec * 1000000LL + ts.tv_nsec / 1000; }
static int answer(unsigned timeout_ms) { (void)timeout_ms; return g_answer; }

int main(int argc, char **argv)
{
	if (argc < 3) { fprintf(stderr, "usage: devlog_cli <root> run|crash|note|flush|clear [options]\n"); return 2; }
	const char *root = argv[1], *mode = argv[2], *app = "logtest", *outcome = "ok", *detail = NULL;
	int lines = 100, nul = 0, async = 0, buffered = 0, no_wait = 0, clear_mid = 0, ask_launch = 0;
	long big = 0, pre = 0;
	static char extra_store[16][1024];
	static const char *extra[17];
	int ne = 0;
	for (int i = 3; i < argc; i++) {
		if (!strcmp(argv[i], "--app") && i + 1 < argc) app = argv[++i];
		else if (!strcmp(argv[i], "--lines") && i + 1 < argc) lines = atoi(argv[++i]);
		else if (!strcmp(argv[i], "--nul")) nul = 1;
		else if (!strcmp(argv[i], "--big") && i + 1 < argc) big = atol(argv[++i]);
		else if (!strcmp(argv[i], "--outcome") && i + 1 < argc) outcome = argv[++i];
		else if (!strcmp(argv[i], "--detail") && i + 1 < argc) detail = argv[++i];
		else if (!strcmp(argv[i], "--pre") && i + 1 < argc) pre = atol(argv[++i]);
		else if (!strcmp(argv[i], "--answer") && i + 1 < argc) { i++; g_answer = !strcmp(argv[i], "yes") ? 1 : !strcmp(argv[i], "no") ? 0 : -1; }
		else if (!strcmp(argv[i], "--async")) async = 1;
		else if (!strcmp(argv[i], "--logfile")) g_use_lf = 1;
		else if (!strcmp(argv[i], "--buffered")) { g_use_lf = 1; buffered = 1; }
		else if (!strcmp(argv[i], "--no-wait")) no_wait = 1;
		else if (!strcmp(argv[i], "--clear-mid")) clear_mid = 1;
		else if (!strcmp(argv[i], "--ask-launch")) ask_launch = 1;
		else if (!strcmp(argv[i], "--extra") && i + 1 < argc && ne < 16) {
			snprintf(extra_store[ne], sizeof extra_store[ne], "%s/%s", root, argv[++i]);
			extra[ne] = extra_store[ne]; ne++;
		} else { fprintf(stderr, "bad option %s\n", argv[i]); return 2; }
	}
	extra[ne] = NULL;
	static char prev[1024];
	snprintf(g_log, sizeof g_log, "%s/%s.txt", root, app);
	snprintf(prev, sizeof prev, "%s/%s.prev.txt", root, app);
	devlog_app A;
	memset(&A, 0, sizeof A);
	A.app = app; A.title_id = "SHPD00014"; A.build = "mac-cli"; A.commit = "0000000"; A.root = root;
	A.log_path = g_log; A.prev_path = prev; A.extra = ne ? extra : NULL; A.log = log_line;
	devlog_result r;
	long long t0 = now_ms();
	if (!strcmp(mode, "flush")) {
		int rc = devlog_flush_app(&A, &r);
		pr("flush", rc, &r);
		printf("wall_ms=%lld\n", now_ms() - t0);
		return 0;
	}
	if (!strcmp(mode, "clear")) {
		int rc = devlog_clear(&A, &r);
		pr("clear", rc, &r);
		return 0;
	}
	remove(prev);
	rename(g_log, prev);
	if (g_use_lf) devlog_logfile_open(g_log, 1, buffered);
	log_line("BEGIN devlog_cli (mac)");
	if (pre > 0) {
		devlog_logfile_flush();
		FILE *f = fopen(g_log, "a");
		if (f) { for (long n = 0; n < pre;) { int w = fprintf(f, "PRE %09ld written before devlog_begin\n", n); n += w > 0 ? w : 1; } fclose(f); }
	}
	long long tb = now_ms(), tbu = now_us();
	int rc = devlog_begin(&A, &r);
	long long begin_call_us = now_us() - tbu;
	printf("begin.call_rc=%d\nbegin_call_us=%lld\nbegin.call_why=%s\n", rc, begin_call_us, r.why);
	if (rc == DEVLOG_STARTED && !no_wait) rc = devlog_wait(~0u, &r);
	long long begin_wall = now_ms() - tb;
	pr("begin", rc, &r);
	printf("begin_wall_ms=%lld\n", begin_wall);
	printf("run=%s\n", devlog_run_id());
	fflush(stdout);
	if (ask_launch) {
		devlog_wait(~0u, NULL);   /* the launch copies */
		printf("asklaunch.should=%d\n", devlog_should_ask());
		rc = devlog_ask_console(answer, 1000, &r);
		pr("asklaunch", rc, &r);
	}
	for (int i = 0; i < lines; i++) {
		char b[200];
		snprintf(b, sizeof b, "P line %d: tab\there, esc \033[1mbold\033[0m, utf-8 \xc3\xa9", i);
		log_line(b);
		if (clear_mid && i == lines / 2) {
			devlog_result cr;
			int crc = devlog_clear(NULL, &cr);
			pr("clear", crc, &cr);
			log_line("AFTER CLEAR");
		}
	}
	devlog_logfile_flush();
	if (nul) { FILE *f = fopen(g_log, "ab"); if (f) { static const char z[16] = { 0 }; fwrite(z, 1, sizeof z, f); fputs("\x01\x02 after a zero-filled gap\n", f); fclose(f); } }
	if (big > 0) {
		FILE *f = fopen(g_log, "a");
		if (f) { for (long n = 0; n < big;) { int w = fprintf(f, "BIG %09ld filler filler filler filler filler filler\n", n); n += w > 0 ? w : 1; } fputs("BIG-END\n", f); fclose(f); }
	}
	if (!strcmp(mode, "crash")) {
		log_line("about to crash");
		if (buffered) { for (int i = 0; i < 5; i++) log_line("UNFLUSHED tail line (buffered, lost in a crash)"); }
		_exit(3);
	}
	if (!strcmp(mode, "note")) { log_line("FAIL fatal error (test)"); devlog_note(DEVLOG_ABORT, "runtime fatal error (test)"); _exit(4); }
	int fail = !strcmp(outcome, "fail");
	log_line(fail ? "FAIL END (test)" : "PASS END");
	long long te = now_ms(), teu = now_us();
	if (async) {
		rc = devlog_end_async(fail ? DEVLOG_FAIL : DEVLOG_OK, detail ? detail : fail ? "FAIL END (test)" : "PASS END", &r);
		printf("end.call_rc=%d\nend_call_us=%lld\n", rc, now_us() - teu);
		if (rc == DEVLOG_STARTED) rc = devlog_wait(~0u, &r);
	} else {
		rc = devlog_end(fail ? DEVLOG_FAIL : DEVLOG_OK, detail ? detail : fail ? "FAIL END (test)" : "PASS END", &r);
	}
	long long end_wall = now_ms() - te;
	pr("end", rc, &r);
	printf("end_wall_ms=%lld\n", end_wall);
	if (rc == DEVLOG_ASK) {
		printf("ask.should=%d\n", devlog_should_ask());
		rc = devlog_ask_console(answer, 1000, &r);
		pr("ask", rc, &r);
	}
	devlog_logfile_close();
	printf("wall_ms=%lld\n", now_ms() - t0);
	return 0;
}
