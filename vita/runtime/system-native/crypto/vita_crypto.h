/*
 * SHEPHERD-VITA: small, self-contained crypto primitives for the PS Vita build (no OpenSSL exists there).
 * Written from the standards, not copied: AES (FIPS 197), SHA-1/SHA-256/SHA-384/SHA-512 (FIPS 180-4), MD5 (RFC 1321),
 * HMAC (FIPS 198-1), PBKDF2 (RFC 8018 5.2). Constant tables are derived by gen_tables.py. Known-answer tests:
 * vita/runtime/tests/test_crypto.c. Not constant-time (table lookups): fine for reading/writing the game's own files,
 * not meant for secrets crossing a network (the Vita build has no network).
 */
#pragma once
#include <stddef.h>
#include <stdint.h>

/* ---------------------------------------------------------------- AES */
typedef struct {
	uint8_t rk [240];   /* (nr + 1) round keys of 16 bytes */
	int nr;             /* 10 / 12 / 14 */
} vc_aes_key;

/* bits = 128 / 192 / 256; returns 0, or -1 for another size */
int vc_aes_set_key (vc_aes_key *k, const uint8_t *key, int bits);
void vc_aes_encrypt_block (const vc_aes_key *k, const uint8_t in [16], uint8_t out [16]);
void vc_aes_decrypt_block (const vc_aes_key *k, const uint8_t in [16], uint8_t out [16]);

/* ---------------------------------------------------------------- hashes */
enum { VC_MD5 = 1, VC_SHA1, VC_SHA256, VC_SHA384, VC_SHA512 };
#define VC_MAX_DIGEST 64
#define VC_MAX_BLOCK 128

typedef struct {
	uint32_t h [8];
	uint64_t bytes;
	uint8_t buf [64];
	uint32_t n;
} vc_ctx32;

typedef struct {
	uint64_t h [8];
	uint64_t bytes;     /* < 2^64 bytes: the high half of the 128-bit length is always 0 */
	uint8_t buf [128];
	uint32_t n;
} vc_ctx64;

typedef union {
	vc_ctx32 c32;
	vc_ctx64 c64;
} vc_hash_ctx;

typedef struct vc_hash {
	int id;
	int digest_len;
	int block_len;
	const char *name;
	void (*init) (vc_hash_ctx *c);
	void (*update) (vc_hash_ctx *c, const uint8_t *data, size_t len);
	void (*final) (vc_hash_ctx *c, uint8_t *out);   /* the context must be re-initialized before reuse */
} vc_hash;

extern const vc_hash vc_md5, vc_sha1, vc_sha256, vc_sha384, vc_sha512;

void vc_hash_oneshot (const vc_hash *h, const void *data, size_t len, uint8_t *out);

/* ---------------------------------------------------------------- HMAC */
typedef struct {
	const vc_hash *h;
	vc_hash_ctx inner, outer;           /* running */
	vc_hash_ctx inner0, outer0;         /* after absorbing key^ipad / key^opad: what a reset returns to */
} vc_hmac;

void vc_hmac_init (vc_hmac *m, const vc_hash *h, const uint8_t *key, size_t keylen);
void vc_hmac_reset (vc_hmac *m);
void vc_hmac_update (vc_hmac *m, const uint8_t *data, size_t len);
void vc_hmac_final (vc_hmac *m, uint8_t *out);   /* then resets itself (same key) */

/* PBKDF2-HMAC (RFC 8018 5.2); iterations >= 1 */
void vc_pbkdf2 (const vc_hash *h, const uint8_t *pw, size_t pwlen, const uint8_t *salt, size_t saltlen,
		uint32_t iterations, uint8_t *out, size_t outlen);
