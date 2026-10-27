/* SHEPHERD-VITA contract test: what the Vita host + pal_vita.c provide to the crypto/net/stub files, on the Mac. */
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
void shepherd_vita_native_log (const char *msg) { fprintf (stderr, "N> %s\n", msg); }
void shepherd_vita_crypto_rng (uint8_t *buf, int32_t len) { arc4random_buf (buf, (size_t) len); }
