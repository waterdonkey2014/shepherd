/* Mac test build: AAC-LC (ADTS) decode through AudioToolbox's AudioConverter. Decoding only: no AudioUnit,
 * no AudioQueue, no output device is ever touched (silence rule). */
#include "sa_internal.h"
#include <AudioToolbox/AudioToolbox.h>

typedef struct apple_dec {
    AudioConverterRef conv;
    int channels, rate;
    const uint8_t *pkt; UInt32 pkt_len; int fed;
    AudioStreamPacketDescription desc;
} apple_dec;

#define SA_NO_MORE ((OSStatus)0x6E6D6F72) /* 'nmor' */

static OSStatus in_proc(AudioConverterRef c, UInt32 *ioPackets, AudioBufferList *io, AudioStreamPacketDescription **outDesc, void *user)
{
    apple_dec *d = (apple_dec *)user; (void)c;
    if (d->fed || !d->pkt) { *ioPackets = 0; return SA_NO_MORE; }
    io->mNumberBuffers = 1;
    io->mBuffers[0].mData = (void *)d->pkt;
    io->mBuffers[0].mDataByteSize = d->pkt_len;
    io->mBuffers[0].mNumberChannels = (UInt32)d->channels;
    d->desc.mStartOffset = 0; d->desc.mVariableFramesInPacket = 0; d->desc.mDataByteSize = d->pkt_len;
    if (outDesc) *outDesc = &d->desc;
    *ioPackets = 1;
    d->fed = 1;
    return noErr;
}

static int a_init(int n) { (void)n; return 0; }
static void a_term(void) {}

static void *a_open(int channels, int rate)
{
    apple_dec *d = (apple_dec *)calloc(1, sizeof *d);
    if (!d) return NULL;
    AudioStreamBasicDescription in = { 0 }, out = { 0 };
    in.mSampleRate = rate; in.mFormatID = kAudioFormatMPEG4AAC; in.mFormatFlags = kMPEG4Object_AAC_LC;
    in.mFramesPerPacket = 1024; in.mChannelsPerFrame = (UInt32)channels;
    out.mSampleRate = rate; out.mFormatID = kAudioFormatLinearPCM;
    out.mFormatFlags = kLinearPCMFormatFlagIsSignedInteger | kLinearPCMFormatFlagIsPacked;
    out.mBitsPerChannel = 16; out.mChannelsPerFrame = (UInt32)channels; out.mFramesPerPacket = 1;
    out.mBytesPerFrame = 2u * (UInt32)channels; out.mBytesPerPacket = out.mBytesPerFrame;
    if (AudioConverterNew(&in, &out, &d->conv) != noErr) { free(d); return NULL; }
    d->channels = channels; d->rate = rate;
    return d;
}

static int a_decode(void *dv, const uint8_t *frame, int len, int16_t *pcm, int max_frames)
{
    apple_dec *d = (apple_dec *)dv;
    sa_adts h;
    if (sa_adts_parse(frame, len, &h) != 1 || h.frame_len > len) return -1;
    d->pkt = frame + h.header_len; d->pkt_len = (UInt32)(h.frame_len - h.header_len); d->fed = 0;
    int total = 0;
    for (int guard = 0; guard < 4 && total < max_frames; guard++) {
        AudioBufferList abl; abl.mNumberBuffers = 1;
        abl.mBuffers[0].mNumberChannels = (UInt32)d->channels;
        abl.mBuffers[0].mDataByteSize = (UInt32)((max_frames - total) * 2 * d->channels);
        abl.mBuffers[0].mData = pcm + (size_t)total * d->channels;
        UInt32 frames = (UInt32)(max_frames - total);
        OSStatus st = AudioConverterFillComplexBuffer(d->conv, in_proc, d, &frames, &abl, NULL);
        total += (int)frames;
        if (st == SA_NO_MORE || frames == 0) break;
        if (st != noErr) return total ? total : -1;
    }
    d->pkt = NULL;
    return total;
}
static void a_reset(void *dv) { apple_dec *d = (apple_dec *)dv; AudioConverterReset(d->conv); }
static void a_close(void *dv) { apple_dec *d = (apple_dec *)dv; if (!d) return; AudioConverterDispose(d->conv); free(d); }

static const sa_aac_vtbl apple_vtbl = { "AudioToolbox AudioConverter", a_init, a_term, a_open, a_decode, a_reset, a_close };
const sa_aac_vtbl *sa_platform_aac(void) { return &apple_vtbl; }
