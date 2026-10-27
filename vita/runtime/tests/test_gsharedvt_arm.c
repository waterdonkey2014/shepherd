/* SHEPHERD-VITA: Mac-side check of the armhf gsharedvt transition (mini-arm-gsharedvt.c + tramp-arm-gsharedvt.c).
 *
 * Links the CROSS build's libmonosgen-2.0.a: host (arm64) code, but the ARM hard-float target ABI, so
 * mono_arch_get_call_info is the same AAPCS-VFP classification the Vita runtime uses, and mono_arm_start_gsharedvt_call
 * is the patched (TARGET_VITA) argument copier. The test builds the M4b signatures by hand (float/double/T, no corlib
 * needed), lets mono_arch_get_gsharedvt_call_info describe the transition, then runs the copier on synthetic register
 * files and checks every destination register. Slots are host gpointers, so only register (not stack) cases are used.
 *
 * The same checks also run against the UPSTREAM copier (tramp-arm-gsharedvt.c from git HEAD, renamed by run-tests.sh)
 * and must FAIL there: that proves the checks see the d-register bug the fork fixes.
 */
#include <config.h>
#include <glib.h>
#include <string.h>
#include <stdio.h>

#include "mini.h"
#include "mini-arm.h"
#include <mono/metadata/loader-internals.h>

typedef gpointer (*StartFn) (GSharedVtCallInfo *info, gpointer *caller, gpointer *callee, gpointer mrgctx_reg,
			     double *caller_fregs, double *callee_fregs);

gpointer upstream_arm_start_gsharedvt_call (GSharedVtCallInfo *info, gpointer *caller, gpointer *callee, gpointer mrgctx_reg,
					    double *caller_fregs, double *callee_fregs);

static MonoType t_r4, t_r8, t_i4, t_obj, t_vt, t_T;
static MonoGenericParam gp_T;
static MonoMemoryManager mm;
static int failures;

static MonoMethodSignature *
make_sig (MonoType *ret, int hasthis, int n, ...)
{
	MonoMethodSignature *sig = g_malloc0 (MONO_SIZEOF_METHOD_SIGNATURE + n * sizeof (MonoType *));
	va_list ap;
	va_start (ap, n);
	for (int i = 0; i < n; ++i)
		sig->params [i] = va_arg (ap, MonoType *);
	va_end (ap);
	sig->ret = ret;
	sig->param_count = n;
	sig->hasthis = hasthis;
	sig->sentinelpos = -1;
	return sig;
}

typedef struct {
	gpointer caller [64], callee [64];
	double caller_fregs [8], callee_fregs [8];
} Regs;

static float *S (double *fregs) { return (float *) fregs; }   /* s-register view of d0-d7 */

static void
check (const char *impl, const char *what, gboolean ok, int *bad)
{
	if (!ok) {
		(*bad)++;
		printf ("   %-9s FAIL %s\n", impl, what);
	}
}

static GSharedVtCallInfo *
info_for (MonoMethodSignature *normal, MonoMethodSignature *gsig, gboolean in)
{
	return mono_arch_get_gsharedvt_call_info (&mm, (gpointer) 0x1000, normal, gsig, in, -1, FALSE);
}

/* returns the number of failed checks for one copier implementation */
static int
run_cases (const char *impl, StartFn start)
{
	int bad = 0;
	Regs r;
	GSharedVtCallInfo *info;
	float a = 1.5f, b = 2.5f;

	/* 1. IN  Pick<float>: normal (this, float a, float b, int second) -> float
	 *        gsharedvt (this, [vret], T& a, T& b, int second) -> T: b sits in odd s1 */
	info = info_for (make_sig (&t_r4, 1, 3, &t_r4, &t_r4, &t_i4), make_sig (&t_T, 1, 3, &t_T, &t_T, &t_i4), TRUE);
	memset (&r, 0, sizeof (r));
	r.caller [0] = (gpointer) 0x7777; r.caller [1] = GINT_TO_POINTER (1);
	S (r.caller_fregs) [0] = 1.5f; S (r.caller_fregs) [1] = 2.5f;
	start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
	check (impl, "pick-in: ret marshal VFP_R4 + vret slot", info->ret_marshal == GSHAREDVT_RET_VFP_R4 && info->vret_slot != -1, &bad);
	check (impl, "pick-in: this in r0", r.callee [0] == (gpointer) 0x7777, &bad);
	check (impl, "pick-in: vret in r1", r.callee [1] == &r.callee [info->vret_slot], &bad);
	check (impl, "pick-in: T& a (r2) -> s0 = 1.5", r.callee [2] && *(float *) r.callee [2] == 1.5f, &bad);
	check (impl, "pick-in: T& b (r3) -> s1 = 2.5", r.callee [3] && *(float *) r.callee [3] == 2.5f, &bad);
	check (impl, "pick-in: int second on the stack", r.callee [4] == GINT_TO_POINTER (1), &bad);

	/* 2. IN  Mix<float>: normal (this, float a, double d, float c, object conv) -> double
	 *        a = s0, d = d1 (s2/s3), c back-filled into s1, conv = r1
	 *        gsharedvt (this, T& a, double d, float c, object conv): a = r1, d = d0, c = s2, conv = r2 */
	info = info_for (make_sig (&t_r8, 1, 4, &t_r4, &t_r8, &t_r4, &t_obj), make_sig (&t_r8, 1, 4, &t_T, &t_r8, &t_r4, &t_obj), TRUE);
	memset (&r, 0, sizeof (r));
	r.caller [0] = (gpointer) 0x7777; r.caller [1] = (gpointer) 0x8888;
	S (r.caller_fregs) [0] = 1.5f; S (r.caller_fregs) [1] = 0.25f; r.caller_fregs [1] = 2.25;
	start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
	check (impl, "mix-in: T& a (r1) -> 1.5", r.callee [1] && *(float *) r.callee [1] == 1.5f, &bad);
	check (impl, "mix-in: double d1 -> d0 = 2.25", r.callee_fregs [0] == 2.25, &bad);
	check (impl, "mix-in: back-filled float s1 -> s2 = 0.25", S (r.callee_fregs) [2] == 0.25f, &bad);
	check (impl, "mix-in: s3 untouched", S (r.callee_fregs) [3] == 0.0f, &bad);
	check (impl, "mix-in: conv r1 -> r2", r.callee [2] == (gpointer) 0x8888, &bad);

	/* 3. IN  Mix<double>: normal (this, double a, double d, float c, object) a = d0, d = d1, c = s4
	 *        gsharedvt (this, T& a, double d, float c, object): a = r1, d = d0, c = s2 */
	info = info_for (make_sig (&t_r8, 1, 4, &t_r8, &t_r8, &t_r4, &t_obj), make_sig (&t_r8, 1, 4, &t_T, &t_r8, &t_r4, &t_obj), TRUE);
	memset (&r, 0, sizeof (r));
	r.caller_fregs [0] = 2.0; r.caller_fregs [1] = 2.25; S (r.caller_fregs) [4] = 0.25f;
	start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
	check (impl, "mixd-in: T& a (r1) -> 2.0", r.callee [1] && *(double *) r.callee [1] == 2.0, &bad);
	check (impl, "mixd-in: double d1 -> d0 = 2.25", r.callee_fregs [0] == 2.25, &bad);
	check (impl, "mixd-in: float s4 -> s2 = 0.25", S (r.callee_fregs) [2] == 0.25f, &bad);

	/* 4. OUT Func<float,float,float>.Invoke from gsharedvt code (Combine<T>):
	 *        gsharedvt caller (this, [vret r1], T& a r2, T& b r3) -> T;  normal callee (this, float s0, float s1) -> float */
	info = info_for (make_sig (&t_r4, 1, 2, &t_r4, &t_r4), make_sig (&t_T, 1, 2, &t_T, &t_T), FALSE);
	memset (&r, 0, sizeof (r));
	r.caller [0] = (gpointer) 0x7777; r.caller [2] = &a; r.caller [3] = &b;
	start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
	check (impl, "combine-out: ret marshal VFP_R4 into vret r1", info->ret_marshal == GSHAREDVT_RET_VFP_R4 && info->vret_arg_reg == 1, &bad);
	check (impl, "combine-out: T& a -> s0 = 1.5", S (r.callee_fregs) [0] == 1.5f, &bad);
	check (impl, "combine-out: T& b -> s1 = 2.5", S (r.callee_fregs) [1] == 2.5f, &bad);

	/* 5. OUT Func<double,double,double>.Invoke: T& -> d0, d1 (doubles were already right upstream) */
	{
		double da = 1.25, db = 2.5;
		info = info_for (make_sig (&t_r8, 1, 2, &t_r8, &t_r8), make_sig (&t_T, 1, 2, &t_T, &t_T), FALSE);
		memset (&r, 0, sizeof (r));
		r.caller [2] = &da; r.caller [3] = &db;
		start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
		check (impl, "double-out: T&, T& -> d0 = 1.25, d1 = 2.5", r.callee_fregs [0] == 1.25 && r.callee_fregs [1] == 2.5, &bad);
	}

	/* 6. OUT Func<float,double>.Invoke from Mix<T>: (this, T& a r1) -> normal (this, float s0); reads exactly 4 bytes */
	{
		float one [2] = { 1.5f, 99.0f };
		info = info_for (make_sig (&t_r8, 1, 1, &t_r4), make_sig (&t_r8, 1, 1, &t_T), FALSE);
		memset (&r, 0, sizeof (r));
		r.caller [1] = &one [0];
		start (info, r.caller, r.callee, NULL, r.caller_fregs, r.callee_fregs);
		check (impl, "conv-out: T& -> s0 = 1.5, s1 untouched", S (r.callee_fregs) [0] == 1.5f && S (r.callee_fregs) [1] == 0.0f, &bad);
	}

	printf ("   %-9s %d failed check(s)\n", impl, bad);
	return bad;
}

int
main (void)
{
	mono_arch_init ();   /* arm_fpu = VFP_HARD (ARM_FPU_VFP_HARD build), eabi */
	if (!mono_arm_is_hard_float ()) {
		printf ("FAIL gsharedvt-arm: the cross library is not hard-float\n");
		return 1;
	}
	t_r4.type = MONO_TYPE_R4;
	t_r8.type = MONO_TYPE_R8;
	t_i4.type = MONO_TYPE_I4;
	t_obj.type = MONO_TYPE_OBJECT;
	t_vt.type = MONO_TYPE_VALUETYPE;
	gp_T.gshared_constraint = &t_vt;   /* what mini_is_gsharedvt_type () looks for (T_GSHAREDVT) */
	t_T.type = MONO_TYPE_MVAR;
	t_T.data.generic_param = &gp_T;
	mm._mp = mono_mempool_new ();
	mono_os_mutex_init (&mm.mp_mutex);

	int fixed_bad = run_cases ("fork", mono_arm_start_gsharedvt_call);
	int upstream_bad = run_cases ("upstream", upstream_arm_start_gsharedvt_call);
	if (fixed_bad == 0 && upstream_bad > 0) {
		printf ("PASS gsharedvt-arm (fork copier: all checks ok; upstream copier: %d failures, as expected)\n", upstream_bad);
		return 0;
	}
	printf ("FAIL gsharedvt-arm (fork: %d failures, upstream: %d)\n", fixed_bad, upstream_bad);
	return 1;
}
