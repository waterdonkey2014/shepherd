/*
 * SHEPHERD-VITA: libSystem.Security.Cryptography.Native.OpenSsl for the PS Vita (static, linked into the eboot).
 *
 * The linux-arm System.Security.Cryptography the Vita runs binds the OpenSSL shim's CryptoNative_* names. There is no
 * OpenSSL on the Vita, so this file answers them with vita_crypto.c (AES, SHA-1/2, MD5, HMAC, PBKDF2; written from the
 * standards) and keeps the shim's contract (src/native/libs/System.Security.Cryptography.Native, MIT):
 *   - EVP_MD / EVP_CIPHER pointers are static descriptors here; contexts are malloc'd structs (SafeHandles free them);
 *   - EVP cipher update/final buffer exactly like OpenSSL's EVP_CipherUpdate/EVP_CipherFinal_ex (block buffering, the
 *     decrypt hold-back of the last block when padding is on, PKCS#7 checks, CFB8/CFB128 as stream modes);
 *   - 1 = success / 0 = failure, with an error code the managed side reads through CryptoNative_ErrGetExceptionError.
 * What the menu boot uses (stage A census): AES-CBC decrypt (settings.json / persistence via CryptoStream), the IV RNG,
 * SHA1 (Rewired's keyboard-map hash). Everything else is a complete implementation of the same families or reports
 * "unsupported" (GCM/CCM/ChaCha20, DES/RC2, SHA-3/SHAKE, keys, X509, TLS: stubs, see pal_vita_stubs.c).
 * Error queue: one global last-error slot (the OpenSSL queue is per thread; here a racing thread can at worst replace
 * the code of a concurrent failure, which only changes an exception message).
 */
#include "crypto/vita_crypto.h"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

void shepherd_vita_native_log (const char *msg);
void shepherd_vita_crypto_rng (uint8_t *buf, int32_t len);   /* pal_vita.c (sceKernelGetRandomNumber); the Mac tests supply their own */

/* ---------------------------------------------------------------- errors */
/* Codes look like OpenSSL's packed (lib << 23 | reason): lib 6 = EVP, reason 65 = ERR_R_MALLOC_FAILURE (isAllocFailure) */
#define VERR(reason) ((uint64_t) ((6u << 23) | (reason)))
enum {
	VR_MALLOC = 65,
	VR_UNSUPPORTED = 300,
	VR_BAD_ARG = 301,
	VR_KEY_LENGTH = 302,
	VR_NO_KEY = 303,
	VR_DATA_NOT_MULTIPLE = 304,
	VR_WRONG_FINAL_BLOCK_LENGTH = 305,
	VR_BAD_DECRYPT = 306,
};
static volatile uint64_t g_err;

static void set_err (int reason) { g_err = VERR (reason); }

/* for the generated stubs (pal_vita_stubs.c): an unsupported CryptoNative_* entry point reports through the error slot */
void shepherd_vita_crypto_unsupported (void) { set_err (VR_UNSUPPORTED); }

static const char *reason_text (uint64_t e)
{
	switch ((int) (e & 0x7fffff)) {
	case VR_MALLOC: return "malloc failure";
	case VR_UNSUPPORTED: return "not supported by the PS Vita crypto shim";
	case VR_BAD_ARG: return "invalid argument";
	case VR_KEY_LENGTH: return "invalid key length";
	case VR_NO_KEY: return "no key set";
	case VR_DATA_NOT_MULTIPLE: return "data not multiple of block length";
	case VR_WRONG_FINAL_BLOCK_LENGTH: return "wrong final block length";
	case VR_BAD_DECRYPT: return "bad decrypt";
	default: return "unknown error";
	}
}

uint64_t CryptoNative_ErrClearError (void) { g_err = 0; return 0; }
uint64_t CryptoNative_ErrPeekError (void) { return g_err; }
uint64_t CryptoNative_ErrPeekLastError (void) { return g_err; }

uint64_t CryptoNative_ErrGetExceptionError (int32_t *isAllocFailure)
{
	uint64_t e = g_err;
	if (isAllocFailure)
		*isAllocFailure = (e & 0x7fffff) == VR_MALLOC;
	g_err = 0;
	return e;
}

const char *CryptoNative_ErrReasonErrorString (uint64_t error)
{
	return error ? reason_text (error) : NULL;
}

void CryptoNative_ErrErrorStringN (uint64_t e, char *buf, int32_t len)
{
	if (buf && len > 0)
		snprintf (buf, (size_t) len, "error:%08lX:PS Vita crypto::%s", (unsigned long) e, reason_text (e));
}

int32_t CryptoNative_EnsureOpenSslInitialized (void) { return 0; }
/* "is libcrypto loadable?": no. The algorithms that need real OpenSSL objects (RSA, ECDSA, ECDH, DSA, AesGcm/AesCcm,
 * ChaCha20Poly1305, X509, TLS) then throw PlatformNotSupportedException up front; AES/SHA/HMAC/PBKDF2/RNG never ask. */
int32_t CryptoNative_OpenSslAvailable (void) { return 0; }
void CryptoNative_EnsureLibSslInitialized (void) { }
int64_t CryptoNative_OpenSslVersionNumber (void) { return 0x30000000L; }   /* reported as OpenSSL 3.0 API level */

static void log_once (volatile int *done, const char *what)
{
	if (!*done) {
		*done = 1;
		char b [200];
		snprintf (b, sizeof (b), "crypto: %s is not supported on the PS Vita (returns NULL / failure)", what);
		shepherd_vita_native_log (b);
	}
}

/* ---------------------------------------------------------------- RNG */
int32_t CryptoNative_GetRandomBytes (uint8_t *buf, int32_t num)
{
	if (num < 0 || (!buf && num))
		return 0;
	shepherd_vita_crypto_rng (buf, num);
	return 1;
}

/* ---------------------------------------------------------------- digests */
typedef struct { const vc_hash *h; } VitaMd;   /* the EVP_MD */
static const VitaMd md_md5 = { &vc_md5 }, md_sha1 = { &vc_sha1 }, md_sha256 = { &vc_sha256 }, md_sha384 = { &vc_sha384 }, md_sha512 = { &vc_sha512 };

const void *CryptoNative_EvpMd5 (void) { return &md_md5; }
const void *CryptoNative_EvpSha1 (void) { return &md_sha1; }
const void *CryptoNative_EvpSha256 (void) { return &md_sha256; }
const void *CryptoNative_EvpSha384 (void) { return &md_sha384; }
const void *CryptoNative_EvpSha512 (void) { return &md_sha512; }
/* NULL = "not available": SHA3_*.IsSupported / Shake*.IsSupported report false, as with an OpenSSL without SHA-3 */
const void *CryptoNative_EvpSha3_256 (void) { return NULL; }
const void *CryptoNative_EvpSha3_384 (void) { return NULL; }
const void *CryptoNative_EvpSha3_512 (void) { return NULL; }
const void *CryptoNative_EvpShake128 (void) { return NULL; }
const void *CryptoNative_EvpShake256 (void) { return NULL; }

static const vc_hash *md_of (const void *md)
{
	if (md == &md_md5 || md == &md_sha1 || md == &md_sha256 || md == &md_sha384 || md == &md_sha512)
		return ((const VitaMd *) md)->h;
	return NULL;
}

int32_t CryptoNative_GetMaxMdSize (void) { return VC_MAX_DIGEST; }

int32_t CryptoNative_EvpMdSize (const void *md)
{
	const vc_hash *h = md_of (md);
	return h ? h->digest_len : 0;
}

typedef struct {
	const vc_hash *h;
	vc_hash_ctx c;
} VitaMdCtx;   /* the EVP_MD_CTX */

void *CryptoNative_EvpMdCtxCreate (const void *type)
{
	g_err = 0;
	const vc_hash *h = md_of (type);
	if (!h) {
		set_err (VR_UNSUPPORTED);
		return NULL;
	}
	VitaMdCtx *x = (VitaMdCtx *) malloc (sizeof (VitaMdCtx));
	if (!x) {
		set_err (VR_MALLOC);
		return NULL;
	}
	x->h = h;
	h->init (&x->c);
	return x;
}

void CryptoNative_EvpMdCtxDestroy (void *ctx)
{
	if (ctx) {
		memset (ctx, 0, sizeof (VitaMdCtx));
		free (ctx);
	}
}

void *CryptoNative_EvpMdCtxCopyEx (const void *ctx)
{
	g_err = 0;
	if (!ctx) {
		set_err (VR_BAD_ARG);
		return NULL;
	}
	VitaMdCtx *x = (VitaMdCtx *) malloc (sizeof (VitaMdCtx));
	if (!x) {
		set_err (VR_MALLOC);
		return NULL;
	}
	memcpy (x, ctx, sizeof (VitaMdCtx));
	return x;
}

int32_t CryptoNative_EvpDigestReset (void *ctx, const void *type)
{
	g_err = 0;
	VitaMdCtx *x = (VitaMdCtx *) ctx;
	const vc_hash *h = type ? md_of (type) : (x ? x->h : NULL);
	if (!x || !h) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	x->h = h;
	h->init (&x->c);
	return 1;
}

int32_t CryptoNative_EvpDigestUpdate (void *ctx, const void *d, int32_t cnt)
{
	g_err = 0;
	VitaMdCtx *x = (VitaMdCtx *) ctx;
	if (!x || cnt < 0 || (!d && cnt)) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	if (cnt)
		x->h->update (&x->c, (const uint8_t *) d, (size_t) cnt);
	return 1;
}

int32_t CryptoNative_EvpDigestFinalEx (void *ctx, uint8_t *md, uint32_t *s)
{
	g_err = 0;
	VitaMdCtx *x = (VitaMdCtx *) ctx;
	if (!x || !md) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	x->h->final (&x->c, md);
	x->h->init (&x->c);   /* OpenSSL wants a re-Init after Final; LiteHash always resets, this makes reuse safe too */
	if (s)
		*s = (uint32_t) x->h->digest_len;
	return 1;
}

int32_t CryptoNative_EvpDigestCurrent (const void *ctx, uint8_t *md, uint32_t *s)
{
	g_err = 0;
	const VitaMdCtx *x = (const VitaMdCtx *) ctx;
	if (!x || !md) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	vc_hash_ctx dup = x->c;
	x->h->final (&dup, md);
	if (s)
		*s = (uint32_t) x->h->digest_len;
	return 1;
}

int32_t CryptoNative_EvpDigestOneShot (const void *type, const void *source, int32_t sourceSize, uint8_t *md, uint32_t *mdSize)
{
	g_err = 0;
	const vc_hash *h = md_of (type);
	if (!h || sourceSize < 0 || !md || !mdSize || (!source && sourceSize)) {
		set_err (h ? VR_BAD_ARG : VR_UNSUPPORTED);
		return 0;
	}
	vc_hash_oneshot (h, source, (size_t) sourceSize, md);
	*mdSize = (uint32_t) h->digest_len;
	return 1;
}

/* XOF (SHAKE): no XOF digest exists here (EvpShake* return NULL), so these are never reached with a valid context */
int32_t CryptoNative_EvpDigestFinalXOF (void *ctx, uint8_t *md, uint32_t len) { set_err (VR_UNSUPPORTED); return 0; }
int32_t CryptoNative_EvpDigestCurrentXOF (const void *ctx, uint8_t *md, uint32_t len) { set_err (VR_UNSUPPORTED); return 0; }
int32_t CryptoNative_EvpDigestXOFOneShot (const void *type, const void *source, int32_t sourceSize, uint8_t *md, uint32_t len) { set_err (VR_UNSUPPORTED); return 0; }

int32_t CryptoNative_Pbkdf2 (const char *password, int32_t passwordLength, const unsigned char *salt, int32_t saltLength,
			     int32_t iterations, const void *digest, unsigned char *destination, int32_t destinationLength)
{
	const vc_hash *h = md_of (digest);
	if (passwordLength < 0 || saltLength < 0 || iterations <= 0 || !h || !destination || destinationLength < 0)
		return -1;
	g_err = 0;
	if ((!salt && saltLength) || (!password && passwordLength))
		return -1;
	vc_pbkdf2 (h, (const uint8_t *) (password ? password : ""), (size_t) passwordLength,
		   salt ? salt : (const unsigned char *) "", (size_t) saltLength, (uint32_t) iterations, destination, (size_t) destinationLength);
	return 1;
}

/* ---------------------------------------------------------------- HMAC */
void *CryptoNative_HmacCreate (const uint8_t *key, int32_t keyLen, const void *md)
{
	g_err = 0;
	const vc_hash *h = md_of (md);
	if (!h || keyLen < 0 || (!key && keyLen)) {
		set_err (h ? VR_BAD_ARG : VR_UNSUPPORTED);
		return NULL;
	}
	vc_hmac *m = (vc_hmac *) malloc (sizeof (vc_hmac));
	if (!m) {
		set_err (VR_MALLOC);
		return NULL;
	}
	vc_hmac_init (m, h, key, (size_t) keyLen);
	return m;
}

void CryptoNative_HmacDestroy (void *ctx)
{
	if (ctx) {
		memset (ctx, 0, sizeof (vc_hmac));
		free (ctx);
	}
}

void *CryptoNative_HmacCopy (const void *ctx)
{
	g_err = 0;
	if (!ctx) {
		set_err (VR_BAD_ARG);
		return NULL;
	}
	vc_hmac *m = (vc_hmac *) malloc (sizeof (vc_hmac));
	if (!m) {
		set_err (VR_MALLOC);
		return NULL;
	}
	memcpy (m, ctx, sizeof (vc_hmac));
	return m;
}

int32_t CryptoNative_HmacReset (void *ctx)
{
	g_err = 0;
	if (!ctx) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	vc_hmac_reset ((vc_hmac *) ctx);
	return 1;
}

int32_t CryptoNative_HmacUpdate (void *ctx, const uint8_t *data, int32_t len)
{
	g_err = 0;
	if (!ctx || len < 0 || (!data && len)) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	if (len)
		vc_hmac_update ((vc_hmac *) ctx, data, (size_t) len);
	return 1;
}

/* *len: in = the buffer size, out = the MAC size (like HMAC_Final, which writes the whole MAC) */
int32_t CryptoNative_HmacFinal (void *ctx, uint8_t *md, int32_t *len)
{
	g_err = 0;
	vc_hmac *m = (vc_hmac *) ctx;
	if (!m || !len || *len < 0 || !md) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	vc_hmac_final (m, md);
	*len = m->h->digest_len;
	return 1;
}

int32_t CryptoNative_HmacCurrent (const void *ctx, uint8_t *md, int32_t *len)
{
	g_err = 0;
	const vc_hmac *m = (const vc_hmac *) ctx;
	if (!m || !len || *len < 0 || !md) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	vc_hmac dup = *m;
	vc_hmac_final (&dup, md);
	*len = m->h->digest_len;
	memset (&dup, 0, sizeof (dup));
	return 1;
}

int32_t CryptoNative_HmacOneShot (const void *type, const uint8_t *key, int32_t keySize, const uint8_t *source, int32_t sourceSize,
				  uint8_t *md, int32_t *mdSize)
{
	g_err = 0;
	const vc_hash *h = md_of (type);
	if (!key && keySize != 0)
		return -1;
	if (!h || !md || !mdSize || keySize < 0 || sourceSize < 0 || (!source && sourceSize)) {
		set_err (h ? VR_BAD_ARG : VR_UNSUPPORTED);
		return 0;
	}
	vc_hmac m;
	vc_hmac_init (&m, h, key, (size_t) keySize);
	if (sourceSize)
		vc_hmac_update (&m, source, (size_t) sourceSize);
	vc_hmac_final (&m, md);
	*mdSize = h->digest_len;
	memset (&m, 0, sizeof (m));
	return 1;
}

/* ---------------------------------------------------------------- ciphers */
enum { M_ECB = 1, M_CBC, M_CFB8, M_CFB128 };
typedef struct {
	int key_bytes;
	int mode;
	int block;      /* EVP_CIPHER_block_size: 16 for ECB/CBC, 1 for the CFB stream modes */
	int iv_len;     /* 0 for ECB */
	const char *name;
} VitaCipher;      /* the EVP_CIPHER */

#define CIPHERS(X) \
	X (128, Ecb, M_ECB, 16, 0) X (128, Cbc, M_CBC, 16, 16) X (128, Cfb8, M_CFB8, 1, 16) X (128, Cfb128, M_CFB128, 1, 16) \
	X (192, Ecb, M_ECB, 16, 0) X (192, Cbc, M_CBC, 16, 16) X (192, Cfb8, M_CFB8, 1, 16) X (192, Cfb128, M_CFB128, 1, 16) \
	X (256, Ecb, M_ECB, 16, 0) X (256, Cbc, M_CBC, 16, 16) X (256, Cfb8, M_CFB8, 1, 16) X (256, Cfb128, M_CFB128, 1, 16)
#define DEF_CIPHER(bits, Mode, mode, block, iv) \
	static const VitaCipher c_aes##bits##Mode = { bits / 8, mode, block, iv, "AES-" #bits "-" #Mode }; \
	const void *CryptoNative_EvpAes##bits##Mode (void) { return &c_aes##bits##Mode; }
CIPHERS (DEF_CIPHER)
#define LIST_CIPHER(bits, Mode, mode, block, iv) &c_aes##bits##Mode,
static const VitaCipher *const all_ciphers [] = { CIPHERS (LIST_CIPHER) NULL };

/* AEAD and legacy ciphers: not available (NULL algorithm -> the managed caller throws CryptographicException) */
#define NO_CIPHER(name) \
	const void *CryptoNative_Evp##name (void) { static volatile int d; log_once (&d, "CryptoNative_Evp" #name); return NULL; }
NO_CIPHER (Aes128Gcm) NO_CIPHER (Aes192Gcm) NO_CIPHER (Aes256Gcm)
NO_CIPHER (Aes128Ccm) NO_CIPHER (Aes192Ccm) NO_CIPHER (Aes256Ccm)
NO_CIPHER (DesEcb) NO_CIPHER (DesCbc) NO_CIPHER (DesCfb8)
NO_CIPHER (Des3Ecb) NO_CIPHER (Des3Cbc) NO_CIPHER (Des3Cfb8) NO_CIPHER (Des3Cfb64)
NO_CIPHER (RC2Ecb) NO_CIPHER (RC2Cbc) NO_CIPHER (ChaCha20Poly1305)

static int is_cipher (const void *p)
{
	for (int i = 0; all_ciphers [i]; ++i)
		if (all_ciphers [i] == p)
			return 1;
	return 0;
}

typedef struct {
	const VitaCipher *c;
	int enc;            /* 1 encrypt, 0 decrypt */
	int padding;        /* EVP default: 1 */
	int key_set;
	vc_aes_key key;
	uint8_t iv [16];    /* CBC chaining value / CFB shift register */
	uint8_t buf [16];   /* partial input block */
	int buf_len;
	uint8_t final [16]; /* decrypt + padding: the held-back last block */
	int final_used;
	int num;            /* CFB128: bytes of the current keystream block already used */
} VitaCipherCtx;       /* the EVP_CIPHER_CTX */

static void ctx_restart (VitaCipherCtx *x, const uint8_t *iv)
{
	if (iv && x->c->iv_len)
		memcpy (x->iv, iv, (size_t) x->c->iv_len);
	x->buf_len = 0;
	x->final_used = 0;
	x->num = 0;
}

/* EVP_CipherInit_ex (ctx, NULL, NULL, key, iv, enc): key/iv may be NULL (kept), enc -1 = keep */
static int ctx_init (VitaCipherCtx *x, const uint8_t *key, const uint8_t *iv, int enc)
{
	if (enc != -1)
		x->enc = enc ? 1 : 0;
	if (key) {
		if (vc_aes_set_key (&x->key, key, x->c->key_bytes * 8) != 0) {
			set_err (VR_KEY_LENGTH);
			return 0;
		}
		x->key_set = 1;
	}
	ctx_restart (x, iv);
	return 1;
}

static VitaCipherCtx *ctx_new (const void *type)
{
	if (!type || !is_cipher (type)) {
		set_err (VR_UNSUPPORTED);
		return NULL;
	}
	VitaCipherCtx *x = (VitaCipherCtx *) calloc (1, sizeof (VitaCipherCtx));
	if (!x) {
		set_err (VR_MALLOC);
		return NULL;
	}
	x->c = (const VitaCipher *) type;
	x->padding = 1;
	return x;
}

void *CryptoNative_EvpCipherCreate2 (const void *type, uint8_t *key, int32_t keyLength, unsigned char *iv, int32_t enc)
{
	g_err = 0;
	VitaCipherCtx *x = ctx_new (type);
	if (!x)
		return NULL;
	/* EVP_CIPHER_CTX_set_key_length (keyLength / 8): AES key lengths are fixed per cipher */
	if (keyLength > 0 && keyLength / 8 != x->c->key_bytes) {
		set_err (VR_KEY_LENGTH);
		free (x);
		return NULL;
	}
	if (!ctx_init (x, key, iv, enc)) {
		free (x);
		return NULL;
	}
	return x;
}

void *CryptoNative_EvpCipherCreatePartial (const void *type)
{
	g_err = 0;
	return ctx_new (type);
}

int32_t CryptoNative_EvpCipherSetKeyAndIV (void *ctx, uint8_t *key, unsigned char *iv, int32_t enc)
{
	g_err = 0;
	if (!ctx) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	return ctx_init ((VitaCipherCtx *) ctx, key, iv, enc);
}

void CryptoNative_EvpCipherDestroy (void *ctx)
{
	if (ctx) {
		memset (ctx, 0, sizeof (VitaCipherCtx));
		free (ctx);
	}
}

int32_t CryptoNative_EvpCipherReset (void *ctx, uint8_t *pIv, int32_t cIv)
{
	g_err = 0;
	if (!ctx || cIv < 0 || (!pIv && cIv)) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	return ctx_init ((VitaCipherCtx *) ctx, NULL, pIv, -1);
}

int32_t CryptoNative_EvpCipherCtxSetPadding (void *x, int32_t padding)
{
	if (!x)
		return 0;
	((VitaCipherCtx *) x)->padding = padding ? 1 : 0;
	return 1;
}

/* one full block in ECB / CBC */
static void do_block (VitaCipherCtx *x, const uint8_t *in, uint8_t *out)
{
	uint8_t t [16];
	if (x->c->mode == M_ECB) {
		if (x->enc)
			vc_aes_encrypt_block (&x->key, in, out);
		else
			vc_aes_decrypt_block (&x->key, in, out);
		return;
	}
	if (x->enc) {
		for (int i = 0; i < 16; ++i)
			t [i] = (uint8_t) (in [i] ^ x->iv [i]);
		vc_aes_encrypt_block (&x->key, t, out);
		memcpy (x->iv, out, 16);
	} else {
		uint8_t c [16];
		memcpy (c, in, 16);   /* in and out may alias */
		vc_aes_decrypt_block (&x->key, c, t);
		for (int i = 0; i < 16; ++i)
			out [i] = (uint8_t) (t [i] ^ x->iv [i]);
		memcpy (x->iv, c, 16);
	}
}

static void do_stream (VitaCipherCtx *x, const uint8_t *in, uint8_t *out, int len)
{
	uint8_t ks [16];
	if (x->c->mode == M_CFB8) {
		for (int i = 0; i < len; ++i) {
			vc_aes_encrypt_block (&x->key, x->iv, ks);
			uint8_t ib = in [i];
			uint8_t ob = (uint8_t) (ib ^ ks [0]);
			out [i] = ob;
			memmove (x->iv, x->iv + 1, 15);
			x->iv [15] = x->enc ? ob : ib;   /* the ciphertext byte is shifted in */
		}
		return;
	}
	/* CFB128: iv holds the last ciphertext block; encrypt it once per 16 bytes */
	for (int i = 0; i < len; ++i) {
		if (x->num == 0)
			vc_aes_encrypt_block (&x->key, x->iv, x->final);   /* final [] doubles as the keystream block here */
		uint8_t ib = in [i];
		uint8_t ob = (uint8_t) (ib ^ x->final [x->num]);
		out [i] = ob;
		x->iv [x->num] = x->enc ? ob : ib;
		x->num = (x->num + 1) & 15;
	}
}

/* EVP_EncryptUpdate's block buffering (used for encrypt, and decrypt without padding) */
static int update_blocks (VitaCipherCtx *x, uint8_t *out, int *outl, const uint8_t *in, int inl)
{
	int written = 0;
	if (x->buf_len) {
		int take = 16 - x->buf_len < inl ? 16 - x->buf_len : inl;
		memcpy (x->buf + x->buf_len, in, (size_t) take);
		x->buf_len += take;
		in += take;
		inl -= take;
		if (x->buf_len < 16) {
			*outl = 0;
			return 1;
		}
		do_block (x, x->buf, out);
		out += 16;
		written += 16;
		x->buf_len = 0;
	}
	while (inl >= 16) {
		do_block (x, in, out);
		in += 16;
		out += 16;
		inl -= 16;
		written += 16;
	}
	if (inl) {
		memcpy (x->buf, in, (size_t) inl);
		x->buf_len = inl;
	}
	*outl = written;
	return 1;
}

int32_t CryptoNative_EvpCipherUpdate (void *ctx, uint8_t *out, int32_t *outl, unsigned char *in, int32_t inl)
{
	g_err = 0;
	VitaCipherCtx *x = (VitaCipherCtx *) ctx;
	if (!x || inl < 0 || (!in && inl) || !outl) {
		/* (EvpCipherSetInputLength passes NULL buffers: an AEAD (CCM) call, not supported here) */
		set_err (x ? VR_BAD_ARG : VR_BAD_ARG);
		return 0;
	}
	if (!x->key_set) {
		set_err (VR_NO_KEY);
		return 0;
	}
	if (inl == 0) {
		*outl = 0;
		return 1;
	}
	if (x->c->block == 1) {
		do_stream (x, in, out, inl);
		*outl = inl;
		return 1;
	}
	if (x->enc || !x->padding)
		return update_blocks (x, out, outl, in, inl);
	/* EVP_DecryptUpdate with padding: emit the block held back by the previous call first, then keep back the last full
	 * block of this call (Final strips the padding from it) */
	int fix = 0;
	if (x->final_used) {
		memcpy (out, x->final, 16);
		out += 16;
		fix = 1;
	}
	int n = 0;
	update_blocks (x, out, &n, in, inl);
	if (x->buf_len == 0 && n >= 16) {
		n -= 16;
		x->final_used = 1;
		memcpy (x->final, out + n, 16);
	} else {
		x->final_used = 0;
	}
	*outl = n + (fix ? 16 : 0);
	return 1;
}

int32_t CryptoNative_EvpCipherFinalEx (void *ctx, uint8_t *outm, int32_t *outl)
{
	g_err = 0;
	VitaCipherCtx *x = (VitaCipherCtx *) ctx;
	if (!x || !outl) {
		set_err (VR_BAD_ARG);
		return 0;
	}
	*outl = 0;
	if (!x->key_set) {
		set_err (VR_NO_KEY);
		return 0;
	}
	if (x->c->block == 1)
		return 1;
	if (x->enc) {
		if (!x->padding) {
			if (x->buf_len) {
				set_err (VR_DATA_NOT_MULTIPLE);
				return 0;
			}
			return 1;
		}
		uint8_t n = (uint8_t) (16 - x->buf_len);
		memset (x->buf + x->buf_len, n, n);
		do_block (x, x->buf, outm);
		x->buf_len = 0;
		*outl = 16;
		return 1;
	}
	if (!x->padding) {
		if (x->buf_len) {
			set_err (VR_DATA_NOT_MULTIPLE);
			return 0;
		}
		return 1;
	}
	if (x->buf_len || !x->final_used) {
		set_err (VR_WRONG_FINAL_BLOCK_LENGTH);
		return 0;
	}
	uint8_t n = x->final [15];
	if (n == 0 || n > 16) {
		set_err (VR_BAD_DECRYPT);
		return 0;
	}
	for (int i = 16 - n; i < 16; ++i)
		if (x->final [i] != n) {
			set_err (VR_BAD_DECRYPT);
			return 0;
		}
	memcpy (outm, x->final, (size_t) (16 - n));
	x->final_used = 0;
	*outl = 16 - n;
	return 1;
}

/* AEAD tag/nonce controls: no AEAD cipher exists here */
#define NO_AEAD(name, args) int32_t CryptoNative_EvpCipher##name args { set_err (VR_UNSUPPORTED); return 0; }
NO_AEAD (SetGcmNonceLength, (void *ctx, int32_t ivLength))
NO_AEAD (SetCcmNonceLength, (void *ctx, int32_t ivLength))
NO_AEAD (GetGcmTag, (void *ctx, uint8_t *tag, int32_t tagLength))
NO_AEAD (SetGcmTag, (void *ctx, uint8_t *tag, int32_t tagLength))
NO_AEAD (GetCcmTag, (void *ctx, uint8_t *tag, int32_t tagLength))
NO_AEAD (SetCcmTag, (void *ctx, uint8_t *tag, int32_t tagLength))
NO_AEAD (GetAeadTag, (void *ctx, uint8_t *tag, int32_t tagLength))
NO_AEAD (SetAeadTag, (void *ctx, uint8_t *tag, int32_t tagLength))
