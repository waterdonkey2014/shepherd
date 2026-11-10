/* SHEPHERD-VITA device kit #12, the integration (vita/notes/kit12-integration.md section 6b): THE KIT #12 DEFAULTS TABLE.
 * Included by host.c after k11_device.inc.c; k12_defaults_config () runs in main right after the env file and kit #11's defaults table, before
 * any kit #12 row reads its switch.
 *
 * The owner's rule for kit #12 (2026-11-04): "If these features help the game run better then add them to the kit 12 on by default". Every row
 * below was built by a kit #12 track behind an env switch and gated on the merged tree (kit12-integration.md section 8g: every route PASS END
 * at the device partitions, the record changes documented, no new per-frame interpreted method). Each row is only a DEFAULT: setenv without
 * overwrite, so a line in the env file always wins (e.g. SHEPHERD_K12_STENCIL=0 turns that one row back to kit #11).
 *
 * Switches back:
 *   SHEPHERD_K12_DEFAULTS=0                  none of these rows (= the integration's first gated defaults, c2: kit #11's records + the PS Vita
 *                                            Settings tab);
 *   the row's own track master =0            that track's rows off (SHEPHERD_K12_WALKA / _WALKB / _BOOT / _MENUSA), as before;
 *   the row's own variable in the env file   that row alone.
 *
 * Rows (what each helps with; the device A/B each needs is in the kit's README):
 *   play apps  SHEPHERD_K10_EVAC=90          sgen's major-block evacuation at 90 % (kit #11 X3 on the device: New Game green, no assertion, boot
 *                                            -2.4 s); walka's CONCPIN (default 1) marks a refused copy in place instead of sgen's assertion.
 *   play apps  SHEPHERD_K12_INTRO=1          walka's covered-frame cleanup: a full collection on the loading black hold at the big scene load,
 *                                            and (SHEPHERD_K12_INTRO_SCENES, default on with it) after every non-additive scene change once the
 *                                            old scene is gone -- what the Main Menu leaves behind is collected before the next scene builds.
 *   all        SHEPHERD_K12_SEQPOINTS=0      walkb: no IL sequence-point tables in the interpreter (-1.2 MiB of C heap on the walk; stack traces
 *                                            lose their IL offsets only). Ignored in the JIT app (its own line says so).
 *   all        SHEPHERD_K12_VPKILZ=1         walkb: the VPK's DLLs read in compressed-IL form when the VPK carries managed-ilz/ (-3.3..-3.5 MiB
 *                                            of IL in newlib; each pair CRC-checked; a VPK without managed-ilz/ = the originals).
 *   GL apps    SHEPHERD_K12_STENCIL=1        boot: uGUI stencil masks (the original's round masks) -- a fidelity fix; records change.
 *   GL apps    SHEPHERD_K12_LOOKS=1          boot: the original's shaders on the loading screen and the masked UI (owner-local Derived) -- a
 *                                            fidelity fix; records change.
 *   GL apps    SHEPHERD_K12_PLUGINS=1        boot: the MISSING recommended plugins' names on the loading screen (nothing drawn when none miss).
 *   GL apps    SHEPHERD_K12_PLUGINS_SET=1    boot -> menusb: the same detection handed to the PS Vita tab's plugins row.
 *   launchers  SHEPHERD_K12_BOOTSEQ=1        boot: the owner's boot sequence (Devolver -> Massive Monster -> the loading screen until the Main Menu;
 *                                            Press X to Start with it). The launcher reads its own default (launcher.c); a start without the
 *                                            boot-seq set or the clip falls back to kit #11's boot (one K12 line says why).
 *   GL apps    SHEPHERD_K12_TEXTCACHE=1      menusa: the text-mesh cache (exact).
 *   GL apps    SHEPHERD_K12_COLPATCH=1       menusa: only changed colour bytes patched (exact).
 *   GL apps    SHEPHERD_K12_RAYMEMO=1        menusa: UI raycasts reused while nothing moves (exact).
 *   GL apps    SHEPHERD_K11_WATERCOLOR=1     kit #11's white-panels fix (the original's watercolour panels; ran clean on the device in kit #11) --
 *                                            a fidelity fix; records change.
 * Not here (kept OFF, device evidence or no gain): gpu1's rows (X1 slower), IL in VRAM (X2 boot +9.1 s), the player build, UIREPLAY.
 * One K12 defaults line says which rows are on and which the env file set. */

#ifndef SHP_K12_DEFAULTS_DEFAULT
#define SHP_K12_DEFAULTS_DEFAULT 1
#endif
typedef struct { const char *key, *val, *master; } K12Default;
static const K12Default k12_defaults [] = {
#ifdef SHP_PLAY_APP
	{ "SHEPHERD_K10_EVAC", "90", "SHEPHERD_K12_WALKA" },
	{ "SHEPHERD_K12_INTRO", "1", "SHEPHERD_K12_WALKA" },
#endif
	{ "SHEPHERD_K12_SEQPOINTS", "0", "SHEPHERD_K12_WALKB" },
	{ "SHEPHERD_K12_VPKILZ", "1", "SHEPHERD_K12_WALKB" },
#ifdef SHEPHERD_GL
	{ "SHEPHERD_K12_STENCIL", "1", "SHEPHERD_K12_BOOT" },
	{ "SHEPHERD_K12_LOOKS", "1", "SHEPHERD_K12_BOOT" },
	{ "SHEPHERD_K12_PLUGINS", "1", "SHEPHERD_K12_BOOT" },
	{ "SHEPHERD_K12_PLUGINS_SET", "1", "SHEPHERD_K12_BOOT" },
#ifdef SHP_K8_MODULE
	{ "SHEPHERD_K12_BOOTSEQ", "1", "SHEPHERD_K12_BOOT" },
#endif
	{ "SHEPHERD_K12_TEXTCACHE", "1", "SHEPHERD_K12_MENUSA" },
	{ "SHEPHERD_K12_COLPATCH", "1", "SHEPHERD_K12_MENUSA" },
	{ "SHEPHERD_K12_RAYMEMO", "1", "SHEPHERD_K12_MENUSA" },
	{ "SHEPHERD_K11_WATERCOLOR", "1", "SHEPHERD_K12_MENUSA" },
#endif
};

static void k12_defaults_config (void)
{
	const char *d = getenv ("SHEPHERD_K12_DEFAULTS");
	if (d ? !strcmp (d, "0") : !SHP_K12_DEFAULTS_DEFAULT) {
		LOG ("K12 defaults OFF (SHEPHERD_K12_DEFAULTS=0): the integration's first gated defaults -- kit #11's rows + the PS Vita Settings tab, every kit #12 opt-in row opt-in");
		return;
	}
	char on [600], env [400], off [300];
	int n_on = 0, n_env = 0, n_off = 0;
	on [0] = env [0] = off [0] = 0;
	for (size_t i = 0; i < sizeof k12_defaults / sizeof k12_defaults [0]; i++) {
		const K12Default *r = &k12_defaults [i];
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
	LOG ("K12 defaults: on %s | the env file set %s | off with their track master %s (SHEPHERD_K12_DEFAULTS=0 = none of them)",
	     n_on ? on : "none", n_env ? env : "none", n_off ? off : "none");
}
