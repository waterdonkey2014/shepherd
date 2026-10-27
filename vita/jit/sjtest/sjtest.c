/*
 * sjtest.c: SHEPHERD-VITA device kit #8 (vita/notes/kit8-jit.md) -- the baseline JIT tier's random-program LOCKSTEP test.
 *
 * Built with the JIT runtime's own compile command (it #includes the runtime's shepjit.c: the very trace compiler, stencil table and
 * interpreter walker the app links) and run under vlx (vita/jit/sjtest/run.sh: a static ARM32 ELF on the vlx shim, whose ForVM model
 * enforces the console's rules: the block writable only while the domain is open, Open not nestable, Sync / Close only by the opener).
 * For each random program -- 4..48 opcodes drawn from every compiled opcode that touches only the frame (moves, constants, integer and
 * float arithmetic incl. the dividing ones, shifts, conversions, compares, all branch forms, safepoints, spans, LDLOCA_S, MOV_VT,
 * INITLOCALS), with random operands, forward branches anywhere and one counted loop back to the start -- it runs the interpreter's walker
 * (the interpreter's own opcode bodies) on one copy of a random frame and the compiled trace on another, and compares the exit point and
 * every byte of the frame (both run on the same frame memory, one after the other, so a pointer into the frame is the same). The frames
 * start with edge values (0, +-1, INT_MIN/MAX, NaN, +-inf, -0, denormals) as well as random bits; divisions by zero and INT_MIN / -1 make
 * both leave at that instruction.
 * HEAP MODE (review fix, kit8-jit.md "Review fixes": the in-game lockstep compiles no store, so the store stencils the app ships were never
 * compared with the interpreter): "<programs> <seed> heap" adds every compiled opcode that reads or writes through a pointer -- field /
 * static / array / indirect loads and stores (incl. the write-barrier ones, through the harness's barriers), the VT forms, LDLEN, CKNULL,
 * LDFLDA, LDOBJ / STOBJ, CPBLK, INITBLK -- with their pointers aimed at a harness heap (an object, an array of 6, 16 statics behind a
 * data-item table, a pointer area; null now and then, indices out of range now and then, a class not yet initialised now and then); the
 * heap is restored between the trace and the walker like the frame and compared byte for byte too. Left out: LDSTR / STRLEN / GETCHR
 * (they need a real string). The in-game lockstep (SHEPHERD_JIT=lockstep) and the routes cover the rest.
 * usage (vlx): ux0:data/sjtest.txt = "<programs> <seed> [heap]" (run.sh writes it); prints one summary line ("SJTEST PASS" / "SJTEST FAIL ...").
 */
#define SJ_HARNESS 1
#include "shepjit.c"

unsigned int sceUserMainThreadStackSize = 1024 * 1024;
unsigned int _newlib_heap_size_user = 24 * 1024 * 1024;
extern int sceKernelAllocMemBlockForVM (const char *name, unsigned int size);
extern int sceKernelGetMemBlockBase (int uid, void **base);

/* the harness never runs a write barrier or polls a real GC: these stand in for the runtime's (both tiers call the same symbols) */
volatile size_t mono_polling_required;
void mono_gc_wbarrier_set_field_internal (MonoObject *obj, gpointer field_ptr, MonoObject *value) { *(MonoObject **) field_ptr = value; }
void mono_gc_wbarrier_generic_store_internal (void volatile *ptr, MonoObject *value) { *(MonoObject *volatile *) ptr = value; }

/* (vlx ends the process without flushing stdio: every line goes out at once) */
static void
sj_out (const char *fmt, ...)
{
	va_list ap;
	va_start (ap, fmt);
	vfprintf (stdout, fmt, ap);
	va_end (ap);
	fflush (stdout);
}

static guint32 rng_s;
static guint32 rnd (void) { rng_s ^= rng_s << 13; rng_s ^= rng_s >> 17; rng_s ^= rng_s << 5; return rng_s; }

#define NSLOT 16                 /* value slots: 8 bytes each, offsets 0..120 */
#define LOOP_SLOT (NSLOT * 8)    /* the loop counter (never a random destination) */
#define ZERO_SLOT (LOOP_SLOT + 8)
#define SPAN_SLOT (ZERO_SLOT + 8)   /* a span (reference, length) for GETITEM_LOCALSPAN */
#define FRAME (SPAN_SLOT + 16 + 64)
/* heap mode: pointer / index / offset / size slots after the span (never a random destination either) */
#define OBJ_SLOT (SPAN_SLOT + 16)   /* -> the object (header 8 bytes, fields 8..255) */
#define ARR_SLOT (OBJ_SLOT + 8)     /* -> the array (MonoArray header, ARR_LEN elements of up to 8 bytes) */
#define PTR_SLOT (ARR_SLOT + 8)     /* -> the pointer area (offsets -64..+64 around it) */
#define IDX_SLOT (PTR_SLOT + 8)     /* an index 0..7 (6 and 7 out of range) */
#define OFF_SLOT (IDX_SLOT + 8)     /* an offset -64..+64, 8-aligned (STIND_OFFSET / LDIND_OFFSET) */
#define SIZE_SLOT (OFF_SLOT + 8)    /* a block size 0..64 (CPBLK / INITBLK) */
#define HFRAME (SIZE_SLOT + 8 + 64)
#define HEAP_SIZE 2048
#define H_OBJ 0
#define H_ARR 256
#define H_STATIC 512                /* 16 statics of 8 bytes: data items 2..17 */
#define H_PTR 1088
#define ARR_LEN 6
static guint8 heap [HEAP_SIZE] __attribute__ ((aligned (8))), heap0 [HEAP_SIZE], heapb [HEAP_SIZE];
static guint8 vt_mem [2][sizeof (MonoVTable) + 64] __attribute__ ((aligned (8)));   /* [0] initialised, [1] not */
static gpointer items [2 + 16];

static int pure_ops [512], npure, heap_ops [256], nheap;

static gboolean
is_pure (int op)
{
	const char *n = mono_interp_opname (op);
	if (!n)
		return FALSE;
	if (sj_heap_store (op))
		return FALSE;
	/* the opcodes that read or write through a pointer operand (the heap, a string, a block) */
	static const char *const bad [] = { "ldfld", "ldsfld", "ldelem", "ldind", "ldobj", "ldlen", "cknull", "stfld", "stsfld", "stelem", "stind", "stobj",
					    "strlen", "getchr", "ldstr", "initblk", "cpblk", NULL };
	for (int i = 0; bad [i]; i++)
		if (!strncmp (n, bad [i], strlen (bad [i])))
			return FALSE;
	/* and every opcode with a data-item hole (a pointer the harness has no table for) */
	const SjStencil *st = &sj_stencils [sj_stencil_of [op]];
	for (int h = 0; h < st->nholes; h++)
		if (st->holes [h].kind == SJH_DATA_LO || st->holes [h].kind == SJH_DATA_HI)
			return FALSE;
	return TRUE;
}

/* a random value slot offset */
static guint16 vslot (void) { return (guint16) ((rnd () % NSLOT) * 8); }

/* heap mode: the element / field size of a load or store (0 = not a heap opcode the harness drives) */
static int
heap_esize (int op)
{
	switch (op) {
	case MINT_STFLD_I1: case MINT_STFLD_U1: case MINT_LDFLD_I1: case MINT_LDFLD_U1: case MINT_LDSFLD_I1: case MINT_LDSFLD_U1:
	case MINT_STSFLD_I1: case MINT_STSFLD_U1: case MINT_LDELEM_I1: case MINT_LDELEM_U1: case MINT_STELEM_I1: case MINT_STELEM_U1:
	case MINT_LDIND_I1: case MINT_LDIND_U1: case MINT_LDIND_OFFSET_I1: case MINT_LDIND_OFFSET_U1: case MINT_LDIND_OFFSET_IMM_I1:
	case MINT_LDIND_OFFSET_IMM_U1: case MINT_STIND_I1: case MINT_STIND_OFFSET_I1: case MINT_STIND_OFFSET_IMM_I1:
		return 1;
	case MINT_STFLD_I2: case MINT_STFLD_U2: case MINT_LDFLD_I2: case MINT_LDFLD_U2: case MINT_LDSFLD_I2: case MINT_LDSFLD_U2:
	case MINT_STSFLD_I2: case MINT_STSFLD_U2: case MINT_LDELEM_I2: case MINT_LDELEM_U2: case MINT_STELEM_I2: case MINT_STELEM_U2:
	case MINT_LDIND_I2: case MINT_LDIND_U2: case MINT_LDIND_OFFSET_I2: case MINT_LDIND_OFFSET_U2: case MINT_LDIND_OFFSET_IMM_I2:
	case MINT_LDIND_OFFSET_IMM_U2: case MINT_STIND_I2: case MINT_STIND_OFFSET_I2: case MINT_STIND_OFFSET_IMM_I2:
		return 2;
	case MINT_STFLD_I4: case MINT_STFLD_R4: case MINT_STFLD_O: case MINT_LDFLD_I4: case MINT_LDFLD_R4: case MINT_LDFLD_O:
	case MINT_LDSFLD_I4: case MINT_LDSFLD_R4: case MINT_LDSFLD_O: case MINT_STSFLD_I4: case MINT_STSFLD_R4:
	case MINT_LDELEM_I4: case MINT_LDELEM_U4: case MINT_LDELEM_R4: case MINT_LDELEM_REF: case MINT_STELEM_I4: case MINT_STELEM_R4:
	case MINT_LDIND_I4: case MINT_LDIND_R4: case MINT_LDIND_OFFSET_I4: case MINT_LDIND_OFFSET_IMM_I4:
	case MINT_STIND_I4: case MINT_STIND_R4: case MINT_STIND_REF: case MINT_STIND_OFFSET_I4: case MINT_STIND_OFFSET_IMM_I4:
	case MINT_LDFLDA: case MINT_LDFLDA_UNSAFE: case MINT_CKNULL: case MINT_LDSFLDA: case MINT_LDLEN:
		return 4;
	case MINT_STFLD_I8: case MINT_STFLD_R8: case MINT_LDFLD_I8: case MINT_LDFLD_R8: case MINT_LDSFLD_I8: case MINT_LDSFLD_R8:
	case MINT_STSFLD_I8: case MINT_STSFLD_R8: case MINT_LDELEM_I8: case MINT_LDELEM_R8: case MINT_STELEM_I8: case MINT_STELEM_R8:
	case MINT_LDIND_I8: case MINT_LDIND_R8: case MINT_LDIND_OFFSET_I8: case MINT_LDIND_OFFSET_IMM_I8:
	case MINT_STIND_I8: case MINT_STIND_R8: case MINT_STIND_OFFSET_I8: case MINT_STIND_OFFSET_IMM_I8:
	case MINT_STFLD_VT_NOREF: case MINT_LDFLD_VT: case MINT_LDELEM_VT: case MINT_LDELEMA1: case MINT_STELEM_VT_NOREF:
	case MINT_LDOBJ_VT: case MINT_STOBJ_VT_NOREF: case MINT_CPBLK: case MINT_INITBLK:
		return 8;
	}
	return 0;
}
static guint16 ptr_or_null (guint16 slot) { return rnd () % 16 ? slot : (guint16) ZERO_SLOT; }
/* a random multiple of align in [lo, hi] */
static int al (int lo, int hi, int align) { int n = (hi - lo) / align + 1; return lo + (int) (rnd () % (unsigned) n) * align; }

/* heap mode: one load / store with its pointers aimed at the harness heap; returns its length */
static int
emit_heap_op (guint16 *code, int pos, int op)
{
	int len = mono_interp_oplen [op], sz = heap_esize (op);
	guint16 *c = code + pos;
	c [0] = (guint16) op;
	for (int i = 1; i < len; i++)
		c [i] = vslot ();
	guint16 vsize = (guint16) (1 + rnd () % 8);
	switch (op) {
	case MINT_STFLD_I1: case MINT_STFLD_U1: case MINT_STFLD_I2: case MINT_STFLD_U2: case MINT_STFLD_I4: case MINT_STFLD_I8:
	case MINT_STFLD_R4: case MINT_STFLD_R8: case MINT_STFLD_O:
		c [1] = ptr_or_null (OBJ_SLOT); c [3] = (guint16) al (8, 240, sz); break;
	case MINT_STFLD_VT_NOREF:
		c [1] = ptr_or_null (OBJ_SLOT); c [3] = (guint16) al (8, 240, 1); c [4] = vsize; break;
	case MINT_LDFLD_I1: case MINT_LDFLD_U1: case MINT_LDFLD_I2: case MINT_LDFLD_U2: case MINT_LDFLD_I4: case MINT_LDFLD_I8:
	case MINT_LDFLD_R4: case MINT_LDFLD_R8: case MINT_LDFLD_O: case MINT_LDFLDA:
		c [2] = ptr_or_null (OBJ_SLOT); c [3] = (guint16) al (8, 240, sz); break;
	case MINT_LDFLDA_UNSAFE:
		c [2] = rnd () % 2 ? OBJ_SLOT : PTR_SLOT; c [3] = (guint16) al (0, 240, 4); break;
	case MINT_LDFLD_VT:
		c [2] = ptr_or_null (OBJ_SLOT); c [3] = (guint16) al (8, 240, 1); c [4] = vsize; break;
	case MINT_CKNULL:
		c [2] = ptr_or_null (OBJ_SLOT); break;
	case MINT_LDSFLD_I1: case MINT_LDSFLD_U1: case MINT_LDSFLD_I2: case MINT_LDSFLD_U2: case MINT_LDSFLD_I4: case MINT_LDSFLD_I8:
	case MINT_LDSFLD_R4: case MINT_LDSFLD_R8: case MINT_LDSFLD_O: case MINT_LDSFLDA:
	case MINT_STSFLD_I1: case MINT_STSFLD_U1: case MINT_STSFLD_I2: case MINT_STSFLD_U2: case MINT_STSFLD_I4: case MINT_STSFLD_I8:
	case MINT_STSFLD_R4: case MINT_STSFLD_R8:
		c [2] = (guint16) (rnd () % 16 ? 0 : 1); c [3] = (guint16) (2 + rnd () % 16); break;
	case MINT_LDLEN:
		c [2] = ptr_or_null (ARR_SLOT); break;
	case MINT_LDELEM_I1: case MINT_LDELEM_U1: case MINT_LDELEM_I2: case MINT_LDELEM_U2: case MINT_LDELEM_I4: case MINT_LDELEM_U4:
	case MINT_LDELEM_I8: case MINT_LDELEM_R4: case MINT_LDELEM_R8: case MINT_LDELEM_REF:
		c [2] = ptr_or_null (ARR_SLOT); c [3] = IDX_SLOT; break;
	case MINT_LDELEMA1: case MINT_LDELEM_VT:
		c [2] = ptr_or_null (ARR_SLOT); c [3] = IDX_SLOT; c [4] = vsize; break;
	case MINT_STELEM_I1: case MINT_STELEM_U1: case MINT_STELEM_I2: case MINT_STELEM_U2: case MINT_STELEM_I4: case MINT_STELEM_I8:
	case MINT_STELEM_R4: case MINT_STELEM_R8:
		c [1] = ptr_or_null (ARR_SLOT); c [2] = IDX_SLOT; break;
	case MINT_STELEM_VT_NOREF:
		c [1] = ptr_or_null (ARR_SLOT); c [2] = IDX_SLOT; c [4] = 0; c [5] = vsize; break;
	case MINT_LDIND_I1: case MINT_LDIND_U1: case MINT_LDIND_I2: case MINT_LDIND_U2: case MINT_LDIND_I4: case MINT_LDIND_I8:
	case MINT_LDIND_R4: case MINT_LDIND_R8:
		c [2] = ptr_or_null (PTR_SLOT); break;
	case MINT_LDIND_OFFSET_I1: case MINT_LDIND_OFFSET_U1: case MINT_LDIND_OFFSET_I2: case MINT_LDIND_OFFSET_U2:
	case MINT_LDIND_OFFSET_I4: case MINT_LDIND_OFFSET_I8:
		c [2] = ptr_or_null (PTR_SLOT); c [3] = OFF_SLOT; break;
	case MINT_LDIND_OFFSET_IMM_I1: case MINT_LDIND_OFFSET_IMM_U1: case MINT_LDIND_OFFSET_IMM_I2: case MINT_LDIND_OFFSET_IMM_U2:
	case MINT_LDIND_OFFSET_IMM_I4: case MINT_LDIND_OFFSET_IMM_I8:
		c [2] = ptr_or_null (PTR_SLOT); c [3] = (guint16) (gint16) al (-64, 64, sz); break;
	case MINT_STIND_REF: case MINT_STIND_I1: case MINT_STIND_I2: case MINT_STIND_I4: case MINT_STIND_I8: case MINT_STIND_R4: case MINT_STIND_R8:
		c [1] = ptr_or_null (PTR_SLOT); break;
	case MINT_STIND_OFFSET_I1: case MINT_STIND_OFFSET_I2: case MINT_STIND_OFFSET_I4: case MINT_STIND_OFFSET_I8:
		c [1] = ptr_or_null (PTR_SLOT); c [2] = OFF_SLOT; break;
	case MINT_STIND_OFFSET_IMM_I1: case MINT_STIND_OFFSET_IMM_I2: case MINT_STIND_OFFSET_IMM_I4: case MINT_STIND_OFFSET_IMM_I8:
		c [1] = ptr_or_null (PTR_SLOT); c [3] = (guint16) (gint16) al (-64, 64, sz); break;
	case MINT_LDOBJ_VT:
		c [2] = ptr_or_null (PTR_SLOT); c [3] = vsize; break;
	case MINT_STOBJ_VT_NOREF:   /* (no null check in either tier: never null) */
		c [1] = PTR_SLOT; c [3] = vsize; break;
	case MINT_CPBLK:            /* dest the pointer area, source the object: disjoint (memcpy) */
		c [1] = ptr_or_null (PTR_SLOT); c [2] = rnd () % 16 ? OBJ_SLOT : ZERO_SLOT; c [3] = SIZE_SLOT; break;
	case MINT_INITBLK:
		c [1] = ptr_or_null (PTR_SLOT); c [3] = SIZE_SLOT; break;
	}
	return len;
}

static guint64
edge64 (void)
{
	static const guint64 e [] = { 0, 1, 0xffffffffffffffffull, 0x80000000ull, 0x7fffffffull, 0xffffffff80000000ull, 0x8000000000000000ull, 0x7fffffffffffffffull,
				      0x7ff8000000000000ull /* NaN */, 0x7ff0000000000000ull /* inf */, 0xfff0000000000000ull, 0x8000000000000000ull /* -0 */,
				      0x0000000000000001ull /* denormal */, 0x3ff0000000000000ull /* 1.0 */, 0x7fc00000ull /* float NaN in the low word */,
				      0x7f800000ull, 0x00000001ull, 0x4f000000ull /* 2^31 as float */, 0xcf000000ull, 0x41dfffffffc00000ull /* 2^31-1 as double */ };
	switch (rnd () % 4) {
	case 0: return e [rnd () % G_N_ELEMENTS (e)];
	case 1: return (guint64) (gint64) (gint32) (rnd () % 33 - 16);
	default: return ((guint64) rnd () << 32) | rnd ();
	}
}

typedef struct { int pos; int len; int op; } Gen;

/* emit one random instruction at code [pos]; returns its length (0 = could not) */
static int
emit_op (guint16 *code, int pos, int op, int nops_left)
{
	int len = mono_interp_oplen [op];
	code [pos] = (guint16) op;
	for (int i = 1; i < len; i++)
		code [pos + i] = vslot ();
	MintOpArgType at = mono_interp_opargtype [op];
	switch (op) {
	case MINT_MOV_VT: code [pos + 1] = vslot (); code [pos + 2] = vslot (); code [pos + 3] = (guint16) (1 + rnd () % 8); return len;   /* (within the slots) */
	case MINT_INITLOCALS: code [pos + 1] = vslot (); code [pos + 2] = (guint16) (rnd () % 9); return len;
	case MINT_LDLOCA_S: code [pos + 1] = vslot (); code [pos + 2] = vslot (); return len;
	case MINT_GETITEM_LOCALSPAN: code [pos + 1] = vslot (); code [pos + 2] = SPAN_SLOT; code [pos + 3] = vslot (); code [pos + 4] = (guint16) (gint16) (rnd () % 33 - 16); return len;
	case MINT_INTRINS_SPAN_CTOR: code [pos + 1] = vslot (); code [pos + 2] = vslot (); code [pos + 3] = vslot (); return len;
	case MINT_NEWOBJ_VT_INLINED: code [pos + 1] = vslot (); code [pos + 2] = vslot (); code [pos + 3] = (guint16) (rnd () % 9); return len;
	}
	if (at == MintOpShortInt || at == MintOpUShortInt)
		code [pos + len - 1] = (guint16) rnd ();
	else if (at == MintOpInt || at == MintOpFloat) {
		guint32 v = (guint32) edge64 ();
		code [pos + len - 2] = (guint16) v; code [pos + len - 1] = (guint16) (v >> 16);
	} else if (at == MintOpLongInt || at == MintOpDouble) {
		guint64 v = edge64 ();
		for (int i = 0; i < 4; i++) code [pos + len - 4 + i] = (guint16) (v >> (16 * i));
	} else if (at == MintOpShortAndShortBranch) {
		code [pos + len - 2] = (guint16) (gint16) (rnd () % 9 - 4);   /* the immediate compared with */
	}
	/* shift immediates: keep them in range (an out-of-range shift is undefined in C; neither tier may run one) */
	const char *n = mono_interp_opname (op);
	if (strstr (n, "sh") && strstr (n, ".imm"))
		code [pos + 3] = (guint16) (rnd () % (strstr (n, "i8") ? 64 : 32));
	return len;
}

int
main (int argc, char **argv)
{
	/* (vlx passes neither arguments nor the environment: "<programs> <seed>" come from ux0:data/sjtest.txt, which run.sh writes) */
	int programs = 20000;
	unsigned seed = 12345u;
	char mode [16] = "";
	FILE *pf = fopen ("ux0:data/sjtest.txt", "r");
	if (pf) {
		int got = fscanf (pf, "%d %u %15s", &programs, &seed, mode);
		if (got < 2) { programs = 20000; seed = 12345u; }
		fclose (pf);
	}
	/* heap-inject: heap mode with a deliberate fault -- the field offset of the first STFLD_I4 in each trace is patched 4 bytes off after
	 * compiling (the control: the heap comparison must report mismatches) */
	gboolean inject = !strcmp (mode, "heap-inject");
	gboolean hmode = inject || !strcmp (mode, "heap");
	guint64 injected = 0;
	int fsize = hmode ? HFRAME : FRAME;
	rng_s = seed ? seed : 1;
	setenv ("SHEPHERD_JIT", "1", 1);
	int uid = sceKernelAllocMemBlockForVM ("sjtest-code", 4 * 1024 * 1024);
	void *base = NULL;
	if (uid < 0 || sceKernelGetMemBlockBase (uid, &base) < 0) { sj_out ("SJTEST FAIL no ForVM block (0x%08x)\n", uid); return 1; }
	mono_shepjit_set_block (uid, base, 4 * 1024 * 1024);
	mono_shepjit_init ();
	if (mono_shepjit_mode != 1) { sj_out ("SJTEST FAIL the tier did not initialise\n"); return 1; }
	sj_work_alloc ();
	/* the shift-by-register opcodes run C shifts whose count comes from a slot: a count >= the width is undefined in C, so the
	 * generator keeps those counts small (see below) -- the opcodes stay in */
	char left_out [256] = "";
	for (int op = 0; op < MINT_LASTOP; op++) {
		if (!sj_compiles (op))
			continue;
		if (is_pure (op)) {
			if (npure < (int) G_N_ELEMENTS (pure_ops))
				pure_ops [npure++] = op;
		} else if (hmode && heap_esize (op) && nheap < (int) G_N_ELEMENTS (heap_ops))
			heap_ops [nheap++] = op;
		else if (hmode && strlen (left_out) < sizeof left_out - 40)
			g_snprintf (left_out + strlen (left_out), sizeof left_out - strlen (left_out), " %s", mono_interp_opname (op));
	}
	static guint16 code [4096];
	static guint8 fa [HFRAME + 64], fb [HFRAME + 64], f0 [HFRAME + 64], fx [HFRAME + 64] __attribute__ ((aligned (8)));
	static guint8 buf [65536];
	InterpMethod im; MonoJitInfo ji; InterpFrame fr;
	memset (&im, 0, sizeof im); memset (&ji, 0, sizeof ji); memset (&fr, 0, sizeof fr);
	im.jinfo = &ji; im.alloca_size = (guint32) fsize; fr.imethod = &im;
	if (hmode) {
		((MonoVTable *) vt_mem [0])->initialized = 1;
		((MonoVTable *) vt_mem [1])->initialized = 0;
		items [0] = vt_mem [0]; items [1] = vt_mem [1];
		for (int i = 0; i < 16; i++)
			items [2 + i] = heap + H_STATIC + 8 * i;
		im.data_items = items;
	}
	guint64 heap_ops_total = 0;
	guint64 runs = 0, mism = 0, bails = 0, ends = 0, outs = 0, loops = 0, ops_total = 0, bytes_total = 0, windows = 0;
	guint32 used = 0;
	int shown = 0;
	SjTrace t;
	for (int p = 0; p < programs; p++) {
		int k = 4 + (int) (rnd () % 45);
		int pos = 0;
		code [pos++] = MINT_SJ_ENTER; code [pos++] = 0; code [pos++] = 0;
		/* the loop counter: 1..3 rounds */
		int starts [128], nst = 0;
		for (int i = 0; i < k; i++) {
			gboolean h = hmode && nheap && rnd () % 2;
			int op = h ? heap_ops [rnd () % nheap] : pure_ops [rnd () % npure];
			starts [nst++] = pos;
			int len = h ? emit_heap_op (code, pos, op) : emit_op (code, pos, op, k - i);
			/* no random destination may hit the loop counter / zero / span slots: value slots only (vslot) */
			pos += len;
		}
		starts [nst++] = pos;
		code [pos++] = MINT_SUB1_I4; code [pos++] = LOOP_SLOT; code [pos++] = LOOP_SLOT;
		starts [nst++] = pos;
		code [pos++] = MINT_BGT_I4_SP; code [pos++] = LOOP_SLOT; code [pos++] = ZERO_SLOT; code [pos] = (guint16) (gint16) (3 - pos + 3); pos++;   /* back to code [3] */
		int end = pos;
		code [pos++] = MINT_NIY;   /* the first opcode the tier does not compile: the trace's end */
		/* the branch offsets: forward, to an instruction start after it or to the end (an exit) */
		for (int s = 0; s < nst - 2; s++) {
			int at_ = starts [s], op = code [at_], len = mono_interp_oplen [op];
			MintOpArgType ty = mono_interp_opargtype [op];
			if (ty != MintOpShortBranch && ty != MintOpShortAndShortBranch && ty != MintOpBranch)
				continue;
			/* forward, to an instruction after it up to the loop's decrement, or to the end -- never straight to the back branch
			 * (that would skip the decrement: a loop the walker and the trace would both run forever) */
			int choice = (int) (rnd () % (nst - 1 - s));
			int target = s + 1 + choice < nst - 1 ? starts [s + 1 + choice] : end;
			if (rnd () % 8 == 0) target = end + 1;   /* sometimes beyond the trace: an exit stub */
			int off = target - at_;
			if (ty == MintOpBranch) { code [at_ + len - 2] = (guint16) off; code [at_ + len - 1] = (guint16) ((guint32) off >> 16); }
			else code [at_ + len - 1] = (guint16) (gint16) off;
		}
		ji.code_start = code; ji.code_size = (guint32) (pos * 2);
		memset (&t, 0, sizeof t);
		t.imethod = &im; t.ip = code; t.state = SJ_S_QUEUED;
		gsize addr = (gsize) base + used;
		if (used + 65536 > 4 * 1024 * 1024) used = 0, addr = (gsize) base;
		if (inject)
			memset (sj_w_map, 0, SJ_W_MAP * sizeof (SjMap));   /* (only this compile's entries, then NULL) */
		int n = sj_compile (&t, buf, sizeof buf, addr);
		if (n <= 0) continue;
		if (inject) {
			/* sj_w_map: the last compile's (ip, code offset) pairs */
			for (int mi = 0; mi < SJ_W_MAP && sj_w_map [mi].ip && sj_w_map [mi].ip < code + t.full_slots; mi++) {
				const guint16 *mip = sj_w_map [mi].ip;
				if (*mip != MINT_STFLD_I4)
					continue;
				const SjStencil *st = &sj_stencils [sj_stencil_of [MINT_STFLD_I4]];
				for (int h = 0; h < st->nholes; h++)
					if (st->holes [h].kind == SJH_A16 && st->holes [h].arg == 3) {
						sj_put_imm16 (buf + sj_w_map [mi].off + st->holes [h].off, (guint32) (mip [3] + 4));
						injected++;
					}
				break;
			}
		}
		if (SJ_OPEN () < 0 || (memcpy ((void *) addr, buf, (size_t) n), SJ_SYNC ((void *) addr, (unsigned) n)) < 0 || SJ_CLOSE () < 0) { sj_out ("SJTEST FAIL a ForVM window failed\n"); return 1; }
		windows++;
		used += (guint32) ((n + 7) & ~7);
		t.code = (gpointer) addr;
		ops_total += t.nops; bytes_total += (guint64) n;
		if (hmode)
			for (int s2 = 0; s2 < nst; s2++)
				if (starts [s2] < (int) t.full_slots && heap_esize (code [starts [s2]]))
					heap_ops_total++;
		for (int r = 0; r < 4; r++) {
			for (int i = 0; i < NSLOT; i++) { guint64 v = edge64 (); memcpy (f0 + i * 8, &v, 8); }
			guint32 rounds = 1 + rnd () % 3, zero = 0;
			memcpy (f0 + LOOP_SLOT, &rounds, 4); memset (f0 + LOOP_SLOT + 4, 0, 4); memcpy (f0 + ZERO_SLOT, &zero, 4); memset (f0 + ZERO_SLOT + 4, 0, 4);
			gpointer sref = (gpointer) (gsize) 0x10000; gint32 slen = (gint32) (rnd () % 8);
			memcpy (f0 + SPAN_SLOT, &sref, 4); memcpy (f0 + SPAN_SLOT + 4, &slen, 4);
			memset (f0 + SPAN_SLOT + 8, 0, fsize - SPAN_SLOT - 8);
			if (hmode) {
				/* the pointer / index / offset / size slots, and a fresh random heap (the array's header kept valid) */
				gpointer po = heap + H_OBJ, pa = heap + H_ARR, pp = heap + H_PTR;
				guint32 idx = rnd () % 8, size = rnd () % 65;
				gint32 off = (gint32) (rnd () % 17) * 8 - 64;
				memcpy (f0 + OBJ_SLOT, &po, 4); memcpy (f0 + ARR_SLOT, &pa, 4); memcpy (f0 + PTR_SLOT, &pp, 4);
				memcpy (f0 + IDX_SLOT, &idx, 4); memcpy (f0 + OFF_SLOT, &off, 4); memcpy (f0 + SIZE_SLOT, &size, 4);
				for (int i = 0; i < HEAP_SIZE; i += 4) { guint32 v = (guint32) edge64 (); memcpy (heap0 + i, &v, 4); }
				MonoArray *arr = (MonoArray *) (heap0 + H_ARR);
				arr->bounds = NULL; arr->max_length = ARR_LEN;
			}
			mono_polling_required = (rnd () % 16 == 0);
			/* both run on the SAME frame memory (and, in heap mode, the same heap), one after the other (a pointer into the frame --
			 * LDLOCA_S -- or the heap, and anything computed from it, is then the same in both): the trace first, its result saved, the
			 * frame and the heap restored, then the interpreter */
			memcpy (fx, f0, fsize);
			if (hmode) memcpy (heap, heap0, HEAP_SIZE);
			ptrdiff_t rt = sj_code_fn (&t) (&fr, fx, &t);
			memcpy (fb, fx, fsize);
			if (hmode) { memcpy (heapb, heap, HEAP_SIZE); memcpy (heap, heap0, HEAP_SIZE); }
			memcpy (fx, f0, fsize);
			const guint16 *wip = sj_walk (&fr, fx, code, code, code + t.full_slots);
			ptrdiff_t rw = (wip - code) * 2;
			memcpy (fa, fx, fsize);
			runs++;
			int diff = -1, hdiff = -1;
			for (int i = 0; i < fsize && diff < 0; i++)
				if (fa [i] != fb [i])
					diff = i & ~3;
			if (hmode)
				for (int i = 0; i < HEAP_SIZE && hdiff < 0; i++)
					if (heap [i] != heapb [i])
						hdiff = i & ~3;
			if (rw != rt || diff >= 0 || hdiff >= 0) {
				mism++;
				if (shown++ < 10) {
					sj_out ("MISMATCH program %d run %d: interpreter exit +%d (%s), trace exit +%d, frame byte %d: interpreter %08x trace %08x; heap byte %d: interpreter %08x trace %08x\n  ops:", p, r,
						(int) (rw / 2), wip < code + pos ? mono_interp_opname (*wip) : "?", (int) (rt / 2), diff, diff >= 0 ? *(guint32 *) (fa + diff) : 0, diff >= 0 ? *(guint32 *) (fb + diff) : 0,
						hdiff, hdiff >= 0 ? *(guint32 *) (heap + hdiff) : 0, hdiff >= 0 ? *(guint32 *) (heapb + hdiff) : 0);
					for (int s = 0; s < nst; s++) sj_out (" %d:%s", starts [s], mono_interp_opname (code [starts [s]]));
					sj_out ("\n");
				}
			}
			if (wip == code + end) ends++;
			else if (wip > code + end || wip < code) outs++;
			else bails++;
			if (rounds > 1) loops++;
		}
	}
	mono_polling_required = 0;
	sj_out ("SJTEST %s: %d programs, %llu runs (%llu ran the loop more than once), %llu mismatches; exits: %llu at the end, %llu branched out, %llu inside (a check / a safepoint); "
		"%d opcodes in the random set of %d compiled ones; %llu opcodes compiled, %llu bytes, %llu ForVM windows; seed %u\n",
		mism ? "FAIL" : "PASS", programs, (unsigned long long) runs, (unsigned long long) loops, (unsigned long long) mism, (unsigned long long) ends,
		(unsigned long long) outs, (unsigned long long) bails, npure + nheap, SJ_STENCIL_COUNT, (unsigned long long) ops_total, (unsigned long long) bytes_total,
		(unsigned long long) windows, (unsigned) seed);
	if (inject)
		sj_out ("SJTEST INJECT (control): %llu traces with a store patched 4 bytes off -> %llu mismatches (%s)\n", (unsigned long long) injected,
			(unsigned long long) mism, mism ? "detected, as it must be" : "NOT DETECTED: the heap comparison is broken");
	if (hmode)
		sj_out ("SJTEST heap mode: %d heap opcodes (loads / stores through a pointer) + %d frame-only ones; %llu heap opcodes compiled into the traces; heap compared byte for byte (%d bytes) after every run; left out:%s\n",
			nheap, npure, (unsigned long long) heap_ops_total, HEAP_SIZE, left_out [0] ? left_out : " none");
	return mism ? 1 : 0;
}
