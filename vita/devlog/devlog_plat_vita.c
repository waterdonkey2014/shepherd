/* devlog platform layer, PS Vita (vitasdk). See devlog_plat.h.
 *
 * Network: SCE_SYSMODULE_NET + sceNetInit + sceNetCtlInit, each only when it is not up already (and undone only
 * when this file brought it up). Every socket is non-blocking (SO_NBIO) and every wait is a short sleep loop
 * against sceKernelGetProcessTimeWide, so no timeout depends on the unit of SO_RCVTIMEO / sceNetEpollWait and a
 * missing server can never hold the caller past its deadline. The sceNet memory pool is a separate USER_RW
 * memblock (not the newlib heap), allocated in dlp_net_up and freed in dlp_net_down: 1 MiB (the vitasdk samples'
 * size) when at least 16 MiB of main memory are free, 512 KiB from 4 MiB, else 256 KiB; a smaller one is tried
 * when the allocation fails. The last raw SCE error (call + code) is kept for the result's why line, so the first
 * device session can tell "no memory / sceNet failed" from "nothing answered".
 * The stubs used here (SceNet, SceNetCtl, SceSysmodule, SceLibKernel, SceSysmem, SceRtc, SceKernelModulemgr,
 * SceIofilemgr) are all in arm-vita-eabi-gcc's default link libraries, so an app needs no build change. */
#include "devlog_plat.h"
#include <psp2/io/dirent.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/modulemgr.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/rng.h>
#include <psp2/kernel/sysmem.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/net/net.h>
#include <psp2/net/netctl.h>
#include <psp2/rtc.h>
#include <psp2/sysmodule.h>
#include <stdio.h>
#include <string.h>

#define DLP_NETCTL_ERROR_NOT_TERMINATED 0x80412102u   /* sceNetCtlInit: already initialised by someone else */
#define DLP_POLL_MS 2

static const char *g_dlp_err_call;
static unsigned g_dlp_err_code;
static unsigned g_dlp_pool_kb;
static void dlp_err(const char *call, int code) { g_dlp_err_call = call; g_dlp_err_code = (unsigned)code; }
static void dlp_err_reset(void) { g_dlp_err_call = NULL; g_dlp_err_code = 0; }
static void dlp_err_text(char *out, size_t n)
{
	if (!n) return;
	if (!g_dlp_err_call) { out[0] = 0; return; }
	if (g_dlp_err_code) snprintf(out, n, "%s 0x%08x", g_dlp_err_call, g_dlp_err_code);
	else snprintf(out, n, "%s", g_dlp_err_call);
}
static unsigned g_dlp_thr_refused;   /* the kernel refused the asked priority / core: the SCE error (first device session) */
static int g_dlp_thr_level;          /* 0 as asked, 1 lowest priority on any user core, 2 the default priority (last resort) */
static void dlp_net_note(char *out, size_t n)
{
	if (!n) return;
	out[0] = 0;
	if (g_dlp_pool_kb) snprintf(out, n, "sceNet pool %u KiB", g_dlp_pool_kb);
	if (g_dlp_thr_refused) {
		size_t k = strlen(out);
		snprintf(out + k, n - k, "%s%s (asked one refused 0x%08x)", k ? "; " : "",
		         g_dlp_thr_level >= 2 ? "worker on the DEFAULT priority 0x10000100, any core" : "worker on priority 191, any core",
		         g_dlp_thr_refused);
	}
}

static uint64_t dlp_now_ms(void) { return (uint64_t)sceKernelGetProcessTimeWide() / 1000u; }
static void dlp_sleep_ms(unsigned ms) { sceKernelDelayThread((SceUInt)ms * 1000u); }

static void dlp_random(void *buf, size_t n)
{
	uint8_t *p = (uint8_t *)buf;
	while (n) {
		size_t k = n > 64 ? 64 : n;   /* sceKernelGetRandomNumber takes at most 64 bytes per call */
		if (sceKernelGetRandomNumber(p, (SceSize)k) < 0) {
			uint64_t t = sceKernelGetProcessTimeWide();     /* never expected; still never all-zero */
			for (size_t i = 0; i < k; i++) { t = t * 6364136223846793005ull + 1442695040888963407ull; p[i] = (uint8_t)(t >> 56); }
		}
		p += k; n -= k;
	}
}

static void dlp_utc(int *y, int *mo, int *d, int *h, int *mi, int *s)
{
	SceDateTime t;
	memset(&t, 0, sizeof t);
	sceRtcGetCurrentClock(&t, 0);   /* time zone offset 0 minutes = UTC */
	*y = t.year; *mo = t.month; *d = t.day; *h = t.hour; *mi = t.minute; *s = t.second;
}

static int dlp_open_read(const char *path) { SceUID fd = sceIoOpen(path, SCE_O_RDONLY, 0); return fd >= 0 ? (int)fd : -1; }
static int dlp_open_write(const char *path, int append)
{
	SceUID fd = sceIoOpen(path, SCE_O_WRONLY | SCE_O_CREAT | (append ? SCE_O_APPEND : SCE_O_TRUNC), 0777);
	return fd >= 0 ? (int)fd : -1;
}
static int dlp_open_update(const char *path) { SceUID fd = sceIoOpen(path, SCE_O_WRONLY, 0); return fd >= 0 ? (int)fd : -1; }
static long dlp_read(int fd, void *buf, size_t n) { int r = sceIoRead(fd, buf, (SceSize)n); return r < 0 ? -1 : r; }
static long dlp_write(int fd, const void *buf, size_t n)
{
	size_t done = 0;
	while (done < n) {
		int r = sceIoWrite(fd, (const char *)buf + done, (SceSize)(n - done));
		if (r <= 0) return -1;
		done += (size_t)r;
	}
	return (long)done;
}
static int dlp_seek(int fd, int64_t off) { return sceIoLseek(fd, (SceOff)off, SCE_SEEK_SET) == (SceOff)off ? 0 : -1; }
static void dlp_close(int fd) { if (fd >= 0) sceIoClose(fd); }
static int64_t dlp_file_size(const char *path)
{
	SceIoStat st;
	memset(&st, 0, sizeof st);
	if (sceIoGetstat(path, &st) < 0 || !SCE_S_ISREG(st.st_mode)) return -1;
	return (int64_t)st.st_size;
}
static int dlp_remove(const char *path) { return sceIoRemove(path) >= 0 ? 0 : -1; }
static int dlp_mkdir(const char *path)
{
	int r = sceIoMkdir(path, 0777);
	if (r >= 0) return 0;
	SceUID d = sceIoDopen(path);    /* exists already? */
	if (d >= 0) { sceIoDclose(d); return 0; }
	return -1;
}
static int dlp_rmdir(const char *path) { return sceIoRmdir(path) >= 0 ? 0 : -1; }
static int dlp_path_exists(const char *path)
{
	SceIoStat st;
	if (sceIoGetstat(path, &st) >= 0) return 1;
	SceUID d = sceIoDopen(path);    /* device roots ("imc0:") do not always stat */
	if (d >= 0) { sceIoDclose(d); return 1; }
	return 0;
}
static int dlp_list_dir(const char *dir, int (*cb)(const char *, int, void *), void *u)
{
	SceUID d = sceIoDopen(dir);
	if (d < 0) return -1;
	SceIoDirent e;
	for (;;) {
		memset(&e, 0, sizeof e);
		if (sceIoDread(d, &e) <= 0) break;
		if (!strcmp(e.d_name, ".") || !strcmp(e.d_name, "..")) continue;
		if (cb(e.d_name, SCE_S_ISDIR(e.d_stat.st_mode) ? 1 : 0, u)) break;
	}
	sceIoDclose(d);
	return 0;
}

static void dlp_device_info(dlp_device *d)
{
	memset(d, 0, sizeof *d);
	snprintf(d->platform, sizeof d->platform, "vita");
	/* model: the console family only (never a per-unit id). PS TV from the kernel; the slim (PCH-2000) and the
	 * PS TV have the internal memory card "imc0:", the PCH-1000 does not. */
	if (sceKernelIsPSVitaTV()) snprintf(d->model, sizeof d->model, "VTE-1000");
	else if (dlp_path_exists("imc0:")) snprintf(d->model, sizeof d->model, "PCH-2000");
	else snprintf(d->model, sizeof d->model, "PCH-1000");
	SceKernelSystemSwVersion v;
	memset(&v, 0, sizeof v);
	v.size = sizeof v;
	if (sceKernelGetSystemSwVersion(&v) >= 0)
		snprintf(d->fw, sizeof d->fw, "%x.%02x", (unsigned)(v.version >> 24), (unsigned)((v.version >> 16) & 0xFF));
	else
		snprintf(d->fw, sizeof d->fw, "unknown");
	SceKernelFreeMemorySizeInfo fi;
	memset(&fi, 0, sizeof fi);
	fi.size = sizeof fi;
	if (sceKernelGetFreeMemorySize(&fi) >= 0) {
		d->free_kb = (unsigned)fi.size_user / 1024u;
		d->free_cdram_kb = (unsigned)fi.size_cdram / 1024u;
		d->free_phycont_kb = (unsigned)fi.size_phycont / 1024u;
	}
}

/* ---- worker thread (one at a time): the sends run here, at a low priority on a core the app leaves spare (the
 * caller passes devlog_app.thread_priority / thread_affinity or the defaults, devlog.c DL_THREAD_*). If the kernel
 * refuses that priority / affinity, the lowest user priority (191) on any user core is tried next, and only then, as
 * the last resort, the default priority (that of the app's main thread); either fallback is named in the next
 * DEVLOG line (dlp_net_note). */
static SceUID g_dlp_thid = -1;
static int (*g_dlp_thr_fn)(void *);
static void *g_dlp_thr_arg;
static int dlp_thread_tramp(SceSize args, void *argp)
{
	(void)args; (void)argp;
	g_dlp_thr_fn(g_dlp_thr_arg);
	return sceKernelExitThread(0);
}
static int dlp_thread_start(int (*fn)(void *), void *arg, int prio, unsigned affinity, unsigned stack_bytes)
{
	if (g_dlp_thid >= 0) return -1;
	g_dlp_thr_fn = fn; g_dlp_thr_arg = arg;
	g_dlp_thr_refused = 0; g_dlp_thr_level = 0;   /* per job: the note describes this job's worker */
	SceUID t = sceKernelCreateThread("shepherd-devlog", dlp_thread_tramp, prio, stack_bytes, 0, (int)affinity, NULL);
	if (t < 0) {
		g_dlp_thr_refused = (unsigned)t;
		dlp_err("sceKernelCreateThread", t);
		g_dlp_thr_level = 1;   /* SCE_KERNEL_LOWEST_PRIORITY_USER, affinity 0 = any user core */
		t = sceKernelCreateThread("shepherd-devlog", dlp_thread_tramp, 191, stack_bytes, 0, 0, NULL);
	}
	if (t < 0) {
		g_dlp_thr_level = 2;   /* last resort: the default user priority (the app's main thread's) */
		t = sceKernelCreateThread("shepherd-devlog", dlp_thread_tramp, 0x10000100, stack_bytes, 0, 0, NULL);
	}
	if (t < 0) return -1;
	g_dlp_thid = t;   /* set before the start: dlp_on_worker must be right from the thread's first instruction */
	int r = sceKernelStartThread(t, 0, NULL);
	if (r < 0) { dlp_err("sceKernelStartThread", r); sceKernelDeleteThread(t); g_dlp_thid = -1; return -1; }
	return 0;
}
static void dlp_thread_join(void)
{
	if (g_dlp_thid < 0) return;
	sceKernelWaitThreadEnd(g_dlp_thid, NULL, NULL);
	sceKernelDeleteThread(g_dlp_thid);
	g_dlp_thid = -1;
}
static int dlp_on_worker(void) { return g_dlp_thid >= 0 && sceKernelGetThreadId() == g_dlp_thid; }

/* ---- network */
static struct {
	int loaded_module, net_inited, ctl_inited;
	SceUID pool_uid;
} g_dlp_net = { 0, 0, 0, -1 };

static void dlp_net_down(void)
{
	if (g_dlp_net.ctl_inited) sceNetCtlTerm();
	if (g_dlp_net.net_inited) sceNetTerm();
	if (g_dlp_net.pool_uid >= 0) sceKernelFreeMemBlock(g_dlp_net.pool_uid);
	if (g_dlp_net.loaded_module) sceSysmoduleUnloadModule(SCE_SYSMODULE_NET);
	g_dlp_net.ctl_inited = g_dlp_net.net_inited = g_dlp_net.loaded_module = 0;
	g_dlp_net.pool_uid = -1;
}

static int dlp_net_up(uint32_t *ip, uint32_t *mask)
{
	*ip = 0; *mask = 0;
	g_dlp_pool_kb = 0;
	int r = sceSysmoduleIsLoaded(SCE_SYSMODULE_NET);
	if (r != SCE_SYSMODULE_LOADED) {
		r = sceSysmoduleLoadModule(SCE_SYSMODULE_NET);
		if (r < 0) { dlp_err("sceSysmoduleLoadModule(NET)", r); return -1; }
		g_dlp_net.loaded_module = 1;
	}
	/* pool: as big as free memory comfortably allows (devlog_end may run right after an app's big allocations) */
	static const unsigned POOL_KB[] = { 1024, 512, 256 };
	unsigned first = 2;
	SceKernelFreeMemorySizeInfo fi;
	memset(&fi, 0, sizeof fi);
	fi.size = sizeof fi;
	if (sceKernelGetFreeMemorySize(&fi) >= 0) first = fi.size_user >= 16 * 1024 * 1024 ? 0 : fi.size_user >= 4 * 1024 * 1024 ? 1 : 2;
	void *pool = NULL;
	for (unsigned k = first; k < 3 && !pool; k++) {
		g_dlp_net.pool_uid = sceKernelAllocMemBlock("shepherd-devlog-net", SCE_KERNEL_MEMBLOCK_TYPE_USER_RW, POOL_KB[k] * 1024u, NULL);
		if (g_dlp_net.pool_uid < 0) { dlp_err("sceKernelAllocMemBlock(net pool)", g_dlp_net.pool_uid); g_dlp_net.pool_uid = -1; continue; }
		if (sceKernelGetMemBlockBase(g_dlp_net.pool_uid, &pool) < 0) { sceKernelFreeMemBlock(g_dlp_net.pool_uid); g_dlp_net.pool_uid = -1; pool = NULL; continue; }
		g_dlp_pool_kb = POOL_KB[k];
	}
	if (!pool) { dlp_net_down(); return -2; }
	SceNetInitParam p;
	p.memory = pool; p.size = (int)(g_dlp_pool_kb * 1024u); p.flags = 0;
	r = sceNetInit(&p);
	if (r >= 0) {
		g_dlp_net.net_inited = 1;
	} else if ((unsigned)r == (unsigned)SCE_NET_ERROR_EBUSY) {   /* the app has sceNet up already: use it, keep it */
		sceKernelFreeMemBlock(g_dlp_net.pool_uid);
		g_dlp_net.pool_uid = -1;
		g_dlp_pool_kb = 0;
	} else {
		dlp_err("sceNetInit", r);
		dlp_net_down();
		return -3;
	}
	r = sceNetCtlInit();
	if (r >= 0) g_dlp_net.ctl_inited = 1;
	else if ((unsigned)r != DLP_NETCTL_ERROR_NOT_TERMINATED) { dlp_err("sceNetCtlInit", r); dlp_net_down(); return -4; }
	int state = 0;
	r = sceNetCtlInetGetState(&state);
	if (r < 0) { dlp_err("sceNetCtlInetGetState", r); return 0; }
	if (state != SCE_NETCTL_STATE_CONNECTED) return 0;   /* no Wi-Fi / no IP */
	SceNetCtlInfo info;
	memset(&info, 0, sizeof info);
	if (sceNetCtlInetGetInfo(SCE_NETCTL_INFO_GET_IP_ADDRESS, &info) >= 0) dlp_parse_ipv4(info.ip_address, ip);
	memset(&info, 0, sizeof info);
	if (sceNetCtlInetGetInfo(SCE_NETCTL_INFO_GET_NETMASK, &info) >= 0) dlp_parse_ipv4(info.netmask, mask);
	return 1;
}

static void dlp_nbio(int s)
{
	int one = 1;
	sceNetSetsockopt(s, SCE_NET_SOL_SOCKET, SCE_NET_SO_NBIO, &one, sizeof one);
}

static int dlp_tcp_connect(uint32_t ip, uint16_t port, unsigned timeout_ms)
{
	int s = sceNetSocket("shepherd-devlog", SCE_NET_AF_INET, SCE_NET_SOCK_STREAM, 0);
	if (s < 0) { dlp_err("sceNetSocket", s); return -1; }
	dlp_nbio(s);
	int one = 1;
	sceNetSetsockopt(s, SCE_NET_IPPROTO_TCP, SCE_NET_TCP_NODELAY, &one, sizeof one);
	SceNetSockaddrIn a;
	memset(&a, 0, sizeof a);
	a.sin_len = sizeof a; a.sin_family = SCE_NET_AF_INET; a.sin_port = sceNetHtons(port); a.sin_addr.s_addr = ip;
	uint64_t deadline = dlp_now_ms() + timeout_ms;
	for (;;) {
		int r = sceNetConnect(s, (const SceNetSockaddr *)&a, sizeof a);
		if (r >= 0 || (unsigned)r == (unsigned)SCE_NET_ERROR_EISCONN) return s;      /* connected */
		if ((unsigned)r != (unsigned)SCE_NET_ERROR_EINPROGRESS && (unsigned)r != (unsigned)SCE_NET_ERROR_EALREADY
		    && (unsigned)r != (unsigned)SCE_NET_ERROR_EINTR) {
			dlp_err("sceNetConnect", r);
			sceNetSocketClose(s);
			return -2;                                                                 /* refused, unreachable, ... */
		}
		if (dlp_now_ms() >= deadline || dlp_stopping()) {
			dlp_err(dlp_stopping() ? "stopped (devlog_cancel)" : "sceNetConnect timeout", 0);
			sceNetSocketClose(s);
			return -3;
		}
		dlp_sleep_ms(DLP_POLL_MS);
	}
}

static int dlp_tcp_send_all(int s, const void *buf, size_t n, unsigned stall_ms, uint64_t deadline_ms)
{
	size_t done = 0;
	uint64_t last = dlp_now_ms();
	while (done < n) {
		int r = sceNetSend(s, (const char *)buf + done, (unsigned)(n - done), 0);
		if (r > 0) { done += (size_t)r; last = dlp_now_ms(); continue; }
		if ((unsigned)r != (unsigned)SCE_NET_ERROR_EAGAIN && (unsigned)r != (unsigned)SCE_NET_ERROR_EINTR) { dlp_err("sceNetSend", r); return -1; }
		uint64_t now = dlp_now_ms();
		if (now >= deadline_ms || dlp_stopping()) return -3;
		if (now - last >= stall_ms) { dlp_err("sceNetSend stalled (no progress)", 0); return -2; }
		dlp_sleep_ms(DLP_POLL_MS);
	}
	return 0;
}

static int dlp_tcp_recv(int s, void *buf, size_t n, unsigned timeout_ms)
{
	uint64_t deadline = dlp_now_ms() + timeout_ms;
	for (;;) {
		int r = sceNetRecv(s, buf, (unsigned)n, 0);
		if (r >= 0) return r;
		if ((unsigned)r != (unsigned)SCE_NET_ERROR_EAGAIN && (unsigned)r != (unsigned)SCE_NET_ERROR_EINTR) { dlp_err("sceNetRecv", r); return -1; }
		if (dlp_now_ms() >= deadline || dlp_stopping()) return -2;
		dlp_sleep_ms(DLP_POLL_MS);
	}
}

static void dlp_tcp_close(int s) { if (s >= 0) sceNetSocketClose(s); }

static int dlp_udp_open(void)
{
	int s = sceNetSocket("shepherd-devlog-disc", SCE_NET_AF_INET, SCE_NET_SOCK_DGRAM, SCE_NET_IPPROTO_UDP);
	if (s < 0) { dlp_err("sceNetSocket(udp)", s); return -1; }
	int one = 1;
	sceNetSetsockopt(s, SCE_NET_SOL_SOCKET, SCE_NET_SO_BROADCAST, &one, sizeof one);
	dlp_nbio(s);
	return s;
}

static int dlp_udp_sendto(int s, uint32_t ip, uint16_t port, const void *buf, size_t n)
{
	SceNetSockaddrIn a;
	memset(&a, 0, sizeof a);
	a.sin_len = sizeof a; a.sin_family = SCE_NET_AF_INET; a.sin_port = sceNetHtons(port); a.sin_addr.s_addr = ip;
	int r = sceNetSendto(s, buf, (unsigned)n, 0, (const SceNetSockaddr *)&a, sizeof a);
	if (r == (int)n) return 0;
	dlp_err("sceNetSendto", r);
	return -1;
}

static int dlp_udp_recvfrom(int s, void *buf, size_t n, unsigned timeout_ms, uint32_t *from_ip)
{
	uint64_t deadline = dlp_now_ms() + timeout_ms;
	for (;;) {
		SceNetSockaddrIn a;
		unsigned al = sizeof a;
		memset(&a, 0, sizeof a);
		int r = sceNetRecvfrom(s, buf, (unsigned)n, 0, (SceNetSockaddr *)&a, &al);
		if (r >= 0) { if (from_ip) *from_ip = a.sin_addr.s_addr; return r; }
		if ((unsigned)r != (unsigned)SCE_NET_ERROR_EAGAIN && (unsigned)r != (unsigned)SCE_NET_ERROR_EINTR) return -1;
		if (dlp_now_ms() >= deadline || dlp_stopping()) return -2;
		dlp_sleep_ms(DLP_POLL_MS);
	}
}

static void dlp_udp_close(int s) { if (s >= 0) sceNetSocketClose(s); }

static int dlp_parse_ipv4(const char *s, uint32_t *ip)
{
	/* parsed here, not with sceNetInetPton: it is also needed before sceNet is up (config parsing) */
	unsigned v[4], k = 0;
	const char *p = s;
	for (k = 0; k < 4; k++) {
		if (*p < '0' || *p > '9') return 0;
		unsigned x = 0, nd = 0;
		while (*p >= '0' && *p <= '9' && nd < 4) { x = x * 10 + (unsigned)(*p - '0'); p++; nd++; }
		if (nd == 0 || nd > 3 || x > 255) return 0;
		v[k] = x;
		if (k < 3) { if (*p != '.') return 0; p++; }
	}
	if (*p) return 0;
	uint8_t b[4] = { (uint8_t)v[0], (uint8_t)v[1], (uint8_t)v[2], (uint8_t)v[3] };
	memcpy(ip, b, 4);   /* network order = the bytes as written */
	return 1;
}

static void dlp_format_ipv4(uint32_t ip, char out[16])
{
	uint8_t b[4];
	memcpy(b, &ip, 4);
	snprintf(out, 16, "%u.%u.%u.%u", b[0], b[1], b[2], b[3]);
}
