/* DIAG-ONLY (devfail1, "Shepherd Menu Diag" SHPD00016): the progress counters the menu diag host's heartbeat reports. Written by the
 * generated counting wrappers (gen_diag_wraps.py: ld --wrap thunks for every P/Invoke target and AOT direct icall, C wrappers for the
 * file P/Invokes and sceKernelCreateThread) and by host.c's diag hooks; read, without locks, by the heartbeat thread. Plain words,
 * not atomics: approximate counts are enough to see whether anything still moves. */
#ifndef SHP_DIAG_H
#define SHP_DIAG_H
#define SHP_DIAG_MAX_THREADS 48
extern volatile unsigned shp_diag_pinv_calls, shp_diag_icall_calls;
extern volatile int shp_diag_pinv_last, shp_diag_icall_last;
extern const char *const shp_diag_pinv_names [];
extern const int shp_diag_pinv_count;
extern const char *const shp_diag_icall_names [];
extern const int shp_diag_icall_count;
extern volatile unsigned shp_diag_files_opened, shp_diag_open_failed, shp_diag_reads, shp_diag_writes;
extern volatile unsigned long long shp_diag_bytes_read, shp_diag_bytes_written;
extern char shp_diag_last_path [96];
extern volatile unsigned shp_diag_threads_created;
extern char shp_diag_last_thread [32];
extern volatile int shp_diag_thread_uids [SHP_DIAG_MAX_THREADS];
#endif
