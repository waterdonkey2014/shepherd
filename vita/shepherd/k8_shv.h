/* SHEPHERD-VITA device kit #8, track boot (vita/notes/kit8-boot.md): the .shv clip file -- one of the game's videos, transcoded owner-locally
 * at pack time by vita/tools/splash_pack.py (the owner's own copy of the clip, the local ffmpeg / x264; never in git, never in a VPK) into
 * what the Vita's hardware H.264 decoder (sceAvcdec, k8_avc.h) takes with no parsing on the device:
 *
 *   header (K8SHV_HEADER_SIZE bytes, little endian, checked by a FNV-1a 32 checksum; the index has its own)
 *   index  frames x { u32 offset (from es_off), u32 size }   -- one access unit (= one picture) per frame, in display order
 *   es     the access units, Annex-B (start codes); the first carries the SPS / PPS; no B-frames (decode order = display order)
 *   audio  optional: s16le interleaved PCM (the splash's sound -- for the Devolver logo the game's own FMOD event from the owner's audio
 *          bake, the sound the PC game plays with it)
 *
 * Header + index are checked here (pure C, no libc: the launcher has none); the pictures are checked by the decoder (an access unit it
 * refuses ends the splash). Shared by launcher.c, host.c (kit #8 video) and the Mac tests (mac-check/test_shv.c). */
#ifndef SHEPHERD_K8_SHV_H
#define SHEPHERD_K8_SHV_H

#define K8SHV_MAGIC 0x31564853u      /* "SHV1" */
#define K8SHV_VERSION 1u
#define K8SHV_HEADER_SIZE 192u

typedef struct K8ShvHeader {
	unsigned magic, version, header_size;
	unsigned width, height;              /* coded picture size: multiples of 16 (960 x 544 for the Vita's screen) */
	unsigned rate_num, rate_den;         /* frames per second as a fraction (the clip's own) */
	unsigned frames;
	unsigned index_off, es_off, es_bytes, max_au;
	unsigned audio_off, audio_bytes, audio_rate, audio_channels, audio_frames;
	unsigned src_frames, src_rate_num, src_rate_den;   /* the original clip's frame count and rate (= the length table's row) */
	unsigned flags;                      /* K8SHV_F_* */
	unsigned content_w, content_h, content_x, content_y;   /* where the picture sits inside the coded frame (letterbox bars around it) */
	unsigned src_bytes, src_fnv;         /* the source clip's size and FNV-1a 32 of its first MiB (which file it was made from) */
	char clip [64];                      /* the clip's name (the game's file name without .mp4), NUL-terminated */
	unsigned index_fnv;                  /* FNV-1a 32 of the index (frames x 8 bytes) */
	unsigned reserved [3];
	unsigned checksum;                   /* FNV-1a 32 of the header bytes before this field */
} K8ShvHeader;

#define K8SHV_F_NO_BFRAMES 1u            /* decode order = display order: one access unit in, one picture out */
#define K8SHV_F_BT601 2u                 /* the transcode converted the colours to BT.601 limited range (the decoder's default conversion) */
#define K8SHV_F_AUDIO_BAKE 4u            /* the audio is the owner's FMOD bake of the clip's event (not the clip's own track) */
#define K8SHV_F_DPB1 8u                  /* kit #9: one reference picture -- the SPS's max_dec_frame_buffering 1 (splash_pack.py --ref 1) */

typedef char k8shv_header_size_check [(sizeof (K8ShvHeader) == K8SHV_HEADER_SIZE) ? 1 : -1];

static inline unsigned k8shv_fnv32 (const void *p, unsigned n)
{
	const unsigned char *b = (const unsigned char *) p;
	unsigned h = 2166136261u;
	for (unsigned i = 0; i < n; i++) { h ^= b [i]; h *= 16777619u; }
	return h;
}

/* 0 = a usable clip (header, index and audio bounds all inside len); else a short reason (static text) */
static inline const char *k8shv_check (const void *buf, unsigned len)
{
	if (!buf || len < K8SHV_HEADER_SIZE) return "shorter than the header";
	const K8ShvHeader *h = (const K8ShvHeader *) buf;
	if (h->magic != K8SHV_MAGIC) return "not a .shv file (magic)";
	if (h->version != K8SHV_VERSION || h->header_size != K8SHV_HEADER_SIZE) return "another .shv version";
	if (k8shv_fnv32 (h, (unsigned) ((const char *) &h->checksum - (const char *) h)) != h->checksum) return "header checksum (damaged)";
	if (!h->width || !h->height || (h->width & 15) || (h->height & 15) || h->width > 1920 || h->height > 1088) return "picture size";
	if (!h->rate_num || !h->rate_den || !h->frames || h->frames > 100000) return "frame rate / count";
	if (h->index_off < K8SHV_HEADER_SIZE || h->index_off > len || (len - h->index_off) / 8u < h->frames) return "index outside the file";
	if (h->es_off > len || h->es_bytes > len - h->es_off) return "pictures outside the file";
	if (h->audio_bytes && (h->audio_off > len || h->audio_bytes > len - h->audio_off || !h->audio_rate || h->audio_channels != 2
			       || h->audio_bytes != h->audio_frames * 4u)) return "audio outside the file / not 16-bit stereo";
	if (h->content_x + h->content_w > h->width || h->content_y + h->content_h > h->height) return "picture area";
	if (h->clip [sizeof (h->clip) - 1]) return "clip name";
	const unsigned *ix = (const unsigned *) ((const char *) buf + h->index_off);
	if (k8shv_fnv32 (ix, h->frames * 8u) != h->index_fnv) return "index checksum (damaged)";
	for (unsigned i = 0; i < h->frames; i++) {
		unsigned off = ix [2 * i], sz = ix [2 * i + 1];
		if (!sz || sz > h->max_au || off > h->es_bytes || sz > h->es_bytes - off) return "an index entry outside the pictures";
	}
	return 0;
}
static inline const unsigned char *k8shv_au (const void *buf, unsigned i, unsigned *size)
{
	const K8ShvHeader *h = (const K8ShvHeader *) buf;
	const unsigned *ix = (const unsigned *) ((const char *) buf + h->index_off);
	*size = ix [2 * i + 1];
	return (const unsigned char *) buf + h->es_off + ix [2 * i];
}
#endif
