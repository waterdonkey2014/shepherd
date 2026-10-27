/* devlog platform layer, POSIX (the Mac test build of the Vita client). See devlog_plat.h. */
#include "devlog_plat.h"
#include <arpa/inet.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <ifaddrs.h>
#include <net/if.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>
#if defined(__APPLE__)
#include <mach/mach.h>
#endif

static const char *g_dlp_err_call;
static int g_dlp_err_code;
static void dlp_err(const char *call, int code) { g_dlp_err_call = call; g_dlp_err_code = code; }
static void dlp_err_reset(void) { g_dlp_err_call = NULL; g_dlp_err_code = 0; }
static void dlp_err_text(char *out, size_t n)
{
	if (!n) return;
	if (!g_dlp_err_call) { out[0] = 0; return; }
	if (g_dlp_err_code) snprintf(out, n, "%s errno %d", g_dlp_err_call, g_dlp_err_code);
	else snprintf(out, n, "%s", g_dlp_err_call);
}
static void dlp_net_note(char *out, size_t n) { if (n) out[0] = 0; }

static uint64_t dlp_now_ms(void)
{
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (uint64_t)ts.tv_sec * 1000u + (uint64_t)ts.tv_nsec / 1000000u;
}

static void dlp_sleep_ms(unsigned ms)
{
	struct timespec ts = { (time_t)(ms / 1000), (long)(ms % 1000) * 1000000L };
	while (nanosleep(&ts, &ts) != 0 && errno == EINTR) {}
}

static void dlp_random(void *buf, size_t n)
{
#if defined(__APPLE__) || defined(__FreeBSD__)
	arc4random_buf(buf, n);
#else
	int fd = open("/dev/urandom", O_RDONLY);
	if (fd >= 0) { ssize_t r = read(fd, buf, n); (void)r; close(fd); }
#endif
}

static void dlp_utc(int *y, int *mo, int *d, int *h, int *mi, int *s)
{
	time_t t = time(NULL);
	struct tm tm;
	gmtime_r(&t, &tm);
	*y = tm.tm_year + 1900; *mo = tm.tm_mon + 1; *d = tm.tm_mday; *h = tm.tm_hour; *mi = tm.tm_min; *s = tm.tm_sec;
}

static int dlp_open_read(const char *path) { int fd = open(path, O_RDONLY); return fd >= 0 ? fd : -1; }
static int dlp_open_write(const char *path, int append)
{
	int fd = open(path, O_WRONLY | O_CREAT | (append ? O_APPEND : O_TRUNC), 0644);
	return fd >= 0 ? fd : -1;
}
static int dlp_open_update(const char *path) { int fd = open(path, O_WRONLY); return fd >= 0 ? fd : -1; }
static long dlp_read(int fd, void *buf, size_t n) { ssize_t r; do r = read(fd, buf, n); while (r < 0 && errno == EINTR); return (long)r; }
static long dlp_write(int fd, const void *buf, size_t n)
{
	size_t done = 0;
	while (done < n) {
		ssize_t r = write(fd, (const char *)buf + done, n - done);
		if (r < 0 && errno == EINTR) continue;
		if (r <= 0) return -1;
		done += (size_t)r;
	}
	return (long)done;
}
static int dlp_seek(int fd, int64_t off) { return lseek(fd, (off_t)off, SEEK_SET) == (off_t)off ? 0 : -1; }
static void dlp_close(int fd) { if (fd >= 0) close(fd); }
static int64_t dlp_file_size(const char *path)
{
	struct stat st;
	if (stat(path, &st) != 0 || !S_ISREG(st.st_mode)) return -1;
	return (int64_t)st.st_size;
}
static int dlp_remove(const char *path) { return unlink(path) == 0 ? 0 : -1; }
static int dlp_mkdir(const char *path) { return (mkdir(path, 0755) == 0 || errno == EEXIST) ? 0 : -1; }
static int dlp_rmdir(const char *path) { return rmdir(path) == 0 ? 0 : -1; }
static int dlp_list_dir(const char *dir, int (*cb)(const char *, int, void *), void *u)
{
	DIR *d = opendir(dir);
	if (!d) return -1;
	struct dirent *e;
	char p[1024];
	while ((e = readdir(d)) != NULL) {
		if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, "..")) continue;
		snprintf(p, sizeof p, "%s/%s", dir, e->d_name);
		struct stat st;
		int is_dir = stat(p, &st) == 0 && S_ISDIR(st.st_mode);
		if (cb(e->d_name, is_dir, u)) break;
	}
	closedir(d);
	return 0;
}

static void dlp_device_info(dlp_device *d)
{
	memset(d, 0, sizeof *d);
	snprintf(d->platform, sizeof d->platform, "mac");
	snprintf(d->model, sizeof d->model, "mac");
	snprintf(d->fw, sizeof d->fw, "posix");
#if defined(__APPLE__)
	vm_statistics64_data_t vs;
	mach_msg_type_number_t cnt = HOST_VM_INFO64_COUNT;
	if (host_statistics64(mach_host_self(), HOST_VM_INFO64, (host_info64_t)&vs, &cnt) == KERN_SUCCESS)
		d->free_kb = (unsigned)(((uint64_t)vs.free_count * (uint64_t)vm_kernel_page_size) / 1024u);
#endif
}

/* ---- worker thread (one at a time). Priority: macOS QoS "utility" (below the app's default); affinity: ignored. */
static pthread_t g_dlp_thr;
static int g_dlp_thr_live;
static __thread int g_dlp_is_worker;   /* set by the worker itself: right from its first line, no race with pthread_create */
static int (*g_dlp_thr_fn)(void *);
static void *g_dlp_thr_arg;
static void *dlp_thread_tramp(void *u)
{
	(void)u;
	g_dlp_is_worker = 1;
#if defined(__APPLE__)
	pthread_set_qos_class_self_np(QOS_CLASS_UTILITY, 0);
#endif
	g_dlp_thr_fn(g_dlp_thr_arg);
	return NULL;
}
static int dlp_thread_start(int (*fn)(void *), void *arg, int prio, unsigned affinity, unsigned stack_bytes)
{
	(void)prio; (void)affinity;
	if (g_dlp_thr_live) return -1;
	g_dlp_thr_fn = fn; g_dlp_thr_arg = arg;
	pthread_attr_t at;
	pthread_attr_init(&at);
	if (stack_bytes) pthread_attr_setstacksize(&at, stack_bytes < 65536 ? 65536 : stack_bytes);
	int r = pthread_create(&g_dlp_thr, &at, dlp_thread_tramp, NULL);
	pthread_attr_destroy(&at);
	if (r != 0) return -1;
	g_dlp_thr_live = 1;
	return 0;
}
static void dlp_thread_join(void)
{
	if (!g_dlp_thr_live) return;
	pthread_join(g_dlp_thr, NULL);
	g_dlp_thr_live = 0;
}
static int dlp_on_worker(void) { return g_dlp_is_worker; }

/* poll() in slices of <= 20 ms so a stop request (devlog_cancel) ends every wait early: > 0 ready, 0 timeout /
 * stopped, < 0 error */
static int dlp_poll1(int s, short ev, unsigned timeout_ms)
{
	uint64_t end = dlp_now_ms() + timeout_ms;
	for (;;) {
		if (dlp_stopping()) return 0;
		uint64_t now = dlp_now_ms();
		if (now >= end) return 0;
		unsigned slice = (unsigned)(end - now) < 2u ? (unsigned)(end - now) : 2u;   /* 2 ms, like the Vita's poll loop */
		struct pollfd p = { s, ev, 0 };
		int r = poll(&p, 1, (int)slice);
		if (r > 0) return r;
		if (r < 0 && errno != EINTR) return -1;
	}
}

/* ---- network */
static int dlp_net_up(uint32_t *ip, uint32_t *mask)
{
	*ip = 0; *mask = 0;
	struct ifaddrs *ifs = NULL;
	if (getifaddrs(&ifs) == 0) {
		for (struct ifaddrs *i = ifs; i; i = i->ifa_next) {
			if (!i->ifa_addr || i->ifa_addr->sa_family != AF_INET || !(i->ifa_flags & IFF_UP) || (i->ifa_flags & IFF_LOOPBACK)
			    || !i->ifa_netmask)
				continue;
			*ip = ((struct sockaddr_in *)i->ifa_addr)->sin_addr.s_addr;
			*mask = ((struct sockaddr_in *)i->ifa_netmask)->sin_addr.s_addr;
			break;
		}
		freeifaddrs(ifs);
	}
	return 1;   /* the Mac always has loopback; broadcast targets are skipped when no LAN address was found */
}
static void dlp_net_down(void) {}

static void dlp_nosigpipe(int s)
{
#ifdef SO_NOSIGPIPE
	int one = 1;
	setsockopt(s, SOL_SOCKET, SO_NOSIGPIPE, &one, sizeof one);
#else
	(void)s;
#endif
}

static int dlp_tcp_connect(uint32_t ip, uint16_t port, unsigned timeout_ms)
{
	int s = socket(AF_INET, SOCK_STREAM, 0);
	if (s < 0) { dlp_err("socket", errno); return -1; }
	dlp_nosigpipe(s);
	/* non-blocking for its whole life: every wait below is a poll() against an explicit deadline */
	fcntl(s, F_SETFL, fcntl(s, F_GETFL, 0) | O_NONBLOCK);
	struct sockaddr_in a;
	memset(&a, 0, sizeof a);
	a.sin_family = AF_INET; a.sin_port = htons(port); a.sin_addr.s_addr = ip;
	int r = connect(s, (struct sockaddr *)&a, sizeof a);
	if (r != 0 && errno != EINPROGRESS) { dlp_err("connect", errno); close(s); return -2; }
	if (r != 0) {
		r = dlp_poll1(s, POLLOUT, timeout_ms);
		if (r <= 0) { dlp_err(dlp_stopping() ? "stopped (devlog_cancel)" : "connect timeout", 0); close(s); return -3; }
		int err = 0; socklen_t el = sizeof err;
		if (getsockopt(s, SOL_SOCKET, SO_ERROR, &err, &el) != 0 || err != 0) { dlp_err("connect", err); close(s); return -4; }
	}
	int one = 1;
	setsockopt(s, IPPROTO_TCP, TCP_NODELAY, &one, sizeof one);
	return s;
}

static int dlp_tcp_send_all(int s, const void *buf, size_t n, unsigned stall_ms, uint64_t deadline_ms)
{
	size_t done = 0;
	uint64_t last = dlp_now_ms();
	while (done < n) {
		ssize_t r = send(s, (const char *)buf + done, n - done, 0);
		if (r > 0) { done += (size_t)r; last = dlp_now_ms(); continue; }
		if (r < 0 && errno == EINTR) continue;
		if (r == 0 || (errno != EAGAIN && errno != EWOULDBLOCK)) { dlp_err("send", r < 0 ? errno : 0); return -1; }
		uint64_t now = dlp_now_ms();
		if (now >= deadline_ms || dlp_stopping()) return -3;
		if (now - last >= stall_ms) { dlp_err("send stalled (no progress)", 0); return -2; }
		uint64_t until = last + stall_ms < deadline_ms ? last + stall_ms : deadline_ms;
		dlp_poll1(s, POLLOUT, (unsigned)(until - now));
	}
	return 0;
}

static int dlp_tcp_recv(int s, void *buf, size_t n, unsigned timeout_ms)
{
	int pr = dlp_poll1(s, POLLIN, timeout_ms);
	if (pr == 0) return -2;
	if (pr < 0) return -1;
	ssize_t r;
	do r = recv(s, buf, n, 0); while (r < 0 && errno == EINTR);
	if (r < 0) return (errno == EAGAIN || errno == EWOULDBLOCK) ? -2 : -1;
	return (int)r;
}

static void dlp_tcp_close(int s) { if (s >= 0) close(s); }

static int dlp_udp_open(void)
{
	int s = socket(AF_INET, SOCK_DGRAM, 0);
	if (s < 0) { dlp_err("socket(udp)", errno); return -1; }
	int one = 1;
	setsockopt(s, SOL_SOCKET, SO_BROADCAST, &one, sizeof one);
	return s;
}

static int dlp_udp_sendto(int s, uint32_t ip, uint16_t port, const void *buf, size_t n)
{
	struct sockaddr_in a;
	memset(&a, 0, sizeof a);
	a.sin_family = AF_INET; a.sin_port = htons(port); a.sin_addr.s_addr = ip;
	if (sendto(s, buf, n, 0, (struct sockaddr *)&a, sizeof a) == (ssize_t)n) return 0;
	dlp_err("sendto", errno);
	return -1;
}

static int dlp_udp_recvfrom(int s, void *buf, size_t n, unsigned timeout_ms, uint32_t *from_ip)
{
	if (dlp_poll1(s, POLLIN, timeout_ms) <= 0) return -1;
	struct sockaddr_in a;
	socklen_t al = sizeof a;
	ssize_t r = recvfrom(s, buf, n, 0, (struct sockaddr *)&a, &al);
	if (r < 0) return -1;
	if (from_ip) *from_ip = a.sin_addr.s_addr;
	return (int)r;
}

static void dlp_udp_close(int s) { if (s >= 0) close(s); }

static int dlp_parse_ipv4(const char *s, uint32_t *ip)
{
	struct in_addr a;
	if (inet_pton(AF_INET, s, &a) != 1) return 0;
	*ip = a.s_addr;
	return 1;
}

static void dlp_format_ipv4(uint32_t ip, char out[16])
{
	struct in_addr a;
	a.s_addr = ip;
	if (!inet_ntop(AF_INET, &a, out, 16)) snprintf(out, 16, "?");
}
