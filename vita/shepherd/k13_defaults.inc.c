/* SHEPHERD-VITA device kit #13, the integration (vita/notes/kit13-integration.md section 6): THE KIT #13 DEFAULTS TABLE.
 * Included by host.c after k12_defaults.inc.c; k13_defaults_config () runs in main right after the kit #12 defaults table, before any kit #13
 * row reads its switch and before k10_fp_config () reads SHEPHERD_K10_FPSCR. It replaces the speed track's own table (k13_speed_defaults.inc.c,
 * one row: FloatPc), as the speed note asked once there is one kit #13 table.
 *
 * The owner's rule (kit #12, unchanged for kit #13 -- COORDINATOR-NOTES section 3): what helps goes ON by default, each with a switch back and
 * an OFF arm in the run order where the device must confirm it; features with device evidence of harm stay OFF. Most kit #13 rows are ON in
 * their own code already (bootc's, walkc's exact retention fixes, basec's, speed's RECTDIRTY / MASKS); this table holds the rows whose engine
 * default is OFF and that the owner's picks / the tracks' notes turn on. Each row is only a DEFAULT: setenv without overwrite, so a line in the
 * env file always wins (e.g. SHEPHERD_K13_PERSP=0 turns that one row back to kit #12).
 *
 * Switches back:
 *   SHEPHERD_K13_DEFAULTS=0                  none of these rows (= every kit #13 row as its own code defaults it);
 *   the row's own track master =0            that track's rows off (SHEPHERD_K13_SPEED / SHEPHERD_K13_LOOKS), as before;
 *   the row's own variable in the env file   that row alone.
 *
 * Rows:
 *   all        SHEPHERD_K10_FPSCR=pc        speed: the PC's float rules (FloatPc; the owner's 2026-11-05 pick: identical on the walk in kit #11 at
 *                                           no speed cost). Device OFF arm: the walk with SHEPHERD_K10_FPSCR=0. (Moved here from speed's table.)
 *   GL apps    SHEPHERD_K13_MASKFIX=1       looks: a UI draw's colour mask / stencil as its shader's pass uses them -- the fix for kit #12's
 *                                           device photos (the Settings background, the Verify Files bands); acts only with kit #12's stencil
 *                                           masks (ON). Records change (fidelity).
 *   GL apps    SHEPHERD_K13_PERSP=1         looks: perspective-correct world draws (the Main Menu pentagram's shape matches the PC). Records change.
 *   GL apps    SHEPHERD_K13_WCWORLD=1       looks: the shrine's Watercolor-Sprite on world sprites (owner-local Derived). Records change.
 *   GL apps    SHEPHERD_K13_KEYART=1        looks: the menu crowd's KeyArt/Follower + BillboardDoodle programs (owner-local Derived; 219 sprite
 *                                           draws -> 1). Records change.
 *   GL apps    SHEPHERD_K13_LOGO=1          looks: the logo's LogoDesaturate (owner-local Derived). Records change.
 *   GL apps    SHEPHERD_K13_BARS=1          looks: Settings' Cutscene Bars offers only On (the owner's pick until every cutscene is checked).
 *   play eboot apps (00097 / 00098 / 00095; not the launchers, which have it with the boot sequence, nor Measure / 00011)
 *              SHEPHERD_K12_PRESSX=1        bootc / the kit stage: "Press X to Start" -- only Cross starts the title (the owner's report: the touch
 *                                           screen and the rear touchpad started the game; bootc traced the rear start to an eboot app, where
 *                                           kit #12 kept the game's any-button rule). The prompt reads "Press X to Start" (records change: the
 *                                           title's prompt). Switch back: SHEPHERD_K12_PRESSX=0; strict original keeps the game's rule.
 * Not here (OFF): looks' SDF (TMP's exact distance field: every text pixel moves; a LOOK step + a README question -- the owner decides) and
 * CAMUI (experiment); walkc's asset release (frees ~200 records on the intro chain, +1-2 MiB peak) and IL in VRAM (a device arm); gpu1's rows,
 * MASKDETACH, the GC arms, Frame Pacing (device arms only); bootc's one-cleanup-per-hold (opt-in).
 * One K13 defaults line says which rows are on, which the env file set and which their track master turned off. */

#ifndef SHP_K13_DEFAULTS_DEFAULT
#define SHP_K13_DEFAULTS_DEFAULT 1
#endif
typedef struct { const char *key, *val, *master; } K13Default;
static const K13Default k13_defaults [] = {
	{ "SHEPHERD_K10_FPSCR", "pc", "SHEPHERD_K13_SPEED" },
#ifdef SHEPHERD_GL
	{ "SHEPHERD_K13_MASKFIX", "1", "SHEPHERD_K13_LOOKS" },
	{ "SHEPHERD_K13_PERSP", "1", "SHEPHERD_K13_LOOKS" },
	{ "SHEPHERD_K13_WCWORLD", "1", "SHEPHERD_K13_LOOKS" },
	{ "SHEPHERD_K13_KEYART", "1", "SHEPHERD_K13_LOOKS" },
	{ "SHEPHERD_K13_LOGO", "1", "SHEPHERD_K13_LOOKS" },
	{ "SHEPHERD_K13_BARS", "1", "SHEPHERD_K13_LOOKS" },
#if defined (SHP_PLAY_APP) && !defined (SHP_K8_MODULE)
	{ "SHEPHERD_K12_PRESSX", "1", "SHEPHERD_K13_BOOTC" },
#endif
#endif
};

static void k13_defaults_config (void)
{
	const char *d = getenv ("SHEPHERD_K13_DEFAULTS");
	if (d ? !strcmp (d, "0") : !SHP_K13_DEFAULTS_DEFAULT) {
		LOG ("K13 defaults OFF (SHEPHERD_K13_DEFAULTS=0): every kit #13 row as its own code defaults it (FloatPc, looks' rows and the eboot apps' Press X off)");
		return;
	}
	char on [400], env [400], off [300];
	int n_on = 0, n_env = 0, n_off = 0;
	on [0] = env [0] = off [0] = 0;
	for (size_t i = 0; i < sizeof k13_defaults / sizeof k13_defaults [0]; i++) {
		const K13Default *r = &k13_defaults [i];
		const char *m = getenv (r->master), *was = getenv (r->key);
		if (was) {
			n_env += snprintf (env + n_env, sizeof env - (size_t) n_env, "%s%s=%s", n_env ? " " : "", r->key + 9, was);
			if (n_env >= (int) sizeof env) n_env = (int) sizeof env - 1;
			continue;
		}
		if (m && !strcmp (m, "0")) {
			n_off += snprintf (off + n_off, sizeof off - (size_t) n_off, "%s%s", n_off ? " " : "", r->key + 9);
			if (n_off >= (int) sizeof off) n_off = (int) sizeof off - 1;
			continue;
		}
		setenv (r->key, r->val, 0);
		n_on += snprintf (on + n_on, sizeof on - (size_t) n_on, "%s%s=%s", n_on ? " " : "", r->key + 9, r->val);
		if (n_on >= (int) sizeof on) n_on = (int) sizeof on - 1;
	}
	LOG ("K13 defaults: on %s | the env file set %s | off with their track master %s (SHEPHERD_K13_DEFAULTS=0 = none of them)",
	     n_on ? on : "none", n_env ? env : "none", n_off ? off : "none");
}
