/*
 * SHEPHERD-VITA: AES / SHA-1 / SHA-2 / MD5 / HMAC / PBKDF2, written from the standards (see vita_crypto.h).
 * Portable C99, no platform calls; compiled into the Vita shim and into the Mac unit tests unchanged.
 */
#include "vita_crypto.h"
#include "vita_crypto_tables.h"

#include <string.h>

/* ================================================================ AES (FIPS 197) */

static uint8_t xtime (uint8_t x)
{
	return (uint8_t) ((x << 1) ^ ((x >> 7) * 0x1b));
}

/* multiplication in GF(2^8) by a small constant (only 2, 3, 9, 11, 13, 14 are used) */
static uint8_t gmul (uint8_t x, uint8_t n)
{
	uint8_t r = 0;
	while (n) {
		if (n & 1)
			r ^= x;
		x = xtime (x);
		n >>= 1;
	}
	return r;
}

int vc_aes_set_key (vc_aes_key *k, const uint8_t *key, int bits)
{
	int nk;
	switch (bits) {
	case 128: nk = 4; break;
	case 192: nk = 6; break;
	case 256: nk = 8; break;
	default: return -1;
	}
	int nr = nk + 6;
	int words = 4 * (nr + 1);
	uint8_t *w = k->rk;
	memcpy (w, key, (size_t) (4 * nk));
	uint8_t rcon = 1;
	for (int i = nk; i < words; ++i) {
		uint8_t t [4];
		memcpy (t, w + 4 * (i - 1), 4);
		if (i % nk == 0) {
			/* SubWord (RotWord (temp)) xor Rcon [i / Nk] */
			uint8_t t0 = t [0];
			t [0] = (uint8_t) (aes_sbox [t [1]] ^ rcon);
			t [1] = aes_sbox [t [2]];
			t [2] = aes_sbox [t [3]];
			t [3] = aes_sbox [t0];
			rcon = xtime (rcon);
		} else if (nk > 6 && i % nk == 4) {
			for (int j = 0; j < 4; ++j)
				t [j] = aes_sbox [t [j]];
		}
		for (int j = 0; j < 4; ++j)
			w [4 * i + j] = (uint8_t) (w [4 * (i - nk) + j] ^ t [j]);
	}
	k->nr = nr;
	return 0;
}

static void add_round_key (uint8_t *s, const uint8_t *rk)
{
	for (int i = 0; i < 16; ++i)
		s [i] ^= rk [i];
}

/* state byte s [4 c + r] = row r, column c (the input order) */
void vc_aes_encrypt_block (const vc_aes_key *k, const uint8_t in [16], uint8_t out [16])
{
	uint8_t s [16], t [16];
	memcpy (s, in, 16);
	add_round_key (s, k->rk);
	for (int round = 1; round <= k->nr; ++round) {
		/* SubBytes + ShiftRows: row r moves left by r */
		for (int c = 0; c < 4; ++c)
			for (int r = 0; r < 4; ++r)
				t [4 * c + r] = aes_sbox [s [4 * ((c + r) & 3) + r]];
		if (round != k->nr) {
			/* MixColumns */
			for (int c = 0; c < 4; ++c) {
				uint8_t a0 = t [4 * c], a1 = t [4 * c + 1], a2 = t [4 * c + 2], a3 = t [4 * c + 3];
				s [4 * c] = (uint8_t) (xtime (a0) ^ (xtime (a1) ^ a1) ^ a2 ^ a3);
				s [4 * c + 1] = (uint8_t) (a0 ^ xtime (a1) ^ (xtime (a2) ^ a2) ^ a3);
				s [4 * c + 2] = (uint8_t) (a0 ^ a1 ^ xtime (a2) ^ (xtime (a3) ^ a3));
				s [4 * c + 3] = (uint8_t) ((xtime (a0) ^ a0) ^ a1 ^ a2 ^ xtime (a3));
			}
		} else {
			memcpy (s, t, 16);
		}
		add_round_key (s, k->rk + 16 * round);
	}
	memcpy (out, s, 16);
}

void vc_aes_decrypt_block (const vc_aes_key *k, const uint8_t in [16], uint8_t out [16])
{
	uint8_t s [16], t [16];
	memcpy (s, in, 16);
	add_round_key (s, k->rk + 16 * k->nr);
	for (int round = k->nr - 1; round >= 0; --round) {
		/* InvShiftRows (row r moves right by r) + InvSubBytes */
		for (int c = 0; c < 4; ++c)
			for (int r = 0; r < 4; ++r)
				t [4 * ((c + r) & 3) + r] = aes_inv_sbox [s [4 * c + r]];
		add_round_key (t, k->rk + 16 * round);
		if (round != 0) {
			/* InvMixColumns */
			for (int c = 0; c < 4; ++c) {
				uint8_t a0 = t [4 * c], a1 = t [4 * c + 1], a2 = t [4 * c + 2], a3 = t [4 * c + 3];
				s [4 * c] = (uint8_t) (gmul (a0, 14) ^ gmul (a1, 11) ^ gmul (a2, 13) ^ gmul (a3, 9));
				s [4 * c + 1] = (uint8_t) (gmul (a0, 9) ^ gmul (a1, 14) ^ gmul (a2, 11) ^ gmul (a3, 13));
				s [4 * c + 2] = (uint8_t) (gmul (a0, 13) ^ gmul (a1, 9) ^ gmul (a2, 14) ^ gmul (a3, 11));
				s [4 * c + 3] = (uint8_t) (gmul (a0, 11) ^ gmul (a1, 13) ^ gmul (a2, 9) ^ gmul (a3, 14));
			}
		} else {
			memcpy (s, t, 16);
		}
	}
	memcpy (out, s, 16);
}

/* ================================================================ hashes */

static uint32_t rol32 (uint32_t x, int n) { return (x << n) | (x >> (32 - n)); }
static uint32_t ror32 (uint32_t x, int n) { return (x >> n) | (x << (32 - n)); }
static uint64_t ror64 (uint64_t x, int n) { return (x >> n) | (x << (64 - n)); }

static uint32_t be32 (const uint8_t *p) { return ((uint32_t) p [0] << 24) | ((uint32_t) p [1] << 16) | ((uint32_t) p [2] << 8) | p [3]; }
static uint32_t le32 (const uint8_t *p) { return ((uint32_t) p [3] << 24) | ((uint32_t) p [2] << 16) | ((uint32_t) p [1] << 8) | p [0]; }
static uint64_t be64 (const uint8_t *p) { return ((uint64_t) be32 (p) << 32) | be32 (p + 4); }
static void put_be32 (uint8_t *p, uint32_t v) { p [0] = (uint8_t) (v >> 24); p [1] = (uint8_t) (v >> 16); p [2] = (uint8_t) (v >> 8); p [3] = (uint8_t) v; }
static void put_le32 (uint8_t *p, uint32_t v) { p [3] = (uint8_t) (v >> 24); p [2] = (uint8_t) (v >> 16); p [1] = (uint8_t) (v >> 8); p [0] = (uint8_t) v; }
static void put_be64 (uint8_t *p, uint64_t v) { put_be32 (p, (uint32_t) (v >> 32)); put_be32 (p + 4, (uint32_t) v); }

/* ---- MD5 (RFC 1321 3.4) */
static void md5_block (uint32_t *h, const uint8_t *p)
{
	static const uint8_t sh [4][4] = { { 7, 12, 17, 22 }, { 5, 9, 14, 20 }, { 4, 11, 16, 23 }, { 6, 10, 15, 21 } };
	uint32_t x [16];
	for (int i = 0; i < 16; ++i)
		x [i] = le32 (p + 4 * i);
	uint32_t a = h [0], b = h [1], c = h [2], d = h [3];
	for (int i = 0; i < 64; ++i) {
		uint32_t f;
		int k, round = i >> 4;
		switch (round) {
		case 0: f = (b & c) | (~b & d); k = i; break;
		case 1: f = (b & d) | (c & ~d); k = (1 + 5 * i) & 15; break;
		case 2: f = b ^ c ^ d; k = (5 + 3 * i) & 15; break;
		default: f = c ^ (b | ~d); k = (7 * i) & 15; break;
		}
		uint32_t t = d;
		d = c;
		c = b;
		b = b + rol32 (a + f + x [k] + md5_t [i], sh [round][i & 3]);
		a = t;
	}
	h [0] += a; h [1] += b; h [2] += c; h [3] += d;
}

/* ---- SHA-1 (FIPS 180-4 6.1.2) */
static void sha1_block (uint32_t *h, const uint8_t *p)
{
	uint32_t w [80];
	for (int t = 0; t < 16; ++t)
		w [t] = be32 (p + 4 * t);
	for (int t = 16; t < 80; ++t)
		w [t] = rol32 (w [t - 3] ^ w [t - 8] ^ w [t - 14] ^ w [t - 16], 1);
	uint32_t a = h [0], b = h [1], c = h [2], d = h [3], e = h [4];
	for (int t = 0; t < 80; ++t) {
		uint32_t f;
		if (t < 20)
			f = (b & c) ^ (~b & d);
		else if (t < 40 || t >= 60)
			f = b ^ c ^ d;
		else
			f = (b & c) ^ (b & d) ^ (c & d);
		uint32_t tmp = rol32 (a, 5) + f + e + sha1_k [t / 20] + w [t];
		e = d;
		d = c;
		c = rol32 (b, 30);
		b = a;
		a = tmp;
	}
	h [0] += a; h [1] += b; h [2] += c; h [3] += d; h [4] += e;
}

/* ---- SHA-256 (FIPS 180-4 6.2.2) */
static void sha256_block (uint32_t *h, const uint8_t *p)
{
	uint32_t w [64];
	for (int t = 0; t < 16; ++t)
		w [t] = be32 (p + 4 * t);
	for (int t = 16; t < 64; ++t) {
		uint32_t s0 = ror32 (w [t - 15], 7) ^ ror32 (w [t - 15], 18) ^ (w [t - 15] >> 3);
		uint32_t s1 = ror32 (w [t - 2], 17) ^ ror32 (w [t - 2], 19) ^ (w [t - 2] >> 10);
		w [t] = s1 + w [t - 7] + s0 + w [t - 16];
	}
	uint32_t a = h [0], b = h [1], c = h [2], d = h [3], e = h [4], f = h [5], g = h [6], hh = h [7];
	for (int t = 0; t < 64; ++t) {
		uint32_t t1 = hh + (ror32 (e, 6) ^ ror32 (e, 11) ^ ror32 (e, 25)) + ((e & f) ^ (~e & g)) + sha256_k [t] + w [t];
		uint32_t t2 = (ror32 (a, 2) ^ ror32 (a, 13) ^ ror32 (a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
		hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
	}
	h [0] += a; h [1] += b; h [2] += c; h [3] += d; h [4] += e; h [5] += f; h [6] += g; h [7] += hh;
}

/* ---- SHA-512 / SHA-384 (FIPS 180-4 6.4.2) */
static void sha512_block (uint64_t *h, const uint8_t *p)
{
	uint64_t w [80];
	for (int t = 0; t < 16; ++t)
		w [t] = be64 (p + 8 * t);
	for (int t = 16; t < 80; ++t) {
		uint64_t s0 = ror64 (w [t - 15], 1) ^ ror64 (w [t - 15], 8) ^ (w [t - 15] >> 7);
		uint64_t s1 = ror64 (w [t - 2], 19) ^ ror64 (w [t - 2], 61) ^ (w [t - 2] >> 6);
		w [t] = s1 + w [t - 7] + s0 + w [t - 16];
	}
	uint64_t a = h [0], b = h [1], c = h [2], d = h [3], e = h [4], f = h [5], g = h [6], hh = h [7];
	for (int t = 0; t < 80; ++t) {
		uint64_t t1 = hh + (ror64 (e, 14) ^ ror64 (e, 18) ^ ror64 (e, 41)) + ((e & f) ^ (~e & g)) + sha512_k [t] + w [t];
		uint64_t t2 = (ror64 (a, 28) ^ ror64 (a, 34) ^ ror64 (a, 39)) + ((a & b) ^ (a & c) ^ (b & c));
		hh = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
	}
	h [0] += a; h [1] += b; h [2] += c; h [3] += d; h [4] += e; h [5] += f; h [6] += g; h [7] += hh;
}

/* ---- buffering shared by the 64-byte-block hashes */
typedef void (*block32_fn) (uint32_t *h, const uint8_t *p);

static void update32 (vc_ctx32 *c, const uint8_t *data, size_t len, block32_fn blk)
{
	c->bytes += len;
	if (c->n) {
		size_t take = 64 - c->n < len ? 64 - c->n : len;
		memcpy (c->buf + c->n, data, take);
		c->n += (uint32_t) take;
		data += take;
		len -= take;
		if (c->n < 64)
			return;
		blk (c->h, c->buf);
		c->n = 0;
	}
	while (len >= 64) {
		blk (c->h, data);
		data += 64;
		len -= 64;
	}
	if (len) {
		memcpy (c->buf, data, len);
		c->n = (uint32_t) len;
	}
}

/* 0x80, zeros, then the bit length as 8 bytes (big- or little-endian) */
static void pad32 (vc_ctx32 *c, block32_fn blk, int little)
{
	uint64_t bits = c->bytes * 8;
	c->buf [c->n++] = 0x80;
	if (c->n > 56) {
		memset (c->buf + c->n, 0, 64 - c->n);
		blk (c->h, c->buf);
		c->n = 0;
	}
	memset (c->buf + c->n, 0, 56 - c->n);
	for (int i = 0; i < 8; ++i)
		c->buf [56 + i] = (uint8_t) (little ? bits >> (8 * i) : bits >> (56 - 8 * i));
	blk (c->h, c->buf);
	c->n = 0;
}

static void md5_init (vc_hash_ctx *x)
{
	vc_ctx32 *c = &x->c32;
	memset (c, 0, sizeof (*c));
	c->h [0] = 0x67452301u; c->h [1] = 0xefcdab89u; c->h [2] = 0x98badcfeu; c->h [3] = 0x10325476u;
}
static void md5_update (vc_hash_ctx *x, const uint8_t *d, size_t n) { update32 (&x->c32, d, n, md5_block); }
static void md5_final (vc_hash_ctx *x, uint8_t *out)
{
	pad32 (&x->c32, md5_block, 1);
	for (int i = 0; i < 4; ++i)
		put_le32 (out + 4 * i, x->c32.h [i]);
}

static void sha1_init (vc_hash_ctx *x)
{
	vc_ctx32 *c = &x->c32;
	memset (c, 0, sizeof (*c));
	c->h [0] = 0x67452301u; c->h [1] = 0xefcdab89u; c->h [2] = 0x98badcfeu; c->h [3] = 0x10325476u; c->h [4] = 0xc3d2e1f0u;
}
static void sha1_update (vc_hash_ctx *x, const uint8_t *d, size_t n) { update32 (&x->c32, d, n, sha1_block); }
static void sha1_final (vc_hash_ctx *x, uint8_t *out)
{
	pad32 (&x->c32, sha1_block, 0);
	for (int i = 0; i < 5; ++i)
		put_be32 (out + 4 * i, x->c32.h [i]);
}

static void sha256_init (vc_hash_ctx *x)
{
	memset (&x->c32, 0, sizeof (x->c32));
	memcpy (x->c32.h, sha256_h0, sizeof (sha256_h0));
}
static void sha256_update (vc_hash_ctx *x, const uint8_t *d, size_t n) { update32 (&x->c32, d, n, sha256_block); }
static void sha256_final (vc_hash_ctx *x, uint8_t *out)
{
	pad32 (&x->c32, sha256_block, 0);
	for (int i = 0; i < 8; ++i)
		put_be32 (out + 4 * i, x->c32.h [i]);
}

static void update64 (vc_ctx64 *c, const uint8_t *data, size_t len)
{
	c->bytes += len;
	if (c->n) {
		size_t take = 128 - c->n < len ? 128 - c->n : len;
		memcpy (c->buf + c->n, data, take);
		c->n += (uint32_t) take;
		data += take;
		len -= take;
		if (c->n < 128)
			return;
		sha512_block (c->h, c->buf);
		c->n = 0;
	}
	while (len >= 128) {
		sha512_block (c->h, data);
		data += 128;
		len -= 128;
	}
	if (len) {
		memcpy (c->buf, data, len);
		c->n = (uint32_t) len;
	}
}

static void pad64 (vc_ctx64 *c)
{
	uint64_t bits = c->bytes * 8, hi = c->bytes >> 61;
	c->buf [c->n++] = 0x80;
	if (c->n > 112) {
		memset (c->buf + c->n, 0, 128 - c->n);
		sha512_block (c->h, c->buf);
		c->n = 0;
	}
	memset (c->buf + c->n, 0, 112 - c->n);
	put_be64 (c->buf + 112, hi);
	put_be64 (c->buf + 120, bits);
	sha512_block (c->h, c->buf);
	c->n = 0;
}

static void sha512_init (vc_hash_ctx *x)
{
	memset (&x->c64, 0, sizeof (x->c64));
	memcpy (x->c64.h, sha512_h0, sizeof (sha512_h0));
}
static void sha384_init (vc_hash_ctx *x)
{
	memset (&x->c64, 0, sizeof (x->c64));
	memcpy (x->c64.h, sha384_h0, sizeof (sha384_h0));
}
static void sha512_update (vc_hash_ctx *x, const uint8_t *d, size_t n) { update64 (&x->c64, d, n); }
static void sha512_final (vc_hash_ctx *x, uint8_t *out)
{
	pad64 (&x->c64);
	for (int i = 0; i < 8; ++i)
		put_be64 (out + 8 * i, x->c64.h [i]);
}
static void sha384_final (vc_hash_ctx *x, uint8_t *out)
{
	pad64 (&x->c64);
	for (int i = 0; i < 6; ++i)
		put_be64 (out + 8 * i, x->c64.h [i]);
}

const vc_hash vc_md5 = { VC_MD5, 16, 64, "MD5", md5_init, md5_update, md5_final };
const vc_hash vc_sha1 = { VC_SHA1, 20, 64, "SHA1", sha1_init, sha1_update, sha1_final };
const vc_hash vc_sha256 = { VC_SHA256, 32, 64, "SHA256", sha256_init, sha256_update, sha256_final };
const vc_hash vc_sha384 = { VC_SHA384, 48, 128, "SHA384", sha384_init, sha512_update, sha384_final };
const vc_hash vc_sha512 = { VC_SHA512, 64, 128, "SHA512", sha512_init, sha512_update, sha512_final };

void vc_hash_oneshot (const vc_hash *h, const void *data, size_t len, uint8_t *out)
{
	vc_hash_ctx c;
	h->init (&c);
	h->update (&c, (const uint8_t *) data, len);
	h->final (&c, out);
}

/* ================================================================ HMAC (FIPS 198-1) */

void vc_hmac_init (vc_hmac *m, const vc_hash *h, const uint8_t *key, size_t keylen)
{
	uint8_t k0 [VC_MAX_BLOCK], pad [VC_MAX_BLOCK];
	size_t b = (size_t) h->block_len;
	memset (k0, 0, sizeof (k0));
	if (keylen > b)
		vc_hash_oneshot (h, key, keylen, k0);   /* K0 = H(K) padded with zeros */
	else if (keylen)
		memcpy (k0, key, keylen);
	m->h = h;
	for (size_t i = 0; i < b; ++i)
		pad [i] = (uint8_t) (k0 [i] ^ 0x36);
	h->init (&m->inner0);
	h->update (&m->inner0, pad, b);
	for (size_t i = 0; i < b; ++i)
		pad [i] = (uint8_t) (k0 [i] ^ 0x5c);
	h->init (&m->outer0);
	h->update (&m->outer0, pad, b);
	memset (k0, 0, sizeof (k0));
	memset (pad, 0, sizeof (pad));
	vc_hmac_reset (m);
}

void vc_hmac_reset (vc_hmac *m)
{
	m->inner = m->inner0;
}

void vc_hmac_update (vc_hmac *m, const uint8_t *data, size_t len)
{
	m->h->update (&m->inner, data, len);
}

void vc_hmac_final (vc_hmac *m, uint8_t *out)
{
	uint8_t t [VC_MAX_DIGEST];
	m->h->final (&m->inner, t);
	m->outer = m->outer0;
	m->h->update (&m->outer, t, (size_t) m->h->digest_len);
	m->h->final (&m->outer, out);
	memset (t, 0, sizeof (t));
	vc_hmac_reset (m);
}

/* ================================================================ PBKDF2 (RFC 8018 5.2) */

void vc_pbkdf2 (const vc_hash *h, const uint8_t *pw, size_t pwlen, const uint8_t *salt, size_t saltlen,
		uint32_t iterations, uint8_t *out, size_t outlen)
{
	vc_hmac base, m;
	uint8_t u [VC_MAX_DIGEST], t [VC_MAX_DIGEST], be [4];
	size_t hl = (size_t) h->digest_len;
	vc_hmac_init (&base, h, pw, pwlen);
	for (uint32_t block = 1; outlen; ++block) {
		put_be32 (be, block);
		m = base;
		vc_hmac_update (&m, salt, saltlen);
		vc_hmac_update (&m, be, 4);
		vc_hmac_final (&m, u);
		memcpy (t, u, hl);
		for (uint32_t i = 1; i < iterations; ++i) {
			m = base;
			vc_hmac_update (&m, u, hl);
			vc_hmac_final (&m, u);
			for (size_t j = 0; j < hl; ++j)
				t [j] ^= u [j];
		}
		size_t n = outlen < hl ? outlen : hl;
		memcpy (out, t, n);
		out += n;
		outlen -= n;
	}
	memset (u, 0, sizeof (u));
	memset (t, 0, sizeof (t));
	memset (&base, 0, sizeof (base));
	memset (&m, 0, sizeof (m));
}
