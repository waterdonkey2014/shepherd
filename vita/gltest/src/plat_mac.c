/* gltest host services for the Mac self-check (headless, silent; log under $GLTEST_OUT or $SHEPHERD_WORK/gltest/mac). */
#include "plat.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <sys/stat.h>

static char g_path[1024];
const char *plat_log_path(void) { return g_path; }
void plat_log_open(void)
{
	const char *d = getenv("GLTEST_OUT");
	if (!d || !*d) d = "/tmp/shepherd-gltest-mac";
	mkdir(d, 0755);
	snprintf(g_path, sizeof g_path, "%s/gltest.txt", d);
	FILE *f = fopen(g_path, "w");
	if (f) fclose(f);
}
void plat_log_line(const char *line)
{
	FILE *f = fopen(g_path, "a");
	if (f) { fputs(line, f); fputc('\n', f); fclose(f); }
	puts(line);
	fflush(stdout);
}
uint64_t plat_us(void) { struct timespec t; clock_gettime(CLOCK_MONOTONIC, &t); return (uint64_t)t.tv_sec * 1000000u + (uint64_t)t.tv_nsec / 1000u; }
void plat_delay_ms(int ms) { usleep((useconds_t)ms * 1000u); }
const char *plat_name(void) { return "macOS self-check (desktop GL, not the device)"; }
const char *plat_dialect(void) { return "glsl120"; }
int plat_shacccg(char *where, int n) { snprintf(where, n, "n/a on the Mac (desktop GLSL compiler)"); return 1; }
void plat_screen(int on) { (void)on; }
void plat_clocks_set(void) {}
void plat_clocks_restore(void) {}
void plat_clocks_str(char *buf, int n) { snprintf(buf, n, "n/a on the Mac"); }
int plat_button(void) { return 1; }
int plat_exit_wait_frames(void) { return 1; }
void plat_exit(int fails) { exit(fails ? 1 : 0); }
