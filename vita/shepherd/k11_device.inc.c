/* SHEPHERD-VITA device kit #11, track device (vita/notes/kit11-device.md): KIT #10's DEVICE RESULTS AS DEFAULTS, the player build, and the
 * boot screen's EMBLEM SET. Included by host.c after k8_boot.inc.c and before k10_boot.inc.c (which uses the emblem set below).
 *
 * THE DEFAULTS TABLE (k11_device_config, main, right after the env file): every row is only a DEFAULT -- setenv without overwrite, so a line in
 * the env file always wins -- and each row was measured on the owner's PS Vita in kit #10 and is identical by its own gates:
 *  - the logging diet (the release apps 00097 / 00098 / 00095 / 00193 / 00194): SHEPHERD_K8_LOG_DIET=batch -- engine.log written once a frame
 *    instead of a card write per line (kit #10 X6: boot to the Main Menu 102.6 -> 97.9 s, -4.7 s; the idle frame unchanged; owner decision:
 *    release builds keep it). =line = kit #10. The measurement app 00099 and the headless 00011 keep kit #10's card write per line (their
 *    engine.log is the crash evidence, and Measure's boot history stays comparable; review fix).
 *  - DMA copies (the GL apps): SHEPHERD_K10_DMA=1 -- the preload's copies into video memory by sceDmacMemcpy (kit #10 X4: 0 mismatches over
 *    49 copies, 0.38 vs 0.49 ms a copy, -22 %, the CPU free meanwhile). =0 = kit #10.
 *  - THE PLAYER BUILD (the play apps 00097 / 00098 / 00095 / 00193 / 00194; owner 2026-10-31, "more ms across the board, picture identical"):
 *    OPT-IN in kit #11 (review fix: "more ms, test first" -- it waits for its device A/B, and the kit's device steps keep the hard-crash trail;
 *    K11_PLAYER_DEFAULT below flips it after the A/B).
 *    SHEPHERD_K11_PLAYER=1 -- the test kit's always-on instruments off, the crash recorder's handled ends kept: the black box's rings + its dump at a FAIL END /
 *    STALL / fatal error stay, its live file (a card write every 5 s) goes (SHEPHERD_K7_BLACKBOX=end); the stack watch (every Mono thread's
 *    stack painted at its start and scanned at its stop: the STK lines) off (SHEPHERD_STACK_WATCH=0); the icall hit counters (an atomic
 *    increment on every engine call) off (SHEPHERD_ICALL_HITS=0); the engine's per-frame statistics only inside their measurement window
 *    (Diag/SimStats.cs reads SHEPHERD_K11_PLAYER). Kept: the heartbeats (the stall watch and the MEM lines read them), the GC's pause ring (one
 *    store per collection), every K / H / MEM line, the [sim] window itself (the kit's idle numbers). NOT kept: a HARD crash (no handler runs on
 *    the Vita) leaves no blackbox-live.txt, and no STK lines show the stacks' deepest use. Unset / SHEPHERD_K11_PLAYER=0 = kit #10 (every
 *    instrument on); each instrument's own switch still works either way. The measurement app 00099 and the headless 00011 keep every
 *    instrument (their kit-to-kit history).
 *  - THE BOOT LOADING SCREEN ON BY DEFAULT with the right emblem: k10_boot.inc.c reads the emblem set below; on by default only when the card
 *    holds a set marked BOTH 'rings drawn' (its rings draw right: vita/tools/bootscreen_set.py checks every frame) AND 'default on' (the owner's
 *    yes to that set, question 3: bootscreen_set.py make --default-on; review fix), else kit #10's rule (opt-in).
 *    SHEPHERD_K10_BOOTSCREEN=0 = black (kit #9 / #10's default); SHEPHERD_K11_EMBLEMS=0 = kit #10's single recording.
 * SHEPHERD_K11_DEVICE=0 = every row above off = kit #10's defaults (one K11 line says which rows are on and why). The host's decision on the
 * player build is the only one: whenever it is off (DEVICE=0, not a play app, or not asked for) the host writes SHEPHERD_K11_PLAYER=0, so
 * the managed half (Diag/SimStats.cs) never runs half on (integration review).
 *
 * THE EMBLEM SET (owner decisions 2026-10-30, vita/notes/roadmap-kits.md "The emblems"): the game picks a RANDOM emblem for every load
 * (MMTools.MMTransition: list[UnityEngine.Random.Range(0, list.Count)] over its pentagram sprites); kit #10's single recording always showed
 * the one its Mac run drew (an 'eyes' symbol). Now: one recording per emblem in ux0:data/shepherd/data/video/boot/ + its SET.txt (made by
 * vita/tools/bootscreen_set.py from recordings of the game's own loading screen, owner-local), and every boot draws ONE at random, every
 * emblem on the card equally likely (the owner's odds: the game's double Pentagram-Star slot dropped); SHEPHERD_K11_EMBLEM_ODDS=game weighs
 * them by the game's list slots instead (a strict copy of MMTransition's odds); SHEPHERD_K11_EMBLEM=<name> always shows that one (tests).
 * Which emblems are on the card is the staging's business (the base set + the Sins of the Flesh pentagram always, Woolhaven's set only if
 * owned, the co-op versions only once co-op was played): the host draws among what is there. The draw uses the console's random generator
 * (sceKernelGetRandomNumber), never the game's: no game state, no game random number, no frame of the game's changes. Each file is checked
 * against the checksum SET.txt lists for it (a stale set = kit #10's single file, or black, one K11 line says why). */

static int g_k11_dev = 1;      /* SHEPHERD_K11_DEVICE: 0 = kit #10's defaults */
static int g_k11_player;       /* the player build is on in this start */

/* main, right after apply_env_file () (before k7_init / stacks_config read their switches): the defaults (env file lines win) */
static void k11_device_config (void)
{
	const char *m = getenv ("SHEPHERD_K11_DEVICE");
	if (m && !strcmp (m, "0")) {
		g_k11_dev = 0;
		setenv ("SHEPHERD_K11_PLAYER", "0", 1);   /* (integration review: the managed half (Diag/SimStats.cs) reads this name -- off with the master) */
		LOG ("K11 device defaults OFF (SHEPHERD_K11_DEVICE=0): kit #10's -- engine.log a card write per line, the preload's copies by the CPU, every test "
		     "instrument on, the boot screen opt-in with kit #10's single recording");
		return;
	}
	char rows [520];
	int n = 0;
	const char *was = getenv ("SHEPHERD_K8_LOG_DIET");
#ifdef SHP_PLAY_APP
	/* (kit #11 review fix: the release apps only -- the owner's decision is "release builds keep it"; the measurement app 00099 and the headless
	 * 00011 keep a card write per line: their engine.log is the crash evidence (K8Speed.cs), and Measure's boot history stays comparable kit to kit) */
	setenv ("SHEPHERD_K8_LOG_DIET", "batch", 0);
	n += snprintf (rows + n, sizeof rows - (size_t) n, "logging diet %s", was ? "as the env file says" : "batch (default; =line = kit #10)");
#else
	n += snprintf (rows + n, sizeof rows - (size_t) n, "logging diet %s", was ? "as the env file says"
		       : "per line as kit #10 (a measurement / diagnostic app: engine.log is the crash evidence; =batch in the env file batches it)");
#endif
#ifdef SHEPHERD_GL
	was = getenv ("SHEPHERD_K10_DMA");
	setenv ("SHEPHERD_K10_DMA", "1", 0);
	n += snprintf (rows + n, sizeof rows - (size_t) n, "; DMA copies %s", was ? "as the env file says" : "on (default; =0 = kit #10)");
#endif
#ifdef SHP_PLAY_APP
	/* (kit #11 review fix: the player build is OPT-IN in this kit -- "more ms, test first": it waits for its device A/B beside the logging diet,
	 * and kit #11's device steps (the first walk, the new GPU paths) run in these apps, where a hard crash runs no handler: blackbox-live.txt and
	 * the STK lines are then the only evidence on the card. After the owner's A/B: K11_PLAYER_DEFAULT 1) */
#define K11_PLAYER_DEFAULT 0
	const char *p = getenv ("SHEPHERD_K11_PLAYER");
	g_k11_player = p ? !strcmp (p, "1") : K11_PLAYER_DEFAULT;
	if (g_k11_player) {
		setenv ("SHEPHERD_K11_PLAYER", "1", 0);
		setenv ("SHEPHERD_K7_BLACKBOX", "end", 0);
		setenv ("SHEPHERD_STACK_WATCH", "0", 0);
		setenv ("SHEPHERD_ICALL_HITS", "0", 0);
	} else
		setenv ("SHEPHERD_K11_PLAYER", "0", 1);   /* (integration review: the C half decides; the managed half (SimStats) follows it) */
	n += snprintf (rows + n, sizeof rows - (size_t) n, "; player build %s", g_k11_player ? (p ? "ON (SHEPHERD_K11_PLAYER=1: black box rings + dump only -- no live file, "
		       "so a hard crash leaves no trail --, no stack watch, no icall counters, statistics only in their window)" : "ON (default: black box rings + dump only, no stack "
		       "watch, no icall counters, statistics only in their window; SHEPHERD_K11_PLAYER=0 = kit #10's instruments)")
		       : p ? "off (as the env file says: every test instrument on)" : "off (default until its device A/B; SHEPHERD_K11_PLAYER=1 = the player build)");
#else
	setenv ("SHEPHERD_K11_PLAYER", "0", 1);   /* (integration review: not in this app -- the managed half follows) */
	n += snprintf (rows + n, sizeof rows - (size_t) n, "; player build: not in this app (a measurement / diagnostic app keeps every instrument)");
#endif
	(void) n;
	LOG ("K11 device defaults: %s; the boot screen: the K10 boot screen line (on by default only with an emblem set marked 'rings drawn' AND 'default on'). An env "
	     "file line wins; SHEPHERD_K11_DEVICE=0 = kit #10's defaults", rows);
}

/* ---- the emblem set (the GL apps: the boot screen is theirs) ---- */
#ifdef SHEPHERD_GL
#include <psp2/kernel/rng.h>
#include "k10_shb.h"
#define K11_SET_DIR DIR_ "/data/video/boot"
#define K11_SET_FILE K11_SET_DIR "/SET.txt"
/* device kit #12 (k12_boot.inc.c): the set's folder -- the boot-seq set (data/video/boot-seq: the same recordings with the controller icon in the
 * corner) when the owner's boot sequence holds the loading screen and the card has it; else this one (kit #11) */
static const char *g_k11_set_dir = K11_SET_DIR;
#define K11_SET_MAX 32
typedef struct {
	int present;                   /* SET.txt read and usable */
	int rings_drawn;               /* 'rings drawn': every recording passed the white-square check */
	int default_on;                /* 'default on': the owner's yes to this set (question 3; bootscreen_set.py make --default-on) */
	int n;
	char file [K11_SET_MAX][48], name [K11_SET_MAX][32];
	unsigned slots [K11_SET_MAX], checksum [K11_SET_MAX];
	unsigned set_fnv;              /* FNV-1a 32 of SET.txt: the timing key's recording (the set, not the one drawn) */
	int pick;                      /* the recording drawn this boot (-1 none) */
	char path [96];
	char why [160];
} K11Set;
static K11Set g_k11set;

/* the set's SET.txt (k10_bootscreen_start, before it decides): 0 = usable; the reason in s->why otherwise */
static int k11_set_read (K11Set *s)
{
	memset (s, 0, sizeof *s);
	s->pick = -1;
	const char *em = getenv ("SHEPHERD_K11_EMBLEMS");
	if (!g_k11_dev || (em && !strcmp (em, "0"))) {
		snprintf (s->why, sizeof s->why, "%s", !g_k11_dev ? "SHEPHERD_K11_DEVICE=0 (kit #10's single recording)" : "SHEPHERD_K11_EMBLEMS=0 (kit #10's single recording)");
		return -1;
	}
	void *data = NULL;
	unsigned size = 0;
	char setfile [96];
	snprintf (setfile, sizeof setfile, "%s/SET.txt", g_k11_set_dir);   /* (kit #12: the folder may be the boot-seq set's) */
	if (read_file (setfile, &data, &size) != 0 || !size) {
		snprintf (s->why, sizeof s->why, "no emblem set on the card (%s)", setfile);
		return -1;
	}
	char *t = (char *) data;   /* (read_file NUL-terminates) */
	if (size > 8192) {
		free (data);
		snprintf (s->why, sizeof s->why, "the emblem set's SET.txt is too large (%u B)", size);
		return -1;
	}
	s->set_fnv = k10shb_fnv32 (data, size);
	int ver = 0;
	for (char *line = t, *nl; line && *line; line = nl) {
		nl = strchr (line, '\n');
		if (nl) *nl++ = 0;
		if (*line == '#' || !*line) continue;
		char a [48], b [32];
		unsigned sl = 0, ck = 0;
		if (sscanf (line, "shb-set %d", &ver) == 1) continue;
		if (sscanf (line, "rings %15s", a) == 1) { s->rings_drawn = !strcmp (a, "drawn"); continue; }
		if (sscanf (line, "default %15s", a) == 1) { s->default_on = !strcmp (a, "on"); continue; }   /* (kit #11 review fix) */
		if (sscanf (line, "emblem %47s %31s %u %x", a, b, &sl, &ck) == 4 && s->n < K11_SET_MAX) {
			if (strchr (a, '/') || strstr (a, "..")) continue;   /* (a file inside the set's folder only) */
			snprintf (s->file [s->n], sizeof s->file [0], "%s", a);
			snprintf (s->name [s->n], sizeof s->name [0], "%s", b);
			s->slots [s->n] = sl ? sl : 1;
			s->checksum [s->n] = ck;
			s->n++;
		}
	}
	free (data);
	if (ver != 1 || !s->n) {
		snprintf (s->why, sizeof s->why, "the emblem set's SET.txt is not usable (version %d, %d recording(s))", ver, s->n);
		return -1;
	}
	s->present = 1;
	return 0;
}

/* one recording of the set for this boot: SHEPHERD_K11_EMBLEM=<name> or the console's random draw (equal odds; =game: the game's slots) */
static int k11_set_pick (K11Set *s)
{
	const char *force = getenv ("SHEPHERD_K11_EMBLEM");
	if (force && *force) {
		for (int i = 0; i < s->n; i++)
			if (!strcmp (s->name [i], force)) { s->pick = i; break; }
		if (s->pick < 0) {
			snprintf (s->why, sizeof s->why, "SHEPHERD_K11_EMBLEM=%.32s is not in the set", force);
			return -1;
		}
	} else {
		const char *odds = getenv ("SHEPHERD_K11_EMBLEM_ODDS");
		int game = odds && !strcmp (odds, "game");
		unsigned total = 0;
		for (int i = 0; i < s->n; i++) total += game ? s->slots [i] : 1u;
		unsigned r = 0;
		if (sceKernelGetRandomNumber (&r, sizeof r) < 0)
			r = (unsigned) sceKernelGetProcessTimeWide () * 2654435761u;   /* (no generator: the clock's low bits, still never the game's) */
		unsigned x = r % total;
		for (int i = 0; i < s->n; i++) {
			unsigned w = game ? s->slots [i] : 1u;
			if (x < w) { s->pick = i; break; }
			x -= w;
		}
	}
	snprintf (s->path, sizeof s->path, "%s/%s", g_k11_set_dir, s->file [s->pick]);
	return 0;
}
#endif /* SHEPHERD_GL */
