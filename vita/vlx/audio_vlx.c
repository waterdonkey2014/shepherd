/* vita/vlx/audio_vlx.c (device kit #3): a SILENT stand-in for the sceAudioOut / sceAudiodec imports of libshepaudio_vita.a, so the
 * GL + audio hosts (SHPD00010, and kit #3's play app SHPD00097 / 00098) run under vlx next to the recording GL stand-in (glrec_vlx.c,
 * which already answers sceCtrlSetSamplingMode and sceKernelWaitThreadEnd). From the kit #2 review's stand-in (kit2/kit-build2/scripts/
 * fakegl_vlx.c, audio part): the output port takes one grain per sceAudioOutOutput at the hardware's pace (48 kHz, deadline-paced, so
 * the mixer thread keeps real time as on the Vita: the heartbeats' "audio clock ... x real time"), and the AAC decoder hands back zeros
 * of the right size. No sound reaches the Mac: vlx has no audio device and nothing here opens one. Never in a VPK. Build + link (vita/vlx/README.md "GL apps"):
 *   arm-vita-eabi-gcc -O2 -c vita/vlx/audio_vlx.c -o $VLX_OBJ/audio_vlx.o
 *   VLX_EXTRA_OBJS="$VLX_OBJ/glrec_vlx.o $VLX_OBJ/audio_vlx.o <work>/obj/audio_pool.o vita/audio/build/vita/libshepaudio_vita.a" \
 *     VLX_AOT_EXTRA="Shepherd.Engine Shepherd.Engine.Derived" vita/vlx/link.sh menu <work>/obj/host.o <out.elf>
 * plus the owner's audio bake at <root>/ux0/data/shepherd/audiobake (the host's A0 line says whether it found it). */
#include <psp2/kernel/threadmgr.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/audioout.h>
#include <psp2/audiodec.h>
#include <string.h>
#include <stdlib.h>

/* The port drains at 48 kHz: each grain handed over is due one grain after the previous one (a deadline, not a fixed sleep, so the
 * mixing time overlaps the playback as with the hardware's queued buffers; the kit #2 stand-in slept a whole grain AFTER mixing, which
 * read ~0.87x real time under vlx). A caller more than 100 ms late starts a new timeline (an underrun on the device). */
static int g_grain = 512, g_freq = 48000;
static SceUInt64 g_due;
/* kit #3 review, the output thread's error path (vita/audio/src/sa_out_vita.c): VLX_AUDIO_FAIL=<first grain>:<count> in the app's env
 * file makes that many sceAudioOutOutput calls, from that grain on, fail at once (SCE_AUDIO_OUT_ERROR_NOT_OPENED, 0x80260003) as a
 * refusing port would; the heartbeats' "audio clock ... x real time" must stay 1.00x through them and the [audio] lines count them. */
static unsigned g_calls, g_fail_from, g_fail_n;
int sceAudioOutOpenPort (SceAudioOutPortType type, int len, int freq, SceAudioOutMode mode)
{
	g_grain = len; g_freq = freq > 0 ? freq : 48000; g_due = 0;
	const char *f = getenv ("VLX_AUDIO_FAIL");
	if (f && *f) {
		g_fail_from = (unsigned) strtoul (f, NULL, 10);
		const char *c = strchr (f, ':');
		g_fail_n = c ? (unsigned) strtoul (c + 1, NULL, 10) : 1000;
	}
	return 1;
}
int sceAudioOutReleasePort (int port) { return 0; }
int sceAudioOutSetVolume (int port, SceAudioOutChannelFlag ch, int *vol) { return 0; }
int sceAudioOutOutput (int port, const void *buf)
{
	if (!buf)
		return 0;
	unsigned call = ++g_calls;
	if (g_fail_n && call >= g_fail_from && call < g_fail_from + g_fail_n)
		return (int) 0x80260003;
	SceUInt64 now = sceKernelGetProcessTimeWide ();
	if (!g_due || now > g_due + 100000)
		g_due = now;
	g_due += (SceUInt64) g_grain * 1000000ull / (SceUInt64) g_freq;
	if (g_due > now)
		sceKernelDelayThread ((SceUInt) (g_due - now));
	return 0;
}
int sceAudiodecInitLibrary (SceUInt32 codecType, SceAudiodecInitParam *pInitParam) { return 0; }
int sceAudiodecTermLibrary (SceUInt32 codecType) { return 0; }
int sceAudiodecCreateDecoder (SceAudiodecCtrl *ctrl, SceUInt32 codecType)
{
	ctrl->handle = 1;
	ctrl->maxEsSize = SCE_AUDIODEC_AAC_MAX_ES_SIZE;
	ctrl->maxPcmSize = SCE_AUDIODEC_AAC_MAX_SAMPLES * 2 * 2;
	return 0;
}
int sceAudiodecDeleteDecoder (SceAudiodecCtrl *ctrl) { return 0; }
int sceAudiodecDecode (SceAudiodecCtrl *ctrl)
{
	unsigned ch = ctrl->pInfo ? ctrl->pInfo->aac.ch : 2;
	if (!ch)
		ch = 2;
	unsigned bytes = 1024u * 2u * ch;
	if (bytes > ctrl->maxPcmSize)
		bytes = ctrl->maxPcmSize;
	if (ctrl->pPcm)
		memset (ctrl->pPcm, 0, bytes);
	ctrl->outputPcmSize = bytes;
	ctrl->inputEsSize = ctrl->maxEsSize;
	return 0;
}
int sceAudiodecClearContext (SceAudiodecCtrl *ctrl) { return 0; }
