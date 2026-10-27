/* Shepherd Log Send Test (SHPD00014): proves on a device session that a Vita app can send its logs to the owner's
 * Mac over the same Wi-Fi (vita/devlog/; the Archive Codex server, or tools/devlog-receiver/ as the fallback).
 * Silent (no audio), no game data. Revision 3 of the client: every send runs on a low-priority background thread
 * (core 2), devlog.txt's mode= decides whether it asks first (ask, the default) or sends on its own (auto).
 *
 *   1. log ux0:data/shepherd/logtest.txt (the previous one kept as logtest.prev.txt, as every Shepherd app does),
 *      written through devlog_logfile_* (write-through: a crash log stays complete)
 *   2. devlog_begin: run marker + tag, and the runs kept from earlier launches: mode=auto sends them in the
 *      background (the screen shows how long devlog_begin itself took, in microseconds); mode=ask asks first
 *   3. ~200 sample log lines (plain text, a tab, UTF-8, an ESC sequence, one control byte that goes as '?')
 *   4. the menu (the background send's result appears when it is done; the app never waits for it):
 *      X (or nothing for 20 s): end the run -- auto: devlog_end_async, the screen keeps counting while the worker
 *         sends; ask: the question (X send / CIRCLE delete / START later)
 *      R: big-log test -- appends ~8 MiB to the log first (the write time is shown), then as X
 *      TRIANGLE: crash test -- quits at once WITHOUT devlog_end; the next start finds it as "crash"
 *      SQUARE: abort test -- devlog_note(ABORT, ...) then quits; the next start finds it as "abort"
 *      CIRCLE: quit without ending (the run stays kept, the next launch of a Shepherd app handles it)
 *      SELECT: clear -- devlog_clear: deletes every kept run, the cached address, logtest.prev.txt, empties logtest.txt
 *      L: card timing -- 2,000 log lines written line by line (dev builds) vs buffered (release builds)
 * Without ux0:data/shepherd/devlog.txt (enabled=1 + token) the app says how to make one and touches no network.
 */
#include <psp2/ctrl.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/clib.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "../devlog_unity.c"
#include "../devlog_pad.h"
#include "debugScreen.h"

#ifndef LOGTEST_BUILD
#define LOGTEST_BUILD "unknown"
#endif
#ifndef LOGTEST_COMMIT
#define LOGTEST_COMMIT ""
#endif

#define LT_DIR "ux0:data/shepherd"
#define LT_LOG LT_DIR "/logtest.txt"
#define LT_PREV LT_DIR "/logtest.prev.txt"
#define LT_BENCH LT_DIR "/logtest-cardtime.txt"

static int g_screen;

static void lt_line(const char *line)
{
	devlog_logfile_write(line);   /* write-through (dev build): on the card when this returns */
	sceClibPrintf("%s\n", line);
	if (g_screen) {
		/* the screen gets printable text only (the log keeps the bytes as they are) */
		char s[200];
		size_t k = 0;
		for (const char *p = line; *p && k + 1 < sizeof s; p++) s[k++] = ((unsigned char)*p >= 0x20 && (unsigned char)*p < 0x7f) ? *p : ' ';
		s[k] = 0;
		psvDebugScreenPuts(s);
		psvDebugScreenPuts("\n");
	}
}

static void lt_log(const char *fmt, ...)
{
	char b[512];
	va_list ap;
	va_start(ap, fmt);
	vsnprintf(b, sizeof b, fmt, ap);
	va_end(ap);
	lt_line(b);
}

static const char *lt_rc(int rc)
{
	switch (rc) {
	case DEVLOG_SENT: return "sent";
	case DEVLOG_PENDING: return "kept for later";
	case DEVLOG_REJECTED: return "REJECTED";
	case DEVLOG_STARTED: return "sending in the background";
	case DEVLOG_ASK: return "waiting for your answer";
	case DEVLOG_DROPPED: return "deleted";
	default: return "off";
	}
}

static void lt_result(const char *what, int rc, const devlog_result *r)
{
	lt_log("%s: rc=%d (%s) | mode=%s | net=%d reached=%d %s | runs sent %d, kept %d, rejected %d | files sent %d, dropped %d, %llu bytes | %u ms",
	       what, rc, lt_rc(rc), r->mode == DEVLOG_MODE_AUTO ? "auto" : r->mode == DEVLOG_MODE_ASK ? "ask" : "off",
	       r->net, r->reached, r->server[0] ? r->server : "-", r->runs_sent, r->runs_pending, r->runs_rejected, r->files_sent,
	       r->files_dropped, r->bytes_sent, r->ms);
	lt_log("%s: %s", what, r->why);
}

static uint64_t lt_us(void) { return sceKernelGetProcessTimeWide(); }

/* a background send started earlier (launch / end): report it once it is done; 1 when it was reported now */
static int g_bg_pending;
static int lt_poll_bg(const char *what)
{
	if (!g_bg_pending) return 0;
	devlog_result r;
	int rc = devlog_wait(0, &r);   /* never blocks; also hands the worker's lines to lt_line */
	if (rc == DEVLOG_STARTED) return 0;
	g_bg_pending = 0;
	lt_result(what, rc, &r);
	return 1;
}

static unsigned lt_wait_buttons(unsigned mask, unsigned timeout_ms)
{
	SceCtrlData pad;
	unsigned prev = 0;
	for (unsigned t = 0; timeout_ms == 0 || t < timeout_ms; t += 16) {
		memset(&pad, 0, sizeof pad);
		sceCtrlPeekBufferPositive(0, &pad, 1);
		unsigned pressed = pad.buttons & ~prev & mask;   /* a new press, not a button still held from before */
		prev = pad.buttons;
		if (pressed) return pressed;
		lt_poll_bg("launch send");
		sceKernelDelayThread(16 * 1000);
	}
	return 0;
}

static void lt_exit(void)
{
	lt_log("(press START to exit, or wait 30 s)");
	lt_wait_buttons(SCE_CTRL_START, 30000);
	devlog_logfile_close();
	sceKernelExitProcess(0);
}

/* L: what a log line costs on this memory card, line by line vs buffered (the release-build helper's way) */
static void lt_card_timing(void)
{
	static char buf[16384];
	char line[96];
	uint64_t us[2] = { 0, 0 };
	for (int mode = 0; mode < 2; mode++) {
		SceUID fd = sceIoOpen(LT_BENCH, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_TRUNC, 0777);
		if (fd < 0) { lt_log("card timing: cannot open %s (0x%08x)", LT_BENCH, (unsigned)fd); return; }
		size_t n = 0;
		uint64_t t0 = lt_us();
		for (int i = 0; i < 2000; i++) {
			int k = snprintf(line, sizeof line, "T%05d timing line, 80 bytes of the kind an app logs every frame..........\n", i);
			if (!mode) sceIoWrite(fd, line, (SceSize)k);
			else { if (n + (size_t)k > sizeof buf) { sceIoWrite(fd, buf, (SceSize)n); n = 0; } memcpy(buf + n, line, (size_t)k); n += (size_t)k; }
		}
		if (n) sceIoWrite(fd, buf, (SceSize)n);
		sceIoClose(fd);
		us[mode] = lt_us() - t0;
	}
	sceIoRemove(LT_BENCH);
	lt_log("card timing, 2,000 lines of 80 bytes: line by line %llu us (%llu us/line), buffered %llu us (%llu us/line)",
	       (unsigned long long)us[0], (unsigned long long)(us[0] / 2000), (unsigned long long)us[1], (unsigned long long)(us[1] / 2000));
}

int main(void)
{
	sceIoMkdir("ux0:data", 0777);
	sceIoMkdir(LT_DIR, 0777);
	sceIoRemove(LT_PREV);                 /* the Shepherd apps' convention: keep the previous log */
	sceIoRename(LT_LOG, LT_PREV);
	devlog_logfile_open(LT_LOG, 0, 0);    /* a new log, write-through */
	g_screen = psvDebugScreenInit() >= 0;
	lt_log("BEGIN shepherd-logtest build %s (devlog " DEVLOG_CLIENT ", SHPD00014)", LOGTEST_BUILD);
	lt_log("Shepherd Log Send Test: sends this log to the Mac over Wi-Fi (Codex server, or the receiver). Silent; no game data.");

	static const devlog_app DL = {
		.app = "logtest", .title_id = "SHPD00014", .build = LOGTEST_BUILD, .commit = LOGTEST_COMMIT,
		.log_path = LT_LOG, .prev_path = LT_PREV, .log = lt_line,
		/* defaults: worker priority 191 on core 2 (SCE_KERNEL_CPU_MASK_USER_2) */
	};
	devlog_result r;
	uint64_t t0 = lt_us();
	int rc = devlog_begin(&DL, &r);
	uint64_t begin_us = lt_us() - t0;
	if (rc == DEVLOG_DISABLED) {
		lt_log("devlog is OFF: %s", r.why);
		lt_log("To turn it on: on the Mac run  tools/devlog-receiver/devlogctl.sh token  (the Archive Codex server");
		lt_log("takes the logs; or also  devlogctl.sh start  for the standalone receiver), then copy");
		lt_log("$SHEPHERD_WORK/devlog/vita-devlog.txt to this Vita as ux0:data/shepherd/devlog.txt");
		lt_log("(VitaShell: SELECT starts FTP). mode=ask (default) asks before sending, mode=auto just sends.");
		lt_exit();
		return 0;
	}
	lt_result("begin", rc, &r);
	lt_log("devlog_begin took %llu us in this thread (the sends run on the worker); run id %s", (unsigned long long)begin_us, devlog_run_id());
	if (rc == DEVLOG_STARTED) g_bg_pending = 1;
	if (rc == DEVLOG_ASK) {   /* runs kept from earlier launches (e.g. the crash test): ask now */
		devlog_wait(10000, NULL);   /* the copies of those runs (local, on the worker) */
		rc = devlog_ask_console(devlog_pad_answer, 30000, &r);
		lt_result("launch question", rc, &r);
	}

	/* sample content: what a real log looks like, plus the cases the text rule handles */
	dlp_device dev;
	dlp_device_info(&dev);
	lt_log("device: %s %s fw %s, free main %u KiB, cdram %u KiB, phycont %u KiB", dev.platform, dev.model, dev.fw,
	       dev.free_kb, dev.free_cdram_kb, dev.free_phycont_kb);
	for (int i = 0; i < 200; i++)
		lt_log("S%03d sample line\ttab | utf-8 \xc3\xa9\xe2\x9c\x93 | esc \x1b[1mbold\x1b[0m | %08x", i, (unsigned)(i * 2654435761u));
	lt_log("one control byte follows and is sent as '?': [\x01]");
	lt_log("PASS END sample written");

	const unsigned MENU = SCE_CTRL_CROSS | SCE_CTRL_RTRIGGER | SCE_CTRL_R1 | SCE_CTRL_TRIANGLE | SCE_CTRL_SQUARE | SCE_CTRL_CIRCLE
	                    | SCE_CTRL_SELECT | SCE_CTRL_LTRIGGER | SCE_CTRL_L1;
	unsigned b;
	for (;;) {
		lt_log("X: end + send   R: big-log test (8 MiB)   TRIANGLE: crash   SQUARE: abort   CIRCLE: quit, keep   SELECT: clear   L: card timing   (20 s: X)");
		b = lt_wait_buttons(MENU, 20000);
		if (b & SCE_CTRL_SELECT) {
			rc = devlog_clear(NULL, &r);
			g_bg_pending = 0;
			lt_result("clear", rc, &r);
			continue;
		}
		if (b & (SCE_CTRL_LTRIGGER | SCE_CTRL_L1)) { lt_card_timing(); continue; }
		break;
	}
	if (b & (SCE_CTRL_RTRIGGER | SCE_CTRL_R1)) {
		/* ~8 MiB of log lines in 32 KiB writes (the way a chatty app grows its log), then the end as usual */
		static char chunk[32768];
		size_t k = 0;
		for (int i = 0; k + 100 < sizeof chunk; i++)
			k += (size_t)snprintf(chunk + k, sizeof chunk - k, "BIG %06d filler filler filler filler filler filler filler filler\n", i);
		uint64_t tb = lt_us();
		SceUID fd = sceIoOpen(LT_LOG, SCE_O_WRONLY | SCE_O_CREAT | SCE_O_APPEND, 0777);
		long long written = 0;
		if (fd >= 0) {
			for (int i = 0; i < 256; i++) { int w = sceIoWrite(fd, chunk, (SceSize)k); if (w <= 0) break; written += w; }
			sceIoClose(fd);
		}
		lt_log("BIG-LOG TEST: appended %lld bytes in %llu ms; now ending the run", written, (unsigned long long)((lt_us() - tb) / 1000u));
		lt_log("BIG-END");
	}
	if (b & SCE_CTRL_TRIANGLE) {
		lt_log("CRASH TEST: quitting now without devlog_end. Start the app again: this run is then found as 'crash'.");
		sceKernelDelayThread(3 * 1000 * 1000);
		sceKernelExitProcess(0);
		return 0;
	}
	if (b & SCE_CTRL_SQUARE) {
		devlog_note(DEVLOG_ABORT, "logtest abort test");
		lt_log("ABORT TEST: outcome noted, quitting. Start the app again: this run is then found as 'abort'.");
		sceKernelDelayThread(3 * 1000 * 1000);
		sceKernelExitProcess(0);
		return 0;
	}
	if (b & SCE_CTRL_CIRCLE) {
		lt_log("quit without ending: the run stays kept; the next launch of a Shepherd app handles it.");
		sceKernelDelayThread(2 * 1000 * 1000);
		sceKernelExitProcess(0);
		return 0;
	}
	lt_poll_bg("launch send");
	uint64_t te = lt_us();
	rc = devlog_end_async(DEVLOG_OK, (b & (SCE_CTRL_RTRIGGER | SCE_CTRL_R1)) ? "logtest: big-log test" : "logtest: sample sent", &r);
	uint64_t end_us = lt_us() - te;
	lt_log("devlog_end_async took %llu us in this thread (%s)", (unsigned long long)end_us, lt_rc(rc));
	if (rc == DEVLOG_STARTED) {
		/* the app is free meanwhile: count on the screen while the worker sends */
		uint64_t ts = lt_us();
		unsigned dots = 0;
		while ((rc = devlog_wait(250, &r)) == DEVLOG_STARTED)
			if (++dots % 4 == 0) lt_log("... still sending in the background (%llu ms), the app keeps running", (unsigned long long)((lt_us() - ts) / 1000u));
	} else if (rc == DEVLOG_ASK) {
		rc = devlog_ask_console(devlog_pad_answer, 30000, &r);
	}
	lt_result("end", rc, &r);
	if (rc == DEVLOG_SENT)
		lt_log("PASS sent to %s in %u ms. On the Mac: the Codex /vita/logs page, or devlogctl.sh list", r.server, r.ms);
	else if (rc == DEVLOG_REJECTED)
		lt_log("REJECTED by the receiver (dropped, see its log on the Mac): %s", r.why);
	else if (rc == DEVLOG_DROPPED)
		lt_log("DELETED at your request: %s", r.why);
	else if (rc == DEVLOG_ASK)
		lt_log("NOT ANSWERED: kept on the card, asked again at the next end of a run: %s", r.why);
	else
		lt_log("NOT SENT (kept on the card, retried at the next launch) after %u ms: %s", r.ms, r.why);
	lt_exit();
	return 0;
}
