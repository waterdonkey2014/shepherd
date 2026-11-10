/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the handshake between the TINY LAUNCHER (vita/shepherd/launcher.c, the
 * ~100 KiB eboot.bin) and the ENGINE MODULE (app0:module/shepherd.suprx: host.c + the Mono runtime + every AOT image, the code that was the
 * whole eboot until kit #7; vita/shepherd/k8_module.c is its entry).
 *
 * The launcher starts first (the Vita reads only its small eboot), puts the Devolver splash on the screen through the hardware video
 * decoder at once (k8_avc.h, the owner-local clip data/video/<clip>.shv made by vita/tools/splash_pack.py), and meanwhile loads the module
 * with sceKernelLoadStartModule on its main thread. The module's module_start does nothing but hand back its entry point (it may run on any
 * thread the kernel picks); the launcher then calls that entry on its own main thread (the stack the eboot's sceUserMainThreadStackSize
 * gave it, as kit #7's host main had), which starts newlib and runs host.c's main as before. The host reads this block for the boot numbers
 * and, before it sizes its memory (the H2 partition), calls release (): that waits for the splash to end -- the clip's own 6.27 s -- and
 * gives back everything the launcher held (its CDRAM: the clip, the decoder, the two frame buffers; its threads; its audio port), so the
 * host partitions exactly the memory kit #7's host saw (only the launcher's own ~100 KiB eboot more). The launcher uses no newlib (only
 * Sce* imports): two newlib copies would share the same thread-local slot (0x89) on the main thread.
 *
 * Plain C, no libc types; the same header on both sides (the struct is laid out by the same compiler). */
#ifndef SHEPHERD_K8_LAUNCH_H
#define SHEPHERD_K8_LAUNCH_H

#define K8_LAUNCH_MAGIC 0x4B384C31u   /* "K8L1" */
#define K8_LAUNCH_VERSION 1u

typedef struct K8Launch K8Launch;
typedef int (*K8ModuleEntry) (const K8Launch *launch);

struct K8Launch {
	unsigned magic, version, size;          /* K8_LAUNCH_MAGIC, K8_LAUNCH_VERSION, sizeof (K8Launch) */
	K8ModuleEntry entry;                    /* written by the module's module_start */
	unsigned long long t_start;             /* process time (us) when the launcher's main began */
	unsigned long long t_first_frame;       /* process time of the splash's first frame on the display (0: no splash) */
	unsigned long long t_load_begin, t_load_end;   /* sceKernelLoadStartModule began / returned */
	unsigned long long t_splash_end;        /* the clip's last frame was shown (0: not yet / no splash) */
	unsigned long long t_released;          /* release () gave the launcher's memory back */
	int splash_state;                       /* 0 = off (SHEPHERD_K8_SPLASH=0), 1 = playing, 2 = ended, -1 = could not start (splash_why) */
	unsigned frames_shown, frames_total, frames_late;   /* the clip's frames put on the display / in the clip / shown a vsync late */
	unsigned decode_us_max, decode_us_total;            /* hardware decode time per frame (max, sum) */
	int audio_state;                        /* 0 = off, 1 = played, -1 = could not (audio_why) */
	unsigned cdram_kib;                     /* CDRAM the splash held (all given back at release) */
	int module_uid, module_status;          /* sceKernelLoadStartModule's result and module_start's */
	char splash_why [160], audio_why [96];
	char clip [72];                         /* the clip's name (the .shv header's) */
	char log [1536];                        /* the launcher's own lines ('\n' separated): the host copies them into its log as "K8 L ..." */
	/* waits until the splash has ended (at most wait_ms_max), then gives back every launcher resource; returns the ms it waited.
	 * Called once, by the host's main thread, before the partition. Safe when there is no splash (returns 0 at once). */
	int (*release) (void);
	unsigned wait_ms_max;                   /* release's cap (default 15000: a decoder that hangs must not stop the boot) */
	/* device kit #9, track device (appended: kit #8's fields keep their places; the launcher and the module are built together) */
	int k9_delay;                           /* 1 = the splash tolerates the decoder's output delay (SHEPHERD_K9_AVC_DELAY=0: kit #8 -- no picture = stop) */
	unsigned out_delay;                     /* access units the decoder took before its first picture (0 = a picture per access unit) */
	unsigned fed, drained;                  /* access units given to the decoder; pictures returned by sceAvcdecDecodeStop after the last one */
	/* device kit #10, track boot (appended): the drain (k8_avc.h "THE DRAIN") */
	int k10_flush;                          /* K10_FLUSH_* the splash used (SHEPHERD_K10_AVC_FLUSH; 0 = kit #9's end-of-stream call) */
	unsigned refed, flushed;                /* flush access units fed (the clip's own again), pictures the flush brought out (refeed + stop1) */
	int flush_rc;                           /* the first refused flush call (0 = none) */
	int k10_fallback;                       /* 1 = the refeed was refused and stop1 was tried; 2 = the refeed's guard was set (runtime/k10-avc-refeed.off: stop1) */
	/* kit #10 review fixes (appended): the second copy's identity (finding 5) */
	int k10_tag;                            /* 1 = the flush access units carried their own time stamp (SHEPHERD_K10_AVC_TAG=0: none) */
	unsigned second;                        /* pictures of the clip's SECOND copy that came out (never shown: the splash ended there) */
	/* kit #10 integration review (appended): the time-stamp check made provable (k8_avc.h K8Avc.pts_seen / pts_bad / flush_ok) */
	unsigned pts_seen, pts_bad, flush_ok;
	/* device kit #12, track boot (appended; vita/notes/kit12-boot.md section 2): THE BOOT SEQUENCE THE OWNER'S WAY (SHEPHERD_K12_BOOTSEQ=1) --
	 * the Massive Monster clip (data/video/MassiveMonster.shv: the game's own logo animation as Shepherd draws it, with its pre-roll sound)
	 * right after the Devolver splash, on the same decoder path; Cross skips the clip on the screen (SHEPHERD_K12_XSKIP=0: no skip) */
	int k12_seq;                            /* 1 = the boot sequence's clips were queued in this start */
	int k12_clip2_state;                    /* 0 = none, 1 = playing (the launcher's wait ended during it: k12_clip2_why), 2 = played to its end, 3 = skipped (Cross), -1 = could not play (k12_clip2_why) */
	unsigned k12_clip2_shown, k12_clip2_total, k12_clip2_late;
	int k12_skip1;                          /* 1 = Cross skipped the Devolver splash, at picture k12_skip1_at */
	unsigned k12_skip1_at, k12_skip2_at;
	unsigned long long k12_t_clip2_first, k12_t_clip2_end;
	char k12_clip2 [64];                    /* the second clip's name (its .shv header's) */
	char k12_clip2_why [128];
};

#endif
