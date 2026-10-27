/* Host services for gltest.c: the Vita (plat_vita.c) or the Mac self-check (plat_mac.c). */
#ifndef GLTEST_PLAT_H
#define GLTEST_PLAT_H
#include <stdint.h>

void plat_log_open(void);                    /* truncate the durable log */
void plat_log_line(const char *line);        /* one open/append/close per line (a crash keeps everything before it) */
const char *plat_log_path(void);
uint64_t plat_us(void);
void plat_delay_ms(int ms);
const char *plat_name(void);
const char *plat_dialect(void);              /* "essl100" (vitaGL) or "glsl120" (Mac legacy context) */
/* libshacccg.suprx: 1 if found (where = the path), 0 if missing (where = the instructions line) */
int plat_shacccg(char *where, int n);
void plat_screen(int on);                    /* text console before vitaGL owns the display (Vita debugScreen) */
void plat_clocks_set(void);                  /* 444/222/222/166 on the Vita; restored by plat_clocks_restore */
void plat_clocks_restore(void);
void plat_clocks_str(char *buf, int n);
int plat_button(void);                       /* 1 while X, O or START is held */
int plat_exit_wait_frames(void);             /* how many final-screen frames to show before giving up waiting (0 = none) */
void plat_exit(int fails);
#endif
