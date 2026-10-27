/* e2e driver: plays a Shepherd app on the Mac, using ONLY the devlog client's public API (vita/devlog/devlog.h),
 * built from the client's own sources (devlog_unity.c, POSIX platform layer). e2e_test.sh runs it against a receiver
 * bound to 127.0.0.1.
 *
 *   mac_driver <root> <mode> [app] [outfile]
 *     root   the fake ux0:data/shepherd folder (devlog.txt lives there)
 *     mode   run     rotate log, BEGIN, devlog_begin, 200 lines, devlog_end(OK)
 *            fail    same, devlog_end(FAIL, "FAIL test: deliberate")
 *            crash   rotate log, BEGIN, devlog_begin, 50 lines, then _exit(3) (no devlog_end: a crash)
 *            flush   devlog_begin (sends pending runs), devlog_flush, devlog_end(OK): a launch that only flushes
 *            unsafe  like run, but lists extra files that must NEVER be uploaded (config, save data, owner folders)
 *   prints key=value lines: rc, enabled, net, reached, server, runs_sent, runs_pending, files_sent, files_dropped,
 *   bytes_sent, ms, why, run (the run id) -- for the begin and the end/flush call. begin.* is the result of the launch's
 *   background send when devlog_begin started one (revision 3; e2e_test.sh's devlog.txt says mode=auto).
 */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/stat.h>

#include "devlog_unity.c"

static char g_log_path[1024];

static void log_line(const char *line)
{
	FILE *f = fopen(g_log_path, "a");
	if (!f)
		return;
	fputs(line, f);
	fputc('\n', f);
	fclose(f);
}

static void print_res(const char *tag, int rc, const devlog_result *r)
{
	printf("%s.rc=%d\n%s.enabled=%d\n%s.net=%d\n%s.reached=%d\n%s.server=%s\n%s.runs_sent=%d\n%s.runs_pending=%d\n"
	       "%s.files_sent=%d\n%s.files_dropped=%d\n%s.bytes_sent=%llu\n%s.ms=%u\n%s.why=%s\n",
	       tag, rc, tag, r->enabled, tag, r->net, tag, r->reached, tag, r->server, tag, r->runs_sent, tag,
	       r->runs_pending, tag, r->files_sent, tag, r->files_dropped, tag, r->bytes_sent, tag, r->ms, tag, r->why);
	fflush(stdout);
}

int main(int argc, char **argv)
{
	if (argc < 3) {
		fprintf(stderr, "usage: %s <root> run|fail|crash|flush|unsafe [app]\n", argv[0]);
		return 2;
	}
	const char *root = argv[1], *mode = argv[2];
	const char *app = argc > 3 ? argv[3] : "logtest";
	static char prev_path[1024], extra1[1024], extra2[1024], extra3[1024], extra4[1024], extra5[1024], extra6[1024];
	snprintf(g_log_path, sizeof g_log_path, "%s/%s.txt", root, app);
	snprintf(prev_path, sizeof prev_path, "%s/%s.prev.txt", root, app);
	/* an allowed extra (runtime/logs/) and forbidden ones (config, save data, owner folders, a binary) */
	snprintf(extra1, sizeof extra1, "%s/runtime/logs/engine.log", root);
	snprintf(extra2, sizeof extra2, "%s/devlog.txt", root);
	snprintf(extra3, sizeof extra3, "%s/persistent/saves/slot_0.txt", root);
	snprintf(extra4, sizeof extra4, "%s/managed/notes.txt", root);
	snprintf(extra5, sizeof extra5, "%s/env.txt", root);
	snprintf(extra6, sizeof extra6, "%s/../outside.txt", root);
	static const char *extra_ok[2];
	static const char *extra_bad[7];
	extra_ok[0] = extra1; extra_ok[1] = NULL;
	extra_bad[0] = extra1; extra_bad[1] = extra2; extra_bad[2] = extra3; extra_bad[3] = extra4; extra_bad[4] = extra5;
	extra_bad[5] = extra6; extra_bad[6] = NULL;
	int unsafe = !strcmp(mode, "unsafe");

	devlog_app A;
	memset(&A, 0, sizeof A);
	A.app = app;
	A.title_id = "SHPD00014";
	A.build = "e2e-mac-driver";
	A.commit = "0000000";
	A.root = root;
	A.log_path = g_log_path;
	A.prev_path = prev_path;
	A.extra = unsafe ? extra_bad : extra_ok;
	A.log = log_line;

	devlog_result r;
	int rc;
	if (!strcmp(mode, "flush")) {
		/* an app launch that only flushes: begin (which already sends pending runs), flush, end */
		log_line("BEGIN e2e mac driver (flush)");
		memset(&r, 0, sizeof r);
		rc = devlog_begin(&A, &r);
		if (rc == DEVLOG_STARTED) rc = devlog_wait(~0u, &r);   /* revision 3: kept runs go on the worker */
		print_res("begin", rc, &r);
		printf("run=%s\n", devlog_run_id());
		memset(&r, 0, sizeof r);
		rc = devlog_flush(&r);
		print_res("flush", rc, &r);
		memset(&r, 0, sizeof r);
		rc = devlog_end(DEVLOG_OK, "flush only", &r);
		print_res("end", rc, &r);
		return 0;
	}

	/* the apps' convention: keep the previous run's log as <app>.prev.txt, start a new one */
	remove(prev_path);
	rename(g_log_path, prev_path);
	log_line("BEGIN e2e mac driver");

	memset(&r, 0, sizeof r);
	rc = devlog_begin(&A, &r);
	if (rc == DEVLOG_STARTED) rc = devlog_wait(~0u, &r);   /* revision 3: kept runs go on the worker */
	print_res("begin", rc, &r);
	printf("run=%s\n", devlog_run_id());
	fflush(stdout);

	int lines = !strcmp(mode, "crash") ? 50 : 200;
	for (int i = 0; i < lines; i++) {
		char b[160];
		snprintf(b, sizeof b, "P line %d of the e2e run: \033[1mbold\033[0m\ttab", i);
		log_line(b);
	}
	if (!strcmp(mode, "crash")) {
		log_line("about to crash");
		_exit(3);
	}
	int fail = !strcmp(mode, "fail");
	log_line(fail ? "FAIL END deliberate" : "PASS END");
	memset(&r, 0, sizeof r);
	rc = devlog_end(fail ? DEVLOG_FAIL : DEVLOG_OK, fail ? "FAIL test: deliberate" : "PASS END", &r);
	print_res("end", rc, &r);
	return 0;
}
