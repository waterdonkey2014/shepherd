/* devlog platform layer: the few file, clock and socket calls the client needs, implemented twice:
 *   devlog_plat_vita.c   sceIo / sceKernel / sceRtc / sceNet + sceNetCtl (vitasdk)
 *   devlog_plat_posix.c  POSIX (the Mac test build)
 * Part of the unity build (devlog_unity.c): everything here is static. Handles are ints >= 0; errors are < 0.
 * IPv4 addresses are in network byte order (uint32_t as it sits in sin_addr).
 * Both layers use non-blocking sockets and explicit deadlines, so the same timings hold on the Mac and the Vita. */
#ifndef DEVLOG_PLAT_H
#define DEVLOG_PLAT_H
#include <stddef.h>
#include <stdint.h>

/* --- time, randomness */
static uint64_t dlp_now_ms(void);                       /* monotonic */
static void dlp_sleep_ms(unsigned ms);
static void dlp_random(void *buf, size_t n);            /* best randomness the platform has */
static void dlp_utc(int *y, int *mo, int *d, int *h, int *mi, int *s);   /* device clock, UTC */

/* --- files (paths are full: "ux0:data/shepherd/x.txt" or a POSIX path) */
static int dlp_open_read(const char *path);
static int dlp_open_write(const char *path, int append);        /* create; truncate unless append */
static int dlp_open_update(const char *path);                   /* existing file, write in place (no truncation) */
static long dlp_read(int fd, void *buf, size_t n);
static long dlp_write(int fd, const void *buf, size_t n);
static int dlp_seek(int fd, int64_t off);                         /* from the start */
static void dlp_close(int fd);
static int64_t dlp_file_size(const char *path);                  /* -1: missing / not a regular file */
static int dlp_remove(const char *path);
static int dlp_mkdir(const char *path);                           /* 0 also when it exists */
static int dlp_rmdir(const char *path);
/* calls cb(name) for each entry of dir (no "." / ".."); cb returns nonzero to stop */
static int dlp_list_dir(const char *dir, int (*cb)(const char *name, int is_dir, void *u), void *u);

/* --- device facts for the meta (never identifiers) */
typedef struct {
	char platform[8];        /* "vita" / "mac" */
	char model[16];          /* "PCH-1000" / "PCH-2000" / "VTE-1000" / "mac" / "unknown" */
	char fw[16];             /* "3.65" */
	unsigned free_kb, free_cdram_kb, free_phycont_kb;   /* 0 = not applicable */
} dlp_device;
static void dlp_device_info(dlp_device *d);

/* --- network
 * dlp_net_up: brings the network library up (Vita: sysmodule + sceNetInit + sceNetCtlInit, each only if not
 * already up) and reports whether there is an IPv4 link. Returns 1 connected (ip/mask filled, network order),
 * 0 not connected, < 0 error. dlp_net_down undoes only what dlp_net_up did. */
static int dlp_net_up(uint32_t *ip, uint32_t *mask);
static void dlp_net_down(void);
static int dlp_tcp_connect(uint32_t ip, uint16_t port, unsigned timeout_ms);   /* socket, or < 0 */
/* sends all n bytes: 0 done, -1 error, -2 no progress for stall_ms (peer stopped reading), -3 deadline reached
 * (deadline_ms on the dlp_now_ms clock; the whole call never runs past it) */
static int dlp_tcp_send_all(int s, const void *buf, size_t n, unsigned stall_ms, uint64_t deadline_ms);
static int dlp_tcp_recv(int s, void *buf, size_t n, unsigned timeout_ms);  /* > 0 bytes, 0 closed, -1 err, -2 timeout */
static void dlp_tcp_close(int s);
static int dlp_udp_open(void);                                   /* broadcast-enabled datagram socket */
static int dlp_udp_sendto(int s, uint32_t ip, uint16_t port, const void *buf, size_t n);
static int dlp_udp_recvfrom(int s, void *buf, size_t n, unsigned timeout_ms, uint32_t *from_ip);
static void dlp_udp_close(int s);
static int dlp_parse_ipv4(const char *s, uint32_t *ip);           /* 1 ok */
static void dlp_format_ipv4(uint32_t ip, char out[16]);

/* --- one background worker at a time (devlog's sends run on it; PROTOCOL.md §6, README "Threads")
 * dlp_thread_start: starts fn(arg) on a new thread. prio / affinity are the Vita's sceKernelCreateThread values
 * (POSIX: a lower QoS class instead, affinity ignored). 0 started, < 0 could not (the caller then runs fn itself).
 * dlp_thread_join: waits for the thread started last and releases it. dlp_on_worker: 1 on that thread. */
static int dlp_thread_start(int (*fn)(void *), void *arg, int prio, unsigned affinity, unsigned stack_bytes);
static void dlp_thread_join(void);
static int dlp_on_worker(void);

/* --- stop request: every wait loop of the platform layer (connect, send, recv, recvfrom) ends early, as if its
 * deadline had passed, once dlp_stop_set(1) was called (devlog_cancel, devlog_end while a launch send runs) */
static int g_dlp_stop;
static void dlp_stop_set(int on) { __atomic_store_n(&g_dlp_stop, on, __ATOMIC_RELEASE); }
static int dlp_stopping(void) { return __atomic_load_n(&g_dlp_stop, __ATOMIC_ACQUIRE); }

/* --- diagnostics: the last raw network error (which call, which code: an SCE error on the Vita, errno on the Mac)
 * and a short note about the network setup (the Vita's sceNet pool size). Reset by dlp_err_reset. Never contain an
 * address or identifier: they go into the app's log and the result's why line. */
static void dlp_err(const char *call, int code);                   /* code 0: call says what happened */
static void dlp_err_reset(void);
static void dlp_err_text(char *out, size_t n);                    /* "" when there was none */
static void dlp_net_note(char *out, size_t n);

#endif
