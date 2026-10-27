/* SHEPHERD-VITA: Mac unit test of the Vita crypto shim (system-native/crypto/vita_crypto.c + pal_vita_crypto.c).
 * 1. Known-answer vectors: FIPS 197 App. A.1/C (AES), NIST SP 800-38A F.1/F.2/F.3 (ECB, CBC, CFB8, CFB128), FIPS 180-4
 *    examples (SHA-1/256/384/512 incl. the 1,000,000 x 'a' messages), RFC 1321 (MD5), RFC 2202 / RFC 4231 (HMAC),
 *    RFC 6070 + the common PBKDF2-HMAC-SHA256 vectors.
 * 2. Differential tests against macOS CommonCrypto (an independent implementation) on random keys, IVs, lengths and
 *    random chunkings of Update calls, through the CryptoNative_* entry points exactly as the managed BCL calls them
 *    (OpenSslCipherLite: padding 0; plus OpenSSL's own padding mode), and the error paths. Exit 0 = all pass. */
#include <CommonCrypto/CommonCrypto.h>
#include <CommonCrypto/CommonHMAC.h>
#include <CommonCrypto/CommonKeyDerivation.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "vita_crypto.h"

/* ---- what the Vita host / shim provide */
void shepherd_vita_native_log (const char *msg) { printf ("     (log) %s\n", msg); }
void shepherd_vita_crypto_rng (uint8_t *buf, int32_t len) { arc4random_buf (buf, (size_t) len); }

/* ---- the CryptoNative_* surface under test */
const void *CryptoNative_EvpMd5 (void); const void *CryptoNative_EvpSha1 (void); const void *CryptoNative_EvpSha256 (void);
const void *CryptoNative_EvpSha384 (void); const void *CryptoNative_EvpSha512 (void); const void *CryptoNative_EvpSha3_256 (void);
void *CryptoNative_EvpMdCtxCreate (const void *type); void CryptoNative_EvpMdCtxDestroy (void *ctx);
void *CryptoNative_EvpMdCtxCopyEx (const void *ctx);
int32_t CryptoNative_EvpDigestReset (void *ctx, const void *type); int32_t CryptoNative_EvpDigestUpdate (void *ctx, const void *d, int32_t cnt);
int32_t CryptoNative_EvpDigestFinalEx (void *ctx, uint8_t *md, uint32_t *s); int32_t CryptoNative_EvpDigestCurrent (const void *ctx, uint8_t *md, uint32_t *s);
int32_t CryptoNative_EvpDigestOneShot (const void *type, const void *source, int32_t sourceSize, uint8_t *md, uint32_t *mdSize);
int32_t CryptoNative_EvpMdSize (const void *md); int32_t CryptoNative_GetMaxMdSize (void);
void *CryptoNative_HmacCreate (const uint8_t *key, int32_t keyLen, const void *md); void CryptoNative_HmacDestroy (void *ctx);
void *CryptoNative_HmacCopy (const void *ctx); int32_t CryptoNative_HmacReset (void *ctx);
int32_t CryptoNative_HmacUpdate (void *ctx, const uint8_t *data, int32_t len); int32_t CryptoNative_HmacFinal (void *ctx, uint8_t *md, int32_t *len);
int32_t CryptoNative_HmacCurrent (const void *ctx, uint8_t *md, int32_t *len);
int32_t CryptoNative_HmacOneShot (const void *type, const uint8_t *key, int32_t keySize, const uint8_t *source, int32_t sourceSize, uint8_t *md, int32_t *mdSize);
int32_t CryptoNative_Pbkdf2 (const char *password, int32_t passwordLength, const unsigned char *salt, int32_t saltLength, int32_t iterations, const void *digest, unsigned char *destination, int32_t destinationLength);
const void *CryptoNative_EvpAes128Ecb (void); const void *CryptoNative_EvpAes128Cbc (void); const void *CryptoNative_EvpAes128Cfb8 (void); const void *CryptoNative_EvpAes128Cfb128 (void);
const void *CryptoNative_EvpAes192Ecb (void); const void *CryptoNative_EvpAes192Cbc (void); const void *CryptoNative_EvpAes192Cfb8 (void); const void *CryptoNative_EvpAes192Cfb128 (void);
const void *CryptoNative_EvpAes256Ecb (void); const void *CryptoNative_EvpAes256Cbc (void); const void *CryptoNative_EvpAes256Cfb8 (void); const void *CryptoNative_EvpAes256Cfb128 (void);
const void *CryptoNative_EvpAes128Gcm (void);
void *CryptoNative_EvpCipherCreate2 (const void *type, uint8_t *key, int32_t keyLength, unsigned char *iv, int32_t enc);
void CryptoNative_EvpCipherDestroy (void *ctx); int32_t CryptoNative_EvpCipherReset (void *ctx, uint8_t *pIv, int32_t cIv);
int32_t CryptoNative_EvpCipherCtxSetPadding (void *x, int32_t padding);
int32_t CryptoNative_EvpCipherUpdate (void *ctx, uint8_t *out, int32_t *outl, unsigned char *in, int32_t inl);
int32_t CryptoNative_EvpCipherFinalEx (void *ctx, uint8_t *outm, int32_t *outl);
int32_t CryptoNative_EvpCipherSetKeyAndIV (void *ctx, uint8_t *key, unsigned char *iv, int32_t enc);
uint64_t CryptoNative_ErrGetExceptionError (int32_t *isAllocFailure); void CryptoNative_ErrErrorStringN (uint64_t e, char *buf, int32_t len);
uint64_t CryptoNative_ErrPeekError (void); int32_t CryptoNative_GetRandomBytes (uint8_t *buf, int32_t num);
int32_t CryptoNative_EnsureOpenSslInitialized (void);

static int g_fail, g_checks;
#define CHECK(cond, ...) do { g_checks++; if (!(cond)) { g_fail++; printf ("FAIL %s:%d: ", __FILE__, __LINE__); printf (__VA_ARGS__); printf ("\n"); } } while (0)

static size_t unhex (const char *h, uint8_t *out)
{
	size_t n = 0;
	for (; h [0] && h [1]; h += 2) {
		unsigned v;
		sscanf (h, "%2x", &v);
		out [n++] = (uint8_t) v;
	}
	return n;
}

static int eqhex (const uint8_t *b, size_t n, const char *hex)
{
	uint8_t want [256];
	size_t wn = unhex (hex, want);
	return wn == n && !memcmp (b, want, n);
}

static void hexs (const uint8_t *b, size_t n, char *out)
{
	for (size_t i = 0; i < n; ++i)
		sprintf (out + 2 * i, "%02x", b [i]);
	out [2 * n] = 0;
}

/* ================================================================ 1. known answers */
static void kat_aes (void)
{
	uint8_t key [32], pt [16], ct [16], back [16];
	vc_aes_key k;
	unhex ("00112233445566778899aabbccddeeff", pt);
	unhex ("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f", key);
	static const struct { int bits; const char *ct; } c [] = {
		{ 128, "69c4e0d86a7b0430d8cdb78070b4c55a" }, { 192, "dda97ca4864cdfe06eaf70a0ec0d7191" }, { 256, "8ea2b7ca516745bfeafc49904b496089" } };
	for (int i = 0; i < 3; ++i) {
		CHECK (vc_aes_set_key (&k, key, c [i].bits) == 0, "aes key %d", c [i].bits);
		vc_aes_encrypt_block (&k, pt, ct);
		CHECK (eqhex (ct, 16, c [i].ct), "FIPS 197 C AES-%d encrypt", c [i].bits);
		vc_aes_decrypt_block (&k, ct, back);
		CHECK (!memcmp (back, pt, 16), "FIPS 197 C AES-%d decrypt", c [i].bits);
	}
	/* FIPS 197 A.1: the last round-key word of the 2b7e1516... key expansion */
	unhex ("2b7e151628aed2a6abf7158809cf4f3c", key);
	vc_aes_set_key (&k, key, 128);
	CHECK (eqhex (k.rk + 172, 4, "b6630ca6"), "FIPS 197 A.1 w[43]");
	CHECK (vc_aes_set_key (&k, key, 100) == -1, "bad key size refused");
}

typedef struct { const void *(*alg) (void); const char *key, *iv, *pt, *ct; const char *name; } CipherKat;
static void kat_modes (void)
{
	static const char *pt4 = "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e5130c81c46a35ce411e5fbc1191a0a52eff69f2445df4f9b17ad2b417be66c3710";
	static const CipherKat k [] = {
		{ CryptoNative_EvpAes128Ecb, "2b7e151628aed2a6abf7158809cf4f3c", "", NULL,
		  "3ad77bb40d7a3660a89ecaf32466ef97f5d3d58503b9699de785895a96fdbaaf43b1cd7f598ece23881b00e3ed0306887b0c785e27e8ad3f8223207104725dd4", "SP 800-38A F.1.1 ECB-AES128" },
		{ CryptoNative_EvpAes128Cbc, "2b7e151628aed2a6abf7158809cf4f3c", "000102030405060708090a0b0c0d0e0f", NULL,
		  "7649abac8119b246cee98e9b12e9197d5086cb9b507219ee95db113a917678b273bed6b8e3c1743b7116e69e222295163ff1caa1681fac09120eca307586e1a7", "SP 800-38A F.2.1 CBC-AES128" },
		{ CryptoNative_EvpAes256Cbc, "603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4", "000102030405060708090a0b0c0d0e0f", NULL,
		  "f58c4c04d6e5f1ba779eabfb5f7bfbd69cfc4e967edb808d679f777bc6702c7d39f23369a9d9bacfa530e26304231461b2eb05e2c39be9fcda6c19078c6a9d1b", "SP 800-38A F.2.5 CBC-AES256" },
		{ CryptoNative_EvpAes128Cfb128, "2b7e151628aed2a6abf7158809cf4f3c", "000102030405060708090a0b0c0d0e0f", NULL,
		  "3b3fd92eb72dad20333449f8e83cfb4ac8a64537a0b3a93fcde3cdad9f1ce58b26751f67a3cbb140b1808cf187a4f4dfc04b05357c5d1c0eeac4c66f9ff7f2e6", "SP 800-38A F.3.13 CFB128-AES128" },
		{ CryptoNative_EvpAes128Cfb8, "2b7e151628aed2a6abf7158809cf4f3c", "000102030405060708090a0b0c0d0e0f", "6bc1bee22e409f96e93d7e117393172aae2d",
		  "3b79424c9c0dd436bace9e0ed4586a4f32b9", "SP 800-38A F.3.7 CFB8-AES128" },
	};
	for (size_t i = 0; i < sizeof (k) / sizeof (k [0]); ++i) {
		uint8_t key [32], iv [16], pt [64], ct [80], out [80];
		size_t kl = unhex (k [i].key, key), pl = unhex (k [i].pt ? k [i].pt : pt4, pt);
		unhex (k [i].iv, iv);
		for (int enc = 1; enc >= 0; --enc) {
			void *c = CryptoNative_EvpCipherCreate2 (k [i].alg (), key, (int32_t) kl * 8, iv, enc);
			CHECK (c != NULL, "%s create", k [i].name);
			CHECK (CryptoNative_EvpCipherCtxSetPadding (c, 0) == 1, "padding 0");
			int32_t n = 0, f = 0;
			uint8_t in [80];
			if (enc) memcpy (in, pt, pl); else unhex (k [i].ct, in);
			CHECK (CryptoNative_EvpCipherUpdate (c, out, &n, in, (int32_t) pl) == 1, "%s update", k [i].name);
			CHECK (CryptoNative_EvpCipherFinalEx (c, out + n, &f) == 1, "%s final", k [i].name);
			if (enc) CHECK (n + f == (int32_t) pl && eqhex (out, pl, k [i].ct), "%s encrypt", k [i].name);
			else CHECK (n + f == (int32_t) pl && !memcmp (out, pt, pl), "%s decrypt", k [i].name);
			CryptoNative_EvpCipherDestroy (c);
		}
		(void) ct;
	}
}

typedef struct { const vc_hash *h; const char *msg; long repeat; const char *digest; } HashKat;
static void kat_hash (void)
{
	static const char *m448 = "abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq";
	static const char *m896 = "abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmnoijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu";
	const HashKat k [] = {
		{ &vc_md5, "", 1, "d41d8cd98f00b204e9800998ecf8427e" },
		{ &vc_md5, "abc", 1, "900150983cd24fb0d6963f7d28e17f72" },
		{ &vc_md5, "message digest", 1, "f96b697d7cb7938d525a2f31aaf161d0" },
		{ &vc_md5, "1234567890", 8, "57edf4a22be3c955ac49da2e2107b67a" },
		{ &vc_sha1, "", 1, "da39a3ee5e6b4b0d3255bfef95601890afd80709" },
		{ &vc_sha1, "abc", 1, "a9993e364706816aba3e25717850c26c9cd0d89d" },
		{ &vc_sha1, m448, 1, "84983e441c3bd26ebaae4aa1f95129e5e54670f1" },
		{ &vc_sha1, "a", 1000000, "34aa973cd4c4daa4f61eeb2bdbad27316534016f" },
		{ &vc_sha256, "", 1, "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" },
		{ &vc_sha256, "abc", 1, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" },
		{ &vc_sha256, m448, 1, "248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1" },
		{ &vc_sha256, "a", 1000000, "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0" },
		{ &vc_sha384, "abc", 1, "cb00753f45a35e8bb5a03d699ac65007272c32ab0eded1631a8b605a43ff5bed8086072ba1e7cc2358baeca134c825a7" },
		{ &vc_sha384, m896, 1, "09330c33f71147e83d192fc782cd1b4753111b173b3b05d22fa08086e3b0f712fcc7c71a557e2db966c3e9fa91746039" },
		{ &vc_sha512, "", 1, "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce47d0d13c5d85f2b0ff8318d2877eec2f63b931bd47417a81a538327af927da3e" },
		{ &vc_sha512, "abc", 1, "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f" },
		{ &vc_sha512, m896, 1, "8e959b75dae313da8cf4f72814fc143f8f7779c6eb9f7fa17299aeadb6889018501d289e4900f7e4331b99dec4b5433ac7d329eeb6dd26545e96e55b874be909" },
	};
	for (size_t i = 0; i < sizeof (k) / sizeof (k [0]); ++i) {
		vc_hash_ctx c;
		uint8_t d [64];
		k [i].h->init (&c);
		for (long r = 0; r < k [i].repeat; ++r)
			k [i].h->update (&c, (const uint8_t *) k [i].msg, strlen (k [i].msg));
		k [i].h->final (&c, d);
		char got [130];
		hexs (d, (size_t) k [i].h->digest_len, got);
		CHECK (eqhex (d, (size_t) k [i].h->digest_len, k [i].digest), "%s(\"%.20s\" x%ld) = %s", k [i].h->name, k [i].msg, k [i].repeat, got);
	}
}

static void kat_hmac_pbkdf2 (void)
{
	uint8_t key [200], out [64];
	vc_hmac m;
	struct { const vc_hash *h; const char *keyhex; int keyrep; const char *data; const char *mac; } k [] = {
		{ &vc_md5, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", 0, "Hi There", "9294727a3638bb1c13f48ef8158bfc9d" },          /* RFC 2202 */
		{ &vc_sha1, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", 0, "Hi There", "b617318655057264e28bc0b6fb378c8ef146be00" },
		{ &vc_sha1, "4a656665", 0, "what do ya want for nothing?", "effcdf6ae5eb2fa2d27416d5f184df9c259a7c79" },
		{ &vc_sha256, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", 0, "Hi There", "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7" },   /* RFC 4231 */
		{ &vc_sha256, "4a656665", 0, "what do ya want for nothing?", "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843" },
		{ &vc_sha256, "aa", 131, "Test Using Larger Than Block-Size Key - Hash Key First", "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54" },
		{ &vc_sha384, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", 0, "Hi There", "afd03944d84895626b0825f4ab46907f15f9dadbe4101ec682aa034c7cebc59cfaea9ea9076ede7f4af152e8b2fa9cb6" },
		{ &vc_sha512, "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b", 0, "Hi There", "87aa7cdea5ef619d4ff0b4241a1d6cb02379f4e2ce4ec2787ad0b30545e17cdedaa833b7d6b8a702038b274eaea3f4e4be9d914eeb61f1702e696c203a126854" },
	};
	for (size_t i = 0; i < sizeof (k) / sizeof (k [0]); ++i) {
		size_t kl = unhex (k [i].keyhex, key);
		if (k [i].keyrep) { memset (key, key [0], (size_t) k [i].keyrep); kl = (size_t) k [i].keyrep; }
		vc_hmac_init (&m, k [i].h, key, kl);
		vc_hmac_update (&m, (const uint8_t *) k [i].data, strlen (k [i].data));
		vc_hmac_final (&m, out);
		CHECK (eqhex (out, (size_t) k [i].h->digest_len, k [i].mac), "HMAC-%s case %zu", k [i].h->name, i);
		/* final resets to the keyed state: the same MAC again */
		vc_hmac_update (&m, (const uint8_t *) k [i].data, strlen (k [i].data));
		vc_hmac_final (&m, out);
		CHECK (eqhex (out, (size_t) k [i].h->digest_len, k [i].mac), "HMAC-%s reuse after final", k [i].h->name);
	}
	struct { const vc_hash *h; uint32_t c; size_t len; const char *dk; } p [] = {
		{ &vc_sha1, 1, 20, "0c60c80f961f0e71f3a9b524af6012062fe037a6" },     /* RFC 6070 */
		{ &vc_sha1, 2, 20, "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957" },
		{ &vc_sha1, 4096, 20, "4b007901b765489abead49d926f721d065a429c1" },
		{ &vc_sha256, 1, 32, "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b" },
		{ &vc_sha256, 2, 32, "ae4d0c95af6b46d32d0adff928f06dd02a303f8ef3c251dfd6e2d85a95474c43" },
		{ &vc_sha256, 4096, 32, "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a" },
	};
	for (size_t i = 0; i < sizeof (p) / sizeof (p [0]); ++i) {
		vc_pbkdf2 (p [i].h, (const uint8_t *) "password", 8, (const uint8_t *) "salt", 4, p [i].c, out, p [i].len);
		CHECK (eqhex (out, p [i].len, p [i].dk), "PBKDF2-HMAC-%s c=%u", p [i].h->name, p [i].c);
	}
	/* RFC 6070 case 5: longer password/salt, 25-byte output (two blocks) */
	vc_pbkdf2 (&vc_sha1, (const uint8_t *) "passwordPASSWORDpassword", 24, (const uint8_t *) "saltSALTsaltSALTsaltSALTsaltSALTsalt", 36, 4096, out, 25);
	CHECK (eqhex (out, 25, "3d2eec4fe41c849b80c8d83662c0e44a8b291a964cf2f07038"), "PBKDF2-HMAC-SHA1 RFC 6070 #5");
}

/* ================================================================ 2. differential, through CryptoNative_* */
static uint32_t rnd (uint32_t n) { return n ? arc4random_uniform (n) : 0; }

static const void *(*const ciphers [3][4]) (void) = {
	{ CryptoNative_EvpAes128Ecb, CryptoNative_EvpAes128Cbc, CryptoNative_EvpAes128Cfb8, CryptoNative_EvpAes128Cfb128 },
	{ CryptoNative_EvpAes192Ecb, CryptoNative_EvpAes192Cbc, CryptoNative_EvpAes192Cfb8, CryptoNative_EvpAes192Cfb128 },
	{ CryptoNative_EvpAes256Ecb, CryptoNative_EvpAes256Cbc, CryptoNative_EvpAes256Cfb8, CryptoNative_EvpAes256Cfb128 },
};
static const CCMode cc_modes [4] = { kCCModeECB, kCCModeCBC, kCCModeCFB8, kCCModeCFB };
static const char *mode_names [4] = { "ECB", "CBC", "CFB8", "CFB128" };

static int cc_crypt (int enc, int mode, int pad, const uint8_t *key, size_t kl, const uint8_t *iv, const uint8_t *in, size_t n, uint8_t *out, size_t *outn)
{
	CCCryptorRef r;
	if (CCCryptorCreateWithMode (enc ? kCCEncrypt : kCCDecrypt, cc_modes [mode], kCCAlgorithmAES, pad ? ccPKCS7Padding : ccNoPadding,
				     mode ? iv : NULL, key, kl, NULL, 0, 0, 0, &r) != kCCSuccess)
		return 0;
	size_t a = 0, b = 0;
	int ok = CCCryptorUpdate (r, in, n, out, n + 32, &a) == kCCSuccess && CCCryptorFinal (r, out + a, n + 32 - a, &b) == kCCSuccess;
	CCCryptorRelease (r);
	*outn = a + b;
	return ok;
}

/* feed `in` in random chunks; returns total written, or -1 on a failed call */
static int vita_crypt (void *c, const uint8_t *in, int n, uint8_t *out, int inplace, int blockmode)
{
	int done = 0, w = 0;
	uint8_t buf [4200], *dest = out;
	if (inplace) { memcpy (buf, in, (size_t) n); in = buf; }
	while (done < n) {
		int take = 1 + (int) rnd ((uint32_t) (n - done));
		if (rnd (4) == 0) take = n - done;
		if (inplace && blockmode) take = (take + 15) & ~15;   /* in place (OpenSslCipherLite, offset 0): whole blocks */
		int32_t o = -7;
		if (CryptoNative_EvpCipherUpdate (c, inplace ? buf + done : out + w, &o, (unsigned char *) in + done, take) != 1)
			return -1;
		if (inplace && o != take) return -2;   /* in-place only makes sense for length-preserving calls */
		w += o;
		done += take;
	}
	int32_t f = -7;
	if (CryptoNative_EvpCipherFinalEx (c, (inplace ? buf : out) + w, &f) != 1)
		return -1;
	if (inplace) memcpy (dest, buf, (size_t) (w + f));
	return w + f;
}

static void diff_ciphers (void)
{
	uint8_t key [32], iv [16], pt [4100], a [4200], b [4200], back [4200];
	int runs = 0;
	for (int ks = 0; ks < 3; ++ks)
		for (int mode = 0; mode < 4; ++mode)
			for (int pad = 0; pad < 2; ++pad)
				for (int it = 0; it < 120; ++it) {
					size_t kl = 16 + 8 * (size_t) ks;
					int blockmode = mode < 2;
					if (pad && !blockmode) continue;
					int n = (int) rnd (4096);
					if (blockmode && !pad) n &= ~15;
					arc4random_buf (key, sizeof key); arc4random_buf (iv, sizeof iv); arc4random_buf (pt, (size_t) n);
					size_t cn;
					CHECK (cc_crypt (1, mode, pad, key, kl, iv, pt, (size_t) n, a, &cn), "CommonCrypto encrypt");
					void *c = CryptoNative_EvpCipherCreate2 (ciphers [ks][mode] (), key, (int32_t) kl * 8, iv, 1);
					CHECK (c && CryptoNative_EvpCipherCtxSetPadding (c, pad) == 1, "create");
					int inplace = !pad && rnd (3) == 0;
					int w = vita_crypt (c, pt, n, b, inplace, blockmode);
					CHECK (w == (int) cn && !memcmp (a, b, cn), "AES-%zu-%s pad=%d n=%d encrypt differs (w=%d want %zu)%s", kl * 8, mode_names [mode], pad, n, w, cn, inplace ? " in-place" : "");
					/* decrypt with the same context after Reset (a new IV = the original one): what UniversalCryptoTransform does */
					CHECK (CryptoNative_EvpCipherSetKeyAndIV (c, NULL, NULL, 0) == 1 && CryptoNative_EvpCipherReset (c, iv, mode ? 16 : 0) == 1, "reset to decrypt");
					w = vita_crypt (c, a, (int) cn, back, inplace, blockmode);
					CHECK (w == n && !memcmp (back, pt, (size_t) n), "AES-%zu-%s pad=%d n=%d decrypt differs (w=%d)", kl * 8, mode_names [mode], pad, n, w);
					CryptoNative_EvpCipherDestroy (c);
					runs++;
				}
	printf ("ok   %d random AES runs (3 key sizes x ECB/CBC/CFB8/CFB128, padding 0/1, random chunking, in-place) == CommonCrypto\n", runs);
}

static void cipher_errors (void)
{
	uint8_t key [16] = { 1 }, iv [16] = { 2 }, buf [64], out [96];
	int32_t n, isAlloc;
	char msg [256];
	/* padding 0 and a partial block: Final fails with an error the managed side can read */
	void *c = CryptoNative_EvpCipherCreate2 (CryptoNative_EvpAes128Cbc (), key, 128, iv, 1);
	CryptoNative_EvpCipherCtxSetPadding (c, 0);
	CryptoNative_EvpCipherUpdate (c, out, &n, buf, 7);
	CHECK (n == 0 && CryptoNative_EvpCipherFinalEx (c, out, &n) == 0, "partial block, no padding -> Final fails");
	uint64_t e = CryptoNative_ErrGetExceptionError (&isAlloc);
	CryptoNative_ErrErrorStringN (e, msg, sizeof msg);
	CHECK (e != 0 && !isAlloc && strstr (msg, "not multiple"), "error code + text (%s)", msg);
	CHECK (CryptoNative_ErrPeekError () == 0, "ErrGetExceptionError clears");
	CryptoNative_EvpCipherDestroy (c);
	/* bad PKCS#7 padding on decrypt */
	memset (buf, 0x5a, 32);
	c = CryptoNative_EvpCipherCreate2 (CryptoNative_EvpAes128Cbc (), key, 128, iv, 0);
	int32_t a = 0, b = 0;
	CHECK (CryptoNative_EvpCipherUpdate (c, out, &a, buf, 32) == 1 && a == 16, "decrypt holds back the last block (%d)", a);
	int fin = CryptoNative_EvpCipherFinalEx (c, out + a, &b);
	/* random ciphertext decrypts to a valid pad by chance with p ~ 1/256; accept a valid one only if it really is */
	CHECK (fin == 0 || (out [31] >= 1 && out [31] <= 16), "bad padding detected");
	CryptoNative_ErrGetExceptionError (&isAlloc);
	CryptoNative_EvpCipherDestroy (c);
	/* wrong key length, unknown algorithm, AEAD unsupported */
	CHECK (CryptoNative_EvpCipherCreate2 (CryptoNative_EvpAes128Cbc (), key, 256, iv, 1) == NULL && CryptoNative_ErrGetExceptionError (&isAlloc) != 0, "key length mismatch refused");
	CHECK (CryptoNative_EvpAes128Gcm () == NULL, "GCM unavailable");
	CHECK (CryptoNative_EvpCipherCreate2 (NULL, key, 128, iv, 1) == NULL && CryptoNative_ErrGetExceptionError (&isAlloc) != 0, "NULL algorithm refused");
	CHECK (CryptoNative_EnsureOpenSslInitialized () == 0, "EnsureOpenSslInitialized == 0");
	uint8_t r1 [64] = { 0 }, r2 [64] = { 0 };
	CHECK (CryptoNative_GetRandomBytes (r1, 64) == 1 && CryptoNative_GetRandomBytes (r2, 64) == 1 && memcmp (r1, r2, 64), "GetRandomBytes");
}

static void diff_hashes (void)
{
	const void *(*mds [5]) (void) = { CryptoNative_EvpMd5, CryptoNative_EvpSha1, CryptoNative_EvpSha256, CryptoNative_EvpSha384, CryptoNative_EvpSha512 };
	const int sizes [5] = { 16, 20, 32, 48, 64 };
	const CCHmacAlgorithm hm [5] = { kCCHmacAlgMD5, kCCHmacAlgSHA1, kCCHmacAlgSHA256, kCCHmacAlgSHA384, kCCHmacAlgSHA512 };
	const CCPseudoRandomAlgorithm prf [5] = { 0, kCCPRFHmacAlgSHA1, kCCPRFHmacAlgSHA256, kCCPRFHmacAlgSHA384, kCCPRFHmacAlgSHA512 };
	uint8_t data [3000], key [300], want [64], got [64], cur [64];
	int runs = 0;
	CHECK (CryptoNative_GetMaxMdSize () == 64 && CryptoNative_EvpSha3_256 () == NULL, "max md size / no SHA-3");
	for (int a = 0; a < 5; ++a) {
		CHECK (CryptoNative_EvpMdSize (mds [a] ()) == sizes [a], "EvpMdSize %d", a);
		for (int it = 0; it < 200; ++it) {
			size_t n = rnd (3000), kl = rnd (300);
			arc4random_buf (data, n); arc4random_buf (key, kl);
			switch (a) {
			case 0: CC_MD5 (data, (CC_LONG) n, want); break;
			case 1: CC_SHA1 (data, (CC_LONG) n, want); break;
			case 2: CC_SHA256 (data, (CC_LONG) n, want); break;
			case 3: CC_SHA384 (data, (CC_LONG) n, want); break;
			default: CC_SHA512 (data, (CC_LONG) n, want); break;
			}
			/* LiteHash: Create, Append in chunks, Current midway, Finalize, Reset, reuse */
			void *ctx = CryptoNative_EvpMdCtxCreate (mds [a] ());
			size_t done = 0, half = n / 2;
			uint32_t s = 0;
			while (done < n) {
				size_t t = 1 + rnd ((uint32_t) (n - done));
				if (done < half && done + t > half) t = half - done;
				CHECK (CryptoNative_EvpDigestUpdate (ctx, data + done, (int32_t) t) == 1, "update");
				done += t;
				if (done == half) {
					CHECK (CryptoNative_EvpDigestCurrent (ctx, cur, &s) == 1 && s == (uint32_t) sizes [a], "current");
					void *cp = CryptoNative_EvpMdCtxCopyEx (ctx);
					uint8_t cw [64];
					CHECK (cp && CryptoNative_EvpDigestFinalEx (cp, cw, &s) == 1 && !memcmp (cw, cur, (size_t) sizes [a]), "copy == current");
					CryptoNative_EvpMdCtxDestroy (cp);
				}
			}
			CHECK (CryptoNative_EvpDigestFinalEx (ctx, got, &s) == 1 && s == (uint32_t) sizes [a] && !memcmp (got, want, s), "digest %d n=%zu", a, n);
			CHECK (CryptoNative_EvpDigestReset (ctx, mds [a] ()) == 1 && CryptoNative_EvpDigestUpdate (ctx, data, (int32_t) n) == 1 &&
			       CryptoNative_EvpDigestFinalEx (ctx, got, &s) == 1 && !memcmp (got, want, s), "digest after reset");
			CryptoNative_EvpMdCtxDestroy (ctx);
			CHECK (CryptoNative_EvpDigestOneShot (mds [a] (), data, (int32_t) n, got, &s) == 1 && !memcmp (got, want, s), "one-shot digest");
			/* HMAC (LiteHmac): Create, Update chunks, Current, Final, Reset, OneShot, Copy */
			CCHmac (hm [a], key, kl, data, n, want);
			void *h = CryptoNative_HmacCreate (key, (int32_t) kl, mds [a] ());
			int32_t ml = 64;
			CHECK (h && CryptoNative_HmacUpdate (h, data, (int32_t) half) == 1, "hmac update");
			void *hc = CryptoNative_HmacCopy (h);
			CHECK (CryptoNative_HmacUpdate (h, data + half, (int32_t) (n - half)) == 1 && CryptoNative_HmacCurrent (h, cur, &ml) == 1 &&
			       ml == sizes [a] && !memcmp (cur, want, (size_t) ml), "hmac current");
			ml = 64;
			CHECK (CryptoNative_HmacFinal (h, got, &ml) == 1 && ml == sizes [a] && !memcmp (got, want, (size_t) ml), "hmac %d n=%zu kl=%zu", a, n, kl);
			ml = 64;
			CHECK (CryptoNative_HmacUpdate (hc, data + half, (int32_t) (n - half)) == 1 && CryptoNative_HmacFinal (hc, got, &ml) == 1 && !memcmp (got, want, (size_t) ml), "hmac copy");
			ml = 64;
			CHECK (CryptoNative_HmacReset (h) == 1 && CryptoNative_HmacUpdate (h, data, (int32_t) n) == 1 && CryptoNative_HmacFinal (h, got, &ml) == 1 &&
			       !memcmp (got, want, (size_t) ml), "hmac after reset");
			ml = 64;
			CHECK (CryptoNative_HmacOneShot (mds [a] (), kl ? key : NULL, (int32_t) kl, data, (int32_t) n, got, &ml) == 1 && !memcmp (got, want, (size_t) ml), "hmac one-shot");
			CryptoNative_HmacDestroy (h);
			CryptoNative_HmacDestroy (hc);
			if (a && it < 12) {
				size_t dl = 1 + rnd (100);
				uint32_t iters = 1 + rnd (300);
				uint8_t dk1 [100], dk2 [100];
				size_t pl = kl ? kl : 1, sl = 1 + n % 64;
				CHECK (CCKeyDerivationPBKDF (kCCPBKDF2, (const char *) key, pl, data, sl, prf [a], iters, dk1, dl) == kCCSuccess, "CommonCrypto PBKDF2");
				CHECK (CryptoNative_Pbkdf2 ((const char *) key, (int32_t) pl, data, (int32_t) sl, (int32_t) iters, mds [a] (), dk2, (int32_t) dl) == 1 &&
				       !memcmp (dk1, dk2, dl), "pbkdf2 %d iters=%u", a, iters);
			}
			runs++;
		}
	}
	printf ("ok   %d random digest/HMAC/PBKDF2 runs (MD5, SHA-1/256/384/512) == CommonCrypto\n", runs);
}

int main (void)
{
	kat_aes ();
	kat_modes ();
	kat_hash ();
	kat_hmac_pbkdf2 ();
	printf ("%s known-answer vectors\n", g_fail ? "FAIL" : "ok  ");
	diff_ciphers ();
	cipher_errors ();
	diff_hashes ();
	printf ("%s crypto (%d checks, %d failed)\n", g_fail ? "FAIL" : "PASS", g_checks, g_fail);
	return g_fail ? 1 : 0;
}
