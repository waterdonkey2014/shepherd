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
 * KIT #9 MODES (vita/notes/kit9-jit.md; kit #9's compiler sj_compile9 and walker sj_walk9 -- the walker stops wherever the trace does not
 * run the instruction itself, the trace's cmap): "k9" / "k9heap" = the programs above with a random RECORD (each instruction reached with
 * p = 7/8): unreached instructions become traps (an odd result), reached instructions the tier does not compile become exits the trace
 * runs past; the exit point, the frame (and heap) and the odd bit (= the exit is one of the trace's trap sites) are compared. "calls" /
 * "callsheap" = + MINT_JIT_CALL instructions run through a MOCK of the interpreter's handler body (the same helper pointer for the trace's
 * call sequence and the walker): it checks the frame / context / instruction it is handed, writes a result computed from the call's argument
 * slot into its return slot and sometimes asks for a resume state (the trace must leave with an odd result at the next instruction);
 * in callsheap two of the four callees are getter INTRINSICS (an instance field load from the harness object) -- the trace's stencil against
 * the mock's real-call load. "calls-inject" = the control: the call sequence's instruction constant patched 2 bytes off, which the mock must
 * see (mismatches reported).
 * PUBLISH MODES (kit #9 review fix, kit9-jit.md "Review fixes"): "publish" / "publishheap" drive the runtime's OWN path for every program
 * instead of calling the compiler directly -- sj_record (the interpreter's walker records SHEPHERD_JIT_RECORD runs on random frames and
 * queues the trace), sj_stage_veneers + sj_compile_one (the staged code and its pending_* tables), sj_publish (the Open / copy / Sync /
 * Close window and the switch to the new code), mono_shepjit_slow_exit on every trap exit (the site counted, its path recorded by the
 * walker for real, the trace requeued at the 4th exit), the RECOMPILE (the old code checked against the walker while the new one is staged
 * and before its window, the new code after it); the programs also hold MINT_SWITCH instructions (variable length: the compile must plan
 * past them). Windows FAIL on purpose -- the first one (the veneer island + the first trace), one in 53, and the first after each wrap of
 * the block -- and every framed trace's veneer island must be in the block (its 8 bytes compared) before it runs. "publish-inject" = the
 * control: after a failed window that held the veneer the harness puts back the first kit #9 build's state (the lost veneer's offset
 * kept), which the veneer check must catch. (Second review) The veneer check decodes the BL of EVERY call of the running code (from the
 * compiler's plan) and compares the 8 bytes at each BL's target -- not the island at the compiler's current offset; the block is poisoned
 * at each wrap (a stale island from before the wrap passed the old check); a recompile whose window fails must keep its old code, which
 * is then checked against the walker like any other (PASS needs such a case); every 8th recompile request meets a FULL block (shrunk for
 * that drain) and must keep its old code too, and an entry point that turns hot with the block full must be refused before its record.
 * usage (vlx): ux0:data/sjtest.txt = "<programs> <seed> [heap|heap-inject|k9|k9heap|calls|callsheap|calls-inject|publish|publishheap|publish-inject|publish-inject-wrap]" (run.sh writes it);
 * prints one summary line ("SJTEST PASS" / "SJTEST FAIL ...").
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

/* kit #9: the harness's stand-ins for the interpreter's MINT_JIT_CALL body (calls modes) */
#define NCALLEE 4
#define CALLEE_ITEM 18             /* data items 18..21: the callees (InterpMethod*) */
static MonoMethod *fake_method [NCALLEE];
static guint8 fake_method_mem [NCALLEE][64] __attribute__ ((aligned (8)));
static InterpMethod callee_im [NCALLEE];
static int callee_getter [NCALLEE];   /* 1 = an instance field getter (offset below) */
static guint16 callee_off [NCALLEE];
static InterpFrame *mock_frame;
static ThreadContext *mock_ctx;
static const guint16 *mock_code, *mock_code_end;
static guint64 mock_calls, mock_bad_args, mock_stops;
static int mock_sets_resume;   /* (publish modes) a resume stop also sets the context's resume state, as the interpreter's handler body does */
static int
mock_jit_call (InterpFrame *frame, ThreadContext *context, const guint16 *ip, unsigned char *locals)
{
	mock_calls++;
	gboolean bad = frame != mock_frame || context != mock_ctx || ip < mock_code || ip >= mock_code_end || *ip != MINT_JIT_CALL || locals != (unsigned char *) frame->stack;
	if (bad)
		mock_bad_args++;
	int c = (int) ip [3] - CALLEE_ITEM;
	if (c >= 0 && c < NCALLEE && callee_getter [c]) {
		/* the real call of an instance field getter: the field of 'this' (the walker never calls it with a null 'this') */
		gpointer o = LOCAL_VAR (ip [2], gpointer);
		LOCAL_VAR (ip [1], gint32) = o ? *(gint32 *) ((char *) o + callee_off [c]) : 0x0bad0bad;
		return 0;
	}
	/* a plain call: a result from the argument slot and the instruction's place; a resume state now and then */
	guint64 a = LOCAL_VAR (ip [2], guint64);
	guint64 r = a * 0x9e3779b97f4a7c15ull + (guint64) (ip - mock_code) * 0x100000001b3ull + (guint64) c;
	if (bad)
		r ^= 0xdeadbeefull;
	LOCAL_VAR (ip [1], guint64) = r;
	if (((a >> 7) & 15) == 5) {
		mock_stops++;
		if (mock_sets_resume)
			context->has_resume_state = 1;
		return 1;
	}
	return 0;
}

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
static gpointer items [2 + 16 + 4];

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

static guint8 fa [HFRAME + 64], fb [HFRAME + 64], f0 [HFRAME + 64], fx [HFRAME + 64] __attribute__ ((aligned (8)));

/* a random frame in f0 (and, in heap mode, a fresh random heap in heap0) + the safepoint flag; returns the loop's rounds (the same random
 * numbers, in the same order, as the run loop always drew them) */
static guint32
fill_frame (int fsize, gboolean hmode)
{
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
	return rounds;
}

/* (review 2) publish modes: the BL of every call a framed trace makes, as the compiler laid it out (its plan: the call sequence's BL is 14
 * bytes into it) -- for the staged code and for the code the trace runs. The veneer check decodes EACH of them and compares the 8 bytes at
 * its target with the island (the review's check compared the island at the compiler's current offset, which is not necessarily where a
 * trace's BL goes -- after a wrap the bytes at that offset were still the old island's). */
typedef struct { gpointer code; int n; guint32 off [SJ_W_MAP]; } BlList;
static BlList bl_pend, bl_cur;
static guint64 p_bl_count_bad, p_bl_lost, p_bls_checked;
/* a Thumb-2 BL (T1) at pc: its target; FALSE = not a BL */
static gboolean
bl_target (const guint8 *pc, gsize *target)
{
	guint16 h1 = rd16 (pc), h2 = rd16 (pc + 2);
	if ((h1 & 0xf800) != 0xf000 || (h2 & 0xd000) != 0xd000)
		return FALSE;
	guint32 S = (h1 >> 10) & 1, J1 = (h2 >> 13) & 1, J2 = (h2 >> 11) & 1;
	guint32 I1 = ~(J1 ^ S) & 1, I2 = ~(J2 ^ S) & 1;
	guint32 imm = (S << 24) | (I1 << 23) | (I2 << 22) | ((guint32) (h1 & 0x3ff) << 12) | ((guint32) (h2 & 0x7ff) << 1);
	if (S)
		imm |= 0xfe000000u;
	*target = (gsize) pc + 4 + (gsize) (gint32) imm;
	return TRUE;
}

/* publish modes: one run of t's CURRENT code (t->code, t->full_slots, t->x->cmap / traps -- whatever the last window left) against the walker
 * on a fresh random frame (and heap); returns 1 when exit point, frame, heap and odd bit agree. *trap = the trace left at one of its trap
 * sites (not a call's resume state): the caller then sends it through mono_shepjit_slow_exit on the walker's frame (fx), as MINT_SJ_ENTER
 * would. */
static guint64 pub_odd_wrong;
static int
pub_run (SjTrace *t, InterpFrame *fr, ThreadContext *ctx, const guint16 *code, int fsize, gboolean hmode, ptrdiff_t *rt_out, int *trap)
{
	fill_frame (fsize, hmode);
	memcpy (fx, f0, fsize);
	if (hmode) memcpy (heap, heap0, HEAP_SIZE);
	ctx->has_resume_state = 0;
	ptrdiff_t rt = sj_code_fn (t) (fr, fx, t, ctx);
	memcpy (fb, fx, fsize);
	if (hmode) { memcpy (heapb, heap, HEAP_SIZE); memcpy (heap, heap0, HEAP_SIZE); }
	memcpy (fx, f0, fsize);
	ctx->has_resume_state = 0;
	int wstop = 0;
	const guint16 *wip = sj_walk9 (fr, fx, ctx, t, code, &wstop);
	mono_polling_required = 0;
	gboolean is_trap = FALSE;
	for (int ti = 1; t->x->traps && ti <= t->x->traps [0].slot; ti++)
		if (t->x->traps [ti].slot == (guint16) (wip - code))
			is_trap = TRUE;
	int bad = 0;
	if (((rt & 1) != 0) != (wstop || is_trap)) {
		pub_odd_wrong++;
		bad = 1;
	}
	*trap = (rt & 1) && !wstop && is_trap;
	*rt_out = rt;
	if ((rt & ~(ptrdiff_t) 1) != (wip - code) * 2)
		bad = 1;
	for (int i = 0; i < fsize && !bad; i++)
		if (fx [i] != fb [i])
			bad = 1;
	for (int i = 0; hmode && i < HEAP_SIZE && !bad; i++)
		if (heap [i] != heapb [i])
			bad = 1;
	ctx->has_resume_state = 0;
	return !bad;
}

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
	char mode [32] = "";
	FILE *pf = fopen ("ux0:data/sjtest.txt", "r");
	if (pf) {
		int got = fscanf (pf, "%d %u %31s", &programs, &seed, mode);
		if (got < 2) { programs = 20000; seed = 12345u; }
		fclose (pf);
	}
	/* heap-inject: heap mode with a deliberate fault -- the field offset of the first STFLD_I4 in each trace is patched 4 bytes off after
	 * compiling (the control: the heap comparison must report mismatches) */
	gboolean inject = !strcmp (mode, "heap-inject");
	gboolean cinject = !strcmp (mode, "calls-inject");
	/* (review 2) publish-inject-wrap: the same control, but the lost island is put back only after the first window that fails after a
	 * WRAP of the block -- the case the review found the first harness could not see (the old island's bytes still at that offset) */
	gboolean pinject_wrap = !strcmp (mode, "publish-inject-wrap");
	gboolean pinject = pinject_wrap || !strcmp (mode, "publish-inject");
	gboolean pmode = pinject || !strcmp (mode, "publish") || !strcmp (mode, "publishheap");
	gboolean cmode = pmode || cinject || !strcmp (mode, "calls") || !strcmp (mode, "callsheap");
	gboolean k9 = cmode || !strcmp (mode, "k9") || !strcmp (mode, "k9heap");
	gboolean hmode = inject || !strcmp (mode, "heap") || !strcmp (mode, "k9heap") || !strcmp (mode, "callsheap") || !strcmp (mode, "publishheap");
	guint64 cinjected = 0, odd_wrong = 0, odd_runs = 0, trap_sites = 0, calls_compiled = 0, intrins_compiled = 0;
	guint64 injected = 0;
	int fsize = hmode ? HFRAME : FRAME;
	rng_s = seed ? seed : 1;
	setenv ("SHEPHERD_JIT", "1", 1);
	setenv ("SHEPHERD_JIT9", k9 ? "1" : "0", 1);
	setenv ("SHEPHERD_JIT_CALLS", cmode ? "1" : "0", 1);
	setenv ("SHEPHERD_JIT_INTRINSICS", cmode ? "1" : "0", 1);
	if (pmode)
		setenv ("SHEPHERD_JIT_MONITOR", "0", 1);   /* (published traces go LIVE at once: the slow exit records and requeues them) */
	int uid = sceKernelAllocMemBlockForVM ("sjtest-code", 4 * 1024 * 1024);
	void *base = NULL;
	if (uid < 0 || sceKernelGetMemBlockBase (uid, &base) < 0) { sj_out ("SJTEST FAIL no ForVM block (0x%08x)\n", uid); return 1; }
	mono_shepjit_set_block (uid, base, 4 * 1024 * 1024);
	mono_shepjit_set_call_helper (mock_jit_call);
	mono_shepjit_init ();
	if (mono_shepjit_mode != 1) { sj_out ("SJTEST FAIL the tier did not initialise\n"); return 1; }
	if (k9 && (!sj_k9 || (cmode && (!sj_calls || !sj_intrinsics)))) { sj_out ("SJTEST FAIL kit #9 switches did not take\n"); return 1; }
	sj_work_alloc ();
	sj_want_cmap = 1;
	guint32 used0 = 0;
	static guint8 ven [8];   /* the veneer island's 8 bytes, as the compiler thread stages them */
	wr16 (ven, 0xf8df); wr16 (ven + 2, 0xf000);
	{ guint32 a = (guint32) (gsize) mock_jit_call; memcpy (ven + 4, &a, 4); }
	if (pmode) {
		/* the compiler thread's buffers (sj_thread_start allocates them); the queue drained by the harness (no compiler thread) */
		sj_stage = (guint8 *) g_malloc (SJ_STAGE_CAP);
		sj_scratch = (guint8 *) g_malloc (SJ_SCRATCH_CAP);
		sj_batch = g_new0 (SjTrace *, SJ_BATCH_CAP);
		sj_thread_started = 1;
		mock_sets_resume = 1;
		/* every callee's getter verdict known up front (the recording walker asks for it: the fake methods have no metadata) */
		sj_getters = g_hash_table_new (NULL, NULL);
	}
	if (cmode) {
		if (!pmode) {
			/* the veneer island at the block's start, as the compiler thread stages it */
			if (SJ_OPEN () < 0 || (memcpy (base, ven, 8), SJ_SYNC (base, 8)) < 0 || SJ_CLOSE () < 0) { sj_out ("SJTEST FAIL the veneer window failed\n"); return 1; }
			sj_veneer_off = 0;
			used0 = 8;
		}
		for (int i = 0; i < NCALLEE; i++) {
			fake_method [i] = (MonoMethod *) fake_method_mem [i];
			callee_im [i].method = fake_method [i];
			if (pmode && !(hmode && i >= 2))
				g_hash_table_insert (sj_getters, fake_method [i], g_new0 (SjIntrinsic, 1));   /* (SJI_NONE: a real call) */
		}
		if (hmode) {
			/* callees 2 and 3: instance field getters of the harness object (int32 fields at 8 and 40) */
			if (!sj_getters)
				sj_getters = g_hash_table_new (NULL, NULL);
			for (int i = 2; i < 4; i++) {
				SjIntrinsic *g = g_new0 (SjIntrinsic, 1);
				g->kind = SJI_LDFLD; g->op = MINT_LDFLD_I4; g->offset = (guint16) (i == 2 ? 8 : 40);
				callee_getter [i] = 1; callee_off [i] = g->offset;
				g_hash_table_insert (sj_getters, fake_method [i], g);
			}
		}
	}
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
	static guint8 buf [65536];
	static guint8 seenbuf [SJ_SEEN_MAX / 8 + 8];
	InterpMethod im; MonoJitInfo ji; InterpFrame fr; ThreadContext ctx;
	memset (&im, 0, sizeof im); memset (&ji, 0, sizeof ji); memset (&fr, 0, sizeof fr); memset (&ctx, 0, sizeof ctx);
	im.jinfo = &ji; im.alloca_size = (guint32) fsize; fr.imethod = &im;
	fr.stack = (stackval *) fx;
	mock_frame = &fr; mock_ctx = &ctx;
	for (int i = 0; i < NCALLEE; i++)
		items [CALLEE_ITEM + i] = &callee_im [i];
	im.data_items = items;
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
	guint32 used = used0;
	int shown = 0;
	SjTrace t;
	SjTraceK9 tx;
	/* publish modes' counts */
	guint64 p_rec_runs = 0, p_recorded = 0, p_notqueued = 0, p_failed_progs = 0, p_short = 0, p_checked = 0, p_runs = 0, p_bad = 0, p_slow = 0;
	guint64 p_recomp_req = 0, p_recomp_pub = 0, p_recomp_failed = 0, p_recomp_kept = 0, p_old_runs = 0, p_new_runs = 0, p_veneer_missing = 0;
	guint64 p_requeued_bad = 0, p_wraps = 0, p_switches = 0, p_windows = 0, p_undone = 0, p_framed_checked = 0, p_recomp_keptfail = 0;
	int fail_next = 1;   /* the very first window fails: the veneer island + the first trace (the review's case) */
	/* (review 2) the full block: an entry point that turns hot then is refused at once (mono_shepjit_queue: OFF_FULL, its opcode NOP, no side
	 * struct, nothing queued); a recompile then keeps its old code (forced below on some rounds by shrinking the block for one drain) */
	guint64 p_keptfull = 0, p_keptfull_bad = 0;
	int p_queue_full_ok = 0;
	if (pmode) {
		static guint16 qcode [8] = { MINT_SJ_ENTRY, 0, 0, MINT_NIY };
		SjTrace qt;
		memset (&qt, 0, sizeof qt);
		qt.ip = qcode; qt.state = SJ_S_COUNTING; qt.threshold = 1;
		guint32 bu = sj_block_used, qh = sj_qtail;
		sj_block_used = sj_block_size - 100;
		mono_shepjit_queue (&qt);
		sj_block_used = bu;
		p_queue_full_ok = qt.state == SJ_S_OFF_FULL && qcode [0] == MINT_SJ_NOP && !qt.x && sj_qtail == qh;
	}
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
			if (pmode && rnd () % 10 == 0) {
				/* a MINT_SWITCH (variable length: 4 + 2 n slots; never compiled -- an exit where reached, a trap where not): its case offsets
				 * are set below, once the end is known (no walker runs one: both stop AT it) */
				guint32 n = 1 + rnd () % 3;
				code [pos] = MINT_SWITCH; code [pos + 1] = vslot (); code [pos + 2] = (guint16) n; code [pos + 3] = 0;
				pos += 4 + 2 * (int) n;
				p_switches++;
				continue;
			}
			if (cmode && rnd () % 6 == 0) {
				/* a JIT call: return slot, argument slot (heap mode, a getter: the object or null), the callee */
				int c = (int) (rnd () % (hmode ? NCALLEE : 2));
				code [pos] = MINT_JIT_CALL; code [pos + 1] = vslot ();
				code [pos + 2] = callee_getter [c] ? ptr_or_null (OBJ_SLOT) : vslot ();
				code [pos + 3] = (guint16) (CALLEE_ITEM + c);
				pos += 4;
				continue;
			}
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
		for (int s = 0; pmode && s < nst - 2; s++)
			if (code [starts [s]] == MINT_SWITCH) {
				int sw = starts [s], n = code [sw + 2];
				for (int c = 0; c < n; c++) {
					gint32 o = end - (sw + 4 + 2 * c);   /* (the interpreter: ip = sw + 4 + 2 * val, then ip += offset) */
					code [sw + 4 + 2 * c] = (guint16) o; code [sw + 5 + 2 * c] = (guint16) ((guint32) o >> 16);
				}
			}
		ji.code_start = code; ji.code_size = (guint32) (pos * 2);
		mock_code = code; mock_code_end = code + pos;
		memset (&t, 0, sizeof t);
		memset (&tx, 0, sizeof tx);
		t.x = &tx;
		t.imethod = &im; t.ip = code; t.state = SJ_S_QUEUED;
		if (pmode) {
			/* the runtime's own record: sj_record runs the interpreter's walker (for real) on random frames, marking what it reaches, until
			 * the SHEPHERD_JIT_RECORD-th run queues the trace */
			guint32 span = (guint32) MIN (pos, SJ_SEEN_MAX);
			memset (seenbuf, 0, sizeof seenbuf);
			t.x->seen = seenbuf; t.x->seen_span = (guint16) span;
			t.state = SJ_S_RECORD; t.fn = sj_record;
			code [0] = MINT_SJ_ENTER;
			for (int r = 0; r < 64 && t.state == SJ_S_RECORD; r++) {
				fill_frame (fsize, hmode);
				memcpy (fx, f0, fsize);
				if (hmode) memcpy (heap, heap0, HEAP_SIZE);
				ctx.has_resume_state = 0;
				sj_record (&fr, fx, &t, &ctx);
				p_rec_runs++;
			}
			mono_polling_required = 0;
			ctx.has_resume_state = 0;
			if (t.state != SJ_S_QUEUED || sj_qhead == sj_qtail) { p_notqueued++; sj_qhead = sj_qtail; continue; }
			p_recorded++;
			bl_pend.code = bl_cur.code = NULL;   /* (review 2) this program's trace: no BL list yet */
			/* the harness reuses the block: the allocator wraps before its end (the veneer island is staged again at its start; that window
			 * fails on purpose too) */
			if (sj_stage_base + 256 * 1024 > 4 * 1024 * 1024) {
				sj_stage_base = 0; sj_stage_used = 0; sj_block_used = 0; sj_veneer_off = 0xffffffffu;
				p_wraps++; fail_next = 1;
				/* (review 2) and poisons it: no byte from before the wrap may pass for one written after it (the old island at offset 0 did) */
				if (SJ_OPEN () < 0 || (memset (sj_block, 0xa5, 4 * 1024 * 1024), SJ_SYNC (sj_block, 4 * 1024 * 1024)) < 0 || SJ_CLOSE () < 0) {
					sj_out ("SJTEST FAIL the poisoning window failed\n");
					return 1;
				}
			}
			gboolean dead = FALSE;
			for (int round = 0; round < 4 && !dead; round++) {
				/* drain the queue as the compiler thread does, then ONE window */
				gpointer old_code = t.code;
				/* (review 2) every 8th recompile request: the block is full for this drain -- the recompile must keep the old code */
				gboolean force_full = round > 0 && sj_qhead != sj_qtail && p_recomp_req % 8 == 3;
				guint32 bs_save = sj_block_size;
				if (force_full)
					sj_block_size = sj_stage_base + sj_stage_used + 128;
				while (sj_qhead != sj_qtail) {
					SjTrace *q = sj_q [sj_qhead++ % SJ_QCAP];
					sj_stage_veneers ();
					gpointer pc0 = q->x ? q->x->pending_code : NULL;
					memset (sj_w_plan, 0, SJ_W_MAP * sizeof (SjPlan));   /* (review 2: this compile's plan only, then NULL) */
					sj_compile_one (q);
					if (q == &t && t.x->pending_code && t.x->pending_code != pc0) {
						/* (review 2) the staged code's calls: each SJD_CALL's sequence holds its BL 14 bytes in */
						bl_pend.code = t.x->pending_code; bl_pend.n = 0;
						for (int pi = 0; pi < SJ_W_MAP && sj_w_plan [pi].ip; pi++)
							if (sj_w_plan [pi].disp == SJD_CALL)
								bl_pend.off [bl_pend.n++] = sj_w_plan [pi].off + 14;
						if (bl_pend.n != t.x->ncalls)
							p_bl_count_bad++;
					}
				}
				if (force_full) {
					sj_block_size = bs_save;
					if (t.code == old_code && t.x->pending_code == old_code && (t.state == SJ_S_LIVE || t.state == SJ_S_MONITOR) &&
					    t.x->recompiles >= (guint8) sj_recompile_cap)
						p_keptfull++;
					else
						p_keptfull_bad++;
				}
				if (old_code && t.x->pending_code != old_code && t.state == SJ_S_QUEUED) {
					/* a recompile is staged: until its window the OLD code runs (its tables too) and must still agree; a trap exit now is only
					 * counted (the trace is queued: no second requeue) */
					for (int r = 0; r < 4; r++) {
						ptrdiff_t rt; int trap;
						if (!pub_run (&t, &fr, &ctx, code, fsize, hmode, &rt, &trap))
							p_bad++;
						if (trap) {
							mono_shepjit_slow_exit (&t, rt, &fr, fx, &ctx);
							if (sj_qhead != sj_qtail) { p_requeued_bad++; sj_qhead = sj_qtail; }
						}
						p_old_runs++;
					}
				}
				if (sj_batch_n || sj_stage_used) {
					p_windows++;
					if (fail_next || p_windows % 53 == 0) {
						sj_harness_fail_open = 1;   /* (this window's Open fails, as a refused Open on the console would) */
						fail_next = 0;
					}
				}
				guint32 restaged0 = sj_st.veneer_restaged, voff0 = sj_veneer_off, wfailed0 = sj_st.windows_failed;
				sj_publish ();
				sj_harness_fail_open = 0;
				gboolean wfail = sj_st.windows_failed != wfailed0;
				if (pinject && sj_st.veneer_restaged != restaged0 && (!pinject_wrap || p_wraps > 0)) {
					/* the control: the first kit #9 build's state after such a window -- the lost veneer's offset kept, nothing staged again */
					sj_veneer_off = voff0; sj_stage_used = 0; sj_block_used = sj_stage_base;
					p_undone++;
				}
				if (t.state == SJ_S_OFF_FULL) {
					if (round) p_recomp_failed++; else p_failed_progs++;   /* (its window failed: the trace is off, as on the console) */
					dead = TRUE;
					break;
				}
				if (!t.code || t.state == SJ_S_OFF_SHORT) {
					p_short++;
					dead = TRUE;
					break;
				}
				if (round) {
					if (t.code != old_code) p_recomp_pub++;
					else {
						p_recomp_kept++;
						if (wfail)
							p_recomp_keptfail++;   /* (review 2) its window failed: the old code runs on (and is checked below) */
					}
				}
				/* every framed trace's veneer island must be in the block before it runs (the review's case: a BL into bytes never written).
				 * (review 2) Each of its calls' BL is decoded and the 8 bytes at its target compared with the island -- the island that BL
				 * really goes to, not the one at the compiler's current offset */
				if (t.code == bl_pend.code && bl_cur.code != t.code)
					bl_cur = bl_pend;
				if (bl_cur.code != t.code) {
					p_bl_lost++;   /* (the harness lost track of the running code's calls: a failure of its own) */
					dead = TRUE;
					break;
				}
				if (bl_cur.n) {
					p_framed_checked++;
					gboolean miss = FALSE;
					for (int b = 0; b < bl_cur.n && !miss; b++) {
						gsize tg = 0;
						if (!bl_target ((const guint8 *) t.code + bl_cur.off [b], &tg) || tg < (gsize) sj_block ||
						    tg + 8 > (gsize) sj_block + 4 * 1024 * 1024 || memcmp ((const void *) tg, ven, 8))
							miss = TRUE;
						p_bls_checked++;
					}
					if (miss) {
						p_veneer_missing++;
						dead = TRUE;
						break;
					}
				}
				if (!round)
					p_checked++;
				/* the published code against the walker; a trap exit goes through the runtime's slow exit (counted, its path recorded, the
				 * trace requeued at the site's 4th exit) */
				for (int r = 0; r < 16; r++) {
					ptrdiff_t rt; int trap;
					if (!pub_run (&t, &fr, &ctx, code, fsize, hmode, &rt, &trap))
						p_bad++;
					if (trap) {
						mono_shepjit_slow_exit (&t, rt, &fr, fx, &ctx);
						p_slow++;
					}
					p_runs++;
					if (round) p_new_runs++;
				}
				ctx.has_resume_state = 0;
				if (sj_qhead == sj_qtail)
					break;   /* no recompile asked for */
				p_recomp_req++;
			}
			sj_qhead = sj_qtail;
			continue;
		}
		if (k9) {
			/* a random record: every instruction reached with p = 7/8 (slot 0, the entry opcode, always) */
			guint32 span = (guint32) MIN (pos, SJ_SEEN_MAX);
			memset (seenbuf, 0, sizeof seenbuf);
			seenbuf [0] = 1;
			for (int s2 = 0; s2 < nst; s2++)
				if ((guint32) starts [s2] < span && rnd () % 8)
					seenbuf [starts [s2] >> 3] |= (guint8) (1 << (starts [s2] & 7));
			if ((guint32) end < span && rnd () % 2)
				seenbuf [end >> 3] |= (guint8) (1 << (end & 7));
			t.x->seen = seenbuf; t.x->seen_span = (guint16) span;
		}
		gsize addr = (gsize) base + used;
		if (used + 65536 > 4 * 1024 * 1024) used = used0, addr = (gsize) base + used0;
		if (inject)
			memset (sj_w_map, 0, SJ_W_MAP * sizeof (SjMap));   /* (only this compile's entries, then NULL) */
		if (cinject)
			memset (sj_w_plan, 0, SJ_W_MAP * sizeof (SjPlan));
		int n = k9 ? sj_compile9 (&t, buf, sizeof buf, addr) : sj_compile (&t, buf, sizeof buf, addr);
		if (n <= 0) continue;
		/* (what the compiler thread's publish does once the window closed: the staged code's tables go live) */
		t.full_slots = t.x->pending_full_slots; t.x->traps = t.x->pending_traps; t.x->cmap = t.x->pending_cmap;
		calls_compiled += t.x->ncalls; intrins_compiled += t.x->nintrins; trap_sites += t.x->ntraps;
		if (cinject && t.x->ncalls) {
			/* the control: the first call sequence's instruction constant (movw r2) 2 bytes off -- the mock must notice */
			for (int pi = 0; pi < SJ_W_MAP && sj_w_plan [pi].ip; pi++)
				if (sj_w_plan [pi].disp == SJD_CALL) {
					guint32 v = (guint32) (gsize) sj_w_plan [pi].ip + 2;
					sj_put_imm16 (buf + sj_w_plan [pi].off + 4, v & 0xffff);
					cinjected++;
					break;
				}
		}
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
			guint32 rounds = fill_frame (fsize, hmode);
			/* both run on the SAME frame memory (and, in heap mode, the same heap), one after the other (a pointer into the frame --
			 * LDLOCA_S -- or the heap, and anything computed from it, is then the same in both): the trace first, its result saved, the
			 * frame and the heap restored, then the interpreter */
			memcpy (fx, f0, fsize);
			if (hmode) memcpy (heap, heap0, HEAP_SIZE);
			ptrdiff_t rt = sj_code_fn (&t) (&fr, fx, &t, &ctx);
			memcpy (fb, fx, fsize);
			if (hmode) { memcpy (heapb, heap, HEAP_SIZE); memcpy (heap, heap0, HEAP_SIZE); }
			memcpy (fx, f0, fsize);
			int wstop = 0;
			const guint16 *wip = k9 ? sj_walk9 (&fr, fx, &ctx, &t, code, &wstop) : sj_walk (&fr, fx, code, code, code + t.full_slots);
			ptrdiff_t rw = (wip - code) * 2;
			if (k9) {
				/* the odd bit: set exactly when the trace left at one of its trap sites, or a call asked for a resume state */
				gboolean is_trap = FALSE;
				for (int ti = 1; t.x->traps && ti <= t.x->traps [0].slot; ti++)
					if (t.x->traps [ti].slot == (guint16) (wip - code))
						is_trap = TRUE;
				gboolean want_odd = wstop || is_trap;
				if (((rt & 1) != 0) != want_odd)
					odd_wrong++;
				if (rt & 1)
					odd_runs++;
				rt &= ~(ptrdiff_t) 1;
			}
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
			if (rw != rt || diff >= 0 || hdiff >= 0 || (k9 && odd_wrong && !shown)) {
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
	if (pmode) {
		/* PASS: every run agreed, no odd-bit error, no bad call argument, no framed trace without its veneer -- and the paths under test ran:
		 * failed windows (incl. one that held the veneer), the veneer staged again, slow exits, recompiles published and checked */
		mism += p_bad + pub_odd_wrong + p_requeued_bad + mock_bad_args + p_bl_count_bad + p_bl_lost + p_keptfull_bad + !p_queue_full_ok;
		if (!pinject) {
			mism += p_veneer_missing;
			/* (review 2: also a recompile whose window failed -- its old code kept and checked -- and the decoded BLs) */
			if (!sj_st.windows_failed || !sj_st.veneer_restaged || !p_slow || !p_recomp_pub || !p_new_runs || !p_old_runs || !p_checked ||
			    !p_recomp_keptfail || !p_bls_checked || !p_keptfull)
				mism++;   /* (a path under test never ran: that is a failure too) */
		} else
			mism = p_veneer_missing ? 0 : 1;   /* (the control passes when the lost veneer IS caught) */
		sj_out ("SJTEST %s: %d programs (%s), seed %u; records %llu (%llu sj_record runs, %llu not queued), traces published and checked %llu "
			"(%llu framed: veneer checked; %llu short), %llu runs against the walker, %llu disagreed, %llu odd-bit errors, %llu bad call arguments; "
			"windows %llu (%u failed on purpose -> %llu traces off, %llu recompiles off), veneer island staged again %u times, %llu framed traces without "
			"their veneer%s; slow exits %llu (trap sites recorded %llu), recompiles asked %llu / published %llu (kept old %llu), old code checked "
			"before its window %llu runs, new code after it %llu runs, requeued while queued %llu; MINT_SWITCH %llu; block wraps %llu\n",
			mism ? "FAIL" : "PASS", programs, mode, (unsigned) seed, (unsigned long long) p_recorded, (unsigned long long) p_rec_runs,
			(unsigned long long) p_notqueued, (unsigned long long) p_checked, (unsigned long long) p_framed_checked, (unsigned long long) p_short,
			(unsigned long long) p_runs, (unsigned long long) p_bad, (unsigned long long) pub_odd_wrong, (unsigned long long) mock_bad_args,
			(unsigned long long) p_windows, sj_st.windows_failed, (unsigned long long) p_failed_progs, (unsigned long long) p_recomp_failed,
			sj_st.veneer_restaged, (unsigned long long) p_veneer_missing,
			pinject ? (p_veneer_missing ? " -- INJECTED FAULT DETECTED, as it must be" : " -- INJECTED FAULT NOT DETECTED: the veneer check is broken") : "",
			(unsigned long long) p_slow, (unsigned long long) sj_st.trap_recs, (unsigned long long) p_recomp_req, (unsigned long long) p_recomp_pub,
			(unsigned long long) p_recomp_kept, (unsigned long long) p_old_runs, (unsigned long long) p_new_runs, (unsigned long long) p_requeued_bad,
			(unsigned long long) p_switches, (unsigned long long) p_wraps);
		sj_out ("SJTEST publish: calls compiled %u in %u framed traces, getter intrinsics %u, trap sites %u, mock calls %llu (resume states %llu); control undo %llu\n",
			sj_st.calls, sj_st.traces_framed, sj_st.intrins, sj_st.traps, (unsigned long long) mock_calls, (unsigned long long) mock_stops, (unsigned long long) p_undone);
		sj_out ("SJTEST publish (review 2): call BLs decoded and their island checked %llu (BL lists not matching the plan %llu, lost %llu); recompiles whose "
			"window failed, old code kept and checked %llu; recompiles with the block full, old code kept and checked %llu (wrong %llu); an entry point "
			"hot with the block full refused before its record: %s; the runtime kept old code: block full %u, window failed %u, off while queued %u\n",
			(unsigned long long) p_bls_checked, (unsigned long long) p_bl_count_bad, (unsigned long long) p_bl_lost, (unsigned long long) p_recomp_keptfail,
			(unsigned long long) p_keptfull, (unsigned long long) p_keptfull_bad, p_queue_full_ok ? "yes" : "NO",
			sj_st.keep_full, sj_st.keep_window, sj_st.keep_off);
		return mism ? 1 : 0;
	}
	if (k9) {
		mism += odd_wrong;
		if (cinject ? !mock_bad_args : mock_bad_args != 0)
			mism++;
	}
	if (cinject)
		mism = mism ? 0 : 1;   /* (the control passes when the injected fault IS seen) */
	sj_out ("SJTEST %s: %d programs, %llu runs (%llu ran the loop more than once), %llu mismatches; exits: %llu at the end, %llu branched out, %llu inside (a check / a safepoint); "
		"%d opcodes in the random set of %d compiled ones; %llu opcodes compiled, %llu bytes, %llu ForVM windows; seed %u\n",
		mism ? "FAIL" : "PASS", programs, (unsigned long long) runs, (unsigned long long) loops, (unsigned long long) mism, (unsigned long long) ends,
		(unsigned long long) outs, (unsigned long long) bails, npure + nheap, SJ_STENCIL_COUNT, (unsigned long long) ops_total, (unsigned long long) bytes_total,
		(unsigned long long) windows, (unsigned) seed);
	if (k9)
		sj_out ("SJTEST kit #9 (%s): random records (p = 7/8 per instruction); %llu trap sites compiled, %llu runs left with an odd result, %llu odd-bit errors; "
			"calls compiled %llu, getter intrinsics %llu; mock calls %llu (bad arguments %llu, resume states %llu)%s\n",
			mode, (unsigned long long) trap_sites, (unsigned long long) odd_runs, (unsigned long long) odd_wrong, (unsigned long long) calls_compiled,
			(unsigned long long) intrins_compiled, (unsigned long long) mock_calls, (unsigned long long) mock_bad_args, (unsigned long long) mock_stops,
			cinject ? (mock_bad_args ? " -- INJECTED FAULT DETECTED, as it must be" : " -- INJECTED FAULT NOT DETECTED: the call check is broken") : "");
	if (cinject)
		sj_out ("SJTEST INJECT (control): %llu traces with a call's instruction constant patched 2 bytes off\n", (unsigned long long) cinjected);
	if (inject)
		sj_out ("SJTEST INJECT (control): %llu traces with a store patched 4 bytes off -> %llu mismatches (%s)\n", (unsigned long long) injected,
			(unsigned long long) mism, mism ? "detected, as it must be" : "NOT DETECTED: the heap comparison is broken");
	if (hmode)
		sj_out ("SJTEST heap mode: %d heap opcodes (loads / stores through a pointer) + %d frame-only ones; %llu heap opcodes compiled into the traces; heap compared byte for byte (%d bytes) after every run; left out:%s\n",
			nheap, npure, (unsigned long long) heap_ops_total, HEAP_SIZE, left_out [0] ? left_out : " none");
	return mism ? 1 : 0;
}
